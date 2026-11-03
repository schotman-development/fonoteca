using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class MovementNumbersTests
{
    [Theory]
    [InlineData("Symphony no. 1 in C minor, op. 11: IV. Allegro con fuoco", 4)]
    [InlineData("Paulus, op. 36, Pt. 2: No. 35, Chorus. Sei uns gnädig", 35)]
    [InlineData("Paulus, op. 36: XXXIV. Rezitativ \"Und nannten Barnabas\"", 34)]
    [InlineData("Paulus, op. 36, Pt. 1: No. 4a, Recit. Die Menge der Gläubigen war ein Herz", 4)]
    [InlineData("Symphony no. 2 in B-flat major, D 125: III. Menuetto: Allegro vivace", 3)]
    [InlineData("Symphony no. 2 in B‐flat major, op. 52 “Hymn of Praise”: IIIb. Aria. Er zählet unsre Tränen", 3)]
    [InlineData("Children’s Corner, L. 113: 1. Doctor Gradus ad Parnassum", 1)]
    [InlineData("Suite bergamasque, L. 75, CD 82 : III. Clair de lune", 3)]
    [InlineData("Swan Lake: Act Two. No. 13. Dances of the Swans: VII. Coda (Allegro vivo)", 7)]
    [InlineData("Swan Lake, op. 20: No. 13. Danses des cygnes: VII. Coda", 7)]
    [InlineData("Paulus, op. 36: 4a. Rezitativ", 4)]
    [InlineData("I. Allegro di molto", 1)]
    public void ATitleStatesItsMovementAsMusicBrainzWritesIt(string title, int number)
    {
        Assert.Equal(number, MovementNumbers.Stated(title));
    }

    [Theory]
    [InlineData("Elijah, op. 70: Wirf dein Anliegen auf den Herrn")]
    [InlineData("Prélude no. 15 in D‐flat major, op. 28 “Raindrop”: Sostenuto")]
    [InlineData("Violin Concerto in A minor, op. 53, B. 96/B. 108")]
    [InlineData("Das Rheingold: Scene I")]
    public void ATitleNamingNoMovementStatesNone(string title)
    {
        Assert.Null(MovementNumbers.Stated(title));
    }

    /// <summary>The five links measured here, each to another movement than its recording's title names.</summary>
    [Theory]
    [InlineData("Symphony no. 1 in C minor, op. 11: IV. Allegro con fuoco", "Symphony no. 1 in C minor, op. 11: I. Allegro di molto")]
    [InlineData("Paulus, op. 36, Pt. 2: No. 35, Chorus. Sei uns gnädig", "Paulus, op. 36: XXXIV. Rezitativ \"Und nannten Barnabas\"")]
    [InlineData("Symphony No. 9 in D minor, Op. 125 \"Choral\": III. Adagio molto e cantabile", "Symphony no. 7 in A major, op. 92: IV. Allegro con brio")]
    [InlineData("Symphony no. 2 in B-flat major, D 125: III. Menuetto: Allegro vivace", "Symphony no. 2 in B-flat major, D. 125: I. Largo - Allegro vivace")]
    [InlineData("Symphony no. 1 in F minor, op. 10: II. Allegro - Meno mosso - Allegro - Meno mosso", "Symphony no. 1 in B minor, op. 12: IV. Finale: Allegro molto - Più animato")]
    public void ARecordingLinkedToAnotherMovementDisagrees(string recording, string work)
    {
        Assert.True(MovementNumbers.Disagree(recording, work));
    }

    [Theory]
    [InlineData("Symphony no. 2 in B-flat major, D 125: III. Menuetto: Allegro vivace", "Symphony no. 2 in B-flat major, D. 125: III. Menuetto & Trio")]
    [InlineData("Paulus, op. 36, Pt. 1: No. 4b, Duet. Wir haben ihn gehört", "Paulus, op. 36: IV. Rezitativ und Duett \"Die Menge der Gläubigen\"")]
    [InlineData("Symphony no. 2 in B‐flat major, op. 52 “Hymn of Praise”: IIb. Solo and Chorus", "Symphony no. 2 in B-flat major, op. 52 “Lobgesang”: IIc. “Lobe den Herrn, meine Seele”")]
    [InlineData("Carmen Fantasy, op. 25: I. Moderato", "Carmen Fantasy, op. 25: I. Moderato")]
    [InlineData("Elias, op. 70, Pt. 1: No. 15, Quartet. Wirf dein Anliegen auf den Herrn", "Elijah, op. 70: Wirf dein Anliegen auf den Herrn")]
    public void TheSameMovementOrNoneStatedAgrees(string recording, string work)
    {
        Assert.False(MovementNumbers.Disagree(recording, work));
    }
}
