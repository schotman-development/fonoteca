using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// An album's track list when no single edition is claimed: every edition's at
/// once, and which of them to measure a rip against.
/// </summary>
public sealed class EditionsTests
{
    private static readonly RecordingId A = RecordingId.New();
    private static readonly RecordingId B = RecordingId.New();
    private static readonly RecordingId C = RecordingId.New();
    private static readonly RecordingId Bonus1 = RecordingId.New();
    private static readonly RecordingId Bonus2 = RecordingId.New();

    private static EditionTracks Edition(params (int Disc, RecordingId Recording)[] slots)
    {
        var positions = new Dictionary<int, int>();

        return new EditionTracks(
            ReleaseId.New(),
            [.. slots.Select(slot =>
            {
                var position = positions[slot.Disc] = positions.GetValueOrDefault(slot.Disc) + 1;
                return new EditionSlot(slot.Disc, position, $"{position}", null, null, slot.Recording);
            })]);
    }

    [Fact]
    public void TheDeluxesBonusTracksFollowTheAlbumTheyWereAddedTo()
    {
        var standard = Edition((1, A), (1, B), (1, C));
        var deluxe = Edition((1, A), (1, B), (1, C), (2, Bonus1), (2, Bonus2));

        var combined = Editions.Combine([standard, deluxe], new HashSet<RecordingId> { A, B, C });

        Assert.Equal([A, B, C, Bonus1, Bonus2], combined.Select(row => row.Slot.Recording));
        Assert.Equal([true, true, true, false, false], combined.Select(row => row.Held));

        // The shared tracks are on both; the bonus disc only on the deluxe,
        // printed where the deluxe prints it.
        Assert.Equal([standard.Id, deluxe.Id], combined[0].On);
        Assert.Equal([deluxe.Id], combined[3].On);
        Assert.Equal(2, combined[3].Slot.Disc);
    }

    [Fact]
    public void AnEditionInsertingATrackMidAlbumAddsOneRowAndMovesNothing()
    {
        var album = Edition((1, A), (1, B), (1, C));
        var japanese = Edition((1, A), (1, Bonus1), (1, B), (1, C));

        var combined = Editions.Combine([album, japanese], new HashSet<RecordingId>());

        // The first edition is the running order; the insertion is appended, not
        // threaded in, and does not renumber the album.
        Assert.Equal([A, B, C, Bonus1], combined.Select(row => row.Slot.Recording));
        Assert.Equal([1, 2, 3], combined.Take(3).Select(row => row.Slot.Position));
    }

    [Fact]
    public void ABoxCarryingTheAlbumOnTwoMediaListsEachSongOnce()
    {
        // Vinyl on discs 1 and 2, the same album again on the CD inside.
        var box = Edition((1, A), (2, B), (3, A), (3, B));

        var combined = Editions.Combine([box], new HashSet<RecordingId> { A, B });

        Assert.Equal([A, B], combined.Select(row => row.Slot.Recording));
        Assert.Equal([1, 2], combined.Select(row => row.Slot.Disc));
    }

    [Fact]
    public void AnotherNightOfTheTourAddsOnlyTheSongsThisOneDidNotPlay()
    {
        // Amsterdam and Nashville share a release group and not one recording:
        // the same two songs played again are not missing, the encore is.
        var amsterdam = Titled(Edition((1, A), (1, B)), "Why Aye Man", "Going Home");
        var nashville = Titled(Edition((1, C), (1, Bonus1), (1, Bonus2)), "Why Aye Man", "going home", "Encore");

        var combined = Editions.Combine([amsterdam, nashville], new HashSet<RecordingId> { A, B }, firstIsClaimed: true);

        Assert.Equal([A, B, Bonus2], combined.Select(row => row.Slot.Recording));
    }

    [Fact]
    public void AnEditionSharingTheAlbumsRecordingsKeepsASongPlayedAgain()
    {
        // A deluxe's bonus live disc: the same titles, new recordings, and every
        // one of them a track the standard edition's owner does not have.
        var standard = Titled(Edition((1, A)), "Why Aye Man");
        var deluxe = Titled(Edition((1, A), (2, C)), "Why Aye Man", "Why Aye Man");

        var combined = Editions.Combine([standard, deluxe], new HashSet<RecordingId> { A });

        Assert.Equal([A, C], combined.Select(row => row.Slot.Recording));
    }

    private static EditionTracks Titled(EditionTracks edition, params string[] titles) =>
        edition with { Slots = [.. edition.Slots.Select((slot, index) => slot with { Title = titles[index] })] };

    [Fact]
    public void AClaimedPressingThatPrintsARecordingTwiceKeepsBothPlaces()
    {
        // "All Night Long" and its 12" version on one album, one recording.
        var album = Edition((1, A), (1, B), (1, A));

        var combined = Editions.Combine([album], new HashSet<RecordingId> { A, B }, firstIsClaimed: true);

        Assert.Equal([A, B, A], combined.Select(row => row.Slot.Recording));
    }

    [Fact]
    public void ARipIsMeasuredAgainstTheShortestEditionHoldingWhatItHas()
    {
        var standard = Edition((1, A), (1, B), (1, C));
        var deluxe = Edition((1, A), (1, B), (1, C), (2, Bonus1), (2, Bonus2));

        Assert.Same(standard, Editions.Nearest([deluxe, standard], new HashSet<RecordingId> { A, B }));
        Assert.Same(deluxe, Editions.Nearest([standard, deluxe], new HashSet<RecordingId> { A, Bonus1 }));
    }

    [Fact]
    public void AnLpAsNearAsTheCdDoesNotSetTheOrder()
    {
        var cd = Edition((1, A), (1, B));
        var lp = new EditionTracks(
            ReleaseId.New(),
            [new EditionSlot(1, 1, "A1", null, null, A), new EditionSlot(2, 1, "B1", null, null, B)]);
        var timed = cd with
        {
            Slots = [.. cd.Slots.Select(slot => slot with { Length = TimeSpan.FromMinutes(4) })],
        };

        Assert.Same(timed, Editions.Nearest([lp, timed], new HashSet<RecordingId> { A, B }));
    }

    [Fact]
    public void NoEditionsIsNoNearestEdition() =>
        Assert.Null(Editions.Nearest([], new HashSet<RecordingId> { A }));
}
