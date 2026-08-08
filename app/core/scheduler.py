"""APScheduler wiring for the indexer tick and daily housekeeping.

Two jobs run in-process on the FastAPI event loop:

``indexer_tick``
    Fires every ``INDEXER_ARTIST_INTERVAL`` seconds (with jitter) and asks the
    :class:`~app.core.indexer.Indexer` to check **one** artist.  ``max_instances=1``
    and ``coalesce=True`` mean a slow tick can never overlap itself or pile up.

``housekeeping``
    Runs once a day: scans the library folder for albums that are already on
    disk (``LIBRARY_SCAN_NIGHTLY``), prunes old activity rows and finished queue
    items, re-verifies that files recorded in the library still exist, and
    re-hashes a rotating slice of them (``INTEGRITY_REVERIFY_FRACTION``) to catch
    the files whose contents changed without their size or mtime saying so.  The
    scan is local-only, so unlike the indexer tick it costs nothing against the
    Qobuz rate limit.

The integrity half of that job — :func:`verify_integrity` — is written as a
reusable pass rather than as a step inside the nightly one, because ``cli.py``
runs exactly the same code for ``verify-library`` and ``baseline-library``.  One
implementation, three callers: a hand-run verification that disagreed with the
nightly one about what "changed" means would be worse than having neither.

The module keeps a reference to the scheduler it built so the API layer can
report next-run times (:func:`next_run_time`) and trigger a manual tick
(:func:`trigger_tick_now`) without threading the object through every call.
"""

from __future__ import annotations

import asyncio
import json
import math
import os
import time
from collections import Counter
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Iterable, Sequence

from apscheduler.job import Job
from apscheduler.schedulers.asyncio import AsyncIOScheduler
from apscheduler.triggers.cron import CronTrigger
from apscheduler.triggers.interval import IntervalTrigger
from sqlalchemy import delete, func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, effective_for, get_settings
from app.db import session_scope
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    QueueItem,
    QueueState,
    Setting,
    Track,
    TrackStatus,
)
from app.core.indexer import IndexResult, Indexer
from app.core.integrity import (
    FileStamp,
    IntegrityState,
    album_content_digest,
    classify,
    read_stamp,
)
from app.core.queue import SessionFactory

__all__ = [
    "ACTIVITY_RETENTION_DAYS",
    "HOUSEKEEPING_HOUR",
    "HOUSEKEEPING_MINUTE",
    "INTEGRITY_BATCH",
    "INTEGRITY_MAX_FILES",
    "LAST_INTEGRITY_KEY",
    "IntegrityBusyError",
    "integrity_running",
    "load_last_integrity",
    "store_last_integrity",
    "JOB_ENRICHMENT",
    "JOB_FOLDER_BINDING",
    "JOB_HOUSEKEEPING",
    "JOB_INDEXER_TICK",
    "QUEUE_RETENTION_DAYS",
    "IntegrityReport",
    "build_scheduler",
    "run_binder_tick",
    "get_scheduler",
    "get_job",
    "housekeeping",
    "next_run_time",
    "run_enrichment_tick",
    "run_indexer_tick",
    "set_scheduler",
    "shutdown",
    "start_background",
    "trigger_tick_now",
    "verify_integrity",
]

logger = get_logger(__name__)

JOB_INDEXER_TICK = "indexer_tick"
JOB_HOUSEKEEPING = "housekeeping"
JOB_ENRICHMENT = "enrichment_tick"
JOB_FOLDER_BINDING = "folder_binding_tick"

#: Activity rows older than this are pruned nightly.
ACTIVITY_RETENTION_DAYS = 30
#: Finished (done/cancelled) queue items older than this are pruned nightly.
QUEUE_RETENTION_DAYS = 14
#: Local time the housekeeping job runs at (a quiet hour, off the hour).
HOUSEKEEPING_HOUR = 3
HOUSEKEEPING_MINUTE = 17

#: Files measured between commits. Hashing is the slow part and it happens with
#: **no session open**, so this is what decides how much work a crash or a
#: shutdown throws away — and, in the other direction, how often the pass takes
#: SQLite's single write lock away from the download worker. Two hundred files is
#: about forty seconds of hashing followed by one short transaction.
INTEGRITY_BATCH = 200

#: Hard ceiling on how many files one nightly re-verification hashes, whatever
#: ``INTEGRITY_REVERIFY_FRACTION`` works out to. At roughly 201 ms per file this
#: is about seven minutes of disk; the fraction alone is a *share*, and a share of
#: a 100 000-file library is a nightly job that is still running at breakfast and
#: is switched off by lunchtime. The rotation is what makes the ceiling harmless:
#: whatever is not reached tonight sorts to the front tomorrow, so bounding the
#: run delays a verdict rather than losing one.
INTEGRITY_MAX_FILES = 2000

#: ``settings`` row holding the last verification pass, so the Integrity screen
#: survives a restart — the same arrangement, and the same reasoning, as
#: :data:`app.core.scanner.LAST_SCAN_KEY`.
LAST_INTEGRITY_KEY = "library.last_integrity"

#: Held for the whole of :func:`verify_integrity`. One pass at a time, and a
#: second request is **refused** rather than queued: two passes hashing at once
#: would double the disk contention while taking turns at SQLite's single write
#: lock, and the second one would measure files the first has already
#: re-baselined — so it would spend an hour producing verdicts about a moving
#: target. Refusing is also what lets the API answer 409 honestly instead of
#: accepting a request it will silently sit on.
_INTEGRITY_LOCK = asyncio.Lock()


class IntegrityBusyError(RuntimeError):
    """Raised when a verification pass is asked for while one is running."""


def integrity_running() -> bool:
    """Whether a verification pass is hashing right now.

    Reads the same lock :func:`verify_integrity` takes, so it is true for the
    nightly rotation as well as for a pass somebody asked for by hand — the
    screen has to say "a pass is running" either way, and a flag owned by the API
    layer would only ever know about half of them.
    """
    return _INTEGRITY_LOCK.locked()


#: Set by :func:`build_scheduler` / :func:`set_scheduler`.
_scheduler: AsyncIOScheduler | None = None
#: Indexer bound to the current scheduler, for :func:`trigger_tick_now`.
_indexer: Indexer | None = None
_session_factory: SessionFactory = session_scope


def _now() -> datetime:
    """Timezone-aware current time."""
    return datetime.now(timezone.utc)


async def run_indexer_tick(
    indexer: Indexer,
    session_factory: SessionFactory = session_scope,
    enricher: Any = None,
) -> IndexResult | None:
    """One indexer tick, with its own session and no exception escaping.

    APScheduler swallows job exceptions into a log line; doing it here means the
    failure also reaches the activity feed via the indexer itself.

    When an enricher is wired in, a tick that discovered something kicks it as
    soon as the indexer's session has **closed** — never while it is open. The
    indexer already queued the new rows at high priority inside its transaction;
    this is what turns "queued" into "fetched now" rather than "fetched at the
    next scheduled enrichment tick".
    """
    result: IndexResult | None = None
    try:
        async with session_factory() as session:
            result = await indexer.tick(session)
    except asyncio.CancelledError:
        raise
    except Exception as exc:  # noqa: BLE001 - a bad tick must not kill the job
        logger.exception("Indexer tick failed: %s", exc)
        return None

    if enricher is not None and result is not None:
        await run_enrichment_tick(enricher)
    return result


async def run_enrichment_tick(enricher: Any) -> Any:
    """One enrichment tick, with no exception escaping.

    Note there is no session argument, unlike :func:`run_indexer_tick`. The
    enricher opens and closes its own sessions around each phase precisely so
    that none of them is held while it is waiting on a third party — see
    :mod:`app.core.enricher`.
    """
    try:
        return await enricher.tick()
    except asyncio.CancelledError:
        raise
    except Exception as exc:  # noqa: BLE001 - a bad tick must not kill the job
        logger.exception("Enrichment tick failed: %s", exc)
        return None


async def run_binder_tick(
    identify: Any,
    session_factory: SessionFactory,
    settings: Settings,
) -> Any:
    """One folder-binding pass, with no exception escaping.

    The nightly answer to a failure the disk scan cannot fix by retrying.
    ``scanner._pick_album`` matches a folder by normalising its title, runs on
    every folder on every scan, and is a pure function of inputs that do not
    change — so a folder whose name is a different string for the same record
    ("Play: The Guitar Album" against a catalogue calling it "Play") is re-tried
    nightly, fails identically, and stays in the wanted list for good. Retrying
    a deterministic function on unchanged inputs is not progress; what has to
    change is the *function*, and this is it: the audio identifies the release
    and the folder's name is never consulted.

    Why a separate job rather than part of the scan. The scan may never call
    Qobuz — that is what makes it safe to run unattended, and it stays true.
    This runs beside it, in the shape :func:`run_enrichment_tick` already
    established: bounded per pass, calling upstreams, writing its own table, and
    convergent because every folder it settles is skipped next time.

    Running it *here* rather than only from the CLI also fixes something the
    command cannot. A CLI process builds its own providers, so its MusicBrainz
    and AcoustID limiters are a second set over the same hosts while the server
    holds the first — the one-limiter-per-upstream rule broken by construction.
    In-process it uses the ladder's own rungs.
    """
    if identify is None:
        return None
    try:
        from app.core.binder import bind_unmatched_folders  # noqa: PLC0415 - cycle

        async with session_factory() as session:
            result = await bind_unmatched_folders(
                session,
                identify=identify,
                settings=settings,
                limit=max(1, int(settings.folder_binding_batch)),
            )
        if result.bound:
            logger.info(
                "Folder binding: %d folder(s) bound; the next disk scan adopts them",
                result.bound,
            )
        return result
    except asyncio.CancelledError:
        raise
    except Exception as exc:  # noqa: BLE001 - a bad pass must not kill the job
        logger.exception("Folder binding pass failed: %s", exc)
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
    * Drops enrichment state rows whose entity is gone, and the ones for entities
      that are no longer in the library — enrichment is scoped to what is on
      disk, and those rows are a work list nothing will ever claim.
    * Re-hashes a rotating slice of the files that *are* there
      (``INTEGRITY_REVERIFY_FRACTION``, capped at :data:`INTEGRITY_MAX_FILES`), so
      a file rewritten behind Qobuzarr's back is found within the month even when
      its size and mtime were left looking untouched.

    It **counts** the unplayable files it knows about and acts on none of them.
    This job used to quarantine them — trash the file, blank the track, mark the
    release wanted — and that is no longer automatic. The objection is not that
    the quarantine is unsafe (it goes to the trash, through every gate) but that
    it *erases its own alarm*: ``corrupt_files`` is what brings somebody to the
    Integrity screen, moving the file out clears it, and the release is left
    saying only that it is incomplete, which is indistinguishable from the
    thousands of releases nobody has ever downloaded. The person who could press
    "download again" is never told there is anything to press it for. So the
    verdict stays, the file stays, and the number is reported.
    :func:`app.core.librarian.quarantine_corrupt_files` is unchanged and still
    reachable from ``POST /api/library/quarantine``.

    The scan and the verify pass are deliberately in that order: adopt what
    appeared, then flag what disappeared.  Reversing them would let the verify
    pass mark an album wanted a moment before the scan proved it was there.  The
    enrichment purge comes last for the same reason: it acts on the scope those
    two passes have just finished settling — and stands down entirely on a run
    where the verify pass demoted anything, because an unreachable library looks
    exactly like an empty one.

    The re-hash runs after all of that and **outside** the housekeeping session,
    which is not a stylistic choice: it is minutes of blocking disk I/O, and
    SQLite has one writer, so doing it inside this transaction would hold the
    write lock long enough to fail whatever the download worker was committing.
    It stands down on a demoting run too — same transient, same argument.

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
        "corrupt_files_waiting": 0,
        "enrichment_pruned": 0,
        "enrichment_purged": 0,
        "files_verified": 0,
        "files_changed": 0,
        # Zero rather than absent, so a run with the follow pass off is
        # distinguishable from one where it ran and found nobody only by the
        # activity row, never by a missing key a caller has to guard.
        "artists_followed": 0,
        "artists_for_review": 0,
    }

    async with session_factory() as session:
        if scan_library is None:
            scan_library = bool(effective_for(get_settings()).library_scan_nightly)
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

        await _count_corrupt(session, summary)
        await _prune_enrichment(session, summary)

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

    await _reverify_files(session_factory, summary)
    await _follow_new_artists(summary)

    logger.info("Housekeeping finished: %s", summary)
    return summary


async def _follow_new_artists(summary: dict[str, Any]) -> None:
    """Follow the artists the scan found on disk but nobody follows yet.

    This is the step that makes the nightly run **two-way**. Every other pass
    here only ever reduces what Qobuzarr would do — the scan can mark an album
    present but never wanted, the verify pass reverses on the next run, the purge
    deletes a work list that rebuilds itself. This one adds work, deliberately,
    because a library that keeps growing on disk and never grows in the database
    is a library the program has stopped describing.

    Four things bound it, and they are the whole safety argument:

    * **It follows; it does not download.** ``AUTO_DOWNLOAD`` is unchanged and
      still governs whether anything is ever queued, and the importer follows
      with ``index_now=False``, so the scheduled indexer picks the new artists up
      one per tick at its configured pace instead of firing hundreds of
      back-catalogue imports at 03:17.
    * **It is exact or nothing, twice over.** A name must match under
      ``artist_key``; failing that the audio route must match a *barcode*. There
      is no third, looser attempt, so the worst case is an artist that stays
      unfollowed for another night.
    * **It runs last**, after the scan, the verify pass and the purge have
      settled what is actually on disk. Anything else would have it act on a
      library that a later step was about to correct.
    * **It can be turned off** — ``LIBRARY_FOLLOW_NIGHTLY`` — and it stands down
      by itself when no importer is wired, when one is already running, or when a
      run is in flight from the UI.

    Swallows its own failure, like every other step here: an upstream that is
    down must not lose the night's maintenance.
    """
    settings = effective_for(get_settings())
    if not bool(getattr(settings, "library_follow_nightly", False)):
        return

    from app.core.state import get_state  # noqa: PLC0415 - avoids a cycle

    try:
        state = get_state()
    except Exception:  # noqa: BLE001 - no app state means the CLI, not the server
        return
    importer = getattr(state, "importer", None)
    if importer is None or importer.running:
        return

    try:
        # start() spawns the run and returns immediately, which is what the UI
        # wants; the nightly job wants the opposite, so it waits for the task it
        # just started. wait() shields, so a cancelled housekeeping job does not
        # abandon a half-finished import.
        await importer.start(index_now=False)
        await importer.wait()
    except Exception:  # noqa: BLE001 - a failed import must not fail the night
        logger.exception("Nightly artist follow failed")
        return

    progress = importer.snapshot()
    summary["artists_followed"] = int(progress.get("followed") or 0)
    summary["artists_for_review"] = int(progress.get("needs_review") or 0)


async def _count_corrupt(session: AsyncSession, summary: dict[str, Any]) -> None:
    """Count the unplayable files waiting for somebody, and act on none of them.

    Housekeeping used to *quarantine* here, trashing every file with a corruption
    verdict on it. That is no longer done automatically, and the reason is what
    the quarantine costs rather than what it risks: moving the file out clears
    ``corrupt_files``, so the alarm that brought somebody to the screen turns
    itself off, and the release is left saying only that it is incomplete. The
    problem stops being *this file is broken, replace it* and becomes one more
    wanted row among thousands. An automatic fix that erases the evidence of what
    it fixed is worse than no fix: the person who could press "download again"
    never learns there is anything to press it for.

    So the verdict stays on the row, the file stays where it is, and the count is
    reported. :func:`app.core.librarian.quarantine_corrupt_files` is unchanged and
    still reachable from ``POST /api/library/quarantine`` — trashing a broken file
    is a decision somebody makes while looking at it.

    Swallows its own failure, like every other step here.
    """
    from app.core.librarian import corrupt_file_count  # noqa: PLC0415 - cycle

    try:
        summary["corrupt_files_waiting"] = await corrupt_file_count(session)
    except Exception:  # noqa: BLE001
        logger.exception("Counting unplayable files failed")


async def _prune_enrichment(session: AsyncSession, summary: dict[str, Any]) -> None:
    """Drop enrichment state rows whose entity is gone or out of scope.

    Two statements answering two different questions. ``EnrichmentState.entity_id``
    is polymorphic — it names either an artist or an album — so it cannot carry a
    foreign key and nothing cascades; that is the price of having one table the
    orchestrator can poll instead of three, and the first statement is what pays
    it. The second is about entities that still exist but are not in the library:
    enrichment is scoped to what is on disk, and a row for a release nobody has
    downloaded is work that will never be claimed and a number that makes every
    coverage figure on the enrichment page wrong.

    Runs **after** the disk scan and the verify pass for the same reason
    everything else here does: adopt what appeared and flag what disappeared
    first, so the scope this purges against is the one that is true this morning
    rather than the one that was true last night. Only ``enrichment_state`` rows
    go — never the metadata tables, which hold what was learned and what makes an
    applied release type reversible.

    **The purge stands down when the verify pass just demoted something.** Those
    two steps compose badly on the one night that matters: an unmounted library
    makes every downloaded track's file look missing, so verify flips the whole
    collection back to ``wanted``, and a purge running behind it would read that
    as "nothing is in the library any more" and delete every state row there is —
    including the ``ok`` rows whose ``next_attempt_at`` is the record of *already
    asked, do not ask again*. The mount returns, the scan re-adopts, and the
    entire library is re-enriched from zero: a full round of MusicBrainz, Deezer,
    Cover Art Archive, Wikidata and AcoustID lookups, plus re-fingerprinting every
    file, none of it due for ``enrichment_refresh_days``. This is the same
    transient ``requeue_missing`` already defaults to False to survive, and the
    same answer — the purge is idempotent and loses nothing by waiting a night,
    so a genuine deletion is collected on the next pass.

    Swallows its own failure, like every other step here: a prune that cannot run
    must not stop the rest of housekeeping.
    """
    from app.core.enricher import (  # noqa: PLC0415 - import cycle
        prune_enrichment_orphans,
        purge_out_of_scope_enrichment,
    )

    try:
        summary["enrichment_pruned"] = await prune_enrichment_orphans(session)
    except Exception:  # noqa: BLE001
        logger.exception("Pruning orphaned enrichment rows failed")

    demoted = int(summary.get("albums_flagged") or 0)
    if demoted:
        summary["enrichment_purged"] = 0
        summary["enrichment_purge_skipped"] = demoted
        logger.warning(
            "Skipping the out-of-scope enrichment purge: the verify pass demoted "
            "%d album(s) this run, which an unreachable library is indistinguishable "
            "from. The purge runs on the next pass that demotes nothing.",
            demoted,
        )
        return

    try:
        summary["enrichment_purged"] = await purge_out_of_scope_enrichment(session)
    except Exception:  # noqa: BLE001
        logger.exception("Purging out-of-scope enrichment rows failed")


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


# ---------------------------------------------------------------------------
# Integrity: measuring the files against what was recorded about them
# ---------------------------------------------------------------------------
@dataclass(frozen=True, slots=True)
class _Candidate:
    """One track to measure, and what the database currently claims about it.

    A snapshot rather than an ORM row, for the same reason
    :mod:`app.core.enricher` takes snapshots: the expensive part happens with no
    session open, so nothing that survives across it may be a live instance whose
    attribute access would go back to a closed session.
    """

    track_id: str
    path: str
    recorded: FileStamp


@dataclass(slots=True)
class IntegrityReport:
    """What one verification pass found, and what it wrote.

    ``states`` counts every track by the verdict :func:`app.core.integrity.classify`
    gave it *before* anything was written, which is the only moment those counts
    mean anything: a pass that re-baselines a ``RETAGGED`` file would otherwise
    report it as ``VERIFIED`` — true of the row afterwards, and useless to the
    person who asked what changed.
    """

    #: Rows with a file to measure that this pass looked at.
    checked: int = 0
    #: Files actually opened and hashed. Below :attr:`checked` only by the ones
    #: that were not there.
    hashed: int = 0
    #: Rows that had no recorded hash and now have one.
    baselined: int = 0
    #: Releases whose ``content_digest`` was recomputed to a different value.
    albums_restamped: int = 0
    #: Releases queued for re-identification because their audio changed.
    albums_reopened: int = 0
    #: Wall-clock seconds, which is almost entirely disk.
    elapsed: float = 0.0
    #: False for a dry run: everything was measured, nothing was written.
    applied: bool = True
    #: Verdicts as they were given, keyed by state. Read through :meth:`count`.
    states: Counter[IntegrityState] = field(default_factory=Counter)

    def count(self, state: IntegrityState) -> int:
        """How many tracks came back in *state*."""
        return int(self.states.get(state, 0))

    @property
    def changed(self) -> int:
        """Tracks whose bytes disagreed with what was recorded for them."""
        return self.count(IntegrityState.RETAGGED) + self.count(IntegrityState.REPLACED)

    def as_dict(self) -> dict[str, Any]:
        """Flat summary, for the housekeeping summary and the activity feed."""
        payload: dict[str, Any] = {
            "checked": self.checked,
            "hashed": self.hashed,
            "baselined": self.baselined,
            "albums_restamped": self.albums_restamped,
            "albums_reopened": self.albums_reopened,
            "elapsed": round(self.elapsed, 1),
        }
        payload.update({state.value: self.count(state) for state in IntegrityState})
        return payload


# ---------------------------------------------------------------------------
# Persistence of the last result
# ---------------------------------------------------------------------------
async def store_last_integrity(session: AsyncSession, report: IntegrityReport) -> None:
    """Remember *report* in the ``settings`` table so a screen survives a restart.

    The same arrangement as :func:`app.core.scanner.store_last_scan`, for the same
    reason: a pass takes minutes and happens at three in the morning, so the only
    way anybody sees what it found is if it is still there in the morning.

    ``finished_at`` is stamped here rather than on the report, because it is a
    fact about the *storing* — a dry run that is never stored has no finished-at
    to speak of, and the report itself already carries how long it took.
    """
    payload = dict(report.as_dict())
    payload["applied"] = report.applied
    payload["finished_at"] = _now().isoformat()
    encoded = json.dumps(payload, default=str)
    row = await session.get(Setting, LAST_INTEGRITY_KEY)
    if row is None:
        session.add(Setting(key=LAST_INTEGRITY_KEY, value=encoded))
    else:
        row.value = encoded
        row.updated_at = _now()


async def load_last_integrity(session: AsyncSession) -> dict[str, Any] | None:
    """The stored summary of the most recent pass, or ``None``.

    A row written by an older version that no longer parses reads as absent
    rather than raising into a page render — the screen's honest answer is then
    "nothing has run yet", which is the same thing it would say on a fresh
    install.
    """
    row = await session.get(Setting, LAST_INTEGRITY_KEY)
    if row is None or not row.value:
        return None
    try:
        data = json.loads(row.value)
    except (TypeError, ValueError):
        logger.warning("Stored integrity summary is not valid JSON; ignoring it")
        return None
    return data if isinstance(data, dict) else None


async def verify_integrity(
    session_factory: SessionFactory = session_scope,
    *,
    limit: int | None = None,
    unknown_only: bool = False,
    apply: bool = True,
) -> IntegrityReport:
    """Measure files on disk against the baseline recorded for them.

    The fourth principle of the integrity model — *verify the disk before
    trusting the database* — as a pass anything can run. Three callers share it:
    the nightly job (a rotating slice, via :func:`_reverify_files`), ``cli.py
    verify-library`` (everything, or ``--limit`` of it) and ``cli.py
    baseline-library`` (``unknown_only``).

    Args:
        session_factory: Where sessions come from. Several short ones are opened,
            never one long one — see below.
        limit: Stop after this many tracks. The order is the rotation order, so a
            limited run measures whatever has gone longest without being looked
            at.
        unknown_only: Only tracks with no recorded hash. This is what
            "baseline the library" means: it converges, because every row it
            writes leaves the candidate set, and it can never overwrite a
            measurement somebody else made.
        apply: ``False`` measures and classifies but writes nothing at all — not
            the stamps, not the release digests, not the re-identification marks.

    **No session is held across the hashing.** Hashing a batch is tens of seconds
    of blocking disk I/O in a worker thread, and SQLite has exactly one writer:
    a session left open across it would hold the write lock long enough for
    :class:`~app.core.downloader.AlbumDownloader`'s per-track commits to hit
    ``busy_timeout``, at which point :class:`~app.core.queue.QueueWorker`'s broad
    ``except`` records a perfectly good download as failed. So the pass runs in
    batches of :data:`INTEGRITY_BATCH`: snapshot (one short transaction), measure
    (no session), write (another short transaction).

    Nothing here queues a download, and nothing changes an album's status. A file
    that is *gone* is not this pass's business either — it writes nothing for a
    ``MISSING`` verdict and leaves the recorded baseline standing, because
    demotion happens at the album level in :func:`_verify_library`, where it
    reverses when the mount comes back.

    Raises:
        IntegrityBusyError: another pass is already running. Refused rather than
            queued — see :data:`_INTEGRITY_LOCK`.
    """
    if _INTEGRITY_LOCK.locked():
        raise IntegrityBusyError("An integrity pass is already running.")
    async with _INTEGRITY_LOCK:
        return await _run_integrity(
            session_factory, limit=limit, unknown_only=unknown_only, apply=apply
        )


async def _run_integrity(
    session_factory: SessionFactory,
    *,
    limit: int | None,
    unknown_only: bool,
    apply: bool,
) -> IntegrityReport:
    """The pass itself, run with :data:`_INTEGRITY_LOCK` held.

    Split out so the guard is a wrapper rather than an indent: the body opens and
    closes several sessions and the one thing that must be obvious about it is
    that none of them spans the hashing.
    """
    started = time.monotonic()
    report = IntegrityReport(applied=apply)

    candidates = await _integrity_candidates(
        session_factory, limit=limit, unknown_only=unknown_only
    )
    for batch in _batched(candidates, INTEGRITY_BATCH):
        measured = await asyncio.to_thread(_measure_files, batch)
        verdicts: dict[str, tuple[IntegrityState, FileStamp | None]] = {}
        for candidate in batch:
            current = measured.get(candidate.track_id)
            state = classify(recorded=candidate.recorded, current=current)
            verdicts[candidate.track_id] = (state, current)
            report.states[state] += 1
            report.checked += 1
            if current is not None:
                report.hashed += 1
        if apply:
            await _record_measurements(session_factory, verdicts, report)

    report.elapsed = time.monotonic() - started
    logger.info("Integrity pass finished: %s", report.as_dict())
    return report


async def _integrity_candidates(
    session_factory: SessionFactory,
    *,
    limit: int | None,
    unknown_only: bool,
) -> list[_Candidate]:
    """The tracks to measure, in rotation order, as snapshots.

    Only rows that claim to have a file: ``DOWNLOADED`` and a path. A row the
    verify pass has already demoted to ``PENDING`` is a file that was not there
    an hour ago, and opening it again here would only produce ``MISSING`` a
    second time.

    The order is what makes the nightly slice a *rotation* rather than the same
    two thousand files every night: never-verified rows first, then the ones
    verified longest ago, with the id as a tie-break so the sequence is stable
    across runs. Everything is therefore reached within
    ``1 / INTEGRITY_REVERIFY_FRACTION`` runs, which is the guarantee the setting
    is written to make.

    Columns, not entities, deliberately: these outlive the session they were read
    in, and an expired ORM instance would go back to a closed one to answer for
    its own path.
    """
    statement = (
        select(
            Track.id,
            Track.path,
            Track.content_hash,
            Track.sample_count,
            Track.file_size,
            Track.file_mtime,
        )
        .where(Track.path.is_not(None), Track.status == TrackStatus.DOWNLOADED)
        .order_by(
            Track.verified_at.is_(None).desc(),
            Track.verified_at.asc(),
            Track.id.asc(),
        )
    )
    if unknown_only:
        statement = statement.where(Track.content_hash.is_(None))
    if limit is not None and limit > 0:
        statement = statement.limit(limit)

    async with session_factory() as session:
        rows = (await session.execute(statement)).all()

    return [
        _Candidate(
            track_id=str(track_id),
            path=str(path),
            recorded=FileStamp(
                size=int(size or 0),
                mtime=float(mtime or 0.0),
                content_hash=content_hash,
                sample_count=sample_count,
            ),
        )
        for track_id, path, content_hash, sample_count, size, mtime in rows
    ]


def _measure_files(batch: Sequence[_Candidate]) -> dict[str, FileStamp | None]:
    """Hash and parse one batch. **Synchronous** — call through ``asyncio.to_thread``.

    Every file is opened, with no tripwire in front of it. That is the difference
    between this pass and the disk scan, and it is the whole point: size and mtime
    are not evidence, a tool that rewrites a file and restores its mtime walks
    straight past them, and this is the thing that eventually catches it.
    """
    return {candidate.track_id: read_stamp(Path(candidate.path)) for candidate in batch}


async def _record_measurements(
    session_factory: SessionFactory,
    verdicts: dict[str, tuple[IntegrityState, FileStamp | None]],
    report: IntegrityReport,
) -> None:
    """Write one batch of measurements, in one short transaction.

    The stamp is written as a **pair**: ``content_hash`` and ``sample_count``
    always come from the same measurement, including when the sample count came
    back ``None`` because the container stopped parsing. Keeping the old count
    beside a fresh hash would leave the row describing two different files, and
    the next pass would compare a sample count that was never true of the bytes
    it is now holding.

    A ``MISSING`` file is skipped entirely: there is nothing to record, and
    clearing the baseline would throw away the only evidence of what the file
    used to be.

    A ``REPLACED`` file re-opens its release for identification. Different audio
    under the same filename means the recording ids, the release match and the
    fingerprint verdict were all made about bytes that are gone — so the album
    goes back on the enrichment work list via
    :func:`app.core.enricher.reopen_library_albums`, which makes no request and
    queues no download. ``RETAGGED`` does **not**: the audio is provably the
    same, so the identification still holds and only the stamp needed refreshing.
    """
    touched_albums: set[str] = set()
    replaced_albums: set[str] = set()
    stamped_at = _now()

    async with session_factory() as session:
        rows = await session.execute(
            select(Track).where(Track.id.in_(list(verdicts)))
        )
        for track in rows.scalars().all():
            state, current = verdicts.get(str(track.id), (IntegrityState.MISSING, None))
            if current is None:
                continue
            if state is IntegrityState.UNKNOWN:
                report.baselined += 1
            track.content_hash = current.content_hash
            track.sample_count = current.sample_count
            track.file_size = current.size
            track.file_mtime = current.mtime
            track.verified_at = stamped_at
            touched_albums.add(str(track.album_id))
            if state is IntegrityState.REPLACED:
                replaced_albums.add(str(track.album_id))

        report.albums_restamped += await _restamp_albums(session, touched_albums)
        report.albums_reopened += await _reopen_replaced(session, replaced_albums)


async def _restamp_albums(session: AsyncSession, album_ids: Iterable[str]) -> int:
    """Recompute ``Album.content_digest`` for every release a batch touched.

    The ordering key here has to stay identical to the one
    :meth:`app.core.scanner.LibraryScanner._stamp_release` uses, because both
    write the same column: if they disagree about the order the member hashes go
    in, the digest flips every time the other one runs and the release reports as
    altered forever. The digest function itself is shared —
    :func:`app.core.integrity.album_content_digest` — so what is duplicated is two
    lines of sort key, and this note.

    Returns how many releases ended up with a different digest. Unchanged ones are
    not written, so a nightly pass over a library nobody touched issues no writes
    here at all.
    """
    changed = 0
    for album_id in sorted(album_ids):
        album = await session.get(Album, album_id)
        if album is None:
            continue
        ordered = sorted(
            album.tracks,
            key=lambda track: (track.media_number or 1, track.track_number or 0, track.id),
        )
        digest = album_content_digest([track.content_hash for track in ordered])
        if digest != album.content_digest:
            album.content_digest = digest
            changed += 1
    return changed


async def _reopen_replaced(session: AsyncSession, album_ids: set[str]) -> int:
    """Put releases whose audio changed back on the enrichment work list.

    :func:`app.core.enricher.reopen_library_albums` rather than
    ``mark_library_due``, and the difference is the whole point of this call. A
    release that has already been identified holds ``ok`` state rows, and
    ``mark_due`` moves only ``pending``/``not_found``/``failed`` ones — so the
    obvious call does nothing here, on precisely the albums this pass exists to
    catch, while this function reported having queued them. Re-opening is what
    the review page's "run now" does, and this is the same act: the recording
    ids, the release match and the fingerprint verdicts were all made about bytes
    that no longer exist.

    Runs inside the caller's transaction: it is SQL, it makes no request, it
    drops ids that are not in the library scope, and it enqueues nothing for
    download. Failure is swallowed — the same album is reached from its status on
    a later ``_seed``, so losing this delays the re-identification rather than
    preventing it. The count is what was really re-opened, never the size of the
    request, so the activity row cannot overstate it.
    """
    if not album_ids:
        return 0
    from app.core.enricher import reopen_library_albums  # noqa: PLC0415 - import cycle

    try:
        return await reopen_library_albums(session, sorted(album_ids))
    except Exception:  # noqa: BLE001 - a side table must not fail the measurement
        logger.exception("Could not re-open %d replaced release(s)", len(album_ids))
        return 0


def _batched(items: Sequence[_Candidate], size: int) -> Iterable[Sequence[_Candidate]]:
    """Yield *items* in chunks of *size*."""
    for start in range(0, len(items), max(1, size)):
        yield items[start : start + size]


async def _reverify_files(
    session_factory: SessionFactory, summary: dict[str, Any]
) -> None:
    """Re-hash a rotating slice of the library, as part of the nightly job.

    Runs **after** the housekeeping session has committed, not inside it: see
    :func:`verify_integrity` for why nothing may hold SQLite's write lock across
    minutes of hashing. It is also the last step for the same reason every other
    step is ordered as it is — adopt what appeared, flag what disappeared, purge
    what fell out of scope, and only then spend the night's disk budget on what is
    still there.

    Two things switch it off:

    * ``INTEGRITY_ENABLED=false``, which is the setting that means *never*.
    * **A verify pass that demoted something**, exactly like the enrichment purge
      standing down in :func:`_prune_enrichment` and for the same reason: albums
      flipped back to ``WANTED`` is what an unmounted library looks like, and
      hashing across a mount that is flapping spends the whole budget producing
      ``MISSING`` verdicts about files that are fine. Nothing is lost by waiting a
      night — the rotation is idempotent and whatever went unmeasured sorts to the
      front of the next run.

    The slice is ``INTEGRITY_REVERIFY_FRACTION`` of the tracks that have a file,
    rounded up so a small library still makes progress, and capped at
    :data:`INTEGRITY_MAX_FILES`.
    """
    settings = effective_for(get_settings())
    if not settings.integrity_enabled:
        return

    demoted = int(summary.get("albums_flagged") or 0)
    if demoted:
        summary["integrity_skipped"] = demoted
        logger.warning(
            "Skipping the nightly re-verification: the verify pass demoted %d "
            "album(s) this run, which an unreachable library is indistinguishable "
            "from. The rotation resumes on the next pass that demotes nothing.",
            demoted,
        )
        return

    async with session_factory() as session:
        total = int(
            await session.scalar(
                select(func.count())
                .select_from(Track)
                .where(Track.path.is_not(None), Track.status == TrackStatus.DOWNLOADED)
            )
            or 0
        )
    if not total:
        return

    share = min(
        INTEGRITY_MAX_FILES,
        max(1, math.ceil(total * float(settings.integrity_reverify_fraction))),
    )

    try:
        report = await verify_integrity(session_factory, limit=share)
    except Exception:  # noqa: BLE001 - housekeeping must finish regardless
        logger.exception("Nightly re-verification failed")
        return

    summary["integrity"] = report.as_dict()
    summary["files_verified"] = report.checked
    summary["files_changed"] = report.changed

    # Stored whether or not anything changed. "The last pass found nothing" is
    # the answer the Integrity screen needs most nights, and a screen that only
    # ever showed the last *eventful* run would keep claiming a month-old
    # disturbance is the current state of the library.
    try:
        async with session_factory() as session:
            await store_last_integrity(session, report)
    except Exception:  # noqa: BLE001 - a summary row must not fail the job
        logger.exception("Could not store the integrity summary")

    if not report.changed:
        return

    async with session_factory() as session:
        session.add(
            Activity(
                level=ActivityLevel.WARNING,
                event="library.integrity",
                message=(
                    f"Re-verified {report.checked} file(s): "
                    f"{report.count(IntegrityState.RETAGGED)} re-tagged, "
                    f"{report.count(IntegrityState.REPLACED)} replaced. "
                    f"{report.albums_reopened} release(s) queued for re-identification"
                ),
            )
        )


def build_scheduler(
    indexer: Indexer,
    *,
    settings: Settings | None = None,
    session_factory: SessionFactory = session_scope,
    housekeeping_job: Callable[[], Any] | None = None,
    enricher: Any = None,
    identify_by_audio: Any = None,
    register: bool = True,
) -> AsyncIOScheduler:
    """Create (but do not start) the application scheduler.

    Args:
        indexer: The indexer whose :meth:`~app.core.indexer.Indexer.tick` is
            scheduled.
        settings: Effective settings; defaults to :func:`app.config.get_settings`.
        session_factory: Session context-manager factory for the jobs.
        housekeeping_job: Override for the nightly job (tests).
        enricher: The :class:`~app.core.enricher.Enricher` to drain, when there
            is one. Omitted, no enrichment job is added at all.
        identify_by_audio: :func:`app.core.discovery.identifier_for`'s result —
            the audio chain bound to the ladder's own rungs, so the nightly
            binding pass shares one rate limiter per upstream with everything
            else. ``None`` (AcoustID or MusicBrainz switched off) adds no job,
            which is the correct behaviour rather than a degraded one.
        register: Store the scheduler module-side so :func:`get_scheduler` and
            :func:`trigger_tick_now` can find it.

    The indexer job is added even when ``INDEXER_ENABLED`` is false, but paused,
    so the UI can still show it and resume it without a restart.
    """
    settings = effective_for(settings or get_settings())
    scheduler = AsyncIOScheduler(timezone="UTC")

    interval = max(1, int(settings.indexer_artist_interval))
    jitter = int(interval * min(max(settings.indexer_jitter_pct, 0.0), 1.0))

    scheduler.add_job(
        run_indexer_tick,
        trigger=IntervalTrigger(seconds=interval, jitter=jitter or None),
        id=JOB_INDEXER_TICK,
        name="Indexer tick (one artist)",
        args=(indexer, session_factory, enricher),
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

    enrich_interval = 0
    if enricher is not None and settings.enrichment_enabled:
        # Paced very differently from the indexer, and deliberately so: that one
        # is slow because Qobuz is a paid account that can be banned, whereas
        # these are public APIs with published allowances. The real throttle is
        # the per-source rate limiter, not this interval.
        enrich_interval = max(30, int(settings.enrichment_interval))
        scheduler.add_job(
            run_enrichment_tick,
            trigger=IntervalTrigger(seconds=enrich_interval),
            id=JOB_ENRICHMENT,
            name="Enrichment tick (metadata batch)",
            args=(enricher,),
            max_instances=1,
            coalesce=True,
            misfire_grace_time=enrich_interval,
            replace_existing=True,
            next_run_time=_now() + timedelta(seconds=min(enrich_interval, 60)),
        )

    if identify_by_audio is not None and settings.folder_binding_enabled:
        # An hour after housekeeping, so the disk scan it runs has already
        # adopted whatever matched by name and the unmatched list this works
        # from is the smallest it will be. Daily rather than on an interval:
        # the population changes when folders appear on disk or the catalogue
        # moves, both of which are slow, and each folder costs a fingerprint of
        # every file in it.
        scheduler.add_job(
            run_binder_tick,
            trigger=CronTrigger(
                hour=(HOUSEKEEPING_HOUR + 1) % 24,
                minute=HOUSEKEEPING_MINUTE,
                timezone="UTC",
            ),
            id=JOB_FOLDER_BINDING,
            name="Folder binding (identify unmatched folders by audio)",
            args=(identify_by_audio, session_factory, settings),
            max_instances=1,
            coalesce=True,
            misfire_grace_time=3600,
            replace_existing=True,
        )

    if register:
        set_scheduler(scheduler, indexer=indexer, session_factory=session_factory)

    logger.info(
        "Scheduler built: indexer tick every %ds (+/-%ds jitter), housekeeping at "
        "%02d:%02d UTC, enrichment %s",
        interval,
        jitter,
        HOUSEKEEPING_HOUR,
        HOUSEKEEPING_MINUTE,
        f"every {enrich_interval}s" if enrich_interval else "disabled",
    )
    return scheduler


async def start_background(
    indexer: Indexer,
    *,
    settings: Settings | None = None,
    session_factory: SessionFactory = session_scope,
    scheduler: AsyncIOScheduler | None = None,
    enricher: Any = None,
) -> AsyncIOScheduler:
    """Build (if needed) and start the scheduler on the running event loop.

    When ``INDEXER_ENABLED`` is false the tick job is paused immediately, so
    only housekeeping runs.
    """
    settings = effective_for(settings or get_settings())
    sched = scheduler or build_scheduler(
        indexer, settings=settings, session_factory=session_factory, enricher=enricher
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
