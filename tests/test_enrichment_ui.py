"""The screens enrichment adds, and the two read-model rules behind them.

HTTP tests against a scratch database, no network. The enricher is monkeypatched
in by both the routes-module attribute *and* the ``app.api.deps`` string path,
because the locators resolve at call time.

The properties that matter here are less about markup than about honesty:

* **completeness is three-valued.** A release the disk scan adopted has no track
  rows to count, and reporting that as 0% would show a complete album as empty.
* **editions group by release group, falling back to normalised title** — which
  is what makes grouping work at all for the majority of a library that nothing
  has identified yet.
* **the review list is the visible half of "exact or nothing".** Without it, a
  matcher refusing to guess is indistinguishable from a matcher doing nothing.
* **a page and the fragment that replaces it build the same context**, or the
  screen rearranges itself a few seconds after it loads.
"""

from __future__ import annotations

import asyncio
from datetime import date
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app.api import deps, routes_api, routes_ui
from app.config import get_settings
from app.core.enricher import Enricher
from app.db import get_session
from app.enrich.types import Candidate
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
    Track,
    TrackStatus,
)

ARTIST_ID = "312829"
MBID = "984f8239-8fe1-4683-9c54-10ffb14439e9"
GROUP = "45017130-d783-4a09-97e1-780a3fd40382"


@pytest.fixture(name="client")
def client_fixture(tmp_path: Any) -> Iterator[TestClient]:
    """One artist, four releases: two editions of one record, plus two others."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'ui.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        """Exactly what ``app.db.get_session`` does — and no commit.

        A fixture that committed for the routes would hide every route that
        forgot to: ``identify`` shipped writing through this session and never
        committing, which made a manual identification a green toast and a
        rolled-back transaction. Mirroring production is what catches that.
        """
        session = maker()
        try:
            yield session
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Joe Bonamassa", image_url=None))
            session.add(
                ArtistMetadata(
                    artist_id=ARTIST_ID,
                    mb_artist_mbid=MBID,
                    isni="0000000115034211",
                    artist_type="Person",
                    country="US",
                    life_span_begin="1977-05-08",
                    genres="blues,blues rock",
                    bio="An American blues rock guitarist.",
                    bio_source_url="https://en.wikipedia.org/wiki/Joe_Bonamassa",
                    bio_licence="CC BY-SA 4.0",
                    deezer_artist_id="1424",
                )
            )

            # Two editions of one record, sharing a release group.
            for index, (album_id, title, tracks) in enumerate(
                (("std", "Blues Of Desperation", 11), ("dlx", "Blues Of Desperation", 13))
            ):
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=title,
                        version="Deluxe" if index else None,
                        release_date=date(2016, 3, 25),
                        tracks_count=tracks,
                        status=AlbumStatus.WANTED,
                    )
                )
                session.add(
                    AlbumMetadata(
                        album_id=album_id,
                        mb_release_group_mbid=GROUP,
                        barcode="0804879535645",
                        catalog_number="JRA-2016",
                        genres="blues rock",
                        deezer_album_id="12195560",
                    )
                )

            # Downloaded, half its tracks present.
            session.add(
                Album(
                    id="part",
                    artist_id=ARTIST_ID,
                    title="Dust Bowl",
                    tracks_count=4,
                    status=AlbumStatus.DOWNLOADED,
                    path="/music/Joe Bonamassa/Dust Bowl",
                )
            )
            for number in range(1, 5):
                session.add(
                    Track(
                        id=f"dust-{number}",
                        album_id="part",
                        title=f"Track {number}",
                        track_number=number,
                        media_number=1,
                        status=(
                            TrackStatus.DOWNLOADED if number <= 2 else TrackStatus.PENDING
                        ),
                        path=f"/music/x/{number}.flac" if number <= 2 else None,
                    )
                )

            # Downloaded but adopted from disk: no track rows at all.
            session.add(
                Album(
                    id="adopted",
                    artist_id=ARTIST_ID,
                    title="Sloe Gin",
                    tracks_count=10,
                    status=AlbumStatus.DOWNLOADED,
                    path="/music/Joe Bonamassa/Sloe Gin",
                )
            )

            # Both unresolved rows sit on albums that are on disk: enrichment is
            # scoped to the library, so a row for a release nobody owns is
            # purged rather than put in front of a person. ``std`` deliberately
            # has none for the same reason — it is wanted, not owned.
            for entity, entity_id, state, error in (
                (EnrichmentEntity.ALBUM, "adopted", "ambiguous", "two releases matched"),
                (EnrichmentEntity.ALBUM, "part", "no_key", "no barcode"),
                (EnrichmentEntity.ARTIST, ARTIST_ID, "not_found", "nothing matched"),
            ):
                session.add(
                    EnrichmentState(
                        entity_type=entity,
                        entity_id=entity_id,
                        source=EnrichmentSource.MUSICBRAINZ,
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
    """A wired-up enricher with no providers — enough for identify and status.

    Its settings are pinned rather than taken from ``get_settings()``: the gate
    readouts are computed from ``ENRICHMENT_CONTACT`` and ``ACOUSTID_API_KEY``,
    and a developer who has both configured would otherwise watch this file fail
    on their machine and pass in CI.
    """
    settings = get_settings().model_copy(
        update={"enrichment_contact": "", "acoustid_api_key": ""}
    )
    instance = Enricher([], settings=settings)
    monkeypatch.setattr(routes_api, "get_enricher", lambda: instance)
    monkeypatch.setattr("app.api.deps.get_enricher", lambda: instance)
    return instance


def squeeze(text: str) -> str:
    return " ".join(text.split())


# ---------------------------------------------------------------------------
# Completeness
# ---------------------------------------------------------------------------
def test_a_half_present_album_reports_its_real_count(client: TestClient) -> None:
    payload = client.get("/api/albums/part").json()

    assert payload["tracks_on_disk"] == 2
    assert payload["complete"] is False


def test_an_album_adopted_from_disk_is_complete_not_empty(client: TestClient) -> None:
    """It has no track rows to count. Zero would report it as empty, which is the
    one wrong answer — the disk scan already proved the files are there."""
    payload = client.get("/api/albums/adopted").json()

    assert payload["tracks_on_disk"] is None
    assert payload["complete"] is True


def test_an_undownloaded_album_reports_unknown(client: TestClient) -> None:
    payload = client.get("/api/albums/std").json()

    assert payload["tracks_on_disk"] is None
    assert payload["complete"] is None


def test_the_artist_page_shows_a_meter_and_the_adopted_case(
    client: TestClient
) -> None:
    body = client.get(f"/legacy/artists/{ARTIST_ID}").text

    assert 'class="meter"' in body
    assert "adopted from disk" in body


# ---------------------------------------------------------------------------
# Release groups
# ---------------------------------------------------------------------------
def test_editions_share_a_release_group_key(client: TestClient) -> None:
    standard = client.get("/api/albums/std").json()
    deluxe = client.get("/api/albums/dlx").json()

    assert standard["release_group_key"] == GROUP == deluxe["release_group_key"]


def test_an_unidentified_release_falls_back_to_its_normalised_title(
    client: TestClient
) -> None:
    """Which is what makes grouping work for the majority nothing has matched."""
    payload = client.get("/api/albums/part").json()

    assert payload["release_group_key"] == "dust bowl"


def test_the_release_group_page_lists_every_edition(client: TestClient) -> None:
    body = client.get(f"/legacy/release-groups/{GROUP}").text

    assert "Deluxe" in body
    assert "2 edition(s)" in squeeze(body)


def test_an_unknown_release_group_is_a_404(client: TestClient) -> None:
    assert client.get("/legacy/release-groups/nothing-here").status_code == 404


def test_the_edition_monitor_toggle_swaps_this_page_not_another(
    client: TestClient,
) -> None:
    """Every album table posts to one endpoint, so the ``view`` it carries has
    to name a table this page actually has — and the target has to exist."""
    page = client.get(f"/legacy/release-groups/{GROUP}").text
    assert 'id="edition-rows"' in page
    assert "view=release-group" in page

    response = client.post(
        f"/legacy/ui/albums/std/monitor?view=release-group&key={GROUP}",
        headers={"HX-Request": "true"},
    )

    assert response.status_code == 200
    # The fragment that comes back is this page's rows: both editions, and the
    # toggle still pointing at the table it was pressed in.
    assert "Deluxe" in response.text
    assert "#edition-rows" in response.text


def test_the_release_page_shows_the_other_edition(client: TestClient) -> None:
    body = client.get("/legacy/albums/std").text

    assert "Other editions" in body
    assert "this one" in body


# ---------------------------------------------------------------------------
# The release page
# ---------------------------------------------------------------------------
def test_the_release_page_shows_the_identifiers(client: TestClient) -> None:
    body = client.get("/legacy/albums/std").text

    assert "0804879535645" in body, "barcode"
    assert "JRA-2016" in body, "catalogue number"
    assert f"musicbrainz.org/release-group/{GROUP}" in body
    assert "deezer.com/album/12195560" in body


def test_a_release_with_no_tracklist_says_why(client: TestClient) -> None:
    body = client.get("/legacy/albums/adopted").text

    assert "No per-track record" in body


def test_an_unknown_release_is_a_404(client: TestClient) -> None:
    assert client.get("/legacy/albums/nope").status_code == 404


# ---------------------------------------------------------------------------
# The artist page
# ---------------------------------------------------------------------------
def test_the_artist_page_shows_the_identity(client: TestClient) -> None:
    body = client.get(f"/legacy/artists/{ARTIST_ID}").text

    assert "0000000115034211" in body, "ISNI"
    assert MBID in body
    assert "1977-05-08" in body
    assert ">blues rock<" in body


def test_a_biography_is_never_shown_without_its_licence(client: TestClient) -> None:
    """CC BY-SA is share-alike. The attribution is a licence term, not a nicety."""
    body = client.get(f"/legacy/artists/{ARTIST_ID}").text

    assert "An American blues rock guitarist." in body
    assert "en.wikipedia.org/wiki/Joe_Bonamassa" in body
    assert "CC BY-SA 4.0" in body


def test_external_links_are_offered(client: TestClient) -> None:
    body = client.get(f"/legacy/artists/{ARTIST_ID}").text

    assert "musicbrainz.org/artist/" in body
    assert "deezer.com/artist/1424" in body


# ---------------------------------------------------------------------------
# The enrichment page
# ---------------------------------------------------------------------------
def test_the_review_list_holds_what_the_matchers_refused(client: TestClient) -> None:
    payload = client.get("/api/enrichment/review").json()

    states = {item["state"] for item in payload["items"]}
    assert states == {"ambiguous", "no_key"}
    assert payload["total"] == 2


def test_not_found_is_not_on_the_review_list(client: TestClient) -> None:
    """The upstream has no such record yet. That is waiting, not deciding."""
    payload = client.get("/api/enrichment/review").json()

    assert all(item["state"] != "not_found" for item in payload["items"])


def test_review_items_are_named_not_bare_ids(client: TestClient) -> None:
    """"album adopted is ambiguous" is not something anyone can act on."""
    items = client.get("/api/enrichment/review").json()["items"]
    by_id = {item["entity_id"]: item for item in items}

    assert by_id["adopted"]["name"] == "Sloe Gin"
    assert by_id["adopted"]["artist_name"] == "Joe Bonamassa"
    assert by_id["adopted"]["reason"] == "two releases matched"


def test_the_review_count_reaches_the_page(
    client: TestClient, enricher: Enricher
) -> None:
    """Otherwise "exact or nothing" is indistinguishable from silent failure."""
    body = client.get("/legacy/enrichment").text

    assert "2 unresolved" in squeeze(body)


def test_the_page_reports_which_rungs_are_gated(
    client: TestClient, enricher: Enricher
) -> None:
    body = client.get("/legacy/enrichment").text

    assert "gated" in body
    assert "ACOUSTID_API_KEY" in body, "and says exactly what is missing"


def test_a_page_with_no_enricher_says_so_rather_than_erroring(
    client: TestClient
) -> None:
    """Startup is forgiving everywhere else; this page is no exception."""
    response = client.get("/legacy/enrichment")

    assert response.status_code == 200
    assert "switched off" in response.text


def test_the_page_and_its_fragment_agree(
    client: TestClient, enricher: Enricher
) -> None:
    """A different context would make the screen rearrange itself after loading."""
    page = squeeze(client.get("/legacy/enrichment").text)
    fragment = squeeze(client.get("/legacy/partials/enrichment").text)

    assert fragment
    assert fragment in page


# ---------------------------------------------------------------------------
# Identifying by hand — the escape hatch
# ---------------------------------------------------------------------------
def test_a_pasted_mbid_is_recorded_and_reopens_the_row(
    client: TestClient, enricher: Enricher
) -> None:
    # ``adopted`` rather than ``std``: the review list holds what is in the
    # library, and identifying is how a row leaves it.
    response = client.post(
        "/api/enrichment/album/adopted/identify",
        json={"source": "musicbrainz", "external_id": MBID},
    )

    assert response.status_code == 200
    assert client.get("/api/albums/adopted").json()["mb_release_mbid"] == MBID
    # Back in the queue rather than left ambiguous.
    assert client.get("/api/enrichment/review").json()["total"] == 1


def test_an_identification_survives_the_request_that_made_it(
    client: TestClient, enricher: Enricher
) -> None:
    """The route runs on the request-scoped session, which never commits. Told
    "recorded" and then rolling it back leaves the row exactly where it was."""
    client.post(
        "/api/enrichment/album/std/identify",
        json={"source": "musicbrainz", "external_id": MBID},
    )

    # A different request, so a different session: nothing is being read out of
    # the identity map that wrote it.
    assert client.get("/api/albums/std").json()["mb_release_mbid"] == MBID
    events = [row["event"] for row in client.get("/api/activity").json()["items"]]
    assert "enrichment.identified" in events


def test_identifying_something_that_is_gone_is_a_404_not_a_500(
    client: TestClient, enricher: Enricher
) -> None:
    """Unfollowing an artist leaves its review rows behind. Pressing Set on one
    used to fail the foreign key and render the raw INSERT to the user."""
    response = client.post(
        "/api/enrichment/artist/does-not-exist/identify",
        json={"source": "musicbrainz", "external_id": MBID},
    )

    assert response.status_code == 404
    assert "INSERT" not in response.text


def test_a_source_with_no_id_of_its_own_cannot_be_identified(
    client: TestClient, enricher: Enricher
) -> None:
    """Cover art is keyed by MusicBrainz's id and Wikidata by the QID it
    publishes. A typed value would land in the Deezer column, which means
    something else entirely."""
    response = client.post(
        "/api/enrichment/album/std/identify",
        json={"source": "coverartarchive", "external_id": "12345"},
    )

    assert response.status_code == 400
    assert client.get("/api/albums/std").json()["deezer_album_id"] == "12195560", (
        "the Deezer id it would have been written into is untouched"
    )


def test_a_malformed_identifier_is_refused_with_a_reason(
    client: TestClient, enricher: Enricher
) -> None:
    response = client.post(
        "/api/enrichment/album/std/identify",
        json={"source": "musicbrainz", "external_id": "not-a-uuid"},
    )

    assert response.status_code == 400
    assert "MusicBrainz id" in response.json()["error"]
    assert client.get("/api/albums/std").json()["mb_release_mbid"] is None


def test_an_empty_identifier_is_refused(client: TestClient, enricher: Enricher) -> None:
    response = client.post(
        "/api/enrichment/album/std/identify",
        json={"source": "musicbrainz", "external_id": "   "},
    )

    assert response.status_code == 400


def test_the_html_form_reports_a_refusal_without_a_500(
    client: TestClient, enricher: Enricher
) -> None:
    response = client.post(
        "/legacy/ui/enrichment/album/std/identify",
        data={"source": "musicbrainz", "external_id": "nonsense"},
    )

    assert response.status_code == 200
    assert "MusicBrainz id" in response.text


def test_identifying_without_a_running_enricher_is_a_503(client: TestClient) -> None:
    response = client.post(
        "/api/enrichment/artist/312829/identify",
        json={"source": "musicbrainz", "external_id": MBID},
    )

    assert response.status_code == 503


# ---------------------------------------------------------------------------
# The picker: identifying something without knowing its id
# ---------------------------------------------------------------------------
class FakeSearchable:
    """A provider that answers ``candidates`` and records what it was asked.

    Deliberately not a real one: what is under test here is the route, the panel
    and where the panel lives on the page, none of which should care whether the
    upstream is Deezer or MusicBrainz.
    """

    source = EnrichmentSource.MUSICBRAINZ

    def __init__(self, items: list[Candidate] | None = None) -> None:
        self.items = items or [
            Candidate(
                external_id=MBID,
                title="Blues of Desperation",
                subtitle="Joe Bonamassa",
                detail="2016 · 11 tracks · barcode 804879535645",
                disambiguation="deluxe edition",
                image_url="https://coverartarchive.example/front-250",
                url="https://musicbrainz.org/release/" + MBID,
            )
        ]
        self.calls: list[str | None] = []

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        return True

    def is_ready(self) -> bool:
        return True

    async def fetch(self, job: Any) -> Any:  # pragma: no cover - never called
        raise AssertionError("the automatic path must not run in these tests")

    async def candidates(self, job: Any, query: str | None = None) -> list[Candidate]:
        self.calls.append(query)
        return self.items

    async def aclose(self) -> None:
        return None


@pytest.fixture(name="searchable")
def searchable_fixture(monkeypatch: pytest.MonkeyPatch) -> FakeSearchable:
    provider = FakeSearchable()
    settings = get_settings().model_copy(
        update={"enrichment_contact": "", "acoustid_api_key": ""}
    )
    instance = Enricher([provider], settings=settings)
    monkeypatch.setattr(routes_api, "get_enricher", lambda: instance)
    monkeypatch.setattr("app.api.deps.get_enricher", lambda: instance)
    return provider


def test_candidates_are_offered_as_records_not_as_ids(
    client: TestClient, searchable: FakeSearchable
) -> None:
    """The review list used to ask for 984f8239-8fe1-… The only people who can
    answer that are the ones who never needed the review list."""
    body = client.get(
        "/api/enrichment/album/std/candidates", params={"source": "musicbrainz"}
    ).json()

    assert [item["external_id"] for item in body["items"]] == [MBID]
    card = body["items"][0]
    assert card["title"] == "Blues of Desperation"
    assert card["subtitle"] == "Joe Bonamassa"
    assert card["disambiguation"] == "deluxe edition"
    assert "barcode" in card["detail"]


def test_a_typed_query_reaches_the_provider(
    client: TestClient, searchable: FakeSearchable
) -> None:
    client.get(
        "/api/enrichment/album/std/candidates",
        params={"source": "musicbrainz", "q": "wagner overtures"},
    )

    assert searchable.calls == ["wagner overtures"]


def test_the_picker_panel_renders_the_cards(
    client: TestClient, searchable: FakeSearchable
) -> None:
    html = client.get(
        "/legacy/partials/enrichment/album/std/candidates",
        params={"source": "musicbrainz", "name": "Blues Of Desperation"},
        headers={"HX-Request": "true"},
    ).text

    assert "Blues of Desperation" in html
    assert "deluxe edition" in html
    assert ">This one<" in squeeze(html).replace("> This one <", ">This one<")
    assert f'name="external_id" value="{MBID}"' in html
    # The artwork guess is allowed to 404 — that is a missing image, not a broken
    # one, so the element removes itself and the placeholder shows through.
    assert 'onerror="this.remove()"' in html


def test_the_picker_lives_outside_the_polled_region(
    client: TestClient, searchable: FakeSearchable
) -> None:
    """``#enrichment-body`` re-fetches itself every fifteen seconds. A picker
    inside it would be swapped away mid-read, on average halfway through
    choosing — so the panel is a sibling, and the poll cannot reach it."""
    page = client.get("/legacy/enrichment").text

    panel_at = page.index('id="identify-panel"')
    body_at = page.index('id="enrichment-body"')
    assert panel_at < body_at, "the panel must not be nested in the polled region"

    body = page[body_at:]
    assert 'id="identify-panel"' not in body
    assert 'hx-trigger="every 15s' in body


def test_opening_the_picker_claims_nothing_changed(
    client: TestClient, searchable: FakeSearchable
) -> None:
    """``qobuzarr:refresh`` means "something changed" and every live region on
    the page acts on it. A GET that fired it would make each region refresh every
    other region, forever."""
    for path in (
        "/partials/enrichment/album/std/candidates?source=musicbrainz",
        "/partials/enrichment/identify",
    ):
        response = client.get(path, headers={"HX-Request": "true"})
        assert response.status_code == 200
        assert "HX-Trigger" not in response.headers, path


def test_closing_the_picker_empties_it(
    client: TestClient, searchable: FakeSearchable
) -> None:
    """Closing is a swap like any other rather than a special case in script."""
    assert client.get("/legacy/partials/enrichment/identify").text.strip() == ""


def test_picking_a_card_records_it_and_says_so(
    client: TestClient, searchable: FakeSearchable
) -> None:
    response = client.post(
        "/legacy/ui/enrichment/album/std/identify",
        data={"source": "musicbrainz", "external_id": MBID, "name": "Blues"},
        headers={"HX-Request": "true"},
    )

    assert response.status_code == 200
    # The panel reports the outcome; the review list refreshes itself, because
    # this fires the event #enrichment-body already listens for.
    assert "note--ok" in response.text
    assert "qobuzarr:refresh" in response.headers["HX-Trigger"]
    assert client.get("/api/albums/std").json()["mb_release_mbid"] == MBID


def test_a_failed_pick_keeps_the_panel_open(
    client: TestClient, searchable: FakeSearchable
) -> None:
    """Dropping someone back to square one after a bad id is the one thing worse
    than the id being bad."""
    response = client.post(
        "/legacy/ui/enrichment/album/std/identify",
        data={"source": "musicbrainz", "external_id": "nonsense", "name": "Blues"},
        headers={"HX-Request": "true"},
    )

    assert response.status_code == 200
    assert "note--warn" in response.text
    assert "MusicBrainz id" in response.text
    assert 'id="identify-card"' in response.text


def test_a_pasted_url_is_an_identifier(
    client: TestClient, searchable: FakeSearchable
) -> None:
    """People copy links, not ids. Rejecting a URL that visibly contains the id
    it is being rejected for is true, unhelpful, and was the previous answer."""
    response = client.post(
        "/api/enrichment/album/std/identify",
        json={
            "source": "musicbrainz",
            "external_id": f"https://musicbrainz.org/release/{MBID}",
        },
    )

    assert response.status_code == 200
    assert client.get("/api/albums/std").json()["mb_release_mbid"] == MBID


def test_a_deezer_link_yields_a_deezer_id(
    client: TestClient, searchable: FakeSearchable
) -> None:
    """Including the locale segment Deezer puts in shared links."""
    response = client.post(
        "/api/enrichment/album/std/identify",
        json={"source": "deezer", "external_id": "https://www.deezer.com/en/album/302127"},
    )

    assert response.status_code == 200
    assert client.get("/api/albums/std").json()["deezer_album_id"] == "302127"


def test_a_source_with_nothing_to_search_offers_no_picker(
    client: TestClient, searchable: FakeSearchable
) -> None:
    """Cover art is keyed by an MBID and Wikidata by a QID. A search box that
    cannot lead anywhere is worse than no search box."""
    response = client.get(
        "/api/enrichment/album/std/candidates", params={"source": "coverartarchive"}
    )

    assert response.status_code == 400
    assert "MusicBrainz or Deezer" in response.json()["error"]


def test_an_unknown_source_lists_the_real_ones(
    client: TestClient, searchable: FakeSearchable
) -> None:
    response = client.get(
        "/api/enrichment/album/std/candidates", params={"source": "banana"}
    )

    assert response.status_code == 400
    assert "musicbrainz" in response.json()["error"]


def test_candidates_for_something_gone_is_a_404(
    client: TestClient, searchable: FakeSearchable
) -> None:
    response = client.get(
        "/api/enrichment/album/vanished/candidates", params={"source": "musicbrainz"}
    )

    assert response.status_code == 404


def test_the_review_row_opens_the_picker_rather_than_asking_for_an_id(
    client: TestClient, enricher: Enricher
) -> None:
    html = client.get("/legacy/enrichment").text

    assert "Find it…" in html
    assert 'hx-target="#identify-panel"' in html
    assert 'name="external_id"' not in html, "the bare id box is gone from the list"


# ---------------------------------------------------------------------------
# Reject — the other way off the review list
# ---------------------------------------------------------------------------
def test_a_rejection_survives_the_request_that_made_it(
    client: TestClient, enricher: Enricher
) -> None:
    """Same rule as identify, and the same bug if it is broken: the route runs on
    the request-scoped session, which never commits. A rejection that rolled back
    would be a green toast over a row that is still sitting there."""
    response = client.post(
        "/api/enrichment/album/adopted/reject",
        json={"source": "musicbrainz", "reason": "self-released, not in MusicBrainz"},
    )

    assert response.status_code == 200
    # A different request, so a different session.
    rows = client.get("/api/enrichment/review", params={"state": "rejected"}).json()
    assert [row["entity_id"] for row in rows["items"]] == ["adopted"]
    assert rows["items"][0]["reason"] == "self-released, not in MusicBrainz"
    events = [row["event"] for row in client.get("/api/activity").json()["items"]]
    assert "enrichment.rejected" in events


def test_a_rejected_row_leaves_the_list_and_the_badge_together(
    client: TestClient, enricher: Enricher
) -> None:
    """The badge and the page it links to must agree, which is why both read
    ``enricher.REVIEW_STATES`` rather than a literal pair of their own."""
    before = client.get("/api/nav-counts").json()["enrichment_review"]
    assert before == 2

    client.post(
        "/api/enrichment/album/adopted/reject", json={"source": "musicbrainz"}
    )

    assert client.get("/api/enrichment/review").json()["total"] == 1
    assert client.get("/api/nav-counts").json()["enrichment_review"] == 1


def test_rejecting_a_row_that_is_gone_is_a_404(
    client: TestClient, enricher: Enricher
) -> None:
    """The list outlives what it is about: unfollowing an artist leaves rows
    behind until the nightly purge."""
    response = client.post(
        "/api/enrichment/album/adopted/reject", json={"source": "deezer"}
    )

    assert response.status_code == 404
    assert "INSERT" not in response.text


def test_rejecting_a_row_nobody_was_asked_about_is_a_400(
    client: TestClient, enricher: Enricher
) -> None:
    """``not_found`` is waiting on the upstream, not on a decision — it is never
    put in front of a person, so there is nothing to dismiss."""
    response = client.post(
        f"/api/enrichment/artist/{ARTIST_ID}/reject", json={"source": "musicbrainz"}
    )

    assert response.status_code == 400
    assert "ambiguous" in response.json()["error"]
    assert client.get("/api/enrichment/review").json()["total"] == 2


def test_rejecting_with_an_unknown_source_lists_the_real_ones(
    client: TestClient, enricher: Enricher
) -> None:
    response = client.post(
        "/api/enrichment/album/adopted/reject", json={"source": "banana"}
    )

    assert response.status_code == 400
    assert "musicbrainz" in response.json()["error"]


def test_rejecting_with_an_unknown_entity_type_lists_the_real_ones(
    client: TestClient, enricher: Enricher
) -> None:
    response = client.post(
        "/api/enrichment/albun/adopted/reject", json={"source": "musicbrainz"}
    )

    assert response.status_code == 400
    assert "album" in response.json()["error"]


def test_a_reject_body_must_name_its_source(
    client: TestClient, enricher: Enricher
) -> None:
    """A work item is one (entity, source) pair. An omitted source would have to
    mean *all* of them, which is a much bigger press than the button sending it."""
    response = client.post("/api/enrichment/album/adopted/reject", json={})

    assert response.status_code == 422


def test_reopening_undoes_a_rejection(client: TestClient, enricher: Enricher) -> None:
    """Nothing re-arms a rejection, so this is the only route back — and it has
    to commit for the same reason the rejection did."""
    client.post(
        "/api/enrichment/album/adopted/reject",
        json={"source": "musicbrainz", "reason": "changed my mind in a moment"},
    )
    assert client.get("/api/enrichment/review").json()["total"] == 1

    response = client.post(
        "/api/enrichment/album/adopted/reopen", params={"source": "musicbrainz"}
    )

    assert response.status_code == 200
    rows = client.get("/api/enrichment/review", params={"state": "pending"}).json()
    assert [row["entity_id"] for row in rows["items"]] == ["adopted"]


def test_reopening_something_that_is_gone_is_a_404(
    client: TestClient, enricher: Enricher
) -> None:
    """``_reopen`` *creates* the row when there is none, so an unchecked id would
    be answered "Queued 5 source(s)" over five rows about nothing — work
    ``_claim`` never touches (it is held to ``in_library()``) and only the
    nightly purge clears. The same check ``identify`` and ``accept`` do."""
    response = client.post("/api/enrichment/album/vanished/reopen")

    assert response.status_code == 404
    listed = client.get(
        "/api/enrichment/review", params={"state": "pending"}
    ).json()
    assert [row["entity_id"] for row in listed["items"]] == []


def test_the_rejected_state_is_published_to_the_client(client: TestClient) -> None:
    """``MetaOut`` is where a client gets its vocabulary; a state it has no name
    for renders as whatever its default branch does."""
    assert "rejected" in client.get("/api/meta").json()["enrichment_states"]


# ---------------------------------------------------------------------------
# Accept — applies what is held, and never searches
# ---------------------------------------------------------------------------
def test_accept_applies_the_release_type_the_sources_agreed_on(
    client: TestClient, enricher: Enricher
) -> None:
    assert client.get("/api/albums/adopted").json()["release_type"] == "album"

    _propose_release_type(client, "adopted", "ep")
    listed = client.get("/api/enrichment/review").json()["items"]
    assert [
        row["suggested_release_type"]
        for row in listed
        if row["entity_id"] == "adopted"
    ] == ["ep"], "the list says what the button will do, before it is pressed"

    response = client.post("/api/enrichment/album/adopted/accept")
    payload = response.json()

    assert response.status_code == 200
    assert (payload["applied"], payload["proposal"]) == (True, "release_type")
    assert (payload["previous_value"], payload["applied_value"]) == ("album", "ep")
    assert payload["level"] == "success"
    # Committed, and read back through a different session.
    after = client.get("/api/albums/adopted").json()
    assert after["release_type"] == "ep"
    assert after["qobuz_release_type"] == "album", "the reversal point is kept"
    assert after["status"] == "downloaded", "one column; no download was created"


def test_accept_with_nothing_held_opens_the_picker_instead(
    client: TestClient, enricher: Enricher
) -> None:
    """The refusal branch, and the whole reason this endpoint is safe. The
    tempting implementation searches the upstream and applies the top hit, which
    is what "a name may reject a candidate, never select one" forbids."""
    response = client.post("/api/enrichment/album/part/accept")
    payload = response.json()

    assert response.status_code == 200
    assert payload["applied"] is False
    assert payload["proposal"] is None
    assert (payload["previous_value"], payload["applied_value"]) == (None, None)
    assert payload["identify_sources"] == ["musicbrainz"]
    assert payload["level"] == "info", "not applying is the answer, not a warning"


def test_accept_leaves_the_row_on_the_review_list(
    client: TestClient, enricher: Enricher
) -> None:
    """The release type and the match are different questions. Marking a row
    resolved because somebody accepted a type is the failure this layer exists
    to avoid."""
    _propose_release_type(client, "adopted", "ep")

    client.post("/api/enrichment/album/adopted/accept")

    assert client.get("/api/enrichment/review").json()["total"] == 2


def test_accept_on_something_that_is_gone_is_a_404(
    client: TestClient, enricher: Enricher
) -> None:
    response = client.post("/api/enrichment/album/vanished/accept")

    assert response.status_code == 404


def test_accept_refuses_a_release_type_this_version_cannot_read(
    client: TestClient, enricher: Enricher
) -> None:
    """A value nothing downstream understands must not reach
    ``Album.release_type``: it feeds ``Artist.accepts()`` and the naming
    templates."""
    _propose_release_type(client, "adopted", "boxset")

    response = client.post("/api/enrichment/album/adopted/accept")

    assert response.status_code == 400
    assert "release type" in response.json()["error"]
    assert client.get("/api/albums/adopted").json()["release_type"] == "album"


def test_accept_with_an_unknown_entity_type_is_a_400(
    client: TestClient, enricher: Enricher
) -> None:
    response = client.post("/api/enrichment/albun/adopted/accept")

    assert response.status_code == 400
    assert "artist" in response.json()["error"]


def _propose_release_type(client: TestClient, album_id: str, value: str) -> None:
    """Put a consensus proposal on an album the way a tick would.

    Written straight into ``album_metadata`` rather than through a fake ladder:
    the point under test is what Accept does with a stored proposal, and the
    storing is covered by the consensus tests.
    """

    async def go() -> None:
        agen = main.app.dependency_overrides[get_session]()
        session = await agen.__anext__()
        try:
            meta = await session.get(AlbumMetadata, album_id)
            if meta is None:
                meta = AlbumMetadata(album_id=album_id)
                session.add(meta)
            meta.suggested_release_type = value
            await session.commit()
        finally:
            await agen.aclose()

    asyncio.run(go())
