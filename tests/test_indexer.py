"""Unit tests for :mod:`app.core.indexer`.

The Qobuz client is replaced by a tiny in-memory fake, and the database is a
private in-memory SQLite (a ``StaticPool`` so every session sees the same
connection).  Nothing here touches the network, the real ``data/`` directory or
the module-level engine in :mod:`app.db`.

The tests drive ``asyncio.run`` themselves, so no async pytest plugin is needed.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from datetime import datetime, timedelta, timezone
from typing import Any, AsyncIterator, Iterable, Sequence

import pytest
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.config import Settings
from app.core.indexer import Indexer, dedupe_key, edition_rank
from app.core.queue import QueueWorker
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    MonitorMode,
    QueueItem,
    QueueState,
)


# ---------------------------------------------------------------------------
# Fixtures / helpers
# ---------------------------------------------------------------------------
class FakeQobuzClient:
    """Minimal stand-in for :class:`app.qobuz.client.QobuzClient`.

    Only the two methods the indexer uses are implemented.  ``calls`` records
    which artists were talked to, which is how the "one artist per tick" test
    proves the indexer stayed sequential.
    """

    def __init__(
        self,
        artists: dict[str, dict[str, Any]] | None = None,
        albums: dict[str, list[dict[str, Any]]] | None = None,
    ) -> None:
        self.artists = artists or {}
        self.albums = albums or {}
        self.calls: list[tuple[str, str]] = []

    async def get_artist(
        self, artist_id: str | int, with_albums: bool = True, limit: int = 100, offset: int = 0
    ) -> dict[str, Any]:
        self.calls.append(("get_artist", str(artist_id)))
        return self.artists.get(
            str(artist_id), {"id": str(artist_id), "name": f"Artist {artist_id}"}
        )

    async def iter_artist_albums(
        self, artist_id: str | int, release_types: Iterable[str] | None = None
    ) -> AsyncIterator[dict[str, Any]]:
        self.calls.append(("iter_artist_albums", str(artist_id)))
        for raw in self.albums.get(str(artist_id), []):
            yield raw

    def artists_indexed(self) -> list[str]:
        """Ids passed to :meth:`iter_artist_albums`, in order."""
        return [artist_id for call, artist_id in self.calls if call == "iter_artist_albums"]


def make_settings(**overrides: Any) -> Settings:
    """Settings with jitter disabled so the pacing maths is exact."""
    values: dict[str, Any] = {
        "indexer_artist_interval": 300,
        "indexer_full_sweep_hours": 6,
        "indexer_jitter_pct": 0.0,
        "indexer_enabled": True,
        "default_monitor_mode": "all",
        "default_quality_profile": "default",
        "default_accepted_release_types": "album,ep",
        "qobuz_favorite_sync": False,
        # Downloading is opt-in in production (AUTO_DOWNLOAD defaults to False).
        # The pre-existing tests below exercise the automatic path, so they turn
        # it on explicitly; the opt-in behaviour has its own tests.
        "auto_download": True,
    }
    values.update(overrides)
    return Settings(**values)


def album_payload(
    album_id: str,
    title: str,
    *,
    released: str = "2015-06-01",
    tracks_count: int = 11,
    version: str | None = None,
    hires: bool = False,
    bit_depth: int = 16,
    sampling_rate: float = 44.1,
    media_count: int = 1,
    release_type: str = "album",
    artist_id: str = "100",
    artist_name: str = "Test Artist",
) -> dict[str, Any]:
    """Build an album payload shaped like a real Qobuz response."""
    return {
        "id": album_id,
        "title": title,
        "version": version,
        "release_date_original": released,
        "tracks_count": tracks_count,
        "media_count": media_count,
        "hires": hires,
        "maximum_bit_depth": bit_depth,
        "maximum_sampling_rate": sampling_rate,
        "release_type": release_type,
        "label": {"name": "Test Records"},
        "genre": {"name": "Rock"},
        "image": {"large": f"https://example.invalid/{album_id}.jpg"},
        "artist": {"id": artist_id, "name": artist_name},
    }


#: Every engine these tests build, kept alive on purpose until the process ends.
#:
#: ``StaticPool`` holds one aiosqlite connection, and that connection's worker
#: thread belongs to the ``asyncio.run`` loop it was opened on — a loop that is
#: closed by the time the test returns. Letting the engine be garbage-collected
#: later makes aiosqlite try to close it through that dead loop, which surfaces
#: as an intermittent "Event loop is closed" thread exception attributed to
#: whichever unlucky test happened to trigger the collection. Disposing here is
#: not an option for the same reason, so hold a reference instead: an in-memory
#: database costs nothing once the test that used it is over.
_ENGINES: list[Any] = []


async def make_sessionmaker() -> async_sessionmaker:
    """A fresh in-memory database with the full schema."""
    engine = create_async_engine(
        "sqlite+aiosqlite:///:memory:",
        poolclass=StaticPool,
        connect_args={"check_same_thread": False},
    )
    _ENGINES.append(engine)
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)
    return async_sessionmaker(engine, expire_on_commit=False, autoflush=False)


def make_session_factory(session_maker: async_sessionmaker) -> Any:
    """A ``session_scope``-alike bound to the test's in-memory database."""

    @asynccontextmanager
    async def factory() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    return factory


async def add_artist_row(
    session_maker: async_sessionmaker,
    artist_id: str,
    *,
    name: str = "Test Artist",
    monitor_mode: MonitorMode = MonitorMode.ALL,
    accepted: Sequence[str] = ("album", "ep"),
    added_at: datetime | None = None,
    last_checked_at: datetime | None = None,
    monitored: bool = True,
) -> None:
    """Insert an artist directly, bypassing the Qobuz round-trip."""
    async with session_maker() as session:
        artist = Artist(
            id=artist_id,
            name=name,
            monitored=monitored,
            monitor_mode=monitor_mode,
            added_at=added_at or datetime.now(timezone.utc),
            last_checked_at=last_checked_at,
        )
        artist.set_accepted_release_types(list(accepted))
        session.add(artist)
        await session.commit()


async def album_statuses(session_maker: async_sessionmaker) -> dict[str, AlbumStatus]:
    """Map of album id -> status."""
    async with session_maker() as session:
        rows = await session.execute(select(Album))
        return {album.id: album.status for album in rows.scalars().all()}


async def queued_album_ids(session_maker: async_sessionmaker) -> set[str]:
    """Album ids that have a pending queue item."""
    async with session_maker() as session:
        rows = await session.execute(
            select(QueueItem.album_id).where(QueueItem.state == QueueState.PENDING)
        )
        return {row[0] for row in rows.all()}


# ---------------------------------------------------------------------------
# New releases become wanted and queued
# ---------------------------------------------------------------------------
def test_new_release_becomes_wanted_and_queued() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={"100": [album_payload("aaa111", "First Album", released="2019-01-01")]}
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100")

        async with session_maker() as session:
            first = await indexer.check_artist(session, "100")

        assert first.releases_seen == 1
        assert first.new_albums == 1
        assert first.wanted == 1
        assert first.queued == 1
        assert (await album_statuses(session_maker))["aaa111"] is AlbumStatus.WANTED
        assert await queued_album_ids(session_maker) == {"aaa111"}

        # A later check sees a brand-new release and queues only that one.
        client.albums["100"].append(
            album_payload("bbb222", "Second Album", released="2024-03-03")
        )
        async with session_maker() as session:
            second = await indexer.check_artist(session, "100")

        assert second.releases_seen == 2
        assert second.new_albums == 1
        assert second.wanted_album_ids == ["bbb222"]
        assert second.queued == 1
        assert await queued_album_ids(session_maker) == {"aaa111", "bbb222"}

        # Re-running with nothing new must not duplicate queue items.
        async with session_maker() as session:
            third = await indexer.check_artist(session, "100")
            total_items = (
                await session.execute(select(func.count()).select_from(QueueItem))
            ).scalar_one()
        assert third.new_albums == 0
        assert third.queued == 0
        assert total_items == 2

    asyncio.run(main())


def test_album_ids_stay_strings() -> None:
    """Qobuz album ids are non-numeric and must survive untouched."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={"100": [album_payload("0884977859300", "Leading Zero Album")]}
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100")

        async with session_maker() as session:
            await indexer.check_artist(session, "100")

        assert "0884977859300" in await album_statuses(session_maker)

    asyncio.run(main())


# ---------------------------------------------------------------------------
# monitor modes
# ---------------------------------------------------------------------------
def test_future_only_mode_skips_back_catalogue() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        added_at = datetime(2024, 1, 1, tzinfo=timezone.utc)
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload("old111", "Old Album", released="2010-04-04"),
                    album_payload("new222", "Brand New Album", released="2024-06-06"),
                    album_payload("undated", "Undated Album", released=""),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(
            session_maker, "100", monitor_mode=MonitorMode.FUTURE, added_at=added_at
        )

        async with session_maker() as session:
            result = await indexer.check_artist(session, "100")

        statuses = await album_statuses(session_maker)
        assert statuses["old111"] is AlbumStatus.SKIPPED
        assert statuses["new222"] is AlbumStatus.WANTED
        # An unknown release date cannot be proven to be new, so it is skipped.
        assert statuses["undated"] is AlbumStatus.SKIPPED
        assert result.wanted_album_ids == ["new222"]
        assert await queued_album_ids(session_maker) == {"new222"}

    asyncio.run(main())


def test_monitor_mode_none_wants_nothing() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(albums={"100": [album_payload("aaa111", "An Album")]})
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100", monitor_mode=MonitorMode.NONE)

        async with session_maker() as session:
            result = await indexer.check_artist(session, "100")

        assert result.new_albums == 1
        assert result.wanted == 0
        assert result.queued == 0
        assert (await album_statuses(session_maker))["aaa111"] is AlbumStatus.SKIPPED

    asyncio.run(main())


def test_release_types_outside_the_filter_are_skipped() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload("alb111", "Proper Album", release_type="album"),
                    album_payload(
                        "liv222", "Live In Tokyo", release_type="live", tracks_count=14
                    ),
                    album_payload(
                        "sng333", "A Single", release_type="epSingle", tracks_count=2
                    ),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100", accepted=("album",))

        async with session_maker() as session:
            await indexer.check_artist(session, "100")

        statuses = await album_statuses(session_maker)
        assert statuses["alb111"] is AlbumStatus.WANTED
        assert statuses["liv222"] is AlbumStatus.SKIPPED
        assert statuses["sng333"] is AlbumStatus.SKIPPED

    asyncio.run(main())


# ---------------------------------------------------------------------------
# Edition de-duplication
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    ("title", "expected"),
    [
        ("Rumours", "rumours"),
        ("Rumours (Deluxe Edition)", "rumours"),
        ("Rumours [2013 Remastered]", "rumours"),
        ("Rumours - Deluxe Edition", "rumours"),
        ("Sign o' the Times", "sign o the times"),
        ("Different Album", "different album"),
    ],
)
def test_dedupe_key_normalises_editions(title: str, expected: str) -> None:
    assert dedupe_key(title) == expected


def test_version_dedupe_prefers_hires_and_most_tracks() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload("std111", "Great Record", tracks_count=10),
                    album_payload(
                        "dlx222",
                        "Great Record",
                        version="Deluxe Edition",
                        tracks_count=16,
                        hires=True,
                        bit_depth=24,
                        sampling_rate=96.0,
                    ),
                    album_payload(
                        "rem333",
                        "Great Record (2011 Remaster)",
                        version="2011 Remaster",
                        tracks_count=10,
                    ),
                    album_payload("oth444", "Another Record", tracks_count=9),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100")

        async with session_maker() as session:
            result = await indexer.check_artist(session, "100")

        statuses = await album_statuses(session_maker)
        assert statuses["dlx222"] is AlbumStatus.WANTED, "hi-res/most-tracks edition wins"
        assert statuses["std111"] is AlbumStatus.SKIPPED
        assert statuses["rem333"] is AlbumStatus.SKIPPED
        assert statuses["oth444"] is AlbumStatus.WANTED, "a different release is untouched"

        assert set(result.deduped_album_ids) == {"std111", "rem333"}
        assert await queued_album_ids(session_maker) == {"dlx222", "oth444"}

    asyncio.run(main())


def test_dedupe_never_demotes_a_downloaded_edition() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={"100": [album_payload("std111", "Great Record", tracks_count=10)]}
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100")

        async with session_maker() as session:
            await indexer.check_artist(session, "100")

        # Pretend the standard edition finished downloading.
        async with session_maker() as session:
            album = await session.get(Album, "std111")
            assert album is not None
            album.status = AlbumStatus.DOWNLOADED
            await session.commit()

        # Now a fancier edition shows up.
        client.albums["100"].append(
            album_payload(
                "dlx222",
                "Great Record",
                version="Deluxe Edition",
                tracks_count=18,
                hires=True,
                bit_depth=24,
            )
        )
        async with session_maker() as session:
            await indexer.check_artist(session, "100")

        statuses = await album_statuses(session_maker)
        assert statuses["std111"] is AlbumStatus.DOWNLOADED
        assert statuses["dlx222"] is AlbumStatus.SKIPPED
        assert "dlx222" not in await queued_album_ids(session_maker)

    asyncio.run(main())


# ---------------------------------------------------------------------------
# Pacing
# ---------------------------------------------------------------------------
def test_period_scales_with_artist_count() -> None:
    """Few artists -> sweep-driven period; many artists -> the interval floor."""
    indexer = Indexer(
        FakeQobuzClient(),
        settings=make_settings(indexer_artist_interval=300, indexer_full_sweep_hours=6),
    )

    assert indexer.artist_period_seconds(1) == pytest.approx(21600.0)
    assert indexer.artist_period_seconds(4) == pytest.approx(5400.0)
    # The period never drops below INDEXER_ARTIST_INTERVAL...
    assert indexer.artist_period_seconds(1000) == pytest.approx(300.0)
    # ...and it decreases monotonically as the library grows.
    periods = [indexer.artist_period_seconds(n) for n in (1, 2, 10, 100, 1000)]
    assert periods == sorted(periods, reverse=True)

    # A big library therefore takes *longer* to sweep, not shorter: one artist
    # per tick means the full cycle grows without bound.
    assert indexer.full_cycle_seconds(1) == pytest.approx(21600.0)
    assert indexer.full_cycle_seconds(100) == pytest.approx(30000.0)
    assert indexer.full_cycle_seconds(1000) == pytest.approx(300000.0)

    slower = Indexer(FakeQobuzClient(), settings=make_settings(indexer_full_sweep_hours=24))
    assert slower.artist_period_seconds(1) == pytest.approx(86400.0)


def test_jitter_stays_within_the_configured_band() -> None:
    settings = make_settings(indexer_jitter_pct=0.2)
    lowest = Indexer(FakeQobuzClient(), settings=settings, random_func=lambda: 0.0)
    highest = Indexer(FakeQobuzClient(), settings=settings, random_func=lambda: 1.0)
    middle = Indexer(FakeQobuzClient(), settings=settings, random_func=lambda: 0.5)

    assert lowest.jittered(1000.0) == pytest.approx(800.0)
    assert highest.jittered(1000.0) == pytest.approx(1200.0)
    assert middle.jittered(1000.0) == pytest.approx(1000.0)
    # Jitter can be switched off entirely.
    assert Indexer(FakeQobuzClient(), settings=make_settings()).jittered(1000.0) == 1000.0


# ---------------------------------------------------------------------------
# tick()
# ---------------------------------------------------------------------------
def test_tick_touches_exactly_one_artist() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        now = datetime.now(timezone.utc)
        client = FakeQobuzClient(
            albums={
                "100": [album_payload("a100", "Album A", artist_id="100")],
                "200": [album_payload("a200", "Album B", artist_id="200")],
                "300": [album_payload("a300", "Album C", artist_id="300")],
            }
        )
        indexer = Indexer(client, settings=make_settings())

        # Three monitored artists, all overdue; 300 is the stalest.
        await add_artist_row(
            session_maker, "100", name="A", last_checked_at=now - timedelta(hours=10)
        )
        await add_artist_row(
            session_maker, "200", name="B", last_checked_at=now - timedelta(hours=20)
        )
        await add_artist_row(
            session_maker, "300", name="C", last_checked_at=now - timedelta(hours=30)
        )

        async with session_maker() as session:
            result = await indexer.tick(session)

        assert result is not None
        assert result.artist_id == "300"
        assert client.artists_indexed() == ["300"], "exactly one artist per tick"

        # The next tick moves on to the next-stalest artist.
        async with session_maker() as session:
            second = await indexer.tick(session)
        assert second is not None and second.artist_id == "200"
        assert client.artists_indexed() == ["300", "200"]

        # last_checked_at was persisted for both.
        async with session_maker() as session:
            rows = await session.execute(select(Artist).order_by(Artist.id))
            checked = {a.id: a.last_checked_at for a in rows.scalars().all()}
        assert checked["300"] is not None and checked["200"] is not None

    asyncio.run(main())


def test_tick_skips_artists_that_are_not_due_yet() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        now = datetime.now(timezone.utc)
        client = FakeQobuzClient(albums={"100": [album_payload("a100", "Album A")]})
        # One artist: period is the full 6h sweep, so a check 10 minutes ago is
        # nowhere near due.
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(
            session_maker, "100", last_checked_at=now - timedelta(minutes=10)
        )

        async with session_maker() as session:
            assert await indexer.tick(session) is None
        assert client.artists_indexed() == []

        # An artist that has never been checked is due immediately.
        await add_artist_row(session_maker, "200", name="Never Checked")
        client.albums["200"] = [album_payload("a200", "Album B", artist_id="200")]
        async with session_maker() as session:
            result = await indexer.tick(session)
        assert result is not None and result.artist_id == "200"

    asyncio.run(main())


def test_tick_ignores_unmonitored_artists_and_respects_the_kill_switch() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(albums={"100": [album_payload("a100", "Album A")]})
        await add_artist_row(session_maker, "100", monitored=False)

        indexer = Indexer(client, settings=make_settings())
        async with session_maker() as session:
            assert await indexer.tick(session) is None

        disabled = Indexer(client, settings=make_settings(indexer_enabled=False))
        await add_artist_row(session_maker, "200", name="Monitored")
        async with session_maker() as session:
            assert await disabled.tick(session) is None
        assert client.artists_indexed() == []

    asyncio.run(main())


def test_next_due_reflects_the_computed_period() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        indexer = Indexer(FakeQobuzClient(), settings=make_settings())

        async with session_maker() as session:
            assert await indexer.next_due(session) is None  # nothing monitored

        checked_at = datetime.now(timezone.utc) - timedelta(hours=1)
        await add_artist_row(session_maker, "100", last_checked_at=checked_at)
        async with session_maker() as session:
            due = await indexer.next_due(session)
        assert due is not None
        # 6h sweep / 1 artist = 6h after the last check.
        assert abs((due - (checked_at + timedelta(hours=6))).total_seconds()) < 2

        # A never-checked artist makes the answer "now".
        await add_artist_row(session_maker, "200", name="Fresh")
        async with session_maker() as session:
            due = await indexer.next_due(session)
        assert due is not None
        assert abs((due - datetime.now(timezone.utc)).total_seconds()) < 2

    asyncio.run(main())


# ---------------------------------------------------------------------------
# add_artist()
# ---------------------------------------------------------------------------
def test_add_artist_imports_the_back_catalogue() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            artists={
                "100": {
                    "id": "100",
                    "name": "Imported Artist",
                    "albums_count": 2,
                    "slug": "imported-artist",
                    "image": {"large": "https://example.invalid/artist.jpg"},
                }
            },
            albums={
                "100": [
                    album_payload("aaa111", "Debut", released="2001-01-01"),
                    album_payload("bbb222", "Follow Up", released="2004-04-04"),
                ]
            },
        )
        indexer = Indexer(client, settings=make_settings())

        async with session_maker() as session:
            artist = await indexer.add_artist(session, "100")

        assert artist.name == "Imported Artist"
        assert artist.qobuz_slug == "imported-artist"
        assert artist.monitor_mode is MonitorMode.ALL
        assert artist.accepted_release_types_list == ["album", "ep"]
        assert artist.last_checked_at is not None

        statuses = await album_statuses(session_maker)
        assert statuses == {"aaa111": AlbumStatus.WANTED, "bbb222": AlbumStatus.WANTED}
        assert await queued_album_ids(session_maker) == {"aaa111", "bbb222"}

    asyncio.run(main())


def test_add_artist_future_mode_baselines_the_catalogue() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={"100": [album_payload("old111", "Ancient", released="1999-09-09")]}
        )
        indexer = Indexer(client, settings=make_settings())

        async with session_maker() as session:
            await indexer.add_artist(session, "100", monitor_mode=MonitorMode.FUTURE)

        assert (await album_statuses(session_maker))["old111"] is AlbumStatus.SKIPPED
        assert await queued_album_ids(session_maker) == set()

        # Only a genuinely new release is picked up later.
        client.albums["100"].append(
            album_payload("new222", "Comeback", released="2030-01-01")
        )
        async with session_maker() as session:
            await indexer.check_artist(session, "100")
        assert (await album_statuses(session_maker))["new222"] is AlbumStatus.WANTED

    asyncio.run(main())


def test_refresh_all_now_visits_every_monitored_artist_once() -> None:
    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={
                "100": [album_payload("a100", "Album A", artist_id="100")],
                "200": [album_payload("a200", "Album B", artist_id="200")],
            }
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100", name="A")
        await add_artist_row(session_maker, "200", name="B")
        await add_artist_row(session_maker, "300", name="Unmonitored", monitored=False)

        async with session_maker() as session:
            results = await indexer.refresh_all_now(session)

        assert sorted(r.artist_id for r in results) == ["100", "200"]
        assert sorted(client.artists_indexed()) == ["100", "200"]

    asyncio.run(main())


# ---------------------------------------------------------------------------
# Hand-off to the download queue worker
# ---------------------------------------------------------------------------
def test_queue_worker_picks_up_what_the_indexer_queued() -> None:
    """The worker drains indexer-created items highest priority first."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        factory = make_session_factory(session_maker)
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload("aaa111", "First", tracks_count=4),
                    album_payload("bbb222", "Second", tracks_count=5),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100")

        async with session_maker() as session:
            await indexer.check_artist(session, "100")

        downloaded: list[str] = []

        async def fake_download(session: AsyncSession, album_id: str) -> None:
            downloaded.append(album_id)

        worker = QueueWorker(
            download_album=fake_download,
            session_factory=factory,
            settings=make_settings(download_track_delay=0.0),
        )
        assert await worker.run_once() is True
        assert await worker.run_once() is True
        assert await worker.run_once() is False, "queue is drained"

        assert sorted(downloaded) == ["aaa111", "bbb222"]
        assert await album_statuses(session_maker) == {
            "aaa111": AlbumStatus.DOWNLOADED,
            "bbb222": AlbumStatus.DOWNLOADED,
        }
        async with session_maker() as session:
            rows = await session.execute(select(QueueItem))
            assert all(item.state is QueueState.DONE for item in rows.scalars().all())

    asyncio.run(main())


def test_queue_worker_honours_a_returned_failure_result() -> None:
    """AlbumDownloader reports failures by return value, not by raising."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        factory = make_session_factory(session_maker)
        client = FakeQobuzClient(albums={"100": [album_payload("aaa111", "First")]})
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100")
        async with session_maker() as session:
            await indexer.check_artist(session, "100")

        class Result:
            """Duck-typed stand-in for downloader.DownloadResult."""

            status = AlbumStatus.FAILED
            cancelled = False
            errors = ["track 3 is not streamable"]

        async def failing_download(session: AsyncSession, album_id: str) -> Result:
            return Result()

        worker = QueueWorker(
            download_album=failing_download,
            session_factory=factory,
            settings=make_settings(download_max_attempts=1, backoff_jitter=0.0),
        )
        assert await worker.run_once() is True

        async with session_maker() as session:
            item = (await session.execute(select(QueueItem))).scalars().one()
            assert item.state is QueueState.FAILED
            assert "not streamable" in (item.last_error or "")
            assert (await session.get(Album, "aaa111")).status is AlbumStatus.FAILED

    asyncio.run(main())


# ---------------------------------------------------------------------------
# Regression: edition-word matching must respect word boundaries
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    "title",
    [
        "Nocturne (Reprise)",          # "ep" inside "R-ep-rise"
        "Etudes (Prepared Piano)",     # "ep" inside "Pr-ep-ared"
        "Solaris (Deep Space Mix)",    # "ep" inside "De-ep"
        "X (Shepherd Mix)",            # "ep" inside "Sh-ep-herd"
        "Halcyon (Method Actor)",      # "hd" is not a word here either
        "Aurora (Monolith Sessions)",  # "mono" inside "Monolith"
        "Nadir (Supersede)",           # "super" inside "Supersede"
        "Kite (Singled Out)",          # "single" inside "Singled"
        "Vesper (Originally Unreleased)",
        "Tessellate (Completely Rebuilt)",
    ],
)
def test_dedupe_key_keeps_non_edition_qualifiers(title: str) -> None:
    """A parenthesised qualifier that is not an edition must not be stripped.

    Before the ``\\b`` anchors were added, short alternatives such as ``ep``
    matched as bare substrings, so 'Nocturne' and 'Nocturne (Reprise)' collapsed
    into one dedupe group and one of two genuinely different releases was
    silently skipped.
    """
    assert dedupe_key(title) != dedupe_key(title.split(" (")[0])


@pytest.mark.parametrize(
    ("title", "expected"),
    [
        ("Rumours (Deluxe Edition)", "rumours"),
        ("Rumours (Mono Version)", "rumours"),
        ("Rumours (EP)", "rumours"),
        ("Rumours [24-bit Remaster]", "rumours"),
        ("Rumours (Hi-Res)", "rumours"),
        ("Rumours - Super Deluxe", "rumours"),
    ],
)
def test_dedupe_key_still_strips_real_editions(title: str, expected: str) -> None:
    assert dedupe_key(title) == expected


def test_reprise_release_is_not_deduped_against_the_original() -> None:
    """Two distinct releases must both stay wanted and both get queued."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload("noc111", "Nocturne", tracks_count=10),
                    album_payload(
                        "rep222",
                        "Nocturne (Reprise)",
                        tracks_count=3,
                        hires=True,
                        bit_depth=24,
                        sampling_rate=96.0,
                    ),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100")

        async with session_maker() as session:
            result = await indexer.check_artist(session, "100")

        statuses = await album_statuses(session_maker)
        assert statuses["noc111"] is AlbumStatus.WANTED
        assert statuses["rep222"] is AlbumStatus.WANTED
        assert result.deduped_album_ids == []
        assert await queued_album_ids(session_maker) == {"noc111", "rep222"}

        # And it stays that way on a re-scan.
        async with session_maker() as session:
            await indexer.check_artist(session, "100")
        assert (await album_statuses(session_maker))["noc111"] is AlbumStatus.WANTED

    asyncio.run(main())


# ---------------------------------------------------------------------------
# Regression: a skipped edition must never win the de-duplication contest
# ---------------------------------------------------------------------------
def test_dedupe_ignores_editions_the_artist_does_not_accept() -> None:
    """A hi-res promo single of a non-accepted type must not skip the album."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload("aur111", "Aurora", tracks_count=12),
                    album_payload(
                        "aur222",
                        "Aurora (Single Edit)",
                        release_type="epSingle",
                        tracks_count=1,
                        hires=True,
                        bit_depth=24,
                        sampling_rate=96.0,
                    ),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100", accepted=("album",))

        async with session_maker() as session:
            await indexer.check_artist(session, "100")

        statuses = await album_statuses(session_maker)
        assert statuses["aur222"] is AlbumStatus.SKIPPED, "type is not accepted"
        assert statuses["aur111"] is AlbumStatus.WANTED, (
            "the only eligible edition must not be demoted by a skipped one"
        )
        assert await queued_album_ids(session_maker) == {"aur111"}

    asyncio.run(main())


def test_dedupe_all_members_skipped_changes_nothing() -> None:
    """When no edition is eligible the group is left exactly as it is."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload(
                        "sng111", "Halo", release_type="epSingle", tracks_count=1
                    ),
                    album_payload(
                        "sng222",
                        "Halo (Deluxe Edition)",
                        release_type="epSingle",
                        tracks_count=2,
                    ),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings())
        await add_artist_row(session_maker, "100", accepted=("album",))

        async with session_maker() as session:
            result = await indexer.check_artist(session, "100")

        statuses = await album_statuses(session_maker)
        assert statuses["sng111"] is AlbumStatus.SKIPPED
        assert statuses["sng222"] is AlbumStatus.SKIPPED
        assert result.deduped_album_ids == []

    asyncio.run(main())


def test_edition_rank_prefers_the_fuller_release_over_a_hires_single() -> None:
    """Track count outranks hi-res so a 1-track promo cannot win."""
    full = Album(id="a", artist_id="100", title="Aurora", tracks_count=12, hires=False)
    promo = Album(id="b", artist_id="100", title="Aurora", tracks_count=1, hires=True)
    assert max((full, promo), key=edition_rank) is full


# ---------------------------------------------------------------------------
# Automatic downloading is opt-in (AUTO_DOWNLOAD, default False)
# ---------------------------------------------------------------------------
def test_auto_download_off_marks_wanted_but_queues_nothing() -> None:
    """The default: releases are discovered, but no download starts by itself."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload("aaa111", "First Album", released="2019-01-01"),
                    album_payload("bbb222", "Second Album", released="2024-03-03"),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings(auto_download=False))
        await add_artist_row(session_maker, "100")

        async with session_maker() as session:
            result = await indexer.check_artist(session, "100")

        # Indexing still did its job...
        assert result.releases_seen == 2
        assert result.new_albums == 2
        assert result.wanted == 2
        statuses = await album_statuses(session_maker)
        assert statuses["aaa111"] is AlbumStatus.WANTED
        assert statuses["bbb222"] is AlbumStatus.WANTED

        # ...but nothing was queued, so the downloader has nothing to do.
        assert result.queued == 0
        assert await queued_album_ids(session_maker) == set()

    asyncio.run(main())


def test_auto_download_defaults_to_off_in_real_settings() -> None:
    """A bare Settings() must not download on its own — this is the safety net."""
    assert Settings().auto_download is False


def test_queue_wanted_starts_downloads_on_request() -> None:
    """The explicit opt-in path queues everything wanted, ignoring the setting."""

    async def main() -> None:
        session_maker = await make_sessionmaker()
        client = FakeQobuzClient(
            albums={
                "100": [
                    album_payload("aaa111", "First Album", released="2019-01-01"),
                    album_payload("bbb222", "Second Album", released="2024-03-03"),
                ]
            }
        )
        indexer = Indexer(client, settings=make_settings(auto_download=False))
        await add_artist_row(session_maker, "100")

        async with session_maker() as session:
            await indexer.check_artist(session, "100")
        assert await queued_album_ids(session_maker) == set()

        async with session_maker() as session:
            artist = await session.get(Artist, "100")
            queued = await indexer.queue_wanted(session, artist)

        assert queued == 2
        assert await queued_album_ids(session_maker) == {"aaa111", "bbb222"}

        # Asking twice must not create duplicate queue items.
        async with session_maker() as session:
            artist = await session.get(Artist, "100")
            again = await indexer.queue_wanted(session, artist)
            total = (
                await session.execute(select(func.count()).select_from(QueueItem))
            ).scalar_one()
        assert again == 0
        assert total == 2

    asyncio.run(main())
