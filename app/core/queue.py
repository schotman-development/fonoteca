"""The sequential download queue worker.

One long-lived asyncio task pulls the highest-priority pending
:class:`~app.models.QueueItem`, hands the album to the downloader, and records
the outcome.  There is deliberately **no concurrency**: albums are downloaded
one at a time, with ``DOWNLOAD_TRACK_DELAY`` seconds of breathing room between
items, so the Qobuz request budget is spent slowly.

Durability
----------
All queue state lives in SQLite, so a restart resumes exactly where it left
off.  Because an ``active`` row can only be left behind by a crash, the worker
resets any such rows back to ``pending`` when it starts.

Retries
-------
A failed item goes back to ``pending`` with an exponential backoff
(``DOWNLOAD_RETRY_DELAY`` x ``BACKOFF_MULTIPLIER`` ^ attempts, capped at
``BACKOFF_MAX``, with jitter) until ``DOWNLOAD_MAX_ATTEMPTS`` is reached, at
which point it is marked ``failed``.  The backoff deadline is held **in memory
only** — after a restart a pending retry becomes eligible immediately, which is
the safe direction to err in.

The actual downloading lives in :mod:`app.core.downloader`; this module only
needs something shaped like ``async def download_album(session, album_id)``.
That indirection is what lets the worker be unit-tested without touching the
network, and lets the application start even if the downloader module is not
importable yet.
"""

from __future__ import annotations

import asyncio
import inspect
import random
import time
from contextlib import suppress
from datetime import datetime, timezone
from typing import Any, AsyncContextManager, Awaitable, Callable, Protocol

from sqlalchemy import func, select
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
)

__all__ = [
    "DownloadCallable",
    "DownloadReportedFailure",
    "QueueWorker",
    "SessionFactory",
    "resolve_download_callable",
]

logger = get_logger(__name__)

#: What the worker needs from the downloader.
DownloadCallable = Callable[[AsyncSession, str], Awaitable[Any]]

#: A zero-argument callable returning an ``async with`` session context manager.
SessionFactory = Callable[[], AsyncContextManager[AsyncSession]]


class _SupportsDownloadAlbum(Protocol):
    """Structural type for :class:`app.core.downloader.AlbumDownloader`."""

    async def download_album(self, session: AsyncSession, album_id: str) -> Any:
        """Download every track of *album_id* and tag it into the library."""


def _now() -> datetime:
    """Timezone-aware current time."""
    return datetime.now(timezone.utc)


def resolve_download_callable(
    *,
    client: Any = None,
    settings: Settings | None = None,
    limiter: Any = None,
) -> DownloadCallable:
    """Build the album-download callable from :mod:`app.core.downloader`.

    The downloader is imported lazily and constructed with whichever of
    ``client`` / ``settings`` / ``limiter`` its constructor actually accepts, so
    the queue worker does not have to know that module's exact signature.  A
    module-level ``download_album`` coroutine is accepted as a fallback.

    Raises:
        RuntimeError: when :mod:`app.core.downloader` cannot be imported or does
            not expose a usable entry point.
    """
    try:
        from app.core import downloader as downloader_module  # noqa: PLC0415
    except ImportError as exc:  # pragma: no cover - depends on deployment state
        raise RuntimeError(
            "app.core.downloader is not available; the queue cannot download "
            f"anything yet ({exc})"
        ) from exc

    factory = getattr(downloader_module, "AlbumDownloader", None)
    if factory is not None:
        offered = {"client": client, "settings": settings, "limiter": limiter}
        try:
            parameters = inspect.signature(factory).parameters
        except (TypeError, ValueError):  # pragma: no cover - exotic callables
            parameters = {}
        kwargs = {
            name: value
            for name, value in offered.items()
            if value is not None and name in parameters
        }
        try:
            instance: _SupportsDownloadAlbum = factory(**kwargs)
        except TypeError as exc:  # e.g. a required `client` we were not given
            raise RuntimeError(
                f"AlbumDownloader could not be constructed with {sorted(kwargs)}: {exc}"
            ) from exc
        method = getattr(instance, "download_album", None)
        if method is None:
            raise RuntimeError("AlbumDownloader has no download_album() method")
        return method

    module_level = getattr(downloader_module, "download_album", None)
    if module_level is None:
        raise RuntimeError(
            "app.core.downloader exposes neither AlbumDownloader nor download_album"
        )
    return module_level


class QueueWorker:
    """Single-slot download worker driving :class:`~app.models.QueueItem` rows.

    Args:
        download_album: Coroutine ``(session, album_id) -> Any``.  When omitted
            it is resolved from :mod:`app.core.downloader` on :meth:`start`.
        session_factory: Callable returning an async context manager yielding a
            session; defaults to :func:`app.db.session_scope` (which commits on
            clean exit).
        settings: Effective settings; defaults to :func:`app.config.get_settings`.
        idle_interval: Seconds to wait before re-polling an empty queue.  The
            wait is interrupted by :meth:`notify`.
        sleep_func / random_func: Injected for tests.

    Lifecycle::

        worker = QueueWorker()
        await worker.start()
        ...
        await worker.stop()
    """

    def __init__(
        self,
        *,
        download_album: DownloadCallable | None = None,
        session_factory: SessionFactory = session_scope,
        settings: Settings | None = None,
        idle_interval: float = 5.0,
        sleep_func: Callable[[float], Awaitable[None]] = asyncio.sleep,
        random_func: Callable[[], float] = random.random,
        client: Any = None,
    ) -> None:
        self._settings = settings or get_settings()
        self._session_factory = session_factory
        self._download: DownloadCallable | None = download_album
        self._client = client
        self._idle_interval = max(0.5, idle_interval)
        self._sleep = sleep_func
        self._random = random_func

        self._task: asyncio.Task[None] | None = None
        self._stopping = False
        #: Set by :meth:`cancel_item` while the *current* item must abort.
        self._cancel_requested = False
        self._wakeup = asyncio.Event()
        self._retry_at: dict[int, float] = {}
        self._current_item_id: int | None = None
        self._current_album_id: str | None = None
        self._current_album_title: str | None = None
        self._current_started_at: datetime | None = None
        self._last_error: str | None = None
        self._processed = 0
        self._failed = 0
        self._downloader_error: str | None = None
        self._accepts_should_stop: bool | None = None

    # ----------------------------------------------------------- lifecycle
    @property
    def running(self) -> bool:
        """True while the background task is alive."""
        return self._task is not None and not self._task.done()

    @property
    def busy(self) -> bool:
        """True while an album is actively being downloaded."""
        return self._current_item_id is not None

    async def start(self) -> None:
        """Reset crashed items, resolve the downloader and spawn the loop.

        Idempotent: calling it on a running worker does nothing.  A missing
        downloader module is logged rather than raised, and simply leaves the
        queue idle until the process is restarted with it available.
        """
        if self.running:
            return

        if self._download is None:
            try:
                self._download = resolve_download_callable(
                    client=self._client, settings=self._settings
                )
                self._downloader_error = None
            except RuntimeError as exc:
                self._downloader_error = str(exc)
                logger.error("Download queue disabled: %s", exc)

        try:
            async with self._session_factory() as session:
                revived = await self.reset_stuck_items(session)
            if revived:
                logger.info("Requeued %d interrupted download(s) from a previous run", revived)
        except Exception as exc:  # noqa: BLE001 - startup must not be fatal
            logger.error("Could not reset interrupted queue items: %s", exc)

        self._stopping = False
        self._wakeup.set()
        self._task = asyncio.create_task(self._run(), name="qobuzarr-queue-worker")
        logger.info("Download queue worker started")

    async def stop(self, timeout: float = 30.0) -> None:
        """Ask the loop to finish, then cancel it if it overstays *timeout*."""
        self._stopping = True
        self._wakeup.set()
        task = self._task
        self._task = None
        if task is None:
            return
        try:
            await asyncio.wait_for(asyncio.shield(task), timeout=timeout)
        except asyncio.TimeoutError:
            logger.warning("Queue worker did not stop in %.0fs, cancelling", timeout)
            task.cancel()
            with suppress(asyncio.CancelledError):
                await task
        except asyncio.CancelledError:  # pragma: no cover - shutdown race
            pass
        logger.info("Download queue worker stopped")

    def notify(self) -> None:
        """Wake the loop immediately (call after enqueuing something)."""
        self._wakeup.set()

    # ---------------------------------------------------------------- loop
    async def _run(self) -> None:
        """The worker loop: one item at a time, politely."""
        while not self._stopping:
            worked = False
            try:
                worked = await self.run_once()
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001 - the loop must never die
                logger.exception("Queue worker iteration failed: %s", exc)
                self._last_error = str(exc)

            if self._stopping:
                break
            if worked:
                await self._sleep(max(0.0, self._settings.download_track_delay))
            else:
                await self._wait_for_work()

    async def _wait_for_work(self) -> None:
        """Sleep until notified, the retry backoff expires, or the poll timeout."""
        timeout = self._idle_interval
        if self._retry_at:
            soonest = min(self._retry_at.values()) - time.monotonic()
            timeout = min(timeout, max(0.1, soonest))
        self._wakeup.clear()
        with suppress(asyncio.TimeoutError):
            await asyncio.wait_for(self._wakeup.wait(), timeout=timeout)

    async def run_once(self) -> bool:
        """Process at most one queue item.

        Returns:
            ``True`` when an item was attempted (success or failure), ``False``
            when the queue is empty, everything pending is in backoff, or no
            downloader is available.
        """
        if self._download is None:
            return False

        claimed = await self._claim_next()
        if claimed is None:
            return False

        item_id, album_id, label = claimed
        logger.info("Downloading %s (queue item %d)", label, item_id)
        try:
            async with self._session_factory() as session:
                outcome = await self._call_download(session, album_id)
        except asyncio.CancelledError:
            await self._release_for_retry(item_id, album_id, "worker shutting down", requeue=True)
            raise
        except Exception as exc:  # noqa: BLE001 - any failure is a queue failure
            logger.warning("Download of %s failed: %s", label, exc)
            await self._finish_failure(item_id, album_id, label, exc)
        else:
            if _was_cancelled(outcome):
                if self._cancel_requested:
                    logger.info("Download of %s was cancelled by the user", label)
                    await self._finish_cancelled(item_id, album_id, label)
                else:
                    logger.info("Download of %s was paused; leaving it queued", label)
                    await self._release_for_retry(
                        item_id, album_id, "paused", requeue=True
                    )
            else:
                reported = _failure_message(outcome)
                if reported is None:
                    await self._finish_success(item_id, album_id, label)
                else:
                    logger.warning("Download of %s reported failure: %s", label, reported)
                    await self._finish_failure(
                        item_id, album_id, label, DownloadReportedFailure(reported)
                    )
        finally:
            self._current_item_id = None
            self._current_album_id = None
            self._current_album_title = None
            self._current_started_at = None
            self._cancel_requested = False
        return True

    async def _call_download(self, session: AsyncSession, album_id: str) -> Any:
        """Invoke the downloader, passing ``should_stop`` when it accepts one.

        :class:`app.core.downloader.AlbumDownloader` polls ``should_stop``
        between tracks.  It returns ``True`` both when the worker is shutting
        down (the album goes back on the queue) and when the user cancelled the
        item that is currently downloading (the album stays cancelled), so a
        mistakenly queued hi-res album can actually be stopped.
        """
        assert self._download is not None  # guarded by run_once()
        if self._accepts_should_stop is None:
            try:
                parameters = inspect.signature(self._download).parameters
            except (TypeError, ValueError):  # pragma: no cover - exotic callables
                parameters = {}
            self._accepts_should_stop = "should_stop" in parameters or any(
                param.kind is inspect.Parameter.VAR_KEYWORD for param in parameters.values()
            )

        if self._accepts_should_stop:
            return await self._download(  # type: ignore[call-arg]
                session, album_id, should_stop=self._should_stop
            )
        return await self._download(session, album_id)

    def _should_stop(self) -> bool:
        """Polled by the downloader between tracks; True to abort the album."""
        return self._stopping or self._cancel_requested

    # ------------------------------------------------------------ item state
    async def _claim_next(self) -> tuple[int, str, str] | None:
        """Atomically mark the next eligible item active. Returns ids + label."""
        now = time.monotonic()
        for item_id, deadline in list(self._retry_at.items()):
            if deadline <= now:
                self._retry_at.pop(item_id, None)

        async with self._session_factory() as session:
            query = (
                select(QueueItem)
                .where(QueueItem.state == QueueState.PENDING)
                .order_by(QueueItem.priority.desc(), QueueItem.id.asc())
            )
            if self._retry_at:
                query = query.where(QueueItem.id.notin_(list(self._retry_at)))
            item = (await session.execute(query.limit(1))).scalars().first()
            if item is None:
                return None

            album = await session.get(Album, item.album_id)
            label = _album_label(album) if album is not None else item.album_id

            item.state = QueueState.ACTIVE
            item.attempts += 1
            item.started_at = _now()
            item.finished_at = None
            if album is not None:
                item.progress_tracks_total = album.tracks_count or item.progress_tracks_total
                album.status = AlbumStatus.DOWNLOADING

            self._current_item_id = item.id
            self._current_album_id = item.album_id
            self._current_album_title = label
            self._current_started_at = item.started_at
            return item.id, item.album_id, label

    async def _finish_success(self, item_id: int, album_id: str, label: str) -> None:
        """Mark the item done and the album downloaded.

        A row that was cancelled while it was downloading is left alone: the
        worker must never resurrect a cancelled item as ``done``.
        """
        async with self._session_factory() as session:
            item = await session.get(QueueItem, item_id)
            album = await session.get(Album, album_id)
            if item is not None and item.state is QueueState.CANCELLED:
                logger.info("%s finished but was cancelled; leaving it cancelled", label)
                self._retry_at.pop(item_id, None)
                return
            if item is not None:
                item.state = QueueState.DONE
                item.finished_at = _now()
                item.last_error = None
                if album is not None and album.tracks_count:
                    item.progress_tracks_total = album.tracks_count
                    item.progress_tracks_done = album.tracks_count
            if album is not None and album.status is not AlbumStatus.DOWNLOADED:
                album.status = AlbumStatus.DOWNLOADED
                album.downloaded_at = album.downloaded_at or _now()
                # Only on this branch: the download loop marks its own releases
                # enrichable as it finalises them, and this is the other case —
                # a callable that reported success without setting the status.
                await _mark_enrichable(session, album_id)
            await session.flush()
            session.add(
                Activity(
                    level=ActivityLevel.INFO,
                    event="download.completed",
                    message=f"Downloaded {label}",
                    album_id=album_id,
                    artist_id=album.artist_id if album is not None else None,
                )
            )
        self._processed += 1
        self._last_error = None
        self._retry_at.pop(item_id, None)
        logger.info("Finished %s", label)

    async def _finish_failure(
        self, item_id: int, album_id: str, label: str, exc: BaseException
    ) -> None:
        """Requeue with backoff, or mark the item permanently failed."""
        message = (
            str(exc)
            if isinstance(exc, DownloadReportedFailure)
            else f"{type(exc).__name__}: {exc}"
        )
        max_attempts = max(1, self._settings.download_max_attempts)

        async with self._session_factory() as session:
            item = await session.get(QueueItem, item_id)
            album = await session.get(Album, album_id)
            if item is None:
                return
            if item.state is QueueState.CANCELLED:
                # The user pulled the plug mid-album; a resulting error is not
                # a queue failure and must not overwrite the cancellation.
                item.last_error = message[:2000]
                self._retry_at.pop(item_id, None)
                return
            item.last_error = message[:2000]
            give_up = item.attempts >= max_attempts
            if give_up:
                item.state = QueueState.FAILED
                item.finished_at = _now()
                if album is not None:
                    album.status = AlbumStatus.FAILED
            else:
                item.state = QueueState.PENDING
                item.finished_at = None
                if album is not None:
                    album.status = AlbumStatus.QUEUED
                delay = self._retry_delay(item.attempts)
                self._retry_at[item_id] = time.monotonic() + delay
                logger.info(
                    "Retrying %s in %.0fs (attempt %d/%d)",
                    label,
                    delay,
                    item.attempts,
                    max_attempts,
                )
            attempts = item.attempts
            await session.flush()
            session.add(
                Activity(
                    level=ActivityLevel.ERROR if give_up else ActivityLevel.WARNING,
                    event="download.failed" if give_up else "download.retry",
                    message=(
                        f"Download of {label} failed permanently after {attempts} "
                        f"attempt(s): {message}"
                        if give_up
                        else f"Download of {label} failed (attempt {attempts}/{max_attempts}): {message}"
                    ),
                    album_id=album_id,
                    artist_id=album.artist_id if album is not None else None,
                )
            )
        self._failed += 1
        self._last_error = message

    async def _release_for_retry(
        self, item_id: int, album_id: str, reason: str, *, requeue: bool = True
    ) -> None:
        """Put an interrupted item straight back on the queue (no attempt burn)."""
        async with self._session_factory() as session:
            item = await session.get(QueueItem, item_id)
            album = await session.get(Album, album_id)
            if item is None or item.state is QueueState.CANCELLED:
                return
            if requeue:
                item.state = QueueState.PENDING
                item.attempts = max(0, item.attempts - 1)
            item.last_error = reason
            if album is not None and album.status is AlbumStatus.DOWNLOADING:
                album.status = AlbumStatus.QUEUED

    async def _finish_cancelled(self, item_id: int, album_id: str, label: str) -> None:
        """Record that the user stopped an in-flight album part-way through."""
        async with self._session_factory() as session:
            item = await session.get(QueueItem, item_id)
            album = await session.get(Album, album_id)
            if item is not None:
                item.state = QueueState.CANCELLED
                item.finished_at = item.finished_at or _now()
                item.last_error = "Cancelled by the user"
            if album is not None and album.status is AlbumStatus.DOWNLOADING:
                album.status = AlbumStatus.SKIPPED
            await session.flush()
            session.add(
                Activity(
                    level=ActivityLevel.WARNING,
                    event="download.cancelled",
                    message=f"Cancelled download of {label}",
                    album_id=album_id,
                    artist_id=album.artist_id if album is not None else None,
                )
            )
        self._retry_at.pop(item_id, None)
        logger.info("Stopped %s", label)

    def _retry_delay(self, attempts: int) -> float:
        """Exponential backoff with jitter for the *attempts*-th failure."""
        base = max(1.0, float(self._settings.download_retry_delay))
        multiplier = max(1.0, float(self._settings.backoff_multiplier))
        delay = min(base * (multiplier ** max(0, attempts - 1)), float(self._settings.backoff_max))
        jitter = min(max(float(self._settings.backoff_jitter), 0.0), 1.0)
        if jitter:
            delay *= 1.0 + ((self._random() * 2.0) - 1.0) * jitter
        return max(1.0, delay)

    # ------------------------------------------------------- public helpers
    async def reset_stuck_items(self, session: AsyncSession) -> int:
        """Return ``active`` rows to ``pending`` (they can only be crash debris)."""
        rows = await session.execute(
            select(QueueItem).where(QueueItem.state == QueueState.ACTIVE)
        )
        items = list(rows.scalars().all())
        for item in items:
            item.state = QueueState.PENDING
            item.started_at = None
            item.last_error = "Interrupted by a restart"
            album = await session.get(Album, item.album_id)
            if album is not None and album.status is AlbumStatus.DOWNLOADING:
                album.status = AlbumStatus.QUEUED
        if items:
            await session.flush()
            session.add(
                Activity(
                    level=ActivityLevel.WARNING,
                    event="queue.recovered",
                    message=f"Requeued {len(items)} interrupted download(s) after restart",
                )
            )
        return len(items)

    async def enqueue_album(
        self, session: AsyncSession, album_id: str, *, priority: int = 0
    ) -> QueueItem | None:
        """Queue an album for download unless it is already pending or active."""
        album = await session.get(Album, str(album_id))
        if album is None:
            raise LookupError(f"Album {album_id!r} is not in the library")

        existing = await session.execute(
            select(QueueItem.id)
            .where(
                QueueItem.album_id == str(album_id),
                QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE)),
            )
            .limit(1)
        )
        if existing.first() is not None:
            return None

        item = QueueItem(
            album_id=str(album_id),
            state=QueueState.PENDING,
            priority=priority,
            progress_tracks_total=album.tracks_count or 0,
        )
        session.add(item)
        if album.status in (AlbumStatus.SKIPPED, AlbumStatus.FAILED):
            album.status = AlbumStatus.WANTED
        await session.flush()
        session.add(
            Activity(
                level=ActivityLevel.INFO,
                event="queue.added",
                message=f"Queued {_album_label(album)}",
                album_id=album.id,
                artist_id=album.artist_id,
            )
        )
        self.notify()
        return item

    async def retry_item(self, session: AsyncSession, item_id: int) -> bool:
        """Reset a failed/cancelled item so the worker picks it up again."""
        item = await session.get(QueueItem, int(item_id))
        if item is None:
            return False
        item.state = QueueState.PENDING
        item.attempts = 0
        item.last_error = None
        item.started_at = None
        item.finished_at = None
        album = await session.get(Album, item.album_id)
        if album is not None and album.status in (AlbumStatus.FAILED, AlbumStatus.SKIPPED):
            album.status = AlbumStatus.WANTED
        self._retry_at.pop(int(item_id), None)
        self.notify()
        return True

    async def cancel_item(self, session: AsyncSession, item_id: int) -> bool:
        """Cancel a queue item, including the one currently downloading.

        For the active item this raises the per-item cancel flag that
        :meth:`_should_stop` reports to the downloader, so the album stops after
        the track in flight instead of running to completion.  The row is marked
        ``cancelled`` immediately; :meth:`_finish_success` and
        :meth:`_finish_failure` both refuse to overwrite that state.

        Returns:
            ``True`` when the item was cancelled, ``False`` when it does not
            exist or is active in *another* worker (callers then fall back to a
            plain database update).
        """
        item = await session.get(QueueItem, int(item_id))
        if item is None:
            return False
        if item.state is QueueState.CANCELLED:
            return True
        if item.state is QueueState.ACTIVE:
            if self._current_item_id != item.id:
                return False
            self._cancel_requested = True
            logger.info("Cancel requested for the active download (item %d)", item.id)
        item.state = QueueState.CANCELLED
        item.finished_at = _now()
        item.last_error = "Cancelled by the user"
        album = await session.get(Album, item.album_id)
        if album is not None and album.status in (
            AlbumStatus.WANTED,
            AlbumStatus.QUEUED,
            AlbumStatus.DOWNLOADING,
        ):
            album.status = AlbumStatus.SKIPPED
        self._retry_at.pop(int(item_id), None)
        self.notify()
        return True

    # ---------------------------------------------------------------- status
    def status(self) -> dict[str, Any]:
        """In-memory snapshot; cheap enough to call from any request.

        The lifetime counters are deliberately named ``items_processed`` /
        ``items_failed`` so they cannot collide with the per-state counts in
        :meth:`stats` (``failed`` there means "rows in the failed state").
        """
        return {
            "worker_running": self.running,
            "busy": self.busy,
            "current_item_id": self._current_item_id,
            "current_album_id": self._current_album_id,
            "current_album_title": self._current_album_title,
            "current_started_at": self._current_started_at,
            "retry_backoff_items": len(self._retry_at),
            "items_processed": self._processed,
            "items_failed": self._failed,
            "last_error": self._last_error,
            "downloader_available": self._download is not None,
            "downloader_error": self._downloader_error,
        }

    async def stats(self, session: AsyncSession) -> dict[str, Any]:
        """Per-state counts merged with :meth:`status`, shaped like
        :class:`app.schemas.QueueStatsOut` (plus a few extra keys)."""
        rows = await session.execute(
            select(QueueItem.state, func.count()).group_by(QueueItem.state)
        )
        counts = {state.value: 0 for state in QueueState}
        total = 0
        for state, count in rows.all():
            key = state.value if isinstance(state, QueueState) else str(state)
            counts[key] = int(count)
            total += int(count)

        data = dict(counts)
        data["total"] = total
        data.update(self.status())
        return data


class DownloadReportedFailure(RuntimeError):
    """Raised internally when the downloader *returns* a failure instead of raising.

    :meth:`app.core.downloader.AlbumDownloader.download_album` reports problems
    through its :class:`~app.core.downloader.DownloadResult` rather than by
    raising, so the worker converts that into an exception-shaped failure to
    keep one retry/backoff path.
    """


def _was_cancelled(outcome: Any) -> bool:
    """True when the downloader stopped early on request (``should_stop``)."""
    return bool(getattr(outcome, "cancelled", False))


def _failure_message(outcome: Any) -> str | None:
    """Error text when *outcome* reports a failed album, else ``None``.

    Duck-typed on purpose: a downloader that simply returns ``None`` (or
    anything without a ``status``/``ok``) is treated as having succeeded, which
    keeps the worker testable with a two-line fake.
    """
    if outcome is None:
        return None

    status = getattr(outcome, "status", None)
    ok = getattr(outcome, "ok", None)
    if status is not None:
        failed = getattr(status, "value", status) == AlbumStatus.FAILED.value
    elif isinstance(ok, bool):
        failed = not ok
    else:
        failed = False
    if not failed:
        return None

    errors = list(getattr(outcome, "errors", None) or [])
    detail = "; ".join(str(error) for error in errors[:5])
    summary = getattr(outcome, "summary", None)
    return detail or (str(summary) if summary else "the downloader reported a failure")


async def _mark_enrichable(session: AsyncSession, album_id: str) -> None:
    """Queue an album this worker promoted to ``downloaded`` for enrichment.

    Enrichment is scoped to the library, so the moment a release joins it is the
    moment it becomes eligible. Swallowed on failure and cheap to lose:
    ``Enricher._seed`` reaches the same album from its status alone on the next
    tick, so this only ever buys the wait between the two.
    """
    from app.core.enricher import mark_library_due  # noqa: PLC0415 - avoids a cycle

    try:
        await mark_library_due(session, [str(album_id)])
    except Exception:  # noqa: BLE001 - a side table must not fail a download
        logger.exception("Could not queue %s for enrichment", album_id)


def _album_label(album: Album | None) -> str:
    """``"Artist - Album"`` for logs and the activity feed."""
    if album is None:
        return "unknown album"
    artist = getattr(album.artist, "name", None) if album.artist is not None else None
    return f"{artist} - {album.display_title}" if artist else album.display_title
