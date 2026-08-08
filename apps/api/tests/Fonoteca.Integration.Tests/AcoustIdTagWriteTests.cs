using System.Diagnostics;
using System.Security.Cryptography;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Events;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Tagging;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The verified write path — ADR 0002, against real files.
/// </summary>
/// <remarks>
/// The highest-stakes tests in the repository. Everything else this application
/// does is recoverable by rescanning; a botched write is not.
///
/// The per-format assertions use <b>ffprobe</b> rather than either of the two
/// libraries under test. That is the point: ATL agreeing with TagLib# proves
/// they agree, and a third tool from an unrelated project proves the tag is
/// actually where the format says it goes. A Vorbis comment called
/// <c>ACOUSTID ID</c> — which is what ATL produces if handed the ID3 spelling —
/// would satisfy both libraries and be invisible to Picard.
/// </remarks>
public sealed class AcoustIdTagWriteTests : IDisposable
{
    private static readonly Guid Identified = new("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fonoteca-tagwrite-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingEventLog _events = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public AcoustIdTagWriteTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// The three spellings, each confirmed by a tool that is neither of ours.
    /// </summary>
    /// <remarks>
    /// These are the authority on <see cref="AcoustIdTagField"/>. If a future ATL
    /// changes where it puts a custom field, this fails rather than 7,735 files
    /// quietly acquiring a tag no other tool reads.
    /// </remarks>
    [Theory]
    [InlineData("flac", "ACOUSTID_ID")]
    [InlineData("mp3", "Acoustid Id")]
    [InlineData("m4a", "Acoustid Id")]
    public async Task TheAcoustIdLandsWhereEveryOtherTaggerLooksForIt(string extension, string expectedTag)
    {
        SkipWithoutTools();

        var (writer, path) = Given(SourceFor(extension));

        var result = await Write(writer, path);

        Assert.Equal(TagWriteStatus.Written, result.Status);
        Assert.Equal(expectedTag, TagNameReportedByFfprobe(path));
        Assert.Equal(Identified.ToString("D"), TagValueReportedByFfprobe(path), ignoreCase: true);
    }

    [Fact]
    public async Task AWrittenAcoustIdIsReadBackAsTheSameValue()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);
        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        await Write(writer, path);

        Assert.Equal(Identified.ToString("D"), await reader.ReadAcoustIdAsync(path, Token), ignoreCase: true);
    }

    /// <summary>
    /// The 227 files Picard already tagged must cost nothing to skip.
    /// </summary>
    [Fact]
    public async Task AFileThatAlreadyCarriesTheSameAcoustIdIsNotRewritten()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.AlreadyTaggedFlac);
        var before = Hash(path);

        var plan = await writer.PlanAsync(path, new Guid(Corpus.PreExistingAcoustId), Token);
        var result = await writer.ApplyAsync(plan, "file-1", "batch", "system", Token);

        Assert.Equal(TagWriteStatus.NothingToDo, result.Status);
        Assert.Equal(before, Hash(path));
        Assert.Empty(_events.Entries);
    }

    /// <summary>
    /// The default posture, and the one the user chose for the first run.
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

        // The plan is still real, which is what makes a disabled run a complete
        // dry run rather than a no-op: the caller has already paid for the
        // fingerprint and the lookup and can report what it would have done.
        Assert.NotNull(result.Plan);
        Assert.False(result.Plan!.IsNoOp);

        var refusal = Assert.Single(_events.Entries);
        Assert.Equal(AcoustIdTagWriter.RefusedEventType, refusal.Type);
    }

    [Fact]
    public async Task EmbeddedArtworkSurvivesTheWrite()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.FlacWithArtwork);
        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        var before = await reader.ReadAsync(path, cancellationToken: Token);
        Assert.Equal(1, before.PictureCount);

        var result = await Write(writer, path);
        Assert.Equal(TagWriteStatus.Written, result.Status);

        var after = await reader.ReadAsync(path, cancellationToken: Token);

        Assert.Equal(1, after.PictureCount);
        Assert.Equal(before.PictureDigests, after.PictureDigests);
    }

    [Fact]
    public async Task WritingAnAcoustIdChangesNoOtherTag()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.FlacWithArtwork);
        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        var before = await reader.ReadAsync(path, cancellationToken: Token);
        await Write(writer, path);
        var after = await reader.ReadAsync(path, cancellationToken: Token);

        Assert.Empty(before.FieldsLostIn(after));
        Assert.Equal("Corpus", after.Fields["TITLE"]);
        Assert.Equal("Fonoteca", after.Fields["ARTIST"]);
    }

    /// <summary>
    /// The property everything else rests on: the audio is not touched.
    /// </summary>
    /// <remarks>
    /// Decoded to raw PCM with ffmpeg and hashed, before and after. Comparing the
    /// files themselves would prove nothing — the container legitimately changes.
    /// This is the test that makes ADR 0002 mean something rather than being a
    /// procedure nobody checked the outcome of.
    /// </remarks>
    [Fact]
    public async Task TheAudioItselfIsBitIdenticalAfterTheWrite()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);
        var before = DecodedAudioHash(path);

        var result = await Write(writer, path);
        Assert.Equal(TagWriteStatus.Written, result.Status);

        Assert.Equal(before, DecodedAudioHash(path));
    }

    /// <summary>
    /// A container with nowhere to put an AcoustID is reported, not failed.
    /// </summary>
    [Fact]
    public async Task AContainerThatCannotCarryTheTagIsReportedRatherThanTreatedAsAFailure()
    {
        SkipWithoutTools();

        File.Copy(Corpus.Flac, Path.Combine(_root, "track.dsf"));

        var writer = Writer(allowMutation: true);
        var path = new LibraryPath("track.dsf");

        var plan = await writer.PlanAsync(path, Identified, Token);
        Assert.Null(plan);

        var result = await writer.ApplyAsync(plan, "file-1", "batch", "system", Token);
        Assert.Equal(TagWriteStatus.Unsupported, result.Status);
    }

    /// <summary>
    /// The undo journal has to distinguish "was absent" from "was blank".
    /// </summary>
    /// <remarks>
    /// Reversing an absent field means removing it; reversing a blank one means
    /// restoring a blank. A non-nullable string collapses the two and makes undo
    /// wrong forever, silently.
    /// </remarks>
    [Fact]
    public async Task TheUndoJournalRecordsThatTheFieldWasAbsentRatherThanEmpty()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac);

        await Write(writer, path);

        var entry = Assert.Single(_events.Entries);

        Assert.Equal(AcoustIdTagWriter.WrittenEventType, entry.Type);
        Assert.Equal("file", entry.SubjectType);
        Assert.Equal("batch", entry.CorrelationId);
        Assert.Contains("\"previous\":null", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(Identified.ToString("D"), entry.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryFileInOneBatchSharesOneCorrelationId()
    {
        SkipWithoutTools();

        var writer = Writer(allowMutation: true);
        var names = Corpus.CopyInto(_root, Corpus.Flac, Corpus.Mp3, Corpus.M4a);

        foreach (var name in names)
        {
            var plan = await writer.PlanAsync(new LibraryPath(name), Identified, Token);
            await writer.ApplyAsync(plan, name, "one-pass", "system", Token);
        }

        Assert.Equal(3, _events.Entries.Count);
        Assert.Single(_events.Entries.Select(e => e.CorrelationId).Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// The caller is handed the new size and timestamp because it must store them.
    /// </summary>
    /// <remarks>
    /// A tag write changes the bytes. A catalogue still holding the old size and
    /// mtime sees the file as modified on the next scan and discards everything
    /// derived from it — including the AcoustID just written, which means
    /// fingerprinting and looking up the whole library again, forever. Returning
    /// the facts makes closing that loop the obvious thing to do rather than
    /// something to remember.
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
        Assert.Equal(
            new DateTimeOffset(onDisk.LastWriteTimeUtc),
            result.Committed.LastModifiedUtc);
    }

    [Fact]
    public async Task ARefusedWriteLeavesNoStagingFileAnywhereInTheLibrary()
    {
        SkipWithoutTools();

        var (writer, path) = Given(Corpus.Flac, allowMutation: false);

        await Write(writer, path);

        Assert.Empty(Directory.GetFiles(_root, ".*", SearchOption.AllDirectories));
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

    private (AcoustIdTagWriter Writer, LibraryPath Path) Given(string corpusFile, bool allowMutation = true)
    {
        var name = Corpus.CopyInto(_root, corpusFile)[0];
        return (Writer(allowMutation), new LibraryPath(name));
    }

    private AcoustIdTagWriter Writer(bool allowMutation)
    {
        var store = new FileSystemAudioFileStore(_root);

        return new AcoustIdTagWriter(
            store,
            new TagReader(store),
            _events,
            new FixedClock(),
            new TagWriterOptions { AllowFileMutation = allowMutation });
    }

    private async Task<TagWriteResult> Write(AcoustIdTagWriter writer, LibraryPath path)
    {
        var plan = await writer.PlanAsync(path, Identified, Token);
        return await writer.ApplyAsync(plan, "file-1", "batch", "system", Token);
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

    private string? TagNameReportedByFfprobe(LibraryPath path) => FfprobeAcoustIdLine(path)?.Key;

    private string? TagValueReportedByFfprobe(LibraryPath path) => FfprobeAcoustIdLine(path)?.Value;

    private (string Key, string Value)? FfprobeAcoustIdLine(LibraryPath path)
    {
        var output = RunTool(
            "ffprobe",
            [
                "-v", "error", "-show_entries", "format_tags",
                "-of", "default=noprint_wrappers=1", Path.Combine(_root, path.Value),
            ],
            binary: false);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("TAG:", StringComparison.Ordinal)) continue;

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0) continue;

            var key = line[4..separator];

            if (key.Contains("acoustid", StringComparison.OrdinalIgnoreCase))
            {
                return (key, line[(separator + 1)..].Trim());
            }
        }

        return null;
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

        public IAsyncEnumerable<DomainEvent> ReadSubjectAsync(
            string subjectType,
            string subjectId,
            CancellationToken cancellationToken = default) =>
            _entries
                .Where(e => e.SubjectType == subjectType && e.SubjectId == subjectId)
                .ToAsyncEnumerable();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    }
}

file static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
