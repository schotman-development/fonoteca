"""Writing to and deleting from the music library.

Everything else in Fonoteca either reads the library (``scanner``) or only ever
adds to it (``downloader``). This module is the single place allowed to *change*
what is already there — move it, rename it, re-tag it, take it away — which is
why the rules live here rather than being spread across four callers.

Four operations:

``delete_album_files``   the album's folder goes to the trash and the row goes
                         back to wanted (or skipped, if it is not monitored).
``refile_album``         move the folder, and the files inside it, to wherever
                         ``NAMING_TEMPLATE`` says they belong today.
``retag_album``          rewrite tags and cover art in place from the catalogue
                         metadata already in the database. No Qobuz calls.
``move_to_trash``        the primitive the first one is built on, also used by
                         the download loop to clear away a copy an upgrade has
                         superseded.

Three rules hold for all of them.

**Nothing is ever unlinked from the library.** Deleting moves the files to
``Settings.trash_dir`` (``data/trash`` by default) under a timestamped batch
directory with a ``manifest.json`` beside the payload, and there they stay until
someone empties the trash. That is a deliberate second chance: a bug in the path
arithmetic below costs a restore, not a re-download of somebody's discography.
Only :func:`empty_trash` calls ``rmtree``, only inside the trash, and only when
asked.

**Every path is proved to be inside the library before it is touched.**
:func:`resolve_in_library` resolves symlinks first and then checks containment,
so neither ``../..`` nor a symlink planted in a folder name can walk out. The
library root itself is refused: no operation here may empty it.

**The worker owns what it is working on.** An album that is ``DOWNLOADING``, or
that has an ``ACTIVE`` queue item, is refused by every operation — moving a file
out from under the download loop is how you get a half-album that looks complete.

Callers are async; the filesystem work is synchronous and goes through
``asyncio.to_thread``.
"""

from __future__ import annotations

import asyncio
import json
import os
import shutil
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, get_settings
from app.core import naming, quality
from app.core.tagger import tag_file
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    QueueItem,
    QueueState,
    Track,
    TrackStatus,
    utcnow,
)

__all__ = [
    "LibraryError",
    "LibraryPathError",
    "LibraryBusyError",
    "DeleteResult",
    "RefilePlan",
    "RefileResult",
    "RetagResult",
    "TrashEntry",
    "MANIFEST_NAME",
    "PAYLOAD_DIRNAME",
    "delete_album_files",
    "empty_trash",
    "list_trash",
    "move_to_trash",
    "plan_refile",
    "refile_album",
    "resolve_in_library",
    "restore_from_trash",
    "retag_album",
]

logger = get_logger(__name__)

#: Written beside every trashed batch: where it came from and why.
MANIFEST_NAME = "manifest.json"

#: The moved tree lives one level down, so the manifest can never be mistaken
#: for part of the content — including by a restore.
PAYLOAD_DIRNAME = "payload"


class LibraryError(Exception):
    """A library operation was refused. The message is safe to show a user."""


class LibraryPathError(LibraryError):
    """A path is not inside the library, or is the library itself."""


class LibraryBusyError(LibraryError):
    """The download worker owns these files right now."""


# ---------------------------------------------------------------------------
# Safety
# ---------------------------------------------------------------------------
def resolve_in_library(path: Any, root: Path) -> Path:
    """Resolve *path* and prove it lies strictly inside *root*.

    Symlinks are resolved **before** the containment test, so a link planted in
    an album folder cannot be used to reach outside the library. The root itself
    is rejected: every operation in this module would otherwise be one empty
    ``album.path`` away from taking out the whole collection.

    Raises:
        LibraryPathError: when the path is empty, is the root, or escapes it.
    """
    if not path or not str(path).strip():
        raise LibraryPathError("No path recorded for this release.")

    resolved_root = Path(root).expanduser().resolve()
    candidate = Path(str(path)).expanduser()
    try:
        resolved = candidate.resolve()
    except OSError as exc:  # pragma: no cover - only on a broken filesystem
        raise LibraryPathError(f"Cannot resolve {candidate}: {exc}") from exc

    if resolved == resolved_root:
        raise LibraryPathError(
            f"Refusing to touch the library root itself ({resolved_root})."
        )
    if not resolved.is_relative_to(resolved_root):
        raise LibraryPathError(f"{resolved} is outside the library ({resolved_root}).")
    return resolved


async def _assert_not_busy(session: AsyncSession, album: Album) -> None:
    """Refuse to touch files the download loop is using.

    ``DOWNLOADING`` is the album-level flag; an ``ACTIVE`` queue item is the
    worker-level one. Either means a ``.part`` file is being written into this
    directory right now.
    """
    if album.status is AlbumStatus.DOWNLOADING:
        raise LibraryBusyError(
            f"{album.display_title} is downloading right now — cancel it first."
        )
    active = (
        await session.execute(
            select(QueueItem.id)
            .where(QueueItem.album_id == album.id, QueueItem.state == QueueState.ACTIVE)
            .limit(1)
        )
    ).scalars().first()
    if active is not None:
        raise LibraryBusyError(
            f"{album.display_title} is being downloaded right now — cancel it first."
        )


def _tree_size(path: Path) -> tuple[int, int]:
    """``(file_count, bytes)`` for a file or directory. Never raises."""
    if path.is_file():
        try:
            return 1, path.stat().st_size
        except OSError:  # pragma: no cover - racing with an external rm
            return 1, 0
    count = 0
    total = 0
    for base, _dirs, files in os.walk(path, onerror=lambda _exc: None):
        for name in files:
            count += 1
            try:
                total += (Path(base) / name).stat().st_size
            except OSError:  # pragma: no cover
                pass
    return count, total


# ---------------------------------------------------------------------------
# Trash
# ---------------------------------------------------------------------------
@dataclass(slots=True)
class TrashEntry:
    """One batch in the trash: what was moved, from where, and why."""

    id: str
    trashed_at: datetime | None
    original_path: str
    reason: str
    album_id: str | None = None
    album_title: str | None = None
    artist_name: str | None = None
    file_count: int = 0
    size_bytes: int = 0
    path: Path | None = None
    """Where the batch directory is now. ``None`` in a serialised manifest."""

    @property
    def payload(self) -> Path | None:
        """The moved tree inside the batch directory."""
        return None if self.path is None else self.path / PAYLOAD_DIRNAME

    def as_dict(self) -> dict[str, Any]:
        """JSON-ready form, used both for the manifest and the API."""
        return {
            "id": self.id,
            "trashed_at": self.trashed_at.isoformat() if self.trashed_at else None,
            "original_path": self.original_path,
            "reason": self.reason,
            "album_id": self.album_id,
            "album_title": self.album_title,
            "artist_name": self.artist_name,
            "file_count": self.file_count,
            "size_bytes": self.size_bytes,
        }


def _batch_id(when: datetime, suffix: str) -> str:
    """A sortable, filesystem-safe directory name for one trash batch."""
    stamp = when.astimezone(timezone.utc).strftime("%Y-%m-%dT%H-%M-%S")
    return f"{stamp}_{suffix}"


def _unique_batch_dir(trash_root: Path, when: datetime, hint: str) -> Path:
    """Reserve an unused batch directory. Two deletes in one second must not collide."""
    base = "".join(char if char.isalnum() else "-" for char in hint)[:24].strip("-")
    for attempt in range(1000):
        suffix = base or "item"
        if attempt:
            suffix = f"{suffix}-{attempt}"
        candidate = trash_root / _batch_id(when, suffix)
        if not candidate.exists():
            return candidate
    raise LibraryError("Could not find a free slot in the trash directory.")


def move_to_trash(
    path: Any,
    *,
    settings: Settings | None = None,
    reason: str = "deleted",
    album: Album | None = None,
) -> TrashEntry:
    """Move *path* out of the library and into the trash. **Synchronous.**

    The batch directory holds a ``manifest.json`` and a ``payload/`` copy of the
    tree, so a restore knows where it came from and can never mistake the
    manifest for content.

    ``shutil.move`` falls back to copy-then-delete across filesystems, so this
    can take as long as writing the album did when ``TRASH_PATH`` is on another
    disk. That is the cost of not unlinking; put the trash on the library's
    filesystem to make it a rename.

    Raises:
        LibraryPathError: when *path* is not inside the library.
        LibraryError: when the move itself fails.
    """
    conf = settings or get_settings()
    source = resolve_in_library(path, conf.library_path)
    if not source.exists():
        raise LibraryError(f"{source} is not there any more.")

    file_count, size_bytes = _tree_size(source)
    when = utcnow()
    trash_root = conf.trash_dir
    trash_root.mkdir(parents=True, exist_ok=True)
    batch = _unique_batch_dir(trash_root, when, source.name)

    entry = TrashEntry(
        id=batch.name,
        trashed_at=when,
        original_path=str(source),
        reason=reason,
        album_id=getattr(album, "id", None),
        album_title=getattr(album, "display_title", None),
        artist_name=getattr(getattr(album, "artist", None), "name", None),
        file_count=file_count,
        size_bytes=size_bytes,
        path=batch,
    )

    payload = batch / PAYLOAD_DIRNAME
    payload.mkdir(parents=True, exist_ok=False)
    try:
        shutil.move(str(source), str(payload / source.name))
    except OSError as exc:
        # Leave no half-made batch behind to confuse the trash listing.
        shutil.rmtree(batch, ignore_errors=True)
        raise LibraryError(f"Could not move {source} to the trash: {exc}") from exc

    (batch / MANIFEST_NAME).write_text(
        json.dumps(entry.as_dict(), indent=2), encoding="utf-8"
    )
    _prune_empty_parents(source.parent, conf.library_path)
    logger.info(
        "Trashed %s (%s, %d file(s)) as %s", source, reason, file_count, batch.name
    )
    return entry


def _prune_empty_parents(start: Path, root: Path) -> None:
    """Remove directories left empty by a move, upwards, stopping at the root.

    An artist folder with nothing in it is litter, not data. ``rmdir`` only ever
    succeeds on an empty directory, so this cannot take anything with it — and
    the loop stops the moment it fails or reaches the library root.
    """
    try:
        resolved_root = Path(root).expanduser().resolve()
        current = start.resolve()
    except OSError:  # pragma: no cover
        return
    while current != resolved_root and current.is_relative_to(resolved_root):
        try:
            current.rmdir()
        except OSError:
            return
        logger.debug("Removed empty directory %s", current)
        current = current.parent


def _read_manifest(batch: Path) -> TrashEntry | None:
    """Parse one batch directory into a :class:`TrashEntry`, or ``None``."""
    manifest = batch / MANIFEST_NAME
    try:
        raw = json.loads(manifest.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        logger.debug("Ignoring unreadable trash manifest %s: %s", manifest, exc)
        return None
    stamp = raw.get("trashed_at")
    try:
        trashed_at = datetime.fromisoformat(stamp) if stamp else None
    except ValueError:  # pragma: no cover - hand-edited manifest
        trashed_at = None
    return TrashEntry(
        id=raw.get("id") or batch.name,
        trashed_at=trashed_at,
        original_path=raw.get("original_path") or "",
        reason=raw.get("reason") or "deleted",
        album_id=raw.get("album_id"),
        album_title=raw.get("album_title"),
        artist_name=raw.get("artist_name"),
        file_count=int(raw.get("file_count") or 0),
        size_bytes=int(raw.get("size_bytes") or 0),
        path=batch,
    )


def list_trash(settings: Settings | None = None) -> list[TrashEntry]:
    """Everything currently in the trash, newest first. **Synchronous.**

    Batch directories without a readable manifest are skipped rather than
    guessed at — the trash is also somewhere a user may poke around by hand.
    """
    conf = settings or get_settings()
    root = conf.trash_dir
    if not root.is_dir():
        return []
    entries = [
        entry
        for batch in sorted(root.iterdir(), reverse=True)
        if batch.is_dir() and (entry := _read_manifest(batch)) is not None
    ]
    return entries


def _trash_batch(entry_id: str, settings: Settings) -> Path:
    """Resolve one batch directory, proving it is really inside the trash."""
    root = settings.trash_dir.resolve()
    candidate = (root / str(entry_id)).resolve()
    if candidate.parent != root or not candidate.is_dir():
        raise LibraryPathError(f"No such trash entry: {entry_id}")
    return candidate


def restore_from_trash(entry_id: str, settings: Settings | None = None) -> Path:
    """Put one trashed batch back where it came from. **Synchronous.**

    The destination is re-checked against the library root — a manifest is a
    file on disk and could have been edited — and an occupied destination is
    refused rather than merged, because a half-overwritten album is worse than
    a failed restore.

    Returns:
        The path the content was restored to.
    """
    conf = settings or get_settings()
    batch = _trash_batch(entry_id, conf)
    entry = _read_manifest(batch)
    if entry is None:
        raise LibraryError(f"Trash entry {entry_id} has no usable manifest.")

    destination = resolve_in_library(entry.original_path, conf.library_path)
    if destination.exists():
        raise LibraryError(
            f"{destination} exists again — move it aside before restoring."
        )

    payload = batch / PAYLOAD_DIRNAME
    contents = sorted(payload.iterdir()) if payload.is_dir() else []
    if len(contents) != 1:
        raise LibraryError(f"Trash entry {entry_id} does not hold exactly one item.")

    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.move(str(contents[0]), str(destination))
    except OSError as exc:
        raise LibraryError(f"Could not restore {entry_id}: {exc}") from exc
    shutil.rmtree(batch, ignore_errors=True)
    logger.info("Restored %s from trash entry %s", destination, entry_id)
    return destination


def empty_trash(
    settings: Settings | None = None, entry_id: str | None = None
) -> tuple[int, int]:
    """Delete trashed batches for real. **Synchronous.**

    The only place in Fonoteca that removes files permanently, and it only ever
    runs inside ``Settings.trash_dir`` on an explicit request. Pass *entry_id*
    for one batch, omit it for all of them.

    Returns:
        ``(batches removed, bytes reclaimed)``.
    """
    conf = settings or get_settings()
    targets = (
        [_trash_batch(entry_id, conf)]
        if entry_id
        else [entry.path for entry in list_trash(conf) if entry.path]
    )
    removed = 0
    freed = 0
    for batch in targets:
        _count, size = _tree_size(batch)
        shutil.rmtree(batch, ignore_errors=True)
        if not batch.exists():
            removed += 1
            freed += size
    logger.info("Emptied %d trash batch(es), %d bytes", removed, freed)
    return removed, freed


# ---------------------------------------------------------------------------
# Results
# ---------------------------------------------------------------------------
@dataclass(slots=True)
class DeleteResult:
    """Outcome of :func:`delete_album_files`."""

    album_id: str
    trashed: TrashEntry | None = None
    tracks_cleared: int = 0
    new_status: AlbumStatus | None = None

    @property
    def summary(self) -> str:
        """One line, safe for an activity row."""
        if self.trashed is None:
            return "nothing was on disk"
        size = self.trashed.size_bytes / (1024 * 1024)
        return f"{self.trashed.file_count} file(s), {size:.1f} MiB, to the trash"


@dataclass(slots=True)
class RefilePlan:
    """What re-filing one album *would* do. Nothing here has happened yet."""

    album_id: str
    album_title: str
    artist_name: str
    current_dir: str
    target_dir: str
    renames: list[tuple[str, str]] = field(default_factory=list)
    """``(current name, target name)`` for files inside the album directory."""
    blocked: str | None = None
    """Why this cannot be applied, when it cannot. ``None`` means it can."""

    @property
    def moves_directory(self) -> bool:
        """True when the album folder itself is in the wrong place."""
        return self.current_dir != self.target_dir

    @property
    def needed(self) -> bool:
        """True when anything at all would change."""
        return self.moves_directory or bool(self.renames)


@dataclass(slots=True)
class RefileResult:
    """Outcome of :func:`refile_album`."""

    album_id: str
    moved_directory: bool = False
    files_renamed: int = 0
    target_dir: str | None = None

    @property
    def summary(self) -> str:
        parts = []
        if self.moved_directory:
            parts.append(f"folder -> {self.target_dir}")
        if self.files_renamed:
            parts.append(f"{self.files_renamed} file(s) renamed")
        return ", ".join(parts) or "already in place"


@dataclass(slots=True)
class RetagResult:
    """Outcome of :func:`retag_album`."""

    album_id: str
    tagged: int = 0
    failed: int = 0
    missing: int = 0
    cover_embedded: bool = False

    @property
    def summary(self) -> str:
        return (
            f"{self.tagged} tagged, {self.failed} failed, {self.missing} missing"
            + (" (cover embedded)" if self.cover_embedded else "")
        )


# ---------------------------------------------------------------------------
# Delete
# ---------------------------------------------------------------------------
def _log(
    session: AsyncSession,
    *,
    event: str,
    message: str,
    album: Album,
    level: ActivityLevel = ActivityLevel.INFO,
) -> None:
    """Record one library action on the activity feed."""
    session.add(
        Activity(
            level=level,
            event=event,
            message=message,
            artist_id=album.artist_id,
            album_id=album.id,
        )
    )


async def delete_album_files(
    session: AsyncSession,
    album: Album,
    *,
    settings: Settings | None = None,
    reason: str = "deleted",
) -> DeleteResult:
    """Move one release's files to the trash and forget they were on disk.

    The album goes back to ``WANTED`` when it is still monitored and ``SKIPPED``
    when it is not, which is the same distinction the monitor toggle makes: a
    release you deleted but still want is a release you want downloaded again,
    and this is the only sensible reading of "delete a monitored album".

    Nothing is queued. Deleting is not a request to re-download — that stays an
    explicit press, like every other path into the queue.

    Raises:
        LibraryBusyError: the download worker is writing into this folder.
        LibraryPathError: ``album.path`` is not inside the library.
    """
    conf = settings or get_settings()
    await _assert_not_busy(session, album)

    result = DeleteResult(album_id=album.id)
    if album.path:
        try:
            result.trashed = await asyncio.to_thread(
                move_to_trash, album.path, settings=conf, reason=reason, album=album
            )
        except LibraryError as exc:
            # A path that has already vanished is not an error worth blocking
            # on: the database is simply stale, and clearing it is the fix.
            logger.info("Nothing to trash for %s: %s", album.id, exc)

    for track in album.tracks:
        if track.path or track.status is TrackStatus.DOWNLOADED:
            track.path = None
            track.file_size = None
            track.downloaded_at = None
            track.status = TrackStatus.PENDING
            result.tracks_cleared += 1

    album.path = None
    album.downloaded_at = None
    album.status = AlbumStatus.WANTED if album.monitored else AlbumStatus.SKIPPED
    result.new_status = album.status

    _log(
        session,
        event="library.deleted",
        message=f"Deleted {album.display_title}: {result.summary}",
        album=album,
        level=ActivityLevel.WARNING,
    )
    await session.commit()
    return result


# ---------------------------------------------------------------------------
# Re-file
# ---------------------------------------------------------------------------
def _owned_quality(tracks: Iterable[Track]) -> tuple[int | None, int | None, float | None]:
    """The ``(format_id, bit_depth, sampling_rate)`` of the worst file we hold.

    Re-filing names a folder after what is *in* it, so the quality tag has to
    come from the files rather than from what the catalogue advertises.
    """
    worst: Track | None = None
    worst_format: int | None = None
    for track in tracks:
        if not track.path:
            continue
        fid = quality.track_format_id(track)
        if fid is None:
            continue
        if worst_format is None or fid < worst_format:
            worst_format, worst = fid, track
    if worst is None:
        return None, None, None
    return worst_format, worst.bit_depth, worst.sampling_rate


async def plan_refile(
    session: AsyncSession, album: Album, settings: Settings | None = None
) -> RefilePlan:
    """Work out where *album*'s files should be, without moving anything.

    Every bulk action in the UI shows this first. ``blocked`` carries the reason
    an album cannot be re-filed — a busy download, a path outside the library, a
    destination already occupied — so a preview lists the problems instead of
    discovering them one at a time halfway through.
    """
    conf = settings or get_settings()
    artist = album.artist
    artist_name = getattr(artist, "name", None) or album.artist_id

    plan = RefilePlan(
        album_id=album.id,
        album_title=album.display_title,
        artist_name=artist_name,
        current_dir=album.path or "",
        target_dir="",
    )

    format_id, bit_depth, sampling_rate = _owned_quality(album.tracks)
    target_dir = naming.render_album_dir(
        artist or artist_name,
        album,
        conf,
        format_id=format_id,
        bit_depth=bit_depth if bit_depth is not None else album.max_bit_depth,
        sampling_rate=(
            sampling_rate if sampling_rate is not None else album.max_sampling_rate
        ),
    )
    # Resolve the target too: ``current`` has had its symlinks resolved, and a
    # library root that is itself a symlink would otherwise make every album
    # look misfiled.
    target_dir = target_dir.resolve()
    plan.target_dir = str(target_dir)

    try:
        current = resolve_in_library(album.path, conf.library_path)
    except LibraryPathError as exc:
        plan.blocked = str(exc)
        return plan
    if not current.is_dir():
        plan.blocked = f"{current} is not a directory any more."
        return plan
    plan.current_dir = str(current)

    if current != target_dir and target_dir.exists():
        plan.blocked = f"{target_dir} already exists."

    for track in album.tracks:
        if not track.path:
            continue
        try:
            source = resolve_in_library(track.path, conf.library_path)
        except LibraryPathError:
            continue
        if not source.is_file():
            continue
        wanted = naming.render_track_name(
            track,
            album,
            conf,
            artist=artist or artist_name,
            format_id=track.format_id,
            bit_depth=track.bit_depth,
            sampling_rate=track.sampling_rate,
            # Name from the extension the file actually has: the recorded
            # format_id may be missing on a release adopted from disk.
            ext=source.suffix.lstrip(".") or None,
        )
        try:
            relative = source.relative_to(current)
        except ValueError:  # pragma: no cover - a track outside its own album dir
            continue
        if str(relative) != str(wanted):
            plan.renames.append((str(relative), str(wanted)))

    if plan.blocked is None:
        try:
            await _assert_not_busy(session, album)
        except LibraryBusyError as exc:
            plan.blocked = str(exc)
    return plan


def _apply_refile_sync(
    plan: RefilePlan, library_root: Path
) -> tuple[bool, list[tuple[Path, Path]]]:
    """Do the moves. **Synchronous.** Returns ``(moved_dir, [(old, new), ...])``."""
    current = Path(plan.current_dir)
    target = Path(plan.target_dir)
    moved_directory = False

    if current != target:
        if target.exists():
            raise LibraryError(f"{target} already exists.")
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            shutil.move(str(current), str(target))
        except OSError as exc:
            raise LibraryError(f"Could not move {current} to {target}: {exc}") from exc
        moved_directory = True

    renamed: list[tuple[Path, Path]] = []
    for old_relative, new_relative in plan.renames:
        old_path = target / old_relative
        new_path = target / new_relative
        if not old_path.is_file() or old_path == new_path:
            continue
        if new_path.exists():
            logger.warning("Not renaming %s: %s is taken", old_path, new_path)
            continue
        new_path.parent.mkdir(parents=True, exist_ok=True)
        try:
            os.replace(old_path, new_path)
        except OSError as exc:
            logger.warning("Could not rename %s: %s", old_path, exc)
            continue
        renamed.append((old_path, new_path))

    if moved_directory:
        # The artist folder the album just left may now be empty.
        _prune_empty_parents(current.parent, library_root)
    return moved_directory, renamed


async def refile_album(
    session: AsyncSession,
    album: Album,
    *,
    settings: Settings | None = None,
    plan: RefilePlan | None = None,
) -> RefileResult:
    """Move one release to where ``NAMING_TEMPLATE`` says it belongs.

    The whole directory moves, so cover art and anything else the folder holds
    travels with the music; files inside it are then renamed individually where
    the template disagrees with what they are called.

    The database is updated from the moves that actually happened, not from the
    plan — a rename that failed leaves that track's row pointing at the file
    that is really there.

    Raises:
        LibraryError: when the plan is blocked or a move fails.
    """
    conf = settings or get_settings()
    plan = plan or await plan_refile(session, album, conf)
    if plan.blocked:
        raise LibraryError(plan.blocked)

    result = RefileResult(album_id=album.id, target_dir=plan.target_dir)
    if not plan.needed:
        return result

    moved_directory, renamed = await asyncio.to_thread(
        _apply_refile_sync, plan, conf.library_path
    )
    result.moved_directory = moved_directory
    result.files_renamed = len(renamed)

    old_dir = Path(plan.current_dir)
    new_dir = Path(plan.target_dir)
    by_old = {old: new for old, new in renamed}
    for track in album.tracks:
        if not track.path:
            continue
        path = Path(track.path)
        if moved_directory:
            try:
                path = new_dir / path.relative_to(old_dir)
            except ValueError:  # pragma: no cover
                pass
        track.path = str(by_old.get(path, path))
    album.path = str(new_dir)

    _log(
        session,
        event="library.refiled",
        message=f"Re-filed {album.display_title}: {result.summary}",
        album=album,
    )
    await session.commit()
    return result


# ---------------------------------------------------------------------------
# Re-tag
# ---------------------------------------------------------------------------
def _album_cover_bytes(album_dir: Path) -> bytes | None:
    """Read ``cover.jpg`` from an album folder, if it is there."""
    cover = album_dir / naming.COVER_FILENAME
    try:
        return cover.read_bytes() if cover.is_file() else None
    except OSError:  # pragma: no cover
        return None


async def retag_album(
    session: AsyncSession,
    album: Album,
    *,
    settings: Settings | None = None,
    embed_cover: bool | None = None,
) -> RetagResult:
    """Rewrite tags and cover art on the files already on disk.

    Local only: the metadata comes from the database, which the indexer keeps
    current, so this costs no Qobuz calls and can be run over a whole library.
    Use it after fixing a title, or on albums adopted from disk with whatever
    tags they arrived with.

    Files are edited in place — this is the one operation here that changes a
    file's contents rather than its location, and there is no trash copy of the
    old tags. The audio itself is untouched.
    """
    conf = settings or get_settings()
    await _assert_not_busy(session, album)

    result = RetagResult(album_id=album.id)
    wants_cover = conf.download_embed_cover if embed_cover is None else embed_cover
    cover: bytes | None = None
    if wants_cover and album.path:
        try:
            album_dir = resolve_in_library(album.path, conf.library_path)
            cover = await asyncio.to_thread(_album_cover_bytes, album_dir)
        except LibraryPathError as exc:
            logger.debug("No cover for %s: %s", album.id, exc)
    result.cover_embedded = cover is not None

    artist_name = getattr(album.artist, "name", None) or album.artist_id
    for track in album.tracks:
        if not track.path:
            result.missing += 1
            continue
        try:
            path = resolve_in_library(track.path, conf.library_path)
        except LibraryPathError as exc:
            logger.warning("Skipping %s: %s", track.id, exc)
            result.failed += 1
            continue
        if not path.is_file():
            result.missing += 1
            continue
        try:
            ok = await asyncio.to_thread(
                tag_file,
                path,
                track,
                album,
                cover,
                artist_name=artist_name,
                ext=path.suffix.lstrip("."),
            )
        except Exception as exc:  # noqa: BLE001 - one bad file must not stop the rest
            logger.warning("Could not tag %s: %s", path, exc)
            result.failed += 1
            continue
        if ok:
            result.tagged += 1
        else:
            result.failed += 1

    _log(
        session,
        event="library.retagged",
        message=f"Re-tagged {album.display_title}: {result.summary}",
        album=album,
        level=ActivityLevel.WARNING if result.failed else ActivityLevel.INFO,
    )
    await session.commit()
    return result


# ---------------------------------------------------------------------------
# Bulk
# ---------------------------------------------------------------------------
async def albums_on_disk(
    session: AsyncSession, *, artist_id: str | None = None, limit: int = 2000
) -> list[Album]:
    """Downloaded releases with a recorded path, oldest first.

    The candidate set for every bulk operation. ``DOWNLOADING`` albums are
    excluded here as well as refused later, so a preview never lists work that
    is going to be rejected.
    """
    stmt = (
        select(Album)
        .where(Album.status == AlbumStatus.DOWNLOADED, Album.path.is_not(None))
        .order_by(Album.added_at.asc())
        .limit(max(1, limit))
    )
    if artist_id:
        stmt = stmt.where(Album.artist_id == str(artist_id))
    return list((await session.execute(stmt)).unique().scalars().all())


async def plan_library_refile(
    session: AsyncSession,
    *,
    artist_id: str | None = None,
    settings: Settings | None = None,
    limit: int = 2000,
) -> list[RefilePlan]:
    """Every album whose files are not where the template says, plus the blocked ones.

    Albums already in the right place are dropped: the point of the list is the
    work outstanding.
    """
    conf = settings or get_settings()
    plans = []
    for album in await albums_on_disk(session, artist_id=artist_id, limit=limit):
        plan = await plan_refile(session, album, conf)
        if plan.needed or plan.blocked:
            plans.append(plan)
    return plans
