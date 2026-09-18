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
            builder.ConfigureTestServices(services => services.AddSingleton<ICoverArtArchive>(_archive));
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

    private sealed class StubArchive : ICoverArtArchive
    {
        public int Downloads { get; private set; }

        public Task<IReadOnlyList<CoverArtImage>> ListAsync(
            Mbid release,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoverArtImage>>(
            [
                new(Spread, Front: true, ["Front"], "Cover"),
                new(Square, Front: false, ["Front"], "Front"),
            ]);

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
