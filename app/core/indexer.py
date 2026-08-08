"""The slow background indexer: the headline feature of Qobuzarr.

The indexer walks the followed artists **one at a time**, at a deliberately
sedate pace, looking for releases Qobuzarr has not seen yet.  Everything it
does goes through :class:`app.qobuz.client.QobuzClient`, which in turn is gated
by the single global :class:`~app.net.ratelimit.RateLimiter`, so the indexer
can never outrun the configured request budget.

Pacing
------
The per-artist re-check period is::

    period = max(INDEXER_ARTIST_INTERVAL,
                 INDEXER_FULL_SWEEP_HOURS * 3600 / max(1, monitored_artists))

With a handful of artists the sweep target dominates and each artist is
re-checked every few hours.  Once the library grows past
``full_sweep_seconds / artist_interval`` artists the floor takes over: the
indexer keeps ticking at ``INDEXER_ARTIST_INTERVAL`` but, because a tick only
ever touches *one* artist, the time to come back round to any given artist
becomes ``artists x interval`` — a big library automatically slows down instead
of speeding up.  :meth:`Indexer.full_cycle_seconds` reports that figure.

Every interval has +/-``INDEXER_JITTER_PCT`` jitter applied so the traffic
pattern is never perfectly periodic.

Decisions
---------
For a **new** album the indexer decides:

* ``monitor_mode == none`` or the artist is unmonitored -> ``skipped``
* release type not in the artist's ``accepted_release_types`` -> ``skipped``
* ``monitor_mode == future`` and the album pre-dates ``artist.added_at``
  -> ``skipped`` (this is how adding an artist establishes a baseline)
* otherwise -> ``wanted``, plus a :class:`~app.models.QueueItem`

Editions of the same release (same base title, differing only by ``version`` —
"Deluxe Edition", "Remastered", ...) are de-duplicated: the hi-res / most-tracks
edition wins and the rest are marked ``skipped``.  If any edition is already
downloaded (or downloading) that one wins outright, so a newly discovered
"better" edition never silently triggers a re-download.

Every decision is written to the :class:`~app.models.Activity` feed.
"""

from __future__ import annotations

import asyncio
import random
import re
import time
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from typing import Any, Callable, Iterable, Mapping, Sequence

from sqlalchemy import func, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, get_settings
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    Artist,
    MonitorMode,
    QueueItem,
    QueueState,
    join_release_types,
)
from app.qobuz.errors import QobuzError
from app.qobuz.mapper import map_album, map_artist

__all__ = [
    "BacklogChange",
    "IndexResult",
    "Indexer",
    "UnknownArtistError",
    "apply_monitoring_to_backlog",
    "dedupe_key",
    "desired_status",
    "edition_rank",
    "ensure_queue_item",
    "utc",
]

logger = get_logger(__name__)

#: Album columns that must never be overwritten when re-mapping a known album,
#: because they carry local state rather than Qobuz metadata.
_PROTECTED_ALBUM_FIELDS: frozenset[str] = frozenset(
    {"id", "status", "monitored", "path", "downloaded_at", "added_at"}
)

#: Album statuses that must never be demoted by the edition de-duplicator.
_UNTOUCHABLE_STATUSES: frozenset[AlbumStatus] = frozenset(
    {AlbumStatus.DOWNLOADED, AlbumStatus.DOWNLOADING, AlbumStatus.FAILED}
)

#: Statuses meaning "we already have, or are getting, this release".
_HELD_STATUSES: frozenset[AlbumStatus] = frozenset(
    {AlbumStatus.DOWNLOADED, AlbumStatus.DOWNLOADING}
)

#: Parenthesised/bracketed qualifiers that mark an *edition*, not a new release.
_EDITION_WORDS = (
    "edition|version|remaster(?:ed)?|deluxe|expanded|anniversary|reissue|remix(?:ed)?|"
    "mono|stereo|bonus|explicit|clean|extended|special|collector'?s|super|"
    "hi-?res|24-?bit|hd|ep|single|complete|original"
)
#: Each alternative must match as a *whole word*.  Without the ``\b`` anchors,
#: short alternatives such as ``ep``, ``hd`` and ``mono`` matched as bare
#: substrings ("R-ep-rise", "Pr-ep-ared", "De-ep"), which collapsed genuinely
#: different releases — ``Nocturne`` and ``Nocturne (Reprise)`` — into one group
#: and silently skipped the loser.
_EDITION_SUFFIX_RE = re.compile(
    rf"\s*[\(\[][^()\[\]]*\b(?:{_EDITION_WORDS})\b[^()\[\]]*[\)\]]\s*$", re.IGNORECASE
)
_DASH_SUFFIX_RE = re.compile(
    rf"\s+[-–—]\s+[^-–—]*\b(?:{_EDITION_WORDS})\b[^-–—]*$", re.IGNORECASE
)
_PUNCT_RE = re.compile(r"[^\w\s]+", re.UNICODE)
_SPACE_RE = re.compile(r"\s+")


class UnknownArtistError(LookupError):
    """Raised when an operation names an artist that is not in the database."""


def utc(value: datetime | None) -> datetime | None:
    """Return *value* as a timezone-aware UTC datetime.

    SQLite hands back naive datetimes even for ``DateTime(timezone=True)``
    columns, and comparing those to :func:`app.models.utcnow` would raise
    ``TypeError``.  Everything Qobuzarr writes is UTC, so a missing tzinfo is
    simply stamped as UTC.
    """
    if value is None:
        return None
    return value if value.tzinfo is not None else value.replace(tzinfo=timezone.utc)


def _now() -> datetime:
    """Timezone-aware current time (kept separate so tests can monkeypatch)."""
    return datetime.now(timezone.utc)


def dedupe_key(title: str | None) -> str:
    """Normalise an album title down to the key used for edition de-duplication.

    ``"Rumours (Deluxe Edition)"``, ``"Rumours [2013 Remastered]"`` and
    ``"Rumours"`` all collapse to ``"rumours"``.  Punctuation and case are
    discarded so ``"Sign o' the Times"`` matches ``"Sign O The Times"``.
    """
    text = (title or "").strip()
    for _ in range(3):  # strip at most a few stacked qualifiers
        stripped = _EDITION_SUFFIX_RE.sub("", text)
        stripped = _DASH_SUFFIX_RE.sub("", stripped)
        if stripped == text or not stripped.strip():
            break
        text = stripped
    text = _PUNCT_RE.sub(" ", text.casefold())
    return _SPACE_RE.sub(" ", text).strip()


def edition_rank(album: Album) -> tuple[Any, ...]:
    """Sort key deciding which edition of a release Qobuzarr keeps.

    **Track count first**, then hi-res, then bit depth / sampling rate / disc
    count, then the newest release date.  The album id is the final tie-break so
    the ordering is stable across runs.

    Track count outranks audio quality deliberately: a one-track hi-res promo
    single must never beat the twelve-track album it was cut from.
    """
    return (
        album.tracks_count or 0,
        1 if album.hires else 0,
        album.max_bit_depth or 0,
        album.max_sampling_rate or 0.0,
        album.media_count or 1,
        album.release_date or date.min,
        str(album.id),
    )


def desired_status(artist: Artist, album: Album) -> tuple[AlbumStatus, str]:
    """Whether *artist*'s settings want *album*, and the sentence saying why.

    The single statement of "does this artist want this release?", with two
    callers: the indexer, deciding what a newly discovered album starts as, and
    :func:`apply_monitoring_to_backlog`, deciding what happens to the releases
    already on file when somebody changes their mind about an artist. It was a
    private method of :class:`Indexer` while it had one caller.

    Pure — it reads the two rows and nothing else.
    """
    if not artist.monitored:
        return AlbumStatus.SKIPPED, "artist is not monitored"
    if artist.monitor_mode is MonitorMode.NONE:
        return AlbumStatus.SKIPPED, "monitor mode is 'none'"
    if not artist.accepts(album.release_type):
        return (
            AlbumStatus.SKIPPED,
            f"release type '{album.release_type}' is not accepted",
        )
    if album.guest_appearance and not artist.include_guest_appearances:
        return AlbumStatus.SKIPPED, "artist only guests on this release"
    # `is None` and not the parsed list: the column's *null* is "nobody has
    # looked", which must behave exactly as if the filter were off, while an
    # analysed release whose every credit was the bare artist name stores `[]`
    # and is a release the filter genuinely refuses. Collapsing the two would
    # demote a whole catalogue the moment a filter was set, before anything had
    # measured a single release.
    accepted = artist.credit_filter_list
    if accepted and album.credit_names is not None:
        wanted = {name.casefold() for name in accepted}
        if not any(name.casefold() in wanted for name in album.credit_names_list):
            return AlbumStatus.SKIPPED, "not credited to an accepted artist"
    if artist.monitor_mode is MonitorMode.FUTURE:
        added = utc(artist.added_at) or _now()
        if album.release_date is None:
            return AlbumStatus.SKIPPED, "future-only mode and release date unknown"
        if album.release_date < added.date():
            return (
                AlbumStatus.SKIPPED,
                f"future-only mode and released {album.release_date.isoformat()}",
            )
    return AlbumStatus.WANTED, "wanted"


def wants_nothing(artist: Artist) -> bool:
    """Whether *artist*'s settings refuse every release, whatever it is.

    The two clauses of :func:`desired_status` that never read the album, named
    once so :func:`apply_monitoring_to_backlog` can demote a whole artist's
    backlog in one UPDATE instead of loading every row to be told the same
    thing four thousand times. It is an optimisation and must stay a faithful
    one: this returning ``True`` has to mean ``desired_status`` would answer
    ``SKIPPED`` for **every** album, so a new album-independent refusal belongs
    here as well as there, and an album-*dependent* one belongs only there.

    Pure — it reads the one row and nothing else.
    """
    return not artist.monitored or artist.monitor_mode is MonitorMode.NONE


@dataclass(slots=True)
class BacklogChange:
    """How many releases a monitoring change moved, in each direction."""

    demoted: int = 0
    """Were wanted, are not any more: their artist's settings stopped wanting
    them — unmonitored, switched to ``monitor_mode='none'`` or ``future``, or
    narrowed to a set of release types this one is not in."""
    restored: int = 0
    """Were skipped, are wanted again: their artist's settings want them once
    more, and no person had ignored the release itself."""

    @property
    def touched(self) -> int:
        return self.demoted + self.restored


async def apply_monitoring_to_backlog(
    session: AsyncSession, artists: Sequence[Artist]
) -> BacklogChange:
    """Bring each artist's **existing** backlog into line with their settings.

    Turning an artist's monitoring off used to leave every release it had
    already marked ``wanted`` sitting in the backlog for good, and nothing else
    would ever come along and clear them: :meth:`Indexer.claim_artist` only
    visits *monitored* artists, and even a visit re-derives the status of new
    albums only. So one real library sat at 4,000-odd wanted releases belonging
    to artists it had been told to stop watching, with no way back short of
    unfollowing them — which throws away the artist too.

    Both directions are the same rule (:func:`desired_status`) applied to rows
    that already exist rather than to ones just discovered:

    * **Demote** — a ``wanted`` release the artist's settings no longer want
      becomes ``skipped``. Nothing else moves: ``queued`` and ``downloading``
      belong to the worker, ``downloaded`` is a fact about the disk, and
      ``failed`` is a record of an attempt rather than an intention.
    * **Restore** — a ``skipped`` release becomes ``wanted`` again when the
      artist's settings want it. Two exclusions, and each is somebody's
      decision being preserved rather than an optimisation:

      1. ``Album.monitored`` false means a **person** ignored this one release
         from a screen. That is not ours to reverse, and it is exactly what
         distinguishes it from a release this cascade demoted, which is why the
         demotion above leaves the flag alone.
      2. A duplicate edition stays skipped. ``Indexer._dedupe_editions`` keeps
         one edition per release, so promoting the whole group would put three
         copies of one album back in the backlog and hand somebody three
         chances to download the wrong one. The winner is chosen by the same
         :func:`dedupe_key` / :func:`edition_rank` pair the deduper uses, and a
         group with a release already held (or being fetched) promotes nothing.

    **Demotion is keyed on :func:`desired_status`, not on ``monitored``.** It
    was the flag for as long as unmonitoring was the only way to stop wanting
    things, and that left the other two settings able to strand a backlog in
    exactly the way this function exists to prevent: switching an artist to
    ``monitor_mode='none'`` marked nothing new but demoted nothing old, and
    narrowing ``accepted_release_types`` left every release of a dropped type
    wanted for good. Both are silent, because the indexer re-derives the status
    of *newly discovered* albums only — nothing revisits a row once it has one.
    One real library reached 4,693 wanted releases that way, every one of them
    under an artist whose mode was already ``none``.

    Which of the two paths an artist takes is a performance decision and
    nothing else. The first two clauses of :func:`desired_status` — not
    monitored, and mode ``none`` — do not read the album at all, so every
    ``wanted`` row goes, and that is one UPDATE. The other two are per-album, so
    those artists have their rows loaded. Both answer what
    :func:`desired_status` answers; the split only decides whether four thousand
    ORM objects have to exist to hear it.

    Flushes, never commits — the caller owns the transaction this rides out on.
    """
    change = BacklogChange()
    # Artists whose settings reject *every* release whatever it is. Reading the
    # first two clauses of `desired_status` rather than re-stating them: add a
    # third album-independent refusal there and this keeps up on its own.
    blanket = [str(a.id) for a in artists if wants_nothing(a)]
    selective = {str(a.id): a for a in artists if not wants_nothing(a)}

    if blanket:
        # One statement, not one per release. The library that produced this
        # feature had four thousand rows to move, and loading four thousand ORM
        # objects to set one column on each of them is the difference between a
        # press that lands and a press that looks broken. `synchronize_session`
        # is what keeps any of those rows the caller is already holding from
        # going stale behind the update.
        result = await session.execute(
            update(Album)
            .where(Album.artist_id.in_(blanket), Album.status == AlbumStatus.WANTED)
            .values(status=AlbumStatus.SKIPPED)
            .execution_options(synchronize_session="fetch")
        )
        change.demoted = int(result.rowcount or 0)

    if selective:
        rows = (
            (
                await session.execute(
                    select(Album).where(Album.artist_id.in_(list(selective)))
                )
            )
            .unique()
            .scalars()
            .all()
        )

        # Demote before grouping, so the restore below reads post-demotion
        # statuses. The other order is not merely untidy: a group holding one
        # newly-unwanted `wanted` release looks "already represented" to the
        # restore test, which skips the group, and only then does the demotion
        # empty it — leaving a group of skipped releases that a *second* call
        # would promote. Doing it this way makes one call idempotent, which is
        # what stops a repeated bulk edit walking a backlog up and down.
        for album in rows:
            artist = selective.get(str(album.artist_id))
            if artist is None or album.status is not AlbumStatus.WANTED:
                continue
            if desired_status(artist, album)[0] is AlbumStatus.SKIPPED:
                album.status = AlbumStatus.SKIPPED
                change.demoted += 1

        # Grouped per artist first: two artists can hold releases that
        # normalise to the same title, and they are not editions of each other.
        groups: dict[tuple[str, str], list[Album]] = {}
        for album in rows:
            # A title that normalises to nothing is its own group: lumping
            # every untitled release together would promote one and strand the
            # rest, which is not what "duplicate edition" means.
            key = dedupe_key(album.title) or f"\x00{album.id}"
            groups.setdefault((str(album.artist_id), key), []).append(album)

        for (artist_id, _key), members in groups.items():
            artist = selective.get(artist_id)
            if artist is None:
                continue
            # Anything but skipped means this release is already represented —
            # held, being fetched, wanted, or a failure somebody can retry —
            # and a second edition beside it is a second chance to download the
            # wrong one.
            if any(album.status is not AlbumStatus.SKIPPED for album in members):
                continue
            candidates = [
                album
                for album in members
                if album.status is AlbumStatus.SKIPPED
                and album.monitored
                and desired_status(artist, album)[0] is AlbumStatus.WANTED
            ]
            if not candidates:
                continue
            winner = max(candidates, key=edition_rank)
            winner.status = AlbumStatus.WANTED
            change.restored += 1

    if change.touched:
        await session.flush()
    return change


async def ensure_queue_item(
    session: AsyncSession, album: Album, *, priority: int = 0
) -> QueueItem | None:
    """Create a pending :class:`~app.models.QueueItem` for *album* if needed.

    Returns the new item, or ``None`` when the album already has a queue entry
    that is still pending or active (so re-running the indexer never piles up
    duplicate work).
    """
    existing = await session.execute(
        select(QueueItem.id)
        .where(
            QueueItem.album_id == str(album.id),
            QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE)),
        )
        .limit(1)
    )
    if existing.first() is not None:
        return None

    item = QueueItem(
        album_id=str(album.id),
        state=QueueState.PENDING,
        priority=priority,
        progress_tracks_total=album.tracks_count or 0,
    )
    session.add(item)
    return item


@dataclass(slots=True)
class IndexResult:
    """Outcome of checking a single artist."""

    artist_id: str
    artist_name: str = ""
    checked_at: datetime = field(default_factory=_now)
    duration_seconds: float = 0.0
    releases_seen: int = 0
    new_albums: int = 0
    updated_albums: int = 0
    wanted_album_ids: list[str] = field(default_factory=list)
    skipped_album_ids: list[str] = field(default_factory=list)
    deduped_album_ids: list[str] = field(default_factory=list)
    queued: int = 0
    errors: list[str] = field(default_factory=list)

    @property
    def wanted(self) -> int:
        """Number of albums that became ``wanted`` during this check."""
        return len(self.wanted_album_ids)

    @property
    def skipped(self) -> int:
        """Number of albums that were recorded but deliberately not wanted."""
        return len(self.skipped_album_ids)

    @property
    def deduped(self) -> int:
        """Number of albums demoted because a better edition exists."""
        return len(self.deduped_album_ids)

    @property
    def ok(self) -> bool:
        """True when the check completed without any recorded error."""
        return not self.errors

    def summary(self) -> str:
        """One-line human summary, used for the activity feed and logs."""
        return (
            f"{self.releases_seen} release(s) seen, {self.new_albums} new, "
            f"{self.wanted} wanted, {self.queued} queued, {self.skipped} skipped"
            + (f", {self.deduped} duplicate edition(s)" if self.deduped else "")
            + (f", {len(self.errors)} error(s)" if self.errors else "")
        )

    def as_dict(self) -> dict[str, Any]:
        """Plain-dict form for the JSON API."""
        return {
            "artist_id": self.artist_id,
            "artist_name": self.artist_name,
            "checked_at": self.checked_at,
            "duration_seconds": round(self.duration_seconds, 3),
            "releases_seen": self.releases_seen,
            "new_albums": self.new_albums,
            "updated_albums": self.updated_albums,
            "wanted": self.wanted,
            "wanted_album_ids": list(self.wanted_album_ids),
            "skipped": self.skipped,
            "deduped": self.deduped,
            "queued": self.queued,
            "errors": list(self.errors),
            "summary": self.summary(),
        }


class Indexer:
    """Discovers new releases for followed artists, slowly and politely.

    Args:
        client: The shared :class:`~app.qobuz.client.QobuzClient`.  Only a small
            duck-typed surface is used (``get_artist``, ``iter_artist_albums``
            and optionally ``add_favorite_artist``), which is what makes the
            unit tests able to inject a fake.
        settings: Effective settings; defaults to :func:`app.config.get_settings`.
        random_func: Source of randomness for interval jitter, injectable so
            tests are deterministic.

    Concurrency:
        A single :class:`asyncio.Lock` serialises :meth:`check_artist`,
        :meth:`tick` and :meth:`refresh_all_now`.  The indexer therefore only
        ever talks to Qobuz about **one artist at a time**; a tick that arrives
        while another is still running is dropped rather than queued.
    """

    def __init__(
        self,
        client: Any,
        *,
        settings: Settings | None = None,
        random_func: Callable[[], float] = random.random,
        enricher: Any = None,
    ) -> None:
        self._client = client
        self._settings = settings or get_settings()
        self._random = random_func
        # Optional and duck-typed: the indexer only ever calls ``mark_due`` on
        # it, which is pure SQL. Enrichment must not make a request from inside
        # the indexer's transaction — see ``Enricher.mark_due``.
        self._enricher = enricher
        self._lock = asyncio.Lock()
        self._paused_until: datetime | None = None
        self._running = False
        self._current_artist_id: str | None = None
        self._current_artist_name: str | None = None
        self._last_run_at: datetime | None = None
        self._last_result: IndexResult | None = None

    # ------------------------------------------------------------------ state
    @property
    def settings(self) -> Settings:
        """The settings this indexer was built with."""
        return self._settings

    @property
    def client(self) -> Any:
        """The Qobuz client this indexer was built with."""
        return self._client

    @property
    def enabled(self) -> bool:
        """Whether the indexer is allowed to run at all (``INDEXER_ENABLED``)."""
        return bool(self._settings.indexer_enabled)

    @property
    def running(self) -> bool:
        """True while an artist check is in flight."""
        return self._running

    @property
    def paused(self) -> bool:
        """True while a manual pause (see :meth:`pause`) is in effect."""
        until = self._paused_until
        return until is not None and until > _now()

    @property
    def paused_until(self) -> datetime | None:
        """When the current pause expires, or ``None`` when not paused."""
        return self._paused_until if self.paused else None

    @property
    def current_artist_id(self) -> str | None:
        """Id of the artist being checked right now, if any."""
        return self._current_artist_id

    @property
    def current_artist_name(self) -> str | None:
        """Name of the artist being checked right now, if any."""
        return self._current_artist_name

    @property
    def last_run_at(self) -> datetime | None:
        """When the most recent check finished."""
        return self._last_run_at

    @property
    def last_result(self) -> IndexResult | None:
        """Result of the most recent check, for the dashboard."""
        return self._last_result

    def pause(self, seconds: float) -> None:
        """Stop picking up new work for *seconds* (used by the circuit breaker)."""
        self._paused_until = _now() + timedelta(seconds=max(0.0, seconds))
        logger.warning("Indexer paused for %.0fs (until %s)", seconds, self._paused_until)

    def resume(self) -> None:
        """Clear a pause set by :meth:`pause`."""
        self._paused_until = None

    # ------------------------------------------------------------- pacing math
    def artist_period_seconds(self, monitored_count: int) -> float:
        """Seconds an artist must wait before being eligible for a re-check.

        ``max(INDEXER_ARTIST_INTERVAL, full_sweep_seconds / artists)`` — see the
        module docstring for why the floor is what makes a large library slow
        down rather than speed up.
        """
        interval = float(max(1, self._settings.indexer_artist_interval))
        sweep = float(max(0, self._settings.indexer_full_sweep_hours)) * 3600.0
        return max(interval, sweep / float(max(1, monitored_count)))

    def full_cycle_seconds(self, monitored_count: int) -> float:
        """Seconds needed to visit every monitored artist once, one per tick."""
        return self.artist_period_seconds(monitored_count) * max(1, monitored_count)

    def jittered(self, seconds: float) -> float:
        """Apply +/-``INDEXER_JITTER_PCT`` jitter to *seconds* (never negative)."""
        pct = min(max(float(self._settings.indexer_jitter_pct), 0.0), 1.0)
        if pct <= 0.0:
            return max(0.0, seconds)
        factor = 1.0 + ((self._random() * 2.0) - 1.0) * pct
        return max(0.0, seconds * factor)

    # ------------------------------------------------------------- DB helpers
    async def monitored_count(self, session: AsyncSession) -> int:
        """Number of artists flagged ``monitored``."""
        result = await session.execute(
            select(func.count()).select_from(Artist).where(Artist.monitored.is_(True))
        )
        return int(result.scalar_one() or 0)

    async def next_due(self, session: AsyncSession) -> datetime | None:
        """When the next artist becomes eligible for a check.

        ``None`` when no artist is monitored.  Returns "now" when at least one
        monitored artist has never been checked.  This is a coroutine (it must
        query the database) — ``await indexer.next_due(session)``.
        """
        count = await self.monitored_count(session)
        if not count:
            return None

        never_checked = await session.execute(
            select(func.count())
            .select_from(Artist)
            .where(Artist.monitored.is_(True), Artist.last_checked_at.is_(None))
        )
        now = _now()
        if int(never_checked.scalar_one() or 0):
            return now

        oldest = await session.execute(
            select(func.min(Artist.last_checked_at)).where(Artist.monitored.is_(True))
        )
        stamp = utc(oldest.scalar_one())
        if stamp is None:
            return now
        due = stamp + timedelta(seconds=self.artist_period_seconds(count))
        return max(due, now) if due < now else due

    async def _pick_due_artist(self, session: AsyncSession) -> Artist | None:
        """Return the single least-recently-checked artist that is due, if any."""
        count = await self.monitored_count(session)
        if not count:
            return None

        cutoff = _now() - timedelta(seconds=self.jittered(self.artist_period_seconds(count)))
        result = await session.execute(
            select(Artist)
            .where(
                Artist.monitored.is_(True),
                (Artist.last_checked_at.is_(None)) | (Artist.last_checked_at <= cutoff),
            )
            .order_by(Artist.last_checked_at.asc().nullsfirst(), Artist.id.asc())
            .limit(1)
        )
        return result.scalars().first()

    # ---------------------------------------------------------------- actions
    async def tick(self, session: AsyncSession) -> IndexResult | None:
        """Check at most **one** artist.

        Picks the least-recently-checked monitored artist whose
        ``last_checked_at`` is older than the computed per-artist period (with
        jitter) and indexes it.  Returns ``None`` — doing nothing at all — when
        the indexer is disabled, paused, already busy, or nothing is due yet.
        """
        if not self.enabled or self.paused:
            return None
        if self._lock.locked():
            logger.debug("Indexer tick skipped: a check is already running")
            return None

        async with self._lock:
            artist = await self._pick_due_artist(session)
            if artist is None:
                logger.debug("Indexer tick: no artist due")
                return None
            return await self._check_artist_locked(session, artist)

    async def check_artist(self, session: AsyncSession, artist_id: str) -> IndexResult:
        """Index a single artist now, regardless of when it was last checked.

        Args:
            session: Session to work in; it is committed before returning.
            artist_id: Qobuz artist id of a followed artist (coerced to ``str``).

        Raises:
            UnknownArtistError: when the artist is not in the database.
        """
        artist = await session.get(Artist, str(artist_id))
        if artist is None:
            raise UnknownArtistError(f"Artist {artist_id!r} is not followed")
        async with self._lock:
            return await self._check_artist_locked(session, artist)

    async def refresh_all_now(self, session: AsyncSession) -> list[IndexResult]:
        """Check every monitored artist, sequentially and still rate-limited.

        Artists are visited least-recently-checked first.  The global limiter
        still applies to every request, so with the default profile this takes a
        long time by design; it is meant to be triggered manually and left to
        run in the background.
        """
        result = await session.execute(
            select(Artist)
            .where(Artist.monitored.is_(True))
            .order_by(Artist.last_checked_at.asc().nullsfirst(), Artist.id.asc())
        )
        artists = list(result.scalars().all())
        results: list[IndexResult] = []

        async with self._lock:
            for artist in artists:
                results.append(await self._check_artist_locked(session, artist))

        logger.info("Manual refresh finished for %d artist(s)", len(results))
        return results

    async def add_artist(
        self,
        session: AsyncSession,
        qobuz_artist_id: str,
        monitored: bool = True,
        *,
        name: str | None = None,
        monitor_mode: MonitorMode | str | None = None,
        quality_profile: str | None = None,
        accepted_release_types: Sequence[str] | None = None,
        index_now: bool = True,
        payload: Mapping[str, Any] | None = None,
    ) -> Artist:
        """Follow a Qobuz artist and import their back catalogue.

        The artist row is created (or updated in place when already followed)
        from ``artist/get``, then — unless ``index_now`` is false — the full
        catalogue is indexed immediately.  That initial import is what gives
        ``monitor_mode="future"`` its baseline: everything already released is
        recorded as ``skipped``, so only genuinely new releases become wanted.

        Args:
            session: Session to work in; committed before returning.
            qobuz_artist_id: The Qobuz artist id.
            monitored: Whether the indexer should keep re-checking this artist.
            name: Fallback display name used when ``artist/get`` fails.
            monitor_mode: Defaults to ``DEFAULT_MONITOR_MODE``.
            quality_profile: Defaults to ``DEFAULT_QUALITY_PROFILE``.
            accepted_release_types: Defaults to ``DEFAULT_ACCEPTED_RELEASE_TYPES``.
            index_now: Run the initial catalogue import (default ``True``).
            payload: A raw Qobuz artist object the caller already holds, which
                skips the ``artist/get`` call entirely.  Search results carry
                every field this needs, so a bulk import that has just searched
                for an artist costs one API call rather than two — the
                difference between 8 and 17 minutes for 500 artists.
        """
        artist_id = str(qobuz_artist_id)
        mapped: dict[str, Any] = {"id": artist_id, "name": name or "", "image_url": None,
                                  "albums_count": 0, "qobuz_slug": None}
        if payload is not None:
            supplied = map_artist(payload)
            if supplied.get("id"):
                mapped = supplied
        else:
            try:
                fetched_payload = await self._client.get_artist(artist_id, with_albums=False)
                fetched = map_artist(fetched_payload)
                if fetched.get("id"):
                    mapped = fetched
            except QobuzError as exc:
                if not name:
                    raise
                logger.warning(
                    "artist/get failed for %s, using supplied name: %s", artist_id, exc
                )

        artist = await session.get(Artist, artist_id)
        created = artist is None
        if artist is None:
            artist = Artist(id=artist_id)
            session.add(artist)
            artist.monitor_mode = _coerce_monitor_mode(
                monitor_mode, self._settings.default_monitor_mode
            )
            artist.quality_profile = quality_profile or self._settings.default_quality_profile
            artist.set_accepted_release_types(
                list(accepted_release_types)
                if accepted_release_types
                else self._settings.accepted_release_type_defaults
            )
        else:
            if monitor_mode is not None:
                artist.monitor_mode = _coerce_monitor_mode(monitor_mode, "all")
            if quality_profile is not None:
                artist.quality_profile = quality_profile
            if accepted_release_types is not None:
                artist.set_accepted_release_types(list(accepted_release_types))

        artist.name = mapped.get("name") or name or artist.name or "Unknown Artist"
        artist.image_url = mapped.get("image_url") or artist.image_url
        artist.albums_count = int(mapped.get("albums_count") or artist.albums_count or 0)
        artist.qobuz_slug = mapped.get("qobuz_slug") or artist.qobuz_slug
        artist.monitored = bool(monitored)

        await session.flush()
        session.add(
            Activity(
                level=ActivityLevel.INFO,
                event="artist.added" if created else "artist.updated",
                message=(
                    f"{'Now following' if created else 'Updated'} {artist.name} "
                    f"(mode={artist.monitor_mode.value}, "
                    f"types={join_release_types(artist.accepted_release_types_list)})"
                ),
                artist_id=artist.id,
            )
        )
        await session.commit()

        if created and self._settings.qobuz_favorite_sync:
            await self._sync_favorite(artist_id)

        if index_now:
            try:
                await self.check_artist(session, artist_id)
            except QobuzError as exc:
                logger.warning("Initial index of %s failed: %s", artist.name, exc)
                await self._log(
                    session,
                    ActivityLevel.WARNING,
                    "indexer.error",
                    f"Initial index of {artist.name} failed: {exc}",
                    artist_id=artist_id,
                )
                await session.commit()

        return artist

    async def _sync_favorite(self, artist_id: str) -> None:
        """Best-effort Qobuz-side favourite, when ``QOBUZ_FAVORITE_SYNC`` is on."""
        adder = getattr(self._client, "add_favorite_artist", None)
        if adder is None:
            return
        try:
            await adder(artist_id)
        except QobuzError as exc:
            logger.warning("Could not favourite artist %s on Qobuz: %s", artist_id, exc)

    async def queue_wanted(self, session: AsyncSession, artist: Artist) -> int:
        """Explicitly queue every ``wanted`` album of *artist* for download.

        This is the opt-in counterpart to the automatic queueing that
        ``AUTO_DOWNLOAD`` disables: it always queues, whatever that setting says,
        because the user asked for it directly.

        Returns:
            The number of albums newly queued.
        """
        result = IndexResult(artist_id=str(artist.id), artist_name=artist.name)
        queued = await self._enqueue_wanted(session, artist, result, force=True)
        await session.commit()
        logger.info("Queued %d wanted album(s) for %s", queued, artist.name)
        return queued

    # -------------------------------------------------------------- internals
    async def _check_artist_locked(
        self, session: AsyncSession, artist: Artist
    ) -> IndexResult:
        """Do the actual work for one artist. The instance lock must be held."""
        started = time.monotonic()
        result = IndexResult(artist_id=str(artist.id), artist_name=artist.name)
        self._running = True
        self._current_artist_id = str(artist.id)
        self._current_artist_name = artist.name
        logger.info("Indexing artist %s (%s)", artist.name, artist.id)

        try:
            raw_albums = await self._fetch_releases(artist, result)
            touched = await self._upsert_albums(session, artist, raw_albums, result)
            await session.flush()
            await self._dedupe_editions(session, artist, result)
            await session.flush()
            await self._enqueue_wanted(session, artist, result)

            artist.last_checked_at = _now()
            if result.new_albums or result.updated_albums:
                artist.albums_count = max(artist.albums_count, len(touched))

            level = ActivityLevel.WARNING if result.errors else ActivityLevel.INFO
            await self._log(
                session,
                level,
                "indexer.checked",
                f"Checked {artist.name}: {result.summary()}",
                artist_id=artist.id,
            )
            await session.commit()
        except QobuzError as exc:
            # Captured before the rollback. ``rollback()`` expires every instance
            # in the session whatever ``expire_on_commit`` says, so reading
            # ``artist.id`` afterwards fires a lazy reload on an async session
            # and raises MissingGreenlet — from inside the handler whose job is
            # to stamp ``last_checked_at``. Without the stamp ``_pick_due_artist``
            # keeps choosing the same artist, so one unreachable artist stalls
            # the whole sweep.
            artist_id = result.artist_id or str(artist.id)
            await session.rollback()
            result.errors.append(str(exc))
            artist = await session.get(Artist, artist_id) or artist
            artist.last_checked_at = _now()
            await self._log(
                session,
                ActivityLevel.ERROR,
                "indexer.error",
                f"Failed to check {result.artist_name}: {exc}",
                artist_id=result.artist_id,
            )
            await session.commit()
            logger.warning("Indexing %s failed: %s", result.artist_name, exc)
        finally:
            result.duration_seconds = time.monotonic() - started
            result.checked_at = _now()
            self._running = False
            self._current_artist_id = None
            self._current_artist_name = None
            self._last_run_at = result.checked_at
            self._last_result = result

        logger.info("Indexed %s: %s", result.artist_name, result.summary())
        return result

    async def _fetch_releases(
        self, artist: Artist, result: IndexResult
    ) -> list[dict[str, Any]]:
        """Pull every release Qobuz knows about for *artist* (rate-limited)."""
        accepted = artist.accepted_release_types_list or None
        raw_albums: list[dict[str, Any]] = []
        async for raw in self._client.iter_artist_albums(str(artist.id), accepted):
            raw_albums.append(raw)
        result.releases_seen = len(raw_albums)
        return raw_albums

    async def _upsert_albums(
        self,
        session: AsyncSession,
        artist: Artist,
        raw_albums: Iterable[dict[str, Any]],
        result: IndexResult,
    ) -> list[Album]:
        """Insert or refresh album rows, deciding the status of new ones."""
        touched: list[Album] = []
        pending_activity: list[tuple[ActivityLevel, str, str, str]] = []
        # Albums created in this loop are not in the identity map until the
        # session is flushed, so a repeated id would otherwise be inserted twice.
        created_here: dict[str, Album] = {}
        # Albums whose release type a consensus of open sources already
        # corrected. Qobuz's own value is a guess for these — its mapper falls
        # back to counting tracks — so re-applying it every tick would undo the
        # correction and oscillate forever.
        enriched = await _release_types_applied_for(session, str(artist.id))

        for raw in raw_albums:
            mapped = map_album(raw, str(artist.id))
            album_id = mapped.get("id")
            if not album_id:
                logger.debug("Skipping unusable album payload for artist %s", artist.id)
                continue

            album = created_here.get(str(album_id)) or await session.get(Album, str(album_id))
            if album is None:
                album = Album(**mapped)
                album.monitored = artist.monitored
                status, reason = self._status_for_new_album(artist, album)
                album.status = status
                session.add(album)
                created_here[str(album.id)] = album
                result.new_albums += 1
                if status is AlbumStatus.WANTED:
                    result.wanted_album_ids.append(str(album.id))
                    pending_activity.append(
                        (
                            ActivityLevel.INFO,
                            "album.wanted",
                            f"New release wanted: {artist.name} - {album.display_title}"
                            + (f" ({album.year})" if album.year else ""),
                            str(album.id),
                        )
                    )
                else:
                    result.skipped_album_ids.append(str(album.id))
                    pending_activity.append(
                        (
                            ActivityLevel.DEBUG,
                            "album.skipped",
                            f"Skipped {artist.name} - {album.display_title}: {reason}",
                            str(album.id),
                        )
                    )
            else:
                protected = _PROTECTED_ALBUM_FIELDS
                if str(album.id) in enriched:
                    protected = protected | {"release_type"}
                if _apply_metadata(album, mapped, protected=protected):
                    result.updated_albums += 1
            touched.append(album)

        await session.flush()
        for level, event, message, album_id in pending_activity:
            await self._log(
                session, level, event, message, artist_id=str(artist.id), album_id=album_id
            )
        await self._mark_for_enrichment(session, artist, touched)
        return touched

    async def _mark_for_enrichment(
        self, session: AsyncSession, artist: Artist, albums: list[Album]
    ) -> None:
        """Queue the part of what we just wrote that is in the library.

        Discovery is no longer what makes a release enrichable — being on disk
        is. Following one prolific artist writes a thousand albums nobody owns,
        and enriching those is the work that starved the thirty that mattered, so
        ``Enricher.mark_due`` drops everything outside ``library_scope()`` and
        this call is left with the releases this artist already has downloaded.
        Which is still worth making: re-indexing is where a corrected UPC or a
        changed track count arrives, and those are exactly the inputs a match is
        derived from. What replaced the old behaviour is a mark at the moment an
        album *lands* — see ``enricher.mark_library_due``.

        What it does **not** do is fetch anything. The indexer holds a write
        transaction here, SQLite has one writer, and a lookup made from inside it
        would block the download worker's per-track commits until they timed out
        and marked a live download failed. Queueing is SQL; fetching happens a
        moment later from the enricher's own tick.

        A failure is swallowed: enrichment is a nicety, and indexing must not
        break because a side table is unhappy.
        """
        if self._enricher is None:
            return
        try:
            from app.models import EnrichmentEntity  # noqa: PLC0415 - local to this hook

            await self._enricher.mark_due(
                session, EnrichmentEntity.ARTIST, [str(artist.id)]
            )
            if albums:
                await self._enricher.mark_due(
                    session,
                    EnrichmentEntity.ALBUM,
                    [str(album.id) for album in albums],
                )
        except Exception:  # noqa: BLE001 - never let enrichment break indexing
            logger.exception("Could not queue %s for enrichment", artist.id)

    def _status_for_new_album(
        self, artist: Artist, album: Album
    ) -> tuple[AlbumStatus, str]:
        """Decide the initial status of a newly discovered album."""
        return desired_status(artist, album)

    async def _dedupe_editions(
        self, session: AsyncSession, artist: Artist, result: IndexResult
    ) -> None:
        """Keep one edition per release; demote the rest to ``skipped``."""
        rows = await session.execute(
            select(Album).where(Album.artist_id == str(artist.id))
        )
        groups: dict[str, list[Album]] = {}
        for album in rows.scalars().all():
            groups.setdefault(dedupe_key(album.title), []).append(album)

        for key, members in groups.items():
            if len(members) < 2 or not key:
                continue

            # Only an edition Qobuzarr actually wants may win.  Ranking over
            # *every* member let an album that was already skipped (wrong
            # release type, or filtered out by the monitor mode) beat the one
            # genuinely wanted edition and demote it to `skipped` too, so the
            # release was never downloaded on this or any later scan.
            held = [a for a in members if a.status in _HELD_STATUSES]
            eligible = held or [
                a for a in members if a.status is not AlbumStatus.SKIPPED
            ]
            if not eligible:
                continue
            winner = max(eligible, key=edition_rank)
            for album in members:
                if album is winner or album.status in _UNTOUCHABLE_STATUSES:
                    continue
                if album.status is AlbumStatus.SKIPPED:
                    continue
                album.status = AlbumStatus.SKIPPED
                await _cancel_pending_queue_items(session, str(album.id))
                result.deduped_album_ids.append(str(album.id))
                if str(album.id) in result.wanted_album_ids:
                    result.wanted_album_ids.remove(str(album.id))
                await self._log(
                    session,
                    ActivityLevel.INFO,
                    "album.deduped",
                    (
                        f"Skipping duplicate edition {album.display_title} - "
                        f"keeping {winner.display_title}"
                    ),
                    artist_id=str(artist.id),
                    album_id=str(album.id),
                )

    async def _enqueue_wanted(
        self,
        session: AsyncSession,
        artist: Artist,
        result: IndexResult,
        *,
        force: bool = False,
    ) -> int:
        """Give every wanted album of this artist a pending queue item.

        Downloading is opt-in: unless ``AUTO_DOWNLOAD`` is enabled, discovered
        releases are left at ``wanted`` and nothing is queued. The user then
        starts them explicitly (per-album button, "Download wanted", or the
        JSON API), which calls this with ``force=True``.

        Args:
            force: Queue regardless of the ``auto_download`` setting.

        Returns:
            The number of albums newly queued.
        """
        if not force and not self._settings.auto_download:
            pending = await session.scalar(
                select(func.count())
                .select_from(Album)
                .where(
                    Album.artist_id == str(artist.id),
                    Album.status == AlbumStatus.WANTED,
                    Album.monitored.is_(True),
                )
            )
            if pending:
                logger.info(
                    "auto_download is off: %d wanted album(s) for %s left unqueued",
                    pending,
                    artist.name,
                )
                await self._log(
                    session,
                    ActivityLevel.INFO,
                    "queue.held",
                    f"{pending} wanted release(s) for {artist.name} are waiting for "
                    f"you to start them (AUTO_DOWNLOAD is off)",
                    artist_id=str(artist.id),
                )
            return 0

        queued = 0
        rows = await session.execute(
            select(Album).where(
                Album.artist_id == str(artist.id),
                Album.status == AlbumStatus.WANTED,
                Album.monitored.is_(True),
            )
        )
        for album in rows.scalars().all():
            item = await ensure_queue_item(session, album)
            if item is None:
                continue
            result.queued += 1
            queued += 1
            await self._log(
                session,
                ActivityLevel.INFO,
                "queue.added",
                f"Queued {artist.name} - {album.display_title}",
                artist_id=str(artist.id),
                album_id=str(album.id),
            )
        return queued

    async def _log(
        self,
        session: AsyncSession,
        level: ActivityLevel,
        event: str,
        message: str,
        *,
        artist_id: str | None = None,
        album_id: str | None = None,
    ) -> None:
        """Append one row to the activity feed (flushing FK targets first)."""
        await session.flush()
        session.add(
            Activity(
                level=level,
                event=event,
                message=message,
                artist_id=artist_id,
                album_id=album_id,
            )
        )

    # ----------------------------------------------------------------- status
    async def status(self, session: AsyncSession | None = None) -> dict[str, Any]:
        """Snapshot shaped like :class:`app.schemas.IndexerStatusOut`.

        Pass a session to include the database-derived counts; without one the
        in-memory fields are still filled in.
        """
        data: dict[str, Any] = {
            "enabled": self.enabled,
            "running": self.running,
            "paused": self.paused,
            "artist_interval_seconds": self._settings.indexer_artist_interval,
            "full_sweep_hours": self._settings.indexer_full_sweep_hours,
            "next_run_at": None,
            "seconds_until_next_run": None,
            "current_artist_id": self.current_artist_id,
            "current_artist_name": self.current_artist_name,
            "last_run_at": self.last_run_at,
            "monitored_artists": 0,
            "artists_never_checked": 0,
        }
        if session is None:
            return data

        data["monitored_artists"] = await self.monitored_count(session)
        never = await session.execute(
            select(func.count())
            .select_from(Artist)
            .where(Artist.monitored.is_(True), Artist.last_checked_at.is_(None))
        )
        data["artists_never_checked"] = int(never.scalar_one() or 0)

        due = await self.next_due(session)
        if due is not None:
            data["next_run_at"] = due
            data["seconds_until_next_run"] = max(0.0, (due - _now()).total_seconds())
        return data


def _coerce_monitor_mode(value: MonitorMode | str | None, default: str) -> MonitorMode:
    """Turn a mode name (or ``None``) into a :class:`MonitorMode` member."""
    candidate = value if value is not None else default
    if isinstance(candidate, MonitorMode):
        return candidate
    try:
        return MonitorMode(str(candidate).strip().lower())
    except ValueError:
        logger.warning("Unknown monitor mode %r, falling back to 'all'", candidate)
        return MonitorMode.ALL


async def _release_types_applied_for(session: AsyncSession, artist_id: str) -> set[str]:
    """Album ids whose release type an enrichment consensus already corrected.

    One statement per artist, and it returns an empty set when the enrichment
    tables are empty — which is the normal case for anyone who has enrichment
    switched off.
    """
    from app.models import AlbumMetadata  # noqa: PLC0415 - keeps the import graph flat

    rows = await session.execute(
        select(AlbumMetadata.album_id)
        .join(Album, Album.id == AlbumMetadata.album_id)
        .where(
            Album.artist_id == artist_id,
            AlbumMetadata.release_type_applied_at.is_not(None),
        )
    )
    return {str(value) for value in rows.scalars().all()}


def _apply_metadata(
    album: Album,
    mapped: dict[str, Any],
    *,
    protected: frozenset[str] | set[str] = _PROTECTED_ALBUM_FIELDS,
) -> bool:
    """Copy refreshed Qobuz metadata onto an existing album row.

    Local state (``status``, ``path``, ``downloaded_at``, ...) is never touched.
    *protected* widens that set: the caller adds ``release_type`` for albums an
    enrichment consensus has corrected, so Qobuz's guess cannot overwrite it back
    on the next tick.

    Returns ``True`` when anything actually changed.
    """
    changed = False
    for key, value in mapped.items():
        if key in protected or value is None:
            continue
        if getattr(album, key, None) != value:
            setattr(album, key, value)
            changed = True
    return changed


async def _cancel_pending_queue_items(session: AsyncSession, album_id: str) -> None:
    """Cancel any pending queue entry for an album we no longer want."""
    rows = await session.execute(
        select(QueueItem).where(
            QueueItem.album_id == str(album_id),
            QueueItem.state == QueueState.PENDING,
        )
    )
    for item in rows.scalars().all():
        item.state = QueueState.CANCELLED
        item.finished_at = _now()
        item.last_error = "Superseded by a better edition"
