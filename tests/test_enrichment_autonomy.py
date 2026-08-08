"""The autonomy split — "how much identification happened without me".

``enrichment_autonomy_counts`` answers a question the Identify screen's band
asks and that nothing else in the codebase could answer honestly: what share of
the library was identified with nobody involved. It is a **partition of
entities**, never a score, and this file pins the properties that make it one:

* the five buckets sum to ``entities``, and ``entities`` is the library —
  ``scope.albums + scope.artists``, never the catalogue;
* actionability is decided by :func:`app.core.enricher.review_picker_for` and
  nothing else, so delegation is honoured (an AcoustID ``ambiguous`` is a
  person's decision, recorded against MusicBrainz) and an unpressable refusal is
  *not* counted as waiting on a person — the same agreement the review list and
  the nav badge already have;
* ``rejected`` is a decision that was made, not one that is waiting;
* precedence is strongest-claim-first, so one ``ok`` rung does not make an
  entity "automatic" while another rung is asking somebody a question;
* an in-scope entity the seeder has not reached is ``unstarted``, never missing.

There is deliberately no test here comparing anything to a threshold. There is
no threshold: ``app.enrich.matching`` is exact or nothing, and a bar that
auto-accepted a match would be the tie-break
:func:`app.enrich.coverage.solve_album` refuses to contain.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from typing import Any, AsyncIterator, Iterator

import pytest
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.config import Settings
from app.core.enricher import (
    Enricher,
    enrichment_autonomy_counts,
    enrichment_scope_counts,
)
from app.enrich.errors import (
    STATE_AMBIGUOUS,
    STATE_GATED,
    STATE_NO_KEY,
    STATE_OK,
    STATE_PENDING,
    STATE_REJECTED,
)
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
)


@pytest.fixture(name="factory")
def factory_fixture() -> Iterator[Any]:
    """A scratch in-memory database plus a ``session_scope``-alike factory."""
    engine = create_async_engine(
        "sqlite+aiosqlite://",
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)

    asyncio.run(setup())

    @asynccontextmanager
    async def session_factory() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    try:
        yield session_factory
    finally:
        asyncio.run(engine.dispose())


async def seed_albums(
    factory: Any,
    albums: tuple[tuple[str, AlbumStatus], ...],
    *,
    artist_id: str = "a1",
) -> None:
    async with factory() as session:
        session.add(Artist(id=artist_id, name="Joe Bonamassa", albums_count=len(albums)))
        for album_id, status in albums:
            session.add(
                Album(
                    id=album_id,
                    artist_id=artist_id,
                    title=f"Album {album_id}",
                    status=status,
                )
            )


async def add_state(
    factory: Any,
    entity_type: EnrichmentEntity,
    entity_id: str,
    source: EnrichmentSource,
    state: str,
) -> None:
    async with factory() as session:
        session.add(
            EnrichmentState(
                entity_type=entity_type,
                entity_id=entity_id,
                source=source,
                state=state,
            )
        )


async def autonomy(factory: Any) -> dict[str, int]:
    async with factory() as session:
        return await enrichment_autonomy_counts(session)


def buckets(counts: dict[str, int]) -> int:
    return (
        counts["automatic"]
        + counts["waiting_person"]
        + counts["waiting_input"]
        + counts["dismissed"]
        + counts["unstarted"]
    )


def test_partition_sums_to_the_library(factory: Any) -> None:
    """The five buckets add up to ``entities``, and ``entities`` is the disk."""

    async def scenario() -> None:
        await seed_albums(
            factory,
            (
                ("alb-ok", AlbumStatus.DOWNLOADED),
                ("alb-review", AlbumStatus.DOWNLOADED),
                ("alb-gated", AlbumStatus.DOWNLOADED),
                ("alb-rejected", AlbumStatus.DOWNLOADED),
                ("alb-bare", AlbumStatus.DOWNLOADED),
                ("alb-catalogue", AlbumStatus.WANTED),
            ),
        )
        await add_state(
            factory, EnrichmentEntity.ALBUM, "alb-ok", EnrichmentSource.DEEZER, STATE_OK
        )
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-review",
            EnrichmentSource.MUSICBRAINZ,
            STATE_AMBIGUOUS,
        )
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-gated",
            EnrichmentSource.ACOUSTID,
            STATE_GATED,
        )
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-rejected",
            EnrichmentSource.MUSICBRAINZ,
            STATE_REJECTED,
        )
        await add_state(
            factory,
            EnrichmentEntity.ARTIST,
            "a1",
            EnrichmentSource.MUSICBRAINZ,
            STATE_OK,
        )

        counts = await autonomy(factory)
        async with factory() as session:
            scope = await enrichment_scope_counts(session)

        assert counts["entities"] == scope["albums"] + scope["artists"] == 6
        assert buckets(counts) == counts["entities"]
        assert counts["automatic"] == 2  # alb-ok and the artist
        assert counts["waiting_person"] == 1
        assert counts["waiting_input"] == 1
        assert counts["dismissed"] == 1
        assert counts["unstarted"] == 1  # alb-bare, seeded by nothing

    asyncio.run(scenario())


def test_delegation_is_honoured_both_ways(factory: Any) -> None:
    """AcoustID ``ambiguous`` is a person's; AcoustID ``no_key`` is not.

    Judging a row by its source alone is exactly the bug ``_DELEGATED_PICKERS``
    was written for — an ambiguity the audio could not settle is a MusicBrainz
    decision somebody can make, while "no files to fingerprint" is a wait no id
    anybody types will end.
    """

    async def scenario() -> None:
        await seed_albums(
            factory,
            (("alb-amb", AlbumStatus.DOWNLOADED), ("alb-nokey", AlbumStatus.DOWNLOADED)),
        )
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-amb",
            EnrichmentSource.ACOUSTID,
            STATE_AMBIGUOUS,
        )
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-nokey",
            EnrichmentSource.ACOUSTID,
            STATE_NO_KEY,
        )

        counts = await autonomy(factory)
        assert counts["waiting_person"] == 1
        assert counts["waiting_input"] == 1
        assert buckets(counts) == counts["entities"]

    asyncio.run(scenario())


def test_unpressable_refusal_is_not_waiting_on_a_person(factory: Any) -> None:
    """A Cover Art Archive ``no_key`` waits on an id another rung supplies."""

    async def scenario() -> None:
        await seed_albums(factory, (("alb-caa", AlbumStatus.DOWNLOADED),))
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-caa",
            EnrichmentSource.COVERARTARCHIVE,
            STATE_NO_KEY,
        )

        counts = await autonomy(factory)
        assert counts["waiting_input"] == 1
        assert counts["waiting_person"] == 0

    asyncio.run(scenario())


def test_rejected_is_dismissed_not_waiting(factory: Any) -> None:
    """A person has already decided; nothing is asking them again."""

    async def scenario() -> None:
        await seed_albums(factory, (("alb-x", AlbumStatus.DOWNLOADED),))
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-x",
            EnrichmentSource.MUSICBRAINZ,
            STATE_REJECTED,
        )

        counts = await autonomy(factory)
        assert counts["dismissed"] == 1
        assert counts["waiting_person"] == 0
        assert counts["unstarted"] == 1  # the artist, no rows of their own

    asyncio.run(scenario())


def test_precedence_a_question_outranks_a_success(factory: Any) -> None:
    """One ``ok`` rung does not make an entity automatic while another asks."""

    async def scenario() -> None:
        await seed_albums(factory, (("alb-both", AlbumStatus.DOWNLOADED),))
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-both",
            EnrichmentSource.DEEZER,
            STATE_OK,
        )
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-both",
            EnrichmentSource.MUSICBRAINZ,
            STATE_AMBIGUOUS,
        )

        counts = await autonomy(factory)
        assert counts["waiting_person"] == 1
        assert counts["automatic"] == 0

    asyncio.run(scenario())


def test_catalogue_only_albums_contribute_nothing(factory: Any) -> None:
    """A release that is not on disk is not in the denominator or any bucket."""

    async def scenario() -> None:
        await seed_albums(factory, (("alb-wanted", AlbumStatus.WANTED),))
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-wanted",
            EnrichmentSource.MUSICBRAINZ,
            STATE_AMBIGUOUS,
        )

        counts = await autonomy(factory)
        assert counts["entities"] == 0
        assert buckets(counts) == 0

    asyncio.run(scenario())


def test_an_entity_the_seeder_has_not_reached_is_unstarted(factory: Any) -> None:
    """No rows at all, and rows that have not been attempted, are both unstarted."""

    async def scenario() -> None:
        await seed_albums(
            factory,
            (
                ("alb-bare", AlbumStatus.DOWNLOADED),
                ("alb-pending", AlbumStatus.DOWNLOADED),
            ),
        )
        await add_state(
            factory,
            EnrichmentEntity.ALBUM,
            "alb-pending",
            EnrichmentSource.DEEZER,
            STATE_PENDING,
        )

        counts = await autonomy(factory)
        assert counts["entities"] == 3  # two albums and their artist
        assert counts["unstarted"] == 3
        assert buckets(counts) == counts["entities"]

    asyncio.run(scenario())


def test_status_carries_the_split_only_with_a_session(factory: Any) -> None:
    """``Enricher.status()`` publishes it; without a session the default applies."""

    async def scenario() -> None:
        await seed_albums(factory, (("alb-ok", AlbumStatus.DOWNLOADED),))
        await add_state(
            factory, EnrichmentEntity.ALBUM, "alb-ok", EnrichmentSource.DEEZER, STATE_OK
        )
        enricher = Enricher(
            [],
            settings=Settings(
                qobuz_app_id="app",
                qobuz_user_auth_token="token",
                enrichment_sources="deezer",
            ),
            session_factory=factory,
        )

        async with factory() as session:
            payload = await enricher.status(session)
        assert payload["autonomy"]["entities"] == 2
        assert payload["autonomy"]["automatic"] == 1

        bare = await enricher.status()
        assert "autonomy" not in bare

    asyncio.run(scenario())
