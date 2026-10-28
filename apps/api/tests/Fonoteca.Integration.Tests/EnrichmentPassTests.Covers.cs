using Fonoteca.Api.Library;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The stage that asks for every sleeve the tag write would draw from.
/// </summary>
public sealed partial class EnrichmentPassTests
{
    private static readonly Mbid OfficialMbid = Mb("a0000000-0000-4000-8000-000000000021");
    private static readonly Mbid BootlegMbid = Mb("a0000000-0000-4000-8000-000000000022");

    /// <summary>A filed release's sleeve is fetched from the archive and stored, and it leaves the count.</summary>
    [Fact]
    public async Task AFiledReleasesSleeveIsStored()
    {
        await SeedFiledReleaseAsync(PressingMbid, "A/One/01.flac", askedAgo: null);

        var archive = new StubArchive(PressingMbid);
        var services = Build(Answering(Recording), ReleasesAnswering(), archive: archive);
        var enrichment = services.GetRequiredService<EnrichmentService>();

        Assert.Equal(1, (await enrichment.CountPendingAsync(Token)).Covers);

        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var cover = await db.ReleaseCovers.AsNoTracking().SingleAsync(Token);

        Assert.Equal(StubArchive.Front, cover.Bytes);
        Assert.Equal(0, (await enrichment.CountPendingAsync(Token)).Covers);
    }

    /// <summary>
    /// "Neither source has one" is believed for a week and not after, a sleeve
    /// found is never asked for again, and neither a release no file is filed
    /// under nor an album with no edition stored is asked about at all.
    /// </summary>
    [Fact]
    public async Task ASleeveNobodyHadIsAskedForAgainAfterAWeekOnly()
    {
        await SeedFiledReleaseAsync(PressingMbid, "A/One/01.flac", askedAgo: TimeSpan.FromDays(6));
        await SeedFiledReleaseAsync(OfficialMbid, "A/Two/01.flac", askedAgo: TimeSpan.FromDays(8));
        await SeedFiledReleaseAsync(BootlegMbid, path: null, askedAgo: null);
        await SeedFiledReleaseAsync(UnfiledMbid, "A/Three/01.flac", askedAgo: TimeSpan.FromDays(30), bytes: StubArchive.Front);

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            var editionless = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Four" };
            seed.ReleaseGroups.Add(editionless);
            seed.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "A/Four/01.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                ReleaseGroupId = editionless.Id,
            });
            await seed.SaveChangesAsync(Token);
        }

        var archive = new StubArchive();
        var services = Build(Answering(Recording), ReleasesAnswering(), archive: archive);

        Assert.Equal(1, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Covers);

        await EnrichAsync(services);

        Assert.Equal([OfficialMbid], archive.Asked);
    }

    /// <summary>
    /// An album whose files are on no edition of it gets the sleeve of the
    /// edition its page shows, the official one, and only that one is asked.
    /// </summary>
    [Fact]
    public async Task AnAlbumFiledByItsGroupAloneGetsItsDisplayEditionsSleeve()
    {
        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "One" };
            seed.ReleaseGroups.Add(group);
            seed.Releases.Add(new Release
            {
                Id = ReleaseId.New(), Title = "One", Mbid = BootlegMbid, Status = "Bootleg", ReleaseGroupId = group.Id,
            });
            seed.Releases.Add(new Release
            {
                Id = ReleaseId.New(), Title = "One", Mbid = OfficialMbid, Status = "Official", ReleaseGroupId = group.Id,
            });
            seed.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "A/One/01.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                ReleaseGroupId = group.Id,
            });
            await seed.SaveChangesAsync(Token);
        }

        var archive = new StubArchive(OfficialMbid);
        var services = Build(Answering(Recording), ReleasesAnswering(), archive: archive);
        var enrichment = services.GetRequiredService<EnrichmentService>();

        Assert.Equal(1, (await enrichment.CountPendingAsync(Token)).Covers);

        await EnrichAsync(services);

        Assert.Equal([OfficialMbid], archive.Asked);
        Assert.Equal(0, (await enrichment.CountPendingAsync(Token)).Covers);
    }

    /// <summary>An album whose display edition nobody had a sleeve for a week ago is not asked again yet.</summary>
    [Fact]
    public async Task AnAlbumWhoseSleeveNobodyHadThisWeekIsNotAsked()
    {
        var (groupId, _) = await SeedFiledReleaseAsync(OfficialMbid, path: null, askedAgo: TimeSpan.FromDays(6));

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            seed.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "A/One/01.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                ReleaseGroupId = groupId,
            });
            await seed.SaveChangesAsync(Token);
        }

        var archive = new StubArchive(OfficialMbid);
        var services = Build(Answering(Recording), ReleasesAnswering(), archive: archive);

        Assert.Equal(0, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Covers);

        await EnrichAsync(services);

        Assert.Empty(archive.Asked);
    }

    /// <summary>
    /// A front the archive lists and will not serve is skipped for the run and
    /// stored as nothing, an answer between two such resets the count that
    /// ends the stage, and the releases after them still get their sleeves.
    /// </summary>
    [Fact]
    public async Task ASleeveThatWillNotDownloadIsSteppedPast()
    {
        // The album's edition first, then five filed releases.
        var served = new[] { false, false, true, false, false, true }
            .Select((works, n) => (Mbid: Mb($"a0000000-0000-4000-8000-00000000005{n}"), Works: works))
            .ToList();

        for (var n = 1; n < served.Count; n++)
        {
            await SeedFiledReleaseAsync(
                served[n].Mbid,
                $"A/{n}/01.flac",
                askedAgo: null,
                id: new ReleaseId(Guid.Parse($"01900000-0000-7000-8000-00000000000{n}")));
        }

        var (groupId, _) = await SeedFiledReleaseAsync(served[0].Mbid, path: null, askedAgo: null);

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            seed.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "A/0/01.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                ReleaseGroupId = groupId,
            });
            await seed.SaveChangesAsync(Token);
        }

        var archive = new StubArchive([.. served.Select(s => s.Mbid)])
        {
            Unserved = [.. served.Where(s => !s.Works).Select(s => s.Mbid)],
        };
        var services = Build(Answering(Recording), ReleasesAnswering(), archive: archive);
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var stored = await db.ReleaseCovers.AsNoTracking()
            .Join(db.Releases, cover => cover.ReleaseId, release => release.Id, (_, release) => release.Mbid)
            .ToListAsync(Token);

        Assert.Equal([.. served.Skip(1).Select(s => s.Mbid), served[0].Mbid], archive.Asked);
        Assert.Equal(
            served.Where(s => s.Works).Select(s => s.Mbid.ToString()).Order(),
            stored.Select(mbid => mbid.ToString()).Order());
        Assert.Equal(4, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Covers);
    }

    /// <summary>
    /// An outage is not an answer: nothing is stored, the sleeves stay
    /// pending, and three unanswered in a row end the stage.
    /// </summary>
    [Fact]
    public async Task AnOutageStoresNoSleeveAndEndsTheStage()
    {
        for (var album = 1; album <= 4; album++)
        {
            await SeedFiledReleaseAsync(Mb($"a0000000-0000-4000-8000-00000000004{album}"), $"A/{album}/01.flac", askedAgo: null);
        }

        var archive = new StubArchive { Down = true };
        var services = Build(Answering(Recording), ReleasesAnswering(), archive: archive);
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Empty(await db.ReleaseCovers.AsNoTracking().ToListAsync(Token));
        Assert.Equal(3, archive.Listings);
        Assert.Equal(4, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Covers);
    }

    /// <summary>
    /// A release in an album of its own, with a file filed under it at
    /// <paramref name="path"/> where there is one, and a sleeve row stored
    /// <paramref name="askedAgo"/> where given: "none found", or
    /// <paramref name="bytes"/>.
    /// </summary>
    private async Task<(ReleaseGroupId Group, ReleaseId Release)> SeedFiledReleaseAsync(
        Mbid mbid,
        string? path,
        TimeSpan? askedAgo,
        byte[]? bytes = null,
        ReleaseId? id = null)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "One" };
        var release = new Release { Id = id ?? ReleaseId.New(), Title = "One", Mbid = mbid, ReleaseGroupId = group.Id };

        db.ReleaseGroups.Add(group);
        db.Releases.Add(release);

        if (path is not null)
        {
            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = path,
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                ReleaseGroupId = group.Id,
                ReleaseId = release.Id,
            });
        }

        if (askedAgo is { } ago)
        {
            db.ReleaseCovers.Add(new ReleaseCover
            {
                ReleaseId = release.Id,
                Bytes = bytes,
                MediaType = bytes is null ? null : "image/jpeg",
                SavedUtc = DateTimeOffset.UtcNow - ago,
            });
        }

        await db.SaveChangesAsync(Token);

        return (group.Id, release.Id);
    }

    /// <summary>Holds a front for the releases it was told about, and records which were asked.</summary>
    private sealed class StubArchive(params Mbid[] fronted) : ICoverArtArchive
    {
        public static readonly byte[] Front = [0xFF, 0xD8, 0xFF];

        public List<Mbid> Asked { get; } = [];

        /// <summary>Every listing, the ones that failed included.</summary>
        public int Listings { get; private set; }

        public bool Down { get; init; }

        /// <summary>Releases whose listed front fails to download.</summary>
        public IReadOnlyCollection<Mbid> Unserved { get; init; } = [];

        public Task<IReadOnlyList<CoverArtImage>> ListAsync(Mbid release, CancellationToken cancellationToken = default)
        {
            Listings++;

            if (Down) throw new ProviderUnavailableException("Cover Art Archive", "Down.");

            Asked.Add(release);

            return Task.FromResult<IReadOnlyList<CoverArtImage>>(
                fronted.Contains(release) ? [new(1, Front: true, ["Front"], null)] : []);
        }

        public Task<CoverArtBytes> DownloadAsync(Mbid release, long imageId, CancellationToken cancellationToken = default) =>
            Unserved.Contains(release)
                ? throw new ProviderUnavailableException("Cover Art Archive", "Not found.")
                : Task.FromResult(new CoverArtBytes(Front, "image/jpeg"));

        public Task<CoverArtBytes?> DownloadOriginalAsync(
            Mbid release,
            long imageId,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
