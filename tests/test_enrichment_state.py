"""The enrichment orchestrator's state machine.

No network and no real providers: every rung here is a hand-written fake with a
``calls`` list, which is what lets the assertions be about *how many lookups
happened* and *what the state became* rather than about plumbing.

The rules being pinned down:

* seeding is one statement and picks up anything new, however it arrived;
* due rows come out in priority order, and a claim is committed before any
  request goes out, so a crash costs one attempt rather than replaying forever;
* every terminal answer gets the right retry ladder — ``no_key`` and ``gated``
  are never retried on a timer at all;
* an absent field leaves the stored value alone while an explicit ``None``
  clears it, so a sparse answer cannot wipe a richer one;
* **no database session is open while a provider is running**. That one is not
  hygiene: SQLite has a single writer, and a transaction held across a two-second
  lookup would stall the download worker into marking a live download failed.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from datetime import datetime, timedelta, timezone
from typing import Any, AsyncIterator, Iterator

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.config import Settings
from app.core import enricher as enricher_module
from app.core.enricher import (
    Enricher,
    list_review_items,
    load_album_metadata,
    prune_enrichment_orphans,
)
from app.enrich.errors import (
    AmbiguousMatch,
    MatchRejected,
    NoMatchKey,
    NotMatched,
    SourceGated,
)
from app.enrich.types import EnrichmentJob, EnrichmentResult
from app.models import (
    Album,
    AlbumMetadata,
    AlbumStatus,
    Artist,
    ArtistMetadata,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
    FileClaim,
    FingerprintState,
    Track,
    TrackMetadata,
    TrackStatus,
)
from app.net.errors import HttpTransportError

NOW = datetime(2026, 7, 31, 12, 0, tzinfo=timezone.utc)


# ---------------------------------------------------------------------------
# Doubles
# ---------------------------------------------------------------------------
class FakeProvider:
    """One rung of the ladder, answering from a canned script.

    ``calls`` records every job it was handed, in order, which is how the tests
    assert that a gated source was skipped or a budget cut a batch short.
    """

    def __init__(
        self,
        source: EnrichmentSource = EnrichmentSource.DEEZER,
        *,
        answers: dict[str, Any] | None = None,
        default: Any = None,
        entities: tuple[EnrichmentEntity, ...] = (
            EnrichmentEntity.ARTIST,
            EnrichmentEntity.ALBUM,
        ),
        ready: bool = True,
    ) -> None:
        self.source = source
        self.answers = answers or {}
        self.default = default if default is not None else EnrichmentResult()
        self.entities = entities
        self.ready = ready
        self.calls: list[str] = []
        #: The whole job, not just its id — what the snapshot actually carried is
        #: the only way to assert that a file's claim reached a provider.
        self.jobs: list[EnrichmentJob] = []
        self.sessions_open_during_fetch: list[int] = []
        self.closed = False

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        return entity_type in self.entities

    def is_ready(self) -> bool:
        return self.ready

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        self.calls.append(job.entity_id)
        self.jobs.append(job)
        self.sessions_open_during_fetch.append(_OPEN_SESSIONS[0])
        answer = self.answers.get(job.entity_id, self.default)
        if isinstance(answer, BaseException):
            raise answer
        return answer

    async def aclose(self) -> None:
        self.closed = True


#: How many sessions the factory below currently has open. A provider samples it
#: mid-fetch; anything above zero means the enricher is holding a transaction
#: across the network, which is the bug this whole design exists to prevent.
_OPEN_SESSIONS = [0]


@pytest.fixture(name="factory")
def factory_fixture() -> Iterator[Any]:
    """A scratch in-memory database plus a session factory that counts itself."""
    engine = create_async_engine(
        "sqlite+aiosqlite://", connect_args={"check_same_thread": False}, poolclass=StaticPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)

    asyncio.run(setup())
    _OPEN_SESSIONS[0] = 0

    @asynccontextmanager
    async def session_factory() -> AsyncIterator[AsyncSession]:
        session = maker()
        _OPEN_SESSIONS[0] += 1
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        finally:
            _OPEN_SESSIONS[0] -= 1
            await session.close()

    session_factory.maker = maker  # type: ignore[attr-defined]
    try:
        yield session_factory
    finally:
        asyncio.run(engine.dispose())


def make_settings(**overrides: Any) -> Settings:
    base: dict[str, Any] = {
        "qobuz_app_id": "app",
        "qobuz_user_auth_token": "token",
        "enrichment_sources": "deezer,musicbrainz",
        "enrichment_batch_size": 50,
        "enrichment_refresh_days": 90,
    }
    base.update(overrides)
    return Settings(**base)


async def seed_library(factory: Any, *, albums: int = 2, tracks: int = 0) -> None:
    """One artist with *albums* releases on disk, optionally with track rows.

    ``downloaded`` rather than ``wanted``: enrichment is scoped to the library,
    so an album nobody owns is never seeded, claimed or re-armed. A fixture that
    left them wanted would be testing an empty queue.
    """
    async with factory() as session:
        session.add(Artist(id="a1", name="Joe Bonamassa", albums_count=albums))
        for index in range(albums):
            session.add(
                Album(
                    id=f"al{index}",
                    artist_id="a1",
                    title=f"Album {index}",
                    tracks_count=tracks,
                    status=AlbumStatus.DOWNLOADED,
                )
            )
            for number in range(tracks):
                session.add(
                    Track(
                        id=f"t{index}-{number}",
                        album_id=f"al{index}",
                        title=f"Track {number}",
                        track_number=number + 1,
                        media_number=1,
                        isrc=f"ISRC{index}{number}",
                        status=TrackStatus.DOWNLOADED,
                    )
                )


def build(factory: Any, providers: list[Any], **settings: Any) -> Enricher:
    return Enricher(
        providers, settings=make_settings(**settings), session_factory=factory
    )


async def states(factory: Any) -> dict[tuple[str, str], EnrichmentState]:
    async with factory() as session:
        rows = (await session.execute(select(EnrichmentState))).scalars().all()
        return {(row.entity_id, row.source.value): row for row in rows}


# ---------------------------------------------------------------------------
# Seeding
# ---------------------------------------------------------------------------
def test_seeding_covers_every_entity_and_enabled_source(factory: Any) -> None:
    asyncio.run(seed_library(factory))
    deezer = FakeProvider(EnrichmentSource.DEEZER)
    mb = FakeProvider(EnrichmentSource.MUSICBRAINZ)

    asyncio.run(build(factory, [deezer, mb]).tick())

    rows = asyncio.run(states(factory))
    # 1 artist + 2 albums, times 2 sources.
    assert len(rows) == 6
    assert {key[1] for key in rows} == {"deezer", "musicbrainz"}


def test_a_source_that_only_handles_albums_is_not_seeded_for_artists(factory: Any) -> None:
    asyncio.run(seed_library(factory))
    albums_only = FakeProvider(
        EnrichmentSource.DEEZER, entities=(EnrichmentEntity.ALBUM,)
    )

    asyncio.run(build(factory, [albums_only], enrichment_sources="deezer").tick())

    rows = asyncio.run(states(factory))
    assert set(rows) == {("al0", "deezer"), ("al1", "deezer")}


def test_a_source_left_out_of_the_settings_never_runs(factory: Any) -> None:
    asyncio.run(seed_library(factory))
    deezer = FakeProvider(EnrichmentSource.DEEZER)
    mb = FakeProvider(EnrichmentSource.MUSICBRAINZ)

    asyncio.run(build(factory, [deezer, mb], enrichment_sources="deezer").tick())

    assert deezer.calls
    assert mb.calls == []
    assert {key[1] for key in asyncio.run(states(factory))} == {"deezer"}


def test_seeding_is_idempotent(factory: Any) -> None:
    asyncio.run(seed_library(factory))
    provider = FakeProvider(EnrichmentSource.DEEZER)
    enricher = build(factory, [provider], enrichment_sources="deezer")

    first = asyncio.run(enricher.tick())
    second = asyncio.run(enricher.tick())

    assert first.seeded == 3
    assert second.seeded == 0


# ---------------------------------------------------------------------------
# The rule this design exists for
# ---------------------------------------------------------------------------
def test_no_session_is_open_while_a_provider_runs(factory: Any) -> None:
    """A held write transaction would stall the download worker into failing."""
    asyncio.run(seed_library(factory, albums=3))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    assert provider.calls, "the provider never ran, so this proves nothing"
    assert provider.sessions_open_during_fetch == [0] * len(provider.calls)


# ---------------------------------------------------------------------------
# What the snapshot carries: claims are hypotheses and travel as such
# ---------------------------------------------------------------------------
async def _claim_barcodes(factory: Any, values: dict[str, str]) -> None:
    """Give ``al0``'s tracks the barcodes in *values*, keyed by track id."""
    async with factory() as session:
        for track_id, barcode in values.items():
            session.add(FileClaim(track_id=track_id, barcode=barcode))


def test_a_barcode_the_files_claim_reaches_the_provider(factory: Any) -> None:
    """The key that came back after the Qobuz-album-id fallback was removed.

    It arrives in ``claimed_barcode`` and **not** in ``barcode``: one field is a
    hypothesis read off a stranger's tags, the other is what a rung verified, and
    a provider has to be able to tell them apart to know how much to trust it.
    """
    asyncio.run(seed_library(factory, albums=1, tracks=2))
    asyncio.run(_claim_barcodes(factory, {"t0-0": "602498672600", "t0-1": "602498672600"}))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    album = next(
        job.album for job in provider.jobs if job.entity_type is EnrichmentEntity.ALBUM
    )
    assert album.claimed_barcode == "602498672600"
    assert album.barcode is None, "a claim must never land in the verified field"


def test_files_claiming_different_barcodes_supply_no_key(factory: Any) -> None:
    """A directory holding two barcodes is not one release, so it offers nothing.

    Picking a winner here would write a barcode into files it was never true of.
    """
    asyncio.run(seed_library(factory, albums=1, tracks=2))
    asyncio.run(_claim_barcodes(factory, {"t0-0": "602498672600", "t0-1": "0804879535645"}))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    album = next(
        job.album for job in provider.jobs if job.entity_type is EnrichmentEntity.ALBUM
    )
    assert album.claimed_barcode is None


def test_an_album_whose_files_claim_nothing_carries_no_claim(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1, tracks=2))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    album = next(
        job.album for job in provider.jobs if job.entity_type is EnrichmentEntity.ALBUM
    )
    assert album.claimed_barcode is None


# ---------------------------------------------------------------------------
# Claiming
# ---------------------------------------------------------------------------
def test_claim_bumps_attempts_and_respects_the_batch_size(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=5))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    result = asyncio.run(
        build(factory, [provider], enrichment_sources="deezer", enrichment_batch_size=2).tick()
    )

    assert result.claimed == 2
    assert len(provider.calls) == 2
    rows = asyncio.run(states(factory))
    touched = [row for row in rows.values() if row.attempts == 1]
    assert len(touched) == 2


def test_artists_are_claimed_before_their_albums(factory: Any) -> None:
    """Both album browse paths need the artist's id first.

    Without this an artist's own row queues behind its two hundred albums, every
    one of which then fails for want of the thing that row would have supplied.
    """
    asyncio.run(seed_library(factory, albums=5))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(
        build(factory, [provider], enrichment_sources="deezer", enrichment_batch_size=1).tick()
    )

    assert provider.calls == ["a1"]


def test_higher_priority_is_claimed_first(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=3))
    provider = FakeProvider(EnrichmentSource.DEEZER)
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher._seed())

    async def prioritise() -> None:
        async with factory() as session:
            row = await session.get(
                EnrichmentState,
                (EnrichmentEntity.ALBUM, "al2", EnrichmentSource.DEEZER),
            )
            row.priority = 10

    asyncio.run(prioritise())
    asyncio.run(enricher.tick(limit=1))

    assert provider.calls == ["al2"]


# ---------------------------------------------------------------------------
# Retry ladders
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    ("outcome", "state"),
    [
        (NoMatchKey("no barcode"), "no_key"),
        (SourceGated("no contact configured"), "gated"),
    ],
)
def test_terminal_outcomes_are_never_retried_on_a_timer(
    factory: Any, outcome: Exception, state: str
) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(EnrichmentSource.DEEZER, default=outcome)

    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    rows = asyncio.run(states(factory))
    row = rows[("al0", "deezer")]
    assert row.state == state
    assert row.next_attempt_at is None


def test_not_found_walks_the_slow_ladder(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(EnrichmentSource.DEEZER, default=NotMatched("nothing matched"))
    enricher = build(factory, [provider], enrichment_sources="deezer")

    seen: list[timedelta] = []
    for _ in range(4):
        asyncio.run(_force_due(factory))
        asyncio.run(enricher.tick())
        row = asyncio.run(states(factory))[("al0", "deezer")]
        seen.append(_delay(row))

    assert [d.days for d in seen] == [7, 30, 90, 90]


def test_ambiguous_waits_a_month_and_keeps_its_candidates_out_of_the_data(
    factory: Any,
) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        default=AmbiguousMatch("two releases share this barcode", candidates=["x", "y"]),
    )

    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    row = asyncio.run(states(factory))[("al0", "deezer")]
    assert row.state == "ambiguous"
    assert _delay(row).days == 30
    # Nothing ambiguous is ever written to the metadata table.
    async def check() -> dict[str, AlbumMetadata]:
        async with factory() as session:
            return await load_album_metadata(session, ["al0"])

    assert asyncio.run(check()) == {}


def test_transport_failures_back_off_fast_and_cap_at_a_day(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER, default=HttpTransportError("no route", source="deezer")
    )
    # The self-pause has its own test; keep it out of the way of the ladder.
    enricher = build(
        factory, [provider], enrichment_sources="deezer", enrichment_failure_cutoff=999
    )

    seen: list[float] = []
    for _ in range(10):
        asyncio.run(_force_due(factory))
        asyncio.run(enricher.tick())
        row = asyncio.run(states(factory))[("al0", "deezer")]
        seen.append(_delay(row).total_seconds())

    assert seen[0] == pytest.approx(300, abs=5)
    assert seen[1] == pytest.approx(600, abs=5)
    assert seen[-1] == pytest.approx(86_400, abs=5)
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "failed"


def test_a_match_is_refreshed_after_the_configured_window(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        default=EnrichmentResult(match_key="0804879535645", album_fields={"title": "x"}),
    )

    asyncio.run(
        build(factory, [provider], enrichment_sources="deezer", enrichment_refresh_days=30).tick()
    )

    row = asyncio.run(states(factory))[("al0", "deezer")]
    assert row.state == "ok"
    assert row.match_key == "0804879535645"
    assert _delay(row).days == 30


# ---------------------------------------------------------------------------
# Writing results
# ---------------------------------------------------------------------------
def test_fields_land_in_the_side_tables_and_never_on_the_album(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1, tracks=2))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        answers={
            "al0": EnrichmentResult(
                album_fields={"barcode": "0804879535645", "label": "J&R Adventures"},
                artist_fields={"deezer_artist_id": "1424"},
                track_fields={"t0-0": {"deezer_track_id": "99"}},
            )
        },
        default=EnrichmentResult(),
    )

    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    async def read() -> tuple[Any, ...]:
        async with factory() as session:
            album = await session.get(Album, "al0")
            return (
                await session.get(AlbumMetadata, "al0"),
                await session.get(ArtistMetadata, "a1"),
                await session.get(TrackMetadata, "t0-0"),
                album.label,
                album.title,
            )

    meta, artist_meta, track_meta, album_label, album_title = asyncio.run(read())
    assert meta.barcode == "0804879535645"
    assert meta.label == "J&R Adventures"
    assert artist_meta.deezer_artist_id == "1424"
    assert track_meta.deezer_track_id == "99"
    # The Qobuz-owned columns are untouched: enrichment writes side tables only.
    assert album_label is None
    assert album_title == "Album 0"


def test_an_automatic_pass_cannot_overwrite_what_a_person_identified(
    factory: Any,
) -> None:
    """The whole point of the ``manual`` marker. A wrong artist MBID does not
    stay in the database — it is written into every file as MUSICBRAINZ_ARTISTID
    and Picard, beets and Roon then believe it."""
    asyncio.run(seed_library(factory, albums=1))
    typed_in = "b95ce3ff-3d05-4e87-9e01-c97b66af13d4"
    derived = "984f8239-8fe1-4683-9c54-10ffb14439e9"

    async def identify_then_enrich() -> None:
        enricher = build(factory, [], enrichment_sources="musicbrainz")
        async with factory() as session:
            await enricher.identify(
                session, EnrichmentEntity.ARTIST, "a1", EnrichmentSource.MUSICBRAINZ, typed_in
            )
        provider = FakeProvider(
            EnrichmentSource.MUSICBRAINZ,
            default=EnrichmentResult(
                artist_fields={
                    "mb_artist_mbid": derived,
                    "mb_match_method": "release-credit",
                    "mb_match_evidence": 1,
                }
            ),
        )
        await build(factory, [provider], enrichment_sources="musicbrainz").tick()

    asyncio.run(identify_then_enrich())

    async def read() -> Any:
        async with factory() as session:
            return await session.get(ArtistMetadata, "a1")

    meta = asyncio.run(read())
    assert meta.mb_artist_mbid == typed_in
    assert meta.mb_match_method == "manual"


def test_a_person_may_correct_their_own_identification(factory: Any) -> None:
    """Only a person overrules a person — including themselves."""
    asyncio.run(seed_library(factory, albums=1))
    first = "b95ce3ff-3d05-4e87-9e01-c97b66af13d4"
    second = "984f8239-8fe1-4683-9c54-10ffb14439e9"

    async def identify_twice() -> None:
        enricher = build(factory, [], enrichment_sources="musicbrainz")
        for value in (first, second):
            async with factory() as session:
                await enricher.identify(
                    session, EnrichmentEntity.ARTIST, "a1", EnrichmentSource.MUSICBRAINZ, value
                )

    asyncio.run(identify_twice())

    async def read() -> Any:
        async with factory() as session:
            return await session.get(ArtistMetadata, "a1")

    assert asyncio.run(read()).mb_artist_mbid == second


def test_what_a_rung_learned_before_saying_no_is_still_written(factory: Any) -> None:
    """"Not identified" and "learned nothing" are different claims. AcoustID that
    could not decode a single file has not identified the release, but it has
    proved every file is broken — and that verdict is the whole point of the rung.
    """
    asyncio.run(seed_library(factory, albums=1, tracks=2))
    verdicts = EnrichmentResult(
        track_fields={
            "t0-0": {"fingerprint_state": "corrupt"},
            "t0-1": {"fingerprint_state": "corrupt"},
        }
    )
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        answers={"al0": MatchRejected("every file failed to decode", partial=verdicts)},
        default=EnrichmentResult(),
    )

    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    async def read() -> tuple[Any, ...]:
        async with factory() as session:
            return (
                await session.get(TrackMetadata, "t0-0"),
                await session.get(TrackMetadata, "t0-1"),
            )

    first, second = asyncio.run(read())
    assert first.fingerprint_state is FingerprintState.CORRUPT
    assert second.fingerprint_state is FingerprintState.CORRUPT
    # And the row still reports the refusal, so it lands on the review list.
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "ambiguous"


def test_absent_leaves_alone_and_explicit_none_clears(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        answers={
            "al0": EnrichmentResult(album_fields={"label": "First", "country": "GB"})
        },
        default=EnrichmentResult(),
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())

    # Second pass: says nothing about country, explicitly empties label.
    provider.answers["al0"] = EnrichmentResult(album_fields={"label": None})
    asyncio.run(_force_due(factory))
    asyncio.run(enricher.tick())

    async def read() -> AlbumMetadata:
        async with factory() as session:
            return await session.get(AlbumMetadata, "al0")

    meta = asyncio.run(read())
    assert meta.label is None, "an explicit None must clear"
    assert meta.country == "GB", "an absent key must not wipe a richer answer"


def test_a_result_can_reopen_another_row_it_unblocked(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    deezer = FakeProvider(
        EnrichmentSource.DEEZER,
        default=EnrichmentResult(
            reopen=[
                (EnrichmentEntity.ARTIST, "a1", EnrichmentSource.MUSICBRAINZ)
            ]
        ),
    )
    mb = FakeProvider(
        EnrichmentSource.MUSICBRAINZ, default=NotMatched("not yet catalogued")
    )
    enricher = build(factory, [deezer, mb])
    asyncio.run(enricher.tick())

    # Drive the MusicBrainz row into a long wait, then let Deezer re-open it.
    async def stale() -> None:
        async with factory() as session:
            row = await session.get(
                EnrichmentState,
                (EnrichmentEntity.ARTIST, "a1", EnrichmentSource.MUSICBRAINZ),
            )
            row.state = "not_found"
            row.attempts = 3
            row.next_attempt_at = NOW + timedelta(days=365)

    asyncio.run(stale())
    asyncio.run(_force_due(factory, only=("a1", "deezer")))
    asyncio.run(enricher.tick())

    row = asyncio.run(states(factory))[("a1", "musicbrainz")]
    assert row.state == "pending"
    assert row.attempts == 0


# ---------------------------------------------------------------------------
# Health and housekeeping
# ---------------------------------------------------------------------------
def test_unbroken_failure_pauses_the_job(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER, default=HttpTransportError("offline", source="deezer")
    )
    enricher = build(
        factory, [provider], enrichment_sources="deezer", enrichment_failure_cutoff=2
    )

    for _ in range(3):
        asyncio.run(_force_due(factory))
        asyncio.run(enricher.tick())

    assert enricher.paused_reason is not None
    assert enricher.enabled is False

    calls_before = len(provider.calls)
    asyncio.run(_force_due(factory))
    asyncio.run(enricher.tick())
    assert len(provider.calls) == calls_before, "a paused job must not keep asking"

    enricher.resume()
    assert enricher.enabled is True


def test_one_success_clears_the_failure_streak(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=2))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        answers={"al0": HttpTransportError("offline", source="deezer")},
        default=EnrichmentResult(album_fields={"label": "ok"}),
    )
    enricher = build(
        factory, [provider], enrichment_sources="deezer", enrichment_failure_cutoff=1
    )

    asyncio.run(enricher.tick())

    assert enricher.paused_reason is None


def test_orphaned_state_rows_are_pruned(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=2))
    provider = FakeProvider(EnrichmentSource.DEEZER)
    asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    async def delete_artist_and_prune() -> int:
        async with factory() as session:
            artist = await session.get(Artist, "a1")
            await session.delete(artist)
            await session.flush()
            return await prune_enrichment_orphans(session)

    removed = asyncio.run(delete_artist_and_prune())

    # Deleting the artist cascades the albums, so all three rows go.
    assert removed == 3
    assert asyncio.run(states(factory)) == {}


def test_gated_source_is_not_asked_at_all(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(EnrichmentSource.DEEZER, ready=False)

    result = asyncio.run(build(factory, [provider], enrichment_sources="deezer").tick())

    assert provider.calls == []
    assert result.gated == 2
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "gated"


def test_disabling_enrichment_stops_everything(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    result = asyncio.run(
        build(factory, [provider], enrichment_sources="deezer", enrichment_enabled=False).tick()
    )

    assert provider.calls == []
    assert result.seeded == 0
    assert asyncio.run(states(factory)) == {}


def test_status_reports_coverage_per_source_and_state(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=2))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        answers={"al0": NotMatched("nothing")},
        default=EnrichmentResult(album_fields={"label": "x"}),
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())

    async def read() -> dict[str, Any]:
        async with factory() as session:
            return await enricher.status(session)

    status = asyncio.run(read())
    assert status["sources"] == ["deezer"]
    assert status["states"]["deezer"] == {"ok": 2, "not_found": 1}


def test_mark_due_queues_a_brand_new_album_at_the_front(factory: Any) -> None:
    """"Enrich on the way in": the indexer queues what it just wrote.

    The row is created rather than waiting for the next seed, which is what makes
    a release discovered thirty seconds ago enrichable now.
    """
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(EnrichmentSource.DEEZER)
    enricher = build(factory, [provider], enrichment_sources="deezer")

    async def go() -> int:
        async with factory() as session:
            return await enricher.mark_due(
                session, EnrichmentEntity.ALBUM, ["al0"], priority=10
            )

    assert asyncio.run(go()) == 1
    row = asyncio.run(states(factory))[("al0", "deezer")]
    assert row.priority == 10
    assert row.state == "pending"


def test_mark_due_leaves_settled_rows_alone(factory: Any) -> None:
    """An ``ok`` row has nothing to learn; ``ambiguous`` is waiting on a human."""
    asyncio.run(seed_library(factory, albums=2))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        answers={"al0": EnrichmentResult(album_fields={"label": "x"})},
        default=AmbiguousMatch("two candidates"),
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())

    async def go() -> int:
        async with factory() as session:
            return await enricher.mark_due(
                session, EnrichmentEntity.ALBUM, ["al0", "al1"]
            )

    assert asyncio.run(go()) == 0
    rows = asyncio.run(states(factory))
    assert rows[("al0", "deezer")].state == "ok"
    assert rows[("al1", "deezer")].state == "ambiguous"


def test_reopen_skips_sources_that_do_not_handle_that_entity(factory: Any) -> None:
    """Cover art is per release, Wikidata per artist.

    Creating a row for the pairing a provider does not serve would hand it the
    wrong kind of subject — and a provider handed an artist where it expects an
    album does not politely decline, it raises.
    """
    asyncio.run(seed_library(factory, albums=1))
    albums_only = FakeProvider(
        EnrichmentSource.DEEZER, entities=(EnrichmentEntity.ALBUM,)
    )
    artists_only = FakeProvider(
        EnrichmentSource.MUSICBRAINZ, entities=(EnrichmentEntity.ARTIST,)
    )
    enricher = build(factory, [albums_only, artists_only])

    async def go() -> tuple[int, int]:
        async with factory() as session:
            return (
                await enricher.reopen(session, EnrichmentEntity.ARTIST, "a1"),
                await enricher.reopen(session, EnrichmentEntity.ALBUM, "al0"),
            )

    artist_rows, album_rows = asyncio.run(go())

    assert (artist_rows, album_rows) == (1, 1)
    rows = asyncio.run(states(factory))
    assert ("a1", "deezer") not in rows
    assert ("al0", "musicbrainz") not in rows


def test_a_wrong_typed_row_is_retired_rather_than_crashing(factory: Any) -> None:
    """An older seeding rule or a hand-edited database can leave one behind."""
    asyncio.run(seed_library(factory, albums=1))
    albums_only = FakeProvider(
        EnrichmentSource.DEEZER, entities=(EnrichmentEntity.ALBUM,)
    )
    enricher = build(factory, [albums_only], enrichment_sources="deezer")

    async def plant() -> None:
        async with factory() as session:
            session.add(
                EnrichmentState(
                    entity_type=EnrichmentEntity.ARTIST,
                    entity_id="a1",
                    source=EnrichmentSource.DEEZER,
                    state="pending",
                    next_attempt_at=datetime.now(timezone.utc),
                )
            )

    asyncio.run(plant())
    result = asyncio.run(enricher.tick())

    assert result.failed == 0
    assert albums_only.calls == ["al0"], "the artist row never reached the provider"
    assert asyncio.run(states(factory))[("a1", "deezer")].state == "gated"


def test_mark_due_never_makes_a_request(factory: Any) -> None:
    """It runs inside the indexer's write transaction, so it must be SQL only.

    A lookup from in there would hold SQLite's single writer across a
    third-party call and time the download worker out into marking a live
    download failed.
    """
    asyncio.run(seed_library(factory, albums=2))
    provider = FakeProvider(EnrichmentSource.DEEZER)
    enricher = build(factory, [provider], enrichment_sources="deezer")

    async def go() -> None:
        async with factory() as session:
            await enricher.mark_due(session, EnrichmentEntity.ALBUM, ["al0", "al1"])

    asyncio.run(go())

    assert provider.calls == []


def test_closing_releases_every_provider(factory: Any) -> None:
    deezer = FakeProvider(EnrichmentSource.DEEZER)
    mb = FakeProvider(EnrichmentSource.MUSICBRAINZ)

    asyncio.run(build(factory, [deezer, mb]).aclose())

    assert deezer.closed and mb.closed


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def _delay(row: EnrichmentState) -> timedelta:
    """How far out ``next_attempt_at`` was scheduled, relative to now."""
    assert row.next_attempt_at is not None
    return enricher_module.utc(row.next_attempt_at) - enricher_module.utc(
        row.last_attempt_at
    )


async def _force_due(factory: Any, *, only: tuple[str, str] | None = None) -> None:
    """Make rows due again without waiting out their real backoff."""
    now = datetime.now(timezone.utc)
    async with factory() as session:
        rows = (await session.execute(select(EnrichmentState))).scalars().all()
        for row in rows:
            if only and (row.entity_id, row.source.value) != only:
                continue
            row.next_attempt_at = now - timedelta(seconds=1)


# ---------------------------------------------------------------------------
# Re-arming dead ends: rows waiting on something that has since arrived
# ---------------------------------------------------------------------------
async def _set_artist_id(factory: Any, **fields: Any) -> None:
    """Write ``artist_metadata`` the way a matched release or a person would."""
    async with factory() as session:
        session.add(ArtistMetadata(artist_id="a1", **fields))


def test_an_album_stranded_before_its_artist_resolved_is_brought_back(
    factory: Any,
) -> None:
    """The leak this repair exists for.

    An album with no barcode can only be matched by browsing its artist's
    discography, and the artist's id is derived from whichever of their releases
    matches on a barcode first — rarely the first one processed. Everything
    reached before that moment records "no artist to browse", stores no
    ``next_attempt_at``, and goes silent permanently while being answerable a few
    minutes later.
    """
    asyncio.run(seed_library(factory, albums=2))
    stuck = FakeProvider(
        EnrichmentSource.DEEZER,
        default=NoMatchKey("no barcode on this release and no Deezer artist to browse"),
    )
    enricher = build(factory, [stuck], enrichment_sources="deezer")
    asyncio.run(enricher.tick())

    rows = asyncio.run(states(factory))
    assert rows[("al0", "deezer")].state == "no_key"
    assert rows[("al0", "deezer")].next_attempt_at is None, "a dead end by design"

    # The artist is identified later — by a barcode match, or by a person.
    asyncio.run(_set_artist_id(factory, deezer_artist_id="4781"))
    stuck.default = EnrichmentResult(match_key="4781")
    result = asyncio.run(enricher.tick())

    # The artist's own row was stuck on the same id, so it comes back too.
    assert result.rearmed == 3
    rows = asyncio.run(states(factory))
    assert rows[("al0", "deezer")].state == "ok"
    assert rows[("al1", "deezer")].state == "ok"
    assert rows[("a1", "deezer")].state == "ok"


def test_a_re_armed_row_leaves_the_review_list(factory: Any) -> None:
    """It is no longer waiting on a human — it is waiting on a tick, and saying
    otherwise puts work on somebody's screen that will do itself."""
    asyncio.run(seed_library(factory, albums=1))
    stuck = FakeProvider(
        EnrichmentSource.DEEZER, default=NoMatchKey("no Deezer artist to browse")
    )
    enricher = build(factory, [stuck], enrichment_sources="deezer")
    asyncio.run(enricher.tick())

    async def review_size() -> int:
        async with factory() as session:
            _, total = await list_review_items(session)
            return total

    assert asyncio.run(review_size()) >= 1
    asyncio.run(_set_artist_id(factory, deezer_artist_id="4781"))
    stuck.default = EnrichmentResult(match_key="4781")
    asyncio.run(enricher.tick())

    assert asyncio.run(review_size()) == 0


def test_only_the_source_whose_id_arrived_is_re_armed(factory: Any) -> None:
    """A Deezer artist id says nothing about MusicBrainz. Re-arming both would
    spend a request proving something already known."""
    asyncio.run(seed_library(factory, albums=1))
    providers = [
        FakeProvider(EnrichmentSource.DEEZER, default=NoMatchKey("no artist to browse")),
        FakeProvider(
            EnrichmentSource.MUSICBRAINZ, default=NoMatchKey("no artist to browse")
        ),
    ]
    enricher = build(factory, providers)
    asyncio.run(enricher.tick())
    asyncio.run(_set_artist_id(factory, deezer_artist_id="4781"))

    for provider in providers:
        provider.calls.clear()
        provider.default = EnrichmentResult()
    assert asyncio.run(enricher.tick()).rearmed == 2

    assert providers[0].calls == ["a1", "al0"], "Deezer re-asked"
    assert providers[1].calls == [], "MusicBrainz left alone"


def test_the_repair_is_idempotent_and_converges(factory: Any) -> None:
    """It runs every tick, so a row it cannot resolve must not be re-armed
    forever — an outcome other than ``no_key`` is what stops the loop."""
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER, default=NoMatchKey("no artist to browse")
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())
    asyncio.run(_set_artist_id(factory, deezer_artist_id="4781"))

    provider.default = NotMatched("not in this artist's discography")
    assert asyncio.run(enricher.tick()).rearmed == 2
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "not_found"

    # Second pass: nothing left in a dead end, so nothing to re-arm.
    assert asyncio.run(enricher.tick()).rearmed == 0


def test_a_gated_rung_that_becomes_configured_reopens_its_rows(factory: Any) -> None:
    """``gated`` waits on configuration and stores no ``next_attempt_at`` either.

    Installing fpcalc or filling in ENRICHMENT_CONTACT would otherwise fix
    nothing already recorded — 266 rows in one real library sat gated while the
    binary they wanted was by then sitting on disk.
    """
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(EnrichmentSource.DEEZER, ready=False)
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())

    rows = asyncio.run(states(factory))
    assert rows[("al0", "deezer")].state == "gated"
    assert rows[("al0", "deezer")].next_attempt_at is None

    provider.ready = True
    result = asyncio.run(enricher.tick())

    assert result.rearmed == 2
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "ok"


def _give_tracks_paths(factory: Any, album_id: str = "al0") -> None:
    """What a disk scan does: point the album's track rows at real files."""

    async def run() -> None:
        async with factory() as session:
            rows = (
                await session.execute(select(Track).where(Track.album_id == album_id))
            ).scalars().all()
            for track in rows:
                track.path = f"/library/{album_id}/{track.id}.flac"

    asyncio.run(run())


def test_files_appearing_reopens_a_row_that_had_nothing_to_fingerprint(
    factory: Any,
) -> None:
    """The third late-arriving input, and the one that read as a scope bug.

    AcoustID needs a file and finds files through track rows, which only the
    download loop used to write. So every release the scanner had adopted
    recorded "nothing on disk to fingerprint" — about its own perfectly readable
    audio — and ``no_key`` stores no ``next_attempt_at``, so it never asked again.
    """
    asyncio.run(seed_library(factory, albums=1, tracks=2))
    provider = FakeProvider(
        EnrichmentSource.ACOUSTID,
        default=NoMatchKey("nothing on disk to fingerprint"),
        entities=(EnrichmentEntity.ALBUM,),
    )
    enricher = build(factory, [provider], enrichment_sources="acoustid")
    asyncio.run(enricher.tick())

    row = asyncio.run(states(factory))[("al0", "acoustid")]
    assert row.state == "no_key"
    assert row.next_attempt_at is None, "a dead end by design"

    _give_tracks_paths(factory)
    provider.default = EnrichmentResult(match_key="2-files")
    result = asyncio.run(enricher.tick())

    assert result.rearmed == 1
    assert asyncio.run(states(factory))[("al0", "acoustid")].state == "ok"


def test_a_row_with_still_no_files_stays_where_it_is(factory: Any) -> None:
    """Same convergence rule as the other two passes: no new input, no re-arm."""
    asyncio.run(seed_library(factory, albums=1, tracks=2))
    provider = FakeProvider(
        EnrichmentSource.ACOUSTID,
        default=NoMatchKey("nothing on disk to fingerprint"),
        entities=(EnrichmentEntity.ALBUM,),
    )
    enricher = build(factory, [provider], enrichment_sources="acoustid")
    asyncio.run(enricher.tick())

    assert asyncio.run(enricher.tick()).rearmed == 0
    assert asyncio.run(states(factory))[("al0", "acoustid")].state == "no_key"


def test_a_still_gated_rung_is_left_where_it_is(factory: Any) -> None:
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(EnrichmentSource.DEEZER, ready=False)
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())

    assert asyncio.run(enricher.tick()).rearmed == 0
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "gated"


def test_a_gated_row_says_what_is_actually_missing(factory: Any) -> None:
    """"acoustid is not configured" is true of two different missing things and
    sent someone to re-check an API key they had already set — the absent piece
    was the fpcalc binary, and the provider knew that and was never asked."""

    class Fussy(FakeProvider):
        def gate_reason(self) -> str:
            return "fpcalc is not installed; fingerprinting is unavailable"

    asyncio.run(seed_library(factory, albums=1))
    enricher = build(
        factory,
        [Fussy(EnrichmentSource.DEEZER, ready=False)],
        enrichment_sources="deezer",
    )
    asyncio.run(enricher.tick())

    assert "fpcalc" in asyncio.run(states(factory))[("al0", "deezer")].last_error


def test_a_rung_with_nothing_specific_to_say_keeps_the_plain_wording(
    factory: Any,
) -> None:
    asyncio.run(seed_library(factory, albums=1))
    enricher = build(
        factory,
        [FakeProvider(EnrichmentSource.DEEZER, ready=False)],
        enrichment_sources="deezer",
    )
    asyncio.run(enricher.tick())

    assert (
        asyncio.run(states(factory))[("al0", "deezer")].last_error
        == "deezer is not configured"
    )


# ---------------------------------------------------------------------------
# Reject — the one dead end nothing re-opens by itself
# ---------------------------------------------------------------------------
# "Exact or nothing" only ever *adds* to the review list, so without a dismissal
# it grows monotonically and people stop reading it. ``rejected`` is that
# dismissal, and every test below is about the same property from a different
# angle: nothing in the machine may put a rejected row back, because a list that
# keeps showing you what you already decided is a list nobody reads. The one
# exception is a person changing their mind, which is ``reopen``.


def _reject(
    factory: Any,
    enricher: Enricher,
    *,
    entity_id: str = "al0",
    entity_type: EnrichmentEntity = EnrichmentEntity.ALBUM,
    source: EnrichmentSource = EnrichmentSource.DEEZER,
    reason: str | None = None,
) -> str:
    async def go() -> str:
        async with factory() as session:
            return await enricher.reject(
                session, entity_type, entity_id, source, reason=reason
            )

    return asyncio.run(go())


def _ambiguous_library(factory: Any, **settings: Any) -> Enricher:
    """One album, one rung, one ambiguous verdict — the review list's own shape.

    Albums only, so the counts below are about the row under test rather than
    about the artist row that would otherwise travel with it.
    """
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        default=AmbiguousMatch("two candidates"),
        entities=(EnrichmentEntity.ALBUM,),
    )
    enricher = build(factory, [provider], enrichment_sources="deezer", **settings)
    asyncio.run(enricher.tick())
    return enricher


def test_rejecting_stops_the_clock_entirely(factory: Any) -> None:
    """``next_attempt_at`` is NULL, so ``_claim`` can never pick it up again.

    That is the whole mechanism: the claim query filters on a non-null due date,
    so a rejected row costs no request however long the process runs.
    """
    enricher = _ambiguous_library(factory)

    message = _reject(factory, enricher, reason="self-released, not on Deezer")

    row = asyncio.run(states(factory))[("al0", "deezer")]
    assert row.state == "rejected"
    assert row.next_attempt_at is None
    assert row.last_error == "self-released, not on Deezer"
    assert "rejected" in message.lower()


def test_a_rejected_row_is_never_claimed_again(factory: Any) -> None:
    enricher = _ambiguous_library(factory)
    provider = enricher._providers[EnrichmentSource.DEEZER]
    _reject(factory, enricher)
    before = len(provider.calls)

    assert asyncio.run(enricher.tick()).claimed == 0
    assert len(provider.calls) == before


def test_seeding_does_not_resurrect_a_rejected_row(factory: Any) -> None:
    """``_seed`` inserts rows that are absent; it must never reset one present."""
    enricher = _ambiguous_library(factory)
    _reject(factory, enricher)

    assert asyncio.run(enricher.tick()).seeded == 0
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "rejected"


def test_rearming_leaves_a_rejected_row_alone(factory: Any) -> None:
    """The distinction the whole state exists for.

    ``gated`` and ``no_key`` are dead ends *waiting on their inputs*, and
    ``_rearm_stranded`` watches for those inputs arriving. A rejection is waiting
    on nobody, so the same pass that rescues the other two must walk past this
    one — even when every input it could possibly want is now present.
    """
    asyncio.run(seed_library(factory, albums=1, tracks=2))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        default=NoMatchKey("no barcode, no artist"),
        entities=(EnrichmentEntity.ALBUM,),
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "no_key"

    _reject(factory, enricher)

    # The very input the no_key row was waiting for turns up.
    async def identify_the_artist() -> None:
        async with factory() as session:
            session.add(ArtistMetadata(artist_id="a1", deezer_artist_id="1234"))

    asyncio.run(identify_the_artist())

    assert asyncio.run(enricher.tick()).rearmed == 0
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "rejected"


def test_a_gated_rung_becoming_ready_does_not_reopen_a_rejection(factory: Any) -> None:
    """The gated pass matches on the state, not on the source. Pin it."""
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER, ready=False, entities=(EnrichmentEntity.ALBUM,)
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "gated"

    # A gated row is not a review row, so reject refuses it — which is itself the
    # point: the only way to reject is to have been asked.
    with pytest.raises(ValueError):
        _reject(factory, enricher)

    provider.ready = True
    assert asyncio.run(enricher.tick()).rearmed == 1


def test_a_rejected_acoustid_row_is_not_re_armed_by_files_appearing(
    factory: Any,
) -> None:
    """``_rearm_fingerprintable`` matches ``no_key`` only. Pin that too."""
    asyncio.run(seed_library(factory, albums=1, tracks=1))
    provider = FakeProvider(
        EnrichmentSource.ACOUSTID,
        default=AmbiguousMatch("two releases explain these files"),
        entities=(EnrichmentEntity.ALBUM,),
    )
    enricher = build(factory, [provider], enrichment_sources="acoustid")
    asyncio.run(enricher.tick())
    _reject(factory, enricher, source=EnrichmentSource.ACOUSTID)

    async def give_it_a_file() -> None:
        async with factory() as session:
            track = await session.get(Track, "t0-0")
            track.path = "/library/a/al0/01.flac"

    asyncio.run(give_it_a_file())

    assert asyncio.run(enricher.tick()).rearmed == 0
    assert asyncio.run(states(factory))[("al0", "acoustid")].state == "rejected"


def test_mark_due_will_not_move_a_rejected_row(factory: Any) -> None:
    """A release landing on disk again does not undo somebody's decision."""
    enricher = _ambiguous_library(factory)
    _reject(factory, enricher)

    async def go() -> int:
        async with factory() as session:
            return await enricher.mark_due(session, EnrichmentEntity.ALBUM, ["al0"])

    assert asyncio.run(go()) == 0
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "rejected"


def test_reopen_is_the_way_back_from_a_rejection(factory: Any) -> None:
    """A person changing their mind has no other route, so this one must work."""
    enricher = _ambiguous_library(factory)
    _reject(factory, enricher, reason="not on Deezer")

    async def go() -> int:
        async with factory() as session:
            return await enricher.reopen(session, EnrichmentEntity.ALBUM, "al0")

    assert asyncio.run(go()) == 1
    row = asyncio.run(states(factory))[("al0", "deezer")]
    assert row.state == "pending"
    assert row.next_attempt_at is not None
    # The person's reason went with the decision it explained.
    assert row.last_error is None
    assert asyncio.run(enricher.tick()).claimed == 1


def test_a_rejection_made_while_the_lookup_is_in_flight_survives_it(
    factory: Any,
) -> None:
    """The window is the whole tick, and it used to close over the person.

    ``_claim`` commits before any request goes out and the review list polls
    every fifteen seconds, so pressing Reject on a row whose lookup is running
    is ordinary rather than exotic. ``_persist`` then wrote the outcome's state
    and next attempt straight over the top: the person got a green toast, and the
    row was back on the list and back in the badge a few seconds later with
    nothing said. Only a person undoes a person — the same rule ``_MANUAL_OWNS``
    states about identifiers.
    """
    asyncio.run(seed_library(factory, albums=1))
    holder: dict[str, Enricher] = {}

    class RejectedMidFlight(FakeProvider):
        """Somebody presses Reject during the lookup. No session is held here —
        that is the invariant the three-phase design exists for, and it is
        exactly what makes the press possible."""

        armed = False

        async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
            if self.armed:
                self.armed = False
                async with factory() as session:
                    await holder["enricher"].reject(
                        session,
                        EnrichmentEntity.ALBUM,
                        job.entity_id,
                        EnrichmentSource.DEEZER,
                        reason="self-released",
                    )
            return await super().fetch(job)

    provider = RejectedMidFlight(
        EnrichmentSource.DEEZER,
        default=AmbiguousMatch("two candidates"),
        entities=(EnrichmentEntity.ALBUM,),
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    holder["enricher"] = enricher
    asyncio.run(enricher.tick())
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "ambiguous"

    # Ambiguous rows are retried on a timer; bring that moment forward without
    # touching the state, exactly as the ladder eventually would.
    async def make_it_due() -> None:
        async with factory() as session:
            row = await session.get(
                EnrichmentState,
                (EnrichmentEntity.ALBUM, "al0", EnrichmentSource.DEEZER),
            )
            row.next_attempt_at = datetime.now(timezone.utc)

    provider.armed = True
    asyncio.run(make_it_due())
    asyncio.run(enricher.tick())

    row = asyncio.run(states(factory))[("al0", "deezer")]
    assert row.state == "rejected"
    assert row.next_attempt_at is None
    assert row.last_error == "self-released"
    # And it stays off the list the next time round, rather than being claimed
    # again a month later.
    assert asyncio.run(enricher.tick()).claimed == 0


def test_what_a_dismissed_row_learned_on_the_way_past_is_still_written(
    factory: Any,
) -> None:
    """The rejection is about the *work item*, not about the release.

    ``reject`` deliberately touches none of the metadata tables, so an answer
    that arrives a moment later is still knowledge and is still recorded. What
    it may not do is re-open the row.
    """
    asyncio.run(seed_library(factory, albums=1))
    holder: dict[str, Enricher] = {}

    class AnsweredAfterTheReject(FakeProvider):
        armed = False

        async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
            if self.armed:
                self.armed = False
                async with factory() as session:
                    await holder["enricher"].reject(
                        session,
                        EnrichmentEntity.ALBUM,
                        job.entity_id,
                        EnrichmentSource.DEEZER,
                    )
            return await super().fetch(job)

    provider = AnsweredAfterTheReject(
        EnrichmentSource.DEEZER,
        answers={"al0": AmbiguousMatch("two candidates")},
        entities=(EnrichmentEntity.ALBUM,),
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    holder["enricher"] = enricher
    asyncio.run(enricher.tick())

    provider.answers = {"al0": EnrichmentResult(album_fields={"barcode": "060249867260"})}
    provider.armed = True

    async def make_it_due() -> None:
        async with factory() as session:
            row = await session.get(
                EnrichmentState,
                (EnrichmentEntity.ALBUM, "al0", EnrichmentSource.DEEZER),
            )
            row.next_attempt_at = datetime.now(timezone.utc)

    asyncio.run(make_it_due())
    asyncio.run(enricher.tick())

    async def go() -> tuple[str, Any]:
        async with factory() as session:
            row = await session.get(
                EnrichmentState,
                (EnrichmentEntity.ALBUM, "al0", EnrichmentSource.DEEZER),
            )
            meta = await session.get(AlbumMetadata, "al0")
            return row.state, (meta.barcode if meta else None)

    state, barcode = asyncio.run(go())
    assert state == "rejected"
    assert barcode == "060249867260"


def test_a_rejected_row_leaves_the_review_list_but_stays_findable(
    factory: Any,
) -> None:
    """Off the default list — and reachable by name, which is the only way to
    find one in order to change your mind about it."""
    enricher = _ambiguous_library(factory)
    _reject(factory, enricher)

    async def go() -> tuple[int, int]:
        async with factory() as session:
            default, _ = await list_review_items(session)
            named, _ = await list_review_items(session, states=("rejected",))
            return len(default), len(named)

    assert asyncio.run(go()) == (0, 1)


def test_a_rejected_row_is_not_actionable_and_explains_itself(factory: Any) -> None:
    enricher = _ambiguous_library(factory)
    _reject(factory, enricher)

    async def go() -> Any:
        async with factory() as session:
            items, _ = await list_review_items(session, states=("rejected",))
            return items[0]

    item = asyncio.run(go())
    assert item.is_actionable is False
    assert item.identify_sources == ()
    assert "said no" in item.state_explanation


def test_rejecting_twice_changes_nothing_and_is_not_an_error(factory: Any) -> None:
    enricher = _ambiguous_library(factory)
    _reject(factory, enricher, reason="first")
    message = _reject(factory, enricher, reason="second")

    assert "already" in message.lower()
    assert asyncio.run(states(factory))[("al0", "deezer")].last_error == "first"


def test_a_settled_row_cannot_be_rejected(factory: Any) -> None:
    """Rejecting an ``ok`` row would close the work item while leaving the
    identifiers it wrote in place, which says something false about both."""
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.DEEZER,
        default=EnrichmentResult(album_fields={"barcode": "0602498672600"}),
    )
    enricher = build(factory, [provider], enrichment_sources="deezer")
    asyncio.run(enricher.tick())

    with pytest.raises(ValueError, match="waiting on a person"):
        _reject(factory, enricher)
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "ok"


def test_rejecting_something_with_no_work_item_is_a_lookup_error(
    factory: Any,
) -> None:
    enricher = _ambiguous_library(factory)

    with pytest.raises(LookupError):
        _reject(factory, enricher, entity_id="nope")


def test_the_purge_still_removes_a_rejected_row(factory: Any) -> None:
    """It is work state, not knowledge: an album that left the library takes its
    whole work list with it, decisions included."""
    from app.core.enricher import purge_out_of_scope_enrichment

    enricher = _ambiguous_library(factory)
    _reject(factory, enricher)

    async def go() -> int:
        async with factory() as session:
            album = await session.get(Album, "al0")
            album.status = AlbumStatus.WANTED
            await session.flush()
            return await purge_out_of_scope_enrichment(session)

    assert asyncio.run(go()) == 1
    assert asyncio.run(states(factory)) == {}


def test_rejected_is_published_in_the_vocabulary(factory: Any) -> None:
    """``MetaOut.enrichment_states`` is built from this tuple, so a client that
    renders a state chip has a name for the one a person just created."""
    from app.enrich.errors import ENRICHMENT_STATES, STATE_REJECTED

    assert STATE_REJECTED in ENRICHMENT_STATES


# ---------------------------------------------------------------------------
# Accept — applies what is held, and never searches
# ---------------------------------------------------------------------------


def _accept(
    factory: Any,
    enricher: Enricher,
    *,
    entity_id: str = "al0",
    entity_type: EnrichmentEntity = EnrichmentEntity.ALBUM,
) -> Any:
    async def go() -> Any:
        async with factory() as session:
            return await enricher.accept(session, entity_type, entity_id)

    return asyncio.run(go())


def _with_suggestion(factory: Any, value: str = "ep") -> None:
    async def go() -> None:
        async with factory() as session:
            session.add(
                AlbumMetadata(album_id="al0", suggested_release_type=value)
            )

    asyncio.run(go())


def test_accept_applies_a_held_release_type_suggestion(factory: Any) -> None:
    enricher = _ambiguous_library(factory)
    _with_suggestion(factory)

    outcome = _accept(factory, enricher)

    assert (outcome.applied, outcome.proposal) == (True, "release_type")
    assert (outcome.previous_value, outcome.applied_value) == ("album", "ep")

    async def read() -> tuple[str, str | None, Any]:
        async with factory() as session:
            album = await session.get(Album, "al0")
            meta = await session.get(AlbumMetadata, "al0")
            return album.release_type, meta.qobuz_release_type, album.status

    release_type, qobuz_original, status = asyncio.run(read())
    assert release_type == "ep"
    # The reversal point, and Qobuz's honest vote in any later consensus.
    assert qobuz_original == "album"
    # One column. Reclassifying can neither create a download nor cancel one.
    assert status is AlbumStatus.DOWNLOADED


def test_accept_does_not_resolve_the_ambiguity_it_was_pressed_from(
    factory: Any,
) -> None:
    """The release type and the match are different questions. Silently marking
    a row matched because somebody accepted a type is the failure this layer
    exists to avoid."""
    enricher = _ambiguous_library(factory)
    _with_suggestion(factory)

    assert _accept(factory, enricher).applied is True
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "ambiguous"


def test_accept_with_nothing_held_applies_nothing_and_offers_the_picker(
    factory: Any,
) -> None:
    """The refusal branch, and the whole safety argument: no proposal means no
    write, not a search for something to write."""
    asyncio.run(seed_library(factory, albums=1))
    provider = FakeProvider(
        EnrichmentSource.MUSICBRAINZ, default=AmbiguousMatch("two candidates")
    )
    enricher = build(factory, [provider], enrichment_sources="musicbrainz")
    asyncio.run(enricher.tick())
    before = len(provider.calls)

    outcome = _accept(factory, enricher)

    assert outcome.applied is False
    assert outcome.proposal is None
    assert (outcome.previous_value, outcome.applied_value) == (None, None)
    assert outcome.identify_sources == ("musicbrainz",)
    # Nothing was asked of any upstream: accept never searches.
    assert len(provider.calls) == before


def test_accept_ignores_a_suggestion_the_album_already_carries(factory: Any) -> None:
    """Not a proposal — a button that changed nothing while claiming to have
    applied something."""
    enricher = _ambiguous_library(factory)
    _with_suggestion(factory, "album")

    outcome = _accept(factory, enricher)

    assert outcome.applied is False
    assert asyncio.run(states(factory))[("al0", "deezer")].state == "ambiguous"
    # And it says which of the two empty-handed answers this is. "No source has
    # offered a value the others agreed on" would be a false statement about a
    # row where one demonstrably has.
    assert "already" in outcome.message
    assert "no source has offered" not in outcome.message.lower()


def test_accept_refuses_a_suggestion_outside_the_release_type_vocabulary(
    factory: Any,
) -> None:
    """A value nothing downstream can read must not reach ``Album.release_type``:
    it feeds ``Artist.accepts()`` and every naming template."""
    enricher = _ambiguous_library(factory)
    _with_suggestion(factory, "boxset")

    with pytest.raises(ValueError, match="not a release type"):
        _accept(factory, enricher)

    async def read() -> str:
        async with factory() as session:
            return (await session.get(Album, "al0")).release_type

    assert asyncio.run(read()) == "album"


def test_accept_on_something_that_is_gone_is_a_lookup_error(factory: Any) -> None:
    enricher = _ambiguous_library(factory)

    with pytest.raises(LookupError):
        _accept(factory, enricher, entity_id="nope")


def test_accept_on_an_artist_holds_no_proposal(factory: Any) -> None:
    """Consensus proposes nothing about an artist, so this is always the picker."""
    asyncio.run(seed_library(factory, albums=1))
    enricher = build(
        factory,
        [FakeProvider(EnrichmentSource.DEEZER, default=AmbiguousMatch("two"))],
        enrichment_sources="deezer",
    )
    asyncio.run(enricher.tick())

    outcome = _accept(
        factory, enricher, entity_id="a1", entity_type=EnrichmentEntity.ARTIST
    )

    assert outcome.applied is False
    assert "deezer" in outcome.identify_sources


def test_the_review_list_says_what_accept_would_do(factory: Any) -> None:
    """So the button can be labelled before it is pressed — the alternative is a
    button reading "Accept & apply" that opens a search box."""
    enricher = _ambiguous_library(factory)

    async def read() -> Any:
        async with factory() as session:
            items, _ = await list_review_items(session)
            return items[0]

    assert asyncio.run(read()).suggested_release_type is None

    _with_suggestion(factory)
    assert asyncio.run(read()).suggested_release_type == "ep"


# ---------------------------------------------------------------------------
# The cascade after a manual identification
# ---------------------------------------------------------------------------
#
# One manual match is rarely one answer. An artist's id is what their
# barcode-less albums are all waiting on, and MusicBrainz's URL relations are
# where Wikidata's QID comes from. Leaving that to the scheduler meant a person
# answered one question and was then shown the consequences of their own answer
# as further questions, five minutes apart.
_MBID = "b95ce3ff-3d05-4e87-9e01-c97b66af13d4"


async def _identify(
    enricher: Enricher,
    factory: Any,
    entity_id: str = "a1",
    *,
    entity_type: EnrichmentEntity = EnrichmentEntity.ARTIST,
    source: EnrichmentSource = EnrichmentSource.MUSICBRAINZ,
    value: str = _MBID,
    cascade: bool = True,
) -> str:
    async with factory() as session:
        return await enricher.identify(
            session, entity_type, entity_id, source, value, cascade=cascade
        )


def test_identifying_fetches_the_entity_without_waiting_for_a_tick(
    factory: Any,
) -> None:
    """The press used to buy one row moving to ``pending`` and a message saying
    "on the next pass". Five minutes is a long time to wonder whether the thing
    you typed worked."""
    asyncio.run(seed_library(factory, albums=1))
    mb = FakeProvider(EnrichmentSource.MUSICBRAINZ)
    enricher = build(factory, [mb], enrichment_sources="musicbrainz")

    message = asyncio.run(_identify(enricher, factory))

    assert "a1" in mb.calls
    assert "straight away" in message
    assert asyncio.run(states(factory))[("a1", "musicbrainz")].state == "ok"


def test_the_cascade_reaches_the_albums_the_artist_id_unblocked(
    factory: Any,
) -> None:
    """The reason somebody typed the id in the first place. An album with no
    barcode is only ever matched by browsing its artist's discography, so every
    one of them was sitting on "no artist to browse"."""
    asyncio.run(seed_library(factory, albums=2))
    mb = FakeProvider(
        EnrichmentSource.MUSICBRAINZ, default=NoMatchKey("no barcode, no artist")
    )
    enricher = build(factory, [mb], enrichment_sources="musicbrainz")
    asyncio.run(enricher.tick())
    rows = asyncio.run(states(factory))
    assert rows[("al0", "musicbrainz")].state == "no_key"
    assert rows[("al1", "musicbrainz")].state == "no_key"

    mb.default = EnrichmentResult()
    mb.calls.clear()
    asyncio.run(_identify(enricher, factory))

    # Both albums, in the same press — not one per five-minute tick.
    assert set(mb.calls) >= {"al0", "al1"}
    rows = asyncio.run(states(factory))
    assert rows[("al0", "musicbrainz")].state == "ok"
    assert rows[("al1", "musicbrainz")].state == "ok"


def test_the_cascade_follows_a_reopen_to_the_next_rung(factory: Any) -> None:
    """Wikidata hangs entirely off the QID MusicBrainz publishes, so it only
    becomes worth running once that arrives. Before this it arrived in one tick
    and was acted on in the next."""
    asyncio.run(seed_library(factory, albums=1))
    mb = FakeProvider(
        EnrichmentSource.MUSICBRAINZ,
        answers={
            "a1": EnrichmentResult(
                artist_fields={"wikidata_qid": "Q123"},
                reopen=[
                    (EnrichmentEntity.ARTIST, "a1", EnrichmentSource.WIKIDATA)
                ],
            )
        },
    )
    wikidata = FakeProvider(
        EnrichmentSource.WIKIDATA, entities=(EnrichmentEntity.ARTIST,)
    )
    enricher = build(
        factory, [mb, wikidata], enrichment_sources="musicbrainz,wikidata"
    )

    asyncio.run(_identify(enricher, factory))

    assert wikidata.calls == ["a1"]
    assert asyncio.run(states(factory))[("a1", "wikidata")].state == "ok"


def test_wikidata_is_re_armed_when_the_qid_turns_up_later(factory: Any) -> None:
    """The state-based backstop the rung never had. Its only route back used to
    be the single ``reopen`` MusicBrainz appends on the tick that discovers the
    QID — which fires exactly once, and never at all when the QID was already in
    the snapshot. A missed event stranded the row permanently, because ``no_key``
    stores no ``next_attempt_at``."""
    asyncio.run(seed_library(factory, albums=1))
    wikidata = FakeProvider(
        EnrichmentSource.WIKIDATA,
        default=NoMatchKey("no Wikidata id yet"),
        entities=(EnrichmentEntity.ARTIST,),
    )
    enricher = build(factory, [wikidata], enrichment_sources="wikidata")
    asyncio.run(enricher.tick())
    assert asyncio.run(states(factory))[("a1", "wikidata")].state == "no_key"

    async def the_qid_arrives() -> None:
        async with factory() as session:
            session.add(ArtistMetadata(artist_id="a1", wikidata_qid="Q123"))
            await session.commit()

    asyncio.run(the_qid_arrives())
    wikidata.default = EnrichmentResult()

    assert asyncio.run(enricher.tick()).rearmed == 1
    assert asyncio.run(states(factory))[("a1", "wikidata")].state == "ok"


def test_the_cascade_stays_inside_the_entity_it_was_given(factory: Any) -> None:
    """One hop, not a transitive closure. A cascade is a request's worth of work
    and the rest of the library is the tick's."""
    asyncio.run(seed_library(factory, albums=1))

    async def a_second_artist() -> None:
        async with factory() as session:
            session.add(Artist(id="a2", name="Mark Knopfler", albums_count=1))
            session.add(
                Album(
                    id="other",
                    artist_id="a2",
                    title="Tracker",
                    status=AlbumStatus.DOWNLOADED,
                )
            )
            await session.commit()

    asyncio.run(a_second_artist())
    mb = FakeProvider(EnrichmentSource.MUSICBRAINZ)
    enricher = build(factory, [mb], enrichment_sources="musicbrainz")

    asyncio.run(_identify(enricher, factory))

    assert "a2" not in mb.calls
    assert "other" not in mb.calls


def test_a_cascade_stands_down_while_a_tick_is_running(factory: Any) -> None:
    """The lock is what stops two drains double-spending the request budget.
    Waiting on it properly would block the request for a whole tick budget;
    skipping costs nothing, because the tick holding it claims these very rows.
    """
    asyncio.run(seed_library(factory, albums=1))
    mb = FakeProvider(EnrichmentSource.MUSICBRAINZ)
    enricher = build(factory, [mb], enrichment_sources="musicbrainz")

    async def identify_during_a_tick() -> str:
        async with enricher._lock:
            return await _identify(enricher, factory)

    message = asyncio.run(identify_during_a_tick())

    assert mb.calls == []
    assert "already running" in message
    # Recorded and due, so nothing is lost — the tick takes it.
    assert asyncio.run(states(factory))[("a1", "musicbrainz")].state == "pending"


def test_the_cascade_can_be_declined(factory: Any) -> None:
    """A caller writing several ids in one go drains once at the end rather than
    once per id."""
    asyncio.run(seed_library(factory, albums=1))
    mb = FakeProvider(EnrichmentSource.MUSICBRAINZ)
    enricher = build(factory, [mb], enrichment_sources="musicbrainz")

    message = asyncio.run(_identify(enricher, factory, cascade=False))

    assert mb.calls == []
    assert "next pass" in message


def test_the_cascade_holds_no_transaction_across_a_provider_call(
    factory: Any,
) -> None:
    """The same rule the tick obeys, and for the same reason: SQLite has one
    writer, and a transaction held across a MusicBrainz call blocks
    ``AlbumDownloader``'s per-track commits until ``busy_timeout`` expires — at
    which point ``QueueWorker`` marks a perfectly good download failed.

    A cascade cannot assert the tick's "no session open at all": it runs inside
    the request that identified something, and that request's session belongs to
    the caller and stays open for the whole request. What it *can* guarantee is
    the thing the invariant is actually about — that the caller's session is
    committed and holds no transaction before the first request goes out, and
    that the cascade's own sessions are closed around every call rather than
    held across one. Hence the two assertions: no open transaction, and never a
    *second* session.
    """
    asyncio.run(seed_library(factory, albums=1))
    mb = FakeProvider(EnrichmentSource.MUSICBRAINZ)
    enricher = build(factory, [mb], enrichment_sources="musicbrainz")
    open_transactions: list[bool] = []

    async def identify_watching_the_session() -> None:
        async with factory() as session:
            fetch = mb.fetch

            async def watching(job: EnrichmentJob) -> Any:
                open_transactions.append(session.in_transaction())
                return await fetch(job)

            mb.fetch = watching  # type: ignore[method-assign]
            await enricher.identify(
                session,
                EnrichmentEntity.ARTIST,
                "a1",
                EnrichmentSource.MUSICBRAINZ,
                _MBID,
            )

    asyncio.run(identify_watching_the_session())

    assert mb.calls, "the cascade has to have run for this to prove anything"
    assert open_transactions == [False] * len(mb.calls)
    # One session, and it is the caller's own — idle since the commit. The
    # cascade's sessions are opened and closed around each phase.
    assert mb.sessions_open_during_fetch == [1] * len(mb.calls)
