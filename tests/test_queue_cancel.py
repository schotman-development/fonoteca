"""Regression tests: cancelling a download that is already running.

The bug these pin down: ``QueueWorker.cancel_item`` refused to touch an
``active`` row and ``_call_download`` only ever passed ``should_stop=lambda:
self._stopping``, so a user cancel could not reach the downloader.  The API
answered ``200 cancelled``, every remaining track was still fetched, and
``_finish_success`` then rewrote the row as ``done``.

Everything here runs against a private in-memory SQLite; nothing touches the
network or ``data/``.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from typing import Any, AsyncIterator

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.api import routes_api
from app.config import Settings
from app.core.queue import QueueWorker
from app.models import Album, AlbumStatus, Artist, Base, QueueItem, QueueState


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def make_settings(**overrides: Any) -> Settings:
    """Settings with no pacing and no jitter, so the tests are deterministic."""
    values: dict[str, Any] = {
        "download_track_delay": 0.0,
        "download_max_attempts": 3,
        "backoff_jitter": 0.0,
    }
    values.update(overrides)
    return Settings(**values)


async def make_sessionmaker() -> async_sessionmaker:
    """A fresh in-memory database with the full schema."""
    engine = create_async_engine(
        "sqlite+aiosqlite:///:memory:",
        poolclass=StaticPool,
        connect_args={"check_same_thread": False},
    )
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)
    return async_sessionmaker(engine, expire_on_commit=False, autoflush=False)


def make_session_factory(session_maker: async_sessionmaker) -> Any:
    """A ``session_scope``-alike bound to the test's in-memory database."""

    @asynccontextmanager
    async def factory() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    return factory


async def seed_queue_item(
    session_maker: async_sessionmaker, *, tracks: int = 6
) -> int:
    """Insert artist + album + one pending queue item; return the item id."""
    async with session_maker() as session:
        session.add(Artist(id="100", name="Nils Frahm"))
        session.add(
            Album(
                id="day111",
                artist_id="100",
                title="Day",
                status=AlbumStatus.WANTED,
                tracks_count=tracks,
            )
        )
        item = QueueItem(album_id="day111", state=QueueState.PENDING)
        session.add(item)
        await session.commit()
        return item.id


class _Outcome:
    """Duck-typed stand-in for ``downloader.DownloadResult``."""

    def __init__(self, *, cancelled: bool, tracks_downloaded: int) -> None:
        self.cancelled = cancelled
        self.tracks_downloaded = tracks_downloaded


# ---------------------------------------------------------------------------
# The cancel actually stops the transfer
# ---------------------------------------------------------------------------
def test_cancelling_the_active_item_stops_the_album_mid_way() -> None:
    """The between-track ``should_stop`` poll must observe a user cancel."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        factory = make_session_factory(session_maker)
        item_id = await seed_queue_item(session_maker, tracks=6)
        downloaded = 0

        async def fake_download(
            session: AsyncSession, album_id: str, should_stop: Any = None
        ) -> _Outcome:
            nonlocal downloaded
            for index in range(6):
                if should_stop is not None and should_stop():
                    return _Outcome(cancelled=True, tracks_downloaded=downloaded)
                if index == 1:
                    # The user hits Cancel while track 2 is in flight.
                    async with factory() as other:
                        assert await worker.cancel_item(other, item_id) is True
                downloaded += 1
            return _Outcome(cancelled=False, tracks_downloaded=downloaded)

        worker = QueueWorker(
            download_album=fake_download,
            session_factory=factory,
            settings=make_settings(),
        )

        assert await worker.run_once() is True
        assert downloaded == 2, "the transfer must stop, not run to completion"

        async with session_maker() as session:
            item = await session.get(QueueItem, item_id)
            album = await session.get(Album, "day111")
            assert item is not None and album is not None
            assert item.state is QueueState.CANCELLED
            assert album.status is not AlbumStatus.DOWNLOADED
            assert album.status is AlbumStatus.SKIPPED

        # The cancel flag must not leak into the next item.
        assert worker._should_stop() is False

    asyncio.run(main())


def test_a_cancelled_item_is_never_resurrected_as_done() -> None:
    """A downloader that ignores ``should_stop`` still cannot undo the cancel."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        factory = make_session_factory(session_maker)
        item_id = await seed_queue_item(session_maker)

        async def stubborn_download(session: AsyncSession, album_id: str) -> None:
            async with factory() as other:
                assert await worker.cancel_item(other, item_id) is True
            return None  # "finished" successfully

        worker = QueueWorker(
            download_album=stubborn_download,
            session_factory=factory,
            settings=make_settings(),
        )

        assert await worker.run_once() is True

        async with session_maker() as session:
            item = await session.get(QueueItem, item_id)
            album = await session.get(Album, "day111")
            assert item is not None and album is not None
            assert item.state is QueueState.CANCELLED
            assert album.status is not AlbumStatus.DOWNLOADED

    asyncio.run(main())


def test_a_cancelled_item_is_not_retried_after_a_failure() -> None:
    """An error raised after the cancel must not requeue the item."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        factory = make_session_factory(session_maker)
        item_id = await seed_queue_item(session_maker)

        async def exploding_download(session: AsyncSession, album_id: str) -> None:
            async with factory() as other:
                assert await worker.cancel_item(other, item_id) is True
            raise RuntimeError("connection reset")

        worker = QueueWorker(
            download_album=exploding_download,
            session_factory=factory,
            settings=make_settings(),
        )

        assert await worker.run_once() is True

        async with session_maker() as session:
            item = await session.get(QueueItem, item_id)
            assert item is not None
            assert item.state is QueueState.CANCELLED
        assert item_id not in worker._retry_at

    asyncio.run(main())


def test_cancelling_a_pending_item_still_works() -> None:
    """The original behaviour for a not-yet-started item is unchanged."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        factory = make_session_factory(session_maker)
        item_id = await seed_queue_item(session_maker)
        worker = QueueWorker(
            download_album=lambda session, album_id: None,
            session_factory=factory,
            settings=make_settings(),
        )

        async with factory() as session:
            assert await worker.cancel_item(session, item_id) is True

        async with session_maker() as session:
            item = await session.get(QueueItem, item_id)
            assert item is not None and item.state is QueueState.CANCELLED
        assert worker._should_stop() is False

    asyncio.run(main())


def test_cancel_item_declines_an_item_another_worker_owns() -> None:
    """Falling back to the DB-only path stays possible for a foreign item."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        factory = make_session_factory(session_maker)
        item_id = await seed_queue_item(session_maker)
        async with session_maker() as session:
            item = await session.get(QueueItem, item_id)
            assert item is not None
            item.state = QueueState.ACTIVE
            await session.commit()

        worker = QueueWorker(
            download_album=lambda session, album_id: None,
            session_factory=factory,
            settings=make_settings(),
        )
        async with factory() as session:
            assert await worker.cancel_item(session, item_id) is False

    asyncio.run(main())


# ---------------------------------------------------------------------------
# The API service function must use the worker path for active items
# ---------------------------------------------------------------------------
def test_api_cancel_routes_an_active_item_through_the_worker(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """``routes_api.cancel_queue_item`` no longer skips the worker when active."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        factory = make_session_factory(session_maker)
        item_id = await seed_queue_item(session_maker)
        seen: list[int] = []

        class RecordingWorker(QueueWorker):
            async def cancel_item(self, session: AsyncSession, wanted: int) -> bool:
                seen.append(int(wanted))
                return await super().cancel_item(session, wanted)

        worker = RecordingWorker(
            download_album=lambda session, album_id: None,
            session_factory=factory,
            settings=make_settings(),
        )
        worker._current_item_id = item_id
        monkeypatch.setattr(routes_api, "get_queue_worker", lambda: worker)

        async with session_maker() as session:
            item = await session.get(QueueItem, item_id)
            assert item is not None
            item.state = QueueState.ACTIVE
            await session.commit()

        async with session_maker() as session:
            item = (await session.execute(select(QueueItem))).scalars().one()
            message = await routes_api.cancel_queue_item(session, item)

        assert seen == [item_id], "the worker must be asked to cancel the active item"
        assert "cancelled" in message.lower()
        assert worker._cancel_requested is True

        async with session_maker() as session:
            item = await session.get(QueueItem, item_id)
            assert item is not None and item.state is QueueState.CANCELLED

    asyncio.run(main())
