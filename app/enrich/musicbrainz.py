"""The MusicBrainz rung — the authority, once something else has supplied a key.

MusicBrainz is hand-curated, CC0, and the only source here that carries the
identifiers other tools actually look for: release and release-group MBIDs,
recording MBIDs, and the artist's **ISNI**, which identifies them in the wider
world rather than in one company's catalogue.

It runs **after AcoustID and before Deezer**, and the second half of that is a
reversal worth explaining, because the old order was defended on a premise that
no longer holds. The premise was that this rung can only be matched *exactly* and
exact matching needs a key it does not own — true when the only keys were Qobuz's
``upc`` (which it fills for a small minority of releases; and the album id is not
a substitute, reading it as a barcode is what used to produce every "no
MusicBrainz release carries barcode …" failure on this user's library) and a
barcode Deezer supplied. AcoustID changed it: the rung above now writes
``mb_release_mbid`` straight onto the album, which is a stronger key than a
barcode and one this source owns outright. Measured on a 422-release library,
253 releases resolved here off AcoustID's answer alone — 107 on a pinned release
and 146 on a release group — while Deezer, running ahead, refused 197 of those
*same* albums for want of a barcode this rung was about to verify.

Run this **first**, though, and most of a library is still unreachable: without
AcoustID above it there is nothing to join on. The ordering constraint was never
"after Deezer", it was "after something that produces a key".

What the swap gives up is real: for a release AcoustID could not pin, Deezer is
still the better first mover, since it answers on an artist browse and its
payloads carry barcodes this rung then matches. One sequence cannot serve both
populations, which is why the fix is not only the order —
:meth:`app.core.enricher.Enricher._rearm_barcoded` re-opens whichever rung ran
too early once any later one learns a barcode, so the ladder reaches the same
answer from either direction.

Three things about this API drive the code:

**It throttles with HTTP 503, not 429.** Hence the widened
``rate_limit_statuses`` — read as a server error it would trip the circuit
breaker instead of simply slowing down.

**It requires a contact in the User-Agent** and blocks generic browser strings.
Without ``ENRICHMENT_CONTACT`` this rung reports itself gated and does nothing;
it never falls back on ``QOBUZ_USER_AGENT``, which impersonates Chrome.

**Barcode "lookup" is really a Lucene search.** ``/ws/2/release/?query=barcode:…``
goes to a separate search index that returns *scored neighbours*, and it will
happily hand back a confident-looking release with a different barcode. So the
score is ignored entirely and the returned ``barcode`` field is compared
character-for-character. That single line is the difference between exact
matching and guessing.
"""

from __future__ import annotations

from typing import Any, Iterable, Mapping, Sequence

from app.config import Settings, get_settings
from app.enrich.errors import (
    AmbiguousMatch,
    MatchRejected,
    NoMatchKey,
    NotMatched,
    SourceGated,
)
from app.enrich.matching import (
    barcode_candidates,
    barcodes_match,
    main_credit,
    normalize_barcode,
    normalize_isni,
    normalize_mbid,
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
from app.net.errors import HttpNotFound
from app.net.http import JsonHttpClient
from app.net.ratelimit import RateLimiter

__all__ = ["MusicBrainzClient", "MusicBrainzProvider"]

logger = get_logger(__name__)

#: Everything one release lookup must return so that artist and track matching
#: cost no further requests.
_RELEASE_INC = (
    "artist-credits+labels+recordings+release-groups+media+isrcs+genres"
)
_ARTIST_INC = "url-rels+genres"

#: Release-groups per browse page, and how many pages to walk. 100 x 5 covers a
#: more prolific discography than any real library holds.
_PAGE_SIZE = 100
_MAX_PAGES = 5

#: ``url-rels`` relation types worth keeping, mapped onto our column names.
_URL_RELATIONS: dict[str, str] = {
    "official homepage": "official_homepage",
    "wikidata": "wikidata_qid",
    "wikipedia": "wikipedia_url",
}


class MusicBrainzClient(JsonHttpClient):
    """Read-only access to the MusicBrainz web service."""

    source = "musicbrainz"
    base_url = "https://musicbrainz.org/ws/2/"
    # MusicBrainz answers "you are going too fast" with 503, not 429. Read as a
    # server error that would trip the breaker rather than pace the caller.
    rate_limit_statuses = frozenset({429, 503})
    timeout = 30.0

    async def search_releases_by_barcode(
        self, barcodes: Sequence[str], *, limit: int = 25
    ) -> list[dict[str, Any]]:
        """Releases the search index associates with any of *barcodes*.

        Returns the raw hits. Filtering them is the caller's job and is
        deliberately not done here — see :meth:`MusicBrainzProvider._resolve_album`
        for why the ``score`` field is never consulted.
        """
        if not barcodes:
            return []
        query = " OR ".join(f"barcode:{value}" for value in barcodes)
        try:
            payload = await self.get_json(
                "release/", {"query": f"({query})", "fmt": "json", "limit": limit}
            )
        except HttpNotFound:
            return []
        releases = payload.get("releases") if isinstance(payload, Mapping) else None
        return [item for item in (releases or []) if isinstance(item, Mapping)]

    async def release(self, mbid: str) -> dict[str, Any] | None:
        """One release with everything: credits, labels, media, recordings, ISRCs."""
        try:
            payload = await self.get_json(
                f"release/{mbid}", {"fmt": "json", "inc": _RELEASE_INC}
            )
        except HttpNotFound:
            return None
        return dict(payload) if isinstance(payload, Mapping) else None

    async def artist(self, mbid: str) -> dict[str, Any] | None:
        """One artist with its url relations and genres."""
        try:
            payload = await self.get_json(
                f"artist/{mbid}", {"fmt": "json", "inc": _ARTIST_INC}
            )
        except HttpNotFound:
            return None
        return dict(payload) if isinstance(payload, Mapping) else None

    async def releases_in_group(self, mbid: str) -> list[dict[str, Any]]:
        """Every release in one release group, paginated.

        The reason this exists is that the pressing MusicBrainz matched is
        usually *not* the one Qobuz sells. Joanne Shaw Taylor's *White Sugar* is
        ``710347114727`` in ``album_metadata`` and ``0884385226442`` on Qobuz —
        two real barcodes, two real editions, one record. Matching Qobuz on the
        single barcode of the single release AcoustID happened to land on
        therefore misses most of the catalogue, and matching on *any* barcode in
        the group finds it while giving up no precision: a release group carries
        one artist credit, so an edition is still an exact answer about who made
        the record.

        Returns the raw release payloads; :func:`barcodes_in_group` is what turns
        them into the set the Qobuz matcher consumes.
        """
        collected: list[dict[str, Any]] = []
        for page in range(_MAX_PAGES):
            try:
                payload = await self.get_json(
                    "release",
                    {
                        "release-group": mbid,
                        "fmt": "json",
                        "limit": _PAGE_SIZE,
                        "offset": page * _PAGE_SIZE,
                    },
                )
            except HttpNotFound:
                break
            releases = (
                payload.get("releases") if isinstance(payload, Mapping) else None
            )
            if not releases:
                break
            collected.extend(item for item in releases if isinstance(item, Mapping))
            if len(releases) < _PAGE_SIZE:
                break
        else:
            logger.info(
                "Stopped after %d pages of MusicBrainz releases for group %s",
                _MAX_PAGES,
                mbid,
            )
        return collected

    async def release_groups_for_artist(self, mbid: str) -> list[dict[str, Any]]:
        """Every release group credited to an artist, paginated."""
        collected: list[dict[str, Any]] = []
        for page in range(_MAX_PAGES):
            try:
                payload = await self.get_json(
                    "release-group",
                    {
                        "artist": mbid,
                        "fmt": "json",
                        "limit": _PAGE_SIZE,
                        "offset": page * _PAGE_SIZE,
                    },
                )
            except HttpNotFound:
                break
            groups = (
                payload.get("release-groups") if isinstance(payload, Mapping) else None
            )
            if not groups:
                break
            collected.extend(item for item in groups if isinstance(item, Mapping))
            if len(groups) < _PAGE_SIZE:
                break
        else:
            logger.info(
                "Stopped after %d pages of MusicBrainz release groups for %s",
                _MAX_PAGES,
                mbid,
            )
        return collected

    # ------------------------------------------------- search (people only)
    # The name searches this module's docstring forbids in the automatic path.
    # They exist for one caller: the review screen, where a person reads the
    # results and picks one. Adding a call to either of these from anywhere in
    # ``MusicBrainzProvider.fetch`` would undo the whole matching doctrine.
    async def search_artists(
        self, name: str, *, limit: int = 8
    ) -> list[dict[str, Any]]:
        """Artists whose name resembles *name*. **Human-driven only.**"""
        return await self._search("artist", "artists", name, limit)

    async def search_releases(
        self, query: str, *, limit: int = 8
    ) -> list[dict[str, Any]]:
        """Releases matching a Lucene *query*. **Human-driven only.**"""
        return await self._search("release", "releases", query, limit)

    async def _search(
        self, path: str, key: str, query: str, limit: int
    ) -> list[dict[str, Any]]:
        text = str(query or "").strip()
        if not text:
            return []
        try:
            payload = await self.get_json(
                path, {"query": text, "fmt": "json", "limit": limit}
            )
        except HttpNotFound:
            return []
        hits = payload.get(key) if isinstance(payload, Mapping) else None
        return [item for item in (hits or []) if isinstance(item, Mapping)][:limit]


class MusicBrainzProvider:
    """Turns MusicBrainz payloads into identifiers, facts and votes."""

    source = EnrichmentSource.MUSICBRAINZ

    def __init__(
        self,
        *,
        limiter: RateLimiter,
        settings: Settings | None = None,
        client: MusicBrainzClient | None = None,
    ) -> None:
        self._settings = settings or get_settings()
        self.client = client or MusicBrainzClient(
            limiter=limiter,
            settings=self._settings,
            user_agent=self._settings.enrichment_user_agent,
        )

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        return entity_type in (EnrichmentEntity.ARTIST, EnrichmentEntity.ALBUM)

    def is_ready(self) -> bool:
        """False until a contact address is configured.

        MusicBrainz asks for one and enforces it. Inventing a plausible-looking
        address would put a stranger in someone else's request logs, so the rung
        simply reports itself gated and the rest of the ladder carries on.
        """
        return self._settings.musicbrainz_ready

    def gate_reason(self) -> str:
        """Names the setting, because there is exactly one and it is fixable."""
        return (
            "ENRICHMENT_CONTACT is unset; MusicBrainz requires a contact address "
            "in the User-Agent and rejects generic browser strings"
        )

    async def aclose(self) -> None:
        await self.client.aclose()

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        if not self.is_ready():
            raise SourceGated(
                "ENRICHMENT_CONTACT is not set; MusicBrainz requires a contact "
                "in the User-Agent",
                source=self.source.value,
            )
        if job.entity_type is EnrichmentEntity.ARTIST:
            return await self._fetch_artist(job.artist)
        return await self._fetch_album(job.album)

    # ------------------------------------------------- candidates (a person)
    async def candidates(
        self, job: EnrichmentJob, query: str | None = None
    ) -> list[Candidate]:
        """Records a person could pick from. Never reached by :meth:`fetch`.

        This is the search ``_fetch_artist`` refuses to make, and the difference
        is not the request — it is who reads the answer. An automatic caller
        would have to pick, and every rule for picking is a guess. A person
        looking at "British blues guitarist" beside "American country singer"
        is not guessing.
        """
        if not self.is_ready():
            raise SourceGated(
                "ENRICHMENT_CONTACT is not set; MusicBrainz requires a contact "
                "in the User-Agent",
                source=self.source.value,
            )

        text = str(query or "").strip()
        if job.entity_type is EnrichmentEntity.ARTIST:
            hits = await self.client.search_artists(text or job.artist.name)
            return [_artist_candidate(hit) for hit in hits]

        hits = await self.client.search_releases(text or _release_query(job.album))
        return [_release_candidate(hit) for hit in hits]

    # ---------------------------------------------------------------- artist
    async def _fetch_artist(self, artist: ArtistSnapshot) -> EnrichmentResult:
        """Fill in an artist whose MBID a matched release already established.

        ``artist?query=<name>`` is not called here and must never be added.
        MusicBrainz holds dozens of artists called *Nirvana*, *Eden* or *Bad
        Company*, and unlike a wrong row in a database a wrong artist MBID
        **leaves the application**: it is written into every downloaded file as
        ``MUSICBRAINZ_ARTISTID``, and Picard, beets and Roon then believe it.
        Unresolved artists go to the review list for a human instead.
        """
        if not artist.mb_artist_mbid:
            raise NoMatchKey(
                "no MusicBrainz artist id yet — it is derived from a matched release",
                source=self.source.value,
            )

        payload = await self.client.artist(artist.mb_artist_mbid)
        if payload is None:
            raise NotMatched(
                f"MusicBrainz artist {artist.mb_artist_mbid} is gone",
                source=self.source.value,
            )

        life_span = payload.get("life-span") or {}
        area = payload.get("area") or {}
        begin_area = payload.get("begin-area") or {}
        fields: dict[str, Any] = {
            "mb_artist_mbid": normalize_mbid(payload.get("id")) or artist.mb_artist_mbid,
            "name": _text(payload.get("name")) or None,
            "sort_name": _text(payload.get("sort-name")) or None,
            "disambiguation": _text(payload.get("disambiguation")) or None,
            "artist_type": _text(payload.get("type")) or None,
            "gender": _text(payload.get("gender")) or None,
            "country": _text(payload.get("country")) or None,
            "area": _text(area.get("name")) or None,
            "begin_area": _text(begin_area.get("name")) or None,
            "life_span_begin": _text(life_span.get("begin")) or None,
            "life_span_end": _text(life_span.get("end")) or None,
            "life_span_ended": bool(life_span.get("ended")),
            "genres": join_tags(_genres(payload)) or None,
            "isni": _first_isni(payload),
        }
        fields.update(_url_relations(payload))

        result = EnrichmentResult(match_key=artist.mb_artist_mbid, artist_fields=fields)
        if fields.get("wikidata_qid"):
            # Wikidata hangs entirely off this id, so it only becomes worth
            # running once we have one.
            result.reopen.append(
                (EnrichmentEntity.ARTIST, artist.id, EnrichmentSource.WIKIDATA)
            )
        return result

    # ----------------------------------------------------------------- album
    async def _fetch_album(self, album: AlbumSnapshot) -> EnrichmentResult:
        payload, method, key = await self._resolve_album(album)
        if method in ("barcode-rg", "rg-browse"):
            # Neither path knows *which pressing* this is — one because several
            # share the barcode, the other because there was no barcode at all.
            # Both know the release group, and release-group facts are common to
            # every pressing. Anything narrower would be invented, including the
            # release MBID itself: writing the group's id into
            # ``mb_release_mbid`` would be a lie that other tools then read.
            return self._release_group_only(album, payload, method, key)
        return self._build_album_result(album, payload, method=method, match_key=key)

    async def _resolve_album(
        self, album: AlbumSnapshot
    ) -> tuple[Mapping[str, Any], str, str]:
        """Find this release, or raise the outcome explaining why not."""
        if album.mb_release_mbid:
            # Already identified — by a previous pass, or by a person on the
            # review list. Deriving it again from a barcode would either repeat
            # the work or, worse, land on a different release and overwrite what
            # somebody typed in. An id we hold is the best key there is.
            detail = await self.client.release(album.mb_release_mbid)
            if detail is not None:
                return detail, "release-id", album.mb_release_mbid
            logger.info(
                "MusicBrainz release %s is gone; falling back to matching",
                album.mb_release_mbid,
            )

        # Barcodes only, from fields that hold barcodes, in descending trust:
        # Qobuz's own ``upc``, then what an earlier rung verified, then what the
        # files claim. The last is a hypothesis — it may point the search at a
        # release, and ``_resolve_by_barcode`` still compares the returned
        # ``barcode`` character-for-character before believing anything, so a
        # wrong tag costs one request rather than a wrong match. The Qobuz album
        # id is not on this list and must not be added back — see
        # :func:`barcode_candidates`.
        candidates = barcode_candidates(album.upc, album.barcode, album.claimed_barcode)
        if candidates:
            return await self._resolve_by_barcode(album, candidates)
        if album.mb_artist_mbid:
            return await self._resolve_by_release_group(album)
        raise NoMatchKey(
            "no barcode on this release and no MusicBrainz artist to browse",
            source=self.source.value,
        )

    async def _resolve_by_barcode(
        self, album: AlbumSnapshot, candidates: Sequence[str]
    ) -> tuple[Mapping[str, Any], str, str]:
        hits = await self.client.search_releases_by_barcode(candidates)

        # The one line that keeps this exact. `hits` is a *scored* list from a
        # Lucene index, and its top result is routinely a different release with
        # a high score. Only an identical barcode counts as evidence.
        matched = [
            hit
            for hit in hits
            if any(barcodes_match(hit.get("barcode"), value) for value in candidates)
        ]
        if not matched:
            raise NotMatched(
                f"no MusicBrainz release carries barcode {candidates[0]}",
                source=self.source.value,
            )

        if len(matched) > 1:
            groups = {
                (hit.get("release-group") or {}).get("id")
                for hit in matched
                if (hit.get("release-group") or {}).get("id")
            }
            if len(groups) == 1:
                # Different pressings of one record, sharing a barcode. Which
                # pressing this is cannot be known, but *which record* can — once
                # something other than the barcode agrees. A mistyped barcode
                # landing on two pressings of the wrong record is the ordinary
                # shape of that mistake, and this branch used to be the one place
                # a barcode was believed on its own: the single-hit path below
                # would have refused the identical typo. What is written from
                # here is the release **group** id, which goes into every file as
                # ``MUSICBRAINZ_RELEASEGROUPID``, into ``album.nfo``, and is the
                # key editions are grouped by, so being loose here is not cheaper
                # than being loose there.
                corroborated = next(
                    (hit for hit in matched if self._corroborates(album, hit)), None
                )
                if corroborated is None:
                    raise self._rejection(album, matched[0])
                return corroborated, "barcode-rg", candidates[0]
            raise AmbiguousMatch(
                f"{len(matched)} MusicBrainz release groups share barcode "
                f"{candidates[0]}",
                source=self.source.value,
                candidates=[_candidate_summary(hit) for hit in matched],
            )

        hit = matched[0]
        self._confirm(album, hit)
        detail = await self.client.release(str(hit.get("id")))
        if detail is None:
            raise NotMatched(
                f"MusicBrainz release {hit.get('id')} vanished between search and lookup",
                source=self.source.value,
            )
        return detail, "barcode", candidates[0]

    def _confirm(self, album: AlbumSnapshot, hit: Mapping[str, Any]) -> None:
        """Sanity-check a barcode hit before trusting it, or raise.

        MusicBrainz barcodes are typed in by volunteers and typos exist. One
        corroborating fact is enough — the track count agreeing, *or* the titles
        agreeing — but with neither, a matching barcode alone is more likely to
        be a transcription error than a match.
        """
        if not self._corroborates(album, hit):
            raise self._rejection(album, hit)

    @staticmethod
    def _corroborates(album: AlbumSnapshot, hit: Mapping[str, Any]) -> bool:
        """Does anything other than the barcode agree with this hit?

        Split out from :meth:`_confirm` so the multi-hit branch can ask the same
        question of several hits and still report the refusal as one outcome.
        """
        track_total = _track_total(hit)
        if album.tracks_count and track_total and track_total == album.tracks_count:
            return True
        return titles_match(hit.get("title"), album.title)

    def _rejection(self, album: AlbumSnapshot, hit: Mapping[str, Any]) -> MatchRejected:
        """The outcome for a barcode nothing else backs up, built from *hit*."""
        return MatchRejected(
            f"barcode matched but nothing else did: MusicBrainz says "
            f"'{_text(hit.get('title'))}' with {_track_total(hit)} track(s), "
            f"Qobuz says '{album.title}' with {album.tracks_count}",
            source=self.source.value,
            candidates=[_candidate_summary(hit)],
        )

    async def _resolve_by_release_group(
        self, album: AlbumSnapshot
    ) -> tuple[Mapping[str, Any], str, str]:
        """Match a barcode-less release within its artist's own release groups.

        Acceptable where a catalogue-wide title search would not be: the artist
        is already pinned by an MBID derived from a barcode match, so the only
        question is which of *their* records this is. Release-group level only —
        without a barcode there is no way to tell which pressing.
        """
        groups = await self.client.release_groups_for_artist(str(album.mb_artist_mbid))
        if not groups:
            raise NotMatched(
                "MusicBrainz lists no release groups for this artist",
                source=self.source.value,
            )

        def matches(candidate: Mapping[str, Any]) -> bool:
            return titles_match(candidate.get("title"), album.title) and years_close(
                candidate.get("first-release-date"), album.release_date
            )

        winner, survivors = sole_match(groups, matches)
        if winner is None:
            if survivors:
                raise AmbiguousMatch(
                    f"{len(survivors)} MusicBrainz release groups match "
                    f"'{album.title}'",
                    source=self.source.value,
                    candidates=[_candidate_summary(item) for item in survivors],
                )
            raise NotMatched(
                f"'{album.title}' is not in this artist's MusicBrainz discography",
                source=self.source.value,
            )
        return winner, "rg-browse", str(winner.get("id"))

    # ---------------------------------------------------------------- mapping
    def _release_group_only(
        self,
        album: AlbumSnapshot,
        hit: Mapping[str, Any],
        method: str,
        match_key: str,
    ) -> EnrichmentResult:
        """Record what a release group alone can tell us, and nothing more.

        Reached when several releases share a barcode, or when a barcode-less
        release was matched by browsing. Release-group facts — what kind of
        record it is, when it first came out — are common to every pressing and
        therefore safe. Catalogue number, country, media and the tracklist belong
        to one specific pressing, so they stay empty and **no track mapping is
        attempted**.

        *hit* is a release (whose ``release-group`` is nested) on the barcode
        path and a release group itself on the browse path.
        """
        nested = hit.get("release-group")
        group = nested if isinstance(nested, Mapping) else hit
        primary = _text(group.get("primary-type")) or None
        secondary = [
            _text(value) for value in (group.get("secondary-types") or []) if value
        ]

        return EnrichmentResult(
            match_key=match_key,
            album_fields={
                "mb_release_group_mbid": normalize_mbid(group.get("id")),
                "mb_match_method": method,
                "title": _text(group.get("title")) or _text(hit.get("title")) or None,
                "primary_type": primary,
                "secondary_types": join_tags(secondary) or None,
                "first_release_date": _text(group.get("first-release-date")) or None,
            },
            opinions={"release_type": _release_type(primary, secondary)},
        )

    def _build_album_result(
        self,
        album: AlbumSnapshot,
        payload: Mapping[str, Any],
        *,
        method: str,
        match_key: str,
    ) -> EnrichmentResult:
        """Map a full release payload onto metadata rows, votes and follow-ups."""
        group = payload.get("release-group") or {}
        primary = _text(group.get("primary-type")) or None
        secondary = [
            _text(value) for value in (group.get("secondary-types") or []) if value
        ]
        media = [m for m in (payload.get("media") or []) if isinstance(m, Mapping)]
        label_info = [
            item for item in (payload.get("label-info") or []) if isinstance(item, Mapping)
        ]

        album_fields: dict[str, Any] = {
            "mb_release_mbid": normalize_mbid(payload.get("id")),
            "mb_release_group_mbid": normalize_mbid(group.get("id")),
            "mb_match_method": method,
            "title": _text(payload.get("title")) or None,
            "primary_type": primary,
            "secondary_types": join_tags(secondary) or None,
            "first_release_date": _text(group.get("first-release-date")) or None,
            "release_date": _text(payload.get("date")) or None,
            "country": _text(payload.get("country")) or None,
            "status": _text(payload.get("status")) or None,
            "media_format": _text(media[0].get("format")) if media else None,
            "media_count": len(media) or None,
            "track_count": sum(int(m.get("track-count") or 0) for m in media) or None,
            "catalog_number": _first(
                _text(item.get("catalog-number")) for item in label_info
            ),
            "label": _first(
                _text((item.get("label") or {}).get("name")) for item in label_info
            ),
            "genres": join_tags(_genres(payload)) or None,
        }
        barcode = _text(payload.get("barcode"))
        if barcode:
            album_fields["barcode"] = barcode

        result = EnrichmentResult(
            match_key=match_key,
            album_fields=album_fields,
            opinions={"release_type": _release_type(primary, secondary)},
        )

        # The *first* credit, not the only one. MusicBrainz orders artist-credit
        # as the release is billed, so a collaboration still says who it is
        # primarily by; the name check on that first credit is what keeps a
        # guest appearance from rewriting the artist being enriched.
        derived = main_credit(
            [c for c in (payload.get("artist-credit") or []) if isinstance(c, Mapping)],
            expected_name=album.artist_name,
        )
        if derived and derived != album.mb_artist_mbid:
            result.artist_fields = {
                "mb_artist_mbid": derived,
                "mb_match_method": "release-credit",
                "mb_match_evidence": 1,
            }
            result.reopen.append(
                (EnrichmentEntity.ARTIST, album.artist_id, EnrichmentSource.MUSICBRAINZ)
            )
            result.note = (
                f"MusicBrainz identified {album.artist_name} from '{album.title}'"
            )

        result.track_fields = _track_fields(album, media)
        return result


# ---------------------------------------------------------------------------
# Payload helpers — pure, tolerant of a field being absent or the wrong shape
# ---------------------------------------------------------------------------
def _text(value: Any) -> str:
    return str(value).strip() if value not in (None, "") else ""


def _first(values: Iterable[str]) -> str | None:
    for value in values:
        if value:
            return value
    return None


def _genres(payload: Mapping[str, Any]) -> list[str]:
    return [
        name
        for item in (payload.get("genres") or [])
        if isinstance(item, Mapping) and (name := _text(item.get("name")))
    ]


def _first_isni(payload: Mapping[str, Any]) -> str | None:
    """The artist's ISNI, normalised.

    Often absent — ISNI coverage is nothing like complete — which is why it is
    stored beside the MBID rather than instead of it.
    """
    return _first(normalize_isni(value) or "" for value in (payload.get("isnis") or []))


def _url_relations(payload: Mapping[str, Any]) -> dict[str, Any]:
    """Harvest the url relations worth keeping.

    These are *links*, never fetched from here. The Wikidata QID in particular is
    the entire entry point for the Wikidata rung — an exact id chain, so that rung
    inherits this one's matching risk rather than adding any of its own.
    """
    found: dict[str, Any] = {}
    for relation in payload.get("relations") or []:
        if not isinstance(relation, Mapping):
            continue
        column = _URL_RELATIONS.get(str(relation.get("type") or "").lower())
        if column is None:
            continue
        url = _text((relation.get("url") or {}).get("resource"))
        if not url or column in found:
            continue
        found[column] = url.rsplit("/", 1)[-1] if column == "wikidata_qid" else url
    return found


def _release_type(primary: str | None, secondary: Sequence[str]) -> str | None:
    """This source's vote on what kind of release it is."""
    from app.enrich.merge import release_type_from_musicbrainz  # noqa: PLC0415 - cycle

    return release_type_from_musicbrainz(primary, secondary)


def _track_total(hit: Mapping[str, Any]) -> int:
    """Tracks across every medium of a search hit; ``0`` when it does not say.

    Zero is "not stated", not "an empty release", which is why the callers test
    it for truth before comparing: a hit with no media listed must not
    corroborate an album whose own count is unknown.
    """
    media = hit.get("media")
    if not isinstance(media, Sequence) or isinstance(media, (str, bytes)):
        return 0
    return sum(
        int(medium.get("track-count") or 0)
        for medium in media
        if isinstance(medium, Mapping)
    )


def barcodes_in_group(releases: Iterable[Mapping[str, Any]]) -> tuple[str, ...]:
    """Every usable barcode across the releases of one release group. PURE.

    Deduplicated and normalised, so the caller hands
    :func:`app.enrich.matching.qobuz_album_by_barcode` one set covering every
    edition of the record rather than the single pressing AcoustID landed on.

    Everything that is not a 12- or 13-digit barcode is dropped by
    :func:`normalize_barcode` — MusicBrainz stores an empty string for "this
    release has no barcode" and the literal absence of one is not a key.
    Ordering is stable (first seen wins) so a failure message names the same
    barcode twice in a row.
    """
    seen: dict[str, None] = {}
    for release in releases:
        if not isinstance(release, Mapping):
            continue
        digits = normalize_barcode(release.get("barcode"))
        if digits:
            seen.setdefault(digits, None)
    return tuple(seen)


def _candidate_summary(hit: Mapping[str, Any]) -> dict[str, Any]:
    """The shape the review list shows for something a human must decide."""
    group = hit.get("release-group") or {}
    return {
        "id": str(hit.get("id") or ""),
        "title": _text(hit.get("title")),
        "barcode": _text(hit.get("barcode")) or None,
        "date": _text(hit.get("date")) or _text(group.get("first-release-date")) or None,
        "country": _text(hit.get("country")) or None,
        "disambiguation": _text(hit.get("disambiguation")) or None,
        "release_group_id": str(group.get("id") or "") or None,
        "url": f"https://musicbrainz.org/release/{hit.get('id')}" if hit.get("id") else None,
    }


# ---------------------------------------------------------------------------
# Candidate cards — display only, and only for a person
# ---------------------------------------------------------------------------
def _lucene(value: str) -> str:
    """Escape the operators Lucene would otherwise act on.

    An unescaped colon in ``Bach: Cello Suites`` turns everything before it into
    a field name and the query silently returns nothing — which reads exactly
    like "MusicBrainz has never heard of this record".
    """
    out = []
    for char in str(value or ""):
        if char in '+-&|!(){}[]^"~*?:\\/':
            out.append("\\")
        out.append(char)
    return "".join(out).strip()


def _terms(value: str) -> str:
    """*value* as loose Lucene terms: escaped, split, and deliberately unquoted."""
    words = str(value or "").replace(",", " ").replace("/", " ").split()
    return " ".join(term for word in words if (term := _lucene(word)))


def _release_query(album: AlbumSnapshot) -> str:
    """A starting query for the release we are trying to name.

    Loose on purpose, and this is the one design decision in the picker that was
    worth measuring. A phrase query — ``release:"<exact title>"`` — is the
    obvious shape and it is useless here: Qobuz names the Wagner release
    *Wagner: Ouvertures, Preludes & Orchestral Works* and MusicBrainz names it
    *Ouvertures, Preludes & Orchestral Works*, with the composer in the credit
    rather than the title. The phrase form returned nothing for a release
    MusicBrainz demonstrably holds — it had already been matched by barcode.

    Grouped bare terms return it first. ``release:(a b c) AND artist:(d e)``
    requires a hit in each field but not every word in either, which is the right
    trade for a list somebody is about to read: too many results costs a glance,
    while zero results looks exactly like "MusicBrainz has never heard of this".
    """
    parts = []
    if title := _terms(album.title):
        parts.append(f"release:({title})")
    if artist := _terms(album.artist_name or ""):
        parts.append(f"artist:({artist})")
    return " AND ".join(parts)


def _release_candidate(hit: Mapping[str, Any]) -> Candidate:
    summary = _candidate_summary(hit)
    mbid = summary["id"]
    credit = " ".join(
        f"{_text(part.get('name'))}{_text(part.get('joinphrase'))}"
        for part in (hit.get("artist-credit") or [])
        if isinstance(part, Mapping)
    ).strip()
    media = [m for m in (hit.get("media") or []) if isinstance(m, Mapping)]
    # The search index reports the total at release level, but not always — a
    # release with one medium sometimes carries it only per-medium. Summing the
    # media is the fallback, because "12 tracks" is one of the two or three facts
    # that separate a single from the album it was cut from.
    tracks = _int(hit.get("track-count")) or sum(
        _int(medium.get("track-count")) or 0 for medium in media
    )
    formats = _first(
        _text(medium.get("format")) for medium in media if medium.get("format")
    )
    bits = [
        (summary["date"] or "")[:4],
        f"{tracks} track{'' if tracks == 1 else 's'}" if tracks else "",
        formats or "",
        summary["country"] or "",
        # The barcode is the single most decisive line on the card: it is what
        # the automatic matcher would have used, so seeing it is how a person
        # confirms this really is their pressing rather than a near neighbour.
        f"barcode {summary['barcode']}" if summary["barcode"] else "",
    ]
    return Candidate(
        external_id=mbid,
        title=summary["title"] or "Untitled",
        subtitle=credit or None,
        detail=" · ".join(bit for bit in bits if bit) or None,
        disambiguation=summary["disambiguation"],
        # MusicBrainz stores no images; the Archive serves art keyed by the very
        # id on this card. It 404s more often than not, which the template treats
        # as "no artwork" rather than as a broken image.
        image_url=f"https://coverartarchive.org/release/{mbid}/front-250" if mbid else None,
        url=summary["url"],
    )


def _artist_candidate(hit: Mapping[str, Any]) -> Candidate:
    life = hit.get("life-span") if isinstance(hit.get("life-span"), Mapping) else {}
    area = hit.get("area") if isinstance(hit.get("area"), Mapping) else {}
    begin, end = _text(life.get("begin"))[:4], _text(life.get("end"))[:4]
    span = f"{begin}–{end}" if begin and end else begin or ""
    bits = [
        _text(hit.get("type")),
        _text(hit.get("country")) or _text(area.get("name")),
        span,
    ]
    return Candidate(
        external_id=str(hit.get("id") or ""),
        title=_text(hit.get("name")) or "Unknown",
        subtitle=(
            sort if (sort := _text(hit.get("sort-name"))) != _text(hit.get("name")) else None
        ),
        detail=" · ".join(bit for bit in bits if bit) or None,
        disambiguation=_text(hit.get("disambiguation")) or None,
        url=f"https://musicbrainz.org/artist/{hit.get('id')}" if hit.get("id") else None,
    )


def _track_fields(
    album: AlbumSnapshot, media: Sequence[Mapping[str, Any]]
) -> dict[str, dict[str, Any]]:
    """Map recordings onto Qobuzarr's tracks, or map none of them.

    Two gates, both on the whole album rather than on individual tracks:

    1. **Layout equality.** Disc count and per-disc track counts must agree. A
       release with a bonus disc is a different product, however similar the
       first disc looks.
    2. **ISRC agreement.** If any Qobuz track carries an ISRC and the recording
       it landed on lists ISRCs that exclude it, the *entire* mapping is
       abandoned.

    Gate 2 does not repair the offending track, deliberately. One positional
    disagreement means the layout assumption itself is wrong — a hidden track, a
    pregap, a differently-ordered reissue — and a half-shuffled tracklist is
    worse than none: it looks authoritative and is wrong from that point on.
    """
    if not album.tracks or not media:
        return {}
    if len(media) != album.media_count:
        return {}

    by_disc: dict[int, list[Any]] = {}
    for track in album.tracks:
        by_disc.setdefault(track.media_number or 1, []).append(track)

    mapped: dict[str, dict[str, Any]] = {}
    for index, medium in enumerate(media, start=1):
        remote = [t for t in (medium.get("tracks") or []) if isinstance(t, Mapping)]
        local = sorted(by_disc.get(index, ()), key=lambda t: t.track_number or 0)
        if len(remote) != len(local) or not local:
            return {}
        for track, entry in zip(local, remote):
            recording = entry.get("recording") or {}
            isrcs = {str(value).upper() for value in (recording.get("isrcs") or [])}
            if track.isrc and isrcs and str(track.isrc).upper() not in isrcs:
                logger.debug(
                    "Abandoning MusicBrainz track mapping for %s: ISRC %s is not on "
                    "recording %s",
                    album.id,
                    track.isrc,
                    recording.get("id"),
                )
                return {}
            mapped[track.id] = {
                "mb_release_track_mbid": normalize_mbid(entry.get("id")),
                "mb_recording_mbid": normalize_mbid(recording.get("id")),
                "isrc": track.isrc or _first(str(value) for value in isrcs) or None,
                "title": _text(entry.get("title")) or None,
                "length_ms": _int(entry.get("length") or recording.get("length")),
                "match_method": "release-position",
            }
    return mapped


def _int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None
