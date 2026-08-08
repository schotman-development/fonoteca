"""Writing to and deleting from the music library.

Everything else in Qobuzarr either reads the library (``scanner``) or only ever
adds to it (``downloader``). This module is the single place allowed to *change*
what is already there — move it, rename it, re-tag it, take it away — which is
why the rules live here rather than being spread across four callers.

Five operations:

``delete_album_files``   the album's folder goes to the trash and the row goes
                         back to wanted (or skipped, if it is not monitored).
``refile_album``         move the folder, and the files inside it, to wherever
                         ``NAMING_TEMPLATE`` says they belong today.
``retag_album``          rewrite tags and cover art in place from the catalogue
                         metadata already in the database. No Qobuz calls.
``move_to_trash``        the primitive the first one is built on, also used by
                         the download loop to clear away a copy an upgrade has
                         superseded.
``quarantine_corrupt_files``  files the fingerprinter could not decode go to the
                         trash and the tracks are marked missing.

Four rules hold for all of them.

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

**The disk is verified before the database is believed.**
:func:`verify_album_files` measures what is actually there and
:func:`_assert_unaltered` refuses an album holding a file that has been
*replaced* — different bytes carrying different audio from the ones that were
recorded. Every operation here acts on ``album.path`` and ``track.path``, which
are statements the database makes about the disk; when one of those statements
has gone stale the operation is aimed at something other than what it was
computed for, and that is how the wrong folder gets trashed. A re-tag is not a
replacement and never blocks: the audio is provably the same, so the row still
describes the recording it says it does.

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
from typing import Any, Iterable, Mapping, Sequence

from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import (
    Settings,
    effective_for,
    get_effective_settings,
    get_settings,
)
from app.core import integrity, naming, quality, scanner
from app.core.integrity import FileStamp, IntegrityState
from app.core.tagger import tag_file
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    FingerprintState,
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
    "SharedDirectoryError",
    "StaleLibraryError",
    "FrozenPathError",
    "AlbumIntegrity",
    "TrackIntegrity",
    "DeleteResult",
    "RefileEstimate",
    "RefilePlan",
    "RefileResult",
    "NfoResult",
    "QuarantineResult",
    "RetagResult",
    "TrashEntry",
    "MANIFEST_NAME",
    "PAYLOAD_DIRNAME",
    "delete_album_files",
    "empty_trash",
    "estimate_library_refile",
    "list_trash",
    "move_to_trash",
    "plan_refile",
    "corrupt_file_count",
    "quarantine_corrupt_files",
    "rebaseline_album",
    "refile_album",
    "resolve_in_library",
    "restore_from_trash",
    "retag_album",
    "verify_album_files",
    "write_library_nfo",
    "write_nfo",
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


class SharedDirectoryError(LibraryPathError):
    """This directory holds more than the one release, so it is not ours to move."""


class StaleLibraryError(LibraryError):
    """The files are not the ones the database recorded, so it is describing something else."""


class FrozenPathError(LibraryError):
    """``Album.freeze_path`` is set: somebody has said where this release lives."""


def _frozen_path_reason(album: Album) -> str:
    """The one sentence every ``freeze_path`` refusal says. Written once.

    Three places refuse a frozen release — the plan, the library-wide preview and
    :func:`refile_album` itself — and a flag that reads differently depending on
    which one you hit is a flag nobody trusts. It names the switch, so the person
    reading it knows where the decision was made and how to unmake it.
    """
    return (
        f"{album.display_title}: this release's path is frozen. Turn off "
        "“Freeze path” on the release to let Qobuzarr re-file it."
    )


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


async def _assert_exclusive(
    session: AsyncSession, album: Album, resolved: Path, root: Path
) -> None:
    """Refuse a directory that is not this one release's own.

    ``resolve_in_library()`` asks whether a path is *inside* the library; this
    asks whether it is *ours*, and the two are not the same question. The scanner
    records ``album.path`` as the directory the audio files were found in, so a
    library laid out flat — files sitting directly in the artist folder rather
    than in a per-album sub-folder, which ``scanner.collect_albums()``
    deliberately supports — gives several albums the same ``path``, and that path
    is the artist folder. Trashing or moving it takes the artist's whole
    discography along. Neither other gate notices: the root check passes because
    an artist folder is not the root, and ``_assert_not_busy()`` is asked about
    this album alone, never about the siblings that would travel with it.

    Two ways a directory fails to be ours, and both are refusals rather than
    best-effort exclusions, because "move most of this folder" is not an
    operation this module offers:

    1. another album's ``path`` is that directory or sits under it;
    2. it is the artist's own folder, by name.

    Raises:
        SharedDirectoryError: with a message naming what else is in there.
    """
    prefix = f"{resolved}{os.sep}"
    rows = (
        await session.execute(
            select(Album.id, Album.title, Album.path).where(
                Album.id != album.id,
                Album.path.is_not(None),
                Album.path != "",
            )
        )
    ).all()
    shared: list[str] = []
    for _id, title, path in rows:
        # Compare resolved forms: one row may have been written before the
        # library moved behind a symlink, and a string compare would miss it.
        try:
            other = Path(str(path)).expanduser().resolve()
        except OSError:  # pragma: no cover - only on a broken filesystem
            continue
        if other == resolved or str(other).startswith(prefix):
            shared.append(str(title or _id))
        if len(shared) >= 3:
            break
    if shared:
        raise SharedDirectoryError(
            f"{resolved.name} holds more than this release "
            f"({', '.join(shared)}{', …' if len(shared) >= 3 else ''}) — "
            "its files are filed directly in a shared folder. Re-file the "
            "artist first, or delete the artist instead of one release."
        )

    artist_name = getattr(getattr(album, "artist", None), "name", None)
    if artist_name and resolved.parent == root:
        if scanner.artist_key(resolved.name) == scanner.artist_key(artist_name):
            raise SharedDirectoryError(
                f"{resolved.name} is the artist folder, not this release's own "
                "folder — its files are filed directly in it. Re-file the artist "
                "first, or delete the artist instead of one release."
            )


# ---------------------------------------------------------------------------
# The fifth gate — verify the disk before trusting the database
# ---------------------------------------------------------------------------
#: Which answer wins when an album's files disagree with each other. Read as
#: "the strongest thing the disk has to say about the database's belief": one
#: replaced file makes the whole release suspect, and one verified file among
#: four unmeasured ones says nothing about the four.
_INTEGRITY_SEVERITY: tuple[IntegrityState, ...] = (
    IntegrityState.REPLACED,
    IntegrityState.MISSING,
    IntegrityState.RETAGGED,
    IntegrityState.UNKNOWN,
    IntegrityState.VERIFIED,
)


@dataclass(frozen=True, slots=True)
class TrackIntegrity:
    """What one file turned out to be, next to what the database recorded."""

    track_id: str
    qid: str | None
    path: str
    state: IntegrityState
    measured: bool = False
    """True when the file was actually opened and hashed on this pass.

    ``False`` with a state of ``VERIFIED`` is the ordinary case and is a weaker
    claim than it looks: it means size and mtime match what was recorded, so
    :func:`app.core.integrity.hash_file` was not worth its 201 ms. That is a
    decision *not to look*, which is why nothing here stamps ``verified_at`` on
    the strength of it — see :meth:`app.core.scanner.LibraryScanner._apply_file`,
    which draws the same line for the same reason.
    """
    stamp: FileStamp | None = None
    """The fresh measurement, when one was taken. ``None`` otherwise."""


@dataclass(slots=True)
class AlbumIntegrity:
    """Every file of one release, classified. The answer :func:`verify_album_files` gives."""

    album_id: str
    tracks: list[TrackIntegrity] = field(default_factory=list)

    @property
    def state(self) -> IntegrityState:
        """The album's overall state: the worst thing any of its files said.

        ``UNKNOWN`` for a release with no files to check — an album the disk scan
        has not reached, or one whose rows carry no path. Nothing was measured, so
        nothing is being claimed, and that is not the same as nothing being wrong.
        """
        seen = {track.state for track in self.tracks}
        for state in _INTEGRITY_SEVERITY:
            if state in seen:
                return state
        return IntegrityState.UNKNOWN

    @property
    def replaced(self) -> list[TrackIntegrity]:
        """The files whose audio is not the audio that was recorded."""
        return [
            track for track in self.tracks if track.state is IntegrityState.REPLACED
        ]

    @property
    def counts(self) -> dict[str, int]:
        """How many files landed in each state, for logs and activity rows."""
        tally: dict[str, int] = {}
        for track in self.tracks:
            tally[track.state.value] = tally.get(track.state.value, 0) + 1
        return tally

    @property
    def summary(self) -> str:
        """One line, safe for an activity row."""
        if not self.tracks:
            return "nothing measured"
        return ", ".join(
            f"{count} {name}" for name, count in sorted(self.counts.items())
        )


#: The baseline recorded on a track row, read exactly as the disk scan reads it.
#: Shared rather than re-written here so that the gate and the scan can never
#: disagree about what those four columns mean — see
#: :func:`app.core.scanner.recorded_stamp`.
_recorded_stamp = scanner.recorded_stamp


def _classify_files(
    items: Sequence[tuple[str, str | None, Path, FileStamp | None]]
) -> list[TrackIntegrity]:
    """Measure and classify one album's files. **Synchronous** — blocking I/O.

    The tripwire comes first and is the whole reason this is affordable on a
    library-wide sweep: ``os.stat`` per file, and the file is opened only when
    size or mtime disagree with the baseline. Hashing every file instead costs
    about 201 ms each, which turns a preview of what re-filing would do into a
    ninety-minute disk sweep on a 30 000-file library — a check nobody leaves on.

    A row with no baseline is answered ``UNKNOWN`` without opening anything, and
    that is a deliberate difference from
    :meth:`app.core.scanner.LibraryScanner._tripwire_fired`, which reads exactly
    those files. The scan is there to *establish* a baseline and has to measure to
    do it; this is a gate, and it only asks whether the disk contradicts the
    database. Nothing that was never recorded can be contradicted, so reading the
    file could not change the answer — and hashing an entire un-baselined library
    to learn that is what would make deleting one album take a minute.
    """
    out: list[TrackIntegrity] = []
    for track_id, qid, path, recorded in items:
        cheap = integrity.stat_stamp(path)
        if cheap is None:
            out.append(
                TrackIntegrity(
                    track_id=track_id,
                    qid=qid,
                    path=str(path),
                    state=IntegrityState.MISSING,
                )
            )
            continue
        if recorded is None:
            out.append(
                TrackIntegrity(
                    track_id=track_id,
                    qid=qid,
                    path=str(path),
                    state=IntegrityState.UNKNOWN,
                )
            )
            continue
        size, mtime = cheap
        if (recorded.size, recorded.mtime) == (size, mtime):
            out.append(
                TrackIntegrity(
                    track_id=track_id,
                    qid=qid,
                    path=str(path),
                    state=IntegrityState.VERIFIED,
                )
            )
            continue
        current = integrity.read_stamp(path)
        out.append(
            TrackIntegrity(
                track_id=track_id,
                qid=qid,
                path=str(path),
                state=integrity.classify(recorded=recorded, current=current),
                measured=current is not None,
                stamp=current,
            )
        )
    return out


async def verify_album_files(
    album: Album, *, settings: Settings | None = None
) -> AlbumIntegrity:
    """Measure one release's files and say what the disk makes of the database.

    The fourth principle of the integrity model — verify the disk before trusting
    the database — as one call. Every operation in this module is aimed by
    ``album.path`` and ``track.path``, and both are *claims* about a filesystem
    that other programs can write to: a tag editor, a re-rip, a media server with
    write access, a restore from a backup taken before the last upgrade. Nothing
    else in Qobuzarr notices any of that.

    Read-only and safe to call anywhere. Rows whose recorded path is not inside
    ``LIBRARY_PATH`` are skipped rather than reported: nothing here may touch
    them either way, so a verdict about them would be noise on a list that exists
    to be acted on.
    """
    conf = effective_for(settings or get_settings())
    items: list[tuple[str, str | None, Path, FileStamp | None]] = []
    for track in album.tracks:
        if not track.path:
            continue
        try:
            path = resolve_in_library(track.path, conf.library_path)
        except LibraryPathError as exc:
            logger.debug("Not verifying %s: %s", track.id, exc)
            continue
        items.append(
            (str(track.id), getattr(track, "qid", None), path, _recorded_stamp(track))
        )

    tracks = await asyncio.to_thread(_classify_files, items) if items else []
    return AlbumIntegrity(album_id=str(album.id), tracks=tracks)


async def _assert_unaltered(
    album: Album, *, settings: Settings | None = None
) -> AlbumIntegrity:
    """Refuse an album whose files are not the files that were recorded.

    The fifth gate, and the only one that asks the disk rather than the database.
    ``resolve_in_library`` proves a path is inside the library,
    :func:`_assert_exclusive` proves the directory is this release's own and
    :func:`_assert_not_busy` proves the download worker is not in it — all three
    reason entirely from rows, and all three pass happily for an album whose
    folder now holds somebody else's music.

    Only ``REPLACED`` is refused, and the other four states are deliberately let
    through:

    * ``RETAGGED`` — the bytes moved but the sample count did not, so the audio is
      provably the same recording. The row still describes what it says it does;
      only the baseline is out of date, and the operation about to run will
      refresh it (see :func:`rebaseline_album`).
    * ``MISSING`` — a file the database expects and cannot find. Every operation
      here already treats a vanished file as a stale row to clear rather than an
      error, and demoting a release whose files are gone belongs to
      ``scheduler._verify_library()``, which reverses when the mount comes back.
      Blocking here would mean an unmounted share refuses the very deletes and
      re-files that clean up after it.
    * ``UNKNOWN`` — nothing was ever baselined, so nothing is being contradicted.
      This is the state of every file in a library the integrity pass has not
      reached yet, and reading it as tampering would refuse every operation in
      this module on the day the feature ships.

    Note what is *not* gated: :func:`write_nfo` merges a sidecar and never reads
    or rewrites an audio file, so a release whose audio was replaced still gets a
    correct NFO for whatever the database currently believes.

    Raises:
        StaleLibraryError: naming the files whose audio no longer agrees.
    """
    found = await verify_album_files(album, settings=settings)
    replaced = found.replaced
    if not replaced:
        return found

    names = ", ".join(Path(track.path).name for track in replaced[:3])
    if len(replaced) > 3:
        names += ", …"
    raise StaleLibraryError(
        f"{album.display_title}: {len(replaced)} file(s) on disk are not the ones "
        f"Qobuzarr recorded ({names}). Something else has rewritten this release, "
        "so what the database says about it is out of date. Re-scan the library "
        "first — acting on a stale record is how the wrong folder gets moved."
    )


def _read_stamps(paths: Sequence[Path]) -> list[FileStamp | None]:
    """Measure each path fully, in order. **Synchronous** — blocking I/O."""
    return [integrity.read_stamp(path) for path in paths]


async def rebaseline_album(
    album: Album, *, settings: Settings | None = None
) -> int:
    """Re-measure a release Qobuzarr has just written to, and store the result.

    Every operation here that changes a file has to end in this, and the reason
    is that the integrity check cannot tell *who* changed a file — only that the
    bytes disagree with the recorded ones. A re-tag rewrites the container of
    every track, so a re-tag that did not re-baseline would leave the whole
    release looking, on the next pass, exactly like somebody else's tagger having
    been through it: the gate above would then refuse the next re-file, the next
    delete and the next re-tag, on the strength of our own edit.

    The files are measured rather than inferred. A move is *usually* byte-for-byte
    — ``os.replace`` within a filesystem is a rename — and it would be cheaper to
    carry the old ``content_hash`` across and re-stat for the new mtime. It is not
    done, because ``shutil.move`` falls back to copy-then-delete across
    filesystems, and a copy that went wrong is precisely the case where recording
    an unmeasured hash would bury the evidence: the tripwire would never fire
    again on that file. The cost is one hash per file this operation actually
    touched, on an operation that has already rewritten them.

    **It heals itself.** If the process dies between the write and this call, the
    next pass finds a hash that disagrees with the recorded one and reports the
    release as altered. That is a false positive and it costs exactly one
    re-identification: the audio is unchanged, so the same recording comes back,
    the row is re-baselined against the file as it now is, and the library is
    correct again. There is no half-applied state to unwind and no window in
    which a wrong answer persists — which is why the crash-safety story here is
    "let it re-verify" rather than a journal.

    Runs on rows the caller already has in its session and does not commit; the
    caller's own commit is what persists it, so the baseline lands in the same
    transaction as the paths it describes.

    Returns:
        How many files were re-measured. An album with no ``Track`` rows — one
        the re-tag stood the files themselves in for — returns 0 and gets no
        baseline, because there is nothing to record one on. The next disk scan
        writes the rows and measures them.
    """
    conf = effective_for(settings or get_settings())
    items: list[tuple[Track, Path]] = []
    for track in album.tracks:
        if not track.path:
            continue
        try:
            items.append((track, resolve_in_library(track.path, conf.library_path)))
        except LibraryPathError as exc:  # pragma: no cover - a row we cannot touch
            logger.debug("Not re-baselining %s: %s", track.id, exc)

    measured = 0
    if items:
        stamps = await asyncio.to_thread(_read_stamps, [path for _, path in items])
        when = utcnow()
        for (track, _path), stamp in zip(items, stamps):
            if stamp is None:
                # Gone, or unreadable. Leaving the old baseline standing is the
                # honest record: it is what was last measured, and the file being
                # absent is a separate fact the verify pass already reports.
                continue
            track.file_size = stamp.size
            track.file_mtime = stamp.mtime
            track.content_hash = stamp.content_hash
            if stamp.sample_count is not None:
                # A container that publishes no sample count leaves the previous
                # figure alone rather than clearing it: "not measured" is not
                # evidence that the audio changed, and classify() already refuses
                # to read a None as agreement.
                track.sample_count = stamp.sample_count
            track.verified_at = when
            measured += 1

    # Recomputed, never left stale: after a re-tag every member hash is different,
    # and a digest describing the release as it was before the write would report
    # it as altered on every pass from now on. The ordering is
    # ``LibraryScanner._stamp_release``'s, and has to stay that way — the two are
    # the only writers of this column, and a different order is a different digest
    # for the same release.
    ordered = sorted(
        album.tracks,
        key=lambda track: (track.media_number or 1, track.track_number or 0, track.id),
    )
    album.content_digest = integrity.album_content_digest(
        [track.content_hash for track in ordered]
    )
    return measured


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
    conf = effective_for(settings or get_settings())
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
    conf = effective_for(settings or get_settings())
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
    conf = effective_for(settings or get_settings())
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

    The only place in Qobuzarr that removes files permanently, and it only ever
    runs inside ``Settings.trash_dir`` on an explicit request. Pass *entry_id*
    for one batch, omit it for all of them.

    Returns:
        ``(batches removed, bytes reclaimed)``.
    """
    conf = effective_for(settings or get_settings())
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
class RefileEstimate:
    """How much of the library one naming template would disturb. Read-only.

    Every album lands in exactly **one** of ``frozen`` / ``blocked`` /
    ``would_refile`` / ``in_place``, so they sum to ``considered``. That is
    structural rather than a rule to remember: one album, one branch, one loop.
    ``moves_directory`` is a *subset* of ``would_refile`` — a release whose files
    are only renamed inside the folder they already occupy has not moved.
    """

    template: str
    considered: int = 0
    would_refile: int = 0
    moves_directory: int = 0
    in_place: int = 0
    blocked: int = 0
    frozen: int = 0
    truncated: bool = False
    """True when the walk hit ``limit``: the figures are a floor, not a total."""


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
        StaleLibraryError: the files are not the ones that were recorded.
    """
    conf = effective_for(settings or get_settings())
    await _assert_not_busy(session, album)
    # Before the path arithmetic below, not after: every step of it is derived
    # from rows describing files, and a delete aimed by a stale row is the one
    # mistake in this module that the trash cannot make cheap to notice.
    await _assert_unaltered(album, settings=conf)

    result = DeleteResult(album_id=album.id)
    if album.path:
        root = Path(conf.library_path).expanduser().resolve()
        try:
            target: Path | None = resolve_in_library(album.path, root)
        except LibraryPathError as exc:
            # A path that has already vanished is not an error worth blocking
            # on: the database is simply stale, and clearing it is the fix.
            logger.info("Nothing to trash for %s: %s", album.id, exc)
            target = None
        if target is not None:
            # Before anything moves. A shared folder is the one case the trash's
            # second chance does not cover, because the user would not know the
            # rest of the artist had gone with it and so would never restore it.
            await _assert_exclusive(session, album, target, root)
            try:
                result.trashed = await asyncio.to_thread(
                    move_to_trash, target, settings=conf, reason=reason, album=album
                )
            except LibraryError as exc:
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


@dataclass(slots=True)
class QuarantineResult:
    """Outcome of :func:`quarantine_corrupt_files`."""

    albums_checked: int = 0
    files_trashed: int = 0
    tracks_cleared: int = 0
    albums_flagged: int = 0
    errors: list[str] = field(default_factory=list)

    @property
    def summary(self) -> str:
        if not self.files_trashed:
            return f"{self.albums_checked} album(s) checked, nothing corrupt"
        return (
            f"{self.files_trashed} unplayable file(s) to the trash from "
            f"{self.albums_flagged} album(s)"
        )


async def corrupt_file_count(session: AsyncSession) -> int:
    """How many unplayable files are sitting in the library right now.

    The same filters :func:`quarantine_corrupt_files` selects on, with the action
    removed — a verdict recorded, a file still on disk, and a release nobody has
    muted. It exists because housekeeping reports this figure instead of acting
    on it: trashing a broken file clears the very count that would have told
    somebody to replace it, so the nightly pass counts and leaves the file where
    it is. See :func:`app.core.scheduler._count_corrupt`.

    Kept beside the quarantine on purpose. Two statements of "which files are
    broken" would drift, and the direction they would drift in is a number on the
    Integrity screen that no button can ever act on.
    """
    from app.models import TrackMetadata  # noqa: PLC0415 - keeps the import graph flat

    return int(
        (
            await session.execute(
                select(func.count(Track.id))
                .join(TrackMetadata, TrackMetadata.track_id == Track.id)
                .join(Album, Album.id == Track.album_id)
                .where(
                    TrackMetadata.fingerprint_state == FingerprintState.CORRUPT,
                    Track.path.is_not(None),
                    Album.mute_integrity.is_not(True),
                )
            )
        ).scalar_one()
        or 0
    )


async def quarantine_corrupt_files(
    session: AsyncSession,
    *,
    settings: Settings | None = None,
    limit: int = 200,
) -> QuarantineResult:
    """Trash files the fingerprinter could not decode, and mark them missing.

    A file Chromaprint cannot decode is a file no music player can play, so
    leaving it on disk means a library that looks complete and is not. The
    verdict comes from :mod:`app.enrich.acoustid`, which only ever *records* it —
    writing to the library is this module's job and nobody else's, so the acting
    on it happens here, through the same gates as every other operation:

    1. ``resolve_in_library`` before anything is touched;
    2. ``_assert_not_busy`` — a download writing ``.part`` files into that folder
       is not corruption, it is work in progress;
    3. ``_assert_unaltered`` — a corruption verdict is a statement about the bytes
       that were fingerprinted, and a file that has been replaced since is not
       those bytes. Trashing it would act on a verdict about a file that no
       longer exists, which is exactly what somebody replacing a bad rip with a
       good one looks like;
    4. the trash, never ``os.remove``. A false positive must be recoverable, and
       "this file is broken" is exactly the kind of verdict that should be.

    The track goes back to ``pending`` and the album to ``wanted``, which is what
    makes the gap visible and re-fetchable. Nothing is queued: that stays an
    explicit user action, like every other path that could start a download.

    Releases with ``Album.mute_integrity`` set are excluded, in the SQL rather
    than in the loop, so a muted release never reaches a gate at all. That flag
    exists for the known-good rip that fails a strict check — a live recording,
    an old transfer, something ``fpcalc`` simply cannot decode — and this is the
    one action in the codebase it suppresses, because this is the one that moves
    a file out of the library while nobody is watching. It hides the **alarm**
    and never the **fact**: the fingerprint verdict is still recorded, and the
    release still reports its real ``integrity_state`` and ``corrupt_tracks`` on
    its own detail payload. Muting is not a claim that the file is fine, it is an
    instruction not to act on the claim that it is not.
    """
    from app.models import TrackMetadata  # noqa: PLC0415 - keeps the import graph flat

    conf = effective_for(settings or get_settings())
    result = QuarantineResult()

    rows = (
        (
            await session.execute(
                select(Track)
                .join(TrackMetadata, TrackMetadata.track_id == Track.id)
                .join(Album, Album.id == Track.album_id)
                .where(
                    TrackMetadata.fingerprint_state == FingerprintState.CORRUPT,
                    Track.path.is_not(None),
                    Album.mute_integrity.is_not(True),
                )
                .limit(max(1, limit))
            )
        )
        .scalars()
        .all()
    )
    if not rows:
        return result

    by_album: dict[str, list[Track]] = {}
    for track in rows:
        by_album.setdefault(str(track.album_id), []).append(track)
    result.albums_checked = len(by_album)

    for album_id, tracks in by_album.items():
        album = await session.get(Album, album_id)
        if album is None:  # pragma: no cover - deleted mid-run
            continue
        try:
            await _assert_not_busy(session, album)
            await _assert_unaltered(album, settings=conf)
        except LibraryBusyError as exc:
            # Not corruption — the worker is writing into that folder right now.
            logger.info("Skipping quarantine for %s: %s", album_id, exc)
            result.errors.append(str(exc))
            continue
        except StaleLibraryError as exc:
            # The verdict was recorded about bytes that are no longer there. The
            # replacement gets fingerprinted on a later pass, and if it really is
            # broken too this comes back round with evidence about the file that
            # is actually on disk.
            logger.info("Skipping quarantine for %s: %s", album_id, exc)
            result.errors.append(str(exc))
            continue

        flagged = False
        for track in tracks:
            try:
                await asyncio.to_thread(
                    move_to_trash,
                    track.path,
                    settings=conf,
                    reason="corrupt",
                    album=album,
                )
                result.files_trashed += 1
            except LibraryError as exc:
                # Already gone is not a failure: the database was stale and
                # clearing it below is the fix.
                logger.info("Nothing to quarantine for track %s: %s", track.id, exc)

            track.path = None
            track.file_size = None
            track.downloaded_at = None
            track.status = TrackStatus.PENDING
            result.tracks_cleared += 1
            flagged = True

        if flagged:
            result.albums_flagged += 1
            if album.status is AlbumStatus.DOWNLOADED:
                album.status = (
                    AlbumStatus.WANTED if album.monitored else AlbumStatus.SKIPPED
                )
            _log(
                session,
                event="library.corrupt",
                message=(
                    f"{album.display_title}: {len(tracks)} unplayable file(s) "
                    "moved to the trash and marked missing"
                ),
                album=album,
                level=ActivityLevel.WARNING,
            )

    await session.commit()
    return result


async def _enrichment_tags(
    session: AsyncSession, album: Album
) -> dict[str, dict[str, str]]:
    """The enrichment tag values for every track of *album*, keyed by track id.

    Three batch queries for a whole album, run on the event loop, so the worker
    thread that does the actual tagging only ever sees plain strings.
    """
    from app.core.enricher import (  # noqa: PLC0415 - avoids an import cycle
        load_album_metadata,
        load_artist_metadata,
        load_track_metadata,
    )
    from app.core.nfo import tag_values  # noqa: PLC0415

    tracks = list(album.tracks)
    if not tracks:
        return {}
    album_meta = (await load_album_metadata(session, [album.id])).get(str(album.id))
    artist_meta = (
        (await load_artist_metadata(session, [album.artist_id])).get(str(album.artist_id))
        if album.artist_id
        else None
    )
    track_meta = await load_track_metadata(session, [str(t.id) for t in tracks])
    return {
        str(track.id): tag_values(
            artist_meta=artist_meta,
            album_meta=album_meta,
            track_meta=track_meta.get(str(track.id)),
        )
        for track in tracks
    }


async def _album_level_tags(session: AsyncSession, album: Album) -> dict[str, str]:
    """The enrichment tags that describe the *release*, not one recording.

    What an album with no ``Track`` rows can still be told: the release and
    release-group MBIDs, the artist MBID and ISNI, the barcode and catalogue
    number. The recording-level ids are absent because there is nothing to hang
    them on, which is honest — writing a recording id onto a file nobody matched
    is how a wrong id gets into a library.
    """
    from app.core.enricher import (  # noqa: PLC0415 - avoids an import cycle
        load_album_metadata,
        load_artist_metadata,
    )
    from app.core.nfo import tag_values  # noqa: PLC0415

    album_meta = (await load_album_metadata(session, [album.id])).get(str(album.id))
    artist_meta = (
        (await load_artist_metadata(session, [album.artist_id])).get(str(album.artist_id))
        if album.artist_id
        else None
    )
    return tag_values(artist_meta=artist_meta, album_meta=album_meta, track_meta=None)


@dataclass(slots=True)
class _AdoptedTrack:
    """One file on disk standing in for the ``Track`` row it never had.

    Duck-typed to what :func:`app.core.tagger.build_tags` reads. The track-level
    values come from the file's own tags — the only place they exist for an
    album Qobuzarr did not download itself.
    """

    id: str
    path: str
    title: str
    track_number: int
    media_number: int
    performer: str = ""
    version: str = ""
    composer: str = ""
    isrc: str = ""


def _adopted_tracks(directory: Path) -> list[_AdoptedTrack]:
    """Read every audio file under *directory* into a stand-in track. **Sync.**"""
    found: list[_AdoptedTrack] = []
    for path in sorted(directory.rglob("*")):
        if path.suffix.lower() not in scanner.AUDIO_EXTENSIONS:
            continue
        if path.name.startswith(".") or not path.is_file():
            continue
        scanned = scanner.read_track(path)
        if scanned is None:
            # Unreadable by mutagen, so there is nothing to preserve and nothing
            # safe to write. Left alone rather than tagged from a guess.
            continue
        found.append(
            _AdoptedTrack(
                id=str(path),
                path=str(path),
                title=scanned.title or path.stem,
                track_number=scanned.track_number,
                media_number=scanned.media_number or 1,
                performer=scanned.artist,
            )
        )
    return found


@dataclass(slots=True)
class NfoResult:
    """Outcome of :func:`write_nfo`."""

    album_id: str | None = None
    artist_written: bool = False
    album_written: bool = False
    skipped: str | None = None

    @property
    def summary(self) -> str:
        if self.skipped:
            return self.skipped
        written = [
            name
            for name, done in (("artist.nfo", self.artist_written), ("album.nfo", self.album_written))
            if done
        ]
        return ", ".join(written) or "nothing to write"


def _write_atomic(target: Path, text: str) -> None:
    """Replace *target* with *text* via a temp file in the same directory.

    Written beside the target and renamed, so a media server scanning mid-write
    never reads half a file, and a crash leaves the previous NFO intact rather
    than a truncated one. ``Path.replace`` is atomic within a filesystem, which
    the sibling temp file guarantees.
    """
    temp = target.with_name(f".{target.name}.tmp")
    temp.write_text(text, encoding="utf-8")
    temp.replace(target)


def _merge_write(
    target: Path, generated: str, merge: Any, locked_error: type[Exception]
) -> bool:
    """Merge *generated* into whatever is already at *target* and write it back.

    Returns ``False`` when the existing file is locked, which is a refusal rather
    than a failure — that flag is how someone tells their media server to stop
    editing a file, and Qobuzarr is not an exception to the instruction.
    """
    existing = None
    if target.is_file():
        try:
            existing = target.read_text(encoding="utf-8-sig")
        except OSError as exc:
            raise LibraryError(f"Cannot read {target}: {exc}") from exc
    try:
        merged = merge(existing, generated)
    except locked_error:
        logger.info("Leaving %s alone: it is locked against edits", target)
        return False
    if existing is not None and merged == existing:
        # Nothing changed. Not rewriting keeps the mtime stable, so a media
        # server watching the directory is not told to re-scan for nothing.
        return True
    _write_atomic(target, merged)
    return True


async def write_nfo(
    session: AsyncSession,
    album: Album,
    *,
    settings: Settings | None = None,
) -> NfoResult:
    """Write ``album.nfo`` for a release and ``artist.nfo`` for its artist.

    Rendering is :mod:`app.core.nfo`'s job and is pure; this is the half that
    touches the library, so it goes through the same gates as everything else
    here — the paths are proved to be inside ``LIBRARY_PATH`` and a busy album is
    refused, because a folder the download loop is writing into is not one to be
    dropping files into.

    An existing NFO is **merged into, never replaced**. Real libraries already
    have these files — Jellyfin, Emby and Kodi write them — and they hold a
    biography, artwork paths and that server's own ids, none of which Qobuzarr
    knows and all of which a wholesale rewrite would destroy. Only the elements
    Qobuzarr owns are updated, and ``<lockdata>true</lockdata>`` is honoured by
    leaving the file alone entirely.

    That is why this is the one operation here with no trash copy and needs none:
    it only ever replaces facts it generated, and those can be regenerated.
    """
    from app.core.enricher import (  # noqa: PLC0415 - avoids an import cycle
        load_album_metadata,
        load_artist_metadata,
        load_track_metadata,
    )
    from app.core.nfo import (  # noqa: PLC0415
        ALBUM_NFO,
        ARTIST_NFO,
        NfoLocked,
        merge_nfo,
        render_album_nfo,
        render_artist_nfo,
    )

    conf = effective_for(settings or get_settings())
    result = NfoResult(album_id=album.id)
    if not conf.nfo_enabled:
        result.skipped = "NFO writing is switched off"
        return result
    if not album.path:
        result.skipped = "nothing on disk"
        return result

    await _assert_not_busy(session, album)
    try:
        album_dir = resolve_in_library(album.path, conf.library_path)
    except LibraryPathError as exc:
        result.skipped = str(exc)
        return result
    if not album_dir.is_dir():
        result.skipped = f"{album_dir} is not there any more"
        return result

    artist = album.artist
    album_meta = (await load_album_metadata(session, [album.id])).get(str(album.id))
    artist_meta = (
        (await load_artist_metadata(session, [album.artist_id])).get(str(album.artist_id))
        if album.artist_id
        else None
    )
    tracks = list(album.tracks)
    track_meta = await load_track_metadata(session, [str(t.id) for t in tracks])

    album_xml = render_album_nfo(
        album,
        album_meta,
        tracks,
        track_meta,
        artist_name=getattr(artist, "name", None),
        artist_meta=artist_meta,
    )
    try:
        result.album_written = await asyncio.to_thread(
            _merge_write, album_dir / ALBUM_NFO, album_xml, merge_nfo, NfoLocked
        )
    except LibraryError:
        raise
    if not result.album_written:
        result.skipped = f"{ALBUM_NFO} is locked against edits"

    # The artist folder is the album folder's parent under every naming template
    # that starts with {artist}; when it does not, the parent is still the right
    # place for a media server to look, so this is correct either way.
    artist_dir = album_dir.parent
    try:
        artist_dir = resolve_in_library(artist_dir, conf.library_path)
    except LibraryPathError:
        # The album sits directly in the library root. There is no artist folder
        # to put an artist.nfo in, and writing one at the root would describe the
        # whole library as one artist.
        return result

    if artist is not None:
        artist_xml = render_artist_nfo(artist, artist_meta)
        result.artist_written = await asyncio.to_thread(
            _merge_write, artist_dir / ARTIST_NFO, artist_xml, merge_nfo, NfoLocked
        )

    return result


async def write_library_nfo(
    session: AsyncSession,
    *,
    settings: Settings | None = None,
    artist_id: str | None = None,
    limit: int = 5000,
) -> list[NfoResult]:
    """Write NFOs across the library, or across one artist's releases."""
    conf = effective_for(settings or get_settings())
    if not conf.nfo_enabled:
        return []

    stmt = select(Album).where(Album.path.is_not(None))
    if artist_id:
        stmt = stmt.where(Album.artist_id == str(artist_id))
    albums = (
        (await session.execute(stmt.limit(max(1, limit)))).unique().scalars().all()
    )

    results: list[NfoResult] = []
    for album in albums:
        try:
            results.append(await write_nfo(session, album, settings=conf))
        except LibraryError as exc:
            # One busy or vanished album must not stop the sweep.
            results.append(NfoResult(album_id=album.id, skipped=str(exc)))
    return results


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


def _disk_quality(directory: Path) -> tuple[int | None, int | None, float | None]:
    """The same answer as :func:`_owned_quality`, read from the files. **Sync.**

    Only the download loop writes ``Track`` rows, so every album the disk scan
    adopted has none — which is most of a pre-existing library. Without this the
    quality tag has nowhere to come from but the catalogue maximum, and re-file
    would name a folder of 128 kbps MP3s ``[FLAC 24-44.1]``.

    ``read_track`` reports the bit depth and sampling rate mutagen finds in the
    file, which is exactly what ``quality.track_format_id`` derives a format from
    when the downloader recorded none. A file it cannot read contributes nothing
    rather than a guess.
    """
    scanned = []
    for path in sorted(directory.rglob("*")):
        if path.suffix.lower() not in scanner.AUDIO_EXTENSIONS:
            continue
        if path.name.startswith(".") or not path.is_file():
            continue
        track = scanner.read_track(path)
        if track is not None:
            scanned.append(track)
    return _owned_quality(scanned)


async def plan_refile(
    session: AsyncSession, album: Album, settings: Settings | None = None
) -> RefilePlan:
    """Work out where *album*'s files should be, without moving anything.

    Every bulk action in the UI shows this first. ``blocked`` carries the reason
    an album cannot be re-filed — a busy download, a path outside the library, a
    destination already occupied, ``freeze_path`` — so a preview lists the
    problems instead of discovering them one at a time halfway through.

    An explicitly supplied ``settings`` is taken as **already effective**.
    Re-applying the overlay over a caller's base would silently restore the
    stored ``naming_template`` row on top of a *candidate* template, which is
    how the library-wide estimate would report a confident figure for the wrong
    template.
    """
    conf = settings if settings is not None else get_effective_settings()
    artist = album.artist
    artist_name = getattr(artist, "name", None) or album.artist_id

    plan = RefilePlan(
        album_id=album.id,
        album_title=album.display_title,
        artist_name=artist_name,
        current_dir=album.path or "",
        target_dir="",
    )

    root = Path(conf.library_path).expanduser().resolve()
    try:
        current = resolve_in_library(album.path, root)
    except LibraryPathError as exc:
        plan.blocked = str(exc)
        return plan
    if not current.is_dir():
        plan.blocked = f"{current} is not a directory any more."
        return plan
    plan.current_dir = str(current)

    if album.freeze_path:
        # Reported as a block rather than raised, like every other refusal in
        # this function, and reported *early*: a frozen release is settled before
        # anything is measured, so there is no reason to spend a thread hashing
        # its quality to render a target directory it will never move to.
        plan.blocked = _frozen_path_reason(album)
        return plan

    try:
        await _assert_exclusive(session, album, current, root)
    except SharedDirectoryError as exc:
        plan.blocked = str(exc)
        return plan

    try:
        await _assert_unaltered(album, settings=conf)
    except StaleLibraryError as exc:
        # Reported as a block rather than raised, like every other refusal here:
        # a library-wide preview should list the releases something else has
        # rewritten, not stop at the first one.
        plan.blocked = str(exc)
        return plan

    # The quality tag names what is *in* the folder. Track rows are the cheap
    # source and the disk is the truthful one; the catalogue maximum is neither,
    # and using it renames a folder of MP3s "[FLAC 24-44.1]".
    format_id, bit_depth, sampling_rate = _owned_quality(album.tracks)
    if format_id is None:
        format_id, bit_depth, sampling_rate = await asyncio.to_thread(
            _disk_quality, current
        )
    if format_id is None and naming.template_uses_quality(conf):
        # Unknown always means no. Rendering the tag empty would strip a correct
        # one off a folder that already carries it, which is a wrong move made
        # for the same reason a fabricated tag is.
        plan.blocked = (
            "Cannot tell what quality the files in this folder are, and the "
            "naming template needs it. Re-tag or re-download this release first."
        )
        return plan

    target_dir = naming.render_album_dir(
        artist or artist_name,
        album,
        conf,
        format_id=format_id,
        bit_depth=bit_depth,
        sampling_rate=sampling_rate,
    ).resolve()
    # Resolved, because ``current`` has had its symlinks resolved and a library
    # root that is itself a symlink would otherwise make every album look
    # misfiled.
    plan.target_dir = str(target_dir)

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

    ``freeze_path`` is checked here as well as in the plan, and deliberately
    *before* the plan is consulted. A caller may pass a ``plan`` it built earlier
    — that is what the preview-then-apply flow does — and a plan built before the
    flag was set, or by hand in a test, carries no ``blocked`` reason to trip
    over. A lock only the planner honours is a lock with a hole in it.

    Raises:
        FrozenPathError: when this release's path is frozen.
        LibraryError: when the plan is blocked or a move fails.
    """
    conf = effective_for(settings or get_settings())
    if album.freeze_path:
        raise FrozenPathError(_frozen_path_reason(album))
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

    # After the paths are updated and before the commit: the baseline describes
    # the files where they are now, and it lands in the same transaction as the
    # rows that say where that is.
    await rebaseline_album(album, settings=conf)

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

    Raises:
        LibraryBusyError: the download worker is writing into this folder.
        StaleLibraryError: the files are not the ones that were recorded.
    """
    conf = effective_for(settings or get_settings())
    await _assert_not_busy(session, album)
    # The tags about to be written describe the release the database believes is
    # here. If the audio has been replaced since, they describe something else,
    # and this is the operation that would write that mistake into every file.
    await _assert_unaltered(album, settings=conf)

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
    # Resolved here, on the event loop, and handed to the worker thread as plain
    # strings. tag_file runs in a thread where touching an ORM relationship would
    # raise MissingGreenlet and fail the whole track.
    enrichment = await _enrichment_tags(session, album)

    tracks: list[Any] = list(album.tracks)
    shared_tags: Mapping[str, str] | None = None
    if not tracks and album.path:
        # Only the download loop writes Track rows, so every album the disk scan
        # adopted has none — which for a pre-existing library is all of them. The
        # loop below would then run zero times and report "0 tagged" as success,
        # making the whole re-tag sweep a no-op on exactly the albums it was
        # meant for. Stand the files themselves in for the missing rows: what is
        # on the file supplies the track-level values, the database supplies the
        # release-level ones, and enrichment applies per album rather than per
        # track because there are no track rows to key it by.
        try:
            tracks = await asyncio.to_thread(
                _adopted_tracks, resolve_in_library(album.path, conf.library_path)
            )
            shared_tags = await _album_level_tags(session, album)
        except LibraryPathError as exc:
            logger.info("Nothing to re-tag for %s: %s", album.id, exc)

    for track in tracks:
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
                extra_tags=(
                    shared_tags
                    if shared_tags is not None
                    else enrichment.get(str(track.id))
                ),
            )
        except Exception as exc:  # noqa: BLE001 - one bad file must not stop the rest
            logger.warning("Could not tag %s: %s", path, exc)
            result.failed += 1
            continue
        if ok:
            result.tagged += 1
        else:
            result.failed += 1

    if result.tagged:
        # Every tagged file has a different hash now. Without this the next pass
        # reads our own edit as tampering and the gate above refuses everything.
        await rebaseline_album(album, settings=conf)

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

    So are albums with ``freeze_path`` set, and that is the one exception to
    "blocked albums are listed". The other blocks are *problems* — a stale
    baseline, an occupied destination, a folder shared with another release —
    and listing them is the whole point, because each is something a person can
    go and fix. A frozen path is not a problem, it is the answer somebody already
    gave; surfacing it in a work list every night is asking the same settled
    question forever. It is still refused by name if anything reaches
    :func:`refile_album` for it directly.

    Like :func:`plan_refile`, an explicitly supplied ``settings`` is taken as
    already effective.
    """
    conf = settings if settings is not None else get_effective_settings()
    plans = []
    for album in await albums_on_disk(session, artist_id=artist_id, limit=limit):
        if album.freeze_path:
            continue
        plan = await plan_refile(session, album, conf)
        if plan.needed or plan.blocked:
            plans.append(plan)
    return plans


async def estimate_library_refile(
    session: AsyncSession,
    *,
    template: str | None = None,
    artist_id: str | None = None,
    settings: Settings | None = None,
    limit: int = 2000,
) -> RefileEstimate:
    """Count what *template* would disturb. Moves nothing, writes nothing.

    ``template`` is the **candidate** — the one somebody is typing — and
    ``None`` means the one in force. It is laid over the effective settings
    *after* the overlay, never before; see :func:`plan_refile`.

    This reuses :func:`plan_refile`, the one planner, so the figure and the
    Health ▸ Tidy preview can never disagree. It does **not** go through
    :func:`plan_library_refile`, whose contract is a work list: that drops
    frozen releases and releases already in place, and both are denominators
    here. A frozen release is *reported*, because saying nothing about a folder
    somebody asked not to move reads as a folder that would move.
    """
    conf = settings if settings is not None else get_effective_settings()
    if template is not None:
        conf = conf.model_copy(update={"naming_template": template})

    albums = await albums_on_disk(session, artist_id=artist_id, limit=limit)
    est = RefileEstimate(
        template=conf.naming_template,
        considered=len(albums),
        truncated=len(albums) >= limit,
    )
    for album in albums:
        if album.freeze_path:
            est.frozen += 1
            continue
        plan = await plan_refile(session, album, conf)
        if plan.blocked:
            est.blocked += 1
        elif plan.needed:
            est.would_refile += 1
            if plan.moves_directory:
                est.moves_directory += 1
        else:
            est.in_place += 1
    return est
