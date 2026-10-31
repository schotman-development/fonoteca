namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// What a classical catalogue says an album's tracks perform, who plays on
/// them, and what it says about the composers.
/// </summary>
/// <remarks>
/// Apple Music Classical is the one source (ADR 0019), read off its public
/// pages as the motion artwork is. Null is "no record it carries is surely
/// this one", which a caller may write down and ask again about later; a
/// <see cref="ProviderException"/> is "I did not get to find out", which it
/// must not — <see cref="IAlbumMotions"/>' distinction, for its reason.
/// </remarks>
public interface IClassicalAlbums
{
    Task<ClassicalAlbum?> FindAsync(AlbumToFind album, CancellationToken cancellationToken = default);

    /// <summary>The biography on a composer's page, or null where the page has none.</summary>
    /// <param name="composer">The catalogue's id for the composer, as <see cref="ClassicalArtist.Id"/> gives it.</param>
    Task<ClassicalBiography?> BiographyAsync(string composer, CancellationToken cancellationToken = default);
}

/// <param name="AlbumId">The catalogue's own id for the record — the provenance.</param>
/// <param name="Storefront">Which country's shop answered.</param>
/// <param name="MatchedBy">How the record was recognised: "barcode", "another edition's barcode" or "title".</param>
/// <param name="Tracks">Every track the page lists, in its order. A page leaves out tracks it has nothing classical to say about.</param>
public sealed record ClassicalAlbum(
    string AlbumId,
    string Storefront,
    string MatchedBy,
    IReadOnlyList<ClassicalTrack> Tracks);

/// <param name="Disc">From 1.</param>
/// <param name="Number">Its place on the disc, from 1.</param>
/// <param name="Work">The piece it performs, where the page links one; null where it names none.</param>
/// <param name="Performers">Who plays on it, each with every role the album credits them with.</param>
public sealed record ClassicalTrack(
    int Disc,
    int Number,
    TimeSpan Length,
    ClassicalWork? Work,
    IReadOnlyList<ClassicalPerformer> Performers);

/// <param name="Id">The catalogue's id for the piece: <c>ludwig-van-beethoven-1770-pp27</c>.</param>
/// <param name="Title">As the catalogue prints it, catalogue number and nickname included.</param>
public sealed record ClassicalWork(string Id, string Title, IReadOnlyList<ClassicalArtist> Composers);

public sealed record ClassicalArtist(string Id, string Name);

/// <param name="Roles">As the album credits them — "Conductor", "Orchestra", "Soprano", "Piano"; empty where it credits none.</param>
public sealed record ClassicalPerformer(string Name, IReadOnlyList<string> Roles);

/// <param name="Text">Plain text, paragraphs apart by a blank line.</param>
/// <param name="Url">The page it was read from, which credits it.</param>
public sealed record ClassicalBiography(string Text, Uri Url);
