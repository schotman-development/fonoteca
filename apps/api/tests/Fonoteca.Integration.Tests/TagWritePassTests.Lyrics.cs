using Fonoteca.Api.Library;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The words: LRCLIB's into the tag as they came, a lyrics file's where LRCLIB
/// has none, and every lyrics file beside a written track to the trash.
/// </summary>
public sealed partial class TagWritePassTests
{
    /// <summary>LRCLIB's own shape: a space after each time, and a blank timed line for a break.</summary>
    private const string Timed = "[00:00.78] So what\n[00:12.80] What's the use\n[00:27.10] \n[00:30.00] Of it all";

    [Theory]
    [InlineData(".flac")]
    [InlineData(".mp3")]
    [InlineData(".m4a")]
    public async Task TheCataloguesWordsGoInAsTheyCameAndTheLyricsFileGoesToTheTrash(string extension)
    {
        SkipWithoutTools();
        var track = $"{Album}/01 track{extension}";
        await SeedAsync((track, 1));
        await SeedLyricsAsync(Timed);

        var advert = Path.Combine(_root, Album, "01 track.lrc");
        await File.WriteAllTextAsync(advert, "www.t.me/pmedia_music", Token);

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        Assert.Equal(Timed, StoredLyrics(track));
        Assert.False(File.Exists(advert));
        Assert.Equal(
            "www.t.me/pmedia_music",
            await File.ReadAllTextAsync(Directory.EnumerateFiles(_root + "-trash", "01 track.lrc", SearchOption.AllDirectories).Single(), Token));

        // Read back as stored, so a second run has nothing to do.
        var again = await RunAsync(TagWriteScope.Library, services);
        Assert.Equal(0, again.Written);
        Assert.Equal(1, again.Unchanged);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Empty(undone.Problems);
        Assert.Equal("www.t.me/pmedia_music", await File.ReadAllTextAsync(advert, Token));
        Assert.Null(StoredLyrics(track));
    }

    /// <summary>Where LRCLIB has nothing, a lyrics file's own words go in before it goes; with mutation off, nothing moves.</summary>
    [Fact]
    public async Task ALyricsFilesOwnWordsGoInWhereLrclibHasNone()
    {
        SkipWithoutTools();
        var track = $"{Album}/01 track.flac";
        await SeedAsync((track, 1));

        const string words = "So what\nWhat's the use\nOf it all";
        var beside = Path.Combine(_root, Album, "01 track.lrc");
        await File.WriteAllTextAsync(beside, "﻿" + words + "\n", Token);

        await RunAsync(TagWriteScope.Library, Build(allowMutation: false));
        Assert.True(File.Exists(beside));
        Assert.Null(StoredLyrics(track));

        await RunAsync(TagWriteScope.Library, Build());

        Assert.Equal(words, StoredLyrics(track));
        Assert.False(File.Exists(beside));
    }

    /// <summary>LRCLIB's words are the ones that go in, and the lyrics file goes all the same.</summary>
    [Fact]
    public async Task LrclibsWordsWinOverALyricsFilesOwn()
    {
        SkipWithoutTools();
        var track = $"{Album}/01 track.flac";
        await SeedAsync((track, 1));
        await SeedLyricsAsync(Timed);

        var beside = Path.Combine(_root, Album, "01 track.lrc");
        await File.WriteAllTextAsync(beside, "Other words\nFrom another rip\nEntirely", Token);

        await RunAsync(TagWriteScope.Library);

        Assert.Equal(Timed, StoredLyrics(track));
        Assert.False(File.Exists(beside));
    }

    /// <summary>
    /// A lyrics file beside a track that already says everything goes only with
    /// mutation on, and Undo of that run, which moved nothing else, puts it back.
    /// </summary>
    [Fact]
    public async Task ALyricsFileBesideAWrittenTrackGoesOnlyWithMutationOnAndUndoBringsItBack()
    {
        SkipWithoutTools();
        var track = $"{Album}/01 track.flac";
        await SeedAsync((track, 1));
        await SeedLyricsAsync(Timed);

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var beside = Path.Combine(_root, Album, "01 track.lrc");
        await File.WriteAllTextAsync(beside, "www.t.me/pmedia_music", Token);

        await RunAsync(TagWriteScope.Library, Build(allowMutation: false));
        Assert.True(File.Exists(beside));

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Unchanged);
        Assert.False(File.Exists(beside));

        Assert.Equal(TagUndoStatus.Undone, (await UndoAsync(services, Album)).Status);
        Assert.Equal("www.t.me/pmedia_music", await File.ReadAllTextAsync(beside, Token));
        Assert.Equal(Timed, StoredLyrics(track));
    }

    /// <summary>A rip group's advert is not words: nothing goes in, and the advert goes to the trash.</summary>
    [Fact]
    public async Task AnAdvertIsNoWordsAndGoesToTheTrash()
    {
        SkipWithoutTools();
        var track = $"{Album}/01 track.flac";
        await SeedAsync((track, 1));

        var advert = Path.Combine(_root, Album, "01 track.lrc");
        await File.WriteAllTextAsync(advert, "www.t.me/pmedia_music\nkmn", Token);

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library)).Written);

        Assert.Null(StoredLyrics(track));
        Assert.False(File.Exists(advert));
    }

    /// <summary>
    /// A file there for its ReplayGain alone takes a lyrics file's words, which
    /// are its own, and not LRCLIB's, which are part of the answer it waits for.
    /// </summary>
    [Fact]
    public async Task AFileTheCatalogueCannotPlaceTakesItsLyricsFilesWordsAndNotLrclibs()
    {
        SkipWithoutTools();
        var loose = await SeedUnattributedAsync("Unknown/loose.flac");
        await SeedLyricsAsync(Timed);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.Measurement = new AudioMeasurement { IntegratedLufs = -12.5, SamplePeakDbfs = -0.5, DurationSeconds = 12 };
            file.MeasuredUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Token);
        }

        const string words = "So what\nWhat's the use\nOf it all";
        var beside = Path.Combine(_root, "Unknown", "loose.lrc");
        await File.WriteAllTextAsync(beside, words, Token);

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library)).Written);

        Assert.Equal(words, StoredLyrics(loose));
        Assert.False(File.Exists(beside));
    }

    /// <summary>
    /// Timed words a file already holds stay byte for byte when a write changes something else.
    /// </summary>
    /// <remarks>
    /// ATL re-renders lyrics from its own parse on every save: the space after
    /// each time goes and a blank timed line moves to the top. Measured on
    /// ATL 7.16 before the writer put them back as stored.
    /// </remarks>
    [Fact]
    public async Task TimedWordsAFileHoldsStayAsStoredWhenTheWriteChangesSomethingElse()
    {
        SkipWithoutTools();
        var track = $"{Album}/01 track.flac";
        await SeedAsync((track, 1));

        var full = Path.Combine(_root, track);
        var tagged = new ATL.Track(full);
        tagged.Lyrics = [new ATL.LyricsInfo { UnsynchronizedLyrics = Timed }];
        Assert.True(tagged.Save());

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library)).Written);

        Assert.Equal(Timed, StoredLyrics(track));
        Assert.Equal("So What", (await TagsAsync(track)).Fields["TITLE"]);
    }

    /// <summary>The same words under foobar2000's name go, or Navidrome shows them twice.</summary>
    [Fact]
    public async Task WordsTheCatalogueGivesAreTheFilesOnlyWords()
    {
        SkipWithoutTools();
        var track = $"{Album}/01 track.flac";
        await SeedAsync((track, 1));
        await SeedLyricsAsync(Timed);

        var full = Path.Combine(_root, track);
        var tagged = new ATL.Track(full);
        tagged.AdditionalFields[CatalogueTags.UnsyncedLyrics] = "Old words";
        Assert.True(tagged.Save());

        await RunAsync(TagWriteScope.Library);

        using var file = TagLib.File.Create(full);
        Assert.Empty(((TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph)).GetField(CatalogueTags.UnsyncedLyrics));
        Assert.Equal(Timed, file.Tag.Lyrics);
    }

    private async Task SeedLyricsAsync(string synced)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        db.RecordingLyrics.Add(new RecordingLyrics
        {
            RecordingId = (await db.Recordings.SingleAsync(Token)).Id,
            Synced = synced,
            Plain = LyricsText.Plain(synced),
            LrclibId = 1,
            MatchedBy = "album",
            SavedUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(Token);
    }

    /// <summary>The words as the file stores them, as TagLib# reads them.</summary>
    private string? StoredLyrics(string path)
    {
        using var file = TagLib.File.Create(Path.Combine(_root, path));
        return string.IsNullOrEmpty(file.Tag.Lyrics) ? null : file.Tag.Lyrics;
    }
}
