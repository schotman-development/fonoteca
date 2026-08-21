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

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>Three files of one folder, in path order.</summary>
    private MediaFileId _first;
    private MediaFileId _second;
    private MediaFileId _third;

    /// <summary>A file the passes already placed, which this endpoint must not touch.</summary>
    private MediaFileId _settled;

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

        await db.SaveChangesAsync(Token);
    }

    /// <summary>A file AcoustID could not place, which is what this screen is for.</summary>
    private static MediaFile Unplaced(string path) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 12_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        AcoustIdOutcome = AcoustIdOutcome.Unknown,
        AcoustIdCheckedUtc = DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
    };

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

        public Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(
            Mbid recording,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicBrainzReleaseCandidate>>([]);

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
                []);
    }
}
