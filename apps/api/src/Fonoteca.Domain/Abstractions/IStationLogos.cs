namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// An internet radio station's logo, looked up when the station is saved.
/// </summary>
/// <remarks>
/// radio-browser.info is the one source, the owner's choice: OpenSubsonic's
/// radio methods carry no logo, so a station saved from a client arrives
/// without one. <b>Keyed on the stream</b>, which is what makes a station that
/// station: the name only finds the candidates.
///
/// Null is "the source knows no such station, or none with a logo"; a
/// <see cref="ProviderException"/> is "I did not get to find out".
/// </remarks>
public interface IStationLogos
{
    /// <returns>The logo's absolute http or https address, or null.</returns>
    Task<string?> FindAsync(string name, string streamUrl, CancellationToken cancellationToken = default);
}
