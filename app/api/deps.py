"""Shared FastAPI dependencies, template plumbing and read-model builders.

This module is the seam between the HTTP layer (:mod:`app.api.routes_ui` and
:mod:`app.api.routes_api`) and the rest of the application:

* **Templates** — a single configured :class:`~fastapi.templating.Jinja2Templates`
  instance plus the filters/globals every page relies on, and :func:`render`,
  which injects the shared context (app name, version, nav state, banners).
* **Runtime singletons** — thin, defensive accessors for the objects that live in
  ``app.core.state`` (Qobuz client, indexer, download worker, scheduler, rate
  limiter).  They are looked up lazily and by several plausible attribute names,
  and every one of them degrades to ``None`` rather than raising, so the web UI
  still renders on a machine with no credentials, no network, or a half-wired
  application.  See the module docstring of ``app.core.state`` for the contract.
* **Read models** — async helpers that turn database rows into the Pydantic
  models declared in :mod:`app.schemas`.  Both the HTML and the JSON routers use
  these, so the two views can never drift apart.

Nothing here performs writes; the routers own all mutations.
"""

from __future__ import annotations

import asyncio
import importlib
import inspect
import math
from collections import defaultdict
from collections.abc import Iterable, Sequence
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Any

from fastapi import Depends, Request
from fastapi.templating import Jinja2Templates
from sqlalchemy import Select, case, func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app import __version__
from app.config import (
    FORMAT_LABELS,
    SETTING_ORIGINS,
    Settings,
    effective_for,
    get_effective_settings,
    get_settings,
    overridable_keys,
    setting_origins,
)
from app.core import quality
from app.core.enricher import (
    REVIEW_STATES,
    actionable_review_clause,
    in_library,
    library_scope,
    load_album_metadata,
    load_artist_metadata,
    load_track_metadata,
)
from app.core.integrity import IntegrityState
from app.core.indexer import dedupe_key
from app.db import get_session
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumMetadata,
    AlbumStatus,
    Artist,
    ArtistMetadata,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
    FingerprintState,
    MonitorMode,
    QueueItem,
    QueueState,
    RELEASE_TYPES,
    Track,
    TrackMetadata,
    TrackOrigin,
    TrackStatus,
    utcnow,
)
from app.schemas import (
    ActivityOut,
    AlbumDetailOut,
    AlbumOut,
    ArtistOut,
    ArtistStatsOut,
    BannerOut,
    ConsensusFieldOut,
    ConsensusRuleOut,
    ConsensusVoteOut,
    CorruptTrackOut,
    EnrichmentSourceStatusOut,
    IndexerStatusOut,
    IntegrityReportOut,
    IntegrityStatusOut,
    DiskCapacityOut,
    LibraryQualityOut,
    LibraryStatsOut,
    MetaOut,
    NavCountsOut,
    QueueItemOut,
    QueueStatsOut,
    RateLimitStatusOut,
    ReleaseGroupOut,
    ReleaseGroupRefOut,
    SettingsOut,
    StatusOut,
    TagMapRowOut,
    TrackIntegrityOut,
    TrackOut,
)

__all__ = [
    "APP_NAME",
    "BASE_DIR",
    "TEMPLATES_DIR",
    "STATIC_DIR",
    "templates",
    "render",
    "SessionDep",
    "SettingsDep",
    "get_settings_dep",
    "get_core_state",
    "get_qobuz_client",
    "get_indexer",
    "get_queue_worker",
    "get_scheduler",
    "get_library_scanner",
    "get_library_importer",
    "get_rate_limiter",
    "get_started_at",
    "call_first",
    "as_utc",
    "seconds_since",
    "library_stats",
    "albums_by_status",
    "library_quality",
    "queue_stats",
    "indexer_status",
    "rate_limit_status",
    "recent_activity",
    "build_status",
    "build_settings_out",
    "naming_preview",
    "artist_to_out",
    "album_to_out",
    "track_to_out",
    "queue_item_to_out",
    "activity_to_out",
    "list_artists",
    "list_albums_for_artist",
    "list_wanted_albums",
    "list_queue_items",
    "list_activity",
    "nav_counts",
    "nav_counts_out",
    "library_scan_status",
    "library_import_status",
    "integrity_report_out",
    "integrity_status",
    "list_corrupt_tracks",
    "MISSING_STATUSES",
    "artist_album_counts",
    "album_corrupt_counts",
    "album_integrity_state",
    "recorded_integrity_state",
    "banner_context",
    "build_banners",
    "build_enrichment_sources",
    "build_meta",
    "album_consensus",
    "album_detail",
    "artist_stats",
    "list_release_group",
    "release_group_out",
    "group_editions",
]

logger = get_logger(__name__)

#: Product name shown in the navbar and page titles.
APP_NAME = "Qobuzarr"

#: Repository root; templates and static assets live beside ``app/``.
BASE_DIR: Path = Path(__file__).resolve().parent.parent.parent
TEMPLATES_DIR: Path = BASE_DIR / "templates"
STATIC_DIR: Path = BASE_DIR / "static"


# ---------------------------------------------------------------------------
# Small formatting helpers, exposed to templates as filters
# ---------------------------------------------------------------------------
def as_utc(value: datetime | str | None) -> datetime | None:
    """Return *value* as a timezone-aware UTC datetime.

    SQLite round-trips ``DateTime(timezone=True)`` columns as *naive* datetimes,
    so anything read back from the database must be re-stamped before it can be
    compared against :func:`app.models.utcnow`.

    ISO-8601 strings are accepted too, because some payloads reach a template
    after a JSON round-trip (the stored disk-scan summary, for one) and the
    ``ago``/``dt`` filters must not explode on them. An unparseable string is
    ``None`` rather than an error.
    """
    if value is None:
        return None
    if isinstance(value, str):
        try:
            value = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return None
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def _maybe_datetime(value: Any) -> datetime | None:
    """Coerce *value* to an aware UTC datetime, or ``None`` if it is not one."""
    return as_utc(value) if isinstance(value, datetime) else None


def seconds_since(value: datetime | None) -> float | None:
    """Seconds elapsed since *value*, or ``None`` when it is unset."""
    stamped = as_utc(value)
    if stamped is None:
        return None
    return (utcnow() - stamped).total_seconds()


def fmt_datetime(value: datetime | None, fmt: str = "%Y-%m-%d %H:%M") -> str:
    """Render a timestamp in UTC, or an em dash when missing."""
    stamped = as_utc(value)
    return stamped.strftime(fmt) if stamped else "—"


def fmt_date(value: Any) -> str:
    """Render a ``date``/``datetime`` as ISO ``YYYY-MM-DD``."""
    if value is None:
        return "—"
    if isinstance(value, datetime):
        stamped = as_utc(value)
        return stamped.date().isoformat() if stamped else "—"
    return str(value)


def fmt_ago(value: datetime | None) -> str:
    """Render a coarse "3m ago" style relative time."""
    elapsed = seconds_since(value)
    if elapsed is None:
        return "never"
    if elapsed < 0:
        return "just now"
    return f"{fmt_span(elapsed)} ago"


def fmt_span(seconds: float | int | None) -> str:
    """Render a duration in seconds as a compact ``2h 5m`` style string."""
    if seconds is None:
        return "—"
    total = int(max(0, seconds))
    if total < 60:
        return f"{total}s"
    if total < 3600:
        return f"{total // 60}m {total % 60:02d}s"
    if total < 86400:
        return f"{total // 3600}h {(total % 3600) // 60:02d}m"
    return f"{total // 86400}d {(total % 86400) // 3600:02d}h"


def fmt_duration(seconds: float | int | None) -> str:
    """Render a track/album duration as ``m:ss`` or ``h:mm:ss``."""
    if not seconds:
        return "—"
    total = int(seconds)
    hours, remainder = divmod(total, 3600)
    minutes, secs = divmod(remainder, 60)
    if hours:
        return f"{hours}:{minutes:02d}:{secs:02d}"
    return f"{minutes}:{secs:02d}"


def fmt_size(num_bytes: int | float | None) -> str:
    """Render a byte count with binary units."""
    if not num_bytes:
        return "—"
    units = ("B", "KiB", "MiB", "GiB", "TiB")
    size = float(num_bytes)
    index = min(int(math.log(size, 1024)) if size > 0 else 0, len(units) - 1)
    return f"{size / (1024 ** index):.1f} {units[index]}"


def fmt_quality(album: Any) -> str:
    """Short quality badge text for an album row."""
    depth = getattr(album, "max_bit_depth", None)
    rate = getattr(album, "max_sampling_rate", None)
    if depth and rate:
        return f"{int(depth)}bit/{float(rate):g}kHz"
    if getattr(album, "hires", False):
        return "Hi-Res"
    return "—"


def fmt_format_label(format_id: Any) -> str:
    """Short label for a Qobuz ``format_id``, e.g. ``"FLAC 24-96"``.

    Empty string for ``None``, so ``{% if %}``-free templates collapse cleanly.
    """
    return quality.format_label(format_id)


def enum_value(value: Any) -> str:
    """Return ``value.value`` for enums, ``str(value)`` otherwise."""
    return getattr(value, "value", value if value is None else str(value)) or ""


def status_class(value: Any) -> str:
    """CSS modifier for an :class:`AlbumStatus`/:class:`QueueState` chip.

    Pairs with the ``.tag--*`` rules in ``static/style.css``.
    """
    return f"tag--{enum_value(value) or 'unknown'}"


# ---------------------------------------------------------------------------
# Templates
# ---------------------------------------------------------------------------
templates = Jinja2Templates(directory=str(TEMPLATES_DIR))
templates.env.filters.update(
    {
        "dt": fmt_datetime,
        "date": fmt_date,
        "ago": fmt_ago,
        "span": fmt_span,
        "duration": fmt_duration,
        "size": fmt_size,
        "quality": fmt_quality,
        "format_label": fmt_format_label,
        "enum_value": enum_value,
        "status_class": status_class,
    }
)
#: Every enum vocabulary and label map the presentation layer iterates, declared
#: once. The templates read these as Jinja globals and :func:`build_meta` serves
#: the same values as JSON; two copies of one list is the kind of duplication
#: that stays correct for exactly as long as nobody adds a release type.
_VOCABULARIES: dict[str, list[str]] = {
    "release_types": list(RELEASE_TYPES),
    "monitor_modes": [m.value for m in MonitorMode],
    "album_statuses": [s.value for s in AlbumStatus],
    "queue_states": [s.value for s in QueueState],
    "activity_levels": [level.value for level in ActivityLevel],
    "track_statuses": [s.value for s in TrackStatus],
    "track_origins": [origin.value for origin in TrackOrigin],
    "enrichment_sources": [source.value for source in EnrichmentSource],
    "enrichment_entities": [entity.value for entity in EnrichmentEntity],
    "fingerprint_states": [s.value for s in FingerprintState],
}

templates.env.globals.update(
    {
        "app_name": APP_NAME,
        "app_version": __version__,
        "format_labels": dict(FORMAT_LABELS),
        **_VOCABULARIES,
    }
)


def build_meta() -> MetaOut:
    """The client's copy of every vocabulary the server decides.

    Deliberately assembled from the enums themselves rather than from a literal
    list: the whole point is that a value added to :class:`AlbumStatus` shows up
    here without anybody remembering to come and add it, because the failure mode
    of forgetting is a silent one — a ``<select>`` short one option, or a chip
    that falls through to whatever the client's default branch does.

    ``identifiable_sources``, ``enrichment_states`` and ``review_states`` are
    imported from the modules that own them (:mod:`app.core.enricher` and
    :mod:`app.enrich.errors`) for the same reason the release-group keying is
    server-side: they encode a rule, and a client that re-implements the rule
    drifts from the matcher without either side noticing.

    ``setting_origins`` is the one entry here that is not an enum at all but a
    :data:`~typing.Literal`, and it is published for a sharper reason than the
    rest: ``SettingsOut.origins`` is declared ``dict[str, str]``, so the pair
    never reaches the OpenAPI document and a hand-typed copy of it in
    ``types.ts`` would be the one union ``tests/test_wire_contract.py`` cannot
    see drift in.
    """
    from app.core.enricher import identifiable_sources  # noqa: PLC0415
    from app.core.integrity import IntegrityState  # noqa: PLC0415
    from app.enrich.errors import ENRICHMENT_STATES  # noqa: PLC0415

    return MetaOut(
        app_name=APP_NAME,
        app_version=__version__,
        enrichment_states=list(ENRICHMENT_STATES),
        review_states=list(REVIEW_STATES),
        identifiable_sources=[source.value for source in identifiable_sources()],
        integrity_states=[state.value for state in IntegrityState],
        setting_origins=list(SETTING_ORIGINS),
        format_labels={str(key): value for key, value in FORMAT_LABELS.items()},
        tag_map=build_tag_map(),
        consensus_rule=build_consensus_rule(),
        **{name: list(values) for name, values in _VOCABULARIES.items()},
    )


#: What may overwrite each kind of tag value, in the words of the rule deciding.
#:
#: Authored here for the same reason as :data:`_GATE_REASONS`: the rule is this
#: process's own, and a client that re-implements ">51% and a tie keeps Qobuz"
#: drifts from :func:`app.enrich.merge.consensus` with neither side noticing.
_CONFLICT_NOTES: dict[str, str] = {
    "qobuz": (
        "Qobuz's own value, written on every download and re-tag. Enrichment "
        "never overwrites it in the file."
    ),
    "enrichment": (
        "Whichever rung matched exactly. A manual identification wins over any "
        "later automatic one, and a release with Pin tags set is skipped by the "
        "background write-back entirely."
    ),
    "consensus": (
        "Decided by majority — more than half of the sources that had an opinion "
        "must agree, and a tie keeps what Qobuz said."
    ),
    "never": (
        "Never overwritten. It is a naming-template token, so a 'better' value "
        "would want to rename every folder it appears in."
    ),
    "derived": "Computed from another tag rather than read from anywhere.",
}


def build_tag_map() -> list[TagMapRowOut]:
    """The Vorbis fields the tagger writes, what fills each, and what wins.

    One row per :data:`app.core.tagger.VORBIS_FIELDS` entry, **in write order**,
    so the table reads as the loop runs. Nothing here is a literal list of tags:
    the fields come from the tagger, the enrichment origins from
    :data:`app.core.nfo.ENRICHMENT_TAGS`, the catalogue origins from
    :data:`app.core.tagger.QOBUZ_TAG_FIELDS` and the conflict verdict from
    :mod:`app.enrich.merge`. Adding a tag to any of those puts a row here without
    anybody remembering to.

    A tag key with no origin at all raises :class:`KeyError` rather than
    rendering a blank cell — ``tests/test_tag_map.py`` asserts the two-way
    closure that makes that unreachable.
    """
    from app.core import nfo, tagger  # noqa: PLC0415
    from app.enrich import merge  # noqa: PLC0415

    qobuz_sources = dict(tagger.QOBUZ_TAG_FIELDS)
    enrichment = {key: (entity, column) for key, entity, column in nfo.ENRICHMENT_TAGS}
    derived = dict(nfo.DERIVED_TAGS)

    rows: list[TagMapRowOut] = []
    for field, key in tagger.VORBIS_FIELDS:
        # A truncation has no ID3 counterpart: MP3 gets the whole ORIGINALDATE in
        # TDOR and nothing carrying just the year, so naming a frame here would
        # promise a cell an MP3 never has. The ORIGINALDATE row says it truthfully.
        id3_frame = None if field in tagger.VORBIS_YEAR_SLICE else tagger.id3_frame_for(key)
        if field in tagger.VORBIS_YEAR_SLICE:
            origin = "derived"
            source = f"tag {key} (first four characters)"
        elif key in enrichment:
            entity, column = enrichment[key]
            origin = "enrichment"
            source = f"{entity}_metadata.{column}"
            fallback = qobuz_sources.get(key)
            if fallback is not None:
                # Both halves write it: build_tags fills the catalogue value and
                # the ``extra`` loop overwrites it when enrichment has one.
                source = f"{source} (falls back to {fallback})"
        elif key in qobuz_sources:
            origin = "qobuz"
            source = qobuz_sources[key]
        elif key in derived:
            origin = "derived"
            source = f"tag {derived[key]}"
        else:
            raise KeyError(f"tag key {key!r} is written but has no origin")

        if key in merge.NEVER_WRITABLE:
            conflict = "never"
        elif key in merge.WRITABLE_FIELDS:
            conflict = "consensus"
        else:
            conflict = origin

        rows.append(
            TagMapRowOut(
                vorbis_field=field,
                tag_key=key,
                id3_frame=id3_frame,
                origin=origin,
                source_field=source,
                conflict=conflict,
                conflict_note=_CONFLICT_NOTES[conflict],
            )
        )
    return rows


def build_consensus_rule() -> ConsensusRuleOut:
    """:func:`app.enrich.merge.consensus`'s rule, read off the function itself.

    The threshold comes from the signature default rather than a repeated
    literal, because the one number on the screen that must not drift from the
    comparison is that one.
    """
    import inspect  # noqa: PLC0415

    from app.enrich import merge  # noqa: PLC0415

    default = inspect.signature(merge.consensus).parameters["threshold"].default
    return ConsensusRuleOut(
        threshold=float(default),
        tie_keeps="qobuz",
        writable_fields=sorted(merge.WRITABLE_FIELDS),
        never_writable=sorted(merge.NEVER_WRITABLE),
        note=(
            "A value is overwritten only when more than half of the sources that "
            "had an opinion agree. Exactly half is a tie, and a tie keeps what "
            "Qobuz said — so with a single rung enabled nothing is ever "
            "overwritten. Qobuz always votes with its original value, so one "
            "early majority never looks unanimous forever."
        ),
    )


#: Why each rung is not running, in the words of the setting that would fix it.
#:
#: Authored here rather than in the client because the gate *is* this process's
#: own configuration: a client that hard-codes "needs ACOUSTID_API_KEY" is
#: describing a `.env` it cannot see, and the sentence stops being true the day
#: the gate moves. That is not hypothetical — it is where these sentences came
#: from, a template that was the only place in the system naming the setting.
_GATE_REASONS: dict[str, str] = {
    "ENRICHMENT_CONTACT": (
        "MusicBrainz requires a contact address in the User-Agent of every "
        "request. Set ENRICHMENT_CONTACT to an email address or a URL and "
        "restart; nothing is sent to MusicBrainz until you do."
    ),
    "ACOUSTID_API_KEY": (
        "AcoustID lookups need a free API key. Set ACOUSTID_API_KEY and restart. "
        "Files are still checked for corruption without one — that half is local "
        "and needs no account — but nothing will be identified from its audio."
    ),
    "fpcalc": (
        "Chromaprint's fpcalc binary was not found. Install it and put it on "
        "PATH, or point FPCALC_PATH at it — a long-running server only sees the "
        "PATH it was started with, so prefer the setting."
    ),
}


def _source_gate(source: EnrichmentSource, settings: Settings) -> str | None:
    """The one thing this rung is missing, or ``None`` when it is ready.

    Only two rungs have gates, and the AcoustID one has two of them in a
    deliberate order: ``fpcalc`` first, because its absence stops the rung
    outright, then the API key, whose absence only costs the identification half
    while the corruption check carries on. Reporting them the other way round
    would name the smaller problem while the bigger one is unmentioned.
    """
    if source is EnrichmentSource.MUSICBRAINZ:
        return None if settings.musicbrainz_ready else "ENRICHMENT_CONTACT"
    if source is EnrichmentSource.ACOUSTID:
        from app.enrich.chromaprint import find_fpcalc  # noqa: PLC0415 - one use

        if find_fpcalc(settings.fpcalc_path) is None:
            return "fpcalc"
        return None if settings.acoustid_ready else "ACOUSTID_API_KEY"
    return None


def build_enrichment_sources(
    payload: dict[str, Any], settings: Settings | None = None
) -> list[EnrichmentSourceStatusOut]:
    """Every rung of the ladder, with its gate, its reason and its counts.

    *payload* is :meth:`app.core.enricher.Enricher.status`'s dict, so the state
    counts come from the one query that produced the rest of the page rather than
    from a second one that could disagree with it.

    Every member of :class:`~app.models.EnrichmentSource` appears, not just the
    configured ones — the configured ladder first, in its own order, then the
    rest. A rung somebody switched off is a fact worth rendering ("Wikidata is
    not in ENRICHMENT_SOURCES"), and a rung that is missing from the list
    entirely is indistinguishable from a rung that answered nothing.
    """
    settings = effective_for(settings or get_settings())
    from app.core.enricher import identifiable_sources  # noqa: PLC0415 - import cycle

    identifiable = set(identifiable_sources())
    configured = [str(name) for name in payload.get("sources") or ()]
    states: dict[str, Any] = payload.get("states") or {}
    enabled = settings.enrichment_enabled

    ordered: list[EnrichmentSource] = []
    for name in configured:
        try:
            ordered.append(EnrichmentSource(name))
        except ValueError:  # a name the settings accepted and the enum does not
            continue
    ordered += [source for source in EnrichmentSource if source not in ordered]

    out: list[EnrichmentSourceStatusOut] = []
    for source in ordered:
        gate = _source_gate(source, settings)
        in_ladder = source.value in configured
        out.append(
            EnrichmentSourceStatusOut(
                name=source.value,
                enabled=in_ladder,
                # Enabled globally, in the ladder, and nothing missing. All three
                # have to hold before this rung will reach an upstream at all.
                ready=bool(enabled and in_ladder and gate is None),
                gated_on=gate,
                gated_reason=_GATE_REASONS.get(gate or ""),
                identifiable=source in identifiable,
                states={
                    str(state): int(count)
                    for state, count in (states.get(source.value) or {}).items()
                },
            )
        )
    return out


def banner_context(settings: Settings | None = None) -> list[dict[str, str]]:
    """Friendly warnings shown at the top of every page.

    Never raises: a missing client or an unresolved secret is reported as a
    banner, not an exception.

    Every banner carries a ``code`` alongside its prose. The prose exists to be
    read and gets rewritten whenever it reads badly; anything that wants to
    *route* on a banner — link "app secret unresolved" to the settings screen,
    say — has to match on something that does not move, and matching on the
    sentence quietly makes the sentence an interface.
    """
    settings = effective_for(settings or get_settings())
    banners: list[dict[str, str]] = []

    if not settings.has_credentials:
        banners.append(
            {
                "code": "no_credentials",
                "level": "error",
                "title": "Qobuz credentials missing",
                "message": (
                    "Set QOBUZ_APP_ID and QOBUZ_USER_AUTH_TOKEN in .env, then "
                    "restart. Searching, indexing and downloading are disabled "
                    "until then."
                ),
            }
        )

    client = get_qobuz_client()
    if settings.has_credentials and client is None:
        banners.append(
            {
                "code": "no_client",
                "level": "warning",
                "title": "Qobuz client not started",
                "message": (
                    "The application started without a Qobuz client. Library "
                    "browsing works, but live search and downloads do not."
                ),
            }
        )
    elif client is not None and not getattr(client, "has_app_secret", False):
        banners.append(
            {
                "code": "no_app_secret",
                "level": "warning",
                "title": "App secret unresolved",
                "message": (
                    "Qobuzarr has not yet derived a working app secret. It will "
                    "try again on the next signed request; set QOBUZ_APP_SECRET "
                    "in .env to skip derivation entirely."
                ),
            }
        )

    limiter = get_rate_limiter()
    breaker = getattr(limiter, "breaker", None) if limiter is not None else None
    if breaker is not None:
        try:
            if breaker.is_open():
                banners.append(
                    {
                        "code": "breaker_open",
                        "level": "warning",
                        "title": "Circuit breaker open",
                        "message": (
                            "Qobuz returned repeated rate-limit errors, so "
                            f"outbound calls are paused for "
                            f"{fmt_span(breaker.seconds_remaining())}."
                        ),
                    }
                )
        except Exception as exc:  # noqa: BLE001 - the banner must never break a page
            logger.debug("Circuit-breaker probe failed: %s", exc)

    return banners


def build_banners(settings: Settings | None = None) -> list[BannerOut]:
    """:func:`banner_context` as typed models, for the JSON API.

    One producer, two shapes — the same reason the read models in this module
    exist. A second implementation would drift, and the way it would drift is
    that one of the four conditions gets fixed on one side only, so the screen
    that polls status says the credentials are fine while the screen that fetches
    banners says they are missing.
    """
    return [BannerOut(**banner) for banner in banner_context(settings)]


def render(
    request: Request,
    template_name: str,
    context: dict[str, Any] | None = None,
    *,
    active: str = "",
    status_code: int = 200,
) -> Any:
    """Render *template_name* with the shared page context applied.

    Args:
        request: The incoming request (Starlette requires it for url_for).
        template_name: Template path relative to ``templates/``.
        context: Page-specific variables.
        active: Nav item to highlight (``dashboard``/``artists``/...).
        status_code: HTTP status for the response.
    """
    settings = get_settings()
    payload: dict[str, Any] = {
        "active": active,
        "settings": settings,
        "banners": banner_context(settings),
        "now": utcnow(),
    }
    payload.update(context or {})
    return templates.TemplateResponse(
        request, template_name, payload, status_code=status_code
    )


async def render_page(
    request: Request,
    session: AsyncSession,
    template_name: str,
    context: dict[str, Any] | None = None,
    *,
    active: str = "",
    status_code: int = 200,
) -> Any:
    """:func:`render` for a whole page: the same thing, plus the nav badges.

    The sidebar is a live region like any other, and the rule for those is that
    the first frame the page renders must match what its poller re-fetches.
    Without the counts here the badges are simply absent until the first tick 20
    seconds later, so every page load shows a Wanted count of nothing and then
    silently grows one.

    Fragments keep using :func:`render` — they are rendered *by* the poller, and
    a fragment that recomputed the nav would have every region refresh every
    other one.
    """
    payload = dict(context or {})
    payload.setdefault("nav_counts", await nav_counts(session))
    return render(
        request, template_name, payload, active=active, status_code=status_code
    )


# ---------------------------------------------------------------------------
# Dependencies
# ---------------------------------------------------------------------------
def get_settings_dep() -> Settings:
    """FastAPI dependency returning the **effective** configuration.

    The environment plus the settings overlay (``app_setting``), which is what
    every request should read: the user pressed the switch more recently than
    they edited the file. With no override installed this returns the cached
    singleton itself, unchanged and un-copied.

    ``get_settings()`` is still called through this module's own name so a test
    that monkeypatches ``app.api.deps.get_settings`` to point the API at a
    scratch library keeps working — the overlay is laid over *that*.
    """
    return effective_for(get_settings())


SessionDep = Depends(get_session)
SettingsDep = Depends(get_settings_dep)


# ---------------------------------------------------------------------------
# Runtime singletons (app.core.state), looked up defensively
# ---------------------------------------------------------------------------
_STATE_MODULE_NAME = "app.core.state"
_state_module_missing = False


def _state_module() -> Any | None:
    """Import ``app.core.state`` lazily, tolerating its absence."""
    global _state_module_missing
    if _state_module_missing:
        return None
    try:
        return importlib.import_module(_STATE_MODULE_NAME)
    except Exception as exc:  # noqa: BLE001 - optional at import time by design
        _state_module_missing = True
        logger.debug("%s unavailable: %s", _STATE_MODULE_NAME, exc)
        return None


def get_core_state() -> Any | None:
    """Return the application-state container, or ``None`` when unavailable.

    Accepts either a ``get_state()``/``get_app_state()`` factory, a module-level
    ``state``/``app_state`` object, or plain module-level attributes.
    """
    module = _state_module()
    if module is None:
        return None
    for name in ("state_or_none", "get_state", "get_app_state"):
        factory = getattr(module, name, None)
        if callable(factory):
            try:
                container = factory()
            except Exception as exc:  # noqa: BLE001 - not started yet is normal
                logger.debug("%s.%s() failed: %s", _STATE_MODULE_NAME, name, exc)
                continue
            if container is not None:
                return container
    for name in ("state", "app_state", "STATE"):
        obj = getattr(module, name, None)
        if obj is not None:
            return obj
    return module


def _lookup(names: Sequence[str]) -> Any | None:
    """First non-``None`` attribute of the state container among *names*.

    Zero-argument callables whose name starts with ``get_`` are invoked.
    """
    state = get_core_state()
    if state is None:
        return None
    for name in names:
        value = getattr(state, name, None)
        if value is None:
            continue
        if name.startswith("get_") and callable(value):
            try:
                value = value()
            except Exception as exc:  # noqa: BLE001
                logger.debug("state.%s() failed: %s", name, exc)
                continue
        if value is not None:
            return value
    return None


def get_qobuz_client() -> Any | None:
    """The shared :class:`~app.qobuz.client.QobuzClient`, if one is wired up."""
    return _lookup(("client", "qobuz_client", "qobuz", "get_client", "get_qobuz_client"))


def get_indexer() -> Any | None:
    """The background indexer service, if one is wired up."""
    return _lookup(("indexer", "indexer_service", "get_indexer"))


def get_queue_worker() -> Any | None:
    """The sequential download worker, if one is wired up."""
    return _lookup(
        (
            "queue",
            "queue_worker",
            "downloader",
            "download_worker",
            "worker",
            "get_queue",
            "get_queue_worker",
            "get_downloader",
        )
    )


def get_scheduler() -> Any | None:
    """The APScheduler instance, if one is wired up."""
    return _lookup(("scheduler", "get_scheduler"))


def get_library_scanner() -> Any | None:
    """The shared :class:`~app.core.scanner.LibraryScanner`, if one is wired up."""
    return _lookup(("scanner", "library_scanner", "get_scanner"))


def get_library_importer() -> Any | None:
    """The shared :class:`~app.core.importer.LibraryImporter`, if one is wired up."""
    return _lookup(("importer", "library_importer", "get_importer"))


def get_enricher() -> Any | None:
    """The shared :class:`~app.core.enricher.Enricher`, if one is wired up."""
    return _lookup(("enricher", "get_enricher"))


def get_rate_limiter() -> Any | None:
    """The single global :class:`~app.net.ratelimit.RateLimiter`."""
    limiter = _lookup(("limiter", "rate_limiter", "ratelimiter", "get_limiter", "get_rate_limiter"))
    if limiter is not None:
        return limiter
    client = get_qobuz_client()
    return getattr(client, "limiter", None) if client is not None else None


def get_started_at() -> datetime | None:
    """Process start time recorded by the application state, if any."""
    value = _lookup(("started_at", "start_time", "get_started_at"))
    return value if isinstance(value, datetime) else None


async def call_first(
    obj: Any, names: Sequence[str], *args: Any, **kwargs: Any
) -> tuple[bool, Any]:
    """Call the first attribute of *obj* named in *names*.

    Awaits the result when it is awaitable. Returns ``(handled, result)`` where
    ``handled`` is ``False`` when *obj* is ``None`` or exposes none of *names*,
    letting callers fall back to a database-only implementation.

    Raises:
        Exception: whatever the underlying call raises — failures are the
            caller's business, only *absence* is swallowed here.
    """
    if obj is None:
        return False, None
    for name in names:
        method = getattr(obj, name, None)
        if method is None or not callable(method):
            continue
        result = method(*args, **kwargs)
        if inspect.isawaitable(result):
            result = await result
        return True, result
    return False, None


# ---------------------------------------------------------------------------
# Read models: entities
# ---------------------------------------------------------------------------
def artist_to_out(
    artist: Artist,
    *,
    album_count: int | None = None,
    wanted_count: int | None = None,
    downloaded_count: int | None = None,
    meta: ArtistMetadata | None = None,
) -> ArtistOut:
    """Convert an :class:`Artist` row into :class:`ArtistOut`.

    *meta* is passed in rather than read off a relationship, and that is load
    bearing. This function is **synchronous**, and even a ``lazy="selectin"``
    relationship raises ``MissingGreenlet`` on a row that was committed but never
    refreshed — precisely what ``Indexer.add_artist`` hands to this function on
    every first follow. Omitting it yields an un-enriched artist, never a 500.
    """
    out = ArtistOut.model_validate(artist)
    # Here rather than in ``_apply_artist_metadata``: enrichment is
    # library-scoped, so most artists have no ``artist_metadata`` row at all and
    # that function returns early. Aliases have no derived source to coalesce
    # with — the only one is what a person typed — so reading them there would
    # show an empty box back to somebody who had just saved three.
    out.aliases = artist.aliases_list
    # Same reasoning as aliases: read straight off the column, no coalesce.
    # Nothing derives a credit filter — it is only ever what somebody picked.
    out.credit_filter = artist.credit_filter_list
    out.album_count = album_count
    out.wanted_count = wanted_count
    out.downloaded_count = downloaded_count
    _apply_artist_metadata(out, artist, meta)
    return out


def _apply_artist_metadata(
    out: ArtistOut, artist: Artist, meta: ArtistMetadata | None
) -> None:
    """Merge ``artist_metadata`` onto the read model. Every field is a coalesce."""
    out.portrait_url = artist.image_url
    if meta is None:
        return

    out.isni = meta.isni
    out.mb_artist_mbid = meta.mb_artist_mbid
    out.deezer_artist_id = meta.deezer_artist_id
    out.wikidata_qid = meta.wikidata_qid
    # The one field with two sources. ``Artist.sort_name`` is typed by a person
    # and nothing re-derives it; ``ArtistMetadata.sort_name`` is MusicBrainz's
    # and is rewritten on every pass. ``model_validate`` above already read the
    # typed one, so this must coalesce rather than assign — plain assignment
    # would show the user the derived value back seconds after they saved.
    out.sort_name = artist.sort_name or meta.sort_name
    out.disambiguation = meta.disambiguation
    out.artist_type = meta.artist_type
    out.country = meta.country
    out.area = meta.area or meta.begin_area
    out.formed = meta.life_span_begin
    out.disbanded = meta.life_span_end
    out.genres = meta.genre_list
    out.bio = meta.bio
    out.bio_source_url = meta.bio_source_url
    out.bio_licence = meta.bio_licence
    # Qobuz's portrait wins when it has one; the others fill the gap.
    out.portrait_url = artist.image_url or meta.portrait_url
    out.external_links = _artist_links(meta)
    out.enriched = bool(
        meta.mb_artist_mbid or meta.deezer_artist_id or meta.isni or meta.bio
    )


def _artist_links(meta: ArtistMetadata) -> list[dict[str, str]]:
    """Outbound links for the artist page, in a stable order."""
    links: list[dict[str, str]] = []
    if meta.mb_artist_mbid:
        links.append(
            {
                "label": "MusicBrainz",
                "url": f"https://musicbrainz.org/artist/{meta.mb_artist_mbid}",
            }
        )
    if meta.isni:
        links.append({"label": "ISNI", "url": f"https://isni.org/isni/{meta.isni}"})
    if meta.wikipedia_url:
        links.append({"label": "Wikipedia", "url": meta.wikipedia_url})
    if meta.wikidata_qid:
        links.append(
            {
                "label": "Wikidata",
                "url": f"https://www.wikidata.org/wiki/{meta.wikidata_qid}",
            }
        )
    if meta.deezer_artist_id:
        links.append(
            {
                "label": "Deezer",
                "url": f"https://www.deezer.com/artist/{meta.deezer_artist_id}",
            }
        )
    if meta.official_homepage:
        links.append({"label": "Official site", "url": meta.official_homepage})
    return links


def recorded_integrity_state(track: Track) -> IntegrityState:
    """What the database *records* about this file. Pure — it opens nothing.

    :func:`app.core.integrity.classify` answers this question properly, by
    reading the file, and costs about 201 ms to do it. A read model may not: an
    album page would turn into a minute of blocking disk I/O on the event loop.
    So this reports the standing record instead, and the record has only three
    values it can be in.

    That is not a simplification, it is what is actually stored. ``RETAGGED`` and
    ``REPLACED`` are verdicts a *pass* gives, and
    :func:`app.core.scheduler.verify_integrity` re-baselines the row in the same
    transaction that gives them — so a moment later the file and its record agree
    again, and a row claiming ``replaced`` would be describing a disagreement
    that no longer exists. Those two counts belong to a run, and
    :class:`app.schemas.IntegrityReportOut` is where they are reported.

    * no ``content_hash`` → ``UNKNOWN``. Nothing has ever baselined this file, so
      nothing is being claimed about it and there is nothing to contradict. It is
      a first-class answer and never means tampering.
    * a hash and a path → ``VERIFIED`` as of ``verified_at``.
    * a hash and no path → ``MISSING``. Something measured this file and the row
      no longer points at one — a quarantine cleared it, or a download was undone.
    """
    if not track.content_hash:
        return IntegrityState.UNKNOWN
    return IntegrityState.VERIFIED if track.path else IntegrityState.MISSING


def album_integrity_state(tracks: Iterable[Track]) -> str | None:
    """Roll a release's recorded verdicts up into one word, or ``None``.

    ``None`` when **no** track carries a baseline, which is the ordinary state of
    a library nothing has measured yet. It has to stay distinguishable from
    ``verified``: collapsing the two would report an unmeasured library as
    checked and clean, which is the one wrong answer available here.

    ``"mixed"`` when the tracks disagree — including the very common case of a
    release half of which has been baselined and half of which has not, because
    saying ``verified`` about that would be claiming coverage the pass has not
    reached yet.
    """
    states = [recorded_integrity_state(track) for track in tracks]
    if not states or all(state is IntegrityState.UNKNOWN for state in states):
        return None
    first = states[0]
    return first.value if all(state is first for state in states) else "mixed"


def track_to_out(track: Track, *, meta: TrackMetadata | None = None) -> TrackOut:
    """Convert a :class:`Track` row into :class:`TrackOut`.

    *meta* is passed in rather than read off a relationship, exactly as it is for
    artists and albums, and for the same reason: this is synchronous, and a row
    that was committed but never refreshed raises ``MissingGreenlet`` on
    attribute access. Omitting it yields a track with ``integrity.fingerprint_state``
    unset, never an error.
    """
    out = TrackOut.model_validate(track)
    out.integrity = TrackIntegrityOut(
        qid=track.qid or "",
        state=recorded_integrity_state(track).value,
        content_hash=track.content_hash,
        sample_count=track.sample_count,
        file_size=track.file_size,
        file_mtime=track.file_mtime,
        last_verified_at=as_utc(track.verified_at),
        fingerprint_state=enum_value(getattr(meta, "fingerprint_state", None)) or None,
    )
    return out


def download_target_format_id(artist: Any = None) -> int:
    """The Qobuz ``format_id`` a download started right now would ask for.

    The artist's quality profile (or the global default), clamped to the formats
    the subscription actually entitles us to — the same two steps
    :meth:`app.core.downloader.AlbumDownloader._resolve_format_id` takes, so the
    *Upgrade* button and the download loop cannot disagree.

    When no client is wired up the entitlement clamp is skipped, which is also
    what ``QobuzClient.allowed_format_ids()`` does before login: assume
    everything is allowed and let the answer Qobuz returns be the truth.
    """
    settings = get_settings()
    profile = getattr(artist, "quality_profile", None) or settings.default_quality_profile
    requested = quality.format_for_profile(profile, settings.default_format_id)

    best = getattr(get_qobuz_client(), "best_format_id", None)
    if callable(best):
        try:
            return int(best(requested))
        except Exception as exc:  # noqa: BLE001 - a read model must never 500
            logger.debug("best_format_id(%s) failed: %s", requested, exc)
    return requested


def _live_queue_state(album: Album) -> QueueState | None:
    """The album's in-flight queue state, or ``None`` when it is not queued.

    A *downloaded* album keeps its ``DOWNLOADED`` status while an upgrade sits
    in the queue — ``queue_album()`` only promotes wanted-ish albums — so the
    album status alone cannot tell a table whether to offer the button again.
    """
    for item in getattr(album, "queue_items", None) or ():
        if item.state in (QueueState.PENDING, QueueState.ACTIVE):
            return item.state
    return None


def album_to_out(
    album: Album,
    *,
    include_tracks: bool = False,
    meta: AlbumMetadata | None = None,
    track_meta: dict[str, TrackMetadata] | None = None,
    corrupt_tracks: int = 0,
) -> AlbumOut:
    """Convert an :class:`Album` row into :class:`AlbumOut`.

    ``include_tracks`` requires the ``tracks`` relationship to be loaded; it is
    ``lazy="selectin"``, so it always is for rows fetched through the ORM — and
    so is ``queue_items``, which is why the quality comparison below costs no
    extra queries.

    *meta* is passed in rather than read off a relationship — see
    :func:`artist_to_out` for why. Omitting it yields an un-enriched album.
    *track_meta* is the same arrangement one level down, keyed by track id, and
    only the callers that ask for the track list need supply it.

    *corrupt_tracks* is counted by the async collection builders in one grouped
    statement per page rather than here, because the fingerprint verdict lives in
    ``track_metadata`` and this function has no session to reach it with. A
    caller that does not supply it gets ``0``, which is the truth for very nearly
    every release — but read it as "not counted" rather than as "checked and
    clean".
    """
    out = AlbumOut.model_validate(album)
    out.year = album.year
    artist = getattr(album, "artist", None)
    out.artist_name = getattr(artist, "name", None)
    if include_tracks:
        meta_by_track = track_meta or {}
        out.tracks = [
            track_to_out(track, meta=meta_by_track.get(str(track.id)))
            for track in album.tracks
        ]

    out.queue_state = _live_queue_state(album)
    tracks = getattr(album, "tracks", None)
    # Provenance is stated, never inferred: "no track rows" used to mean "the
    # scanner adopted this", and stopped meaning it the moment the disk scan
    # started writing a row per file — which is exactly the population the
    # sentence is about.
    out.adopted = not any(
        track.origin is TrackOrigin.DOWNLOAD for track in (tracks or ())
    )
    out.integrity_state = album_integrity_state(tracks or ())
    out.corrupt_tracks = int(corrupt_tracks)
    out.owned_format_id = quality.owned_format_id(tracks)
    # The hi-res flag for what we HOLD. ``Album.hires`` rides along beside it and
    # means something else entirely (the catalogue would sell a hi-res copy), so
    # the two must never be read for each other.
    out.owned_hires = quality.is_hires(out.owned_format_id)
    if album.status is AlbumStatus.DOWNLOADED:
        upgrade = quality.upgrade_available(
            album, tracks, download_target_format_id(artist)
        )
        out.upgrade_format_id = upgrade[1] if upgrade else None
    _apply_album_metadata(out, album, meta)
    return out


def _apply_completeness(out: AlbumOut, album: Album) -> None:
    """How much of this release is actually on disk.

    Still three-valued, and the third value still matters — but it now means what
    it says. "No track rows" used to cover two very different situations,
    because only the download loop wrote rows: an album nobody had looked at, and
    an album the scanner had walked file by file. Reporting the second as zero
    would have shown a complete album as empty, so both were answered from the
    album's status instead.

    The scan records what it finds now, so a downloaded album that has been
    scanned can be counted honestly, including when the count is bad news: nine
    files where the catalogue says twelve is a real gap, and the old blanket
    ``True`` hid exactly that. ``None`` is reserved for a release nothing has
    counted yet.
    """
    tracks = getattr(album, "tracks", None) or ()
    if not tracks:
        # Downloaded but never scanned and never fetched track-by-track: the only
        # evidence is the status, and it says the files are there.
        out.complete = True if album.status is AlbumStatus.DOWNLOADED else None
        return

    present = sum(
        1
        for track in tracks
        if track.status is TrackStatus.DOWNLOADED and track.path
    )
    out.tracks_on_disk = present
    expected = album.tracks_count or len(tracks)
    out.complete = present >= expected if expected else None


def _apply_album_metadata(
    out: AlbumOut, album: Album, meta: AlbumMetadata | None
) -> None:
    """Merge ``album_metadata`` onto the read model.

    Note what is *not* here: nothing overwrites ``label``, ``genre`` or
    ``release_date``. All three are naming-template tokens, so a "better" value
    on this screen would make the next re-file want to move the folder. The
    enriched values are exposed alongside them instead — ``genres`` next to
    ``genre``, ``catalog_number`` as its own field.
    """
    out.cover_url = album.image_url
    _apply_completeness(out, album)
    # Without a release-group id, editions still group by normalised title —
    # which is what makes grouping work at all for the 76% of releases no source
    # has matched yet.
    out.release_group_key = dedupe_key(album.title) or album.id
    if meta is None:
        return

    out.barcode = meta.barcode
    out.mb_release_mbid = meta.mb_release_mbid
    out.mb_release_group_mbid = meta.mb_release_group_mbid
    out.deezer_album_id = meta.deezer_album_id
    out.catalog_number = meta.catalog_number
    out.mb_country = meta.country
    out.genres = meta.genre_list
    out.suggested_release_type = meta.suggested_release_type
    out.qobuz_release_type = meta.qobuz_release_type
    out.release_type_disagrees = bool(
        meta.suggested_release_type
        and meta.suggested_release_type != album.release_type
    )
    if meta.mb_release_group_mbid:
        out.release_group_key = meta.mb_release_group_mbid

    external = meta.cover_url
    if external and (get_settings().enrichment_prefer_external_cover or not album.image_url):
        out.cover_url = external

    out.enriched = bool(
        meta.mb_release_mbid or meta.mb_release_group_mbid or meta.deezer_album_id
    )


def queue_item_to_out(item: QueueItem) -> QueueItemOut:
    """Convert a :class:`QueueItem` row into :class:`QueueItemOut`."""
    out = QueueItemOut.model_validate(item)
    out.progress_percent = item.progress_percent
    album = getattr(item, "album", None)
    if album is not None:
        out.album_title = album.display_title
        out.album_image_url = album.image_url
        out.album_monitored = bool(album.monitored)
        out.artist_id = album.artist_id
        out.artist_name = getattr(getattr(album, "artist", None), "name", None)
    return out


def activity_to_out(entry: Activity) -> ActivityOut:
    """Convert an :class:`Activity` row into :class:`ActivityOut`."""
    out = ActivityOut.model_validate(entry)
    out.artist_name = getattr(getattr(entry, "artist", None), "name", None)
    album = getattr(entry, "album", None)
    out.album_title = album.display_title if album is not None else None
    return out


# ---------------------------------------------------------------------------
# Read models: collections
# ---------------------------------------------------------------------------
_ARTIST_SORTS: dict[str, Any] = {
    "name": Artist.name,
    "added_at": Artist.added_at,
    "last_checked_at": Artist.last_checked_at,
    "albums_count": Artist.albums_count,
}


async def artist_album_counts(
    session: AsyncSession, artist_ids: Iterable[str]
) -> dict[str, dict[str, int]]:
    """Per-artist album roll-ups keyed by artist id.

    Returns ``{artist_id: {"albums": n, "wanted": n, "downloaded": n}}``.
    """
    ids = [str(a) for a in artist_ids]
    if not ids:
        return {}
    stmt = (
        select(Album.artist_id, Album.status, func.count(Album.id))
        .where(Album.artist_id.in_(ids))
        .group_by(Album.artist_id, Album.status)
    )
    rows = (await session.execute(stmt)).all()
    counts: dict[str, dict[str, int]] = {
        artist_id: {"albums": 0, "wanted": 0, "downloaded": 0} for artist_id in ids
    }
    for artist_id, status, count in rows:
        bucket = counts.setdefault(
            artist_id, {"albums": 0, "wanted": 0, "downloaded": 0}
        )
        bucket["albums"] += count
        if status in (AlbumStatus.WANTED, AlbumStatus.QUEUED, AlbumStatus.DOWNLOADING):
            bucket["wanted"] += count
        elif status is AlbumStatus.DOWNLOADED:
            bucket["downloaded"] += count
    return counts


async def list_artists(
    session: AsyncSession,
    *,
    query: str | None = None,
    monitored: bool | None = None,
    sort: str = "name",
    order: str = "asc",
    limit: int = 200,
    offset: int = 0,
    with_counts: bool = True,
) -> tuple[list[ArtistOut], int]:
    """Page through followed artists, newest sort options first.

    Returns ``(items, total)``.
    """
    stmt: Select[Any] = select(Artist)
    count_stmt = select(func.count(Artist.id))
    if query:
        pattern = f"%{query.strip()}%"
        stmt = stmt.where(Artist.name.ilike(pattern))
        count_stmt = count_stmt.where(Artist.name.ilike(pattern))
    if monitored is not None:
        stmt = stmt.where(Artist.monitored.is_(monitored))
        count_stmt = count_stmt.where(Artist.monitored.is_(monitored))

    column = _ARTIST_SORTS.get(sort, Artist.name)
    stmt = stmt.order_by(column.desc() if order == "desc" else column.asc())
    stmt = stmt.limit(max(1, limit)).offset(max(0, offset))

    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())

    counts = (
        await artist_album_counts(session, [row.id for row in rows])
        if with_counts
        else {}
    )
    meta = await load_artist_metadata(session, [row.id for row in rows])
    items = [
        artist_to_out(
            row,
            album_count=counts.get(row.id, {}).get("albums"),
            wanted_count=counts.get(row.id, {}).get("wanted"),
            downloaded_count=counts.get(row.id, {}).get("downloaded"),
            meta=meta.get(str(row.id)),
        )
        for row in rows
    ]
    return items, total


async def list_albums_for_artist(
    session: AsyncSession,
    artist_id: str,
    *,
    status: AlbumStatus | None = None,
    release_type: str | None = None,
    query: str | None = None,
    limit: int = 500,
    offset: int = 0,
) -> tuple[list[AlbumOut], int]:
    """All known releases for one artist, newest first.

    ``query`` is a case-insensitive substring match over the release title, its
    edition/version and its label — the three things you actually remember when
    hunting for one album in a 200-release discography.
    """
    stmt = select(Album).where(Album.artist_id == str(artist_id))
    count_stmt = select(func.count(Album.id)).where(Album.artist_id == str(artist_id))
    if status is not None:
        stmt = stmt.where(Album.status == status)
        count_stmt = count_stmt.where(Album.status == status)
    if release_type:
        stmt = stmt.where(Album.release_type == release_type)
        count_stmt = count_stmt.where(Album.release_type == release_type)
    if query and query.strip():
        pattern = f"%{query.strip()}%"
        matches = (
            Album.title.ilike(pattern)
            | Album.version.ilike(pattern)
            | Album.label.ilike(pattern)
        )
        stmt = stmt.where(matches)
        count_stmt = count_stmt.where(matches)
    stmt = (
        stmt.order_by(Album.release_date.desc().nullslast(), Album.title.asc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return await albums_to_out(session, rows), total


async def artist_stats(session: AsyncSession, artist: Artist) -> ArtistStatsOut:
    """One artist's release roll-up, counted per status.

    The counts are taken in Python over the artist's whole discography rather
    than as a ``GROUP BY``, because the same page needs the roll-ups on the
    :class:`ArtistOut` *and* a count for every status the filter offers, and one
    pass answering both cannot disagree with itself.

    Zero-filling is the load-bearing detail. These numbers label the options of a
    status ``<select>``, and a status with no albums has no row in a grouped
    query — so the option renders with a hole where its count belongs rather
    than with a nought.

    ``wanted_count`` is ``wanted + queued``: the releases this artist is waiting
    on, counting the ones already handed to the worker. It is deliberately not
    :data:`MISSING_STATUSES` (which is the *monitored* backlog, and includes
    failures) nor ``LibraryStatsOut.wanted_albums`` (which adds ``downloading``).
    Three definitions of "wanted" exist; each is right for its own screen and
    conflating them is how a header stops matching the table under it.
    """
    albums, _ = await list_albums_for_artist(session, artist.id, limit=2000)
    counts: dict[str, int] = {status.value: 0 for status in AlbumStatus}
    for album in albums:
        counts[enum_value(album.status)] = counts.get(enum_value(album.status), 0) + 1

    total = len(albums)
    wanted = counts[AlbumStatus.WANTED.value] + counts[AlbumStatus.QUEUED.value]
    downloaded = counts[AlbumStatus.DOWNLOADED.value]
    return ArtistStatsOut(
        artist=await artist_out(
            session,
            artist,
            album_count=total,
            wanted_count=wanted,
            downloaded_count=downloaded,
        ),
        counts=counts,
        total_albums=total,
        wanted_count=wanted,
        downloaded_count=downloaded,
        # A ratio of nothing is not zero: an artist with no known releases has
        # not got 0% of them on disk, the question does not apply.
        on_disk_ratio=(downloaded / total) if total else None,
    )


#: Album states that mean "we want this and it is not on disk". ``QUEUED`` and
#: ``DOWNLOADING`` are deliberately excluded — those are already in flight and
#: belong on the queue page, not the backlog.
MISSING_STATUSES: tuple[AlbumStatus, ...] = (AlbumStatus.WANTED, AlbumStatus.FAILED)


async def list_wanted_albums(
    session: AsyncSession,
    *,
    query: str | None = None,
    status: AlbumStatus | None = None,
    monitored: bool | None = True,
    limit: int = 500,
    offset: int = 0,
) -> tuple[list[AlbumOut], int]:
    """The cross-artist backlog: releases wanted but not yet on disk.

    Ordered oldest-wanted first, so the things that have been waiting longest
    surface at the top. ``status=None`` means "any missing state" (see
    :data:`MISSING_STATUSES`); pass a specific one to narrow it.

    Returns ``(items, total)``.
    """
    wanted = (status,) if status is not None else MISSING_STATUSES
    filters = [Album.status.in_(wanted)]
    if monitored is not None:
        filters.append(Album.monitored.is_(monitored))
    if query:
        pattern = f"%{query.strip()}%"
        filters.append(Album.title.ilike(pattern) | Artist.name.ilike(pattern))

    stmt = select(Album).join(Artist, Artist.id == Album.artist_id).where(*filters)
    count_stmt = (
        select(func.count(Album.id))
        .select_from(Album)
        .join(Artist, Artist.id == Album.artist_id)
        .where(*filters)
    )
    stmt = (
        stmt.order_by(Album.added_at.asc(), Album.title.asc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return await albums_to_out(session, rows), total


async def list_recent_releases(
    session: AsyncSession,
    *,
    monitored: bool | None = None,
    limit: int = 25,
    offset: int = 0,
) -> tuple[list[AlbumOut], int]:
    """Releases by **release date**, newest first — the radar's actual subject.

    This is the one list in the API ordered by when the music came out rather
    than by anything Qobuzarr did to it, and that distinction is the whole
    reason it exists. The Release radar used to draw the download queue under a
    heading that said *New & upcoming*, which is a different list wearing the
    right label: the queue is ordered by the moment somebody pressed Download,
    so a 1975 remaster fetched this morning outranks a record released last
    week, and a radar nobody has downloaded from is empty.

    Two rules make the answer honest:

    **A release with no date is not new, it is undated**, so ``release_date IS
    NULL`` is excluded rather than sorted to one end. ``nullslast()`` would park
    them at the bottom, which reads as "released longest ago" about releases
    whose date nobody knows — and on this library that is a large minority of
    the catalogue, so the tail of any page would be pure noise. They are absent
    from ``total`` for the same reason: a count that includes rows the ordering
    cannot place is a promise of pages that never arrive.

    **Future dates are kept.** Qobuz dates announced releases ahead of time and
    ``desc`` puts them at the top, which is exactly what the *upcoming* half of
    the heading means. Nothing filters on ``date.today()`` — a release dated
    tomorrow is the single most interesting row on the screen.

    ``monitored`` filters on the **album** flag, matching every other list here;
    ``None`` (the default) applies no filter, because a radar that hides the
    releases somebody has already ignored is hiding the evidence they were
    ignored. Returns ``(items, total)``.
    """
    filters = [Album.release_date.is_not(None)]
    if monitored is not None:
        filters.append(Album.monitored.is_(monitored))

    stmt = (
        select(Album)
        .where(*filters)
        # Ties are common and not rare enough to leave to the database: a label
        # dates every release of a reissue campaign to the same Friday. Falling
        # back to the moment the row was learned keeps a page stable between
        # two polls, which an unordered tie does not.
        .order_by(
            Album.release_date.desc(),
            Album.added_at.desc(),
            Album.title.asc(),
        )
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    count_stmt = select(func.count(Album.id)).select_from(Album).where(*filters)
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return await albums_to_out(session, rows), total


async def album_corrupt_counts(
    session: AsyncSession, album_ids: Sequence[str]
) -> dict[str, int]:
    """How many unplayable files each of *album_ids* is holding.

    One grouped statement keyed on the **albums** of a page, never on their
    tracks. Loading the tracks' metadata instead would be the obvious thing and
    is the wrong shape twice over: a 500-release page is thousands of track ids,
    which is an ``IN`` list long enough to meet SQLite's variable limit, and all
    that is wanted from it is a count.

    Albums with nothing corrupt are simply absent from the result, which is what
    every caller wants — the answer is ``0`` and the dict comprehension supplies
    it.
    """
    ids = [str(value) for value in album_ids if value]
    if not ids:
        return {}
    rows = (
        await session.execute(
            select(Track.album_id, func.count(Track.id))
            .join(TrackMetadata, TrackMetadata.track_id == Track.id)
            .where(
                Track.album_id.in_(ids),
                TrackMetadata.fingerprint_state == FingerprintState.CORRUPT,
                Track.path.is_not(None),
            )
            .group_by(Track.album_id)
        )
    ).all()
    return {str(album_id): int(count) for album_id, count in rows}


async def artist_out(session: AsyncSession, artist: Artist, **kwargs: Any) -> ArtistOut:
    """:func:`artist_to_out` with the enrichment fetched for one row."""
    meta = await load_artist_metadata(session, [artist.id])
    return artist_to_out(artist, meta=meta.get(str(artist.id)), **kwargs)


async def album_out(
    session: AsyncSession, album: Album, *, include_tracks: bool = False, **kwargs: Any
) -> AlbumOut:
    """:func:`album_to_out` with the enrichment fetched for one row.

    The per-track metadata is loaded only when the track list is being built:
    it is one statement bounded by a single release's track count, and nothing
    else in the payload reads it.
    """
    meta = await load_album_metadata(session, [album.id])
    track_meta: dict[str, TrackMetadata] | None = None
    if include_tracks:
        track_meta = await load_track_metadata(
            session, [str(track.id) for track in album.tracks]
        )
    corrupt = await album_corrupt_counts(session, [album.id])
    return album_to_out(
        album,
        include_tracks=include_tracks,
        meta=meta.get(str(album.id)),
        track_meta=track_meta,
        corrupt_tracks=corrupt.get(str(album.id), 0),
        **kwargs,
    )


async def albums_to_out(
    session: AsyncSession, rows: Sequence[Album], *, include_tracks: bool = False
) -> list[AlbumOut]:
    """Build read models for a page of albums, enrichment included.

    Two extra statements for the whole page, which is the reason neither the
    metadata nor the corruption count is an ORM relationship: a ``selectin``
    relationship would cost the same query while also making the synchronous
    builders liable to ``MissingGreenlet`` on freshly-committed rows.
    """
    if not rows:
        return []
    ids = [row.id for row in rows]
    meta = await load_album_metadata(session, ids)
    corrupt = await album_corrupt_counts(session, ids)
    return [
        album_to_out(
            row,
            include_tracks=include_tracks,
            meta=meta.get(str(row.id)),
            corrupt_tracks=corrupt.get(str(row.id), 0),
        )
        for row in rows
    ]


def _record_key_pairs(
    items: Sequence[tuple[str, str | None, str | None]],
) -> dict[str, str]:
    """Assign every album to a record, given ``(id, mbid, title_key)`` triples.

    One rule, two callers — :func:`list_release_group` and :func:`group_editions`
    must agree about what "the same record" is or the artist page and the release
    page contradict each other.

    Grouping by whichever key an album happens to carry is not enough, and stops
    being enough the moment enrichment is scoped to the library. Only albums on
    disk are ever enriched, so the copy you own carries a release-group MBID and
    its catalogue-only siblings never will: keyed naively, *Rumours* splits into
    the one you have and the ones you do not, and the page for the copy you own
    is the one that claims the record has no other editions. That half-enriched
    state used to be transient. It is now permanent and normal.

    So the equivalence is deliberately asymmetric:

    * two albums that **both** carry an MBID are the same record only when the
      MBIDs match — MusicBrainz has already said they are different records, and
      two distinct release groups sharing a normalised title (a self-titled debut
      and its reissue under the same name) must not be merged on that evidence;
    * an album with **no** MBID joins an MBID-keyed record when its normalised
      title matches that record's, because nothing better is available and the
      title is the same evidence the de-duplicator already acts on;
    * unless the title is claimed by more than one MBID-keyed record, in which
      case there is no honest way to choose and the unmatched albums stay in a
      title-keyed record of their own.

    Returns ``{album_id: record_key}``. The key is the MBID where one is known
    and the normalised title otherwise, so it stays stable as enrichment lands
    and both kinds round-trip through ``/release-groups/{key}``.
    """
    by_mbid: dict[str, list[str]] = {}
    titles_of: dict[str, set[str]] = {}
    loose: list[tuple[str, str]] = []
    for album_id, mbid, title_key in items:
        if mbid:
            by_mbid.setdefault(mbid, []).append(album_id)
            if title_key:
                titles_of.setdefault(mbid, set()).add(title_key)
        elif title_key:
            loose.append((album_id, title_key))

    claimed: dict[str, set[str]] = {}
    for mbid, keys in titles_of.items():
        for title_key in keys:
            claimed.setdefault(title_key, set()).add(mbid)

    assigned: dict[str, str] = {}
    for mbid, album_ids in by_mbid.items():
        for album_id in album_ids:
            assigned[album_id] = mbid
    for album_id, title_key in loose:
        owners = claimed.get(title_key, ())
        # next(iter(...)), not .pop(): the set is shared by every album with this
        # title, and popping would hand the record to the first of them and leave
        # the rest looking unclaimed.
        assigned[album_id] = next(iter(owners)) if len(owners) == 1 else title_key
    return assigned


async def list_release_group(
    session: AsyncSession, key: str, *, artist_id: str | None = None
) -> tuple[list[AlbumOut], str]:
    """Every edition of one record, best first.

    Editions are grouped by MusicBrainz release-group id when it is known and by
    normalised title otherwise. The fallback is what makes grouping work at all
    for the majority of a library that nothing has matched yet — and it is the
    same normaliser the edition de-duplicator already trusts, so the two agree
    about what counts as "the same record".

    ``key`` is matched against both, because a caller holding one kind of key
    should not have to know which kind it is — and because the two kinds now
    coexist permanently within one record, the match is resolved through
    :func:`_record_key_pairs` rather than by comparing the key to each row.
    """
    stmt = select(Album)
    if artist_id:
        stmt = stmt.where(Album.artist_id == str(artist_id))
    rows = list((await session.execute(stmt)).unique().scalars().all())
    meta = await load_album_metadata(session, [row.id for row in rows])

    wanted = str(key)
    assigned = _record_key_pairs(
        [
            (
                str(row.id),
                getattr(meta.get(str(row.id)), "mb_release_group_mbid", None),
                dedupe_key(row.title),
            )
            for row in rows
        ]
    )
    # The caller may hold either kind of key for a record that is now filed under
    # the other, so resolve the key to a record before collecting its editions.
    record = assigned.get(wanted)
    if record is None:
        for row in rows:
            row_id = str(row.id)
            if (
                getattr(meta.get(row_id), "mb_release_group_mbid", None) == wanted
                or dedupe_key(row.title) == wanted
            ):
                record = assigned.get(row_id)
                break
    editions = [row for row in rows if assigned.get(str(row.id)) == record] if record else []
    if not editions:
        return [], ""

    # Best first, using the same ranking the de-duplicator applies: track count
    # before audio quality, because a one-track hi-res promo must never outrank
    # the album it was cut from.
    from app.core.indexer import edition_rank  # noqa: PLC0415 - one use

    editions.sort(key=edition_rank, reverse=True)
    title = editions[0].title
    return [
        album_to_out(row, meta=meta.get(str(row.id))) for row in editions
    ], title


def _release_group_ref(key: str, editions: Sequence[AlbumOut]) -> ReleaseGroupRefOut:
    """Name the record a set of editions belongs to.

    The title comes from the best edition, matching what
    :func:`list_release_group` already returns. The MBID does **not**: it is
    taken from the first edition that carries one, wherever in the ranking that
    edition happens to sit.

    Reading it off ``editions[0]`` is the obvious thing and it is wrong most of
    the time, because the two orderings are unrelated. ``edition_rank`` sorts by
    track count and audio quality, while the MBID is only ever on a release that
    is *on disk* — enrichment's whole subject is the library — so the identified
    edition is routinely outranked by a catalogue-only sibling with the same
    track count. The record then reports ``None`` and the screen says "nothing
    has identified this record yet" about a record MusicBrainz has already
    named. ``_record_key_pairs`` guarantees every MBID-carrying edition of one
    record shares one MBID, so any of them is the record's answer.

    ``None`` is still a real state and still worth rendering — it means the
    grouping is by normalised title — but only when no edition has one.
    """
    best = editions[0] if editions else None
    return ReleaseGroupRefOut(
        key=key,
        title=getattr(best, "title", "") or "",
        mb_release_group_mbid=next(
            (
                edition.mb_release_group_mbid
                for edition in editions
                if edition.mb_release_group_mbid
            ),
            None,
        ),
    )


async def release_group_out(
    session: AsyncSession, key: str, *, artist_id: str | None = None
) -> ReleaseGroupOut:
    """:func:`list_release_group` as a read model, editions and all.

    The *requested* key is echoed back rather than the record key it resolved
    to. Both are accepted on the way in — a bookmark made before enrichment
    landed carries a title key for a record now filed under an MBID — so echoing
    the resolution would silently change the URL under a client that is holding
    the other one, for no gain: :func:`list_release_group` already resolves
    either through :func:`_record_key_pairs`, which is the one place allowed to
    decide what "the same record" means.

    Empty ``editions`` means no such record. The caller turns that into a 404;
    this returns the empty shape rather than raising, because it is a read model
    and read models do not own status codes.
    """
    editions, _title = await list_release_group(session, key, artist_id=artist_id)
    ref = _release_group_ref(str(key), editions)
    best = editions[0] if editions else None
    return ReleaseGroupOut(
        key=ref.key,
        title=ref.title,
        mb_release_group_mbid=ref.mb_release_group_mbid,
        artist_id=getattr(best, "artist_id", None) or (str(artist_id) if artist_id else None),
        artist_name=getattr(best, "artist_name", None),
        total=len(editions),
        editions=editions,
    )


def _consensus_field(payload: Any) -> ConsensusFieldOut:
    """One decoded vote record, tolerating whatever is in the column.

    ``consensus_json`` is written by a previous version of this program and read
    by this one, so every field is defaulted: a record missing ``votes`` is a
    record with no votes, not a 500 on a release-detail page.
    """
    if not isinstance(payload, dict):
        return ConsensusFieldOut()
    votes = payload.get("votes")
    return ConsensusFieldOut(
        value=payload.get("value"),
        agreed=int(payload.get("agreed") or 0),
        total=int(payload.get("total") or 0),
        share=float(payload.get("share") or 0.0),
        # Decided, not recomputed from the arithmetic. A joint-first placing is a
        # tie however many sources voted, and whether a tie counts is
        # ``app.enrich.merge``'s call — it stored the answer, so read it.
        majority=payload.get("value") is not None,
        votes=[
            ConsensusVoteOut(source=str(source), value=value)
            for source, value in sorted((votes or {}).items())
        ]
        if isinstance(votes, dict)
        else [],
    )


async def album_consensus(
    session: AsyncSession, album_id: str, *, meta: AlbumMetadata | None = None
) -> dict[str, ConsensusFieldOut]:
    """What each source said about each field of one release.

    Pass *meta* when the caller already has it — the release screen loads the
    metadata anyway, and re-reading it here would be a second statement for the
    same row.
    """
    from app.enrich.merge import decode_consensus  # noqa: PLC0415 - one use

    if meta is None:
        meta = (await load_album_metadata(session, [str(album_id)])).get(str(album_id))
    decoded = decode_consensus(getattr(meta, "consensus_json", None))
    return {str(name): _consensus_field(payload) for name, payload in decoded.items()}


async def album_detail(session: AsyncSession, album: Album) -> AlbumDetailOut:
    """One release, its sibling editions and the vote record, in one pass.

    Assembled server-side because two of the three parts cannot be built from
    the album alone: the edition list needs the whole-catalogue keying pass in
    :func:`_record_key_pairs`, and the consensus lives in a column nothing else
    returns. The subject is *included* in ``editions`` — the screen marks it
    "this one" rather than showing it twice, and a list that excluded it could
    not say how many editions the record has without adding one back.
    """
    meta = (await load_album_metadata(session, [album.id])).get(str(album.id))
    track_meta = await load_track_metadata(
        session, [str(track.id) for track in album.tracks]
    )
    corrupt = await album_corrupt_counts(session, [album.id])
    out = album_to_out(
        album,
        include_tracks=True,
        meta=meta,
        track_meta=track_meta,
        corrupt_tracks=corrupt.get(str(album.id), 0),
    )
    key = out.release_group_key or album.id
    editions, _title = await list_release_group(
        session, key, artist_id=album.artist_id
    )
    return AlbumDetailOut(
        album=out,
        release_group=_release_group_ref(str(key), editions),
        editions=editions,
        consensus=await album_consensus(session, album.id, meta=meta),
    )


def group_editions(albums: Sequence[AlbumOut]) -> list[dict[str, Any]]:
    """Collapse a list of releases into one entry per record.

    Six rows for six editions of *Rumours* is six chances to download the wrong
    one. This keeps the best edition visible and folds the rest underneath it,
    with a count of how many are on disk.

    Pure — it groups what it is given and queries nothing, so the caller decides
    what "given" means (one artist's discography, a filtered view, a search).

    It groups through :func:`_record_key_pairs` rather than on each album's own
    ``release_group_key``, so that the edition you own — the only one enrichment
    ever reaches — stays in the same record as the catalogue-only editions beside
    it instead of splitting off into a group of one.
    """
    assigned = _record_key_pairs(
        [
            (album.id, album.mb_release_group_mbid, dedupe_key(album.title))
            for album in albums
        ]
    )
    groups: dict[str, dict[str, Any]] = {}
    for album in albums:
        key = assigned.get(album.id) or album.release_group_key or album.id
        group = groups.get(key)
        if group is None:
            group = {
                "key": key,
                "title": album.title,
                "artist_id": album.artist_id,
                "artist_name": album.artist_name,
                "release_type": album.release_type,
                "year": album.year,
                "cover_url": album.cover_url or album.image_url,
                "editions": [],
                "downloaded": 0,
                "identified": False,
            }
            groups[key] = group
        group["editions"].append(album)
        if album.status is AlbumStatus.DOWNLOADED:
            group["downloaded"] += 1
        if album.enriched:
            group["identified"] = True
        # The earliest year is the record's year; a 2013 remaster does not make
        # a 1977 album a 2013 one.
        if album.year and (group["year"] is None or album.year < group["year"]):
            group["year"] = album.year

    for group in groups.values():
        group["total"] = len(group["editions"])
        group["best"] = group["editions"][0]
    return list(groups.values())


async def nav_counts(session: AsyncSession) -> dict[str, int]:
    """Badge numbers for the sidebar: followed artists, backlog, queue depth."""
    artists = int((await session.execute(select(func.count(Artist.id)))).scalar_one())
    wanted = int(
        (
            await session.execute(
                select(func.count(Album.id)).where(
                    Album.status.in_(MISSING_STATUSES), Album.monitored.is_(True)
                )
            )
        ).scalar_one()
    )
    queued = int(
        (
            await session.execute(
                select(func.count(QueueItem.id)).where(
                    QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE))
                )
            )
        ).scalar_one()
    )
    # The trash badge is a nudge, not a metric: files sitting there still take
    # up disk. Counting directory entries is cheap and never touches the DB.
    try:
        trash_root = get_settings().trash_dir
        trash = sum(1 for entry in trash_root.iterdir() if entry.is_dir())
    except OSError:
        trash = 0
    # How many entities the matchers refused to guess at. A badge because the
    # review list is the only place they surface: "exact or nothing" is silent
    # failure unless somebody is told there is something to look at. Counted
    # over ``in_library()`` because that is what the list itself shows — a badge
    # promising more rows than the page can display is worse than no badge.
    #
    # The states come from ``enricher.REVIEW_STATES`` rather than from a literal
    # here, because this count and ``list_review_items``' default are one rule
    # written in two files: a state added to one and not the other is a badge
    # that disagrees with the page it links to. ``rejected`` is outside it by
    # construction — a row somebody dismissed must stop being nagged about, and
    # a badge is nothing but a nag.
    # ``actionable_review_clause`` for the same reason the states come from
    # ``REVIEW_STATES``: it is the other half of what the list shows. A badge
    # counting rows the page filters out is the same disagreement in a new
    # place, and it reads worse — a number that never goes down no matter how
    # much of the list you work through.
    review = int(
        (
            await session.execute(
                select(func.count(EnrichmentState.entity_id)).where(
                    EnrichmentState.state.in_(REVIEW_STATES),
                    in_library(),
                    actionable_review_clause(),
                )
            )
        ).scalar_one()
        or 0
    )
    return {
        "artists": artists,
        "wanted": wanted,
        "queue": queued,
        "trash": trash,
        "enrichment_review": review,
    }


async def nav_counts_out(session: AsyncSession) -> NavCountsOut:
    """:func:`nav_counts` as a typed model, for the JSON API.

    A wrapper and nothing more, on purpose: the two definitions of "wanted" in
    this codebase differ by three album states, and the moment the badge counts
    them itself it is a fourth definition waiting to disagree with the page it
    links to.
    """
    return NavCountsOut(**await nav_counts(session))


async def library_scan_status(session: AsyncSession, settings: Settings) -> dict[str, Any]:
    """Everything the Disk scan page needs before a scan has been triggered.

    Degrades to "never scanned" when the application state is not wired up, so
    the page renders on a half-started process exactly like every other read
    model in this module.
    """
    from app.core.scanner import load_last_scan  # noqa: PLC0415 - keeps app.api import-light

    scanner = get_library_scanner()
    try:
        last = await load_last_scan(session)
    except Exception:  # noqa: BLE001 - a corrupt stored row must not 500 the page
        logger.warning("Could not read the stored library-scan summary", exc_info=True)
        last = None

    return {
        "running": bool(getattr(scanner, "running", False)),
        "available": scanner is not None,
        "library_path": str(settings.library_path),
        "nightly": settings.library_scan_nightly,
        "complete_ratio": settings.library_scan_complete_ratio,
        "last": last,
    }


def integrity_report_out(payload: dict[str, Any] | None) -> IntegrityReportOut | None:
    """Turn one :meth:`app.core.scheduler.IntegrityReport.as_dict` into a model.

    The dict is flat — the per-state counts sit beside the totals — because that
    is the shape the housekeeping summary and the stored ``settings`` row want.
    Here they are lifted back into a ``states`` map and zero-filled, so a client
    can iterate the five states without discovering that ``replaced`` is simply
    absent on a pass that found none.

    ``None`` in, ``None`` out: nothing has run yet is a state to render, not a
    zeroed report to fabricate.
    """
    if not isinstance(payload, dict):
        return None
    return IntegrityReportOut(
        checked=int(payload.get("checked") or 0),
        hashed=int(payload.get("hashed") or 0),
        baselined=int(payload.get("baselined") or 0),
        albums_restamped=int(payload.get("albums_restamped") or 0),
        albums_reopened=int(payload.get("albums_reopened") or 0),
        elapsed=float(payload.get("elapsed") or 0.0),
        applied=bool(payload.get("applied", True)),
        states={
            state.value: int(payload.get(state.value) or 0) for state in IntegrityState
        },
        finished_at=as_utc(payload.get("finished_at")),
    )


async def integrity_status(
    session: AsyncSession, settings: Settings | None = None
) -> IntegrityStatusOut:
    """What the database records about the files it believes it has.

    Recorded, never measured. Answering this by reading the library would be
    minutes of blocking disk I/O inside a GET — see :mod:`app.core.integrity`,
    where the whole design is arranged around hashing being the expensive thing
    that runs rarely.

    Two populations are counted here and the difference is deliberate.
    ``tracks_total`` and the ``unknown``/``verified`` buckets are the rows that
    *claim to have a file* — ``downloaded`` with a path, which is exactly
    ``_integrity_candidates``'s filter, so these numbers describe what a pass
    would actually do. ``missing`` counts rows that carry a baseline and no
    longer point at a file, which is by definition **not** one of those rows and
    so is not part of ``tracks_total``.

    ``retagged`` and ``replaced`` are always zero here and that is not a bug: a
    pass re-baselines what it classifies, so no row is ever left recording a
    disagreement with its own file. Those two live on ``last_result``, which is a
    statement about a run.

    ``corrupt_files`` counts what the quarantine would act on, so it leaves out
    releases with ``Album.mute_integrity`` set — the same exclusion
    :func:`app.core.librarian.quarantine_corrupt_files` makes, for the same
    reason, so the figure and the button cannot disagree. Those files are counted
    separately as ``corrupt_muted`` rather than dropped: muting hides the alarm,
    not the fact.
    """
    from app.core.scheduler import (  # noqa: PLC0415 - keeps app.api import-light
        integrity_running,
        load_last_integrity,
    )

    settings = effective_for(settings or get_settings())
    on_disk = (Track.path.is_not(None), Track.status == TrackStatus.DOWNLOADED)

    tracks_total = int(
        (
            await session.execute(select(func.count(Track.id)).where(*on_disk))
        ).scalar_one()
        or 0
    )
    never_baselined = int(
        (
            await session.execute(
                select(func.count(Track.id)).where(
                    *on_disk, Track.content_hash.is_(None)
                )
            )
        ).scalar_one()
        or 0
    )
    gone = int(
        (
            await session.execute(
                select(func.count(Track.id)).where(
                    Track.path.is_(None), Track.content_hash.is_not(None)
                )
            )
        ).scalar_one()
        or 0
    )
    # Two counts, one query each, and the split is the whole point of
    # ``Album.mute_integrity``. ``corrupt_files`` is the actionable figure — what
    # the quarantine button would move — so it must exclude muted releases or the
    # screen offers to do something and then does nothing. ``corrupt_muted`` is
    # the rest of the truth, published rather than swallowed: a suppressed alarm
    # that leaves no trace is indistinguishable from a check that never ran.
    corrupt_filters = (
        TrackMetadata.fingerprint_state == FingerprintState.CORRUPT,
        Track.path.is_not(None),
    )
    corrupt = int(
        (
            await session.execute(
                select(func.count(Track.id))
                .join(TrackMetadata, TrackMetadata.track_id == Track.id)
                .join(Album, Album.id == Track.album_id)
                .where(*corrupt_filters, Album.mute_integrity.is_not(True))
            )
        ).scalar_one()
        or 0
    )
    corrupt_muted = int(
        (
            await session.execute(
                select(func.count(Track.id))
                .join(TrackMetadata, TrackMetadata.track_id == Track.id)
                .join(Album, Album.id == Track.album_id)
                .where(*corrupt_filters, Album.mute_integrity.is_(True))
            )
        ).scalar_one()
        or 0
    )

    try:
        stored = await load_last_integrity(session)
    except Exception:  # noqa: BLE001 - a corrupt stored row reads as "never ran"
        logger.warning("Could not read the stored integrity summary", exc_info=True)
        stored = None
    last = integrity_report_out(stored)

    return IntegrityStatusOut(
        enabled=settings.integrity_enabled,
        reverify_fraction=settings.integrity_reverify_fraction,
        tracks_total=tracks_total,
        states={
            IntegrityState.UNKNOWN.value: never_baselined,
            IntegrityState.VERIFIED.value: tracks_total - never_baselined,
            IntegrityState.RETAGGED.value: 0,
            IntegrityState.REPLACED.value: 0,
            IntegrityState.MISSING.value: gone,
        },
        never_baselined=never_baselined,
        corrupt_files=corrupt,
        corrupt_muted=corrupt_muted,
        albums_reopened=last.albums_reopened if last else 0,
        last_run_at=last.finished_at if last else None,
        last_result=last,
        running=integrity_running(),
    )


async def list_corrupt_tracks(
    session: AsyncSession, *, limit: int = 200, offset: int = 0
) -> tuple[list[CorruptTrackOut], int]:
    """Files ``fpcalc`` could not decode, named well enough to act on.

    ``Track.path IS NOT NULL`` is a filter and not a nicety: a verdict about a
    file that has already been quarantined describes something that is in the
    trash, and offering to quarantine it again is offering to do nothing. It is
    also the same condition :func:`app.core.librarian.quarantine_corrupt_files`
    selects on, so the list and the button it sits above cannot disagree about
    how many files there are.

    ``Album.mute_integrity`` is part of that same condition, and for the same
    reason: the quarantine skips those releases, so listing them here would offer
    a file the button will not touch. The verdict is not hidden — it is still on
    the release's own detail payload, in ``integrity_state`` and
    ``corrupt_tracks`` — and ``IntegrityStatusOut.corrupt_muted`` says how many
    there are, so the suppression is visible even though the rows are not.

    Oldest verdict first: a corruption that has been sitting there for a month is
    the one nobody has looked at.
    """
    filters = (
        TrackMetadata.fingerprint_state == FingerprintState.CORRUPT,
        Track.path.is_not(None),
        Album.mute_integrity.is_not(True),
    )
    stmt = (
        select(Track, TrackMetadata, Album, Artist)
        .join(TrackMetadata, TrackMetadata.track_id == Track.id)
        .join(Album, Album.id == Track.album_id)
        .outerjoin(Artist, Artist.id == Album.artist_id)
        .where(*filters)
        .order_by(TrackMetadata.fingerprinted_at.asc(), Track.id.asc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    count_stmt = (
        select(func.count(Track.id))
        .select_from(Track)
        .join(TrackMetadata, TrackMetadata.track_id == Track.id)
        .join(Album, Album.id == Track.album_id)
        .where(*filters)
    )
    rows = (await session.execute(stmt)).unique().all()
    total = int((await session.execute(count_stmt)).scalar_one() or 0)
    return [
        CorruptTrackOut(
            track_id=str(track.id),
            qid=track.qid or "",
            title=track.display_title,
            album_id=str(album.id),
            album_title=album.display_title,
            artist_id=getattr(artist, "id", None),
            artist_name=getattr(artist, "name", None),
            path=str(track.path or ""),
            fingerprint_state=enum_value(meta.fingerprint_state) or "corrupt",
            last_attempt_at=as_utc(meta.fingerprinted_at),
        )
        for track, meta, album, artist in rows
    ], total


async def library_import_status(session: AsyncSession) -> dict[str, Any]:
    """Live artist-import progress, falling back to the stored last run.

    Like every accessor in this module it degrades rather than raising: with no
    importer wired up the Disk scan page still renders, just without the import
    controls.
    """
    from app.core.importer import ImportProgress, load_last_import  # noqa: PLC0415

    importer = get_library_importer()
    snapshot: dict[str, Any] | None = None
    if importer is not None and callable(getattr(importer, "snapshot", None)):
        try:
            snapshot = importer.snapshot()
        except Exception:  # noqa: BLE001 - a broken importer must not 500 the page
            logger.warning("Could not read the importer snapshot", exc_info=True)

    if snapshot is not None and (snapshot.get("running") or snapshot.get("started_at")):
        snapshot["available"] = True
        return snapshot

    try:
        stored = await load_last_import(session)
    except Exception:  # noqa: BLE001 - a corrupt stored row reads as "never ran"
        logger.warning("Could not read the stored import summary", exc_info=True)
        stored = None

    result = stored or ImportProgress().as_dict()
    result["available"] = importer is not None
    return result


#: Display order for the queue page: what is happening now, then what is next.
_QUEUE_STATE_ORDER = case(
    (QueueItem.state == QueueState.ACTIVE, 0),
    (QueueItem.state == QueueState.PENDING, 1),
    (QueueItem.state == QueueState.FAILED, 2),
    (QueueItem.state == QueueState.DONE, 3),
    else_=4,
)


async def list_queue_items(
    session: AsyncSession,
    *,
    state: QueueState | None = None,
    limit: int = 100,
    offset: int = 0,
) -> tuple[list[QueueItemOut], int]:
    """Queue entries ordered the way the worker will pick them up."""
    stmt = select(QueueItem)
    count_stmt = select(func.count(QueueItem.id))
    if state is not None:
        stmt = stmt.where(QueueItem.state == state)
        count_stmt = count_stmt.where(QueueItem.state == state)
    stmt = (
        stmt.order_by(_QUEUE_STATE_ORDER, QueueItem.priority.desc(), QueueItem.id.asc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return [queue_item_to_out(row) for row in rows], total


async def list_activity(
    session: AsyncSession,
    *,
    level: ActivityLevel | None = None,
    event: str | None = None,
    artist_id: str | None = None,
    album_id: str | None = None,
    limit: int = 100,
    offset: int = 0,
) -> tuple[list[ActivityOut], int]:
    """Activity feed, newest first."""
    stmt = select(Activity)
    count_stmt = select(func.count(Activity.id))
    if level is not None:
        stmt = stmt.where(Activity.level == level)
        count_stmt = count_stmt.where(Activity.level == level)
    if event:
        stmt = stmt.where(Activity.event == event)
        count_stmt = count_stmt.where(Activity.event == event)
    if artist_id:
        stmt = stmt.where(Activity.artist_id == str(artist_id))
        count_stmt = count_stmt.where(Activity.artist_id == str(artist_id))
    if album_id:
        stmt = stmt.where(Activity.album_id == str(album_id))
        count_stmt = count_stmt.where(Activity.album_id == str(album_id))
    stmt = (
        stmt.order_by(Activity.created_at.desc(), Activity.id.desc())
        .limit(max(1, limit))
        .offset(max(0, offset))
    )
    rows = list((await session.execute(stmt)).unique().scalars().all())
    total = int((await session.execute(count_stmt)).scalar_one())
    return [activity_to_out(row) for row in rows], total


async def recent_activity(session: AsyncSession, limit: int = 15) -> list[ActivityOut]:
    """The newest *limit* activity entries (dashboard feed)."""
    items, _ = await list_activity(session, limit=limit)
    return items


# ---------------------------------------------------------------------------
# Read models: status
# ---------------------------------------------------------------------------
async def library_stats(session: AsyncSession) -> LibraryStatsOut:
    """Headline library counts for the dashboard."""
    artists_total = int((await session.execute(select(func.count(Artist.id)))).scalar_one())
    artists_monitored = int(
        (
            await session.execute(
                select(func.count(Artist.id)).where(Artist.monitored.is_(True))
            )
        ).scalar_one()
    )
    album_rows = (
        await session.execute(select(Album.status, func.count(Album.id)).group_by(Album.status))
    ).all()
    by_status = {enum_value(status): int(count) for status, count in album_rows}
    tracks_total = int((await session.execute(select(func.count(Track.id)))).scalar_one())
    tracks_done = int(
        (
            await session.execute(
                select(func.count(Track.id)).where(Track.status == TrackStatus.DOWNLOADED)
            )
        ).scalar_one()
    )
    size_bytes = (
        await session.execute(
            select(func.sum(Track.file_size))
            .join(Album, Album.id == Track.album_id)
            .where(Album.status == AlbumStatus.DOWNLOADED)
        )
    ).scalar()
    return LibraryStatsOut(
        artists=artists_total,
        monitored_artists=artists_monitored,
        albums=sum(by_status.values()),
        wanted_albums=by_status.get(AlbumStatus.WANTED.value, 0)
        + by_status.get(AlbumStatus.QUEUED.value, 0)
        + by_status.get(AlbumStatus.DOWNLOADING.value, 0),
        downloaded_albums=by_status.get(AlbumStatus.DOWNLOADED.value, 0),
        failed_albums=by_status.get(AlbumStatus.FAILED.value, 0),
        tracks=tracks_total,
        downloaded_tracks=tracks_done,
        size_bytes=int(size_bytes) if size_bytes is not None else None,
    )


async def albums_by_status(session: AsyncSession) -> dict[str, int]:
    """Album counts keyed by :class:`AlbumStatus` value, zero-filled."""
    rows = (
        await session.execute(select(Album.status, func.count(Album.id)).group_by(Album.status))
    ).all()
    counts = {status.value: 0 for status in AlbumStatus}
    for status, count in rows:
        counts[enum_value(status)] = int(count)
    return counts


async def library_quality(session: AsyncSession) -> LibraryQualityOut:
    """How much of what is on disk is held in a hi-res format.

    One statement and a fold. The statement pulls the four columns
    :mod:`app.core.quality` reads, for every track of every release in
    ``library_scope`` that has a path; the fold groups them by release and asks
    :mod:`app.core.quality` — twice, and nothing else asks anything.

    **No format arithmetic happens here and none may be added.** Not a
    ``bit_depth > 16`` in the SQL, not an ``.mp3`` extension test, not a
    ``COALESCE`` that treats a missing bit depth as sixteen. The whole point of
    :mod:`app.core.quality` is that the header figure, the Upgrade button and
    the download loop cannot disagree, and a second implementation here would be
    the one that drifts, silently, in a number nobody can check by eye.

    A release whose format cannot be determined is left out of ``measured``
    rather than counted as lossy. That is the same refusal
    :func:`app.core.quality.owned_format_id` makes and for the same reason: an
    untagged FLAC and an MP3 look identical from these four columns, and calling
    both lossy would put a 24/96 rip in the "not hi-res" pile. It costs a
    slightly optimistic share and buys a number that is never wrong.

    Measured on a real library — 75 releases on disk, 1032 track rows — this is
    a few milliseconds, which is why the roll-up is computed rather than stored:
    a column on ``albums`` would be a derived value that can go stale, and
    CLAUDE.md keeps columns on ``albums`` for the things that cannot be
    re-derived.
    """
    in_scope = library_scope(EnrichmentEntity.ALBUM)

    albums_total = int(
        (
            await session.execute(select(func.count(Album.id)).where(Album.id.in_(in_scope)))
        ).scalar_one()
    )

    rows = (
        await session.execute(
            select(
                Track.album_id,
                Track.path,
                Track.format_id,
                Track.bit_depth,
                Track.sampling_rate,
            ).where(Track.album_id.in_(in_scope), Track.path.is_not(None))
        )
    ).all()

    by_album: dict[str, list[Any]] = defaultdict(list)
    for row in rows:
        by_album[str(row.album_id)].append(row)

    measured = 0
    hires = 0
    for tracks in by_album.values():
        # The album is only as good as its worst file — quality.py's rule, and
        # the same call album_to_out makes for AlbumOut.owned_format_id.
        fid = quality.owned_format_id(tracks)
        verdict = quality.is_hires(fid)
        if verdict is None:
            # Either nothing on this release has a determinable format, or the
            # format id is one this build has never heard of. Both are "we do
            # not know", which is not "no".
            continue
        measured += 1
        if verdict:
            hires += 1

    return LibraryQualityOut(
        albums=albums_total,
        measured=measured,
        hires=hires,
        hires_share=(hires / measured) if measured else None,
    )


async def _service_payload(
    *sources: Any, label: str = "service"
) -> dict[str, Any]:
    """Call the first available ``(obj, method_names)`` source for a status dict.

    Trailing non-tuple positional arguments are passed to the method. Every
    failure is logged at debug level and skipped — status pages must never 500
    because a background service is unhappy.
    """
    pairs = [item for item in sources if isinstance(item, tuple)]
    args = [item for item in sources if not isinstance(item, tuple)]
    for obj, names in pairs:
        if obj is None:
            continue
        try:
            handled, value = await call_first(obj, names, *args)
        except TypeError:
            try:
                handled, value = await call_first(obj, names)
            except Exception as exc:  # noqa: BLE001
                logger.debug("%s status probe failed: %s", label, exc)
                continue
        except Exception as exc:  # noqa: BLE001
            logger.debug("%s status probe failed: %s", label, exc)
            continue
        if handled and isinstance(value, dict):
            return value
    return {}


def _overlay(target: Any, payload: dict[str, Any], model: type) -> None:
    """Copy the keys of *payload* that are fields of *model* onto *target*."""
    fields = getattr(model, "model_fields", {})
    for key, value in payload.items():
        if key not in fields or value is None:
            continue
        if key.endswith(("_at", "_until")):
            value = _maybe_datetime(value)
            if value is None:
                continue
        try:
            setattr(target, key, value)
        except Exception as exc:  # noqa: BLE001 - a bad field must not 500
            logger.debug("status field %s rejected: %s", key, exc)


async def queue_stats(session: AsyncSession) -> QueueStatsOut:
    """Queue depth per state plus what the worker is doing right now."""
    rows = (
        await session.execute(
            select(QueueItem.state, func.count(QueueItem.id)).group_by(QueueItem.state)
        )
    ).all()
    counts = {state.value: 0 for state in QueueState}
    for state, count in rows:
        counts[enum_value(state)] = int(count)

    stats = QueueStatsOut(
        pending=counts[QueueState.PENDING.value],
        active=counts[QueueState.ACTIVE.value],
        done=counts[QueueState.DONE.value],
        failed=counts[QueueState.FAILED.value],
        cancelled=counts[QueueState.CANCELLED.value],
        total=sum(counts.values()),
    )

    active = (
        await session.execute(
            select(QueueItem).where(QueueItem.state == QueueState.ACTIVE).limit(1)
        )
    ).unique().scalars().first()
    if active is not None:
        stats.current_album_id = active.album_id
        album = getattr(active, "album", None)
        stats.current_album_title = album.display_title if album is not None else None

    # Overlay the live worker's own view (``AppState.queue_status`` /
    # ``QueueWorker.stats``), which knows what it is downloading right now.
    payload = await _service_payload(
        (get_core_state(), ("queue_status",)),
        (get_queue_worker(), ("stats",)),
        session,
        label="queue",
    )
    if payload:
        _overlay(stats, payload, QueueStatsOut)
    elif get_queue_worker() is not None:
        running = getattr(get_queue_worker(), "running", None)
        stats.worker_running = bool(running) if running is not None else True
    return stats


async def indexer_status(session: AsyncSession) -> IndexerStatusOut:
    """Indexer state, merging database facts with the live service's own view."""
    settings = get_settings()
    monitored = int(
        (
            await session.execute(
                select(func.count(Artist.id)).where(Artist.monitored.is_(True))
            )
        ).scalar_one()
    )
    never_checked = int(
        (
            await session.execute(
                select(func.count(Artist.id)).where(
                    Artist.monitored.is_(True), Artist.last_checked_at.is_(None)
                )
            )
        ).scalar_one()
    )
    last_checked = (
        await session.execute(
            select(func.max(Artist.last_checked_at)).where(Artist.monitored.is_(True))
        )
    ).scalar_one()

    status = IndexerStatusOut(
        enabled=settings.indexer_enabled,
        running=False,
        paused=False,
        artist_interval_seconds=settings.indexer_artist_interval,
        full_sweep_hours=settings.indexer_full_sweep_hours,
        last_run_at=as_utc(last_checked),
        monitored_artists=monitored,
        artists_never_checked=never_checked,
    )

    indexer = get_indexer()
    payload = await _service_payload(
        (get_core_state(), ("indexer_status",)),
        (indexer, ("status", "get_status")),
        session,
        label="indexer",
    )
    if not payload and indexer is not None:
        payload = {
            key: getattr(indexer, key)
            for key in (
                "enabled",
                "running",
                "paused",
                "next_run_at",
                "last_run_at",
                "current_artist_id",
                "current_artist_name",
            )
            if hasattr(indexer, key)
        }
    _overlay(status, payload, IndexerStatusOut)

    if status.next_run_at is None:
        scheduler = get_scheduler()
        job = None
        if scheduler is not None:
            for job_id in ("indexer_tick", "indexer", "qobuzarr-indexer"):
                try:
                    job = scheduler.get_job(job_id)
                except Exception as exc:  # noqa: BLE001
                    logger.debug("scheduler.get_job(%s) failed: %s", job_id, exc)
                    job = None
                if job is not None:
                    break
        next_run = getattr(job, "next_run_time", None) if job is not None else None
        if isinstance(next_run, datetime):
            status.next_run_at = as_utc(next_run)

    if status.next_run_at is not None:
        delta = (status.next_run_at - utcnow()).total_seconds()
        status.seconds_until_next_run = max(0.0, delta)
    elif status.enabled and status.last_run_at is not None:
        elapsed = seconds_since(status.last_run_at) or 0.0
        status.seconds_until_next_run = max(
            0.0, settings.indexer_artist_interval - elapsed
        )
    return status


def rate_limit_status() -> RateLimitStatusOut | None:
    """Snapshot the global rate limiter.

    Prefers ``AppState.rate_limit_status()`` (already keyed for
    :class:`RateLimitStatusOut`); falls back to the limiter's own ``stats()``
    dict, and finally to the configured defaults when nothing is running.
    """
    settings = get_settings()
    fallback = RateLimitStatusOut(
        min_request_interval=settings.qobuz_min_request_interval,
        max_requests_per_hour=settings.qobuz_max_requests_per_hour,
        budget_remaining=settings.qobuz_max_requests_per_hour,
    )

    state = get_core_state()
    getter = getattr(state, "rate_limit_status", None) if state is not None else None
    if callable(getter):
        try:
            payload = getter()
        except Exception as exc:  # noqa: BLE001 - dashboards must not 500
            logger.warning("AppState.rate_limit_status() failed: %s", exc)
            payload = None
        if isinstance(payload, dict):
            snapshot = fallback.model_copy()
            _overlay(snapshot, payload, RateLimitStatusOut)
            return snapshot

    limiter = get_rate_limiter()
    if limiter is None:
        return fallback
    try:
        stats = limiter.stats()
    except Exception as exc:  # noqa: BLE001 - dashboards must not 500
        logger.warning("Rate-limiter stats unavailable: %s", exc)
        return RateLimitStatusOut(
            min_request_interval=settings.qobuz_min_request_interval,
            max_requests_per_hour=settings.qobuz_max_requests_per_hour,
            budget_remaining=settings.qobuz_max_requests_per_hour,
        )

    return RateLimitStatusOut(
        min_request_interval=float(
            stats.get("min_interval", settings.qobuz_min_request_interval)
        ),
        max_requests_per_hour=int(
            stats.get("hourly_cap", settings.qobuz_max_requests_per_hour)
        ),
        requests_last_hour=int(stats.get("requests_last_hour", 0)),
        budget_remaining=int(stats.get("budget_remaining", 0)),
        budget_used_percent=float(stats.get("budget_used_percent", 0.0)),
        seconds_until_next_slot=float(stats.get("next_slot_in_seconds", 0.0)),
        last_request_at=_maybe_datetime(stats.get("last_request_at")),
        circuit_open=bool(stats.get("circuit_open", False)),
        circuit_open_until=_maybe_datetime(stats.get("circuit_open_until")),
        recent_429s=int(stats.get("recent_429s", 0)),
    )


#: A made-up hi-res release used only to illustrate the naming template.
_PREVIEW_ARTIST = {"id": "0", "name": "Floating Points"}
_PREVIEW_ALBUM = {
    "id": "preview",
    "title": "Promises",
    "version": None,
    "release_date": date(2021, 3, 26),
    "label": "Luaka Bop",
    "genre": "Jazz",
    "media_count": 1,
    "tracks_count": 9,
    "hires": True,
    "max_bit_depth": 24,
    "max_sampling_rate": 96.0,
    "artist": _PREVIEW_ARTIST,
}
_PREVIEW_TRACKS = (
    {"title": "Movement 1", "track_number": 1, "media_number": 1},
    {"title": "Movement 2", "track_number": 2, "media_number": 1},
)


def naming_preview(settings: Settings | None = None) -> list[str]:
    """Sample library paths produced by the configured naming template.

    Purely illustrative — it touches no database and writes nothing. Returns an
    empty list if the template cannot be rendered, so the settings page still
    loads when someone puts a typo in ``NAMING_TEMPLATE``.

    An explicitly supplied ``settings`` is taken as **already effective**, the
    same rule :func:`app.core.librarian.plan_refile` holds to: re-applying the
    overlay over a caller's base would put the stored ``naming_template`` row
    back on top of a *candidate* template, so a preview of what somebody just
    typed would silently be a preview of what is already saved.
    """
    settings = settings if settings is not None else get_effective_settings()
    try:
        from app.core.naming import render_track_path

        return [
            str(
                render_track_path(
                    track,
                    _PREVIEW_ALBUM,
                    settings,
                    artist=_PREVIEW_ARTIST,
                    bit_depth=24,
                    sampling_rate=96.0,
                    ext="flac",
                )
            )
            for track in _PREVIEW_TRACKS
        ]
    except Exception as exc:  # noqa: BLE001 - a bad template must not 500 the page
        logger.debug("Naming preview failed: %s", exc)
        return []


def _pending_setting_keys() -> list[str]:
    """Overridden keys the running process has not picked up yet.

    Asked of :class:`app.core.state.AppState`, defensively like every other
    runtime lookup in this module: with nothing running there is nothing that
    could be stale, so the honest answer is an empty list rather than a 500.
    """
    state = get_core_state()
    getter = getattr(state, "pending_setting_keys", None) if state is not None else None
    if not callable(getter):
        return []
    try:
        return [str(key) for key in getter()]
    except Exception as exc:  # noqa: BLE001 - the settings page must not 500
        logger.warning("Pending settings lookup failed: %s", exc)
        return []


def build_settings_out(settings: Settings | None = None) -> SettingsOut:
    """Read-only view of the effective configuration for the settings page.

    "Effective" now means the environment *plus* the settings overlay, and the
    payload says which is which: ``overridable`` is what may be written,
    ``origins`` is where each of those values came from, and ``pending`` is the
    short list of writes that have not reached the running process yet.
    """
    settings = effective_for(settings or get_settings())
    client = get_qobuz_client()
    source = getattr(client, "app_secret_source", None) if client is not None else None
    if not source:
        source = "env" if settings.qobuz_app_secret else "unset"
    return SettingsOut(
        app_name=APP_NAME,
        app_version=__version__,
        library_path=str(settings.library_path),
        data_path=str(settings.data_path),
        trash_path=str(settings.trash_dir),
        default_format_id=settings.default_format_id,
        default_format_label=settings.format_label(settings.default_format_id),
        naming_template=settings.naming_template,
        naming_preview=naming_preview(settings),
        default_monitor_mode=settings.default_monitor_mode,
        default_quality_profile=settings.default_quality_profile,
        default_accepted_release_types=settings.accepted_release_type_defaults,
        qobuz_min_request_interval=settings.qobuz_min_request_interval,
        qobuz_max_requests_per_hour=settings.qobuz_max_requests_per_hour,
        indexer_artist_interval=settings.indexer_artist_interval,
        indexer_full_sweep_hours=settings.indexer_full_sweep_hours,
        indexer_enabled=settings.indexer_enabled,
        auto_index_on_follow=settings.auto_index_on_follow,
        auto_download=settings.auto_download,
        download_track_delay=settings.download_track_delay,
        download_concurrency=settings.download_concurrency,
        download_max_attempts=settings.download_max_attempts,
        upgrade_cleanup=settings.upgrade_cleanup,
        library_scan_nightly=settings.library_scan_nightly,
        library_scan_complete_ratio=settings.library_scan_complete_ratio,
        integrity_enabled=settings.integrity_enabled,
        integrity_reverify_fraction=settings.integrity_reverify_fraction,
        host=settings.host,
        port=settings.port,
        log_level=enum_value(settings.log_level) or "INFO",
        qobuz_app_id=settings.qobuz_app_id,
        credentials_ok=settings.has_credentials,
        app_secret_source=source,
        enrichment_enabled=settings.enrichment_enabled,
        enrichment_sources=settings.enrichment_source_list,
        enrichment_consensus_threshold=settings.enrichment_consensus_threshold,
        enrichment_apply_release_type=settings.enrichment_apply_release_type,
        enrichment_interval=settings.enrichment_interval,
        enrichment_batch_size=settings.enrichment_batch_size,
        enrichment_refresh_days=settings.enrichment_refresh_days,
        # Set/unset only — the settings page is public within the app and this is
        # an email address, so the value itself never leaves the process.
        enrichment_contact="set" if settings.enrichment_contact.strip() else "",
        musicbrainz_ready=settings.musicbrainz_ready,
        acoustid_ready=settings.acoustid_ready,
        enrichment_write_back=settings.enrichment_write_back,
        enrichment_prefer_external_cover=settings.enrichment_prefer_external_cover,
        nfo_enabled=settings.nfo_enabled,
        overridable=list(overridable_keys()),
        origins=setting_origins(),
        pending=_pending_setting_keys(),
    )


async def build_status(session: AsyncSession, *, activity_limit: int = 15) -> StatusOut:
    """Assemble the full dashboard payload in one go.

    ``library_path`` is not in ``OVERRIDABLE_SETTINGS``, so ``get_settings()``
    is the whole truth here and there is no overlay to consult. The one
    ``statvfs`` behind :attr:`StatusOut.disk` goes through
    :func:`asyncio.to_thread`: it is a single syscall and walks nothing, but a
    stale network mount blocks it for that mount's timeout, and that is a thread
    to lose rather than the event loop the whole application runs on.
    """
    settings = get_settings()
    from app.core.scanner import disk_capacity  # noqa: PLC0415 - keeps app.api import-light

    capacity = await asyncio.to_thread(disk_capacity, settings.library_path)
    state = get_core_state()
    started_at = get_started_at()
    client = get_qobuz_client()
    credentials_ok = getattr(state, "credentials_ok", None)
    app_secret_ok = getattr(state, "app_secret_ok", None)
    return StatusOut(
        version=__version__,
        started_at=started_at,
        uptime_seconds=seconds_since(started_at) or 0.0,
        library=await library_stats(session),
        queue=await queue_stats(session),
        indexer=await indexer_status(session),
        rate_limit=rate_limit_status(),
        recent_activity=(
            await recent_activity(session, limit=activity_limit)
            if activity_limit
            else []
        ),
        credentials_ok=(
            bool(credentials_ok)
            if credentials_ok is not None
            else settings.has_credentials
        ),
        app_secret_ok=(
            bool(app_secret_ok)
            if app_secret_ok is not None
            else bool(getattr(client, "has_app_secret", False))
            or bool(settings.qobuz_app_secret)
        ),
        library_path=str(settings.library_path),
        disk=(
            DiskCapacityOut(
                path=str(settings.library_path),
                total_bytes=capacity.total_bytes,
                used_bytes=capacity.used_bytes,
                free_bytes=capacity.free_bytes,
            )
            if capacity is not None
            else None
        ),
        banners=build_banners(settings),
    )
