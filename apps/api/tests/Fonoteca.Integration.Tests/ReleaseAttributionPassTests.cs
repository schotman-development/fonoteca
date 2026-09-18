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
    private static readonly Mbid SecondAlbumId = Mb("55555555-5555-4555-8555-555555555555");
    private static readonly Mbid BoxSetId = Mb("66666666-6666-4666-8666-666666666666");
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
    /// An album is found, written whole, and its files placed on it — from a
    /// folder whose name says something else entirely.
    /// </summary>
    /// <remarks>
    /// <b>The folder is the boundary and never the label.</b> This test used to
    /// scatter one album across three unrelated directories to prove the pass
    /// ignored them; the grouping is now taken from the folder, so the half worth
    /// keeping is the other half. Every file here sits in a directory naming a
    /// 1998 compilation, the audio is a 2007 album, and the answer is the album —
    /// which is exactly the distinction <see cref="AlbumFolder"/> draws.
    /// </remarks>
    [Fact]
    public async Task TheFoldersNameIsStillNeverRead()
    {
        await SeedAsync(
            ("Compilations/Best Of 1998/01 - unknown.flac", Song(1), 180),
            ("Compilations/Best Of 1998/07 - unknown.flac", Song(2), 200),
            ("Compilations/Best Of 1998/12 - unknown.flac", Song(3), 220));

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
    /// Two albums glued together by one box set stay two albums.
    /// </summary>
    /// <remarks>
    /// <b>The regression this rewrite exists for.</b> Under the expanding gather
    /// the seed's browse reaches the box set, the box set's track list names the
    /// second album's recordings, and <c>AdmitAsync</c> pulls that album's files
    /// into the same component. Six files then face a six-track box set that
    /// covers all of them — weight 6.0 against each album's 3.0 — and it takes
    /// the lot. Nothing about the rule is wrong; the set handed to it was.
    ///
    /// Cut at the folder, the box set explains three of six slots for either
    /// album: coverage 0.50, weight 1.50, and it does not clear the first rung at
    /// all. Each album covers its own folder whole.
    /// </remarks>
    [Fact]
    public async Task TwoAlbumsSharingABoxSetAreNotCollapsedIntoIt()
    {
        await SeedAsync(
            ("Artist/First Album/01.flac", Song(1), 180),
            ("Artist/First Album/02.flac", Song(2), 200),
            ("Artist/First Album/03.flac", Song(3), 220),
            ("Artist/Second Album/01.flac", Song(4), 240),
            ("Artist/Second Album/02.flac", Song(5), 260),
            ("Artist/Second Album/03.flac", Song(6), 280));

        var catalogue = new StubCatalogue()
            .With(Album())
            .With(SecondAlbum())
            .With(BoxSet())
            .On(Song(1), AlbumId, BoxSetId)
            .On(Song(2), AlbumId, BoxSetId)
            .On(Song(3), AlbumId, BoxSetId)
            .On(Song(4), SecondAlbumId, BoxSetId)
            .On(Song(5), SecondAlbumId, BoxSetId)
            .On(Song(6), SecondAlbumId, BoxSetId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var written = await db.Releases.ToListAsync(Token);

        Assert.DoesNotContain(written, release => release.Mbid == BoxSetId);
        Assert.Equal(
            ["Album", "Second Album"],
            written.Select(release => release.Title).Order(StringComparer.Ordinal));

        var files = await db.MediaFiles
            .Include(f => f.Release)
            .OrderBy(f => f.Path)
            .ToListAsync(Token);

        Assert.All(files, file =>
            Assert.Equal(ReleaseAttributionOutcome.Attributed, file.AttributionOutcome));

        // Each folder on its own album, and the two components decided apart:
        // one clock read per component is what the worklist prints as an id.
        Assert.Equal(
            ["Album", "Album", "Album", "Second Album", "Second Album", "Second Album"],
            files.Select(file => file.Release!.Title));

        Assert.Equal(2, files.Select(file => file.ReleaseLookupUtc).Distinct().Count());
    }

    /// <summary>
    /// A file loose under an artist does not drag that artist's albums in with it.
    /// </summary>
    /// <remarks>
    /// <b>The one shape a prefix match gets wrong, and it would be very hard to
    /// see.</b> A file at <c>Prince/x.flac</c> has <c>Prince</c> for an album
    /// folder, so the query that narrows by <c>Prince/</c> returns every file
    /// that artist has — one component for a whole discography, which is the
    /// failure the folder cut exists to remove, arriving by the back door. The
    /// rule is applied again in memory for exactly this, and one such file is
    /// live in the target library today.
    ///
    /// Two components, not one, is the assertion: the loose file is its own, and
    /// the album is decided without it.
    /// </remarks>
    [Fact]
    public async Task AFileLooseUnderAnArtistDoesNotJoinThatArtistsAlbums()
    {
        await SeedAsync(
            ("Artist/loose.flac", Song(4), 240),
            ("Artist/Album/01.flac", Song(1), 180),
            ("Artist/Album/02.flac", Song(2), 200),
            ("Artist/Album/03.flac", Song(3), 220));

        var catalogue = new StubCatalogue()
            .With(Album())
            .With(SecondAlbum())
            .On(Song(1), AlbumId)
            .On(Song(2), AlbumId)
            .On(Song(3), AlbumId)
            .On(Song(4), SecondAlbumId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var files = await db.MediaFiles
            .Include(f => f.Release)
            .OrderBy(f => f.Path)
            .ToListAsync(Token);

        // Two components: the loose file was never in the album's folder.
        Assert.Equal(2, files.Select(file => file.ReleaseLookupUtc).Distinct().Count());

        var album = files.Where(file => file.Path.StartsWith("Artist/Album/", StringComparison.Ordinal));

        Assert.All(album, file =>
        {
            Assert.Equal(ReleaseAttributionOutcome.Attributed, file.AttributionOutcome);
            Assert.Equal("Album", file.Release!.Title);
        });

        // One file covering one of three tracks clears no rung, so it stays open —
        // which is the honest answer and not the album's problem either way.
        var loose = files.Single(file => file.Path == "Artist/loose.flac");

        Assert.NotNull(loose.ReleaseLookupUtc);
        Assert.NotEqual(ReleaseAttributionOutcome.Attributed, loose.AttributionOutcome);
    }

    /// <summary>
    /// A two-disc rip is one question, not two.
    /// </summary>
    /// <remarks>
    /// <c>CD 01</c> and <c>CD 02</c> are directories of their own, and treating
    /// them as separate components splits a three-track album into a pair of two
    /// and one. The single file left over covers 1/3 of the album, which does not
    /// clear even the singles rung, so it would be refused while its own album sat
    /// written in the catalogue beside it. All 99 of the target library's
    /// depth-three folders are discs, which is what <see cref="AlbumFolder.Depth"/>
    /// is cut for.
    /// </remarks>
    [Fact]
    public async Task DiscFoldersAreOneAlbumAndNotOnePerDisc()
    {
        await SeedAsync(
            ("Artist/Album/CD 01/01.flac", Song(1), 180),
            ("Artist/Album/CD 01/02.flac", Song(2), 200),
            ("Artist/Album/CD 02/01.flac", Song(3), 220));

        var catalogue = new StubCatalogue()
            .With(Album())
            .On(Song(1), AlbumId)
            .On(Song(2), AlbumId)
            .On(Song(3), AlbumId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var files = await db.MediaFiles.ToListAsync(Token);

        Assert.All(files, file =>
            Assert.Equal(ReleaseAttributionOutcome.Attributed, file.AttributionOutcome));

        Assert.Single(files.Select(file => file.ReleaseLookupUtc).Distinct());
    }

    /// <summary>
    /// A long album is found from one file, which the prune very nearly made
    /// impossible.
    /// </summary>
    /// <remarks>
    /// The regression test for the first live run, which refused 1,149 of 1,247
    /// files. The prune bounded a release's coverage by the recordings a browse
    /// had placed on it, which after the opening browse was one — so a
    /// twelve-track album scored 1/12, fell under the floor, and was discarded
    /// before its track list was ever fetched. Every album in the library was.
    ///
    /// <b>That failure is now structurally unreachable, and this still earns its
    /// place.</b> Browsing the whole folder before pruning anything means the
    /// album is holding twelve of twelve when the prune reads it, so the count
    /// the arithmetic was wrong about is the count that is now correct. What the
    /// test guards is the outcome rather than the mechanism: an album of ordinary
    /// length, held whole, comes back attributed. Three tracks was never enough
    /// to catch the original — 1/3 clears a floor of 0.25 — and twelve still is.
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
    /// A long album reached sideways, through a compilation that shares a few of
    /// its songs.
    /// </summary>
    /// <remarks>
    /// The <i>Muddy Wolf at Red Rocks</i> shape, and the third prune bug. The
    /// component is seeded from a compilation track, so the album is not in the
    /// opening round — it arrives in the second with only the shared songs
    /// counted against it. Judged as a fraction that is 5/25, under the floor,
    /// and the album is discarded: the compilation keeps the five files it
    /// shares and the album's other twenty sit unattributed.
    ///
    /// The whole point of expanding is that those counts are still arriving, so
    /// the test is "does this library plausibly own a chunk of it" rather than
    /// "could it already clear the gate".
    /// </remarks>
    [Fact]
    public async Task AnAlbumReachedThroughACompilationIsStillFetchedAndStillWins()
    {
        // Twenty-five songs of a live album, five of which a thirty-track
        // compilation also carries.
        var albumFiles = Enumerable.Range(1, 25)
            .Select(index => ($"live/{index:D2}.flac", Song(index), 200 + index))
            .ToArray();

        await SeedAsync(albumFiles);

        var live = Album() with
        {
            Id = VinylId,
            Title = "Live at Red Rocks",
            Tracks = [.. Enumerable.Range(1, 25).Select(index => Track(index, Song(index), 200 + index))],
        };

        var compilation = Album() with
        {
            Id = CompilationId,
            Title = "30 Most Slow Blues",
            ReleaseGroupId = Mb("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            Tracks =
            [
                .. Enumerable.Range(1, 5).Select(index => Track(index, Song(index), 200 + index)),
                .. Enumerable.Range(6, 25).Select(index => Track(index, Song(900 + index), 200)),
            ],
        };

        var catalogue = new StubCatalogue().With(live).With(compilation);

        // The five shared songs are on both; the other twenty only on the album.
        // All twenty-five are in one folder, so every one is browsed and the
        // album is the release holding the most of them.
        for (var index = 1; index <= 5; index++) catalogue.On(Song(index), CompilationId, VinylId);
        for (var index = 6; index <= 25; index++) catalogue.On(Song(index), VinylId);

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = Assert.Single(await db.Releases.ToListAsync(Token));
        Assert.Equal("Live at Red Rocks", release.Title);

        // All twenty-five, not the twenty the compilation left behind.
        Assert.Equal(25, await db.MediaFiles.CountAsync(f => f.ReleaseId == release.Id, Token));
    }

    /// <summary>
    /// An album buried under thirty compilations that each reprint some of it.
    /// </summary>
    /// <remarks>
    /// An attempt to reproduce the Michael Jackson failure in a fixture, where it
    /// can be debugged. `Off the Wall` is ten tracks, all ten held, with an
    /// edition matching to the millisecond — and it never reaches the candidate
    /// set on real data, so the files land on whichever compilation did.
    ///
    /// If this passes, the failure is data-specific rather than structural and
    /// the trace has to come from the real mirror.
    /// </remarks>
    [Fact]
    public async Task AnAlbumIsFoundEvenUnderThirtyCompilationsThatReprintIt()
    {
        var albumFiles = Enumerable.Range(1, 10)
            .Select(index => ($"mj/{index:D2}.flac", Song(index), 200 + index))
            .ToArray();

        await SeedAsync(albumFiles);

        var album = Album() with
        {
            Id = VinylId,
            Title = "Off the Wall",
            Tracks = [.. Enumerable.Range(1, 10).Select(index => Track(index, Song(index), 200 + index))],
        };

        var catalogue = new StubCatalogue().With(album);

        // Thirty compilations, each reprinting a rolling window of the album at
        // slightly different lengths, and each padded out with music nobody owns.
        var compilations = new List<Mbid>();

        for (var comp = 0; comp < 30; comp++)
        {
            var id = new Mbid(new Guid($"77777777-7777-4777-8777-{comp:D12}"));
            compilations.Add(id);

            var shared = Enumerable.Range(1, 10)
                .Where(index => (index + comp) % 3 != 0)
                .Select(index => Track(index, Song(index), 202 + index))
                .ToList();

            catalogue.With(Album() with
            {
                Id = id,
                Title = $"Greatest Hits {comp}",
                ReleaseGroupId = new Mbid(new Guid($"88888888-8888-4888-8888-{comp:D12}")),
                Tracks = [.. shared, .. Enumerable.Range(1, 20).Select(n => Track(100 + n, Song(500 + comp * 20 + n), 200))],
            });
        }

        // Every album track is on the album and on most of the compilations, so
        // the seed's opening browse is dominated by them.
        for (var index = 1; index <= 10; index++)
        {
            var on = new List<Mbid> { VinylId };
            on.AddRange(compilations.Where(c => true));
            catalogue.On(Song(index), [.. on]);
        }

        await RunAsync(catalogue);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var chosen = await db.MediaFiles
            .Where(f => f.ReleaseId != null)
            .Select(f => f.Release!.Title)
            .ToListAsync(Token);

        Assert.Equal(10, chosen.Count);
        Assert.All(chosen, title => Assert.Equal("Off the Wall", title));
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

    /// <summary>
    /// The song left out of an album that matched: its cluster names the album's
    /// recording as well as the one enrichment chose, and the pass seats it.
    /// </summary>
    [Fact]
    public async Task AFileIdentifiedAsAnotherTakeOfTheSameAudioFillsTheAlbumsGap()
    {
        var take = Song(90);

        await SeedAsync(
            ("Artist/Album/01.flac", Song(1), 180),
            ("Artist/Album/02.flac", Song(2), 200),
            ("Artist/Album/03.flac", take, 220));

        var cluster = Guid.CreateVersion7();

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            var row = await seed.MediaFiles.SingleAsync(f => f.Path == "Artist/Album/03.flac", Token);
            row.AcoustId = new AcoustId(cluster);
            row.Fingerprint = "AQAAfingerprint";
            await seed.SaveChangesAsync(Token);
        }

        var catalogue = new StubCatalogue()
            .With(Album())
            .On(Song(1), AlbumId)
            .On(Song(2), AlbumId);

        var clusters = new StubClusters(
            [new AcoustIdMatch(cluster, 0.97, [new(take, 324), new(Song(3), 4)])]);

        await RunAsync(catalogue, clusters);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var file = await db.MediaFiles
            .Include(f => f.Recording)
            .SingleAsync(f => f.Path == "Artist/Album/03.flac", Token);

        Assert.Equal(ReleaseAttributionOutcome.Attributed, file.AttributionOutcome);
        Assert.NotNull(file.ReleaseId);
        Assert.Equal(Song(3), file.Recording!.Mbid);

        var track = await db.Tracks.SingleAsync(t => t.Id == file.TrackId, Token);
        Assert.Equal(3, track.Position);
        Assert.Equal(file.RecordingId, track.RecordingId);

        // The answer is kept, so the next run asks nobody.
        Assert.NotNull(file.AcoustIdMatchesJson);
        Assert.Equal(1, clusters.Calls);
    }

    private async Task RunAsync(StubCatalogue catalogue, IAcoustIdLookup? clusters = null)
    {
        var attribution = Build(catalogue, clusters);

        Assert.Equal(AttributionStatus.Started, attribution.Start().Status);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (attribution.IsRunning && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Token);
        }

        Assert.False(attribution.IsRunning, "The attribution pass did not finish.");
    }

    private ReleaseAttributionService Build(StubCatalogue catalogue, IAcoustIdLookup? clusters = null)
    {
        var provider = Services(catalogue, clusters).BuildServiceProvider();
        _providers.Add(provider);

        return provider.GetRequiredService<ReleaseAttributionService>();
    }

    private ServiceCollection Services(StubCatalogue catalogue, IAcoustIdLookup? clusters = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSignalR();
        services.AddDbContext<FonotecaDbContext>(options => options.UseNpgsql(_connectionString));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IMusicBrainzCatalogue>(catalogue);
        services.AddSingleton<IAcoustIdLookup>(clusters ?? new StubClusters([]));
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

    /// <summary>A second album, sharing nothing with the first but a box set.</summary>
    private static MusicBrainzRelease SecondAlbum() =>
        Album() with
        {
            Id = SecondAlbumId,
            Title = "Second Album",
            ReleaseGroupId = Mb("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            ReleaseGroupTitle = "Second Album",
            Tracks = [Track(1, Song(4), 240), Track(2, Song(5), 260), Track(3, Song(6), 280)],
        };

    /// <summary>
    /// Both albums on one disc, at the same lengths.
    /// </summary>
    /// <remarks>
    /// Deliberately covers every file the library holds, so that on a merged
    /// component it wins on weight (6.0 against each album's 3.0) and on a folder
    /// it loses on coverage (0.50 against 1.00). Nothing but the size of the set
    /// separates the two answers.
    /// </remarks>
    private static MusicBrainzRelease BoxSet() =>
        Album() with
        {
            Id = BoxSetId,
            Title = "The Collection",
            ReleaseGroupId = Mb("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            ReleaseGroupTitle = "The Collection",
            SecondaryTypes = ["Compilation"],
            Tracks =
            [
                Track(1, Song(1), 180),
                Track(2, Song(2), 200),
                Track(3, Song(3), 220),
                Track(4, Song(4), 240),
                Track(5, Song(5), 260),
                Track(6, Song(6), 280),
            ],
        };

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

        public Task<MusicBrainzDiscography> BrowseReleaseGroupsForArtistAsync(
            Mbid artist,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MusicBrainzDiscography([], Complete: true));

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

        /// <summary>Never searched for. Only the by-hand album screen searches.</summary>
        public Task<IReadOnlyList<MusicBrainzReleaseMatch>> SearchReleasesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseMatch>>([]);

        public Task<MusicBrainzRecording?> GetRecordingAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Attribution reads recordings from the catalogue; nothing here should call this.");

        public Task<MusicBrainzArtist?> GetArtistAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzArtist?>(null);

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

    /// <summary>An AcoustID that gives one answer, and counts how often it was asked.</summary>
    private sealed class StubClusters(IReadOnlyList<AcoustIdMatch> matches) : IAcoustIdLookup
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
            AudioFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(matches);
        }
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
