"""Regression tests: the "show everything" option in every filter dropdown.

Each filter ``<select>`` renders ``<option value="">``, so choosing it submits
``?state=`` / ``?level=`` / ``?status=`` / ``?monitored=`` — an *empty string*,
not an absent parameter.  With the parameters typed as bare enums/bools those
requests failed validation and the user landed on the red "422 - Invalid input"
page, which on ``/queue`` and ``/artists/{id}`` happens without even pressing a
button because the selects auto-submit.

The database is a private SQLite file under ``tmp_path``, injected through
``dependency_overrides``; nothing here touches ``data/`` or the network.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.api import routes_api
from app.db import get_session
from app.models import Album, AlbumStatus, Artist, Base, QueueItem, QueueState


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """A TestClient whose session dependency points at a seeded scratch DB.

    A file-backed database with ``NullPool`` is deliberate: ``TestClient``
    services each request on its own event loop, and an ``aiosqlite``
    connection may not outlive the loop that opened it.
    """
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'filters.db'}",
        poolclass=NullPool,
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
            session.add(Artist(id="720076", name="Nils Frahm", monitored=True))
            session.add(
                Album(
                    id="uyej1o165e870",
                    artist_id="720076",
                    title="Day",
                    status=AlbumStatus.WANTED,
                    release_type="album",
                    tracks_count=6,
                )
            )
            session.add(
                QueueItem(album_id="uyej1o165e870", state=QueueState.PENDING)
            )
            await session.commit()

    asyncio.run(seed())

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        asyncio.run(engine.dispose())


EMPTY_FILTER_URLS = [
    "/queue?state=",
    "/activity?level=&event=&limit=200",
    "/artists?q=&monitored=&sort=name&order=asc",
    "/artists/720076?status=&release_type=",
    "/partials/queue-table?state=",
    "/partials/albums/720076?status=&release_type=",
    "/api/queue?state=",
    "/api/activity?level=&event=&artist_id=&album_id=",
    "/api/artists?q=&monitored=",
    "/api/artists/720076/albums?status=&release_type=",
]


@pytest.mark.parametrize("url", EMPTY_FILTER_URLS)
def test_empty_filter_value_means_no_filter(client: TestClient, url: str) -> None:
    response = client.get(url)
    assert response.status_code == 200, f"{url} -> {response.status_code}"


def test_empty_filter_returns_the_unfiltered_queue(client: TestClient) -> None:
    unfiltered = client.get("/api/queue").json()
    blanked = client.get("/api/queue?state=").json()
    assert blanked["total"] == unfiltered["total"] == 1


def test_a_real_filter_value_still_filters(client: TestClient) -> None:
    assert client.get("/api/queue?state=pending").json()["total"] == 1
    assert client.get("/api/queue?state=done").json()["total"] == 0


def test_a_genuinely_invalid_value_is_still_rejected(client: TestClient) -> None:
    """The fix must not turn every typo into a silent no-op."""
    assert client.get("/api/queue?state=banana").status_code == 422
    assert client.get("/legacy/queue?state=banana").status_code == 422


# ---------------------------------------------------------------------------
# Search annotations
# ---------------------------------------------------------------------------
class SearchOnlyClient:
    """Just enough Qobuz client for ``run_search``."""

    def __init__(self, albums: list[dict[str, object]]) -> None:
        self.albums = albums

    async def search(self, query: str, **kwargs: object) -> dict[str, object]:
        return {"albums": {"items": self.albums, "total": len(self.albums)}}


def test_a_release_we_only_know_about_is_not_in_the_library(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The indexer writes a row for every release a followed artist has, so
    "we have a row" marked the whole Wanted backlog as already owned."""
    monkeypatch.setattr(
        routes_api,
        "require_client",
        lambda: SearchOnlyClient(
            [
                # Seeded as WANTED — known, not owned.
                {"id": "uyej1o165e870", "title": "Day", "tracks_count": 6},
                # Not in the database at all.
                {"id": "brandnew00001", "title": "Night", "tracks_count": 8},
            ]
        ),
    )

    albums = client.get("/api/search?q=frahm").json()["albums"]
    by_id = {album["id"]: album for album in albums}

    assert by_id["uyej1o165e870"]["in_library"] is False
    assert by_id["uyej1o165e870"]["tracked"] is True, "known, and worth saying so"
    assert by_id["brandnew00001"]["in_library"] is False
    assert by_id["brandnew00001"]["tracked"] is False
