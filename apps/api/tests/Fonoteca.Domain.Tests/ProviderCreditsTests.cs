using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Qobuz's credit strings, read into the catalogue's roles. The strings are
/// copied from two live <c>album/get</c> responses.
/// </summary>
public sealed class ProviderCreditsTests
{
    [Fact]
    public void AClassicalTrackIsItsComposerConductorAndOrchestraAndNotVariousArtists()
    {
        var credits = ProviderCredits.Parse(
            "Various Artists, MainArtist - Johannes Brahms, Composer - William Steinberg, Conductor - Pittsburgh Symphony Orchestra, Orchestra",
            billed: "Various Artists",
            composer: "Johannes Brahms");

        Assert.Equal(
            [
                new ProviderCredit("Johannes Brahms", CreditRole.Writer, 0, null),
                new ProviderCredit("William Steinberg", CreditRole.Conductor, 0, null),
                new ProviderCredit("Pittsburgh Symphony Orchestra", CreditRole.Ensemble, 0, null),
            ],
            credits);
    }

    [Fact]
    public void TwoSoloistsAreBilledInTheirOrderAndJoinedAsMusicBrainzPrintsThem()
    {
        var credits = ProviderCredits.Parse(
            "Philadelphia Orchestra, Orchestra - Emanuel Feuermann, MainArtist - Johannes Brahms, Composer - Eugene Ormandy, Conductor - Jasha Heifetz, MainArtist");

        Assert.Equal(
            [
                new ProviderCredit("Emanuel Feuermann", CreditRole.Billed, 0, " & "),
                new ProviderCredit("Jasha Heifetz", CreditRole.Billed, 1, null),
            ],
            credits.Where(credit => credit.Role == CreditRole.Billed));
        Assert.Contains(new ProviderCredit("Philadelphia Orchestra", CreditRole.Ensemble, 0, null), credits);
    }

    [Fact]
    public void ABandsPlayersProducersAndEngineersAreNotKeptButItsWriterIs()
    {
        var credits = ProviderCredits.Parse(
            "Fleetwood Mac, MainArtist - Chris Morris, AssistantEngineer - Christine Mcvie, Keyboards, Synthesizer, Vocals, Producer - "
            + "JOHN MCVIE, Bass Guitar, Producer - Ken Caillat, Audio Recording Engineer, Producer - Ken Perry, MasteringEngineer - "
            + "Lindsey Buckingham, Guitar, Vocals, Producer - MICK FLEETWOOD, Drums, Percussion, Producer - Richard Dashut, "
            + "Audio Recording Engineer, Producer - Stevie Nicks, Producer, Vocals, Writer",
            billed: "Fleetwood Mac");

        Assert.Equal(
            [
                new ProviderCredit("Fleetwood Mac", CreditRole.Billed, 0, null),
                new ProviderCredit("Stevie Nicks", CreditRole.Writer, 0, null),
            ],
            credits);
    }

    [Fact]
    public void WithNoCreditStringTheTracksOwnCreditIsTheBilling()
    {
        Assert.Equal([new ProviderCredit("Dire Straits", CreditRole.Billed, 0, null)], ProviderCredits.Parse(null, "Dire Straits"));
        Assert.Empty(ProviderCredits.Parse(null, "Various Artists", "Various Composers"));
    }

    [Fact]
    public void ABandWithACommaInItsNameIsReadWholeWhereTheShopNamesIt()
    {
        var credits = ProviderCredits.Parse("Crosby, Stills & Nash, MainArtist - David Crosby, Writer", "Crosby, Stills & Nash");

        Assert.Equal(new ProviderCredit("Crosby, Stills & Nash", CreditRole.Billed, 0, null), credits[0]);
        Assert.Equal(new ProviderCredit("David Crosby", CreditRole.Writer, 0, null), credits[1]);
    }

    [Fact]
    public void ThreeBilledNamesReadAsACommaThenAnAmpersand()
    {
        var credits = ProviderCredits.Parse("A, MainArtist - B, MainArtist - C, MainArtist");

        Assert.Equal([", ", " & ", null], credits.Select(credit => credit.JoinPhrase));
    }

    [Theory]
    [InlineData("Leonard Bernstein, MainArtist, Composer, Conductor", CreditRole.Billed)]
    [InlineData("Adele, MainArtist, ComposerLyricist", CreditRole.Billed)]
    [InlineData("Pierre Boulez, Composer, Conductor", CreditRole.Conductor)]
    public void AnArtistIsCreditedOnceInTheStrongestRoleItHolds(string performers, CreditRole role)
    {
        var credit = Assert.Single(ProviderCredits.Parse(performers));

        Assert.Equal(role, credit.Role);
    }

    [Fact]
    public void TheShopsComposerIsTheWriterWhereTheStringNamesNone()
    {
        Assert.Equal(
            new ProviderCredit("Johannes Brahms", CreditRole.Writer, 0, null),
            Assert.Single(ProviderCredits.Parse("Various Artists, MainArtist", composer: "Johannes Brahms")));
    }

    [Theory]
    [InlineData("Lorenzo Da Ponte, Lyricist", CreditRole.Writer)]
    [InlineData("Monteverdi Choir, Choir", CreditRole.Ensemble)]
    [InlineData("Chamber Orchestra of Europe, ChamberOrchestra", CreditRole.Ensemble)]
    [InlineData("Tenebrae, Mixed Choir", CreditRole.Ensemble)]
    [InlineData("The Sixteen, VocalEnsemble", CreditRole.Ensemble)]
    [InlineData("Simon Halsey, ChorusMaster", CreditRole.Conductor)]
    public void EachRoleWordIsReadAsTheCataloguesRole(string performers, CreditRole role) =>
        Assert.Equal(role, Assert.Single(ProviderCredits.Parse(performers)).Role);

    [Fact]
    public void OnlyTheShopsPlaceholderIsNobody() =>
        Assert.Equal("Various Production", Assert.Single(ProviderCredits.Parse(null, "Various Production")).Name);

    [Fact]
    public void AFeaturedArtistIsBilledAfterTheMainOnesBehindFeat()
    {
        Assert.Equal(
            [
                new ProviderCredit("Calvin Harris", CreditRole.Billed, 0, " feat. "),
                new ProviderCredit("Rihanna", CreditRole.Billed, 1, null),
            ],
            ProviderCredits.Parse("Calvin Harris, MainArtist - Rihanna, FeaturedArtist"));

        Assert.Equal(
            [" & ", " feat. ", " & ", null],
            ProviderCredits.Parse("A, MainArtist - B, MainArtist - C, FeaturedArtist - D, Featured Artist")
                .Select(credit => credit.JoinPhrase));
    }

    [Theory]
    [InlineData("Sammy Davis, Jr., MainArtist", "Sammy Davis, Jr.")]
    [InlineData("Hank Williams, III, MainArtist", "Hank Williams, III")]
    [InlineData("Earth, Wind & Fire, MainArtist", "Earth, Wind & Fire")]
    [InlineData("Tyler, The Creator, MainArtist", "Tyler, The Creator")]
    [InlineData("Crosby, Stills, Nash & Young, MainArtist", "Crosby, Stills, Nash & Young")]
    [InlineData("Peter, Paul and Mary, MainArtist", "Peter, Paul and Mary")]
    public void ANameThatGoesOnPastItsCommaIsReadWhole(string performers, string name) =>
        Assert.Equal(new ProviderCredit(name, CreditRole.Billed, 0, null), Assert.Single(ProviderCredits.Parse(performers)));

    [Fact]
    public void AFeaturedNameWithACommaIsBilledWhole()
    {
        Assert.Equal(
            ["Kali Uchis", "Tyler, The Creator", "Bootsy Collins"],
            ProviderCredits.Parse("Kali Uchis, MainArtist - Tyler, The Creator, FeaturedArtist - Bootsy Collins, FeaturedArtist")
                .Select(credit => credit.Name));
    }

    [Fact]
    public void APlayersRolesAreNeverReadAsTheirName()
    {
        Assert.Equal(
            "Christine McVie",
            Assert.Single(ProviderCredits.Parse("Christine McVie, Keyboards, Synthesizer, Vocals, Producer, MainArtist")).Name);
    }

    [Fact]
    public void AMainArtistAlsoMarkedFeaturedIsBilledOnce()
    {
        Assert.Equal(
            new ProviderCredit("Adele", CreditRole.Billed, 0, null),
            Assert.Single(ProviderCredits.Parse("Adele, MainArtist, FeaturedArtist")));
    }

    [Fact]
    public void AFeaturedWriterIsBilledAndNotAlsoAWriter()
    {
        var credits = ProviderCredits.Parse("Calvin Harris, MainArtist - Rihanna, FeaturedArtist, Composer");

        Assert.Equal(["Calvin Harris", "Rihanna"], credits.Select(credit => credit.Name));
        Assert.All(credits, credit => Assert.Equal(CreditRole.Billed, credit.Role));
    }

    [Theory]
    [InlineData("Choir of King's College, Cambridge, Choir", "Choir of King's College, Cambridge")]
    [InlineData("Orchestra of the Royal Opera House, Covent Garden, Orchestra", "Orchestra of the Royal Opera House, Covent Garden")]
    public void AnEnsemblesNameRunsOnToItsPlace(string performers, string name) =>
        Assert.Equal(new ProviderCredit(name, CreditRole.Ensemble, 0, null), Assert.Single(ProviderCredits.Parse(performers)));

    [Fact]
    public void ANumbersThousandsAreNotANewPart() =>
        Assert.Equal(
            ["Natalie Merchant", "10,000 Maniacs"],
            ProviderCredits.Parse("Natalie Merchant, MainArtist - 10,000 Maniacs, FeaturedArtist").Select(credit => credit.Name));

    [Fact]
    public void ANameTheShopListsWholeIsReadWholeWhateverItsCommas() =>
        Assert.Equal(
            "Hey, Rosetta!",
            Assert.Single(ProviderCredits.Parse("Hey, Rosetta!, MainArtist", names: ["Hey, Rosetta!"])).Name);

    [Theory]
    [InlineData("Bruce Fowler, Orchestrator")]
    [InlineData("Anne Smith, Choir Director")]
    [InlineData("Ian Bostridge, ChoirMember")]
    public void APersonWhoseRoleMentionsAnEnsembleIsNotOne(string performers) =>
        Assert.Empty(ProviderCredits.Parse(performers));

    [Fact]
    public void AComposerWhoAlsoOrchestratesIsTheWriter() =>
        Assert.Equal(CreditRole.Writer, Assert.Single(ProviderCredits.Parse("John Williams, Composer, Orchestrator")).Role);

    /// <summary>Each way a part reads as a role stops a name from running on into what follows it.</summary>
    [Theory]
    [InlineData("Christine McVie, Keyboards, Vocals & Harmony, MainArtist", "Christine McVie")]
    [InlineData("Brian Wilson, VocalArrangement, The Wrecking Crew, MainArtist", "Brian Wilson")]
    [InlineData("Rihanna, Featuring, The Remix, MainArtist", "Rihanna")]
    [InlineData("Max Martin, Composer, The Writers & Co, MainArtist", "Max Martin")]
    public void ANameStopsAtTheFirstRole(string performers, string name) =>
        Assert.Contains(name, ProviderCredits.Parse(performers).Select(credit => credit.Name));

    [Theory]
    [InlineData("Kronos Quartet, Strings, MainArtist", "Kronos Quartet")]
    [InlineData("Electric Light Orchestra, Band, MainArtist", "Electric Light Orchestra")]
    [InlineData("Monteverdi Choir, Soprano, MainArtist", "Monteverdi Choir")]
    public void AnEnsembleWithNoPlaceStopsAtItsComma(string performers, string name) =>
        Assert.Equal(name, Assert.Single(ProviderCredits.Parse(performers)).Name);

    [Theory]
    [InlineData("The Sixteen, VocalEnsemble", CreditRole.Ensemble)]
    [InlineData("Tenebrae, Mixed Chorus", CreditRole.Ensemble)]
    public void AnEnsembleRoleIsReadByItsEnding(string performers, CreditRole role) =>
        Assert.Equal(role, Assert.Single(ProviderCredits.Parse(performers)).Role);

    /// <summary>"Orchestrator" and "Member" read as roles, so a name stops before them.</summary>
    [Theory]
    [InlineData("Bruce Fowler, Orchestrator, The Mothers, MainArtist", "Bruce Fowler")]
    [InlineData("Ian Bostridge, Choir Member, The Sixteen & Friends, MainArtist", "Ian Bostridge")]
    public void APersonsRolesNamedForAnEnsembleStopTheirName(string performers, string name) =>
        Assert.Equal(name, Assert.Single(ProviderCredits.Parse(performers)).Name);

    [Fact]
    public void TheLongestNameTheShopListsIsTheOneRead() =>
        Assert.Equal(
            "Crosby, Stills, Nash & Young",
            Assert.Single(ProviderCredits.Parse(
                "Crosby, Stills, Nash & Young, MainArtist",
                names: ["Crosby, Stills & Nash", "Crosby, Stills", "Crosby, Stills, Nash & Young"])).Name);
}
