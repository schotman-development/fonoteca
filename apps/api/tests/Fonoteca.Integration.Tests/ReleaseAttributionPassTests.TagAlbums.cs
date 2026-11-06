using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>Albums made from the files' own tags, at the end of the attribution pass (client issue #65).</summary>
public sealed partial class ReleaseAttributionPassTests
{
    private const string CoversFolder = "Pepita Salim/Covers";

    private static readonly Mbid PepitaSalim = Mb("3611e48b-9535-4387-9ea7-0c432d4d9717");

    /// <summary>
    /// Two singles, each its own album tag, marked "never released" by a
    /// person: one album named after the folder, songs as the files say,
    /// billed to the artist the files' MBID names, and nobody's answer moved.
    /// </summary>
    [Fact]
    public async Task AFolderNobodyCouldIdentifyIsOneAlbumFromItsTags()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await TaggedAsync($"{CoversFolder}/01 Gravity - Pepita Salim.flac", "Gravity", "Gravity", track: 1);
        await TaggedAsync($"{CoversFolder}/02 True Colors - Pepita Salim.flac", "True Colors", "True Colors", track: 1);
        await UnidentifiedAsync(unreleased: true);

        // Two of that name: only the MBID the files state can say which.
        ArtistId named;
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var right = new Artist { Id = ArtistId.New(), Name = "Pepita Salim", Mbid = PepitaSalim };
            db.Artists.Add(new Artist { Id = ArtistId.New(), Name = "Pepita Salim", Mbid = Mb("99999999-9999-4999-8999-999999999999") });
            db.Artists.Add(right);
            await db.SaveChangesAsync(Token);
            named = right.Id;
        }

        await RunAsync(new StubCatalogue());

        await using var read = PostgresFixture.CreateContext(_connectionString);

        var group = await read.ReleaseGroups.SingleAsync(Token);
        Assert.Equal("Covers", group.Title);
        Assert.Equal(CoversFolder, group.TagFolder);
        Assert.Null(group.Mbid);

        var release = await read.Releases.SingleAsync(Token);
        Assert.Equal(group.Id, release.ReleaseGroupId);
        Assert.Equal(2, release.TrackCount);
        Assert.Equal(named, (await read.ArtistCredits.SingleAsync(credit => credit.ReleaseId == release.Id, Token)).ArtistId);

        var files = await read.MediaFiles.Include(file => file.Recording).Include(file => file.Track).OrderBy(file => file.Path).ToListAsync(Token);

        Assert.Equal(["Gravity", "True Colors"], files.Select(file => file.Recording!.Title));
        Assert.Equal([1, 2], files.Select(file => file.Track!.Position));
        Assert.All(files, file =>
        {
            Assert.Equal(group.Id, file.ReleaseGroupId);
            Assert.Equal(release.Id, file.ReleaseId);

            // The person's answer, as they left it.
            Assert.Equal(AcoustIdOutcome.Unreleased, file.AcoustIdOutcome);
            Assert.Equal(ReleaseAttributionOutcome.NotAttempted, file.AttributionOutcome);
            Assert.Null(file.ReleaseLookupUtc);
        });

        Assert.Equal(2, await read.ArtistCredits.CountAsync(credit => credit.RecordingId != null && credit.ArtistId == named, Token));

        // Nothing MusicBrainz could be asked about, so no pending work it never finishes.
        Assert.Equal(0, await Build(new StubCatalogue()).CountPendingAsync(Token));

        // Never written back into the files it came from.
        Assert.Empty(await TagWriteService.Answered(read.MediaFiles).ToListAsync(Token));
    }

    /// <summary>The files are leading: a retag is followed on the next run, and the album and recordings keep their ids.</summary>
    [Fact]
    public async Task ARetaggedFileRenamesItsSongInPlace()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var path = $"{CoversFolder}/01 Gravity - Pepita Salim.flac";
        await TaggedAsync(path, "Gravity", "Covers", track: 1);
        await UnidentifiedAsync(unreleased: false);

        await RunAsync(new StubCatalogue());

        (ReleaseGroupId Group, RecordingId Recording) before;
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            before = (file.ReleaseGroupId!.Value, file.RecordingId!.Value);
        }

        await TaggedAsync(path, "Gravity (Live)", "Covers", track: 1);
        await RunAsync(new StubCatalogue());

        await using var read = PostgresFixture.CreateContext(_connectionString);
        var after = await read.MediaFiles.Include(file => file.Recording).SingleAsync(Token);

        Assert.Equal(before, (after.ReleaseGroupId!.Value, after.RecordingId!.Value));
        Assert.Equal("Gravity (Live)", after.Recording!.Title);
        Assert.Single(await read.ReleaseGroups.ToListAsync(Token));
        Assert.Single(await read.Tracks.ToListAsync(Token));
    }

    /// <summary>A file identification has not answered yet waits for it, rather than being filed a moment before MusicBrainz could.</summary>
    [Fact]
    public async Task AFileStillWaitingOnIdentificationIsNotFiled()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await TaggedAsync($"{CoversFolder}/01 Gravity - Pepita Salim.flac", "Gravity", "Covers", track: 1);

        await RunAsync(new StubCatalogue());

        await using var read = PostgresFixture.CreateContext(_connectionString);
        Assert.Null((await read.MediaFiles.SingleAsync(Token)).ReleaseGroupId);
        Assert.Empty(await read.ReleaseGroups.ToListAsync(Token));
    }

    /// <summary>A person's later filing replaces the tag album for that file, and the next run leaves it there.</summary>
    [Fact]
    public async Task AFileFiledElsewhereLeavesTheAlbumFromTags()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await TaggedAsync($"{CoversFolder}/01 Gravity - Pepita Salim.flac", "Gravity", "Covers", track: 1);
        await TaggedAsync($"{CoversFolder}/02 True Colors - Pepita Salim.flac", "True Colors", "Covers", track: 2);
        await UnidentifiedAsync(unreleased: true);

        await RunAsync(new StubCatalogue());

        ReleaseGroupId elsewhere;
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Gravity", Mbid = Mb("12121212-1212-4121-8121-121212121212") };
            var recording = new Recording { Id = RecordingId.New(), Title = "Gravity", Mbid = Mb("34343434-3434-4343-8343-343434343434") };
            db.ReleaseGroups.Add(album);
            db.Recordings.Add(recording);

            var file = await db.MediaFiles.OrderBy(row => row.Path).FirstAsync(Token);
            file.ReleaseGroupId = album.Id;
            file.RecordingId = recording.Id;
            file.ReleaseId = null;
            file.TrackId = null;
            file.AttributionOutcome = ReleaseAttributionOutcome.AttributedByPerson;
            file.ReleaseDecidedUtc = StoreTime.ToStorePrecision(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Token);
            elsewhere = album.Id;
        }

        await RunAsync(new StubCatalogue());

        await using var read = PostgresFixture.CreateContext(_connectionString);
        var files = await read.MediaFiles.OrderBy(file => file.Path).ToListAsync(Token);
        var tagAlbum = await read.ReleaseGroups.SingleAsync(group => group.TagFolder == CoversFolder, Token);

        Assert.Equal(elsewhere, files[0].ReleaseGroupId);
        Assert.Equal(tagAlbum.Id, files[1].ReleaseGroupId);
        Assert.Equal(files[1].TrackId, (await read.Tracks.SingleAsync(track => track.Release!.ReleaseGroupId == tagAlbum.Id, Token)).Id);
    }

    /// <summary>A folder with a file that cannot be read keeps what it had: an unmounted volume is not a folder of untagged files.</summary>
    [Fact]
    public async Task AFolderThatCannotBeReadIsLeftAsItWas()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var path = $"{CoversFolder}/01 Gravity - Pepita Salim.flac";
        await TaggedAsync(path, "Gravity", "Covers", track: 1);
        await UnidentifiedAsync(unreleased: false);
        await RunAsync(new StubCatalogue());

        File.Delete(Path.Combine(_root, path));
        await RunAsync(new StubCatalogue());

        await using var read = PostgresFixture.CreateContext(_connectionString);
        var file = await read.MediaFiles.Include(row => row.Recording).SingleAsync(Token);
        Assert.Equal("Gravity", file.Recording!.Title);
        Assert.NotNull(file.TrackId);
    }

    /// <summary>A copy of the corpus FLAC with these tags, and a row for it if it has none.</summary>
    private async Task TaggedAsync(string path, string title, string album, int track)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(Corpus.Flac, full, overwrite: true);

        var tags = new ATL.Track(full)
        {
            Title = title,
            Artist = "Pepita Salim",
            AlbumArtist = "Pepita Salim",
            Album = album,
            TrackNumber = track,
        };
        tags.AdditionalFields["MUSICBRAINZ_ARTISTID"] = PepitaSalim.ToString();
        tags.AdditionalFields["MUSICBRAINZ_ALBUMARTISTID"] = PepitaSalim.ToString();
        Assert.True(tags.Save());

        await using var db = PostgresFixture.CreateContext(_connectionString);
        if (await db.MediaFiles.AnyAsync(file => file.Path == path, Token)) return;

        db.MediaFiles.Add(new MediaFile
        {
            Id = MediaFileId.New(),
            Path = path,
            SizeBytes = new FileInfo(full).Length,
            LastModifiedUtc = StoreTime.ToStorePrecision(DateTimeOffset.UtcNow),
        });
        await db.SaveChangesAsync(Token);
    }

    /// <summary>Every file, answered by identification as AcoustID never having heard it — or as a person's "never released".</summary>
    private async Task UnidentifiedAsync(bool unreleased)
    {
        var now = StoreTime.ToStorePrecision(DateTimeOffset.UtcNow);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.MediaFiles.ExecuteUpdateAsync(
            set => set
                .SetProperty(file => file.AcoustIdOutcome, unreleased ? AcoustIdOutcome.Unreleased : AcoustIdOutcome.Unknown)
                .SetProperty(file => file.AcoustIdCheckedUtc, now)
                .SetProperty(file => file.AcoustIdDecidedUtc, unreleased ? now : null)
                .SetProperty(file => file.IdentityDecidedUtc, unreleased ? now : null),
            Token);
    }
}
