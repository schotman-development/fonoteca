using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Identification;

/// <summary>
/// Whether a folder of files is, beyond contradiction, one particular edition.
/// </summary>
/// <remarks>
/// <b>An edition is claimed only where nothing can contradict it.</b> The album is
/// a far safer claim than the pressing: the same recordings in the same order are
/// on the CD, the remaster, the digital release and three regional pressings, and
/// "99% the same" is exactly what a wrong pressing looks like. Measured on the
/// library this was built for, 60 releases MusicBrainz lists as CD held files no
/// CD could have produced, and only 226 of 551 complete releases matched their
/// files within 50 ms on every track. So the bar is proof, and every clause here
/// is something a wrong pressing fails:
///
/// <list type="bullet">
/// <item><b>The track count is the folder's.</b> A folder of ten is not the
/// twelve-track deluxe with two missing, nor the ten-track album with a bonus
/// file beside it — the files are placed, not the edition.</item>
/// <item><b>Every file seats on a track</b> of the edition, one each.</item>
/// <item><b>Every track is within <see cref="Tolerance"/></b> of the length the
/// edition prints — every one, not the mean, which let a fifteen-second
/// difference hide among eleven that matched. A length MusicBrainz does not know
/// proves nothing, and nor does a track list whose lengths are all whole seconds:
/// that is lengths typed from a sleeve or a shop page, not measured from a disc,
/// and it cannot tell two masterings a second apart.</item>
/// <item><b>The audio does not contradict the medium.</b> See
/// <see cref="ContradictsCompactDisc"/>.</item>
/// </list>
///
/// A pure rule: a release and some files in, a seating or nothing out.
/// </remarks>
public static class EditionProof
{
    /// <summary>How far one file's measured length may sit from the edition's printed one.</summary>
    /// <remarks>
    /// A correctly attributed album in the target library sits under 100 ms on
    /// every track; a rip of the exact pressing a disc ID describes lands within
    /// one CD frame (13 ms). The headroom above that is for lossy encoders, whose
    /// decoded length carries tens of milliseconds of padding.
    /// </remarks>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The seating that proves <paramref name="edition"/>, or null when it is not proven.
    /// </summary>
    /// <param name="files">Every file in the folder, identified or not; one without a recording proves nothing.</param>
    public static IReadOnlyList<SlotMatch>? Seat(MusicBrainzRelease edition, IReadOnlyList<FolderFile> files)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0 || edition.Tracks.Count != files.Count) return null;
        if (files.Any(file => file.Recording is null)) return null;

        var lengths = edition.Tracks.Select(track => track.Length).ToList();
        if (lengths.Any(length => length is null)) return null;
        if (lengths.All(length => length!.Value.Ticks % TimeSpan.TicksPerSecond == 0)) return null;

        var fit = ReleaseFit.For(
            edition,
            [.. files.Select(file => new AttributionFile(file.Id, file.Recording!.Value, file.Duration))]);

        if (fit is null || fit.FilesExplained != files.Count) return null;
        if (fit.Matches.Any(match => match.Drift is not { } drift || drift > Tolerance)) return null;

        var byId = files.ToDictionary(file => file.Id);

        foreach (var match in fit.Matches)
        {
            var medium = edition.Media?.FirstOrDefault(disc => disc.Position == match.Slot.DiscNumber);

            if (MayBeCompactDisc(medium?.Format) && ContradictsCompactDisc(byId[match.File].Quality)) return null;
        }

        return fit.Matches;
    }

    /// <summary>
    /// Whether a disc of this format may be a compact disc, whose audio can then be
    /// held against it.
    /// </summary>
    /// <remarks>
    /// An unknown format may be one, which is the side that claims less: a hi-res
    /// file against a disc of no stated format is not proven to be it. The CD
    /// family counts — HDCD, Enhanced CD, Copy Control CD are all 16-bit 44.1 kHz
    /// on the disc. A hybrid SACD does not: its other layer is where a hi-res rip
    /// honestly comes from — unless MusicBrainz lists the medium as that
    /// release's CD layer alone.
    /// </remarks>
    public static bool MayBeCompactDisc(string? format) =>
        format is null
        || format.Contains("(CD layer)", StringComparison.Ordinal)
        || (format.Contains("CD", StringComparison.Ordinal)
            && !format.Contains("SACD", StringComparison.Ordinal));

    /// <summary>
    /// Whether this file's audio could not have come straight off a compact disc.
    /// </summary>
    /// <remarks>
    /// A CD holds 16-bit, 44.1 kHz audio cut into sectors of 588 samples, and a
    /// rip's track boundaries sit on those sectors — so lossless audio at any other
    /// depth or rate is not a rip of one, and 16/44.1 lossless whose length is not
    /// a whole number of sectors is not a straight rip either. That is what a
    /// download cut from the same master looks like, and the one percent that
    /// "99% the same" hides.
    ///
    /// Only ever evidence against. Lossy audio carries encoder padding and proves
    /// nothing either way; an unprobed file has nothing to say; and aligned audio
    /// does not prove a CD, because a download made from the disc's master is
    /// aligned too.
    /// </remarks>
    public static bool ContradictsCompactDisc(AudioQuality? quality)
    {
        if (quality is not { IsLossless: true }) return false;
        if (quality.BitDepth is { } depth && depth != 16) return true;
        if (quality.SampleRateHz != 44_100) return true;
        if (quality.Duration is not { } duration) return false;

        var samples = duration.TotalSeconds * 44_100;
        var whole = Math.Round(samples);

        // Only judged where the stored length is a whole number of samples, as a
        // lossless container's is; anything else is not measurement enough.
        return Math.Abs(samples - whole) < 0.2 && (long)whole % 588 != 0;
    }
}

/// <summary>One file in an album folder, as the folder's rules read it.</summary>
/// <param name="Recording">What identification said it is; null for a file nobody has identified.</param>
/// <param name="Groups">
/// The release groups whose releases MusicBrainz lists this recording on —
/// complete, because it comes from a browse paged to its end, which is what makes
/// "on no edition of this album" a fact rather than a guess.
/// </param>
/// <param name="Linked">Every recording the file's AcoustID cluster is linked to; empty until asked.</param>
/// <param name="Duration">The probe's decoded length where there is one, fpcalc's otherwise.</param>
/// <param name="TaggedDisc">The disc number the file's own tags carry.</param>
/// <param name="TaggedTrack">The track number the file's own tags carry.</param>
public sealed record FolderFile(
    MediaFileId Id,
    string Path,
    Mbid? Recording,
    IReadOnlySet<Mbid> Groups,
    IReadOnlySet<Mbid> Linked,
    TimeSpan? Duration,
    AudioQuality? Quality,
    int? TaggedDisc,
    int? TaggedTrack);
