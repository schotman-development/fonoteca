"""Builds the enrichment ladder from settings.

One place decides which rungs exist and in what order, so
:func:`app.core.state.init_state` stays a list of constructor calls and adding a
source is a single edit here.

Each provider gets **its own** :class:`~app.net.ratelimit.RateLimiter`. That is
not a departure from the one-limiter rule — the rule is one limiter *per
upstream*, shared by everyone who talks to it. Qobuz and MusicBrainz have nothing
to do with each other's budgets, and MusicBrainz's ~1 req/s has nothing to do
with Deezer's forty.

A rung the settings leave out is simply never constructed, so
``ENRICHMENT_SOURCES`` disables one with no special casing anywhere downstream:
the orchestrator only ever claims work for sources it has a provider for.

**The order is the setting's, not this file's.** :func:`build_providers` walks
``Settings.enrichment_source_list`` and nothing here ranks anything — the dicts
below are lookup tables, and rearranging them changes no behaviour at all. The
default that setting ships with puts **acoustid first**, because the audio is the
anchor: every other input to an identification is something a person typed, and
the library being identified is exactly the one whose typing cannot be trusted.
The waveform is the only thing in it that cannot be mistagged, so the rungs below
corroborate what the audio said rather than proposing an answer of their own.
"""

from __future__ import annotations

from typing import Callable

from app.config import Settings, get_settings
from app.enrich.types import EnrichmentProvider
from app.logging_conf import get_logger
from app.net.ratelimit import RateLimiter

__all__ = ["build_providers", "limiter_for"]

logger = get_logger(__name__)

#: ``source name -> (min-interval setting, hourly-cap setting)``.
_PACING: dict[str, tuple[str, str]] = {
    "deezer": ("deezer_min_request_interval", "deezer_max_requests_per_hour"),
    "musicbrainz": (
        "musicbrainz_min_request_interval",
        "musicbrainz_max_requests_per_hour",
    ),
    "acoustid": ("acoustid_min_request_interval", "acoustid_max_requests_per_hour"),
    "coverartarchive": (
        "coverart_min_request_interval",
        "coverart_max_requests_per_hour",
    ),
    "wikidata": ("wikidata_min_request_interval", "wikidata_max_requests_per_hour"),
}


def limiter_for(source: str, settings: Settings | None = None) -> RateLimiter:
    """Build the one rate limiter for *source*, paced from its own settings."""
    settings = settings or get_settings()
    interval_field, cap_field = _PACING[source]
    return RateLimiter.for_source(
        source,
        min_interval=float(getattr(settings, interval_field)),
        max_per_hour=int(getattr(settings, cap_field)),
        settings=settings,
    )


def _factories() -> dict[str, Callable[[Settings], EnrichmentProvider]]:
    """Constructors, imported lazily so an unused rung costs nothing to start."""
    factories: dict[str, Callable[[Settings], EnrichmentProvider]] = {}

    def _deezer(settings: Settings) -> EnrichmentProvider:
        from app.enrich.deezer import DeezerProvider  # noqa: PLC0415

        return DeezerProvider(
            limiter=limiter_for("deezer", settings), settings=settings
        )

    factories["deezer"] = _deezer

    def _musicbrainz(settings: Settings) -> EnrichmentProvider:
        from app.enrich.musicbrainz import MusicBrainzProvider  # noqa: PLC0415

        return MusicBrainzProvider(
            limiter=limiter_for("musicbrainz", settings), settings=settings
        )

    factories["musicbrainz"] = _musicbrainz

    def _acoustid(settings: Settings) -> EnrichmentProvider:
        from app.enrich.acoustid import AcoustIdProvider  # noqa: PLC0415

        return AcoustIdProvider(
            limiter=limiter_for("acoustid", settings), settings=settings
        )

    factories["acoustid"] = _acoustid

    def _coverart(settings: Settings) -> EnrichmentProvider:
        from app.enrich.coverart import CoverArtProvider  # noqa: PLC0415

        return CoverArtProvider(
            limiter=limiter_for("coverartarchive", settings), settings=settings
        )

    factories["coverartarchive"] = _coverart

    def _wikidata(settings: Settings) -> EnrichmentProvider:
        from app.enrich.wikidata import WikidataProvider  # noqa: PLC0415

        return WikidataProvider(
            limiter=limiter_for("wikidata", settings), settings=settings
        )

    factories["wikidata"] = _wikidata
    return factories


def build_providers(settings: Settings | None = None) -> list[EnrichmentProvider]:
    """Construct every enabled rung, in ``ENRICHMENT_SOURCES`` order.

    A source that is named but not yet implemented is skipped with a debug line
    rather than raising: the ladder is built in phases, and a half-built ladder
    should still enrich with the rungs it has.
    """
    settings = settings or get_settings()
    if not settings.enrichment_enabled:
        return []

    available = _factories()
    providers: list[EnrichmentProvider] = []
    for name in settings.enrichment_source_list:
        factory = available.get(name)
        if factory is None:
            logger.debug("Enrichment source %r is configured but not implemented yet", name)
            continue
        try:
            providers.append(factory(settings))
        except ModuleNotFoundError:
            # The ladder is built in phases; a rung that is named but not yet
            # written is a normal intermediate state, not a startup failure.
            logger.debug("Enrichment source %r is not implemented yet", name)
        except Exception:  # noqa: BLE001 - one bad rung must not stop startup
            logger.exception("Could not build the %s enrichment provider", name)

    if providers:
        logger.info(
            "Enrichment ladder: %s", ", ".join(p.source.value for p in providers)
        )
    return providers
