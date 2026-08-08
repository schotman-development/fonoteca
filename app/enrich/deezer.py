"""The Deezer rung — the widest net, and where a barcode comes from when nothing else has one.

Deezer is key-free, fast (roughly fifty requests per five seconds), and has close
to complete catalogue coverage. Its particular value is that **it supplies the
barcode**: MusicBrainz can only be matched exactly on a barcode or an ISRC, and
Qobuz hands over neither for most releases — in one real library, 6 albums out of
3313 carried a ``upc``. Deezer answers on a barcode *or* on a browse of an
artist's discography, and its album payload contains the UPC.

That used to put it ahead of MusicBrainz on the ladder, and no longer does. Once
AcoustID runs first it writes ``mb_release_mbid`` onto the album, and MusicBrainz
matching on its own id needs nothing from here — measured on a 422-release
library, MusicBrainz resolved 253 releases off the audio's answer while this rung,
running ahead of it, recorded "no barcode on this release and no Deezer artist to
browse" on 197 of the very same albums. So this now runs *after* MusicBrainz,
where the barcode it wants has usually already been verified.

For a release AcoustID could **not** pin, the old order is still the better one,
and no single sequence serves both populations. What resolves that is not the
order but :meth:`app.core.enricher.Enricher._rearm_barcoded`, which re-opens a
rung that refused for want of a barcode as soon as any later rung learns one —
so this rung's ``no_key`` is a "not yet" rather than a verdict.

Two ways in, both exact:

* **By barcode.** ``GET /album/upc:{barcode}``, accepted only when the response's
  own ``upc`` field echoes the one asked for.
* **By artist browse.** ``GET /artist/{id}/albums``, matched locally on
  normalised title plus year. The artist is already pinned, so this is a
  comparison within one discography rather than a search across a catalogue —
  which is what makes it acceptable under the exact-or-nothing rule.

The barcode that opens the first path has to be a real one: a Qobuz ``upc``, or
a ``BARCODE`` tag read off the files. It used to be allowed to be the Qobuz
album id, which is how most releases got in — and how they got in *wrong*; the
arithmetic is in :func:`~app.enrich.matching.barcode_candidates`. Without a
genuine barcode anywhere in an artist's catalogue neither path opens, the artist
id is never derived, and their releases go to the review list. That is the
intended shape of the failure: the ladder now waits for evidence instead of
manufacturing a key.

The artist id itself is never searched for. It is derived from the first ``Main``
contributor of an album that matched by barcode, and only when that contributor's
name survives the rejection test in :func:`~app.enrich.matching.main_credit`.

One protocol quirk drives the whole client: **Deezer reports errors with HTTP
200** and an error object in the body — for a miss and for a quota breach alike.
Reading status codes alone would record both as successes, so
:meth:`DeezerClient.check_payload` inspects every body.
"""

from __future__ import annotations

from typing import Any, Iterable, Mapping, Sequence

from app.config import Settings, get_settings
from app.enrich.errors import (
    AmbiguousMatch,
    NoMatchKey,
    NotMatched,
    SourceGated,
)
from app.enrich.matching import (
    barcode_candidates,
    barcodes_match,
    main_credit,
    normalize_barcode,
    sole_match,
    titles_match,
    years_close,
)
from app.enrich.types import (
    AlbumSnapshot,
    ArtistSnapshot,
    Candidate,
    EnrichmentJob,
    EnrichmentResult,
)
from app.logging_conf import get_logger
from app.models import EnrichmentEntity, EnrichmentSource, join_tags
from app.net.errors import HttpError, HttpNotFound, HttpRateLimitError
from app.net.http import JsonHttpClient
from app.net.ratelimit import RateLimiter

__all__ = ["DeezerClient", "DeezerProvider"]

logger = get_logger(__name__)

#: Pages of an artist's discography to walk before giving up. 100 per page, so
#: this covers 500 releases — beyond the most prolific artist in a real library.
_MAX_ALBUM_PAGES = 5
_PAGE_SIZE = 100


class DeezerClient(JsonHttpClient):
    """Read-only access to Deezer's public API. No key, no account, no auth."""

    source = "deezer"
    base_url = "https://api.deezer.com/"
    timeout = 20.0

    def check_payload(self, path: str, body: Any, *, status: int) -> Any:
        """Catch the errors Deezer hides inside a 200 response.

        ``{"error": {"type": "DataException"}}`` is a miss and must not touch the
        circuit breaker — the round trip was perfectly healthy, the answer was
        just "no". ``Quota limit exceeded`` is the opposite: it means slow down,
        and it has to reach the limiter or the next request repeats the mistake.
        """
        if not isinstance(body, Mapping):
            return body
        error = body.get("error")
        if not error:
            return body
        if not isinstance(error, Mapping):
            raise HttpError(str(error), source=self.source, endpoint=path)

        kind = str(error.get("type") or "")
        message = str(error.get("message") or kind or "Deezer returned an error")
        if "quota" in message.lower() or kind == "QuotaException":
            raise HttpRateLimitError(message, source=self.source, endpoint=path)
        raise HttpNotFound(message, source=self.source, endpoint=path)

    # ------------------------------------------------------------- endpoints
    async def album_by_barcode(self, barcode: str) -> dict[str, Any] | None:
        """The album carrying *barcode*, or ``None``.

        The echoed ``upc`` is verified rather than trusted. Deezer resolves this
        path server-side and a mismatch would mean it answered about a different
        record — cheap to check, and the whole match hangs off it.
        """
        try:
            payload = await self.get_json(f"album/upc:{barcode}")
        except HttpNotFound:
            return None
        if not isinstance(payload, Mapping):
            return None
        if not barcodes_match(payload.get("upc"), barcode):
            logger.debug(
                "Deezer answered barcode %s with upc %s; ignoring",
                barcode,
                payload.get("upc"),
            )
            return None
        return dict(payload)

    async def album(self, album_id: str) -> dict[str, Any] | None:
        """One album by Deezer id, with contributors, genres and tracklist."""
        try:
            payload = await self.get_json(f"album/{album_id}")
        except HttpNotFound:
            return None
        return dict(payload) if isinstance(payload, Mapping) else None

    async def artist(self, artist_id: str) -> dict[str, Any] | None:
        """One artist by Deezer id."""
        try:
            payload = await self.get_json(f"artist/{artist_id}")
        except HttpNotFound:
            return None
        return dict(payload) if isinstance(payload, Mapping) else None

    async def artist_albums(self, artist_id: str) -> list[dict[str, Any]]:
        """Every release Deezer lists for an artist, paginated.

        Bounded by :data:`_MAX_ALBUM_PAGES`, and the cut is logged rather than
        silent — a truncated discography that looked complete would make the
        browse path quietly stop matching an artist's older records.
        """
        collected: list[dict[str, Any]] = []
        for page in range(_MAX_ALBUM_PAGES):
            try:
                payload = await self.get_json(
                    f"artist/{artist_id}/albums",
                    {"limit": _PAGE_SIZE, "index": page * _PAGE_SIZE},
                )
            except HttpNotFound:
                break
            items = payload.get("data") if isinstance(payload, Mapping) else None
            if not items:
                break
            collected.extend(item for item in items if isinstance(item, Mapping))
            if len(items) < _PAGE_SIZE or not payload.get("next"):
                break
        else:
            logger.info(
                "Stopped after %d pages of Deezer albums for artist %s; "
                "older releases may not be matchable",
                _MAX_ALBUM_PAGES,
                artist_id,
            )
        return collected

    # ------------------------------------------------- search (people only)
    # Everything above this line is exact. Everything below it is a name search,
    # which the automatic path must never reach: it is offered to a person on the
    # review screen, who reads the results and picks one. See
    # ``app.enrich.types.Candidate``.
    async def search_artists(self, name: str, *, limit: int = 8) -> list[Mapping[str, Any]]:
        """Artists whose name resembles *name*, in Deezer's relevance order."""
        return await self._search("search/artist", name, limit)

    async def search_albums(self, query: str, *, limit: int = 8) -> list[Mapping[str, Any]]:
        """Releases matching *query*, in Deezer's relevance order.

        The caller builds *query* — usually ``artist:"X" album:"Y"``, which is
        Deezer's own advanced syntax and narrows a common album title to one
        artist's version of it.
        """
        return await self._search("search/album", query, limit)

    async def _search(
        self, path: str, query: str, limit: int
    ) -> list[Mapping[str, Any]]:
        text = str(query or "").strip()
        if not text:
            return []
        try:
            payload = await self.get_json(path, {"q": text, "limit": limit})
        except HttpNotFound:
            # Deezer answers an unmatched search with an empty data array rather
            # than a 404, so this is the genuinely-broken case; an empty list is
            # still the right answer for a picker.
            return []
        items = payload.get("data") if isinstance(payload, Mapping) else None
        if not isinstance(items, Sequence):
            return []
        return [item for item in items if isinstance(item, Mapping)][:limit]


class DeezerProvider:
    """Turns Deezer payloads into metadata rows and votes."""

    source = EnrichmentSource.DEEZER

    def __init__(
        self,
        *,
        limiter: RateLimiter,
        settings: Settings | None = None,
        client: DeezerClient | None = None,
    ) -> None:
        self._settings = settings or get_settings()
        self.client = client or DeezerClient(
            limiter=limiter,
            settings=self._settings,
            user_agent=self._settings.enrichment_user_agent,
        )

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        return entity_type in (EnrichmentEntity.ARTIST, EnrichmentEntity.ALBUM)

    def is_ready(self) -> bool:
        """Always ready: Deezer needs no key and no configuration."""
        return True

    async def aclose(self) -> None:
        await self.client.aclose()

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        if job.entity_type is EnrichmentEntity.ARTIST:
            return await self._fetch_artist(job.artist)
        return await self._fetch_album(job.album)

    # ------------------------------------------------- candidates (a person)
    async def candidates(
        self, job: EnrichmentJob, query: str | None = None
    ) -> list[Candidate]:
        """Records a person could pick from. Never reached by :meth:`fetch`."""
        text = str(query or "").strip()
        if job.entity_type is EnrichmentEntity.ARTIST:
            return [
                _artist_candidate(hit)
                for hit in await self.client.search_artists(text or job.artist.name)
            ]

        album = job.album
        # Deezer's advanced syntax, which is what stops "Greatest Hits" from
        # returning three hundred unrelated compilations. A typed query is taken
        # literally — the person is searching, not refining our guess.
        search = text or _album_query(album)
        return [_album_candidate(hit) for hit in await self.client.search_albums(search)]

    # ---------------------------------------------------------------- artist
    async def _fetch_artist(self, artist: ArtistSnapshot) -> EnrichmentResult:
        """Fill in an artist whose Deezer id another album already established.

        There is no search here on purpose. The id arrives from
        :meth:`_fetch_album` deriving it from a barcode-matched release's sole
        ``Main`` contributor — an artist already pinned by a product code. A name
        search would be a guess, and a wrong artist id poisons every album, the
        portrait and the browse path all at once.
        """
        if not artist.deezer_artist_id:
            raise NoMatchKey(
                "no Deezer artist id yet — it is derived from a matched release",
                source=self.source.value,
            )

        payload = await self.client.artist(artist.deezer_artist_id)
        if payload is None:
            raise NotMatched(
                f"Deezer artist {artist.deezer_artist_id} is gone",
                source=self.source.value,
            )

        return EnrichmentResult(
            match_key=str(artist.deezer_artist_id),
            artist_fields={
                "deezer_artist_id": str(payload.get("id") or artist.deezer_artist_id),
                "deezer_image_url": _picture(payload),
                "name": _text(payload.get("name")) or None,
            },
        )

    # ----------------------------------------------------------------- album
    async def _fetch_album(self, album: AlbumSnapshot) -> EnrichmentResult:
        """Match one release, by barcode first and by artist browse second."""
        payload, method, key = await self._resolve_album(album)
        return self._build_album_result(album, payload, method=method, match_key=key)

    async def _resolve_album(
        self, album: AlbumSnapshot
    ) -> tuple[dict[str, Any], str, str]:
        """Find this release on Deezer, or raise the outcome that says why not."""
        if album.deezer_album_id:
            # Already identified, automatically or by a person on the review
            # list. Re-deriving it could land somewhere else and overwrite what
            # they typed in; an id in hand is the strongest key available.
            payload = await self.client.album(str(album.deezer_album_id))
            if payload is not None:
                return payload, "album-id", str(album.deezer_album_id)
            logger.info(
                "Deezer album %s is gone; falling back to matching",
                album.deezer_album_id,
            )

        # Qobuz's declared ``upc``, then anything an earlier pass established,
        # then what the files themselves claim — descending trust, and the last
        # is only ever a place to look: ``album_by_barcode`` returns the release
        # carrying that exact barcode or nothing at all. Never the Qobuz album
        # id, however much it looks like one (:func:`barcode_candidates` records
        # what that fallback cost).
        candidates = barcode_candidates(album.upc, album.barcode, album.claimed_barcode)
        for barcode in candidates:
            payload = await self.client.album_by_barcode(barcode)
            if payload is not None:
                return payload, "barcode", barcode

        if album.deezer_artist_id:
            return await self._resolve_by_browse(album)

        if candidates:
            raise NotMatched(
                f"no Deezer release carries barcode {candidates[0]}",
                source=self.source.value,
            )
        raise NoMatchKey(
            "no barcode on this release and no Deezer artist to browse",
            source=self.source.value,
        )

    async def _resolve_by_browse(
        self, album: AlbumSnapshot
    ) -> tuple[dict[str, Any], str, str]:
        """Match within one artist's discography, on title and year.

        Safe in a way a catalogue-wide title search is not: the artist is already
        established, so the only question is which of *their* records this is.
        Two survivors still means ambiguous — an artist really can release two
        records a year apart with the same title, and picking one is a guess.
        """
        listing = await self.client.artist_albums(str(album.deezer_artist_id))
        if not listing:
            raise NotMatched(
                "Deezer lists no releases for this artist", source=self.source.value
            )

        def matches(candidate: Mapping[str, Any]) -> bool:
            return titles_match(candidate.get("title"), album.title) and years_close(
                candidate.get("release_date"), album.release_date
            )

        winner, survivors = sole_match(listing, matches)
        if winner is None:
            if survivors:
                raise AmbiguousMatch(
                    f"{len(survivors)} Deezer releases match '{album.title}'",
                    source=self.source.value,
                    candidates=[
                        {
                            "id": str(item.get("id")),
                            "title": _text(item.get("title")),
                            "release_date": _text(item.get("release_date")),
                            "record_type": _text(item.get("record_type")),
                        }
                        for item in survivors
                    ],
                )
            raise NotMatched(
                f"'{album.title}' is not in this artist's Deezer discography",
                source=self.source.value,
            )

        # The browse listing is a summary — no UPC, no contributors — so fetch
        # the full object. That UPC is what unlocks MusicBrainz for a release
        # Qobuz gave no barcode for, which is the entire point of this path.
        detail = await self.client.album(str(winner.get("id")))
        if detail is None:
            raise NotMatched(
                f"Deezer album {winner.get('id')} vanished between listing and lookup",
                source=self.source.value,
            )
        return detail, "artist-browse", str(winner.get("id"))

    def _build_album_result(
        self,
        album: AlbumSnapshot,
        payload: Mapping[str, Any],
        *,
        method: str,
        match_key: str,
    ) -> EnrichmentResult:
        """Map a Deezer album payload onto metadata rows, votes and follow-ups."""
        barcode = normalize_barcode(payload.get("upc"))
        deezer_album_id = str(payload.get("id") or "")

        album_fields: dict[str, Any] = {
            "deezer_album_id": deezer_album_id or None,
            "deezer_match_method": method,
            "deezer_record_type": _text(payload.get("record_type")) or None,
            "deezer_cover_url": _text(payload.get("cover_xl")) or _text(payload.get("cover_big")) or None,
            "deezer_release_date": _text(payload.get("release_date")) or None,
            "title": _text(payload.get("title")) or None,
            "label": _text(payload.get("label")) or None,
            "genres": join_tags(_genres(payload)) or None,
        }
        if barcode:
            album_fields["barcode"] = barcode
            # Where the key came from matters: a barcode Deezer supplied is the
            # only reason a release with no Qobuz ``upc`` becomes matchable at
            # all, and knowing that lets a bad rule be re-run later.
            #
            # Compared with ``barcodes_match``, not ``==``: Deezer returns the
            # 12-digit UPC-A where Qobuz declares the 13-digit EAN, and a string
            # comparison would credit every one of those to Deezer.
            #
            # There is deliberately no ``album-id`` answer any more. Qobuz's id
            # is not a barcode, so a barcode can no longer have come from it, and
            # recording that provenance would be a claim about a lookup that
            # never happened.
            album_fields["barcode_source"] = (
                "upc" if barcodes_match(album.upc, barcode) else "deezer"
            )
        if payload.get("nb_tracks"):
            album_fields["track_count"] = _int(payload.get("nb_tracks"))

        result = EnrichmentResult(
            match_key=match_key,
            album_fields=album_fields,
            opinions={"release_type": _release_type(payload)},
        )

        derived = self._derive_artist(album, payload)
        if derived:
            result.artist_fields = {
                "deezer_artist_id": derived,
                "deezer_match_method": "contributor",
                "deezer_image_url": _picture(_main_contributor(payload) or {}),
            }
            if not album.deezer_artist_id:
                # A newly known artist id makes the artist's own row worth
                # running, and makes every other album by them browsable.
                result.reopen.append(
                    (EnrichmentEntity.ARTIST, album.artist_id, EnrichmentSource.DEEZER)
                )
                result.note = (
                    f"Deezer identified {album.artist_name} from '{album.title}'"
                )

        result.track_fields = _track_fields(album, payload)
        return result

    def _derive_artist(
        self, album: AlbumSnapshot, payload: Mapping[str, Any]
    ) -> str | None:
        """The Deezer artist id this release proves, or ``None``.

        Only accepted from a **barcode** match. A browse match already assumed
        the artist id, so letting it confirm that same id would be circular — it
        would turn one lucky guess into permanent evidence.

        Deezer lists contributors in billing order, so the *first* ``Main`` one
        is who the release is by; :func:`~app.enrich.matching.main_credit` then
        refuses it unless the name is the artist being enriched. Demanding a
        lone contributor instead would discard *Robert Cray, Hi Rhythm* and
        every other record billed to a headliner and their band.
        """
        contributors = payload.get("contributors")
        if not isinstance(contributors, Sequence) or isinstance(contributors, (str, bytes)):
            return None
        mains = [
            item
            for item in contributors
            if isinstance(item, Mapping) and str(item.get("role") or "Main") == "Main"
        ]
        return main_credit(mains, expected_name=album.artist_name, role_key="role")


# ---------------------------------------------------------------------------
# Payload helpers — pure, tolerant of a field being absent or the wrong shape
# ---------------------------------------------------------------------------
def _text(value: Any) -> str:
    return str(value).strip() if value not in (None, "") else ""


def _int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _genres(payload: Mapping[str, Any]) -> list[str]:
    """Genre names from Deezer's nested ``{"genres": {"data": [...]}}``."""
    bucket = payload.get("genres")
    items = bucket.get("data") if isinstance(bucket, Mapping) else bucket
    if not isinstance(items, Iterable) or isinstance(items, (str, bytes)):
        return []
    return [
        name
        for item in items
        if isinstance(item, Mapping) and (name := _text(item.get("name")))
    ]


def _picture(payload: Mapping[str, Any]) -> str | None:
    """The largest artist portrait Deezer offers (1000x1000)."""
    for key in ("picture_xl", "picture_big", "picture_medium", "picture"):
        url = _text(payload.get(key))
        if url:
            return url
    return None


# ---------------------------------------------------------------------------
# Candidate cards — display only, and only for a person
# ---------------------------------------------------------------------------
def _album_query(album: AlbumSnapshot) -> str:
    """Deezer's advanced search syntax for the release we are trying to name.

    Quoting both halves matters: unquoted, ``album:Live At The Fillmore`` is
    parsed as the one word ``Live`` and the rest becomes a free-text soup that
    returns whatever is popular.
    """
    title = _text(album.title).replace('"', " ").strip()
    artist = _text(album.artist_name or "").replace('"', " ").strip()
    if artist and title:
        return f'artist:"{artist}" album:"{title}"'
    return title or artist


def _album_candidate(hit: Mapping[str, Any]) -> Candidate:
    artist = hit.get("artist") if isinstance(hit.get("artist"), Mapping) else {}
    tracks = _int(hit.get("nb_tracks"))
    record_type = _text(hit.get("record_type"))
    bits = [
        f"{tracks} track{'' if tracks == 1 else 's'}" if tracks else "",
        record_type,
        "explicit" if hit.get("explicit_lyrics") else "",
    ]
    return Candidate(
        external_id=str(hit.get("id") or ""),
        title=_text(hit.get("title")) or "Untitled",
        subtitle=_text(artist.get("name")) or None,
        detail=" · ".join(bit for bit in bits if bit) or None,
        image_url=_cover(hit),
        url=_text(hit.get("link")) or None,
    )


def _artist_candidate(hit: Mapping[str, Any]) -> Candidate:
    albums = _int(hit.get("nb_album"))
    fans = _int(hit.get("nb_fan"))
    bits = [
        f"{albums} release{'' if albums == 1 else 's'}" if albums else "",
        # Follower count is not trivia here: it is usually the only thing
        # separating a well-known act from the covers band that took its name.
        f"{fans:,} fans" if fans else "",
    ]
    return Candidate(
        external_id=str(hit.get("id") or ""),
        title=_text(hit.get("name")) or "Unknown",
        detail=" · ".join(bit for bit in bits if bit) or None,
        image_url=_picture(hit),
        url=_text(hit.get("link")) or None,
    )


def _cover(payload: Mapping[str, Any]) -> str | None:
    """Album artwork, medium first — these are thumbnails on a card, not the
    artwork that gets stored."""
    for key in ("cover_medium", "cover_big", "cover_xl", "cover"):
        url = _text(payload.get(key))
        if url:
            return url
    return None


def _main_contributor(payload: Mapping[str, Any]) -> Mapping[str, Any] | None:
    contributors = payload.get("contributors")
    if isinstance(contributors, Sequence) and not isinstance(contributors, (str, bytes)):
        for item in contributors:
            if isinstance(item, Mapping) and str(item.get("role") or "Main") == "Main":
                return item
    artist = payload.get("artist")
    return artist if isinstance(artist, Mapping) else None


def _release_type(payload: Mapping[str, Any]) -> str | None:
    """Deezer's vote on what kind of release this is."""
    from app.enrich.merge import release_type_from_deezer  # noqa: PLC0415 - cycle

    return release_type_from_deezer(_text(payload.get("record_type")) or None)


def _track_fields(
    album: AlbumSnapshot, payload: Mapping[str, Any]
) -> dict[str, dict[str, Any]]:
    """Map Deezer track ids onto Qobuzarr's tracks, by position.

    Gated on the tracklists being the same length, and applied in full or not at
    all. A partial positional mapping is worse than none: it looks authoritative
    while being wrong from the first bonus track onwards.

    Note there are no ISRCs here — Deezer's embedded tracklist omits them, and
    fetching one per track would cost as many requests as the whole rest of the
    ladder for a field MusicBrainz supplies for free.
    """
    if not album.tracks:
        return {}
    bucket = payload.get("tracks")
    items = bucket.get("data") if isinstance(bucket, Mapping) else None
    if not isinstance(items, Sequence) or len(items) != len(album.tracks):
        return {}

    mapped: dict[str, dict[str, Any]] = {}
    for track, remote in zip(album.tracks, items):
        if not isinstance(remote, Mapping):
            return {}
        if not titles_match(remote.get("title"), track.title):
            return {}
        mapped[track.id] = {"deezer_track_id": str(remote.get("id") or "") or None}
    return mapped
