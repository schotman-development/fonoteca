using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The Apple Music Classical stage (ADR 0019): its pieces and performers where
/// MusicBrainz has none, replaced when MusicBrainz names its own, and its
/// composers' biographies where Wikipedia has none.
/// </summary>
public sealed partial class EnrichmentPassTests
{
    private static readonly Mbid Beethoven = Mb("1f9df192-a621-4f54-8850-2c5373b7eac9");

    private static readonly Mbid NinthWork = Mb("0a2c4e6f-8b1d-4f3a-9c5e-7d9f1b3a5c7e");

    private const string AppleFifth = "ludwig-van-beethoven-1770-pp27";

    private static readonly ClassicalArtist AppleBeethoven = new("1130028", "Ludwig van Beethoven");

    private static readonly ClassicalArtist AppleMozart = new("1149479", "Wolfgang Amadeus Mozart");

    private static readonly ClassicalPerformer[] KarajansBerliners =
    [
        new("Berlin Philharmonic Orchestra", ["Orchestra", "Symphony Orchestra"]),
        new("Herbert von Karajan", ["Conductor"]),
    ];

    /// <summary>The record Apple carries: four tracks, two pieces.</summary>
    private static ClassicalAlbum AppleAlbum() =>
        new(
            "1440744257",
            "us",
            "barcode",
            [
                new(1, 1, TimeSpan.FromSeconds(442), new(AppleFifth, "Symphony No. 5 in C Minor, Op. 67", [AppleBeethoven]), KarajansBerliners),
                new(1, 2, TimeSpan.FromSeconds(590), new(AppleFifth, "Symphony No. 5 in C Minor, Op. 67", [AppleBeethoven]), KarajansBerliners),
                new(1, 3, TimeSpan.FromSeconds(600), new("ludwig-van-beethoven-1770-pp28", "Symphony No. 6", [AppleBeethoven, AppleMozart]), KarajansBerliners),
                new(1, 4, TimeSpan.FromSeconds(300), null, KarajansBerliners),
            ]);

    /// <summary>The four recordings of a classical album, as MusicBrainz left them.</summary>
    private sealed record ClassicalSeed(
        ReleaseGroupId Album,
        RecordingId Unknown,
        RecordingId Conducted,
        RecordingId Worked,
        RecordingId TooLong,
        WorkId Ninth);

    /// <summary>
    /// A held album tagged classical: one recording MusicBrainz knows nothing
    /// about, one it names a conductor for, one it links to a work, and one
    /// whose file is twenty seconds longer than Apple's track. Every stage
    /// before Apple's has nothing left to ask.
    /// </summary>
    private async Task<ClassicalSeed> SeedClassicalAlbumAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var now = DateTimeOffset.UtcNow;

        db.Artists.Add(new Artist { Id = ArtistId.New(), Name = "Ludwig van Beethoven", Mbid = Beethoven });
        db.Artists.Add(new Artist
        {
            Id = ArtistId.New(),
            Name = "Wolfgang Amadeus Mozart",
            Mbid = Mozart,
            BiographyText = "Wikipedia's Mozart.",
            BiographyUrl = "https://en.wikipedia.org/wiki/Wolfgang_Amadeus_Mozart",
            BiographyLookupUtc = now,
        });

        var ninth = new Work
        {
            Id = Domain.Catalogue.WorkId.New(),
            Title = "Symphony No. 9",
            Mbid = NinthWork,
            Type = "Symphony",
            DescribedUtc = now,
            PartOfLookupUtc = now,
        };

        db.Works.Add(ninth);

        foreach (var (position, (mbid, name)) in new[] { (Beethoven, "Ludwig van Beethoven"), (Mozart, "Wolfgang Amadeus Mozart") }.Index())
        {
            db.Contributions.Add(new Contribution
            {
                Id = Guid.CreateVersion7(),
                WorkId = ninth.Id,
                Type = "composer",
                ArtistMbid = mbid,
                Name = name,
                Position = position,
            });
        }

        var album = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Beethoven: Symphonies Nos. 5 & 6",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Genres = "classical",
            GenresLookupUtc = now,
        };

        db.ReleaseGroups.Add(album);

        RecordingId Held(string title, int number, double seconds, WorkId? work = null)
        {
            var recording = new Recording
            {
                Id = RecordingId.New(),
                Title = title,
                Mbid = new Mbid(Guid.CreateVersion7()),
                WorkId = work,
                Genres = "classical",
                DescribedUtc = now,
            };

            db.Recordings.Add(recording);

            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = $"Karajan/Beethoven 5 & 6/{number:00} - {title}.flac",
                SizeBytes = 1024,
                LastModifiedUtc = now,
                RecordingId = recording.Id,
                ReleaseGroupId = album.Id,
                TagTrackNumber = number,
                Quality = new AudioQuality
                {
                    Codec = "flac",
                    SampleRateHz = 44100,
                    Channels = 2,
                    BitDepth = 16,
                    BitrateBps = 900_000,
                    IsLossless = true,
                    Duration = TimeSpan.FromSeconds(seconds),
                },
            });

            return recording.Id;
        }

        var unknown = Held("Allegro con brio", 1, 443.4);
        var conducted = Held("Andante con moto", 2, 589);
        var worked = Held("Allegro ma non troppo", 3, 601, ninth.Id);
        var tooLong = Held("Andante molto mosso", 4, 320);

        db.Contributions.Add(new Contribution
        {
            Id = Guid.CreateVersion7(),
            RecordingId = conducted,
            Type = "conductor",
            ArtistMbid = Karajan,
            Name = "Herbert von Karajan",
            Position = 0,
        });

        await db.SaveChangesAsync(Token);

        return new ClassicalSeed(album.Id, unknown, conducted, worked, tooLong, ninth.Id);
    }

    /// <summary>
    /// A recording MusicBrainz links to no work is performed under Apple's
    /// piece, minted once with its composer; one it names no performer for
    /// gains Apple's, in MusicBrainz's terms; what MusicBrainz says stands.
    /// </summary>
    [Fact]
    public async Task ApplesPiecesAndPerformersFillOnlyWhatMusicBrainzLeftEmpty()
    {
        var seed = await SeedClassicalAlbumAsync();
        var classical = new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()));

        var services = Build(Answering(Recording), new StubCatalogue(null, null), classical: classical);

        await EnrichAsync(services);

        Assert.Single(classical.Asked);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var piece = await db.Works.AsNoTracking().SingleAsync(w => w.AppleWorkId != null, Token);

        Assert.Equal(AppleFifth, piece.AppleWorkId);
        Assert.Null(piece.Mbid);
        Assert.Equal("Symphony No. 5 in C Minor, Op. 67", piece.Title);

        var recordings = await db.Recordings.AsNoTracking().ToDictionaryAsync(r => r.Id, Token);

        Assert.Equal(piece.Id, recordings[seed.Unknown].WorkId);
        Assert.Equal(piece.Id, recordings[seed.Conducted].WorkId);
        Assert.Equal(seed.Ninth, recordings[seed.Worked].WorkId);
        Assert.Null(recordings[seed.TooLong].WorkId);

        var composer = await db.Contributions.AsNoTracking().SingleAsync(c => c.WorkId == piece.Id, Token);

        Assert.Equal(("composer", "Ludwig van Beethoven", Beethoven, Contribution.AppleMusicClassical),
            (composer.Type, composer.Name, composer.ArtistMbid!.Value, composer.Source));

        var link = await db.Relationships.AsNoTracking().SingleAsync(r => r.WorkId == piece.Id, Token);
        var beethoven = await db.Artists.AsNoTracking().SingleAsync(a => a.Mbid == Beethoven, Token);

        Assert.Equal(("composer", beethoven.Id), (link.Type, link.ArtistId!.Value));

        async Task<List<(string, string?, string, string?)>> PerformersAsync(RecordingId id) =>
            (await db.Contributions.AsNoTracking()
                .Where(c => c.RecordingId == id)
                .OrderBy(c => c.Source).ThenBy(c => c.Position)
                .ToListAsync(Token))
            .ConvertAll(c => (c.Type, c.Attributes, c.Name, c.Source));

        (string, string?, string, string?)[] apples =
        [
            ("performing orchestra", null, "Berlin Philharmonic Orchestra", Contribution.AppleMusicClassical),
            ("conductor", null, "Herbert von Karajan", Contribution.AppleMusicClassical),
        ];

        Assert.Equal(apples, await PerformersAsync(seed.Unknown));
        Assert.Equal(apples, await PerformersAsync(seed.Worked));
        Assert.Equal([("conductor", null, "Herbert von Karajan", null)], await PerformersAsync(seed.Conducted));
        Assert.Empty(await PerformersAsync(seed.TooLong));

        var album = await db.ReleaseGroups.AsNoTracking().SingleAsync(g => g.Id == seed.Album, Token);

        Assert.Equal("1440744257", album.AppleClassicalAlbumId);
        Assert.NotNull(album.ClassicalLookupUtc);

        Assert.Equal(0, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Classical);

        await EnrichAsync(services);

        Assert.Single(classical.Asked);
    }

    /// <summary>
    /// A composer Wikipedia has no article about gets Apple's biography,
    /// credited to Apple; one it has an article about keeps it and Apple is
    /// never asked. A week on, Wikipedia is asked again, and wins if it answers.
    /// </summary>
    [Fact]
    public async Task ApplesBiographyFillsWikipediasGapUntilWikipediaHasOne()
    {
        await SeedClassicalAlbumAsync();

        var classical = new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))
        {
            Biographies = { ["1130028"] = "Apple's Beethoven.", ["1149479"] = "Apple's Mozart." },
        };

        var services = Build(Answering(Recording), new StubCatalogue(null, null), classical: classical);

        await EnrichAsync(services);

        Assert.Equal(["1130028"], classical.BiographiesAsked);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var beethoven = await db.Artists.AsNoTracking().SingleAsync(a => a.Mbid == Beethoven, Token);
            var mozart = await db.Artists.AsNoTracking().SingleAsync(a => a.Mbid == Mozart, Token);

            Assert.Equal("Apple's Beethoven.", beethoven.BiographyText);
            Assert.Equal("https://classical.music.apple.com/us/artist/1130028", beethoven.BiographyUrl);
            Assert.Equal("Wikipedia's Mozart.", mozart.BiographyText);

            Assert.Equal("Apple Music Classical", EnrichmentService.BiographySource(beethoven.BiographyUrl));
            Assert.Equal("Wikipedia", EnrichmentService.BiographySource(mozart.BiographyUrl));

            await db.Artists
                .Where(a => a.Mbid == Beethoven || a.Mbid == Mozart)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.BiographyLookupUtc, DateTimeOffset.UtcNow.AddDays(-8)), Token);
        }

        var before = (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Articles;

        Assert.Equal(1, before);
    }

    /// <summary>
    /// MusicBrainz's answer replaces Apple's: its work takes the recording and
    /// Apple's piece goes once nothing performs it, composer and all; a
    /// performer it names takes the place of Apple's.
    /// </summary>
    [Fact]
    public async Task MusicBrainzsWorkAndPerformersReplaceApples()
    {
        var seed = await SeedClassicalAlbumAsync();

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            classical: new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Recordings
                .Where(r => r.Id == seed.Unknown || r.Id == seed.Conducted)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.DescribedUtc, (DateTimeOffset?)null), Token);
        }

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(
                recording: RecordingWith(
                    [Credit(Karajan, "Herbert von Karajan")],
                    [Relation("conductor", Karajan, "Herbert von Karajan", "Person")]),
                work: WorkWith([]))));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        Assert.False(await check.Works.AnyAsync(w => w.AppleWorkId != null, Token));
        Assert.False(await check.Contributions.AnyAsync(c => c.WorkId != null && c.Source != null, Token));

        var work = await check.Works.AsNoTracking().SingleAsync(w => w.Mbid == WorkId, Token);
        var unknown = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == seed.Unknown, Token);

        Assert.Equal(work.Id, unknown.WorkId);
        Assert.Equal(
            [("conductor", "Herbert von Karajan", (string?)null)],
            (await check.Contributions.AsNoTracking().Where(c => c.RecordingId == seed.Unknown).ToListAsync(Token))
                .ConvertAll(c => (c.Type, c.Name, c.Source)));
    }

    /// <summary>
    /// Where Wikipedia could not be asked, the composer gets Apple's biography
    /// all the same, and Wikipedia's article replaces it on the next run.
    /// </summary>
    [Fact]
    public async Task AWikipediaOutageLeavesApplesBiographyUntilWikipediaAnswers()
    {
        await SeedClassicalAlbumAsync();

        var classical = new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))
        {
            Biographies = { ["1130028"] = "Apple's Beethoven." },
        };

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            encyclopedia: new StubEncyclopedia { Unavailable = true },
            classical: classical));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var beethoven = await db.Artists.AsNoTracking().SingleAsync(a => a.Mbid == Beethoven, Token);

            Assert.Equal("Apple's Beethoven.", beethoven.BiographyText);
            Assert.Null(beethoven.BiographyLookupUtc);
        }

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            encyclopedia: new StubEncyclopedia((Beethoven, "Wikipedia's Beethoven.")),
            classical: classical));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var beethoven = await db.Artists.AsNoTracking().SingleAsync(a => a.Mbid == Beethoven, Token);

            Assert.Equal("Wikipedia's Beethoven.", beethoven.BiographyText);
            Assert.Equal("Wikipedia", EnrichmentService.BiographySource(beethoven.BiographyUrl));
        }
    }

    /// <summary>
    /// A heart on Apple's piece goes to the whole piece MusicBrainz's work is
    /// a part of when MusicBrainz takes the recordings over.
    /// </summary>
    [Fact]
    public async Task AHeartOnApplesPieceMovesToTheWholePieceMusicBrainzNames()
    {
        var seed = await SeedClassicalAlbumAsync();

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            classical: new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var piece = await db.Works.SingleAsync(w => w.AppleWorkId == AppleFifth, Token);

            db.Loves.Add(new Love { Id = Guid.CreateVersion7(), WorkId = piece.Id, LovedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Token);

            await db.Recordings
                .Where(r => r.Id == seed.Unknown || r.Id == seed.Conducted)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.DescribedUtc, (DateTimeOffset?)null), Token);
        }

        var whole = Mb("3c5e7a9b-1d2f-4a6b-8c0d-2e4f6a8b0c1d");

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(
                recording: RecordingWith([Credit(Karajan, "Herbert von Karajan")], []),
                work: WorkWith([]) with { ParentId = whole, ParentTitle = "Symphony No. 5 in C minor, Op. 67" })));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        Assert.False(await check.Works.AnyAsync(w => w.AppleWorkId == AppleFifth, Token));

        var symphony = await check.Works.AsNoTracking().SingleAsync(w => w.Mbid == whole, Token);
        var heart = await check.Loves.IgnoreQueryFilters().AsNoTracking().SingleAsync(Token);

        Assert.Equal(symphony.Id, heart.WorkId);
    }

    /// <summary>
    /// A recording MusicBrainz has merged into another leaves Apple's piece to
    /// the survivor while MusicBrainz names none.
    /// </summary>
    [Fact]
    public async Task AMergedRecordingsSurvivorKeepsApplesPiece()
    {
        var seed = await SeedClassicalAlbumAsync();

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            classical: new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))));

        Mbid merged;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            merged = (await db.Recordings.AsNoTracking().SingleAsync(r => r.Id == seed.Unknown, Token)).Mbid!.Value;

            await db.Recordings
                .Where(r => r.Id == seed.Unknown)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.DescribedUtc, (DateTimeOffset?)null), Token);
        }

        var survivor = Mb("5e7a9c1b-3d4f-4b6c-8d0e-2f4a6b8c0d1e");

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(
                recording: RecordingWith([Credit(Karajan, "Herbert von Karajan")], []),
                work: null,
                merged: new Dictionary<Mbid, Mbid> { [merged] = survivor })));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var piece = await check.Works.AsNoTracking().SingleAsync(w => w.AppleWorkId == AppleFifth, Token);

        Assert.False(await check.Recordings.AnyAsync(r => r.Id == seed.Unknown, Token));
        Assert.Equal(piece.Id, (await check.Recordings.AsNoTracking().SingleAsync(r => r.Mbid == survivor, Token)).WorkId);
    }

    /// <summary>
    /// A person's or an agent's work for a recording is replaced by neither
    /// MusicBrainz describing it again nor Apple's piece for its track.
    /// </summary>
    [Theory]
    [InlineData(WorkSources.Person)]
    [InlineData(WorkSources.Agent)]
    public async Task APersonsWorkForARecordingIsReplacedByNoRule(string source)
    {
        var seed = await SeedClassicalAlbumAsync();

        await LinkByHandAsync(seed, source);

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(recording: RecordingWith([Credit(Karajan, "Herbert von Karajan")], []), work: WorkWith([])),
            classical: new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == seed.Unknown, Token);

        Assert.Equal(seed.Ninth, row.WorkId);
        Assert.Equal(source, row.WorkSource);
        Assert.NotNull(row.DescribedUtc);
    }

    /// <summary>A recording MusicBrainz has merged into another leaves a person's work to the survivor.</summary>
    [Fact]
    public async Task AMergedRecordingsSurvivorKeepsAPersonsWork()
    {
        var seed = await SeedClassicalAlbumAsync();
        var merged = await LinkByHandAsync(seed, WorkSources.Person);
        var survivor = Mb("6f8b0d2c-4e5a-4c7d-9e1f-3a5b7c9d1e2f");

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(
                recording: RecordingWith([Credit(Karajan, "Herbert von Karajan")], []),
                work: WorkWith([]),
                merged: new Dictionary<Mbid, Mbid> { [merged] = survivor })));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Mbid == survivor, Token);

        Assert.Equal(seed.Ninth, row.WorkId);
        Assert.Equal(WorkSources.Person, row.WorkSource);
    }

    /// <summary>The seed's unknown recording linked to the Ninth by hand and left to be described again; its MBID.</summary>
    private async Task<Mbid> LinkByHandAsync(ClassicalSeed seed, string source)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var recording = await db.Recordings.SingleAsync(r => r.Id == seed.Unknown, Token);

        recording.WorkId = seed.Ninth;
        recording.WorkSource = source;
        recording.DescribedUtc = null;

        await db.SaveChangesAsync(Token);

        return recording.Mbid!.Value;
    }

    /// <summary>
    /// A piece another recording still performs stays when MusicBrainz names
    /// the work of one of them.
    /// </summary>
    [Fact]
    public async Task APieceAnotherRecordingPerformsStaysWhenMusicBrainzTakesOne()
    {
        var seed = await SeedClassicalAlbumAsync();

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            classical: new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Recordings
                .Where(r => r.Id == seed.Unknown)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.DescribedUtc, (DateTimeOffset?)null), Token);
        }

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(recording: RecordingWith([Credit(Karajan, "Herbert von Karajan")], []), work: WorkWith([]))));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var piece = await check.Works.AsNoTracking().SingleAsync(w => w.AppleWorkId == AppleFifth, Token);
        var work = await check.Works.AsNoTracking().SingleAsync(w => w.Mbid == WorkId, Token);
        var recordings = await check.Recordings.AsNoTracking().ToDictionaryAsync(r => r.Id, Token);

        Assert.Equal(work.Id, recordings[seed.Unknown].WorkId);
        Assert.Equal(piece.Id, recordings[seed.Conducted].WorkId);
        Assert.Equal(1, await check.Contributions.CountAsync(c => c.WorkId == piece.Id, Token));
        Assert.Equal(1, await check.Relationships.CountAsync(r => r.WorkId == piece.Id, Token));
    }

    /// <summary>A recording whose two files sit on two different tracks of Apple's is neither.</summary>
    [Fact]
    public async Task ARecordingTwoFilesPlaceOnDifferentTracksIsNobodys()
    {
        var seed = await SeedClassicalAlbumAsync();

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "Karajan/Beethoven 5 & 6 (again)/02 - Allegro con brio.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                RecordingId = seed.Unknown,
                ReleaseGroupId = seed.Album,
                TagTrackNumber = 2,
                Quality = new AudioQuality
                {
                    Codec = "flac",
                    SampleRateHz = 44100,
                    Channels = 2,
                    BitrateBps = 900_000,
                    IsLossless = true,
                    Duration = TimeSpan.FromSeconds(590),
                },
            });

            await db.SaveChangesAsync(Token);
        }

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            classical: new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var piece = await check.Works.AsNoTracking().SingleAsync(w => w.AppleWorkId == AppleFifth, Token);
        var recordings = await check.Recordings.AsNoTracking().ToDictionaryAsync(r => r.Id, Token);

        Assert.Null(recordings[seed.Unknown].WorkId);
        Assert.False(await check.Contributions.AnyAsync(c => c.RecordingId == seed.Unknown, Token));
        Assert.Equal(piece.Id, recordings[seed.Conducted].WorkId);
    }

    /// <summary>Asking about an album again rebuilds its piece's composers rather than adding to them.</summary>
    [Fact]
    public async Task AskingAgainRebuildsAPieceRatherThanAddingToIt()
    {
        var seed = await SeedClassicalAlbumAsync();
        var classical = new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()));
        var services = Build(Answering(Recording), new StubCatalogue(null, null), classical: classical);

        await EnrichAsync(services);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.ReleaseGroups
                .Where(g => g.Id == seed.Album)
                .ExecuteUpdateAsync(setters => setters.SetProperty(g => g.ClassicalLookupUtc, (DateTimeOffset?)null), Token);
        }

        await EnrichAsync(services);

        Assert.Equal(2, classical.Asked.Count);

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var piece = await check.Works.AsNoTracking().SingleAsync(w => w.AppleWorkId != null, Token);

        Assert.Equal(1, await check.Contributions.CountAsync(c => c.WorkId == piece.Id, Token));
        Assert.Equal(1, await check.Relationships.CountAsync(r => r.WorkId == piece.Id, Token));
        Assert.Equal(2, await check.Contributions.CountAsync(c => c.RecordingId == seed.Unknown, Token));
    }

    /// <summary>
    /// A recording MusicBrainz links to a work keeps it even where Apple files
    /// it under a piece another recording on the album takes.
    /// </summary>
    [Fact]
    public async Task MusicBrainzsWorkStandsBesideAPieceAppleFilesItUnder()
    {
        var seed = await SeedClassicalAlbumAsync();

        var album = AppleAlbum();
        var shared = album.Tracks.ToList();
        shared[2] = shared[2] with { Work = shared[0].Work };

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            classical: new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", album with { Tracks = shared }))));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var piece = await check.Works.AsNoTracking().SingleAsync(w => w.AppleWorkId == AppleFifth, Token);
        var recordings = await check.Recordings.AsNoTracking().ToDictionaryAsync(r => r.Id, Token);

        Assert.Equal(piece.Id, recordings[seed.Unknown].WorkId);
        Assert.Equal(seed.Ninth, recordings[seed.Worked].WorkId);
    }

    /// <summary>
    /// Where MusicBrainz still names no work and no performer, describing the
    /// recording again keeps Apple's beside whatever MusicBrainz does say.
    /// </summary>
    [Fact]
    public async Task MusicBrainzsSilenceKeepsApples()
    {
        var seed = await SeedClassicalAlbumAsync();

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(null, null),
            classical: new StubClassical(("Beethoven: Symphonies Nos. 5 & 6", AppleAlbum()))));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Recordings
                .Where(r => r.Id == seed.Unknown)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.DescribedUtc, (DateTimeOffset?)null), Token);
        }

        await EnrichAsync(Build(
            Answering(Recording),
            new StubCatalogue(
                recording: RecordingWith(
                    [Credit(Karajan, "Herbert von Karajan")],
                    [Relation("producer", Karajan, "Michel Glotz", "Person")]),
                work: null)));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var piece = await check.Works.AsNoTracking().SingleAsync(w => w.AppleWorkId == AppleFifth, Token);
        var unknown = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == seed.Unknown, Token);

        Assert.Equal(piece.Id, unknown.WorkId);
        Assert.Equal(
            ["Berlin Philharmonic Orchestra", "Herbert von Karajan", "Michel Glotz"],
            (await check.Contributions.AsNoTracking().Where(c => c.RecordingId == seed.Unknown).ToListAsync(Token))
                .Select(c => c.Name)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// An album Apple does not carry is asked about again a week later; one it
    /// carries never is. An album not tagged classical is never asked.
    /// </summary>
    [Fact]
    public async Task AnAlbumAppleDoesNotCarryIsAskedAgainAfterAWeek()
    {
        var seed = await SeedClassicalAlbumAsync();
        await SeedBilledArtistAsync(Karajan, "Karajan");

        // Tagged classical but held nowhere, as a discography browse mints them.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.Add(new ReleaseGroup
            {
                Id = ReleaseGroupId.New(),
                Title = "Requiem",
                Mbid = new Mbid(Guid.CreateVersion7()),
                Genres = "classical",
                GenresLookupUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(Token);
        }

        var classical = new StubClassical();
        var services = Build(Answering(Recording), new StubCatalogue(null, null), classical: classical);
        var enrichment = services.GetRequiredService<EnrichmentService>();

        await EnrichAsync(services);

        Assert.Equal(["Beethoven: Symphonies Nos. 5 & 6"], classical.Asked.Select(album => album.Title));

        var settled = await enrichment.CountPendingAsync(Token);
        Assert.Equal(0, settled.Classical);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.ReleaseGroups
                .Where(g => g.Id == seed.Album)
                .ExecuteUpdateAsync(setters => setters.SetProperty(g => g.ClassicalLookupUtc, DateTimeOffset.UtcNow.AddDays(-8)), Token);
        }

        var stale = await enrichment.CountPendingAsync(Token);
        Assert.Equal(1, stale.Classical);
        Assert.Equal(settled.Total + 1, stale.Total);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.ReleaseGroups
                .Where(g => g.Id == seed.Album)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(g => g.AppleClassicalAlbumId, "1440744257")
                        .SetProperty(g => g.ClassicalLookupUtc, DateTimeOffset.UtcNow.AddDays(-30)),
                    Token);
        }

        Assert.Equal(0, (await enrichment.CountPendingAsync(Token)).Classical);
    }

    /// <summary>Answers for the album titles it was told about, and records the asking.</summary>
    private sealed class StubClassical(params (string Title, ClassicalAlbum Found)[] albums) : IClassicalAlbums
    {
        public List<AlbumToFind> Asked { get; } = [];

        public List<string> BiographiesAsked { get; } = [];

        public Dictionary<string, string> Biographies { get; } = [];

        public Task<ClassicalAlbum?> FindAsync(AlbumToFind album, CancellationToken cancellationToken = default)
        {
            lock (Asked) Asked.Add(album);

            return Task.FromResult<ClassicalAlbum?>(albums.FirstOrDefault(entry => entry.Title == album.Title).Found);
        }

        public Task<ClassicalBiography?> BiographyAsync(string composer, CancellationToken cancellationToken = default)
        {
            lock (BiographiesAsked) BiographiesAsked.Add(composer);

            return Task.FromResult(Biographies.TryGetValue(composer, out var text)
                ? new ClassicalBiography(text, new Uri($"https://classical.music.apple.com/us/artist/{composer}"))
                : null);
        }
    }
}
