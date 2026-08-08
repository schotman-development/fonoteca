"""Bind the folders the disk scan could not name to the Qobuz releases they hold.

The scan matches a directory to a catalogue row by normalising the tagged album
title and the folder name (:meth:`app.core.scanner.LibraryScanner._pick_album`).
That is right by default and has a ceiling that no tuning removes: a folder whose
name is a *different string* for the same record cannot match. On one real
library 104 of 611 folders sat unmatched, and every confirmed case was a subtitle:

    disk: Play: The Guitar Album      catalogue: Play
    disk: Muddy Wolf at Red Rocks     catalogue: Muddy Wolf At Red Rocks (Live)
    disk: Live! At the Ryman          catalogue: Live! at The Ryman (Live)

Each is a release the user owns, listed as *missing*, and queued to be downloaded
again the moment anybody presses "download all". That is the cost this module
exists to remove, and it is a bigger one than any metadata gap: an unbound folder
makes the wanted list wrong, which is the list the whole application is for.

**The chain is the audio's, and it is already written.**
:func:`app.core.discovery.identify_folder` runs AcoustID over the files, takes the
release group the recordings agree on, asks MusicBrainz for every barcode in that
group, and lets one of those barcodes pick the Qobuz album out of a search. It
already returns ``qobuz_album_id``; until now only ``qobuz_artist_id`` was read
from it, by the bulk importer. This module is the other consumer.

Three properties come from that chain rather than from anything here, and all
three are why the result can be trusted enough to write:

* **A name may reject, never select.** The Qobuz search decides which twenty
  albums come back; :func:`app.enrich.matching.qobuz_album_by_barcode` decides
  which one is the record, and answers "none of them" freely. So this cannot
  record a guess — which matters more here than in enrichment, because a wrong
  binding does not merely mistag a release, it declares the user owns something
  they do not.
* **The release *group*, not the release.** The pressing MusicBrainz matched is
  usually not the edition Qobuz sells — Joanne Shaw Taylor's *White Sugar* is
  ``710347114727`` in MusicBrainz and ``0884385226442`` on Qobuz. Matching on any
  barcode in the group finds it and gives up no precision, because a release group
  carries one artist credit.
* **Two survivors is ambiguous, not a coin toss.** A folder that could be either
  of two Qobuz editions is reported and left alone.

**It writes a binding and nothing else.** No status moves, no track row is
created, no file is touched, nothing is queued. The adoption that follows is the
disk scan's, unchanged: it reads ``folder_bindings`` before it normalises a name,
finds the album, and runs the same ``_adopt`` every other folder goes through.
That split is deliberate rather than tidy. Adoption is where track rows, integrity
baselines, file claims and ``mark_library_due`` all happen, and a second
implementation of it here would be a second thing to keep correct. It also keeps
the scan's own guarantees intact: the scan still never calls Qobuz (it reads a
local table), and it still only ever moves an album *towards* ``downloaded``.

**Bounded and explicit.** Every folder costs a fingerprint of each of its files
plus a MusicBrainz browse and a Qobuz search, so this is a command somebody runs,
capped by ``limit``, and never a scheduled job.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Awaitable, Callable, Mapping

from sqlalchemy import delete, or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.discovery import Outcome
from app.core.scanner import LibraryScanner, collect_albums
from app.enrich.chromaprint import FpcalcMissing
from app.models import (
    BINDING_BOUND,
    BINDING_NOT_IN_CATALOGUE,
    Album,
    AlbumStatus,
    FolderBinding,
)

logger = logging.getLogger(__name__)

__all__ = [
    "BindResult",
    "BoundFolder",
    "bind_unmatched_folders",
    "clear_folder",
    "list_folder_bindings",
    "mark_folder",
]


@dataclass(slots=True)
class BoundFolder:
    """One directory that was resolved, for the report."""

    path: str
    album_id: str
    album_title: str
    artist_name: str
    barcode: str | None
    already_owned_at: str | None = None
    """Set when the release is already adopted at a *different* path."""


@dataclass(slots=True)
class BindResult:
    """What one binding pass did.

    The outcome buckets are the chain's own vocabulary rather than a
    pass/fail split, because they license different next steps: an *ambiguous*
    folder has an answer a person can pick, an *unidentified* one does not, and a
    *gated* run means nothing was measured at all and the numbers below say
    nothing about the library.
    """

    unmatched: int = 0
    """Folders the scan could not bind by name — the population considered."""

    examined: int = 0
    """Folders actually put through the chain; ``unmatched`` capped by ``limit``."""

    bound: int = 0
    ambiguous: int = 0
    unidentified: int = 0
    failed: int = 0

    manual: int = 0
    """Folders left alone because a person bound them by hand.

    Deliberately the *only* binding this pass declines to redo. A folder whose
    binding works is matched by the scan and never reaches the unmatched list at
    all, so the ones that do arrive here already bound are the broken ones —
    naming an album the indexer has since dropped, say — and re-deriving those is
    the point rather than waste. A person's answer is the exception, for the same
    reason ``_MANUAL_OWNS`` protects a manual enrichment id: only a person may
    overrule a person.
    """

    duplicates: int = 0
    """Identified, but the release is already adopted at another path."""

    gated: bool = False
    """No rung could run — no ``fpcalc``, no AcoustID key, no contact."""

    truncated: int = 0
    """Unmatched folders the scan report dropped before this could see them."""

    bindings: list[BoundFolder] = field(default_factory=list)
    unresolved: list[dict[str, Any]] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)

    @property
    def remaining(self) -> int:
        """Unmatched folders still unbound when this pass stopped."""
        return max(0, self.unmatched - self.bound)


async def bind_unmatched_folders(
    session: AsyncSession,
    *,
    identify: Callable[[Any], Awaitable[Any]] | None,
    settings: Any = None,
    root: Path | str | None = None,
    limit: int = 200,
    dry_run: bool = False,
    progress: Callable[[int, int], Awaitable[None] | None] | None = None,
) -> BindResult:
    """Identify the scan's unmatched folders by audio and record the bindings.

    Args:
        session: Session to read and write on. This function commits.
        identify: :func:`app.core.discovery.identifier_for`'s return value — the
            chain bound to the *running* ladder's rungs, so there is one rate
            limiter per upstream. ``None`` (the chain is not configured) is a
            first-class answer and returns a ``gated`` result rather than
            raising: a Qobuzarr with no AcoustID key still works, it just cannot
            do this.
        settings: Effective settings; ``LIBRARY_PATH`` comes from here.
        root: Directory to walk instead of ``LIBRARY_PATH``.
        limit: Most folders to identify in one pass.
        dry_run: Run the chain and report, write no binding.
        progress: Called as ``(done, total)``. May be a coroutine function.

    Returns:
        A :class:`BindResult`. One folder's failure never stops the pass; a
        missing ``fpcalc`` does, because the answer would be identical for every
        remaining directory and running them all to say so wastes the rate limit.
    """
    scanner = LibraryScanner(settings=settings)
    result = BindResult()

    # The unmatched list comes from a real scan in report-only mode rather than
    # from a rule restated here. That is the whole point: "unmatched" has to mean
    # exactly what the scan means by it, or this pass identifies folders the scan
    # would have bound anyway and misses ones it would not.
    # ``record=False`` as well as ``apply=False``: this is a report-only scan run
    # for its unmatched list, and storing it as *the* last scan would overwrite
    # what the Scan screen shows with a run nobody asked for.
    scan = await scanner.scan(session, root=root, apply=False, record=False)
    unmatched = list(scan.unmatched)
    result.unmatched = scan.unmatched_count
    result.truncated = scan.truncated.get("unmatched", 0)
    if result.truncated:
        logger.warning(
            "The scan report dropped %d unmatched folder(s); re-run to reach them",
            result.truncated,
        )
    if not unmatched:
        return result

    if identify is None:
        result.gated = True
        result.errors.append(
            "The audio route is not configured: it needs the AcoustID and "
            "MusicBrainz rungs enabled, an ACOUSTID_API_KEY, ENRICHMENT_CONTACT "
            "and fpcalc on PATH."
        )
        return result

    # Both kinds of settled answer are left alone, for the same reason and by
    # the same rule: a person said so. A ``not_in_catalogue`` folder is the more
    # important of the two here, because it is the one the chain would otherwise
    # re-fingerprint and re-query on every single run, forever, to reach the
    # answer it already reached — a library legitimately holds records no
    # catalogue sells, and no number of attempts changes that.
    pinned = {
        str(path)
        for path in (
            await session.execute(
                select(FolderBinding.path).where(
                    or_(
                        FolderBinding.method == "manual",
                        FolderBinding.state == BINDING_NOT_IN_CATALOGUE,
                    )
                )
            )
        ).scalars()
    }
    targets = [
        entry for entry in unmatched if str(entry.get("path") or "") not in pinned
    ]
    result.manual = len(unmatched) - len(targets)
    targets = targets[: max(0, int(limit))]
    result.examined = len(targets)
    if not targets:
        return result

    # One walk, so a folder's files are read once for the whole pass. The scan
    # above walked the tree too; both are read-only and the cost is seconds,
    # which is the price of not restating what "unmatched" means.
    by_directory = _index_directories(scanner, root)

    logger.info(
        "Identifying %d unmatched folder(s) by audio%s",
        len(targets),
        " (dry run)" if dry_run else "",
    )

    for index, entry in enumerate(targets, start=1):
        path = str(entry.get("path") or "")
        scanned = by_directory.get(path)
        if scanned is None:
            result.failed += 1
            result.errors.append(f"{path}: the directory went away during the pass")
        else:
            try:
                identity = await identify(scanned)
            except FpcalcMissing as exc:
                # Gates the pass, not the folder — see the docstring.
                result.gated = True
                result.errors.append(str(exc) or "fpcalc is not installed")
                logger.warning("Stopping: %s", exc)
                break
            except Exception as exc:  # noqa: BLE001 - one folder must not stop the pass
                result.failed += 1
                result.errors.append(f"{path}: {exc}")
                logger.exception("Identifying %s failed", path)
            else:
                await _record(session, result, path, identity, dry_run=dry_run)

        if progress is not None:
            outcome = progress(index, len(targets))
            if hasattr(outcome, "__await__"):
                await outcome

    if dry_run:
        await session.rollback()
    else:
        await session.commit()

    logger.info(
        "Folder binding: %d bound, %d ambiguous, %d unidentified, %d duplicate, %d failed",
        result.bound,
        result.ambiguous,
        result.unidentified,
        result.duplicates,
        result.failed,
    )
    return result


async def mark_folder(
    session: AsyncSession,
    path: str,
    *,
    album_id: str | None = None,
    note: str | None = None,
) -> FolderBinding:
    """Record a person's decision about one directory. Commits.

    With *album_id*, this directory is that Qobuz release. Without one, it holds
    **no** catalogue release at all.

    That second case is the one only a human can supply, and the reason is the
    ``UNKNOWN is not CHANGED`` rule from :func:`app.core.integrity.classify`
    wearing different clothes: the automatic chain failing to find a release is
    not evidence that none exists. A machine allowed to conclude "not a real
    release" would retire folders it merely could not identify — a compilation
    it cannot solve, an artist nobody follows, a MusicBrainz gap — and those are
    exactly the folders somebody still wants to see. What a person knows and no
    upstream can confirm is that a directory holds covers never released
    anywhere, a game soundtrack, or a radio bootleg. So the marker is written
    here and nowhere else, always as ``manual``.

    Both forms are recorded as ``manual``, which is what stops
    :func:`bind_unmatched_folders` from spending a fingerprint and two requests
    re-deriving an answer somebody already gave.

    Raises:
        LookupError: *album_id* names no album row. Binding a folder to an album
            that is not there would leave a row pointing at nothing, and the
            honest answer is that the artist needs following first.
    """
    target = str(album_id).strip() if album_id else ""
    if target:
        album = await session.get(Album, target)
        if album is None:
            raise LookupError(
                f"No album {target!r} in the database — follow the artist first, "
                "then bind the folder."
            )

    row = await session.get(FolderBinding, str(path))
    if row is None:
        row = FolderBinding(path=str(path))
        session.add(row)
    row.state = BINDING_BOUND if target else BINDING_NOT_IN_CATALOGUE
    row.album_id = target or None
    row.method = "manual"
    row.note = (note or "").strip() or None
    if not target:
        # Evidence of a match that is no longer claimed. Leaving a barcode on a
        # row that now says "no such release" would read as a contradiction to
        # anybody looking at the table later.
        row.barcode = None
        row.mb_release_group_mbid = None
    await session.commit()
    return row


async def clear_folder(session: AsyncSession, path: str) -> bool:
    """Forget whatever was settled about *path*. Commits.

    The way back from a marker somebody regrets, and the reason the marker is
    safe to apply liberally. The folder returns to the unmatched list and the
    automatic chain will consider it again on the next pass.
    """
    result = await session.execute(
        delete(FolderBinding).where(FolderBinding.path == str(path))
    )
    await session.commit()
    return bool(result.rowcount or 0)


async def list_folder_bindings(
    session: AsyncSession, *, state: str | None = None
) -> list[FolderBinding]:
    """Every settled directory, oldest first. Read-only."""
    statement = select(FolderBinding).order_by(FolderBinding.path)
    if state:
        statement = statement.where(FolderBinding.state == state)
    return list((await session.execute(statement)).scalars().all())


async def _record(
    session: AsyncSession,
    result: BindResult,
    path: str,
    identity: Any,
    *,
    dry_run: bool,
) -> None:
    """Turn one :class:`~app.core.discovery.FolderIdentity` into a row, or a report."""
    album_id = getattr(identity, "qobuz_album_id", None)
    outcome = getattr(identity, "outcome", Outcome.UNIDENTIFIED)
    reason = str(getattr(identity, "reason", "") or "")

    if outcome == Outcome.AMBIGUOUS:
        result.ambiguous += 1
        result.unresolved.append({"path": path, "outcome": "ambiguous", "reason": reason})
        return
    if outcome != Outcome.IDENTIFIED or not album_id:
        result.unidentified += 1
        result.unresolved.append(
            {"path": path, "outcome": str(outcome), "reason": reason}
        )
        return

    album = await session.get(Album, str(album_id))
    if album is None:
        # The chain answers from Qobuz's catalogue, which is wider than the rows
        # the indexer has created — a release by an artist nobody follows has no
        # row to bind to. Recording the binding anyway would be a row pointing at
        # nothing; the honest answer is that this folder needs the artist
        # followed first, which is the importer's job.
        result.unidentified += 1
        result.unresolved.append(
            {
                "path": path,
                "outcome": "not followed",
                "reason": (
                    f"identified as Qobuz album {album_id}"
                    f"{' by ' + identity.qobuz_artist_name if getattr(identity, 'qobuz_artist_name', None) else ''}"
                    ", who is not followed — import the artist first"
                ),
            }
        )
        return

    # A release already adopted somewhere else is not a binding, it is a second
    # copy. Writing the binding would move ``album.path`` on the next scan and
    # quietly re-point every library operation at the other directory, so this is
    # reported and left for a person: which of two folders to keep is not a
    # question the audio can answer.
    owned_elsewhere = (
        album.status is AlbumStatus.DOWNLOADED
        and (album.path or "")
        and str(album.path) != path
    )
    if owned_elsewhere:
        result.duplicates += 1
        result.unresolved.append(
            {
                "path": path,
                "outcome": "duplicate",
                "reason": f"already adopted at {album.path}",
            }
        )
        return

    result.bound += 1
    result.bindings.append(
        BoundFolder(
            path=path,
            album_id=str(album_id),
            album_title=str(album.title or ""),
            artist_name=str(getattr(identity, "qobuz_artist_name", "") or ""),
            barcode=_barcode_from(identity, reason),
        )
    )
    if dry_run:
        return

    row = await session.get(FolderBinding, path)
    if row is None:
        session.add(
            FolderBinding(
                path=path,
                album_id=str(album_id),
                state=BINDING_BOUND,
                method="audio-barcode",
                barcode=_barcode_from(identity, reason),
                mb_release_group_mbid=getattr(identity, "mb_release_group_mbid", None),
            )
        )
    elif row.method != "manual":
        # A person is never overruled by a later automatic pass — the same rule
        # ``_MANUAL_OWNS`` states for enrichment ids, for the same reason.
        row.state = BINDING_BOUND
        row.album_id = str(album_id)
        row.barcode = _barcode_from(identity, reason)
        row.mb_release_group_mbid = getattr(identity, "mb_release_group_mbid", None)


def _index_directories(
    scanner: LibraryScanner, root: Path | str | None
) -> Mapping[str, Any]:
    """Every album directory under the library root, keyed by path."""
    library_root = Path(root) if root else Path(scanner.settings.library_path)
    albums, _stats = collect_albums(library_root)
    return {str(album.directory): album for album in albums}


def _barcode_from(identity: Any, reason: str) -> str | None:
    """The barcode that decided this match.

    ``identify_folder`` reports it in its reason (``matched on barcode …``)
    rather than as a field; falling back to the group's first is honest but
    weaker, so it is only used when the reason does not name one.
    """
    marker = "matched on barcode "
    if marker in reason:
        candidate = reason.split(marker, 1)[1].strip()
        if candidate and candidate.lower() != "none":
            return candidate[:32]
    barcodes = tuple(getattr(identity, "barcodes", ()) or ())
    return str(barcodes[0])[:32] if barcodes else None
