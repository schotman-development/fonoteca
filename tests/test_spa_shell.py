"""How the two front ends coexist, and the one way that can go wrong quietly.

``main.create_app`` now serves a React single-page app: everything that is not
an API route, a static file or the old Jinja UI is answered with
``static/app/index.html`` so the client router can take the path from there.
The old UI is still mounted, one prefix deeper, under ``/legacy``.

Only one of these tests is about a bug that would be hard to find. A mistyped
``/api`` endpoint answered with the SPA shell comes back ``200 text/html``,
which a client does not read as "no such endpoint" — it reads as an object that
will not parse, raised from wherever the response was eventually used. The URL
that caused it appears nowhere. So the catch-all refuses ``/api``, ``/health``
and ``/openapi`` outright and lets the ordinary error envelope answer them.

Nothing here touches the network or the database.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from fastapi.testclient import TestClient

import main


@pytest.fixture(name="client")
def client_fixture() -> TestClient:
    """The real application. None of these routes reads the database."""
    return TestClient(main.app)


# --------------------------------------------------------------- the SPA shell
@pytest.mark.parametrize(
    "path",
    [
        # The landing screen. It used to redirect to /library; the dashboard is
        # a real screen again (web/src/routes.tsx).
        "/",
        "/library",
        "/library/artists/1234",
        "/library/releases",
        "/library/releases/uyej1o165e870",
        "/library/release-groups/some-normalised-title",
        "/library/add",
        # The three addresses the new IA added. None of them is a server route,
        # which is exactly why the catch-all has to answer them: a hard reload
        # or a shared link on any one lands here first.
        "/radar",
        "/identify",
        "/rules",
        "/activity",
        # The backlog and the download worker, each on its own address. Worth
        # naming here rather than trusting the catch-all: ``/queue`` reads like
        # an API path and is not one — the JSON queue is ``/api/queue`` — so a
        # future root-level route with that name would take the address away
        # from the client and hand a browser JSON on a hard reload, which is the
        # failure ``/health`` and ``/system`` are already kept apart for.
        "/missing",
        "/queue",
        # The client's System SECTION is addressed /system, not /health: the
        # JSON probe owns /health and wins in the route table, so a hard reload
        # on /health would hand the browser JSON instead of the application
        # (web/src/shell/nav.ts, web/src/routes.tsx).
        "/system",
        "/system/integrity",
        "/system/scan",
        "/system/tidy",
        # The old address of the enrichment review. The client redirects it to
        # /identify — which it can only do if the server hands it the shell.
        "/system/enrichment",
        # The /health subtree is still unreserved and still answers with the
        # shell — nothing routes there, but a stale bookmark must not 500.
        "/health/integrity",
        "/settings",
        "/a/path/the/client/router/has/never/heard/of",
    ],
)
def test_client_routes_all_get_the_shell(client: TestClient, path: str) -> None:
    """Every client-side route is served the same document.

    Including the ones that do not exist: a single-page app owns its own
    not-found screen, and a server 404 on a deep link would break the back
    button for a typo.
    """
    response = client.get(path)

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("text/html")
    assert '<div id="root">' in response.text


def test_the_shell_is_never_cached(client: TestClient) -> None:
    """It names the hashed bundles, so a stale copy is a client pinned to a
    deploy that no longer exists — a blank page and a 404 on a filename nobody
    recognises. The bundles themselves are content-addressed and cached hard."""
    assert client.get("/").headers["cache-control"] == "no-store"


def test_a_missing_build_explains_itself_rather_than_500ing(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """A fresh clone has no build. That is not the server failing."""
    monkeypatch.setattr(main, "SPA_INDEX", tmp_path / "nothing" / "index.html")

    response = client.get("/")

    assert response.status_code == 200
    assert "npm run build --prefix web" in response.text


# ------------------------------------------------- the shell may not shadow JSON
@pytest.mark.parametrize(
    "path", ["/api/nope", "/api/albums", "/api/no/such/thing", "/openapi.json"]
)
def test_a_mistyped_endpoint_is_json_not_the_shell(
    client: TestClient, path: str
) -> None:
    """The one failure worth a test of its own.

    HTML here does not surface as a 404 anywhere near the request that caused
    it; it surfaces as a parse error somewhere else entirely.
    """
    response = client.get(path)

    assert response.status_code == 404
    assert response.headers["content-type"].startswith("application/json")
    body = response.json()
    assert body["ok"] is False
    assert body["status_code"] == 404
    assert isinstance(body["error"], str) and body["error"]


def test_the_real_api_still_answers(client: TestClient) -> None:
    """The catch-all is registered last and must not have swallowed anything."""
    assert client.get("/api/meta").status_code == 200
    assert client.get("/health").status_code == 200
    assert client.get("/api/openapi.json").status_code == 200


# ------------------------------------------------------------------- /legacy
def test_the_old_ui_is_still_reachable(client: TestClient) -> None:
    """Transitional: the Jinja UI keeps working until the SPA covers everything."""
    response = client.get("/legacy/settings")

    assert response.status_code == 200
    assert 'class="shell"' in response.text


def test_a_missing_legacy_page_keeps_the_jinja_error_page(client: TestClient) -> None:
    """While it is mounted it behaves as it always did — including its 404.

    Answering it with the SPA shell would be worse than either: a page that
    looks like it loaded, from a UI the request was not addressed to.
    """
    response = client.get("/legacy/no-such-page")

    assert response.status_code == 404
    assert response.headers["content-type"].startswith("text/html")
    assert '<div id="root">' not in response.text


def test_static_assets_are_not_swallowed(client: TestClient) -> None:
    """``/static`` is a mount and is registered before the catch-all."""
    response = client.get("/static/app/index.html")

    assert response.status_code == 200
    assert '<div id="root">' in response.text
