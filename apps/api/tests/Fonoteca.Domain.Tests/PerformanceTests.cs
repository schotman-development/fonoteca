using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>When and where a recording was made, and when its piece was written.</summary>
public sealed class PerformanceTests
{
    /// <summary>Previn's Sleeping Beauty: five sessions at Abbey Road over three months.</summary>
    [Fact]
    public void FiveSessionsSpanTheFirstDayToTheLastAtOnePlace()
    {
        (string, string, string?, string?)[] sessions =
        [
            ("recorded at", "Abbey Road Studios: Studio 1", "1974-04-08", "1974-04-09"),
            ("recorded at", "Abbey Road Studios: Studio 1", "1974-06-29", "1974-06-29"),
            ("recorded at", "Abbey Road Studios: Studio 1", "1974-05-09", "1974-05-10"),
            ("performance", "The Sleeping Beauty, op. 66: Acte II, no. 12. a. Scène", "1974-04-08", "1974-06-27"),
        ];

        var (span, places) = Performance.Recorded(sessions, []);

        Assert.Equal(new DateSpan(new ReleaseDate(1974, 4, 8), new ReleaseDate(1974, 6, 29)), span);
        Assert.Equal(["Abbey Road Studios: Studio 1"], places);
    }

    /// <summary>
    /// Solti's Siegfried: the players' links carry the only dates, and the
    /// conductor, there for every session, reaches past each singer.
    /// </summary>
    [Fact]
    public void ThePlayersDatesSpanARecordingNoSessionDates()
    {
        (string, string?, string?, string?)[] links =
        [
            ("conductor", null, "1962-05", "1962-10"),
            ("vocal", "tenor vocals", "1962-06", "1962-09"),
            ("producer", null, "1997", "1997"),
            ("mix", null, "2012-01-01", "2012-02-01"),
        ];

        var (span, places) = Performance.Recorded([], links);

        Assert.Equal(new DateSpan(new ReleaseDate(1962, 5, null), new ReleaseDate(1962, 10, null)), span);
        Assert.Empty(places);
    }

    /// <summary>"1962" may mean any day of it, so it begins before May and ends after October.</summary>
    [Fact]
    public void ACoarserDateReachesFurtherBothWays()
    {
        var span = Performance.Span([("1962-05-10", "1962-10-31"), ("1962", "1962")]);

        Assert.Equal(new DateSpan(new ReleaseDate(1962, null, null), new ReleaseDate(1962, null, null)), span);
    }

    [Fact]
    public void ASideNobodyDatedStaysUnknownAndNothingDatedIsNoSpan()
    {
        Assert.Equal(new DateSpan(null, new ReleaseDate(1727, null, null)), Performance.Span([(null, "1727")]));
        Assert.Null(Performance.Span([(null, null), ("", "not a date")]));
    }

    [Fact]
    public void ACityIsNamedOnlyWhereNoPlaceIs()
    {
        (string, string, string?, string?)[] both =
        [
            ("recorded in", "Berlin", null, null),
            ("recorded at", "Teldex Studio Berlin", "2006-03-30", "2006-10-31"),
            ("mixed at", "Somewhere Else", "2007", "2007"),
        ];

        var (span, places) = Performance.Recorded(both, []);

        Assert.Equal(["Teldex Studio Berlin"], places);
        Assert.Equal(new DateSpan(new ReleaseDate(2006, 3, 30), new ReleaseDate(2006, 10, 31)), span);
        Assert.Equal(["Berlin"], Performance.Recorded([("recorded in", "Berlin", null, null)], []).Places);
    }

    /// <summary>Beethoven's Fifth, 1804 to 1808, off the piece's composer link and no other.</summary>
    [Fact]
    public void APieceWasComposedOverItsComposersLink()
    {
        var composed = Performance.Composed([("composer", "1804", "1808"), ("librettist", "1790", "1790")]);

        Assert.Equal(new DateSpan(new ReleaseDate(1804, null, null), new ReleaseDate(1808, null, null)), composed);
    }

    [Theory]
    [InlineData("1962", 1962, null, null)]
    [InlineData("1962-05", 1962, 5, null)]
    [InlineData("1974-04-08", 1974, 4, 8)]
    public void APartialDateReadsBackAsItWasWritten(string text, int year, int? month, int? day)
    {
        var date = new ReleaseDate(year, month, day);

        Assert.Equal(text, date.ToString());
        Assert.Equal(date, ReleaseDate.Parse(text));
    }
}
