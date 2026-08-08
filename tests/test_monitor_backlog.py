"""What changing an artist's monitoring does to the releases already on file.

Turning an artist's monitoring off used to change nothing about the backlog.
Every release it had already marked ``wanted`` stayed wanted for good, and
nothing was ever coming to clear them: ``Indexer.claim_artist`` only visits
*monitored* artists, and a visit re-derives the status of newly discovered
albums only. One real library sat at four thousand wanted releases belonging to
artists it had been told to stop watching, and the only way out was to unfollow
them — which throws the artist away too.

So the rule is applied to the rows that exist, in both directions, and most of
what is asserted below is what stayed put: the worker's rows, the disk's rows,
one person's decision about one release, and the duplicate editions the
deduper spent a whole function suppressing.

Demotion is keyed on ``desired_status``, not on the ``monitored`` flag. It was
the flag for as long as unmonitoring was the only way to stop wanting things,
which left the other two settings able to strand a backlog the same way:
``monitor_mode='none'`` marked nothing new but demoted nothing old, and
narrowing ``accepted_release_types`` left every release of a dropped type
wanted for good. The same library reached 4,693 wanted releases through the
first of those, under artists whose mode was already ``none``.
``quality_profile`` is the one setting on the payload that still moves nothing.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; nothing touches ``data/`` or the network.
"""

from __future__ import annotations

import asyncio
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.db import get_session
from app.models import Album, AlbumStatus, Artist, Base, MonitorMode

FOLLOWED = datetime(2024, 1, 1, tzinfo=timezone.utc)


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'backlog.db'}", poolclass=NullPool
    )
    session_maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
        finally:
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with session_maker() as session:
            session.add(
                Artist(
                    id="a1",
                    name="Nils Frahm",
                    monitored=True,
                    monitor_mode=MonitorMode.ALL,
                    accepted_release_types="album,ep",
                    added_at=FOLLOWED,
                )
            )
            session.add(
                Artist(
                    id="a2",
                    name="Alice Coltrane",
                    monitored=True,
                    monitor_mode=MonitorMode.ALL,
                    accepted_release_types="album",
                    added_at=FOLLOWED,
                )
            )
            for album_id, artist_id, title, status, monitored, rtype in (
                # a1's backlog, one row per state that must or must not move.
                ("w1", "a1", "Spaces", AlbumStatus.WANTED, True, "album"),
                ("w2", "a1", "Felt", AlbumStatus.WANTED, True, "album"),
                ("q1", "a1", "Solo", AlbumStatus.QUEUED, True, "album"),
                ("d1", "a1", "Screws", AlbumStatus.DOWNLOADED, True, "album"),
                ("f1", "a1", "Wintermusik", AlbumStatus.FAILED, True, "album"),
                # Ignored by a person from a screen: monitored false, skipped.
                ("i1", "a1", "Encores 1", AlbumStatus.SKIPPED, False, "album"),
                # Skipped because the type is not accepted.
                ("t1", "a1", "Toilet Brushes", AlbumStatus.SKIPPED, True, "single"),
                # Another artist entirely — nothing here may move.
                ("w3", "a2", "Journey in Satchidananda", AlbumStatus.WANTED, True, "album"),
            ):
                session.add(
                    Album(
                        id=album_id,
                        artist_id=artist_id,
                        title=title,
                        status=status,
                        monitored=monitored,
                        release_type=rtype,
                        release_date=date(2015, 6, 1),
                        tracks_count=10,
                    )
                )
            await session.commit()

    asyncio.run(seed())

    main.app.dependency_overrides[get_session] = override_session
    client = TestClient(main.app)
    client.__dict__["_maker"] = session_maker
    try:
        yield client
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        asyncio.run(engine.dispose())


def statuses(client: TestClient) -> dict[str, str]:
    """Every album's status, straight from the database."""
    maker = client.__dict__["_maker"]

    async def read() -> dict[str, str]:
        async with maker() as session:
            rows = (await session.execute(select(Album))).scalars().all()
            return {str(a.id): a.status.value for a in rows}

    return asyncio.run(read())


def add_album(client: TestClient, **fields: Any) -> None:
    maker = client.__dict__["_maker"]

    async def write() -> None:
        async with maker() as session:
            session.add(Album(**fields))
            await session.commit()

    asyncio.run(write())


def bulk(client: TestClient, **fields: Any) -> Any:
    return client.post("/api/artists/bulk", json=fields)


# ------------------------------------------------------------------ turning off
def test_unmonitoring_clears_the_wanted_backlog(client: TestClient) -> None:
    response = bulk(client, artist_ids=["a1"], monitored=False)
    assert response.status_code == 200

    after = statuses(client)
    assert after["w1"] == "skipped"
    assert after["w2"] == "skipped"


def test_unmonitoring_leaves_the_worker_and_the_disk_alone(client: TestClient) -> None:
    bulk(client, artist_ids=["a1"], monitored=False)

    after = statuses(client)
    # The worker owns these two, and `downloaded` is a fact about the disk.
    assert after["q1"] == "queued"
    assert after["d1"] == "downloaded"
    # A failure is a record of an attempt rather than an intention, and it is
    # the one missing state somebody can retry.
    assert after["f1"] == "failed"


def test_unmonitoring_touches_only_the_selection(client: TestClient) -> None:
    bulk(client, artist_ids=["a1"], monitored=False)
    assert statuses(client)["w3"] == "wanted"


def test_the_message_says_how_many_releases_moved(client: TestClient) -> None:
    body = bulk(client, artist_ids=["a1"], monitored=False).json()

    # The biggest thing a bulk unmonitor does is otherwise invisible: the count
    # on the Releases screen drops by thousands and nothing said why.
    assert "2 release(s) are no longer wanted" in body["message"]
    assert body["detail"]["demoted"] == 2
    assert body["detail"]["restored"] == 0


def test_a_settings_change_that_is_not_monitoring_moves_nothing(
    client: TestClient,
) -> None:
    before = statuses(client)
    # `quality_profile` decides how good a copy to fetch, never whether to want
    # one. It is the setting that must move no row at all, and the only one on
    # `ArtistUpdateIn` that `desired_status` does not read.
    response = client.patch("/api/artists/a1", json={"quality_profile": "lossless"})
    assert response.status_code == 200
    assert statuses(client) == before


# ------------------------------------------------- the other two settings
# Unmonitoring was the only way to stop wanting things for as long as demotion
# was keyed on the `monitored` flag, and the other two settings could strand a
# backlog in exactly the way this whole module exists to prevent: switching to
# `monitor_mode='none'` marked nothing new but demoted nothing old, and
# narrowing the accepted types left every release of a dropped type wanted for
# good. One real library reached 4,693 wanted releases that way, every one of
# them under an artist whose mode was already `none`.
def test_monitor_mode_none_clears_the_backlog(client: TestClient) -> None:
    body = bulk(client, artist_ids=["a1"], monitor_mode="none").json()

    after = statuses(client)
    assert after["w1"] == "skipped"
    assert after["w2"] == "skipped"
    assert body["detail"]["demoted"] == 2


def test_monitor_mode_future_demotes_the_back_catalogue(client: TestClient) -> None:
    # a1 was followed in 2024 and these were released in 2015, so future-only
    # no longer wants either of them.
    bulk(client, artist_ids=["a1"], monitor_mode="future")

    after = statuses(client)
    assert after["w1"] == "skipped"
    assert after["w2"] == "skipped"


def test_narrowing_the_accepted_types_demotes_what_it_dropped(
    client: TestClient,
) -> None:
    # a1 accepted album,ep; w1 and w2 are albums. Dropping albums must clear
    # them, and the single that was already skipped stays where it is.
    bulk(client, artist_ids=["a1"], release_types=["ep"], release_types_action="set")

    after = statuses(client)
    assert after["w1"] == "skipped"
    assert after["w2"] == "skipped"
    assert after["t1"] == "skipped"


def test_widening_the_accepted_types_wants_what_it_added(client: TestClient) -> None:
    # The single a1 does not accept, which is the mirror of the test above.
    bulk(
        client,
        artist_ids=["a1"],
        release_types=["single"],
        release_types_action="add",
    )
    assert statuses(client)["t1"] == "wanted"


def test_a_mode_change_leaves_the_worker_and_the_disk_alone(
    client: TestClient,
) -> None:
    bulk(client, artist_ids=["a1"], monitor_mode="none")

    after = statuses(client)
    # Same rule as unmonitoring: these three are not the backlog's to move.
    assert after["q1"] == "queued"
    assert after["d1"] == "downloaded"
    assert after["f1"] == "failed"


def test_a_mode_change_touches_only_the_selection(client: TestClient) -> None:
    bulk(client, artist_ids=["a1"], monitor_mode="none")
    assert statuses(client)["w3"] == "wanted"


def test_the_single_artist_endpoint_moves_the_backlog_too(client: TestClient) -> None:
    # One artist and fifty have to behave alike, so PATCH gets the same pass
    # the bulk edit does.
    response = client.patch("/api/artists/a1", json={"monitor_mode": "none"})
    assert response.status_code == 200

    after = statuses(client)
    assert after["w1"] == "skipped"
    assert after["w2"] == "skipped"


def test_applying_the_same_mode_change_twice_settles(client: TestClient) -> None:
    # Demotion runs before the restore pass reads a group's statuses, so one
    # call reaches a fixed point. The other order leaves a group the demotion
    # emptied for a *second* call to promote, which walks a backlog up and down
    # on every repeated press.
    bulk(client, artist_ids=["a1"], monitor_mode="future")
    once = statuses(client)
    body = bulk(client, artist_ids=["a1"], monitor_mode="future").json()

    assert statuses(client) == once
    assert body["detail"]["demoted"] == 0
    assert body["detail"]["restored"] == 0


def test_unmonitoring_an_already_unmonitored_artist_is_a_no_op(
    client: TestClient,
) -> None:
    bulk(client, artist_ids=["a1"], monitored=False)
    body = bulk(client, artist_ids=["a1"], monitored=False).json()

    assert body["detail"]["demoted"] == 0
    assert statuses(client)["w1"] == "skipped"


# ------------------------------------------------------------------ turning on
def test_monitoring_again_wants_the_backlog_again(client: TestClient) -> None:
    bulk(client, artist_ids=["a1"], monitored=False)
    body = bulk(client, artist_ids=["a1"], monitored=True).json()

    after = statuses(client)
    assert after["w1"] == "wanted"
    assert after["w2"] == "wanted"
    assert "2 release(s) are wanted again" in body["message"]
    assert body["detail"]["restored"] == 2


def test_a_release_a_person_ignored_stays_ignored(client: TestClient) -> None:
    bulk(client, artist_ids=["a1"], monitored=False)
    bulk(client, artist_ids=["a1"], monitored=True)

    # `monitored=False` on the album is a decision somebody made about that one
    # release from a screen. It is not ours to reverse, and it is exactly what
    # tells it apart from a release the cascade demoted.
    assert statuses(client)["i1"] == "skipped"


def test_the_artists_own_rules_still_decide(client: TestClient) -> None:
    bulk(client, artist_ids=["a1"], monitored=False)
    bulk(client, artist_ids=["a1"], monitored=True)

    # A single, for an artist that accepts only albums and EPs.
    assert statuses(client)["t1"] == "skipped"


def test_future_only_does_not_resurrect_the_back_catalogue(client: TestClient) -> None:
    bulk(client, artist_ids=["a1"], monitored=False, monitor_mode="future")
    bulk(client, artist_ids=["a1"], monitored=True)

    after = statuses(client)
    assert after["w1"] == "skipped"
    assert after["w2"] == "skipped"


def test_only_one_edition_of_a_release_comes_back(client: TestClient) -> None:
    # Two editions of one record, both skipped — which is what the deduper
    # leaves behind. Promoting the group would hand somebody two chances to
    # download the wrong one.
    add_album(
        client,
        id="e1",
        artist_id="a2",
        title="Ptah, the El Daoud",
        status=AlbumStatus.SKIPPED,
        monitored=True,
        release_type="album",
        release_date=date(1970, 1, 1),
        tracks_count=5,
    )
    add_album(
        client,
        id="e2",
        artist_id="a2",
        title="Ptah, the El Daoud (Deluxe Edition)",
        status=AlbumStatus.SKIPPED,
        monitored=True,
        release_type="album",
        release_date=date(2010, 1, 1),
        tracks_count=8,
    )
    bulk(client, artist_ids=["a2"], monitored=False)
    bulk(client, artist_ids=["a2"], monitored=True)

    after = statuses(client)
    # The deluxe edition has the higher track count, which is the deduper's
    # first key.
    assert after["e2"] == "wanted"
    assert after["e1"] == "skipped"


def test_a_release_already_on_disk_promotes_no_sibling(client: TestClient) -> None:
    # The copy on disk, and a remaster of it that the deduper skipped. Turning
    # the artist back on must not put the remaster in the backlog: the record
    # is already held, and a second edition beside it is a second chance to
    # download the wrong one.
    add_album(
        client,
        id="h1",
        artist_id="a2",
        title="Turiya and Ramakrishna",
        status=AlbumStatus.DOWNLOADED,
        monitored=True,
        release_type="album",
        release_date=date(1970, 1, 1),
        tracks_count=6,
    )
    add_album(
        client,
        id="h2",
        artist_id="a2",
        title="Turiya and Ramakrishna (Remastered)",
        status=AlbumStatus.SKIPPED,
        monitored=True,
        release_type="album",
        release_date=date(2010, 1, 1),
        tracks_count=12,
    )
    bulk(client, artist_ids=["a2"], monitored=False)
    bulk(client, artist_ids=["a2"], monitored=True)

    after = statuses(client)
    assert after["h1"] == "downloaded"
    assert after["h2"] == "skipped"


# ------------------------------------------------------------- one artist at a time
def test_the_single_artist_patch_behaves_the_same(client: TestClient) -> None:
    assert client.patch("/api/artists/a1", json={"monitored": False}).status_code == 200

    after = statuses(client)
    assert after["w1"] == "skipped"
    assert after["q1"] == "queued"
    assert statuses(client)["w3"] == "wanted"


def test_the_single_artist_patch_restores_too(client: TestClient) -> None:
    client.patch("/api/artists/a1", json={"monitored": False})
    client.patch("/api/artists/a1", json={"monitored": True})

    after = statuses(client)
    assert after["w1"] == "wanted"
    assert after["i1"] == "skipped"


def test_a_patch_that_does_not_touch_monitoring_moves_nothing(
    client: TestClient,
) -> None:
    before = statuses(client)
    client.patch("/api/artists/a1", json={"quality_profile": "hires"})
    assert statuses(client) == before


def test_the_wanted_list_reflects_it(client: TestClient) -> None:
    before = client.get("/api/wanted?limit=100").json()["total"]
    bulk(client, artist_ids=["a1"], monitored=False)
    after = client.get("/api/wanted?limit=100").json()["total"]

    # The screen the user was looking at when they reported this: two of a1's
    # wanted releases gone, a2's untouched, and `failed` still counted as
    # missing by `deps.MISSING_STATUSES`.
    assert before - after == 2


def test_the_rule_is_stated_once(client: TestClient) -> None:
    """`desired_status` is the whole rule, and the indexer's own method is it."""
    from app.core.indexer import desired_status

    artist = Artist(
        id="x",
        name="X",
        monitored=True,
        monitor_mode=MonitorMode.ALL,
        accepted_release_types="album",
        added_at=FOLLOWED,
    )
    album = Album(
        id="y",
        artist_id="x",
        title="Y",
        release_type="album",
        release_date=date(2020, 1, 1),
    )
    assert desired_status(artist, album)[0] is AlbumStatus.WANTED

    artist.monitored = False
    assert desired_status(artist, album) == (
        AlbumStatus.SKIPPED,
        "artist is not monitored",
    )


def test_nothing_is_deleted_and_the_artist_is_still_followed(
    client: TestClient,
) -> None:
    """Unmonitoring is reversible: nothing is removed, only re-stated."""
    bulk(client, artist_ids=["a1"], monitored=False)

    assert len(statuses(client)) == 8
    assert client.get("/api/artists/a1").json()["monitored"] is False


def test_nothing_is_queued_by_any_of_it(client: TestClient) -> None:
    """Downloading stays opt-in: this moves statuses and enqueues nothing."""
    bulk(client, artist_ids=["a1"], monitored=False)
    bulk(client, artist_ids=["a1"], monitored=True)

    assert client.get("/api/queue?limit=100").json()["total"] == 0
