using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;

namespace Fonoteca.Integration.Tests;

/// <summary>The audio packets' hash: blind to tags, not to the audio.</summary>
public sealed class FfmpegAudioHasherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TagsDoNotMoveTheHashAndOtherAudioDoes()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var hasher = new FfmpegAudioHasher(new FileSystemAudioFileStore(Corpus.Directory), "ffmpeg");

        var plain = await hasher.HashAsync(Name(Corpus.Flac), Token);
        var tagged = await hasher.HashAsync(Name(Corpus.AlreadyTaggedFlac), Token);
        var other = await hasher.HashAsync(Name(Corpus.Mp3), Token);

        Assert.NotNull(plain);
        Assert.Equal(plain, tagged);
        Assert.NotEqual(plain, other);
        Assert.Null(await hasher.HashAsync(Name(Corpus.NotAudioFlac), Token));
    }

    [Fact]
    public async Task AToolThatWillNotStartIsNotTheFilesFault()
    {
        var hasher = new FfmpegAudioHasher(new FileSystemAudioFileStore(Path.GetTempPath()), "/nonexistent/ffmpeg");

        var failure = await Assert.ThrowsAsync<AudioProbeFailedException>(
            () => hasher.HashAsync(new LibraryPath("a.flac"), Token));

        Assert.False(failure.IsFileFault);
    }

    private static LibraryPath Name(string absolute) => new(Path.GetFileName(absolute));
}
