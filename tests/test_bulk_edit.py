"""Bulk monitoring edits on the library page.

The whole risk of a bulk editor is collateral damage: changing the monitor mode
for fifty artists must not also reset their release types, and a dropdown left
on "no change" must mean *no change* rather than submitting a falsy value that
quietly unmonitors everything it touches. Most of the tests below assert what
was **not** modified.

A scratch SQLite file under ``tmp_path`` is injected through
``dependency_overrides``; nothing touches ``data/`` or the network.
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
from app.db import get_session
from app.models import Activity, Artist, Base, MonitorMode

#: (id, name, monitored, monitor_mode, accepted_release_types, quality_profile)
SEED = (
    ("100", "Nils Frahm", True, MonitorMode.ALL, "album,ep", "hires"),
    ("200", "Alice Coltrane", True, MonitorMode.FUTURE, "album", "default"),
    ("300", "black midi", False, MonitorMode.NONE, "album,ep,single", "lossless"),
)
ALL_IDS = [row[0] for row in SEED]


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path) -> Iterator[TestClient]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'bulk.db'}", poolclass=NullPool
    )
    session_maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with session_maker() as session:
            for artist_id, name, monitored, mode, types, profile in SEED:
                session.add(
                    Artist(
                        id=artist_id, name=name, monitored=monitored,
                        monitor_mode=mode, accepted_release_types=types,
                        quality_profile=profile,
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


def artists(client: TestClient) -> dict[str, dict[str, Any]]:
    """Current state of every artist, straight from the JSON API."""
    body = client.get("/api/artists?limit=100").json()
    return {item["id"]: item for item in body["items"]}


def bulk(client: TestClient, **fields: Any) -> Any:
    """POST the library page's bulk form the way the browser would."""
    data: dict[str, Any] = {
        "q": "", "monitored": "", "sort": "name", "order": "asc", "view": "table",
    }
    data.update(fields)
    return client.post("/ui/artists/bulk", data=data, headers={"HX-Request": "true"})


def activity_rows(client: TestClient) -> list[dict[str, Any]]:
    return client.get("/api/activity?limit=100").json()["items"]


# ------------------------------------------------------------------ rendering
@pytest.mark.parametrize("view", ["grid", "table"])
def test_both_views_offer_selection_and_the_bulk_bar(
    client: TestClient, view: str
) -> None:
    body = client.get(f"/artists?view={view}").text
    # `value=` anchors this to real inputs; the page's script also mentions the
    # field name in a selector string.
    assert body.count('name="artist_ids" value="') == len(SEED)
    assert 'id="bulk-bar"' in body
    assert 'id="select-all"' in body
    assert 'name="set_monitored"' in body
    assert 'name="set_monitor_mode"' in body
    assert 'name="types_action"' in body


def test_bulk_form_posts_to_the_bulk_endpoint(client: TestClient) -> None:
    body = client.get("/artists").text
    assert 'hx-post="/ui/artists/bulk"' in body
    assert 'hx-target="#artist-list"' in body


def test_confirm_lives_on_the_form_not_the_button(client: TestClient) -> None:
    """The shim reads hx-confirm off the element it bound — here, the form."""
    body = client.get("/artists").text
    form = body.split('id="bulk-form"')[1].split(">")[0]
    assert "hx-confirm" in form


# ---------------------------------------------------------------- monitoring
def test_bulk_stop_monitoring_touches_only_the_selection(client: TestClient) -> None:
    bulk(client, artist_ids=["100"], set_monitored="false")
    state = artists(client)
    assert state["100"]["monitored"] is False
    assert state["200"]["monitored"] is True     # untouched
    assert state["300"]["monitored"] is False    # already off


def test_bulk_monitor_turns_several_on_at_once(client: TestClient) -> None:
    bulk(client, artist_ids=["300"], set_monitored="true")
    assert artists(client)["300"]["monitored"] is True


def test_no_change_leaves_monitoring_alone(client: TestClient) -> None:
    """The regression this design exists to prevent."""
    bulk(client, artist_ids=ALL_IDS, set_monitored="", set_monitor_mode="future")
    state = artists(client)
    assert state["100"]["monitored"] is True
    assert state["200"]["monitored"] is True
    assert state["300"]["monitored"] is False    # NOT flipped on
    assert {a["monitor_mode"] for a in state.values()} == {"future"}


def test_mode_change_does_not_disturb_release_types_or_profile(
    client: TestClient,
) -> None:
    before = artists(client)
    bulk(client, artist_ids=ALL_IDS, set_monitor_mode="all")
    after = artists(client)
    for artist_id in ALL_IDS:
        assert after[artist_id]["accepted_release_types"] == \
            before[artist_id]["accepted_release_types"]
        assert after[artist_id]["quality_profile"] == before[artist_id]["quality_profile"]


# -------------------------------------------------------------- release types
def test_add_unions_without_clobbering(client: TestClient) -> None:
    bulk(client, artist_ids=["100", "200"], types_action="add",
         release_types=["single"])
    state = artists(client)
    assert set(state["100"]["accepted_release_types"]) == {"album", "ep", "single"}
    assert set(state["200"]["accepted_release_types"]) == {"album", "single"}


def test_add_is_idempotent(client: TestClient) -> None:
    for _ in range(2):
        bulk(client, artist_ids=["100"], types_action="add", release_types=["ep"])
    assert artists(client)["100"]["accepted_release_types"].count("ep") == 1


def test_remove_subtracts_and_keeps_the_rest(client: TestClient) -> None:
    bulk(client, artist_ids=["300"], types_action="remove", release_types=["single"])
    assert set(artists(client)["300"]["accepted_release_types"]) == {"album", "ep"}


def test_set_replaces_outright(client: TestClient) -> None:
    bulk(client, artist_ids=["100"], types_action="set", release_types=["live"])
    assert artists(client)["100"]["accepted_release_types"] == ["live"]


def test_ticked_types_do_nothing_without_an_action(client: TestClient) -> None:
    """Checkboxes are meaningless until "also accept"/"stop accepting" is chosen."""
    before = artists(client)
    bulk(client, artist_ids=ALL_IDS, types_action="", release_types=["single", "live"])
    assert artists(client) == before


def test_emptying_types_warns_by_name(client: TestClient) -> None:
    """An artist that accepts nothing will never want anything again — say so."""
    response = bulk(
        client, artist_ids=["200"], types_action="remove", release_types=["album"]
    )
    assert artists(client)["200"]["accepted_release_types"] == []
    trigger = response.headers["HX-Trigger"]
    assert "Alice Coltrane" in trigger
    assert "accept no release types" in trigger
    assert '"level": "warning"' in trigger


def test_emptying_types_is_logged_as_a_warning(client: TestClient) -> None:
    bulk(client, artist_ids=["200"], types_action="remove", release_types=["album"])
    row = next(r for r in activity_rows(client) if r["event"] == "artist.bulk")
    assert row["level"] == "warning"


# ------------------------------------------------------------------- edge cases
def test_empty_selection_changes_nothing(client: TestClient) -> None:
    before = artists(client)
    response = bulk(client, set_monitored="false")
    assert response.status_code == 200
    assert "Nothing selected" in response.headers["HX-Trigger"]
    assert artists(client) == before


def test_unknown_ids_are_reported_not_fatal(client: TestClient) -> None:
    response = client.post(
        "/api/artists/bulk",
        json={"artist_ids": ["100", "does-not-exist"], "monitored": False},
    )
    assert response.status_code == 200
    body = response.json()
    assert body["detail"]["updated"] == 1
    assert body["detail"]["missing"] == 1
    assert artists(client)["100"]["monitored"] is False


def test_duplicate_ids_count_once(client: TestClient) -> None:
    body = client.post(
        "/api/artists/bulk",
        json={"artist_ids": ["100", "100", "100"], "monitored": False},
    ).json()
    assert body["detail"]["selected"] == 1
    assert body["detail"]["updated"] == 1


def test_a_no_op_edit_says_so_and_writes_no_activity(client: TestClient) -> None:
    """Re-applying settings an artist already has must not spam the history."""
    before = len(activity_rows(client))
    response = client.post(
        "/api/artists/bulk", json={"artist_ids": ["100"], "monitor_mode": "all"}
    )
    body = response.json()
    assert body["ok"] is False
    assert "Nothing to change" in body["message"]
    assert body["detail"]["unchanged"] == 1
    assert len(activity_rows(client)) == before


def test_one_activity_row_per_bulk_edit_not_one_per_artist(
    client: TestClient,
) -> None:
    bulk(client, artist_ids=ALL_IDS, set_monitor_mode="none")
    rows = [r for r in activity_rows(client) if r["event"] == "artist.bulk"]
    assert len(rows) == 1


def test_the_count_reports_real_changes_not_selection_size(
    client: TestClient,
) -> None:
    """Three selected, but black midi is already on `none` — so it says two."""
    response = client.post(
        "/api/artists/bulk", json={"artist_ids": ALL_IDS, "monitor_mode": "none"}
    ).json()
    assert response["detail"]["selected"] == 3
    assert response["detail"]["updated"] == 2
    assert response["detail"]["unchanged"] == 1
    assert "Updated 2 artist(s)" in response["message"]


def test_bulk_response_keeps_the_active_filter(client: TestClient) -> None:
    """The refreshed list must not silently widen to every artist."""
    response = bulk(
        client, artist_ids=["100"], set_monitor_mode="none",
        q="frahm", monitored="true", view="table",
    )
    body = response.text
    assert "Nils Frahm" in body
    assert "Alice Coltrane" not in body
    assert 'name="q" value="frahm"' in body


def test_json_and_form_paths_agree(client: TestClient) -> None:
    client.post(
        "/api/artists/bulk",
        json={"artist_ids": ["100"], "release_types": ["single"],
              "release_types_action": "add"},
    )
    bulk(client, artist_ids=["200"], types_action="add", release_types=["single"])
    state = artists(client)
    assert "single" in state["100"]["accepted_release_types"]
    assert "single" in state["200"]["accepted_release_types"]


def test_bulk_edit_does_not_queue_anything(client: TestClient) -> None:
    """Monitoring is not downloading — the opt-in gate stays shut."""
    bulk(client, artist_ids=ALL_IDS, set_monitored="true", set_monitor_mode="all")
    assert client.get("/api/queue").json()["total"] == 0
