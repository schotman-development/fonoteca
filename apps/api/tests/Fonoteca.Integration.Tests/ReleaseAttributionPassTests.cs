using Fonoteca.Api.Configuration;
using Fonoteca.Api.Library;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The attribution pass, against real PostgreSQL and a stubbed MusicBrainz.
/// </summary>
/// <remarks>
/// What is under test here is not the rule — <c>ReleaseAttributionTests</c> pins
/// that with no database at all — but everything around it: that a component is
/// discovered from a seed without anyone naming it, that only the releases
/// something was filed under are written, that the track list persisted is the
/// whole one rather than the part the library holds, and that a second pass
/// changes nothing.
///
/// Seeded paths are deliberately misleading in one test, because the pass must
/// not read them.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class ReleaseAttributionPassTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Mbid AlbumId = Mb("11111111-1111-4111-8111-111111111111");
    private static readonly Mbid RemasterId = Mb("22222222-2222-4222-8222-222222222222");
    private static readonly Mbid CompilationId = Mb("33333333-3333-4333-8333-333333333333");
    private static readonly Mbid VinylId = Mb("44444444-4444-4444-8444-444444444444");
    private static readonly Mbid GroupId = Mb("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private readonly List<ServiceProvider> _providers = [];

    private string _connectionString = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();
    }

    /// <summary>
    /// An album is found, written whole, and its files placed on it — starting
    /// from one file and a folder layout that says something else entirely.
    /// </summary>
    [Fact]
    public async Task AnAlbumIsAssembledFromOneSeedWithoutReadingTheFolders()
    {
        // Three files of one album, scattered across three directories with
        // three different album names. If any of this reached the answer the
        // test would fail; none of it is ever read.
        await SeedAsync(
            ("Misc/Rips/track01.flac", Song(1), 180),
            ("Compilations/Best Of 1998/07 - unknown.flac", Song(2), 200),
            ("Downloads/incoming/x.flac", Song(3), 220));

        var catalogue = new StubCatalogue()
            .With(Album())
            .On(Song(1), AlbumId)
            .On(Song(2), AlbumId)
            .On(Song(3), AlbumId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = Assert.Single(await db.Releases.ToListAsync(Token));
        Assert.Equal("Album", release.Title);
        Assert.Equal(AlbumId, release.Mbid);
        Assert.Equal(3, release.TrackCount);
        Assert.Equal(1, release.DiscCount);
        Assert.Equal("CD", release.MediumFormats);

        // The year-only date the old DateOnly column could not have held.
        Assert.Equal(2007, release.ReleasedYear);
        Assert.Null(release.ReleasedMonth);

        var files = await db.MediaFiles.OrderBy(f => f.Path).ToListAsync(Token);

        Assert.All(files, file =>
        {
            Assert.Equal(ReleaseAttributionOutcome.Attributed, file.AttributionOutcome);
            Assert.Equal(release.Id, file.ReleaseId);
            Assert.NotNull(file.TrackId);
            Assert.NotNull(file.ReleaseGroupId);
            Assert.NotNull(file.ReleaseLookupUtc);
            Assert.Equal(0, file.EditionAlternatives);
        });

        // Each file on its own track, none doubled up.
        Assert.Equal(3, files.Select(f => f.TrackId).Distinct().Count());
    }

    /// <summary>
    /// A long album is found from one file, which the prune very nearly made
    /// impossible.
    /// </summary>
    /// <remarks>
    /// The regression test for the first live run, which refused 1,149 of 1,247
    /// files. The prune bounds a release's coverage by the recordings a browse
    /// has placed on it, and after the opening browse that is one — so a
    /// twelve-track album scores 1/12, falls under the floor, and is discarded
    /// before its track list is ever fetched. Every album in the library was.
    ///
    /// Three tracks was not enough to catch it: 1/3 clears a floor of 0.25. It
    /// takes an album long enough for one track to be a small fraction of it,
    /// which is to say an album of an ordinary length.
    /// </remarks>
    [Fact]
    public async Task AnAlbumTooLongForOneTrackToClearThePruneIsStillFound()
    {
        var files = Enumerable.Range(1, 12)
            .Select(index => ($"a/{index:D2}.flac", Song(index), 180 + index))
            .ToArray();

        await SeedAsync(files);

        var album = Album() with
        {
            Tracks = [.. Enumerable.Range(1, 12).Select(index => Track(index, Song(index), 180 + index))],
        };

        var catalogue = new StubCatalogue().With(album);

        foreach (var (_, recording, _) in files) catalogue.On(recording, AlbumId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(1, await db.Releases.CountAsync(Token));
        Assert.Equal(12, await db.MediaFiles.CountAsync(f => f.ReleaseId != null, Token));

        Assert.Equal(
            12,
            await db.MediaFiles.CountAsync(
                f => f.AttributionOutcome == ReleaseAttributionOutcome.Attributed, Token));
    }

    /// <summary>
    /// Two releases in one component that both list a track nobody owns.
    /// </summary>
    /// <remarks>
    /// The other regression from the first live run. EF queries the database
    /// rather than the change tracker, so the recording minted for the first
    /// release's track list is invisible to the second — and the component dies
    /// on IX_Recordings_Mbid, taking every file in it down with it.
    /// </remarks>
    [Fact]
    public async Task TwoReleasesSharingATrackNobodyOwnsDoNotCollide()
    {
        await SeedAsync(
            ("a/1.flac", Song(1), 180),
            ("a/2.flac", Song(2), 200),
            ("a/3.flac", Song(3), 220));

        // A second edition listing the same unowned bonus track as the first.
        var bonus = Song(500);

        var first = Album() with
        {
            Tracks = [Track(1, Song(1), 180), Track(2, Song(2), 200), Track(3, bonus, 240)],
        };

        var second = Album() with
        {
            Id = RemasterId,
            Status = "Promotion",
            Tracks = [Track(1, Song(1), 181), Track(2, Song(3), 220), Track(3, bonus, 241)],
        };

        var catalogue = new StubCatalogue()
            .With(first)
            .With(second)
            .On(Song(1), AlbumId, RemasterId)
            .On(Song(2), AlbumId)
            .On(Song(3), RemasterId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        // Nothing failed, and the shared unowned recording exists exactly once.
        Assert.Equal(
            0,
            await db.MediaFiles.CountAsync(
                f => f.AttributionOutcome == ReleaseAttributionOutcome.LookupFailed, Token));

        Assert.Equal(1, await db.Recordings.CountAsync(r => r.Mbid == bonus, Token));
    }

    /// <summary>
    /// Two pressings of one album, both filed under, sharing a release group.
    /// </summary>
    /// <remarks>
    /// The third regression from the live runs and the most common of them: two
    /// editions of an album share a release group by definition, and EF queries
    /// the database rather than the change tracker — so the group added for the
    /// first is invisible to the second, and the component dies on
    /// IX_ReleaseGroups_Mbid taking every one of its files with it. 51
    /// components in one run.
    /// </remarks>
    [Fact]
    public async Task TwoEditionsSharingAReleaseGroupDoNotCollide()
    {
        await SeedAsync(
            ("a/1.flac", Song(1), 180),
            ("a/2.flac", Song(2), 200),
            ("a/3.flac", Song(3), 220),
            ("b/1.flac", Song(4), 300),
            ("b/2.flac", Song(5), 320));

        // Same release group, different track lists, so both are filed under.
        var deluxe = Album() with
        {
            Id = RemasterId,
            Title = "Album (deluxe)",
            Tracks = [Track(1, Song(4), 300), Track(2, Song(5), 320)],
        };

        var catalogue = new StubCatalogue()
            .With(Album())
            .With(deluxe)
            .On(Song(1), AlbumId)
            .On(Song(2), AlbumId)
            .On(Song(3), AlbumId)
            .On(Song(4), RemasterId)
            .On(Song(5), RemasterId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(
            0,
            await db.MediaFiles.CountAsync(
                f => f.AttributionOutcome == ReleaseAttributionOutcome.LookupFailed, Token));

        Assert.Equal(2, await db.Releases.CountAsync(Token));
        Assert.Equal(1, await db.ReleaseGroups.CountAsync(Token));
        Assert.Equal(5, await db.MediaFiles.CountAsync(f => f.ReleaseId != null, Token));
    }

    /// <summary>
    /// The whole track list is written, not only the part the library holds —
    /// which is what makes a missing track visible as missing.
    /// </summary>
    [Fact]
    public async Task TheWholeTrackListIsPersistedIncludingTracksNobodyOwns()
    {
        await SeedAsync(
            ("a/1.flac", Song(1), 180),
            ("a/2.flac", Song(2), 200));

        var catalogue = new StubCatalogue()
            .With(Album())
            .On(Song(1), AlbumId)
            .On(Song(2), AlbumId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var tracks = await db.Tracks.OrderBy(t => t.Position).ToListAsync(Token);

        Assert.Equal(3, tracks.Count);
        Assert.Equal([1, 2, 3], tracks.Select(t => t.Position).ToArray());

        // The third track's recording is created even though no file holds it.
        Assert.Equal(3, await db.Recordings.CountAsync(Token));

        // And the per-release length is stored, which is the number the edition
        // was chosen by and the one a track list should print.
        Assert.Equal(TimeSpan.FromSeconds(220), tracks[2].Length);

        var owned = await db.MediaFiles.CountAsync(f => f.TrackId != null, Token);
        Assert.Equal(2, owned);
    }

    /// <summary>
    /// A compilation the files also appear on is not written at all, because
    /// nothing was filed under it.
    /// </summary>
    /// <remarks>
    /// The candidate set for a real component runs to hundreds of releases.
    /// Persisting them would turn the release list into a browse of MusicBrainz
    /// rather than of the library.
    /// </remarks>
    [Fact]
    public async Task CandidateReleasesNothingWasFiledUnderAreNotWritten()
    {
        await SeedAsync(
            ("a/1.flac", Song(1), 180),
            ("a/2.flac", Song(2), 200),
            ("a/3.flac", Song(3), 220));

        var catalogue = new StubCatalogue()
            .With(Album())
            .With(Compilation())
            .On(Song(1), AlbumId, CompilationId)
            .On(Song(2), AlbumId, CompilationId)
            .On(Song(3), AlbumId, CompilationId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = Assert.Single(await db.Releases.ToListAsync(Token));
        Assert.Equal(AlbumId, release.Mbid);
    }

    /// <summary>
    /// Editions that fit identically are chosen between, and the count of the
    /// ones passed over is recorded on every file.
    /// </summary>
    [Fact]
    public async Task TiedEditionsAreRecordedAsTiedRatherThanAsCertain()
    {
        await SeedAsync(
            ("a/1.flac", Song(1), 180),
            ("a/2.flac", Song(2), 200),
            ("a/3.flac", Song(3), 220));

        var catalogue = new StubCatalogue()
            .With(Album())
            .With(Album() with { Id = VinylId, Status = "Official" })
            .With(Album() with { Id = RemasterId, Status = "Official" })
            .On(Song(1), AlbumId, VinylId, RemasterId)
            .On(Song(2), AlbumId, VinylId, RemasterId)
            .On(Song(3), AlbumId, VinylId, RemasterId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var files = await db.MediaFiles.ToListAsync(Token);

        Assert.All(files, file =>
        {
            Assert.Equal(ReleaseAttributionOutcome.AttributedAmbiguously, file.AttributionOutcome);
            Assert.Equal(2, file.EditionAlternatives);
            Assert.NotNull(file.ReleaseId);
        });

        // One chosen, two passed over and never written.
        Assert.Equal(1, await db.Releases.CountAsync(Token));
    }

    /// <summary>
    /// A file that fits nothing well is stamped as asked-and-refused, so the pass
    /// terminates instead of re-asking the most expensive question forever.
    /// </summary>
    [Fact]
    public async Task AFileNoReleaseExplainsIsStampedRatherThanLeftOnTheWorklist()
    {
        await SeedAsync(("a/1.flac", Song(1), 180));

        var catalogue = new StubCatalogue()
            .With(Compilation())
            .On(Song(1), CompilationId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var file = Assert.Single(await db.MediaFiles.ToListAsync(Token));

        Assert.Equal(ReleaseAttributionOutcome.NoConfidentFit, file.AttributionOutcome);
        Assert.Null(file.ReleaseId);
        Assert.NotNull(file.ReleaseLookupUtc);

        Assert.Empty(await db.Releases.ToListAsync(Token));

        var service = Build(catalogue);
        Assert.Equal(0, await service.CountPendingAsync(Token));
    }

    /// <summary>
    /// A second pass over unchanged data does nothing and changes nothing.
    /// </summary>
    [Fact]
    public async Task ASecondPassIsANoOp()
    {
        await SeedAsync(
            ("a/1.flac", Song(1), 180),
            ("a/2.flac", Song(2), 200),
            ("a/3.flac", Song(3), 220));

        var catalogue = new StubCatalogue()
            .With(Album())
            .On(Song(1), AlbumId)
            .On(Song(2), AlbumId)
            .On(Song(3), AlbumId);

        await RunAsync(catalogue);

        var afterFirst = catalogue.Calls;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            Assert.Equal(1, await db.Releases.CountAsync(Token));
            Assert.Equal(3, await db.Tracks.CountAsync(Token));
        }

        await RunAsync(catalogue);

        // Nothing was pending, so nothing was fetched.
        Assert.Equal(afterFirst, catalogue.Calls);

        await using var after = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(1, await after.Releases.CountAsync(Token));
        Assert.Equal(3, await after.Tracks.CountAsync(Token));
        Assert.Equal(3, await after.MediaFiles.CountAsync(f => f.ReleaseId != null, Token));
    }

    /// <summary>
    /// A recording MusicBrainz puts on no release is a different answer from one
    /// that fits nothing, and the row says which.
    /// </summary>
    [Fact]
    public async Task ARecordingOnNoReleaseIsRecordedAsSuch()
    {
        await SeedAsync(("a/1.flac", Song(9), 180));

        await RunAsync(new StubCatalogue());

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var file = Assert.Single(await db.MediaFiles.ToListAsync(Token));
        Assert.Equal(ReleaseAttributionOutcome.NoCandidate, file.AttributionOutcome);
    }

    /// <summary>
    /// Only one pass touches the catalogue at a time.
    /// </summary>
    [Fact]
    public async Task AttributionIsRefusedWhileAnotherPassHoldsTheGate()
    {
        await SeedAsync(("a/1.flac", Song(1), 180));

        var services = Services(new StubCatalogue());
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var gate = provider.GetRequiredService<LibraryWorkGate>();
        Assert.True(gate.TryEnter("library.scan", out var lease));

        using (lease)
        {
            var attribution = provider.GetRequiredService<ReleaseAttributionService>();
            Assert.Equal(AttributionStatus.AlreadyRunning, attribution.Start().Status);
        }
    }

    private async Task RunAsync(StubCatalogue catalogue)
    {
        var attribution = Build(catalogue);

        Assert.Equal(AttributionStatus.Started, attribution.Start().Status);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (attribution.IsRunning && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Token);
        }

        Assert.False(attribution.IsRunning, "The attribution pass did not finish.");
    }

    private ReleaseAttributionService Build(StubCatalogue catalogue)
    {
        var provider = Services(catalogue).BuildServiceProvider();
        _providers.Add(provider);

        return provider.GetRequiredService<ReleaseAttributionService>();
    }

    private ServiceCollection Services(StubCatalogue catalogue)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSignalR();
        services.AddDbContext<FonotecaDbContext>(options => options.UseNpgsql(_connectionString));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IMusicBrainzCatalogue>(catalogue);
        services.AddSingleton<LibraryWorkGate>();
        services.AddSingleton<ReleaseAttributionService>();
        services.AddSingleton<IHostApplicationLifetime, NeverStops>();
        services.AddSingleton<IOptions<FonotecaOptions>>(
            new OptionsWrapper<FonotecaOptions>(new FonotecaOptions { LibraryPath = "/tmp" }));

        return services;
    }

    /// <summary>
    /// Seeds files already linked to recordings, as enrichment leaves them.
    /// </summary>
    private async Task SeedAsync(params (string Path, Mbid Recording, int Seconds)[] files)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        foreach (var (path, recording, seconds) in files)
        {
            var row = await db.Recordings.FirstOrDefaultAsync(r => r.Mbid == recording, Token);

            if (row is null)
            {
                row = new Recording
                {
                    Id = RecordingId.New(),
                    Title = $"Song {recording.Value.ToString()[..8]}",
                    Mbid = recording,
                };

                db.Recordings.Add(row);
            }

            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = path,
                SizeBytes = 1024,
                LastModifiedUtc = StoreTime.ToStorePrecision(DateTimeOffset.UtcNow),
                RecordingId = row.Id,
                RecordingLookupUtc = DateTimeOffset.UtcNow,
                EnrichmentOutcome = EnrichmentOutcome.Linked,
                FingerprintDuration = TimeSpan.FromSeconds(seconds),
            });
        }

        await db.SaveChangesAsync(Token);
    }

    /// <summary>Three tracks, exactly the lengths the seeded files measure.</summary>
    private static MusicBrainzRelease Album() =>
        new(
            Id: AlbumId,
            Title: "Album",
            ReleasedOn: new ReleaseDate(2007, null, null),
            Country: "GB",
            Status: "Official",
            Barcode: "5051011111129",
            Labels: [new MusicBrainzLabel(null, "A Label", "CAT-1")],
            ReleaseGroupId: GroupId,
            ReleaseGroupTitle: "Album",
            PrimaryType: "Album",
            SecondaryTypes: [],
            Credits: [],
            Tracks:
            [
                Track(1, Song(1), 180),
                Track(2, Song(2), 200),
                Track(3, Song(3), 220),
            ]);

    /// <summary>Twenty tracks, three of which the library holds. Coverage 0.15.</summary>
    private static MusicBrainzRelease Compilation() =>
        Album() with
        {
            Id = CompilationId,
            Title = "Greatest Hits",
            ReleaseGroupId = Mb("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            ReleaseGroupTitle = "Greatest Hits",
            SecondaryTypes = ["Compilation"],
            Tracks =
            [
                Track(1, Song(1), 182),
                Track(2, Song(2), 202),
                Track(3, Song(3), 222),
                .. Enumerable.Range(4, 17).Select(index => Track(index, Song(100 + index), 200)),
            ],
        };

    private static MusicBrainzTrack Track(int position, Mbid recording, int seconds) =>
        new(
            DiscNumber: 1,
            Position: position,
            Number: position.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title: $"Track {position}",
            Length: TimeSpan.FromSeconds(seconds),
            RecordingId: recording,
            Credits: []);

    private static Mbid Song(int number) =>
        new(new Guid($"99999999-9999-4999-8999-{number:D12}"));

    private static Mbid Mb(string value) => new(Guid.Parse(value));

    /// <summary>
    /// A MusicBrainz that knows which releases hold which recordings.
    /// </summary>
    /// <remarks>
    /// Modelled as the real service is — a browse gives candidates with track
    /// counts and no tracks, a lookup gives the track list — so the pass's
    /// two-stage shape and its prune are exercised rather than bypassed. The call
    /// counter is what proves a second pass fetches nothing.
    /// </remarks>
    private sealed class StubCatalogue : IMusicBrainzCatalogue
    {
        private readonly Dictionary<Mbid, MusicBrainzRelease> _releases = [];
        private readonly Dictionary<Mbid, List<Mbid>> _appearsOn = [];

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public StubCatalogue With(MusicBrainzRelease release)
        {
            _releases[release.Id] = release;
            return this;
        }

        /// <summary>This recording is on these releases, as a browse would say.</summary>
        public StubCatalogue On(Mbid recording, params Mbid[] releases)
        {
            _appearsOn[recording] = [.. releases];
            return this;
        }

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(
            Mbid recording,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            var candidates = _appearsOn.TryGetValue(recording, out var ids)
                ? ids.Where(_releases.ContainsKey).Select(id => Summarise(_releases[id])).ToList()
                : [];

            return Task.FromResult<IReadOnlyList<MusicBrainzReleaseCandidate>>(candidates);
        }

        public Task<MusicBrainzRelease?> GetReleaseAsync(
            Mbid id,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_releases.GetValueOrDefault(id));
        }

        public Task<MusicBrainzRecording?> GetRecordingAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Attribution reads recordings from the catalogue; nothing here should call this.");

        public Task<MusicBrainzWork?> GetWorkAsync(Mbid id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Attribution does not look up works; nothing here should call this.");

        /// <summary>A browse result: counts, never tracks.</summary>
        private static MusicBrainzReleaseCandidate Summarise(MusicBrainzRelease release) =>
            new(
                Id: release.Id,
                Title: release.Title,
                ReleasedOn: release.ReleasedOn,
                Country: release.Country,
                Status: release.Status,
                Barcode: release.Barcode,
                ReleaseGroupId: release.ReleaseGroupId,
                ReleaseGroupTitle: release.ReleaseGroupTitle,
                PrimaryType: release.PrimaryType,
                SecondaryTypes: release.SecondaryTypes,
                Media: [new MusicBrainzMediumSummary(1, "CD", release.Tracks.Count)]);
    }

    /// <summary>Never stops, because these tests own the lifetime themselves.</summary>
    private sealed class NeverStops : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
