namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// A shop's genre for an album, asked where MusicBrainz has none.
/// </summary>
/// <remarks>
/// Null is "no record they sell that is surely this one has a genre that says
/// anything", which a caller may write down and ask again about later; a
/// <see cref="ProviderException"/> is "I did not get to find out", which it
/// must not — <see cref="IAlbumMotions"/>' distinction, for its reason.
/// </remarks>
public interface IAlbumGenres
{
    Task<AlbumGenresFound?> FindAsync(AlbumToFind album, CancellationToken cancellationToken = default);
}

/// <param name="Genres">The genres, stated as MusicBrainz states them (<c>ShopGenres</c>).</param>
/// <param name="Source">The shop that answered.</param>
/// <param name="MatchedBy">How its record was recognised: "barcode", "another edition's barcode" or "title".</param>
public sealed record AlbumGenresFound(IReadOnlyList<string> Genres, string Source, string MatchedBy);
