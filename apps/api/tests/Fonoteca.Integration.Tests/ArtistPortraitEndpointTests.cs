using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Where an artist's picture comes from, through the real host.
/// </summary>
/// <remarks>
/// <b>Three sources answer one URL, and the order is the thing under test.</b>
/// An upload, then <c>artist.*</c> on the artist's own shelf, then a redirect
/// to whatever a provider found. The shelf sits above the provider because it
/// is what every other player reading the same disk shows — which is the
/// complaint the whole feature began as, two catalogues on one machine
/// disagreeing about somebody's face.
///
/// The half worth testing through the host rather than in a unit is the shelf
/// lookup: it reads a real directory, and which directory is <i>theirs</i> is a
/// claim <see cref="ArtistShelf"/> makes about a name.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class ArtistPortraitEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly byte[] Uploaded = [0xFF, 0xD8, 0xFF, 0xE0, 1, 1, 1];
    private static readonly byte[] OnTheShelf = [0xFF, 0xD8, 0xFF, 0xDB, 2, 2, 2];

    private const string ProviderUrl =
        "https://static.qobuz.com/images/artists/covers/large/abc123.jpg";

    private const string BannerProviderUrl = "https://r2.theaudiodb.com/images/fanart/wide.jpg";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;
    private ArtistId _artist;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-portrait-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        using var warm = _factory.CreateClient();

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var artist = new Artist
        {
            Id = ArtistId.New(),
            Name = "Janine Jansen",
            SortName = "Jansen, Janine",
            PortraitUrl = ProviderUrl,
            PortraitLookupUtc = DateTimeOffset.UtcNow,
            BannerUrl = BannerProviderUrl,
            BannerLookupUtc = DateTimeOffset.UtcNow,
        };

        db.Artists.Add(artist);
        await db.SaveChangesAsync(Token);

        _artist = artist.Id;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// With nothing on disk, the provider's own URL is where the browser is sent.
    /// </summary>
    /// <remarks>
    /// A redirect rather than a proxy, so the page has one <c>src</c> whatever
    /// the answer turns out to be — and sized on the way out, because a grid of
    /// unsized Qobuz <c>large</c> files was measured at 39.7 MB of images drawn
    /// into 112px circles.
    /// </remarks>
    [Fact]
    public async Task WithNoPictureOnDiskTheProviderIsWhereTheBrowserIsSent()
    {
        using var client = Client();

        var response = await client.GetAsync(Portrait(), Token);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "https://static.qobuz.com/images/artists/covers/small/abc123.jpg",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task AWiderBoxAsksForABiggerRendition()
    {
        using var client = Client();

        var response = await client.GetAsync(Portrait("?width=600"), Token);

        Assert.Equal(
            "https://static.qobuz.com/images/artists/covers/medium/abc123.jpg",
            response.Headers.Location?.ToString());
    }

    /// <summary>
    /// A picture on the artist's own shelf outranks the provider's.
    /// </summary>
    /// <remarks>
    /// The whole point. Once the tag write has put a file beside their records,
    /// this page and every other player on the disk are reading the same bytes.
    /// </remarks>
    [Fact]
    public async Task TheShelfOutranksTheProvider()
    {
        Shelve("Janine Jansen", "artist.jpg", OnTheShelf);

        using var client = Client();

        var response = await client.GetAsync(Portrait(), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(OnTheShelf, await response.Content.ReadAsByteArrayAsync(Token));

        // Revalidated every time, so a picture replaced on disk shows on the
        // next view rather than whenever a browser next feels like asking.
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.NotNull(response.Headers.ETag);
    }

    /// <summary>
    /// Somebody else's shelf is not read, however much of this artist it holds.
    /// </summary>
    /// <remarks>
    /// The measured shape, asked from the reading side: 47 of her 54 files sit
    /// under <c>Johann Sebastian Bach</c> and <c>Antonio Vivaldi</c>. A lookup
    /// that took any folder holding her music would put a composer's picture on
    /// her page — and it would do it for every classical artist here.
    /// </remarks>
    [Fact]
    public async Task AComposersShelfIsNotReadForThePerformer()
    {
        Shelve("Johann Sebastian Bach", "artist.jpg", OnTheShelf);

        using var client = Client();

        var response = await client.GetAsync(Portrait(), Token);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    /// <summary>
    /// A file that is not an image is not a portrait, whatever it is called.
    /// </summary>
    [Fact]
    public async Task ATextFileCalledArtistIsNotAPicture()
    {
        Shelve("Janine Jansen", "artist.txt", "not a picture"u8.ToArray());

        using var client = Client();

        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(Portrait(), Token)).StatusCode);
    }

    /// <summary>
    /// An upload outranks both, and takes effect without a tag write.
    /// </summary>
    /// <remarks>
    /// A person's answer against a provider's (rule 4), and it has to be
    /// instant: putting the file into the library is the tag write's job, and
    /// that is a button. A page that showed the old picture until somebody
    /// pressed it would read as an upload that did not work.
    /// </remarks>
    [Fact]
    public async Task AnUploadOutranksTheShelfAndTheProvider()
    {
        Shelve("Janine Jansen", "artist.jpg", OnTheShelf);

        using var client = Client();

        Assert.Equal(HttpStatusCode.NoContent, (await Upload(client, "image/jpeg")).StatusCode);

        var response = await client.GetAsync(Portrait(), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Uploaded, await response.Content.ReadAsByteArrayAsync(Token));
    }

    /// <summary>
    /// Deleting the upload uncovers whatever was under it, unharmed.
    /// </summary>
    /// <remarks>
    /// Rule 4's payoff: the upload is stored <i>beside</i>
    /// <c>Artists.PortraitUrl</c> rather than over it, so an undo is a delete
    /// and nothing had to be remembered to make it possible.
    ///
    /// <b>What is underneath is not always the provider.</b> With nothing on the
    /// shelf it is, which is this case; where a tag write has already copied the
    /// upload beside the artist's records, that file is — and it stays until
    /// tags are written again, because this endpoint touches the catalogue and
    /// never the library. <see cref="TheShelfSurvivesTheUploadBeingRemoved"/>
    /// pins that half, and the button says so rather than claiming otherwise.
    /// </remarks>
    [Fact]
    public async Task RemovingTheUploadUncoversTheProvidersPicture()
    {
        using var client = Client();

        await Upload(client, "image/jpeg");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync(Portrait(), Token)).StatusCode);

        var response = await client.GetAsync(Portrait(), Token);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        // And the row is gone rather than emptied.
        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.False(await db.ArtistImages.AnyAsync(Token));
    }

    /// <summary>
    /// Removing an upload does not reach into the library to undo a tag write.
    /// </summary>
    /// <remarks>
    /// The honest half of the sentence above. A person who uploaded a picture,
    /// wrote tags, then changed their mind has two things to undo and this
    /// endpoint owns one of them — putting a file into somebody's music is the
    /// tag write's job, gated by <c>AllowFileMutation</c>, and a delete that
    /// quietly rewrote the library would be exactly the automatic file mutation
    /// this application is built not to do.
    /// </remarks>
    [Fact]
    public async Task TheShelfSurvivesTheUploadBeingRemoved()
    {
        Shelve("Janine Jansen", "artist.jpg", OnTheShelf);

        using var client = Client();

        await Upload(client, "image/jpeg");
        await client.DeleteAsync(Portrait(), Token);

        var response = await client.GetAsync(Portrait(), Token);

        // The shelf, not the provider: the file is still there and still theirs.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OnTheShelf, await response.Content.ReadAsByteArrayAsync(Token));
    }

    /// <summary>
    /// An SVG is refused, because it is a document that runs script.
    /// </summary>
    /// <remarks>
    /// The allowlist the cover upload is held to, applied here for a second
    /// reason: this picture is not only served back, it is written into the
    /// library, where every other player on the machine will open it.
    /// </remarks>
    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task OnlyARasterImageIsAccepted(string mediaType)
    {
        using var client = Client();

        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(client, mediaType)).StatusCode);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.False(await db.ArtistImages.AnyAsync(Token));
    }

    [Fact]
    public async Task AnArtistNothingHasFoundAPictureOfAnswersNothing()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var artist = await db.Artists.SingleAsync(row => row.Id == _artist, Token);
            artist.PortraitUrl = null;
            await db.SaveChangesAsync(Token);
        }

        using var client = Client();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Portrait(), Token)).StatusCode);
    }

    /// <summary>
    /// A picture a person picked is the one the browser is sent to.
    /// </summary>
    /// <remarks>
    /// <b>The profile editor does not write <c>PortraitUrl</c>.</b> It stores
    /// the correction in <c>EditsJson</c> beside the provider's answer, which
    /// is rule 4 and is right — but it means every reader has to apply the
    /// edit, and a reader that forgets makes picking a different picture do
    /// nothing whatsoever. Measured against a running instance before this was
    /// fixed: the pick was recorded, the artist list showed it, and this
    /// endpoint went on redirecting to the provider's.
    /// </remarks>
    [Fact]
    public async Task APickedPictureIsWhatIsServed()
    {
        await EditAsync("https://example.invalid/picked.jpg");

        using var client = Client();

        var response = await client.GetAsync(Portrait(), Token);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.invalid/picked.jpg", response.Headers.Location?.ToString());
    }

    /// <summary>
    /// A shelf reached through a directory symlink is not this library's.
    /// </summary>
    /// <remarks>
    /// <b>Lexical containment is not containment</b>, which this codebase has
    /// already paid for once: <c>ln -s /etc secretdir</c> inside the library is
    /// under the root by every string comparison and somewhere else on the
    /// disk. The rule lives in <c>FileSystemAudioFileStore.Resolve</c> so that
    /// no endpoint can forget it — and this endpoint forgot it, by listing the
    /// root directly and reading whatever the matched folder turned out to be.
    /// It served bytes from outside the library, under a 200.
    /// </remarks>
    [Fact]
    public async Task AShelfThatIsASymlinkOutOfTheLibraryIsNotRead()
    {
        var outside = Directory.CreateTempSubdirectory("fonoteca-portrait-outside-").FullName;
        var link = Path.Combine(_root, "Janine Jansen");

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outside, "artist.jpg"), OnTheShelf, Token);
            Directory.CreateSymbolicLink(link, outside);

            using var client = Client();

            var response = await client.GetAsync(Portrait(), Token);

            // The provider, not the bytes on the other end of the link.
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>
    /// The banner is the same three sources under a different name on disk.
    /// </summary>
    /// <remarks>
    /// <c>backdrop.*</c> rather than <c>artist.*</c>, which is the only thing
    /// that differs between the two kinds — and the reason the file name lives
    /// on <see cref="ArtistImageKind"/> rather than at four call sites.
    /// </remarks>
    [Fact]
    public async Task TheBannerIsReadFromBackdropOnTheShelf()
    {
        Shelve("Janine Jansen", "backdrop.jpg", OnTheShelf);

        using var client = Client();

        var response = await client.GetAsync(Banner(), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OnTheShelf, await response.Content.ReadAsByteArrayAsync(Token));
    }

    /// <summary>
    /// The two kinds do not read each other's files.
    /// </summary>
    /// <remarks>
    /// The glob is the whole separation, so this is the test that fails the day
    /// somebody threads the wrong kind through: a shelf holding only a portrait
    /// has no banner, and the request falls through to the provider.
    /// </remarks>
    [Fact]
    public async Task APortraitIsNotABanner()
    {
        Shelve("Janine Jansen", "artist.jpg", OnTheShelf);

        using var client = Client();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Portrait(), Token)).StatusCode);

        var banner = await client.GetAsync(Banner(), Token);

        Assert.Equal(HttpStatusCode.Found, banner.StatusCode);
        Assert.Equal(BannerProviderUrl, banner.Headers.Location?.ToString());
    }

    /// <summary>
    /// An artist can hold an uploaded portrait and an uploaded banner at once.
    /// </summary>
    /// <remarks>
    /// The reason the kind is part of the key rather than a second pair of
    /// columns: one row per picture, and uploading one does not disturb the
    /// other.
    /// </remarks>
    [Fact]
    public async Task BothPicturesCanBeUploadedAndAreKeptApart()
    {
        using var client = Client();

        await Upload(client, "image/jpeg");
        await Upload(client, "image/jpeg", Banner());

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(
            ["banner", "portrait"],
            await db.ArtistImages
                .Where(row => row.ArtistId == _artist)
                .Select(row => row.Kind)
                .OrderBy(kind => kind)
                .ToListAsync(Token));

        // And removing one leaves the other where it is.
        await client.DeleteAsync(Portrait(), Token);

        Assert.Equal(
            "banner",
            await db.ArtistImages.Where(row => row.ArtistId == _artist)
                .Select(row => row.Kind)
                .SingleAsync(Token));
    }

    [Fact]
    public async Task AnArtistTheCatalogueHasNeverHeardOfIsNotFound()
    {
        using var client = Client();

        var response = await client.GetAsync(
            new Uri($"/api/catalogue/artists/{Guid.CreateVersion7()}/portrait", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient Client() =>
        _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private Uri Portrait(string query = "") =>
        new($"/api/catalogue/artists/{_artist.Value}/portrait{query}", UriKind.Relative);

    private Uri Banner(string query = "") =>
        new($"/api/catalogue/artists/{_artist.Value}/banner{query}", UriKind.Relative);

    private async Task<HttpResponseMessage> Upload(
        HttpClient client,
        string mediaType,
        Uri? to = null)
    {
        var content = new ByteArrayContent(Uploaded);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        return await client.PostAsync(to ?? Portrait(), content, Token);
    }

    /// <summary>A person's correction, as the profile editor stores one.</summary>
    private async Task EditAsync(string portrait)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var artist = await db.Artists.SingleAsync(row => row.Id == _artist, Token);

        artist.EditsJson = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["portrait"] = portrait });

        await db.SaveChangesAsync(Token);
    }

    private void Shelve(string folder, string name, byte[] bytes)
    {
        var shelf = Path.Combine(_root, folder);

        Directory.CreateDirectory(shelf);
        File.WriteAllBytes(Path.Combine(shelf, name), bytes);
    }
}
