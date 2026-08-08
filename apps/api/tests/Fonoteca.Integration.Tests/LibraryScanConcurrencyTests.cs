using System.Data.Common;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The one-scan-at-a-time gate.
/// </summary>
/// <remarks>
/// Two scans over one library race on the same unique paths, and the loser
/// fails on a constraint violation partway through — which is why the service
/// refuses the second rather than queueing it.
///
/// Proving that needs a scan held open at a known point, and "start two and
/// hope" is a coin flip that passes on a broken gate about half the time. So
/// the first scan is blocked inside its very first database read by an EF
/// interceptor, which makes the ordering a fact rather than a race.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class LibraryScanConcurrencyTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly BlockingInterceptor _interceptor = new();

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        _root = Directory.CreateTempSubdirectory("fonoteca-scan-gate-").FullName;
        File.WriteAllText(Path.Combine(_root, "a.flac"), "not really audio");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FonotecaDbContext>(options => options
            .UseNpgsql(_connectionString)
            .AddInterceptors(_interceptor));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new FileSystemAudioFileStore(_root));
        services.AddSingleton<IAudioFileStore>(
            sp => sp.GetRequiredService<FileSystemAudioFileStore>());
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<LibraryScanService>();

        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        _interceptor.Release();

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SecondScanIsRefusedWhileTheFirstIsStillRunning()
    {
        var scans = _services!.GetRequiredService<LibraryScanService>();

        _interceptor.BlockNextRead();

        var first = Task.Run(
            () => scans.ScanAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        // The first scan is now parked inside its opening query, holding the gate.
        await _interceptor.WaitUntilBlocked(TestContext.Current.CancellationToken);

        Assert.True(scans.IsRunning);

        var second = await scans.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LibraryScanStatus.AlreadyRunning, second.Status);
        Assert.Null(second.Summary);

        _interceptor.Release();

        var completed = await first;

        Assert.Equal(LibraryScanStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Summary!.Added);

        // The gate reopens, or every later scan is refused forever.
        Assert.False(scans.IsRunning);

        var third = await scans.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LibraryScanStatus.Completed, third.Status);
    }

    /// <summary>Parks the first query it is told to, until released.</summary>
    private sealed class BlockingInterceptor : DbCommandInterceptor
    {
        private readonly Lock _sync = new();

        private TaskCompletionSource _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _armed;

        public void BlockNextRead()
        {
            lock (_sync)
            {
                _blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _armed = true;
            }
        }

        public async Task WaitUntilBlocked(CancellationToken cancellationToken)
        {
            Task blocked;
            lock (_sync)
            {
                blocked = _blocked.Task;
            }

            await blocked.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        public void Release()
        {
            lock (_sync)
            {
                _armed = false;
                _release.TrySetResult();
            }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Task release;

            lock (_sync)
            {
                if (!_armed)
                {
                    return result;
                }

                // One-shot: only the scan's opening read is parked, so the rest
                // of it runs normally once released.
                _armed = false;
                _blocked.TrySetResult();
                release = _release.Task;
            }

            await release.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            return result;
        }
    }
}
