"""The footer's capacity meter, both halves of it, and the line between them.

The bar in the status footer is a claim about a **volume** — how full the
filesystem holding ``LIBRARY_PATH`` is — and the figure beside it is a claim
about the **library** — how many bytes of audio Qobuzarr believes it holds.
They are different facts, they come from different places (one ``statvfs``
against a ``SUM`` over ``tracks.file_size``), and only the first one answers
"will another discography fit". Reading either as the other is the
"never use a value as something it is not" failure in its cheapest form, so
the tests below pin them apart deliberately: a probe that cannot measure is
``None`` rather than a zeroed record, and a catalogue release nobody
downloaded contributes no bytes to the library's size.

No network, no server: the probe is a syscall and the builders take a session.
"""

from __future__ import annotations

import asyncio
import os
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app import config
from app.api import deps
from app.core.scanner import disk_capacity
from app.db import get_session
from app.models import Album, AlbumStatus, Artist, Base, Track, TrackStatus

ARTIST_ID = "720076"
OWNED_ID = "cccc3333dddd4"
WANTED_ID = "uyej1o165e870"


class _Statvfs:
    """A ``statvfs`` result the kernel could plausibly return."""

    def __init__(self, *, frsize: int = 4096, bsize: int = 4096, blocks: int = 1000) -> None:
        self.f_frsize = frsize
        self.f_bsize = bsize
        self.f_blocks = blocks
        self.f_bfree = blocks // 2
        self.f_bavail = blocks // 4


# --------------------------------------------------------------------------
# the probe
# --------------------------------------------------------------------------


def test_measures_the_volume_a_real_path_sits_on(tmp_path: Path) -> None:
    """The happy answer: a record, with every figure inside its own total."""
    measured = disk_capacity(tmp_path)
    assert measured is not None
    assert measured.path == str(tmp_path)
    assert measured.total_bytes > 0
    assert 0 <= measured.used_bytes <= measured.total_bytes
    assert 0 <= measured.free_bytes <= measured.total_bytes
    # Reserved blocks belong to neither side, so this is an inequality on
    # purpose. Making it an identity would misreport one of the two.
    assert measured.used_bytes + measured.free_bytes <= measured.total_bytes


def test_a_missing_path_is_unknown_not_empty(tmp_path: Path) -> None:
    assert disk_capacity(tmp_path / "nope") is None


def test_a_failed_syscall_is_unknown(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """A stale mount raises; it does not answer zero."""

    def boom(_path: str) -> None:
        raise OSError(116, "Stale file handle")

    monkeypatch.setattr(os, "statvfs", boom)
    assert disk_capacity(tmp_path) is None


@pytest.mark.parametrize(
    "stub",
    [
        _Statvfs(frsize=0, bsize=0),
        _Statvfs(blocks=0),
    ],
    ids=["no block size", "no blocks"],
)
def test_a_nonsense_measurement_is_unknown(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, stub: _Statvfs
) -> None:
    """Three ways to be unknown, one answer, and it is never a zeroed record."""
    monkeypatch.setattr(os, "statvfs", lambda _path: stub)
    assert disk_capacity(tmp_path) is None


def test_frsize_falls_back_to_bsize(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """``f_frsize`` is 0 on some filesystems; ``f_bsize`` is the fallback."""
    monkeypatch.setattr(os, "statvfs", lambda _path: _Statvfs(frsize=0, bsize=512, blocks=100))
    measured = disk_capacity(tmp_path)
    assert measured is not None
    assert measured.total_bytes == 512 * 100
    assert measured.free_bytes == 512 * 25
    assert measured.used_bytes == 512 * 50


# --------------------------------------------------------------------------
# the builders
# --------------------------------------------------------------------------


@pytest.fixture(name="maker")
def maker_fixture(tmp_path: Path) -> Iterator[async_sessionmaker[AsyncSession]]:
    """One artist, one downloaded release with sized tracks, one wanted one."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'disk.db'}", poolclass=NullPool
    )
    sessions = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with sessions() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm", monitored=True))
            session.add(
                Album(
                    id=OWNED_ID,
                    artist_id=ARTIST_ID,
                    title="Solo",
                    status=AlbumStatus.DOWNLOADED,
                    monitored=True,
                    tracks_count=2,
                    path=str(tmp_path / "Nils Frahm" / "Solo"),
                )
            )
            session.add(
                Album(
                    id=WANTED_ID,
                    artist_id=ARTIST_ID,
                    title="All Melody",
                    status=AlbumStatus.WANTED,
                    monitored=True,
                    tracks_count=1,
                )
            )
            await session.commit()

    asyncio.run(seed())
    try:
        yield sessions
    finally:
        asyncio.run(engine.dispose())


def _add_tracks(
    sessions: async_sessionmaker[AsyncSession],
    rows: tuple[tuple[str, str, int | None], ...],
) -> None:
    async def go() -> None:
        async with sessions() as session:
            for track_id, album_id, size in rows:
                session.add(
                    Track(
                        id=track_id,
                        album_id=album_id,
                        title=f"track {track_id}",
                        track_number=int(track_id[-1]),
                        status=TrackStatus.DOWNLOADED,
                        file_size=size,
                    )
                )
            await session.commit()

    asyncio.run(go())


def _library_stats(sessions: async_sessionmaker[AsyncSession]) -> Any:
    async def go() -> Any:
        async with sessions() as session:
            return await deps.library_stats(session)

    return asyncio.run(go())


def _build_status(sessions: async_sessionmaker[AsyncSession]) -> Any:
    async def go() -> Any:
        async with sessions() as session:
            return await deps.build_status(session, activity_limit=0)

    return asyncio.run(go())


def test_library_size_is_none_when_nothing_has_measured_one(
    maker: async_sessionmaker[AsyncSession],
) -> None:
    """``SUM`` over no sizes is ``NULL``, and that is the answer wanted."""
    _add_tracks(maker, (("1", OWNED_ID, None),))
    assert _library_stats(maker).size_bytes is None


def test_library_size_sums_only_what_is_on_disk(
    maker: async_sessionmaker[AsyncSession],
) -> None:
    """The join is the point: a catalogue row is not library bytes."""
    _add_tracks(
        maker,
        (
            ("1", OWNED_ID, 40_000_000),
            ("2", OWNED_ID, 60_000_000),
            ("3", WANTED_ID, 999_000_000),
        ),
    )
    assert _library_stats(maker).size_bytes == 100_000_000


def test_status_reports_no_disk_when_the_library_path_is_missing(
    maker: async_sessionmaker[AsyncSession],
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("LIBRARY_PATH", str(tmp_path / "unmounted"))
    config.get_settings.cache_clear()
    try:
        status = _build_status(maker)
    finally:
        config.get_settings.cache_clear()
    # ``is None``, not falsy: a zeroed record would pass a truthiness test and
    # draw a 0%-full disk.
    assert status.disk is None


def test_status_carries_the_volume_holding_the_library(
    maker: async_sessionmaker[AsyncSession],
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    library = tmp_path / "music"
    library.mkdir()
    monkeypatch.setenv("LIBRARY_PATH", str(library))
    config.get_settings.cache_clear()
    try:
        status = _build_status(maker)
    finally:
        config.get_settings.cache_clear()
    assert status.disk is not None
    assert status.disk.path == status.library_path == str(library)
    assert status.disk.total_bytes > 0
    assert status.disk.used_bytes <= status.disk.total_bytes


def test_status_serialises_the_disk_record_and_its_absence(
    maker: async_sessionmaker[AsyncSession],
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Over the wire: an object with four keys, or ``null`` — never ``{}``."""

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
        finally:
            await session.close()

    library = tmp_path / "music"
    library.mkdir()
    monkeypatch.setenv("LIBRARY_PATH", str(library))
    config.get_settings.cache_clear()
    # No ``with``: the lifespan signs in to Qobuz for real, and this suite makes
    # no network calls.
    client = TestClient(main.app)
    main.app.dependency_overrides[get_session] = override_session
    try:
        payload = client.get("/api/status").json()
        assert set(payload["disk"]) == {
            "path",
            "total_bytes",
            "used_bytes",
            "free_bytes",
        }
        assert payload["disk"]["total_bytes"] > 0
        assert "size_bytes" in payload["library"]

        monkeypatch.setenv("LIBRARY_PATH", str(tmp_path / "unmounted"))
        config.get_settings.cache_clear()
        assert client.get("/api/status").json()["disk"] is None
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        config.get_settings.cache_clear()
