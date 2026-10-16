using Fonoteca.Api.Library;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Picard's other tags: the pressing's facts only with a proven pressing, the
/// album's and the work's whatever is filed, and the audio's own on a file the
/// catalogue has no answer for.
/// </summary>
public sealed partial class TagWritePassTests
{
    private const string PicardTrack = "Miles Davis/Kind of Blue/01 track.flac";

    private static readonly Mbid TrackMbid = Mb("88888888-8888-4888-8888-888888888888");
    private static readonly Mbid WorkMbid = Mb("99999999-9999-4999-8999-999999999999");
    private static readonly Mbid VariousArtistsMbid = Mb("89ad4ac3-39f7-470e-963a-56509c546377");

    /// <summary>
    /// A file filed under a proven pressing carries the pressing's tags, the
    /// album's and the movement's, and a second run finds nothing to do, so
    /// every one of them reads back as the catalogue holds it.
    /// </summary>
    [Fact]
    public async Task AFiledFileCarriesThePressingTheAlbumAndTheMovementTags()
    {
        SkipWithoutTools();
        await SeedAsync((PicardTrack, 1));
        await SeedPicardFactsAsync();

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(PicardTrack), cancellationToken: Token);

        Assert.Equal("Blue Note", reading.Find(CatalogueTags.Label));
        Assert.Equal("BLP 1595", reading.Find(CatalogueTags.CatalogNumber));
        Assert.Equal("0724349522129", reading.Find(CatalogueTags.Barcode));
        Assert.Equal("US", reading.Find(CatalogueTags.ReleaseCountry));
        Assert.Equal("official", reading.Find(CatalogueTags.ReleaseStatus));
        Assert.Equal("CD", reading.Find(CatalogueTags.Media));
        Assert.Equal(TrackMbid.ToString(), reading.Find(CatalogueTags.ReleaseTrackId));
        Assert.Equal("B000002ADT", reading.Find(CatalogueTags.Asin));
        Assert.Equal("Latn", reading.Find(CatalogueTags.Script));
        Assert.Equal("The Sessions", reading.Find(CatalogueTags.DiscSubtitle));

        Assert.Equal($"album{CatalogueTags.ValueSeparator}live", reading.Find(CatalogueTags.ReleaseType));
        Assert.Equal("1958", reading.Find(CatalogueTags.OriginalYear));
        Assert.Null(reading.Find(CatalogueTags.Compilation));
        Assert.Equal("Miles Davis", reading.Find(CatalogueTags.Artists));
        Assert.Equal("Miles Davis", reading.Find(CatalogueTags.AlbumArtists));
        Assert.Equal("Davis, Miles", reading.Find(CatalogueTags.ArtistSort));
        Assert.Equal("Davis, Miles", reading.Find(CatalogueTags.AlbumArtistSort));
        Assert.Equal("Davis, Miles", reading.Find(CatalogueTags.ComposerSort));
        Assert.Equal(ArtistMbid.ToString(), reading.Find(CatalogueTags.ComposerId));
        Assert.Equal("https://milesdavis.example/", reading.Find(CatalogueTags.Website));
        Assert.Equal("Jimmy Cobb", reading.Find(CatalogueTags.Writer));
        Assert.Equal("Teo Macero", reading.Find(CatalogueTags.Remixer));
        Assert.Equal($"eng{CatalogueTags.ValueSeparator}deu", reading.Find(CatalogueTags.Language));

        // The work is the whole, and the leaf is the movement beside it.
        Assert.Equal("Kind of Blue Suite", reading.Find(CatalogueTags.Work));
        Assert.Equal("So What", reading.Find(CatalogueTags.MovementName));
        Assert.Equal("1", reading.Find(CatalogueTags.Movement));
        Assert.Equal("5", reading.Find(CatalogueTags.MovementTotal));
        Assert.Equal("1", reading.Find(CatalogueTags.ShowMovement));

        var again = await RunAsync(TagWriteScope.Library, services);
        Assert.Equal(0, again.Written);
        Assert.Equal(1, again.Unchanged);
    }

    /// <summary>
    /// A work with no known place in a whole is the work, whole, and no movement
    /// tag is written: a number out of order would claim a movement it is not.
    /// </summary>
    [Fact]
    public async Task AWorkWithNoPositionInItsParentIsWrittenWholeWithNoMovement()
    {
        SkipWithoutTools();
        await SeedAsync((PicardTrack, 1));
        await SeedPicardFactsAsync();

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            (await db.Works.SingleAsync(w => w.Mbid == WorkMbid, Token)).Position = null;
            await db.SaveChangesAsync(Token);
        }

        await RunAsync(TagWriteScope.Library);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(PicardTrack), cancellationToken: Token);

        Assert.Equal("Kind of Blue Suite: I. So What", reading.Find(CatalogueTags.Work));
        Assert.Null(reading.Find(CatalogueTags.MovementName));
        Assert.Null(reading.Find(CatalogueTags.Movement));
        Assert.Null(reading.Find(CatalogueTags.MovementTotal));
        Assert.Null(reading.Find(CatalogueTags.ShowMovement));
    }

    /// <summary>
    /// A file held to its album alone has the album's facts and none of a
    /// pressing's: which edition it is would be a claim nothing proved.
    /// </summary>
    [Fact]
    public async Task AFileHeldToItsAlbumAloneCarriesNoPressingTags()
    {
        SkipWithoutTools();
        await SeedAsync((PicardTrack, 1));
        await SeedPicardFactsAsync();
        await HoldToAlbumAsync(1);

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(PicardTrack), cancellationToken: Token);

        Assert.Equal($"album{CatalogueTags.ValueSeparator}live", reading.Find(CatalogueTags.ReleaseType));
        Assert.Equal("1958", reading.Find(CatalogueTags.OriginalYear));
        Assert.Equal("Miles Davis", reading.Find(CatalogueTags.Artists));

        Assert.Null(reading.Find(CatalogueTags.Label));
        Assert.Null(reading.Find(CatalogueTags.CatalogNumber));
        Assert.Null(reading.Find(CatalogueTags.Barcode));
        Assert.Null(reading.Find(CatalogueTags.ReleaseCountry));
        Assert.Null(reading.Find(CatalogueTags.ReleaseStatus));
        Assert.Null(reading.Find(CatalogueTags.Media));
        Assert.Null(reading.Find(CatalogueTags.ReleaseTrackId));
        Assert.Null(reading.Find(CatalogueTags.Asin));
        Assert.Null(reading.Find(CatalogueTags.Script));
        Assert.Null(reading.Find(CatalogueTags.DiscSubtitle));

        var again = await RunAsync(TagWriteScope.Library, services);
        Assert.Equal(0, again.Written);
        Assert.Equal(1, again.Unchanged);
    }

    /// <summary>A release billed to Various Artists is a compilation, and only then.</summary>
    [Fact]
    public async Task OnlyAReleaseBilledToVariousArtistsIsACompilation()
    {
        SkipWithoutTools();
        await SeedAsync((PicardTrack, 1));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var various = new Artist { Id = ArtistId.New(), Name = "Various Artists", Mbid = VariousArtistsMbid };
            db.Artists.Add(various);

            var billing = await db.ArtistCredits.SingleAsync(c => c.ReleaseId != null, Token);
            db.ArtistCredits.Remove(billing);
            db.ArtistCredits.Add(new ArtistCredit { Id = Guid.CreateVersion7(), ArtistId = various.Id, ReleaseId = billing.ReleaseId, Position = 0 });
            await db.SaveChangesAsync(Token);
        }

        await RunAsync(TagWriteScope.Library);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(PicardTrack), cancellationToken: Token);

        Assert.Equal("1", reading.Find(CatalogueTags.Compilation));
        Assert.Equal("Various Artists", reading.Find(CatalogueTags.AlbumArtists));
        Assert.Equal("Miles Davis", reading.Find(CatalogueTags.Artists));
    }

    /// <summary>
    /// A file the catalogue has not answered still takes what its own audio
    /// says — tempo, key, its fingerprint and the loudness reference — and
    /// nothing of an identity it has not got.
    /// </summary>
    [Fact]
    public async Task AnUnansweredFileGetsTempoKeyAndFingerprintAndNoCatalogueTags()
    {
        SkipWithoutTools();
        var loose = await SeedUnattributedAsync("Unknown/loose.flac");

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.Measurement = new AudioMeasurement { IntegratedLufs = -12.5, SamplePeakDbfs = -0.5, DurationSeconds = 12, DynamicRange = 9.6, LoudnessRangeLu = 7.5 };
            file.MeasuredUtc = DateTimeOffset.UtcNow;
            file.TempoAndKey = new TempoAndKey { Bpm = 119.6, BpmConfidence = 3, Key = "A", Scale = "minor", KeyStrength = 0.8 };
            file.TempoAndKeyMeasuredUtc = DateTimeOffset.UtcNow;
            file.Fingerprint = "AQAAfingerprint";
            await db.SaveChangesAsync(Token);
        }

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(loose), cancellationToken: Token);

        Assert.Equal("120", reading.Find(CatalogueTags.Bpm));
        Assert.Equal("Am", reading.Find(CatalogueTags.Key));
        Assert.Equal("AQAAfingerprint", reading.Find(CatalogueTags.AcoustIdFingerprint));
        Assert.Equal("-5.50 dB", reading.Find(CatalogueTags.TrackGain));
        Assert.Equal("-18.00 LUFS", reading.Find(CatalogueTags.ReferenceLoudness));
        Assert.NotNull(reading.Find(CatalogueTags.TrackRange));

        Assert.Null(reading.Find(CatalogueTags.Artists));
        Assert.Null(reading.Find(CatalogueTags.Label));
        Assert.Null(reading.Find(CatalogueTags.Work));

        var again = await RunAsync(TagWriteScope.Library, services);
        Assert.Equal(0, again.Written);
        Assert.Equal(1, again.Unchanged);
    }

    /// <summary>A tempo Essentia is unsure of is not written: below the confidence, no BPM, but the key stands.</summary>
    [Fact]
    public async Task ATempoBelowTheConfidenceIsNotWrittenButTheKeyIs()
    {
        SkipWithoutTools();
        var loose = await SeedUnattributedAsync("Unknown/loose.flac");

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.MeasuredUtc = DateTimeOffset.UtcNow;
            file.TempoAndKey = new TempoAndKey { Bpm = 70, BpmConfidence = 0.4, Key = "C", Scale = "major", KeyStrength = 0.6 };
            file.TempoAndKeyMeasuredUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Token);
        }

        await RunAsync(TagWriteScope.Library);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(loose), cancellationToken: Token);

        Assert.Null(reading.Find(CatalogueTags.Bpm));
        Assert.Equal("C", reading.Find(CatalogueTags.Key));
    }

    /// <summary>
    /// The composers' sort names go all together or not at all: a list missing
    /// one would sort a piece under some of its composers and not the others.
    /// </summary>
    [Fact]
    public async Task AComposerWithNoSortNameLeavesTheComposerSortOut()
    {
        SkipWithoutTools();
        await SeedAsync((PicardTrack, 1));
        await SeedPicardFactsAsync();

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var movement = await db.Works.SingleAsync(work => work.Mbid == WorkMbid, Token);

            // Credited by MBID, and no artist row says how to sort him.
            db.Contributions.Add(new Contribution
            {
                Id = Guid.CreateVersion7(),
                WorkId = movement.Id,
                Type = "composer",
                Name = "Gil Evans",
                ArtistMbid = Mb("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
                Position = 2,
            });

            await db.SaveChangesAsync(Token);
        }

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, Build())).Written);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(PicardTrack), cancellationToken: Token);

        Assert.Equal($"Miles Davis{CatalogueTags.ValueSeparator}Gil Evans", reading.Find(CatalogueTags.Composer));
        Assert.Null(reading.Find(CatalogueTags.ComposerSort));
    }

    /// <summary>
    /// What the seeded album is given on top of <see cref="SeedAsync"/>: a
    /// pressing's label to script, a track's id and medium, the album's types,
    /// a work that is the first of five parts, and who wrote and remixed it.
    /// </summary>
    private async Task SeedPicardFactsAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var artist = await db.Artists.SingleAsync(Token);
        artist.SortName = "Davis, Miles";
        artist.LinksJson = ArtistLinks.Write([new MusicBrainzLink("official homepage", new Uri("https://milesdavis.example/"))]);

        var release = await db.Releases.SingleAsync(Token);
        release.Label = "Blue Note";
        release.CatalogNumber = "BLP 1595";
        release.Barcode = "0724349522129";
        release.Country = "US";
        release.Status = "Official";
        release.Asin = "B000002ADT";
        release.Script = "Latn";

        var track = await db.Tracks.SingleAsync(Token);
        track.Mbid = TrackMbid;
        track.MediumTitle = "The Sessions";
        track.MediumFormat = "CD";

        var group = await db.ReleaseGroups.SingleAsync(Token);
        group.SecondaryTypes = "Live";
        group.FirstReleaseYear = 1958;

        var whole = new Work
        {
            Id = Domain.Catalogue.WorkId.New(),
            Title = "Kind of Blue Suite",
            Mbid = Mb("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            PartCount = 5,
        };

        var movement = new Work
        {
            Id = Domain.Catalogue.WorkId.New(),
            Title = "Kind of Blue Suite: I. So What",
            Mbid = WorkMbid,
            ParentId = whole.Id,
            Position = 1,
            Languages = "eng, deu",
        };

        db.Works.AddRange(whole, movement);
        (await db.Recordings.SingleAsync(Token)).WorkId = movement.Id;

        db.Contributions.AddRange(
            new Contribution { Id = Guid.CreateVersion7(), WorkId = movement.Id, Type = "composer", Name = "Miles Davis", ArtistMbid = ArtistMbid, Position = 0 },
            new Contribution { Id = Guid.CreateVersion7(), WorkId = movement.Id, Type = "writer", Name = "Jimmy Cobb", Position = 1 },
            new Contribution { Id = Guid.CreateVersion7(), RecordingId = (await db.Recordings.SingleAsync(Token)).Id, Type = "remixer", Name = "Teo Macero", Position = 0 });

        await db.SaveChangesAsync(Token);
    }
}
