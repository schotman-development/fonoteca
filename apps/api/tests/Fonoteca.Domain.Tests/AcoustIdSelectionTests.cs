using Fonoteca.Domain.Abstractions;
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
    /// Two clusters at 0.95 and 0.94 are not one good answer and one bad one.
    /// They are AcoustID reporting two candidates and no opinion — typically a
    /// track that appears on both an album and a compilation. Taking the higher
    /// one is a coin flip presented as a decision, and it is how a remaster gets
    /// identified as the original.
    /// </remarks>
    [Fact]
    public void TwoClustersTooCloseToCallAreAmbiguousRatherThanAGuess()
    {
        var choice = AcoustIdSelection.Choose([Match(0.95), Match(0.94)]);

        Assert.Equal(AcoustIdChoiceReason.Ambiguous, choice.Reason);
        Assert.Null(choice.Value);
        Assert.Equal(0.95, choice.Score);
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
        var a = Match(0.99);
        var b = Match(0.99);

        Assert.Equal(AcoustIdChoiceReason.Ambiguous, AcoustIdSelection.Choose([a, b]).Reason);
        Assert.Equal(AcoustIdChoiceReason.Ambiguous, AcoustIdSelection.Choose([b, a]).Reason);
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

    private static AcoustIdMatch Match(double score) => new(Guid.NewGuid(), score, []);
}
