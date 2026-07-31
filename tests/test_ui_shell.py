"""Tests for the rebuilt UI: the app shell, the sidebar and the Wanted screen.

The shell (header / grouped sidebar / title bar / status footer) is shared by
every page, so a mistake in ``base.html`` breaks all of them at once — these
assert its landmarks render and that the nav highlights the current section.

The Wanted screen is new: a cross-artist view of releases that were discovered
but never downloaded. Its bulk "Download all" button is one of the few paths
that may queue anything, so there are tests that it queues only *monitored*,
*wanted* albums and leaves everything else alone.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; no queue worker runs and nothing touches the network.
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
from app.db import get_session
from app.models import Album, AlbumStatus, Artist, Base, QueueItem, QueueState

ARTIST_ID = "720076"

#: (album id, status, monitored) — only the two monitored WANTED rows are
#: eligible for the bulk download button.
SEED_ALBUMS = (
    ("uyej1o165e870", AlbumStatus.WANTED, True),
    ("wxl78pvfqlm3b", AlbumStatus.WANTED, True),
    ("0884977859300", AlbumStatus.WANTED, False),   # ignored by the user
    ("aaaa1111bbbb2", AlbumStatus.FAILED, True),    # missing, but not "wanted"
    ("cccc3333dddd4", AlbumStatus.DOWNLOADED, True),
    ("eeee5555ffff6", AlbumStatus.SKIPPED, True),
)

QUEUEABLE = {"uyej1o165e870", "wxl78pvfqlm3b"}


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """A TestClient over a scratch DB holding one artist and six releases."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'shell.db'}", poolclass=NullPool
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
            for index, (album_id, status, monitored) in enumerate(SEED_ALBUMS):
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=f"Release {index}",
                        status=status,
                        monitored=monitored,
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
    return {item["album_id"] for item in client.get("/api/queue").json()["items"]}


# --------------------------------------------------------------------- shell
PAGES = ["/", "/artists", "/add", "/wanted", "/queue", "/activity", "/settings"]


@pytest.mark.parametrize("path", PAGES)
def test_every_page_renders_the_shell(client: TestClient, path: str) -> None:
    """Header, sidebar, title bar and status footer are on every page."""
    body = client.get(path).text
    for landmark in (
        'class="shell"',
        'class="shell__top"',
        'class="sidebar__nav"',
        'class="titlebar"',
        'id="main"',
        'id="status-bar"',
        "/static/style.css",
    ):
        assert landmark in body, f"{landmark} missing from {path}"


@pytest.mark.parametrize("path", PAGES)
def test_pages_have_a_single_h1(client: TestClient, path: str) -> None:
    """The title bar owns the page heading; nothing else may claim <h1>."""
    assert client.get(path).text.count("<h1") == 1


@pytest.mark.parametrize(
    ("path", "label"),
    [("/", "Dashboard"), ("/artists", "Library"), ("/add", "Add new"),
     ("/wanted", "Wanted"), ("/queue", "Queue"), ("/activity", "Activity"),
     ("/settings", "Settings")],
)
def test_nav_marks_the_current_section(
    client: TestClient, path: str, label: str
) -> None:
    """Exactly one nav entry is active, and it is the one for this page."""
    body = client.get(path).text
    active = [
        chunk for chunk in body.split('<a class="nav__link')
        if chunk.startswith(" is-active")
    ]
    assert len(active) == 1, f"{path} highlighted {len(active)} nav entries"
    assert f"<span class=\"nav__label\">{label}</span>" in active[0]


def test_nav_partial_carries_live_counts(client: TestClient) -> None:
    """The polled sidebar fragment reports the backlog and queue depth."""
    body = client.get("/partials/nav?active=wanted").text
    # Three monitored missing releases: two wanted, one failed.
    assert '<span class="nav__count nav__count--hot">3</span>' in body
    assert "is-active" in body


def test_status_footer_never_leaks_the_library_secret(client: TestClient) -> None:
    """The footer shows the library path and version, and no credentials."""
    body = client.get("/partials/status-bar").text
    assert "Qobuz" in body and "Indexer" in body and "Queue" in body
    assert "X-User-Auth-Token" not in body


# -------------------------------------------------------------------- wanted
def test_wanted_lists_missing_monitored_releases(client: TestClient) -> None:
    """Wanted + failed and monitored; downloaded/skipped/ignored stay out."""
    body = client.get("/wanted").text
    assert "Release 0" in body and "Release 1" in body   # wanted
    assert "Release 3" in body                            # failed
    assert "Release 2" not in body                        # not monitored
    assert "Release 4" not in body                        # already downloaded
    assert "Release 5" not in body                        # skipped


def test_wanted_status_filter_narrows_to_one_state(client: TestClient) -> None:
    assert "Release 3" not in client.get("/wanted?status=wanted").text
    assert "Release 0" not in client.get("/wanted?status=failed").text


def test_wanted_empty_status_means_all_states(client: TestClient) -> None:
    """``?status=`` is what the dropdown submits for "show everything"."""
    response = client.get("/wanted?status=&monitored=")
    assert response.status_code == 200
    assert "Release 0" in response.text and "Release 3" in response.text


def test_wanted_search_filters_by_artist_name(client: TestClient) -> None:
    assert "Release 0" in client.get("/wanted?q=frahm").text
    assert "Release 0" not in client.get("/wanted?q=zzzznope").text


def test_download_all_queues_only_monitored_wanted(client: TestClient) -> None:
    """The bulk button skips failed, skipped, downloaded and ignored rows."""
    response = client.post("/ui/wanted/download-all", headers={"HX-Request": "true"})
    assert response.status_code == 200
    assert queued_ids(client) == QUEUEABLE


def test_download_all_is_idempotent(client: TestClient) -> None:
    client.post("/ui/wanted/download-all", headers={"HX-Request": "true"})
    second = client.post("/ui/wanted/download-all", headers={"HX-Request": "true"})

    assert second.status_code == 200
    assert client.get("/api/queue").json()["total"] == len(QUEUEABLE)


def test_single_row_download_queues_just_that_album(client: TestClient) -> None:
    album_id = next(iter(QUEUEABLE))
    response = client.post(
        f"/ui/wanted/{album_id}/queue", headers={"HX-Request": "true"}
    )
    assert response.status_code == 200
    assert queued_ids(client) == {album_id}


def test_wanted_json_api_matches_the_page(client: TestClient) -> None:
    body = client.get("/api/wanted").json()
    assert body["total"] == 3
    assert {item["id"] for item in body["items"]} == QUEUEABLE | {"aaaa1111bbbb2"}


def test_wanted_json_download_is_explicit_only(client: TestClient) -> None:
    """Nothing is queued until the endpoint is actually called."""
    assert client.get("/api/queue").json()["total"] == 0
    message = client.post("/api/wanted/download").json()["message"]
    assert "2" in message
    assert queued_ids(client) == QUEUEABLE


# ------------------------------------------------------------------- library
def test_library_defaults_to_the_artwork_grid(client: TestClient) -> None:
    body = client.get("/artists").text
    assert 'class="tiles"' in body
    assert 'class="seg__item is-active"' in body


def test_library_table_view_is_reachable(client: TestClient) -> None:
    body = client.get("/artists?view=table").text
    assert "<table" in body
    assert 'class="tiles"' not in body


def test_unknown_view_falls_back_to_a_422_not_a_crash(client: TestClient) -> None:
    """``view`` is a Literal, so a junk value is a validation error, not a 500."""
    assert client.get("/artists?view=nonsense").status_code == 422
