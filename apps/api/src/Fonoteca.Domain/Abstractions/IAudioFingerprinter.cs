namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Turns a file into the Chromaprint fingerprint AcoustID identifies audio by.
/// </summary>
/// <remarks>
/// The producer <see cref="IAcoustIdLookup"/> has been waiting for. Everything
/// downstream of a fingerprint already exists and is tested; this is the step
/// that had no implementation, because Chromaprint has no usable .NET binding
/// and the answer is to shell out to <c>fpcalc</c> (ADR 0001).
///
/// <b>It takes a path, not a stream</b>, which breaks the pattern
/// <see cref="IAudioFileStore"/> sets everywhere else. That is deliberate:
/// <c>fpcalc</c> is a subprocess that opens the file itself, so handing it a
/// stream would mean a named pipe or a temp copy of every file in the library —
/// 240 GB of copying to preserve a convention. The domain still never sees an
/// absolute path; resolving one stays the adapter's job, as it already is for
/// the walk.
/// </remarks>
public interface IAudioFingerprinter
{
    /// <summary>Fingerprint one file, over the leading portion AcoustID indexes.</summary>
    /// <exception cref="FingerprintFailedException">
    /// The file could not be decoded, or the tool could not be run. The two are
    /// different problems and <see cref="FingerprintFailedException.IsFileFault"/>
    /// tells them apart.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller gave up.</exception>
    Task<AudioFingerprint> ComputeAsync(LibraryPath path, CancellationToken cancellationToken = default);
}

/// <summary>A file did not produce a fingerprint.</summary>
/// <remarks>
/// <see cref="IsFileFault"/> is the whole reason this type exists rather than a
/// bare exception. One truncated FLAC in a library of 100,000 marks a row and
/// the pass moves on; a misconfigured <c>Fonoteca:FpcalcPath</c> must stop the
/// pass on the first file and say so. Collapsing the two means a PATH problem
/// quietly marks the entire library unreadable, and the evidence that it was
/// never about the files is gone by the time anyone looks.
/// </remarks>
public sealed class FingerprintFailedException : Exception
{
    public FingerprintFailedException(LibraryPath path, string message, bool isFileFault)
        : base(message)
    {
        Path = path;
        IsFileFault = isFileFault;
    }

    public FingerprintFailedException(
        LibraryPath path, string message, bool isFileFault, Exception innerException)
        : base(message, innerException)
    {
        Path = path;
        IsFileFault = isFileFault;
    }

    /// <summary>Required by CA1032; prefer the constructors that say whose fault it was.</summary>
    public FingerprintFailedException()
    {
    }

    /// <inheritdoc cref="FingerprintFailedException()"/>
    public FingerprintFailedException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="FingerprintFailedException()"/>
    public FingerprintFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LibraryPath Path { get; }

    /// <summary>
    /// True when the tool ran and refused this file; false when the tool itself
    /// could not be run.
    /// </summary>
    public bool IsFileFault { get; }
}
