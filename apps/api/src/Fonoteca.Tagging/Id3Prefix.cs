namespace Fonoteca.Tagging;

/// <summary>
/// An ID3v2 tag glued to the front of a FLAC, and a view of the file past it.
/// </summary>
/// <remarks>
/// ATL throws <c>NullReferenceException</c> on these when the ID3 tag uses
/// unsynchronisation — ten files here, <c>Corpus.Id3PrefixedFlac</c> builds
/// one. FLAC has no place for an ID3 tag and every player reads the Vorbis
/// comment, so the owner's choice is to drop the block: ATL is handed the file
/// from <c>fLaC</c> on, reads it, and renders the staged copy without it. What
/// only the ID3 block held — a CD table of contents, the encoder, a length, its
/// own copies of the dates — is gone with it; <see cref="TagWriter"/> still
/// refuses if a picture would go, or the date the FLAC comment carries.
/// </remarks>
internal static class Id3Prefix
{
    /// <summary>How many bytes of ID3v2 sit in front of a FLAC stream, or zero.</summary>
    public static long Length(Stream stream, string container)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!container.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) return 0;

        Span<byte> head = stackalloc byte[10];
        stream.Position = 0;

        var length = 0L;

        if (stream.Read(head) == head.Length && head[..3].SequenceEqual("ID3"u8))
        {
            // The size is syncsafe, seven bits a byte, and excludes the header
            // and a footer when the flags say there is one.
            var size = 10L + ((head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9])
                + ((head[5] & 0x10) != 0 ? 10 : 0);

            Span<byte> magic = stackalloc byte[4];
            stream.Position = size;

            if (stream.Read(magic) == magic.Length && magic.SequenceEqual("fLaC"u8)) length = size;
        }

        stream.Position = 0;
        return length;
    }

    /// <summary>The stream from its FLAC stream on, or the stream itself when nothing is in front.</summary>
    public static Stream Past(Stream stream, string container) =>
        Length(stream, container) is > 0 and var length ? new OffsetStream(stream, length) : stream;

    /// <summary>A read-only view of a stream that starts <c>offset</c> bytes in.</summary>
    private sealed class OffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _offset;

        public OffsetStream(Stream inner, long offset)
        {
            _inner = inner;
            _offset = offset;
            _inner.Position = offset;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _inner.Length - _offset;

        public override long Position
        {
            get => _inner.Position - _offset;
            set => _inner.Position = value + _offset;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => origin switch
        {
            SeekOrigin.Begin => _inner.Seek(offset + _offset, SeekOrigin.Begin) - _offset,
            _ => _inner.Seek(offset, origin) - _offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
