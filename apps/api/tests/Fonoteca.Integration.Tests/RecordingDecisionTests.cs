using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Api.Matching;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// A person answering an identification question, through the real host.
/// </summary>
/// <remarks>
/// The other half of <see cref="MatchingEndpointTests"/>, and its own class for
/// the same reason that one is: this seed is a library that is about to change,
/// and every assertion in there is about counts over a library that does not.
///
/// AcoustID and MusicBrainz are stubbed; everything else is real — a real FLAC
/// from <see cref="Corpus"/>, real ATL and TagLib# through
/// <c>AcoustIdTagWriter</c>, real rows in real PostgreSQL. The point is what the
/// application does with an answer, and three parts of that are worth more than
/// the rest:
///
/// <list type="bullet">
/// <item><b>The cluster is resolved, not accepted.</b> The caller sends a
/// recording; the AcoustID written into the file's bytes is the one a live
/// lookup links to that recording, which is <i>not</i> the top-scoring cluster
/// in general. <see cref="TheClusterWrittenIsTheOneThatNamesTheChosenRecording"/>
/// is the test that would catch a client being trusted with it.</item>
/// <item><b>A rejection is an answer.</b> It has to close the question, mark the
/// file as a person's, and open no file — and it must not be reachable by
/// accident, which is what <see cref="ABodyWithNoAnswerIsNotARejection"/> pins:
/// the difference between a client bug and a recorded human decision.</item>
/// <item><b>A decided file is out of reach of the passes.</b> Clearing
/// <c>AcoustIdCheckedUtc</c> by hand is the documented way to re-ask a library
/// after a rule change, and that same UPDATE must not sweep up the answers
/// somebody gave.</item>
/// </list>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class RecordingDecisionTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The recording a person picks. Not the one the top cluster names.</summary>
    private static readonly Mbid Chosen = new(new Guid("aaaaaaaa-1111-4111-8111-111111111111"));

    /// <summary>What the highest-scoring cluster names instead.</summary>
    private static readonly Mbid Rival = new(new Guid("bbbbbbbb-2222-4222-8222-222222222222"));

    /// <summary>The cluster that names <see cref="Chosen"/>, and the one that must be written.</summary>
    private static readonly Guid ChosenCluster = new("cccccccc-3333-4333-8333-333333333333");

    /// <summary>The cluster that scores higher and names something else.</summary>
    private static readonly Guid RivalCluster = new("dddddddd-4444-4444-8444-444444444444");

    private string _connectionString = string.Empty;
    private string _root = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-decision-").FullName;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The whole flow: the catalogue is decided, the file is tagged, the question is gone.
    /// </summary>
    /// <remarks>
    /// All three matter and they are three separate facts. A decision that
    /// changed only the row would leave the file's tags contradicting the
    /// catalogue forever; one that changed only the file would be undone by the
    /// next pass; and one that did both without closing the question would put
    /// the same file back in front of a person who had already answered it.
    ///
    /// The size and timestamp assertion is the identification pass's sharpest
    /// edge reached by a second route — a tag write changes the bytes, and a
    /// catalogue still holding the old facts reads that as "modified" on the
    /// next scan and discards everything derived from them, including the
    /// AcoustID just written.
    /// </remarks>
    [Fact]
    public async Task ChoosingARecordingLinksItTagsTheFileAndClosesTheQuestion()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/07 Ambiguous.flac");

        await using var factory = FactoryWith(allowMutation: true);

        var decision = await DecideAsync(factory, id, Answer(Chosen));

        Assert.Equal(nameof(AcoustIdOutcome.IdentifiedByPerson), decision.Outcome);
        Assert.Equal(nameof(EnrichmentOutcome.Linked), decision.Enrichment);
        Assert.Equal(Chosen.Value, decision.Recording);
        Assert.Equal(ChosenCluster, decision.AcoustId);
        Assert.Equal(nameof(TagWriteStatus.Written), decision.Tag);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var row = await db.MediaFiles
            .Include(file => file.Recording)
            .SingleAsync(file => file.Id == id, Token);

        Assert.NotNull(row.RecordingId);
        Assert.Equal(Chosen, row.Recording!.Mbid);
        Assert.Equal(new AcoustId(ChosenCluster), row.AcoustId);
        Assert.Equal(AcoustIdOutcome.IdentifiedByPerson, row.AcoustIdOutcome);
        Assert.Equal(EnrichmentOutcome.Linked, row.EnrichmentOutcome);
        Assert.NotNull(row.IdentityDecidedUtc);
        Assert.NotNull(row.RecordingLookupUtc);
        Assert.NotNull(row.AcoustIdTaggedUtc);

        // The file really says so, read back by the library that did not write it.
        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        Assert.Equal(
            ChosenCluster.ToString("D"),
            await reader.ReadAcoustIdAsync(new LibraryPath(row.Path), Token),
            ignoreCase: true);

        // And the catalogue agrees with the filesystem about the bytes that
        // changed, so the next scan sees an unchanged file.
        var onDisk = new FileInfo(Path.Combine(_root, row.Path));
        Assert.Equal(onDisk.Length, row.SizeBytes);
        Assert.Null(row.ContentHash);

        // Gone from the worklist, which is the only part a person sees.
        Assert.DoesNotContain(await QuestionsAsync(factory), question => question.Id == $"recording:{id.Value}");
    }

    /// <summary>
    /// The cluster written is the one that names the chosen recording, not the best-scoring one.
    /// </summary>
    /// <remarks>
    /// The reason this endpoint spends a turn at AcoustID's rate limit on a
    /// click. The candidate list a person chooses from names <i>recordings</i>,
    /// and the thing written into the file's bytes is a <i>cluster</i> — so
    /// something has to link the two, and the only honest place for it is here.
    /// A naive implementation reads <c>results[0]</c>, which is exactly how a
    /// remaster gets tagged as the original.
    ///
    /// The stub answers 0.98 for the rival and 0.91 for the right one, so any
    /// implementation that ranks by score and ignores the answer fails.
    /// </remarks>
    [Fact]
    public async Task TheClusterWrittenIsTheOneThatNamesTheChosenRecording()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/08 Two clusters.flac");

        await using var factory = FactoryWith(allowMutation: true);

        var decision = await DecideAsync(factory, id, Answer(Chosen));

        Assert.Equal(ChosenCluster, decision.AcoustId);
        Assert.NotEqual(RivalCluster, decision.AcoustId);

        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        Assert.Equal(
            ChosenCluster.ToString("D"),
            await reader.ReadAcoustIdAsync(new LibraryPath("Bootlegs/08 Two clusters.flac"), Token),
            ignoreCase: true);
    }

    /// <summary>
    /// "None of these" closes the question, and opens no file.
    /// </summary>
    /// <remarks>
    /// A rejection is a claim about the music — somebody listened and none of
    /// the candidates is it — so it is recorded as an outcome of its own rather
    /// than folded into <see cref="AcoustIdOutcome.Unknown"/>, which means
    /// AcoustID never heard the audio. What it must not do is touch the file:
    /// there is no identity to write, and a decision that rewrote 30 MB to
    /// record an absence would be the worst kind of surprise.
    /// </remarks>
    [Fact]
    public async Task RejectingEveryCandidateIsAnAnswerAndTouchesNoFile()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/09 None of them.flac");

        var before = new FileInfo(Path.Combine(_root, "Bootlegs/09 None of them.flac"));
        var bytes = before.Length;
        var modified = before.LastWriteTimeUtc;

        await using var factory = FactoryWith(allowMutation: true);

        var decision = await DecideAsync(factory, id, Reject());

        Assert.Equal(nameof(AcoustIdOutcome.RejectedByPerson), decision.Outcome);
        Assert.Null(decision.Recording);
        Assert.Null(decision.AcoustId);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

        Assert.Equal(AcoustIdOutcome.RejectedByPerson, row.AcoustIdOutcome);
        Assert.NotNull(row.IdentityDecidedUtc);
        Assert.Null(row.RecordingId);
        Assert.Null(row.AcoustId);

        var after = new FileInfo(Path.Combine(_root, "Bootlegs/09 None of them.flac"));
        Assert.Equal(bytes, after.Length);
        Assert.Equal(modified, after.LastWriteTimeUtc);

        Assert.DoesNotContain(await QuestionsAsync(factory), question => question.Id == $"recording:{id.Value}");
    }

    /// <summary>
    /// A body with no answer is a client bug, not a rejection.
    /// </summary>
    /// <remarks>
    /// The whole reason <c>answer</c> exists as a field rather than being
    /// inferred from a missing <c>recording</c>. Inferred, an empty body — a
    /// forgotten field, a serialiser that dropped a null — would be recorded as
    /// a person deciding the audio is none of the candidates, which then outranks
    /// every pass and can only be undone by hand. The row has to come out
    /// untouched.
    /// </remarks>
    [Fact]
    public async Task ABodyWithNoAnswerIsNotARejection()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/10 Empty body.flac");

        await using var factory = FactoryWith(allowMutation: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Decision(id), new Dictionary<string, string?>(StringComparer.Ordinal), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

        Assert.Equal(AcoustIdOutcome.Ambiguous, row.AcoustIdOutcome);
        Assert.Null(row.IdentityDecidedUtc);
    }

    /// <summary>
    /// With mutation off the decision still stands, and the file is untouched.
    /// </summary>
    /// <remarks>
    /// <c>Fonoteca:AllowFileMutation</c> is false by default, so this is the
    /// ordinary path rather than an edge case. Everything except the write
    /// happens — the recording is linked, the cluster is resolved and stored,
    /// the question closes — and <c>AcoustIdTaggedUtc</c> stays null, which is
    /// exactly the state <c>IX_MediaFiles_AcoustIdUntagged</c> exists to find.
    /// Flipping the flag later tags the file and spends no lookup.
    ///
    /// Reported as <c>Refused</c> rather than as an error, because it is not
    /// one: a screen that shouted here would teach somebody to ignore the one
    /// message that means a write actually failed.
    /// </remarks>
    [Fact]
    public async Task WithMutationOffTheCatalogueIsDecidedAndTheBytesAreNot()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/11 Dry run.flac");

        var before = new FileInfo(Path.Combine(_root, "Bootlegs/11 Dry run.flac")).LastWriteTimeUtc;

        await using var factory = FactoryWith(allowMutation: false);

        var decision = await DecideAsync(factory, id, Answer(Chosen));

        Assert.Equal(nameof(TagWriteStatus.Refused), decision.Tag);
        Assert.Equal(ChosenCluster, decision.AcoustId);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

        Assert.Equal(new AcoustId(ChosenCluster), row.AcoustId);
        Assert.NotNull(row.RecordingId);
        Assert.NotNull(row.IdentityDecidedUtc);
        Assert.Null(row.AcoustIdTaggedUtc);

        Assert.Equal(
            before,
            new FileInfo(Path.Combine(_root, "Bootlegs/11 Dry run.flac")).LastWriteTimeUtc);
    }

    /// <summary>
    /// A decided file is out of reach of the identification worklist.
    /// </summary>
    /// <remarks>
    /// The guard <c>IdentityDecidedUtc</c> exists for. Every worklist in this
    /// application is a timestamp being null, and clearing
    /// <c>AcoustIdCheckedUtc</c> by hand is the documented way to re-ask a whole
    /// library after a rule change — which is right for an answer a rule
    /// produced and wrong for one a person produced. Without the guard that
    /// UPDATE hands every answered file back to the rule that could not answer
    /// it, and nothing says so.
    /// </remarks>
    [Fact]
    public async Task ReopeningTheWorklistDoesNotReopenAnAnsweredFile()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/12 Answered.flac");

        await using var factory = FactoryWith(allowMutation: false);
        await DecideAsync(factory, id, Answer(Chosen));

        // The documented re-ask, by hand, exactly as it would be run against a
        // live database after a rule change.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.MediaFiles
                .Where(file => file.Id == id)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(file => file.AcoustIdCheckedUtc, (DateTimeOffset?)null)
                        .SetProperty(file => file.RecordingLookupUtc, (DateTimeOffset?)null),
                    Token);
        }

        using var scope = factory.Services.CreateScope();

        var identification = scope.ServiceProvider.GetRequiredService<IdentificationService>();
        var enrichment = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        Assert.Equal(0, await identification.CountPendingAsync(Token));

        // `.Files`, not the total: this test is about a file, and the decision
        // just wrote the artists behind its recording into a catalogue nobody
        // has described yet — which is a real question and not this one.
        Assert.Equal(0, (await enrichment.CountPendingAsync(Token)).Files);
    }

    /// <summary>
    /// An answer is refused while a pass holds the gate.
    /// </summary>
    /// <remarks>
    /// The same race <see cref="LibraryWorkGate"/> already exists to prevent,
    /// reached from a request rather than from a second pass: a scan that decides
    /// a file changed clears every derived column on it, and this sets several of
    /// them and rewrites the file's bytes underneath it. Refusing costs somebody
    /// one click; not refusing costs a decision that silently disappears.
    /// </remarks>
    [Fact]
    public async Task AnAnswerIsRefusedWhileAPassHoldsTheGate()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/13 Busy.flac");

        await using var factory = FactoryWith(allowMutation: false);
        using var client = factory.CreateClient();

        var gate = factory.Services.GetRequiredService<LibraryWorkGate>();
        Assert.True(gate.TryEnter(IdentificationService.JobKind, out var lease));

        using (lease)
        {
            var response = await client.PostAsJsonAsync(Decision(id), Answer(Chosen), Token);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

        Assert.Null(row.IdentityDecidedUtc);
    }

    /// <summary>
    /// A recording MusicBrainz no longer holds is a 404, and decides nothing.
    /// </summary>
    /// <remarks>
    /// The candidate list is recovered from a live lookup rather than read back,
    /// so it can be minutes old by the time somebody answers it and MusicBrainz
    /// merges recordings constantly. Writing the link anyway would put an MBID in
    /// the catalogue that resolves to nothing.
    /// </remarks>
    [Fact]
    public async Task ARecordingMusicBrainzHasLostIsNotFound()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/14 Merged away.flac");

        await using var factory = FactoryWith(allowMutation: false, knowsNothing: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Decision(id), Answer(Chosen), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

        Assert.Null(row.IdentityDecidedUtc);
        Assert.Null(row.RecordingId);
    }

    /// <summary>
    /// A provider outage leaves the question open rather than half-answered.
    /// </summary>
    /// <remarks>
    /// The lookup happens before anything is written, so an outage is a decision
    /// that was never taken rather than one that was lost. The same click works
    /// when AcoustID does.
    /// </remarks>
    [Fact]
    public async Task AnAcoustIdOutageLeavesTheQuestionOpen()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/15 Provider down.flac");

        await using var factory = FactoryWith(allowMutation: false, acoustIdDown: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Decision(id), Answer(Chosen), Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

        Assert.Equal(AcoustIdOutcome.Ambiguous, row.AcoustIdOutcome);
        Assert.Null(row.IdentityDecidedUtc);
        Assert.Null(row.RecordingId);
    }

    /// <summary>
    /// The decision is in the event log, with an actor and a date on it.
    /// </summary>
    /// <remarks>
    /// Separate from the tag write's undo entry, which answers a different
    /// question: that one records what the file's tags were before a byte
    /// changed so the write can be reversed, and this records that a person
    /// overrode a rule. Without it, "why does this file say it is decided when
    /// the pass refused it" has no answer at all — and a rejection, which writes
    /// no undo entry because nothing was opened, would leave no trace whatsoever.
    /// </remarks>
    [Fact]
    public async Task TheDecisionIsWrittenToTheEventLog()
    {
        SkipWithoutTools();

        var chosen = await SeedAsync("Bootlegs/16 Journalled.flac");
        var refused = await SeedAsync("Bootlegs/17 Journalled refusal.flac");

        await using var factory = FactoryWith(allowMutation: false);

        await DecideAsync(factory, chosen, Answer(Chosen));
        await DecideAsync(factory, refused, Reject());

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var forChosen = await db.DomainEvents
            .Where(entry => entry.SubjectId == chosen.Value.ToString())
            .ToListAsync(Token);

        var decided = Assert.Single(forChosen, entry => entry.Type == "matching.recording.decided");
        Assert.Contains(Chosen.Value.ToString(), decided.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SingleUserCallerContext.OwnerId, decided.ActorId);

        // The tag writer's own entry, beside it and not instead of it. Mutation
        // is off here, so it records a refusal — which is exactly the pair this
        // separation exists for: one entry says a person decided, the other says
        // what would have been written into the file.
        var refusal = Assert.Single(
            forChosen, entry => entry.Type == AcoustIdTagWriter.RefusedEventType);

        Assert.Equal(decided.CorrelationId, refusal.CorrelationId);

        var forRefused = await db.DomainEvents
            .Where(entry => entry.SubjectId == refused.Value.ToString())
            .ToListAsync(Token);

        // Exactly one, because a rejection opens no file: without the decision
        // entry a refusal would leave no trace in the log at all.
        var rejected = Assert.Single(forRefused);
        Assert.Equal("matching.recording.rejected", rejected.Type);
    }

    /// <summary>
    /// Opening the same question twice asks nobody the second time.
    /// </summary>
    /// <remarks>
    /// The behaviour the cache exists for, stated as a count of provider calls
    /// rather than as a timing. Before it, every open of one file cost an
    /// AcoustID turn plus a MusicBrainz recording lookup per candidate — the
    /// heaviest request this application makes, measured at 10.3 seconds cold —
    /// so returning to a question a second time cost as much as reaching it the
    /// first.
    /// </remarks>
    [Fact]
    public async Task OpeningTheSameQuestionTwiceAsksTheProvidersOnce()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/18 Twice.flac");

        var clusters = new StubClusters(unavailable: false);
        var catalogue = new StubCatalogue(knowsNothing: false);

        await using var factory = FactoryWith(allowMutation: false, clusters, catalogue);

        var first = await CandidatesAsync(factory, id);

        Assert.False(first.FromCache);
        Assert.Equal(1, clusters.Calls);
        Assert.Equal(2, catalogue.Calls);

        var second = await CandidatesAsync(factory, id);

        Assert.True(second.FromCache);
        Assert.Equal(1, clusters.Calls);
        Assert.Equal(2, catalogue.Calls);

        // Same evidence, not merely a same-shaped answer.
        Assert.Equal(first.AsOfUtc, second.AsOfUtc);
        Assert.Equal(
            first.Candidates.Select(row => row.Mbid),
            second.Candidates.Select(row => row.Mbid));
    }

    /// <summary>
    /// The identification pass leaves AcoustID's answer behind, so the first open is cheaper too.
    /// </summary>
    /// <remarks>
    /// "Right after scanning": the pass has the answer in its hand and used to
    /// throw it away, which is why finding out what 951 withheld files were cost
    /// 951 turns at the rate limit. Stored, the first person to open one of those
    /// questions pays for the MusicBrainz half only.
    /// </remarks>
    [Fact]
    public async Task ThePassStoresAcoustIdsAnswerSoTheFirstOpenDoesNotAskItAgain()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/19 Prefilled.flac");

        // What the identification pass would have left behind, in the shape it
        // leaves it in — written through the same serialiser the pass uses, so a
        // change to the stored document breaks this rather than passing on a
        // fixture that has drifted.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

            row.AcoustIdMatchesJson = AcoustIdEvidence.Serialise(StubClusters.Answer);
            row.AcoustIdMatchesUtc = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(Token);
        }

        var clusters = new StubClusters(unavailable: false);
        var catalogue = new StubCatalogue(knowsNothing: false);

        await using var factory = FactoryWith(allowMutation: false, clusters, catalogue);

        var candidates = await CandidatesAsync(factory, id);

        Assert.Equal(0, clusters.Calls);
        Assert.Equal(2, catalogue.Calls);
        Assert.Equal(2, candidates.Clusters.Count);
    }

    /// <summary>
    /// An answer older than a week is rebuilt, and `refresh` rebuilds one that is not.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they fail in opposite directions. A cache that
    /// never expires serves a person a year-old reading of two databases that
    /// move every day; one with no way to force a refresh leaves them with no
    /// answer at all when they have reason to believe it has changed.
    /// </remarks>
    [Fact]
    public async Task AWeekOldAnswerIsRebuiltAndRefreshRebuildsAFreshOne()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/20 Stale.flac");

        var clusters = new StubClusters(unavailable: false);
        var catalogue = new StubCatalogue(knowsNothing: false);

        await using var factory = FactoryWith(allowMutation: false, clusters, catalogue);

        await CandidatesAsync(factory, id);
        Assert.Equal(1, clusters.Calls);

        // Forced rather than waited for: the age is what is under test, and a
        // clock this test controlled would be testing the substitution instead.
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.MediaFiles
                .Where(file => file.Id == id)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(file => file.AcoustIdMatchesUtc, longAgo)
                        .SetProperty(file => file.RecordingCandidatesUtc, longAgo),
                    Token);
        }

        var rebuilt = await CandidatesAsync(factory, id);

        Assert.False(rebuilt.FromCache);
        Assert.Equal(2, clusters.Calls);

        // And now fresh again, so only `refresh` gets past it.
        Assert.True((await CandidatesAsync(factory, id)).FromCache);
        Assert.Equal(2, clusters.Calls);

        var forced = await CandidatesAsync(factory, id, refresh: true);

        Assert.False(forced.FromCache);
        Assert.Equal(3, clusters.Calls);
    }

    /// <summary>
    /// Committing after reading the candidates costs no further AcoustID turn.
    /// </summary>
    /// <remarks>
    /// The decision resolves the cluster from the server's own evidence rather
    /// than from the request body, and the evidence it uses is the one the
    /// person was just shown. That is not a weaker check than asking again —
    /// neither reading is the caller's — and it takes a click off the rate
    /// limit.
    /// </remarks>
    [Fact]
    public async Task CommittingReusesTheEvidenceTheChooserWasBuiltFrom()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/21 Commit from cache.flac");

        var clusters = new StubClusters(unavailable: false);
        var catalogue = new StubCatalogue(knowsNothing: false);

        await using var factory = FactoryWith(allowMutation: false, clusters, catalogue);

        await CandidatesAsync(factory, id);
        Assert.Equal(1, clusters.Calls);

        var decision = await DecideAsync(factory, id, Answer(Chosen));

        Assert.Equal(ChosenCluster, decision.AcoustId);
        Assert.Equal(1, clusters.Calls);
    }

    /// <summary>
    /// Replacing the bytes takes the decision and the evidence with them.
    /// </summary>
    /// <remarks>
    /// Two failures in one, and the second is the dangerous one.
    ///
    /// A person decided what <i>those</i> bytes were. The scan resets
    /// <c>AcoustIdOutcome</c> to <c>NotAttempted</c>, which the worklist
    /// deliberately does not count as a question, and both passes exclude
    /// <c>IdentityDecidedUtc</c> — so a file that kept the stamp through a
    /// replacement would be invisible to every pass and to the screen at once,
    /// with nothing in the application able to reach it again.
    ///
    /// And the cached candidate set is believed for a week, checked <i>before</i>
    /// the endpoint checks for a fingerprint. Left behind, opening the replaced
    /// file would offer the recordings the previous audio matched, and committing
    /// one would write that cluster into the new bytes — which is exactly what
    /// clearing the AcoustID is there to prevent, reached by another route.
    /// </remarks>
    [Fact]
    public async Task ReplacingTheAudioClearsBothTheDecisionAndTheEvidence()
    {
        SkipWithoutTools();

        const string Path = "Bootlegs/22 Replaced.flac";

        var id = await SeedAsync(Path);

        await using var factory = FactoryWith(allowMutation: false);

        await CandidatesAsync(factory, id);
        await DecideAsync(factory, id, Answer(Chosen));

        await using (var decided = PostgresFixture.CreateContext(_connectionString))
        {
            var before = await decided.MediaFiles.SingleAsync(file => file.Id == id, Token);

            Assert.NotNull(before.IdentityDecidedUtc);
            Assert.NotNull(before.AcoustIdMatchesJson);
            Assert.NotNull(before.RecordingCandidatesJson);
        }

        // Something other than this application changes the file. Different
        // bytes and a different length, which is what a scan reacts to.
        System.IO.File.Copy(Corpus.Mp3, System.IO.Path.Combine(_root, Path), overwrite: true);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryScanService>()
                .ScanAsync(Token);
        }

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

        Assert.Null(row.IdentityDecidedUtc);
        Assert.Null(row.AcoustIdMatchesJson);
        Assert.Null(row.AcoustIdMatchesUtc);
        Assert.Null(row.RecordingCandidatesJson);
        Assert.Null(row.RecordingCandidatesUtc);

        // And so it is back in front of the passes rather than stranded between
        // them. That is the whole reason the stamp had to go.
        using var after = factory.Services.CreateScope();

        Assert.Equal(
            1,
            await after.ServiceProvider.GetRequiredService<IdentificationService>()
                .CountPendingAsync(Token));
    }

    /// <summary>
    /// A scan over an untouched file leaves the decision and the evidence alone.
    /// </summary>
    /// <remarks>
    /// The other half of
    /// <see cref="ReplacingTheAudioClearsBothTheDecisionAndTheEvidence"/>, and
    /// the more important half now. Clearing on changed bytes is right, but it
    /// made the scan's size-and-mtime bookkeeping load-bearing for <i>human</i>
    /// work: before, a file that wrongly compared as modified cost a wasted
    /// re-identify, and now it silently discards somebody's answer.
    ///
    /// The tag write is what makes this delicate rather than obvious. Committing
    /// a decision rewrites the file, so the row's stored facts have to be updated
    /// in the same transaction — the loop the identification pass documents,
    /// reached from a request. Get that wrong and the very next scan throws away
    /// the decision that caused the write.
    /// </remarks>
    [Fact]
    public async Task AScanOverTheSameFileLeavesTheDecisionStanding()
    {
        SkipWithoutTools();

        const string Path = "Bootlegs/24 Untouched.flac";

        var id = await SeedAsync(Path);

        // Mutation on, so the decision really does rewrite the file — which is
        // the case that could make the next scan think it changed.
        await using var factory = FactoryWith(allowMutation: true);

        await CandidatesAsync(factory, id);

        var decision = await DecideAsync(factory, id, Answer(Chosen));

        Assert.Equal(nameof(TagWriteStatus.Written), decision.Tag);

        using (var scope = factory.Services.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<LibraryScanService>()
                .ScanAsync(Token);

            Assert.Equal(LibraryScanStatus.Completed, outcome.Status);
            Assert.Equal(0, outcome.Summary!.Updated);
        }

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

        Assert.NotNull(row.IdentityDecidedUtc);
        Assert.NotNull(row.AcoustIdMatchesJson);
        Assert.NotNull(row.RecordingCandidatesJson);
        Assert.NotNull(row.RecordingId);
        Assert.Equal(AcoustIdOutcome.IdentifiedByPerson, row.AcoustIdOutcome);
        Assert.Equal(new AcoustId(ChosenCluster), row.AcoustId);
    }

    /// <summary>
    /// A decision is refused while a pass runs, and holds the gate while it runs itself.
    /// </summary>
    /// <remarks>
    /// Both directions, because only one of them was ever the point. Reading
    /// <c>ActiveKind</c> and carrying on would refuse a decision that arrived
    /// second and do nothing at all about a <i>pass</i> that arrived second —
    /// and the second case is the one that costs something, since the handler
    /// goes on to spend seconds on two lookups and a tag write while the pass
    /// works on the same row.
    ///
    /// The gate is entered here as the identification pass, using that pass's
    /// own <c>JobKind</c>, because that is a holder this actually excludes. A
    /// scan is not: <c>LibraryScanService</c> guards itself with a private flag
    /// and has never taken this gate, so entering with a scan's label would be a
    /// test asserting an exclusion the application does not have.
    /// </remarks>
    [Fact]
    public async Task ADecisionHoldsTheGateRatherThanMerelyCheckingIt()
    {
        SkipWithoutTools();

        var id = await SeedAsync("Bootlegs/23 Gate held.flac");

        await using var factory = FactoryWith(allowMutation: false);
        var gate = factory.Services.GetRequiredService<LibraryWorkGate>();

        // A pass is running: the decision is refused.
        Assert.True(gate.TryEnter(IdentificationService.JobKind, out var lease));

        using (lease)
        {
            using var client = factory.CreateClient();

            var refused = await client.PostAsJsonAsync(Decision(id), Answer(Chosen), Token);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        // Nothing is running: the decision goes through, and the gate is free
        // again afterwards rather than leaked.
        await DecideAsync(factory, id, Answer(Chosen));

        Assert.Null(gate.ActiveKind);
        Assert.True(gate.TryEnter(IdentificationService.JobKind, out var after));
        after.Dispose();
    }

    [Fact]
    public async Task AFileTheCatalogueDoesNotHoldIsNotFound()
    {
        await using var factory = FactoryWith(allowMutation: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri(
                $"/api/catalogue/matching/recordings/{Guid.CreateVersion7()}/decision",
                UriKind.Relative),
            Answer(Chosen),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<RecordingCandidatesResponse> CandidatesAsync(
        WebApplicationFactory<Program> factory,
        MediaFileId id,
        bool refresh = false)
    {
        using var client = factory.CreateClient();

        var query = refresh ? "?refresh=true" : string.Empty;

        var candidates = await client.GetFromJsonAsync<RecordingCandidatesResponse>(
            new Uri(
                $"/api/catalogue/matching/recordings/{id.Value}/candidates{query}",
                UriKind.Relative),
            Token);

        Assert.NotNull(candidates);
        return candidates;
    }

    private static RecordingDecisionRequest Answer(Mbid recording) =>
        new(RecordingDecisionRequest.ChoseRecording, recording.Value);

    private static RecordingDecisionRequest Reject() =>
        new(RecordingDecisionRequest.ChoseNone, null);

    private static Uri Decision(MediaFileId id) =>
        new($"/api/catalogue/matching/recordings/{id.Value}/decision", UriKind.Relative);

    private static async Task<RecordingDecisionResponse> DecideAsync(
        WebApplicationFactory<Program> factory,
        MediaFileId id,
        RecordingDecisionRequest request)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Decision(id), request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var decision = await response.Content.ReadFromJsonAsync<RecordingDecisionResponse>(Token);

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

    /// <summary>
    /// One ambiguous file, on disk and in the catalogue.
    /// </summary>
    /// <remarks>
    /// A real FLAC rather than a placeholder, because the tag write is half of
    /// what is under test and two tag libraries have to agree about the bytes
    /// before it commits.
    /// </remarks>
    private async Task<MediaFileId> SeedAsync(string path)
    {
        var destination = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        System.IO.File.Copy(Corpus.Flac, destination, overwrite: true);

        var facts = new FileInfo(destination);

        var row = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = path,
            SizeBytes = facts.Length,
            LastModifiedUtc = StoreTime.ToStorePrecision(facts.LastWriteTimeUtc),
            Fingerprint = "AQAAmockfingerprint",
            FingerprintDuration = TimeSpan.FromSeconds(Corpus.DurationSeconds),
            AcoustIdOutcome = AcoustIdOutcome.Ambiguous,
            AcoustIdCheckedUtc =
                DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
        };

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(Token);

        db.MediaFiles.Add(row);
        await db.SaveChangesAsync(Token);

        return row.Id;
    }

    private WebApplicationFactory<Program> FactoryWith(
        bool allowMutation,
        bool knowsNothing = false,
        bool acoustIdDown = false) =>
        FactoryWith(allowMutation, new StubClusters(acoustIdDown), new StubCatalogue(knowsNothing));

    /// <summary>
    /// The same host with the two providers replaced by counting stubs.
    /// </summary>
    /// <remarks>
    /// The stubs are passed in rather than built here so a test can hold onto
    /// them and read their call counts afterwards — which is the only honest way
    /// to assert a cache, since "it was faster" is not a test.
    /// </remarks>
    private WebApplicationFactory<Program> FactoryWith(
        bool allowMutation,
        StubClusters clusters,
        StubCatalogue catalogue) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);

            // The background warmer would put its own questions to the providers,
            // out of a thread nothing here waits for.
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
            builder.UseSetting(
                "Fonoteca:AllowFileMutation", allowMutation ? "true" : "false");

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAcoustIdLookup>(clusters);
                services.AddSingleton<IMusicBrainzCatalogue>(catalogue);
            });
        });

    private static void SkipWithoutTools() =>
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH.");

    /// <summary>
    /// Two clusters, and the higher-scoring one is the wrong answer.
    /// </summary>
    /// <remarks>
    /// The shape that makes <see cref="TheClusterWrittenIsTheOneThatNamesTheChosenRecording"/>
    /// mean something: ranking by score alone picks <see cref="RivalCluster"/>,
    /// and only following the chosen recording picks the other.
    /// </remarks>
    private sealed class StubClusters(bool unavailable) : IAcoustIdLookup
    {
        /// <summary>The fixed answer, exposed so a test can store it as a pass would.</summary>
        public static IReadOnlyList<AcoustIdMatch> Answer { get; } =
        [
            new AcoustIdMatch(RivalCluster, 0.98, [new AcoustIdRecordingRef(Rival, 400)]),
            new AcoustIdMatch(ChosenCluster, 0.91, [new AcoustIdRecordingRef(Chosen, 3)]),
        ];

        /// <summary>How many lookups reached it. The whole assertion for the cache.</summary>
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
            AudioFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            if (unavailable) throw new ProviderUnavailableException("acoustid", "Down.");

            Calls++;
            return Task.FromResult(Answer);
        }
    }

    /// <summary>Names whatever recording it is asked about, or holds nothing at all.</summary>
    private sealed class StubCatalogue(bool knowsNothing) : IMusicBrainzCatalogue
    {
        /// <summary>Recording lookups that reached it — one per candidate, the expensive half.</summary>
        public int Calls { get; private set; }

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
            Calls++;

            return Task.FromResult(knowsNothing
                ? null
                : new MusicBrainzRecording(
                    id,
                    "Burn This Disco Out",
                    null,
                    TimeSpan.FromSeconds(Corpus.DurationSeconds),
                    [new MusicBrainzCredit(null, "Michael Jackson", null, null, null, "Person")],
                    [],
                    [],
                    [],
                    null,
                    null));
        }

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
}
