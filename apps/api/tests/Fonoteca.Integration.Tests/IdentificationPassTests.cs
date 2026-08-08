using Fonoteca.Api.Configuration;
using Fonoteca.Api.Library;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The identification pass, end to end against real files and real PostgreSQL.
/// </summary>
/// <remarks>
/// AcoustID is stubbed and everything else is real: real audio through real
/// fpcalc, real tag writes through ATL and TagLib#, real rows in a real
/// database. Only the third-party service is faked, because the point of these
/// tests is what this application does with an answer, not what AcoustID's
/// answer is.
///
/// The most valuable test here is
/// <see cref="TaggingAFileDoesNotMakeTheNextScanThinkItChanged"/>. Everything
/// else would fail loudly; that one fails as an infinite loop of expensive work
/// that looks, from the outside, like the feature simply never finishing.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class IdentificationPassTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid Cluster = new("11111111-2222-4333-8444-555555555555");

    private readonly List<ServiceProvider> _providers = [];

    private string _connectionString = string.Empty;
    private string _root = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(Token);

        _root = Directory.CreateTempSubdirectory("fonoteca-identify-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AConfidentMatchIsStoredAndTheFileIsTagged()
    {
        SkipWithoutTools();
        Copy(Corpus.Flac, "track.flac");

        var services = Build(allowMutation: true, Answering(0.97));
        await ScanAsync(services);
        await IdentifyAsync(services);

        var row = await RowAsync("track.flac");

        Assert.Equal(new AcoustId(Cluster), row.AcoustId);
        Assert.Equal(AcoustIdOutcome.Identified, row.AcoustIdOutcome);
        Assert.NotNull(row.AcoustIdCheckedUtc);
        Assert.NotNull(row.AcoustIdTaggedUtc);
        Assert.NotNull(row.Fingerprint);
        Assert.NotNull(row.FingerprintDuration);

        var reader = new TagReader(new FileSystemAudioFileStore(_root));
        Assert.Equal(
            Cluster.ToString("D"),
            await reader.ReadAcoustIdAsync(new LibraryPath("track.flac"), Token),
            ignoreCase: true);
    }

    /// <summary>
    /// The loop this feature would otherwise create, and the reason for the test.
    /// </summary>
    /// <remarks>
    /// Writing a tag changes the file's size and modification time. The scan
    /// reacts to changed bytes by discarding everything derived from them —
    /// including the AcoustID just written. Unhandled, the two passes undo each
    /// other forever: identify, tag, rescan, discard, identify… at forty minutes
    /// and 237 GB of rewriting per cycle, converging on nothing.
    ///
    /// The fix is that a committed write records the file's <i>new</i> facts in
    /// the same transaction as the AcoustID. This asserts the outcome rather than
    /// the mechanism: after tagging, a rescan reports nothing updated and the
    /// identification survives.
    /// </remarks>
    [Fact]
    public async Task TaggingAFileDoesNotMakeTheNextScanThinkItChanged()
    {
        SkipWithoutTools();
        Copy(Corpus.Flac, "track.flac");

        var services = Build(allowMutation: true, Answering(0.97));
        await ScanAsync(services);
        await IdentifyAsync(services);

        var tagged = await RowAsync("track.flac");
        Assert.NotNull(tagged.AcoustId);

        var rescan = await ScanAsync(services);

        Assert.Equal(0, rescan.Updated);
        Assert.Equal(1, rescan.Unchanged);

        // And the identification is still there, which is the half that would
        // otherwise be silently thrown away.
        var after = await RowAsync("track.flac");
        Assert.Equal(new AcoustId(Cluster), after.AcoustId);
        Assert.NotNull(after.Fingerprint);
    }

    /// <summary>
    /// The 227 files Picard already tagged cost one metadata read, not a lookup.
    /// </summary>
    [Fact]
    public async Task AFileThatAlreadyCarriesAnAcoustIdIsAdoptedWithoutAskingAnyone()
    {
        SkipWithoutTools();
        Copy(Corpus.AlreadyTaggedFlac, "tagged.flac");

        var lookup = Answering(0.97);
        var services = Build(allowMutation: true, lookup);

        await ScanAsync(services);
        await IdentifyAsync(services);

        var row = await RowAsync("tagged.flac");

        Assert.Equal(new AcoustId(new Guid(Corpus.PreExistingAcoustId)), row.AcoustId);
        Assert.Equal(AcoustIdOutcome.Identified, row.AcoustIdOutcome);
        Assert.NotNull(row.AcoustIdTaggedUtc);

        // Neither AcoustID nor fpcalc was troubled: the file already said what
        // it was, and rederiving that is a turn at a rate limit spent to learn
        // something already known.
        Assert.Equal(0, lookup.Calls);
        Assert.Null(row.Fingerprint);
    }

    /// <summary>
    /// Audio AcoustID has never heard must not be asked about on every pass.
    /// </summary>
    /// <remarks>
    /// A library contains bootlegs, DJ mixes and field recordings that will never
    /// be in the database. Selecting the worklist on "has no AcoustID" rather
    /// than "has not been asked about" means paying a third of a second for each
    /// of them, every run, forever.
    /// </remarks>
    [Fact]
    public async Task AFileAcoustIdDoesNotKnowIsNotAskedAboutAgain()
    {
        SkipWithoutTools();
        Copy(Corpus.Flac, "obscure.flac");

        var lookup = Answering();
        var services = Build(allowMutation: true, lookup);

        await ScanAsync(services);
        await IdentifyAsync(services);

        var row = await RowAsync("obscure.flac");

        Assert.Null(row.AcoustId);
        Assert.Equal(AcoustIdOutcome.Unknown, row.AcoustIdOutcome);
        Assert.NotNull(row.AcoustIdCheckedUtc);
        Assert.Equal(1, lookup.Calls);

        await IdentifyAsync(services);

        Assert.Equal(1, lookup.Calls);
        Assert.Equal(0, await PendingAsync(services));
    }

    /// <summary>
    /// Two close clusters leave the file alone rather than guessing at it.
    /// </summary>
    [Fact]
    public async Task AnAmbiguousMatchIsRecordedAndNothingIsWritten()
    {
        SkipWithoutTools();
        Copy(Corpus.Flac, "compilation.flac");

        var services = Build(
            allowMutation: true,
            new StubLookup([
                new AcoustIdMatch(Cluster, 0.95, []),
                new AcoustIdMatch(Guid.NewGuid(), 0.94, []),
            ]));

        await ScanAsync(services);
        await IdentifyAsync(services);

        var row = await RowAsync("compilation.flac");

        Assert.Equal(AcoustIdOutcome.Ambiguous, row.AcoustIdOutcome);
        Assert.Null(row.AcoustId);
        Assert.Null(row.AcoustIdTaggedUtc);

        var reader = new TagReader(new FileSystemAudioFileStore(_root));
        Assert.Null(await reader.ReadAcoustIdAsync(new LibraryPath("compilation.flac"), Token));
    }

    /// <summary>
    /// The default posture: everything expensive is done, nothing is written.
    /// </summary>
    /// <remarks>
    /// And the second half is what makes it worth doing that way — enabling
    /// mutation and running again writes the tags without spending a single
    /// further lookup, because the answers are already in the catalogue.
    /// </remarks>
    [Fact]
    public async Task WithMutationDisabledEverythingIsIdentifiedAndNothingIsWritten()
    {
        SkipWithoutTools();
        Copy(Corpus.Flac, "track.flac");

        var lookup = Answering(0.97);
        var dryRun = Build(allowMutation: false, lookup);

        await ScanAsync(dryRun);
        var summary = await IdentifyAsync(dryRun);

        Assert.Equal(1, summary.Identified);
        Assert.Equal(0, summary.Tagged);
        Assert.Equal(1, summary.WriteRefused);

        var row = await RowAsync("track.flac");
        Assert.Equal(new AcoustId(Cluster), row.AcoustId);
        Assert.NotNull(row.AcoustIdCheckedUtc);

        // Null is the whole point: this is what the second run finds its work by.
        Assert.Null(row.AcoustIdTaggedUtc);

        var reader = new TagReader(new FileSystemAudioFileStore(_root));
        Assert.Null(await reader.ReadAcoustIdAsync(new LibraryPath("track.flac"), Token));

        // A refusal is journalled, so a dry run leaves evidence of what it would
        // have done rather than only a number in a summary.
        await using var db = PostgresFixture.CreateContext(_connectionString);
        var journalled = await db.DomainEvents
            .Where(e => e.Type == AcoustIdTagWriter.RefusedEventType)
            .CountAsync(Token);
        Assert.Equal(1, journalled);
    }

    /// <summary>
    /// The journal is keyed by the catalogue id, not by the path.
    /// </summary>
    /// <remarks>
    /// Two reasons, and this test carries a path long enough to prove both.
    /// <c>DomainEvent.SubjectId</c> is a <c>varchar(200)</c> while a library path
    /// is up to 4096, so keying by path fails the moment somebody owns a deeply
    /// nested box set — it did, on the first real library this ran against, with
    /// 75 files identified and the 76th aborting the pass. And paths move: a
    /// rename would orphan every journal entry for a file, which is precisely
    /// when an undo matters.
    /// </remarks>
    [Fact]
    public async Task TheUndoJournalIsKeyedByCatalogueIdSoALongPathCannotBreakIt()
    {
        SkipWithoutTools();

        // Comfortably past varchar(200), and not contrived: classical box sets
        // produce paths like this routinely.
        var deep = string.Join(
            '/',
            "The Complete Recordings Of A Very Long Ensemble Name Indeed",
            "Volume 17 - Concertos, Sonatas And Assorted Chamber Works 1954-1961",
            "Disc 4 - Remastered From The Original Analogue Tapes (2019 Edition)",
            "04 - A Movement With An Unusually Descriptive And Lengthy Subtitle.flac");

        Assert.True(deep.Length > 200, "The path has to exceed the column to prove anything.");

        Directory.CreateDirectory(Path.Combine(_root, Path.GetDirectoryName(deep)!));
        File.Copy(Corpus.Flac, Path.Combine(_root, deep), overwrite: true);

        var services = Build(allowMutation: true, Answering(0.97));
        await ScanAsync(services);
        var summary = await IdentifyAsync(services);

        Assert.Equal(1, summary.Tagged);
        Assert.Equal(0, summary.Failed);

        var row = await RowAsync(deep);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var entry = await db.DomainEvents
            .SingleAsync(e => e.Type == AcoustIdTagWriter.WrittenEventType, Token);

        Assert.Equal(row.Id.ToString(), entry.SubjectId);

        // The path is still recorded — in the payload, where it is descriptive
        // rather than a key and has no length limit.
        Assert.Contains(deep, entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUndoJournalRecordsACommittedWriteAgainstTheRunsCorrelationId()
    {
        SkipWithoutTools();
        Copy(Corpus.Flac, "a.flac");
        Copy(Corpus.Mp3, "b.mp3");

        var services = Build(allowMutation: true, Answering(0.97));
        await ScanAsync(services);
        var summary = await IdentifyAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var written = await db.DomainEvents
            .Where(e => e.Type == AcoustIdTagWriter.WrittenEventType)
            .ToListAsync(Token);

        Assert.Equal(2, written.Count);

        // ADR 0002: a whole batch is reversible as a unit, which needs one id
        // shared by every file the pass touched.
        Assert.Single(written.Select(e => e.CorrelationId).Distinct(StringComparer.Ordinal));
        Assert.Equal(summary.JobId, written[0].CorrelationId);
    }

    /// <summary>
    /// A service outage costs the lookup, not the fingerprint.
    /// </summary>
    [Fact]
    public async Task AProviderOutageLeavesTheFilePendingButKeepsTheFingerprint()
    {
        SkipWithoutTools();
        Copy(Corpus.Flac, "track.flac");

        var services = Build(allowMutation: true, StubLookup.Unavailable());
        await ScanAsync(services);
        var summary = await IdentifyAsync(services);

        Assert.Equal(1, summary.Failed);

        var row = await RowAsync("track.flac");

        // Still on the worklist, so the next run retries it...
        Assert.Null(row.AcoustIdCheckedUtc);
        Assert.Equal(1, await PendingAsync(services));

        // ...but the expensive half is banked, so the retry is only a request.
        Assert.NotNull(row.Fingerprint);
        Assert.NotNull(row.FingerprintDuration);
    }

    [Fact]
    public async Task AFileTheDecoderRefusesIsMarkedAndNotRetried()
    {
        SkipWithoutTools();
        Copy(Corpus.NotAudioFlac, "broken.flac");

        var lookup = Answering(0.97);
        var services = Build(allowMutation: true, lookup);

        await ScanAsync(services);
        var summary = await IdentifyAsync(services);

        Assert.Equal(1, summary.Unfingerprintable);
        Assert.Equal(0, lookup.Calls);

        var row = await RowAsync("broken.flac");

        Assert.Equal(AcoustIdOutcome.Unfingerprintable, row.AcoustIdOutcome);
        Assert.Equal(IntegrityState.Unreadable, row.Integrity);
        Assert.NotNull(row.AcoustIdCheckedUtc);
    }

    /// <summary>
    /// A scan clears the columns a pass is filling in, so they must not overlap.
    /// </summary>
    [Fact]
    public void IdentificationIsRefusedWhileSomethingElseHoldsTheLibrary()
    {
        SkipWithoutTools();

        var services = Build(allowMutation: false, Answering(0.97));
        var gate = services.GetRequiredService<LibraryWorkGate>();
        var identification = services.GetRequiredService<IdentificationService>();

        Assert.True(gate.TryEnter("library.scan", out var lease));

        using (lease)
        {
            var outcome = identification.Start();
            Assert.Equal(IdentificationStatus.AlreadyRunning, outcome.Status);
            Assert.Null(outcome.JobId);
        }
    }

    [Fact]
    public async Task RunningTheWholePassTwiceIdentifiesEachFileOnce()
    {
        SkipWithoutTools();
        Copy(Corpus.Flac, "a.flac");
        Copy(Corpus.Mp3, "b.mp3");

        var lookup = Answering(0.97);
        var services = Build(allowMutation: true, lookup);

        await ScanAsync(services);
        await IdentifyAsync(services);

        Assert.Equal(2, lookup.Calls);

        var second = await IdentifyAsync(services);

        Assert.Equal(2, lookup.Calls);
        Assert.Equal(0, second.Examined);
    }

    private static void SkipWithoutTools() =>
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

    private static StubLookup Answering(params double[] scores) =>
        new([.. scores.Select(score => new AcoustIdMatch(Cluster, score, []))]);

    private void Copy(string corpusFile, string name) =>
        File.Copy(corpusFile, Path.Combine(_root, name), overwrite: true);

    private async Task<LibraryScanSummary> ScanAsync(ServiceProvider services)
    {
        var outcome = await services.GetRequiredService<LibraryScanService>().ScanAsync(Token);
        return outcome.Summary!;
    }

    /// <summary>Starts the pass and waits for it, since it is fire-and-forget by design.</summary>
    private static async Task<IdentificationSummary> IdentifyAsync(ServiceProvider services)
    {
        var identification = services.GetRequiredService<IdentificationService>();
        var outcome = identification.Start();

        Assert.Equal(IdentificationStatus.Started, outcome.Status);

        var deadline = DateTime.UtcNow.AddMinutes(2);

        while (identification.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.False(identification.IsRunning, "The pass did not finish within two minutes.");

        return identification.LastCompleted!;
    }

    private static Task<int> PendingAsync(ServiceProvider services) =>
        services.GetRequiredService<IdentificationService>().CountPendingAsync(Token);

    private async Task<MediaFile> RowAsync(string path)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        return await db.MediaFiles.AsNoTracking().SingleAsync(f => f.Path == path, Token);
    }

    /// <summary>
    /// The same graph Program builds, minus the web host and with AcoustID faked.
    /// </summary>
    private ServiceProvider Build(bool allowMutation, IAcoustIdLookup lookup)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSignalR();
        services.AddDbContext<FonotecaDbContext>(options => options.UseNpgsql(_connectionString));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new FileSystemAudioFileStore(_root));
        services.AddSingleton<IAudioFileStore>(sp => sp.GetRequiredService<FileSystemAudioFileStore>());
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<LibraryScanService>();

        services.AddSingleton<IAudioFingerprinter>(
            sp => new FpcalcFingerprinter(sp.GetRequiredService<FileSystemAudioFileStore>(), "fpcalc"));

        services.AddSingleton(lookup);
        services.AddSingleton<TagReader>();
        services.AddSingleton(new TagWriterOptions { AllowFileMutation = allowMutation });
        services.AddScoped<IEventLog, EventLog>();
        services.AddScoped<AcoustIdTagWriter>();

        services.AddSingleton<LibraryWorkGate>();
        services.AddSingleton<IdentificationService>();

        services.AddSingleton<IOptions<FonotecaOptions>>(
            new OptionsWrapper<FonotecaOptions>(new FonotecaOptions
            {
                LibraryPath = _root,
                AllowFileMutation = allowMutation,
                ScanConcurrency = 2,
            }));

        services.AddSingleton<IHostApplicationLifetime, StubLifetime>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>Answers with a fixed set of matches, and counts how often it was asked.</summary>
    private sealed class StubLookup(IReadOnlyList<AcoustIdMatch> matches, bool unavailable = false)
        : IAcoustIdLookup
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public static StubLookup Unavailable() => new([], unavailable: true);

        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
            AudioFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            if (unavailable)
            {
                throw new ProviderUnavailableException("AcoustID", "Stubbed outage.");
            }

            return Task.FromResult(matches);
        }
    }

    /// <summary>Never stops, because these tests own the lifetime themselves.</summary>
    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
