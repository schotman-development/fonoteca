"""Comparing the copy on disk with what Qobuz would deliver today.

A downloaded release used to show the same *Download* button as everything
else, which was at best pointless (the files are already there) and at worst a
lie (the download loop reuses whatever it finds and fetches nothing). Now the
row shows one of three things:

* **Upgrade** — a strictly better format than the worst file we hold is
  obtainable for this release, on this account;
* **nothing** — it is already as good as it gets, or the comparison cannot be
  made, in which case there is no honest button to offer;
* **In queue** — an upgrade (or a download) is already pending.

The load-bearing rule is that :mod:`app.core.quality` answers the question for
*both* sides. The artist page uses it to decide whether to show the button, and
:meth:`AlbumDownloader._is_upgradable` uses it to decide whether a file on disk
may be reused. If they ever disagreed, Upgrade would queue a download that
skipped every track and changed nothing.

Three ceilings can each veto an upgrade and all three are tested: the account's
entitlement, the artist's quality profile, and the release itself — asking for
24/192 on a CD master gets you 16/44.1 forever.

Scratch SQLite under ``tmp_path`` via ``dependency_overrides``; no worker, no
network.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from types import SimpleNamespace
from typing import AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.core import quality
from app.core.downloader import AlbumDownloader
from app.db import get_session
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    QueueItem,
    QueueState,
    Track,
    TrackOrigin,
    TrackStatus,
)

ARTIST_ID = "720076"

#: A release, the quality Qobuz says it exists in, and the quality we hold.
#: ``owned`` is ``(format_id, bit_depth, sampling_rate)`` per track, or ``None``
#: for a release with no files.
#:   id, title, status, (max_depth, max_rate), owned
SEED = (
    # 16/44.1 on disk, 24/96 available: the case the feature exists for.
    ("upgradable0001", "All Melody", AlbumStatus.DOWNLOADED, (24, 96.0), (6, 16, 44.1)),
    # Already at the release ceiling: nothing to offer.
    ("maxedout000002", "Spaces", AlbumStatus.DOWNLOADED, (24, 96.0), (7, 24, 96.0)),
    # A CD master. Held at 16/44.1 and that is all it will ever be.
    ("cdmaster000003", "Screws", AlbumStatus.DOWNLOADED, (16, 44.1), (6, 16, 44.1)),
    # Downloaded but the files' quality is unknown — no comparison, no button.
    ("unknownqual004", "Wintermusik", AlbumStatus.DOWNLOADED, (24, 96.0), (None, None, None)),
    # Not on disk at all.
    ("wantedrelease5", "Felt", AlbumStatus.WANTED, (24, 192.0), None),
    # Upgradable, but an upgrade is already queued.
    ("alreadyqueued6", "Solo", AlbumStatus.DOWNLOADED, (24, 192.0), (6, 16, 44.1)),
)

UPGRADABLE = "upgradable0001"
MAXED_OUT = "maxedout000002"
CD_MASTER = "cdmaster000003"
UNKNOWN = "unknownqual004"
WANTED = "wantedrelease5"
QUEUED_UPGRADE = "alreadyqueued6"

HX = {"HX-Request": "true"}


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """A TestClient over a scratch DB seeded with one release per interesting case."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'upgrade.db'}", poolclass=NullPool
    )
    session_maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with session_maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm", monitored=True))
            for album_id, title, status, (depth, rate), owned in SEED:
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=title,
                        status=status,
                        monitored=True,
                        release_type="album",
                        tracks_count=2,
                        hires=depth > 16,
                        max_bit_depth=depth,
                        max_sampling_rate=rate,
                    )
                )
                if owned is None:
                    continue
                format_id, bit_depth, sampling_rate = owned
                for number in (1, 2):
                    session.add(
                        Track(
                            id=f"{album_id}-{number}",
                            album_id=album_id,
                            title=f"Track {number}",
                            track_number=number,
                            status=TrackStatus.DOWNLOADED,
                            path=f"{tmp_path}/{album_id}/{number:02d}.flac",
                            format_id=format_id,
                            bit_depth=bit_depth,
                            sampling_rate=sampling_rate,
                        )
                    )
            session.add(
                QueueItem(album_id=QUEUED_UPGRADE, state=QueueState.PENDING, priority=0)
            )
            await session.commit()

    asyncio.run(seed())

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        asyncio.run(engine.dispose())


def row(body: str, album_id: str) -> str:
    """The ``<tr>`` of the album table that belongs to one release.

    Split on the row tag rather than on the id: the id appears first in the
    *actions* cell, so slicing forward from it would cut off the quality cell
    that half these assertions are about.
    """
    for chunk in body.split("<tr"):
        if album_id in chunk:
            return chunk
    raise AssertionError(f"{album_id} has no row in the table")


def album_rows(client: TestClient) -> str:
    return client.get(f"/legacy/partials/albums/{ARTIST_ID}").text


def album_json(client: TestClient, album_id: str) -> dict:
    return client.get(f"/api/albums/{album_id}").json()


# --------------------------------------------------------------- the arithmetic
@pytest.mark.parametrize(
    ("depth", "rate", "expected"),
    [
        (16, 44.1, 6),
        (16, 48.0, 6),
        (24, 44.1, 7),
        (24, 96.0, 7),
        (24, 192.0, 27),
        (24, 44100, 7),  # Hz rather than kHz, as some tag readers report it
        (None, 96.0, None),
        (24, None, None),
        (0, 0, None),
    ],
)
def test_a_quality_maps_to_the_smallest_format_that_carries_it(
    depth: object, rate: object, expected: int | None
) -> None:
    assert quality.format_for_quality(depth, rate) == expected


@pytest.mark.parametrize(
    ("profile", "expected"),
    [
        ("default", 27),
        ("", 27),
        (None, 27),
        ("lossless", 6),
        ("hires", 7),
        ("max", 27),
        ("6", 6),
        ("nonsense", 27),  # a typo on the artist row must not stop a download
    ],
)
def test_quality_profiles_resolve_to_a_format(profile: object, expected: int) -> None:
    assert quality.format_for_profile(profile, 27) == expected


def test_the_owned_format_is_the_worst_file_not_the_best() -> None:
    """An album is only as upgraded as its weakest track."""
    tracks = [
        SimpleNamespace(path="a.flac", format_id=27, bit_depth=24, sampling_rate=192.0),
        SimpleNamespace(path="b.flac", format_id=6, bit_depth=16, sampling_rate=44.1),
    ]
    assert quality.owned_format_id(tracks) == 6


def test_files_that_are_not_there_do_not_count() -> None:
    tracks = [SimpleNamespace(path=None, format_id=6, bit_depth=16, sampling_rate=44.1)]
    assert quality.owned_format_id(tracks) is None
    assert quality.owned_format_id([]) is None


def test_an_untagged_file_is_skipped_rather_than_counted_as_bad() -> None:
    """One unreadable file must not make a maximal album permanently upgradable."""
    tracks = [
        SimpleNamespace(path="a.flac", format_id=None, bit_depth=None, sampling_rate=None),
        SimpleNamespace(path="b.flac", format_id=7, bit_depth=24, sampling_rate=96.0),
    ]
    assert quality.owned_format_id(tracks) == 7


def test_a_scanned_file_reports_its_format_without_a_format_id() -> None:
    """The library scanner only knows depth and rate; that has to be enough."""
    scanned = SimpleNamespace(
        path="a.flac", format_id=None, bit_depth=24, sampling_rate=96.0
    )
    assert quality.track_format_id(scanned) == 7


def test_the_release_clamps_what_can_be_obtained() -> None:
    """Asking for 24/192 on a CD master gets 16/44.1, so that is the ceiling."""
    cd = SimpleNamespace(max_bit_depth=16, max_sampling_rate=44.1)
    assert quality.obtainable_format_id(cd, 27) == 6


def test_the_account_clamps_what_can_be_obtained() -> None:
    """A lossless-only plan cannot reach the hi-res master."""
    hires = SimpleNamespace(max_bit_depth=24, max_sampling_rate=192.0)
    assert quality.obtainable_format_id(hires, 6) == 6


def test_an_unknown_release_quality_obtains_nothing() -> None:
    unknown = SimpleNamespace(max_bit_depth=None, max_sampling_rate=None)
    assert quality.obtainable_format_id(unknown, 27) is None


@pytest.mark.parametrize(
    ("owned", "release", "target", "expected"),
    [
        ((6, 16, 44.1), (24, 96.0), 27, (6, 7)),
        ((6, 16, 44.1), (24, 192.0), 27, (6, 27)),
        ((7, 24, 96.0), (24, 96.0), 27, None),  # already at the release ceiling
        ((6, 16, 44.1), (16, 44.1), 27, None),  # CD master, nothing better exists
        ((6, 16, 44.1), (24, 192.0), 6, None),  # lossless-only account
        ((None, None, None), (24, 96.0), 27, None),  # unknown files: no guessing
        (None, (24, 96.0), 27, None),  # nothing on disk to compare
    ],
)
def test_upgrade_availability(
    owned: tuple | None, release: tuple, target: int, expected: tuple | None
) -> None:
    album = SimpleNamespace(max_bit_depth=release[0], max_sampling_rate=release[1])
    tracks = (
        []
        if owned is None
        else [
            SimpleNamespace(
                path="a.flac", format_id=owned[0], bit_depth=owned[1],
                sampling_rate=owned[2],
            )
        ]
    )
    assert quality.upgrade_available(album, tracks, target) == expected


def test_format_labels_read_the_way_the_folder_names_do() -> None:
    assert quality.format_label(5) == "MP3 320"
    assert quality.format_label(6) == "FLAC 16-44.1"
    assert quality.format_label(7) == "FLAC 24-96"
    assert quality.format_label(27) == "FLAC 24-192"
    assert quality.format_label(None) == ""


# ------------------------------------------------------- the download loop side
@pytest.mark.parametrize(
    ("owned_format", "obtainable", "expected"),
    [
        (6, 7, True),
        (6, 6, False),
        (7, 6, False),  # never downgrade what is already better
        (6, None, False),  # release quality unknown: leave the file alone
        (None, 7, False),  # file quality unknown: leave the file alone
    ],
)
def test_the_downloader_replaces_only_a_genuinely_worse_file(
    owned_format: int | None, obtainable: int | None, expected: bool
) -> None:
    """The same comparison the button makes, on the side that does the work."""
    track = SimpleNamespace(format_id=owned_format, bit_depth=None, sampling_rate=None)
    assert AlbumDownloader._is_upgradable(track, obtainable) is expected


class _StubClient:
    """The three things :meth:`AlbumDownloader._download_track` asks a client for."""

    def __init__(self, delivered: dict) -> None:
        self.delivered = delivered
        self.calls: list[tuple[str, int]] = []

    async def get_file_url(self, track_id: str, format_id: int) -> dict:
        self.calls.append((track_id, format_id))
        return {"url": "https://cdn.example/signed", **self.delivered}

    async def stream_to_file(self, url: str, dest: Path, on_progress=None) -> int:
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(b"audio")
        return 5


def _download_one(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, *, obtainable: int | None
) -> tuple[object, _StubClient]:
    """Run one track through the real ``_download_track`` with a file already there."""
    monkeypatch.setattr("app.core.downloader.tag_file", lambda *a, **k: True)

    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'loop.db'}", poolclass=NullPool
    )
    session_maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)
    stub = _StubClient({"format_id": 7, "bit_depth": 24, "sampling_rate": 96.0})
    downloader = AlbumDownloader(stub)  # type: ignore[arg-type]
    album_dir = tmp_path / "library" / "Nils Frahm" / "All Melody"

    async def run():
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with session_maker() as session:
            artist = Artist(id=ARTIST_ID, name="Nils Frahm")
            album = Album(
                id=UPGRADABLE, artist_id=ARTIST_ID, title="All Melody",
                status=AlbumStatus.DOWNLOADED, max_bit_depth=24, max_sampling_rate=96.0,
            )
            track = Track(
                id="t1", album_id=UPGRADABLE, title="Sunson", track_number=1,
                status=TrackStatus.DOWNLOADED, format_id=6, bit_depth=16,
                sampling_rate=44.1,
            )
            session.add_all([artist, album, track])
            await session.commit()

            # The 16/44.1 file the resume logic would otherwise reuse. The name
            # is what the default naming template renders for this track.
            already_there = album_dir / "01 - Sunson.flac"
            already_there.parent.mkdir(parents=True, exist_ok=True)
            already_there.write_bytes(b"old")
            track.path = str(already_there)
            await session.commit()
            assert downloader._find_existing_file(track, album, artist, album_dir) is not None

            return await downloader._download_track(
                session,
                album=album,
                artist=artist,
                artist_name="Nils Frahm",
                track=track,
                album_dir=album_dir,
                format_id=27,
                obtainable_format_id=obtainable,
                cover_bytes=None,
            )

    try:
        result, _hit_network = asyncio.run(run())
    finally:
        asyncio.run(engine.dispose())
    return result, stub


def test_the_loop_reuses_a_file_that_is_already_good_enough(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Resumability is unchanged when there is nothing better to fetch."""
    result, stub = _download_one(tmp_path, monkeypatch, obtainable=6)
    assert result.already_present is True
    assert result.upgraded is False
    assert stub.calls == []  # not one API call


def test_the_loop_replaces_a_file_a_better_format_exists_for(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The half that makes the Upgrade button mean something."""
    result, stub = _download_one(tmp_path, monkeypatch, obtainable=7)
    assert result.already_present is False
    assert result.upgraded is True
    assert stub.calls == [("t1", 27)]
    assert Path(result.path).read_bytes() == b"audio"


def test_the_button_and_the_download_loop_agree(client: TestClient) -> None:
    """Whatever the table offers, the loop must be willing to perform.

    Both sides go through :mod:`app.core.quality`; this pins the two together so
    an Upgrade button can never queue a download that skips every track.
    """
    for album_id, *_ in SEED:
        payload = album_json(client, album_id)
        offered = payload["upgrade_format_id"]
        track = SimpleNamespace(
            format_id=payload["owned_format_id"], bit_depth=None, sampling_rate=None
        )
        assert AlbumDownloader._is_upgradable(track, offered) is (offered is not None)


# ------------------------------------------------------------------ the button
def test_an_upgradable_release_offers_upgrade(client: TestClient) -> None:
    cell = row(album_rows(client), UPGRADABLE)
    assert ">Upgrade<" in cell
    assert ">Download<" not in cell
    assert f"/ui/albums/{UPGRADABLE}/queue" in cell


def test_a_maximal_release_offers_nothing_at_all(client: TestClient) -> None:
    """The other half of the request: no button, not a disabled one."""
    cell = row(album_rows(client), MAXED_OUT)
    assert ">Upgrade<" not in cell
    assert ">Download<" not in cell
    assert f"/ui/albums/{MAXED_OUT}/queue" not in cell


def test_a_cd_master_is_never_offered_a_hi_res_upgrade(client: TestClient) -> None:
    """16/44.1 held, 16/44.1 available — the hi-res default profile changes nothing."""
    cell = row(album_rows(client), CD_MASTER)
    assert ">Upgrade<" not in cell
    assert ">Download<" not in cell


def test_an_unmeasurable_release_offers_nothing(client: TestClient) -> None:
    """No format on disk means no comparison, and no button rather than a guess."""
    cell = row(album_rows(client), UNKNOWN)
    assert ">Upgrade<" not in cell
    assert ">Download<" not in cell


def test_a_release_that_is_not_downloaded_still_offers_download(
    client: TestClient,
) -> None:
    """The change must not touch the ordinary backlog path."""
    cell = row(album_rows(client), WANTED)
    assert ">Download<" in cell
    assert ">Upgrade<" not in cell


def test_a_queued_upgrade_shows_the_queue_not_the_button(client: TestClient) -> None:
    """A downloaded album keeps its status while queued, so the row reads the queue."""
    cell = row(album_rows(client), QUEUED_UPGRADE)
    assert ">In queue<" in cell
    assert ">Upgrade<" not in cell


def test_the_monitor_toggle_survives(client: TestClient) -> None:
    """Every row keeps its monitor button, including the ones with no action."""
    body = album_rows(client)
    for album_id, *_ in SEED:
        assert f"/ui/albums/{album_id}/monitor?view=artist" in body


# ----------------------------------------------------------------- the quality
def test_a_downloaded_row_shows_what_is_on_disk(client: TestClient) -> None:
    """Not the catalogue maximum — that cannot tell you if the files are any good."""
    cell = row(album_rows(client), UPGRADABLE)
    assert "FLAC 16-44.1" in cell


def test_an_upgradable_row_shows_both_qualities(client: TestClient) -> None:
    cell = row(album_rows(client), UPGRADABLE)
    assert "FLAC 16-44.1" in cell and "FLAC 24-96" in cell


def test_a_wanted_row_still_shows_the_catalogue_quality(client: TestClient) -> None:
    cell = row(album_rows(client), WANTED)
    assert "24bit/192kHz" in cell


# -------------------------------------------------------------------- the JSON
def test_the_api_exposes_the_comparison(client: TestClient) -> None:
    payload = album_json(client, UPGRADABLE)
    assert payload["owned_format_id"] == 6
    assert payload["upgrade_format_id"] == 7
    assert payload["queue_state"] is None


def test_the_api_reports_no_upgrade_when_there_is_none(client: TestClient) -> None:
    payload = album_json(client, MAXED_OUT)
    assert payload["owned_format_id"] == 7
    assert payload["upgrade_format_id"] is None


def test_the_api_reports_a_live_queue_entry(client: TestClient) -> None:
    """``status`` stays ``downloaded`` through an upgrade; ``queue_state`` does not."""
    payload = album_json(client, QUEUED_UPGRADE)
    assert payload["status"] == "downloaded"
    assert payload["queue_state"] == "pending"


# -------------------------------------------------------------- pressing it
def test_pressing_upgrade_queues_the_album(client: TestClient) -> None:
    response = client.post(f"/legacy/ui/albums/{UPGRADABLE}/queue", headers=HX)
    assert response.status_code == 200
    assert "upgrade" in response.headers.get("HX-Trigger", "").lower()
    queued = {item["album_id"] for item in client.get("/api/queue").json()["items"]}
    assert UPGRADABLE in queued


def test_pressing_upgrade_leaves_the_album_downloaded(client: TestClient) -> None:
    """It is on disk until the replacement lands; the backlog must not claim it."""
    client.post(f"/legacy/ui/albums/{UPGRADABLE}/queue", headers=HX)
    assert album_json(client, UPGRADABLE)["status"] == "downloaded"
    assert UPGRADABLE not in client.get("/legacy/wanted").text


def test_the_row_stops_offering_an_upgrade_once_it_is_queued(client: TestClient) -> None:
    body = client.post(f"/legacy/ui/albums/{UPGRADABLE}/queue", headers=HX).text
    cell = row(body, UPGRADABLE)
    assert ">In queue<" in cell
    assert ">Upgrade<" not in cell


def test_nothing_else_gets_queued(client: TestClient) -> None:
    """One press, one album — the same blast-radius rule as everywhere else."""
    client.post(f"/legacy/ui/albums/{UPGRADABLE}/queue", headers=HX)
    queued = {item["album_id"] for item in client.get("/api/queue").json()["items"]}
    assert queued == {UPGRADABLE, QUEUED_UPGRADE}


# ---------------------------------------------------------------------------
# Upgrading a release the scanner adopted
# ---------------------------------------------------------------------------
def test_downloading_supersedes_the_rows_the_scan_made(tmp_path: Path) -> None:
    """Qobuz's real track ids replace the scan's positional guesses.

    An adopted album carries ``scan`` rows keyed on ``(disc, track)``; a download
    of the same release brings rows keyed on Qobuz track ids. Keeping both would
    double the album's track count — which is what completeness and the quality
    comparison are computed from, so an upgraded album would report half its
    tracks missing for ever.
    """
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'sync.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)
    raw = {
        "tracks": {
            "items": [
                {"id": "q1", "title": "Sunson", "track_number": 1, "media_number": 1},
                {"id": "q2", "title": "Says", "track_number": 2, "media_number": 1},
            ]
        }
    }

    async def run() -> list[Track]:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm"))
            session.add(
                Album(id=UPGRADABLE, artist_id=ARTIST_ID, title="All Melody",
                      status=AlbumStatus.DOWNLOADED, tracks_count=2)
            )
            for number in (1, 2):
                session.add(
                    Track(id=f"scan:{number:024d}", album_id=UPGRADABLE,
                          title=f"Track {number}", track_number=number,
                          origin=TrackOrigin.SCAN, status=TrackStatus.DOWNLOADED,
                          path=f"/library/{number}.flac")
                )
            await session.commit()

            album = await session.get(Album, UPGRADABLE)
            downloader = AlbumDownloader(SimpleNamespace())  # type: ignore[arg-type]
            await downloader._sync_tracks(session, album, raw)
            await session.commit()

            rows = await session.execute(
                select(Track).where(Track.album_id == UPGRADABLE)
            )
            return list(rows.scalars().all())

    try:
        tracks = asyncio.run(run())
    finally:
        asyncio.run(engine.dispose())

    assert sorted(track.id for track in tracks) == ["q1", "q2"]
    assert all(track.origin is TrackOrigin.DOWNLOAD for track in tracks)
