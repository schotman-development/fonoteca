using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
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
/// Filing files under an album a person chose, through the real host.
/// </summary>
/// <remarks>
/// Its own class and its own database, like <see cref="MatchingEndpointTests"/>
/// and for a sharper version of the same reason: this is the only endpoint in
/// the application that <b>writes</b> a recording link for files that hold no
/// recording MBID, and every assertion here is about what it left behind rather
/// than what it returned.
///
/// Four judgements are under test, and each is a way this could be quietly
/// wrong:
///
/// <list type="bullet">
/// <item>a filed file leaves <b>all three</b> worklists, because a file that
/// keeps any one of the three refusals goes on being offered by the screen that
/// just filed it;</item>
/// <item>the seating committed is the seating that was sent, including a pairing
/// that is not in file order — the whole point of letting a person correct
/// one;</item>
/// <item>a position the release does not print is refused, and refused
/// <b>before</b> anything is written;</item>
/// <item>a file that is no longer an open question is skipped rather than
/// rewritten — this endpoint answers refusals, it does not overrule
/// decisions.</item>
/// </list>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class AlbumFilingEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Mbid Album = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    /// <summary>A second edition with the same track list, for the cross-edition case.</summary>
    private static readonly Mbid Other = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    /// <summary>An artist the catalogue already holds, so a track credit can link to it.</summary>
    private static readonly Mbid Known = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));

    /// <summary>Billed on the same tracks and absent from the catalogue.</summary>
    private static readonly Mbid Stranger = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>Held rather than built inline, so a test can read what was sent.</summary>
    private readonly StubSubmissions _submissions = new();

    /// <summary>Three files of one folder, in path order.</summary>
    private MediaFileId _first;
    private MediaFileId _second;
    private MediaFileId _third;

    /// <summary>A file the passes already placed, which this endpoint must not touch.</summary>
    private MediaFileId _settled;

    /// <summary>Known audio MusicBrainz links to no recording: open on enrichment only.</summary>
    private MediaFileId _unlinked;

    /// <summary>Identified and linked, and no release fitted: open on attribution only.</summary>
    private MediaFileId _unfitted;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-filing-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IMusicBrainzCatalogue>(new StubAlbum());
                services.AddSingleton<IAcoustIdSubmission>(_submissions);
            });
        });

        using var warm = _factory.CreateClient();

        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The album, the track list, the recordings and the links, from one click.
    /// </summary>
    /// <remarks>
    /// The whole track list, not only the seated part: "you are missing track 3"
    /// is the question the release page exists to answer, and a writer that only
    /// minted the slots something landed on could never answer it.
    /// </remarks>
    [Fact]
    public async Task FilingWritesTheAlbumItsTrackListAndTheRecordingEachFileNowHolds()
    {
        var result = await FileAsync(
            (_first, 1, 1),
            (_second, 1, 2));

        Assert.Equal(2, result.Filed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal("Off the Wall", result.Title);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = await db.Releases.SingleAsync(row => row.Mbid == Album, Token);
        Assert.Equal(3, release.TrackCount);
        Assert.NotNull(release.ReleaseGroupId);

        // Every printed slot, including the one nobody holds.
        var tracks = await db.Tracks.Where(row => row.ReleaseId == release.Id).ToListAsync(Token);
        Assert.Equal(3, tracks.Count);

        var first = await db.MediaFiles.SingleAsync(row => row.Id == _first, Token);

        Assert.Equal(release.Id, first.ReleaseId);
        Assert.Equal(release.ReleaseGroupId, first.ReleaseGroupId);
        Assert.NotNull(first.TrackId);

        // The identity comes off the slot, which is the only place it exists for
        // these files: they carried no recording MBID of their own.
        var recording = await db.Recordings.SingleAsync(row => row.Id == first.RecordingId, Token);
        Assert.Equal("Don't Stop 'Til You Get Enough", recording.Title);
    }

    /// <summary>
    /// A recording minted from a track list reaches its artist's page.
    /// </summary>
    /// <remarks>
    /// <c>BrowsableAsync</c> reaches an artist through
    /// <c>ArtistCredits.RecordingId</c>, a recording relationship or the work
    /// hop — <b>never through the release</b>. So a recording minted with no
    /// credit is on the album and on nobody's page, and the failure is invisible
    /// on the screen that just filed it: measured on the target library, filing
    /// <i>Sixteen Tons</i> by hand took its artist from eleven tracks to six.
    ///
    /// The billing line costs nothing — <c>ReleaseIncludes</c> already asks for
    /// <c>ArtistCredits</c> — which is what separates it from the rest of the
    /// graph. Conductors, orchestras and the composer hop need a recording
    /// lookup each, and refusing that per-file cost is exactly what
    /// <see cref="EnrichmentOutcome.LinkedByPerson"/> means.
    /// </remarks>
    [Fact]
    public async Task ARecordingMintedFromATrackListIsCreditedToTheArtistItIsBilledTo()
    {
        await FileAsync((_first, 1, 1));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var file = await db.MediaFiles.SingleAsync(row => row.Id == _first, Token);

        var credits = await db.ArtistCredits
            .Where(credit => credit.RecordingId == file.RecordingId)
            .Join(db.Artists, credit => credit.ArtistId, artist => artist.Id, (credit, artist) => artist)
            .ToListAsync(Token);

        // Only the artist the catalogue already held. The stranger billed beside
        // him on the same track is not minted here, which is
        // `ApplyCreditsAsync`'s rule and holds for the same reason.
        var artist = Assert.Single(credits);
        Assert.Equal("Michael Jackson", artist.Name);
        Assert.Equal(Known, artist.Mbid);
    }

    /// <summary>
    /// A recording the enrichment pass already described keeps its own credits.
    /// </summary>
    /// <remarks>
    /// Filled where empty, never replaced. A recording lookup carries the
    /// conductor, the orchestra and the composer hop; a release track list
    /// carries the billing line and nothing else, so converging on MusicBrainz
    /// here would overwrite the richer answer with the poorer one on every pass —
    /// and the pass runs over the whole library. <c>MediumFormats</c>'s bargain
    /// and <c>PrimaryType ??=</c>'s, not <c>ApplyTracksAsync</c>'s.
    /// </remarks>
    [Fact]
    public async Task FilingDoesNotReplaceTheCreditsEnrichmentAlreadyWrote()
    {
        // Track 3, whose recording is seeded with a credit of its own.
        await FileAsync((_third, 1, 3));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var file = await db.MediaFiles.SingleAsync(row => row.Id == _third, Token);

        var credits = await db.ArtistCredits
            .Where(credit => credit.RecordingId == file.RecordingId)
            .Join(db.Artists, credit => credit.ArtistId, artist => artist.Id, (credit, artist) => artist)
            .ToListAsync(Token);

        var artist = Assert.Single(credits);
        Assert.Equal("The Enrichment Pass Found This One", artist.Name);
    }

    /// <summary>
    /// A filed file is off the identification, enrichment and attribution worklists.
    /// </summary>
    /// <remarks>
    /// The assertion that matters most and the one easiest to leave out. Setting
    /// the attribution outcome alone looks like it worked — the album is on the
    /// release page — while the file goes on appearing under "AcoustID has never
    /// heard this" forever, because the worklist is
    /// <c>UnidentifiedOutcomes OR UnlinkedOutcomes</c> and nothing cleared
    /// either.
    /// </remarks>
    [Fact]
    public async Task AFiledFileLeavesEveryWorklist()
    {
        await FileAsync((_first, 1, 1), (_second, 1, 2), (_third, 1, 3));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var row = await db.MediaFiles.SingleAsync(file => file.Id == _first, Token);

        Assert.Equal(AcoustIdOutcome.IdentifiedByPerson, row.AcoustIdOutcome);
        Assert.Equal(EnrichmentOutcome.LinkedByPerson, row.EnrichmentOutcome);
        Assert.Equal(ReleaseAttributionOutcome.AttributedByPerson, row.AttributionOutcome);

        // The three guards that keep a pass — or a hand-written UPDATE clearing
        // the lookup stamps to re-ask after a rule change — from handing this
        // file back to the rule that could not answer it.
        Assert.NotNull(row.IdentityDecidedUtc);
        Assert.NotNull(row.ReleaseDecidedUtc);

        using var client = _factory!.CreateClient();

        var queue = await client.GetFromJsonAsync<MatchingQueueResponse>(
            new Uri("/api/catalogue/matching", UriKind.Relative), Token);

        Assert.NotNull(queue);
        Assert.DoesNotContain(queue.Items, item => item.Id == $"recording:{_first.Value}");
    }

    /// <summary>
    /// The pairing committed is the pairing sent, not the one file order implies.
    /// </summary>
    /// <remarks>
    /// The reason the request carries seats at all. The client's default is file
    /// order and it is a guess that is wrong the moment a rip is missing a track;
    /// a person correcting it is the entire value of the screen, and an endpoint
    /// that re-derived the order would throw their correction away while
    /// reporting success.
    /// </remarks>
    [Fact]
    public async Task TheSeatingCommittedIsTheOneThatWasSentAndNotFileOrder()
    {
        // Deliberately crossed: the first file on the third position, the third
        // on the first. File order would produce the identity mapping.
        await FileAsync((_first, 1, 3), (_third, 1, 1));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var first = await db.MediaFiles.SingleAsync(file => file.Id == _first, Token);
        var third = await db.MediaFiles.SingleAsync(file => file.Id == _third, Token);

        var firstTrack = await db.Tracks.SingleAsync(track => track.Id == first.TrackId, Token);
        var thirdTrack = await db.Tracks.SingleAsync(track => track.Id == third.TrackId, Token);

        Assert.Equal(3, firstTrack.Position);
        Assert.Equal(1, thirdTrack.Position);
    }

    /// <summary>
    /// A position the album does not print is refused, and nothing is written.
    /// </summary>
    /// <remarks>
    /// Checked against the release before the writer runs, because the failure
    /// is otherwise silent: <c>TrackIdAt</c> answers null for a slot that does
    /// not exist, so the file would be filed under the album with no position at
    /// all — which reads on every later screen as a track MusicBrainz has since
    /// removed rather than as a number somebody made up.
    /// </remarks>
    [Fact]
    public async Task APositionTheAlbumDoesNotPrintIsRefusedBeforeAnythingIsWritten()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/files/release", UriKind.Relative),
            new AlbumFilingRequest(Album.Value, [new AlbumFilingPair(_first.Value, 1, 99)]),
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.False(await db.Releases.AnyAsync(row => row.Mbid == Album, Token));

        var row = await db.MediaFiles.SingleAsync(file => file.Id == _first, Token);
        Assert.Null(row.ReleaseId);
        Assert.Equal(AcoustIdOutcome.Unknown, row.AcoustIdOutcome);
    }

    /// <summary>Two files cannot sit on one position, and saying so is not a write.</summary>
    [Fact]
    public async Task TwoFilesOnOnePositionIsRefused()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/files/release", UriKind.Relative),
            new AlbumFilingRequest(
                Album.Value,
                [new AlbumFilingPair(_first.Value, 1, 1), new AlbumFilingPair(_second.Value, 1, 1)]),
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.False(await db.Releases.AnyAsync(row => row.Mbid == Album, Token));
    }

    /// <summary>
    /// A file that is not an open question is skipped, not rewritten.
    /// </summary>
    /// <remarks>
    /// This endpoint answers refusals. Overruling a decision — a pass's or a
    /// person's — is a different act, and one a screen offering "match these to
    /// an album" is not the place to perform silently. The count comes back so
    /// two open tabs are visible rather than merely lucky.
    /// </remarks>
    [Fact]
    public async Task AFileThatIsNoLongerAQuestionIsSkippedRatherThanRewritten()
    {
        var result = await FileAsync((_first, 1, 1), (_settled, 1, 2));

        Assert.Equal(1, result.Filed);
        Assert.Equal(1, result.Skipped);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var settled = await db.MediaFiles.SingleAsync(file => file.Id == _settled, Token);

        Assert.Equal(ReleaseAttributionOutcome.Attributed, settled.AttributionOutcome);
        Assert.Null(settled.ReleaseDecidedUtc);
    }

    /// <summary>
    /// A folder that is nobody's release stops being asked about, and the files
    /// in it the passes did place are left exactly as they were.
    /// </summary>
    /// <remarks>
    /// The second half is the one that could be quietly wrong. A folder is rarely
    /// wholly unmatched — the fixture's is three open files and one the passes
    /// settled, which is the shape of the target library — and a dismissal that
    /// stamped every row under the prefix would throw away an identity, a
    /// recording link and a release that cost turns at the rate limit to get, on
    /// a click whose whole meaning is "stop asking about the ones you could not
    /// answer".
    /// </remarks>
    [Fact]
    public async Task MarkingAFolderUnreleasedClosesItsOpenFilesAndLeavesTheSettledOneAlone()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/folders/unreleased", UriKind.Relative),
            new FolderUnreleasedRequest("Michael Jackson/Off the Wall"),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<FolderUnreleasedResponse>(Token);

        Assert.NotNull(result);

        // Three open on identification, one on enrichment, one on attribution.
        Assert.Equal(5, result.Closed);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        foreach (var id in new[] { _first, _second, _third })
        {
            var row = await db.MediaFiles.SingleAsync(file => file.Id == id, Token);

            Assert.Equal(AcoustIdOutcome.Unreleased, row.AcoustIdOutcome);
            Assert.NotNull(row.IdentityDecidedUtc);

            // The stamp that was already there is the one AcoustID was asked at,
            // not the one somebody answered at.
            Assert.Equal(
                DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
                row.AcoustIdCheckedUtc);
        }

        // Open on enrichment and nowhere else: the enrichment column moves, and
        // the identification verdict the pass reached is left standing, because
        // this file's identity was never the open question.
        var unlinked = await db.MediaFiles.SingleAsync(file => file.Id == _unlinked, Token);

        Assert.Equal(EnrichmentOutcome.Unreleased, unlinked.EnrichmentOutcome);
        Assert.Equal(AcoustIdOutcome.Identified, unlinked.AcoustIdOutcome);
        Assert.NotNull(unlinked.IdentityDecidedUtc);
        Assert.NotNull(unlinked.AcoustId);

        // Open on attribution and nowhere else — the leg the worklist prints
        // under a different heading, and the reason the row query is the union of
        // both of the worklist's own predicates rather than the filing
        // endpoint's.
        var unfitted = await db.MediaFiles.SingleAsync(file => file.Id == _unfitted, Token);

        Assert.Equal(ReleaseAttributionOutcome.Unreleased, unfitted.AttributionOutcome);
        Assert.NotNull(unfitted.ReleaseDecidedUtc);
        Assert.Equal(EnrichmentOutcome.Linked, unfitted.EnrichmentOutcome);
        Assert.NotNull(unfitted.RecordingId);

        var settled = await db.MediaFiles.SingleAsync(file => file.Id == _settled, Token);

        Assert.Equal(AcoustIdOutcome.Identified, settled.AcoustIdOutcome);
        Assert.Equal(EnrichmentOutcome.Linked, settled.EnrichmentOutcome);
        Assert.Equal(ReleaseAttributionOutcome.Attributed, settled.AttributionOutcome);
        Assert.Null(settled.IdentityDecidedUtc);
        Assert.NotNull(settled.RecordingId);
    }

    /// <summary>
    /// A folder with nothing open in it is a 404 rather than a write of nothing.
    /// </summary>
    /// <remarks>
    /// The commonest cause is a mistyped or moved path, and the second commonest
    /// is two tabs. Both want to be told, because a silent "0 files marked" reads
    /// exactly like success.
    /// </remarks>
    [Fact]
    public async Task MarkingAFolderWithNothingOpenInItSaysSoRatherThanSucceeding()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/folders/unreleased", UriKind.Relative),
            new FolderUnreleasedRequest("Michael Jackson/Thriller"),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// What is contributed is the link a person made, not audio nobody has heard.
    /// </summary>
    /// <remarks>
    /// The distinction this pins is the one an earlier draft got backwards, and
    /// it decides most of the library. <c>_unlinked</c> is the majority case —
    /// <b>454 of 697 open files</b> on the library this was built against:
    /// AcoustID recognises the audio perfectly well and carries a cluster for
    /// it, and MusicBrainz links that cluster to no recording at all. Selecting
    /// on "AcoustID has never heard this" drops every one of them, which is
    /// exactly backwards — a submission carries <c>fingerprint + mbid</c>, so
    /// what it contributes is the <i>link</i>, and the link is the thing those
    /// files are missing and a person has just supplied.
    ///
    /// The file that must <b>not</b> be sent is the one a pass placed, and this
    /// makes one by clearing the decided stamp off a filed row afterwards. Its
    /// recording came from AcoustID's own answer, so submitting it back would be
    /// feeding the provider its opinion and reading the echo as a second source.
    /// </remarks>
    [Fact]
    public async Task ContributingSendsTheRecordingsAPersonChoseAndNotTheOnesAPassDid()
    {
        await FileAsync((_first, 1, 1), (_second, 1, 2), (_unlinked, 1, 3));

        var release = await FiledReleaseAsync();

        // All three, including the one AcoustID has heard: it is the link that
        // is news, not the audio.
        Assert.Equal(3, (await DetailAsync(release)).Contributable);

        // Now make one of them look like a file the attribution pass placed —
        // same release, same recording, no person in it.
        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            var placed = await seed.MediaFiles.FirstAsync(row => row.Id == _second, Token);

            placed.IdentityDecidedUtc = null;
            placed.AcoustIdOutcome = AcoustIdOutcome.Identified;

            await seed.SaveChangesAsync(Token);
        }

        Assert.Equal(2, (await DetailAsync(release)).Contributable);

        var result = await ContributeAsync(release);

        Assert.Equal(2, result.Submitted);
        Assert.Equal(2, result.Accepted);

        // The pairing that crossed the wire is the one the person filed: each
        // file's own fingerprint, bound to the recording of the slot it was
        // seated on — never to a cluster the endpoint chose for itself.
        Assert.Equal(2, _submissions.Sent.Count);

        Assert.Equal(
            [TrackRecording(1), TrackRecording(3)],
            [.. _submissions.Sent.Select(item => item.Recording.Value).Order()]);

        // The audio's length, not the fingerprint's 120 seconds — and each
        // file's own, which is what a shared constant here would fail to catch.
        Assert.Equal(
            [TimeSpan.FromSeconds(212), TimeSpan.FromSeconds(641)],
            [.. _submissions.Sent.Select(item => item.Fingerprint.Duration).Order()]);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var sent = await db.MediaFiles.Where(file => file.Id == _first || file.Id == _unlinked)
            .ToListAsync(Token);

        Assert.All(sent, file => Assert.NotNull(file.AcoustIdSubmittedUtc));

        // Nothing else is stamped — not the row that now looks pass-placed, and
        // not the one the passes really did settle.
        var untouched = await db.MediaFiles
            .Where(file => file.Id == _second || file.Id == _settled)
            .ToListAsync(Token);

        Assert.All(untouched, file => Assert.Null(file.AcoustIdSubmittedUtc));

        // And the offer does not repeat. The import happens out of band, so
        // nothing about the file changes and only the stamp can retire it.
        Assert.Equal(0, (await DetailAsync(release)).Contributable);

        using var client = _factory!.CreateClient();

        var again = await client.PostAsync(
            new Uri($"/api/catalogue/releases/{release}/fingerprints", UriKind.Relative),
            content: null,
            Token);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    /// <summary>
    /// A send that failed leaves the offer standing, rather than the reverse.
    /// </summary>
    /// <remarks>
    /// The ordering this pins is the only one with a silent failure in it. Stamp
    /// first and a refused batch retires the question anyway: the button stops
    /// appearing, the count reads zero, and the contribution that never left is
    /// indistinguishable from one that did. Sending first costs, at worst, a
    /// duplicate submission — which AcoustID discards.
    /// </remarks>
    [Fact]
    public async Task WhenAcoustIdWillNotTakeThemNothingIsRecorded()
    {
        await FileAsync((_first, 1, 1));

        var release = await FiledReleaseAsync();
        _submissions.Unavailable = true;

        using var client = _factory!.CreateClient();

        var response = await client.PostAsync(
            new Uri($"/api/catalogue/releases/{release}/fingerprints", UriKind.Relative),
            content: null,
            Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var file = await db.MediaFiles.FirstAsync(row => row.Id == _first, Token);

        Assert.Null(file.AcoustIdSubmittedUtc);
        Assert.Equal(1, (await DetailAsync(release)).Contributable);
    }

    /// <summary>
    /// A refusal is not an outage, and the difference is what a person does next.
    /// </summary>
    /// <remarks>
    /// The state a fresh install is in: <c>Fonoteca:AcoustIdUserKey</c> unset, so
    /// the client refuses before a byte leaves. Answered 503 — which is what a
    /// single <c>catch (ProviderException)</c> gives — the screen says the
    /// service is unavailable and invites the one action that can never work.
    /// The two subclasses exist to tell "retry later" from "somebody must change
    /// something" apart, and this is the endpoint where that distinction is
    /// worth a status code.
    ///
    /// The detail is asserted as well as the code, because it is the only place
    /// the setting is named — and because nothing in <c>Program.cs</c> registers
    /// problem-details customisation today, which is a thing that could change
    /// under this without any other test noticing.
    /// </remarks>
    [Fact]
    public async Task AKeyThatIsNotSetIsRefusedRatherThanReportedAsAnOutage()
    {
        await FileAsync((_first, 1, 1));

        var release = await FiledReleaseAsync();
        _submissions.Rejected = true;

        using var client = _factory!.CreateClient();

        var response = await client.PostAsync(
            new Uri($"/api/catalogue/releases/{release}/fingerprints", UriKind.Relative),
            content: null,
            Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var problem = await response.Content.ReadAsStringAsync(Token);

        Assert.Contains("Fonoteca:AcoustIdUserKey", problem, StringComparison.Ordinal);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var file = await db.MediaFiles.FirstAsync(row => row.Id == _first, Token);

        Assert.Null(file.AcoustIdSubmittedUtc);
    }

    /// <summary>The recording MBID <see cref="StubAlbum"/> prints at a position.</summary>
    private static Guid TrackRecording(int position) =>
        Guid.Parse($"33333333-3333-3333-3333-33333333333{position}");

    /// <summary>The catalogue id of the album the filing just minted.</summary>
    private async Task<Guid> FiledReleaseAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = await db.Releases.FirstAsync(row => row.Mbid == Album, Token);
        return release.Id.Value;
    }

    private async Task<ReleaseDetailResponse> DetailAsync(Guid release)
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync(
            new Uri($"/api/catalogue/releases/{release}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<ReleaseDetailResponse>(Token);

        Assert.NotNull(detail);
        return detail;
    }

    private async Task<FingerprintContributionResponse> ContributeAsync(Guid release)
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsync(
            new Uri($"/api/catalogue/releases/{release}/fingerprints", UriKind.Relative),
            content: null,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<FingerprintContributionResponse>(Token);

        Assert.NotNull(result);
        return result;
    }

    private async Task<AlbumFilingResponse> FileAsync(
        params (MediaFileId File, int Disc, int Position)[] seats)
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/files/release", UriKind.Relative),
            new AlbumFilingRequest(
                Album.Value,
                [.. seats.Select(seat =>
                    new AlbumFilingPair(seat.File.Value, seat.Disc, seat.Position))]),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AlbumFilingResponse>(Token);

        Assert.NotNull(result);
        return result;
    }

    /// <summary>
    /// Answering part of an album shows the part already answered as taken.
    /// </summary>
    /// <remarks>
    /// <b>The case the screen was blind to, and it is the ordinary one.</b> The
    /// worklist lists open questions, so a folder the passes mostly placed
    /// arrives at the album dialog as a one-file album — and the seating it
    /// proposes, files in path order against slots in printed order, then puts
    /// that leftover on track 1. Measured on the target library: a twenty-one
    /// file live album with a single <c>Ambiguous</c> file whose siblings hold
    /// every position except fourteen.
    ///
    /// Filing first and reading back second is deliberate: it uses this
    /// endpoint's own write to make the state, so what is asserted is the loop a
    /// person actually walks — answer some of an album, come back to the rest.
    /// </remarks>
    [Fact]
    public async Task PositionsHeldByAFoldersOwnFilesComeBackNamingThem()
    {
        await FileAsync((_first, 1, 1), (_second, 1, 2));

        var slots = await SlotsAsync(Album, "Michael Jackson/Off the Wall");

        Assert.Equal("01 First.flac", Held(slots, 1));
        Assert.Equal("02 Second.flac", Held(slots, 2));

        // The one still open, which is what the client seats onto.
        Assert.Null(Held(slots, 3));
    }

    /// <summary>
    /// A position held under a different edition is not reported against this one.
    /// </summary>
    /// <remarks>
    /// The load-bearing half of the query. Track 2 of one pressing is not track 2
    /// of another, so a sibling's position is only a fact about the release it
    /// was filed under. Reported across editions it would grey out a slot on the
    /// strength of a number that means something else — and silently, since the
    /// file making the claim is not on the screen. Where the editions differ the
    /// query finds nothing and the dialog behaves exactly as it did before, which
    /// is the right way for this to fail.
    /// </remarks>
    [Fact]
    public async Task PositionsHeldUnderADifferentEditionAreNotReportedAgainstThisOne()
    {
        await FileAsync((_first, 1, 1), (_second, 1, 2));

        var slots = await SlotsAsync(Other, "Michael Jackson/Off the Wall");

        Assert.All(slots, slot => Assert.Null(slot.HeldBy));
    }

    /// <summary>
    /// Without a folder the track list is MusicBrainz's alone, as it always was.
    /// </summary>
    [Fact]
    public async Task WithNoFolderNoPositionIsReportedAsHeld()
    {
        await FileAsync((_first, 1, 1));

        var slots = await SlotsAsync(Album, folder: null);

        Assert.All(slots, slot => Assert.Null(slot.HeldBy));
    }

    /// <summary>
    /// The folder listing is the directory, not the worklist — matched files and all.
    /// </summary>
    /// <remarks>
    /// The whole point of the endpoint. The worklist shows what the passes
    /// refused, so an album matched to the wrong record appears on it as the two
    /// or three files that were also refused, and the wrong ones are on no
    /// screen at all. Here the counts disagree — six files, four of them open —
    /// and the two that are not say what they were matched to instead.
    /// </remarks>
    [Fact]
    public async Task TheFolderListingHoldsTheMatchedFilesAndNotOnlyTheOpenOnes()
    {
        var listing = await ContentsAsync("Michael Jackson/Off the Wall");

        Assert.Equal(6, listing.Files);
        Assert.Equal(4, listing.Open);
        Assert.Equal(6, listing.Items.Count);

        // Path order, so the discs of a set arrive in the order they sit in.
        Assert.Equal(
            listing.Items.Select(item => item.Name).Order(StringComparer.Ordinal),
            listing.Items.Select(item => item.Name));

        var settled = Assert.Single(listing.Items, item => item.Name == "04 Settled.flac");
        Assert.False(settled.Open);
        Assert.Null(settled.Reason);
        Assert.Equal("Rock with You", settled.Recording);
        Assert.Equal(nameof(ReleaseAttributionOutcome.Attributed), settled.Certainty);

        // Identified, linked, and refused by attribution: matched to a recording
        // and to no album, which is a state the screen has to be able to print.
        var unfitted = Assert.Single(listing.Items, item => item.Name == "06 Unfitted.flac");
        Assert.False(unfitted.Open);
        Assert.Null(unfitted.Release);
        Assert.Equal(nameof(ReleaseAttributionOutcome.NoConfidentFit), unfitted.Certainty);

        // The first pass that refused names the question, exactly as the
        // worklist folds it — this file's audio AcoustID knows perfectly well.
        var unlinked = Assert.Single(listing.Items, item => item.Name == "05 Unlinked.flac");
        Assert.True(unlinked.Open);
        Assert.Equal(nameof(EnrichmentOutcome.NoRecording), unlinked.Reason);

        var unknown = Assert.Single(listing.Items, item => item.Name == "01 First.flac");
        Assert.Equal(nameof(AcoustIdOutcome.Unknown), unknown.Reason);

        // The sibling folder whose name starts with this one.
        Assert.DoesNotContain(listing.Items, item => item.Name == "01 Live.flac");
    }

    /// <summary>
    /// A file filed by hand reads back as matched, with the position it was given.
    /// </summary>
    /// <remarks>
    /// The other half of what makes a wrong match visible: not just <i>that</i> a
    /// file is placed but where, and by whom. <c>AttributedByPerson</c> rather
    /// than <c>Attributed</c> is the distinction somebody hunting a bad match is
    /// reading for.
    /// </remarks>
    [Fact]
    public async Task AFileFiledByHandReadsBackWithItsAlbumAndItsPosition()
    {
        await FileAsync((_first, 1, 2));

        var listing = await ContentsAsync("Michael Jackson/Off the Wall");
        var filed = Assert.Single(listing.Items, item => item.Name == "01 First.flac");

        Assert.False(filed.Open);
        Assert.Equal(1, filed.Disc);
        Assert.Equal(2, filed.Position);
        Assert.NotNull(filed.Release);
        Assert.NotNull(filed.ReleaseId);
        Assert.Equal(nameof(ReleaseAttributionOutcome.AttributedByPerson), filed.Certainty);

        // And the folder is one file less open than it was.
        Assert.Equal(3, listing.Open);
    }

    [Fact]
    public async Task AFolderTheCatalogueHoldsNothingUnderIsNotFound()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync(
            new Uri(
                "/api/catalogue/matching/folders/files?folder="
                + Uri.EscapeDataString("Nobody/Nothing"),
                UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<FolderContentsResponse> ContentsAsync(string folder)
    {
        using var client = _factory!.CreateClient();

        var listing = await client.GetFromJsonAsync<FolderContentsResponse>(
            new Uri(
                $"/api/catalogue/matching/folders/files?folder={Uri.EscapeDataString(folder)}",
                UriKind.Relative),
            Token);

        Assert.NotNull(listing);
        return listing;
    }

    private async Task<IReadOnlyList<ReleaseSlotRow>> SlotsAsync(Mbid release, string? folder)
    {
        using var client = _factory!.CreateClient();

        var url = $"/api/catalogue/matching/releases/{release.Value}/slots"
            + (folder is null ? string.Empty : $"?folder={Uri.EscapeDataString(folder)}");

        var response = await client.GetFromJsonAsync<ReleaseSlotsResponse>(
            new Uri(url, UriKind.Relative), Token);

        Assert.NotNull(response);
        return response.Slots;
    }

    private static string? Held(IReadOnlyList<ReleaseSlotRow> slots, int position) =>
        slots.Single(slot => slot.DiscNumber == 1 && slot.Position == position).HeldBy;

    /// <summary>
    /// Reopening takes back what a pass decided and leaves an open file alone.
    /// </summary>
    /// <remarks>
    /// The two halves of the rule, in one test because they are one judgement.
    /// A folder reaching this endpoint is usually part right: the files a pass
    /// placed are the ones being disputed, and the ones already waiting for an
    /// answer are waiting for the *same* answer and say something true in the
    /// meantime — <c>NoRecording</c> is a fact about MusicBrainz's links that
    /// "somebody disagreed" does not carry.
    /// </remarks>
    [Fact]
    public async Task ReopeningAFolderTakesBackWhatAPassDecidedAndLeavesTheOpenFilesAlone()
    {
        var result = await ReopenAsync("Michael Jackson/Off the Wall");

        // The two rows with a derived recording on them: the one attribution
        // filed, and the one it refused after enrichment had linked it.
        Assert.Equal(2, result.Reopened);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var settled = await db.MediaFiles.SingleAsync(file => file.Id == _settled, Token);

        Assert.Null(settled.RecordingId);
        Assert.Null(settled.ReleaseId);
        Assert.Null(settled.TrackId);
        Assert.Null(settled.ReleaseGroupId);
        Assert.Null(settled.IdentityDecidedUtc);
        Assert.Null(settled.ReleaseDecidedUtc);
        Assert.Equal(AcoustIdOutcome.ReopenedByPerson, settled.AcoustIdOutcome);
        Assert.Equal(EnrichmentOutcome.NotAttempted, settled.EnrichmentOutcome);
        Assert.Equal(ReleaseAttributionOutcome.NotAttempted, settled.AttributionOutcome);

        // Open already, and on a leg this endpoint never reaches: nothing about
        // it was a pass's decision, so there is nothing to take back.
        var unlinked = await db.MediaFiles.SingleAsync(file => file.Id == _unlinked, Token);

        Assert.Equal(AcoustIdOutcome.Identified, unlinked.AcoustIdOutcome);
        Assert.Equal(EnrichmentOutcome.NoRecording, unlinked.EnrichmentOutcome);

        // And a file that never held anything is untouched, rather than
        // relabelled with a disagreement nobody expressed about it.
        var first = await db.MediaFiles.SingleAsync(file => file.Id == _first, Token);

        Assert.Equal(AcoustIdOutcome.Unknown, first.AcoustIdOutcome);
    }

    /// <summary>
    /// A reopened file is on the worklist, and every pass's lookup stamp survives.
    /// </summary>
    /// <remarks>
    /// The whole design in one assertion pair. The point of reopening is that
    /// the folder becomes a question a <i>person</i> answers, so it has to be on
    /// the worklist — and no pass may answer it instead, because a pass asking
    /// AcoustID and MusicBrainz the same question gets the same wrong answer
    /// back. The three lookup stamps are what keep the passes off it: each is
    /// its pass's worklist, and each still records something true, namely that
    /// the provider was asked. Clearing them is what "reopen" sounds like it
    /// should do and is exactly the bug.
    /// </remarks>
    [Fact]
    public async Task AReopenedFileIsOnTheWorklistAndNoPassCanReachIt()
    {
        await ReopenAsync("Michael Jackson/Off the Wall");

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var unfitted = await db.MediaFiles.SingleAsync(file => file.Id == _unfitted, Token);

        Assert.Equal(
            DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
            unfitted.AcoustIdCheckedUtc);
        Assert.Equal(
            DateTimeOffset.Parse("2026-02-01T11:00:00Z", CultureInfo.InvariantCulture),
            unfitted.RecordingLookupUtc);
        Assert.Equal(
            DateTimeOffset.Parse("2026-02-01T12:00:00Z", CultureInfo.InvariantCulture),
            unfitted.ReleaseLookupUtc);

        using var client = _factory!.CreateClient();

        var queue = await client.GetFromJsonAsync<MatchingQueueResponse>(
            new Uri("/api/catalogue/matching?take=1000", UriKind.Relative), Token);

        Assert.NotNull(queue);
        Assert.Contains(queue.Items, item => item.Id == $"recording:{_unfitted.Value}");
        Assert.Contains(queue.Items, item => item.Id == $"recording:{_settled.Value}");
    }

    /// <summary>
    /// A reopened file can then be filed by hand, which is the only way back.
    /// </summary>
    /// <remarks>
    /// The reason <c>ReopenedByPerson</c> had to go into the endpoint's own
    /// <c>UnidentifiedOutcomes</c> rather than only into the worklist query.
    /// <c>FileFilesUnderRelease</c> narrows to the same set — it answers
    /// refusals and does not overrule decisions — so a value that was on the
    /// screen and not in that set would produce a folder offering a chooser
    /// that then refuses every file in it.
    /// </remarks>
    [Fact]
    public async Task AReopenedFileCanBeFiledUnderAnAlbumByHand()
    {
        await ReopenAsync("Michael Jackson/Off the Wall");

        var result = await FileAsync((_settled, 1, 2));

        Assert.Equal(1, result.Filed);
        Assert.Equal(0, result.Skipped);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var row = await db.MediaFiles.SingleAsync(file => file.Id == _settled, Token);

        Assert.Equal(AcoustIdOutcome.IdentifiedByPerson, row.AcoustIdOutcome);
        Assert.NotNull(row.RecordingId);
        Assert.NotNull(row.TrackId);
    }

    /// <summary>A folder with nothing a pass placed is a 404, not a silent no-op.</summary>
    [Fact]
    public async Task ReopeningAFolderWithNothingMatchedInItIsRefused()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/folders/reopen", UriKind.Relative),
            new FolderReopenRequest("Michael Jackson/Thriller"),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<FolderReopenResponse> ReopenAsync(string folder)
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/matching/folders/reopen", UriKind.Relative),
            new FolderReopenRequest(folder),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<FolderReopenResponse>(Token);

        Assert.NotNull(result);
        return result;
    }

    private async Task SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        // Three files of one album folder, none of which holds a recording — the
        // measured state of every open file-level question on the library this
        // was built against.
        var first = Unplaced("Michael Jackson/Off the Wall/01 First.flac");
        var second = Unplaced("Michael Jackson/Off the Wall/02 Second.flac");
        var third = Unplaced("Michael Jackson/Off the Wall/03 Third.flac");

        _first = first.Id;
        _second = second.Id;
        _third = third.Id;

        db.MediaFiles.AddRange(first, second, third);

        // The artist the track list bills. Present before the filing, because the
        // writer links only artists the catalogue already knows.
        var known = new Artist
        {
            Id = ArtistId.New(),
            Name = "Michael Jackson",
            Mbid = Known,
        };

        db.Artists.Add(known);

        // Track 3's recording, already described by the enrichment pass and
        // credited to somebody else. The release lookup bills it to Michael
        // Jackson; filling only where empty means this credit survives.
        var described = new Recording
        {
            Id = RecordingId.New(),
            Title = "Working Day and Night",
            Mbid = new Mbid(Guid.Parse("33333333-3333-3333-3333-333333333333")),
        };

        var enriched = new Artist
        {
            Id = ArtistId.New(),
            Name = "The Enrichment Pass Found This One",
            Mbid = new Mbid(Guid.Parse("77777777-7777-7777-7777-777777777777")),
        };

        db.Artists.Add(enriched);
        db.Recordings.Add(described);

        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = enriched.Id,
            RecordingId = described.Id,
            Position = 0,
        });

        // One the attribution pass already placed.
        var recording = new Recording
        {
            Id = RecordingId.New(),
            Title = "Rock with You",
            Mbid = new Mbid(Guid.CreateVersion7()),
        };

        db.Recordings.Add(recording);

        var settled = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = "Michael Jackson/Off the Wall/04 Settled.flac",
            SizeBytes = 42_000_000,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            RecordingId = recording.Id,
            AcoustIdOutcome = AcoustIdOutcome.Identified,
            EnrichmentOutcome = EnrichmentOutcome.Linked,
            AttributionOutcome = ReleaseAttributionOutcome.Attributed,
        };

        _settled = settled.Id;
        db.MediaFiles.Add(settled);

        // The other two shapes a folder dismissal has to close, and neither is
        // reachable through the identification leg: this file's audio AcoustID
        // knows perfectly well, and MusicBrainz links it to nothing…
        var unlinked = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = "Michael Jackson/Off the Wall/05 Unlinked.flac",
            SizeBytes = 9_000_000,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            Fingerprint = "AQADtEmi5FGiKMnxB92H_MeR_TjxHDmO_MFxHTmO_MFx3",
            FingerprintDuration = TimeSpan.FromSeconds(212),
            AcoustId = new AcoustId(Guid.Parse("3f2b0c11-0000-4000-8000-000000000001")),
            AcoustIdOutcome = AcoustIdOutcome.Identified,
            AcoustIdCheckedUtc = DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
            EnrichmentOutcome = EnrichmentOutcome.NoRecording,
            RecordingLookupUtc = DateTimeOffset.Parse("2026-02-01T11:00:00Z", CultureInfo.InvariantCulture),
        };

        _unlinked = unlinked.Id;
        db.MediaFiles.Add(unlinked);

        // …and this one the attribution pass reached and refused, which is the
        // leg the row query was widened for and the one the screen shows under a
        // different heading entirely.
        var unfitted = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = "Michael Jackson/Off the Wall/06 Unfitted.flac",
            SizeBytes = 8_000_000,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            RecordingId = recording.Id,
            AcoustIdOutcome = AcoustIdOutcome.Identified,
            AcoustIdCheckedUtc = DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
            EnrichmentOutcome = EnrichmentOutcome.Linked,
            RecordingLookupUtc = DateTimeOffset.Parse("2026-02-01T11:00:00Z", CultureInfo.InvariantCulture),
            AttributionOutcome = ReleaseAttributionOutcome.NoConfidentFit,
            ReleaseLookupUtc = DateTimeOffset.Parse("2026-02-01T12:00:00Z", CultureInfo.InvariantCulture),
        };

        _unfitted = unfitted.Id;
        db.MediaFiles.Add(unfitted);

        // A folder whose name begins with the one above. Every prefix query here
        // carries a trailing slash for this: `Off the Wall` is not `Off the Wall
        // Live`, and a live album swept into a studio one is silent.
        db.MediaFiles.Add(Unplaced("Michael Jackson/Off the Wall Live/01 Live.flac"));

        await db.SaveChangesAsync(Token);
    }

    /// <summary>A file AcoustID could not place, which is what this screen is for.</summary>
    /// <remarks>
    /// Fingerprinted and unrecognised, which is what <see cref="AcoustIdOutcome.Unknown"/>
    /// means: <c>fpcalc</c> ran and AcoustID had never heard the result. The
    /// fingerprint being present is what later makes the file worth contributing
    /// back, and a seed without one would make that endpoint untestable here for
    /// the wrong reason.
    /// </remarks>
    private static MediaFile Unplaced(string path) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 12_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        Fingerprint = "AQABz0qUkZK4oOfhL-CPc4e5C_wW2H2QH9uDL4cvoT8UNQ",
        FingerprintDuration = TimeSpan.FromSeconds(641),
        AcoustIdOutcome = AcoustIdOutcome.Unknown,
        AcoustIdCheckedUtc = DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
    };

    /// <summary>AcoustID's submit endpoint, remembering what it was told.</summary>
    private sealed class StubSubmissions : IAcoustIdSubmission
    {
        public List<AcoustIdSubmissionItem> Sent { get; } = [];

        /// <summary>Set by the test that checks nothing is recorded when the send fails.</summary>
        public bool Unavailable { get; set; }

        /// <summary>Set by the test that checks a refusal is not reported as an outage.</summary>
        public bool Rejected { get; set; }

        public Task<IReadOnlyList<AcoustIdSubmissionReceipt>> SubmitAsync(
            IReadOnlyList<AcoustIdSubmissionItem> items,
            CancellationToken cancellationToken = default)
        {
            if (Unavailable)
            {
                throw new ProviderUnavailableException("AcoustID", "AcoustID submit failed: down.");
            }

            if (Rejected)
            {
                // The message the real client produces for the case a fresh
                // install hits first, verbatim enough to assert on.
                throw new ProviderRejectedException(
                    "AcoustID",
                    "No AcoustID user key is configured. Set Fonoteca:AcoustIdUserKey — it is the "
                    + "operator's own key, shown on the account page at https://acoustid.org/ after "
                    + "signing in, and it is not the application key.");
            }

            Sent.AddRange(items);

            return Task.FromResult<IReadOnlyList<AcoustIdSubmissionReceipt>>(
                [.. items.Select((_, index) => new AcoustIdSubmissionReceipt(9000 + index, "pending"))]);
        }
    }

    /// <summary>One album with three printed positions, and nothing else.</summary>
    private sealed class StubAlbum : IMusicBrainzCatalogue
    {
        private static readonly Mbid Group = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        public Task<MusicBrainzRelease?> GetReleaseAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzRelease?>(id == Album || id == Other
                ? new MusicBrainzRelease(
                    id,
                    id == Album ? "Off the Wall" : "Off the Wall (2015 remaster)",
                    new ReleaseDate(1979, 8, 10),
                    "US",
                    "Official",
                    null,
                    [],
                    Group,
                    "Off the Wall",
                    "Album",
                    [],
                    [],
                    [
                        Track(1, "Don't Stop 'Til You Get Enough"),
                        Track(2, "Rock with You"),
                        Track(3, "Working Day and Night"),
                    ])
                : null);

        public Task<IReadOnlyList<MusicBrainzReleaseMatch>> SearchReleasesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseMatch>>([]);

        public Task<MusicBrainzRecording?> GetRecordingAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzRecording?>(null);

        public Task<MusicBrainzDiscography> BrowseReleaseGroupsForArtistAsync(
            Mbid artist,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MusicBrainzDiscography([], Complete: true));

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(
            Mbid recording,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseCandidate>>([]);

        public Task<MusicBrainzArtist?> GetArtistAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzArtist?>(null);

        public Task<MusicBrainzWork?> GetWorkAsync(
            Mbid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MusicBrainzWork?>(null);

        private static MusicBrainzTrack Track(int position, string title) =>
            new(
                1,
                position,
                position.ToString(CultureInfo.InvariantCulture),
                title,
                TimeSpan.FromMinutes(4),
                new Mbid(Guid.Parse($"33333333-3333-3333-3333-33333333333{position}")),

                // The billing line every release lookup already returns. Two
                // artists, and the catalogue holds only the first: a writer that
                // minted the second would put a row with a name and nothing else
                // into the artist list.
                [
                    new MusicBrainzCredit(Known, "Michael Jackson", null, " & ", null, "Person"),
                    new MusicBrainzCredit(Stranger, "Nobody In The Catalogue", null, null, null, null),
                ]);
    }
}
