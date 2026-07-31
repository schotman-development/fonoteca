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

from datetime import date, datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator

from app.models import (
    ActivityLevel,
    AlbumStatus,
    MonitorMode,
    QueueState,
    TrackStatus,
)

__all__ = [
    "ORMModel",
    "ArtistOut",
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
    "LibraryScanOut",
    "LibraryScanStatusOut",
    "LibraryImportOut",
    "LibraryImportStartIn",
    "LibraryTidyOut",
    "RefilePlanOut",
    "TrashEntryOut",
    "TrashOut",
    "ImportCandidateOut",
    "ImportReviewOut",
    "ScannedFolderOut",
    "SettingsOut",
    "MessageOut",
    "ArtistCreateIn",
    "ArtistUpdateIn",
    "ArtistBulkUpdateIn",
    "AlbumUpdateIn",
    "PageOut",
]


class ORMModel(BaseModel):
    """Base for models read straight off SQLAlchemy rows."""

    model_config = ConfigDict(from_attributes=True)


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
    image_url: str | None = None
    albums_count: int = 0
    last_checked_at: datetime | None = None
    added_at: datetime | None = None
    qobuz_slug: str | None = None

    # Roll-ups, populated by the router when available.
    album_count: int | None = None
    wanted_count: int | None = None
    downloaded_count: int | None = None

    @field_validator("accepted_release_types", mode="before")
    @classmethod
    def _split_csv(cls, value: Any) -> Any:
        """Accept the stored comma-separated string as well as a real list."""
        if isinstance(value, str):
            return [part.strip().lower() for part in value.split(",") if part.strip()]
        return value


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
    path: str | None = None
    format_id: int | None = None
    bit_depth: int | None = None
    sampling_rate: float | None = None
    file_size: int | None = None
    downloaded_at: datetime | None = None


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
    downloaded_at: datetime | None = None
    added_at: datetime | None = None

    # Convenience extras, populated by the router when available.
    artist_name: str | None = None
    year: int | None = None
    tracks: list[TrackOut] | None = None

    # Quality comparison, filled in by ``app.api.deps.album_to_out``.
    owned_format_id: int | None = None
    """The Qobuz ``format_id`` of the worst file we hold for this release, or
    ``None`` when nothing is on disk (or its quality cannot be determined)."""
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
    created_at: datetime | None = None
    started_at: datetime | None = None
    finished_at: datetime | None = None

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
    created_at: datetime | None = None

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
    in_library: bool = False


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
    last_request_at: datetime | None = None
    circuit_open: bool = False
    circuit_open_until: datetime | None = None
    recent_429s: int = 0


class IndexerStatusOut(BaseModel):
    """Current state of the slow background indexer."""

    enabled: bool = True
    running: bool = False
    paused: bool = False
    artist_interval_seconds: int = 0
    full_sweep_hours: int = 0
    next_run_at: datetime | None = None
    seconds_until_next_run: float | None = None
    current_artist_id: str | None = None
    current_artist_name: str | None = None
    last_run_at: datetime | None = None
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


class StatusOut(BaseModel):
    """Everything the dashboard needs in a single payload."""

    version: str = "0.1.0"
    started_at: datetime | None = None
    uptime_seconds: float = 0.0
    library: LibraryStatsOut = Field(default_factory=LibraryStatsOut)
    queue: QueueStatsOut = Field(default_factory=QueueStatsOut)
    indexer: IndexerStatusOut = Field(default_factory=IndexerStatusOut)
    rate_limit: RateLimitStatusOut | None = None
    recent_activity: list[ActivityOut] = Field(default_factory=list)
    credentials_ok: bool = False
    app_secret_ok: bool = False
    library_path: str = ""


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
    started_at: datetime | None = None
    duration_seconds: float = 0.0
    applied: bool = True
    directories: int = 0
    audio_files: int = 0
    albums_found: int = 0
    albums_matched: int = 0
    albums_adopted: int = 0
    albums_already: int = 0
    albums_partial: int = 0
    albums_busy: int = 0
    tracks_linked: int = 0
    queue_items_cancelled: int = 0
    partial: list[ScannedFolderOut] = Field(default_factory=list)
    unmatched: list[ScannedFolderOut] = Field(default_factory=list)
    unknown_artists: list[ScannedFolderOut] = Field(default_factory=list)
    truncated: dict[str, int] = Field(default_factory=dict)
    errors: list[str] = Field(default_factory=list)
    summary: str = ""


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
    started_at: datetime | None = None
    finished_at: datetime | None = None
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


class TrashEntryOut(BaseModel):
    """One batch in the trash, mirroring :class:`app.core.librarian.TrashEntry`."""

    id: str
    trashed_at: datetime | None = None
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
    blocked: int = 0
    failed: int = 0
    plans: list[RefilePlanOut] = Field(default_factory=list)
    """Populated by re-file; the preview *is* this list."""
    errors: list[str] = Field(default_factory=list)
    summary: str = ""


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

    library_path: str
    data_path: str
    default_format_id: int
    default_format_label: str = ""
    naming_template: str
    default_monitor_mode: str
    default_quality_profile: str
    default_accepted_release_types: list[str] = Field(default_factory=list)
    qobuz_min_request_interval: float
    qobuz_max_requests_per_hour: int
    indexer_artist_interval: int
    indexer_full_sweep_hours: int
    indexer_enabled: bool
    download_track_delay: float
    download_concurrency: int
    download_max_attempts: int
    library_scan_nightly: bool = True
    library_scan_complete_ratio: float = 1.0
    host: str
    port: int
    log_level: str
    qobuz_app_id: str = ""
    credentials_ok: bool = False
    app_secret_source: str = "unset"


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
    release_types: list[str] | None = None
    release_types_action: Literal["set", "add", "remove"] = "set"


class AlbumUpdateIn(BaseModel):
    """Partial update of an album's state (used by the wanted/skip buttons)."""

    monitored: bool | None = None
    status: AlbumStatus | None = None


class MessageOut(BaseModel):
    """Generic acknowledgement returned by action endpoints."""

    ok: bool = True
    message: str = ""
    detail: dict[str, Any] | None = None


class PageOut(BaseModel):
    """Pagination envelope used by list endpoints."""

    total: int = 0
    limit: int = 50
    offset: int = 0
    sort: str | None = None
    order: Literal["asc", "desc"] = "asc"
