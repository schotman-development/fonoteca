"""The last of the JSON API's gaps: G27–G34 of the front-end rebuild spec.

These are the capabilities the server-rendered UI had and no ``/api`` route
carried, plus the surface the new Health section needs. Four of them add
nothing to the wire and are recorded here as *behaviour that must not
regress*, because the reason each one is safe lives on the server:

* **G27** following from the import review list is the ordinary
  ``POST /api/artists``, and its ``created`` flag is the only thing that
  distinguishes "now following" from "already followed" — a second press is
  otherwise indistinguishable from the first.
* **G28** a catalogue search that cannot run fails *inside the panel that asked*,
  which means the failure has to be legible: the 503 names the environment
  variables, and ``detail.code`` lets the panel tell "not configured" (send them
  to Settings) from "Qobuz did not answer" (offer a retry) without matching on
  prose.
* **G29** a dry-run scan must not overwrite the stored record of the last real
  one, or a Preview quietly becomes the library's history.
* **G30** release-group keys are resolved by the server, and the endpoint accepts
  either kind of key, because both kinds coexist permanently — only albums on
  disk are ever enriched, so the copy you own carries an MBID and its
  catalogue-only twins never will.

Everything is HTTP against a scratch database. No network, no queue worker.
"""

from __future__ import annotations

import asyncio
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app import schemas
from app.api import routes_api
from app.config import get_settings
from app.core.enricher import Enricher, ReviewItem
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

#: Two releases with the same title and **no** MusicBrainz ids at all. That is
#: the majority case in a real library, and it is what makes the title-key
#: fallback load-bearing rather than a nicety.
TWIN_TITLE = "Blues Of Desperation"


@pytest.fixture(name="client")
def client_fixture(tmp_path: Any) -> Iterator[TestClient]:
    """One artist, three releases, and four rows of unresolved enrichment."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'gaps.db'}", poolclass=NullPool
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
            session.add(Artist(id=ARTIST_ID, name="Joe Bonamassa", monitored=True))
            # Two editions of one record, keyed by normalised title because
            # nothing has identified either of them.
            session.add(
                Album(
                    id="std",
                    artist_id=ARTIST_ID,
                    title=TWIN_TITLE,
                    tracks_count=11,
                    status=AlbumStatus.WANTED,
                )
            )
            session.add(
                Album(
                    id="dlx",
                    artist_id=ARTIST_ID,
                    title=TWIN_TITLE,
                    version="Deluxe",
                    tracks_count=13,
                    status=AlbumStatus.WANTED,
                )
            )
            # On disk, so enrichment is in scope for it — the review list is
            # held to the library, not the catalogue.
            session.add(
                Album(
                    id="owned",
                    artist_id=ARTIST_ID,
                    title="Dust Bowl",
                    tracks_count=12,
                    status=AlbumStatus.DOWNLOADED,
                    path="/music/Joe Bonamassa/Dust Bowl",
                )
            )
            for source, state, error in (
                (EnrichmentSource.MUSICBRAINZ, "ambiguous", "two releases matched"),
                (EnrichmentSource.DEEZER, "no_key", "no barcode"),
                # Keyed by an id MusicBrainz establishes: there is nothing for a
                # person to type and no column to put it in.
                (EnrichmentSource.COVERARTARCHIVE, "no_key", "no release mbid"),
                # Takes no id of its own either — but its ambiguity is a list of
                # MusicBrainz releases the coverage solver refused to choose
                # between, so a person really can settle it, on MusicBrainz.
                (EnrichmentSource.ACOUSTID, "ambiguous", "three editions fit"),
            ):
                session.add(
                    EnrichmentState(
                        entity_type=EnrichmentEntity.ALBUM,
                        entity_id="owned",
                        source=source,
                        state=state,
                        last_error=error,
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


@pytest.fixture(name="enricher")
def enricher_fixture(monkeypatch: pytest.MonkeyPatch) -> Enricher:
    """A wired-up enricher with no providers.

    Its settings are pinned so a developer who really does have an AcoustID key
    sees the same gate readouts as CI does.
    """
    settings = get_settings().model_copy(
        update={"enrichment_contact": "", "acoustid_api_key": ""}
    )
    instance = Enricher([], settings=settings)
    monkeypatch.setattr(routes_api, "get_enricher", lambda: instance)
    monkeypatch.setattr("app.api.deps.get_enricher", lambda: instance)
    return instance


def review_rows(client: TestClient) -> dict[str, dict[str, Any]]:
    """The review list keyed by source — every row here is about one album."""
    payload = client.get("/api/enrichment/review?limit=100").json()
    return {row["source"]: row for row in payload["items"]}


# ---------------------------------------------------------------------------
# G27 — following from the import review list
# ---------------------------------------------------------------------------
def test_following_says_whether_it_was_new(client: TestClient) -> None:
    """``created`` is the whole difference between the two sentences.

    The import review list offers one follow button per candidate and the same
    artist can be reached from more than one row, so "Now following X." and "X
    was already followed." are both true answers to the same press — and after
    the fact the response is identical either way.
    """
    first = client.post("/api/artists", json={"artist_id": "5000", "name": "Alt-J"})
    second = client.post("/api/artists", json={"artist_id": "5000", "name": "Alt-J"})

    assert first.status_code == 201
    assert first.json()["created"] is True
    assert second.json()["created"] is False
    assert second.json()["id"] == "5000"


def test_following_is_the_only_import_follow_route(client: TestClient) -> None:
    """There is deliberately no import-specific endpoint.

    A person choosing from candidates is not the bulk importer being made fuzzy
    — it is the ordinary follow, which is why it honours ``AUTO_INDEX_ON_FOLLOW``
    for that one artist while a bulk import never indexes.
    """
    paths = set(client.get("/api/openapi.json").json()["paths"])

    assert "/api/artists" in paths
    assert not [path for path in paths if path.startswith("/api/library/import/follow")]


def test_following_populates_the_roll_ups(client: TestClient) -> None:
    """A ``null`` count renders as an em dash where a number belongs."""
    body = client.post(
        "/api/artists", json={"artist_id": ARTIST_ID, "name": "Joe Bonamassa"}
    ).json()

    assert body["created"] is False
    assert body["album_count"] == 3
    assert body["downloaded_count"] == 1


# ---------------------------------------------------------------------------
# G28 — a search failure is legible enough to render in the panel that asked
# ---------------------------------------------------------------------------
def test_search_without_credentials_names_the_settings(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """503, and the message is the fix.

    This is what "degrades gracefully without credentials" means from the
    outside. A search box that says only "unavailable" turns a two-line change
    in ``.env`` into a support question.
    """
    monkeypatch.setattr(routes_api, "get_qobuz_client", lambda: None)

    response = client.get("/api/search?q=frahm")
    body = response.json()

    assert response.status_code == 503
    assert "QOBUZ_APP_ID" in body["error"]
    assert "QOBUZ_USER_AUTH_TOKEN" in body["error"]


def test_search_failures_carry_a_machine_readable_code(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The panel tells the two failures apart without matching on prose.

    "Nothing is configured" links to Settings and offers no retry; "Qobuz did
    not answer" offers a retry and must not send anyone to Settings. Deriving
    that from the sentence makes the sentence an interface nobody can edit.
    """
    monkeypatch.setattr(routes_api, "get_qobuz_client", lambda: None)

    body = client.get("/api/search?q=frahm").json()

    assert body["detail"]["code"] == "no_client"
    assert body["detail"]["missing"] == ["QOBUZ_APP_ID", "QOBUZ_USER_AUTH_TOKEN"]


def test_the_error_envelope_keeps_its_shape(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """``error`` stays prose even though ``detail`` is now a payload."""
    monkeypatch.setattr(routes_api, "get_qobuz_client", lambda: None)

    body = client.get("/api/search?q=frahm").json()

    assert body["ok"] is False
    assert body["status_code"] == 503
    assert isinstance(body["error"], str)
    assert "error" not in body["detail"]


@pytest.mark.parametrize(
    "call",
    [("post", "/api/enrichment/run"), ("post", "/api/library/import")],
)
def test_the_other_inline_surfaces_fail_the_same_way(
    client: TestClient, call: tuple[str, str]
) -> None:
    """Three panels, one envelope.

    The import panel and the enrichment panel draw their own failures for the
    same reason the search box does — a subsystem that is not wired up is not a
    reason to replace the screen. What they need from the server is a 503 that
    is renderable: ``ok``, a prose ``error`` and the status code, every time.
    """
    method, path = call
    response = getattr(client, method)(path)
    body = response.json()

    assert response.status_code == 503
    assert body["ok"] is False
    assert body["status_code"] == 503
    assert isinstance(body["error"], str) and body["error"]


def test_an_empty_query_needs_no_client_at_all(client: TestClient) -> None:
    """Nothing was asked, so nothing can fail — the panel opens before typing."""
    response = client.get("/api/search?q=")

    assert response.status_code == 200
    assert response.json()["artists"] == []


# ---------------------------------------------------------------------------
# G29 — a dry run reports itself and changes nothing
# ---------------------------------------------------------------------------
def test_a_dry_run_scan_is_not_written_into_the_stored_history(
    client: TestClient,
) -> None:
    """A Preview must not become the record of the last real scan.

    The report comes back on the mutation's own response and the client holds it
    in component state; ``GET /api/library/scan`` keeps answering with whatever
    the last *applied* run was — which is ``None`` here, and "nothing has been
    scanned yet" is a distinct claim from "the last scan found nothing".
    """
    before = client.get("/api/library/scan").json()

    preview = client.post("/api/library/scan?dry_run=true")

    assert preview.status_code in (200, 409, 503)
    if preview.status_code == 200:
        assert preview.json()["applied"] is False
    assert client.get("/api/library/scan").json()["last"] == before["last"]


# ---------------------------------------------------------------------------
# G30 — the server resolves release-group keys, in both directions
# ---------------------------------------------------------------------------
def test_a_title_key_resolves_to_every_edition(client: TestClient) -> None:
    """The fallback key is the one most of a real library is filed under.

    Nothing has identified either of these releases, so the record is keyed by
    normalised title — and a client that grouped them itself would have to
    re-implement a rule that is deliberately asymmetric.
    """
    key = client.get("/api/albums/std").json()["release_group_key"]

    group = client.get(f"/api/release-groups/{key}").json()

    assert group["total"] == 2
    assert {edition["id"] for edition in group["editions"]} == {"std", "dlx"}
    assert group["mb_release_group_mbid"] is None


def test_the_best_edition_comes_first(client: TestClient) -> None:
    """Ordered by ``indexer.edition_rank`` — track count before audio quality, so
    a one-track hi-res promo never outranks the album it was cut from."""
    key = client.get("/api/albums/std").json()["release_group_key"]

    group = client.get(f"/api/release-groups/{key}").json()

    assert group["editions"][0]["id"] == "dlx"


def test_an_unknown_key_is_a_404(client: TestClient) -> None:
    assert client.get("/api/release-groups/nothing-here").status_code == 404


# ---------------------------------------------------------------------------
# G31 — every list envelope is declared where a generator will find it
# ---------------------------------------------------------------------------
ENVELOPES = [
    "ArtistListOut",
    "AlbumListOut",
    "WantedListOut",
    "QueueListOut",
    "EnrichmentReviewListOut",
    "ActivityListOut",
    "ArtistDetailOut",
    "EnrichmentCandidateOut",
    "EnrichmentCandidatesOut",
]


@pytest.mark.parametrize("name", ENVELOPES)
def test_the_envelopes_are_exported_from_schemas(name: str) -> None:
    """A type generator reading ``app/schemas.py`` saw every entity and not one
    of the shapes they arrive in, so the client's list types were hand-written —
    right until a field was added here."""
    assert name in schemas.__all__
    assert hasattr(schemas, name)


@pytest.mark.parametrize("name", ENVELOPES[:7])
def test_the_routes_module_re_exports_the_same_objects(name: str) -> None:
    """Moved, not copied. Two classes with one name is the bug this avoids."""
    assert getattr(routes_api, name) is getattr(schemas, name)


def test_every_list_endpoint_is_in_the_openapi_document(client: TestClient) -> None:
    """The point of the move: the client is generated, not typed by hand."""
    schema_names = set(client.get("/api/openapi.json").json()["components"]["schemas"])

    assert {"ArtistListOut", "WantedListOut", "ActivityListOut"} <= schema_names


# ---------------------------------------------------------------------------
# G32 — the Health overview is an aggregate, not a second source of truth
# ---------------------------------------------------------------------------
def test_the_overview_gathers_every_subsystem(
    client: TestClient, enricher: Enricher
) -> None:
    summary = client.get("/api/health/summary").json()

    assert set(summary) == {
        "status",
        "scan",
        "library_import",
        "enrichment",
        "integrity",
        "trash",
    }


@pytest.mark.parametrize(
    ("field", "endpoint"),
    [
        ("scan", "/api/library/scan"),
        ("library_import", "/api/library/import"),
        ("enrichment", "/api/enrichment"),
        ("integrity", "/api/integrity"),
    ],
)
def test_the_overview_equals_the_screen_it_links_to(
    client: TestClient, enricher: Enricher, field: str, endpoint: str
) -> None:
    """Equality by construction: each field *is* the sub-endpoint's own builder.

    An overview that computed its own handful of numbers is how a landing screen
    starts disagreeing with the page it links to — the same failure the old UI
    had when a live region rendered from a different context than its page.
    """
    summary = client.get("/api/health/summary").json()

    assert summary[field] == client.get(endpoint).json()


def test_the_autonomy_split_reaches_the_wire_and_partitions_the_library(
    client: TestClient, enricher: Enricher
) -> None:
    """``GET /api/enrichment`` really carries ``autonomy``, and it adds up.

    The Identify band's whole arithmetic is that the five buckets partition
    ``entities``, so the screen can say "30 of 40" without doing sums of its
    own. Asserting it on the *body* rather than only under the service function
    is the point: a response model that gained the field and a splat that
    dropped it fail in the same place and nowhere else.

    On this seed ``entities`` is 2 — the one ``DOWNLOADED`` release and the
    artist who owns it — and the two catalogue-only twins are in neither, which
    is the honest denominator ("a percentage against the catalogue reports a
    finished library as 1%"). The album's ``musicbrainz=ambiguous`` row has a
    picker, so the album is ``waiting_person``; the artist has no state rows at
    all, which is ``unstarted`` rather than absent.
    """
    payload = client.get("/api/enrichment").json()
    autonomy = payload["autonomy"]

    assert set(autonomy) == {
        "entities",
        "automatic",
        "waiting_person",
        "waiting_input",
        "dismissed",
        "unstarted",
    }
    assert autonomy["entities"] == 2
    assert autonomy["waiting_person"] == 1
    assert autonomy["unstarted"] == 1
    assert autonomy["automatic"] == 0
    assert (
        autonomy["automatic"]
        + autonomy["waiting_person"]
        + autonomy["waiting_input"]
        + autonomy["dismissed"]
        + autonomy["unstarted"]
        == autonomy["entities"]
    )
    assert autonomy["entities"] == payload["scope"]["albums"] + payload["scope"]["artists"]
    assert payload["scope"]["catalogue_albums"] == 3


def test_the_autonomy_split_and_the_review_badge_agree_about_the_person(
    client: TestClient, enricher: Enricher
) -> None:
    """One album, two pickable rows, one entity waiting on somebody.

    ``review_total`` counts *rows* and ``waiting_person`` counts *entities*, so
    they legitimately differ — three pressable rows here all sit on one album,
    and that is exactly why the band was not built out of ``states``, whose
    figures are row counts against a denominator of entities. What must never
    differ is which rows either of them thinks a person can act on: both go
    through ``review_picker_for``. Deezer's ``no_key`` counts (it has an id
    somebody can type), the Cover Art Archive's does not (it is keyed by an MBID
    MusicBrainz will supply), and AcoustID's ``ambiguous`` counts by delegation
    to MusicBrainz.
    """
    payload = client.get("/api/enrichment").json()

    assert payload["review_total"] == 3  # musicbrainz + deezer + acoustid, one album
    assert payload["autonomy"]["waiting_person"] == 1  # the album they all sit on
    # The Cover Art Archive row is a genuine input-wait, but ``waiting_person``
    # already claimed this album: the buckets partition, so it is outranked
    # rather than counted twice.
    assert payload["autonomy"]["waiting_input"] == 0


def test_the_overview_carries_the_status_numbers(
    client: TestClient, enricher: Enricher
) -> None:
    """Compared field by field rather than whole: ``uptime_seconds`` moves
    between two requests, and a test that failed on that would be measuring the
    clock rather than the contract."""
    summary = client.get("/api/health/summary").json()["status"]
    status = client.get("/api/status").json()

    assert summary["library"] == status["library"]
    assert summary["queue"] == status["queue"]
    assert summary["banners"] == status["banners"]


def test_the_overview_shows_no_activity_feed_by_default(
    client: TestClient, enricher: Enricher
) -> None:
    """The Activity screen owns that; here it would be a page of rows nobody
    reads, fetched every ten seconds."""
    assert client.get("/api/health/summary").json()["status"]["recent_activity"] == []


def test_the_overview_summarises_the_trash_without_listing_it(
    client: TestClient, enricher: Enricher
) -> None:
    """Three numbers, not every batch's manifest."""
    trash = client.get("/api/health/summary").json()["trash"]
    listing = client.get("/api/library/trash").json()

    assert set(trash) == {"total", "size_bytes", "path"}
    assert trash["total"] == listing["total"]
    assert trash["path"] == listing["path"]


# ---------------------------------------------------------------------------
# G33 — which sources a review row can be identified against
# ---------------------------------------------------------------------------
def test_an_identifiable_row_names_its_source(client: TestClient) -> None:
    """The picker needs a source to search, and that is the same rule as whether
    to draw the button at all. Publishing only the flag left the client working
    the other half out for itself."""
    rows = review_rows(client)

    assert rows["musicbrainz"]["identify_sources"] == ["musicbrainz"]
    assert rows["deezer"]["identify_sources"] == ["deezer"]


def test_a_row_nobody_can_act_on_is_not_listed(client: TestClient) -> None:
    """The Cover Art Archive is keyed by MusicBrainz's release MBID: there is no
    id to type, no column to put one in, and no other rung's picker that stands
    in for it. It is waiting on an id another rung will hand it, which is not a
    to-do — so it is kept off the list entirely rather than shown unpressable.

    Measured on a real library, two thirds of this list was rows like this one,
    and a list you cannot act on two thirds of is a list people stop reading."""
    assert "coverartarchive" not in review_rows(client)


def test_an_acoustid_ambiguity_is_answered_on_musicbrainz(
    client: TestClient,
) -> None:
    """AcoustID takes no id of its own, and judging it by that alone hid real
    work: its ``ambiguous`` says *several releases explain this directory
    equally well*, and those releases are MusicBrainz releases. The decision is
    available to a person; it is simply recorded against MusicBrainz."""
    row = review_rows(client)["acoustid"]

    assert row["is_actionable"] is True
    assert row["identify_sources"] == ["musicbrainz"]


def test_a_delegated_source_is_still_dead_outside_its_states(
    client: TestClient,
) -> None:
    """The delegation names the states it holds for, because the state matters
    as much as the source. An AcoustID ``no_key`` means *no files to
    fingerprint*, which no id anybody types can fix — so it gets no picker and,
    like the Cover Art Archive row, no place on the list."""
    item = ReviewItem(
        entity_type="album",
        entity_id="owned",
        source="acoustid",
        state="no_key",
        name="Dust Bowl",
    )

    assert item.identify_sources == ()
    assert item.is_actionable is False


def test_identify_sources_agrees_with_is_actionable(client: TestClient) -> None:
    """Two halves of one rule; asserted as an invariant over every row rather
    than as equality on one seed."""
    for row in review_rows(client).values():
        assert bool(row["identify_sources"]) is row["is_actionable"]


def test_the_vocabulary_is_published_too(client: TestClient) -> None:
    """``/api/meta`` carries the whole set, so a screen can reason about sources
    it has no review row for."""
    assert client.get("/api/meta").json()["identifiable_sources"] == [
        "deezer",
        "musicbrainz",
    ]


# ---------------------------------------------------------------------------
# G34 — the "why" is authored beside the matcher
# ---------------------------------------------------------------------------
def test_every_review_row_explains_its_state(client: TestClient) -> None:
    """The explanation is domain knowledge, not copy: ``ambiguous`` means the
    matcher found more than one record that fit and refused to pick. Written in
    a template, it was free to keep describing something the matcher had stopped
    doing."""
    for row in review_rows(client).values():
        assert row["state_explanation"]


def test_the_explanation_is_about_the_state_not_the_row(client: TestClient) -> None:
    """``reason`` is this rung's free-form detail about this row; the
    explanation is what the state itself claims. Two rows in one state say the
    same thing about it and different things about themselves."""
    rows = review_rows(client)

    assert (
        rows["musicbrainz"]["state_explanation"]
        == rows["acoustid"]["state_explanation"]
    )
    assert rows["musicbrainz"]["reason"] != rows["acoustid"]["reason"]


def test_ambiguous_and_no_key_do_not_say_the_same_thing(client: TestClient) -> None:
    """One is "more than one matched", the other is "nothing to match on". They
    lead to different actions, and the review list is the only place either is
    ever explained."""
    rows = review_rows(client)

    assert (
        rows["musicbrainz"]["state_explanation"] != rows["deezer"]["state_explanation"]
    )
