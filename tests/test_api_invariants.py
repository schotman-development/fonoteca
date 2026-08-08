"""The domain rules, as executable assertions against the JSON API.

These are not shape checks. Each one is a rule that has already been broken once
and each is stated here in the form "call the endpoint, look at the database":

* **the monitor toggle toggles**, and a ``monitored`` query parameter is a
  filter, never a value. On the old ``/ui`` route the calling screen's filters
  travelled on the mutating URL, so pressing the toggle while the backlog was
  filtered to "ignored only" wrote ``monitored=false`` onto whatever row was
  pressed. The SPA designs the collision out by never putting filters on a
  mutating URL — which is exactly why the test has to stay: it comes back the
  moment somebody adds a query parameter to a mutation.
* **unmonitoring a ``WANTED`` release collapses it to ``SKIPPED``** so the row
  leaves the backlog rather than sitting in it inert, and monitoring it again
  restores both. ``QUEUED`` and ``DOWNLOADING`` belong to the worker and are
  left alone.
* **a partial update is partial.** ``None``/omitted means *leave alone*. A bulk
  ``<select>`` left on "no change" that serialised as ``false`` would unmonitor
  every artist in the selection.
* **indexing marks releases wanted and queues nothing.** One follow of a
  prolific artist once pulled 1.7 GB unprompted.
* **exactly three endpoints may create a queue item.** Enumerated from the
  OpenAPI document rather than from a list in this file, so an endpoint added
  next year is covered without anybody remembering to come back here.

``LIBRARY_PATH`` and ``DATA_PATH`` are redirected under ``tmp_path`` before any
test body runs — the sweep calls every mutating endpoint there is, including the
ones that write to the library, and a failure to redirect would point them at a
real music collection.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app import config
from app.api import routes_api
from app.config import Settings, get_settings
from app.core.indexer import Indexer
from app.db import get_session
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    MonitorMode,
    QueueItem,
    QueueState,
)

FRAHM = "720076"

WANTED_ID = "uyej1o165e870"        # wanted, monitored
IGNORED_ID = "0884977859300"       # wanted, NOT monitored
FAILED_ID = "aaaa1111bbbb2"        # failed, monitored
ON_DISK = "cccc3333dddd4"          # downloaded, monitored
QUEUED_ID = "eeee5555ffff6"        # queued, monitored — the worker owns this one
DOWNLOADING_ID = "ffff7777aaaa8"   # downloading, monitored — likewise

#: (id, title, status, monitored)
SEED_ALBUMS = (
    (WANTED_ID, "All Melody", AlbumStatus.WANTED, True),
    (IGNORED_ID, "Screws", AlbumStatus.WANTED, False),
    (FAILED_ID, "Felt", AlbumStatus.FAILED, True),
    (ON_DISK, "Solo", AlbumStatus.DOWNLOADED, True),
    (QUEUED_ID, "Spaces", AlbumStatus.QUEUED, True),
    (DOWNLOADING_ID, "Wintermusik", AlbumStatus.DOWNLOADING, True),
)

#: The only three endpoints allowed to create a queue item.
QUEUEING_ENDPOINTS = {
    ("post", "/api/albums/{album_id}/queue"),
    ("post", "/api/artists/{artist_id}/download-wanted"),
    ("post", "/api/wanted/download"),
}


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
@pytest.fixture(name="settings")
def settings_fixture(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[Settings]:
    """Point LIBRARY_PATH and DATA_PATH at scratch directories, process-wide.

    The assertion is not decoration. The endpoint sweep below calls re-file,
    re-tag, NFO, quarantine and empty-trash, all of which are real writes through
    ``app/core/librarian.py``; without the redirect they would run against
    whatever ``.env`` points at.
    """
    library = tmp_path / "music"
    data = tmp_path / "data"
    library.mkdir()
    data.mkdir()
    monkeypatch.setenv("LIBRARY_PATH", str(library))
    monkeypatch.setenv("DATA_PATH", str(data))
    config.get_settings.cache_clear()
    conf = get_settings()
    assert conf.library_path == library, "refusing to run against the real library"
    assert conf.trash_dir == data / "trash"
    try:
        yield conf
    finally:
        config.get_settings.cache_clear()


@pytest.fixture(name="db")
def db_fixture(tmp_path: Path) -> Iterator[async_sessionmaker[AsyncSession]]:
    """A seeded scratch database, handed over as a session factory.

    The factory rather than only a client because two of these rules are about
    services that never see an HTTP request — the indexer is one — and the
    honest way to test "indexing queues nothing" is to index, then ask the API.
    """
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'invariants.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(
                Artist(
                    id=FRAHM,
                    name="Nils Frahm",
                    monitored=True,
                    monitor_mode=MonitorMode.ALL,
                    quality_profile="default",
                    accepted_release_types="album,ep",
                )
            )
            for album_id, title, status, monitored in SEED_ALBUMS:
                session.add(
                    Album(
                        id=album_id,
                        artist_id=FRAHM,
                        title=title,
                        status=status,
                        monitored=monitored,
                        release_type="album",
                        tracks_count=6,
                    )
                )
            # One in-flight item so retry/cancel have something real to act on.
            session.add(
                QueueItem(album_id=DOWNLOADING_ID, state=QueueState.ACTIVE)
            )
            await session.commit()

    asyncio.run(seed())
    try:
        yield maker
    finally:
        asyncio.run(engine.dispose())


@pytest.fixture(name="client")
def client_fixture(db: async_sessionmaker[AsyncSession]) -> Iterator[TestClient]:
    async def override_session() -> AsyncIterator[AsyncSession]:
        session = db()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)


def album(client: TestClient, album_id: str) -> dict[str, Any]:
    return client.get(f"/api/albums/{album_id}").json()


def artist(client: TestClient, artist_id: str = FRAHM) -> dict[str, Any]:
    return client.get(f"/api/artists/{artist_id}").json()


def queued_album_ids(client: TestClient) -> set[str]:
    """Every album with a queue entry, whatever state it is in."""
    return {
        item["album_id"] for item in client.get("/api/queue?limit=500").json()["items"]
    }


# ---------------------------------------------------------------------------
# The monitor toggle toggles (§6.2)
# ---------------------------------------------------------------------------
def test_a_bodyless_post_toggles(client: TestClient) -> None:
    """The row control sends no value at all, and two presses are a round trip."""
    assert album(client, ON_DISK)["monitored"] is True

    assert client.post(f"/api/albums/{ON_DISK}/monitor").json()["monitored"] is False
    assert client.post(f"/api/albums/{ON_DISK}/monitor").json()["monitored"] is True


def test_an_empty_body_toggles_too(client: TestClient) -> None:
    """``{}`` and no body are the same request — a client that always sends JSON
    must not get different behaviour from one that does not."""
    assert client.post(f"/api/albums/{ON_DISK}/monitor", json={}).json()["monitored"] is False


@pytest.mark.parametrize("query", ["?monitored=false", "?monitored=true", "?monitored="])
def test_a_monitored_query_parameter_is_never_the_new_value(
    client: TestClient, query: str
) -> None:
    """The rule that cost a screen full of releases.

    ``monitored`` is the name of a *filter* on three of the screens that show
    this button. Bound as the new value, pressing the toggle on the Wanted page
    filtered to "ignored only" wrote ``false`` onto whatever row was pressed —
    and the row then vanished from the list, which looks exactly like the toggle
    working.
    """
    before = album(client, ON_DISK)["monitored"]

    after = client.post(f"/api/albums/{ON_DISK}/monitor{query}").json()["monitored"]

    assert after is not before, f"{query} was read as a value, not ignored"


def test_an_explicit_body_sets_rather_than_toggles(client: TestClient) -> None:
    """The other half: a body is how a client says what it means, and it is
    idempotent — which is what makes an "unmonitor these" bulk action safe."""
    first = client.post(f"/api/albums/{ON_DISK}/monitor", json={"monitored": False})
    second = client.post(f"/api/albums/{ON_DISK}/monitor", json={"monitored": False})

    assert first.json()["monitored"] is False
    assert second.json()["monitored"] is False


def test_the_toggle_returns_the_changed_entity(client: TestClient) -> None:
    """View-agnostic: the old endpoint took a ``view`` parameter and returned a
    different table per caller. One mutation, one entity, and every screen
    re-renders from it."""
    body = client.post(f"/api/albums/{WANTED_ID}/monitor").json()

    assert body["id"] == WANTED_ID
    assert body == album(client, WANTED_ID)


# ---------------------------------------------------------------------------
# WANTED collapses to SKIPPED and back (§6.5)
# ---------------------------------------------------------------------------
def test_unmonitoring_a_wanted_release_takes_it_out_of_the_backlog(
    client: TestClient,
) -> None:
    """Otherwise the row sits in the backlog for ever, ignored and still listed —
    which is indistinguishable from the toggle not working."""
    body = client.post(f"/api/albums/{WANTED_ID}/monitor").json()

    assert body["monitored"] is False
    assert body["status"] == AlbumStatus.SKIPPED.value
    assert WANTED_ID not in {row["id"] for row in client.get("/api/wanted").json()["items"]}


def test_the_collapse_reverses(client: TestClient) -> None:
    """Two presses restore ``monitored`` **and** ``status``. A one-way collapse
    would make the toggle a trap: pressing it twice would leave the release
    monitored and skipped, which no screen offers a way out of."""
    client.post(f"/api/albums/{WANTED_ID}/monitor")
    body = client.post(f"/api/albums/{WANTED_ID}/monitor").json()

    assert body["monitored"] is True
    assert body["status"] == AlbumStatus.WANTED.value


@pytest.mark.parametrize(
    ("album_id", "status"),
    [(QUEUED_ID, AlbumStatus.QUEUED), (DOWNLOADING_ID, AlbumStatus.DOWNLOADING)],
)
def test_the_workers_states_are_left_alone(
    client: TestClient, album_id: str, status: AlbumStatus
) -> None:
    """``QUEUED`` and ``DOWNLOADING`` belong to the queue worker. Collapsing one
    to ``SKIPPED`` would leave the worker writing files into a release the
    database says nobody wants."""
    body = client.post(f"/api/albums/{album_id}/monitor").json()

    assert body["monitored"] is False
    assert body["status"] == status.value


def test_unmonitoring_never_cancels_an_in_flight_download(client: TestClient) -> None:
    """Ignoring a release reduces what Qobuzarr will do next; it does not reach
    into what it is doing now. Cancelling is its own button, on its own screen."""
    client.post(f"/api/albums/{DOWNLOADING_ID}/monitor")

    states = {
        item["album_id"]: item["state"]
        for item in client.get("/api/queue?limit=500").json()["items"]
    }

    assert states[DOWNLOADING_ID] == QueueState.ACTIVE.value


def test_a_downloaded_release_keeps_its_status(client: TestClient) -> None:
    """The collapse is only ever between ``WANTED`` and ``SKIPPED``. A downloaded
    release that got un-monitored is still on disk, and saying otherwise would
    make the disk scan and the database disagree until the next nightly run."""
    body = client.post(f"/api/albums/{ON_DISK}/monitor").json()

    assert body["status"] == AlbumStatus.DOWNLOADED.value


# ---------------------------------------------------------------------------
# Partial updates are partial (§6.3)
# ---------------------------------------------------------------------------
def test_a_bulk_update_changes_only_what_it_names(client: TestClient) -> None:
    """Setting the monitor mode for fifty artists must not reset the release
    types of all fifty — and the failure is silent, because a release type
    nobody accepts simply stops marking anything wanted."""
    before = artist(client)

    response = client.post(
        "/api/artists/bulk",
        json={"artist_ids": [FRAHM], "monitor_mode": "future"},
    )
    after = artist(client)

    assert response.status_code == 200
    assert after["monitor_mode"] == "future"
    assert after["monitored"] == before["monitored"]
    assert after["quality_profile"] == before["quality_profile"]
    assert after["accepted_release_types"] == before["accepted_release_types"]


def test_omitted_and_explicit_null_are_the_same_thing(client: TestClient) -> None:
    """Both mean *leave alone*. Tested together because a serialiser coercing
    ``null`` to ``false`` is the same bug in a new costume — and a JSON client
    reaches for ``null`` exactly where an HTML form reached for ``""``."""
    before = artist(client)

    client.post(
        "/api/artists/bulk",
        json={
            "artist_ids": [FRAHM],
            "monitored": None,
            "monitor_mode": None,
            "release_types": None,
        },
    )

    assert artist(client) == before


def test_no_change_never_means_unmonitor(client: TestClient) -> None:
    """The specific accident: the bulk dropdown's "no change" option submitted an
    empty value, and reading it as a boolean turned "change nothing" into
    "unmonitor everything selected"."""
    client.post("/api/artists/bulk", json={"artist_ids": [FRAHM]})

    assert artist(client)["monitored"] is True


def test_an_empty_selection_is_a_no_op_not_an_apply_to_all(
    client: TestClient,
) -> None:
    """The other direction of the same mistake. "No artists selected" with a real
    value set is the one case where doing what was asked would be catastrophic,
    so it says so instead."""
    response = client.post(
        "/api/artists/bulk", json={"artist_ids": [], "monitored": False}
    )

    assert response.status_code == 200
    assert response.json()["detail"]["updated"] == 0
    assert artist(client)["monitored"] is True


def test_release_types_need_an_action_to_mean_anything(client: TestClient) -> None:
    """``add`` and ``remove`` exist because "also accept singles for these twenty"
    should not require knowing what each of them already accepts."""
    client.post(
        "/api/artists/bulk",
        json={
            "artist_ids": [FRAHM],
            "release_types": ["single"],
            "release_types_action": "add",
        },
    )

    assert set(artist(client)["accepted_release_types"]) == {"album", "ep", "single"}


def test_the_single_artist_form_is_the_opposite(client: TestClient) -> None:
    """``PATCH`` with an explicit ``false`` genuinely means false. The two rules
    look contradictory and are not: a bulk edit describes a *change*, a single
    edit describes a *state*. Copying either rule onto the other form is the
    bug."""
    response = client.patch(f"/api/artists/{FRAHM}", json={"monitored": False})

    assert response.status_code == 200
    assert response.json()["monitored"] is False


def test_a_patch_still_leaves_unnamed_fields_alone(client: TestClient) -> None:
    before = artist(client)

    client.patch(f"/api/artists/{FRAHM}", json={"quality_profile": "hires"})
    after = artist(client)

    assert after["quality_profile"] == "hires"
    assert after["monitor_mode"] == before["monitor_mode"]
    assert after["accepted_release_types"] == before["accepted_release_types"]


def test_a_rejected_update_writes_nothing(client: TestClient) -> None:
    """Validation runs before any write. A 422 that had already applied half the
    payload would leave the entity in a state nobody asked for and no screen
    shows."""
    before = artist(client)

    response = client.patch(f"/api/artists/{FRAHM}", json={"monitor_mode": "sideways"})

    assert response.status_code == 422
    assert artist(client) == before


# ---------------------------------------------------------------------------
# Indexing marks wanted and queues nothing (§6.1)
# ---------------------------------------------------------------------------
class FakeQobuzClient:
    """The two methods the indexer uses, and nothing else."""

    def __init__(self, albums: list[dict[str, Any]]) -> None:
        self.albums = albums

    async def get_artist(self, artist_id: Any, **_kwargs: Any) -> dict[str, Any]:
        return {"id": str(artist_id), "name": "Nils Frahm"}

    async def iter_artist_albums(
        self, artist_id: Any, release_types: Any = None
    ) -> AsyncIterator[dict[str, Any]]:
        for raw in self.albums:
            yield raw


def catalogue_album(album_id: str, title: str) -> dict[str, Any]:
    """One album shaped like a Qobuz response."""
    return {
        "id": album_id,
        "title": title,
        "release_date_original": "2015-06-01",
        "tracks_count": 11,
        "media_count": 1,
        "release_type": "album",
        "maximum_bit_depth": 24,
        "maximum_sampling_rate": 96.0,
        "artist": {"id": FRAHM, "name": "Nils Frahm"},
    }


def test_indexing_marks_releases_wanted_and_queues_nothing(
    client: TestClient, db: async_sessionmaker[AsyncSession]
) -> None:
    """The rule the whole product is arranged around.

    ``AUTO_DOWNLOAD`` defaults to false, so ``Indexer._enqueue_wanted`` returns
    early and logs ``queue.held``: discovery is decoupled from downloading, and
    the only things that queue are three explicit presses. This is asserted
    through the indexer itself rather than through a route because the route is
    not where the decision lives — following, scanning and the nightly tick all
    arrive at the same method.
    """
    settings = Settings(
        auto_download=False,
        indexer_jitter_pct=0.0,
        default_monitor_mode="all",
        default_accepted_release_types="album,ep",
        qobuz_favorite_sync=False,
    )
    indexer = Indexer(
        FakeQobuzClient(
            [catalogue_album("newrelease001", "Music For Animals"),
             catalogue_album("newrelease002", "Old Friends New Friends")]
        ),
        settings=settings,
    )

    async def index() -> Any:
        async with db() as session:
            result = await indexer.check_artist(session, FRAHM)
            await session.commit()
            return result

    result = asyncio.run(index())

    assert result.wanted == 2
    listed = {row["id"]: row for row in client.get("/api/wanted?limit=500").json()["items"]}
    assert {"newrelease001", "newrelease002"} <= set(listed)
    assert queued_album_ids(client) == {DOWNLOADING_ID}, "indexing queued something"


def test_indexing_records_that_it_held_the_queue(
    client: TestClient, db: async_sessionmaker[AsyncSession]
) -> None:
    """It is not silent about it. A release marked wanted and not queued is the
    intended behaviour, and the activity row is how somebody watching the feed
    can tell that from a download that failed to start."""
    settings = Settings(
        auto_download=False,
        indexer_jitter_pct=0.0,
        default_monitor_mode="all",
        qobuz_favorite_sync=False,
    )
    indexer = Indexer(
        FakeQobuzClient([catalogue_album("newrelease001", "Music For Animals")]),
        settings=settings,
    )

    async def index() -> None:
        async with db() as session:
            await indexer.check_artist(session, FRAHM)
            await session.commit()

    asyncio.run(index())

    events = {row["event"] for row in client.get("/api/activity?limit=100").json()["items"]}

    assert "queue.held" in events


# ---------------------------------------------------------------------------
# Exactly three endpoints may enqueue (§6.1)
# ---------------------------------------------------------------------------
def mutating_endpoints() -> list[tuple[str, str]]:
    """Every mutating operation the application publishes, from its own schema.

    Read out of the OpenAPI document rather than listed here so an endpoint added
    later is swept without anybody remembering to come back — which is the only
    way a closed-set claim stays true.
    """
    paths = main.app.openapi()["paths"]
    return sorted(
        (method, path)
        for path, operations in paths.items()
        for method in operations
        if method in {"post", "patch", "put", "delete"}
    )


#: Bodies and path parameters for the sweep. A request that 422s on a missing
#: body proves nothing, so each endpoint is called with something it accepts.
PATH_PARAMS = {
    "{album_id}": ON_DISK,
    "{artist_id}": FRAHM,
    "{item_id}": "1",
    "{entry_id}": "no-such-batch",
    "{entity_type}": "album",
    "{entity_id}": ON_DISK,
    "{key}": "all-melody",
}

BODIES: dict[tuple[str, str], dict[str, Any]] = {
    ("post", "/api/artists"): {"artist_id": "5000", "name": "Alt-J"},
    ("post", "/api/artists/bulk"): {"artist_ids": [FRAHM], "monitored": True},
    ("patch", "/api/artists/{artist_id}"): {"monitored": True},
    ("post", "/api/albums/{album_id}/monitor"): {"monitored": True},
    ("post", "/api/library/import"): {"monitored": True, "limit": 1},
    ("post", "/api/enrichment/{entity_type}/{entity_id}/identify"): {
        "source": "musicbrainz",
        "external_id": "984f8239-8fe1-4683-9c54-10ffb14439e9",
    },
}


#: The answers a mutating endpoint is allowed to give. 503 is on the list on
#: purpose: half of these subsystems are simply not wired up in a test process
#: (and are not wired up in a half-started one either), and "not available on
#: this instance" is a documented, renderable answer rather than a failure.
#: Anything outside this set is an unhandled exception reaching the client.
DOCUMENTED_STATUSES = {200, 201, 202, 400, 404, 409, 422, 502, 503}


def resolve(path: str) -> str:
    for placeholder, value in PATH_PARAMS.items():
        path = path.replace(placeholder, value)
    return path


@pytest.fixture(name="scoped_session_scope")
def scoped_session_scope_fixture(
    db: async_sessionmaker[AsyncSession], monkeypatch: pytest.MonkeyPatch
) -> None:
    """Point the background-job session factory at the scratch database.

    ``run_integrity_verify`` deliberately does not run on the request's session —
    it opens its own short ones around minutes of hashing, because SQLite has one
    writer. That means the sweep would otherwise reach the real ``data/``
    database, so the factory is redirected rather than the endpoint skipped.
    """

    @asynccontextmanager
    async def scope() -> AsyncIterator[AsyncSession]:
        session = db()
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    monkeypatch.setattr(routes_api, "session_scope", scope)


@pytest.mark.parametrize(("method", "path"), mutating_endpoints())
def test_only_three_endpoints_ever_create_a_queue_item(
    client: TestClient,
    settings: Settings,
    scoped_session_scope: None,
    method: str,
    path: str,
) -> None:
    """Call everything. Only the three named may add to the queue.

    Following, indexing, scanning, monitoring, bulk-editing, enriching,
    re-filing, re-tagging, writing NFOs, quarantining, restoring and emptying the
    trash all queue **nothing**. Whatever each call answers — 200, 400, 404, 409
    or 503 — the assertion is the same, because "it failed" is not a defence: a
    download that started before the failure is still a download.
    """
    before = queued_album_ids(client)

    # ``request`` rather than ``client.delete`` — the latter refuses a body, and
    # two of these endpoints are DELETEs that the sweep needs to reach anyway.
    response = client.request(
        method.upper(), resolve(path), json=BODIES.get((method, path))
    )
    after = queued_album_ids(client)

    assert response.status_code in DOCUMENTED_STATUSES, response.text
    if (method, path) in QUEUEING_ENDPOINTS:
        return
    assert after <= before, f"{method.upper()} {path} queued {sorted(after - before)}"


@pytest.mark.parametrize(("method", "path"), sorted(QUEUEING_ENDPOINTS))
def test_the_three_that_may_enqueue_actually_do(
    client: TestClient, settings: Settings, method: str, path: str
) -> None:
    """The other half of a closed-set claim, and the half that makes it mean
    something: a set of three that queue nothing would satisfy the sweep above
    and leave the application unable to download anything."""
    target = resolve(path).replace(ON_DISK, WANTED_ID)

    response = getattr(client, method)(target)

    assert response.status_code == 200
    assert WANTED_ID in queued_album_ids(client)


def test_the_closed_set_is_still_three(client: TestClient) -> None:
    """A guard on the guard.

    The sweep above is parametrised over the OpenAPI document, so it grows by
    itself — but a new queueing endpoint would be swept as a *forbidden* one and
    would fail loudly, which is the intent. This asserts the allow-list has not
    quietly grown to match instead.
    """
    published = set(mutating_endpoints())

    assert QUEUEING_ENDPOINTS <= published
    assert len(QUEUEING_ENDPOINTS) == 3


def test_a_second_press_does_not_queue_twice(client: TestClient) -> None:
    """Queueing is idempotent per album, which is what makes a double-click and a
    retried request safe."""
    client.post(f"/api/albums/{WANTED_ID}/queue")
    second = client.post(f"/api/albums/{WANTED_ID}/queue")

    assert second.json()["detail"]["created"] is False
    assert client.get("/api/queue?limit=500").json()["total"] == 2  # seeded + this one


def test_queueing_an_album_on_disk_is_an_upgrade(client: TestClient) -> None:
    """``queue_album`` only promotes ``skipped``/``failed``/``wanted``, so an
    album being upgraded keeps ``status = downloaded`` for the whole download.
    The client cannot work that out afterwards, so the server says so at the
    moment it is still knowable."""
    detail = client.post(f"/api/albums/{ON_DISK}/queue").json()["detail"]

    assert detail["upgrade"] is True
    assert detail["album_status_before"] == AlbumStatus.DOWNLOADED.value
    assert album(client, ON_DISK)["status"] == AlbumStatus.DOWNLOADED.value


def test_in_flight_is_read_from_queue_state_not_status(client: TestClient) -> None:
    """Which is why ``queue_state`` exists: the upgraded album above is
    ``downloaded`` and in the queue at the same time, and only one of the two
    fields can answer "is this in flight?"."""
    client.post(f"/api/albums/{ON_DISK}/queue")

    body = album(client, ON_DISK)

    assert body["status"] == AlbumStatus.DOWNLOADED.value
    assert body["queue_state"] == QueueState.PENDING.value


# ---------------------------------------------------------------------------
# Reads never claim a change (§6.18)
# ---------------------------------------------------------------------------
READS = [
    "/api/status",
    "/api/stats",
    "/api/nav-counts",
    "/api/artists",
    f"/api/artists/{FRAHM}/albums",
    "/api/wanted",
    "/api/queue",
    "/api/activity",
    "/api/enrichment/review",
    f"/api/albums/{ON_DISK}",
]


def test_no_get_writes_anything(
    client: TestClient, db: async_sessionmaker[AsyncSession]
) -> None:
    """Every one of these is polled. A GET with a side effect writes a row every
    few seconds for as long as the tab is open, and the first symptom is a
    database that will not stop growing.
    """

    async def counts() -> tuple[int, int, int]:
        async with db() as session:
            return (
                int((await session.execute(select(func.count(Album.id)))).scalar_one()),
                int((await session.execute(select(func.count(QueueItem.id)))).scalar_one()),
                int((await session.execute(select(func.count(Artist.id)))).scalar_one()),
            )

    before = asyncio.run(counts())
    activity_before = client.get("/api/activity?limit=1000").json()["total"]

    for url in READS:
        client.get(url)
        client.get(url)

    assert asyncio.run(counts()) == before
    assert client.get("/api/activity?limit=1000").json()["total"] == activity_before
