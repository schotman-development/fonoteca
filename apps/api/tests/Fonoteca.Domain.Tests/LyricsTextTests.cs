using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class LyricsTextTests
{
    /// <summary>LRCLIB's own shape: a space after the time, and a blank timed line for a break.</summary>
    [Fact]
    public void TimedLinesCarryTheirStartInMilliseconds()
    {
        var lines = LyricsText.Lines("[00:00.78] Amazing grace, how sweet the sound\n[00:12.80] That saved a wretch like me\n[01:05.5] \n[02:03.123]Was blind");

        Assert.True(lines.Synced);
        Assert.Equal(
            [new LyricsLine(780, "Amazing grace, how sweet the sound"), new LyricsLine(12800, "That saved a wretch like me"), new LyricsLine(65500, ""), new LyricsLine(123123, "Was blind")],
            lines.Lines);
    }

    /// <summary>A chorus stamped twice is sung twice, so it is two lines, in time order.</summary>
    [Fact]
    public void ALineWithTwoTimesIsTwoLinesAndTheHeaderIsNoLine()
    {
        var lines = LyricsText.Lines("[ar:Somebody]\n[offset:-250]\n[00:30.00][00:10.00]Chorus\n[00:20.00]<00:20.00>Verse <00:20.50>words");

        Assert.Equal(-250, lines.Offset);
        Assert.Equal(
            [new LyricsLine(10000, "Chorus"), new LyricsLine(20000, "Verse words"), new LyricsLine(30000, "Chorus")],
            lines.Lines);
    }

    /// <summary>Plain words keep their stanza breaks and a sung "[Chorus]", and lose the blank ends.</summary>
    [Fact]
    public void PlainWordsAreLinesWithNoStart()
    {
        var lines = LyricsText.Lines("\r\n[Chorus]\r\nI've been really trying\r\n\r\n[Verse 1: Marvin]\r\nBaby\r\n\r\n");

        Assert.False(lines.Synced);
        Assert.Equal(["[Chorus]", "I've been really trying", "", "[Verse 1: Marvin]", "Baby"], lines.Lines.Select(line => line.Text));
        Assert.All(lines.Lines, line => Assert.Null(line.Start));
    }

    [Fact]
    public void PlainIsTheWordsWithoutTheTimes()
    {
        Assert.Equal("First\nSecond", LyricsText.Plain("[ti:Song]\n[00:01.00] First\n[00:02.00] Second"));
        Assert.Equal("Just words", LyricsText.Plain("Just words"));
    }

    /// <summary>What sits beside tracks here under their names and is not lyrics.</summary>
    [Theory]
    [InlineData("www.t.me/pmedia_music")]
    [InlineData("kmn")]
    [InlineData("[ti:Mars, the Bringer of War]\n[ar:Berliner Philharmoniker]\n[al:The Planets]\n[length:07:21]")]
    [InlineData("[00:01.00] One\n[00:02.00] Two\n[00:03.00]")]
    [InlineData("")]
    public void AnAdvertOrAHeaderIsNotLyrics(string text) => Assert.False(LyricsText.IsLyrics(text));

    [Theory]
    [InlineData("Swore I'd never do this again\nThink that I'm so clever\nHe just called me")]
    [InlineData("[00:01.00] One\n[00:02.00] Two\n[00:03.00] Three")]
    public void ThreeLinesOfWordsAreLyrics(string text) => Assert.True(LyricsText.IsLyrics(text));
}
