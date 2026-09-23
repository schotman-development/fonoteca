using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// A wide photograph of an artist for the head of their page.
/// </summary>
/// <remarks>
/// Not <see cref="IArtistPortraits"/>: a portrait is square and the tile's face,
/// a banner is landscape and only ever blurred behind it. The one source that
/// has these, TheAudioDB, keys them on the MusicBrainz id, so no name travels.
/// An artist absent from the answer is an answer.
/// </remarks>
public interface IArtistBanners
{
    Task<IReadOnlyDictionary<Mbid, Uri>> FindAsync(
        IReadOnlyCollection<Mbid> artists,
        CancellationToken cancellationToken = default);
}
