using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The tag editor: a person's tags, stored beside the catalogue's and written over them.
/// </summary>
public sealed partial class TagWritePassTests
{
    [Fact]
    public async Task AFilesCorrectionIsWrittenAtOnceAndWinsFromThenOn()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        await RunAsync(TagWriteScope.Library, services);

        var file = await FileIdAsync();
        var saved = await SaveAsync(services, Album, new TagEdit(file, CatalogueTags.Title, "So What (take 1)"));

        Assert.Equal(TagEditStatus.Saved, saved.Status);
        Assert.Equal(1, saved.Written);
        Assert.Equal("So What (take 1)", (await TagsAsync($"{Album}/01 track.flac")).Fields["TITLE"]);

        // Another tagger changes it; the next library write puts the person's back.
        await OutsideAsync(services, $"{Album}/01 track.flac", CatalogueTags.Title, "Theirs");
        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("So What (take 1)", (await TagsAsync($"{Album}/01 track.flac")).Fields["TITLE"]);

        var state = await services.GetRequiredService<TagWriteService>().FolderTagsAsync(Album, Token);
        var title = state!.Files.Single().Cells.Single(cell => cell.Field == CatalogueTags.Title);

        Assert.True(title.Mine);
        Assert.Equal("So What", title.Catalogue);
        Assert.Equal("So What (take 1)", title.Value);
        Assert.Equal("So What (take 1)", title.File);
    }

    [Fact]
    public async Task AnAlbumsCorrectionsGoOnTheAlbumAndNameItsFolder()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/track one.flac", 1));

        var services = Build(fileNaming: FileNaming.DefaultPattern);

        var saved = await SaveAsync(
            services,
            "Miles Davis/kob rip",
            new TagEdit(null, CatalogueTags.AlbumArtist, "Miles Davis Sextet"),
            new TagEdit(null, CatalogueTags.Album, "Kind of Blue (Mono)"),
            new TagEdit(null, CatalogueTags.Year, "1958"));

        Assert.Equal(TagEditStatus.Saved, saved.Status);
        Assert.Equal("Miles Davis Sextet/Kind of Blue (Mono) (1958)", saved.Folder);

        var tags = await TagsAsync("Miles Davis Sextet/Kind of Blue (Mono) (1958)/01 - So What.flac");
        Assert.Equal("Kind of Blue (Mono)", tags.Fields["ALBUM"]);
        Assert.Equal("Miles Davis Sextet", tags.Find(CatalogueTags.AlbumArtist));
        Assert.Equal("1958", tags.Fields["YEAR"]);

        // The title and artist on the album, the year on the folder's files.
        await using var db = PostgresFixture.CreateContext(_connectionString);
        var album = PersonEdits.Read((await db.ReleaseGroups.SingleAsync(Token)).EditsJson);
        var pressing = PersonEdits.Read((await db.Releases.SingleAsync(release => release.Mbid == ReleaseMbid, Token)).EditsJson);

        Assert.Equal("Kind of Blue (Mono)", album["title"]);
        Assert.Equal("Miles Davis Sextet", album["credit"]);
        Assert.False(pressing.ContainsKey("releasedYear"));
        Assert.Equal("1958", PersonEdits.Read((await db.MediaFiles.SingleAsync(Token)).TagEditsJson)[CatalogueTags.Year]);
    }

    [Fact]
    public async Task AFieldTakenOutStaysOutUntilItsCorrectionIsForgotten()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        await RunAsync(TagWriteScope.Library, services);

        var file = await FileIdAsync();

        await SaveAsync(services, Album, new TagEdit(file, CatalogueTags.Title, null));
        Assert.False((await TagsAsync($"{Album}/01 track.flac")).Fields.ContainsKey("TITLE"));

        await RunAsync(TagWriteScope.Library, services);
        Assert.False((await TagsAsync($"{Album}/01 track.flac")).Fields.ContainsKey("TITLE"));

        await SaveAsync(services, Album, new TagEdit(file, CatalogueTags.Title, null, Reset: true));
        Assert.Equal("So What", (await TagsAsync($"{Album}/01 track.flac")).Fields["TITLE"]);
    }

    [Fact]
    public async Task AFolderTheCatalogueHasNoAlbumForIsTaggedAndNamedByHand()
    {
        SkipWithoutTools();
        var file = await SeedBareAsync("Pepita Salim/videos/clip.flac");

        var services = Build(fileNaming: FileNaming.DefaultPattern);

        var saved = await SaveAsync(
            services,
            "Pepita Salim/videos",
            new TagEdit(file, CatalogueTags.AlbumArtist, "Pepita Salim"),
            new TagEdit(file, CatalogueTags.Album, "Covers"),
            new TagEdit(file, CatalogueTags.Title, "Hallelujah"),
            new TagEdit(file, CatalogueTags.Artist, "Pepita Salim"),
            new TagEdit(file, CatalogueTags.TrackNumber, "1"));

        Assert.Equal(TagEditStatus.Saved, saved.Status);
        Assert.Equal("Pepita Salim/Covers", saved.Folder);

        var tags = await TagsAsync("Pepita Salim/Covers/01 - Hallelujah.flac");
        Assert.Equal("Hallelujah", tags.Fields["TITLE"]);
        Assert.Equal("Covers", tags.Fields["ALBUM"]);
        Assert.Equal("1", tags.Fields["TRACKNUMBER"]);
    }

    [Fact]
    public async Task UndoTakesASaveBackAndLeavesTheAlbumAnswered()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        await RunAsync(TagWriteScope.Library, services);

        var file = await FileIdAsync();
        await SaveAsync(services, Album, new TagEdit(file, CatalogueTags.Title, "Mine"), new TagEdit(file, CatalogueTags.Genre, "Jazz"));

        var undone = await UndoAsync(services, Album);

        Assert.Equal(FolderEditKind.TagEdit, undone.Edit!.Kind);
        Assert.Equal(0, undone.Reopened);

        var tags = await TagsAsync($"{Album}/01 track.flac");
        Assert.Equal("So What", tags.Fields["TITLE"]);
        Assert.False(tags.Fields.ContainsKey("GENRE"));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(Token);

        Assert.Null(row.TagEditsJson);
        Assert.NotNull(row.RecordingId);
    }

    [Fact]
    public async Task UndoOfAnAlbumsCorrectionPutsTheAlbumBack()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        await SaveAsync(services, Album, new TagEdit(null, CatalogueTags.Album, "Kind of Blue (Mono)"));

        await UndoAsync(services, Album);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Null((await db.ReleaseGroups.SingleAsync(Token)).EditsJson);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public async Task GenreComposerAndCommentAreWritten(string extension)
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.{extension}", 1));

        var services = Build();
        var file = await FileIdAsync();

        var saved = await SaveAsync(
            services,
            Album,
            new TagEdit(file, CatalogueTags.Genre, "Modal jazz"),
            new TagEdit(file, CatalogueTags.Composer, "Miles Davis"),
            new TagEdit(file, CatalogueTags.Comment, "First take"));

        Assert.Equal(1, saved.Written);

        var tags = await TagsAsync($"{Album}/01 track.{extension}");
        Assert.Equal("Modal jazz", tags.Fields["GENRE"]);
        Assert.Equal("Miles Davis", tags.Fields["COMPOSER"]);
        Assert.Equal("First take", tags.Fields["COMMENT"]);
    }

    [Fact]
    public async Task AnAlbumsTitleIsNotAFilesToSetAndANumberIsANumber()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        var file = await FileIdAsync();

        var album = await SaveAsync(services, Album, new TagEdit(file, CatalogueTags.Album, "Mine"));
        var year = await SaveAsync(services, Album, new TagEdit(null, CatalogueTags.Year, "nineteen"));

        Assert.Equal(TagEditStatus.Invalid, album.Status);
        Assert.Equal(TagEditStatus.Invalid, year.Status);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Null((await db.MediaFiles.SingleAsync(Token)).TagEditsJson);
        Assert.Null((await db.ReleaseGroups.SingleAsync(Token)).EditsJson);
    }

    [Fact]
    public async Task WithFileWritingOffNothingIsSaved()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var file = await FileIdAsync();
        var saved = await SaveAsync(Build(allowMutation: false), Album, new TagEdit(file, CatalogueTags.Title, "Mine"));

        Assert.Equal(TagEditStatus.MutationOff, saved.Status);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Null((await db.MediaFiles.SingleAsync(Token)).TagEditsJson);
    }

    [Fact]
    public async Task AnAgentsSaveIsRecordedAsTheAgents()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        var file = await FileIdAsync();

        await services.GetRequiredService<TagWriteService>()
            .SaveTagsAsync(Album, [new TagEdit(file, CatalogueTags.Title, "Mine")], new AgentCallerContext(), Token);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.All(
            await db.DomainEvents
                .Where(entry => entry.Type == "tagging.person.saved" || entry.Type == "tagging.catalogue.written")
                .ToListAsync(Token),
            entry => Assert.Equal(AgentCallerContext.AgentId, entry.ActorId));
    }

    [Fact]
    public async Task APersonsTrackNumberOnAnAlbumOnlyFileIsWrittenOverTheFilesOwn()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var row = await db.MediaFiles.SingleAsync(Token);
            row.ReleaseId = null;
            row.TrackId = null;
            row.FolderPosition = 4;
            await db.SaveChangesAsync(Token);
        }

        var services = Build();
        await OutsideAsync(services, $"{Album}/01 track.flac", CatalogueTags.TrackNumber, "7");

        // The file's own number is kept by the pass, and a person's replaces it.
        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("7", (await TagsAsync($"{Album}/01 track.flac")).Fields["TRACKNUMBER"]);

        await SaveAsync(services, Album, new TagEdit(await FileIdAsync(), CatalogueTags.TrackNumber, "3"));
        Assert.Equal("3", (await TagsAsync($"{Album}/01 track.flac")).Fields["TRACKNUMBER"]);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("ogg")]
    public async Task AFieldAPersonNamesIsWrittenWhereItsContainerKeepsItAndTakenOutAgain(string extension)
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.{extension}", 1));

        var services = Build();
        var file = await FileIdAsync();

        var saved = await SaveAsync(services, Album, new TagEdit(file, "Occasion", "Calm"));

        Assert.Equal(TagEditStatus.Saved, saved.Status);

        await using (var why = PostgresFixture.CreateContext(_connectionString))
        {
            var refused = await why.DomainEvents.Where(entry => entry.Type.EndsWith(".aborted")).Select(entry => entry.PayloadJson).ToListAsync(Token);
            Assert.True(saved.Written == 1, string.Join(" | ", refused));
        }
        Assert.Equal("Calm", (await TagsAsync($"{Album}/01 track.{extension}")).Find("Occasion"));

        var state = await services.GetRequiredService<TagWriteService>().FolderTagsAsync(Album, Token);
        var mood = state!.Files.Single().Cells.Single(cell => cell.Field == "Occasion");
        Assert.True(mood.Mine);
        Assert.Equal("Calm", mood.File);

        await SaveAsync(services, Album, new TagEdit(file, "Occasion", null));
        Assert.Null((await TagsAsync($"{Album}/01 track.{extension}")).Find("Occasion"));
    }

    [Theory]
    [InlineData("TKEY")]
    [InlineData("Mood")]
    [InlineData("MusicBrainz Track Id")]
    [InlineData("ACOUSTID_ID")]
    [InlineData("a=b")]
    [InlineData("title")]
    [InlineData("Lyrics:eng")]
    [InlineData("Publisher")]
    public async Task ANameNoContainerCanSafelyCarryIsRefused(string name)
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var saved = await SaveAsync(Build(), Album, new TagEdit(await FileIdAsync(), name, "x"));

        Assert.Equal(TagEditStatus.Invalid, saved.Status);
    }

    [Fact]
    public async Task AnAlbumsYearIsWrittenOverTheYearOrDateAnAlbumOnlyFileCarries()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1), ($"{Album}/02 track.flac", 2));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            foreach (var row in await db.MediaFiles.ToListAsync(Token))
            {
                row.ReleaseId = null;
                row.TrackId = null;
            }

            await db.SaveChangesAsync(Token);
        }

        var services = Build();
        await OutsideAsync(services, $"{Album}/01 track.flac", CatalogueTags.Year, "2015");

        using (var tagged = TagLib.File.Create(Path.Combine(_root, Album, "02 track.flac")))
        {
            ((TagLib.Ogg.XiphComment)tagged.GetTag(TagLib.TagTypes.Xiph, true)).SetField("DATE", "1959-08-17");
            tagged.Save();
        }

        // The pass keeps each file's own; the album's year a person typed replaces both.
        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("2015", (await TagsAsync($"{Album}/01 track.flac")).Fields["YEAR"]);

        var saved = await SaveAsync(services, Album, new TagEdit(null, CatalogueTags.Year, "1979"));

        Assert.Equal(2, saved.Written);
        Assert.Equal("1979", (await TagsAsync($"{Album}/01 track.flac")).Fields["YEAR"]);
        Assert.Equal("1979", (await TagsAsync($"{Album}/02 track.flac")).Fields["YEAR"]);

        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("1979", (await TagsAsync($"{Album}/01 track.flac")).Fields["YEAR"]);
    }

    [Fact]
    public async Task UndoPutsBackOnlyWhatItsSaveChangedOnTheAlbum()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        await SaveAsync(services, Album, new TagEdit(null, CatalogueTags.Album, "Kind of Blue (Mono)"));

        // The album page corrects something else since.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var album = await db.ReleaseGroups.SingleAsync(Token);
            album.EditsJson = PersonEdits.Write(new Dictionary<string, string?>(PersonEdits.Read(album.EditsJson)) { ["review"] = "Later" });
            await db.SaveChangesAsync(Token);
        }

        await UndoAsync(services, Album);

        await using var check = PostgresFixture.CreateContext(_connectionString);
        var edits = PersonEdits.Read((await check.ReleaseGroups.SingleAsync(Token)).EditsJson);

        Assert.False(edits.ContainsKey("title"));
        Assert.Equal("Later", edits["review"]);
    }

    [Fact]
    public async Task ATypedAlbumArtistIsTheFolderAndLinksNobody()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis, John Coltrane/Kind of Blue/01.flac", 1));
        await BillColtraneAsync();

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        await RunAsync(TagWriteScope.Library, services);

        var saved = await SaveAsync(
            services, "Miles Davis/Kind of Blue (1959)", new TagEdit(null, CatalogueTags.AlbumArtist, "Miles Davis & John Coltrane"));

        Assert.Equal("Miles Davis & John Coltrane/Kind of Blue (1959)", saved.Folder);
        Assert.False(Directory.Exists(Path.Combine(_root, "John Coltrane", "Kind of Blue (1959)")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Miles Davis", "Kind of Blue (1959)")));
    }

    [Fact]
    public async Task ASaveIsRefusedWhileAnotherPassRuns()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        Assert.True(services.GetRequiredService<LibraryWorkGate>().TryEnter("library.scan", out var lease));

        using (lease)
        {
            var saved = await SaveAsync(services, Album, new TagEdit(await FileIdAsync(), CatalogueTags.Title, "Mine"));
            Assert.Equal(TagEditStatus.Busy, saved.Status);
        }

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Null((await db.MediaFiles.SingleAsync(Token)).TagEditsJson);
    }

    /// <summary>
    /// ATL writes a four-character name into an MP4 as an atom of that name,
    /// which TagLib# does not read as a field: the second reading is what refuses it.
    /// </summary>
    [Fact]
    public async Task AFieldOfOnesOwnTheSecondLibraryCannotFindIsNotWritten()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.m4a", 1));

        var services = Build();
        var full = Path.Combine(_root, Album, "01 track.m4a");
        var before = await File.ReadAllBytesAsync(full, Token);

        using var scope = services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<TagWriter>();
        var plan = await writer.PlanAsync(
            new LibraryPath($"{Album}/01 track.m4a"), new Dictionary<string, string>(), new Dictionary<string, string?> { ["Mood"] = "Calm" }, Token);

        var write = await writer.ApplyAsync(plan, "file", "run", "owner", TagWriteService.EventPrefix, Token);

        Assert.NotEqual(TagWriteStatus.Written, write.Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(full, Token));
    }

    [Fact]
    public async Task AFoldersTypedYearStaysWhenItIsFiledUnderAPressingLater()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1), ($"{Album}/02 track.flac", 2));

        // One file held to the album alone, one on the pressing.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var row = await db.MediaFiles.SingleAsync(file => file.Path.EndsWith("01 track.flac"), Token);
            row.ReleaseId = null;
            row.TrackId = null;
            await db.SaveChangesAsync(Token);
        }

        var services = Build();
        var saved = await SaveAsync(services, Album, new TagEdit(null, CatalogueTags.Year, "1979"));

        Assert.Equal(2, saved.Written);
        Assert.Equal("1979", (await TagsAsync($"{Album}/01 track.flac")).Fields["YEAR"]);
        Assert.Equal("1979", (await TagsAsync($"{Album}/02 track.flac")).Fields["YEAR"]);

        // Filed under the pressing since: the pressing's 1959 does not take it back.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var pressing = await db.Releases.SingleAsync(release => release.Mbid == ReleaseMbid, Token);
            var row = await db.MediaFiles.SingleAsync(file => file.Path.EndsWith("01 track.flac"), Token);
            row.ReleaseId = pressing.Id;
            row.TrackId = (await db.Tracks.FirstAsync(track => track.Position == 1, Token)).Id;
            await db.SaveChangesAsync(Token);
        }

        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("1979", (await TagsAsync($"{Album}/01 track.flac")).Fields["YEAR"]);

        var state = await services.GetRequiredService<TagWriteService>().FolderTagsAsync(Album, Token);
        var year = state!.Album.Single(cell => cell.Field == CatalogueTags.Year);

        Assert.True(year.Mine);
        Assert.Equal("1979", year.Value);
    }

    [Fact]
    public async Task ANumberTakenOutTakesItsTotalAndTheFileStaysWritable()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("5", (await TagsAsync($"{Album}/01 track.flac")).Fields["TRACKTOTAL"]);

        var file = await FileIdAsync();
        var saved = await SaveAsync(services, Album, new TagEdit(file, CatalogueTags.TrackNumber, null));

        Assert.Equal(1, saved.Written);
        var tags = await TagsAsync($"{Album}/01 track.flac");
        Assert.False(tags.Fields.ContainsKey("TRACKNUMBER"));
        Assert.False(tags.Fields.ContainsKey("TRACKTOTAL"));

        Assert.Equal(1, (await SaveAsync(services, Album, new TagEdit(file, CatalogueTags.Title, "Mine"))).Written);
    }

    [Fact]
    public async Task ASaveWritesItsFolderAndNothingElse()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));
        var other = await SeedOtherAlbumAsync("Someone/Another Record/01 track.flac");

        var full = Path.Combine(_root, other);
        var before = await File.ReadAllBytesAsync(full, Token);

        Guid file;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            file = (await db.MediaFiles.SingleAsync(row => row.Path.StartsWith(Album), Token)).Id.Value;
        }

        var saved = await SaveAsync(Build(), Album, new TagEdit(file, CatalogueTags.Title, "Mine"));

        Assert.Equal(1, saved.Written);
        Assert.Equal(before, await File.ReadAllBytesAsync(full, Token));
    }

    [Fact]
    public async Task APersonsTrackNumberIsWrittenBesideADiscNumber()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var row = await db.MediaFiles.SingleAsync(Token);
            row.ReleaseId = null;
            row.TrackId = null;
            await db.SaveChangesAsync(Token);
        }

        var services = Build();
        await OutsideAsync(services, $"{Album}/01 track.flac", CatalogueTags.DiscNumber, "2");

        await SaveAsync(services, Album, new TagEdit(await FileIdAsync(), CatalogueTags.TrackNumber, "3"));

        Assert.Equal("3", (await TagsAsync($"{Album}/01 track.flac")).Fields["TRACKNUMBER"]);
    }

    [Fact]
    public async Task AnAlbumsTitleCannotBeTakenOut()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var saved = await SaveAsync(Build(), Album, new TagEdit(null, CatalogueTags.Album, null));

        Assert.Equal(TagEditStatus.Invalid, saved.Status);
    }

    private async Task<TagEditResult> SaveAsync(ServiceProvider services, string folder, params TagEdit[] changes) =>
        await services.GetRequiredService<TagWriteService>()
            .SaveTagsAsync(folder, changes, new SingleUserCallerContext(), Token);

    private async Task<Guid> FileIdAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        return (await db.MediaFiles.SingleAsync(Token)).Id.Value;
    }

    /// <summary>Another tagger's edit: through the same writer, under a prefix nothing here reads.</summary>
    private async Task OutsideAsync(ServiceProvider services, string path, string field, string value)
    {
        using var scope = services.CreateScope();

        var writer = scope.ServiceProvider.GetRequiredService<TagWriter>();
        var plan = await writer.PlanAsync(new LibraryPath(path), new Dictionary<string, string> { [field] = value }, Token);

        Assert.Equal(
            TagWriteStatus.Written,
            (await writer.ApplyAsync(plan, "elsewhere", "another-tagger", "someone", "test.outside", Token)).Status);
    }

    /// <summary>A file the catalogue knows nothing about: no recording, no album.</summary>
    private async Task<Guid> SeedBareAsync(string path)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(Corpus.Flac, full, overwrite: true);

        var facts = new FileInfo(full);
        var row = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = path,
            SizeBytes = facts.Length,
            LastModifiedUtc = StoreTime.ToStorePrecision(new DateTimeOffset(facts.LastWriteTimeUtc)),
        };

        await using var db = PostgresFixture.CreateContext(_connectionString);
        db.MediaFiles.Add(row);
        await db.SaveChangesAsync(Token);

        return row.Id.Value;
    }
}
