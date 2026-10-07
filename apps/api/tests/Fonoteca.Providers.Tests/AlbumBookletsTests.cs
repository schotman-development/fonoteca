using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.CoverArt;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// An album's booklets from the Cover Art Archive and Qobuz, built through the
/// real registrations and pointed at a stub socket answering with recorded
/// responses.
/// </summary>
/// <remarks>
/// The archive fixtures are the three editions of Ally Venable's <i>Texas
/// Honey</i> as the archive listed them on 2026-10-07: one with four raw
/// booklet scans among a back, a tray and two discs, one with a front alone,
/// and one with five edited booklet pages beside its fronts and discs. The
/// Qobuz ones are Andreas Staier's <i>Goldberg Variationen</i>, which comes
/// with a digital booklet. Only the image and PDF bytes are made up.
/// </remarks>
public sealed class AlbumBookletsTests : IDisposable
{
    private static readonly Mbid Raw = new(Guid.Parse("115f7e36-17ee-4ab5-9098-f2dcac596912"));

    private static readonly Mbid FrontOnly = new(Guid.Parse("66084cf3-8fc0-482c-b32f-0f1d52beafb2"));

    private static readonly Mbid Edited = new(Guid.Parse("f333a3df-77d1-4d68-bcc8-85fdffd1f3a6"));

    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(1);

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 booklet");

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AlbumToFind TexasHoney => new("Texas Honey", "Ally Venable", ["Ally Venable"], 2019, "710347126720", []);

    private static AlbumToFind Goldberg => new("Bach: Goldberg Variationen", "Andreas Staier", ["Andreas Staier"], 2010, null, []);

    /// <summary>
    /// Every edition is listed, and the one with the most booklet pages is kept
    /// whole and in its order — never its fronts, discs or spine.
    /// </summary>
    [Fact]
    public async Task TheEditionWithTheMostBookletPagesIsKeptWhole()
    {
        var (booklets, stub) = Build(Recorded());

        var found = await booklets.FindAsync(TexasHoney, [FrontOnly, Raw, Edited], Token);

        Assert.Equal(Edited, found.Edition);
        Assert.Equal(
            ["22600048246", "22600055134", "22600056038", "22600060572", "22600066908"],
            found.Pages.Select(page => page.SourceId).ToArray());
        Assert.All(found.Pages, page => Assert.Equal("image/jpeg", page.MediaType));

        // Three listings, five originals, and nothing of the packaging fetched.
        Assert.Equal(
            3,
            stub.Requests.Count(request => request.Uri.Host == "coverartarchive.org" && !request.Uri.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal)));
        Assert.Equal(5, stub.Requests.Count(request => request.Uri.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal)));

        // The shop does not sell this record, so nothing beyond the search.
        Assert.Empty(found.Pdfs);
        Assert.Null(found.QobuzAlbumId);
        Assert.DoesNotContain(stub.Requests, request => request.Uri.AbsolutePath.EndsWith("/album/get", StringComparison.Ordinal));
    }

    /// <summary>
    /// A raw scan is the same page unedited, so it counts only where an edition
    /// has nothing else; then it is the booklet.
    /// </summary>
    [Fact]
    public async Task RawScansAreTheBookletOnlyWhereThereIsNothingElse()
    {
        var (booklets, _) = Build(Recorded());

        var found = await booklets.FindAsync(TexasHoney, [FrontOnly, Raw], Token);

        Assert.Equal(Raw, found.Edition);
        Assert.Equal(
            ["23922607680", "23922609102", "23922610447", "23922611872"],
            found.Pages.Select(page => page.SourceId).ToArray());
    }

    /// <summary>On a tie the earlier edition keeps it — the caller lists the cover's first.</summary>
    [Fact]
    public async Task ATieGoesToTheEditionListedFirst()
    {
        var twin = new Mbid(Guid.Parse("00000000-0000-4000-8000-000000000001"));

        var (booklets, _) = Build(Recorded(listing: release => release == twin ? "caa-texas-honey-booklet.json" : null));

        Assert.Equal(twin, (await booklets.FindAsync(TexasHoney, [twin, Edited], Token)).Edition);
        Assert.Equal(Edited, (await booklets.FindAsync(TexasHoney, [Edited, twin], Token)).Edition);
    }

    /// <summary>
    /// The shop's digital booklet is found by the cover rule and fetched from
    /// their file host.
    /// </summary>
    [Fact]
    public async Task TheShopsDigitalBookletIsKept()
    {
        var (booklets, stub) = Build(Recorded());

        var found = await booklets.FindAsync(Goldberg, [], Token);

        Assert.Equal("0794881950324", found.QobuzAlbumId);

        var pdf = Assert.Single(found.Pdfs);
        Assert.Equal("82536", pdf.SourceId);
        Assert.Equal("application/pdf", pdf.MediaType);
        Assert.Equal(Pdf, pdf.Bytes);

        Assert.Contains(stub.Requests, request => request.Uri.AbsoluteUri == "https://static.qobuz.com/goodies/63/000082536.pdf");

        // And no edition to list, so the archive was never asked.
        Assert.DoesNotContain(stub.Requests, request => request.Uri.Host == "coverartarchive.org");
    }

    /// <summary>A file that is not a PDF is not kept, whatever it was offered as.</summary>
    [Fact]
    public async Task ADigitalBookletThatIsNotAPdfIsNotKept()
    {
        var (booklets, _) = Build(Recorded(goody: Bytes(Encoding.ASCII.GetBytes("<html>"), "application/pdf")));

        var found = await booklets.FindAsync(Goldberg, [], Token);

        Assert.Empty(found.Pdfs);
        Assert.Null(found.QobuzAlbumId);
        Assert.True(found.IsEmpty);
    }

    /// <summary>
    /// An original over the cap is skipped on its declared size, and a page
    /// whose type is not a picture or a PDF is skipped too; the rest are kept.
    /// </summary>
    [Fact]
    public async Task AnOriginalTooLargeOrOfAnotherTypeIsSkipped()
    {
        var (booklets, _) = Build(Recorded(original: id => id switch
        {
            "22600055134" => Declared(AlbumBooklets.MaxFileBytes + 1),
            "22600056038" => Bytes([1, 2, 3], "image/svg+xml"),
            _ => null,
        }));

        var found = await booklets.FindAsync(TexasHoney, [Edited], Token);

        Assert.Equal(
            ["22600048246", "22600060572", "22600066908"],
            found.Pages.Select(page => page.SourceId).ToArray());
    }

    /// <summary>
    /// A file that is gone or withheld is an answer about that file, never an
    /// outage: a dead link must not hold the stage at one album forever.
    /// </summary>
    [Fact]
    public async Task AGoneFileIsSkippedNotThrown()
    {
        var (booklets, _) = Build(Recorded(
            original: id => id switch
            {
                "22600048246" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                "22600055134" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "22600056038" => new HttpResponseMessage(HttpStatusCode.Gone),
                _ => null,
            },
            goody: new HttpResponseMessage(HttpStatusCode.NotFound)));

        var found = await booklets.FindAsync(Goldberg, [Edited], Token);

        Assert.Equal(["22600060572", "22600066908"], found.Pages.Select(page => page.SourceId).ToArray());
        Assert.Empty(found.Pdfs);
        Assert.Null(found.QobuzAlbumId);
    }

    /// <summary>A server error on a file is the source not answering, and is thrown.</summary>
    [Fact]
    public async Task AServerErrorOnAFileIsAnOutage()
    {
        var (booklets, _) = Build(Recorded(goody: new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => booklets.FindAsync(Goldberg, [], Token));
    }

    /// <summary>Within one edition the edited pages are the booklet; raw ones count only alone.</summary>
    [Fact]
    public void EditedPagesWinOverRawOnesWithinAnEdition()
    {
        CoverArtImage[] mixed =
        [
            new(1, Front: true, ["Front"], null),
            new(2, Front: false, ["Booklet", "Raw/Unedited"], null),
            new(3, Front: false, ["Booklet"], null),
            new(4, Front: false, ["Front", "Booklet"], null),
            new(5, Front: false, ["Medium"], null),
        ];

        Assert.Equal([3L, 4L], AlbumBooklets.Booklet(mixed).Select(image => image.Id).ToArray());
        Assert.Equal([2L], AlbumBooklets.Booklet([mixed[0], mixed[1], mixed[4]]).Select(image => image.Id).ToArray());
    }

    /// <summary>A digital booklet off their file host, or not a PDF by format, is never fetched.</summary>
    [Theory]
    [InlineData("static.qobuz.com", "evil.example.org")]
    [InlineData("\"file_format_id\":21", "\"file_format_id\":22")]
    public async Task OnlyAPdfOnTheirFileHostIsFetched(string from, string to)
    {
        var (booklets, stub) = Build(Recorded(album: json => json.Replace(from, to, StringComparison.Ordinal)));

        var found = await booklets.FindAsync(Goldberg, [], Token);

        Assert.Empty(found.Pdfs);
        Assert.DoesNotContain(stub.Requests, request => request.Uri.AbsolutePath.EndsWith(".pdf", StringComparison.Ordinal));
    }

    /// <summary>
    /// An archive file typed as a PDF is kept only where its bytes are one, and
    /// one that never says its size is cut off at the cap as it streams.
    /// </summary>
    [Fact]
    public async Task AnArchivePdfMustBeOneAndAnUndeclaredSizeIsCappedAsItStreams()
    {
        var (booklets, _) = Build(Recorded(original: id => id switch
        {
            "22600048246" => Bytes(Pdf, "application/pdf"),
            "22600055134" => Bytes(Encoding.ASCII.GetBytes("<html>"), "application/pdf"),
            "22600056038" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new Zeros(AlbumBooklets.MaxFileBytes + 1))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
                },
            },
            _ => null,
        }));

        var found = await booklets.FindAsync(TexasHoney, [Edited], Token);

        Assert.Equal(
            ["22600048246", "22600060572", "22600066908"],
            found.Pages.Select(page => page.SourceId).ToArray());
        Assert.Equal("application/pdf", found.Pages[0].MediaType);
    }

    /// <summary>
    /// One edition whose listing fails every time is left out while the others
    /// answer — the archive.org node behind one release of <i>Live at the
    /// Regal</i> did exactly that, and ended the stage on every run.
    /// </summary>
    [Fact]
    public async Task OneEditionTheArchiveCannotListIsLeftOut()
    {
        var (booklets, _) = Build(Recorded(listing: release => release == Raw ? "503" : null));

        var found = await booklets.FindAsync(TexasHoney, [Raw, Edited, FrontOnly], Token);

        Assert.Equal(Edited, found.Edition);
        Assert.Equal(5, found.Pages.Count);
    }

    /// <summary>
    /// The fullest edition failing to give its pages falls back to the next
    /// fullest; with nothing to fall back to, it is an outage.
    /// </summary>
    [Fact]
    public async Task AFullerEditionThatCannotGiveItsPagesFallsBackToTheNext()
    {
        var failing = Recorded(original: id => id == "22600048246"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : null);

        var (booklets, _) = Build(failing);

        var found = await booklets.FindAsync(TexasHoney, [Edited, Raw], Token);

        Assert.Equal(Raw, found.Edition);
        Assert.Equal(4, found.Pages.Count);

        await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => booklets.FindAsync(TexasHoney, [Edited, FrontOnly], Token));
    }

    /// <summary>An archive that cannot be asked is an outage, never "no booklet".</summary>
    [Fact]
    public async Task AnArchiveOutageIsThrownNotAnsweredAsNone()
    {
        var (booklets, _) = Build(Recorded(listing: _ => "503"));

        await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => booklets.FindAsync(TexasHoney, [Edited], Token));
    }

    /// <summary>Without Qobuz configured the shop is not asked at all.</summary>
    [Fact]
    public async Task WithoutQobuzTheShopIsNotAsked()
    {
        var (booklets, stub) = Build(Recorded(), options => options.UserAuthToken = string.Empty);

        var found = await booklets.FindAsync(Goldberg, [Edited], Token);

        Assert.Equal(5, found.Pages.Count);
        Assert.DoesNotContain(stub.Requests, request => request.Uri.Host.EndsWith("qobuz.com", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    /// <param name="listing">A fixture name for a release, "503" for an outage, or null for the recorded one.</param>
    /// <param name="original">An answer for an image id, or null for a JPEG.</param>
    /// <param name="goody">The answer for the digital booklet, or null for a PDF.</param>
    /// <param name="album">A change to the recorded <c>album/get</c>, or null for it verbatim.</param>
    private static Func<RecordedRequest, HttpResponseMessage> Recorded(
        Func<Mbid, string?>? listing = null,
        Func<string, HttpResponseMessage?>? original = null,
        HttpResponseMessage? goody = null,
        Func<string, string>? album = null) => request =>
    {
        var uri = request.Uri;

        if (uri.Host == "coverartarchive.org")
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            var release = new Mbid(Guid.Parse(parts[1]));

            if (parts.Length == 3)
            {
                var id = Path.GetFileNameWithoutExtension(parts[2]);
                return original?.Invoke(id) ?? Bytes([0xFF, 0xD8, 0xFF, .. Encoding.ASCII.GetBytes(id)], "image/jpeg");
            }

            var fixture = listing?.Invoke(release) ?? (release == Raw ? "caa-texas-honey-raw.json"
                : release == FrontOnly ? "caa-texas-honey-front.json"
                : release == Edited ? "caa-texas-honey-booklet.json"
                : null);

            return fixture switch
            {
                null => new HttpResponseMessage(HttpStatusCode.NotFound),
                "503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => Bytes(Encoding.UTF8.GetBytes(ReadFixture(fixture)), "application/json"),
            };
        }

        if (uri.AbsolutePath.EndsWith("/album/search", StringComparison.Ordinal))
        {
            return Bytes(Encoding.UTF8.GetBytes(ReadFixture("qobuz-search-goldberg.json")), "application/json");
        }

        if (uri.AbsolutePath.EndsWith("/album/get", StringComparison.Ordinal))
        {
            var json = ReadFixture("qobuz-album-goldberg.json");
            return Bytes(Encoding.UTF8.GetBytes(album?.Invoke(json) ?? json), "application/json");
        }

        if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.Ordinal)) return goody ?? Bytes(Pdf, "application/pdf");

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    private static HttpResponseMessage Bytes(byte[] body, string mediaType) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(mediaType) } },
        };

    /// <summary>A response claiming a size it never sends, which must be refused before it is read.</summary>
    private static HttpResponseMessage Declared(long length)
    {
        var response = Bytes([0xFF, 0xD8, 0xFF], "image/jpeg");
        response.Content.Headers.ContentLength = length;
        return response;
    }

    /// <summary>A body that never says how long it is, of zeros.</summary>
    private sealed class Zeros(long length) : Stream
    {
        private long _left = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, read);
            _left -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string ReadFixture(string name)
    {
        var assembly = typeof(AlbumBookletsTests).Assembly;
        var resource = $"{assembly.GetName().Name}.Responses.{name}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded fixture '{resource}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (IAlbumBooklets Booklets, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<QobuzOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddCoverArtArchive(options => options.Contact = "test@example.org");
        services.AddQobuz(options =>
        {
            options.AppId = "test-app";
            options.UserAuthToken = "test-token";
            options.MinimumRequestInterval = Gate;
            configure?.Invoke(options);
        });
        services.AddAlbumBooklets();

        foreach (var name in new[]
        {
            CoverArtArchiveOptions.HttpClientName,
            CoverArtArchiveOptions.OriginalsHttpClientName,
            QobuzOptions.HttpClientName,
            QobuzClient.ContentHttpClientName,
        })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        }

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<IAlbumBooklets>(), stub);
    }
}
