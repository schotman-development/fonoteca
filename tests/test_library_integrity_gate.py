"""The fifth gate: verifying the disk before acting on what the database says.

Every other gate in :mod:`app.core.librarian` reasons from rows.
``resolve_in_library`` proves a path is inside the library, ``_assert_exclusive``
proves the folder is this release's own, ``_assert_not_busy`` proves the worker
is not writing into it — and all three pass happily for a release whose audio
somebody else has since replaced. This one opens the files.

The gate is easy to write and easy to neuter, and neutering it fails nothing
unless the states are pinned individually: it deliberately lets four of the five
through, so a test that only checked "an ordinary album can still be deleted"
would pass with the refusal deleted outright. So each state is asserted for what
it licenses:

* ``REPLACED`` refuses, and the folder is still there afterwards — acting on a
  stale record is the one mistake in that module the trash does not make cheap;
* ``RETAGGED`` proceeds, because the sample count proves the audio is the same
  recording and only the baseline was out of date;
* ``MISSING`` proceeds, because an unmounted share must not block the very
  cleanup that follows it;
* ``UNKNOWN`` proceeds, because a library the integrity pass has not reached yet
  is every library on the day this ships.

The files are real FLACs and the hashes are real. A hand-built ``FileStamp``
would prove nothing about whether the gate reads the columns the scan writes.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any, Iterator

import pytest
from mutagen.flac import FLAC
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

from app import config
from app.config import Settings, get_settings
from app.core.integrity import IntegrityState, hash_file, read_stamp
from app.core.librarian import (
    AlbumIntegrity,
    StaleLibraryError,
    TrackIntegrity,
    delete_album_files,
    plan_refile,
    rebaseline_album,
    retag_album,
    verify_album_files,
)
from app.models import Album, AlbumStatus, Artist, Base, Track, TrackStatus
from tests.test_library_scan import write_flac

ARTIST_ID = "720076"
ALBUM_ID = "uyej1o165e870"
FILES = ("01 - Sunson.flac", "02 - My Friend The Forest.flac")


# ---------------------------------------------------------------------------
# A scratch library with one real album in it
# ---------------------------------------------------------------------------
@pytest.fixture(name="settings")
def settings_fixture(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[Settings]:
    """Point LIBRARY_PATH and DATA_PATH at scratch directories, process-wide.

    The assertion is not decoration: these tests trash things, and a redirection
    that silently failed would trash the developer's own library.
    """
    library = tmp_path / "music"
    data = tmp_path / "data"
    library.mkdir()
    data.mkdir()
    monkeypatch.setenv("LIBRARY_PATH", str(library))
    monkeypatch.setenv("DATA_PATH", str(data))
    config.get_settings.cache_clear()
    conf = get_settings()
    assert conf.library_path == library, "refusing to run against the real library"
    try:
        yield conf
    finally:
        config.get_settings.cache_clear()


@pytest.fixture(name="album_dir")
def album_dir_fixture(settings: Settings) -> Path:
    """Two genuine FLACs, tagged, in the folder the database will point at."""
    directory = settings.library_path / "Nils Frahm" / "All Melody (2018)"
    for number, name in enumerate(FILES, start=1):
        write_flac(
            directory / name,
            album="All Melody",
            albumartist="Nils Frahm",
            artist="Nils Frahm",
            title=name.split(" - ", 1)[1].removesuffix(".flac"),
            tracknumber=str(number),
            date="2018",
        )
    return directory


@pytest.fixture(name="maker")
def maker_fixture(
    tmp_path: Path, album_dir: Path
) -> Iterator[async_sessionmaker[AsyncSession]]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'gate.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm", monitored=True))
            session.add(
                Album(
                    id=ALBUM_ID,
                    artist_id=ARTIST_ID,
                    title="All Melody",
                    status=AlbumStatus.DOWNLOADED,
                    monitored=True,
                    tracks_count=len(FILES),
                    path=str(album_dir),
                )
            )
            for number, name in enumerate(FILES, start=1):
                session.add(
                    Track(
                        id=f"{ALBUM_ID}-{number}",
                        album_id=ALBUM_ID,
                        title=name.split(" - ", 1)[1].removesuffix(".flac"),
                        track_number=number,
                        media_number=1,
                        status=TrackStatus.DOWNLOADED,
                        path=str(album_dir / name),
                    )
                )
            await session.commit()

    asyncio.run(seed())
    try:
        yield maker
    finally:
        asyncio.run(engine.dispose())


def baseline(maker: async_sessionmaker[AsyncSession]) -> None:
    """Measure every file and record it, as a disk scan or the nightly pass does."""

    async def go() -> None:
        async with maker() as session:
            album = await session.get(Album, ALBUM_ID)
            for track in album.tracks:
                stamp = read_stamp(Path(str(track.path)))
                assert stamp is not None
                track.content_hash = stamp.content_hash
                track.sample_count = stamp.sample_count
                track.file_size = stamp.size
                track.file_mtime = stamp.mtime
            await session.commit()

    asyncio.run(go())


def with_album(maker: async_sessionmaker[AsyncSession], func: Any) -> Any:
    """Run *func(session, album)* against a fresh session."""

    async def go() -> Any:
        async with maker() as session:
            album = await session.get(Album, ALBUM_ID)
            return await func(session, album)

    return asyncio.run(go())


def replace_audio(path: Path) -> None:
    """Different audio under the same filename — a re-rip, or a restored backup."""
    write_flac(path, seconds=100, album="All Melody", title="Sunson")


def retag_on_disk(path: Path) -> None:
    """A tag write by something that is not Qobuzarr: same audio, new bytes."""
    audio = FLAC(str(path))
    audio["comment"] = ["ripped by somebody else"]
    audio.save()


# ---------------------------------------------------------------------------
# REPLACED — the one state the gate refuses
# ---------------------------------------------------------------------------
def test_a_replaced_file_refuses_the_delete_and_leaves_the_folder(
    maker: async_sessionmaker[AsyncSession], album_dir: Path, settings: Settings
) -> None:
    baseline(maker)
    replace_audio(album_dir / FILES[0])

    with pytest.raises(StaleLibraryError) as caught:
        with_album(maker, lambda session, album: delete_album_files(session, album))

    assert FILES[0] in str(caught.value), "the file that disagreed is named"
    assert (album_dir / FILES[0]).exists()
    assert (album_dir / FILES[1]).exists()
    assert not list(settings.trash_dir.glob("*")), "nothing was trashed"


def test_a_replaced_file_blocks_the_refile_rather_than_moving_it(
    maker: async_sessionmaker[AsyncSession], album_dir: Path
) -> None:
    """Reported as a block, not raised: a library-wide preview lists them all."""
    baseline(maker)
    replace_audio(album_dir / FILES[0])

    plan = with_album(maker, lambda session, album: plan_refile(session, album))

    assert plan.blocked and "not the ones Qobuzarr recorded" in plan.blocked
    assert album_dir.is_dir(), "the preview moved nothing"


def test_a_replaced_file_refuses_the_retag(
    maker: async_sessionmaker[AsyncSession], album_dir: Path
) -> None:
    """The tags about to be written describe a release that is no longer here."""
    baseline(maker)
    replace_audio(album_dir / FILES[0])
    before = hash_file(album_dir / FILES[1])

    with pytest.raises(StaleLibraryError):
        with_album(maker, lambda session, album: retag_album(session, album))

    assert hash_file(album_dir / FILES[1]) == before, "no file was rewritten"


# ---------------------------------------------------------------------------
# RETAGGED, MISSING, UNKNOWN — the states that must still get through
# ---------------------------------------------------------------------------
def test_a_retagged_file_still_deletes(
    maker: async_sessionmaker[AsyncSession], album_dir: Path
) -> None:
    """Different bytes, same sample count: provably the same recording.

    Refusing here would lock a release out of every operation because somebody
    ran a tagger over it, which is the ordinary thing to have done.
    """
    baseline(maker)
    retag_on_disk(album_dir / FILES[0])

    result = with_album(maker, lambda session, album: delete_album_files(session, album))

    assert result.trashed is not None
    assert not album_dir.exists()


def test_a_missing_file_still_deletes(
    maker: async_sessionmaker[AsyncSession], album_dir: Path
) -> None:
    """An unmounted share must not block the cleanup that follows it."""
    baseline(maker)
    (album_dir / FILES[0]).unlink()

    result = with_album(maker, lambda session, album: delete_album_files(session, album))

    assert result.trashed is not None
    assert not album_dir.exists()


def test_an_unbaselined_album_still_deletes(
    maker: async_sessionmaker[AsyncSession], album_dir: Path
) -> None:
    """No baseline is no claim, which is every library on the day this ships."""
    result = with_album(maker, lambda session, album: delete_album_files(session, album))

    assert result.trashed is not None
    assert not album_dir.exists()


# ---------------------------------------------------------------------------
# Re-baselining after Qobuzarr's own write
# ---------------------------------------------------------------------------
def test_a_retag_leaves_the_baseline_describing_the_new_bytes(
    maker: async_sessionmaker[AsyncSession], album_dir: Path
) -> None:
    """Otherwise our own edit reads as somebody else's on the next pass.

    ``retag_album`` rewrites the container of every track, so without the
    re-baseline the gate above would refuse the next re-file, the next delete and
    the next re-tag on the strength of a write this program made itself.
    """
    baseline(maker)

    with_album(maker, lambda session, album: retag_album(session, album))

    async def read_back() -> list[tuple[str | None, str]]:
        async with maker() as session:
            album = await session.get(Album, ALBUM_ID)
            return [
                (track.content_hash, hash_file(Path(str(track.path))))
                for track in album.tracks
            ]

    pairs = asyncio.run(read_back())
    assert pairs, "the album has tracks to re-measure"
    for recorded, actual in pairs:
        assert recorded == actual


def test_rebaseline_records_what_is_there_now(
    maker: async_sessionmaker[AsyncSession], album_dir: Path
) -> None:
    baseline(maker)
    retag_on_disk(album_dir / FILES[0])

    measured = with_album(maker, lambda session, album: _rebaseline_and_commit(session, album))

    assert measured == len(FILES)

    async def read_back() -> list[tuple[str | None, str]]:
        async with maker() as session:
            album = await session.get(Album, ALBUM_ID)
            return [
                (track.content_hash, hash_file(Path(str(track.path))))
                for track in album.tracks
            ]

    for recorded, actual in asyncio.run(read_back()):
        assert recorded == actual


async def _rebaseline_and_commit(session: AsyncSession, album: Album) -> int:
    """``rebaseline_album`` deliberately does not commit; its callers do."""
    measured = await rebaseline_album(album)
    await session.commit()
    return measured


# ---------------------------------------------------------------------------
# What the album-level answer says
# ---------------------------------------------------------------------------
def test_verify_reports_each_file_as_it_found_it(
    maker: async_sessionmaker[AsyncSession], album_dir: Path
) -> None:
    baseline(maker)
    replace_audio(album_dir / FILES[0])

    found = with_album(maker, lambda session, album: verify_album_files(album))

    assert found.counts == {"replaced": 1, "verified": 1}
    assert [Path(track.path).name for track in found.replaced] == [FILES[0]]
    assert found.state is IntegrityState.REPLACED


def track_state(state: IntegrityState) -> TrackIntegrity:
    return TrackIntegrity(track_id="t", qid=None, path="/music/a.flac", state=state)


@pytest.mark.parametrize(
    ("states", "expected"),
    [
        ((IntegrityState.REPLACED, IntegrityState.MISSING), IntegrityState.REPLACED),
        ((IntegrityState.MISSING, IntegrityState.RETAGGED), IntegrityState.MISSING),
        ((IntegrityState.RETAGGED, IntegrityState.UNKNOWN), IntegrityState.RETAGGED),
        ((IntegrityState.UNKNOWN, IntegrityState.VERIFIED), IntegrityState.UNKNOWN),
        ((IntegrityState.VERIFIED, IntegrityState.VERIFIED), IntegrityState.VERIFIED),
    ],
)
def test_the_album_state_is_the_worst_thing_any_file_said(
    states: tuple[IntegrityState, ...], expected: IntegrityState
) -> None:
    """One replaced file makes the release suspect; one verified file among
    unmeasured ones says nothing about the unmeasured ones."""
    found = AlbumIntegrity(album_id="a", tracks=[track_state(s) for s in states])

    assert found.state is expected


def test_an_album_with_nothing_to_check_is_unknown() -> None:
    """Nothing was measured, which is not the same as nothing being wrong."""
    found = AlbumIntegrity(album_id="a")

    assert found.state is IntegrityState.UNKNOWN
    assert found.summary == "nothing measured"
