"""APScheduler wiring for the indexer tick and daily housekeeping.

Two jobs run in-process on the FastAPI event loop:

``indexer_tick``
    Fires every ``INDEXER_ARTIST_INTERVAL`` seconds (with jitter) and asks the
    :class:`~app.core.indexer.Indexer` to check **one** artist.  ``max_instances=1``
    and ``coalesce=True`` mean a slow tick can never overlap itself or pile up.

``housekeeping``
    Runs once a day: scans the library folder for albums that are already on
    disk (``LIBRARY_SCAN_NIGHTLY``), prunes old activity rows and finished queue
    items, and re-verifies that files recorded in the library still exist.  The
    scan is local-only, so unlike the indexer tick it costs nothing against the
    Qobuz rate limit.

The module keeps a reference to the scheduler it built so the API layer can
report next-run times (:func:`next_run_time`) and trigger a manual tick
(:func:`trigger_tick_now`) without threading the object through every call.
"""

from __future__ import annotations

import asyncio
import os
from datetime import datetime, timedelta, timezone
from typing import Any, Callable

from apscheduler.job import Job
from apscheduler.schedulers.asyncio import AsyncIOScheduler
from apscheduler.triggers.cron import CronTrigger
from apscheduler.triggers.interval import IntervalTrigger
from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, get_settings
from app.db import session_scope
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    QueueItem,
    QueueState,
    Track,
    TrackStatus,
)
from app.core.indexer import IndexResult, Indexer
from app.core.queue import SessionFactory

__all__ = [
    "ACTIVITY_RETENTION_DAYS",
    "HOUSEKEEPING_HOUR",
    "HOUSEKEEPING_MINUTE",
    "JOB_HOUSEKEEPING",
    "JOB_INDEXER_TICK",
    "QUEUE_RETENTION_DAYS",
    "build_scheduler",
    "get_scheduler",
    "get_job",
    "housekeeping",
    "next_run_time",
    "run_indexer_tick",
    "set_scheduler",
    "shutdown",
    "start_background",
    "trigger_tick_now",
]

logger = get_logger(__name__)

JOB_INDEXER_TICK = "indexer_tick"
JOB_HOUSEKEEPING = "housekeeping"

#: Activity rows older than this are pruned nightly.
ACTIVITY_RETENTION_DAYS = 30
#: Finished (done/cancelled) queue items older than this are pruned nightly.
QUEUE_RETENTION_DAYS = 14
#: Local time the housekeeping job runs at (a quiet hour, off the hour).
HOUSEKEEPING_HOUR = 3
HOUSEKEEPING_MINUTE = 17

#: Set by :func:`build_scheduler` / :func:`set_scheduler`.
_scheduler: AsyncIOScheduler | None = None
#: Indexer bound to the current scheduler, for :func:`trigger_tick_now`.
_indexer: Indexer | None = None
_session_factory: SessionFactory = session_scope


def _now() -> datetime:
    """Timezone-aware current time."""
    return datetime.now(timezone.utc)


async def run_indexer_tick(
    indexer: Indexer, session_factory: SessionFactory = session_scope
) -> IndexResult | None:
    """One indexer tick, with its own session and no exception escaping.

    APScheduler swallows job exceptions into a log line; doing it here means the
    failure also reaches the activity feed via the indexer itself.
    """
    try:
        async with session_factory() as session:
            return await indexer.tick(session)
    except asyncio.CancelledError:
        raise
    except Exception as exc:  # noqa: BLE001 - a bad tick must not kill the job
        logger.exception("Indexer tick failed: %s", exc)
        return None


async def housekeeping(
    session_factory: SessionFactory = session_scope,
    *,
    activity_retention_days: int = ACTIVITY_RETENTION_DAYS,
    queue_retention_days: int = QUEUE_RETENTION_DAYS,
    verify_library: bool = True,
    requeue_missing: bool = False,
    scan_library: bool | None = None,
) -> dict[str, Any]:
    """Nightly maintenance.

    * Scans the library folder and adopts albums that are already on disk, so
      music the user acquired elsewhere stops being wanted.  Controlled by
      ``LIBRARY_SCAN_NIGHTLY`` (*scan_library* overrides it).  This runs
      **first**, because it can only ever mark albums *present*.
    * Deletes activity rows older than *activity_retention_days*.
    * Deletes ``done``/``cancelled`` queue items older than *queue_retention_days*.
    * Re-verifies that every downloaded track's file is still on disk.  Missing
      files flip the track back to ``pending`` and the album to ``wanted``;
      re-queueing is opt-in (*requeue_missing*) so a temporarily unmounted
      library cannot trigger a mass re-download.

    The scan and the verify pass are deliberately in that order: adopt what
    appeared, then flag what disappeared.  Reversing them would let the verify
    pass mark an album wanted a moment before the scan proved it was there.

    Returns a small summary dict, which is also written to the activity feed.
    """
    summary: dict[str, Any] = {
        "activities_pruned": 0,
        "queue_items_pruned": 0,
        "tracks_checked": 0,
        "tracks_missing": 0,
        "albums_flagged": 0,
        "albums_requeued": 0,
        "albums_adopted": 0,
        "files_scanned": 0,
    }

    async with session_factory() as session:
        if scan_library is None:
            scan_library = bool(get_settings().library_scan_nightly)
        if scan_library:
            await _scan_library(session, summary)

        activity_cutoff = _now() - timedelta(days=max(1, activity_retention_days))
        result = await session.execute(
            delete(Activity).where(Activity.created_at < activity_cutoff)
        )
        summary["activities_pruned"] = int(result.rowcount or 0)

        queue_cutoff = _now() - timedelta(days=max(1, queue_retention_days))
        result = await session.execute(
            delete(QueueItem).where(
                QueueItem.state.in_((QueueState.DONE, QueueState.CANCELLED)),
                QueueItem.created_at < queue_cutoff,
            )
        )
        summary["queue_items_pruned"] = int(result.rowcount or 0)

        if verify_library:
            await _verify_library(session, summary, requeue_missing=requeue_missing)

        session.add(
            Activity(
                level=ActivityLevel.INFO,
                event="housekeeping",
                message=(
                    f"Housekeeping: pruned {summary['activities_pruned']} activity row(s) "
                    f"and {summary['queue_items_pruned']} finished queue item(s); "
                    f"verified {summary['tracks_checked']} file(s), "
                    f"{summary['tracks_missing']} missing; "
                    f"adopted {summary['albums_adopted']} album(s) from disk"
                ),
            )
        )

    logger.info("Housekeeping finished: %s", summary)
    return summary


async def _scan_library(session: AsyncSession, summary: dict[str, Any]) -> None:
    """Run a disk scan as part of housekeeping, folding its counts into *summary*.

    The scanner held on :class:`~app.core.state.AppState` is reused when the
    application is running so that the UI's "a scan is in progress" flag stays
    truthful; a standalone call (a test, or ``housekeeping`` invoked from a
    script) builds a throwaway one.

    A failure here is logged and swallowed: an unmounted library must not stop
    the rest of housekeeping from pruning.
    """
    from app.core.scanner import LibraryScanner  # noqa: PLC0415 - avoids an import cycle
    from app.core.state import state_or_none  # noqa: PLC0415 - state imports this module

    state = state_or_none()
    scanner = getattr(state, "scanner", None) or LibraryScanner()

    try:
        result = await scanner.scan(session)
    except Exception as exc:  # noqa: BLE001 - housekeeping must finish regardless
        logger.exception("Nightly library scan failed: %s", exc)
        return

    summary["albums_adopted"] = result.albums_adopted
    summary["files_scanned"] = result.audio_files


async def _verify_library(
    session: AsyncSession, summary: dict[str, Any], *, requeue_missing: bool
) -> None:
    """Check that downloaded track files still exist; flag the ones that do not."""
    rows = await session.execute(
        select(Track).where(
            Track.status == TrackStatus.DOWNLOADED, Track.path.is_not(None)
        )
    )
    tracks = list(rows.scalars().all())
    summary["tracks_checked"] = len(tracks)
    if not tracks:
        return

    paths = [track.path or "" for track in tracks]
    missing_flags = await asyncio.to_thread(
        lambda: [not os.path.exists(path) for path in paths]
    )

    affected_albums: set[str] = set()
    for track, missing in zip(tracks, missing_flags):
        if not missing:
            continue
        summary["tracks_missing"] += 1
        track.status = TrackStatus.PENDING
        affected_albums.add(track.album_id)

    for album_id in affected_albums:
        album = await session.get(Album, album_id)
        if album is None or album.status is AlbumStatus.DOWNLOADING:
            continue
        album.status = AlbumStatus.WANTED
        album.downloaded_at = None
        summary["albums_flagged"] += 1
        await session.flush()
        session.add(
            Activity(
                level=ActivityLevel.WARNING,
                event="library.missing",
                message=f"Files are missing for {album.display_title}; marked wanted again",
                album_id=album.id,
                artist_id=album.artist_id,
            )
        )
        if requeue_missing:
            from app.core.indexer import ensure_queue_item  # noqa: PLC0415

            if await ensure_queue_item(session, album) is not None:
                summary["albums_requeued"] += 1


def build_scheduler(
    indexer: Indexer,
    *,
    settings: Settings | None = None,
    session_factory: SessionFactory = session_scope,
    housekeeping_job: Callable[[], Any] | None = None,
    register: bool = True,
) -> AsyncIOScheduler:
    """Create (but do not start) the application scheduler.

    Args:
        indexer: The indexer whose :meth:`~app.core.indexer.Indexer.tick` is
            scheduled.
        settings: Effective settings; defaults to :func:`app.config.get_settings`.
        session_factory: Session context-manager factory for the jobs.
        housekeeping_job: Override for the nightly job (tests).
        register: Store the scheduler module-side so :func:`get_scheduler` and
            :func:`trigger_tick_now` can find it.

    The indexer job is added even when ``INDEXER_ENABLED`` is false, but paused,
    so the UI can still show it and resume it without a restart.
    """
    settings = settings or get_settings()
    scheduler = AsyncIOScheduler(timezone="UTC")

    interval = max(1, int(settings.indexer_artist_interval))
    jitter = int(interval * min(max(settings.indexer_jitter_pct, 0.0), 1.0))

    scheduler.add_job(
        run_indexer_tick,
        trigger=IntervalTrigger(seconds=interval, jitter=jitter or None),
        id=JOB_INDEXER_TICK,
        name="Indexer tick (one artist)",
        args=(indexer, session_factory),
        max_instances=1,
        coalesce=True,
        misfire_grace_time=interval,
        replace_existing=True,
        next_run_time=_now() + timedelta(seconds=min(interval, 30)),
    )

    scheduler.add_job(
        housekeeping_job or housekeeping,
        trigger=CronTrigger(hour=HOUSEKEEPING_HOUR, minute=HOUSEKEEPING_MINUTE, timezone="UTC"),
        id=JOB_HOUSEKEEPING,
        name="Daily housekeeping",
        args=() if housekeeping_job else (session_factory,),
        max_instances=1,
        coalesce=True,
        misfire_grace_time=3600,
        replace_existing=True,
    )

    if register:
        set_scheduler(scheduler, indexer=indexer, session_factory=session_factory)

    logger.info(
        "Scheduler built: indexer tick every %ds (+/-%ds jitter), housekeeping at %02d:%02d UTC",
        interval,
        jitter,
        HOUSEKEEPING_HOUR,
        HOUSEKEEPING_MINUTE,
    )
    return scheduler


async def start_background(
    indexer: Indexer,
    *,
    settings: Settings | None = None,
    session_factory: SessionFactory = session_scope,
    scheduler: AsyncIOScheduler | None = None,
) -> AsyncIOScheduler:
    """Build (if needed) and start the scheduler on the running event loop.

    When ``INDEXER_ENABLED`` is false the tick job is paused immediately, so
    only housekeeping runs.
    """
    settings = settings or get_settings()
    sched = scheduler or build_scheduler(
        indexer, settings=settings, session_factory=session_factory
    )
    if not sched.running:
        sched.start()
    if not settings.indexer_enabled:
        job = sched.get_job(JOB_INDEXER_TICK)
        if job is not None:
            job.pause()
        logger.warning("INDEXER_ENABLED is false: the indexer tick job is paused")
    return sched


async def shutdown(wait: bool = False) -> None:
    """Stop the registered scheduler, if any. Safe to call more than once."""
    global _scheduler, _indexer
    sched = _scheduler
    _scheduler = None
    _indexer = None
    if sched is None:
        return
    if sched.running:
        sched.shutdown(wait=wait)
    logger.info("Scheduler shut down")


def set_scheduler(
    scheduler: AsyncIOScheduler | None,
    *,
    indexer: Indexer | None = None,
    session_factory: SessionFactory = session_scope,
) -> None:
    """Register the process-wide scheduler (and the indexer it drives)."""
    global _scheduler, _indexer, _session_factory
    _scheduler = scheduler
    if indexer is not None:
        _indexer = indexer
    _session_factory = session_factory


def get_scheduler() -> AsyncIOScheduler | None:
    """The registered scheduler, or ``None`` before startup."""
    return _scheduler


def get_job(job_id: str = JOB_INDEXER_TICK) -> Job | None:
    """Look up one of the scheduled jobs by id."""
    sched = _scheduler
    if sched is None:
        return None
    try:
        return sched.get_job(job_id)
    except Exception:  # noqa: BLE001 - scheduler not started yet
        return None


def next_run_time(job_id: str = JOB_INDEXER_TICK) -> datetime | None:
    """Next fire time of a job as an aware UTC datetime, or ``None``."""
    job = get_job(job_id)
    stamp = getattr(job, "next_run_time", None) if job is not None else None
    if stamp is None:
        return None
    return stamp if stamp.tzinfo is not None else stamp.replace(tzinfo=timezone.utc)


def pause_indexer_job() -> bool:
    """Pause the tick job; returns ``False`` when there is nothing to pause."""
    job = get_job(JOB_INDEXER_TICK)
    if job is None:
        return False
    job.pause()
    logger.info("Indexer tick job paused")
    return True


def resume_indexer_job() -> bool:
    """Resume the tick job; returns ``False`` when there is nothing to resume."""
    job = get_job(JOB_INDEXER_TICK)
    if job is None:
        return False
    job.resume()
    logger.info("Indexer tick job resumed")
    return True


async def trigger_tick_now(indexer: Indexer | None = None) -> IndexResult | None:
    """Run one indexer tick immediately, inline (used by the API's "scan now").

    Returns the :class:`~app.core.indexer.IndexResult`, or ``None`` when no
    artist was due, the indexer is busy, or none is registered.
    """
    target = indexer or _indexer
    if target is None:
        logger.warning("trigger_tick_now called before the scheduler was registered")
        return None
    return await run_indexer_tick(target, _session_factory)
