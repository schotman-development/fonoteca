"""The Cover Art Archive rung — artwork, keyed by MusicBrainz id.

The only source here with **zero matching risk**, by construction: it is looked
up by an MBID that MusicBrainz already established, so there is no candidate list
and nothing to be ambiguous about. If the id is right the art is right.

It is also the least urgent, which is why it runs late — and why it is last of
the artwork sources, not merely late in the ladder. Qobuz ships artwork for
practically everything it sells, and that artwork is of the exact release it is
selling; Deezer serves the label's own 1000x1000 for the release a barcode
pinned. This Archive serves whatever a contributor uploaded, which is sometimes
a press-kit scan and sometimes a phone photograph of a jewel case, and it is
keyed by an MBID — so the only releases it can answer for are ones another
source already identified and already supplied art for. So it fills the tail
rather than replacing anything: the read models coalesce Qobuz's image first
unless ``ENRICHMENT_PREFER_EXTERNAL_COVER`` says otherwise, and Deezer's ahead
of this one either way (``AlbumMetadata.cover_url``).

Two protocol details shape the client:

**404 is a normal answer.** Most release groups have no art, and treating that as
an error would fill the activity feed with non-events.

**Everything redirects.** The index is served from coverartarchive.org but the
images live on archive.org, and the JSON itself 307s. httpx does not follow
redirects unless told, which the shared base already does.
"""

from __future__ import annotations

from typing import Any, Iterable, Mapping, Sequence

from app.config import Settings, get_settings
from app.enrich.errors import NoMatchKey, NotMatched
from app.enrich.types import AlbumSnapshot, EnrichmentJob, EnrichmentResult
from app.logging_conf import get_logger
from app.models import EnrichmentEntity, EnrichmentSource
from app.net.errors import HttpNotFound
from app.net.http import JsonHttpClient
from app.net.ratelimit import RateLimiter

__all__ = ["CoverArtClient", "CoverArtProvider"]

logger = get_logger(__name__)

#: Thumbnail sizes, largest first. 1200 is plenty for a UI thumbnail and a
#: fraction of the original, which can be a 20 MB scan.
_PREFERRED_SIZES = ("1200", "large", "500", "250", "small")


class CoverArtClient(JsonHttpClient):
    """Read-only access to the Cover Art Archive index."""

    source = "coverartarchive"
    base_url = "https://coverartarchive.org/"
    timeout = 30.0

    async def images(self, kind: str, mbid: str) -> list[dict[str, Any]]:
        """Artwork listed for a release or release group, or an empty list.

        *kind* is ``release`` or ``release-group``. A 404 means "no art", which
        is the common case and not a failure.
        """
        try:
            payload = await self.get_json(f"{kind}/{mbid}")
        except HttpNotFound:
            return []
        images = payload.get("images") if isinstance(payload, Mapping) else None
        return [item for item in (images or []) if isinstance(item, Mapping)]


class CoverArtProvider:
    """Finds the front cover for a release Qobuzarr already identified."""

    source = EnrichmentSource.COVERARTARCHIVE

    def __init__(
        self,
        *,
        limiter: RateLimiter,
        settings: Settings | None = None,
        client: CoverArtClient | None = None,
    ) -> None:
        self._settings = settings or get_settings()
        self.client = client or CoverArtClient(
            limiter=limiter,
            settings=self._settings,
            user_agent=self._settings.enrichment_user_agent,
        )

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        """Albums only — the archive holds release artwork, not artist portraits."""
        return entity_type is EnrichmentEntity.ALBUM

    def is_ready(self) -> bool:
        """Always: no key, no account, no configuration."""
        return True

    async def aclose(self) -> None:
        await self.client.aclose()

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        album = job.album
        # Release first, release group second. A release-level cover is of the
        # exact pressing; the group's is of whichever pressing someone uploaded.
        targets = [
            ("release", album.mb_release_mbid),
            ("release-group", album.mb_release_group_mbid),
        ]
        if not any(mbid for _kind, mbid in targets):
            raise NoMatchKey(
                "no MusicBrainz id yet — cover art is looked up by id, never searched",
                source=self.source.value,
            )

        for kind, mbid in targets:
            if not mbid:
                continue
            front = _front_image(await self.client.images(kind, str(mbid)))
            if front is None:
                continue
            return EnrichmentResult(
                match_key=str(mbid),
                album_fields={
                    "caa_source_mbid": str(mbid),
                    "caa_front_url": front[0],
                    "caa_thumb_url": front[1],
                },
            )

        raise NotMatched(
            f"the Cover Art Archive has no front cover for '{album.title}'",
            source=self.source.value,
        )


def _front_image(images: Sequence[Mapping[str, Any]]) -> tuple[str, str] | None:
    """``(full url, thumbnail url)`` for the front cover, or ``None``.

    Only ``front`` images are considered: the archive holds booklet scans, media
    labels and back covers too, and any of them would be a wrong answer for the
    one slot an album row has.

    Unapproved images are skipped — those are pending edits, which is exactly the
    sort of thing that turns out to be somebody's placeholder.
    """
    for image in images:
        if not image.get("front") or not image.get("approved", True):
            continue
        full = _https(image.get("image"))
        if not full:
            continue
        thumbnails = image.get("thumbnails")
        thumb = ""
        if isinstance(thumbnails, Mapping):
            thumb = _first(_https(thumbnails.get(size)) for size in _PREFERRED_SIZES)
        return full, thumb or full
    return None


def _https(url: Any) -> str:
    """Force https. The archive still advertises http in its JSON."""
    text = str(url).strip() if url else ""
    if not text:
        return ""
    return f"https://{text[7:]}" if text.startswith("http://") else text


def _first(values: Iterable[str]) -> str:
    for value in values:
        if value:
            return value
    return ""
