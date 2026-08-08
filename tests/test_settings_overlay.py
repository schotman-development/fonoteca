"""The settings overlay: a narrow, visible, refusable way to change seven values.

``Settings`` is read from the environment and stays that way. What this covers is
the thin layer on top of it — an ``app_setting`` row per overridden key, an
allowlist that is *exactly* seven keys, and an accessor that lays one over the
other. Four properties are what make it safe, and each of them is a refusal:

* **A key outside the allowlist is a 400.** Not a no-op. A switch that does
  nothing and says nothing is the failure a settings screen cannot recover from,
  and "no credential, no path, no rate-limit figure is writable" is only a real
  guarantee if asking is an error.
* **A value the key cannot hold is a 400, at the moment it is submitted.** A bad
  ``naming_template`` accepted here would surface hours later, inside a re-file,
  as a 500 nobody can connect to the press that caused it — so it is *rendered*
  before it is stored.
* **The write commits itself.** Both HTTP entry points that write outside a
  service (this one and ``identify``) run on the request-scoped session, which
  never commits. The assertion that matters is a *separate* GET afterwards, so
  nothing is read out of the identity map that wrote it.
* **What cannot take effect immediately says so.** ``enrichment_sources``
  rebuilds the provider ladder, and rebuilding it under a running tick would
  close an HTTP client mid-request — so it defers, reports ``pending``, and one
  ladder (one limiter per upstream) is alive at every instant in between.

Nothing here touches the network, and every test that installs an overlay tears
it down: the overlay is process-global, and a leaked one is the next test reading
somebody else's configuration.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app import config
from app.config import Settings
from app.core import settings_store
from app.core.enricher import Enricher
from app.core.state import AppState
from app.db import get_session
from app.models import AppSetting, Base, EnrichmentSource

#: The contract, spelled out once here so widening it is a deliberate edit in two
#: places rather than a quiet one in the mapping.
ALLOWLIST = (
    "naming_template",
    "enrichment_sources",
    "upgrade_cleanup",
    "library_scan_nightly",
    "integrity_enabled",
    "enrichment_write_back",
    "nfo_enabled",
)


@pytest.fixture(autouse=True)
def clean_overlay() -> Iterator[None]:
    """No test starts with an overlay installed, and none leaves one behind."""
    config.reset_overrides()
    try:
        yield
    finally:
        config.reset_overrides()


def make_settings(**overrides: Any) -> Settings:
    base: dict[str, Any] = {"qobuz_app_id": "app", "qobuz_user_auth_token": "token"}
    base.update(overrides)
    return Settings(**base)


# ---------------------------------------------------------------------------
# The allowlist itself
# ---------------------------------------------------------------------------
def test_the_allowlist_is_exactly_the_seven_keys() -> None:
    """Anything else is environment-only — no credential, no path, no pacing."""
    assert config.overridable_keys() == ALLOWLIST
    assert set(config.OVERRIDABLE_SETTINGS) == set(ALLOWLIST)
    for forbidden in (
        "qobuz_user_auth_token",
        "qobuz_app_secret",
        "library_path",
        "data_path",
        "trash_path",
        "qobuz_max_requests_per_hour",
        "auto_download",
    ):
        assert forbidden not in config.OVERRIDABLE_SETTINGS


def test_an_unknown_key_is_refused_rather_than_dropped() -> None:
    with pytest.raises(KeyError):
        config.parse_override("library_path", "/tmp/elsewhere")


@pytest.mark.parametrize(
    ("key", "value"),
    [
        ("nfo_enabled", "sometimes"),
        ("upgrade_cleanup", 7),
        ("integrity_enabled", None),
        ("enrichment_sources", "deezer,not-a-source"),
        ("enrichment_sources", 5),
        ("naming_template", "   "),
        ("naming_template", 42),
    ],
)
def test_a_value_the_key_cannot_hold_is_refused(key: str, value: Any) -> None:
    with pytest.raises(ValueError):
        config.parse_override(key, value)


def test_a_template_that_would_overwrite_every_track_is_refused() -> None:
    """``Settings`` never validates this, and the renderer never raises.

    :mod:`app.core.naming` sanitises rather than fails — an unknown token becomes
    ``Unknown`` — so the check that is worth making is the one whose absence
    destroys data: two tracks of one release must not render to one path, or the
    download loop writes each over the last and a nine-track album ends as one
    file. Both of these render perfectly happily.
    """
    with pytest.raises(ValueError, match="same path"):
        config.parse_override("naming_template", "{artist}/{album}")
    with pytest.raises(ValueError, match="same path"):
        config.parse_override("naming_template", "{artist}/{album}/{titel}.{ext}")

    template = "{artist}/{album}/{track:02d} {title}.{ext}"
    typed, stored = config.parse_override("naming_template", template)
    assert typed == template and stored == template


def test_a_switch_takes_whatever_a_client_actually_sends() -> None:
    for sent in (True, "true", "True", "on", 1):
        assert config.parse_override("nfo_enabled", sent) == (True, "true")
    for sent in (False, "false", "off", 0):
        assert config.parse_override("nfo_enabled", sent) == (False, "false")


def test_the_ladder_keeps_its_order_and_drops_duplicates() -> None:
    typed, stored = config.parse_override(
        "enrichment_sources", ["musicbrainz", "acoustid", "musicbrainz"]
    )
    assert typed == "musicbrainz,acoustid" == stored


# ---------------------------------------------------------------------------
# Precedence, origin and the one accessor
# ---------------------------------------------------------------------------
def test_the_database_wins_over_the_environment_and_says_so() -> None:
    base = make_settings(nfo_enabled=True, naming_template="{artist}/{title}.{ext}")
    effective = config.install_overrides({"nfo_enabled": "false"}, base=base)

    assert effective.nfo_enabled is False
    assert base.nfo_enabled is True, "the base object must never be mutated"
    assert effective is not base
    # Everything not overridden still comes from the base.
    assert effective.naming_template == base.naming_template

    origins = config.setting_origins()
    assert origins["nfo_enabled"] == "override"
    assert origins["upgrade_cleanup"] == "env"
    assert set(origins) == set(ALLOWLIST), "every key reports an origin, always"


def test_resetting_an_override_restores_the_environment_value() -> None:
    base = make_settings(nfo_enabled=True)
    config.install_overrides({"nfo_enabled": "false"}, base=base)
    # install replaces the whole set — the table is the truth, not a merge target.
    effective = config.install_overrides({}, base=base)

    assert effective.nfo_enabled is True
    assert config.setting_origins()["nfo_enabled"] == "env"
    assert config.installed_overrides() == {}


def test_effective_for_is_a_no_op_with_nothing_installed() -> None:
    """Routing a call site through the accessor cannot change behaviour on its own."""
    base = make_settings()
    assert config.effective_for(base) is base
    assert config.get_effective_settings() is config.get_settings()


def test_a_stored_row_that_no_longer_parses_is_skipped_not_raised() -> None:
    """An allowlist can shrink, and a hand-edited table must not stop startup."""
    base = make_settings(nfo_enabled=True)
    effective = config.apply_overrides(
        base,
        {
            "nfo_enabled": "banana",  # unusable
            "library_path": "/tmp",  # no longer (never) overridable
            "upgrade_cleanup": "false",  # good
        },
    )
    assert effective.nfo_enabled is True
    assert effective.upgrade_cleanup is False


def test_the_overlay_reaches_the_naming_preview_and_the_librarian() -> None:
    """The Rules screen's preview and a re-file must read the same template."""
    from app.api.deps import naming_preview

    template = "{artist}/{year} {album}/{track:02d}. {title}.{ext}"
    config.install_overrides({"naming_template": template})

    live = config.get_effective_settings()
    assert live.naming_template == template
    preview = naming_preview(live)
    assert preview and preview[0].endswith(".flac")
    assert "2021 Promises" in preview[0]


# ---------------------------------------------------------------------------
# The table
# ---------------------------------------------------------------------------
@pytest.fixture(name="factory")
def factory_fixture(tmp_path: Path) -> Iterator[async_sessionmaker[AsyncSession]]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'overlay.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)

    asyncio.run(setup())
    try:
        yield maker
    finally:
        asyncio.run(engine.dispose())


def test_the_store_round_trips_and_ignores_a_key_it_does_not_know(
    factory: async_sessionmaker[AsyncSession],
) -> None:
    async def scenario() -> dict[str, str]:
        async with factory() as session:
            await settings_store.set_override(session, "nfo_enabled", "false")
            await settings_store.set_override(session, "nfo_enabled", "true")
            session.add(AppSetting(key="library_path", value="/tmp/elsewhere"))
            await session.commit()
        async with factory() as session:
            return await settings_store.load_overrides(session)

    overrides = asyncio.run(scenario())
    assert overrides == {"nfo_enabled": "true"}, "a second write replaces the first"


def test_clearing_deletes_the_row_rather_than_storing_the_env_value(
    factory: async_sessionmaker[AsyncSession],
) -> None:
    async def scenario() -> tuple[int, list[str]]:
        async with factory() as session:
            await settings_store.set_override(session, "nfo_enabled", "false")
            await session.commit()
        async with factory() as session:
            removed = await settings_store.clear_override(session, ["nfo_enabled"])
            await session.commit()
        async with factory() as session:
            rows = (await session.execute(select(AppSetting.key))).scalars().all()
            return removed, [str(row) for row in rows]

    removed, rows = asyncio.run(scenario())
    assert removed == 1
    assert rows == []


# ---------------------------------------------------------------------------
# The endpoint
# ---------------------------------------------------------------------------
@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    """A scratch database behind the real routes.

    The override **must not commit** — that is the whole point of the
    "it commits itself" test below, and a committing fixture hides the bug.
    """
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'settings.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)

    asyncio.run(setup())
    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        asyncio.run(engine.dispose())


def test_a_key_outside_the_allowlist_is_a_400(client: TestClient) -> None:
    response = client.patch("/api/settings", json={"values": {"library_path": "/tmp"}})

    assert response.status_code == 400
    body = response.json()
    assert body["ok"] is False
    assert "library_path" in body["error"]
    assert body["detail"]["code"] == "not_overridable"
    assert body["detail"]["key"] == "library_path"
    assert set(body["detail"]["overridable"]) == set(ALLOWLIST)
    assert config.installed_overrides() == {}, "a refusal writes nothing"


def test_a_credential_cannot_be_written_through_this_endpoint(
    client: TestClient,
) -> None:
    response = client.patch(
        "/api/settings", json={"values": {"qobuz_user_auth_token": "stolen"}}
    )
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "not_overridable"


def test_a_bad_value_is_a_400_naming_the_key(client: TestClient) -> None:
    response = client.patch(
        "/api/settings", json={"values": {"enrichment_sources": "deezer,discogs"}}
    )

    assert response.status_code == 400
    body = response.json()
    assert body["detail"]["code"] == "invalid_setting"
    assert body["detail"]["key"] == "enrichment_sources"
    assert "discogs" in body["error"]


def test_a_bad_template_is_refused_by_the_endpoint(client: TestClient) -> None:
    response = client.patch(
        "/api/settings", json={"values": {"naming_template": "{artist}/{album}"}}
    )
    assert response.status_code == 400
    assert response.json()["detail"]["key"] == "naming_template"


def test_setting_and_resetting_one_key_in_one_request_is_refused(
    client: TestClient,
) -> None:
    response = client.patch(
        "/api/settings",
        json={"values": {"nfo_enabled": False}, "reset": ["nfo_enabled"]},
    )
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "settings_conflict"


def test_an_override_survives_the_request_that_made_it(client: TestClient) -> None:
    """The endpoint commits itself; the *next* request is what proves it."""
    written = client.patch("/api/settings", json={"values": {"nfo_enabled": False}})
    assert written.status_code == 200
    assert written.json()["nfo_enabled"] is False

    read = client.get("/api/settings")
    assert read.status_code == 200
    body = read.json()
    assert body["nfo_enabled"] is False
    assert body["origins"]["nfo_enabled"] == "override"
    assert body["origins"]["upgrade_cleanup"] == "env"
    assert set(body["overridable"]) == set(ALLOWLIST)
    assert body["pending"] == []


def test_a_reset_puts_the_key_back_on_the_environment(client: TestClient) -> None:
    client.patch("/api/settings", json={"values": {"nfo_enabled": False}})
    response = client.patch("/api/settings", json={"reset": ["nfo_enabled"]})

    assert response.status_code == 200
    body = response.json()
    assert body["nfo_enabled"] is config.get_settings().nfo_enabled
    assert body["origins"]["nfo_enabled"] == "env"


def test_resetting_a_key_outside_the_allowlist_is_also_a_400(
    client: TestClient,
) -> None:
    response = client.patch("/api/settings", json={"reset": ["qobuz_app_secret"]})
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "not_overridable"


def test_the_template_write_is_reflected_in_the_preview_it_returns(
    client: TestClient,
) -> None:
    template = "{artist}/{album}/{track:02d}. {title}.{ext}"
    response = client.patch(
        "/api/settings", json={"values": {"naming_template": template}}
    )

    body = response.json()
    assert body["naming_template"] == template
    assert body["naming_preview"], "the preview must render what was just accepted"
    assert body["naming_preview"][0].endswith("01. Movement 1.flac")


def test_nothing_secret_reaches_the_payload_after_a_write(client: TestClient) -> None:
    body = client.patch(
        "/api/settings", json={"values": {"nfo_enabled": True}}
    ).json()
    assert body["enrichment_contact"] in ("set", "")
    serialised = str(body)
    assert config.get_settings().qobuz_user_auth_token not in serialised or not (
        config.get_settings().qobuz_user_auth_token
    )


def test_an_empty_write_changes_nothing_and_still_answers(client: TestClient) -> None:
    response = client.patch("/api/settings", json={})
    assert response.status_code == 200
    assert response.json()["origins"] == {key: "env" for key in ALLOWLIST}


# ---------------------------------------------------------------------------
# The provider ladder — one limiter per upstream, at every instant
# ---------------------------------------------------------------------------
class FakeProvider:
    """Enough of a provider to be built, counted and closed."""

    def __init__(self, source: EnrichmentSource) -> None:
        self.source = source
        self.closed = False

    async def aclose(self) -> None:
        self.closed = True


def build_enricher(sources: str) -> Enricher:
    settings = make_settings(enrichment_sources=sources, enrichment_enabled=False)
    providers = [
        FakeProvider(EnrichmentSource(name))
        for name in settings.enrichment_source_list
    ]
    return Enricher(providers, settings=settings)  # type: ignore[arg-type]


def test_rebuilding_the_ladder_closes_the_rungs_it_replaces() -> None:
    """Two ladders alive is two rate limiters per upstream — the invariant break."""
    enricher = build_enricher("deezer,musicbrainz")
    old = list(enricher.providers.values())
    built: list[list[FakeProvider]] = []

    def factory() -> list[FakeProvider]:
        fresh = [FakeProvider(EnrichmentSource("acoustid"))]
        built.append(fresh)
        return fresh

    live = asyncio.run(enricher.reconfigure(factory))  # type: ignore[arg-type]

    assert live is True
    assert enricher.ladder_pending is False
    assert all(provider.closed for provider in old)
    assert len(built) == 1, "the factory runs once; nothing builds a spare ladder"
    assert set(enricher.providers) == {EnrichmentSource("acoustid")}


def test_a_ladder_change_under_a_running_tick_defers_and_says_so() -> None:
    """Closing an HTTP client mid-request is how a settings press fails a lookup."""
    enricher = build_enricher("deezer")
    old = list(enricher.providers.values())
    calls: list[int] = []

    def factory() -> list[FakeProvider]:
        calls.append(1)
        return [FakeProvider(EnrichmentSource("musicbrainz"))]

    async def scenario() -> bool:
        await enricher._lock.acquire()  # stand in for a tick that is draining
        try:
            return await enricher.reconfigure(factory, timeout=0.01)  # type: ignore[arg-type]
        finally:
            enricher._lock.release()

    live = asyncio.run(scenario())

    assert live is False
    assert enricher.ladder_pending is True
    assert calls == [], "nothing is built while the old ladder is still in use"
    assert all(not provider.closed for provider in old)
    assert set(enricher.providers) == {EnrichmentSource("deezer")}

    # ...and the next tick installs it.
    asyncio.run(enricher.tick())
    assert enricher.ladder_pending is False
    assert set(enricher.providers) == {EnrichmentSource("musicbrainz")}
    assert all(provider.closed for provider in old)


def test_a_deferred_ladder_does_not_revert_a_later_settings_write() -> None:
    """What is deferred is the rungs, never the values.

    A deferral used to carry the whole ``Settings`` object with it. Any write
    that landed before the next tick — ``AppState._repoint_settings`` puts it
    straight onto the enricher — was then overwritten by that snapshot when the
    ladder finally installed. The worst case is the one drawn here:
    ``enrichment_write_back`` switched **off** while a ladder change was pending
    switched itself back on one tick later and started rewriting the tags in
    files the user had just told it to leave alone.
    """
    enricher = build_enricher("deezer")
    at_deferral = make_settings(
        enrichment_sources="musicbrainz",
        enrichment_enabled=False,
        enrichment_write_back=True,
    )

    def factory() -> list[FakeProvider]:
        return [FakeProvider(EnrichmentSource("musicbrainz"))]

    async def scenario() -> None:
        await enricher._lock.acquire()  # a tick is draining
        try:
            live = await enricher.reconfigure(  # type: ignore[arg-type]
                factory, settings=at_deferral, timeout=0.01
            )
        finally:
            enricher._lock.release()
        assert live is False and enricher.ladder_pending is True
        # The names are live at once even though the rungs are not — which is
        # the whole reason holding the object back was never needed.
        assert enricher._settings.enrichment_source_list == ["musicbrainz"]

        # A second, unrelated press. This is exactly what _repoint_settings does.
        enricher._settings = make_settings(
            enrichment_sources="musicbrainz",
            enrichment_enabled=False,
            enrichment_write_back=False,
        )
        await enricher.tick()

    asyncio.run(scenario())

    assert enricher.ladder_pending is False
    assert set(enricher.providers) == {EnrichmentSource("musicbrainz")}
    assert enricher._settings.enrichment_write_back is False, (
        "the deferred ladder put a stale Settings back and re-enabled write-back"
    )


# ---------------------------------------------------------------------------
# AppState — what a write actually reaches
# ---------------------------------------------------------------------------
class Component:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings


class Downloader(Component):
    async def download_album(self, session: Any, album_id: str) -> None:  # noqa: D102
        return None


def build_state(**overrides: Any) -> tuple[AppState, Downloader, Enricher]:
    base = make_settings(enrichment_enabled=False, **overrides)
    enricher = Enricher(
        [FakeProvider(EnrichmentSource(name)) for name in base.enrichment_source_list],  # type: ignore[arg-type]
        settings=base,
    )
    downloader = Downloader(base)
    queue = Component(base)
    queue._download = downloader.download_album  # type: ignore[attr-defined]
    state = AppState(
        settings=base,
        limiter=None,  # type: ignore[arg-type]
        client=None,  # type: ignore[arg-type]
        indexer=Component(base),  # type: ignore[arg-type]
        queue=queue,  # type: ignore[arg-type]
        scanner=Component(base),  # type: ignore[arg-type]
        importer=Component(base),  # type: ignore[arg-type]
        enricher=enricher,
        base_settings=base,
    )
    return state, downloader, enricher


def test_a_write_reaches_the_downloader_not_just_the_read_models() -> None:
    """``upgrade_cleanup`` is read where the files are written, or it is a lie."""
    state, downloader, enricher = build_state(upgrade_cleanup=True, nfo_enabled=True)

    report = asyncio.run(
        state.apply_setting_overrides({"upgrade_cleanup": "false", "nfo_enabled": "false"})
    )

    assert state.settings.upgrade_cleanup is False
    assert downloader._settings.upgrade_cleanup is False
    assert downloader._settings.nfo_enabled is False
    assert enricher._settings.nfo_enabled is False
    assert report["ladder_rebuilt"] is False, "the ladder did not change"
    assert report["pending"] == []


def test_a_write_that_does_not_touch_the_ladder_does_not_rebuild_it() -> None:
    """Rebuilding for an unrelated switch mints a second limiter per upstream."""
    state, _, enricher = build_state(enrichment_sources="deezer,musicbrainz")
    before = enricher.providers

    asyncio.run(state.apply_setting_overrides({"nfo_enabled": "false"}))

    assert enricher.providers.keys() == before.keys()
    assert all(not provider.closed for provider in before.values())  # type: ignore[attr-defined]


def test_clearing_an_override_rebuilds_from_the_environment_not_the_last_value() -> None:
    """The overlay is laid over ``base_settings``, never over the effective object."""
    state, _, _ = build_state(nfo_enabled=True)

    asyncio.run(state.apply_setting_overrides({"nfo_enabled": "false"}))
    assert state.settings.nfo_enabled is False

    asyncio.run(state.apply_setting_overrides({}))
    assert state.settings.nfo_enabled is True


def test_a_deferred_ladder_is_reported_as_pending_not_as_live(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The honesty rule: a control that has not taken effect must not claim it has."""
    import app.core.state as state_module
    from app.api.deps import build_settings_out

    state, _, enricher = build_state(enrichment_sources="deezer")

    async def defer() -> None:
        await enricher._lock.acquire()  # a tick is draining
        try:
            await state.apply_setting_overrides({"enrichment_sources": "musicbrainz"})
        finally:
            enricher._lock.release()

    asyncio.run(defer())
    assert enricher.ladder_pending is True
    assert state.pending_setting_keys() == ("enrichment_sources",)

    monkeypatch.setattr(state_module, "_state", state, raising=False)
    payload = build_settings_out(state.settings)
    assert payload.pending == ["enrichment_sources"]
    assert payload.origins["enrichment_sources"] == "override"
    # The stored value is what the screen shows; the ladder catches up next tick.
    assert payload.enrichment_sources == ["musicbrainz"]

    asyncio.run(enricher.tick())
    assert state.pending_setting_keys() == ()
    assert build_settings_out(state.settings).pending == []


# ---------------------------------------------------------------------------
# ...and the value that has to be live, where it is actually read
# ---------------------------------------------------------------------------
def test_an_overridden_template_reaches_a_refile_plan(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The Rules preview and the re-file plan must agree, so both read the overlay.

    It doubles as a refusal test: the environment's template carries
    ``{quality}``, these files have none that can be read, and that is the
    already-existing blocked branch — so the two calls differ in *which* answer
    they give, not merely in a folder name.

    ``naming_template`` is the one overridable value that is read *per call*
    rather than captured, so this is where "live" is either true or a claim. A
    plan built from ``.env`` while the preview showed the override is the exact
    failure the honesty rule exists to prevent: the screen says one folder name
    and the button produces another.
    """
    from app.core import librarian
    from app.models import Album, AlbumStatus, Artist

    library = tmp_path / "music"
    data = tmp_path / "data"
    library.mkdir()
    data.mkdir()
    monkeypatch.setenv("LIBRARY_PATH", str(library))
    monkeypatch.setenv("DATA_PATH", str(data))
    config.get_settings.cache_clear()
    conf = config.get_settings()
    assert conf.library_path == library, "refusing to run against the real library"

    folder = library / "Nils Frahm" / "old-import"
    folder.mkdir(parents=True)
    (folder / "01 - Sunson.flac").write_bytes(b"audio")

    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'refile.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def scenario() -> tuple[Any, Any]:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id="720076", name="Nils Frahm", monitored=True))
            session.add(
                Album(
                    id="uyej1o165e870",
                    artist_id="720076",
                    title="All Melody",
                    status=AlbumStatus.DOWNLOADED,
                    monitored=True,
                    release_type="album",
                    tracks_count=1,
                    media_count=1,
                    path=str(folder),
                )
            )
            await session.commit()
        async with maker() as session:
            album = await session.get(Album, "uyej1o165e870")
            # No settings argument: the module's own fallback is what a route,
            # a job and the CLI all take, so that is the path worth proving.
            before = await librarian.plan_refile(session, album)
            config.install_overrides(
                {"naming_template": "{artist}/[{year}] {album}/{title}.{ext}"}
            )
            after = await librarian.plan_refile(session, album)
            return before, after

    try:
        before, after = asyncio.run(scenario())
    finally:
        asyncio.run(engine.dispose())
        config.get_settings.cache_clear()

    # The environment's template carries {quality}, and these files have none to
    # read — so the plan is *blocked*, which is the refusal branch that already
    # existed and the proof that the default was in force a moment earlier.
    assert before.blocked is not None and "quality" in before.blocked

    # The override does not, so the same album now plans a real move.
    assert after.blocked is None, after.blocked
    assert Path(after.target_dir).name == "All Melody"
    assert Path(after.target_dir).parent.name == "Nils Frahm"
    assert after.moves_directory


# ---------------------------------------------------------------------------
# ``integrity_enabled`` — the switch has to reach the button, not just the badge
# ---------------------------------------------------------------------------
# The nightly rotation and ``GET /api/integrity`` both read the overlay, and for
# a while ``POST /api/integrity/verify`` did not. That is the worst shape this
# failure can take: the *manual* pass is the one somebody presses while looking
# at the switch, so turning it off left one path that still hashed the whole
# library, and turning it on left one path that answered "set INTEGRITY_ENABLED
# in .env and restart" to a person who had just done the equivalent.
def test_switching_integrity_off_stops_the_manual_pass(client: TestClient) -> None:
    written = client.patch("/api/settings", json={"values": {"integrity_enabled": False}})
    assert written.status_code == 200
    assert written.json()["integrity_enabled"] is False

    response = client.post("/api/integrity/verify")

    assert response.status_code == 503
    # ...and it points at the control that is actually holding it off, rather
    # than at a file the user does not need to edit.
    assert "Rules" in response.json()["error"]


def test_switching_integrity_on_lets_the_manual_pass_run(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The other direction: an override of ``true`` over an environment of false."""
    import app.core.scheduler as scheduler_module
    from app.api import deps

    off = make_settings(integrity_enabled=False)
    monkeypatch.setattr(deps, "get_settings", lambda: off)
    monkeypatch.setattr("app.api.routes_api.get_settings", lambda: off)

    ran: list[bool] = []

    async def fake_verify(*_args: Any, **_kwargs: Any) -> Any:
        ran.append(True)
        return scheduler_module.IntegrityReport()

    monkeypatch.setattr(scheduler_module, "verify_integrity", fake_verify)

    blocked = client.post("/api/integrity/verify")
    assert blocked.status_code == 503, "the environment says off, and nothing overrides it"
    assert ran == []

    assert (
        client.patch("/api/settings", json={"values": {"integrity_enabled": True}}).status_code
        == 200
    )
    allowed = client.post("/api/integrity/verify")

    assert allowed.status_code == 200, allowed.text
    assert ran == [True]


# ---------------------------------------------------------------------------
# Origin honesty
# ---------------------------------------------------------------------------
def test_a_row_that_cannot_be_parsed_does_not_claim_to_be_the_live_value() -> None:
    """``origins`` answers about the value in force, not about the row's existence.

    The stored row survives (it is not this build's to delete — the allowlist can
    shrink between releases and grow back), but the value being *read* came from
    the environment, so that is what the screen is told. Saying ``"override"``
    would put an "overridden" badge beside ``.env``'s own value and offer a reset
    that changes nothing anyone can see.
    """
    base = make_settings(nfo_enabled=True, upgrade_cleanup=True)
    effective = config.install_overrides(
        {"nfo_enabled": "banana", "upgrade_cleanup": "false"}, base=base
    )

    assert effective.nfo_enabled is True, "the unusable row is skipped"
    assert effective.upgrade_cleanup is False

    origins = config.setting_origins()
    assert origins["nfo_enabled"] == "env"
    assert origins["upgrade_cleanup"] == "override"
    # The row itself is still there to be reset.
    assert config.installed_overrides()["nfo_enabled"] == "banana"


def test_an_overlay_of_nothing_usable_is_the_environment_itself() -> None:
    """``effective_for`` returns *base* un-copied when no override really applies."""
    base = make_settings(nfo_enabled=True)
    config.install_overrides({"nfo_enabled": "banana"}, base=base)

    other = make_settings(nfo_enabled=False)
    assert config.effective_for(other) is other


# ---------------------------------------------------------------------------
# The CLI reads the same settings the server does
# ---------------------------------------------------------------------------
def test_the_cli_runs_on_the_overlay_not_on_the_environment_alone(
    factory: async_sessionmaker[AsyncSession], monkeypatch: pytest.MonkeyPatch
) -> None:
    """``enrich-now`` builds the ladder from ``ctx.settings``; ``verify-library``
    reports ``integrity_enabled`` from it. Both are on the allowlist, so an
    environment-only CLI would run a different configuration from the server it
    shares a library with — against the same files, on the same night."""
    import cli

    monkeypatch.setattr(cli, "session_scope", factory)

    async def scenario() -> Any:
        async with factory() as session:
            await settings_store.set_override(session, "enrichment_sources", "deezer")
            await settings_store.set_override(session, "integrity_enabled", "false")
            await session.commit()
        return await cli._effective_settings()

    settings = asyncio.run(scenario())

    assert settings.enrichment_source_list == ["deezer"]
    assert settings.integrity_enabled is False
    assert config.setting_origins()["enrichment_sources"] == "override"


def test_the_cli_still_runs_when_the_overlay_cannot_be_read(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A database from before this table existed must not stop a command."""
    import cli

    def explode() -> Any:
        raise RuntimeError("no such table: app_setting")

    monkeypatch.setattr(cli, "session_scope", explode)

    settings = asyncio.run(cli._effective_settings())

    assert settings is config.get_settings()
    assert config.installed_overrides() == {}
