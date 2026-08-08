"""The contract between the enrichment orchestrator and the sources.

A provider never sees the ORM. :class:`~app.core.enricher.Enricher` reads what an
entity currently looks like into a frozen :class:`ArtistSnapshot` or
:class:`AlbumSnapshot`, **closes the session**, and only then hands the snapshot
to a provider. That is not tidiness — SQLite allows exactly one writer, and a
transaction held open across a two-second MusicBrainz call would block the
download worker's per-track commits until it timed out and marked a live download
failed. Snapshots make that mistake impossible to make by accident.

Providers hand back an :class:`EnrichmentResult`: plain field dictionaries for
the three metadata tables, plus per-field *opinions* which are what
:mod:`app.enrich.merge` counts when deciding whether a majority exists. They
never decide to write anything themselves, and they never raise to mean "no
match" — that is what the :class:`~app.enrich.errors.EnrichmentOutcome` family is
for.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date
from typing import Any, Protocol, runtime_checkable

from app.models import EnrichmentEntity, EnrichmentSource

__all__ = [
    "TrackSnapshot",
    "AlbumSnapshot",
    "ArtistSnapshot",
    "Candidate",
    "EnrichmentJob",
    "EnrichmentResult",
    "EnrichmentProvider",
]


@dataclass(frozen=True, slots=True)
class TrackSnapshot:
    """One track as Qobuzarr currently knows it.

    Present only for albums that have been downloaded — track rows are written by
    the download loop, not the indexer — so a provider must treat an empty track
    list as normal rather than as an error.
    """

    id: str
    title: str
    track_number: int
    media_number: int
    duration: int | None = None
    isrc: str | None = None
    path: str | None = None

    # -- what earlier rungs already resolved --------------------------------
    #: The recording MusicBrainz mapped onto this track. The fingerprint rung
    #: compares its own verdict against this, which is the only way the audio
    #: gets to contradict the metadata.
    mb_recording_mbid: str | None = None
    acoustid: str | None = None


@dataclass(frozen=True, slots=True)
class AlbumSnapshot:
    """One release as Qobuzarr currently knows it, plus what enrichment found before.

    Three separate barcode-shaped fields sit here, and keeping them apart is the
    point. ``upc`` is what Qobuz published for this release; ``barcode`` is one an
    earlier rung verified; ``claimed_barcode`` is what the files on disk say. They
    are handed to :func:`app.enrich.matching.barcode_candidates` in that order,
    which is descending trust.

    The Qobuz **album id** is not among them and must never be added. It looks
    like a barcode often enough to be tempting — ``0804879535645`` really is
    *Blues Of Desperation* — and it was used as one, and on this user's library
    every one of the 25 "no MusicBrainz release carries barcode …" failures came
    from that. ``0060249867260`` is not the barcode of *Shangri-La*;
    ``602498672600`` is, one digit-shift away. See
    :func:`~app.enrich.matching.barcode_candidates` for the full measurement.
    """

    id: str
    artist_id: str
    artist_name: str
    title: str
    version: str | None = None
    release_date: date | None = None
    release_type: str = "album"
    tracks_count: int = 0
    media_count: int = 1
    upc: str | None = None
    label: str | None = None
    genre: str | None = None
    tracks: tuple[TrackSnapshot, ...] = ()

    #: The barcode this release's *files* claim, when they all claim the same
    #: one — read out of ``file_claims`` by
    #: :func:`app.enrich.matching.album_claimed_barcode`, which returns ``None``
    #: the moment two of them disagree.
    #:
    #: A **hypothesis**, and the lowest-trust entry in the list: nothing vouches
    #: for a tag, so this may narrow a search and may reject a candidate, but a
    #: provider must never write it anywhere as a fact. It exists because
    #: refusing to read the Qobuz album id as a barcode took a key away from
    #: releases with no ``upc``, and a ``BARCODE`` tag is the honest replacement —
    #: a field that actually is a barcode, checked against its own check digit.
    claimed_barcode: str | None = None

    # -- what earlier rungs already resolved, so a later one can chain off it --
    barcode: str | None = None
    deezer_album_id: str | None = None
    deezer_artist_id: str | None = None
    mb_release_mbid: str | None = None
    mb_release_group_mbid: str | None = None
    mb_artist_mbid: str | None = None

    @property
    def year(self) -> int | None:
        """Release year, or ``None`` when Qobuz gave no date."""
        return self.release_date.year if self.release_date else None


@dataclass(frozen=True, slots=True)
class ArtistSnapshot:
    """One followed artist as Qobuzarr currently knows it."""

    id: str
    name: str
    image_url: str | None = None
    albums_count: int = 0

    # -- what earlier rungs already resolved --------------------------------
    isni: str | None = None
    mb_artist_mbid: str | None = None
    deezer_artist_id: str | None = None
    wikidata_qid: str | None = None


@dataclass(frozen=True, slots=True)
class EnrichmentJob:
    """One (entity, source) unit of work, detached from any session."""

    entity_type: EnrichmentEntity
    source: EnrichmentSource
    entity_id: str
    #: The snapshot, typed by ``entity_type``.
    subject: ArtistSnapshot | AlbumSnapshot
    #: The key the previous attempt used. A change re-opens the row by itself.
    match_key: str | None = None
    #: How many attempts have already been made, including this one.
    attempts: int = 0

    @property
    def artist(self) -> ArtistSnapshot:
        """The subject as an artist. Only valid for artist jobs."""
        assert isinstance(self.subject, ArtistSnapshot)
        return self.subject

    @property
    def album(self) -> AlbumSnapshot:
        """The subject as an album. Only valid for album jobs."""
        assert isinstance(self.subject, AlbumSnapshot)
        return self.subject


@dataclass(frozen=True, slots=True)
class Candidate:
    """One upstream record a person could pick, rendered as a card.

    This is the *only* place in the enrichment code where a name search happens,
    and it exists because the alternative is worse. "Exact or nothing" leaves a
    residue that no automatic rule can resolve — a classical release credited to
    a composer, a conductor and an orchestra; an artist with no barcoded release
    to derive an id from — and the escape hatch for that residue used to be a
    text box asking for ``984f8239-8fe1-4683-9c54-10ffb14439e9``. Nobody knows
    that. They know what the record looks like.

    So the fields here are chosen to be *recognisable* rather than complete:
    artwork, the title, who it is by, the year, how many tracks, and whatever the
    upstream says to tell two same-named records apart. ``external_id`` is what
    gets stored; everything else exists so a person can tell which one it is.

    Nothing automatic may ever consume these. :meth:`EnrichmentProvider.fetch`
    does not call the search endpoints that build them, and a candidate list is
    never resolved by picking the top row — that is precisely the fuzzy scoring
    the matchers refuse to do. A ``Candidate`` becomes a match only when a person
    presses the button, and what is then written is marked ``manual``.
    """

    #: What gets stored: an MBID, or a Deezer numeric id.
    external_id: str
    #: The record's own name — a release title, or an artist name.
    title: str
    #: Who it is by, for releases. ``None`` for artists, who are their own title.
    subtitle: str | None = None
    #: One line of identifying facts already joined for display, e.g.
    #: ``"2024 · 12 tracks · CD · GB"`` or ``"Group · GB · formed 1946"``.
    detail: str | None = None
    #: The upstream's own note for telling same-named records apart. This is the
    #: single most useful field on the card and it is why MusicBrainz is
    #: searchable by a human at all: "British blues guitarist" against
    #: "American country singer" settles in a glance what an id never could.
    disambiguation: str | None = None
    #: Artwork or a portrait, when the upstream has one. Best-effort: a card with
    #: no image still identifies its record from the text.
    image_url: str | None = None
    #: A link to the record on the upstream, so someone can check before picking.
    url: str | None = None


@dataclass(slots=True)
class EnrichmentResult:
    """What one provider learned. Plain data — the orchestrator does the writing.

    The three ``*_fields`` dictionaries are column names on
    :class:`~app.models.ArtistMetadata`, :class:`~app.models.AlbumMetadata` and
    :class:`~app.models.TrackMetadata`. A key that is absent means "no opinion"
    and leaves whatever is there alone; a key present with ``None`` means "I know
    this is empty" and clears it. That distinction is what stops a sparse answer
    from wiping a richer one, the same rule ``Indexer._apply_metadata`` follows.
    """

    #: The key this attempt actually used, stored so a change re-opens the row.
    match_key: str | None = None
    artist_fields: dict[str, Any] = field(default_factory=dict)
    album_fields: dict[str, Any] = field(default_factory=dict)
    #: ``track_id`` -> column values.
    track_fields: dict[str, dict[str, Any]] = field(default_factory=dict)
    #: Votes this source casts on contested fields, e.g.
    #: ``{"release_type": "compilation"}``. Counted by :mod:`app.enrich.merge`;
    #: a value only ever reaches a Qobuz-owned column through a majority there.
    opinions: dict[str, Any] = field(default_factory=dict)
    #: Rows to re-open because this result changed something they depend on —
    #: resolving an artist MBID, say, makes the Wikidata rung worth retrying.
    reopen: list[tuple[EnrichmentEntity, str, EnrichmentSource]] = field(
        default_factory=list
    )
    #: Human-readable one-liner for the activity feed, when worth logging.
    note: str | None = None

    def is_empty(self) -> bool:
        """True when the provider learned nothing worth writing."""
        return not (
            self.artist_fields or self.album_fields or self.track_fields or self.opinions
        )


@runtime_checkable
class EnrichmentProvider(Protocol):
    """One rung of the ladder.

    Implementations live in :mod:`app.enrich` and are pure request-and-map: they
    take a snapshot, talk to exactly one upstream, and return an
    :class:`EnrichmentResult` or raise an
    :class:`~app.enrich.errors.EnrichmentOutcome`. They do not touch the
    database, do not decide precedence, and do not retry — the shared
    :class:`~app.net.http.JsonHttpClient` already does that.
    """

    #: Which source this is; matches ``enrichment_state.source``.
    source: EnrichmentSource

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        """True when this provider has anything to say about that entity type."""
        ...

    def is_ready(self) -> bool:
        """False when the rung is not configured and should be gated, not run.

        MusicBrainz without a contact, AcoustID without ``fpcalc``. Reported as
        :class:`~app.enrich.errors.SourceGated` so the other rungs carry on.

        A provider that can answer ``False`` here should also define
        ``gate_reason() -> str`` naming the *specific* missing piece; the
        orchestrator writes it into ``enrichment_state.last_error`` and the
        enrichment page shows it. Without one the row reads "<source> is not
        configured", which is true of two different missing things at once and
        sends people to check the one they already have.
        """
        ...

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        """Do the lookup. Raise an ``EnrichmentOutcome`` for a definite "no"."""
        ...

    async def aclose(self) -> None:
        """Release the HTTP client."""
        ...


@runtime_checkable
class SearchableProvider(Protocol):
    """A provider whose records a *person* can search through and pick from.

    Deliberately separate from :class:`EnrichmentProvider`. Cover Art Archive is
    keyed by an MBID, Wikidata by a QID MusicBrainz publishes, AcoustID by the
    audio itself — none of them has an id anyone could look up, so none of them
    implements this and none of them offers an input box that leads nowhere.
    Splitting the protocol is what makes that a type-level fact rather than a
    convention: ``isinstance(provider, SearchableProvider)`` is the check, and a
    rung cannot accidentally acquire a search path by growing a method on the
    base protocol.
    """

    source: EnrichmentSource

    async def candidates(
        self, job: EnrichmentJob, query: str | None = None
    ) -> list[Candidate]:
        """Records a person might mean, best guess first.

        *query* overrides what the subject is called — the name in the library is
        the natural first search, but it is also sometimes the reason nothing
        matched, so the UI lets it be edited.

        Ordering is the upstream's own relevance, which is fine here for the same
        reason it is forbidden in :meth:`EnrichmentProvider.fetch`: a person is
        going to read the cards. Never resolve this list by taking the first one.
        """
        ...
