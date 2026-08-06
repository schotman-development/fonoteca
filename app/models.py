"""SQLAlchemy 2.0 ORM models for Fonoteca.

Key data-model rule, confirmed against the live API: **Qobuz album ids are not
integers** (e.g. ``"uyej1o165e870"``, ``"0884977859300"``).  They are stored as
``String`` primary keys and must never be coerced to ``int``.  Artist and track
ids happen to be numeric, but they are stored as ``String`` too for consistency
and to survive any future format change.

All enums are persisted as plain ``VARCHAR`` (``native_enum=False``) holding the
enum *value*, so the database stays readable and migrations stay painless.
"""

from __future__ import annotations

import enum
from datetime import date, datetime, timezone

from sqlalchemy import (
    BigInteger,
    Boolean,
    Date,
    DateTime,
    Enum as SAEnum,
    Float,
    ForeignKey,
    Index,
    Integer,
    String,
    Text,
    func,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, relationship

__all__ = [
    "ActivityLevel",
    "AlbumStatus",
    "Base",
    "MonitorMode",
    "QueueState",
    "ReleaseType",
    "TrackStatus",
    "Artist",
    "Album",
    "Track",
    "QueueItem",
    "Activity",
    "Setting",
    "RELEASE_TYPES",
    "DEFAULT_ACCEPTED_RELEASE_TYPES",
    "QOBUZ_RELEASE_TYPE_MAP",
    "normalize_release_type",
    "parse_release_types",
    "join_release_types",
    "utcnow",
]


def utcnow() -> datetime:
    """Timezone-aware ``now`` in UTC, used as the default for timestamp columns."""
    return datetime.now(timezone.utc)


class Base(DeclarativeBase):
    """Declarative base for every Fonoteca table."""


# ---------------------------------------------------------------------------
# Enums
# ---------------------------------------------------------------------------
class MonitorMode(str, enum.Enum):
    """How aggressively an artist's back catalogue is pursued."""

    ALL = "all"
    """Monitor everything, including releases already in the catalogue."""

    FUTURE = "future"
    """Only monitor releases discovered after the artist was added."""

    NONE = "none"
    """Track the artist but never mark anything wanted."""


class AlbumStatus(str, enum.Enum):
    """Lifecycle of an album inside Fonoteca."""

    SKIPPED = "skipped"
    WANTED = "wanted"
    QUEUED = "queued"
    DOWNLOADING = "downloading"
    DOWNLOADED = "downloaded"
    FAILED = "failed"


class TrackStatus(str, enum.Enum):
    """Lifecycle of an individual track file."""

    PENDING = "pending"
    DOWNLOADING = "downloading"
    DOWNLOADED = "downloaded"
    FAILED = "failed"
    SKIPPED = "skipped"


class QueueState(str, enum.Enum):
    """State of a download-queue entry."""

    PENDING = "pending"
    ACTIVE = "active"
    DONE = "done"
    FAILED = "failed"
    CANCELLED = "cancelled"


class ActivityLevel(str, enum.Enum):
    """Severity of an activity-log entry."""

    DEBUG = "debug"
    INFO = "info"
    WARNING = "warning"
    ERROR = "error"


class ReleaseType(str, enum.Enum):
    """Normalised release types Fonoteca filters on."""

    ALBUM = "album"
    EP = "ep"
    SINGLE = "single"
    LIVE = "live"
    COMPILATION = "compilation"
    DOWNLOAD = "download"
    OTHER = "other"


#: All normalised release types, in UI display order.
RELEASE_TYPES: tuple[str, ...] = tuple(rt.value for rt in ReleaseType)

#: Sensible default for a freshly added artist.
DEFAULT_ACCEPTED_RELEASE_TYPES: tuple[str, ...] = ("album", "ep")

#: Maps the raw values Qobuz uses onto :class:`ReleaseType` values.
QOBUZ_RELEASE_TYPE_MAP: dict[str, str] = {
    "album": ReleaseType.ALBUM.value,
    "albums": ReleaseType.ALBUM.value,
    "lp": ReleaseType.ALBUM.value,
    "epsingle": ReleaseType.EP.value,
    "ep": ReleaseType.EP.value,
    "single": ReleaseType.SINGLE.value,
    "singles": ReleaseType.SINGLE.value,
    "live": ReleaseType.LIVE.value,
    "compilation": ReleaseType.COMPILATION.value,
    "compilations": ReleaseType.COMPILATION.value,
    "download": ReleaseType.DOWNLOAD.value,
}


def normalize_release_type(raw: str | None) -> str:
    """Map a raw Qobuz ``release_type`` onto a :class:`ReleaseType` value.

    Unknown or missing values become ``"other"``.  Note that Qobuz lumps EPs and
    singles together under ``epSingle``; callers that care about the distinction
    should refine the result using the album's ``tracks_count``.
    """
    if not raw:
        return ReleaseType.OTHER.value
    return QOBUZ_RELEASE_TYPE_MAP.get(str(raw).strip().lower(), ReleaseType.OTHER.value)


def parse_release_types(csv_value: str | None) -> list[str]:
    """Split a stored comma-separated release-type list into a clean list."""
    if not csv_value:
        return []
    return [part.strip().lower() for part in csv_value.split(",") if part.strip()]


def join_release_types(values: list[str] | tuple[str, ...] | None) -> str:
    """Join release types back into the comma-separated storage form."""
    if not values:
        return ""
    return ",".join(str(v).strip().lower() for v in values if str(v).strip())


# Reusable column types for the enums above.
_MonitorModeType = SAEnum(
    MonitorMode, name="monitor_mode", native_enum=False, values_callable=lambda e: [m.value for m in e]
)
_AlbumStatusType = SAEnum(
    AlbumStatus, name="album_status", native_enum=False, values_callable=lambda e: [m.value for m in e]
)
_TrackStatusType = SAEnum(
    TrackStatus, name="track_status", native_enum=False, values_callable=lambda e: [m.value for m in e]
)
_QueueStateType = SAEnum(
    QueueState, name="queue_state", native_enum=False, values_callable=lambda e: [m.value for m in e]
)
_ActivityLevelType = SAEnum(
    ActivityLevel, name="activity_level", native_enum=False, values_callable=lambda e: [m.value for m in e]
)


# ---------------------------------------------------------------------------
# Tables
# ---------------------------------------------------------------------------
class Artist(Base):
    """A Qobuz artist the user follows."""

    __tablename__ = "artists"

    id: Mapped[str] = mapped_column(String(64), primary_key=True)
    name: Mapped[str] = mapped_column(String(512), nullable=False, default="")
    monitored: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    monitor_mode: Mapped[MonitorMode] = mapped_column(
        _MonitorModeType, nullable=False, default=MonitorMode.ALL
    )
    quality_profile: Mapped[str] = mapped_column(String(64), nullable=False, default="default")
    accepted_release_types: Mapped[str] = mapped_column(
        String(255), nullable=False, default=",".join(DEFAULT_ACCEPTED_RELEASE_TYPES)
    )
    image_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    albums_count: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    last_checked_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    added_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, server_default=func.now()
    )
    qobuz_slug: Mapped[str | None] = mapped_column(String(512), nullable=True)

    albums: Mapped[list["Album"]] = relationship(
        back_populates="artist",
        cascade="all, delete-orphan",
        passive_deletes=True,
        lazy="selectin",
    )

    __table_args__ = (
        Index("ix_artists_monitored", "monitored"),
        Index("ix_artists_last_checked_at", "last_checked_at"),
        Index("ix_artists_name", "name"),
        Index("ix_artists_monitored_last_checked", "monitored", "last_checked_at"),
    )

    # -- convenience ------------------------------------------------------
    @property
    def accepted_release_types_list(self) -> list[str]:
        """``accepted_release_types`` parsed into a list of lowercase values."""
        return parse_release_types(self.accepted_release_types)

    def set_accepted_release_types(self, values: list[str] | tuple[str, ...]) -> None:
        """Store *values* as the comma-separated accepted-release-type list."""
        self.accepted_release_types = join_release_types(values)

    def accepts(self, release_type: str | None) -> bool:
        """True when this artist wants releases of the given normalised type."""
        return normalize_release_type(release_type) in self.accepted_release_types_list

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<Artist id={self.id!r} name={self.name!r} monitored={self.monitored}>"


class Album(Base):
    """A Qobuz release belonging to a followed artist.

    ``id`` is the Qobuz album id and is a **non-numeric string**.
    """

    __tablename__ = "albums"

    id: Mapped[str] = mapped_column(String(64), primary_key=True)
    artist_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("artists.id", ondelete="CASCADE"), nullable=False
    )
    title: Mapped[str] = mapped_column(String(1024), nullable=False, default="")
    version: Mapped[str | None] = mapped_column(String(512), nullable=True)
    release_date: Mapped[date | None] = mapped_column(Date, nullable=True)
    release_type: Mapped[str] = mapped_column(
        String(32), nullable=False, default=ReleaseType.ALBUM.value
    )
    tracks_count: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    media_count: Mapped[int] = mapped_column(Integer, nullable=False, default=1)
    hires: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    max_bit_depth: Mapped[int | None] = mapped_column(Integer, nullable=True)
    max_sampling_rate: Mapped[float | None] = mapped_column(Float, nullable=True)
    label: Mapped[str | None] = mapped_column(String(512), nullable=True)
    genre: Mapped[str | None] = mapped_column(String(255), nullable=True)
    upc: Mapped[str | None] = mapped_column(String(64), nullable=True)
    image_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    duration: Mapped[int | None] = mapped_column(Integer, nullable=True)
    status: Mapped[AlbumStatus] = mapped_column(
        _AlbumStatusType, nullable=False, default=AlbumStatus.SKIPPED
    )
    monitored: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    path: Mapped[str | None] = mapped_column(String(2048), nullable=True)
    downloaded_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    added_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, server_default=func.now()
    )

    artist: Mapped["Artist"] = relationship(back_populates="albums", lazy="joined")
    tracks: Mapped[list["Track"]] = relationship(
        back_populates="album",
        cascade="all, delete-orphan",
        passive_deletes=True,
        order_by="(Track.media_number, Track.track_number)",
        lazy="selectin",
    )
    queue_items: Mapped[list["QueueItem"]] = relationship(
        back_populates="album",
        cascade="all, delete-orphan",
        passive_deletes=True,
        lazy="selectin",
    )

    __table_args__ = (
        Index("ix_albums_artist_id", "artist_id"),
        Index("ix_albums_status", "status"),
        Index("ix_albums_release_date", "release_date"),
        Index("ix_albums_artist_status", "artist_id", "status"),
        Index("ix_albums_added_at", "added_at"),
    )

    @property
    def year(self) -> int | None:
        """Release year, or ``None`` when the release date is unknown."""
        return self.release_date.year if self.release_date else None

    @property
    def display_title(self) -> str:
        """Title with the Qobuz ``version`` suffix appended when present."""
        return f"{self.title} ({self.version})" if self.version else self.title

    @property
    def is_multi_disc(self) -> bool:
        """True when the release spans more than one disc."""
        return (self.media_count or 1) > 1

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<Album id={self.id!r} title={self.title!r} status={self.status}>"


class Track(Base):
    """A single track of an album. ``id`` is the Qobuz track id, stored as text."""

    __tablename__ = "tracks"

    id: Mapped[str] = mapped_column(String(64), primary_key=True)
    album_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("albums.id", ondelete="CASCADE"), nullable=False
    )
    title: Mapped[str] = mapped_column(String(1024), nullable=False, default="")
    version: Mapped[str | None] = mapped_column(String(512), nullable=True)
    track_number: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    media_number: Mapped[int] = mapped_column(Integer, nullable=False, default=1)
    duration: Mapped[int | None] = mapped_column(Integer, nullable=True)
    isrc: Mapped[str | None] = mapped_column(String(64), nullable=True)
    performer: Mapped[str | None] = mapped_column(String(512), nullable=True)
    composer: Mapped[str | None] = mapped_column(String(512), nullable=True)
    status: Mapped[TrackStatus] = mapped_column(
        _TrackStatusType, nullable=False, default=TrackStatus.PENDING
    )
    path: Mapped[str | None] = mapped_column(String(2048), nullable=True)
    format_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    bit_depth: Mapped[int | None] = mapped_column(Integer, nullable=True)
    sampling_rate: Mapped[float | None] = mapped_column(Float, nullable=True)
    file_size: Mapped[int | None] = mapped_column(BigInteger, nullable=True)
    downloaded_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)

    album: Mapped["Album"] = relationship(back_populates="tracks")

    __table_args__ = (
        Index("ix_tracks_album_id", "album_id"),
        Index("ix_tracks_status", "status"),
        Index("ix_tracks_album_disc_number", "album_id", "media_number", "track_number"),
    )

    @property
    def display_title(self) -> str:
        """Title with the Qobuz ``version`` suffix appended when present."""
        return f"{self.title} ({self.version})" if self.version else self.title

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<Track id={self.id!r} title={self.title!r} status={self.status}>"


class QueueItem(Base):
    """One unit of download work: an album to fetch, in order."""

    __tablename__ = "queue_items"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    album_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("albums.id", ondelete="CASCADE"), nullable=False
    )
    state: Mapped[QueueState] = mapped_column(
        _QueueStateType, nullable=False, default=QueueState.PENDING
    )
    priority: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    attempts: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    last_error: Mapped[str | None] = mapped_column(Text, nullable=True)
    progress_tracks_done: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    progress_tracks_total: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, server_default=func.now()
    )
    started_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)

    album: Mapped["Album"] = relationship(back_populates="queue_items", lazy="joined")

    __table_args__ = (
        Index("ix_queue_items_state", "state"),
        Index("ix_queue_items_album_id", "album_id"),
        # The worker's pick-next query: pending items, highest priority first.
        Index("ix_queue_items_state_priority_id", "state", "priority", "id"),
        Index("ix_queue_items_created_at", "created_at"),
    )

    @property
    def is_terminal(self) -> bool:
        """True when the item will not be picked up again without a retry."""
        return self.state in (QueueState.DONE, QueueState.FAILED, QueueState.CANCELLED)

    @property
    def progress_percent(self) -> int:
        """Whole-percent completion, 0 when the total is unknown."""
        if not self.progress_tracks_total:
            return 0
        return int(100 * self.progress_tracks_done / self.progress_tracks_total)

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<QueueItem id={self.id} album_id={self.album_id!r} state={self.state}>"


class Activity(Base):
    """Append-only history feed shown on the dashboard and activity page."""

    __tablename__ = "activities"

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    level: Mapped[ActivityLevel] = mapped_column(
        _ActivityLevelType, nullable=False, default=ActivityLevel.INFO
    )
    event: Mapped[str] = mapped_column(String(64), nullable=False, default="")
    message: Mapped[str] = mapped_column(Text, nullable=False, default="")
    artist_id: Mapped[str | None] = mapped_column(
        String(64), ForeignKey("artists.id", ondelete="SET NULL"), nullable=True
    )
    album_id: Mapped[str | None] = mapped_column(
        String(64), ForeignKey("albums.id", ondelete="SET NULL"), nullable=True
    )
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, server_default=func.now()
    )

    # Not view-only: the unit of work skips view-only relationships when it
    # orders INSERTs, which would let an Activity be flushed before the Album
    # it references and trip the foreign key.
    artist: Mapped["Artist | None"] = relationship(lazy="joined")
    album: Mapped["Album | None"] = relationship(lazy="joined")

    __table_args__ = (
        Index("ix_activities_created_at", "created_at"),
        Index("ix_activities_level", "level"),
        Index("ix_activities_event", "event"),
        Index("ix_activities_artist_id", "artist_id"),
        Index("ix_activities_album_id", "album_id"),
    )

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<Activity id={self.id} event={self.event!r} level={self.level}>"


class Setting(Base):
    """Simple key/value store for runtime-mutable settings and worker state."""

    __tablename__ = "settings"

    key: Mapped[str] = mapped_column(String(128), primary_key=True)
    value: Mapped[str | None] = mapped_column(Text, nullable=True)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utcnow,
        onupdate=utcnow,
        server_default=func.now(),
    )

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<Setting key={self.key!r}>"
