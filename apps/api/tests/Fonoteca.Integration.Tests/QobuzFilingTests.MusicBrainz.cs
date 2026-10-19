using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Acquisition;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// A download moving onto MusicBrainz once MusicBrainz knows it (ADR 0011): by
/// barcode at download, by the weekly sweep, by ISRC, and at once when a person
/// names the album.
/// </summary>
public sealed partial class QobuzFilingTests
{
    private static readonly Mbid RumoursRelease = new(Guid.Parse("4b1c2e0a-0000-4000-8000-000000000001"));
    private static readonly Mbid RumoursGroup = new(Guid.Parse("4b1c2e0a-0000-4000-8000-000000000002"));
    private static readonly Mbid TheChain = new(Guid.Parse("4b1c2e0a-0000-4000-8000-000000000003"));
    private static readonly Mbid Dreams = new(Guid.Parse("4b1c2e0a-0000-4000-8000-000000000004"));

    [Fact]
    public async Task ADownloadWhoseBarcodeMusicBrainzKnowsIsFiledUnderItsRelease()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz { Releases = { Rumours() } };
        var factory = Factory(mutation: false, musicBrainz);

        var body = await DownloadAsync(factory);
        Assert.Equal(2, body.Filing!.Filed);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(RumoursRelease, (await db.Releases.SingleAsync(Token)).Mbid);
        Assert.Empty(await db.Recordings.Where(recording => recording.Mbid == null).ToListAsync(Token));

        var files = await db.MediaFiles.Include(file => file.Recording).OrderBy(file => file.Path).ToListAsync(Token);
        Assert.Equal([TheChain, Dreams], files.Select(file => file.Recording!.Mbid!.Value));
        Assert.All(files, file => Assert.Equal(EnrichmentOutcome.LinkedByProvider, file.EnrichmentOutcome));

        // Their graph is enrichment's to fetch, as for a file filed by hand.
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, (await scope.ServiceProvider.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSweepMovesAShopsAlbumOntoMusicBrainzOnceItKnowsIt(bool albumHeldAlready)
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz();
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);

        // Held and loved already, the album keeps one heart.
        if (albumHeldAlready)
        {
            await using var seed = PostgresFixture.CreateContext(_connectionString);
            seed.ReleaseGroups.Add(new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Rumours", Mbid = RumoursGroup });
            await seed.SaveChangesAsync(Token);
        }

        await LoveEverythingAsync();

        musicBrainz.Releases.Add(Rumours());
        var swept = await factory.Services.GetRequiredService<MusicBrainzCatchUp>().SweepAsync(Token);

        Assert.Equal((1, 0), swept);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        // Moved whole: the files where they were, the shop's release gone.
        var release = await db.Releases.SingleAsync(Token);
        Assert.Equal(RumoursRelease, release.Mbid);
        Assert.All(await db.MediaFiles.ToListAsync(Token), file => Assert.Equal(release.Id, file.ReleaseId));
        Assert.Empty(await db.Recordings.Where(recording => recording.Mbid == null).ToListAsync(Token));
        Assert.Empty(await db.ReleaseGroups.Where(group => group.Mbid == null).ToListAsync(Token));
        Assert.Single(await db.DomainEvents.Where(entry => entry.Type == MusicBrainzCatchUp.MatchedEvent).ToListAsync(Token));
        await EverythingIsLovedAsync(db);
        Assert.Equal(1, await db.Loves.CountAsync(love => love.ReleaseGroupId != null, Token));
    }

    /// <summary>
    /// An album MusicBrainz knows keeps its heart when its folder is named as
    /// another: it is no download's own row, and stays for its other folders.
    /// </summary>
    [Fact]
    public async Task AHeartOnAnAlbumMusicBrainzKnowsStaysWhenItsFolderIsNamedAnother()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var deluxe = new Mbid(Guid.CreateVersion7());
        var musicBrainz = new StubMusicBrainz();
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);

        musicBrainz.Releases.Add(Rumours());
        Assert.Equal((1, 0), await factory.Services.GetRequiredService<MusicBrainzCatchUp>().SweepAsync(Token));
        await LoveEverythingAsync();

        var other = new Mbid(Guid.CreateVersion7());
        musicBrainz.Releases.Add(Rumours() with { Id = other, Barcode = null, ReleaseGroupId = deluxe, ReleaseGroupTitle = "Rumours (Deluxe)" });

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/folders/album", UriKind.Relative),
            new FolderAlbumRequest("Fleetwood Mac/Rumours", other.Value),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var rumours = await db.ReleaseGroups.SingleAsync(group => group.Mbid == RumoursGroup, Token);
        var named = await db.ReleaseGroups.SingleAsync(group => group.Mbid == deluxe, Token);

        Assert.All(await db.MediaFiles.ToListAsync(Token), file => Assert.Equal(named.Id, file.ReleaseGroupId));
        Assert.True(await db.Loves.AnyAsync(love => love.ReleaseGroupId == rumours.Id, Token));
        Assert.False(await db.Loves.AnyAsync(love => love.ReleaseGroupId == named.Id, Token));
    }

    [Fact]
    public async Task WhereTheBarcodeFindsNothingAnIsrcNamesTheRecordingAndTheAlbumIsAskedWeekly()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz { Isrcs = { ["USWB10101367"] = [Dreams] } };
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);

        var catchUp = factory.Services.GetRequiredService<MusicBrainzCatchUp>();
        Assert.Equal((0, 1), await catchUp.SweepAsync(Token));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var recordings = await db.Recordings.ToDictionaryAsync(recording => recording.Isrc!, Token);
            Assert.Equal(Dreams, recordings["USWB10101367"].Mbid);
            Assert.Null(recordings["USWB10101361"].Mbid);
            Assert.NotNull((await db.Releases.SingleAsync(Token)).ProviderCheckedUtc);
        }

        // Asked, so not asked again within the week.
        var asked = musicBrainz.Calls;
        Assert.Equal((0, 0), await catchUp.SweepAsync(Token));
        Assert.Equal(asked, musicBrainz.Calls);
    }

    [Fact]
    public async Task AnIsrcTheCatalogueAlreadyHoldsFoldsTheShopsRecordingIntoIt()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz { Isrcs = { ["USWB10101367"] = [Dreams] } };
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);
        await LoveEverythingAsync();

        var held = new Recording { Id = RecordingId.New(), Title = "Dreams", Mbid = Dreams };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.Recordings.Add(held);
            await db.SaveChangesAsync(Token);
        }

        await factory.Services.GetRequiredService<MusicBrainzCatchUp>().SweepAsync(Token);

        await using var check = PostgresFixture.CreateContext(_connectionString);
        var dreams = await check.MediaFiles.Include(file => file.Track).SingleAsync(file => file.Path.EndsWith("Dreams.flac"), Token);

        Assert.Equal(held.Id, dreams.RecordingId);
        Assert.Equal(held.Id, dreams.Track!.RecordingId);
        Assert.Equal(1, await check.Recordings.CountAsync(recording => recording.Title == "Dreams", Token));
        await EverythingIsLovedAsync(check);
    }

    [Fact]
    public async Task AnOutageStampsNothing()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz { Down = true };
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);

        Assert.Equal((0, 0), await factory.Services.GetRequiredService<MusicBrainzCatchUp>().SweepAsync(Token));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Null((await db.Releases.SingleAsync(Token)).ProviderCheckedUtc);
    }

    [Fact]
    public async Task NamingTheAlbumByHandReFilesADownloadAtOnce()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz();
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);

        // A pressing the barcode search never finds, named by a person.
        musicBrainz.Releases.Add(Rumours() with { Barcode = null });
        await LoveEverythingAsync();

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/folders/album", UriKind.Relative),
            new FolderAlbumRequest("Fleetwood Mac/Rumours", RumoursRelease.Value),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var files = await db.MediaFiles.Include(file => file.Recording).OrderBy(file => file.Path).ToListAsync(Token);

        Assert.Equal([TheChain, Dreams], files.Select(file => file.Recording!.Mbid!.Value));
        Assert.All(files, file =>
        {
            Assert.Equal(ReleaseAttributionOutcome.AlbumByPerson, file.AttributionOutcome);
            Assert.Equal(EnrichmentOutcome.LinkedByPerson, file.EnrichmentOutcome);
            Assert.Null(file.ReleaseLookupUtc);
        });

        Assert.Empty(await db.Releases.Where(release => release.Mbid == null).ToListAsync(Token));
        Assert.Empty(await db.Recordings.Where(recording => recording.Mbid == null).ToListAsync(Token));
        await EverythingIsLovedAsync(db);
    }

    /// <summary>A heart on every song and album a download filed, to see each follow its files.</summary>
    private async Task LoveEverythingAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        var recordings = await db.Recordings.Select(recording => recording.Id).ToListAsync(Token);
        var albums = await db.ReleaseGroups.Select(group => group.Id).ToListAsync(Token);
        db.Loves.AddRange(recordings.Select(recording => new Love { Id = Guid.CreateVersion7(), RecordingId = recording, LovedUtc = DateTimeOffset.UtcNow }));
        db.Loves.AddRange(albums.Select(album => new Love { Id = Guid.CreateVersion7(), ReleaseGroupId = album, LovedUtc = DateTimeOffset.UtcNow }));
        await db.SaveChangesAsync(Token);
    }

    private async Task EverythingIsLovedAsync(FonotecaDbContext db)
    {
        var songs = await db.Loves.Select(love => love.RecordingId).ToListAsync(Token);
        var albums = await db.Loves.Select(love => love.ReleaseGroupId).ToListAsync(Token);
        Assert.All(await db.MediaFiles.ToListAsync(Token), file =>
        {
            Assert.Contains(file.RecordingId, songs);
            Assert.Contains(file.ReleaseGroupId, albums);
        });
    }

    /// <summary>
    /// A recording MusicBrainz named by ISRC keeps its heart when the pressing
    /// later seats the file on another: it is no download's own row, and stays
    /// for its other copies.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeartOnARecordingMusicBrainzKnowsStaysWhenItsFileMoves(bool byHand)
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var named = new Mbid(Guid.CreateVersion7());
        var musicBrainz = new StubMusicBrainz { Isrcs = { ["USWB10101367"] = [named] } };
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);

        var catchUp = factory.Services.GetRequiredService<MusicBrainzCatchUp>();
        Assert.Equal((0, 1), await catchUp.SweepAsync(Token));
        await LoveEverythingAsync();

        if (byHand)
        {
            musicBrainz.Releases.Add(Rumours() with { Barcode = null });

            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                new Uri("/api/catalogue/matching/folders/album", UriKind.Relative),
                new FolderAlbumRequest("Fleetwood Mac/Rumours", RumoursRelease.Value),
                Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            await using (var db = PostgresFixture.CreateContext(_connectionString))
            {
                (await db.Releases.SingleAsync(Token)).ProviderCheckedUtc = null;
                await db.SaveChangesAsync(Token);
            }

            musicBrainz.Releases.Add(Rumours());
            Assert.Equal((1, 0), await catchUp.SweepAsync(Token));
        }

        await using var check = PostgresFixture.CreateContext(_connectionString);
        var dreams = await check.MediaFiles.Include(file => file.Recording).SingleAsync(file => file.Path.EndsWith("Dreams.flac"), Token);
        var held = await check.Recordings.SingleAsync(recording => recording.Mbid == named, Token);

        Assert.Equal(Dreams, dreams.Recording!.Mbid);
        Assert.True(await check.Loves.AnyAsync(love => love.RecordingId == held.Id, Token));
        Assert.False(await check.Loves.AnyAsync(love => love.RecordingId == dreams.RecordingId, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTracksAnIsrcNamesAsOneRecordingShareIt(bool heldAlready)
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz { Isrcs = { ["USWB10101361"] = [Dreams], ["USWB10101367"] = [Dreams] } };
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);
        await LoveEverythingAsync();

        // Held already, both of the shop's rows fold into it in one save, and one heart stays.
        if (heldAlready)
        {
            await using var seed = PostgresFixture.CreateContext(_connectionString);
            seed.Recordings.Add(new Recording { Id = RecordingId.New(), Title = "Dreams", Mbid = Dreams });
            await seed.SaveChangesAsync(Token);
        }

        Assert.Equal((0, 2), await factory.Services.GetRequiredService<MusicBrainzCatchUp>().SweepAsync(Token));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var files = await db.MediaFiles.ToListAsync(Token);

        Assert.Single(files.Select(file => file.RecordingId).Distinct());
        Assert.Equal(Dreams, (await db.Recordings.SingleAsync(Token)).Mbid);
        Assert.NotNull((await db.Releases.SingleAsync(Token)).ProviderCheckedUtc);
        await EverythingIsLovedAsync(db);
        Assert.Equal(1, await db.Loves.CountAsync(love => love.RecordingId != null, Token));
    }

    [Fact]
    public async Task AnIsrcNamingSeveralRecordingsIsNoAnswer()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz { Isrcs = { ["USWB10101367"] = [Dreams, TheChain] } };
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);

        Assert.Equal((0, 0), await factory.Services.GetRequiredService<MusicBrainzCatchUp>().SweepAsync(Token));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.All(await db.Recordings.ToListAsync(Token), recording => Assert.Null(recording.Mbid));
    }

    [Fact]
    public async Task ANamedReleaseThatLacksATrackLeavesTheDownloadsRecordings()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var musicBrainz = new StubMusicBrainz();
        var factory = Factory(mutation: false, musicBrainz);
        await DownloadAsync(factory);

        // Prints The Chain and not Dreams: no file is seated, rather than one.
        musicBrainz.Releases.Add(Rumours() with
        {
            Barcode = null,
            Tracks = [new MusicBrainzTrack(1, 1, "1", "The Chain", TimeSpan.FromSeconds(270), TheChain, [])],
        });

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/folders/album", UriKind.Relative),
            new FolderAlbumRequest("Fleetwood Mac/Rumours", RumoursRelease.Value),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var files = await db.MediaFiles.Include(file => file.Recording).ToListAsync(Token);

        Assert.All(files, file =>
        {
            Assert.Null(file.Recording!.Mbid);
            Assert.Equal(ReleaseAttributionOutcome.AlbumByPerson, file.AttributionOutcome);
            Assert.Equal(EnrichmentOutcome.LinkedByProvider, file.EnrichmentOutcome);
        });
    }

    [Fact]
    public async Task AnAlbumWhoseQuestionIsRefusedDoesNotKeepTheNextFromBeingAsked()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var overAndOver = new Mbid(Guid.Parse("4b1c2e0a-0000-4000-8000-000000000005"));
        var musicBrainz = new StubMusicBrainz { Refused = { "USWB10101361" }, Isrcs = { ["USWB19900001"] = [overAndOver] } };
        var factory = Factory(mutation: false, musicBrainz);

        await DownloadAsync(factory);
        await DownloadAsync(factory, album: "2");

        Assert.Equal((0, 1), await factory.Services.GetRequiredService<MusicBrainzCatchUp>().SweepAsync(Token));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var releases = await db.Releases.ToDictionaryAsync(release => release.Title, Token);

        Assert.Null(releases["Rumours"].ProviderCheckedUtc);
        Assert.NotNull(releases["Tusk"].ProviderCheckedUtc);
        Assert.Equal(overAndOver, (await db.Recordings.SingleAsync(recording => recording.Title == "Over & Over", Token)).Mbid);
    }

    private static MusicBrainzRelease Rumours() =>
        new(
            Id: RumoursRelease,
            Title: "Rumours",
            ReleasedOn: new ReleaseDate(2001, null, null),
            Country: "XW",
            Status: "Official",
            Barcode: "0603497941032",
            Labels: [],
            ReleaseGroupId: RumoursGroup,
            ReleaseGroupTitle: "Rumours",
            PrimaryType: "Album",
            SecondaryTypes: [],
            Credits: [],
            Tracks:
            [
                new MusicBrainzTrack(1, 1, "1", "The Chain", TimeSpan.FromSeconds(270), TheChain, []),
                new MusicBrainzTrack(1, 2, "2", "Dreams", TimeSpan.FromSeconds(257), Dreams, []),
            ],
            Media: [new MusicBrainzMediumSummary(1, "Digital Media", 2)]);

    /// <summary>A MusicBrainz that knows what each test puts in it, and counts what it is asked.</summary>
    private sealed class StubMusicBrainz : IMusicBrainzCatalogue
    {
        public List<MusicBrainzRelease> Releases { get; } = [];

        public Dictionary<string, List<Mbid>> Isrcs { get; } = new(StringComparer.Ordinal);

        public bool Down { get; init; }

        /// <summary>ISRCs MusicBrainz refuses, as it would a malformed one.</summary>
        public HashSet<string> Refused { get; } = new(StringComparer.Ordinal);

        public int Calls { get; private set; }

        public Task<IReadOnlyList<MusicBrainzReleaseMatch>> SearchReleasesAsync(string query, int limit, CancellationToken cancellationToken = default)
        {
            Asked();

            return Task.FromResult<IReadOnlyList<MusicBrainzReleaseMatch>>(
            [
                .. Releases
                    .Where(release => release.Barcode is not null && query.Contains(release.Barcode, StringComparison.Ordinal))
                    .Select(release => new MusicBrainzReleaseMatch(
                        new MusicBrainzReleaseCandidate(
                            release.Id, release.Title, release.ReleasedOn, release.Country, release.Status, release.Barcode,
                            release.ReleaseGroupId, release.ReleaseGroupTitle, release.PrimaryType, release.SecondaryTypes, release.Media!),
                        [],
                        100)),
            ]);
        }

        public Task<MusicBrainzRelease?> GetReleaseAsync(Mbid id, CancellationToken cancellationToken = default)
        {
            Asked();
            return Task.FromResult(Releases.FirstOrDefault(release => release.Id == id));
        }

        public Task<IReadOnlyList<string>?> GetReleaseGroupGenresAsync(Mbid id, CancellationToken cancellationToken = default) =>

            Task.FromResult<IReadOnlyList<string>?>([]);


        public Task<IReadOnlyList<Mbid>> RecordingsForIsrcAsync(string isrc, CancellationToken cancellationToken = default)
        {
            Asked();
            if (Refused.Contains(isrc)) throw new ProviderRejectedException("musicbrainz", "Invalid ISRC.");
            return Task.FromResult<IReadOnlyList<Mbid>>(Isrcs.TryGetValue(isrc, out var found) ? found : []);
        }

        public Task<MusicBrainzRecording?> GetRecordingAsync(Mbid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzRecording?>(null);

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(Mbid recording, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseCandidate>>([]);

        public Task<IReadOnlyList<MusicBrainzReleaseGroupMatch>> SearchReleaseGroupsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseGroupMatch>>([]);

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForReleaseGroupAsync(Mbid releaseGroup, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseCandidate>>([]);

        public Task<MusicBrainzArtist?> GetArtistAsync(Mbid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzArtist?>(null);

        public Task<MusicBrainzWork?> GetWorkAsync(Mbid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzWork?>(null);

        public Task<MusicBrainzDiscography> BrowseReleaseGroupsForArtistAsync(Mbid artist, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MusicBrainzDiscography([], Complete: true));

        private void Asked()
        {
            Calls++;
            if (Down) throw new ProviderUnavailableException("musicbrainz", "Down.");
        }
    }
}
