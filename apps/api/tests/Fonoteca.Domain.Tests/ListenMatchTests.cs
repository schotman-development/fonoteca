using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class ListenMatchTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static readonly Mbid Gravity = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));

    private static HeldSong Held(string title, string artist, string? album, Mbid? mbid = null, RecordingId? recording = null) =>
        new(MediaFileId.New(), recording ?? RecordingId.New(), ReleaseGroupId.New(), mbid, title, artist, album);

    private static HeardListen Heard(string track, string artist, string? release = null, Mbid? recording = null) =>
        new(At, artist, track, release, recording);

    [Fact]
    public void TheRecordingMbidWinsOverTheNames()
    {
        var right = Held("Gravity (Live)", "Pepita Salim", "Covers", Gravity);
        var match = new ListenMatch([Held("Gravity", "Pepita Salim", "Covers"), right]);

        Assert.Equal(right, match.Of(Heard("Gravity", "Pepita Salim", recording: Gravity)));
    }

    [Fact]
    public void TitleAndArtistAreFoldedAsNamesAre()
    {
        var held = Held("Dvořák: Symphony No. 9", "Berliner Philharmoniker", "New World");

        Assert.Equal(held, new ListenMatch([held]).Of(Heard("dvorak symphony no 9", "BERLINER PHILHARMONIKER")));
    }

    [Fact]
    public void OfOneRecordingsCopiesTheOneOnTheListensAlbumIsTaken()
    {
        var song = RecordingId.New();
        var album = Held("Gravity", "Pepita Salim", "Covers", recording: song);
        var bestOf = Held("Gravity", "Pepita Salim", "Best Of", recording: song);
        var match = new ListenMatch([album, bestOf]);

        Assert.Equal(bestOf, match.Of(Heard("Gravity", "Pepita Salim", "Best Of")));
        Assert.Equal(song, match.Of(Heard("Gravity", "Pepita Salim", "Nowhere"))!.Recording);
    }

    [Fact]
    public void ANameNamingTwoRecordingsIsNoAnswerUnlessTheAlbumNamesOne()
    {
        var studio = Held("Gravity", "Pepita Salim", "Covers");
        var live = Held("Gravity", "Pepita Salim", "Live");
        var match = new ListenMatch([studio, live]);

        Assert.Null(match.Of(Heard("Gravity", "Pepita Salim")));
        Assert.Null(match.Of(Heard("Gravity", "Pepita Salim", "Somewhere Else")));
        Assert.Equal(live, match.Of(Heard("Gravity", "Pepita Salim", "Live")));
    }

    [Fact]
    public void AnMbidNothingHeldCarriesFallsBackToTheNames()
    {
        var held = Held("Gravity", "Pepita Salim", "Covers");

        Assert.Equal(held, new ListenMatch([held]).Of(Heard("Gravity", "Pepita Salim", recording: Gravity)));
        Assert.Null(new ListenMatch([held]).Of(Heard("True Colors", "Pepita Salim")));
    }
}
