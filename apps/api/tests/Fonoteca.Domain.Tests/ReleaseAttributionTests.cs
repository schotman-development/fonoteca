using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;
using static Fonoteca.Domain.Tests.AlbumFolderFixtures;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The rule that decides which album a folder is, and whether a pressing of it is
/// proven.
/// </summary>
/// <remarks>
/// The shapes here are the ones a real library produced: a compilation, a box set
/// and a single each reaching into an album's folder; two pressings of one album
/// a second apart; a remaster whose lengths match and an original whose do not;
/// a song identified as another take of itself. The folder is the unit, so every
/// case is one folder's files.
/// </remarks>
public sealed class ReleaseAttributionTests
{
    private const double Coverage = 0.25;

    private static FolderDecision Decide(
        IReadOnlyList<FolderFile> files,
        IReadOnlyCollection<MusicBrainzRelease> candidates,
        Mbid? byHand = null,
        bool complete = true) =>
        ReleaseAttribution.Decide(files, candidates, byHand, complete, Coverage);

    private static readonly (string Name, int? Ms)[] Album =
        Tracks(("a", 180_437), ("b", 240_120), ("c", 200_880));

    [Fact]
    public void AnAlbumWhoseTracksAreAllPresentAndMatchIsProvenAndSeated()
    {
        MusicBrainzRelease[] candidates = [Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 180_440, candidates),
            File("b", 240_110, candidates),
            File("c", 200_900, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Equal(Group, decision.Album);
        Assert.Equal(Id("album"), decision.Edition?.Id);
        Assert.All(decision.Files, file => Assert.Equal(ReleaseAttributionOutcome.Attributed, file.Outcome));
        Assert.Equal([1, 2, 3], decision.Files.Select(file => file.Slot?.Position));
    }

    [Fact]
    public void AFileTheAlbumDoesNotPrintStaysWithTheFolder()
    {
        // The folder is the album. A compilation printing the album's three songs
        // and a fourth does not take the fourth away, and the compilation does not
        // win the folder either: it explains four files at a third of itself.
        MusicBrainzRelease[] candidates =
        [
            Release("album", Album),
            Release("compilation", [.. Album, ("d", 190_000), .. Filler("comp", 8)], group: OtherGroup),
        ];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
            File("d", 190_000, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Equal(Group, decision.Album);
        Assert.Equal(
            [ReleaseAttributionOutcome.GroupOnly, ReleaseAttributionOutcome.GroupOnly,
             ReleaseAttributionOutcome.GroupOnly, ReleaseAttributionOutcome.OnNoEdition],
            decision.Files.Select(file => file.Outcome));

        // Four files against a three-track pressing proves nothing.
        Assert.Null(decision.Edition);
    }

    [Fact]
    public void ABoxSetDoesNotSwallowTheAlbumItReprints()
    {
        var album = Tracks([.. Enumerable.Range(0, 10).Select(n => ($"t{n}", 200_437 + n))]);
        MusicBrainzRelease[] candidates =
        [
            Release("album", album),
            Release("box", [.. album, .. Filler("box", 66)], group: OtherGroup),
        ];
        var files = album.Select(track => File(track.Name, track.Ms!.Value, candidates)).ToList();

        var decision = Decide(files, candidates);

        Assert.Equal(Group, decision.Album);
        Assert.Equal(Id("album"), decision.Edition?.Id);
    }

    [Fact]
    public void ASingleDoesNotOutrankTheAlbumItWasLiftedFrom()
    {
        var album = Tracks([.. Enumerable.Range(0, 10).Select(n => ($"t{n}", 200_437 + n))]);
        MusicBrainzRelease[] candidates =
        [
            Release("album", album),
            Release("single", [album[2], album[3]], group: OtherGroup),
        ];
        var files = album.Select(track => File(track.Name, track.Ms!.Value, candidates)).ToList();

        Assert.Equal(Group, Decide(files, candidates).Album);
    }

    [Fact]
    public void OnlyTheEditionWithinAHundredMillisecondsOnEveryTrackIsProven()
    {
        // The remaster matches; the original pressing is two seconds out on every
        // track. Both are the album, and only one is the pressing.
        MusicBrainzRelease[] candidates =
        [
            Release("remaster", Album, year: 2015),
            Release("original", Tracks(("a", 182_437), ("b", 242_120), ("c", 202_880)), year: 1979),
        ];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Equal(Id("remaster"), decision.Edition?.Id);
        Assert.Equal(1, decision.EditionsProven);
    }

    [Fact]
    public void TwoEditionsThatBothProveLeaveOnlyTheAlbum()
    {
        // Two pressings of one master, a CD from two countries: the audio cannot
        // tell them apart, and naming one would claim its barcode and label.
        MusicBrainzRelease[] candidates = [Release("uk", Album), Release("us", Album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Equal(Group, decision.Album);
        Assert.Null(decision.Edition);
        Assert.Equal(2, decision.EditionsProven);
        Assert.All(decision.Files, file =>
        {
            Assert.Equal(ReleaseAttributionOutcome.GroupOnly, file.Outcome);
            Assert.Null(file.Slot);
        });
    }

    [Fact]
    public void APseudoReleaseCopyingAPressingsLengthsDoesNotTieIt()
    {
        // A transliterated track list prints the original's lengths; it is not a
        // second pressing, and counting it would leave every such album unproven.
        MusicBrainzRelease[] candidates = [Release("cd", Album), Release("latin", Album, status: "Pseudo-Release")];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Equal(Id("cd"), decision.Edition?.Id);
        Assert.Equal(1, decision.EditionsProven);
    }

    [Fact]
    public void OneTrackFifteenSecondsOutLeavesOnlyTheAlbum()
    {
        // Eleven tracks exact and one fifteen seconds out averages 1.25s, which a
        // mean would pass. Every track has to agree.
        var album = Tracks([.. Enumerable.Range(0, 12).Select(n => ($"t{n}", 200_437 + n))]);
        MusicBrainzRelease[] candidates = [Release("album", album)];
        var files = album
            .Select((track, n) => File(track.Name, track.Ms!.Value + (n == 5 ? 15_000 : 0), candidates))
            .ToList();

        var decision = Decide(files, candidates);

        Assert.Equal(Group, decision.Album);
        Assert.Null(decision.Edition);
    }

    [Fact]
    public void ATrackCountThatIsNotTheFoldersFileCountProvesNothing()
    {
        MusicBrainzRelease[] candidates = [Release("album", [.. Album, ("d", 150_111)])];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Equal(Group, decision.Album);
        Assert.Null(decision.Edition);
    }

    [Fact]
    public void LengthsKnownOnlyToTheSecondOrNotAtAllProveNothing()
    {
        MusicBrainzRelease[] whole = [Release("album", Tracks(("a", 180_000), ("b", 240_000), ("c", 201_000)))];
        FolderFile[] exact =
        [
            File("a", 180_000, whole),
            File("b", 240_000, whole),
            File("c", 201_000, whole),
        ];

        Assert.Null(Decide(exact, whole).Edition);

        MusicBrainzRelease[] unknown = [Release("album", [("a", null), ("b", 240_120), ("c", 200_880)])];
        FolderFile[] files =
        [
            File("a", 180_437, unknown),
            File("b", 240_120, unknown),
            File("c", 200_880, unknown),
        ];

        Assert.Null(Decide(files, unknown).Edition);
    }

    [Fact]
    public void AFolderMostlySomethingElseIsAQuestionAndOneMostlyTheAlbumIsTheAlbum()
    {
        // Eleven files, six of them the album's: the album. Ten files, five of
        // them: a compilation or a mix, and a person's to answer.
        var album = Tracks([.. Enumerable.Range(0, 6).Select(n => ($"t{n}", 200_437 + n))]);
        MusicBrainzRelease[] candidates = [Release("album", album)];

        List<FolderFile> Folder(int onAlbum, int others) =>
        [
            .. album.Take(onAlbum).Select(track => File(track.Name, track.Ms!.Value, candidates)),
            .. Enumerable.Range(0, others).Select(n => File($"stray{n}", 100_000, candidates)),
        ];

        Assert.Equal(Group, Decide(Folder(6, 5), candidates).Album);

        var refused = Decide(Folder(5, 5), candidates);
        Assert.Null(refused.Album);
        Assert.Equal(Group, refused.Proposed);
    }

    [Fact]
    public void AnUnidentifiedFileCountsTowardsTheFolderButGetsNoDecision()
    {
        MusicBrainzRelease[] candidates = [Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            Unidentified("Artist/Album/03 ?.flac"),
            Unidentified("Artist/Album/04 ?.flac"),
        ];

        var decision = Decide(files, candidates);

        // Two of four is not more than half.
        Assert.Null(decision.Album);
        Assert.Equal(2, decision.Files.Count);
    }

    [Fact]
    public void ATieAcrossTwoDifferentAlbumsIsRefusedRatherThanSplitTheDifference()
    {
        MusicBrainzRelease[] candidates =
        [
            Release("one", Album),
            Release("other", Album, group: OtherGroup),
        ];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Null(decision.Album);
        Assert.All(decision.Files, file => Assert.Equal(ReleaseAttributionOutcome.NoConfidentFit, file.Outcome));
    }

    [Fact]
    public void ARecordingOnNoReleaseAtAllIsDistinguishedFromAPoorFit()
    {
        MusicBrainzRelease[] candidates = [Release("anthology", [("a", 180_437), .. Filler("anth", 39)])];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("nowhere", 200_000, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Null(decision.Album);
        Assert.Equal(
            [ReleaseAttributionOutcome.NoConfidentFit, ReleaseAttributionOutcome.NoCandidate],
            decision.Files.Select(file => file.Outcome));
    }

    [Fact]
    public void AStandaloneSingleIsProvenDespiteBeingOneFile()
    {
        MusicBrainzRelease[] candidates = [Release("single", Tracks(("a", 180_437)))];

        var decision = Decide([File("a", 180_437, candidates)], candidates);

        Assert.Equal(Id("single"), decision.Edition?.Id);
    }

    [Fact]
    public void TheAnswerDoesNotDependOnTheOrderTheCandidatesArriveIn()
    {
        MusicBrainzRelease[] candidates =
        [
            Release("uk", Album),
            Release("us", Album),
            Release("compilation", [.. Album, .. Filler("comp", 8)], group: OtherGroup),
        ];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
        ];

        Assert.Equal(Decide(files, candidates), Decide(files, [.. candidates.Reverse()]), Same);
    }

    [Fact]
    public void AnAlbumChosenByHandIsKeptAndOnlyTheEditionIsProved()
    {
        // The rule would pick the other album; the person named this one, and it
        // stands — held to no majority — while the pressing is still proved.
        MusicBrainzRelease[] candidates =
        [
            Release("album", Album),
            Release("theirs", Album, group: OtherGroup),
        ];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
        ];

        var decision = Decide(files, candidates, byHand: OtherGroup);

        Assert.Equal(OtherGroup, decision.Album);
        Assert.Equal(Id("theirs"), decision.Edition?.Id);
    }

    [Fact]
    public void AFileIdentifiedAsAnotherTakeTakesTheAlbumsRecordingItsClusterNames()
    {
        // "Blue in Green (Take 1)": the cluster is linked to the album's own
        // recording too, and the album is what says which one is meant.
        MusicBrainzRelease[] candidates = [Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("take", 200_880, candidates, linked: new HashSet<Mbid> { Recording("c"), Recording("elsewhere") }),
        ];

        var decision = Decide(files, candidates);

        var take = decision.Files.Single(file => file.File == FileId("take"));
        Assert.Equal(Recording("c"), take.Recording);
        Assert.Equal(ReleaseAttributionOutcome.Attributed, take.Outcome);
    }

    [Fact]
    public void ATakeIsNotSubstitutedOntoATrackAnotherFileHolds()
    {
        MusicBrainzRelease[] candidates = [Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
            File("take", 200_880, candidates, linked: new HashSet<Mbid> { Recording("c") }),
        ];

        var decision = Decide(files, candidates);

        var take = decision.Files.Single(file => file.File == FileId("take"));
        Assert.Equal(Recording("take"), take.Recording);
        Assert.Equal(ReleaseAttributionOutcome.OnNoEdition, take.Outcome);
    }

    [Theory]
    [InlineData(203_881)]
    [InlineData(197_879)]
    public void ATakeMoreThanThreeSecondsFromTheGapIsNotSubstituted(int ms)
    {
        MusicBrainzRelease[] candidates = [Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("take", ms, candidates, linked: new HashSet<Mbid> { Recording("c") }),
        ];

        var take = Decide(files, candidates).Files.Single(file => file.File == FileId("take"));

        Assert.Equal(Recording("take"), take.Recording);
        Assert.Equal(ReleaseAttributionOutcome.OnNoEdition, take.Outcome);
    }

    [Fact]
    public void ATakeWhoseClusterNamesTwoOfTheAlbumsGapsTakesNeither()
    {
        var album = Tracks(("a", 180_437), ("b", 240_120), ("c", 200_880), ("d", 201_120));
        MusicBrainzRelease[] candidates = [Release("album", album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("take", 200_990, candidates, linked: new HashSet<Mbid> { Recording("c"), Recording("d") }),
        ];

        var take = Decide(files, candidates).Files.Single(file => file.File == FileId("take"));

        Assert.Equal(Recording("take"), take.Recording);
    }

    [Fact]
    public void TwoTakesWantingOneGapAreNeitherSubstituted()
    {
        MusicBrainzRelease[] candidates = [Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("take1", 200_880, candidates, linked: new HashSet<Mbid> { Recording("c") }),
            File("take2", 200_881, candidates, linked: new HashSet<Mbid> { Recording("c") }),
        ];

        var decision = Decide(files, candidates);

        Assert.DoesNotContain(decision.Files, file => file.Recording == Recording("c"));
    }

    [Fact]
    public void ACompilationDoesNotOutbidTheAlbumItPlundered()
    {
        // The compilation holds every file the folder does, and seventeen more
        // songs: the album covers itself whole and the compilation a sixth.
        var compilation = Release("hits", [.. Album, .. Filler("hits", 17)], group: OtherGroup);
        MusicBrainzRelease[] candidates = [compilation, Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates),
            File("b", 240_120, candidates),
            File("c", 200_880, candidates),
        ];

        var decision = Decide(files, candidates);

        Assert.Equal(Group, decision.Album);
        Assert.Equal(Id("album"), decision.Edition?.Id);
    }

    [Fact]
    public void ATaggedOrderEveryEditionContradictsIsAQuestionWithoutAProvenPressing()
    {
        // Tagged c, b, a against an album every edition prints a, b, c — and no
        // pressing proven (the lengths are a second out), so nothing stands
        // between the question and the files.
        MusicBrainzRelease[] candidates = [Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 181_437, candidates, track: 3),
            File("b", 241_120, candidates, track: 2),
            File("c", 201_880, candidates, track: 1),
        ];

        var decision = Decide(files, candidates);

        Assert.Null(decision.Album);
        Assert.Equal(FolderOrderOutcome.Contradicted, decision.Order);
        Assert.All(decision.Files, file => Assert.Equal(ReleaseAttributionOutcome.OrderContradicted, file.Outcome));
    }

    [Fact]
    public void AProvenPressingStandsAgainstTagsThatContradictIt()
    {
        MusicBrainzRelease[] candidates = [Release("album", Album)];
        FolderFile[] files =
        [
            File("a", 180_437, candidates, track: 3),
            File("b", 240_120, candidates, track: 2),
            File("c", 200_880, candidates, track: 1),
        ];

        var decision = Decide(files, candidates);

        Assert.Equal(Id("album"), decision.Edition?.Id);
        Assert.Equal(FolderOrderOutcome.Contradicted, decision.Order);
    }

    private static readonly IEqualityComparer<FolderDecision> Same = EqualityComparer<FolderDecision>.Create(
        (left, right) => left!.Album == right!.Album
            && left.Edition?.Id == right.Edition?.Id
            && left.EditionsProven == right.EditionsProven
            && left.Files.SequenceEqual(right.Files),
        decision => decision.Album.GetHashCode());
}
