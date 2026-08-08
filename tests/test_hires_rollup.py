"""The library-wide hi-res roll-up behind ``StatsOut.quality``.

The Library header draws one percentage over the whole library, and the release
rows underneath it each draw their own ``owned_hires``. The only interesting
property of the roll-up is that those two can never disagree, which is why
:func:`app.api.deps.library_quality` asks
:func:`app.core.quality.owned_format_id` and :func:`app.core.quality.is_hires`
— the identical pair :func:`app.api.deps.album_to_out` asks — rather than doing
format arithmetic of its own. ``test_the_figure_agrees_with_the_release_rows``
is the assertion that pins it and ``test_the_roll_up_makes_no_format_judgement_
of_its_own`` is the one that stops a "faster" SQL rewrite quietly reintroducing
a second implementation.

The other half is that the figure is **three-valued**, exactly like
``AlbumOut.complete``. A release whose held format cannot be determined — every
scan-adopted MP3 folder is one, because those rows carry a sampling rate and no
bit depth — is left out of ``measured`` rather than counted as lossy. Nine of
seventy-five on the library this was measured against, which is the difference
between an honest 24% and a wrong 21%.

Scratch SQLite under ``tmp_path`` via ``dependency_overrides``; no worker, no
network.
"""

from __future__ import annotations

import asyncio
import inspect
from dataclasses import dataclass
from pathlib import Path
from typing import AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import delete
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.api import deps
from app.db import get_session
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    Track,
    TrackStatus,
)

ARTIST_ID = "720076"

#: id, status, track count, (format_id, bit_depth, sampling_rate), has a path.
SEED = (
    # Straightforwardly hi-res.
    ("hires24960001", AlbumStatus.DOWNLOADED, 3, (7, 24, 96.0), True),
    ("hires241920002", AlbumStatus.DOWNLOADED, 2, (27, 24, 192.0), True),
    # Straightforwardly not.
    ("redbook00003", AlbumStatus.DOWNLOADED, 4, (6, 16, 44.1), True),
    # A scan-adopted MP3 folder: a sampling rate, no bit depth, no format id.
    # Unmeasurable, and NOT the same claim as "lossy".
    ("scannedmp3005", AlbumStatus.DOWNLOADED, 5, (None, None, 44.1), True),
    # Rows with no file behind them. Nothing to measure.
    ("nopaths000006", AlbumStatus.DOWNLOADED, 3, (6, 16, 44.1), False),
    ("notracks00007", AlbumStatus.DOWNLOADED, 0, (6, 16, 44.1), True),
    # Not on disk: outside library_scope however good the files claim to be.
    ("wantedrel0008", AlbumStatus.WANTED, 3, (7, 24, 96.0), True),
    ("inflight00009", AlbumStatus.DOWNLOADING, 3, (7, 24, 96.0), True),
)

#: Three hi-res files and one red-book one. The worst file decides, so this is
#: measured and is *not* hi-res.
MIXED = "mixedworst004"


@dataclass
class Env:
    client: TestClient
    session_maker: async_sessionmaker[AsyncSession]


@pytest.fixture(name="env")
def env_fixture(tmp_path: Path) -> Iterator[Env]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'hires.db'}", poolclass=NullPool
    )
    session_maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    def add_track(
        session: AsyncSession,
        album_id: str,
        number: int,
        owned: tuple[int | None, int | None, float | None],
        has_path: bool,
    ) -> None:
        format_id, bit_depth, sampling_rate = owned
        session.add(
            Track(
                id=f"{album_id}-{number}",
                album_id=album_id,
                title=f"Track {number}",
                track_number=number,
                status=TrackStatus.DOWNLOADED,
                path=f"{tmp_path}/{album_id}/{number:02d}.flac" if has_path else None,
                format_id=format_id,
                bit_depth=bit_depth,
                sampling_rate=sampling_rate,
            )
        )

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with session_maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm", monitored=True))
            for album_id, status, count, owned, has_path in SEED:
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=album_id,
                        status=status,
                        monitored=True,
                        release_type="album",
                        tracks_count=count,
                        max_bit_depth=24,
                        max_sampling_rate=192.0,
                        path=f"{tmp_path}/{album_id}",
                    )
                )
                for number in range(1, count + 1):
                    add_track(session, album_id, number, owned, has_path)

            session.add(
                Album(
                    id=MIXED,
                    artist_id=ARTIST_ID,
                    title=MIXED,
                    status=AlbumStatus.DOWNLOADED,
                    monitored=True,
                    release_type="album",
                    tracks_count=4,
                    max_bit_depth=24,
                    max_sampling_rate=192.0,
                    path=f"{tmp_path}/{MIXED}",
                )
            )
            for number in (1, 2, 3):
                add_track(session, MIXED, number, (7, 24, 96.0), True)
            add_track(session, MIXED, 4, (6, 16, 44.1), True)

            await session.commit()

    asyncio.run(seed())

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield Env(client=TestClient(main.app), session_maker=session_maker)
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        asyncio.run(engine.dispose())


def rollup(env: Env) -> dict:
    payload = env.client.get("/api/stats")
    assert payload.status_code == 200, payload.text
    return payload.json()["quality"]


def test_the_population_is_the_releases_on_disk(env: Env) -> None:
    """``albums`` is ``enricher.library_scope``'s population and nothing wider.

    The ``wanted`` and ``downloading`` releases carry 24/96 track rows and are
    still absent: a release that is not on disk has no files to be hi-res.
    """
    assert rollup(env)["albums"] == 7


def test_a_release_whose_format_is_unknown_is_not_counted_as_lossy(env: Env) -> None:
    """The whole three-valued rule, as arithmetic.

    Four of the seven on disk can be measured; two of those four are hi-res. The
    share is ``2/4``, **not** ``2/7`` — the scan-adopted MP3 folder, the
    pathless rows and the trackless release have not been found to be lossy,
    they have not been found at all.
    """
    quality = rollup(env)
    assert quality["measured"] == 4
    assert quality["hires_share"] == pytest.approx(0.5)


def test_the_worst_file_decides(env: Env) -> None:
    """Three 24/96 files and one 16/44.1 file is not a hi-res release.

    Delegated entirely to :func:`app.core.quality.owned_format_id`, whose rule
    this is; the roll-up must not soften it into an average.
    """
    quality = rollup(env)
    assert quality["hires"] == 2
    albums = env.client.get(f"/api/artists/{ARTIST_ID}/albums").json()["items"]
    mixed = next(album for album in albums if album["id"] == MIXED)
    assert mixed["owned_hires"] is False


def test_the_share_is_none_when_nothing_is_measured(env: Env) -> None:
    """A share of nothing is ``null``, never ``0.0``.

    A fresh install reporting ``0% hi-res`` reads as a broken library rather
    than an empty one. ``albums`` still reports the population, so the two
    numbers together say "seven releases, none measurable" rather than "no
    releases".
    """

    async def wipe() -> None:
        async with env.session_maker() as session:
            await session.execute(delete(Track))
            await session.commit()

    asyncio.run(wipe())

    quality = rollup(env)
    assert quality["hires_share"] is None
    assert quality["measured"] == 0
    assert quality["albums"] == 7


def test_a_track_with_no_path_is_not_a_file(env: Env) -> None:
    """A row without a path describes no file, so it measures nothing."""
    quality = rollup(env)
    assert quality["albums"] == 7
    # Were the pathless release counted, `measured` would be five.
    assert quality["measured"] == 4


def test_the_figure_agrees_with_the_release_rows(env: Env) -> None:
    """The load-bearing one: the header and the rows beneath it are one answer.

    If somebody re-derives the format arithmetic inside ``library_quality`` —
    a ``bit_depth > 16`` in the SQL is the tempting version — this is what
    fails, because ``AlbumOut.owned_hires`` would still be
    ``quality.is_hires(quality.owned_format_id(tracks))``.
    """
    quality = rollup(env)
    albums = env.client.get(f"/api/artists/{ARTIST_ID}/albums").json()["items"]
    on_disk = [album for album in albums if album["status"] == "downloaded"]

    assert sum(1 for album in on_disk if album["owned_hires"] is True) == quality["hires"]
    assert (
        sum(1 for album in on_disk if album["owned_hires"] is not None)
        == quality["measured"]
    )


def test_the_roll_up_makes_no_format_judgement_of_its_own() -> None:
    """A source-level guard on "one quality comparison, whoever is asking".

    Blunt, and deliberately so: the invariant is otherwise unenforceable, and
    the failure it prevents is a number nobody can check by eye drifting away
    from the rows it sits above.

    The **docstring is excised first**, because it names every forbidden form in
    order to forbid it. Scanning the whole source would make the warning against
    a mistake indistinguishable from the mistake.
    """
    source = inspect.getsource(deps.library_quality)
    doc = deps.library_quality.__doc__ or ""
    assert doc and doc in source, "the docstring moved; this guard needs rewriting"
    body = source.replace(doc, "")

    assert "quality.owned_format_id" in body
    assert "quality.is_hires" in body
    for forbidden in ("bit_depth >", ".mp3", "FORMAT_CEILINGS", "format_for_quality"):
        assert forbidden not in body, forbidden


def test_the_roll_up_is_not_on_the_status_payload(env: Env) -> None:
    """It rides on ``StatsOut``, which polls at 30s, not on the 10s footer.

    ``LibraryStatsOut`` is embedded in ``StatusOut`` as well, so putting the
    roll-up there would triple the cost of a scan the shell has no use for.
    """
    assert "quality" not in env.client.get("/api/status").json()
    assert "quality" not in env.client.get("/api/stats").json()["library"]
