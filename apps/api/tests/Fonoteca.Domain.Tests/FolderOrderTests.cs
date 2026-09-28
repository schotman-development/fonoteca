using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;
using static Fonoteca.Domain.Tests.AlbumFolderFixtures;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The order a folder's files run in, against the order the album's editions print.
/// </summary>
/// <remarks>
/// Measured on the target library before this existed: of 601 releases, file
/// order and printed order agreed on 568. Of the 33 that did not, 20 were file
/// names with no number or with numbers not zero-padded, 10 were files filed
/// under another album entirely, and three were genuine.
/// </remarks>
public sealed class FolderOrderTests
{
    private static readonly MusicBrainzRelease Album =
        Release("album", Tracks(("a", 180_437), ("b", 240_120), ("c", 200_880)));

    [Fact]
    public void TaggedFilesInTheEditionsOrderAreCorroborated()
    {
        FolderFile[] files =
        [
            File("c", 0, [Album], track: 3),
            File("a", 0, [Album], track: 1),
            File("b", 0, [Album], track: 2),
        ];

        var (outcome, order) = FolderOrder.Check(files, [Album], gatherComplete: true);

        Assert.Equal(FolderOrderOutcome.Corroborated, outcome);
        Assert.Equal([FileId("a"), FileId("b"), FileId("c")], order);
    }

    [Fact]
    public void NumberedFileNamesRunTwoBeforeTenAndDiscByDisc()
    {
        var box = Release("box", Tracks([.. Enumerable.Range(1, 12).Select(n => ($"t{n}", 200_437))]));
        var files = new[] { 10, 2, 1, 11, 12 }
            .Select(n => File($"t{n}", 0, [box], path: n <= 10 ? $"X/Box/CD 1/{n} t.flac" : $"X/Box/CD 2/{n - 10} t.flac"))
            .ToList();

        var (outcome, order) = FolderOrder.Check(files, [box], gatherComplete: true);

        Assert.Equal(FolderOrderOutcome.Corroborated, outcome);
        Assert.Equal(
            ["t1", "t2", "t10", "t11", "t12"],
            order.Select(id => files.Single(file => file.Id == id).Recording!.Value).Select(Name));

        string Name(Mbid recording) => new[] { 1, 2, 10, 11, 12 }.Select(n => $"t{n}").Single(n => Recording(n) == recording);
    }

    [Fact]
    public void ExtraTracksOnEitherSideAreIgnored()
    {
        // The deluxe's bonus tracks and a file no edition prints say nothing about
        // the order of what the two share.
        var deluxe = Release("deluxe", Tracks(("a", 1), ("bonus", 1), ("b", 1), ("c", 1)));
        FolderFile[] files =
        [
            File("a", 0, [deluxe], track: 1),
            File("b", 0, [deluxe], track: 2),
            File("stray", 0, [deluxe], track: 3),
            File("c", 0, [deluxe], track: 4),
        ];

        Assert.Equal(FolderOrderOutcome.Corroborated, FolderOrder.Check(files, [deluxe], gatherComplete: true).Outcome);
    }

    [Fact]
    public void ATaggedOrderEveryEditionReversesIsContradictedButOnlyFromTagsAndACompleteGather()
    {
        FolderFile[] tagged =
        [
            File("a", 0, [Album], track: 2),
            File("b", 0, [Album], track: 1),
            File("c", 0, [Album], track: 3),
        ];

        Assert.Equal(FolderOrderOutcome.Contradicted, FolderOrder.Check(tagged, [Album], gatherComplete: true).Outcome);

        // A gather cut off at its cap has not seen every edition.
        Assert.Equal(FolderOrderOutcome.Uncorroborated, FolderOrder.Check(tagged, [Album], gatherComplete: false).Outcome);

        // A file name is a weaker claim than a tag and never contradicts.
        FolderFile[] named =
        [
            File("a", 0, [Album], path: "X/Y/2 a.flac"),
            File("b", 0, [Album], path: "X/Y/1 b.flac"),
            File("c", 0, [Album], path: "X/Y/3 c.flac"),
        ];

        Assert.Equal(FolderOrderOutcome.Uncorroborated, FolderOrder.Check(named, [Album], gatherComplete: true).Outcome);
    }

    [Fact]
    public void EditionsThatDisagreeLeaveNothingToContradict()
    {
        // A UK and a US edition that swap two songs: a folder in either order is
        // corroborated by one of them. Only a pair both of them print the other
        // way round contradicts it.
        var us = Release("us", Tracks(("b", 1), ("a", 1), ("c", 1)));
        FolderFile[] files =
        [
            File("a", 0, [Album, us], track: 1),
            File("b", 0, [Album, us], track: 2),
            File("c", 0, [Album, us], track: 3),
        ];

        Assert.Equal(FolderOrderOutcome.Corroborated, FolderOrder.Check(files, [Album, us], gatherComplete: true).Outcome);

        FolderFile[] reversed =
        [
            File("a", 0, [Album, us], track: 3),
            File("b", 0, [Album, us], track: 2),
            File("c", 0, [Album, us], track: 1),
        ];

        Assert.Equal(FolderOrderOutcome.Contradicted, FolderOrder.Check(reversed, [Album, us], gatherComplete: true).Outcome);
    }

    [Fact]
    public void FilesWithNoOrderOfTheirOwnTakeTheOneEveryEditionAgreesOn()
    {
        FolderFile[] files =
        [
            File("c", 0, [Album], path: "X/Y/The C.flac"),
            File("a", 0, [Album], path: "X/Y/The A.flac"),
            File("b", 0, [Album], path: "X/Y/The B.flac"),
        ];

        var (outcome, order) = FolderOrder.Check(files, [Album], gatherComplete: true);

        Assert.Equal(FolderOrderOutcome.TakenFromEditions, outcome);
        Assert.Equal([FileId("X/Y/The A.flac"), FileId("X/Y/The B.flac"), FileId("X/Y/The C.flac")], order);

        var us = Release("us", Tracks(("b", 1), ("a", 1), ("c", 1)));
        var (disagreeing, none) = FolderOrder.Check(files, [Album, us], gatherComplete: true);

        Assert.Equal(FolderOrderOutcome.Uncorroborated, disagreeing);
        Assert.Empty(none);
    }

    [Fact]
    public void TwoFilesTaggedOnOnePlaceAreNotAnOrder()
    {
        // Two rips, or a tagger's mistake: the names are asked instead.
        FolderFile[] files =
        [
            File("a", 0, [Album], path: "X/Y/1 a.flac", track: 1),
            File("b", 0, [Album], path: "X/Y/2 b.flac", track: 1),
            File("c", 0, [Album], path: "X/Y/3 c.flac", track: 3),
        ];

        var (outcome, order) = FolderOrder.Check(files, [Album], gatherComplete: true);

        Assert.Equal(FolderOrderOutcome.Corroborated, outcome);
        Assert.Equal(3, order.Count);
    }
}
