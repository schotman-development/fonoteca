using Fonoteca.Api.Library;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The two stages that read a work's languages and parts and a filed release's
/// ASIN, script and track ids, which nothing but the tag write uses.
/// </summary>
public sealed partial class EnrichmentPassTests
{
    private static readonly Mbid PieceMbid = Mb("a0000000-0000-4000-8000-000000000001");
    private static readonly Mbid MovementMbid = Mb("a0000000-0000-4000-8000-000000000002");
    private static readonly Mbid PressingMbid = Mb("a0000000-0000-4000-8000-000000000003");
    private static readonly Mbid UnfiledMbid = Mb("a0000000-0000-4000-8000-000000000004");
    private static readonly Mbid FirstTrackMbid = Mb("a0000000-0000-4000-8000-000000000011");
    private static readonly Mbid SecondTrackMbid = Mb("a0000000-0000-4000-8000-000000000012");

    /// <summary>
    /// A movement's languages, its place among its parent's parts and the parts
    /// a whole piece lists are kept, and each work is stamped.
    /// </summary>
    [Fact]
    public async Task AWorkIsDescribedWithItsLanguagesPositionAndPartCount()
    {
        await SeedMovementAsync();

        var services = Build(Answering(Recording), WorksAnswering(
            new MusicBrainzWork(MovementMbid, "Allegro", null, [], PieceMbid, "Symphony", ["eng", "deu"], 3, 0),
            new MusicBrainzWork(PieceMbid, "Symphony", null, [], Languages: ["zxx"], PartCount: 4)));

        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var works = await db.Works.AsNoTracking().ToDictionaryAsync(w => w.Mbid!.Value, Token);

        Assert.Equal("eng, deu", works[MovementMbid].Languages);
        Assert.Equal(3, works[MovementMbid].Position);
        Assert.Null(works[MovementMbid].PartCount);
        Assert.NotNull(works[MovementMbid].DescribedUtc);

        Assert.Equal("zxx", works[PieceMbid].Languages);
        Assert.Equal(4, works[PieceMbid].PartCount);
        Assert.Null(works[PieceMbid].Position);
        Assert.NotNull(works[PieceMbid].DescribedUtc);

        Assert.Equal(0, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Works);
    }

    /// <summary>
    /// A movement number is a number in one parent's order: where the answer
    /// names another parent than the row's, the position is not kept.
    /// </summary>
    [Fact]
    public async Task AWorkWhoseAnswerNamesAnotherParentKeepsNoPosition()
    {
        await SeedMovementAsync();

        var other = Mb("a0000000-0000-4000-8000-0000000000ff");

        await EnrichAsync(Build(Answering(Recording), WorksAnswering(
            new MusicBrainzWork(MovementMbid, "Allegro", null, [], other, "Another piece", ["eng"], 3, 0),
            new MusicBrainzWork(PieceMbid, "Symphony", null, []))));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var movement = await db.Works.AsNoTracking().SingleAsync(w => w.Mbid == MovementMbid, Token);

        Assert.Null(movement.Position);
        Assert.Equal("eng", movement.Languages);
        Assert.NotNull(movement.DescribedUtc);
    }

    /// <summary>A work MusicBrainz no longer knows is stamped with nothing stored, or it would be asked forever.</summary>
    [Fact]
    public async Task AWorkMusicBrainzNoLongerKnowsIsStampedWithNothingStored()
    {
        await SeedMovementAsync();

        var services = Build(Answering(Recording), WorksAnswering());
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.All(await db.Works.AsNoTracking().ToListAsync(Token), work =>
        {
            Assert.NotNull(work.DescribedUtc);
            Assert.Null(work.Languages);
            Assert.Null(work.Position);
            Assert.Null(work.PartCount);
        });

        Assert.Equal(0, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Works);
    }

    /// <summary>An outage is not an answer: the work stays unstamped and pending.</summary>
    [Fact]
    public async Task AnOutageLeavesAWorkUndescribed()
    {
        await SeedMovementAsync();

        var catalogue = WorksAnswering();
        catalogue.DescriptionsUnavailable = true;

        var services = Build(Answering(Recording), catalogue);
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.All(await db.Works.AsNoTracking().ToListAsync(Token), work => Assert.Null(work.DescribedUtc));
        Assert.Equal(2, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Works);
    }

    /// <summary>
    /// A release a file is filed under gets its ASIN and script, and each of its
    /// tracks the id, medium title and medium format of the slot it holds.
    /// </summary>
    [Fact]
    public async Task AFiledReleaseGetsItsAsinScriptAndTrackIdsSlotBySlot()
    {
        await SeedReleaseAsync(PressingMbid, filed: true);

        var services = Build(Answering(Recording), ReleasesAnswering(Pressing()));
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var release = await db.Releases.AsNoTracking().Include(r => r.Tracks).SingleAsync(Token);
        var tracks = release.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.Position).ToList();

        Assert.Equal("B00005NZ2W", release.Asin);
        Assert.Equal("Latn", release.Script);
        Assert.NotNull(release.DescribedUtc);

        Assert.Equal([FirstTrackMbid, SecondTrackMbid], tracks.Select(t => t.Mbid));
        Assert.Equal(["Live at the Fillmore", "Studio"], tracks.Select(t => t.MediumTitle));
        Assert.Equal(["CD", "Digital Media"], tracks.Select(t => t.MediumFormat));

        Assert.Equal(0, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Releases);
    }

    /// <summary>A release no file is filed under is not asked about and not counted: its tags would never be written.</summary>
    [Fact]
    public async Task AnUnfiledReleaseIsNeitherAskedNorCounted()
    {
        await SeedReleaseAsync(UnfiledMbid, filed: false);

        var catalogue = ReleasesAnswering(Pressing());
        var services = Build(Answering(Recording), catalogue);

        Assert.Equal(0, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Releases);

        await EnrichAsync(services);

        Assert.Empty(catalogue.ReleasesAsked);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Null((await db.Releases.AsNoTracking().SingleAsync(Token)).DescribedUtc);
    }

    /// <summary>A release MusicBrainz no longer knows is stamped with nothing stored.</summary>
    [Fact]
    public async Task AReleaseMusicBrainzNoLongerKnowsIsStampedWithNothingStored()
    {
        await SeedReleaseAsync(PressingMbid, filed: true);

        var services = Build(Answering(Recording), ReleasesAnswering());
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var release = await db.Releases.AsNoTracking().Include(r => r.Tracks).SingleAsync(Token);

        Assert.NotNull(release.DescribedUtc);
        Assert.Null(release.Asin);
        Assert.All(release.Tracks, track => Assert.Null(track.Mbid));
        Assert.Equal(0, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Releases);
    }

    /// <summary>An outage is not an answer: the release stays unstamped and pending.</summary>
    [Fact]
    public async Task AnOutageLeavesAReleaseUndescribed()
    {
        await SeedReleaseAsync(PressingMbid, filed: true);

        var catalogue = ReleasesAnswering(Pressing());
        catalogue.DescriptionsUnavailable = true;

        var services = Build(Answering(Recording), catalogue);
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Null((await db.Releases.AsNoTracking().SingleAsync(Token)).DescribedUtc);
        Assert.Equal(1, (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Releases);
    }

    private static StubCatalogue WorksAnswering(params MusicBrainzWork[] answers) =>
        new(null, null, works: answers.ToDictionary(w => w.Id));

    private static StubCatalogue ReleasesAnswering(params MusicBrainzRelease[] answers) =>
        new(null, null, releases: answers.ToDictionary(r => r.Id));

    private static MusicBrainzRelease Pressing() =>
        new(
            PressingMbid, "Two Discs", null, null, null, null, [], null, null, null, [], [],
            [
                new MusicBrainzTrack(1, 1, "1", "One", null, null, [], FirstTrackMbid),
                new MusicBrainzTrack(2, 1, "1", "Two", null, null, [], SecondTrackMbid),
            ],
            [
                new MusicBrainzMediumSummary(1, "CD", 1, "Live at the Fillmore"),
                new MusicBrainzMediumSummary(2, "Digital Media", 1, "Studio"),
            ],
            Asin: "B00005NZ2W",
            Script: "Latn");

    /// <summary>A movement under its piece, both with an MBID and both already asked what they are part of.</summary>
    private async Task SeedMovementAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var piece = new Work
        {
            Id = Domain.Catalogue.WorkId.New(),
            Title = "Symphony",
            Mbid = PieceMbid,
            PartOfLookupUtc = DateTimeOffset.UtcNow,
        };

        db.Works.Add(piece);
        db.Works.Add(new Work
        {
            Id = Domain.Catalogue.WorkId.New(),
            Title = "Allegro",
            Mbid = MovementMbid,
            ParentId = piece.Id,
            PartOfLookupUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(Token);
    }

    /// <summary>A two-disc release with a track on each disc, and a file filed under it where asked.</summary>
    private async Task SeedReleaseAsync(Mbid mbid, bool filed)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = new Release { Id = ReleaseId.New(), Title = "Two Discs", Mbid = mbid };
        db.Releases.Add(release);

        for (var disc = 1; disc <= 2; disc++)
        {
            var recording = new Recording { Id = RecordingId.New(), Title = $"Disc {disc}" };
            db.Recordings.Add(recording);
            db.Tracks.Add(new Track
            {
                Id = TrackId.New(),
                ReleaseId = release.Id,
                RecordingId = recording.Id,
                DiscNumber = disc,
                Position = 1,
            });
        }

        if (filed)
        {
            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "Artist/Two Discs/01.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                ReleaseId = release.Id,
            });
        }

        await db.SaveChangesAsync(Token);
    }
}
