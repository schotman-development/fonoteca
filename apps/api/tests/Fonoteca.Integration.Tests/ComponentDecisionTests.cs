using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// A person answering an album question, through the real host.
/// </summary>
/// <remarks>
/// The attribution half of <see cref="RecordingDecisionTests"/>, and the shape
/// under test is the documented failure: an album the library holds whole, and a
/// box set that reprints it and explains exactly the same files. Ranking by how
/// many files a release explains puts the box set first — both explain ten — and
/// only coverage separates them, ten of ten against ten of forty.
///
/// No file is opened and none exists. That is the point rather than a shortcut:
/// an album is a catalogue fact, so this decision touches no bytes, needs no
/// <c>Fonoteca:AllowFileMutation</c> and writes no undo journal. A test needing
/// a real FLAC would be testing something this endpoint does not do.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class ComponentDecisionTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The album, held whole. Ten tracks, ten files, exact running times.</summary>
    private static readonly Mbid Album = new(new Guid("a1111111-1111-4111-8111-111111111111"));

    /// <summary>The box set that reprints it, plus thirty songs nobody owns.</summary>
    private static readonly Mbid BoxSet = new(new Guid("b2222222-2222-4222-8222-222222222222"));

    private static readonly Mbid AlbumGroup = new(new Guid("c3333333-3333-4333-8333-333333333333"));

    /// <summary>A real release holding none of this library's recordings.</summary>
    private static readonly Mbid Elsewhere = new(new Guid("e5555555-5555-4555-8555-555555555555"));

    /// <summary>The component's stamp: one clock reading, shared by every file in it.</summary>
    private static readonly DateTimeOffset Refused =
        StoreTime.ToStorePrecision(
            DateTimeOffset.Parse("2026-03-01T09:00:00Z", CultureInfo.InvariantCulture));

    /// <summary>A second component, refused a moment later. A different question about one album.</summary>
    private static readonly DateTimeOffset Later =
        StoreTime.ToStorePrecision(
            DateTimeOffset.Parse("2026-03-01T09:05:00Z", CultureInfo.InvariantCulture));

    private string _connectionString = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _connectionString = await postgres.CreateDatabaseAsync(Token);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// The album outranks the box set that reprints it, on coverage alone.
    /// </summary>
    /// <remarks>
    /// Both explain all ten files, so anything ranking by "files explained" ties
    /// them or prefers the larger. `AnAlbumIsFoundEvenUnderThirty…` pins the same
    /// judgement inside the pass; this pins it on the screen a person reads,
    /// where getting it wrong means offering the wrong answer first.
    /// </remarks>
    [Fact]
    public async Task TheAlbumOutranksTheBoxSetThatReprintsIt()
    {
        await SeedComponentAsync(10);

        using var factory = Factory();

        var candidates = await CandidatesAsync(factory);

        Assert.Equal(2, candidates.Candidates.Count);
        Assert.Equal(10, candidates.Files);
        Assert.Equal(10, candidates.Recordings);
        Assert.Equal(10, candidates.Browsed);

        var best = candidates.Candidates[0];

        Assert.Equal(Album.Value.ToString(), best.Mbid);
        Assert.Equal(1.0, best.Coverage, 3);
        Assert.Equal(10, best.FilesExplained);
        Assert.Equal(0, best.MeanDriftMs);

        // Every slot is a row, filled or not — that is what makes "you are
        // missing track 7" answerable, and the box set is where it shows.
        var box = candidates.Candidates[1];

        Assert.Equal(BoxSet.Value.ToString(), box.Mbid);
        Assert.Equal(40, box.Slots.Count);
        Assert.Equal(10, box.Slots.Count(slot => slot.Path is not null));
        Assert.Equal(0.25, box.Coverage, 3);
    }

    /// <summary>
    /// The second visit spends no requests, and says it did not.
    /// </summary>
    /// <remarks>
    /// The whole point of the cache. A gather is one browse per recording plus
    /// one lookup per album offered — measured against a live mirror, up to two
    /// minutes and 38 turns at the rate limit — so re-opening a question a person
    /// closed and came back to must not cost that again. The stub counts calls,
    /// because "it was faster" is not a test.
    ///
    /// <c>refresh=true</c> is the other half: a stored answer a person has reason
    /// to doubt has to be re-askable, and the count is what proves the flag is
    /// wired to anything.
    /// </remarks>
    [Fact]
    public async Task TheSecondVisitIsAnsweredFromTheCatalogue()
    {
        await SeedComponentAsync(10);

        var catalogue = new StubCatalogue();
        using var factory = Factory(catalogue);

        var first = await CandidatesAsync(factory);

        Assert.False(first.FromCache);
        Assert.Equal(10, catalogue.Browses);
        Assert.Equal(2, catalogue.Lookups);

        var second = await CandidatesAsync(factory);

        Assert.True(second.FromCache);
        Assert.Equal(10, catalogue.Browses);
        Assert.Equal(2, catalogue.Lookups);

        // Same answer, not merely a fast one.
        Assert.Equal(
            first.Candidates.Select(candidate => candidate.Mbid),
            second.Candidates.Select(candidate => candidate.Mbid));

        var again = await CandidatesAsync(factory, refresh: true);

        Assert.False(again.FromCache);
        Assert.Equal(20, catalogue.Browses);
        Assert.Equal(4, catalogue.Lookups);
    }

    /// <summary>
    /// A stored answer for a component that has since changed size is not used.
    /// </summary>
    /// <remarks>
    /// A component is a <i>set</i>, and the set moves under the document: a scan
    /// clears the stamp on a file whose bytes changed, and a person answering
    /// part of a component takes files out of it. Every coverage figure in the
    /// document was computed against the set that existed when it was written, so
    /// a different count means the numbers describe a component that no longer
    /// exists.
    /// </remarks>
    [Fact]
    public async Task AStoredAnswerForADifferentSetOfFilesIsAMiss()
    {
        await SeedComponentAsync(10);

        var catalogue = new StubCatalogue();
        using var factory = Factory(catalogue);

        await CandidatesAsync(factory);
        Assert.Equal(10, catalogue.Browses);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var leaving = await db.MediaFiles.OrderBy(file => file.Path).FirstAsync(Token);

            leaving.ReleaseLookupUtc = null;
            leaving.AttributionOutcome = ReleaseAttributionOutcome.NotAttempted;

            await db.SaveChangesAsync(Token);
        }

        var rebuilt = await CandidatesAsync(factory);

        Assert.False(rebuilt.FromCache);
        Assert.Equal(9, rebuilt.Files);
        Assert.Equal(19, catalogue.Browses);
    }

    /// <summary>
    /// The attribution pass leaves the candidate set behind, so the screen spends nothing.
    /// </summary>
    /// <remarks>
    /// <b>This is the one that matters.</b> The pass has already browsed every
    /// recording and fetched every track list worth fetching by the time it
    /// refuses a component — recovering that afterwards was pure waste. A person
    /// opening a refused album should reach a stored document and cost the
    /// providers nothing at all, and the call counts are the only honest way to
    /// say so.
    /// </remarks>
    [Fact]
    public async Task ThePassStoresTheCandidatesItWouldOtherwiseDiscard()
    {
        // Drifted past the pass's tolerance, so it refuses the component rather
        // than filing it — a confidently attributed component is not a question
        // and deliberately gets no document.
        await SeedComponentAsync(10, attributed: false, drift: TimeSpan.FromSeconds(5));

        var catalogue = new StubCatalogue();
        using var factory = Factory(catalogue);
        using var client = factory.CreateClient();

        var started = await client.PostAsync(
            new Uri("/api/library/attribute", UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        await WaitForAttributionAsync(client);

        var duringPass = catalogue.Browses + catalogue.Lookups;

        Assert.True(duringPass > 0, "the pass should have asked MusicBrainz something");

        // The component's identity is the stamp the pass chose, which is a clock
        // reading and cannot be seeded — so it is read back the way the worklist
        // reads it.
        DateTimeOffset stamp;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var decided = await db.MediaFiles.FirstAsync(Token);

            Assert.Equal(ReleaseAttributionOutcome.NoConfidentFit, decided.AttributionOutcome);

            stamp = decided.ReleaseLookupUtc!.Value;
        }

        var candidates = await CandidatesAsync(factory, component: stamp);

        Assert.True(candidates.FromCache);
        Assert.Equal(duringPass, catalogue.Browses + catalogue.Lookups);
        Assert.NotEmpty(candidates.Candidates);
    }

    /// <summary>
    /// Choosing the album files every file, writes its whole track list, and closes the question.
    /// </summary>
    /// <remarks>
    /// Three separate facts. The rows carry the release and their own track; the
    /// catalogue carries all ten tracks whether or not a file sits on one, which
    /// is what a release page needs to say what is missing; and the component is
    /// gone from the worklist, which is what a person came to do.
    /// </remarks>
    [Fact]
    public async Task ChoosingTheAlbumFilesEveryFileAndWritesItsWholeTrackList()
    {
        await SeedComponentAsync(10);

        using var factory = Factory();

        var decision = await DecideAsync(factory, new ComponentDecisionRequest("release", Album.Value));

        Assert.Equal(10, decision.Decided);
        Assert.Equal(0, decision.StillOpen);
        Assert.Equal(ReleaseAttributionOutcome.AttributedByPerson.ToString(), decision.Outcome);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = await db.Releases.SingleAsync(r => r.Mbid == Album, Token);

        Assert.Equal(10, release.TrackCount);
        Assert.Equal(10, await db.Tracks.CountAsync(t => t.ReleaseId == release.Id, Token));
        Assert.NotNull(release.ReleaseGroupId);

        var rows = await db.MediaFiles.ToListAsync(Token);

        Assert.All(rows, row =>
        {
            Assert.Equal(release.Id, row.ReleaseId);
            Assert.NotNull(row.TrackId);
            Assert.NotNull(row.ReleaseGroupId);
            Assert.NotNull(row.ReleaseDecidedUtc);
            Assert.Equal(ReleaseAttributionOutcome.AttributedByPerson, row.AttributionOutcome);
        });

        Assert.Empty(await QuestionsAsync(factory));
    }

    /// <summary>
    /// Files the chosen album does not list keep their refusal.
    /// </summary>
    /// <remarks>
    /// A component is a set that shared a <i>candidate set</i>, which is not a
    /// promise that every file in it came from one album — the worklist holds
    /// components carrying two rips at once. Stamping the remainder with a
    /// release that does not name them would be exactly the invention the pass
    /// refuses to make, so they stay where they were and the response says how
    /// many.
    /// </remarks>
    [Fact]
    public async Task FilesTheChosenAlbumDoesNotListStayOnTheWorklist()
    {
        // Eleven files: the album's ten, and one recording on neither release.
        await SeedComponentAsync(10, stray: true);

        using var factory = Factory();

        var decision = await DecideAsync(factory, new ComponentDecisionRequest("release", Album.Value));

        Assert.Equal(10, decision.Decided);
        Assert.Equal(1, decision.StillOpen);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var open = await db.MediaFiles
            .Where(file => file.ReleaseDecidedUtc == null)
            .ToListAsync(Token);

        var stray = Assert.Single(open);

        Assert.Equal(ReleaseAttributionOutcome.NoConfidentFit, stray.AttributionOutcome);
        Assert.Equal(Refused, stray.ReleaseLookupUtc);
        Assert.Null(stray.ReleaseId);
    }

    /// <summary>
    /// "None of these" closes the question without inventing an album.
    /// </summary>
    /// <remarks>
    /// A stronger claim than the rule is in a position to make: the pass refused
    /// because nothing cleared its gates, and this is somebody who looked at the
    /// candidates saying so. <see cref="ReleaseAttributionOutcome.NoReleaseByPerson"/>
    /// keeps the two distinguishable forever.
    /// </remarks>
    [Fact]
    public async Task NoneOfTheseClosesTheQuestionWithoutAnAlbum()
    {
        await SeedComponentAsync(10);

        using var factory = Factory();

        var decision = await DecideAsync(factory, new ComponentDecisionRequest("none", null));

        Assert.Equal(10, decision.Decided);
        Assert.Null(decision.Release);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var rows = await db.MediaFiles.ToListAsync(Token);

        Assert.All(rows, row =>
        {
            Assert.Equal(ReleaseAttributionOutcome.NoReleaseByPerson, row.AttributionOutcome);
            Assert.NotNull(row.ReleaseDecidedUtc);
            Assert.Null(row.ReleaseId);
        });

        Assert.Empty(await db.Releases.ToListAsync(Token));
        Assert.Empty(await QuestionsAsync(factory));
    }

    /// <summary>
    /// An answered component survives the documented way of re-asking the library.
    /// </summary>
    /// <remarks>
    /// <c>UPDATE "MediaFiles" SET "ReleaseLookupUtc" = NULL</c> is how attribution
    /// is re-run after a rule change, and without a separate guard that one
    /// statement would hand every answered component back to the rule that could
    /// not answer it — overwriting a person's decision with the same refusal they
    /// were answering, and leaving no trace that it had.
    /// </remarks>
    [Fact]
    public async Task ClearingTheWorklistStampDoesNotReopenAPersonsAnswer()
    {
        await SeedComponentAsync(10);

        using var factory = Factory();

        await DecideAsync(factory, new ComponentDecisionRequest("release", Album.Value));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"MediaFiles\" SET \"ReleaseLookupUtc\" = NULL", Token);

        var pending = await db.MediaFiles
            .Where(file => file.RecordingId != null
                && file.ReleaseLookupUtc == null
                && file.ReleaseDecidedUtc == null)
            .CountAsync(Token);

        Assert.Equal(0, pending);
    }

    /// <summary>
    /// Answering a second component with an album already in the catalogue keeps the first
    /// component's track links.
    /// </summary>
    /// <remarks>
    /// <b>The failure is silent and it survived a code read.</b>
    /// <c>ReleaseWriter.ApplyTracksAsync</c> replaces a release's track list, and
    /// <c>MediaFiles.TrackId</c> is <c>ON DELETE SET NULL</c> — so deleting the
    /// rows and minting new ids strips the position off every file already filed
    /// under that release, leaving the release and the group intact and the
    /// track link gone. Nothing errors and nothing logs. What breaks is "you are
    /// missing track 7", which is the entire reason the whole track list is
    /// written down.
    ///
    /// Two components rather than one because that is how it is reached: a pass
    /// builds a writer per component, and a person answering two questions with
    /// one album does the same thing in two requests.
    /// </remarks>
    [Fact]
    public async Task DecidingASecondComponentKeepsTheFirstComponentsTrackLinks()
    {
        await SeedComponentAsync(5);
        await SeedComponentAsync(5, from: 5, refused: Later);

        using var factory = Factory();

        await DecideAsync(factory, new ComponentDecisionRequest("release", Album.Value));
        await DecideAsync(factory, new ComponentDecisionRequest("release", Album.Value), Later);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var rows = await db.MediaFiles.ToListAsync(Token);

        Assert.Equal(10, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.TrackId));

        // Still one track list, not two: the second write updated the rows the
        // first one made rather than adding beside them.
        var release = await db.Releases.SingleAsync(r => r.Mbid == Album, Token);

        Assert.Equal(10, await db.Tracks.CountAsync(t => t.ReleaseId == release.Id, Token));
    }

    /// <summary>
    /// A real album that lists none of these recordings is refused, not written.
    /// </summary>
    /// <remarks>
    /// The failure mode without this is silent: the writer would mint the
    /// release, its group and forty tracks, link nothing to any of them and
    /// report a decision. A catalogue growing albums nobody owns, one bad click
    /// at a time.
    /// </remarks>
    [Fact]
    public async Task AnAlbumThatExplainsNothingIsRefused()
    {
        await SeedComponentAsync(10);

        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Decision(), new ComponentDecisionRequest("release", Elsewhere.Value), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Empty(await db.Releases.ToListAsync(Token));
        Assert.Equal(10, await db.MediaFiles.CountAsync(f => f.ReleaseDecidedUtc == null, Token));
    }

    private static Uri Candidates(DateTimeOffset? component = null) =>
        new(
            $"/api/catalogue/matching/components/{(component ?? Refused).UtcTicks}/candidates",
            UriKind.Relative);

    private static Uri Decision(DateTimeOffset? component = null) =>
        new(
            $"/api/catalogue/matching/components/{(component ?? Refused).UtcTicks}/decision",
            UriKind.Relative);

    private static async Task<ComponentCandidatesResponse> CandidatesAsync(
        WebApplicationFactory<Program> factory,
        bool refresh = false,
        DateTimeOffset? component = null)
    {
        using var client = factory.CreateClient();

        var url = Candidates(component);

        var candidates = await client.GetFromJsonAsync<ComponentCandidatesResponse>(
            refresh ? new Uri($"{url}?refresh=true", UriKind.Relative) : url,
            Token);

        Assert.NotNull(candidates);
        return candidates;
    }

    /// <summary>Blocks until the attribution pass has finished, or fails the test.</summary>
    private static async Task WaitForAttributionAsync(HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<AttributionStatusResponse>(
                new Uri("/api/library/attribute", UriKind.Relative), Token);

            if (status is { Running: false, LastCompleted: not null }) return;

            await Task.Delay(25, Token);
        }

        Assert.Fail("The attribution pass did not finish.");
    }

    private static async Task<ComponentDecisionResponse> DecideAsync(
        WebApplicationFactory<Program> factory,
        ComponentDecisionRequest request,
        DateTimeOffset? component = null)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Decision(component), request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var decision = await response.Content.ReadFromJsonAsync<ComponentDecisionResponse>(Token);

        Assert.NotNull(decision);
        return decision;
    }

    private static async Task<IReadOnlyList<OpenQuestion>> QuestionsAsync(
        WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();

        var queue = await client.GetFromJsonAsync<MatchingQueueResponse>(
            new Uri("/api/catalogue/matching", UriKind.Relative), Token);

        Assert.NotNull(queue);
        return queue.Items;
    }

    private WebApplicationFactory<Program> Factory() => Factory(new StubCatalogue());

    /// <summary>
    /// The same host with MusicBrainz replaced by a counting stub.
    /// </summary>
    /// <remarks>
    /// The stub is passed in rather than built here so a test can hold onto it
    /// and read its call counts afterwards, which is the only honest way to
    /// assert a cache — "it was faster" is not a test.
    /// </remarks>
    private WebApplicationFactory<Program> Factory(StubCatalogue catalogue) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", Path.GetTempPath());

            // The background warmer would put its own questions to the providers,
            // out of a thread nothing here waits for.
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IMusicBrainzCatalogue>(catalogue);
            });
        });

    /// <summary>The recording MBID of the album's nth track. Deterministic, so the stub agrees.</summary>
    private static Mbid Recording(int index) =>
        new(new Guid($"d4444444-0000-4000-8000-{index:D12}"));

    /// <summary>The nth track's true length. Distinct per track, so drift means something.</summary>
    private static TimeSpan Length(int index) => TimeSpan.FromSeconds(180 + (index * 7));

    /// <summary>
    /// One refused component: <paramref name="tracks"/> files sharing one stamp.
    /// </summary>
    /// <remarks>
    /// No files on disk and no fingerprints. Attribution decides from
    /// <c>FingerprintDuration</c> and the recording link, both of which are
    /// catalogue columns by the time this pass runs.
    /// </remarks>
    private async Task SeedComponentAsync(
        int tracks,
        bool stray = false,
        int from = 0,
        DateTimeOffset? refused = null,
        bool attributed = true,
        TimeSpan drift = default)
    {
        var stamp = refused ?? Refused;

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(Token);

        for (var index = from; index < from + tracks + (stray ? 1 : 0); index++)
        {
            var recording = new Recording
            {
                Id = RecordingId.New(),
                Title = $"Track {index + 1}",
                Mbid = Recording(index),
                Duration = Length(index),
            };

            db.Recordings.Add(recording);

            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = $"Album/{index + 1:D2}.flac",
                SizeBytes = 30_000_000,
                LastModifiedUtc = stamp,
                RecordingId = recording.Id,
                RecordingLookupUtc = stamp,
                EnrichmentOutcome = EnrichmentOutcome.Linked,
                AcoustIdOutcome = AcoustIdOutcome.Identified,
                AcoustIdCheckedUtc = stamp,
                FingerprintDuration = Length(index) + drift,

                // `attributed: false` leaves the file on the attribution pass's
                // own worklist, which is what a test of the pass needs. The
                // stamp is what the pass writes, so it cannot be seeded and the
                // component's identity is not known until the run is over.
                ReleaseLookupUtc = attributed ? stamp : null,
                AttributionOutcome = attributed
                    ? ReleaseAttributionOutcome.NoConfidentFit
                    : ReleaseAttributionOutcome.NotAttempted,
            });
        }

        await db.SaveChangesAsync(Token);
    }

    /// <summary>
    /// Two releases over one set of recordings: the album, and the box set reprinting it.
    /// </summary>
    /// <remarks>
    /// Both are returned by every browse, so nothing about the order recordings
    /// are asked in can decide the answer — which is what makes the ranking
    /// assertion about the rule rather than about the fixture.
    /// </remarks>
    private sealed class StubCatalogue : IMusicBrainzCatalogue
    {
        private const int AlbumTracks = 10;
        private const int BoxTracks = 40;

        /// <summary>Browses that reached it — one per recording asked about.</summary>
        public int Browses { get; private set; }

        /// <summary>Release lookups that reached it — the track lists, the heavy half.</summary>
        public int Lookups { get; private set; }

        public Task<MusicBrainzRecording?> GetRecordingAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzRecording?>(null);

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(
            Mbid recording,
            CancellationToken cancellationToken = default)
        {
            Browses++;

            return Task.FromResult<IReadOnlyList<MusicBrainzReleaseCandidate>>(
            [
                Summary(Album, "Off the Wall", AlbumTracks),
                Summary(BoxSet, "The Ultimate Collection", BoxTracks),
            ]);
        }

        public Task<MusicBrainzRelease?> GetReleaseAsync(
            Mbid id,
            CancellationToken cancellationToken = default)
        {
            Lookups++;

            if (id == Album) return Task.FromResult<MusicBrainzRelease?>(Release(Album, "Off the Wall", AlbumTracks));
            if (id == BoxSet) return Task.FromResult<MusicBrainzRelease?>(Release(BoxSet, "The Ultimate Collection", BoxTracks));

            // Never returned by a browse, so it is only reachable by a caller
            // naming it — which is the case under test.
            if (id == Elsewhere) return Task.FromResult<MusicBrainzRelease?>(Release(Elsewhere, "Something Else", 5));

            return Task.FromResult<MusicBrainzRelease?>(null);
        }

        public Task<MusicBrainzWork?> GetWorkAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzWork?>(null);

        private static MusicBrainzReleaseCandidate Summary(Mbid id, string title, int tracks) =>
            new(id, title, new ReleaseDate(1979, null, null), "US", "Official", null,
                id == Album ? AlbumGroup : null, title, "Album", [],
                [new MusicBrainzMediumSummary(1, "CD", tracks)]);

        /// <summary>
        /// The album's ten tracks, then thirty the library does not hold.
        /// </summary>
        /// <remarks>
        /// The first ten are the same recordings in both releases and print the
        /// same lengths, so the two fits differ in coverage and in nothing else.
        /// That is the documented failure reduced to a fixture.
        /// </remarks>
        private static MusicBrainzRelease Release(Mbid id, string title, int tracks) =>
            new(id, title, new ReleaseDate(1979, null, null), "US", "Official", null, [],
                id == Album ? AlbumGroup : null, title, "Album", [], [],
                [.. Enumerable.Range(0, tracks).Select(index => new MusicBrainzTrack(
                    1,
                    index + 1,
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    $"Track {index + 1}",
                    Length(index),
                    id != Elsewhere && index < AlbumTracks ? Recording(index) : Foreign(index),
                    []))]);

        /// <summary>A recording only the box set holds. Never in the library.</summary>
        private static Mbid Foreign(int index) =>
            new(new Guid($"f6666666-0000-4000-8000-{index:D12}"));
    }
}
