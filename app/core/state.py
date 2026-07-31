"""Process-wide application state — the integration seam for the API layer.

Everything long-lived lives on a single :class:`AppState` instance: the shared
rate limiter, the Qobuz client, the indexer, the download-queue worker and the
scheduler.  The FastAPI lifespan calls :func:`init_state` on startup and
:func:`shutdown_state` on shutdown; routers reach the machinery with::

    from app.core.state import get_state

    state = get_state()
    results = await state.indexer.check_artist(session, artist_id)
    await state.queue.enqueue_album(session, album_id)
    snapshot = await state.status(session)          # StatusOut-shaped dict

Design rules worth preserving:

* **One rate limiter.** It is built here with
  :meth:`~app.qobuz.ratelimit.RateLimiter.from_settings` and handed to the
  Qobuz client, so the indexer, the downloader and UI searches all share one
  global budget.
* **Startup never explodes.** Missing credentials, an unreachable Qobuz or a
  not-yet-written downloader module degrade to a warning plus a flag on the
  state (``credentials_ok`` / ``app_secret_ok``); the web UI still boots so the
  user can see what is wrong.
* **Nothing here imports the API layer**, so ``app.core`` stays importable from
  scripts and tests.
"""

from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any

from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app import __version__
from app.config import Settings, get_settings
from app.core import scheduler as scheduler_module
from app.core.importer import LibraryImporter, load_last_import
from app.core.indexer import IndexResult, Indexer
from app.core.queue import QueueWorker, SessionFactory, resolve_download_callable
from app.core.scanner import LibraryScanner, ScanResult, load_last_scan
from app.db import init_db, session_scope
from app.logging_conf import get_logger
from app.models import Album, AlbumStatus, Artist, Track, TrackStatus
from app.qobuz.client import QobuzClient
from app.qobuz.errors import QobuzError
from app.qobuz.ratelimit import RateLimiter

__all__ = [
    "AppState",
    "get_state",
    "init_state",
    "shutdown_state",
    "state_or_none",
]

logger = get_logger(__name__)

_state: "AppState | None" = None
_lock = asyncio.Lock()


def _now() -> datetime:
    """Timezone-aware current time."""
    return datetime.now(timezone.utc)


@dataclass(slots=True)
class AppState:
    """Container for every long-lived object in the running application."""

    settings: Settings
    limiter: RateLimiter
    client: QobuzClient
    indexer: Indexer
    queue: QueueWorker
    scanner: LibraryScanner
    importer: LibraryImporter
    session_factory: SessionFactory = session_scope
    scheduler: Any = None
    started_at: datetime = field(default_factory=_now)
    credentials_ok: bool = False
    app_secret_ok: bool = False
    startup_errors: list[str] = field(default_factory=list)

    # ------------------------------------------------------------- lifecycle
    @property
    def uptime_seconds(self) -> float:
        """Seconds since :func:`init_state` ran."""
        return max(0.0, (_now() - self.started_at).total_seconds())

    async def aclose(self) -> None:
        """Stop the scheduler, importer and worker, then close the HTTP clients."""
        await scheduler_module.shutdown(wait=False)
        self.scheduler = None
        await self.importer.cancel()
        await self.queue.stop()
        await self.client.aclose()

    # ----------------------------------------------------------- reporting
    def rate_limit_status(self) -> dict[str, Any]:
        """Limiter stats, keyed for :class:`app.schemas.RateLimitStatusOut`."""
        stats = self.limiter.stats()
        return {
            "min_request_interval": stats["min_interval"],
            "max_requests_per_hour": stats["hourly_cap"],
            "requests_last_hour": stats["requests_last_hour"],
            "budget_remaining": stats["budget_remaining"],
            "budget_used_percent": stats["budget_used_percent"],
            "seconds_until_next_slot": stats["next_slot_in_seconds"],
            "last_request_at": stats["last_request_at"],
            "circuit_open": stats["circuit_open"],
            "circuit_open_until": stats["circuit_open_until"],
            "recent_429s": stats["recent_429s"],
        }

    async def indexer_status(self, session: AsyncSession) -> dict[str, Any]:
        """Indexer snapshot for :class:`app.schemas.IndexerStatusOut`.

        The scheduler's next fire time wins over the indexer's own estimate when
        it is later, because a due artist still has to wait for the next tick.
        """
        data = await self.indexer.status(session)
        job_next = scheduler_module.next_run_time(scheduler_module.JOB_INDEXER_TICK)
        if job_next is not None:
            due = data.get("next_run_at")
            data["next_run_at"] = max(due, job_next) if due else job_next
            data["seconds_until_next_run"] = max(
                0.0, (data["next_run_at"] - _now()).total_seconds()
            )
        return data

    async def queue_status(self, session: AsyncSession) -> dict[str, Any]:
        """Queue snapshot for :class:`app.schemas.QueueStatsOut`."""
        return await self.queue.stats(session)

    async def library_status(self, session: AsyncSession) -> dict[str, Any]:
        """Headline library counts for :class:`app.schemas.LibraryStatsOut`."""

        async def count(statement: Any) -> int:
            return int((await session.execute(statement)).scalar_one() or 0)

        albums_by_status = await session.execute(
            select(Album.status, func.count()).group_by(Album.status)
        )
        buckets: dict[str, int] = {}
        for status, total in albums_by_status.all():
            key = status.value if isinstance(status, AlbumStatus) else str(status)
            buckets[key] = int(total)

        return {
            "artists": await count(select(func.count()).select_from(Artist)),
            "monitored_artists": await count(
                select(func.count()).select_from(Artist).where(Artist.monitored.is_(True))
            ),
            "albums": sum(buckets.values()),
            "wanted_albums": buckets.get(AlbumStatus.WANTED.value, 0)
            + buckets.get(AlbumStatus.QUEUED.value, 0),
            "downloaded_albums": buckets.get(AlbumStatus.DOWNLOADED.value, 0),
            "failed_albums": buckets.get(AlbumStatus.FAILED.value, 0),
            "tracks": await count(select(func.count()).select_from(Track)),
            "downloaded_tracks": await count(
                select(func.count())
                .select_from(Track)
                .where(Track.status == TrackStatus.DOWNLOADED)
            ),
        }

    async def status(self, session: AsyncSession) -> dict[str, Any]:
        """Everything the dashboard needs, shaped like :class:`app.schemas.StatusOut`
        minus ``recent_activity`` (which the API layer queries itself)."""
        return {
            "version": __version__,
            "started_at": self.started_at,
            "uptime_seconds": self.uptime_seconds,
            "library": await self.library_status(session),
            "queue": await self.queue_status(session),
            "indexer": await self.indexer_status(session),
            "rate_limit": self.rate_limit_status(),
            "credentials_ok": self.credentials_ok,
            "app_secret_ok": self.app_secret_ok,
            "library_path": str(self.settings.library_path),
        }

    async def scan_status(self, session: AsyncSession) -> dict[str, Any]:
        """Disk-scan snapshot: whether one is running plus the last summary."""
        return {
            "running": self.scanner.running,
            "library_path": str(self.settings.library_path),
            "nightly": self.settings.library_scan_nightly,
            "last": await load_last_scan(session),
        }

    async def import_status(self, session: AsyncSession) -> dict[str, Any]:
        """Artist-import snapshot: live progress, falling back to the last run."""
        snapshot = self.importer.snapshot()
        if not snapshot["running"] and snapshot["started_at"] is None:
            stored = await load_last_import(session)
            if stored is not None:
                return stored
        return snapshot

    # -------------------------------------------------------------- actions
    async def scan_now(self) -> IndexResult | None:
        """Trigger a single manual indexer tick (see the scheduler module)."""
        return await scheduler_module.trigger_tick_now(self.indexer)

    async def scan_library(
        self, session: AsyncSession, *, artist_id: str | None = None, apply: bool = True
    ) -> ScanResult:
        """Run a disk scan through the shared :class:`LibraryScanner`.

        Purely local: it reads the library folder and the database and makes no
        Qobuz API call, so it does not touch the rate limiter at all.
        """
        return await self.scanner.scan(session, artist_id=artist_id, apply=apply)


async def init_state(
    *,
    settings: Settings | None = None,
    session_factory: SessionFactory = session_scope,
    initialise_database: bool = True,
    start_scheduler: bool = True,
    start_queue: bool = True,
    login: bool = True,
) -> AppState:
    """Build (or return) the process-wide :class:`AppState`.

    Args:
        settings: Override settings (tests).
        session_factory: Session context-manager factory used by the workers.
        initialise_database: Run :func:`app.db.init_db` first.
        start_scheduler: Start the APScheduler jobs.
        start_queue: Start the download-queue worker task.
        login: Validate the Qobuz credentials and pre-resolve the app secret.

    Failures in the Qobuz handshake are recorded on the returned state rather
    than raised, so the web UI always comes up.
    """
    global _state

    async with _lock:
        if _state is not None:
            return _state

        settings = settings or get_settings()
        if initialise_database:
            await init_db()

        limiter = RateLimiter.from_settings(settings)
        client = QobuzClient(settings, limiter=limiter)
        indexer = Indexer(client, settings=settings)

        download_album = None
        try:
            download_album = resolve_download_callable(client=client, settings=settings)
        except RuntimeError as exc:
            logger.error("Downloader unavailable: %s", exc)

        queue = QueueWorker(
            download_album=download_album,
            session_factory=session_factory,
            settings=settings,
            client=client,
        )

        scanner = LibraryScanner(settings=settings)
        state = AppState(
            settings=settings,
            limiter=limiter,
            client=client,
            indexer=indexer,
            queue=queue,
            scanner=scanner,
            importer=LibraryImporter(
                client,
                indexer,
                scanner,
                settings=settings,
                session_factory=session_factory,
            ),
            session_factory=session_factory,
        )

        if login and settings.has_credentials:
            await _handshake(state)
        elif login:
            message = (
                "QOBUZ_APP_ID / QOBUZ_USER_AUTH_TOKEN are not set; "
                "Qobuzarr will start read-only"
            )
            logger.warning(message)
            state.startup_errors.append(message)

        if start_queue:
            await queue.start()

        if start_scheduler:
            state.scheduler = await scheduler_module.start_background(
                indexer, settings=settings, session_factory=session_factory
            )
        else:
            scheduler_module.set_scheduler(None, indexer=indexer, session_factory=session_factory)

        _state = state
        logger.info(
            "Application state ready (version %s, credentials_ok=%s, app_secret_ok=%s)",
            __version__,
            state.credentials_ok,
            state.app_secret_ok,
        )
        return state


async def _handshake(state: AppState) -> None:
    """Log in and pre-resolve the app secret, recording failures on *state*."""
    try:
        user = await state.client.login()
        state.credentials_ok = True
        logger.info(
            "Signed in to Qobuz (country=%s, formats=%s)",
            user.get("country_code", "?"),
            state.client.allowed_format_ids(),
        )
    except QobuzError as exc:
        state.startup_errors.append(f"Qobuz login failed: {exc}")
        logger.error("Qobuz login failed: %s", exc)
        return

    try:
        await state.client.resolve_app_secret()
        state.app_secret_ok = state.client.has_app_secret
    except QobuzError as exc:
        state.startup_errors.append(f"Could not resolve the Qobuz app secret: {exc}")
        logger.error(
            "Could not resolve the Qobuz app secret (downloads will fail): %s", exc
        )


async def shutdown_state() -> None:
    """Tear down the process-wide state. Safe to call when never initialised."""
    global _state

    async with _lock:
        state = _state
        _state = None

    if state is None:
        await scheduler_module.shutdown(wait=False)
        return

    try:
        await state.aclose()
    except Exception as exc:  # noqa: BLE001 - shutdown must not raise
        logger.warning("Error while shutting down application state: %s", exc)
    logger.info("Application state shut down")


def state_or_none() -> AppState | None:
    """The current state, or ``None`` when the application has not started."""
    return _state


def get_state() -> AppState:
    """The current state.

    Raises:
        RuntimeError: when called before :func:`init_state` (which would mean a
            router ran outside the FastAPI lifespan).
    """
    if _state is None:
        raise RuntimeError(
            "Application state is not initialised; call init_state() from the "
            "FastAPI lifespan before using the API."
        )
    return _state
