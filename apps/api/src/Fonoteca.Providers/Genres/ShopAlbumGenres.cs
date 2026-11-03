using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.AppleMusic;
using Fonoteca.Providers.Logging;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fonoteca.Providers.Genres;

/// <summary>
/// <see cref="IAlbumGenres"/> over Apple Music, and over Qobuz where Apple has nothing.
/// </summary>
/// <remarks>
/// <b>The order is the owner's</b> (2026-11-03), on a measurement of the 119
/// albums here MusicBrainz held no genre for: Apple answered 98, in English
/// and finer-grained; Qobuz 100, coarser, filing crossover, film and pop
/// records as classical. Qobuz answers the few Apple cannot.
///
/// <b>Each recognises the record as the sleeves and the motion art do</b> —
/// <see cref="AppleMusicRecords"/> and <see cref="QobuzCovers.Match"/> — so
/// the three cannot come to disagree about which record an album is. A record
/// recognised whose genre states nothing (<see cref="ShopGenres"/>) is no
/// answer from that shop.
/// </remarks>
public sealed class ShopAlbumGenres(
    AppleMusicRecords apple,
    QobuzClient qobuz,
    IOptions<QobuzOptions> qobuzOptions,
    ILogger<ShopAlbumGenres> logger) : IAlbumGenres
{
    /// <summary>Qobuz search results considered: <see cref="QobuzCovers"/>' ten, for its reason.</summary>
    private const int SearchLimit = 10;

    public async Task<AlbumGenresFound?> FindAsync(AlbumToFind album, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);

        var found = await AppleMusicAsync(album, cancellationToken).ConfigureAwait(false)
            ?? await QobuzAsync(album, cancellationToken).ConfigureAwait(false);

        if (found is not null) ProviderLog.AlbumGenresFound(logger, found.Source, album.Artist, album.Title, found.MatchedBy);

        return found;
    }

    private async Task<AlbumGenresFound?> AppleMusicAsync(AlbumToFind album, CancellationToken cancellationToken)
    {
        var read = await apple.FindAsync(album, AppleGenresAsync, cancellationToken).ConfigureAwait(false);

        return read is null ? null : new AlbumGenresFound(read.Value, AppleMusicRecords.ProviderName, read.MatchedBy);
    }

    /// <summary>The genres one of Apple's records states, or null where it states none.</summary>
    private async Task<IReadOnlyList<string>?> AppleGenresAsync(string storefront, long album, CancellationToken cancellationToken)
    {
        var rows = await apple
            .ResultsAsync($"lookup?id={album}&country={Uri.EscapeDataString(storefront)}", cancellationToken)
            .ConfigureAwait(false);

        var genres = ShopGenres.FromAppleMusic(rows.FirstOrDefault(row => row.CollectionId == album)?.PrimaryGenreName);

        return genres.Count > 0 ? genres : null;
    }

    private async Task<AlbumGenresFound?> QobuzAsync(AlbumToFind album, CancellationToken cancellationToken)
    {
        if (!qobuzOptions.Value.IsConfigured) return null;

        var albums = await qobuz
            .SearchAlbumsAsync(
                string.IsNullOrWhiteSpace(album.Artist) ? album.Title : $"{album.Artist} {album.Title}",
                SearchLimit,
                cancellationToken)
            .ConfigureAwait(false);

        var match = QobuzCovers.Match(
            albums, album.Title, album.Artist, album.Credited, album.Year, album.Barcode, album.Editions, out var how);

        var genres = ShopGenres.FromQobuz(match?.GenrePath ?? []);

        return genres.Count > 0 ? new AlbumGenresFound(genres, QobuzClient.ProviderName, how) : null;
    }
}
