using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Listens to a file for its tempo and its key.
/// </summary>
/// <remarks>
/// <see cref="IAudioMeasurer"/>'s sibling, run by the same pass, and not a
/// widening of it: another tool, with its own way of failing. Failures are split
/// by whose fault they are with the probe's own exception.
/// </remarks>
public interface ITempoAndKeyMeasurer
{
    /// <returns>Null when the file holds no audio the tool could listen to.</returns>
    /// <exception cref="AudioProbeFailedException">
    /// The tool could not run (<c>IsFileFault</c> false), or this file would not decode.
    /// </exception>
    Task<TempoAndKey?> MeasureAsync(LibraryPath path, CancellationToken cancellationToken = default);
}
