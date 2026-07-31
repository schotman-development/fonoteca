"""Disk scanning: reading a real library folder and matching it to the database.

The fixtures build genuine (if silent) FLAC files with genuine Vorbis comments,
so ``mutagen`` really parses them and the tag-reading path is exercised rather
than mocked.  A FLAC file is legal with nothing but a STREAMINFO block, which is
what :func:`write_flac` produces — 50-odd bytes each, so a whole test library
costs nothing.

Most of these tests assert what the scan *did not* do.  Adoption is only safe
because it moves in one direction: a scan may decide "we already have this", it
may never decide "we are missing this", and it may never start a download.  A
regression in that direction would silently pull gigabytes.
"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import mutagen
import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.api import routes_api
from app.config import Settings, get_settings
from app.core.scanner import (
    LibraryScanner,
    album_directory_title,
    artist_key,
    collect_albums,
    load_last_scan,
    read_track,
)
from app.db import get_session
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    QueueItem,
    QueueState,
    Setting,
    Track,
    TrackStatus,
)


# ---------------------------------------------------------------------------
# Building a throwaway library on disk
# ---------------------------------------------------------------------------
def write_flac(
    path: Path,
    *,
    sample_rate: int = 44100,
    channels: int = 2,
    bits: int = 16,
    seconds: int = 200,
    **tags: str,
) -> Path:
    """Write a minimal but valid FLAC file with Vorbis comments.

    Only a STREAMINFO block is emitted (no audio frames), which is enough for
    ``mutagen`` to report the stream properties and to attach tags — and which
    keeps a 40-file fixture library under 4 kB.
    """
    path.parent.mkdir(parents=True, exist_ok=True)

    bitstream = 0
    width = 0

    def put(value: int, bits_used: int) -> None:
        nonlocal bitstream, width
        bitstream = (bitstream << bits_used) | (value & ((1 << bits_used) - 1))
        width += bits_used

    put(4096, 16)  # minimum block size
    put(4096, 16)  # maximum block size
    put(0, 24)  # minimum frame size (unknown)
    put(0, 24)  # maximum frame size (unknown)
    put(sample_rate, 20)
    put(channels - 1, 3)
    put(bits - 1, 5)
    put(sample_rate * seconds, 36)  # total samples
    streaminfo = bitstream.to_bytes(width // 8, "big") + b"\x00" * 16  # + empty MD5

    header = bytes([0x80]) + len(streaminfo).to_bytes(3, "big")  # last block, type 0
    path.write_bytes(b"fLaC" + header + streaminfo)

    if tags:
        audio = mutagen.File(str(path), easy=True)
        for key, value in tags.items():
            audio[key] = [str(value)]
        audio.save()
    return path


def write_album(
    root: Path,
    artist: str,
    folder: str,
    *,
    tracks: int = 3,
    album: str | None = None,
    album_artist: str | None = None,
    year: str = "2018",
    disc: str | None = None,
    total: int | None = None,
    **stream: Any,
) -> Path:
    """Write one album folder of *tracks* tagged FLAC files and return its path.

    The ``album`` tag defaults to the folder name with its ``(2018)`` year
    stripped, which is how a real tagger writes it — the year lives in the
    directory name, not in the title.
    """
    directory = root / artist / folder
    for number in range(1, tracks + 1):
        write_flac(
            directory / f"{number:02d} - Track {number}.flac",
            album=album if album is not None else album_directory_title(folder)[0],
            albumartist=album_artist if album_artist is not None else artist,
            artist=artist,
            title=f"Track {number}",
            tracknumber=str(number),
            discnumber=disc or "1",
            tracktotal=str(total if total is not None else tracks),
            date=year,
            **stream,
        )
    return directory


@pytest.fixture(name="library")
def library_fixture(tmp_path: Path) -> Path:
    """A small library covering the layouts the scanner has to survive."""
    root = tmp_path / "music"

    write_album(root, "Nils Frahm", "All Melody (2018)", tracks=4)
    # An edition suffix on disk that the database does not have.
    write_album(root, "Nils Frahm", "Spaces (Deluxe Edition)", tracks=3, year="2013")
    # Multi-disc, split across CD sub-folders, per-disc TRACKTOTAL.
    two_disc = root / "Alice Coltrane" / "Journey (1971)"
    for disc_number in (1, 2):
        for number in (1, 2):
            write_flac(
                two_disc / f"CD 0{disc_number}" / f"{number:02d} - Part {number}.flac",
                album="Journey",
                albumartist="Alice Coltrane",
                artist="Alice Coltrane",
                title=f"Part {number} disc {disc_number}",
                tracknumber=str(number),
                discnumber=str(disc_number),
                tracktotal="2",
                date="1971",
            )
    # Only two of the three tracks the database expects.
    write_album(root, "black midi", "Hellfire", tracks=2, year="2022")
    # Nobody follows this one.
    write_album(root, "Stranger", "Unknown Record", tracks=2)
    write_album(root, "Stranger", "Second Record", tracks=1)

    # Things that are not music and must be ignored.
    (root / "Nils Frahm" / "folder.jpg").write_bytes(b"\xff\xd8\xff")
    (root / "Nils Frahm" / "artist.nfo").write_text("<artist/>")
    (root / "Nils Frahm" / "All Melody (2018)" / "cover.jpg").write_bytes(b"\xff\xd8\xff")
    (root / "@eaDir" / "thumbs").mkdir(parents=True)
    write_flac(root / "@eaDir" / "thumbs" / "junk.flac", album="Junk")

    return root


@pytest.fixture(name="settings")
def settings_fixture(library: Path) -> Settings:
    """Real settings with the library root pointed at the fixture tree."""
    return get_settings().model_copy(update={"library_path": library})


#: (id, name) for the artists the fixture database follows.
SEED_ARTISTS = (("100", "Nils Frahm"), ("200", "Alice Coltrane"), ("300", "black midi"))

#: (id, artist_id, title, tracks_count, status)
SEED_ALBUMS = (
    ("a1", "100", "All Melody", 4, AlbumStatus.WANTED),
    ("a2", "100", "Spaces", 3, AlbumStatus.WANTED),
    ("a3", "200", "Journey", 4, AlbumStatus.WANTED),
    ("a4", "300", "Hellfire", 3, AlbumStatus.WANTED),
    ("a5", "300", "Schlagenheim", 9, AlbumStatus.WANTED),
)


@pytest.fixture(name="session_maker")
def session_maker_fixture(tmp_path: Path) -> Iterator[async_sessionmaker[AsyncSession]]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'scan.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            for artist_id, name in SEED_ARTISTS:
                session.add(Artist(id=artist_id, name=name))
            for album_id, artist_id, title, count, status in SEED_ALBUMS:
                session.add(
                    Album(
                        id=album_id,
                        artist_id=artist_id,
                        title=title,
                        tracks_count=count,
                        status=status,
                    )
                )
            await session.commit()

    asyncio.run(seed())
    try:
        yield maker
    finally:
        asyncio.run(engine.dispose())


def scan(
    maker: async_sessionmaker[AsyncSession], settings: Settings, **kwargs: Any
) -> Any:
    """Run one scan against a fresh session and return the :class:`ScanResult`."""

    async def run() -> Any:
        async with maker() as session:
            return await LibraryScanner(settings=settings).scan(session, **kwargs)

    return asyncio.run(run())


def albums(maker: async_sessionmaker[AsyncSession]) -> dict[str, Album]:
    """Every album row, keyed by id."""

    async def run() -> dict[str, Album]:
        async with maker() as session:
            rows = await session.execute(select(Album))
            return {row.id: row for row in rows.scalars().unique().all()}

    return asyncio.run(run())


# ---------------------------------------------------------------------------
# Normalisation helpers
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    ("left", "right"),
    [
        ("Nils Frahm", "nils frahm"),
        ("María Dueñas", "Maria Duenas"),
        ("Thorbjørn Risager & The Black Tornado", "Thorbjorn Risager  &  the black tornado"),
        ("The Beatles", "Beatles"),
        ("Sigur Rós", "sigur ros"),
    ],
)
def test_artist_key_folds_the_differences_that_do_not_matter(left: str, right: str) -> None:
    assert artist_key(left) == artist_key(right)


def test_artist_key_keeps_a_band_apart_from_its_frontman() -> None:
    """"The Robert Cray Band" is a different Qobuz artist from "Robert Cray"."""
    assert artist_key("The Robert Cray Band") != artist_key("Robert Cray")


def test_artist_key_of_nothing_is_empty_not_a_match() -> None:
    """An empty key must never be looked up, or every unnamed folder collides."""
    assert artist_key("") == ""
    assert artist_key(None) == ""
    assert artist_key("   ...  ") == ""


@pytest.mark.parametrize(
    ("folder", "title", "year"),
    [
        ("All Melody (2018)", "All Melody", 2018),
        ("All Melody (2018) [FLAC 24-96]", "All Melody", 2018),
        ("All Melody [FLAC 16-44.1]", "All Melody", None),
        ("All Melody", "All Melody", None),
        ("Blues Deluxe, Vol. 2", "Blues Deluxe, Vol. 2", None),
        ("Live 1974 (1975)", "Live 1974", 1975),
    ],
)
def test_album_directory_title_splits_off_year_and_quality(
    folder: str, title: str, year: int | None
) -> None:
    assert album_directory_title(folder) == (title, year)


# ---------------------------------------------------------------------------
# Walking the disk
# ---------------------------------------------------------------------------
def test_the_walk_finds_one_album_per_folder(library: Path) -> None:
    found, stats = collect_albums(library)
    titles = sorted(album.title for album in found)
    assert titles == [
        "All Melody",
        "Hellfire",
        "Journey",
        "Second Record",
        "Spaces (Deluxe Edition)",
        "Unknown Record",
    ]
    assert stats["audio_files"] == 4 + 3 + 4 + 2 + 2 + 1


def test_disc_subfolders_fold_into_one_album(library: Path) -> None:
    found, _ = collect_albums(library)
    journey = next(album for album in found if album.title == "Journey")
    assert journey.file_count == 4
    assert journey.disc_count == 2
    assert journey.directory.name == "Journey (1971)"
    # TRACKTOTAL is per disc (2 + 2), not per release.
    assert journey.total_tracks == 4


def test_non_audio_files_and_nas_directories_are_ignored(library: Path) -> None:
    found, stats = collect_albums(library)
    assert all("@eaDir" not in str(album.directory) for album in found)
    # The .jpg/.nfo sidecars must not inflate the file count.
    all_melody = next(album for album in found if album.title == "All Melody")
    assert all_melody.file_count == 4


def test_artist_comes_from_tags_and_from_the_folder(library: Path) -> None:
    found, _ = collect_albums(library)
    all_melody = next(album for album in found if album.title == "All Melody")
    assert "Nils Frahm" in all_melody.artist_candidates


def test_an_unreadable_file_is_reported_not_raised(tmp_path: Path) -> None:
    """A zero-byte .flac is common in a real library; it must not abort a scan."""
    root = tmp_path / "music"
    write_album(root, "Nils Frahm", "All Melody", tracks=2)
    (root / "Nils Frahm" / "All Melody" / "03 - Broken.flac").write_bytes(b"")

    errors: list[str] = []
    found, stats = collect_albums(root, on_error=errors.append)

    assert len(errors) == 1
    assert "Broken.flac" in errors[0]
    assert stats["unreadable"] == 1
    assert found[0].file_count == 2  # the good files still scanned


def test_read_track_returns_none_for_something_that_is_not_audio(tmp_path: Path) -> None:
    junk = tmp_path / "not-really.flac"
    junk.write_bytes(b"this is not a FLAC stream")
    assert read_track(junk) is None


def test_a_missing_library_root_is_an_error_not_a_crash(tmp_path: Path) -> None:
    errors: list[str] = []
    found, stats = collect_albums(tmp_path / "nope", on_error=errors.append)
    assert found == []
    assert stats["audio_files"] == 0
    assert errors and "does not exist" in errors[0]


# ---------------------------------------------------------------------------
# Matching and adoption
# ---------------------------------------------------------------------------
def test_a_complete_album_on_disk_is_marked_downloaded(
    session_maker: Any, settings: Settings
) -> None:
    result = scan(session_maker, settings)
    rows = albums(session_maker)

    assert rows["a1"].status is AlbumStatus.DOWNLOADED
    assert rows["a1"].path.endswith("All Melody (2018)")
    assert rows["a1"].downloaded_at is not None
    assert result.albums_adopted >= 1


def test_an_edition_suffix_on_disk_still_matches_the_plain_title(
    session_maker: Any, settings: Settings
) -> None:
    """``Spaces (Deluxe Edition)/`` on disk is the database's ``Spaces``."""
    scan(session_maker, settings)
    assert albums(session_maker)["a2"].status is AlbumStatus.DOWNLOADED


def test_a_multi_disc_release_counts_every_disc(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings)
    assert albums(session_maker)["a3"].status is AlbumStatus.DOWNLOADED


def test_an_incomplete_album_stays_wanted_and_is_reported(
    session_maker: Any, settings: Settings
) -> None:
    """Two files where Qobuz lists three: say so, do not call it downloaded."""
    result = scan(session_maker, settings)
    rows = albums(session_maker)

    assert rows["a4"].status is AlbumStatus.WANTED
    assert result.albums_partial == 1
    entry = result.partial[0]
    assert entry["album_id"] == "a4"
    assert (entry["files"], entry["expected"]) == (2, 3)


def test_an_incomplete_album_still_records_where_its_files_are(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings)
    assert albums(session_maker)["a4"].path.endswith("Hellfire")


def test_a_release_with_nothing_on_disk_is_left_completely_alone(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings)
    schlagenheim = albums(session_maker)["a5"]
    assert schlagenheim.status is AlbumStatus.WANTED
    assert schlagenheim.path is None


def test_scanning_twice_changes_nothing_the_second_time(
    session_maker: Any, settings: Settings
) -> None:
    first = scan(session_maker, settings)
    second = scan(session_maker, settings)

    assert first.albums_adopted > 0
    assert second.albums_adopted == 0
    assert second.albums_already == first.albums_adopted
    assert second.albums_matched == first.albums_matched


def test_the_completeness_threshold_is_configurable(
    session_maker: Any, settings: Settings
) -> None:
    """At 60%, two of three tracks is enough — and the album flips."""
    lenient = settings.model_copy(update={"library_scan_complete_ratio": 0.6})
    scan(session_maker, lenient)
    assert albums(session_maker)["a4"].status is AlbumStatus.DOWNLOADED


# ---------------------------------------------------------------------------
# The safety rules
# ---------------------------------------------------------------------------
def test_a_scan_never_marks_anything_wanted(
    session_maker: Any, settings: Settings
) -> None:
    """The whole design rests on this: adoption is one-directional."""

    async def make_downloaded() -> None:
        async with session_maker() as session:
            album = await session.get(Album, "a5")
            album.status = AlbumStatus.DOWNLOADED
            album.path = "/somewhere/else"
            await session.commit()

    asyncio.run(make_downloaded())
    scan(session_maker, settings)

    # a5 has no files on disk at all, and must still not be demoted.
    assert albums(session_maker)["a5"].status is AlbumStatus.DOWNLOADED


def test_a_scan_never_queues_anything(session_maker: Any, settings: Settings) -> None:
    scan(session_maker, settings)

    async def count() -> int:
        async with session_maker() as session:
            rows = await session.execute(select(QueueItem))
            return len(rows.scalars().all())

    assert asyncio.run(count()) == 0


def test_an_album_being_downloaded_right_now_is_left_alone(
    session_maker: Any, settings: Settings
) -> None:
    """The worker owns a downloading album; racing it would corrupt the queue."""

    async def mark_busy() -> None:
        async with session_maker() as session:
            album = await session.get(Album, "a1")
            album.status = AlbumStatus.DOWNLOADING
            await session.commit()

    asyncio.run(mark_busy())
    result = scan(session_maker, settings)

    assert albums(session_maker)["a1"].status is AlbumStatus.DOWNLOADING
    assert result.albums_busy == 1


def test_adopting_an_album_cancels_its_pending_download(
    session_maker: Any, settings: Settings
) -> None:
    async def enqueue() -> None:
        async with session_maker() as session:
            session.add(QueueItem(album_id="a1", state=QueueState.PENDING))
            await session.commit()

    asyncio.run(enqueue())
    result = scan(session_maker, settings)

    async def state() -> QueueState:
        async with session_maker() as session:
            rows = await session.execute(select(QueueItem))
            return rows.scalars().one().state

    assert asyncio.run(state()) is QueueState.CANCELLED
    assert result.queue_items_cancelled == 1


def test_an_active_queue_item_is_not_cancelled(
    session_maker: Any, settings: Settings
) -> None:
    async def enqueue() -> None:
        async with session_maker() as session:
            session.add(QueueItem(album_id="a1", state=QueueState.ACTIVE))
            await session.commit()

    asyncio.run(enqueue())
    result = scan(session_maker, settings)

    async def state() -> QueueState:
        async with session_maker() as session:
            rows = await session.execute(select(QueueItem))
            return rows.scalars().one().state

    assert asyncio.run(state()) is QueueState.ACTIVE
    assert result.queue_items_cancelled == 0


def test_a_dry_run_writes_nothing(session_maker: Any, settings: Settings) -> None:
    result = scan(session_maker, settings, apply=False)

    assert result.albums_adopted > 0  # it still says what it would do
    assert result.applied is False
    rows = albums(session_maker)
    assert all(album.status is AlbumStatus.WANTED for album in rows.values())
    assert all(album.path is None for album in rows.values())


def test_a_dry_run_does_not_overwrite_the_stored_summary(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings)
    scan(session_maker, settings, apply=False)

    async def stored() -> dict[str, Any] | None:
        async with session_maker() as session:
            return await load_last_scan(session)

    assert asyncio.run(stored())["applied"] is True


def test_the_scan_does_not_modify_the_library(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    """Read-only means read-only: same files, same bytes, afterwards."""
    before = {
        path: path.stat().st_mtime_ns
        for path in sorted(library.rglob("*"))
        if path.is_file()
    }
    scan(session_maker, settings)
    after = {
        path: path.stat().st_mtime_ns
        for path in sorted(library.rglob("*"))
        if path.is_file()
    }
    assert before == after


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------
def test_unknown_artists_are_rolled_up_one_row_each(
    session_maker: Any, settings: Settings
) -> None:
    """Two folders by one unfollowed artist is one decision, not two."""
    result = scan(session_maker, settings)

    assert len(result.unknown_artists) == 1
    entry = result.unknown_artists[0]
    assert entry["name"] == "Stranger"
    assert entry["albums"] == 2
    assert entry["files"] == 3


def test_a_followed_artists_unexpected_release_is_unmatched_not_unknown(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    write_album(library, "Nils Frahm", "Felt (2011)", tracks=2, year="2011")
    result = scan(session_maker, settings)

    titles = [row["title"] for row in result.unmatched]
    assert titles == ["Felt"]
    assert result.unmatched[0]["artist_id"] == "100"
    assert result.unknown_artist_count == 1  # still just Stranger


def test_the_summary_reads_as_a_sentence(session_maker: Any, settings: Settings) -> None:
    result = scan(session_maker, settings)
    summary = result.summary()
    assert "matched" in summary
    assert "newly marked downloaded" in summary


def test_the_result_is_stored_and_can_be_read_back(
    session_maker: Any, settings: Settings
) -> None:
    result = scan(session_maker, settings)

    async def stored() -> dict[str, Any] | None:
        async with session_maker() as session:
            return await load_last_scan(session)

    data = asyncio.run(stored())
    assert data is not None
    assert data["albums_adopted"] == result.albums_adopted
    assert data["summary"] == result.summary()


def test_a_corrupt_stored_summary_reads_as_absent(session_maker: Any) -> None:
    """A row written by an older build must not 500 the page that shows it."""

    async def run() -> dict[str, Any] | None:
        async with session_maker() as session:
            session.add(Setting(key="library.last_scan", value="{not json"))
            await session.commit()
            return await load_last_scan(session)

    assert asyncio.run(run()) is None


# ---------------------------------------------------------------------------
# Track rows
# ---------------------------------------------------------------------------
def test_existing_track_rows_are_pointed_at_the_files(
    session_maker: Any, settings: Settings
) -> None:
    async def add_tracks() -> None:
        async with session_maker() as session:
            for number in range(1, 5):
                session.add(
                    Track(
                        id=f"t{number}",
                        album_id="a1",
                        title=f"Track {number}",
                        track_number=number,
                        media_number=1,
                    )
                )
            await session.commit()

    asyncio.run(add_tracks())
    result = scan(session_maker, settings)

    async def tracks() -> list[Track]:
        async with session_maker() as session:
            rows = await session.execute(select(Track).order_by(Track.track_number))
            return list(rows.scalars().all())

    rows = asyncio.run(tracks())
    assert result.tracks_linked == 4
    assert all(track.status is TrackStatus.DOWNLOADED for track in rows)
    assert all(track.path and track.path.endswith(".flac") for track in rows)
    assert rows[0].sampling_rate == pytest.approx(44.1)
    assert rows[0].bit_depth == 16


def test_a_scan_cannot_invent_track_rows(
    session_maker: Any, settings: Settings
) -> None:
    """Track ids are Qobuz ids; a file the database never saw cannot become one."""
    result = scan(session_maker, settings)

    async def count() -> int:
        async with session_maker() as session:
            rows = await session.execute(select(Track))
            return len(rows.scalars().all())

    assert asyncio.run(count()) == 0
    assert result.tracks_linked == 0


# ---------------------------------------------------------------------------
# Restricting to one artist
# ---------------------------------------------------------------------------
def test_scanning_one_artist_leaves_everyone_else_untouched(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings, artist_id="100", root=settings.library_path)
    rows = albums(session_maker)

    assert rows["a1"].status is AlbumStatus.DOWNLOADED
    assert rows["a3"].status is AlbumStatus.WANTED  # Alice Coltrane, not asked for


def test_scanning_an_unknown_artist_is_a_lookup_error(
    session_maker: Any, settings: Settings
) -> None:
    with pytest.raises(LookupError):
        scan(session_maker, settings, artist_id="does-not-exist")


# ---------------------------------------------------------------------------
# The nightly job
# ---------------------------------------------------------------------------
def run_housekeeping(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch, **kwargs: Any
) -> dict[str, Any]:
    """Call :func:`app.core.scheduler.housekeeping` against the fixtures."""
    from app.core import scanner as scanner_module, scheduler

    # Both modules reach for the process-wide settings; without this the nightly
    # job would walk the developer's real LIBRARY_PATH.
    monkeypatch.setattr(scanner_module, "get_settings", lambda: settings)
    monkeypatch.setattr(scheduler, "get_settings", lambda: settings)

    from contextlib import asynccontextmanager

    @asynccontextmanager
    async def factory() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    return asyncio.run(scheduler.housekeeping(factory, **kwargs))


def test_housekeeping_scans_the_library_by_default(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    summary = run_housekeeping(session_maker, settings, monkeypatch)
    assert summary["albums_adopted"] >= 3
    assert albums(session_maker)["a1"].status is AlbumStatus.DOWNLOADED


def test_housekeeping_can_be_told_not_to_scan(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    summary = run_housekeeping(session_maker, settings, monkeypatch, scan_library=False)
    assert summary["albums_adopted"] == 0
    assert albums(session_maker)["a1"].status is AlbumStatus.WANTED


def test_the_setting_switches_the_nightly_scan_off(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    off = settings.model_copy(update={"library_scan_nightly": False})
    summary = run_housekeeping(session_maker, off, monkeypatch)
    assert summary["albums_adopted"] == 0


def test_the_verify_pass_does_not_undo_the_scan(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Adopt first, then check for missing files — never the other way round."""

    async def add_tracks() -> None:
        async with session_maker() as session:
            for number in range(1, 5):
                session.add(
                    Track(
                        id=f"t{number}",
                        album_id="a1",
                        title=f"Track {number}",
                        track_number=number,
                        media_number=1,
                    )
                )
            await session.commit()

    asyncio.run(add_tracks())
    summary = run_housekeeping(session_maker, settings, monkeypatch)

    assert summary["tracks_missing"] == 0
    assert albums(session_maker)["a1"].status is AlbumStatus.DOWNLOADED


# ---------------------------------------------------------------------------
# HTTP surface
# ---------------------------------------------------------------------------
@pytest.fixture(name="client")
def client_fixture(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> Iterator[TestClient]:
    """A TestClient whose session and scanner both point at the fixtures."""

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    scanner = LibraryScanner(settings=settings)
    monkeypatch.setattr(routes_api, "get_library_scanner", lambda: scanner)
    monkeypatch.setattr("app.api.deps.get_library_scanner", lambda: scanner)
    monkeypatch.setattr("app.api.deps.get_settings", lambda: settings)

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)


def test_the_api_runs_a_scan_and_reports_it(client: TestClient) -> None:
    body = client.post("/api/library/scan").json()
    assert body["albums_adopted"] >= 3
    assert body["applied"] is True
    assert body["summary"]


def test_the_api_dry_run_flag_is_honoured(client: TestClient) -> None:
    body = client.post("/api/library/scan?dry_run=true").json()
    assert body["applied"] is False
    assert client.get("/api/albums/a1").json()["status"] == "wanted"


def test_the_api_returns_the_last_scan(client: TestClient) -> None:
    client.post("/api/library/scan")
    body = client.get("/api/library/scan").json()
    assert body["running"] is False
    assert body["last"]["albums_adopted"] >= 3


def test_the_last_scan_is_null_before_anything_ran(client: TestClient) -> None:
    assert client.get("/api/library/scan").json()["last"] is None


def test_scanning_an_unknown_artist_is_a_404(client: TestClient) -> None:
    response = client.post("/api/artists/nope/library-scan")
    assert response.status_code == 404


def test_the_page_renders_before_any_scan(client: TestClient) -> None:
    body = client.get("/library/scan").text
    assert "Nothing scanned yet" in body
    assert "Disk scan" in body
    assert 'hx-post="/ui/library/scan"' in body


def test_the_page_shows_the_report_after_a_scan(client: TestClient) -> None:
    client.post("/api/library/scan")
    body = client.get("/library/scan").text
    assert "newly marked downloaded" in body
    assert "Stranger" in body  # the unknown artist
    assert "/add?q=Stranger" in body


def test_the_ui_action_returns_a_fragment_with_a_toast(client: TestClient) -> None:
    response = client.post("/ui/library/scan", headers={"HX-Request": "true"})
    assert response.status_code == 200
    assert "<html" not in response.text.lower()
    trigger = json.loads(response.headers["HX-Trigger"])
    assert "matched" in trigger["qobuzarr:toast"]["message"]


def test_the_ui_preview_button_really_previews(client: TestClient) -> None:
    """dry_run travels as a query parameter; a Form() binding would drop it."""
    response = client.post(
        "/ui/library/scan?dry_run=true", headers={"HX-Request": "true"}
    )
    assert response.status_code == 200
    assert "Dry run" in response.text
    assert client.get("/api/albums/a1").json()["status"] == "wanted"


def test_the_nav_links_to_the_disk_scan_page(client: TestClient) -> None:
    assert 'href="/library/scan"' in client.get("/partials/nav").text
