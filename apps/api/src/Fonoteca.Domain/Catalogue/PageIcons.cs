using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The pictures a web page names for itself, as candidates for a radio
/// station's logo.
/// </summary>
/// <remarks>
/// <b>Two kinds, because they rank either side of the other sources.</b> An
/// <c>apple-touch-icon</c>, or an icon declaring at least
/// <see cref="LargeSize"/> pixels, is drawn to be a tile, and is preferred to
/// anything else (<see cref="PageIconList.Large"/>, largest first). The page's
/// share picture (<c>og:image</c>) and its small or unsized icons are what is
/// left when no other source has a logo (<see cref="PageIconList.Small"/>, in
/// that order): a share picture is often a banner, and a favicon is 16 pixels.
///
/// SVG is left out, as everywhere a picture is shown (<see cref="FilePreview"/>):
/// a client drawing a station's logo is not expecting a document.
///
/// Read with patterns rather than a parser, which is enough for
/// <c>&lt;link&gt;</c> and <c>&lt;meta&gt;</c> tags; a <c>&lt;base&gt;</c>
/// element is not read, so a page relying on one for its icons loses them.
/// </remarks>
public static partial class PageIcons
{
    public const int LargeSize = 120;

    /// <summary>What Apple draws an <c>apple-touch-icon</c> at when the page gives no size.</summary>
    private const int TouchIconSize = 180;

    /// <param name="page">The address the page was read from, after redirects, which a relative address is read from.</param>
    public static PageIconList From(string html, Uri page)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(page);

        List<(int Size, string Url)> large = [];
        List<string> small = [];
        string? share = null;

        foreach (Match tag in Tag().Matches(html))
        {
            var attributes = Attributes(tag.Value);

            if (tag.Groups["name"].Value.Equals("meta", StringComparison.OrdinalIgnoreCase))
            {
                if ((attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name")) is { } property
                    && property.Equals("og:image", StringComparison.OrdinalIgnoreCase))
                {
                    share ??= Address(attributes.GetValueOrDefault("content"), page, attributes.GetValueOrDefault("type"));
                }

                continue;
            }

            var rel = (attributes.GetValueOrDefault("rel") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var touch = rel.Any(token => token.StartsWith("apple-touch-icon", StringComparison.OrdinalIgnoreCase));

            if (!touch && !rel.Contains("icon", StringComparer.OrdinalIgnoreCase)) continue;

            if (Address(attributes.GetValueOrDefault("href"), page, attributes.GetValueOrDefault("type")) is not { } url) continue;

            var size = Size(attributes.GetValueOrDefault("sizes")) ?? (touch ? TouchIconSize : 0);

            if (size >= LargeSize) large.Add((size, url));
            else small.Add(url);
        }

        return new PageIconList(
            [.. large.OrderByDescending(icon => icon.Size).Select(icon => icon.Url).Distinct(StringComparer.Ordinal)],
            [.. (share is null ? small : small.Prepend(share)).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>The address made absolute, or null where it is not a raster picture on the web.</summary>
    private static string? Address(string? href, Uri page, string? type)
    {
        if (string.IsNullOrWhiteSpace(href) || type?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true) return null;

        if (!Uri.TryCreate(page, WebUtility.HtmlDecode(href).Trim(), out var address)) return null;

        if (address.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return null;

        return RadioStationFile.WebAddress(address.AbsoluteUri) ? address.AbsoluteUri : null;
    }

    /// <summary>The largest width a <c>sizes</c> attribute declares; null for none, or for <c>any</c>.</summary>
    private static int? Size(string? sizes) =>
        (sizes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(size => size.Split('x', 'X'))
            .Where(parts => parts.Length == 2)
            .Select(parts => int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ? width : (int?)null)
            .Max();

    private static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match attribute in Attribute().Matches(tag))
        {
            attributes.TryAdd(attribute.Groups["key"].Value, attribute.Groups["value"].Value);
        }

        return attributes;
    }

    [GeneratedRegex("""<(?<name>link|meta)\b[^>]*>""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    [GeneratedRegex("""(?<key>[^\s=/>"']+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s"'>]+))""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Attribute();
}

/// <param name="Large">Icons drawn to be a tile, largest first.</param>
/// <param name="Small">The share picture, then the small and unsized icons, in the page's order.</param>
public sealed record PageIconList(IReadOnlyList<string> Large, IReadOnlyList<string> Small)
{
    public static readonly PageIconList None = new([], []);
}
