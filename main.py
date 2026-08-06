"""Fonoteca application entrypoint.

Builds the FastAPI application, wires the lifespan to
:func:`app.core.state.init_state` / :func:`app.core.state.shutdown_state`,
mounts the static assets and includes both routers (the HTML UI and the JSON
API).  Run it with either of::

    ./.venv/bin/python -m uvicorn main:app
    ./.venv/bin/python main.py

The lifespan is deliberately forgiving: a missing or rejected Qobuz credential
never prevents the server from booting, because the web UI is how the user is
supposed to find out that something is wrong.  Failures are recorded on the
application state (``credentials_ok`` / ``app_secret_ok`` / ``startup_errors``)
and surfaced as banners by :func:`app.api.deps.banner_context`.
"""

from __future__ import annotations

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Any

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import HTMLResponse, JSONResponse, Response
from fastapi.staticfiles import StaticFiles
from starlette.exceptions import HTTPException as StarletteHTTPException

from app import __version__
from app.api import routes_api, routes_ui
from app.api.deps import STATIC_DIR, render
from app.config import Settings, get_settings
from app.core.state import init_state, shutdown_state, state_or_none
from app.db import dispose_engine, init_db
from app.logging_conf import get_logger, setup_logging

__all__ = ["app", "create_app", "lifespan", "main"]

logger = get_logger("fonoteca.main")

DESCRIPTION = """
Fonoteca follows artists on Qobuz and downloads their new releases, the way
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
    logger.info("Starting Fonoteca %s", __version__)

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
        logger.info("Shutting down Fonoteca")
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
            return JSONResponse(
                {"ok": False, "error": detail, "status_code": exc.status_code},
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
            "Fonoteca hit an unexpected error. The details are in data/fonoteca.log.",
            detail=f"{type(exc).__name__}: {exc}",
        )


# ---------------------------------------------------------------------------
# Application factory
# ---------------------------------------------------------------------------
def create_app(settings: Settings | None = None) -> FastAPI:
    """Build the FastAPI application.

    Args:
        settings: Override settings (tests); defaults to the cached singleton.
    """
    settings = settings or get_settings()
    setup_logging(settings=settings)

    application = FastAPI(
        title="Fonoteca",
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
    application.include_router(routes_ui.router)

    register_exception_handlers(application)
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
