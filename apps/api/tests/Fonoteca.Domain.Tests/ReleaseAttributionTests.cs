using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The rule that decides which album a file came from.
/// </summary>
/// <remarks>
/// The shapes here are the ones a real library produced. Two of them are
/// failures the first working prototype had, measured against 389 real files
/// before this rule existed, and they are the reason it is shaped the way it is:
/// a bootleg compilation that outbid four albums at once, and a pair of editions
/// that fitted identically while disagreeing about where the music sat.
///
/// No folder appears anywhere in this file, and that is the invariant under
/// test as much as any assertion is.
/// </remarks>
public sealed class ReleaseAttributionTests
{
    private static readonly Mbid AlbumId = Mb("11111111-1111-4111-8111-111111111111");
    private static readonly Mbid RemasterId = Mb("22222222-2222-4222-8222-222222222222");
    private static readonly Mbid CompilationId = Mb("33333333-3333-4333-8333-333333333333");
    private static readonly Mbid VinylId = Mb("44444444-4444-4444-8444-444444444444");
    private static readonly Mbid SingleDiscId = Mb("55555555-5555-4555-8555-555555555555");
    private static readonly Mbid GroupId = Mb("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Mbid OtherGroupId = Mb("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    /// <summary>
    /// The ordinary case: every track of an album present, nothing else close.
    /// </summary>
    [Fact]
    public void AnAlbumWhoseTracksAreAllPresentIsAttributedOutright()
    {
        var files = Library(("a", 180), ("b", 200), ("c", 220));

        var result = ReleaseAttribution.Assign(
            files,
            [Release(AlbumId, "Album", tracks: [("a", 180), ("b", 200), ("c", 220)])]);

        Assert.All(result, assignment =>
        {
            Assert.Equal(ReleaseAttributionOutcome.Attributed, assignment.Outcome);
            Assert.Equal(AlbumId, assignment.Release);
            Assert.Equal(0, assignment.EditionAlternatives);
        });

        Assert.Equal([1, 2, 3], result.Select(a => a.Position).ToArray());
        Assert.All(result, assignment => Assert.Equal(1, assignment.DiscNumber));
    }

    /// <summary>
    /// The failure that shaped the ladder.
    /// </summary>
    /// <remarks>
    /// A greedy that maximises files explained picks the compilation first — it
    /// covers four files against the album's three — and having taken them,
    /// leaves the album holding a third of itself. Measured on real data, one
    /// thirty-one-track bootleg took seventeen files out of four albums this way.
    ///
    /// The compilation is a perfectly real release and the files really do appear
    /// on it. What makes it the wrong answer is that it explains almost none of
    /// itself, and the album explains all of itself.
    /// </remarks>
    [Fact]
    public void ACompilationDoesNotOutbidTheAlbumItPlundered()
    {
        var files = Library(("a", 180), ("b", 200), ("c", 220), ("d", 240));

        var result = ReleaseAttribution.Assign(
            files,
            [
                Release(AlbumId, "Album", tracks: [("a", 180), ("b", 200), ("c", 220)]),

                // Twenty tracks, four of which we hold: coverage 0.20, and it
                // reaches one file the album cannot explain.
                Release(
                    CompilationId,
                    "Greatest Hits",
                    tracks: [("a", 182), ("b", 202), ("c", 222), ("d", 242), .. Filler(16)]),
            ]);

        var album = result.Where(a => a.Release == AlbumId).ToList();
        Assert.Equal(3, album.Count);
        Assert.All(album, a => Assert.Equal(ReleaseAttributionOutcome.Attributed, a.Outcome));

        // And the fourth file, which really is only on the compilation, is still
        // refused: one track of twenty is not evidence of owning that release.
        // Filing it there would be the lenient answer, and the lenient answer is
        // what scattered a real compilation across seven albums.
        var orphan = Assert.Single(result, a => a.File == File("d"));
        Assert.Equal(ReleaseAttributionOutcome.NoConfidentFit, orphan.Outcome);
        Assert.Null(orphan.Release);
    }

    /// <summary>
    /// Duration is what separates two editions that a track list cannot.
    /// </summary>
    /// <remarks>
    /// The <i>Off the Wall</i> case, reduced. The album and its remaster carry
    /// the same songs in the same order, so coverage is 1.00 for both and only
    /// the running times differ. The files here last exactly what the remaster
    /// prints, which is the answer even though the original is older and would
    /// win any tie-break — because it never reaches the tie-break.
    /// </remarks>
    [Fact]
    public void TheEditionWhoseRunningTimesMatchWinsOverTheOlderPressing()
    {
        var files = Library(("a", 180), ("b", 200), ("c", 220));

        var result = ReleaseAttribution.Assign(
            files,
            [
                Release(AlbumId, "Album", tracks: [("a", 178), ("b", 198), ("c", 218)], year: 1979),
                Release(RemasterId, "Album", tracks: [("a", 180), ("b", 200), ("c", 220)], year: 2015),
            ]);

        Assert.All(result, a => Assert.Equal(RemasterId, a.Release));
        Assert.All(result, a => Assert.Equal(ReleaseAttributionOutcome.Attributed, a.Outcome));
    }

    /// <summary>
    /// Editions that agree about the music are chosen between, and the choosing
    /// is recorded.
    /// </summary>
    /// <remarks>
    /// Three pressings of <i>Sloe Gin</i> fitted identically in the real run.
    /// Nothing this catalogue stores about the file differs between them, so
    /// refusing to file it would cost the user an album to gain nothing. The
    /// count of alternatives is what makes the coin-flip reviewable afterwards.
    /// </remarks>
    [Fact]
    public void EditionsThatAgreeAboutThePositionsAreChosenBetweenAndCounted()
    {
        var tracks = new[] { ("a", 180), ("b", 200), ("c", 220) };
        var files = Library(tracks);

        var result = ReleaseAttribution.Assign(
            files,
            [
                Release(VinylId, "Album", tracks, year: 2009, status: "Official"),
                Release(AlbumId, "Album", tracks, year: 2007, status: "Official"),
                Release(RemasterId, "Album", tracks, year: 2007, status: "Promotion"),
            ]);

        Assert.All(result, assignment =>
        {
            Assert.Equal(ReleaseAttributionOutcome.AttributedAmbiguously, assignment.Outcome);

            // Official first, then the earliest — so the 2007 official pressing,
            // not the 2007 promo and not the 2009 reissue.
            Assert.Equal(AlbumId, assignment.Release);
            Assert.Equal(2, assignment.EditionAlternatives);
        });
    }

    /// <summary>
    /// The other prototype failure: a tie that cannot be chosen between without
    /// inventing a track number.
    /// </summary>
    /// <remarks>
    /// A two-disc original against a single-disc reissue holding the same music.
    /// Both explain every file, both to the same accuracy, and they contradict
    /// each other about which disc the music is on. Picking one would write a
    /// disc and a position into the catalogue that the evidence does not support,
    /// so only the album survives — which is a real answer and a smaller one.
    /// </remarks>
    [Fact]
    public void EditionsThatDisagreeAboutThePositionsKeepOnlyTheAlbum()
    {
        var files = Library(("a", 180), ("b", 200), ("c", 220), ("d", 240));

        var twoDisc = Release(
            AlbumId,
            "Album",
            tracks: [("a", 180), ("b", 200)],
            group: GroupId);

        twoDisc = twoDisc with
        {
            Tracks = [.. twoDisc.Tracks, Track(2, 1, "c", 220), Track(2, 2, "d", 240)],
        };

        var result = ReleaseAttribution.Assign(
            files,
            [
                twoDisc,
                Release(
                    SingleDiscId,
                    "Album",
                    tracks: [("a", 180), ("b", 200), ("c", 220), ("d", 240)],
                    group: GroupId),
            ]);

        Assert.All(result, assignment =>
        {
            Assert.Equal(ReleaseAttributionOutcome.GroupOnly, assignment.Outcome);
            Assert.Equal(GroupId, assignment.ReleaseGroup);

            // The whole point of the outcome: no position is asserted.
            Assert.Null(assignment.Release);
            Assert.Null(assignment.DiscNumber);
            Assert.Null(assignment.Position);
        });
    }

    /// <summary>
    /// When even the album is ambiguous, nothing survives and the file is refused.
    /// </summary>
    [Fact]
    public void ATieAcrossTwoDifferentAlbumsIsRefusedRatherThanSplitTheDifference()
    {
        var files = Library(("a", 180), ("b", 200), ("c", 220), ("d", 240));

        var left = Release(AlbumId, "One", tracks: [("a", 180), ("b", 200)], group: GroupId);

        left = left with { Tracks = [.. left.Tracks, Track(2, 1, "c", 220), Track(2, 2, "d", 240)] };

        var result = ReleaseAttribution.Assign(
            files,
            [
                left,
                Release(
                    SingleDiscId,
                    "Two",
                    tracks: [("a", 180), ("b", 200), ("c", 220), ("d", 240)],
                    group: OtherGroupId),
            ]);

        Assert.All(result, assignment =>
        {
            Assert.Equal(ReleaseAttributionOutcome.NoConfidentFit, assignment.Outcome);
            Assert.Null(assignment.Release);
            Assert.Null(assignment.ReleaseGroup);
        });
    }

    /// <summary>
    /// A file that fits nothing well is refused rather than filed badly.
    /// </summary>
    /// <remarks>
    /// The strict end of the design. A licensed recording appearing on a
    /// forty-track anthology and nowhere else is exactly the case that scattered
    /// a compilation across seven releases in the prototype; leaving it
    /// unattributed puts it in a review queue instead of in the wrong album.
    /// </remarks>
    [Fact]
    public void AFileNoReleaseExplainsWellIsLeftUnattributed()
    {
        var files = Library(("a", 180));

        var result = ReleaseAttribution.Assign(
            files,
            [Release(CompilationId, "Anthology", tracks: [("a", 180), .. Filler(39)])]);

        var only = Assert.Single(result);
        Assert.Equal(ReleaseAttributionOutcome.NoConfidentFit, only.Outcome);
        Assert.Null(only.Release);
    }

    /// <summary>
    /// "No release holds this recording" and "no release holds it well enough"
    /// are different facts and lead to different follow-ups.
    /// </summary>
    [Fact]
    public void ARecordingOnNoReleaseAtAllIsDistinguishedFromAPoorFit()
    {
        var files = Library(("a", 180), ("z", 300));

        var result = ReleaseAttribution.Assign(
            files,
            [Release(CompilationId, "Anthology", tracks: [("a", 180), .. Filler(39)])]);

        Assert.Equal(
            ReleaseAttributionOutcome.NoConfidentFit,
            Assert.Single(result, a => a.File == File("a")).Outcome);

        Assert.Equal(
            ReleaseAttributionOutcome.NoCandidate,
            Assert.Single(result, a => a.File == File("z")).Outcome);
    }

    /// <summary>
    /// A standalone single is a release with one track, which no rung that
    /// demands two files could ever reach.
    /// </summary>
    [Fact]
    public void AStandaloneSingleIsAttributedDespiteBeingOneFile()
    {
        var files = Library(("a", 180));

        var result = ReleaseAttribution.Assign(files, [Release(AlbumId, "Single", tracks: [("a", 180)])]);

        Assert.Equal(ReleaseAttributionOutcome.Attributed, Assert.Single(result).Outcome);
    }

    /// <summary>
    /// Five encodings of one song are five files, not a five-track album.
    /// </summary>
    /// <remarks>
    /// A slot takes one file. Without that, a folder of duplicate rips would fill
    /// a release to full coverage on the strength of one song and win outright.
    /// </remarks>
    [Fact]
    public void DuplicateEncodingsCannotFillOneTrackListSeveralTimesOver()
    {
        var files = new List<AttributionFile>
        {
            new(File("a1"), Recording("a"), TimeSpan.FromSeconds(180)),
            new(File("a2"), Recording("a"), TimeSpan.FromSeconds(180)),
            new(File("a3"), Recording("a"), TimeSpan.FromSeconds(180)),
        };

        var result = ReleaseAttribution.Assign(
            files,
            [Release(AlbumId, "Album", tracks: [("a", 180), ("b", 200), ("c", 220)])]);

        // Coverage is one slot of three, which no rung admits at two files.
        Assert.All(result, a => Assert.Equal(ReleaseAttributionOutcome.NoConfidentFit, a.Outcome));
    }

    /// <summary>
    /// The same inputs must always produce the same answer, whatever order they
    /// arrive in — a rerun that changes its mind is worse than one that is wrong.
    /// </summary>
    [Fact]
    public void TheAnswerDoesNotDependOnTheOrderTheCandidatesArriveIn()
    {
        var files = Library(("a", 180), ("b", 200), ("c", 220));

        var tracks = new[] { ("a", 180), ("b", 200), ("c", 220) };

        MusicBrainzRelease[] candidates =
        [
            Release(VinylId, "Album", tracks),
            Release(AlbumId, "Album", tracks),
            Release(RemasterId, "Album", tracks),
        ];

        var forwards = ReleaseAttribution.Assign(files, candidates);
        var backwards = ReleaseAttribution.Assign(files, [.. candidates.Reverse()]);

        Assert.Equal(
            forwards.Select(a => (a.File, a.Release, a.Outcome)),
            backwards.Select(a => (a.File, a.Release, a.Outcome)));
    }

    /// <summary>
    /// A release MusicBrainz gives no track lengths for is judged on coverage
    /// alone rather than treated as a perfect match.
    /// </summary>
    [Fact]
    public void AReleaseWithNoPrintedLengthsIsNotCreditedWithPerfectAgreement()
    {
        var files = Library(("a", 180), ("b", 200), ("c", 220));

        var undated = Release(AlbumId, "Album", tracks: [("a", null), ("b", null), ("c", null)]);
        var exact = Release(RemasterId, "Album", tracks: [("a", 180), ("b", 200), ("c", 220)]);

        var result = ReleaseAttribution.Assign(files, [undated, exact]);

        // Both cover everything; only one of them has shown any evidence.
        Assert.All(result, a => Assert.Equal(RemasterId, a.Release));
        Assert.All(result, a => Assert.Equal(ReleaseAttributionOutcome.Attributed, a.Outcome));
    }

    private static List<AttributionFile> Library(params (string Name, int Seconds)[] files) =>
        [.. files.Select(file => new AttributionFile(
            File(file.Name),
            Recording(file.Name),
            TimeSpan.FromSeconds(file.Seconds)))];

    private static MusicBrainzRelease Release(
        Mbid id,
        string title,
        (string Name, int? Seconds)[] tracks,
        int? year = null,
        string status = "Official",
        Mbid? group = null) =>
        new(
            Id: id,
            Title: title,
            ReleasedOn: year is { } known ? new ReleaseDate(known, null, null) : null,
            Country: null,
            Status: status,
            Barcode: null,
            Labels: [],
            ReleaseGroupId: group ?? GroupId,
            ReleaseGroupTitle: title,
            PrimaryType: "Album",
            SecondaryTypes: [],
            Credits: [],
            Tracks: [.. tracks.Select((track, index) =>
                Track(1, index + 1, track.Name, track.Seconds))]);

    private static MusicBrainzRelease Release(
        Mbid id,
        string title,
        (string Name, int Seconds)[] tracks,
        int? year = null,
        string status = "Official",
        Mbid? group = null) =>
        Release(
            id,
            title,
            [.. tracks.Select(track => (track.Name, (int?)track.Seconds))],
            year,
            status,
            group);

    private static MusicBrainzTrack Track(int disc, int position, string name, int? seconds) =>
        new(
            DiscNumber: disc,
            Position: position,
            Number: position.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title: name,
            Length: seconds is { } known ? TimeSpan.FromSeconds(known) : null,
            RecordingId: Recording(name),
            Credits: []);

    /// <summary>Tracks the library does not hold, to dilute a candidate's coverage.</summary>
    private static (string Name, int? Seconds)[] Filler(int count) =>
        [.. Enumerable.Range(0, count).Select(index => ($"filler-{index}", (int?)200))];

    /// <summary>A stable id per name, so a test can name a file and find it again.</summary>
    private static MediaFileId File(string name) => new(Deterministic(name, 0xF1));

    private static Mbid Recording(string name) => new(Deterministic(name, 0x2E));

    private static Guid Deterministic(string name, byte salt)
    {
        var bytes = new byte[16];
        bytes[0] = salt;

        for (var index = 0; index < name.Length && index < 15; index++)
        {
            bytes[index + 1] = (byte)name[index];
        }

        return new Guid(bytes);
    }

    private static Mbid Mb(string value) => new(Guid.Parse(value));
}
