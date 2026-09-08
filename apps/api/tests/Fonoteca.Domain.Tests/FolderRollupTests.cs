using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Which folder row a catalogue path is counted on.
/// </summary>
/// <remarks>
/// The file manager's whole reason for existing is telling two folders of the
/// same music apart, so a rule that counts one folder's files on another's row
/// defeats the screen rather than merely being wrong.
/// </remarks>
public sealed class FolderRollupTests
{
    [Fact]
    public void AFileDirectlyInTheFolderIsAFile()
    {
        var child = FolderRollup.Under("Brahms/Symphony No. 1", "Brahms/Symphony No. 1/01 Allegro.flac");

        Assert.Equal(new FolderChild("01 Allegro.flac", IsDirectory: false), child);
    }

    [Fact]
    public void AFileFurtherDownIsCountedOnTheFolderBetween()
    {
        // A two-disc set: the artist row must say "Symphony No. 1", not "CD 1".
        var child = FolderRollup.Under("Brahms", "Brahms/Symphony No. 1/CD 1/01 Allegro.flac");

        Assert.Equal(new FolderChild("Symphony No. 1", IsDirectory: true), child);
    }

    [Fact]
    public void TheRootIsTheEmptyString()
    {
        var child = FolderRollup.Under(string.Empty, "Brahms/Symphony No. 1/01 Allegro.flac");

        Assert.Equal(new FolderChild("Brahms", IsDirectory: true), child);
    }

    [Fact]
    public void ALooseFileAtTheRootIsAFile()
    {
        Assert.Equal(new FolderChild("stray.flac", IsDirectory: false), FolderRollup.Under("", "stray.flac"));
    }

    [Fact]
    public void ASiblingSharingAPrefixIsNotUnderTheFolder()
    {
        // The trailing slash. Without it the duplicate this screen exists to
        // find would have its files counted on the album it duplicates.
        Assert.Null(FolderRollup.Under("Brahms/Symphony No. 1", "Brahms/Symphony No. 1 (mess)/01.mp3"));
    }

    [Fact]
    public void AnUnrelatedPathIsNotUnderTheFolder() =>
        Assert.Null(FolderRollup.Under("Brahms", "Mahler/Symphony No. 2/01.flac"));

    [Fact]
    public void TheFolderItselfIsNotOneOfItsOwnChildren() =>
        Assert.Null(FolderRollup.Under("Brahms/Symphony No. 1", "Brahms/Symphony No. 1"));

    [Theory]
    [InlineData("/Brahms/")]
    [InlineData("Brahms/")]
    [InlineData("/Brahms")]
    public void SurroundingSeparatorsDoNotChangeTheAnswer(string folder) =>
        Assert.Equal(
            new FolderChild("Symphony No. 1", IsDirectory: true),
            FolderRollup.Under(folder, "Brahms/Symphony No. 1/01 Allegro.flac"));

    [Fact]
    public void CaseIsNotFolded()
    {
        // Two directories differing only in case are two directories on the
        // filesystem this runs against, and merging them would produce a count
        // describing neither.
        Assert.Null(FolderRollup.Under("brahms", "Brahms/Symphony No. 1/01 Allegro.flac"));
    }
}
