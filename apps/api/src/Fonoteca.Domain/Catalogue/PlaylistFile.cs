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
/// </remarks>
public static class PlaylistFile
{
    public const string Folder = "Playlists";

    public const string Extension = ".m3u8";

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
        text.Append("#PLAYLIST:").Append(OneLine(name)).Append('\n');

        foreach (var line in lines)
        {
            if (line.Path.AsSpan().IndexOfAny('\n', '\r') >= 0) continue;

            var seconds = line.Duration is { } duration ? (int)Math.Round(duration.TotalSeconds) : -1;

            text.Append("#EXTINF:").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(',');
            if (line.Artist is not null) text.Append(OneLine(line.Artist)).Append(" - ");
            text.Append(OneLine(line.Title)).Append('\n');
            text.Append("../").Append(line.Path).Append('\n');
        }

        return text.ToString();
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

/// <summary>One file holding a song: what <see cref="PlaylistFile.Best"/> weighs, and its size for the row that shows it.</summary>
public sealed record PlaylistCopy(
    MediaFileId Id,
    string Path,
    long SizeBytes,
    AudioQuality? Quality,
    AudioMeasurement? Measurement,
    IntegrityState Integrity);
