"""SQLAlchemy 2.0 ORM models for Qobuzarr.

Key data-model rule, confirmed against the live API: **Qobuz album ids are not
integers** (e.g. ``"uyej1o165e870"``, ``"0884977859300"``).  They are stored as
``String`` primary keys and must never be coerced to ``int``.  Artist and track
ids happen to be numeric, but they are stored as ``String`` too for consistency
and to survive any future format change.

All enums are persisted as plain ``VARCHAR`` (``native_enum=False``) holding the
enum *value*, so the database stays readable and migrations stay painless.

Beside the Qobuz ids sits a second, parallel identifier: :func:`mint_qid` mints a
``qid`` for every artist, album and track, and that value **never changes for the
life of the row**.  The Qobuz id cannot do that job.  It is the catalogue's name
for a thing, not ours: a release Qobuz never sold has no id at all (the disk scan
invents one from the track's position, so a retagger that renumbers a track
changes it), and the whole identification system is built on the premise that the
audio is the anchor and the catalogue is a hypothesis.  A stable local key is what
lets a file's measured facts — its hash, its sample count, the verdict of a
fingerprint — survive being re-matched to a different release.
"""

from __future__ import annotations

import enum
import json
import secrets
from collections.abc import Sequence
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
    text,
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
    "AppSetting",
    "Setting",
    "ArtistMetadata",
    "AlbumMetadata",
    "TrackMetadata",
    "FileClaim",
    "FolderBinding",
    "BINDING_BOUND",
    "BINDING_NOT_IN_CATALOGUE",
    "BINDING_STATES",
    "EnrichmentState",
    "ENRICHMENT_SCHEMA_VERSION",
    "ENRICHMENT_SCHEMA_KEY",
    "ENRICHMENT_TABLES",
    "EnrichmentSource",
    "EnrichmentEntity",
    "FingerprintState",
    "RELEASE_TYPES",
    "DEFAULT_ACCEPTED_RELEASE_TYPES",
    "QOBUZ_RELEASE_TYPE_MAP",
    "normalize_release_type",
    "parse_release_types",
    "join_release_types",
    "parse_aliases",
    "join_aliases",
    "mint_qid",
    "QID_ARTIST_PREFIX",
    "QID_RELEASE_PREFIX",
    "QID_TRACK_PREFIX",
    "utcnow",
]


def utcnow() -> datetime:
    """Timezone-aware ``now`` in UTC, used as the default for timestamp columns."""
    return datetime.now(timezone.utc)


#: Prefixes for the three kinds of ``qid``. They exist so a stray id in a log line
#: or a tag can be told apart at a glance — a track qid pasted where a release qid
#: belongs is otherwise indistinguishable from a valid one. Nothing may *parse* a
#: qid beyond this: it is opaque, and the prefix is a courtesy, not a schema.
QID_ARTIST_PREFIX = "qa_"
QID_RELEASE_PREFIX = "qr_"
QID_TRACK_PREFIX = "qt_"


def mint_qid(prefix: str) -> str:
    """Mint a fresh, opaque local identifier, e.g. ``qt_3f0c…``.

    This lives in ``app/models.py`` rather than in ``app/core/integrity.py``
    because of the dependency direction: ``core`` may import ``models``, never the
    other way round, and the qid columns need a Python-side default *here* — every
    ``Track``, ``Album`` and ``Artist`` row must arrive with one, including the
    rows constructed by the download loop, the disk scan and the test suite, none
    of which will remember to pass one. Anything in ``app/core`` that needs to
    mint one — ``app.core.integrity`` and its callers — imports it from here. A
    second implementation anywhere is a second id format, and the point of a qid
    is that there is exactly one.

    The value is 96 bits of ``secrets`` randomness in hex behind a three-character
    prefix — 27 characters, comfortably inside ``String(32)``. It is deliberately
    **not** derived from anything: a qid computed from a path, a title or a Qobuz
    id would change when that input changed, which is the one thing a qid must
    never do. Collision is not a practical concern at library scale, and the unique
    index is there to make a collision an error rather than a silent merge.
    """
    return f"{prefix}{secrets.token_hex(12)}"


class Base(DeclarativeBase):
    """Declarative base for every Qobuzarr table."""


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
    """Lifecycle of an album inside Qobuzarr."""

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


class TrackOrigin(str, enum.Enum):
    """Which half of Qobuzarr put a :class:`Track` row in the database.

    This used to be implicit — track rows were written by the download loop and
    by nothing else, so *having* one meant "Qobuzarr fetched this release" and
    the write-back gate read it that way. The disk scan now creates rows too,
    because assuming a library is untouched by anything else is wrong: files get
    retagged, refiled and replaced by other tools, and a release Qobuzarr can
    only see as an opaque folder is one it can neither fingerprint, quality-check
    nor repair.

    So the distinction that gate needs is stated rather than inferred.
    ``DOWNLOAD`` means Qobuzarr wrote the file and may rewrite it; ``SCAN`` means
    the file arrived with somebody else's tags, which are not ours to overwrite.
    """

    DOWNLOAD = "download"
    SCAN = "scan"


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


class EnrichmentSource(str, enum.Enum):
    """The open datasources enrichment draws on, in the order they run.

    Deezer is first on purpose: it is fast, key-free, has near-total catalogue
    coverage, and it is where the **barcode** comes from. MusicBrainz can only
    match exactly on a barcode or an ISRC, and Qobuz supplies neither for most
    releases — so run in the other order, MusicBrainz has nothing to join on.
    """

    DEEZER = "deezer"
    MUSICBRAINZ = "musicbrainz"
    ACOUSTID = "acoustid"
    COVERARTARCHIVE = "coverartarchive"
    WIKIDATA = "wikidata"


class EnrichmentEntity(str, enum.Enum):
    """What an :class:`EnrichmentState` row is about.

    Tracks are absent deliberately: they are never fetched on their own. They
    come back inside a release lookup at no extra cost, so they have metadata
    rows but no fetch bookkeeping of their own.
    """

    ARTIST = "artist"
    ALBUM = "album"


class FingerprintState(str, enum.Enum):
    """What Chromaprint made of a file on disk.

    ``CORRUPT`` is not a guess: if ``fpcalc`` cannot decode the audio, neither
    can a music player. That is the corruption test, and it is why fingerprinting
    doubles as a library integrity check.
    """

    OK = "ok"
    CORRUPT = "corrupt"
    UNREADABLE = "unreadable"


class ReleaseType(str, enum.Enum):
    """Normalised release types Qobuzarr filters on."""

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


def parse_name_list(raw: str | None) -> list[str]:
    """A stored JSON array of strings, as a clean list. Order preserved.

    JSON rather than the comma join :func:`join_release_types` uses, because
    that column holds a closed vocabulary of lowercase slugs that provably
    contain no commas, and the free-text things stored this way legitimately do
    — ``"Tchaikovsky, Pyotr Ilyich"`` is exactly what somebody types as an
    alias, and a credit line is no better behaved. A comma join would turn one
    value into two on the round trip.

    **Never raises.** A hand-edited database, a truncated write or a value from
    some future shape must not 500 the artist page, so anything that is not a
    JSON list of strings reads as "nobody has said". Blanks and exact
    duplicates are dropped.
    """
    if not raw:
        return []
    try:
        loaded = json.loads(raw)
    except (ValueError, TypeError):
        return []
    if not isinstance(loaded, list):
        return []
    out: list[str] = []
    for item in loaded:
        if not isinstance(item, str):
            continue
        text = item.strip()
        if text and text not in out:
            out.append(text)
    return out


def join_name_list(values: Sequence[str]) -> str | None:
    """A list of strings as compact JSON, or ``None`` when nothing survives.

    ``None`` rather than ``"[]"`` so a column using this keeps saying "nobody
    has said" in one shape only — the same distinction ``sort_name`` draws.

    Callers that need to record *"this was measured and the answer was
    nothing"* must not use this: ``None`` and ``"[]"`` are different claims
    there, and :attr:`Album.credit_names` is the column that depends on it.
    """
    cleaned = parse_name_list(json.dumps([str(v) for v in values]))
    return json.dumps(cleaned, ensure_ascii=False) if cleaned else None


#: Aliases were the first user of the JSON-string-list pair above and named it.
#: Kept as the alias-shaped spelling so call sites read for what they store.
parse_aliases = parse_name_list
join_aliases = join_name_list


def parse_tags(csv_value: str | None) -> list[str]:
    """Split a stored comma-separated list, preserving case and order.

    Used for the enrichment side tables' genre and secondary-type lists.  Unlike
    :func:`parse_release_types` this does **not** lowercase: these are display
    strings (``"Progressive Rock"``, ``"Soundtrack"``) rather than a controlled
    vocabulary Qobuzarr filters on.
    """
    if not csv_value:
        return []
    seen: list[str] = []
    for part in csv_value.split(","):
        cleaned = part.strip()
        if cleaned and cleaned not in seen:
            seen.append(cleaned)
    return seen


def join_tags(values: list[str] | tuple[str, ...] | None) -> str:
    """Join a display list back into the comma-separated storage form."""
    if not values:
        return ""
    return ",".join(parse_tags(",".join(str(v) for v in values)))


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
_TrackOriginType = SAEnum(
    TrackOrigin, name="track_origin", native_enum=False, values_callable=lambda e: [m.value for m in e]
)
_QueueStateType = SAEnum(
    QueueState, name="queue_state", native_enum=False, values_callable=lambda e: [m.value for m in e]
)
_ActivityLevelType = SAEnum(
    ActivityLevel, name="activity_level", native_enum=False, values_callable=lambda e: [m.value for m in e]
)
_EnrichmentSourceType = SAEnum(
    EnrichmentSource,
    name="enrichment_source",
    native_enum=False,
    values_callable=lambda e: [m.value for m in e],
)
_EnrichmentEntityType = SAEnum(
    EnrichmentEntity,
    name="enrichment_entity",
    native_enum=False,
    values_callable=lambda e: [m.value for m in e],
)
_FingerprintStateType = SAEnum(
    FingerprintState,
    name="fingerprint_state",
    native_enum=False,
    values_callable=lambda e: [m.value for m in e],
)


# ---------------------------------------------------------------------------
# Tables
# ---------------------------------------------------------------------------
class Artist(Base):
    """A Qobuz artist the user follows."""

    __tablename__ = "artists"

    id: Mapped[str] = mapped_column(String(64), primary_key=True)
    #: Stable local identity. See :func:`mint_qid`. An artist carries one for the
    #: same reason a release does — identity propagates *upward* from the audio,
    #: so the artist a release is credited to can be revised without the rows that
    #: point at that artist losing their anchor.
    qid: Mapped[str] = mapped_column(
        String(32), nullable=False, unique=True, index=True,
        default=lambda: mint_qid(QID_ARTIST_PREFIX),
    )
    name: Mapped[str] = mapped_column(String(512), nullable=False, default="")
    #: How this artist files, when a person has said. ``"Frahm, Nils"``.
    #:
    #: It lives *here* rather than on :class:`ArtistMetadata`, which already has
    #: a ``sort_name``, and the difference between the two columns is the whole
    #: reason this one exists. That one is derived: MusicBrainz supplies it and
    #: rewrites it on every pass, ``_MANUAL_OWNS`` protects only ids and not
    #: descriptive fields, and the table it sits in is one of the five that
    #: ``ENRICHMENT_SCHEMA_VERSION`` **drops and rebuilds** — safe for derived,
    #: re-fetchable data and fatal for a sentence somebody typed. So the typed
    #: one goes on ``artists``, where nothing re-derives it, and the read models
    #: coalesce: this value wins, the derived one fills the gap.
    #:
    #: ``None`` means "nobody has said", which is not the same as an empty
    #: string; clearing the field stores ``None`` and hands the answer back to
    #: enrichment rather than pinning it to blank.
    sort_name: Mapped[str | None] = mapped_column(String(512), nullable=True)
    #: Alternative spellings of this artist's name, as a person typed them.
    #: A JSON array of strings in a nullable ``Text`` column; ``None`` means
    #: "nobody has said", exactly as for ``sort_name`` above.
    #:
    #: JSON rather than the comma join ``accepted_release_types`` uses: that
    #: column holds a closed vocabulary of slugs that contain no commas, and an
    #: alias legitimately does — ``"Tchaikovsky, Pyotr Ilyich"``. A comma join
    #: would turn one alias into two on the round trip.
    #:
    #: **Database-only.** Nothing writes it to a file tag (Picard has no such
    #: field, and a tag spelled differently is a tag no other tool will read),
    #: nothing writes it to ``artist.nfo`` (no media server reads one), and
    #: ``app/enrich/matching.py`` must never read it: widening the accepted
    #: name set with a string a person typed would let a name *select* a
    #: candidate at the last gate before an MBID is written into every file.
    aliases_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    #: Want releases this artist only *guests* on. Default **False**: a record
    #: credited to somebody else, on which this artist plays, is that other
    #: person's release. Qobuz files those under the guest too, so following
    #: Joe Bonamassa offers six Black Country Communion albums, a Dion single
    #: and three Scary Pockets covers as though he had made them — 122 releases
    #: across 41 monitored artists, 8.2% of everything they offer.
    #:
    #: It is a column rather than a global setting because
    #: :func:`app.core.indexer.desired_status` is pure and reads two rows: a
    #: ``Settings`` lookup inside it would end that, and an overridable key
    #: would then have to reach it through ``config.effective_for``. Per-artist
    #: is also the honest granularity — a session player's guest appearances
    #: are most of their catalogue.
    #: ``server_default`` as well as ``default``: the ORM's Python-side default
    #: does not reach a raw ``INSERT``, and this column is ``NOT NULL``, so
    #: tooling and fixtures that write SQL directly would fail on a row they
    #: have no opinion about. It also makes a freshly ``create_all``-ed schema
    #: identical to a migrated one rather than differing in ``dflt_value``.
    include_guest_appearances: Mapped[bool] = mapped_column(
        Boolean, nullable=False, default=False, server_default=text("0")
    )
    #: Accept only releases whose track credits name one of these people.
    #: A JSON array of strings; ``None`` means **no filter**, which is the
    #: default and is byte-for-byte today's behaviour.
    #:
    #: This exists because a Qobuz artist id is not always one artist. Id
    #: ``322476`` "Boaz" holds releases by at least eleven different people —
    #: a Dutch country singer, a Pittsburgh rapper, a French rapper, a
    #: Tanzanian gospel act — and every one of them is stamped
    #: ``main-artist: 322476`` in Qobuz's own payload, so nothing about the
    #: release distinguishes them. Deezer merges them identically and
    #: MusicBrainz knows only 12% of the ISRCs, so no upstream can be asked.
    #: What *does* separate them is the qualified credit line on the tracks
    #: (``Boaz Roelevink`` vs ``Boaz Ndarivoi``), and a person picks which
    #: are theirs — see :attr:`Album.credit_names`.
    #:
    #: A name chosen here may **reject** a release and never selects one for
    #: any other purpose: it decides ``wanted`` vs ``skipped``, which is
    #: reversible and writes no tag, no NFO and no id. It must never be fed to
    #: :mod:`app.enrich.matching`, for the same reason ``aliases_json`` may not
    #: be — that is the gate before an MBID is written into every file.
    credit_filter_json: Mapped[str | None] = mapped_column(Text, nullable=True)
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

    @property
    def aliases_list(self) -> list[str]:
        """``aliases_json`` parsed into a list of strings. Never raises."""
        return parse_aliases(self.aliases_json)

    def set_aliases(self, values: Sequence[str]) -> None:
        """Store *values* as the JSON alias list, or ``None`` when empty."""
        self.aliases_json = join_aliases(values)

    @property
    def credit_filter_list(self) -> list[str]:
        """``credit_filter_json`` parsed into a list of strings. Never raises."""
        return parse_name_list(self.credit_filter_json)

    def set_credit_filter(self, values: Sequence[str]) -> None:
        """Store *values* as the accepted-credit list, or ``None`` when empty.

        Empty stores ``None`` — *no filter* — deliberately. An empty accepted
        set read as "accept nothing" would let one stray press unmonitor an
        artist's whole catalogue with no visible cause.
        """
        self.credit_filter_json = join_name_list(values)

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
    #: Stable local identity. See :func:`mint_qid`.
    qid: Mapped[str] = mapped_column(
        String(32), nullable=False, unique=True, index=True,
        default=lambda: mint_qid(QID_RELEASE_PREFIX),
    )
    #: blake2b-128 of this release's member track hashes, taken in disc/track
    #: order — one value answering "is anything under this release different from
    #: the last time we looked?" without opening a file. Order is part of it: a
    #: retagger that renumbers two tracks has changed the release even though the
    #: same bytes are still on disk, and that is exactly the case a set-of-hashes
    #: digest would call unchanged. ``NULL`` means *never baselined*, which is not
    #: the same claim as *changed*; see :attr:`Track.content_hash`.
    content_digest: Mapped[str | None] = mapped_column(String(32), nullable=True)
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
    #: Do not let enrichment rewrite this release's file tags.
    #: :meth:`app.core.enricher.Enricher._write_back` is the single writer of
    #: enrichment tags into files, so this is one guard clause there and nowhere
    #: else. The NFO is still written, because an NFO is *merged* rather than
    #: replaced — pinning protects the bytes somebody curated, and no element of
    #: theirs is touched by a merge. An explicit user action (the artist re-tag,
    #: the library-wide re-tag) is a different thing from a background pass and
    #: is not gated on this.
    pin_tags: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    #: Do not move or rename this release's folder or files.
    #: :func:`app.core.librarian.plan_refile` returns a **blocked** plan,
    #: :func:`app.core.librarian.plan_library_refile` leaves it out of the preview
    #: entirely, and :func:`app.core.librarian.refile_album` refuses even when it
    #: is handed a plan built elsewhere — a flag only one of the three honoured
    #: would be a lock with a hole in it.
    freeze_path: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    #: Do not raise the corruption alarm for this release.
    #: The measurement still happens and the release still reports its real
    #: ``integrity_state`` and ``corrupt_tracks`` on its own detail payload: this
    #: hides the **alarm**, never the **fact**. What it suppresses is the acting
    #: on it — :func:`app.core.librarian.quarantine_corrupt_files`, which the
    #: nightly job runs unattended and which moves files to the trash — and the
    #: library-wide corruption figure that would otherwise keep a known-good rip
    #: permanently on the health screen's to-do list.
    mute_integrity: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    #: Whether the owning artist merely *guests* on this release: they appear in
    #: Qobuz's ``artists`` array as ``featured-artist`` and **never** as
    #: ``main-artist``. Written by :func:`app.qobuz.mapper.map_album` from the
    #: free ``artist/getReleasesList`` payload, so it costs no extra call.
    #:
    #: **Three-valued, and the third value is load-bearing.** ``None`` means no
    #: payload has said yet — a row that predates the column, or one mapped from
    #: the older ``artist/get?extra=albums`` shape, which carries no roles at
    #: all. ``None`` is a **no-op** in :func:`app.core.indexer.desired_status`,
    #: never a demotion: reading "not measured" as "guest" would demote an
    #: artist's whole backlog the moment the column shipped and nothing would
    #: bring it back, because only ``apply_monitoring_to_backlog`` re-derives an
    #: existing row's status and only two HTTP handlers call it. That is
    #: ``integrity.classify()``'s ``UNKNOWN is not CHANGED`` rule, here.
    #:
    #: An artist **absent** from ``artists`` entirely is ``False``, not ``None``
    #: and never ``True``. That array holds performers, so a composer is
    #: routinely missing from their own release — Samuel Barber's id is absent
    #: on 156 of his 169 — and a rule phrased "lacks ``main-artist``" rather
    #: than "is ``featured-artist`` and never ``main-artist``" erases 92% of a
    #: composer's catalogue.
    guest_appearance: Mapped[bool | None] = mapped_column(Boolean, nullable=True)
    #: The qualified credit names found on this release's tracks — the people
    #: named in Qobuz's ``performers`` string whose name extends the owning
    #: artist's (``Boaz Roelevink``, not the bare ``Boaz`` every release
    #: carries). Read by :attr:`Artist.credit_filter_json`; see there for why.
    #:
    #: Three-valued like :attr:`guest_appearance`, and for the same reason, but
    #: the distinction is drawn differently because both states are reachable:
    #: ``None`` means *nobody has looked* (no-op), while ``"[]"`` means *looked,
    #: and every credit was the bare name* — which is a real answer about 20 of
    #: Boaz's 58 releases and must not read as "not analysed". So this column is
    #: written with :func:`json.dumps` directly and never through
    #: :func:`join_name_list`, which collapses empty to ``None``.
    #:
    #: Populated only on request, per artist, because it costs one ``album/get``
    #: per release. The indexer never fetches it.
    credit_names: Mapped[str | None] = mapped_column(Text, nullable=True)
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

    @property
    def credit_names_list(self) -> list[str]:
        """``credit_names`` parsed into a list of strings. Never raises.

        Answers ``[]`` both for *not analysed* and for *analysed, nothing
        qualified*. Callers that must tell those apart test ``credit_names is
        None`` — the column, not this property.
        """
        return parse_name_list(self.credit_names)

    def set_credit_names(self, values: Sequence[str] | None) -> None:
        """Record the qualified credits found on this release.

        ``None`` clears the column back to *not analysed*. An empty sequence
        stores ``"[]"`` — *analysed, and no credit qualified* — which is a real
        answer and deliberately not the same value.
        """
        if values is None:
            self.credit_names = None
            return
        cleaned = parse_name_list(json.dumps([str(v) for v in values]))
        self.credit_names = json.dumps(cleaned, ensure_ascii=False)

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<Album id={self.id!r} title={self.title!r} status={self.status}>"


class Track(Base):
    """A single track of an album.

    ``id`` is the Qobuz track id for a row the download loop wrote. Rows the disk
    scan created have no Qobuz counterpart — the file may predate Qobuzarr, or
    come from a release Qobuz does not sell — so they carry a synthetic id from
    :func:`app.core.scanner.scanned_track_id` instead, keyed on the album and the
    track's position within it. :attr:`origin` says which kind a row is; never
    infer it from the shape of the id.
    """

    __tablename__ = "tracks"

    id: Mapped[str] = mapped_column(String(64), primary_key=True)
    #: Stable local identity, and the **only** key anything outside this table may
    #: look a track up by. :attr:`id` cannot serve: for a scanned row it is
    #: synthesised from the track's position in the album, so a retagger that
    #: renumbers a track deletes one row and creates another, taking every
    #: measurement made about that file with it. The qid is minted once and never
    #: reissued, which is what lets a file keep its fingerprint verdict and its
    #: baseline across a re-file, a re-tag or a re-match.
    qid: Mapped[str] = mapped_column(
        String(32), nullable=False, unique=True, index=True,
        default=lambda: mint_qid(QID_TRACK_PREFIX),
    )
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
    #: Defaults to ``download`` so every row written before this column existed
    #: — all of which came from the download loop — reads correctly without a
    #: backfill.
    origin: Mapped[TrackOrigin] = mapped_column(
        _TrackOriginType, nullable=False, default=TrackOrigin.DOWNLOAD,
        server_default=TrackOrigin.DOWNLOAD.value,
    )
    path: Mapped[str | None] = mapped_column(String(2048), nullable=True)
    format_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    bit_depth: Mapped[int | None] = mapped_column(Integer, nullable=True)
    sampling_rate: Mapped[float | None] = mapped_column(Float, nullable=True)
    file_size: Mapped[int | None] = mapped_column(BigInteger, nullable=True)
    downloaded_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)

    # -- integrity baseline ------------------------------------------------
    # Four columns answering one question — "is the file still the file we
    # recorded?" — at three costs, and the point of having three is that the
    # expensive one almost never runs.
    #
    # ``file_size`` (above) and ``file_mtime`` are the tripwire: a ``stat`` per
    # file, no read. Unchanged means the check stops there. Changed means open the
    # file and hash it, because mtime is not evidence — it is preserved by some
    # copies and rewritten by others.
    #
    # The remaining pair is what makes the *verdict* useful rather than merely
    # true. ``content_hash`` says the bytes differ; ``sample_count`` says whether
    # the **audio** differs. A tag edit rewrites the container and changes the
    # hash while leaving every sample intact, and calling that a replaced file
    # would send Qobuzarr re-identifying a library every time somebody ran a
    # tagger over it. Comparing sample counts separates RETAGGED from REPLACED
    # without parsing container structure, which is the only other way to know.
    #
    # All four are nullable and that is a third state, not a missing value: a row
    # that has never been baselined is UNKNOWN, and UNKNOWN is not CHANGED.
    # Collapsing the two makes every pre-existing row look freshly tampered with
    # on the first run, which is precisely when nobody can tell a real alarm from
    # the noise.
    #: blake2b-128 of the whole file, hex. Whole-file rather than audio-only
    #: because the tags are part of what is being protected.
    content_hash: Mapped[str | None] = mapped_column(String(32), nullable=True)
    #: Total decoded sample frames. Survives any tag edit; changes if the audio does.
    sample_count: Mapped[int | None] = mapped_column(BigInteger, nullable=True)
    #: POSIX mtime as a float — seconds with sub-second precision, as ``os.stat``
    #: reports it. Stored as a number rather than a ``DateTime`` so it round-trips
    #: without a timezone question being asked of a value that has no timezone.
    file_mtime: Mapped[float | None] = mapped_column(Float, nullable=True)
    #: When the baseline above was last confirmed against the disk.
    verified_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )

    album: Mapped["Album"] = relationship(back_populates="tracks")

    __table_args__ = (
        Index("ix_tracks_album_id", "album_id"),
        Index("ix_tracks_status", "status"),
        Index("ix_tracks_album_disc_number", "album_id", "media_number", "track_number"),
        Index("ix_tracks_verified_at", "verified_at"),
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


class AppSetting(Base):
    """One overridden configuration value — the settings overlay's storage.

    Deliberately **not** :class:`Setting`, which is worker bookkeeping (the
    enrichment schema version, the last scan, the last import) written by the
    application about itself. This table holds what a *person* chose on the
    settings screen, and the two want opposite things from a reset: wiping this
    one restores ``.env`` and loses nothing, wiping the other would make the
    enrichment tables look like a fresh install.

    ``key`` is always one of :data:`app.config.OVERRIDABLE_SETTINGS`; ``value``
    is the text form of the parsed value (``"true"``/``"false"`` for a switch,
    the CSV for a list), because SQLite has no other honest home for a union of
    types and :func:`app.config.parse_override` has to re-read it anyway.

    A row exists only while a key is overridden — deleting it *is* "reset to
    .env", which is why the value stored is never a copy of the env value.
    """

    __tablename__ = "app_setting"

    key: Mapped[str] = mapped_column(String(64), primary_key=True)
    value: Mapped[str] = mapped_column(Text, nullable=False, default="")
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utcnow,
        onupdate=utcnow,
        server_default=func.now(),
    )

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<AppSetting key={self.key!r}>"


# ---------------------------------------------------------------------------
# Enrichment side tables
# ---------------------------------------------------------------------------
# Everything below holds what *other* sources say. It is separate from the tables
# above for one structural reason and one safety reason.
#
# Structural: ``init_db()`` only runs ``create_all``. New *tables* appear on an
# existing database for free; new *columns* on an existing table silently do not.
# Side tables are therefore the only shape that can grow without a migration.
#
# Safety: keeping enrichment out of ``albums``/``artists``/``tracks`` makes the
# tug-of-war with ``Indexer._apply_metadata`` impossible by construction rather
# than by care, and means the enricher cannot disturb downloading, filing,
# naming, the quality comparison or the wanted decision no matter what an
# upstream claims. The single exception — ``Album.release_type``, written only on
# a majority vote — is spelled out in ``app/enrich/merge.py``.
#
# These carry no ORM relationship back to their parent. ``deps.artist_to_out``
# and ``deps.album_to_out`` are synchronous, and even ``lazy="selectin"`` raises
# ``MissingGreenlet`` on a row that was committed but never refreshed — which is
# exactly what ``Indexer.add_artist`` hands to ``artist_to_out``. They are
# batch-loaded by id in the async collection builders and passed in explicitly.
# Deletion is handled by the database through ``ON DELETE CASCADE``.

#: Bumped whenever a column on one of the tables below changes, or a table joins
#: the family. ``create_all`` checks only that a table *exists*, never its shape,
#: so without this a new column would surface as ``no such column`` at runtime. On
#: a mismatch every table listed in :data:`ENRICHMENT_TABLES` is dropped and
#: rebuilt — every row in them is derived, re-fetchable data, so that is cheap and
#: always safe. ``file_claims`` qualifies on exactly that test: it is re-read from
#: the files in a single pass over the disk.
ENRICHMENT_SCHEMA_VERSION = 2

#: Where that version is recorded, in the generic :class:`Setting` store.
ENRICHMENT_SCHEMA_KEY = "enrichment.schema_version"

#: The tables the version guard owns, children first so drops respect the keys.
ENRICHMENT_TABLES: tuple[str, ...] = (
    "enrichment_state",
    "file_claims",
    "track_metadata",
    "album_metadata",
    "artist_metadata",
)


class ArtistMetadata(Base):
    """What the open sources know about a followed artist.

    Identity is the point of this table. ``isni`` is the artist's real-world
    identifier — stable across every catalogue, unlike a Qobuz id — and is
    cross-checked between MusicBrainz and Wikidata (P213) before it is trusted.
    """

    __tablename__ = "artist_metadata"

    artist_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("artists.id", ondelete="CASCADE"), primary_key=True
    )

    # -- identity ---------------------------------------------------------
    isni: Mapped[str | None] = mapped_column(String(64), nullable=True)
    mb_artist_mbid: Mapped[str | None] = mapped_column(String(36), nullable=True)
    deezer_artist_id: Mapped[str | None] = mapped_column(String(64), nullable=True)
    wikidata_qid: Mapped[str | None] = mapped_column(String(32), nullable=True)

    # How each id was arrived at, and how much evidence backs it. A derivation
    # that later contradicts itself clears the id rather than picking a winner.
    mb_match_method: Mapped[str | None] = mapped_column(String(32), nullable=True)
    mb_match_evidence: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    deezer_match_method: Mapped[str | None] = mapped_column(String(32), nullable=True)

    # -- descriptive ------------------------------------------------------
    name: Mapped[str | None] = mapped_column(String(512), nullable=True)
    sort_name: Mapped[str | None] = mapped_column(String(512), nullable=True)
    disambiguation: Mapped[str | None] = mapped_column(String(512), nullable=True)
    artist_type: Mapped[str | None] = mapped_column(String(32), nullable=True)
    gender: Mapped[str | None] = mapped_column(String(32), nullable=True)
    country: Mapped[str | None] = mapped_column(String(8), nullable=True)
    area: Mapped[str | None] = mapped_column(String(255), nullable=True)
    begin_area: Mapped[str | None] = mapped_column(String(255), nullable=True)
    # Text, not Date: MusicBrainz life spans are legitimately partial ("1975-06").
    life_span_begin: Mapped[str | None] = mapped_column(String(16), nullable=True)
    life_span_end: Mapped[str | None] = mapped_column(String(16), nullable=True)
    life_span_ended: Mapped[bool | None] = mapped_column(Boolean, nullable=True)
    genres: Mapped[str | None] = mapped_column(String(512), nullable=True)

    # -- links and imagery -------------------------------------------------
    official_homepage: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    wikipedia_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    commons_image_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    deezer_image_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)

    # -- biography ---------------------------------------------------------
    # Wikipedia text is CC BY-SA: displaying it without the source link and the
    # licence is a licence breach, so the attribution is stored with the text and
    # the two are written together or not at all.
    bio: Mapped[str | None] = mapped_column(Text, nullable=True)
    bio_source_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    bio_licence: Mapped[str | None] = mapped_column(String(64), nullable=True)

    #: Payload fragments not yet worth promoting to a column.
    extra_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, onupdate=utcnow
    )

    __table_args__ = (
        Index("ix_artist_metadata_mbid", "mb_artist_mbid"),
        Index("ix_artist_metadata_isni", "isni"),
    )

    @property
    def genre_list(self) -> list[str]:
        """``genres`` parsed into a display list."""
        return parse_tags(self.genres)

    @property
    def portrait_url(self) -> str | None:
        """Best available portrait from the enrichment sources.

        Qobuz's own image is *not* considered here — the caller coalesces this
        behind ``Artist.image_url``, so what Qobuz supplied always wins and this
        only fills the gap.
        """
        return self.commons_image_url or self.deezer_image_url

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<ArtistMetadata artist_id={self.artist_id!r} isni={self.isni!r}>"


class AlbumMetadata(Base):
    """What the open sources know about one release.

    ``barcode`` is the hinge of the whole feature: it is the only key that maps a
    Qobuz release onto a MusicBrainz one exactly. Qobuz rarely fills ``upc``, but
    it often *uses the barcode as the album id* — hence ``barcode_source``, which
    records where the key came from so a bad rule can be re-run later.
    """

    __tablename__ = "album_metadata"

    album_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("albums.id", ondelete="CASCADE"), primary_key=True
    )

    # -- the join key ------------------------------------------------------
    barcode: Mapped[str | None] = mapped_column(String(16), nullable=True)
    #: ``upc`` | ``album-id`` | ``deezer`` — where :attr:`barcode` came from.
    barcode_source: Mapped[str | None] = mapped_column(String(16), nullable=True)

    # -- identity ----------------------------------------------------------
    mb_release_mbid: Mapped[str | None] = mapped_column(String(36), nullable=True)
    mb_release_group_mbid: Mapped[str | None] = mapped_column(String(36), nullable=True)
    deezer_album_id: Mapped[str | None] = mapped_column(String(64), nullable=True)
    mb_match_method: Mapped[str | None] = mapped_column(String(32), nullable=True)
    deezer_match_method: Mapped[str | None] = mapped_column(String(32), nullable=True)

    # -- release facts -----------------------------------------------------
    title: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    #: MusicBrainz release-group primary type: Album/Single/EP/Broadcast/Other.
    primary_type: Mapped[str | None] = mapped_column(String(32), nullable=True)
    #: CSV of secondary types (Compilation, Live, Soundtrack, Remix, ...). These
    #: outrank the primary type when deciding what a release actually is.
    secondary_types: Mapped[str | None] = mapped_column(String(255), nullable=True)
    #: Deezer's own vocabulary: album/single/ep/compilation.
    deezer_record_type: Mapped[str | None] = mapped_column(String(32), nullable=True)
    # Text, not Date: MusicBrainz release dates are legitimately partial.
    first_release_date: Mapped[str | None] = mapped_column(String(16), nullable=True)
    release_date: Mapped[str | None] = mapped_column(String(16), nullable=True)
    country: Mapped[str | None] = mapped_column(String(8), nullable=True)
    status: Mapped[str | None] = mapped_column(String(32), nullable=True)
    media_format: Mapped[str | None] = mapped_column(String(64), nullable=True)
    label: Mapped[str | None] = mapped_column(String(512), nullable=True)
    catalog_number: Mapped[str | None] = mapped_column(String(128), nullable=True)
    track_count: Mapped[int | None] = mapped_column(Integer, nullable=True)
    media_count: Mapped[int | None] = mapped_column(Integer, nullable=True)
    genres: Mapped[str | None] = mapped_column(String(512), nullable=True)

    # -- artwork -----------------------------------------------------------
    caa_source_mbid: Mapped[str | None] = mapped_column(String(36), nullable=True)
    caa_front_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    caa_thumb_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    deezer_cover_url: Mapped[str | None] = mapped_column(String(1024), nullable=True)

    # -- the one write-back ------------------------------------------------
    #: What the sources agree this release is, normalised to :class:`ReleaseType`.
    suggested_release_type: Mapped[str | None] = mapped_column(String(32), nullable=True)
    #: ``Album.release_type`` as Qobuz set it, kept so applying is reversible.
    qobuz_release_type: Mapped[str | None] = mapped_column(String(32), nullable=True)
    release_type_applied_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    #: JSON record of who voted for what, per field. Rendered as the review list
    #: when the vote fell short of a majority and nothing was written.
    consensus_json: Mapped[str | None] = mapped_column(Text, nullable=True)

    extra_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, onupdate=utcnow
    )

    __table_args__ = (
        Index("ix_album_metadata_release_mbid", "mb_release_mbid"),
        Index("ix_album_metadata_release_group_mbid", "mb_release_group_mbid"),
        Index("ix_album_metadata_barcode", "barcode"),
    )

    @property
    def genre_list(self) -> list[str]:
        """``genres`` parsed into a display list."""
        return parse_tags(self.genres)

    @property
    def secondary_type_list(self) -> list[str]:
        """``secondary_types`` parsed into a display list."""
        return parse_tags(self.secondary_types)

    @property
    def cover_url(self) -> str | None:
        """Best available cover from the enrichment sources.

        Coalesced *behind* ``Album.image_url`` by the caller: Qobuz's artwork
        matches the exact release it sells and is usually the better answer, so
        this fills the tail rather than replacing it.

        Deezer before the Cover Art Archive, and the order is a quality
        judgement rather than a ladder position. Deezer serves the label's own
        1000x1000 artwork for the exact release; the Archive serves whatever a
        contributor uploaded, which ranges from a press-kit scan to a phone
        photograph of a jewel case, and it is keyed by an MBID rather than by
        the barcode — so the one release it can answer for is one another source
        already identified. Preferring it meant a hand-held snapshot displacing
        official art, which is the wrong way round.
        """
        return self.deezer_cover_url or self.caa_front_url

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<AlbumMetadata album_id={self.album_id!r} barcode={self.barcode!r}>"


class TrackMetadata(Base):
    """Per-track identifiers and the verdict of the audio fingerprint.

    Tracks are never looked up on their own: recordings arrive inside the release
    lookup at no extra API cost, and fingerprints are computed from files already
    on disk. That is why there is no :class:`EnrichmentState` row for a track.
    """

    __tablename__ = "track_metadata"

    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("tracks.id", ondelete="CASCADE"), primary_key=True
    )

    mb_recording_mbid: Mapped[str | None] = mapped_column(String(36), nullable=True)
    mb_release_track_mbid: Mapped[str | None] = mapped_column(String(36), nullable=True)
    isrc: Mapped[str | None] = mapped_column(String(64), nullable=True)
    deezer_track_id: Mapped[str | None] = mapped_column(String(64), nullable=True)
    title: Mapped[str | None] = mapped_column(String(1024), nullable=True)
    #: Recording length in milliseconds, as MusicBrainz reports it.
    length_ms: Mapped[int | None] = mapped_column(Integer, nullable=True)
    #: ``release-position`` when the whole album's layout matched. A mapping is
    #: applied in full or not at all — a half-shuffled tracklist is worse than
    #: none, so one contradicting ISRC abandons the lot.
    match_method: Mapped[str | None] = mapped_column(String(32), nullable=True)

    # -- fingerprint -------------------------------------------------------
    acoustid: Mapped[str | None] = mapped_column(String(64), nullable=True)
    fingerprint_state: Mapped[FingerprintState | None] = mapped_column(
        _FingerprintStateType, nullable=True
    )
    fingerprinted_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    #: Set when this track's audio is byte-for-byte the same recording as another
    #: track already in the library. Drives duplicate collapsing for singles and
    #: EPs, which is where the same recording is reissued most often.
    duplicate_of_track_id: Mapped[str | None] = mapped_column(String(64), nullable=True)

    extra_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, onupdate=utcnow
    )

    __table_args__ = (
        Index("ix_track_metadata_recording_mbid", "mb_recording_mbid"),
        Index("ix_track_metadata_acoustid", "acoustid"),
        Index("ix_track_metadata_isrc", "isrc"),
    )

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<TrackMetadata track_id={self.track_id!r} acoustid={self.acoustid!r}>"


class FileClaim(Base):
    """What the tags in a file **claim** — read off disk, believed by nothing.

    This is deliberately a separate table from :class:`TrackMetadata`, and the
    separation is the point rather than tidiness. ``track_metadata`` holds
    *verified* enrichment output: an MBID that got there because a barcode matched
    exactly, or because a human picked a candidate, or because the audio itself
    confirmed it. Every column here holds a string somebody else's tagger wrote,
    of unknown provenance and unknown correctness — a hand-typed barcode, an MBID
    copied from the wrong release, an ISRC belonging to a different mix, or a
    ``qobuzarr_qid`` from a file that was duplicated rather than moved.

    Conflating the two is the entire bug class this design exists to prevent. A
    claim written into the verified table is indistinguishable from a fact five
    minutes later: it is then read back as evidence, propagated to the release, to
    the artist, and written into every other file on the next re-tag — and nothing
    downstream can tell that the whole chain rests on a string a stranger typed.
    One table for hypotheses and one for conclusions makes that impossible to do by
    accident, because promoting a claim means writing a different row on purpose.

    So: a claim may **narrow** a search — it is a hint about where to look, and a
    cheap one, since reading it costs a tag parse rather than an HTTP request — and
    a claim may **reject** a candidate. It may never select one. That is the same
    rule ``app/enrich/matching.py`` already enforces for names, applied to the one
    other input that arrives unverified.

    The column widths are looser than the verified table's on purpose: ``barcode``
    is ``String(64)`` where :attr:`AlbumMetadata.barcode` is ``String(16)``,
    because this stores whatever was in the tag, not a barcode that has been
    checked for being one.
    """

    __tablename__ = "file_claims"

    track_id: Mapped[str] = mapped_column(
        String(64), ForeignKey("tracks.id", ondelete="CASCADE"), primary_key=True
    )

    isrc: Mapped[str | None] = mapped_column(String(64), nullable=True)
    barcode: Mapped[str | None] = mapped_column(String(64), nullable=True)
    mb_recording_mbid: Mapped[str | None] = mapped_column(String(64), nullable=True)
    mb_release_mbid: Mapped[str | None] = mapped_column(String(64), nullable=True)
    mb_release_group_mbid: Mapped[str | None] = mapped_column(String(64), nullable=True)
    #: The track artist's claimed MBID, and the *album* artist's, kept apart
    #: because on a compilation they legitimately differ and folding them together
    #: would make every track look like it contradicted its release.
    mb_artist_mbid: Mapped[str | None] = mapped_column(String(64), nullable=True)
    mb_album_artist_mbid: Mapped[str | None] = mapped_column(String(64), nullable=True)
    #: A qid this program wrote into the file on a previous pass. The strongest
    #: hint available and still only a hint: copying a file copies the tag, so two
    #: files can claim the same qid and at most one of them can be right.
    qobuzarr_qid: Mapped[str | None] = mapped_column(String(32), nullable=True)

    #: When the tags were last read. Says nothing about whether they are true —
    #: only how stale this row is relative to the file.
    read_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, onupdate=utcnow
    )

    __table_args__ = (
        Index("ix_file_claims_barcode", "barcode"),
        Index("ix_file_claims_release_mbid", "mb_release_mbid"),
        Index("ix_file_claims_recording_mbid", "mb_recording_mbid"),
        Index("ix_file_claims_qid", "qobuzarr_qid"),
        Index("ix_file_claims_isrc", "isrc"),
    )

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<FileClaim track_id={self.track_id!r} barcode={self.barcode!r}>"


#: :attr:`FolderBinding.state` — what has been settled about a directory.
BINDING_BOUND = "bound"
BINDING_NOT_IN_CATALOGUE = "not_in_catalogue"
BINDING_STATES: tuple[str, ...] = (BINDING_BOUND, BINDING_NOT_IN_CATALOGUE)


class FolderBinding(Base):
    """"This directory **is** that Qobuz release" — settled by the audio, not a name.

    The disk scan binds a folder to a catalogue row by normalising the tagged
    album title and the folder name and looking for a release of that artist with
    a matching key (:meth:`app.core.scanner.LibraryScanner._pick_album`). That is
    the right default — it is free, local, and correct for most of a library — and
    it has a hard ceiling: a folder whose name is a *different string* for the same
    record can never match, however the normaliser is tuned. Measured on one real
    library, 104 of 611 folders were unmatched, and the failures are all the same
    shape:

        disk: Play: The Guitar Album      catalogue: Play
        disk: Muddy Wolf at Red Rocks     catalogue: Muddy Wolf At Red Rocks (Live)
        disk: Live! At the Ryman          catalogue: Live! at The Ryman (Live)

    Each of those is a release the user owns that was sitting in the *wanted* list
    waiting to be downloaded again. Loosening the normaliser is not the fix — it is
    the same move in the opposite direction, and it buys false positives, which are
    worse: binding a folder to the wrong edition writes somebody else's identifiers
    into the files.

    So a row here is an **exact** statement produced by
    :mod:`app.core.discovery`'s chain — AcoustID identifies the recordings, the
    release group's barcodes come from MusicBrainz, and a barcode picks the Qobuz
    album out of a search. A name is used only to decide which twenty results come
    back and never to choose among them, so this table cannot record a guess. It is
    keyed on the directory because that is what was identified: no ``Track`` rows
    exist for an unmatched folder, so there is nothing else to hang it on.

    **Not in :data:`ENRICHMENT_TABLES`, deliberately.** Every row in those five is
    re-fetchable, which is what makes dropping and rebuilding them safe. This is
    re-*derivable* but not cheaply — each row cost a fingerprint of every file in
    the directory plus a MusicBrainz browse and a Qobuz search — and a schema bump
    that silently spent that again on a few hundred folders would be a surprise
    measured in hours. It is a plain new table, so ``create_all`` adds it and no
    migration script is needed.
    """

    __tablename__ = "folder_bindings"

    #: Absolute path of the album directory, as the scanner reports it.
    path: Mapped[str] = mapped_column(String(1024), primary_key=True)

    #: What was settled about this directory. ``bound`` names a Qobuz release in
    #: :attr:`album_id`; ``not_in_catalogue`` says there is no such release to
    #: name, and :attr:`album_id` is ``NULL``.
    #:
    #: **Only a person may write ``not_in_catalogue``.** The automatic chain
    #: failing to find a release is not evidence that none exists — it is the
    #: same distinction :func:`app.core.integrity.classify` draws between
    #: ``UNKNOWN`` and ``CHANGED`` — and a machine allowed to conclude "this is
    #: not a real release" would quietly retire folders it merely could not
    #: identify. A library legitimately holds records no catalogue sells: albums
    #: assembled from covers that were never released elsewhere, game
    #: soundtracks, a radio bootleg. Each is a fact a human knows and no upstream
    #: can confirm.
    state: Mapped[str] = mapped_column(String(24), nullable=False, default="bound")

    #: The Qobuz album this directory holds, or ``NULL`` for ``not_in_catalogue``.
    #: No foreign key on purpose: the binding outlives an ``albums`` row being
    #: pruned and re-created by the indexer, and a cascade delete would silently
    #: spend the identification again. A binding naming an album that is not
    #: there is simply skipped.
    album_id: Mapped[str | None] = mapped_column(String(64), nullable=True, index=True)

    #: How this was decided. ``audio-barcode`` is the automatic chain;
    #: ``manual`` is a person, and a person is never overruled by a later pass.
    method: Mapped[str] = mapped_column(String(32), nullable=False, default="audio-barcode")

    #: Why, in a person's own words. Only they know that a folder holds covers
    #: that were never released, so only they can write down why nothing will
    #: ever match it — and a marker with no reason is one nobody dares undo.
    note: Mapped[str | None] = mapped_column(Text, nullable=True)

    #: The barcode that picked the Qobuz album, and the release group it came
    #: from. Evidence, so a binding can be argued with rather than only trusted.
    barcode: Mapped[str | None] = mapped_column(String(32), nullable=True)
    mb_release_group_mbid: Mapped[str | None] = mapped_column(String(64), nullable=True)

    bound_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, onupdate=utcnow
    )

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"<FolderBinding path={self.path!r} album_id={self.album_id!r}>"


class EnrichmentState(Base):
    """Per (entity, source) fetch bookkeeping — the only table the enricher polls.

    Splitting *how the fetching is going* from *what we learned* keeps one table
    to ask "what is due?" even though an artist can be simultaneously matched on
    MusicBrainz, gated on AcoustID and failing on Wikidata.

    ``entity_id`` is polymorphic, so it carries no foreign key and deleting an
    artist leaves orphans behind. That is paid for with a prune step in nightly
    housekeeping — cheaper than three parallel state tables with real keys.
    """

    __tablename__ = "enrichment_state"

    entity_type: Mapped[EnrichmentEntity] = mapped_column(
        _EnrichmentEntityType, primary_key=True
    )
    entity_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    source: Mapped[EnrichmentSource] = mapped_column(
        _EnrichmentSourceType, primary_key=True
    )

    #: One of ``pending``/``ok``/``not_found``/``ambiguous``/``no_key``/
    #: ``gated``/``failed``/``rejected`` — the vocabulary in
    #: :mod:`app.enrich.errors`, which the matchers raise directly so the state
    #: is never re-derived from an exception type. ``rejected`` is the one value
    #: no matcher can produce: it is written only by
    #: :meth:`app.core.enricher.Enricher.reject`, which is what makes it mean
    #: "a person looked at this and said no".
    state: Mapped[str] = mapped_column(String(16), nullable=False, default="pending")
    #: The key this attempt used: a normalised barcode, or the upstream id. When
    #: it changes, the row re-opens by itself — which is how a corrected UPC
    #: re-triggers matching without anyone asking it to.
    match_key: Mapped[str | None] = mapped_column(String(128), nullable=True)
    attempts: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    priority: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    last_error: Mapped[str | None] = mapped_column(Text, nullable=True)
    last_attempt_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    #: When to try again. ``NULL`` means never — used for ``no_key`` and
    #: ``gated``, which cannot improve until their inputs change.
    next_attempt_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, onupdate=utcnow
    )

    __table_args__ = (
        Index("ix_enrichment_due", "next_attempt_at", "priority"),
        Index("ix_enrichment_state_state", "state"),
        Index("ix_enrichment_entity", "entity_type", "entity_id"),
    )

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return (
            f"<EnrichmentState {self.entity_type}:{self.entity_id} "
            f"{self.source} state={self.state!r}>"
        )
