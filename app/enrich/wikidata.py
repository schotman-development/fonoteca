"""The Wikidata rung — a biography, a portrait, and a second opinion on ISNI.

Reached only through the Wikidata QID that MusicBrainz already published as a
url-relation, so it inherits that rung's matching risk and adds none of its own.
There is no search here and there must not be: "which Wikipedia article is about
this musician" is exactly the question that goes wrong quietly.

Three things come back, over two hosts:

**A biography**, from the Wikipedia REST summary. This text is **CC BY-SA**, so
the source URL and the licence are stored *with* it and written together into the
artist page and the NFO. Share-alike is a licence term, not a nicety, and a
biography with no attribution is one that should not have been republished.

**A portrait**, from Wikidata's ``P18``, which names a file on Wikimedia Commons.
Commons files are individually licensed by whoever uploaded them, so this fills
the gap behind Qobuz's own image rather than replacing it.

**An ISNI**, from ``P213``. MusicBrainz often has none — it does not for Joe
Bonamassa, while Wikidata does — so this is where an artist's real-world
identifier usually comes from.
"""

from __future__ import annotations

import urllib.parse
from typing import Any, Mapping

from app.config import Settings, get_settings
from app.enrich.errors import NoMatchKey, NotMatched
from app.enrich.matching import normalize_isni
from app.enrich.types import ArtistSnapshot, EnrichmentJob, EnrichmentResult
from app.logging_conf import get_logger
from app.models import EnrichmentEntity, EnrichmentSource
from app.net.errors import HttpNotFound
from app.net.http import JsonHttpClient
from app.net.ratelimit import RateLimiter

__all__ = ["WikidataClient", "WikipediaClient", "WikidataProvider"]

logger = get_logger(__name__)

#: Wikidata property ids. Named, because ``P18`` in a conditional is unreadable.
_P_IMAGE = "P18"
_P_ISNI = "P213"

#: Wikipedia text is CC BY-SA 4.0 and this is stored beside every extract. It is
#: not configurable: it is a statement of fact about where the text came from.
WIKIPEDIA_LICENCE = "CC BY-SA 4.0"

#: Portrait width requested from Commons. Full-size originals are routinely
#: 20 MB scans, which is not what a page header needs.
_IMAGE_WIDTH = 640


class WikidataClient(JsonHttpClient):
    """Entity lookups against Wikidata."""

    source = "wikidata"
    # Deliberately the site root, not ``/wiki/``. A relative path whose *first*
    # segment contains a colon — ``Special:EntityData/...`` — is parsed by httpx
    # as a URL scheme and silently dropped, producing a request for
    # ``/wiki/EntityData/...`` which does not exist. Keeping ``wiki/`` in the
    # path puts a slash before the colon and the segment survives intact.
    base_url = "https://www.wikidata.org/"
    timeout = 30.0

    async def entity(self, qid: str) -> dict[str, Any] | None:
        """One entity's full JSON, or ``None`` when there is no such QID."""
        try:
            payload = await self.get_json(f"wiki/Special:EntityData/{qid}.json")
        except HttpNotFound:
            return None
        entities = payload.get("entities") if isinstance(payload, Mapping) else None
        found = (entities or {}).get(qid) if isinstance(entities, Mapping) else None
        return dict(found) if isinstance(found, Mapping) else None


class WikipediaClient(JsonHttpClient):
    """The Wikipedia REST summary endpoint."""

    source = "wikipedia"
    base_url = "https://en.wikipedia.org/api/rest_v1/"
    timeout = 30.0

    async def summary(self, title: str) -> dict[str, Any] | None:
        """The lead extract for an article title, or ``None``."""
        try:
            payload = await self.get_json(
                f"page/summary/{urllib.parse.quote(title, safe='')}"
            )
        except HttpNotFound:
            return None
        return dict(payload) if isinstance(payload, Mapping) else None


class WikidataProvider:
    """Turns a Wikidata QID into a biography, a portrait and an ISNI."""

    source = EnrichmentSource.WIKIDATA

    def __init__(
        self,
        *,
        limiter: RateLimiter,
        settings: Settings | None = None,
        client: WikidataClient | None = None,
        wikipedia: WikipediaClient | None = None,
    ) -> None:
        self._settings = settings or get_settings()
        agent = self._settings.enrichment_user_agent
        self.client = client or WikidataClient(
            limiter=limiter, settings=self._settings, user_agent=agent
        )
        # Same limiter: both are Wikimedia, and one budget across the pair is the
        # polite reading of "one limiter per upstream".
        self.wikipedia = wikipedia or WikipediaClient(
            limiter=limiter, settings=self._settings, user_agent=agent
        )

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        """Artists only. Wikipedia has album articles, but matching one to a
        release is a search, and searches are what this ladder refuses to do."""
        return entity_type is EnrichmentEntity.ARTIST

    def is_ready(self) -> bool:
        """Always: no key, no account, no configuration."""
        return True

    async def aclose(self) -> None:
        await self.client.aclose()
        await self.wikipedia.aclose()

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        artist = job.artist
        if not artist.wikidata_qid:
            raise NoMatchKey(
                "no Wikidata id yet — it comes from MusicBrainz's url relations",
                source=self.source.value,
            )

        entity = await self.client.entity(artist.wikidata_qid)
        if entity is None:
            raise NotMatched(
                f"Wikidata has no entity {artist.wikidata_qid}",
                source=self.source.value,
            )

        claims = entity.get("claims") if isinstance(entity.get("claims"), Mapping) else {}
        fields: dict[str, Any] = {}

        isni = normalize_isni(_claim(claims, _P_ISNI))
        if isni:
            # MusicBrainz frequently has none, so this is usually where an
            # artist's real-world identifier actually comes from.
            fields["isni"] = isni

        image = _claim(claims, _P_IMAGE)
        if image:
            fields["commons_image_url"] = _commons_url(str(image))

        title = _article_title(entity)
        if title:
            summary = await self.wikipedia.summary(title)
            if summary:
                extract = str(summary.get("extract") or "").strip()
                url = _page_url(summary)
                # Written together or not at all: CC BY-SA is share-alike, and a
                # biography without its source is one that should not be shown.
                if extract and url:
                    fields["bio"] = extract
                    fields["bio_source_url"] = url
                    fields["bio_licence"] = WIKIPEDIA_LICENCE
                    fields["wikipedia_url"] = url

        if not fields:
            raise NotMatched(
                f"Wikidata entity {artist.wikidata_qid} has nothing we use",
                source=self.source.value,
            )
        return EnrichmentResult(match_key=artist.wikidata_qid, artist_fields=fields)


# ---------------------------------------------------------------------------
# Payload helpers
# ---------------------------------------------------------------------------
def _claim(claims: Mapping[str, Any], prop: str) -> Any:
    """The value of the first statement for *prop*, or ``None``.

    Wikidata nests it four deep and any level may be missing on an incomplete
    item, so every step is guarded rather than assumed.
    """
    statements = claims.get(prop)
    if not isinstance(statements, (list, tuple)) or not statements:
        return None
    first = statements[0]
    if not isinstance(first, Mapping):
        return None
    snak = first.get("mainsnak")
    if not isinstance(snak, Mapping):
        return None
    value = snak.get("datavalue")
    if not isinstance(value, Mapping):
        return None
    return value.get("value")


def _article_title(entity: Mapping[str, Any]) -> str | None:
    """The English Wikipedia article title for this entity, if it has one."""
    sitelinks = entity.get("sitelinks")
    if not isinstance(sitelinks, Mapping):
        return None
    link = sitelinks.get("enwiki")
    if not isinstance(link, Mapping):
        return None
    title = str(link.get("title") or "").strip()
    return title or None


def _page_url(summary: Mapping[str, Any]) -> str:
    """The canonical article URL — the attribution target for the extract."""
    urls = summary.get("content_urls")
    desktop = urls.get("desktop") if isinstance(urls, Mapping) else None
    if isinstance(desktop, Mapping):
        page = str(desktop.get("page") or "").strip()
        if page:
            return page
    title = str(summary.get("titles", {}).get("canonical") or "").strip()
    return f"https://en.wikipedia.org/wiki/{title}" if title else ""


def _commons_url(filename: str, width: int = _IMAGE_WIDTH) -> str:
    """A stable URL for a Commons file, at a sensible width.

    ``Special:FilePath`` redirects to wherever the file actually lives, so this
    keeps working when Commons reorganises its storage — which a direct upload
    URL would not.
    """
    quoted = urllib.parse.quote(filename.replace(" ", "_"), safe="")
    return f"https://commons.wikimedia.org/wiki/Special:FilePath/{quoted}?width={width}"
