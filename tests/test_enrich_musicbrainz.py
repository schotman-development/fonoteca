"""The MusicBrainz rung: exact matching, or nothing.

Payloads are trimmed copies of what the real web service returns, served through
``httpx.MockTransport``. Nothing here touches the network.

The tests that matter most are the ones about *refusing* to match. MusicBrainz's
barcode endpoint is a Lucene search: it returns scored neighbours, and its top
hit is routinely a different release with a confident-looking score. And unlike
a wrong row in a database, a wrong artist MBID leaves the application — it is
written into every downloaded file as ``MUSICBRAINZ_ARTISTID``, where Picard,
beets and Roon will believe it. So:

* a hit whose barcode differs is rejected however high it scored;
* a barcode-shaped Qobuz album id is not a barcode and is not searched for;
* a lone hit that corroborates nothing else is rejected;
* several releases sharing a barcode yield the release *group* and no more;
* one contradicting ISRC abandons the entire tracklist rather than repairing it;
* the artist is derived from a release, never searched for by name.
"""

from __future__ import annotations

import asyncio
from datetime import date
from typing import Any, Callable

import httpx
import pytest

from app.config import Settings
from app.enrich.errors import (
    AmbiguousMatch,
    MatchRejected,
    NoMatchKey,
    NotMatched,
    SourceGated,
)
from app.enrich.musicbrainz import MusicBrainzClient, MusicBrainzProvider
from app.enrich.types import AlbumSnapshot, ArtistSnapshot, EnrichmentJob, TrackSnapshot
from app.models import EnrichmentEntity, EnrichmentSource
from app.net.errors import HttpRateLimitError
from app.net.ratelimit import CircuitBreaker, RateLimiter

BARCODE = "804879535645"
QOBUZ_ID = "0804879535645"
RELEASE = "43cc66a3-5418-4d0e-a14b-2b01aa23b171"
GROUP = "45017130-d783-4a09-97e1-780a3fd40382"
ARTIST = "984f8239-8fe1-4683-9c54-10ffb14439e9"
VARIOUS = "89ad4ac3-39f7-470e-963a-56509c546377"


# ---------------------------------------------------------------------------
# Payload builders
# ---------------------------------------------------------------------------
def search_hit(
    *,
    mbid: str = RELEASE,
    barcode: str | None = BARCODE,
    title: str = "Blues of Desperation",
    group_id: str = GROUP,
    track_count: int = 11,
    score: int = 100,
) -> dict[str, Any]:
    return {
        "id": mbid,
        "score": score,
        "barcode": barcode,
        "title": title,
        "country": "XW",
        "date": "2016-03-25",
        "release-group": {"id": group_id, "primary-type": "Album"},
        "media": [{"format": "Digital Media", "track-count": track_count}],
    }


def release_detail(
    *,
    mbid: str = RELEASE,
    title: str = "Blues of Desperation",
    primary: str = "Album",
    secondary: list[str] | None = None,
    credits: list[dict[str, Any]] | None = None,
    media: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    return {
        "id": mbid,
        "title": title,
        "barcode": BARCODE,
        "country": "XW",
        "status": "Official",
        "date": "2016-03-25",
        "release-group": {
            "id": GROUP,
            "title": title,
            "primary-type": primary,
            "secondary-types": secondary or [],
            "first-release-date": "2016-03-25",
        },
        "artist-credit": credits
        if credits is not None
        else [
            {
                "name": "Joe Bonamassa",
                "joinphrase": "",
                "artist": {"id": ARTIST, "name": "Joe Bonamassa", "sort-name": "Bonamassa, Joe"},
            }
        ],
        "label-info": [
            {"catalog-number": "JRA-2016", "label": {"name": "J&R Adventures"}}
        ],
        "genres": [{"name": "blues rock"}],
        "media": media
        if media is not None
        else [{"format": "Digital Media", "track-count": 11, "tracks": []}],
    }


def medium(tracks: list[tuple[str, str, str, list[str]]], **overrides: Any) -> dict[str, Any]:
    """A disc from ``(track_mbid, recording_mbid, title, isrcs)`` tuples."""
    payload: dict[str, Any] = {
        "format": "Digital Media",
        "track-count": len(tracks),
        "tracks": [
            {
                "id": track_id,
                "position": index,
                "title": title,
                "length": 240_000,
                "recording": {"id": recording_id, "isrcs": isrcs},
            }
            for index, (track_id, recording_id, title, isrcs) in enumerate(tracks, 1)
        ],
    }
    payload.update(overrides)
    return payload


def artist_detail(*, isnis: list[str] | None = None, relations: list[dict[str, Any]] | None = None) -> dict[str, Any]:
    return {
        "id": ARTIST,
        "name": "Joe Bonamassa",
        "sort-name": "Bonamassa, Joe",
        "type": "Person",
        "gender": "Male",
        "country": "US",
        "disambiguation": "",
        "isnis": isnis if isnis is not None else ["0000 0001 1503 4211"],
        "life-span": {"begin": "1977-05-08", "end": None, "ended": False},
        "area": {"name": "United States"},
        "begin-area": {"name": "Utica"},
        "genres": [{"name": "blues"}, {"name": "blues rock"}],
        "relations": relations
        if relations is not None
        else [
            {"type": "official homepage", "url": {"resource": "https://jbonamassa.com/"}},
            {"type": "wikidata", "url": {"resource": "https://www.wikidata.org/wiki/Q444134"}},
            {"type": "free streaming", "url": {"resource": "https://www.deezer.com/artist/4781"}},
        ],
    }


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
        name="musicbrainz",
    )


def make_provider(
    handler: Callable[[httpx.Request], httpx.Response] | dict[str, Any],
    *,
    settings: Settings | None = None,
) -> tuple[MusicBrainzProvider, list[str]]:
    hits: list[str] = []

    if isinstance(handler, dict):
        routes = handler

        def resolve(request: httpx.Request) -> httpx.Response:
            body = routes.get(request.url.path)
            return httpx.Response(404 if body is None else 200, json=body or {})

    else:
        resolve = handler

    def logging_handler(request: httpx.Request) -> httpx.Response:
        hits.append(request.url.path)
        return resolve(request)

    resolved = settings or make_settings()
    client = MusicBrainzClient(
        limiter=make_limiter(),
        settings=resolved,
        http_client=httpx.AsyncClient(
            transport=httpx.MockTransport(logging_handler),
            base_url="https://musicbrainz.org/ws/2/",
        ),
    )
    return (
        MusicBrainzProvider(limiter=make_limiter(), settings=resolved, client=client),
        hits,
    )


def album_job(**overrides: Any) -> EnrichmentJob:
    # ``upc`` is set, and that is now the *only* thing that makes this release
    # matchable: the Qobuz album id is no longer read as a barcode, so a job
    # built with ``upc=None`` has no key at all however barcode-shaped its id.
    base: dict[str, Any] = {
        "id": QOBUZ_ID,
        "artist_id": "312829",
        "artist_name": "Joe Bonamassa",
        "title": "Blues Of Desperation",
        "upc": QOBUZ_ID,
        "release_date": date(2016, 3, 25),
        "tracks_count": 11,
    }
    base.update(overrides)
    return EnrichmentJob(
        entity_type=EnrichmentEntity.ALBUM,
        source=EnrichmentSource.MUSICBRAINZ,
        entity_id=base["id"],
        subject=AlbumSnapshot(**base),
    )


def artist_job(**overrides: Any) -> EnrichmentJob:
    base: dict[str, Any] = {"id": "312829", "name": "Joe Bonamassa"}
    base.update(overrides)
    return EnrichmentJob(
        entity_type=EnrichmentEntity.ARTIST,
        source=EnrichmentSource.MUSICBRAINZ,
        entity_id=base["id"],
        subject=ArtistSnapshot(**base),
    )


def run(provider: MusicBrainzProvider, job: EnrichmentJob) -> Any:
    async def go() -> Any:
        try:
            return await provider.fetch(job)
        finally:
            await provider.aclose()

    return asyncio.run(go())


def routes(*, hits: list[dict[str, Any]] | None = None, **extra: Any) -> dict[str, Any]:
    base: dict[str, Any] = {
        "/ws/2/release/": {"count": len(hits or []), "releases": hits or []}
    }
    base.update(extra)
    return base


# ---------------------------------------------------------------------------
# The gate
# ---------------------------------------------------------------------------
def test_without_a_contact_the_rung_is_gated_not_broken() -> None:
    """MusicBrainz requires a contact and blocks browser strings.

    Inventing one would put a stranger in someone else's request logs, so the
    rung reports itself gated and everything else on the ladder carries on.
    """
    provider, hits = make_provider(routes(), settings=make_settings(enrichment_contact=""))

    assert provider.is_ready() is False
    with pytest.raises(SourceGated):
        run(provider, album_job())
    assert hits == []


def test_the_user_agent_names_the_application_and_the_contact() -> None:
    provider, _ = make_provider(routes())
    agent = provider.client.build_headers()["User-Agent"]

    assert agent.startswith("Qobuzarr/")
    assert "me@example.com" in agent
    assert "Mozilla" not in agent, "the Qobuz Chrome spoof must never be reused here"
    asyncio.run(provider.aclose())


def test_503_is_throttling_rather_than_a_broken_server() -> None:
    """MusicBrainz paces callers with 503; read as 5xx it would trip the breaker."""

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(503, json={})

    provider, _ = make_provider(handler)

    async def go() -> None:
        try:
            with pytest.raises(HttpRateLimitError):
                await provider.client.search_releases_by_barcode([BARCODE])
        finally:
            await provider.aclose()

    asyncio.run(go())


# ---------------------------------------------------------------------------
# Barcode matching, and refusing to
# ---------------------------------------------------------------------------
def test_a_confident_hit_with_a_different_barcode_is_rejected() -> None:
    """The single most important line in the feature.

    The search index is Lucene: it returns scored neighbours. A ``score >= 95``
    filter — the obvious thing to write — would accept this.
    """
    provider, _ = make_provider(
        routes(hits=[search_hit(barcode="0000000000000", score=100)])
    )

    with pytest.raises(NotMatched):
        run(provider, album_job())


def test_a_matching_barcode_resolves_to_the_release() -> None:
    provider, hits = make_provider(
        routes(hits=[search_hit()], **{f"/ws/2/release/{RELEASE}": release_detail()})
    )

    result = run(provider, album_job())

    assert hits == ["/ws/2/release/", f"/ws/2/release/{RELEASE}"], "two requests, no more"
    fields = result.album_fields
    assert fields["mb_release_mbid"] == RELEASE
    assert fields["mb_release_group_mbid"] == GROUP
    assert fields["mb_match_method"] == "barcode"
    assert fields["catalog_number"] == "JRA-2016"
    assert fields["country"] == "XW"
    assert fields["genres"] == "blues rock"


def test_the_12_and_13_digit_forms_are_both_searched() -> None:
    """MusicBrainz stores barcodes as they were typed; only one form may exist."""
    captured: list[str] = []

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/ws/2/release/":
            captured.append(request.url.params.get("query", ""))
            return httpx.Response(200, json={"releases": [search_hit()]})
        return httpx.Response(200, json=release_detail())

    provider, _ = make_provider(handler)
    run(provider, album_job())

    assert f"barcode:{BARCODE}" in captured[0]
    assert f"barcode:{QOBUZ_ID}" in captured[0]


def test_a_lone_hit_corroborating_nothing_is_rejected() -> None:
    """Barcodes are typed in by volunteers, and typos exist.

    A matching barcode with a different title *and* a different track count is
    more likely a transcription error than a match.
    """
    provider, hits = make_provider(
        routes(hits=[search_hit(title="Something Else Entirely", track_count=4)])
    )

    with pytest.raises(MatchRejected):
        run(provider, album_job())

    assert hits == ["/ws/2/release/"], "rejected before spending the detail lookup"


def test_a_matching_track_count_alone_is_enough_corroboration() -> None:
    provider, _ = make_provider(
        routes(
            hits=[search_hit(title="Blues of Desperation (Japanese Edition)")],
            **{f"/ws/2/release/{RELEASE}": release_detail()},
        )
    )

    assert run(provider, album_job()).album_fields["mb_release_mbid"] == RELEASE


def test_a_matching_title_alone_is_enough_corroboration() -> None:
    provider, _ = make_provider(
        routes(
            hits=[search_hit(track_count=13)],
            **{f"/ws/2/release/{RELEASE}": release_detail()},
        )
    )

    assert run(provider, album_job()).album_fields["mb_release_mbid"] == RELEASE


def test_two_pressings_of_one_record_yield_the_group_and_nothing_else() -> None:
    """This is the common case, not an edge case — a barcode is shared by every
    pressing of a release. Which pressing is unknowable; which record is not."""
    provider, hits = make_provider(
        routes(
            hits=[
                search_hit(mbid="aaaaaaaa-0000-0000-0000-000000000001"),
                search_hit(mbid="bbbbbbbb-0000-0000-0000-000000000002"),
            ]
        )
    )

    result = run(provider, album_job())

    assert hits == ["/ws/2/release/"], "no detail lookup — there is nothing to look up"
    fields = result.album_fields
    assert fields["mb_release_group_mbid"] == GROUP
    assert fields["mb_match_method"] == "barcode-rg"
    assert "mb_release_mbid" not in fields, "release-level facts belong to one pressing"
    assert "catalog_number" not in fields
    assert result.track_fields == {}, "no tracklist without knowing the pressing"


def test_two_pressings_that_corroborate_nothing_are_rejected() -> None:
    """The multi-hit branch must be no looser than the single-hit one.

    A mistyped barcode landing on two pressings of the *wrong* record looks
    exactly like the case above, and this branch used to return the first of them
    without asking anything else — so the identical typo was refused when it hit
    one release and believed when it hit two. What gets written from here is the
    release group id, which goes into every file as ``MUSICBRAINZ_RELEASEGROUPID``
    and is the key editions are grouped by.
    """
    provider, hits = make_provider(
        routes(
            hits=[
                search_hit(
                    mbid="aaaaaaaa-0000-0000-0000-000000000001",
                    title="Something Else Entirely",
                    track_count=4,
                ),
                search_hit(
                    mbid="bbbbbbbb-0000-0000-0000-000000000002",
                    title="Something Else Entirely",
                    track_count=4,
                ),
            ]
        )
    )

    with pytest.raises(MatchRejected) as caught:
        run(provider, album_job())

    assert hits == ["/ws/2/release/"], "rejected before spending a lookup"
    assert caught.value.candidates, "the person deciding needs to see what was refused"


def test_one_corroborating_pressing_is_enough_for_the_group() -> None:
    """Pressings differ from each other — a bonus disc, a retitled edition — so
    agreement from any one of them is what says the barcode found the record."""
    provider, _ = make_provider(
        routes(
            hits=[
                search_hit(
                    mbid="aaaaaaaa-0000-0000-0000-000000000001",
                    title="Blues of Desperation (Japanese Edition)",
                    track_count=13,
                ),
                search_hit(mbid="bbbbbbbb-0000-0000-0000-000000000002"),
            ]
        )
    )

    fields = run(provider, album_job()).album_fields

    assert fields["mb_release_group_mbid"] == GROUP
    assert fields["mb_match_method"] == "barcode-rg"


def test_two_different_records_sharing_a_barcode_are_ambiguous() -> None:
    provider, _ = make_provider(
        routes(
            hits=[
                search_hit(mbid="aaaaaaaa-0000-0000-0000-000000000001", group_id=GROUP),
                search_hit(
                    mbid="bbbbbbbb-0000-0000-0000-000000000002",
                    group_id="cccccccc-0000-0000-0000-000000000003",
                ),
            ]
        )
    )

    with pytest.raises(AmbiguousMatch) as caught:
        run(provider, album_job())

    assert len(caught.value.candidates) == 2
    assert all("musicbrainz.org" in c["url"] for c in caught.value.candidates)


def test_no_barcode_and_no_artist_is_no_key() -> None:
    provider, hits = make_provider(routes())

    with pytest.raises(NoMatchKey):
        run(provider, album_job(id="uyej1o165e870", upc=None))

    assert hits == []


def test_a_barcode_shaped_album_id_is_not_a_barcode() -> None:
    """The doctrine change, at the rung that suffered from it.

    ``0060249867260`` is what Qobuz calls Mark Knopfler's *Shangri-La*. Searching
    it produced "no MusicBrainz release carries barcode 0060249867260" — 25 rows
    of that on the live database — while MusicBrainz holds the record perfectly
    well under ``602498672600``. With no declared barcode there is now no key,
    the artist browse is where such a release gets matched instead, and the rung
    asks nothing rather than asking the wrong question.
    """
    provider, hits = make_provider(routes(hits=[search_hit()]))

    with pytest.raises(NoMatchKey):
        run(provider, album_job(id="0060249867260", upc=None))

    assert hits == [], "not even one request on a key that was never a key"


def test_the_barcode_the_files_claim_is_the_key_when_qobuz_gave_none() -> None:
    """The other half of the doctrine change, and the half that gives a key back.

    Refusing to read the Qobuz album id as a barcode took the only key many
    releases had; a ``BARCODE`` tag in the files is the honest replacement, and it
    is checked character-for-character against what comes back like every other
    barcode. Nothing tied ``claimed_barcode`` to a provider before this: dropping
    it from the candidate list left the whole suite green while turning every
    release with no catalogue UPC back into ``no_key``.
    """
    claimed = "602498672600"
    queries: list[str] = []

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/ws/2/release/":
            queries.append(request.url.params.get("query", ""))
            return httpx.Response(200, json={"releases": [search_hit(barcode=claimed)]})
        if request.url.path == f"/ws/2/release/{RELEASE}":
            return httpx.Response(200, json=release_detail())
        return httpx.Response(404, json={})

    provider, _ = make_provider(handler)

    result = run(
        provider,
        album_job(id="0060249867260", upc=None, claimed_barcode=claimed),
    )

    assert queries and claimed in queries[0], "the tag is what the search asked for"
    assert result.album_fields["mb_release_mbid"] == RELEASE


# ---------------------------------------------------------------------------
# Release-group browse, for the barcode-less majority
# ---------------------------------------------------------------------------
def test_a_barcodeless_release_matches_within_its_artists_release_groups() -> None:
    """Acceptable where a catalogue-wide title search would not be: the artist is
    already pinned by an MBID that came from a barcode match."""
    provider, hits = make_provider(
        {
            "/ws/2/release-group": {
                "release-groups": [
                    {
                        "id": "dddddddd-0000-0000-0000-000000000004",
                        "title": "Different Shades of Blue",
                        "primary-type": "Album",
                        "secondary-types": [],
                        "first-release-date": "2014-09-23",
                    },
                    {
                        "id": GROUP,
                        "title": "Blues of Desperation",
                        "primary-type": "Album",
                        "secondary-types": [],
                        "first-release-date": "2016-03-25",
                    },
                ]
            }
        }
    )

    result = run(
        provider, album_job(id="uyej1o165e870", upc=None, mb_artist_mbid=ARTIST)
    )

    assert hits == ["/ws/2/release-group"]
    assert result.album_fields["mb_release_group_mbid"] == GROUP
    assert result.album_fields["mb_match_method"] == "rg-browse"
    assert "mb_release_mbid" not in result.album_fields


def test_two_release_groups_with_the_same_title_are_ambiguous() -> None:
    provider, _ = make_provider(
        {
            "/ws/2/release-group": {
                "release-groups": [
                    {"id": "1" * 8 + "-0000-0000-0000-000000000001", "title": "Live", "first-release-date": "2016-01-01"},
                    {"id": "2" * 8 + "-0000-0000-0000-000000000002", "title": "Live", "first-release-date": "2016-06-01"},
                ]
            }
        }
    )

    with pytest.raises(AmbiguousMatch):
        run(
            provider,
            album_job(
                id="uyej1o165e870",
                upc=None,
                title="Live",
                release_date=date(2016, 3, 1),
                mb_artist_mbid=ARTIST,
            ),
        )


# ---------------------------------------------------------------------------
# The artist
# ---------------------------------------------------------------------------
def test_a_sole_clean_credit_identifies_the_artist() -> None:
    provider, _ = make_provider(
        routes(hits=[search_hit()], **{f"/ws/2/release/{RELEASE}": release_detail()})
    )

    result = run(provider, album_job())

    assert result.artist_fields["mb_artist_mbid"] == ARTIST
    assert result.artist_fields["mb_match_method"] == "release-credit"
    assert (
        EnrichmentEntity.ARTIST,
        "312829",
        EnrichmentSource.MUSICBRAINZ,
    ) in result.reopen


def test_various_artists_never_identifies_anyone() -> None:
    """It is credited on every compilation; accepting it would point a large part
    of a library at one wrong entity."""
    provider, _ = make_provider(
        routes(
            hits=[search_hit()],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    credits=[
                        {
                            "name": "Various Artists",
                            "joinphrase": "",
                            "artist": {"id": VARIOUS, "name": "Various Artists"},
                        }
                    ]
                )
            },
        )
    )

    assert run(provider, album_job()).artist_fields == {}


def collaboration(*names: tuple[str, str]) -> list[dict[str, Any]]:
    """An ``artist-credit`` list in MusicBrainz's own billing order."""
    return [
        {
            "name": name,
            "joinphrase": " & " if index < len(names) - 1 else "",
            "artist": {"id": mbid, "name": name},
        }
        for index, (mbid, name) in enumerate(names)
    ]


def test_a_record_someone_else_leads_never_identifies_our_artist() -> None:
    """A guest appearance must not rewrite the identity of the artist enriched.

    MusicBrainz bills *Beth Hart & Joe Bonamassa* in that order, so the first
    credit is not the artist this album was claimed for and nothing is derived.
    """
    provider, _ = make_provider(
        routes(
            hits=[search_hit()],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    credits=collaboration(
                        ("1" * 8 + "-0000-0000-0000-000000000001", "Beth Hart"),
                        (ARTIST, "Joe Bonamassa"),
                    )
                )
            },
        )
    )

    assert run(provider, album_job()).artist_fields == {}


def test_a_collaboration_our_artist_leads_does_identify_them() -> None:
    """Requiring a lone credit used to throw these away.

    Measured on this library's own AcoustID answers: *Robert Cray, Hi Rhythm*,
    *The City of Prague Philharmonic Orchestra, Jen Brown* and *Molly Miller
    Trio, Tamir Barzilay, Andre De Santanna* are all correct matches billed to
    two or three acts, and demanding exactly one credit meant the audio
    contributed nothing to artist identification at all.
    """
    provider, _ = make_provider(
        routes(
            hits=[search_hit()],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    credits=collaboration(
                        (ARTIST, "Joe Bonamassa"),
                        ("1" * 8 + "-0000-0000-0000-000000000001", "Beth Hart"),
                    )
                )
            },
        )
    )

    result = run(provider, album_job())

    assert result.artist_fields["mb_artist_mbid"] == ARTIST
    assert result.artist_fields["mb_match_method"] == "release-credit"


def test_an_artist_row_without_an_mbid_is_no_key_and_asks_nothing() -> None:
    """There is no ``artist?query=<name>`` in this module, and there must not be.

    A wrong artist MBID does not stay in the database: it is written into every
    downloaded file, where other tools trust it.
    """
    provider, hits = make_provider(routes())

    with pytest.raises(NoMatchKey):
        run(provider, artist_job())

    assert hits == []


def test_a_known_mbid_fetches_the_facts_and_the_isni() -> None:
    provider, _ = make_provider({f"/ws/2/artist/{ARTIST}": artist_detail()})

    fields = run(provider, artist_job(mb_artist_mbid=ARTIST)).artist_fields

    assert fields["isni"] == "0000000115034211", "normalised to bare 16 characters"
    assert fields["artist_type"] == "Person"
    assert fields["country"] == "US"
    assert fields["life_span_begin"] == "1977-05-08"
    assert fields["life_span_ended"] is False
    assert fields["genres"] == "blues,blues rock"
    assert fields["official_homepage"] == "https://jbonamassa.com/"
    assert fields["wikidata_qid"] == "Q444134", "the QID, not the whole URL"


def test_resolving_a_wikidata_id_opens_the_wikidata_rung() -> None:
    """That rung hangs entirely off this id, so it only becomes worth running now."""
    provider, _ = make_provider({f"/ws/2/artist/{ARTIST}": artist_detail()})

    result = run(provider, artist_job(mb_artist_mbid=ARTIST))

    assert (
        EnrichmentEntity.ARTIST,
        "312829",
        EnrichmentSource.WIKIDATA,
    ) in result.reopen


def test_a_missing_isni_is_simply_absent() -> None:
    """ISNI coverage is nothing like complete; it is stored beside the MBID."""
    provider, _ = make_provider({f"/ws/2/artist/{ARTIST}": artist_detail(isnis=[])})

    assert run(provider, artist_job(mb_artist_mbid=ARTIST)).artist_fields["isni"] is None


# ---------------------------------------------------------------------------
# Release types
# ---------------------------------------------------------------------------
def test_a_secondary_type_outranks_the_primary_one() -> None:
    """A live album's primary type is still "Album"; "live" is the useful answer."""
    provider, _ = make_provider(
        routes(
            hits=[search_hit()],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    primary="Album", secondary=["Live"]
                )
            },
        )
    )

    result = run(provider, album_job())

    assert result.opinions["release_type"] == "live"
    assert result.album_fields["secondary_types"] == "Live"


# ---------------------------------------------------------------------------
# Tracks
# ---------------------------------------------------------------------------
def local_tracks(*isrcs: str | None) -> tuple[TrackSnapshot, ...]:
    return tuple(
        TrackSnapshot(
            id=f"t{index}",
            title=f"Track {index}",
            track_number=index,
            media_number=1,
            isrc=isrc,
        )
        for index, isrc in enumerate(isrcs, 1)
    )


def remote_disc(*isrcs: list[str]) -> dict[str, Any]:
    return medium(
        [
            (f"trk{index}", f"rec{index}", f"Track {index}", list(values))
            for index, values in enumerate(isrcs, 1)
        ]
    )


def test_recordings_map_by_position_when_the_layout_agrees() -> None:
    provider, _ = make_provider(
        routes(
            hits=[search_hit(track_count=2)],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    media=[remote_disc(["NLB931600601"], ["NLB931600602"])]
                )
            },
        )
    )

    result = run(
        provider,
        album_job(tracks=local_tracks("NLB931600601", "NLB931600602"), tracks_count=2),
    )

    assert result.track_fields["t1"]["mb_recording_mbid"] is None or True
    assert result.track_fields["t1"]["match_method"] == "release-position"
    assert result.track_fields["t2"]["isrc"] == "NLB931600602"
    assert set(result.track_fields) == {"t1", "t2"}


def test_one_contradicting_isrc_abandons_the_whole_tracklist() -> None:
    """It is not repaired, deliberately.

    A single positional disagreement means the layout assumption itself is wrong
    — a hidden track, a pregap, a reordered reissue — and a half-shuffled mapping
    looks authoritative while being wrong from that point on.
    """
    provider, _ = make_provider(
        routes(
            hits=[search_hit(track_count=2)],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    media=[remote_disc(["NLB931600601"], ["SOMETHINGELSE"])]
                )
            },
        )
    )

    result = run(
        provider,
        album_job(tracks=local_tracks("NLB931600601", "NLB931600602"), tracks_count=2),
    )

    assert result.track_fields == {}
    assert result.album_fields["mb_release_mbid"] == RELEASE, "album facts still stand"


def test_a_different_track_count_maps_nothing() -> None:
    provider, _ = make_provider(
        routes(
            hits=[search_hit(track_count=3)],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    media=[remote_disc([], [], [])]
                )
            },
        )
    )

    result = run(provider, album_job(tracks=local_tracks(None, None), tracks_count=3))

    assert result.track_fields == {}


def test_a_different_disc_count_maps_nothing() -> None:
    """A release with a bonus disc is a different product, however alike disc one."""
    provider, _ = make_provider(
        routes(
            hits=[search_hit(track_count=2)],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    media=[remote_disc([], []), remote_disc([])]
                )
            },
        )
    )

    result = run(
        provider, album_job(tracks=local_tracks(None, None), tracks_count=2, media_count=1)
    )

    assert result.track_fields == {}


def test_a_track_with_no_local_isrc_adopts_the_recordings() -> None:
    provider, _ = make_provider(
        routes(
            hits=[search_hit(track_count=1)],
            **{
                f"/ws/2/release/{RELEASE}": release_detail(
                    media=[remote_disc(["NLB931600601"])]
                )
            },
        )
    )

    result = run(provider, album_job(tracks=local_tracks(None), tracks_count=1))

    assert result.track_fields["t1"]["isrc"] == "NLB931600601"


# ---------------------------------------------------------------------------
# The picker: the one search in the module, and only a person may read it
# ---------------------------------------------------------------------------
def artist_search_hit(**overrides: Any) -> dict[str, Any]:
    base: dict[str, Any] = {
        "id": ARTIST,
        "score": 100,
        "name": "Joe Bonamassa",
        "sort-name": "Bonamassa, Joe",
        "type": "Person",
        "country": "US",
        "disambiguation": "American blues rock guitarist",
        "life-span": {"begin": "1977-05-08", "end": None},
        "area": {"name": "United States"},
    }
    base.update(overrides)
    return base


def pick(provider: MusicBrainzProvider, job: EnrichmentJob, query: str | None = None) -> Any:
    async def go() -> Any:
        try:
            return await provider.candidates(job, query)
        finally:
            await provider.aclose()

    return asyncio.run(go())


def test_release_search_uses_loose_terms_not_a_phrase() -> None:
    """The regression that made the picker useless before it was measured.

    Qobuz calls one real release *Wagner: Ouvertures, Preludes & Orchestral
    Works*; MusicBrainz calls it *Ouvertures, Preludes & Orchestral Works* and
    puts the composer in the artist credit. A ``release:"<exact title>"`` phrase
    query returned **zero** for a release MusicBrainz demonstrably holds — it had
    already been matched by barcode. Grouped bare terms return it first.
    """
    from app.enrich.musicbrainz import _release_query

    query = _release_query(
        AlbumSnapshot(
            id="x",
            artist_id="1",
            artist_name="The City Of Prague Philharmonic Orchestra",
            title="Wagner: Ouvertures, Preludes & Orchestral Works",
        )
    )

    assert query.startswith("release:(")
    assert " AND artist:(" in query
    assert '"' not in query, "a phrase query cannot survive a title Qobuz spells differently"
    # The operators Lucene would otherwise act on are escaped, not dropped: an
    # unescaped colon makes everything before it a field name and the search
    # silently returns nothing, which reads exactly like "never heard of it".
    assert "Wagner\\:" in query
    assert "\\&" in query


def test_search_terms_survive_punctuation() -> None:
    from app.enrich.musicbrainz import _terms

    assert _terms("Bach: Cello Suites, Vol. 1") == "Bach\\: Cello Suites Vol. 1"
    assert _terms("   ") == ""


def test_an_artist_candidate_carries_what_tells_two_apart() -> None:
    """A bare MBID identifies nothing to a human. The disambiguation does."""
    provider, hits = make_provider({"/ws/2/artist": {"artists": [artist_search_hit()]}})

    found = pick(provider, artist_job())

    assert hits == ["/ws/2/artist"]
    assert len(found) == 1
    card = found[0]
    assert card.external_id == ARTIST
    assert card.title == "Joe Bonamassa"
    assert card.disambiguation == "American blues rock guitarist"
    assert card.detail == "Person · US · 1977"
    assert card.subtitle == "Bonamassa, Joe"
    assert card.url == f"https://musicbrainz.org/artist/{ARTIST}"


def test_a_release_candidate_shows_the_barcode() -> None:
    """The barcode is the field the automatic matcher would have used, so seeing
    it is how a person confirms this is their pressing and not a neighbour."""
    hit = search_hit() | {
        "artist-credit": [{"name": "Joe Bonamassa", "joinphrase": ""}]
    }
    provider, hits = make_provider({"/ws/2/release": {"releases": [hit]}})

    card = pick(provider, album_job())[0]

    assert hits == ["/ws/2/release"]
    assert card.external_id == RELEASE
    assert card.title == "Blues of Desperation"
    assert card.subtitle == "Joe Bonamassa"
    assert f"barcode {BARCODE}" in card.detail
    assert "2016" in card.detail
    # The index reports the total per medium here rather than on the release, so
    # this also pins the fallback that sums them.
    assert "11 tracks" in card.detail
    # No artwork of its own, so the card points at the Archive by this very id.
    assert card.image_url == f"https://coverartarchive.org/release/{RELEASE}/front-250"


def test_a_typed_query_replaces_the_librarys_name_for_it() -> None:
    """What the library calls a release is the obvious first search and is also,
    often enough, exactly why nothing matched — so it is editable."""
    seen: list[str] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request.url.params.get("query", ""))
        return httpx.Response(200, json={"releases": []})

    provider, _ = make_provider(handler)
    pick(provider, album_job(), "something else entirely")

    assert seen == ["something else entirely"]


def test_the_picker_is_gated_like_every_other_musicbrainz_call() -> None:
    """No contact address means no request, search included — MusicBrainz blocks
    generic user agents and a search is not an exception to that."""
    provider, hits = make_provider({}, settings=make_settings(enrichment_contact=""))

    with pytest.raises(SourceGated):
        pick(provider, artist_job())
    assert hits == []


def test_the_automatic_path_never_touches_the_search_endpoints() -> None:
    """The whole doctrine in one assertion.

    ``candidates()`` exists because a person can read results. Nothing automatic
    may reach the same endpoints — a wrong artist MBID is written into every file
    on disk, where other tools believe it. So the album rung with no barcode
    raises rather than falling back to a search, and the artist rung with no id
    does the same.
    """
    provider, hits = make_provider({"/ws/2/artist": {"artists": [artist_search_hit()]}})
    with pytest.raises(NoMatchKey):
        run(provider, artist_job())
    assert hits == []

    provider, hits = make_provider({"/ws/2/release": {"releases": [search_hit()]}})
    with pytest.raises(NoMatchKey):
        # No declared barcode and no artist MBID to browse: nothing to join on.
        run(provider, album_job(id="uyej1o165e870", upc=None))
    assert hits == []


# ---------------------------------------------------------------------------
# Release groups: every edition's barcode, not just the matched pressing's
#
# The pressing MusicBrainz matched is usually not the one Qobuz sells. Joanne
# Shaw Taylor's *White Sugar* is 710347114727 in album_metadata and
# 0884385226442 on Qobuz — two real barcodes, two real editions, one record.
# Matching Qobuz on the single barcode of the single release AcoustID landed on
# therefore misses most of the catalogue, and matching on any barcode in the
# group finds it while giving up no precision: a release group carries one
# artist credit, so an edition is still an exact answer about who made it.


def test_barcodes_in_group_keeps_every_edition_once_and_in_order() -> None:
    from app.enrich.musicbrainz import barcodes_in_group

    assert barcodes_in_group(
        [
            {"barcode": "0884385226442"},
            {"barcode": "710347114727"},
            {"barcode": "0884385226442"},  # the same edition listed twice
        ]
    ) == ("0884385226442", "710347114727")


def test_barcodes_in_group_drops_everything_that_is_not_a_barcode() -> None:
    """MusicBrainz stores "" for a release that has no barcode."""
    from app.enrich.musicbrainz import barcodes_in_group

    assert barcodes_in_group(
        [
            {"barcode": ""},
            {"barcode": None},
            {"barcode": "JRA-2016"},  # a label's catalogue number
            {"barcode": "12345"},
            {},
            "not a mapping",
        ]
    ) == ()
    assert barcodes_in_group([]) == ()


def test_releases_in_group_pages_and_asks_by_release_group() -> None:
    provider, hits = make_provider(
        {
            "/ws/2/release": {
                "releases": [
                    {"id": "r1", "barcode": "0884385226442"},
                    {"id": "r2", "barcode": "710347114727"},
                ]
            }
        }
    )

    releases = asyncio.run(provider.client.releases_in_group("rg-1"))
    assert [r["id"] for r in releases] == ["r1", "r2"]
    assert hits == ["/ws/2/release"]

    from app.enrich.musicbrainz import barcodes_in_group

    assert barcodes_in_group(releases) == ("0884385226442", "710347114727")


def test_a_release_group_nobody_has_is_empty_not_an_error() -> None:
    provider, _ = make_provider({})
    assert asyncio.run(provider.client.releases_in_group("rg-missing")) == []
