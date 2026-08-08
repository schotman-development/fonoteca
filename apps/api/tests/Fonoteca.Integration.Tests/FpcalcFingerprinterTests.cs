using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The fpcalc adapter, against the real binary and real audio.
/// </summary>
/// <remarks>
/// No database, so no <c>PostgresCollection</c> — but real infrastructure all
/// the same, which is why these live here rather than with the pure unit tests.
/// A stubbed subprocess would prove nothing: every failure worth catching is
/// about how a real tool behaves when handed a real file.
///
/// What is deliberately <b>not</b> asserted is that two encodings of the same
/// audio fingerprint identically. Measured on this corpus, FLAC and MP3 of one
/// source agree on 120 of 130 characters — Chromaprint is designed to be
/// <i>robust</i> to encoding, meaning AcoustID's matcher tolerates the
/// difference, not that the strings are equal. Asserting equality would fail,
/// and asserting similarity would be testing somebody else's library rather
/// than this adapter.
/// </remarks>
public sealed class FpcalcFingerprinterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AGeneratedFlacYieldsAFingerprintAndItsWholeDuration()
    {
        SkipWithoutTools();

        var (fingerprinter, path) = Build(Corpus.Flac);

        var fingerprint = await fingerprinter.ComputeAsync(path, Token);

        Assert.NotEmpty(fingerprint.Value);

        // The duration AcoustID wants is the whole file's, not the 120 seconds
        // analysed. Sending the analysis window instead would make every track
        // over two minutes look like a two-minute track.
        Assert.Equal(Corpus.DurationSeconds, fingerprint.Duration.TotalSeconds, tolerance: 0.5);
    }

    /// <summary>
    /// The same bytes must give the same answer, or nothing downstream can be cached.
    /// </summary>
    /// <remarks>
    /// This is what catches an invocation that has quietly stopped being
    /// deterministic — a missing <c>-length</c>, a default that moved underneath
    /// us. It cannot catch fpcalc changing its algorithm, which is what pinning
    /// the version in docs/toolchain.md is for.
    /// </remarks>
    [Fact]
    public async Task TheSameFileFingerprintsIdenticallyEveryTime()
    {
        SkipWithoutTools();

        var (fingerprinter, path) = Build(Corpus.Flac);

        var first = await fingerprinter.ComputeAsync(path, Token);
        var second = await fingerprinter.ComputeAsync(path, Token);

        Assert.Equal(first.Value, second.Value, StringComparer.Ordinal);
        Assert.Equal(first.Duration, second.Duration);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("ogg")]
    public async Task EveryContainerInTheLibraryCanBeFingerprinted(string extension)
    {
        SkipWithoutTools();

        var source = extension switch
        {
            "mp3" => Corpus.Mp3,
            "m4a" => Corpus.M4a,
            _ => Corpus.Ogg,
        };

        var (fingerprinter, path) = Build(source);

        var fingerprint = await fingerprinter.ComputeAsync(path, Token);

        Assert.NotEmpty(fingerprint.Value);
        Assert.Equal(Corpus.DurationSeconds, fingerprint.Duration.TotalSeconds, tolerance: 0.5);
    }

    /// <summary>
    /// One unreadable file marks one row; it must not look like a broken install.
    /// </summary>
    [Theory]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("text")]
    public async Task AFileFpcalcCannotDecodeIsTheFilesFaultAndNotTheToolsFault(string kind)
    {
        SkipWithoutTools();

        var source = kind switch
        {
            "truncated" => Corpus.TruncatedFlac,
            "empty" => Corpus.EmptyFlac,
            _ => Corpus.NotAudioFlac,
        };

        var (fingerprinter, path) = Build(source);

        var failure = await Assert.ThrowsAsync<FingerprintFailedException>(
            async () => await fingerprinter.ComputeAsync(path, Token));

        Assert.True(
            failure.IsFileFault,
            $"A {kind} file must be reported as the file's fault, or one bad file marks the "
            + $"whole library unreadable. Message was: {failure.Message}");
    }

    /// <summary>
    /// The opposite mistake, and the more expensive one.
    /// </summary>
    /// <remarks>
    /// A misconfigured path fails identically on every file. Reported as a file
    /// fault it would mark 100,000 rows unreadable, and the evidence that it was
    /// never about the files would be gone by the time anyone looked.
    /// </remarks>
    [Fact]
    public async Task AMissingFpcalcIsAConfigurationProblemAndNamesTheSetting()
    {
        SkipWithoutTools();

        var store = new FileSystemAudioFileStore(Corpus.Directory);
        var fingerprinter = new FpcalcFingerprinter(store, "fpcalc-that-is-not-installed");

        var failure = await Assert.ThrowsAsync<FingerprintFailedException>(
            async () => await fingerprinter.ComputeAsync(
                new LibraryPath(Path.GetFileName(Corpus.Flac)), Token));

        Assert.False(failure.IsFileFault);
        Assert.Contains("Fonoteca:FpcalcPath", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFpcalcIsCaughtBeforeAPassStartsRatherThanOnEveryFile()
    {
        SkipWithoutTools();

        var store = new FileSystemAudioFileStore(Corpus.Directory);
        var fingerprinter = new FpcalcFingerprinter(store, "fpcalc-that-is-not-installed");

        var failure = await Assert.ThrowsAsync<FingerprintFailedException>(
            async () => await fingerprinter.VerifyAvailableAsync(Token));

        Assert.False(failure.IsFileFault);
    }

    [Fact]
    public async Task TheInstalledFpcalcReportsItsVersion()
    {
        SkipWithoutTools();

        var (fingerprinter, _) = Build(Corpus.Flac);

        var version = await fingerprinter.VerifyAvailableAsync(Token);

        Assert.Contains("fpcalc", version, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Paths are handed to the process as an argument vector, never as a command line.
    /// </summary>
    /// <remarks>
    /// Not a contrived case. <c>Down the Road I Go (2000)</c> and
    /// <c>The House Is Rockin'</c> are both in the library this was written for,
    /// and a hand-quoted command line breaks on whichever it meets first.
    /// </remarks>
    [Theory]
    [InlineData("awkward")]
    [InlineData("non-ascii")]
    public async Task APathTheShellWouldManglesSurvivesIntact(string kind)
    {
        SkipWithoutTools();

        var source = kind == "awkward" ? Corpus.AwkwardlyNamedFlac : Corpus.NonAsciiNamedFlac;
        var (fingerprinter, path) = Build(source);

        var fingerprint = await fingerprinter.ComputeAsync(path, Token);

        Assert.NotEmpty(fingerprint.Value);
    }

    [Fact]
    public async Task CancellingAFingerprintDoesNotWaitForTheToolToFinish()
    {
        SkipWithoutTools();

        var (fingerprinter, path) = Build(Corpus.Flac);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await fingerprinter.ComputeAsync(path, cancelled.Token));
    }

    /// <summary>A path outside the library root never reaches a subprocess.</summary>
    [Fact]
    public async Task APathOutsideTheLibraryRootIsRefusedBeforeAnythingIsRun()
    {
        SkipWithoutTools();

        var store = new FileSystemAudioFileStore(Corpus.Directory);
        var fingerprinter = new FpcalcFingerprinter(store, "fpcalc");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await fingerprinter.ComputeAsync(new LibraryPath("../../../etc/passwd"), Token));
    }

    private static (FpcalcFingerprinter Fingerprinter, LibraryPath Path) Build(string corpusFile)
    {
        var store = new FileSystemAudioFileStore(Corpus.Directory);
        return (new FpcalcFingerprinter(store, "fpcalc"), new LibraryPath(Path.GetFileName(corpusFile)));
    }

    private static void SkipWithoutTools()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");
    }
}
