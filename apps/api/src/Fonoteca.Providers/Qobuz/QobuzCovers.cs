using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fonoteca.Providers.Qobuz;

/// <summary>
/// A sleeve for an album the Cover Art Archive has none of.
/// </summary>
/// <remarks>
/// <b>The fallback, never the first source.</b> The archive is keyed on the
/// MusicBrainz release id and therefore cannot be wrong about which record it
/// is showing; this is keyed on a search, and is asked only where that key
/// produced nothing. Measured on this library, the archive holds no front for
/// <b>105 of 612</b> albums it was asked about — a sixth of the shelf drawing a
/// monogram for a record the shop sells a picture of.
///
/// <b>Two keys, and the good one is available more often than it looks.</b>
/// 69 of those 105 releases carry a <c>Barcode</c>, and a Qobuz album carries a
/// <c>Upc</c>: where both exist and agree, this is not a match at all but an
/// identifier, as safe as the archive's own. Only the remainder falls back to
/// comparing a title and an artist, which is <see cref="ReleaseTitleMatch"/>'s
/// job and is deliberately reluctant — an album whose name does not fold to
/// theirs gets no cover rather than somebody else's.
///
/// <b>What a wrong answer costs here is unusually cheap</b>, which is why name
/// matching is allowed at all after <c>ArtistPortraitSources</c> refused it for
/// portraits. A stranger's face on an artist page is invisible to the person it
/// is wrong for; a wrong sleeve is the one error a record collector spots
/// instantly, and the cover dialog is one click away and takes an upload.
/// </remarks>
public sealed class QobuzCovers(
    QobuzClient qobuz,
    IOptions<QobuzOptions> options,
    ILogger<QobuzCovers> logger)
{
    /// <summary>The only host a cover may be fetched from.</summary>
    /// <remarks>
    /// <c>QobuzPortraits</c>'s allowlist and its reasoning, applied to bytes
    /// this process stores rather than to a URL a browser follows — so it
    /// matters more here, not less.
    /// </remarks>
    private const string ImageHost = "static.qobuz.com";

    /// <summary>
    /// How many search results to consider.
    /// </summary>
    /// <remarks>
    /// Their ranking is not an answer — <see cref="QobuzClient.SearchArtistsAsync"/>
    /// makes the same point about artists — so the first row is frequently a
    /// compilation carrying the title. Ten costs exactly what one costs: a
    /// single gated request.
    /// </remarks>
    private const int SearchLimit = 10;

    /// <summary>The shop's picture of this record, or nothing.</summary>
    /// <param name="title">The title the catalogue holds.</param>
    /// <param name="artist">Its credit line, or null where nothing is credited.</param>
    /// <param name="year">Its first release year, which narrows a title match.</param>
    /// <param name="barcode">Its barcode — the key, where there is one.</param>
    /// <returns>Null when Qobuz is unconfigured or carries nothing that matches.</returns>
    /// <exception cref="ProviderException">The shop could not be asked.</exception>
    /// <remarks>
    /// <b>Null and throwing are different answers and both matter here.</b> Null
    /// is "they do not sell a record I can be sure is this one", which the
    /// caller may write down and stop asking about for a while. A throw is "I
    /// did not get to find out", which it must not: a week of monograms bought
    /// by one bad afternoon is the failure this whole retry exists to avoid.
    /// </remarks>
    public async Task<QobuzCover?> FindAsync(
        string title,
        string? artist,
        int? year,
        string? barcode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (!options.Value.IsConfigured) return null;

        var albums = await qobuz
            .SearchAlbumsAsync(
                string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}",
                SearchLimit,
                cancellationToken)
            .ConfigureAwait(false);

        var match = Match(albums, title, artist, year, barcode, out var how);

        if (match?.CoverUrl is not { } url)
        {
            ProviderLog.QobuzCoverUnmatched(logger, artist, title, albums.Count);
            return null;
        }

        if (!Picture(url))
        {
            ProviderLog.QobuzCoverRejected(logger, url);
            return null;
        }

        var image = await qobuz
            .DownloadImageAsync(new Uri(url, UriKind.Absolute), cancellationToken)
            .ConfigureAwait(false);

        // The type the CDN declared, held to the allowlist the whole application
        // serves pictures under. A redirect ending at a document must not become
        // a stored cover — the reason `DownloadCoverAsync` checks the archive's.
        if (!FilePreview.IsSafeImageMediaType(image.MediaType))
        {
            ProviderLog.QobuzCoverRejected(logger, url);
            return null;
        }

        ProviderLog.QobuzCoverMatched(logger, match.Id, artist, title, how);

        return new QobuzCover(match.Id, image.Bytes, image.MediaType);
    }

    /// <summary>Which of the shop's albums is this record, if any.</summary>
    /// <remarks>
    /// <b>The barcode is tried across every result before any title is</b>, and
    /// not per-album, because the first row whose title happens to fold to ours
    /// is routinely a different pressing of it — and where a barcode agrees
    /// anywhere in the list, that row is the pressing, not merely the album.
    /// </remarks>
    private static QobuzAlbum? Match(
        IReadOnlyList<QobuzAlbum> albums,
        string title,
        string? artist,
        int? year,
        string? barcode,
        out string how)
    {
        if (Barcode(barcode) is { } wanted)
        {
            foreach (var album in albums)
            {
                if (Barcode(album.Upc) == wanted)
                {
                    how = "barcode";
                    return album;
                }
            }
        }

        how = "title";

        // No credit line is nothing to check a title against. A search for a
        // bare title matches a compilation by somebody else as readily as the
        // record, and there is nothing here able to tell them apart.
        if (string.IsNullOrWhiteSpace(artist)) return null;

        var credited = ArtistNameMatch.Normalise(artist);

        if (credited.Length == 0) return null;

        foreach (var album in albums)
        {
            if (album.Artist is not { } billed) continue;

            if (!string.Equals(ArtistNameMatch.Normalise(billed), credited, StringComparison.Ordinal))
            {
                continue;
            }

            if (ReleaseTitleMatch.IsSameRecord(album.Title, Year(album.ReleaseDate), title, year))
            {
                return album;
            }
        }

        return null;
    }

    /// <summary>A barcode in the one form two catalogues can be compared in.</summary>
    /// <remarks>
    /// <b>Leading zeros are a formatting difference, not a different record.</b>
    /// The same album is a 12-digit UPC in one catalogue and the 13-digit EAN of
    /// it — the same digits behind a <c>0</c> — in the other, and MusicBrainz
    /// holds both shapes. Compared literally, the key that was supposed to make
    /// this safe fails on exactly the releases it was reached for.
    ///
    /// Non-digits go for the same reason: a barcode typed with spaces or hyphens
    /// is the same barcode. Anything left holding no digits is not one.
    /// </remarks>
    private static string? Barcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digits = new string([.. value.Where(char.IsAsciiDigit)]).TrimStart('0');

        return digits.Length == 0 ? null : digits;
    }

    /// <summary>The year out of a <c>YYYY-MM-DD</c>, or null.</summary>
    /// <remarks>
    /// Partial dates stay partial — the provider rule this codebase states for
    /// MusicBrainz holds for a shop too. Only the year is ever compared here, so
    /// only the year is read, and a date in any other shape reads as "they did
    /// not say" rather than as a parse failure. Silence does not veto a match;
    /// see <see cref="ReleaseTitleMatch.YearsAgree"/>.
    /// </remarks>
    private static int? Year(string? releaseDate) =>
        releaseDate is { Length: >= 4 } date
        && int.TryParse(date.AsSpan(0, 4), out var year)
        && year is > 1000 and < 3000
            ? year
            : null;

    /// <summary>Whether this URL is one of their pictures.</summary>
    private static bool Picture(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, ImageHost, StringComparison.OrdinalIgnoreCase);
}

/// <param name="AlbumId">Which Qobuz album it came from; stored as the provenance.</param>
public sealed record QobuzCover(string AlbumId, byte[] Bytes, string MediaType);
