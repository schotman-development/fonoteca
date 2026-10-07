using System.Globalization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Logging;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fonoteca.Providers.CoverArt;

/// <summary><see cref="IAlbumBooklets"/> over the Cover Art Archive and Qobuz.</summary>
/// <remarks>
/// <b>The archive is keyed on each edition's MusicBrainz id</b> and cannot be
/// wrong about which record a page belongs to. <b>The shop is reached by
/// <see cref="QobuzCovers.Match"/></b>, the cover fallback's rule — a barcode,
/// else a title and billing it is reluctant about — because a booklet is a
/// sleeve's sibling and a wrong one costs what a wrong sleeve costs.
///
/// <b>Both are asked for every album</b>, since "every booklet" is the ask: a
/// CD's scanned booklet and the shop's digital one are different documents.
/// </remarks>
public sealed class AlbumBooklets(
    ICoverArtArchive archive,
    QobuzClient qobuz,
    IOptions<QobuzOptions> qobuzOptions,
    ILogger<AlbumBooklets> logger) : IAlbumBooklets
{
    /// <summary>The largest file kept. The biggest booklet scan measured here was 14 MB.</summary>
    public const long MaxFileBytes = 100L * 1024 * 1024;

    /// <summary>How long a digital booklet may take; they measured about 4 MB.</summary>
    private static readonly TimeSpan PdfTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Qobuz's <c>file_format_id</c> for a PDF.</summary>
    private const int PdfFormat = 21;

    /// <summary>The only host a digital booklet may be fetched from.</summary>
    private const string GoodiesHost = "static.qobuz.com";

    /// <summary>How many search results to consider: <see cref="QobuzCovers"/>' ten, for its reason.</summary>
    private const int SearchLimit = 10;

    public async Task<AlbumBookletFound> FindAsync(
        AlbumToFind album,
        IReadOnlyList<Mbid> editions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(editions);

        var (edition, pages) = await PagesAsync(editions, cancellationToken).ConfigureAwait(false);
        var (shop, pdfs) = await PdfsAsync(album, cancellationToken).ConfigureAwait(false);

        if (pages.Count > 0 || pdfs.Count > 0)
        {
            ProviderLog.BookletFound(
                logger, album.Artist, album.Title, pages.Count, edition, pdfs.Count, shop);
        }

        return new AlbumBookletFound(edition, pages, shop, pdfs);
    }

    /// <summary>The booklet pages of one edition: its edited scans, else its raw ones.</summary>
    /// <remarks>
    /// An image typed both <c>Booklet</c> and <c>Front</c> is the booklet's own
    /// cover, and a page of it. A raw scan is the same page unedited, uploaded
    /// beside the edited one, so it counts only where it is all there is.
    /// </remarks>
    public static IReadOnlyList<CoverArtImage> Booklet(IReadOnlyList<CoverArtImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        var booklet = images
            .Where(image => image.Types.Contains("Booklet", StringComparer.OrdinalIgnoreCase))
            .ToList();

        var edited = booklet
            .Where(image => !image.Types.Contains("Raw/Unedited", StringComparer.OrdinalIgnoreCase))
            .ToList();

        return edited.Count > 0 ? edited : booklet;
    }

    /// <remarks>
    /// <b>One edition the archive cannot give is left out, not an outage.</b>
    /// The archive keeps each release on an archive.org storage node, and one
    /// node answering 500 for one release, every time, was measured here
    /// (<i>Live at the Regal</i>): read as the archive being down, it ended the
    /// stage at that album on every run. So the archive is down only when not
    /// one edition of the album could be listed, or when every edition that
    /// had pages failed to give them; then this throws and nothing is stamped.
    /// </remarks>
    private async Task<(Mbid? Edition, List<BookletFile> Pages)> PagesAsync(
        IReadOnlyList<Mbid> editions,
        CancellationToken cancellationToken)
    {
        List<(Mbid Edition, IReadOnlyList<CoverArtImage> Pages)> listed = [];
        ProviderUnavailableException? unavailable = null;

        foreach (var edition in editions)
        {
            try
            {
                listed.Add((edition, Booklet(await archive.ListAsync(edition, cancellationToken).ConfigureAwait(false))));
            }
            catch (ProviderUnavailableException cause)
            {
                unavailable = cause;
                ProviderLog.BookletEditionUnavailable(logger, edition, cause.Message);
            }
        }

        if (listed.Count == 0 && unavailable is not null) throw unavailable;

        unavailable = null;

        // Fullest first. OrderByDescending is stable, so a tie keeps the
        // caller's order and the cover's edition wins it.
        foreach (var (edition, pages) in listed
            .Where(candidate => candidate.Pages.Count > 0)
            .OrderByDescending(candidate => candidate.Pages.Count))
        {
            try
            {
                var files = await DownloadAsync(edition, pages, cancellationToken).ConfigureAwait(false);

                if (files.Count > 0) return (edition, files);
            }
            catch (ProviderUnavailableException cause)
            {
                unavailable = cause;
                ProviderLog.BookletEditionUnavailable(logger, edition, cause.Message);
            }
        }

        if (unavailable is not null) throw unavailable;

        return (null, []);
    }

    /// <summary>One edition's booklet pages as uploaded, leaving out any that are gone or not keepable.</summary>
    private async Task<List<BookletFile>> DownloadAsync(
        Mbid release,
        IReadOnlyList<CoverArtImage> pages,
        CancellationToken cancellationToken)
    {
        List<BookletFile> files = [];

        foreach (var page in pages)
        {
            var original = await archive
                .DownloadOriginalAsync(release, page.Id, MaxFileBytes, cancellationToken)
                .ConfigureAwait(false);

            var id = page.Id.ToString(CultureInfo.InvariantCulture);

            if (original is null)
            {
                ProviderLog.BookletRejected(logger, CoverArtArchiveClient.ProviderName, id, "gone, withheld or too large");
                continue;
            }

            if (!Keepable(original.Bytes, original.MediaType))
            {
                ProviderLog.BookletRejected(
                    logger, CoverArtArchiveClient.ProviderName, id, $"'{original.MediaType}' is not a picture or a PDF");
                continue;
            }

            files.Add(new BookletFile(id, original.Bytes, original.MediaType));
        }

        return files;
    }

    private async Task<(string? Album, List<BookletFile> Pdfs)> PdfsAsync(
        AlbumToFind album,
        CancellationToken cancellationToken)
    {
        if (!qobuzOptions.Value.IsConfigured) return (null, []);

        var albums = await qobuz
            .SearchAlbumsAsync(
                string.IsNullOrWhiteSpace(album.Artist) ? album.Title : $"{album.Artist} {album.Title}",
                SearchLimit,
                cancellationToken)
            .ConfigureAwait(false);

        var match = QobuzCovers.Match(
            albums, album.Title, album.Artist, album.Credited, album.Year, album.Barcode, album.Editions, out _);

        if (match is null) return (null, []);

        // A search row leaves the goodies out; only the album itself lists them.
        var whole = await qobuz.GetAlbumAsync(match.Id, cancellationToken).ConfigureAwait(false);

        List<BookletFile> pdfs = [];

        foreach (var goody in whole?.Goodies ?? [])
        {
            if (goody.FileFormatId != PdfFormat) continue;

            var id = goody.Id.ToString(CultureInfo.InvariantCulture);

            if (goody.Url.Scheme != Uri.UriSchemeHttps
                || !string.Equals(goody.Url.Host, GoodiesHost, StringComparison.OrdinalIgnoreCase))
            {
                ProviderLog.BookletRejected(logger, QobuzClient.ProviderName, id, $"{goody.Url.Host} is not their file host");
                continue;
            }

            var file = await qobuz.DownloadAsync(goody.Url, PdfTimeout, cancellationToken).ConfigureAwait(false);

            if (file is null)
            {
                ProviderLog.BookletRejected(logger, QobuzClient.ProviderName, id, "gone");
                continue;
            }

            if (file.Bytes.LongLength > MaxFileBytes || !IsPdf(file.Bytes))
            {
                ProviderLog.BookletRejected(logger, QobuzClient.ProviderName, id, "not a PDF this application will keep");
                continue;
            }

            pdfs.Add(new BookletFile(id, file.Bytes, "application/pdf"));
        }

        return (pdfs.Count == 0 ? null : match.Id, pdfs);
    }

    /// <summary>
    /// A picture the application would serve, or a PDF that is one — never a
    /// type taken on a CDN's word alone where the bytes can say.
    /// </summary>
    private static bool Keepable(byte[] bytes, string mediaType) =>
        FilePreview.IsSafeImageMediaType(mediaType)
        || (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) && IsPdf(bytes));

    private static bool IsPdf(byte[] bytes) => bytes.AsSpan().StartsWith("%PDF-"u8);
}
