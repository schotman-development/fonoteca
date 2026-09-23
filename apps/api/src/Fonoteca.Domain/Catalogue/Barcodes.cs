namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// A barcode in the one form two catalogues can be compared in.
/// </summary>
/// <remarks>
/// <b>Leading zeros are a formatting difference, not a different record.</b> The
/// same album is a 12-digit UPC in one catalogue and the 13-digit EAN of it —
/// the same digits behind a <c>0</c> — in the other, and MusicBrainz holds both
/// shapes. Compared literally, the key that was supposed to make this safe fails
/// on exactly the releases it was reached for.
///
/// Non-digits go for the same reason: a barcode typed with spaces or hyphens is
/// the same barcode. Anything left holding no digits is not one.
///
/// <b>One home, because two layers now need it.</b> This began as a private
/// method in <c>QobuzCovers</c>, where it was written and paid for; the
/// discography shelf then reached for the same comparison between the same two
/// catalogues — a shop's UPC against <c>Release.Barcode</c> — and a second copy
/// is how one of them stops folding a 13-digit EAN a release later. It lives in
/// the domain rather than beside either caller because a barcode is a fact about
/// a record, not about a provider.
/// </remarks>
public static class Barcodes
{
    /// <summary>The comparable form, or null where there is no barcode in it.</summary>
    public static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digits = new string([.. value.Where(char.IsAsciiDigit)]).TrimStart('0');

        return digits.Length == 0 ? null : digits;
    }
}
