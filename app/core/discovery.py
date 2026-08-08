"""Turn a folder nobody follows into a followed Qobuz artist, using the audio.

The disk scan reports every artist it found that is not followed and stops
there — 136 of them on this library. The bulk importer is the other half of that
sentence, and it answers the question by *name*: one ``catalog/search`` per
folder name, followed only on an exact match under
:func:`app.core.scanner.artist_key`. That is deliberately strict, because Qobuz
answers ``Joanne Shaw Taylor`` with a ``Joanna Shaw Taylor`` too and the cost of
a wrong guess is monitoring a stranger's discography. The price of the strictness
is everything whose folder is not spelled the way Qobuz spells it: a composer
under a localised name, a band with an ampersand, ``Дмитрий Дмитриевич
Шостакович``.

This module is the other route to the same answer, and it never reads a name to
decide anything:

    AcoustID  ->  MusicBrainz  ->  universal code  ->  Qobuz

The audio says which recordings the files hold;
:func:`app.enrich.coverage.solve_album` says which release — or which releases —
explain the whole directory; each of those is looked up on MusicBrainz by id for
its title, its artist credits and its barcode; and a barcode picks the Qobuz
album out of a free-text search, which carries the artist credit inline. Every
step is an exact key except the search itself, and the search is never allowed to
select: see :func:`app.enrich.matching.qobuz_album_by_barcode`.

**Nothing the folder says is ever used to decide, and that now includes the
query.** The rule used to be stated as "a name may narrow a search, never select
within it", and the implementation quietly broke the half nobody was watching: it
built the query from the directory's own tags. A folder only reaches this module
because its name *already failed* to match the catalogue, so it is the one input
carrying positive evidence against it, and searching in its vocabulary asks the
question in the spelling just demonstrated not to work. Measured live: Brad
Paisley's folder is ``Play: The Guitar Album`` and that query returns **nothing**
on Qobuz, while MusicBrainz's title for the same record — ``Play`` — returns it
first. So every search term comes from a release MusicBrainz returned for an MBID
the *audio* chose. The folder's name survives in one place, ``folder_artist``,
which is reported so a human can read the row and is never matched on.

**An ambiguous verdict is not a dead end.** ``solve_album`` refuses to name a
*pressing* when several releases explain the directory equally well, and it is
right to — a guess there is written into every file as ``MUSICBRAINZ_ALBUMID``.
But this chain never needed the pressing. It needs barcodes, and every candidate
is a release id MusicBrainz will answer for. Brad Paisley's *Play* returns four
releases that each explain 16 of 16 files, all in one release group, one of them
carrying Qobuz's exact barcode. Reading that verdict as a refusal is what made
this route bind nothing at all.

**Why the release group, and not the release.** The pressing MusicBrainz matched
is usually not the one Qobuz sells. Joanne Shaw Taylor's *White Sugar* is
``710347114727`` in ``album_metadata`` and ``0884385226442`` on Qobuz — two real
barcodes, two real editions, one record. Matching on the single barcode of the
single release AcoustID landed on therefore misses most of a catalogue. Matching
on *any* barcode in the group finds it and gives up no precision at all, because
a release group carries one artist credit: an edition is still an exact answer
about who made the record. That is the whole reason
:func:`app.enrich.musicbrainz.barcodes_in_group` exists.

**This module identifies; it does not follow.** Following in bulk belongs to
:mod:`app.core.importer` and nowhere else, and the disk scan's one-way rule —
a scan may promote an album to ``DOWNLOADED``, never mark one wanted, never
enqueue — is what makes it safe to run unattended every night. So nothing here
opens a session, writes a row, or starts a download. It answers a question and
returns the answer; the caller decides what to do with it.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping, Sequence

from app.enrich.chromaprint import FpcalcMissing
from app.enrich.errors import EnrichmentOutcome
from app.enrich.matching import (
    barcode_candidates,
    qobuz_album_by_barcode,
    qobuz_artist_credit,
)
from app.enrich.musicbrainz import barcodes_in_group
from app.enrich.types import AlbumSnapshot, EnrichmentJob, TrackSnapshot
from app.models import EnrichmentEntity, EnrichmentSource

logger = logging.getLogger(__name__)

#: Candidate releases resolved per folder. An ambiguous verdict normally carries
#: two to four editions of one record; a compilation can carry dozens, and each
#: one costs a MusicBrainz request. They are editions of the same record, so the
#: barcodes converge fast — reading every one buys almost nothing and spends the
#: upstream's allowance on a folder that is unlikely to resolve anyway.
_MAX_CANDIDATES = 6

__all__ = [
    "FolderIdentity",
    "Outcome",
    "folder_snapshot",
    "identifier_for",
    "identify_folder",
    "search_terms",
]


class Outcome:
    """Why a folder ended where it did. Strings, because they are reported."""

    IDENTIFIED = "identified"
    """A single Qobuz album matched a barcode, and it names an artist."""

    AMBIGUOUS = "ambiguous"
    """The audio or the barcode fitted more than one record. A person decides."""

    UNIDENTIFIED = "unidentified"
    """Nothing matched. Not an error — most of a mixed library lands here."""

    GATED = "gated"
    """A rung could not run at all: no ``fpcalc``, no AcoustID key, no contact."""


@dataclass(frozen=True)
class FolderIdentity:
    """What one directory turned out to be, and how certain that is.

    Every field is ``None`` until something exact established it. There is no
    "best guess" field and there must not be one: the id in
    :attr:`qobuz_artist_id` is about to be followed and monitored, which starts
    marking releases wanted, so a wrong answer here is not a cosmetic error.
    """

    directory: str
    folder_artist: str
    """The name on disk. Reported so a human can read the row; never matched on."""

    outcome: str = Outcome.UNIDENTIFIED
    reason: str = ""

    qobuz_artist_id: str | None = None
    qobuz_artist_name: str | None = None
    qobuz_album_id: str | None = None

    mb_release_mbid: str | None = None
    mb_release_group_mbid: str | None = None
    barcodes: tuple[str, ...] = ()
    candidates: tuple[Mapping[str, Any], ...] = field(default=())

    @property
    def followable(self) -> bool:
        """True only when there is an exact Qobuz artist to follow."""
        return self.outcome == Outcome.IDENTIFIED and bool(self.qobuz_artist_id)


def folder_snapshot(scanned: Any) -> AlbumSnapshot:
    """A :class:`~app.enrich.types.AlbumSnapshot` for a folder with no database rows.

    This is the adapter that lets the existing AcoustID rung run against a
    directory the database has never heard of. The rung reaches files through
    :attr:`TrackSnapshot.path` and nothing else — it never loads a row — so a
    synthetic snapshot is a complete input rather than a stub.

    The ids are the paths. They are opaque to everything downstream
    (:class:`~app.enrich.coverage.FileCandidates` says so explicitly: "``file_id``
    is opaque — a track id, a path, an index — and is only ever echoed back in
    the evidence"), and using the path keeps them unique without inventing a
    numbering the directory does not have. Nothing persists them: an id here
    never reaches ``tracks``, which is what keeps this module's promise that it
    writes nothing.
    """
    tracks = tuple(
        TrackSnapshot(
            id=str(track.path),
            title=track.title or Path(str(track.path)).stem,
            track_number=int(getattr(track, "track_number", 0) or 0),
            media_number=int(getattr(track, "media_number", 1) or 1),
            path=str(track.path),
        )
        for track in getattr(scanned, "tracks", ()) or ()
    )
    return AlbumSnapshot(
        id=f"disk:{scanned.directory}",
        artist_id="",
        # ``artist_name`` first, and that ordering is the bug this once was.
        # :class:`~app.core.scanner.ScannedAlbum` has no ``artist`` attribute —
        # it carries ``artist_candidates`` and computes ``artist_name`` from it —
        # so reading ``artist`` alone silently produced an empty string, and
        # :func:`search_terms` then sent Qobuz a bare album title. Its own
        # docstring says why that fails ("a bare album title matches half the
        # catalogue"): searching for ``Play: The Guitar Album`` returned
        # ``artist_name`` first: :class:`~app.core.scanner.ScannedAlbum` has no
        # ``artist`` attribute — it carries ``artist_candidates`` and computes
        # ``artist_name`` from it — so reading ``artist`` alone silently produced
        # an empty string. ``artist`` is kept as a fallback because the parameter
        # is duck-typed and a caller may hand over something simpler.
        #
        # This reaches the fingerprinter and the *report*, and goes nowhere near
        # the Qobuz query — see :func:`search_terms`.
        artist_name=str(
            getattr(scanned, "artist_name", "") or getattr(scanned, "artist", "") or ""
        ),
        title=str(getattr(scanned, "title", "") or ""),
        tracks_count=len(tracks),
        tracks=tracks,
    )


def search_terms(releases: Sequence[Mapping[str, Any]], *, limit: int = 6) -> tuple[str, ...]:
    """Qobuz queries built from MusicBrainz alone. **Nothing from the folder.**

    This used to fall back to the folder's own title and take the artist from the
    directory, and that was wrong on the chain's own premise rather than merely
    unlucky. The whole reason a folder reaches this module is that its name
    *already failed* to match the catalogue — it is the one input with positive
    evidence against it. Feeding it back in as the search key asks the catalogue
    a question in the vocabulary that has just been demonstrated not to work:
    ``Brad Paisley Play: The Guitar Album`` returns **nothing**, while
    MusicBrainz's own title for the same record, ``Play``, returns it first.

    So every term here comes from a release MusicBrainz returned for an MBID the
    *audio* chose. That keeps the rule the module is built on — a name may narrow
    a search, never select within it — and adds the one it was quietly breaking:
    a name may only narrow when something verified supplied it.

    **Several credits on purpose, which is what classical needs.** MusicBrainz
    files a release under every artist credited on it, and the two catalogues
    routinely disagree about which of them is "the" album artist: MusicBrainz
    leads with the composer where Qobuz leads with the orchestra, the conductor
    or the soloist. Taking only the first credit is a coin toss on exactly the
    repertoire this route exists for. Each credit is paired with each title and
    the barcode still decides, so extra queries cost requests and can never cost
    precision.

    Returns an empty tuple when MusicBrainz supplied no usable name, which the
    caller must treat as "no search to make" rather than reaching for the folder.
    """
    titles: list[str] = []
    credits: list[str] = []
    for release in releases:
        title = _text(release.get("title"))
        if title and title not in titles:
            titles.append(title)
        for credit in release.get("artist-credit") or ():
            if not isinstance(credit, Mapping):
                continue
            artist = credit.get("artist")
            name = _text(credit.get("name")) or (
                _text(artist.get("name")) if isinstance(artist, Mapping) else None
            )
            if name and name not in credits:
                credits.append(name)

    queries: list[str] = []
    for title in titles:
        for credit in credits or [""]:
            query = " ".join(part for part in (credit, title) if part)
            if query and query not in queries:
                queries.append(query)
    return tuple(queries[: max(1, int(limit))])


async def identify_folder(
    scanned: Any,
    *,
    acoustid: Any,
    musicbrainz: Any,
    qobuz: Any,
    search_limit: int = 20,
) -> FolderIdentity:
    """Run the whole chain for one directory. Makes requests; writes nothing.

    Returns a :class:`FolderIdentity` in every case, including every failure —
    a folder that could not be identified is the normal outcome for most of a
    mixed library, not an exception. The one thing that propagates is
    :class:`~app.enrich.chromaprint.FpcalcMissing`'s meaning: it gates the whole
    pass rather than failing one folder, because the answer would be identical
    for every remaining directory and running them all to say so is a waste of
    the caller's rate limit.
    """
    snapshot = folder_snapshot(scanned)
    identity = FolderIdentity(
        directory=str(scanned.directory), folder_artist=snapshot.artist_name
    )
    if not snapshot.tracks:
        return _with(identity, reason="the folder holds no audio files")

    # ---- 1. the audio ----------------------------------------------------
    # ``entity_id`` is the synthetic snapshot id and reaches no table: this
    # module opens no session. It is here because the job is the rung's input
    # shape, not because anything is about to be written under it.
    job = EnrichmentJob(
        entity_type=EnrichmentEntity.ALBUM,
        source=EnrichmentSource.ACOUSTID,
        entity_id=snapshot.id,
        subject=snapshot,
    )
    refused = ""
    mbids: tuple[str, ...] = ()
    try:
        result = await acoustid.fetch(job)
    except FpcalcMissing:
        raise
    except EnrichmentOutcome as outcome:
        # An ambiguous verdict is not a dead end, and reading it as one is what
        # made this route bind nothing. ``solve_album`` refuses to name a
        # *pressing* when several releases explain the directory equally well —
        # rightly, since a guess would be written into every file as
        # MUSICBRAINZ_ALBUMID — but every one of those candidates is a release
        # MBID, and this chain does not need the pressing. It needs barcodes, and
        # it matches Qobuz on any of them.
        #
        # It deliberately does NOT lean on ``partial``'s release group, which was
        # the first attempt: ``acoustid._release_group_id`` says in as many words
        # that ``meta=releases`` usually carries no group, so that field is empty
        # almost always — 0 of 40 folders on a real library. The candidates are
        # always there, and step 2 turns them into the group anyway.
        state = getattr(outcome, "state", "") or ""
        mbids = _candidate_mbids(outcome)
        if not mbids:
            # The rung's own vocabulary already distinguishes "cannot run" from
            # "ran and found nothing", and it is the right answer to report.
            return _with(
                identity,
                outcome=Outcome.GATED
                if state in ("gated", "no_key")
                else Outcome.AMBIGUOUS
                if state == "ambiguous"
                else Outcome.UNIDENTIFIED,
                reason=str(outcome) or state,
            )
        result = getattr(outcome, "partial", None)
        refused = str(outcome)
        logger.debug(
            "%s: the pressing is ambiguous; carrying %d candidate release(s) forward",
            scanned.directory,
            len(mbids),
        )
    except Exception as exc:  # noqa: BLE001 - one folder must not stop the pass
        logger.exception("Fingerprinting %s failed", scanned.directory)
        return _with(identity, reason=f"fingerprinting failed: {exc}")

    fields = dict(getattr(result, "album_fields", None) or {})
    identity = _with(
        identity,
        mb_release_mbid=fields.get("mb_release_mbid"),
        mb_release_group_mbid=fields.get("mb_release_group_mbid"),
    )
    if not mbids:
        pinned = _text(fields.get("mb_release_mbid"))
        mbids = (pinned,) if pinned else ()
    if not mbids:
        return _with(identity, reason="the audio did not settle on any release")

    # ---- 2. what MusicBrainz says those releases are ---------------------
    # One exact lookup per candidate. This is where every *verified* fact enters
    # the chain: the catalogue's own title, its artist credits, its barcode and
    # its release group. Nothing from the folder is used past this point.
    releases: list[Mapping[str, Any]] = []
    for mbid in mbids[:_MAX_CANDIDATES]:
        try:
            payload = await musicbrainz.release(mbid)
        except Exception as exc:  # noqa: BLE001
            logger.warning("MusicBrainz release %s failed: %s", mbid, exc)
            continue
        if payload:
            releases.append(payload)
    if not releases:
        return _with(identity, reason="MusicBrainz returned none of the candidate releases")

    groups = _distinct(
        _text((release.get("release-group") or {}).get("id"))
        for release in releases
        if isinstance(release.get("release-group"), Mapping)
    )
    if len(groups) == 1:
        identity = _with(identity, mb_release_group_mbid=groups[0])

    # ---- 3. every edition's barcode --------------------------------------
    # The candidates' own barcodes first, then every other edition of the same
    # record: the pressing MusicBrainz matched is usually not the one Qobuz
    # sells, and a release group carries one artist credit, so widening across it
    # gives up no precision.
    found = [_text(release.get("barcode")) for release in releases]
    for group in groups:
        try:
            found.extend(barcodes_in_group(await musicbrainz.releases_in_group(group)))
        except Exception as exc:  # noqa: BLE001
            logger.warning("MusicBrainz release group %s failed: %s", group, exc)

    # Through ``barcode_candidates`` rather than used raw, and that is not
    # tidiness: MusicBrainz stores a barcode as it was typed, so the same code is
    # the 12-digit UPC-A on one release and the 13-digit EAN on another.
    # Brad Paisley's *Play* is ``884977725872`` in MusicBrainz and
    # ``0884977725872`` on Qobuz — one leading zero between a match and nothing.
    barcodes = barcode_candidates(*[value for value in found if value])
    identity = _with(identity, barcodes=barcodes)
    if not barcodes:
        return _with(
            identity,
            reason="no release of this record carries a barcode to match Qobuz on",
        )

    # ---- 4. the searches, and the barcode that decides --------------------
    # Every query is MusicBrainz's own vocabulary — see :func:`search_terms` for
    # why the folder's is not merely unhelpful but disqualified. Several queries
    # because the two catalogues disagree about which credit leads a release, and
    # results are pooled: the barcode is what selects, so a wider net can add
    # candidates it cannot promote a wrong one.
    queries = search_terms(releases)
    if not queries:
        return _with(identity, reason="MusicBrainz named no title to search Qobuz with")

    items: list[Mapping[str, Any]] = []
    seen_ids: set[str] = set()
    failures: list[str] = []
    for query in queries:
        try:
            found_items = await qobuz.search_albums(query, limit=search_limit)
        except Exception as exc:  # noqa: BLE001
            logger.warning("Qobuz search for %r failed: %s", query, exc)
            failures.append(str(exc))
            continue
        for item in found_items or ():
            key = _text(item.get("id"))
            if key and key not in seen_ids:
                seen_ids.add(key)
                items.append(item)
        # A barcode hit is exact, so once one query has produced the record there
        # is nothing a further query could improve — only requests it could cost.
        if qobuz_album_by_barcode(items, barcodes)[0] is not None:
            break

    if not items and failures:
        return _with(identity, reason=f"the Qobuz search failed: {failures[0]}")

    winner, survivors = qobuz_album_by_barcode(items, barcodes)
    if winner is None and len(survivors) > 1:
        return _with(
            identity,
            outcome=Outcome.AMBIGUOUS,
            reason=f"{len(survivors)} Qobuz albums carry a barcode from this record",
            candidates=tuple(survivors),
        )
    if winner is None:
        return _with(
            identity,
            reason=(
                f"no Qobuz album among {len(items)} result(s) for "
                f"{len(queries)} query/queries carries one of "
                f"{len(barcodes)} barcode(s)"
            ),
        )

    artist_id, artist_name = qobuz_artist_credit(winner)
    if not artist_id:
        return _with(
            identity,
            qobuz_album_id=_text(winner.get("id")),
            reason="the matched Qobuz album credits no artist",
        )
    return _with(
        identity,
        outcome=Outcome.IDENTIFIED,
        reason=(
            f"matched on barcode {_text(winner.get('upc'))}"
            # Said out loud rather than smoothed over: the record is certain and
            # the pressing was not, and somebody reading this row should be able
            # to tell those apart without re-deriving it.
            + (" (the record, not the pressing: the audio fitted several editions)"
               if refused else "")
        ),
        qobuz_album_id=_text(winner.get("id")),
        qobuz_artist_id=artist_id,
        qobuz_artist_name=artist_name,
    )


def identifier_for(providers: Any, qobuz: Any) -> Any | None:
    """Bind :func:`identify_folder` to the ladder's own rungs, or return ``None``.

    The AcoustID and MusicBrainz rungs are taken from the **enrichment ladder
    that is already running** rather than constructed here, and that is the one
    rate-limiter-per-upstream rule rather than an optimisation of it: a second
    ``MusicBrainzClient`` would carry a second limiter, and two limiters over one
    upstream is exactly the budget-doubling ``CLAUDE.md`` forbids. It also means
    the route follows ``ENRICHMENT_SOURCES`` for free — turn either rung off and
    this returns ``None``, because a chain missing a link is not a chain.

    ``None`` is a first-class answer, not a failure: a Qobuzarr with no AcoustID
    key still has a working name-based importer, and the caller passes the
    ``None`` straight to :class:`~app.core.importer.LibraryImporter`, which then
    behaves exactly as it did before this route existed.
    """
    from app.models import EnrichmentSource  # noqa: PLC0415 - avoids a cycle

    # The enricher publishes its ladder as a {source: provider} mapping and
    # build_providers returns a sequence; accept either rather than making the
    # caller remember which one it is holding.
    rungs = providers.values() if isinstance(providers, Mapping) else (providers or ())
    by_source = {getattr(one, "source", None): one for one in rungs}
    acoustid = by_source.get(EnrichmentSource.ACOUSTID)
    musicbrainz = by_source.get(EnrichmentSource.MUSICBRAINZ)
    if acoustid is None or musicbrainz is None or qobuz is None:
        return None
    client = getattr(musicbrainz, "client", None)
    if client is None or not hasattr(client, "releases_in_group"):
        return None

    async def identify(scanned: Any) -> FolderIdentity:
        return await identify_folder(
            scanned, acoustid=acoustid, musicbrainz=client, qobuz=qobuz
        )

    return identify


def _candidate_mbids(outcome: Any) -> tuple[str, ...]:
    """The release MBIDs an ambiguous verdict weighed, in the order it weighed them.

    ``AcoustIdProvider`` attaches these as ``candidates`` — the summaries the
    review list renders — and every one is a release that explained the whole
    directory. They are the durable half of an ambiguous answer: the *pressing*
    cannot be chosen from them, but each is an exact key MusicBrainz will answer
    for, which is all this chain needs.
    """
    found: list[str] = []
    for candidate in getattr(outcome, "candidates", ()) or ():
        if not isinstance(candidate, Mapping):
            continue
        mbid = _text(candidate.get("id"))
        if mbid and mbid not in found:
            found.append(mbid)
    return tuple(found)


def _distinct(values: Any) -> tuple[str, ...]:
    """Truthy values, de-duplicated, first seen wins."""
    seen: list[str] = []
    for value in values:
        if value and value not in seen:
            seen.append(value)
    return tuple(seen)


def _with(identity: FolderIdentity, **changes: Any) -> FolderIdentity:
    """A copy of *identity* with fields replaced; the dataclass is frozen."""
    from dataclasses import replace

    return replace(identity, **changes)


def _text(value: Any) -> str | None:
    """A trimmed string, or ``None`` for anything blank."""
    if value is None:
        return None
    text = str(value).strip()
    return text or None
