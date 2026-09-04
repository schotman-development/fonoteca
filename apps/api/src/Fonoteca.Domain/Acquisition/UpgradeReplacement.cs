using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Acquisition;

/// <summary>
/// Whether a download that has already landed may retire the album it was meant
/// to replace.
/// </summary>
/// <remarks>
/// <b>The one rule in this project that stands between a person's click and
/// their music being taken away</b>, so it is pure, it is asked <i>after</i> the
/// new files are on disk, and it refuses on anything it cannot establish.
///
/// Asked after rather than before, and that ordering is the whole design. What
/// Qobuz advertises on an album page is a ceiling — "up to 24-bit / 192 kHz" —
/// and what arrives is whatever that release actually has. Deciding on the
/// advertisement means deleting a CD rip because a better one was <i>offered</i>;
/// deciding on the files means deleting it because a better one is
/// <i>already here</i>. Only the second is a fact.
///
/// The three refusals are three different mistakes, and none of them is rare:
///
/// <list type="bullet">
/// <item><b>Incomplete.</b> Licensing gaps are ordinary on compilations, and
/// this project's own download path reports them as <c>Skipped</c> rather than
/// as failures — eleven of twelve is worth having, and it is <i>not</i> worth
/// replacing twelve with. A replacement that loses a track is a loss dressed as
/// an upgrade, and the track it loses is the one nobody notices for a year.</item>
/// <item><b>Not better.</b> The reason to be strict rather than lenient is that
/// <see cref="AudioQuality.Compare"/> is the same rule the upgrade list ranks
/// with, so "it was on the list" and "it may replace" cannot drift apart. A tie
/// is not an upgrade: re-downloading the CD master you already hold and deleting
/// the original is a pure loss of provenance.</item>
/// <item><b>Nothing to compare against.</b> An album no probe has measured
/// cannot be shown to be worse than anything. That is a gap in the catalogue,
/// not permission — and it is answerable, because the measure pass fills it in.
/// Refusing sends somebody to run that pass; assuming sends them to a restore.</item>
/// </list>
///
/// It compares against the <i>best</i> file the album holds, not the worst. An
/// album with one MP3 among eleven FLACs is on the upgrade list because of the
/// MP3, and replacing all twelve on the strength of that one file is how the
/// eleven get quietly downgraded.
/// </remarks>
public static class UpgradeReplacement
{
    /// <summary>May the album at hand be retired in favour of what just arrived?</summary>
    /// <param name="held">
    /// What each file of the old album measures. Nulls are files nothing has
    /// probed, and are ignored rather than assumed — see the remarks.
    /// </param>
    /// <param name="heldTracks">Distinct tracks the old album holds, not files.</param>
    /// <param name="arrived">What the new files measure, one per downloaded track.</param>
    public static ReplacementVerdict Check(
        IReadOnlyList<AudioQuality?> held,
        int heldTracks,
        IReadOnlyList<AudioQuality> arrived)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(arrived);

        if (arrived.Count == 0) return ReplacementVerdict.NothingArrived;

        // Tracks rather than files on the left, because five encodings of one
        // song are one track of the album — the same reason `held` counts
        // distinct tracks everywhere else in the catalogue.
        if (arrived.Count < heldTracks) return ReplacementVerdict.Incomplete;

        var best = Best(held);

        if (best is null) return ReplacementVerdict.NotMeasured;

        // The worst of what arrived against the best of what is here. A hi-res
        // album with one CD-quality bonus track does not get to retire a
        // CD-quality album on the strength of its other eleven.
        var worst = Worst(arrived);

        return AudioQuality.Compare(worst, best) > 0
            ? ReplacementVerdict.Replace
            : ReplacementVerdict.NotBetter;
    }

    private static AudioQuality? Best(IReadOnlyList<AudioQuality?> qualities)
    {
        AudioQuality? best = null;

        foreach (var quality in qualities)
        {
            if (quality is null) continue;
            if (best is null || AudioQuality.Compare(quality, best) > 0) best = quality;
        }

        return best;
    }

    private static AudioQuality Worst(IReadOnlyList<AudioQuality> qualities)
    {
        var worst = qualities[0];

        foreach (var quality in qualities)
        {
            if (AudioQuality.Compare(quality, worst) < 0) worst = quality;
        }

        return worst;
    }
}

/// <summary>What may be done with the album that was to be replaced.</summary>
/// <remarks>
/// A name on the wire, and one value per reason rather than a boolean with a
/// sentence beside it. A client that has to parse English to tell "you now hold
/// two copies" from "you now hold one" is a client that will get it wrong, and
/// these two outcomes leave the library in states a person acts on differently.
/// </remarks>
public enum ReplacementVerdict
{
    /// <summary>Every check passed. The old files may be retired.</summary>
    Replace,

    /// <summary>No track downloaded. Nothing to replace it with.</summary>
    NothingArrived,

    /// <summary>Fewer tracks than the album holds. Replacing would lose music.</summary>
    Incomplete,

    /// <summary>What arrived is no better than what is here, or is worse.</summary>
    NotBetter,

    /// <summary>Nothing has measured the old album, so nothing can be compared.</summary>
    NotMeasured,

    /// <summary>A track that downloaded could not be measured, so it cannot be ranked.</summary>
    /// <remarks>
    /// Separate from <see cref="NotMeasured"/> because it is a fact about the
    /// <i>new</i> files, and the two send a person to opposite places — one to
    /// the measure pass, the other to a re-download.
    /// </remarks>
    ArrivalNotMeasured,

    /// <summary>The download landed inside the album it was to replace.</summary>
    LandedInside,

    /// <summary>The catalogue holds nothing under that folder, or not what the caller expected.</summary>
    NotHeld,
}
