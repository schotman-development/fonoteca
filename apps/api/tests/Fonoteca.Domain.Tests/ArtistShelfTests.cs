using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Whether a folder is one artist's own shelf.
/// </summary>
/// <remarks>
/// <b>The half of the portrait feature that can be wrong in a way nobody
/// notices.</b> Said yes too readily and a living violinist's photograph lands
/// on a dead composer's shelf, where every other player reading the same disk
/// shows it; said no too readily and the picture goes nowhere and the page says
/// nothing. The cases below are split that way.
/// </remarks>
public sealed class ArtistShelfTests
{
    [Fact]
    public void AFolderNamedForTheArtistIsTheirs() =>
        Assert.True(ArtistShelf.IsNamedFor("Janine Jansen", "Janine Jansen", null));

    [Fact]
    public void AComposersShelfIsNotThePerformersEvenWhenItHoldsTheirPlaying() =>
        // The measured shape: 47 of her 54 files sit under these two, and every
        // one of them is on her page and reaches the pass under her scope.
        Assert.False(ArtistShelf.IsNamedFor("Johann Sebastian Bach", "Janine Jansen", null));

    [Theory]
    [InlineData("Sgt Peppers", "Sgt. Pepper's")]
    [InlineData("AC-DC", "AC/DC")]
    [InlineData("beyonce", "Beyoncé")]
    [InlineData("Tedeschi  Trucks  Band", "Tedeschi Trucks Band")]
    public void SpellingThatOnlyDiffersInPunctuationOrCaseStillMatches(string folder, string name) =>
        // What a ripper wrote on a disk against what MusicBrainz prints. The
        // normaliser is the same one the Qobuz portrait search is guarded by.
        Assert.True(ArtistShelf.IsNamedFor(folder, name, null));

    [Fact]
    public void ABandIsNotItsMember() =>
        // Derek Trucks is followed here and has no shelf: his music is filed
        // under the band, and "The" is a word rather than punctuation, so
        // nothing folds these together.
        Assert.False(ArtistShelf.IsNamedFor("Tedeschi Trucks Band", "Derek Trucks", null));

    [Fact]
    public void TheEnglishAliasIsComparedAsWell() =>
        // A library on a western disk is filed under the alias far more often
        // than under the name the catalogue holds.
        Assert.True(ArtistShelf.IsNamedFor("Joe Hisaishi", "久石譲", "Joe Hisaishi"));

    [Fact]
    public void TheLeafIsWhatIsCompared() =>
        // Given a library-relative folder rather than a bare name, which is how
        // the tag write asks — and what keeps a deeper library working.
        Assert.True(ArtistShelf.IsNamedFor("Classical/Janine Jansen", "Janine Jansen", null));

    [Theory]
    [InlineData("Janine Jansen/")]
    [InlineData("Classical\\Janine Jansen")]
    public void ASeparatorOnEitherEndOrEitherSlashIsStillTheSameShelf(string folder) =>
        Assert.True(ArtistShelf.IsNamedFor(folder, "Janine Jansen", null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsNobodysShelf(string? folder) =>
        Assert.False(ArtistShelf.IsNamedFor(folder, "Janine Jansen", null));

    [Fact]
    public void AFolderOfPurePunctuationIsNotEverybodysShelf()
    {
        // Both fold to an empty string, and equal-and-empty would make every
        // such folder match every such artist. "!!!" is a real band.
        Assert.False(ArtistShelf.IsNamedFor("...", "!!!", null));
        Assert.True(ArtistShelf.IsNamedFor("!!!", "!!!", null));
    }

    [Fact]
    public void AnArtistWithNoAliasIsNotMatchedByAnEmptyOne() =>
        // `LatinName` is null far more often than not, and a blank comparing
        // equal to a blank folder name would be a match on nothing.
        Assert.False(ArtistShelf.IsNamedFor("Some Folder", string.Empty, null));
}
