using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The rule that decides which artists a track can be found under.
/// </summary>
/// <remarks>
/// The two cases that drove it are named here as tests: a classical recording,
/// where the credit line names only the composer and everyone who actually
/// played it lives in relationships, and a two-artist collaboration, where the
/// track belongs to both names on the sleeve.
/// </remarks>
public sealed class PrimaryCreditsTests
{
    private static readonly Mbid Beethoven = Mb("1f9df192-a621-4f54-8850-2c5373b7eac9");
    private static readonly Mbid Karajan = Mb("5b11f4ce-a62d-471e-81fc-a69a8278c7da");
    private static readonly Mbid Berliner = Mb("d0e5b1e2-9d3b-4d6b-9a0b-7c8f9a0b1c2d");
    private static readonly Mbid Hart = Mb("3fe817fc-966e-4ece-b00a-76be43e7e73c");
    private static readonly Mbid Bonamassa = Mb("984f8239-8fe1-4683-9c54-10ffb14439e9");
    private static readonly Mbid Engineer = Mb("f4a1b2c3-d4e5-4f60-8172-93a4b5c6d7e8");

    /// <summary>
    /// The case the whole rule exists for. MusicBrainz bills this to Beethoven
    /// and nobody else; a library that reads the credit line alone has no page
    /// for Karajan and none for the orchestra that played it.
    /// </summary>
    [Fact]
    public void AClassicalRecordingIsFiledUnderItsPerformersAsWellAsItsComposer()
    {
        var credits = PrimaryCredits.From(
            Recording(
                credits: [Credit(Beethoven, "Ludwig van Beethoven", "Person")],
                relations:
                [
                    Relation("conductor", Karajan, "Herbert von Karajan", "Person"),
                    Relation("performing orchestra", Berliner, "Berliner Philharmoniker", "Group"),
                ]),
            Work([Relation("composer", Beethoven, "Ludwig van Beethoven", "Person")]));

        Assert.Equal(
            [Beethoven, Karajan, Berliner],
            credits.Select(credit => credit.ArtistId));

        // Beethoven is both billed and the composer. Billed is the stronger
        // claim, so that is the role that survives — and he appears once.
        Assert.Equal(CreditRole.Billed, credits[0].Role);
        Assert.Equal(CreditRole.Conductor, credits[1].Role);
        Assert.Equal(CreditRole.Ensemble, credits[2].Role);
    }

    /// <summary>
    /// A composer with no performance relations at all still gets the track,
    /// which is what makes "everything Mozart wrote that I own" answerable.
    /// </summary>
    [Fact]
    public void AWorksWritersAreCreditedEvenWhenNobodyBilledThem()
    {
        var credits = PrimaryCredits.From(
            Recording(credits: [Credit(Karajan, "Herbert von Karajan", "Person")]),
            Work(
            [
                Relation("composer", Beethoven, "Ludwig van Beethoven", "Person"),
                Relation("lyricist", Hart, "Beth Hart", "Person"),
            ]));

        Assert.Equal([Karajan, Beethoven, Hart], credits.Select(credit => credit.ArtistId));
        Assert.Equal(CreditRole.Writer, credits[1].Role);
        Assert.Equal(CreditRole.Writer, credits[2].Role);
    }

    /// <summary>
    /// Most popular music has no work in MusicBrainz, and a null one is the
    /// ordinary case rather than a missing lookup to be defended against.
    /// </summary>
    [Fact]
    public void ACollaborationIsFiledUnderBothNamesOnTheSleeve()
    {
        var credits = PrimaryCredits.From(
            Recording(credits:
            [
                Credit(Hart, "Beth Hart", "Person", " & "),
                Credit(Bonamassa, "Joe Bonamassa", "Person"),
            ]));

        Assert.Equal([Hart, Bonamassa], credits.Select(credit => credit.ArtistId));

        // Billing order and the printed join phrase both survive — collapsing
        // them to a string would lose the links, collapsing them to a set would
        // lose the order.
        Assert.Equal([0, 1], credits.Select(credit => credit.Position));
        Assert.Equal(" & ", credits[0].JoinPhrase);
        Assert.Null(credits[1].JoinPhrase);
    }

    /// <summary>
    /// The people who made the record rather than the music. Browsing by them is
    /// a real question and a different one; folding it in here would put the
    /// engineer in the artist list beside the band.
    /// </summary>
    [Theory]
    [InlineData("mix")]
    [InlineData("engineer")]
    [InlineData("producer")]
    [InlineData("mastering")]
    [InlineData("recording")]
    public void ProductionRolesAreNotCredits(string role)
    {
        var credits = PrimaryCredits.From(
            Recording(
                credits: [Credit(Hart, "Beth Hart", "Person")],
                relations: [Relation(role, Engineer, "Kevin Shirley", "Person")]));

        Assert.Equal([Hart], credits.Select(credit => credit.ArtistId));
    }

    /// <summary>
    /// A symphony names its soloists and a jazz date names its sidemen. Included,
    /// they would be most of the artist list on a real library — one or two
    /// tracks each — and the ensemble they played in would be lost among them.
    /// </summary>
    [Fact]
    public void IndividualPlayersAreNotCreditsButTheEnsembleIs()
    {
        var players = Enumerable
            .Range(0, 40)
            .Select(index => Relation(
                "instrument",
                Mb($"00000000-0000-4000-8000-{index:D12}"),
                $"Player {index}",
                "Person",
                attribute: "violin"))
            .ToList();

        players.Add(Relation("performer", Berliner, "Berliner Philharmoniker", "Orchestra"));
        players.Add(Relation("conductor", Karajan, "Herbert von Karajan", "Person"));

        var credits = PrimaryCredits.From(
            Recording(credits: [Credit(Beethoven, "Ludwig van Beethoven", "Person")], relations: players));

        Assert.Equal([Beethoven, Karajan, Berliner], credits.Select(credit => credit.ArtistId));
    }

    /// <summary>
    /// MusicBrainz types most ensembles <c>Group</c>, so the artist type alone
    /// finds some orchestras and misses others. Either signal is enough.
    /// </summary>
    [Theory]
    [InlineData("performer", "Orchestra", null)]
    [InlineData("performer", "Choir", null)]
    [InlineData("performer", "Group", "orchestra")]
    [InlineData("performing orchestra", "Group", null)]
    [InlineData("vocal", "Choir", null)]
    public void AnEnsembleIsRecognisedByItsTypeOrByTheRelation(
        string relationType,
        string? artistType,
        string? attribute)
    {
        var credits = PrimaryCredits.From(
            Recording(relations:
                [Relation(relationType, Berliner, "Berliner Philharmoniker", artistType, attribute)]));

        var credit = Assert.Single(credits);

        Assert.Equal(Berliner, credit.ArtistId);
        Assert.Equal(CreditRole.Ensemble, credit.Role);
    }

    /// <summary>
    /// Ordering must be a total function of the input. Dictionary iteration
    /// order leaking in here would reshuffle every artist page on a rerun over
    /// an unchanged library.
    /// </summary>
    [Fact]
    public void TheSameInputAlwaysProducesTheSameOrder()
    {
        var recording = Recording(
            credits: [Credit(Hart, "Beth Hart", "Person", " & "), Credit(Bonamassa, "Joe Bonamassa", "Person")],
            relations:
            [
                Relation("performing orchestra", Berliner, "Berliner Philharmoniker", "Group"),
                Relation("conductor", Karajan, "Herbert von Karajan", "Person"),
            ]);

        var work = Work([Relation("composer", Beethoven, "Ludwig van Beethoven", "Person")]);

        var first = PrimaryCredits.From(recording, work).Select(credit => credit.ArtistId).ToList();
        var second = PrimaryCredits.From(recording, work).Select(credit => credit.ArtistId).ToList();

        Assert.Equal(first, second);
        Assert.Equal([Hart, Bonamassa, Karajan, Berliner, Beethoven], first);
    }

    /// <summary>
    /// A credit MusicBrainz has not linked to an artist names somebody the
    /// catalogue cannot store or navigate to. Dropped rather than invented.
    /// </summary>
    [Fact]
    public void ACreditWithNoArtistIsNotACredit()
    {
        var credits = PrimaryCredits.From(
            Recording(
                credits: [new MusicBrainzCredit(null, "Various Artists", null, null, null, null)],
                relations: [new MusicBrainzRelation("conductor", null, null, "Anonymous", null, null, null)]));

        Assert.Empty(credits);
    }

    private static MusicBrainzRecording Recording(
        IReadOnlyList<MusicBrainzCredit>? credits = null,
        IReadOnlyList<MusicBrainzRelation>? relations = null) =>
        new(
            Id: Mb("85db2cdf-80c9-4aa2-9789-19328dde47ed"),
            Title: "Symphony No. 5 in C minor, Op. 67: I. Allegro con brio",
            Disambiguation: null,
            Length: TimeSpan.FromMinutes(7),
            Credits: credits ?? [],
            Isrcs: [],
            Appearances: [],
            Relations: relations ?? [],
            WorkId: null,
            WorkTitle: null);

    private static MusicBrainzWork Work(IReadOnlyList<MusicBrainzRelation> relations) =>
        new(Mb("70729fa3-654b-4a0b-85ec-5c0ba3b3fb80"), "Symphony no. 5", "Symphony", relations);

    private static MusicBrainzCredit Credit(
        Mbid artist,
        string name,
        string? type,
        string? joinPhrase = null) =>
        new(artist, name, $"{name} (sort)", joinPhrase, null, type);

    private static MusicBrainzRelation Relation(
        string type,
        Mbid artist,
        string name,
        string? artistType,
        string? attribute = null) =>
        new(type, attribute, artist, name, $"{name} (sort)", artistType, null);

    private static Mbid Mb(string value) => new(Guid.Parse(value));
}
