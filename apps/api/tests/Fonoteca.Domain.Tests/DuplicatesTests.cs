using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>An album in two folders, a song twice in one, and what is neither.</summary>
public sealed class DuplicatesTests
{
    private static readonly ReleaseGroupId Rumours = ReleaseGroupId.New();
    private static readonly ReleaseGroupId BestOf = ReleaseGroupId.New();
    private static readonly RecordingId Dreams = RecordingId.New();
    private static readonly RecordingId TheChain = RecordingId.New();
    private static readonly RecordingId Songbird = RecordingId.New();

    [Fact]
    public void AnAlbumInTwoFoldersIsHeldTwiceAndTheLosslessFolderIsBetter()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Rumours/02.flac", Rumours, TheChain, Cd()),
            File("Fleetwood Mac/Rumours (MP3)/01.mp3", Rumours, Dreams, Mp3()),
            File("Fleetwood Mac/Rumours (MP3)/02.mp3", Rumours, TheChain, Mp3()),
        ];

        var album = Assert.Single(Duplicates.Albums(files));

        Assert.Equal(Rumours, album.Album);
        Assert.Equal(["Fleetwood Mac/Rumours", "Fleetwood Mac/Rumours (MP3)"], album.Folders.Select(folder => folder.Folder));
        Assert.Equal([true, false], album.Folders.Select(folder => folder.Better));
    }

    [Fact]
    public void ASongOnItsAlbumAndOnABestOfIsNotADuplicate()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Greatest Hits/05.mp3", BestOf, Dreams, Mp3()),
        ];

        Assert.Empty(Duplicates.Albums(files));
        Assert.Empty(Duplicates.Songs(files));
    }

    /// <summary>One album misfiled across four folders is a quarter in each, not four copies.</summary>
    [Fact]
    public void FoldersEachHoldingAPartOfOneAlbumAreNotItsCopies()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Tusk/01.flac", Rumours, TheChain, Cd()),
        ];

        Assert.Empty(Duplicates.Albums(files));
    }

    [Fact]
    public void AFolderMissingASongIsNotCalledBetterForItsQuality()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Rumours/02.flac", Rumours, TheChain, Cd()),
            File("Fleetwood Mac/Rumours (MP3)/01.mp3", Rumours, Dreams, Mp3()),
            File("Fleetwood Mac/Rumours (MP3)/02.mp3", Rumours, TheChain, Mp3()),
            File("Fleetwood Mac/Rumours (MP3)/03.mp3", Rumours, Songbird, Mp3()),
        ];

        Assert.All(Assert.Single(Duplicates.Albums(files)).Folders, folder => Assert.False(folder.Better));
    }

    [Fact]
    public void AFolderWithAnUnmeasuredFileHasNoBetterRival()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Rumours (MP3)/01.mp3", Rumours, Dreams, null),
        ];

        Assert.All(Assert.Single(Duplicates.Albums(files)).Folders, folder => Assert.False(folder.Better));
    }

    [Fact]
    public void ADamagedFileMakesItsFolderTheWorse()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Rumours (2)/01.flac", Rumours, Dreams, null) with { Integrity = IntegrityState.Corrupt },
        ];

        var album = Assert.Single(Duplicates.Albums(files));

        Assert.Equal("Fleetwood Mac/Rumours", album.Folders[0].Folder);
        Assert.True(album.Folders[0].Better);
        Assert.Equal(1, album.Folders[1].Damaged);
    }

    [Fact]
    public void ASongTwiceInAFolderIsADuplicateAndTheFlacIsBetter()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01 Dreams.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Rumours/01 Dreams.mp3", Rumours, Dreams, Mp3()),
            File("Fleetwood Mac/Rumours/02 The Chain.flac", Rumours, TheChain, Cd()),
        ];

        var song = Assert.Single(Duplicates.Songs(files));

        Assert.Equal("Fleetwood Mac/Rumours", song.Folder);
        Assert.Equal(Dreams, song.Recording);
        Assert.Equal(2, song.Copies.Count);
        Assert.Equal("Fleetwood Mac/Rumours/01 Dreams.flac", song.Better);
        Assert.False(song.SameAudio);
    }

    /// <summary>A pressing printing one recording twice holds it twice, on two slots: the 12-inch version.</summary>
    [Fact]
    public void ASongOnTwoSlotsOfItsPressingIsNotADuplicate()
    {
        HeldFile[] files =
        [
            File("Lionel Richie/Can't Slow Down/1-02.flac", Rumours, Dreams, Cd()) with { Track = TrackId.New() },
            File("Lionel Richie/Can't Slow Down/1-09.flac", Rumours, Dreams, Cd()) with { Track = TrackId.New() },
        ];

        Assert.Empty(Duplicates.Songs(files));
    }

    [Fact]
    public void ACopyBeyondTheSlotsItsPressingPrintsIsADuplicate()
    {
        var slot = TrackId.New();

        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()) with { Track = slot },
            File("Fleetwood Mac/Rumours/01 (copy).flac", Rumours, Dreams, Cd()) with { Track = slot },
        ];

        Assert.Single(Duplicates.Songs(files));
    }

    /// <summary>The same audio twice is not one copy better than the other, whatever the path.</summary>
    [Fact]
    public void UnidentifiedFilesOfOneAudioAreOneSongWithNoBetterCopy()
    {
        HeldFile[] files =
        [
            File("Unknown/Album/01.flac", null, null, Cd()) with { AudioHash = "abc" },
            File("Unknown/Album/01 (1).flac", null, null, Cd()) with { AudioHash = "abc" },
            File("Unknown/Album/02.flac", null, null, Cd()) with { AudioHash = "def" },
        ];

        var song = Assert.Single(Duplicates.Songs(files));

        Assert.Null(song.Recording);
        Assert.True(song.SameAudio);
        Assert.Null(song.Better);
    }

    /// <summary>An unprobed copy may be the lossless one, so the measured MP3 is not called better.</summary>
    [Fact]
    public void AMeasuredCopyIsNotBetterThanOneNothingHasMeasured()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.mp3", Rumours, Dreams, Mp3()),
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, null),
        ];

        Assert.Null(Assert.Single(Duplicates.Songs(files)).Better);
    }

    [Fact]
    public void AnIntactCopyIsBetterThanADamagedOneWhateverItsFormat()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, null) with { Integrity = IntegrityState.Corrupt },
            File("Fleetwood Mac/Rumours/01.mp3", Rumours, Dreams, Mp3()),
        ];

        Assert.Equal("Fleetwood Mac/Rumours/01.mp3", Assert.Single(Duplicates.Songs(files)).Better);
    }

    /// <summary>Two folders alike on every count: neither is marked, rather than the first by path.</summary>
    [Fact]
    public void TwoFoldersAlikeOnEveryCountHaveNoBetter()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Rumours (2)/01.flac", Rumours, Dreams, Cd()),
        ];

        Assert.All(Assert.Single(Duplicates.Albums(files)).Folders, folder => Assert.False(folder.Better));
    }

    /// <summary>Better quality does not outweigh a damaged file.</summary>
    [Fact]
    public void AFolderWithADamagedFileIsNotBetterForItsQuality()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Fleetwood Mac/Rumours/02.flac", Rumours, TheChain, null) with { Integrity = IntegrityState.Corrupt },
            File("Fleetwood Mac/Rumours (MP3)/01.mp3", Rumours, Dreams, Mp3()),
            File("Fleetwood Mac/Rumours (MP3)/02.mp3", Rumours, TheChain, Mp3()),
        ];

        Assert.All(Assert.Single(Duplicates.Albums(files)).Folders, folder => Assert.False(folder.Better));
    }

    /// <summary>A folder mostly of something else is not this album's, and loose files at the root are no album folder.</summary>
    [Fact]
    public void AFolderTheAlbumIsNotMostOfAndTheRootAreNotItsCopies()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Various/Mixtape/01.flac", Rumours, Dreams, Cd()),
            File("Various/Mixtape/02.flac", null, null, Cd()),
            File("Various/Mixtape/03.flac", null, null, Cd()),
            File("01 Dreams.flac", Rumours, Dreams, Cd()),
        ];

        Assert.Empty(Duplicates.Albums(files));
    }

    /// <summary>Half a folder is not most of it, so it is not the album's.</summary>
    [Fact]
    public void AFolderHalfOfAnAlbumIsNotItsCopy()
    {
        HeldFile[] files =
        [
            File("Fleetwood Mac/Rumours/01.flac", Rumours, Dreams, Cd()),
            File("Various/Mixtape/01.flac", Rumours, Dreams, Cd()),
            File("Various/Mixtape/02.flac", null, null, Cd()),
        ];

        Assert.Empty(Duplicates.Albums(files));
    }

    private static HeldFile File(string path, ReleaseGroupId? album, RecordingId? recording, AudioQuality? quality) =>
        new(path, album, recording, null, null, quality, null, quality is null ? IntegrityState.Unchecked : IntegrityState.Intact, 1_000);

    private static AudioQuality Mp3() => new()
    {
        Codec = "mp3",
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = 320_000,
        IsLossless = false,
    };

    private static AudioQuality Cd() => new()
    {
        Codec = "flac",
        SampleRateHz = 44_100,
        Channels = 2,
        BitDepth = 16,
        BitrateBps = 900_000,
        IsLossless = true,
    };
}
