using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The worklist of refusals, through the real host against a seeded catalogue.
/// </summary>
/// <remarks>
/// Its own class and its own database rather than more cases in
/// <see cref="CatalogueEndpointTests"/>, because the seed here is the opposite
/// one: that class seeds a catalogue where everything worked, and every file
/// added to it that did <i>not</i> work would have to be checked against its
/// artist counts, its folder report and its release totals.
///
/// The shape under test is not "refused files come back". It is the three
/// judgements around that:
///
/// <list type="bullet">
/// <item>a set of files decided together comes back as <b>one</b> question,
/// recovered from the timestamp the attribution pass stamps on a whole
/// component;</item>
/// <item>a refusal nobody can answer — a transient lookup failure, a file the
/// pass has not reached, a file the decoder could not read — is <b>not</b> a
/// question, and putting it here would fill a worklist with work that answering
/// cannot clear;</item>
/// <item>the reason is the <b>first</b> pass that refused, not the silence that
/// followed it.</item>
/// </list>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class MatchingEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>
    /// One component's stamp: three files decided in a single act.
    /// </summary>
    /// <remarks>
    /// Whole microseconds, because <c>timestamptz</c> keeps microseconds and
    /// .NET keeps 100ns ticks — a stamp written at tick precision comes back
    /// rounded, and the grouping this endpoint does would then split one
    /// component in three.
    /// </remarks>
    private static readonly DateTimeOffset FirstComponent =
        DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset SecondComponent =
        DateTimeOffset.Parse("2026-02-01T10:05:00Z", CultureInfo.InvariantCulture);

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;
    private MediaFileId _ambiguous;
    private MediaFileId _notFingerprinted;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-matching-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);

            // The background warmer would put its own questions to the providers,
            // out of a thread nothing here waits for.
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        // Forces the host to start, which migrates.
        using var warm = _factory.CreateClient();

        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Four questions out of eleven files, and the arithmetic is the point.
    /// </summary>
    /// <remarks>
    /// Three of the eleven are refusals nobody can answer and are absent; four
    /// more are one album's worth of files decided together, and they are one
    /// question rather than four. A worklist that counted rows would report both
    /// numbers wrong in opposite directions.
    /// </remarks>
    [Fact]
    public async Task RefusalsAPersonCanAnswerAreCountedByQuestionAndByFile()
    {
        var queue = await QueueAsync();

        Assert.Equal(4, queue.Total);

        var releases = Assert.Single(queue.Kinds, kind => kind.Name == "release");
        Assert.Equal(2, releases.Questions);
        Assert.Equal(4, releases.Files);

        var recordings = Assert.Single(queue.Kinds, kind => kind.Name == "recording");
        Assert.Equal(2, recordings.Questions);
        Assert.Equal(2, recordings.Files);
    }

    /// <summary>
    /// A component is one question, and the timestamp is what makes it one.
    /// </summary>
    /// <remarks>
    /// The three files share nothing in the catalogue but the moment they were
    /// decided — different recordings, and two of them in a different directory,
    /// so neither the recording nor the folder could have grouped them. That is
    /// the real shape: a component is discovered by following shared candidate
    /// releases, and the stamp is the only trace of it that survives the pass.
    /// </remarks>
    [Fact]
    public async Task FilesDecidedTogetherComeBackAsOneQuestion()
    {
        var queue = await QueueAsync();

        // Biggest first, so the question with an album hanging on it leads.
        var component = queue.Items[0];

        Assert.Equal("release", component.Kind);
        Assert.Equal(3, component.Files);
        Assert.Equal($"release:{FirstComponent.UtcTicks}", component.Id);

        // Named by what the audio was identified as, never by the directory.
        Assert.Equal("Burn This Disco Out and 2 others", component.Subject);

        // Both directories the files sit in, carried but not believed.
        Assert.Equal(2, component.Folders.Count);
        Assert.Contains("Michael Jackson/Off the Wall (1979)", component.Folders, StringComparer.Ordinal);
        Assert.Contains("Michael Jackson/Singles", component.Folders, StringComparer.Ordinal);
    }

    /// <summary>
    /// One component, two outcomes, and the more actionable one names it.
    /// </summary>
    /// <remarks>
    /// Attribution decides a component together but records an outcome per file,
    /// so a component routinely holds both refusals at once: some of its files
    /// had releases to compare against and some had none. Splitting on that
    /// would invent a second question about the same rip, and reporting "nothing
    /// to compare against" for a set where something was compared would send a
    /// person looking for a gap that is not there.
    /// </remarks>
    [Fact]
    public async Task AComponentIsNamedByTheStrongerOfItsOutcomes()
    {
        var queue = await QueueAsync();

        Assert.Equal("NoConfidentFit", queue.Items[0].Reason);

        var fit = Assert.Single(queue.Reasons, reason => reason.Name == "NoConfidentFit");
        Assert.Equal(1, fit.Questions);
        Assert.Equal(3, fit.Files);

        // The lone file of the other component, which really did have nothing.
        var none = Assert.Single(queue.Reasons, reason => reason.Name == "NoCandidate");
        Assert.Equal(1, none.Questions);
        Assert.Equal(1, none.Files);
    }

    /// <summary>
    /// The reason is the first pass that refused, not the last one that was quiet.
    /// </summary>
    /// <remarks>
    /// A file AcoustID could not place is left <c>NotAttempted</c> by enrichment,
    /// which never sees it. Reading the outcomes in the other order describes a
    /// consequence — "no recording" — and sends a person to MusicBrainz to look
    /// for something that was never asked about.
    /// </remarks>
    [Fact]
    public async Task AFileIsNamedByTheFirstPassThatRefusedIt()
    {
        var queue = await QueueAsync();

        var ambiguous = Assert.Single(queue.Items, item => item.Subject == "07 Ambiguous.flac");
        Assert.Equal("recording", ambiguous.Kind);
        Assert.Equal("Ambiguous", ambiguous.Reason);
        Assert.Equal(1, ambiguous.Files);
        Assert.Equal("Bootlegs", Assert.Single(ambiguous.Folders));

        // Identified, and then MusicBrainz had nothing to link it to — the
        // second pass refusing, with the first having succeeded.
        var unlinked = Assert.Single(queue.Items, item => item.Subject == "08 No recording.flac");
        Assert.Equal("NoRecording", unlinked.Reason);
    }

    /// <summary>
    /// Three refusals that are nobody's decision, and none of them is here.
    /// </summary>
    /// <remarks>
    /// A lookup that did not answer is transient and the file stays on the
    /// pass's own worklist; a file no pass has reached is a queue position; and
    /// a file the decoder could not read is a question about the file rather
    /// than about the music, whose follow-up is an integrity check and not a
    /// match. All three would make this list longer and none of them would make
    /// it shorter by being answered.
    /// </remarks>
    [Fact]
    public async Task RefusalsNobodyCanAnswerAreNotQuestions()
    {
        var queue = await QueueAsync();

        Assert.DoesNotContain(queue.Items, item => item.Subject.Contains("Unfingerprintable", StringComparison.Ordinal));
        Assert.DoesNotContain(queue.Items, item => item.Subject.Contains("Lookup failed", StringComparison.Ordinal));
        Assert.DoesNotContain(queue.Items, item => item.Subject.Contains("Not attempted", StringComparison.Ordinal));

        // And nothing that was decided, either. A filed album is not a question.
        Assert.DoesNotContain(queue.Items, item => item.Subject.Contains("Attributed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Paging crosses the boundary between the two units without losing a row.
    /// </summary>
    /// <remarks>
    /// Components are counted in one query and their files fetched in another,
    /// and the files are paged in the database while the components are paged in
    /// memory — so the one page that straddles the two is the one that can drop
    /// or repeat a row. The totals stay the whole worklist's throughout, which is
    /// what lets a client say "showing 2 of 4" without a second request.
    /// </remarks>
    [Fact]
    public async Task PagingStraddlesTheTwoUnitsAndKeepsTheTotal()
    {
        var first = await QueueAsync("?skip=0&take=2");
        var second = await QueueAsync("?skip=2&take=2");

        Assert.Equal(4, first.Total);
        Assert.Equal(4, second.Total);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);

        // Both components, then both files: two kinds, one continuous sequence.
        Assert.All(first.Items, item => Assert.Equal("release", item.Kind));
        Assert.All(second.Items, item => Assert.Equal("recording", item.Kind));

        var ids = first.Items.Concat(second.Items).Select(item => item.Id).ToList();
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());

        // Past the end is an empty page rather than a wrapped one.
        Assert.Empty((await QueueAsync("?skip=4&take=2")).Items);
    }

    /// <summary>
    /// The candidate set comes back collapsed, and the collapse is the answer.
    /// </summary>
    /// <remarks>
    /// Two clusters, near-tied, and both of them name the same recording — the
    /// commonest shape of an ambiguous refusal and the one a person cannot read
    /// off two scores. Collapsed, it is one candidate carrying
    /// <c>Clusters == 2</c> and the sources of both, which says "one answer
    /// arriving twice" where the raw scores said "a disagreement".
    ///
    /// The second cluster also names a second recording, so the collapse is
    /// tested against a set where something genuinely does differ rather than
    /// against one where everything folds into a single row.
    /// </remarks>
    [Fact]
    public async Task NearTiedClustersNamingOneRecordingCollapseToOneCandidate()
    {
        var shared = new Mbid(Guid.CreateVersion7());
        var other = new Mbid(Guid.CreateVersion7());

        await using var factory = FactoryWith(
            new StubClusters(
            [
                new AcoustIdMatch(Guid.CreateVersion7(), 0.98, [new AcoustIdRecordingRef(shared, 1_200)]),
                new AcoustIdMatch(
                    Guid.CreateVersion7(),
                    0.97,
                    [new AcoustIdRecordingRef(shared, 147), new AcoustIdRecordingRef(other, 6)]),
            ]),
            new StubTitles());

        var candidates = await CandidatesAsync(factory, _ambiguous.Value);

        Assert.Equal(2, candidates.Clusters.Count);
        Assert.Equal(2, candidates.Total);
        // The file's whole length, not the two minutes fingerprinted — it is
        // what a candidate's printed length is compared against.
        Assert.Equal("3:45", candidates.Measured);

        // Score is the best across the clusters, sources are their sum: two
        // clusters were submitted by two populations, so that really is more
        // agreement rather than the same agreement counted twice.
        var first = candidates.Candidates[0];
        Assert.Equal(shared.Value, first.Mbid);
        Assert.Equal(2, first.Clusters);
        Assert.Equal(1_347, first.Sources);
        Assert.Equal(0.98, first.Score);

        var second = candidates.Candidates[1];
        Assert.Equal(other.Value, second.Mbid);
        Assert.Equal(1, second.Clusters);
    }

    /// <summary>
    /// MusicBrainz not answering costs the row its title, not its existence.
    /// </summary>
    /// <remarks>
    /// The identity and the evidence — the MBID, the score, the submissions
    /// behind it — all come from AcoustID, and they are the part that explains
    /// the refusal. Failing the whole request because the naming service is down
    /// would take away a readable answer to keep a decorative one.
    /// </remarks>
    [Fact]
    public async Task ARecordingLookupFailureLeavesTheCandidateWithoutATitle()
    {
        var mbid = new Mbid(Guid.CreateVersion7());

        await using var factory = FactoryWith(
            new StubClusters(
                [new AcoustIdMatch(Guid.CreateVersion7(), 0.91, [new AcoustIdRecordingRef(mbid, 4)])]),
            StubTitles.Unavailable());

        var candidates = await CandidatesAsync(factory, _ambiguous.Value);

        var only = Assert.Single(candidates.Candidates);
        Assert.Equal(mbid.Value, only.Mbid);
        Assert.Null(only.Title);
        Assert.Equal(4, only.Sources);
    }

    /// <summary>
    /// A long candidate set is cut, and says by how much.
    /// </summary>
    /// <remarks>
    /// The cut is a cap on <i>requests</i> — every row past it is another
    /// recording lookup, the heaviest call this application makes. So the rows
    /// stop and the count does not: a truncated set that reported its own length
    /// would be indistinguishable from a complete one, which is the mistake the
    /// worklist's own paging already pays for once.
    /// </remarks>
    [Fact]
    public async Task ALongCandidateSetIsCutToTheRequestBudgetAndSaysSo()
    {
        var recordings = Enumerable
            .Range(0, 9)
            .Select(index => new AcoustIdRecordingRef(new Mbid(Guid.CreateVersion7()), 10 - index))
            .ToList();

        await using var factory = FactoryWith(
            new StubClusters([new AcoustIdMatch(Guid.CreateVersion7(), 0.95, recordings)]),
            new StubTitles());

        var candidates = await CandidatesAsync(factory, _ambiguous.Value);

        Assert.Equal(9, candidates.Total);
        Assert.Equal(6, candidates.Candidates.Count);
    }

    /// <summary>
    /// AcoustID not answering fails the request, rather than reading as "nothing matched".
    /// </summary>
    /// <remarks>
    /// The distinction the outcome columns already keep everywhere else: a
    /// provider that did not answer is transient, and rendering its silence as
    /// an empty candidate set would tell somebody their audio matches nothing —
    /// a conclusion nobody reached.
    /// </remarks>
    [Fact]
    public async Task AProviderThatDoesNotAnswerIsNotAnEmptyCandidateSet()
    {
        await using var factory = FactoryWith(StubClusters.Unavailable(), new StubTitles());

        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            new Uri(
                $"/api/catalogue/matching/recordings/{_ambiguous.Value}/candidates",
                UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// A file with no fingerprint is a conflict, not a missing file.
    /// </summary>
    /// <remarks>
    /// It is here and the thing to ask AcoustID with is not — the ordinary state
    /// of a file the pass has not reached, and of one the decoder could not
    /// read. A 404 would say the catalogue does not hold it, and send somebody
    /// looking for a scan problem instead of a queue position.
    /// </remarks>
    [Fact]
    public async Task AFileWithNoFingerprintHasNothingToAskWith()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync(
            new Uri(
                $"/api/catalogue/matching/recordings/{_notFingerprinted.Value}/candidates",
                UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AFileTheCatalogueDoesNotHoldIsNotFound()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync(
            new Uri(
                $"/api/catalogue/matching/recordings/{Guid.CreateVersion7()}/candidates",
                UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A row for one file carries what the file is, not only what it is called.
    /// </summary>
    /// <remarks>
    /// Length, size and container, and all three are catalogue reads — the
    /// length is the duration <c>fpcalc</c> already measured and stored. So a
    /// worklist of seven hundred rows still opens no files, which is the
    /// property that keeps this endpoint a query rather than a pass. Bitrate and
    /// the file's own tags need the bytes and live on
    /// <c>matching/files/{id}</c>, one file at a time.
    /// </remarks>
    [Fact]
    public async Task AFileRowCarriesWhatTheCatalogueAlreadyKnowsAboutTheFile()
    {
        var queue = await QueueAsync();

        var ambiguous = Assert.Single(queue.Items, item => item.Subject == "07 Ambiguous.flac");

        Assert.Equal("3:45", ambiguous.Length);
        Assert.Equal("11.4 MiB", ambiguous.Size);
        Assert.Equal("FLAC", ambiguous.Format);
    }

    /// <summary>
    /// A component says none of it, and the silence is deliberate.
    /// </summary>
    /// <remarks>
    /// A set of files has a total runtime and a total size, and neither is a
    /// fact about music: an album's length belongs to the release rather than to
    /// the rip, and "412 MiB" tells nobody which pressing they are looking at.
    /// Summing them would put a number on the row that reads like evidence and
    /// is not.
    /// </remarks>
    [Fact]
    public async Task AComponentRowCarriesNoFileFactsBecauseItIsNotAFile()
    {
        var queue = await QueueAsync();

        var component = queue.Items[0];

        Assert.Equal("release", component.Kind);
        Assert.Null(component.Length);
        Assert.Null(component.Size);
        Assert.Null(component.Format);
    }

    /// <summary>
    /// A candidate carries the evidence the lookup had already paid for.
    /// </summary>
    /// <remarks>
    /// <c>GetRecordingAsync</c> asks for artists, credits, releases, release
    /// groups, media, ISRCs and two kinds of relationship — the request measured
    /// at 10.3 seconds cold — and the row built from it printed a title, a
    /// credit line, a length and three numbers. Everything asserted here arrived
    /// in that same response.
    ///
    /// The two that decide a real question:
    ///
    /// <list type="bullet">
    /// <item><b>drift</b>, because it is the edition discriminator the
    /// attribution pass already ranks pressings on, and a person should not have
    /// to subtract two timestamps in their head;</item>
    /// <item><b>the performers</b>, because MusicBrainz bills a classical
    /// recording to its composer — so on that catalogue every candidate row
    /// reads identically until the conductor and the orchestra appear.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task ACandidateCarriesTheEvidenceItsLookupAlreadyPaidFor()
    {
        var mbid = new Mbid(Guid.CreateVersion7());

        await using var factory = FactoryWith(
            new StubClusters(
                [new AcoustIdMatch(Guid.CreateVersion7(), 0.94, [new AcoustIdRecordingRef(mbid, 12)])]),
            new StubDetailedTitles());

        var candidates = await CandidatesAsync(factory, _ambiguous.Value);

        var only = Assert.Single(candidates.Candidates);

        // The file measures 3:45; the recording prints 3:48.
        Assert.Equal("-3.00s", only.Drift);

        // The earliest release, on the parts MusicBrainz stated — never widened
        // to a January the 1st nobody claimed.
        Assert.Equal("1979", only.FirstReleased);

        Assert.Equal("Symphony no. 3", only.Work);
        Assert.Equal("GBAAA7900001", Assert.Single(only.Isrcs));

        // Neither of these two is on the credit line, which bills the composer.
        Assert.Collection(
            only.Performers,
            who =>
            {
                Assert.Equal("conductor", who.Role);
                Assert.Equal("Bernard Haitink", who.Name);
            },
            who =>
            {
                Assert.Equal("performing orchestra", who.Role);
                Assert.Equal("Koninklijk Concertgebouworkest", who.Name);
            });

        Assert.Equal(2, only.Appearances);

        // Earliest first, so the original sits above the compilation that
        // reprinted it — which is the whole reason the list carries a date.
        Assert.Collection(
            only.Releases,
            release =>
            {
                Assert.Equal("Off the Wall", release.Title);
                Assert.Equal("1979", release.Released);
                Assert.Equal(10, release.TrackCount);
            },
            release =>
            {
                Assert.Equal("The Ultimate Collection", release.Title);
                Assert.Equal("2004-11-16", release.Released);
            });
    }

    /// <summary>
    /// A stored document written before a field existed is rebuilt, not served.
    /// </summary>
    /// <remarks>
    /// Deserialising last week's JSON into today's record succeeds and leaves
    /// the new collections <b>null</b>, which then serialise back to the client
    /// as <c>null</c> where the generated schema promises an array — so the
    /// cache that exists to make this screen fast would be the thing that broke
    /// it, and only for the files somebody had already looked at. Same rule as
    /// the malformed-JSON case it sits beside: the most a stale entry may cost
    /// is the request it was there to save.
    /// </remarks>
    [Fact]
    public async Task ACachedDocumentMissingAFieldIsRebuiltRatherThanServed()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.FirstAsync(row => row.Id == _ambiguous, Token);

            // The shape this endpoint returned before the evidence was added:
            // valid JSON, fresh, and three arrays short.
            file.RecordingCandidatesJson =
                """
                {
                  "mediaFileId": "00000000-0000-0000-0000-000000000000",
                  "measured": "3:45",
                  "clusters": [],
                  "total": 1,
                  "candidates": [
                    {
                      "mbid": "11111111-1111-1111-1111-111111111111",
                      "title": "Stale",
                      "artist": null,
                      "disambiguation": null,
                      "length": "3:45",
                      "score": 0.9,
                      "sources": 1,
                      "clusters": 1,
                      "release": null
                    }
                  ],
                  "asOfUtc": "2026-02-01T10:00:00+00:00",
                  "fromCache": false
                }
                """;

            file.RecordingCandidatesUtc = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(Token);
        }

        var mbid = new Mbid(Guid.CreateVersion7());

        await using var factory = FactoryWith(
            new StubClusters(
                [new AcoustIdMatch(Guid.CreateVersion7(), 0.94, [new AcoustIdRecordingRef(mbid, 3)])]),
            new StubTitles());

        var candidates = await CandidatesAsync(factory, _ambiguous.Value);

        Assert.False(candidates.FromCache);

        var only = Assert.Single(candidates.Candidates);
        Assert.Equal(mbid.Value, only.Mbid);
        Assert.NotNull(only.Isrcs);
        Assert.NotNull(only.Performers);
        Assert.NotNull(only.Releases);
    }

    /// <summary>
    /// A sweep builds the answers, so the click that follows costs no request.
    /// </summary>
    /// <remarks>
    /// The point of the warmer, asserted the only way that means anything: by
    /// counting provider calls. The first open of a question is one AcoustID
    /// turn plus a MusicBrainz lookup per candidate — measured at 24 seconds
    /// against the live providers — and this proves the sweep spends them
    /// instead, once, out of a thread nobody is waiting for.
    /// </remarks>
    [Fact]
    public async Task WarmingTheWorklistMakesOpeningAQuestionACacheRead()
    {
        var lookups = new CountingClusters(
            [new AcoustIdMatch(Guid.CreateVersion7(), 0.94, [new AcoustIdRecordingRef(new Mbid(Guid.CreateVersion7()), 3)])]);

        await using var factory = FactoryWith(lookups, new StubTitles());

        // Forces the host to start, which is what constructs the warmer.
        using (var start = factory.CreateClient()) { }

        var warmer = factory.Services
            .GetServices<IHostedService>()
            .OfType<CandidateWarmService>()
            .Single();

        var warmed = await warmer.SweepAsync(Token);

        // The ambiguous file and the one component whose refusal a person can
        // answer. The three nobody can answer are not swept, for the same reason
        // they are not on the worklist.
        Assert.Equal(2, warmed);
        Assert.Equal(1, lookups.Calls);

        var candidates = await CandidatesAsync(factory, _ambiguous.Value);

        Assert.True(candidates.FromCache);
        Assert.Equal(1, lookups.Calls);

        // And a second sweep finds everything already answered, which is what
        // makes repeating it forever cost nothing.
        Assert.Equal(0, await warmer.SweepAsync(Token));
        Assert.Equal(1, lookups.Calls);
    }

    /// <summary>
    /// An outage is not swept into the cache and believed for a week.
    /// </summary>
    /// <remarks>
    /// The endpoint stores a candidate set whose rows have no titles on purpose
    /// — MusicBrainz refusing one lookup does not make the clusters wrong, and
    /// the person looking at it has <c>?refresh=true</c>. Unattended that
    /// reasoning inverts: AcoustID goes on answering, so nothing else notices,
    /// and a sweep would write the outage across the whole worklist. The sweep
    /// reads a document nobody could name as a failure and stops.
    /// </remarks>
    [Fact]
    public async Task AnOutageIsNotSweptIntoTheCache()
    {
        var lookups = new CountingClusters(
            [new AcoustIdMatch(Guid.CreateVersion7(), 0.94, [new AcoustIdRecordingRef(new Mbid(Guid.CreateVersion7()), 3)])]);

        await using var factory = FactoryWith(lookups, StubTitles.Unavailable());

        using (var start = factory.CreateClient()) { }

        var warmer = factory.Services
            .GetServices<IHostedService>()
            .OfType<CandidateWarmService>()
            .Single();

        Assert.Equal(0, await warmer.SweepAsync(Token));
    }

    /// <summary>
    /// The same host, with the two providers replaced by answers we control.
    /// </summary>
    /// <remarks>
    /// Registered through <c>ConfigureTestServices</c>, which runs after the
    /// application's own wiring, so these win the resolution without the
    /// application's registrations having to be found and removed.
    /// </remarks>
    private WebApplicationFactory<Program> FactoryWith(
        IAcoustIdLookup lookup,
        IMusicBrainzCatalogue catalogue) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);

            // The background warmer would put its own questions to the providers,
            // out of a thread nothing here waits for.
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(lookup);
                services.AddSingleton(catalogue);
            });
        });

    private static async Task<RecordingCandidatesResponse> CandidatesAsync(
        WebApplicationFactory<Program> factory,
        Guid mediaFileId)
    {
        using var client = factory.CreateClient();

        var candidates = await client.GetFromJsonAsync<RecordingCandidatesResponse>(
            new Uri($"/api/catalogue/matching/recordings/{mediaFileId}/candidates", UriKind.Relative),
            Token);

        Assert.NotNull(candidates);
        return candidates;
    }

    /// <summary>Answers with a fixed set of clusters, and counts being asked.</summary>
    private sealed class CountingClusters(IReadOnlyList<AcoustIdMatch> matches) : IAcoustIdLookup
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
            AudioFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(matches);
        }
    }

    /// <summary>Answers with a fixed set of clusters, or refuses to answer.</summary>
    private sealed class StubClusters(
        IReadOnlyList<AcoustIdMatch> matches,
        bool unavailable = false) : IAcoustIdLookup
    {
        public static StubClusters Unavailable() => new([], unavailable: true);

        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
            AudioFingerprint fingerprint,
            CancellationToken cancellationToken = default) =>
            unavailable
                ? throw new ProviderUnavailableException("acoustid", "Down.")
                : Task.FromResult(matches);
    }

    /// <summary>Names whatever recording it is asked about, or refuses to answer.</summary>
    private sealed class StubTitles(bool unavailable = false) : IMusicBrainzCatalogue
    {
        public static StubTitles Unavailable() => new(unavailable: true);

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
            if (unavailable) throw new ProviderUnavailableException("musicbrainz", "Down.");

            return Task.FromResult<MusicBrainzRecording?>(new MusicBrainzRecording(
                id,
                "Midnight in Montgomery",
                null,
                TimeSpan.FromSeconds(225),
                [new MusicBrainzCredit(null, "Alan Jackson", null, null, null, "Person")],
                [],
                [],
                [],
                null,
                null));
        }

        public Task<MusicBrainzDiscography> BrowseReleaseGroupsForArtistAsync(
            Mbid artist,
            CancellationToken cancellationToken = default) =>
            unavailable
                ? throw new ProviderUnavailableException("musicbrainz", "Down.")
                : Task.FromResult(new MusicBrainzDiscography([], Complete: true));

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(
            Mbid recording,
            CancellationToken cancellationToken = default) =>
            unavailable
                ? throw new ProviderUnavailableException("musicbrainz", "Down.")
                : Task.FromResult<IReadOnlyList<MusicBrainzReleaseCandidate>>([]);

        public Task<MusicBrainzRelease?> GetReleaseAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzRelease?>(null);

        public Task<MusicBrainzArtist?> GetArtistAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzArtist?>(null);

        public Task<MusicBrainzWork?> GetWorkAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzWork?>(null);
    }


    /// <summary>
    /// A recording answered in full: releases, ISRCs, a work and the people the
    /// credit line leaves out.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StubTitles"/> rather than an extension of it,
    /// because the thin answer is a case in its own right — most of the existing
    /// assertions here are about what survives when MusicBrainz says very little
    /// — and widening the shared stub would quietly change what those tests are
    /// testing.
    /// </remarks>
    private sealed class StubDetailedTitles : IMusicBrainzCatalogue
    {
        /// <summary>Never searched for. Only the by-hand album screen searches.</summary>
        public Task<IReadOnlyList<MusicBrainzReleaseMatch>> SearchReleasesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseMatch>>([]);

        public Task<MusicBrainzRecording?> GetRecordingAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzRecording?>(new MusicBrainzRecording(
                id,
                "Burn This Disco Out",
                null,

                // Three seconds longer than the file measures, which is what a
                // different master looks like.
                TimeSpan.FromSeconds(228),

                [new MusicBrainzCredit(null, "Michael Jackson", null, null, null, "Person")],
                ["GBAAA7900001"],
                [
                    // Deliberately out of order, so the endpoint's sort is what
                    // puts the original above the compilation.
                    new MusicBrainzAppearance(
                        new Mbid(Guid.CreateVersion7()),
                        "The Ultimate Collection",
                        new ReleaseDate(2004, 11, 16),
                        "US",
                        "Official",
                        null,
                        null,
                        "Album",
                        2,
                        7,
                        "7",
                        18),
                    new MusicBrainzAppearance(
                        new Mbid(Guid.CreateVersion7()),
                        "Off the Wall",
                        new ReleaseDate(1979, null, null),
                        "US",
                        "Official",
                        null,
                        null,
                        "Album",
                        1,
                        10,
                        "10",
                        10),
                ],
                [
                    new MusicBrainzRelation(
                        "conductor", null, new Mbid(Guid.CreateVersion7()), "Bernard Haitink", null, "Person", null),
                    new MusicBrainzRelation(
                        "performing orchestra",
                        null,
                        new Mbid(Guid.CreateVersion7()),
                        "Koninklijk Concertgebouworkest",
                        null,
                        "Group",
                        null),
                ],
                new Mbid(Guid.CreateVersion7()),
                "Symphony no. 3"));

        public Task<MusicBrainzDiscography> BrowseReleaseGroupsForArtistAsync(
            Mbid artist,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MusicBrainzDiscography([], Complete: true));

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(
            Mbid recording,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseCandidate>>([]);

        public Task<MusicBrainzRelease?> GetReleaseAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzRelease?>(null);

        public Task<MusicBrainzArtist?> GetArtistAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzArtist?>(null);

        public Task<MusicBrainzWork?> GetWorkAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzWork?>(null);
    }

    private async Task<MatchingQueueResponse> QueueAsync(string query = "")
    {
        using var client = _factory!.CreateClient();

        var queue = await client.GetFromJsonAsync<MatchingQueueResponse>(
            new Uri($"/api/catalogue/matching{query}", UriKind.Relative), Token);

        Assert.NotNull(queue);
        return queue;
    }

    private async Task SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        // A component: three files decided in one act, sharing no recording and
        // not even a folder. Only the stamp says they belong together.
        var burn = Recording(db, "Burn This Disco Out");
        var girlfriend = Recording(db, "Girlfriend");
        var floor = Recording(db, "Get on the Floor");

        db.MediaFiles.Add(Refused(
            File("Michael Jackson/Off the Wall (1979)/10 Burn This Disco Out.flac", burn),
            FirstComponent,
            ReleaseAttributionOutcome.NoConfidentFit));

        db.MediaFiles.Add(Refused(
            File("Michael Jackson/Singles/06 Girlfriend.flac", girlfriend),
            FirstComponent,
            ReleaseAttributionOutcome.NoConfidentFit));

        // The same component, a weaker outcome. One question, not two.
        db.MediaFiles.Add(Refused(
            File("Michael Jackson/Singles/04 Get on the Floor.flac", floor),
            FirstComponent,
            ReleaseAttributionOutcome.NoCandidate));

        // A second component, decided five minutes later.
        var live = Recording(db, "Sloe Gin (live)");

        db.MediaFiles.Add(Refused(
            File("Joe Bonamassa/Live/03 Sloe Gin.flac", live),
            SecondComponent,
            ReleaseAttributionOutcome.NoCandidate));

        // Filed, and therefore not a question.
        var filed = Recording(db, "Rock with You");
        var attributed = File("Michael Jackson/Off the Wall (1979)/02 Attributed.flac", filed);
        attributed.ReleaseLookupUtc = FirstComponent;
        attributed.AttributionOutcome = ReleaseAttributionOutcome.Attributed;
        db.MediaFiles.Add(attributed);

        // Two file-level refusals a person could answer. The ambiguous one
        // carries a fingerprint, because that is what makes its candidate set
        // recoverable — the pass threw the candidates away, and the stored
        // fingerprint is the only reason they can be asked for again.
        var ambiguous = Unidentified("Bootlegs/07 Ambiguous.flac", AcoustIdOutcome.Ambiguous);
        ambiguous.Fingerprint = "AQAAmockfingerprint";
        ambiguous.FingerprintDuration = TimeSpan.FromSeconds(225);
        ambiguous.SizeBytes = 11_953_766;
        _ambiguous = ambiguous.Id;
        db.MediaFiles.Add(ambiguous);

        var unlinked = Unidentified("Bootlegs/08 No recording.flac", AcoustIdOutcome.Identified);
        unlinked.EnrichmentOutcome = EnrichmentOutcome.NoRecording;
        unlinked.RecordingLookupUtc = SecondComponent;
        db.MediaFiles.Add(unlinked);

        // And three nobody can.
        db.MediaFiles.Add(Unidentified("Bootlegs/09 Unfingerprintable.flac", AcoustIdOutcome.Unfingerprintable));

        var failed = Unidentified("Bootlegs/10 Lookup failed.flac", AcoustIdOutcome.Identified);
        failed.EnrichmentOutcome = EnrichmentOutcome.LookupFailed;
        db.MediaFiles.Add(failed);

        var untouched = Unidentified("Bootlegs/11 Not attempted.flac", AcoustIdOutcome.NotAttempted);
        _notFingerprinted = untouched.Id;
        db.MediaFiles.Add(untouched);

        await db.SaveChangesAsync(Token);
    }

    private static Recording Recording(FonotecaDbContext db, string title)
    {
        var recording = new Recording
        {
            Id = RecordingId.New(),
            Title = title,
            Mbid = new Mbid(Guid.CreateVersion7()),
        };

        db.Recordings.Add(recording);
        return recording;
    }

    private static MediaFile Refused(
        MediaFile file,
        DateTimeOffset decidedUtc,
        ReleaseAttributionOutcome outcome)
    {
        file.ReleaseLookupUtc = decidedUtc;
        file.AttributionOutcome = outcome;
        return file;
    }

    /// <summary>A file the identification or enrichment pass could not place.</summary>
    private static MediaFile Unidentified(string path, AcoustIdOutcome outcome) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 12_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        AcoustIdOutcome = outcome,
        AcoustIdCheckedUtc = outcome == AcoustIdOutcome.NotAttempted ? null : FirstComponent,
    };

    /// <summary>A file identification and enrichment both got through.</summary>
    private static MediaFile File(string path, Recording recording) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 42_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        RecordingId = recording.Id,
        AcoustIdOutcome = AcoustIdOutcome.Identified,
        AcoustIdCheckedUtc = FirstComponent,
        RecordingLookupUtc = FirstComponent,
        EnrichmentOutcome = EnrichmentOutcome.Linked,
    };
}
