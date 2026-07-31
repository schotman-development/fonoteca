"""HTML routes: the Jinja2 + HTMX web interface.

Page routes render full documents that extend ``templates/base.html``:

=====================  ==========================================
``GET /``              Dashboard
``GET /artists``       Library — followed artists (grid or table)
``GET /artists/{id}``  Artist detail (releases, per-artist settings)
``GET /add``           Search Qobuz and follow artists
``GET /wanted``        Cross-artist backlog of missing releases
``GET /queue``         Download queue
``GET /activity``      Activity history
``GET /library/scan``  Disk scan — what is already in the library folder
``GET /library/tidy``  Re-file, re-tag and the trash — the writing side of the disk
``GET /settings``      Effective configuration
=====================  ==========================================

``/partials/*`` returns HTML fragments for HTMX polling, and ``/ui/*`` handles
the form posts.  Both reuse the service functions in
:mod:`app.api.routes_api`, so an action taken in the browser and the equivalent
``/api`` call do exactly the same thing.

The router degrades gracefully: no credentials, no Qobuz client or a tripped
circuit breaker produce banners (see :func:`app.api.deps.banner_context`), not
tracebacks.
"""

from __future__ import annotations

import json
from typing import Annotated, Any, Literal
from urllib.parse import urlencode

from fastapi import APIRouter, Depends, Form, HTTPException, Query, Request
from fastapi.responses import RedirectResponse, Response
from sqlalchemy.ext.asyncio import AsyncSession

from app.api.deps import (
    albums_by_status,
    artist_to_out,
    build_settings_out,
    build_status,
    get_settings_dep,
    indexer_status,
    library_import_status,
    library_scan_status,
    library_stats,
    list_activity,
    list_albums_for_artist,
    list_artists,
    list_queue_items,
    list_wanted_albums,
    naming_preview,
    nav_counts,
    queue_stats,
    rate_limit_status,
    render,
    templates,
)
from app.api.params import (
    EmptyAsNone,
    OptActivityLevelQuery,
    OptAlbumStatusQuery,
    OptBoolQuery,
    OptQueueStateQuery,
    OptStrQuery,
)
from app.api.routes_api import (
    apply_artist_update,
    bulk_update_artists,
    cancel_library_import,
    cancel_queue_item,
    delete_album_files,
    delete_artist,
    empty_trash,
    follow_artist,
    get_album_or_404,
    get_artist_or_404,
    get_queue_item_or_404,
    queue_album,
    queue_all_wanted,
    queue_wanted_for_artist,
    refile_library,
    restore_trash_entry,
    retag_library,
    retry_queue_item,
    run_search,
    scan_all,
    scan_artist,
    scan_library,
    set_album_monitored,
    start_library_import,
    trash_contents,
)
from app.config import Settings
from app.db import get_session
from app.logging_conf import get_logger
from app.models import Album, AlbumStatus, Artist, MonitorMode, QueueState
from app.schemas import (
    AlbumUpdateIn,
    ArtistBulkUpdateIn,
    ArtistCreateIn,
    ArtistUpdateIn,
    LibraryImportStartIn,
    SearchResultOut,
)

__all__ = ["router"]

logger = get_logger(__name__)

router = APIRouter(include_in_schema=False)

SessionDep = Annotated[AsyncSession, Depends(get_session)]
SettingsDep = Annotated[Settings, Depends(get_settings_dep)]

#: How many backlog rows the dashboard previews. The page and the fragment it
#: swaps in have to agree, or ignoring a row would change the card's length.
DASHBOARD_WANTED = 6

#: How many queue rows and artist tiles the dashboard previews. Same reasoning.
DASHBOARD_QUEUE = 6
DASHBOARD_ARTISTS = 7

#: Fired on the body by every mutating fragment; the live regions listen for it
#: so a press updates the counters immediately rather than at the next poll.
LIVE_REFRESH_EVENT = "qobuzarr:refresh"


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def _fragment(
    request: Request,
    template_name: str,
    context: dict[str, Any],
    *,
    toast: str | None = None,
    level: str = "info",
    refresh: str | None = None,
) -> Response:
    """Render an HTMX fragment, optionally attaching a toast and a refresh event.

    Every fragment fires ``qobuzarr:refresh`` on the body. Only ``/ui/*`` — the
    mutating half of the HTML surface — renders through here, so that event
    means "something changed"; the live regions listen for it and re-fetch at
    once instead of showing a stale count until their next poll.

    Args:
        request: The current request.
        template_name: Partial template path.
        context: Template variables.
        toast: Message for the client-side toast strip.
        level: Toast severity (``info``/``success``/``warning``/``error``).
        refresh: A further HTMX event name to fire (e.g. ``qobuzarr:import``).
    """
    response = templates.TemplateResponse(request, template_name, context)
    triggers: dict[str, Any] = {LIVE_REFRESH_EVENT: True}
    if toast:
        triggers["qobuzarr:toast"] = {"message": toast, "level": level}
    if refresh:
        triggers[refresh] = True
    response.headers["HX-Trigger"] = json.dumps(triggers)
    return response


def _redirect(target: str, request: Request, *, toast: str | None = None) -> Response:
    """Redirect, using ``HX-Redirect`` when the request came from HTMX."""
    if request.headers.get("HX-Request"):
        response = Response(status_code=204)
        response.headers["HX-Redirect"] = target
        if toast:
            response.headers["HX-Trigger"] = json.dumps(
                {"qobuzarr:toast": {"message": toast, "level": "info"}}
            )
        return response
    return RedirectResponse(target, status_code=303)


def _parse_bool(value: str | None) -> bool:
    """Interpret an HTML checkbox value."""
    return str(value).lower() in ("1", "true", "on", "yes")


def _tristate(value: str | None) -> bool | None:
    """Read a "no change / yes / no" bulk-edit dropdown.

    An empty string is what "leave alone" submits and must stay ``None``, not
    collapse to ``False`` — otherwise a bulk mode change would silently
    unmonitor every artist it touched.
    """
    if value is None or value.strip() == "":
        return None
    return _parse_bool(value)


def _artist_list_context(
    artists: list[Any],
    total: int,
    q: str | None,
    monitored: bool | None,
    sort: str,
    order: str,
    view: str,
) -> dict[str, Any]:
    """Context shared by the artists page and its list fragment.

    The fragment re-renders itself after a bulk edit, so it has to carry the
    active filter state forward or the list would silently reset to unfiltered.
    """
    return {
        "artists": artists,
        "total": total,
        "q": q or "",
        "monitored": monitored,
        "sort": sort,
        "order": order,
        "view": view,
    }


def _scope(**params: Any) -> str:
    """Encode a screen's active filters as a query string.

    Row actions append this to their POST URL so the table they get back is the
    one the user was looking at, not the unfiltered default. ``None`` means "the
    parameter was not set" and is dropped; ``False`` is a real filter value and
    is kept, which is why this cannot be a plain truthiness test.
    """
    pairs = {
        name: ("true" if value is True else "false" if value is False else str(value))
        for name, value in params.items()
        if value is not None and value != ""
    }
    return urlencode(pairs)


def _wanted_context(
    albums: list[Any],
    total: int,
    *,
    q: str | None = None,
    status: AlbumStatus | None = None,
    monitored: bool | None = None,
) -> dict[str, Any]:
    """Context shared by the Wanted page, its fragment and its row actions."""
    return {
        "albums": albums,
        "total": total,
        "q": q or "",
        "filter_status": status.value if status else "",
        "monitored": monitored,
        "scope": _scope(
            q=q, status=status.value if status else None, monitored=monitored
        ),
    }


def _album_rows_context(
    albums: list[Any],
    total: int,
    artist_id: str,
    *,
    q: str | None = None,
    status: AlbumStatus | None = None,
    release_type: str | None = None,
) -> dict[str, Any]:
    """Context shared by the artist-detail album table and its row actions."""
    return {
        "albums": albums,
        "album_total": total,
        "artist_id": artist_id,
        "filter_q": q or "",
        "scope": _scope(
            q=q,
            status=status.value if status else None,
            release_type=release_type,
        ),
    }


async def _queue_context(
    session: AsyncSession,
    items: list[Any],
    total: int,
    state: QueueState | None = None,
) -> dict[str, Any]:
    """Context shared by the queue table, its poll fragment and its row actions."""
    return {
        "items": items,
        "total": total,
        "stats": await queue_stats(session),
        "filter_state": state.value if state else "",
        "scope": _scope(state=state.value if state else None),
    }


async def _artist_stats_context(
    session: AsyncSession, artist: Artist
) -> dict[str, Any]:
    """One artist's album roll-up: the ``ArtistOut`` and the per-status counts.

    The detail page renders this once and its stats fragment re-renders it every
    few seconds. Both go through here so the headline numbers and the counts in
    the status dropdown can never drift apart.
    """
    all_albums, _ = await list_albums_for_artist(session, artist.id, limit=2000)
    counts: dict[str, int] = {status.value: 0 for status in AlbumStatus}
    for album in all_albums:
        counts[album.status.value] = counts.get(album.status.value, 0) + 1
    return {
        "artist": artist_to_out(
            artist,
            album_count=len(all_albums),
            wanted_count=counts.get(AlbumStatus.WANTED.value, 0)
            + counts.get(AlbumStatus.QUEUED.value, 0),
            downloaded_count=counts.get(AlbumStatus.DOWNLOADED.value, 0),
        ),
        "counts": counts,
    }


def _monitor_toast(album: Album) -> str:
    """What the toggle says it did.

    Unmonitoring a *wanted* release also marks it skipped (see
    ``set_album_monitored``), so the row vanishes from the Wanted list. Say that
    out loud rather than letting the row silently disappear.
    """
    if album.monitored:
        return f"Monitoring {album.display_title}."
    return f"Ignoring {album.display_title} — nothing will download it."


# ---------------------------------------------------------------------------
# Pages
# ---------------------------------------------------------------------------
@router.get("/", response_class=Response, summary="Dashboard")
async def page_dashboard(request: Request, session: SessionDep) -> Response:
    """Counts, indexer countdown, rate-limiter budget and recent activity.

    Everything here is also served as a fragment below, so the page renders the
    first frame and the browser keeps it current without a reload.
    """
    status = await build_status(session, activity_limit=12)
    by_status = await albums_by_status(session)
    artists, artist_total = await list_artists(
        session, sort="last_checked_at", limit=DASHBOARD_ARTISTS
    )
    queue_items, _ = await list_queue_items(session, limit=DASHBOARD_QUEUE)
    wanted, wanted_total = await list_wanted_albums(session, limit=DASHBOARD_WANTED)
    return render(
        request,
        "dashboard.html",
        {
            "status": status,
            "albums_by_status": by_status,
            "recent_artists": artists,
            "artist_total": artist_total,
            "queue_items": queue_items,
            "wanted_albums": wanted,
            "wanted_total": wanted_total,
        },
        active="dashboard",
    )


@router.get("/artists", response_class=Response, summary="Followed artists")
async def page_artists(
    request: Request,
    session: SessionDep,
    q: OptStrQuery = None,
    monitored: OptBoolQuery = None,
    sort: str = Query("name"),
    order: Literal["asc", "desc"] = Query("asc"),
    view: Literal["grid", "table"] = Query("grid"),
) -> Response:
    """The library's artist list, as an artwork grid (default) or a table."""
    artists, total = await list_artists(
        session, query=q, monitored=monitored, sort=sort, order=order, limit=1000
    )
    return render(
        request,
        "artists.html",
        _artist_list_context(artists, total, q, monitored, sort, order, view),
        active="artists",
    )


@router.get("/artists/{artist_id}", response_class=Response, summary="Artist detail")
async def page_artist_detail(
    request: Request,
    session: SessionDep,
    artist_id: str,
    album_status: Annotated[
        AlbumStatus | None, EmptyAsNone, Query(alias="status")
    ] = None,
    release_type: OptStrQuery = None,
    q: OptStrQuery = None,
) -> Response:
    """One artist: their releases, states and per-artist monitoring settings."""
    artist = await get_artist_or_404(session, artist_id)
    albums, total = await list_albums_for_artist(
        session,
        artist.id,
        status=album_status,
        release_type=release_type,
        query=q,
        limit=1000,
    )
    activity, _ = await list_activity(session, artist_id=artist.id, limit=25)
    context = _album_rows_context(
        albums, total, artist.id, q=q, status=album_status, release_type=release_type
    )
    context.update(await _artist_stats_context(session, artist))
    context.update(
        {
            "filter_status": album_status.value if album_status else "",
            "filter_type": release_type or "",
            "activity": activity,
        }
    )
    return render(request, "artist_detail.html", context, active="artists")


@router.get("/add", response_class=Response, summary="Add artist")
async def page_add_artist(
    request: Request,
    session: SessionDep,
    q: OptStrQuery = None,
) -> Response:
    """Search Qobuz for artists (and albums) and follow them."""
    result: SearchResultOut | None = None
    error: str | None = None
    if q:
        try:
            result = await run_search(session, q, search_type="all", limit=25)
        except HTTPException as exc:
            error = str(exc.detail)
    return render(
        request,
        "add_artist.html",
        {"q": q or "", "result": result, "search_error": error},
        active="add",
    )


@router.get("/wanted", response_class=Response, summary="Missing releases")
async def page_wanted(
    request: Request,
    session: SessionDep,
    q: OptStrQuery = None,
    album_status: Annotated[
        AlbumStatus | None, EmptyAsNone, Query(alias="status")
    ] = None,
    monitored: OptBoolQuery = True,
) -> Response:
    """The cross-artist backlog: everything wanted that is not yet on disk."""
    items, total = await list_wanted_albums(
        session, query=q, status=album_status, monitored=monitored, limit=500
    )
    context = _wanted_context(
        items, total, q=q, status=album_status, monitored=monitored
    )
    # "Download all" queues the whole monitored backlog, not the filtered rows
    # in front of you, so the button has to count what it will actually do.
    _rows, context["queueable"] = await list_wanted_albums(
        session, status=AlbumStatus.WANTED, monitored=True, limit=1
    )
    context["showing_filtered"] = bool(q or album_status or monitored is not True)
    return render(request, "wanted.html", context, active="wanted")


@router.get("/queue", response_class=Response, summary="Download queue")
async def page_queue(
    request: Request,
    session: SessionDep,
    state: OptQueueStateQuery = None,
) -> Response:
    """The sequential download queue, polled live by HTMX."""
    items, total = await list_queue_items(session, state=state, limit=300)
    return render(
        request,
        "queue.html",
        await _queue_context(session, items, total, state),
        active="queue",
    )


@router.get("/activity", response_class=Response, summary="Activity history")
async def page_activity(
    request: Request,
    session: SessionDep,
    level: OptActivityLevelQuery = None,
    event: OptStrQuery = None,
    limit: int = Query(200, ge=1, le=1000),
) -> Response:
    """The append-only history feed."""
    items, total = await list_activity(
        session, level=level, event=event, limit=limit
    )
    return render(
        request,
        "activity.html",
        {
            "items": items,
            "total": total,
            "filter_level": level.value if level else "",
            "filter_event": event or "",
            "limit": limit,
        },
        active="activity",
    )


@router.get("/settings", response_class=Response, summary="Settings")
async def page_settings(
    request: Request, session: SessionDep, settings: SettingsDep
) -> Response:
    """Read-only effective configuration plus the per-artist defaults."""
    return render(
        request,
        "settings.html",
        {
            "config": build_settings_out(settings),
            "rate_limit": rate_limit_status(),
            "indexer": await indexer_status(session),
            "library": await library_stats(session),
            "naming_paths": naming_preview(settings),
        },
        active="settings",
    )


@router.get("/library/scan", response_class=Response, summary="Disk scan")
async def page_library_scan(
    request: Request, session: SessionDep, settings: SettingsDep
) -> Response:
    """Report of the last disk scan, the import controls, and their progress."""
    return render(
        request,
        "library_scan.html",
        {
            "scan": await library_scan_status(session, settings),
            "import_status": await library_import_status(session),
        },
        active="library-scan",
    )


async def _tidy_context(
    session: AsyncSession,
    settings: Settings,
    *,
    artist_id: str | None = None,
    refile: Any = None,
    retag: Any = None,
) -> dict[str, Any]:
    """Context shared by the Library tidy page and every fragment it swaps.

    ``refile``/``retag`` carry the result of whatever the user just pressed;
    both are ``None`` on a plain page load, which is what makes the panels
    render their "nothing has been run yet" state.
    """
    artist = await get_artist_or_404(session, artist_id) if artist_id else None
    return {
        "artist": artist_to_out(artist) if artist is not None else None,
        "artist_id": artist_id or "",
        "scope": _scope(artist_id=artist_id),
        "trash": await trash_contents(settings),
        "refile": refile,
        "retag": retag,
        "library_path": str(settings.library_path),
        "trash_path": str(settings.trash_dir),
    }


@router.get("/library/tidy", response_class=Response, summary="Library tidy")
async def page_library_tidy(
    request: Request,
    session: SessionDep,
    settings: SettingsDep,
    artist_id: OptStrQuery = None,
) -> Response:
    """Re-file, re-tag and the trash — everything that changes files on disk.

    Deliberately one page away from the rest of the UI: these are the only
    actions in Qobuzarr that modify or remove music you already have, and the
    re-file panel previews before it moves anything.
    """
    return render(
        request,
        "library_tidy.html",
        await _tidy_context(session, settings, artist_id=artist_id),
        active="library-tidy",
    )


# ---------------------------------------------------------------------------
# HTMX partials
# ---------------------------------------------------------------------------
@router.get("/partials/status-bar", response_class=Response)
async def partial_status_bar(request: Request, session: SessionDep) -> Response:
    """Live status strip: indexer countdown, queue depth, rate-limiter budget."""
    return templates.TemplateResponse(
        request,
        "partials/status_bar.html",
        {"status": await build_status(session, activity_limit=0)},
    )


@router.get("/partials/dashboard-wanted", response_class=Response)
async def partial_dashboard_wanted(request: Request, session: SessionDep) -> Response:
    """The dashboard's Wanted preview — re-rendered when a row is ignored."""
    items, total = await list_wanted_albums(session, limit=DASHBOARD_WANTED)
    return templates.TemplateResponse(
        request,
        "partials/dashboard_wanted.html",
        {"wanted_albums": items, "wanted_total": total},
    )


@router.get("/partials/dashboard/metrics", response_class=Response)
async def partial_dashboard_metrics(request: Request, session: SessionDep) -> Response:
    """The dashboard's headline counters and the indexer countdown."""
    return templates.TemplateResponse(
        request,
        "partials/dashboard_metrics.html",
        {"status": await build_status(session, activity_limit=0)},
    )


@router.get("/partials/dashboard/status", response_class=Response)
async def partial_dashboard_status(request: Request, session: SessionDep) -> Response:
    """The dashboard's right-hand column: indexer, rate limiter, status mix."""
    return templates.TemplateResponse(
        request,
        "partials/dashboard_status.html",
        {
            "status": await build_status(session, activity_limit=0),
            "albums_by_status": await albums_by_status(session),
        },
    )


@router.get("/partials/dashboard/queue", response_class=Response)
async def partial_dashboard_queue(request: Request, session: SessionDep) -> Response:
    """The dashboard's queue preview, including the live progress meters."""
    items, _ = await list_queue_items(session, limit=DASHBOARD_QUEUE)
    return templates.TemplateResponse(
        request, "partials/dashboard_queue.html", {"queue_items": items}
    )


@router.get("/partials/dashboard/artists", response_class=Response)
async def partial_dashboard_artists(request: Request, session: SessionDep) -> Response:
    """The "least recently checked" tiles — the indexer's visible work queue."""
    artists, total = await list_artists(
        session, sort="last_checked_at", limit=DASHBOARD_ARTISTS
    )
    return templates.TemplateResponse(
        request,
        "partials/dashboard_artists.html",
        {"recent_artists": artists, "artist_total": total},
    )


@router.get("/partials/nav", response_class=Response)
async def partial_nav(
    request: Request, session: SessionDep, active: str = Query("")
) -> Response:
    """Sidebar navigation, re-fetched so the Wanted/Queue badges stay current."""
    return templates.TemplateResponse(
        request,
        "partials/nav.html",
        {"active": active, "nav_counts": await nav_counts(session)},
    )


@router.get("/partials/artists", response_class=Response)
async def partial_artist_list(
    request: Request,
    session: SessionDep,
    q: OptStrQuery = None,
    monitored: OptBoolQuery = None,
    sort: str = Query("name"),
    order: Literal["asc", "desc"] = Query("asc"),
    view: Literal["grid", "table"] = Query("grid"),
) -> Response:
    """The selectable artist list plus its bulk-edit bar."""
    artists, total = await list_artists(
        session, query=q, monitored=monitored, sort=sort, order=order, limit=1000
    )
    return templates.TemplateResponse(
        request,
        "partials/artist_list.html",
        _artist_list_context(artists, total, q, monitored, sort, order, view),
    )


@router.get("/partials/wanted-rows", response_class=Response)
async def partial_wanted_rows(
    request: Request,
    session: SessionDep,
    q: OptStrQuery = None,
    album_status: Annotated[
        AlbumStatus | None, EmptyAsNone, Query(alias="status")
    ] = None,
    monitored: OptBoolQuery = True,
) -> Response:
    """The backlog table body on the Wanted page."""
    items, total = await list_wanted_albums(
        session, query=q, status=album_status, monitored=monitored, limit=500
    )
    return templates.TemplateResponse(
        request,
        "partials/wanted_rows.html",
        _wanted_context(items, total, q=q, status=album_status, monitored=monitored),
    )


@router.get("/partials/queue-table", response_class=Response)
async def partial_queue_table(
    request: Request,
    session: SessionDep,
    state: OptQueueStateQuery = None,
) -> Response:
    """The queue table body, polled every few seconds."""
    items, total = await list_queue_items(session, state=state, limit=300)
    return templates.TemplateResponse(
        request,
        "partials/queue_table.html",
        await _queue_context(session, items, total, state),
    )


@router.get("/partials/search", response_class=Response)
async def partial_search(
    request: Request,
    session: SessionDep,
    q: str = Query(""),
    type: Literal["all", "artists", "albums"] = Query("all"),
) -> Response:
    """Search results fragment used by the add-artist page."""
    result: SearchResultOut | None = None
    error: str | None = None
    try:
        result = await run_search(session, q, search_type=type, limit=25)
    except HTTPException as exc:
        error = str(exc.detail)
    return templates.TemplateResponse(
        request,
        "partials/search_results.html",
        {"q": q, "result": result, "search_error": error},
    )


@router.get("/partials/albums/{artist_id}", response_class=Response)
async def partial_album_rows(
    request: Request,
    session: SessionDep,
    artist_id: str,
    album_status: Annotated[
        AlbumStatus | None, EmptyAsNone, Query(alias="status")
    ] = None,
    release_type: OptStrQuery = None,
    q: OptStrQuery = None,
) -> Response:
    """The album table body on the artist-detail page, filtered live by search."""
    await get_artist_or_404(session, artist_id)
    albums, total = await list_albums_for_artist(
        session,
        artist_id,
        status=album_status,
        release_type=release_type,
        query=q,
        limit=1000,
    )
    return templates.TemplateResponse(
        request,
        "partials/album_rows.html",
        _album_rows_context(
            albums, total, artist_id, q=q, status=album_status,
            release_type=release_type,
        ),
    )


@router.get("/partials/artists/{artist_id}/stats", response_class=Response)
async def partial_artist_stats(
    request: Request, session: SessionDep, settings: SettingsDep, artist_id: str
) -> Response:
    """One artist's live counters, polled by the detail page."""
    artist = await get_artist_or_404(session, artist_id)
    context = await _artist_stats_context(session, artist)
    context["settings"] = settings
    return templates.TemplateResponse(request, "partials/artist_stats.html", context)


@router.get("/partials/activity", response_class=Response)
async def partial_activity(
    request: Request,
    session: SessionDep,
    level: OptActivityLevelQuery = None,
    event: OptStrQuery = None,
    limit: int = Query(12, ge=1, le=1000),
) -> Response:
    """Recent-activity rows: the dashboard preview and the Activity page feed.

    The page passes its filters through so a refresh keeps the view the user set
    up; the dashboard passes only a limit and gets the newest of everything.
    """
    items, _ = await list_activity(session, level=level, event=event, limit=limit)
    return templates.TemplateResponse(
        request, "partials/activity_rows.html", {"items": items}
    )


@router.get("/partials/library-scan", response_class=Response)
async def partial_library_scan(
    request: Request, session: SessionDep, settings: SettingsDep
) -> Response:
    """The disk-scan report on its own, so the page can refresh it in place."""
    return templates.TemplateResponse(
        request,
        "partials/library_scan_report.html",
        {
            "scan": await library_scan_status(session, settings),
            "import_status": await library_import_status(session),
        },
    )


@router.get("/partials/library-import", response_class=Response)
async def partial_library_import(request: Request, session: SessionDep) -> Response:
    """Import progress, polled every few seconds while a run is in flight."""
    return templates.TemplateResponse(
        request,
        "partials/library_import_progress.html",
        {"import_status": await library_import_status(session)},
    )


# ---------------------------------------------------------------------------
# Form actions
# ---------------------------------------------------------------------------
@router.post("/ui/artists/add", response_class=Response)
async def ui_add_artist(
    request: Request,
    session: SessionDep,
    settings: SettingsDep,
    artist_id: Annotated[str, Form()],
    name: Annotated[str | None, Form()] = None,
    search_now: Annotated[str | None, Form()] = None,
) -> Response:
    """Follow an artist from the search-results fragment."""
    payload = ArtistCreateIn(
        artist_id=artist_id,
        name=name or None,
        search_now=_parse_bool(search_now),
    )
    artist, created = await follow_artist(session, payload, settings)
    return _fragment(
        request,
        "partials/follow_button.html",
        {"artist_id": artist.id, "followed": True, "name": artist.name},
        toast=(
            f"Now following {artist.name}."
            if created
            else f"Already following {artist.name}."
        ),
        level="success",
    )


@router.post("/ui/artists/bulk", response_class=Response)
async def ui_bulk_update_artists(
    request: Request,
    session: SessionDep,
    artist_ids: Annotated[list[str] | None, Form()] = None,
    set_monitored: Annotated[str | None, Form()] = None,
    set_monitor_mode: Annotated[str | None, Form()] = None,
    types_action: Annotated[str | None, Form()] = None,
    release_types: Annotated[list[str] | None, Form()] = None,
    q: Annotated[str | None, Form()] = None,
    monitored: Annotated[str | None, Form()] = None,
    sort: Annotated[str, Form()] = "name",
    order: Annotated[str, Form()] = "asc",
    view: Annotated[str, Form()] = "grid",
) -> Response:
    """Apply the library page's bulk monitoring edit and re-render the list.

    The ``set_*`` prefix keeps the *actions* apart from the like-named *filter*
    fields (``monitored``) that the same POST carries so the refreshed list
    keeps showing what the user was looking at.
    """
    mode: MonitorMode | None = None
    if set_monitor_mode:
        try:
            mode = MonitorMode(set_monitor_mode)
        except ValueError:
            mode = None

    # Release types only mean anything once an action has been chosen; an
    # untouched checkbox row must not wipe everyone's settings.
    action = (types_action or "").strip()
    types: list[str] | None = list(release_types or []) if action else None

    payload = ArtistBulkUpdateIn(
        artist_ids=list(artist_ids or []),
        monitored=_tristate(set_monitored),
        monitor_mode=mode,
        release_types=types,
        release_types_action=action if action in ("set", "add", "remove") else "set",
    )
    message, detail = await bulk_update_artists(session, payload)

    filter_monitored = None if not monitored else _parse_bool(monitored)
    artists, total = await list_artists(
        session,
        query=q or None,
        monitored=filter_monitored,
        sort=sort,
        order=order if order in ("asc", "desc") else "asc",
        limit=1000,
    )
    return _fragment(
        request,
        "partials/artist_list.html",
        _artist_list_context(
            artists,
            total,
            q,
            filter_monitored,
            sort,
            order,
            view if view in ("grid", "table") else "grid",
        ),
        toast=message,
        level=(
            "warning"
            if detail.get("emptied")
            else "success"
            if detail.get("updated")
            else "info"
        ),
        refresh="qobuzarr:selection",
    )


@router.post("/ui/artists/{artist_id}/scan", response_class=Response)
async def ui_scan_artist(
    request: Request, session: SessionDep, artist_id: str
) -> Response:
    """Trigger a manual scan of one artist."""
    artist = await get_artist_or_404(session, artist_id)
    message = await scan_artist(session, artist)
    return _fragment(
        request,
        "partials/inline_message.html",
        {"message": message, "level": "info"},
        toast=message,
    )


@router.post("/ui/artists/{artist_id}/settings", response_class=Response)
async def ui_update_artist(
    request: Request,
    session: SessionDep,
    artist_id: str,
    monitored: Annotated[str | None, Form()] = None,
    monitor_mode: Annotated[str | None, Form()] = None,
    quality_profile: Annotated[str | None, Form()] = None,
    release_types: Annotated[list[str] | None, Form()] = None,
) -> Response:
    """Save the per-artist monitoring settings form."""
    artist = await get_artist_or_404(session, artist_id)
    mode: MonitorMode | None = None
    if monitor_mode:
        try:
            mode = MonitorMode(monitor_mode)
        except ValueError:
            mode = None
    payload = ArtistUpdateIn(
        monitored=_parse_bool(monitored),
        monitor_mode=mode,
        quality_profile=quality_profile or None,
        accepted_release_types=list(release_types or []),
    )
    await apply_artist_update(session, artist, payload)
    return _fragment(
        request,
        "partials/inline_message.html",
        {"message": "Settings saved.", "level": "success"},
        toast="Artist settings saved.",
        level="success",
    )


@router.post("/ui/artists/{artist_id}/delete", response_class=Response)
async def ui_delete_artist(
    request: Request, session: SessionDep, artist_id: str
) -> Response:
    """Unfollow an artist and go back to the artist list."""
    artist = await get_artist_or_404(session, artist_id)
    message = await delete_artist(session, artist)
    return _redirect("/artists", request, toast=message)


#: The album-table filters a row action carries back so the refreshed table is
#: still the one the user was looking at. ``status`` is aliased because the
#: parameter cannot be called ``status`` without shadowing the imported module.
StatusQuery = Annotated[AlbumStatus | None, EmptyAsNone, Query(alias="status")]


@router.post("/ui/albums/{album_id}/delete", response_class=Response)
async def ui_delete_album_files(
    request: Request,
    session: SessionDep,
    album_id: str,
    q: OptStrQuery = None,
    album_status: StatusQuery = None,
    release_type: OptStrQuery = None,
) -> Response:
    """Move one release's files to the trash and refresh the artist's table.

    The files are recoverable from the Library tidy page until the trash is
    emptied, and the album goes back to wanted or skipped — so the toast says
    which, rather than leaving the row's new state to be inferred.
    """
    album = await get_album_or_404(session, album_id)
    title = album.display_title
    result = await delete_album_files(session, album)
    albums, total = await list_albums_for_artist(
        session,
        album.artist_id,
        status=album_status,
        release_type=release_type,
        query=q,
        limit=1000,
    )
    state = result.new_status.value if result.new_status else "skipped"
    return _fragment(
        request,
        "partials/album_rows.html",
        _album_rows_context(
            albums, total, album.artist_id, q=q, status=album_status,
            release_type=release_type,
        ),
        toast=(
            f"Deleted {title} ({result.summary}). It is now {state}; "
            "restore it from Library tidy."
            if result.trashed
            else f"{title} had no files on disk; it is now {state}."
        ),
        level="warning",
    )


@router.post("/ui/albums/{album_id}/queue", response_class=Response)
async def ui_queue_album(
    request: Request,
    session: SessionDep,
    album_id: str,
    q: OptStrQuery = None,
    album_status: StatusQuery = None,
    release_type: OptStrQuery = None,
) -> Response:
    """Queue one album for download and refresh the artist's album table.

    Also the *Upgrade* path: the button is the same POST, because queueing a
    release that is already on disk is exactly what an upgrade is. The download
    loop works out per track which files it may reuse (see
    :mod:`app.core.quality`), so nothing extra needs to be passed here — only
    the wording of the toast changes.
    """
    album = await get_album_or_404(session, album_id)
    upgrading = album.status is AlbumStatus.DOWNLOADED
    _item, created = await queue_album(session, album)
    albums, total = await list_albums_for_artist(
        session,
        album.artist_id,
        status=album_status,
        release_type=release_type,
        query=q,
        limit=1000,
    )
    return _fragment(
        request,
        "partials/album_rows.html",
        _album_rows_context(
            albums, total, album.artist_id, q=q, status=album_status,
            release_type=release_type,
        ),
        toast=(
            (
                f"Queued an upgrade of {album.display_title}."
                if upgrading
                else f"Queued {album.display_title}."
            )
            if created
            else f"{album.display_title} is already queued."
        ),
        level="success" if created else "info",
    )


@router.post("/ui/artists/{artist_id}/download-wanted", response_class=Response)
async def ui_download_wanted(
    request: Request, session: SessionDep, artist_id: str
) -> Response:
    """Queue every wanted release of one artist and refresh its album table."""
    artist = await get_artist_or_404(session, artist_id)
    message = await queue_wanted_for_artist(session, artist)
    albums, total = await list_albums_for_artist(session, artist.id, limit=1000)
    return _fragment(
        request,
        "partials/album_rows.html",
        _album_rows_context(albums, total, artist.id),
        toast=message,
        level="success" if "Queued" in message else "info",
    )


@router.post("/ui/wanted/download-all", response_class=Response)
async def ui_download_all_wanted(
    request: Request,
    session: SessionDep,
    q: OptStrQuery = None,
    album_status: StatusQuery = None,
    monitored: OptBoolQuery = None,
) -> Response:
    """Queue the whole backlog from the Wanted page and re-render its table."""
    message = await queue_all_wanted(session)
    items, total = await list_wanted_albums(
        session, query=q, status=album_status, monitored=monitored, limit=500
    )
    return _fragment(
        request,
        "partials/wanted_rows.html",
        _wanted_context(items, total, q=q, status=album_status, monitored=monitored),
        toast=message,
        level="success" if "Queued" in message else "info",
    )


@router.post("/ui/wanted/{album_id}/queue", response_class=Response)
async def ui_queue_wanted_album(
    request: Request,
    session: SessionDep,
    album_id: str,
    q: OptStrQuery = None,
    album_status: StatusQuery = None,
    monitored: OptBoolQuery = None,
) -> Response:
    """Queue one backlog row and re-render the Wanted table."""
    album = await get_album_or_404(session, album_id)
    _item, created = await queue_album(session, album)
    items, total = await list_wanted_albums(
        session, query=q, status=album_status, monitored=monitored, limit=500
    )
    return _fragment(
        request,
        "partials/wanted_rows.html",
        _wanted_context(items, total, q=q, status=album_status, monitored=monitored),
        toast=(
            f"Queued {album.display_title}."
            if created
            else f"{album.display_title} is already queued."
        ),
        level="success" if created else "info",
    )


@router.post("/ui/albums/{album_id}/monitor", response_class=Response)
async def ui_monitor_album(
    request: Request,
    session: SessionDep,
    album_id: str,
    view: Literal["artist", "wanted", "queue", "dashboard"] = Query("artist"),
    q: OptStrQuery = None,
    album_status: StatusQuery = None,
    release_type: OptStrQuery = None,
    monitored: OptBoolQuery = None,
    state: OptQueueStateQuery = None,
) -> Response:
    """Toggle one release's monitored flag and re-render the caller's table.

    Every screen that lists albums shows this button, so ``view`` says which
    table to send back and the remaining parameters are that screen's active
    filters (see :func:`_scope`).

    ``monitored`` here is the **Wanted page's filter**, never the album's new
    value: this endpoint always toggles, and nothing in it may feed a submitted
    value into ``AlbumUpdateIn``. Reading it as a value would let the Wanted
    page's "ignored only" filter silently unmonitor whatever row was pressed —
    the album-level twin of the ``set_monitored`` trap in the bulk artist
    editor. Use ``POST /api/albums/{id}/monitor`` with a body when an explicit
    value is wanted.
    """
    album = await get_album_or_404(session, album_id)
    album = await set_album_monitored(session, album, AlbumUpdateIn())
    toast = _monitor_toast(album)

    if view == "wanted":
        items, total = await list_wanted_albums(
            session, query=q, status=album_status, monitored=monitored, limit=500
        )
        return _fragment(
            request,
            "partials/wanted_rows.html",
            _wanted_context(
                items, total, q=q, status=album_status, monitored=monitored
            ),
            toast=toast,
        )

    if view == "queue":
        items, total = await list_queue_items(session, state=state, limit=300)
        return _fragment(
            request,
            "partials/queue_table.html",
            await _queue_context(session, items, total, state),
            toast=toast,
        )

    if view == "dashboard":
        items, total = await list_wanted_albums(session, limit=DASHBOARD_WANTED)
        return _fragment(
            request,
            "partials/dashboard_wanted.html",
            {"wanted_albums": items, "wanted_total": total},
            toast=toast,
        )

    albums, total = await list_albums_for_artist(
        session,
        album.artist_id,
        status=album_status,
        release_type=release_type,
        query=q,
        limit=1000,
    )
    return _fragment(
        request,
        "partials/album_rows.html",
        _album_rows_context(
            albums, total, album.artist_id, q=q, status=album_status,
            release_type=release_type,
        ),
        toast=toast,
    )


@router.post("/ui/queue/{item_id}/retry", response_class=Response)
async def ui_retry_queue_item(
    request: Request,
    session: SessionDep,
    item_id: int,
    state: OptQueueStateQuery = None,
) -> Response:
    """Retry a queue entry and re-render the queue table."""
    item = await get_queue_item_or_404(session, item_id)
    message = await retry_queue_item(session, item)
    items, total = await list_queue_items(session, state=state, limit=300)
    return _fragment(
        request,
        "partials/queue_table.html",
        await _queue_context(session, items, total, state),
        toast=message,
    )


@router.post("/ui/queue/{item_id}/cancel", response_class=Response)
async def ui_cancel_queue_item(
    request: Request,
    session: SessionDep,
    item_id: int,
    state: OptQueueStateQuery = None,
) -> Response:
    """Cancel a queue entry and re-render the queue table."""
    item = await get_queue_item_or_404(session, item_id)
    message = await cancel_queue_item(session, item)
    items, total = await list_queue_items(session, state=state, limit=300)
    return _fragment(
        request,
        "partials/queue_table.html",
        await _queue_context(session, items, total, state),
        toast=message,
        level="warning",
    )


@router.post("/ui/scan-all", response_class=Response)
async def ui_scan_all(request: Request, session: SessionDep) -> Response:
    """Request a full sweep from the dashboard."""
    message = await scan_all(session)
    return _fragment(
        request,
        "partials/inline_message.html",
        {"message": message, "level": "info"},
        toast=message,
    )


@router.post("/ui/library/scan", response_class=Response)
async def ui_library_scan(
    request: Request,
    session: SessionDep,
    settings: SettingsDep,
    artist_id: OptStrQuery = None,
    dry_run: bool = Query(False),
) -> Response:
    """Run a disk scan and swap the report back in.

    Inline rather than detached: the scan touches no network and finishes in
    about a second per thousand files, so there is nothing to background.

    ``dry_run`` is a *query* parameter, not a form field, because the Preview
    button is a bare ``hx-post`` with no form around it — a ``Form()`` binding
    would silently read ``None`` and apply the scan for real.
    """
    result = await scan_library(session, artist_id=artist_id, apply=not dry_run)
    scan = await library_scan_status(session, settings)
    # A dry run is not stored, so show the fresh report rather than the last
    # applied one — otherwise the page would appear to ignore the button.
    scan["last"] = result
    return _fragment(
        request,
        "partials/library_scan_report.html",
        {"scan": scan, "import_status": await library_import_status(session)},
        toast=result.get("summary", "Scan finished."),
        level="warning" if result.get("errors") else "info",
    )


@router.post("/ui/library/refile", response_class=Response)
async def ui_library_refile(
    request: Request,
    session: SessionDep,
    settings: SettingsDep,
    artist_id: OptStrQuery = None,
    apply: bool = Query(False),
) -> Response:
    """Preview or perform a library-wide re-file and swap the report back in.

    ``apply`` is a *query* parameter for the same reason ``dry_run`` is on the
    disk scan: the Preview and Apply buttons are bare ``hx-post``\\ s with no
    form around them, and a ``Form()`` binding would read ``None`` — which here
    would mean moving folders when the user asked to look first.
    """
    result = await refile_library(session, artist_id=artist_id, apply=apply)
    return _fragment(
        request,
        "partials/refile_report.html",
        await _tidy_context(session, settings, artist_id=artist_id, refile=result),
        toast=result.summary,
        level="warning" if result.failed or result.blocked else "success",
    )


@router.post("/ui/library/retag", response_class=Response)
async def ui_library_retag(
    request: Request,
    session: SessionDep,
    settings: SettingsDep,
    artist_id: OptStrQuery = None,
) -> Response:
    """Re-tag every downloaded release (or one artist's) and report."""
    result = await retag_library(session, artist_id=artist_id)
    return _fragment(
        request,
        "partials/retag_report.html",
        await _tidy_context(session, settings, artist_id=artist_id, retag=result),
        toast=result.summary,
        level="warning" if result.failed or result.blocked else "success",
    )


@router.post("/ui/library/trash/{entry_id}/restore", response_class=Response)
async def ui_restore_trash(
    request: Request,
    session: SessionDep,
    settings: SettingsDep,
    entry_id: str,
    artist_id: OptStrQuery = None,
) -> Response:
    """Put one trashed batch back and refresh the trash list."""
    restored = await restore_trash_entry(entry_id, settings)
    return _fragment(
        request,
        "partials/trash_list.html",
        await _tidy_context(session, settings, artist_id=artist_id),
        toast=f"Restored to {restored}. Run a disk scan to adopt it again.",
        level="success",
    )


@router.post("/ui/library/trash/empty", response_class=Response)
async def ui_empty_trash(
    request: Request,
    session: SessionDep,
    settings: SettingsDep,
    entry_id: OptStrQuery = None,
    artist_id: OptStrQuery = None,
) -> Response:
    """Destroy trashed files for real.

    The only irreversible action in the application. ``entry_id`` empties one
    batch; omitting it empties all of them.
    """
    removed, freed = await empty_trash(entry_id, settings)
    return _fragment(
        request,
        "partials/trash_list.html",
        await _tidy_context(session, settings, artist_id=artist_id),
        toast=(
            f"Permanently deleted {removed} item(s), freeing {freed / 1048576:.1f} MiB."
        ),
        level="warning",
    )


@router.post("/ui/library/import", response_class=Response)
async def ui_library_import(
    request: Request,
    session: SessionDep,
    index_now: Annotated[str | None, Form()] = None,
    monitored: Annotated[str | None, Form()] = None,
    limit: Annotated[str | None, Form()] = None,
) -> Response:
    """Start the bulk artist import and swap in the progress panel.

    The panel polls itself from then on, so this returns as soon as the
    background run is under way rather than holding the request open for the
    minutes a real import takes.
    """
    payload = LibraryImportStartIn(
        monitored=_parse_bool(monitored) if monitored is not None else True,
        index_now=_parse_bool(index_now),
        limit=int(limit) if limit and limit.strip().isdigit() else None,
    )
    try:
        snapshot = await start_library_import(payload)
    except HTTPException as exc:
        return _fragment(
            request,
            "partials/library_import_progress.html",
            {"import_status": await library_import_status(session)},
            toast=str(exc.detail),
            level="warning",
        )

    return _fragment(
        request,
        "partials/library_import_progress.html",
        {"import_status": snapshot},
        toast=(
            f"Looking up {snapshot['total']} artist(s) on Qobuz — "
            f"about {int(snapshot['eta_seconds'] // 60)} minute(s)."
        ),
        refresh="qobuzarr:import",
    )


@router.post("/ui/library/import/cancel", response_class=Response)
async def ui_cancel_library_import(request: Request, session: SessionDep) -> Response:
    """Stop a running import after the artist it is currently searching for."""
    stopped = await cancel_library_import()
    return _fragment(
        request,
        "partials/library_import_progress.html",
        {"import_status": await library_import_status(session)},
        toast=(
            "Stopping after the current artist."
            if stopped
            else "No import is running."
        ),
        level="info" if stopped else "warning",
    )


@router.post("/ui/library/import/follow/{artist_id}", response_class=Response)
async def ui_follow_reviewed_artist(
    request: Request,
    session: SessionDep,
    settings: SettingsDep,
    artist_id: str,
    name: Annotated[str | None, Form()] = None,
) -> Response:
    """Follow one artist chosen by hand from the import review list.

    Reuses :func:`app.api.routes_api.follow_artist`, so a manual pick behaves
    exactly like following from the search page — including honouring
    ``AUTO_INDEX_ON_FOLLOW`` for this single artist, which is affordable in a
    way that doing it for five hundred is not.
    """
    artist, created = await follow_artist(
        session, ArtistCreateIn(artist_id=artist_id, name=name or None), settings
    )
    return _fragment(
        request,
        "partials/follow_button.html",
        {"artist_id": artist.id, "followed": True, "name": artist.name},
        toast=(
            f"Now following {artist.name}."
            if created
            else f"{artist.name} was already followed."
        ),
    )
