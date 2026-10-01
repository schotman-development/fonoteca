using System.Globalization;
using System.Text;
using ATL;
using ATL.AudioData.IO;

namespace Fonoteca.Tagging;

/// <summary>
/// The old ID3v1 tag at the end of an MP3, written in full after ATL saves.
/// </summary>
/// <remarks>
/// The owner's choice is both tags, old and new, for whatever reads only the
/// old one. ATL writes the old tag's year from a field it empties whenever it
/// writes a full date — 50 files here lost "2005" that way — takes a genre
/// only when the whole genre field is one of ID3v1's names, so
/// "Country;Contemporary Country" became 255, none, on 33; and writes no old
/// tag to a file that has none. So: the year from the date, the first genre
/// ID3v1 numbers, and a whole old tag where there was none, as Latin-1 cut to
/// ID3v1's thirty bytes a field.
/// </remarks>
internal static class Id3v1Trailer
{
    private const int Size = 128;

    public static void Write(Stream stream, Track track)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(track);

        var tag = new byte[Size];
        var present = false;

        if (stream.Length >= Size)
        {
            stream.Seek(-Size, SeekOrigin.End);
            present = stream.ReadAtLeast(tag, Size, throwOnEndOfStream: false) == Size
                && tag.AsSpan(0, 3).SequenceEqual("TAG"u8);
        }

        if (!present)
        {
            tag = new byte[Size];
            "TAG"u8.CopyTo(tag);
            Text(track.Title, tag.AsSpan(3, 30));
            Text(track.Artist, tag.AsSpan(33, 30));
            Text(track.Album, tag.AsSpan(63, 30));
            Text(track.Comment, tag.AsSpan(97, 28));
            tag[126] = (byte)Math.Clamp(track.TrackNumber ?? 0, 0, byte.MaxValue);
            tag[127] = byte.MaxValue;
        }

        if (track.Year is > 0 and < 10000 and var year)
        {
            Encoding.Latin1.GetBytes(year.ToString("D4", CultureInfo.InvariantCulture)).CopyTo(tag.AsSpan(93, 4));
        }

        if (Genre(track.Genre) is { } genre) tag[127] = genre;

        stream.Seek(present ? -Size : 0, SeekOrigin.End);
        stream.Write(tag);
        stream.Flush();
    }

    private static void Text(string? value, Span<byte> field)
    {
        if (string.IsNullOrEmpty(value)) return;

        var bytes = Encoding.Latin1.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
    }

    /// <summary>ID3v1's number for the first of the genres it has one for.</summary>
    private static byte? Genre(string? genres)
    {
        if (string.IsNullOrEmpty(genres)) return null;

        foreach (var name in genres.Split([TaggingDefaults.ValueSeparator, ';'], StringSplitOptions.TrimEntries))
        {
            for (var number = 0; number < ID3v1.MAX_MUSIC_GENRES; number++)
            {
                if (string.Equals(name, ID3v1.MusicGenre[number], StringComparison.OrdinalIgnoreCase)) return (byte)number;
            }
        }

        return null;
    }
}
