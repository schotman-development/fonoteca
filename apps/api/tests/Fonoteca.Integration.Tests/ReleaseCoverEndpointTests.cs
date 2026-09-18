using System.Net;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// An album's cover is fetched from the archive once and then served from the
/// catalogue, and a person's choice replaces the archive's.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ReleaseCoverEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Mbid Release = new(Guid.Parse("4a78528d-8ea9-4213-a5b1-96d4b38ac439"));

    // The Back to Tennessee shape: the archive's front is a spread, and the
    // square sleeve is a second Front listed after it.
    private const long Spread = 1;
    private const long Square = 2;

    private readonly StubArchive _archive = new();
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly ReleaseId _releaseId = new(Guid.CreateVersion7());
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        _root = Directory.CreateTempSubdirectory("fonoteca-cover-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ICoverArtArchive>(_archive);
                services.AddSingleton<IClock>(_clock);
            });
        });

        // Starting the host is what migrates the database.
        _ = _factory.Services;

        await using var db = PostgresFixture.CreateContext(connectionString);
        db.Releases.Add(new Release { Id = _releaseId, Title = "Back to Tennessee", Mbid = Release });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task TheArchiveIsAskedOnceAndAChoiceReplacesItsFront()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory!.CreateClient();
        var cover = new Uri($"/api/catalogue/releases/{_releaseId.Value}/cover", UriKind.Relative);

        Assert.Equal(new[] { (byte)Spread }, await client.GetByteArrayAsync(cover, ct));
        Assert.Equal(new[] { (byte)Spread }, await client.GetByteArrayAsync(cover, ct));
        Assert.Equal(1, _archive.Downloads);

        using var chosen = await client.PostAsync(
            new Uri($"/api/catalogue/releases/{_releaseId.Value}/cover/archive/{Square}", UriKind.Relative),
            content: null,
            ct);
        Assert.Equal(HttpStatusCode.NoContent, chosen.StatusCode);
        Assert.Equal(new[] { (byte)Square }, await client.GetByteArrayAsync(cover, ct));

        using var elsewhere = await client.PostAsync(
            new Uri($"/api/catalogue/releases/{_releaseId.Value}/cover/archive/999", UriKind.Relative),
            content: null,
            ct);
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    [Fact]
    public async Task AnUploadIsServedAndAScriptBearingOneIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory!.CreateClient();
        var cover = new Uri($"/api/catalogue/releases/{_releaseId.Value}/cover", UriKind.Relative);

        using var svg = new ByteArrayContent("<svg><script/></svg>"u8.ToArray());
        svg.Headers.ContentType = new("image/svg+xml");
        using var refused = await client.PostAsync(cover, svg, ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        using var png = new ByteArrayContent([7, 7, 7]);
        png.Headers.ContentType = new("image/png");
        using var accepted = await client.PostAsync(cover, png, ct);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);

        using var served = await client.GetAsync(cover, ct);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 7, 7, 7 }, await served.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(0, _archive.Downloads);
    }

    /// <summary>
    /// An album nobody has a picture of is asked about again later.
    /// </summary>
    /// <remarks>
    /// <b>The one behaviour a stored "no cover" must not have is permanence.</b>
    /// Sleeves reach the archive and the shop continuously, and an album whose
    /// art arrived the day after somebody looked at it would otherwise draw a
    /// monogram forever — with nothing on any screen to say the answer was six
    /// months old, and no endpoint to clear it. Measured on the live library
    /// when this was written: <b>105 of 612</b> albums were holding exactly that
    /// answer, and 63 of them had a cover available the same afternoon.
    ///
    /// The two halves are both the point. Asking again <i>immediately</i> would
    /// make a shelf of a hundred coverless albums a hundred requests per browse
    /// against a paid subscription; never asking again is the bug. So: believed,
    /// then not.
    /// </remarks>
    [Fact]
    public async Task AnAlbumNobodyHasAPictureOfIsAskedAboutAgainAfterAWhile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory!.CreateClient();
        var cover = new Uri($"/api/catalogue/releases/{_releaseId.Value}/cover", UriKind.Relative);

        _archive.HasFront = false;

        using (var missing = await client.GetAsync(cover, ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // Believed: the row that was just written is the answer for now, and a
        // page redrawing its tiles does not re-ask on every view.
        using (var again = await client.GetAsync(cover, ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        Assert.Equal(1, _archive.Listings);

        // A week and a day later, with a sleeve now in the archive.
        _clock.Advance(TimeSpan.FromDays(8));
        _archive.HasFront = true;

        Assert.Equal(new[] { (byte)Spread }, await client.GetByteArrayAsync(cover, ct));
        Assert.Equal(2, _archive.Listings);

        // And once found, it is the answer again — no further asking.
        _clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(new[] { (byte)Spread }, await client.GetByteArrayAsync(cover, ct));
        Assert.Equal(2, _archive.Listings);
    }

    /// <summary>
    /// A picture somebody put there is not re-asked about, however old it is.
    /// </summary>
    /// <remarks>
    /// <b>The expiry has to be able to tell a stamp from an answer, and getting
    /// it wrong is unrecoverable.</b> A row with no bytes is "nobody had one
    /// last week"; a row with bytes is a picture, and for an upload it is the
    /// only copy — the archive never held it, so a re-ask that overwrote it
    /// would destroy something no pass could rebuild. Staged through the
    /// expiry rather than immediately, because the condition being tested is
    /// exactly the one an age check gets wrong.
    /// </remarks>
    [Fact]
    public async Task APictureSomebodyUploadedSurvivesTheExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory!.CreateClient();
        var cover = new Uri($"/api/catalogue/releases/{_releaseId.Value}/cover", UriKind.Relative);

        _archive.HasFront = false;

        using (var missing = await client.GetAsync(cover, ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using var png = new ByteArrayContent([9, 9, 9]);
        png.Headers.ContentType = new("image/png");
        using (var accepted = await client.PostAsync(cover, png, ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        // Long past the window, with the archive now offering a front of its own.
        _clock.Advance(TimeSpan.FromDays(400));
        _archive.HasFront = true;

        Assert.Equal(new byte[] { 9, 9, 9 }, await client.GetByteArrayAsync(cover, ct));
        Assert.Equal(1, _archive.Listings);
        Assert.Equal(0, _archive.Downloads);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }

    private sealed class StubArchive : ICoverArtArchive
    {
        public int Downloads { get; private set; }

        /// <summary>How many times the listing was asked for; the re-ask counter.</summary>
        public int Listings { get; private set; }

        /// <summary>Whether the archive holds a front at all, which can change.</summary>
        public bool HasFront { get; set; } = true;

        public Task<IReadOnlyList<CoverArtImage>> ListAsync(
            Mbid release,
            CancellationToken cancellationToken = default)
        {
            Listings++;

            return Task.FromResult<IReadOnlyList<CoverArtImage>>(
                HasFront
                    ?
                    [
                        new(Spread, Front: true, ["Front"], "Cover"),
                        new(Square, Front: false, ["Front"], "Front"),
                    ]
                    : []);
        }

        public Task<CoverArtBytes> DownloadAsync(
            Mbid release,
            long imageId,
            CancellationToken cancellationToken = default)
        {
            Downloads++;
            return Task.FromResult(new CoverArtBytes([(byte)imageId], "image/jpeg"));
        }
    }
}
