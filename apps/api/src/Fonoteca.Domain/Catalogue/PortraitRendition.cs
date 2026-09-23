namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The same photograph, at a size worth sending.
/// </summary>
/// <remarks>
/// <b>Measured, and the numbers are the whole argument.</b> The three sources
/// behind a portrait resize in three different ways:
///
/// <list type="bullet">
/// <item>Wikimedia Commons honours <c>?width=</c>. Its originals are frequently
/// several megabytes of scanned photograph — one here is 972 KB whole and 28 KB
/// at 250.</item>
/// <item>Qobuz puts the rendition in the path, and <c>large</c> is not a fixed
/// size: across 251 of them the median is 211 KB and the largest is
/// <b>13.3 MB at 4480x6720</b>, so a grid of them was <b>39.7 MB of images
/// drawn into 112px circles</b>. <c>small</c> is 129-438px and 3-20 KB, which
/// is a whole page for about the weight of one of the old ones.</item>
/// <item>TheAudioDB offers no renditions at all and falls through unchanged.
/// Its thumbnails are already small, which is why that costs nothing.</item>
/// </list>
///
/// So a caller asks for a width and never for a provider.
///
/// <b>It lives here rather than beside the page it used to serve.</b> The
/// portrait endpoint is what redirects a browser at one of these URLs now, so
/// the rule has to be where that endpoint is; a copy left in TypeScript would
/// be a second opinion about which rendition exists, and the failure it would
/// produce — a 404 from a provider's CDN — reads as a missing photograph rather
/// than as a bug.
/// </remarks>
public static class PortraitRendition
{
    /// <summary>What a tile asks for when it does not say.</summary>
    public const int DefaultWidth = 250;

    /// <summary>
    /// Above this, <c>small</c> is no longer enough and <c>medium</c> is the
    /// honest answer rather than the 13 MB one.
    /// </summary>
    private const int LargestSmall = 400;

    /// <summary>The provider's URL, asked for at about <paramref name="width"/> pixels.</summary>
    public static string Sized(string url, int width = DefaultWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)) return url;

        // Commons: a query parameter, and the only source that resizes to an
        // arbitrary number rather than to a named rendition.
        if (address.AbsolutePath.StartsWith("/wiki/Special:FilePath/", StringComparison.Ordinal))
        {
            var separator = address.Query.Length > 1 ? '&' : '?';

            return $"{url}{separator}width={width}";
        }

        // Qobuz. `small` covers a 112px circle on a 2x display at the low end of
        // its range and comfortably above it at the high end.
        var rendition = width <= LargestSmall ? "small" : "medium";

        return url.Replace(
            "/images/artists/covers/large/",
            $"/images/artists/covers/{rendition}/",
            StringComparison.Ordinal);
    }
}
