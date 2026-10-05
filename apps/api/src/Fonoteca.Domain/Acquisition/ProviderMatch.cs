using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Acquisition;

/// <summary>
/// Which MusicBrainz release a shop's album is, where the barcode and the track
/// list leave exactly one answer (ADR 0011).
/// </summary>
/// <remarks>
/// A barcode is not a unique key: two editions can share one, and a box set's
/// barcode is on every disc's release. So the release must also print the
/// shop's track list slot for slot — the same discs and positions, no more and
/// no fewer — and more than one doing so is no answer at all. A wrong release
/// filed confidently is the failure this application exists to avoid; the
/// shop's own album, already filed, is the honest fallback.
/// </remarks>
public static class ProviderMatch
{
    /// <summary>The one release that is the shop's album, or null.</summary>
    /// <param name="barcode">The shop's barcode for the album.</param>
    /// <param name="slots">Every track the shop lists, as disc and position.</param>
    /// <param name="candidates">Releases a barcode search found, with their track lists.</param>
    public static MusicBrainzRelease? Pick(
        string? barcode,
        IReadOnlyCollection<(int Disc, int Position)> slots,
        IEnumerable<MusicBrainzRelease> candidates)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(candidates);

        if (Barcodes.Normalise(barcode) is not { } wanted || slots.Count == 0) return null;

        var shop = slots.ToHashSet();

        var fitting = candidates
            .Where(release => Barcodes.Normalise(release.Barcode) == wanted)
            .Where(release => release.Tracks.Count == shop.Count
                && release.Tracks.Select(track => (track.DiscNumber, track.Position)).ToHashSet().SetEquals(shop))
            .DistinctBy(release => release.Id)
            .ToList();

        return fitting is [var only] ? only : null;
    }
}
