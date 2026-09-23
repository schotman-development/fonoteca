using System.Globalization;
using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// The catalogue, read into the protocol's three shapes.
/// </summary>
/// <remarks>
/// Projections, not rules. Everything that decides something — the billing
/// line, a person's corrections, which media type a file is served as — is
/// called out to the code that already owns it; what is here is the joins and
/// the arithmetic between a row and an attribute.
///
/// <b>Whole lists are loaded and paged in memory, deliberately.</b>
/// <c>getArtists</c> has no paging in the protocol at all, and an album's
/// billing line is a nested collection that cannot be ordered in SQL without
/// costing more than the rows do. This is a library of hundreds of albums, not
/// a streaming service's catalogue; the day that stops being true, the shape to
/// change is this one.
/// </remarks>
public static partial class SubsonicEndpoints
{
    /// <summary>
    /// The artists the album list is billed to.
    /// </summary>
    /// <remarks>
    /// Takes the albums rather than fetching them, because every caller already
    /// has them: an artist here is defined as somebody an album is billed to, so
    /// deriving one from the other twice would be the same expensive query run
    /// for the same answer.
    /// </remarks>
    private static async Task<IReadOnlyList<SubsonicArtist>> ArtistsOfAsync(
        FonotecaDbContext db,
        IReadOnlyList<Album> albums,
        CancellationToken cancellationToken)
    {
        var billed = albums
            .Where(album => album.ArtistId is not null)
            .Select(album => album.ArtistId!.Value)
            .Distinct()
            .ToList();

        if (billed.Count == 0) return [];

        var described = await db.Artists
            .AsNoTracking()
            .Where(artist => billed.Contains(artist.Id))
            .Select(artist => new
            {
                artist.Id,
                artist.Name,
                artist.LatinName,
                artist.SortName,
                artist.Mbid,
                artist.PortraitUrl,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = described.ToDictionary(artist => artist.Id);

        return albums
            .Where(album => album.ArtistId is not null)
            .GroupBy(album => album.ArtistId!.Value)
            .Where(group => byId.ContainsKey(group.Key))
            .Select(group =>
            {
                var artist = byId[group.Key];

                return new SubsonicArtist(
                    artist.Id,
                    artist.LatinName ?? artist.Name,
                    artist.SortName,
                    artist.Mbid,
                    artist.PortraitUrl,
                    group.Count());
            })
            .OrderBy(artist => artist.SortName ?? artist.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artist => artist.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every release the library holds files for.
    /// </summary>
    /// <remarks>
    /// <b>Files, not tracks.</b> A release the catalogue knows about but owns
    /// nothing of is a gap on the Acquire screen; offering it to a client that
    /// would try to play it is the wrong answer to a different question.
    /// </remarks>
    private static async Task<IReadOnlyList<Album>> AlbumsAsync(
        FonotecaDbContext db,
        ArtistId? artist,
        CancellationToken cancellationToken,
        ReleaseId? release = null)
    {
        var query = db.Releases.AsNoTracking().Where(row => row.Files.Any());

        if (release is { } only) query = query.Where(row => row.Id == only);

        var rows = await query
            .Select(row => new
            {
                row.Id,
                row.Title,
                row.Mbid,
                row.ReleasedYear,
                row.EditsJson,
                SongCount = row.Files.Count,

                // Ordered rather than aggregated: PostgreSQL has comparison
                // operators for uuid but no max() over it, and the query fails
                // with 42883. The ids are UUIDv7, so the largest is the newest.
                Added = row.Files
                    .OrderByDescending(file => file.Id)
                    .Select(file => file.Id)
                    .FirstOrDefault(),

                Created = row.Files.Min(file => file.LastModifiedUtc),

                Credits = row.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        credit.ArtistId,
                        credit.CreditedAs,
                        credit.JoinPhrase,
                        Name = credit.Artist!.LatinName ?? credit.Artist!.Name,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var albums = rows
            .Select(row =>
            {
                var edits = PersonEdits.Read(row.EditsJson);

                var line = CatalogueEndpoints.CreditLine(
                    row.Credits.Select(credit => (credit.CreditedAs ?? credit.Name, credit.JoinPhrase)));

                return new Album(
                    row.Id,
                    PersonEdits.Apply(edits, "title", row.Title) ?? row.Title,
                    PersonEdits.Apply(edits, "credit", line),
                    row.Credits.Count == 0 ? null : row.Credits[0].ArtistId,
                    Year(edits, row.ReleasedYear),
                    row.Mbid,
                    row.SongCount,
                    row.Added.Value,
                    row.Created);
            })
            .ToList();

        return artist is { } billed
            ? albums.Where(album => album.ArtistId == billed).ToList()
            : albums;
    }

    private static int? Year(IReadOnlyDictionary<string, string?> edits, int? stated)
    {
        var edited = PersonEdits.Apply(
            edits,
            "releasedYear",
            stated?.ToString(CultureInfo.InvariantCulture));

        return int.TryParse(edited, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static Task<IReadOnlyList<Song>> SongsAsync(
        FonotecaDbContext db,
        ReleaseId? release,
        CancellationToken cancellationToken,
        MediaFileId? file = null)
    {
        var query = db.MediaFiles.AsNoTracking();

        if (release is { } album) query = query.Where(row => row.ReleaseId == album);
        if (file is { } one) query = query.Where(row => row.Id == one);

        return SongsAsync(query, cancellationToken);
    }

    private static async Task<IReadOnlyList<Song>> SearchSongsAsync(
        FonotecaDbContext db,
        string? pattern,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var query = db.MediaFiles.AsNoTracking();

        if (pattern is not null)
        {
            query = query.Where(row =>
                EF.Functions.ILike(row.Path, pattern, "\\")
                || (row.Recording != null
                    && EF.Functions.ILike(row.Recording.Title, pattern, "\\")));
        }

        var page = query.OrderBy(row => row.Path).Skip(offset).Take(count);

        return await SongsAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Song>> SongsAsync(
        IQueryable<MediaFile> files,
        CancellationToken cancellationToken)
    {
        var rows = await files
            .Select(file => new SongRow(
                file.Id,
                file.Path,
                file.SizeBytes,
                file.LastModifiedUtc,
                file.Quality,
                file.ReleaseId,
                file.Recording != null ? file.Recording.Title : null,
                file.Recording != null ? file.Recording.Duration : null,
                file.Recording != null ? file.Recording.Mbid : null,
                file.Track != null ? file.Track.Title : null,
                file.Track != null ? (int?)file.Track.Position : null,
                file.Track != null ? (int?)file.Track.DiscNumber : null,
                file.Track != null ? file.Track.Length : null,
                file.Release != null ? file.Release.Title : null,
                file.Release != null ? file.Release.ReleasedYear : null,
                file.Release != null ? file.Release.EditsJson : null,
                // No ternary on the navigation: a null one yields an empty
                // collection through the join EF writes, where `new List<>()`
                // is not something it can translate at all.
                file.Release!.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new CreditRow(
                        credit.ArtistId,
                        credit.CreditedAs ?? (credit.Artist!.LatinName ?? credit.Artist!.Name),
                        credit.JoinPhrase))
                    .ToList(),
                file.Recording!.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new CreditRow(
                        credit.ArtistId,
                        credit.CreditedAs ?? (credit.Artist!.LatinName ?? credit.Artist!.Name),
                        credit.JoinPhrase))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .OrderBy(row => row.DiscNumber ?? 1)
            .ThenBy(row => row.Position ?? int.MaxValue)
            .ThenBy(row => row.Path, StringComparer.Ordinal)
            .Select(Describe)
            .ToList();
    }

    /// <summary>
    /// One file, as a song.
    /// </summary>
    /// <remarks>
    /// <b>A duration is taken from whatever measured it, in order:</b> the probe
    /// pass, then the track length MusicBrainz prints, then the recording's.
    /// Only the first is about this file — the other two are about the music —
    /// but a client with no duration has no progress bar and no seeking, and a
    /// printed length is a far better answer than none.
    ///
    /// <b>The media type comes from the same allowlist the file endpoints
    /// use</b>, never from the extension itself, so what a client is told a song
    /// is and what the bytes are served as cannot drift apart.
    /// </remarks>
    private static Song Describe(SongRow row)
    {
        var edits = PersonEdits.Read(row.AlbumEdits);

        var albumLine = CatalogueEndpoints.CreditLine(
            row.AlbumCredits.Select(credit => (credit.Name, credit.JoinPhrase)));

        var ownLine = CatalogueEndpoints.CreditLine(
            row.RecordingCredits.Select(credit => (credit.Name, credit.JoinPhrase)));

        var duration = row.Quality?.Duration ?? row.TrackLength ?? row.RecordingDuration;
        var preview = FilePreview.Of(row.Path);

        return new Song(
            SubsonicIds.Song(row.Id),
            row.TrackTitle ?? row.RecordingTitle ?? System.IO.Path.GetFileNameWithoutExtension(row.Path),
            row.Path,
            row.AlbumTitle is null
                ? null
                : PersonEdits.Apply(edits, "title", row.AlbumTitle) ?? row.AlbumTitle,
            row.ReleaseId,
            ownLine ?? PersonEdits.Apply(edits, "credit", albumLine),
            (row.RecordingCredits.Count > 0 ? row.RecordingCredits : row.AlbumCredits)
                .Select(credit => (ArtistId?)credit.ArtistId)
                .FirstOrDefault(),
            row.Position,
            row.DiscNumber,
            Year(edits, row.AlbumYear),
            row.RecordingMbid,
            row.SizeBytes,
            preview.MediaType,
            System.IO.Path.GetExtension(row.Path).TrimStart('.').ToLowerInvariant(),
            duration is { } measured ? (int)Math.Round(measured.TotalSeconds) : null,
            row.Quality is { } quality ? (int)(quality.BitrateBps / 1000) : null,
            row.Quality?.SampleRateHz,
            row.Quality?.Channels,
            row.Quality?.BitDepth,
            row.LastModifiedUtc);
    }

    /// <summary>One media file and everything around it a song element needs.</summary>
    private sealed record SongRow(
        MediaFileId Id,
        string Path,
        long SizeBytes,
        DateTimeOffset LastModifiedUtc,
        AudioQuality? Quality,
        ReleaseId? ReleaseId,
        string? RecordingTitle,
        TimeSpan? RecordingDuration,
        Mbid? RecordingMbid,
        string? TrackTitle,
        int? Position,
        int? DiscNumber,
        TimeSpan? TrackLength,
        string? AlbumTitle,
        int? AlbumYear,
        string? AlbumEdits,
        List<CreditRow> AlbumCredits,
        List<CreditRow> RecordingCredits);

    /// <summary>One line of a billing, already resolved to what it prints.</summary>
    private sealed record CreditRow(ArtistId ArtistId, string Name, string? JoinPhrase);
}
