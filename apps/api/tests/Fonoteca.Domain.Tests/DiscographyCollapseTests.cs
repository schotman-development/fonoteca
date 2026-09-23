using Fonoteca.Domain.Acquisition;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Folding a shelf's editions of one record into one row and a count.
/// </summary>
/// <remarks>
/// <b>What this rule is allowed to take away, and what it must never.</b> It
/// groups rows that are already equal on a folded title and a kind; it asserts
/// no new identity. The cases below are split that way: what it exists to fold,
/// and what it must leave alone — the second half is the important one, because
/// a row wrongly folded is a record nobody is ever offered and nothing on the
/// screen says it was withheld.
/// </remarks>
public sealed class DiscographyCollapseTests
{
    [Fact]
    public void ManyRecordingsOfOneWorkBecomeOneRowAndACount()
    {
        // The measured shape: 132 rows titled "The Four Seasons" on one
        // composer's shelf, different performances, nothing on screen to tell
        // them apart but a year.
        var shelf = Collapse(
            Row("The Four Seasons", "Album", 1989),
            Row("The Four Seasons", "Album", 2005),
            Row("The Four Seasons", "Album", 2021));

        var only = Assert.Single(shelf);
        Assert.Equal(3, only.Count);
        Assert.Equal(1989, only.Row.Year);
    }

    [Fact]
    public void AnAlbumAndTheSingleNamedAfterItStayTwoRecords()
    {
        // Measured on this library: Mark Knopfler's "Sailing to Philadelphia"
        // is both. Folding them is the one way this rule could take a real
        // record away, so the kind is part of the key.
        Assert.Equal(2, Collapse(
            Row("Sailing to Philadelphia", "Album", 2000),
            Row("Sailing to Philadelphia", "Single", 2000)).Count);
    }

    [Fact]
    public void TwoDifferentRecordsThatMerelyShareWordsAreNotFolded()
    {
        // The hazard `ReleaseTitleMatch` refuses containment over, asserted here
        // so nobody reaches for it later: these are two records and stay two.
        Assert.Equal(2, Collapse(
            Row("Greatest Hits", "Album", 2008),
            Row("Greatest Hits, Volume 2", "Album", 2011)).Count);
    }

    [Fact]
    public void AReissueOfTheSameMusicFoldsIntoIt()
    {
        // A shop sells these as two products and they are one record.
        var only = Assert.Single(Collapse(
            Row("Brothers in Arms", "Album", 1985),
            Row("Brothers in Arms (Remastered)", "Album", 1996),
            Row("Brothers in Arms (Deluxe Edition)", "Album", 2005)));

        Assert.Equal(3, only.Count);
    }

    [Fact]
    public void ALiveOrRemixedRecordIsDifferentMusicAndDoesNotFold()
    {
        // The line `Noise` draws, drawn again here. A live record folded into
        // the studio one hides a gap nobody is told about.
        Assert.Equal(3, Collapse(
            Row("Alchemy", "Album", 1984),
            Row("Alchemy (Live)", "Album", 1984),
            Row("Alchemy (Remix)", "Album", 1990)).Count);
    }

    /// <summary>
    /// The forms MusicBrainz actually uses, every one of them a real row here.
    /// </summary>
    /// <remarks>
    /// <b>These are the cases the first draft of this rule got wrong</b>, and
    /// the bare "(Live)" test above passed the whole time it was wrong: the
    /// spelling in the data is "(live version)", and a rule asking only whether
    /// the bracket <i>contained</i> "version" folded every one of them.
    /// </remarks>
    [Theory]
    [InlineData("1989 (Taylor's Version)")]          // A re-recording, separately sold.
    [InlineData("Thriller (Demo Version)")]          // Not the record. It became the tile.
    [InlineData("Unholy (live version)")]
    [InlineData("Billy Preston (Re-Recorded Version)")]
    [InlineData("The Way (Spanglish version)")]      // A different language is different music.
    [InlineData("Das Lied von der Erde (piano version)")]
    [InlineData("Pétrouchka (1947 Version)")]        // Against the 1911 score: two works.
    [InlineData("Goldberg Variations, BWV 988 (version for 2 pianos)")]
    [InlineData("La Mer (solo piano version)")]
    [InlineData("The Life of a Showgirl (Track by Track Version)")]
    [InlineData("Jesus Christ Superstar (Highlights From The 20th Anniversary)")]
    [InlineData("Symphony No. 9 (Staatskapelle Dresden Edition, Vol. 7)")]
    public void AQualifierNamingDifferentMusicIsNotStripped(string title) =>
        Assert.Equal(title, Discography.StripEdition(title));

    [Theory]
    [InlineData("Brothers in Arms (Remastered)", "Brothers in Arms")]
    [InlineData("Minutes to Midnight (Deluxe Edition)", "Minutes to Midnight")]
    [InlineData("evermore (deluxe version)", "evermore")]
    [InlineData("Dangerous Woman (Bonus Track Version)", "Dangerous Woman")]
    [InlineData("Rumours (2007 Remastered Version)", "Rumours")]
    [InlineData("Nevermind (20th Anniversary Edition)", "Nevermind")]
    [InlineData("Ten (Expanded Edition)", "Ten")]
    public void AReissueQualifierIsStripped(string title, string expected) =>
        Assert.Equal(expected, Discography.StripEdition(title));

    [Fact]
    public void ATitleThatIsNothingButAQualifierKeepsItself()
    {
        // `LastIndexOf('(') > 0` rather than `>= 0`. Stripped, this would fold
        // to nothing and join every other untitled row on the shelf.
        Assert.Equal("(Remastered)", Discography.StripEdition("(Remastered)"));
    }

    [Fact]
    public void ABracketOfNothingButFillerIsNotAQualifier()
    {
        // "the", "vol" and a year say nothing on their own — one of the words
        // has to actually mean reissue, or "(Vol. 2)" would fold two volumes.
        Assert.Equal("Anthology (Vol. 2)", Discography.StripEdition("Anthology (Vol. 2)"));
        Assert.Equal("Live at Leeds (1970)", Discography.StripEdition("Live at Leeds (1970)"));
    }

    [Fact]
    public void ARecordWantedTwiceOverIsNotFoldedAtAll()
    {
        // Two deliberate clicks on two editions of one record. Folded, one of
        // them is reachable from the artist page and the other stands
        // invisible — a person's standing intent hidden by a display rule.
        var shelf = Collapse(
            Row("The Four Seasons", "Album", 1989, Wanted: true),
            Row("The Four Seasons", "Album", 2005, Wanted: true),
            Row("The Four Seasons", "Album", 2021));

        Assert.Equal(3, shelf.Count);
        Assert.All(shelf, edition => Assert.Equal(1, edition.Count));
    }

    [Fact]
    public void TheRowWithNoQualifierIsTheOnePrinted()
    {
        // Measured: the shelf offered "Thriller" and printed it as
        // "Thriller (Demo Version)", because the representative was chosen by
        // year alone and the demo was older.
        var only = Assert.Single(Collapse(
            Row("Thriller (Deluxe Edition)", "Album", 2001),
            Row("Thriller", "Album", 1982)));

        Assert.Equal("Thriller", only.Row.Title);
    }

    [Fact]
    public void TheWantedRowIsTheOneKept()
    {
        // `Monitored` is one of the facts nothing can recompute, so the folded
        // row has to be the one carrying it — otherwise the button on screen
        // acts on a row that does not hold the answer.
        var only = Assert.Single(Collapse(
            Row("The Four Seasons", "Album", 1989),
            Row("The Four Seasons", "Album", 2005, Wanted: true)));

        Assert.True(only.Row.Wanted);
        Assert.Equal(2005, only.Row.Year);
    }

    [Fact]
    public void ATitleThatFoldsToNothingKeepsItsOwnIdentity()
    {
        // "!!!" is a real band and a classical shelf has a row of punctuation
        // somewhere. Folded to an empty string these would all become one row.
        Assert.Equal(2, Collapse(
            Row("!!!", "Album", 2007),
            Row("???", "Album", 2010)).Count);
    }

    [Fact]
    public void AShelfWithNothingToFoldComesBackUnchanged()
    {
        // The case every popular artist here is: measured, eighteen of the
        // twenty-eight followed artists lose no rows at all.
        Assert.Equal(3, Collapse(
            Row("Damn Right, I've Got the Blues", "Album", 1991),
            Row("Feels Like Rain", "Album", 1993),
            Row("Slippin' In", "Album", 1994)).Count);
    }

    [Fact]
    public void AFoldedRowCarriesTheRepresentativesYearSoTheCallerMustReSort()
    {
        // <b>The reason both call sites sort after folding.</b> Fold in place
        // and the shelf reads 2014, 2010, 2022, 2008: the group sits where its
        // first row sat and prints a different row's date. Measured on one
        // artist's shelf at 79 out-of-order pairs in 1,236.
        var shelf = Collapse(
            Row("Newest", "Album", 2024),
            Row("Reissued", "Album", 2019),
            Row("Reissued", "Album", 1981),
            Row("Oldest", "Album", 1999));

        Assert.Equal([2024, 1981, 1999], shelf.Select(e => e.Row.Year));
    }

    /// <remarks>
    /// Spacing goes with the punctuation and the case, because the normaliser
    /// is <see cref="Fonoteca.Domain.Catalogue.ArtistNameMatch.Normalise"/> —
    /// deliberately the same one <c>ReleaseTitleMatch</c> reuses, so that
    /// "Sgt. Pepper's" and "Sgt Peppers" fold together here too.
    /// </remarks>
    [Theory]
    [InlineData("Blue Train", "bluetrain")]
    [InlineData("Sgt. Pepper's", "sgtpeppers")]
    [InlineData("Brothers in Arms (Remastered)", "brothersinarms")]
    [InlineData("Brothers in Arms (Live)", "brothersinarmslive")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void TitlesFoldToWhatTwoPrintingsShare(string? title, string expected) =>
        Assert.Equal(expected, Discography.EditionTitle(title));

    private static IReadOnlyList<Edition<Shelf>> Collapse(params Shelf[] rows) =>
        Discography.Collapse(
            rows, row => row.Title, row => row.Kind, row => row.Year, row => row.Wanted);

    private static Shelf Row(string title, string? kind, int? year, bool Wanted = false) =>
        new(title, kind, year, Wanted);

    private sealed record Shelf(string Title, string? Kind, int? Year, bool Wanted);
}
