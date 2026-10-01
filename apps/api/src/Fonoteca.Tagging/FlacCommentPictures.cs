using System.Buffers.Binary;
using System.Text;

namespace Fonoteca.Tagging;

/// <summary>
/// Covers a FLAC keeps inside its Vorbis comment, put back after ATL saves as
/// picture blocks — where every player looks.
/// </summary>
/// <remarks>
/// A <c>METADATA_BLOCK_PICTURE</c> comment is a picture block's body in
/// base64. ATL reads it and drops it on save — it matches pictures to blocks by
/// position, and the comment's has none — so 51 files here with a larger cover
/// in the comment than in their picture block were refused. The owner's choice
/// is to move it: the same bytes, as a block of its own after the last
/// metadata block, the audio moving up to make room.
/// </remarks>
internal static class FlacCommentPictures
{
    private const byte VorbisComment = 4;

    private const byte Picture = 6;

    private const string Field = "METADATA_BLOCK_PICTURE";

    /// <summary>The picture-block bodies the Vorbis comment holds, of a stream starting at <c>fLaC</c>.</summary>
    public static List<byte[]> Read(Stream flac)
    {
        ArgumentNullException.ThrowIfNull(flac);

        var found = new List<byte[]>();

        foreach (var block in Blocks(flac).Where(block => block.Type == VorbisComment))
        {
            var comment = ReadAt(flac, block.At, block.Length);
            var at = 0;

            if (!Take(comment, ref at, out var vendor) || !Skip(comment, ref at, vendor)) continue;
            if (!Take(comment, ref at, out var count)) continue;

            for (var index = 0; index < count && Take(comment, ref at, out var length); index++)
            {
                if (at + length > comment.Length) break;

                var entry = Encoding.UTF8.GetString(comment, at, length);
                at += length;

                var equals = entry.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0 || !entry.AsSpan(0, equals).Equals(Field, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    found.Add(Convert.FromBase64String(entry[(equals + 1)..]));
                }
                catch (FormatException)
                {
                    // Not a picture anyone could read back; nothing to move.
                }
            }
        }

        return found;
    }

    /// <summary>Adds a picture block for each body whose image no picture block in the stream carries.</summary>
    public static void Restore(Stream flac, IReadOnlyList<byte[]> pictures)
    {
        ArgumentNullException.ThrowIfNull(flac);
        ArgumentNullException.ThrowIfNull(pictures);

        if (pictures.Count == 0) return;

        var blocks = Blocks(flac);
        if (blocks.Count == 0) return;

        var held = blocks
            .Where(block => block.Type == Picture)
            .Select(block => Image(ReadAt(flac, block.At, block.Length)))
            .ToList();

        var missing = pictures.Where(body => !held.Any(image => image.AsSpan().SequenceEqual(Image(body)))).ToList();
        if (missing.Count == 0) return;

        var added = new MemoryStream();

        for (var index = 0; index < missing.Count; index++)
        {
            var body = missing[index];
            var header = new byte[4];

            header[0] = (byte)(Picture | (index == missing.Count - 1 ? 0x80 : 0));
            header[1] = (byte)(body.Length >> 16);
            header[2] = (byte)(body.Length >> 8);
            header[3] = (byte)body.Length;

            added.Write(header);
            added.Write(body);
        }

        // The last block stops being the last; the new ones go after it.
        var last = blocks[^1];
        var end = last.At + last.Length;

        flac.Position = last.At - 4;
        var flag = (byte)flac.ReadByte();
        flac.Position = last.At - 4;
        flac.WriteByte((byte)(flag & 0x7F));

        Streams.Shift(flac, end, (int)added.Length);

        flac.Position = end;
        flac.Write(added.GetBuffer().AsSpan(0, (int)added.Length));
        flac.Flush();
    }

    private sealed record Block(byte Type, long At, int Length);

    /// <summary>The metadata blocks after <c>fLaC</c>, or none when the stream does not start with one.</summary>
    private static List<Block> Blocks(Stream flac)
    {
        var blocks = new List<Block>();
        var head = new byte[4];

        flac.Position = 0;
        if (flac.Read(head) != 4 || !head.AsSpan().SequenceEqual("fLaC"u8)) return blocks;

        var at = 4L;

        while (true)
        {
            flac.Position = at;
            if (flac.Read(head) != 4) return [];

            var length = (head[1] << 16) | (head[2] << 8) | head[3];
            if (at + 4 + length > flac.Length) return [];

            blocks.Add(new Block((byte)(head[0] & 0x7F), at + 4, length));
            at += 4 + length;

            if ((head[0] & 0x80) != 0) return blocks;
        }
    }

    private static byte[] ReadAt(Stream flac, long at, int length)
    {
        var bytes = new byte[length];
        flac.Position = at;
        flac.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A picture body's image bytes, or the whole body where it does not parse.</summary>
    private static byte[] Image(byte[] body)
    {
        var at = 4;

        if (Take(body, ref at, out var mime, bigEndian: true) && Skip(body, ref at, mime)
            && Take(body, ref at, out var description, bigEndian: true) && Skip(body, ref at, description)
            && Skip(body, ref at, 16)
            && Take(body, ref at, out var length, bigEndian: true) && at + length <= body.Length)
        {
            return body[at..(at + length)];
        }

        return body;
    }

    private static bool Take(byte[] bytes, ref int at, out int value, bool bigEndian = false)
    {
        value = 0;
        if (at + 4 > bytes.Length) return false;

        var raw = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));

        if (raw > int.MaxValue) return false;

        value = (int)raw;
        at += 4;
        return true;
    }

    private static bool Skip(byte[] bytes, ref int at, int count)
    {
        if (count < 0 || at + count > bytes.Length) return false;
        at += count;
        return true;
    }
}
