using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Playlists through the real host: static ones that are their files, smart
/// ones the library keeps, and a Subsonic client editing them.
/// </summary>
/// <remarks>
/// The file is what is asserted wherever it can be, because it is what another
/// player reads, and for a static playlist it is the whole of the playlist.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class PlaylistEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "a-test-password";
    private const string Credentials = $"u=me&p={Password}&v=1.16.1&c=test&f=json";

    private const string Lossless = "Fleetwood Mac/Rumours (1977)/07 - The Chain.flac";
    private const string Lossy = "Fleetwood Mac/Rumours MP3/07 The Chain.mp3";
    private const string DreamsPath = "Fleetwood Mac/Rumours (1977)/02 - Dreams.flac";
    private const string NamelessPath = "Somebody/01 Nameless.flac";

    private string _root = string.Empty;
    private string _trash = string.Empty;
    private string _connectionString = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private RecordingId _chain;
    private RecordingId _dreams;
    private MediaFileId _lossless;
    private MediaFileId _lossy;
    private MediaFileId _dreamsFile;
    private MediaFileId _nameless;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-playlists-").FullName;
        _trash = Directory.CreateTempSubdirectory("fonoteca-playlists-bin-").FullName;

        foreach (var path in new[] { Lossless, Lossy, DreamsPath, NamelessPath }) WriteFile(path, "audio");

        _factory = Factory(_root);

        using (var warm = _factory.CreateClient())
        {
            using var boot = await warm.GetAsync(new Uri("/health", UriKind.Relative), Token);
        }

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var band = new Artist { Id = ArtistId.New(), Name = "Fleetwood Mac", Type = "Group", Country = "GB" };
        var rumours = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Rumours",
            PrimaryType = "Album",
            FirstReleaseYear = 1977,
            Genres = "rock, soft rock",
        };
        var chain = new Recording { Id = RecordingId.New(), Title = "The Chain", Duration = TimeSpan.FromSeconds(270) };
        var dreams = new Recording { Id = RecordingId.New(), Title = "Dreams" };

        var lossless = File(Lossless, chain.Id, new AudioQuality
        {
            Codec = "flac",
            SampleRateHz = 44_100,
            Channels = 2,
            BitDepth = 16,
            BitrateBps = 900_000,
            IsLossless = true,
            Duration = TimeSpan.FromSeconds(270.6),
        });

        var lossy = File(Lossy, chain.Id, new AudioQuality
        {
            Codec = "mp3",
            SampleRateHz = 44_100,
            Channels = 2,
            BitrateBps = 320_000,
            IsLossless = false,
            Duration = TimeSpan.FromSeconds(270.6),
        });

        var dreamsFile = File(DreamsPath, dreams.Id, new AudioQuality
        {
            Codec = "flac",
            SampleRateHz = 96_000,
            Channels = 2,
            BitDepth = 24,
            BitrateBps = 2_900_000,
            IsLossless = true,
            Duration = TimeSpan.FromSeconds(257),
        });

        lossless.ReleaseGroupId = rumours.Id;
        dreamsFile.ReleaseGroupId = rumours.Id;

        var nameless = File(NamelessPath, null, null);

        (_chain, _dreams) = (chain.Id, dreams.Id);
        (_lossless, _lossy, _dreamsFile, _nameless) = (lossless.Id, lossy.Id, dreamsFile.Id, nameless.Id);

        db.Artists.Add(band);
        db.ReleaseGroups.Add(rumours);
        db.Recordings.AddRange(chain, dreams);
        db.MediaFiles.AddRange(lossless, lossy, dreamsFile, nameless);
        db.ArtistCredits.Add(new ArtistCredit { Id = Guid.CreateVersion7(), ArtistId = band.Id, RecordingId = chain.Id, Position = 0 });
        db.ArtistCredits.Add(new ArtistCredit { Id = Guid.CreateVersion7(), ArtistId = band.Id, ReleaseGroupId = rumours.Id, Position = 0 });
        db.Contributions.Add(new Contribution
        {
            Id = Guid.CreateVersion7(),
            RecordingId = chain.Id,
            Type = "composer",
            Name = "Lindsey Buckingham",
            Position = 0,
        });

        await db.SaveChangesAsync(Token);
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
    public async Task AStaticPlaylistIsAnM3uNamingTheBestCopyOfEachSongRelativeToItself()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "Road trip", recordingIds = new[] { _chain.Value, _dreams.Value, _chain.Value } },
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var playlist = (await response.Content.ReadFromJsonAsync<PlaylistResponse>(Token))!;

        Assert.Equal("Road trip.m3u8", playlist.Id);
        Assert.Equal(PlaylistKind.Static, playlist.Kind);
        Assert.Equal("Playlists/Road trip.m3u8", playlist.File);
        Assert.True(playlist.Editable);
        Assert.Equal([_lossless.Value, _dreamsFile.Value, _lossless.Value], playlist.Entries.Select(entry => entry.FileId!.Value));
        Assert.Equal(Lossless, playlist.Entries[0].Track!.File.Path);
        Assert.Equal("Fleetwood Mac", playlist.Entries[0].Artist);
        Assert.Equal("13:18", playlist.Duration);

        Assert.Equal(
            "#EXTM3U\n"
            + "#PLAYLIST:Road trip\n"
            + "#EXTINF:271,Fleetwood Mac - The Chain\n"
            + $"../{Lossless}\n"
            + "#EXTINF:257,Dreams\n"
            + $"../{DreamsPath}\n"
            + "#EXTINF:271,Fleetwood Mac - The Chain\n"
            + $"../{Lossless}\n",
            ReadPlaylist("Road trip"));
    }

    /// <summary>
    /// A file another program wrote is a playlist as it stands: its name line,
    /// an absolute path under the root, a stream and a missing file all read
    /// back, and only the entries naming a catalogued file have a track.
    /// </summary>
    [Fact]
    public async Task AFileWrittenElsewhereIsAPlaylistReadAsItIs()
    {
        WriteFile(
            "Playlists/mine.m3u",
            "#EXTM3U\r\n#PLAYLIST:Mine\r\n"
            + $"{Path.Combine(_root, Lossy)}\r\n"
            + "http://radio.example/stream\r\n"
            + "#EXTINF:100,Gone - Away\r\n../Gone/01.flac\r\n"
            + $"..\\{NamelessPath.Replace('/', '\\')}\r\n");

        using var client = _factory!.CreateClient();

        var listed = (await client.GetFromJsonAsync<PlaylistListResponse>(new Uri("/api/catalogue/playlists", UriKind.Relative), Token))!;
        var summary = Assert.Single(listed.Items);

        Assert.Equal(("mine.m3u", PlaylistKind.Static, "Mine", 4, 2), (summary.Id, summary.Kind, summary.Name, summary.Songs, summary.Held));

        var read = await GetAsync(client, "mine.m3u");

        Assert.Equal([_lossy.Value, null, null, _nameless.Value], read.Entries.Select(entry => entry.FileId));
        Assert.Equal("Gone - Away", read.Entries[2].Title);
        Assert.Equal("../Gone/01.flac", read.Entries[2].Location);
        Assert.Null(read.Entries[2].Track);
        Assert.Equal("The Chain", read.Entries[0].Track!.Title);
        Assert.Equal("01 Nameless", read.Entries[3].Track!.Title);
    }

    /// <summary>A hand edit made since is what the next edit here starts from, and stays in the file.</summary>
    [Fact]
    public async Task AnEditMadeElsewhereIsWhereTheNextEditStarts()
    {
        using var client = _factory!.CreateClient();
        await CreateAsync(client, "Road trip", _chain);

        var edited = ReadPlaylist("Road trip") + "# a note\n../Gone/01.flac\n";
        WriteFile("Playlists/Road trip.m3u8", edited);

        using var added = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists/Road trip.m3u8/songs", UriKind.Relative),
            new { recordingIds = new[] { _dreams.Value } },
            Token);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(edited + $"#EXTINF:257,Dreams\n../{DreamsPath}\n", ReadPlaylist("Road trip"));
    }

    /// <summary>
    /// A folder moved in the file manager, and every playlist naming anything
    /// in it follows at once — the line whose file is already gone too — while
    /// a file that is not UTF-8 is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task AMoveInTheFileManagerRepointsEveryPlaylistAtOnce()
    {
        using var client = _factory!.CreateClient();
        await CreateAsync(client, "Road trip", _chain);

        WriteFile("Playlists/Other.m3u8", "#EXTM3U\n../Fleetwood Mac/Rumours (1977)/99 - Gone.flac\n../Somebody/01 Nameless.flac\n");

        var latin = Encoding.Latin1.GetBytes("#EXTM3U\n../Fleetwood Mac/Rumours (1977)/07 - The Chain.flac\n#Bjørk\n");
        await System.IO.File.WriteAllBytesAsync(PlaylistPath("Latin", ".m3u"), latin, Token);

        using var moved = await client.PostAsJsonAsync(
            new Uri("/api/files/move", UriKind.Relative),
            new { from = "Fleetwood Mac/Rumours (1977)", to = "Fleetwood Mac/Rumours" },
            Token);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Contains("\n../Fleetwood Mac/Rumours/07 - The Chain.flac\n", ReadPlaylist("Road trip"), StringComparison.Ordinal);
        Assert.Equal("#EXTM3U\n../Fleetwood Mac/Rumours/99 - Gone.flac\n../Somebody/01 Nameless.flac\n", ReadPlaylist("Other"));
        Assert.Equal(latin, await System.IO.File.ReadAllBytesAsync(PlaylistPath("Latin", ".m3u"), Token));
    }

    [Fact]
    public async Task ReorderingRemovingRenamingAndDeletingAllReachTheFile()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Morning", _chain, _dreams);

        using var patched = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists/Morning.m3u8", UriKind.Relative),
            new { name = "Evening", entries = new[] { 1 }, version = playlist.Version },
            Token);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("Evening.m3u8", (await patched.Content.ReadFromJsonAsync<PlaylistResponse>(Token))!.Id);
        Assert.False(System.IO.File.Exists(PlaylistPath("Morning")));
        Assert.Equal($"#EXTM3U\n#PLAYLIST:Evening\n#EXTINF:257,Dreams\n../{DreamsPath}\n", ReadPlaylist("Evening"));

        using var deleted = await client.DeleteAsync(new Uri("/api/catalogue/playlists/Evening.m3u8", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(System.IO.File.Exists(PlaylistPath("Evening")));
        Assert.Single(Directory.GetFiles(_trash, "Evening.m3u8", SearchOption.AllDirectories));

        using var gone = await client.GetAsync(new Uri("/api/catalogue/playlists/Evening.m3u8", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    /// <summary>Entries are named by their place, so an edit made against a file that has changed since is refused, not misapplied.</summary>
    [Fact]
    public async Task AnEditToAFileChangedSinceItWasReadIsRefused()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Road trip", _chain, _dreams);

        var changed = "#EXTM3U\n../Somebody/01 Nameless.flac\n" + ReadPlaylist("Road trip")["#EXTM3U\n".Length..];
        WriteFile("Playlists/Road trip.m3u8", changed);

        using var stale = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists/Road trip.m3u8", UriKind.Relative),
            new { entries = new[] { 0 }, version = playlist.Version },
            Token);

        using var unversioned = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists/Road trip.m3u8", UriKind.Relative),
            new { entries = new[] { 0 } },
            Token);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unversioned.StatusCode);
        Assert.Equal(changed, ReadPlaylist("Road trip"));
    }

    /// <summary>Every m3u in the folder is a playlist, so a name whose file exists is taken — in any case.</summary>
    [Fact]
    public async Task ANameWhoseFileIsTakenIsRefused()
    {
        WriteFile("Playlists/Mine.m3u8", "#EXTM3U\n../somewhere.flac\n");

        using var client = _factory!.CreateClient();

        using var foreign = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "mine" },
            Token);

        using var smart = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/smart-playlists", UriKind.Relative),
            new { name = "Mine", criteria = Criteria(Group("All")) },
            Token);

        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, smart.StatusCode);
        Assert.Equal("#EXTM3U\n../somewhere.flac\n", ReadPlaylist("Mine"));
    }

    [Fact]
    public async Task OnlySongsAndFilesTheLibraryHoldsCanBeAddedAndAPlaylistNeedsAName()
    {
        using var client = _factory!.CreateClient();

        using var unknown = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "Road trip", recordingIds = new[] { Guid.CreateVersion7() } },
            Token);

        using var unknownFile = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "Road trip", fileIds = new[] { Guid.CreateVersion7() } },
            Token);

        using var unnamed = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "  " },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownFile.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "Playlists")));
    }

    /// <summary>
    /// A root with nothing in it is a mount point with nothing behind it, and a
    /// playlist written there would land on the host's own disk (rule 3).
    /// </summary>
    [Fact]
    public async Task NothingIsWrittenWhileTheLibraryLooksUnmounted()
    {
        var empty = Directory.CreateTempSubdirectory("fonoteca-playlists-unmounted-").FullName;

        try
        {
            await using var unmounted = Factory(empty);
            using var client = unmounted.CreateClient();

            using var response = await client.PostAsJsonAsync(
                new Uri("/api/catalogue/playlists", UriKind.Relative),
                new { name = "Road trip", recordingIds = new[] { _chain.Value } },
                Token);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(empty));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task AFileThatIsNotUtf8IsReadButNeverWritten()
    {
        var latin = Encoding.Latin1.GetBytes("#EXTM3U\n#PLAYLIST:Bjørk\n../Somebody/01 Nameless.flac\n");
        Directory.CreateDirectory(Path.Combine(_root, "Playlists"));
        await System.IO.File.WriteAllBytesAsync(PlaylistPath("Old", ".m3u"), latin, Token);

        using var client = _factory!.CreateClient();

        var read = await GetAsync(client, "Old.m3u");

        Assert.False(read.Editable);
        Assert.Equal("Bjørk", read.Name);
        Assert.Equal(_nameless.Value, read.Entries[0].FileId);

        using var added = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists/Old.m3u/songs", UriKind.Relative),
            new { recordingIds = new[] { _dreams.Value } },
            Token);

        Assert.Equal(HttpStatusCode.Conflict, added.StatusCode);
        Assert.Equal(latin, await System.IO.File.ReadAllBytesAsync(PlaylistPath("Old", ".m3u"), Token));
    }

    /// <summary>
    /// After an upgrade, a line naming the old rip moves to the one file of its
    /// name in another format beside it — and stays where two would answer.
    /// </summary>
    [Fact]
    public async Task AnUpgradeCarriesALineToTheSameNameInAnotherFormat()
    {
        WriteFile(
            "Playlists/Mix.m3u8",
            $"#EXTM3U\n../{Lossy}\n../Fleetwood Mac/Rumours MP3/08 Two.mp3\n../Somebody/02 Gone.mp3\n");

        System.IO.File.Delete(Path.Combine(_root, Lossy));
        WriteFile("Fleetwood Mac/Rumours MP3/07 The Chain.flac", "audio");
        WriteFile("Fleetwood Mac/Rumours MP3/08 Two.flac", "audio");
        WriteFile("Fleetwood Mac/Rumours MP3/08 Two.m4a", "audio");
        WriteFile("Somebody/02 Gone.flac", "audio");

        await _factory!.Services.GetRequiredService<PlaylistFiles>().FollowFormatsAsync("Fleetwood Mac/Rumours MP3", Token);

        // Another folder's dead line is not the upgrade's, whatever sits beside it.
        Assert.Equal(
            "#EXTM3U\n../Fleetwood Mac/Rumours MP3/07 The Chain.flac\n../Fleetwood Mac/Rumours MP3/08 Two.mp3\n../Somebody/02 Gone.mp3\n",
            ReadPlaylist("Mix"));
    }

    /// <summary>
    /// A smart playlist writes its songs, each once as the best copy that
    /// matched, in its order — and a lossy rule plays the lossy copy.
    /// </summary>
    [Fact]
    public async Task ASmartPlaylistWritesTheSongsItsRulesHold()
    {
        using var client = _factory!.CreateClient();

        var criteria = Criteria(
            Group("Any", Condition("Title", "Is", "dreams"), Group("All", Condition("Artist", "Is", "fleetwood mac"), Condition("Composer", "Contains", "buckingham"))),
            sort: "Title");

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/smart-playlists", UriKind.Relative),
            new { name = "Smart", criteria },
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var playlist = (await response.Content.ReadFromJsonAsync<PlaylistResponse>(Token))!;

        Assert.Equal(PlaylistKind.Smart, playlist.Kind);
        Assert.False(playlist.Editable);
        Assert.Equal(SmartSort.Title, playlist.Criteria!.Sort);
        Assert.Equal([_dreamsFile.Value, _lossless.Value], playlist.Entries.Select(entry => entry.FileId!.Value));
        Assert.Equal(
            $"#EXTM3U\n#PLAYLIST:Smart\n#EXTINF:257,Dreams\n../{DreamsPath}\n#EXTINF:271,Fleetwood Mac - The Chain\n../{Lossless}\n",
            ReadPlaylist("Smart"));

        var lossy = await PreviewAsync(client, Criteria(Group("All", Condition("Lossless", "IsFalse"))));

        Assert.Equal([_lossy.Value], lossy.Entries.Select(entry => entry.FileId!.Value));
    }

    /// <summary>Every field with every operator it allows becomes SQL: EF refuses an expression only when the query runs.</summary>
    [Fact]
    public async Task EveryFieldAndOperatorTranslates()
    {
        using var client = _factory!.CreateClient();

        foreach (var field in Enum.GetValues<SmartField>())
        {
            var kind = SmartRules.KindOf(field);

            foreach (var op in Enum.GetValues<SmartOperator>().Where(op => SmartRules.Allows(kind, op)))
            {
                var (value, to) = kind switch
                {
                    SmartFieldKind.Number => ("1", "100000"),
                    SmartFieldKind.Date => ("30", null),
                    _ => ("a", (string?)null),
                };

                using var response = await client.PostAsJsonAsync(
                    new Uri("/api/catalogue/smart-playlists/preview", UriKind.Relative),
                    new { criteria = Criteria(Group("All", Condition(field.ToString(), op.ToString(), value, to))) },
                    Token);

                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{field} {op}: {await response.Content.ReadAsStringAsync(Token)}");
            }
        }
    }

    [Theory]
    [InlineData("Genre", "Is", "soft rock", null, 2)]
    [InlineData("Genre", "Is", "soft", null, 0)]
    // Asked of Fonoteca's genres: "soft rock" carries Soft Rock and its top, Rock.
    [InlineData("Genre", "Is", "Rock", null, 2)]
    [InlineData("Genre", "Contains", "soft", null, 2)]
    [InlineData("Genre", "Is", "Pop", null, 0)]
    [InlineData("Genre", "DoesNotContain", "Rock", null, 1)]
    // A rule in MusicBrainz's words folds as the files do: yacht rock is Soft Rock.
    [InlineData("Genre", "Is", "yacht rock", null, 2)]
    // The MP3 is filed under no album and its recording has no genre of its own.
    [InlineData("Genre", "IsNot", "rock", null, 1)]
    [InlineData("Genre", "IsEmpty", null, null, 1)]
    [InlineData("Album", "StartsWith", "rum", null, 2)]
    [InlineData("AlbumArtist", "Is", "Fleetwood Mac", null, 2)]
    [InlineData("ArtistCountry", "Is", "gb", null, 2)]
    [InlineData("Year", "Between", "1980", "1970", 2)]
    [InlineData("Year", "IsEmpty", null, null, 1)]
    [InlineData("Year", "IsNot", "1977", null, 1)]
    [InlineData("SampleRate", "AtLeast", "88.2", null, 1)]
    [InlineData("HiRes", "IsTrue", null, null, 1)]
    [InlineData("Length", "AtMost", "260", null, 1)]
    [InlineData("Added", "InTheLast", "1", null, 2)]
    [InlineData("Added", "NotInTheLast", "1", null, 0)]
    [InlineData("Path", "EndsWith", ".mp3", null, 1)]
    [InlineData("Composer", "IsNot", "Lindsey Buckingham", null, 1)]
    public async Task EachRuleAnswersWhatItSays(string field, string op, string? value, string? to, int songs)
    {
        using var client = _factory!.CreateClient();

        var preview = await PreviewAsync(client, Criteria(Group("All", Condition(field, op, value, to))));

        Assert.Equal(songs, preview.Songs);
    }

    /// <summary>Anything that took the gate may have changed the answer, so its release writes the file again.</summary>
    [Fact]
    public async Task ASmartPlaylistIsRewrittenWhenTheGateIsReleased()
    {
        using var client = _factory!.CreateClient();
        await CreateSmartAsync(client, "Band", Criteria(Group("All", Condition("Artist", "Is", "Fleetwood Mac"))));

        Assert.DoesNotContain("Dreams", ReadPlaylist("Band"), StringComparison.Ordinal);

        var gate = _factory.Services.GetRequiredService<LibraryWorkGate>();
        Assert.True(gate.TryEnter("test", out var lease));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var band = await db.Artists.SingleAsync(Token);
            db.ArtistCredits.Add(new ArtistCredit { Id = Guid.CreateVersion7(), ArtistId = band.Id, RecordingId = _dreams, Position = 0 });
            await db.SaveChangesAsync(Token);
        }

        lease.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ReadPlaylist("Band").Contains("Dreams", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Token);
        }

        Assert.Contains($"../{DreamsPath}\n", ReadPlaylist("Band"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A smart playlist's file is the library's: the static routes do not
    /// reach it, an edit made to it is overwritten, a rename moves it, and
    /// deleting the playlist puts it in the trash.
    /// </summary>
    [Fact]
    public async Task ASmartPlaylistsFileIsTheLibrarysOwn()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateSmartAsync(client, "Mine", Criteria(Group("All", Condition("Title", "Is", "Dreams"))));
        var written = ReadPlaylist("Mine");

        using var asStatic = await client.GetAsync(new Uri("/api/catalogue/playlists/Mine.m3u8", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, asStatic.StatusCode);

        WriteFile("Playlists/Mine.m3u8", "#EXTM3U\n../elsewhere.flac\n");
        Assert.Equal(1, await _factory.Services.GetRequiredService<PlaylistFiles>().RefreshAsync(null, Token));
        Assert.Equal(written, ReadPlaylist("Mine"));

        using var renamed = await client.PostAsJsonAsync(
            new Uri($"/api/catalogue/smart-playlists/{playlist.Id}", UriKind.Relative),
            new { name = "Yours" },
            Token);

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.False(System.IO.File.Exists(PlaylistPath("Mine")));
        Assert.StartsWith("#EXTM3U\n#PLAYLIST:Yours\n", ReadPlaylist("Yours"), StringComparison.Ordinal);

        using var deleted = await client.DeleteAsync(new Uri($"/api/catalogue/smart-playlists/{playlist.Id}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(System.IO.File.Exists(PlaylistPath("Yours")));
        Assert.Single(Directory.GetFiles(_trash, "Yours.m3u8", SearchOption.AllDirectories));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.False(await db.SmartPlaylists.AnyAsync(Token));
    }

    /// <summary>Following takes no gate, so it asks for the refresh itself: a rule can ask who is followed.</summary>
    [Fact]
    public async Task FollowingAnArtistRewritesTheSmartPlaylists()
    {
        using var client = _factory!.CreateClient();
        await CreateSmartAsync(client, "Followed", Criteria(Group("All", Condition("Followed", "IsTrue"))));

        Assert.DoesNotContain("The Chain", ReadPlaylist("Followed"), StringComparison.Ordinal);

        Guid band;
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            band = (await db.Artists.SingleAsync(Token)).Id.Value;
        }

        using var followed = await client.PostAsJsonAsync(
            new Uri($"/api/catalogue/artists/{band}/follow", UriKind.Relative),
            new { follow = true },
            Token);

        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ReadPlaylist("Followed").Contains("The Chain", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Token);
        }

        Assert.Contains($"../{Lossless}\n", ReadPlaylist("Followed"), StringComparison.Ordinal);
    }

    /// <summary>The scan never takes the gate, so it asks for the refresh itself.</summary>
    [Fact]
    public async Task AScanRewritesTheSmartPlaylists()
    {
        using var client = _factory!.CreateClient();
        await CreateSmartAsync(client, "Smart", Criteria(Group("All", Condition("Title", "Is", "Dreams"))));

        WriteFile("Playlists/Smart.m3u8", "#EXTM3U\n");

        using var scan = await client.PostAsync(new Uri("/api/library/scan", UriKind.Relative), null, Token);
        Assert.True(scan.IsSuccessStatusCode, await scan.Content.ReadAsStringAsync(Token));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ReadPlaylist("Smart").Contains("#PLAYLIST:Smart", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Token);
        }

        Assert.StartsWith("#EXTM3U\n#PLAYLIST:Smart\n", ReadPlaylist("Smart"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesThatCannotBeAskedAreRefused()
    {
        using var client = _factory!.CreateClient();

        using var preview = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/smart-playlists/preview", UriKind.Relative),
            new { criteria = Criteria(Group("All", Condition("Year", "Contains", "19"))) },
            Token);

        using var created = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/smart-playlists", UriKind.Relative),
            new { name = "Bad", criteria = Criteria(Group("All", Condition("Added", "InTheLast", "soon"))) },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.False(System.IO.File.Exists(PlaylistPath("Bad")));
    }

    [Fact]
    public async Task ASubsonicClientCreatesEditsAndDeletesStaticPlaylists()
    {
        using var client = _factory!.CreateClient();

        var created = await SubsonicAsync(client, $"createPlaylist?name=Phone&songId={Song(_lossy)}&songId={Song(_dreamsFile)}");

        Assert.Equal("ok", created.GetProperty("status").GetString());

        var id = created.GetProperty("playlist").GetProperty("id").GetString()!;

        // An entry is the file the client named.
        Assert.Equal("pf-Phone.m3u8", id);
        Assert.Equal([Song(_lossy), Song(_dreamsFile)], Entries(created.GetProperty("playlist")));
        Assert.Contains($"../{Lossy}", ReadPlaylist("Phone"), StringComparison.Ordinal);

        var refused = await SubsonicAsync(client, $"createPlaylist?name=Bad&songId=tr-{Guid.CreateVersion7():N}");

        Assert.Equal("failed", refused.GetProperty("status").GetString());

        var listed = await SubsonicAsync(client, "getPlaylists");
        var only = Assert.Single(listed.GetProperty("playlists").GetProperty("playlist").EnumerateArray());

        Assert.Equal(id, only.GetProperty("id").GetString());
        Assert.Equal(2, only.GetProperty("songCount").GetInt32());
        Assert.Equal(528, only.GetProperty("duration").GetInt32());

        var updated = await SubsonicAsync(client, $"updatePlaylist?playlistId={id}&songIndexToRemove=0&songIdToAdd={Song(_nameless)}");

        Assert.Equal("ok", updated.GetProperty("status").GetString());
        Assert.Equal(
            [Song(_dreamsFile), Song(_nameless)],
            Entries((await SubsonicAsync(client, $"getPlaylist?id={id}")).GetProperty("playlist")));

        var deleted = await SubsonicAsync(client, $"deletePlaylist?id={id}");

        Assert.Equal("ok", deleted.GetProperty("status").GetString());
        Assert.Empty((await SubsonicAsync(client, "getPlaylists")).GetProperty("playlists").GetProperty("playlist").EnumerateArray());
    }

    /// <summary>
    /// An entry naming nothing the library holds is not shown to a client, and
    /// a client replacing the songs it was shown does not take that one away.
    /// </summary>
    [Fact]
    public async Task ASubsonicReplaceKeepsTheEntriesTheClientCouldNotSee()
    {
        WriteFile("Playlists/Road trip.m3u8", $"#EXTM3U\nhttp://radio.example/stream\n../{Lossless}\n");

        using var client = _factory!.CreateClient();

        const string id = "pf-Road trip.m3u8";

        Assert.Equal([Song(_lossless)], Entries((await SubsonicAsync(client, $"getPlaylist?id={id}")).GetProperty("playlist")));

        var replaced = await SubsonicAsync(client, $"createPlaylist?playlistId={id}&songId={Song(_dreamsFile)}&songId={Song(_lossy)}");

        Assert.Equal([Song(_dreamsFile), Song(_lossy)], Entries(replaced.GetProperty("playlist")));
        Assert.Equal(
            ["../" + DreamsPath, "../" + Lossy, "http://radio.example/stream"],
            (await GetAsync(client, "Road trip.m3u8")).Entries.Select(entry => entry.Location));
    }

    [Fact]
    public async Task ASmartPlaylistIsReadOnlyToASubsonicClient()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateSmartAsync(client, "Smart", Criteria(Group("All", Condition("Title", "Is", "Dreams"))));

        var id = $"pl-{Guid.Parse(playlist.Id):N}";

        var listed = await SubsonicAsync(client, "getPlaylists");
        Assert.Equal(id, Assert.Single(listed.GetProperty("playlists").GetProperty("playlist").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal([Song(_dreamsFile)], Entries((await SubsonicAsync(client, $"getPlaylist?id={id}")).GetProperty("playlist")));

        foreach (var route in new[] { $"updatePlaylist?playlistId={id}&songIndexToRemove=0", $"deletePlaylist?id={id}", $"createPlaylist?playlistId={id}&songId={Song(_lossy)}" })
        {
            var refused = await SubsonicAsync(client, route);
            Assert.Equal(50, refused.GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.True(System.IO.File.Exists(PlaylistPath("Smart")));
    }

    private async Task<PlaylistResponse> CreateAsync(HttpClient client, string name, params RecordingId[] songs)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name, recordingIds = songs.Select(song => song.Value).ToArray() },
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PlaylistResponse>(Token))!;
    }

    private static async Task<PlaylistResponse> CreateSmartAsync(HttpClient client, string name, object criteria)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/smart-playlists", UriKind.Relative),
            new { name, criteria },
            Token);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));

        return (await response.Content.ReadFromJsonAsync<PlaylistResponse>(Token))!;
    }

    private static async Task<SmartPreviewResponse> PreviewAsync(HttpClient client, object criteria)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/smart-playlists/preview", UriKind.Relative),
            new { criteria },
            Token);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));

        return (await response.Content.ReadFromJsonAsync<SmartPreviewResponse>(Token))!;
    }

    private static async Task<PlaylistResponse> GetAsync(HttpClient client, string file) =>
        (await client.GetFromJsonAsync<PlaylistResponse>(new Uri($"/api/catalogue/playlists/{Uri.EscapeDataString(file)}", UriKind.Relative), Token))!;

    private static object Criteria(object rules, string sort = "Added", bool reverse = false, int? limit = null) =>
        new { rules, sort, reverse, limit };

    private static object Group(string match, params object[] rules) => new { match, rules };

    private static object Condition(string field, string op, string? value = null, string? to = null) =>
        new { field, @operator = op, value, to };

    private static async Task<JsonElement> SubsonicAsync(HttpClient client, string route)
    {
        var separator = route.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        using var response = await client.GetAsync(new Uri($"/rest/{route}{separator}{Credentials}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token))
            .RootElement
            .GetProperty("subsonic-response")
            .Clone();
    }

    private static List<string> Entries(JsonElement playlist) =>
        [.. playlist.GetProperty("entry").EnumerateArray().Select(entry => entry.GetProperty("id").GetString()!)];

    private static string Song(MediaFileId id) => $"tr-{id.Value:N}";

    private string PlaylistPath(string name, string extension = ".m3u8") => Path.Combine(_root, "Playlists", $"{name}{extension}");

    private string ReadPlaylist(string name) => System.IO.File.ReadAllText(PlaylistPath(name));

    private static MediaFile File(string path, RecordingId? recording, AudioQuality? quality) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 5,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        RecordingId = recording,
        Quality = quality,
    };

    private WebApplicationFactory<Program> Factory(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", root);
            builder.UseSetting("Fonoteca:TrashPath", _trash);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:IdentifyAfterScan", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
            builder.UseSetting("Fonoteca:SubsonicPassword", Password);
        });

    private void WriteFile(string relativePath, string content)
    {
        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        System.IO.File.WriteAllText(absolute, content);
    }
}
