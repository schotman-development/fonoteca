using System.Globalization;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The analysis pass, against real audio on a real disk: what it measures,
/// what it stamps, and what it leaves for later.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AnalysisPassTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-analysis-pass-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        using var warm = _factory.CreateClient();

        if (Corpus.IsAvailable) Corpus.CopyInto(_root, Corpus.Flac, Corpus.Mp3, Corpus.NotAudioFlac);

        await SeedAsync(Corpus.Flac, Corpus.Mp3, Corpus.NotAudioFlac, "not-on-disk.flac");
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Audio is measured; bytes that are not audio are stamped with no
    /// measurement; a file not on disk is left on the worklist, never stamped.
    /// </summary>
    [Fact]
    public async Task ThePassMeasuresWhatItCanAndLeavesTheMissingPending()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var status = await RunAsync();

        Assert.Equal(2, status.LastCompleted!.Measured);
        Assert.Equal(1, status.LastCompleted.Unmeasurable);
        Assert.Equal(1, status.LastCompleted.Skipped);
        Assert.Equal(1, status.Coverage.Pending);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var rows = await db.MediaFiles.AsNoTracking().ToDictionaryAsync(f => f.Path, Token);

        Assert.NotNull(rows[Path.GetFileName(Corpus.Flac)].Measurement?.IntegratedLufs);
        Assert.NotNull(rows[Path.GetFileName(Corpus.Mp3)].Measurement?.TruePeakDbtp);

        var notAudio = rows[Path.GetFileName(Corpus.NotAudioFlac)];
        Assert.NotNull(notAudio.MeasuredUtc);
        Assert.Null(notAudio.Measurement);

        Assert.Null(rows["not-on-disk.flac"].MeasuredUtc);
    }

    [Fact]
    public async Task ASecondPassAsksOnlyAboutWhatIsStillMissing()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await RunAsync();
        var again = await RunAsync();

        Assert.Equal(1, again.LastCompleted!.Examined);
        Assert.Equal(1, again.LastCompleted.Skipped);
    }

    /// <summary>Bytes a scan sees change are no longer the audio that was measured.</summary>
    [Fact]
    public async Task AScanThatSeesTheBytesChangeForgetsTheMeasurement()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await RunAsync();

        // The seeded sizes are not the files' own, so the scan reads all three as changed.
        using var client = _factory!.CreateClient();
        (await client.PostAsync(new Uri("/api/library/scan", UriKind.Relative), null, Token)).EnsureSuccessStatusCode();

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var flac = await db.MediaFiles.AsNoTracking().SingleAsync(f => f.Path == Path.GetFileName(Corpus.Flac), Token);

        Assert.Null(flac.MeasuredUtc);
        Assert.Null(flac.Measurement);
    }

    /// <summary>An ffmpeg that cannot be run is the tool's fault: the pass ends and stamps nothing.</summary>
    [Fact]
    public async Task AnFfmpegThatCannotRunEndsThePassAndStampsNothing()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await using var broken = _factory!.WithWebHostBuilder(builder =>
            builder.UseSetting("Fonoteca:FfmpegPath", "/nonexistent/ffmpeg"));

        var status = await RunAsync(broken);

        Assert.NotNull(status.LastError);
        Assert.Null(status.LastCompleted);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.False(await db.MediaFiles.AnyAsync(f => f.MeasuredUtc != null, Token));
    }

    /// <summary>A file a scan rewrote while it was being decoded is not the audio measured, and stays pending.</summary>
    [Fact]
    public async Task AFileThatChangesWhileItIsMeasuredIsLeftPending()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await using var changing = _factory!.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IAudioMeasurer>(new ChangingMeasurer(_connectionString))));

        var status = await RunAsync(changing);

        Assert.Equal(0, status.LastCompleted!.Measured);
        Assert.Equal(4, status.LastCompleted.Skipped);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.False(await db.MediaFiles.AnyAsync(f => f.MeasuredUtc != null, Token));
    }

    private async Task<AnalysisStatusResponse> RunAsync(WebApplicationFactory<Program>? factory = null)
    {
        using var client = (factory ?? _factory!).CreateClient();

        var started = await client.PostAsync(new Uri("/api/library/analyse", UriKind.Relative), null, Token);
        started.EnsureSuccessStatusCode();

        for (var attempt = 0; attempt < 1200; attempt++)
        {
            var status = await client.GetFromJsonAsync<AnalysisStatusResponse>(
                new Uri("/api/library/analyse", UriKind.Relative), Token);

            if (status is { Running: false } && (status.LastCompleted is not null || status.LastError is not null)) return status;

            await Task.Delay(50, Token);
        }

        throw new InvalidOperationException("The analysis pass did not finish.");
    }

    /// <summary>Measures every file, and meanwhile a scan records new bytes for it.</summary>
    private sealed class ChangingMeasurer(string connectionString) : IAudioMeasurer
    {
        public async Task<AudioMeasurement?> MeasureAsync(LibraryPath path, CancellationToken cancellationToken = default)
        {
            await using var db = PostgresFixture.CreateContext(connectionString);

            var row = await db.MediaFiles.SingleAsync(f => f.Path == path.Value, cancellationToken);
            row.SizeBytes++;
            await db.SaveChangesAsync(cancellationToken);

            return new AudioMeasurement { IntegratedLufs = -14 };
        }
    }

    private async Task SeedAsync(params string[] sources)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        foreach (var source in sources)
        {
            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = Path.GetFileName(source),
                SizeBytes = 1,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            });
        }

        await db.SaveChangesAsync(Token);
    }
}
