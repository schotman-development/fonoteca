using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Browsing what the catalogue knows: artists, albums and the songs on them.
/// </summary>
/// <remarks>
/// The id3 half of the protocol. An album is a <see cref="ReleaseGroup"/>, as
/// everywhere else here: a pressing is claimed only on proof, so most albums
/// have none, and a song's numbers are the pressing's where one is claimed and
/// the file's own tags otherwise.
///
/// <b>Only albums the library actually holds files for appear.</b> An album
/// the catalogue knows about but owns nothing of is a gap on the Acquire screen,
/// not an album a client should offer to play.
///
/// <b>The album's own answers are what the web client shows.</b> The billing
/// line is <c>CreditLine</c> over the credits in position order, and a person's
/// corrections are applied through <c>PersonEdits</c> — so an album somebody
/// retitled by hand is retitled here too, rather than reverting to what
/// MusicBrainz said on a phone.
/// </remarks>
public static partial class SubsonicEndpoints
{
    /// <summary>How many albums a list answers with when the client does not say.</summary>
    private const int DefaultListSize = 10;

    /// <summary>The protocol's own ceiling on a list.</summary>
    private const int MaxListSize = 500;

    /// <summary>
    /// What an alphabetical index ignores at the front of a name.
    /// </summary>
    /// <remarks>
    /// Advertised rather than applied: the catalogue already holds
    /// <c>Artist.SortName</c>, which is MusicBrainz's own answer — "Beatles,
    /// The" — and is a better one than any article list. This is on the wire so
    /// a client knows not to apply its own.
    /// </remarks>
    private const string IgnoredArticles = "The El La Los Las Le Les";

    private static async Task<IResult> GetArtists(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var albums = await AlbumsAsync(db, artist: null, cancellationToken).ConfigureAwait(false);
        var artists = await ArtistsOfAsync(db, albums, cancellationToken).ConfigureAwait(false);

        var indexed = artists
            .GroupBy(artist => Initial(artist.SortName ?? artist.Name))
            .OrderBy(bucket => bucket.Key, StringComparer.Ordinal)
            .ToList();

        return SubsonicResult.Ok("artists", root =>
        {
            root.Attr("ignoredArticles", IgnoredArticles);

            root.Children("index", indexed, (index, bucket) =>
            {
                index.Attr("name", bucket.Key);
                index.Children("artist", bucket, WriteArtist);
            });
        });
    }

    private static async Task<IResult> GetArtist(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsArtist(Text(http, "id")) is not { } artistId)
        {
            return SubsonicResult.Error(70, "No such artist.");
        }

        // One pass over the albums, not two: the artists are derived from the
        // same list this endpoint then filters, and that list is the expensive
        // query on this surface.
        var everything = await AlbumsAsync(db, artist: null, cancellationToken).ConfigureAwait(false);
        var artists = await ArtistsOfAsync(db, everything, cancellationToken).ConfigureAwait(false);

        var artist = artists.FirstOrDefault(row => row.Id == artistId);

        if (artist is null) return SubsonicResult.Error(70, "No such artist.");

        var albums = everything.Where(album => album.ArtistId == artistId).ToList();

        return SubsonicResult.Ok("artist", root =>
        {
            WriteArtist(root, artist);
            root.Children("album", albums, WriteAlbum);
        });
    }

    private static async Task<IResult> GetAlbum(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsAlbum(Text(http, "id")) is not { } albumId)
        {
            return SubsonicResult.Error(70, "No such album.");
        }

        var albums = await AlbumsAsync(db, artist: null, cancellationToken, albumId)
            .ConfigureAwait(false);

        if (albums.Count == 0) return SubsonicResult.Error(70, "No such album.");

        var album = albums[0];
        var songs = await SongsAsync(db, albumId, cancellationToken).ConfigureAwait(false);

        return SubsonicResult.Ok("album", root =>
        {
            WriteAlbum(root, album);
            root.Children("song", songs, WriteSong);
        });
    }

    /// <summary>
    /// A shelf of albums.
    /// </summary>
    /// <remarks>
    /// <b>Three of the protocol's list types come back empty on purpose.</b>
    /// <c>frequent</c>, <c>recent</c> and <c>starred</c> are questions about
    /// listening, and nothing here records a play or a star — so an album list
    /// is the wrong answer to all three. Empty says "nothing"; a list of albums
    /// would say "these are your most played", which would be an invention.
    /// </remarks>
    private static async Task<IResult> GetAlbumList2(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var type = Text(http, "type") ?? "alphabeticalByName";
        var size = Number(http, "size", DefaultListSize, MaxListSize);
        var offset = Number(http, "offset", 0, int.MaxValue);

        if (type is "frequent" or "recent" or "starred")
        {
            return SubsonicResult.Ok(
                "albumList2",
                list => list.Children("album", Array.Empty<Album>(), WriteAlbum));
        }

        var albums = await AlbumsAsync(db, artist: null, cancellationToken).ConfigureAwait(false);

        IEnumerable<Album> ordered = type switch
        {
            "alphabeticalByArtist" => albums
                .OrderBy(album => album.Artist ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(album => album.Name, StringComparer.OrdinalIgnoreCase),

            // Newest by when the library gained it, which is what the album list
            // sorts "added" by: the media file ids are UUIDv7 and time-ordered,
            // so the largest one under an album is when its first file arrived.
            "newest" => albums.OrderByDescending(album => album.Added),

            "byYear" => ByYear(http, albums),

            "random" => albums.OrderBy(_ => Random.Shared.Next()),

            _ => albums.OrderBy(album => album.Name, StringComparer.OrdinalIgnoreCase),
        };

        var page = ordered.Skip(offset).Take(size).ToList();

        return SubsonicResult.Ok(
            "albumList2",
            list => list.Children("album", page, WriteAlbum));
    }

    private static async Task<IResult> GetSong(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsSong(Text(http, "id")) is not { } fileId)
        {
            return SubsonicResult.Error(70, "No such song.");
        }

        var songs = await SongsAsync(db, album: null, cancellationToken, fileId)
            .ConfigureAwait(false);

        return songs.Count == 0
            ? SubsonicResult.Error(70, "No such song.")
            : SubsonicResult.Ok("song", song => WriteSong(song, songs[0]));
    }

    /// <summary>
    /// Search, over the catalogue.
    /// </summary>
    /// <remarks>
    /// The note elsewhere that search needs a Solr index is about searching
    /// <i>MusicBrainz</i>, behind the by-hand album screen; searching what is
    /// already here owes a mirror nothing.
    ///
    /// <b>Songs are matched in SQL and artists and albums in memory</b>, which
    /// is not an oversight but is worth knowing: the files are the many, so they
    /// go through <c>ILIKE</c> and the trigram indexes, while the album list is
    /// already loaded whole for every other shelf on this surface and filtering
    /// it again costs nothing. The day the album list stops being loaded whole,
    /// this is the second place to change.
    ///
    /// An empty query means everything, because several clients open their
    /// search screen by asking for it.
    /// </remarks>
    private static async Task<IResult> Search3(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var query = Text(http, "query")?.Trim();
        var wanted = query is null or "" or "\"\"" ? null : $"%{Escape(query)}%";

        var artistCount = Number(http, "artistCount", 20, MaxListSize);
        var albumCount = Number(http, "albumCount", 20, MaxListSize);
        var songCount = Number(http, "songCount", 20, MaxListSize);

        var albums = await AlbumsAsync(db, artist: null, cancellationToken).ConfigureAwait(false);
        var artists = await ArtistsOfAsync(db, albums, cancellationToken).ConfigureAwait(false);

        var matchingArtists = artists
            .Where(artist => wanted is null || Matches(artist.Name, query))
            .Skip(Number(http, "artistOffset", 0, int.MaxValue))
            .Take(artistCount)
            .ToList();

        var matchingAlbums = albums
            .Where(album => wanted is null
                || Matches(album.Name, query)
                || Matches(album.Artist, query))
            .Skip(Number(http, "albumOffset", 0, int.MaxValue))
            .Take(albumCount)
            .ToList();

        var songs = await SearchSongsAsync(
                db,
                wanted,
                Number(http, "songOffset", 0, int.MaxValue),
                songCount,
                cancellationToken)
            .ConfigureAwait(false);

        return SubsonicResult.Ok("searchResult3", result =>
        {
            result.Children("artist", matchingArtists, WriteArtist);
            result.Children("album", matchingAlbums, WriteAlbum);
            result.Children("song", songs, WriteSong);
        });
    }

    private static IEnumerable<Album> ByYear(HttpContext http, IReadOnlyList<Album> albums)
    {
        var from = Number(http, "fromYear", 0, int.MaxValue);
        var to = Number(http, "toYear", int.MaxValue, int.MaxValue);

        // The protocol allows the range backwards, and means reverse order by it.
        var (low, high) = from <= to ? (from, to) : (to, from);

        var within = albums.Where(album => album.Year is { } year && year >= low && year <= high);

        return from <= to
            ? within.OrderBy(album => album.Year)
            : within.OrderByDescending(album => album.Year);
    }

    private static bool Matches(string? value, string? query) =>
        value is not null
        && query is not null
        && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first letter a name files under.
    /// </summary>
    /// <remarks>
    /// Anything that is not a letter goes under <c>#</c>, which is where every
    /// client's index puts numerals and every alphabet it does not have a row
    /// for. The sort name is used where there is one, so "The Beatles" files
    /// under B because MusicBrainz says "Beatles, The" — not because of an
    /// article list.
    /// </remarks>
    private static string Initial(string name)
    {
        var trimmed = name.TrimStart();

        return trimmed.Length > 0 && char.IsLetter(trimmed[0])
            ? char.ToUpperInvariant(trimmed[0]).ToString()
            : "#";
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static void WriteArtist(ISubsonicWriter writer, SubsonicArtist artist)
    {
        writer.Attr("id", SubsonicIds.Artist(artist.Id));
        writer.Attr("name", artist.Name);
        writer.Attr("albumCount", artist.AlbumCount);
        writer.Attr("sortName", artist.SortName);
        writer.Attr("musicBrainzId", artist.Mbid?.Value.ToString());

        // A full URL on somebody else's CDN, which is how it is stored — four
        // services answer here and only they know how their addresses are
        // shaped. Nothing is proxied; a client that cannot reach the CDN draws
        // its own monogram, exactly as the web client does.
        writer.Attr("artistImageUrl", artist.Portrait);
    }

    private static void WriteAlbum(ISubsonicWriter writer, Album album)
    {
        writer.Attr("id", SubsonicIds.Album(album.Id));
        writer.Attr("name", album.Name);

        // `album` and `title` beside `name`, because the three generations of
        // this protocol disagree about which one an album's title lives in and
        // clients read whichever they were written against.
        writer.Attr("album", album.Name);
        writer.Attr("title", album.Name);
        writer.Attr("artist", album.Artist);
        writer.Attr("artistId", album.ArtistId is { } id ? SubsonicIds.Artist(id) : null);
        writer.Attr("coverArt", SubsonicIds.Album(album.Id));
        writer.Attr("songCount", album.SongCount);
        writer.Attr("created", album.Created);
        writer.Attr("year", album.Year);
        writer.Attr("musicBrainzId", album.Mbid?.Value.ToString());
        writer.Attr("isDir", true);
    }

    private static void WriteSong(ISubsonicWriter writer, Song song) =>
        WriteSong(writer, song, parent: null);

    /// <param name="parent">
    /// What a client walks up to. Null means the album, which is right when the
    /// song was reached through one; folder browsing passes the directory it
    /// listed instead.
    /// </param>
    private static void WriteSong(ISubsonicWriter writer, Song song, string? parent)
    {
        writer.Attr("id", song.Id);
        writer.Attr(
            "parent",
            parent ?? (song.AlbumId is { } album ? SubsonicIds.Album(album) : null));
        writer.Attr("isDir", false);
        writer.Attr("title", song.Title);
        writer.Attr("album", song.Album);
        writer.Attr("artist", song.Artist);
        writer.Attr("track", song.Track);
        writer.Attr("discNumber", song.DiscNumber);
        writer.Attr("year", song.Year);
        writer.Attr("coverArt", song.AlbumId is { } cover ? SubsonicIds.Album(cover) : song.Id);
        writer.Attr("size", song.SizeBytes);
        writer.Attr("contentType", song.ContentType);
        writer.Attr("suffix", song.Suffix);
        writer.Attr("duration", song.DurationSeconds);
        writer.Attr("bitRate", song.BitRateKbps);
        writer.Attr("samplingRate", song.SampleRateHz);
        writer.Attr("channelCount", song.Channels);
        writer.Attr("bitDepth", song.BitDepth);
        writer.Attr("path", song.Path);
        writer.Attr("albumId", song.AlbumId is { } owner ? SubsonicIds.Album(owner) : null);
        writer.Attr("artistId", song.ArtistId is { } billed ? SubsonicIds.Artist(billed) : null);
        writer.Attr("created", song.Created);
        writer.Attr("type", "music");
        writer.Attr("isVideo", false);
        writer.Attr("musicBrainzId", song.Mbid?.Value.ToString());
    }
}

/// <summary>An artist, as much of one as a client draws.</summary>
internal sealed record SubsonicArtist(
    ArtistId Id,
    string Name,
    string? SortName,
    Mbid? Mbid,
    string? Portrait,
    int AlbumCount);

/// <summary>A release group, as an album.</summary>
/// <param name="Added">
/// When the library gained its first file, taken from the media file ids rather
/// than from a column: they are UUIDv7 and time-ordered, which is the same thing
/// the album list already sorts "added" by.
/// </param>
internal sealed record Album(
    ReleaseGroupId Id,
    string Name,
    string? Artist,
    ArtistId? ArtistId,
    int? Year,
    Mbid? Mbid,
    int SongCount,
    Guid Added,
    DateTimeOffset? Created);

/// <summary>A media file, as a song.</summary>
/// <param name="Id">Already in its wire form: a catalogued file is <c>tr-</c>, anything else <c>fo-</c>.</param>
internal sealed record Song(
    string Id,
    string Title,
    string Path,
    string? Album,
    ReleaseGroupId? AlbumId,
    string? Artist,
    ArtistId? ArtistId,
    int? Track,
    int? DiscNumber,
    int? Year,
    Mbid? Mbid,
    long SizeBytes,
    string ContentType,
    string Suffix,
    int? DurationSeconds,
    int? BitRateKbps,
    int? SampleRateHz,
    int? Channels,
    int? BitDepth,
    DateTimeOffset? Created);
