using System.Globalization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fonoteca.Providers.Qobuz;

/// <summary>
/// <see cref="IReleaseDiscovery"/> over Qobuz's artist catalogue.
/// </summary>
/// <remarks>
/// <b>A shop knows about a record on the day it goes on sale.</b> MusicBrainz
/// learns about it when an editor adds it, which for a new release is routinely
/// days or weeks — and "released after you followed them" is a question about
/// exactly that window. This is the source that can answer it.
///
/// <b>Two requests per artist, and that is the cost to keep in view.</b> A name
/// search to find out who they are, then one call to ask what they released.
/// Both queue at the same gate and draw on the same hourly budget as a download,
/// which is the risk <c>QobuzPortraits</c> already names: a discovery run and a
/// purchase are spending one allowance.
///
/// <b>It answers nothing about audio and claims no authority.</b> ADR 0011 puts
/// a provider's authority at the moment it serves bytes; nothing is served here.
/// A row from this class is a question for a person — "this exists and you have
/// not got it" — and being wrong costs one dismissible tile.
/// </remarks>
public sealed class QobuzReleaseDiscovery(
    QobuzClient client,
    IOptions<QobuzOptions> options,
    ILogger<QobuzReleaseDiscovery> logger) : IReleaseDiscovery
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "Qobuz";

    public async Task<DiscoveredReleases> FindAsync(
        ArtistToPicture artist,
        CancellationToken cancellationToken = default)
    {
        // Unconfigured is an answer, not a failure — the same bargain
        // QobuzPortraits takes. An installation without credentials still runs
        // the pass and simply learns nothing here.
        if (!options.Value.IsConfigured) return DiscoveredReleases.None;

        if (string.IsNullOrWhiteSpace(artist.Name)) return DiscoveredReleases.None;

        var matches = await client
            .SearchArtistsAsync(artist.Name, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (Chosen(artist.Name, matches) is not { Id: { } id } chosen)
        {
            Logging.ProviderLog.QobuzArtistNotFound(logger, artist.Name);
            return DiscoveredReleases.None;
        }

        var found = await client.GetArtistAlbumsAsync(id, cancellationToken).ConfigureAwait(false);

        Logging.ProviderLog.QobuzArtistAlbums(
            logger, chosen.Name, found.Albums.Count, found.Total);

        var releases = new List<DiscoveredRelease>(found.Albums.Count);

        foreach (var album in found.Albums)
        {
            releases.Add(new DiscoveredRelease(
                album.Title,
                YearOf(album.ReleaseDate),
                album.Upc,
                album.Id,
                album.CoverUrl,
                // Qobuz's own count, and zero means they did not state one.
                //
                // `QobuzAlbum.TrackCount` is non-nullable and mapped as
                // `tracks_count ?? tracks.Count` — and these rows carry no track
                // list at all, so an absent count arrives here as 0 rather than
                // as nothing. Passed through, every such record would read as a
                // zero-track single and be dropped by `MinimumTracksToOffer`,
                // which is the exact opposite of that rule's stated bargain that
                // an unknown count is kept because a silence is not a small
                // number. No real album has no tracks, so the collapse is safe.
                album.TrackCount == 0 ? null : album.TrackCount));
        }

        return new DiscoveredReleases(releases, found.Total);
    }

    /// <summary>
    /// Which search result is the artist we meant, with their Qobuz id.
    /// </summary>
    /// <remarks>
    /// <b>The deciding is <see cref="ArtistNameMatch"/>'s, and the id is this
    /// method's problem.</b> That rule answers "which of these rows is them" and
    /// hands back an <see cref="ArtistCandidate"/> — a name, a catalogue size and
    /// a picture — because that is all deciding needs. It does not carry Qobuz's
    /// artist id, so choosing correctly still leaves this class holding a row it
    /// cannot ask anything about.
    ///
    /// Mapping back by name and catalogue size works because the candidates were
    /// built one-for-one from the search results a line above. Where a service
    /// returns two identical rows it picks the first, which is harmless: they
    /// agree on everything the caller goes on to use except an id that resolves
    /// to the same artist either way.
    ///
    /// <b>No picture is required here</b>, unlike the portrait source — that
    /// filter belongs to the caller that needs a photograph, and applying it
    /// would silently lose every artist Qobuz sells records by and holds no press
    /// shot of, which is a large share of exactly the artists worth asking about.
    /// </remarks>
    private QobuzArtist? Chosen(string wanted, IReadOnlyList<QobuzArtist> matches)
    {
        var candidates = matches
            .Select(match => new ArtistCandidate(match.Name, match.AlbumCount, match.ImageUrl))
            .ToList();

        var match = ArtistNameMatch.Pick(wanted, candidates);

        if (match.Chosen is not { } chosen)
        {
            if (match.Rivals > 1)
            {
                Logging.ProviderLog.QobuzArtistAmbiguous(logger, wanted, match.Rivals);
            }

            return null;
        }

        return matches.FirstOrDefault(row =>
            string.Equals(row.Name, chosen.Name, StringComparison.Ordinal)
            && row.AlbumCount == chosen.Catalogue);
    }

    /// <summary>
    /// The year out of whatever the shop calls a release date.
    /// </summary>
    /// <remarks>
    /// Qobuz send <c>release_date_original</c> as <c>YYYY-MM-DD</c>, and only the
    /// year is wanted: <see cref="ReleaseTitleMatch"/> compares years because
    /// that is the precision two catalogues actually agree at, and
    /// <c>ReleaseGroup.FirstReleaseYear</c> is a year for the same reason.
    /// Parsing the whole date and narrowing it would invent a month and a day
    /// this application has a documented rule against inventing.
    ///
    /// Anything unparseable is null rather than a guess — an undated record is a
    /// weaker match, which the rule states, rather than a wrong one.
    /// </remarks>
    private static int? YearOf(string? releaseDate) =>
        releaseDate is { Length: >= 4 }
        && int.TryParse(
            releaseDate.AsSpan(0, 4),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var year)
            ? year
            : null;
}
