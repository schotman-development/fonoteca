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
import os
from pathlib import Path
from types import SimpleNamespace
from typing import Any, AsyncIterator, Iterator

import mutagen
import pytest
from fastapi.testclient import TestClient
from mutagen.id3 import ID3, TIT2, TRCK, TSRC, TXXX, UFID
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.api import routes_api
from app.config import Settings, get_settings
from app.core import scanner as scanner_module
from app.core import state as state_module
from app.core.integrity import album_content_digest, hash_file
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
    EnrichmentSource,
    EnrichmentState,
    FileClaim,
    QID_ARTIST_PREFIX,
    QID_RELEASE_PREFIX,
    QID_TRACK_PREFIX,
    QueueItem,
    QueueState,
    Setting,
    Track,
    TrackMetadata,
    TrackOrigin,
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


#: MPEG-1 Layer III, 128 kbps, 44100 Hz, stereo, no padding — 417 bytes a frame.
#: The same silent frame :mod:`tests.test_integrity` uses, repeated: mutagen parses
#: it for real, which is the only way to exercise the ID3 half of tag reading.
_MP3_FRAME = bytes([0xFF, 0xFB, 0x90, 0x00]) + b"\x00" * 413


def write_mp3(path: Path, *, frames: int = 40) -> Path:
    """Write a genuine (silent) MPEG-1 Layer III file with no tags yet."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(_MP3_FRAME * frames)
    return path


def tag_album(directory: Path, **tags: str) -> None:
    """Add raw Vorbis comments to every FLAC in *directory*.

    Written through the non-easy interface on purpose: the identifiers Picard
    writes (``MUSICBRAINZ_ALBUMID`` and friends) are not keys the easy interface
    knows, and they are exactly the ones the scanner has to read.
    """
    for path in sorted(directory.glob("*.flac")):
        audio = mutagen.File(str(path))
        for key, value in tags.items():
            audio[key] = [value]
        audio.save()


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


async def tracks_of(
    maker: async_sessionmaker[AsyncSession], album_id: str
) -> list[Track]:
    """One album's track rows, in playing order."""
    async with maker() as session:
        rows = await session.execute(
            select(Track)
            .where(Track.album_id == album_id)
            .order_by(Track.media_number, Track.track_number)
        )
        return list(rows.scalars().all())


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
    assert "empty file" in errors[0]
    assert stats["unreadable"] == 1
    # It is *kept*, not discarded. Dropping it is what made 30 zero-byte files
    # in one real library invisible to the whole application: no track row, so
    # nothing to fingerprint and nowhere for a corruption verdict to land. The
    # two good files are still scanned normally beside it.
    assert found[0].file_count == 3
    broken = [track for track in found[0].tracks if track.unreadable]
    assert [track.path.name for track in broken] == ["03 - Broken.flac"]
    assert all(not track.unreadable for track in found[0].tracks if track not in broken)


def test_the_album_around_an_unreadable_file_is_still_derived_from_the_good_ones(
    tmp_path: Path,
) -> None:
    # An unreadable file carries no tags at all, so it must contribute nothing
    # to the title, the year or the artist candidates — otherwise one empty file
    # could rename the release it sits in.
    root = tmp_path / "music"
    write_album(root, "Nils Frahm", "All Melody", tracks=2)
    (root / "Nils Frahm" / "All Melody" / "03 - Broken.flac").write_bytes(b"")

    found, _ = collect_albums(root)
    assert found[0].title == "All Melody"
    assert "Nils Frahm" in found[0].artist_candidates


def test_read_track_marks_something_that_is_not_audio_unreadable(
    tmp_path: Path,
) -> None:
    junk = tmp_path / "not-really.flac"
    junk.write_bytes(b"this is not a FLAC stream")

    track = read_track(junk)
    # Present, stable, and nothing can parse it: a fact about the file, so it
    # gets a row for the verdict to live on rather than being dropped.
    assert track is not None
    assert track.unreadable is True
    assert track.file_size == len(b"this is not a FLAC stream")


def test_read_track_returns_none_when_the_file_cannot_be_reached(
    tmp_path: Path,
) -> None:
    # The other half of the split, and the one that matters for safety: a path
    # that does not resolve is a fact about the *mount*, not about the audio.
    # Recording it as corruption is how a share that blipped for a minute puts a
    # healthy release on the Integrity screen.
    assert read_track(tmp_path / "never-existed.flac") is None


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

    rows = [track for track in asyncio.run(tracks()) if track.album_id == "a1"]
    assert [track.id for track in rows] == ["t1", "t2", "t3", "t4"]
    # Offered to the existing rows first: a row the download loop wrote knows the
    # Qobuz track id and the delivered format, which no file does. Matching one
    # and creating a second for the same file would double the album.
    assert all(track.origin is TrackOrigin.DOWNLOAD for track in rows)
    assert all(track.status is TrackStatus.DOWNLOADED for track in rows)
    assert all(track.path and track.path.endswith(".flac") for track in rows)
    assert rows[0].sampling_rate == pytest.approx(44.1)
    assert rows[0].bit_depth == 16


def test_a_scan_records_the_files_it_finds(
    session_maker: Any, settings: Settings
) -> None:
    """A release Qobuzarr never downloaded still gets track rows.

    The rule used to be the opposite — track ids were Qobuz ids, so a file the
    database had never seen could not become a row — and the cost was that every
    adopted album was an opaque folder: nothing to fingerprint, no per-file
    quality to compare, and nowhere for a corruption verdict to land.
    """
    result = scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a1"))
    assert len(rows) == 4
    assert result.tracks_linked == 13  # every file under a matched album
    assert all(track.origin is TrackOrigin.SCAN for track in rows)
    assert all(track.id.startswith("scan:") and len(track.id) <= 64 for track in rows)
    assert all(track.status is TrackStatus.DOWNLOADED for track in rows)
    assert all(track.path and Path(track.path).is_file() for track in rows)
    # Read off the files themselves, which is the whole point: this is what
    # app.core.quality needs to answer "is a better copy obtainable?".
    assert all(track.bit_depth == 16 for track in rows)
    assert all(track.sampling_rate == pytest.approx(44.1) for track in rows)


def test_rescanning_an_unchanged_library_reuses_the_same_rows(
    session_maker: Any, settings: Settings
) -> None:
    """Ids are keyed on position, not path, so a rescan converges.

    If they churned, every fingerprint verdict and recording id in
    ``track_metadata`` would be deleted and recreated on each nightly scan.
    """
    scan(session_maker, settings)
    first = {track.id for track in asyncio.run(tracks_of(session_maker, "a1"))}

    scan(session_maker, settings)
    second = asyncio.run(tracks_of(session_maker, "a1"))

    assert {track.id for track in second} == first
    assert len(second) == 4


def test_a_refiled_album_keeps_its_track_rows(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    """Re-filing renames every file in an album — the rows must survive it.

    ``librarian.plan_refile()`` does exactly this whenever the quality tag in the
    folder template changes, so a path-keyed id would throw the album's rows away
    on the next scan.
    """
    scan(session_maker, settings)
    before = {track.id for track in asyncio.run(tracks_of(session_maker, "a1"))}

    album_dir = library / "Nils Frahm" / "All Melody (2018)"
    for path in sorted(album_dir.glob("*.flac")):
        path.rename(path.with_name(f"renamed {path.name}"))

    scan(session_maker, settings)
    after = asyncio.run(tracks_of(session_maker, "a1"))

    assert {track.id for track in after} == before
    assert all("renamed" in Path(str(track.path)).name for track in after)


def test_a_file_that_disappears_retires_its_scan_row(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    """A library that shrank must stop reporting the tracks it lost."""
    scan(session_maker, settings)
    assert len(asyncio.run(tracks_of(session_maker, "a1"))) == 4

    album_dir = library / "Nils Frahm" / "All Melody (2018)"
    sorted(album_dir.glob("*.flac"))[-1].unlink()
    scan(session_maker, settings)

    assert len(asyncio.run(tracks_of(session_maker, "a1"))) == 3


def test_a_missing_file_never_retires_a_downloaded_row(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    """Only ``scan`` rows are retired.

    A ``download`` row is the record of work this program did. An unmounted share
    looks exactly like a deleted one, and erasing the evidence per-file would make
    it unrecoverable — demoting is ``scheduler._verify_library``'s job, at the
    album level, where it reverses.
    """

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
    for path in sorted((library / "Nils Frahm" / "All Melody (2018)").glob("*.flac")):
        path.unlink()
    scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a1"))
    assert [track.id for track in rows] == ["t1", "t2", "t3", "t4"]


# ---------------------------------------------------------------------------
# Reading the identifiers that are already in the files
# ---------------------------------------------------------------------------
#: The full Picard set, in Vorbis spelling. ``MUSICBRAINZ_TRACKID`` is the
#: *recording* mbid; ``MUSICBRAINZ_RELEASETRACKID`` is a different thing entirely
#: and must not be mistaken for one.
VORBIS_CLAIMS = {
    "ISRC": "GBAYE1234567",
    "BARCODE": "0602577626296",
    "MUSICBRAINZ_TRACKID": "11111111-1111-1111-1111-111111111111",
    "MUSICBRAINZ_RELEASETRACKID": "99999999-9999-9999-9999-999999999999",
    "MUSICBRAINZ_ALBUMID": "22222222-2222-2222-2222-222222222222",
    "MUSICBRAINZ_RELEASEGROUPID": "33333333-3333-3333-3333-333333333333",
    "MUSICBRAINZ_ARTISTID": "44444444-4444-4444-4444-444444444444",
    "MUSICBRAINZ_ALBUMARTISTID": "55555555-5555-5555-5555-555555555555",
    "QOBUZARR_QID": "qt_0123456789abcdef01234567",
}


def write_id3(path: Path, **frames: str) -> None:
    """Write Picard's ID3 spellings onto *path*.

    ``UFID`` carries its value as raw bytes rather than as text, which is the one
    frame a naive ``str()`` turns into a plausible-looking wrong answer.
    """
    tags = ID3()
    tags.add(TIT2(encoding=3, text=[frames.get("title", "Track 1")]))
    tags.add(TRCK(encoding=3, text=["1"]))
    tags.add(TSRC(encoding=3, text=[frames["isrc"]]))
    tags.add(UFID(owner="http://musicbrainz.org", data=frames["recording"].encode()))
    tags.add(TXXX(encoding=3, desc="BARCODE", text=[frames["barcode"]]))
    tags.add(TXXX(encoding=3, desc="MusicBrainz Album Id", text=[frames["release"]]))
    tags.add(
        TXXX(encoding=3, desc="MusicBrainz Release Group Id", text=[frames["group"]])
    )
    tags.add(TXXX(encoding=3, desc="MusicBrainz Artist Id", text=[frames["artist"]]))
    tags.add(
        TXXX(encoding=3, desc="MusicBrainz Album Artist Id", text=[frames["albumartist"]])
    )
    tags.add(TXXX(encoding=3, desc="QOBUZARR_QID", text=[frames["qid"]]))
    tags.save(str(path))


def test_vorbis_comments_are_read_into_claims(tmp_path: Path) -> None:
    """A Picard-tagged FLAC: 126 of 137 files in the real library look like this."""
    path = write_flac(tmp_path / "a.flac", title="Track 1", tracknumber="1")
    tag_album(tmp_path, **VORBIS_CLAIMS)

    claims = read_track(path).claims()
    assert claims == {
        "isrc": "GBAYE1234567",
        "barcode": "0602577626296",
        "mb_recording_mbid": "11111111-1111-1111-1111-111111111111",
        "mb_release_mbid": "22222222-2222-2222-2222-222222222222",
        "mb_release_group_mbid": "33333333-3333-3333-3333-333333333333",
        "mb_artist_mbid": "44444444-4444-4444-4444-444444444444",
        "mb_album_artist_mbid": "55555555-5555-5555-5555-555555555555",
        "qobuzarr_qid": "qt_0123456789abcdef01234567",
    }


def test_id3_frames_are_read_into_the_same_claims(tmp_path: Path) -> None:
    """The ID3 spellings are different names for the same eight facts."""
    path = write_mp3(tmp_path / "a.mp3")
    write_id3(
        path,
        isrc="GBAYE7654321",
        recording="aaaaaaaa-1111-1111-1111-111111111111",
        barcode="0602577626297",
        release="bbbbbbbb-2222-2222-2222-222222222222",
        group="cccccccc-3333-3333-3333-333333333333",
        artist="dddddddd-4444-4444-4444-444444444444",
        albumartist="eeeeeeee-5555-5555-5555-555555555555",
        qid="qt_fedcba9876543210fedcba98",
    )

    claims = read_track(path).claims()
    assert claims["isrc"] == "GBAYE7654321"
    assert claims["barcode"] == "0602577626297"
    assert claims["mb_release_mbid"] == "bbbbbbbb-2222-2222-2222-222222222222"
    assert claims["mb_release_group_mbid"] == "cccccccc-3333-3333-3333-333333333333"
    assert claims["mb_artist_mbid"] == "dddddddd-4444-4444-4444-444444444444"
    assert claims["mb_album_artist_mbid"] == "eeeeeeee-5555-5555-5555-555555555555"
    assert claims["qobuzarr_qid"] == "qt_fedcba9876543210fedcba98"


def test_the_ufid_frame_is_decoded_not_stringified(tmp_path: Path) -> None:
    """``str(UFID(...))`` is the frame's repr, and it looks enough like a value.

    Storing that would put ``UFID(owner='http://musicbrainz.org', data=b'…')``
    in a column every consumer reads as a recording mbid.
    """
    path = write_mp3(tmp_path / "a.mp3")
    write_id3(
        path,
        isrc="GBAYE7654321",
        recording="aaaaaaaa-1111-1111-1111-111111111111",
        barcode="0602577626297",
        release="bbbbbbbb-2222-2222-2222-222222222222",
        group="cccccccc-3333-3333-3333-333333333333",
        artist="dddddddd-4444-4444-4444-444444444444",
        albumartist="eeeeeeee-5555-5555-5555-555555555555",
        qid="qt_fedcba9876543210fedcba98",
    )

    assert read_track(path).mb_recording_mbid == "aaaaaaaa-1111-1111-1111-111111111111"


def test_the_release_track_id_is_never_read_as_a_recording_id(tmp_path: Path) -> None:
    """It identifies a slot on one release, which is not what a recording is.

    Principle three of the integrity model: never use a value as something it is
    not. There is no column for it, so it is dropped rather than repurposed.
    """
    path = write_flac(tmp_path / "a.flac", title="Track 1", tracknumber="1")
    tag_album(
        tmp_path,
        MUSICBRAINZ_RELEASETRACKID="99999999-9999-9999-9999-999999999999",
    )

    claims = read_track(path).claims()
    assert claims["mb_recording_mbid"] is None
    assert "99999999-9999-9999-9999-999999999999" not in str(claims)


def test_upc_is_accepted_as_a_barcode_spelling(tmp_path: Path) -> None:
    path = write_flac(tmp_path / "a.flac", title="Track 1", tracknumber="1")
    tag_album(tmp_path, UPC="0602577626296")
    assert read_track(path).barcode == "0602577626296"


@pytest.mark.parametrize(
    "value",
    [
        "not an identifier at all",  # whitespace: no id has any
        "x" * 65,  # longer than the column that would hold it
        "   ",
    ],
)
def test_something_that_cannot_be_an_identifier_is_not_a_claim(
    tmp_path: Path, value: str
) -> None:
    """A value that will not fit a ``String(64)`` was never a barcode with a typo."""
    path = write_flac(tmp_path / "a.flac", title="Track 1", tracknumber="1")
    tag_album(tmp_path, BARCODE=value)
    assert read_track(path).barcode == ""


def test_a_file_with_no_identifiers_claims_nothing(library: Path) -> None:
    """The untagged case is the common one and must stay silent, not empty-string."""
    path = sorted((library / "Nils Frahm" / "All Melody (2018)").glob("*.flac"))[0]
    assert not any(read_track(path).claims().values())


# ---------------------------------------------------------------------------
# Integrity stamps
# ---------------------------------------------------------------------------
def test_reading_a_track_measures_the_cheap_things(library: Path) -> None:
    """Size, mtime and sample count all come out of the walk for free."""
    path = sorted((library / "Nils Frahm" / "All Melody (2018)").glob("*.flac"))[0]
    scanned = read_track(path)

    assert scanned.file_size == path.stat().st_size
    assert scanned.file_mtime == pytest.approx(path.stat().st_mtime)
    assert scanned.sample_count == 44100 * 200


def test_reading_a_track_does_not_hash_unless_asked(library: Path) -> None:
    """The default is the whole performance argument: 201 ms a file, times 30 000."""
    path = sorted((library / "Nils Frahm" / "All Melody (2018)").glob("*.flac"))[0]

    assert read_track(path).content_hash is None
    assert read_track(path, content_hash=True).content_hash is not None


def test_the_hash_is_of_the_whole_file(library: Path) -> None:
    path = sorted((library / "Nils Frahm" / "All Melody (2018)").glob("*.flac"))[0]
    assert read_track(path, content_hash=True).content_hash == hash_file(path)


def hashed_paths(monkeypatch: pytest.MonkeyPatch) -> list[Path]:
    """Record every file the scanner opens to hash. The list is live."""
    calls: list[Path] = []
    real = scanner_module.hash_file

    def spy(path: Path) -> str:
        calls.append(Path(path))
        return real(path)

    monkeypatch.setattr(scanner_module, "hash_file", spy)
    return calls


def test_the_first_scan_hashes_everything_it_adopts(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """No baseline is UNKNOWN, and UNKNOWN is resolved by measuring."""
    hashed = hashed_paths(monkeypatch)
    scan(session_maker, settings)
    assert len(hashed) == 13  # every file under a matched album


def test_an_unchanged_rescan_opens_no_files_at_all(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The tripwire is what makes the nightly scan runnable.

    Hashing unconditionally is 201 ms a file — an hour and a half over 30 000
    files, every night, to learn that nothing changed.
    """
    scan(session_maker, settings)
    hashed = hashed_paths(monkeypatch)
    scan(session_maker, settings)
    assert hashed == []


def test_only_the_file_that_changed_is_hashed_again(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    scan(session_maker, settings)

    changed = sorted((settings.library_path / "Nils Frahm" / "All Melody (2018)").glob("*.flac"))[0]
    write_flac(changed, seconds=190)

    hashed = hashed_paths(monkeypatch)
    scan(session_maker, settings)
    assert hashed == [changed]


def test_an_mtime_that_moved_for_no_reason_costs_exactly_one_hash(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Over-eagerness in this direction is free; the hash then agrees."""
    scan(session_maker, settings)

    touched = sorted((settings.library_path / "Nils Frahm" / "All Melody (2018)").glob("*.flac"))[0]
    os.utime(touched, (1_600_000_000, 1_600_000_000))

    hashed = hashed_paths(monkeypatch)
    scan(session_maker, settings)
    assert hashed == [touched]

    hashed.clear()
    scan(session_maker, settings)
    assert hashed == []


def test_the_stamps_are_written_to_the_track_rows(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings)
    rows = asyncio.run(tracks_of(session_maker, "a1"))

    assert all(row.content_hash and len(row.content_hash) == 32 for row in rows)
    assert all(row.sample_count == 44100 * 200 for row in rows)
    assert all(row.file_mtime for row in rows)
    assert all(row.verified_at is not None for row in rows)
    assert len({row.content_hash for row in rows}) == len(rows)  # distinct files


def test_verified_at_only_moves_when_the_file_was_really_opened(
    session_maker: Any, settings: Settings
) -> None:
    """A tripwire that did not fire is a decision not to look, not a verification."""
    scan(session_maker, settings)
    first = {row.id: row.verified_at for row in asyncio.run(tracks_of(session_maker, "a1"))}

    scan(session_maker, settings)
    second = {row.id: row.verified_at for row in asyncio.run(tracks_of(session_maker, "a1"))}

    assert second == first


def test_the_release_gets_a_digest_over_its_members(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings)
    rows = asyncio.run(tracks_of(session_maker, "a1"))
    album = albums(session_maker)["a1"]

    assert album.content_digest == album_content_digest([row.content_hash for row in rows])


def test_the_release_digest_moves_when_a_member_file_changes(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    scan(session_maker, settings)
    before = albums(session_maker)["a1"].content_digest

    write_flac(sorted((library / "Nils Frahm" / "All Melody (2018)").glob("*.flac"))[0], seconds=190)
    scan(session_maker, settings)

    assert albums(session_maker)["a1"].content_digest not in (None, before)


def test_the_release_digest_is_stable_across_an_unchanged_rescan(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings)
    before = albums(session_maker)["a1"].content_digest
    scan(session_maker, settings)
    assert albums(session_maker)["a1"].content_digest == before


# ---------------------------------------------------------------------------
# The scan re-baselines, so the scan has to be the one that classifies
# ---------------------------------------------------------------------------
#: The first file of the album the fixture database calls ``a1``.
def first_file(library: Path) -> Path:
    return sorted((library / "Nils Frahm" / "All Melody (2018)").glob("*.flac"))[0]


def replace_audio(path: Path, *, seconds: int = 100) -> None:
    """A re-rip: different audio, same tags, same filename.

    The tags are carried across on purpose. A file that lost its track number
    would be paired differently on the next scan and get a new row, which is a
    different scenario entirely — this one is about the row that stays.
    """
    tags = {
        key: str(value[0])
        for key, value in mutagen.File(str(path), easy=True).items()
    }
    write_flac(path, seconds=seconds, **tags)


@pytest.fixture(name="running_enricher")
def running_enricher_fixture(
    session_maker: Any, monkeypatch: pytest.MonkeyPatch
) -> Any:
    """An enricher reachable through ``app.core.state``, as in production.

    Re-opening a release for identification looks for one there rather than
    being plumbed through the scanner, so without this the interesting half of
    the verdict is silently a no-op.
    """
    from app.core.enricher import Enricher  # noqa: PLC0415 - only these tests need it
    from tests.test_enrichment_scope import FakeProvider  # noqa: PLC0415

    enricher = Enricher(
        [FakeProvider(EnrichmentSource.DEEZER)],
        settings=get_settings().model_copy(update={"enrichment_sources": "deezer"}),
        session_factory=session_maker,
    )
    monkeypatch.setattr(
        state_module, "_state", SimpleNamespace(enricher=enricher), raising=False
    )
    return enricher


def enrichment_states(maker: async_sessionmaker[AsyncSession]) -> dict[str, str]:
    """Every ``enrichment_state`` row as ``entity_id -> state``."""

    async def run() -> dict[str, str]:
        async with maker() as session:
            rows = (await session.execute(select(EnrichmentState))).scalars().all()
            return {str(row.entity_id): str(row.state) for row in rows}

    return asyncio.run(run())


def mark_identified(maker: async_sessionmaker[AsyncSession]) -> None:
    """Settle every state row at ``ok``, which is what a matched release looks like.

    It is also the state that makes the difference visible: ``mark_due`` moves
    ``pending``/``not_found``/``failed`` and nothing else, so a re-open that went
    through it would leave these exactly as they are while reporting otherwise.
    """

    async def run() -> None:
        async with maker() as session:
            rows = (await session.execute(select(EnrichmentState))).scalars().all()
            for row in rows:
                row.state = "ok"
            await session.commit()

    asyncio.run(run())


def test_a_replaced_file_is_reported_by_the_scan_that_re_baselines_it(
    session_maker: Any, settings: Settings, library: Path, running_enricher: Any
) -> None:
    """The scan overwrites the baseline, so the scan is where the verdict lives.

    Housekeeping runs the disk scan first and the integrity rotation last, and
    the rotation covers 1/30 of the library a night — so a scan that re-stamped
    silently destroyed the evidence for any change that moved size or mtime,
    which is essentially all of them. The release then kept its release match,
    its recording ids and its fingerprint verdicts for audio that is gone.
    """
    scan(session_maker, settings)
    mark_identified(session_maker)
    replace_audio(first_file(library))

    result = scan(session_maker, settings)

    assert result.files_measured == 1
    assert result.files_replaced == 1
    assert result.albums_reopened == 1
    assert enrichment_states(session_maker)["a1"] == "pending"


def test_a_retagged_file_is_re_stamped_and_nothing_is_re_identified(
    session_maker: Any, settings: Settings, library: Path, running_enricher: Any
) -> None:
    """Same audio, new container: the identification still holds."""
    scan(session_maker, settings)
    mark_identified(session_maker)
    audio = mutagen.File(str(first_file(library)))
    audio["comment"] = ["ripped by somebody else"]
    audio.save()

    result = scan(session_maker, settings)

    assert result.files_retagged == 1
    assert result.files_replaced == 0
    assert result.albums_reopened == 0
    assert enrichment_states(session_maker)["a1"] == "ok"


def test_a_hash_that_could_not_be_read_leaves_the_whole_stamp_alone(
    session_maker: Any,
    settings: Settings,
    library: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The four columns are one statement about one read, or they are nothing.

    Writing a fresh size, mtime and sample count beside a stale ``content_hash``
    silences the tripwire permanently — every later pass compares size and mtime,
    finds them equal, and never opens the file again. Worse, the sample count it
    would eventually be compared against has already been copied from the *new*
    file, so a replacement classifies as a retag and the release is never
    re-identified.
    """
    scan(session_maker, settings)
    target = first_file(library)
    before = {row.id: row for row in asyncio.run(tracks_of(session_maker, "a1"))}
    recorded = next(row for row in before.values() if row.path == str(target))
    stamp = (
        recorded.content_hash,
        recorded.sample_count,
        recorded.file_size,
        recorded.file_mtime,
    )

    replace_audio(target)
    monkeypatch.setattr(scanner_module, "_hash_files", lambda paths: {})
    scan(session_maker, settings)

    rows = {row.id: row for row in asyncio.run(tracks_of(session_maker, "a1"))}
    unread = rows[recorded.id]
    assert (
        unread.content_hash,
        unread.sample_count,
        unread.file_size,
        unread.file_mtime,
    ) == stamp, "half a measurement is worse than none"

    # And the proof that it is not merely stale: the tripwire fires again.
    monkeypatch.undo()
    result = scan(session_maker, settings)
    assert result.files_replaced == 1


# ---------------------------------------------------------------------------
# qids — minted once, never reissued
# ---------------------------------------------------------------------------
def test_every_row_the_scan_touches_gets_a_qid(
    session_maker: Any, settings: Settings
) -> None:
    scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a1"))
    assert all(row.qid.startswith(QID_TRACK_PREFIX) for row in rows)
    assert len({row.qid for row in rows}) == len(rows)
    assert albums(session_maker)["a1"].qid.startswith(QID_RELEASE_PREFIX)


def test_a_rescan_never_mints_a_new_qid(
    session_maker: Any, settings: Settings
) -> None:
    """The load-bearing rule of the whole integrity model.

    Every measurement made about a file hangs off its qid. Re-minting one on a
    rescan orphans all of it silently — the library would quietly forget its
    fingerprints every night, with nothing to see in a log.
    """
    scan(session_maker, settings)
    before = {row.id: row.qid for row in asyncio.run(tracks_of(session_maker, "a1"))}
    album_qid = albums(session_maker)["a1"].qid

    scan(session_maker, settings)
    scan(session_maker, settings)

    after = {row.id: row.qid for row in asyncio.run(tracks_of(session_maker, "a1"))}
    assert after == before
    assert albums(session_maker)["a1"].qid == album_qid


def test_a_refiled_album_keeps_its_qids(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    """Re-filing renames every file; the identity of the tracks does not change."""
    scan(session_maker, settings)
    before = {row.id: row.qid for row in asyncio.run(tracks_of(session_maker, "a1"))}

    for path in sorted((library / "Nils Frahm" / "All Melody (2018)").glob("*.flac")):
        path.rename(path.with_name(f"renamed {path.name}"))
    scan(session_maker, settings)

    after = {row.id: row.qid for row in asyncio.run(tracks_of(session_maker, "a1"))}
    assert after == before


def test_a_row_that_already_has_a_qid_keeps_it(
    session_maker: Any, settings: Settings
) -> None:
    """A download-loop row arrives with its own qid, and the scan must not touch it."""

    async def add_tracks() -> None:
        async with session_maker() as session:
            for number in range(1, 5):
                session.add(
                    Track(
                        id=f"t{number}",
                        qid=f"{QID_TRACK_PREFIX}{number:024d}",
                        album_id="a1",
                        title=f"Track {number}",
                        track_number=number,
                        media_number=1,
                    )
                )
            await session.commit()

    asyncio.run(add_tracks())
    scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a1"))
    assert [row.qid for row in rows] == [f"{QID_TRACK_PREFIX}{n:024d}" for n in range(1, 5)]


def test_the_artist_gets_a_qid_too(session_maker: Any, settings: Settings) -> None:
    """Identity propagates upward, so the artist needs an anchor of its own."""
    scan(session_maker, settings)

    async def artist() -> Artist:
        async with session_maker() as session:
            return await session.get(Artist, "100")

    assert asyncio.run(artist()).qid.startswith(QID_ARTIST_PREFIX)


# ---------------------------------------------------------------------------
# File claims
# ---------------------------------------------------------------------------
def claims_of(maker: async_sessionmaker[AsyncSession]) -> dict[str, FileClaim]:
    """Every ``file_claims`` row, keyed by track id."""

    async def run() -> dict[str, FileClaim]:
        async with maker() as session:
            rows = await session.execute(select(FileClaim))
            return {row.track_id: row for row in rows.scalars().all()}

    return asyncio.run(run())


def test_the_tags_are_recorded_as_claims(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    tag_album(library / "Nils Frahm" / "All Melody (2018)", **VORBIS_CLAIMS)
    scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a1"))
    claims = claims_of(session_maker)
    assert set(claims) == {row.id for row in rows}

    one = claims[rows[0].id]
    assert one.barcode == "0602577626296"
    assert one.mb_release_mbid == "22222222-2222-2222-2222-222222222222"
    assert one.mb_album_artist_mbid == "55555555-5555-5555-5555-555555555555"
    assert one.qobuzarr_qid == "qt_0123456789abcdef01234567"
    assert one.read_at is not None


def test_a_claim_never_lands_in_the_verified_table(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    """``track_metadata`` holds conclusions; a tag is a hypothesis.

    Once a claim is written where verified ids live it is read back as evidence,
    propagated to the release and to the artist, and written into every other file
    on the next re-tag — and nothing downstream can tell the chain rests on a
    string a stranger typed.
    """
    tag_album(library / "Nils Frahm" / "All Melody (2018)", **VORBIS_CLAIMS)
    scan(session_maker, settings)

    async def metadata() -> list[TrackMetadata]:
        async with session_maker() as session:
            rows = await session.execute(select(TrackMetadata))
            return list(rows.scalars().all())

    assert asyncio.run(metadata()) == []


def test_a_file_claiming_nothing_gets_no_row(
    session_maker: Any, settings: Settings
) -> None:
    """Most of a library is untagged; an empty row per file says nothing."""
    scan(session_maker, settings)
    assert claims_of(session_maker) == {}


def test_a_claim_that_was_removed_from_the_file_is_cleared(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    """Verify the disk before trusting the database — including about itself."""
    album_dir = library / "Nils Frahm" / "All Melody (2018)"
    tag_album(album_dir, **VORBIS_CLAIMS)
    scan(session_maker, settings)
    assert all(row.barcode for row in claims_of(session_maker).values())

    for path in sorted(album_dir.glob("*.flac")):
        audio = mutagen.File(str(path))
        del audio["BARCODE"]
        audio.save()
    scan(session_maker, settings)

    rows = claims_of(session_maker)
    assert all(row.barcode is None for row in rows.values())
    # The rest of what the file still claims is untouched.
    assert all(row.mb_release_mbid for row in rows.values())


def test_a_corrected_claim_is_followed(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    album_dir = library / "Nils Frahm" / "All Melody (2018)"
    tag_album(album_dir, **VORBIS_CLAIMS)
    scan(session_maker, settings)

    tag_album(album_dir, BARCODE="5099951234567")
    scan(session_maker, settings)

    assert all(row.barcode == "5099951234567" for row in claims_of(session_maker).values())


def test_claims_are_read_from_an_album_that_is_only_half_there(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    """A partial release is still on disk, and its tags are still the cheapest hint."""
    tag_album(library / "black midi" / "Hellfire", **VORBIS_CLAIMS)
    scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a4"))
    claims = claims_of(session_maker)
    assert {row.id for row in rows} == set(claims)


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


def test_the_nightly_job_notices_a_release_whose_audio_was_swapped(
    session_maker: Any,
    settings: Settings,
    library: Path,
    monkeypatch: pytest.MonkeyPatch,
    running_enricher: Any,
) -> None:
    """The two halves have to be measured together, or the hole is invisible.

    Housekeeping runs the disk scan **first** and the integrity rotation last,
    and the rotation only reaches 1/30 of the library a night. So a scan that
    re-baselined silently made the verdict unreachable for any change that moved
    size or mtime — the release kept its release match, its recording ids and its
    fingerprint verdicts for audio that had been replaced, and no activity row
    said so. Every other test here measures one side or the other.
    """
    run_housekeeping(session_maker, settings, monkeypatch)
    mark_identified(session_maker)
    replace_audio(first_file(library))

    run_housekeeping(session_maker, settings, monkeypatch)

    assert enrichment_states(session_maker)["a1"] == "pending"


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
    body = client.get("/legacy/library/scan").text
    assert "Nothing scanned yet" in body
    assert "Disk scan" in body
    assert 'hx-post="/ui/library/scan"' in body


def test_the_page_shows_the_report_after_a_scan(client: TestClient) -> None:
    client.post("/api/library/scan")
    body = client.get("/legacy/library/scan").text
    assert "newly marked downloaded" in body
    assert "Stranger" in body  # the unknown artist
    assert "/add?q=Stranger" in body


def test_the_ui_action_returns_a_fragment_with_a_toast(client: TestClient) -> None:
    response = client.post("/legacy/ui/library/scan", headers={"HX-Request": "true"})
    assert response.status_code == 200
    assert "<html" not in response.text.lower()
    trigger = json.loads(response.headers["HX-Trigger"])
    assert "matched" in trigger["qobuzarr:toast"]["message"]


def test_the_ui_preview_button_really_previews(client: TestClient) -> None:
    """dry_run travels as a query parameter; a Form() binding would drop it."""
    response = client.post(
        "/legacy/ui/library/scan?dry_run=true", headers={"HX-Request": "true"}
    )
    assert response.status_code == 200
    assert "Dry run" in response.text
    assert client.get("/api/albums/a1").json()["status"] == "wanted"


def test_the_nav_links_to_the_disk_scan_page(client: TestClient) -> None:
    assert 'href="/library/scan"' in client.get("/legacy/partials/nav").text


# --------------------------------------------------------------- integrity of
# the scan's own transaction
#
# Two defects, one incident. A real 4 704-album library produced
#
#     INSERT INTO file_claims (track_id, ...) VALUES ('scan:acb2076bd9abb732dba00fef', ...)
#     sqlite3.IntegrityError: FOREIGN KEY constraint failed
#
# and then two hundred identical PendingRollbackErrors, one per remaining
# folder, and finally a 500 from POST /api/library/scan. The first is a claim
# outliving the track it hangs off; the second is a whole scan lost to it.
#
# Neither could be caught before, because a test engine does not enforce foreign
# keys: `PRAGMA foreign_keys=ON` is set by the listener in app/db.py on the
# application's own engine, and SQLite's default is OFF. So these build an
# engine that enforces them.


@pytest.fixture(name="fk_session_maker")
def fk_session_maker_fixture(
    tmp_path: Path,
) -> Iterator[async_sessionmaker[AsyncSession]]:
    """A session factory that enforces foreign keys, as the real one does."""
    from sqlalchemy import event

    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'fk.db'}", poolclass=NullPool
    )

    @event.listens_for(engine.sync_engine, "connect")
    def _fk_on(dbapi_connection: Any, _record: Any) -> None:
        cursor = dbapi_connection.cursor()
        cursor.execute("PRAGMA foreign_keys=ON")
        cursor.close()

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


def test_the_test_engine_really_enforces_foreign_keys(
    fk_session_maker: async_sessionmaker[AsyncSession],
) -> None:
    """Guard the guard: without this pragma the two tests below prove nothing."""

    async def run() -> None:
        async with fk_session_maker() as session:
            session.add(FileClaim(track_id="scan:nobody", barcode="1"))
            with pytest.raises(Exception):  # noqa: B017 - IntegrityError, wrapped
                await session.flush()

    asyncio.run(run())


def test_a_retired_track_takes_its_pending_claim_with_it(
    fk_session_maker: async_sessionmaker[AsyncSession], settings: Settings
) -> None:
    """The root cause: a claim must never outlive the track it describes.

    ``file_claims.track_id`` cascades in the database, which settles a track that
    has been written. A track that has *not* is the gap: discarding it from
    ``Album.tracks`` emits no DELETE — there is nothing to delete yet — so its
    claim survived as a pending INSERT against a row that would never exist.
    """

    async def run() -> None:
        async with fk_session_maker() as session:
            album = await session.get(Album, "a1")
            assert album is not None
            await session.refresh(album, ["tracks"])

            track = Track(
                id="scan:doomed",
                album_id="a1",
                title="Doomed",
                track_number=1,
                media_number=1,
                origin=TrackOrigin.SCAN,
                status=TrackStatus.PENDING,
            )
            album.tracks.append(track)
            session.add(FileClaim(track_id="scan:doomed", barcode="5099973515517"))

            instance = LibraryScanner(settings=settings)
            retired = instance._retire_scanned(album, claimed=set())
            assert retired == {"scan:doomed"}

            instance._drop_claims(session, retired)
            assert not [obj for obj in session.new if isinstance(obj, FileClaim)]

            # The flush that used to raise FOREIGN KEY constraint failed.
            await session.flush()
            await session.commit()

        async with fk_session_maker() as session:
            rows = await session.execute(select(FileClaim))
            assert rows.scalars().all() == []

    asyncio.run(run())


def test_one_folder_that_poisons_the_session_does_not_lose_the_scan(
    fk_session_maker: async_sessionmaker[AsyncSession],
    settings: Settings,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The resilience defect: one bad folder used to fail every folder after it.

    The whole walk is one transaction, so an IntegrityError during a flush left
    the session unusable and the remaining albums raised PendingRollbackError in
    turn — then the final commit raised too and the endpoint answered 500, so a
    scan that had matched hundreds of releases stored none of them.
    """
    real_link = LibraryScanner._link_tracks
    poisoned: list[str] = []

    async def link(
        self: LibraryScanner,
        session: AsyncSession,
        album: Album,
        scanned: Any,
        result: Any,
    ) -> int:
        count = await real_link(self, session, album, scanned, result)
        if album.id == "a1" and not poisoned:
            poisoned.append(album.id)
            # Exactly the shape of the live failure: a claim whose track is not
            # and never will be there.
            session.add(FileClaim(track_id="scan:nonexistent", barcode="1"))
        return count

    monkeypatch.setattr(LibraryScanner, "_link_tracks", link)

    async def run() -> Any:
        async with fk_session_maker() as session:
            return await LibraryScanner(settings=settings).scan(session)

    result = asyncio.run(run())

    assert poisoned == ["a1"], "the poisoning branch never ran"
    # The scan completed rather than raising, and said which folder failed.
    assert any("All Melody" in error for error in result.errors)

    # And everything else still landed: Journey is the multi-disc album that is
    # scanned after the failing one.
    rows = albums(fk_session_maker)
    assert rows["a3"].status is AlbumStatus.DOWNLOADED
    assert rows["a1"].status is AlbumStatus.WANTED  # its own writes rolled back


def test_a_track_row_is_written_before_the_claim_that_points_at_it(
    fk_session_maker: async_sessionmaker[AsyncSession],
    settings: Settings,
    library: Path,
) -> None:
    """The root cause: the order of INSERTs inside one flush.

    ``file_claims.track_id`` is a ``ForeignKey``, but there is deliberately no
    ORM relationship from :class:`~app.models.Track` to
    :class:`~app.models.FileClaim`, so the unit of work has no inter-mapper
    dependency to sort on — and a table-level foreign key orders DDL, not the
    INSERTs of a flush. Measured on a real library, the flush emitted
    ``INSERT INTO file_claims`` first and never reached ``INSERT INTO tracks``
    at all: ten correctly-keyed pending Track objects sat in ``session.new``
    while their claims were written against rows that did not exist yet.

    This is ``test_the_tags_are_recorded_as_claims`` run against an engine that
    enforces foreign keys, which is the only difference that matters — the
    seeded albums carry no Track rows, so every file becomes a new ``scan`` row
    and the album is exactly the shape that failed.
    """
    tag_album(library / "Nils Frahm" / "All Melody (2018)", **VORBIS_CLAIMS)

    async def run() -> Any:
        async with fk_session_maker() as session:
            return await LibraryScanner(settings=settings).scan(session)

    result = asyncio.run(run())
    assert result.errors == []

    rows = asyncio.run(tracks_of(fk_session_maker, "a1"))
    assert rows, "the album adopted no track rows at all"

    claims = claims_of(fk_session_maker)
    assert set(claims) == {row.id for row in rows}
    assert claims[rows[0].id].barcode == "0602577626296"


# ---------------------------------------------------------------------------
# The nightly follow pass: the one step that ADDS work
#
# Every other nightly pass only ever reduces what Qobuzarr would do — the scan
# marks albums present but never wanted, the verify pass reverses on the next
# run, the purge deletes a list that rebuilds itself. This one follows artists,
# which is why it is off by default and why each guard below is tested rather
# than assumed.


class FakeImporter:
    def __init__(self, *, running=False, followed=3, review=2, explode=False):
        self.running = running
        self._followed = followed
        self._review = review
        self._explode = explode
        self.started = 0
        self.index_now: list[bool] = []

    async def start(self, *, index_now: bool = False, **_: Any) -> dict[str, Any]:
        self.started += 1
        self.index_now.append(index_now)
        if self._explode:
            raise RuntimeError("Qobuz is down")
        return {}

    async def wait(self) -> None:
        return None

    def snapshot(self) -> dict[str, Any]:
        return {"followed": self._followed, "needs_review": self._review}


def with_importer(monkeypatch: pytest.MonkeyPatch, importer: Any | None) -> None:
    """Stand in for the app state the nightly job reaches for."""
    from app.core import state as state_module

    monkeypatch.setattr(
        state_module,
        "get_state",
        lambda: type("S", (), {"importer": importer})(),
    )


def test_the_nightly_run_does_not_follow_anyone_by_default(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Adding work has to be asked for; the default nightly job stays one-way.

    The switch is stated here rather than inherited from the ``settings``
    fixture, which is built from :func:`get_settings` and therefore from the
    developer's own ``.env``. A test about a **default** that reads ambient
    configuration is testing the machine it runs on: switching
    ``LIBRARY_FOLLOW_NIGHTLY`` on for real use failed this assertion while the
    code it is about had not changed. The paired test below states ``True`` for
    exactly the same reason.
    """
    importer = FakeImporter()
    with_importer(monkeypatch, importer)
    summary = run_housekeeping(
        session_maker,
        settings.model_copy(update={"library_follow_nightly": False}),
        monkeypatch,
    )
    assert importer.started == 0
    assert summary["artists_followed"] == 0


def test_the_nightly_run_follows_when_it_is_switched_on(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    importer = FakeImporter()
    with_importer(monkeypatch, importer)
    summary = run_housekeeping(
        session_maker,
        settings.model_copy(update={"library_follow_nightly": True}),
        monkeypatch,
    )
    assert importer.started == 1
    assert summary["artists_followed"] == 3
    assert summary["artists_for_review"] == 2


def test_the_nightly_follow_never_indexes_the_back_catalogue(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Indexing 136 artists at 03:17 is tens of thousands of calls at once."""
    importer = FakeImporter()
    with_importer(monkeypatch, importer)
    run_housekeeping(
        session_maker,
        settings.model_copy(update={"library_follow_nightly": True}),
        monkeypatch,
    )
    assert importer.index_now == [False]


def test_the_nightly_follow_stands_down_when_one_is_already_running(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A run started from the UI owns the budget; the night waits."""
    importer = FakeImporter(running=True)
    with_importer(monkeypatch, importer)
    run_housekeeping(
        session_maker,
        settings.model_copy(update={"library_follow_nightly": True}),
        monkeypatch,
    )
    assert importer.started == 0


def test_a_failed_follow_does_not_lose_the_nights_maintenance(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    importer = FakeImporter(explode=True)
    with_importer(monkeypatch, importer)
    summary = run_housekeeping(
        session_maker,
        settings.model_copy(update={"library_follow_nightly": True}),
        monkeypatch,
    )
    # The scan and the prune still happened, and the follow reports nothing.
    assert summary["artists_followed"] == 0
    assert "activities_pruned" in summary


def test_no_app_state_means_no_follow_rather_than_a_crash(
    session_maker: Any, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Housekeeping is reachable from the CLI, which builds no AppState."""
    from app.core import state as state_module

    def boom() -> Any:
        raise RuntimeError("state is not initialised")

    monkeypatch.setattr(state_module, "get_state", boom)
    summary = run_housekeeping(
        session_maker,
        settings.model_copy(update={"library_follow_nightly": True}),
        monkeypatch,
    )
    assert summary["artists_followed"] == 0


# ---------------------------------------------------------------------------
# A scan is two-way at the API boundary, and one-way everywhere below it
#
# app.core.scanner still only ever marks albums present, still writes no files
# and still calls Qobuz never — a dry run is still a dry run. What changed is
# that the endpoint, having been pressed by a person, starts the importer on the
# unknown artists the scan just reported.


class ScanImporter:
    def __init__(self, *, running=False, explode=False):
        self.running = running
        self._explode = explode
        self.started = 0
        self.index_now: list[bool] = []

    async def start(self, *, index_now: bool = False, **_: Any) -> dict[str, Any]:
        self.started += 1
        self.index_now.append(index_now)
        if self._explode:
            raise RuntimeError("Qobuz is down")
        return {}


def scan_with_importer(
    monkeypatch: pytest.MonkeyPatch, importer: Any, client: TestClient, **params: Any
) -> dict[str, Any]:
    monkeypatch.setattr(routes_api, "get_library_importer", lambda: importer)
    response = client.post("/api/library/scan", params=params)
    assert response.status_code == 200, response.text
    return response.json()


def test_a_scan_starts_following_the_unknown_artists_it_found(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The fixture library holds ``Stranger``, whom nobody follows."""
    importer = ScanImporter()
    body = scan_with_importer(monkeypatch, importer, client)
    assert body["unknown_artists"], "the fixture should report an unknown artist"
    assert body["import_started"] is True
    assert importer.started == 1


def test_the_scan_never_indexes_the_new_back_catalogues(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    importer = ScanImporter()
    scan_with_importer(monkeypatch, importer, client)
    assert importer.index_now == [False]


def test_a_dry_run_follows_nobody(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """``apply=false`` must stay a question, not an action."""
    importer = ScanImporter()
    body = scan_with_importer(monkeypatch, importer, client, dry_run=True)
    assert body["import_started"] is False
    assert importer.started == 0


def test_a_scan_stands_down_when_an_import_is_already_running(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    importer = ScanImporter(running=True)
    body = scan_with_importer(monkeypatch, importer, client)
    assert body["import_started"] is False
    assert importer.started == 0


def test_an_importer_that_will_not_start_still_returns_the_scan_report(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The scan's own findings are the thing the user pressed the button for."""
    importer = ScanImporter(explode=True)
    body = scan_with_importer(monkeypatch, importer, client)
    assert body["import_started"] is False
    assert body["albums_matched"] >= 0 and "summary" in body


def test_an_instance_with_no_importer_scans_anyway(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    body = scan_with_importer(monkeypatch, None, client)
    assert body["import_started"] is False


# ---------------------------------------------------------------------------
# Files that hold no playable audio
#
# A zero-byte .flac used to produce no track row at all, which meant it was
# invisible to the entire application: nothing to fingerprint, nothing to
# baseline, no row for a verdict to land on, and no count it appeared in. Its
# only trace was a line in the transient error list of whichever scan last ran.
# One real library held 30 of them across 15 releases, none of them reported
# anywhere. So the scan records the file and marks it corrupt, and the verdict
# lands in the column every existing consumer already reads.
# ---------------------------------------------------------------------------
def _break_a_file(library: Path) -> Path:
    """Replace one of All Melody's four files with an empty one."""
    path = library / "Nils Frahm" / "All Melody (2018)" / "01 - Track 1.flac"
    assert path.exists(), sorted(p.name for p in path.parent.iterdir())
    path.write_bytes(b"")
    return path


def _fingerprint_states(
    maker: async_sessionmaker[AsyncSession], album_id: str
) -> dict[str, str | None]:
    """Each track's recorded fingerprint state, keyed by file name."""

    async def run() -> dict[str, str | None]:
        async with maker() as session:
            rows = (
                await session.execute(
                    select(Track, TrackMetadata)
                    .outerjoin(TrackMetadata, TrackMetadata.track_id == Track.id)
                    .where(Track.album_id == album_id)
                )
            ).all()
            out: dict[str, str | None] = {}
            for track, meta in rows:
                name = Path(track.path).name if track.path else track.id
                state = getattr(meta, "fingerprint_state", None)
                out[name] = getattr(state, "value", state)
            return out

    return asyncio.run(run())


def test_an_empty_file_becomes_a_track_row(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    broken = _break_a_file(library)
    scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a1"))
    paths = {Path(t.path).name for t in rows if t.path}
    assert broken.name in paths


def test_an_empty_file_is_recorded_corrupt(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    broken = _break_a_file(library)
    result = scan(session_maker, settings)

    assert result.files_corrupt == 1
    assert _fingerprint_states(session_maker, "a1")[broken.name] == "corrupt"


def test_the_good_files_beside_it_are_not_marked(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    broken = _break_a_file(library)
    scan(session_maker, settings)

    states = _fingerprint_states(session_maker, "a1")
    assert all(
        state != "corrupt" for name, state in states.items() if name != broken.name
    )


def test_an_empty_file_does_not_count_as_present(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    # `deps._apply_completeness` counts tracks that are DOWNLOADED with a path,
    # so a broken file must not be one — otherwise a release reports 4 of 4
    # while one of them is silent, which is the whole failure this exists to fix.
    broken = _break_a_file(library)
    scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a1"))
    by_name = {Path(t.path).name: t for t in rows if t.path}
    assert by_name[broken.name].status is TrackStatus.FAILED
    assert by_name[broken.name].origin is TrackOrigin.SCAN


def test_an_empty_file_is_never_baselined(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    # Hashing it would give a perfectly good baseline for a file with no audio,
    # and the row would then read as VERIFIED for as long as nobody touched it.
    broken = _break_a_file(library)
    scan(session_maker, settings)

    rows = asyncio.run(tracks_of(session_maker, "a1"))
    row = next(t for t in rows if t.path and Path(t.path).name == broken.name)
    assert row.content_hash is None
    assert row.verified_at is None


def test_fixing_the_file_clears_the_verdict(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    # The alarm has to go down again. The fix for a corrupt file is to replace
    # it, so this is the common case rather than the rare one — and a count that
    # never falls is a count people stop reading.
    broken = _break_a_file(library)
    scan(session_maker, settings)
    assert _fingerprint_states(session_maker, "a1")[broken.name] == "corrupt"

    write_flac(
        broken,
        album="All Melody",
        albumartist="Nils Frahm",
        artist="Nils Frahm",
        title="Track 1",
        tracknumber="1",
    )
    result = scan(session_maker, settings)

    # The alarm is down, which is the guarantee. It got there by *retirement*
    # rather than by clearing: an unreadable file has no track number, so
    # `scanned_track_id` keys it on its filename, and the repaired file — which
    # now has one — is a different id. The old row is retired and takes its
    # `track_metadata` with it through the delete-orphan cascade. Both routes
    # are legitimate; `files_recovered` counts only the other one, which the
    # test below exercises.
    assert result.files_corrupt == 0
    assert _fingerprint_states(session_maker, "a1")[broken.name] != "corrupt"


def test_a_repaired_file_that_keeps_its_row_has_the_verdict_cleared(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    # The same recovery, on the row-preserving path: with no track number the id
    # stays keyed on the filename across both scans, so the verdict has to be
    # actively cleared rather than deleted with the row. Getting this wrong
    # leaves a release reporting a problem somebody already fixed.
    broken = _break_a_file(library)
    scan(session_maker, settings)
    assert _fingerprint_states(session_maker, "a1")[broken.name] == "corrupt"

    write_flac(
        broken,
        album="All Melody",
        albumartist="Nils Frahm",
        artist="Nils Frahm",
        title="Track 1",
    )
    result = scan(session_maker, settings)

    assert result.files_corrupt == 0
    assert result.files_recovered == 1
    assert _fingerprint_states(session_maker, "a1")[broken.name] is None


def test_rescanning_a_still_broken_file_does_not_double_count(
    session_maker: Any, settings: Settings, library: Path
) -> None:
    _break_a_file(library)
    scan(session_maker, settings)
    result = scan(session_maker, settings)

    # Still one broken file, still one verdict — not a second row and not a
    # recovery. A nightly scan over a stable library must converge.
    assert result.files_corrupt == 1
    assert result.files_recovered == 0
