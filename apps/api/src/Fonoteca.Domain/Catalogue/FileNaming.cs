using System.Globalization;
using System.Text;
using Fonoteca.Domain.Acquisition;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Where a file belongs in the library, by a naming pattern.
/// </summary>
/// <remarks>
/// <b>Tokens</b> are <c>{albumartist}</c>, <c>{album}</c>, <c>{year}</c>,
/// <c>{disc}</c>, <c>{track}</c>, <c>{title}</c> and <c>{artist}</c>.
/// <c>{disc}</c> is empty on a single-disc release and <c>{track}</c> is two
/// digits. <b><c>[…]</c> is dropped whole when a token inside it is empty</b> —
/// foobar2000's convention — so <c>[ ({year})]</c> leaves no empty brackets on
/// an undated album. A token outside one that is empty renders nothing at all:
/// a file is not named <c>Unknown</c> on a fact the catalogue does not hold.
///
/// <b>The first two segments are the album folder</b>, because that is the
/// unit <see cref="AlbumFolder"/> cuts at and the attribution pass decides by.
/// A pattern with fewer is refused, and a separator inside <c>[…]</c> is too,
/// since dropping the group would move the file up a level.
///
/// Every value is a path segment by <see cref="StagedFileName.Segment"/>'s
/// rule: no separators, no characters Windows refuses, clamped to ext4's bytes.
/// </remarks>
public static class FileNaming
{
    /// <summary>What Plex, Jellyfin, Navidrome, Lidarr and Picard all read.</summary>
    public const string DefaultPattern = "{albumartist}/{album}[ ({year})]/[{disc}-]{track} - {title}";

    private static readonly string[] Tokens = ["albumartist", "album", "year", "disc", "track", "title", "artist"];

    /// <summary>Why a pattern cannot be used, or null when it can.</summary>
    public static string? Problem(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "The naming pattern is empty.";

        var inGroup = false;

        for (var at = 0; at < pattern.Length; at++)
        {
            switch (pattern[at])
            {
                case '[' when inGroup:
                    return "Optional groups '[…]' cannot be nested.";
                case '[':
                    inGroup = true;
                    break;
                case ']' when !inGroup:
                    return "A ']' closes no '['.";
                case ']':
                    inGroup = false;
                    break;
                case '/' when inGroup:
                    return "A '/' inside '[…]' would move the file up a folder when the group is dropped.";
                case '{':
                    var close = pattern.IndexOf('}', at);
                    if (close < 0) return "A '{' is never closed.";

                    var token = pattern[(at + 1)..close];
                    if (!Tokens.Contains(token, StringComparer.Ordinal))
                    {
                        return $"'{{{token}}}' is not a token. Use {string.Join(", ", Tokens.Select(name => $"{{{name}}}"))}.";
                    }

                    at = close;
                    break;
            }
        }

        if (inGroup) return "A '[' is never closed.";

        var segments = pattern.Split('/');

        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment)))
        {
            return "A '/' at either end of the pattern, or two together, leaves a folder or file with no name.";
        }

        return segments.Length < 3
            ? "The pattern needs an artist folder, an album folder and a file name: two '/' at least."
            : null;
    }

    /// <summary>The album folder: the pattern's first two segments, or null when a fact they need is missing.</summary>
    public static string? Folder(string pattern, NamingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var segments = pattern.Split('/');

        return Join(segments[..2].Select(segment => Render(segment, facts)));
    }

    /// <summary>
    /// The file's path below its album folder, with its extension, or null when
    /// a fact it needs is missing.
    /// </summary>
    public static string? File(string pattern, NamingFacts facts, string extension)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var segments = pattern.Split('/');
        var rendered = segments[2..].Select(segment => Render(segment, facts)).ToList();

        if (rendered.Any(segment => segment is null)) return null;

        // The extension after the clamp, as StagedFileName.For does, so a long
        // title cannot cut it off.
        rendered[^1] = $"{rendered[^1]}.{extension.TrimStart('.')}";

        return string.Join('/', rendered);
    }

    private static string? Join(IEnumerable<string?> segments)
    {
        var list = segments.ToList();
        return list.Any(segment => segment is null) ? null : string.Join('/', list);
    }

    /// <summary>One segment, or null where a token outside a group had nothing to say.</summary>
    private static string? Render(string segment, NamingFacts facts)
    {
        var text = new StringBuilder();
        var group = new StringBuilder();
        var inGroup = false;
        var groupEmpty = false;

        for (var at = 0; at < segment.Length; at++)
        {
            var character = segment[at];

            if (character == '[')
            {
                inGroup = true;
                groupEmpty = false;
                group.Clear();
                continue;
            }

            if (character == ']')
            {
                inGroup = false;
                if (!groupEmpty) text.Append(group);
                continue;
            }

            var target = inGroup ? group : text;

            if (character != '{')
            {
                target.Append(character);
                continue;
            }

            var close = segment.IndexOf('}', at);
            var value = Value(segment[(at + 1)..close], facts);
            at = close;

            if (value.Length == 0)
            {
                if (!inGroup) return null;
                groupEmpty = true;
            }

            target.Append(value);
        }

        var cleaned = StagedFileName.Segment(text.ToString());

        return cleaned == StagedFileName.Unnamed && text.ToString().Trim() != StagedFileName.Unnamed
            ? null
            : cleaned;
    }

    private static string Value(string token, NamingFacts facts) => token switch
    {
        "albumartist" => Clean(facts.AlbumArtist),
        "album" => Clean(facts.Album),
        "year" => facts.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        "disc" => facts is { DiscCount: > 1, Disc: { } disc }
            ? disc.ToString(CultureInfo.InvariantCulture)
            : string.Empty,
        "track" => facts.Track?.ToString("00", CultureInfo.InvariantCulture) ?? string.Empty,
        "title" => Clean(facts.Title),
        "artist" => Clean(facts.Artist),
        _ => string.Empty,
    };

    /// <summary>A value with separators and illegal characters gone, or empty when nothing survives.</summary>
    private static string Clean(string? value)
    {
        var cleaned = StagedFileName.Segment(value);
        return cleaned == StagedFileName.Unnamed && value?.Trim() != StagedFileName.Unnamed ? string.Empty : cleaned;
    }
}

/// <summary>What a file's name is made of, as the catalogue holds it.</summary>
/// <param name="AlbumArtist">One billed album artist's own name.</param>
/// <param name="DiscCount">Discs on the release; <c>{disc}</c> is empty unless more than one.</param>
public sealed record NamingFacts(
    string? AlbumArtist,
    string? Album,
    int? Year,
    int? Disc,
    int? DiscCount,
    int? Track,
    string? Title,
    string? Artist);
