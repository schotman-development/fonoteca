namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// The looping video a shop shows in place of an album's sleeve.
/// </summary>
/// <remarks>
/// Apple Music is the one source, and calls it album motion: a square video for
/// a large screen and a tall one for a phone, both silent and made to loop.
/// Null is "no record they sell that is surely this one carries any", which a
/// caller may write down and ask again about later; a
/// <see cref="ProviderException"/> is "I did not get to find out", which it
/// must not — the distinction <c>QobuzCovers</c> draws, for its reason.
/// </remarks>
public interface IAlbumMotions
{
    Task<AlbumMotionFound?> FindAsync(AlbumToFind album, CancellationToken cancellationToken = default);
}

/// <param name="Title">The display edition's title.</param>
/// <param name="Artist">Its billed credit line, or null where nothing is credited.</param>
/// <param name="Credited">Each name on that line.</param>
/// <param name="Year">The display edition's year, which narrows a title match.</param>
/// <param name="Barcode">The display edition's barcode — the key, where there is one.</param>
/// <param name="Editions">The barcodes of the album's other editions.</param>
public sealed record AlbumToFind(
    string Title,
    string? Artist,
    IReadOnlyCollection<string> Credited,
    int? Year,
    string? Barcode,
    IReadOnlyCollection<string> Editions);

/// <param name="AlbumId">The shop's own id for the record the video came from — the provenance.</param>
/// <param name="Storefront">Which country's shop answered.</param>
/// <param name="MatchedBy">How that record was recognised: "barcode", "another edition's barcode" or "title".</param>
/// <param name="Square">The square video, or null where the record has only the tall one.</param>
/// <param name="Tall">The tall video, or null where the record has only the square one.</param>
public sealed record AlbumMotionFound(
    string AlbumId,
    string Storefront,
    string MatchedBy,
    byte[]? Square,
    byte[]? Tall);
