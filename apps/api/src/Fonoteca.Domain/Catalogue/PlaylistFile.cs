using System.Globalization;
using System.Text;
using Fonoteca.Domain.Acquisition;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Where a playlist's m3u lives, what it says, and which copy of a song it names.
/// </summary>
/// <remarks>
/// <b>At the top of the library, with paths relative to the file</b> (the
/// owner's choice). Navidrome and Roon import any playlist under the folder they
/// watch, the production sync carries it with the music, and a relative path
/// still resolves on the production copy and on a Mac reading the share. Both
/// players read <c>.m3u8</c>; Navidrome takes <c>#PLAYLIST:</c> as the name, and
/// Roon's own help recommends relative paths.
///
/// <b>A static playlist is its file</b> (the owner's choice), so whatever wrote
/// it — a text editor, another player — is believed, and an edit here changes
/// only the lines it names. Every other line, directive and comment goes back
/// as it came. The newline is the file's own; a file with mixed ones comes back
/// with the one it uses most.
/// </remarks>
public static class PlaylistFile
{
    public const string Folder = "Playlists";

    public const string Extension = ".m3u8";

    /// <summary>Both spellings players write and read; a file of either kind at the top of <see cref="Folder"/> is a playlist.</summary>
    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>([".m3u8", ".m3u"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The library-relative path a playlist of this name is written to.</summary>
    public static string PathFor(string name) =>
        $"{Folder}/{StagedFileName.Segment(name)}{Extension}";

    /// <summary>The file's text: extended M3U, one song per two lines, paths relative to <see cref="Folder"/>.</summary>
    /// <remarks>
    /// A path with a line break in it is left out: the format's records are
    /// lines, and no reader could take it back.
    /// </remarks>
    public static string Render(string name, IEnumerable<PlaylistLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var text = new StringBuilder("#EXTM3U\n");
        text.Append(NameDirective).Append(OneLine(name)).Append('\n');

        foreach (var entry in lines.SelectMany(EntryLines)) text.Append(entry).Append('\n');

        return text.ToString();
    }

    /// <summary>The name the file gives itself in <c>#PLAYLIST:</c>, or null where it gives none.</summary>
    public static string? NameIn(string text) =>
        Parse(text).Header
            .Where(line => line.StartsWith(NameDirective, StringComparison.OrdinalIgnoreCase))
            .Select(line => line[NameDirective.Length..].Trim())
            .FirstOrDefault(name => name.Length > 0);

    /// <summary>Each entry in the file's order: where it points, as written, and what its <c>#EXTINF</c> calls it.</summary>
    public static IReadOnlyList<PlaylistItem> Items(string text) =>
        [.. Parse(text).Entries.Select(entry =>
        {
            var info = entry.LastOrDefault(line => line.StartsWith(InfoDirective, StringComparison.OrdinalIgnoreCase));
            var (duration, title) = info is null ? (null, null) : Info(info[InfoDirective.Length..]);

            return new PlaylistItem(entry[^1].Trim(), title, duration);
        })];

    /// <summary>
    /// The library-relative path an entry names, or null where it names
    /// nothing under the library: a stream, another disk, a path climbing out
    /// of the root.
    /// </summary>
    /// <param name="location">The entry's line, as <see cref="PlaylistItem.Location"/>.</param>
    /// <param name="playlist">The m3u's own library-relative path, which a relative entry is read from.</param>
    /// <param name="root">The library's absolute root, which an absolute entry must be under.</param>
    public static string? Resolve(string location, string playlist, string? root)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(playlist);

        var path = location.Trim();

        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            path = uri.LocalPath;
        }
        else if (path.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        path = path.Replace('\\', '/');

        if (path.StartsWith('/'))
        {
            if (root is null) return null;

            var top = root.TrimEnd('/') + "/";
            return path.StartsWith(top, StringComparison.Ordinal) ? Normalise([], path[top.Length..]) : null;
        }

        // A drive letter: a playlist written on Windows naming a disk this is not.
        if (path.Length >= 2 && path[1] == ':') return null;

        return Normalise(Segments(FolderOf(playlist)), path);
    }

    /// <summary>
    /// The text with every entry naming a moved path — the file itself, or
    /// anything under a moved folder — pointing where it went; the same string
    /// where none does.
    /// </summary>
    /// <remarks>
    /// The moves apply in order, so a chain of renames lands at its end. An
    /// entry keeps its style: one written absolute stays absolute, under the
    /// root it was read against; any other becomes relative to the playlist.
    /// An entry naming nothing on disk moves with its folder all the same — it
    /// is still the line somebody put there.
    /// </remarks>
    public static string Repoint(string text, string playlist, string? root, IReadOnlyList<PlaylistMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);

        var document = Parse(text);
        var changed = false;

        foreach (var entry in document.Entries)
        {
            var location = entry[^1].Trim();

            if (Resolve(location, playlist, root) is not { } path) continue;

            var moved = path;

            foreach (var move in moves)
            {
                if (string.Equals(moved, move.From, StringComparison.Ordinal)) moved = move.To;
                else if (moved.StartsWith(move.From + "/", StringComparison.Ordinal)) moved = move.To + moved[move.From.Length..];
            }

            if (string.Equals(moved, path, StringComparison.Ordinal)) continue;

            entry[^1] = root is not null && (location.StartsWith('/') || location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                ? $"{root.TrimEnd('/')}/{moved}"
                : Locate(moved, playlist);

            changed = true;
        }

        return changed ? Render(document) : text;
    }

    /// <summary>The text with its entries in this order, each taking its own <c>#EXTINF</c> and comments along; one left out is removed.</summary>
    /// <param name="order">Indexes into <see cref="Items"/>, each at most once.</param>
    public static string Arrange(string text, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var document = Parse(text);

        if (order.Distinct().Count() != order.Count || order.Any(index => index < 0 || index >= document.Entries.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Each index must name one of the file's entries, at most once.");
        }

        return Render(document with { Entries = [.. order.Select(index => document.Entries[index])] });
    }

    /// <summary>The text with these songs after its last entry; a whole new file where the text is empty.</summary>
    public static string Append(string text, string name, IEnumerable<PlaylistLine> lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);

        if (string.IsNullOrWhiteSpace(text)) return Render(name, lines);

        var document = Parse(text);
        var entries = new List<List<string>>(document.Entries);
        var pending = new List<string>(document.Trailer);

        foreach (var line in lines)
        {
            if (EntryLines(line) is not { Count: > 0 } added) continue;

            pending.AddRange(added);
            entries.Add(pending);
            pending = [];
        }

        return Render(document with { Entries = entries, Trailer = pending, FinalNewline = true });
    }

    /// <summary>The text with <c>#PLAYLIST:</c> saying this name, added under <c>#EXTM3U</c> where the file has none.</summary>
    public static string Renamed(string text, string name)
    {
        var document = Parse(text);
        var header = new List<string>(document.Header);
        var directive = NameDirective + OneLine(name);

        var at = header.FindIndex(line => line.StartsWith(NameDirective, StringComparison.OrdinalIgnoreCase));

        if (at >= 0)
        {
            header[at] = directive;
        }
        else
        {
            var extended = header.FindIndex(line => line.StartsWith(ExtendedDirective, StringComparison.OrdinalIgnoreCase));

            if (extended < 0)
            {
                header.InsertRange(0, [ExtendedDirective, directive]);
            }
            else
            {
                header.Insert(extended + 1, directive);
            }
        }

        return Render(document with { Header = header, FinalNewline = document.FinalNewline || document.Entries.Count == 0 });
    }

    /// <summary>
    /// The copy of a song a playlist plays.
    /// </summary>
    /// <remarks>
    /// One that decodes over one the probe found damaged, then the best by the
    /// rule that decides which duplicate to keep — the audio's real bit depth,
    /// not the container's — then the path, so the same library always gives
    /// the same file and the m3u is not rewritten for nothing.
    /// </remarks>
    public static PlaylistCopy? Best(IEnumerable<PlaylistCopy> copies) =>
        copies
            .OrderBy(copy => copy.Integrity is IntegrityState.Corrupt or IntegrityState.Truncated or IntegrityState.Unreadable)
            .ThenByDescending(
                copy => AudioAnalysis.Decided(copy.Quality, copy.Measurement),
                Comparer<AudioQuality?>.Create(AudioQuality.Compare))
            .ThenBy(copy => copy.Path, StringComparer.Ordinal)
            .FirstOrDefault();

    private const string ExtendedDirective = "#EXTM3U";

    private const string NameDirective = "#PLAYLIST:";

    private const string InfoDirective = "#EXTINF:";

    /// <summary>
    /// The file split into what comes before the first entry, the entries —
    /// each its own directives and comments, its path last — and what comes
    /// after the last.
    /// </summary>
    private sealed record Document(List<string> Header, List<List<string>> Entries, List<string> Trailer, string Newline, bool FinalNewline);

    private static Document Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var crlf = text.Split("\r\n").Length - 1;
        var newline = crlf > 0 && crlf * 2 >= text.Count(character => character == '\n') ? "\r\n" : "\n";
        var final = text.EndsWith('\n');

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (final || text.Length == 0) lines.RemoveAt(lines.Count - 1);

        var header = new List<string>();
        var entries = new List<List<string>>();
        var pending = new List<string>();
        var started = false;

        foreach (var line in lines)
        {
            if (!started && !IsLocation(line) && !line.StartsWith(InfoDirective, StringComparison.OrdinalIgnoreCase))
            {
                header.Add(line);
                continue;
            }

            started = true;
            pending.Add(line);

            if (IsLocation(line))
            {
                entries.Add(pending);
                pending = [];
            }
        }

        return new Document(header, entries, pending, newline, final);
    }

    private static string Render(Document document)
    {
        var lines = document.Header.Concat(document.Entries.SelectMany(entry => entry)).Concat(document.Trailer).ToList();
        var text = string.Join(document.Newline, lines);

        return document.FinalNewline && lines.Count > 0 ? text + document.Newline : text;
    }

    private static bool IsLocation(string line) => line.Trim() is { Length: > 0 } trimmed && !trimmed.StartsWith('#');

    /// <summary>A song's two lines, relative to <see cref="Folder"/>; none where its path holds a line break.</summary>
    private static List<string> EntryLines(PlaylistLine line)
    {
        if (line.Path.AsSpan().IndexOfAny('\n', '\r') >= 0) return [];

        var seconds = line.Duration is { } duration ? (int)Math.Round(duration.TotalSeconds) : -1;
        var title = line.Artist is null ? OneLine(line.Title) : $"{OneLine(line.Artist)} - {OneLine(line.Title)}";

        return [$"{InfoDirective}{seconds.ToString(CultureInfo.InvariantCulture)},{title}", $"../{line.Path}"];
    }

    /// <summary><c>#EXTINF</c>'s length and display text; attributes between the two are skipped.</summary>
    private static (TimeSpan? Duration, string? Title) Info(string value)
    {
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        var head = comma < 0 ? value : value[..comma];
        var title = comma < 0 ? null : value[(comma + 1)..].Trim();

        var number = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        TimeSpan? duration = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

        return (duration, string.IsNullOrEmpty(title) ? null : title);
    }

    /// <summary>A library-relative path as an entry of the playlist at <paramref name="playlist"/> writes it.</summary>
    private static string Locate(string path, string playlist)
    {
        var from = Segments(FolderOf(playlist));
        var to = Segments(path);

        var shared = 0;
        while (shared < from.Count && shared < to.Count - 1 && from[shared] == to[shared]) shared++;

        return string.Concat(Enumerable.Repeat("../", from.Count - shared)) + string.Join('/', to.Skip(shared));
    }

    /// <summary><paramref name="path"/> read from <paramref name="folder"/>, its dots resolved; null where it climbs out of the root.</summary>
    private static string? Normalise(List<string> folder, string path)
    {
        var segments = new List<string>(folder);

        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".") continue;

            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string FolderOf(string path) =>
        path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : string.Empty;

    private static List<string> Segments(string path) =>
        [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries)];

    private static string OneLine(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value) text.Append(char.IsControl(character) ? ' ' : character);
        return text.ToString();
    }
}

/// <summary>One song as the m3u names it.</summary>
/// <param name="Path">Library-relative, as <c>MediaFile.Path</c>.</param>
public sealed record PlaylistLine(string Path, TimeSpan? Duration, string? Artist, string Title);

/// <summary>One entry as a file has it.</summary>
/// <param name="Location">The path line, as written: relative, absolute or a URL.</param>
/// <param name="Title">What its <c>#EXTINF</c> calls it, usually "Artist - Title"; null with none.</param>
public sealed record PlaylistItem(string Location, string? Title, TimeSpan? Duration);

/// <summary>A path Fonoteca moved, library-relative: a file, or a folder and everything in it.</summary>
public sealed record PlaylistMove(string From, string To);

/// <summary>One file holding a song: what <see cref="PlaylistFile.Best"/> weighs, and its size for the row that shows it.</summary>
public sealed record PlaylistCopy(
    MediaFileId Id,
    string Path,
    long SizeBytes,
    AudioQuality? Quality,
    AudioMeasurement? Measurement,
    IntegrityState Integrity);
