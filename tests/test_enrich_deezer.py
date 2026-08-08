"""The Deezer rung, and the one column enrichment is allowed to write back.

Every response is served by an ``httpx.MockTransport`` handler built from the
shapes Deezer really returns — including the one that matters most: **an error
reported with HTTP 200**. A client that trusted status codes would record a miss
and a quota breach as successes and feed the circuit breaker the wrong signal.

Two halves. First the provider: how a release is matched (barcode, then a browse
of an already-identified artist, then nothing), and how an artist id is derived
from a release rather than searched for. Then the write-back: a majority may
change ``Album.release_type`` and may change **nothing else** — not the status,
not the label, not the genre, because the last two are naming-template tokens and
a "better" value would make the next re-file move thousands of folders.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from datetime import date
from typing import Any, AsyncIterator, Callable, Iterator

import httpx
import pytest
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.config import Settings
from app.core.enricher import Enricher
from app.enrich.deezer import DeezerClient, DeezerProvider
from app.enrich.errors import AmbiguousMatch, NoMatchKey, NotMatched
from app.enrich.merge import decode_consensus
from app.enrich.types import AlbumSnapshot, ArtistSnapshot, EnrichmentJob, TrackSnapshot
from app.models import (
    Album,
    AlbumMetadata,
    AlbumStatus,
    Artist,
    ArtistMetadata,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    Track,
    TrackStatus,
)
from app.net.ratelimit import CircuitBreaker, RateLimiter

BARCODE = "0804879535645"


# ---------------------------------------------------------------------------
# Payload builders, shaped like the real API
# ---------------------------------------------------------------------------
def deezer_album(
    *,
    album_id: str = "11054404",
    title: str = "Blues Of Desperation",
    upc: str | None = BARCODE,
    record_type: str = "album",
    release_date: str = "2016-03-25",
    contributors: list[dict[str, Any]] | None = None,
    tracks: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "id": album_id,
        "title": title,
        "upc": upc,
        "label": "J&R Adventures",
        "nb_tracks": 11,
        "record_type": record_type,
        "release_date": release_date,
        "cover_xl": "https://cdn-images.dzcdn.net/images/cover/x/1000x1000.jpg",
        "genres": {"data": [{"id": 152, "name": "Rock"}, {"id": 153, "name": "Blues"}]},
        "contributors": contributors
        if contributors is not None
        else [
            {
                "id": "1424",
                "name": "Joe Bonamassa",
                "role": "Main",
                "picture_xl": "https://cdn-images.dzcdn.net/images/artist/x/1000x1000.jpg",
            }
        ],
    }
    if tracks is not None:
        payload["tracks"] = {"data": tracks}
    return payload


def deezer_error(kind: str = "DataException", message: str = "no data") -> dict[str, Any]:
    """Deezer's miss: HTTP 200, with the failure hidden in the body."""
    return {"error": {"type": kind, "message": message}}


# ---------------------------------------------------------------------------
# Harness
# ---------------------------------------------------------------------------
def make_settings(**overrides: Any) -> Settings:
    base: dict[str, Any] = {
        "qobuz_app_id": "app",
        "qobuz_user_auth_token": "token",
        "enrichment_sources": "deezer",
        "backoff_initial": 0.0,
        "backoff_max": 0.0,
        "backoff_jitter": 0.0,
        "request_max_retries": 2,
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
        name="deezer",
    )


def make_provider(
    routes: dict[str, Any] | Callable[[httpx.Request], httpx.Response],
    *,
    settings: Settings | None = None,
) -> tuple[DeezerProvider, list[str]]:
    """A provider whose transport answers from *routes*, plus the request log."""
    hits: list[str] = []

    if callable(routes):
        handler = routes
    else:

        def handler(request: httpx.Request) -> httpx.Response:
            body = routes.get(request.url.path)
            if body is None:
                return httpx.Response(200, json=deezer_error())
            return httpx.Response(200, json=body)

    def logging_handler(request: httpx.Request) -> httpx.Response:
        hits.append(request.url.path)
        return handler(request)

    resolved = settings or make_settings()
    client = DeezerClient(
        limiter=make_limiter(),
        settings=resolved,
        http_client=httpx.AsyncClient(
            transport=httpx.MockTransport(logging_handler),
            base_url="https://api.deezer.com/",
        ),
    )
    return DeezerProvider(limiter=make_limiter(), settings=resolved, client=client), hits


def album_snapshot(**overrides: Any) -> AlbumSnapshot:
    # ``upc`` is what makes this release matchable, and the album id no longer
    # is: Qobuz's id is not read as a barcode, so ``upc=None`` here means no key
    # at all no matter how barcode-shaped the id beside it looks.
    base: dict[str, Any] = {
        "id": BARCODE,
        "artist_id": "312829",
        "artist_name": "Joe Bonamassa",
        "title": "Blues Of Desperation",
        "upc": BARCODE,
        "release_date": date(2016, 3, 25),
        "tracks_count": 11,
    }
    base.update(overrides)
    return AlbumSnapshot(**base)


def album_job(**overrides: Any) -> EnrichmentJob:
    return EnrichmentJob(
        entity_type=EnrichmentEntity.ALBUM,
        source=EnrichmentSource.DEEZER,
        entity_id=overrides.get("id", BARCODE),
        subject=album_snapshot(**overrides),
    )


def artist_job(**overrides: Any) -> EnrichmentJob:
    base: dict[str, Any] = {"id": "312829", "name": "Joe Bonamassa"}
    base.update(overrides)
    return EnrichmentJob(
        entity_type=EnrichmentEntity.ARTIST,
        source=EnrichmentSource.DEEZER,
        entity_id=base["id"],
        subject=ArtistSnapshot(**base),
    )


def run(provider: DeezerProvider, job: EnrichmentJob) -> Any:
    async def go() -> Any:
        try:
            return await provider.fetch(job)
        finally:
            await provider.aclose()

    return asyncio.run(go())


# ---------------------------------------------------------------------------
# Matching an album
# ---------------------------------------------------------------------------
def test_a_declared_upc_matches_by_barcode() -> None:
    provider, hits = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    result = run(provider, album_job(id="uyej1o165e870"))

    assert hits == [f"/album/upc:{BARCODE}"]
    assert result.album_fields["barcode"] == BARCODE
    assert result.album_fields["barcode_source"] == "upc"
    assert result.album_fields["deezer_match_method"] == "barcode"
    assert result.album_fields["deezer_album_id"] == "11054404"


def test_a_barcode_shaped_album_id_is_not_a_key() -> None:
    """The rung that used to get in on the album id, and how it got in wrong.

    Qobuz numbers a great many releases with something that looks exactly like a
    barcode and is not one — ``0060249867260`` for *Shangri-La*, whose real
    barcode is ``602498672600``. With no declared ``upc`` and no artist to
    browse there is now no key, and the honest answer is to ask nothing.
    """
    provider, hits = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    with pytest.raises(NoMatchKey):
        run(provider, album_job(upc=None))

    assert hits == [], "the id was never a barcode, so it is never asked about"


def test_the_barcode_source_survives_a_12_vs_13_digit_difference() -> None:
    """Deezer answers with the 12-digit UPC-A where Qobuz declares the 13-digit EAN.

    Compared as strings, every match would be credited to Deezer and the fact
    that Qobuz supplied the key would be lost.
    """
    provider, _ = make_provider(
        {f"/album/upc:{BARCODE}": deezer_album(upc="804879535645")}
    )

    result = run(provider, album_job())

    assert result.album_fields["barcode_source"] == "upc"


def test_a_barcode_learned_elsewhere_is_a_key_too() -> None:
    """What replaces the album-id fallback: a barcode from a field that is one.

    Here it is an earlier pass's finding — a ``BARCODE`` tag read off the files,
    or one MusicBrainz returned — carried on the snapshot.
    """
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    result = run(provider, album_job(id="uyej1o165e870", upc=None, barcode=BARCODE))

    assert result.album_fields["deezer_match_method"] == "barcode"
    assert result.album_fields["barcode_source"] == "deezer", (
        "Qobuz did not declare it, so Deezer is where this run got it"
    )


def test_the_barcode_the_files_claim_is_a_key_too() -> None:
    """The lowest-trust entry in the list, and the one that gives a key back.

    Nothing joined ``claimed_barcode`` to a provider before this — it was tested
    as a pure function and as a snapshot field — so removing it from the
    candidate list left the whole suite green while turning every release with no
    catalogue UPC back into ``no_key``. It is a hypothesis, so it only decides
    what to *ask*: ``album_by_barcode`` still checks the echoed ``upc``.
    """
    provider, hits = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    result = run(
        provider, album_job(id="uyej1o165e870", upc=None, claimed_barcode=BARCODE)
    )

    assert hits == [f"/album/upc:{BARCODE}"], "the tag is what was asked about"
    assert result.album_fields["deezer_match_method"] == "barcode"


def test_deezer_answering_about_a_different_release_is_rejected() -> None:
    """The echoed ``upc`` is verified, not trusted — the whole match hangs on it."""
    provider, _ = make_provider(
        {f"/album/upc:{BARCODE}": deezer_album(upc="0000000000000")}
    )

    with pytest.raises(NotMatched):
        run(provider, album_job())


def test_an_error_hidden_in_a_200_response_is_a_miss_not_a_success() -> None:
    provider, _ = make_provider({})  # every path answers with the error object

    with pytest.raises(NotMatched):
        run(provider, album_job())


def test_no_barcode_and_no_known_artist_is_no_key() -> None:
    """Terminal until the inputs change — never a reason to fall back on a title."""
    provider, hits = make_provider({})

    with pytest.raises(NoMatchKey):
        run(provider, album_job(id="uyej1o165e870", upc=None))

    assert hits == [], "nothing to search with means nothing was asked"


def test_genres_labels_and_cover_are_mapped() -> None:
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    fields = run(provider, album_job()).album_fields

    assert fields["genres"] == "Rock,Blues"
    assert fields["label"] == "J&R Adventures"
    assert fields["deezer_cover_url"].endswith("1000x1000.jpg")
    assert fields["track_count"] == 11


# ---------------------------------------------------------------------------
# The browse path — for the releases with no barcode at all
# ---------------------------------------------------------------------------
def test_a_barcodeless_release_matches_within_the_artists_discography() -> None:
    """The artist is already pinned, so this is not a catalogue-wide search."""
    listing = {
        "data": [
            {"id": "1", "title": "Different Shades of Blue", "release_date": "2014-09-23"},
            {"id": "2", "title": "Blues Of Desperation", "release_date": "2016-03-25"},
        ]
    }
    provider, hits = make_provider(
        {
            "/artist/1424/albums": listing,
            "/album/2": deezer_album(album_id="2", upc="0888072000001"),
        }
    )

    result = run(
        provider, album_job(id="uyej1o165e870", upc=None, deezer_artist_id="1424")
    )

    assert "/artist/1424/albums" in hits
    assert result.album_fields["deezer_match_method"] == "artist-browse"
    # The point of the whole path: a barcode for a release Qobuz gave none for,
    # which is what makes MusicBrainz reachable later.
    assert result.album_fields["barcode"] == "0888072000001"
    assert result.album_fields["barcode_source"] == "deezer"


def test_two_candidates_in_the_discography_are_ambiguous() -> None:
    listing = {
        "data": [
            {"id": "1", "title": "Live At The Fillmore", "release_date": "2016-01-01"},
            {"id": "2", "title": "Live at the Fillmore", "release_date": "2016-06-01"},
        ]
    }
    provider, _ = make_provider({"/artist/1424/albums": listing})

    with pytest.raises(AmbiguousMatch) as caught:
        run(
            provider,
            album_job(
                id="uyej1o165e870",
                upc=None,
                title="Live At The Fillmore",
                release_date=date(2016, 3, 1),
                deezer_artist_id="1424",
            ),
        )

    assert len(caught.value.candidates) == 2, "both go to a human, neither is chosen"


def test_a_release_from_the_wrong_year_is_not_a_match() -> None:
    listing = {
        "data": [{"id": "1", "title": "Blues Of Desperation", "release_date": "2004-01-01"}]
    }
    provider, _ = make_provider({"/artist/1424/albums": listing})

    with pytest.raises(NotMatched):
        run(provider, album_job(id="uyej1o165e870", upc=None, deezer_artist_id="1424"))


# ---------------------------------------------------------------------------
# Deriving the artist
# ---------------------------------------------------------------------------
def test_a_sole_main_contributor_identifies_the_artist() -> None:
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    result = run(provider, album_job())

    assert result.artist_fields["deezer_artist_id"] == "1424"
    assert result.artist_fields["deezer_match_method"] == "contributor"
    # Knowing the artist makes their own row worth running, and every other
    # release of theirs browsable.
    assert (
        EnrichmentEntity.ARTIST,
        "312829",
        EnrichmentSource.DEEZER,
    ) in result.reopen


def test_a_record_someone_else_is_billed_first_on_identifies_nobody() -> None:
    """Deezer lists contributors in billing order, so this is *Beth Hart's*
    record with a guest on it, and it must not rewrite who Joe Bonamassa is."""
    provider, _ = make_provider(
        {
            f"/album/upc:{BARCODE}": deezer_album(
                contributors=[
                    {"id": "9", "name": "Beth Hart", "role": "Main"},
                    {"id": "1424", "name": "Joe Bonamassa", "role": "Main"},
                ]
            )
        }
    )

    result = run(provider, album_job())

    assert result.artist_fields == {}
    assert result.reopen == []


def test_a_headliner_billed_with_their_band_still_identifies_them() -> None:
    """The gate that used to discard *Robert Cray, Hi Rhythm*.

    Demanding a lone contributor is not extra exactness, it is a different
    question: a record billed to a headliner and their band is still one
    record, by the act named first.
    """
    provider, _ = make_provider(
        {
            f"/album/upc:{BARCODE}": deezer_album(
                contributors=[
                    {"id": "1424", "name": "Joe Bonamassa", "role": "Main"},
                    {"id": "9", "name": "Beth Hart", "role": "Main"},
                ]
            )
        }
    )

    assert run(provider, album_job()).artist_fields["deezer_artist_id"] == "1424"


def test_a_credit_naming_someone_else_is_ignored() -> None:
    """A guest appearance must not rewrite the followed artist's identity."""
    provider, _ = make_provider(
        {
            f"/album/upc:{BARCODE}": deezer_album(
                contributors=[{"id": "77", "name": "Eric Clapton", "role": "Main"}]
            )
        }
    )

    assert run(provider, album_job()).artist_fields == {}


def test_an_artist_row_without_a_known_id_is_no_key_rather_than_a_search() -> None:
    """There is no artist search here at all, and that is the point."""
    provider, hits = make_provider({})

    with pytest.raises(NoMatchKey):
        run(provider, artist_job())

    assert hits == []


def test_a_known_artist_id_fetches_the_portrait() -> None:
    provider, _ = make_provider(
        {
            "/artist/1424": {
                "id": "1424",
                "name": "Joe Bonamassa",
                "picture_xl": "https://cdn-images.dzcdn.net/images/artist/x/1000x1000.jpg",
            }
        }
    )

    result = run(provider, artist_job(deezer_artist_id="1424"))

    assert result.artist_fields["deezer_image_url"].endswith("1000x1000.jpg")


# ---------------------------------------------------------------------------
# Tracks
# ---------------------------------------------------------------------------
def test_track_ids_map_by_position_when_the_layouts_agree() -> None:
    tracks = [{"id": "101", "title": "This Train"}, {"id": "102", "title": "Mountain Climbing"}]
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album(tracks=tracks)})

    result = run(
        provider,
        album_job(
            tracks=(
                TrackSnapshot(id="t1", title="This Train", track_number=1, media_number=1),
                TrackSnapshot(id="t2", title="Mountain Climbing", track_number=2, media_number=1),
            )
        ),
    )

    assert result.track_fields == {
        "t1": {"deezer_track_id": "101"},
        "t2": {"deezer_track_id": "102"},
    }


def test_a_mismatched_tracklist_maps_nothing_at_all() -> None:
    """Half a positional mapping looks authoritative and is wrong from the first
    bonus track onwards."""
    tracks = [{"id": "101", "title": "This Train"}]
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album(tracks=tracks)})

    result = run(
        provider,
        album_job(
            tracks=(
                TrackSnapshot(id="t1", title="This Train", track_number=1, media_number=1),
                TrackSnapshot(id="t2", title="Mountain Climbing", track_number=2, media_number=1),
            )
        ),
    )

    assert result.track_fields == {}


def test_a_tracklist_whose_titles_disagree_maps_nothing() -> None:
    tracks = [{"id": "101", "title": "Something Else"}, {"id": "102", "title": "Mountain Climbing"}]
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album(tracks=tracks)})

    result = run(
        provider,
        album_job(
            tracks=(
                TrackSnapshot(id="t1", title="This Train", track_number=1, media_number=1),
                TrackSnapshot(id="t2", title="Mountain Climbing", track_number=2, media_number=1),
            )
        ),
    )

    assert result.track_fields == {}


# ---------------------------------------------------------------------------
# The write-back
# ---------------------------------------------------------------------------
@pytest.fixture(name="factory")
def factory_fixture() -> Iterator[Any]:
    engine = create_async_engine(
        "sqlite+aiosqlite://", connect_args={"check_same_thread": False}, poolclass=StaticPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id="312829", name="Joe Bonamassa"))
            session.add(
                Album(
                    id=BARCODE,
                    artist_id="312829",
                    title="Blues Of Desperation",
                    # Declared, because the id beside it is not a barcode and is
                    # no longer read as one — without this there is no key and
                    # the rung would never get far enough to cast a vote.
                    upc=BARCODE,
                    release_type="single",  # Qobuz's guess: too few tracks
                    tracks_count=3,
                    label="Qobuz Label",
                    genre="Blues",
                    # On disk: enrichment is scoped to the library, so a wanted
                    # album is never claimed and there would be no vote to count.
                    status=AlbumStatus.DOWNLOADED,
                )
            )
            await session.commit()

    asyncio.run(setup())

    @asynccontextmanager
    async def session_factory() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    try:
        yield session_factory
    finally:
        asyncio.run(engine.dispose())


class SecondOpinion:
    """A stand-in for the MusicBrainz rung, casting one vote and nothing else.

    Needed because **Qobuz is a source too**. With only Deezer enabled a
    disagreement is 1–1, which is a tie, which changes nothing — so testing the
    write-back at all requires two sources that agree with each other.
    """

    source = EnrichmentSource.MUSICBRAINZ

    def __init__(self, release_type: str | None) -> None:
        self.release_type = release_type

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        return entity_type is EnrichmentEntity.ALBUM

    def is_ready(self) -> bool:
        return True

    async def fetch(self, job: EnrichmentJob) -> Any:
        from app.enrich.types import EnrichmentResult

        return EnrichmentResult(opinions={"release_type": self.release_type})

    async def aclose(self) -> None:
        return None


def enrich(factory: Any, *providers: Any, **settings: Any) -> None:
    settings.setdefault(
        "enrichment_sources",
        ",".join(p.source.value for p in providers),
    )
    enricher = Enricher(
        list(providers), settings=make_settings(**settings), session_factory=factory
    )

    async def go() -> None:
        try:
            await enricher.tick()
        finally:
            await enricher.aclose()

    asyncio.run(go())


def read(factory: Any) -> tuple[Album, AlbumMetadata | None]:
    async def go() -> tuple[Album, AlbumMetadata | None]:
        async with factory() as session:
            return (
                await session.get(Album, BARCODE),
                await session.get(AlbumMetadata, BARCODE),
            )

    return asyncio.run(go())


def test_one_external_source_cannot_outvote_qobuz(factory: Any) -> None:
    """Qobuz is a source. One against one is a tie, and a tie changes nothing.

    This is the whole safety property in one test: with a single rung enabled, no
    amount of confidence on its part rewrites the library.
    """
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    enrich(factory, provider)

    album, meta = read(factory)
    assert album.release_type == "single", "untouched"
    assert meta.release_type_applied_at is None
    votes = decode_consensus(meta.consensus_json)["release_type"]["votes"]
    assert votes == {"qobuz": "single", "deezer": "album"}


def test_two_agreeing_sources_correct_the_release_type(factory: Any) -> None:
    """Qobuz called an eleven-track album a single because it counted three."""
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    enrich(factory, provider, SecondOpinion("album"))

    album, meta = read(factory)
    assert album.release_type == "album"
    assert meta.qobuz_release_type == "single", "the original is kept, so this reverses"
    assert meta.release_type_applied_at is not None


def test_the_write_back_never_touches_the_status(factory: Any) -> None:
    """Reclassifying can neither create a download nor cancel one."""
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    enrich(factory, provider, SecondOpinion("album"))

    album, _ = read(factory)
    assert album.release_type == "album", "the reclassification really happened"
    assert album.status is AlbumStatus.DOWNLOADED


def test_the_write_back_never_touches_label_or_genre(factory: Any) -> None:
    """Both are naming-template tokens; changing one moves folders on the next re-file."""
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    enrich(factory, provider, SecondOpinion("album"))

    album, meta = read(factory)
    assert album.release_type == "album", "the reclassification really happened"
    assert album.label == "Qobuz Label"
    assert album.genre == "Blues"
    # The richer values are still available — beside the Qobuz ones, not over them.
    assert meta.label == "J&R Adventures"
    assert meta.genre_list == ["Rock", "Blues"]


def test_turning_the_write_back_off_records_the_verdict_without_applying_it(
    factory: Any,
) -> None:
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    enrich(
        factory, provider, SecondOpinion("album"), enrichment_apply_release_type=False
    )

    album, meta = read(factory)
    assert album.release_type == "single", "untouched"
    assert meta.suggested_release_type == "album", "but the disagreement is visible"
    assert meta.release_type_applied_at is None


def test_qobuz_keeps_voting_with_its_original_answer(factory: Any) -> None:
    """Otherwise one early majority would look unanimous on the next pass."""
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})
    enrich(factory, provider, SecondOpinion("album"))

    _, meta = read(factory)
    votes = decode_consensus(meta.consensus_json)["release_type"]["votes"]
    assert votes["qobuz"] == "single", "not the applied value"
    assert votes["deezer"] == "album"
    assert votes["musicbrainz"] == "album"


def test_three_way_disagreement_changes_nothing(factory: Any) -> None:
    provider, _ = make_provider({f"/album/upc:{BARCODE}": deezer_album()})

    enrich(factory, provider, SecondOpinion("ep"))

    album, meta = read(factory)
    assert album.release_type == "single"
    assert meta.suggested_release_type is None
    assert meta.release_type_applied_at is None


def test_sources_agreeing_with_qobuz_change_nothing(factory: Any) -> None:
    provider, _ = make_provider(
        {f"/album/upc:{BARCODE}": deezer_album(record_type="single")}
    )

    enrich(factory, provider, SecondOpinion("single"))

    album, meta = read(factory)
    assert album.release_type == "single"
    assert meta.suggested_release_type == "single"
    assert meta.release_type_applied_at is None, "nothing was applied, so nothing to revert"


# ---------------------------------------------------------------------------
# The picker: a search, offered to a person and to nothing else
# ---------------------------------------------------------------------------
def pick(provider: DeezerProvider, job: EnrichmentJob, query: str | None = None) -> Any:
    async def go() -> Any:
        try:
            return await provider.candidates(job, query)
        finally:
            await provider.aclose()

    return asyncio.run(go())


def test_the_album_search_quotes_both_halves() -> None:
    """Deezer's advanced syntax, and the quoting is the whole point of it.

    Unquoted, ``album:Live At The Fillmore`` is parsed as the one word ``Live``
    and everything after it becomes free text — which returns whatever is
    popular rather than the release being identified.
    """
    from app.enrich.deezer import _album_query

    query = _album_query(album_snapshot(title="Live At The Fillmore"))

    assert query == 'artist:"Joe Bonamassa" album:"Live At The Fillmore"'
    # A stray quote in a title would close the operator early and change what the
    # rest of the string means, so it is spent rather than escaped.
    assert '"' not in _album_query(
        album_snapshot(title='He Said "No"', artist_name="")
    ).replace("He Said", "")


def test_an_album_candidate_is_recognisable_without_the_id() -> None:
    provider, hits = make_provider(
        {
            "/search/album": {
                "data": [
                    {
                        "id": 302127,
                        "title": "Blues of Desperation",
                        "cover_medium": "https://cdn-images.dzcdn.net/x/250.jpg",
                        "nb_tracks": 11,
                        "record_type": "album",
                        "explicit_lyrics": False,
                        "link": "https://www.deezer.com/album/302127",
                        "artist": {"id": 4781, "name": "Joe Bonamassa"},
                    }
                ]
            }
        }
    )

    card = pick(provider, album_job())[0]

    assert hits == ["/search/album"]
    assert card.external_id == "302127"
    assert card.title == "Blues of Desperation"
    assert card.subtitle == "Joe Bonamassa"
    assert card.detail == "11 tracks · album"
    assert card.image_url.endswith("250.jpg")
    assert card.url == "https://www.deezer.com/album/302127"


def test_an_artist_candidate_shows_the_follower_count() -> None:
    """Not trivia: it is usually the only thing separating a well-known act from
    the covers band that took its name."""
    provider, _ = make_provider(
        {
            "/search/artist": {
                "data": [
                    {
                        "id": 4781,
                        "name": "Joe Bonamassa",
                        "nb_album": 50,
                        "nb_fan": 555_401,
                        "picture_big": "https://cdn-images.dzcdn.net/x/500.jpg",
                        "link": "https://www.deezer.com/artist/4781",
                    },
                    {"id": 99, "name": "Joe Bonamassa Tribute", "nb_album": 1, "nb_fan": 12},
                ]
            }
        }
    )

    first, second = pick(provider, artist_job())

    assert first.detail == "50 releases · 555,401 fans"
    assert second.detail == "1 release · 12 fans"


def test_a_search_that_matches_nothing_is_an_empty_list() -> None:
    """Deezer answers a miss with HTTP 200 and an error object. A picker showing
    "no results" is right; an exception reaching the page is not."""
    provider, _ = make_provider({})

    assert pick(provider, artist_job()) == []


def test_the_automatic_path_never_searches() -> None:
    """The rule the whole rung is built on. An album with no barcode and no
    identified artist raises rather than falling back to a name search — a wrong
    id here is written into every file on disk."""
    provider, hits = make_provider(
        {"/search/album": {"data": [{"id": 1, "title": "Anything"}]}}
    )

    with pytest.raises(NoMatchKey):
        run(provider, album_job(id="uyej1o165e870", upc=None))
    assert hits == []
