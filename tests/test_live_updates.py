"""Tests for the live-updating UI: polled fragments and the refresh event.

Every counter and status readout in Qobuzarr now re-fetches itself instead of
going stale until someone reloads the page. Two mechanisms carry that, and both
are easy to break silently:

* **Polled regions.** A page renders its first frame inline, then a
  ``hx-trigger="every Ns"`` element re-fetches the same partial. The page and
  the fragment therefore have to render the same markup from the same context,
  or a poll would visibly change the page a few seconds after it loaded. The
  tests below fetch both halves and compare.

* **``qobuzarr:refresh``.** Every ``/ui/*`` response fires it on the body so a
  press updates the counters immediately rather than at the next tick. It is
  emitted by ``_fragment``, which is the single funnel for those responses — so
  the test is that *every* mutating action carries it, not a sampled few.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; no worker runs and nothing touches the network.
"""

from __future__ import annotations

import asyncio
import json
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

#: (album id, title, status) — one of each state the dashboard counts.
SEED_ALBUMS = (
    ("uyej1o165e870", "Spaces", AlbumStatus.WANTED),
    ("wxl78pvfqlm3b", "Felt", AlbumStatus.WANTED),
    ("0884977859300", "Screws", AlbumStatus.DOWNLOADED),
    ("aaaa1111bbbb2", "Solo", AlbumStatus.QUEUED),
)

#: Every region that refreshes itself, as (page, region id, fragment URL).
LIVE_REGIONS = [
    ("/", "dash-metrics", "/partials/dashboard/metrics"),
    ("/", "dash-artists", "/partials/dashboard/artists"),
    ("/", "dash-queue", "/partials/dashboard/queue"),
    ("/", "dash-status", "/partials/dashboard/status"),
    ("/", "dash-wanted", "/partials/dashboard-wanted"),
    ("/", "dash-activity", "/partials/activity?limit=12"),
    ("/wanted", "wanted-rows", "/partials/wanted-rows"),
    ("/queue", "queue-table", "/partials/queue-table"),
    ("/activity", "activity-rows", "/partials/activity"),
    (f"/artists/{ARTIST_ID}", "artist-stats", f"/partials/artists/{ARTIST_ID}/stats"),
    (f"/artists/{ARTIST_ID}", "album-rows", f"/partials/albums/{ARTIST_ID}"),
]


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """A TestClient over a scratch DB with one artist, four albums, one queue row."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'live.db'}", poolclass=NullPool
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
            for album_id, title, status in SEED_ALBUMS:
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=title,
                        status=status,
                        monitored=True,
                        release_type="album",
                        tracks_count=6,
                    )
                )
            session.add(
                QueueItem(
                    album_id="aaaa1111bbbb2",
                    state=QueueState.PENDING,
                    progress_tracks_total=6,
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


def region(body: str, region_id: str) -> str:
    """The opening tag of the live region with *region_id*."""
    marker = f'id="{region_id}"'
    assert marker in body, f"no live region {region_id!r} on the page"
    start = body.rindex("<", 0, body.index(marker))
    return body[start : body.index(">", start) + 1]


def squeeze(html: str) -> str:
    """Collapse whitespace so indentation differences do not matter."""
    return " ".join(html.split())


def triggers(response: object) -> dict[str, object]:
    """The decoded ``HX-Trigger`` header of an HTMX response."""
    raw = response.headers.get("HX-Trigger")  # type: ignore[attr-defined]
    assert raw, "response carried no HX-Trigger header"
    return json.loads(raw)


# ------------------------------------------------------------------ fragments
@pytest.mark.parametrize(("page", "region_id", "url"), LIVE_REGIONS)
def test_every_live_region_polls_itself(
    client: TestClient, page: str, region_id: str, url: str
) -> None:
    """The region declares a poll interval and the URL it polls."""
    tag = region(client.get(page).text, region_id)
    assert "hx-trigger=" in tag and "every " in tag
    assert f'hx-get="{url}"' in tag


@pytest.mark.parametrize(("page", "region_id", "url"), LIVE_REGIONS)
def test_every_live_region_reacts_to_a_change_at_once(
    client: TestClient, page: str, region_id: str, url: str
) -> None:
    """Waiting out the poll interval is a fallback, not the only path."""
    assert "qobuzarr:refresh from:body" in region(client.get(page).text, region_id)


@pytest.mark.parametrize(("page", "region_id", "url"), LIVE_REGIONS)
def test_every_fragment_is_a_fragment(
    client: TestClient, page: str, region_id: str, url: str
) -> None:
    """A polled URL returns markup to swap in, never a whole page."""
    response = client.get(url)
    assert response.status_code == 200
    assert "<html" not in response.text and "<body" not in response.text
    assert "{{" not in response.text and "{%" not in response.text


@pytest.mark.parametrize(
    "fragment_url",
    [
        "/partials/dashboard/metrics",
        "/partials/dashboard/status",
        "/partials/dashboard/queue",
        "/partials/dashboard/artists",
        "/partials/dashboard-wanted",
    ],
)
def test_dashboard_first_frame_matches_the_fragment(
    client: TestClient, fragment_url: str
) -> None:
    """The inline render and the polled render are byte-identical.

    Sharing the template is not enough — the fragment route has to build the
    same context too. Give it a different row limit and the page would visibly
    rearrange itself a few seconds after loading. Comparing the whole fragment,
    rather than sampling lines out of it, is what catches that.
    """
    assert squeeze(client.get(fragment_url).text) in squeeze(client.get("/").text)


# -------------------------------------------------------------------- content
def test_metrics_fragment_counts_the_library(client: TestClient) -> None:
    body = client.get("/partials/dashboard/metrics").text
    assert "artists followed" in body and "albums wanted" in body
    assert ">1</span></a> artists followed" in body


def test_status_fragment_carries_indexer_and_limiter(client: TestClient) -> None:
    body = client.get("/partials/dashboard/status").text
    assert "Indexer" in body and "Rate limiter" in body and "Albums by status" in body


def test_queue_fragment_shows_the_pending_download(client: TestClient) -> None:
    body = client.get("/partials/dashboard/queue").text
    assert "Solo" in body and "pending" in body


def test_artists_fragment_lists_the_indexer_backlog(client: TestClient) -> None:
    body = client.get("/partials/dashboard/artists").text
    assert "Nils Frahm" in body and "All 1 artists" in body


def test_artist_stats_fragment_reports_the_split(client: TestClient) -> None:
    body = client.get(f"/partials/artists/{ARTIST_ID}/stats").text
    # Two wanted plus one queued count as outstanding; one is on disk.
    assert "3 release(s) wanted, 1 downloaded." in body


def test_artist_stats_fragment_404s_for_a_stranger(client: TestClient) -> None:
    assert client.get("/partials/artists/nobody/stats").status_code == 404


def test_a_change_is_visible_on_the_next_poll(client: TestClient) -> None:
    """The whole point: no reload needed for the counters to catch up."""
    before = client.get(f"/partials/artists/{ARTIST_ID}/stats").text
    assert "3 release(s) wanted, 1 downloaded." in before

    response = client.post(
        "/api/albums/uyej1o165e870/monitor", json={"monitored": False}
    )
    assert response.status_code == 200

    after = client.get(f"/partials/artists/{ARTIST_ID}/stats").text
    assert "2 release(s) wanted, 1 downloaded." in after


# --------------------------------------------------------------------- filters
def test_filtered_regions_carry_their_filters_into_the_poll(
    client: TestClient,
) -> None:
    """A poll must not reset a table the user has filtered.

    Both filterable regions include their form rather than a fixed URL, so
    whatever is typed or selected is re-sent on every tick.
    """
    wanted = region(client.get("/wanted").text, "wanted-rows")
    assert 'hx-include="#wanted-filters"' in wanted
    assert 'id="wanted-filters"' in client.get("/wanted").text

    detail = client.get(f"/artists/{ARTIST_ID}").text
    assert 'hx-include="#artist-filters"' in region(detail, "album-rows")
    assert 'id="artist-filters"' in detail

    activity = client.get("/activity").text
    assert 'hx-include="#activity-filters"' in region(activity, "activity-rows")
    assert 'id="activity-filters"' in activity


def test_the_polled_wanted_fragment_honours_those_filters(client: TestClient) -> None:
    """What the form sends is what the fragment filters on."""
    everything = client.get("/partials/wanted-rows?q=&status=&monitored=").text
    assert "Spaces" in everything and "Felt" in everything

    narrowed = client.get("/partials/wanted-rows?q=felt&status=&monitored=true").text
    assert "Felt" in narrowed and "Spaces" not in narrowed


def test_the_polled_album_fragment_honours_those_filters(client: TestClient) -> None:
    url = f"/partials/albums/{ARTIST_ID}"
    assert "Screws" in client.get(f"{url}?q=&status=&release_type=").text
    assert "Screws" not in client.get(f"{url}?q=&status=wanted&release_type=").text


def test_the_polled_activity_fragment_honours_those_filters(
    client: TestClient,
) -> None:
    """The page's filters reach the fragment; the dashboard sends only a limit.

    Before the Activity page started tailing itself the fragment took a limit
    and nothing else, which would have made every poll reset the filters.
    """
    client.post("/ui/albums/uyej1o165e870/queue", headers={"HX-Request": "true"})

    unfiltered = client.get("/partials/activity?level=&event=&limit=200").text
    assert "queue.add" in unfiltered

    filtered = client.get("/partials/activity?level=&event=no.such.event&limit=200")
    assert "queue.add" not in filtered.text


# --------------------------------------------------------------- refresh event
#: One representative call per mutating handler shape (path, method, body).
MUTATIONS = [
    ("/ui/albums/uyej1o165e870/monitor", {}),
    ("/ui/albums/uyej1o165e870/queue", {}),
    (f"/ui/artists/{ARTIST_ID}/scan", {}),
    ("/ui/wanted/download-all", {}),
    ("/ui/scan-all", {}),
]


@pytest.mark.parametrize(("path", "body"), MUTATIONS)
def test_every_mutation_asks_the_live_regions_to_refresh(
    client: TestClient, path: str, body: dict[str, str]
) -> None:
    """``_fragment`` is the funnel, so this holds for actions added later too."""
    response = client.post(path, data=body, headers={"HX-Request": "true"})
    assert response.status_code == 200
    assert triggers(response).get("qobuzarr:refresh") is True


def test_a_refresh_does_not_displace_the_toast(client: TestClient) -> None:
    """The event rides along with the message; it does not replace it."""
    response = client.post(
        "/ui/albums/uyej1o165e870/monitor", headers={"HX-Request": "true"}
    )
    fired = triggers(response)
    assert fired.get("qobuzarr:refresh") is True
    assert "qobuzarr:toast" in fired


def test_read_only_fragments_do_not_claim_something_changed(
    client: TestClient,
) -> None:
    """A poll response firing the event would make every region refresh forever."""
    for url in ("/partials/dashboard/metrics", "/partials/nav", "/partials/status-bar"):
        assert "qobuzarr:refresh" not in client.get(url).headers.get("HX-Trigger", "")
