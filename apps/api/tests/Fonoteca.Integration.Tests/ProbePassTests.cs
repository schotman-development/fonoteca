using System.Globalization;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The pass that measures the library, against real audio on a real disk.
/// </summary>
/// <remarks>
/// Four files and four different answers, which is the whole point: a healthy
/// FLAC, a healthy MP3, a truncated FLAC whose header still declares its
/// original length, and a text file named <c>.flac</c>. The pass has to tell
/// them apart, and it has to leave every one of them off its own worklist
/// afterwards — the treadmill <c>AcoustIdCheckedUtc</c> already paid for, where
/// keying on the answer re-decodes the unmeasurable files forever.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class ProbePassTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-probe-pass-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        using var warm = _factory.CreateClient();

        if (Corpus.IsAvailable)
        {
            Corpus.CopyInto(
                _root, Corpus.Flac, Corpus.Mp3, Corpus.TruncatedFlac, Corpus.NotAudioFlac);
        }

        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ThePassMeasuresWhatItCanAndSaysWhyForTheRest()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var status = await RunAsync();

        Assert.NotNull(status.LastCompleted);
        Assert.Equal(4, status.LastCompleted.Examined);

        // The two healthy files carry a measurement; the truncated one is a
        // complaint and the text file describes no audio at all.
        Assert.Equal(2, status.LastCompleted.Measured);
        Assert.Equal(1, status.LastCompleted.Complained);
        Assert.Equal(1, status.LastCompleted.Unreadable);
        Assert.Equal(0, status.LastCompleted.Failed);
        Assert.Equal(0, status.LastCompleted.Skipped);
        Assert.Null(status.LastError);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var rows = await db.MediaFiles.AsNoTracking()
            .ToDictionaryAsync(file => Path.GetFileName(file.Path), Token);

        var flac = rows[Path.GetFileName(Corpus.Flac)];
        Assert.Equal(IntegrityState.Intact, flac.Integrity);
        Assert.NotNull(flac.Quality);
        Assert.True(flac.Quality.IsLossless);
        Assert.Equal(44_100, flac.Quality.SampleRateHz);
        Assert.Equal(16, flac.Quality.BitDepth);

        var mp3 = rows[Path.GetFileName(Corpus.Mp3)];
        Assert.Equal(IntegrityState.Intact, mp3.Integrity);
        Assert.False(mp3.Quality!.IsLossless);
    }

    [Fact]
    public async Task ADecoderComplaintIsRecordedButNotRemembered()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await RunAsync();

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var truncated = await db.MediaFiles.AsNoTracking()
            .SingleAsync(f => f.Path == Path.GetFileName(Corpus.TruncatedFlac), Token);

        // A FLAC cut to a fraction of its bytes goes on declaring its original
        // duration and exits zero. AudioQuality decides which duplicate to keep,
        // so a number the decoder objected to must not reach it — but the file
        // is still marked, and still off the worklist.
        Assert.Equal(IntegrityState.Corrupt, truncated.Integrity);
        Assert.Null(truncated.Quality);
        Assert.NotNull(truncated.LastVerifiedUtc);
    }

    [Fact]
    public async Task AFileNothingCanMeasureStillLeavesTheWorklist()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var status = await RunAsync();

        // The whole reason the worklist is LastVerifiedUtc rather than Quality:
        // keyed on the answer, the two files that cannot produce one come back
        // on every pass forever and the list never empties.
        Assert.Equal(0, status.Coverage.Pending);
        Assert.Equal(0, status.Coverage.Unchecked);
        Assert.Equal(2, status.Coverage.Measured);
        Assert.Equal(1, status.Coverage.Corrupt);
        Assert.Equal(1, status.Coverage.Unreadable);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var notAudio = await db.MediaFiles.AsNoTracking()
            .SingleAsync(f => f.Path == Path.GetFileName(Corpus.NotAudioFlac), Token);

        Assert.Equal(IntegrityState.Unreadable, notAudio.Integrity);
        Assert.NotNull(notAudio.LastVerifiedUtc);

        // And they are reachable. The matching worklist lists neither of them —
        // both are Unfingerprintable or already identified — so the count alone
        // would name files nothing can get to.
        Assert.Equal(2, status.Coverage.Damaged.Count);
        Assert.Contains(Path.GetFileName(Corpus.NotAudioFlac), status.Coverage.Damaged);
        Assert.Contains(Path.GetFileName(Corpus.TruncatedFlac), status.Coverage.Damaged);
    }

    [Fact]
    public async Task AFileThatIsNotOnDiskIsLeftPendingRatherThanMarkedBroken()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            // The unmounted-volume shape, one row wide. ffprobe reports a missing
            // file exactly as it reports a corrupt one, so without the stat guard
            // this row is stamped Unreadable and never looked at again — and with
            // the volume unmounted, so is every other row in the library.
            seed.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "Gone/Missing/01.flac",
                SizeBytes = 1,
                LastModifiedUtc = DateTimeOffset.Parse(
                    "2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            });

            await seed.SaveChangesAsync(Token);
        }

        var status = await RunAsync();

        Assert.Equal(1, status.LastCompleted!.Skipped);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var missing = await db.MediaFiles.AsNoTracking()
            .SingleAsync(f => f.Path == "Gone/Missing/01.flac", Token);

        Assert.Null(missing.LastVerifiedUtc);
        Assert.Equal(IntegrityState.Unchecked, missing.Integrity);

        // Still on the worklist, which is what makes a mount that comes back a
        // second run rather than a hand-written UPDATE.
        Assert.Equal(1, status.Coverage.Pending);
    }

    [Fact]
    public async Task ASecondPassHasNothingLeftToDo()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await RunAsync();
        var again = await RunAsync();

        Assert.Equal(0, again.LastCompleted!.Examined);
    }

    [Fact]
    public async Task AMeasuredCdRipReachesTheUpgradeList()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        using var client = _factory!.CreateClient();

        // Before the pass, the FLAC is not a question — nothing has looked at it.
        var before = await client.GetFromJsonAsync<UpgradeListResponse>(
            new Uri("/api/qobuz/upgrades", UriKind.Relative), Token);

        Assert.NotNull(before);
        Assert.DoesNotContain(before.Items, item => item.Reason == "BelowHiRes");

        await RunAsync();

        var after = await client.GetFromJsonAsync<UpgradeListResponse>(
            new Uri("/api/qobuz/upgrades", UriKind.Relative), Token);

        Assert.NotNull(after);

        // 16/44.1, measured by a decoder — the half of the list that does not
        // exist until this pass has run.
        var cd = Assert.Single(after.Items, item => item.Reason == "BelowHiRes");
        Assert.Equal(["FLAC 16/44.1"], cd.Formats);
    }

    [Fact]
    public async Task AWholePageOfMissingFilesDoesNotStopThePass()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            // A page is 200, and Guid v7 ids are creation-ordered — so a
            // directory that went missing in one import is a contiguous block at
            // the head of the worklist. Filtering skipped rows out of the page
            // after the fact made that page look like it contained nothing new,
            // and the pass ended there reporting success with the rest of the
            // library untouched. These are minted first, so they sort first.
            for (var n = 0; n < 250; n++)
            {
                seed.MediaFiles.Add(new MediaFile
                {
                    Id = MediaFileId.New(),
                    Path = $"Gone/Missing/{n:000}.flac",
                    SizeBytes = 1,
                    LastModifiedUtc = DateTimeOffset.Parse(
                        "2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                });
            }

            await seed.SaveChangesAsync(Token);
        }

        var status = await RunAsync();

        Assert.Equal(250, status.LastCompleted!.Skipped);

        // The four real files are behind all 250 of them and still get measured.
        Assert.Equal(2, status.LastCompleted.Measured);
        Assert.Equal(1, status.LastCompleted.Complained);
        Assert.Equal(1, status.LastCompleted.Unreadable);

        // And the missing ones are still pending, so a mount that comes back is
        // one more run rather than an UPDATE written by hand.
        Assert.Equal(250, status.Coverage.Pending);
    }

    /// <summary>Starts the pass and waits for it, rather than polling on a clock.</summary>
    private async Task<ProbeStatusResponse> RunAsync()
    {
        using var client = _factory!.CreateClient();

        var started = await client.PostAsync(new Uri("/api/library/probe", UriKind.Relative), null, Token);
        started.EnsureSuccessStatusCode();

        ProbeStatusResponse? status;

        // Bounded: four short files at any concurrency is well under a second,
        // so a loop that never ends is a failure rather than a slow machine.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            status = await client.GetFromJsonAsync<ProbeStatusResponse>(
                new Uri("/api/library/probe", UriKind.Relative), Token);

            if (status is { Running: false, LastCompleted: not null }) return status;

            await Task.Delay(50, Token);
        }

        throw new InvalidOperationException("The probe pass did not finish.");
    }

    private async Task SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        foreach (var source in new[]
                 {
                     Corpus.Flac, Corpus.Mp3, Corpus.TruncatedFlac, Corpus.NotAudioFlac,
                 })
        {
            var name = Path.GetFileName(source);

            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = name,
                SizeBytes = 1,
                LastModifiedUtc = DateTimeOffset.Parse(
                    "2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            });
        }

        await db.SaveChangesAsync(Token);
    }
}
