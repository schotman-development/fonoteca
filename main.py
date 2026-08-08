"""Qobuzarr application entrypoint.

Builds the FastAPI application, wires the lifespan to
:func:`app.core.state.init_state` / :func:`app.core.state.shutdown_state`,
mounts the static assets and includes the routers.  Run it with either of::

    ./.venv/bin/python -m uvicorn main:app
    ./.venv/bin/python main.py

The lifespan is deliberately forgiving: a missing or rejected Qobuz credential
never prevents the server from booting, because the web UI is how the user is
supposed to find out that something is wrong.  Failures are recorded on the
application state (``credentials_ok`` / ``app_secret_ok`` / ``startup_errors``)
and surfaced as banners by :func:`app.api.deps.banner_context`.

**Two front ends are mounted, and only one of them is permanent.** The React
single-page app built into ``static/app`` owns the site: every path that is not
an API route, a static file or the old UI is answered with its ``index.html``
and the client router takes it from there.  The server-rendered Jinja UI is
still included, under ``/legacy``, so nothing that worked yesterday is
unreachable while the SPA is finished — see :func:`create_app` for why that
prefix is transitional and what happens when it goes.
"""

from __future__ import annotations

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, Response
from fastapi.staticfiles import StaticFiles
from starlette.exceptions import HTTPException as StarletteHTTPException

from app import __version__
from app.api import routes_api, routes_ui
from app.api.deps import STATIC_DIR, render
from app.config import Settings, get_settings
from app.core.state import init_state, shutdown_state, state_or_none
from app.db import dispose_engine, init_db
from app.logging_conf import get_logger, setup_logging

__all__ = ["app", "create_app", "lifespan", "main", "LEGACY_PREFIX", "SPA_INDEX"]

logger = get_logger("qobuzarr.main")

#: Where the old server-rendered UI now lives. **Transitional.** See
#: :func:`create_app`.
LEGACY_PREFIX = "/legacy"

#: The Vite build. ``static/app`` is inside the existing ``/static`` mount, so
#: the hashed assets under ``static/app/assets`` are already served — only the
#: shell needs a route of its own, because it answers for paths that do not
#: exist on disk.
SPA_DIR = STATIC_DIR / "app"
SPA_INDEX = SPA_DIR / "index.html"

#: First path segments that must never be answered with the SPA shell. A typo'd
#: endpoint returning ``200 text/html`` does not look like a 404 to a client — it
#: looks like a JSON parse error raised from wherever the response was eventually
#: used, with the offending URL nowhere in sight.
#:
#: ``health`` is deliberately **absent**, even though :func:`wants_json` names
#: it. The JSON surface there is exactly one path, ``/health``, and the router
#: already owns it — nothing can reach the catch-all by mistyping a sibling
#: because it has none, so reserving the whole prefix would protect an endpoint
#: that is not at risk.
#:
#: That the probe wins is also why the client's Health section is addressed
#: ``/system`` and not ``/health``: a hard reload on ``/health`` hands the
#: browser the JSON probe instead of the application, and a hard reload is
#: exactly what somebody does when a health screen looks wrong. The section
#: keeps the LABEL "Health" — see ``web/src/routes.tsx`` and
#: ``web/src/shell/nav.ts``, which are the authority on client addresses.
_JSON_ONLY_PREFIXES = ("api", "openapi")

DESCRIPTION = """
Qobuzarr follows artists on Qobuz and downloads their new releases, the way
Lidarr follows artists on indexers.  Everything is fetched from the official
Qobuz API with your own paid account, gated behind a single global rate limiter.
"""


# ---------------------------------------------------------------------------
# Lifespan
# ---------------------------------------------------------------------------
@asynccontextmanager
async def lifespan(application: FastAPI) -> AsyncIterator[None]:
    """Start and stop every long-lived component.

    Startup order: logging -> directories/database -> application state
    (rate limiter, Qobuz client + login + app-secret resolution, indexer,
    download-queue worker, APScheduler).  Shutdown reverses it and disposes the
    SQLAlchemy engine.
    """
    settings = get_settings()
    setup_logging(settings=settings)
    logger.info("Starting Qobuzarr %s", __version__)

    settings.ensure_directories()
    await init_db()

    try:
        state = await init_state(settings=settings)
    except Exception as exc:  # noqa: BLE001 - the UI must still come up
        logger.exception("Application state failed to initialise: %s", exc)
        state = None

    if state is not None:
        for problem in state.startup_errors:
            logger.warning("Startup warning: %s", problem)
        logger.info(
            "Listening on http://%s:%s (credentials_ok=%s, app_secret_ok=%s, library=%s)",
            settings.host,
            settings.port,
            state.credentials_ok,
            state.app_secret_ok,
            settings.library_path,
        )

    application.state.settings = settings
    application.state.core = state

    try:
        yield
    finally:
        logger.info("Shutting down Qobuzarr")
        try:
            await shutdown_state()
        except Exception as exc:  # noqa: BLE001 - shutdown must not raise
            logger.warning("Error during state shutdown: %s", exc)
        try:
            await dispose_engine()
        except Exception as exc:  # noqa: BLE001
            logger.warning("Error disposing the database engine: %s", exc)
        logger.info("Goodbye")


# ---------------------------------------------------------------------------
# Error handling
# ---------------------------------------------------------------------------
def wants_json(request: Request) -> bool:
    """True when the caller expects JSON rather than an HTML page."""
    path = request.url.path
    if path.startswith("/api") or path == "/health" or path.startswith("/openapi"):
        return True
    accept = request.headers.get("accept", "")
    if "text/html" in accept:
        return False
    return "application/json" in accept


_STATUS_TITLES: dict[int, str] = {
    400: "Bad request",
    401: "Not authorised",
    403: "Forbidden",
    404: "Not found",
    405: "Method not allowed",
    422: "Invalid input",
    500: "Something went wrong",
    503: "Service unavailable",
}


def _error_page(
    request: Request,
    status_code: int,
    message: str,
    detail: str | None = None,
) -> Response:
    """Render the friendly HTML error page, falling back to bare HTML."""
    title = _STATUS_TITLES.get(status_code, "Error")
    try:
        return render(
            request,
            "error.html",
            {
                "status_code": status_code,
                "title": title,
                "message": message,
                "detail": detail,
            },
            status_code=status_code,
        )
    except Exception as exc:  # noqa: BLE001 - never fail while reporting a failure
        logger.error("Error page could not be rendered: %s", exc)
        return HTMLResponse(
            f"<h1>{status_code} — {title}</h1><p>{message}</p>",
            status_code=status_code,
        )


def register_exception_handlers(application: FastAPI) -> None:
    """Attach HTML/JSON-aware handlers for HTTP, validation and crash errors."""

    @application.exception_handler(StarletteHTTPException)
    async def http_exception_handler(  # type: ignore[unused-ignore]
        request: Request, exc: StarletteHTTPException
    ) -> Response:
        detail = exc.detail if isinstance(exc.detail, str) else str(exc.detail)
        if wants_json(request):
            payload: dict[str, Any] = {
                "ok": False,
                "error": detail,
                "status_code": exc.status_code,
            }
            # A structured detail (``routes_api.ErrorDetail``) carries the fields
            # the failing screen needs to re-draw itself — which request was
            # refused, and what a good value would have looked like. It also
            # stringifies to the sentence above, so the envelope keeps its shape
            # and the HTML branch keeps working: ``error`` is always prose, and
            # ``detail`` is present only when there is more to say than prose.
            if isinstance(exc.detail, dict):
                payload["detail"] = {
                    key: value
                    for key, value in exc.detail.items()
                    if key != "error"
                }
            return JSONResponse(
                payload,
                status_code=exc.status_code,
                headers=getattr(exc, "headers", None),
            )
        return _error_page(request, exc.status_code, detail)

    @application.exception_handler(RequestValidationError)
    async def validation_exception_handler(  # type: ignore[unused-ignore]
        request: Request, exc: RequestValidationError
    ) -> Response:
        if wants_json(request):
            return JSONResponse(
                {"ok": False, "error": "Invalid request", "detail": exc.errors()},
                status_code=422,
            )
        return _error_page(
            request,
            422,
            "The form could not be processed because some values were invalid.",
            detail="\n".join(
                f"{'.'.join(str(part) for part in err.get('loc', ()))}: {err.get('msg', '')}"
                for err in exc.errors()
            ),
        )

    @application.exception_handler(Exception)
    async def unhandled_exception_handler(  # type: ignore[unused-ignore]
        request: Request, exc: Exception
    ) -> Response:
        logger.exception("Unhandled error on %s %s", request.method, request.url.path)
        if wants_json(request):
            return JSONResponse(
                {"ok": False, "error": "Internal server error", "detail": str(exc)},
                status_code=500,
            )
        return _error_page(
            request,
            500,
            "Qobuzarr hit an unexpected error. The details are in data/qobuzarr.log.",
            detail=f"{type(exc).__name__}: {exc}",
        )


# ---------------------------------------------------------------------------
# The single-page app shell
# ---------------------------------------------------------------------------
#: Shown instead of the shell when ``static/app/index.html`` is not there. A
#: checkout without a build is the normal state of a fresh clone, and answering
#: it with a stack trace teaches nothing — the fix is one command and this names
#: it. Deliberately plain: it must not depend on the Jinja environment, the
#: stylesheet or anything else the SPA was supposed to replace.
_SPA_MISSING_HTML = """<!doctype html>
<meta charset="utf-8">
<title>Qobuzarr — front end not built</title>
<style>
  body {{ font: 16px/1.6 system-ui, sans-serif; margin: 12vh auto; max-width: 40rem;
         padding: 0 1.5rem; color: #14161A; }}
  code {{ background: #F4F2EE; padding: .15em .4em; border-radius: 4px; }}
  pre {{ background: #F4F2EE; padding: 1rem; border-radius: 8px; overflow-x: auto; }}
  a {{ color: #14337F; }}
</style>
<h1>The front end has not been built yet</h1>
<p>Qobuzarr is running — this is the web UI, and it is missing. The React app is
built into <code>{path}</code>, and that file is not there.</p>
<pre>npm install --prefix web
npm run build --prefix web</pre>
<p>Then reload this page; the server picks the build up without a restart.</p>
<p>Meanwhile the JSON API is entirely usable:
<a href="/api/docs">/api/docs</a>,
<a href="/api/status">/api/status</a>,
<a href="/health">/health</a> — and the previous server-rendered UI is still
served under <a href="{legacy}/">{legacy}/</a>.</p>
"""


def spa_shell() -> Response:
    """Return the SPA's ``index.html``, or an explanation of its absence.

    ``Cache-Control: no-store`` on the shell and nothing else. The shell names
    the hashed asset bundles that actually hold the application, so a cached copy
    of it is a client pinned to a deploy that no longer exists — which presents
    as a blank page and a 404 on a filename nobody recognises. The bundles
    themselves are content-addressed and cached by the ``/static`` mount for as
    long as anyone likes.

    Existence is checked per request rather than at startup so a build that
    lands while the server is running is simply picked up. This costs one
    ``stat`` on a path the OS has cached, and it removes the one instruction
    ("restart it as well") that everybody forgets.
    """
    if SPA_INDEX.is_file():
        return FileResponse(
            SPA_INDEX,
            media_type="text/html",
            headers={"Cache-Control": "no-store"},
        )
    logger.warning("SPA shell missing: %s (run `npm run build --prefix web`)", SPA_INDEX)
    # 200, not 500 and not 503: the request was answered, the answer is a page
    # that says what to do, and a build step that has not been run is not the
    # server failing.
    return HTMLResponse(
        _SPA_MISSING_HTML.format(path=SPA_INDEX, legacy=LEGACY_PREFIX),
        headers={"Cache-Control": "no-store"},
    )


def register_spa(application: FastAPI) -> None:
    """Serve the SPA shell for every path nothing else claimed.

    Registered **last**, because it matches everything: Starlette tries routes in
    the order they were added, so the ``/static`` mount, both API routers and the
    legacy UI all get first refusal and this only ever sees what is left.

    Two families of path are refused rather than answered:

    ``/api`` · ``/openapi``
        A mistyped endpoint must come back as JSON. Answering it with the shell
        means ``200 text/html`` where a client expected an object, and the error
        it eventually raises is a parse failure with no mention of the URL that
        caused it — the bug reports itself as being somewhere else. Raising a
        404 here puts it through the normal envelope,
        ``{"ok": false, "error": ..., "status_code": 404}``. ``/health`` is one
        exact path with no siblings and is not reserved — see
        :data:`_JSON_ONLY_PREFIXES`. The client's Health section is at
        ``/system`` precisely because the probe owns ``/health``.

    ``/legacy``
        The old UI keeps its own 404, which is the Jinja error page, because
        while it is still mounted it should behave as it always did.
    """

    @application.get("/{full_path:path}", include_in_schema=False)
    async def spa_catch_all(full_path: str) -> Response:  # type: ignore[unused-ignore]
        """Hand the path to the client router."""
        head = full_path.split("/", 1)[0].lower()
        # ``openapi.json`` is one segment, not two, and a code generator asking
        # for the conventional path must not be handed a web page.
        if head.split(".", 1)[0] in _JSON_ONLY_PREFIXES:
            raise StarletteHTTPException(
                status_code=404, detail=f"No such endpoint: /{full_path}"
            )
        if head == LEGACY_PREFIX.strip("/"):
            raise StarletteHTTPException(
                status_code=404, detail=f"No such page: /{full_path}"
            )
        return spa_shell()


# ---------------------------------------------------------------------------
# Application factory
# ---------------------------------------------------------------------------
def create_app(settings: Settings | None = None) -> FastAPI:
    """Build the FastAPI application.

    Registration order is load-bearing — Starlette matches routes in the order
    they are added, and the SPA catch-all matches literally everything:

    1. ``/static`` (includes ``static/app/assets``, the hashed build output),
    2. ``/health`` and ``/api/*``,
    3. the old server-rendered UI under ``/legacy``,
    4. the catch-all, which answers everything else with the SPA shell.

    **The ``/legacy`` prefix is transitional.** The rebuild deletes
    ``app/api/routes_ui.py``, ``templates/`` and ``static/style.css`` outright;
    until the SPA covers every screen, moving that router out of the way is what
    lets both exist at once — it no longer owns ``/``, ``/artists``, ``/wanted``
    and the rest, so the client router can. Nothing new should be added to it,
    the templates' own links still point at the unprefixed paths (they were
    written when the old UI owned the site, and are not being rewritten for a
    layer that is going away), and when the SPA is complete this line and the
    import above it are deleted together.

    Args:
        settings: Override settings (tests); defaults to the cached singleton.
    """
    settings = settings or get_settings()
    setup_logging(settings=settings)

    application = FastAPI(
        title="Qobuzarr",
        description=DESCRIPTION,
        version=__version__,
        lifespan=lifespan,
        docs_url="/api/docs",
        redoc_url=None,
        openapi_url="/api/openapi.json",
    )

    STATIC_DIR.mkdir(parents=True, exist_ok=True)
    application.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

    application.include_router(routes_api.health_router)
    application.include_router(routes_api.router)
    # Transitional: see the note above. The old UI keeps working, one prefix
    # deeper, and stops owning the paths the SPA needs.
    application.include_router(routes_ui.router, prefix=LEGACY_PREFIX)

    register_exception_handlers(application)
    register_spa(application)
    return application


app = create_app()


# ---------------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------------
def main(argv: list[str] | None = None) -> int:
    """Run the development/production server with uvicorn."""
    import uvicorn

    settings = get_settings()
    setup_logging(settings=settings)
    uvicorn.run(
        "main:app" if settings.reload else app,
        host=settings.host,
        port=settings.port,
        reload=settings.reload,
        log_level=str(settings.log_level.value).lower(),
        access_log=True,
    )
    return 0


def current_state() -> Any:
    """The live :class:`app.core.state.AppState`, or ``None`` before startup."""
    return state_or_none()


if __name__ == "__main__":
    raise SystemExit(main())
