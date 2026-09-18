using System.Globalization;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// The Identify screen: one album folder at a time, with what its files claim.
/// </summary>
/// <remarks>
/// <b>The unit is the folder, whatever each file in it was refused for.</b> The
/// worklist splits one rip into a component question and a handful of file
/// questions under two headings; a person answers it once, by naming the album.
/// So the queue groups every open file by <see cref="AlbumFolder.Of"/> and the
/// folder endpoint merges them back into one question.
///
/// <b>The tags are read because they are frequently the answer.</b> Measured on
/// the target library, the refused files carry full Picard tag sets —
/// <c>MUSICBRAINZ_ALBUMID</c> included — which nothing else here reads. They
/// order the queue (folders whose files agree on one release come first) and
/// prefill the screen's search. They decide nothing: the screen still shows the
/// pairing and a person still presses File.
///
/// <b>Tags only, never <c>ffprobe</c>.</b> <see cref="AudioFileDescriber.ReadTagsAsync"/>
/// is a parse per file rather than a subprocess, and the reading is cached per
/// path, size and modification time — the same identity the scan uses to decide
/// a file changed, so a retagged file is read again and nothing else is.
/// </remarks>
public static partial class CatalogueEndpoints
{
    /// <summary>How long one file's tags are believed without reading them again.</summary>
    /// <remarks>
    /// The key already changes when the file does; this only bounds how long
    /// entries for files that left the worklist stay in memory.
    /// </remarks>
    private static readonly TimeSpan TagCacheDuration = TimeSpan.FromHours(1);

    private static void MapIdentifyEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/matching/folders/queue", GetIdentifyQueue)
            .WithName("GetIdentifyQueue")
            .WithSummary("Every album folder with an open question, in the order to answer them.")
            .WithDescription(
                "One row per album folder (`Artist/Album`, discs collapsed) holding at least one "
                + "file a filing would accept. Folders whose open files all carry the same "
                + "`MUSICBRAINZ_ALBUMID` tag come first, then the rest by how many files are "
                + "open, then by path.\n\n"
                + "Reads every open file's tags to order the list, cached per path, size and "
                + "modification time. No provider call, nothing written. Files sitting at the "
                + "library root belong to no album folder and are not listed.");

        group.MapGet("/matching/folders/identify", GetIdentifyFolder)
            .WithName("GetIdentifyFolder")
            .WithSummary("One album folder's open files, and what their tags say.")
            .WithDescription(
                "Every open file in the folder whatever it was refused for, in path order, with "
                + "its tags and the title, track and disc read from them — the filename stands in "
                + "for a missing title. `tags` summarises the folder: the album, artist, year and "
                + "label most files agree on, and the release id most files name with how many "
                + "name it.\n\n"
                + "`files` counts everything in the folder, answered files included, so "
                + "`files - open` is how much a pass or a person already placed. Reads tags only; "
                + "no provider call, nothing written.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Ok<IdentifyQueueResponse>> GetIdentifyQueue(
        FonotecaDbContext db,
        AudioFileDescriber describer,
        IMemoryCache cache,
        CancellationToken cancellationToken)
    {
        // The filing endpoint's own predicate, so every folder offered here is one
        // `matching/files/release` will actually accept files from.
        var rows = await db.MediaFiles
            .AsNoTracking()
            .Where(file => (file.IdentityDecidedUtc == null
                    && (UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                        || UnlinkedOutcomes.Contains(file.EnrichmentOutcome)))
                || (file.ReleaseDecidedUtc == null
                    && UnattributedOutcomes.Contains(file.AttributionOutcome)))
            .Select(file => new TagSource(file.Path, file.SizeBytes, file.LastModifiedUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new List<IdentifyQueueRow>();

        foreach (var folder in rows
            .GroupBy(row => AlbumFolder.Of(row.Path), StringComparer.Ordinal)
            .Where(folder => folder.Key.Length > 0))
        {
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unnamed = false;

            foreach (var file in folder)
            {
                var tags = await TagsOfAsync(describer, cache, file, cancellationToken)
                    .ConfigureAwait(false);

                if (TagNamed(tags, "MUSICBRAINZ_ALBUMID") is { } release) named.Add(release);
                else unnamed = true;
            }

            items.Add(new IdentifyQueueRow(folder.Key, folder.Count(), !unnamed && named.Count == 1));
        }

        var ordered = items
            .OrderByDescending(item => item.TagsNameRelease)
            .ThenByDescending(item => item.Open)
            .ThenBy(item => item.Folder, StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok(new IdentifyQueueResponse(
            ordered.Count, ordered.Sum(item => item.Open), ordered));
    }

    internal static async Task<Results<Ok<IdentifyFolderResponse>, ProblemHttpResult>>
        GetIdentifyFolder(
            string? folder,
            FonotecaDbContext db,
            AudioFileDescriber describer,
            IMemoryCache cache,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return TypedResults.Problem(
                title: "No folder named",
                detail: "`folder` is required, and is an album folder as the queue lists it.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var prefix = folder.EndsWith('/') ? folder : folder + "/";
        var album = folder.TrimEnd('/');

        var candidates = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Path.StartsWith(prefix))
            .OrderBy(file => file.Path)
            .Select(file => new
            {
                file.Id,
                file.Path,
                file.SizeBytes,
                file.LastModifiedUtc,
                file.FingerprintDuration,
                Measured = file.Quality == null ? null : file.Quality.Duration,
                Codec = file.Quality == null ? null : file.Quality.Codec,
                Bitrate = file.Quality == null ? (long?)null : file.Quality.BitrateBps,
                SampleRate = file.Quality == null ? (int?)null : file.Quality.SampleRateHz,
                BitDepth = file.Quality == null ? null : file.Quality.BitDepth,
                Lossless = file.Quality == null ? (bool?)null : file.Quality.IsLossless,
                file.AcoustIdOutcome,
                file.EnrichmentOutcome,
                file.AttributionOutcome,
                file.IdentityDecidedUtc,
                file.ReleaseDecidedUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Exactly the files the queue grouped under this name. For an album folder
        // that is everything under the prefix; for a stray file directly under an
        // artist it is not, since the prefix is then the whole discography.
        var inFolder = candidates
            .Where(file => AlbumFolder.Of(file.Path) == album)
            .ToList();

        // Disc folders numbered over every file in the album, answered ones too:
        // counted over the open files alone, a fully filed CD 1 would make CD 2
        // disc 1. A folder's disc is the number in its name ("CD 2", "Disc 02 -
        // Live"), and only when every disc folder carries a different number: a
        // "Bonus" folder, or one lone subfolder beside loose files, says nothing
        // about which disc it is, and a guessed disc files onto the wrong one.
        var subFolders = inFolder
            .Select(file => FolderOf(file.Path))
            .Select(own => own.Length > prefix.Length ? own[prefix.Length..] : string.Empty)
            .Where(sub => sub.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var numbered = subFolders
            .Select(sub => (Sub: sub, Number: FirstNumber(sub)))
            .ToList();

        var discFolders = subFolders.Count > 1
            && numbered.All(entry => entry.Number is not null)
            && numbered.Select(entry => entry.Number).Distinct().Count() == numbered.Count
                ? numbered.ToDictionary(entry => entry.Sub, entry => entry.Number!.Value, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);

        if (inFolder.Count == 0)
        {
            return TypedResults.Problem(
                title: "No files in that folder",
                detail:
                    $"The catalogue holds nothing under “{folder}”. It may have been answered "
                    + "or moved since the queue was read.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var open = inFolder.Count(file =>
            (file.IdentityDecidedUtc is null
                && (UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                    || UnlinkedOutcomes.Contains(file.EnrichmentOutcome)))
            || (file.ReleaseDecidedUtc is null
                && UnattributedOutcomes.Contains(file.AttributionOutcome)));

        var items = new List<IdentifyFileRow>();
        var readings = new List<IReadOnlyList<TagValue>>();

        foreach (var file in inFolder)
        {
            if (items.Count >= MaximumFolderFiles) break;

            var unidentified = file.IdentityDecidedUtc is null
                && (UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                    || UnlinkedOutcomes.Contains(file.EnrichmentOutcome));

            var unattributed = file.ReleaseDecidedUtc is null
                && UnattributedOutcomes.Contains(file.AttributionOutcome);

            if (!unidentified && !unattributed) continue;

            var tags = await TagsOfAsync(
                    describer,
                    cache,
                    new TagSource(file.Path, file.SizeBytes, file.LastModifiedUtc),
                    cancellationToken)
                .ConfigureAwait(false);

            readings.Add(tags);

            var length = file.FingerprintDuration ?? file.Measured;
            var own = FolderOf(file.Path);
            var sub = own.Length > prefix.Length ? own[prefix.Length..] : string.Empty;
            var disc = WholeNumber(TagNamed(tags, "DISC"))
                ?? (discFolders.TryGetValue(sub, out var numberedDisc) ? numberedDisc : null);

            items.Add(new IdentifyFileRow(
                file.Id.Value,
                file.Path,
                NameOf(file.Path),
                sub,
                TagNamed(tags, "TITLE") ?? TitleFromName(NameOf(file.Path)),
                WholeNumber(TagNamed(tags, "TRACK")),
                disc,
                Format(length),
                length is { } measured ? (int)measured.TotalMilliseconds : null,

                // The first pass that refused, as the worklist and the folder
                // listing both name it.
                UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                    ? file.AcoustIdOutcome.ToString()
                    : unidentified
                        ? file.EnrichmentOutcome.ToString()
                        : file.AttributionOutcome.ToString(),
                Bytes(file.SizeBytes),
                ExtensionOf(file.Path),
                [.. tags.Select(tag => new FileTagRow(tag.Name, tag.Value))]));
        }

        var releases = readings
            .Select(tags => TagNamed(tags, "MUSICBRAINZ_ALBUMID"))
            .Where(value => value is not null)
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();

        var summary = new IdentifyFolderTags(
            Commonest(readings.Select(tags => TagNamed(tags, "ALBUM"))),
            Commonest(readings.Select(tags =>
                TagNamed(tags, "ALBUMARTIST") ?? TagNamed(tags, "ARTIST"))),
            Year(Commonest(readings.Select(tags => TagNamed(tags, "DATE")))),
            Commonest(readings.Select(tags =>
                TagNamed(tags, "PUBLISHER") ?? TagNamed(tags, "LABEL"))),
            Math.Max(
                Math.Max(1, discFolders.Count),
                items.Select(item => item.Disc ?? 1).DefaultIfEmpty(1).Max()),
            releases is not null && Guid.TryParse(releases.Key, out var release) ? release : null,
            releases?.Count() ?? 0);

        var sample = inFolder.FirstOrDefault(file => file.Codec is not null);

        var audio = sample is null
            ? string.Join(
                " · ",
                items.Select(item => item.Format).Distinct(StringComparer.Ordinal))
            : string.Join(
                " · ",
                new[]
                {
                    ExtensionOf(sample.Path),
                    sample.BitDepth is { } depth
                        ? string.Create(CultureInfo.InvariantCulture, $"{depth}-bit")
                        : null,
                    sample.SampleRate is > 0 and var rate
                        ? string.Create(CultureInfo.InvariantCulture, $"{rate / 1000.0:0.###} kHz")
                        : null,
                    sample.Bitrate is > 0 and var bitrate
                        ? string.Create(CultureInfo.InvariantCulture, $"{bitrate / 1000} kbps")
                        : null,
                    sample.Lossless == true ? "lossless" : "lossy",
                }.Where(part => !string.IsNullOrEmpty(part)));

        return TypedResults.Ok(new IdentifyFolderResponse(
            album,
            inFolder.Count,
            open,
            audio,
            summary,
            items));
    }

    /// <summary>One file's tags, read once per version of the file.</summary>
    private static async Task<IReadOnlyList<TagValue>> TagsOfAsync(
        AudioFileDescriber describer,
        IMemoryCache cache,
        TagSource file,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(file, out IReadOnlyList<TagValue>? cached) && cached is not null)
        {
            return cached;
        }

        var tags = await describer.ReadTagsAsync(new LibraryPath(file.Path), cancellationToken)
            .ConfigureAwait(false);

        // A read that failed — an unmounted volume, an I/O error — is not "this
        // file has no tags", and caching it as that would hide the folder's
        // release for an hour. It is answered empty and asked again next time.
        if (tags is null) return [];

        cache.Set(file, tags, TagCacheDuration);

        return tags;
    }

    /// <summary>A tag by name, case-insensitively, or null when absent or blank.</summary>
    private static string? TagNamed(IReadOnlyList<TagValue> tags, string name)
    {
        var value = tags
            .FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The first run of digits anywhere in a name, or null when it has none.</summary>
    private static int? FirstNumber(string name)
    {
        var start = 0;
        while (start < name.Length && !char.IsAsciiDigit(name[start])) start++;

        var end = start;
        while (end < name.Length && char.IsAsciiDigit(name[end])) end++;

        return end > start
            && int.TryParse(
                name.AsSpan(start, Math.Min(end - start, 6)),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number)
                ? number
                : null;
    }

    /// <summary>The leading number of a "7", "7/12" or "7 of 12" tag.</summary>
    private static int? WholeNumber(string? tagged) =>
        Number(tagged) is { } number
        && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>The identity a tag reading is cached under: the scan's own idea of "this version".</summary>
    private sealed record TagSource(string Path, long Size, DateTimeOffset Modified);
}

/// <summary>Every album folder with an open question, in the order to answer them.</summary>
/// <param name="Total">Folders in the queue.</param>
/// <param name="Files">Open files across all of them.</param>
public sealed record IdentifyQueueResponse(
    int Total,
    int Files,
    IReadOnlyList<IdentifyQueueRow> Items);

/// <summary>One album folder on the queue.</summary>
/// <param name="TagsNameRelease">Every open file carries the same MusicBrainz release id.</param>
public sealed record IdentifyQueueRow(string Folder, int Open, bool TagsNameRelease);

/// <summary>One album folder's open files, and what their tags say.</summary>
/// <param name="Files">Every file in the folder, answered ones included.</param>
/// <param name="Open">Files still waiting on an answer, counted over the whole folder. <paramref name="Items"/> stops at 300.</param>
/// <param name="Audio">What the decoder measured on one file, or the containers when nothing was measured.</param>
public sealed record IdentifyFolderResponse(
    string Folder,
    int Files,
    int Open,
    string Audio,
    IdentifyFolderTags Tags,
    IReadOnlyList<IdentifyFileRow> Items);

/// <summary>What most of the open files' tags agree the folder is.</summary>
/// <param name="Discs">The highest disc tag, or the number of subfolders, whichever is more.</param>
/// <param name="Release">The MusicBrainz release id most files name.</param>
/// <param name="Agreeing">How many open files name <paramref name="Release"/>.</param>
public sealed record IdentifyFolderTags(
    string? Album,
    string? Artist,
    int? Year,
    string? Label,
    int Discs,
    Guid? Release,
    int Agreeing);

/// <summary>One open file, and what it claims about itself.</summary>
/// <param name="SubFolder">The disc folder under the album folder, or "" for a flat rip.</param>
/// <param name="Disc">The DISC tag, or the number in the disc folder's name; null when neither says.</param>
/// <param name="Title">The TITLE tag, or the filename with its track number removed.</param>
/// <param name="Reason">The first refusal, named as the worklist names it.</param>
public sealed record IdentifyFileRow(
    Guid MediaFileId,
    string Path,
    string Name,
    string SubFolder,
    string Title,
    int? Track,
    int? Disc,
    string? Length,
    int? LengthMs,
    string Reason,
    string Size,
    string Format,
    IReadOnlyList<FileTagRow> Tags);
