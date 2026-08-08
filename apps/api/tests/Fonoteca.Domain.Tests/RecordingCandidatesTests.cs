using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The rule that turns an AcoustID answer into "what recording is this".
/// </summary>
/// <remarks>
/// No network, no adapter, no JSON — which is the whole reason it lives in the
/// domain. This is the decision that determines what a file becomes, and it has
/// to be arguable on its own.
/// </remarks>
public sealed class RecordingCandidatesTests
{
    private static readonly Mbid Original = Mb("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff");
    private static readonly Mbid Remaster = Mb("38035858-f990-4fbb-b3b2-f2f8b958eeba");

    [Fact]
    public void NothingInMeansNothingOut() =>
        Assert.Empty(RecordingCandidates.From([]));

    [Fact]
    public void AClusterWithNoRecordingsContributesNoCandidates() =>
        Assert.Empty(RecordingCandidates.From([Cluster(1.0)]));

    /// <summary>
    /// The case that makes this a rule rather than an indexer. Two AcoustID
    /// clusters, one recording: a lossless rip and a lossy rip of the same
    /// track that were never merged.
    /// </summary>
    [Fact]
    public void OneRecordingUnderTwoClustersCollapsesToOneCandidate()
    {
        var candidates = RecordingCandidates.From(
        [
            Cluster(0.87, (Original, 653)),
            Cluster(1.0, (Original, 25)),
        ]);

        var candidate = Assert.Single(candidates);

        Assert.Equal(Original, candidate.Id);

        // Best acoustic match, not the average: a second, weaker cluster is not
        // evidence against the strong one.
        Assert.Equal(1.0, candidate.Score);

        // Summed, not maximised: two clusters are two separate populations of
        // submitters, so that really is more agreement.
        Assert.Equal(678, candidate.Sources);
    }

    /// <summary>
    /// The trap. <c>results[0].recordings[0]</c> is the remaster here, and it
    /// is the wrong answer — which is exactly what a client that reads the
    /// first element writes into the file's tags.
    /// </summary>
    [Fact]
    public void ScoreLeadsSoTheStrongestAcousticMatchWins()
    {
        var candidates = RecordingCandidates.From(
        [
            Cluster(0.72, (Remaster, 9_000)),
            Cluster(0.99, (Original, 12)),
        ]);

        Assert.Equal(Original, candidates[0].Id);
        Assert.Equal(Remaster, candidates[1].Id);
    }

    [Fact]
    public void SourcesBreakATieOnScore()
    {
        var candidates = RecordingCandidates.From([Cluster(0.9, (Original, 4), (Remaster, 400))]);

        Assert.Equal(Remaster, candidates[0].Id);
        Assert.Equal(Original, candidates[1].Id);
    }

    /// <summary>
    /// Two identical candidates must not order themselves by whatever the
    /// dictionary felt like. A rerun of the same identification picking a
    /// different recording is the kind of bug nobody reproduces.
    /// </summary>
    [Fact]
    public void TheOrderIsAFunctionOfTheInputAlone()
    {
        var forwards = RecordingCandidates.From(
            [Cluster(0.9, (Original, 10)), Cluster(0.9, (Remaster, 10))]);

        var backwards = RecordingCandidates.From(
            [Cluster(0.9, (Remaster, 10)), Cluster(0.9, (Original, 10))]);

        Assert.Equal(forwards.Select(c => c.Id), backwards.Select(c => c.Id));
    }

    /// <summary>
    /// One cluster can name several recordings — people who disagreed about
    /// whether this is the album cut or the single edit. Both are candidates,
    /// with the same acoustic evidence behind them.
    /// </summary>
    [Fact]
    public void OneClusterNamingTwoRecordingsProducesTwoCandidates()
    {
        var candidates = RecordingCandidates.From([Cluster(0.95, (Original, 30), (Remaster, 7))]);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(0.95, candidate.Score));
    }

    [Fact]
    public void NullIsRefusedRatherThanTreatedAsEmpty() =>
        Assert.Throws<ArgumentNullException>(() => RecordingCandidates.From(null!));

    private static AcoustIdMatch Cluster(double score, params (Mbid Id, int Sources)[] recordings) =>
        new(
            Guid.CreateVersion7(),
            score,
            [.. recordings.Select(r => new AcoustIdRecordingRef(r.Id, r.Sources))]);

    private static Mbid Mb(string value) => new(Guid.Parse(value));
}
