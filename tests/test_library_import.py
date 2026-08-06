"""Bulk import: turning artists found on disk into followed artists.

The Qobuz client is a stub throughout — the point of these tests is the
matching policy and the pacing, not HTTP.  The stub records every search it is
asked for, which is what lets the important assertions be about **how many API
calls happened** and **which artists were followed**, rather than about
plumbing.

The rules being pinned down here:

* exactly one search per artist, never a second ``artist/get``;
* only an exact normalised name match is followed automatically;
* following in bulk does **not** index, because 500 back-catalogue imports at
  once would be tens of thousands of calls;
* a run can be cancelled, and a wall of Qobuz failures aborts it rather than
  grinding through five hundred of them.

One harness caveat: ``TestClient`` tears its event loop down when a request
returns, so a background task started inside a request does **not** survive it —
under uvicorn the loop outlives the response and it does.  The HTTP tests here
therefore check what the route itself owns (status codes, headers, rendered
markup) and drive the importer directly whenever a *completed* run is needed.
"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.api import routes_api
from app.config import Settings, get_settings
from app.core.importer import LibraryImporter, load_last_import
from app.core.indexer import Indexer
from app.core.scanner import LibraryScanner
from app.db import get_session
from app.models import Activity, Artist, Base, MonitorMode
from app.qobuz.errors import QobuzError
from tests.test_library_scan import write_album


# ---------------------------------------------------------------------------
# Doubles
# ---------------------------------------------------------------------------
class FakeClient:
    """A Qobuz client that answers artist searches from a canned catalogue."""

    def __init__(self, catalogue: dict[str, list[dict[str, Any]]] | None = None) -> None:
        self.catalogue = catalogue or {}
        self.searches: list[str] = []
        self.artist_gets: list[str] = []
        self.fail_with: Exception | None = None
        self.delay = 0.0

    async def search_artists(
        self, query: str, limit: int = 25, offset: int = 0
    ) -> list[dict[str, Any]]:
        self.searches.append(query)
        if self.delay:
            await asyncio.sleep(self.delay)
        if self.fail_with is not None:
            raise self.fail_with
        return list(self.catalogue.get(query, []))

    async def get_artist(self, artist_id: str, with_albums: bool = False) -> dict[str, Any]:
        # Recorded so a test can prove the importer never calls it: the search
        # hit already carries everything the artist row needs.
        self.artist_gets.append(str(artist_id))
        return {"id": artist_id, "name": f"Artist {artist_id}"}


def hit(artist_id: str, name: str, albums: int = 10) -> dict[str, Any]:
    """One raw Qobuz artist search result."""
    return {
        "id": artist_id,
        "name": name,
        "albums_count": albums,
        "slug": name.lower().replace(" ", "-"),
        "image": {"large": f"https://example.invalid/{artist_id}.jpg"},
    }


CATALOGUE: dict[str, list[dict[str, Any]]] = {
    # Exact match, plus a near-miss Qobuz really does return for this query.
    "Joanne Shaw Taylor": [
        hit("1464909", "Joanne Shaw Taylor", 126),
        hit("14775306", "Joanna Shaw Taylor", 1),
    ],
    # Exact match once accents and the article are folded.
    "Thorbjorn Risager": [hit("2000", "Thorbjørn Risager", 30)],
    "The Robert Cray Band": [hit("3000", "Robert Cray Band", 44)],
    # Two exact duplicates; the fuller discography must win.
    "Molly Miller": [hit("4001", "Molly Miller", 2), hit("4002", "Molly Miller", 17)],
    # Only near misses — must go to review, never be guessed at.
    "Some Local Band": [hit("5000", "Some Local Bands", 3), hit("5001", "Local Band", 9)],
    # Nothing at all.
    "Not On Qobuz": [],
}


@pytest.fixture(name="library")
def library_fixture(tmp_path: Path) -> Path:
    """A library folder holding one album for each name in the catalogue."""
    root = tmp_path / "music"
    for artist in CATALOGUE:
        write_album(root, artist, "A Record (2020)", tracks=2, year="2020")
    write_album(root, "Joanne Shaw Taylor", "Another Record (2021)", tracks=3, year="2021")
    return root


@pytest.fixture(name="settings")
def settings_fixture(library: Path) -> Settings:
    return get_settings().model_copy(
        update={"library_path": library, "qobuz_min_request_interval": 2.0}
    )


@pytest.fixture(name="session_maker")
def session_maker_fixture(tmp_path: Path) -> Iterator[async_sessionmaker[AsyncSession]]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'import.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def create() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)

    asyncio.run(create())
    try:
        yield maker
    finally:
        asyncio.run(engine.dispose())


@pytest.fixture(name="factory")
def factory_fixture(session_maker: Any) -> Any:
    """A ``SessionFactory``-shaped context manager over the scratch database."""
    from contextlib import asynccontextmanager

    @asynccontextmanager
    async def factory() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    return factory


@pytest.fixture(name="client_stub")
def client_stub_fixture() -> FakeClient:
    return FakeClient(CATALOGUE)


@pytest.fixture(name="importer")
def importer_fixture(
    client_stub: FakeClient, settings: Settings, factory: Any
) -> LibraryImporter:
    return LibraryImporter(
        client_stub,  # type: ignore[arg-type]
        Indexer(client_stub, settings=settings),  # type: ignore[arg-type]
        LibraryScanner(settings=settings),
        settings=settings,
        session_factory=factory,
    )


def run_import(importer: LibraryImporter, **kwargs: Any) -> dict[str, Any]:
    """Start an import and wait for it to finish."""

    async def go() -> dict[str, Any]:
        await importer.start(**kwargs)
        await importer.wait()
        return importer.snapshot()

    return asyncio.run(go())


def followed(session_maker: Any) -> dict[str, Artist]:
    """Every followed artist, keyed by id."""

    async def run() -> dict[str, Artist]:
        async with session_maker() as session:
            rows = await session.execute(select(Artist))
            return {row.id: row for row in rows.scalars().unique().all()}

    return asyncio.run(run())


# ---------------------------------------------------------------------------
# What gets looked up
# ---------------------------------------------------------------------------
def test_preview_lists_the_unfollowed_artists_without_calling_qobuz(
    importer: LibraryImporter, client_stub: FakeClient, session_maker: Any
) -> None:
    async def run() -> list[dict[str, Any]]:
        async with session_maker() as session:
            return await importer.preview(session)

    names = {row["name"] for row in asyncio.run(run())}
    assert names == set(CATALOGUE)
    assert client_stub.searches == []


def test_one_folder_per_artist_not_per_album(
    importer: LibraryImporter, client_stub: FakeClient
) -> None:
    """Joanne Shaw Taylor has two albums on disk and must be searched once."""
    run_import(importer)
    assert client_stub.searches.count("Joanne Shaw Taylor") == 1


def test_exactly_one_api_call_per_artist(
    importer: LibraryImporter, client_stub: FakeClient
) -> None:
    """The search hit carries everything, so artist/get is never needed."""
    run_import(importer)
    assert len(client_stub.searches) == len(CATALOGUE)
    assert client_stub.artist_gets == []


def test_the_estimate_is_calls_times_the_minimum_interval(
    importer: LibraryImporter,
) -> None:
    assert importer.estimate_seconds(500) == pytest.approx(1000.0)


# ---------------------------------------------------------------------------
# Matching policy
# ---------------------------------------------------------------------------
def test_an_exact_name_is_followed(
    importer: LibraryImporter, session_maker: Any
) -> None:
    run_import(importer)
    assert "1464909" in followed(session_maker)


def test_a_near_miss_is_never_followed(
    importer: LibraryImporter, session_maker: Any
) -> None:
    """"Joanna Shaw Taylor" is a different person; guessing is not allowed."""
    run_import(importer)
    assert "14775306" not in followed(session_maker)


def test_accents_and_articles_are_folded_when_matching(
    importer: LibraryImporter, session_maker: Any
) -> None:
    rows = followed(session_maker)
    run_import(importer)
    rows = followed(session_maker)
    assert rows["2000"].name == "Thorbjørn Risager"  # folder said "Thorbjorn"
    assert rows["3000"].name == "Robert Cray Band"  # folder said "The Robert Cray Band"


def test_duplicate_exact_matches_resolve_to_the_bigger_discography(
    importer: LibraryImporter, session_maker: Any
) -> None:
    run_import(importer)
    rows = followed(session_maker)
    assert "4002" in rows  # 17 releases
    assert "4001" not in rows  # 2 releases


def test_a_name_with_no_exact_match_goes_to_review_with_candidates(
    importer: LibraryImporter, session_maker: Any
) -> None:
    progress = run_import(importer)
    entry = next(row for row in progress["review"] if row["name"] == "Some Local Band")
    assert entry["reason"] == "no-exact-match"
    assert [c["name"] for c in entry["candidates"]] == ["Some Local Bands", "Local Band"]
    assert "5000" not in followed(session_maker)


def test_a_name_qobuz_has_never_heard_of_is_reported_separately(
    importer: LibraryImporter,
) -> None:
    progress = run_import(importer)
    entry = next(row for row in progress["review"] if row["name"] == "Not On Qobuz")
    assert entry["reason"] == "not-found"
    assert entry["candidates"] == []
    assert progress["not_found"] == 1


def test_the_review_row_carries_the_disk_context(importer: LibraryImporter) -> None:
    """Deciding needs to know how much music is at stake, and where it is."""
    progress = run_import(importer)
    entry = next(row for row in progress["review"] if row["name"] == "Some Local Band")
    assert entry["files"] == 2
    assert entry["albums"] == 1
    assert entry["path"].endswith("Some Local Band")


def test_the_counts_add_up(importer: LibraryImporter) -> None:
    progress = run_import(importer)
    assert progress["processed"] == len(CATALOGUE)
    assert (
        progress["followed"]
        + progress["already_followed"]
        + progress["needs_review"]
        + progress["not_found"]
        + progress["failed"]
    ) == progress["processed"]


# ---------------------------------------------------------------------------
# What it deliberately does not do
# ---------------------------------------------------------------------------
def test_importing_does_not_index_any_catalogue(
    importer: LibraryImporter, session_maker: Any
) -> None:
    """500 back-catalogue imports at once is the thing this must never do."""
    run_import(importer)

    async def events() -> list[str]:
        async with session_maker() as session:
            rows = await session.execute(select(Activity.event))
            return [row[0] for row in rows.all()]

    assert not any(event.startswith("indexer.") for event in asyncio.run(events()))


def test_importing_queues_nothing(importer: LibraryImporter, session_maker: Any) -> None:
    from app.models import QueueItem

    run_import(importer)

    async def count() -> int:
        async with session_maker() as session:
            rows = await session.execute(select(QueueItem))
            return len(rows.scalars().all())

    assert asyncio.run(count()) == 0


def seed_followed(session_maker: Any, artist_id: str, name: str) -> None:
    """Pre-follow an artist so the "we already have this" paths can be exercised."""

    async def run() -> None:
        async with session_maker() as session:
            session.add(Artist(id=artist_id, name=name, monitor_mode=MonitorMode.NONE))
            await session.commit()

    asyncio.run(run())


def test_a_followed_artist_never_reaches_the_search_at_all(
    importer: LibraryImporter, client_stub: FakeClient, session_maker: Any
) -> None:
    """The disk scan resolves the folder, so the name is not even on the work list."""
    seed_followed(session_maker, "1464909", "Joanne Shaw Taylor")
    progress = run_import(importer)

    assert "Joanne Shaw Taylor" not in client_stub.searches
    assert progress["total"] == len(CATALOGUE) - 1


def test_an_already_followed_artist_is_not_reconfigured(
    importer: LibraryImporter, session_maker: Any
) -> None:
    """Reached by name (or by a folder spelled differently): touch nothing."""
    seed_followed(session_maker, "1464909", "Joanne Shaw Taylor")
    progress = run_import(importer, names=["Joanne Shaw Taylor"])

    assert progress["already_followed"] == 1
    assert progress["followed"] == 0
    # Settings the user already tuned must survive an import.
    assert followed(session_maker)["1464909"].monitor_mode is MonitorMode.NONE


def test_running_it_twice_follows_nothing_new(
    importer: LibraryImporter, session_maker: Any
) -> None:
    first = run_import(importer)
    before = set(followed(session_maker))
    second = run_import(importer)

    assert first["followed"] > 0
    assert second["followed"] == 0
    assert set(followed(session_maker)) == before


def test_the_second_run_only_searches_what_is_still_unknown(
    importer: LibraryImporter, client_stub: FakeClient
) -> None:
    """Resumability: matched artists drop out of the work list."""
    run_import(importer)
    client_stub.searches.clear()
    run_import(importer)

    assert "Joanne Shaw Taylor" not in client_stub.searches
    assert "Some Local Band" in client_stub.searches


# ---------------------------------------------------------------------------
# Options
# ---------------------------------------------------------------------------
def test_the_limit_caps_the_run(
    importer: LibraryImporter, client_stub: FakeClient
) -> None:
    progress = run_import(importer, limit=2)
    assert progress["total"] == 2
    assert len(client_stub.searches) == 2


def test_explicit_names_skip_the_disk_scan(
    importer: LibraryImporter, client_stub: FakeClient
) -> None:
    progress = run_import(importer, names=["Joanne Shaw Taylor"])
    assert progress["total"] == 1
    assert client_stub.searches == ["Joanne Shaw Taylor"]


def test_artists_can_be_followed_unmonitored(
    importer: LibraryImporter, session_maker: Any
) -> None:
    run_import(importer, names=["Joanne Shaw Taylor"], monitored=False)
    assert followed(session_maker)["1464909"].monitored is False


def test_the_monitor_mode_is_applied_to_new_artists(
    importer: LibraryImporter, session_maker: Any
) -> None:
    run_import(importer, names=["Joanne Shaw Taylor"], monitor_mode=MonitorMode.FUTURE)
    assert followed(session_maker)["1464909"].monitor_mode is MonitorMode.FUTURE


# ---------------------------------------------------------------------------
# Control and failure
# ---------------------------------------------------------------------------
def test_only_one_run_at_a_time(importer: LibraryImporter, client_stub: FakeClient) -> None:
    client_stub.delay = 0.05

    async def go() -> None:
        await importer.start()
        with pytest.raises(RuntimeError, match="already running"):
            await importer.start()
        await importer.cancel()
        await importer.wait()

    asyncio.run(go())


def test_cancelling_stops_the_run_early(
    importer: LibraryImporter, client_stub: FakeClient
) -> None:
    client_stub.delay = 0.02

    async def go() -> dict[str, Any]:
        await importer.start()
        await asyncio.sleep(0.03)
        assert await importer.cancel() is True
        await importer.wait()
        return importer.snapshot()

    progress = asyncio.run(go())
    assert progress["cancelled"] is True
    assert progress["running"] is False
    assert progress["processed"] < progress["total"]


def test_cancelling_when_idle_says_so(importer: LibraryImporter) -> None:
    assert asyncio.run(importer.cancel()) is False


def test_a_wall_of_failures_aborts_instead_of_grinding_on(
    importer: LibraryImporter, client_stub: FakeClient
) -> None:
    """A tripped breaker fails every search; do not fail 500 times in a row."""
    client_stub.fail_with = QobuzError("circuit breaker is open")
    progress = run_import(importer)

    assert progress["failed"] == 5
    assert progress["processed"] == 5
    assert "consecutive Qobuz failures" in progress["aborted_reason"]


def test_one_bad_name_does_not_stop_the_rest(
    importer: LibraryImporter, client_stub: FakeClient, session_maker: Any
) -> None:
    original = client_stub.search_artists

    async def flaky(query: str, limit: int = 25, offset: int = 0) -> list[dict[str, Any]]:
        if query == "Thorbjorn Risager":
            client_stub.searches.append(query)
            raise QobuzError("boom")
        return await original(query, limit, offset)

    client_stub.search_artists = flaky  # type: ignore[assignment]
    progress = run_import(importer)

    assert progress["failed"] == 1
    assert progress["processed"] == len(CATALOGUE)
    assert "1464909" in followed(session_maker)  # the rest still ran


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------
def test_the_result_is_stored_and_read_back(
    importer: LibraryImporter, session_maker: Any
) -> None:
    progress = run_import(importer)

    async def stored() -> dict[str, Any] | None:
        async with session_maker() as session:
            return await load_last_import(session)

    data = asyncio.run(stored())
    assert data is not None
    assert data["followed"] == progress["followed"]
    assert data["summary"] == progress["summary"]


def test_one_activity_row_per_run(
    importer: LibraryImporter, session_maker: Any
) -> None:
    run_import(importer)

    async def rows() -> list[Activity]:
        async with session_maker() as session:
            result = await session.execute(
                select(Activity).where(Activity.event == "library.import")
            )
            return list(result.scalars().all())

    entries = asyncio.run(rows())
    assert len(entries) == 1
    assert "followed" in entries[0].message


def test_the_snapshot_carries_an_eta(importer: LibraryImporter) -> None:
    """The UI promises a duration up front; it comes from the limiter's floor."""

    async def go() -> dict[str, Any]:
        await importer.start()
        snapshot = importer.snapshot()
        await importer.cancel()
        await importer.wait()
        return snapshot

    snapshot = asyncio.run(go())
    assert snapshot["eta_seconds"] == pytest.approx(snapshot["remaining"] * 2.0)


# ---------------------------------------------------------------------------
# HTTP surface
# ---------------------------------------------------------------------------
@pytest.fixture(name="client")
def client_fixture(
    session_maker: Any,
    settings: Settings,
    importer: LibraryImporter,
    monkeypatch: pytest.MonkeyPatch,
) -> Iterator[TestClient]:
    async def override_session() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    monkeypatch.setattr(routes_api, "get_library_importer", lambda: importer)
    monkeypatch.setattr("app.api.deps.get_library_importer", lambda: importer)
    monkeypatch.setattr(
        "app.api.deps.get_library_scanner", lambda: LibraryScanner(settings=settings)
    )
    monkeypatch.setattr("app.api.deps.get_settings", lambda: settings)

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)


def test_the_preview_endpoint_costs_no_api_calls(
    client: TestClient, client_stub: FakeClient
) -> None:
    body = client.get("/api/library/import/preview").json()
    assert body["count"] == len(CATALOGUE)
    assert body["eta_seconds"] == pytest.approx(len(CATALOGUE) * 2.0)
    assert client_stub.searches == []


def test_the_api_starts_an_import_and_returns_202(client: TestClient) -> None:
    response = client.post("/api/library/import", json={})
    assert response.status_code == 202
    assert response.json()["total"] == len(CATALOGUE)


def test_the_api_reports_progress_when_idle(client: TestClient) -> None:
    assert client.get("/api/library/import").json()["running"] is False


def test_a_second_start_maps_to_409(
    client: TestClient, importer: LibraryImporter, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The importer raises; the route must turn that into a conflict, not a 500."""

    async def busy(**_: Any) -> dict[str, Any]:
        raise RuntimeError("An artist import is already running.")

    monkeypatch.setattr(importer, "start", busy)
    response = client.post("/api/library/import", json={})
    assert response.status_code == 409
    # main.py renders HTTPException as {"ok", "error", "status_code"}.
    assert "already running" in response.json()["error"]


def test_cancel_maps_a_running_import_to_ok(
    client: TestClient, importer: LibraryImporter, monkeypatch: pytest.MonkeyPatch
) -> None:
    async def stopping() -> bool:
        return True

    monkeypatch.setattr(importer, "cancel", stopping)
    body = client.post("/api/library/import/cancel").json()
    assert body["ok"] is True
    assert "Stopping" in body["message"]


def test_cancelling_when_nothing_runs_is_not_an_error(client: TestClient) -> None:
    body = client.post("/api/library/import/cancel").json()
    assert body["ok"] is False
    assert "No import is running" in body["message"]


def test_the_page_offers_the_import_controls(client: TestClient) -> None:
    body = client.get("/library/scan").text
    assert 'hx-post="/ui/library/import"' in body
    assert "index each catalogue immediately" in body
    assert 'id="import-panel"' in body


def test_the_ui_start_returns_the_progress_panel(client: TestClient) -> None:
    response = client.post("/ui/library/import", headers={"HX-Request": "true"})
    assert response.status_code == 200
    trigger = json.loads(response.headers["HX-Trigger"])
    assert "Looking up" in trigger["fonoteca:toast"]["message"]


def test_the_progress_panel_polls_only_while_running(
    client: TestClient, importer: LibraryImporter, monkeypatch: pytest.MonkeyPatch
) -> None:
    """An idle page must not poll; a running one must, or progress freezes."""
    assert "hx-trigger" not in client.get("/partials/library-import").text

    running = dict(importer.snapshot())
    running.update({"running": True, "total": 40, "processed": 7, "current": "Somebody"})
    monkeypatch.setattr(importer, "snapshot", lambda: running)

    body = client.get("/partials/library-import").text
    assert 'hx-trigger="every 4s"' in body
    assert "Somebody" in body
    assert "Stop after this artist" in body


def test_the_review_list_renders_with_follow_buttons(
    client: TestClient, importer: LibraryImporter
) -> None:
    run_import(importer)
    body = client.get("/partials/library-import").text

    assert "Needs a decision" in body
    assert "Some Local Bands" in body
    assert 'hx-post="/ui/library/import/follow/5000"' in body


def test_following_from_the_review_list_works(
    client: TestClient, importer: LibraryImporter, session_maker: Any
) -> None:
    run_import(importer)
    response = client.post(
        "/ui/library/import/follow/5000",
        data={"name": "Some Local Bands"},
        headers={"HX-Request": "true"},
    )
    assert response.status_code == 200
    assert "5000" in followed(session_maker)
