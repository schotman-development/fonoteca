"""The app shell, as JSON: badge counts, banners, vocabularies, configuration.

The React shell renders on four payloads and nothing else — ``/api/nav-counts``,
``/api/banners`` (also carried on ``/api/status``), ``/api/meta`` and
``/api/settings``. Between them they replace ``templates/base.html``,
``partials/nav.html``, ``partials/status_bar.html`` and the whole of
``templates.env.globals``, so the guarantees the markup used to carry have to be
re-stated here or they are simply gone:

* **the badge and the page it links to agree.** ``nav.wanted`` is the *monitored
  backlog* — ``deps.MISSING_STATUSES`` plus ``monitored`` — which is a different
  number from ``library_stats.wanted_albums`` (``WANTED + QUEUED +
  DOWNLOADING``). Both definitions are correct and both are published; the one
  thing that must never happen is a third. The old test compared a rendered
  fragment against a rendered page; this compares two producers of one number.
* **a vocabulary is published, not copied.** Every ``<select>`` and chip in the
  client iterates ``/api/meta``. A hand-kept TypeScript list stays right until
  somebody adds an enum member, and then fails as an option that silently is not
  there.
* **no secret ever reaches a payload.** The old assertion was a substring check
  on one fragment of HTML. This walks the entire JSON tree of ``/api/settings``
  and ``/api/status`` looking for the configured token, app secret, AcoustID key
  and contact address — strictly stronger, and it is the invariant that would
  otherwise be re-broken by one convenient new field.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; nothing here touches the network.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app import config
from app.api import deps
from app.config import get_settings
from app.db import get_session
from app.models import (
    ActivityLevel,
    Album,
    AlbumStatus,
    Artist,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
    MonitorMode,
    QueueItem,
    QueueState,
    RELEASE_TYPES,
    TrackOrigin,
    TrackStatus,
)

ARTIST_ID = "720076"
OTHER_ARTIST = "861312"
OWNED_ID = "cccc3333dddd4"

#: (album id, title, status, monitored) — the backlog the badge counts is the
#: two monitored ``wanted`` rows plus the monitored ``failed`` one. The ignored
#: ``wanted`` release is the trap: it is missing, and nobody asked for it.
SEED_ALBUMS = (
    ("uyej1o165e870", "All Melody", AlbumStatus.WANTED, True),
    ("wxl78pvfqlm3b", "Spaces", AlbumStatus.WANTED, True),
    ("0884977859300", "Screws", AlbumStatus.WANTED, False),
    ("aaaa1111bbbb2", "Felt", AlbumStatus.FAILED, True),
    (OWNED_ID, "Solo", AlbumStatus.DOWNLOADED, True),
    ("eeee5555ffff6", "Wintermusik", AlbumStatus.QUEUED, True),
)

#: What ``nav.wanted`` must report for that seed.
BACKLOG = 3


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """Two artists, six releases, one queue item, two unresolved rungs."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'shell.db'}", poolclass=NullPool
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
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm", monitored=True))
            session.add(Artist(id=OTHER_ARTIST, name="Janine Jansen", monitored=False))
            for album_id, title, status, monitored in SEED_ALBUMS:
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=title,
                        status=status,
                        monitored=monitored,
                        release_type="album",
                        tracks_count=6,
                        path="/music/Nils Frahm/Solo"
                        if status is AlbumStatus.DOWNLOADED
                        else None,
                    )
                )
            session.add(
                QueueItem(album_id="eeee5555ffff6", state=QueueState.PENDING)
            )
            # On disk, so enrichment is in scope for it: the review badge counts
            # over ``in_library()`` because the list it links to does.
            for source, state in (
                (EnrichmentSource.MUSICBRAINZ, "ambiguous"),
                (EnrichmentSource.DEEZER, "no_key"),
                # Neither of these reaches a person: ``not_found`` is waiting,
                # ``ok`` is done.
                (EnrichmentSource.ACOUSTID, "not_found"),
                (EnrichmentSource.WIKIDATA, "ok"),
            ):
                session.add(
                    EnrichmentState(
                        entity_type=EnrichmentEntity.ALBUM,
                        entity_id=OWNED_ID,
                        source=source,
                        state=state,
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


@pytest.fixture(name="secrets")
def secrets_fixture(monkeypatch: pytest.MonkeyPatch) -> Iterator[dict[str, str]]:
    """Configure recognisable secrets, process-wide, and hand them back.

    Environment variables outrank ``.env`` in pydantic-settings, and clearing the
    ``get_settings`` cache makes every module agree — they all hold the same
    cached function. Sentinel values rather than the developer's real ones so
    the search below is deterministic and so a failure names what leaked.
    """
    values = {
        "QOBUZ_USER_AUTH_TOKEN": "SECRET-user-auth-token-a1b2c3",
        "QOBUZ_APP_SECRET": "SECRET-app-secret-d4e5f6",
        "ACOUSTID_API_KEY": "SECRET-acoustid-key-g7h8i9",
        "ENRICHMENT_CONTACT": "SECRET-person@example.invalid",
    }
    for name, value in values.items():
        monkeypatch.setenv(name, value)
    config.get_settings.cache_clear()
    assert get_settings().qobuz_user_auth_token == values["QOBUZ_USER_AUTH_TOKEN"]
    try:
        yield values
    finally:
        config.get_settings.cache_clear()


def strings(payload: Any) -> Iterator[str]:
    """Every string anywhere in a decoded JSON document, keys included.

    Keys as well as values because a payload that leaked a secret as a *key* —
    a ``{token: ...}`` map — would pass a values-only sweep while being exactly
    as public.
    """
    if isinstance(payload, dict):
        for key, value in payload.items():
            yield str(key)
            yield from strings(value)
    elif isinstance(payload, list):
        for item in payload:
            yield from strings(item)
    elif isinstance(payload, str):
        yield payload


# ---------------------------------------------------------------------------
# Nav counts (G1)
# ---------------------------------------------------------------------------
def test_nav_counts_publishes_the_five_badges(client: TestClient) -> None:
    """The sidebar's whole data set, and nothing else in it."""
    body = client.get("/api/nav-counts").json()

    assert set(body) == {"artists", "wanted", "queue", "trash", "enrichment_review"}
    assert all(isinstance(value, int) for value in body.values())


def test_the_backlog_badge_counts_the_monitored_missing(client: TestClient) -> None:
    """``wanted`` + ``failed``, and monitored — an ignored release is not backlog.

    This is one of the two definitions of "wanted" in the codebase and the
    narrower one. The other counts ``QUEUED`` and ``DOWNLOADING`` as well,
    because it is answering "how much is outstanding", not "what can I press
    Download on".
    """
    assert client.get("/api/nav-counts").json()["wanted"] == BACKLOG


def test_the_badge_agrees_with_the_page_it_links_to(client: TestClient) -> None:
    """Cross-producer equality, which is what the old same-context rule becomes.

    The sidebar used to be a live region rendered from the page's own context;
    getting that wrong showed one number on load and a different one twenty
    seconds later. Two endpoints cannot share a context, so they have to agree
    by counting the same thing.
    """
    badge = client.get("/api/nav-counts").json()["wanted"]

    assert client.get("/api/wanted?monitored=true").json()["total"] == badge


def test_the_queue_badge_agrees_with_the_queue_and_the_stats(
    client: TestClient,
) -> None:
    """Three producers of one number: badge, counters, list."""
    badge = client.get("/api/nav-counts").json()["queue"]
    queue = client.get("/api/stats").json()["queue"]

    assert badge == queue["pending"] + queue["active"]
    assert badge == client.get("/api/queue").json()["total"]


def test_the_review_badge_agrees_with_the_review_list(client: TestClient) -> None:
    """A badge promising rows the page cannot show is worse than no badge.

    Both are held to ``ambiguous`` + ``no_key`` and to the library scope, so the
    ``not_found`` row (waiting on an upstream, not on a person) and the ``ok``
    one are counted by neither.
    """
    badge = client.get("/api/nav-counts").json()["enrichment_review"]

    assert badge == 2
    assert badge == client.get("/api/enrichment/review").json()["total"]
    assert badge == client.get("/api/enrichment").json()["review_total"]


def test_an_unwired_enricher_still_publishes_a_zeroed_autonomy(
    client: TestClient,
) -> None:
    """No enricher, no counts — but the field is there and the shape is whole.

    ``enrichment_status`` splats a payload that has no ``autonomy`` key at all
    on this path, so what the client receives is the schema default. That has to
    be six zeroes rather than an absent object: the band reads
    ``autonomy.entities``, and a missing key would be a ``TypeError`` three
    layers from here instead of the honest "nothing has counted the library".
    """
    autonomy = client.get("/api/enrichment").json()["autonomy"]

    assert set(autonomy) == {
        "entities",
        "automatic",
        "waiting_person",
        "waiting_input",
        "dismissed",
        "unstarted",
    }
    assert set(autonomy.values()) == {0}


def test_the_two_wanted_definitions_are_both_published(client: TestClient) -> None:
    """And they differ, on this seed, by the queued release.

    Asserting the *difference* rather than each number is the point: the failure
    this guards against is somebody noticing they disagree and "fixing" it.
    """
    badge = client.get("/api/nav-counts").json()["wanted"]
    library = client.get("/api/stats").json()["library"]["wanted_albums"]

    assert badge == BACKLOG
    assert library == BACKLOG + 1  # the QUEUED release, which is not backlog


def test_the_trash_badge_survives_a_missing_directory(client: TestClient) -> None:
    """``OSError -> 0``. A trash folder on an unmounted disk is not a 500 on
    every screen in the application."""
    assert client.get("/api/nav-counts").json()["trash"] >= 0


def test_the_badges_are_a_read(client: TestClient) -> None:
    """No GET may claim a change — polling one every twenty seconds otherwise
    writes a row every twenty seconds, forever."""
    before = client.get("/api/activity?limit=1000").json()["total"]

    client.get("/api/nav-counts")
    client.get("/api/nav-counts")

    assert client.get("/api/activity?limit=1000").json()["total"] == before


# ---------------------------------------------------------------------------
# Banners (G2)
# ---------------------------------------------------------------------------
BANNER_CODES = {"no_credentials", "no_client", "no_app_secret", "breaker_open"}


def test_banners_are_a_list_of_typed_notices(client: TestClient) -> None:
    body = client.get("/api/banners").json()

    assert isinstance(body, list)
    for banner in body:
        assert set(banner) == {"level", "title", "message", "code"}
        assert banner["level"] in {"info", "warning", "error"}
        assert banner["code"] in BANNER_CODES
        assert banner["title"] and banner["message"]


def test_the_banner_code_is_the_machine_readable_half(client: TestClient) -> None:
    """The prose gets rewritten whenever it reads badly. A client routing on the
    sentence makes the sentence an interface nobody can edit."""
    codes = [banner["code"] for banner in client.get("/api/banners").json()]

    assert len(codes) == len(set(codes)), "one condition, one banner"


def test_missing_credentials_are_an_error_banner(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The one banner that is not a warning: nothing works without these."""
    monkeypatch.setenv("QOBUZ_APP_ID", "")
    monkeypatch.setenv("QOBUZ_USER_AUTH_TOKEN", "")
    config.get_settings.cache_clear()
    try:
        banners = {item["code"]: item for item in client.get("/api/banners").json()}
    finally:
        config.get_settings.cache_clear()

    assert banners["no_credentials"]["level"] == "error"
    assert "QOBUZ_APP_ID" in banners["no_credentials"]["message"]


def test_status_carries_the_same_banners(client: TestClient) -> None:
    """A screen already polling status needs no second request, and the two must
    not be able to disagree about whether anything is wrong."""
    assert client.get("/api/status").json()["banners"] == client.get(
        "/api/banners"
    ).json()


class ExplodingBreaker:
    """A circuit breaker whose probe fails, which is the realistic shape of it:
    the limiter is somebody else's object and it is read on every page."""

    def is_open(self) -> bool:
        raise RuntimeError("the breaker probe blew up")

    def seconds_remaining(self) -> float:
        raise RuntimeError("the breaker probe blew up")


class BrokenLimiter:
    breaker = ExplodingBreaker()


def test_banners_never_raise(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    """A broken probe is an absent banner, not a 500 on every screen.

    The banner stack is the one payload that renders above *everything*, so it
    is the one that must degrade rather than fail — a warning nobody can see is
    a smaller loss than an application nobody can open.
    """
    monkeypatch.setattr(deps, "get_rate_limiter", BrokenLimiter)

    response = client.get("/api/banners")

    assert response.status_code == 200
    assert isinstance(response.json(), list)
    assert "breaker_open" not in {item["code"] for item in response.json()}


# ---------------------------------------------------------------------------
# Meta vocabularies (G11)
# ---------------------------------------------------------------------------
#: Every vocabulary the client iterates, against the enum that owns it. Written
#: as the enum rather than a literal list on purpose: a value added to
#: ``AlbumStatus`` has to show up here without anybody remembering to.
VOCABULARIES = {
    "release_types": list(RELEASE_TYPES),
    "monitor_modes": [item.value for item in MonitorMode],
    "album_statuses": [item.value for item in AlbumStatus],
    "queue_states": [item.value for item in QueueState],
    "activity_levels": [item.value for item in ActivityLevel],
    "track_statuses": [item.value for item in TrackStatus],
    "track_origins": [item.value for item in TrackOrigin],
    "enrichment_sources": [item.value for item in EnrichmentSource],
    "enrichment_entities": [item.value for item in EnrichmentEntity],
}


@pytest.mark.parametrize(("name", "expected"), sorted(VOCABULARIES.items()))
def test_meta_publishes_every_vocabulary(
    client: TestClient, name: str, expected: list[str]
) -> None:
    """Hand-copying these into TypeScript guarantees drift, and the drift is
    silent: a ``<select>`` one option short, or a chip falling through to the
    client's default branch."""
    assert client.get("/api/meta").json()[name] == expected


@pytest.mark.parametrize(
    "name",
    [
        "enrichment_states",
        "review_states",
        "identifiable_sources",
        "integrity_states",
        "fingerprint_states",
        "setting_origins",
    ],
)
def test_meta_publishes_the_derived_vocabularies(client: TestClient, name: str) -> None:
    """These are not plain enum dumps — ``identifiable_sources`` encodes a *rule*
    (which sources have an id a person could type and a column to put it in), and
    a client re-implementing it drifts from the matcher without either side
    noticing."""
    values = client.get("/api/meta").json()[name]

    assert values and all(isinstance(item, str) for item in values)


def test_identifiable_sources_are_real_sources(client: TestClient) -> None:
    meta = client.get("/api/meta").json()

    assert set(meta["identifiable_sources"]) <= set(meta["enrichment_sources"])


def test_review_states_are_real_states_and_are_the_enrichers_own(
    client: TestClient,
) -> None:
    """``review_states`` is a rule, not a vocabulary, and it is *the same tuple*
    the review query filters on, the nav badge counts over and ``reject``
    validates against.

    Publishing a copy would defeat the point. The failure a client-side copy
    produces is silent in the way that matters: a filter chip offering a state
    the endpoint refuses opens an empty pane, and a state the server starts
    putting on the list is a row nobody can find.
    """
    from app.core.enricher import REVIEW_STATES

    meta = client.get("/api/meta").json()

    assert meta["review_states"] == list(REVIEW_STATES)
    assert set(meta["review_states"]) <= set(meta["enrichment_states"])
    # The two the matchers produce that a person can act on — and neither of the
    # two dead ends that are waiting rather than deciding.
    assert "not_found" not in meta["review_states"]
    assert "rejected" not in meta["review_states"]


def test_meta_publishes_the_setting_origin_pair(client: TestClient) -> None:
    """``SettingsOut.origins`` is a ``dict[str, str]`` on the wire and
    ``SettingOrigin`` is a ``Literal``, so this pair reaches the OpenAPI document
    nowhere at all — which makes a hand-typed copy of it in ``types.ts`` the one
    union nothing else could police."""
    from app.config import SETTING_ORIGINS

    meta = client.get("/api/meta").json()

    assert meta["setting_origins"] == list(SETTING_ORIGINS) == ["env", "override"]


def test_every_overridable_key_reports_an_origin_from_that_vocabulary(
    client: TestClient,
) -> None:
    """The two halves of the settings overlay have to agree: every allowlisted
    key reports an origin, always, and it is one of the two words ``/api/meta``
    published. An absent key would make the client guess where a value came
    from, which is the one thing the origin map exists to stop."""
    meta = client.get("/api/meta").json()
    settings = client.get("/api/settings").json()

    assert set(settings["origins"]) == set(settings["overridable"])
    assert set(settings["origins"].values()) <= set(meta["setting_origins"])


def test_format_labels_are_keyed_by_stringified_ints(client: TestClient) -> None:
    """JSON object keys are strings; the format id is an int everywhere else.
    Saying so here is what stops a client doing ``labels[album.format_id]`` and
    getting ``undefined``."""
    labels = client.get("/api/meta").json()["format_labels"]

    assert labels
    for key, value in labels.items():
        assert key.isdigit(), key
        assert isinstance(value, str) and value


def test_meta_names_the_application(client: TestClient) -> None:
    """The shell title and footer used to come from ``templates.env.globals``."""
    meta = client.get("/api/meta").json()

    assert meta["app_name"] and meta["app_version"]


def test_meta_needs_no_database(client: TestClient) -> None:
    """It is cached forever on the client, so it must be answerable by a process
    whose database is not there at all."""
    main.app.dependency_overrides.pop(get_session, None)

    assert client.get("/api/meta").status_code == 200


# ---------------------------------------------------------------------------
# Settings payload (G3, G4)
# ---------------------------------------------------------------------------
#: The fields the settings screen, the artist blurb and the Health screens read.
#: Each was a template reaching into the raw ``Settings`` object, which a JSON
#: client cannot do.
SETTINGS_FIELDS = (
    "app_name",
    "app_version",
    "auto_download",
    "auto_index_on_follow",
    "upgrade_cleanup",
    "library_path",
    "trash_path",
    "data_path",
    "integrity_enabled",
    "integrity_reverify_fraction",
    "enrichment_write_back",
    "enrichment_prefer_external_cover",
    "naming_template",
    "naming_preview",
    "download_track_delay",
)


@pytest.mark.parametrize("field", SETTINGS_FIELDS)
def test_settings_carries_every_field_a_screen_reads(
    client: TestClient, field: str
) -> None:
    assert field in client.get("/api/settings").json()


def test_the_naming_preview_is_rendered_server_side(client: TestClient) -> None:
    """Two example paths from the configured template. The client cannot build
    them: rendering one means the sanitiser, the quality clamp and the template
    tokens, all of which live in ``app/core/naming.py``."""
    preview = client.get("/api/settings").json()["naming_preview"]

    assert isinstance(preview, list)
    assert len(preview) == 2
    assert all(isinstance(path, str) and path for path in preview)


def test_an_unrenderable_template_answers_with_an_empty_list(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Empty is a real answer — "the template could not be rendered", which is
    what a typo in ``NAMING_TEMPLATE`` looks like — and the screen must show it
    as that rather than as a blank box. It is never an exception: a bad template
    still has to let the page that fixes it load."""
    monkeypatch.setattr(
        deps, "naming_preview", lambda *_args, **_kwargs: []
    )

    body = client.get("/api/settings").json()

    assert body["naming_preview"] == []
    assert body["naming_template"]


def test_the_opt_in_rule_is_visible(client: TestClient) -> None:
    """``auto_download`` is what the add-artist hint and the artist blurb flip
    on. A client that assumed the default would be wrong for whoever changed it."""
    body = client.get("/api/settings").json()

    assert isinstance(body["auto_download"], bool)
    assert isinstance(body["upgrade_cleanup"], bool)


# ---------------------------------------------------------------------------
# Secrets (§6.16)
# ---------------------------------------------------------------------------
def test_the_contact_address_is_reduced_to_set(
    client: TestClient, secrets: dict[str, str]
) -> None:
    """It is an email address, and the settings screen is not private."""
    assert client.get("/api/settings").json()["enrichment_contact"] == "set"


def test_an_unset_contact_says_so_rather_than_lying(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The empty string is the other half of the same field: MusicBrainz is
    gated without it, and the screen has to be able to say which."""
    monkeypatch.setenv("ENRICHMENT_CONTACT", "")
    config.get_settings.cache_clear()
    try:
        body = client.get("/api/settings").json()
    finally:
        config.get_settings.cache_clear()

    assert body["enrichment_contact"] == ""
    assert body["musicbrainz_ready"] is False


@pytest.mark.parametrize("path", ["/api/settings", "/api/status", "/api/health/summary"])
def test_no_configured_secret_appears_anywhere_in_the_payload(
    client: TestClient, secrets: dict[str, str], path: str
) -> None:
    """The whole JSON tree, not a substring check on one rendered fragment.

    Four values are configured and none of them may travel: the auth token and
    the app secret sign every request, the AcoustID key is somebody's account,
    and the contact address is a person's email. The sweep is over keys as well
    as values, and over every screen that reports configuration.
    """
    body = client.get(path).json()
    leaked = [
        name
        for name, value in secrets.items()
        if any(value in text for text in strings(body))
    ]

    assert leaked == [], f"{path} leaked {leaked}"


def test_the_readiness_flags_are_what_replaces_the_values(
    client: TestClient, secrets: dict[str, str]
) -> None:
    """Set/unset is the whole interface. Everything a screen wants to *say* about
    a secret is derivable from a boolean, which is why there is no reason to
    send the value and no excuse for adding one later."""
    body = client.get("/api/settings").json()

    assert body["acoustid_ready"] is True
    assert body["musicbrainz_ready"] is True
    assert body["credentials_ok"] is True
    assert body["app_secret_source"] in {"env", "cache", "derived", "unset"}


def test_the_app_id_is_not_a_secret_and_is_published(
    client: TestClient, secrets: dict[str, str]
) -> None:
    """Deliberate asymmetry, recorded so nobody "fixes" it: the app id is the
    public half of the pair and the settings screen shows it, because "which app
    id is this process using" is the first question when a search stops working.
    The token beside it is the secret, and it is absent."""
    body = client.get("/api/settings").json()

    assert "qobuz_app_id" in body
    assert "qobuz_user_auth_token" not in body
    assert "qobuz_app_secret" not in body
    assert "acoustid_api_key" not in body
