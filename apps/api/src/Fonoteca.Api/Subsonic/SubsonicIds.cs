using System.Buffers.Text;
using System.Text;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// What this application's identifiers look like on Subsonic's wire.
/// </summary>
/// <remarks>
/// <b>Subsonic ids are opaque strings, which is the one place the protocol's age
/// helps.</b> A typed <c>Guid</c> goes out prefixed because <c>getCoverArt</c>
/// takes a single id that may name an artist, an album or a song and has to be
/// routed to one of three different answers. Without a prefix the endpoint would
/// have to guess, or try all three.
///
/// <b>A folder id carries the path itself, Base64Url-encoded.</b> It is
/// self-describing, so there is no lookup table to keep and no collision to
/// resolve; it is URL-safe, so it survives a query string; and it does not
/// repeat the lesson <c>DomainEvent.SubjectId</c> already paid for, where a
/// library path met a column sized for something else.
///
/// <b>Songs have one id space, not two.</b> A file the catalogue holds is
/// <c>tr-</c> and its media file id in both browsing modes, so the same file
/// browsed by folder and browsed by album is the same song to a client. A file
/// on disk that no scan has reached has no such id and goes out as <c>fo-</c>
/// and its path — which is also what makes it streamable, since it is exactly
/// the argument <c>/api/files/content</c> already takes.
/// </remarks>
internal static class SubsonicIds
{
    private const string ArtistPrefix = "ar-";
    private const string AlbumPrefix = "al-";
    private const string SongPrefix = "tr-";
    private const string PathPrefix = "fo-";

    internal static string Artist(ArtistId id) => ArtistPrefix + id.Value.ToString("N");

    internal static string Album(ReleaseGroupId id) => AlbumPrefix + id.Value.ToString("N");

    internal static string Song(MediaFileId id) => SongPrefix + id.Value.ToString("N");

    /// <summary>A directory, or a file the catalogue does not hold.</summary>
    internal static string Path(string libraryRelativePath) =>
        PathPrefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(libraryRelativePath));

    internal static ArtistId? AsArtist(string? id) =>
        Guid(id, ArtistPrefix) is { } value ? new ArtistId(value) : null;

    internal static ReleaseGroupId? AsAlbum(string? id) =>
        Guid(id, AlbumPrefix) is { } value ? new ReleaseGroupId(value) : null;

    internal static MediaFileId? AsSong(string? id) =>
        Guid(id, SongPrefix) is { } value ? new MediaFileId(value) : null;

    /// <summary>
    /// The library-relative path a folder id carries, or null when the id is not
    /// one — including when it decodes to something that is not UTF-8, which is
    /// a malformed request rather than a path.
    /// </summary>
    internal static string? AsPath(string? id)
    {
        if (id is null || !id.StartsWith(PathPrefix, StringComparison.Ordinal)) return null;

        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(id.AsSpan(PathPrefix.Length)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Guid? Guid(string? id, string prefix)
    {
        if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal)) return null;

        return System.Guid.TryParseExact(id.AsSpan(prefix.Length), "N", out var value)
            ? value
            : null;
    }
}
