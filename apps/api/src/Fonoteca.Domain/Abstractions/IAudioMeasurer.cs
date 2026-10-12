using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Decodes a file through analysis filters and reports what the audio is like.
/// </summary>
/// <remarks>
/// <see cref="IAudioProbe"/>'s sibling and not a widening of it: the probe's
/// decode is also the integrity check, which reads anything the decoder says as
/// an objection, and analysis filters talk on the same channel. Failures are
/// split by whose fault they are with the probe's own exception.
/// </remarks>
public interface IAudioMeasurer
{
    /// <returns>Null when the file holds no audio a decoder could measure.</returns>
    /// <exception cref="AudioProbeFailedException">
    /// The tool could not run (<c>IsFileFault</c> false), or this file would not decode.
    /// </exception>
    Task<AudioMeasurement?> MeasureAsync(LibraryPath path, CancellationToken cancellationToken = default);
}
