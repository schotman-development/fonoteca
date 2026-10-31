using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.AppleMusic;

/// <summary>
/// Which of Apple's records an album is, and the requests every reader of
/// Apple's public pages makes.
/// </summary>
/// <remarks>
/// <b>One recognition for the motion artwork and the classical pages</b>, so
/// the two cannot come to disagree about which record an album is.
///
/// <b>The record is recognised the way <c>QobuzCovers</c> recognises one, and
/// in its order.</b> This edition's barcode is an identifier; another
/// edition's names that edition, so it counts only under the same title; and a
/// title search counts only where a billed name, the title and the year all
/// agree. A record a barcode identified ends the asking whether or not its
/// page has what the reader wants — a search could only find the same record,
/// or a wrong one.
///
/// The US shop is asked first; <see cref="AppleMusicOptions.FallbackStorefront"/>
/// names a second, asked only where the first identified no record at all.
/// </remarks>
public sealed partial class AppleMusicRecords(
    IHttpClientFactory clients,
    IOptions<AppleMusicOptions> options)
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "Apple Music";

    /// <summary>
    /// How many search results to consider. Their ranking is not an answer, and
    /// ten costs what one does.
    /// </summary>
    private const int SearchLimit = 10;

    /// <summary>Pages read per shop, so an album with many look-alike records stays a few requests.</summary>
    private const int MaxPages = 3;

    /// <summary>Other editions' barcodes asked about, in one request.</summary>
    private const int MaxEditions = 50;

    /// <summary>
    /// The suffixes Apple adds to a record's name and nobody else prints.
    /// </summary>
    private static readonly string[] KindSuffixes = [" - Single", " - EP"];

    /// <summary>
    /// The first record of this album that <paramref name="read"/> answers for,
    /// or null where no shop has one it answers for.
    /// </summary>
    /// <param name="read">
    /// A record's page read for what the caller wants: null where it has none,
    /// a throw where the page could not be read.
    /// </param>
    internal async Task<RecordRead<T>?> FindAsync<T>(
        AlbumToFind album,
        Func<string, long, CancellationToken, Task<T?>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(album);

        var fallback = options.Value.FallbackStorefront.Trim();

        string[] shops = fallback.Length == 0
            || string.Equals(fallback, AppleMusicOptions.Storefront, StringComparison.OrdinalIgnoreCase)
            ? [AppleMusicOptions.Storefront]
            : [AppleMusicOptions.Storefront, fallback];

        foreach (var storefront in shops)
        {
            var (found, identified) = await FindInAsync(album, storefront, read, cancellationToken)
                .ConfigureAwait(false);

            if (found is not null) return found;

            // A record this shop knows is the same record in every other shop,
            // page and all. Only an album no shop has placed is worth a second.
            if (identified) break;
        }

        return null;
    }

    /// <summary>What one shop has for this album, and whether it identified a record at all.</summary>
    private async Task<(RecordRead<T>? Found, bool Identified)> FindInAsync<T>(
        AlbumToFind album,
        string storefront,
        Func<string, long, CancellationToken, Task<T?>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        var asked = new HashSet<long>();
        var pages = 0;

        async Task<RecordRead<T>?> FirstReadAsync(IEnumerable<AppleRecord> records, string how)
        {
            foreach (var record in records)
            {
                if (pages >= MaxPages) return null;
                if (!asked.Add(record.Id)) continue;

                pages++;

                if (await read(storefront, record.Id, cancellationToken).ConfigureAwait(false) is { } value)
                {
                    return new RecordRead<T>(
                        value,
                        record.Id.ToString(CultureInfo.InvariantCulture),
                        storefront,
                        how);
                }
            }

            return null;
        }

        var own = Digits(album.Barcode);

        if (own is not null)
        {
            var found = await FirstReadAsync(
                    await LookupAsync([own], storefront, cancellationToken).ConfigureAwait(false),
                    "barcode")
                .ConfigureAwait(false);

            if (found is not null) return (found, true);
        }

        var others = album.Editions
            .Select(Digits)
            .OfType<string>()
            .Where(barcode => barcode != own)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxEditions)
            .ToList();

        if (others.Count > 0)
        {
            // Another edition's barcode names that edition, so it counts only
            // under the same title: without it, Bewitched takes the video of
            // Bewitched: The Goddess Edition. The year is not asked, since the
            // barcode already says which record.
            var editions = (await LookupAsync(others, storefront, cancellationToken).ConfigureAwait(false))
                .Where(record => ReleaseTitleMatch.IsSameRecord(Title(record.Name), null, album.Title, null))
                .ToList();

            var found = await FirstReadAsync(editions, "another edition's barcode").ConfigureAwait(false);

            if (found is not null) return (found, true);
        }

        if (asked.Count > 0) return (null, true);

        // No credit line is nothing to check a title against: a search for a
        // bare title matches somebody else's record as readily as this one.
        if (string.IsNullOrWhiteSpace(album.Artist)) return (null, false);

        var names = album.Credited
            .Prepend(album.Artist)
            .Select(ArtistNameMatch.Normalise)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var titled = (await SearchAsync($"{album.Artist} {album.Title}", storefront, cancellationToken)
                .ConfigureAwait(false))
            .Where(record => record.Artist is { } billed
                && names.Contains(ArtistNameMatch.Normalise(billed))
                && ReleaseTitleMatch.IsSameRecord(Title(record.Name), record.Year, album.Title, album.Year))
            .ToList();

        return (await FirstReadAsync(titled, "title").ConfigureAwait(false), titled.Count > 0);
    }

    /// <summary>The records these barcodes name in one shop, in one request.</summary>
    /// <remarks>
    /// The lookup takes a comma-separated list and answers in no particular
    /// order and without saying which barcode found which record — so a batch is
    /// only ever of barcodes that are treated alike.
    /// </remarks>
    private Task<List<AppleRecord>> LookupAsync(
        IReadOnlyList<string> barcodes,
        string storefront,
        CancellationToken cancellationToken) =>
        RecordsAsync(
            $"lookup?upc={string.Join(',', barcodes)}&entity=album&country={Uri.EscapeDataString(storefront)}",
            cancellationToken);

    private Task<List<AppleRecord>> SearchAsync(
        string term,
        string storefront,
        CancellationToken cancellationToken) =>
        RecordsAsync(
            $"search?term={Uri.EscapeDataString(term)}&entity=album&limit={SearchLimit}"
            + $"&country={Uri.EscapeDataString(storefront)}",
            cancellationToken);

    private async Task<List<AppleRecord>> RecordsAsync(string path, CancellationToken cancellationToken) =>
        [.. (await ResultsAsync(path, cancellationToken).ConfigureAwait(false))
            .Where(row => row.WrapperType == "collection" && row.CollectionId is > 0 && row.CollectionName is not null)
            .Select(row => new AppleRecord(row.CollectionId!.Value, row.CollectionName!, row.ArtistName, Year(row.ReleaseDate)))];

    /// <summary>The rows the documented search API answers a lookup or a search with.</summary>
    internal async Task<IReadOnlyList<ITunesResult>> ResultsAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(AppleMusicOptions.HttpClientName, new Uri(path, UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw Unavailable($"the search API answered {(int)response.StatusCode}", cause: null);
        }

        ITunesResults? results;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                results = await JsonSerializer
                    .DeserializeAsync(stream, AppleMusicJsonContext.Default.ITunesResults, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException cause)
        {
            throw Unavailable("the search API did not answer with the JSON we expect", cause);
        }

        // A throttled answer is an empty body, which deserialises to nothing at
        // all — not to an empty list, which is how "no such record" is said.
        return results?.Results
            ?? throw Unavailable("the search API answered with no result list", cause: null);
    }

    /// <summary>One request through one of the named clients, with every way it can fail made an outage.</summary>
    internal async Task<HttpResponseMessage> SendAsync(string client, Uri uri, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(client);

        try
        {
            return await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
        catch (TaskCanceledException cause) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("the request timed out", cause);
        }
        catch (ExecutionRejectedException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
    }

    /// <summary>A barcode as the lookup takes it: its digits, leading zeros and all.</summary>
    /// <remarks>
    /// Not <see cref="Barcodes.Normalise"/>, which drops the zeros and the check
    /// digit to compare two catalogues' spellings — this is a key sent to one,
    /// and it answers a 13-digit EAN and its 12-digit UPC alike.
    /// </remarks>
    private static string? Digits(string? barcode) =>
        barcode is null ? null : new string([.. barcode.Where(char.IsAsciiDigit)]) is { Length: > 0 } digits ? digits : null;

    /// <summary>The record's name as other catalogues print it, without Apple's " - Single".</summary>
    private static string Title(string name)
    {
        foreach (var suffix in KindSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        }

        return name;
    }

    /// <summary>The year out of an ISO date, or null — partial dates stay partial.</summary>
    private static int? Year(string? releaseDate) =>
        releaseDate is { Length: >= 4 } date
        && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
        && year is > 1000 and < 3000
            ? year
            : null;

    internal static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"Apple Music request failed: {reason}.")
            : new ProviderUnavailableException(ProviderName, $"Apple Music request failed: {reason}.", cause);

    /// <summary>The JSON a public page embeds for what it draws.</summary>
    [GeneratedRegex("""<script[^>]*\bid="serialized-server-data"[^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline)]
    internal static partial Regex ServerData();

    private sealed record AppleRecord(long Id, string Name, string? Artist, int? Year);
}

/// <param name="AlbumId">Apple's id for the record — the provenance.</param>
/// <param name="Storefront">Which country's shop answered.</param>
/// <param name="MatchedBy">How the record was recognised: "barcode", "another edition's barcode" or "title".</param>
internal sealed record RecordRead<T>(T Value, string AlbumId, string Storefront, string MatchedBy);
