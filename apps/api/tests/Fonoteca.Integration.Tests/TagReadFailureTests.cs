using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Tagging;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// What the tag readers do with files they cannot parse.
/// </summary>
/// <remarks>
/// Tag parsers meet whatever a decade of other people's tools left behind, and
/// they do not fail politely. ATL raises <see cref="NullReferenceException"/>
/// from <c>EmbeddedPictures</c>; TagLib# raises <c>CorruptFileException</c>. Left
/// alone, those escape into the identification pass — where, before this was
/// fixed, a single one of them stopped a 7,317-file run dead with nothing in the
/// log to say why.
///
/// The rule these tests pin down is not "tolerate anything". It is that the two
/// questions have different answers: <b>asking whether a file already has an
/// AcoustID never fails</b>, because "cannot read it" and "has not got one" lead
/// to the same next step; while <b>reading a file in order to write to it fails
/// loudly</b>, because a file we cannot describe is a file we must not rewrite.
///
/// No database and no collection: these are file reads, and they are the fastest
/// tests in the project.
/// </remarks>
public sealed class TagReadFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fonoteca-tagread-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The exact shape of the forty files that stopped the pass.
    /// </summary>
    [Fact]
    public async Task AFlacCarryingAPrependedId3TagIsReportedAsUnreadableRatherThanCrashing()
    {
        SkipWithoutTools();

        var reader = ReaderOver(Corpus.Id3PrefixedFlac, "id3-prefixed.flac");

        var failure = await Assert.ThrowsAsync<TagReadFailedException>(
            () => reader.ReadAsync(new LibraryPath("id3-prefixed.flac"), null, Token));

        Assert.Equal("ATL", failure.Library);
        Assert.Equal("id3-prefixed.flac", failure.Path.Value);

        // The point of naming the underlying type: this is ATL's bug, not ours,
        // and the day it is fixed upstream this assertion is how we find out.
        Assert.Equal(nameof(NullReferenceException), failure.CauseType);
    }

    /// <summary>
    /// The reason the pass reached file 370 rather than failing on file 1.
    /// </summary>
    /// <remarks>
    /// Stage A asks every file whether it already carries an AcoustID, and these
    /// files answer that question perfectly well. Only stage B — reading the file
    /// in order to write to it — touches the part ATL cannot parse. Worth pinning
    /// down, because it is what makes the failure so late and so confusing.
    /// </remarks>
    [Fact]
    public async Task AskingWhetherAFileAlreadyCarriesAnAcoustIdNeverThrows()
    {
        SkipWithoutTools();

        var names = Corpus.CopyInto(_root, [.. Corpus.All]);
        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        foreach (var name in names)
        {
            var answer = await reader.ReadAcoustIdAsync(new LibraryPath(name), Token);

            // Only the fixture that was built with one should have one. What
            // matters is that nothing threw, whatever the file.
            if (name == Path.GetFileName(Corpus.AlreadyTaggedFlac))
            {
                Assert.Equal(Corpus.PreExistingAcoustId, answer, ignoreCase: true);
            }
        }
    }

    [Fact]
    public async Task AFileWithNoAudioInItIsReportedAsUnreadableByBothLibraries()
    {
        SkipWithoutTools();

        var reader = ReaderOver(Corpus.NotAudioFlac, "not-audio.flac");
        var path = new LibraryPath("not-audio.flac");

        var byWriter = await Assert.ThrowsAsync<TagReadFailedException>(
            () => reader.ReadAsync(path, null, Token));

        var byVerifier = await Assert.ThrowsAsync<TagReadFailedException>(
            () => reader.ReadWithVerifierAsync(path, null, Token));

        Assert.Equal("ATL", byWriter.Library);
        Assert.Equal("TagLib#", byVerifier.Library);
    }

    /// <summary>An ordinary file still reads, which is what makes the rest meaningful.</summary>
    [Fact]
    public async Task AWellFormedFileStillReadsEverythingItCarries()
    {
        SkipWithoutTools();

        var reader = ReaderOver(Corpus.FlacWithArtwork, "artwork.flac");
        var path = new LibraryPath("artwork.flac");

        var snapshot = await reader.ReadAsync(path, null, Token);
        var verifier = await reader.ReadWithVerifierAsync(path, null, Token);

        Assert.Equal(1, snapshot.PictureCount);
        Assert.Equal(1, verifier.PictureCount);
    }

    private static void SkipWithoutTools() =>
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

    private TagReader ReaderOver(string corpusFile, string name)
    {
        File.Copy(corpusFile, Path.Combine(_root, name), overwrite: true);
        return new TagReader(new FileSystemAudioFileStore(_root));
    }
}
