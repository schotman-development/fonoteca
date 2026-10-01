using System.Text.RegularExpressions;
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
/// the original is a pure loss of provenance — unless the download also carries
/// tracks the album is missing, which is how an incomplete album is completed.</item>
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
public static partial class UpgradeReplacement
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

        // The worst of what arrived against the best of what is here. A hi-res
        // album with one CD-quality bonus track does not get to retire a
        // CD-quality album on the strength of its other eleven.
        return Decide(held, heldTracks, Worst(arrived), arrived.Count);
    }

    /// <summary>Is what the shop offers worth downloading to replace the album at hand?</summary>
    /// <remarks>
    /// Asked <i>before</i> the download, on the shop's own description, so an
    /// album that would only be refused afterwards costs no transfer. It is a
    /// gate on spending the download, never permission to replace:
    /// <see cref="Check"/> still decides on the files that actually arrive,
    /// because what a shop advertises is a ceiling.
    /// </remarks>
    /// <param name="offered">The best encoding the shop will serve for this album.</param>
    /// <param name="offeredTracks">Tracks the shop will actually serve.</param>
    public static ReplacementVerdict Offer(
        IReadOnlyList<AudioQuality?> held,
        int heldTracks,
        AudioQuality offered,
        int offeredTracks)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(offered);

        return offeredTracks == 0
            ? ReplacementVerdict.NothingArrived
            : Decide(held, heldTracks, offered, offeredTracks);
    }

    private static ReplacementVerdict Decide(
        IReadOnlyList<AudioQuality?> held,
        int heldTracks,
        AudioQuality candidate,
        int candidateTracks)
    {
        // Tracks rather than files on the left, because five encodings of one
        // song are one track of the album — the same reason `held` counts
        // distinct tracks everywhere else in the catalogue.
        //
        // Counted, never matched by title: the album is the release group, and
        // the reasons to replace it are quality and missing tracks — the owner's
        // choice. A shop titles the same track "The Chain (Album Version)", so
        // matching titles refused most real upgrades. What that costs: a held
        // track the shop's album lacks goes to the archive with the rest.
        if (candidateTracks < heldTracks) return ReplacementVerdict.Incomplete;

        var best = Best(held);

        if (best is null) return ReplacementVerdict.NotMeasured;

        var byFormat = CompareFormat(candidate, best);

        // Better, or as good with tracks the album is missing: completing an
        // album from the Incomplete list is an upgrade at the same quality.
        return byFormat > 0 || (byFormat == 0 && candidateTracks > heldTracks)
            ? ReplacementVerdict.Replace
            : ReplacementVerdict.NotBetter;
    }

    /// <summary>A shop's album title with its edition notes taken off: "Rumours (Deluxe Edition)" is Rumours.</summary>
    public static string WithoutEditionNote(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return EditionNote().Replace(title, string.Empty);
    }

    /// <summary>
    /// A note naming an edition of the same record, and nothing else: "(2004
    /// Remaster)", " - Remastered 2011", "(Deluxe Edition)", "(40th Anniversary
    /// Edition)", "[Bonus Tracks]".
    /// </summary>
    /// <remarks>
    /// Nothing else, because "Rumours (Live)" is another record, and so is an
    /// acoustic or a demo one: MusicBrainz files those as release groups of
    /// their own.
    /// </remarks>
    [GeneratedRegex(
        @"\s*(?:\(|\[|\s-\s)\s*(?:\d{4}\s+)?(?:\d+(?:st|nd|rd|th)\s+)?(?:super\s+)?(?:digital(?:ly)?\s+)?"
        + @"(?:(?:remaster(?:ed)?|deluxe|expanded|anniversary|special|collector'?s|legacy|bonus\s+tracks?)\s*)+"
        + @"(?:\d{4}\s*)?(?:(?:version|edition)\s*)?(?:\)|\]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EditionNote();

    /// <summary><see cref="AudioQuality.Compare"/> without its lossless size tie-break.</summary>
    /// <remarks>
    /// That tie-break prefers the smaller of two identical-format files, which
    /// is right for choosing which duplicate to keep and wrong here: a smaller
    /// encode of the same 16/44.1 master is not an upgrade, and a shop's
    /// description states no size at all.
    /// </remarks>
    private static int CompareFormat(AudioQuality left, AudioQuality right) =>
        left.IsLossless
        && right.IsLossless
        && left.Tier == right.Tier
        && (left.BitDepth ?? 0) == (right.BitDepth ?? 0)
        && left.SampleRateHz == right.SampleRateHz
            ? 0
            : AudioQuality.Compare(left, right);

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

    /// <summary>
    /// The folder's files cannot show they are this album — filed under
    /// another, or under none. Asked again with a person's confirmation, the
    /// offer is judged.
    /// </summary>
    Unconfirmed,

    /// <summary>
    /// Nothing held is this album by barcode or billing, and one album of the
    /// same title is: a person says whether to replace it, or to download this
    /// as an album of its own.
    /// </summary>
    SameTitle,
}
