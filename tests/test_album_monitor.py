"""The per-release monitor toggle, on every screen that lists albums.

Before this existed the toggle lived only on the artist-detail page, so
un-wanting one release out of a 500-artist backlog meant finding its artist
first. The button is now on the Wanted page, the queue, the dashboard preview
and artist detail, all posting to the same ``/ui/albums/{id}/monitor``.

Two things are worth guarding and both are here:

* The endpoint **toggles**. It never reads a submitted value. Its query string
  carries the calling screen's *filters*, one of which is called ``monitored``
  — if that were ever bound as the new value, pressing the toggle while the
  Wanted page was filtered to "ignored only" would write ``monitored=False``
  onto whatever row you pressed. That is the album-level twin of the
  ``set_monitored`` trap in the bulk artist editor.
* Ignoring a release must not queue, cancel or download anything. It only ever
  reduces what Qobuzarr will do.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; no queue worker runs and nothing touches the network.
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
OTHER_ARTIST = "861312"

#: (album id, title, status, monitored)
SEED_ALBUMS = (
    ("uyej1o165e870", "All Melody", AlbumStatus.WANTED, True),
    ("wxl78pvfqlm3b", "Spaces", AlbumStatus.WANTED, True),
    ("0884977859300", "Screws", AlbumStatus.WANTED, False),
    ("aaaa1111bbbb2", "Felt", AlbumStatus.FAILED, True),
    ("cccc3333dddd4", "Solo", AlbumStatus.DOWNLOADED, True),
    ("eeee5555ffff6", "Wintermusik", AlbumStatus.QUEUED, True),
)

WANTED_ID = "uyej1o165e870"
IGNORED_ID = "0884977859300"
QUEUED_ID = "eeee5555ffff6"

HX = {"HX-Request": "true"}


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """A TestClient over a scratch DB: two artists, six releases, one queue row."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'monitor.db'}", poolclass=NullPool
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
            session.add(Artist(id=OTHER_ARTIST, name="Janine Jansen", monitored=True))
            for album_id, title, status, monitored in SEED_ALBUMS:
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=title,
                        status=status,
                        monitored=monitored,
                        release_type="album",
                        tracks_count=6,
                    )
                )
            session.add(
                QueueItem(album_id=QUEUED_ID, state=QueueState.PENDING, priority=0)
            )
            await session.commit()

    asyncio.run(seed())

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        asyncio.run(engine.dispose())


def album(client: TestClient, album_id: str) -> dict:
    """One album's current state, read back through the JSON API."""
    return client.get(f"/api/albums/{album_id}").json()


def toggle(client: TestClient, album_id: str, query: str = "") -> object:
    """Press the monitor button for one release."""
    return client.post(f"/ui/albums/{album_id}/monitor{query}", headers=HX)


def queued_ids(client: TestClient) -> set[str]:
    return {item["album_id"] for item in client.get("/api/queue").json()["items"]}


# ------------------------------------------------------------------ discovery
@pytest.mark.parametrize(
    ("path", "view"),
    [
        ("/wanted", "view=wanted"),
        ("/queue", "view=queue"),
        ("/", "view=dashboard"),
        (f"/artists/{ARTIST_ID}", "view=artist"),
    ],
)
def test_every_album_screen_offers_the_toggle(
    client: TestClient, path: str, view: str
) -> None:
    """The point of the change: no screen makes you go somewhere else to ignore."""
    body = client.get(path).text
    assert "/ui/albums/" in body and f"/monitor?{view}" in body
    assert 'aria-pressed="true"' in body


def test_the_toggle_states_are_distinguishable(client: TestClient) -> None:
    """A monitored row and an ignored row do not render identically."""
    body = client.get("/wanted?monitored=").text
    assert 'aria-pressed="true"' in body and 'aria-pressed="false"' in body
    assert "btn--icon is-on" in body and "btn--icon is-off" in body


# --------------------------------------------------------------------- toggle
def test_ignoring_from_the_wanted_page_takes_the_row_out_of_the_backlog(
    client: TestClient,
) -> None:
    """The whole request: unmonitor a release without leaving the Wanted page."""
    assert "All Melody" in client.get("/wanted").text

    response = toggle(client, WANTED_ID, "?view=wanted")

    assert response.status_code == 200
    assert album(client, WANTED_ID)["monitored"] is False
    assert "All Melody" not in client.get("/wanted").text


def test_ignoring_a_wanted_release_also_marks_it_skipped(client: TestClient) -> None:
    """Otherwise it would sit in the backlog forever as an unmonitored row."""
    toggle(client, WANTED_ID, "?view=wanted")
    assert album(client, WANTED_ID)["status"] == AlbumStatus.SKIPPED.value


def test_monitoring_again_puts_it_back_in_the_backlog(client: TestClient) -> None:
    toggle(client, IGNORED_ID, "?view=wanted")

    state = album(client, IGNORED_ID)
    assert state["monitored"] is True
    assert state["status"] == AlbumStatus.WANTED.value
    assert "Screws" in client.get("/wanted").text


def test_the_toggle_is_symmetric(client: TestClient) -> None:
    """Two presses land back where they started, status included."""
    before = album(client, WANTED_ID)
    toggle(client, WANTED_ID, "?view=wanted")
    toggle(client, WANTED_ID, "?view=wanted")
    after = album(client, WANTED_ID)

    assert after["monitored"] == before["monitored"]
    assert after["status"] == before["status"]


def test_it_touches_exactly_one_release(client: TestClient) -> None:
    toggle(client, WANTED_ID, "?view=wanted")

    others = {row[0] for row in SEED_ALBUMS} - {WANTED_ID}
    for album_id in others:
        expected = next(row[3] for row in SEED_ALBUMS if row[0] == album_id)
        assert album(client, album_id)["monitored"] is expected


# ------------------------------------------------- the filter/value collision
def test_the_monitored_query_parameter_is_a_filter_not_a_value(
    client: TestClient,
) -> None:
    """``?monitored=true`` is the Wanted page's filter — the endpoint still toggles.

    If this ever binds as the album's new value, pressing the toggle from the
    "monitored only" view would leave a monitored album monitored and the button
    would look broken; from "ignored only" it would unmonitor whatever you
    pressed. Both are silent, so assert the toggle wins.
    """
    toggle(client, WANTED_ID, "?view=wanted&monitored=true")
    assert album(client, WANTED_ID)["monitored"] is False

    toggle(client, WANTED_ID, "?view=wanted&monitored=false")
    assert album(client, WANTED_ID)["monitored"] is True


def test_a_posted_monitored_field_cannot_set_the_value_either(
    client: TestClient,
) -> None:
    """The route takes no form body; a stray field must not become the value."""
    client.post(
        f"/ui/albums/{IGNORED_ID}/monitor?view=wanted",
        data={"monitored": "false"},
        headers=HX,
    )
    assert album(client, IGNORED_ID)["monitored"] is True


# ---------------------------------------------------------------- the fragment
@pytest.mark.parametrize(
    ("view", "marker"),
    [
        ("wanted", "missing release(s) shown"),
        ("queue", "queue entries shown"),
        ("artist", "releases shown"),
        ("dashboard", "Open</a>"),
    ],
)
def test_each_screen_gets_its_own_table_back(
    client: TestClient, view: str, marker: str
) -> None:
    """One endpoint, four callers — each must be handed the table it swaps."""
    body = toggle(client, WANTED_ID, f"?view={view}").text
    assert marker in body


def test_an_unknown_view_is_a_validation_error_not_a_crash(
    client: TestClient,
) -> None:
    assert toggle(client, WANTED_ID, "?view=nonsense").status_code == 422
    assert album(client, WANTED_ID)["monitored"] is True   # nothing was written


def test_a_missing_album_is_a_404(client: TestClient) -> None:
    assert toggle(client, "no-such-album", "?view=wanted").status_code == 404


def test_the_refreshed_wanted_table_keeps_the_active_filter(
    client: TestClient,
) -> None:
    """Toggling from a filtered view must not silently reset it to everything."""
    body = toggle(client, WANTED_ID, "?view=wanted&q=frahm&status=wanted").text

    assert "All Melody" not in body      # the row we just ignored
    assert "Spaces" in body              # still wanted, still matches ?q=frahm
    assert "Felt" not in body            # failed, excluded by ?status=wanted
    assert "q=frahm" in body             # and the row actions carry it forward


def test_the_refreshed_artist_table_keeps_the_active_filter(
    client: TestClient,
) -> None:
    body = toggle(client, WANTED_ID, "?view=artist&q=melody").text
    assert "All Melody" in body and "Spaces" not in body


def test_the_toast_says_what_it_did(client: TestClient) -> None:
    off = toggle(client, WANTED_ID, "?view=wanted")
    assert "Ignoring All Melody" in off.headers["HX-Trigger"]

    on = toggle(client, WANTED_ID, "?view=wanted")
    assert "Monitoring All Melody" in on.headers["HX-Trigger"]


# ------------------------------------------------------------------ safety net
def test_ignoring_never_queues_anything(client: TestClient) -> None:
    """The cardinal rule: only explicit user downloads may add to the queue."""
    before = queued_ids(client)
    for album_id, _title, _status, _monitored in SEED_ALBUMS:
        toggle(client, album_id, "?view=wanted")
    assert queued_ids(client) == before


def test_ignoring_from_the_queue_does_not_cancel_the_download(
    client: TestClient,
) -> None:
    """Cancel does that. The toggle only stops the release coming back."""
    toggle(client, QUEUED_ID, "?view=queue")

    assert album(client, QUEUED_ID)["monitored"] is False
    items = client.get("/api/queue").json()["items"]
    assert [item["state"] for item in items] == [QueueState.PENDING.value]


def test_a_queued_album_keeps_its_status_when_ignored(client: TestClient) -> None:
    """Only WANTED collapses to SKIPPED; an in-flight album is the worker's."""
    toggle(client, QUEUED_ID, "?view=queue")
    assert album(client, QUEUED_ID)["status"] == AlbumStatus.QUEUED.value


def test_the_queue_table_reports_whether_the_release_is_monitored(
    client: TestClient,
) -> None:
    """The toggle needs the flag denormalised onto the queue row to render it."""
    item = client.get("/api/queue").json()["items"][0]
    assert item["album_monitored"] is True

    toggle(client, QUEUED_ID, "?view=queue")
    assert client.get("/api/queue").json()["items"][0]["album_monitored"] is False


def test_download_all_counts_the_backlog_it_will_actually_queue(
    client: TestClient,
) -> None:
    """The button ignores the page filter, so its label must not follow it."""
    unfiltered = client.get("/wanted").text
    filtered = client.get("/wanted?q=melody").text

    assert "Download all (2)" in unfiltered     # two monitored WANTED releases
    assert "Download all (2)" in filtered       # one row shown, still queues two
    assert "not just the 1 row(s)" in filtered


def test_ignoring_shrinks_what_download_all_would_take(client: TestClient) -> None:
    toggle(client, WANTED_ID, "?view=wanted")
    assert "Download all (1)" in client.get("/wanted").text


def test_the_artist_is_left_alone(client: TestClient) -> None:
    """Ignoring a release says nothing about whether the artist is monitored."""
    toggle(client, WANTED_ID, "?view=wanted")
    assert client.get(f"/api/artists/{ARTIST_ID}").json()["monitored"] is True
    assert client.get(f"/api/artists/{OTHER_ARTIST}").json()["monitored"] is True
