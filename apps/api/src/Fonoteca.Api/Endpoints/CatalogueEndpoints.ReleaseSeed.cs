using System.Globalization;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Handing a folder to MusicBrainz's own release editor, prefilled.
/// </summary>
/// <remarks>
/// <b>The step before every other screen here can do anything.</b> The whole
/// matching apparatus — the worklist, the candidate sets, the by-hand album
/// dialog, the fingerprint contribution — assumes the release exists in
/// MusicBrainz and the only question is which one. A concert nobody has entered
/// breaks that assumption at the first step: there is nothing to search for,
/// nothing to seat files onto, and therefore no recording to bind a fingerprint
/// to. Measured on the library this was built for, three Mark Knopfler live
/// folders — 45 files — sit in exactly that state, and MusicBrainz answers
/// <c>0</c> for all three.
///
/// <b>Fonoteca still writes nothing to MusicBrainz, and this does not change
/// that.</b> It cannot: their <c>/ws/2</c> write API takes ratings, tags, ISRCs
/// and barcodes and <i>cannot create a release at all</i>. The supported path
/// for a tagger is the one Picard uses — POST the fields to
/// <c>musicbrainz.org/release/add</c> and let the person's own browser open the
/// release editor with them filled in. So what crosses the boundary here is a
/// form, to be submitted by a person, signed in as themselves, who reads it
/// first. The edit is theirs; the typing is ours.
///
/// <b>The seed is read from the files, because the files are all there is.</b>
/// The catalogue holds no titles for these — a <c>MediaFile</c> has a path, a
/// fingerprint and a set of outcomes — so the tags are read on the way past
/// through <see cref="AudioFileDescriber"/>, the same reader the file-detail
/// screen uses and one that never throws on a file it cannot parse. Untagged
/// files fall back to the filename, which is where a rip's track names usually
/// are anyway.
///
/// <b>The one number not taken from a tag is the length, and it is taken from
/// the catalogue first.</b> <c>FingerprintDuration</c> is what <c>fpcalc</c>
/// measured, <c>Quality.Duration</c> is what <c>ffprobe</c> measured, and either
/// beats a container's own claim about itself — the same ordering the matching
/// screens already use, and for the same reason: a VBR MP3 with no Xing header
/// declares a length that is wrong by minutes, and a track list is where that
/// error would become somebody else's data.
///
/// <b>Subfolders become mediums.</b> A folder cut at <c>Artist/Album</c> holds
/// <c>CD1</c> and <c>CD2</c> as often as it holds files, and the release editor
/// wants those as two mediums. Grouping by the directory each file actually sits
/// in gets that right without a rule about disc numbering, and collapses to one
/// medium when there are no subfolders.
///
/// <b>Nothing is written and no provider is called.</b> This is a read of the
/// catalogue and of the files, so it takes no <c>LibraryWorkGate</c> lease — for
/// the reason the file-detail endpoint states, that refusing a person's screen
/// for the length of a pass is the wrong trade for a read. It is a subprocess
/// per file, though, so it is a click rather than anything a pass does.
/// </remarks>
public static partial class CatalogueEndpoints
{
    /// <summary>
    /// Where a new release is entered. Not the configured server, deliberately.
    /// </summary>
    /// <remarks>
    /// <c>Fonoteca:MusicBrainzServer</c> may well be a local mirror, and a mirror
    /// is a read-only copy that replicates <i>from</i> here — an edit entered
    /// against one is either refused or lost at the next replication. Data is
    /// added to MusicBrainz in one place, so this is that place, and it is the
    /// only URL in this application that is not configurable.
    /// </remarks>
    private const string ReleaseEditorUrl = "https://musicbrainz.org/release/add";

    /// <summary>
    /// Most files seeded from one folder.
    /// </summary>
    /// <remarks>
    /// Not a limit on album length — a five-disc box set is comfortably inside
    /// it — but on what one click may spend, since each file is an
    /// <c>ffprobe</c>. A folder past this is not an album that needs entering,
    /// it is a path typed one level too high, and the answer to that is the
    /// error rather than four hundred subprocesses.
    /// ponytail: fixed cap; raise it if a real box set ever trips it.
    /// </remarks>
    private const int MaximumSeedTracks = 120;

    private static void MapReleaseSeedEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/matching/folders/seed", SeedRelease)
            .WithName("SeedRelease")
            .WithSummary("A prefilled MusicBrainz “add release” form for one folder.")
            .WithDescription(
                "For the case every other screen here cannot help with: the album is not in "
                + "MusicBrainz at all, so there is nothing to search for and nothing to seat "
                + "files onto. Live sets, private recordings and anything else nobody has "
                + "entered land here.\n\n"
                + "Returns the field names and values for MusicBrainz's own release editor, read "
                + "from the folder's files — title, artist, date and track list, with lengths "
                + "measured rather than declared. A client POSTs them to "
                + "`https://musicbrainz.org/release/add`, which opens the editor prefilled in the "
                + "person's own browser, under their own account. **Fonoteca sends nothing to "
                + "MusicBrainz**; their write API cannot create a release, and the judgement in "
                + "an edit is not ours to make. Add `redirect_uri` to the form and MusicBrainz "
                + "will send the browser back with `release_mbid` on the query string once the "
                + "edit is saved.\n\n"
                + "Subfolders become mediums, so a `CD1`/`CD2` rip seeds as two discs. `status` "
                + "is seeded as `bootleg`, which is what an unissued concert recording is; it is "
                + "one dropdown to change in the editor and it is worth checking. Reads the files "
                + "and nothing else — no provider call, no write, nothing touched on disk.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Results<Ok<ReleaseSeedResponse>, ProblemHttpResult>> SeedRelease(
        string? folder,
        FonotecaDbContext db,
        AudioFileDescriber describer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return TypedResults.Problem(
                title: "Nothing to seed",
                detail: "`folder` is required, and is a library-relative path.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var prefix = folder.EndsWith('/') ? folder : folder + "/";

        // Every file under the path, not only the open questions. A release's
        // track list is the whole concert: seeding the three tracks a pass
        // happened to refuse would enter an album into MusicBrainz that is
        // missing nine of its twelve songs, which is a worse thing to have done
        // than not entering it at all.
        var rows = await db.MediaFiles
            .Where(file => file.Path.StartsWith(prefix))
            .OrderBy(file => file.Path)
            .Select(file => new SeedSource(
                file.Path,
                file.FingerprintDuration,
                file.Quality == null ? null : file.Quality.Duration))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return TypedResults.Problem(
                title: "No files in that folder",
                detail:
                    $"The catalogue holds nothing under “{folder}”. A scan may not have "
                    + "reached it, or the path may not be the one the worklist printed.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (rows.Count > MaximumSeedTracks)
        {
            return TypedResults.Problem(
                title: "That folder is too big to be one release",
                detail:
                    $"“{folder}” holds {rows.Count} files, past the {MaximumSeedTracks} "
                    + "one release editor is seeded with. It is probably a level above the album.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tracks = new List<SeedTrack>(rows.Count);

        foreach (var row in rows)
        {
            // One at a time on purpose. Each is an ffprobe and a tag read, and
            // the person is waiting on the whole set rather than on any one of
            // them, so concurrency here would buy a little wall clock in
            // exchange for an unbounded number of decoders on a click.
            var reading = await describer
                .DescribeAsync(new LibraryPath(row.Path), cancellationToken)
                .ConfigureAwait(false);

            var tags = reading.Tags.ToDictionary(
                tag => tag.Name,
                tag => tag.Value,
                StringComparer.OrdinalIgnoreCase);

            tracks.Add(new SeedTrack(
                MediumOf(row.Path, prefix),
                Tag(tags, "TITLE") ?? TitleFromName(NameOf(row.Path)),
                Tag(tags, "TRACK"),
                row.FingerprintDuration ?? row.ProbedDuration ?? reading.Duration,
                Tag(tags, "ALBUM"),
                Tag(tags, "ALBUMARTIST") ?? Tag(tags, "ARTIST"),
                Tag(tags, "DATE")));
        }

        var title = Commonest(tracks.Select(track => track.Album)) ?? LeafOf(folder);
        var artist = Commonest(tracks.Select(track => track.Artist)) ?? RootOf(folder);
        var year = Year(Commonest(tracks.Select(track => track.Date)));

        // Shortest first, then ordinal — which is the cheapest thing that orders
        // "CD 2" before "CD 10". A plain ordinal sort puts disc 10 second, and
        // the mediums are seeded in the order they come out of here, so on a
        // box set that is a release whose discs are in the wrong order with
        // nothing on screen saying so.
        // ponytail: length-then-ordinal, not a natural sort; enough for CD n.
        var mediums = tracks
            .Select(track => track.Medium)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name.Length)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();

        var fields = new List<SeedField>
        {
            new("name", title),
            new("artist_credit.names.0.name", artist),
            new("artist_credit.names.0.artist.name", artist),

            // What an unissued concert recording is, in MusicBrainz's own
            // vocabulary. Seeded rather than left blank because the editor's own
            // default is "Official", which is the one answer that is certainly
            // wrong for a folder that is not in MusicBrainz at all — and a
            // wrongly-official release is a thing somebody else has to correct.
            new("status", "bootleg"),

            new(
                "edit_note",
                $"Track list and timings from a personal rip of this recording (folder: {folder}). "
                + "Seeded with Fonoteca; lengths are decoder-measured, not read from tags."),
        };

        if (year is { } stated)
        {
            fields.Add(new SeedField(
                "events.0.date.year", stated.ToString(CultureInfo.InvariantCulture)));
        }

        for (var medium = 0; medium < mediums.Count; medium++)
        {
            var seated = tracks.Where(track => track.Medium == mediums[medium]).ToList();

            // No `mediums.N.name`. MusicBrainz means that field as a medium's
            // *title* — "Bonus Disc", "The Rehearsals" — and a subfolder called
            // "CD 1" is a designator, which the medium's own position already
            // states. Seeded, every two-disc rip enters a release with a disc
            // literally titled "CD 1" for somebody else to remove.

            for (var position = 0; position < seated.Count; position++)
            {
                var track = seated[position];
                var slot = $"mediums.{medium}.track.{position}";

                fields.Add(new SeedField($"{slot}.name", track.Title));

                // The tag's number when it is a plain number, and the position
                // otherwise. A rip missing track 3 is numbered 1, 2, 4 by its
                // tags and 1, 2, 3 by its order, and the tags are right — but
                // "4/12" and "A1" are also things that live in that field, and
                // seeding one of those as a MusicBrainz track number is how a
                // release ends up printing "4/12" on its fourth line.
                fields.Add(new SeedField(
                    $"{slot}.number",
                    Number(track.Number) ?? (position + 1).ToString(CultureInfo.InvariantCulture)));

                if (track.Length is { } length)
                {
                    fields.Add(new SeedField(
                        $"{slot}.length",
                        ((long)length.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        return TypedResults.Ok(new ReleaseSeedResponse(
            folder,
            ReleaseEditorUrl,
            title,
            artist,
            year,
            tracks.Count,
            mediums.Count,
            tracks.Count(track => track.Length is null),
            fields));
    }

    /// <summary>Which disc a file belongs to: the directory it actually sits in.</summary>
    /// <remarks>
    /// Relative to the seeded folder, so a flat album is one empty-named medium
    /// and <c>CD1</c>/<c>CD2</c> are two named ones. Deeper nesting collapses to
    /// the whole remaining path, which keeps the grouping honest without
    /// inventing a disc order the folder did not state.
    /// </remarks>
    private static string MediumOf(string path, string prefix)
    {
        var relative = path[prefix.Length..];
        var cut = relative.LastIndexOf('/');

        return cut < 0 ? string.Empty : relative[..cut];
    }

    /// <summary>A track title from a filename, when the file carries no tags.</summary>
    /// <remarks>
    /// Strips the extension and a leading track number, which is how rips are
    /// named and the only two things that are safe to remove. Everything else is
    /// left alone: a person is about to read this in the release editor, and a
    /// title that is slightly wrong there is corrected in the box it is sitting
    /// in, while a title this was clever about is one nobody notices.
    /// </remarks>
    private static string TitleFromName(string name)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;

        var digits = 0;
        while (digits < stem.Length && char.IsAsciiDigit(stem[digits])) digits++;

        if (digits == 0) return stem;

        var rest = stem[digits..].TrimStart(' ', '-', '.', '_');

        return rest.Length == 0 ? stem : rest;
    }

    /// <summary>
    /// A track number MusicBrainz can print, or null if the tag does not hold one.
    /// </summary>
    /// <remarks>
    /// <b>The leading digits, and nothing about the rest.</b> A track tag is a
    /// position and a count in one field, and its spelling belongs to whichever
    /// library read it rather than to the container: TagLib# hands this one
    /// <c>"7 of 12"</c>, the raw ID3 frame says <c>"7/12"</c>, and a first draft
    /// that only knew about the slash quietly returned null for every file whose
    /// container states a total — which is most of a ripped album. The fallback
    /// then numbered the release by <i>path order</i>, which is right on a
    /// complete rip and wrong on exactly the gapped folders this screen exists
    /// for: a rip missing track 3 is 1, 2, 4 by its tags, and the tags are the
    /// ones that agree with the album.
    ///
    /// Anything not starting with a digit is null rather than guessed at.
    /// <c>"A1"</c> is a real vinyl number and this could not tell it from a
    /// filename fragment, so it defers to the position and lets a person type
    /// the side in.
    /// </remarks>
    private static string? Number(string? tagged)
    {
        if (string.IsNullOrWhiteSpace(tagged)) return null;

        var value = tagged.Trim();

        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits])) digits++;

        if (digits == 0) return null;

        // "007" is a filename habit rather than a track number, and MusicBrainz
        // prints what it is given.
        var number = value[..digits].TrimStart('0');

        return number.Length > 0 ? number : "0";
    }

    /// <summary>The value most of the files agree on, ties broken by first seen.</summary>
    /// <remarks>
    /// One file in a folder with a stray <c>ALBUM</c> tag should not name the
    /// release, and one file missing the tag entirely should not make the folder
    /// nameless. Nulls and blanks do not vote.
    /// </remarks>
    private static string? Commonest(IEnumerable<string?> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();

    /// <summary>The year in a date tag, which may be a year or a whole date.</summary>
    private static int? Year(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;

        var digits = date.Trim();
        if (digits.Length < 4) return null;

        return int.TryParse(
            digits.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is > 1000 and < 3000
                ? year
                : null;
    }

    private static string? Tag(Dictionary<string, string> tags, string name) =>
        tags.TryGetValue(name, out var value) ? value : null;

    /// <summary>The last segment of a folder path: usually the album's name.</summary>
    private static string LeafOf(string folder)
    {
        var trimmed = folder.TrimEnd('/');
        var cut = trimmed.LastIndexOf('/');

        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>The first segment of a folder path: usually the artist.</summary>
    private static string RootOf(string folder)
    {
        var cut = folder.IndexOf('/', StringComparison.Ordinal);

        return cut < 0 ? folder : folder[..cut];
    }

    /// <summary>What the catalogue knows about one file before it is opened.</summary>
    private sealed record SeedSource(
        string Path, TimeSpan? FingerprintDuration, TimeSpan? ProbedDuration);

    /// <summary>One file, as much of a track as it can describe itself to be.</summary>
    private sealed record SeedTrack(
        string Medium,
        string Title,
        string? Number,
        TimeSpan? Length,
        string? Album,
        string? Artist,
        string? Date);
}

/// <summary>One field of MusicBrainz's release editor, and what to put in it.</summary>
/// <remarks>
/// A list rather than a map, because the order is the track order and a map on
/// the wire has none. The names are MusicBrainz's own seeding parameters —
/// <c>mediums.0.track.3.length</c> — so the client renders them into hidden
/// inputs without knowing what any of them mean, which is what keeps their
/// vocabulary out of the browser.
/// </remarks>
public sealed record SeedField(string Name, string Value);

/// <summary>A folder, rewritten as a release somebody could enter.</summary>
/// <param name="Action">Where to POST. Always musicbrainz.org, never the configured server.</param>
/// <param name="UnmeasuredTracks">
/// Tracks seeded with no length at all — neither fingerprinted nor probed, and
/// the container's own claim missing too. Worth printing: a track list with
/// holes in it is still worth entering, and a person should know before they
/// look at it rather than after.
/// </param>
public sealed record ReleaseSeedResponse(
    string Folder,
    string Action,
    string Title,
    string Artist,
    int? Year,
    int TrackCount,
    int MediumCount,
    int UnmeasuredTracks,
    IReadOnlyList<SeedField> Fields);
