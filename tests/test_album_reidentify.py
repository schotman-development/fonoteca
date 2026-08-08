"""``POST /api/albums/{id}/reidentify`` — the release drawer's Re-identify button.

The design calls it "Re-fingerprint"; nothing stores a fingerprint per release
and ``fpcalc`` lives inside the AcoustID rung, so what the press buys is a fresh
identification. The properties worth pinning are all about *which* helper does
that and *when*:

* it goes through :meth:`Enricher.reopen`, not ``mark_library_due`` — ``mark_due``
  moves ``pending``/``not_found``/``failed`` only, so an already-identified
  release (every row ``ok``, exactly the one somebody wants re-checked) would
  come back a number with nothing behind it;
* every album-supporting rung is re-opened, not AcoustID alone, because a fresh
  fingerprint verdict can change which release the directory *is*;
* scope is tested **before** anything is written, so a release that is not on
  disk leaves no rows for the nightly purge;
* the session is committed **before** the cascade, because the cascade opens its
  own sessions and makes network calls — holding SQLite's single writer across
  one is the stall that makes ``QueueWorker`` mark a live download failed.

The ``get_session`` override deliberately does not commit. A fixture that
committed for the routes would hide every route that forgot to.
"""

from __future__ import annotations

import asyncio
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.api import routes_api
from app.core.enricher import CascadeResult
from app.db import get_session
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
)

ARTIST_ID = "312829"
OWNED = "dust"
WANTED = "std"


class StubEnricher:
    """Records the two calls the route makes, in the order it makes them."""

    def __init__(self, *, sources: int = 3, outcome: CascadeResult | None = None) -> None:
        self.sources = sources
        self.outcome = outcome if outcome is not None else CascadeResult(rounds=1, ran=2)
        self.reopen_calls: list[tuple[EnrichmentEntity, str, Any]] = []
        self.cascade_calls: list[tuple[EnrichmentEntity, str]] = []
        self.order: list[str] = []
        self.on_cascade: Any = None

    async def reopen(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_id: str,
        *,
        sources: Any = None,
        priority: int = 20,
    ) -> int:
        self.order.append("reopen")
        self.reopen_calls.append((entity_type, entity_id, sources))
        # What the real ``_reopen`` does, in miniature: set ``pending``
        # unconditionally, inserting the row when there is none. Doing it as a
        # blind insert would collide with the seeded ``ok`` rows, which is the
        # very thing this test is about.
        for source in list(EnrichmentSource)[: self.sources]:
            existing = (
                await session.execute(
                    select(EnrichmentState).where(
                        EnrichmentState.entity_type == entity_type,
                        EnrichmentState.entity_id == entity_id,
                        EnrichmentState.source == source,
                    )
                )
            ).scalars().first()
            if existing is None:
                session.add(
                    EnrichmentState(
                        entity_type=entity_type,
                        entity_id=entity_id,
                        source=source,
                        state="pending",
                        priority=priority,
                    )
                )
            else:
                existing.state = "pending"
                existing.priority = priority
        await session.flush()
        return self.sources

    async def cascade(
        self, entity_type: EnrichmentEntity, entity_id: str, **_: Any
    ) -> CascadeResult:
        self.order.append("cascade")
        self.cascade_calls.append((entity_type, entity_id))
        if self.on_cascade is not None:
            await self.on_cascade()
        return self.outcome


@pytest.fixture(name="maker")
def maker_fixture(tmp_path: Any) -> Iterator[Any]:
    """The session factory the routes and the assertions both use."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'reid.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Joe Bonamassa"))
            session.add(
                Album(
                    id=OWNED,
                    artist_id=ARTIST_ID,
                    title="Dust Bowl",
                    status=AlbumStatus.DOWNLOADED,
                    path="/music/Joe Bonamassa/Dust Bowl",
                )
            )
            session.add(
                Album(
                    id=WANTED,
                    artist_id=ARTIST_ID,
                    title="Blues Of Desperation",
                    status=AlbumStatus.WANTED,
                )
            )
            # Already identified: every row ``ok``. ``mark_library_due`` would
            # move none of these, which is the bug this route must not have.
            for source in (EnrichmentSource.MUSICBRAINZ, EnrichmentSource.DEEZER):
                session.add(
                    EnrichmentState(
                        entity_type=EnrichmentEntity.ALBUM,
                        entity_id=OWNED,
                        source=source,
                        state="ok",
                        last_error=None,
                    )
                )
            await session.commit()

    asyncio.run(seed())
    try:
        yield maker
    finally:
        asyncio.run(engine.dispose())


@pytest.fixture(name="client")
def client_fixture(maker: Any) -> Iterator[TestClient]:
    async def override_session() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)


@pytest.fixture(name="enricher")
def enricher_fixture(monkeypatch: pytest.MonkeyPatch) -> StubEnricher:
    stub = StubEnricher()
    monkeypatch.setattr(routes_api, "get_enricher", lambda: stub)
    monkeypatch.setattr("app.api.deps.get_enricher", lambda: stub)
    return stub


def rows(maker: Any, album_id: str) -> list[EnrichmentState]:
    async def read() -> list[EnrichmentState]:
        async with maker() as session:
            found = await session.execute(
                select(EnrichmentState).where(
                    EnrichmentState.entity_type == EnrichmentEntity.ALBUM,
                    EnrichmentState.entity_id == album_id,
                )
            )
            return list(found.scalars())

    return asyncio.run(read())


# ---------------------------------------------------------------------------
# The two calls, and their order
# ---------------------------------------------------------------------------
def test_it_reopens_every_album_rung_then_cascades(
    client: TestClient, enricher: StubEnricher
) -> None:
    """No ``sources=``: every rung that handles an album, not AcoustID alone."""
    response = client.post(f"/api/albums/{OWNED}/reidentify")

    assert response.status_code == 200
    assert enricher.reopen_calls == [(EnrichmentEntity.ALBUM, OWNED, None)]
    assert enricher.cascade_calls == [(EnrichmentEntity.ALBUM, OWNED)]
    assert enricher.order == ["reopen", "cascade"]


def test_the_write_is_committed_before_the_cascade_runs(
    client: TestClient, maker: Any, enricher: StubEnricher
) -> None:
    """A fresh session sees the rows while the cascade is still going.

    That proves both halves: the request-scoped session (which never commits on
    its own) was committed, and no write transaction is held across the network
    calls the cascade makes.
    """
    seen: list[int] = []

    async def peek() -> None:
        async with maker() as other:
            found = await other.execute(
                select(EnrichmentState).where(EnrichmentState.entity_id == OWNED)
            )
            seen.append(len(list(found.scalars())))

    enricher.on_cascade = peek
    client.post(f"/api/albums/{OWNED}/reidentify")

    assert seen == [enricher.sources]


def test_an_identified_release_comes_back_pending(
    client: TestClient, maker: Any, enricher: StubEnricher
) -> None:
    """The regression on the wrong helper. Every row started ``ok``; a
    ``mark_library_due`` implementation would have moved none of them."""
    client.post(f"/api/albums/{OWNED}/reidentify")

    states = {row.state for row in rows(maker, OWNED)}
    assert states == {"pending"}


# ---------------------------------------------------------------------------
# Refusals
# ---------------------------------------------------------------------------
def test_a_release_not_on_disk_is_refused_and_writes_nothing(
    client: TestClient, maker: Any, enricher: StubEnricher
) -> None:
    response = client.post(f"/api/albums/{WANTED}/reidentify")

    assert response.status_code == 400
    assert "wanted" in response.json()["error"]
    assert enricher.reopen_calls == []
    assert rows(maker, WANTED) == []


def test_an_unknown_album_is_404(client: TestClient, enricher: StubEnricher) -> None:
    assert client.post("/api/albums/nope/reidentify").status_code == 404
    assert enricher.reopen_calls == []


def test_no_enricher_is_503(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(routes_api, "get_enricher", lambda: None)
    monkeypatch.setattr("app.api.deps.get_enricher", lambda: None)

    assert client.post(f"/api/albums/{OWNED}/reidentify").status_code == 503


def test_no_album_supporting_provider_is_refused(
    client: TestClient, maker: Any, enricher: StubEnricher
) -> None:
    """``reopen`` returning 0 means the ladder holds nothing for a release."""
    enricher.sources = 0
    response = client.post(f"/api/albums/{OWNED}/reidentify")

    assert response.status_code == 400
    assert "ENRICHMENT_SOURCES" in response.json()["error"]
    assert enricher.cascade_calls == []
    # Nothing was committed: the two rows it started with are untouched.
    assert {row.state for row in rows(maker, OWNED)} == {"ok"}


# ---------------------------------------------------------------------------
# What the person reads
# ---------------------------------------------------------------------------
def test_a_tick_holding_the_lock_is_info_not_a_failure(
    client: TestClient, enricher: StubEnricher
) -> None:
    enricher.outcome = CascadeResult(skipped=True)
    payload = client.post(f"/api/albums/{OWNED}/reidentify").json()

    assert payload["ok"] is True
    assert payload["level"] == "info"
    assert "already running" in payload["message"]
    assert payload["detail"]["skipped"] is True


def test_nothing_ran_says_next_pass(client: TestClient, enricher: StubEnricher) -> None:
    enricher.outcome = CascadeResult()
    payload = client.post(f"/api/albums/{OWNED}/reidentify").json()

    assert payload["level"] == "info"
    assert "next pass" in payload["message"]


def test_a_run_reports_both_counts_and_the_tail(
    client: TestClient, enricher: StubEnricher
) -> None:
    enricher.outcome = CascadeResult(rounds=2, ran=3, written_back=2, exhausted=True)
    payload = client.post(f"/api/albums/{OWNED}/reidentify").json()

    assert payload["level"] == "success"
    assert "ran 3 source(s)" in payload["message"]
    assert "re-tagged 2 file(s)" in payload["message"]
    assert "the rest follows on the next pass" in payload["message"]
    assert payload["detail"] == {
        "album_id": OWNED,
        "sources": enricher.sources,
        "ran": 3,
        "written_back": 2,
        "exhausted": True,
        "skipped": False,
    }
