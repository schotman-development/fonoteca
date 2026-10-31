using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Apple Music Classical's answers read into the catalogue. The roles are the
/// ones its album pages printed on 2026-10-30.
/// </summary>
public sealed class ClassicalCatalogueTests
{
    private static ClassicalTrack Track(int disc, int number, double seconds) =>
        new(disc, number, TimeSpan.FromSeconds(seconds), null, []);

    [Fact]
    public void AFileIsTheTrackAtItsDiscAndNumberWhenTheLengthsAgree()
    {
        ClassicalTrack[] tracks = [Track(1, 1, 442), Track(1, 2, 590), Track(2, 1, 300)];

        var seats = ClassicalCatalogue.Seat(
            [
                new HeldTrack<string>("first", 1, 1, TimeSpan.FromSeconds(443.5)),
                new HeldTrack<string>("second disc", 2, 1, TimeSpan.FromSeconds(297.2)),
            ],
            tracks);

        Assert.Same(tracks[0], seats["first"]);
        Assert.Same(tracks[2], seats["second disc"]);
    }

    [Fact]
    public void ALengthMoreThanThreeSecondsOffOrUnknownIsNobodys()
    {
        ClassicalTrack[] tracks = [Track(1, 1, 442), Track(1, 2, 590)];

        var seats = ClassicalCatalogue.Seat(
            [
                new HeldTrack<string>("too long", 1, 1, TimeSpan.FromSeconds(445.5)),
                new HeldTrack<string>("unmeasured", 1, 2, null),
                new HeldTrack<string>("no such track", 1, 3, TimeSpan.FromSeconds(100)),
            ],
            tracks);

        Assert.Empty(seats);
    }

    [Fact]
    public void APlaceTheCatalogueListsTwiceIsNobodys()
    {
        var seats = ClassicalCatalogue.Seat(
            [new HeldTrack<int>(1, 1, 4, TimeSpan.FromSeconds(200))],
            [Track(1, 4, 200), Track(1, 4, 201)]);

        Assert.Empty(seats);
    }

    [Theory]
    [InlineData("Conductor", "conductor", null)]
    [InlineData("Symphony Orchestra", "performing orchestra", null)]
    [InlineData("Choir", "vocal", "choir vocals")]
    [InlineData("Chorus Director", "chorus master", null)]
    [InlineData("Soprano", "vocal", "soprano vocals")]
    [InlineData("Mezzo-Soprano", "vocal", "mezzo-soprano vocals")]
    [InlineData("Vocals", "vocal", null)]
    [InlineData("Background Vocals", "vocal", "background vocals")]
    [InlineData("Narrator", "vocal", "spoken vocals")]
    [InlineData("Alto Saxophone", "instrument", "alto saxophone")]
    [InlineData("Didgeridoo", "instrument", "didgeridoo")]
    [InlineData("Artist", "performer", null)]
    [InlineData("Ensemble", "performer", null)]
    public void EachRoleIsTheLinkMusicBrainzWouldState(string role, string type, string? attributes)
    {
        Assert.Equal([(type, attributes)], ClassicalCatalogue.Links([role]));
    }

    [Fact]
    public void AnOrchestraCreditedThreeWaysIsOneOrchestraAndAConductorWhoPlaysIsBoth()
    {
        Assert.Equal(
            [("performing orchestra", null)],
            ClassicalCatalogue.Links(["Orchestra", "Ensemble", "Symphony Orchestra"]));
        Assert.Equal(
            [("conductor", null), ("instrument", "piano")],
            ClassicalCatalogue.Links(["Conductor", "Piano"]));
    }

    [Fact]
    public void WritersAreNoPerformanceAndNoRoleIsAPerformer()
    {
        Assert.Empty(ClassicalCatalogue.Links(["Composer"]));
        Assert.Empty(ClassicalCatalogue.Links(["Lyricist", "Arranger", "Orchestrator", "Producer"]));
        Assert.Equal([("performer", null)], ClassicalCatalogue.Links([]));
    }

    [Fact]
    public void ThePerformerTypesAreTheOnesThatNameSomebodyPlaying()
    {
        foreach (var type in new[] { "conductor", "performing orchestra", "instrument", "vocal", "performer", "chorus master", "concertmaster" })
        {
            Assert.True(ClassicalCatalogue.IsPerformer(type), type);
        }

        foreach (var type in new[] { "composer", "producer", "engineer", "mix", "arranger", "lyricist" })
        {
            Assert.False(ClassicalCatalogue.IsPerformer(type), type);
        }
    }

    [Fact]
    public void AComposerIsTheOneArtistAnsweringToTheNameOrItsLatinForm()
    {
        var tchaikovsky = new Mbid(Guid.Parse("9ddd7abc-9e1b-471d-8031-583bc6bc8be9"));
        var dvorak = new Mbid(Guid.Parse("c70d12a2-24fe-4f83-a6e6-57d84f8efb51"));

        KnownComposer[] composers =
        [
            new(tchaikovsky, "Пётр Ильич Чайковский", "Pyotr Ilyich Tchaikovsky"),
            new(dvorak, "Antonín Dvořák", null),
        ];

        Assert.Equal(tchaikovsky, ClassicalCatalogue.Composer("Pyotr Ilyich Tchaikovsky", composers));
        Assert.Equal(dvorak, ClassicalCatalogue.Composer("Antonin Dvorak", composers));
        Assert.Null(ClassicalCatalogue.Composer("Johann Strauss II", composers));
    }

    [Fact]
    public void TwoComposersAnsweringToOneNameAreNobody()
    {
        var elder = new Mbid(Guid.Parse("e5b9ff36-2f3f-4ee3-a8b4-1d3f2e4f5a6b"));
        var younger = new Mbid(Guid.Parse("f6c0a047-3a4b-4c5d-9e6f-2a3b4c5d6e7f"));

        Assert.Null(ClassicalCatalogue.Composer(
            "Johann Strauss",
            [new(elder, "Johann Strauss", null), new(younger, "Johann Strauss", null)]));
        Assert.Equal(
            elder,
            ClassicalCatalogue.Composer("Johann Strauss", [new(elder, "Johann Strauss", null), new(elder, "Johann Strauss", "Johann Strauss")]));
    }
}
