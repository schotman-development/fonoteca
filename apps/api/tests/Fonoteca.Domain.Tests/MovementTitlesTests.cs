using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class MovementTitlesTests
{
    [Fact]
    public void ThePiecesOwnNameInFrontComesOff()
    {
        Assert.Equal(
            "I. Allegro con brio",
            MovementTitles.Without("Symphony No. 5 in C minor, Op. 67: I. Allegro con brio", "Symphony No. 5 in C minor, Op. 67"));
    }

    /// <summary>Each spelling measured here that left the whole title on screen.</summary>
    [Theory]
    [InlineData("Violin Concerto in A minor, op. 53: I. Allegro ma non troppo", "Violin Concerto in A minor, op. 53, B. 96/B. 108", "I. Allegro ma non troppo")]
    [InlineData("Das Rheingold: Scene I", "Das Rheingold, WWV 86A", "Scene I")]
    [InlineData("Symphony no. 3 in D minor, Part I: I. Kräftig. Entschieden", "Symphony no. 3 in D minor", "I. Kräftig. Entschieden")]
    [InlineData("Sonata for Violin and Piano no. 5 in F major, op. 24 \"Spring\": I. Allegro", "Sonata for Violin and Piano no. 5 in F major, op. 24 “Spring”", "I. Allegro")]
    [InlineData("Symphony no. 5 in B-flat major, D. 485: I. Allegro", "Symphony no. 5 in B‐flat major, D. 485", "I. Allegro")]
    [InlineData("Children's Corner, L. 113: I. Doctor Gradus ad Parnassum", "Children’s Corner, L. 113, CD 119", "I. Doctor Gradus ad Parnassum")]
    [InlineData("E.T. The Extra-Terrestrial: Adventure on Earth", "E.T. the Extra-Terrestrial", "Adventure on Earth")]
    [InlineData("Suite bergamasque, L. 75, CD 82 : III. Clair de lune", "Suite bergamasque, L. 75, CD 82", "III. Clair de lune")]
    public void ThePieceInFrontIsKnownByAnySpellingOfIt(string title, string piece, string movement)
    {
        Assert.Equal(movement, MovementTitles.Without(title, piece));
    }

    /// <summary>A colon inside the piece's own name is part of the name, not the place to cut.</summary>
    [Fact]
    public void APieceWithAColonInItsNameIsCutAfterTheWholeName()
    {
        Assert.Equal(
            "Variation 11: Adagio",
            MovementTitles.Without(
                "12 Variations on French Traditional: “Ah, vous dirai-je, Maman” for Piano in C major, K. 300e/265: Variation 11: Adagio",
                "12 Variations on French Traditional: \"Ah, vous dirai-je, Maman\" for Piano in C major, K. 300e/265"));
    }

    [Theory]
    [InlineData("Prélude no. 15 in D‐flat major, op. 28 “Raindrop”: Sostenuto", "24 Préludes pour le piano, op. 28")]
    [InlineData("Symphony no. 10 in E minor, op. 93: I. Moderato", "Symphony no. 1 in F minor, op. 10")]
    [InlineData("Symphony no. 1: I. Allegretto", "Symphony no. 10 in E minor, op. 93")]
    [InlineData("The Sleeping Beauty, op. 66: Acte I, no. 7. Scène", "The Sleeping Beauty, op. 66: Acte I")]
    [InlineData("Symphony no. 3: ", "Symphony no. 3")]
    public void SomethingElseInFrontStays(string title, string piece)
    {
        Assert.Null(MovementTitles.Without(title, piece));
    }
}
