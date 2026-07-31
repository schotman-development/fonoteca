"""Route-level tests for the opt-in download behaviour.

The important guarantee is that *following* an artist and *indexing* it never
starts a download by itself: with ``AUTO_DOWNLOAD=false`` (the default) albums
reach ``wanted`` and stop there. Only an explicit user action queues them.

These use a scratch SQLite file and ``dependency_overrides``; no queue worker
runs and nothing touches the network, so exercising the "start downloads" route
here is safe.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.config import Settings
from app.db import get_session
from app.models import Album, AlbumStatus, Artist, Base, QueueItem, QueueState

ARTIST_ID = "720076"
WANTED_IDS = ("uyej1o165e870", "wxl78pvfqlm3b")


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """A TestClient over a scratch DB holding two *wanted*, unqueued albums."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'optin.db'}", poolclass=NullPool
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
            for index, album_id in enumerate(WANTED_IDS):
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=f"Album {index}",
                        status=AlbumStatus.WANTED,
                        release_type="album",
                        tracks_count=6,
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


def queued_ids(client: TestClient) -> set[str]:
    """Album ids currently sitting in the queue, via the public API."""
    body = client.get("/api/queue").json()
    return {item["album_id"] for item in body["items"]}


def test_auto_download_is_off_by_default() -> None:
    """The safety net: a bare Settings() must never download unattended."""
    assert Settings().auto_download is False


def test_wanted_albums_are_not_queued_on_their_own(client: TestClient) -> None:
    """Seeded wanted albums stay out of the queue until asked for."""
    assert client.get("/api/queue").json()["total"] == 0
    assert queued_ids(client) == set()


def test_download_wanted_queues_the_backlog(client: TestClient) -> None:
    """The explicit opt-in route starts everything this artist wants."""
    response = client.post(f"/api/artists/{ARTIST_ID}/download-wanted")
    assert response.status_code == 200
    body = response.json()
    assert body["ok"] is True
    assert "2" in body["message"]

    assert queued_ids(client) == set(WANTED_IDS)


def test_download_wanted_is_idempotent(client: TestClient) -> None:
    """Clicking twice must not create duplicate queue items."""
    client.post(f"/api/artists/{ARTIST_ID}/download-wanted")
    second = client.post(f"/api/artists/{ARTIST_ID}/download-wanted")

    assert second.status_code == 200
    assert "Nothing wanted" in second.json()["message"]
    assert client.get("/api/queue").json()["total"] == len(WANTED_IDS)


def test_download_wanted_404s_for_an_unknown_artist(client: TestClient) -> None:
    response = client.post("/api/artists/does-not-exist/download-wanted")
    assert response.status_code == 404


def test_single_album_queue_route_still_works(client: TestClient) -> None:
    """The per-album opt-in path is unaffected by the global setting."""
    response = client.post(f"/api/albums/{WANTED_IDS[0]}/queue")
    assert response.status_code == 200
    assert queued_ids(client) == {WANTED_IDS[0]}


def test_ui_download_wanted_button_renders_and_posts(client: TestClient) -> None:
    """The artist page offers the button, and it swaps the album table."""
    page = client.get(f"/artists/{ARTIST_ID}")
    assert page.status_code == 200
    assert f"/ui/artists/{ARTIST_ID}/download-wanted" in page.text

    posted = client.post(
        f"/ui/artists/{ARTIST_ID}/download-wanted", headers={"HX-Request": "true"}
    )
    assert posted.status_code == 200
    assert queued_ids(client) == set(WANTED_IDS)
