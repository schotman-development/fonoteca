namespace Fonoteca.Domain.Catalogue;

/// <summary>A codec a stream can be re-encoded to, named as a client asks for it and as ffprobe names it.</summary>
public enum StreamCodec
{
    Mp3,
    Opus,
    Aac,
}

/// <summary>
/// What a Subsonic stream is re-encoded to, or null where the file goes out as
/// it is.
/// </summary>
/// <remarks>
/// <b>The original wherever nothing smaller was asked for.</b> <c>raw</c>, or
/// no format and no bitrate, is the file. A bitrate alone is MP3, which every
/// Subsonic client plays, unless the file is an MP3 within it. A format
/// alone is that format at a rate nobody hears as different from the source
/// (<see cref="DefaultKilobitsPerSecond"/>). A format the server does not make
/// is the protocol's "preferred" format, not a demand, so it counts as no
/// format.
///
/// <b>A file already within what was asked goes out as it is</b>: in the
/// format it would be sent as, at or below the rate asked — the rate itself,
/// not the 320 kbps it is held to. Re-encoding it would only lose quality — except when seeking, since the
/// original cannot start at an offset and a client seeking in a transcode
/// expects the transcode.
/// </remarks>
public sealed record Transcode(StreamCodec Codec, int KilobitsPerSecond)
{
    /// <summary>The lowest and highest of the rates Subsonic lists.</summary>
    public const int MinimumKilobitsPerSecond = 32;

    public const int MaximumKilobitsPerSecond = 320;

    /// <summary>The format a bitrate alone is sent as.</summary>
    public const StreamCodec DefaultCodec = StreamCodec.Mp3;

    /// <summary>The asked format's name and ffprobe's name for the codec, which are the same.</summary>
    public string Name => Codec.ToString().ToLowerInvariant();

    /// <summary>What the bytes are: Opus goes in Ogg, AAC as ADTS frames.</summary>
    public string MediaType => Codec switch
    {
        StreamCodec.Mp3 => "audio/mpeg",
        StreamCodec.Opus => "audio/ogg",
        StreamCodec.Aac => "audio/aac",
        _ => throw new ArgumentOutOfRangeException(nameof(Codec), Codec, null),
    };

    /// <summary>Each format's rate when none was asked for, transparent to most ears.</summary>
    public static int DefaultKilobitsPerSecond(StreamCodec codec) => codec switch
    {
        StreamCodec.Mp3 => 320,
        StreamCodec.Opus => 160,
        StreamCodec.Aac => 256,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
    };

    /// <param name="format">The <c>format</c> sent, or null.</param>
    /// <param name="maxBitRate">The <c>maxBitRate</c> sent, in kbps; 0 is no limit.</param>
    /// <param name="source">The file's probed quality, or null where nothing probed it.</param>
    /// <param name="seeking">Whether the stream starts at an offset.</param>
    public static Transcode? For(string? format, int maxBitRate, AudioQuality? source, bool seeking)
    {
        if (string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase)) return null;

        StreamCodec? asked = format?.ToLowerInvariant() switch
        {
            "mp3" => StreamCodec.Mp3,
            "opus" => StreamCodec.Opus,
            "aac" => StreamCodec.Aac,
            _ => null,
        };

        if (asked is null && maxBitRate <= 0) return null;

        var transcode = new Transcode(
            asked ?? DefaultCodec,
            maxBitRate > 0
                ? Math.Clamp(maxBitRate, MinimumKilobitsPerSecond, MaximumKilobitsPerSecond)
                : DefaultKilobitsPerSecond(asked!.Value));

        var fits = source is not null
            && source.BitrateBps <= Math.Max(maxBitRate, transcode.KilobitsPerSecond) * 1000L
            && string.Equals(source.Codec, transcode.Name, StringComparison.OrdinalIgnoreCase);

        return fits && !seeking ? null : transcode;
    }

    /// <summary>
    /// A length no transcode of <paramref name="duration"/> runs past: the rate,
    /// the codec's overshoot and 3 kbps of framing over it, and the headers.
    /// </summary>
    /// <remarks>
    /// Measured on ffmpeg 8.0. MP3 lands on its rate exactly and Opus in
    /// constrained VBR up to 1.9% over, across five of the library's files and
    /// two made tones; 3% covers both. ffmpeg's own AAC encoder holds its rate
    /// loosely: across sixteen of the library's files it ran up to 7.4% over
    /// at 128 kbps and 4.3% at 320, ADTS's 7 bytes a frame included, so it is
    /// given 10%.
    /// </remarks>
    public long EstimatedLength(TimeSpan duration) =>
        (long)Math.Ceiling(Math.Max(duration.TotalSeconds, 0) * (KilobitsPerSecond * Overshoot + 3) * 1000 / 8) + HeaderAllowance;

    private double Overshoot => Codec == StreamCodec.Aac ? 1.10 : 1.03;

    private const int HeaderAllowance = 4096;
}
