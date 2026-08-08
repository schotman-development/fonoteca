"""The barcode a release never had, and the rows that were waiting for it.

Two halves of one repair, and neither works alone.

``artist/getReleasesList`` — the endpoint every album row is built from — does
not publish a ``upc``. ``album/get`` does, and only the download loop calls it.
So on a library assembled by the disk scan the field is empty almost everywhere:
15 of 422 releases on disk had one, and those 15 were the only rows carrying a
barcode anywhere in a 16,549-album catalogue. ``app.core.catalogue`` is the
bounded pass that fills the gap.

Filling it changes nothing by itself. The rows that wanted a barcode are
``no_key``, which stores ``next_attempt_at = NULL`` and is never retried on a
timer — 480 of them on that library, reading "no barcode on this release and no
… artist to browse". :meth:`Enricher._rearm_barcoded` is what notices, and the
property these tests exist to pin is that it **converges**: it re-arms a row
only when :func:`barcode_candidates` would give the provider something to search
with, because a row re-armed into the same refusal is an infinite loop billed to
the upstream.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from typing import Any, AsyncIterator, Iterator

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.config import Settings
from app.core.catalogue import backfill_upc
from app.core.enricher import Enricher
from app.enrich.errors import NoMatchKey
from app.enrich.types import EnrichmentJob, EnrichmentResult
from app.models import (
    Album,
    AlbumMetadata,
    AlbumStatus,
    Artist,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
)


# ---------------------------------------------------------------------------
# Doubles
# ---------------------------------------------------------------------------
class RefusingProvider:
    """A rung that answers ``no_key`` until the snapshot carries a barcode.

    Modelled on the real Deezer and MusicBrainz providers, whose refusal is
    conditional in exactly this way: with candidates in hand neither can reach
    its ``NoMatchKey`` branch, which is the whole reason the re-arm terminates.
    """

    def __init__(self, source: EnrichmentSource = EnrichmentSource.DEEZER) -> None:
        self.source = source
        self.calls: list[str] = []
        self.seen_barcodes: list[str | None] = []

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        return entity_type is EnrichmentEntity.ALBUM

    def is_ready(self) -> bool:
        return True

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        self.calls.append(job.entity_id)
        album = job.album
        barcode = getattr(album, "upc", None) or getattr(album, "barcode", None)
        self.seen_barcodes.append(barcode)
        if not barcode:
            raise NoMatchKey(
                "no barcode on this release and no Deezer artist to browse",
                source=self.source.value,
            )
        return EnrichmentResult()

    async def aclose(self) -> None:
        return None


class FakeQobuz:
    """``album/get``, from a canned table. Records what it was asked."""

    def __init__(self, payloads: dict[str, Any]) -> None:
        self.payloads = payloads
        self.asked: list[str] = []

    async def get_album(self, album_id: str) -> Any:
        self.asked.append(album_id)
        answer = self.payloads.get(album_id, {})
        if isinstance(answer, BaseException):
            raise answer
        return answer


@pytest.fixture(name="factory")
def factory_fixture() -> Iterator[Any]:
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

    session_factory.maker = maker  # type: ignore[attr-defined]
    try:
        yield session_factory
    finally:
        asyncio.run(engine.dispose())


async def seed(
    factory: Any, *, albums: int = 2, status: AlbumStatus = AlbumStatus.DOWNLOADED
) -> None:
    async with factory() as session:
        session.add(Artist(id="a1", name="Mark Knopfler", albums_count=albums))
        for index in range(albums):
            session.add(
                Album(
                    id=f"al{index}",
                    artist_id="a1",
                    title=f"Album {index}",
                    status=status,
                )
            )


def make_settings(**overrides: Any) -> Settings:
    base: dict[str, Any] = {
        "qobuz_app_id": "x",
        "qobuz_user_auth_token": "y",
        "library_path": "/library",
        "enrichment_sources": "deezer",
        "enrichment_contact": "test@example.com",
    }
    base.update(overrides)
    return Settings(**base)


def build(factory: Any, providers: list[Any], **settings: Any) -> Enricher:
    return Enricher(
        providers, settings=make_settings(**settings), session_factory=factory
    )


async def states(factory: Any) -> dict[tuple[str, str], EnrichmentState]:
    async with factory() as session:
        rows = (await session.execute(select(EnrichmentState))).scalars().all()
        return {(row.entity_id, row.source.value): row for row in rows}


async def set_upc(factory: Any, album_id: str, value: str | None) -> None:
    async with factory() as session:
        album = await session.get(Album, album_id)
        album.upc = value


# ---------------------------------------------------------------------------
# The re-arm
# ---------------------------------------------------------------------------
def test_a_stranded_row_waits_forever_without_a_barcode(factory: Any) -> None:
    """The state this exists to fix. ``no_key`` retries on no timer at all."""
    asyncio.run(seed(factory, albums=1))
    enricher = build(factory, [RefusingProvider()])

    asyncio.run(enricher.tick())
    row = asyncio.run(states(factory))[("al0", "deezer")]
    assert row.state == "no_key"
    assert row.next_attempt_at is None

    # Ticking again changes nothing and costs no request: that is the bug.
    assert asyncio.run(enricher.tick()).rearmed == 0


def test_a_upc_arriving_reopens_the_row(factory: Any) -> None:
    asyncio.run(seed(factory, albums=1))
    provider = RefusingProvider()
    enricher = build(factory, [provider])
    asyncio.run(enricher.tick())
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "no_key"

    asyncio.run(set_upc(factory, "al0", "0602498672600"))

    assert asyncio.run(enricher.tick()).rearmed == 1
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "ok"
    # And the provider was handed the barcode, not merely woken up.
    assert provider.seen_barcodes[-1] == "0602498672600"


def test_a_verified_barcode_reopens_the_row_too(factory: Any) -> None:
    """The other field ``barcode_candidates`` reads: what a rung already learned.

    This is the case the ladder's own ordering creates — MusicBrainz verifying a
    barcode after Deezer has already refused for want of one.
    """
    asyncio.run(seed(factory, albums=1))
    enricher = build(factory, [RefusingProvider()])
    asyncio.run(enricher.tick())

    async def learn_it() -> None:
        async with factory() as session:
            session.add(AlbumMetadata(album_id="al0", barcode="0602498672600"))

    asyncio.run(learn_it())

    assert asyncio.run(enricher.tick()).rearmed == 1


def test_the_reason_is_cleared_so_the_review_list_stops_asking(factory: Any) -> None:
    """A row waiting on a tick must not sit on the list claiming to need a person."""
    asyncio.run(seed(factory, albums=1))
    enricher = build(factory, [RefusingProvider()])
    asyncio.run(enricher.tick())
    assert asyncio.run(states(factory))[("al0", "deezer")].last_error

    asyncio.run(set_upc(factory, "al0", "0602498672600"))
    asyncio.run(enricher.tick())

    assert asyncio.run(states(factory))[("al0", "deezer")].last_error is None


def test_an_unusable_barcode_does_not_re_arm(factory: Any) -> None:
    """The convergence guarantee, and the reason the test is Python not SQL.

    ``upc IS NOT NULL`` is not the same claim as *there is something to search
    with*. A tag editor writes ``"N/A"``; ``normalize_barcode`` rejects it, the
    provider would refuse identically, and a re-arm keyed on the column being
    filled would wake this row on every tick for the rest of the library's life.
    """
    asyncio.run(seed(factory, albums=1))
    provider = RefusingProvider()
    enricher = build(factory, [provider])
    asyncio.run(enricher.tick())
    before = len(provider.calls)

    asyncio.run(set_upc(factory, "al0", "N/A"))

    assert asyncio.run(enricher.tick()).rearmed == 0
    assert asyncio.run(enricher.tick()).rearmed == 0
    assert len(provider.calls) == before


def test_re_arming_happens_once_and_the_row_settles(factory: Any) -> None:
    """A re-armed row must not come back to ``no_key`` and go round again."""
    asyncio.run(seed(factory, albums=1))
    provider = RefusingProvider()
    enricher = build(factory, [provider])
    asyncio.run(enricher.tick())

    asyncio.run(set_upc(factory, "al0", "0602498672600"))
    assert asyncio.run(enricher.tick()).rearmed == 1

    # Whatever the outcome, it is not ``no_key`` again — so nothing re-arms.
    for _ in range(3):
        assert asyncio.run(enricher.tick()).rearmed == 0
    assert len(provider.calls) == 2


def test_a_release_not_on_disk_is_not_re_armed(factory: Any) -> None:
    """Held to ``library_scope`` like every other pass. Out of scope is not stranded."""
    asyncio.run(seed(factory, albums=1))
    enricher = build(factory, [RefusingProvider()])
    asyncio.run(enricher.tick())

    async def leave_the_library() -> None:
        async with factory() as session:
            album = await session.get(Album, "al0")
            album.status = AlbumStatus.WANTED
            album.upc = "0602498672600"

    asyncio.run(leave_the_library())

    assert asyncio.run(enricher.tick()).rearmed == 0


def test_only_barcode_consuming_sources_take_part(factory: Any) -> None:
    """AcoustID's ``no_key`` means "no file to fingerprint". A barcode is no answer."""
    asyncio.run(seed(factory, albums=1))
    provider = RefusingProvider(EnrichmentSource.ACOUSTID)
    enricher = build(factory, [provider], enrichment_sources="acoustid")
    asyncio.run(enricher.tick())
    assert asyncio.run(states(factory))[("al0", "acoustid")].state == "no_key"

    asyncio.run(set_upc(factory, "al0", "0602498672600"))

    assert asyncio.run(enricher.tick()).rearmed == 0


def test_a_rejected_row_is_not_re_armed_by_a_barcode(factory: Any) -> None:
    """A person took this off the list. An arriving input does not put it back."""
    asyncio.run(seed(factory, albums=1))
    enricher = build(factory, [RefusingProvider()])
    asyncio.run(enricher.tick())

    async def reject() -> None:
        async with factory() as session:
            await enricher.reject(
                session, EnrichmentEntity.ALBUM, "al0", EnrichmentSource.DEEZER
            )

    asyncio.run(reject())
    asyncio.run(set_upc(factory, "al0", "0602498672600"))

    assert asyncio.run(enricher.tick()).rearmed == 0
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "rejected"


# ---------------------------------------------------------------------------
# The backfill
# ---------------------------------------------------------------------------
def test_it_fills_only_releases_on_disk_that_have_none(factory: Any) -> None:
    asyncio.run(seed(factory, albums=3))

    async def arrange() -> None:
        async with factory() as session:
            (await session.get(Album, "al1")).upc = "111111111111"
            (await session.get(Album, "al2")).status = AlbumStatus.WANTED

    asyncio.run(arrange())
    qobuz = FakeQobuz(
        {
            "al0": {"upc": "0602498672600"},
            "al1": {"upc": "999999999999"},
            "al2": {"upc": "888888888888"},
        }
    )

    async def run() -> Any:
        async with factory() as session:
            return await backfill_upc(session, qobuz)

    result = asyncio.run(run())

    # al1 already has one, al2 is not on disk: neither is even asked about.
    assert qobuz.asked == ["al0"]
    assert result.candidates == 1
    assert result.filled == 1
    assert result.usable == 1

    async def read() -> dict[str, str | None]:
        async with factory() as session:
            rows = (await session.execute(select(Album))).scalars().all()
            return {row.id: row.upc for row in rows}

    stored = asyncio.run(read())
    assert stored["al0"] == "0602498672600"
    assert stored["al1"] == "111111111111"  # untouched
    assert stored["al2"] is None


def test_a_dry_run_asks_and_writes_nothing(factory: Any) -> None:
    asyncio.run(seed(factory, albums=1))
    qobuz = FakeQobuz({"al0": {"upc": "0602498672600"}})

    async def run() -> Any:
        async with factory() as session:
            return await backfill_upc(session, qobuz, dry_run=True)

    result = asyncio.run(run())
    assert result.filled == 1
    assert qobuz.asked == ["al0"]

    async def read() -> str | None:
        async with factory() as session:
            return (await session.get(Album, "al0")).upc

    assert asyncio.run(read()) is None


def test_a_release_qobuz_publishes_no_barcode_for_is_counted_not_guessed(
    factory: Any,
) -> None:
    """And emphatically not filled from the album **id**, however barcode-shaped.

    ``0060249867260`` is a Qobuz id that passes a GS1 check digit and is not the
    barcode of *Shangri-La*. Writing one into ``upc`` would reintroduce the
    exact failure ``barcode_candidates`` was rewritten to remove, one layer
    lower, where every consumer takes it for Qobuz's own answer.
    """
    asyncio.run(seed(factory, albums=1))
    qobuz = FakeQobuz({"al0": {"id": "0060249867260", "title": "Shangri-La"}})

    async def run() -> Any:
        async with factory() as session:
            return await backfill_upc(session, qobuz)

    result = asyncio.run(run())
    assert (result.filled, result.absent) == (0, 1)

    async def read() -> str | None:
        async with factory() as session:
            return (await session.get(Album, "al0")).upc

    assert asyncio.run(read()) is None


def test_one_failure_does_not_stop_the_pass(factory: Any) -> None:
    asyncio.run(seed(factory, albums=3))
    qobuz = FakeQobuz(
        {
            "al0": {"upc": "0602498672600"},
            "al1": RuntimeError("Qobuz said no"),
            "al2": {"upc": "0884385226442"},
        }
    )

    async def run() -> Any:
        async with factory() as session:
            return await backfill_upc(session, qobuz)

    result = asyncio.run(run())
    assert result.examined == 3
    assert result.filled == 2
    assert result.failed == 1
    assert "Qobuz said no" in result.errors[0]
    assert result.remaining == 1


def test_filled_and_usable_are_reported_apart(factory: Any) -> None:
    """A value can be stored faithfully and still not be a barcode."""
    asyncio.run(seed(factory, albums=2))
    qobuz = FakeQobuz({"al0": {"upc": "0602498672600"}, "al1": {"upc": "N/A"}})

    async def run() -> Any:
        async with factory() as session:
            return await backfill_upc(session, qobuz)

    result = asyncio.run(run())
    assert result.filled == 2
    assert result.usable == 1


def test_the_limit_is_a_cap_on_requests(factory: Any) -> None:
    """Bounded per invocation is the whole reason this is a command and not a job."""
    asyncio.run(seed(factory, albums=4))
    qobuz = FakeQobuz({f"al{i}": {"upc": f"06024986726{i}0"} for i in range(4)})

    async def run() -> Any:
        async with factory() as session:
            return await backfill_upc(session, qobuz, limit=2)

    result = asyncio.run(run())
    assert len(qobuz.asked) == 2
    assert result.candidates == 4
    assert result.examined == 2
    assert result.remaining == 2


# ---------------------------------------------------------------------------
# The two halves together
# ---------------------------------------------------------------------------
def test_the_backfill_unblocks_a_stranded_row_on_the_next_tick(factory: Any) -> None:
    """The end-to-end claim, and why neither half ships alone.

    The backfill seeds no work of its own — deliberately, because a state-based
    repair also fixes rows stranded before it existed, which an event cannot.
    """
    asyncio.run(seed(factory, albums=1))
    provider = RefusingProvider()
    enricher = build(factory, [provider])

    asyncio.run(enricher.tick())
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "no_key"

    qobuz = FakeQobuz({"al0": {"upc": "0602498672600"}})

    async def run() -> Any:
        async with factory() as session:
            return await backfill_upc(session, qobuz)

    assert asyncio.run(run()).usable == 1

    # Nothing was queued by the backfill itself...
    row = asyncio.run(states(factory))[("al0", "deezer")]
    assert (row.state, row.next_attempt_at) == ("no_key", None)

    # ...the enricher's own pass is what notices.
    assert asyncio.run(enricher.tick()).rearmed == 1
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "ok"
