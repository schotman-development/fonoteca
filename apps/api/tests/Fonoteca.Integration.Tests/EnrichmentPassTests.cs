using Fonoteca.Api.Library;
using Fonoteca.Api.Matching;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The enrichment pass, against real PostgreSQL and stubbed providers.
/// </summary>
/// <remarks>
/// No files and no audio, which is the point rather than a shortcut: this pass
/// reads fingerprints out of the catalogue and never opens anything, so a test
/// that needed a library on disk would be testing something the pass does not
/// do. Both web services are stubbed; everything else — the service graph, the
/// upserts, the migration — is the one the application runs.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class EnrichmentPassTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Mbid Recording = Mb("85db2cdf-80c9-4aa2-9789-19328dde47ed");
    private static readonly Mbid SecondRecording = Mb("e52e52b6-ee59-4be4-a858-88d3dd4742b4");
    private static readonly Mbid WorkId = Mb("70729fa3-654b-4a0b-85ec-5c0ba3b3fb80");
    private static readonly Mbid Mozart = Mb("b972f589-fb0e-474e-b64a-803b0364fa75");
    private static readonly Mbid Karajan = Mb("5b11f4ce-a62d-471e-81fc-a69a8278c7da");
    private static readonly Mbid Berliner = Mb("d0e5b1e2-9d3b-4d6b-9a0b-7c8f9a0b1c2d");
    private static readonly Mbid Soloist = Mb("c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f");

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
    /// The case the feature exists for. One file, four artists, and only one of
    /// them on the credit line.
    /// </summary>
    [Fact]
    public async Task AClassicalTrackIsFiledUnderItsComposerConductorAndOrchestra()
    {
        await SeedAsync(("Karajan/Beethoven 5/01 - Allegro.flac", Cluster(1)));

        var services = Build(Answering(Recording), Classical());
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var artists = await db.Artists.AsNoTracking().OrderBy(a => a.Name).ToListAsync(Token);

        Assert.Equal(
            ["Berliner Philharmoniker", "Herbert von Karajan", "Wolfgang Amadeus Mozart"],
            artists.Select(a => a.Name));

        // The soloist is fetched and deliberately not credited — see
        // PrimaryCredits. Forty of these on one recording is what the rule is
        // protecting the artist list from.
        Assert.DoesNotContain(artists, a => a.Mbid == Soloist);

        var file = await db.MediaFiles.AsNoTracking().SingleAsync(Token);

        Assert.NotNull(file.RecordingId);
        Assert.Equal(EnrichmentOutcome.Linked, file.EnrichmentOutcome);
        Assert.NotNull(file.RecordingLookupUtc);

        var recording = await db.Recordings.AsNoTracking().SingleAsync(Token);
        Assert.Equal(Recording, recording.Mbid);
        Assert.NotNull(recording.WorkId);

        // The credit line, which for a classical recording names the composer
        // and nobody who played it.
        var credit = await db.ArtistCredits.AsNoTracking().SingleAsync(Token);
        var composer = artists.Single(a => a.Mbid == Mozart);
        Assert.Equal(composer.Id, credit.ArtistId);

        // The performers, as typed links. The composer arrives twice — billed and
        // as the work's writer — and PrimaryCredits keeps the stronger claim, so
        // there is no composer relationship beside the credit above.
        var links = await db.Relationships.AsNoTracking().OrderBy(r => r.Type).ToListAsync(Token);

        Assert.Equal(["conductor", "ensemble"], links.Select(r => r.Type));
        Assert.All(links, link => Assert.Equal("artist", link.SourceType));
        Assert.All(links, link => Assert.Equal("recording", link.TargetType));
    }

    /// <summary>
    /// A composer whose work MusicBrainz links but who is not on the credit line
    /// lands as a work relationship, which is what makes "everything this
    /// composer wrote" answerable across every performance of it.
    /// </summary>
    [Fact]
    public async Task AWritersLinkHangsOffTheWorkRatherThanThePerformance()
    {
        await SeedAsync(("Karajan/Beethoven 5/01 - Allegro.flac", Cluster(1)));

        var services = Build(
            Answering(Recording),
            new StubCatalogue(
                recording: RecordingWith(
                    credits: [Credit(Karajan, "Herbert von Karajan")],
                    relations: []),
                work: WorkWith([Relation("composer", Mozart, "Wolfgang Amadeus Mozart", "Person")])));

        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var work = await db.Works.AsNoTracking().SingleAsync(Token);
        var link = await db.Relationships.AsNoTracking().SingleAsync(Token);

        Assert.Equal("composer", link.Type);
        Assert.Equal("work", link.TargetType);
        Assert.Equal(work.Id.Value, link.TargetId);
        Assert.Equal(work.Id, link.WorkId);
    }

    /// <summary>
    /// Five encodings of one track are one AcoustID cluster, one recording and
    /// one turn at each rate limit — which on a duplicate-heavy library is the
    /// difference between an hour and an afternoon.
    /// </summary>
    [Fact]
    public async Task FilesSharingAClusterCostOneLookupBetweenThem()
    {
        await SeedAsync(
            ("Hart/Seesaw/01 - Nutbush.flac", Cluster(1)),
            ("Hart/Seesaw/01 - Nutbush.mp3", Cluster(1)),
            ("Compilations/Blues/07 - Nutbush.flac", Cluster(1)));

        var lookup = Answering(Recording);
        var catalogue = Collaboration();

        await EnrichAsync(Build(lookup, catalogue));

        Assert.Equal(1, lookup.Calls);
        Assert.Equal(1, catalogue.RecordingCalls);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        // One recording, three files hanging off it. That split is the dedupe
        // anchor the whole entity graph is built around.
        var recording = await db.Recordings.AsNoTracking().SingleAsync(Token);

        Assert.Equal(3, await db.MediaFiles.CountAsync(f => f.RecordingId == recording.Id, Token));
    }

    /// <summary>
    /// A work is shared by every recording of it, so a four-movement symphony
    /// ripped twice is eight recordings and one work lookup.
    /// </summary>
    [Fact]
    public async Task AWorkIsLookedUpOnceHoweverManyRecordingsShareIt()
    {
        await SeedAsync(
            ("Karajan/Beethoven 5/01.flac", Cluster(1)),
            ("Karajan/Beethoven 5/02.flac", Cluster(2)));

        var catalogue = Classical();

        // Two clusters naming two different recordings — two movements of the
        // same symphony — so the recording memo cannot stand in for the work one.
        var lookup = new StubLookup(cluster => cluster == Cluster(1) ? Recording : SecondRecording);

        await EnrichAsync(Build(lookup, catalogue));

        Assert.Equal(2, catalogue.RecordingCalls);
        Assert.Equal(1, catalogue.WorkCalls);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(2, await db.Recordings.CountAsync(Token));
        Assert.Equal(1, await db.Works.CountAsync(Token));
    }

    /// <summary>
    /// Both shortfalls are answers rather than errors, and are told apart on the
    /// row — a cluster nobody has linked to MusicBrainz invites submitting one,
    /// a merged-away MBID does not.
    /// </summary>
    [Fact]
    public async Task ClustersWithNoRecordingAndRecordingsMusicBrainzLostAreDistinguished()
    {
        await SeedAsync(
            ("Bootlegs/unknown.flac", Cluster(1)),
            ("Karajan/Beethoven 5/01.flac", Cluster(2)));

        // The first cluster resolves to nothing; the second names a recording
        // MusicBrainz answers 404 for.
        var lookup = new StubLookup(cluster => cluster == Cluster(1) ? null : Recording);

        await EnrichAsync(Build(lookup, new StubCatalogue(recording: null, work: null)));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var rows = await db.MediaFiles.AsNoTracking().OrderBy(f => f.Path).ToListAsync(Token);

        Assert.Equal(EnrichmentOutcome.NoRecording, rows[0].EnrichmentOutcome);
        Assert.Equal(EnrichmentOutcome.RecordingNotFound, rows[1].EnrichmentOutcome);

        // Both were asked and neither will be asked again: the worklist is
        // "have we asked", not "did we get an answer".
        Assert.All(rows, row => Assert.NotNull(row.RecordingLookupUtc));
        Assert.All(rows, row => Assert.Null(row.RecordingId));

        Assert.Equal(0, await PendingAsync(Build(lookup, new StubCatalogue(null, null))));
    }

    /// <summary>
    /// A provider outage must leave the file exactly where it was, or the pass
    /// converts a five-minute network blip into a permanent hole in the library.
    /// </summary>
    [Fact]
    public async Task AnOutageLeavesTheFileOnTheWorklist()
    {
        await SeedAsync(("Karajan/Beethoven 5/01.flac", Cluster(1)));

        var services = Build(Answering(Recording), StubCatalogue.Unavailable());
        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var row = await db.MediaFiles.AsNoTracking().SingleAsync(Token);

        Assert.Null(row.RecordingLookupUtc);
        Assert.Equal(EnrichmentOutcome.NotAttempted, row.EnrichmentOutcome);
        Assert.Equal(1, await PendingAsync(services));
    }

    /// <summary>
    /// A second pass over an unchanged library must be free and must not
    /// duplicate a single row — the upserts key on the MBID for exactly this.
    /// </summary>
    [Fact]
    public async Task ASecondPassIsANoOp()
    {
        await SeedAsync(("Karajan/Beethoven 5/01 - Allegro.flac", Cluster(1)));

        var first = Answering(Recording);
        await EnrichAsync(Build(first, Classical()));

        var second = Answering(Recording);
        var services = Build(second, Classical());

        Assert.Equal(0, await PendingAsync(services));

        await EnrichAsync(services);

        Assert.Equal(0, second.Calls);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(1, await db.Recordings.CountAsync(Token));
        Assert.Equal(3, await db.Artists.CountAsync(Token));
        Assert.Equal(1, await db.ArtistCredits.CountAsync(Token));
        Assert.Equal(2, await db.Relationships.CountAsync(Token));
    }

    /// <summary>
    /// A file somebody filed under an album by hand gets its graph, without
    /// spending an AcoustID turn to find out what it already knows.
    /// </summary>
    /// <remarks>
    /// The gap this closes, measured on the target library: 615 files sat at
    /// <see cref="EnrichmentOutcome.LinkedByPerson"/> and 433 of them had no
    /// work at all — Beethoven's complete symphonies among them, 33 movements
    /// whose album page therefore had nothing to group by. They are unreachable
    /// by the first worklist on all three of its clauses: AcoustID never placed
    /// them, so most carry no cluster; the album screen stamps
    /// <c>RecordingLookupUtc</c>; and the decision stamps
    /// <c>IdentityDecidedUtc</c>.
    /// </remarks>
    [Fact]
    public async Task AFilePersonFiledUnderAnAlbumIsEnrichedFromTheRecordingItAlreadyHas()
    {
        await SeedPersonFiledAsync("Concertgebouw/Beethoven 1/01 - Adagio molto.flac");

        var lookup = Answering(Recording);
        var catalogue = Classical();
        var services = Build(lookup, catalogue);

        // It is on the worklist, so the dashboard's count is honest about it.
        Assert.Equal(1, await PendingAsync(services));

        await EnrichAsync(services);

        // The recording MBID was already in the catalogue, so no AcoustID turn
        // was spent working out what this file is.
        Assert.Equal(0, lookup.Calls);
        Assert.Equal(1, catalogue.RecordingCalls);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var file = await db.MediaFiles.AsNoTracking().SingleAsync(Token);

        // Now it is what the value promises: linked, with its artists.
        Assert.Equal(EnrichmentOutcome.Linked, file.EnrichmentOutcome);

        var recording = await db.Recordings
            .AsNoTracking()
            .Include(r => r.Work)
            .SingleAsync(Token);

        Assert.Equal("Symphony no. 40 in G minor, K. 550", recording.Work?.Title);

        // The conductor and the orchestra, which a release track list never had.
        // The soloist is absent on purpose: PrimaryCredits excludes individual
        // instrument performers, and this pass is the same rule as the other.
        Assert.Equal(
            ["Berliner Philharmoniker", "Herbert von Karajan", "Wolfgang Amadeus Mozart"],
            await db.Artists.AsNoTracking().OrderBy(a => a.Name).Select(a => a.Name)
                .ToListAsync(Token));

        // And it is off the worklist, so a second pass spends nothing.
        Assert.Equal(0, await PendingAsync(services));
    }

    /// <summary>
    /// A recording MusicBrainz will not produce leaves the person's decision
    /// exactly where it was.
    /// </summary>
    /// <remarks>
    /// <c>RecordingNotFound</c> is one of the two outcomes the by-hand album
    /// screen reads as an open question, so writing it here would put somebody's
    /// answered file back on the worklist as a question — undoing the decision
    /// this pass exists to complete.
    /// </remarks>
    [Fact]
    public async Task ARecordingMusicBrainzCannotProduceLeavesTheDecisionAlone()
    {
        await SeedPersonFiledAsync("Concertgebouw/Beethoven 1/01 - Adagio molto.flac");

        await EnrichAsync(Build(Answering(Recording), new StubCatalogue(recording: null, work: null)));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var file = await db.MediaFiles.AsNoTracking().SingleAsync(Token);

        Assert.Equal(EnrichmentOutcome.LinkedByPerson, file.EnrichmentOutcome);
        Assert.NotNull(file.RecordingId);
    }

    /// <summary>
    /// A file as the by-hand album screen leaves it: an identity off a release
    /// track list, a lookup stamp, a decision stamp, and no cluster.
    /// </summary>
    private async Task SeedPersonFiledAsync(string path)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var recording = new Recording
        {
            Id = RecordingId.New(),
            Title = "Symphony No. 1 in C major, Op. 21: I. Adagio molto",
            Mbid = Recording,
        };

        db.Recordings.Add(recording);

        db.MediaFiles.Add(new MediaFile
        {
            Id = MediaFileId.New(),
            Path = path,
            SizeBytes = 1024,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            RecordingId = recording.Id,
            RecordingLookupUtc = DateTimeOffset.UtcNow,
            IdentityDecidedUtc = DateTimeOffset.UtcNow,
            EnrichmentOutcome = EnrichmentOutcome.LinkedByPerson,
        });

        await db.SaveChangesAsync(Token);
    }

    /// <summary>
    /// Identification's cheap path adopts an AcoustID straight out of a file's
    /// tags and never fingerprints it. Those rows have an identity and nothing to
    /// ask AcoustID with — and reopening the file here is the one thing this pass
    /// does not do, so they are recorded and skipped rather than crashing it.
    /// </summary>
    [Fact]
    public async Task AFileWithNoStoredFingerprintIsRecordedRatherThanReopened()
    {
        await SeedAsync(fingerprinted: false, ("Adopted/track.flac", Cluster(1)));

        var lookup = Answering(Recording);
        await EnrichAsync(Build(lookup, Classical()));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var row = await db.MediaFiles.AsNoTracking().SingleAsync(Token);

        Assert.Equal(0, lookup.Calls);
        Assert.Equal(EnrichmentOutcome.NoRecording, row.EnrichmentOutcome);
        Assert.NotNull(row.RecordingLookupUtc);
    }

    /// <summary>
    /// Every file sharing a cluster keeps the evidence, not just the one that asked.
    /// </summary>
    /// <remarks>
    /// The memo exists so five encodings of one track cost one lookup between
    /// them, and it used to hold the recording that lookup collapsed to. Holding
    /// AcoustID's whole answer instead is what lets the saving stay invisible:
    /// with the old shape, four of the five files would have had no stored
    /// evidence and the cache behind the Identify screen would have had a gap
    /// with no explanation in it.
    /// </remarks>
    [Fact]
    public async Task EveryFileSharingAClusterKeepsTheAnswerThatOneLookupBought()
    {
        await SeedAsync(
            ("Hart/Seesaw/01 - Nutbush.flac", Cluster(1)),
            ("Hart/Seesaw/01 - Nutbush.mp3", Cluster(1)),
            ("Compilations/Blues/07 - Nutbush.flac", Cluster(1)));

        var lookup = Answering(Recording);

        await EnrichAsync(Build(lookup, Collaboration()));

        Assert.Equal(1, lookup.Calls);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var rows = await db.MediaFiles.AsNoTracking().ToListAsync(Token);

        Assert.Equal(3, rows.Count);

        Assert.All(rows, row =>
        {
            Assert.NotNull(row.AcoustIdMatchesUtc);

            var matches = AcoustIdEvidence.Deserialise(row.AcoustIdMatchesJson);

            Assert.NotNull(matches);
            Assert.Equal(Recording, Assert.Single(Assert.Single(matches).Recordings).Id);
        });
    }

    /// <summary>
    /// A file with no fingerprint does not answer for every other file sharing its cluster.
    /// </summary>
    /// <remarks>
    /// The memo is keyed on the <i>cluster</i> and answers "what did AcoustID say
    /// about this audio". A file with no stored fingerprint cannot be asked
    /// about without reopening it, which this pass does not do — but that is a
    /// fact about one file's columns, not about the cluster, and recording it
    /// against the cluster makes the next file to share one inherit an answer
    /// nobody ever asked for.
    ///
    /// It went wrong twice over. The second file is never looked up, so it is
    /// filed as <c>NoRecording</c> despite carrying a perfectly good
    /// fingerprint; and since <i>it</i> has the columns that say a lookup was
    /// possible, that emptiness is written into its evidence column and believed
    /// for a week, which then stops the decision endpoint asking either.
    ///
    /// Seeded fingerprintless-first because the worklist is ordered by id and
    /// the ids are time-ordered, so insertion order is processing order. The
    /// other way round the memo is filled with the real answer and nothing is
    /// under test.
    /// </remarks>
    [Fact]
    public async Task AFingerprintlessFileDoesNotAnswerForTheClusterItShares()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.MediaFiles.Add(Pending("Tagged/01 - No fingerprint.flac", Cluster(1), false));
            await db.SaveChangesAsync(Token);

            db.MediaFiles.Add(Pending("Ripped/01 - Fingerprinted.flac", Cluster(1), true));
            await db.SaveChangesAsync(Token);
        }

        var lookup = Answering(Recording);

        await EnrichAsync(Build(lookup, Collaboration()));

        // Asked once — for the file that could be asked about.
        Assert.Equal(1, lookup.Calls);

        await using var after = PostgresFixture.CreateContext(_connectionString);

        var untouched = await after.MediaFiles.AsNoTracking()
            .SingleAsync(f => f.Path == "Tagged/01 - No fingerprint.flac", Token);

        Assert.Null(untouched.RecordingId);
        Assert.Null(untouched.AcoustIdMatchesJson);
        Assert.Equal(EnrichmentOutcome.NoRecording, untouched.EnrichmentOutcome);

        var linked = await after.MediaFiles.AsNoTracking()
            .SingleAsync(f => f.Path == "Ripped/01 - Fingerprinted.flac", Token);

        Assert.NotNull(linked.RecordingId);
        Assert.Equal(EnrichmentOutcome.Linked, linked.EnrichmentOutcome);

        var matches = AcoustIdEvidence.Deserialise(linked.AcoustIdMatchesJson);

        Assert.NotNull(matches);
        Assert.Equal(Recording, Assert.Single(Assert.Single(matches).Recordings).Id);
    }

    private static MediaFile Pending(string path, AcoustId cluster, bool fingerprinted) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 1024,
        LastModifiedUtc = DateTimeOffset.UtcNow,
        AcoustId = cluster,
        AcoustIdCheckedUtc = DateTimeOffset.UtcNow,
        AcoustIdOutcome = AcoustIdOutcome.Identified,
        Fingerprint = fingerprinted ? FingerprintFor(cluster) : null,
        FingerprintDuration = fingerprinted ? TimeSpan.FromMinutes(7) : null,
    };

    private static Task<int> PendingAsync(ServiceProvider services) =>
        services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token);

    private static async Task EnrichAsync(ServiceProvider services)
    {
        var enrichment = services.GetRequiredService<EnrichmentService>();

        Assert.Equal(EnrichmentStatus.Started, enrichment.Start().Status);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (enrichment.IsRunning && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Token);
        }

        Assert.False(enrichment.IsRunning, "The enrichment pass did not finish.");
    }

    /// <summary>
    /// One catalogue row per file, as identification would have left it.
    /// </summary>
    /// <param name="fingerprinted">
    /// Whether the row carries a stored fingerprint. False models the file
    /// identification adopted straight from an existing tag, which never
    /// fingerprints.
    /// </param>
    private async Task SeedAsync(
        bool fingerprinted,
        params (string Path, AcoustId Cluster)[] files)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        foreach (var (path, cluster) in files)
        {
            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = path,
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                AcoustId = cluster,
                AcoustIdCheckedUtc = DateTimeOffset.UtcNow,
                AcoustIdOutcome = AcoustIdOutcome.Identified,

                // The fingerprint carries the cluster, so the stub lookup can
                // answer per cluster from the one argument it is given. Real
                // fingerprints work the same way — the same audio produces the
                // same string — which is what makes the memo sound.
                Fingerprint = fingerprinted ? FingerprintFor(cluster) : null,
                FingerprintDuration = fingerprinted ? TimeSpan.FromMinutes(7) : null,
            });
        }

        await db.SaveChangesAsync(Token);
    }

    private Task SeedAsync(params (string Path, AcoustId Cluster)[] files) =>
        SeedAsync(fingerprinted: true, files);

    private static string FingerprintFor(AcoustId cluster) => $"AQAA-{cluster.Value:N}";

    private static AcoustId Cluster(int n) =>
        new(new Guid($"11111111-2222-4333-8444-{n:D12}"));

    private static Mbid Mb(string value) => new(Guid.Parse(value));

    /// <summary>A lookup that names the same recording whatever it is asked.</summary>
    private static StubLookup Answering(Mbid recording) => new(_ => recording);

    /// <summary>
    /// The classical shape: billed to the composer, everyone who played it in
    /// relationships, and a soloist who must not become an artist.
    /// </summary>
    private static StubCatalogue Classical() =>
        new(
            recording: RecordingWith(
                credits: [Credit(Mozart, "Wolfgang Amadeus Mozart")],
                relations:
                [
                    Relation("conductor", Karajan, "Herbert von Karajan", "Person"),
                    Relation("performing orchestra", Berliner, "Berliner Philharmoniker", "Group"),
                    Relation("instrument", Soloist, "Anne-Sophie Mutter", "Person"),
                ]),
            work: WorkWith([Relation("composer", Mozart, "Wolfgang Amadeus Mozart", "Person")]));

    private static StubCatalogue Collaboration() =>
        new(
            recording: new MusicBrainzRecording(
                Recording, "Nutbush City Limits", null, TimeSpan.FromMinutes(4),
                [Credit(Karajan, "Beth Hart", " & "), Credit(Berliner, "Joe Bonamassa")],
                [], [], [], null, null),
            work: null);

    private static MusicBrainzRecording RecordingWith(
        IReadOnlyList<MusicBrainzCredit> credits,
        IReadOnlyList<MusicBrainzRelation> relations) =>
        new(
            Recording,
            "Symphony No. 40 in G minor, K. 550: I. Molto allegro",
            null,
            TimeSpan.FromMinutes(7),
            credits,
            [],
            [],
            relations,
            WorkId,
            "Symphony no. 40 in G minor, K. 550");

    private static MusicBrainzWork WorkWith(IReadOnlyList<MusicBrainzRelation> relations) =>
        new(WorkId, "Symphony no. 40 in G minor, K. 550", "Symphony", relations);

    private static MusicBrainzCredit Credit(Mbid artist, string name, string? joinPhrase = null) =>
        new(artist, name, $"{name} (sort)", joinPhrase, null, "Person");

    private static MusicBrainzRelation Relation(
        string type,
        Mbid artist,
        string name,
        string? artistType) =>
        new(type, null, artist, name, $"{name} (sort)", artistType, null);

    /// <summary>The same graph Program builds for this pass, minus the web host.</summary>
    private ServiceProvider Build(IAcoustIdLookup lookup, IMusicBrainzCatalogue catalogue)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSignalR();
        services.AddDbContext<FonotecaDbContext>(options => options.UseNpgsql(_connectionString));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(lookup);
        services.AddSingleton(catalogue);
        services.AddSingleton<LibraryWorkGate>();
        services.AddSingleton<EnrichmentService>();
        services.AddSingleton<IHostApplicationLifetime, NeverStops>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// Answers one recording MBID per cluster, and counts the asking.
    /// </summary>
    /// <remarks>
    /// The cluster is recovered from the fingerprint, which is how the real
    /// service works: the argument identifies the audio, and the audio is what
    /// determines the cluster. That keeps the memo under test rather than
    /// papered over — a stub answering from a call counter would pass whether or
    /// not the pass ever asked twice about the same cluster.
    /// </remarks>
    private sealed class StubLookup(Func<AcoustId, Mbid?> answer) : IAcoustIdLookup
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
            AudioFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            var cluster = new AcoustId(Guid.Parse(fingerprint.Value["AQAA-".Length..]));
            var recording = answer(cluster);

            IReadOnlyList<AcoustIdMatch> matches = recording is { } id
                ? [new AcoustIdMatch(cluster.Value, 0.97, [new AcoustIdRecordingRef(id, 40)])]
                : [new AcoustIdMatch(cluster.Value, 0.97, [])];

            return Task.FromResult(matches);
        }
    }

    /// <summary>
    /// Answers a recording for whatever id it is asked, and counts each kind of ask.
    /// </summary>
    /// <remarks>
    /// The id is echoed into the answer rather than fixed, so two clusters
    /// naming two recordings really do produce two rows — which is what makes
    /// the work memo's test mean anything.
    /// </remarks>
    private sealed class StubCatalogue(
        MusicBrainzRecording? recording,
        MusicBrainzWork? work,
        bool unavailable = false) : IMusicBrainzCatalogue
    {
        private int _recordingCalls;
        private int _workCalls;

        public int RecordingCalls => Volatile.Read(ref _recordingCalls);

        public int WorkCalls => Volatile.Read(ref _workCalls);

        public static StubCatalogue Unavailable() => new(null, null, unavailable: true);

        /// <summary>Never searched for. Only the by-hand album screen searches.</summary>
        public Task<IReadOnlyList<MusicBrainzReleaseMatch>> SearchReleasesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseMatch>>([]);

        public Task<MusicBrainzRecording?> GetRecordingAsync(
            Mbid id,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _recordingCalls);

            if (unavailable) throw new ProviderUnavailableException("MusicBrainz", "Stubbed outage.");

            return Task.FromResult(recording is null ? null : recording with { Id = id });
        }

        public Task<MusicBrainzRelease?> GetReleaseAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Enrichment does not attribute releases; nothing here should call this.");

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(
            Mbid recording,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Enrichment does not attribute releases; nothing here should call this.");

        public Task<MusicBrainzWork?> GetWorkAsync(Mbid id, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _workCalls);
            return Task.FromResult(work);
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
