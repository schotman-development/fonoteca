using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// An internet radio station's logo, looked up when the station is saved.
/// </summary>
/// <remarks>
/// OpenSubsonic's radio methods carry no logo, so a station saved from a
/// client arrives without one. Three sources, the owner's choice: the
/// station's own home page, Wikidata, and radio-browser.info, each keyed on
/// something that makes the station that station — the page itself, the
/// official website Wikidata names, the stream.
///
/// Null is "no source has one"; a source that could not be asked is passed
/// over, never the save's failure.
/// </remarks>
public interface IStationLogos
{
    /// <returns>The logo's absolute http or https address, or null.</returns>
    Task<string?> FindAsync(RadioStation station, CancellationToken cancellationToken = default);
}
