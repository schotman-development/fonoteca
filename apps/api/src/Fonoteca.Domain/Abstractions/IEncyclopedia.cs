using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// What an encyclopedia says about an artist or an album, found by MusicBrainz id.
/// </summary>
/// <remarks>
/// A batch for <see cref="IArtistPortraits"/>'s reason: Wikidata is asked
/// about a few hundred ids in one query, and a seam taking one id would make
/// that impossible to express.
///
/// <b>An id absent from the answer is an answer</b> — no article — and the
/// caller records having asked. A <see cref="ProviderUnavailableException"/>
/// means nothing is known about any id in the batch.
/// </remarks>
public interface IEncyclopedia
{
    /// <summary>Articles about these artists, keyed by MusicBrainz artist id.</summary>
    Task<IReadOnlyDictionary<Mbid, Article>> ArtistsAsync(
        IReadOnlyCollection<Mbid> artists,
        CancellationToken cancellationToken = default);

    /// <summary>Articles about these albums, keyed by MusicBrainz release group id.</summary>
    Task<IReadOnlyDictionary<Mbid, Article>> ReleaseGroupsAsync(
        IReadOnlyCollection<Mbid> groups,
        CancellationToken cancellationToken = default);
}

/// <summary>An article's prose, as plain text, and where it came from.</summary>
/// <param name="Text">
/// Paragraphs separated by a blank line. The whole article rather than its lead
/// — a lead is one sentence on most of the artists a catalogue holds — with the
/// source's lists and appendices dropped and the length clamped by whoever
/// fetched it.
/// </param>
/// <param name="Url">The article, which the page credits beside the text.</param>
public sealed record Article(string Text, Uri Url);
