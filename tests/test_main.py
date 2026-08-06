"""Integration-layer tests for ``main.py`` and ``cli.py``.

These deliberately avoid touching the database or the network: they assert the
wiring (routes registered, error responses negotiated correctly, CLI parser
shape) that the rest of the suite cannot see because it tests modules in
isolation.
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest
from starlette.requests import Request

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import cli  # noqa: E402
import main  # noqa: E402


def make_request(path: str, accept: str = "") -> Request:
    """Build a minimal Starlette request for handler-level tests."""
    headers = [(b"accept", accept.encode())] if accept else []
    return Request(
        {
            "type": "http",
            "http_version": "1.1",
            "method": "GET",
            "scheme": "http",
            "server": ("testserver", 80),
            "path": path,
            "raw_path": path.encode(),
            "query_string": b"",
            "root_path": "",
            "headers": headers,
            "client": ("test", 1),
            "app": main.app,
        }
    )


# ---------------------------------------------------------------------- app
def test_app_metadata() -> None:
    assert main.app.title == "Fonoteca"
    assert main.app.router.lifespan_context is not None


def test_expected_routes_are_registered() -> None:
    paths = {
        getattr(route, "path", "")
        for router in (main.routes_api.router, main.routes_api.health_router, main.routes_ui.router)
        for route in router.routes
    }
    for expected in (
        "/health",
        "/api/status",
        "/api/artists",
        "/api/queue",
        "/api/search",
        "/",
        "/artists",
        "/add",
        "/queue",
        "/activity",
        "/settings",
    ):
        assert expected in paths, f"{expected} is not registered"


def test_static_mount_exists() -> None:
    mounts = [route for route in main.app.routes if getattr(route, "path", "") == "/static"]
    assert mounts, "/static is not mounted"
    assert main.STATIC_DIR.is_dir()


def test_error_template_exists() -> None:
    assert (main.STATIC_DIR.parent / "templates" / "error.html").is_file()


# ------------------------------------------------------------ negotiation
@pytest.mark.parametrize(
    ("path", "accept", "expected"),
    [
        ("/api/status", "", True),
        ("/health", "", True),
        ("/artists", "text/html", False),
        ("/artists", "", False),
        ("/artists", "application/json", True),
        ("/", "text/html,application/xhtml+xml", False),
    ],
)
def test_wants_json(path: str, accept: str, expected: bool) -> None:
    assert main.wants_json(make_request(path, accept)) is expected


def test_error_page_renders_html() -> None:
    response = main._error_page(make_request("/artists", "text/html"), 404, "Nope")
    assert response.status_code == 404


# ------------------------------------------------------------------- cli
def test_cli_parser_has_every_subcommand() -> None:
    parser = cli.build_parser()
    for command in (
        "verify-credentials",
        "add-artist",
        "list-artists",
        "scan-now",
        "queue-status",
        "serve",
    ):
        args = parser.parse_args([command] + (["x"] if command == "add-artist" else []))
        assert callable(args.func)
        assert isinstance(args.needs_async, bool)


def test_cli_add_artist_defaults() -> None:
    args = cli.build_parser().parse_args(["add-artist", "Nils Frahm"])
    assert args.query == "Nils Frahm"
    assert args.index == 0
    assert args.scan is False
    assert args.needs_async is True


def test_cli_serve_is_synchronous() -> None:
    args = cli.build_parser().parse_args(["serve", "--port", "9999"])
    assert args.needs_async is False
    assert args.port == 9999


def test_cli_no_command_prints_help(capsys: pytest.CaptureFixture[str]) -> None:
    assert cli.main([]) == cli.EXIT_USAGE
    assert "verify-credentials" in capsys.readouterr().out


def test_cli_table_alignment(capsys: pytest.CaptureFixture[str]) -> None:
    cli.table(("a", "bb"), [(1, "xyz"), ("longer", None)])
    lines = capsys.readouterr().out.splitlines()
    assert lines[0].startswith("a")
    assert lines[1].startswith("-")
    assert lines[3].startswith("longer")
