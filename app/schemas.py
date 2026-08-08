"""Pydantic response models for the JSON API under ``/api/*``.

All ORM-backed models set ``from_attributes=True``, so a router can return
``ArtistOut.model_validate(artist_row)`` directly. Fields that are not columns
(``artist_name`` on :class:`AlbumOut`, the roll-up counts on :class:`ArtistOut`)
are optional and are filled in by the router when it has the data.

Request bodies are deliberately included where the UI needs them
(:class:`ArtistCreateIn`, :class:`ArtistUpdateIn`, :class:`AlbumUpdateIn`) so
that the JSON API mirrors the HTML forms exactly.
"""

from __future__ import annotations

from datetime import UTC, date, datetime
from typing import Annotated, Any, Literal

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, field_validator

from app.models import (
    ActivityLevel,
    AlbumStatus,
    MonitorMode,
    QueueState,
    TrackOrigin,
    TrackStatus,
    parse_name_list,
)

__all__ = [
    "ORMModel",
    "ArtistOut",
    "ArtistCreditsOut",
    "ArtistCreditFilterIn",
    "CreditClusterOut",
    "AlbumOut",
    "TrackOut",
    "QueueItemOut",
    "ActivityOut",
    "SearchArtistOut",
    "SearchAlbumOut",
    "SearchResultOut",
    "StatusOut",
    "RateLimitStatusOut",
    "IndexerStatusOut",
    "QueueStatsOut",
    "LibraryStatsOut",
    "DiskCapacityOut",
    "LibraryScanOut",
    "LibraryScanStatusOut",
    "LibraryImportOut",
    "LibraryImportStartIn",
    "LibraryTidyOut",
    "RefileEstimateOut",
    "RefilePlanOut",
    "TrashEntryOut",
    "TrashOut",
    "ImportCandidateOut",
    "ImportReviewOut",
    "ScannedFolderOut",
    "SettingsOut",
    "SettingsUpdateIn",
    "EnrichmentReviewOut",
    "EnrichmentAutonomyOut",
    "EnrichmentStatusOut",
    "EnrichmentIdentifyIn",
    "EnrichmentRejectIn",
    "EnrichmentAcceptOut",
    "EnrichmentCandidateOut",
    "EnrichmentCandidatesOut",
    "MessageOut",
    "MessageLevel",
    "EnrichmentSourceStatusOut",
    "LibraryImportPreviewOut",
    "DatabaseHealthOut",
    "HealthOut",
    "StatsOut",
    "TrackIntegrityOut",
    "IntegrityReportOut",
    "IntegrityStatusOut",
    "IntegrityRunOut",
    "CorruptTrackOut",
    "CorruptListOut",
    "ArtistCreateIn",
    "ArtistUpdateIn",
    "ArtistBulkUpdateIn",
    "AlbumUpdateIn",
    "ArtistFollowOut",
    "PageOut",
    "ArtistListOut",
    "AlbumListOut",
    "WantedListOut",
    "QueueListOut",
    "EnrichmentReviewListOut",
    "ActivityListOut",
    "ArtistDetailOut",
    "TrashSummaryOut",
    "HealthSummaryOut",
    "NavCountsOut",
    "BannerOut",
    "MetaOut",
    "ConsensusVoteOut",
    "ConsensusFieldOut",
    "ReleaseGroupRefOut",
    "ReleaseGroupOut",
    "AlbumDetailOut",
    "ArtistStatsOut",
]


def _stamp_utc(value: datetime | None) -> datetime | None:
    """Attach UTC to a naive datetime on its way out to a client.

    SQLite round-trips ``DateTime(timezone=True)`` columns as *naive* datetimes.
    :func:`app.api.deps.as_utc` re-stamps them for anything Python compares, but a
    model built by ``model_validate(row)`` copies the column straight through, so
    the value that reached the browser carried no offset at all. ``new Date()``
    reads an offset-less ISO string as *local*, which made every relative time in
    the UI wrong by the host's offset — two hours in Amsterdam, and exactly zero
    on the UTC machines where tests and containers run, so the bug is invisible
    precisely where it would be caught.

    It was visible on the wire as two spellings of one instant in one response:
    ``"added_at": "2026-01-02T03:04:05"`` beside
    ``"last_verified_at": "2026-01-02T03:04:05Z"``.

    Naive means UTC here because that is what the database stores; a value that
    already carries an offset is converted rather than overwritten, so a caller
    that hands in a real local time is not silently relabelled.
    """
    if value is None or value.tzinfo is not None:
        return None if value is None else value.astimezone(UTC)
    return value.replace(tzinfo=UTC)


#: A datetime that is always serialised as aware UTC.
#:
#: Use this for EVERY datetime a client can see. A bare ``datetime`` on a read
#: model is the bug above waiting to happen again, and it fails silently.
UtcDatetime = Annotated[datetime, AfterValidator(_stamp_utc)]


class ORMModel(BaseModel):
    """Base for models read straight off SQLAlchemy rows."""

    model_config = ConfigDict(from_attributes=True)


#: How loudly a client should announce the result of a mutation.
#:
#: The server picks it because the server is the only side that knows: it holds
#: ``result.failed``, ``result.blocked`` and the count of artists a bulk edit
#: emptied. A client deriving severity from the prose has to match on sentences,
#: which quietly makes the sentences an interface — the same trap
#: :class:`BannerOut.code` exists to avoid.
MessageLevel = Literal["info", "success", "warning", "error"]


# ---------------------------------------------------------------------------
# Library entities
# ---------------------------------------------------------------------------
class ArtistOut(ORMModel):
    """A followed artist."""

    id: str
    name: str
    monitored: bool
    monitor_mode: MonitorMode
    quality_profile: str
    accepted_release_types: list[str] = Field(default_factory=list)
    include_guest_appearances: bool = False
    """Whether releases this artist only guests on count as theirs. Default
    ``False``; see ``Artist.include_guest_appearances``."""
    credit_filter: list[str] = Field(default_factory=list)
    """Accepted credit names, when a person has narrowed this artist to a
    subset of the Qobuz id's releases. The empty list means **no filter**, which
    is the default — there is no unmeasured state here, so this is
    ``list[str]`` rather than ``list[str] | None``."""
    image_url: str | None = None
    albums_count: int = 0
    last_checked_at: UtcDatetime | None = None
    added_at: UtcDatetime | None = None
    qobuz_slug: str | None = None

    # Roll-ups, populated by the router when available.
    album_count: int | None = None
    wanted_count: int | None = None
    downloaded_count: int | None = None

    # ------------------------------------------------------------ enrichment
    # Merged in at read time from ``artist_metadata``. See ``AlbumOut`` for why
    # these are passed in rather than loaded through a relationship.
    isni: str | None = None
    """The artist's ISNI — their identifier in the wider world, stable across
    every catalogue, unlike any single service's id."""
    mb_artist_mbid: str | None = None
    deezer_artist_id: str | None = None
    wikidata_qid: str | None = None
    sort_name: str | None = None
    """How the artist files. Coalesced: ``Artist.sort_name`` when somebody has
    typed one, MusicBrainz's otherwise — so what the edit drawer shows back is
    what was saved, not what the next enrichment pass would have said."""
    aliases: list[str] = Field(default_factory=list)
    """What a person typed as alternative spellings, in the order they typed
    them. Read straight off ``Artist.aliases_json`` with no coalesce, unlike
    ``sort_name``: MusicBrainz's aliases are read live by the matcher and never
    stored, so the only source is the box in the edit drawer. The empty list is
    the honest answer for "nobody typed any" — there is no unmeasured state
    here, which is why this is ``list[str]`` and not ``list[str] | None``."""
    disambiguation: str | None = None
    artist_type: str | None = None
    """Person, Group, Orchestra, Choir."""
    country: str | None = None
    area: str | None = None
    formed: str | None = None
    """Life-span start. A *string*, because these dates are legitimately partial
    (``"1975"``, ``"1975-06"``)."""
    disbanded: str | None = None
    genres: list[str] = Field(default_factory=list)
    bio: str | None = None
    bio_source_url: str | None = None
    """Where the biography came from. Wikipedia text is CC BY-SA, so rendering it
    without this link is a licence breach — the two travel together."""
    bio_licence: str | None = None
    portrait_url: str | None = None
    """Qobuz's image, falling back to a Wikimedia Commons or Deezer portrait."""
    external_links: list[dict[str, str]] = Field(default_factory=list)
    """``[{"label": ..., "url": ...}]`` for MusicBrainz, Wikipedia, Deezer and the
    artist's own site."""
    enriched: bool = False

    @field_validator("accepted_release_types", mode="before")
    @classmethod
    def _split_csv(cls, value: Any) -> Any:
        """Accept the stored comma-separated string as well as a real list."""
        if isinstance(value, str):
            return [part.strip().lower() for part in value.split(",") if part.strip()]
        return value


class TrackIntegrityOut(BaseModel):
    """What has actually been measured about one file, never re-measured on read.

    Every field here is a *recorded* observation. Answering a GET by opening the
    file would mean blocking disk I/O on the event loop — roughly 201 ms a file,
    see :mod:`app.core.integrity` — inside a read model, which is the one place
    that must never do it. So this reports the baseline and when it was last
    confirmed, and nothing else.

    That is also why ``state`` only ever comes back ``unknown``, ``verified`` or
    ``missing``. ``retagged`` and ``replaced`` are verdicts a *pass* gives, and
    the pass re-baselines the row in the same transaction that gives them — the
    file and its record agree again a moment later, so a row storing "replaced"
    would be describing a disagreement that no longer exists. Those two counts
    live on :class:`IntegrityReportOut`, which is a statement about a run rather
    than about a file.

    ``fingerprint_state`` is the other half and comes from ``track_metadata``:
    ``corrupt`` is the only value that is actionable, and acting on it is
    ``POST /api/library/quarantine``.
    """

    qid: str = ""
    """The track's stable local identity. Minted once and never reissued, which
    is what lets a measurement survive a re-file, a re-tag or a re-match — the
    row's ``id`` cannot, because a scanned row synthesises it from the track's
    position in the album."""
    state: str = "unknown"
    """One of :class:`app.core.integrity.IntegrityState`. ``unknown`` means
    *nothing has ever baselined this file*, which is emphatically not a claim
    that it was tampered with."""
    content_hash: str | None = None
    sample_count: int | None = None
    file_size: int | None = None
    file_mtime: float | None = None
    """POSIX mtime as a float, exactly as ``os.stat`` reports it. A number rather
    than a timestamp, because it has no timezone to be asked about."""
    last_verified_at: UtcDatetime | None = None
    fingerprint_state: str | None = None
    """``ok`` / ``corrupt`` / ``unreadable``, or ``None`` when nothing has
    fingerprinted this file."""


class TrackOut(ORMModel):
    """A single track of an album."""

    id: str
    album_id: str
    title: str
    version: str | None = None
    track_number: int = 0
    media_number: int = 1
    duration: int | None = None
    isrc: str | None = None
    performer: str | None = None
    composer: str | None = None
    status: TrackStatus
    origin: TrackOrigin = TrackOrigin.DOWNLOAD
    """Which half of Qobuzarr wrote this row: ``download`` means the download
    loop fetched the file and may rewrite its tags, ``scan`` means the file was
    already on disk with somebody else's tags on it.

    Stated, never inferred. The old test was "does a track row exist at all",
    which held only while the download loop was the sole writer of them; the disk
    scan writes a row for every file it finds now, so reading it the old way turns
    ``ENRICHMENT_WRITE_BACK`` into a licence to rewrite the tags of every
    hand-curated album in the library."""
    path: str | None = None
    format_id: int | None = None
    bit_depth: int | None = None
    sampling_rate: float | None = None
    file_size: int | None = None
    downloaded_at: UtcDatetime | None = None

    integrity: TrackIntegrityOut | None = None
    """What has been measured about the file. ``None`` when the caller did not
    ask for it — it is built only where the track list itself is built, so a
    release page gets it and a bare album row does not pay for it."""


class AlbumOut(ORMModel):
    """A release belonging to a followed artist. ``id`` is a non-numeric string."""

    id: str
    artist_id: str
    title: str
    version: str | None = None
    release_date: date | None = None
    release_type: str = "album"
    tracks_count: int = 0
    media_count: int = 1
    hires: bool = False
    max_bit_depth: int | None = None
    max_sampling_rate: float | None = None
    label: str | None = None
    genre: str | None = None
    upc: str | None = None
    image_url: str | None = None
    duration: int | None = None
    status: AlbumStatus
    monitored: bool = True
    path: str | None = None
    downloaded_at: UtcDatetime | None = None
    added_at: UtcDatetime | None = None

    # Convenience extras, populated by the router when available.
    artist_name: str | None = None
    year: int | None = None
    tracks: list[TrackOut] | None = None

    # Quality comparison, filled in by ``app.api.deps.album_to_out``.
    owned_format_id: int | None = None
    """The Qobuz ``format_id`` of the worst file we hold for this release, or
    ``None`` when nothing is on disk (or its quality cannot be determined)."""
    owned_hires: bool | None = None
    """Whether the copy **on disk** is hi-res, derived from
    :attr:`owned_format_id` by :func:`app.core.quality.is_hires`. ``None`` when
    nothing is on disk. Deliberately not :attr:`hires`, which is the catalogue's
    availability flag: a 16/44.1 download of a hi-res listing has ``hires=True``
    and ``owned_hires=False``, and a UI that reads the first as the second
    labels a CD-quality file hi-res."""
    upgrade_format_id: int | None = None
    """The format a fresh download would land, when that is strictly better than
    :attr:`owned_format_id`. ``None`` means no upgrade is on offer — either the
    copy on disk is already the best this account can get for this release, or
    something needed for the comparison is unknown. See
    :mod:`app.core.quality`."""
    queue_state: QueueState | None = None
    """The state of this album's live queue entry (``pending``/``active``), or
    ``None`` when it is not queued. A downloaded album keeps its ``DOWNLOADED``
    status while an upgrade is queued, so ``status`` alone cannot tell you."""

    # ------------------------------------------------------------ enrichment
    # Merged in at read time from ``album_metadata`` by ``deps.album_to_out``,
    # which the async callers batch-load and pass in. Every one of these is
    # optional and defaults to empty: a caller that does not supply the metadata
    # gets an un-enriched album rather than an error.
    barcode: str | None = None
    """The release barcode, from Qobuz's ``upc`` or from its album id when that
    id *is* the barcode — which is far more often. This is the key everything
    else was matched on."""
    mb_release_mbid: str | None = None
    mb_release_group_mbid: str | None = None
    deezer_album_id: str | None = None
    catalog_number: str | None = None
    mb_country: str | None = None
    genres: list[str] = Field(default_factory=list)
    """Every genre the open sources list, rather than the single string Qobuz
    collapses its own list down to. Display only — ``genre`` stays Qobuz's."""
    cover_url: str | None = None
    """Best artwork available: Qobuz's, unless it has none or
    ``ENRICHMENT_PREFER_EXTERNAL_COVER`` is on. Behind it, Deezer's official
    1000x1000 before the Cover Art Archive's contributor upload — see
    ``AlbumMetadata.cover_url`` for why that order and not the ladder's."""
    suggested_release_type: str | None = None
    """What a majority of sources say this release is."""
    release_type_disagrees: bool = False
    """True when the sources' verdict differs from ``release_type``, which only
    happens while the write-back is switched off."""
    qobuz_release_type: str | None = None
    """What Qobuz called it before a consensus corrected it. ``None`` means
    nothing was ever overwritten."""
    release_group_key: str | None = None
    """What editions of one record are grouped by: the MusicBrainz release-group
    id when known, otherwise the normalised title. Set by the collection
    builders, which are the only place that can see the whole group."""
    enriched: bool = False
    """True when at least one source matched this release."""

    # --------------------------------------------------------- completeness
    tracks_on_disk: int | None = None
    """How many of this release's tracks are actually present.

    ``None`` when it cannot be known — an album adopted from disk by the scanner
    has no ``Track`` rows, because only the download loop creates those. That is
    a real gap and it is reported as unknown rather than as zero, which would
    show a complete album as empty."""
    complete: bool | None = None
    """Whether every track is present. ``None`` when unknown, for the same reason."""

    adopted: bool = False
    """True when no track row for this release came from the download loop.

    The release screen needs to say "adopted from disk — only the download loop
    writes per-track records" without guessing, and the guess it used to make was
    ``tracks == []``. That stopped being true when the disk scan started writing a
    row for every file it finds, which is exactly the population this sentence is
    about. ``Track.origin`` is the answer and this is it, rolled up.

    Only meaningful for a release with files: a catalogue release nobody has
    downloaded trivially has no ``download``-origin rows either, so read it
    alongside ``status``/``tracks_on_disk`` rather than on its own."""

    # ------------------------------------------------------------- integrity
    integrity_state: str | None = None
    """What this release's files are recorded as, rolled up from its tracks.

    One of :class:`app.core.integrity.IntegrityState`'s values when every track
    agrees, ``"mixed"`` when they do not, and ``None`` when **no** track carries a
    baseline — which is the ordinary state of a library nothing has measured yet
    and must render as "not measured", never as a clean bill of health.

    Recorded, not measured: see :class:`TrackIntegrityOut` for why a read model
    may not open the files, and why ``retagged``/``replaced`` are pass verdicts
    rather than stored ones."""
    corrupt_tracks: int = 0
    """Files this release holds that ``fpcalc`` ran on, finished, and could not
    decode — which no music player could either. Counted by the async collection
    builders; a caller that builds an :class:`AlbumOut` on its own gets ``0``,
    the same way it gets an un-enriched album.

    Reported whatever :attr:`mute_integrity` says. Muting suppresses the alarm —
    the quarantine, and the library-wide figure on the Integrity screen — never
    the measurement, so the release that is muted is exactly the release whose
    own page has to keep saying what was found."""

    # ------------------------------------------------------ per-album switches
    # Three decisions a person makes about one release, each doing its work in
    # exactly one place. Written through ``PATCH /api/albums/{id}``, where
    # ``None`` means "leave alone".
    pin_tags: bool = False
    """Enrichment's background write-back leaves this release's file tags alone.

    ``Enricher._write_back()`` is the single writer of enrichment tags into
    files, so this is one guard clause there. The NFO is still written, because
    an NFO is merged element by element rather than overwritten. The explicit
    re-tag buttons still work: they are a decision made while looking at the
    release, which is a different thing from a background pass."""
    freeze_path: bool = False
    """Nothing moves or renames this release's folder.

    ``plan_refile()`` returns a blocked plan, the library-wide preview leaves it
    out, and ``refile_album()`` refuses even when handed a plan built
    elsewhere."""
    mute_integrity: bool = False
    """No corruption alarm for this release.

    For the known-good rip that fails a strict check. It excludes the release
    from ``quarantine_corrupt_files()`` — the nightly, unattended action that
    moves files to the trash — and from the Integrity screen's ``corrupt_files``
    figure, which is that button's own count. It does **not** stop the
    fingerprinting, clear a verdict, or change :attr:`corrupt_tracks` or
    :attr:`integrity_state` here."""

    # ------------------------------------------------------------ attribution
    guest_appearance: bool | None = None
    """Whether the owning artist only guests on this release.

    Three-valued on purpose: ``None`` means no Qobuz payload has said yet, and
    it behaves exactly as ``False`` does when deciding what to want. It is not
    folded into a plain ``bool`` here precisely so a client can show "not
    measured" rather than asserting the artist is the main credit."""
    credit_names: list[str] | None = None
    """Qualified credit names found on this release's tracks, or ``None`` when
    nobody has analysed it. ``[]`` means analysed and every credit was the bare
    artist name — a real answer, and the reason this is not ``list[str]``."""

    @field_validator("credit_names", mode="before")
    @classmethod
    def _parse_credit_names(cls, value: Any) -> Any:
        """Accept the stored JSON string as well as a list.

        ``Album.credit_names`` is a JSON ``Text`` column, so ``from_attributes``
        hands this a ``str``. Parsing here keeps the read model's field name the
        same as the column's — and keeps ``None`` (*not analysed*) distinct from
        ``"[]"`` (*analysed, nothing qualified*), which is the whole point of
        the column."""
        if isinstance(value, str):
            return parse_name_list(value)
        return value


# ---------------------------------------------------------------------------
# Queue and activity
# ---------------------------------------------------------------------------
class QueueItemOut(ORMModel):
    """One entry in the sequential download queue."""

    id: int
    album_id: str
    state: QueueState
    priority: int = 0
    attempts: int = 0
    last_error: str | None = None
    progress_tracks_done: int = 0
    progress_tracks_total: int = 0
    created_at: UtcDatetime | None = None
    started_at: UtcDatetime | None = None
    finished_at: UtcDatetime | None = None

    # Denormalised display fields, populated by the router when available.
    album_title: str | None = None
    album_image_url: str | None = None
    album_monitored: bool = True
    """Whether the *release* is still monitored — the queue page shows a toggle
    for it. Unrelated to ``state``: ignoring an album does not cancel a download
    that is already running."""
    artist_id: str | None = None
    artist_name: str | None = None
    progress_percent: int = 0


class ActivityOut(ORMModel):
    """One line of the activity/history feed."""

    id: int
    level: ActivityLevel
    event: str
    message: str
    artist_id: str | None = None
    album_id: str | None = None
    created_at: UtcDatetime | None = None

    artist_name: str | None = None
    album_title: str | None = None


# ---------------------------------------------------------------------------
# Search
# ---------------------------------------------------------------------------
class SearchArtistOut(BaseModel):
    """An artist hit from ``catalog/search``, plus whether we already follow it."""

    id: str
    name: str
    image_url: str | None = None
    albums_count: int = 0
    slug: str | None = None
    followed: bool = False


class SearchAlbumOut(BaseModel):
    """An album hit from ``catalog/search``."""

    id: str
    title: str
    version: str | None = None
    artist_id: str | None = None
    artist_name: str | None = None
    release_date: date | None = None
    year: int | None = None
    tracks_count: int = 0
    hires: bool = False
    image_url: str | None = None

    #: The files are on disk. Not the same as being *known*: the indexer writes
    #: a row for every release a followed artist has, so "we have a row" would
    #: mark a release the Wanted page is still asking for as owned.
    in_library: bool = False

    #: Qobuzarr has a row for this release — followed, and wanted or ignored.
    tracked: bool = False


class SearchResultOut(BaseModel):
    """Combined result of a catalogue search."""

    query: str
    artists: list[SearchArtistOut] = Field(default_factory=list)
    albums: list[SearchAlbumOut] = Field(default_factory=list)
    total_artists: int = 0
    total_albums: int = 0
    limit: int = 25
    offset: int = 0


# ---------------------------------------------------------------------------
# Status / dashboard
# ---------------------------------------------------------------------------
class RateLimitStatusOut(BaseModel):
    """Current state of the global Qobuz rate limiter."""

    min_request_interval: float
    max_requests_per_hour: int
    requests_last_hour: int = 0
    budget_remaining: int = 0
    budget_used_percent: float = 0.0
    seconds_until_next_slot: float = 0.0
    last_request_at: UtcDatetime | None = None
    circuit_open: bool = False
    circuit_open_until: UtcDatetime | None = None
    recent_429s: int = 0


class IndexerStatusOut(BaseModel):
    """Current state of the slow background indexer."""

    enabled: bool = True
    running: bool = False
    paused: bool = False
    artist_interval_seconds: int = 0
    full_sweep_hours: int = 0
    next_run_at: UtcDatetime | None = None
    seconds_until_next_run: float | None = None
    current_artist_id: str | None = None
    current_artist_name: str | None = None
    last_run_at: UtcDatetime | None = None
    monitored_artists: int = 0
    artists_never_checked: int = 0


class QueueStatsOut(BaseModel):
    """Counts per queue state plus what the worker is doing right now."""

    pending: int = 0
    active: int = 0
    done: int = 0
    failed: int = 0
    cancelled: int = 0
    total: int = 0
    current_album_id: str | None = None
    current_album_title: str | None = None
    worker_running: bool = False


class LibraryStatsOut(BaseModel):
    """Headline counts for the dashboard."""

    artists: int = 0
    monitored_artists: int = 0
    albums: int = 0
    wanted_albums: int = 0
    downloaded_albums: int = 0
    failed_albums: int = 0
    tracks: int = 0
    downloaded_tracks: int = 0
    size_bytes: int | None = None
    """Bytes of audio this library holds, summed from ``tracks.file_size`` over
    releases whose status is ``DOWNLOADED``. ``None`` when no track carries a
    size — nothing has measured it, which is not zero. Never a ``du`` walk: the
    footer polls every 10s and the figure has to cost one aggregate.

    This is the LIBRARY's size, not the disk's fullness — see
    :class:`DiskCapacityOut`, which is the number that says whether another
    discography will fit."""


class BannerOut(BaseModel):
    """One "something is not wired up" notice, shown above whatever screen you
    are on.

    ``code`` is the machine-readable half and the reason this is a model rather
    than three strings. The prose is written to be read by a person and changes
    when it reads badly; a client that wanted to link "app secret unresolved" to
    the settings screen had to match on the sentence, so the sentence became an
    interface nobody could edit. Route on ``code`` and render ``message``.

    The circuit-breaker banner keeps its remaining seconds inside ``message``:
    the number is stale the moment it is serialised, so it is prose, not data.
    """

    level: Literal["info", "warning", "error"] = "warning"
    title: str = ""
    message: str = ""
    code: str = ""
    """``no_credentials`` / ``no_client`` / ``no_app_secret`` / ``breaker_open``."""


class DiskCapacityOut(BaseModel):
    """The volume holding ``LIBRARY_PATH``, measured with one ``statvfs``.

    Whole-record three-valuedness: :attr:`StatusOut.disk` is ``None`` when the
    path could not be probed, because a total with no used figure is not a
    partial answer, it is a different one. ``used_bytes + free_bytes`` may be
    less than ``total_bytes`` — the difference is the root-reserved blocks
    ``df`` also omits from *Avail*.

    This is the DISK's fullness, not the library's size. The library's own byte
    total is :attr:`LibraryStatsOut.size_bytes`; they are different facts and
    only one of them says whether another discography will fit.
    """

    path: str = ""
    total_bytes: int = 0
    used_bytes: int = 0
    free_bytes: int = 0


class StatusOut(BaseModel):
    """Everything the dashboard needs in a single payload."""

    version: str = "0.1.0"
    started_at: UtcDatetime | None = None
    uptime_seconds: float = 0.0
    library: LibraryStatsOut = Field(default_factory=LibraryStatsOut)
    queue: QueueStatsOut = Field(default_factory=QueueStatsOut)
    indexer: IndexerStatusOut = Field(default_factory=IndexerStatusOut)
    rate_limit: RateLimitStatusOut | None = None
    recent_activity: list[ActivityOut] = Field(default_factory=list)
    credentials_ok: bool = False
    app_secret_ok: bool = False
    library_path: str = ""
    disk: DiskCapacityOut | None = None
    """``None`` when ``library_path`` does not exist or ``statvfs`` failed — an
    unprobeable disk, never a full or an empty one."""
    banners: list[BannerOut] = Field(default_factory=list)
    """Carried here as well as on ``GET /api/banners`` because the shell already
    polls this endpoint: a screen that polls status must not need a second
    request to know the credentials are missing."""


class LibraryQualityOut(BaseModel):
    """The hi-res roll-up over the releases actually on disk.

    Three-valued, for the same reason :attr:`AlbumOut.complete` is. The
    denominator is :attr:`measured`, never :attr:`albums`: a release whose held
    format cannot be determined has not been found to be lossy, it has not been
    found at all, and dividing by the whole population would report it as a
    definite "not hi-res". On one real library that is nine releases in
    seventy-five — every one of them a scan-adopted MP3 folder, whose rows carry
    a sampling rate and no bit depth, so :func:`app.core.quality.format_for_quality`
    correctly refuses to guess. Both numbers are published so the exclusion
    leaves a trace: a share over a denominator nobody can see is a share nobody
    can check.

    The arithmetic is :mod:`app.core.quality`'s and no one else's —
    :func:`~app.core.quality.owned_format_id` then
    :func:`~app.core.quality.is_hires`, the same pair
    :func:`app.api.deps.album_to_out` uses for :attr:`AlbumOut.owned_hires`.
    That is what makes this figure and the release rows under it unable to
    disagree.
    """

    albums: int = 0
    """Releases on disk — ``enricher.library_scope``'s album population."""
    measured: int = 0
    """Of those, the ones whose held format is known. The denominator."""
    hires: int = 0
    """Of the measured, the ones held in a format above 16 bit."""
    hires_share: float | None = None
    """``hires / measured``. ``None`` — never ``0.0`` — when nothing has been
    measured: a share of nothing is not zero, and a fresh install reporting
    ``0% hi-res`` reads as a broken library rather than an empty one."""


class StatsOut(BaseModel):
    """The compact counters, typed.

    Same numbers as :class:`StatusOut` carries, without the activity page and
    without the banners — this is the endpoint an external dashboard scrapes, and
    the one a screen polls when all it wants is a header strip. It had no
    ``response_model`` at all, which meant a generated client saw an opaque
    object where the two most-polled payloads in the application are.

    ``albums_by_status`` is zero-filled for every :class:`app.models.AlbumStatus`
    for the same reason :class:`ArtistStatsOut.counts` is: it drives a bar list,
    and a status with no releases has no row in a grouped count, so the bar would
    be missing rather than empty.
    """

    library: LibraryStatsOut = Field(default_factory=LibraryStatsOut)
    albums_by_status: dict[str, int] = Field(default_factory=dict)
    queue: QueueStatsOut = Field(default_factory=QueueStatsOut)
    indexer: IndexerStatusOut = Field(default_factory=IndexerStatusOut)
    rate_limit: RateLimitStatusOut | None = None
    """``None`` when no limiter is wired up — which is a half-started process, not
    an unlimited one."""
    quality: LibraryQualityOut = Field(default_factory=LibraryQualityOut)
    """The hi-res roll-up. Deliberately here and not on
    :class:`LibraryStatsOut`, which :class:`StatusOut` also embeds: the shell
    polls status every 10s and does not want this scan."""


class DatabaseHealthOut(BaseModel):
    """Result of the database probe. ``error`` is present only when it failed."""

    ok: bool = False
    journal_mode: str | None = None
    """WAL, in a healthy install. Worth reporting: a database that fell back to
    ``delete`` mode serialises readers against the writer, which is what a
    mysteriously slow UI during a download looks like."""
    error: str | None = None


class HealthOut(BaseModel):
    """The unprefixed liveness probe, typed.

    ``degraded`` rather than a 500 when the database is unreachable, because the
    process is alive and able to say so, and that distinction is the whole point
    of a health endpoint. Credentials being absent is **not** degradation: the
    application is designed to run without them and simply does less.
    """

    status: Literal["ok", "degraded"] = "ok"
    version: str = "0.1.0"
    database: DatabaseHealthOut = Field(default_factory=DatabaseHealthOut)
    credentials_ok: bool = False
    app_secret_ok: bool = False
    qobuz_client: bool = False
    indexer_enabled: bool = False


class ScannedFolderOut(BaseModel):
    """One album folder the disk scan reported on.

    Used for all three detail lists (partial, unmatched, unknown artists), so
    every field beyond the path is optional — an unknown-artist row has no
    ``album_id``, and a partial row has no ``name``.
    """

    path: str = ""
    artist: str = ""
    title: str = ""
    year: int | None = None
    files: int = 0
    discs: int = 1
    bytes: int = 0
    albums: int = 0
    """Album folders rolled into this row (unknown-artist rows only)."""
    name: str = ""
    """Artist name as it appears on disk (unknown-artist rows only)."""
    artist_id: str | None = None
    album_id: str | None = None
    expected: int | None = None
    """Track count Qobuz reports for the release (partial rows only)."""
    status: str | None = None


class LibraryScanOut(BaseModel):
    """Result of one disk scan, mirroring :class:`app.core.scanner.ScanResult`."""

    root: str = ""
    started_at: UtcDatetime | None = None
    duration_seconds: float = 0.0
    applied: bool = True
    directories: int = 0
    audio_files: int = 0
    albums_found: int = 0
    albums_matched: int = 0
    albums_excluded: int = 0
    """Folders a person marked as holding no catalogue release.

    Counted apart from ``unmatched`` on purpose: that figure is what somebody
    reads to know how much identification work is left, and a permanently
    unmatchable folder left in it makes the number unable to fall. Still
    reported rather than hidden — a folder absent from every count is
    indistinguishable from one the scan never saw."""
    albums_adopted: int = 0
    albums_already: int = 0
    albums_partial: int = 0
    albums_busy: int = 0
    tracks_linked: int = 0
    queue_items_cancelled: int = 0

    import_started: bool = False
    """Whether the scan started following the unknown artists it found.

    The scan itself is still one-directional and still makes no Qobuz call; the
    following happens afterwards, in ``app.core.importer``. It is *started* and
    not awaited, because 136 unknown artists is one search each — about seven
    minutes — and a request that blocked for that long would time out in the
    browser having still done the work. So this flag is the client's cue to poll
    ``GET /api/library/import`` for the rest, exactly as the Import button does.

    ``False`` covers every reason nothing began: the scan found nobody new, an
    import was already running, or this instance has no importer wired."""

    # -------------------------------------------------------- integrity
    # ``ScanResult`` has reported these for as long as the scan has classified
    # what it measures, and this model simply did not declare them — so they were
    # dropped on serialisation, silently, which made the ``REPLACED`` verdict
    # invisible to every client. It is the only place that verdict ever surfaces
    # outside the nightly job.
    files_measured: int = 0
    """Files the scan stamped, having classified them against the stamp already
    recorded. Classify-then-assign is the order that matters: a scan that
    re-stamped quietly would make ``replaced`` unreachable for any change that
    moved size or mtime, which is essentially all of them."""
    files_retagged: int = 0
    """Different bytes, provably the same audio. The identification still holds;
    only the baseline needed refreshing."""
    files_replaced: int = 0
    """Different audio under the same filename. Everything the database believed
    about those files was decided about bytes that are gone."""
    albums_reopened: int = 0
    """Releases put back on the enrichment work list because their audio was
    replaced. Enrichment only — **nothing is queued for download**."""
    files_corrupt: int = 0
    """Files present on disk that hold no playable audio, usually zero-byte.

    Recorded as ``fingerprint_state=corrupt``, which is what puts them on the
    Integrity screen and behind its badge. **Nothing is trashed for it** — the
    quarantine is an explicit press, because moving the file out clears the very
    count that would have told somebody to fetch a replacement."""
    files_recovered: int = 0
    """Files that carried a corruption verdict and parse again, so it was cleared.
    Without this the alarm would never go down after somebody fixed the file."""

    partial: list[ScannedFolderOut] = Field(default_factory=list)
    unmatched: list[ScannedFolderOut] = Field(default_factory=list)
    unknown_artists: list[ScannedFolderOut] = Field(default_factory=list)
    truncated: dict[str, int] = Field(default_factory=dict)
    errors: list[str] = Field(default_factory=list)
    summary: str = ""
    level: MessageLevel = "info"
    """How loudly to announce this report: ``warning`` when the scan could not
    read something or found audio it cannot account for, ``info`` otherwise.
    Filled in by the route, because a stored report loaded from the settings row
    predates the field and defaults honestly to ``info``."""


class LibraryScanStatusOut(BaseModel):
    """Whether a scan is running, plus the summary of the most recent one."""

    running: bool = False
    library_path: str = ""
    nightly: bool = True
    complete_ratio: float = 1.0
    last: LibraryScanOut | None = None


class ImportCandidateOut(BaseModel):
    """One Qobuz search hit offered as a manual choice for an unresolved name."""

    id: str = ""
    name: str = ""
    albums_count: int = 0
    image_url: str | None = None


class ImportReviewOut(BaseModel):
    """An artist folder the importer could not resolve on its own."""

    name: str = ""
    albums: int = 0
    files: int = 0
    path: str = ""
    reason: str = ""
    """``no-exact-match`` or ``not-found``."""
    candidates: list[ImportCandidateOut] = Field(default_factory=list)


class LibraryImportOut(BaseModel):
    """Progress of an artist import, mirroring :class:`app.core.importer.ImportProgress`."""

    running: bool = False
    cancelled: bool = False
    started_at: UtcDatetime | None = None
    finished_at: UtcDatetime | None = None
    total: int = 0
    processed: int = 0
    remaining: int = 0
    percent: int = 0
    followed: int = 0
    already_followed: int = 0
    needs_review: int = 0
    not_found: int = 0
    failed: int = 0
    current: str = ""
    index_now: bool = False
    review: list[ImportReviewOut] = Field(default_factory=list)
    truncated_review: int = 0
    errors: list[str] = Field(default_factory=list)
    aborted_reason: str = ""
    eta_seconds: float = 0.0
    summary: str = ""


class LibraryImportPreviewOut(BaseModel):
    """Who a bulk import would look up, and roughly how long it would take.

    A dry disk scan and nothing else — local, read-only, no Qobuz call — which is
    what makes it safe to render before asking somebody to commit to a run that
    takes minutes. The estimate exists because that run is one rate-limited search
    per artist and the honest answer to "how long?" is often "longer than you
    think"; a number here is what stops the cancel button being the first thing
    anyone presses.
    """

    count: int = 0
    names: list[str] = Field(default_factory=list)
    """Artist folder names as they appear on disk, in the order they would be
    looked up."""
    eta_seconds: float = 0.0


class TrashEntryOut(BaseModel):
    """One batch in the trash, mirroring :class:`app.core.librarian.TrashEntry`."""

    id: str
    trashed_at: UtcDatetime | None = None
    original_path: str = ""
    """Where it came from, and where *Restore* would put it back."""
    reason: str = "deleted"
    album_id: str | None = None
    album_title: str | None = None
    artist_name: str | None = None
    file_count: int = 0
    size_bytes: int = 0


class TrashOut(BaseModel):
    """Everything currently recoverable, newest first."""

    entries: list[TrashEntryOut] = Field(default_factory=list)
    total: int = 0
    size_bytes: int = 0
    path: str = ""
    """``Settings.trash_dir`` — worth showing, since it may be on another disk."""


class TrashSummaryOut(BaseModel):
    """How much is in the trash, without listing it.

    :class:`TrashOut` carries every batch and every batch carries a manifest, so
    an overview screen that wanted three numbers would be paying for the whole
    tidy page. The counts are the same counts — they are taken from the same
    listing — so the overview and the tidy screen can never disagree about how
    many items are recoverable.
    """

    total: int = 0
    size_bytes: int = 0
    path: str = ""


class RefilePlanOut(BaseModel):
    """What re-filing one release would do. Nothing has happened yet."""

    album_id: str
    album_title: str = ""
    artist_name: str = ""
    current_dir: str = ""
    target_dir: str = ""
    renames: list[tuple[str, str]] = Field(default_factory=list)
    moves_directory: bool = False
    blocked: str | None = None
    """Why it cannot be applied — a running download, a path outside the
    library, an occupied destination. ``None`` means it can."""


class LibraryTidyOut(BaseModel):
    """Result of a re-file or re-tag pass over one or many releases."""

    dry_run: bool = False
    considered: int = 0
    changed: int = 0
    """Releases whose *tags* changed, or that a re-file preview would move."""
    moved: int = 0
    """Folders actually renamed. Zero on every pass that did not re-file, and
    counted apart from ``changed`` because an artist re-tag can do both in one
    request: adding the two would report each release twice."""
    described: int = 0
    """Releases whose ``album.nfo``/``artist.nfo`` was written. Also counted
    apart, for the same reason — and it is where a typed ``sort_name`` lands, so
    a client that reports only ``changed`` would say nothing happened."""
    blocked: int = 0
    failed: int = 0
    plans: list[RefilePlanOut] = Field(default_factory=list)
    """Populated by re-file; the preview *is* this list."""
    errors: list[str] = Field(default_factory=list)
    summary: str = ""
    level: MessageLevel = "info"
    """``warning`` when anything failed or was blocked, ``success`` otherwise.
    Decided here because this side holds ``failed`` and ``blocked``; a client
    inferring severity from the summary sentence would be matching on prose."""


class RefileEstimateOut(BaseModel):
    """How many releases a naming template would disturb, library-wide."""

    template: str
    """The template the figures were computed against — echoed back so a client
    can tell a stale answer from a fresh one without trusting its own state."""
    considered: int = 0
    would_refile: int = 0
    moves_directory: int = 0
    """A subset of ``would_refile``: releases whose folder itself moves, rather
    than only having files renamed inside the folder they already occupy."""
    in_place: int = 0
    blocked: int = 0
    frozen: int = 0
    """Releases with ``freeze_path`` set. Reported rather than folded into the
    headline, because a release somebody froze is not a release that would move
    — and saying nothing about it reads as a folder that would."""
    truncated: bool = False
    """True when the walk hit ``limit``: the figures are a floor, not a total."""
    summary: str = ""
    level: MessageLevel = "info"
    """``warning`` when anything was blocked or the walk was truncated. Decided
    here, like :class:`LibraryTidyOut`'s — a client inferring severity from the
    summary sentence would be matching on prose."""


class LibraryImportStartIn(BaseModel):
    """Options for starting an artist import."""

    names: list[str] | None = None
    """Explicit names to look up; a disk scan supplies them when omitted."""
    monitored: bool = True
    monitor_mode: MonitorMode | None = None
    quality_profile: str | None = None
    release_types: list[str] | None = None
    index_now: bool = False
    """Import each artist's back catalogue immediately. Expensive; off by default."""
    limit: int | None = Field(default=None, ge=1, le=10000)


class SettingsOut(BaseModel):
    """Read-only view of the effective configuration for the settings page."""

    app_name: str = "Qobuzarr"
    """Product name. Lived in ``templates.env.globals`` while the shell was
    server-rendered; a client shell needs it in a payload."""
    app_version: str = "0.1.0"

    library_path: str
    data_path: str
    trash_path: str = ""
    """``Settings.trash_dir``. Worth showing beside the library path: deleting is
    a move to here, and here may be another disk — in which case it is a copy of
    the whole album rather than a rename."""
    default_format_id: int
    default_format_label: str = ""
    naming_template: str
    naming_preview: list[str] = Field(default_factory=list)
    """Two example paths the configured template would produce, from a fabricated
    hi-res release. **Empty means the template could not be rendered** — a real
    answer that the settings screen must show as such, not as a blank box."""
    default_monitor_mode: str
    default_quality_profile: str
    default_accepted_release_types: list[str] = Field(default_factory=list)
    qobuz_min_request_interval: float
    qobuz_max_requests_per_hour: int
    indexer_artist_interval: int
    indexer_full_sweep_hours: int
    indexer_enabled: bool
    auto_index_on_follow: bool = True
    """Whether following an artist imports their back catalogue there and then.
    Read-only and rate-limited, but still many calls."""
    auto_download: bool = False
    """The opt-in rule's visible half. False — the default — means indexing marks
    releases wanted and queues nothing; only an explicit press downloads."""
    download_track_delay: float
    download_concurrency: int
    download_max_attempts: int
    upgrade_cleanup: bool = True
    """Whether a *complete* upgrade moves the folder it superseded to the trash.
    The confirm copy on an upgrade depends on it, so the client has to know."""
    library_scan_nightly: bool = True
    library_scan_complete_ratio: float = 1.0

    # -------------------------------------------------------------- integrity
    integrity_enabled: bool = True
    integrity_reverify_fraction: float = 1 / 30
    """Fraction of the library re-hashed each night regardless of the size/mtime
    tripwire — which is how long a file that kept its mtime can hide."""
    host: str
    port: int
    log_level: str
    qobuz_app_id: str = ""
    credentials_ok: bool = False
    app_secret_source: str = "unset"

    # ------------------------------------------------------------ enrichment
    enrichment_enabled: bool = True
    enrichment_sources: list[str] = Field(default_factory=list)
    """The ladder, in the order it runs."""
    enrichment_consensus_threshold: float = 0.51
    enrichment_apply_release_type: bool = True
    enrichment_interval: int = 300
    enrichment_batch_size: int = 25
    enrichment_refresh_days: int = 90
    enrichment_contact: str = ""
    """Shown as set/unset only — it is an email address."""
    musicbrainz_ready: bool = False
    """False means the MusicBrainz rung is gated for want of a contact, not
    broken. Every other source carries on."""
    acoustid_ready: bool = False
    """False means fingerprinting is gated for want of a free API key."""
    enrichment_write_back: bool = True
    """Whether an identification also reaches the files. Off means the ids stay
    in the database, where no music player will ever read them."""
    enrichment_prefer_external_cover: bool = False
    """Whether Deezer's/the Archive's artwork outranks Qobuz's. Off by default —
    Qobuz's image is of the exact release it sells."""
    nfo_enabled: bool = True

    # --------------------------------------------------------------- overlay
    overridable: list[str] = Field(default_factory=list)
    """The keys ``PATCH /api/settings`` accepts, in display order. Everything
    else on this payload is environment-only and must be rendered read-only —
    the client does not keep its own copy of that list, because a copy that went
    stale would offer a control whose write is a 400."""
    origins: dict[str, str] = Field(default_factory=dict)
    """``key -> "env" | "override"`` for every key in ``overridable``, always all
    of them. Precedence is database-over-``.env`` — the switch was pressed more
    recently than the file was edited — so the origin is the only thing that
    tells a reader *why* the value is what it is, and it is what a "Reset to
    .env" affordance is drawn from."""
    pending: list[str] = Field(default_factory=list)
    """Overridden keys whose stored value is not yet the one the running process
    is using. Normally empty. ``enrichment_sources`` lands here when the ladder
    could not be rebuilt on the spot (a tick was mid-flight) and takes effect on
    the next enrichment tick — a control that cannot take effect immediately has
    to say so rather than claim it did."""


class SettingsUpdateIn(BaseModel):
    """A partial write of the overridable settings.

    Two halves, because a settings screen has two verbs and they are not the same
    write. ``values`` sets an override; ``reset`` **deletes** the row, which
    restores whatever ``.env`` says — there is no stored copy of the environment
    value to write back, and there must not be one.

    Both are checked against :data:`app.config.OVERRIDABLE_SETTINGS`, and a key
    outside it is a **400**, not a silent no-op: a switch that does nothing and
    says nothing is the one failure a settings screen cannot recover from. That
    is also why this is a free-shaped map rather than seven optional fields —
    Pydantic would drop an unknown key without a word.

    A value may arrive in whatever shape the control produces: ``true`` or
    ``"true"`` for a switch, a list or a comma-separated string for the
    enrichment ladder. It is parsed by the key's own rule and stored in one
    canonical text form, so what comes back out is what went in.
    """

    values: dict[str, Any] = Field(default_factory=dict)
    """``key -> value`` to override. An empty map writes nothing."""
    reset: list[str] = Field(default_factory=list)
    """Keys to un-override, returning them to ``.env``."""


# ---------------------------------------------------------------------------
# Requests and generic responses
# ---------------------------------------------------------------------------
class ArtistCreateIn(BaseModel):
    """Payload for following a new artist."""

    artist_id: str
    name: str | None = None
    monitored: bool = True
    monitor_mode: MonitorMode | None = None
    quality_profile: str | None = None
    accepted_release_types: list[str] | None = None
    search_now: bool = False


class ArtistUpdateIn(BaseModel):
    """Partial update of an artist's monitoring settings."""

    monitored: bool | None = None
    monitor_mode: MonitorMode | None = None
    quality_profile: str | None = None
    accepted_release_types: list[str] | None = None
    #: Want releases this artist only guests on. Belongs here rather than
    #: beside the credit filter because it is exactly what the others are — a
    #: field `desired_status` reads, so changing it moves the existing backlog
    #: through the same `apply_monitoring_to_backlog` call.
    include_guest_appearances: bool | None = None


class ArtistTagsIn(BaseModel):
    """Hand-edited identity for one artist: what goes into files and folders.

    Deliberately **not** part of :class:`ArtistUpdateIn`, which is monitoring
    settings. These values are a different size of decision — they name
    every folder this artist owns and are written into every file of theirs on
    the next re-tag — and one of them commits the session on its own (see
    ``mb_artist_mbid``), which is not a surprise the settings PATCH should have
    to carry.

    ``None`` means **leave alone**, as everywhere else. An empty string is a
    real value where it can be one: it clears ``sort_name`` back to whatever
    MusicBrainz supplies. It is refused for the other two strings, because a
    blank name would name a folder nothing and a blank id cannot be told apart
    from "I did not mean to touch this". ``aliases: []`` is likewise a real
    value and clears the list — the single-artist rule, not the bulk one.

    Unknown keys are **rejected** rather than ignored, and that refusal is what
    ``aliases`` was measured against: it was a 422 until it earned a column on
    ``artists``, because silently dropping the list somebody typed is the one
    outcome worse than not offering the field. Anything still unrecognised gets
    the same answer.
    """

    model_config = ConfigDict(extra="forbid")

    name: str | None = None
    """Display name. Written as ``ARTIST``/``ALBUMARTIST`` by a re-tag and
    rendered into the ``{artist}`` token by a re-file."""
    sort_name: str | None = None
    """How the artist files — ``"Frahm, Nils"``. Stored on ``artists``, not on
    ``artist_metadata``, so nothing re-derives it; reaches disk as
    ``<sortname>`` in ``artist.nfo``."""
    aliases: list[str] | None = Field(default=None, max_length=64)
    """Alternative spellings, one per entry. ``None`` leaves them alone; ``[]``
    is a real value and clears the list. Entries are stripped, blanks and exact
    duplicates dropped, order kept.

    Recorded and read back and **nothing else**: no file tag (Picard writes no
    such field, and a tag spelled differently is a tag no other tool reads), no
    ``artist.nfo`` element, and no input to name matching — widening the
    accepted name set with a string a person typed would let a name *select* a
    candidate. See ``Artist.aliases_json``."""
    mb_artist_mbid: str | None = None
    """MusicBrainz artist id, or a link containing one.

    Recorded through the same path as the review screen's Identify, so it is
    marked ``manual`` and ``_MANUAL_OWNS`` stops the next automatic derivation
    putting a different id back. That path commits the whole edit itself."""


class CreditClusterOut(BaseModel):
    """One credited person under a Qobuz artist id, and what they account for.

    A Qobuz artist id is not always one artist. Id ``322476`` "Boaz" holds
    releases by at least eleven people, all stamped ``main-artist: 322476``, so
    the release payload cannot tell them apart and neither can Deezer, which
    merges them the same way. The track credits can: ``Boaz Roelevink`` and
    ``Boaz Ndarivoi`` are different lines in Qobuz's own ``performers`` string.

    This is one such line, with enough beside it for somebody to recognise
    whose it is. Nothing here selects anything — a person reads it and picks.
    """

    name: str
    """The credit exactly as Qobuz spells it, e.g. ``"Boaz Roelevink"``."""
    releases: int
    """How many of this artist's releases carry it."""
    accepted: bool
    """Whether it is currently in the artist's credit filter."""
    sample_titles: list[str] = Field(default_factory=list)
    """A few release titles carrying it, newest first — what makes the name
    recognisable to somebody who knows the music but not the credit."""


class ArtistCreditsOut(BaseModel):
    """Every credit found under one artist id, and what is unattributed."""

    artist_id: str
    artist_name: str
    analysed: int
    """Releases whose credits have been read."""
    total: int
    """Releases this artist has. ``analysed < total`` means the pass has not
    finished, not that the remainder carry no credits."""
    unattributed: int
    """Analysed releases whose every credit was the bare artist name. These
    are refused while a filter is on — there is nothing to match them by — and
    the per-release monitor toggle is the way back for any one of them."""
    filter_active: bool
    clusters: list[CreditClusterOut] = Field(default_factory=list)


class ArtistCreditFilterIn(BaseModel):
    """The credits a person accepts as this artist.

    Deliberately its own endpoint rather than a field on
    :class:`ArtistUpdateIn`, for the reason :class:`ArtistTagsIn` is: this is a
    different size of decision. It re-derives the status of every release the
    artist has, and it is made while looking at a list of credits, not while
    ticking a monitoring box.

    An **empty list clears the filter** — back to wanting everything the id
    offers. It does not mean "accept nothing": a filter that refused every
    release would look identical to a broken artist, and there is already a
    way to say that (``monitor_mode='none'``).
    """

    model_config = ConfigDict(extra="forbid")

    credits: list[str] = Field(default_factory=list)


class ArtistBulkUpdateIn(BaseModel):
    """Apply one set of monitoring changes to many artists at once.

    Every field is optional and ``None`` means **leave alone** — that is the
    whole point of a bulk edit. Setting ``monitor_mode`` for fifty artists must
    not silently reset the release types of all fifty.

    ``release_types_action`` decides what ``release_types`` means:

    ``set``
        Replace each artist's accepted types with exactly this list.
    ``add``
        Union: start accepting these *in addition* to what is already there.
    ``remove``
        Difference: stop accepting these, keep the rest.

    ``add``/``remove`` exist because "also grab singles for these twenty
    artists" should not require knowing what each of them already accepts.
    """

    artist_ids: list[str] = Field(default_factory=list)
    monitored: bool | None = None
    monitor_mode: MonitorMode | None = None
    include_guest_appearances: bool | None = None
    release_types: list[str] | None = None
    release_types_action: Literal["set", "add", "remove"] = "set"


class AlbumUpdateIn(BaseModel):
    """Partial update of one release: its state, and the three per-album switches.

    **Partial means partial.** Every field is ``None`` by default and ``None``
    means *leave alone*, exactly as on :class:`ArtistUpdateIn`. A serialiser that
    turns an untouched switch into ``false`` unpins, unfreezes and unmutes every
    release it is sent for, which is the same bug that once silently unmonitored
    a whole selection of artists.

    Two routes take this model and they read ``monitored`` differently, which is
    why the difference is written down here:

    * ``PATCH /api/albums/{id}`` — a partial update. ``monitored=None`` leaves
      the flag alone.
    * ``POST /api/albums/{id}/monitor`` — a toggle. An **empty body** flips
      ``monitored``, which is what every album row's switch sends. That endpoint
      refuses a body carrying any of the three switches rather than applying it,
      because "set ``pin_tags``" and "flip ``monitored``" arriving in one request
      is not a partial update, it is two different verbs — and the one that would
      silently happen is the one nobody asked for.
    """

    monitored: bool | None = None
    status: AlbumStatus | None = None
    pin_tags: bool | None = None
    """Keep enrichment's background write-back off this release's file tags."""
    freeze_path: bool | None = None
    """Keep re-filing away from this release's folder."""
    mute_integrity: bool | None = None
    """Keep the corruption alarm — the quarantine and its count — off this
    release. The measurement and the release's own reported state are
    unaffected."""

    @property
    def flag_fields(self) -> dict[str, bool]:
        """The switches this body actually sets, ``{name: value}``.

        One place decides what "a flag was sent" means, so the applier and the
        toggle endpoint's refusal cannot drift into disagreeing about it.
        """
        return {
            name: value
            for name in ("pin_tags", "freeze_path", "mute_integrity")
            if (value := getattr(self, name)) is not None
        }


class EnrichmentReviewOut(ORMModel):
    """One entity the matchers refused to resolve, for the review list."""

    entity_type: str
    entity_id: str
    source: str
    state: str
    name: str
    artist_id: str | None = None
    artist_name: str | None = None
    reason: str | None = None
    attempts: int = 0
    last_attempt_at: UtcDatetime | None = None
    is_actionable: bool = False
    """False for ``not_found``: the upstream has no such record yet, which is a
    matter of waiting rather than of deciding."""
    identify_sources: list[str] = Field(default_factory=list)
    """Which sources this row can be handed an id for — empty when none can.

    The picker needs a source to search against, and the old page took it from
    the row's own ``source`` while separately deciding whether to show the button
    at all from ``is_actionable``. Two halves of one rule, and the half that
    encodes *which* sources accept an id (``enricher._IDENTIFIABLE_SOURCES``:
    MusicBrainz and Deezer have ids a person could type; the Cover Art Archive is
    keyed by an MBID, Wikidata by a QID, AcoustID by the audio) was being
    re-derived by whoever rendered the row. Publish it instead: a client that
    keeps its own copy renders a picker whose POST is a 400, or hides one that
    would have worked.

    A list rather than a string because it is the *set of pickers to offer*, and
    an empty list is the whole answer for a row nobody can act on — no separate
    check, no second rule to keep in step."""
    state_explanation: str = ""
    """One server-authored sentence saying what this state means.

    ``ambiguous`` and ``no_key`` are the matcher's own vocabulary, and what they
    mean is a fact about how the matcher refuses — that more than one upstream
    record fit and none of them is trustworthy, or that there was nothing exact
    to match on in the first place. The old copy for both lived in a template, so
    the sentence and the code that produced the state could drift without
    anything noticing. ``reason`` stays what it was: free-form detail about this
    particular row, from whichever rung gave up on it."""
    suggested_release_type: str | None = None
    """What pressing **Accept** on this row would apply, or ``null``.

    Non-null means the system is holding a proposal — a release type the sources
    agreed on that this album is not carrying — and Accept will write it.
    ``null`` means it is holding nothing, and Accept will open the identify
    picker instead. Published so the button can be *labelled* with what it is
    about to do, which is the whole reason Accept is safe: the tempting
    implementation is "search the upstream and take the top hit", and that is
    the one thing ``app.enrich.matching``'s "a name may reject a candidate,
    never select one" forbids. Always ``null`` for artist rows — consensus
    proposes nothing about an artist."""


class EnrichmentRejectIn(BaseModel):
    """A person saying "there is no answer here" about one work item.

    The counterpart to :class:`EnrichmentIdentifyIn`. ``source`` is required and
    unguessable from the URL because a work item is one (entity, source) pair:
    rejecting MusicBrainz for a release says nothing about Deezer, and a body
    that omitted it would have to mean *all* of them, which is a much larger
    press than the button that sends it.
    """

    model_config = ConfigDict(extra="forbid")

    source: str
    reason: str | None = None
    """Why, in the person's own words. Optional, stored where the matcher's own
    reason was, and shown if the row is ever looked at again."""


class EnrichmentAcceptOut(BaseModel):
    """What **Accept** did, or why it did nothing.

    ``applied=False`` is a success, not a failure: it means the system was
    holding no proposal for this entity, and ``identify_sources`` says which
    pickers to open instead. That distinction is the entire safety argument for
    the endpoint — see ``app.core.enricher.AcceptOutcome``.
    """

    entity_type: str
    entity_id: str
    applied: bool = False
    proposal: str | None = None
    """Which held proposal was applied — ``"release_type"``, or ``null`` when
    nothing was."""
    previous_value: str | None = None
    applied_value: str | None = None
    """What the field held before and what it holds now. Both ``null`` when
    nothing was applied, so a client cannot render a change that did not
    happen."""
    identify_sources: list[str] = Field(default_factory=list)
    """The pickers to offer when there was nothing to apply. Same rule as
    ``EnrichmentReviewOut.identify_sources`` — only sources that take an id a
    person could type."""
    message: str = ""
    level: MessageLevel = "info"


class EnrichmentSourceStatusOut(BaseModel):
    """One rung of the ladder: whether it runs, and if not, what it is waiting for.

    ``gates`` says only *that* a rung is gated, and for a year the only place the
    missing setting was ever named was a hard-coded sentence in a template. That
    makes the answer to "why is nothing being fingerprinted?" a thing the client
    knows and the server does not, which is backwards — the gate is the server's
    own configuration. So the reason is authored here, names the setting exactly
    as ``.env`` spells it, and travels with the flag.

    ``ready`` is not ``enabled``: a source can be in ``ENRICHMENT_SOURCES`` and
    still make no request because it has nothing to make one with.
    """

    name: str
    enabled: bool = False
    """Present in the configured ladder (``ENRICHMENT_SOURCES``)."""
    ready: bool = False
    """Enabled **and** not gated — this rung will actually reach the upstream."""
    gated_on: str | None = None
    """The name of the missing thing: a setting (``ACOUSTID_API_KEY``,
    ``ENRICHMENT_CONTACT``) or a binary (``fpcalc``). ``None`` when nothing is
    missing."""
    gated_reason: str | None = None
    """One server-authored sentence saying what to do about it."""
    identifiable: bool = False
    """Whether a person can hand this source an id by hand. False for the rungs
    keyed by an id an earlier rung established — there is nothing to type and no
    column to put it in, which is precisely the rule a client would otherwise
    re-implement."""
    states: dict[str, int] = Field(default_factory=dict)
    """``{enrichment_state: count}`` for this source, counted over the library
    only — the same scope the review list uses, so the two agree."""


class EnrichmentAutonomyOut(BaseModel):
    """How much identification is happening without a person — a partition.

    Two invariants, and both are the point of the model:

    * **the five buckets sum to** ``entities``. It is a partition of the
      entities in the library, one bucket each, chosen by the strongest claim on
      that entity's attention — see
      :func:`app.core.enricher.enrichment_autonomy_counts` for the precedence
      and why it runs that way round.
    * **there is no confidence figure here and no threshold.** Nothing in this
      model may be compared against a bar. ``app.enrich.matching`` is exact or
      nothing and :func:`app.enrich.coverage.solve_album` deliberately has no
      highest-coverage-wins branch, so a number to auto-accept above would be
      that refused tie-break wearing a percentage — and the value it accepted
      gets written into every file on disk as ``MUSICBRAINZ_ALBUMID``.
    """

    entities: int = 0
    """The denominator: ``scope.albums + scope.artists``, what is on disk.
    Never the catalogue — that reports a finished library as 1%."""
    automatic: int = 0
    """Identified with nobody involved: at least one ``ok`` rung and no rung
    making a stronger claim."""
    waiting_person: int = 0
    """A decision is genuinely available to a human, by
    :func:`app.core.enricher.review_picker_for` — the same rule the review list
    and the nav badge use, delegation included."""
    waiting_input: int = 0
    """Waiting on an input rather than on anybody: a refusal no picker settles,
    or a gated rung. ``_rearm_stranded()`` is what notices the input arriving,
    so there is nothing to press."""
    dismissed: int = 0
    """A person said "there is no answer here". The one state no matcher can
    produce, so the figure is a count of decisions actually made."""
    unstarted: int = 0
    """Seeded but not yet attempted, or not seeded at all. An in-scope entity
    the seeder has not reached is unstarted, never missing."""


class EnrichmentStatusOut(BaseModel):
    """Coverage and health for the enrichment page."""

    enabled: bool = True
    sources: list[str] = Field(default_factory=list)
    paused_reason: str | None = None
    last_run_at: UtcDatetime | None = None
    last_result: dict[str, Any] | None = None
    gates: dict[str, bool] = Field(default_factory=dict)
    """Which rungs are configured. False means gated, not broken.

    Kept as the flat pair it has always been; ``source_status`` is the same
    information with the *reason* attached, and is what a new client should
    read."""
    source_status: list[EnrichmentSourceStatusOut] = Field(default_factory=list)
    """Every source in the vocabulary — the configured ladder in its own order
    first, then the rest — with its gate, its reason and its state counts.

    Deliberately not folded into ``sources``: that field is the ladder as a list
    of names, which is what the enricher itself reports and what the existing
    page iterates. Two shapes of one fact is the thing this codebase avoids, so
    both are produced from the same call rather than counted twice."""
    states: dict[str, dict[str, int]] = Field(default_factory=dict)
    """``{source: {state: count}}``, counted over the library only."""
    scope: dict[str, int] = Field(default_factory=dict)
    """The denominator every other figure here is against: ``albums`` and
    ``artists`` are what is on disk — all enrichment is for — with
    ``catalogue_albums``/``catalogue_artists`` beside them, because "32 of 3313"
    is the difference between coverage that looks broken and coverage that is
    complete."""
    autonomy: EnrichmentAutonomyOut = Field(default_factory=EnrichmentAutonomyOut)
    """The same library, partitioned by *who* the work is waiting on. ``states``
    counts rows per source; this counts **entities**, once each, which is the
    only shape a share of the library can be computed from."""
    review_total: int = 0


class EnrichmentIdentifyIn(BaseModel):
    """A human saying "it is this one" — the escape hatch from the review list."""

    source: str
    external_id: str
    """The upstream id, or a link containing it. A pasted browser URL is
    accepted, because that is what someone looking at the record actually has."""


class EnrichmentCandidateOut(BaseModel):
    """One upstream record offered for a person to pick.

    Everything but ``external_id`` is here to be *read*. The review list used to
    ask for a bare identifier, which only the people who did not need it could
    supply; these are the fields that let someone recognise their own record.
    """

    external_id: str
    title: str
    subtitle: str | None = None
    detail: str | None = None
    disambiguation: str | None = None
    image_url: str | None = None
    url: str | None = None


class EnrichmentCandidatesOut(BaseModel):
    """Candidates for one entity on one source, with the query that found them."""

    entity_type: str
    entity_id: str
    source: str
    query: str = ""
    """What was searched for — echoed back so the UI can show it in the box and
    let it be edited. The name in the library is the natural first guess and is
    also, quite often, exactly why nothing matched."""
    items: list[EnrichmentCandidateOut] = Field(default_factory=list)


class MessageOut(BaseModel):
    """Generic acknowledgement returned by action endpoints."""

    ok: bool = True
    message: str = ""
    level: MessageLevel = "info"
    """The severity the acknowledgement should be shown at.

    Deleting a release, cancelling a download and emptying the trash are all
    ``ok=True`` and none of them is good news; a bulk edit that left three artists
    accepting no release types at all is the same shape. ``ok`` says the request
    was honoured, this says how it went, and only the server has the counts the
    answer depends on."""
    detail: dict[str, Any] | None = None


class PageOut(BaseModel):
    """Pagination envelope used by list endpoints."""

    total: int = 0
    limit: int = 50
    offset: int = 0
    sort: str | None = None
    order: Literal["asc", "desc"] = "asc"
    unfiltered_total: int | None = None
    """The same count with the *filters* dropped — the difference between "no
    release matches that search" and "this artist has no releases".

    A list that returns only its filtered total cannot tell those apart, so the
    empty state has to be written to cover both and ends up saying neither. What
    is not a filter stays applied: the artist an album list is scoped to, and
    enrichment's ``library_scope``, are the subject of the query rather than a
    narrowing of it.

    ``None`` means the endpoint takes no filters, so there is nothing to drop."""


# ---------------------------------------------------------------------------
# List envelopes
#
# These lived in ``app/api/routes_api.py``, beside the routes that return them,
# which reads well and generates badly: a type generator pointed at this module
# sees every entity and not one of the shapes they actually arrive in, so the
# client ends up with six hand-written wrappers that are right until a field is
# added here. They are declarations, not routing, and this is where declarations
# live. ``routes_api`` re-exports the names, so nothing that imported them from
# there has to change.
# ---------------------------------------------------------------------------
class ArtistListOut(PageOut):
    """Paged list of followed artists."""

    items: list[ArtistOut] = Field(default_factory=list)


class AlbumListOut(PageOut):
    """Paged list of albums."""

    items: list[AlbumOut] = Field(default_factory=list)


class WantedListOut(AlbumListOut):
    """The backlog, plus the number the bulk action would actually queue."""

    queueable_total: int = 0
    """How many releases *Download all* would put on the queue.

    Not ``total``, and the difference is the whole reason this field exists.
    ``POST /api/wanted/download`` queues every **monitored** release whose status
    is ``wanted`` — it does not look at the filters this list was drawn with, so
    a page filtered to one failed release still queues the entire backlog behind
    it. A button labelled from ``total`` therefore promises one download and
    delivers however many there really are, which is the sort of mistake nobody
    notices until the disk fills.

    Invariant: this number does not move when ``q``, ``status`` or ``monitored``
    change. If it ever does, it has been computed from the filtered query and
    the label is lying again."""


class QueueListOut(PageOut):
    """Paged list of queue entries."""

    items: list[QueueItemOut] = Field(default_factory=list)


class EnrichmentReviewListOut(PageOut):
    """Paged list of entities the matchers refused to resolve."""

    items: list[EnrichmentReviewOut] = Field(default_factory=list)


class ActivityListOut(PageOut):
    """Paged list of activity entries."""

    items: list[ActivityOut] = Field(default_factory=list)


class ArtistDetailOut(ArtistOut):
    """One artist plus their known releases."""

    albums: list[AlbumOut] = Field(default_factory=list)


class ArtistFollowOut(ArtistOut):
    """The artist a follow request produced, and whether it was new.

    Following is idempotent — asking twice updates rather than fails — which is
    what makes it usable from the import review list, where the same name can
    reach it from two different rows. The cost is that the response alone cannot
    say whether anything happened, and the two sentences the caller has to choose
    between ("Now following X." against "X was already followed.") are exactly
    that distinction.

    A field rather than a status code: the request succeeded either way, and
    a client should not have to read 201 against 200 to write a sentence.
    """

    created: bool = False
    """False when the artist was already followed and this request updated them."""


class NavCountsOut(BaseModel):
    """The five sidebar badges.

    Two of these are not table counts and cannot be derived from anything else
    on the wire: ``trash`` lists directories under ``Settings.trash_dir`` (an
    ``OSError`` reads as nothing there, because a nudge that raises is worse than
    a nudge that is late), and ``enrichment_review`` is counted over
    ``enricher.in_library()`` so the badge and the review list agree — a badge
    promising more rows than the page can show is worse than no badge.

    ``wanted`` is ``deps.MISSING_STATUSES`` (``wanted`` + ``failed``) **and**
    monitored. That is deliberately not the ``wanted + queued + downloading``
    figure ``LibraryStatsOut.wanted_albums`` reports: this one is the backlog you
    could act on, that one is everything not yet on disk. Both are real; keep
    them apart.
    """

    artists: int = 0
    wanted: int = 0
    queue: int = 0
    trash: int = 0
    enrichment_review: int = 0


class TagMapRowOut(BaseModel):
    """One row of the Structure & tags mapping table.

    Derived, never authored: ``vorbis_field``/``tag_key`` come from
    :data:`app.core.tagger.VORBIS_FIELDS`, the origin from
    :data:`app.core.nfo.ENRICHMENT_TAGS` and
    :data:`app.core.tagger.QOBUZ_TAG_FIELDS`, and the conflict verdict from
    :data:`app.enrich.merge.WRITABLE_FIELDS` / ``NEVER_WRITABLE``. A hand-written
    copy of this table is worse than no table, because it goes stale in exactly
    the direction nobody notices — the screen keeps describing the tagger the
    program used to have.
    """

    vorbis_field: str
    """The Vorbis comment ``_tag_flac`` writes, e.g. ``MUSICBRAINZ_ALBUMID``.
    Unique across the list, so it is also the row's identity."""
    tag_key: str
    """``build_tags``' logical key — several Vorbis fields share one (``LABEL``
    and ``ORGANIZATION`` are both ``label``)."""
    id3_frame: str | None = None
    """What an MP3 gets for the same value: ``TIT2``, ``TXXX:Acoustid Id``,
    ``UFID:http://musicbrainz.org``, ``TRCK (number/total)``. ``None`` means MP3
    files carry nothing for it, which is a real answer and not a gap."""
    origin: str
    """``qobuz`` / ``enrichment`` / ``derived``."""
    source_field: str
    """The column the value is read from — ``album_metadata.mb_release_mbid``,
    ``albums.upc``. Deliberately the *store* rather than the rung: which rung
    matched is a per-release fact (``mb_match_method``), shown on the album and
    Identify screens, and a per-field constant claiming otherwise would be wrong
    the moment the ladder is reordered."""
    conflict: str
    """``qobuz`` / ``enrichment`` / ``consensus`` / ``never`` / ``derived`` — what
    may overwrite this value. ``consensus`` matches no row today, because
    ``WRITABLE_FIELDS`` holds ``release_type``, an ``Album`` column rather than a
    tag key; it lights up by itself the day a tag key enters that set."""
    conflict_note: str
    """The rule in a sentence, authored server-side for the same reason as
    ``_GATE_REASONS``: the rule is this process's own, and a client that
    re-implements ">51%" drifts from ``merge.consensus`` silently."""


class ConsensusRuleOut(BaseModel):
    """``merge.consensus()``'s rule, published once rather than per row."""

    threshold: float = 0.51
    """Read from the function's own signature default, so the number on the
    screen cannot disagree with the number in the comparison."""
    tie_keeps: str = "qobuz"
    writable_fields: list[str] = Field(default_factory=list)
    """:data:`app.enrich.merge.WRITABLE_FIELDS`, sorted."""
    never_writable: list[str] = Field(default_factory=list)
    """:data:`app.enrich.merge.NEVER_WRITABLE`, sorted — the exclusions stated
    rather than left to be inferred from an absence."""
    note: str = ""


class MetaOut(BaseModel):
    """Every enum vocabulary and label map the client needs, in one payload.

    These changed only when the server did, so the old UI baked them into
    ``templates.env.globals`` and the templates iterated them directly. A
    hand-typed copy in the client is the same list maintained twice, and the
    failure is silent: a release type added here renders as a missing ``<option>``
    there, and a chip for a status the client has never heard of falls back to
    whatever the default branch does. Fetch this once and cache it forever — it
    only changes on deploy.

    ``enrichment_sources`` is the whole vocabulary, not the configured ladder.
    The ladder is the *setting's* order and lives in
    ``SettingsOut.enrichment_sources``; this list is what the words can be.
    """

    app_name: str = "Qobuzarr"
    app_version: str = "0.1.0"
    release_types: list[str] = Field(default_factory=list)
    monitor_modes: list[str] = Field(default_factory=list)
    album_statuses: list[str] = Field(default_factory=list)
    queue_states: list[str] = Field(default_factory=list)
    activity_levels: list[str] = Field(default_factory=list)
    track_statuses: list[str] = Field(default_factory=list)
    track_origins: list[str] = Field(default_factory=list)
    """``download`` / ``scan``. Provenance is stated, never inferred from the
    presence or the shape of a track row."""
    enrichment_sources: list[str] = Field(default_factory=list)
    enrichment_entities: list[str] = Field(default_factory=list)
    enrichment_states: list[str] = Field(default_factory=list)
    review_states: list[str] = Field(default_factory=list)
    """The subset of ``enrichment_states`` the review list is *about* — the ones
    a person can actually decide, and therefore the ones ``reject`` will accept.

    A rule, not a vocabulary, which is exactly why it is published: the client
    used to carry its own ``['ambiguous', 'no_key']`` beside the server's
    :data:`app.core.enricher.REVIEW_STATES`, and the two disagreeing is a filter
    tab offering a state the endpoint refuses, or a row on the list with no
    button that fits it. Note what is deliberately absent — ``not_found`` is
    waiting on the upstream rather than on anybody, and ``rejected`` is the row
    somebody has already taken off this list."""
    identifiable_sources: list[str] = Field(default_factory=list)
    """The sources a person can hand an id to. The rest are keyed by an id an
    earlier rung established, so there is nothing to type and no column to put it
    in — which is exactly the rule a client would otherwise re-implement."""
    integrity_states: list[str] = Field(default_factory=list)
    fingerprint_states: list[str] = Field(default_factory=list)
    setting_origins: list[str] = Field(default_factory=list)
    """``env`` / ``override`` — the two answers ``SettingsOut.origins`` gives for
    an overridable setting. Published for the same reason as every other list
    here, and for one more: ``origins`` is declared ``dict[str, str]``, so this
    pair never becomes an OpenAPI enum and a client's copy of it would be
    unpoliced by ``tests/test_wire_contract.py``."""
    format_labels: dict[str, str] = Field(default_factory=dict)
    """``{"7": "FLAC 24bit 96kHz"}``. Keys are stringified ``format_id``s,
    because JSON object keys are strings and pretending otherwise gives the
    client an integer-keyed map it cannot actually have."""
    tag_map: list[TagMapRowOut] = Field(default_factory=list)
    """Every Vorbis field :mod:`app.core.tagger` writes, what fills it, and what
    may overwrite it — in write order. DERIVED from ``tagger.VORBIS_FIELDS``,
    ``nfo.ENRICHMENT_TAGS`` and ``merge.WRITABLE_FIELDS``, never hand-written,
    because a table that drifts from the tagger is worse than no table at all."""
    consensus_rule: ConsensusRuleOut = Field(default_factory=ConsensusRuleOut)
    """The >51% rule stated once, instead of once per row of ``tag_map``."""


class ConsensusVoteOut(BaseModel):
    """What one source said about one field."""

    source: str
    value: Any | None = None


class ConsensusFieldOut(BaseModel):
    """The vote record for one field: what won, and what the losers said.

    Kept whole rather than reduced to the winner, because the split is the
    interesting case. ``value`` is ``None`` when no majority formed — sources
    disagreed and nothing was written — and that is what the "no majority" chip
    renders. The >51% rule itself, and the fact that Qobuz always votes with its
    *original* value, live in :mod:`app.enrich.merge` and stay there.
    """

    value: Any | None = None
    agreed: int = 0
    total: int = 0
    share: float = 0.0
    majority: bool = False
    """True exactly when ``value`` was decided. Not ``agreed / total > 0.5``:
    a joint-first placing is a tie however many sources voted, and the tie test
    is the matcher's to make."""
    votes: list[ConsensusVoteOut] = Field(default_factory=list)


class ReleaseGroupRefOut(BaseModel):
    """Which record a release belongs to, and what identified it."""

    key: str = ""
    """The record key: a MusicBrainz release-group MBID when one is known, the
    normalised title otherwise. Both kinds round-trip through
    ``GET /api/release-groups/{key}``."""
    title: str = ""
    mb_release_group_mbid: str | None = None
    """``None`` means the grouping is by normalised title — nothing has
    identified this record yet. Say that rather than showing a bare key."""


class ReleaseGroupOut(ReleaseGroupRefOut):
    """Every edition of one record, best first.

    The keying rule is asymmetric (two albums that both carry an MBID are one
    record only if the MBIDs match; an album with none joins the record whose
    normalised title it shares; a title claimed by two MBID-keyed records leaves
    the unmatched album on its own) and it is **server-side**. Only albums on
    disk are ever enriched, so the copy you own carries an MBID and its
    catalogue-only twins never will: a client grouping by comparing keys files
    them apart permanently, and the artist page and the release page then
    disagree about how many editions exist. Link to this endpoint with whatever
    key you hold and let the server resolve it.
    """

    artist_id: str | None = None
    artist_name: str | None = None
    total: int = 0
    editions: list[AlbumOut] = Field(default_factory=list)
    """Ordered by ``indexer.edition_rank``: track count before audio quality, so
    a one-track hi-res promo never outranks the album it was cut from. Best
    first — ``editions[0]`` is "best" only because the server sorted it."""


class AlbumDetailOut(BaseModel):
    """One release with everything its own screen needs, in one request.

    The three parts are fetched together because they are read together and two
    of them cannot be assembled client-side: the edition list needs the
    whole-catalogue keying pass, and the vote record is a column nothing else
    returns.
    """

    album: AlbumOut
    release_group: ReleaseGroupRefOut = Field(default_factory=ReleaseGroupRefOut)
    editions: list[AlbumOut] = Field(default_factory=list)
    """Includes the subject. One edition means there is nothing to show."""
    consensus: dict[str, ConsensusFieldOut] = Field(default_factory=dict)


class ArtistStatsOut(BaseModel):
    """One artist's release roll-up, per status.

    ``counts`` is zero-filled for every :class:`~app.models.AlbumStatus`, which
    is not tidiness: these labels drive a status ``<select>``, and an option
    whose count is simply absent renders as a hole.

    ``wanted_count`` is ``wanted + queued`` — the artist page's own definition,
    and the third of three that exist in this codebase. The other two are
    ``deps.MISSING_STATUSES`` + monitored (the backlog and the nav badge) and
    ``wanted + queued + downloading`` (``LibraryStatsOut``). They are stated
    rather than derived so nobody invents a fourth.
    """

    artist: ArtistOut
    counts: dict[str, int] = Field(default_factory=dict)
    total_albums: int = 0
    wanted_count: int = 0
    downloaded_count: int = 0
    on_disk_ratio: float | None = None
    """``downloaded / total``, or ``None`` when the artist has no releases at
    all — a ratio of nothing is not zero."""


# ---------------------------------------------------------------------------
# Integrity: what is actually on disk
# ---------------------------------------------------------------------------
class IntegrityReportOut(BaseModel):
    """What one verification pass found, and what it wrote.

    Mirrors :class:`app.core.scheduler.IntegrityReport`. ``states`` counts every
    track by the verdict it was given **before** anything was written, which is
    the only moment those counts mean anything — the pass re-baselines a
    ``retagged`` file in the same breath, so counted afterwards it would report as
    ``verified``: true of the row, and useless to the person who asked what
    changed. This is therefore the only place ``retagged`` and ``replaced`` are
    ever visible; see :class:`TrackIntegrityOut` for why they are not stored.
    """

    checked: int = 0
    """Rows with a file to measure that this pass looked at."""
    hashed: int = 0
    """Files actually opened and hashed. Below ``checked`` only by the ones that
    were not there."""
    baselined: int = 0
    """Rows that had no recorded hash and now have one."""
    albums_restamped: int = 0
    albums_reopened: int = 0
    """Releases put back on the enrichment work list because their audio changed.
    Enrichment, never the download queue."""
    elapsed: float = 0.0
    """Wall-clock seconds, which is almost entirely disk."""
    applied: bool = True
    """False for a dry run: everything was measured, nothing was written."""
    states: dict[str, int] = Field(default_factory=dict)
    """``{integrity_state: count}``, zero-filled for every state."""
    finished_at: UtcDatetime | None = None
    """When this report was stored. Absent on a report that has just been
    returned from the run that produced it."""


class IntegrityStatusOut(BaseModel):
    """The Integrity screen's landing payload.

    Two histograms live here and they answer different questions, which is the
    thing to get right. ``states`` is what the **rows** currently record, so it
    only ever holds ``unknown``, ``verified`` and ``missing``: a pass re-baselines
    whatever it classifies, so no row is left saying it disagrees with its file.
    ``last_result.states`` is what the last **pass** saw, and that is where
    ``retagged`` and ``replaced`` appear.

    ``never_baselined`` is the number the *Baseline library* action exists for,
    and it is not a fault: until something measures a file, the database makes no
    claim about it and there is nothing for the file to contradict. Rendering it
    as tampering is the mistake that would make this feature cry wolf on the day
    it shipped.
    """

    enabled: bool = True
    """``INTEGRITY_ENABLED``. False means the nightly rotation never runs; an
    explicit pass is refused with a 503 rather than run anyway."""
    reverify_fraction: float = 1 / 30
    """Share of the library re-hashed each night regardless of the size/mtime
    tripwire — so its reciprocal is how long a file that kept its mtime can
    hide."""
    tracks_total: int = 0
    """Rows that claim to have a file: ``downloaded`` with a path. The
    denominator for everything else here."""
    states: dict[str, int] = Field(default_factory=dict)
    """Recorded verdicts, zero-filled for every
    :class:`app.core.integrity.IntegrityState`."""
    never_baselined: int = 0
    """``content_hash IS NULL``. What *Baseline library* would measure."""
    corrupt_files: int = 0
    """Files ``fpcalc`` ran on, finished, and could not decode — and that are
    still on disk. The only actionable verdict in this payload.

    Releases with ``mute_integrity`` set are **not** counted here, because this
    number is what the quarantine button would move and that button skips them:
    a figure the action cannot act on is an offer to do nothing. They are counted
    in :attr:`corrupt_muted` instead."""
    corrupt_muted: int = 0
    """Unplayable files on releases the user has muted.

    Published rather than swallowed. Muting suppresses the alarm and never the
    measurement, and a suppression that leaves no trace anywhere is
    indistinguishable from a check that never ran — which is the one thing an
    integrity screen must not be. Zero for a library where nothing is muted,
    which is every library until somebody presses the switch."""
    albums_reopened: int = 0
    """Releases the last pass put back on the enrichment work list because their
    audio had been replaced."""
    last_run_at: UtcDatetime | None = None
    last_result: IntegrityReportOut | None = None
    running: bool = False
    """A pass is hashing right now. Passes do not overlap — hashing is minutes of
    disk and SQLite has one writer — so a second request is refused with a 409
    rather than queued."""


class IntegrityRunOut(MessageOut):
    """Acknowledgement of a verification pass, with the report it produced.

    The report is a field of its own rather than a dict under ``detail`` because
    it is the entire point of the response: a generated client should get
    :class:`IntegrityReportOut` here, not ``dict[str, Any]``.
    """

    report: IntegrityReportOut = Field(default_factory=IntegrityReportOut)


class CorruptTrackOut(BaseModel):
    """One file the fingerprinter could not decode, named well enough to act on.

    ``fingerprint_state`` is ``corrupt`` and that is a stronger claim than it
    looks: ``fpcalc`` ran, finished, and the audio would not decode, which no
    music player could either. The three failures it is *not* — the tool being
    absent, the decode timing out, the path not resolving — are facts about the
    machine or the mount and never reach this list, because acting on one of them
    moves a healthy album into the trash while nobody is watching.
    """

    track_id: str
    qid: str = ""
    title: str = ""
    album_id: str = ""
    album_title: str = ""
    artist_id: str | None = None
    artist_name: str | None = None
    path: str = ""
    fingerprint_state: str = "corrupt"
    last_attempt_at: UtcDatetime | None = None
    """When the fingerprint that produced this verdict was taken."""


class CorruptListOut(PageOut):
    """Everything currently recorded as unplayable, oldest verdict first."""

    items: list[CorruptTrackOut] = Field(default_factory=list)


# ---------------------------------------------------------------------------
# Health overview
# ---------------------------------------------------------------------------
class HealthSummaryOut(BaseModel):
    """Every subsystem's own payload, gathered once for the overview screen.

    The Health landing screen shows a card per subsystem and each card links to
    the sub-screen that owns it. Fetched one endpoint at a time that is five
    round trips for a screen whose whole job is to be glanced at; fetched as
    five *different* queries it is also five chances for the overview to
    disagree with the page it links to, which is the failure this codebase
    already knows by name — a live region has to render from the same context as
    its page, or it silently rearranges itself.

    So nothing here is recomputed. Every field is the identical model the
    sub-endpoint returns, produced by the identical builder, and the contract is
    equality: for one database, ``summary.integrity`` is byte-for-byte what
    ``GET /api/integrity`` answers. Adding a field means adding it to the
    sub-endpoint, never here.

    ``library_import`` rather than the more symmetric ``import``: that is a
    Python keyword, and a field named ``import_`` would put a trailing underscore
    into the JSON — an artefact of this language on a wire that has no opinion
    about it.
    """

    status: StatusOut = Field(default_factory=StatusOut)
    """Credentials, indexer, queue, rate limiter and the banner stack."""
    scan: LibraryScanStatusOut = Field(default_factory=LibraryScanStatusOut)
    library_import: LibraryImportOut = Field(default_factory=LibraryImportOut)
    enrichment: EnrichmentStatusOut = Field(default_factory=EnrichmentStatusOut)
    integrity: IntegrityStatusOut = Field(default_factory=IntegrityStatusOut)
    trash: TrashSummaryOut = Field(default_factory=TrashSummaryOut)
