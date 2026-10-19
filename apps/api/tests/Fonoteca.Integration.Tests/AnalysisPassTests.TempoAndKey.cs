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
/// The analysis pass's second question, tempo and key, with both tools faked so
/// what is pinned is the pass's bookkeeping: which half is pending, which
/// stamp goes on, and what a tool's fault does.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AnalysisPassTempoAndKeyTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Measured = DateTimeOffset.Parse("2026-02-02T00:00:00Z", CultureInfo.InvariantCulture);

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-analysis-tempo-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.SignedIn();
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:IdentifyAfterScan", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        using var warm = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>With the option set a file gets its reading and both stamps, each from its own tool.</summary>
    [Fact]
    public async Task AFileGetsItsTempoAndKeyAndBothStamps()
    {
        var file = await SeedFileAsync("a.flac");
        var (ffmpeg, essentia) = (new FakeMeasurer(), new FakeEssentia());

        var status = await RunAsync(Configure(ffmpeg, essentia, "/usr/bin/python3"));

        Assert.Equal(1, status.LastCompleted!.Measured);
        Assert.Equal(["a.flac"], ffmpeg.Asked);
        Assert.Equal(["a.flac"], essentia.Asked);

        var row = await ReadAsync(file);
        Assert.Equal(-14, row.Measurement!.IntegratedLufs);
        Assert.NotNull(row.MeasuredUtc);
        Assert.Equal(120, row.TempoAndKey!.Bpm);
        Assert.Equal("A", row.TempoAndKey.Key);
        Assert.NotNull(row.TempoAndKeyMeasuredUtc);
    }

    /// <summary>
    /// A file ffmpeg measured before Essentia was configured is asked Essentia's
    /// half alone: decoding it twice for a number it already has is waste.
    /// </summary>
    [Fact]
    public async Task AFileAlreadyMeasuredByFfmpegIsAskedOnlyForTempoAndKey()
    {
        var file = await SeedFileAsync("a.flac", measured: true);
        var (ffmpeg, essentia) = (new FakeMeasurer(), new FakeEssentia());

        await RunAsync(Configure(ffmpeg, essentia, "/usr/bin/python3"));

        Assert.Empty(ffmpeg.Asked);
        Assert.Equal(["a.flac"], essentia.Asked);

        var row = await ReadAsync(file);
        Assert.Equal(-9, row.Measurement!.IntegratedLufs);
        Assert.Equal(Measured, row.MeasuredUtc);
        Assert.Equal(120, row.TempoAndKey!.Bpm);
        Assert.NotNull(row.TempoAndKeyMeasuredUtc);
    }

    /// <summary>
    /// A file ffmpeg has measured still counts as pending while its tempo and
    /// key are unasked, or the button would say there is nothing to do.
    /// </summary>
    [Fact]
    public async Task AFileWaitingOnlyForItsTempoAndKeyIsPending()
    {
        await SeedFileAsync("a.flac", measured: true);

        await using var factory = Configure(new FakeMeasurer(), new FakeEssentia(), "/usr/bin/python3");
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<AnalysisStatusResponse>(
            new Uri("/api/library/analyse", UriKind.Relative), Token);

        Assert.Equal(1, status!.Coverage.Pending);
    }

    /// <summary>With the option empty Essentia is never asked, and ffmpeg's stamp alone settles a file.</summary>
    [Fact]
    public async Task WithNoPythonConfiguredEssentiaIsNeverAskedAndTheFileIsNotPending()
    {
        var file = await SeedFileAsync("a.flac", measured: true);
        var (ffmpeg, essentia) = (new FakeMeasurer(), new FakeEssentia());

        var status = await RunAsync(Configure(ffmpeg, essentia, ""));

        Assert.Empty(essentia.Asked);
        Assert.Empty(ffmpeg.Asked);
        Assert.Equal(0, status.LastCompleted!.Examined);
        Assert.Equal(0, status.Coverage.Pending);

        var row = await ReadAsync(file);
        Assert.Null(row.TempoAndKey);
        Assert.Null(row.TempoAndKeyMeasuredUtc);
    }

    /// <summary>A tool that cannot run is the tool's fault: the pass ends and the file carries no stamp.</summary>
    [Fact]
    public async Task ATempoToolThatCannotRunEndsThePassAndStampsNothing()
    {
        var file = await SeedFileAsync("a.flac");
        var broken = new FakeEssentia { Throws = true };

        var status = await RunAsync(Configure(new FakeMeasurer(), broken, "/usr/bin/python3"));

        Assert.NotNull(status.LastError);
        Assert.Null(status.LastCompleted);

        var row = await ReadAsync(file);
        Assert.Null(row.TempoAndKeyMeasuredUtc);
        Assert.Null(row.TempoAndKey);
    }

    /// <summary>A scan that sees the audio change forgets the tempo and key with the rest of what was heard.</summary>
    [Fact]
    public async Task AScanThatSeesTheBytesChangeForgetsTheTempoAndKey()
    {
        var file = await SeedFileAsync("a.mp3", measured: true, tempo: true, sizeBytes: 1);

        using var client = _factory!.CreateClient();
        (await client.PostAsync(new Uri("/api/library/scan", UriKind.Relative), null, Token)).EnsureSuccessStatusCode();

        var row = await ReadAsync(file);
        Assert.Null(row.MeasuredUtc);
        Assert.Null(row.Measurement);
        Assert.Null(row.TempoAndKey);
        Assert.Null(row.TempoAndKeyMeasuredUtc);
    }

    private WebApplicationFactory<Program> Configure(FakeMeasurer ffmpeg, FakeEssentia essentia, string python) =>
        _factory!.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Fonoteca:EssentiaPythonPath", python);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAudioMeasurer>(ffmpeg);
                services.AddSingleton<ITempoAndKeyMeasurer>(essentia);
            });
        });

    private async Task<MediaFile> ReadAsync(MediaFileId id)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        return await db.MediaFiles.AsNoTracking().SingleAsync(f => f.Id == id, Token);
    }

    private async Task<MediaFileId> SeedFileAsync(string path, bool measured = false, bool tempo = false, long sizeBytes = 1)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        // A scan reads the seeded size of 1 as a change; the analyse pass needs only that the file is there.
        await File.WriteAllBytesAsync(Path.Combine(_root, path), [1, 2, 3, 4], Token);

        var id = MediaFileId.New();
        db.MediaFiles.Add(new MediaFile
        {
            Id = id,
            Path = path,
            SizeBytes = sizeBytes,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            MeasuredUtc = measured ? Measured : null,
            Measurement = measured ? new AudioMeasurement { IntegratedLufs = -9 } : null,
            TempoAndKeyMeasuredUtc = tempo ? Measured : null,
            TempoAndKey = tempo ? new TempoAndKey { Bpm = 90, BpmConfidence = 2, Key = "C", Scale = "major", KeyStrength = 0.5 } : null,
        });

        await db.SaveChangesAsync(Token);
        return id;
    }

    private async Task<AnalysisStatusResponse> RunAsync(WebApplicationFactory<Program> factory)
    {
        await using var _ = factory;
        using var client = factory.CreateClient();

        (await client.PostAsync(new Uri("/api/library/analyse", UriKind.Relative), null, Token)).EnsureSuccessStatusCode();

        for (var attempt = 0; attempt < 1200; attempt++)
        {
            var status = await client.GetFromJsonAsync<AnalysisStatusResponse>(
                new Uri("/api/library/analyse", UriKind.Relative), Token);

            if (status is { Running: false } && (status.LastCompleted is not null || status.LastError is not null)) return status;

            await Task.Delay(50, Token);
        }

        throw new InvalidOperationException("The analysis pass did not finish.");
    }

    private sealed class FakeMeasurer : IAudioMeasurer
    {
        public List<string> Asked { get; } = [];

        public Task<AudioMeasurement?> MeasureAsync(LibraryPath path, CancellationToken cancellationToken = default)
        {
            lock (Asked) Asked.Add(path.Value);
            return Task.FromResult<AudioMeasurement?>(new AudioMeasurement { IntegratedLufs = -14 });
        }
    }

    private sealed class FakeEssentia : ITempoAndKeyMeasurer
    {
        public List<string> Asked { get; } = [];

        public bool Throws { get; init; }

        public Task<TempoAndKey?> MeasureAsync(LibraryPath path, CancellationToken cancellationToken = default)
        {
            lock (Asked) Asked.Add(path.Value);

            if (Throws) throw new AudioProbeFailedException(path, "python is not there", isFileFault: false);

            return Task.FromResult<TempoAndKey?>(new TempoAndKey { Bpm = 120, BpmConfidence = 3, Key = "A", Scale = "minor", KeyStrength = 0.8 });
        }
    }
}
