using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// What a service says an artist has released, for records the library has not got.
/// </summary>
/// <remarks>
/// <b>MusicBrainz is a volunteer catalogue and it is late.</b> A record reaches
/// it when an editor gets round to adding it, which for a new release is
/// routinely days or weeks — and the monitoring feature's whole premise is that
/// something appearing *after* somebody followed an artist is worth telling them
/// about. A source that learns about a record a fortnight late cannot answer
/// that question well on its own. A shop lists a record the day it goes on sale,
/// because that is the business it is in.
///
/// <b>This is discovery, not cataloguing, and the distinction is the reason the
/// bar is lower here than anywhere else in this application.</b> ADR 0011 puts a
/// provider's authority at the moment it serves bytes: what it says about files
/// it just delivered is fact. Nothing like that is claimed here. A row from this
/// seam is a *question* — "this exists and you have not got it" — that a person
/// reads on a shelf and acts on by searching. Being wrong costs one silly tile,
/// which is the same trade <c>Discography.IsGap</c> already takes and for the
/// same stated reason: the error to prefer is the one somebody can see.
///
/// <b>The unit is one artist, unlike <see cref="IArtistPortraits"/>.</b> That
/// seam is a batch because Wikidata's query language takes a list and asking
/// about a library one artist at a time would be an hour at the gate. No release
/// source here has a bulk form — a shop is asked about one artist — so a batch
/// shape would be a loop wearing a disguise, and it would hide the thing a
/// caller most needs to control: that this costs a gated request per followed
/// artist, every time it is asked.
///
/// <b>Both the id and the name travel, for <see cref="IArtistPortraits"/>'s
/// reason.</b> No shop has heard of MusicBrainz, so every one of them is
/// searched by name and every answer is a decision rather than a lookup —
/// <see cref="ArtistNameMatch"/> is that decision, shared, so that two sources
/// cannot disagree about who an artist is. The MBID travels anyway because it is
/// the key the caller files the answer under, and because a source that *can*
/// use it should not be prevented from doing so by a seam that never carried it.
/// </remarks>
public interface IReleaseDiscovery
{
    /// <summary>
    /// What this service says the artist has released.
    /// </summary>
    /// <param name="artist">Who to ask about, by whichever key the source uses.</param>
    /// <returns>
    /// What the source holds, and whether that was all of it. <b>An empty list is
    /// an answer</b> — "this shop carries nothing by them" — and so is a refusal
    /// to identify the artist at all; neither is a failure, and a caller must
    /// record having asked or it will ask again forever. That is the lesson six
    /// worklists in this codebase have each paid for separately.
    /// </returns>
    /// <exception cref="ProviderUnavailableException">
    /// The service could not be reached, or refused. Nothing is known, which is
    /// different from finding nothing: the caller must leave the artist on its
    /// worklist rather than stamping it.
    /// </exception>
    Task<DiscoveredReleases> FindAsync(
        ArtistToPicture artist,
        CancellationToken cancellationToken = default);
}

/// <summary>What one source says an artist released.</summary>
/// <param name="Releases">
/// The records it holds by them. Empty when the source carries nothing, and also
/// when it could not decide which of several namesakes was meant — the caller
/// cannot tell those apart and does not need to, because the action is the same.
/// </param>
/// <param name="Total">
/// How many the source holds, where it says. Greater than
/// <paramref name="Releases"/>'s length means the answer was cut — one real
/// artist measured at 166 albums against a 100-row page — and a caller reading
/// the length instead would treat part of a discography as all of it.
/// </param>
public readonly record struct DiscoveredReleases(
    IReadOnlyList<DiscoveredRelease> Releases,
    int Total)
{
    /// <summary>The source has nothing to say about this artist, and that is an answer.</summary>
    public static DiscoveredReleases None => new([], 0);
}

/// <summary>One record a source says exists.</summary>
/// <param name="Title">
/// As the source prints it, edition suffix and all. Not normalised here: the
/// comparison is <see cref="ReleaseTitleMatch"/>'s and the display is somebody
/// else's, and folding it at the boundary would lose "Deluxe Edition" for both.
/// </param>
/// <param name="Year">
/// First release year, or null where the source gives no usable date. Null is
/// common on back catalogue and is not a reason to discard the row — it only
/// makes the match against what is already held weaker, which
/// <see cref="ReleaseTitleMatch"/> states rather than hides.
/// </param>
/// <param name="Barcode">
/// The UPC, or null. <b>The only identifier here that is not provider-specific</b>
/// and the one ADR 0011 keys a provider-sourced release on — so it is carried
/// even though nothing at release-group level can currently use it, because the
/// day a <c>Release</c> is written from one of these it is the difference
/// between recognising a MusicBrainz album and minting a duplicate of it.
/// </param>
/// <param name="SourceId">
/// The source's own id for the record, for a caller that wants to fetch or buy
/// it. Opaque, and meaningless to any other provider.
/// </param>
/// <param name="CoverUrl">A sleeve, where the source has one.</param>
/// <param name="TrackCount">
/// How many tracks the source says it has, or null where it does not say.
/// <b>A shop's artist page is not a discography</b> — measured, one artist
/// returned 166 rows, and they are singles, EPs and compilations as much as
/// albums. Nothing else in the row separates those: the type field a catalogue
/// would carry does not exist here, and the title rarely says. The track count
/// is the only usable signal, which is why it travels.
/// </param>
public readonly record struct DiscoveredRelease(
    string Title,
    int? Year,
    string? Barcode,
    string? SourceId,
    string? CoverUrl,
    int? TrackCount = null);

/// <summary>
/// The sources that can answer "what has this artist released".
/// </summary>
/// <remarks>
/// Keyed registrations rather than "inject them all and try each", for
/// <see cref="ArtistPortraitSources"/>'s reason: they differ in what they cost
/// and in what they know, so the order is a decision a caller has to be able to
/// state out loud rather than one the container makes by registration order.
/// </remarks>
public static class ReleaseDiscoverySources
{
    /// <summary>
    /// The shop this application already buys from.
    /// </summary>
    /// <remarks>
    /// Preferred, and not only because it is current: it is the same catalogue a
    /// download comes out of, so "this record exists and you have not got it" and
    /// "you can have it" are one question. A record found anywhere else still has
    /// to be looked for here before anything can be done about it.
    /// </remarks>
    public const string Qobuz = "qobuz";

    /// <summary>
    /// The free breadth layer.
    /// </summary>
    /// <remarks>
    /// Carries artists and labels a subscription shop does not, and costs
    /// nothing to ask. It cannot sell anything, so a record only it knows about
    /// is a lead rather than an offer — which is exactly what a discovery seam is
    /// for.
    /// </remarks>
    public const string Deezer = "deezer";
}
