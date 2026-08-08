"""Every filter on every list endpoint, at the JSON layer.

The server-rendered version of this file existed because each filter
``<select>`` rendered ``<option value="">``, so choosing "show everything"
submitted ``?status=`` — an *empty string*, not an absent parameter — and with
the parameters typed as bare enums that was a 422 on a page the user had not
even pressed a button on. The markup is gone; the rule is not, because a
controlled React ``<select>`` submits exactly the same empty string and the SPA
sends its filter object verbatim.

Four properties are asserted for each filter, and each of them has its own way
of failing quietly:

* **blank means unfiltered** — otherwise the "everything" option 422s;
* **a real value narrows** — otherwise the filter is decoration;
* **filters compose** — an ``AND`` that became an ``OR`` returns *more* rows for
  a narrower request, which reads as the filter being ignored;
* **a typo is rejected** — otherwise a misspelled parameter silently shows
  everything, which is indistinguishable from the filter working and finding a
  lot.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; nothing here touches the network.
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
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    Artist,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
    QueueItem,
    QueueState,
)

FRAHM = "720076"
JANSEN = "861312"

WANTED_MONITORED = "uyej1o165e870"   # All Melody, wanted, monitored, album
WANTED_IGNORED = "wxl78pvfqlm3b"     # Spaces, wanted, not monitored, live
FAILED_MONITORED = "aaaa1111bbbb2"   # Felt, failed, monitored, album
ON_DISK = "cccc3333dddd4"            # Solo, downloaded, monitored, ep

#: (id, title, status, monitored, release_type, label)
SEED_ALBUMS = (
    (WANTED_MONITORED, "All Melody", AlbumStatus.WANTED, True, "album", "Erased Tapes"),
    (WANTED_IGNORED, "Spaces", AlbumStatus.WANTED, False, "live", "Erased Tapes"),
    (FAILED_MONITORED, "Felt", AlbumStatus.FAILED, True, "album", "Erased Tapes"),
    (ON_DISK, "Solo", AlbumStatus.DOWNLOADED, True, "ep", "Nonesuch"),
)


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """Two artists, four releases, two queue items, three activity rows."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'filters.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id=FRAHM, name="Nils Frahm", monitored=True))
            session.add(Artist(id=JANSEN, name="Janine Jansen", monitored=False))
            for album_id, title, status, monitored, kind, label in SEED_ALBUMS:
                session.add(
                    Album(
                        id=album_id,
                        artist_id=FRAHM,
                        title=title,
                        status=status,
                        monitored=monitored,
                        release_type=kind,
                        label=label,
                        tracks_count=6,
                        path="/music/Nils Frahm/Solo"
                        if status is AlbumStatus.DOWNLOADED
                        else None,
                    )
                )
            session.add(
                QueueItem(album_id=WANTED_MONITORED, state=QueueState.PENDING)
            )
            session.add(QueueItem(album_id=ON_DISK, state=QueueState.DONE))
            session.add(
                Activity(
                    level=ActivityLevel.INFO,
                    event="queue.add",
                    message="Queued All Melody",
                    artist_id=FRAHM,
                    album_id=WANTED_MONITORED,
                )
            )
            session.add(
                Activity(
                    level=ActivityLevel.WARNING,
                    event="indexer.error",
                    message="Scan of Janine Jansen failed",
                    artist_id=JANSEN,
                )
            )
            session.add(
                Activity(
                    level=ActivityLevel.ERROR,
                    event="download.failed",
                    message="Felt failed",
                    artist_id=FRAHM,
                    album_id=FAILED_MONITORED,
                )
            )
            # On disk, so these are inside ``library_scope`` and reach the
            # review list at all.
            session.add(
                EnrichmentState(
                    entity_type=EnrichmentEntity.ALBUM,
                    entity_id=ON_DISK,
                    source=EnrichmentSource.MUSICBRAINZ,
                    state="ambiguous",
                )
            )
            session.add(
                EnrichmentState(
                    entity_type=EnrichmentEntity.ALBUM,
                    entity_id=ON_DISK,
                    source=EnrichmentSource.DEEZER,
                    state="no_key",
                )
            )
            await session.commit()

    asyncio.run(seed())

    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        asyncio.run(engine.dispose())


def total(client: TestClient, url: str) -> int:
    response = client.get(url)
    assert response.status_code == 200, f"{url} -> {response.status_code}"
    return int(response.json()["total"])


def ids(client: TestClient, url: str, key: str = "id") -> set[str]:
    response = client.get(url)
    assert response.status_code == 200, f"{url} -> {response.status_code}"
    return {str(item[key]) for item in response.json()["items"]}


# ---------------------------------------------------------------------------
# Blank is not a filter — the whole matrix, in one sweep
# ---------------------------------------------------------------------------
#: ``(blanked, equivalent)`` — every optional filter on every list endpoint,
#: submitted the way a "show everything" ``<select>`` submits it, beside the
#: request it must be identical to.
#:
#: For five of the six that is the bare URL. ``/api/wanted`` is the exception and
#: it is not a wrinkle in the rule: its ``monitored`` parameter *defaults* to
#: ``True``, so the bare URL is already filtered and blanking the parameter
#: genuinely widens — which is precisely what the screen's "monitored / both /
#: ignored only" control is for. Blank still means "no filter"; the default was
#: never "no filter".
BLANKED = [
    ("/api/artists?q=&monitored=&sort=name&order=asc", "/api/artists"),
    (
        f"/api/artists/{FRAHM}/albums?status=&release_type=&q=",
        f"/api/artists/{FRAHM}/albums",
    ),
    ("/api/wanted?q=&status=&monitored=", "/api/wanted?monitored="),
    ("/api/queue?state=", "/api/queue"),
    ("/api/activity?level=&event=&artist_id=&album_id=", "/api/activity"),
    ("/api/enrichment/review?state=&source=", "/api/enrichment/review"),
]


@pytest.mark.parametrize(("blanked", "equivalent"), BLANKED)
def test_a_blanked_filter_is_a_200(
    client: TestClient, blanked: str, equivalent: str
) -> None:
    assert client.get(blanked).status_code == 200


@pytest.mark.parametrize(("blanked", "equivalent"), BLANKED)
def test_a_blanked_filter_returns_everything(
    client: TestClient, blanked: str, equivalent: str
) -> None:
    """Not merely a 200 — the same rows as the unfiltered request. A blank that
    parsed as ``False`` or as the literal string ``""`` would be a 200 with the
    wrong list in it, which is the worse failure."""
    assert total(client, blanked) == total(client, equivalent)


def test_a_blanked_filter_can_widen_past_a_default(client: TestClient) -> None:
    """Stated on its own because it is the one asymmetry in the rule above.

    ``/api/wanted`` defaults ``monitored`` to ``True``. Blanking it therefore
    returns *more* rows than omitting it, and that is correct on both counts:
    blank means no filter, and the endpoint's default is a filter somebody chose.
    A client that "helpfully" dropped empty values from its query string would
    silently lose the only way to see the ignored backlog.
    """
    assert total(client, "/api/wanted") == 2
    assert total(client, "/api/wanted?monitored=") == 3


# ---------------------------------------------------------------------------
# /api/artists
# ---------------------------------------------------------------------------
def test_artists_search_matches_a_substring_of_the_name(client: TestClient) -> None:
    assert ids(client, "/api/artists?q=frahm") == {FRAHM}
    assert ids(client, "/api/artists?q=jan") == {JANSEN}
    assert total(client, "/api/artists?q=zzznope") == 0


def test_artists_search_is_case_insensitive(client: TestClient) -> None:
    """The search box is typed into, so it is typed into in lower case."""
    assert ids(client, "/api/artists?q=FRAHM") == ids(client, "/api/artists?q=frahm")


def test_artists_monitored_is_tri_state(client: TestClient) -> None:
    """Three answers, not two: monitored only, ignored only, and both — which is
    why the parameter is ``bool | None`` and why blank has to mean ``None``."""
    assert ids(client, "/api/artists?monitored=true") == {FRAHM}
    assert ids(client, "/api/artists?monitored=false") == {JANSEN}
    assert total(client, "/api/artists?monitored=") == 2


def test_artists_filters_compose(client: TestClient) -> None:
    """``AND``, not ``OR``. An ``OR`` returns more rows for a narrower request,
    which looks like the filter simply not working."""
    assert total(client, "/api/artists?q=frahm&monitored=false") == 0
    assert total(client, "/api/artists?q=frahm&monitored=true") == 1


def test_artists_sort_and_order_are_honoured(client: TestClient) -> None:
    ascending = client.get("/api/artists?sort=name&order=asc").json()
    descending = client.get("/api/artists?sort=name&order=desc").json()

    assert [item["name"] for item in ascending["items"]] == ["Janine Jansen", "Nils Frahm"]
    assert [item["name"] for item in descending["items"]] == ["Nils Frahm", "Janine Jansen"]
    assert ascending["sort"] == "name" and ascending["order"] == "asc"


@pytest.mark.parametrize(
    "url", ["/api/artists?monitored=banana", "/api/artists?order=sideways"]
)
def test_artists_rejects_a_typo(client: TestClient, url: str) -> None:
    assert client.get(url).status_code == 422


# ---------------------------------------------------------------------------
# /api/artists/{id}/albums
# ---------------------------------------------------------------------------
def test_artist_albums_status_filter(client: TestClient) -> None:
    assert ids(client, f"/api/artists/{FRAHM}/albums?status=wanted") == {
        WANTED_MONITORED,
        WANTED_IGNORED,
    }
    assert ids(client, f"/api/artists/{FRAHM}/albums?status=downloaded") == {ON_DISK}


def test_artist_albums_release_type_filter(client: TestClient) -> None:
    assert ids(client, f"/api/artists/{FRAHM}/albums?release_type=ep") == {ON_DISK}
    assert ids(client, f"/api/artists/{FRAHM}/albums?release_type=live") == {
        WANTED_IGNORED
    }


def test_artist_albums_search_covers_title_and_label(client: TestClient) -> None:
    """``q`` is title, version *or* label — the three things printed on the row,
    so searching for what is on screen finds it."""
    assert ids(client, f"/api/artists/{FRAHM}/albums?q=melody") == {WANTED_MONITORED}
    assert ids(client, f"/api/artists/{FRAHM}/albums?q=nonesuch") == {ON_DISK}


def test_artist_albums_filters_compose(client: TestClient) -> None:
    assert total(client, f"/api/artists/{FRAHM}/albums?status=wanted&release_type=ep") == 0
    assert (
        total(client, f"/api/artists/{FRAHM}/albums?status=wanted&release_type=album") == 1
    )


def test_artist_albums_scope_is_the_artist_not_a_filter(client: TestClient) -> None:
    """Janine Jansen owns nothing, and that is a 200 with an empty list — not
    somebody else's releases, and not a 404."""
    body = client.get(f"/api/artists/{JANSEN}/albums").json()

    assert body["total"] == 0
    assert body["items"] == []


def test_artist_albums_404s_for_a_stranger(client: TestClient) -> None:
    """The artist is the subject of the query, so an unknown one is a missing
    entity rather than a filter that matched nothing."""
    assert client.get("/api/artists/nobody/albums").status_code == 404


def test_artist_albums_rejects_a_typo(client: TestClient) -> None:
    assert client.get(f"/api/artists/{FRAHM}/albums?status=banana").status_code == 422


# ---------------------------------------------------------------------------
# /api/wanted
# ---------------------------------------------------------------------------
def test_wanted_defaults_to_the_monitored_backlog(client: TestClient) -> None:
    """``wanted`` + ``failed``, monitored only. That default *is* the screen: it
    opens as the backlog, and everything else is a widening."""
    assert ids(client, "/api/wanted") == {WANTED_MONITORED, FAILED_MONITORED}


def test_wanted_status_narrows_to_one_state(client: TestClient) -> None:
    assert ids(client, "/api/wanted?status=wanted") == {WANTED_MONITORED}
    assert ids(client, "/api/wanted?status=failed") == {FAILED_MONITORED}


def test_wanted_monitored_false_is_the_ignored_list(client: TestClient) -> None:
    """"Ignored only" is a real view — it is how somebody finds what they
    un-wanted by accident."""
    assert ids(client, "/api/wanted?monitored=false") == {WANTED_IGNORED}


def test_wanted_blank_monitored_shows_both(client: TestClient) -> None:
    assert ids(client, "/api/wanted?monitored=") == {
        WANTED_MONITORED,
        WANTED_IGNORED,
        FAILED_MONITORED,
    }


def test_wanted_search_covers_the_artist_name_too(client: TestClient) -> None:
    """The backlog is cross-artist, so the artist name is on the row and has to
    be searchable — otherwise "show me what I am missing by Frahm" is impossible
    from the screen that lists it."""
    assert ids(client, "/api/wanted?q=frahm") == {WANTED_MONITORED, FAILED_MONITORED}
    assert ids(client, "/api/wanted?q=melody") == {WANTED_MONITORED}
    assert total(client, "/api/wanted?q=jansen") == 0


def test_wanted_filters_compose(client: TestClient) -> None:
    assert total(client, "/api/wanted?q=melody&status=failed") == 0
    assert total(client, "/api/wanted?q=spaces&monitored=false&status=wanted") == 1


def test_wanted_never_lists_what_is_already_on_disk(client: TestClient) -> None:
    """No filter combination may surface a downloaded release here. The list is
    defined by the states it is *about*, and widening a filter must not widen
    that."""
    for query in ("", "?status=", "?monitored=", "?q=", "?q=&status=&monitored="):
        assert ON_DISK not in ids(client, f"/api/wanted{query}")


def test_wanted_rejects_a_typo(client: TestClient) -> None:
    assert client.get("/api/wanted?status=banana").status_code == 422
    assert client.get("/api/wanted?monitored=banana").status_code == 422


# ---------------------------------------------------------------------------
# /api/queue
# ---------------------------------------------------------------------------
def test_queue_state_filter(client: TestClient) -> None:
    assert ids(client, "/api/queue?state=pending", key="album_id") == {WANTED_MONITORED}
    assert ids(client, "/api/queue?state=done", key="album_id") == {ON_DISK}
    assert total(client, "/api/queue?state=cancelled") == 0


def test_queue_blank_state_is_every_state(client: TestClient) -> None:
    assert total(client, "/api/queue?state=") == 2


def test_queue_rejects_a_typo(client: TestClient) -> None:
    """The original bug: the queue's select auto-submitted, so this fired without
    anybody pressing anything."""
    assert client.get("/api/queue?state=banana").status_code == 422


# ---------------------------------------------------------------------------
# /api/activity
# ---------------------------------------------------------------------------
def test_activity_level_filter(client: TestClient) -> None:
    assert total(client, "/api/activity?level=warning") == 1
    assert total(client, "/api/activity?level=error") == 1
    assert total(client, "/api/activity?level=info") == 1


def test_activity_event_filter_is_free_text(client: TestClient) -> None:
    """``event`` is a typed-in string, not an enum — the vocabulary grows every
    time somebody logs a new one, so an unknown value is an empty list rather
    than a 422."""
    assert total(client, "/api/activity?event=queue.add") == 1
    assert total(client, "/api/activity?event=no.such.event") == 0


def test_activity_scopes_to_an_artist_or_an_album(client: TestClient) -> None:
    """What the artist screen's activity strip uses, and the only way to read one
    release's history."""
    assert total(client, f"/api/activity?artist_id={JANSEN}") == 1
    assert total(client, f"/api/activity?album_id={WANTED_MONITORED}") == 1


def test_activity_filters_compose(client: TestClient) -> None:
    assert total(client, f"/api/activity?artist_id={FRAHM}&level=warning") == 0
    assert total(client, f"/api/activity?artist_id={FRAHM}&level=error") == 1


def test_activity_still_offers_the_whole_range(client: TestClient) -> None:
    """The history screen's own limit control went to 1000. Halving the ceiling
    would quietly delete half the range of a filter somebody was already using."""
    assert client.get("/api/activity?limit=1000").status_code == 200
    assert client.get("/api/activity?limit=1001").status_code == 422


def test_activity_rejects_a_typo(client: TestClient) -> None:
    assert client.get("/api/activity?level=banana").status_code == 422


# ---------------------------------------------------------------------------
# /api/enrichment/review
# ---------------------------------------------------------------------------
def test_review_lists_only_what_needs_a_person(client: TestClient) -> None:
    """``ambiguous`` and ``no_key``. ``not_found`` is waiting on an upstream, not
    on a decision, and never appears."""
    assert {row["state"] for row in client.get("/api/enrichment/review").json()["items"]} == {
        "ambiguous",
        "no_key",
    }


def test_review_state_filter(client: TestClient) -> None:
    assert total(client, "/api/enrichment/review?state=ambiguous") == 1
    assert total(client, "/api/enrichment/review?state=no_key") == 1


def test_review_source_filter(client: TestClient) -> None:
    assert ids(client, "/api/enrichment/review?source=musicbrainz", key="source") == {
        "musicbrainz"
    }
    assert total(client, "/api/enrichment/review?source=deezer") == 1


def test_review_filters_compose(client: TestClient) -> None:
    assert total(client, "/api/enrichment/review?source=deezer&state=ambiguous") == 0
    assert total(client, "/api/enrichment/review?source=deezer&state=no_key") == 1


def test_review_rejects_an_unknown_source(client: TestClient) -> None:
    """A rung nobody could ever have run is a bad request, and the message names
    the ones that exist — a filter for a source that does not exist is a typo,
    every time."""
    response = client.get("/api/enrichment/review?source=banana")

    assert response.status_code == 422
    assert "musicbrainz" in response.json()["error"]


#: **Known inconsistency — an unknown ``state`` is not rejected.**
#:
#: ``source=banana`` is a 422 because it reaches an enum constructor;
#: ``state=banana`` is passed straight through as a string, matches nothing and
#: comes back ``200`` with an empty list. Those are the same mistake with two
#: different answers, and the empty one is the misleading answer: "nothing needs
#: a human" is exactly what a review screen wants to hear, so a typo'd filter
#: reads as good news. The enrichment states are a closed set
#: (``app.enrich.errors.ENRICHMENT_STATES``, published on ``/api/meta``), so
#: there is nothing stopping this being validated like every other enum.
#: FIXED: ``enrichment_review`` now checks ``state`` against
#: ``ENRICHMENT_STATES`` before it builds the query, so a typo is a 422 naming
#: the vocabulary, exactly as an unknown ``source`` already was.
def test_review_rejects_an_unknown_state(client: TestClient) -> None:
    assert client.get("/api/enrichment/review?state=banana").status_code == 422


def test_review_is_held_to_the_library(client: TestClient) -> None:
    """Scope is not a filter. Only releases on disk are ever enriched, so a
    catalogue-only album has nothing to review and no filter can reach one."""
    rows = client.get("/api/enrichment/review").json()["items"]

    assert {row["entity_id"] for row in rows} == {ON_DISK}
