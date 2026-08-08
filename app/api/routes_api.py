"""JSON API under ``/api/*`` plus the ``/health`` endpoint.

Every action the web UI can perform is available here as JSON so the whole
application is scriptable::

    curl localhost:8000/api/status
    curl -X POST localhost:8000/api/artists -d '{"artist_id":"12345"}' \\
         -H 'content-type: application/json'

The module also exposes the *service functions* (``follow_artist``,
``queue_album``, ``scan_artist``, ...) that :mod:`app.api.routes_ui` calls, so
the HTML forms and the JSON endpoints share one implementation and can never
drift apart.

Two routers are exported:

``router``
    Everything under the ``/api`` prefix.
``health_router``
    The unprefixed ``/health`` probe.

Both must be mounted by the application factory.
"""

from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Callable, Coroutine
from typing import Annotated, Any, Literal, Sequence

from fastapi import APIRouter, Body, Depends, HTTPException, Query, status as http_status
from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app import __version__
from app.api.deps import (
    album_consensus,
    album_detail,
    album_out,
    artist_stats,
    get_enricher,
    albums_by_status,
    artist_album_counts,
    artist_out,
    build_banners,
    build_enrichment_sources,
    build_meta,
    build_settings_out,
    build_status,
    call_first,
    integrity_report_out,
    integrity_status,
    list_corrupt_tracks,
    get_indexer,
    get_library_importer,
    get_library_scanner,
    get_qobuz_client,
    get_queue_worker,
    get_settings_dep,
    indexer_status,
    library_import_status,
    library_quality,
    library_scan_status,
    library_stats,
    list_activity,
    list_albums_for_artist,
    list_artists,
    list_queue_items,
    list_recent_releases,
    list_wanted_albums,
    nav_counts_out,
    queue_item_to_out,
    queue_stats,
    rate_limit_status,
    release_group_out,
)
from app.api.params import (
    EmptyAsNone,
    OptActivityLevelQuery,
    OptBoolQuery,
    OptQueueStateQuery,
    OptStrQuery,
)
from app import config
from app.config import Settings, get_effective_settings, get_settings
from app.core import librarian, settings_store
from app.core.state import state_or_none
from app.db import get_session, healthcheck, session_scope
from app.logging_conf import get_logger
from app.models import (
    Activity,
    EnrichmentEntity,
    EnrichmentSource,
    ActivityLevel,
    Album,
    AlbumStatus,
    Artist,
    MonitorMode,
    QueueItem,
    QueueState,
    RELEASE_TYPES,
    join_release_types,
    parse_release_types,
    normalize_release_type,
    utcnow,
)
from app.enrich.errors import ENRICHMENT_STATES, EnrichmentOutcome
from app.net.errors import HttpError
from app.qobuz.errors import QobuzError
from app.qobuz.mapper import (
    album_credit_names,
    extract_album_artist,
    map_album,
    map_artist,
)
from app.core.enricher import REVIEW_STATES, library_scope, list_review_items
from app.core.indexer import BacklogChange, apply_monitoring_to_backlog
from app.schemas import (
    ActivityListOut,
    AlbumDetailOut,
    AlbumListOut,
    AlbumOut,
    AlbumUpdateIn,
    ArtistBulkUpdateIn,
    ArtistCreateIn,
    ArtistDetailOut,
    ArtistFollowOut,
    ArtistListOut,
    ArtistOut,
    ArtistStatsOut,
    ArtistCreditFilterIn,
    ArtistCreditsOut,
    ArtistTagsIn,
    ArtistUpdateIn,
    CreditClusterOut,
    BannerOut,
    ConsensusFieldOut,
    CorruptListOut,
    DatabaseHealthOut,
    EnrichmentAcceptOut,
    EnrichmentCandidateOut,
    EnrichmentCandidatesOut,
    EnrichmentIdentifyIn,
    EnrichmentRejectIn,
    EnrichmentReviewListOut,
    EnrichmentReviewOut,
    EnrichmentStatusOut,
    HealthOut,
    HealthSummaryOut,
    IntegrityReportOut,
    IntegrityRunOut,
    IntegrityStatusOut,
    LibraryImportOut,
    LibraryImportPreviewOut,
    LibraryImportStartIn,
    LibraryScanOut,
    LibraryScanStatusOut,
    LibraryTidyOut,
    MessageOut,
    MetaOut,
    NavCountsOut,
    QueueItemOut,
    QueueListOut,
    RefileEstimateOut,
    RefilePlanOut,
    ReleaseGroupOut,
    SearchAlbumOut,
    SearchArtistOut,
    SearchResultOut,
    SettingsOut,
    SettingsUpdateIn,
    StatsOut,
    StatusOut,
    TrashEntryOut,
    TrashOut,
    TrashSummaryOut,
    WantedListOut,
)

__all__ = [
    "router",
    "health_router",
    # Re-exported from :mod:`app.schemas`, where they are now declared. Kept in
    # this namespace because the routes are the reason they exist and every
    # existing importer reaches for them here.
    "ArtistListOut",
    "AlbumListOut",
    "WantedListOut",
    "QueueListOut",
    "EnrichmentReviewListOut",
    "ActivityListOut",
    "ArtistDetailOut",
    "health_summary",
    "follow_artist",
    "apply_artist_update",
    "bulk_update_artists",
    "delete_artist",
    "scan_artist",
    "scan_all",
    "scan_library",
    "delete_album_files",
    "refile_album",
    "refile_library",
    "estimate_refile",
    "retag_album",
    "retag_library",
    "trash_contents",
    "restore_trash_entry",
    "empty_trash",
    "start_library_import",
    "cancel_library_import",
    "preview_library_import",
    "run_integrity_verify",
    "quarantine_corrupt",
    "queue_album",
    "queue_wanted_for_artist",
    "queue_all_wanted",
    "apply_album_update",
    "set_album_monitored",
    "retry_queue_item",
    "cancel_queue_item",
    "run_search",
    "get_artist_or_404",
    "get_album_or_404",
    "get_queue_item_or_404",
]

logger = get_logger(__name__)

router = APIRouter(prefix="/api", tags=["api"])
health_router = APIRouter(tags=["health"])

SessionDep = Annotated[AsyncSession, Depends(get_session)]
SettingsDep = Annotated[Settings, Depends(get_settings_dep)]


# The list envelopes (``ArtistListOut`` and friends) used to be declared here,
# beside the routes that return them. They now live in :mod:`app.schemas` with
# every other response model — a type generator reading only that module saw no
# envelope at all and produced a client whose list shapes were hand-written — and
# are re-exported above so nothing that imported them from here has to change.


# ---------------------------------------------------------------------------
# Lookup helpers
# ---------------------------------------------------------------------------
async def get_artist_or_404(session: AsyncSession, artist_id: str) -> Artist:
    """Fetch an artist by id or raise ``404``."""
    artist = await session.get(Artist, str(artist_id))
    if artist is None:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND,
            detail=f"Artist {artist_id!r} is not followed.",
        )
    return artist


async def get_album_or_404(session: AsyncSession, album_id: str) -> Album:
    """Fetch an album by its (string!) id or raise ``404``."""
    album = await session.get(Album, str(album_id))
    if album is None:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND,
            detail=f"Album {album_id!r} is not in the library.",
        )
    return album


async def get_queue_item_or_404(session: AsyncSession, item_id: int) -> QueueItem:
    """Fetch a queue entry by id or raise ``404``."""
    item = await session.get(QueueItem, int(item_id))
    if item is None:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND,
            detail=f"Queue item {item_id} does not exist.",
        )
    return item


async def _unfiltered_total(
    *,
    filtered: bool,
    total: int,
    count: Callable[[], Awaitable[tuple[Any, int]]],
) -> int:
    """How many rows this list would hold with its *filters* dropped.

    The number that separates "nothing matches what you typed" from "there is
    nothing here yet". A list that reports only its filtered total cannot tell
    those apart, so its empty state has to be written to cover both and ends up
    saying neither — and the second one is the one with an action attached.

    *count* re-runs the endpoint's own loader with the narrowing arguments
    omitted, which keeps the definition of "not a filter" in one place: whatever
    the caller still passes (the artist an album list is scoped to, the missing
    statuses the backlog is *about*, enrichment's ``library_scope``) is the
    subject of the query rather than a narrowing of it, and stays applied.

    Skipped entirely when no filter is active, because then the answer is
    ``total`` and the extra ``COUNT`` would be a second query per request for a
    number already in hand.
    """
    if not filtered:
        return total
    _rows, unfiltered = await count()
    return unfiltered


class ErrorDetail(dict[str, Any]):
    """An ``HTTPException`` detail that is a payload **and** a sentence.

    FastAPI happily serialises a dict as ``detail``, and the application's error
    envelope needs both halves of one: the prose a person reads, and the fields a
    client needs to re-draw the form that failed. The awkward part is that
    everything already written against ``exc.detail`` renders it with ``str()``,
    and ``str({...})`` is a Python repr with braces and quotes in it.

    A dict subclass with a ``__str__`` gives both for free: it *is* a dict, so it
    serialises as one and the envelope can lift the fields out of it, and it
    stringifies to the sentence, so every existing caller keeps printing what it
    always printed. The alternative — a parallel ``detail=`` argument threaded
    through every raiser — is the same information in two places.
    """

    def __str__(self) -> str:
        return str(self.get("error") or "")


def require_client() -> Any:
    """Return the shared Qobuz client or raise a friendly ``503``.

    The message names ``QOBUZ_APP_ID`` and ``QOBUZ_USER_AUTH_TOKEN`` on purpose
    and must keep doing so: this is what "degrades gracefully without
    credentials" actually looks like from the outside, and a search box that says
    only "unavailable" turns a two-line fix into a support question.

    ``detail.code`` is the machine-readable half, for the same reason
    :class:`app.schemas.BannerOut` has one. Every surface that calls this renders
    the failure *inside the panel that asked* rather than as a global error page
    — a catalogue search failing is not the application being broken — and the
    panel needs to tell "nothing is configured" (link to Settings, no retry
    button) from "Qobuz did not answer" (retry, do not send anyone to Settings).
    Matching on the sentence to do that makes the sentence an interface nobody
    can edit.
    """
    client = get_qobuz_client()
    if client is None:
        raise HTTPException(
            status_code=http_status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=ErrorDetail(
                error=(
                    "The Qobuz client is not available. Check QOBUZ_APP_ID / "
                    "QOBUZ_USER_AUTH_TOKEN in .env and restart Qobuzarr."
                ),
                code="no_client",
                missing=["QOBUZ_APP_ID", "QOBUZ_USER_AUTH_TOKEN"],
            ),
        )
    return client


def _log_activity(
    session: AsyncSession,
    *,
    event: str,
    message: str,
    level: ActivityLevel = ActivityLevel.INFO,
    artist_id: str | None = None,
    album_id: str | None = None,
) -> None:
    """Append an activity row (not committed — the caller commits)."""
    session.add(
        Activity(
            level=level,
            event=event,
            message=message,
            artist_id=artist_id,
            album_id=album_id,
        )
    )


#: Strong references to detached background jobs, so they are not garbage
#: collected mid-flight (asyncio only keeps weak references to tasks).
_BACKGROUND_TASKS: set[asyncio.Task[Any]] = set()


def _spawn(coro: Coroutine[Any, Any, Any], *, label: str) -> bool:
    """Run *coro* detached from the request. Returns ``False`` if it could not.

    Indexing one artist means many rate-limited Qobuz calls and can take
    minutes, so "search now" must not block the HTTP response.
    """
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:  # pragma: no cover - no loop outside the server
        coro.close()
        return False

    task = loop.create_task(coro, name=label)
    _BACKGROUND_TASKS.add(task)

    def _done(finished: asyncio.Task[Any]) -> None:
        _BACKGROUND_TASKS.discard(finished)
        if finished.cancelled():
            logger.info("Background job %s cancelled", label)
            return
        error = finished.exception()
        if error is not None:
            logger.error("Background job %s failed: %s", label, error, exc_info=error)

    task.add_done_callback(_done)
    return True


async def _scan_artist_job(artist_id: str, artist_name: str) -> None:
    """Index one artist in the background, recording failures as activity."""
    indexer = get_indexer()
    if indexer is None:
        return
    try:
        async with session_scope() as session:
            await indexer.check_artist(session, artist_id)
    except Exception as exc:  # noqa: BLE001 - surfaced in the activity feed
        logger.exception("Background scan of %s failed", artist_name)
        try:
            async with session_scope() as session:
                _log_activity(
                    session,
                    event="indexer.error",
                    message=f"Scan of {artist_name} failed: {exc}",
                    level=ActivityLevel.ERROR,
                    artist_id=artist_id,
                )
        except Exception:  # noqa: BLE001 - logging must not mask the original
            logger.exception("Could not record the scan failure for %s", artist_name)


def _clean_release_types(values: list[str] | None) -> str | None:
    """Normalise and re-join a list of release types, dropping unknown values.

    Qobuz spellings are accepted (``"epSingle"`` becomes ``"ep"``), an explicit
    ``"other"`` is kept, and anything unrecognised is silently dropped rather
    than being collapsed into ``"other"``.
    """
    if values is None:
        return None
    cleaned: list[str] = []
    for raw in values:
        candidate = str(raw).strip().lower()
        if candidate in RELEASE_TYPES:
            cleaned.append(candidate)
            continue
        mapped = normalize_release_type(candidate)
        if mapped != "other":
            cleaned.append(mapped)
    # Preserve order while removing duplicates.
    return join_release_types(list(dict.fromkeys(cleaned))) or None


# ---------------------------------------------------------------------------
# Service functions — shared by the JSON API and the HTML routes
# ---------------------------------------------------------------------------
async def follow_artist(
    session: AsyncSession, payload: ArtistCreateIn, settings: Settings
) -> tuple[Artist, bool]:
    """Create (or update) a followed artist.

    Looks the artist up on Qobuz to fill in name/image/album count when a client
    is available; falls back to the supplied ``name`` otherwise.

    Prefers :meth:`app.core.indexer.Indexer.add_artist` (which knows how to
    fetch and normalise the artist) and falls back to a database-only insert so
    that following still works with no indexer or no network.

    Returns:
        ``(artist, created)``.

    Raises:
        HTTPException: 400 when the artist cannot be identified at all.
    """
    artist_id = str(payload.artist_id).strip()
    if not artist_id:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail="artist_id is required.",
        )

    created = (await session.get(Artist, artist_id)) is None
    # Through the same filter PATCH and bulk-update use. ``Indexer.add_artist``
    # writes what it is given straight to the column, and a value outside
    # RELEASE_TYPES matches no release at all — so an unvalidated "lives" would
    # silently follow an artist whose every release is then ignored.
    cleaned = _clean_release_types(payload.accepted_release_types)
    accepted = parse_release_types(cleaned) if cleaned else None
    if payload.accepted_release_types and not accepted:
        logger.warning(
            "Ignoring unrecognised release types %s for artist %s; using the "
            "defaults instead of following nothing",
            payload.accepted_release_types,
            artist_id,
        )

    indexer = get_indexer()
    adder = getattr(indexer, "add_artist", None) if indexer is not None else None
    if callable(adder):
        try:
            artist = await adder(
                session,
                artist_id,
                payload.monitored,
                name=payload.name,
                monitor_mode=payload.monitor_mode,
                quality_profile=payload.quality_profile,
                accepted_release_types=accepted,
                # The initial catalogue import is many rate-limited calls; run
                # it in the background so the request returns immediately.
                index_now=False,
            )
            # Indexing is read-only and rate-limited, but it is still many API
            # calls, so it honours `auto_index_on_follow`. Downloading is a
            # separate opt-in governed by `auto_download` in the indexer.
            if payload.search_now or (created and settings.auto_index_on_follow):
                _spawn(
                    _scan_artist_job(artist.id, artist.name),
                    label=f"scan:{artist.id}",
                )
            return artist, created
        except QobuzError as exc:
            logger.warning("Indexer.add_artist(%s) failed: %s", artist_id, exc)
        except Exception as exc:  # noqa: BLE001 - fall back to the DB-only path
            logger.warning("Indexer.add_artist(%s) errored: %s", artist_id, exc)
            await session.rollback()

    details: dict[str, Any] = {}
    client = get_qobuz_client()
    if client is not None:
        try:
            raw = await client.get_artist(artist_id, with_albums=False)
            details = map_artist(raw)
        except QobuzError as exc:
            logger.warning("Qobuz lookup for artist %s failed: %s", artist_id, exc)
        except Exception as exc:  # noqa: BLE001 - never block following on lookup
            logger.warning("Unexpected error looking up artist %s: %s", artist_id, exc)

    name = (payload.name or details.get("name") or "").strip()
    if not name:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=(
                "Could not determine the artist name. Pass `name` explicitly or "
                "make sure the Qobuz client is configured."
            ),
        )

    artist = await session.get(Artist, artist_id)
    created = artist is None
    if artist is None:
        artist = Artist(id=artist_id)
        session.add(artist)

    artist.name = name
    if details.get("image_url"):
        artist.image_url = details["image_url"]
    if details.get("albums_count"):
        artist.albums_count = int(details["albums_count"])
    if details.get("qobuz_slug"):
        artist.qobuz_slug = details["qobuz_slug"]

    artist.monitored = payload.monitored
    if created:
        artist.monitor_mode = payload.monitor_mode or MonitorMode(
            settings.default_monitor_mode
        )
        artist.quality_profile = payload.quality_profile or settings.default_quality_profile
        artist.accepted_release_types = (
            _clean_release_types(payload.accepted_release_types)
            or join_release_types(settings.accepted_release_type_defaults)
        )
    else:
        if payload.monitor_mode is not None:
            artist.monitor_mode = payload.monitor_mode
        if payload.quality_profile:
            artist.quality_profile = payload.quality_profile
        cleaned = _clean_release_types(payload.accepted_release_types)
        if cleaned:
            artist.accepted_release_types = cleaned

    _log_activity(
        session,
        event="artist.follow" if created else "artist.update",
        message=f"{'Now following' if created else 'Updated'} {name}",
        artist_id=artist_id,
    )
    await session.commit()
    await session.refresh(artist)

    if payload.search_now:
        await scan_artist(session, artist)
    return artist, created


async def apply_artist_update(
    session: AsyncSession, artist: Artist, payload: ArtistUpdateIn
) -> Artist:
    """Apply a partial monitoring-settings update to *artist* and commit.

    Changing any setting that :func:`~app.core.indexer.desired_status` reads
    also moves the releases already on file, through
    :func:`~app.core.indexer.apply_monitoring_to_backlog` — the same call the
    bulk edit makes, so one artist and fifty behave alike. Without that the
    releases already marked ``wanted`` sit in the backlog for good: the indexer
    re-derives the status of *newly discovered* albums only, so nothing is
    coming to clear them.

    The trigger is every field :func:`desired_status` reads and not the whole
    payload. ``quality_profile`` is deliberately absent — it decides how good a
    copy to fetch, never whether to want one, so changing it must not move a
    single row.
    """
    before = (
        artist.monitored,
        artist.monitor_mode,
        artist.accepted_release_types,
        artist.include_guest_appearances,
    )
    if payload.monitored is not None:
        artist.monitored = payload.monitored
    if payload.monitor_mode is not None:
        artist.monitor_mode = payload.monitor_mode
    if payload.quality_profile:
        artist.quality_profile = payload.quality_profile
    if payload.include_guest_appearances is not None:
        artist.include_guest_appearances = payload.include_guest_appearances
    if payload.accepted_release_types is not None:
        artist.accepted_release_types = _clean_release_types(
            payload.accepted_release_types
        ) or ""

    change = BacklogChange()
    after = (
        artist.monitored,
        artist.monitor_mode,
        artist.accepted_release_types,
        artist.include_guest_appearances,
    )
    if before != after:
        change = await apply_monitoring_to_backlog(session, [artist])

    message = f"Updated settings for {artist.name}"
    if change.demoted:
        message += f" — {change.demoted} release(s) no longer wanted"
    elif change.restored:
        message += f" — {change.restored} release(s) wanted again"
    _log_activity(
        session,
        event="artist.update",
        message=message,
        artist_id=artist.id,
    )
    await session.commit()
    await session.refresh(artist)
    return artist


def build_artist_credits(artist: Artist, albums: Sequence[Album]) -> ArtistCreditsOut:
    """Cluster *albums* by the credits found on them. Pure; no I/O.

    ``analysed`` counts releases whose ``credit_names`` is not ``NULL`` —
    *somebody looked* — which is deliberately not the same as the number that
    produced a credit. A release where every credit was the bare artist name is
    analysed and unattributed, and it is counted in both figures precisely so
    the screen can say how much of the catalogue no credit can speak for.
    """
    accepted = {name.casefold() for name in artist.credit_filter_list}
    counts: dict[str, int] = {}
    samples: dict[str, list[str]] = {}
    analysed = unattributed = 0
    for album in sorted(
        albums, key=lambda a: (a.release_date is None, a.release_date), reverse=True
    ):
        if album.credit_names is None:
            continue
        analysed += 1
        names = album.credit_names_list
        if not names:
            unattributed += 1
        for name in names:
            counts[name] = counts.get(name, 0) + 1
            if len(samples.setdefault(name, [])) < 3:
                samples[name].append(album.title)
    clusters = [
        CreditClusterOut(
            name=name,
            releases=count,
            accepted=name.casefold() in accepted,
            sample_titles=samples.get(name, []),
        )
        for name, count in sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    ]
    return ArtistCreditsOut(
        artist_id=str(artist.id),
        artist_name=artist.name,
        analysed=analysed,
        total=len(albums),
        unattributed=unattributed,
        filter_active=bool(accepted),
        clusters=clusters,
    )


async def analyse_artist_credits(
    session: AsyncSession, artist: Artist, *, limit: int = 25
) -> ArtistCreditsOut:
    """Read the track credits of up to *limit* not-yet-analysed releases.

    Bounded per press, and the bound is pacing rather than caution: one
    ``album/get`` per release at ``QOBUZ_MIN_REQUEST_INTERVAL`` makes a whole
    catalogue a multi-minute request. The caller repeats while
    ``analysed < total``, which is also what lets a person watch it move.

    **Every request is made with no write pending.** The payloads are collected
    first and the rows are touched only afterwards, so the transaction SQLite
    lets exactly one writer hold is opened after the last call rather than
    across all of them — the same reason ``Enricher`` splits its tick into
    claim / fetch / persist, and the failure it avoids is a stalled download
    worker being marked failed.

    A release Qobuz will not answer for is left ``NULL`` — *not analysed* —
    rather than recorded as having no credits. The next press retries it.
    """
    client = require_client()
    albums = list(
        (
            await session.execute(select(Album).where(Album.artist_id == str(artist.id)))
        )
        .unique()
        .scalars()
        .all()
    )
    todo = [album for album in albums if album.credit_names is None][: max(0, limit)]

    fetched: dict[str, list[str]] = {}
    for album in todo:
        try:
            raw = await client.get_album(str(album.id))
        except QobuzError as exc:
            logger.warning("Credit lookup failed for album %s: %s", album.id, exc)
            continue
        fetched[str(album.id)] = album_credit_names(raw, artist.name)

    if fetched:
        for album in albums:
            names = fetched.get(str(album.id))
            if names is not None:
                album.set_credit_names(names)
        await session.commit()

    return build_artist_credits(artist, albums)


async def set_artist_credit_filter(
    session: AsyncSession, artist: Artist, payload: ArtistCreditFilterIn
) -> BacklogChange:
    """Accept only these credits as *artist*, and move the backlog to match.

    Commits, like ``update_artist_tags`` and ``identify()`` do: the
    request-scoped session from ``get_session`` never commits on its own, so
    the caller-commits convention would make this a green toast and a rollback.

    The counts come back because the largest thing this does is otherwise
    invisible — narrowing a conflated artist to one of the people sharing their
    id moved 37 releases out of one real backlog, and nothing would have said
    so. An empty list clears the filter and the same call restores what it can.
    """
    before = artist.credit_filter_json
    artist.set_credit_filter(payload.credits)
    change = BacklogChange()
    if artist.credit_filter_json != before:
        change = await apply_monitoring_to_backlog(session, [artist])

    accepted = artist.credit_filter_list
    message = (
        f"{artist.name}: accepting {len(accepted)} credit(s)"
        if accepted
        else f"{artist.name}: credit filter cleared"
    )
    if change.demoted:
        message += f" — {change.demoted} release(s) no longer wanted"
    if change.restored:
        message += f" — {change.restored} release(s) wanted again"
    _log_activity(
        session, event="artist.credit_filter", message=message, artist_id=artist.id
    )
    await session.commit()
    return change


async def update_artist_tags(
    session: AsyncSession, artist: Artist, payload: ArtistTagsIn
) -> Artist:
    """Record a hand-edited name, sort name and MusicBrainz id for *artist*.

    Writing only — nothing on disk changes here. ``POST
    /api/artists/{id}/retag`` is the half that touches files, and the two are
    separate because a typo in a name should be correctable without committing
    to a thousand file writes in the same press.

    Three rules, each a refusal somewhere:

    * **Validation runs before any write.** A blank name, a blank id and an
      absent enricher are all decided up front, so a rejected edit leaves the
      database exactly as it was rather than half-applied.
    * **``mb_artist_mbid`` goes through** :meth:`Enricher.identify`, not through
      a second write of its own. That is what marks it ``manual``, which is what
      ``_MANUAL_OWNS`` reads to stop the next automatic derivation replacing it
      — and an id that is silently re-derived next tick is worse than no field,
      because it is written into every file as ``MUSICBRAINZ_ARTISTID`` and
      Picard, beets and Roon then believe it. It also **commits**, deliberately:
      both HTTP entry points run on the request-scoped session, which never
      commits, so the whole edit rides out on that commit and a rejected id
      rolls the name back with it.
    * **A name may still never select a candidate.** Nothing here searches
      anything. The id is typed by a person, exactly as on the review screen.

    Raises:
        HTTPException: 400 for a value this cannot store, 404 for an artist that
            has gone, 503 when the id needs an enricher and there is not one.
    """
    name: str | None = None
    if payload.name is not None:
        name = payload.name.strip()
        if not name:
            raise HTTPException(
                status_code=http_status.HTTP_400_BAD_REQUEST,
                detail=(
                    "An artist name cannot be blank — it is what names the "
                    "folder every one of their releases sits in."
                ),
            )

    aliases: list[str] | None = None
    if payload.aliases is not None:
        # Cleaned before anything is written, like the other two: an entry that
        # cannot be stored has to be a refusal rather than a silent drop, which
        # is the whole reason this field waited for a column.
        aliases = []
        for entry in payload.aliases:
            text = entry.strip()
            if not text:
                continue
            if len(text) > 512:
                raise HTTPException(
                    status_code=http_status.HTTP_400_BAD_REQUEST,
                    detail=(
                        "An alias is a name, not a paragraph — keep each one "
                        "under 512 characters."
                    ),
                )
            if text not in aliases:
                aliases.append(text)

    mbid: str | None = None
    if payload.mb_artist_mbid is not None:
        mbid = payload.mb_artist_mbid.strip()
        if not mbid:
            # Not a silent no-op and not a clear: an empty box is far more often
            # somebody who never filled it in than somebody asking to forget an
            # identification, and forgetting one is not reversible by typing.
            raise HTTPException(
                status_code=http_status.HTTP_400_BAD_REQUEST,
                detail=(
                    "A MusicBrainz artist id cannot be emptied here. Leave the "
                    "field as it was, or paste a different id to replace it."
                ),
            )
        # Raises 503 before anything is written rather than after.
        enricher = require_enricher()

    changed: list[str] = []
    if name is not None and name != artist.name:
        artist.name = name
        changed.append("name")
    if payload.sort_name is not None:
        # The empty string is a real value here: it clears the typed override
        # and hands the answer back to MusicBrainz. ``None`` is stored rather
        # than ``""`` so "nobody has said" stays distinguishable from "somebody
        # said nothing", which is what the read-model coalesce keys on.
        sort_name = payload.sort_name.strip() or None
        if sort_name != artist.sort_name:
            artist.sort_name = sort_name
            changed.append("sort name")
    if aliases is not None and aliases != artist.aliases_list:
        # The empty list is a real value here too — it clears them. Nothing
        # re-derives an alias, so there is no answer to hand back to.
        artist.set_aliases(aliases)
        changed.append("aliases")

    if changed:
        _log_activity(
            session,
            event="artist.tags",
            message=f"Edited {' and '.join(changed)} for {artist.name}",
            artist_id=artist.id,
        )

    if mbid is None:
        await session.commit()
    else:
        try:
            await enricher.identify(
                session,
                EnrichmentEntity.ARTIST,
                artist.id,
                EnrichmentSource.MUSICBRAINZ,
                mbid,
            )
        except LookupError as exc:
            await session.rollback()
            raise HTTPException(
                status_code=http_status.HTTP_404_NOT_FOUND, detail=str(exc)
            ) from exc
        except ValueError as exc:
            # The whole edit goes back, not just the id. Committing the name and
            # refusing the id would leave the drawer showing a half-saved form
            # with an error attached to the field that did save.
            await session.rollback()
            raise HTTPException(
                status_code=http_status.HTTP_400_BAD_REQUEST, detail=str(exc)
            ) from exc

    await session.refresh(artist)
    return artist


async def retag_artist(
    session: AsyncSession,
    artist: Artist,
    *,
    refile: bool = False,
    limit: int = 2000,
) -> LibraryTidyOut:
    """Push this artist's stored identity onto every release of theirs on disk.

    The counterpart to :func:`update_artist_tags`, and the reason that one is
    write-only. Three passes, in an order that is not arbitrary:

    1. **Re-tag**, through the existing :func:`librarian.retag_album` — there is
       no second tagging path here, and there must not be. Unlike
       ``enricher._write_back()``, which refuses albums the disk scan adopted
       because their tags are somebody else's work, this *is* allowed to touch
       them: it is an explicit press, about this artist, by the person who owns
       the library. ``retag_album`` already stands the files in for the missing
       ``Track`` rows those albums have none of.
    2. **Re-file**, and only when asked. A tag fix and a thousand-file move are
       different-sized decisions, so ``refile`` defaults to **False**. It runs
       after the re-tag because re-tagging changes every file's hash: run it
       first and the re-file's staleness gate reads our own edit as tampering
       and refuses everything. ``Album.freeze_path`` is honoured, and — unlike
       the nightly work list — the frozen releases are *reported*: silence about
       a folder somebody explicitly asked to rename reads as a rename that
       happened.
    3. **NFO**, because ``sort_name`` reaches disk as ``<sortname>`` in
       ``artist.nfo`` and nowhere else. Skipping this would make a saved sort
       name a value that never left the database. It comes **last**, after any
       move: ``artist.nfo`` is written into the parent of the album folder, so
       describing before re-filing writes it into the folder the release is
       about to leave and leaves the renamed one — the one a media server reads
       — with none. Safe in this order because an NFO is not audio and cannot
       disturb the baseline the re-file gate reads.

    Nothing is queued and nothing is fetched — this is local, and re-running it
    is safe. Blocked releases are counted and named, never skipped silently, and
    counted **once**: three passes can each refuse the same busy album, and
    reporting "3 blocked" about one release is a figure nobody can act on.
    ``errors`` still carries every reason, so it is legitimately longer than the
    count beside it.
    """
    albums = await librarian.albums_on_disk(session, artist_id=artist.id, limit=limit)
    result = LibraryTidyOut(considered=len(albums))
    #: The releases this pass is *about*, by id, and how to name each one in a
    #: message. Every count below is scoped to it. The NFO sweep works from a
    #: wider set — any album with a recorded path, whatever its status — and an
    #: unmounted share is exactly the case that separates them: ``_verify_library``
    #: demotes every album it cannot see to ``WANTED`` without clearing
    #: ``Album.path``, so an unscoped tally would report "0 of 0 release(s)
    #: re-tagged, 12 blocked" about twelve releases this never considered, naming
    #: them by bare id.
    titles = {album.id: album.display_title for album in albums}
    #: Releases something has already refused, so the next pass does not count
    #: them again. The reason is still appended — a locked NFO on a busy album
    #: is two different facts about one release.
    blocked: set[str] = set()

    def refuse(album_id: str | None, reason: str) -> None:
        if album_id is not None and album_id not in blocked:
            blocked.add(album_id)
            result.blocked += 1
        result.errors.append(reason)

    for album in albums:
        try:
            outcome = await librarian.retag_album(session, album)
        except librarian.LibraryError as exc:
            # A busy album, or one whose audio was replaced since it was
            # measured. One refusal must not end the pass.
            refuse(album.id, f"{album.display_title}: {exc}")
            continue
        if outcome.tagged:
            result.changed += 1
        result.failed += outcome.failed

    if refile:
        plans = await librarian.plan_library_refile(
            session, artist_id=artist.id, limit=limit
        )
        result.plans = [_plan_out(plan) for plan in plans]
        for plan in plans:
            if plan.blocked:
                refuse(plan.album_id, f"{plan.album_title}: {plan.blocked}")
                continue
            if not plan.needed:
                continue
            album = await session.get(Album, plan.album_id)
            if album is None:  # pragma: no cover - deleted mid-pass
                continue
            try:
                await librarian.refile_album(session, album, plan=plan)
            except librarian.FrozenPathError as exc:
                # Only reachable if the flag was set between the plan and the
                # move. Counted as blocked rather than failed all the same: a
                # frozen path is an answer somebody gave, not a fault.
                refuse(plan.album_id, str(exc))
                continue
            except librarian.LibraryError as exc:
                result.failed += 1
                result.errors.append(f"{plan.album_title}: {exc}")
                continue
            result.moved += 1

        # ``plan_library_refile`` drops frozen releases entirely, on purpose: a
        # nightly work list must not keep asking a question somebody already
        # settled. This is not that list. It is one press, about one artist,
        # asking for folders to be renamed — and "two of your five were left
        # alone" is precisely what the person who pressed it needs to know. The
        # refusal is fetched from the mover rather than written again here, so
        # it is word for word the sentence every other ``freeze_path`` refusal
        # gives.
        for album in albums:
            if not album.freeze_path:
                continue
            try:
                await librarian.refile_album(session, album)
            except librarian.LibraryError as exc:
                refuse(album.id, str(exc))
            else:  # pragma: no cover - the flag was cleared mid-pass
                result.moved += 1

    # Last, and after the move rather than before it. ``artist.nfo`` is written
    # into the *parent* of each album folder, so describing first and re-filing
    # second puts it in the folder the release has just left: the renamed artist
    # folder — the one a media server will actually read — ends up with no
    # ``artist.nfo`` at all, and the abandoned one is left holding a stale copy
    # that also stops ``_prune_empty_parents`` clearing it away. Nothing here
    # pushes the other way: an NFO is not audio, so writing one cannot disturb
    # the integrity baseline the re-file pass depends on.
    for described in await librarian.write_library_nfo(
        session, artist_id=artist.id, limit=limit
    ):
        title = titles.get(described.album_id)
        if title is None:
            # Not one of the releases this pass considered — see ``titles``. The
            # file was still written or skipped on its own merits; it is only the
            # arithmetic that stays scoped.
            continue
        if described.skipped:
            # Usually ``<lockdata>true</lockdata>`` — somebody telling their
            # media server to stop editing the file, which Qobuzarr honours.
            refuse(described.album_id, f"{title}: {described.skipped}")
        elif described.album_written or described.artist_written:
            result.described += 1

    parts = [f"{result.changed} of {result.considered} release(s) re-tagged"]
    if result.described:
        parts.append(f"{result.described} described")
    if refile:
        parts.append(f"{result.moved} folder(s) renamed")
    if result.failed:
        parts.append(f"{result.failed} failed")
    if result.blocked:
        parts.append(f"{result.blocked} blocked")
    result.summary = ", ".join(parts)
    result.level = "warning" if (result.blocked or result.failed) else "success"
    return result


async def bulk_update_artists(
    session: AsyncSession, payload: ArtistBulkUpdateIn
) -> tuple[str, dict[str, Any]]:
    """Apply one monitoring change to every artist in ``payload.artist_ids``.

    Only the fields that are not ``None`` are touched, so a bulk mode change
    leaves quality profiles and release types exactly as they were.

    Emptying an artist's accepted release types is allowed but is almost never
    what someone means — an artist that accepts nothing will never mark anything
    wanted again. Those are counted and named in the returned message rather
    than being silently applied.

    Returns:
        ``(message, detail)`` where *detail* carries the counts for the JSON API.
    """
    ids = list(dict.fromkeys(str(a).strip() for a in payload.artist_ids if str(a).strip()))
    if not ids:
        return "Nothing selected — tick some artists first.", {
            "selected": 0,
            "updated": 0,
            "unchanged": 0,
            "missing": 0,
            "emptied": 0,
            "emptied_names": [],
        }

    rows = list(
        (await session.execute(select(Artist).where(Artist.id.in_(ids))))
        .unique()
        .scalars()
        .all()
    )
    missing = len(ids) - len(rows)

    requested: list[str] | None = None
    if payload.release_types is not None:
        requested = parse_release_types(_clean_release_types(payload.release_types) or "")

    updated = 0
    emptied: list[str] = []
    monitor_changed: list[Artist] = []
    for artist in rows:
        before = (
            artist.monitored,
            artist.monitor_mode,
            artist.accepted_release_types,
            artist.include_guest_appearances,
        )
        if payload.monitored is not None:
            artist.monitored = payload.monitored
        if payload.monitor_mode is not None:
            artist.monitor_mode = payload.monitor_mode
        if payload.include_guest_appearances is not None:
            artist.include_guest_appearances = payload.include_guest_appearances
        if requested is not None:
            current = artist.accepted_release_types_list
            if payload.release_types_action == "add":
                merged = current + [rt for rt in requested if rt not in current]
            elif payload.release_types_action == "remove":
                merged = [rt for rt in current if rt not in requested]
            else:
                merged = requested
            artist.set_accepted_release_types(merged)
            if not merged:
                emptied.append(artist.name)
        after = (
            artist.monitored,
            artist.monitor_mode,
            artist.accepted_release_types,
            artist.include_guest_appearances,
        )
        if before != after:
            updated += 1
            # Every field in that tuple is one `desired_status` reads, so any
            # change to it can move the backlog and all three have to be here.
            # Watching only `monitored` is what let a bulk switch to
            # `monitor_mode='none'` leave four thousand releases wanted under
            # artists that would never mark another one.
            monitor_changed.append(artist)

    # One pass over the whole selection rather than one per artist: it is two
    # statements for fifty artists, and the rows it moves are the ones that
    # made "stop watching these" look like it had done nothing.
    change = await apply_monitoring_to_backlog(session, monitor_changed)

    detail: dict[str, Any] = {
        "selected": len(ids),
        "updated": updated,
        "unchanged": len(rows) - updated,
        "missing": missing,
        "emptied": len(emptied),
        # The names, not just the count. The warning is about a specific and
        # recoverable mistake — those artists will never mark anything wanted
        # again — and "3 artists" is not something anybody can act on. The
        # message names three of them because a sentence has to stop somewhere;
        # this is the whole list, for a client that can afford to show it.
        "emptied_names": sorted(emptied),
        # What the edit did to the backlog. Without these two the biggest thing
        # a bulk unmonitor does is invisible: the count on the Releases screen
        # drops by four thousand and nothing said why.
        "demoted": change.demoted,
        "restored": change.restored,
    }

    if not updated:
        await session.rollback()
        return "Nothing to change — those artists already have those settings.", detail

    parts = [f"Updated {updated} artist(s)."]
    if change.demoted:
        parts.append(f"{change.demoted} release(s) are no longer wanted.")
    if change.restored:
        parts.append(f"{change.restored} release(s) are wanted again.")
    if missing:
        parts.append(f"{missing} no longer exist(s).")
    if emptied:
        shown = ", ".join(sorted(emptied)[:3])
        more = f" and {len(emptied) - 3} more" if len(emptied) > 3 else ""
        parts.append(
            f"Heads up: {shown}{more} now accept no release types at all, "
            "so nothing new will be marked wanted for them."
        )
    message = " ".join(parts)

    # One summary row: a bulk edit of 200 artists must not write 200 log lines.
    _log_activity(
        session,
        event="artist.bulk",
        message=message,
        level=ActivityLevel.WARNING if emptied else ActivityLevel.INFO,
    )
    await session.commit()
    return message, detail


async def delete_artist(session: AsyncSession, artist: Artist) -> str:
    """Unfollow *artist*, cascading to albums, tracks and queue items."""
    name = artist.name
    artist_id = artist.id
    await session.delete(artist)
    # The activity row must not reference the artist we just removed.
    _log_activity(
        session,
        event="artist.unfollow",
        message=f"Unfollowed {name} ({artist_id})",
    )
    await session.commit()
    return f"Unfollowed {name}."


async def scan_artist(session: AsyncSession, artist: Artist) -> str:
    """Ask the indexer to check *artist* now.

    The scan runs as a detached background job because indexing one artist is
    many rate-limited API calls. When no indexer is available, ``last_checked_at``
    is cleared instead so the least-recently-checked selection picks this artist
    on the next tick.
    """
    indexer = get_indexer()
    can_scan = callable(getattr(indexer, "check_artist", None))

    if can_scan:
        message = (
            f"Scanning {artist.name} now — releases appear here as they are found."
        )
    else:
        artist.last_checked_at = None
        message = (
            f"{artist.name} moved to the front of the indexer queue "
            "(the indexer runs on its own slow schedule)."
        )

    _log_activity(session, event="artist.scan", message=message, artist_id=artist.id)
    await session.commit()

    if can_scan and not _spawn(
        _scan_artist_job(artist.id, artist.name), label=f"scan:{artist.id}"
    ):
        artist.last_checked_at = None
        await session.commit()
        return f"{artist.name} marked due for the next indexer tick."
    return message


async def queue_wanted_for_artist(session: AsyncSession, artist: Artist) -> str:
    """Queue every ``wanted`` release of *artist*, whatever ``AUTO_DOWNLOAD`` says.

    Falls back to queueing directly from the database when no indexer is wired
    up, so the button still works in a degraded app.
    """
    indexer = get_indexer()
    queue_wanted = getattr(indexer, "queue_wanted", None) if indexer else None

    if callable(queue_wanted):
        queued = await queue_wanted(session, artist)
    else:
        rows = await session.execute(
            select(Album).where(
                Album.artist_id == str(artist.id),
                Album.status == AlbumStatus.WANTED,
                Album.monitored.is_(True),
            )
        )
        queued = 0
        for album in rows.scalars().all():
            _, created = await queue_album(session, album)
            queued += int(created)
        await session.commit()

    if not queued:
        return f"Nothing wanted to download for {artist.name}."

    message = f"Queued {queued} release(s) for {artist.name}."
    _log_activity(
        session, event="queue.bulk", message=message, artist_id=str(artist.id)
    )
    await session.commit()
    return message


async def queue_all_wanted(session: AsyncSession, *, limit: int = 500) -> str:
    """Queue the whole cross-artist backlog — an explicit user action only.

    This is the "Download all" button on the Wanted page. Like
    :func:`queue_wanted_for_artist` it bypasses ``AUTO_DOWNLOAD`` because the
    user asked for it by name; nothing calls this on a timer, and nothing
    should. ``limit`` caps one press so a 5,000-album library cannot be
    committed to disk by a single mis-click.
    """
    rows = await session.execute(
        select(Album)
        .where(Album.status == AlbumStatus.WANTED, Album.monitored.is_(True))
        .order_by(Album.added_at.asc())
        .limit(max(1, limit))
    )
    albums = list(rows.unique().scalars().all())
    queued = 0
    for album in albums:
        _, created = await queue_album(session, album)
        queued += int(created)

    if not queued:
        await session.commit()
        return "Nothing wanted to download."

    message = f"Queued {queued} release(s) for download."
    if len(albums) == limit:
        message += f" Capped at {limit} — press again for the rest."
    _log_activity(session, event="queue.bulk", message=message)
    await session.commit()
    return message


async def scan_all(session: AsyncSession) -> str:
    """Mark every monitored artist due so the indexer sweeps them.

    This deliberately does **not** call ``Indexer.refresh_all_now`` inline: with
    the configured rate limits a full sweep takes hours, which is exactly why
    the scheduler exists. Clearing ``last_checked_at`` makes every artist due
    and lets the normal one-artist-per-tick loop work through them.
    """
    await session.execute(
        update(Artist).where(Artist.monitored.is_(True)).values(last_checked_at=None)
    )
    message = (
        "Full sweep requested; the indexer will work through every monitored "
        "artist at its configured pace."
    )
    _log_activity(session, event="indexer.sweep", message=message)
    await session.commit()
    return message


# ---------------------------------------------------------------------------
# Library management: the services that change files on disk
# ---------------------------------------------------------------------------
def _library_http_error(exc: librarian.LibraryError) -> HTTPException:
    """Map a refusal from :mod:`app.core.librarian` onto a status code.

    A busy album is a 409 (try again once the download finishes); anything else
    the librarian refused is a 400 with its own message, which is written to be
    read by a person.
    """
    if isinstance(exc, librarian.LibraryBusyError):
        code = http_status.HTTP_409_CONFLICT
    else:
        code = http_status.HTTP_400_BAD_REQUEST
    return HTTPException(status_code=code, detail=str(exc))


async def delete_album_files(
    session: AsyncSession, album: Album, *, reason: str = "deleted"
) -> librarian.DeleteResult:
    """Move one release's files to the trash and mark it missing again.

    Nothing is unlinked: the folder goes to ``Settings.trash_dir`` and stays
    there until the trash is emptied. The album returns to ``WANTED`` when it is
    still monitored and ``SKIPPED`` when it is not, and — like every other path
    in Qobuzarr — **nothing is queued**. Deleting is not a request to fetch it
    again.
    """
    try:
        return await librarian.delete_album_files(session, album, reason=reason)
    except librarian.LibraryError as exc:
        await session.rollback()
        raise _library_http_error(exc) from exc


async def refile_album(
    session: AsyncSession, album: Album, *, apply: bool = True
) -> librarian.RefilePlan:
    """Move one release to where ``NAMING_TEMPLATE`` says it belongs.

    Returns the plan either way, so a dry run and a real run report the same
    shape. With ``apply=False`` nothing touches the disk.
    """
    plan = await librarian.plan_refile(session, album)
    if not apply or plan.blocked or not plan.needed:
        return plan
    try:
        await librarian.refile_album(session, album, plan=plan)
    except librarian.LibraryError as exc:
        await session.rollback()
        raise _library_http_error(exc) from exc
    return plan


async def retag_album(session: AsyncSession, album: Album) -> librarian.RetagResult:
    """Rewrite tags and cover art in place from the catalogue metadata we hold.

    Local only — no Qobuz call — and the only library operation that edits a
    file's contents rather than moving it. There is no trash copy of the old
    tags; the audio itself is untouched.
    """
    try:
        return await librarian.retag_album(session, album)
    except librarian.LibraryError as exc:
        await session.rollback()
        raise _library_http_error(exc) from exc


def _plan_out(plan: librarian.RefilePlan) -> RefilePlanOut:
    """Read model for one re-file plan."""
    return RefilePlanOut(
        album_id=plan.album_id,
        album_title=plan.album_title,
        artist_name=plan.artist_name,
        current_dir=plan.current_dir,
        target_dir=plan.target_dir,
        renames=list(plan.renames),
        moves_directory=plan.moves_directory,
        blocked=plan.blocked,
    )


async def refile_library(
    session: AsyncSession,
    *,
    artist_id: str | None = None,
    apply: bool = False,
    limit: int = 2000,
) -> LibraryTidyOut:
    """Re-file every release whose files are not where the template says.

    ``apply`` defaults to **False**: this walks the whole library and moves
    folders, so the preview is the default and committing to it is the explicit
    act. Blocked albums are reported, never skipped silently.
    """
    plans = await librarian.plan_library_refile(
        session, artist_id=artist_id, limit=limit
    )
    result = LibraryTidyOut(dry_run=not apply, considered=len(plans))
    result.plans = [_plan_out(plan) for plan in plans]
    result.blocked = sum(1 for plan in plans if plan.blocked)

    if apply:
        for plan in plans:
            if plan.blocked or not plan.needed:
                continue
            album = await session.get(Album, plan.album_id)
            if album is None:  # pragma: no cover - deleted mid-pass
                continue
            try:
                await librarian.refile_album(session, album, plan=plan)
            except librarian.LibraryError as exc:
                result.failed += 1
                result.errors.append(f"{plan.album_title}: {exc}")
                continue
            result.changed += 1
    else:
        result.changed = sum(1 for plan in plans if plan.needed and not plan.blocked)

    verb = "would be re-filed" if not apply else "re-filed"
    result.summary = (
        f"{result.changed} release(s) {verb}"
        + (f", {result.blocked} blocked" if result.blocked else "")
        + (f", {result.failed} failed" if result.failed else "")
    )
    result.level = "warning" if (result.blocked or result.failed) else "success"
    return result


async def estimate_refile(
    session: AsyncSession,
    *,
    template: str | None = None,
    artist_id: str | None = None,
    limit: int = 2000,
) -> RefileEstimateOut:
    """Count what a naming template would move. Writes nothing, ever.

    ``template`` is the candidate somebody is typing; ``None`` counts against
    the template in force. It is laid over the *effective* settings, so a stored
    ``naming_template`` override cannot put the saved template back on top of
    the one being asked about.
    """
    conf = get_effective_settings()
    if template is not None:
        # The same validator ``PATCH /api/settings`` applies, deliberately: a
        # template this endpoint will count is a template that could be saved,
        # so nobody is handed a confident figure for a value the settings write
        # would then refuse. It is the *only* check, and a second one over
        # ``naming_preview`` was removed rather than kept as belt-and-braces:
        # ``config.parse_override`` already renders the template against probe
        # tracks and refuses anything that raises, renders empty, or maps two
        # tracks onto one path, so a template reaching the preview is one the
        # preview cannot refuse. A branch no input can enter reads as a
        # safeguard while guaranteeing nothing.
        try:
            config.parse_override("naming_template", template)
        except ValueError as exc:
            raise HTTPException(
                status_code=400,
                detail=(
                    f"naming_template {exc}. Check the tokens against the list "
                    "on the Rules screen."
                ),
            ) from exc
    est = await librarian.estimate_library_refile(
        session, template=template, artist_id=artist_id, settings=conf, limit=limit
    )
    out = RefileEstimateOut(
        template=est.template,
        considered=est.considered,
        would_refile=est.would_refile,
        moves_directory=est.moves_directory,
        in_place=est.in_place,
        blocked=est.blocked,
        frozen=est.frozen,
        truncated=est.truncated,
    )
    out.summary = (
        f"{est.would_refile} of {est.considered} release(s) would be re-filed"
        + (f", {est.blocked} blocked" if est.blocked else "")
        + (f", {est.frozen} frozen" if est.frozen else "")
        + (f" (first {limit} only)" if est.truncated else "")
    )
    out.level = "warning" if (est.blocked or est.truncated) else "info"
    return out


async def retag_library(
    session: AsyncSession, *, artist_id: str | None = None, limit: int = 2000
) -> LibraryTidyOut:
    """Re-tag every downloaded release, or one artist's.

    No dry run: re-tagging is idempotent and writes the metadata already in the
    database, so previewing it would only ever list every album.
    """
    albums = await librarian.albums_on_disk(session, artist_id=artist_id, limit=limit)
    result = LibraryTidyOut(considered=len(albums))
    for album in albums:
        try:
            outcome = await librarian.retag_album(session, album)
        except librarian.LibraryError as exc:
            result.blocked += 1
            result.errors.append(f"{album.display_title}: {exc}")
            continue
        if outcome.tagged:
            result.changed += 1
        result.failed += outcome.failed
    result.summary = (
        f"{result.changed} of {result.considered} release(s) re-tagged"
        + (f", {result.failed} file(s) failed" if result.failed else "")
        + (f", {result.blocked} blocked" if result.blocked else "")
    )
    result.level = "warning" if (result.blocked or result.failed) else "success"
    return result


async def write_nfo_library(
    session: AsyncSession, *, artist_id: str | None = None, limit: int = 5000
) -> LibraryTidyOut:
    """Write ``artist.nfo`` and ``album.nfo`` across the library, or one artist's.

    No dry run, for the same reason re-tagging has none: the NFO is rendered from
    the database and rewriting it is idempotent, so a preview would list every
    release every time. And unlike a re-file, nothing moves — the worst outcome
    is a file with the same content it already had.
    """
    results = await librarian.write_library_nfo(
        session, artist_id=artist_id, limit=limit
    )
    result = LibraryTidyOut(considered=len(results))
    for outcome in results:
        if outcome.skipped:
            result.blocked += 1
            result.errors.append(f"{outcome.album_id}: {outcome.skipped}")
        elif outcome.album_written or outcome.artist_written:
            result.changed += 1
    result.summary = (
        f"{result.changed} of {result.considered} release(s) described"
        + (f", {result.blocked} skipped" if result.blocked else "")
    )
    # A skipped NFO is usually ``<lockdata>true</lockdata>`` — somebody telling
    # their media server to stop editing the file, which Qobuzarr honours. Worth
    # a warning so the count is not mistaken for a failure to write.
    result.level = "warning" if result.blocked else "success"
    return result


# ---------------------------------------------------------------------------
# Enrichment
# ---------------------------------------------------------------------------
def require_enricher() -> Any:
    """The running :class:`~app.core.enricher.Enricher`, or a 503."""
    enricher = get_enricher()
    if enricher is None:
        raise HTTPException(
            status_code=http_status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Enrichment is not running.",
        )
    return enricher


async def enrichment_status(session: AsyncSession) -> EnrichmentStatusOut:
    """Coverage, health and how much is waiting on a human.

    ``source_status`` is built from this same payload rather than from a second
    query, so the per-source counts and the totals beside them are one
    observation. It carries the *reason* a rung is gated, which used to exist
    only as a hard-coded sentence in a template — meaning the one place naming
    the missing setting was the one place that could not see the configuration.
    """
    enricher = get_enricher()
    payload: dict[str, Any] = (
        await enricher.status(session)
        if enricher is not None
        else {"enabled": False, "sources": [], "states": {}}
    )
    _, review_total = await list_review_items(session, limit=1)
    return EnrichmentStatusOut(
        **payload,
        review_total=review_total,
        source_status=build_enrichment_sources(payload),
    )


async def enrichment_review(
    session: AsyncSession,
    *,
    state: str | None = None,
    source: str | None = None,
    limit: int = 200,
    offset: int = 0,
) -> tuple[list[EnrichmentReviewOut], int]:
    """What the matchers refused to resolve, for a person to look at."""
    # An unknown ``state`` used to be passed straight through: it matched no row,
    # so the endpoint answered 200 with an empty list. That is the same mistake
    # an unknown ``source`` makes below, but with the misleading answer — "nothing
    # needs a human" is exactly what a review screen wants to hear, so a typo in a
    # bookmarked URL reads as an empty queue rather than as a broken filter. The
    # states are a closed set, published on ``/api/meta``, so they validate like
    # every other enum.
    if state is not None and state not in ENRICHMENT_STATES:
        raise HTTPException(
            status_code=http_status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=f"Unknown enrichment state {state!r}. Known states: "
            + ", ".join(ENRICHMENT_STATES),
        )
    # ``REVIEW_STATES`` rather than a literal pair: the badge in ``deps`` and the
    # default here are one rule, and the moment they are written twice they
    # disagree. Naming ``rejected`` explicitly is legal and deliberate — it is
    # not in the default, so a rejected row leaves the list, but asking for it is
    # the only way to find one again in order to un-reject it.
    states = (state,) if state else REVIEW_STATES
    try:
        items, total = await list_review_items(
            session, states=states, source=source, limit=limit, offset=offset
        )
    except ValueError as exc:
        # An unknown ``source`` reaches the enum constructor inside the query
        # builder. A filter nobody could satisfy is a bad request, not a bug.
        raise HTTPException(
            status_code=http_status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=f"{exc} Known sources: "
            + ", ".join(item.value for item in EnrichmentSource),
        ) from exc
    return [
        EnrichmentReviewOut(
            **{
                field: getattr(item, field)
                for field in (
                    "entity_type",
                    "entity_id",
                    "source",
                    "state",
                    "name",
                    "artist_id",
                    "artist_name",
                    "reason",
                    "attempts",
                    "last_attempt_at",
                    "suggested_release_type",
                )
            },
            is_actionable=item.is_actionable,
            # Both of these are the matcher's own — ``ReviewItem`` computes them
            # beside the rule they describe. Copied through rather than derived
            # here, because a second derivation is a second rule: the client that
            # decided for itself which sources open a picker is exactly what
            # ``identify_sources`` exists to stop, and doing the same thing one
            # layer down would only move the drift.
            identify_sources=list(item.identify_sources),
            state_explanation=item.state_explanation,
        )
        for item in items
    ], total


#: What a person is expected to paste, per source. Named because the id formats
#: are nothing alike — a 36-character UUID against a bare integer — so "that is
#: not a valid id" is unhelpful in exactly the case where somebody has pasted the
#: right thing from the wrong site.
_IDENTIFY_EXPECTS: dict[str, str] = {
    EnrichmentSource.MUSICBRAINZ.value: "MusicBrainz id (a 36-character UUID, or a link containing one)",
    EnrichmentSource.DEEZER.value: "Deezer id (a number, or a link containing one)",
}


def _identify_failure(
    exc: Exception,
    *,
    status_code: int,
    entity_type: str,
    entity_id: str,
    payload: EnrichmentIdentifyIn,
) -> HTTPException:
    """Turn a refusal to identify into an error a picker can be re-drawn from.

    A bad id must not drop somebody back to an empty search. The old HTML route
    kept the candidate list on screen and put the error beside it, and it could
    only do that because it still held the request; a JSON client that gets back
    a bare sentence has to remember what it asked, which it will do right up
    until a refetch lands in between.

    So the response echoes the whole request — the entity, the source, the id
    that was rejected — plus what that source's ids look like. Everything here is
    what the caller already sent; nothing is looked up, and no write has
    happened, because validation runs before any write.
    """
    return HTTPException(
        status_code=status_code,
        detail=ErrorDetail(
            error=str(exc),
            entity_type=str(entity_type),
            entity_id=str(entity_id),
            source=str(payload.source),
            external_id=str(payload.external_id),
            expected=_IDENTIFY_EXPECTS.get(str(payload.source)),
        ),
    )


async def identify_entity(
    session: AsyncSession,
    entity_type: str,
    entity_id: str,
    payload: EnrichmentIdentifyIn,
) -> str:
    """Record an identifier a person supplied for something unmatched.

    Every failure comes back with the request echoed in ``detail`` so the picker
    survives it — see :func:`_identify_failure`.
    """
    try:
        enricher = require_enricher()
    except HTTPException as exc:
        # 503: enrichment is not running. Still the picker's problem to render,
        # and still worth echoing, or the drawer closes on a configuration
        # message it cannot attribute to anything.
        raise _identify_failure(
            RuntimeError(exc.detail),
            status_code=exc.status_code,
            entity_type=entity_type,
            entity_id=entity_id,
            payload=payload,
        ) from exc
    try:
        return await enricher.identify(
            session,
            EnrichmentEntity(entity_type),
            entity_id,
            EnrichmentSource(payload.source),
            payload.external_id,
        )
    except LookupError as exc:
        # The review list outlives the thing it is about: unfollowing an artist
        # leaves its rows behind, and 404 is the honest answer for them.
        raise _identify_failure(
            exc,
            status_code=http_status.HTTP_404_NOT_FOUND,
            entity_type=entity_type,
            entity_id=entity_id,
            payload=payload,
        ) from exc
    except ValueError as exc:
        raise _identify_failure(
            exc,
            status_code=http_status.HTTP_400_BAD_REQUEST,
            entity_type=entity_type,
            entity_id=entity_id,
            payload=payload,
        ) from exc


def _enrichment_target(entity_type: str, source: str | None = None) -> tuple[Any, ...]:
    """Parse the URL's entity type and a body's source, or 400 saying what is valid.

    ``EnrichmentEntity("albun")`` raises ``ValueError`` whose message names the
    Python class, which is exactly the sort of error that reads as a server bug.
    Both vocabularies are closed and published on ``/api/meta``, so a typo is a
    bad request and the answer should list the choices.
    """
    try:
        entity = EnrichmentEntity(entity_type)
    except ValueError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=(
                f"{entity_type!r} is not an entity type. Known types: "
                + ", ".join(item.value for item in EnrichmentEntity)
            ),
        ) from exc
    if source is None:
        return (entity,)
    try:
        return (entity, EnrichmentSource(source))
    except ValueError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=(
                f"{source!r} is not a source. Known sources: "
                + ", ".join(item.value for item in EnrichmentSource)
            ),
        ) from exc


async def reject_enrichment(
    session: AsyncSession,
    entity_type: str,
    entity_id: str,
    payload: EnrichmentRejectIn,
) -> str:
    """Record that a person looked at a review row and said no.

    The other way off the review list, and the reason the list can be trusted:
    "exact or nothing" only ever *adds* to it, so without a dismissal it grows
    monotonically and people stop reading it — which costs the rows that really
    do need a decision.

    Refusals, and what each means to a client:

    * **404** — there is no such work item. The list outlives what it is about;
      unfollowing an artist leaves rows behind until the nightly purge.
    * **400** — the row is in a state nobody is being asked about. Rejecting a
      ``pending`` row dismisses something nobody has read yet, and rejecting an
      ``ok`` row would close the work item while leaving the identifiers it
      wrote in place.

    :meth:`Enricher.reject` commits itself, for the same reason
    :meth:`Enricher.identify` does — the request-scoped session never commits,
    and a rolled-back rejection is a green toast over a row that is still there.
    """
    enricher = require_enricher()
    entity, source = _enrichment_target(entity_type, payload.source)
    try:
        return await enricher.reject(
            session, entity, entity_id, source, reason=payload.reason
        )
    except LookupError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND, detail=str(exc)
        ) from exc
    except ValueError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST, detail=str(exc)
        ) from exc


async def accept_enrichment(
    session: AsyncSession, entity_type: str, entity_id: str
) -> EnrichmentAcceptOut:
    """Apply the proposal this entity already carries — or report that it has none.

    **This endpoint does not search, and must never learn to.** The design draws
    an "Accept & apply" button, and the obvious way to build it is to search the
    upstream and take the best hit; that is precisely what
    ``app.enrich.matching``'s *a name may reject a candidate, never select one*
    forbids, and the id it picked would be written into every file on disk as
    ``MUSICBRAINZ_ALBUMID``. The ``Candidate``/``SearchableProvider`` split makes
    "only a person reads a name search" a fact the type checker holds, and this
    route stays on the right side of it by only ever applying something already
    stored.

    Exactly one proposal is stored today: ``suggested_release_type``, the
    majority verdict of ``app.enrich.merge.consensus``. When it is present and
    the album disagrees with it, Accept writes it (one column, no status change,
    reversible through ``qobuz_release_type``). When it is not, the response is
    ``applied=False`` with ``identify_sources`` — a complete, successful answer
    meaning *open the picker*, because a human choosing is what ``identify`` is
    for. ``EnrichmentReviewOut.suggested_release_type`` tells a client which of
    the two will happen **before** the press, so the button can say so.
    """
    enricher = require_enricher()
    (entity,) = _enrichment_target(entity_type)
    try:
        outcome = await enricher.accept(session, entity, entity_id)
    except LookupError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND, detail=str(exc)
        ) from exc
    except ValueError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST, detail=str(exc)
        ) from exc
    return EnrichmentAcceptOut(
        entity_type=entity.value,
        entity_id=str(entity_id),
        applied=outcome.applied,
        proposal=outcome.proposal,
        previous_value=outcome.previous_value,
        applied_value=outcome.applied_value,
        identify_sources=list(outcome.identify_sources),
        message=outcome.message,
        # Nothing applied is not a warning: it is the answer, and the client
        # follows it by opening a picker. Colouring it amber would train people
        # to read the safe branch as a fault.
        level="success" if outcome.applied else "info",
    )


async def reopen_enrichment(
    session: AsyncSession,
    entity_type: str,
    entity_id: str,
    source: str | None = None,
) -> str:
    """Put an entity back on the work list, whatever state it is in.

    "Run now", and the only way back from ``rejected``. That matters more than it
    looks: ``rejected`` is the one state nothing re-arms — ``_rearm_stranded``
    watches for the *inputs* ``no_key`` and ``gated`` are waiting on, and a
    rejection is waiting on nobody — so a person who changes their mind has no
    other route. It works because ``_reopen`` sets ``pending`` unconditionally.

    The commit lives here rather than in ``Enricher.reopen``, which is also
    called from inside other people's transactions (``identify`` finishes with
    one, and the scheduler re-opens replaced releases mid-pass). A method that
    committed would end those transactions early; a request that did not commit
    would roll the whole thing back, which is the bug ``identify`` was shipped
    with. So the entry point commits, and only the entry point.
    """
    enricher = require_enricher()
    entity, *rest = _enrichment_target(entity_type, source)
    entity_id = str(entity_id)
    # The same check ``identify`` does first, and for a sharper reason here:
    # ``Enricher._reopen`` *creates* the row when there is none, so an id that
    # is not in the library at all would be answered "Queued 5 source(s)" over
    # five rows for a thing that does not exist — work nothing will ever claim
    # (``_claim`` is held to ``in_library()``) and that only the nightly purge
    # removes. A review row outlives its subject, so 404 is the honest answer.
    subject = await session.get(
        Artist if entity is EnrichmentEntity.ARTIST else Album, entity_id
    )
    if subject is None:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND,
            detail=f"{entity.value} {entity_id} is not in the library any more.",
        )
    reopened = await enricher.reopen(
        session, entity, entity_id, sources=rest or None
    )
    await session.commit()
    return f"Queued {reopened} source(s) for this {entity.value}."


async def reidentify_album(session: AsyncSession, album: Album) -> MessageOut:
    """Fingerprint one release again and work out what it is, now.

    The design calls this button "Re-fingerprint", and building that literally
    would be dishonest twice over: nothing in this program stores a fingerprint
    per release (the drawer's own AcoustID placeholder says so), and ``fpcalc``
    is not something a route may call — it is an implementation detail inside
    the AcoustID rung, run through ``asyncio.to_thread`` by the provider. What a
    person wants from the press is *measure this directory's audio again and
    work out which release it is*, which is a per-album re-open plus a cascade:
    the same two calls :meth:`Enricher.identify` ends with, scoped to one album.

    Three decisions, each of them an invariant rather than a preference:

    * **:meth:`Enricher.reopen`, never ``mark_library_due``.** ``mark_due``
      moves ``pending``/``not_found``/``failed`` and nothing else, so an
      identified release — every row ``ok``, which is exactly the release
      somebody wants re-checked — would come back a number with nothing behind
      it. ``reopen`` resets the state, the attempts and ``last_error``
      regardless. It is called directly rather than through
      ``enricher.reopen_library_albums`` because that helper is built for batch
      callers already inside a transaction: it reaches the enricher through
      ``app.core.state``, returns an album count rather than a source count, and
      has no way to say *this release is not on disk* in a sentence.
    * **Scope is tested before anything is written.** Enrichment's subject is
      the library, not the catalogue: a release that is not ``DOWNLOADED`` has
      no audio to fingerprint, and seeding rows for it only gives the nightly
      ``purge_out_of_scope_enrichment`` something to delete.
    * **Commit, then cascade — in that order.** The request-scoped session never
      commits, so a re-open without one is a green toast over a rollback; and
      the cascade opens its own sessions and makes AcoustID and MusicBrainz
      calls, so running it inside this transaction would hold SQLite's single
      writer across a network call, which is the stall that makes
      ``QueueWorker`` mark a live download failed.

    Every album-supporting rung is re-opened, not AcoustID alone. A fresh
    fingerprint verdict can change which release this directory *is*, so a
    MusicBrainz row left at ``ok`` would keep the MBID the old audio pinned and
    the write-back would stamp it into the files again — the same reasoning the
    ``REPLACED`` path already uses. Scope stays the album; re-opening the artist
    would re-derive keys for their whole discography.

    No corruption verdict is reported here. ``FpcalcMissing`` lands the row
    ``gated``, ``FingerprintTimeout`` and ``FileUnavailable`` leave no verdict at
    all, and only ``UnreadableAudio`` ever reaches
    ``track_metadata.fingerprint_state`` — all inside ``AcoustIdProvider``. What
    the drawer learns is what ran; it re-reads ``integrity_state`` and
    ``corrupt_tracks`` from the invalidated detail query.
    """
    enricher = require_enricher()
    in_scope = (
        await session.execute(
            library_scope(EnrichmentEntity.ALBUM).where(Album.id == album.id)
        )
    ).scalars().first()
    if in_scope is None:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=(
                f"{album.display_title} is not on disk (status "
                f"{album.status.value}), so there is no audio to fingerprint. "
                "Enrichment's subject is the library, not the catalogue."
            ),
        )
    reopened = await enricher.reopen(session, EnrichmentEntity.ALBUM, album.id)
    if reopened == 0:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=(
                "No enrichment source is configured for releases "
                "(ENRICHMENT_SOURCES)."
            ),
        )
    await session.commit()
    outcome = await enricher.cascade(EnrichmentEntity.ALBUM, album.id)

    detail: dict[str, Any] = {
        "album_id": album.id,
        "sources": reopened,
        "ran": outcome.ran,
        "written_back": outcome.written_back,
        "exhausted": outcome.exhausted,
        "skipped": outcome.skipped,
    }
    if outcome.skipped:
        return MessageOut(
            ok=True,
            message=(
                f"Re-opened {reopened} source(s). A pass is already running; "
                "it will be picked up there."
            ),
            level="info",
            detail=detail,
        )
    if not outcome.ran:
        return MessageOut(
            ok=True,
            message=(
                f"Re-opened {reopened} source(s). They will be fetched on the "
                "next pass."
            ),
            level="info",
            detail=detail,
        )
    tail = (
        f" and re-tagged {outcome.written_back} file(s)"
        if outcome.written_back
        else ""
    )
    rest = "; the rest follows on the next pass" if outcome.exhausted else ""
    return MessageOut(
        ok=True,
        message=(
            f"Re-identified {album.display_title}: ran {outcome.ran} source(s)"
            f"{tail}{rest}."
        ),
        level="success",
        detail=detail,
    )


async def enrichment_candidates(
    session: AsyncSession,
    entity_type: str,
    entity_id: str,
    source: str,
    query: str | None = None,
) -> EnrichmentCandidatesOut:
    """Upstream records a person could pick as the match for this entity.

    Search results, and the one place in the codebase that produces any. That is
    safe for exactly one reason — nothing downstream picks from them. They are
    rendered as cards and a person presses one.
    """
    enricher = require_enricher()
    try:
        # Converted before the call so a junk source name says what the choices
        # are, rather than surfacing "'banana' is not a valid EnrichmentSource".
        known = EnrichmentSource(source)
    except ValueError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=(
                f"{source!r} is not a source. Known sources: "
                + ", ".join(item.value for item in EnrichmentSource)
            ),
        ) from exc
    try:
        items = await enricher.candidates(
            session, EnrichmentEntity(entity_type), entity_id, known, query
        )
    except LookupError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND, detail=str(exc)
        ) from exc
    except ValueError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST, detail=str(exc)
        ) from exc
    except EnrichmentOutcome as exc:
        # A gated rung, most often MusicBrainz with no contact address. Not an
        # error in the request — a piece of configuration that is missing.
        raise HTTPException(
            status_code=http_status.HTTP_409_CONFLICT, detail=str(exc)
        ) from exc
    except HttpError as exc:
        # The upstream is down or rate-limiting. 502 rather than 500: nothing
        # here is wrong with Qobuzarr, and the user can simply try again.
        raise HTTPException(
            status_code=http_status.HTTP_502_BAD_GATEWAY,
            detail=f"{source} did not answer: {exc}",
        ) from exc

    return EnrichmentCandidatesOut(
        entity_type=entity_type,
        entity_id=entity_id,
        source=source,
        query=str(query or ""),
        items=[
            EnrichmentCandidateOut(
                external_id=item.external_id,
                title=item.title,
                subtitle=item.subtitle,
                detail=item.detail,
                disambiguation=item.disambiguation,
                image_url=item.image_url,
                url=item.url,
            )
            for item in items
            if item.external_id
        ],
    )


async def run_enrichment_now(limit: int | None = None) -> str:
    """Drain a batch right now. Read-only lookups; nothing is ever queued."""
    enricher = require_enricher()
    enricher.resume()
    result = await enricher.tick(limit=limit)
    summary = result.as_dict()
    return (
        f"{summary['matched']} matched, {summary['unmatched']} unmatched, "
        f"{summary['gated']} gated, {summary['failed']} failed "
        f"in {summary['elapsed']}s"
    )


async def trash_contents(settings: Settings | None = None) -> TrashOut:
    """Everything currently recoverable, newest first."""
    conf = settings or get_settings()
    entries = await asyncio.to_thread(librarian.list_trash, conf)
    return TrashOut(
        entries=[TrashEntryOut.model_validate(entry.as_dict()) for entry in entries],
        total=len(entries),
        size_bytes=sum(entry.size_bytes for entry in entries),
        path=str(conf.trash_dir),
    )


async def restore_trash_entry(entry_id: str, settings: Settings | None = None) -> str:
    """Put one trashed batch back where it came from.

    The database is **not** touched: a restored album is a folder that appeared
    on disk, which is exactly what the disk scan exists to reconcile. Run a scan
    afterwards to have it adopted again.
    """
    conf = settings or get_settings()
    try:
        restored = await asyncio.to_thread(
            librarian.restore_from_trash, entry_id, conf
        )
    except librarian.LibraryError as exc:
        raise _library_http_error(exc) from exc
    return str(restored)


async def empty_trash(
    entry_id: str | None = None, settings: Settings | None = None
) -> tuple[int, int]:
    """Delete trashed batches for real. The one irreversible action in Qobuzarr."""
    conf = settings or get_settings()
    try:
        return await asyncio.to_thread(librarian.empty_trash, conf, entry_id)
    except librarian.LibraryError as exc:
        raise _library_http_error(exc) from exc


async def scan_library(
    session: AsyncSession,
    *,
    artist_id: str | None = None,
    apply: bool = True,
) -> dict[str, Any]:
    """Walk the library folder and adopt whatever is already on disk.

    Purely local: it reads files and the database and makes **no Qobuz API
    call**, so unlike :func:`scan_artist` it costs nothing against the rate
    limit and can run inline in the request. It also never queues or downloads
    anything — see :mod:`app.core.scanner` for the rules it holds itself to.

    Args:
        session: Request session; the scanner commits it once at the end.
        artist_id: Restrict changes to one artist. Folders belonging to anyone
            else are ignored rather than reported.
        apply: When false the report is built but nothing is written.

    Returns:
        :meth:`app.core.scanner.ScanResult.as_dict`.

    Raises:
        HTTPException: 503 when no scanner is wired up (the application was
            started without its state), 404 when *artist_id* is unknown.
    """
    scanner = get_library_scanner()
    if scanner is None or not callable(getattr(scanner, "scan", None)):
        raise HTTPException(
            status_code=http_status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="The library scanner is not available on this instance.",
        )
    if getattr(scanner, "running", False):
        raise HTTPException(
            status_code=http_status.HTTP_409_CONFLICT,
            detail="A disk scan is already running.",
        )

    try:
        result = await scanner.scan(session, artist_id=artist_id, apply=apply)
    except LookupError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND, detail=str(exc)
        ) from exc

    report = result.as_dict()
    if apply and artist_id is None:
        report["import_started"] = await _follow_unknown_artists(report)
    return report


async def _follow_unknown_artists(report: dict[str, Any]) -> bool:
    """Start following the artists this scan found on disk but nobody follows.

    This is what makes a scan two-way, and it is deliberately the **only** part
    of a scan that is: everything :mod:`app.core.scanner` itself does is still
    one-directional and still makes no Qobuz call, so the nightly job's safety
    argument is untouched and a dry run is still a dry run. The following
    happens afterwards, in :mod:`app.core.importer`, which remains the one place
    allowed to follow in bulk.

    It is **started, not awaited**. A library with 136 unknown artists is one
    Qobuz search each through the shared limiter — about seven minutes — and a
    scan request that blocked for seven minutes would time out in the browser
    long before it finished, having still done the work. So this returns as soon
    as the run is under way and the caller polls ``GET /api/library/import``,
    exactly like the Import button does. ``import_started`` on the report is how
    the client knows to start polling.

    Three things make it safe to fire from a button somebody presses often: it
    stands down when an import is already running, it follows only on an exact
    name or an exact barcode, and it downloads nothing — ``AUTO_DOWNLOAD`` still
    governs that, and the new artists are indexed one per scheduled tick rather
    than all at once.
    """
    # ``as_dict()`` publishes the list, not the property that counts it, and
    # the list is capped — so an empty list really does mean "nobody new".
    if not report.get("unknown_artists"):
        return False
    importer = get_library_importer()
    if importer is None or not callable(getattr(importer, "start", None)):
        return False
    if getattr(importer, "running", False):
        return False
    try:
        await importer.start(index_now=False)
    except Exception:  # noqa: BLE001 - a scan must still report what it found
        logger.exception("Could not start the artist import after the disk scan")
        return False
    return True


def _require_importer() -> Any:
    """The shared importer, or a 503 when the application has no state."""
    importer = get_library_importer()
    if importer is None or not callable(getattr(importer, "start", None)):
        raise HTTPException(
            status_code=http_status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="The artist importer is not available on this instance.",
        )
    return importer


async def start_library_import(payload: LibraryImportStartIn) -> dict[str, Any]:
    """Begin importing the artists found in the library folder.

    One Qobuz search per artist, through the shared rate limiter, in a
    background task — a 500-artist library takes minutes, not milliseconds, so
    the request returns as soon as the run is under way and the caller polls
    :func:`api_library_import_status`.

    Only unambiguous name matches are followed; everything else lands in the
    review list. Nothing is indexed unless ``index_now`` is set, and nothing is
    ever downloaded.

    Raises:
        HTTPException: 503 with no importer, 409 when a run is already going.
    """
    importer = _require_importer()
    try:
        return await importer.start(
            names=payload.names,
            monitored=payload.monitored,
            monitor_mode=payload.monitor_mode,
            quality_profile=payload.quality_profile,
            release_types=payload.release_types,
            index_now=payload.index_now,
            limit=payload.limit,
        )
    except RuntimeError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_409_CONFLICT, detail=str(exc)
        ) from exc


async def cancel_library_import() -> bool:
    """Ask a running import to stop after the artist it is on. ``False`` if idle."""
    return bool(await _require_importer().cancel())


async def preview_library_import(session: AsyncSession) -> dict[str, Any]:
    """Who an import would look up, and roughly how long it would take.

    A dry disk scan — local only, no Qobuz call — so it is safe to render
    before asking the user to commit to a long run.
    """
    importer = _require_importer()
    candidates = await importer.preview(session)
    return {
        "count": len(candidates),
        "names": [str(row.get("name") or "") for row in candidates],
        "eta_seconds": importer.estimate_seconds(len(candidates)),
    }


async def _reload_queue_items(session: AsyncSession, album: Album) -> None:
    """Re-read *album*'s queue entries after one was inserted by ``album_id``.

    Sessions are built with ``expire_on_commit=False``, and a ``QueueItem``
    created from an id never touches the album's loaded ``queue_items``
    collection — so without this the caller re-renders the album from a
    collection that predates the row it just added. The artist table decides
    between "In queue" and the Download/Upgrade button on exactly that
    collection, so it would offer the button a second time.

    Refresh rather than expire: an expired ``selectin`` collection reloads
    lazily on attribute access, which raises ``MissingGreenlet`` under asyncio.
    """
    try:
        await session.refresh(album, ["queue_items"])
    except Exception as exc:  # noqa: BLE001 - display detail, never fatal
        logger.debug("Could not reload queue items for %s: %s", album.id, exc)


async def queue_album(session: AsyncSession, album: Album) -> tuple[QueueItem, bool]:
    """Mark *album* wanted and put it on the download queue.

    Existing non-terminal entries are reused rather than duplicated.

    Prefers :meth:`app.core.queue.QueueWorker.enqueue_album` so the worker's own
    bookkeeping (progress totals, activity events, wake-up) happens; falls back
    to inserting the row directly when no worker is running.

    Returns:
        ``(queue_item, created)``.
    """
    worker = get_queue_worker()
    if callable(getattr(worker, "enqueue_album", None)):
        try:
            item = await worker.enqueue_album(session, album.id)
            await session.commit()
        except LookupError as exc:  # album vanished between lookups
            await session.rollback()
            raise HTTPException(
                status_code=http_status.HTTP_404_NOT_FOUND, detail=str(exc)
            ) from exc
        except Exception as exc:  # noqa: BLE001 - fall back to the DB-only path
            await session.rollback()
            logger.warning("QueueWorker.enqueue_album(%s) failed: %s", album.id, exc)
        else:
            if item is not None:
                await session.refresh(item)
                await _reload_queue_items(session, album)
                return item, True
            current = (
                await session.execute(
                    select(QueueItem)
                    .where(
                        QueueItem.album_id == album.id,
                        QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE)),
                    )
                    .order_by(QueueItem.id.desc())
                    .limit(1)
                )
            ).unique().scalars().first()
            if current is not None:
                return current, False

    existing = (
        await session.execute(
            select(QueueItem)
            .where(
                QueueItem.album_id == album.id,
                QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE)),
            )
            .limit(1)
        )
    ).unique().scalars().first()

    created = existing is None
    if existing is None:
        existing = QueueItem(album_id=album.id, state=QueueState.PENDING)
        session.add(existing)

    album.monitored = True
    if album.status in (AlbumStatus.SKIPPED, AlbumStatus.FAILED, AlbumStatus.WANTED):
        album.status = AlbumStatus.QUEUED

    _log_activity(
        session,
        event="queue.add",
        message=f"Queued {album.display_title}",
        artist_id=album.artist_id,
        album_id=album.id,
    )
    await session.commit()
    await session.refresh(existing)
    await _reload_queue_items(session, album)

    worker = get_queue_worker()
    try:
        await call_first(worker, ("wake", "notify", "kick", "poke", "signal"))
    except Exception as exc:  # noqa: BLE001 - the row is already persisted
        logger.debug("Could not wake the download worker: %s", exc)
    return existing, created


def _apply_album_flags(album: Album, payload: AlbumUpdateIn) -> dict[str, bool]:
    """Write the per-album switches this body actually carries. Returns what changed.

    Only fields that are not ``None`` are written, which is the whole of "partial
    updates mean partial": a switch the client left alone must arrive as an
    omitted key, never as ``false``, and this function is the half that makes an
    omitted key harmless.

    Returns the switches whose value is genuinely different from what was stored,
    so the caller can log the change and say nothing when there was none.
    """
    changed: dict[str, bool] = {}
    for name, value in payload.flag_fields.items():
        if bool(getattr(album, name)) == value:
            continue
        setattr(album, name, value)
        changed[name] = value
    return changed


async def apply_album_update(
    session: AsyncSession, album: Album, payload: AlbumUpdateIn
) -> Album:
    """Partial update of one release: ``None`` everywhere means *leave alone*.

    Deliberately **not** ``set_album_monitored``. That endpoint's empty body
    toggles ``monitored``, which is right for a row's switch and exactly wrong
    here: a PATCH that sets ``pin_tags`` and says nothing about monitoring must
    leave monitoring where it is, and inheriting the toggle would make it flip
    every time somebody pinned a release.

    The ``monitored``/``status`` collapse rule is shared with the toggle,
    however, and is the reason this does not simply assign the fields: turning
    monitoring off has to take a ``wanted`` release out of the backlog rather
    than leave it sitting there inert, and back in when it goes on again. The
    worker owns ``queued``/``downloading``, so those are untouched either way.
    """
    changed_flags = _apply_album_flags(album, payload)
    monitor_changed = False
    if payload.monitored is not None and payload.monitored != album.monitored:
        album.monitored = payload.monitored
        monitor_changed = True

    if payload.status is not None:
        album.status = payload.status
    elif monitor_changed and album.monitored and album.status is AlbumStatus.SKIPPED:
        album.status = AlbumStatus.WANTED
    elif (
        monitor_changed and not album.monitored and album.status is AlbumStatus.WANTED
    ):
        album.status = AlbumStatus.SKIPPED

    if changed_flags or monitor_changed or payload.status is not None:
        described = ", ".join(
            f"{name}={'on' if value else 'off'}"
            for name, value in sorted(changed_flags.items())
        )
        if monitor_changed:
            described = ", ".join(
                filter(None, [f"monitored={'on' if album.monitored else 'off'}", described])
            )
        _log_activity(
            session,
            event="album.update",
            message=f"{album.display_title}: {described or 'status changed'}",
            artist_id=album.artist_id,
            album_id=album.id,
        )
    await session.commit()
    await session.refresh(album)
    return album


async def set_album_monitored(
    session: AsyncSession, album: Album, payload: AlbumUpdateIn
) -> Album:
    """Toggle or set an album's monitored flag and optional status.

    Refuses a body carrying ``pin_tags``, ``freeze_path`` or ``mute_integrity``.
    They share a request model with this endpoint, and this endpoint's contract
    is that an **absent** ``monitored`` toggles — so ``{"pin_tags": true}`` would
    set the switch *and* silently flip monitoring, which is the collision the
    monitor endpoint's whole history is about. Sending them here is a mistake in
    the caller and a 400 says so; ``PATCH /api/albums/{id}`` is the partial
    update that takes them.
    """
    if payload.flag_fields:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=ErrorDetail(
                error=(
                    "This endpoint toggles monitoring; an empty body flips it. "
                    "Use PATCH /api/albums/{album_id} to set pin_tags, "
                    "freeze_path or mute_integrity."
                ),
                code="wrong_endpoint",
                album_id=str(album.id),
                rejected=sorted(payload.flag_fields),
            ),
        )
    album.monitored = (
        (not album.monitored) if payload.monitored is None else payload.monitored
    )
    if payload.status is not None:
        album.status = payload.status
    elif album.monitored and album.status is AlbumStatus.SKIPPED:
        album.status = AlbumStatus.WANTED
    elif not album.monitored and album.status is AlbumStatus.WANTED:
        album.status = AlbumStatus.SKIPPED

    _log_activity(
        session,
        event="album.monitor",
        message=(
            f"{'Monitoring' if album.monitored else 'Ignoring'} "
            f"{album.display_title}"
        ),
        artist_id=album.artist_id,
        album_id=album.id,
    )
    await session.commit()
    await session.refresh(album)
    return album


async def retry_queue_item(session: AsyncSession, item: QueueItem) -> str:
    """Return a failed/cancelled queue entry to the pending state."""
    if item.state is QueueState.ACTIVE:
        raise HTTPException(
            status_code=http_status.HTTP_409_CONFLICT,
            detail="That item is downloading right now.",
        )

    worker = get_queue_worker()
    if callable(getattr(worker, "retry_item", None)):
        try:
            if await worker.retry_item(session, item.id):
                _log_activity(
                    session,
                    event="queue.retry",
                    message=f"Retrying queue item {item.id}",
                    album_id=item.album_id,
                )
                await session.commit()
                return f"Queue item {item.id} will be retried."
        except Exception as exc:  # noqa: BLE001 - fall back to the DB-only path
            await session.rollback()
            logger.warning("QueueWorker.retry_item(%s) failed: %s", item.id, exc)
            item = await get_queue_item_or_404(session, item.id)

    item.state = QueueState.PENDING
    item.attempts = 0
    item.last_error = None
    item.started_at = None
    item.finished_at = None
    album = getattr(item, "album", None)
    if album is not None and album.status is AlbumStatus.FAILED:
        album.status = AlbumStatus.QUEUED
    _log_activity(
        session,
        event="queue.retry",
        message=f"Retrying queue item {item.id}",
        album_id=item.album_id,
    )
    await session.commit()

    worker = get_queue_worker()
    try:
        await call_first(worker, ("wake", "notify", "kick", "poke", "signal"))
    except Exception as exc:  # noqa: BLE001
        logger.debug("Could not wake the download worker: %s", exc)
    return f"Queue item {item.id} will be retried."


async def cancel_queue_item(session: AsyncSession, item: QueueItem) -> str:
    """Cancel a queue entry.

    An item the worker is actively downloading cannot be interrupted mid-track,
    but :meth:`app.core.queue.QueueWorker.cancel_item` raises a per-item stop
    flag so the album really does stop after the file currently in flight — it
    is not left to run to completion.
    """
    worker = get_queue_worker()
    if callable(getattr(worker, "cancel_item", None)):
        try:
            if await worker.cancel_item(session, item.id):
                _log_activity(
                    session,
                    event="queue.cancel",
                    message=f"Cancelled queue item {item.id}",
                    level=ActivityLevel.WARNING,
                    album_id=item.album_id,
                )
                await session.commit()
                return f"Queue item {item.id} cancelled."
        except Exception as exc:  # noqa: BLE001 - fall back to the DB-only path
            await session.rollback()
            logger.warning("QueueWorker.cancel_item(%s) failed: %s", item.id, exc)
            item = await get_queue_item_or_404(session, item.id)

    item.state = QueueState.CANCELLED
    item.finished_at = utcnow()
    album = getattr(item, "album", None)
    if album is not None and album.status in (
        AlbumStatus.QUEUED,
        AlbumStatus.DOWNLOADING,
    ):
        album.status = AlbumStatus.WANTED
    _log_activity(
        session,
        event="queue.cancel",
        message=f"Cancelled queue item {item.id}",
        level=ActivityLevel.WARNING,
        album_id=item.album_id,
    )
    await session.commit()
    return f"Queue item {item.id} cancelled."


async def run_search(
    session: AsyncSession,
    query: str,
    *,
    search_type: str = "all",
    limit: int = 25,
    offset: int = 0,
) -> SearchResultOut:
    """Search the Qobuz catalogue and annotate hits with local library state.

    Raises:
        HTTPException: 503 when no client is configured, 502 when Qobuz errors.
    """
    query = (query or "").strip()
    result = SearchResultOut(query=query, limit=limit, offset=offset)
    if not query:
        return result

    client = require_client()
    wanted = str(search_type or "all").lower()
    try:
        payload = await client.search(query, type="all", limit=limit, offset=offset)
    except QobuzError as exc:
        logger.warning("Qobuz search for %r failed: %s", query, exc)
        # 502, and ``code`` says which of the two failures this is. The search
        # panel draws both itself — a catalogue that will not answer is not a
        # reason to replace the screen somebody is working on — and the two want
        # different affordances: this one retries, ``no_client`` sends them to
        # the settings they have not filled in.
        raise HTTPException(
            status_code=http_status.HTTP_502_BAD_GATEWAY,
            detail=ErrorDetail(
                error=f"Qobuz search failed: {exc}",
                code="upstream_error",
                query=query,
            ),
        ) from exc

    def _bucket(key: str) -> dict[str, Any]:
        value = payload.get(key) if isinstance(payload, dict) else None
        return value if isinstance(value, dict) else {}

    if wanted in ("all", "artists", "artist"):
        bucket = _bucket("artists")
        items = [item for item in bucket.get("items", []) if isinstance(item, dict)]
        mapped = [map_artist(item) for item in items]
        ids = [m["id"] for m in mapped if m.get("id")]
        followed: set[str] = set()
        if ids:
            followed = {
                row
                for row in (
                    await session.execute(select(Artist.id).where(Artist.id.in_(ids)))
                )
                .scalars()
                .all()
            }
        result.total_artists = int(bucket.get("total", len(mapped)) or 0)
        result.artists = [
            SearchArtistOut(
                id=str(m["id"]),
                name=m.get("name") or "Unknown artist",
                image_url=m.get("image_url"),
                albums_count=int(m.get("albums_count") or 0),
                slug=m.get("qobuz_slug"),
                followed=str(m["id"]) in followed,
            )
            for m in mapped
            if m.get("id")
        ]

    if wanted in ("all", "albums", "album"):
        bucket = _bucket("albums")
        items = [item for item in bucket.get("items", []) if isinstance(item, dict)]
        albums: list[SearchAlbumOut] = []
        for raw in items:
            mapped = map_album(raw)
            album_id = mapped.get("id")
            if not album_id:
                continue
            artist_info = extract_album_artist(raw) or {}
            release_date = mapped.get("release_date")
            albums.append(
                SearchAlbumOut(
                    id=str(album_id),
                    title=mapped.get("title") or "Unknown album",
                    version=mapped.get("version"),
                    artist_id=(str(artist_info["id"]) if artist_info.get("id") else None),
                    artist_name=artist_info.get("name"),
                    release_date=release_date,
                    year=release_date.year if release_date else None,
                    tracks_count=int(mapped.get("tracks_count") or 0),
                    hires=bool(mapped.get("hires")),
                    image_url=mapped.get("image_url"),
                )
            )
        if albums:
            # "In library" means the files are there, not that a row exists.
            # The indexer writes a row for every release it sees, so testing for
            # the row alone marks the whole of a followed artist's catalogue as
            # owned — including the releases the Wanted page is asking for.
            rows = (
                await session.execute(
                    select(Album.id, Album.status).where(
                        Album.id.in_([a.id for a in albums])
                    )
                )
            ).all()
            on_disk = {
                str(album_id)
                for album_id, status in rows
                if status is AlbumStatus.DOWNLOADED
            }
            tracked = {str(album_id) for album_id, _status in rows}
            for album in albums:
                album.in_library = album.id in on_disk
                album.tracked = album.id in tracked
        result.total_albums = int(bucket.get("total", len(albums)) or 0)
        result.albums = albums

    return result


# ---------------------------------------------------------------------------
# Health
# ---------------------------------------------------------------------------
@health_router.get(
    "/health", response_model=HealthOut, summary="Liveness and database probe"
)
async def health(settings: SettingsDep) -> HealthOut:
    """Report process, database and credential health.

    ``degraded`` rather than a 500 when the database will not answer: the process
    is alive and able to say what is wrong, which is the entire point of a health
    endpoint. Missing credentials are deliberately **not** degradation —
    Qobuzarr is designed to run without them and simply does less.
    """
    db = await healthcheck()
    client = get_qobuz_client()
    return HealthOut(
        status="ok" if db.get("ok") else "degraded",
        version=__version__,
        database=DatabaseHealthOut(**db),
        credentials_ok=settings.has_credentials,
        app_secret_ok=bool(getattr(client, "has_app_secret", False))
        or bool(settings.qobuz_app_secret),
        qobuz_client=client is not None,
        indexer_enabled=settings.indexer_enabled,
    )


async def health_summary(
    session: AsyncSession,
    settings: Settings,
    *,
    activity_limit: int = 0,
) -> HealthSummaryOut:
    """Gather every subsystem's own payload for the Health overview.

    Five sub-screens, one glance. The temptation on a screen like this is to
    compute the handful of numbers it shows — a scan count here, an unresolved
    count there — because each card only needs two or three of them. That is how
    an overview starts disagreeing with the page it links to, and this codebase
    already has the name for it: a live region has to render from the same
    context as its page, or the two quietly tell different stories about the same
    database.

    So nothing is computed here. Each field is the *same builder* the sub-endpoint
    calls, which makes equality a property of the construction rather than
    something a test has to keep re-establishing: ``summary.integrity`` is what
    ``GET /api/integrity`` answers because it is the same call.

    ``activity_limit`` is the one knob, and it defaults to **0** because the
    overview shows no activity feed — the Activity screen owns that. Pass 15 and
    the ``status`` field is byte-identical to ``GET /api/status``, which is what
    makes the equality checkable rather than merely claimed.

    Never raises for want of a subsystem: every builder here already degrades to
    "not wired up" on a half-started process, which is precisely the state this
    screen exists to describe.
    """
    trash = await trash_contents(settings)
    return HealthSummaryOut(
        status=await build_status(session, activity_limit=activity_limit),
        scan=LibraryScanStatusOut.model_validate(
            await library_scan_status(session, settings)
        ),
        library_import=LibraryImportOut.model_validate(
            await library_import_status(session)
        ),
        enrichment=await enrichment_status(session),
        integrity=await integrity_status(session, settings),
        # Counted from the same listing ``GET /api/library/trash`` returns, minus
        # the entries themselves: an overview that shipped the whole manifest of
        # every trashed batch would be paying the tidy screen's price for three
        # numbers.
        trash=TrashSummaryOut(
            total=trash.total, size_bytes=trash.size_bytes, path=trash.path
        ),
    )


@router.get(
    "/health/summary",
    response_model=HealthSummaryOut,
    summary="Every subsystem's payload, for the Health overview",
)
async def api_health_summary(
    session: SessionDep,
    settings: SettingsDep,
    activity_limit: int = Query(
        0, ge=0, le=100, description="Activity rows to include in `status`."
    ),
) -> HealthSummaryOut:
    """One request for the Health landing screen.

    Convenience, not a new source of truth — every sub-screen keeps polling its
    own endpoint, and the numbers here are those endpoints' numbers because they
    come from the same builders. If this ever disagrees with a sub-screen, this
    is the one that is wrong.
    """
    return await health_summary(session, settings, activity_limit=activity_limit)


# ---------------------------------------------------------------------------
# Status / settings
# ---------------------------------------------------------------------------
@router.get("/status", response_model=StatusOut, summary="Dashboard payload")
async def api_status(
    session: SessionDep,
    activity_limit: int = Query(15, ge=0, le=100),
) -> StatusOut:
    """Everything the dashboard shows, in one request."""
    return await build_status(session, activity_limit=activity_limit)


@router.get(
    "/nav-counts", response_model=NavCountsOut, summary="Sidebar badge counts"
)
async def api_nav_counts(session: SessionDep) -> NavCountsOut:
    """The five numbers on the sidebar badges.

    Its own endpoint rather than a corner of ``/api/status`` because it is polled
    on a different clock and by a component that outlives every screen — and
    because three of the five are cheap counts while the status payload assembles
    the indexer, the rate limiter and an activity page.

    ``wanted`` here is the *monitored backlog* (``wanted`` + ``failed``), which
    is a different number from ``status.library.wanted_albums``. Both are
    correct; the badge has to match the page it links to.
    """
    return await nav_counts_out(session)


@router.get("/banners", response_model=list[BannerOut], summary="Startup warnings")
async def api_banners(settings: SettingsDep) -> list[BannerOut]:
    """Whatever is not wired up: credentials, client, app secret, breaker.

    Also carried on ``/api/status``, so a screen already polling that needs no
    second request. This exists for the screens that do not poll status — and
    because "there is nothing wrong" is an answer worth being able to ask for
    cheaply. Never raises: a broken probe is a banner that is absent, not a 500
    on every page.
    """
    return build_banners(settings)


@router.get("/meta", response_model=MetaOut, summary="Enum vocabularies and labels")
async def api_meta() -> MetaOut:
    """Every enum value and label map the client renders, in one payload.

    Cache it forever — it changes only when the server is redeployed. The point
    is that nothing downstream keeps its own copy: a hand-typed list of album
    statuses stays right until somebody adds one, and then fails silently, as a
    ``<select>`` one option short.
    """
    return build_meta()


@router.get("/settings", response_model=SettingsOut, summary="Effective configuration")
async def api_settings(settings: SettingsDep) -> SettingsOut:
    """Read-only view of the effective configuration.

    Includes ``naming_preview`` (G3) — two example paths the configured template
    would produce. An empty list is meaningful: the template could not be
    rendered, which is what a typo in ``NAMING_TEMPLATE`` looks like, and it must
    render as that rather than as an empty box.

    Nothing secret appears here or ever may: ``enrichment_contact`` is reduced to
    set/unset, and the auth token, the app secret and signed URLs are absent by
    construction.
    """
    return build_settings_out(settings)


async def update_settings(
    session: AsyncSession, payload: SettingsUpdateIn
) -> SettingsOut:
    """Write the settings overlay, then hand the new values to what is running.

    The allowlist (:data:`app.config.OVERRIDABLE_SETTINGS`) is the whole contract:
    seven keys, no credential, no path, no rate-limit figure. A key outside it is
    a **400** naming the key and listing what is writable, and so is a value the
    key cannot hold — a bad ``naming_template`` is refused *here*, by rendering
    it, rather than 500ing inside a re-file at three in the morning.

    ``reset`` deletes rows and ``values`` writes them; a key in both is a
    contradiction rather than an ordering puzzle, so it is refused too.

    Like :meth:`app.core.enricher.Enricher.identify` this **commits itself**.
    Both run on the request-scoped session from ``get_session``, which never
    commits, so the caller-commits convention would make a settings change a
    green toast and a rollback.
    """
    overridable = set(config.overridable_keys())
    clashes = sorted(set(payload.values) & set(payload.reset))
    if clashes:
        raise HTTPException(
            status_code=http_status.HTTP_400_BAD_REQUEST,
            detail=ErrorDetail(
                error=(
                    f"{clashes[0]!r} was both set and reset in one request. Send "
                    "one or the other — resetting means 'use whatever .env says'."
                ),
                code="settings_conflict",
                keys=clashes,
            ),
        )

    for key in list(payload.values) + list(payload.reset):
        if key not in overridable:
            raise HTTPException(
                status_code=http_status.HTTP_400_BAD_REQUEST,
                detail=ErrorDetail(
                    error=(
                        f"{key!r} is not an overridable setting. It is configured "
                        "in .env and read at startup."
                    ),
                    code="not_overridable",
                    key=key,
                    overridable=sorted(overridable),
                ),
            )

    parsed: dict[str, str] = {}
    for key, value in payload.values.items():
        try:
            _, stored = config.parse_override(key, value)
        except ValueError as exc:
            raise HTTPException(
                status_code=http_status.HTTP_400_BAD_REQUEST,
                detail=ErrorDetail(
                    error=f"{key} {exc}",
                    code="invalid_setting",
                    key=key,
                ),
            ) from exc
        parsed[key] = stored

    for key, stored in parsed.items():
        await settings_store.set_override(session, key, stored)
    cleared = await settings_store.clear_override(session, payload.reset)

    if parsed or cleared:
        # Named, and with the verb, because this is the only record that a value
        # differs from the file somebody will read when they go looking.
        changed = [f"{key}={parsed[key]}" for key in sorted(parsed)]
        changed += [f"{key} reset to .env" for key in sorted(payload.reset)]
        _log_activity(
            session,
            event="settings.updated",
            message="Settings changed: " + ", ".join(changed),
        )
    await session.commit()

    # Re-read rather than merge: the table is the truth, and a key that was just
    # reset has to disappear from the overlay rather than linger in a dict the
    # request happened to be holding.
    overrides = await settings_store.load_overrides(session)
    state = state_or_none()
    if state is None:
        # No running application (a script, or a test driving the routes alone).
        # The overlay still installs; there is simply nothing to re-point.
        settings = config.install_overrides(overrides)
    else:
        report = await state.apply_setting_overrides(overrides)
        settings = report["settings"]
    return build_settings_out(settings)


@router.patch(
    "/settings",
    response_model=SettingsOut,
    summary="Change an overridable setting",
)
async def api_update_settings(
    session: SessionDep, payload: SettingsUpdateIn
) -> SettingsOut:
    """Override an allowlisted setting, or reset one back to ``.env``.

    Returns the whole of ``SettingsOut``, so one round trip leaves the screen
    holding the new value, the new ``naming_preview``, the new ``origins`` and —
    the honest part — ``pending``, which names anything stored but not yet live.
    """
    return await update_settings(session, payload)


@router.get("/stats", response_model=StatsOut, summary="Compact counters")
async def api_stats(session: SessionDep) -> StatsOut:
    """Compact counters, handy for external dashboards.

    The same numbers ``/api/status`` carries, without the activity page and the
    banners — this is what a header strip polls and what a scraper reads.
    ``albums_by_status`` is zero-filled for every status, because it drives a bar
    list and a status with no releases has no row in a grouped count: the bar
    would be missing rather than empty.

    ``quality`` is the hi-res roll-up over the releases on disk. Its denominator
    is ``measured``, not ``albums`` — a release whose held format cannot be
    determined is not a release known to be lossy — and ``hires_share`` is
    ``null`` rather than ``0.0`` when nothing has been measured.
    """
    return StatsOut(
        library=await library_stats(session),
        albums_by_status=await albums_by_status(session),
        queue=await queue_stats(session),
        indexer=await indexer_status(session),
        rate_limit=rate_limit_status(),
        quality=await library_quality(session),
    )


# ---------------------------------------------------------------------------
# Artists
# ---------------------------------------------------------------------------
@router.get("/artists", response_model=ArtistListOut, summary="List followed artists")
async def api_list_artists(
    session: SessionDep,
    q: Annotated[
        str | None, EmptyAsNone, Query(description="Filter by name (substring).")
    ] = None,
    monitored: OptBoolQuery = None,
    sort: str = Query("name"),
    order: Literal["asc", "desc"] = Query("asc"),
    limit: int = Query(200, ge=1, le=1000),
    offset: int = Query(0, ge=0),
) -> ArtistListOut:
    """Page through the followed artists."""
    items, total = await list_artists(
        session,
        query=q,
        monitored=monitored,
        sort=sort,
        order=order,
        limit=limit,
        offset=offset,
    )
    return ArtistListOut(
        items=items,
        total=total,
        limit=limit,
        offset=offset,
        sort=sort,
        order=order,
        unfiltered_total=await _unfiltered_total(
            filtered=bool(q) or monitored is not None,
            total=total,
            count=lambda: list_artists(session, limit=1, with_counts=False),
        ),
    )


@router.post(
    "/artists",
    response_model=ArtistFollowOut,
    status_code=http_status.HTTP_201_CREATED,
    summary="Follow an artist",
)
async def api_create_artist(
    session: SessionDep,
    settings: SettingsDep,
    payload: ArtistCreateIn,
) -> ArtistFollowOut:
    """Follow a Qobuz artist (idempotent — following twice just updates).

    This is also the endpoint the disk-scan review list posts to when somebody
    picks the right Qobuz artist for a folder the bulk importer would not guess
    at. There is deliberately no import-specific route: the importer's own rule
    is *exact or nothing*, and a person choosing from candidates is not the
    importer being made fuzzy — it is the ordinary follow, which is why it also
    honours ``AUTO_INDEX_ON_FOLLOW`` for that one artist while a bulk import
    never indexes.

    ``created`` says which of the two sentences to show. It cannot be derived
    from the response otherwise, and it cannot be derived afterwards at all: a
    second press looks identical to the first.
    """
    artist, created = await follow_artist(session, payload, settings)
    counts = await artist_album_counts(session, [artist.id])
    bucket = counts.get(artist.id, {})
    base = await artist_out(
        session,
        artist,
        album_count=bucket.get("albums"),
        wanted_count=bucket.get("wanted"),
        downloaded_count=bucket.get("downloaded"),
    )
    return ArtistFollowOut(**base.model_dump(), created=created)


@router.get(
    "/artists/{artist_id}",
    response_model=ArtistDetailOut,
    summary="One artist with their releases",
)
async def api_get_artist(
    session: SessionDep,
    artist_id: str,
    album_limit: int = Query(500, ge=1, le=2000),
) -> ArtistDetailOut:
    """Return one followed artist plus every release Qobuzarr knows about."""
    artist = await get_artist_or_404(session, artist_id)
    albums, total = await list_albums_for_artist(session, artist.id, limit=album_limit)
    counts = await artist_album_counts(session, [artist.id])
    bucket = counts.get(artist.id, {})
    base = await artist_out(
        session,
        artist,
        album_count=bucket.get("albums", total),
        wanted_count=bucket.get("wanted"),
        downloaded_count=bucket.get("downloaded"),
    )
    return ArtistDetailOut(**base.model_dump(), albums=albums)


@router.get(
    "/artists/{artist_id}/stats",
    response_model=ArtistStatsOut,
    summary="One artist's per-status release counts",
)
async def api_artist_stats(session: SessionDep, artist_id: str) -> ArtistStatsOut:
    """The artist header's numbers, including a count for **every** album status.

    Zero-filled deliberately: these label the options of the status filter, and
    a status with no releases simply has no row in a grouped count — so the
    option would render with a hole where its number belongs.
    """
    artist = await get_artist_or_404(session, artist_id)
    return await artist_stats(session, artist)


@router.patch(
    "/artists/{artist_id}", response_model=ArtistOut, summary="Update artist settings"
)
async def api_update_artist(
    session: SessionDep, artist_id: str, payload: ArtistUpdateIn
) -> ArtistOut:
    """Change monitoring mode, quality profile or accepted release types.

    Omitted (or ``null``) fields are left alone — that is what makes this a
    partial update, and sending ``false`` to mean "no change" would unmonitor
    the artist.

    Returns the artist with the three roll-ups filled in, like every other route
    that returns one. They are not free, but a client re-rendering a row from
    this response with ``album_count`` suddenly ``null`` shows an em dash where
    a number was a moment ago, which reads as data loss rather than as an
    unpopulated optional.
    """
    artist = await get_artist_or_404(session, artist_id)
    artist = await apply_artist_update(session, artist, payload)
    counts = await artist_album_counts(session, [artist.id])
    bucket = counts.get(artist.id, {})
    return await artist_out(
        session,
        artist,
        album_count=bucket.get("albums"),
        wanted_count=bucket.get("wanted"),
        downloaded_count=bucket.get("downloaded"),
    )


@router.patch(
    "/artists/{artist_id}/tags",
    response_model=ArtistOut,
    summary="Edit an artist's name, sort name, aliases and MusicBrainz id",
)
async def api_update_artist_tags(
    session: SessionDep, artist_id: str, payload: ArtistTagsIn
) -> ArtistOut:
    """Save the identity that names this artist's folders and tags their files.

    Separate from ``PATCH /api/artists/{id}``, which is monitoring settings.
    These values change what is written to disk, so saving them and applying
    them are two presses: this one records, ``POST /api/artists/{id}/retag``
    applies. Omitted fields are left alone; an empty ``sort_name`` clears the
    override, an empty ``aliases`` list clears the aliases, an empty ``name`` or
    ``mb_artist_mbid`` is a 400, and an unknown key is a 422 rather than a
    silent discard. Aliases are recorded here and read back and go nowhere else
    — no file tag, no NFO element, no automatic match.
    """
    artist = await get_artist_or_404(session, artist_id)
    artist = await update_artist_tags(session, artist, payload)
    counts = await artist_album_counts(session, [artist.id])
    bucket = counts.get(artist.id, {})
    return await artist_out(
        session,
        artist,
        album_count=bucket.get("albums"),
        wanted_count=bucket.get("wanted"),
        downloaded_count=bucket.get("downloaded"),
    )


@router.get(
    "/artists/{artist_id}/credits",
    response_model=ArtistCreditsOut,
    summary="Which credited people share this Qobuz artist id",
)
async def api_get_artist_credits(session: SessionDep, artist_id: str) -> ArtistCreditsOut:
    """The credits already read for this artist, clustered. Makes no request.

    ``analysed < total`` means the catalogue has not been read yet — POST to
    the same path to read more.
    """
    artist = await get_artist_or_404(session, artist_id)
    albums = list(
        (
            await session.execute(select(Album).where(Album.artist_id == str(artist.id)))
        )
        .unique()
        .scalars()
        .all()
    )
    return build_artist_credits(artist, albums)


@router.post(
    "/artists/{artist_id}/credits",
    response_model=ArtistCreditsOut,
    summary="Read track credits for this artist's releases",
)
async def api_analyse_artist_credits(
    session: SessionDep,
    artist_id: str,
    limit: int = Query(25, ge=1, le=200, description="Releases to read this press"),
) -> ArtistCreditsOut:
    """Read the ``performers`` credits off releases nobody has looked at yet.

    One ``album/get`` per release, so it is bounded per press and repeated
    until ``analysed`` reaches ``total``. It writes only ``credit_names``:
    nothing here changes a status, queues anything, or touches a file.
    """
    artist = await get_artist_or_404(session, artist_id)
    return await analyse_artist_credits(session, artist, limit=limit)


@router.put(
    "/artists/{artist_id}/credit-filter",
    response_model=MessageOut,
    summary="Accept only releases credited to these people",
)
async def api_set_artist_credit_filter(
    session: SessionDep, artist_id: str, payload: ArtistCreditFilterIn
) -> MessageOut:
    """Narrow a shared Qobuz artist id to the person you actually follow.

    ``PUT`` and not ``PATCH``: the list is replaced wholesale, because that is
    what the screen shows — every credit with a tick beside it — and a partial
    update of a set nobody can see the whole of is how ticks go missing.
    An **empty list clears the filter**.

    Releases already on file move with it, through the same
    ``apply_monitoring_to_backlog`` the monitoring settings use, and the counts
    come back on ``detail``. Nothing is queued and no file is touched.
    """
    artist = await get_artist_or_404(session, artist_id)
    change = await set_artist_credit_filter(session, artist, payload)
    accepted = artist.credit_filter_list
    parts: list[str] = []
    if change.demoted:
        parts.append(f"{change.demoted} release(s) no longer wanted")
    if change.restored:
        parts.append(f"{change.restored} release(s) wanted again")
    summary = (
        f"Accepting {len(accepted)} credit(s) as {artist.name}"
        if accepted
        else f"Credit filter cleared for {artist.name}"
    )
    return MessageOut(
        message=summary + (" — " + ", ".join(parts) if parts else ""),
        detail={
            "demoted": change.demoted,
            "restored": change.restored,
            "accepted": accepted,
        },
    )


@router.post(
    "/artists/{artist_id}/retag",
    response_model=LibraryTidyOut,
    summary="Re-tag every release of one artist that is on disk",
)
async def api_retag_artist(
    session: SessionDep,
    artist_id: str,
    refile: bool = Query(False, description="Also rename folders to match the template"),
    limit: int = Query(2000, ge=1, le=10000),
) -> LibraryTidyOut:
    """Write this artist's stored identity into their files, and their NFOs.

    ``refile=true`` additionally renames folders to match the naming template.
    It is opt-in because a tag fix and a thousand-file move are different-sized
    decisions, and it refuses any release whose path is frozen. Local only: no
    Qobuz call, nothing queued, safe to run again.
    """
    artist = await get_artist_or_404(session, artist_id)
    return await retag_artist(session, artist, refile=refile, limit=limit)


@router.delete(
    "/artists/{artist_id}", response_model=MessageOut, summary="Unfollow an artist"
)
async def api_delete_artist(session: SessionDep, artist_id: str) -> MessageOut:
    """Unfollow an artist and delete their albums, tracks and queue entries."""
    artist = await get_artist_or_404(session, artist_id)
    message = await delete_artist(session, artist)
    return MessageOut(ok=True, message=message, level="warning")


@router.post(
    "/artists/{artist_id}/scan", response_model=MessageOut, summary="Scan one artist"
)
async def api_scan_artist(session: SessionDep, artist_id: str) -> MessageOut:
    """Ask the indexer to re-check one artist as soon as it can."""
    artist = await get_artist_or_404(session, artist_id)
    return MessageOut(ok=True, message=await scan_artist(session, artist))


@router.post(
    "/artists/{artist_id}/download-wanted",
    response_model=MessageOut,
    summary="Queue every wanted release of one artist",
)
async def api_download_wanted(session: SessionDep, artist_id: str) -> MessageOut:
    """Explicitly start downloads for everything this artist has marked wanted.

    This is the opt-in counterpart to ``AUTO_DOWNLOAD``: it queues regardless of
    that setting, because the request is a direct instruction from the user.
    """
    artist = await get_artist_or_404(session, artist_id)
    return MessageOut(ok=True, message=await queue_wanted_for_artist(session, artist))


@router.get(
    "/artists/{artist_id}/albums",
    response_model=AlbumListOut,
    summary="Releases of one artist",
)
async def api_artist_albums(
    session: SessionDep,
    artist_id: str,
    album_status: Annotated[
        AlbumStatus | None, EmptyAsNone, Query(alias="status")
    ] = None,
    release_type: OptStrQuery = None,
    q: OptStrQuery = None,
    limit: int = Query(500, ge=1, le=2000),
    offset: int = Query(0, ge=0),
) -> AlbumListOut:
    """Page through one artist's releases. ``q`` matches title, version or label.

    ``unfiltered_total`` counts the same artist's releases with ``status``,
    ``release_type`` and ``q`` dropped — the artist is the *subject* of this
    query, not a filter on it, so it stays applied. That is what lets the empty
    state say "no release matches that" rather than "this artist has no
    releases", which are different sentences with different next steps.
    """
    await get_artist_or_404(session, artist_id)
    items, total = await list_albums_for_artist(
        session,
        artist_id,
        status=album_status,
        release_type=release_type,
        query=q,
        limit=limit,
        offset=offset,
    )
    return AlbumListOut(
        items=items,
        total=total,
        limit=limit,
        offset=offset,
        unfiltered_total=await _unfiltered_total(
            filtered=bool(album_status or release_type or q),
            total=total,
            count=lambda: list_albums_for_artist(session, artist_id, limit=1),
        ),
    )


@router.post(
    "/artists/bulk",
    response_model=MessageOut,
    summary="Change monitoring settings for many artists at once",
)
async def api_bulk_update_artists(
    session: SessionDep, payload: Annotated[ArtistBulkUpdateIn, Body()]
) -> MessageOut:
    """Apply monitoring changes to a list of artists. Omitted fields are left alone.

    ``level`` is ``warning`` whenever the edit left an artist accepting no release
    types at all: it is a legal thing to ask for and almost never what somebody
    means, and it is silent — that artist simply stops marking anything wanted.
    """
    message, detail = await bulk_update_artists(session, payload)
    if detail.get("emptied"):
        level: Any = "warning"
    elif detail.get("updated"):
        level = "success"
    else:
        level = "info"
    return MessageOut(
        ok=bool(detail.get("updated")),
        message=message,
        level=level,
        detail=detail,
    )


@router.post("/scan-all", response_model=MessageOut, summary="Sweep every artist")
async def api_scan_all(session: SessionDep) -> MessageOut:
    """Request a full (slow) sweep of every monitored artist."""
    return MessageOut(ok=True, message=await scan_all(session))


# ---------------------------------------------------------------------------
# Library management (changing what is on disk)
# ---------------------------------------------------------------------------
@router.delete(
    "/albums/{album_id}/files",
    response_model=MessageOut,
    summary="Move a release's files to the trash",
)
async def api_delete_album_files(session: SessionDep, album_id: str) -> MessageOut:
    """Delete one release from the library folder.

    The files go to ``Settings.trash_dir``, not to ``/dev/null`` — recover them
    with ``POST /api/library/trash/{entry_id}/restore`` until the trash is
    emptied. The album row survives and goes back to wanted (or skipped, if it
    is not monitored); nothing is re-queued.
    """
    album = await get_album_or_404(session, album_id)
    result = await delete_album_files(session, album)
    return MessageOut(
        ok=True,
        # A warning, not a success: this moved somebody's music out of the
        # library. It is recoverable, and the message says where to, which is the
        # half of the sentence that has to survive being read in a hurry.
        message=f"Deleted {album.display_title}: {result.summary}.",
        level="warning",
        detail={
            "album_id": album.id,
            "trash_id": result.trashed.id if result.trashed else None,
            "status": result.new_status.value if result.new_status else None,
            "tracks_cleared": result.tracks_cleared,
        },
    )


@router.post(
    "/albums/{album_id}/refile",
    response_model=RefilePlanOut,
    summary="Move a release to where the naming template says it belongs",
)
async def api_refile_album(
    session: SessionDep,
    album_id: str,
    dry_run: bool = Query(False, description="Report the plan without moving anything"),
) -> RefilePlanOut:
    """Re-file one release. The whole folder moves, cover art included."""
    album = await get_album_or_404(session, album_id)
    return _plan_out(await refile_album(session, album, apply=not dry_run))


@router.post(
    "/albums/{album_id}/retag",
    response_model=MessageOut,
    summary="Rewrite a release's tags from the catalogue metadata",
)
async def api_retag_album(session: SessionDep, album_id: str) -> MessageOut:
    """Re-tag one release in place. Local only — no Qobuz call, no re-download."""
    album = await get_album_or_404(session, album_id)
    result = await retag_album(session, album)
    return MessageOut(
        ok=result.failed == 0,
        message=f"Re-tagged {album.display_title}: {result.summary}.",
        level="warning" if result.failed else "success",
        detail={"album_id": album.id, "tagged": result.tagged, "failed": result.failed},
    )


@router.post(
    "/albums/{album_id}/reidentify",
    response_model=MessageOut,
    summary="Fingerprint this release again and re-identify it",
)
async def api_reidentify_album(session: SessionDep, album_id: str) -> MessageOut:
    """Re-open every album enrichment rung for one release and drain it now.

    AcoustID fingerprints the files again as the first rung of that pass. A
    release that is not on disk is a 400: there is nothing to fingerprint.
    """
    album = await get_album_or_404(session, album_id)
    return await reidentify_album(session, album)


@router.post(
    "/library/refile",
    response_model=LibraryTidyOut,
    summary="Re-file every release that is not where the template says",
)
async def api_refile_library(
    session: SessionDep,
    artist_id: OptStrQuery = None,
    apply: bool = Query(False, description="Actually move the files"),
    limit: int = Query(2000, ge=1, le=10000),
) -> LibraryTidyOut:
    """Bring the library in line with ``NAMING_TEMPLATE``.

    Previews by default: this walks every downloaded release and moves folders,
    so ``apply=true`` has to be asked for. Albums that cannot be re-filed are
    listed with the reason rather than passed over.
    """
    return await refile_library(session, artist_id=artist_id, apply=apply, limit=limit)


@router.get(
    "/library/refile/estimate",
    response_model=RefileEstimateOut,
    summary="Count the releases a naming template would re-file",
)
async def api_estimate_refile(
    session: SessionDep,
    template: OptStrQuery = None,
    artist_id: OptStrQuery = None,
    limit: int = Query(2000, ge=1, le=10000),
) -> RefileEstimateOut:
    """Answer "how much would this template disturb?" without disturbing it.

    A **GET** on purpose: there is no ``apply`` parameter and there cannot be
    one, so the method itself is the guarantee. ``template`` is the candidate
    somebody is typing; omitting it counts against the template in force.

    Cost is real — it walks every downloaded release, hashes the files behind
    the staleness gate and re-reads the files of releases with no track rows —
    so it is asked for by a press and never on a timer.
    """
    return await estimate_refile(
        session, template=template, artist_id=artist_id, limit=limit
    )


@router.post(
    "/library/retag",
    response_model=LibraryTidyOut,
    summary="Rewrite tags across the library",
)
async def api_retag_library(
    session: SessionDep,
    artist_id: OptStrQuery = None,
    limit: int = Query(2000, ge=1, le=10000),
) -> LibraryTidyOut:
    """Re-tag every downloaded release, or one artist's. No Qobuz calls."""
    return await retag_library(session, artist_id=artist_id, limit=limit)


@router.post(
    "/library/nfo",
    response_model=LibraryTidyOut,
    summary="Write NFO files across the library",
)
async def api_write_library_nfo(
    session: SessionDep,
    artist_id: OptStrQuery = None,
    limit: int = Query(5000, ge=1, le=20000),
) -> LibraryTidyOut:
    """Describe every release on disk for a media server. No Qobuz calls."""
    return await write_nfo_library(session, artist_id=artist_id, limit=limit)


@router.post(
    "/albums/{album_id}/nfo",
    response_model=MessageOut,
    summary="Write NFO files for one release",
)
async def api_write_album_nfo(session: SessionDep, album_id: str) -> MessageOut:
    """Write ``album.nfo`` beside one release, and ``artist.nfo`` above it."""
    album = await get_album_or_404(session, album_id)
    try:
        outcome = await librarian.write_nfo(session, album)
    except librarian.LibraryError as exc:
        raise _library_http_error(exc) from exc
    return MessageOut(
        ok=True,
        message=f"{album.display_title}: {outcome.summary}",
        level="warning" if outcome.skipped else "success",
    )


@router.get(
    "/enrichment", response_model=EnrichmentStatusOut, summary="Enrichment coverage"
)
async def api_enrichment_status(session: SessionDep) -> EnrichmentStatusOut:
    """How much of the library the open sources have identified, and what is stuck."""
    return await enrichment_status(session)


@router.get(
    "/enrichment/review",
    response_model=EnrichmentReviewListOut,
    summary="What needs a human",
)
async def api_enrichment_review(
    session: SessionDep,
    state: OptStrQuery = None,
    source: OptStrQuery = None,
    limit: int = Query(200, ge=1, le=1000),
    offset: int = Query(0, ge=0),
) -> EnrichmentReviewListOut:
    """Entities the matchers refused to guess at.

    ``unfiltered_total`` drops ``state`` and ``source`` but **not** the default
    ``{ambiguous, no_key}`` pair, nor ``library_scope``: those are what this list
    *is*. ``not_found`` deliberately never appears — the upstream has no such
    record yet, which is waiting rather than deciding, and nothing is gained by
    showing a person a row they cannot act on.
    """
    items, total = await enrichment_review(
        session, state=state, source=source, limit=limit, offset=offset
    )
    return EnrichmentReviewListOut(
        items=items,
        total=total,
        limit=limit,
        offset=offset,
        unfiltered_total=await _unfiltered_total(
            filtered=bool(state or source),
            total=total,
            count=lambda: enrichment_review(session, limit=1),
        ),
    )


@router.post(
    "/enrichment/run", response_model=MessageOut, summary="Run enrichment now"
)
async def api_enrichment_run(limit: int | None = Query(None, ge=1, le=500)) -> MessageOut:
    """Drain one batch immediately. No Qobuz calls, and nothing is queued."""
    return MessageOut(ok=True, message=await run_enrichment_now(limit))


@router.get(
    "/enrichment/{entity_type}/{entity_id}/candidates",
    response_model=EnrichmentCandidatesOut,
    summary="Records a person could pick from",
)
async def api_enrichment_candidates(
    session: SessionDep,
    entity_type: str,
    entity_id: str,
    source: str = Query(..., description="musicbrainz or deezer"),
    q: OptStrQuery = None,
) -> EnrichmentCandidatesOut:
    """Search an upstream on someone's behalf so they can identify a record.

    The only search in the enrichment code. Its results are never resolved
    automatically — see ``app.enrich.types.Candidate``.
    """
    return await enrichment_candidates(session, entity_type, entity_id, source, q)


@router.post(
    "/enrichment/{entity_type}/{entity_id}/identify",
    response_model=MessageOut,
    summary="Identify something by hand",
)
async def api_enrichment_identify(
    session: SessionDep,
    entity_type: str,
    entity_id: str,
    payload: EnrichmentIdentifyIn,
) -> MessageOut:
    """Say which upstream record this is. The escape hatch from the review list."""
    return MessageOut(
        ok=True, message=await identify_entity(session, entity_type, entity_id, payload)
    )


@router.post(
    "/enrichment/{entity_type}/{entity_id}/reject",
    response_model=MessageOut,
    summary="Say there is no answer here",
)
async def api_enrichment_reject(
    session: SessionDep,
    entity_type: str,
    entity_id: str,
    payload: EnrichmentRejectIn,
) -> MessageOut:
    """Dismiss one work item. The other way off the review list.

    Reversible by ``POST .../reopen``: nothing re-arms a rejection, so that is
    the only route back and it is deliberately one press away.
    """
    return MessageOut(
        ok=True,
        message=await reject_enrichment(session, entity_type, entity_id, payload),
        level="success",
    )


@router.post(
    "/enrichment/{entity_type}/{entity_id}/accept",
    response_model=EnrichmentAcceptOut,
    summary="Apply the proposal this entity carries",
)
async def api_enrichment_accept(
    session: SessionDep, entity_type: str, entity_id: str
) -> EnrichmentAcceptOut:
    """Apply a held proposal, or answer that there is none and the picker is next.

    Takes no body on purpose: there is nothing to choose. It applies what the
    system already decided, and it never searches — see
    :func:`accept_enrichment`.
    """
    return await accept_enrichment(session, entity_type, entity_id)


@router.post(
    "/enrichment/{entity_type}/{entity_id}/reopen",
    response_model=MessageOut,
    summary="Put it back on the work list",
)
async def api_enrichment_reopen(
    session: SessionDep,
    entity_type: str,
    entity_id: str,
    source: OptStrQuery = None,
) -> MessageOut:
    """Run this again, whatever it last answered. Un-rejects a rejected row."""
    return MessageOut(
        ok=True,
        message=await reopen_enrichment(session, entity_type, entity_id, source),
        level="success",
    )


@router.get("/library/trash", response_model=TrashOut, summary="What is recoverable")
async def api_trash(settings: SettingsDep) -> TrashOut:
    """Everything Qobuzarr has taken out of the library and not yet destroyed."""
    return await trash_contents(settings)


@router.post(
    "/library/trash/{entry_id}/restore",
    response_model=MessageOut,
    summary="Put a trashed batch back",
)
async def api_restore_trash(entry_id: str, settings: SettingsDep) -> MessageOut:
    """Restore one batch to its original path.

    Refused when something is there again — a half-overwritten album is worse
    than a failed restore. The database is not updated; run a disk scan to have
    the files adopted again.
    """
    restored = await restore_trash_entry(entry_id, settings)
    return MessageOut(
        ok=True,
        message=f"Restored to {restored}. Run a disk scan to adopt it again.",
        level="success",
        detail={"path": restored},
    )


@router.delete(
    "/library/trash",
    response_model=MessageOut,
    summary="Destroy trashed files permanently",
)
async def api_empty_trash(
    settings: SettingsDep,
    entry_id: str | None = Query(None, description="One batch; omit for all of them"),
) -> MessageOut:
    """Empty the trash. **This is the one thing here that cannot be undone.**"""
    removed, freed = await empty_trash(entry_id, settings)
    return MessageOut(
        ok=True,
        message=f"Permanently deleted {removed} item(s), freeing {freed / 1048576:.1f} MiB.",
        # The one irreversible action in Qobuzarr, and the only one whose
        # acknowledgement is a warning about something that already succeeded.
        level="warning",
        detail={"removed": removed, "bytes": freed},
    )


# ---------------------------------------------------------------------------
# Library scanning (the disk, not Qobuz)
# ---------------------------------------------------------------------------
def _scan_out(payload: dict[str, Any]) -> LibraryScanOut:
    """One scan report as a model, with its severity decided here.

    The four integrity counters (``files_measured`` / ``files_retagged`` /
    ``files_replaced`` / ``albums_reopened``) come straight off
    :meth:`app.core.scanner.ScanResult.as_dict` and are simply declared on the
    model now. They were being computed and then dropped on serialisation, which
    made ``replaced`` — *different audio under the same filename* — a verdict no
    client could ever see, on the one screen whose job is to report it.

    ``level`` is ``warning`` when the scan could not read something, or when it
    found audio that has changed underneath the database. Both are things
    somebody has to look at; everything else about a scan is bookkeeping.
    """
    out = LibraryScanOut.model_validate(payload)
    out.level = "warning" if (out.errors or out.files_replaced) else "info"
    return out


@router.get(
    "/library/scan",
    response_model=LibraryScanStatusOut,
    summary="Result of the most recent disk scan",
)
async def api_library_scan_status(
    session: SessionDep, settings: SettingsDep
) -> LibraryScanStatusOut:
    """Whether a disk scan is running, plus the summary of the last one."""
    return LibraryScanStatusOut.model_validate(
        await library_scan_status(session, settings)
    )


@router.post(
    "/library/scan",
    response_model=LibraryScanOut,
    summary="Scan the library folder and adopt what is already there",
)
async def api_library_scan(
    session: SessionDep,
    artist_id: OptStrQuery = None,
    dry_run: bool = Query(False, description="Build the report without writing anything"),
) -> LibraryScanOut:
    """Reconcile the library folder with the database.

    Reads tags off every audio file under ``LIBRARY_PATH``, matches each album
    folder against the database and marks the complete ones ``downloaded`` so
    they stop being wanted. No Qobuz API call is made and no file is modified;
    pass ``dry_run=true`` to see what a scan would change first.
    """
    return _scan_out(
        await scan_library(session, artist_id=artist_id, apply=not dry_run)
    )


@router.post(
    "/artists/{artist_id}/library-scan",
    response_model=LibraryScanOut,
    summary="Scan one artist's folder",
)
async def api_artist_library_scan(
    session: SessionDep,
    artist_id: str,
    dry_run: bool = Query(False, description="Build the report without writing anything"),
) -> LibraryScanOut:
    """Disk scan restricted to one artist. ``artist_id`` must already be followed."""
    return _scan_out(
        await scan_library(session, artist_id=str(artist_id), apply=not dry_run)
    )


@router.get(
    "/library/import",
    response_model=LibraryImportOut,
    summary="Progress of the artist import",
)
async def api_library_import_status(session: SessionDep) -> LibraryImportOut:
    """Live progress while an import runs, the last run's summary when idle."""
    return LibraryImportOut.model_validate(await library_import_status(session))


@router.get(
    "/library/import/preview",
    response_model=LibraryImportPreviewOut,
    summary="Who an import would look up, and how long it would take",
)
async def api_library_import_preview(session: SessionDep) -> LibraryImportPreviewOut:
    """Dry disk scan listing every artist on disk that is not followed yet.

    Local only — no Qobuz call — which is what makes it safe to render before
    asking somebody to commit to a run of one rate-limited search per artist.
    """
    return LibraryImportPreviewOut.model_validate(
        await preview_library_import(session)
    )


@router.post(
    "/library/import",
    response_model=LibraryImportOut,
    status_code=http_status.HTTP_202_ACCEPTED,
    summary="Look up the artists found on disk and follow the certain ones",
)
async def api_library_import(
    payload: Annotated[LibraryImportStartIn, Body()] = LibraryImportStartIn(),
) -> LibraryImportOut:
    """Start a background import: one Qobuz search per artist, exact names only.

    Returns immediately with the initial progress snapshot; poll
    ``GET /api/library/import`` for the rest. Artists are followed but **not**
    indexed unless ``index_now`` is set — the scheduled indexer works through
    them at its normal one-per-tick pace — and nothing is ever downloaded.
    """
    return LibraryImportOut.model_validate(await start_library_import(payload))


@router.post(
    "/library/import/cancel",
    response_model=MessageOut,
    summary="Stop a running artist import",
)
async def api_cancel_library_import() -> MessageOut:
    """Stop after the artist currently being searched for."""
    stopped = await cancel_library_import()
    return MessageOut(
        ok=stopped,
        message=(
            "Stopping the import after the current artist."
            if stopped
            else "No import is running."
        ),
    )


# ---------------------------------------------------------------------------
# Integrity: what is actually on disk
# ---------------------------------------------------------------------------
async def run_integrity_verify(
    *, limit: int | None = None, unknown_only: bool = False, apply: bool = True
) -> Any:
    """Measure files against the baseline recorded for them, right now.

    A thin wrapper over :func:`app.core.scheduler.verify_integrity`, which is the
    same pass the nightly rotation and ``cli.py verify-library`` run — one
    implementation, three callers, because a hand-run verification that disagreed
    with the nightly one about what "changed" means would be worse than having
    neither.

    Three things this deliberately does **not** do. It does not queue anything:
    a file whose audio was replaced re-opens its release for *enrichment*, and
    filling a gap stays an explicit user action. It does not change any album's
    status — demotion is ``_verify_library``'s job, at the album level, where it
    reverses when a mount comes back. And it does not run on the request's
    session: the pass opens and closes its own short ones around minutes of
    hashing, because SQLite has a single writer and holding it that long is how a
    perfectly good download gets recorded as failed.

    The report is stored on the way out (unless this was a dry run, which by
    definition wrote nothing and should not overwrite the record of a run that
    did), so ``GET /api/integrity`` shows it after a restart.

    The gate reads the **effective** settings, not the environment.
    ``integrity_enabled`` is on the settings overlay's allowlist, so the switch on
    the Rules screen is the more recent answer; reading ``get_settings()`` here
    made this the one place the switch did not reach — it 503'd with "edit .env
    and restart" at somebody who had just turned it on in the UI, and it ran a
    full pass for somebody who had just turned it off, while ``GET /api/integrity``
    and the nightly rotation both agreed it was off.

    Raises:
        HTTPException: 503 when integrity checking is switched off, 409 when a
            pass is already running.
    """
    from app.core.scheduler import (  # noqa: PLC0415 - keeps app.api import-light
        IntegrityBusyError,
        store_last_integrity,
        verify_integrity,
    )

    settings = config.effective_for(get_settings())
    if not settings.integrity_enabled:
        origin = config.setting_origins().get("integrity_enabled", "env")
        raise HTTPException(
            status_code=http_status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=(
                "Integrity checking is switched off on the Rules screen. Switch "
                "it back on there."
                if origin == "override"
                else "Integrity checking is switched off. Set INTEGRITY_ENABLED="
                "true in .env, or switch it on from the Rules screen."
            ),
        )
    try:
        report = await verify_integrity(
            session_scope, limit=limit, unknown_only=unknown_only, apply=apply
        )
    except IntegrityBusyError as exc:
        raise HTTPException(
            status_code=http_status.HTTP_409_CONFLICT, detail=str(exc)
        ) from exc

    if apply:
        try:
            async with session_scope() as session:
                await store_last_integrity(session, report)
        except Exception as exc:  # noqa: BLE001 - the measurements are committed
            logger.warning("Could not store the integrity summary: %s", exc)
    return report


async def quarantine_corrupt(
    session: AsyncSession, *, limit: int = 200
) -> librarian.QuarantineResult:
    """Move unplayable files to the trash and mark them missing again.

    Straight through :mod:`app.core.librarian`, which is the only module allowed
    to touch files already in ``LIBRARY_PATH`` and never unlinks one — the four
    gates and the trash copy apply here exactly as they do to a delete, and they
    matter more here than anywhere else, because "this file is broken" is the
    kind of verdict that has to be recoverable when it is wrong.

    The track goes back to ``pending`` and the release to ``wanted``, which is
    what makes the gap visible. **Nothing is queued.** Albums the librarian
    refused — a download is writing into that folder, or the file has been
    replaced since the verdict was recorded — are reported, never skipped
    silently.
    """
    try:
        return await librarian.quarantine_corrupt_files(session, limit=limit)
    except librarian.LibraryError as exc:
        await session.rollback()
        raise _library_http_error(exc) from exc


@router.get(
    "/integrity", response_model=IntegrityStatusOut, summary="What is on disk"
)
async def api_integrity(session: SessionDep, settings: SettingsDep) -> IntegrityStatusOut:
    """The recorded state of the library's files. Nothing is measured here.

    ``states`` is what the rows *record*, so it holds only ``unknown``,
    ``verified`` and ``missing``: a pass re-baselines whatever it classifies, so
    no row is ever left recording that it disagrees with its own file.
    ``retagged`` and ``replaced`` are verdicts about a *run* and live on
    ``last_result``.

    ``unknown`` means nothing has ever baselined that file. It is a first-class
    answer and not an accusation — treating it as change is what would make this
    whole feature cry wolf on the day it shipped, with every file in the library
    reporting as tampered with and the one real edit invisible among them.
    """
    return await integrity_status(session, settings)


@router.post(
    "/integrity/verify",
    response_model=IntegrityRunOut,
    summary="Hash files and compare them with their baseline",
)
async def api_integrity_verify(
    limit: int | None = Query(
        None, ge=1, le=100000, description="Stop after this many files."
    ),
    unknown_only: bool = Query(
        False, description="Only files with no recorded hash — 'baseline the library'."
    ),
    apply: bool = Query(True, description="Write what was measured."),
) -> IntegrityRunOut:
    """Run a verification pass now.

    ``unknown_only=true`` is what *Baseline library* means, and it converges:
    every row it writes leaves the candidate set, and it can never overwrite a
    measurement somebody else made. ``apply=false`` measures and classifies but
    writes nothing at all — not the stamps, not the release digests, not the
    re-identification marks.

    Runs inline and returns the report, because that is what the caller wants to
    look at; ``limit`` is how a first press is kept to something that finishes.
    At roughly 201 ms a file, an unbounded pass over a large library is an hour
    of disk.

    **It queues nothing and changes no album's status.**
    """
    report = await run_integrity_verify(
        limit=limit, unknown_only=unknown_only, apply=apply
    )
    summary = report.as_dict()
    return IntegrityRunOut(
        ok=True,
        message=(
            f"Checked {report.checked} file(s): {report.baselined} baselined, "
            f"{summary.get('retagged', 0)} re-tagged, "
            f"{summary.get('replaced', 0)} replaced, "
            f"{report.albums_reopened} release(s) re-opened for identification"
            + ("" if report.applied else " (nothing was written)")
        ),
        # Replaced audio is the one verdict somebody has to act on; everything
        # else a pass reports is bookkeeping.
        level="warning" if summary.get("replaced") else "success",
        report=integrity_report_out(summary) or IntegrityReportOut(),
    )


@router.get(
    "/integrity/corrupt",
    response_model=CorruptListOut,
    summary="Files that will not decode",
)
async def api_integrity_corrupt(
    session: SessionDep,
    limit: int = Query(200, ge=1, le=2000),
    offset: int = Query(0, ge=0),
) -> CorruptListOut:
    """Everything currently recorded as unplayable, oldest verdict first.

    ``corrupt`` here is a strong claim: ``fpcalc`` ran, finished, and the audio
    would not decode, which no music player could either. The three failures that
    are *not* this — the tool being absent, the decode timing out, the path not
    resolving — are facts about the machine or the mount and never reach this
    list, because acting on one of them moves a healthy album into the trash
    while nobody is watching.

    This exists so the quarantine button is never pressed blind: the files it
    would move are named first, and this list and that action select on the same
    condition.
    """
    items, total = await list_corrupt_tracks(session, limit=limit, offset=offset)
    return CorruptListOut(items=items, total=total, limit=limit, offset=offset)


@router.post(
    "/library/quarantine",
    response_model=MessageOut,
    summary="Move unplayable files to the trash",
)
async def api_quarantine(
    session: SessionDep, limit: int = Query(200, ge=1, le=2000)
) -> MessageOut:
    """Trash the files ``fpcalc`` could not decode and mark them missing again.

    Through :mod:`app.core.librarian` and its four gates, to the trash — never
    ``os.remove`` — so a wrong verdict is recoverable. The track returns to
    ``pending``, the release to ``wanted``, and **nothing is queued**: filling
    the gap stays an explicit user action, like every other path that could start
    a download.

    ``detail.blocked`` is what the librarian refused and why. A busy album (a
    download is writing into that folder) and a stale verdict (the file has been
    replaced since it was fingerprinted) are both reasons to come back later, not
    failures.
    """
    result = await quarantine_corrupt(session, limit=limit)
    return MessageOut(
        ok=True,
        message=result.summary,
        level="warning" if result.files_trashed or result.errors else "info",
        detail={
            "quarantined": result.files_trashed,
            "tracks_cleared": result.tracks_cleared,
            "albums_checked": result.albums_checked,
            "albums_flagged": result.albums_flagged,
            "blocked": list(result.errors),
        },
    )


# ---------------------------------------------------------------------------
# Albums
# ---------------------------------------------------------------------------
@router.get("/wanted", response_model=WantedListOut, summary="The missing-album backlog")
async def api_wanted(
    session: SessionDep,
    q: OptStrQuery = None,
    album_status: Annotated[
        AlbumStatus | None, EmptyAsNone, Query(alias="status")
    ] = None,
    monitored: OptBoolQuery = True,
    limit: int = Query(500, ge=1, le=2000),
    offset: int = Query(0, ge=0),
) -> WantedListOut:
    """Releases that are wanted (or failed) and not yet on disk, oldest first.

    Carries two counts beyond ``total``, and neither is derivable from it.
    ``queueable_total`` is what *Download all* would really queue: monitored and
    ``wanted``, whatever this list has been filtered to, because the bulk action
    ignores the filters. ``unfiltered_total`` is the backlog with the filters
    dropped, so "nothing matches" and "nothing is missing" are distinguishable.
    """
    items, total = await list_wanted_albums(
        session,
        query=q,
        status=album_status,
        monitored=monitored,
        limit=limit,
        offset=offset,
    )
    # Counted with its own query, from the same statuses ``queue_all_wanted``
    # selects on — deliberately not narrowed by anything above, because the
    # button it labels is not narrowed by anything above either.
    _queueable, queueable_total = await list_wanted_albums(
        session, status=AlbumStatus.WANTED, monitored=True, limit=1
    )
    return WantedListOut(
        items=items,
        total=total,
        limit=limit,
        offset=offset,
        queueable_total=queueable_total,
        unfiltered_total=await _unfiltered_total(
            # ``monitored`` defaults to True, so the default view *is* filtered.
            filtered=bool(q or album_status) or monitored is not None,
            total=total,
            count=lambda: list_wanted_albums(session, monitored=None, limit=1),
        ),
    )


@router.post(
    "/wanted/download",
    response_model=MessageOut,
    summary="Queue the whole wanted backlog",
)
async def api_download_all_wanted(
    session: SessionDep, limit: int = Query(500, ge=1, le=2000)
) -> MessageOut:
    """Queue every monitored ``wanted`` release. An explicit action, never automatic."""
    return MessageOut(ok=True, message=await queue_all_wanted(session, limit=limit))


@router.get(
    "/releases/recent",
    response_model=AlbumListOut,
    summary="Releases by release date, newest first",
)
async def api_recent_releases(
    session: SessionDep,
    monitored: OptBoolQuery = None,
    limit: int = Query(25, ge=1, le=200),
    offset: int = Query(0, ge=0),
) -> AlbumListOut:
    """What came out most recently, whatever Qobuzarr has or has not done with it.

    Ordered on ``Album.release_date``, which is the only ordering on this API
    that describes the *music* rather than this program's activity. Releases
    with no date are excluded rather than sorted to the end, and dates in the
    future are kept and sort first — that is the *upcoming* half of the Release
    radar, and Qobuz really does date announced records ahead.

    Addressed ``/releases/recent`` rather than ``/albums/recent`` on purpose:
    ``/albums/{album_id}`` matches any single segment, so an ``/albums/recent``
    would be a literal path competing with a parameter for the same shape, and
    which one won would depend on the order the routes happen to be registered
    in. A separate noun cannot be shadowed by a mistake somebody makes later.
    """
    items, total = await list_recent_releases(
        session, monitored=monitored, limit=limit, offset=offset
    )
    return AlbumListOut(
        items=items,
        total=total,
        limit=limit,
        offset=offset,
        unfiltered_total=await _unfiltered_total(
            filtered=monitored is not None,
            total=total,
            count=lambda: list_recent_releases(session, limit=1),
        ),
    )


@router.get("/albums/{album_id}", response_model=AlbumOut, summary="One album")
async def api_get_album(session: SessionDep, album_id: str) -> AlbumOut:
    """Return one album with its track list. ``album_id`` is a string, not an int."""
    album = await get_album_or_404(session, album_id)
    return await album_out(session, album, include_tracks=True)


@router.get(
    "/albums/{album_id}/detail",
    response_model=AlbumDetailOut,
    summary="One release, its other editions and the vote record",
)
async def api_album_detail(session: SessionDep, album_id: str) -> AlbumDetailOut:
    """Everything the release screen shows, in one request.

    Bundled rather than composed client-side because two of the three parts
    cannot be built from the album: the edition list needs the whole-catalogue
    keying pass (see ``GET /api/release-groups/{key}`` for why that is
    asymmetric and server-side), and the consensus is a column nothing else
    returns. ``GET /api/albums/{id}`` stays as it is — it is what a mutation
    refetches.
    """
    album = await get_album_or_404(session, album_id)
    return await album_detail(session, album)


@router.get(
    "/albums/{album_id}/consensus",
    response_model=dict[str, ConsensusFieldOut],
    summary="What each source said about this release",
)
async def api_album_consensus(
    session: SessionDep, album_id: str
) -> dict[str, ConsensusFieldOut]:
    """The per-field vote record, keyed by field name.

    Empty when nothing has voted yet — which is most of a catalogue, since only
    releases on disk are ever enriched. A field whose ``value`` is ``None`` was
    voted on and *not* decided: the sources disagreed, nothing was written, and
    that is the case worth showing rather than the unanimous ones.
    """
    await get_album_or_404(session, album_id)
    return await album_consensus(session, album_id)


@router.get(
    "/release-groups/{key}",
    response_model=ReleaseGroupOut,
    summary="Every edition of one record",
)
async def api_release_group(
    session: SessionDep, key: str, artist_id: OptStrQuery = None
) -> ReleaseGroupOut:
    """Resolve *key* to a record and return its editions, best first.

    ``key`` may be either a MusicBrainz release-group MBID or a normalised
    title, and the caller is not expected to know which it holds: the two kinds
    coexist permanently within one record, because only albums on disk are ever
    enriched, so the copy you own carries an MBID and its catalogue-only twins
    never will. A bookmark made before enrichment landed carries the title key
    for a record now filed under the MBID, and both must still arrive here.

    Resolution goes through ``deps._record_key_pairs``, which is the only place
    allowed to decide what "the same record" is. Do not group editions by
    comparing keys anywhere else — the rule is asymmetric, and a naive match
    files the copy you own apart from its siblings for good.
    """
    group = await release_group_out(session, key, artist_id=artist_id)
    if not group.editions:
        raise HTTPException(
            status_code=http_status.HTTP_404_NOT_FOUND,
            detail="No such release group.",
        )
    return group


@router.post(
    "/albums/{album_id}/queue", response_model=MessageOut, summary="Queue an album"
)
async def api_queue_album(session: SessionDep, album_id: str) -> MessageOut:
    """Mark an album wanted and enqueue it for download.

    ``detail.upgrade`` says whether this was a fresh download or a better copy of
    something already on disk, and it is read **before** the album is queued
    because afterwards it cannot be: ``queue_album`` only promotes
    ``skipped``/``failed``/``wanted``, so an album being upgraded keeps
    ``status = downloaded`` for the whole download. A client deriving "was that
    an upgrade?" from the album it gets back would answer no, every time.
    """
    album = await get_album_or_404(session, album_id)
    status_before = album.status
    upgrade = status_before is AlbumStatus.DOWNLOADED
    item, created = await queue_album(session, album)
    if not created:
        message = f"{album.display_title} is already queued."
    elif upgrade:
        message = f"Queued an upgrade of {album.display_title}."
    else:
        message = f"Queued {album.display_title}."
    return MessageOut(
        ok=True,
        message=message,
        level="success" if created else "info",
        detail={
            "queue_item_id": item.id,
            "created": created,
            "upgrade": upgrade,
            "album_status_before": status_before.value,
        },
    )


@router.patch(
    "/albums/{album_id}", response_model=AlbumOut, summary="Update release settings"
)
async def api_update_album(
    session: SessionDep, album_id: str, payload: AlbumUpdateIn
) -> AlbumOut:
    """Set the per-release switches, and optionally ``monitored``/``status``.

    Omitted (or ``null``) fields are left alone — that is what makes this a
    partial update. Sending ``false`` to mean "no change" turns three switches
    off, so a client with a tri-state control must omit the key rather than
    serialise its untouched state.

    The three switches are ``pin_tags`` (enrichment's background write-back
    leaves this release's tags alone; the NFO is still merged, and the explicit
    re-tag buttons still work), ``freeze_path`` (nothing re-files this folder)
    and ``mute_integrity`` (no quarantine and no place in the Integrity screen's
    actionable count — the measurement still happens and this release's own
    ``integrity_state`` and ``corrupt_tracks`` still report it).

    Toggling ``monitored`` from a row is ``POST /albums/{id}/monitor``, whose
    empty body flips it. This route needs the value, because omitting it here
    means "leave alone".
    """
    album = await get_album_or_404(session, album_id)
    album = await apply_album_update(session, album, payload)
    return await album_out(session, album)


@router.post(
    "/albums/{album_id}/monitor",
    response_model=AlbumOut,
    summary="Monitor or ignore an album",
)
async def api_monitor_album(
    session: SessionDep,
    album_id: str,
    payload: Annotated[AlbumUpdateIn | None, Body()] = None,
) -> AlbumOut:
    """Set (or toggle, when the body is empty) an album's monitored flag."""
    album = await get_album_or_404(session, album_id)
    album = await set_album_monitored(session, album, payload or AlbumUpdateIn())
    return await album_out(session, album)


# ---------------------------------------------------------------------------
# Queue
# ---------------------------------------------------------------------------
@router.get("/queue", response_model=QueueListOut, summary="Download queue")
async def api_queue(
    session: SessionDep,
    state: OptQueueStateQuery = None,
    limit: int = Query(100, ge=1, le=500),
    offset: int = Query(0, ge=0),
) -> QueueListOut:
    """List queue entries, active first, then pending in worker order."""
    items, total = await list_queue_items(
        session, state=state, limit=limit, offset=offset
    )
    return QueueListOut(
        items=items,
        total=total,
        limit=limit,
        offset=offset,
        unfiltered_total=await _unfiltered_total(
            filtered=state is not None,
            total=total,
            count=lambda: list_queue_items(session, limit=1),
        ),
    )


@router.post(
    "/queue/{item_id}/retry", response_model=MessageOut, summary="Retry a queue item"
)
async def api_retry_queue_item(session: SessionDep, item_id: int) -> MessageOut:
    """Reset a failed or cancelled queue entry back to pending."""
    item = await get_queue_item_or_404(session, item_id)
    return MessageOut(ok=True, message=await retry_queue_item(session, item))


@router.delete(
    "/queue/{item_id}", response_model=MessageOut, summary="Cancel a queue item"
)
async def api_cancel_queue_item(session: SessionDep, item_id: int) -> MessageOut:
    """Cancel a queue entry (aborting the download when it is active)."""
    item = await get_queue_item_or_404(session, item_id)
    return MessageOut(
        ok=True,
        message=await cancel_queue_item(session, item),
        # Cancelling is a warning for the same reason deleting is: it succeeded,
        # and what it did was stop something somebody asked for.
        level="warning",
    )


@router.get("/queue/{item_id}", response_model=QueueItemOut, summary="One queue item")
async def api_get_queue_item(session: SessionDep, item_id: int) -> QueueItemOut:
    """Return a single queue entry."""
    item = await get_queue_item_or_404(session, item_id)
    return queue_item_to_out(item)


# ---------------------------------------------------------------------------
# Activity
# ---------------------------------------------------------------------------
@router.get("/activity", response_model=ActivityListOut, summary="Activity history")
async def api_activity(
    session: SessionDep,
    level: OptActivityLevelQuery = None,
    event: OptStrQuery = None,
    artist_id: OptStrQuery = None,
    album_id: OptStrQuery = None,
    limit: int = Query(100, ge=1, le=1000),
    offset: int = Query(0, ge=0),
) -> ActivityListOut:
    """Page through the activity/history feed, newest first.

    The ceiling is 1000 because the history screen's own limit control offered
    that much and halving it would quietly delete half the range of a filter
    somebody was already using. Nothing here is expensive per row — it is one
    indexed query and a page of short strings.
    """
    items, total = await list_activity(
        session,
        level=level,
        event=event,
        artist_id=artist_id,
        album_id=album_id,
        limit=limit,
        offset=offset,
    )
    return ActivityListOut(
        items=items,
        total=total,
        limit=limit,
        offset=offset,
        unfiltered_total=await _unfiltered_total(
            filtered=bool(level or event or artist_id or album_id),
            total=total,
            count=lambda: list_activity(session, limit=1),
        ),
    )


# ---------------------------------------------------------------------------
# Search
# ---------------------------------------------------------------------------
@router.get("/search", response_model=SearchResultOut, summary="Search Qobuz")
async def api_search(
    session: SessionDep,
    q: str = Query("", description="Free-text query."),
    type: Literal["all", "artists", "albums"] = Query("all"),
    limit: int = Query(25, ge=1, le=100),
    offset: int = Query(0, ge=0),
) -> SearchResultOut:
    """Search the Qobuz catalogue, annotated with what is already in the library."""
    return await run_search(session, q, search_type=type, limit=limit, offset=offset)
