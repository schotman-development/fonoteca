"""The two rungs that hang off a MusicBrainz id: cover art and Wikidata.

Neither searches for anything. Both are handed an id another rung established,
so they inherit its matching risk and add none of their own — which is why the
tests here are about *protocol* rather than about matching: a 404 that means "no
art" rather than "failure", a redirect that must be followed, and a licence term
that travels with the text it applies to.

Payloads are trimmed copies of the real responses, served by
``httpx.MockTransport``.
"""

from __future__ import annotations

import asyncio
from typing import Any, Callable

import httpx
import pytest

from app.config import Settings
from app.enrich.coverart import CoverArtClient, CoverArtProvider
from app.enrich.errors import NoMatchKey, NotMatched
from app.enrich.types import AlbumSnapshot, ArtistSnapshot, EnrichmentJob
from app.enrich.wikidata import (
    WIKIPEDIA_LICENCE,
    WikidataClient,
    WikidataProvider,
    WikipediaClient,
)
from app.models import EnrichmentEntity, EnrichmentSource
from app.net.ratelimit import CircuitBreaker, RateLimiter

RELEASE = "43cc66a3-5418-4d0e-a14b-2b01aa23b171"
GROUP = "45017130-d783-4a09-97e1-780a3fd40382"
QID = "Q347717"


# ---------------------------------------------------------------------------
# Harness
# ---------------------------------------------------------------------------
def make_settings(**overrides: Any) -> Settings:
    base: dict[str, Any] = {
        "qobuz_app_id": "app",
        "qobuz_user_auth_token": "token",
        "enrichment_contact": "me@example.com",
        "backoff_initial": 0.0,
        "backoff_max": 0.0,
        "backoff_jitter": 0.0,
        "request_max_retries": 1,
    }
    base.update(overrides)
    return Settings(**base)


def make_limiter() -> RateLimiter:
    now = [1_000.0]

    async def sleep(seconds: float) -> None:
        now[0] += seconds
        await asyncio.sleep(0)

    return RateLimiter(
        min_interval=0.0,
        max_per_hour=10_000,
        breaker=CircuitBreaker(time_func=lambda: now[0]),
        time_func=lambda: now[0],
        sleep_func=sleep,
        name="test",
    )


def transport(
    routes: dict[str, Any], hits: list[str], base: str
) -> httpx.AsyncClient:
    def handler(request: httpx.Request) -> httpx.Response:
        hits.append(request.url.path)
        body = routes.get(request.url.path)
        return httpx.Response(404 if body is None else 200, json=body or {})

    return httpx.AsyncClient(
        transport=httpx.MockTransport(handler), base_url=base
    )


def caa_images(*, front: bool = True, approved: bool = True) -> dict[str, Any]:
    return {
        "images": [
            {
                "front": False,
                "approved": True,
                "types": ["Booklet"],
                "image": "http://coverartarchive.org/release/x/15195999985.jpg",
                "thumbnails": {"500": "http://coverartarchive.org/release/x/15195999985-500.jpg"},
            },
            {
                "front": front,
                "approved": approved,
                "types": ["Front"],
                "image": "http://coverartarchive.org/release/x/13164190086.jpg",
                "thumbnails": {
                    "1200": "http://coverartarchive.org/release/x/13164190086-1200.jpg",
                    "250": "http://coverartarchive.org/release/x/13164190086-250.jpg",
                },
            },
        ]
    }


def make_caa(
    routes: dict[str, Any], **settings: Any
) -> tuple[CoverArtProvider, list[str]]:
    hits: list[str] = []
    resolved = make_settings(**settings)
    client = CoverArtClient(
        limiter=make_limiter(),
        settings=resolved,
        http_client=transport(routes, hits, "https://coverartarchive.org/"),
    )
    return (
        CoverArtProvider(limiter=make_limiter(), settings=resolved, client=client),
        hits,
    )


def album_job(**overrides: Any) -> EnrichmentJob:
    base: dict[str, Any] = {
        "id": "al1",
        "artist_id": "a1",
        "artist_name": "Joe Bonamassa",
        "title": "Blues Of Desperation",
    }
    base.update(overrides)
    return EnrichmentJob(
        entity_type=EnrichmentEntity.ALBUM,
        source=EnrichmentSource.COVERARTARCHIVE,
        entity_id=base["id"],
        subject=AlbumSnapshot(**base),
    )


def run(provider: Any, job: EnrichmentJob) -> Any:
    async def go() -> Any:
        try:
            return await provider.fetch(job)
        finally:
            await provider.aclose()

    return asyncio.run(go())


# ---------------------------------------------------------------------------
# Cover Art Archive
# ---------------------------------------------------------------------------
def test_the_release_cover_is_preferred_over_the_groups() -> None:
    """A release-level cover is of the exact pressing; the group's is of
    whichever pressing somebody uploaded."""
    provider, hits = make_caa(
        {
            f"/release/{RELEASE}": caa_images(),
            f"/release-group/{GROUP}": caa_images(),
        }
    )

    result = run(
        provider,
        album_job(mb_release_mbid=RELEASE, mb_release_group_mbid=GROUP),
    )

    assert hits == [f"/release/{RELEASE}"], "the group is not even asked"
    assert result.album_fields["caa_source_mbid"] == RELEASE


def test_it_falls_back_to_the_release_group() -> None:
    provider, hits = make_caa({f"/release-group/{GROUP}": caa_images()})

    result = run(
        provider,
        album_job(mb_release_mbid=RELEASE, mb_release_group_mbid=GROUP),
    )

    assert hits == [f"/release/{RELEASE}", f"/release-group/{GROUP}"]
    assert result.album_fields["caa_source_mbid"] == GROUP


def test_urls_are_upgraded_to_https() -> None:
    """The archive still advertises http in its JSON; a mixed-content image would
    simply not load."""
    provider, _ = make_caa({f"/release/{RELEASE}": caa_images()})

    fields = run(provider, album_job(mb_release_mbid=RELEASE)).album_fields

    assert fields["caa_front_url"].startswith("https://")
    assert fields["caa_thumb_url"].startswith("https://")


def test_the_largest_thumbnail_wins() -> None:
    provider, _ = make_caa({f"/release/{RELEASE}": caa_images()})

    fields = run(provider, album_job(mb_release_mbid=RELEASE)).album_fields

    assert fields["caa_thumb_url"].endswith("-1200.jpg")


def test_only_the_front_cover_is_taken() -> None:
    """The archive holds booklets, media labels and back covers; any of them
    would be a wrong answer for the one slot an album row has."""
    provider, _ = make_caa({f"/release/{RELEASE}": caa_images(front=False)})

    with pytest.raises(NotMatched):
        run(provider, album_job(mb_release_mbid=RELEASE))


def test_an_unapproved_image_is_skipped() -> None:
    """Pending edits are exactly where somebody's placeholder lives."""
    provider, _ = make_caa({f"/release/{RELEASE}": caa_images(approved=False)})

    with pytest.raises(NotMatched):
        run(provider, album_job(mb_release_mbid=RELEASE))


def test_no_art_is_a_normal_answer_not_a_failure() -> None:
    """Most release groups have none. Treating 404 as an error would fill the
    activity feed with non-events."""
    provider, _ = make_caa({})

    with pytest.raises(NotMatched):
        run(provider, album_job(mb_release_mbid=RELEASE))


def test_without_an_mbid_nothing_is_asked() -> None:
    provider, hits = make_caa({})

    with pytest.raises(NoMatchKey):
        run(provider, album_job())

    assert hits == []


def test_the_client_follows_redirects() -> None:
    """The index 307s and the images live on archive.org. httpx does not follow
    redirects unless asked, and the shared base is what asks."""
    provider, _ = make_caa({})
    built = provider.client._build_client()  # noqa: SLF001 - the point of the test

    assert built.follow_redirects is True
    asyncio.run(built.aclose())
    asyncio.run(provider.aclose())


def test_cover_art_needs_no_configuration() -> None:
    provider, _ = make_caa({})
    assert provider.is_ready() is True
    assert provider.supports(EnrichmentEntity.ARTIST) is False
    asyncio.run(provider.aclose())


# ---------------------------------------------------------------------------
# Wikidata / Wikipedia
# ---------------------------------------------------------------------------
def wikidata_entity(
    *, image: str | None = "Joe Bonamassa.jpg", isni: str | None = "0000000081895264",
    enwiki: str | None = "Joe Bonamassa",
) -> dict[str, Any]:
    claims: dict[str, Any] = {}
    if image:
        claims["P18"] = [{"mainsnak": {"datavalue": {"value": image}}}]
    if isni:
        claims["P213"] = [{"mainsnak": {"datavalue": {"value": isni}}}]
    entity: dict[str, Any] = {"id": QID, "claims": claims}
    if enwiki:
        entity["sitelinks"] = {"enwiki": {"title": enwiki}}
    return {"entities": {QID: entity}}


def wikipedia_summary(
    *, extract: str = "An American blues rock guitarist.", page: str | None = None
) -> dict[str, Any]:
    payload: dict[str, Any] = {"extract": extract}
    if page is not None:
        payload["content_urls"] = {"desktop": {"page": page}}
    return payload


def make_wikidata(
    wd_routes: dict[str, Any], wp_routes: dict[str, Any]
) -> tuple[WikidataProvider, list[str], list[str]]:
    wd_hits: list[str] = []
    wp_hits: list[str] = []
    settings = make_settings()
    limiter = make_limiter()
    return (
        WikidataProvider(
            limiter=limiter,
            settings=settings,
            client=WikidataClient(
                limiter=limiter,
                settings=settings,
                http_client=transport(wd_routes, wd_hits, "https://www.wikidata.org/"),
            ),
            wikipedia=WikipediaClient(
                limiter=limiter,
                settings=settings,
                http_client=transport(
                    wp_routes, wp_hits, "https://en.wikipedia.org/api/rest_v1/"
                ),
            ),
        ),
        wd_hits,
        wp_hits,
    )


def artist_job(**overrides: Any) -> EnrichmentJob:
    base: dict[str, Any] = {"id": "a1", "name": "Joe Bonamassa"}
    base.update(overrides)
    return EnrichmentJob(
        entity_type=EnrichmentEntity.ARTIST,
        source=EnrichmentSource.WIKIDATA,
        entity_id=base["id"],
        subject=ArtistSnapshot(**base),
    )


def test_the_biography_arrives_with_its_licence() -> None:
    """CC BY-SA is share-alike: the attribution is a licence term, not a nicety."""
    provider, _, _ = make_wikidata(
        {f"/wiki/Special:EntityData/{QID}.json": wikidata_entity()},
        {
            "/api/rest_v1/page/summary/Joe Bonamassa": wikipedia_summary(
                page="https://en.wikipedia.org/wiki/Joe_Bonamassa"
            )
        },
    )

    fields = run(provider, artist_job(wikidata_qid=QID)).artist_fields

    assert fields["bio"] == "An American blues rock guitarist."
    assert fields["bio_source_url"] == "https://en.wikipedia.org/wiki/Joe_Bonamassa"
    assert fields["bio_licence"] == WIKIPEDIA_LICENCE


def test_an_extract_with_no_source_url_is_not_stored() -> None:
    """Republishing share-alike text with no attribution is a licence breach, so
    the two are written together or not at all."""
    provider, _, _ = make_wikidata(
        {f"/wiki/Special:EntityData/{QID}.json": wikidata_entity()},
        {"/api/rest_v1/page/summary/Joe Bonamassa": wikipedia_summary(page=None)},
    )

    fields = run(provider, artist_job(wikidata_qid=QID)).artist_fields

    assert "bio" not in fields
    assert fields["isni"] == "0000000081895264", "the rest still lands"


def test_wikidata_supplies_the_isni_musicbrainz_lacks() -> None:
    """MusicBrainz has none for this artist; Wikidata does. This is usually where
    an artist's real-world identifier actually comes from."""
    provider, _, _ = make_wikidata(
        {f"/wiki/Special:EntityData/{QID}.json": wikidata_entity()}, {}
    )

    assert (
        run(provider, artist_job(wikidata_qid=QID)).artist_fields["isni"]
        == "0000000081895264"
    )


def test_a_malformed_isni_is_dropped_rather_than_stored() -> None:
    provider, _, _ = make_wikidata(
        {f"/wiki/Special:EntityData/{QID}.json": wikidata_entity(isni="nope")}, {}
    )

    assert "isni" not in run(provider, artist_job(wikidata_qid=QID)).artist_fields


def test_the_commons_portrait_uses_a_stable_url() -> None:
    """``Special:FilePath`` redirects to wherever the file lives, so it keeps
    working when Commons reorganises storage."""
    provider, _, _ = make_wikidata(
        {f"/wiki/Special:EntityData/{QID}.json": wikidata_entity()}, {}
    )

    url = run(provider, artist_job(wikidata_qid=QID)).artist_fields["commons_image_url"]

    assert url.startswith("https://commons.wikimedia.org/wiki/Special:FilePath/")
    assert "Joe_Bonamassa.jpg" in url
    assert "width=" in url, "a full-size original is routinely a 20MB scan"


def test_an_entity_with_no_article_still_yields_its_identifiers() -> None:
    provider, _, wp_hits = make_wikidata(
        {f"/wiki/Special:EntityData/{QID}.json": wikidata_entity(enwiki=None)}, {}
    )

    fields = run(provider, artist_job(wikidata_qid=QID)).artist_fields

    assert wp_hits == [], "no article, so Wikipedia is not asked"
    assert fields["isni"]


def test_without_a_qid_nothing_is_asked() -> None:
    """There is no search here, and there must not be: "which article is about
    this musician" is the question that goes wrong quietly."""
    provider, wd_hits, wp_hits = make_wikidata({}, {})

    with pytest.raises(NoMatchKey):
        run(provider, artist_job())

    assert wd_hits == [] and wp_hits == []


def test_an_unknown_qid_is_not_a_match() -> None:
    provider, _, _ = make_wikidata({}, {})

    with pytest.raises(NotMatched):
        run(provider, artist_job(wikidata_qid=QID))


def test_an_entity_with_nothing_usable_is_not_a_match() -> None:
    provider, _, _ = make_wikidata(
        {
            f"/wiki/Special:EntityData/{QID}.json": wikidata_entity(
                image=None, isni=None, enwiki=None
            )
        },
        {},
    )

    with pytest.raises(NotMatched):
        run(provider, artist_job(wikidata_qid=QID))


def test_wikidata_handles_artists_only() -> None:
    """Wikipedia has album articles, but matching one to a release is a search."""
    provider, _, _ = make_wikidata({}, {})

    assert provider.supports(EnrichmentEntity.ARTIST) is True
    assert provider.supports(EnrichmentEntity.ALBUM) is False
    assert provider.is_ready() is True
    asyncio.run(provider.aclose())


# ---------------------------------------------------------------------------
# Which artwork actually reaches the page
# ---------------------------------------------------------------------------
def test_deezer_artwork_outranks_the_archive() -> None:
    """The ladder's order is not the artwork's order.

    Cover Art Archive runs *after* Deezer because it needs an id MusicBrainz
    supplies, and for a while that position was mistaken for a preference. It is
    the other way round: Deezer serves the label's own 1000x1000 for a release a
    barcode pinned, while the Archive serves whatever a contributor uploaded —
    a press-kit scan on a good day and a photograph of a jewel case on a bad one.
    """
    from app.models import AlbumMetadata

    both = AlbumMetadata(
        album_id="uyej1o165e870",
        deezer_cover_url="https://cdn-images.dzcdn.net/x/1000x1000.jpg",
        caa_front_url="https://ia.us.archive.org/x/front.jpg",
    )
    assert both.cover_url == "https://cdn-images.dzcdn.net/x/1000x1000.jpg"

    # ...and it is still a fallback, not a filter: with nothing from Deezer the
    # Archive is better than the blank placeholder.
    archive_only = AlbumMetadata(
        album_id="wxl78pvfqlm3b",
        caa_front_url="https://ia.us.archive.org/x/front.jpg",
    )
    assert archive_only.cover_url == "https://ia.us.archive.org/x/front.jpg"
    assert AlbumMetadata(album_id="0884977859300").cover_url is None


def test_qobuz_artwork_still_wins_by_default() -> None:
    """Whatever the enrichment sources found sits *behind* Qobuz's own image,
    which is of the exact release Qobuz sells. Only the opt-in setting reorders
    that, and this is the assertion that keeps the default honest."""
    from app.api.deps import album_to_out
    from app.models import Album, AlbumMetadata, AlbumStatus

    album = Album(
        id="uyej1o165e870",
        artist_id="720076",
        title="Spaces",
        status=AlbumStatus.DOWNLOADED,
        image_url="https://static.qobuz.com/images/covers/aa/bb/x_600.jpg",
        # Column defaults only land on flush, and this album is never persisted.
        tracks_count=0,
        media_count=1,
        hires=False,
        monitored=True,
        release_type="album",
        pin_tags=False,
        freeze_path=False,
        mute_integrity=False,
    )
    meta = AlbumMetadata(
        album_id=album.id,
        deezer_cover_url="https://cdn-images.dzcdn.net/x/1000x1000.jpg",
    )

    assert album_to_out(album, meta=meta).cover_url == album.image_url
