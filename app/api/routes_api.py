"""JSON API under ``/api/*`` plus the ``/health`` endpoint.

Every action the web UI can perform is available here as JSON so the whole
application is scriptable::

    curl localhost:8000/api/status
    curl -X POST localhost:8000/api/artists -d '{"artist_id":"12345"}' \\
         -H 'content-type: application/json'

The module also exposes the *service functions* (``follow_artist``,
``queue_album``, ``scan_artist``, ...) that :mod:`app.api.routes_ui` calls, so
the HTML forms and the JSON endpoints share one implementation and can never
drift apart.

Two routers are exported:

``router``
    Everything under the ``/api`` prefix.
``health_router``
    The unprefixed ``/health`` probe.

Both must be mounted by the application factory.
"""

from __future__ import annotations

import asyncio
from collections.abc import Coroutine
from typing import Annotated, Any, Literal

from fastapi import APIRouter, Body, Depends, HTTPException, Query, status as http_status
from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app import __version__
from app.api.deps import (
    album_to_out,
    albums_by_status,
    artist_album_counts,
    artist_to_out,
    build_settings_out,
    build_status,
    call_first,
    get_indexer,
    get_library_importer,
    get_library_scanner,
    get_qobuz_client,
    get_queue_worker,
    get_settings_dep,
    indexer_status,
    library_import_status,
    library_scan_status,
    library_stats,
    list_activity,
    list_albums_for_artist,
    list_artists,
    list_queue_items,
    list_wanted_albums,
    queue_item_to_out,
    queue_stats,
    rate_limit_status,
)
from app.api.params import (
    EmptyAsNone,
    OptActivityLevelQuery,
    OptBoolQuery,
    OptQueueStateQuery,
    OptStrQuery,
)
from app.config import Settings, get_settings
from app.core import librarian
from app.db import get_session, healthcheck, session_scope
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
    join_release_types,
    parse_release_types,
    normalize_release_type,
    utcnow,
)
from app.qobuz.errors import QobuzError
from app.qobuz.mapper import extract_album_artist, map_album, map_artist
from app.schemas import (
    ActivityOut,
    AlbumOut,
    AlbumUpdateIn,
    ArtistCreateIn,
    ArtistOut,
    ArtistBulkUpdateIn,
    ArtistUpdateIn,
    LibraryImportOut,
    LibraryImportStartIn,
    LibraryScanOut,
    LibraryScanStatusOut,
    LibraryTidyOut,
    MessageOut,
    RefilePlanOut,
    TrashEntryOut,
    TrashOut,
    PageOut,
    QueueItemOut,
    SearchAlbumOut,
    SearchArtistOut,
    SearchResultOut,
    SettingsOut,
    StatusOut,
)

__all__ = [
    "router",
    "health_router",
    "follow_artist",
    "apply_artist_update",
    "bulk_update_artists",
    "delete_artist",
    "scan_artist",
    "scan_all",
    "scan_library",
    "delete_album_files",
    "refile_album",
    "refile_library",
    "retag_album",
    "retag_library",
    "trash_contents",
    "restore_trash_entry",
    "empty_trash",
    "start_library_import",
    "cancel_library_import",
    "preview_library_import",
    "queue_album",
    "queue_wanted_for_artist",
    "queue_all_wanted",
    "set_album_monitored",
    "retry_queue_item",
    "cancel_queue_item",
    "run_search",
    "get_artist_or_404",
    "get_album_or_404",
    "get_queue_item_or_404",
]

logger = get_logger(__name__)

router = APIRouter(prefix="/api", tags=["api"])
health_router = APIRouter(tags=["health"])

SessionDep = Annotated[AsyncSession, Depends(get_session)]
SettingsDep = Annotated[Settings, Depends(get_settings_dep)]


# ---------------------------------------------------------------------------
# Local pagination envelopes (reuse the shared PageOut fields)
# ---------------------------------------------------------------------------
class ArtistListOut(PageOut):
    """Paged list of followed artists."""

    items: list[ArtistOut] = []


class AlbumListOut(PageOut):
    """Paged list of albums."""

    items: list[AlbumOut] = []


class QueueListOut(PageOut):
    """Paged list of queue entries."""

    items: list[QueueItemOut] = []


class ActivityListOut(PageOut):
    """Paged list of activity entries."""

    items: list[ActivityOut] = []


class ArtistDetailOut(ArtistOut):
    """One artist plus their known releases."""

    albums: list[AlbumOut] = []


# ---------------------------------------------------------------------------
# Lookup helpers
# ---------------------------------------------------------------------------
async def get_artist_or_404(session: AsyncSession, artist_id: str) -> Artist:
    """Fetch an artist by id or raise ``404``."""
    artist = await session.get(Artist, str(artist_id))
    if artist is None:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND,
            detail=f"Artist {artist_id!r} is not followed.",
        )
    return artist


async def get_album_or_404(session: AsyncSession, album_id: str) -> Album:
    """Fetch an album by its (string!) id or raise ``404``."""
    album = await session.get(Album, str(album_id))
    if album is None:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND,
            detail=f"Album {album_id!r} is not in the library.",
        )
    return album


async def get_queue_item_or_404(session: AsyncSession, item_id: int) -> QueueItem:
    """Fetch a queue entry by id or raise ``404``."""
    item = await session.get(QueueItem, int(item_id))
    if item is None:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND,
            detail=f"Queue item {item_id} does not exist.",
        )
    return item


def require_client() -> Any:
    """Return the shared Qobuz client or raise a friendly ``503``."""
    client = get_qobuz_client()
    if client is None:
        raise HTTPException(
            status_code=http_status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=(
                "The Qobuz client is not available. Check QOBUZ_APP_ID / "
                "QOBUZ_USER_AUTH_TOKEN in .env and restart Qobuzarr."
            ),
        )
    return client


def _log_activity(
    session: AsyncSession,
    *,
    event: str,
    message: str,
    level: ActivityLevel = ActivityLevel.INFO,
    artist_id: str | None = None,
    album_id: str | None = None,
) -> None:
    """Append an activity row (not committed — the caller commits)."""
    session.add(
        Activity(
            level=level,
            event=event,
            message=message,
            artist_id=artist_id,
            album_id=album_id,
        )
    )


#: Strong references to detached background jobs, so they are not garbage
#: collected mid-flight (asyncio only keeps weak references to tasks).
_BACKGROUND_TASKS: set[asyncio.Task[Any]] = set()


def _spawn(coro: Coroutine[Any, Any, Any], *, label: str) -> bool:
    """Run *coro* detached from the request. Returns ``False`` if it could not.

    Indexing one artist means many rate-limited Qobuz calls and can take
    minutes, so "search now" must not block the HTTP response.
    """
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:  # pragma: no cover - no loop outside the server
        coro.close()
        return False

    task = loop.create_task(coro, name=label)
    _BACKGROUND_TASKS.add(task)

    def _done(finished: asyncio.Task[Any]) -> None:
        _BACKGROUND_TASKS.discard(finished)
        if finished.cancelled():
            logger.info("Background job %s cancelled", label)
            return
        error = finished.exception()
        if error is not None:
            logger.error("Background job %s failed: %s", label, error, exc_info=error)

    task.add_done_callback(_done)
    return True


async def _scan_artist_job(artist_id: str, artist_name: str) -> None:
    """Index one artist in the background, recording failures as activity."""
    indexer = get_indexer()
    if indexer is None:
        return
    try:
        async with session_scope() as session:
            await indexer.check_artist(session, artist_id)
    except Exception as exc:  # noqa: BLE001 - surfaced in the activity feed
        logger.exception("Background scan of %s failed", artist_name)
        try:
            async with session_scope() as session:
                _log_activity(
                    session,
                    event="indexer.error",
                    message=f"Scan of {artist_name} failed: {exc}",
                    level=ActivityLevel.ERROR,
                    artist_id=artist_id,
                )
        except Exception:  # noqa: BLE001 - logging must not mask the original
            logger.exception("Could not record the scan failure for %s", artist_name)


def _clean_release_types(values: list[str] | None) -> str | None:
    """Normalise and re-join a list of release types, dropping unknown values.

    Qobuz spellings are accepted (``"epSingle"`` becomes ``"ep"``), an explicit
    ``"other"`` is kept, and anything unrecognised is silently dropped rather
    than being collapsed into ``"other"``.
    """
    if values is None:
        return None
    cleaned: list[str] = []
    for raw in values:
        candidate = str(raw).strip().lower()
        if candidate in RELEASE_TYPES:
            cleaned.append(candidate)
            continue
        mapped = normalize_release_type(candidate)
        if mapped != "other":
            cleaned.append(mapped)
    # Preserve order while removing duplicates.
    return join_release_types(list(dict.fromkeys(cleaned))) or None


# ---------------------------------------------------------------------------
# Service functions — shared by the JSON API and the HTML routes
# ---------------------------------------------------------------------------
async def follow_artist(
    session: AsyncSession, payload: ArtistCreateIn, settings: Settings
) -> tuple[Artist, bool]:
    """Create (or update) a followed artist.

    Looks the artist up on Qobuz to fill in name/image/album count when a client
    is available; falls back to the supplied ``name`` otherwise.

    Prefers :meth:`app.core.indexer.Indexer.add_artist` (which knows how to
    fetch and normalise the artist) and falls back to a database-only insert so
    that following still works with no indexer or no network.

    Returns:
        ``(artist, created)``.

    Raises:
        HTTPException: 400 when the artist cannot be identified at all.
    """
    artist_id = str(payload.artist_id).strip()
    if not artist_id:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail="artist_id is required.",
        )

    created = (await session.get(Artist, artist_id)) is None
    accepted = payload.accepted_release_types or None

    indexer = get_indexer()
    adder = getattr(indexer, "add_artist", None) if indexer is not None else None
    if callable(adder):
        try:
            artist = await adder(
                session,
                artist_id,
                payload.monitored,
                name=payload.name,
                monitor_mode=payload.monitor_mode,
                quality_profile=payload.quality_profile,
                accepted_release_types=accepted,
                # The initial catalogue import is many rate-limited calls; run
                # it in the background so the request returns immediately.
                index_now=False,
            )
            # Indexing is read-only and rate-limited, but it is still many API
            # calls, so it honours `auto_index_on_follow`. Downloading is a
            # separate opt-in governed by `auto_download` in the indexer.
            if payload.search_now or (created and settings.auto_index_on_follow):
                _spawn(
                    _scan_artist_job(artist.id, artist.name),
                    label=f"scan:{artist.id}",
                )
            return artist, created
        except QobuzError as exc:
            logger.warning("Indexer.add_artist(%s) failed: %s", artist_id, exc)
        except Exception as exc:  # noqa: BLE001 - fall back to the DB-only path
            logger.warning("Indexer.add_artist(%s) errored: %s", artist_id, exc)
            await session.rollback()

    details: dict[str, Any] = {}
    client = get_qobuz_client()
    if client is not None:
        try:
            raw = await client.get_artist(artist_id, with_albums=False)
            details = map_artist(raw)
        except QobuzError as exc:
            logger.warning("Qobuz lookup for artist %s failed: %s", artist_id, exc)
        except Exception as exc:  # noqa: BLE001 - never block following on lookup
            logger.warning("Unexpected error looking up artist %s: %s", artist_id, exc)

    name = (payload.name or details.get("name") or "").strip()
    if not name:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=(
                "Could not determine the artist name. Pass `name` explicitly or "
                "make sure the Qobuz client is configured."
            ),
        )

    artist = await session.get(Artist, artist_id)
    created = artist is None
    if artist is None:
        artist = Artist(id=artist_id)
        session.add(artist)

    artist.name = name
    if details.get("image_url"):
        artist.image_url = details["image_url"]
    if details.get("albums_count"):
        artist.albums_count = int(details["albums_count"])
    if details.get("qobuz_slug"):
        artist.qobuz_slug = details["qobuz_slug"]

    artist.monitored = payload.monitored
    if created:
        artist.monitor_mode = payload.monitor_mode or MonitorMode(
            settings.default_monitor_mode
        )
        artist.quality_profile = payload.quality_profile or settings.default_quality_profile
        artist.accepted_release_types = (
            _clean_release_types(payload.accepted_release_types)
            or join_release_types(settings.accepted_release_type_defaults)
        )
    else:
        if payload.monitor_mode is not None:
            artist.monitor_mode = payload.monitor_mode
        if payload.quality_profile:
            artist.quality_profile = payload.quality_profile
        cleaned = _clean_release_types(payload.accepted_release_types)
        if cleaned:
            artist.accepted_release_types = cleaned

    _log_activity(
        session,
        event="artist.follow" if created else "artist.update",
        message=f"{'Now following' if created else 'Updated'} {name}",
        artist_id=artist_id,
    )
    await session.commit()
    await session.refresh(artist)

    if payload.search_now:
        await scan_artist(session, artist)
    return artist, created


async def apply_artist_update(
    session: AsyncSession, artist: Artist, payload: ArtistUpdateIn
) -> Artist:
    """Apply a partial monitoring-settings update to *artist* and commit."""
    if payload.monitored is not None:
        artist.monitored = payload.monitored
    if payload.monitor_mode is not None:
        artist.monitor_mode = payload.monitor_mode
    if payload.quality_profile:
        artist.quality_profile = payload.quality_profile
    if payload.accepted_release_types is not None:
        artist.accepted_release_types = _clean_release_types(
            payload.accepted_release_types
        ) or ""
    _log_activity(
        session,
        event="artist.update",
        message=f"Updated settings for {artist.name}",
        artist_id=artist.id,
    )
    await session.commit()
    await session.refresh(artist)
    return artist


async def bulk_update_artists(
    session: AsyncSession, payload: ArtistBulkUpdateIn
) -> tuple[str, dict[str, Any]]:
    """Apply one monitoring change to every artist in ``payload.artist_ids``.

    Only the fields that are not ``None`` are touched, so a bulk mode change
    leaves quality profiles and release types exactly as they were.

    Emptying an artist's accepted release types is allowed but is almost never
    what someone means — an artist that accepts nothing will never mark anything
    wanted again. Those are counted and named in the returned message rather
    than being silently applied.

    Returns:
        ``(message, detail)`` where *detail* carries the counts for the JSON API.
    """
    ids = list(dict.fromkeys(str(a).strip() for a in payload.artist_ids if str(a).strip()))
    if not ids:
        return "Nothing selected — tick some artists first.", {
            "selected": 0,
            "updated": 0,
            "unchanged": 0,
            "missing": 0,
        }

    rows = list(
        (await session.execute(select(Artist).where(Artist.id.in_(ids))))
        .unique()
        .scalars()
        .all()
    )
    missing = len(ids) - len(rows)

    requested: list[str] | None = None
    if payload.release_types is not None:
        requested = parse_release_types(_clean_release_types(payload.release_types) or "")

    updated = 0
    emptied: list[str] = []
    for artist in rows:
        before = (
            artist.monitored,
            artist.monitor_mode,
            artist.accepted_release_types,
        )
        if payload.monitored is not None:
            artist.monitored = payload.monitored
        if payload.monitor_mode is not None:
            artist.monitor_mode = payload.monitor_mode
        if requested is not None:
            current = artist.accepted_release_types_list
            if payload.release_types_action == "add":
                merged = current + [rt for rt in requested if rt not in current]
            elif payload.release_types_action == "remove":
                merged = [rt for rt in current if rt not in requested]
            else:
                merged = requested
            artist.set_accepted_release_types(merged)
            if not merged:
                emptied.append(artist.name)
        after = (
            artist.monitored,
            artist.monitor_mode,
            artist.accepted_release_types,
        )
        if before != after:
            updated += 1

    detail: dict[str, Any] = {
        "selected": len(ids),
        "updated": updated,
        "unchanged": len(rows) - updated,
        "missing": missing,
        "emptied": len(emptied),
    }

    if not updated:
        await session.rollback()
        return "Nothing to change — those artists already have those settings.", detail

    parts = [f"Updated {updated} artist(s)."]
    if missing:
        parts.append(f"{missing} no longer exist(s).")
    if emptied:
        shown = ", ".join(sorted(emptied)[:3])
        more = f" and {len(emptied) - 3} more" if len(emptied) > 3 else ""
        parts.append(
            f"Heads up: {shown}{more} now accept no release types at all, "
            "so nothing new will be marked wanted for them."
        )
    message = " ".join(parts)

    # One summary row: a bulk edit of 200 artists must not write 200 log lines.
    _log_activity(
        session,
        event="artist.bulk",
        message=message,
        level=ActivityLevel.WARNING if emptied else ActivityLevel.INFO,
    )
    await session.commit()
    return message, detail


async def delete_artist(session: AsyncSession, artist: Artist) -> str:
    """Unfollow *artist*, cascading to albums, tracks and queue items."""
    name = artist.name
    artist_id = artist.id
    await session.delete(artist)
    # The activity row must not reference the artist we just removed.
    _log_activity(
        session,
        event="artist.unfollow",
        message=f"Unfollowed {name} ({artist_id})",
    )
    await session.commit()
    return f"Unfollowed {name}."


async def scan_artist(session: AsyncSession, artist: Artist) -> str:
    """Ask the indexer to check *artist* now.

    The scan runs as a detached background job because indexing one artist is
    many rate-limited API calls. When no indexer is available, ``last_checked_at``
    is cleared instead so the least-recently-checked selection picks this artist
    on the next tick.
    """
    indexer = get_indexer()
    can_scan = callable(getattr(indexer, "check_artist", None))

    if can_scan:
        message = (
            f"Scanning {artist.name} now — releases appear here as they are found."
        )
    else:
        artist.last_checked_at = None
        message = (
            f"{artist.name} moved to the front of the indexer queue "
            "(the indexer runs on its own slow schedule)."
        )

    _log_activity(session, event="artist.scan", message=message, artist_id=artist.id)
    await session.commit()

    if can_scan and not _spawn(
        _scan_artist_job(artist.id, artist.name), label=f"scan:{artist.id}"
    ):
        artist.last_checked_at = None
        await session.commit()
        return f"{artist.name} marked due for the next indexer tick."
    return message


async def queue_wanted_for_artist(session: AsyncSession, artist: Artist) -> str:
    """Queue every ``wanted`` release of *artist*, whatever ``AUTO_DOWNLOAD`` says.

    Falls back to queueing directly from the database when no indexer is wired
    up, so the button still works in a degraded app.
    """
    indexer = get_indexer()
    queue_wanted = getattr(indexer, "queue_wanted", None) if indexer else None

    if callable(queue_wanted):
        queued = await queue_wanted(session, artist)
    else:
        rows = await session.execute(
            select(Album).where(
                Album.artist_id == str(artist.id),
                Album.status == AlbumStatus.WANTED,
                Album.monitored.is_(True),
            )
        )
        queued = 0
        for album in rows.scalars().all():
            _, created = await queue_album(session, album)
            queued += int(created)
        await session.commit()

    if not queued:
        return f"Nothing wanted to download for {artist.name}."

    message = f"Queued {queued} release(s) for {artist.name}."
    _log_activity(
        session, event="queue.bulk", message=message, artist_id=str(artist.id)
    )
    await session.commit()
    return message


async def queue_all_wanted(session: AsyncSession, *, limit: int = 500) -> str:
    """Queue the whole cross-artist backlog — an explicit user action only.

    This is the "Download all" button on the Wanted page. Like
    :func:`queue_wanted_for_artist` it bypasses ``AUTO_DOWNLOAD`` because the
    user asked for it by name; nothing calls this on a timer, and nothing
    should. ``limit`` caps one press so a 5,000-album library cannot be
    committed to disk by a single mis-click.
    """
    rows = await session.execute(
        select(Album)
        .where(Album.status == AlbumStatus.WANTED, Album.monitored.is_(True))
        .order_by(Album.added_at.asc())
        .limit(max(1, limit))
    )
    albums = list(rows.unique().scalars().all())
    queued = 0
    for album in albums:
        _, created = await queue_album(session, album)
        queued += int(created)

    if not queued:
        await session.commit()
        return "Nothing wanted to download."

    message = f"Queued {queued} release(s) for download."
    if len(albums) == limit:
        message += f" Capped at {limit} — press again for the rest."
    _log_activity(session, event="queue.bulk", message=message)
    await session.commit()
    return message


async def scan_all(session: AsyncSession) -> str:
    """Mark every monitored artist due so the indexer sweeps them.

    This deliberately does **not** call ``Indexer.refresh_all_now`` inline: with
    the configured rate limits a full sweep takes hours, which is exactly why
    the scheduler exists. Clearing ``last_checked_at`` makes every artist due
    and lets the normal one-artist-per-tick loop work through them.
    """
    await session.execute(
        update(Artist).where(Artist.monitored.is_(True)).values(last_checked_at=None)
    )
    message = (
        "Full sweep requested; the indexer will work through every monitored "
        "artist at its configured pace."
    )
    _log_activity(session, event="indexer.sweep", message=message)
    await session.commit()
    return message


# ---------------------------------------------------------------------------
# Library management: the services that change files on disk
# ---------------------------------------------------------------------------
def _library_http_error(exc: librarian.LibraryError) -> HTTPException:
    """Map a refusal from :mod:`app.core.librarian` onto a status code.

    A busy album is a 409 (try again once the download finishes); anything else
    the librarian refused is a 400 with its own message, which is written to be
    read by a person.
    """
    if isinstance(exc, librarian.LibraryBusyError):
        code = http_status.HTTP_409_CONFLICT
    else:
        code = http_status.HTTP_400_BAD_REQUEST
    return HTTPException(status_code=code, detail=str(exc))


async def delete_album_files(
    session: AsyncSession, album: Album, *, reason: str = "deleted"
) -> librarian.DeleteResult:
    """Move one release's files to the trash and mark it missing again.

    Nothing is unlinked: the folder goes to ``Settings.trash_dir`` and stays
    there until the trash is emptied. The album returns to ``WANTED`` when it is
    still monitored and ``SKIPPED`` when it is not, and — like every other path
    in Qobuzarr — **nothing is queued**. Deleting is not a request to fetch it
    again.
    """
    try:
        return await librarian.delete_album_files(session, album, reason=reason)
    except librarian.LibraryError as exc:
        await session.rollback()
        raise _library_http_error(exc) from exc


async def refile_album(
    session: AsyncSession, album: Album, *, apply: bool = True
) -> librarian.RefilePlan:
    """Move one release to where ``NAMING_TEMPLATE`` says it belongs.

    Returns the plan either way, so a dry run and a real run report the same
    shape. With ``apply=False`` nothing touches the disk.
    """
    plan = await librarian.plan_refile(session, album)
    if not apply or plan.blocked or not plan.needed:
        return plan
    try:
        await librarian.refile_album(session, album, plan=plan)
    except librarian.LibraryError as exc:
        await session.rollback()
        raise _library_http_error(exc) from exc
    return plan


async def retag_album(session: AsyncSession, album: Album) -> librarian.RetagResult:
    """Rewrite tags and cover art in place from the catalogue metadata we hold.

    Local only — no Qobuz call — and the only library operation that edits a
    file's contents rather than moving it. There is no trash copy of the old
    tags; the audio itself is untouched.
    """
    try:
        return await librarian.retag_album(session, album)
    except librarian.LibraryError as exc:
        await session.rollback()
        raise _library_http_error(exc) from exc


def _plan_out(plan: librarian.RefilePlan) -> RefilePlanOut:
    """Read model for one re-file plan."""
    return RefilePlanOut(
        album_id=plan.album_id,
        album_title=plan.album_title,
        artist_name=plan.artist_name,
        current_dir=plan.current_dir,
        target_dir=plan.target_dir,
        renames=list(plan.renames),
        moves_directory=plan.moves_directory,
        blocked=plan.blocked,
    )


async def refile_library(
    session: AsyncSession,
    *,
    artist_id: str | None = None,
    apply: bool = False,
    limit: int = 2000,
) -> LibraryTidyOut:
    """Re-file every release whose files are not where the template says.

    ``apply`` defaults to **False**: this walks the whole library and moves
    folders, so the preview is the default and committing to it is the explicit
    act. Blocked albums are reported, never skipped silently.
    """
    plans = await librarian.plan_library_refile(
        session, artist_id=artist_id, limit=limit
    )
    result = LibraryTidyOut(dry_run=not apply, considered=len(plans))
    result.plans = [_plan_out(plan) for plan in plans]
    result.blocked = sum(1 for plan in plans if plan.blocked)

    if apply:
        for plan in plans:
            if plan.blocked or not plan.needed:
                continue
            album = await session.get(Album, plan.album_id)
            if album is None:  # pragma: no cover - deleted mid-pass
                continue
            try:
                await librarian.refile_album(session, album, plan=plan)
            except librarian.LibraryError as exc:
                result.failed += 1
                result.errors.append(f"{plan.album_title}: {exc}")
                continue
            result.changed += 1
    else:
        result.changed = sum(1 for plan in plans if plan.needed and not plan.blocked)

    verb = "would be re-filed" if not apply else "re-filed"
    result.summary = (
        f"{result.changed} release(s) {verb}"
        + (f", {result.blocked} blocked" if result.blocked else "")
        + (f", {result.failed} failed" if result.failed else "")
    )
    return result


async def retag_library(
    session: AsyncSession, *, artist_id: str | None = None, limit: int = 2000
) -> LibraryTidyOut:
    """Re-tag every downloaded release, or one artist's.

    No dry run: re-tagging is idempotent and writes the metadata already in the
    database, so previewing it would only ever list every album.
    """
    albums = await librarian.albums_on_disk(session, artist_id=artist_id, limit=limit)
    result = LibraryTidyOut(considered=len(albums))
    for album in albums:
        try:
            outcome = await librarian.retag_album(session, album)
        except librarian.LibraryError as exc:
            result.blocked += 1
            result.errors.append(f"{album.display_title}: {exc}")
            continue
        if outcome.tagged:
            result.changed += 1
        result.failed += outcome.failed
    result.summary = (
        f"{result.changed} of {result.considered} release(s) re-tagged"
        + (f", {result.failed} file(s) failed" if result.failed else "")
        + (f", {result.blocked} blocked" if result.blocked else "")
    )
    return result


async def trash_contents(settings: Settings | None = None) -> TrashOut:
    """Everything currently recoverable, newest first."""
    conf = settings or get_settings()
    entries = await asyncio.to_thread(librarian.list_trash, conf)
    return TrashOut(
        entries=[TrashEntryOut.model_validate(entry.as_dict()) for entry in entries],
        total=len(entries),
        size_bytes=sum(entry.size_bytes for entry in entries),
        path=str(conf.trash_dir),
    )


async def restore_trash_entry(entry_id: str, settings: Settings | None = None) -> str:
    """Put one trashed batch back where it came from.

    The database is **not** touched: a restored album is a folder that appeared
    on disk, which is exactly what the disk scan exists to reconcile. Run a scan
    afterwards to have it adopted again.
    """
    conf = settings or get_settings()
    try:
        restored = await asyncio.to_thread(
            librarian.restore_from_trash, entry_id, conf
        )
    except librarian.LibraryError as exc:
        raise _library_http_error(exc) from exc
    return str(restored)


async def empty_trash(
    entry_id: str | None = None, settings: Settings | None = None
) -> tuple[int, int]:
    """Delete trashed batches for real. The one irreversible action in Qobuzarr."""
    conf = settings or get_settings()
    try:
        return await asyncio.to_thread(librarian.empty_trash, conf, entry_id)
    except librarian.LibraryError as exc:
        raise _library_http_error(exc) from exc


async def scan_library(
    session: AsyncSession,
    *,
    artist_id: str | None = None,
    apply: bool = True,
) -> dict[str, Any]:
    """Walk the library folder and adopt whatever is already on disk.

    Purely local: it reads files and the database and makes **no Qobuz API
    call**, so unlike :func:`scan_artist` it costs nothing against the rate
    limit and can run inline in the request. It also never queues or downloads
    anything — see :mod:`app.core.scanner` for the rules it holds itself to.

    Args:
        session: Request session; the scanner commits it once at the end.
        artist_id: Restrict changes to one artist. Folders belonging to anyone
            else are ignored rather than reported.
        apply: When false the report is built but nothing is written.

    Returns:
        :meth:`app.core.scanner.ScanResult.as_dict`.

    Raises:
        HTTPException: 503 when no scanner is wired up (the application was
            started without its state), 404 when *artist_id* is unknown.
    """
    scanner = get_library_scanner()
    if scanner is None or not callable(getattr(scanner, "scan", None)):
        raise HTTPException(
            status_code=http_status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="The library scanner is not available on this instance.",
        )
    if getattr(scanner, "running", False):
        raise HTTPException(
            status_code=http_status.HTTP_409_CONFLICT,
            detail="A disk scan is already running.",
        )

    try:
        result = await scanner.scan(session, artist_id=artist_id, apply=apply)
    except LookupError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND, detail=str(exc)
        ) from exc
    return result.as_dict()


def _require_importer() -> Any:
    """The shared importer, or a 503 when the application has no state."""
    importer = get_library_importer()
    if importer is None or not callable(getattr(importer, "start", None)):
        raise HTTPException(
            status_code=http_status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="The artist importer is not available on this instance.",
        )
    return importer


async def start_library_import(payload: LibraryImportStartIn) -> dict[str, Any]:
    """Begin importing the artists found in the library folder.

    One Qobuz search per artist, through the shared rate limiter, in a
    background task — a 500-artist library takes minutes, not milliseconds, so
    the request returns as soon as the run is under way and the caller polls
    :func:`api_library_import_status`.

    Only unambiguous name matches are followed; everything else lands in the
    review list. Nothing is indexed unless ``index_now`` is set, and nothing is
    ever downloaded.

    Raises:
        HTTPException: 503 with no importer, 409 when a run is already going.
    """
    importer = _require_importer()
    try:
        return await importer.start(
            names=payload.names,
            monitored=payload.monitored,
            monitor_mode=payload.monitor_mode,
            quality_profile=payload.quality_profile,
            release_types=payload.release_types,
            index_now=payload.index_now,
            limit=payload.limit,
        )
    except RuntimeError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_409_CONFLICT, detail=str(exc)
        ) from exc


async def cancel_library_import() -> bool:
    """Ask a running import to stop after the artist it is on. ``False`` if idle."""
    return bool(await _require_importer().cancel())


async def preview_library_import(session: AsyncSession) -> dict[str, Any]:
    """Who an import would look up, and roughly how long it would take.

    A dry disk scan — local only, no Qobuz call — so it is safe to render
    before asking the user to commit to a long run.
    """
    importer = _require_importer()
    candidates = await importer.preview(session)
    return {
        "count": len(candidates),
        "names": [str(row.get("name") or "") for row in candidates],
        "eta_seconds": importer.estimate_seconds(len(candidates)),
    }


async def _reload_queue_items(session: AsyncSession, album: Album) -> None:
    """Re-read *album*'s queue entries after one was inserted by ``album_id``.

    Sessions are built with ``expire_on_commit=False``, and a ``QueueItem``
    created from an id never touches the album's loaded ``queue_items``
    collection — so without this the caller re-renders the album from a
    collection that predates the row it just added. The artist table decides
    between "In queue" and the Download/Upgrade button on exactly that
    collection, so it would offer the button a second time.

    Refresh rather than expire: an expired ``selectin`` collection reloads
    lazily on attribute access, which raises ``MissingGreenlet`` under asyncio.
    """
    try:
        await session.refresh(album, ["queue_items"])
    except Exception as exc:  # noqa: BLE001 - display detail, never fatal
        logger.debug("Could not reload queue items for %s: %s", album.id, exc)


async def queue_album(session: AsyncSession, album: Album) -> tuple[QueueItem, bool]:
    """Mark *album* wanted and put it on the download queue.

    Existing non-terminal entries are reused rather than duplicated.

    Prefers :meth:`app.core.queue.QueueWorker.enqueue_album` so the worker's own
    bookkeeping (progress totals, activity events, wake-up) happens; falls back
    to inserting the row directly when no worker is running.

    Returns:
        ``(queue_item, created)``.
    """
    worker = get_queue_worker()
    if callable(getattr(worker, "enqueue_album", None)):
        try:
            item = await worker.enqueue_album(session, album.id)
            await session.commit()
        except LookupError as exc:  # album vanished between lookups
            await session.rollback()
            raise HTTPException(
                status_code=http_status.HTTP_404_NOT_FOUND, detail=str(exc)
            ) from exc
        except Exception as exc:  # noqa: BLE001 - fall back to the DB-only path
            await session.rollback()
            logger.warning("QueueWorker.enqueue_album(%s) failed: %s", album.id, exc)
        else:
            if item is not None:
                await session.refresh(item)
                await _reload_queue_items(session, album)
                return item, True
            current = (
                await session.execute(
                    select(QueueItem)
                    .where(
                        QueueItem.album_id == album.id,
                        QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE)),
                    )
                    .order_by(QueueItem.id.desc())
                    .limit(1)
                )
            ).unique().scalars().first()
            if current is not None:
                return current, False

    existing = (
        await session.execute(
            select(QueueItem)
            .where(
                QueueItem.album_id == album.id,
                QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE)),
            )
            .limit(1)
        )
    ).unique().scalars().first()

    created = existing is None
    if existing is None:
        existing = QueueItem(album_id=album.id, state=QueueState.PENDING)
        session.add(existing)

    album.monitored = True
    if album.status in (AlbumStatus.SKIPPED, AlbumStatus.FAILED, AlbumStatus.WANTED):
        album.status = AlbumStatus.QUEUED

    _log_activity(
        session,
        event="queue.add",
        message=f"Queued {album.display_title}",
        artist_id=album.artist_id,
        album_id=album.id,
    )
    await session.commit()
    await session.refresh(existing)
    await _reload_queue_items(session, album)

    worker = get_queue_worker()
    try:
        await call_first(worker, ("wake", "notify", "kick", "poke", "signal"))
    except Exception as exc:  # noqa: BLE001 - the row is already persisted
        logger.debug("Could not wake the download worker: %s", exc)
    return existing, created


async def set_album_monitored(
    session: AsyncSession, album: Album, payload: AlbumUpdateIn
) -> Album:
    """Toggle or set an album's monitored flag and optional status."""
    album.monitored = (
        (not album.monitored) if payload.monitored is None else payload.monitored
    )
    if payload.status is not None:
        album.status = payload.status
    elif album.monitored and album.status is AlbumStatus.SKIPPED:
        album.status = AlbumStatus.WANTED
    elif not album.monitored and album.status is AlbumStatus.WANTED:
        album.status = AlbumStatus.SKIPPED

    _log_activity(
        session,
        event="album.monitor",
        message=(
            f"{'Monitoring' if album.monitored else 'Ignoring'} "
            f"{album.display_title}"
        ),
        artist_id=album.artist_id,
        album_id=album.id,
    )
    await session.commit()
    await session.refresh(album)
    return album


async def retry_queue_item(session: AsyncSession, item: QueueItem) -> str:
    """Return a failed/cancelled queue entry to the pending state."""
    if item.state is QueueState.ACTIVE:
        raise HTTPException(
            status_code=http_status.HTTP_409_CONFLICT,
            detail="That item is downloading right now.",
        )

    worker = get_queue_worker()
    if callable(getattr(worker, "retry_item", None)):
        try:
            if await worker.retry_item(session, item.id):
                _log_activity(
                    session,
                    event="queue.retry",
                    message=f"Retrying queue item {item.id}",
                    album_id=item.album_id,
                )
                await session.commit()
                return f"Queue item {item.id} will be retried."
        except Exception as exc:  # noqa: BLE001 - fall back to the DB-only path
            await session.rollback()
            logger.warning("QueueWorker.retry_item(%s) failed: %s", item.id, exc)
            item = await get_queue_item_or_404(session, item.id)

    item.state = QueueState.PENDING
    item.attempts = 0
    item.last_error = None
    item.started_at = None
    item.finished_at = None
    album = getattr(item, "album", None)
    if album is not None and album.status is AlbumStatus.FAILED:
        album.status = AlbumStatus.QUEUED
    _log_activity(
        session,
        event="queue.retry",
        message=f"Retrying queue item {item.id}",
        album_id=item.album_id,
    )
    await session.commit()

    worker = get_queue_worker()
    try:
        await call_first(worker, ("wake", "notify", "kick", "poke", "signal"))
    except Exception as exc:  # noqa: BLE001
        logger.debug("Could not wake the download worker: %s", exc)
    return f"Queue item {item.id} will be retried."


async def cancel_queue_item(session: AsyncSession, item: QueueItem) -> str:
    """Cancel a queue entry.

    An item the worker is actively downloading cannot be interrupted mid-track,
    but :meth:`app.core.queue.QueueWorker.cancel_item` raises a per-item stop
    flag so the album really does stop after the file currently in flight — it
    is not left to run to completion.
    """
    worker = get_queue_worker()
    if callable(getattr(worker, "cancel_item", None)):
        try:
            if await worker.cancel_item(session, item.id):
                _log_activity(
                    session,
                    event="queue.cancel",
                    message=f"Cancelled queue item {item.id}",
                    level=ActivityLevel.WARNING,
                    album_id=item.album_id,
                )
                await session.commit()
                return f"Queue item {item.id} cancelled."
        except Exception as exc:  # noqa: BLE001 - fall back to the DB-only path
            await session.rollback()
            logger.warning("QueueWorker.cancel_item(%s) failed: %s", item.id, exc)
            item = await get_queue_item_or_404(session, item.id)

    item.state = QueueState.CANCELLED
    item.finished_at = utcnow()
    album = getattr(item, "album", None)
    if album is not None and album.status in (
        AlbumStatus.QUEUED,
        AlbumStatus.DOWNLOADING,
    ):
        album.status = AlbumStatus.WANTED
    _log_activity(
        session,
        event="queue.cancel",
        message=f"Cancelled queue item {item.id}",
        level=ActivityLevel.WARNING,
        album_id=item.album_id,
    )
    await session.commit()
    return f"Queue item {item.id} cancelled."


async def run_search(
    session: AsyncSession,
    query: str,
    *,
    search_type: str = "all",
    limit: int = 25,
    offset: int = 0,
) -> SearchResultOut:
    """Search the Qobuz catalogue and annotate hits with local library state.

    Raises:
        HTTPException: 503 when no client is configured, 502 when Qobuz errors.
    """
    query = (query or "").strip()
    result = SearchResultOut(query=query, limit=limit, offset=offset)
    if not query:
        return result

    client = require_client()
    wanted = str(search_type or "all").lower()
    try:
        payload = await client.search(query, type="all", limit=limit, offset=offset)
    except QobuzError as exc:
        logger.warning("Qobuz search for %r failed: %s", query, exc)
        raise HTTPException(
            status_code=http_status.HTTP_502_BAD_GATEWAY,
            detail=f"Qobuz search failed: {exc}",
        ) from exc

    def _bucket(key: str) -> dict[str, Any]:
        value = payload.get(key) if isinstance(payload, dict) else None
        return value if isinstance(value, dict) else {}

    if wanted in ("all", "artists", "artist"):
        bucket = _bucket("artists")
        items = [item for item in bucket.get("items", []) if isinstance(item, dict)]
        mapped = [map_artist(item) for item in items]
        ids = [m["id"] for m in mapped if m.get("id")]
        followed: set[str] = set()
        if ids:
            followed = {
                row
                for row in (
                    await session.execute(select(Artist.id).where(Artist.id.in_(ids)))
                )
                .scalars()
                .all()
            }
        result.total_artists = int(bucket.get("total", len(mapped)) or 0)
        result.artists = [
            SearchArtistOut(
                id=str(m["id"]),
                name=m.get("name") or "Unknown artist",
                image_url=m.get("image_url"),
                albums_count=int(m.get("albums_count") or 0),
                slug=m.get("qobuz_slug"),
                followed=str(m["id"]) in followed,
            )
            for m in mapped
            if m.get("id")
        ]

    if wanted in ("all", "albums", "album"):
        bucket = _bucket("albums")
        items = [item for item in bucket.get("items", []) if isinstance(item, dict)]
        albums: list[SearchAlbumOut] = []
        for raw in items:
            mapped = map_album(raw)
            album_id = mapped.get("id")
            if not album_id:
                continue
            artist_info = extract_album_artist(raw) or {}
            release_date = mapped.get("release_date")
            albums.append(
                SearchAlbumOut(
                    id=str(album_id),
                    title=mapped.get("title") or "Unknown album",
                    version=mapped.get("version"),
                    artist_id=(str(artist_info["id"]) if artist_info.get("id") else None),
                    artist_name=artist_info.get("name"),
                    release_date=release_date,
                    year=release_date.year if release_date else None,
                    tracks_count=int(mapped.get("tracks_count") or 0),
                    hires=bool(mapped.get("hires")),
                    image_url=mapped.get("image_url"),
                )
            )
        if albums:
            known = {
                row
                for row in (
                    await session.execute(
                        select(Album.id).where(Album.id.in_([a.id for a in albums]))
                    )
                )
                .scalars()
                .all()
            }
            for album in albums:
                album.in_library = album.id in known
        result.total_albums = int(bucket.get("total", len(albums)) or 0)
        result.albums = albums

    return result


# ---------------------------------------------------------------------------
# Health
# ---------------------------------------------------------------------------
@health_router.get("/health", summary="Liveness and database probe")
async def health(settings: SettingsDep) -> dict[str, Any]:
    """Report process, database and credential health."""
    db = await healthcheck()
    client = get_qobuz_client()
    return {
        "status": "ok" if db.get("ok") else "degraded",
        "version": __version__,
        "database": db,
        "credentials_ok": settings.has_credentials,
        "app_secret_ok": bool(getattr(client, "has_app_secret", False))
        or bool(settings.qobuz_app_secret),
        "qobuz_client": client is not None,
        "indexer_enabled": settings.indexer_enabled,
    }


# ---------------------------------------------------------------------------
# Status / settings
# ---------------------------------------------------------------------------
@router.get("/status", response_model=StatusOut, summary="Dashboard payload")
async def api_status(
    session: SessionDep,
    activity_limit: int = Query(15, ge=0, le=100),
) -> StatusOut:
    """Everything the dashboard shows, in one request."""
    return await build_status(session, activity_limit=activity_limit)


@router.get("/settings", response_model=SettingsOut, summary="Effective configuration")
async def api_settings(settings: SettingsDep) -> SettingsOut:
    """Read-only view of the effective configuration."""
    return build_settings_out(settings)


@router.get("/stats", summary="Compact counters")
async def api_stats(session: SessionDep) -> dict[str, Any]:
    """Compact counters, handy for external dashboards."""
    return {
        "library": (await library_stats(session)).model_dump(),
        "albums_by_status": await albums_by_status(session),
        "queue": (await queue_stats(session)).model_dump(),
        "indexer": (await indexer_status(session)).model_dump(),
        "rate_limit": (rl.model_dump() if (rl := rate_limit_status()) else None),
    }


# ---------------------------------------------------------------------------
# Artists
# ---------------------------------------------------------------------------
@router.get("/artists", response_model=ArtistListOut, summary="List followed artists")
async def api_list_artists(
    session: SessionDep,
    q: Annotated[
        str | None, EmptyAsNone, Query(description="Filter by name (substring).")
    ] = None,
    monitored: OptBoolQuery = None,
    sort: str = Query("name"),
    order: Literal["asc", "desc"] = Query("asc"),
    limit: int = Query(200, ge=1, le=1000),
    offset: int = Query(0, ge=0),
) -> ArtistListOut:
    """Page through the followed artists."""
    items, total = await list_artists(
        session,
        query=q,
        monitored=monitored,
        sort=sort,
        order=order,
        limit=limit,
        offset=offset,
    )
    return ArtistListOut(
        items=items, total=total, limit=limit, offset=offset, sort=sort, order=order
    )


@router.post(
    "/artists",
    response_model=ArtistOut,
    status_code=http_status.HTTP_201_CREATED,
    summary="Follow an artist",
)
async def api_create_artist(
    session: SessionDep,
    settings: SettingsDep,
    payload: ArtistCreateIn,
) -> ArtistOut:
    """Follow a Qobuz artist (idempotent — following twice just updates)."""
    artist, _created = await follow_artist(session, payload, settings)
    counts = await artist_album_counts(session, [artist.id])
    bucket = counts.get(artist.id, {})
    return artist_to_out(
        artist,
        album_count=bucket.get("albums"),
        wanted_count=bucket.get("wanted"),
        downloaded_count=bucket.get("downloaded"),
    )


@router.get(
    "/artists/{artist_id}",
    response_model=ArtistDetailOut,
    summary="One artist with their releases",
)
async def api_get_artist(
    session: SessionDep,
    artist_id: str,
    album_limit: int = Query(500, ge=1, le=2000),
) -> ArtistDetailOut:
    """Return one followed artist plus every release Qobuzarr knows about."""
    artist = await get_artist_or_404(session, artist_id)
    albums, total = await list_albums_for_artist(session, artist.id, limit=album_limit)
    counts = await artist_album_counts(session, [artist.id])
    bucket = counts.get(artist.id, {})
    base = artist_to_out(
        artist,
        album_count=bucket.get("albums", total),
        wanted_count=bucket.get("wanted"),
        downloaded_count=bucket.get("downloaded"),
    )
    return ArtistDetailOut(**base.model_dump(), albums=albums)


@router.patch(
    "/artists/{artist_id}", response_model=ArtistOut, summary="Update artist settings"
)
async def api_update_artist(
    session: SessionDep, artist_id: str, payload: ArtistUpdateIn
) -> ArtistOut:
    """Change monitoring mode, quality profile or accepted release types."""
    artist = await get_artist_or_404(session, artist_id)
    artist = await apply_artist_update(session, artist, payload)
    return artist_to_out(artist)


@router.delete(
    "/artists/{artist_id}", response_model=MessageOut, summary="Unfollow an artist"
)
async def api_delete_artist(session: SessionDep, artist_id: str) -> MessageOut:
    """Unfollow an artist and delete their albums, tracks and queue entries."""
    artist = await get_artist_or_404(session, artist_id)
    message = await delete_artist(session, artist)
    return MessageOut(ok=True, message=message)


@router.post(
    "/artists/{artist_id}/scan", response_model=MessageOut, summary="Scan one artist"
)
async def api_scan_artist(session: SessionDep, artist_id: str) -> MessageOut:
    """Ask the indexer to re-check one artist as soon as it can."""
    artist = await get_artist_or_404(session, artist_id)
    return MessageOut(ok=True, message=await scan_artist(session, artist))


@router.post(
    "/artists/{artist_id}/download-wanted",
    response_model=MessageOut,
    summary="Queue every wanted release of one artist",
)
async def api_download_wanted(session: SessionDep, artist_id: str) -> MessageOut:
    """Explicitly start downloads for everything this artist has marked wanted.

    This is the opt-in counterpart to ``AUTO_DOWNLOAD``: it queues regardless of
    that setting, because the request is a direct instruction from the user.
    """
    artist = await get_artist_or_404(session, artist_id)
    return MessageOut(ok=True, message=await queue_wanted_for_artist(session, artist))


@router.get(
    "/artists/{artist_id}/albums",
    response_model=AlbumListOut,
    summary="Releases of one artist",
)
async def api_artist_albums(
    session: SessionDep,
    artist_id: str,
    album_status: Annotated[
        AlbumStatus | None, EmptyAsNone, Query(alias="status")
    ] = None,
    release_type: OptStrQuery = None,
    q: OptStrQuery = None,
    limit: int = Query(500, ge=1, le=2000),
    offset: int = Query(0, ge=0),
) -> AlbumListOut:
    """Page through one artist's releases. ``q`` matches title, version or label."""
    await get_artist_or_404(session, artist_id)
    items, total = await list_albums_for_artist(
        session,
        artist_id,
        status=album_status,
        release_type=release_type,
        query=q,
        limit=limit,
        offset=offset,
    )
    return AlbumListOut(items=items, total=total, limit=limit, offset=offset)


@router.post(
    "/artists/bulk",
    response_model=MessageOut,
    summary="Change monitoring settings for many artists at once",
)
async def api_bulk_update_artists(
    session: SessionDep, payload: Annotated[ArtistBulkUpdateIn, Body()]
) -> MessageOut:
    """Apply monitoring changes to a list of artists. Omitted fields are left alone."""
    message, detail = await bulk_update_artists(session, payload)
    return MessageOut(ok=bool(detail.get("updated")), message=message, detail=detail)


@router.post("/scan-all", response_model=MessageOut, summary="Sweep every artist")
async def api_scan_all(session: SessionDep) -> MessageOut:
    """Request a full (slow) sweep of every monitored artist."""
    return MessageOut(ok=True, message=await scan_all(session))


# ---------------------------------------------------------------------------
# Library management (changing what is on disk)
# ---------------------------------------------------------------------------
@router.delete(
    "/albums/{album_id}/files",
    response_model=MessageOut,
    summary="Move a release's files to the trash",
)
async def api_delete_album_files(session: SessionDep, album_id: str) -> MessageOut:
    """Delete one release from the library folder.

    The files go to ``Settings.trash_dir``, not to ``/dev/null`` — recover them
    with ``POST /api/library/trash/{entry_id}/restore`` until the trash is
    emptied. The album row survives and goes back to wanted (or skipped, if it
    is not monitored); nothing is re-queued.
    """
    album = await get_album_or_404(session, album_id)
    result = await delete_album_files(session, album)
    return MessageOut(
        ok=True,
        message=f"Deleted {album.display_title}: {result.summary}.",
        detail={
            "album_id": album.id,
            "trash_id": result.trashed.id if result.trashed else None,
            "status": result.new_status.value if result.new_status else None,
            "tracks_cleared": result.tracks_cleared,
        },
    )


@router.post(
    "/albums/{album_id}/refile",
    response_model=RefilePlanOut,
    summary="Move a release to where the naming template says it belongs",
)
async def api_refile_album(
    session: SessionDep,
    album_id: str,
    dry_run: bool = Query(False, description="Report the plan without moving anything"),
) -> RefilePlanOut:
    """Re-file one release. The whole folder moves, cover art included."""
    album = await get_album_or_404(session, album_id)
    return _plan_out(await refile_album(session, album, apply=not dry_run))


@router.post(
    "/albums/{album_id}/retag",
    response_model=MessageOut,
    summary="Rewrite a release's tags from the catalogue metadata",
)
async def api_retag_album(session: SessionDep, album_id: str) -> MessageOut:
    """Re-tag one release in place. Local only — no Qobuz call, no re-download."""
    album = await get_album_or_404(session, album_id)
    result = await retag_album(session, album)
    return MessageOut(
        ok=result.failed == 0,
        message=f"Re-tagged {album.display_title}: {result.summary}.",
        detail={"album_id": album.id, "tagged": result.tagged, "failed": result.failed},
    )


@router.post(
    "/library/refile",
    response_model=LibraryTidyOut,
    summary="Re-file every release that is not where the template says",
)
async def api_refile_library(
    session: SessionDep,
    artist_id: OptStrQuery = None,
    apply: bool = Query(False, description="Actually move the files"),
    limit: int = Query(2000, ge=1, le=10000),
) -> LibraryTidyOut:
    """Bring the library in line with ``NAMING_TEMPLATE``.

    Previews by default: this walks every downloaded release and moves folders,
    so ``apply=true`` has to be asked for. Albums that cannot be re-filed are
    listed with the reason rather than passed over.
    """
    return await refile_library(session, artist_id=artist_id, apply=apply, limit=limit)


@router.post(
    "/library/retag",
    response_model=LibraryTidyOut,
    summary="Rewrite tags across the library",
)
async def api_retag_library(
    session: SessionDep,
    artist_id: OptStrQuery = None,
    limit: int = Query(2000, ge=1, le=10000),
) -> LibraryTidyOut:
    """Re-tag every downloaded release, or one artist's. No Qobuz calls."""
    return await retag_library(session, artist_id=artist_id, limit=limit)


@router.get("/library/trash", response_model=TrashOut, summary="What is recoverable")
async def api_trash(settings: SettingsDep) -> TrashOut:
    """Everything Qobuzarr has taken out of the library and not yet destroyed."""
    return await trash_contents(settings)


@router.post(
    "/library/trash/{entry_id}/restore",
    response_model=MessageOut,
    summary="Put a trashed batch back",
)
async def api_restore_trash(entry_id: str, settings: SettingsDep) -> MessageOut:
    """Restore one batch to its original path.

    Refused when something is there again — a half-overwritten album is worse
    than a failed restore. The database is not updated; run a disk scan to have
    the files adopted again.
    """
    restored = await restore_trash_entry(entry_id, settings)
    return MessageOut(
        ok=True,
        message=f"Restored to {restored}. Run a disk scan to adopt it again.",
        detail={"path": restored},
    )


@router.delete(
    "/library/trash",
    response_model=MessageOut,
    summary="Destroy trashed files permanently",
)
async def api_empty_trash(
    settings: SettingsDep,
    entry_id: str | None = Query(None, description="One batch; omit for all of them"),
) -> MessageOut:
    """Empty the trash. **This is the one thing here that cannot be undone.**"""
    removed, freed = await empty_trash(entry_id, settings)
    return MessageOut(
        ok=True,
        message=f"Permanently deleted {removed} item(s), freeing {freed / 1048576:.1f} MiB.",
        detail={"removed": removed, "bytes": freed},
    )


# ---------------------------------------------------------------------------
# Library scanning (the disk, not Qobuz)
# ---------------------------------------------------------------------------
@router.get(
    "/library/scan",
    response_model=LibraryScanStatusOut,
    summary="Result of the most recent disk scan",
)
async def api_library_scan_status(
    session: SessionDep, settings: SettingsDep
) -> LibraryScanStatusOut:
    """Whether a disk scan is running, plus the summary of the last one."""
    return LibraryScanStatusOut.model_validate(
        await library_scan_status(session, settings)
    )


@router.post(
    "/library/scan",
    response_model=LibraryScanOut,
    summary="Scan the library folder and adopt what is already there",
)
async def api_library_scan(
    session: SessionDep,
    artist_id: OptStrQuery = None,
    dry_run: bool = Query(False, description="Build the report without writing anything"),
) -> LibraryScanOut:
    """Reconcile the library folder with the database.

    Reads tags off every audio file under ``LIBRARY_PATH``, matches each album
    folder against the database and marks the complete ones ``downloaded`` so
    they stop being wanted. No Qobuz API call is made and no file is modified;
    pass ``dry_run=true`` to see what a scan would change first.
    """
    return LibraryScanOut.model_validate(
        await scan_library(session, artist_id=artist_id, apply=not dry_run)
    )


@router.post(
    "/artists/{artist_id}/library-scan",
    response_model=LibraryScanOut,
    summary="Scan one artist's folder",
)
async def api_artist_library_scan(
    session: SessionDep,
    artist_id: str,
    dry_run: bool = Query(False, description="Build the report without writing anything"),
) -> LibraryScanOut:
    """Disk scan restricted to one artist. ``artist_id`` must already be followed."""
    return LibraryScanOut.model_validate(
        await scan_library(session, artist_id=str(artist_id), apply=not dry_run)
    )


@router.get(
    "/library/import",
    response_model=LibraryImportOut,
    summary="Progress of the artist import",
)
async def api_library_import_status(session: SessionDep) -> LibraryImportOut:
    """Live progress while an import runs, the last run's summary when idle."""
    return LibraryImportOut.model_validate(await library_import_status(session))


@router.get(
    "/library/import/preview",
    summary="Who an import would look up, and how long it would take",
)
async def api_library_import_preview(session: SessionDep) -> dict[str, Any]:
    """Dry disk scan listing every artist on disk that is not followed yet."""
    return await preview_library_import(session)


@router.post(
    "/library/import",
    response_model=LibraryImportOut,
    status_code=http_status.HTTP_202_ACCEPTED,
    summary="Look up the artists found on disk and follow the certain ones",
)
async def api_library_import(
    payload: Annotated[LibraryImportStartIn, Body()] = LibraryImportStartIn(),
) -> LibraryImportOut:
    """Start a background import: one Qobuz search per artist, exact names only.

    Returns immediately with the initial progress snapshot; poll
    ``GET /api/library/import`` for the rest. Artists are followed but **not**
    indexed unless ``index_now`` is set — the scheduled indexer works through
    them at its normal one-per-tick pace — and nothing is ever downloaded.
    """
    return LibraryImportOut.model_validate(await start_library_import(payload))


@router.post(
    "/library/import/cancel",
    response_model=MessageOut,
    summary="Stop a running artist import",
)
async def api_cancel_library_import() -> MessageOut:
    """Stop after the artist currently being searched for."""
    stopped = await cancel_library_import()
    return MessageOut(
        ok=stopped,
        message=(
            "Stopping the import after the current artist."
            if stopped
            else "No import is running."
        ),
    )


# ---------------------------------------------------------------------------
# Albums
# ---------------------------------------------------------------------------
@router.get("/wanted", response_model=AlbumListOut, summary="The missing-album backlog")
async def api_wanted(
    session: SessionDep,
    q: OptStrQuery = None,
    album_status: Annotated[
        AlbumStatus | None, EmptyAsNone, Query(alias="status")
    ] = None,
    monitored: OptBoolQuery = True,
    limit: int = Query(500, ge=1, le=2000),
    offset: int = Query(0, ge=0),
) -> AlbumListOut:
    """Releases that are wanted (or failed) and not yet on disk, oldest first."""
    items, total = await list_wanted_albums(
        session,
        query=q,
        status=album_status,
        monitored=monitored,
        limit=limit,
        offset=offset,
    )
    return AlbumListOut(items=items, total=total, limit=limit, offset=offset)


@router.post(
    "/wanted/download",
    response_model=MessageOut,
    summary="Queue the whole wanted backlog",
)
async def api_download_all_wanted(
    session: SessionDep, limit: int = Query(500, ge=1, le=2000)
) -> MessageOut:
    """Queue every monitored ``wanted`` release. An explicit action, never automatic."""
    return MessageOut(ok=True, message=await queue_all_wanted(session, limit=limit))


@router.get("/albums/{album_id}", response_model=AlbumOut, summary="One album")
async def api_get_album(session: SessionDep, album_id: str) -> AlbumOut:
    """Return one album with its track list. ``album_id`` is a string, not an int."""
    album = await get_album_or_404(session, album_id)
    return album_to_out(album, include_tracks=True)


@router.post(
    "/albums/{album_id}/queue", response_model=MessageOut, summary="Queue an album"
)
async def api_queue_album(session: SessionDep, album_id: str) -> MessageOut:
    """Mark an album wanted and enqueue it for download."""
    album = await get_album_or_404(session, album_id)
    item, created = await queue_album(session, album)
    return MessageOut(
        ok=True,
        message=(
            f"Queued {album.display_title}."
            if created
            else f"{album.display_title} is already queued."
        ),
        detail={"queue_item_id": item.id, "created": created},
    )


@router.post(
    "/albums/{album_id}/monitor",
    response_model=AlbumOut,
    summary="Monitor or ignore an album",
)
async def api_monitor_album(
    session: SessionDep,
    album_id: str,
    payload: Annotated[AlbumUpdateIn | None, Body()] = None,
) -> AlbumOut:
    """Set (or toggle, when the body is empty) an album's monitored flag."""
    album = await get_album_or_404(session, album_id)
    album = await set_album_monitored(session, album, payload or AlbumUpdateIn())
    return album_to_out(album)


# ---------------------------------------------------------------------------
# Queue
# ---------------------------------------------------------------------------
@router.get("/queue", response_model=QueueListOut, summary="Download queue")
async def api_queue(
    session: SessionDep,
    state: OptQueueStateQuery = None,
    limit: int = Query(100, ge=1, le=500),
    offset: int = Query(0, ge=0),
) -> QueueListOut:
    """List queue entries, active first, then pending in worker order."""
    items, total = await list_queue_items(
        session, state=state, limit=limit, offset=offset
    )
    return QueueListOut(items=items, total=total, limit=limit, offset=offset)


@router.post(
    "/queue/{item_id}/retry", response_model=MessageOut, summary="Retry a queue item"
)
async def api_retry_queue_item(session: SessionDep, item_id: int) -> MessageOut:
    """Reset a failed or cancelled queue entry back to pending."""
    item = await get_queue_item_or_404(session, item_id)
    return MessageOut(ok=True, message=await retry_queue_item(session, item))


@router.delete(
    "/queue/{item_id}", response_model=MessageOut, summary="Cancel a queue item"
)
async def api_cancel_queue_item(session: SessionDep, item_id: int) -> MessageOut:
    """Cancel a queue entry (aborting the download when it is active)."""
    item = await get_queue_item_or_404(session, item_id)
    return MessageOut(ok=True, message=await cancel_queue_item(session, item))


@router.get("/queue/{item_id}", response_model=QueueItemOut, summary="One queue item")
async def api_get_queue_item(session: SessionDep, item_id: int) -> QueueItemOut:
    """Return a single queue entry."""
    item = await get_queue_item_or_404(session, item_id)
    return queue_item_to_out(item)


# ---------------------------------------------------------------------------
# Activity
# ---------------------------------------------------------------------------
@router.get("/activity", response_model=ActivityListOut, summary="Activity history")
async def api_activity(
    session: SessionDep,
    level: OptActivityLevelQuery = None,
    event: OptStrQuery = None,
    artist_id: OptStrQuery = None,
    album_id: OptStrQuery = None,
    limit: int = Query(100, ge=1, le=500),
    offset: int = Query(0, ge=0),
) -> ActivityListOut:
    """Page through the activity/history feed, newest first."""
    items, total = await list_activity(
        session,
        level=level,
        event=event,
        artist_id=artist_id,
        album_id=album_id,
        limit=limit,
        offset=offset,
    )
    return ActivityListOut(items=items, total=total, limit=limit, offset=offset)


# ---------------------------------------------------------------------------
# Search
# ---------------------------------------------------------------------------
@router.get("/search", response_model=SearchResultOut, summary="Search Qobuz")
async def api_search(
    session: SessionDep,
    q: str = Query("", description="Free-text query."),
    type: Literal["all", "artists", "albums"] = Query("all"),
    limit: int = Query(25, ge=1, le=100),
    offset: int = Query(0, ge=0),
) -> SearchResultOut:
    """Search the Qobuz catalogue, annotated with what is already in the library."""
    return await run_search(session, q, search_type=type, limit=limit, offset=offset)
