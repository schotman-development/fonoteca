using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The scanner's filter. Everything it rejects is a file that will never be
/// catalogued, so the cases below are the ones worth being sure about.
/// </summary>
public sealed class AudioFormatsTests
{
    [Theory]
    [InlineData("Miles Davis/Kind of Blue/01 So What.flac")]
    [InlineData("track.mp3")]
    [InlineData("track.m4a")]
    [InlineData("track.opus")]
    [InlineData("track.dsf")]
    public void RecognisesAudioExtensions(string path) =>
        Assert.True(AudioFormats.IsAudioFile(path));

    /// <summary>
    /// Rips arrive with every casing there is, and a case-sensitive match would
    /// silently skip a whole disc ripped on Windows.
    /// </summary>
    [Theory]
    [InlineData("TRACK.FLAC")]
    [InlineData("Track.Mp3")]
    public void ExtensionMatchingIsCaseInsensitive(string path) =>
        Assert.True(AudioFormats.IsAudioFile(path));

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("album.log")]
    [InlineData("album.cue")]
    [InlineData("playlist.m3u")]
    [InlineData(".DS_Store")]
    [InlineData("README")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsEverythingElse(string? path) =>
        Assert.False(AudioFormats.IsAudioFile(path));

    /// <summary>
    /// A directory named for a format is common in libraries organised by
    /// quality ("Kind of Blue [24-96.flac]/"), and the files inside it are the
    /// ones that decide, not the folder.
    /// </summary>
    [Fact]
    public void OnlyTheLastPathSegmentDecides()
    {
        Assert.False(AudioFormats.IsAudioFile("Kind of Blue [24-96.flac]/cover.jpg"));
        Assert.True(AudioFormats.IsAudioFile("Kind of Blue [24-96.flac]/01 So What.flac"));
    }

    /// <summary>
    /// A dotfile named after a format is not a track, and macOS resource forks
    /// sit beside every real file on any library that has ever touched a Mac —
    /// cataloguing them would double the file count with unreadable stubs.
    /// </summary>
    [Theory]
    [InlineData(".flac")]
    [InlineData("Kind of Blue/._01 So What.flac")]
    public void RejectsDotfilesAndResourceForks(string path) =>
        Assert.False(AudioFormats.IsAudioFile(path));

    [Fact]
    public void ExtensionsAreExposedWithoutLeadingDots() =>
        Assert.All(AudioFormats.Extensions, e => Assert.DoesNotContain('.', e));
}
