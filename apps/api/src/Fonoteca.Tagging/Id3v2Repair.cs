using System.Text;

namespace Fonoteca.Tagging;

/// <summary>
/// Puts right, in the staged copy, what ATL damages in an ID3v2 tag it does not
/// understand.
/// </summary>
/// <remarks>
/// <b>A MusicBrainz <c>UFID</c>.</b> ATL ends every frame it writes without a
/// text encoding with a terminator (<c>writeNullTermination</c>, a local nothing
/// can set), and a <c>UFID</c> identifier is binary: the byte becomes part of
/// the id. TagLib# ignores it; Picard reads the recording id as <c>…\0</c>,
/// which is no MBID. The owner's choice is to trim it.
///
/// <b><c>RGAD</c></b>, the old ReplayGain frame: eight bytes of peak and gain
/// that ATL reads as text and writes back with an encoding byte in front and
/// the last byte gone. The owner's choice is old and new ReplayGain both, so
/// the original bytes go back in.
///
/// Frames after a changed one move, and padding — zero, as padding is — takes
/// up the difference; where there is not enough, the tag grows. What this
/// cannot put right refuses the write, since TagLib# sees neither: another
/// owner's identifier, whose last byte may be its own; a frame with flags; a
/// tag unsynchronised, extended or with a footer.
/// </remarks>
internal static class Id3v2Repair
{
    private const string MusicBrainz = "http://musicbrainz.org";

    /// <summary>Frames ATL rewrites as text, whose bytes go back as they were.</summary>
    private static readonly string[] Binary = ["RGAD"];

    /// <summary>
    /// The original tag's frames to put back, by id; null when the tag holds
    /// one and cannot be walked, or holds two.
    /// </summary>
    public static Dictionary<string, byte[]>? Kept(Stream original)
    {
        ArgumentNullException.ThrowIfNull(original);

        var kept = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        if (Read(original) is not var (version, flags, tag)) return kept;

        if (Frames(version, flags, tag) is not { } frames)
        {
            return Binary.Any(id => tag.AsSpan().IndexOf(Encoding.Latin1.GetBytes(id)) >= 0) ? null : kept;
        }

        foreach (var frame in frames.Where(frame => Binary.Contains(frame.Id)))
        {
            if (frame.Flagged || !kept.TryAdd(frame.Id, tag.AsSpan(frame.At + 10, frame.Size).ToArray())) return null;
        }

        return kept;
    }

    /// <summary>
    /// Repairs the tag at the start of the stream in place. Null when nothing is
    /// left wrong; otherwise why the write should not go ahead.
    /// </summary>
    public static string? Repair(Stream stream, IReadOnlyDictionary<string, byte[]> kept)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(kept);

        if (Read(stream) is not var (version, flags, tag))
        {
            return kept.Count > 0 ? $"The write would drop the {string.Join(", ", kept.Keys)} frame." : null;
        }

        if (Frames(version, flags, tag) is not { } frames)
        {
            return tag.AsSpan().IndexOf("UFID"u8) >= 0 || kept.Count > 0
                ? "ATL damages frames in this tag, and it is laid out in a way the repair does not rewrite."
                : null;
        }

        var output = new MemoryStream(tag.Length);
        var restored = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;

        foreach (var frame in frames)
        {
            var body = tag.AsSpan(frame.At + 10, frame.Size);
            byte[]? replacement = null;

            if (frame.Id == "UFID"
                && body.IndexOf((byte)0) is var owner and >= 0
                && body.Length > owner + 1
                && body[^1] == 0)
            {
                if (frame.Flagged || Encoding.Latin1.GetString(body[..owner]) != MusicBrainz)
                {
                    return "ATL would leave a NUL on the end of a file identifier that is not MusicBrainz's.";
                }

                replacement = body[..^1].ToArray();
            }
            else if (kept.TryGetValue(frame.Id, out var original))
            {
                if (frame.Flagged) return $"ATL rewrote the {frame.Id} frame in a form the repair does not read.";

                restored.Add(frame.Id);
                if (!body.SequenceEqual(original)) replacement = original;
            }

            if (replacement is null)
            {
                output.Write(tag.AsSpan(frame.At, 10 + frame.Size));
                continue;
            }

            var header = tag.AsSpan(frame.At, 10).ToArray();
            WriteSize(header.AsSpan(4, 4), replacement.Length, version);
            output.Write(header);
            output.Write(replacement);
            changed = true;
        }

        if (kept.Keys.FirstOrDefault(id => !restored.Contains(id)) is { } dropped)
        {
            return $"The write would drop the {dropped} frame.";
        }

        if (!changed) return null;

        // ATL leaves no padding, so a frame put back longer than ATL wrote it
        // grows the tag, and the audio after it moves up by as much.
        if (output.Length > tag.Length)
        {
            Streams.Shift(stream, 10 + tag.Length, (int)output.Length - tag.Length);

            var size = new byte[4];
            WriteSize(size, (int)output.Length, 4);
            stream.Position = 6;
            stream.Write(size);
        }
        else
        {
            output.SetLength(tag.Length);
        }

        stream.Position = 10;
        stream.Write(output.GetBuffer().AsSpan(0, (int)output.Length));
        stream.Flush();

        return null;
    }

    /// <param name="Flagged">
    /// Whether the frame's format flags are set — compression, encryption,
    /// grouping, unsynchronisation — any of which make its bytes something
    /// other than its body. Status flags say what to do with a frame when the
    /// file changes, and leave its bytes alone.
    /// </param>
    private sealed record Frame(string Id, int At, int Size, bool Flagged);

    /// <summary>The tag at the start of the stream: version, flags and body. Null when there is none.</summary>
    private static (byte Version, byte Flags, byte[] Tag)? Read(Stream stream)
    {
        stream.Position = 0;

        var header = new byte[10];
        if (stream.Read(header) != header.Length || !header.AsSpan(0, 3).SequenceEqual("ID3"u8)) return null;

        var size = Syncsafe(header.AsSpan(6, 4));
        if (size <= 0) return null;

        var tag = new byte[size];

        stream.Position = 10;
        return stream.ReadAtLeast(tag, size, throwOnEndOfStream: false) == size ? (header[3], header[5], tag) : null;
    }

    /// <summary>A tag body's frames, or null when it is not one this walks.</summary>
    /// <param name="version">3 or 4; anything else is not walked.</param>
    /// <param name="flags">The tag header's flags; any set and it is not walked.</param>
    private static List<Frame>? Frames(byte version, byte flags, ReadOnlySpan<byte> tag)
    {
        if (version is not (3 or 4) || flags != 0) return null;

        var frames = new List<Frame>();
        var at = 0;

        while (at + 10 <= tag.Length && tag[at] != 0)
        {
            var size = version == 4 ? Syncsafe(tag.Slice(at + 4, 4)) : BigEndian(tag.Slice(at + 4, 4));

            if (size < 0 || at + 10 + size > tag.Length) return null;

            frames.Add(new Frame(Encoding.Latin1.GetString(tag.Slice(at, 4)), at, size, tag[at + 9] != 0));
            at += 10 + size;
        }

        // What follows the frames is padding, and has to be: anything else
        // there is a tag this does not understand.
        foreach (var value in tag[at..])
        {
            if (value != 0) return null;
        }

        return frames;
    }

    private static int Syncsafe(ReadOnlySpan<byte> value) =>
        (value[0] & 0x80) != 0 || (value[1] & 0x80) != 0 || (value[2] & 0x80) != 0 || (value[3] & 0x80) != 0
            ? -1
            : (value[0] << 21) | (value[1] << 14) | (value[2] << 7) | value[3];

    private static int BigEndian(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

    private static void WriteSize(Span<byte> target, int size, byte version)
    {
        if (version == 4)
        {
            target[0] = (byte)((size >> 21) & 0x7F);
            target[1] = (byte)((size >> 14) & 0x7F);
            target[2] = (byte)((size >> 7) & 0x7F);
            target[3] = (byte)(size & 0x7F);
        }
        else
        {
            target[0] = (byte)(size >> 24);
            target[1] = (byte)(size >> 16);
            target[2] = (byte)(size >> 8);
            target[3] = (byte)size;
        }
    }
}
