using System.Globalization;
using System.Text.Json;
using Track = ATL.Track;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;

namespace Fonoteca.Tagging;

/// <summary>
/// Writes tags into an audio file, or refuses to.
/// </summary>
/// <remarks>
/// <b>The only code in this application permitted to modify a file in the user's
/// library</b>, and the reason ADR 0002 exists: every other operation is
/// recoverable by rescanning, and a botched write is not.
///
/// It was <c>AcoustIdTagWriter</c> first, writing one field. That class is now a
/// wrapper over this one and the sequence lives here exactly once — the
/// alternative was a second copy of the most dangerous hundred lines in the
/// repository, which is the kind of duplication that gets somebody paged.
///
/// The sequence, and what each step is actually for:
///
/// <list type="number">
/// <item>Read with <b>both</b> libraries. That reading is the undo payload and
/// the baseline every later comparison is made against.</item>
/// <item>Diff. If the file already says what we were going to write, stop —
/// re-writing 30 MB to change nothing is not free, and a second undo entry for a
/// write that did not happen is worse than not free. This is also what makes
/// running the pass twice cost a read per file rather than a rewrite.</item>
/// <item>If mutation is disabled, stop <b>here</b>, before anything is opened
/// for writing. The plan is still returned, so a disabled run is a complete dry
/// run rather than a no-op.</item>
/// <item>Write through <c>IStagedWrite</c>: ATL renders the whole file into a
/// temporary sibling. The original is not touched.</item>
/// <item>Read the staged file back with <b>ATL</b>: is every field what we
/// intended, and did anything we did <i>not</i> intend change?</item>
/// <item>Read it back with <b>TagLib#</b>: does a second, independently written
/// parser agree — and, where an AcoustID was written, is the value in the frame
/// the format actually specifies?</item>
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
public sealed class TagWriter(
    IAudioFileStore files,
    TagReader reader,
    IEventLog events,
    IClock clock,
    TagWriterOptions options)
{
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
    /// What writing <paramref name="desired"/> into this file would change.
    /// </summary>
    /// <param name="desired">
    /// Canonical field names — <see cref="CatalogueTags"/>'s constants — to the
    /// values they should hold. A fact the catalogue does not know is simply
    /// absent from the map; there is no way to express "erase this", and that is
    /// deliberate. This runs over a library at a time, and a deletion dressed as
    /// an edit is the one mistake nothing downstream could distinguish from a
    /// ripper that never wrote the field.
    /// </param>
    /// <remarks>
    /// Opens the file for reading only. Safe to call with mutation disabled.
    ///
    /// The changes come out ordered by field name, so the plan, the staged write
    /// and the journal all describe one write in the same sequence — over a
    /// dictionary's own order, two identical writes produce two different
    /// payloads and nothing downstream can compare them.
    /// </remarks>
    public async Task<TagWritePlan?> PlanAsync(
        LibraryPath path,
        IReadOnlyDictionary<string, string> desired,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desired);

        // The same test the AcoustID path makes: a container with nowhere to put
        // a custom field is reported as unsupported rather than failed.
        if (AcoustIdTagField.For(path) is null) return null;

        var before = await _reader.ReadAsync(path, null, cancellationToken).ConfigureAwait(false);

        var changes = new List<TagFieldChange>();

        foreach (var canonical in desired.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            var field = CatalogueTagFields.Spell(path, canonical);

            if (field is null) continue;

            var current = before.Find(field);
            var value = desired[canonical];

            if (!string.Equals(current, value, StringComparison.Ordinal))
            {
                changes.Add(new TagFieldChange(field, current, value));
            }
        }

        return new TagWritePlan(path, changes, before);
    }

    /// <summary>Carries out a plan, or explains why it did not.</summary>
    /// <param name="subjectId">
    /// The catalogue id of the file, not its path. Paths move — a rename would
    /// orphan a journal keyed by one — and <c>DomainEvent.SubjectId</c> is a
    /// <c>varchar(200)</c> while a library path is up to 4096. The path is still
    /// recorded, in the payload, where it is descriptive rather than a key.
    /// </param>
    /// <param name="correlationId">
    /// Shared by every file in one pass, so ADR 0002's "a whole batch is
    /// reversible as a unit" is answerable by a single indexed query.
    /// </param>
    /// <param name="eventPrefix">
    /// What the journal calls this kind of write — <c>tagging.acoustid</c> or
    /// <c>tagging.catalogue</c>. Two names rather than one because undoing an
    /// identification's tag and undoing a library-wide catalogue write are
    /// different acts, and a single event type would make the query that finds
    /// one find both.
    /// </param>
    public async Task<TagWriteResult> ApplyAsync(
        TagWritePlan? plan,
        string subjectId,
        string correlationId,
        string actorId,
        string eventPrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPrefix);

        if (plan is null)
        {
            return new TagWriteResult(null, TagWriteStatus.Unsupported,
                "This container has no field these tags can be written to.");
        }

        if (plan.IsNoOp)
        {
            return new TagWriteResult(plan, TagWriteStatus.NothingToDo);
        }

        if (!_options.AllowFileMutation)
        {
            // The dry run's whole value: everything expensive has already been
            // computed and stored by the caller, and this is the only step
            // skipped. Journalled so a disabled pass leaves evidence of what it
            // would have done.
            await JournalAsync(
                plan, $"{eventPrefix}.refused", subjectId, correlationId, actorId, null, cancellationToken)
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
            var rendered = await RenderAsync(plan, staged, cancellationToken).ConfigureAwait(false);

            if (rendered is not null)
            {
                await JournalAsync(
                    plan, $"{eventPrefix}.aborted", subjectId, correlationId, actorId, rendered,
                    cancellationToken).ConfigureAwait(false);

                return new TagWriteResult(plan, TagWriteStatus.VerificationFailed, rendered);
            }

            var problem = await VerifyAsync(plan, staged, facts, cancellationToken).ConfigureAwait(false);

            if (problem is not null)
            {
                await JournalAsync(
                    plan, $"{eventPrefix}.aborted", subjectId, correlationId, actorId, problem,
                    cancellationToken).ConfigureAwait(false);

                // Falling out of the await using without committing deletes the
                // staged file and leaves the original exactly as it was.
                return new TagWriteResult(plan, TagWriteStatus.VerificationFailed, problem);
            }

            var undoId = await JournalAsync(
                plan, $"{eventPrefix}.written", subjectId, correlationId, actorId, null, cancellationToken)
                .ConfigureAwait(false);

            await staged.CommitAsync(cancellationToken).ConfigureAwait(false);

            var after = await _files.StatAsync(plan.Path, cancellationToken).ConfigureAwait(false);

            return new TagWriteResult(plan, TagWriteStatus.Written, null, undoId, after);
        }
    }

    /// <summary>Renders the tagged file into the staging stream. Null on success.</summary>
    private async Task<string?> RenderAsync(
        TagWritePlan plan,
        IStagedWrite staged,
        CancellationToken cancellationToken)
    {
        var source = await _files.OpenReadAsync(plan.Path, cancellationToken).ConfigureAwait(false);

        await using (source.ConfigureAwait(false))
        {
            var track = new Track(source, Path.GetExtension(plan.Path.Value));

            foreach (var change in plan.Changes)
            {
                Set(track, change);
            }

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

    /// <summary>
    /// One field onto the ATL track, through the property that owns it.
    /// </summary>
    /// <remarks>
    /// <b>The typed properties are not a convenience.</b> A title put into
    /// <c>AdditionalFields["TITLE"]</c> is a custom field beside the real one on
    /// every container that has a real one, so the file grows a second title
    /// nothing reads. ATL knows that <c>TRACKNUMBER</c> is a Vorbis comment, a
    /// <c>TRCK</c> frame and a <c>trkn</c> atom; nothing here has to.
    /// </remarks>
    private static void Set(Track track, TagFieldChange change)
    {
        var value = change.To;

        switch (change.Field)
        {
            case CatalogueTags.Title: track.Title = value; break;
            case CatalogueTags.Artist: track.Artist = value; break;
            case CatalogueTags.Album: track.Album = value; break;
            case CatalogueTags.AlbumArtist: track.AlbumArtist = value; break;
            case CatalogueTags.TrackNumber: track.TrackNumber = Count(value); break;
            case CatalogueTags.TrackTotal: track.TrackTotal = Count(value); break;
            case CatalogueTags.DiscNumber: track.DiscNumber = Count(value); break;
            case CatalogueTags.DiscTotal: track.DiscTotal = Count(value); break;

            // The year, never a date. ATL's Date is a DateTime and cannot hold
            // "1969" without inventing the first of January — see
            // CatalogueTagSource.Year.
            case CatalogueTags.Year: track.Year = Count(value); break;

            default: track.AdditionalFields[change.Field] = value!; break;
        }
    }

    private static int? Count(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>The checks. Returns the first failure, or null when all pass.</summary>
    private async Task<string?> VerifyAsync(
        TagWritePlan plan,
        IStagedWrite staged,
        FileFacts original,
        CancellationToken cancellationToken)
    {
        var stagedPath = staged.StagingPath;

        // Read as the container we just wrote, not as ".tmp" — see TagReader.
        var written = await _reader.ReadAsync(stagedPath, plan.Path, cancellationToken).ConfigureAwait(false);

        foreach (var change in plan.Changes)
        {
            var now = written.Find(change.Field);

            if (!string.Equals(now, change.To, StringComparison.Ordinal))
            {
                return $"ATL read {change.Field} back as '{now ?? "none"}' rather than '{change.To}'.";
            }
        }

        var lost = Lost(plan, written);

        if (lost.Count > 0)
        {
            return $"Writing these tags would have changed or dropped: {string.Join(", ", lost)}.";
        }

        var verified = await _reader
            .ReadWithVerifierAsync(stagedPath, plan.Path, cancellationToken)
            .ConfigureAwait(false);

        // The AcoustID gets the extra check, through TagLib#'s native accessor
        // rather than a generic map: it must confirm the value landed in the
        // frame the format specifies, not merely somewhere in the file under a
        // name we chose. A Vorbis comment called "ACOUSTID ID" satisfies a
        // generic lookup and satisfies nobody's tag reader.
        var acoustId = plan.Changes
            .FirstOrDefault(change => change.Field == AcoustIdTagField.For(plan.Path));

        if (acoustId is not null
            && !string.Equals(verified.AcoustId, acoustId.To, StringComparison.OrdinalIgnoreCase))
        {
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
                + $"{original.SizeBytes}; that is too large a change for a tag write.";
        }

        return null;
    }

    /// <summary>
    /// Fields that changed and were not meant to.
    /// </summary>
    /// <remarks>
    /// <see cref="TagSnapshot.FieldsLostIn"/> reports everything that moved, and
    /// on a multi-field write most of what moved is the point. So the intended
    /// fields come out — and with them <c>DATE</c>, because ATL derives it from
    /// the same value as <c>YEAR</c> and reports both. Without that pairing every
    /// write that corrects a year fails verification and rolls itself back.
    /// </remarks>
    private static IReadOnlyList<string> Lost(TagWritePlan plan, TagSnapshot written)
    {
        var intended = new HashSet<string>(
            plan.Changes.Select(change => change.Field), StringComparer.OrdinalIgnoreCase);

        if (intended.Contains(CatalogueTags.Year)) intended.Add("DATE");

        return [.. plan.Before.FieldsLostIn(written).Where(field => !intended.Contains(field))];
    }

    private async Task<Guid> JournalAsync(
        TagWritePlan plan,
        string type,
        string subjectId,
        string correlationId,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new TagWritePayload
            {
                Path = plan.Path.Value,
                Changes =
                [
                    .. plan.Changes.Select(change =>
                        new TagFieldWrite
                        {
                            Field = change.Field,
                            Previous = change.From,
                            Written = change.To,
                        }),
                ],
                Reason = reason,
                Pictures = plan.Before.PictureCount,
                PictureDigests = plan.Before.PictureDigests,
                Writer = "ATL.NET",
                Verifier = "TagLib#",
            },
            TaggingJson.Default.TagWritePayload);

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
