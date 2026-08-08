"""The nightly job counts unplayable files. It does not trash them.

Housekeeping used to quarantine every file carrying a corruption verdict: trash
it, blank the track row, mark the release wanted. That is no longer automatic,
and the objection is not that the quarantine is unsafe — it goes to the trash,
through the same four gates as every other library write, and it is recoverable.
The objection is that it **erases its own alarm**.

``corrupt_files`` is the number that brings somebody to the Integrity screen.
Moving the file out clears it. What is left is a release that says only that it
is incomplete, which on a real library is indistinguishable from the thousands
nobody has ever downloaded — so the person who could have pressed *download
again* is never told there is anything to press it for. An automatic fix that
deletes the evidence of what it fixed is worse than no fix.

So the verdict stays on the row, the file stays on disk, and the count is
reported. ``POST /api/library/quarantine`` is unchanged: trashing a broken file
is a decision somebody makes while looking at it.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

from app.core.librarian import corrupt_file_count
from app.core.scheduler import housekeeping
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    FingerprintState,
    Track,
    TrackMetadata,
    TrackOrigin,
    TrackStatus,
)


@pytest.fixture(name="setup")
def setup_fixture(tmp_path: Path) -> Iterator[tuple[Any, Path]]:
    """A one-album library with one file on disk, recorded as corrupt."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'hk.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    audio = tmp_path / "music" / "Nils Frahm" / "All Melody" / "01 - Broken.flac"
    audio.parent.mkdir(parents=True)
    audio.write_bytes(b"")

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id="a", name="Nils Frahm"))
            session.add(
                Album(
                    id="al",
                    artist_id="a",
                    title="All Melody",
                    status=AlbumStatus.DOWNLOADED,
                    path=str(audio.parent),
                    tracks_count=1,
                )
            )
            session.add(
                Track(
                    id="scan:t1",
                    album_id="al",
                    title="Broken",
                    track_number=1,
                    status=TrackStatus.FAILED,
                    origin=TrackOrigin.SCAN,
                    path=str(audio),
                )
            )
            session.add(
                TrackMetadata(
                    track_id="scan:t1",
                    fingerprint_state=FingerprintState.CORRUPT,
                )
            )
            await session.commit()

    asyncio.run(seed())

    try:
        yield maker, audio
    finally:
        asyncio.run(engine.dispose())


def run_housekeeping(maker: Any) -> dict[str, Any]:
    """One nightly pass, with the two passes that need a real library off."""

    def factory() -> Any:
        class _Ctx:
            async def __aenter__(self) -> AsyncSession:
                self.session = maker()
                return self.session

            async def __aexit__(self, *exc: Any) -> None:
                await self.session.close()

        return _Ctx()

    return asyncio.run(
        housekeeping(factory, scan_library=False, verify_library=False)
    )


def test_the_nightly_job_leaves_the_file_alone(setup: tuple[Any, Path]) -> None:
    maker, audio = setup
    run_housekeeping(maker)

    assert audio.exists(), "housekeeping must never trash a corrupt file"


def test_the_verdict_survives_the_night(setup: tuple[Any, Path]) -> None:
    maker, _ = setup
    run_housekeeping(maker)

    async def read() -> Any:
        async with maker() as session:
            row = await session.get(TrackMetadata, "scan:t1")
            return None if row is None else row.fingerprint_state

    # The alarm has to still be ringing in the morning. Clearing it is what made
    # the problem disappear from the UI instead of being fixed.
    assert asyncio.run(read()) is FingerprintState.CORRUPT


def test_the_track_row_still_points_at_the_file(setup: tuple[Any, Path]) -> None:
    maker, audio = setup
    run_housekeeping(maker)

    async def read() -> Any:
        async with maker() as session:
            return await session.get(Track, "scan:t1")

    track = asyncio.run(read())
    # The quarantine blanks these. Nothing here may.
    assert track is not None
    assert track.path == str(audio)


def test_the_count_is_reported(setup: tuple[Any, Path]) -> None:
    maker, _ = setup
    summary = run_housekeeping(maker)

    # Reported rather than acted on — and reported at all, because a suppressed
    # alarm that leaves no trace is indistinguishable from a check that never ran.
    assert summary["corrupt_files_waiting"] == 1


def test_a_muted_release_is_left_out_of_the_count(setup: tuple[Any, Path]) -> None:
    maker, _ = setup

    async def mute() -> None:
        async with maker() as session:
            album = await session.get(Album, "al")
            assert album is not None
            album.mute_integrity = True
            await session.commit()

    asyncio.run(mute())

    async def count() -> int:
        async with maker() as session:
            return await corrupt_file_count(session)

    # Same exclusion the quarantine and the Integrity screen's actionable figure
    # make. Two statements of "which files are broken" would drift, and the
    # direction they drift in is a number no button can act on.
    assert asyncio.run(count()) == 0


def test_the_count_query_agrees_with_what_is_recorded(setup: tuple[Any, Path]) -> None:
    maker, _ = setup

    async def count() -> tuple[int, int]:
        async with maker() as session:
            rows = (
                await session.execute(
                    select(TrackMetadata).where(
                        TrackMetadata.fingerprint_state == FingerprintState.CORRUPT
                    )
                )
            ).scalars().all()
            return await corrupt_file_count(session), len(rows)

    counted, recorded = asyncio.run(count())
    assert counted == recorded == 1
