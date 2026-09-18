using System.Net;
using System.Net.Http.Json;
using System.Text;
using Fonoteca.Api.Endpoints;
using Fonoteca.Fixtures;
using Fonoteca.Api.Library;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The file endpoints through the real host, real routing and real serialisation.
/// </summary>
/// <remarks>
/// <see cref="FileManagerTests"/> proves the operations are correct; this proves
/// they are reachable, and it exists because the service-level tests could not
/// have caught the two things that actually broke.
///
/// <b>The upload's content type.</b> <c>Accepts&lt;Stream&gt;("application/octet-stream")</c>
/// is not only documentation — it is a <c>Consumes</c> constraint, so the
/// endpoint answers <b>415</b> to anything else. A browser's
/// <c>fetch(url, { body: file })</c> sends the file's own type, <c>audio/flac</c>,
/// which means every real upload failed while a test that called
/// <c>SaveAsync</c> directly passed. Driving the page in a browser did not catch
/// it either: the probe aborted the route before it reached the server.
///
/// <b>A body that is missing its members.</b> <c>{}</c> deserialises every field
/// to null, and null reaching the service is a 500 on the endpoint that moves
/// albums.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class FileEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _trash = string.Empty;
    private string _connectionString = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        _root = Directory.CreateTempSubdirectory("fonoteca-files-api-").FullName;
        _trash = Directory.CreateTempSubdirectory("fonoteca-files-bin-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:TrashPath", _trash);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        foreach (var directory in new[] { _root, _trash })
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheListingIsReachableAndSurvivesJson()
    {
        WriteFile("Brahms/Symphony 1/01 Allegro.flac");
        WriteFile("Brahms/cover.jpg");

        using var client = _factory!.CreateClient();

        var listing = await client.GetFromJsonAsync<FolderListing>(
            new Uri("/api/files?path=Brahms", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.NotNull(listing);
        Assert.True(listing.Exists);
        Assert.Equal(string.Empty, listing.Parent);
        Assert.Equal(["Symphony 1", "cover.jpg"], listing.Entries.Select(e => e.Name).ToArray());
    }

    /// <summary>The one a browser actually sends.</summary>
    [Fact]
    public async Task AnUploadSentAsTheDeclaredContentTypeIsAccepted()
    {
        using var client = _factory!.CreateClient();
        using var body = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"));

        body.Headers.ContentType = new("application/octet-stream");

        using var response = await client.PostAsync(
            new Uri("/api/files/upload?folder=Brahms&name=01%20Allegro.flac", UriKind.Relative),
            body,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(_root, "Brahms", "01 Allegro.flac")));
    }

    /// <summary>
    /// And the one it sends by accident, pinned so the constraint cannot tighten
    /// under the client without a test saying so.
    /// </summary>
    [Fact]
    public async Task AnUploadSentAsItsOwnMediaTypeIsRefusedRatherThanMisfiled()
    {
        using var client = _factory!.CreateClient();
        using var body = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"));

        body.Headers.ContentType = new("audio/flac");

        using var response = await client.PostAsync(
            new Uri("/api/files/upload?folder=Brahms&name=01%20Allegro.flac", UriKind.Relative),
            body,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "Brahms", "01 Allegro.flac")));
    }

    [Fact]
    public async Task AnUploadDoesNotRewriteTheNameOfTheFolderItLandsIn()
    {
        // The colon: seven album folders in the target library have one, and it
        // is what StagedFileName.Segment removes.
        const string folder = "Johannes Brahms/Essential Brahms, Volume 1: 50 Tracks";

        Directory.CreateDirectory(Path.Combine(_root, folder.Replace('/', Path.DirectorySeparatorChar)));

        using var client = _factory!.CreateClient();
        using var body = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"));

        body.Headers.ContentType = new("application/octet-stream");

        using var response = await client.PostAsync(
            new Uri(
                $"/api/files/upload?folder={Uri.EscapeDataString(folder)}&name=01.flac",
                UriKind.Relative),
            body,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(
            ["Essential Brahms, Volume 1: 50 Tracks"],
            Directory.EnumerateDirectories(Path.Combine(_root, "Johannes Brahms"))
                .Select(Path.GetFileName)
                .ToArray());
    }

    [Fact]
    public async Task ARequestMissingItsFieldsIsRejectedRatherThanFaulting()
    {
        using var client = _factory!.CreateClient();

        foreach (var route in new[] { "/api/files/trash", "/api/files/move" })
        {
            using var response = await client.PostAsJsonAsync(
                new Uri(route, UriKind.Relative),
                new { },
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task APathOutsideTheLibraryIsRejectedByEveryRoute()
    {
        using var client = _factory!.CreateClient();

        using var listing = await client.GetAsync(
            new Uri("/api/files?path=../escape", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, listing.StatusCode);

        using var trash = await client.PostAsJsonAsync(
            new Uri("/api/files/trash", UriKind.Relative),
            new { paths = new[] { "../escape" } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, trash.StatusCode);

        // Not the empty-string version this used to print.
        var detail = await trash.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("''", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedMoveIsA409WithSomethingToRead()
    {
        WriteFile("Brahms/Symphony 1/01 Allegro.flac");
        WriteFile("Brahms/Symphony No. 1/01 Allegro.flac");

        using var client = _factory!.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/files/move", UriKind.Relative),
            new { from = "Brahms/Symphony 1", to = "Brahms/Symphony No. 1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var detail = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("already exists", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The rename opens a transaction, and the real context retries.</b>
    /// </summary>
    /// <remarks>
    /// <c>NpgsqlRetryingExecutionStrategy</c> refuses a transaction it did not
    /// open, so every rename answered <b>500</b> — "does not support
    /// user-initiated transactions" — while <see cref="FileManagerTests"/> stayed
    /// green throughout, because it builds its own context and never turns
    /// <c>EnableRetryOnFailure</c> on. Only the host's own registration has the
    /// configuration that breaks, which is exactly what this class is for.
    ///
    /// The row is what is being asserted, not the rename: carrying it is the
    /// whole reason this is an endpoint rather than a <c>mv</c> and a rescan.
    /// </remarks>
    [Fact]
    public async Task RenamingThroughTheEndpointCarriesTheCatalogueRowWithTheFile()
    {
        WriteFile("Brahms/Symphony 1/01 Allegro.flac");

        // The client first: the host migrates on start, so there is nothing to
        // seed into until it has.
        using var client = _factory!.CreateClient();

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            seed.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "Brahms/Symphony 1/01 Allegro.flac",
                SizeBytes = 16,
                LastModifiedUtc = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/files/move", UriKind.Relative),
            new { from = "Brahms/Symphony 1", to = "Brahms/Symphony No. 1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(File.Exists(
            Path.Combine(_root, "Brahms", "Symphony No. 1", "01 Allegro.flac")));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(
            ["Brahms/Symphony No. 1/01 Allegro.flac"],
            await db.MediaFiles
                .AsNoTracking()
                .Select(file => file.Path)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <b>The reason the media type comes from an allowlist.</b>
    /// </summary>
    /// <remarks>
    /// This endpoint serves bytes out of a directory nobody vetted, on the same
    /// origin as the application. An <c>.html</c> echoed back as
    /// <c>text/html</c> is script running on the page that lists it, put there
    /// by dropping a file into a music folder.
    /// </remarks>
    [Theory]
    [InlineData("Brahms/notes.html")]
    [InlineData("Brahms/cover.svg")]
    public async Task AFileThatCouldRunScriptIsServedAsAnOpaqueDownload(string path)
    {
        WriteFile(path);

        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri($"/api/files/content?path={Uri.EscapeDataString(path)}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);

        // And the browser may not overrule it by sniffing.
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task AnImageIsServedInlineAsItself()
    {
        WriteFile("Brahms/cover.jpg");

        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/files/content?path=Brahms%2Fcover.jpg", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);

        // No download name, so no Content-Disposition, so the browser renders it.
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    /// <summary>
    /// What lets an &lt;audio&gt; element seek into the middle of a 400 MB FLAC
    /// without reading the 399 before it.
    /// </summary>
    [Fact]
    public async Task ContentAnswersARangeRequestWithThatRange()
    {
        WriteFile("Brahms/01 Allegro.flac", new string('x', 4096));

        using var client = _factory!.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/files/content?path=Brahms%2F01%20Allegro.flac", UriKind.Relative));

        request.Headers.Range = new(1024, 2047);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1024, response.Content.Headers.ContentRange?.From);
        Assert.Equal(4096, response.Content.Headers.ContentRange?.Length);
        Assert.Equal(1024, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task ContentRefusesAPathOutsideTheLibraryAndAPathThatIsNotThere()
    {
        using var client = _factory!.CreateClient();

        using var outside = await client.GetAsync(
            new Uri("/api/files/content?path=..%2F..%2Fetc%2Fpasswd", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);

        using var missing = await client.GetAsync(
            new Uri("/api/files/content?path=Brahms%2Fgone.flac", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>A folder's picture is a file inside it, and 404 is ordinary.</summary>
    [Fact]
    public async Task FolderArtIsTheCoverItHoldsOrNothing()
    {
        WriteFile("Brahms/Symphony 1/cover.jpg");
        WriteFile("Brahms/Symphony 2/01 Allegro.flac");

        using var client = _factory!.CreateClient();

        using var found = await client.GetAsync(
            new Uri("/api/files/art?path=Brahms%2FSymphony%201", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal("image/jpeg", found.Content.Headers.ContentType?.MediaType);

        using var none = await client.GetAsync(
            new Uri("/api/files/art?path=Brahms%2FSymphony%202", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    /// <summary>
    /// Most rips carry the sleeve inside every track and never write it beside
    /// them, so without this the commonest album folder draws a monogram.
    /// </summary>
    [Fact]
    public async Task AFolderWithNoCoverFileFallsBackToTheArtInsideItsMusic()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var flac = Path.Combine(_root, "Brahms", "Symphony 1", "01 Allegro.flac");

        Directory.CreateDirectory(Path.GetDirectoryName(flac)!);
        File.Copy(Corpus.FlacWithArtwork, flac);

        // A cover file would win, so there deliberately is not one.
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/files/art?path=Brahms%2FSymphony%201", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(
            "image/",
            response.Content.Headers.ContentType?.MediaType,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The art path walked around the allowlist, and it was a live XSS.</b>
    /// </summary>
    /// <remarks>
    /// An embedded cover has no filename — only a MIME string a tagger wrote
    /// inside the file — so the extension allowlist never saw it. The check was
    /// <c>StartsWith("image/")</c>, which passes <c>image/svg+xml</c>: a
    /// document that runs script, served from this application's origin, put
    /// there by one PICTURE block. <c>nosniff</c> is no defence, because it
    /// stops a browser guessing a type rather than honouring the one it was
    /// given.
    /// </remarks>
    [Fact]
    public async Task EmbeddedArtDeclaringAScriptableTypeIsNotServedAsOne()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var flac = Path.Combine(_root, "Brahms", "01 Allegro.flac");

        Directory.CreateDirectory(Path.GetDirectoryName(flac)!);
        File.Copy(Corpus.FlacWithArtwork, flac);

        // Re-declare the embedded picture as SVG, which is what a hostile or
        // simply broken tagger leaves behind. Written with TagLib# because that
        // is the library the describer reads pictures with, so this is the
        // claim it will actually see.
        using (var tagged = TagLib.File.Create(flac))
        {
            tagged.Tag.Pictures =
            [
                new TagLib.Picture(TagLib.ByteVector.FromString("<svg/>", TagLib.StringType.UTF8))
                {
                    MimeType = "image/svg+xml",
                    Type = TagLib.PictureType.FrontCover,
                },
            ];

            tagged.Save();
        }

        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/files/art?path=Brahms%2F01%20Allegro.flac", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// <b>Containment is lexical and a symlink is not.</b>
    /// </summary>
    /// <remarks>
    /// <c>ln -s /etc secretdir</c> inside the library produces a path that is
    /// under the root by every string comparison and is <c>/etc</c> on the disk.
    /// Harmless while nothing served bytes — the walk refuses to recurse through
    /// a link — and a way to read and list arbitrary directories the moment a
    /// file manager existed.
    /// </remarks>
    [Fact]
    public async Task ADirectorySymlinkIsNotAWayOutOfTheLibrary()
    {
        var outside = Directory.CreateTempSubdirectory("fonoteca-outside-").FullName;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(outside, "secret.txt"),
                "not yours",
                TestContext.Current.CancellationToken);

            Directory.CreateDirectory(Path.Combine(_root, "Brahms"));
            Directory.CreateSymbolicLink(Path.Combine(_root, "Brahms", "escape"), outside);

            using var client = _factory!.CreateClient();

            foreach (var route in new[]
            {
                "/api/files?path=Brahms%2Fescape",
                "/api/files/content?path=Brahms%2Fescape%2Fsecret.txt",
                "/api/files/detail?path=Brahms%2Fescape%2Fsecret.txt",
                "/api/files/art?path=Brahms%2Fescape",
            })
            {
                using var response = await client.GetAsync(
                    new Uri(route, UriKind.Relative),
                    TestContext.Current.CancellationToken);

                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>
    /// A symlinked <i>file</i> stays reachable, because the walk catalogues
    /// those deliberately and the passes hold rows for them.
    /// </summary>
    [Fact]
    public async Task AFileSymlinkInsideTheLibraryStillReads()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Brahms"));
        WriteFile("Brahms/real.log", "Exact Audio Copy");

        Directory.CreateSymbolicLink(
            Path.Combine(_root, "Brahms", "link.log"),
            Path.Combine(_root, "Brahms", "real.log"));

        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/files/content?path=Brahms%2Flink.log", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A null byte is a malformed request, not a fault in this process.</summary>
    [Fact]
    public async Task ANullByteInAPathIsRejectedRatherThanFaulting()
    {
        using var client = _factory!.CreateClient();

        foreach (var route in new[]
        {
            "/api/files?path=Brahms%2Frip%00.log",
            "/api/files/content?path=Brahms%2Frip%00.log",
            "/api/files/detail?path=Brahms%2Frip%00.log",
        })
        {
            using var response = await client.GetAsync(
                new Uri(route, UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>Six album folders in the target library spell it `Front.jpg`.</summary>
    [Fact]
    public async Task AFolderCoverIsFoundWhateverItsCase()
    {
        WriteFile("Brahms/Symphony 1/Front.JPG");

        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/files/art?path=Brahms%2FSymphony%201", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// A folder's cover is chosen through the same allowlist, so a
    /// <c>cover.svg</c> is not a folder's picture.
    /// </summary>
    [Fact]
    public async Task AFolderCoverThatCouldRunScriptIsNotAFolderCover()
    {
        WriteFile("Brahms/Symphony 1/cover.svg");

        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/files/art?path=Brahms%2FSymphony%201", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Keyed on a path, so it answers for a file no scan has seen — which is the
    /// whole reason it is not the catalogue's file endpoint.
    /// </summary>
    [Fact]
    public async Task DetailDescribesAFileWithNoCatalogueRow()
    {
        WriteFile("Brahms/rip.log", "Exact Audio Copy");

        using var client = _factory!.CreateClient();

        var detail = await client.GetFromJsonAsync<FilePreviewResponse>(
            new Uri("/api/files/detail?path=Brahms%2Frip.log", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal("Text", detail.Kind);
        Assert.Equal("text/plain", detail.MediaType);
        Assert.False(detail.IsAudio);

        // Nothing was decoded, so nothing is claimed about the audio.
        Assert.Null(detail.Audio);
        Assert.Empty(detail.Tags);
    }

    private void WriteFile(string relativePath, string content = "not really audio")
    {
        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }
}
