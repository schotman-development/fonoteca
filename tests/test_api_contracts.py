"""The response-shape promises the SPA is written against.

None of these is about a feature. Each is a property of the *wire* that a client
cannot check for itself and that fails silently when it breaks — the whole class
of bug where the server is right, the client is reasonable, and the screen is
wrong:

* **an id is a string.** Real album ids are ``uyej1o165e870`` and
  ``0884977859300``. The second one survives ``parseInt`` and comes back a
  different number, so the failure is a lookup that finds nothing rather than a
  type error anybody can see.
* **an empty string is not a filter, and a typo still is an error.** Every
  ``<select>``'s "show everything" option submits ``?status=``. Tolerating that
  is deliberate; tolerating ``?state=banana`` would turn every mistyped
  parameter into a silently unfiltered list.
* **a timestamp is aware UTC.** SQLite hands back naive datetimes from
  ``DateTime(timezone=True)`` columns, and a naive ISO string reaching the
  browser makes every relative time wrong by the host's offset — plausibly,
  which is why nobody notices.
* **``complete`` is genuinely three-valued.** ``null`` means *nothing has counted
  this release yet*, and it has to survive serialisation as JSON ``null``:
  rendering it as 0% reports a complete album as empty, which is the one wrong
  answer available.
* **``unfiltered_total`` and ``queueable_total`` are counts nothing else can
  derive.** One separates "nothing matches what you typed" from "there is
  nothing here"; the other labels a button that ignores the filters on screen.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; nothing here touches the network.
"""

from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

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
    Track,
    TrackStatus,
)

ARTIST_ID = "720076"

#: An id that is all digits, which is what makes the "ids are strings" rule
#: worth a test: it round-trips through ``parseInt`` looking fine.
NUMERIC_ID = "0884977859300"
WANTED_ID = "uyej1o165e870"
ADOPTED_ID = "cccc3333dddd4"
PARTIAL_ID = "eeee5555ffff6"
QUEUED_ID = "aaaa1111bbbb2"


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """One artist and five releases, chosen to cover every completeness state."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'contracts.db'}", poolclass=NullPool
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
            # Naive, exactly as SQLite returns one from a tz-aware column. If the
            # read models did not re-stamp these the browser would read them as
            # local time and every "ago" would be wrong by the host's offset.
            stamp = datetime(2026, 1, 2, 3, 4, 5)
            session.add(
                Artist(
                    id=ARTIST_ID,
                    name="Nils Frahm",
                    monitored=True,
                    added_at=stamp,
                    last_checked_at=stamp,
                )
            )
            for album_id, title, status, tracks_count in (
                (WANTED_ID, "All Melody", AlbumStatus.WANTED, 6),
                (NUMERIC_ID, "Screws", AlbumStatus.WANTED, 9),
                (ADOPTED_ID, "Solo", AlbumStatus.DOWNLOADED, 6),
                (PARTIAL_ID, "Spaces", AlbumStatus.DOWNLOADED, 3),
                (QUEUED_ID, "Felt", AlbumStatus.QUEUED, 11),
            ):
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=title,
                        status=status,
                        monitored=True,
                        release_type="album" if album_id != NUMERIC_ID else "ep",
                        tracks_count=tracks_count,
                        added_at=stamp,
                        path="/music/Nils Frahm/" + title
                        if status is AlbumStatus.DOWNLOADED
                        else None,
                    )
                )
            # Two of three files present: a real gap, and the case the old
            # blanket ``True`` hid.
            for number in (1, 2):
                session.add(
                    Track(
                        id=f"track-{number}",
                        album_id=PARTIAL_ID,
                        title=f"Track {number}",
                        track_number=number,
                        status=TrackStatus.DOWNLOADED,
                        path=f"/music/Nils Frahm/Spaces/0{number}.flac",
                        downloaded_at=stamp,
                        content_hash="0123456789abcdef0123456789abcdef",
                        sample_count=10_584_000,
                        file_size=41_337_000,
                        file_mtime=1_767_322_445.0,
                        verified_at=stamp,
                    )
                )
            session.add(
                QueueItem(
                    album_id=QUEUED_ID,
                    state=QueueState.PENDING,
                    created_at=stamp,
                    progress_tracks_total=11,
                )
            )
            session.add(
                Activity(
                    level=ActivityLevel.INFO,
                    event="queue.add",
                    message="Queued Felt",
                    artist_id=ARTIST_ID,
                    album_id=QUEUED_ID,
                    created_at=stamp,
                )
            )
            session.add(
                EnrichmentState(
                    entity_type=EnrichmentEntity.ALBUM,
                    entity_id=ADOPTED_ID,
                    source=EnrichmentSource.MUSICBRAINZ,
                    state="ambiguous",
                    last_attempt_at=stamp,
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


def walk(payload: Any, path: str = "") -> Iterator[tuple[str, Any]]:
    """Every ``(dotted key, value)`` pair in a decoded JSON document."""
    if isinstance(payload, dict):
        for key, value in payload.items():
            yield f"{path}.{key}" if path else str(key), value
            yield from walk(value, f"{path}.{key}" if path else str(key))
    elif isinstance(payload, list):
        for index, item in enumerate(payload):
            yield from walk(item, f"{path}[{index}]")


#: Every list endpoint, with a filtered variant that matches strictly fewer rows.
LIST_ENDPOINTS = [
    ("/api/artists", "/api/artists?q=zzznope"),
    (
        f"/api/artists/{ARTIST_ID}/albums",
        f"/api/artists/{ARTIST_ID}/albums?status=wanted",
    ),
    ("/api/wanted", "/api/wanted?q=zzznope"),
    ("/api/queue", "/api/queue?state=done"),
    ("/api/activity", "/api/activity?event=no.such.event"),
    ("/api/enrichment/review", "/api/enrichment/review?source=deezer"),
]


# ---------------------------------------------------------------------------
# Ids are strings
# ---------------------------------------------------------------------------
def test_a_numeric_looking_album_id_stays_a_string(client: TestClient) -> None:
    """``0884977859300`` is a real Qobuz album id. As a number it loses its
    leading zero and stops matching anything — and nothing raises."""
    body = client.get(f"/api/albums/{NUMERIC_ID}").json()

    assert body["id"] == NUMERIC_ID
    assert isinstance(body["id"], str)


@pytest.mark.parametrize(
    ("url", "keys"),
    [
        ("/api/artists", ("id",)),
        (f"/api/artists/{ARTIST_ID}/albums", ("id", "artist_id")),
        ("/api/wanted", ("id", "artist_id")),
        ("/api/queue", ("album_id",)),
        ("/api/activity", ("artist_id", "album_id")),
        ("/api/enrichment/review", ("entity_id", "artist_id")),
    ],
)
def test_every_entity_id_is_a_string(
    client: TestClient, url: str, keys: tuple[str, ...]
) -> None:
    """Coercing with ``str()`` before querying is a documented invariant on the
    server; this is the same rule stated where the client can rely on it."""
    items = client.get(url).json()["items"]
    assert items, f"{url} returned nothing to check"

    for item in items:
        for key in keys:
            assert item[key] is None or isinstance(item[key], str), f"{url}: {key}"


def test_a_track_id_is_a_string_too(client: TestClient) -> None:
    """Scanner-minted ids are synthetic strings keyed on position; download ids
    come from Qobuz. Neither is an integer, and nothing may infer provenance
    from the shape of one."""
    tracks = client.get(f"/api/albums/{PARTIAL_ID}").json()["tracks"]

    assert tracks
    assert all(isinstance(track["id"], str) for track in tracks)


def test_the_queue_item_id_is_the_one_integer(client: TestClient) -> None:
    """Recorded because it is the exception, and an exception nobody writes down
    becomes a client that stringifies it "for consistency" and 404s."""
    item = client.get("/api/queue").json()["items"][0]

    assert isinstance(item["id"], int)
    assert isinstance(item["album_id"], str)


# ---------------------------------------------------------------------------
# Empty string is not a filter; a typo still is an error
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    ("blank", "plain"),
    [
        ("/api/artists?q=&monitored=", "/api/artists"),
        (
            f"/api/artists/{ARTIST_ID}/albums?status=&release_type=&q=",
            f"/api/artists/{ARTIST_ID}/albums",
        ),
        ("/api/wanted?q=&status=", "/api/wanted"),
        ("/api/queue?state=", "/api/queue"),
        ("/api/activity?level=&event=&artist_id=&album_id=", "/api/activity"),
        ("/api/enrichment/review?state=&source=", "/api/enrichment/review"),
    ],
)
def test_blank_parameters_are_identical_to_omitting_them(
    client: TestClient, blank: str, plain: str
) -> None:
    """``?status=``, ``?status`` and an omitted ``status`` are one request.

    The client may therefore keep a filter object with empty strings in it and
    send it verbatim, which is what makes a controlled ``<select>`` possible
    without a serialisation step that has to know which values are "really" set.
    """
    blanked = client.get(blank)
    unfiltered = client.get(plain)

    assert blanked.status_code == 200
    assert blanked.json()["total"] == unfiltered.json()["total"]


@pytest.mark.parametrize(
    "url",
    [
        "/api/queue?state=banana",
        "/api/wanted?status=banana",
        f"/api/artists/{ARTIST_ID}/albums?status=banana",
        "/api/activity?level=banana",
        "/api/artists?monitored=banana",
        "/api/enrichment/review?source=banana",
    ],
)
def test_a_typo_is_still_a_422(client: TestClient, url: str) -> None:
    """The tolerance is for the empty string and nothing else. A misspelled enum
    silently returning everything is how a filtered screen quietly stops being
    filtered."""
    assert client.get(url).status_code == 422


def test_null_is_never_the_way_to_say_no_filter(client: TestClient) -> None:
    """Recorded so the client sends ``''``: the literal string ``null`` is a
    value like any other and does not parse as an ``AlbumStatus``."""
    assert client.get("/api/wanted?status=null").status_code == 422


def test_a_422_keeps_the_error_envelope(client: TestClient) -> None:
    """The SPA's API client keys off ``error``, on every failure shape."""
    body = client.get("/api/queue?state=banana").json()

    assert body["ok"] is False
    assert isinstance(body["error"], str) and body["error"]
    assert isinstance(body["detail"], list)


# ---------------------------------------------------------------------------
# Timestamps
# ---------------------------------------------------------------------------
TIMESTAMPED = [
    "/api/artists",
    f"/api/artists/{ARTIST_ID}/albums",
    "/api/wanted",
    "/api/queue",
    "/api/activity",
    "/api/enrichment/review",
    "/api/status",
    f"/api/albums/{PARTIAL_ID}",
]


#: **Known server bug — do not delete this marker without deleting the bug.**
#:
#: The contract is that every timestamp on the wire is aware UTC, and the client
#: is written against it: ``web/src/format/time.ts`` says so in its opening
#: comment and calls ``new Date(iso)`` on the strength of it. It is not true.
#: ``deps.as_utc`` exists and is applied to the handful of models that are built
#: field by field (``TrackOut.integrity.last_verified_at``, the stored scan
#: summary, ``IndexerStatusOut``), but every model derived from an ORM row goes
#: through ``model_validate`` and copies SQLite's **naive** datetime straight
#: out: ``ArtistOut.added_at``/``last_checked_at``, ``AlbumOut.added_at``/
#: ``wanted_at``, ``QueueItemOut.created_at``/``started_at``/``finished_at``,
#: ``ActivityOut.created_at``, ``TrackOut.downloaded_at``,
#: ``EnrichmentReviewOut.last_attempt_at``.
#:
#: The symptom is not an error anywhere. ``new Date('2026-01-02T03:04:05')``
#: parses as *local* time, so every relative time in the application is wrong by
#: the host's UTC offset — an hour or two, which reads as a slightly wrong clock
#: rather than as a bug, and reads as nothing at all on a machine running UTC
#: (which is what CI is, and what a container usually is).
#:
#: FIXED by ``schemas.UtcDatetime``: every datetime field a client can see runs
#: through ``_stamp_utc`` on the way out, so a naive column becomes aware UTC
#: before it is serialised. The marker came off in the same commit, and this test
#: is now what stops it coming back — a new read model that declares a bare
#: ``datetime`` fails here rather than in somebody's timezone.
def test_every_timestamp_is_aware_utc(client: TestClient) -> None:
    """SQLite returns naive datetimes from ``DateTime(timezone=True)`` columns.

    Serialising one naive makes ``new Date(iso)`` read it as local time, so every
    relative time on the screen is wrong by the host's offset — by an hour or
    two, which reads as a clock being slightly off rather than as a bug.

    Every endpoint is swept in one test rather than parametrised so the failure
    names the whole surface at once: the fix is one change and it either lands
    everywhere or it has not landed.
    """
    naive = [
        f"{url} {key} = {value}"
        for url in TIMESTAMPED
        for key, value in walk(client.get(url).json())
        if key.split(".")[-1].endswith("_at")
        and isinstance(value, str)
        and datetime.fromisoformat(value).tzinfo is None
    ]

    assert naive == [], f"naive timestamps on the wire: {naive}"


def test_a_seeded_naive_timestamp_comes_back_stamped(client: TestClient) -> None:
    """The re-stamping is a real conversion, not a formatting accident: the
    fixture wrote a naive value and the wire carries the same instant, in UTC."""
    added = client.get("/api/artists").json()["items"][0]["added_at"]

    parsed = datetime.fromisoformat(added)

    assert parsed.tzinfo is not None
    assert parsed.utcoffset() == timedelta(0)
    assert parsed == datetime(2026, 1, 2, 3, 4, 5, tzinfo=timezone.utc)


def test_the_timestamps_that_are_built_by_hand_are_already_right(
    client: TestClient,
) -> None:
    """Half the contract does hold, and it is worth pinning which half.

    ``TrackOut.integrity`` is assembled field by field and its timestamp goes
    through ``deps.as_utc``, so it is aware — from the same row, in the same
    response, as the naive ``downloaded_at`` beside it. That is the evidence the
    intent is real and the gap above is an omission rather than a decision, and
    it is what the eventual fix has to match.
    """
    track = client.get(f"/api/albums/{PARTIAL_ID}").json()["tracks"][0]

    verified = datetime.fromisoformat(track["integrity"]["last_verified_at"])

    assert verified.tzinfo is not None
    assert verified.utcoffset() == timedelta(0)
    assert verified == datetime(2026, 1, 2, 3, 4, 5, tzinfo=timezone.utc)


# ---------------------------------------------------------------------------
# Completeness is three-valued
# ---------------------------------------------------------------------------
def test_nothing_has_counted_this_release_is_null(client: TestClient) -> None:
    """Not ``false``, and emphatically not ``0``. The client renders "unknown",
    which is a different cell from an empty meter."""
    body = client.get(f"/api/albums/{WANTED_ID}").json()

    assert "complete" in body
    assert body["complete"] is None
    assert body["tracks_on_disk"] is None


def test_null_survives_serialisation_rather_than_being_dropped(
    client: TestClient,
) -> None:
    """A key that is absent and a key that is ``null`` are the same thing to
    ``if (album.complete)`` and different things to a three-way render. The
    server must send the key."""
    raw = client.get(f"/api/albums/{WANTED_ID}").text

    assert '"complete":null' in raw.replace(" ", "")


def test_an_adopted_release_answers_from_its_status(client: TestClient) -> None:
    """A folder the scanner adopted has no per-track record, and reporting it as
    0% would show a complete album as empty."""
    body = client.get(f"/api/albums/{ADOPTED_ID}").json()

    assert body["complete"] is True
    assert body["tracks_on_disk"] is None


def test_a_release_that_was_counted_reports_the_bad_news(client: TestClient) -> None:
    """Two files where the catalogue says three is a real gap. Both numbers are
    known, so this is the only case where a meter may render."""
    body = client.get(f"/api/albums/{PARTIAL_ID}").json()

    assert body["tracks_on_disk"] == 2
    assert body["tracks_count"] == 3
    assert body["complete"] is False


def test_completeness_is_the_same_in_a_list_as_on_its_own(client: TestClient) -> None:
    """The release screen and the artist table must not disagree about whether a
    release is intact — one is reached from the other."""
    listed = {
        album["id"]: album
        for album in client.get(f"/api/artists/{ARTIST_ID}/albums").json()["items"]
    }

    for album_id in (WANTED_ID, ADOPTED_ID, PARTIAL_ID):
        single = client.get(f"/api/albums/{album_id}").json()
        assert listed[album_id]["complete"] == single["complete"]
        assert listed[album_id]["tracks_on_disk"] == single["tracks_on_disk"]


# ---------------------------------------------------------------------------
# PageOut.unfiltered_total (G10)
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(("plain", "filtered"), LIST_ENDPOINTS)
def test_every_list_envelope_carries_the_unfiltered_total(
    client: TestClient, plain: str, filtered: str
) -> None:
    body = client.get(plain).json()

    assert "unfiltered_total" in body
    assert body["unfiltered_total"] is None or isinstance(body["unfiltered_total"], int)


@pytest.mark.parametrize(("plain", "filtered"), LIST_ENDPOINTS)
def test_a_filter_that_matches_nothing_still_reports_what_exists(
    client: TestClient, plain: str, filtered: str
) -> None:
    """The number that makes "no release matches that" a different sentence from
    "this artist has no releases". Only one of the two has an action attached —
    clear the filter — and a list that reports only its filtered total cannot
    tell them apart, so its empty state ends up saying neither.
    """
    narrowed = client.get(filtered).json()

    assert narrowed["total"] <= narrowed["unfiltered_total"]
    assert narrowed["unfiltered_total"] == client.get(plain).json()["unfiltered_total"]


def test_the_artist_scope_is_not_a_filter(client: TestClient) -> None:
    """Whatever the endpoint is *about* stays applied. An album list scoped to
    one artist reports that artist's releases, never the library's — otherwise
    the empty state would offer to clear a filter that does not exist."""
    body = client.get(f"/api/artists/{ARTIST_ID}/albums?status=wanted").json()

    assert body["total"] == 2
    assert body["unfiltered_total"] == 5


def test_the_backlog_states_are_not_a_filter_either(client: TestClient) -> None:
    """``/api/wanted`` is *about* ``wanted`` + ``failed``; only ``q``,
    ``status`` and ``monitored`` narrow it. Dropping the subject as well would
    make the unfiltered total the whole catalogue."""
    body = client.get("/api/wanted").json()

    assert body["unfiltered_total"] == 2  # both wanted releases, monitored or not


# ---------------------------------------------------------------------------
# queueable_total (G9)
# ---------------------------------------------------------------------------
FILTER_COMBINATIONS = [
    "",
    "?q=all",
    "?q=zzznope",
    "?status=wanted",
    "?status=failed",
    "?monitored=true",
    "?monitored=false",
    "?monitored=",
    "?q=&status=&monitored=",
    "?q=screws&status=wanted&monitored=true",
    "?limit=1",
]


@pytest.mark.parametrize("query", FILTER_COMBINATIONS)
def test_the_download_all_count_ignores_every_filter(
    client: TestClient, query: str
) -> None:
    """It labels a button that queues the whole monitored backlog, not the rows
    on screen. Mislabelling it means somebody presses expecting one download and
    gets two — which is the failure the opt-in rule exists to prevent, arriving
    through the button that is allowed to queue.
    """
    body = client.get(f"/api/wanted{query}").json()

    assert body["queueable_total"] == 2


def test_the_download_all_count_is_what_the_button_really_queues(
    client: TestClient,
) -> None:
    """Asserted against the endpoint rather than against the seed: the label and
    the action are two readings of one rule, and this is the only place they can
    be compared."""
    promised = client.get("/api/wanted?status=failed").json()["queueable_total"]

    message = client.post("/api/wanted/download").json()["message"]

    assert str(promised) in message
    assert client.get("/api/queue").json()["total"] == promised + 1  # the seeded item


def test_queueable_total_is_only_on_the_backlog(client: TestClient) -> None:
    """It answers for one button. On any other list it would be a number with no
    action attached, which is how a count starts being read as something else."""
    assert "queueable_total" not in client.get(f"/api/artists/{ARTIST_ID}/albums").json()
