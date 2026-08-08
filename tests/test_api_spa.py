"""Where the single-page app ends and the JSON API begins.

One route table serves two audiences, and the boundary between them is the only
part of the wiring that can fail silently. The catch-all answers every unknown
path with ``static/app/index.html`` so the client router owns its own deep links
and its own not-found screen — and that is right for ``/library/releases/xyz``
and catastrophic for ``/api/albunms``:

    HTML at an ``/api`` path comes back ``200 text/html``. A client does not read
    that as "no such endpoint"; it reads it as an object that will not parse,
    raised from wherever the response was eventually used. The URL that caused it
    appears nowhere in the stack.

So the rule is stated in both directions and for every method: **a path under
``/api`` is JSON, always, including when it does not exist**, and everything
else is the shell, including when it does not exist.

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


# ---------------------------------------------------------------------------
# The shell
# ---------------------------------------------------------------------------
def test_the_root_is_the_shell(client: TestClient) -> None:
    response = client.get("/")

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("text/html")
    assert '<div id="root">' in response.text


@pytest.mark.parametrize(
    "path",
    [
        "/library",
        "/library/releases",
        "/library/releases/uyej1o165e870",
        "/library/artists/720076",
        "/library/release-groups/all-melody",
        "/library/add",
        "/activity",
        "/system",
        "/system/scan",
        "/system/enrichment",
        "/settings",
        "/deep/link/the/router/has/never/heard/of",
    ],
)
def test_a_deep_client_route_is_served_the_shell(client: TestClient, path: str) -> None:
    """A hard reload on any screen has to work, and so does a pasted link.

    Including the paths that do not exist: the client owns its own not-found
    screen, and a server 404 on a typo would break the back button for it.
    """
    response = client.get(path)

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("text/html")
    assert '<div id="root">' in response.text


def test_every_client_route_gets_the_identical_document(client: TestClient) -> None:
    """One document, one cache entry, one place the bundle names are written."""
    assert client.get("/library").text == client.get("/settings").text


def test_the_shell_is_never_cached(client: TestClient) -> None:
    """It names the hashed bundles. A stale copy is a client pinned to a deploy
    that no longer exists — a blank page and a 404 on a filename nobody
    recognises. The bundles themselves are content-addressed and cached hard."""
    assert client.get("/").headers["cache-control"] == "no-store"


def test_a_missing_build_explains_itself(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """A fresh clone has no ``web/dist``. That is not the server failing, and the
    answer has to name the command that fixes it — this is the first thing
    somebody sees after cloning."""
    monkeypatch.setattr(main, "SPA_INDEX", tmp_path / "nothing" / "index.html")

    response = client.get("/")

    assert response.status_code == 200
    assert "npm run build --prefix web" in response.text


# ---------------------------------------------------------------------------
# The shell may never shadow JSON
# ---------------------------------------------------------------------------
UNKNOWN_API_PATHS = [
    "/api/nope",
    "/api/albunms",
    "/api/artists/720076/albumss",
    "/api/no/such/thing",
    "/api/",
]


@pytest.mark.parametrize("path", UNKNOWN_API_PATHS)
def test_an_unknown_api_path_is_a_json_404(client: TestClient, path: str) -> None:
    """The one failure worth a test of its own — see the module docstring."""
    response = client.get(path)

    assert response.status_code == 404
    assert response.headers["content-type"].startswith("application/json")
    assert '<div id="root">' not in response.text


@pytest.mark.parametrize("path", UNKNOWN_API_PATHS)
def test_the_404_keeps_the_error_envelope(client: TestClient, path: str) -> None:
    """The SPA's API client keys off ``error``, so a 404 that is JSON but a
    different shape of JSON fails one layer further along than it should."""
    body = client.get(path).json()

    assert body["ok"] is False
    assert body["status_code"] == 404
    assert isinstance(body["error"], str) and body["error"]


@pytest.mark.parametrize("method", ["get", "post", "patch", "put", "delete"])
def test_no_method_reaches_the_shell_through_an_api_path(
    client: TestClient, method: str
) -> None:
    """A mutation is where a wrong answer costs the most, and a POST to a
    mistyped endpoint answered with ``200 text/html`` would read as success."""
    response = client.request(method.upper(), "/api/definitely-not-a-route")

    assert response.status_code in {404, 405}
    assert "text/html" not in response.headers.get("content-type", "")
    assert '<div id="root">' not in response.text


def test_a_wrong_method_on_a_real_endpoint_is_json_too(client: TestClient) -> None:
    """405 rather than 404, and still not the shell."""
    response = client.delete("/api/meta")

    assert response.status_code == 405
    assert "text/html" not in response.headers.get("content-type", "")


# ---------------------------------------------------------------------------
# The probes keep their own paths
# ---------------------------------------------------------------------------
def test_health_stays_json(client: TestClient) -> None:
    """It is read by a process supervisor, not a person. Answering it with the
    application would make every container report itself healthy for as long as
    the front end builds."""
    response = client.get("/health")

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("application/json")
    assert response.json()["status"] in {"ok", "degraded"}


def test_the_openapi_document_stays_json(client: TestClient) -> None:
    """The wire types are generated from it, and a generator handed HTML fails
    with a parse error that names neither the URL nor the reason."""
    for path in ("/openapi.json", "/api/openapi.json"):
        response = client.get(path)
        assert response.headers["content-type"].startswith("application/json")

    assert client.get("/api/openapi.json").status_code == 200


def test_the_api_routers_were_registered_before_the_catch_all(
    client: TestClient,
) -> None:
    """Order is the whole mechanism: the catch-all matches everything, so it has
    to be last. If it were not, this is what would still answer 200 while every
    real endpoint returned the shell."""
    assert client.get("/api/meta").status_code == 200
    assert client.get("/api/banners").status_code == 200
    assert client.get("/health").status_code == 200


def test_static_assets_are_not_swallowed(client: TestClient) -> None:
    """``/static`` is a mount, registered before the catch-all. Serving the shell
    for a missing asset turns a 404 in the network tab into a JavaScript syntax
    error on the first byte of "HTML"."""
    response = client.get("/static/app/index.html")

    assert response.status_code == 200
    assert '<div id="root">' in response.text
