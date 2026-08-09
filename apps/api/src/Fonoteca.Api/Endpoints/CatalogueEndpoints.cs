using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Reading the catalogue back out. The first endpoints that do.
/// </summary>
/// <remarks>
/// Same shape as <see cref="SystemEndpoints"/> and <see cref="LibraryEndpoints"/>:
/// one static class per capability, one <c>MapXEndpoints</c> extension, DTOs at
/// the bottom of the file.
///
/// <b>An artist's tracks come from three places, not one.</b> Being on the
/// printed credit line is only the most obvious way to be responsible for a
/// recording; MusicBrainz bills a classical recording to its composer and leaves
/// the conductor and the orchestra in relationships, and the composer link
/// itself hangs off the <i>work</i> rather than the performance. A query that
/// reads <c>ArtistCredits</c> alone therefore shows Karajan nothing and files
/// every symphony under a man who died in 1827. All three are unioned here, and
/// which one matched is returned as the track's <c>Roles</c> so the answer is
/// legible rather than mysterious.
///
/// <b>Only recordings that have a file are listed.</b> This is a library
/// browser, not a MusicBrainz browser: an artist page that included every
/// recording MusicBrainz links to them would be mostly music the user does not
/// own, and the one question it exists to answer — "what do I have by this
/// artist" — would be the one it could not answer.
/// </remarks>
public static class CatalogueEndpoints
{
    /// <summary>
    /// The most artists one response will carry.
    /// </summary>
    /// <remarks>
    /// Generous rather than tuned: the author's library yields a few hundred, so
    /// the default fetches all of them in one request and the paging exists so
    /// that a library ten times the size does not have to discover the limit the
    /// hard way.
    /// </remarks>
    private const int DefaultTake = 500;

    private const int MaxTake = 1000;

    public static IEndpointRouteBuilder MapCatalogueEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalogue").WithTags("Catalogue");

        group.MapGet("/artists", GetArtists)
            .WithName("GetArtists")
            .WithSummary("Artists with at least one track in the library.")
            .WithDescription(
                "Ordered by sort name. `query` filters on the artist's name, case-insensitively, "
                + "anywhere in the string. An artist appears here if they are on a recording's "
                + "credit line, are linked to it as a conductor or ensemble, or wrote the work it "
                + "performs — and only when at least one file in the library holds that recording.");

        group.MapGet("/artists/{id:guid}", GetArtist)
            .WithName("GetArtist")
            .WithSummary("One artist and every track of theirs in the library.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Ok<ArtistListResponse>> GetArtists(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        int skip = 0,
        int take = DefaultTake)
    {
        var wanted = Math.Clamp(take, 1, MaxTake);
        var from = Math.Max(skip, 0);

        var matching = db.Artists.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
        {
            // ILIKE, through EF.Functions, so the pg_trgm GIN index on Name can
            // serve it. Contains() would compile to the same operator only for
            // some providers, and being explicit is the difference between an
            // index scan and a sequential one at a hundred thousand rows.
            var pattern = $"%{Escape(query.Trim())}%";
            matching = matching.Where(a => EF.Functions.ILike(a.Name, pattern, "\\"));
        }

        // The same rule as TracksOf, written out. It cannot be shared: a helper
        // taking the artist as an argument is a closure over the row being
        // projected, and EF gives up on that at runtime with a 500 rather than
        // at compile time. Expressing it as a set of (artist, recording) pairs
        // and unioning them fails differently — EF refuses a set operation over
        // a projection to a type of ours. So it is inline here, and
        // TheListAndTheDetailPageAgree pins the two copies together.
        var withCounts = matching
            .Select(a => new
            {
                Artist = a,
                TrackCount = db.Recordings.Count(recording =>
                    recording.Files.Any()
                    && (recording.Credits.Any(credit => credit.ArtistId == a.Id)
                        || recording.Relationships.Any(link => link.ArtistId == a.Id)
                        || (recording.Work != null
                            && recording.Work.Relationships.Any(link => link.ArtistId == a.Id)))),
            })
            .Where(row => row.TrackCount > 0);

        var total = await withCounts.CountAsync(cancellationToken).ConfigureAwait(false);

        var page = await withCounts
            // SortName first — "Beatles, The" is what an alphabetical list wants
            // and Name is not. Falling back to Name rather than sorting nulls
            // wherever the collation puts them; the id breaks ties so paging
            // cannot show or skip a row.
            .OrderBy(row => row.Artist.SortName ?? row.Artist.Name)
            .ThenBy(row => row.Artist.Id)
            .Skip(from)
            .Take(wanted)
            .Select(row => new ArtistSummary(
                row.Artist.Id.Value,
                row.Artist.Name,
                row.Artist.SortName,
                row.Artist.Disambiguation,
                row.Artist.Type,
                row.TrackCount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new ArtistListResponse(total, page));
    }

    private static async Task<Results<Ok<ArtistDetailResponse>, ProblemHttpResult>> GetArtist(
        Guid id,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var artistId = new ArtistId(id);

        var artist = await db.Artists
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken)
            .ConfigureAwait(false);

        if (artist is null)
        {
            return TypedResults.Problem(
                title: "No such artist",
                detail: $"The catalogue has no artist with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var tracks = await TracksOf(db, artistId)
            .AsNoTracking()
            .Select(recording => new
            {
                recording.Id,
                recording.Title,
                recording.Duration,
                WorkTitle = recording.Work == null ? null : recording.Work.Title,
                Billed = recording.Credits.Any(c => c.ArtistId == artistId),
                Roles = recording.Relationships
                    .Where(r => r.ArtistId == artistId)
                    .Select(r => r.Type)
                    .ToList(),
                WorkRoles = recording.Work == null
                    ? new List<string>()
                    : recording.Work.Relationships
                        .Where(r => r.ArtistId == artistId)
                        .Select(r => r.Type)
                        .ToList(),
                Files = recording.Files
                    .OrderBy(f => f.Path)
                    .Select(f => new { f.Path, f.SizeBytes })
                    .ToList(),
            })
            .OrderBy(row => row.Title)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = tracks
            .Select(row => new TrackRow(
                RecordingId: row.Id.Value,
                Title: row.Title,
                WorkTitle: row.WorkTitle,
                // Formatted here rather than sent as a number: this is a display
                // string, and the alternative — seconds, formatted in the browser
                // — puts the same decision in a second place.
                Duration: Format(row.Duration),
                Roles: Roles(row.Billed, row.Roles, row.WorkRoles),
                Folder: FolderOf(row.Files[0].Path),
                Files: [.. row.Files.Select(file => new FileRow(file.Path, file.SizeBytes))]))
            .ToList();

        return TypedResults.Ok(new ArtistDetailResponse(
            new ArtistSummary(
                artist.Id.Value,
                artist.Name,
                artist.SortName,
                artist.Disambiguation,
                artist.Type,
                rows.Count),
            rows));
    }

    /// <summary>
    /// Every recording in the library this artist is responsible for.
    /// </summary>
    /// <remarks>
    /// The three ways of being responsible: on the printed credit line, linked to
    /// the recording as conductor or ensemble, or a writer of the work it
    /// performs. The third is why a query cannot stop at the first — MusicBrainz
    /// puts the composer on the work, so joining there is what spreads one
    /// "composer" link across every performance of the piece.
    ///
    /// One predicate rather than a union of three sets, and that is EF's choice
    /// rather than ours: a set operation over a projection to a type of ours is
    /// refused at runtime ("unable to translate set operation after client
    /// projection has been applied"). As a predicate it composes into a single
    /// correlated <c>EXISTS</c> per branch, which is also the plan PostgreSQL
    /// wants.
    ///
    /// <c>Files.Any()</c> is the library filter, and first because it is by far
    /// the most selective: a recording with no file is one the catalogue learned
    /// about some other way, and this is a browser for what the user owns.
    ///
    /// <b>The artist list holds a second copy of this predicate</b>, inline,
    /// because a helper taking the artist as an argument cannot be used inside a
    /// projection — EF reads the argument as a closure over the row and gives up.
    /// <c>TheListAndTheDetailPageAgree</c> is what stops the two drifting.
    /// </remarks>
    private static IQueryable<Recording> TracksOf(FonotecaDbContext db, ArtistId artist) =>
        db.Recordings.Where(recording =>
            recording.Files.Any()
            && (recording.Credits.Any(credit => credit.ArtistId == artist)
                || recording.Relationships.Any(link => link.ArtistId == artist)
                || (recording.Work != null
                    && recording.Work.Relationships.Any(link => link.ArtistId == artist))));

    /// <summary>Why this artist has this track, strongest claim first.</summary>
    private static IReadOnlyList<string> Roles(
        bool billed,
        List<string> recordingRoles,
        List<string> workRoles)
    {
        var roles = new List<string>(recordingRoles.Count + workRoles.Count + 1);

        if (billed) roles.Add("billed");

        roles.AddRange(recordingRoles);
        roles.AddRange(workRoles);

        return [.. roles.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The folder a track's files sit in, which stands in for the album.
    /// </summary>
    /// <remarks>
    /// The filesystem's claim, not MusicBrainz's, and labelled as such in the
    /// UI. Releases are not attributed yet — deciding which of the thirty
    /// releases a recording appears on a given file actually came from is a rule
    /// of its own — and until they are, the directory is the only album-shaped
    /// fact the catalogue holds. It is also usually right.
    /// </remarks>
    private static string FolderOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut <= 0 ? string.Empty : path[..cut];
    }

    /// <summary>Duration as <c>m:ss</c>, or <c>h:mm:ss</c> past the hour.</summary>
    private static string? Format(TimeSpan? duration) => duration switch
    {
        null => null,
        { TotalHours: >= 1 } value => $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}",
        { } value => $"{(int)value.TotalMinutes}:{value.Seconds:D2}",
    };

    /// <summary>
    /// Neutralises the wildcards in a user's filter.
    /// </summary>
    /// <remarks>
    /// Without this a search for <c>%</c> matches everything and a search for
    /// <c>_</c> matches every single character — not a security hole, since the
    /// value is still parameterised, but a search box that lies about what it
    /// found.
    /// </remarks>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>A page of artists, with the total the filter matched.</summary>
/// <remarks>
/// <c>Total</c> is the count before paging, so a client can say "showing 50 of
/// 312" without a second request.
/// </remarks>
public sealed record ArtistListResponse(int Total, IReadOnlyList<ArtistSummary> Items);

/// <param name="Type">Person, Group, Orchestra, Choir. Null when MusicBrainz does not say.</param>
/// <param name="TrackCount">Recordings in the library this artist is credited on.</param>
public sealed record ArtistSummary(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    string? Type,
    int TrackCount);

/// <summary>One artist and every track of theirs the library holds.</summary>
public sealed record ArtistDetailResponse(ArtistSummary Artist, IReadOnlyList<TrackRow> Tracks);

/// <param name="WorkTitle">The composition, when MusicBrainz links one. Usually null outside classical.</param>
/// <param name="Duration">Pre-formatted for display; null when MusicBrainz does not know it.</param>
/// <param name="Roles">Why this artist has this track: "billed", "conductor", "ensemble", "composer".</param>
/// <param name="Folder">
/// The directory the files sit in. The filesystem's idea of the album, not
/// MusicBrainz's — releases are not attributed yet.
/// </param>
/// <param name="Files">
/// Every file holding this recording. More than one is the point rather than a
/// problem: it is the same recording in several encodings, which is what the
/// Recording/MediaFile split exists to express.
/// </param>
public sealed record TrackRow(
    Guid RecordingId,
    string Title,
    string? WorkTitle,
    string? Duration,
    IReadOnlyList<string> Roles,
    string Folder,
    IReadOnlyList<FileRow> Files);

public sealed record FileRow(string Path, long SizeBytes);
