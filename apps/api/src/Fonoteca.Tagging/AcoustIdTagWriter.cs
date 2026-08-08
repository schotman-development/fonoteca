using System.Globalization;
using System.Text.Json;
using ATL;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Events;

namespace Fonoteca.Tagging;

/// <summary>
/// Writes an AcoustID into an audio file, or refuses to.
/// </summary>
/// <remarks>
/// The only code in this application permitted to modify a file in the user's
/// library, and the reason ADR 0002 exists: every other operation is recoverable
/// by rescanning, and a botched write is not.
///
/// The sequence, and what each step is actually for:
///
/// <list type="number">
/// <item>Read with <b>both</b> libraries. That reading is the undo payload and
/// the baseline every later comparison is made against.</item>
/// <item>Diff. If the file already says what we were going to write, stop —
/// re-writing 30 MB to change nothing is not free, and a second undo entry for a
/// write that did not happen is worse than not free.</item>
/// <item>If mutation is disabled, stop <b>here</b>, before anything is opened
/// for writing. The plan is still returned, so a disabled run is a complete dry
/// run rather than a no-op.</item>
/// <item>Write through <c>IStagedWrite</c>: ATL renders the whole file into a
/// temporary sibling. The original is not touched.</item>
/// <item>Read the staged file back with <b>ATL</b>: is the field what we
/// intended, and did anything else change?</item>
/// <item>Read it back with <b>TagLib#</b>: does a second, independently written
/// parser agree — and specifically, is the value in the frame the format
/// actually specifies?</item>
/// <item>Sanity-check the length. Not in ADR 0002, and it should be: it is the
/// one failure the two-reader check structurally cannot catch, because both
/// libraries will cheerfully agree about a file with perfect tags and no
/// audio.</item>
/// <item>Append the undo entry, <b>then</b> commit. In that order: a crash
/// between them leaves a journal entry for a write that never happened, and
/// undoing that is a no-op the diff reports as <c>IsNoOp</c>. The reverse leaves
/// a changed file with no journal entry at all, which is unrecoverable. An
/// over-recorded undo is idempotent; an unrecorded write is not.</item>
/// </list>
///
/// A failure at 5, 6 or 7 disposes the staged write without committing, so the
/// original is byte-for-byte what it was.
/// </remarks>
public sealed class AcoustIdTagWriter(
    IAudioFileStore files,
    TagReader reader,
    IEventLog events,
    IClock clock,
    TagWriterOptions options)
{
    /// <summary>Event type for a committed write. Dotted, matching <see cref="DomainEvent.Type"/>.</summary>
    public const string WrittenEventType = "tagging.acoustid.written";

    /// <summary>Event type for a write that was planned and refused.</summary>
    public const string RefusedEventType = "tagging.acoustid.refused";

    /// <summary>Event type for a write abandoned after verification failed.</summary>
    public const string AbortedEventType = "tagging.acoustid.aborted";

    /// <summary>The subject vocabulary the journal uses for a file.</summary>
    public const string FileSubject = "file";

    /// <summary>
    /// How far the staged file may differ in size from the original before the
    /// write is refused: the larger of a mebibyte and one percent.
    /// </summary>
    /// <remarks>
    /// Generous, because it only has to catch catastrophe. Measured on this
    /// corpus, adding a 36-character comment to a FLAC changed the size by
    /// <i>zero</i> bytes — the existing padding block absorbed it. A staged file
    /// that is 90% smaller than its original has lost the audio, which is
    /// exactly the outcome two agreeing tag readers would call a success.
    /// </remarks>
    public static long ToleratedSizeDrift(long originalBytes) =>
        Math.Max(1024L * 1024L, originalBytes / 100);

    private readonly IAudioFileStore _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly TagReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly IEventLog _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TagWriterOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Whether this writer would write anything at all.</summary>
    public bool MutationAllowed => _options.AllowFileMutation;

    /// <summary>
    /// What writing <paramref name="acoustId"/> into this file would change.
    /// </summary>
    /// <remarks>Opens the file for reading only. Safe to call with mutation disabled.</remarks>
    public async Task<TagWritePlan?> PlanAsync(
        LibraryPath path,
        Guid acoustId,
        CancellationToken cancellationToken = default)
    {
        var field = AcoustIdTagField.For(path);
        if (field is null) return null;

        var before = await _reader.ReadAsync(path, null, cancellationToken).ConfigureAwait(false);
        var desired = acoustId.ToString("D", CultureInfo.InvariantCulture);

        var changes = string.Equals(before.AcoustId, desired, StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<TagFieldChange>()
            : [new TagFieldChange(field, before.AcoustId, desired)];

        return new TagWritePlan(path, changes, before);
    }

    /// <summary>Carries out a plan, or explains why it did not.</summary>
    /// <param name="correlationId">
    /// Shared by every file in one pass, so ADR 0002's "a whole batch is
    /// reversible as a unit" is answerable by a single indexed query.
    /// </param>
    /// <param name="subjectId">
    /// The catalogue id of the file, not its path. Paths move — a rename would
    /// orphan a journal keyed by one — and <c>DomainEvent.SubjectId</c> is a
    /// <c>varchar(200)</c> while a library path is up to 4096. The path is still
    /// recorded, in the payload, where it is descriptive rather than a key.
    /// </param>
    public async Task<TagWriteResult> ApplyAsync(
        TagWritePlan? plan,
        string subjectId,
        string correlationId,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            return new TagWriteResult(null, TagWriteStatus.Unsupported,
                "This container has no field an AcoustID can be written to.");
        }

        if (plan.IsNoOp)
        {
            return new TagWriteResult(plan, TagWriteStatus.NothingToDo);
        }

        var change = plan.Changes[0];

        if (!_options.AllowFileMutation)
        {
            // The dry run's whole value: everything expensive has already been
            // computed and stored by the caller, and this is the only step
            // skipped. Journalled so a disabled pass leaves evidence of what it
            // would have done.
            await JournalAsync(
                plan, change, RefusedEventType, subjectId, correlationId, actorId, null, cancellationToken)
                .ConfigureAwait(false);

            return new TagWriteResult(plan, TagWriteStatus.Refused,
                "Fonoteca:AllowFileMutation is false, so nothing was written.");
        }

        var facts = await _files.StatAsync(plan.Path, cancellationToken).ConfigureAwait(false);

        if (facts is null)
        {
            return new TagWriteResult(plan, TagWriteStatus.Failed, "The file is no longer there.");
        }

        var staged = await _files.OpenForReplaceAsync(plan.Path, cancellationToken).ConfigureAwait(false);

        await using (staged.ConfigureAwait(false))
        {
            var rendered = await RenderAsync(plan, change, staged, cancellationToken).ConfigureAwait(false);

            if (rendered is not null)
            {
                await JournalAsync(
                    plan, change, AbortedEventType, subjectId, correlationId, actorId, rendered,
                    cancellationToken).ConfigureAwait(false);

                return new TagWriteResult(plan, TagWriteStatus.VerificationFailed, rendered);
            }

            var problem = await VerifyAsync(plan, change, staged, facts, cancellationToken).ConfigureAwait(false);

            if (problem is not null)
            {
                await JournalAsync(
                    plan, change, AbortedEventType, subjectId, correlationId, actorId, problem,
                    cancellationToken).ConfigureAwait(false);

                // Falling out of the await using without committing deletes the
                // staged file and leaves the original exactly as it was.
                return new TagWriteResult(plan, TagWriteStatus.VerificationFailed, problem);
            }

            var undoId = await JournalAsync(
                plan, change, WrittenEventType, subjectId, correlationId, actorId, null, cancellationToken)
                .ConfigureAwait(false);

            await staged.CommitAsync(cancellationToken).ConfigureAwait(false);

            var after = await _files.StatAsync(plan.Path, cancellationToken).ConfigureAwait(false);

            return new TagWriteResult(plan, TagWriteStatus.Written, null, undoId, after);
        }
    }

    /// <summary>Renders the tagged file into the staging stream. Null on success.</summary>
    private async Task<string?> RenderAsync(
        TagWritePlan plan,
        TagFieldChange change,
        IStagedWrite staged,
        CancellationToken cancellationToken)
    {
        var source = await _files.OpenReadAsync(plan.Path, cancellationToken).ConfigureAwait(false);

        await using (source.ConfigureAwait(false))
        {
            var track = new Track(source, Path.GetExtension(plan.Path.Value));
            track.AdditionalFields[change.Field] = change.To!;

            // ATL renders the complete file — audio and all — to offset 0 of the
            // target. That is precisely the temp-sibling semantics ADR 0002 asks
            // for, so no pre-copy of the original is needed.
            var saved = await track.SaveToAsync(staged.Content).ConfigureAwait(false);

            if (!saved)
            {
                // ATL reports failure as a bare false and puts the reason only in
                // its own static log, so there is nothing more specific to say
                // here without making this class depend on that global.
                return "ATL declined to write the file.";
            }
        }

        await staged.Content.FlushAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>The three checks. Returns the first failure, or null when all pass.</summary>
    private async Task<string?> VerifyAsync(
        TagWritePlan plan,
        TagFieldChange change,
        IStagedWrite staged,
        FileFacts original,
        CancellationToken cancellationToken)
    {
        var stagedPath = staged.StagingPath;

        // Read as the container we just wrote, not as ".tmp" — see TagReader.
        var written = await _reader.ReadAsync(stagedPath, plan.Path, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(written.AcoustId, change.To, StringComparison.OrdinalIgnoreCase))
        {
            return $"ATL read the file back with an AcoustID of '{written.AcoustId ?? "none"}' "
                + $"rather than '{change.To}'.";
        }

        var lost = plan.Before.FieldsLostIn(written);

        if (lost.Count > 0)
        {
            return $"Writing the AcoustID would have changed or dropped: {string.Join(", ", lost)}.";
        }

        var verified = await _reader
            .ReadWithVerifierAsync(stagedPath, plan.Path, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(verified.AcoustId, change.To, StringComparison.OrdinalIgnoreCase))
        {
            // The failure this whole design exists to catch: ATL is satisfied and
            // an independent parser cannot find the value where the format says
            // it should be. A tag no other tool can read is not a tag.
            return $"TagLib# does not agree the AcoustID was written: it reads "
                + $"'{verified.AcoustId ?? "none"}'.";
        }

        if (!written.AgreesWith(verified))
        {
            return "The two tag libraries disagree about the file they just read.";
        }

        var stagedFacts = await _files.StatAsync(stagedPath, cancellationToken).ConfigureAwait(false);

        if (stagedFacts is null)
        {
            return "The staged file disappeared before it could be verified.";
        }

        var drift = Math.Abs(stagedFacts.SizeBytes - original.SizeBytes);

        if (drift > ToleratedSizeDrift(original.SizeBytes))
        {
            return $"The staged file is {stagedFacts.SizeBytes} bytes against the original's "
                + $"{original.SizeBytes}; that is too large a change for one tag.";
        }

        return null;
    }

    private async Task<Guid> JournalAsync(
        TagWritePlan plan,
        TagFieldChange change,
        string type,
        string subjectId,
        string correlationId,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new AcoustIdWritePayload
            {
                Path = plan.Path.Value,
                Field = change.Field,
                Previous = change.From,
                Written = change.To,
                Reason = reason,
                Pictures = plan.Before.PictureCount,
                PictureDigests = plan.Before.PictureDigests,
                Writer = "ATL.NET",
                Verifier = "TagLib#",
            },
            TaggingJson.Default.AcoustIdWritePayload);

        var entry = DomainEvent.Create(
            type,
            FileSubject,
            subjectId,
            actorId,
            _clock.UtcNow,
            payload,
            correlationId);

        await _events.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        return entry.Id;
    }
}
