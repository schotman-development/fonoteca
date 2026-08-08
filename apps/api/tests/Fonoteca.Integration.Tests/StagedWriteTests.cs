using System.Security.Cryptography;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Staged replacement: the mechanism ADR 0002's "never in place" rests on.
/// </summary>
/// <remarks>
/// Real files on a real filesystem, because every property worth asserting here
/// is a filesystem property. No database.
///
/// The tests are mostly about what happens when a write does <b>not</b> finish.
/// That a successful swap replaces the file is arithmetic; that an abandoned one
/// leaves the original byte-for-byte identical is the reason this class exists,
/// and it is the difference between a bug and a destroyed library.
/// </remarks>
public sealed class StagedWriteTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fonoteca-staged-" + Guid.CreateVersion7().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public StagedWriteTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CommittingReplacesTheOriginal()
    {
        var (store, path) = Given("track.flac", "original bytes");

        var staged = await store.OpenForReplaceAsync(path, Token);
        await using (staged.ConfigureAwait(false))
        {
            await Write(staged, "replacement bytes");
            await staged.CommitAsync(Token);
        }

        Assert.Equal("replacement bytes", await File.ReadAllTextAsync(Absolute("track.flac"), Token));
    }

    /// <summary>
    /// The property the whole design exists for.
    /// </summary>
    /// <remarks>
    /// Any failure in the verification steps abandons the handle without
    /// committing. If that path could damage the original, every one of those
    /// carefully-argued checks would be worse than useless — they would be extra
    /// chances to destroy a file while proving it was fine.
    /// </remarks>
    [Fact]
    public async Task AbandoningAStagedWriteLeavesTheOriginalByteForByteUnchanged()
    {
        var (store, path) = Given("track.flac", "original bytes");
        var before = Hash("track.flac");

        var staged = await store.OpenForReplaceAsync(path, Token);
        await using (staged.ConfigureAwait(false))
        {
            await Write(staged, "replacement that is never committed");
        }

        Assert.Equal(before, Hash("track.flac"));
    }

    [Fact]
    public async Task AbandoningAStagedWriteLeavesNoTemporaryFileBehind()
    {
        var (store, path) = Given("track.flac", "original bytes");

        var staged = await store.OpenForReplaceAsync(path, Token);
        await using (staged.ConfigureAwait(false))
        {
            await Write(staged, "abandoned");
        }

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// A sibling, because <c>rename(2)</c> is only atomic within one filesystem.
    /// </summary>
    [Fact]
    public async Task TheStagingFileSitsBesideItsTargetSoTheSwapIsARename()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Artist", "Album"));
        var relative = Path.Combine("Artist", "Album", "track.flac");
        await File.WriteAllTextAsync(Path.Combine(_root, relative), "original", Token);

        var store = new FileSystemAudioFileStore(_root);

        var staged = await store.OpenForReplaceAsync(new LibraryPath(relative), Token);
        await using (staged.ConfigureAwait(false))
        {
            var stagingDirectory = Path.GetDirectoryName(staged.StagingPath.Value);
            Assert.Equal(Path.Combine("Artist", "Album"), stagingDirectory);
        }
    }

    /// <summary>
    /// A leftover from a killed process must not become a catalogue row.
    /// </summary>
    /// <remarks>
    /// Two independent mechanisms already exclude it — the leading dot makes it
    /// hidden, which the walk skips, and <c>.tmp</c> is not an audio extension —
    /// and this asserts both are actually in force rather than merely intended.
    /// </remarks>
    [Fact]
    public async Task TheStagingFileIsInvisibleToTheLibraryWalk()
    {
        var (store, path) = Given("track.flac", "original");

        var staged = await store.OpenForReplaceAsync(path, Token);
        await using (staged.ConfigureAwait(false))
        {
            await Write(staged, "in progress");

            var name = Path.GetFileName(staged.StagingPath.Value);
            Assert.StartsWith(".", name, StringComparison.Ordinal);
            Assert.False(AudioFormats.IsAudioFile(staged.StagingPath));

            var seen = new List<string>();
            var walk = new LibraryScanner(store).EnumerateAsync(new LibraryWalkReport(), Token);

            await foreach (var file in walk.ConfigureAwait(false))
            {
                seen.Add(file.Path.Value);
            }

            Assert.Equal(["track.flac"], seen);
        }
    }

    /// <summary>
    /// Without this, every tagged file quietly acquires the process umask.
    /// </summary>
    [Fact]
    public async Task TheReplacedFileKeepsThePermissionsTheOriginalHad()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only.");

        var (store, path) = Given("track.flac", "original");

        const UnixFileMode GroupWritable =
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

        File.SetUnixFileMode(Absolute("track.flac"), GroupWritable);

        var staged = await store.OpenForReplaceAsync(path, Token);
        await using (staged.ConfigureAwait(false))
        {
            await Write(staged, "replacement");
            await staged.CommitAsync(Token);
        }

        Assert.Equal(GroupWritable, File.GetUnixFileMode(Absolute("track.flac")));
    }

    [Fact]
    public async Task AStagedWriteOutsideTheLibraryRootIsRefused()
    {
        var store = new FileSystemAudioFileStore(_root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await store.OpenForReplaceAsync(new LibraryPath("../escaped.flac"), Token));
    }

    [Fact]
    public async Task ReplacingAFileThatIsNotThereIsRefusedRatherThanCreatingOne()
    {
        var store = new FileSystemAudioFileStore(_root);

        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await store.OpenForReplaceAsync(new LibraryPath("absent.flac"), Token));

        Assert.False(File.Exists(Absolute("absent.flac")));
    }

    [Fact]
    public async Task TwoStagedWritesOfOneFileDoNotShareAStagingFile()
    {
        var (store, path) = Given("track.flac", "original");

        var first = await store.OpenForReplaceAsync(path, Token);
        await using (first.ConfigureAwait(false))
        {
            var second = await store.OpenForReplaceAsync(path, Token);
            await using (second.ConfigureAwait(false))
            {
                Assert.NotEqual(first.StagingPath.Value, second.StagingPath.Value);
            }
        }
    }

    [Fact]
    public void AStaleStagingFileIsSweptAndAFreshOneIsLeftAlone()
    {
        var store = new FileSystemAudioFileStore(_root);
        var now = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        var stale = Path.Combine(_root, ".track.flac.fonoteca-aaaaaaaa.tmp");
        var fresh = Path.Combine(_root, ".other.flac.fonoteca-bbbbbbbb.tmp");
        var theirs = Path.Combine(_root, "someone-elses.tmp");

        foreach (var file in new[] { stale, fresh, theirs })
        {
            File.WriteAllText(file, "x");
        }

        File.SetLastWriteTimeUtc(stale, now.AddHours(-4).UtcDateTime);
        File.SetLastWriteTimeUtc(fresh, now.AddMinutes(-1).UtcDateTime);
        File.SetLastWriteTimeUtc(theirs, now.AddDays(-30).UtcDateTime);

        var removed = store.RemoveStaleStagingFiles(TimeSpan.FromHours(1), now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(stale));

        // A write happening right now looks exactly like an abandoned one, which
        // is what the age gate is for.
        Assert.True(File.Exists(fresh));

        // And a .tmp we did not create is none of our business.
        Assert.True(File.Exists(theirs));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is not a test failure.
        }
    }

    private (FileSystemAudioFileStore Store, LibraryPath Path) Given(string name, string content)
    {
        File.WriteAllText(Path.Combine(_root, name), content);
        return (new FileSystemAudioFileStore(_root), new LibraryPath(name));
    }

    private string Absolute(string name) => Path.Combine(_root, name);

    private string Hash(string name) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Absolute(name))));

    private static async Task Write(IStagedWrite staged, string content) =>
        await staged.Content.WriteAsync(Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
}
