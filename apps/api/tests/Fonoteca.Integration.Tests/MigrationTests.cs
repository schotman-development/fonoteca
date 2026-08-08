using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Proves the schema actually applies to a real PostgreSQL, and that the parts
/// of it which only exist for scale are genuinely there.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MigrationsApplyToAnEmptyDatabase()
    {
        await using var db = postgres.CreateContext();

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var applied = await db.Database.GetAppliedMigrationsAsync(
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(applied);

        var pending = await db.Database.GetPendingMigrationsAsync(
            TestContext.Current.CancellationToken);
        Assert.Empty(pending);
    }

    /// <summary>
    /// pg_trgm is not decoration: without it the fuzzy title indexes cannot be
    /// created at all, and title matching across 100k rows falls back to a
    /// sequential scan.
    /// </summary>
    [Fact]
    public async Task TrigramExtensionIsInstalled()
    {
        await using var db = postgres.CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // EF's SqlQuery<T> projects from a subquery and expects the scalar to be
        // named "Value"; without the alias this fails with 42703.
        var count = await db.Database
            .SqlQuery<int>(
                $"""SELECT COUNT(*)::int AS "Value" FROM pg_extension WHERE extname = 'pg_trgm'""")
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task MediaFilePathIsUniqueInTheDatabase()
    {
        await using var db = postgres.CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var path = $"/music/{Guid.CreateVersion7()}.flac";

        db.MediaFiles.Add(NewFile(path));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The uniqueness of a file path must be the database's job. If it were
        // only the scanner's, a concurrent second scan would create duplicates.
        db.MediaFiles.Add(NewFile(path));
        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The strongly-typed ids must survive the round trip as real uuid columns.
    /// </summary>
    [Fact]
    public async Task StronglyTypedIdsRoundTrip()
    {
        await using var db = postgres.CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var recording = new Recording
        {
            Id = RecordingId.New(),
            Title = "So What",
            Duration = TimeSpan.FromMinutes(9),
        };
        db.Recordings.Add(recording);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var loaded = await db.Recordings.SingleAsync(
            r => r.Id == recording.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(recording.Id, loaded.Id);
        Assert.Equal("So What", loaded.Title);
    }

    /// <summary>
    /// A recording holding several files at different qualities is the ordinary
    /// case, not an error — it is what dedupe and upgrade monitoring both act on.
    /// </summary>
    [Fact]
    public async Task OneRecordingCanHoldManyFilesAtDifferentQualities()
    {
        await using var db = postgres.CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var recording = new Recording { Id = RecordingId.New(), Title = "Blue in Green" };
        db.Recordings.Add(recording);

        var stem = Guid.CreateVersion7();
        db.MediaFiles.Add(NewFile($"/music/{stem}-hires.flac", recording.Id, Hires()));
        db.MediaFiles.Add(NewFile($"/music/{stem}-cd.flac", recording.Id, Cd()));
        db.MediaFiles.Add(NewFile($"/music/{stem}.mp3", recording.Id, Mp3()));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var loaded = await db.Recordings
            .Include(r => r.Files)
            .SingleAsync(r => r.Id == recording.Id, TestContext.Current.CancellationToken);

        Assert.Equal(3, loaded.Files.Count);

        var best = loaded.Files
            .OrderByDescending(f => f.Quality, Comparer<AudioQuality?>.Create(AudioQuality.Compare))
            .First();

        Assert.Equal(QualityTier.LosslessHiRes, best.Quality?.Tier);
    }

    [Fact]
    public async Task DomainEventsPersistAsQueryableJsonb()
    {
        await using var db = postgres.CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var subjectId = Guid.CreateVersion7().ToString();
        db.DomainEvents.Add(Domain.Events.DomainEvent.Create(
            type: "tagging.write.committed",
            subjectType: "file",
            subjectId: subjectId,
            actorId: "owner",
            occurredAtUtc: DateTimeOffset.UtcNow,
            payloadJson: """{"before":{"title":"So Waht"},"after":{"title":"So What"}}""",
            correlationId: "batch-1"));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.DomainEvents.SingleAsync(
            e => e.SubjectId == subjectId,
            TestContext.Current.CancellationToken);

        Assert.Equal("tagging.write.committed", stored.Type);
        Assert.Contains("So What", stored.PayloadJson, StringComparison.Ordinal);
    }

    private static MediaFile NewFile(string path, RecordingId? recordingId = null, AudioQuality? quality = null) =>
        new()
        {
            Id = MediaFileId.New(),
            Path = path,
            SizeBytes = 1024,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            RecordingId = recordingId,
            Quality = quality,
        };

    private static AudioQuality Hires() => new()
    {
        Codec = "flac", SampleRateHz = 96_000, Channels = 2,
        BitDepth = 24, BitrateBps = 2_300_000, IsLossless = true,
    };

    private static AudioQuality Cd() => new()
    {
        Codec = "flac", SampleRateHz = 44_100, Channels = 2,
        BitDepth = 16, BitrateBps = 900_000, IsLossless = true,
    };

    private static AudioQuality Mp3() => new()
    {
        Codec = "mp3", SampleRateHz = 44_100, Channels = 2,
        BitDepth = null, BitrateBps = 320_000, IsLossless = false,
    };
}
