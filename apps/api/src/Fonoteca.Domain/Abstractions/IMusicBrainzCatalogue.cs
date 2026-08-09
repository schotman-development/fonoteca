using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// "What is this recording, and what was it released on?", answered by MBID.
/// </summary>
/// <remarks>
/// Lookup by identifier only. There is no search here, and that is a decision
/// rather than an omission: searching MusicBrainz by artist and title returns a
/// ranked list that has to be <i>matched</i> — scored against what the file's
/// tags claim, against its duration, against the rest of its folder — and
/// matching is a domain rule, not an HTTP call. Putting a search method on the
/// provider would invite that rule to be written next to the JSON parser.
/// Identification therefore goes fingerprint → <see cref="IAcoustIdLookup"/> →
/// MBID → here, and the tag-based fallback arrives with its own scoring code.
///
/// The shapes below are MusicBrainz's, flattened to what the catalogue in
/// <c>Fonoteca.Domain.Catalogue</c> actually stores. Deliberately not the
/// entities themselves: an <see cref="Recording"/> constructed from a provider
/// response is indistinguishable from one that has been merged, corrected and
/// linked to files, and letting an adapter mint them is how a bad match becomes
/// permanent.
/// </remarks>
public interface IMusicBrainzCatalogue
{
    /// <summary>
    /// One recording, with its artist credits and every release it appears on.
    /// </summary>
    /// <returns>Null when MusicBrainz has no such recording.</returns>
    /// <exception cref="ProviderUnavailableException">The service did not answer.</exception>
    /// <exception cref="ProviderRejectedException">The request was refused.</exception>
    Task<MusicBrainzRecording?> GetRecordingAsync(
        Mbid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One release, with its full track list.
    /// </summary>
    /// <remarks>
    /// The other half of identification: knowing which recording a file holds
    /// says nothing about whether the folder it sits in is a complete album.
    /// That question needs the release's whole track list, which a recording
    /// lookup does not carry.
    /// </remarks>
    /// <returns>Null when MusicBrainz has no such release.</returns>
    Task<MusicBrainzRelease?> GetReleaseAsync(
        Mbid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One composition, with the people MusicBrainz says wrote it.
    /// </summary>
    /// <remarks>
    /// A separate call rather than a deeper <c>inc=</c> on the recording, for two
    /// reasons. WS/2 does not offer the hop: a recording asked for
    /// <c>work-rels</c> gets a work <i>stub</i> — id, title, type — and the
    /// work's own artist relationships are only served by a work lookup. And the
    /// sharing is the point: a symphony movement recorded forty times is one
    /// work, so a caller that memoises by <see cref="MusicBrainzWork.Id"/> spends
    /// one request where folding it into the recording lookup would spend forty.
    /// </remarks>
    /// <returns>Null when MusicBrainz has no such work.</returns>
    Task<MusicBrainzWork?> GetWorkAsync(
        Mbid id,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A date MusicBrainz may only partly know: <c>1969</c>, <c>1969-08</c>,
/// <c>1969-08-16</c>.
/// </summary>
/// <remarks>
/// Not a <see cref="DateOnly"/>, because the missing parts are information.
/// "1969" widened to 1969-01-01 is a claim MusicBrainz never made, and it is
/// exactly the claim that makes a remaster sort ahead of the original pressing
/// when editions are ranked by release date. Collapsing this is the caller's
/// decision, taken where the consequence is visible.
/// </remarks>
public readonly record struct ReleaseDate(int Year, int? Month, int? Day)
{
    /// <summary>The date, only when all three parts are known.</summary>
    public DateOnly? ToDateOnly() =>
        Month is { } month && Day is { } day ? new DateOnly(Year, month, day) : null;

    public override string ToString() => (Month, Day) switch
    {
        ({ } m, { } d) => $"{Year:D4}-{m:D2}-{d:D2}",
        ({ } m, null) => $"{Year:D4}-{m:D2}",
        _ => $"{Year:D4}",
    };
}

/// <summary>A recording as MusicBrainz describes it.</summary>
public sealed record MusicBrainzRecording(
    Mbid Id,
    string Title,
    string? Disambiguation,

    /// <summary>Canonical length. Individual files differ by a frame or two across encodings.</summary>
    TimeSpan? Length,

    IReadOnlyList<MusicBrainzCredit> Credits,

    /// <summary>
    /// ISRCs, the recording industry's own identifier for this performance.
    /// </summary>
    /// <remarks>
    /// Free to collect here and worth having: an ISRC in a file's tags
    /// identifies a recording without a fingerprint or a network call at all,
    /// which makes it the cheapest confirmation that a match is right.
    /// </remarks>
    IReadOnlyList<string> Isrcs,

    IReadOnlyList<MusicBrainzAppearance> Appearances,

    /// <summary>
    /// Typed links to the people behind the performance, which the credit line
    /// does not name.
    /// </summary>
    /// <remarks>
    /// The whole reason this type is not just <see cref="Credits"/>. MusicBrainz
    /// bills a classical recording to the composer — "Ludwig van Beethoven" — and
    /// puts the conductor, the orchestra and the soloists in relationships. A
    /// consumer that reads only the credit line therefore cannot find the
    /// recording under the people who actually played it.
    /// </remarks>
    IReadOnlyList<MusicBrainzRelation> Relations,

    /// <summary>The composition this is a performance of, when MusicBrainz links one.</summary>
    /// <remarks>
    /// Only the identity and the title: the work's own relationships — the
    /// composer among them — need <see cref="IMusicBrainzCatalogue.GetWorkAsync"/>.
    /// </remarks>
    Mbid? WorkId,

    string? WorkTitle);

/// <summary>A composition and the people credited with writing it.</summary>
public sealed record MusicBrainzWork(
    Mbid Id,
    string Title,

    /// <summary>Song, Symphony, Opera. Free text, mirroring MusicBrainz work types.</summary>
    string? Type,

    IReadOnlyList<MusicBrainzRelation> Relations);

/// <summary>One typed link from an entity to an artist.</summary>
/// <remarks>
/// Flattened to the artist end on purpose: every relationship this application
/// has a use for — conductor, orchestra, composer, engineer — points at a
/// person or a group, and carrying MusicBrainz's full bidirectional relationship
/// model would put its serialisation format in the catalogue's vocabulary. Links
/// to anything else are dropped by the adapter rather than represented here.
///
/// <see cref="ArtistType"/> earns its place: "is this an ensemble" is a fact
/// about the <i>artist</i>, not about the relation. MusicBrainz links the
/// Berliner Philharmoniker to a recording as a plain <c>performer</c> about as
/// often as it uses <c>performing orchestra</c>, so a rule that reads only the
/// relation type finds one of them and misses the other.
/// </remarks>
public sealed record MusicBrainzRelation(
    /// <summary>The relationship type: "conductor", "composer", "performer", "mix".</summary>
    string Type,

    /// <summary>Instrument or role qualifier — "trumpet", "guest", "orchestra".</summary>
    string? Attribute,

    Mbid? ArtistId,

    /// <summary>Name as credited on this relationship, falling back to the artist's own.</summary>
    string Name,

    string? SortName,

    /// <summary>Person, Group, Orchestra, Choir.</summary>
    string? ArtistType,

    string? Disambiguation);

/// <summary>An artist's credit, in billing order, as printed.</summary>
/// <remarks>
/// Mirrors <see cref="ArtistCredit"/>: the join phrase and the credited name
/// are kept because "Miles Davis feat. John Coltrane" is three facts, not one
/// string.
/// </remarks>
public sealed record MusicBrainzCredit(
    Mbid? ArtistId,

    /// <summary>Name as credited here, which can differ from the artist's own.</summary>
    string Name,

    string? SortName,
    string? JoinPhrase,
    string? Disambiguation,

    /// <summary>Person, Group, Orchestra, Choir.</summary>
    string? ArtistType);

/// <summary>One release a recording appears on, and where on it.</summary>
/// <remarks>
/// A popular recording appears on dozens of these — the album, three
/// compilations, a remaster, six regional pressings. Which one a given file
/// actually came from is a separate question that duration, track count and the
/// rest of the folder answer; this type's job is to lay out the options.
/// </remarks>
public sealed record MusicBrainzAppearance(
    Mbid ReleaseId,
    string ReleaseTitle,
    ReleaseDate? ReleasedOn,

    /// <summary>ISO 3166-1 country code of the release event, when there is one.</summary>
    string? Country,

    /// <summary>Official, Promotion, Bootleg, Pseudo-Release.</summary>
    string? Status,

    Mbid? ReleaseGroupId,
    string? ReleaseGroupTitle,

    /// <summary>Album, EP, Single, Broadcast, Other.</summary>
    string? PrimaryType,

    /// <summary>1-based disc number within the release.</summary>
    int? DiscNumber,

    /// <summary>1-based track position on that disc.</summary>
    int? TrackPosition,

    /// <summary>Printed track number, which is not always the position — "A1", "12a".</summary>
    string? TrackNumber,

    /// <summary>Tracks on that disc. The count an incomplete rip is measured against.</summary>
    int? TrackCount);

/// <summary>A release and its whole track list.</summary>
public sealed record MusicBrainzRelease(
    Mbid Id,
    string Title,
    ReleaseDate? ReleasedOn,
    string? Country,
    string? Status,
    string? Barcode,
    IReadOnlyList<MusicBrainzLabel> Labels,
    Mbid? ReleaseGroupId,
    string? ReleaseGroupTitle,
    string? PrimaryType,

    /// <summary>Live, Compilation, Remix, Soundtrack. A release can carry several.</summary>
    IReadOnlyList<string> SecondaryTypes,

    IReadOnlyList<MusicBrainzCredit> Credits,

    /// <summary>Tracks across every disc, ordered by disc then position.</summary>
    IReadOnlyList<MusicBrainzTrack> Tracks);

/// <summary>A label and the catalogue number it gave this release.</summary>
public sealed record MusicBrainzLabel(Mbid? Id, string? Name, string? CatalogNumber);

/// <summary>One track's position on a release.</summary>
public sealed record MusicBrainzTrack(
    int DiscNumber,
    int Position,
    string? Number,
    string Title,
    TimeSpan? Length,

    /// <summary>The recording this track is an appearance of.</summary>
    Mbid? RecordingId,

    IReadOnlyList<MusicBrainzCredit> Credits);
