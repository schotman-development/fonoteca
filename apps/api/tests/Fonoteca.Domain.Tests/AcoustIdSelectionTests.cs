using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The rule that decides what gets written into somebody's music files.
/// </summary>
/// <remarks>
/// Pure, and tested with lists rather than a network, because it is the one
/// piece of this feature whose mistakes are not recoverable by rerunning. A file
/// left untagged costs another pass; a file tagged with the wrong identifier is
/// believed by every later pass and by every other tool that reads it.
/// </remarks>
public sealed class AcoustIdSelectionTests
{
    [Fact]
    public void AClearWinnerIsChosen()
    {
        var choice = AcoustIdSelection.Choose([Match(0.97), Match(0.42)]);

        Assert.Equal(AcoustIdChoiceReason.Confident, choice.Reason);
        Assert.True(choice.IsConfident);
        Assert.NotNull(choice.Value);
        Assert.Equal(0.97, choice.Score);
    }

    [Fact]
    public void ASingleStrongMatchNeedsNoRunnerUpToBeatAtAll()
    {
        var choice = AcoustIdSelection.Choose([Match(0.93)]);

        Assert.Equal(AcoustIdChoiceReason.Confident, choice.Reason);
    }

    /// <summary>
    /// Audio nobody has ever submitted. Ordinary, and not an error.
    /// </summary>
    /// <remarks>
    /// Distinguished from a weak match on purpose: this file can usefully be
    /// re-asked about in a year, when someone else may have submitted it, while a
    /// file that matched badly will keep matching badly.
    /// </remarks>
    [Fact]
    public void NothingMatchingIsNoMatchRatherThanAnError()
    {
        var choice = AcoustIdSelection.Choose([]);

        Assert.Equal(AcoustIdChoiceReason.NoMatch, choice.Reason);
        Assert.Null(choice.Value);
        Assert.Null(choice.Score);
    }

    [Fact]
    public void AWeakBestMatchIsNotWrittenToAnyFile()
    {
        var choice = AcoustIdSelection.Choose([Match(0.62), Match(0.10)]);

        Assert.Equal(AcoustIdChoiceReason.BelowThreshold, choice.Reason);
        Assert.Null(choice.Value);

        // The score survives even though nothing was chosen: "we looked and the
        // best was 0.62" and "we looked and found nothing" are different facts.
        Assert.Equal(0.62, choice.Score);
    }

    /// <summary>
    /// The case a naive client gets wrong, and the reason a margin exists at all.
    /// </summary>
    /// <remarks>
    /// Two clusters at 0.95 and 0.94 naming <i>different recordings</i> are not
    /// one good answer and one bad one. They are AcoustID reporting two
    /// candidates and no opinion — typically a live take against a studio one.
    /// Taking the higher is a coin flip presented as a decision, and it is how a
    /// remaster gets identified as the original.
    /// </remarks>
    [Fact]
    public void TwoClustersNamingDifferentRecordingsAreAmbiguousRatherThanAGuess()
    {
        var choice = AcoustIdSelection.Choose([Match(0.95, Studio), Match(0.94, Live)]);

        Assert.Equal(AcoustIdChoiceReason.Ambiguous, choice.Reason);
        Assert.Null(choice.Value);
        Assert.Equal(0.95, choice.Score);
    }

    /// <summary>
    /// The bug this rule was rewritten for: 925 files withheld over nothing.
    /// </summary>
    /// <remarks>
    /// AcoustID clusters fingerprints, so one recording routinely spans several
    /// clusters — a lossless rip and a 128kbps rip that were never merged. The
    /// old rule compared cluster scores and read that split as a disagreement,
    /// which withheld 97% of everything it ever called ambiguous in the author's
    /// library. Two clusters naming the same recording are one answer arriving
    /// twice.
    /// </remarks>
    [Fact]
    public void TwoClustersNamingTheSameRecordingAreOneAnswerArrivingTwice()
    {
        var choice = AcoustIdSelection.Choose([Match(0.9575, Studio), Match(0.9391, Studio)]);

        Assert.Equal(AcoustIdChoiceReason.Confident, choice.Reason);
        Assert.Equal(0.9575, choice.Score);
    }

    /// <summary>
    /// The winner is the cluster, even when a rival has more submissions behind it.
    /// </summary>
    [Fact]
    public void TheHigherScoringClusterIsTheOneWrittenDown()
    {
        var best = Match(0.9575, (Studio, 210));
        var also = Match(0.9391, (Studio, 8961));

        var choice = AcoustIdSelection.Choose([best, also]);

        Assert.Equal(new AcoustId(best.AcoustId), choice.Value);
    }

    /// <summary>
    /// A cluster nobody has linked to MusicBrainz cannot contradict one that is.
    /// </summary>
    /// <remarks>
    /// It asserts nothing about what the audio is, so treating it as a rival
    /// withholds a file on the strength of an absence. 46 files in the author's
    /// library turned on this alone, and every one resolved to the recording its
    /// own filename already claimed.
    /// </remarks>
    [Fact]
    public void AnUnlinkedClusterDoesNotCompeteWithALinkedOne()
    {
        var choice = AcoustIdSelection.Choose([Match(0.9628, Studio), Match(0.9326)]);

        Assert.Equal(AcoustIdChoiceReason.Confident, choice.Reason);
    }

    /// <summary>
    /// Unless there is nothing to reason with, in which case the plain margin applies.
    /// </summary>
    /// <remarks>
    /// Two unlinked clusters give no basis for saying they are the same audio or
    /// different audio. The conservative answer is the right degradation: this
    /// costs nothing in practice — no file in the author's library depends on
    /// it — and refusing to guess is the whole posture of this class.
    /// </remarks>
    [Fact]
    public void TwoUnlinkedClustersTooCloseToCallStayAmbiguous()
    {
        var choice = AcoustIdSelection.Choose([Match(0.95), Match(0.94)]);

        Assert.Equal(AcoustIdChoiceReason.Ambiguous, choice.Reason);
    }

    /// <summary>
    /// Submissions rank recordings within a cluster; they never overrule a score.
    /// </summary>
    /// <remarks>
    /// Letting a better-supported cluster outvote a near-tied one recovers a
    /// further 36 files and decides live-against-studio by popularity — it tags
    /// a track from an album called <i>Live</i> with the studio recording,
    /// because that one has more submissions. Measured, rejected, and pinned
    /// here so it is not reintroduced as an obvious improvement.
    /// </remarks>
    [Fact]
    public void AWellSupportedRivalIsStillARival()
    {
        var choice = AcoustIdSelection.Choose(
            [Match(0.9653, (Studio, 73)), Match(0.9605, (Live, 6))]);

        Assert.Equal(AcoustIdChoiceReason.Ambiguous, choice.Reason);
    }

    /// <summary>
    /// A rival outside the margin is not a rival, whatever it names.
    /// </summary>
    [Fact]
    public void ADistantClusterNamingSomethingElseIsIrrelevant()
    {
        var choice = AcoustIdSelection.Choose([Match(0.98, Studio), Match(0.62, Live)]);

        Assert.Equal(AcoustIdChoiceReason.Confident, choice.Reason);
    }

    /// <summary>
    /// Which recording a cluster "means" is the one most people submitted it as.
    /// </summary>
    /// <remarks>
    /// Clusters are often linked to several recordings by submitters who
    /// disagreed, and the tail of that list is somebody's one-off mistagging.
    /// Comparing the tails would make almost everything look contested; both
    /// clusters here are dominated by the studio recording despite carrying a
    /// stray link each.
    /// </remarks>
    [Fact]
    public void TheRecordingAClusterMeansIsTheOneMostPeopleSubmittedItAs()
    {
        var choice = AcoustIdSelection.Choose(
        [
            Match(0.96, (Studio, 4401), (Live, 2)),
            Match(0.95, (Live, 1), (Studio, 7435)),
        ]);

        Assert.Equal(AcoustIdChoiceReason.Confident, choice.Reason);
    }

    [Fact]
    public void AStrongMatchIsStillChosenWhenTheRunnerUpIsFarBehind()
    {
        var choice = AcoustIdSelection.Choose([Match(0.95), Match(0.89)]);

        Assert.Equal(AcoustIdChoiceReason.Confident, choice.Reason);
    }

    [Theory]
    [InlineData(0.90, true)]
    [InlineData(0.8999, false)]
    public void TheThresholdIsInclusive(double score, bool expectedConfident)
    {
        var choice = AcoustIdSelection.Choose([Match(score)]);

        Assert.Equal(expectedConfident, choice.IsConfident);
    }

    [Fact]
    public void ALibraryOfBootlegsCanLowerTheBarDeliberately()
    {
        var matches = new[] { Match(0.72) };

        Assert.False(AcoustIdSelection.Choose(matches).IsConfident);
        Assert.True(AcoustIdSelection.Choose(matches, minimumScore: 0.70).IsConfident);
    }

    /// <summary>
    /// Order in must not change the answer out.
    /// </summary>
    /// <remarks>
    /// The client does return best-first, and a rule that silently relies on its
    /// caller having sorted is a rule that breaks the day somebody hands it a
    /// set. Same discipline as <c>RecordingCandidates</c>, for the same reason: a
    /// rerun of the same identification must not be able to change its mind.
    /// </remarks>
    [Fact]
    public void TheAnswerIsAFunctionOfTheMatchesAloneAndNotTheirOrder()
    {
        var a = Match(0.97);
        var b = Match(0.40);
        var c = Match(0.66);

        var forwards = AcoustIdSelection.Choose([a, b, c]);
        var backwards = AcoustIdSelection.Choose([c, b, a]);

        Assert.Equal(forwards.Value, backwards.Value);
        Assert.Equal(forwards.Reason, backwards.Reason);
    }

    /// <summary>
    /// Two clusters tied exactly are still ambiguous, and still deterministic.
    /// </summary>
    [Fact]
    public void AnExactTieIsAmbiguousHoweverItArrives()
    {
        var a = Match(0.99, Studio);
        var b = Match(0.99, Live);

        Assert.Equal(AcoustIdChoiceReason.Ambiguous, AcoustIdSelection.Choose([a, b]).Reason);
        Assert.Equal(AcoustIdChoiceReason.Ambiguous, AcoustIdSelection.Choose([b, a]).Reason);
    }

    /// <summary>
    /// Determinism where the answer is a cluster and not just a reason.
    /// </summary>
    /// <remarks>
    /// The dominant-recording comparison sorts inside each cluster too, so a
    /// cluster whose links arrive in a different order must still mean the same
    /// recording. Without the tiebreak that ordering comes from the source data,
    /// and a rerun of the same identification could change its mind.
    /// </remarks>
    [Fact]
    public void TheSameClustersAlwaysChooseTheSameAnswerWhateverTheOrder()
    {
        var a = Match(0.96, (Studio, 4401), (Live, 2));
        var b = Match(0.95, (Live, 1), (Studio, 7435));

        var forwards = AcoustIdSelection.Choose([a, b]);
        var backwards = AcoustIdSelection.Choose([b, a]);

        Assert.Equal(forwards.Reason, backwards.Reason);
        Assert.Equal(forwards.Value, backwards.Value);
    }

    /// <summary>
    /// A cluster AcoustID knows but nobody has linked to MusicBrainz still counts.
    /// </summary>
    /// <remarks>
    /// This pass writes the cluster identifier and asks MusicBrainz nothing, so
    /// an empty recording list is irrelevant to it. It would matter to
    /// enrichment, later, which is a different rule.
    /// </remarks>
    [Fact]
    public void AClusterWithNoMusicBrainzLinkIsStillAConfidentAnswer()
    {
        var choice = AcoustIdSelection.Choose([new AcoustIdMatch(Guid.NewGuid(), 0.96, [])]);

        Assert.Equal(AcoustIdChoiceReason.Confident, choice.Reason);
    }

    [Fact]
    public void NullMatchesIsACallerBug()
    {
        Assert.Throws<ArgumentNullException>(() => AcoustIdSelection.Choose(null!));
    }

    /// <summary>Two recordings that stand for "the same track" and "a different one".</summary>
    private static readonly Mbid Studio = new(new Guid("cf61eb88-0000-0000-0000-000000000001"));
    private static readonly Mbid Live = new(new Guid("cf61eb88-0000-0000-0000-000000000002"));

    /// <summary>A cluster linked to nothing, which is what most of these tests want.</summary>
    private static AcoustIdMatch Match(double score) => new(Guid.NewGuid(), score, []);

    private static AcoustIdMatch Match(double score, Mbid recording) =>
        Match(score, (recording, 100));

    private static AcoustIdMatch Match(double score, params (Mbid Id, int Sources)[] recordings) =>
        new(Guid.NewGuid(), score, [.. recordings.Select(r => new AcoustIdRecordingRef(r.Id, r.Sources))]);
}
