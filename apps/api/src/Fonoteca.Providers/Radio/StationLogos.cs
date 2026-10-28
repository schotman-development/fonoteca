using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Logging;
using Fonoteca.Providers.RadioBrowser;
using Fonoteca.Providers.Wikidata;
using Microsoft.Extensions.Logging;

namespace Fonoteca.Providers.Radio;

/// <summary>
/// <see cref="IStationLogos"/>: the station's home page, Wikidata and
/// radio-browser.info, asked in turn until one has a logo.
/// </summary>
/// <remarks>
/// <b>In this order, because a tile wants a tile's picture.</b> A large icon
/// the home page names (<see cref="PageIconList.Large"/>) is drawn by the
/// station to be one; Wikidata's logo is the official one, often a wordmark;
/// radio-browser's is whatever whoever added the station gave, often a
/// favicon. What the page has left — its share picture, its small icons, then
/// a <c>/favicon.ico</c> it answers for — comes last, since a 16-pixel icon is
/// the last thing a tile should show.
///
/// A source that could not be asked is logged and passed over; the station is
/// saved with whatever the others had, or with no logo.
/// </remarks>
public sealed class StationLogos(
    StationHomepages homepages,
    WikidataStationLogos wikidata,
    RadioBrowserLogos radioBrowser,
    ILogger<StationLogos> logger) : IStationLogos
{
    public async Task<string?> FindAsync(RadioStation station, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);

        var home = station.HomePageUrl;

        var (icons, page) = home is null
            ? (PageIconList.None, null)
            : await AskAsync(StationHomepages.ProviderName, station, () => homepages.IconsAsync(home, cancellationToken), (PageIconList.None, null))
                .ConfigureAwait(false);

        if (icons.Large is [var large, ..]) return large;

        if (await AskAsync(WikidataPortraits.ProviderName, station, () => wikidata.FindAsync(station.StreamUrl, home, cancellationToken), null)
                .ConfigureAwait(false) is { } logo)
        {
            return logo;
        }

        if (await AskAsync(RadioBrowserLogos.ProviderName, station, () => radioBrowser.FindAsync(station.Name, station.StreamUrl, cancellationToken), null)
                .ConfigureAwait(false) is { } favicon)
        {
            return favicon;
        }

        if (icons.Small is [var small, ..]) return small;

        if (page is null) return null;

        var guessed = new Uri(page, "/favicon.ico").AbsoluteUri;

        return await AskAsync(StationHomepages.ProviderName, station, () => homepages.IsImageAsync(guessed, cancellationToken), false).ConfigureAwait(false)
            ? guessed
            : null;
    }

    private async Task<T> AskAsync<T>(string source, RadioStation station, Func<Task<T>> ask, T fallback)
    {
        try
        {
            return await ask().ConfigureAwait(false);
        }
        catch (ProviderException cause)
        {
            ProviderLog.StationLogoSourceFailed(logger, source, station.Name, cause.Message);
            return fallback;
        }
    }
}
