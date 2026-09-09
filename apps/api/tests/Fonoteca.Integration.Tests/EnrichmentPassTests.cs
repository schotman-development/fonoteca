using Fonoteca.Api.Library;
using Fonoteca.Api.Matching;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fonoteca.Providers.Wikidata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
    private static readonly Mbid Unheld = Mb("f1e2d3c4-b5a6-4978-8695-a4b3c2d1e0f9");
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

    /// <summary>The file half of the worklist, which is what every caller here means.</summary>
    private static async Task<int> PendingAsync(ServiceProvider services) =>
        (await services.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Files;

    /// <summary>
    /// The artist stage's reason for existing: an artist the catalogue already
    /// holds is described even though this run enriched no file that credits
    /// them.
    /// </summary>
    /// <remarks>
    /// <b>The one behaviour that distinguishes the stage's worklist from the
    /// obvious wiring.</b> Draining <c>Memo.Artists</c> — the set
    /// <c>CatalogueWriter</c> already fills — would describe only artists this
    /// run's files credited, so a library whose files are all enriched has an
    /// empty file worklist, touches no artists, and describes none of the
    /// thousands already sitting there. Keyed on the artist's own
    /// <c>LookupUtc</c>, the backlog and the new arrivals are one query.
    ///
    /// So: no media files at all here, and an artist row put in by hand the way
    /// an earlier run would have left it.
    /// </remarks>
    [Fact]
    public async Task AnArtistNoFileInThisRunCreditedIsStillDescribed()
    {
        await SeedArtistAsync(Karajan, "Karajan");

        var catalogue = new StubCatalogue(recording: null, work: null, artist: Conductor());
        var services = Build(Answering(Recording), catalogue);

        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var artist = await db.Artists.AsNoTracking().SingleAsync(Token);

        Assert.Equal(1, catalogue.ArtistCalls);

        // The lookup outranks the credit line that minted the row: this is the
        // artist's own record rather than what one sleeve printed.
        Assert.Equal("Herbert von Karajan", artist.Name);
        Assert.Equal("von Karajan, Herbert", artist.SortName);
        Assert.Equal("Person", artist.Type);
        Assert.Equal("AT", artist.Country);
        Assert.Equal(1908, artist.BeganYear);
        Assert.Equal(1989, artist.EndedYear);
        Assert.True(artist.Ended);
        Assert.Equal("classical, opera", artist.Genres);
        Assert.NotNull(artist.LookupUtc);
    }

    /// <summary>
    /// An artist with no photograph is stamped anyway, or the worklist never
    /// empties.
    /// </summary>
    /// <remarks>
    /// The fifth time this codebase has paid for the same lesson, after
    /// <c>AcoustIdCheckedUtc</c>, <c>RecordingLookupUtc</c>,
    /// <c>ReleaseLookupUtc</c> and <c>Artists.LookupUtc</c>. Keyed on
    /// <c>PortraitUrl</c> being null instead, the quarter of a library nobody
    /// has ever photographed — session players, small ensembles, most
    /// orchestras — is asked about on every run forever.
    ///
    /// Two artists in one batch and only one picture between them, so the
    /// stamp cannot be coming from "we found something".
    /// </remarks>
    [Fact]
    public async Task AnArtistWithNoPictureIsStampedJustAsOneWithAPictureIs()
    {
        await SeedArtistAsync(Karajan, "Karajan");
        await SeedArtistAsync(Berliner, "Berliner Philharmoniker");

        var portraits = new StubPortraits(
            (Karajan, "https://commons.wikimedia.org/wiki/Special:FilePath/Karajan.jpg"));

        var services = Build(
            Answering(Recording),
            new StubCatalogue(recording: null, work: null, artist: null),
            portraits);

        await EnrichAsync(services);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var rows = await db.Artists.AsNoTracking().ToListAsync(Token);

            Assert.All(rows, artist => Assert.NotNull(artist.PortraitLookupUtc));

            Assert.EndsWith(
                "Karajan.jpg",
                rows.Single(a => a.Mbid == Karajan).PortraitUrl ?? "",
                StringComparison.Ordinal);

            Assert.Null(rows.Single(a => a.Mbid == Berliner).PortraitUrl);
        }

        // And the second run asks nothing at all, which is what the stamp buys.
        await EnrichAsync(services);

        Assert.Equal(1, portraits.Calls);
    }

    /// <summary>
    /// The better picture wins, and only the artists worth paying for it get asked.
    /// </summary>
    /// <remarks>
    /// Two sources with two costs. Wikidata answers for a whole library in a
    /// dozen queries and is asked about everyone; Qobuz gives the picture a
    /// record shop would use and costs a request each against an allowance of
    /// 600 an hour, so it is asked only about the artists an album is billed to
    /// — 326 against 2,906 on the real catalogue, five minutes against five
    /// hours.
    ///
    /// The seed is the smallest thing that can tell the rule from its two
    /// plausible mistakes. Karajan is billed on a release the library holds and
    /// must get the press photograph <b>over</b> the Wikidata one, which is what
    /// stops the preference being written backwards. Mozart is credited on a
    /// recording and on no sleeve, and must keep his Wikidata picture <b>and
    /// never be asked about</b> — an implementation that asked Qobuz about
    /// everybody would pass every other assertion here and turn into a five-hour
    /// run against somebody's commercial API.
    /// </remarks>
    [Fact]
    public async Task ThePressPhotoWinsAndOnlyAlbumArtistsAreAskedForOne()
    {
        var karajan = await SeedBilledArtistAsync(Karajan, "Herbert von Karajan");
        await SeedArtistAsync(Mozart, "Mozart");

        var wikidata = new StubPortraits(
            (Karajan, "https://commons.wikimedia.org/wiki/Special:FilePath/Stadium.jpg"),
            (Mozart, "https://commons.wikimedia.org/wiki/Special:FilePath/Mozart.jpg"));

        var qobuz = new StubPortraits(
            (Karajan, "https://static.qobuz.com/images/artists/covers/large/karajan.jpg"));

        var services = Build(
            Answering(Recording),
            new StubCatalogue(recording: null, work: null, artist: null),
            wikidata,
            qobuz);

        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var rows = await db.Artists.AsNoTracking().ToListAsync(Token);

        Assert.Equal(
            "https://static.qobuz.com/images/artists/covers/large/karajan.jpg",
            rows.Single(a => a.Mbid == Karajan).PortraitUrl);

        Assert.EndsWith(
            "Mozart.jpg",
            rows.Single(a => a.Mbid == Mozart).PortraitUrl ?? "",
            StringComparison.Ordinal);

        // The expensive source saw the billed artist and nobody else.
        Assert.Equal([Karajan], qobuz.Asked.Select(artist => artist.Id));

        // And the cheap one saw both, since it answers for whoever the other
        // cannot.
        Assert.Contains(Mozart, wikidata.Asked.Select(artist => artist.Id));

        // The name travels, because a source searched by name cannot use an id.
        Assert.Equal("Herbert von Karajan", Assert.Single(qobuz.Asked).Name);
        Assert.Equal(karajan, rows.Single(a => a.Mbid == Karajan).Id);
    }

    /// <summary>
    /// The stage works through more than one batch.
    /// </summary>
    /// <remarks>
    /// Three artists at a batch size of two, which is the smallest seed that can
    /// tell "loops correctly" from "ran once and stopped". Everything else here
    /// passes either way — and this codebase has already lost twenty-five
    /// minutes of a live run to a pipeline bug that every one- and two-item test
    /// sailed straight past, which is the whole reason this test exists rather
    /// than being obvious from reading the loop.
    /// </remarks>
    [Fact]
    public async Task ThePictureStageWorksThroughEveryBatchAndNotJustTheFirst()
    {
        await SeedArtistAsync(Karajan, "Karajan");
        await SeedArtistAsync(Berliner, "Berliner Philharmoniker");
        await SeedArtistAsync(Mozart, "Mozart");

        var portraits = new StubPortraits(
            (Karajan, "https://commons.wikimedia.org/wiki/Special:FilePath/Karajan.jpg"),
            (Berliner, "https://commons.wikimedia.org/wiki/Special:FilePath/Berliner.jpg"),
            (Mozart, "https://commons.wikimedia.org/wiki/Special:FilePath/Mozart.jpg"));

        var services = Build(
            Answering(Recording),
            new StubCatalogue(recording: null, work: null, artist: null),
            portraits);

        await EnrichAsync(services);

        // Two batches for three artists, and a third call to find the worklist
        // empty is not made — the claim that comes back empty ends the stage
        // before anything is asked.
        Assert.Equal(2, portraits.Calls);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var rows = await db.Artists.AsNoTracking().ToListAsync(Token);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, artist => Assert.NotNull(artist.PortraitUrl));
    }

    /// <summary>
    /// A batch that failed is left for the next run, and does not spin.
    /// </summary>
    /// <remarks>
    /// Two things at once, and the second is why this test is worth more than it
    /// looks. Leaving the stamps null is the honest record — nothing is known
    /// about any of these artists, and an outage written down as "no pictures
    /// exist" would be believed forever.
    ///
    /// But the claim query is keyed on that same stamp, so a batch that fails
    /// without writing one is a batch the next iteration claims again. The
    /// stage has to <i>end</i> rather than continue, and the assertion that it
    /// does is the call count: the alternative is not a wrong answer but a pass
    /// that never finishes, hammering a service that has just failed.
    /// </remarks>
    [Fact]
    public async Task AFailedPictureLookupStampsNothingAndDoesNotRetryInTheSameRun()
    {
        await SeedArtistAsync(Karajan, "Karajan");
        await SeedArtistAsync(Berliner, "Berliner Philharmoniker");
        await SeedArtistAsync(Mozart, "Mozart");

        var portraits = new StubPortraits { Unavailable = true };

        var services = Build(
            Answering(Recording),
            new StubCatalogue(recording: null, work: null, artist: null),
            portraits);

        await EnrichAsync(services);

        // Three artists at a batch size of two is two batches' worth of work,
        // and the stage stopped after the first failure rather than working
        // through them.
        Assert.Equal(1, portraits.Calls);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var rows = await db.Artists.AsNoTracking().ToListAsync(Token);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, artist => Assert.Null(artist.PortraitLookupUtc));
        Assert.All(rows, artist => Assert.Null(artist.PortraitUrl));
    }

    /// <summary>
    /// A second run asks nothing, because the stamp is the worklist.
    /// </summary>
    /// <remarks>
    /// The lesson <c>AcoustIdCheckedUtc</c>, <c>RecordingLookupUtc</c> and
    /// <c>ReleaseLookupUtc</c> each paid for separately, applied a fourth time.
    /// Keyed on a field the answer fills instead, every artist MusicBrainz holds
    /// no country for is re-asked about on every run forever.
    /// </remarks>
    [Fact]
    public async Task DescribingAnArtistTwiceCostsOneLookup()
    {
        await SeedArtistAsync(Karajan, "Karajan");

        var catalogue = new StubCatalogue(recording: null, work: null, artist: Conductor());
        var services = Build(Answering(Recording), catalogue);

        await EnrichAsync(services);
        await EnrichAsync(services);

        Assert.Equal(1, catalogue.ArtistCalls);
    }

    /// <summary>
    /// An artist MusicBrainz cannot describe is still stamped; one it could not
    /// be asked about is not.
    /// </summary>
    /// <remarks>
    /// The two halves of the bargain, and they must not be the same answer. "We
    /// asked and the artist has been merged away" is an answer and leaving it
    /// unstamped re-asks forever; "MusicBrainz did not respond" is not an answer
    /// and stamping it would silently give up on the row for good, since no
    /// endpoint clears this column.
    /// </remarks>
    [Fact]
    public async Task AnUnknownArtistIsStampedAndAnUnreachableOneIsNot()
    {
        await SeedArtistAsync(Karajan, "Karajan");

        var known = new StubCatalogue(recording: null, work: null, artist: null);
        await EnrichAsync(Build(Answering(Recording), known));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var artist = await db.Artists.AsNoTracking().SingleAsync(Token);

            Assert.NotNull(artist.LookupUtc);
            Assert.Null(artist.Country);
        }

        await SeedArtistAsync(Mozart, "Mozart");

        await EnrichAsync(Build(Answering(Recording), StubCatalogue.Unavailable()));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var mozart = await db.Artists.AsNoTracking().SingleAsync(a => a.Mbid == Mozart, Token);

            Assert.Null(mozart.LookupUtc);
        }
    }

    /// <summary>
    /// The pending count comes back split, because one noun cannot cover both.
    /// </summary>
    /// <remarks>
    /// The panel prints this, and it printed it as "identified files with no
    /// recording yet" — so on a library whose files are all enriched and whose
    /// artists are not, the sentence named the wrong thing entirely and sent a
    /// person to the wrong screen. <c>Total</c> stays the sum because the
    /// button's enable rule reads it.
    ///
    /// Three nouns now, and the same artist is in two of them: an artist nobody
    /// has described is also an artist nobody has looked for a picture of, and
    /// they are separate work against separate services. Summing them would say
    /// "2" about one row, and folding them would put a stage costing seconds
    /// behind a number that used to mean forty-five minutes.
    /// </remarks>
    [Fact]
    public async Task ThePendingCountSeparatesFilesFromArtists()
    {
        await SeedArtistAsync(Karajan, "Karajan");

        var services = Build(Answering(Recording), new StubCatalogue(null, null));
        var pending = await services.GetRequiredService<EnrichmentService>()
            .CountPendingAsync(Token);

        Assert.Equal(0, pending.Files);
        Assert.Equal(1, pending.Artists);
        Assert.Equal(1, pending.Portraits);
        Assert.Equal(2, pending.Total);
    }

    /// <summary>
    /// A blank name from MusicBrainz does not erase the one the credit line gave.
    /// </summary>
    /// <remarks>
    /// The provider sends <c>""</c> for absent text rather than null, and
    /// <c>Artists.Name</c> is <c>NOT NULL</c> — which <c>""</c> satisfies. The
    /// stamp goes on in the same save and no endpoint clears it, so an
    /// unguarded write loses the name for good.
    /// </remarks>
    [Fact]
    public async Task AnArtistWithNoNameInTheAnswerKeepsTheOneItHad()
    {
        await SeedArtistAsync(Karajan, "Karajan");

        var nameless = Conductor() with { Name = "", SortName = null, Country = "AT" };
        var services = Build(Answering(Recording), new StubCatalogue(null, null, artist: nameless));

        await EnrichAsync(services);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var artist = await db.Artists.AsNoTracking().SingleAsync(Token);

        Assert.Equal("Karajan", artist.Name);

        // The rest of the answer is still written: a blank name is one bad
        // field, not a reason to discard a whole description.
        Assert.Equal("AT", artist.Country);
        Assert.NotNull(artist.LookupUtc);
    }

    /// <summary>An artist row as a credit line would have left it: a name and an MBID.</summary>
    /// <summary>
    /// A member's bands are stored as links, and only to artists the library has.
    /// </summary>
    /// <remarks>
    /// The fact behind the "With the band" shelf. MusicBrainz credits a Dire
    /// Straits recording to the <i>group</i>, so a member reaches the catalogue
    /// only as the composer of the work and every one of the band's albums reads
    /// as somebody else covering them.
    ///
    /// Two artists are named and only one is seeded, because the interesting
    /// half is the one that is dropped: MusicBrainz knows every band a session
    /// player passed through, and minting a row for each would put artists with
    /// no tracks into a list whose whole promise is that it browses what you own.
    /// </remarks>
    [Fact]
    public async Task AMembersBandsAreLinkedButOnlyToArtistsTheLibraryHolds()
    {
        await SeedArtistAsync(Karajan, "Karajan");
        await SeedArtistAsync(Berliner, "Berliner Philharmoniker");

        var catalogue = new StubCatalogue(
            recording: null,
            work: null,
            artist: Conductor() with { Bands = [Berliner, Unheld] });

        await EnrichAsync(Build(Answering(Recording), catalogue));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var member = await db.Artists.AsNoTracking().SingleAsync(a => a.Mbid == Karajan, Token);
        var band = await db.Artists.AsNoTracking().SingleAsync(a => a.Mbid == Berliner, Token);

        var links = await db.Relationships
            .AsNoTracking()
            .Where(r => r.Type == RelationshipTargets.Member)
            .ToListAsync(Token);

        var link = Assert.Single(links);
        Assert.Equal(member.Id, link.ArtistId);
        Assert.Equal(band.Id.Value, link.TargetId);
        Assert.Equal(RelationshipTargets.Artist, link.SourceType);
        Assert.Equal(RelationshipTargets.Artist, link.TargetType);

        // Neither end is a recording or a work, which is what keeps these rows
        // invisible to every query that reaches relationships through those.
        Assert.Null(link.RecordingId);
        Assert.Null(link.WorkId);

        // The band nothing in the library is by was not minted.
        Assert.False(await db.Artists.AnyAsync(a => a.Mbid == Unheld, Token));
    }

    /// <summary>
    /// Re-asking an artist does not record the same membership twice.
    /// </summary>
    /// <remarks>
    /// There is no unique index on <c>Relationships</c>, and re-asking the whole
    /// artist worklist is a hand-written <c>UPDATE</c> clearing
    /// <c>LookupUtc</c> — the documented path, the same one
    /// <c>AcoustIdCheckedUtc</c> takes. So this method runs again over artists it
    /// has already described, and a membership counted twice would double a band
    /// on every screen that ever groups by it.
    /// </remarks>
    [Fact]
    public async Task ReAskingAnArtistDoesNotRecordTheSameBandTwice()
    {
        await SeedArtistAsync(Karajan, "Karajan");
        await SeedArtistAsync(Berliner, "Berliner Philharmoniker");

        var described = Conductor() with { Bands = [Berliner] };

        await EnrichAsync(Build(Answering(Recording), new StubCatalogue(recording: null, work: null, artist: described)));

        // Exactly what the documented re-ask does.
        await using (var reset = PostgresFixture.CreateContext(_connectionString))
        {
            foreach (var row in await reset.Artists.ToListAsync(Token)) row.LookupUtc = null;

            await reset.SaveChangesAsync(Token);
        }

        await EnrichAsync(Build(Answering(Recording), new StubCatalogue(recording: null, work: null, artist: described)));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Single(await db.Relationships
            .AsNoTracking()
            .Where(r => r.Type == RelationshipTargets.Member)
            .ToListAsync(Token));
    }

    private async Task SeedArtistAsync(Mbid mbid, string name)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        db.Artists.Add(new Artist { Id = ArtistId.New(), Name = name, Mbid = mbid });

        await db.SaveChangesAsync(Token);
    }

    /// <summary>
    /// An artist billed on a release the library holds a file of — which is what
    /// <c>CatalogueEndpoints.AlbumArtistsAsync</c> means by an album artist, and
    /// therefore who is worth a rationed press photograph.
    /// </summary>
    /// <remarks>
    /// The whole chain has to exist, not just the credit: the rule joins the
    /// credit to a <c>MediaFile</c> through the release, so a release nothing is
    /// held of is not an album anybody owns.
    /// </remarks>
    private async Task<ArtistId> SeedBilledArtistAsync(Mbid mbid, string name)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var artist = new Artist { Id = ArtistId.New(), Name = name, Mbid = mbid };

        var group = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Mozart: Symphony no. 40",
            Mbid = new Mbid(Guid.CreateVersion7()),
        };

        var release = new Release
        {
            Id = ReleaseId.New(),
            Title = "Mozart: Symphony no. 40",
            Mbid = new Mbid(Guid.CreateVersion7()),
            ReleaseGroupId = group.Id,
            TrackCount = 1,
            DiscCount = 1,
        };

        db.Artists.Add(artist);
        db.ReleaseGroups.Add(group);
        db.Releases.Add(release);

        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            ReleaseId = release.Id,
            Position = 0,
        });

        db.MediaFiles.Add(new MediaFile
        {
            Id = MediaFileId.New(),
            Path = $"{name}/Symphony 40/01.flac",
            SizeBytes = 1024,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            ReleaseId = release.Id,
            ReleaseGroupId = group.Id,
        });

        await db.SaveChangesAsync(Token);

        return artist.Id;
    }

    /// <summary>Everything a credit line cannot carry, for one dead conductor.</summary>
    private static MusicBrainzArtist Conductor() =>
        new(
            Id: Karajan,
            Name: "Herbert von Karajan",
            SortName: "von Karajan, Herbert",
            Type: "Person",
            Disambiguation: null,
            Country: "AT",
            Gender: "Male",
            BeganYear: 1908,
            EndedYear: 1989,
            HasEnded: true,
            Genres: ["classical", "opera"],
            Bands: []);

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
    private ServiceProvider Build(
        IAcoustIdLookup lookup,
        IMusicBrainzCatalogue catalogue,
        IArtistPortraits? portraits = null,
        IArtistPortraits? pressPhotos = null,
        IArtistPortraits? thumbnails = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSignalR();
        services.AddDbContext<FonotecaDbContext>(options => options.UseNpgsql(_connectionString));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(lookup);
        services.AddSingleton(catalogue);

        // Finds nothing unless a test says otherwise, which is a real answer and
        // the commonest one — most of a library has no photograph anywhere. One
        // keyed registration per source, because the pass asks them in a stated
        // order and has to be able to say which it wants.
        services.AddKeyedSingleton<IArtistPortraits>(
            ArtistPortraitSources.Wikidata,
            portraits ?? new StubPortraits());

        services.AddKeyedSingleton<IArtistPortraits>(
            ArtistPortraitSources.Qobuz,
            pressPhotos ?? new StubPortraits());

        // The third source is registered and finds nothing unless a test asks
        // for it, so the existing Qobuz-versus-Wikidata expectations are
        // unchanged by its arrival — which is the point: a source that answers
        // for nobody must not alter what the others decide.
        services.AddKeyedSingleton<IArtistPortraits>(
            ArtistPortraitSources.AudioDb,
            thumbnails ?? new StubPortraits());

        services.Configure<WikidataOptions>(options => options.BatchSize = 2);
        services.AddSingleton<LibraryWorkGate>();
        services.AddSingleton<EnrichmentService>();
        services.AddSingleton<IHostApplicationLifetime, NeverStops>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// Answers pictures for the artists it was told about, and counts the asking.
    /// </summary>
    /// <remarks>
    /// The count is the point in two of the tests below: one asserts that an
    /// artist with no picture is never asked about twice, and the other that a
    /// batch which failed is not immediately re-claimed — a loop whose claim
    /// query is keyed on a stamp that the failure did not write.
    /// </remarks>
    private sealed class StubPortraits(params (Mbid Artist, string Image)[] pictures)
        : IArtistPortraits
    {
        private readonly Dictionary<Mbid, Uri> _pictures =
            pictures.ToDictionary(row => row.Artist, row => new Uri(row.Image));

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>When set, every batch fails the way an outage does.</summary>
        public bool Unavailable { get; init; }

        /// <summary>Every artist this stub was asked about, in order.</summary>
        public List<ArtistToPicture> Asked { get; } = [];

        public Task<IReadOnlyDictionary<Mbid, Uri>> FindAsync(
            IReadOnlyCollection<ArtistToPicture> artists,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            lock (Asked) Asked.AddRange(artists);

            if (Unavailable)
            {
                throw new ProviderUnavailableException("stub", "the service is down.");
            }

            IReadOnlyDictionary<Mbid, Uri> found = artists
                .Select(artist => artist.Id)
                .Where(_pictures.ContainsKey)
                .Distinct()
                .ToDictionary(artist => artist, artist => _pictures[artist]);

            return Task.FromResult(found);
        }
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
        bool unavailable = false,
        MusicBrainzArtist? artist = null) : IMusicBrainzCatalogue
    {
        private int _recordingCalls;
        private int _workCalls;
        private int _artistCalls;

        public int RecordingCalls => Volatile.Read(ref _recordingCalls);

        public int WorkCalls => Volatile.Read(ref _workCalls);

        public int ArtistCalls => Volatile.Read(ref _artistCalls);

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

        /// <summary>
        /// Echoes the id into the answer, like <see cref="GetRecordingAsync"/>.
        /// </summary>
        /// <remarks>
        /// The outage applies here too, and it has to: the artist stage's whole
        /// bargain is that a transient failure leaves <c>LookupUtc</c> null so
        /// the next run retries, and a stub that answered through an outage
        /// could not test it.
        /// </remarks>
        public Task<MusicBrainzArtist?> GetArtistAsync(
            Mbid id,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _artistCalls);

            if (unavailable) throw new ProviderUnavailableException("MusicBrainz", "Stubbed outage.");

            return Task.FromResult(artist is null ? null : artist with { Id = id });
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
