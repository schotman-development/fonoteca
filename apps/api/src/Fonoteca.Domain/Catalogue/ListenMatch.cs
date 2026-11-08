using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Which held song a ListenBrainz listen was a play of (client issue #67).
/// </summary>
/// <remarks>
/// <b>By the recording MBID first, then by title and artist</b>, the owner's
/// order, each folded as <see cref="ArtistNameMatch.Normalise"/> folds a name.
/// <b>A name naming two recordings is no answer</b> — a studio take and a live
/// one share a title — unless the listen's album names exactly one of them: a
/// play counted against the wrong song is worse than one not counted.
///
/// Of the copies of the recording, the one on the listen's album where the
/// library holds it there, else the first held, so the play also counts on the
/// album it was heard from.
/// </remarks>
public sealed class ListenMatch
{
    private readonly ILookup<Mbid, HeldSong> _byMbid;
    private readonly ILookup<(string Title, string Artist), HeldSong> _byName;

    public ListenMatch(IEnumerable<HeldSong> held)
    {
        ArgumentNullException.ThrowIfNull(held);

        var songs = held.OrderBy(song => song.File.Value).ToList();

        _byMbid = songs.Where(song => song.Mbid is not null).ToLookup(song => song.Mbid!.Value);
        _byName = songs.ToLookup(song => (ArtistNameMatch.Normalise(song.Title), ArtistNameMatch.Normalise(song.Artist)));
    }

    /// <summary>The copy this listen was a play of, or null where nothing held answers it alone.</summary>
    public HeldSong? Of(HeardListen listen)
    {
        ArgumentNullException.ThrowIfNull(listen);

        if (listen.Recording is { } mbid && _byMbid[mbid].ToList() is { Count: > 0 } identified)
        {
            return OnItsAlbum(identified, listen.Release) ?? identified[0];
        }

        var named = _byName[(ArtistNameMatch.Normalise(listen.Track), ArtistNameMatch.Normalise(listen.Artist))].ToList();

        if (named.Count == 0) return null;

        if (named.Select(song => song.Recording).Distinct().Count() == 1)
        {
            return OnItsAlbum(named, listen.Release) ?? named[0];
        }

        var onAlbum = named.Where(song => SameAlbum(song, listen.Release)).ToList();

        return onAlbum.Select(song => song.Recording).Distinct().Count() == 1 ? onAlbum[0] : null;
    }

    private static HeldSong? OnItsAlbum(IEnumerable<HeldSong> copies, string? release) =>
        copies.FirstOrDefault(song => SameAlbum(song, release));

    private static bool SameAlbum(HeldSong song, string? release) =>
        release is not null
        && song.Album is not null
        && ArtistNameMatch.Normalise(song.Album) == ArtistNameMatch.Normalise(release);
}

/// <summary>One held file, as a listen is matched against it.</summary>
/// <param name="Artist">Its recording's billing line, as printed.</param>
public sealed record HeldSong(
    MediaFileId File,
    RecordingId Recording,
    ReleaseGroupId? AlbumId,
    Mbid? Mbid,
    string Title,
    string Artist,
    string? Album);
