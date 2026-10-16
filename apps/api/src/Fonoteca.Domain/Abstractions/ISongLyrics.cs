namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// A song's words, timed line by line where the source has them so.
/// </summary>
/// <remarks>
/// LRCLIB is the one source (ADR 0016). <b>It is keyed on names, not on an
/// identifier</b>: a title, an artist and a length within two seconds, the album
/// narrowing it only where it is sent. The length is what keeps a live take
/// from answering for the studio one.
///
/// Null is "the source holds no such song", which a caller may write down and
/// ask again about later; a <see cref="ProviderException"/> is "I did not get to
/// find out", which it must not — <c>QobuzCovers</c>' distinction, for its
/// reason.
/// </remarks>
public interface ISongLyrics
{
    Task<SongLyricsFound?> FindAsync(SongToFind song, CancellationToken cancellationToken = default);
}

/// <param name="Title">The recording's title.</param>
/// <param name="Artist">Its billed credit line.</param>
/// <param name="FirstArtist">The first name on that line, where it bills more than one.</param>
/// <param name="Album">The album a held file is filed under, which narrows the first ask.</param>
/// <param name="Duration">The held audio's length, which every ask is held to.</param>
public sealed record SongToFind(
    string Title,
    string Artist,
    string? FirstArtist,
    string? Album,
    TimeSpan Duration);

/// <param name="Id">The source's own id for the track that answered — the provenance.</param>
/// <param name="Synced">The words as LRC, a time before each line, or null.</param>
/// <param name="Plain">The words without times, or null.</param>
/// <param name="Instrumental">The source says the song has no words.</param>
/// <param name="MatchedBy">"album", "any album", or "first artist": how far the ask had to widen.</param>
public sealed record SongLyricsFound(
    long Id,
    string? Synced,
    string? Plain,
    bool Instrumental,
    string MatchedBy);
