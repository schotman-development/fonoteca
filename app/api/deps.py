"""Shared FastAPI dependencies, template plumbing and read-model builders.

This module is the seam between the HTTP layer (:mod:`app.api.routes_ui` and
:mod:`app.api.routes_api`) and the rest of the application:

* **Templates** — a single configured :class:`~fastapi.templating.Jinja2Templates`
  instance plus the filters/globals every page relies on, and :func:`render`,
  which injects the shared context (app name, version, nav state, banners).
* **Runtime singletons** — thin, defensive accessors for the objects that live in
  ``app.core.state`` (Qobuz client, indexer, download worker, scheduler, rate
  limiter).  They are looked up lazily and by several plausible attribute names,
  and every one of them degrades to ``None`` rather than raising, so the web UI
  still renders on a machine with no credentials, no network, or a half-wired
  application.  See the module docstring of ``app.core.state`` for the contract.
* **Read models** — async helpers that turn database rows into the Pydantic
  models declared in :mod:`app.schemas`.  Both the HTML and the JSON routers use
  these, so the two views can never drift apart.

Nothing here performs writes; the routers own all mutations.
"""

from __future__ import annotations

import importlib
import inspect
import math
from collections.abc import Iterable, Sequence
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Any

from fastapi import Depends, Request
from fastapi.templating import Jinja2Templates
from sqlalchemy import Select, case, func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app import __version__
from app.config import FORMAT_LABELS, Settings, get_settings
from app.core import quality
from app.db import get_session
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    Artist,
    MonitorMode,
    QueueItem,
    QueueState,
    RELEASE_TYPES,
    Track,
    TrackStatus,
    utcnow,
)
from app.schemas import (
    ActivityOut,
    AlbumOut,
    ArtistOut,
    IndexerStatusOut,
    LibraryStatsOut,
    QueueItemOut,
    QueueStatsOut,
    RateLimitStatusOut,
    SettingsOut,
    StatusOut,
    TrackOut,
)

__all__ = [
    "APP_NAME",
    "BASE_DIR",
    "TEMPLATES_DIR",
    "STATIC_DIR",
    "templates",
    "render",
    "SessionDep",
    "SettingsDep",
    "get_settings_dep",
    "get_core_state",
    "get_qobuz_client",
    "get_indexer",
    "get_queue_worker",
    "get_scheduler",
    "get_library_scanner",
    "get_library_importer",
    "get_rate_limiter",
    "get_started_at",
    "call_first",
    "as_utc",
    "seconds_since",
    "library_stats",
    "albums_by_status",
    "queue_stats",
    "indexer_status",
    "rate_limit_status",
    "recent_activity",
    "build_status",
    "build_settings_out",
    "naming_preview",
    "artist_to_out",
    "album_to_out",
    "track_to_out",
    "queue_item_to_out",
    "activity_to_out",
    "list_artists",
    "list_albums_for_artist",
    "list_wanted_albums",
    "list_queue_items",
    "list_activity",
    "nav_counts",
    "library_scan_status",
    "library_import_status",
    "MISSING_STATUSES",
    "artist_album_counts",
    "banner_context",
]

logger = get_logger(__name__)

#: Product name shown in the navbar and page titles.
APP_NAME = "Qobuzarr"

#: Repository root; templates and static assets live beside ``app/``.
BASE_DIR: Path = Path(__file__).resolve().parent.parent.parent
TEMPLATES_DIR: Path = BASE_DIR / "templates"
STATIC_DIR: Path = BASE_DIR / "static"


# ---------------------------------------------------------------------------
# Small formatting helpers, exposed to templates as filters
# ---------------------------------------------------------------------------
def as_utc(value: datetime | str | None) -> datetime | None:
    """Return *value* as a timezone-aware UTC datetime.

    SQLite round-trips ``DateTime(timezone=True)`` columns as *naive* datetimes,
    so anything read back from the database must be re-stamped before it can be
    compared against :func:`app.models.utcnow`.

    ISO-8601 strings are accepted too, because some payloads reach a template
    after a JSON round-trip (the stored disk-scan summary, for one) and the
    ``ago``/``dt`` filters must not explode on them. An unparseable string is
    ``None`` rather than an error.
    """
    if value is None:
        return None
    if isinstance(value, str):
        try:
            value = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return None
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def _maybe_datetime(value: Any) -> datetime | None:
    """Coerce *value* to an aware UTC datetime, or ``None`` if it is not one."""
    return as_utc(value) if isinstance(value, datetime) else None


def seconds_since(value: datetime | None) -> float | None:
    """Seconds elapsed since *value*, or ``None`` when it is unset."""
    stamped = as_utc(value)
    if stamped is None:
        return None
    return (utcnow() - stamped).total_seconds()


def fmt_datetime(value: datetime | None, fmt: str = "%Y-%m-%d %H:%M") -> str:
    """Render a timestamp in UTC, or an em dash when missing."""
    stamped = as_utc(value)
    return stamped.strftime(fmt) if stamped else "—"


def fmt_date(value: Any) -> str:
    """Render a ``date``/``datetime`` as ISO ``YYYY-MM-DD``."""
    if value is None:
        return "—"
    if isinstance(value, datetime):
        stamped = as_utc(value)
        return stamped.date().isoformat() if stamped else "—"
    return str(value)


def fmt_ago(value: datetime | None) -> str:
    """Render a coarse "3m ago" style relative time."""
    elapsed = seconds_since(value)
    if elapsed is None:
        return "never"
    if elapsed < 0:
        return "just now"
    return f"{fmt_span(elapsed)} ago"


def fmt_span(seconds: float | int | None) -> str:
    """Render a duration in seconds as a compact ``2h 5m`` style string."""
    if seconds is None:
        return "—"
    total = int(max(0, seconds))
    if total < 60:
        return f"{total}s"
    if total < 3600:
        return f"{total // 60}m {total % 60:02d}s"
    if total < 86400:
        return f"{total // 3600}h {(total % 3600) // 60:02d}m"
    return f"{total // 86400}d {(total % 86400) // 3600:02d}h"


def fmt_duration(seconds: float | int | None) -> str:
    """Render a track/album duration as ``m:ss`` or ``h:mm:ss``."""
    if not seconds:
        return "—"
    total = int(seconds)
    hours, remainder = divmod(total, 3600)
    minutes, secs = divmod(remainder, 60)
    if hours:
        return f"{hours}:{minutes:02d}:{secs:02d}"
    return f"{minutes}:{secs:02d}"


def fmt_size(num_bytes: int | float | None) -> str:
    """Render a byte count with binary units."""
    if not num_bytes:
        return "—"
    units = ("B", "KiB", "MiB", "GiB", "TiB")
    size = float(num_bytes)
    index = min(int(math.log(size, 1024)) if size > 0 else 0, len(units) - 1)
    return f"{size / (1024 ** index):.1f} {units[index]}"


def fmt_quality(album: Any) -> str:
    """Short quality badge text for an album row."""
    depth = getattr(album, "max_bit_depth", None)
    rate = getattr(album, "max_sampling_rate", None)
    if depth and rate:
        return f"{int(depth)}bit/{float(rate):g}kHz"
    if getattr(album, "hires", False):
        return "Hi-Res"
    return "—"


def fmt_format_label(format_id: Any) -> str:
    """Short label for a Qobuz ``format_id``, e.g. ``"FLAC 24-96"``.

    Empty string for ``None``, so ``{% if %}``-free templates collapse cleanly.
    """
    return quality.format_label(format_id)


def enum_value(value: Any) -> str:
    """Return ``value.value`` for enums, ``str(value)`` otherwise."""
    return getattr(value, "value", value if value is None else str(value)) or ""


def status_class(value: Any) -> str:
    """CSS modifier for an :class:`AlbumStatus`/:class:`QueueState` chip.

    Pairs with the ``.tag--*`` rules in ``static/style.css``.
    """
    return f"tag--{enum_value(value) or 'unknown'}"


# ---------------------------------------------------------------------------
# Templates
# ---------------------------------------------------------------------------
templates = Jinja2Templates(directory=str(TEMPLATES_DIR))
templates.env.filters.update(
    {
        "dt": fmt_datetime,
        "date": fmt_date,
        "ago": fmt_ago,
        "span": fmt_span,
        "duration": fmt_duration,
        "size": fmt_size,
        "quality": fmt_quality,
        "format_label": fmt_format_label,
        "enum_value": enum_value,
        "status_class": status_class,
    }
)
templates.env.globals.update(
    {
        "app_name": APP_NAME,
        "app_version": __version__,
        "release_types": list(RELEASE_TYPES),
        "monitor_modes": [m.value for m in MonitorMode],
        "album_statuses": [s.value for s in AlbumStatus],
        "queue_states": [s.value for s in QueueState],
        "activity_levels": [level.value for level in ActivityLevel],
        "format_labels": dict(FORMAT_LABELS),
    }
)


def banner_context(settings: Settings | None = None) -> list[dict[str, str]]:
    """Friendly warnings shown at the top of every page.

    Never raises: a missing client or an unresolved secret is reported as a
    banner, not an exception.
    """
    settings = settings or get_settings()
    banners: list[dict[str, str]] = []

    if not settings.has_credentials:
        banners.append(
            {
                "level": "error",
                "title": "Qobuz credentials missing",
                "message": (
                    "Set QOBUZ_APP_ID and QOBUZ_USER_AUTH_TOKEN in .env, then "
                    "restart. Searching, indexing and downloading are disabled "
                    "until then."
                ),
            }
        )

    client = get_qobuz_client()
    if settings.has_credentials and client is None:
        banners.append(
            {
                "level": "warning",
                "title": "Qobuz client not started",
                "message": (
                    "The application started without a Qobuz client. Library "
                    "browsing works, but live search and downloads do not."
                ),
            }
        )
    elif client is not None and not getattr(client, "has_app_secret", False):
        banners.append(
            {
                "level": "warning",
                "title": "App secret unresolved",
                "message": (
                    "Qobuzarr has not yet derived a working app secret. It will "
                    "try again on the next signed request; set QOBUZ_APP_SECRET "
                    "in .env to skip derivation entirely."
                ),
            }
        )

    limiter = get_rate_limiter()
    breaker = getattr(limiter, "breaker", None) if limiter is not None else None
    if breaker is not None:
        try:
            if breaker.is_open():
                banners.append(
                    {
                        "level": "warning",
                        "title": "Circuit breaker open",
                        "message": (
                            "Qobuz returned repeated rate-limit errors, so "
                            f"outbound calls are paused for "
                            f"{fmt_span(breaker.seconds_remaining())}."
                        ),
                    }
                )
        except Exception as exc:  # noqa: BLE001 - the banner must never break a page
            logger.debug("Circuit-breaker probe failed: %s", exc)

    return banners


def render(
    request: Request,
    template_name: str,
    context: dict[str, Any] | None = None,
    *,
    active: str = "",
    status_code: int = 200,
) -> Any:
    """Render *template_name* with the shared page context applied.

    Args:
        request: The incoming request (Starlette requires it for url_for).
        template_name: Template path relative to ``templates/``.
        context: Page-specific variables.
        active: Nav item to highlight (``dashboard``/``artists``/...).
        status_code: HTTP status for the response.
    """
    settings = get_settings()
    payload: dict[str, Any] = {
        "active": active,
        "settings": settings,
        "banners": banner_context(settings),
        "now": utcnow(),
    }
    payload.update(context or {})
    return templates.TemplateResponse(
        request, template_name, payload, status_code=status_code
    )


# ---------------------------------------------------------------------------
# Dependencies
# ---------------------------------------------------------------------------
def get_settings_dep() -> Settings:
    """FastAPI dependency returning the cached :class:`Settings` singleton."""
    return get_settings()


SessionDep = Depends(get_session)
SettingsDep = Depends(get_settings_dep)


# ---------------------------------------------------------------------------
# Runtime singletons (app.core.state), looked up defensively
# ---------------------------------------------------------------------------
_STATE_MODULE_NAME = "app.core.state"
_state_module_missing = False


def _state_module() -> Any | None:
    """Import ``app.core.state`` lazily, tolerating its absence."""
    global _state_module_missing
    if _state_module_missing:
        return None
    try:
        return importlib.import_module(_STATE_MODULE_NAME)
    except Exception as exc:  # noqa: BLE001 - optional at import time by design
        _state_module_missing = True
        logger.debug("%s unavailable: %s", _STATE_MODULE_NAME, exc)
        return None


def get_core_state() -> Any | None:
    """Return the application-state container, or ``None`` when unavailable.

    Accepts either a ``get_state()``/``get_app_state()`` factory, a module-level
    ``state``/``app_state`` object, or plain module-level attributes.
    """
    module = _state_module()
    if module is None:
        return None
    for name in ("state_or_none", "get_state", "get_app_state"):
        factory = getattr(module, name, None)
        if callable(factory):
            try:
                container = factory()
            except Exception as exc:  # noqa: BLE001 - not started yet is normal
                logger.debug("%s.%s() failed: %s", _STATE_MODULE_NAME, name, exc)
                continue
            if container is not None:
                return container
    for name in ("state", "app_state", "STATE"):
        obj = getattr(module, name, None)
        if obj is not None:
            return obj
    return module


def _lookup(names: Sequence[str]) -> Any | None:
    """First non-``None`` attribute of the state container among *names*.

    Zero-argument callables whose name starts with ``get_`` are invoked.
    """
    state = get_core_state()
    if state is None:
        return None
    for name in names:
        value = getattr(state, name, None)
        if value is None:
            continue
        if name.startswith("get_") and callable(value):
            try:
                value = value()
            except Exception as exc:  # noqa: BLE001
                logger.debug("state.%s() failed: %s", name, exc)
                continue
        if value is not None:
            return value
    return None


def get_qobuz_client() -> Any | None:
    """The shared :class:`~app.qobuz.client.QobuzClient`, if one is wired up."""
    return _lookup(("client", "qobuz_client", "qobuz", "get_client", "get_qobuz_client"))


def get_indexer() -> Any | None:
    """The background indexer service, if one is wired up."""
    return _lookup(("indexer", "indexer_service", "get_indexer"))


def get_queue_worker() -> Any | None:
    """The sequential download worker, if one is wired up."""
    return _lookup(
        (
            "queue",
            "queue_worker",
            "downloader",
            "download_worker",
            "worker",
            "get_queue",
            "get_queue_worker",
            "get_downloader",
        )
    )


def get_scheduler() -> Any | None:
    """The APScheduler instance, if one is wired up."""
    return _lookup(("scheduler", "get_scheduler"))


def get_library_scanner() -> Any | None:
    """The shared :class:`~app.core.scanner.LibraryScanner`, if one is wired up."""
    return _lookup(("scanner", "library_scanner", "get_scanner"))


def get_library_importer() -> Any | None:
    """The shared :class:`~app.core.importer.LibraryImporter`, if one is wired up."""
    return _lookup(("importer", "library_importer", "get_importer"))


def get_rate_limiter() -> Any | None:
    """The single global :class:`~app.qobuz.ratelimit.RateLimiter`."""
    limiter = _lookup(("limiter", "rate_limiter", "ratelimiter", "get_limiter", "get_rate_limiter"))
    if limiter is not None:
        return limiter
    client = get_qobuz_client()
    return getattr(client, "limiter", None) if client is not None else None


def get_started_at() -> datetime | None:
    """Process start time recorded by the application state, if any."""
    value = _lookup(("started_at", "start_time", "get_started_at"))
    return value if isinstance(value, datetime) else None


async def call_first(
    obj: Any, names: Sequence[str], *args: Any, **kwargs: Any
) -> tuple[bool, Any]:
    """Call the first attribute of *obj* named in *names*.

    Awaits the result when it is awaitable. Returns ``(handled, result)`` where
    ``handled`` is ``False`` when *obj* is ``None`` or exposes none of *names*,
    letting callers fall back to a database-only implementation.

    Raises:
        Exception: whatever the underlying call raises — failures are the
            caller's business, only *absence* is swallowed here.
    """
    if obj is None:
        return False, None
    for name in names:
        method = getattr(obj, name, None)
        if method is None or not callable(method):
            continue
        result = method(*args, **kwargs)
        if inspect.isawaitable(result):
            result = await result
        return True, result
    return False, None


# ---------------------------------------------------------------------------
# Read models: entities
# ---------------------------------------------------------------------------
def artist_to_out(
    artist: Artist,
    *,
    album_count: int | None = None,
    wanted_count: int | None = None,
    downloaded_count: int | None = None,
) -> ArtistOut:
    """Convert an :class:`Artist` row into :class:`ArtistOut`."""
    out = ArtistOut.model_validate(artist)
    out.album_count = album_count
    out.wanted_count = wanted_count
    out.downloaded_count = downloaded_count
    return out


def track_to_out(track: Track) -> TrackOut:
    """Convert a :class:`Track` row into :class:`TrackOut`."""
    return TrackOut.model_validate(track)


def download_target_format_id(artist: Any = None) -> int:
    """The Qobuz ``format_id`` a download started right now would ask for.

    The artist's quality profile (or the global default), clamped to the formats
    the subscription actually entitles us to — the same two steps
    :meth:`app.core.downloader.AlbumDownloader._resolve_format_id` takes, so the
    *Upgrade* button and the download loop cannot disagree.

    When no client is wired up the entitlement clamp is skipped, which is also
    what ``QobuzClient.allowed_format_ids()`` does before login: assume
    everything is allowed and let the answer Qobuz returns be the truth.
    """
    settings = get_settings()
    profile = getattr(artist, "quality_profile", None) or settings.default_quality_profile
    requested = quality.format_for_profile(profile, settings.default_format_id)

    best = getattr(get_qobuz_client(), "best_format_id", None)
    if callable(best):
        try:
            return int(best(requested))
        except Exception as exc:  # noqa: BLE001 - a read model must never 500
            logger.debug("best_format_id(%s) failed: %s", requested, exc)
    return requested


def _live_queue_state(album: Album) -> QueueState | None:
    """The album's in-flight queue state, or ``None`` when it is not queued.

    A *downloaded* album keeps its ``DOWNLOADED`` status while an upgrade sits
    in the queue — ``queue_album()`` only promotes wanted-ish albums — so the
    album status alone cannot tell a table whether to offer the button again.
    """
    for item in getattr(album, "queue_items", None) or ():
        if item.state in (QueueState.PENDING, QueueState.ACTIVE):
            return item.state
    return None


def album_to_out(album: Album, *, include_tracks: bool = False) -> AlbumOut:
    """Convert an :class:`Album` row into :class:`AlbumOut`.

    ``include_tracks`` requires the ``tracks`` relationship to be loaded; it is
    ``lazy="selectin"``, so it always is for rows fetched through the ORM — and
    so is ``queue_items``, which is why the quality comparison below costs no
    extra queries.
    """
    out = AlbumOut.model_validate(album)
    out.year = album.year
    artist = getattr(album, "artist", None)
    out.artist_name = getattr(artist, "name", None)
    if include_tracks:
        out.tracks = [track_to_out(track) for track in album.tracks]

    out.queue_state = _live_queue_state(album)
    tracks = getattr(album, "tracks", None)
    out.owned_format_id = quality.owned_format_id(tracks)
    if album.status is AlbumStatus.DOWNLOADED:
        upgrade = quality.upgrade_available(
            album, tracks, download_target_format_id(artist)
        )
        out.upgrade_format_id = upgrade[1] if upgrade else None
    return out


def queue_item_to_out(item: QueueItem) -> QueueItemOut:
    """Convert a :class:`QueueItem` row into :class:`QueueItemOut`."""
    out = QueueItemOut.model_validate(item)
    out.progress_percent = item.progress_percent
    album = getattr(item, "album", None)
    if album is not None:
        out.album_title = album.display_title
        out.album_image_url = album.image_url
        out.album_monitored = bool(album.monitored)
        out.artist_id = album.artist_id
        out.artist_name = getattr(getattr(album, "artist", None), "name", None)
    return out


def activity_to_out(entry: Activity) -> ActivityOut:
    """Convert an :class:`Activity` row into :class:`ActivityOut`."""
    out = ActivityOut.model_validate(entry)
    out.artist_name = getattr(getattr(entry, "artist", None), "name", None)
    album = getattr(entry, "album", None)
    out.album_title = album.display_title if album is not None else None
    return out


# ---------------------------------------------------------------------------
# Read models: collections
# ---------------------------------------------------------------------------
_ARTIST_SORTS: dict[str, Any] = {
    "name": Artist.name,
    "added_at": Artist.added_at,
    "last_checked_at": Artist.last_checked_at,
    "albums_count": Artist.albums_count,
}


async def artist_album_counts(
    session: AsyncSession, artist_ids: Iterable[str]
) -> dict[str, dict[str, int]]:
    """Per-artist album roll-ups keyed by artist id.

    Returns ``{artist_id: {"albums": n, "wanted": n, "downloaded": n}}``.
    """
    ids = [str(a) for a in artist_ids]
    if not ids:
        return {}
    stmt = (
        select(Album.artist_id, Album.status, func.count(Album.id))
        .where(Album.artist_id.in_(ids))
        .group_by(Album.artist_id, Album.status)
    )
    rows = (await session.execute(stmt)).all()
    counts: dict[str, dict[str, int]] = {
        artist_id: {"albums": 0, "wanted": 0, "downloaded": 0} for artist_id in ids
    }
    for artist_id, status, count in rows:
        bucket = counts.setdefault(
            artist_id, {"albums": 0, "wanted": 0, "downloaded": 0}
        )
        bucket["albums"] += count
        if status in (AlbumStatus.WANTED, AlbumStatus.QUEUED, AlbumStatus.DOWNLOADING):
            bucket["wanted"] += count
        elif status is AlbumStatus.DOWNLOADED:
            bucket["downloaded"] += count
    return counts


async def list_artists(
    session: AsyncSession,
    *,
    query: str | None = None,
    monitored: bool | None = None,
    sort: str = "name",
    order: str = "asc",
    limit: int = 200,
    offset: int = 0,
    with_counts: bool = True,
) -> tuple[list[ArtistOut], int]:
    """Page through followed artists, newest sort options first.

    Returns ``(items, total)``.
    """
    stmt: Select[Any] = select(Artist)
    count_stmt = select(func.count(Artist.id))
    if query:
        pattern = f"%{query.strip()}%"
        stmt = stmt.where(Artist.name.ilike(pattern))
        count_stmt = count_stmt.where(Artist.name.ilike(pattern))
    if monitored is not None:
        stmt = stmt.where(Artist.monitored.is_(monitored))
        count_stmt = count_stmt.where(Artist.monitored.is_(monitored))

    column = _ARTIST_SORTS.get(sort, Artist.name)
    stmt = stmt.order_by(column.desc() if order == "desc" else column.asc())
    stmt = stmt.limit(max(1, limit)).offset(max(0, offset))

    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())

    counts = (
        await artist_album_counts(session, [row.id for row in rows])
        if with_counts
        else {}
    )
    items = [
        artist_to_out(
            row,
            album_count=counts.get(row.id, {}).get("albums"),
            wanted_count=counts.get(row.id, {}).get("wanted"),
            downloaded_count=counts.get(row.id, {}).get("downloaded"),
        )
        for row in rows
    ]
    return items, total


async def list_albums_for_artist(
    session: AsyncSession,
    artist_id: str,
    *,
    status: AlbumStatus | None = None,
    release_type: str | None = None,
    query: str | None = None,
    limit: int = 500,
    offset: int = 0,
) -> tuple[list[AlbumOut], int]:
    """All known releases for one artist, newest first.

    ``query`` is a case-insensitive substring match over the release title, its
    edition/version and its label — the three things you actually remember when
    hunting for one album in a 200-release discography.
    """
    stmt = select(Album).where(Album.artist_id == str(artist_id))
    count_stmt = select(func.count(Album.id)).where(Album.artist_id == str(artist_id))
    if status is not None:
        stmt = stmt.where(Album.status == status)
        count_stmt = count_stmt.where(Album.status == status)
    if release_type:
        stmt = stmt.where(Album.release_type == release_type)
        count_stmt = count_stmt.where(Album.release_type == release_type)
    if query and query.strip():
        pattern = f"%{query.strip()}%"
        matches = (
            Album.title.ilike(pattern)
            | Album.version.ilike(pattern)
            | Album.label.ilike(pattern)
        )
        stmt = stmt.where(matches)
        count_stmt = count_stmt.where(matches)
    stmt = (
        stmt.order_by(Album.release_date.desc().nullslast(), Album.title.asc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return [album_to_out(row) for row in rows], total


#: Album states that mean "we want this and it is not on disk". ``QUEUED`` and
#: ``DOWNLOADING`` are deliberately excluded — those are already in flight and
#: belong on the queue page, not the backlog.
MISSING_STATUSES: tuple[AlbumStatus, ...] = (AlbumStatus.WANTED, AlbumStatus.FAILED)


async def list_wanted_albums(
    session: AsyncSession,
    *,
    query: str | None = None,
    status: AlbumStatus | None = None,
    monitored: bool | None = True,
    limit: int = 500,
    offset: int = 0,
) -> tuple[list[AlbumOut], int]:
    """The cross-artist backlog: releases wanted but not yet on disk.

    Ordered oldest-wanted first, so the things that have been waiting longest
    surface at the top. ``status=None`` means "any missing state" (see
    :data:`MISSING_STATUSES`); pass a specific one to narrow it.

    Returns ``(items, total)``.
    """
    wanted = (status,) if status is not None else MISSING_STATUSES
    filters = [Album.status.in_(wanted)]
    if monitored is not None:
        filters.append(Album.monitored.is_(monitored))
    if query:
        pattern = f"%{query.strip()}%"
        filters.append(Album.title.ilike(pattern) | Artist.name.ilike(pattern))

    stmt = select(Album).join(Artist, Artist.id == Album.artist_id).where(*filters)
    count_stmt = (
        select(func.count(Album.id))
        .select_from(Album)
        .join(Artist, Artist.id == Album.artist_id)
        .where(*filters)
    )
    stmt = (
        stmt.order_by(Album.added_at.asc(), Album.title.asc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return [album_to_out(row) for row in rows], total


async def nav_counts(session: AsyncSession) -> dict[str, int]:
    """Badge numbers for the sidebar: followed artists, backlog, queue depth."""
    artists = int((await session.execute(select(func.count(Artist.id)))).scalar_one())
    wanted = int(
        (
            await session.execute(
                select(func.count(Album.id)).where(
                    Album.status.in_(MISSING_STATUSES), Album.monitored.is_(True)
                )
            )
        ).scalar_one()
    )
    queued = int(
        (
            await session.execute(
                select(func.count(QueueItem.id)).where(
                    QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE))
                )
            )
        ).scalar_one()
    )
    # The trash badge is a nudge, not a metric: files sitting there still take
    # up disk. Counting directory entries is cheap and never touches the DB.
    try:
        trash_root = get_settings().trash_dir
        trash = sum(1 for entry in trash_root.iterdir() if entry.is_dir())
    except OSError:
        trash = 0
    return {"artists": artists, "wanted": wanted, "queue": queued, "trash": trash}


async def library_scan_status(session: AsyncSession, settings: Settings) -> dict[str, Any]:
    """Everything the Disk scan page needs before a scan has been triggered.

    Degrades to "never scanned" when the application state is not wired up, so
    the page renders on a half-started process exactly like every other read
    model in this module.
    """
    from app.core.scanner import load_last_scan  # noqa: PLC0415 - keeps app.api import-light

    scanner = get_library_scanner()
    try:
        last = await load_last_scan(session)
    except Exception:  # noqa: BLE001 - a corrupt stored row must not 500 the page
        logger.warning("Could not read the stored library-scan summary", exc_info=True)
        last = None

    return {
        "running": bool(getattr(scanner, "running", False)),
        "available": scanner is not None,
        "library_path": str(settings.library_path),
        "nightly": settings.library_scan_nightly,
        "complete_ratio": settings.library_scan_complete_ratio,
        "last": last,
    }


async def library_import_status(session: AsyncSession) -> dict[str, Any]:
    """Live artist-import progress, falling back to the stored last run.

    Like every accessor in this module it degrades rather than raising: with no
    importer wired up the Disk scan page still renders, just without the import
    controls.
    """
    from app.core.importer import ImportProgress, load_last_import  # noqa: PLC0415

    importer = get_library_importer()
    snapshot: dict[str, Any] | None = None
    if importer is not None and callable(getattr(importer, "snapshot", None)):
        try:
            snapshot = importer.snapshot()
        except Exception:  # noqa: BLE001 - a broken importer must not 500 the page
            logger.warning("Could not read the importer snapshot", exc_info=True)

    if snapshot is not None and (snapshot.get("running") or snapshot.get("started_at")):
        snapshot["available"] = True
        return snapshot

    try:
        stored = await load_last_import(session)
    except Exception:  # noqa: BLE001 - a corrupt stored row reads as "never ran"
        logger.warning("Could not read the stored import summary", exc_info=True)
        stored = None

    result = stored or ImportProgress().as_dict()
    result["available"] = importer is not None
    return result


#: Display order for the queue page: what is happening now, then what is next.
_QUEUE_STATE_ORDER = case(
    (QueueItem.state == QueueState.ACTIVE, 0),
    (QueueItem.state == QueueState.PENDING, 1),
    (QueueItem.state == QueueState.FAILED, 2),
    (QueueItem.state == QueueState.DONE, 3),
    else_=4,
)


async def list_queue_items(
    session: AsyncSession,
    *,
    state: QueueState | None = None,
    limit: int = 100,
    offset: int = 0,
) -> tuple[list[QueueItemOut], int]:
    """Queue entries ordered the way the worker will pick them up."""
    stmt = select(QueueItem)
    count_stmt = select(func.count(QueueItem.id))
    if state is not None:
        stmt = stmt.where(QueueItem.state == state)
        count_stmt = count_stmt.where(QueueItem.state == state)
    stmt = (
        stmt.order_by(_QUEUE_STATE_ORDER, QueueItem.priority.desc(), QueueItem.id.asc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return [queue_item_to_out(row) for row in rows], total


async def list_activity(
    session: AsyncSession,
    *,
    level: ActivityLevel | None = None,
    event: str | None = None,
    artist_id: str | None = None,
    album_id: str | None = None,
    limit: int = 100,
    offset: int = 0,
) -> tuple[list[ActivityOut], int]:
    """Activity feed, newest first."""
    stmt = select(Activity)
    count_stmt = select(func.count(Activity.id))
    if level is not None:
        stmt = stmt.where(Activity.level == level)
        count_stmt = count_stmt.where(Activity.level == level)
    if event:
        stmt = stmt.where(Activity.event == event)
        count_stmt = count_stmt.where(Activity.event == event)
    if artist_id:
        stmt = stmt.where(Activity.artist_id == str(artist_id))
        count_stmt = count_stmt.where(Activity.artist_id == str(artist_id))
    if album_id:
        stmt = stmt.where(Activity.album_id == str(album_id))
        count_stmt = count_stmt.where(Activity.album_id == str(album_id))
    stmt = (
        stmt.order_by(Activity.created_at.desc(), Activity.id.desc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return [activity_to_out(row) for row in rows], total


async def recent_activity(session: AsyncSession, limit: int = 15) -> list[ActivityOut]:
    """The newest *limit* activity entries (dashboard feed)."""
    items, _ = await list_activity(session, limit=limit)
    return items


# ---------------------------------------------------------------------------
# Read models: status
# ---------------------------------------------------------------------------
async def library_stats(session: AsyncSession) -> LibraryStatsOut:
    """Headline library counts for the dashboard."""
    artists_total = int((await session.execute(select(func.count(Artist.id)))).scalar_one())
    artists_monitored = int(
        (
            await session.execute(
                select(func.count(Artist.id)).where(Artist.monitored.is_(True))
            )
        ).scalar_one()
    )
    album_rows = (
        await session.execute(select(Album.status, func.count(Album.id)).group_by(Album.status))
    ).all()
    by_status = {enum_value(status): int(count) for status, count in album_rows}
    tracks_total = int((await session.execute(select(func.count(Track.id)))).scalar_one())
    tracks_done = int(
        (
            await session.execute(
                select(func.count(Track.id)).where(Track.status == TrackStatus.DOWNLOADED)
            )
        ).scalar_one()
    )
    return LibraryStatsOut(
        artists=artists_total,
        monitored_artists=artists_monitored,
        albums=sum(by_status.values()),
        wanted_albums=by_status.get(AlbumStatus.WANTED.value, 0)
        + by_status.get(AlbumStatus.QUEUED.value, 0)
        + by_status.get(AlbumStatus.DOWNLOADING.value, 0),
        downloaded_albums=by_status.get(AlbumStatus.DOWNLOADED.value, 0),
        failed_albums=by_status.get(AlbumStatus.FAILED.value, 0),
        tracks=tracks_total,
        downloaded_tracks=tracks_done,
    )


async def albums_by_status(session: AsyncSession) -> dict[str, int]:
    """Album counts keyed by :class:`AlbumStatus` value, zero-filled."""
    rows = (
        await session.execute(select(Album.status, func.count(Album.id)).group_by(Album.status))
    ).all()
    counts = {status.value: 0 for status in AlbumStatus}
    for status, count in rows:
        counts[enum_value(status)] = int(count)
    return counts


async def _service_payload(
    *sources: Any, label: str = "service"
) -> dict[str, Any]:
    """Call the first available ``(obj, method_names)`` source for a status dict.

    Trailing non-tuple positional arguments are passed to the method. Every
    failure is logged at debug level and skipped — status pages must never 500
    because a background service is unhappy.
    """
    pairs = [item for item in sources if isinstance(item, tuple)]
    args = [item for item in sources if not isinstance(item, tuple)]
    for obj, names in pairs:
        if obj is None:
            continue
        try:
            handled, value = await call_first(obj, names, *args)
        except TypeError:
            try:
                handled, value = await call_first(obj, names)
            except Exception as exc:  # noqa: BLE001
                logger.debug("%s status probe failed: %s", label, exc)
                continue
        except Exception as exc:  # noqa: BLE001
            logger.debug("%s status probe failed: %s", label, exc)
            continue
        if handled and isinstance(value, dict):
            return value
    return {}


def _overlay(target: Any, payload: dict[str, Any], model: type) -> None:
    """Copy the keys of *payload* that are fields of *model* onto *target*."""
    fields = getattr(model, "model_fields", {})
    for key, value in payload.items():
        if key not in fields or value is None:
            continue
        if key.endswith(("_at", "_until")):
            value = _maybe_datetime(value)
            if value is None:
                continue
        try:
            setattr(target, key, value)
        except Exception as exc:  # noqa: BLE001 - a bad field must not 500
            logger.debug("status field %s rejected: %s", key, exc)


async def queue_stats(session: AsyncSession) -> QueueStatsOut:
    """Queue depth per state plus what the worker is doing right now."""
    rows = (
        await session.execute(
            select(QueueItem.state, func.count(QueueItem.id)).group_by(QueueItem.state)
        )
    ).all()
    counts = {state.value: 0 for state in QueueState}
    for state, count in rows:
        counts[enum_value(state)] = int(count)

    stats = QueueStatsOut(
        pending=counts[QueueState.PENDING.value],
        active=counts[QueueState.ACTIVE.value],
        done=counts[QueueState.DONE.value],
        failed=counts[QueueState.FAILED.value],
        cancelled=counts[QueueState.CANCELLED.value],
        total=sum(counts.values()),
    )

    active = (
        await session.execute(
            select(QueueItem).where(QueueItem.state == QueueState.ACTIVE).limit(1)
        )
    ).unique().scalars().first()
    if active is not None:
        stats.current_album_id = active.album_id
        album = getattr(active, "album", None)
        stats.current_album_title = album.display_title if album is not None else None

    # Overlay the live worker's own view (``AppState.queue_status`` /
    # ``QueueWorker.stats``), which knows what it is downloading right now.
    payload = await _service_payload(
        (get_core_state(), ("queue_status",)),
        (get_queue_worker(), ("stats",)),
        session,
        label="queue",
    )
    if payload:
        _overlay(stats, payload, QueueStatsOut)
    elif get_queue_worker() is not None:
        running = getattr(get_queue_worker(), "running", None)
        stats.worker_running = bool(running) if running is not None else True
    return stats


async def indexer_status(session: AsyncSession) -> IndexerStatusOut:
    """Indexer state, merging database facts with the live service's own view."""
    settings = get_settings()
    monitored = int(
        (
            await session.execute(
                select(func.count(Artist.id)).where(Artist.monitored.is_(True))
            )
        ).scalar_one()
    )
    never_checked = int(
        (
            await session.execute(
                select(func.count(Artist.id)).where(
                    Artist.monitored.is_(True), Artist.last_checked_at.is_(None)
                )
            )
        ).scalar_one()
    )
    last_checked = (
        await session.execute(
            select(func.max(Artist.last_checked_at)).where(Artist.monitored.is_(True))
        )
    ).scalar_one()

    status = IndexerStatusOut(
        enabled=settings.indexer_enabled,
        running=False,
        paused=False,
        artist_interval_seconds=settings.indexer_artist_interval,
        full_sweep_hours=settings.indexer_full_sweep_hours,
        last_run_at=as_utc(last_checked),
        monitored_artists=monitored,
        artists_never_checked=never_checked,
    )

    indexer = get_indexer()
    payload = await _service_payload(
        (get_core_state(), ("indexer_status",)),
        (indexer, ("status", "get_status")),
        session,
        label="indexer",
    )
    if not payload and indexer is not None:
        payload = {
            key: getattr(indexer, key)
            for key in (
                "enabled",
                "running",
                "paused",
                "next_run_at",
                "last_run_at",
                "current_artist_id",
                "current_artist_name",
            )
            if hasattr(indexer, key)
        }
    _overlay(status, payload, IndexerStatusOut)

    if status.next_run_at is None:
        scheduler = get_scheduler()
        job = None
        if scheduler is not None:
            for job_id in ("indexer_tick", "indexer", "qobuzarr-indexer"):
                try:
                    job = scheduler.get_job(job_id)
                except Exception as exc:  # noqa: BLE001
                    logger.debug("scheduler.get_job(%s) failed: %s", job_id, exc)
                    job = None
                if job is not None:
                    break
        next_run = getattr(job, "next_run_time", None) if job is not None else None
        if isinstance(next_run, datetime):
            status.next_run_at = as_utc(next_run)

    if status.next_run_at is not None:
        delta = (status.next_run_at - utcnow()).total_seconds()
        status.seconds_until_next_run = max(0.0, delta)
    elif status.enabled and status.last_run_at is not None:
        elapsed = seconds_since(status.last_run_at) or 0.0
        status.seconds_until_next_run = max(
            0.0, settings.indexer_artist_interval - elapsed
        )
    return status


def rate_limit_status() -> RateLimitStatusOut | None:
    """Snapshot the global rate limiter.

    Prefers ``AppState.rate_limit_status()`` (already keyed for
    :class:`RateLimitStatusOut`); falls back to the limiter's own ``stats()``
    dict, and finally to the configured defaults when nothing is running.
    """
    settings = get_settings()
    fallback = RateLimitStatusOut(
        min_request_interval=settings.qobuz_min_request_interval,
        max_requests_per_hour=settings.qobuz_max_requests_per_hour,
        budget_remaining=settings.qobuz_max_requests_per_hour,
    )

    state = get_core_state()
    getter = getattr(state, "rate_limit_status", None) if state is not None else None
    if callable(getter):
        try:
            payload = getter()
        except Exception as exc:  # noqa: BLE001 - dashboards must not 500
            logger.warning("AppState.rate_limit_status() failed: %s", exc)
            payload = None
        if isinstance(payload, dict):
            snapshot = fallback.model_copy()
            _overlay(snapshot, payload, RateLimitStatusOut)
            return snapshot

    limiter = get_rate_limiter()
    if limiter is None:
        return fallback
    try:
        stats = limiter.stats()
    except Exception as exc:  # noqa: BLE001 - dashboards must not 500
        logger.warning("Rate-limiter stats unavailable: %s", exc)
        return RateLimitStatusOut(
            min_request_interval=settings.qobuz_min_request_interval,
            max_requests_per_hour=settings.qobuz_max_requests_per_hour,
            budget_remaining=settings.qobuz_max_requests_per_hour,
        )

    return RateLimitStatusOut(
        min_request_interval=float(
            stats.get("min_interval", settings.qobuz_min_request_interval)
        ),
        max_requests_per_hour=int(
            stats.get("hourly_cap", settings.qobuz_max_requests_per_hour)
        ),
        requests_last_hour=int(stats.get("requests_last_hour", 0)),
        budget_remaining=int(stats.get("budget_remaining", 0)),
        budget_used_percent=float(stats.get("budget_used_percent", 0.0)),
        seconds_until_next_slot=float(stats.get("next_slot_in_seconds", 0.0)),
        last_request_at=_maybe_datetime(stats.get("last_request_at")),
        circuit_open=bool(stats.get("circuit_open", False)),
        circuit_open_until=_maybe_datetime(stats.get("circuit_open_until")),
        recent_429s=int(stats.get("recent_429s", 0)),
    )


#: A made-up hi-res release used only to illustrate the naming template.
_PREVIEW_ARTIST = {"id": "0", "name": "Floating Points"}
_PREVIEW_ALBUM = {
    "id": "preview",
    "title": "Promises",
    "version": None,
    "release_date": date(2021, 3, 26),
    "label": "Luaka Bop",
    "genre": "Jazz",
    "media_count": 1,
    "tracks_count": 9,
    "hires": True,
    "max_bit_depth": 24,
    "max_sampling_rate": 96.0,
    "artist": _PREVIEW_ARTIST,
}
_PREVIEW_TRACKS = (
    {"title": "Movement 1", "track_number": 1, "media_number": 1},
    {"title": "Movement 2", "track_number": 2, "media_number": 1},
)


def naming_preview(settings: Settings | None = None) -> list[str]:
    """Sample library paths produced by the configured naming template.

    Purely illustrative — it touches no database and writes nothing. Returns an
    empty list if the template cannot be rendered, so the settings page still
    loads when someone puts a typo in ``NAMING_TEMPLATE``.
    """
    settings = settings or get_settings()
    try:
        from app.core.naming import render_track_path

        return [
            str(
                render_track_path(
                    track,
                    _PREVIEW_ALBUM,
                    settings,
                    artist=_PREVIEW_ARTIST,
                    bit_depth=24,
                    sampling_rate=96.0,
                    ext="flac",
                )
            )
            for track in _PREVIEW_TRACKS
        ]
    except Exception as exc:  # noqa: BLE001 - a bad template must not 500 the page
        logger.debug("Naming preview failed: %s", exc)
        return []


def build_settings_out(settings: Settings | None = None) -> SettingsOut:
    """Read-only view of the effective configuration for the settings page."""
    settings = settings or get_settings()
    client = get_qobuz_client()
    source = getattr(client, "app_secret_source", None) if client is not None else None
    if not source:
        source = "env" if settings.qobuz_app_secret else "unset"
    return SettingsOut(
        library_path=str(settings.library_path),
        data_path=str(settings.data_path),
        default_format_id=settings.default_format_id,
        default_format_label=settings.format_label(settings.default_format_id),
        naming_template=settings.naming_template,
        default_monitor_mode=settings.default_monitor_mode,
        default_quality_profile=settings.default_quality_profile,
        default_accepted_release_types=settings.accepted_release_type_defaults,
        qobuz_min_request_interval=settings.qobuz_min_request_interval,
        qobuz_max_requests_per_hour=settings.qobuz_max_requests_per_hour,
        indexer_artist_interval=settings.indexer_artist_interval,
        indexer_full_sweep_hours=settings.indexer_full_sweep_hours,
        indexer_enabled=settings.indexer_enabled,
        download_track_delay=settings.download_track_delay,
        download_concurrency=settings.download_concurrency,
        download_max_attempts=settings.download_max_attempts,
        library_scan_nightly=settings.library_scan_nightly,
        library_scan_complete_ratio=settings.library_scan_complete_ratio,
        host=settings.host,
        port=settings.port,
        log_level=enum_value(settings.log_level) or "INFO",
        qobuz_app_id=settings.qobuz_app_id,
        credentials_ok=settings.has_credentials,
        app_secret_source=source,
    )


async def build_status(session: AsyncSession, *, activity_limit: int = 15) -> StatusOut:
    """Assemble the full dashboard payload in one go."""
    settings = get_settings()
    state = get_core_state()
    started_at = get_started_at()
    client = get_qobuz_client()
    credentials_ok = getattr(state, "credentials_ok", None)
    app_secret_ok = getattr(state, "app_secret_ok", None)
    return StatusOut(
        version=__version__,
        started_at=started_at,
        uptime_seconds=seconds_since(started_at) or 0.0,
        library=await library_stats(session),
        queue=await queue_stats(session),
        indexer=await indexer_status(session),
        rate_limit=rate_limit_status(),
        recent_activity=(
            await recent_activity(session, limit=activity_limit)
            if activity_limit
            else []
        ),
        credentials_ok=(
            bool(credentials_ok)
            if credentials_ok is not None
            else settings.has_credentials
        ),
        app_secret_ok=(
            bool(app_secret_ok)
            if app_secret_ok is not None
            else bool(getattr(client, "has_app_secret", False))
            or bool(settings.qobuz_app_secret)
        ),
        library_path=str(settings.library_path),
    )
