"""The enrichment orchestrator.

Reads ``enrichment_state`` to find what is due, hands each due unit to the
provider that owns that source, and writes what comes back into the three
metadata side tables. It is to the open datasources what
:class:`~app.core.indexer.Indexer` is to Qobuz, with three differences that all
follow from what it is talking to.

**Sessions are never held across a network call.** SQLite allows exactly one
writer. SQLAlchemy's SQLite dialect opens the transaction at the first write and
holds it until commit — so a loop that wrote, awaited a two-second MusicBrainz
call, then committed would block :class:`~app.core.queue.QueueWorker`'s per-track
commits for up to ``busy_timeout``, after which the worker's broad ``except``
would mark a perfectly good download **failed**. Every tick is therefore three
separate phases: seed and claim under one short transaction, then *all* HTTP with
no session open, then persist under another. The
:class:`~app.enrich.types.EnrichmentJob` snapshots exist to make that possible.

**It is not paced like the indexer.** One artist per five minutes is right for
Qobuz, where the constraint is a paid account that can be banned. These are
public APIs with published allowances, and copying that cadence would take
months to enrich a real library. Pacing lives in the per-source
:class:`~app.net.ratelimit.RateLimiter` instead; a tick simply works until its
wall-clock budget runs out.

**"No" is a first-class answer.** A provider raising
:class:`~app.enrich.errors.EnrichmentOutcome` is not a failure — it means the
upstream behaved and the answer was no match, an ambiguous match, or nothing to
match on. Each carries the state it becomes and gets its own retry ladder, so an
album with no barcode is never asked about again while one MusicBrainz has simply
not catalogued yet is retried next month.

**The subject is the library, not the catalogue.** Following one prolific artist
puts their entire discography in ``albums``, and enriching all of it is work
nobody asked for: metadata is written into files, rendered into NFOs and read off
the artist page, and an album that is not on disk has no files, no NFO and
nothing to correct. One real library made the arithmetic plain — 3313 albums, 32
of them downloaded, and 11418 enrichment rows pending, so the thirty-two releases
that actually mattered sat behind three thousand that did not. So eligibility is
:func:`library_scope`: an album is in scope when its status is ``downloaded``
(which includes the ones the disk scan adopted), and an artist is in scope when
they own at least one such album. Nothing else is seeded, claimed, re-armed or
listed for review, and :func:`purge_out_of_scope_enrichment` deletes the state
rows of everything that has fallen out of it.

That purge is safe for the same reason the schema-version rebuild is: an
``enrichment_state`` row is a *work list entry*, re-created by :meth:`_seed` the
moment an album lands. What it deliberately does **not** touch is the three
metadata tables. Those hold what was learned, and two things depend on it
surviving: ``album_metadata.qobuz_release_type`` is the only way an applied
``Album.release_type`` reverses, and ``mb_release_group_mbid`` is what
``deps.list_release_group`` groups editions by. Deleting either would change what
release Qobuzarr thinks it is looking at, which is the one thing narrowing the
scope must not do.
"""

from __future__ import annotations

import asyncio
import re
import time
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Iterable, Mapping, Sequence

from sqlalchemy import Select, and_, delete, func, or_, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, get_settings
from app.core.queue import SessionFactory
from app.db import session_scope
from app.enrich.errors import (
    STATE_AMBIGUOUS,
    STATE_FAILED,
    STATE_GATED,
    STATE_NOT_FOUND,
    STATE_NO_KEY,
    STATE_OK,
    STATE_PENDING,
    STATE_REJECTED,
    EnrichmentOutcome,
    SourceGated,
)
from app.enrich.matching import album_claimed_barcode, barcode_candidates
from app.enrich.merge import (
    ConsensusResult,
    consensus,
    decode_consensus,
    encode_consensus,
    release_type_from_qobuz,
)
from app.enrich.types import (
    AlbumSnapshot,
    ArtistSnapshot,
    Candidate,
    EnrichmentJob,
    EnrichmentProvider,
    EnrichmentResult,
    SearchableProvider,
    TrackSnapshot,
)
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
    FileClaim,
    RELEASE_TYPES,
    Track,
    TrackMetadata,
    TrackOrigin,
)
from app.net.errors import HttpError

__all__ = [
    "AcceptOutcome",
    "Enricher",
    "EnrichResult",
    "REVIEW_STATES",
    "actionable_review_clause",
    "enrichment_autonomy_counts",
    "enrichment_scope_counts",
    "identifiable_sources",
    "in_library",
    "library_scope",
    "mark_library_due",
    "purge_out_of_scope_enrichment",
    "reopen_library_albums",
    "review_picker_for",
    "utc",
]

logger = get_logger(__name__)


def utc(value: datetime | None) -> datetime | None:
    """Return *value* as a timezone-aware UTC datetime.

    SQLite hands back naive datetimes even from ``DateTime(timezone=True)``
    columns, and comparing one to an aware ``utcnow()`` raises ``TypeError``.
    """
    if value is None:
        return None
    return value if value.tzinfo is not None else value.replace(tzinfo=timezone.utc)


def _now() -> datetime:
    """Timezone-aware current time (kept separate so tests can monkeypatch)."""
    return datetime.now(timezone.utc)


# ---------------------------------------------------------------------------
# Scope — what enrichment is for
# ---------------------------------------------------------------------------
def library_scope(entity_type: EnrichmentEntity) -> Select[tuple[str]]:
    """The ids of the entities enrichment may touch: the ones on disk.

    An album is in scope when its status is ``downloaded``. That covers the
    releases the download loop wrote *and* the ones the disk scan adopted, which
    have no ``Track`` rows at all — the status is the only thing both have in
    common, and it is exactly the question being asked: is this release in the
    library? ``downloading`` is deliberately not in scope; the album joins a
    moment later, when it has actually landed.

    An artist is in scope when they own at least one such album. Following an
    artist is a statement about what to *watch*, not about what is owned, so an
    artist with nothing on disk yet has no files to tag and no artist.nfo to
    write — there is nothing for the enrichment to be applied to.

    Returned as a ``SELECT`` rather than a list of ids because every caller wants
    it as a subquery: it is evaluated inside the statement it constrains, so it
    can never go stale between being read and being used.
    """
    downloaded = Album.status == AlbumStatus.DOWNLOADED
    if entity_type is EnrichmentEntity.ALBUM:
        return select(Album.id).where(downloaded)
    # ``is_not(None)`` is not decoration: an ``IN`` against a subquery holding a
    # NULL evaluates to NULL rather than false, which would quietly turn every
    # scope test into "no opinion".
    return select(Album.artist_id).where(downloaded, Album.artist_id.is_not(None))


def in_library() -> Any:
    """SQL predicate: this ``enrichment_state`` row's entity is in the library.

    The row-level form of :func:`library_scope`, for the statements that filter
    ``enrichment_state`` itself rather than a list of ids. ``entity_id`` is
    polymorphic, so each entity type has to be tested against its own scope —
    the same shape :func:`prune_enrichment_orphans` uses for the same reason.
    """
    return or_(
        (EnrichmentState.entity_type == EnrichmentEntity.ARTIST)
        & EnrichmentState.entity_id.in_(library_scope(EnrichmentEntity.ARTIST)),
        (EnrichmentState.entity_type == EnrichmentEntity.ALBUM)
        & EnrichmentState.entity_id.in_(library_scope(EnrichmentEntity.ALBUM)),
    )


#: Where an id sits in each upstream's URLs. Both put it in the last path
#: segment after a type word, so one shape covers releases, release groups,
#: artists and Deezer's localised ``/en/album/…`` links alike.
_URL_ID = re.compile(
    r"""(?:musicbrainz\.org|deezer\.com|deezer\.page\.link)
        /(?:[a-z]{2}/)?                       # deezer's optional locale segment
        (?:release-group|release|artist|album|track|recording)
        /([0-9a-fA-F-]{8,36}|\d+)""",
    re.VERBOSE,
)


def _id_from_url(value: str, source: EnrichmentSource) -> str:
    """The identifier inside a pasted link, or *value* unchanged.

    Asking someone for a bare identifier asks them to do string surgery on a URL
    they already have open. They paste the URL, and before this the answer was
    ``'https://musicbrainz.org/release/8817…' is not a MusicBrainz id`` — which
    is true, unhelpful, and about a string that contains the id it is rejecting.

    Deliberately permissive about *which* URL: a release-group link pasted into a
    release box still yields something that looks like an MBID, and letting the
    upstream lookup fail on it gives a far better message than a regex refusing
    to see an id that is plainly there. Anything that is not a link is handed
    back untouched, so a bare id still takes the normal path.
    """
    text = value.strip().strip("<>")
    if "/" not in text:
        return text
    match = _URL_ID.search(text)
    if match:
        return match.group(1)
    # A link to somewhere else entirely. Returning it unchanged lets the
    # per-source validation below say what an id looks like, which is more use
    # than "unrecognised URL".
    return text


def _manually_identified_fields(row: Any) -> frozenset[str]:
    """Columns on *row* that a person owns and no automatic pass may rewrite.

    The ``manual`` marker is only worth writing if something reads it. Without
    this the next pass over an album whose artist a human corrected re-derives
    the artist from a release credit and quietly puts the wrong id back — and
    that id is written into every file on disk as ``MUSICBRAINZ_ARTISTID``.
    """
    owned: set[str] = set()
    for method_field, fields in _MANUAL_OWNS.items():
        if str(getattr(row, method_field, "") or "").lower() == "manual":
            owned |= fields
    return frozenset(owned)


# ---------------------------------------------------------------------------
# Retry ladders
# ---------------------------------------------------------------------------
#: How long each terminal state waits before it is worth asking again.
#:
#: ``no_key`` and ``gated`` are absent on purpose: neither can improve until its
#: *inputs* change, so both store ``next_attempt_at = NULL``. Asking MusicBrainz
#: nightly about an album that has no barcode is pure noise. Their inputs *do*
#: change, though, and two mechanisms notice: :meth:`Enricher.reopen` when
#: somebody acts, and :meth:`Enricher._rearm_stranded` every tick, which asks
#: whether the id or the configuration a row was waiting for has since arrived.
#: The second exists because the first only helps if the moment is caught.
_NOT_FOUND_LADDER: tuple[timedelta, ...] = (
    timedelta(days=7),
    timedelta(days=30),
    timedelta(days=90),
)

#: Ambiguity is a human's problem, not a scheduling one — it sits on the review
#: list. It is retried slowly all the same, because upstream edits do resolve it.
_AMBIGUOUS_DELAY = timedelta(days=30)

#: Transport failures back off fast and cap at a day.
_FAILED_INITIAL = timedelta(minutes=5)
_FAILED_CAP = timedelta(hours=24)

#: How long :meth:`Enricher.cascade` may spend, and how many times round.
#:
#: A cascade runs inside the request that identified something, so its ceiling
#: is somebody watching a spinner rather than the tick's much larger appetite
#: (``enrichment_tick_budget`` defaults to 240s, which is a fine number for a
#: background job and an absurd one for a button). Reaching either ceiling is
#: not a failure: the rows it did not get to are due, and the scheduled tick
#: takes them a few minutes later exactly as it always did. The caller is told
#: so rather than left to assume everything finished.
#:
#: Four rounds because that is the longest real chain plus one. Identifying an
#: artist re-arms their albums (round 1 the artist, round 2 the albums), and
#: MusicBrainz discovering a Wikidata QID re-opens Wikidata a round after that.
#: A cascade converges by the same argument :meth:`_rearm_stranded` does — every
#: round ends its rows in a state that is not "due now" — so the cap is a
#: backstop against a provider that re-opens what it just closed, not the
#: mechanism that ends the loop.
_CASCADE_BUDGET = 30.0
_CASCADE_MAX_ROUNDS = 4

#: How long a cascade waits for a scheduled tick to finish before giving up on
#: running at all. Deliberately short. The lock is what stops two drains
#: double-spending the request budget, and waiting on it properly would mean a
#: request blocking for up to a whole tick budget; skipping costs nothing,
#: because the tick holding the lock re-claims the very rows this cascade would
#: have taken.
_CASCADE_LOCK_WAIT = 2.0

#: States that carry no ``next_attempt_at`` at all.
#:
#: ``no_key`` and ``gated`` are dead ends waiting on their inputs, and
#: :meth:`Enricher._rearm_stranded` watches for those inputs arriving.
#: ``rejected`` is the third, and it is a different kind of dead end: it is
#: waiting on nobody, so nothing re-arms it and only :meth:`Enricher.reopen`
#: brings it back.
_TERMINAL_STATES = frozenset({STATE_NO_KEY, STATE_GATED, STATE_REJECTED})

#: The states the review list is *about* — the two the matchers produce when
#: they refuse to guess and a person has to decide.
#:
#: One tuple, four readers: :func:`list_review_items`' default, the
#: :attr:`ReviewItem.is_actionable` test, the nav badge in
#: :func:`app.api.deps.nav_counts`, and :meth:`Enricher.reject`'s notion of what
#: there is to reject. It was a literal ``("ambiguous", "no_key")`` in the first
#: three, which is a rule written down three times: the badge and the page it
#: links to disagree the moment one copy learns a state the others have not, and
#: a badge promising rows the list does not show is worse than no badge. Note
#: what is deliberately *not* here — ``not_found`` (the upstream simply has no
#: such record yet, which is waiting rather than deciding) and ``rejected`` (a
#: person has already decided, and putting it back would undo them).
REVIEW_STATES: tuple[str, ...] = (STATE_AMBIGUOUS, STATE_NO_KEY)

#: The ``artist_metadata`` column each source waits on, and which
#: :meth:`Enricher._rearm_stranded` therefore watches for a late arrival. A
#: source absent from this map is skipped rather than guessed at.
#:
#: Two different waits share one map. Deezer and MusicBrainz wait on an id they
#: *browse a discography* by — an album with no barcode is matched by walking
#: the artist's releases, and that needs the artist pinned first, which is why
#: those two also drive the album half of the re-arm. Wikidata waits on an id it
#: needs for the **artist alone**: the QID is not a browse key, it is the whole
#: subject, and it arrives second-hand from MusicBrainz's URL relations long
#: after the Wikidata row went ``no_key``.
#:
#: Leaving Wikidata out is what made it the one rung with no state-based
#: backstop. Its only route back was the single ``reopen`` MusicBrainz appends
#: on the tick that discovers the QID — which fires exactly once, at a moment
#: that may have passed before this code existed, and never at all when the QID
#: was already in the snapshot. That is precisely the fragility
#: :meth:`Enricher._rearm_stranded` exists to remove, and the fix is to ask the
#: state rather than to catch the event. Cover Art Archive is still absent and
#: still correct: it is keyed by the *album's* own MBID, which is not a column
#: on ``artist_metadata`` at all.
#:
#: The album half of the re-arm is gated on the provider actually handling
#: albums, so adding an artist-only source here cannot re-arm album rows it
#: would only refuse.
_ARTIST_ID_COLUMN: Mapping[EnrichmentSource, Any] = {
    EnrichmentSource.DEEZER: ArtistMetadata.deezer_artist_id,
    EnrichmentSource.MUSICBRAINZ: ArtistMetadata.mb_artist_mbid,
    EnrichmentSource.WIKIDATA: ArtistMetadata.wikidata_qid,
}

#: Sources whose album match starts from :func:`~app.enrich.matching.barcode_candidates`,
#: and which therefore go ``no_key`` on an album that has no barcode *yet*. The
#: fourth input :meth:`Enricher._rearm_stranded` watches, and the fourth instance
#: of one failure: a rung refused for want of an input, the input arrived later
#: and elsewhere, and nothing was watching.
#:
#: A barcode arrives from more places than the rung that wanted it — Qobuz's
#: ``upc`` (absent from the ``getReleasesList`` payload the indexer builds rows
#: from, so it lands only when something explicitly fetches ``album/get``),
#: another rung verifying one, or a person identifying the release by hand. On a
#: real library this was the single largest stranded block: 480 album rows across
#: two sources reading "no barcode on this release and no … artist to browse",
#: on releases whose barcode either was already discoverable or became so on the
#: next rung.
#:
#: **This pass converges, and the reason is a property of the providers rather
#: than a counter.** Both consumers refuse differently once a barcode exists:
#: with candidates in hand neither can reach its ``NoMatchKey`` branch, so a
#: failed re-attempt lands on ``not_found`` ("no Deezer release carries barcode
#: …") and never back on ``no_key``. Each row is therefore re-armed at most once
#: per barcode it has never been tried with. Widen this map to a source that can
#: still answer ``no_key`` with a barcode in hand and that guarantee is gone —
#: the row re-arms every tick, forever, spending the upstream's budget on a
#: question already answered.
_BARCODE_SOURCES: frozenset[EnrichmentSource] = frozenset(
    {EnrichmentSource.DEEZER, EnrichmentSource.MUSICBRAINZ}
)

#: What a manual identification owns, per source. Once somebody has said "it is
#: *this* one", the id and the ids derived from it are theirs — an automatic pass
#: that disagrees is exactly the contradiction the ``manual`` marker exists to
#: lose to. Keyed by the ``*_match_method`` column that records how a row was
#: matched; the release group travels with the release id because keeping one and
#: replacing the other would leave the row describing two different records.
_MANUAL_OWNS: Mapping[str, frozenset[str]] = {
    "mb_match_method": frozenset(
        {
            "mb_artist_mbid",
            "mb_release_mbid",
            "mb_release_group_mbid",
            "mb_match_method",
            "mb_match_evidence",
        }
    ),
    "deezer_match_method": frozenset(
        {"deezer_artist_id", "deezer_album_id", "deezer_match_method"}
    ),
}

#: The only sources a person can hand an id to. The rest are keyed by an id an
#: earlier rung established — the Cover Art Archive by MusicBrainz's release
#: MBID, Wikidata by the QID MusicBrainz publishes, AcoustID by the audio — so
#: there is no identifier of their own to type, and no column to put one in.
_IDENTIFIABLE_SOURCES = frozenset(
    {EnrichmentSource.MUSICBRAINZ, EnrichmentSource.DEEZER}
)

#: When a rung refuses, whose picker settles it — for the rungs that take no id
#: of their own. Keyed by the refusing source, valued by the picker to open and
#: the states in which opening one is honest.
#:
#: AcoustID is the case this exists for, and it is not a special case so much as
#: the one place where "this source takes no id" and "nobody can act on this"
#: came apart. An AcoustID ``ambiguous`` says *several releases explain this
#: directory equally well* — and those releases are MusicBrainz releases, the
#: candidates :func:`app.enrich.coverage.solve_album` weighed and refused to
#: choose between. The decision is genuinely available to a person; it is simply
#: recorded against MusicBrainz, because that is where the id lives. Judging it
#: by its own source hid it: measured on this library, 51 rows, **26 of them on
#: albums with no other review row at all**, so they vanished from the list
#: entirely — and 34 sat on ``musicbrainz=ok`` with no ``mb_release_mbid``,
#: matched to a release *group* and never to a pressing, which is the exact
#: question AcoustID was asking.
#:
#: The state matters as much as the source. AcoustID ``no_key`` means *no files
#: to fingerprint*, which no id can fix — offering a picker there is the input
#: box that cannot lead anywhere. So a delegation names the states it holds for
#: and nothing outside them is actionable.
#:
#: This is a delegation, never a widening. The rule
#: :attr:`ReviewItem.identify_sources` states — a person resolving a MusicBrainz
#: ambiguity is asked about MusicBrainz's records, not offered Deezer's — is
#: untouched: each entry names exactly one picker, chosen because it is where
#: that rung's own candidates come from.
_DELEGATED_PICKERS: Mapping[
    EnrichmentSource, tuple[EnrichmentSource, frozenset[str]]
] = {
    EnrichmentSource.ACOUSTID: (
        EnrichmentSource.MUSICBRAINZ,
        frozenset({STATE_AMBIGUOUS}),
    ),
}


def _as_source(value: Any) -> EnrichmentSource | None:
    """Coerce a stored ``source`` back to its enum member, or ``None``.

    Enum columns are ``native_enum=False`` and normally read back as members,
    but a row written by an older build (or a source since removed from the
    vocabulary) can be a bare string that no longer maps. ``None`` is the honest
    answer for that: the rule it would be fed to, :func:`review_picker_for`,
    keys on identity, and guessing would put an unpressable row in front of a
    person.
    """
    if isinstance(value, EnrichmentSource):
        return value
    try:
        return EnrichmentSource(str(value))
    except ValueError:
        return None


def review_picker_for(source: EnrichmentSource, state: str) -> EnrichmentSource | None:
    """The picker that settles *source* refusing in *state*, or ``None``.

    The single answer to "can a person act on this row, and against what?".
    :attr:`ReviewItem.identify_sources`, the SQL in
    :func:`actionable_review_clause` and :meth:`Enricher._pickers_for` all defer
    to it, so the list, the badge and the button cannot come to different
    conclusions — which is the same reason :data:`REVIEW_STATES` is one tuple
    rather than three literals.
    """
    if state not in REVIEW_STATES:
        return None
    if source in _IDENTIFIABLE_SOURCES:
        return source
    delegated = _DELEGATED_PICKERS.get(source)
    if delegated is not None and state in delegated[1]:
        return delegated[0]
    return None


def actionable_review_clause() -> Any:
    """The SQL half of :func:`review_picker_for`, for the list and the badge.

    Actionability has to be decided in the database rather than filtered out of
    the rows afterwards: ``total`` would otherwise count what the page does not
    show and the last page of a paginated list would come back short.

    Built from the same two constants the Python property reads, so a source
    that becomes identifiable is identifiable in both at once. Rows in a state
    outside :data:`REVIEW_STATES` pass untouched — ``?state=rejected`` is how
    somebody finds a dismissal in order to change their mind about it, and a
    filter that swallowed those would take away the only route back.
    """
    allowed = [EnrichmentState.source.in_(sorted(_IDENTIFIABLE_SOURCES))]
    for source, (_picker, states) in _DELEGATED_PICKERS.items():
        allowed.append(
            and_(
                EnrichmentState.source == source,
                EnrichmentState.state.in_(sorted(states)),
            )
        )
    return or_(EnrichmentState.state.not_in(list(REVIEW_STATES)), *allowed)


def identifiable_sources() -> tuple[EnrichmentSource, ...]:
    """The sources a person can hand an id to, in enum order.

    A published view of :data:`_IDENTIFIABLE_SOURCES`, because the API has to
    tell a client which review rows will open a picker and which will not, and
    the alternative is the client keeping its own copy of the rule. That copy
    goes stale in the direction that hurts: a source added here without the
    client knowing renders an actionable row with no way to act on it, and one
    removed renders a picker whose POST is a 400.

    Sorted by the enum's own order rather than by name, so the list reads the
    way every other vocabulary in the API does.
    """
    return tuple(
        source for source in EnrichmentSource if source in _IDENTIFIABLE_SOURCES
    )


#: What each ``enrichment_state`` value actually claims, in one sentence apiece.
#:
#: This is domain knowledge and it belongs beside the code that assigns the
#: states, not beside whatever renders them. The two sentences that matter most
#: — ``ambiguous`` and ``no_key`` — were written into a template, which put the
#: only explanation of "exact or nothing" in the one place that cannot see the
#: matcher, and left it free to keep saying something the matcher had stopped
#: doing. Adding a state to :data:`app.enrich.errors.ENRICHMENT_STATES` without
#: adding a line here yields an empty string, which renders as no explanation
#: rather than as a wrong one.
_STATE_EXPLANATIONS: dict[str, str] = {
    STATE_PENDING: "Not looked at yet — it is on the work list and its turn will come.",
    STATE_OK: "Matched. The identifiers this source supplied are recorded.",
    STATE_NOT_FOUND: (
        "This source has no such record. Nothing is wrong and nobody has to "
        "decide anything — it is worth asking again once the upstream has had "
        "time to catch up."
    ),
    STATE_AMBIGUOUS: (
        "More than one upstream record matched and none of them is trusted over "
        "the others, so nothing was written. A wrong identifier here is copied "
        "into every file on disk, which is why a person picks rather than the "
        "matcher guessing."
    ),
    STATE_NO_KEY: (
        "There was nothing exact to match on — no barcode, and no identified "
        "artist whose discography could be browsed. Names are only ever used to "
        "reject a candidate, never to choose one."
    ),
    STATE_GATED: (
        "This source is switched off or missing something it needs, so no "
        "request was made. It re-opens by itself once the setting arrives."
    ),
    STATE_FAILED: (
        "The lookup errored. It is retried on a timer; a run of these usually "
        "means the upstream is unwell rather than the release being unusual."
    ),
    STATE_REJECTED: (
        "Somebody looked at this and said no. Nothing retries it and nothing "
        "re-opens it by itself — unlike the other dead ends, it is not waiting "
        "for an input to arrive. Run it again to put it back on the list."
    ),
}


@dataclass(slots=True)
class CascadeResult:
    """Outcome of one :meth:`Enricher.cascade`, for the message a person reads.

    Separate from :class:`EnrichResult` because it answers a different question.
    A tick reports on the *library* — how much was seeded, claimed, written. A
    cascade reports on the *press*: did anything happen while you waited, and is
    there more to come. Folding one into the other would put six fields nobody
    can act on into a toast.

    ``exhausted`` and ``skipped`` are both "not finished", kept apart because
    they say different things to the person who pressed the button. ``skipped``
    means nothing ran at all (a scheduled tick held the lock); ``exhausted``
    means work was done and more remains. Neither is an error, and both end the
    same way — the rows are due, and the next tick takes them.
    """

    #: Rounds that claimed at least one job. Zero means the identification
    #: unblocked nothing that was not already in flight, which is ordinary.
    rounds: int = 0
    #: Provider calls made.
    ran: int = 0
    #: Files re-tagged by the write-back phase.
    written_back: int = 0
    #: A tick held the lock, so this cascade did not run.
    skipped: bool = False
    #: Stopped on the budget or the round cap with work still answerable.
    exhausted: bool = False


@dataclass(slots=True)
class EnrichResult:
    """Outcome of one :meth:`Enricher.tick`, for logs and the status page."""

    seeded: int = 0
    #: Dead-end rows brought back because the id they were waiting for arrived.
    rearmed: int = 0
    claimed: int = 0
    matched: int = 0
    unmatched: int = 0
    gated: int = 0
    failed: int = 0
    #: Releases whose files were re-tagged because this tick identified them.
    written_back: int = 0
    elapsed: float = 0.0
    budget_exhausted: bool = False
    by_source: dict[str, int] = field(default_factory=dict)

    @property
    def attempted(self) -> int:
        """How many jobs actually reached a provider."""
        return self.matched + self.unmatched + self.gated + self.failed

    def as_dict(self) -> dict[str, Any]:
        """Plain dict for the JSON API and the activity feed."""
        return {
            "seeded": self.seeded,
            "rearmed": self.rearmed,
            "claimed": self.claimed,
            "matched": self.matched,
            "unmatched": self.unmatched,
            "gated": self.gated,
            "failed": self.failed,
            "written_back": self.written_back,
            "elapsed": round(self.elapsed, 2),
            "budget_exhausted": self.budget_exhausted,
            "by_source": dict(self.by_source),
        }


@dataclass(slots=True)
class AcceptOutcome:
    """What :meth:`Enricher.accept` did, or why it did nothing.

    Accept exists because the design draws an "Accept & apply" button, and the
    obvious implementation of that button is the one thing this codebase must
    never do: search the upstream by name and take the top hit. That would break
    "exact or nothing" (:mod:`app.enrich.matching`) at the exact point it costs
    most — the id it chose is written into every file on disk as
    ``MUSICBRAINZ_ALBUMID``, and Picard, beets and Roon then believe it.

    So Accept applies a proposal **the system already holds**, or it applies
    nothing and says so. There is exactly one such proposal today:
    ``album_metadata.suggested_release_type``, the majority verdict of
    :func:`app.enrich.merge.consensus`, which is already surfaced as
    ``AlbumOut.suggested_release_type`` / ``release_type_disagrees`` and which
    ``qobuz_release_type`` already makes reversible.

    When there is none, :attr:`applied` is ``False`` and
    :attr:`identify_sources` names the pickers a person could open instead. That
    is not a fallback, it is the correct answer: a human choosing is what
    :meth:`Enricher.identify` is for, and a button that opened a picker while
    claiming to have applied something would be lying about which of the two
    just happened.

    A note on what is *not* implemented, so the next person does not go looking:
    an ``ambiguous`` row whose provider withheld a single candidate for want of
    corroboration (:class:`app.enrich.errors.MatchRejected`) would be the second
    kind of held proposal — but ``EnrichmentOutcome.candidates`` is never
    persisted. ``_persist`` writes ``outcome.message`` to ``last_error`` and
    nothing else, so after the tick that produced it the candidate is gone. The
    system does not hold that proposal, and Accept does not pretend it does;
    those rows report ``applied=False`` and open the picker. Persisting them
    would mean a column on ``enrichment_state``, hence
    ``ENRICHMENT_SCHEMA_VERSION``, hence dropping and rebuilding the four
    metadata tables — including every manual identification somebody typed.
    """

    #: Whether anything was written. ``False`` is a complete, successful answer.
    applied: bool = False
    #: Which held proposal was applied — ``"release_type"`` or ``None``.
    proposal: str | None = None
    message: str = ""
    #: What the field held before, and what it holds now. Both ``None`` when
    #: nothing was applied, so a client cannot render a change that did not
    #: happen.
    previous_value: str | None = None
    applied_value: str | None = None
    #: The pickers to offer when there was nothing to apply. Same rule as
    #: :attr:`ReviewItem.identify_sources`: only sources that take an id a person
    #: could type, and only ones this process actually has a provider for.
    identify_sources: tuple[str, ...] = ()


class Enricher:
    """Drains ``enrichment_state``, one bounded batch per tick.

    Args:
        providers: The rungs, in ladder order. A source with no provider is
            simply never claimed, which is how ``ENRICHMENT_SOURCES`` disables
            one without any special casing.
        settings: Overrides ``get_settings()``.
        session_factory: How to open a session. Injected so tests can point it
            at a scratch database.
    """

    def __init__(
        self,
        providers: Sequence[EnrichmentProvider] = (),
        *,
        settings: Settings | None = None,
        session_factory: SessionFactory = session_scope,
    ) -> None:
        self._settings = settings or get_settings()
        self._session_factory = session_factory
        self._providers: dict[EnrichmentSource, EnrichmentProvider] = {
            provider.source: provider for provider in providers
        }
        # Ticks are serialised: two of them draining the same due list would
        # claim the same rows and spend the request budget twice.
        self._lock = asyncio.Lock()
        self._consecutive_failures = 0
        self._paused_reason: str | None = None
        self._last_result: EnrichResult | None = None
        self._last_run_at: datetime | None = None
        #: A ladder waiting for the current tick to finish — see
        #: :meth:`reconfigure`. ``None`` means what is running is what is
        #: configured, which is what :attr:`ladder_pending` reports. The second
        #: element is always ``None`` on this path: a deferral holds back the
        #: *rungs*, never the ``Settings`` object, which is applied immediately
        #: so a later write cannot be reverted by an older snapshot landing on
        #: top of it.
        self._pending_ladder: (
            tuple[Callable[[], Sequence[EnrichmentProvider]], Settings | None] | None
        ) = None

    # ------------------------------------------------------------- accessors
    @property
    def providers(self) -> dict[EnrichmentSource, EnrichmentProvider]:
        """The registered providers, keyed by source."""
        return dict(self._providers)

    @property
    def enabled(self) -> bool:
        """True when enrichment is switched on and something can actually run."""
        return bool(
            self._settings.enrichment_enabled
            and self._providers
            and self._paused_reason is None
        )

    @property
    def paused_reason(self) -> str | None:
        """Why the job stopped itself, or ``None`` when it is running."""
        return self._paused_reason

    def resume(self) -> None:
        """Clear a self-imposed pause. Called by the explicit "run now" action."""
        self._paused_reason = None
        self._consecutive_failures = 0

    def active_sources(self) -> list[EnrichmentSource]:
        """Registered sources that ``ENRICHMENT_SOURCES`` also enables, in order."""
        wanted = self._settings.enrichment_source_list
        return [
            source
            for name in wanted
            for source in (EnrichmentSource(name),)
            if source in self._providers
        ]

    async def aclose(self) -> None:
        """Release every provider's HTTP client."""
        for provider in self._providers.values():
            try:
                await provider.aclose()
            except Exception:  # pragma: no cover - shutdown must not raise
                logger.exception("Closing the %s provider failed", provider.source)

    # ------------------------------------------------------- reconfiguration
    @property
    def ladder_pending(self) -> bool:
        """True when a new ladder has been asked for but is not running yet.

        The honest half of a settings write: ``ENRICHMENT_SOURCES`` changed, a
        tick was mid-flight, and the rungs on the next tick will not be the ones
        on this one. The settings screen renders it as such rather than claiming
        the change is live.
        """
        return self._pending_ladder is not None

    async def reconfigure(
        self,
        factory: Callable[[], Sequence[EnrichmentProvider]],
        *,
        settings: Settings | None = None,
        timeout: float = 2.0,
    ) -> bool:
        """Replace the ladder with what *factory* builds. Returns whether it is live.

        ``True`` means the swap happened here; ``False`` means a tick was
        draining and the swap is deferred to the start of the next one (see
        :attr:`ladder_pending`), because closing a provider's HTTP client out
        from under a request in flight is how a settings press becomes a failed
        enrichment.

        *factory* is a callable rather than a list for the one-limiter-per-upstream
        rule: :func:`app.enrich.registry.build_providers` mints a fresh
        ``RateLimiter`` per source, so building a ladder that is then thrown away
        would leave a second limiter for every upstream in it. Deferring keeps
        the *old* ladder — one limiter each — until the moment it is replaced.

        **What is deferred is the rungs, never the values.** ``settings`` is
        applied at once even on the deferred path, and the pending entry carries
        only the factory. Holding the object back instead means holding a
        *snapshot*: any settings write that lands before the next tick is
        re-pointed onto this enricher by
        :meth:`app.core.state.AppState._repoint_settings`, and then the deferred
        install puts the snapshot back over the top of it. Turning
        ``ENRICHMENT_WRITE_BACK`` off while a ladder change is pending would
        switch itself back on one tick later and start re-tagging files, which is
        a settings screen undoing the user rather than the other way round.
        Applying it here is also no new hazard: ``_repoint_settings`` already
        replaces this reference mid-tick, and ``active_sources()`` intersects the
        names with :attr:`_providers`, so a source named but not yet built simply
        does not run until it is.
        """
        try:
            await asyncio.wait_for(self._lock.acquire(), timeout)
        except (TimeoutError, asyncio.TimeoutError):
            if settings is not None:
                self._settings = settings
            self._pending_ladder = (factory, None)
            logger.info(
                "Enrichment ladder change deferred: a tick is running. It takes "
                "effect at the start of the next one."
            )
            return False
        try:
            await self._install_ladder(factory, settings)
        finally:
            self._lock.release()
        return True

    async def _install_ladder(
        self,
        factory: Callable[[], Sequence[EnrichmentProvider]],
        settings: Settings | None,
    ) -> None:
        """Swap in a freshly built ladder. **The caller must hold the lock.**

        Built first and closed second on purpose. The two ladders overlap only
        for the duration of a dict assignment, and no request can be issued
        through the new one until it is installed, so at no point are two
        limiters *in use* against one upstream — while building second would mean
        a factory that raises had already closed the ladder that was working.
        """
        if settings is not None:
            self._settings = settings
        providers = list(factory())
        superseded = self._providers
        self._providers = {provider.source: provider for provider in providers}
        self._pending_ladder = None
        # A pause is a verdict about the ladder that just failed; the operator
        # has replaced it, so it does not carry over.
        self._paused_reason = None
        self._consecutive_failures = 0
        for provider in superseded.values():
            try:
                await provider.aclose()
            except Exception:  # noqa: BLE001 - a stuck close must not lose the swap
                logger.exception("Closing the %s provider failed", provider.source)
        logger.info(
            "Enrichment ladder is now: %s",
            ", ".join(provider.source.value for provider in providers) or "(empty)",
        )

    async def _apply_pending_ladder(self) -> None:
        """Install a deferred ladder. Called at the top of a tick, under the lock."""
        pending = self._pending_ladder
        if pending is None:
            return
        factory, settings = pending
        self._pending_ladder = None
        try:
            await self._install_ladder(factory, settings)
        except Exception:  # noqa: BLE001 - a bad ladder must not kill the tick
            logger.exception("Could not install the reconfigured enrichment ladder")

    # ------------------------------------------------------------------ tick
    async def tick(self, *, limit: int | None = None) -> EnrichResult:
        """Seed, claim and process one batch.

        Returns immediately when a tick is already running — the next scheduled
        one will pick the work up, and two concurrent drains would double-spend
        the request budget.
        """
        if self._lock.locked():
            logger.debug("Enrichment tick already running; skipping this one")
            return EnrichResult()

        async with self._lock:
            result = await self._run_once(limit=limit)
            self._last_result = result
            self._last_run_at = _now()
            return result

    async def _run_once(self, *, limit: int | None) -> EnrichResult:
        started = time.monotonic()
        result = EnrichResult()

        # Before anything else: a ladder change that arrived while the last tick
        # was draining takes effect now, so this tick runs the rungs the settings
        # screen says it does.
        await self._apply_pending_ladder()

        if not self.enabled:
            return result

        batch = limit or self._settings.enrichment_batch_size
        budget = max(1.0, float(self._settings.enrichment_tick_budget))

        # Phase 1 — seed and claim. One short transaction, no network.
        result.seeded = await self._seed()
        # Before claiming, not after: a row repaired here is due immediately, so
        # this tick can spend its budget on it rather than idling for five
        # minutes with answerable work sitting in a terminal state.
        result.rearmed = await self._rearm_stranded()
        jobs = await self._claim(batch)
        result.claimed = len(jobs)
        if not jobs:
            result.elapsed = time.monotonic() - started
            return result

        # Phase 2 — all HTTP, with no session open anywhere.
        outcomes: list[tuple[EnrichmentJob, EnrichmentResult | BaseException]] = []
        for job in jobs:
            if time.monotonic() - started > budget:
                result.budget_exhausted = True
                # Un-claimed jobs simply stay due; nothing is lost.
                await self._release([j for j in jobs[len(outcomes) :]])
                break
            outcomes.append((job, await self._run_job(job)))

        # Phase 3 — persist. A fresh transaction, still no network.
        await self._persist(outcomes, result)

        # Phase 4 — put what was learned into the files. Its own transaction
        # again, after the persist has committed, because it reads back what
        # phase 3 wrote.
        result.written_back = await self._write_back(outcomes)

        self._update_health(result)
        result.elapsed = time.monotonic() - started
        return result

    async def _run_job(self, job: EnrichmentJob) -> EnrichmentResult | BaseException:
        """Call one provider, converting every failure into a value."""
        provider = self._providers.get(job.source)
        if provider is None:  # pragma: no cover - claim already filters these
            return SourceGated("No provider registered", source=job.source.value)
        if not provider.supports(job.entity_type):
            # A row that should never have been created — an older seeding rule,
            # a hand-edited database, a source whose scope narrowed. Retiring it
            # is right; letting the provider assert on the wrong subject is not.
            return SourceGated(
                f"{job.source.value} does not handle {job.entity_type.value}s",
                source=job.source.value,
            )
        if not provider.is_ready():
            # Ask the provider what is actually missing. This used to record a
            # flat "acoustid is not configured", which sent someone looking for
            # an API key they had already set — the missing piece was the
            # ``fpcalc`` binary, and the provider knew that and was never asked.
            # A rung with nothing more specific to say keeps the old wording.
            reason = getattr(provider, "gate_reason", None)
            return SourceGated(
                reason() if callable(reason) else f"{job.source.value} is not configured",
                source=job.source.value,
            )
        try:
            return await provider.fetch(job)
        except EnrichmentOutcome as outcome:
            return outcome
        except HttpError as exc:
            return exc
        except Exception as exc:  # noqa: BLE001 - one bad provider must not stop the tick
            logger.exception(
                "Enriching %s %s from %s raised",
                job.entity_type.value,
                job.entity_id,
                job.source.value,
            )
            return exc

    # ------------------------------------------------------------------ seed
    async def _seed(self) -> int:
        """Create missing ``enrichment_state`` rows for every in-scope entity.

        Deliberately *not* wired into the indexer. A single query per (entity
        type, source) picks up anything new — whether it arrived via a download,
        a scan, a bulk import, the API or a hand-edited database — and costs one
        statement whether it finds nothing or ten thousand rows.

        That is also what makes "an album becoming ``downloaded`` is enrichable
        immediately" true without anything remembering the moment it happened.
        Four separate places flip an album to ``downloaded`` — the download loop,
        the queue worker, the disk scan and a restore from the trash — and a hook
        on each is four chances to miss one. Asking :func:`library_scope` every
        tick is state-based, idempotent and repairs history, exactly like
        :meth:`_rearm_stranded`. :func:`mark_library_due` still runs at the two
        moments a person is watching, so the wait is a tick rather than an
        interval; it is an accelerator, not the mechanism.
        """
        sources = self.active_sources()
        if not sources:
            return 0

        seeded = 0
        async with self._session_factory() as session:
            for source in sources:
                provider = self._providers[source]
                for entity_type, model in (
                    (EnrichmentEntity.ARTIST, Artist),
                    (EnrichmentEntity.ALBUM, Album),
                ):
                    if not provider.supports(entity_type):
                        continue
                    existing = select(EnrichmentState.entity_id).where(
                        EnrichmentState.entity_type == entity_type,
                        EnrichmentState.source == source,
                    )
                    missing = (
                        await session.execute(
                            select(model.id).where(
                                model.id.in_(library_scope(entity_type)),
                                model.id.not_in(existing),
                            )
                        )
                    ).scalars().all()
                    for entity_id in missing:
                        session.add(
                            EnrichmentState(
                                entity_type=entity_type,
                                entity_id=str(entity_id),
                                source=source,
                                state=STATE_PENDING,
                                next_attempt_at=_now(),
                            )
                        )
                        seeded += 1
        if seeded:
            logger.info("Seeded %d enrichment rows", seeded)
        return seeded

    async def _rearm_stranded(self) -> int:
        """Bring back rows waiting on something that has since arrived.

        Two dead ends, one rule. ``gated`` waits on configuration; ``no_key`` on
        an album waits on its artist's id. Both store ``next_attempt_at = NULL``,
        so neither retries by itself, and both are routinely fixed by something
        that happens *later* and elsewhere.

        The album case is the one that bleeds. An album with no barcode can only
        be matched by browsing its artist's discography, and the artist's id
        arrives *late* — it is derived from the first of that artist's releases to
        match on a barcode, which is rarely the first one processed. Everything
        the enricher reached before that moment recorded "no artist to browse"
        and went silent permanently, while being perfectly answerable minutes
        later. In one real library that had already stranded 28 rows across three
        artists and was still growing.

        Deliberately **state-based rather than event-based**. Re-opening the
        albums at the instant the id is derived is the obvious fix and it is not
        enough: it only helps rows stranded after the fix ships, and it depends on
        catching an event exactly once. Asking "is anything waiting for something
        that has arrived?" each tick is a handful of indexed statements, is
        idempotent, converges (a re-armed row ends ``ok``/``not_found``/
        ``ambiguous``, never back where it started), and repairs history —
        including rows stranded by a manual identification, which writes the id
        straight into ``artist_metadata`` and would otherwise leave that artist's
        whole backlog sitting there.

        Only the two sources that browse by artist take part in the second pass:
        Cover Art Archive is keyed by the album's own MBID and Wikidata takes no
        albums, so neither has an artist id an album could be waiting on.

        Both passes are held to :func:`library_scope`. A row for something that
        is not on disk is not stranded, it is out of scope, and re-arming it
        would put work back into the very queue the scope exists to keep short.
        """
        now = _now()
        rearmed = 0
        async with self._session_factory() as session:
            # ``gated`` is the same dead end for the same reason, and its input
            # is a piece of configuration rather than an id. Installing fpcalc or
            # filling in ENRICHMENT_CONTACT would otherwise fix nothing already
            # recorded: 266 rows in one real library sat gated with no
            # next_attempt_at, and the binary they were waiting for was by then
            # sitting on disk. Whether a rung is ready is a live question, so ask
            # it rather than remembering the answer.
            for source in self.active_sources():
                if not self._providers[source].is_ready():
                    continue
                result = await session.execute(
                    update(EnrichmentState)
                    .where(
                        EnrichmentState.source == source,
                        EnrichmentState.state == "gated",
                        in_library(),
                    )
                    .values(state=STATE_PENDING, next_attempt_at=now, last_error=None)
                )
                count = int(result.rowcount or 0)
                if count:
                    logger.info(
                        "Re-opened %d %s row(s): the rung is configured now",
                        count,
                        source.value,
                    )
                rearmed += count

            for source in self.active_sources():
                column = _ARTIST_ID_COLUMN.get(source)
                if column is None:
                    continue
                identified = select(ArtistMetadata.artist_id).where(column.is_not(None))
                # The artist's own row waits on the same id its albums do. It has
                # an event path of its own — a provider deriving the id appends a
                # reopen — but that fires exactly once, at a moment that may have
                # passed before the code existed, and never at all when the id
                # was already in the snapshot. Asking the state closes the loop.
                count = 0
                for entity_type, answerable in (
                    (EnrichmentEntity.ARTIST, identified),
                    (
                        EnrichmentEntity.ALBUM,
                        select(Album.id).where(Album.artist_id.in_(identified)),
                    ),
                ):
                    result = await session.execute(
                        update(EnrichmentState)
                        .where(
                            EnrichmentState.entity_type == entity_type,
                            EnrichmentState.source == source,
                            EnrichmentState.state == "no_key",
                            EnrichmentState.entity_id.in_(answerable),
                            in_library(),
                        )
                        .values(
                            state=STATE_PENDING,
                            next_attempt_at=now,
                            # The reason is no longer true, and leaving it would
                            # keep the row on the review list claiming a human is
                            # needed when it is actually waiting for a tick.
                            last_error=None,
                        )
                    )
                    count += int(result.rowcount or 0)
                if count:
                    logger.info(
                        "Re-opened %d %s row(s) whose artist is now identified",
                        count,
                        source.value,
                    )
                rearmed += count

            rearmed += await self._rearm_barcoded(session, now)
            rearmed += await self._rearm_fingerprintable(session, now)
        return rearmed

    async def _rearm_barcoded(self, session: AsyncSession, now: datetime) -> int:
        """Re-open album rows that recorded "no barcode on this release".

        The fourth late arrival, and the one the ladder's own ordering creates.
        Only one sequence can be run, and two populations want opposite ones: a
        release AcoustID pinned wants MusicBrainz first (it holds the MBID), a
        release AcoustID could not pin wants Deezer first (it holds most
        barcodes). Whichever rung loses the coin toss refuses for want of a key
        the other is about to produce, and ``no_key`` stores
        ``next_attempt_at = NULL``, so losing it once used to mean losing it for
        good. Watching for the barcode is what turns the order into a question of
        *how many ticks*, rather than of what is reachable at all.

        The membership test is :func:`~app.enrich.matching.barcode_candidates`
        itself, run here in Python over a bounded set rather than approximated in
        SQL, and that is load-bearing twice over. It is the same function the
        providers call, so this pass cannot disagree with them about whether
        there is anything to search with — the "one comparison, two callers" rule
        that :mod:`app.core.quality` exists to state. And it is the only thing
        that keeps the pass convergent: a column being non-NULL is not the same
        claim as a *usable* barcode (``normalize_barcode`` rejects a ``"N/A"``
        somebody typed into a tag editor), and re-arming a row whose upstream
        will immediately answer ``no_key`` again is an infinite loop billed to
        the upstream.

        Deliberately **not** among the sources read here: ``claimed_barcode``.
        :func:`~app.enrich.matching.album_claimed_barcode` returns ``None`` the
        moment two files under one directory disagree, so a folder holding two
        barcodes has a ``FileClaim`` row and no usable candidate — exactly the
        non-convergent shape above. The claim still reaches the provider through
        the snapshot; it just cannot be what *wakes* a row.
        """
        sources = sorted(
            source for source in self.active_sources() if source in _BARCODE_SOURCES
        )
        if not sources:
            return 0

        stranded = (
            select(EnrichmentState.entity_id)
            .where(
                EnrichmentState.entity_type == EnrichmentEntity.ALBUM,
                EnrichmentState.source.in_(sources),
                EnrichmentState.state == STATE_NO_KEY,
                in_library(),
            )
            .distinct()
        )
        rows = (
            await session.execute(
                select(Album.id, Album.upc, AlbumMetadata.barcode)
                .outerjoin(AlbumMetadata, AlbumMetadata.album_id == Album.id)
                .where(Album.id.in_(stranded))
            )
        ).all()
        answerable = [
            str(album_id)
            for album_id, upc, barcode in rows
            if barcode_candidates(upc, barcode)
        ]
        if not answerable:
            return 0

        rearmed = 0
        for source in sources:
            result = await session.execute(
                update(EnrichmentState)
                .where(
                    EnrichmentState.entity_type == EnrichmentEntity.ALBUM,
                    EnrichmentState.source == source,
                    EnrichmentState.state == STATE_NO_KEY,
                    EnrichmentState.entity_id.in_(answerable),
                    in_library(),
                )
                .values(
                    state=STATE_PENDING,
                    next_attempt_at=now,
                    # The recorded reason ("no barcode on this release…") is no
                    # longer true, and leaving it would keep the row on the
                    # review list asking a person for something that has arrived.
                    last_error=None,
                )
            )
            count = int(result.rowcount or 0)
            if count:
                logger.info(
                    "Re-opened %d %s row(s) whose release now has a barcode",
                    count,
                    source.value,
                )
            rearmed += count
        return rearmed

    async def _rearm_fingerprintable(self, session: AsyncSession, now: datetime) -> int:
        """Re-open AcoustID rows that recorded "nothing on disk to fingerprint".

        A third input that arrives late, and the one that made the message read
        as a scope bug. AcoustID needs a *file*, and it finds files through track
        rows — which the download loop wrote and the disk scan, for a long time,
        did not. So every release Qobuzarr had adopted rather than fetched went
        ``no_key`` with a message about the disk, while its audio sat there
        perfectly readable, and ``no_key`` stores no ``next_attempt_at``.

        Now that a scan records what it finds, those rows are answerable — but
        only if something asks. Same state-based rule as the two passes above:
        does this album have a track with a path *now*. Not restricted to
        AcoustID by name — any source stranded for want of files is repaired by
        files appearing — but AcoustID is the only rung that reads them.
        """
        playable = select(Track.album_id).where(Track.path.is_not(None))
        result = await session.execute(
            update(EnrichmentState)
            .where(
                EnrichmentState.entity_type == EnrichmentEntity.ALBUM,
                EnrichmentState.source == EnrichmentSource.ACOUSTID,
                EnrichmentState.state == "no_key",
                EnrichmentState.entity_id.in_(playable),
                in_library(),
            )
            .values(state=STATE_PENDING, next_attempt_at=now, last_error=None)
        )
        count = int(result.rowcount or 0)
        if count:
            logger.info("Re-opened %d acoustid row(s): the files are visible now", count)
        return count

    # ----------------------------------------------------------------- claim
    async def _claim(
        self, batch: int, *, scope: Any | None = None
    ) -> list[EnrichmentJob]:
        """Take up to *batch* due in-scope rows and snapshot their subjects.

        Marks each row in-flight (``attempts`` bumped, ``last_attempt_at``
        stamped) and commits **before** any request goes out, so a crash mid-tick
        costs one attempt rather than replaying the batch forever.

        :func:`in_library` is applied here and not left to the seed and the
        purge, because this is the statement that spends requests. A stale row —
        one written before the scope narrowed, or by an album that was deleted
        from the library after it was queued — must cost nothing at all until
        :func:`purge_out_of_scope_enrichment` gets round to removing it.

        *scope* narrows the claim to one entity's rows, which is what
        :meth:`cascade` runs on. It is an extra ``WHERE``, never a replacement:
        every other condition — the active sources, due-ness, ``in_library`` —
        still applies, so a scoped claim can only ever be a subset of what the
        scheduled tick would have taken anyway. That is what makes running one
        inside a request safe: it spends no request the tick was not already
        entitled to spend, and it cannot reach a row the scope rule excludes.
        """
        sources = self.active_sources()
        if not sources:
            return []

        now = _now()
        jobs: list[EnrichmentJob] = []

        async with self._session_factory() as session:
            rows = (
                (
                    await session.execute(
                        select(EnrichmentState)
                        .where(
                            EnrichmentState.source.in_(sources),
                            EnrichmentState.next_attempt_at.is_not(None),
                            EnrichmentState.next_attempt_at <= now,
                            in_library(),
                            *([] if scope is None else [scope]),
                        )
                        .order_by(
                            EnrichmentState.priority.desc(),
                            # Artists before their albums, always. Both album
                            # browse paths — Deezer's discography listing and
                            # MusicBrainz's release-group browse — need the
                            # artist's id, and that is the only way a release
                            # with no barcode is ever matched. Leave this out and
                            # an artist's own row queues behind its two hundred
                            # albums, every one of which then fails for want of
                            # the very thing that row would have supplied.
                            _artist_first(),
                            EnrichmentState.next_attempt_at,
                        )
                        .limit(batch)
                    )
                )
                .scalars()
                .all()
            )
            if not rows:
                return []

            artist_ids = {
                row.entity_id for row in rows if row.entity_type is EnrichmentEntity.ARTIST
            }
            album_ids = {
                row.entity_id for row in rows if row.entity_type is EnrichmentEntity.ALBUM
            }
            artists = await self._snapshot_artists(session, artist_ids)
            albums = await self._snapshot_albums(session, album_ids)

            for row in rows:
                subject: ArtistSnapshot | AlbumSnapshot | None = (
                    artists.get(row.entity_id)
                    if row.entity_type is EnrichmentEntity.ARTIST
                    else albums.get(row.entity_id)
                )
                if subject is None:
                    # The entity was deleted between seeding and now. Drop the
                    # orphan here rather than waiting for nightly housekeeping.
                    await session.delete(row)
                    continue
                row.attempts += 1
                row.last_attempt_at = now
                # Park it far enough out that a crash cannot hot-loop; the real
                # next_attempt_at is written by _persist a moment later.
                row.next_attempt_at = now + _FAILED_INITIAL
                jobs.append(
                    EnrichmentJob(
                        entity_type=row.entity_type,
                        source=row.source,
                        entity_id=row.entity_id,
                        subject=subject,
                        match_key=row.match_key,
                        attempts=row.attempts,
                    )
                )
        return jobs

    async def _release(self, jobs: Iterable[EnrichmentJob]) -> None:
        """Make jobs the budget cut short due again immediately."""
        pending = list(jobs)
        if not pending:
            return
        now = _now()
        async with self._session_factory() as session:
            for job in pending:
                row = await session.get(
                    EnrichmentState, (job.entity_type, job.entity_id, job.source)
                )
                if row is not None:
                    row.attempts = max(0, row.attempts - 1)
                    row.next_attempt_at = now

    # ------------------------------------------------------------- snapshots
    async def _snapshot_artists(
        self, session: AsyncSession, ids: set[str]
    ) -> dict[str, ArtistSnapshot]:
        """Freeze the artists in *ids*, including what earlier rungs resolved."""
        if not ids:
            return {}
        artists = (
            (await session.execute(select(Artist).where(Artist.id.in_(ids))))
            .scalars()
            .all()
        )
        meta = await load_artist_metadata(session, ids)
        out: dict[str, ArtistSnapshot] = {}
        for artist in artists:
            found = meta.get(str(artist.id))
            out[str(artist.id)] = ArtistSnapshot(
                id=str(artist.id),
                name=artist.name or "",
                image_url=artist.image_url,
                albums_count=artist.albums_count or 0,
                isni=getattr(found, "isni", None),
                mb_artist_mbid=getattr(found, "mb_artist_mbid", None),
                deezer_artist_id=getattr(found, "deezer_artist_id", None),
                wikidata_qid=getattr(found, "wikidata_qid", None),
            )
        return out

    async def _snapshot_albums(
        self, session: AsyncSession, ids: set[str]
    ) -> dict[str, AlbumSnapshot]:
        """Freeze the albums in *ids*, with their tracks and prior enrichment."""
        if not ids:
            return {}
        albums = (
            (await session.execute(select(Album).where(Album.id.in_(ids))))
            .scalars()
            .all()
        )
        if not albums:
            return {}

        meta = await load_album_metadata(session, ids)
        artist_ids = {str(album.artist_id) for album in albums}
        artist_names = dict(
            (
                await session.execute(
                    select(Artist.id, Artist.name).where(Artist.id.in_(artist_ids))
                )
            ).all()
        )
        artist_meta = await load_artist_metadata(session, artist_ids)
        tracks = (
            (await session.execute(select(Track).where(Track.album_id.in_(ids))))
            .scalars()
            .all()
        )
        track_meta = await load_track_metadata(session, [str(t.id) for t in tracks])
        claims = await load_file_claims(session, [str(t.id) for t in tracks])
        # What the files say, kept in its own bucket all the way to the snapshot
        # so it can never be mistaken for the verified barcode loaded above.
        claimed: dict[str, list[str]] = {}
        for track in tracks:
            claim = claims.get(str(track.id))
            if claim is not None and claim.barcode:
                claimed.setdefault(str(track.album_id), []).append(claim.barcode)
        by_album: dict[str, list[TrackSnapshot]] = {}
        for track in sorted(tracks, key=lambda t: (t.media_number or 1, t.track_number or 0)):
            found = track_meta.get(str(track.id))
            by_album.setdefault(str(track.album_id), []).append(
                TrackSnapshot(
                    id=str(track.id),
                    title=track.title or "",
                    track_number=track.track_number or 0,
                    media_number=track.media_number or 1,
                    duration=track.duration,
                    isrc=track.isrc,
                    path=track.path,
                    mb_recording_mbid=getattr(found, "mb_recording_mbid", None),
                    acoustid=getattr(found, "acoustid", None),
                )
            )

        out: dict[str, AlbumSnapshot] = {}
        for album in albums:
            found = meta.get(str(album.id))
            artist_found = artist_meta.get(str(album.artist_id))
            out[str(album.id)] = AlbumSnapshot(
                id=str(album.id),
                artist_id=str(album.artist_id),
                artist_name=str(artist_names.get(album.artist_id) or ""),
                title=album.title or "",
                version=album.version,
                release_date=album.release_date,
                release_type=album.release_type or "album",
                tracks_count=album.tracks_count or 0,
                media_count=album.media_count or 1,
                upc=album.upc,
                label=album.label,
                genre=album.genre,
                tracks=tuple(by_album.get(str(album.id), ())),
                claimed_barcode=album_claimed_barcode(claimed.get(str(album.id), ())),
                barcode=getattr(found, "barcode", None),
                deezer_album_id=getattr(found, "deezer_album_id", None),
                deezer_artist_id=getattr(artist_found, "deezer_artist_id", None),
                mb_release_mbid=getattr(found, "mb_release_mbid", None),
                mb_release_group_mbid=getattr(found, "mb_release_group_mbid", None),
                mb_artist_mbid=getattr(artist_found, "mb_artist_mbid", None),
            )
        return out

    # --------------------------------------------------------------- persist
    async def _persist(
        self,
        outcomes: Sequence[tuple[EnrichmentJob, EnrichmentResult | BaseException]],
        result: EnrichResult,
    ) -> None:
        """Write every outcome in one transaction. No network happens in here."""
        if not outcomes:
            return

        now = _now()
        async with self._session_factory() as session:
            for job, outcome in outcomes:
                row = await session.get(
                    EnrichmentState, (job.entity_type, job.entity_id, job.source)
                )
                if row is None:  # pragma: no cover - deleted mid-tick
                    continue

                # A person can press Reject *while this lookup is in flight* —
                # the claim commits before the request goes out and the review
                # list is polling all the while, so the window is the whole
                # length of the tick. Every branch below then writes a state and
                # a next_attempt_at over the top, which puts the row back on the
                # list somebody just took it off, silently, seconds after they
                # were told it was gone. Only a person undoes a person (the rule
                # ``_MANUAL_OWNS`` already states about ids), so the outcome's
                # *knowledge* is applied and its *schedule* is not.
                dismissed = (
                    (row.state, row.last_error)
                    if row.state == STATE_REJECTED
                    else None
                )

                if isinstance(outcome, EnrichmentResult):
                    await self._apply(session, job, outcome)
                    row.state = STATE_OK
                    row.last_error = None
                    row.match_key = outcome.match_key or row.match_key
                    row.next_attempt_at = now + timedelta(
                        days=self._settings.enrichment_refresh_days
                    )
                    result.matched += 1
                    for entity_type, entity_id, source in outcome.reopen:
                        await self._reopen(session, entity_type, entity_id, source, now)
                    if outcome.note:
                        session.add(
                            Activity(
                                level=ActivityLevel.INFO,
                                event="enrichment.matched",
                                message=outcome.note,
                                artist_id=self._artist_id_for(job),
                                album_id=self._album_id_for(job),
                            )
                        )

                elif isinstance(outcome, EnrichmentOutcome):
                    # What the rung established before it said "no" is still
                    # true. AcoustID rejecting a release it could not decode has
                    # proved the files are broken, and that verdict is the whole
                    # point of the rung — see EnrichmentOutcome.partial.
                    if isinstance(outcome.partial, EnrichmentResult):
                        await self._apply(session, job, outcome.partial)
                    row.state = outcome.state
                    row.last_error = outcome.message or None
                    row.next_attempt_at = self._next_attempt(
                        outcome.state, row.attempts, now
                    )
                    if isinstance(outcome, SourceGated):
                        result.gated += 1
                    else:
                        result.unmatched += 1

                else:
                    message = str(outcome) or type(outcome).__name__
                    row.state = STATE_FAILED
                    row.last_error = message[:500]
                    row.next_attempt_at = self._next_attempt(
                        STATE_FAILED, row.attempts, now
                    )
                    result.failed += 1

                if dismissed is not None:
                    # Put the dismissal back exactly as it was written, including
                    # the person's own reason where the matcher's used to be.
                    row.state, row.last_error = dismissed
                    row.next_attempt_at = None

                result.by_source[job.source.value] = (
                    result.by_source.get(job.source.value, 0) + 1
                )

    async def _write_back(
        self,
        outcomes: Sequence[tuple[EnrichmentJob, EnrichmentResult | BaseException]],
    ) -> int:
        """Put what a tick learned into the files it describes.

        Scoping enrichment to the library closed a loop that used to be open by
        accident. A release is only enrichable once it is ``DOWNLOADED``, and the
        download loop resolves its tags *before* the first file is written — so
        for a first download the ids arrive strictly after the only thing that
        would have written them, and nothing else ever revisits the files. The
        result was a library that knew every MusicBrainz id in ``album_metadata``
        and put none of them where Picard, beets or Roon would look. Enrichment
        that never reaches the files is enrichment nobody asked for.

        Two rules keep this from being destructive:

        * **Only releases this program downloaded are re-tagged.** An album the
          disk scan adopted arrived with somebody else's tags — possibly hand-made
          — and those are not ours to overwrite. It still gets its NFO, which is
          merged rather than replaced. The test is
          ``Track.origin is TrackOrigin.DOWNLOAD``, not the mere existence of a
          track row: the scan writes rows too, so "has tracks" stopped meaning
          "Qobuzarr fetched this" and reading it that way would have quietly
          turned this gate into a licence to rewrite the whole library's tags.
        * **``Album.pin_tags`` is the per-release version of the same sentence**,
          said by a person rather than inferred from provenance. A release Qobuzarr
          downloaded is ours to re-tag by default, and pinning is how somebody who
          has since curated it says otherwise. It gates the *tags* only: the NFO is
          still written, because an NFO is merged element by element rather than
          overwritten, so nothing a person put in one is at risk. It also gates
          only this background pass — the explicit re-tag buttons are a different
          decision, made while looking at the release.
        * **The NFO half obeys ``nfo_enabled`` and ``<lockdata>``** exactly as it
          does everywhere else, because that is how a person tells a media server,
          and therefore us, to stop editing a file.

        Runs after phase 3 has committed, in its own session, and swallows its
        failures: a library write that goes wrong must not fail the enrichment
        that succeeded. Returns how many releases were touched.
        """
        if not self._settings.enrichment_write_back:
            return 0

        album_ids = {
            job.entity_id
            for job, outcome in outcomes
            if job.entity_type is EnrichmentEntity.ALBUM
            and isinstance(outcome, EnrichmentResult)
        }
        if not album_ids:
            return 0

        from app.core.librarian import retag_album, write_nfo  # noqa: PLC0415 - cycle

        written = 0
        async with self._session_factory() as session:
            for album_id in sorted(album_ids):
                album = await session.get(Album, str(album_id))
                if album is None or album.status is not AlbumStatus.DOWNLOADED:
                    continue
                if not album.path:
                    continue
                try:
                    ours = bool(
                        (
                            await session.execute(
                                select(func.count(Track.id)).where(
                                    Track.album_id == str(album.id),
                                    Track.origin == TrackOrigin.DOWNLOAD,
                                )
                            )
                        ).scalar_one()
                    )
                    touched = False
                    if ours and not album.pin_tags:
                        retagged = await retag_album(
                            session, album, settings=self._settings
                        )
                        touched = retagged.tagged > 0
                    nfo = await write_nfo(session, album, settings=self._settings)
                    touched = touched or nfo.album_written or nfo.artist_written
                    if touched:
                        written += 1
                except Exception:  # noqa: BLE001 - a bad write must not fail the tick
                    logger.exception(
                        "Writing enrichment back to %s failed", album_id
                    )
            await session.commit()
        return written

    async def _apply(
        self, session: AsyncSession, job: EnrichmentJob, outcome: EnrichmentResult
    ) -> None:
        """Merge one result into the three metadata tables.

        An absent key means "no opinion" and leaves the stored value alone; a key
        present with ``None`` means "known empty" and clears it. That is the same
        distinction ``Indexer._apply_metadata`` draws, and it is what stops a
        sparse answer from wiping a richer one.

        Note what is *not* here: nothing writes to ``artists``, ``albums`` or
        ``tracks``. The single exception, ``Album.release_type``, goes through
        :mod:`app.enrich.merge` on a majority vote and is applied by its own
        step — never as a side effect of one source answering.
        """
        if outcome.artist_fields:
            artist_id = self._artist_id_for(job)
            if artist_id:
                await self._upsert(
                    session, ArtistMetadata, {"artist_id": artist_id}, outcome.artist_fields
                )
        album_meta: AlbumMetadata | None = None
        if job.entity_type is EnrichmentEntity.ALBUM and (
            outcome.album_fields or outcome.opinions
        ):
            album_meta = await self._upsert(
                session, AlbumMetadata, {"album_id": job.entity_id}, outcome.album_fields
            )
        for track_id, fields in outcome.track_fields.items():
            await self._upsert(session, TrackMetadata, {"track_id": track_id}, fields)
        if outcome.opinions and album_meta is not None:
            await self._vote(session, job, album_meta, outcome.opinions)

    async def _vote(
        self,
        session: AsyncSession,
        job: EnrichmentJob,
        meta: AlbumMetadata,
        opinions: Mapping[str, Any],
    ) -> None:
        """Record this source's opinions and act if a majority has now formed.

        Votes accumulate in ``album_metadata.consensus_json`` because the sources
        answer in different ticks — Deezer today, MusicBrainz in ten minutes — so
        there is never a moment when they are all in hand at once. Each new
        opinion is merged in and the whole field is re-counted.

        Qobuz always gets a vote, and it is always its **original** value: once a
        write-back has happened, ``qobuz_release_type`` holds what Qobuz actually
        said. Voting with the applied value instead would make the result
        self-reinforcing — one early majority would look unanimous forever.
        """
        album = await session.get(Album, job.entity_id)
        if album is None:  # pragma: no cover - claimed a moment ago
            return

        stored = decode_consensus(meta.consensus_json)
        votes: dict[str, dict[str, Any]] = {
            name: dict(entry.get("votes") or {}) for name, entry in stored.items()
        }
        for name, value in opinions.items():
            if value is not None:
                votes.setdefault(name, {})[job.source.value] = value

        qobuz_release_type = meta.qobuz_release_type or album.release_type
        if "release_type" in votes:
            votes["release_type"]["qobuz"] = release_type_from_qobuz(qobuz_release_type)

        threshold = float(self._settings.enrichment_consensus_threshold)
        results = [
            consensus(name, cast, threshold=threshold) for name, cast in votes.items()
        ]
        meta.consensus_json = encode_consensus(results)

        for result in results:
            if result.field_name == "release_type":
                meta.suggested_release_type = result.value
                await self._apply_release_type(session, album, meta, result)

    async def _apply_release_type(
        self,
        session: AsyncSession,
        album: Album,
        meta: AlbumMetadata,
        verdict: ConsensusResult,
    ) -> None:
        """Write a majority release type onto the album, and nothing else.

        The blast radius is deliberately one column. ``Album.release_type`` feeds
        ``Artist.accepts()`` and therefore what *future* indexing marks wanted —
        but this never touches ``Album.status``, so reclassifying an existing
        release can neither create a download nor cancel one. That is the same
        asymmetry the artist page already documents for widening
        ``accepted_release_types``.

        ``qobuz_release_type`` is captured on the way past, so the change is
        reversible and Qobuz's vote stays honest.
        """
        if not self._settings.enrichment_apply_release_type:
            return
        if not verdict.decided or verdict.value == album.release_type:
            return

        previous = self._write_release_type(album, meta, verdict.value)
        session.add(
            Activity(
                level=ActivityLevel.INFO,
                event="enrichment.release_type",
                message=(
                    f"{album.title}: release type {previous} -> {verdict.value} "
                    f"({verdict.agreed}/{verdict.total} sources agreed)"
                ),
                artist_id=str(album.artist_id),
                album_id=str(album.id),
            )
        )

    @staticmethod
    def _write_release_type(album: Album, meta: AlbumMetadata, value: str) -> str:
        """Move ``Album.release_type`` to *value*, capturing what it was.

        The three statements that make the change reversible, in one place
        because there are now two callers with different licences to make it: a
        majority verdict (:meth:`_apply_release_type`, gated on the setting and
        on the vote) and a person pressing Accept (:meth:`accept`, gated on
        neither, because a person deciding is the thing consensus is a
        substitute for).

        ``qobuz_release_type`` is only ever written once — the *first* time the
        column moves. Re-capturing on a second change would make the reversal
        point the previous enriched guess rather than what Qobuz actually said,
        and Qobuz's vote in :func:`app.enrich.merge.consensus` reads the same
        field, so one early majority would start looking unanimous forever.

        Returns the previous value, for the activity line.
        """
        previous = album.release_type
        if meta.qobuz_release_type is None:
            meta.qobuz_release_type = previous
        album.release_type = value
        meta.release_type_applied_at = _now()
        return previous

    @staticmethod
    async def _upsert(
        session: AsyncSession,
        model: type[Any],
        key: Mapping[str, Any],
        fields: Mapping[str, Any],
        *,
        force: bool = False,
    ) -> Any:
        """Insert or update one metadata row, ignoring unknown column names.

        Returns the row, because a freshly added one is not in the identity map
        until the session flushes — a later ``session.get`` for the same key
        would come back empty and the caller would silently skip its work.

        Ids a person supplied by hand are not overwritten (see
        :data:`_MANUAL_OWNS`). *force* is for :meth:`identify` itself: a second
        manual identification is a person correcting the first, and only a
        person may overrule a person.
        """
        pk = tuple(key.values())
        row = await session.get(model, pk if len(pk) > 1 else pk[0])
        if row is None:
            row = model(**key)
            session.add(row)
            # Flush immediately, not at commit. Two sources routinely answer for
            # the same album inside one persist phase, and until this row reaches
            # the identity map the second one's ``session.get`` comes back empty
            # and inserts a duplicate primary key.
            await session.flush()
        protected = frozenset() if force else _manually_identified_fields(row)
        for name, value in fields.items():
            if name in protected or not hasattr(row, name):
                continue
            setattr(row, name, value)
        return row

    @staticmethod
    def _cascade_scope(entity_type: EnrichmentEntity, entity_id: str) -> Any:
        """The rows a manual identification of this entity can unblock.

        Both directions of the one relationship that carries keys, because both
        are how a single answer stops being a single answer:

        * Identifying an **artist** unblocks *their albums*. An album with no
          barcode is only ever matched by browsing its artist's discography, so
          every one of them was sitting on "no artist to browse" — which is the
          whole reason somebody typed the id in.
        * Identifying an **album** unblocks *its artist*. Identity propagates
          upward: a release's sole clean artist credit is where the artist's own
          id is derived from, and that derivation is what the artist's other
          albums are then waiting on.

        Deliberately one hop, not a transitive closure. Two hops from an album
        is the artist's entire discography, which is a tick's job and not a
        request's — and the rows it would have reached are due anyway.
        """
        if entity_type is EnrichmentEntity.ARTIST:
            return or_(
                and_(
                    EnrichmentState.entity_type == EnrichmentEntity.ARTIST,
                    EnrichmentState.entity_id == entity_id,
                ),
                and_(
                    EnrichmentState.entity_type == EnrichmentEntity.ALBUM,
                    EnrichmentState.entity_id.in_(
                        select(Album.id).where(Album.artist_id == entity_id)
                    ),
                ),
            )
        return or_(
            and_(
                EnrichmentState.entity_type == EnrichmentEntity.ALBUM,
                EnrichmentState.entity_id == entity_id,
            ),
            and_(
                EnrichmentState.entity_type == EnrichmentEntity.ARTIST,
                EnrichmentState.entity_id.in_(
                    select(Album.artist_id).where(Album.id == entity_id)
                ),
            ),
        )

    async def cascade(
        self,
        entity_type: EnrichmentEntity,
        entity_id: str,
        *,
        budget: float | None = None,
        max_rounds: int = _CASCADE_MAX_ROUNDS,
    ) -> CascadeResult:
        """Run every rung a manual identification just unblocked, right now.

        The waterfall the ladder already describes, drained for one entity while
        somebody is still looking at the screen. Typing an id used to buy a
        single row moving to ``pending`` and a message saying "on the next
        pass"; what actually followed was one rung per five-minute tick, because
        each tick claims its batch before the previous tick's discoveries are
        written. A manual MusicBrainz match therefore took one tick to fetch the
        artist, a second for the Wikidata QID it uncovered, and a third for the
        albums the artist id unblocked — a quarter of an hour of a person
        wondering whether the thing they typed had worked, and, worse, being
        shown review rows in the meantime that their answer had already settled.

        Same four phases as :meth:`_run_once` and for the same reasons — claim
        commits before any request goes out, no session is held across HTTP,
        persist and write-back are their own transactions — just scoped and
        looped. Each round re-runs :meth:`_rearm_stranded` first, because that
        is what notices the id has landed: the albums waiting on their artist and
        the Wikidata row waiting on its QID are both repaired by asking the
        state, not by catching the event.

        Three things bound it, and all three are honest about stopping:

        * the lock, so a cascade and a scheduled tick never drain together;
        * *budget* seconds and *max_rounds*, so a request cannot run long;
        * running out of work, which is the ordinary ending.

        Nothing is lost at any of them. Every row is either finished or due, and
        due rows are what the scheduler was always going to pick up.
        """
        outcome = CascadeResult()
        if not self.enabled:
            return outcome

        try:
            await asyncio.wait_for(self._lock.acquire(), timeout=_CASCADE_LOCK_WAIT)
        except TimeoutError:
            # A tick is draining. It will claim these rows itself — they are due
            # — so there is nothing to do and nothing to report but the fact.
            logger.debug(
                "Cascade for %s %s skipped: a tick holds the lock",
                entity_type.value,
                entity_id,
            )
            outcome.skipped = True
            return outcome

        started = time.monotonic()
        ceiling = _CASCADE_BUDGET if budget is None else budget
        scope = self._cascade_scope(entity_type, entity_id)
        batch = max(1, int(self._settings.enrichment_batch_size))
        try:
            await self._apply_pending_ladder()
            for _ in range(max(1, max_rounds)):
                if time.monotonic() - started > ceiling:
                    outcome.exhausted = True
                    break

                await self._rearm_stranded()
                jobs = await self._claim(batch, scope=scope)
                if not jobs:
                    break

                outcome.rounds += 1
                results: list[tuple[EnrichmentJob, EnrichmentResult | BaseException]] = []
                for index, job in enumerate(jobs):
                    if time.monotonic() - started > ceiling:
                        outcome.exhausted = True
                        await self._release(jobs[index:])
                        break
                    results.append((job, await self._run_job(job)))

                if results:
                    round_result = EnrichResult()
                    await self._persist(results, round_result)
                    outcome.ran += len(results)
                    outcome.written_back += await self._write_back(results)
                if outcome.exhausted:
                    break
            else:
                # Fell out on the round cap rather than on running dry, so there
                # may still be answerable work. Say so rather than implying the
                # waterfall reached the bottom.
                outcome.exhausted = True
        finally:
            self._lock.release()

        logger.info(
            "Cascade for %s %s: %d round(s), %d source(s) fetched, %d file(s) re-tagged%s",
            entity_type.value,
            entity_id,
            outcome.rounds,
            outcome.ran,
            outcome.written_back,
            " (stopped early)" if outcome.exhausted else "",
        )
        return outcome

    async def _reopen(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_id: str,
        source: EnrichmentSource,
        now: datetime,
        priority: int = 0,
    ) -> None:
        """Make another (entity, source) due again because its inputs changed."""
        row = await session.get(EnrichmentState, (entity_type, entity_id, source))
        if row is None:
            session.add(
                EnrichmentState(
                    entity_type=entity_type,
                    entity_id=entity_id,
                    source=source,
                    state=STATE_PENDING,
                    priority=priority,
                    next_attempt_at=now,
                )
            )
            return
        row.state = STATE_PENDING
        row.attempts = 0
        row.last_error = None
        row.priority = max(row.priority, priority)
        row.next_attempt_at = now

    async def mark_due(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_ids: Iterable[str],
        *,
        priority: int = 10,
    ) -> int:
        """Queue in-scope entities for enrichment right now, at the front.

        This is how "enrich on the way in" is honoured — where "in" now means
        *into the library* rather than into the catalogue. The callers are the
        moments an album lands on disk (see :func:`mark_library_due`) and the
        indexer, which re-visits releases it already knows about; both call it
        from inside their own write transaction, and **no request is made here**.
        Making one would mean holding that transaction open across a third-party
        call, which is exactly the thing that stalls the download worker into
        marking a live download failed. The lookups happen a moment later, from
        the enricher's own tick, against these rows.

        Creates the state row when it does not exist yet, which is what makes a
        release that has just landed enrichable immediately rather than at the
        next seed. Ids outside :func:`library_scope` are dropped, so the indexer
        calling this for a whole discography queues only the part of it that is
        actually on disk — and no caller has to know the scope rule to be safe.

        Only ``pending``, ``not_found`` and ``failed`` rows are moved forward. An
        ``ok`` row has nothing to learn, and ``ambiguous``/``no_key``/``gated``
        are waiting on a human or on configuration, not on a retry.
        """
        sources = self.active_sources()
        ids = [str(value) for value in entity_ids if value]
        if not sources or not ids:
            return 0

        # The scope test is SQL and these sessions do not autoflush, so without
        # this the status the caller has just written — the very thing that puts
        # the album in scope — is still sitting in the identity map, and every
        # album that has just landed reads as out of scope.
        await session.flush()
        column = (
            Album.id if entity_type is EnrichmentEntity.ALBUM else Album.artist_id
        )
        found = (
            (await session.execute(library_scope(entity_type).where(column.in_(ids))))
            .scalars()
            .all()
        )
        in_scope = {str(value) for value in found}
        ids = [entity_id for entity_id in ids if entity_id in in_scope]
        if not ids:
            return 0

        now = _now()
        movable = {STATE_PENDING, "not_found", STATE_FAILED}
        touched = 0
        for source in sources:
            if not self._providers[source].supports(entity_type):
                continue
            for entity_id in ids:
                row = await session.get(
                    EnrichmentState, (entity_type, entity_id, source)
                )
                if row is None:
                    session.add(
                        EnrichmentState(
                            entity_type=entity_type,
                            entity_id=entity_id,
                            source=source,
                            state=STATE_PENDING,
                            priority=priority,
                            next_attempt_at=now,
                        )
                    )
                    touched += 1
                elif row.state in movable:
                    row.priority = max(row.priority, priority)
                    row.next_attempt_at = now
                    touched += 1
        return touched

    async def reopen(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_id: str,
        *,
        sources: Iterable[EnrichmentSource] | None = None,
        priority: int = 20,
    ) -> int:
        """Public re-open, used by the "identify this" and "run now" actions.

        This is also how the two terminal states escape: ``no_key`` and ``gated``
        store no ``next_attempt_at`` at all, so nothing but a change of inputs —
        a pasted MBID, a filled-in contact address — brings them back.

        Re-opened rows outrank both the background backlog and the indexer's
        on-the-way-in queueing, because this only ever happens when a person
        asked for it and is waiting to see the result.
        """
        now = _now()
        if sources is not None:
            # Named explicitly, so honour it: this is a person saying "it is this
            # one", and the id is worth recording even for a rung that happens to
            # be switched off right now.
            wanted = list(sources)
        else:
            # Defaulting to every provider, so filter by what each one handles.
            # Cover art is looked up per release and Wikidata per artist; a row
            # for the pairing a provider does not serve would hand it the wrong
            # kind of subject, and a provider given the wrong subject raises.
            wanted = [
                source
                for source, provider in self._providers.items()
                if provider.supports(entity_type)
            ]
        for source in wanted:
            await self._reopen(session, entity_type, entity_id, source, now, priority)
        return len(wanted)

    async def candidates(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_id: str,
        source: EnrichmentSource,
        query: str | None = None,
    ) -> list[Candidate]:
        """Upstream records a person could pick as the match for this entity.

        The other half of :meth:`identify`. On its own, identify assumed the user
        already had ``984f8239-8fe1-4683-9c54-10ffb14439e9`` to hand — and the
        people who have that are the people who did not need the review list. So
        this searches on their behalf and hands back cards to read.

        Read-only, and the "never hold a session across a network call" rule is
        satisfied for the reason that rule exists: it is about the *write* lock
        SQLite hands to one transaction at a time, and nothing here writes. The
        snapshot is still taken first, because a provider that touched the ORM
        would be the thing the snapshots exist to prevent.

        Raises:
            ValueError: the source has no records a person could search.
            LookupError: the artist or album is not in the database any more.
        """
        if source not in _IDENTIFIABLE_SOURCES:
            raise ValueError(
                f"{source.value} has no records of its own to search — it is "
                "keyed by an id another source supplies. Identify this on "
                "MusicBrainz or Deezer instead."
            )
        provider = self._providers.get(source)
        if not isinstance(provider, SearchableProvider):
            raise ValueError(f"{source.value} is not configured.")

        entity_id = str(entity_id)
        if entity_type is EnrichmentEntity.ARTIST:
            found = await self._snapshot_artists(session, {entity_id})
        else:
            found = await self._snapshot_albums(session, {entity_id})
        subject = found.get(entity_id)
        if subject is None:
            raise LookupError(
                f"{entity_type.value} {entity_id} is not in the library any more."
            )

        job = EnrichmentJob(
            entity_type=entity_type,
            source=source,
            entity_id=entity_id,
            subject=subject,
        )
        return await provider.candidates(job, query)

    async def identify(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_id: str,
        source: EnrichmentSource,
        external_id: str,
        cascade: bool = True,
    ) -> str:
        """Record an identifier a person supplied, and re-open the entity.

        This is the escape hatch the "exact or nothing" rule requires. The
        matchers refuse to guess and everything they refuse on ends up on the
        review list; without a way for someone to say "it is *this* one", that
        list would only ever grow.

        A human deciding is not fuzzy matching — it is the same doctrine the bulk
        importer already follows. What is stored is marked ``manual``, which is
        what protects it from being cleared later by an automatic derivation that
        disagrees.

        Then, unless *cascade* is off, :meth:`cascade` drains everything that
        answer just unblocked before this returns. One manual match is rarely
        one answer: an artist's id is what their barcode-less albums are all
        waiting on, and MusicBrainz's URL relations are where Wikidata's QID
        comes from. Leaving that to the scheduler meant a person answered one
        question and was shown the consequences of their own answer as further
        questions, five minutes apart, for a quarter of an hour. *cascade* is a
        parameter rather than always-on so that a caller writing several ids in
        one go can drain once at the end instead of once per id.

        Returns a message describing what was recorded, and what the cascade
        managed while the caller waited.

        Raises:
            ValueError: the identifier is not well formed for that source, or
                that source has nothing a person can supply.
            LookupError: the artist or album is not in the database any more.
        """
        from app.enrich.matching import normalize_mbid  # noqa: PLC0415 - one use

        value = str(external_id or "").strip()
        if not value:
            raise ValueError("No identifier supplied.")

        if source not in _IDENTIFIABLE_SOURCES:
            # The other rungs are keyed by what an earlier rung established — the
            # Cover Art Archive by MBID, Wikidata by the QID MusicBrainz
            # published — so there is no id of their own for a person to type,
            # and writing one somewhere would only corrupt a column that means
            # something else.
            raise ValueError(
                f"{source.value} is looked up by an id another source supplies, "
                "so there is nothing to identify it by. Identify it on "
                "MusicBrainz or Deezer instead."
            )

        # Before anything is written: unfollowing an artist leaves its review
        # rows behind, and an upsert against a row that is gone fails on the
        # foreign key with a 500 that shows the user a raw INSERT statement.
        subject = await session.get(
            Artist if entity_type is EnrichmentEntity.ARTIST else Album, str(entity_id)
        )
        if subject is None:
            raise LookupError(
                f"{entity_type.value} {entity_id} is not in the library any more."
            )

        # A pasted browser URL is the commonest thing anyone has to hand, so it
        # is accepted as an identifier rather than rejected as a typo. Doing this
        # before validation means the error messages below only ever fire on
        # something that really is not an id.
        value = _id_from_url(value, source)

        if source is EnrichmentSource.MUSICBRAINZ:
            canonical = normalize_mbid(value)
            if canonical is None:
                raise ValueError(
                    f"{value!r} is not a MusicBrainz id. They look like "
                    "984f8239-8fe1-4683-9c54-10ffb14439e9."
                )
            value = canonical
        elif not value.isdigit():
            raise ValueError(
                f"{value!r} is not a Deezer id. They are plain numbers, like "
                "12195560 — the last part of a deezer.com link."
            )

        if entity_type is EnrichmentEntity.ARTIST:
            field_name = (
                "mb_artist_mbid"
                if source is EnrichmentSource.MUSICBRAINZ
                else "deezer_artist_id"
            )
            method_field = (
                "mb_match_method"
                if source is EnrichmentSource.MUSICBRAINZ
                else "deezer_match_method"
            )
            fields: dict[str, Any] = {field_name: value, method_field: "manual"}
            if source is EnrichmentSource.MUSICBRAINZ:
                # Evidence a derivation can never reach, so a later contradiction
                # loses to the person who typed this in.
                fields["mb_match_evidence"] = 999
            await self._upsert(
                session, ArtistMetadata, {"artist_id": entity_id}, fields, force=True
            )
        else:
            field_name = (
                "mb_release_mbid"
                if source is EnrichmentSource.MUSICBRAINZ
                else "deezer_album_id"
            )
            method_field = (
                "mb_match_method"
                if source is EnrichmentSource.MUSICBRAINZ
                else "deezer_match_method"
            )
            await self._upsert(
                session,
                AlbumMetadata,
                {"album_id": entity_id},
                {field_name: value, method_field: "manual"},
                force=True,
            )

        await self.reopen(session, entity_type, entity_id, sources=[source])
        session.add(
            Activity(
                level=ActivityLevel.INFO,
                event="enrichment.identified",
                message=f"{entity_type.value} {entity_id} identified on {source.value} as {value}",
                artist_id=entity_id if entity_type is EnrichmentEntity.ARTIST else None,
                album_id=entity_id if entity_type is EnrichmentEntity.ALBUM else None,
            )
        )
        # Committed here rather than by the caller. Both HTTP entry points run on
        # the request-scoped session, which never commits, and a rolled-back
        # identification is worse than none at all: the user is told it was
        # recorded and the row stays exactly where it was on the review list.
        await session.commit()

        recorded = f"Recorded {source.value} id {value}."
        if not cascade:
            return f"{recorded} It will be fetched on the next pass."

        # After the commit, never before: the cascade opens its own sessions and
        # reads back the id that was just written. Running it inside this
        # transaction would hand it a row that does not exist yet — and hold a
        # SQLite write transaction open across a MusicBrainz call, which is the
        # thing the whole three-phase design exists to prevent.
        outcome = await self.cascade(entity_type, entity_id)
        if outcome.skipped:
            return f"{recorded} A pass is already running; it will be picked up there."
        if not outcome.ran:
            return f"{recorded} It will be fetched on the next pass."
        fetched = (
            f"{recorded} Fetched {outcome.ran} source"
            f"{'' if outcome.ran == 1 else 's'} straight away"
        )
        if outcome.written_back:
            fetched += (
                f" and re-tagged {outcome.written_back} file"
                f"{'' if outcome.written_back == 1 else 's'}"
            )
        return fetched + (
            "; the rest follows on the next pass." if outcome.exhausted else "."
        )

    # --------------------------------------------------------------- reject
    async def reject(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_id: str,
        source: EnrichmentSource,
        *,
        reason: str | None = None,
    ) -> str:
        """Record that a person looked at this row and said no.

        The other half of :meth:`identify`, and the half the review list could
        not do without. "Exact or nothing" guarantees the list only ever grows
        by itself; identify is one way off it and this is the other. Some rows
        genuinely have no answer — a self-released record MusicBrainz has never
        held, a Qobuz-exclusive edition of something Deezer lists once. Leaving
        them on the list forever teaches people to stop reading it, which costs
        the rows that *do* need deciding.

        What it writes is deliberately minimal: the state, no ``next_attempt_at``
        at all, and the person's reason where the matcher's reason used to be.
        Nothing in the metadata tables is touched, because a rejection is not a
        claim about the release — it is a claim about the *work item*.

        Restricted to the states the review list actually shows
        (:data:`REVIEW_STATES`). Rejecting a ``pending`` row would be rejecting
        something nobody has looked at yet; rejecting an ``ok`` row would leave
        the identifiers it wrote in place while marking the row closed, which
        says something false about both. A row that is already ``rejected`` is
        accepted and changes nothing, so a double-press is not an error.

        Reversible by :meth:`reopen`, which sets ``pending`` unconditionally.
        That is the only way back, and it is why nothing here is destructive.

        Returns a message describing what was recorded.

        Raises:
            LookupError: there is no such work item.
            ValueError: its state is not one a person is being asked about.
        """
        entity_id = str(entity_id)
        row = await session.get(EnrichmentState, (entity_type, entity_id, source))
        if row is None:
            # The review list outlives the thing it is about — unfollowing an
            # artist leaves rows behind, and the nightly purge removes them a
            # night later. 404 is the honest answer for a row that is gone.
            raise LookupError(
                f"There is no {source.value} work item for {entity_type.value} "
                f"{entity_id} any more."
            )

        if row.state == STATE_REJECTED:
            return (
                f"{source.value} was already rejected for this "
                f"{entity_type.value}. Nothing changed."
            )
        if row.state not in REVIEW_STATES:
            raise ValueError(
                f"Only rows waiting on a person can be rejected, and this one is "
                f"{row.state!r}. Rejectable states: " + ", ".join(REVIEW_STATES) + "."
            )

        note = (reason or "").strip() or None
        row.state = STATE_REJECTED
        # No ladder entry, on purpose. _next_attempt would return None for this
        # state anyway; writing it here means the row is inert the instant it is
        # committed rather than at the end of whatever tick touches it next.
        row.next_attempt_at = None
        row.last_error = note

        session.add(
            Activity(
                level=ActivityLevel.INFO,
                event="enrichment.rejected",
                message=(
                    f"{entity_type.value} {entity_id} rejected on {source.value}"
                    + (f": {note}" if note else "")
                ),
                artist_id=entity_id if entity_type is EnrichmentEntity.ARTIST else None,
                album_id=entity_id if entity_type is EnrichmentEntity.ALBUM else None,
            )
        )
        # Committed here for exactly the reason :meth:`identify` is: both run on
        # the request-scoped session, which never commits, and a rolled-back
        # rejection is a green toast over a row that is still there.
        await session.commit()
        return (
            f"Rejected {source.value} for this {entity_type.value}. It will not be "
            "asked about again until you run it again."
        )

    # --------------------------------------------------------------- accept
    async def accept(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_id: str,
    ) -> AcceptOutcome:
        """Apply the proposal this entity already carries, if it carries one.

        Read :class:`AcceptOutcome` before changing this. The short version:
        **there is no search here and there must never be one.** Accept applies
        something the system already decided and stored; where it has stored
        nothing, it says so and the client opens the identify picker. Adding
        "and if all else fails, take the best search hit" would make this the
        one place in the codebase that selects a candidate by name.

        The single held proposal is ``suggested_release_type`` — the majority
        verdict of :func:`app.enrich.merge.consensus`, which is written whether
        or not ``ENRICHMENT_APPLY_RELEASE_TYPE`` allows it to be applied. That
        setting is what makes Accept useful: with it off, every disagreement is
        recorded and none is acted on, and this button is how a person acts on
        one without turning the automatic behaviour on for the whole library.

        Applying it is one column, exactly as for the automatic path: it never
        touches ``Album.status``, so reclassifying can neither create a download
        nor cancel one, and ``qobuz_release_type`` holds the reversal.

        It deliberately does **not** touch the ``enrichment_state`` row. The
        release type and the match are different questions — accepting that a
        record is an EP says nothing about which MusicBrainz release it is — and
        silently resolving an ambiguity somebody has not resolved is the failure
        this whole layer exists to avoid.

        Raises:
            LookupError: the artist or album is not in the database any more.
            ValueError: the stored suggestion is not a release type this
                application knows, so applying it would write a value nothing
                downstream can read.
        """
        entity_id = str(entity_id)
        album: Album | None = None
        if entity_type is EnrichmentEntity.ALBUM:
            album = await session.get(Album, entity_id)
            missing = album is None
        else:
            missing = await session.get(Artist, entity_id) is None
        if missing:
            raise LookupError(
                f"{entity_type.value} {entity_id} is not in the library any more."
            )

        already: str | None = None
        if album is not None:
            meta = await session.get(AlbumMetadata, entity_id)
            suggested = (getattr(meta, "suggested_release_type", None) or "").strip()
            if suggested and suggested == album.release_type:
                # A suggestion the album is already carrying. Nothing to apply,
                # but "no source has offered a value" — the other empty-handed
                # branch — would be a false statement about the same row, and
                # this layer's whole argument is that it says what it did.
                already = suggested
            if suggested and suggested != album.release_type:
                if suggested not in RELEASE_TYPES:
                    # A vocabulary drift rather than a user error, but the user
                    # is the one holding the button, so refuse loudly instead of
                    # writing a release type that ``Artist.accepts()`` and every
                    # naming template would then have to guess at.
                    raise ValueError(
                        f"{suggested!r} is not a release type this version knows. "
                        "Known types: " + ", ".join(RELEASE_TYPES) + "."
                    )
                # ``meta`` cannot be None here: ``suggested`` was read off it.
                previous = self._write_release_type(album, meta, suggested)
                session.add(
                    Activity(
                        level=ActivityLevel.INFO,
                        event="enrichment.release_type",
                        message=(
                            f"{album.title}: release type {previous} -> {suggested} "
                            "(accepted by hand)"
                        ),
                        artist_id=str(album.artist_id),
                        album_id=str(album.id),
                    )
                )
                await session.commit()
                return AcceptOutcome(
                    applied=True,
                    proposal="release_type",
                    previous_value=previous,
                    applied_value=suggested,
                    message=(
                        f"Release type changed from {previous} to {suggested}. "
                        "Nothing was queued and no file moved."
                    ),
                )

        # Nothing held. Not a failure and not an error — the honest answer, and
        # the one that tells the client to open a picker rather than inventing a
        # match on its behalf.
        pickers = await self._pickers_for(session, entity_type, entity_id)
        if already is not None:
            message = (
                f"This release is already a {already}, which is what the sources "
                "agreed on. Nothing to apply."
            )
        elif pickers:
            message = (
                "Nothing is proposed for this "
                f"{entity_type.value} — no source has offered a value the others "
                "agreed on. Pick the record yourself."
            )
        else:
            message = (
                "Nothing is proposed for this "
                f"{entity_type.value}, and no source here takes an id by hand."
            )
        return AcceptOutcome(applied=False, message=message, identify_sources=pickers)

    async def _pickers_for(
        self,
        session: AsyncSession,
        entity_type: EnrichmentEntity,
        entity_id: str,
    ) -> tuple[str, ...]:
        """Which identify pickers make sense for this entity, in enum order.

        The row's own sources first — a person resolving a MusicBrainz ambiguity
        is being asked about MusicBrainz's records, which is the rule
        :attr:`ReviewItem.identify_sources` states. When no row is waiting on a
        person (Accept is reachable from the album drawer too, not only from the
        review list) it falls back to every identifiable source this process has
        a provider for, because "which pickers could I open" still has an answer
        there and an empty list would read as "none".
        """
        rows = (
            (
                await session.execute(
                    select(EnrichmentState.source, EnrichmentState.state).where(
                        EnrichmentState.entity_type == entity_type,
                        EnrichmentState.entity_id == entity_id,
                        EnrichmentState.state.in_(REVIEW_STATES),
                    )
                )
            )
            .all()
        )
        # Through :func:`review_picker_for` rather than against
        # ``_IDENTIFIABLE_SOURCES`` directly, so a row that delegates its picker
        # offers the picker it delegates to. An album whose only waiting row is
        # an AcoustID ambiguity is answerable on MusicBrainz, and this used to
        # fall through to the "no row is waiting" branch and offer every
        # identifiable source instead — the widening the delegation exists to
        # avoid.
        waiting = {
            picker
            for source, state in rows
            if (picker := review_picker_for(source, state)) is not None
        }
        if not waiting:
            waiting = {
                source
                for source, provider in self._providers.items()
                if source in _IDENTIFIABLE_SOURCES and provider.supports(entity_type)
            }
        return tuple(
            source.value for source in identifiable_sources() if source in waiting
        )

    # ------------------------------------------------------------- schedule
    def _next_attempt(self, state: str, attempts: int, now: datetime) -> datetime | None:
        """When to try again, given the outcome and how often we already have."""
        if state in _TERMINAL_STATES:
            return None
        if state == "ambiguous":
            return now + _AMBIGUOUS_DELAY
        if state == STATE_FAILED:
            step = _FAILED_INITIAL * (2 ** max(0, attempts - 1))
            return now + min(step, _FAILED_CAP)
        index = min(max(0, attempts - 1), len(_NOT_FOUND_LADDER) - 1)
        return now + _NOT_FOUND_LADDER[index]

    def _update_health(self, result: EnrichResult) -> None:
        """Pause the job when nothing but failures come back, tick after tick.

        An offline machine would otherwise write one activity row per album —
        thousands of them, burying the feed and outliving the 30-day prune. One
        warning and a stop is the honest response.
        """
        if not result.attempted:
            return
        if result.failed == result.attempted:
            self._consecutive_failures += 1
        else:
            self._consecutive_failures = 0
            return

        cutoff = max(1, int(self._settings.enrichment_failure_cutoff))
        if self._consecutive_failures >= cutoff:
            self._paused_reason = (
                f"every request failed for {self._consecutive_failures} ticks — "
                "paused until the next explicit run"
            )
            logger.warning("Enrichment paused: %s", self._paused_reason)

    # -------------------------------------------------------------- helpers
    @staticmethod
    def _artist_id_for(job: EnrichmentJob) -> str | None:
        if job.entity_type is EnrichmentEntity.ARTIST:
            return job.entity_id
        subject = job.subject
        return subject.artist_id if isinstance(subject, AlbumSnapshot) else None

    @staticmethod
    def _album_id_for(job: EnrichmentJob) -> str | None:
        return job.entity_id if job.entity_type is EnrichmentEntity.ALBUM else None

    # --------------------------------------------------------------- status
    async def status(self, session: AsyncSession | None = None) -> dict[str, Any]:
        """Coverage counters for the ``/enrichment`` page and the JSON API.

        Both halves are counted over :func:`library_scope`, and they have to be:
        a percentage is a fraction, and reading "32 identified" against a
        catalogue of 3313 albums says enrichment has covered 1% of a library it
        has in fact finished. ``scope`` carries the honest denominator — how many
        albums and artists are on disk — with the catalogue totals beside it, so
        the page can say what is *not* being enriched rather than looking as if
        it had lost the rest.
        """
        payload: dict[str, Any] = {
            "enabled": self._settings.enrichment_enabled,
            "sources": [source.value for source in self.active_sources()],
            "paused_reason": self._paused_reason,
            "last_run_at": self._last_run_at,
            "last_result": self._last_result.as_dict() if self._last_result else None,
            "gates": {
                "musicbrainz": self._settings.musicbrainz_ready,
                "acoustid": self._settings.acoustid_ready,
            },
            "states": {},
            "scope": {},
        }
        if session is None:
            return payload

        scope = await enrichment_scope_counts(session)
        payload["scope"] = scope
        # Hand the scope over rather than letting it be counted twice: this
        # endpoint is polled, and four duplicate COUNTs a tick is four for
        # nothing.
        payload["autonomy"] = await enrichment_autonomy_counts(session, scope=scope)
        rows = (
            await session.execute(
                select(
                    EnrichmentState.source,
                    EnrichmentState.state,
                    _count(EnrichmentState.entity_id),
                )
                .where(in_library())
                .group_by(EnrichmentState.source, EnrichmentState.state)
            )
        ).all()
        states: dict[str, dict[str, int]] = {}
        for source, state, count in rows:
            key = source.value if isinstance(source, EnrichmentSource) else str(source)
            states.setdefault(key, {})[str(state)] = int(count)
        payload["states"] = states
        return payload


@dataclass(slots=True)
class ReviewItem:
    """One thing a human needs to look at.

    The review list is the other half of "exact or nothing": the matchers refuse
    to guess, and everything they refuse on ends up here. Without it the doctrine
    would just be silent failure.
    """

    entity_type: str
    entity_id: str
    source: str
    state: str
    name: str
    artist_name: str | None = None
    artist_id: str | None = None
    reason: str | None = None
    attempts: int = 0
    last_attempt_at: datetime | None = None
    #: The release type consensus proposed for this album and that the album is
    #: not carrying — i.e. exactly what pressing Accept would apply, or ``None``
    #: when pressing it would open a picker instead.
    #:
    #: Here so the button can be *labelled* honestly before it is pressed. The
    #: alternative is the client fetching every row's album detail to find out,
    #: or guessing, and a button that says "Accept & apply" and then opens a
    #: search box is the same dishonesty as one that applies a search hit.
    #: Always ``None`` for artist rows: consensus proposes nothing about an
    #: artist.
    suggested_release_type: str | None = None

    @property
    def identify_sources(self) -> tuple[str, ...]:
        """The sources a picker may be opened against for this row.

        The row's own source when that source takes an id, the source it
        delegates to when it does not (see :data:`_DELEGATED_PICKERS`), and
        nothing at all otherwise. Both answers come from
        :func:`review_picker_for`, which is also what the list's SQL filter and
        :meth:`Enricher._pickers_for` ask, so the button, the page and the badge
        cannot disagree about whether there is anything to press.

        This is the primary property and :attr:`is_actionable` is derived from
        it — the reverse of how the pair started out. The original direction
        made "can this be acted on?" a question about the *source* alone, which
        is the assumption AcoustID broke: its refusals are answerable, just not
        against AcoustID. Deriving the flag from the answer means the two can
        never drift, and the client is told which source to post to rather than
        left to re-derive ``_IDENTIFIABLE_SOURCES`` for itself.

        A tuple rather than a single value because the question is "which
        pickers", and the empty case then needs no separate flag. It is
        deliberately not "every identifiable source" — a person resolving a
        MusicBrainz ambiguity is being asked about MusicBrainz's records, and
        offering them Deezer's would be a different question with the same
        button.
        """
        try:
            source = EnrichmentSource(self.source)
        except ValueError:  # pragma: no cover - a source no longer in the enum
            return ()
        picker = review_picker_for(source, self.state)
        return (picker.value,) if picker is not None else ()

    @property
    def is_actionable(self) -> bool:
        """True when a person could plausibly resolve this by identifying it.

        Exactly "is there a picker to open", so it cannot say yes to a row the
        client would then draw a dead button on.

        ``not_found`` is not actionable: the upstream simply has no such record
        yet, and that is a matter of waiting rather than of deciding. Nor is a
        rung keyed by an id no person can supply and that no other rung's picker
        stands in for — an input box that cannot lead anywhere is worse than no
        input box, which is why these rows are now kept off the list entirely
        rather than shown as unpressable.
        """
        return bool(self.identify_sources)

    @property
    def state_explanation(self) -> str:
        """What this state means, in one sentence, authored beside the matcher.

        ``reason`` is what *this* rung said about *this* row and is free-form;
        this is what the state itself claims, and it is domain knowledge rather
        than copy — ``ambiguous`` means the matcher found more than one record
        that fit and refused to pick, which is the visible half of "exact or
        nothing". Written here because the alternative is written wherever the
        list happens to be rendered, where nothing connects it to the code that
        decides the state, and the two drift silently apart.
        """
        return _STATE_EXPLANATIONS.get(self.state, "")


async def list_review_items(
    session: AsyncSession,
    *,
    states: Sequence[str] = REVIEW_STATES,
    source: str | None = None,
    entity_type: str | None = None,
    limit: int = 200,
    offset: int = 0,
) -> tuple[list[ReviewItem], int]:
    """In-scope entities the matchers refused to resolve, newest attempt first.

    Named rather than bare ids: "album al7 is ambiguous on musicbrainz" is not
    something anyone can act on, and the whole point of the list is that a person
    can.

    Held to :func:`library_scope` for the same reason. Asking somebody to
    identify a release that is not in their library is asking them to do work
    that changes nothing on disk, and there are three thousand of those for every
    thirty that matter — a list that long is one nobody reads.

    Held to :func:`actionable_review_clause` as well, so what is listed is only
    ever what somebody can actually settle. A rung waiting on an id another rung
    will hand it — the Cover Art Archive on a release MBID, Wikidata on the QID
    MusicBrainz publishes — is doing nothing wrong and needs nobody; putting it
    here made two thirds of this list unpressable and taught people to stop
    reading it. Those rows still exist, still show their state on the entity's
    own screen, and still clear themselves. They are simply not a to-do.

    The default *states* is :data:`REVIEW_STATES`, which is also what
    :func:`app.api.deps.nav_counts` counts, so the badge and the page agree.
    ``rejected`` is not in it: a person has already decided about those, and a
    list that keeps showing you what you dismissed is a list you stop reading.
    They are still reachable by asking for them explicitly — ``?state=rejected``
    — which is deliberate, because it is the only way to find one in order to
    change your mind about it.
    """
    filters = [
        EnrichmentState.state.in_(list(states)),
        in_library(),
        actionable_review_clause(),
    ]
    if source:
        filters.append(EnrichmentState.source == EnrichmentSource(source))
    if entity_type:
        filters.append(EnrichmentState.entity_type == EnrichmentEntity(entity_type))

    total = int(
        (
            await session.execute(
                select(_count(EnrichmentState.entity_id)).where(*filters)
            )
        ).scalar_one()
        or 0
    )
    rows = (
        (
            await session.execute(
                select(EnrichmentState)
                .where(*filters)
                .order_by(
                    EnrichmentState.last_attempt_at.desc().nullslast(),
                    EnrichmentState.entity_id,
                )
                .limit(max(1, limit))
                .offset(max(0, offset))
            )
        )
        .scalars()
        .all()
    )
    if not rows:
        return [], total

    artist_ids = {
        row.entity_id for row in rows if row.entity_type is EnrichmentEntity.ARTIST
    }
    album_ids = {
        row.entity_id for row in rows if row.entity_type is EnrichmentEntity.ALBUM
    }
    albums = {
        str(album.id): album
        for album in (
            (await session.execute(select(Album).where(Album.id.in_(album_ids))))
            .unique()
            .scalars()
            .all()
        )
    }
    # One statement for the whole page, not one per row: this is only here so
    # the Accept button can be labelled with what it would do, and a per-row
    # lookup would make the label cost a query each.
    album_meta = await load_album_metadata(session, albums.keys())
    artist_ids |= {str(album.artist_id) for album in albums.values()}
    artists = dict(
        (
            await session.execute(
                select(Artist.id, Artist.name).where(Artist.id.in_(artist_ids))
            )
        ).all()
    )

    items: list[ReviewItem] = []
    for row in rows:
        proposed: str | None = None
        if row.entity_type is EnrichmentEntity.ARTIST:
            name = str(artists.get(row.entity_id) or row.entity_id)
            artist_id, artist_name = row.entity_id, name
        else:
            album = albums.get(row.entity_id)
            name = album.display_title if album is not None else row.entity_id
            artist_id = str(album.artist_id) if album is not None else None
            artist_name = str(artists.get(artist_id) or "") if artist_id else None
            found = album_meta.get(row.entity_id)
            suggestion = getattr(found, "suggested_release_type", None)
            # Only a *disagreement* is a proposal. A suggestion the album is
            # already carrying is not something Accept would change, and
            # offering to apply it would be a button that does nothing.
            if album is not None and suggestion and suggestion != album.release_type:
                proposed = str(suggestion)
        items.append(
            ReviewItem(
                suggested_release_type=proposed,
                entity_type=row.entity_type.value,
                entity_id=row.entity_id,
                source=row.source.value,
                state=row.state,
                name=name,
                artist_id=artist_id,
                artist_name=artist_name or None,
                reason=row.last_error,
                attempts=row.attempts,
                last_attempt_at=utc(row.last_attempt_at),
            )
        )
    return items, total


def _count(column: Any) -> Any:
    """``func.count`` without importing it at module scope for one use."""
    from sqlalchemy import func  # noqa: PLC0415

    return func.count(column)


def _artist_first() -> Any:
    """Order term sorting artist rows ahead of album rows."""
    from sqlalchemy import case  # noqa: PLC0415

    return case((EnrichmentState.entity_type == EnrichmentEntity.ARTIST, 0), else_=1)


# ---------------------------------------------------------------------------
# Batch loaders — the reason there is no ORM relationship
# ---------------------------------------------------------------------------
# ``deps.artist_to_out`` and ``deps.album_to_out`` are synchronous. A
# ``lazy="selectin"`` relationship does not save them: a row that was committed
# but never refreshed — exactly what ``Indexer.add_artist`` hands to
# ``artist_to_out`` — still raises ``MissingGreenlet`` on attribute access. So
# metadata is fetched here, in bulk, by the async callers, and passed into those
# builders explicitly. One statement per collection, and the failure mode of
# forgetting is "no enrichment shown" rather than a 500.


async def load_artist_metadata(
    session: AsyncSession, artist_ids: Iterable[str]
) -> dict[str, ArtistMetadata]:
    """Fetch artist metadata for *artist_ids* in one statement, keyed by id."""
    ids = {str(value) for value in artist_ids if value}
    if not ids:
        return {}
    rows = (
        (
            await session.execute(
                select(ArtistMetadata).where(ArtistMetadata.artist_id.in_(ids))
            )
        )
        .scalars()
        .all()
    )
    return {str(row.artist_id): row for row in rows}


async def load_album_metadata(
    session: AsyncSession, album_ids: Iterable[str]
) -> dict[str, AlbumMetadata]:
    """Fetch album metadata for *album_ids* in one statement, keyed by id."""
    ids = {str(value) for value in album_ids if value}
    if not ids:
        return {}
    rows = (
        (
            await session.execute(
                select(AlbumMetadata).where(AlbumMetadata.album_id.in_(ids))
            )
        )
        .scalars()
        .all()
    )
    return {str(row.album_id): row for row in rows}


async def load_track_metadata(
    session: AsyncSession, track_ids: Iterable[str]
) -> dict[str, TrackMetadata]:
    """Fetch track metadata for *track_ids* in one statement, keyed by id."""
    ids = {str(value) for value in track_ids if value}
    if not ids:
        return {}
    rows = (
        (
            await session.execute(
                select(TrackMetadata).where(TrackMetadata.track_id.in_(ids))
            )
        )
        .scalars()
        .all()
    )
    return {str(row.track_id): row for row in rows}


async def load_file_claims(
    session: AsyncSession, track_ids: Iterable[str]
) -> dict[str, FileClaim]:
    """Fetch what the *files* claim for *track_ids* in one statement, keyed by id.

    Loaded the same way and at the same moment as the three metadata tables, and
    kept scrupulously apart from them everywhere else. A row here holds a string
    somebody else's tagger wrote — a hand-typed barcode, an MBID copied from the
    wrong release, a ``qobuzarr_qid`` that came along when a file was duplicated
    rather than moved. ``track_metadata`` holds what was *verified*: an id that
    got there because a barcode matched exactly, because a person picked a
    candidate, or because the audio itself agreed.

    So a claim is a **hypothesis**, and there are exactly two things a rung may
    do with one. It may be **used as a lookup key** — a barcode in a tag is a
    free place to start looking, costing a tag parse instead of an HTTP request —
    and it may **corroborate or reject** a candidate something else proposed.
    It may never select one, and it may never be written into
    ``artist_metadata``, ``album_metadata`` or ``track_metadata``, because a claim
    that lands in the verified tables is indistinguishable from a fact five
    minutes later: it is read back as evidence, propagated to the release and the
    artist, and written into every file on the next re-tag, with nothing
    downstream able to tell that the whole chain rests on a string a stranger
    typed. That is the same rule :mod:`app.enrich.matching` already enforces for
    names — a name may reject a candidate, never select one — applied to the other
    input that arrives unverified.

    Absent means the file claimed nothing, which is the ordinary case: the disk
    scan writes no row for a file with no identifying tags.
    """
    ids = {str(value) for value in track_ids if value}
    if not ids:
        return {}
    rows = (
        (await session.execute(select(FileClaim).where(FileClaim.track_id.in_(ids))))
        .scalars()
        .all()
    )
    return {str(row.track_id): row for row in rows}


async def mark_library_due(
    session: AsyncSession, album_ids: Iterable[str], *, priority: int = 10
) -> int:
    """Put albums that have just landed on disk at the front of the line.

    Called from the moments a release becomes part of the library: the download
    loop finalising one, the queue worker promoting one whose callable did not
    set the status itself, and the disk scan adopting one. So the minutes after a
    download are spent enriching the thing the user is looking at rather than
    whatever the backlog happened to be holding. The artists are marked with
    them, and ahead of them: both album browse paths need the artist's id, so an
    album whose artist is still unknown can only answer ``no_key``.

    Reaches the running :class:`Enricher` through :mod:`app.core.state` rather
    than being plumbed through two constructors, and does nothing at all when
    there is no application state — a CLI scan, or a test. That is deliberate and
    safe: :meth:`Enricher._seed` finds the same albums on its next tick from the
    database alone, so this only ever changes *when* the work happens, never
    whether it happens.

    Runs on the caller's session and inside the caller's transaction, like
    :meth:`Enricher.mark_due` itself: it is SQL, no request is made, and the
    write lock is held no longer than the statements take.
    """
    from app.core.state import state_or_none  # noqa: PLC0415 - state imports this module

    enricher = getattr(state_or_none(), "enricher", None)
    if enricher is None:
        return 0

    ids = [str(value) for value in album_ids if value]
    if not ids:
        return 0
    artist_ids = {
        str(found)
        for found in (
            await session.execute(
                select(Album.artist_id).where(
                    Album.id.in_(ids), Album.artist_id.is_not(None)
                )
            )
        )
        .scalars()
        .all()
    }
    touched = await enricher.mark_due(
        session, EnrichmentEntity.ARTIST, artist_ids, priority=priority
    )
    touched += await enricher.mark_due(
        session, EnrichmentEntity.ALBUM, ids, priority=priority
    )
    return touched


async def reopen_library_albums(
    session: AsyncSession, album_ids: Iterable[str], *, priority: int = 15
) -> int:
    """Re-identify releases whose audio is no longer the audio that identified them.

    The counterpart of :func:`mark_library_due`, and it exists because that
    function cannot do this job. ``mark_due`` only moves ``pending``,
    ``not_found`` and ``failed`` rows forward, which is right for a release that
    has just landed — an ``ok`` row has nothing to learn from being asked again.
    A release whose files were *replaced* is the case where that reasoning
    inverts: every row is ``ok`` precisely because it was matched, and what it
    was matched against is gone. Calling ``mark_due`` there changes nothing at
    all while reporting that it did, which is how the nightly integrity pass came
    to log "N release(s) queued for re-identification" having queued none.

    So this goes through :meth:`Enricher.reopen`, the same escape hatch the
    review page's "run now" uses: the state, the attempt count and the last error
    are all reset regardless of where the row had got to. Callers are
    :func:`app.core.scheduler._reopen_replaced` and the disk scan, both of which
    have just hashed the file and found different bytes with different audio in
    it.

    Scope is still honoured — only albums :func:`library_scope` recognises are
    re-opened, so a partially scanned release that is not ``DOWNLOADED`` does not
    acquire work rows for the nightly purge to delete again. Runs on the caller's
    session, inside the caller's transaction, and makes no request, exactly like
    :meth:`Enricher.mark_due`.

    Returns:
        How many **albums** were re-opened, which is what the callers report. Not
        how many rows moved: one album with five sources is one release the user
        will see re-identified.
    """
    from app.core.state import state_or_none  # noqa: PLC0415 - state imports this module

    enricher = getattr(state_or_none(), "enricher", None)
    if enricher is None:
        return 0

    ids = [str(value) for value in album_ids if value]
    if not ids:
        return 0

    # Same reason ``mark_due`` flushes: these sessions do not autoflush, so a
    # status the caller has just written is invisible to a SQL scope test.
    await session.flush()
    in_scope = {
        str(found)
        for found in (
            await session.execute(
                library_scope(EnrichmentEntity.ALBUM).where(Album.id.in_(ids))
            )
        )
        .scalars()
        .all()
    }

    reopened = 0
    for album_id in ids:
        if album_id not in in_scope:
            continue
        if await enricher.reopen(
            session, EnrichmentEntity.ALBUM, album_id, priority=priority
        ):
            reopened += 1
    return reopened


async def enrichment_scope_counts(session: AsyncSession) -> dict[str, int]:
    """How much of the catalogue is actually in the library, for the readouts.

    Four counts rather than a percentage: the page decides how to phrase it, and
    a ratio computed here would have to be recomputed there anyway to say
    "32 of 3313". ``albums``/``artists`` are the denominator every enrichment
    figure is against; the catalogue totals are what makes the gap explicable
    instead of alarming.
    """
    in_scope_albums = select(_count(Album.id)).where(
        Album.id.in_(library_scope(EnrichmentEntity.ALBUM))
    )
    in_scope_artists = select(_count(Artist.id)).where(
        Artist.id.in_(library_scope(EnrichmentEntity.ARTIST))
    )
    counts: dict[str, int] = {}
    for name, statement in (
        ("albums", in_scope_albums),
        ("artists", in_scope_artists),
        ("catalogue_albums", select(_count(Album.id))),
        ("catalogue_artists", select(_count(Artist.id))),
    ):
        counts[name] = int((await session.execute(statement)).scalar_one() or 0)
    return counts


async def enrichment_autonomy_counts(
    session: AsyncSession, *, scope: Mapping[str, int] | None = None
) -> dict[str, int]:
    """How much identification happened without a person — a partition, not a score.

    Six keys. ``entities`` is the denominator — the albums and artists actually
    on disk, from :func:`enrichment_scope_counts`, because a percentage against
    the catalogue reports a finished library as 1%. The other five partition
    those same entities: ``automatic``, ``waiting_person``, ``waiting_input``,
    ``dismissed``, ``unstarted``. They sum to ``entities`` by construction, and
    that is the property the readout's arithmetic rests on.

    An entity is bucketed by the **strongest claim on its attention**, first
    match wins, because an entity has one row per source and those rows disagree:

    1. ``waiting_person`` — some row for which :func:`review_picker_for` names a
       picker. Exactly what the review list shows and what the nav badge counts,
       delegation included, because it is the same function rather than a second
       copy of the rule.
    2. ``waiting_input`` — else a refusal nobody can press (a state in
       :data:`REVIEW_STATES` with no picker) or a gated rung. These are the rows
       :meth:`Enricher._rearm_stranded` watches: an absent ``fpcalc``, an artist
       id another rung will supply. They store ``next_attempt_at = NULL`` and
       need no one.
    3. ``dismissed`` — else a ``rejected`` row. No matcher can produce that
       state, so its presence is proof a person pressed the button.
    4. ``automatic`` — else an ``ok`` row: identification that happened with
       nobody involved, which is the figure the band is about.
    5. ``unstarted`` — everything else (only ``pending``/``failed``/
       ``not_found`` rows), plus every in-scope entity :meth:`Enricher._seed`
       has not reached yet, which is unstarted rather than missing.

    A pending human decision outranks an input-wait because the person is the
    scarce resource; an input-wait outranks a dismissal and a dismissal outranks
    a partial success because both of those are already settled for now.

    What this is **not**: it is not a score, not a confidence, and no figure in
    it may ever be compared against a threshold. ``app.enrich.matching`` is exact
    or nothing and :func:`app.enrich.coverage.solve_album` has no
    highest-coverage-wins branch; a bar that auto-accepted anything would be that
    tie-break with a number on it, and the value it accepted is written into
    every file as ``MUSICBRAINZ_ALBUMID``.

    ``scope`` is an optional hand-over of an
    :func:`enrichment_scope_counts` a caller has already paid for —
    :meth:`Enricher.status` computes it a line earlier, and this endpoint is
    polled. It is a saving, never a different answer: the counts are the same
    four aggregates either way.
    """
    counted = scope if scope is not None else await enrichment_scope_counts(session)
    entities = counted["albums"] + counted["artists"]

    # Rows, not aggregates — the bucket is a property of the entity, and an
    # entity's rows disagree. Bounded by ``in_library()``, the same predicate
    # :meth:`Enricher.status` counts over, so this is roughly
    # (albums + artists) x len(sources) rows rather than the whole catalogue.
    rows = (
        await session.execute(
            select(
                EnrichmentState.entity_type,
                EnrichmentState.entity_id,
                EnrichmentState.source,
                EnrichmentState.state,
            ).where(in_library())
        )
    ).all()

    per_entity: dict[tuple[str, str], list[tuple[EnrichmentSource | None, str]]] = {}
    for entity_type, entity_id, source, state in rows:
        key = (
            entity_type.value
            if isinstance(entity_type, EnrichmentEntity)
            else str(entity_type),
            str(entity_id),
        )
        per_entity.setdefault(key, []).append((_as_source(source), str(state)))

    counts = {
        "automatic": 0,
        "waiting_person": 0,
        "waiting_input": 0,
        "dismissed": 0,
        "unstarted": 0,
    }
    for pairs in per_entity.values():
        if any(
            source is not None and review_picker_for(source, state) is not None
            for source, state in pairs
        ):
            counts["waiting_person"] += 1
        elif any(
            state in REVIEW_STATES or state == STATE_GATED for _source, state in pairs
        ):
            counts["waiting_input"] += 1
        elif any(state == STATE_REJECTED for _source, state in pairs):
            counts["dismissed"] += 1
        elif any(state == STATE_OK for _source, state in pairs):
            counts["automatic"] += 1
        else:
            counts["unstarted"] += 1

    counts["unstarted"] += max(0, entities - len(per_entity))
    return {"entities": entities, **counts}


async def purge_out_of_scope_enrichment(session: AsyncSession) -> int:
    """Delete the state rows of everything that is not in the library.

    The maintenance half of :func:`library_scope`, run nightly by housekeeping
    and on demand from the CLI. Narrowing the scope only stops *new* work being
    created; one real database was left holding 11418 pending rows for a
    catalogue of 3313 albums, and until they are gone every readout on the
    enrichment page is counting them.

    Three properties make it safe to run unattended, and each is load-bearing:

    * **It deletes work, never knowledge.** ``enrichment_state`` is a to-do list
      of (entity, source) pairs, re-created by :meth:`Enricher._seed` the moment
      the album is on disk again. The three metadata tables are untouched:
      ``album_metadata.qobuz_release_type`` is the only thing that makes an
      applied ``Album.release_type`` reversible, and ``mb_release_group_mbid`` is
      what edition grouping keys on, so dropping either would change which
      release Qobuzarr thinks it has — the one thing this change must not do.
    * **It is idempotent.** It asks the current state, so running it twice, or
      after a scan, or on an empty database, does the same thing.
    * **It cannot touch an in-scope row.** Including the ``ambiguous`` and
      ``no_key`` rows a person is waiting on: those belong to entities in the
      library, which is exactly what this predicate keeps.

    Returns the number of rows deleted.
    """
    stmt = delete(EnrichmentState).where(
        or_(
            (EnrichmentState.entity_type == EnrichmentEntity.ARTIST)
            & EnrichmentState.entity_id.not_in(library_scope(EnrichmentEntity.ARTIST)),
            (EnrichmentState.entity_type == EnrichmentEntity.ALBUM)
            & EnrichmentState.entity_id.not_in(library_scope(EnrichmentEntity.ALBUM)),
        )
    )
    removed = int((await session.execute(stmt)).rowcount or 0)
    if removed:
        logger.info("Purged %d out-of-scope enrichment row(s)", removed)
    return removed


async def prune_enrichment_orphans(session: AsyncSession) -> int:
    """Delete state rows whose entity is gone.

    ``EnrichmentState.entity_id`` is polymorphic, so it cannot carry a foreign
    key and deleting an artist leaves its rows behind. This is the price of one
    state table instead of three, and it is a cheap one — a nightly statement
    against an index.
    """
    stmt = delete(EnrichmentState).where(
        or_(
            (EnrichmentState.entity_type == EnrichmentEntity.ARTIST)
            & EnrichmentState.entity_id.not_in(select(Artist.id)),
            (EnrichmentState.entity_type == EnrichmentEntity.ALBUM)
            & EnrichmentState.entity_id.not_in(select(Album.id)),
        )
    )
    return int((await session.execute(stmt)).rowcount or 0)
