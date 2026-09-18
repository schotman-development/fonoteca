using System.Buffers;
using System.Globalization;
using System.Text;

namespace Fonoteca.Domain.Acquisition;

/// <summary>
/// Where a downloaded track goes, relative to the staging root.
/// </summary>
/// <remarks>
/// Pure, and in the domain for the reason <c>AudioFormats</c> is: it is a rule
/// about names, not an act on a filesystem. Every part of it comes from the
/// provider's metadata, which is <b>untrusted input</b> — an album called
/// <c>../../etc</c> is a path traversal, and one ending in a dot is a file
/// Windows cannot open. Sanitising here rather than at the write is what makes
/// the rule testable without a disk.
///
/// The layout is <c>Artist/Album/NN Title.ext</c> deliberately. It is the same
/// two-deep shape <c>ALBUM_FOLDER_DEPTH</c> cuts at on the matching screen, so
/// a staged download that is later moved into the library arrives as one album
/// question rather than as a run of loose files.
/// </remarks>
public static class StagedFileName
{
    /// <summary>What a segment becomes when nothing survives sanitising.</summary>
    public const string Unnamed = "Unknown";

    /// <summary>
    /// Longest a single segment may be, <b>in UTF-8 bytes</b>.
    /// </summary>
    /// <remarks>
    /// Bytes rather than characters because that is what ext4 limits — 255 of
    /// them — and the difference is not academic: a 96-character CJK title is
    /// 288 bytes and will not create. 200 leaves room for the track-number
    /// prefix and the extension that <see cref="For"/> adds after the cap.
    /// </remarks>
    public const int MaximumSegmentBytes = 200;

    private static readonly SearchValues<char> Illegal =
        SearchValues.Create("<>:\"/\\|?*");

    /// <summary>The relative path for one track, using '/' regardless of platform.</summary>
    /// <param name="discCount">
    /// How many discs the release has. One means no disc prefix — a single-disc
    /// album numbered <c>1-01</c> reads as a box set that lost its other discs.
    /// </param>
    public static string For(
        string? artist,
        string? album,
        int discNumber,
        int trackNumber,
        string? title,
        string extension,
        int discCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var prefix = discCount > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{discNumber:0}-{trackNumber:00}")
            : trackNumber.ToString("00", CultureInfo.InvariantCulture);

        var leaf = Segment($"{prefix} {Segment(title)}");

        return $"{Segment(artist)}/{Segment(album)}/{leaf}.{extension.TrimStart('.')}";
    }

    /// <summary>
    /// One path segment: no separators, no control characters, no leading or
    /// trailing dots or spaces, never empty.
    /// </summary>
    /// <remarks>
    /// Deliberately not a claim of NTFS safety. Windows also reserves the device
    /// names — <c>CON</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1</c> — and a track
    /// titled "Aux" would still produce a name it refuses. Left alone because
    /// this runs against a Linux library and adding the rule would mangle a
    /// legitimate title on the platform that is actually used; the trade would
    /// be worth revisiting if staging ever lands on a Windows share.
    /// </remarks>
    public static string Segment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Unnamed;

        var text = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            // Control characters are legal in an ext4 name and are a menace in
            // every log, shell and UI that later prints one.
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (Illegal.Contains(character)) continue;

            if (pendingSpace)
            {
                text.Append(' ');
                pendingSpace = false;
            }

            text.Append(character);
        }

        // Trailing dots and spaces: legal to create on Linux, impossible to open
        // on Windows, and silently stripped by SMB — so a staging directory
        // shared over the network loses track of its own file.
        //
        // Leading dots go too, and for a different reason: "..\..\passwd" has
        // had its separators stripped by now, so it is no longer a traversal —
        // it is "....passwd", a dotfile, invisible to `ls` and to anything that
        // walks the staging root looking for what was downloaded.
        var cleaned = Truncate(text.ToString().Trim('.', ' '));

        // "." and ".." survive everything above and are not names.
        return cleaned.Length == 0 ? Unnamed : cleaned;
    }

    /// <summary>Cuts to <paramref name="maximumBytes"/> UTF-8 bytes without splitting a character.</summary>
    /// <remarks>
    /// Rune by rune rather than by index. A surrogate pair cut down the middle
    /// is a lone surrogate, which does not round-trip through UTF-8 and comes
    /// back off the filesystem as a replacement character — a filename that no
    /// longer equals the one that was written.
    ///
    /// Public because the staging file the tag write opens has the same limit
    /// and learned it the hard way: a name of 241 bytes is legal, and the same
    /// name inside <c>.{name}.fonoteca-xxxxxxxx.tmp</c> is 264 and cannot be
    /// created at all. One copy of the rune loop, two callers — the alternative
    /// is two, and one of them ends up counting characters.
    /// </remarks>
    public static string ClampToBytes(string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;

        var kept = new StringBuilder(maximumBytes);
        var bytes = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maximumBytes) break;

            kept.Append(rune);
            bytes += size;
        }

        return kept.ToString();
    }

    private static string Truncate(string value) =>
        ClampToBytes(value, MaximumSegmentBytes).TrimEnd('.', ' ');
}
