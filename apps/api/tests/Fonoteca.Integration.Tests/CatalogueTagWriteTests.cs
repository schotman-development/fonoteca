using System.Diagnostics;
using System.Security.Cryptography;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Tagging;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Writing the whole catalogue into a file — ADR 0002's path, many fields at once.
/// </summary>
/// <remarks>
/// <see cref="AcoustIdTagWriteTests"/>'s companion, and the stakes are the same
/// or higher: this rewrites every identified file in the library rather than one
/// field on the ones a pass has just decided.
///
/// The per-format assertions go through <b>ffprobe</b> rather than either of the
/// two libraries under test, exactly as that class's do. ATL agreeing with
/// TagLib# proves they agree; a third tool from an unrelated project proves the
/// tag is where the format says it goes. <c>MUSICBRAINZ TRACK ID</c> — which is
/// what ATL produces from the title-case spelling on a FLAC — would satisfy both
/// of ours and be invisible to Picard.
/// </remarks>
public sealed class CatalogueTagWriteTests : IDisposable
{
    private static readonly Guid Recording = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Release = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Group = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Artist = new("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Cluster = new("55555555-5555-4555-8555-555555555555");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fonoteca-catalogue-tags-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingEventLog _events = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public CatalogueTagWriteTests() => Directory.CreateDirectory(_root);

    /// <summary>What the catalogue would say about a file it has fully answered.</summary>
    private static CatalogueTagSource Answered => new()
    {
        TrackTitle = "So What",
        RecordingTitle = "So What",
        RecordingMbid = new Mbid(Recording),
        TrackNumber = 1,
        TrackTotal = 5,
        DiscNumber = 1,
        DiscTotal = 1,
        AlbumTitle = "Kind of Blue",
        ReleaseMbid = new Mbid(Release),
        ReleaseGroupMbid = new Mbid(Group),
        Year = 1959,
        ArtistCredit = "Miles Davis",
        ArtistMbids = [new Mbid(Artist)],
        AlbumArtistCredit = "Miles Davis",
        AlbumArtistMbids = [new Mbid(Artist)],
        AcoustId = new AcoustId(Cluster),
    };

    /// <summary>
    /// The two spellings, each confirmed by a tool that is neither of ours.
    /// </summary>
    /// <remarks>
    /// These are the authority on <see cref="CatalogueTagFields"/>. If a future
    /// ATL changes where it puts a custom field, this fails rather than a
    /// library quietly acquiring identifiers no other tool reads.
    /// </remarks>
    [Theory]
    [InlineData("flac", "MUSICBRAINZ_TRACKID", "MUSICBRAINZ_ALBUMID")]
    [InlineData("mp3", "MusicBrainz Track Id", "MusicBrainz Album Id")]
    [InlineData("m4a", "MusicBrainz Track Id", "MusicBrainz Album Id")]
    public async Task TheIdentifiersLandWhereEveryOtherTaggerLooksForThem(
        string extension,
        string expectedRecordingTag,
        string expectedReleaseTag)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        var result = await Write(writer, path);

        Assert.Equal(TagWriteStatus.Written, result.Status);

        var tags = TagsReportedByFfprobe(path);

        Assert.Equal(Recording.ToString("D"), tags[expectedRecordingTag], ignoreCase: true);
        Assert.Equal(Release.ToString("D"), tags[expectedReleaseTag], ignoreCase: true);
    }

    /// <summary>
    /// The ordinary fields, through the properties ATL owns rather than as custom ones.
    /// </summary>
    /// <remarks>
    /// A title put into <c>AdditionalFields["TITLE"]</c> is a second title beside
    /// the real one on every container that has a real one — valid, invisible,
    /// and only detectable with a third tool. ffprobe reports the real one under
    /// its own lowercase name, so finding the value there is the proof.
    /// </remarks>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task TheOrdinaryFieldsGoWhereAPlayerReadsThem(string extension)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        var tags = TagsReportedByFfprobe(path);

        // Case-insensitively, and only here: Vorbis comments come back as TITLE
        // and ID3 as title, which is the container's business rather than ours.
        // The identifier assertions above stay ordinal, because there the
        // underscore and the spaces are exactly what is being checked.
        Assert.Equal("So What", Tag(tags, "title"));
        Assert.Equal("Miles Davis", Tag(tags, "artist"));
        Assert.Equal("Kind of Blue", Tag(tags, "album"));
        Assert.Contains("1959", Tag(tags, "date") ?? "", StringComparison.Ordinal);
    }

    private static string? Tag(Dictionary<string, string> tags, string name) => tags
        .FirstOrDefault(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
        .Value;

    /// <summary>
    /// The property that makes running this over a whole library affordable.
    /// </summary>
    /// <remarks>
    /// There is no <c>TagsWrittenUtc</c> column and no migration: the diff is the
    /// worklist. A second run reads each file, finds it already says what the
    /// catalogue says, and does not open it for writing — so re-running after
    /// enriching one more album costs a tag read per file rather than eight
    /// thousand container rewrites.
    /// </remarks>
    [Fact]
    public async Task WritingTheSameCatalogueTwiceRewritesNothing()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        var afterFirst = Hash(path);
        var again = await Write(writer, path);

        Assert.Equal(TagWriteStatus.NothingToDo, again.Status);
        Assert.Equal(afterFirst, Hash(path));

        // One journal entry, not two: an undo entry for a write that did not
        // happen is worse than useless, because undoing it restores nothing.
        Assert.Single(_events.Entries);
    }

    /// <summary>
    /// The property everything else rests on: the audio is not touched.
    /// </summary>
    /// <remarks>
    /// Decoded to raw PCM with ffmpeg and hashed, before and after. Comparing the
    /// files themselves would prove nothing — the container legitimately changes.
    /// </remarks>
    [Fact]
    public async Task TheAudioItselfIsBitIdenticalAfterTheWrite()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);
        var before = DecodedAudioHash(path);

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        Assert.Equal(before, DecodedAudioHash(path));
    }

    [Fact]
    public async Task EmbeddedArtworkSurvivesTheWrite()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.FlacWithArtwork);
        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        var before = await reader.ReadAsync(path, cancellationToken: Token);
        Assert.Equal(1, before.PictureCount);

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        var after = await reader.ReadAsync(path, cancellationToken: Token);

        Assert.Equal(1, after.PictureCount);
        Assert.Equal(before.PictureDigests, after.PictureDigests);
    }

    /// <summary>
    /// Correcting a field the file already held is an edit, not a verification failure.
    /// </summary>
    /// <remarks>
    /// The check that a write disturbed nothing else is one-directional and
    /// reports <i>everything</i> that moved, so on a multi-field write most of
    /// what it reports is the point. Excluding the intended fields is what makes
    /// overwriting a wrong album name possible at all — and the AcoustID path had
    /// the same latent problem, where replacing an existing, different AcoustID
    /// always failed verification and rolled itself back.
    /// </remarks>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task CorrectingAValueTheFileAlreadyCarriedIsNotReadAsLosingIt(string extension)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        Assert.Equal(TagWriteStatus.Written, (await Write(writer, path)).Status);

        // The same file again, saying something else about every field. The
        // exclusion matches ATL's own key names, so a container that reported
        // its custom fields under a prefix would abort here rather than in
        // production.
        var corrected = Answered with
        {
            TrackTitle = "Blue in Green",
            AlbumTitle = "Kind of Blue (Legacy Edition)",
            ArtistCredit = "Miles Davis Sextet",
            TrackNumber = 3,
            Year = 1997,
            RecordingMbid = new Mbid(new Guid("99999999-9999-4999-8999-999999999999")),
        };

        var plan = await writer.PlanAsync(path, CatalogueTags.For(corrected), Token);
        var result = await writer.ApplyAsync(
            plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Written, result.Status);

        var tags = TagsReportedByFfprobe(path);

        Assert.Equal("Blue in Green", Tag(tags, "title"));
        Assert.Equal("Kind of Blue (Legacy Edition)", Tag(tags, "album"));
        Assert.Contains("1997", Tag(tags, "date") ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The default posture: a complete dry run that changes not one byte.
    /// </summary>
    [Fact]
    public async Task NothingIsWrittenWhileFileMutationIsDisabled()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac, allowMutation: false);
        var before = Hash(path);

        var result = await Write(writer, path);

        Assert.Equal(TagWriteStatus.Refused, result.Status);
        Assert.Equal(before, Hash(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        // The plan is real, which is what makes the run a dry run rather than a
        // no-op: the whole diff has been computed and journalled.
        Assert.NotNull(result.Plan);
        Assert.False(result.Plan!.IsNoOp);

        var refusal = Assert.Single(_events.Entries);
        Assert.Equal("tagging.catalogue.refused", refusal.Type);
    }

    /// <summary>
    /// The undo journal has to distinguish "was absent" from "was blank".
    /// </summary>
    /// <remarks>
    /// Reversing an absent field means removing it; reversing a blank one means
    /// restoring a blank. Every field on an untagged FLAC is the first case.
    /// </remarks>
    [Fact]
    public async Task TheUndoJournalRecordsEveryFieldItChanged()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);

        await Write(writer, path);

        var entry = Assert.Single(_events.Entries);

        Assert.Equal("tagging.catalogue.written", entry.Type);
        Assert.Equal("file", entry.SubjectType);
        Assert.Equal("batch", entry.CorrelationId);
        Assert.Contains("\"previous\":null", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(CatalogueTags.RecordingId, entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(Recording.ToString("D"), entry.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A container with nowhere to put these is reported, not failed.</summary>
    [Fact]
    public async Task AContainerThatCannotCarryTheTagsIsReportedRatherThanTreatedAsAFailure()
    {
        SkipWithoutTools();

        File.Copy(Corpus.Flac, Path.Combine(_root, "track.dsf"));

        var writer = Writer(allowMutation: true);
        var path = new LibraryPath("track.dsf");

        var plan = await writer.PlanAsync(path, CatalogueTags.For(Answered), Token);
        Assert.Null(plan);

        var result = await writer.ApplyAsync(
            plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Unsupported, result.Status);
    }

    /// <summary>
    /// The caller is handed the new size and timestamp because it must store them.
    /// </summary>
    /// <remarks>
    /// A tag write changes the bytes. A catalogue still holding the old size and
    /// mtime sees the file as modified on the next scan and discards everything
    /// derived from it — over a library-wide run, that is the whole catalogue.
    /// </remarks>
    [Fact]
    public async Task ACommittedWriteReportsTheFilesNewSizeAndTimestamp()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Mp3);

        var result = await Write(writer, path);

        Assert.NotNull(result.Committed);

        var onDisk = new FileInfo(Path.Combine(_root, path.Value));

        Assert.Equal(onDisk.Length, result.Committed!.SizeBytes);
        Assert.Equal(new DateTimeOffset(onDisk.LastWriteTimeUtc), result.Committed.LastModifiedUtc);
    }

    /// <summary>
    /// A billing line containing a semicolon is written whole, not resplit.
    /// </summary>
    /// <remarks>
    /// ATL splits and rejoins field values on a separator character, and its
    /// default is <c>';'</c>. An album artist billed
    /// <c>"Dvořák, Ginastera, Sarasate; Hilary Hahn, …"</c> was therefore written
    /// as two values and read back joined by a bare <c>';'</c> — one space short
    /// — which <see cref="TagWriter"/> correctly read as a field it had not meant
    /// to change, and aborted the whole write over. <see cref="TaggingDefaults"/>
    /// is what stops it.
    ///
    /// Not a corner case: MusicBrainz uses <c>"; "</c> as the join phrase that
    /// separates composers from performers, so this refused every classical
    /// release carrying one. Twenty of them in the target library, 489 files,
    /// none of which could be tagged at all — and the failure was total rather
    /// than partial, which is the only reason it was noticed.
    ///
    /// Asserted through ffprobe, like the rest of this class, and compared
    /// <b>ordinally</b> — the entire difference between pass and fail is one
    /// space.
    /// </remarks>
    [Fact]
    public async Task ASemicolonInACreditLineSurvivesTheWrite()
    {
        SkipWithoutTools();

        const string Billed = "Dvořák, Ginastera, Sarasate; Hilary Hahn, Frankfurt Radio Symphony";

        var (writer, path) = Given(Corpus.Flac);

        var plan = await writer.PlanAsync(
            path,
            CatalogueTags.For(Answered with { AlbumArtistCredit = Billed }),
            Token);

        var result = await writer.ApplyAsync(
            plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);

        Assert.Equal(TagWriteStatus.Written, result.Status);
        Assert.Equal(Billed, Tag(TagsReportedByFfprobe(path), "album_artist"), StringComparer.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    /// <summary>Kept as a literal so the test asserts the wire value, not the constant.</summary>
    private const string TagWriteServiceEventPrefix = "tagging.catalogue";

    private (TagWriter Writer, LibraryPath Path) Given(string corpusFile, bool allowMutation = true)
    {
        var name = Corpus.CopyInto(_root, corpusFile)[0];
        return (Writer(allowMutation), new LibraryPath(name));
    }

    private TagWriter Writer(bool allowMutation)
    {
        var store = new FileSystemAudioFileStore(_root);

        return new TagWriter(
            store,
            new TagReader(store),
            _events,
            new FixedClock(),
            new TagWriterOptions { AllowFileMutation = allowMutation });
    }

    private async Task<TagWriteResult> Write(TagWriter writer, LibraryPath path)
    {
        var plan = await writer.PlanAsync(path, CatalogueTags.For(Answered), Token);
        return await writer.ApplyAsync(plan, "file-1", "batch", "system", TagWriteServiceEventPrefix, Token);
    }

    private static string SourceFor(string extension) => extension switch
    {
        "flac" => Corpus.Flac,
        "mp3" => Corpus.Mp3,
        _ => Corpus.M4a,
    };

    private string Hash(LibraryPath path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, path.Value))));

    /// <summary>SHA-256 of the decoded PCM, so the container may change and the audio may not.</summary>
    private string DecodedAudioHash(LibraryPath path) =>
        RunTool("ffmpeg", ["-v", "error", "-i", Path.Combine(_root, path.Value), "-f", "s16le", "-"], binary: true);

    /// <summary>Every tag ffprobe can see, by the name it sees it under.</summary>
    private Dictionary<string, string> TagsReportedByFfprobe(LibraryPath path)
    {
        var output = RunTool(
            "ffprobe",
            [
                "-v", "error", "-show_entries", "format_tags",
                "-of", "default=noprint_wrappers=1", Path.Combine(_root, path.Value),
            ],
            binary: false);

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("TAG:", StringComparison.Ordinal)) continue;

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0) continue;

            tags[line[4..separator]] = line[(separator + 1)..].Trim();
        }

        return tags;
    }

    private static string RunTool(string tool, string[] arguments, bool binary)
    {
        var startInfo = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;

        if (binary)
        {
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"{tool} exited {process.ExitCode}: {stderr}");

            return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        }

        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return stdout;
    }

    private static void SkipWithoutTools()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");
    }

    /// <summary>Records what was appended without needing a database.</summary>
    private sealed class RecordingEventLog : IEventLog
    {
        private readonly List<DomainEvent> _entries = [];

        public IReadOnlyList<DomainEvent> Entries => _entries;

        public Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            _entries.Add(domainEvent);
            return Task.CompletedTask;
        }

        public Task AppendAsync(
            IReadOnlyCollection<DomainEvent> events,
            CancellationToken cancellationToken = default)
        {
            _entries.AddRange(events);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<DomainEvent> ReadSubjectAsync(
            string subjectType,
            string subjectId,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in _entries.Where(
                e => e.SubjectType == subjectType && e.SubjectId == subjectId))
            {
                yield return entry;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
