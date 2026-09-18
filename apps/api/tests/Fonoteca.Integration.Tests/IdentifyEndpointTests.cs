using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The Identify screen's queue and folder reads, through the real host and real tags.
/// </summary>
/// <remarks>
/// What is under test is the grouping and the ordering, because both are
/// invisible when wrong: a folder split in two reads as two albums, a folder
/// whose tags already name its release sorted to the back is an afternoon spent
/// on the hard ones first, and an answered file counted as open is a File
/// button that files nothing.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class IdentifyEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Tagged = "Tagged Artist/Small Album";
    private const string Untagged = "Untagged Artist/Big Album";
    private const string Release = "297ebf3a-b7ea-4ee7-8898-b2b00d477b8a";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-identify-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        using var warm = _factory.CreateClient();

        if (Corpus.IsAvailable) Place();

        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task FoldersWhoseTagsNameOneReleaseComeFirstAndDiscsAreOneFolder()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH.");

        using var client = _factory!.CreateClient();

        var queue = await client.GetFromJsonAsync<IdentifyQueueResponse>(
            new Uri("/api/catalogue/matching/folders/queue", UriKind.Relative), Token);

        Assert.NotNull(queue);

        // Two folders, not three: the untagged album's CD 1 and CD 2 are one
        // question. And not four: a file at the library root has no album folder.
        Assert.Equal([Tagged, Untagged], queue.Items.Select(item => item.Folder));

        // Smaller, and first, because its tags already say what it is.
        Assert.True(queue.Items[0].TagsNameRelease);
        Assert.Equal(2, queue.Items[0].Open);

        Assert.False(queue.Items[1].TagsNameRelease);
        Assert.Equal(3, queue.Items[1].Open);
    }

    [Fact]
    public async Task AFolderListsItsOpenFilesWithWhatTheirTagsSay()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH.");

        var folder = await FolderAsync(Tagged);

        // The placed file counts toward the folder and is not offered.
        Assert.Equal(3, folder.Files);
        Assert.Equal(2, folder.Open);
        Assert.Equal(2, folder.Items.Count);
        Assert.DoesNotContain(folder.Items, item => item.Name == "03 Placed.flac");

        var first = folder.Items[0];
        Assert.Equal("Tagged Title", first.Title);
        Assert.Equal(4, first.Track);
        Assert.Equal("Unknown", first.Reason);
        Assert.Equal(61_500, first.LengthMs);

        // No TITLE tag, so the filename less its number.
        Assert.Equal("Second Song", folder.Items[1].Title);

        Assert.Equal("Small Album", folder.Tags.Album);
        Assert.Equal(Guid.Parse(Release), folder.Tags.Release);
        Assert.Equal(2, folder.Tags.Agreeing);
    }

    [Fact]
    public async Task DiscFoldersAreReportedUnderTheAlbum()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH.");

        var folder = await FolderAsync(Untagged);

        Assert.Equal(["CD 1", "CD 1", "CD 2"], folder.Items.Select(item => item.SubFolder));

        // No DISC tags, so the disc folders number the discs.
        Assert.Equal([1, 1, 2], folder.Items.Select(item => item.Disc));
        Assert.Equal(2, folder.Tags.Discs);
        Assert.Null(folder.Tags.Release);
    }

    [Fact]
    public async Task AFolderTheCatalogueDoesNotHoldIsRefused()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync(
            new Uri(
                "/api/catalogue/matching/folders/identify?folder=Nobody/Nothing", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<IdentifyFolderResponse> FolderAsync(string folder)
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetFromJsonAsync<IdentifyFolderResponse>(
            new Uri(
                $"/api/catalogue/matching/folders/identify?folder={Uri.EscapeDataString(folder)}",
                UriKind.Relative),
            Token);

        Assert.NotNull(response);
        return response;
    }

    private void Place()
    {
        var tagged = Directory.CreateDirectory(Path.Combine(_root, "Tagged Artist", "Small Album"));

        var first = Path.Combine(tagged.FullName, "01 Not The Title.flac");
        File.Copy(Corpus.Flac, first);
        Tag(first, title: "Tagged Title", track: 4);

        var second = Path.Combine(tagged.FullName, "02 Second Song.flac");
        File.Copy(Corpus.Flac, second);
        Tag(second, title: null, track: null);

        File.Copy(Corpus.Flac, Path.Combine(tagged.FullName, "03 Placed.flac"));

        foreach (var (disc, name) in new[] { ("CD 1", "01 A.flac"), ("CD 1", "02 B.flac"), ("CD 2", "01 C.flac") })
        {
            var directory = Directory.CreateDirectory(
                Path.Combine(_root, "Untagged Artist", "Big Album", disc));
            File.Copy(Corpus.Flac, Path.Combine(directory.FullName, name));
        }

        File.Copy(Corpus.Flac, Path.Combine(_root, "Loose.flac"));
    }

    private static void Tag(string path, string? title, uint? track)
    {
        using var file = TagLib.File.Create(path);

        if (title is not null) file.Tag.Title = title;
        if (track is { } number) file.Tag.Track = number;

        file.Tag.Album = "Small Album";
        file.Tag.MusicBrainzReleaseId = Release;
        file.Save();
    }

    private async Task SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        db.MediaFiles.AddRange(
            Open($"{Tagged}/01 Not The Title.flac", TimeSpan.FromSeconds(61.5)),
            Open($"{Tagged}/02 Second Song.flac", TimeSpan.FromSeconds(12)),
            Placed($"{Tagged}/03 Placed.flac"),
            Open($"{Untagged}/CD 1/01 A.flac", TimeSpan.FromSeconds(12)),
            Open($"{Untagged}/CD 1/02 B.flac", TimeSpan.FromSeconds(12)),
            Open($"{Untagged}/CD 2/01 C.flac", TimeSpan.FromSeconds(12)),
            Open("Loose.flac", TimeSpan.FromSeconds(12)));

        await db.SaveChangesAsync(Token);
    }

    private static MediaFile Open(string path, TimeSpan fingerprinted) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 1_000_000,
        LastModifiedUtc =
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        FingerprintDuration = fingerprinted,
        AcoustIdOutcome = AcoustIdOutcome.Unknown,
        AcoustIdCheckedUtc =
            DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
    };

    private static MediaFile Placed(string path) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 1_000_000,
        LastModifiedUtc =
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        AcoustIdOutcome = AcoustIdOutcome.Identified,
        AcoustIdCheckedUtc =
            DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
    };
}
