using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// "What is this recording, and what was it released on?", answered by MBID.
/// </summary>
/// <remarks>
/// Lookup by identifier, and one search that exists only because a person is
/// reading its results. The original rule here was that there is no search at
/// all — a text search returns a <i>ranked</i> list, ranking is matching, and
/// matching is a domain rule that had no business being written next to the JSON
/// parser. Every automated path still obeys it: identification goes fingerprint
/// → <see cref="IAcoustIdLookup"/> → MBID → here, and attribution starts from
/// <see cref="BrowseReleasesForRecordingAsync"/>.
///
/// <see cref="SearchReleasesAsync"/> is the exception and it is narrow. Its
/// caller is one endpoint behind one screen, where somebody types an album name
/// and picks from what comes back; nothing scores those results, nothing writes
/// anything from them without a click, and the relevance number MusicBrainz
/// returns is printed rather than believed. A pass that reached for this would
/// be the mistake the rule was written against.
///
/// <b>It needs a search index, which a mirror does not have.</b> Replication
/// covers the database and not Solr, so against a self-hosted server this call
/// fails where every other method on this interface works — see ADR 0006. That
/// is reported as the provider error it is rather than as an empty result, so
/// "no albums match" and "this server cannot search" stay distinguishable.
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
    /// Every release a recording appears on, without the lookup's silent cap.
    /// </summary>
    /// <remarks>
    /// The candidate set release attribution starts from, and a separate call
    /// from <see cref="GetRecordingAsync"/> because that one cannot answer it.
    /// A recording lookup asked for <c>inc=releases</c> returns <b>at most 25</b>
    /// and says nothing about having stopped: <i>Don't Rock the Jukebox</i> comes
    /// back with 25 where a browse reports 40. Choosing an album from a truncated
    /// list is how a file ends up on whichever pressing happened to sort first.
    ///
    /// Paged internally to exhaustion, so the answer is the whole set or an
    /// exception — never a quiet prefix of one.
    ///
    /// The medium summaries are what make the caller's shortlist cheap. A
    /// release's coverage can never exceed the share of its track count the
    /// caller already holds, so a 150-track compilation contributing two tracks
    /// can be ruled out without ever fetching its track list.
    /// </remarks>
    /// <exception cref="ProviderUnavailableException">The service did not answer.</exception>
    /// <exception cref="ProviderRejectedException">The request was refused.</exception>
    Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseReleasesForRecordingAsync(
        Mbid recording,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases matching what somebody typed, most relevant first.
    /// </summary>
    /// <remarks>
    /// A free-text search, which is the only method here that is not keyed on an
    /// identifier — see the type's remarks for why that is allowed exactly once.
    /// The query goes to MusicBrainz's own indexed search, so its syntax is
    /// theirs: bare words match across the release, and field prefixes
    /// (<c>artist:</c>, <c>date:</c>, <c>barcode:</c>) work as documented.
    ///
    /// Results carry a track count and no track list. Choosing one and reading
    /// what is on it is <see cref="GetReleaseAsync"/>, one release at a time,
    /// for the reason that method already gives.
    /// </remarks>
    /// <param name="limit">Results wanted. MusicBrainz caps this at 100.</param>
    /// <exception cref="ProviderUnavailableException">The service did not answer.</exception>
    /// <exception cref="ProviderRejectedException">
    /// The request was refused — which is also what a server with no search index
    /// looks like from here.
    /// </exception>
    Task<IReadOnlyList<MusicBrainzReleaseMatch>> SearchReleasesAsync(
        string query,
        int limit,
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
    /// One artist, as MusicBrainz describes them rather than as a sleeve billed them.
    /// </summary>
    /// <remarks>
    /// <b>The only call here whose subject the catalogue already holds a row
    /// for.</b> Every other method answers "what is this thing" about something
    /// nothing in the library knows yet; artists arrive the other way round —
    /// <c>CatalogueWriter</c> mints one from whichever <c>MusicBrainzCredit</c>
    /// reached it first and fills the fields with <c>??=</c>, so a row's name,
    /// sort name and type are whatever one release happened to print. That is
    /// enough to browse by and it is not a description of a musician: a credit
    /// line carries no country, no life span and no genre, because it is a line
    /// on a sleeve.
    ///
    /// Asked once per artist and never again, which is what makes it affordable
    /// at all: a library of eight thousand files is under three thousand
    /// artists, and they change on the timescale of MusicBrainz edits rather
    /// than of scans.
    /// </remarks>
    /// <returns>Null when MusicBrainz has no such artist — merged away, or deleted.</returns>
    /// <exception cref="ProviderUnavailableException">The service did not answer.</exception>
    /// <exception cref="ProviderRejectedException">The request was refused.</exception>
    Task<MusicBrainzArtist?> GetArtistAsync(
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

    /// <summary>
    /// Releases this recording appears on — <b>at most 25 of them</b>.
    /// </summary>
    /// <remarks>
    /// WS/2 caps <c>inc=releases</c> on a recording lookup at 25 and reports no
    /// total, so a full list and a truncated one are indistinguishable here.
    /// Measured: <i>Don't Rock the Jukebox</i> returns 25, and a browse of the
    /// same recording reports 40. Enough to show a file's context; <b>not</b>
    /// enough to choose a release from, which is what
    /// <see cref="IMusicBrainzCatalogue.BrowseReleasesForRecordingAsync"/> is for.
    /// </remarks>
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

/// <summary>An artist as MusicBrainz describes them, rather than as a credit line does.</summary>
/// <remarks>
/// Narrower than the WS/2 response on purpose. Aliases, IPIs, ISNIs, areas and
/// URL relations all arrive in the same document and none of them has a reader:
/// there is no catalogue search for aliases to feed, and no screen with a
/// external link on it. Widening this is one <c>Include</c> and one field the day
/// something wants them.
/// </remarks>
public sealed record MusicBrainzArtist(
    Mbid Id,
    string Name,
    string? SortName,

    /// <summary>Person, Group, Orchestra, Choir, Character, Other.</summary>
    string? Type,

    string? Disambiguation,

    /// <summary>ISO 3166-1 code of the country MusicBrainz primarily associates them with.</summary>
    string? Country,

    /// <summary>Male, female, non-binary — set on people, absent on groups.</summary>
    string? Gender,

    /// <summary>Born, or formed. The year alone, which is the part that is always known.</summary>
    /// <remarks>
    /// A year rather than a <see cref="ReleaseDate"/>, and the narrowing is
    /// deliberate rather than a shortcut. The <i>reason</i> release dates keep
    /// their month and day is that an edition sorted by date decides which
    /// pressing outranks which; nothing sorts artists by birthday. What a page
    /// prints is "1962–1970", so the year is the whole of what is read back.
    /// </remarks>
    int? BeganYear,

    /// <summary>Died, or dissolved. Null both when they have not and when nobody recorded it.</summary>
    int? EndedYear,

    /// <summary>
    /// Whether MusicBrainz says the life span is over.
    /// </summary>
    /// <remarks>
    /// Not <c>EndedYear is not null</c>. A band everybody knows split up but
    /// nobody has dated carries the flag with no year, and reading the year
    /// alone reports them as still going.
    /// </remarks>
    bool HasEnded,

    /// <summary>
    /// MusicBrainz's curated genres, most-voted first.
    /// </summary>
    /// <remarks>
    /// Genres and not tags. The two arrive in the same response and the tag list
    /// is the raw free-text one, where "seen live", "favourites" and a
    /// misspelling of the artist's own name outvote anything about the music.
    /// </remarks>
    IReadOnlyList<string> Genres,

    /// <summary>
    /// The groups this artist has been a member of, by MusicBrainz id.
    /// </summary>
    /// <remarks>
    /// <b>The one fact that separates a member's own record from a cover of
    /// their song.</b> MusicBrainz credits a Dire Straits recording to the
    /// <i>group</i>, which is an artist entity of its own, so Mark Knopfler
    /// reaches the catalogue only through the work's composer relation — and a
    /// rule reading roles alone files <i>Brothers in Arms</i> under "somebody
    /// else recorded their music", which is exactly wrong about the person who
    /// played and sang it.
    ///
    /// Ids alone, and no name, dates or instruments. The rule asks one question
    /// — is the artist credited on this release a band this artist was in — and
    /// answers it by identity. The group's name is already on its own artist row
    /// whenever the library holds any of its music, and storing a copy for a
    /// band the library holds nothing by would put an artist with no tracks in
    /// the browse list.
    ///
    /// <b>Only forward relations.</b> MusicBrainz serves <c>member of band</c>
    /// from both artists — forward from the person with the group on the far
    /// end, backward from the group with the member there — so this is filled
    /// by looking up the <i>member</i> and is empty for every group. Reading the
    /// backward ones too would record a band as belonging to each of its own
    /// members, which puts a band's albums on its members' shelves and none on
    /// its own.
    /// </remarks>
    IReadOnlyList<Mbid> Bands);

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

/// <summary>
/// A release a recording might have come from, before its track list is known.
/// </summary>
/// <remarks>
/// Everything a browse can say in one page, which is everything needed to decide
/// whether the release is worth a second round trip. <see cref="Media"/> is the
/// load-bearing part: it gives the track count without the tracks, and a release
/// whose track count dwarfs what the caller holds cannot be the answer no matter
/// what its track list turns out to say.
///
/// Deliberately not a <see cref="MusicBrainzRelease"/> with an empty track list.
/// The two would be indistinguishable at the type level, and "this release has no
/// tracks" is a very different claim from "nobody has asked yet" — one of them
/// scores zero coverage and the other is a bug.
/// </remarks>
public sealed record MusicBrainzReleaseCandidate(
    Mbid Id,
    string Title,
    ReleaseDate? ReleasedOn,

    /// <summary>ISO 3166-1 country code of the release event, when there is one.</summary>
    string? Country,

    /// <summary>Official, Promotion, Bootleg, Pseudo-Release.</summary>
    string? Status,

    string? Barcode,
    Mbid? ReleaseGroupId,
    string? ReleaseGroupTitle,

    /// <summary>Album, EP, Single, Broadcast, Other.</summary>
    string? PrimaryType,

    /// <summary>Live, Compilation, Remix, Soundtrack. A release can carry several.</summary>
    IReadOnlyList<string> SecondaryTypes,

    /// <summary>The discs, with their track counts but not their tracks.</summary>
    IReadOnlyList<MusicBrainzMediumSummary> Media)
{
    /// <summary>Tracks across every disc — the denominator of any coverage estimate.</summary>
    public int TrackCount => Media.Sum(medium => medium.TrackCount);
}

/// <summary>
/// A search hit: a release, plus the two things a browse result cannot carry.
/// </summary>
/// <remarks>
/// Composed rather than a wider <see cref="MusicBrainzReleaseCandidate"/>,
/// because the extra fields are facts about the <i>search</i> and not about the
/// release. <see cref="Score"/> exists only because a query produced this;
/// <see cref="Credits"/> is here because a browse for a known recording never
/// needed the billing line — the caller already knew whose recording it was —
/// and somebody reading a list of forty albums called <i>Greatest Hits</i>
/// needs nothing more urgently.
/// </remarks>
/// <param name="Score">MusicBrainz's own relevance, 0-100. Printed, never ranked on.</param>
public sealed record MusicBrainzReleaseMatch(
    MusicBrainzReleaseCandidate Release,
    IReadOnlyList<MusicBrainzCredit> Credits,
    int? Score);

/// <summary>One disc of a release, counted rather than listed.</summary>
public sealed record MusicBrainzMediumSummary(
    /// <summary>1-based position of this disc within the release.</summary>
    int Position,

    /// <summary>CD, Digital Media, 12" Vinyl, SHM-CD.</summary>
    string? Format,

    int TrackCount);

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
