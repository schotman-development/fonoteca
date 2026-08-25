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
/// Turning a folder into a MusicBrainz release editor form, through the real host.
/// </summary>
/// <remarks>
/// Its own class and its own database because, like the file-detail screen, this
/// reads <b>bytes</b> — the catalogue holds no track titles, so the only place
/// they can come from is the files.
///
/// What is under test is not "does it call the describer". It is the four ways
/// this can be quietly wrong once somebody has pressed Save on musicbrainz.org,
/// where a mistake is a public edit somebody else has to correct:
///
/// <list type="bullet">
/// <item>the field names and their indices, which are MusicBrainz's own
/// vocabulary and mean nothing to anything in this codebase — a track seeded as
/// <c>mediums.0.track.5</c> when it is the second track of the second disc is
/// accepted silently and comes out as a different album;</item>
/// <item>the length, which must come from what measured the audio and not from
/// what the container claims about itself;</item>
/// <item>the title, which is the tag where there is one and the filename where
/// there is not — and never the filename when a tag exists;</item>
/// <item>subfolders becoming separate mediums, since a concert rip is
/// <c>CD 1</c>/<c>CD 2</c> as often as it is flat.</item>
/// </list>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class ReleaseSeedEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Folder = "Fonoteca/Live in Zwolle";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-seed-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        using var warm = _factory.CreateClient();

        if (Corpus.IsAvailable) PlaceAsync();

        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The form MusicBrainz would be handed, field by field.
    /// </summary>
    /// <remarks>
    /// One test rather than five, because the fields are one document and the
    /// interesting failures are all disagreements <i>between</i> them — a title
    /// on the wrong medium, a length against the wrong track. Split up, each
    /// assertion passes while the form as a whole describes a different album.
    /// </remarks>
    [Fact]
    public async Task AFolderIsSeededAsMusicBrainzsOwnFieldsWithItsTagsAndItsMeasuredLengths()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH.");

        var seed = await SeedRequestAsync(Folder);

        // Never the configured server: a mirror is a read-only copy, and an edit
        // entered against one is refused or lost at the next replication.
        Assert.Equal("https://musicbrainz.org/release/add", seed.Action);

        // No file carries an ALBUM tag, so the folder's own leaf names the
        // release — and the artist comes from the one file that *does* carry a
        // tag rather than from the path, which is the ordering under test.
        Assert.Equal("Live in Zwolle", seed.Title);
        Assert.Equal("Fonoteca", seed.Artist);
        Assert.Equal(4, seed.TrackCount);
        Assert.Equal(2, seed.MediumCount);

        var fields = seed.Fields.ToDictionary(
            field => field.Name, field => field.Value, StringComparer.Ordinal);

        Assert.Equal("Live in Zwolle", fields["name"]);
        Assert.Equal("Fonoteca", fields["artist_credit.names.0.name"]);

        // What an unissued concert recording is. Seeded rather than left to the
        // editor's default, which is "Official" — the one answer certainly
        // wrong for a folder MusicBrainz has never heard of.
        Assert.Equal("bootleg", fields["status"]);

        // The tag, not the filename: this file is called `01 Not The Title.flac`
        // and carries TITLE=Corpus.
        Assert.Equal("Corpus", fields["mediums.0.track.0.name"]);

        // The filename, with the track number taken off it, where there is no
        // tag to read.
        Assert.Equal("Second Song", fields["mediums.0.track.1.name"]);

        // Numbered within its own medium, from zero, and the second disc's first
        // track is track 0 of medium 1 — not track 2 of medium 0. Getting this
        // wrong produces an album with a plausible track list and the wrong
        // shape.
        Assert.Equal("Encore", fields["mediums.1.track.0.name"]);

        // A disc is not given a title. MusicBrainz means `mediums.N.name` as a
        // medium's own name — "Bonus Disc" — and "CD 1" is a designator its
        // position already states.
        Assert.False(fields.ContainsKey("mediums.0.name"));

        // The number the file's own tag states, which is the whole reason the
        // tag is read at all. The fixture's first file carries TRACK=4 while
        // sitting first in path order, so a seed numbering by position produces
        // "1" here and passes every other assertion in this test.
        Assert.Equal("4", fields["mediums.0.track.0.number"]);

        // And the position where there is no tag to read, so a gapped rip does
        // not silently renumber the album.
        Assert.Equal("2", fields["mediums.0.track.1.number"]);

        // The catalogue's measurement beats the container's claim, and the
        // fixture makes them disagree on purpose: the audio is twelve seconds
        // and `FingerprintDuration` says 61.5, which is the number that has to
        // arrive. A container's own header is exactly what this must not use —
        // a VBR MP3 with no Xing header is wrong by minutes.
        Assert.Equal("61500", fields["mediums.0.track.0.length"]);

        // A file nothing could measure seeds no length at all rather than a
        // zero, and the response says how many of those there are so a person
        // knows before they read the form rather than after.
        Assert.Equal(1, seed.UnmeasuredTracks);
        Assert.False(fields.ContainsKey("mediums.1.track.1.length"));
        Assert.Equal("Missing", fields["mediums.1.track.1.name"]);
    }

    /// <summary>A folder the catalogue holds nothing under is a 404.</summary>
    /// <remarks>
    /// Rather than an empty form, which musicbrainz.org would happily open — a
    /// release editor with a name and no tracks is a worse outcome than an error,
    /// because it is one Save away from being a real empty release.
    /// </remarks>
    [Fact]
    public async Task AFolderTheCatalogueDoesNotHoldIsRefused()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync(
            new Uri(
                "/api/catalogue/matching/folders/seed?folder=Nobody/Nothing", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Nothing to name the folder with is a 400, not a seed of the whole library.</summary>
    [Fact]
    public async Task AMissingFolderParameterIsRefused()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync(
            new Uri("/api/catalogue/matching/folders/seed", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<ReleaseSeedResponse> SeedRequestAsync(string folder)
    {
        using var client = _factory!.CreateClient();

        var seed = await client.GetFromJsonAsync<ReleaseSeedResponse>(
            new Uri(
                $"/api/catalogue/matching/folders/seed?folder={Uri.EscapeDataString(folder)}",
                UriKind.Relative),
            Token);

        Assert.NotNull(seed);
        return seed;
    }

    /// <summary>
    /// Two discs of a concert, one file of which carries tags and one of which
    /// is not on disk at all.
    /// </summary>
    private void PlaceAsync()
    {
        var one = Path.Combine(_root, Folder.Replace('/', Path.DirectorySeparatorChar), "CD 1");
        var two = Path.Combine(_root, Folder.Replace('/', Path.DirectorySeparatorChar), "CD 2");

        Directory.CreateDirectory(one);
        Directory.CreateDirectory(two);

        // TITLE=Corpus, ARTIST=Fonoteca — deliberately not what the file is
        // called, so a seed reading the filename fails this test.
        var tagged = Path.Combine(one, "01 Not The Title.flac");
        File.Copy(Corpus.FlacWithArtwork, tagged);

        // And a track number that disagrees with its position, written with the
        // count beside it because that is the shape that matters: a container
        // stating a total comes back from TagLib# as "4 of 12", not "4/12", and
        // a parser that only knew about the slash discarded every one of them.
        using (var write = TagLib.File.Create(tagged))
        {
            write.Tag.Track = 4;
            write.Tag.TrackCount = 12;
            write.Save();
        }

        File.Copy(Corpus.Flac, Path.Combine(one, "02 Second Song.flac"));
        File.Copy(Corpus.Flac, Path.Combine(two, "01 Encore.flac"));
    }

    private async Task SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        db.MediaFiles.AddRange(
            Row($"{Folder}/CD 1/01 Not The Title.flac", TimeSpan.FromSeconds(61.5)),
            Row($"{Folder}/CD 1/02 Second Song.flac", TimeSpan.FromSeconds(12)),
            Row($"{Folder}/CD 2/01 Encore.flac", TimeSpan.FromSeconds(12)),

            // Catalogued and gone: no tags, no probe, no length anywhere.
            Row($"{Folder}/CD 2/02 Missing.flac", null));

        await db.SaveChangesAsync(Token);
    }

    private static MediaFile Row(string path, TimeSpan? fingerprinted) => new()
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
}
