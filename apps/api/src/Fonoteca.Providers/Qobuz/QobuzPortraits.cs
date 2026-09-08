using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fonoteca.Providers.Qobuz;

/// <summary>
/// <see cref="IArtistPortraits"/> over Qobuz's artist search.
/// </summary>
/// <remarks>
/// <b>The picture a record shop would use.</b> Wikidata's <c>P18</c> is "an
/// image of the subject" and takes whatever Commons happens to hold — measured
/// against this library, AC/DC's is a photograph of the Olympic Stadium in
/// which the band occupies about eight pixels. Qobuz's is a square press
/// portrait, because it is the picture on their own artist page. In a grid of
/// 112px circles that difference is the whole feature.
///
/// <b>It costs a request per artist, where Wikidata costs a dozen for a
/// library</b>, and Qobuz's own budget here is 600 requests an hour. That is
/// what makes this the <i>preferred</i> source rather than the only one:
/// asking about all 2,906 artists in a catalogue would be a five-hour run for
/// pictures of session players nobody browses to, so the caller scopes it to
/// the artists an album is actually billed to — 326 of them, about five
/// minutes — and lets Wikidata answer for the rest.
///
/// <b>Nothing here is authenticated differently or costs anything extra.</b>
/// It rides the same client, the same gate and the same hourly budget as the
/// acquisition calls, which is also the risk worth naming: a portrait run and
/// a download are drawing on one allowance.
/// </remarks>
public sealed class QobuzPortraits(
    QobuzClient client,
    IOptions<QobuzOptions> options,
    ILogger<QobuzPortraits> logger) : IArtistPortraits
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "Qobuz";

    /// <summary>Where a picture may be served from.</summary>
    /// <remarks>
    /// The same reasoning as the Wikidata source's allowlist, against a URL from
    /// the same kind of place: it goes straight into an <c>img src</c> on this
    /// application's origin, and an answer from an unexpected host is a bug
    /// somewhere either way.
    /// </remarks>
    private const string ImageHost = "static.qobuz.com";

    /// <summary>The longest URL <c>Artists.PortraitUrl</c> can hold.</summary>
    private const int MaxUrlLength = 1000;

    public async Task<IReadOnlyDictionary<Mbid, Uri>> FindAsync(
        IReadOnlyCollection<ArtistToPicture> artists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artists);

        var found = new Dictionary<Mbid, Uri>();

        if (!options.Value.IsConfigured) return found;

        foreach (var artist in artists.DistinctBy(entry => entry.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(artist.Name)) continue;

            var matches = await client
                .SearchArtistsAsync(artist.Name, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (Picture(artist.Name, matches) is { } picture) found[artist.Id] = picture;
        }

        return found;
    }

    /// <summary>
    /// The picture of the artist we asked about, if one of these is them.
    /// </summary>
    /// <remarks>
    /// <b>The deciding is <see cref="ArtistNameMatch"/>'s, not this class's.</b>
    /// Deezer is searched by name too and needs the identical rule — exact match
    /// on a folded name, catalogue size breaking a tie only when it plainly
    /// does — and two copies of it is how one of them ends up without the
    /// tie-break. What is left here is the part that is about Qobuz: which field
    /// is the count, and which host may serve a picture.
    ///
    /// Measured, this accepts 34 names in 40. Of the six it refuses, three are
    /// Cyrillic or Japanese spellings that Qobuz index under Latin names and
    /// three are ensembles Qobuz only carries inside a longer credit line. All
    /// six fall through to a source that is keyed on the MusicBrainz id instead,
    /// which is the right outcome for every one of them.
    /// </remarks>
    private Uri? Picture(string wanted, IReadOnlyList<QobuzArtist> matches)
    {
        var candidates = matches
            .Select(match => new ArtistCandidate(match.Name, match.AlbumCount, match.ImageUrl))
            .ToList();

        var match = ArtistNameMatch.Pick(wanted, candidates);

        if (match.Chosen is not { } chosen)
        {
            if (match.Rivals > 1) Logging.ProviderLog.QobuzArtistAmbiguous(logger, wanted, match.Rivals);

            return null;
        }

        var url = chosen.PictureUrl!;

        if (Image(url) is { } picture) return picture;

        Logging.ProviderLog.QobuzPortraitRejected(logger, wanted, url);
        return null;
    }

    /// <summary>A URL this application is willing to put in front of a browser.</summary>
    private static Uri? Image(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.Equals(parsed.Host, ImageHost, StringComparison.OrdinalIgnoreCase)) return null;

        return parsed.AbsoluteUri.Length > MaxUrlLength ? null : parsed;
    }
}
