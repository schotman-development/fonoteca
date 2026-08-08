"""Enrichment is scoped to the library, not to the catalogue.

Following one prolific artist puts their whole discography in ``albums``, and
enriching all of it is work nobody asked for: metadata is written into files,
rendered into NFOs and read off the artist page, and a release that is not on
disk has none of those. One real database made the arithmetic plain — 3313
albums, 32 of them downloaded, 11418 enrichment rows pending — so the thirty-two
that mattered sat behind three thousand that did not.

What is pinned down here:

* **only ``downloaded`` is in scope**, and that includes the shape the disk scan
  adopts, which has no ``Track`` rows at all. ``wanted`` and ``skipped`` are not
  seeded, not claimed, and ``mark_due`` refuses to create a row for them;
* **an artist rides on their albums** — eligible while they own at least one
  release on disk, and not one moment longer;
* **landing on disk is the moment eligibility starts**, asserted through the
  real hooks: the download loop finalising a release, the queue worker promoting
  one, and the disk scan adopting one;
* **the purge deletes work, never knowledge.** It removes out-of-scope
  ``enrichment_state`` rows, leaves in-scope ones untouched — including the ones
  a person is waiting on — and is safe to run twice;
* **Qobuz identity is unchanged by any of it.** Ids stay opaque strings, edition
  grouping still works across a half-enriched library, an applied release type
  still reverses through ``album_metadata.qobuz_release_type`` after a purge, and
  an album with no metadata row still renders;
* **the coverage denominator is the library.** "32 identified" against a
  catalogue of 3313 reads as 1% of a job that is in fact finished.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from typing import Any, AsyncIterator, Iterator

import mutagen
import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.api import deps, routes_api
from app.config import Settings, get_settings
from app.core import state as state_module
from app.core.downloader import AlbumDownloader, DownloadResult
from app.core.enricher import (
    Enricher,
    enrichment_scope_counts,
    list_review_items,
    purge_out_of_scope_enrichment,
)
from app.core.indexer import dedupe_key
from app.core.queue import QueueWorker
from app.core.scanner import LibraryScanner
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
    QueueItem,
    QueueState,
    Track,
    TrackStatus,
)

#: Real Qobuz ids: one opaque, one that looks like a number and is not.
OPAQUE_ID = "uyej1o165e870"
NUMERIC_LOOKING_ID = "0884977859300"

#: A MusicBrainz release-group id, for the edition-grouping tests.
GROUP = "45017130-d783-4a09-97e1-780a3fd40382"


# ---------------------------------------------------------------------------
# Doubles
# ---------------------------------------------------------------------------
class FakeProvider:
    """One rung, answering from a canned script and recording what it was asked.

    ``calls`` is the whole point: a scope regression shows up as an id in here
    that should never have reached a provider at all.
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
        self.subjects: list[Any] = []

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        return entity_type in self.entities

    def is_ready(self) -> bool:
        return self.ready

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        self.calls.append(job.entity_id)
        self.subjects.append(job.subject)
        answer = self.answers.get(job.entity_id, self.default)
        if isinstance(answer, BaseException):
            raise answer
        return answer

    async def aclose(self) -> None:
        return None


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
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

    session_factory.maker = maker  # type: ignore[attr-defined]
    try:
        yield session_factory
    finally:
        asyncio.run(engine.dispose())


def make_settings(**overrides: Any) -> Settings:
    base: dict[str, Any] = {
        "qobuz_app_id": "app",
        "qobuz_user_auth_token": "token",
        "enrichment_sources": "deezer",
        "enrichment_batch_size": 50,
    }
    base.update(overrides)
    return Settings(**base)


def build(factory: Any, providers: list[Any], **settings: Any) -> Enricher:
    return Enricher(
        providers, settings=make_settings(**settings), session_factory=factory
    )


async def seed(
    factory: Any,
    albums: tuple[tuple[str, AlbumStatus], ...],
    *,
    artist_id: str = "a1",
    artist_name: str = "Joe Bonamassa",
    tracks: int = 0,
) -> None:
    """One artist and the releases named, each with the status given."""
    async with factory() as session:
        session.add(Artist(id=artist_id, name=artist_name, albums_count=len(albums)))
        for album_id, status in albums:
            session.add(
                Album(
                    id=album_id,
                    artist_id=artist_id,
                    title=f"Record {album_id}",
                    tracks_count=tracks,
                    status=status,
                )
            )
            for number in range(tracks):
                session.add(
                    Track(
                        id=f"t-{album_id}-{number}",
                        album_id=album_id,
                        title=f"Track {number}",
                        track_number=number + 1,
                        media_number=1,
                        status=TrackStatus.DOWNLOADED,
                    )
                )


async def states(factory: Any) -> dict[tuple[str, str], EnrichmentState]:
    async with factory() as session:
        rows = (await session.execute(select(EnrichmentState))).scalars().all()
        return {(row.entity_id, row.source.value): row for row in rows}


async def set_status(factory: Any, album_id: str, status: AlbumStatus) -> None:
    """Move one release in or out of the library, as the verify pass would."""
    async with factory() as session:
        album = await session.get(Album, album_id)
        album.status = status


# ---------------------------------------------------------------------------
# What is in scope
# ---------------------------------------------------------------------------
@pytest.mark.parametrize("status", [AlbumStatus.WANTED, AlbumStatus.SKIPPED])
def test_an_album_that_is_not_on_disk_is_never_seeded_or_claimed(
    factory: Any, status: AlbumStatus
) -> None:
    """The whole point: a release nobody owns has no files to tag and no NFO to
    write, so there is nothing for the enrichment to be applied to."""
    asyncio.run(seed(factory, ((OPAQUE_ID, status),)))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    result = asyncio.run(build(factory, [provider]).tick())

    assert provider.calls == []
    assert result.seeded == 0
    assert asyncio.run(states(factory)) == {}


@pytest.mark.parametrize("status", [AlbumStatus.WANTED, AlbumStatus.SKIPPED])
def test_mark_due_creates_no_row_for_an_album_that_is_not_on_disk(
    factory: Any, status: AlbumStatus
) -> None:
    """The indexer calls this for a whole discography. Dropping what is out of
    scope here is what keeps every caller from having to know the rule."""
    asyncio.run(seed(factory, ((OPAQUE_ID, status),)))
    enricher = build(factory, [FakeProvider(EnrichmentSource.DEEZER)])

    async def go() -> int:
        async with factory() as session:
            return await enricher.mark_due(
                session, EnrichmentEntity.ALBUM, [OPAQUE_ID], priority=10
            )

    assert asyncio.run(go()) == 0
    assert asyncio.run(states(factory)) == {}


def test_a_downloaded_album_is_seeded_and_claimed(factory: Any) -> None:
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.DOWNLOADED),), tracks=2))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider]).tick())

    assert provider.calls == ["a1", OPAQUE_ID]
    assert asyncio.run(states(factory))[(OPAQUE_ID, "deezer")].state == "ok"


def test_an_album_the_scanner_adopted_is_in_scope_with_no_track_rows(
    factory: Any,
) -> None:
    """Only the download loop writes ``Track`` rows, so an adopted release has
    none — and the status is the only thing it and a downloaded one share. Ask
    for track rows instead and the half of a library that predates Qobuzarr is
    silently excluded."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.DOWNLOADED),), tracks=0))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider]).tick())

    assert OPAQUE_ID in provider.calls
    album_subject = provider.subjects[provider.calls.index(OPAQUE_ID)]
    assert album_subject.tracks == (), "adopted from disk: nothing to enumerate"


def test_only_the_part_of_a_discography_that_is_on_disk_is_enriched(
    factory: Any,
) -> None:
    """The 3313-albums-to-32 case, in miniature."""
    asyncio.run(
        seed(
            factory,
            (
                (OPAQUE_ID, AlbumStatus.DOWNLOADED),
                (NUMERIC_LOOKING_ID, AlbumStatus.WANTED),
                ("wxl78pvfqlm3b", AlbumStatus.SKIPPED),
                ("qx9012mnbvc", AlbumStatus.QUEUED),
            ),
        )
    )
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider]).tick())

    assert sorted(provider.calls) == sorted(["a1", OPAQUE_ID])


def test_an_artist_with_nothing_on_disk_is_not_enriched(factory: Any) -> None:
    """Following is a statement about what to watch, not about what is owned."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.WANTED),)))
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider]).tick())

    assert "a1" not in provider.calls
    assert asyncio.run(states(factory)) == {}


def test_an_artist_is_in_scope_from_their_first_download_onwards(
    factory: Any,
) -> None:
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.WANTED),)))
    provider = FakeProvider(EnrichmentSource.DEEZER)
    enricher = build(factory, [provider])
    asyncio.run(enricher.tick())
    assert provider.calls == []

    asyncio.run(set_status(factory, OPAQUE_ID, AlbumStatus.DOWNLOADED))
    asyncio.run(enricher.tick())

    assert provider.calls == ["a1", OPAQUE_ID]


def test_an_artist_leaves_scope_with_their_last_download(factory: Any) -> None:
    """The verify pass demotes an album whose files vanished; the artist that
    was only in scope because of it goes with it."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.DOWNLOADED),)))
    provider = FakeProvider(EnrichmentSource.DEEZER)
    asyncio.run(build(factory, [provider]).tick())
    assert ("a1", "deezer") in asyncio.run(states(factory))

    asyncio.run(set_status(factory, OPAQUE_ID, AlbumStatus.WANTED))

    async def purge() -> int:
        async with factory() as session:
            return await purge_out_of_scope_enrichment(session)

    assert asyncio.run(purge()) == 2
    assert asyncio.run(states(factory)) == {}


def test_a_stale_row_is_not_claimed_even_before_the_purge_reaches_it(
    factory: Any,
) -> None:
    """The claim is the statement that spends requests, so it carries the scope
    test itself rather than trusting the nightly purge to have run."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.DOWNLOADED),)))
    provider = FakeProvider(EnrichmentSource.DEEZER)
    enricher = build(factory, [provider])
    asyncio.run(enricher.tick())
    provider.calls.clear()

    asyncio.run(set_status(factory, OPAQUE_ID, AlbumStatus.WANTED))

    async def force_due() -> None:
        async with factory() as session:
            for row in (await session.execute(select(EnrichmentState))).scalars():
                row.state = "pending"
                row.next_attempt_at = datetime.now(timezone.utc)

    asyncio.run(force_due())
    asyncio.run(enricher.tick())

    assert provider.calls == [], "an out-of-scope row must cost nothing at all"


def test_the_review_list_only_shows_what_is_in_the_library(factory: Any) -> None:
    """Asking somebody to identify a release they do not own is asking for work
    that changes nothing on disk."""
    asyncio.run(
        seed(
            factory,
            (
                (OPAQUE_ID, AlbumStatus.DOWNLOADED),
                (NUMERIC_LOOKING_ID, AlbumStatus.WANTED),
            ),
        )
    )

    async def plant_and_read() -> tuple[list[Any], int]:
        async with factory() as session:
            for album_id in (OPAQUE_ID, NUMERIC_LOOKING_ID):
                session.add(
                    EnrichmentState(
                        entity_type=EnrichmentEntity.ALBUM,
                        entity_id=album_id,
                        source=EnrichmentSource.MUSICBRAINZ,
                        state="ambiguous",
                        last_error="two releases matched",
                    )
                )
            await session.flush()
            return await list_review_items(session)

    items, total = asyncio.run(plant_and_read())

    assert total == 1
    assert [item.entity_id for item in items] == [OPAQUE_ID]


# ---------------------------------------------------------------------------
# Landing on disk is the moment eligibility starts — through the real hooks
# ---------------------------------------------------------------------------
@pytest.fixture(name="running_enricher")
def running_enricher_fixture(
    factory: Any, monkeypatch: pytest.MonkeyPatch
) -> Enricher:
    """An enricher reachable through ``app.core.state``, as in production.

    ``mark_library_due`` finds it with ``state_or_none()`` rather than being
    plumbed through three constructors, so a test that wants to observe the hook
    has to put one there.
    """
    enricher = build(factory, [FakeProvider(EnrichmentSource.DEEZER)])
    monkeypatch.setattr(
        state_module, "_state", SimpleNamespace(enricher=enricher), raising=False
    )
    return enricher


def test_a_finished_download_makes_its_release_enrichable_at_once(
    factory: Any, running_enricher: Enricher
) -> None:
    """The download loop's own hook. Waiting for the next seed would spend the
    minutes somebody is actually looking at the page on the backlog instead."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.QUEUED),)))

    async def finish() -> None:
        downloader = AlbumDownloader(
            SimpleNamespace(),
            make_settings(nfo_enabled=False, upgrade_cleanup=False),
        )
        async with factory() as session:
            album = await session.get(Album, OPAQUE_ID)
            await downloader._finalise(
                session,
                album,
                "Joe Bonamassa",
                None,
                DownloadResult(album_id=OPAQUE_ID, status=AlbumStatus.DOWNLOADED),
            )

    asyncio.run(finish())

    rows = asyncio.run(states(factory))
    assert rows[(OPAQUE_ID, "deezer")].state == "pending"
    assert rows[(OPAQUE_ID, "deezer")].priority == 10, "in front of the backlog"
    # The artist comes with it, and ahead of it: both album browse paths need
    # the artist's id, so an album whose artist is unknown can only say no_key.
    assert ("a1", "deezer") in rows


def test_the_queue_worker_marks_an_album_it_promoted_itself(
    factory: Any, running_enricher: Enricher
) -> None:
    """The other download path: a callable that reported success without setting
    the status. The worker sets it, so the worker owns the hook."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.QUEUED),)))

    async def finish() -> None:
        async with factory() as session:
            item = QueueItem(album_id=OPAQUE_ID, state=QueueState.ACTIVE)
            session.add(item)
            await session.flush()
            item_id = item.id
        worker = QueueWorker(settings=make_settings(), session_factory=factory)
        await worker._finish_success(item_id, OPAQUE_ID, "Joe Bonamassa - Record")

    asyncio.run(finish())

    rows = asyncio.run(states(factory))
    assert (OPAQUE_ID, "deezer") in rows
    assert ("a1", "deezer") in rows


def test_the_disk_scan_makes_an_adopted_release_enrichable_at_once(
    factory: Any, running_enricher: Enricher, tmp_path: Path
) -> None:
    """Adoption is the same event as a download finishing — the release is on
    disk — so it is the same moment eligibility starts. The scan still makes no
    request and writes nothing to the filesystem: it queues a row."""
    root = tmp_path / "music"
    write_album(root, "Nils Frahm", "All Melody (2018)", tracks=3)
    asyncio.run(seed(factory, (), artist_id="100", artist_name="Nils Frahm"))

    async def run_scan() -> Any:
        async with factory() as session:
            session.add(
                Album(
                    id=OPAQUE_ID,
                    artist_id="100",
                    title="All Melody",
                    tracks_count=3,
                    status=AlbumStatus.WANTED,
                )
            )
            await session.flush()
            settings = get_settings().model_copy(update={"library_path": root})
            return await LibraryScanner(settings=settings).scan(session, record=False)

    result = asyncio.run(run_scan())
    assert result.albums_adopted == 1, "the fixture never adopted anything"

    rows = asyncio.run(states(factory))
    assert (OPAQUE_ID, "deezer") in rows
    assert ("100", "deezer") in rows


def test_a_scan_that_adopts_nothing_queues_nothing(
    factory: Any, running_enricher: Enricher, tmp_path: Path
) -> None:
    """A dry run reports what it would adopt and changes nothing — including
    the enrichment queue, which is a change like any other."""
    root = tmp_path / "music"
    write_album(root, "Nils Frahm", "All Melody (2018)", tracks=3)
    asyncio.run(seed(factory, (), artist_id="100", artist_name="Nils Frahm"))

    async def run_scan() -> Any:
        async with factory() as session:
            session.add(
                Album(
                    id=OPAQUE_ID,
                    artist_id="100",
                    title="All Melody",
                    tracks_count=3,
                    status=AlbumStatus.WANTED,
                )
            )
            await session.flush()
            settings = get_settings().model_copy(update={"library_path": root})
            return await LibraryScanner(settings=settings).scan(
                session, apply=False, record=False
            )

    asyncio.run(run_scan())

    assert asyncio.run(states(factory)) == {}


# ---------------------------------------------------------------------------
# The purge
# ---------------------------------------------------------------------------
async def _seed_mixed_rows(factory: Any) -> None:
    """One album on disk and one not, each with a state row per source.

    The out-of-scope rows are planted directly rather than seeded, because that
    is how they arise in the real database: they were written when the scope was
    wider, or before the album's files went missing.
    """
    await seed(
        factory,
        (
            (OPAQUE_ID, AlbumStatus.DOWNLOADED),
            (NUMERIC_LOOKING_ID, AlbumStatus.WANTED),
        ),
    )
    async with factory() as session:
        session.add(Artist(id="a2", name="Stranger", albums_count=0))
        for entity_type, entity_id, state in (
            (EnrichmentEntity.ALBUM, OPAQUE_ID, "ambiguous"),
            (EnrichmentEntity.ALBUM, NUMERIC_LOOKING_ID, "ambiguous"),
            (EnrichmentEntity.ALBUM, NUMERIC_LOOKING_ID, "pending"),
            (EnrichmentEntity.ARTIST, "a1", "ok"),
            (EnrichmentEntity.ARTIST, "a2", "pending"),
        ):
            source = (
                EnrichmentSource.MUSICBRAINZ
                if state != "pending"
                else EnrichmentSource.DEEZER
            )
            session.add(
                EnrichmentState(
                    entity_type=entity_type,
                    entity_id=entity_id,
                    source=source,
                    state=state,
                    last_error="two releases matched" if state == "ambiguous" else None,
                    attempts=2,
                    next_attempt_at=datetime(2026, 7, 31, 12, tzinfo=timezone.utc),
                )
            )


def _snapshot(rows: dict[tuple[str, str], EnrichmentState]) -> dict[Any, Any]:
    """Everything about a row that a purge must not change."""
    return {
        key: (row.state, row.attempts, row.last_error, row.next_attempt_at)
        for key, row in rows.items()
    }


def test_the_purge_deletes_the_out_of_scope_rows_and_only_those(
    factory: Any,
) -> None:
    asyncio.run(_seed_mixed_rows(factory))

    async def purge() -> int:
        async with factory() as session:
            return await purge_out_of_scope_enrichment(session)

    removed = asyncio.run(purge())

    assert removed == 3
    assert set(asyncio.run(states(factory))) == {
        (OPAQUE_ID, "musicbrainz"),
        ("a1", "musicbrainz"),
    }


def test_the_purge_keeps_the_row_a_person_is_waiting_on(factory: Any) -> None:
    """An in-scope ``ambiguous`` row is somebody's to-do item, and the reason
    attached to it is what makes it actionable."""
    asyncio.run(_seed_mixed_rows(factory))

    async def purge_and_read() -> EnrichmentState:
        async with factory() as session:
            await purge_out_of_scope_enrichment(session)
        return (await states(factory))[(OPAQUE_ID, "musicbrainz")]

    row = asyncio.run(purge_and_read())

    assert row.state == "ambiguous"
    assert row.last_error == "two releases matched"
    assert row.attempts == 2


def test_the_purge_is_idempotent(factory: Any) -> None:
    """It runs nightly and from the CLI, so a second pass must be a no-op rather
    than a second opinion."""
    asyncio.run(_seed_mixed_rows(factory))

    async def purge() -> int:
        async with factory() as session:
            return await purge_out_of_scope_enrichment(session)

    first = asyncio.run(purge())
    after_first = _snapshot(asyncio.run(states(factory)))
    second = asyncio.run(purge())
    after_second = _snapshot(asyncio.run(states(factory)))

    assert (first, second) == (3, 0)
    assert after_second == after_first


def test_the_purge_on_an_untouched_database_does_nothing(factory: Any) -> None:
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.DOWNLOADED),)))

    async def purge() -> int:
        async with factory() as session:
            return await purge_out_of_scope_enrichment(session)

    assert asyncio.run(purge()) == 0


def test_what_the_purge_removes_comes_straight_back_when_the_album_does(
    factory: Any,
) -> None:
    """``enrichment_state`` is a work list, not a record. That is the whole
    safety argument for deleting from it."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.DOWNLOADED),)))
    enricher = build(factory, [FakeProvider(EnrichmentSource.DEEZER)])
    asyncio.run(enricher.tick())

    asyncio.run(set_status(factory, OPAQUE_ID, AlbumStatus.WANTED))

    async def purge() -> int:
        async with factory() as session:
            return await purge_out_of_scope_enrichment(session)

    assert asyncio.run(purge()) == 2
    asyncio.run(set_status(factory, OPAQUE_ID, AlbumStatus.DOWNLOADED))
    assert asyncio.run(enricher.tick()).seeded == 2


# ---------------------------------------------------------------------------
# Qobuz identity — nothing here may make a release ambiguous
# ---------------------------------------------------------------------------
def test_album_ids_stay_opaque_strings(factory: Any) -> None:
    """Real ids look like ``uyej1o165e870`` and ``0884977859300``. Coercing one
    that happens to be all digits loses the leading zero and the row with it."""
    asyncio.run(
        seed(
            factory,
            (
                (OPAQUE_ID, AlbumStatus.DOWNLOADED),
                (NUMERIC_LOOKING_ID, AlbumStatus.DOWNLOADED),
            ),
        )
    )
    provider = FakeProvider(EnrichmentSource.DEEZER)

    asyncio.run(build(factory, [provider]).tick())

    async def read() -> list[str]:
        async with factory() as session:
            return [
                str(value)
                for value in (await session.execute(select(Album.id))).scalars().all()
            ]

    assert sorted(asyncio.run(read())) == sorted([OPAQUE_ID, NUMERIC_LOOKING_ID])
    rows = asyncio.run(states(factory))
    assert (NUMERIC_LOOKING_ID, "deezer") in rows
    assert all(isinstance(row.entity_id, str) for row in rows.values())


def test_an_applied_release_type_still_reverses_after_a_purge(factory: Any) -> None:
    """``album_metadata.qobuz_release_type`` is the only thing that makes the one
    Qobuz-owned column enrichment writes reversible, so the purge must not be
    able to reach it — not even for an album that has left the library."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.DOWNLOADED),)))
    providers = [
        FakeProvider(
            source,
            default=EnrichmentResult(opinions={"release_type": "ep"}),
        )
        for source in (EnrichmentSource.DEEZER, EnrichmentSource.MUSICBRAINZ)
    ]

    asyncio.run(
        build(factory, providers, enrichment_sources="deezer,musicbrainz").tick()
    )

    async def read() -> tuple[str, Any]:
        async with factory() as session:
            album = await session.get(Album, OPAQUE_ID)
            return album.release_type, await session.get(AlbumMetadata, OPAQUE_ID)

    release_type, meta = asyncio.run(read())
    assert release_type == "ep", "a majority overruled Qobuz"
    assert meta.qobuz_release_type == "album", "and what Qobuz said was kept"

    # The album leaves the library and the nightly purge runs.
    asyncio.run(set_status(factory, OPAQUE_ID, AlbumStatus.WANTED))

    async def purge_and_read() -> tuple[dict[Any, Any], Any]:
        async with factory() as session:
            await purge_out_of_scope_enrichment(session)
        rows = await states(factory)
        async with factory() as session:
            return rows, await session.get(AlbumMetadata, OPAQUE_ID)

    rows, meta_after = asyncio.run(purge_and_read())

    assert rows == {}, "the work list went"
    assert meta_after.qobuz_release_type == "album", "the knowledge stayed"


def test_the_purge_leaves_the_metadata_tables_alone(factory: Any) -> None:
    """It deletes work, never knowledge — including for entities that are long
    out of scope, whose artwork, ids and biography are all still true."""
    asyncio.run(seed(factory, ((OPAQUE_ID, AlbumStatus.WANTED),)))

    async def plant() -> None:
        async with factory() as session:
            session.add(ArtistMetadata(artist_id="a1", mb_artist_mbid=GROUP))
            session.add(
                AlbumMetadata(
                    album_id=OPAQUE_ID,
                    mb_release_group_mbid=GROUP,
                    barcode="0804879535645",
                )
            )
            session.add(
                EnrichmentState(
                    entity_type=EnrichmentEntity.ALBUM,
                    entity_id=OPAQUE_ID,
                    source=EnrichmentSource.DEEZER,
                    state="ok",
                )
            )

    asyncio.run(plant())

    async def purge_and_read() -> tuple[int, Any, Any]:
        async with factory() as session:
            removed = await purge_out_of_scope_enrichment(session)
        async with factory() as session:
            return (
                removed,
                await session.get(AlbumMetadata, OPAQUE_ID),
                await session.get(ArtistMetadata, "a1"),
            )

    removed, album_meta, artist_meta = asyncio.run(purge_and_read())

    assert removed == 1
    assert album_meta.mb_release_group_mbid == GROUP
    assert album_meta.barcode == "0804879535645"
    assert artist_meta.mb_artist_mbid == GROUP


def test_editions_still_group_across_a_half_enriched_library(factory: Any) -> None:
    """Most of a library has no MusicBrainz match, so grouping has to work for
    the enriched and the unenriched at once: by release group where there is
    one, by normalised title everywhere else."""

    async def build_rows() -> tuple[list[dict[str, Any]], list[Any], str]:
        await seed(factory, ())
        async with factory() as session:
            for album_id, title, version, status in (
                ("std", "Blues Of Desperation", None, AlbumStatus.DOWNLOADED),
                ("dlx", "Blues Of Desperation", "Deluxe", AlbumStatus.DOWNLOADED),
                ("db1", "Dust Bowl", None, AlbumStatus.WANTED),
                ("db2", "Dust  Bowl", "Japan", AlbumStatus.WANTED),
            ):
                session.add(
                    Album(
                        id=album_id,
                        artist_id="a1",
                        title=title,
                        version=version,
                        tracks_count=11,
                        status=status,
                    )
                )
            # Only the two on disk were ever enriched.
            for album_id in ("std", "dlx"):
                session.add(
                    AlbumMetadata(album_id=album_id, mb_release_group_mbid=GROUP)
                )
            # And the state rows for the rest are purged out from under them.
            await session.flush()
            await purge_out_of_scope_enrichment(session)

        async with factory() as session:
            albums = (
                (await session.execute(select(Album).order_by(Album.id)))
                .unique()
                .scalars()
                .all()
            )
            meta = {
                str(row.album_id): row
                for row in (await session.execute(select(AlbumMetadata)))
                .scalars()
                .all()
            }
            outs = [
                deps.album_to_out(album, meta=meta.get(str(album.id)))
                for album in albums
            ]
            editions, title = await deps.list_release_group(session, GROUP)
            return deps.group_editions(outs), editions, title

    groups, editions, title = asyncio.run(build_rows())
    by_key = {group["key"]: group for group in groups}

    # The identified pair groups on the release group; the unidentified pair
    # still groups, on the normalised title — that fallback is what makes
    # grouping work at all for most of a real library.
    assert sorted(by_key) == sorted([GROUP, dedupe_key("Dust Bowl")])
    assert by_key[GROUP]["total"] == 2
    assert by_key[dedupe_key("Dust Bowl")]["total"] == 2
    # And the release-group page still finds both editions after the purge.
    assert sorted(album.id for album in editions) == ["dlx", "std"]
    assert title == "Blues Of Desperation"


def test_an_album_with_no_metadata_row_still_renders(factory: Any) -> None:
    """Most of the catalogue now has none, and a read model that needed one
    would turn narrowing the scope into a 500 on the artist page."""

    async def render() -> Any:
        await seed(factory, ((NUMERIC_LOOKING_ID, AlbumStatus.WANTED),))
        async with factory() as session:
            album = await session.get(Album, NUMERIC_LOOKING_ID)
            return deps.album_to_out(album)

    out = asyncio.run(render())

    assert out.id == NUMERIC_LOOKING_ID
    assert isinstance(out.id, str)
    assert out.enriched is False
    assert out.release_group_key == (
        dedupe_key(f"Record {NUMERIC_LOOKING_ID}") or NUMERIC_LOOKING_ID
    )
    assert out.complete is None, "not on disk: unknown, not empty"


# ---------------------------------------------------------------------------
# The coverage readout
# ---------------------------------------------------------------------------
def test_the_coverage_denominator_is_the_library_not_the_catalogue(
    factory: Any,
) -> None:
    """"32 identified" against 3313 albums reports 1% coverage of a job that is
    in fact finished. The catalogue totals stay beside it so the gap is
    explicable rather than alarming."""
    asyncio.run(
        seed(
            factory,
            (
                (OPAQUE_ID, AlbumStatus.DOWNLOADED),
                (NUMERIC_LOOKING_ID, AlbumStatus.WANTED),
                ("wxl78pvfqlm3b", AlbumStatus.SKIPPED),
            ),
        )
    )

    async def read() -> dict[str, int]:
        async with factory() as session:
            session.add(Artist(id="a2", name="Nobody", albums_count=0))
            await session.flush()
            return await enrichment_scope_counts(session)

    counts = asyncio.run(read())

    assert counts == {
        "albums": 1,
        "artists": 1,
        "catalogue_albums": 3,
        "catalogue_artists": 2,
    }


def test_the_state_counts_ignore_rows_that_are_out_of_scope(factory: Any) -> None:
    """A stale row that the next purge will delete must not sit in the coverage
    table looking like outstanding work."""
    asyncio.run(_seed_mixed_rows(factory))
    enricher = build(
        factory,
        [
            FakeProvider(EnrichmentSource.DEEZER),
            FakeProvider(EnrichmentSource.MUSICBRAINZ),
        ],
        enrichment_sources="deezer,musicbrainz",
    )

    async def read() -> dict[str, Any]:
        async with factory() as session:
            return await enricher.status(session)

    status = asyncio.run(read())

    assert status["states"] == {"musicbrainz": {"ambiguous": 1, "ok": 1}}
    assert status["scope"]["albums"] == 1
    assert status["scope"]["catalogue_albums"] == 2


def test_the_status_endpoint_carries_the_honest_denominator(
    factory: Any, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Through the response model the page renders from, not just the enricher:
    a count the schema drops is a count the screen cannot show."""
    asyncio.run(_seed_mixed_rows(factory))
    enricher = build(factory, [FakeProvider(EnrichmentSource.DEEZER)])
    monkeypatch.setattr(routes_api, "get_enricher", lambda: enricher)

    async def read() -> Any:
        async with factory() as session:
            return await routes_api.enrichment_status(session)

    payload = asyncio.run(read())

    assert payload.scope["albums"] == 1
    assert payload.scope["catalogue_albums"] == 2
    assert payload.review_total == 1, "the one ambiguous row that is on disk"


# ---------------------------------------------------------------------------
# A throwaway library on disk, for the scanner hook
# ---------------------------------------------------------------------------
def write_flac(
    path: Path,
    *,
    sample_rate: int = 44100,
    channels: int = 2,
    bits: int = 16,
    seconds: int = 200,
    **tags: str,
) -> Path:
    """Write a minimal but valid FLAC file with Vorbis comments.

    Only a STREAMINFO block is emitted, which is enough for ``mutagen`` to
    report the stream properties and to attach tags — the same helper
    ``tests/test_library_scan.py`` uses, kept local so neither file has to
    import the other.
    """
    path.parent.mkdir(parents=True, exist_ok=True)

    bitstream = 0
    width = 0

    def put(value: int, bits_used: int) -> None:
        nonlocal bitstream, width
        bitstream = (bitstream << bits_used) | (value & ((1 << bits_used) - 1))
        width += bits_used

    put(4096, 16)  # minimum block size
    put(4096, 16)  # maximum block size
    put(0, 24)  # minimum frame size (unknown)
    put(0, 24)  # maximum frame size (unknown)
    put(sample_rate, 20)
    put(channels - 1, 3)
    put(bits - 1, 5)
    put(sample_rate * seconds, 36)  # total samples
    streaminfo = bitstream.to_bytes(width // 8, "big") + b"\x00" * 16  # + empty MD5

    header = bytes([0x80]) + len(streaminfo).to_bytes(3, "big")  # last block, type 0
    path.write_bytes(b"fLaC" + header + streaminfo)

    if tags:
        audio = mutagen.File(str(path), easy=True)
        for key, value in tags.items():
            audio[key] = [str(value)]
        audio.save()
    return path


def write_album(
    root: Path, artist: str, folder: str, *, tracks: int = 3, year: str = "2018"
) -> Path:
    """One album folder of tagged FLAC files, as a real tagger would write it."""
    directory = root / artist / folder
    for number in range(1, tracks + 1):
        write_flac(
            directory / f"{number:02d} - Track {number}.flac",
            album=folder.split(" (")[0],
            albumartist=artist,
            artist=artist,
            title=f"Track {number}",
            tracknumber=str(number),
            discnumber="1",
            tracktotal=str(tracks),
            date=year,
        )
    return directory
