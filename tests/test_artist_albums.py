"""The artist page's release search, and cover art in the album tables.

The search box swaps ``#album-rows`` live via ``/partials/albums/{id}``, so the
page route, the fragment route and the JSON route must all filter identically —
the tests below drive all three with the same terms.

Cover art is hotlinked from Qobuz and is frequently absent (the indexer records
releases long before anyone downloads them), so both branches of the ``cover``
macro are asserted: a real ``<img>`` when there is a URL, a placeholder when
there is not.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; nothing touches ``data/`` or the network.
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
from app.db import get_session
from app.models import Album, AlbumStatus, Artist, Base, QueueItem, QueueState

ARTIST_ID = "720076"
OTHER_ID = "35135"
COVER = "https://static.qobuz.com/images/covers/ab/cd/abcdef_600.jpg"

#: (id, title, version, label, status, image_url)
SEED = (
    ("uyej1o165e870", "Spaces", None, "Erased Tapes", AlbumStatus.WANTED, COVER),
    ("wxl78pvfqlm3b", "Solo", "Deluxe Edition", "Erased Tapes", AlbumStatus.WANTED, None),
    ("0884977859300", "All Melody", None, "Erased Tapes", AlbumStatus.DOWNLOADED, COVER),
    ("aaaa1111bbbb2", "Felt", None, "Kning Disk", AlbumStatus.SKIPPED, None),
)


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """One artist with four releases, plus a decoy album on a second artist."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'albums.db'}", poolclass=NullPool
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
            session.add(Artist(id=OTHER_ID, name="Janine Jansen", monitored=True))
            for album_id, title, version, label, status, image in SEED:
                session.add(
                    Album(
                        id=album_id, artist_id=ARTIST_ID, title=title,
                        version=version, label=label, status=status,
                        image_url=image, release_type="album", tracks_count=6,
                    )
                )
            # Same word, different artist: must never surface on Nils Frahm.
            session.add(
                Album(
                    id="zzzz9999yyyy8", artist_id=OTHER_ID, title="Spaces Between",
                    status=AlbumStatus.WANTED, release_type="album", tracks_count=4,
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


def page(client: TestClient, query: str = "") -> str:
    return client.get(f"/legacy/artists/{ARTIST_ID}{query}").text


def rows(client: TestClient, query: str = "") -> str:
    """The fragment the search box actually swaps in."""
    return client.get(f"/legacy/partials/albums/{ARTIST_ID}{query}").text


# --------------------------------------------------------------------- search
def test_search_box_targets_the_album_fragment(client: TestClient) -> None:
    """The input posts to the partial, not the whole page, as you type."""
    body = page(client)
    assert 'name="q"' in body
    assert f'hx-get="/partials/albums/{ARTIST_ID}"' in body
    assert 'hx-target="#album-rows"' in body
    assert "keyup changed delay:300ms" in body


def test_search_matches_the_title(client: TestClient) -> None:
    body = rows(client, "?q=melody")
    assert "All Melody" in body
    assert "Spaces" not in body


def test_search_matches_the_edition(client: TestClient) -> None:
    """Version is part of how you remember a release."""
    body = rows(client, "?q=deluxe")
    assert "Solo" in body
    assert "All Melody" not in body


def test_search_matches_the_label(client: TestClient) -> None:
    body = rows(client, "?q=kning")
    assert "Felt" in body
    assert "Spaces" not in body


def test_search_is_case_insensitive(client: TestClient) -> None:
    assert "All Melody" in rows(client, "?q=ALL+MELODY")


def test_search_ignores_surrounding_whitespace(client: TestClient) -> None:
    assert "All Melody" in rows(client, "?q=%20%20melody%20%20")


def test_empty_search_returns_everything(client: TestClient) -> None:
    """``?q=`` is what an emptied search box submits — not a request for nothing."""
    body = rows(client, "?q=")
    for _, title, *_ in SEED:
        assert title in body


def test_search_does_not_cross_artists(client: TestClient) -> None:
    """A matching album belonging to someone else must not leak in."""
    assert "Spaces Between" not in rows(client, "?q=spaces")


def test_search_combines_with_the_status_filter(client: TestClient) -> None:
    body = rows(client, "?q=erased&status=wanted")
    assert "Spaces" in body and "Solo" in body
    assert "All Melody" not in body       # right label, wrong status


def test_no_match_says_so_rather_than_claiming_an_empty_library(
    client: TestClient,
) -> None:
    body = rows(client, "?q=zzzznope")
    assert "No release matches" in body
    assert "No releases recorded yet" not in body


def test_search_survives_a_full_page_load(client: TestClient) -> None:
    """Submitting the form (no JS) filters server-side and refills the box."""
    body = page(client, "?q=melody")
    assert "All Melody" in body
    assert "Spaces" not in body
    assert 'value="melody"' in body


def test_json_api_filters_the_same_way(client: TestClient) -> None:
    body = client.get(f"/api/artists/{ARTIST_ID}/albums?q=deluxe").json()
    assert body["total"] == 1
    assert body["items"][0]["title"] == "Solo"


# ------------------------------------------------------------------ cover art
def test_album_rows_show_cover_art(client: TestClient) -> None:
    body = rows(client)
    assert f'<img src="{COVER}"' in body
    assert 'loading="lazy"' in body
    assert 'referrerpolicy="no-referrer"' in body


def test_missing_cover_falls_back_to_a_placeholder(client: TestClient) -> None:
    """Two of the four seeded releases have no artwork."""
    body = rows(client)
    assert body.count('class="art__fallback"') == 2


def test_every_album_row_has_exactly_one_thumbnail(client: TestClient) -> None:
    """Art or placeholder — the row height must not depend on the CDN."""
    body = rows(client)
    assert body.count('class="art"') == len(SEED)


def test_queue_rows_show_cover_art(client: TestClient) -> None:
    body = client.get("/legacy/partials/queue-table").text
    assert f'<img src="{COVER}"' in body


def test_wanted_rows_show_cover_art(client: TestClient) -> None:
    body = client.get("/legacy/partials/wanted-rows").text
    assert f'<img src="{COVER}"' in body


def test_queue_json_exposes_the_cover(client: TestClient) -> None:
    item = client.get("/api/queue").json()["items"][0]
    assert item["album_image_url"] == COVER
