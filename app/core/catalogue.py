"""Backfill catalogue fields Qobuz publishes but does not list.

One field, one call, one reason. ``artist/getReleasesList`` — the endpoint
:class:`~app.core.indexer.Indexer` builds every album row from — does not carry
``upc``. ``album/get`` does, and the only caller that reaches it is
:meth:`~app.core.downloader.AlbumDownloader.download_album`, through
``_ALBUM_REFRESH_FIELDS``. So a release Qobuzarr *fetched* has a barcode and a
release it merely *knows about* does not, and on a library assembled by the disk
scan that is almost all of them: measured on a real database, 15 of 422 releases
on disk carried a ``upc``, and across the whole 16,549-row catalogue those same
15 were the only ones. Every one of the other 407 was a release whose barcode
Qobuz holds and Qobuzarr had never asked for.

That gap is not cosmetic, because a barcode is the key the enrichment ladder is
built on. :func:`app.enrich.matching.barcode_candidates` reads three fields and
``upc`` is the most trusted of them; without it Deezer and MusicBrainz can only
match a release by browsing an artist's discography, and the artist id is itself
derived from a release that matched on a barcode. 480 stranded ``no_key`` rows
on that library said so in as many words: "no barcode on this release and no …
artist to browse".

**Bounded, explicit, and never on a timer.** This is one ``album/get`` per
release, so it is the same shape as the credit lookup in
:mod:`app.api.routes_api`: capped per invocation, asked for by a person, and
never reached by the indexer or a scheduled job. Following one prolific artist
puts thousands of releases in ``albums`` and none of them is on disk; running
this over the catalogue would spend a day of the account's request budget on
releases nobody owns. Scope is therefore the library — ``status == DOWNLOADED``,
the same definition :func:`app.core.enricher.library_scope` uses, and for the
same reason.

**It fills, it does not refresh.** A release that already has a ``upc`` is
skipped rather than re-read, which makes the pass idempotent and cheap to repeat
after new albums are adopted. And ``upc`` is the *only* column written, though
``album/get`` returns everything ``_ALBUM_REFRESH_FIELDS`` lists. Two of those
are somebody else's: ``release_type`` is the one column enrichment may write on
``albums`` and :meth:`Enricher._apply_metadata` protects it for enriched albums
precisely so the indexer cannot stomp a consensus verdict back and oscillate,
while ``label`` and ``genre`` are naming-template tokens, so a "better" value
makes :func:`app.core.librarian.plan_refile` want to move every folder they
appear in. A backfill that quietly re-filed a library would be a much larger
action than the one being asked for.

(``upc`` is itself a naming token. No default template renders it — the shipped
one is ``{artist}/{album} ({year})[ [{quality}]]/…`` — but a template that does
would start rendering a value where it previously rendered nothing, which is a
re-file. Worth knowing before running this against a customised template.)

**Nothing here touches ``enrichment_state``.** The rows this unblocks are
``no_key`` with ``next_attempt_at = NULL``, and the thing that re-opens them is
:meth:`app.core.enricher.Enricher._rearm_barcoded`, which asks each tick whether
any stranded album has since gained a usable barcode. That is deliberate and it
is the codebase's standing preference: a state-based repair is idempotent, works
on rows stranded before it shipped, and cannot be missed by being in the wrong
place when an event fired. Seeding work from here would be the event-based
version of the same fix, and strictly worse.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Mapping, Sequence

from sqlalchemy import or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Album, AlbumStatus

logger = logging.getLogger(__name__)

__all__ = ["BackfillResult", "backfill_upc", "albums_missing_upc"]

#: Rows written per transaction. The pass is a long sequence of rate-limited
#: requests, so an interruption partway through is ordinary rather than
#: exceptional — committing in batches means a run that is stopped keeps what it
#: has learned, exactly as the download loop's per-track commits do.
_COMMIT_EVERY = 25


@dataclass(slots=True)
class BackfillResult:
    """What one backfill pass did. Counts are reported, so they are honest.

    ``filled`` and ``usable`` are kept apart on purpose. Qobuz publishes the
    ``upc`` field as free text and a value can arrive that is stored faithfully
    and is still not a barcode — :func:`app.enrich.matching.normalize_barcode`
    is what decides, and it is the same test the enrichment ladder will apply.
    Reporting only ``filled`` would promise unblocked matching that a malformed
    value never delivers.
    """

    candidates: int = 0
    """Releases in scope that had no ``upc`` when the pass started."""

    examined: int = 0
    """Releases actually asked about — ``candidates`` capped by ``limit``."""

    filled: int = 0
    """Releases that gained a ``upc``."""

    usable: int = 0
    """Of *filled*, the ones whose value is a barcode a match key can use."""

    absent: int = 0
    """Releases Qobuz answered about and published no ``upc`` for."""

    failed: int = 0
    """Releases whose lookup raised. The row is left exactly as it was."""

    errors: list[str] = field(default_factory=list)

    @property
    def remaining(self) -> int:
        """Releases still without a ``upc`` when this pass stopped."""
        return max(0, self.candidates - self.filled)


def albums_missing_upc() -> Any:
    """In-scope releases with no ``upc``, as a ``SELECT`` of ids.

    A ``SELECT`` rather than a list for the reason
    :func:`app.core.enricher.library_scope` gives: callers want it as a
    subquery, evaluated inside the statement it constrains, so it cannot go
    stale between being read and being used.
    """
    return select(Album.id).where(
        Album.status == AlbumStatus.DOWNLOADED,
        or_(Album.upc.is_(None), Album.upc == ""),
    )


async def backfill_upc(
    session: AsyncSession,
    client: Any,
    *,
    limit: int = 1000,
    dry_run: bool = False,
    progress: Callable[[int, int], Awaitable[None] | None] | None = None,
) -> BackfillResult:
    """Fetch ``album/get`` for in-scope releases with no ``upc`` and store it.

    Args:
        session: Session to read and write on. This function commits.
        client: The one :class:`~app.qobuz.client.QobuzClient` — reach for
            ``get_state().client`` or the CLI's context, never a fresh one. It
            carries the single rate limiter for the Qobuz host, and a second
            client would spend the same hourly budget twice while each believed
            it was within it.
        limit: Most releases to ask about in one pass. A cap rather than a
            default page size: the whole point is that this is bounded work
            somebody asked for.
        dry_run: Make the requests and report, write nothing.
        progress: Called as ``(done, total)`` after each release. May be a
            coroutine function.

    Returns:
        A :class:`BackfillResult`. Never raises for one release's failure — a
        release Qobuz refuses to answer about is counted and skipped, because
        stopping a 400-release pass on one bad row leaves the operator with a
        partial result and no idea which one it was.
    """
    from app.enrich.matching import normalize_barcode  # noqa: PLC0415 - one use

    result = BackfillResult()

    ids: Sequence[str] = [
        str(row) for row in (await session.execute(albums_missing_upc())).scalars()
    ]
    result.candidates = len(ids)
    targets = ids[: max(0, int(limit))]
    if not targets:
        return result

    logger.info(
        "Backfilling upc for %d of %d release(s) with none%s",
        len(targets),
        result.candidates,
        " (dry run)" if dry_run else "",
    )

    pending = 0
    for index, album_id in enumerate(targets, start=1):
        result.examined += 1
        try:
            payload = await client.get_album(album_id)
        except Exception as exc:  # noqa: BLE001 - one release must not stop the pass
            result.failed += 1
            result.errors.append(f"{album_id}: {exc}")
            logger.warning("album/get failed for %s: %s", album_id, exc)
        else:
            upc = _published_upc(payload)
            if upc is None:
                result.absent += 1
            else:
                result.filled += 1
                if normalize_barcode(upc) is not None:
                    result.usable += 1
                if not dry_run:
                    album = await session.get(Album, album_id)
                    # Re-checked rather than assumed: the list was read before
                    # the first request, and a download finishing mid-pass has
                    # written the same field from the same endpoint.
                    if album is not None and not (album.upc or "").strip():
                        album.upc = upc
                        pending += 1

        if pending >= _COMMIT_EVERY:
            await session.commit()
            pending = 0

        if progress is not None:
            outcome = progress(index, len(targets))
            if hasattr(outcome, "__await__"):
                await outcome

    if pending and not dry_run:
        await session.commit()
    elif dry_run:
        await session.rollback()

    logger.info(
        "upc backfill: %d filled (%d usable as a match key), %d not published, %d failed",
        result.filled,
        result.usable,
        result.absent,
        result.failed,
    )
    return result


def _published_upc(payload: Mapping[str, Any] | Any) -> str | None:
    """The ``upc`` in an ``album/get`` payload, or ``None``.

    Deliberately does not fall back to the album **id**, however barcode-shaped
    it looks. ``0060249867260`` is a Qobuz id that passes a GS1 check digit and
    is not the barcode of *Shangri-La* — ``602498672600`` is, one digit-shift
    away. Reading one field as another is the whole failure
    :func:`app.enrich.matching.barcode_candidates` was rewritten to stop, and
    writing the id into ``upc`` here would reintroduce it one layer lower, where
    every consumer would take it for Qobuz's own answer.
    """
    if not isinstance(payload, Mapping):
        return None
    value = payload.get("upc")
    if value is None or isinstance(value, (dict, list, tuple, set, bool)):
        return None
    text = str(value).strip()
    return text or None
