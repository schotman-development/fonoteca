using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// A picture of the artist, rather than of one of their records.
/// </summary>
/// <remarks>
/// <b>MusicBrainz has no artist images and the Cover Art Archive is keyed on
/// releases</b>, which is why every artist tile in this application has until
/// now shown an album — right about the artist, and not what somebody scanning
/// a page of names is looking at. Wikidata holds one, under <c>P18</c>, and
/// indexes its items by MusicBrainz artist id under <c>P434</c>, so the question
/// "what does this MBID look like" is answerable in one hop without MusicBrainz
/// being involved at all.
///
/// <b>The unit is a batch, and that is the whole reason this is not a method on
/// <see cref="IMusicBrainzCatalogue"/>.</b> Every other provider call here
/// answers about one thing and queues behind a gate that permits one request a
/// second; a library's artists asked one at a time is 2,902 turns and the better
/// part of an hour. Asked as a set, the same question is a dozen requests —
/// measured, 307 artists in one 13-second query — because the query language on
/// the other end takes a list. A seam that took one id would make that
/// impossible to express.
///
/// <b>An implementation may still cost a request each, and one does.</b> Qobuz
/// has no bulk form and is searched by name, so its batch is a loop. The seam
/// is the batch either way because that is what lets the caller hand over a
/// worklist and be told what came back, rather than owning the difference.
///
/// <b>Both the id and the name travel, because the two sources are keyed
/// differently.</b> Wikidata holds the MusicBrainz id and is asked with it;
/// Qobuz has never heard of MusicBrainz and is asked with a name, which is why
/// its matching is a decision and not a lookup. Passing only the id would make
/// the second source impossible; passing only the name would make the first one
/// guess.
///
/// The answer is a URL rather than a filename, an image or a byte array: the
/// browser fetches it directly from the provider's CDN exactly as it already
/// fetches sleeves from the Cover Art Archive, so nothing here proxies, caches
/// or resizes an image.
/// </remarks>
public interface IArtistPortraits
{
    /// <summary>
    /// Where a picture of each of these artists can be found, for those that
    /// have one.
    /// </summary>
    /// <param name="artists">The artists to look for. Duplicates are harmless.</param>
    /// <returns>
    /// Only the artists a picture was found for. <b>An artist absent from the
    /// result is an answer</b> — "nobody has uploaded a photograph of this
    /// orchestra" — and the caller is expected to record having asked, or every
    /// artist without one is re-asked about on every run forever.
    /// </returns>
    /// <exception cref="ProviderUnavailableException">
    /// The service could not be reached, or refused. Nothing is known about any
    /// artist in the batch, which is different from finding none: the caller
    /// must leave them all on the worklist.
    /// </exception>
    Task<IReadOnlyDictionary<Mbid, Uri>> FindAsync(
        IReadOnlyCollection<ArtistToPicture> artists,
        CancellationToken cancellationToken = default);
}

/// <summary>An artist to find a picture of, by whichever key a source uses.</summary>
/// <param name="Id">The MusicBrainz id, which is also the result's key.</param>
/// <param name="Name">
/// The catalogue's name for them. A search term rather than an identifier — and
/// the reason a source keyed on it has to decide rather than look up.
/// </param>
public readonly record struct ArtistToPicture(Mbid Id, string Name);
