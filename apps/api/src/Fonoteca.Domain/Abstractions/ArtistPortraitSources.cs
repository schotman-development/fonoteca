namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// The service keys the two <see cref="IArtistPortraits"/> registrations answer to.
/// </summary>
/// <remarks>
/// Three implementations of one seam, and the caller picks by name rather than
/// receiving them all and guessing, because the differences are not quality
/// alone. <see cref="Wikidata"/> answers for a whole library in a dozen
/// requests; the other two cost a request per artist, and one of those draws on
/// an allowance shared with downloading. Which artists deserve the expensive
/// ones, and in what order to ask, is a decision about the catalogue, so it
/// belongs to the pass and not to the container.
///
/// <b>They split two ways at once, and both splits matter.</b> By key:
/// <see cref="Qobuz"/> is searched by <i>name</i> and has to decide which result
/// is the artist; <see cref="AudioDb"/> and <see cref="Wikidata"/> take the
/// MusicBrainz id and cannot be wrong about who. By picture: the first two hold
/// photographs of people, and Wikidata holds whatever Commons has. See
/// <c>EnrichmentService.PictureSources</c> for the order and what each is for.
///
/// <b>Deezer was measured and rejected, which is worth writing down so it is
/// not tried twice.</b> Its search API is open, needs no credential and covers
/// the most — 30 of the 40 album artists Qobuz cannot place, against
/// TheAudioDB's 16. But its "artist picture" is whatever the label supplied,
/// and inspected one by one <b>16 of those 20 were album covers</b>: the
/// <i>Civilization VI</i> sleeve for its composer, <i>Bach Cantates</i> for a
/// conductor, one <i>Great Voices of Harlem</i> cover for two different
/// singers. This application does not put a sleeve on an artist, so a source
/// that supplies one four times in five is not a source.
/// </remarks>
public static class ArtistPortraitSources
{
    /// <summary>Press photographs, searched by name. Preferred, and rationed.</summary>
    public const string Qobuz = "portraits.qobuz";

    /// <summary>Thumbnails, looked up by MusicBrainz id, one at a time.</summary>
    public const string AudioDb = "portraits.audiodb";

    /// <summary>Commons images, looked up by MusicBrainz id in bulk. The fallback.</summary>
    public const string Wikidata = "portraits.wikidata";
}
