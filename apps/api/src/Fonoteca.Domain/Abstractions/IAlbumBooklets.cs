using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// An album's booklet: the Cover Art Archive's scanned pages and the shop's
/// digital booklet.
/// </summary>
/// <remarks>
/// <b>Booklets and nothing else of the packaging.</b> The archive also holds
/// backs, discs, trays, spines and obis; the owner wants one cover, the motion
/// artwork and every booklet (ADR 0015).
///
/// <b>The archive's pages are one edition's, the fullest.</b> The archive files
/// scans per pressing, and an album's editions often split them — one has the
/// front, another the thirteen-page booklet. Every edition is listed and the
/// one with the most booklet pages kept whole, so the pages are one booklet in
/// its own order rather than a mix of two printings.
///
/// An empty answer is "neither source has one", which a caller may write down
/// and ask again about later; a <see cref="ProviderException"/> is "I did not
/// get to find out", which it must not.
/// </remarks>
public interface IAlbumBooklets
{
    /// <param name="album">The album as its display edition describes it, for the shop's search.</param>
    /// <param name="editions">
    /// The album's editions with a MusicBrainz id, preferred first: on a tie in
    /// pages the earlier one wins.
    /// </param>
    Task<AlbumBookletFound> FindAsync(
        AlbumToFind album,
        IReadOnlyList<Mbid> editions,
        CancellationToken cancellationToken = default);
}

/// <param name="Edition">The edition the archive's pages are from; null with none.</param>
/// <param name="Pages">The archive's booklet pages, in the archive's order.</param>
/// <param name="QobuzAlbumId">The shop's album the digital booklets are from — the provenance.</param>
/// <param name="Pdfs">The shop's digital booklets.</param>
public sealed record AlbumBookletFound(
    Mbid? Edition,
    IReadOnlyList<BookletFile> Pages,
    string? QobuzAlbumId,
    IReadOnlyList<BookletFile> Pdfs)
{
    public static AlbumBookletFound None { get; } = new(null, [], null, []);

    public bool IsEmpty => Pages.Count == 0 && Pdfs.Count == 0;
}

/// <param name="SourceId">The archive's image id, or the shop's id for the booklet.</param>
public sealed record BookletFile(string SourceId, byte[] Bytes, string MediaType);
