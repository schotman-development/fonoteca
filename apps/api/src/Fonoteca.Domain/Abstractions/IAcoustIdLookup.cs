using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// "What recording is this audio?", answered from an acoustic fingerprint.
/// </summary>
/// <remarks>
/// The interface names the vendor on purpose, which is the opposite of what
/// this file does for Qobuz and Deezer. Those two are interchangeable sources
/// of the same thing, so an honest abstraction over them exists. AcoustID is
/// not interchangeable with anything: it is the only open fingerprint database,
/// and pretending otherwise would produce an <c>IAudioIdentifier</c> with one
/// implementation, one shape, and a name that hides where the data came from.
///
/// It answers <b>identity only</b> — an AcoustID cluster and the MusicBrainz
/// recordings it maps to. AcoustID will also return titles and artists
/// (<c>meta=recordings</c>), and this deliberately does not ask for them: that
/// metadata is a mirror of MusicBrainz, so taking it here would mean two paths
/// to the same fact, ageing at different rates. Identity comes from here,
/// metadata comes from <see cref="IMusicBrainzCatalogue"/>.
/// </remarks>
public interface IAcoustIdLookup
{
    /// <summary>
    /// Every AcoustID cluster matching this fingerprint, best score first.
    /// </summary>
    /// <returns>
    /// An empty list when nothing matched — the ordinary answer for audio
    /// nobody has ever submitted, not an error.
    /// </returns>
    /// <exception cref="ProviderUnavailableException">The service did not answer.</exception>
    /// <exception cref="ProviderRejectedException">The key or the fingerprint was refused.</exception>
    Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(
        AudioFingerprint fingerprint,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A Chromaprint fingerprint together with the duration it was computed over.
/// </summary>
/// <remarks>
/// The two travel together because AcoustID needs both: the fingerprint covers
/// only the first two minutes of audio by default, so duration is what
/// separates a track from a twelve-minute extended mix that opens identically.
/// Passing them as one value makes it impossible to send a fingerprint from one
/// file with the duration of another, which is a mismatch nothing downstream
/// could detect.
///
/// <see cref="Value"/> is the base64 form <c>fpcalc</c> prints, not raw bytes.
/// </remarks>
public readonly record struct AudioFingerprint(string Value, TimeSpan Duration);

/// <summary>
/// One AcoustID cluster that matched, and the MusicBrainz recordings linked to it.
/// </summary>
/// <remarks>
/// A lookup routinely returns several of these for one file, and the same
/// recording MBID can appear under more than one of them — AcoustID clusters
/// fingerprints, and two encodings of the same track can end up in separate
/// clusters that both point at the same recording. Ranking candidates therefore
/// means collapsing across matches rather than taking the first one;
/// <c>RecordingCandidates</c> is that rule.
/// </remarks>
public sealed record AcoustIdMatch(
    Guid AcoustId,

    /// <summary>How well the fingerprint matched, 0 to 1.</summary>
    double Score,

    IReadOnlyList<AcoustIdRecordingRef> Recordings);

/// <summary>A MusicBrainz recording an AcoustID cluster is linked to.</summary>
public sealed record AcoustIdRecordingRef(
    Mbid Id,

    /// <summary>
    /// How many submissions link this cluster to this recording.
    /// </summary>
    /// <remarks>
    /// The signal a score alone cannot give. A perfect 1.0 match backed by one
    /// submission is one person's tagging, possibly wrong; a 0.95 backed by six
    /// hundred is the consensus of six hundred libraries. Both numbers are kept
    /// because ranking needs both.
    /// </remarks>
    int Sources);
