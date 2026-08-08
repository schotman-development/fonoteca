"""``GET /api/releases/recent`` — the Release radar's own list.

The radar's left column used to be the download queue. That is a real list, but
it is ordered by the moment somebody pressed Download, and the heading above it
said *New & upcoming* — so the top row was whatever finished downloading most
recently. A 1975 remaster fetched this morning outranked a record released last
week, and a radar nobody had downloaded from was empty.

This endpoint is the honest answer: albums ordered on ``Album.release_date``,
which is the only ordering in the API that describes the *music* rather than
this program's activity. The four rules worth pinning are that it really is
ordered by date, that undated releases are excluded rather than parked at one
end, that future dates are kept and sort first (that is the "upcoming" half),
and that a download changes nothing about where a row sits.
"""

from __future__ import annotations

import asyncio
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.db import get_session
from app.models import Album, AlbumStatus, Artist, Base

ARTIST = "720076"

#: (id, title, release_date, status, monitored)
#:
#: The dates are deliberately unrelated to ``added_at``, which every row gets
#: from the same seeding pass a moment apart — an implementation that ordered on
#: the wrong column would return these in insertion order and look plausible.
SEED = (
    ("undated", "Untitled Bootleg", None, AlbumStatus.WANTED, True),
    ("oldest", "Electric Piano", date(1998, 3, 2), AlbumStatus.DOWNLOADED, True),
    ("middle", "Spaces", date(2013, 11, 15), AlbumStatus.WANTED, True),
    ("newest", "All Melody", date(2018, 1, 26), AlbumStatus.WANTED, False),
)

#: Far enough ahead that no test run can overtake it.
UPCOMING = date.today() + timedelta(days=45)


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'radar.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id=ARTIST, name="Nils Frahm", monitored=True))
            # Newest first into the database, so "returned newest first" cannot
            # pass by accident on insertion order.
            stamp = datetime(2026, 1, 1, tzinfo=timezone.utc)
            for offset, (album_id, title, released, status, monitored) in enumerate(
                SEED
            ):
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST,
                        title=title,
                        release_date=released,
                        status=status,
                        monitored=monitored,
                        tracks_count=6,
                        added_at=stamp + timedelta(minutes=offset),
                    )
                )
            await session.commit()

    asyncio.run(seed())

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        asyncio.run(engine.dispose())


def fetch(client: TestClient, url: str = "/api/releases/recent") -> dict:
    response = client.get(url)
    assert response.status_code == 200, f"{url} -> {response.status_code}"
    return response.json()


def ids(client: TestClient, url: str = "/api/releases/recent") -> list[str]:
    return [item["id"] for item in fetch(client, url)["items"]]


async def add_album(client: TestClient, **kwargs) -> None:
    """Insert one more release through the same engine the app is using."""
    session_factory = main.app.dependency_overrides[get_session]
    agen = session_factory()
    session = await agen.__anext__()
    session.add(Album(artist_id=ARTIST, tracks_count=6, **kwargs))
    await session.commit()
    await session.close()


def test_newest_release_first(client: TestClient) -> None:
    assert ids(client) == ["newest", "middle", "oldest"]


def test_a_release_with_no_date_is_absent_rather_than_last(client: TestClient) -> None:
    """``nullslast()`` would park it at the bottom, reading as "longest ago".

    On a real library that is a large minority of the catalogue, so the tail of
    every page would be releases nobody has a date for, sorted as though the
    date were known and ancient.
    """
    payload = fetch(client)
    assert "undated" not in [item["id"] for item in payload["items"]]
    # And it is out of the count too — a total including rows the ordering
    # cannot place promises pages that never arrive.
    assert payload["total"] == 3


def test_an_unreleased_record_sorts_first_and_is_not_filtered_out(
    client: TestClient,
) -> None:
    """The *upcoming* half of the heading. Qobuz dates announced records ahead."""
    asyncio.run(
        add_album(
            client,
            id="announced",
            title="Sable, Fable",
            release_date=UPCOMING,
            status=AlbumStatus.WANTED,
            monitored=True,
        )
    )
    assert ids(client)[0] == "announced"


def test_downloading_a_release_does_not_move_it(client: TestClient) -> None:
    """The whole defect, stated as a property.

    The queue-backed column reordered itself around whatever was fetched last.
    Nothing about a download may change where a release sits on a list ordered
    by when the music came out.
    """
    before = ids(client)
    session_factory = main.app.dependency_overrides[get_session]

    async def download_the_oldest() -> None:
        agen = session_factory()
        session = await agen.__anext__()
        album = await session.get(Album, "oldest")
        assert album is not None
        album.status = AlbumStatus.DOWNLOADED
        album.downloaded_at = datetime.now(timezone.utc)
        album.path = "/music/Nils Frahm/Electric Piano"
        await session.commit()
        await session.close()

    asyncio.run(download_the_oldest())
    assert ids(client) == before


def test_the_monitor_filter_narrows_and_blank_means_unfiltered(
    client: TestClient,
) -> None:
    """Absent is *no filter* here, unlike ``/api/wanted`` where it means True.

    A radar that hid the releases somebody had already ignored would be hiding
    the evidence they were ignored.
    """
    assert "newest" in ids(client)  # monitored=False, and present by default
    assert ids(client, "/api/releases/recent?monitored=true") == ["middle", "oldest"]
    assert ids(client, "/api/releases/recent?monitored=false") == ["newest"]
    assert ids(client, "/api/releases/recent?monitored=") == ["newest", "middle", "oldest"]


def test_unfiltered_total_distinguishes_no_match_from_nothing_here(
    client: TestClient,
) -> None:
    payload = fetch(client, "/api/releases/recent?monitored=false")
    assert payload["total"] == 1
    assert payload["unfiltered_total"] == 3


def test_the_page_carries_the_artist_and_the_release_date(client: TestClient) -> None:
    """The two fields the row is built from. A row cannot render either from an id."""
    first = fetch(client)["items"][0]
    assert first["artist_name"] == "Nils Frahm"
    assert first["release_date"] == "2018-01-26"


def test_limit_and_offset_page_the_same_ordering(client: TestClient) -> None:
    assert ids(client, "/api/releases/recent?limit=1") == ["newest"]
    assert ids(client, "/api/releases/recent?limit=1&offset=2") == ["oldest"]


def test_a_bad_limit_is_refused_rather_than_clamped(client: TestClient) -> None:
    assert client.get("/api/releases/recent?limit=0").status_code == 422
    assert client.get("/api/releases/recent?limit=99999").status_code == 422


def test_recent_is_a_path_not_an_album_id(client: TestClient) -> None:
    """``/albums/{album_id}`` matches any single segment.

    Addressing this ``/albums/recent`` would have been a literal path competing
    with a parameter for the same shape, resolved by registration order. The
    separate noun cannot be shadowed by a route somebody adds later.
    """
    assert client.get("/api/albums/recent").status_code == 404
