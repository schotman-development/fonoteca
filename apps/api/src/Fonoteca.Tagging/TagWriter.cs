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

        // A UFID an earlier save left with an encoding byte in front of its
        // owner — see TaggingDefaults.UfidIsBinary — is put right as a change of
        // its own, so the journal can undo it and a file whose tags are already
        // correct is still opened for it.
        if (TaggingDefaults.UfidIsBinary
            && before.Fields.TryGetValue(UfidField, out var ufid)
            && ufid.StartsWith('\u0003'))
        {
            changes.Add(new TagFieldChange(UfidField, ufid, ufid[1..]));
        }

        return new TagWritePlan(path, changes, before);
    }

    /// <summary>
    /// What putting fields back to the values a journal recorded would change.
    /// </summary>
    /// <param name="fields">
    /// Field names as the journal spells them — already the container's, since
    /// they came out of a plan for this file — to the value each should hold.
    /// Null removes the field: unlike <see cref="PlanAsync"/>, this is how an
    /// undo says "the write added this", and only an undo calls it.
    /// </param>
    public async Task<TagWritePlan?> PlanRestoreAsync(
        LibraryPath path,
        IReadOnlyList<KeyValuePair<string, string?>> fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (AcoustIdTagField.For(path) is null) return null;

        var before = await _reader.ReadAsync(path, null, cancellationToken).ConfigureAwait(false);

        TagFieldChange[] changes =
        [
            .. fields
                .OrderBy(field => field.Key, StringComparer.Ordinal)
                .Select(field => new TagFieldChange(field.Key, before.Find(field.Key), field.Value))
                .Where(change => !string.Equals(change.From, change.To, StringComparison.Ordinal)),
        ];

        return new TagWritePlan(path, changes, before);
    }

    /// <summary>ATL's name for the ID3v2 frame MusicBrainz's recording id lives in.</summary>
    private const string UfidField = "UFID";

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

        var stored = await _reader.ReadWithVerifierAsync(plan.Path, null, cancellationToken).ConfigureAwait(false);

        // An ID3 tag in front of a FLAC is dropped by the write — see Id3Prefix —
        // and with it whatever only it held, so the checks on ID3-only facts
        // stand down for this file.
        var source = await _files.OpenReadAsync(plan.Path, cancellationToken).ConfigureAwait(false);
        var mp3 = plan.Path.Value.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        var flac = plan.Path.Value.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
        bool stripped;
        Dictionary<string, byte[]>? kept = [];
        List<byte[]> commentPictures = [];

        await using (source.ConfigureAwait(false))
        {
            stripped = Id3Prefix.Length(source, plan.Path.Value) > 0;

            // The frames ATL rewrites as text, as they were — see Id3v2Repair.
            if (mp3) kept = Id3v2Repair.Kept(source);

            // The covers ATL drops from a Vorbis comment — see FlacCommentPictures.
            if (flac) commentPictures = FlacCommentPictures.Read(Id3Prefix.Past(source, plan.Path.Value));
        }

        var staged = await _files.OpenForReplaceAsync(plan.Path, cancellationToken).ConfigureAwait(false);

        await using (staged.ConfigureAwait(false))
        {
            var rendered = await RenderAsync(plan, stored, staged, cancellationToken).ConfigureAwait(false);

            if (rendered is not null)
            {
                await JournalAsync(
                    plan, $"{eventPrefix}.aborted", subjectId, correlationId, actorId, rendered,
                    cancellationToken, stored).ConfigureAwait(false);

                return new TagWriteResult(plan, TagWriteStatus.VerificationFailed, rendered);
            }

            // Before the checks, so both readers verify the file as it will be.
            if (flac) FlacCommentPictures.Restore(staged.Content, commentPictures);

            var problem = !mp3 ? null
                : kept is null ? "ATL damages the RGAD frame, and the repair cannot read this tag to put it back."
                : Id3v2Repair.Repair(staged.Content, kept);

            // The base64 of the covers moved out of the comment, which the file sheds.
            var shed = commentPictures.Sum(picture => 4L * ((picture.Length + 2) / 3));

            problem ??= await VerifyAsync(plan, stored, stripped, shed, staged, facts, cancellationToken)
                .ConfigureAwait(false);

            if (problem is not null)
            {
                await JournalAsync(
                    plan, $"{eventPrefix}.aborted", subjectId, correlationId, actorId, problem,
                    cancellationToken, stored).ConfigureAwait(false);

                // Falling out of the await using without committing deletes the
                // staged file and leaves the original exactly as it was.
                return new TagWriteResult(plan, TagWriteStatus.VerificationFailed, problem);
            }

            var undoId = await JournalAsync(
                plan, $"{eventPrefix}.written", subjectId, correlationId, actorId, null, cancellationToken, stored)
                .ConfigureAwait(false);

            await staged.CommitAsync(cancellationToken).ConfigureAwait(false);

            var after = await _files.StatAsync(plan.Path, cancellationToken).ConfigureAwait(false);

            return new TagWriteResult(plan, TagWriteStatus.Written, null, undoId, after);
        }
    }

    /// <summary>Renders the tagged file into the staging stream. Null on success.</summary>
    private async Task<string?> RenderAsync(
        TagWritePlan plan,
        TagSnapshot stored,
        IStagedWrite staged,
        CancellationToken cancellationToken)
    {
        var source = await _files.OpenReadAsync(plan.Path, cancellationToken).ConfigureAwait(false);

        await using (source.ConfigureAwait(false))
        {
            var track = new Track(Id3Prefix.Past(source, plan.Path.Value), Path.GetExtension(plan.Path.Value));

            // ATL 7.16 judges an ID3v2 date's precision wrongly on load: a 2.4
            // "2017" becomes "2017-01-01" on save, a 2.3 TYER+TDAT loses its day.
            // Hand it back the precision the file stores, as TagLib# read it.
            Restate(stored.RecordedDate, track.Date, year => track.Year = year, date => track.Date = date);
            Restate(stored.OriginalDate, track.OriginalReleaseDate,
                year => track.OriginalReleaseYear = year, date => track.OriginalReleaseDate = date);

            // And the totals it read as none — see TagSnapshot.TrackTotal — or
            // they are dropped by a write that never meant to touch them.
            if (track.TrackTotal is null or 0 && stored.TrackTotal is { } tracks) track.TrackTotal = tracks;
            if (track.DiscTotal is null or 0 && stored.DiscTotal is { } discs) track.DiscTotal = discs;

            foreach (var change in plan.Changes)
            {
                Set(track, change);
            }

            // ATL reads a WXXX frame as its description and URL joined by its own
            // value separator, and writes that separator into the URL. The frame
            // defines a NUL between them; put it back.
            foreach (var key in track.AdditionalFields.Keys.Where(key => key is "WXXX" or "WXX").ToList())
            {
                var url = track.AdditionalFields[key];
                var at = url.IndexOf(TaggingDefaults.ValueSeparator, StringComparison.Ordinal);

                if (at >= 0) track.AdditionalFields[key] = string.Concat(url.AsSpan(0, at), "\0", url.AsSpan(at + 1));
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

            if (plan.Path.Value.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) Id3v1Trailer.Write(staged.Content, track);
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
            // ATL reads null on a typed property as "leave it", and an empty
            // value as "remove it" — the second is what a null here means.
            case CatalogueTags.Title: track.Title = value ?? string.Empty; break;
            case CatalogueTags.Artist: track.Artist = value ?? string.Empty; break;
            case CatalogueTags.Album: track.Album = value ?? string.Empty; break;
            case CatalogueTags.AlbumArtist: track.AlbumArtist = value ?? string.Empty; break;
            case CatalogueTags.TrackNumber: track.TrackNumber = value is null ? 0 : Count(value); break;
            case CatalogueTags.TrackTotal: track.TrackTotal = value is null ? 0 : Count(value); break;
            case CatalogueTags.DiscNumber: track.DiscNumber = value is null ? 0 : Count(value); break;
            case CatalogueTags.DiscTotal: track.DiscTotal = value is null ? 0 : Count(value); break;

            // The year, never a date. ATL's Date is a DateTime and cannot hold
            // "1969" without inventing the first of January — see
            // CatalogueTagSource.Year.
            case CatalogueTags.Year: track.Year = value is null ? 0 : Count(value); break;

            // Gone, under every spelling of the key, as below.
            case var _ when value is null:
                foreach (var key in track.AdditionalFields.Keys
                             .Where(key => key.Equals(change.Field, StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    track.AdditionalFields.Remove(key);
                }

                break;

            default:
                // Every spelling of the key. A file carrying an APE tag beside
                // its ID3 one holds the field twice, one key uppercased, and ATL's
                // writers keep whichever comes first — the old value.
                foreach (var key in track.AdditionalFields.Keys
                             .Where(key => key.Equals(change.Field, StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    track.AdditionalFields[key] = value!;
                }

                track.AdditionalFields[change.Field] = value!;
                break;
        }
    }

    /// <summary>
    /// Puts back a stored date ATL parsed at the wrong precision; leaves any other alone.
    /// </summary>
    private static void Restate(string? stored, DateTime? parsed, Action<int> year, Action<DateTime> date)
    {
        if (stored is null || parsed is not { Month: 1, Day: 1 } read || read.TimeOfDay != TimeSpan.Zero) return;

        if (stored.Length == 4 && Count(stored) == read.Year)
        {
            year(read.Year);
        }
        else if (DateTime.TryParseExact(stored, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            && day.Year == read.Year && day != DateTime.MinValue)
        {
            date(day);
        }
    }

    /// <summary>A value with its control characters spelled out, for a message.</summary>
    private static string Visible(string? value) =>
        value is null
            ? "none"
            : string.Concat(value.Select(c => char.IsControl(c) ? $"\\x{(int)c:X2}" : c.ToString()));

    private static int? Count(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>The checks. Returns the first failure, or null when all pass.</summary>
    /// <param name="shed">Bytes the file may lose beyond a tag write's own, for covers moved out of a Vorbis comment.</param>
    private async Task<string?> VerifyAsync(
        TagWritePlan plan,
        TagSnapshot stored,
        bool stripped,
        long shed,
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

        var lost = Lost(plan, stored, written);

        if (lost.Count > 0)
        {
            return $"Writing these tags would have changed or dropped: {string.Join(", ", lost)}.";
        }

        // Each reader's pictures before against its own after, in any order.
        // The two agreeing with each other is not enough: ATL drops a cover
        // stored inside a Vorbis comment, TagLib# never saw it, and both then
        // agreed about a file with one sleeve fewer.
        if (!plan.Before.PictureDigests.Order(StringComparer.Ordinal)
                .SequenceEqual(written.PictureDigests.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return "Writing these tags would have changed or dropped an embedded picture: "
                + $"{plan.Before.PictureCount} before, {written.PictureCount} after.";
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

        // As a set: TagLib# counts a cover twice when an ID3 tag in front of a
        // FLAC carries a copy of it, and the copy going is not a cover going.
        // A cover moved out of a Vorbis comment is new to TagLib#, which never
        // read it there; ATL did, so it may be new only if ATL saw it before.
        var pictures = stored.PictureDigests.ToHashSet(StringComparer.Ordinal);

        if (!pictures.IsSubsetOf(verified.PictureDigests)
            || !verified.PictureDigests.All(digest => pictures.Contains(digest) || plan.Before.PictureDigests.Contains(digest)))
        {
            return "Writing these tags would have changed or dropped an embedded picture, as TagLib# reads them: "
                + $"{stored.PictureCount} before, {verified.PictureCount} after.";
        }

        foreach (var (field, before, after) in new (string Field, int? Before, int? After)[]
                 {
                     (CatalogueTags.TrackTotal, stored.TrackTotal, verified.TrackTotal),
                     (CatalogueTags.DiscTotal, stored.DiscTotal, verified.DiscTotal),
                 })
        {
            if (before is not null && before != after && !plan.Changes.Any(change => change.Field == field))
            {
                return $"Writing these tags would have rewritten {field} '{before}' as '{after?.ToString(CultureInfo.InvariantCulture) ?? "none"}'.";
            }
        }

        // MusicBrainz's recording id on an MP3, which ATL has damaged before —
        // see TaggingDefaults.UfidIsBinary. Only a planned repair may change it.
        var identifiers = plan.Changes.Any(change => change.Field == UfidField)
            ? stored.FileIdentifiers?.Replace("\u0003", string.Empty, StringComparison.Ordinal)
            : stored.FileIdentifiers;

        if (!stripped && !string.Equals(identifiers, verified.FileIdentifiers, StringComparison.Ordinal))
        {
            return $"Writing these tags would have rewritten the file identifiers '{Visible(stored.FileIdentifiers)}' "
                + $"as '{Visible(verified.FileIdentifiers)}'.";
        }

        // Each library against its own reading of the original, not against the
        // other: TagLib# 2.3 ignores LAME's "Info" header and times a CBR stream at
        // the first frame's bitrate, so the two can disagree about an untouched file.
        if (Math.Abs(written.DurationSeconds - plan.Before.DurationSeconds) > 1.0
            || Math.Abs(verified.DurationSeconds - stored.DurationSeconds) > 1.0)
        {
            return "The staged file is not as long as the original.";
        }

        // ATL renders a date from its own parse on every save, so a date nothing
        // asked to change can still come out different: ID3v2.3's day and month
        // dropped on the upgrade to 2.4, "2008-10" given a first of the month,
        // "12.10.2008" read the other way round. Its own reading cannot see that,
        // and it is not in the plan, so the journal could not undo it. TagLib# reads
        // the date as stored on both sides; a difference means this file is not
        // written.
        if (!plan.Changes.Any(change => change.Field == CatalogueTags.Year)
            && !string.Equals(stored.RecordedDate, verified.RecordedDate, StringComparison.Ordinal))
        {
            return $"Writing these tags would have rewritten the date '{stored.RecordedDate ?? "none"}' "
                + $"as '{verified.RecordedDate ?? "none"}'.";
        }

        if (!stripped && !string.Equals(stored.OriginalDate, verified.OriginalDate, StringComparison.Ordinal))
        {
            return $"Writing these tags would have rewritten the original date '{stored.OriginalDate ?? "none"}' "
                + $"as '{verified.OriginalDate ?? "none"}'.";
        }

        var stagedFacts = await _files.StatAsync(stagedPath, cancellationToken).ConfigureAwait(false);

        if (stagedFacts is null)
        {
            return "The staged file disappeared before it could be verified.";
        }

        var drift = Math.Abs(stagedFacts.SizeBytes - original.SizeBytes);

        if (drift > ToleratedSizeDrift(original.SizeBytes) + shed)
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
    private static IReadOnlyList<string> Lost(TagWritePlan plan, TagSnapshot stored, TagSnapshot written)
    {
        var intended = new HashSet<string>(
            plan.Changes.Select(change => change.Field), StringComparer.OrdinalIgnoreCase);

        // ATL's DATE is its parse, which is what gets a date's precision wrong;
        // a date TagLib# read as stored is compared as stored instead.
        if (intended.Contains(CatalogueTags.Year) || stored.RecordedDate is not null) intended.Add("DATE");

        return [.. plan.Before.FieldsLostIn(written).Where(field => !intended.Contains(field))];
    }

    /// <summary>A total TagLib# read where ATL read none — see <see cref="TagWritePayload.TotalsVerified"/>.</summary>
    private static string? Total(string field, TagSnapshot? stored) => field switch
    {
        CatalogueTags.TrackTotal => stored?.TrackTotal?.ToString(CultureInfo.InvariantCulture),
        CatalogueTags.DiscTotal => stored?.DiscTotal?.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>A value PostgreSQL's <c>jsonb</c> will store.</summary>
    /// <remarks>
    /// <c>jsonb</c> refuses <c>\u0000</c> outright, and a <c>UFID</c> is an owner
    /// and an identifier either side of one. Refused, the journal's save fails
    /// after the file is committed and takes the row's new size and mtime with
    /// it — the one thing a write must never do (rule 2). Shown as U+2400, the
    /// symbol for NUL; nothing reads a <c>UFID</c> back out of the journal.
    /// </remarks>
    private static string? Storable(string? value) => value?.Replace('\0', '\u2400');

    private async Task<Guid> JournalAsync(
        TagWritePlan plan,
        string type,
        string subjectId,
        string correlationId,
        string actorId,
        string? reason,
        CancellationToken cancellationToken,
        TagSnapshot? stored = null)
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
                            Previous = Storable(change.From ?? Total(change.Field, stored)),
                            Written = Storable(change.To),
                        }),
                ],
                TotalsVerified = stored is not null,
                Reason = Storable(reason),
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
