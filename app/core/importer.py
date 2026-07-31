"""Bulk import: turn artists found on disk into followed artists.

The disk scan (:mod:`app.core.scanner`) tells you which artists are in the
library folder but not in the database.  On a library of any size that list is
hundreds of names long, and clicking *Find on Qobuz* five hundred times is not a
workflow.  This module does it in one pass: one Qobuz search per name, follow
the ones that match unambiguously, and hand back everything else as a short
review list.

What it costs
-------------
**One API call per artist.**  A ``catalog/search`` hit already carries the id,
name, artwork and album count, so the artist row is written straight from the
search result and ``artist/get`` is never called.  At the default
``QOBUZ_MIN_REQUEST_INTERVAL`` of 2s that is about 8 minutes for 500 artists,
and it goes through the same global limiter as everything else — an import
cannot outrun the budget or the circuit breaker.

Matching
--------
An artist is followed only when a search hit's name matches the folder name
*exactly* after :func:`app.core.scanner.artist_key` normalisation (accents,
case, punctuation and a leading article folded).  Qobuz returns
``Joanne Shaw Taylor`` and ``Joanna Shaw Taylor`` for the same query; one of
those is not the artist on disk, and no amount of fuzzy scoring makes guessing
between them a good idea when the consequence is monitoring the wrong
discography.  Everything without an exact hit goes to the review list with its
top candidates, one click each.

Indexing is deliberately *not* triggered
----------------------------------------
Following 500 artists with ``AUTO_INDEX_ON_FOLLOW`` semantics would fire 500
back-catalogue imports at once — tens of thousands of API calls.  The importer
follows with ``index_now=False`` and spawns nothing; the scheduled indexer tick
picks the new artists up one at a time at its configured pace, which is the
whole point of the one-artist-per-tick design.  ``index_now=True`` is available
for callers who really want the catalogue now and understand the cost.

Nothing here downloads.  Newly followed artists get their releases marked
``wanted`` by the indexer in the usual way, and ``AUTO_DOWNLOAD`` still governs
whether anything is ever queued.
"""

from __future__ import annotations

import asyncio
import json
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Mapping, Sequence

from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, get_settings
from app.core.indexer import Indexer
from app.core.queue import SessionFactory
from app.core.scanner import LibraryScanner, artist_key
from app.db import session_scope
from app.logging_conf import get_logger
from app.models import Activity, ActivityLevel, Artist, MonitorMode, Setting, utcnow
from app.qobuz.client import QobuzClient
from app.qobuz.errors import QobuzError
from app.qobuz.mapper import map_artist

__all__ = [
    "CONSECUTIVE_FAILURE_LIMIT",
    "LAST_IMPORT_KEY",
    "MAX_REVIEWED",
    "ImportProgress",
    "LibraryImporter",
    "load_last_import",
]

logger = get_logger(__name__)

#: ``Setting`` key holding the JSON summary of the most recent import.
LAST_IMPORT_KEY = "library.last_import"

#: How many artists the review list carries before it starts counting drops.
MAX_REVIEWED = 500

#: Consecutive Qobuz failures that abort the run. A tripped circuit breaker or
#: an expired token fails *every* search; without this the importer would spend
#: an hour failing five hundred times in a row and log five hundred errors.
CONSECUTIVE_FAILURE_LIMIT = 5

#: Search hits kept per unresolved name, for the review list.
CANDIDATES_PER_NAME = 3


def _now() -> datetime:
    """Timezone-aware current time."""
    return datetime.now(timezone.utc)


@dataclass(slots=True)
class ImportProgress:
    """Live state of an import run, polled by the UI and returned by the API."""

    running: bool = False
    cancelled: bool = False
    started_at: datetime | None = None
    finished_at: datetime | None = None
    total: int = 0
    processed: int = 0
    followed: int = 0
    already_followed: int = 0
    needs_review: int = 0
    not_found: int = 0
    failed: int = 0
    current: str = ""
    index_now: bool = False
    review: list[dict[str, Any]] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)
    truncated_review: int = 0
    aborted_reason: str = ""

    @property
    def remaining(self) -> int:
        """Names still to search."""
        return max(0, self.total - self.processed)

    @property
    def percent(self) -> int:
        """Whole-percent completion, 0 when the total is unknown."""
        if not self.total:
            return 0
        return int(100 * self.processed / self.total)

    def summary(self) -> str:
        """One-line human summary for toasts, the activity feed and the CLI."""
        parts = [f"{self.processed} of {self.total} searched", f"{self.followed} followed"]
        if self.already_followed:
            parts.append(f"{self.already_followed} already followed")
        if self.needs_review:
            parts.append(f"{self.needs_review} need a decision")
        if self.not_found:
            parts.append(f"{self.not_found} not on Qobuz")
        if self.failed:
            parts.append(f"{self.failed} failed")
        if self.cancelled:
            parts.append("cancelled")
        if self.aborted_reason:
            parts.append(self.aborted_reason)
        return ", ".join(parts)

    def as_dict(self, *, seconds_per_call: float = 0.0) -> dict[str, Any]:
        """JSON-safe dict for the API, the stored ``Setting`` row and templates."""
        return {
            "running": self.running,
            "cancelled": self.cancelled,
            "started_at": self.started_at.isoformat() if self.started_at else None,
            "finished_at": self.finished_at.isoformat() if self.finished_at else None,
            "total": self.total,
            "processed": self.processed,
            "remaining": self.remaining,
            "percent": self.percent,
            "followed": self.followed,
            "already_followed": self.already_followed,
            "needs_review": self.needs_review,
            "not_found": self.not_found,
            "failed": self.failed,
            "current": self.current,
            "index_now": self.index_now,
            "review": list(self.review),
            "truncated_review": self.truncated_review,
            "errors": list(self.errors),
            "aborted_reason": self.aborted_reason,
            "eta_seconds": round(self.remaining * max(0.0, seconds_per_call), 1),
            "summary": self.summary(),
        }


async def load_last_import(session: AsyncSession) -> dict[str, Any] | None:
    """The stored summary of the most recent import, or ``None``."""
    row = await session.get(Setting, LAST_IMPORT_KEY)
    if row is None or not row.value:
        return None
    try:
        data = json.loads(row.value)
    except (TypeError, ValueError):
        logger.warning("Stored library import summary is not valid JSON; ignoring it")
        return None
    return data if isinstance(data, dict) else None


class LibraryImporter:
    """Searches Qobuz for artists found on disk and follows the certain ones.

    One instance lives on :class:`~app.core.state.AppState`.  Only one run may
    be in flight at a time: the work is inherently sequential (it is pacing
    itself against a global budget), and two runs would double the request rate
    the limiter is there to hold down.
    """

    def __init__(
        self,
        client: QobuzClient,
        indexer: Indexer,
        scanner: LibraryScanner,
        *,
        settings: Settings | None = None,
        session_factory: SessionFactory = session_scope,
    ) -> None:
        self._client = client
        self._indexer = indexer
        self._scanner = scanner
        self._settings = settings or get_settings()
        self._session_factory = session_factory
        self._progress = ImportProgress()
        self._task: asyncio.Task[None] | None = None
        self._cancel = asyncio.Event()
        self._lock = asyncio.Lock()

    # ------------------------------------------------------------- reporting
    @property
    def running(self) -> bool:
        """True while a run is in flight."""
        return self._progress.running

    def snapshot(self) -> dict[str, Any]:
        """Current progress, shaped for :class:`app.schemas.LibraryImportOut`."""
        return self._progress.as_dict(
            seconds_per_call=float(self._settings.qobuz_min_request_interval)
        )

    def estimate_seconds(self, count: int) -> float:
        """How long *count* searches will take at the configured minimum interval.

        A floor, not a promise: the hourly cap and the circuit breaker can both
        make a run take longer, never shorter.
        """
        return max(0, count) * float(self._settings.qobuz_min_request_interval)

    # ---------------------------------------------------------------- control
    async def preview(self, session: AsyncSession) -> list[dict[str, Any]]:
        """The artists a run would search for, without touching Qobuz.

        This is a dry disk scan (``apply=False``), so it neither adopts albums
        nor writes a scan summary — it only answers "who is on disk that we do
        not follow?".
        """
        result = await self._scanner.scan(session, apply=False, record=False)
        return list(result.unknown_artists)

    async def start(
        self,
        *,
        names: Sequence[str] | None = None,
        monitored: bool = True,
        monitor_mode: MonitorMode | str | None = None,
        quality_profile: str | None = None,
        release_types: Sequence[str] | None = None,
        index_now: bool = False,
        limit: int | None = None,
    ) -> dict[str, Any]:
        """Begin an import in the background and return the initial snapshot.

        Args:
            names: Artist names to look up. When omitted the importer runs a
                disk scan first and uses every unknown artist it finds, which is
                the normal path from the Disk scan page.
            monitored: Whether the new artists are monitored (default yes).
            monitor_mode: Monitor mode for the new artists; defaults to
                ``DEFAULT_MONITOR_MODE``.
            quality_profile: Quality profile; defaults to the configured one.
            release_types: Accepted release types; defaults to the configured
                ones.
            index_now: Import each artist's back catalogue as it is followed.
                Off by default — see the module docstring.
            limit: Stop after this many names. Useful for trying the matcher on
                a handful before committing to a long run.

        Returns:
            The progress snapshot as of the moment the task was spawned.

        Raises:
            RuntimeError: when a run is already in flight.
        """
        async with self._lock:
            if self._progress.running:
                raise RuntimeError("An artist import is already running.")

            async with self._session_factory() as session:
                candidates = (
                    [{"name": name} for name in names]
                    if names is not None
                    else await self.preview(session)
                )

            if limit is not None and limit > 0:
                candidates = candidates[: int(limit)]

            self._cancel.clear()
            self._progress = ImportProgress(
                running=True,
                started_at=_now(),
                total=len(candidates),
                index_now=bool(index_now),
            )
            self._task = asyncio.create_task(
                self._run(
                    candidates,
                    monitored=monitored,
                    monitor_mode=monitor_mode,
                    quality_profile=quality_profile,
                    release_types=release_types,
                    index_now=index_now,
                ),
                name="library-import",
            )
            return self.snapshot()

    async def cancel(self) -> bool:
        """Ask the running import to stop after the current artist.

        Returns ``False`` when nothing was running. The in-flight search is
        allowed to finish so the artist is either fully followed or not touched
        at all — there is no half-followed state to clean up.
        """
        if not self._progress.running:
            return False
        self._cancel.set()
        logger.info("Artist import cancellation requested")
        return True

    async def wait(self) -> None:
        """Await the running task. Test/CLI helper; the UI polls instead."""
        task = self._task
        if task is not None:
            await asyncio.shield(asyncio.gather(task, return_exceptions=True))

    # -------------------------------------------------------------- the work
    async def _run(
        self,
        candidates: Sequence[Mapping[str, Any]],
        *,
        monitored: bool,
        monitor_mode: MonitorMode | str | None,
        quality_profile: str | None,
        release_types: Sequence[str] | None,
        index_now: bool,
    ) -> None:
        """Search for and follow each candidate, one at a time."""
        progress = self._progress
        started = time.monotonic()
        consecutive_failures = 0

        try:
            for candidate in candidates:
                if self._cancel.is_set():
                    progress.cancelled = True
                    break

                name = str(candidate.get("name") or "").strip()
                progress.current = name
                if not name:
                    progress.processed += 1
                    continue

                try:
                    outcome = await self._import_one(
                        candidate,
                        monitored=monitored,
                        monitor_mode=monitor_mode,
                        quality_profile=quality_profile,
                        release_types=release_types,
                        index_now=index_now,
                    )
                    consecutive_failures = 0
                except QobuzError as exc:
                    consecutive_failures += 1
                    progress.failed += 1
                    self._record_error(f"{name}: {exc}")
                    logger.warning("Import of %r failed: %s", name, exc)
                    if consecutive_failures >= CONSECUTIVE_FAILURE_LIMIT:
                        progress.aborted_reason = (
                            f"stopped after {consecutive_failures} consecutive Qobuz "
                            "failures — check the rate limiter and credentials"
                        )
                        logger.error("Artist import aborted: %s", progress.aborted_reason)
                        progress.processed += 1
                        break
                except asyncio.CancelledError:
                    progress.cancelled = True
                    raise
                except Exception as exc:  # noqa: BLE001 - one bad name must not stop the run
                    consecutive_failures += 1
                    progress.failed += 1
                    self._record_error(f"{name}: {exc}")
                    logger.exception("Unexpected error importing %r: %s", name, exc)
                else:
                    if outcome == "followed":
                        progress.followed += 1
                    elif outcome == "already":
                        progress.already_followed += 1
                    elif outcome == "review":
                        progress.needs_review += 1
                    elif outcome == "missing":
                        progress.not_found += 1

                progress.processed += 1
        except asyncio.CancelledError:
            progress.cancelled = True
            raise
        finally:
            progress.running = False
            progress.current = ""
            progress.finished_at = _now()
            await self._finish(progress, time.monotonic() - started)

    async def _import_one(
        self,
        candidate: Mapping[str, Any],
        *,
        monitored: bool,
        monitor_mode: MonitorMode | str | None,
        quality_profile: str | None,
        release_types: Sequence[str] | None,
        index_now: bool,
    ) -> str:
        """Search for one name and follow it when the match is unambiguous.

        Returns one of ``"followed"``, ``"already"``, ``"review"``, ``"missing"``.
        """
        name = str(candidate["name"]).strip()
        key = artist_key(name)

        hits = await self._client.search_artists(name, limit=10)
        mapped = [map_artist(hit) for hit in hits]
        usable = [(hit, row) for hit, row in zip(hits, mapped) if row.get("id")]

        if not usable:
            self._record_review(candidate, [], reason="not-found")
            return "missing"

        exact = [(hit, row) for hit, row in usable if artist_key(row["name"]) == key]
        if not exact:
            self._record_review(
                candidate,
                [row for _, row in usable[:CANDIDATES_PER_NAME]],
                reason="no-exact-match",
            )
            return "review"

        # Several Qobuz entries can share a name (duplicates, or a reissue
        # label). The one with the most releases is the real discography.
        exact.sort(key=lambda pair: -int(pair[1].get("albums_count") or 0))
        payload, row = exact[0]
        artist_id = str(row["id"])

        async with self._session_factory() as session:
            existing = await session.get(Artist, artist_id)
            if existing is not None:
                # Already followed under a different folder spelling; nothing to
                # do, and definitely nothing to re-index.
                return "already"

            await self._indexer.add_artist(
                session,
                artist_id,
                monitored,
                name=row["name"],
                monitor_mode=monitor_mode,
                quality_profile=quality_profile,
                accepted_release_types=release_types,
                index_now=index_now,
                payload=payload,
            )
        logger.info("Imported %s (%s) from the library folder", row["name"], artist_id)
        return "followed"

    # ------------------------------------------------------------- bookkeeping
    def _record_review(
        self,
        candidate: Mapping[str, Any],
        options: Sequence[Mapping[str, Any]],
        *,
        reason: str,
    ) -> None:
        """Add one unresolved name to the review list, capped."""
        progress = self._progress
        if len(progress.review) >= MAX_REVIEWED:
            progress.truncated_review += 1
            return
        progress.review.append(
            {
                "name": str(candidate.get("name") or ""),
                "albums": int(candidate.get("albums") or 0),
                "files": int(candidate.get("files") or 0),
                "path": str(candidate.get("path") or ""),
                "reason": reason,
                "candidates": [
                    {
                        "id": str(option.get("id") or ""),
                        "name": str(option.get("name") or ""),
                        "albums_count": int(option.get("albums_count") or 0),
                        "image_url": option.get("image_url"),
                    }
                    for option in options
                ],
            }
        )

    def _record_error(self, message: str) -> None:
        """Append an error, capped the same way the review list is."""
        if len(self._progress.errors) < MAX_REVIEWED:
            self._progress.errors.append(message)

    async def _finish(self, progress: ImportProgress, elapsed: float) -> None:
        """Store the final summary and write one activity row."""
        try:
            async with self._session_factory() as session:
                payload = json.dumps(
                    progress.as_dict(
                        seconds_per_call=float(self._settings.qobuz_min_request_interval)
                    ),
                    default=str,
                )
                row = await session.get(Setting, LAST_IMPORT_KEY)
                if row is None:
                    session.add(Setting(key=LAST_IMPORT_KEY, value=payload))
                else:
                    row.value = payload
                    row.updated_at = utcnow()

                level = ActivityLevel.INFO
                if progress.failed or progress.aborted_reason:
                    level = ActivityLevel.WARNING
                session.add(
                    Activity(
                        level=level,
                        event="library.import",
                        message=f"Artist import: {progress.summary()}",
                    )
                )
        except Exception as exc:  # noqa: BLE001 - never let bookkeeping mask the run
            logger.exception("Could not record the artist import summary: %s", exc)

        logger.info("Artist import finished in %.0fs: %s", elapsed, progress.summary())
