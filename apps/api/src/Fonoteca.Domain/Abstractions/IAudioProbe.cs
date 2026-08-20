using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Measures what a file actually contains: codec, bitrate, rate, depth, length.
/// </summary>
/// <remarks>
/// <b>A third opinion, and it exists because the first two are not qualified to
/// give one.</b> ADR 0002 pairs two tag libraries so that a claim about a file
/// has an independent witness, and that pairing is about <i>tags</i>. Neither
/// library is a decoder, and asked about the audio they answer from headers they
/// parse in passing:
///
/// <list type="bullet">
/// <item>a VBR MP3 with no Xing header reads back at the first frame's bitrate
/// and a duration extrapolated from it — measured against a real library file,
/// 64 kbps and 5:35 for audio that is 132 kbps and 2:47;</item>
/// <item>a FLAC truncated to a fifth of its length still carries an intact
/// STREAMINFO, so it reads back as its original duration at a bitrate computed
/// against the bytes that remain — 3 kbps, and typed lossless CD.</item>
/// </list>
///
/// Both would be written into <see cref="AudioQuality"/>, whose stated job is
/// deciding which copy of a duplicate to keep and whether a candidate is an
/// upgrade. A wrong number there is not a display bug; it is a rule reaching the
/// wrong conclusion with nothing recording that its input was nonsense.
///
/// So the measurement comes from a decoder. <c>ffprobe</c> is already a declared
/// dependency of this project, the layering table already lists probing under
/// <c>Fonoteca.Ingest</c>, and it is the third tool the tag-write tests already
/// verify through for exactly this reason: two libraries agreeing only proves
/// they agree.
///
/// <b>It takes a path, not a stream</b>, for the reason
/// <see cref="IAudioFingerprinter"/> does: the tool is a subprocess that opens
/// the file itself, and honouring the stream convention would mean a named pipe
/// or a temp copy of every file in the library. Resolving the absolute path
/// stays the adapter's job.
/// </remarks>
public interface IAudioProbe
{
    /// <summary>
    /// Measure one file, having read its frames rather than only its header.
    /// </summary>
    /// <remarks>
    /// The numbers are still the container's declarations — that is what a
    /// container is for. What reading the frames adds is
    /// <see cref="AudioProbeReading.Complaint"/>, which is the only thing that
    /// distinguishes a file lying about its own length from a short one.
    /// </remarks>
    /// <returns>
    /// Null when the tool ran and found no audio it could describe — a text file
    /// named <c>.flac</c>, a container with no audio stream. That is an answer
    /// about the file rather than a failure.
    /// </returns>
    /// <exception cref="AudioProbeFailedException">
    /// The file could not be opened or the tool could not be run.
    /// <see cref="AudioProbeFailedException.IsFileFault"/> tells the two apart,
    /// for the same reason <see cref="FingerprintFailedException"/> does.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller gave up.</exception>
    Task<AudioProbeReading?> ProbeAsync(LibraryPath path, CancellationToken cancellationToken = default);
}

/// <summary>What one file measured, and whether the decoder was happy about it.</summary>
/// <param name="Complaint">
/// What the decoder said while reading the stream, or null if it said nothing.
/// </param>
/// <remarks>
/// <b>The two are separate because a measurement and a trustworthy measurement
/// are different things, and only one of them may be written down.</b>
///
/// A container states its own length in a header, and a file truncated after
/// that header goes on stating it: a FLAC cut to a quarter of its bytes reports
/// its original duration and a bitrate computed against what remains — a quarter
/// of the real one, still typed lossless, and the tool exits <b>zero</b>.
/// Nothing about that reading looks wrong. It is only wrong.
///
/// So the frames are read as well as the header, and whatever the decoder
/// complains about on the way through comes back here. It does not correct the
/// numbers; nothing short of a full integrity pass can. It is the one signal
/// that says not to believe them. Measured across sixty
/// files of a real library, exactly one produced a complaint, and that one has a
/// genuine defect — so treating a complaint as "show this, do not remember it"
/// costs a cached number on roughly one file in sixty and never lets a number
/// the decoder objected to reach a rule that ranks files by quality.
///
/// It is deliberately not a verdict on the file. Whether these bytes are intact
/// is <c>IntegrityState</c>'s question and needs its own pass; this is one
/// sentence from a decoder that was reading the file anyway.
/// </remarks>
public sealed record AudioProbeReading(AudioQuality Quality, string? Complaint)
{
    /// <summary>True when the decoder read the whole stream without remark.</summary>
    public bool DecodedCleanly => Complaint is null;
}

/// <summary>A file could not be measured.</summary>
/// <remarks>
/// <see cref="IsFileFault"/> carries the same distinction
/// <see cref="FingerprintFailedException"/> exists for: one unreadable file is a
/// row, a missing <c>ffprobe</c> is a configuration error, and merging them
/// makes a PATH problem look like a library full of broken audio.
/// </remarks>
public sealed class AudioProbeFailedException : Exception
{
    public AudioProbeFailedException(LibraryPath path, string message, bool isFileFault)
        : base(message)
    {
        Path = path;
        IsFileFault = isFileFault;
    }

    public AudioProbeFailedException(
        LibraryPath path, string message, bool isFileFault, Exception innerException)
        : base(message, innerException)
    {
        Path = path;
        IsFileFault = isFileFault;
    }

    /// <summary>Required by CA1032; prefer the constructors that say whose fault it was.</summary>
    public AudioProbeFailedException() { }

    public AudioProbeFailedException(string message) : base(message) { }

    public AudioProbeFailedException(string message, Exception innerException)
        : base(message, innerException) { }

    public LibraryPath Path { get; }

    /// <summary>True when these bytes are the problem, false when the tool is.</summary>
    public bool IsFileFault { get; }
}
