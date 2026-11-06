namespace Fonoteca.Domain.Acquisition;

/// <summary>
/// A shop's word for what an album is, as MusicBrainz's primary and secondary types.
/// </summary>
/// <remarks>
/// Qobuz documents no list. The words are the ones open-source Qobuz clients
/// branch on: <c>single</c> and <c>epSingle</c>, <c>ep</c> and <c>epmini</c>,
/// <c>compilation</c>, <c>live</c>. Anything else, or nothing, is an album,
/// which is what every download was typed before the shop's word was read.
/// Qobuz types some three-track EPs <c>single</c>; that is believed, since the
/// shop is the authority about the album (ADR 0011).
/// </remarks>
public static class ShopReleaseType
{
    private const string Album = "Album";
    private const string SingleType = "Single";
    private const string EpType = "EP";

    /// <summary>The primary type, and the secondary one where the word states it.</summary>
    public static (string Primary, string? Secondary) Of(string? word) =>
        word?.Trim().ToLowerInvariant() switch
        {
            "single" or "epsingle" => (SingleType, null),
            "ep" or "epmini" => (EpType, null),
            "compilation" => (Album, "Compilation"),
            "live" => (Album, "Live"),
            _ => (Album, null),
        };
}
