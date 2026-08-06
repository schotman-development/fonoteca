"""Library scanning: reconcile what is already on disk with what Fonoteca knows.

This is the Lidarr "rescan folder" / "import library" equivalent.  It walks
``Settings.library_path``, reads tags out of every audio file it finds, groups
those files into albums, and matches each album against the database so that a
release the user already owns stops being *wanted*.

Three hard rules, in descending order of importance:

**It never writes to the filesystem.**  No renaming, no moving, no tag repair,
no deleting.  The scan is a read-only observation of the disk; the only thing
it mutates is the database.  A library assembled by Beets, Picard, Lidarr or by
hand is left exactly as it was found.

**It never marks anything wanted.**  Adoption only ever moves an album
*towards* "we have this" — ``wanted``/``skipped``/``failed`` may become
``downloaded``, never the other way round.  A scan can therefore only ever
reduce the amount of downloading Fonoteca would do, which is what makes it safe
to run unattended (see the opt-in rule in ``CLAUDE.md``).  Demoting an album
when its files vanish is a different job and lives in
:func:`app.core.scheduler.housekeeping`.

**It never fights the download worker.**  An album that is currently
``downloading``, or whose queue item is ``active``, is reported and skipped.

Layout assumptions
------------------
The scanner is deliberately forgiving about layout, because the point is to
adopt a library it did not create:

* Any directory containing audio files is a candidate album.
* A directory called ``CD 01``, ``Disc 2``, ``Disk3``, ``Vol. 2`` … is folded
  into its parent, so ``Album (2015)/CD 02/`` is one album with two discs
  rather than two albums.  ``{disc_prefix}`` folders written by Fonoteca's own
  downloader are folded by the same rule.
* The album title comes from the ``album`` tag when the files carry one, and
  otherwise from the directory name with a trailing ``(2015)`` year and a
  trailing ``[FLAC 24-96]`` quality tag stripped off.
* The artist is looked for in the ``albumartist`` tags, the parent directory
  name and the ``artist`` tags — *all* of them, because a compilation folder
  and a "The X Band" / "X" split are both common.  Whichever candidate produces
  an album match wins.

Matching
--------
Artist names are compared through :func:`artist_key` (accents folded, case and
punctuation discarded, a leading article dropped) and album titles through
:func:`app.core.indexer.dedupe_key`, which is the same normaliser the indexer
uses to collapse editions.  ``"Rumours [2013 Remastered]"`` on disk therefore
finds ``"Rumours"`` in the database.

An album is *complete* when the number of audio files reaches
``Settings.library_scan_complete_ratio`` of the track count Qobuz reported.  The
default is ``1.0`` — every track must be present — which errs towards leaving a
release on the Wanted page rather than towards quietly declaring a half-ripped
album finished.  Incomplete matches are reported separately so the gap is
visible instead of silent.

Everything the scan could not place is reported too, split into *unmatched*
(the artist is followed, the release is not in the database) and *unknown*
(nothing in the database resembles the artist at all).  Neither triggers any
Qobuz API call; turning an unknown folder into a followed artist is an explicit
user action on the Disk scan page.
"""

from __future__ import annotations

import asyncio
import json
import os
import re
import time
import unicodedata
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Sequence

import mutagen
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, get_settings
from app.core.indexer import dedupe_key
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    Artist,
    QueueItem,
    QueueState,
    Setting,
    Track,
    TrackStatus,
    utcnow,
)

__all__ = [
    "AUDIO_EXTENSIONS",
    "IGNORED_DIRECTORIES",
    "LAST_SCAN_KEY",
    "MAX_REPORTED",
    "REPORT_LIMITS",
    "LibraryScanner",
    "ScanResult",
    "ScannedAlbum",
    "ScannedTrack",
    "album_directory_title",
    "artist_key",
    "collect_albums",
    "load_last_scan",
    "read_track",
    "store_last_scan",
]

logger = get_logger(__name__)

#: Extensions the scanner treats as music. Anything else in the tree (artwork,
#: ``.nfo`` sidecars, ``.lrc`` lyrics, ``.DS_Store``) is ignored entirely.
AUDIO_EXTENSIONS: frozenset[str] = frozenset(
    {
        ".flac",
        ".mp3",
        ".m4a",
        ".m4b",
        ".mp4",
        ".aac",
        ".ogg",
        ".oga",
        ".opus",
        ".wav",
        ".aif",
        ".aiff",
        ".wma",
        ".ape",
        ".wv",
        ".alac",
        ".dsf",
        ".dff",
        ".mpc",
    }
)

#: Directory names that are never part of a music library. Skipping these keeps
#: NAS thumbnail caches and version-control metadata out of the walk.
IGNORED_DIRECTORIES: frozenset[str] = frozenset(
    {
        "@eaDir",
        ".git",
        ".svn",
        ".hg",
        "__MACOSX",
        ".Trash",
        ".Trash-1000",
        "#recycle",
        "$RECYCLE.BIN",
        "lost+found",
        ".stfolder",
        ".stversions",
    }
)

#: ``Setting`` key holding the JSON summary of the most recent scan.
LAST_SCAN_KEY = "library.last_scan"

#: Cap on how many detail rows a result carries, per bucket. A first scan of an
#: unimported library would otherwise produce a report too large to render or to
#: store. The counters stay exact; only the detail lists are capped, and the
#: result records how many entries were dropped.
#:
#: ``unknown_artists`` gets a far higher ceiling than the others because it is
#: rolled up per artist *and* because it is the work-list the bulk importer
#: consumes — truncating it would silently leave artists unimported, which is
#: exactly the failure this cap is supposed to make visible.
REPORT_LIMITS: dict[str, int] = {
    "partial": 200,
    "unmatched": 200,
    "unknown_artists": 5000,
}

#: Default ceiling for a bucket with no entry in :data:`REPORT_LIMITS`.
MAX_REPORTED = 200

#: ``CD 01`` / ``Disc 2`` / ``Disk3`` / ``Vol. 2`` — a disc sub-folder, not an album.
_DISC_DIRECTORY_RE = re.compile(
    r"^(?:cd|disc|disk|dvd|vol(?:ume)?)\s*[._\-]?\s*(\d{1,3})$", re.IGNORECASE
)

#: A trailing ``(2015)`` / ``[2015]`` year marker on a directory name.
_YEAR_SUFFIX_RE = re.compile(r"[\s._\-]*[\(\[\{](\d{4})[\)\]\}][\s._\-]*$")

#: A trailing ``[FLAC 24-96]``-style quality tag, which Fonoteca's own naming
#: template appends and which is never part of the title.
_QUALITY_SUFFIX_RE = re.compile(r"\s*\[[^\[\]]*\]\s*$")

#: The first four-digit year in a ``date``/``originaldate`` tag.
_YEAR_RE = re.compile(r"(\d{4})")

#: Leading track number in a tag value such as ``"3/12"`` or ``"03"``.
_NUMBER_RE = re.compile(r"(\d+)")

#: A definite/indefinite article at the start of an artist name.
_ARTICLE_RE = re.compile(r"^(?:the|a|an|le|la|les|el|los|las|der|die|das)\s+")

_PUNCT_RE = re.compile(r"[^\w\s]+", re.UNICODE)
_SPACE_RE = re.compile(r"\s+")

#: Album-artist values that mean "this is a compilation", not a real artist.
_VARIOUS_ARTISTS = frozenset({"various artists", "various", "va", "soundtrack", "ost"})


def _now() -> datetime:
    """Timezone-aware current time (kept separate so tests can monkeypatch)."""
    return datetime.now(timezone.utc)


# ---------------------------------------------------------------------------
# Normalisation
# ---------------------------------------------------------------------------
#: Latin letters Unicode decomposition cannot reach, because they are distinct
#: letters rather than a base plus a combining mark. Taggers ASCII-fold them
#: routinely, so ``Thorbjørn`` on Qobuz and ``Thorbjorn`` on disk must match.
_TRANSLITERATIONS = str.maketrans(
    {
        "ø": "o", "Ø": "O", "æ": "ae", "Æ": "AE", "œ": "oe", "Œ": "OE",
        "ß": "ss", "đ": "d", "Đ": "D", "ð": "d", "Ð": "D", "þ": "th",
        "Þ": "TH", "ł": "l", "Ł": "L", "ħ": "h", "ŧ": "t", "ı": "i",
        "ĸ": "k", "ŋ": "n", "ſ": "s",
    }
)


def _strip_accents(text: str) -> str:
    """Fold accented and stroked Latin characters onto their base letters.

    ``"María Dueñas"`` and ``"Maria Duenas"`` must be the same artist, and so
    must ``"Thorbjørn"`` and ``"Thorbjorn"`` — the first pair falls out of
    Unicode decomposition, the second needs the explicit table above. Scripts
    with no Latin equivalent (Cyrillic, CJK) pass through untouched.
    """
    decomposed = unicodedata.normalize("NFKD", text.translate(_TRANSLITERATIONS))
    return "".join(char for char in decomposed if not unicodedata.combining(char))


def artist_key(name: str | None) -> str:
    """Normalise an artist name down to the key used for disk/database matching.

    Accents are folded, case and punctuation discarded and a leading article
    dropped, so ``"The Robert Cray Band"`` becomes ``"robert cray band"`` and
    ``"Thorbjørn Risager & The Black Tornado"`` becomes
    ``"thorbjorn risager the black tornado"``.

    The article is stripped only from the *front*, which deliberately keeps
    ``"Robert Cray"`` and ``"The Robert Cray Band"`` apart: Qobuz lists them as
    two artists and the library keeps them in two folders.

    Returns an empty string for a name that normalises to nothing, which
    callers must treat as "no candidate" rather than as a match.
    """
    text = _strip_accents(str(name or "")).casefold()
    text = _PUNCT_RE.sub(" ", text)
    text = _SPACE_RE.sub(" ", text).strip()
    return _ARTICLE_RE.sub("", text).strip()


def album_directory_title(name: str) -> tuple[str, int | None]:
    """Split a directory name into ``(title, year)``.

    Handles both the shapes a library actually contains: ``"All Melody (2018)"``
    and Fonoteca's own ``"All Melody (2018) [FLAC 24-96]"``. The quality tag is
    removed first because it sits *after* the year.

    Returns the name unchanged and ``None`` when there is no year to find.
    """
    text = str(name or "").strip()
    stripped = _QUALITY_SUFFIX_RE.sub("", text).strip()
    # Only accept the bracket-stripped form if something is left of it; a folder
    # literally called "[Unsorted]" should keep its name.
    if stripped:
        text = stripped

    match = _YEAR_SUFFIX_RE.search(text)
    if match is None:
        return text.strip(" -_."), None

    year = int(match.group(1))
    title = text[: match.start()].strip(" -_.")
    if not title:
        return text.strip(" -_."), year
    return title, year


def _year_from(value: str | None) -> int | None:
    """Pull a plausible release year out of a ``date``-style tag value."""
    match = _YEAR_RE.search(str(value or ""))
    if match is None:
        return None
    year = int(match.group(1))
    return year if 1500 <= year <= 2200 else None


def _number(value: Any, default: int = 0) -> int:
    """Read a track/disc number out of ``"3"``, ``"03"``, ``"3/12"`` or ``(3, 12)``."""
    if isinstance(value, (list, tuple)) and value:
        value = value[0]
    if isinstance(value, int):
        return value
    match = _NUMBER_RE.search(str(value or ""))
    return int(match.group(1)) if match else default


# ---------------------------------------------------------------------------
# What one file and one directory look like once read
# ---------------------------------------------------------------------------
@dataclass(slots=True)
class ScannedTrack:
    """One audio file on disk, as the scanner understands it."""

    path: Path
    title: str = ""
    artist: str = ""
    album_artist: str = ""
    album: str = ""
    track_number: int = 0
    media_number: int = 1
    year: int | None = None
    duration: int | None = None
    bit_depth: int | None = None
    sampling_rate: float | None = None
    """Sampling rate in **kHz**, matching :attr:`app.models.Track.sampling_rate`."""
    file_size: int = 0
    total_tracks: int = 0
    total_discs: int = 0

    @property
    def extension(self) -> str:
        """Lowercase file extension including the dot."""
        return self.path.suffix.lower()


@dataclass(slots=True)
class ScannedAlbum:
    """A directory's worth of audio files, grouped into one release."""

    directory: Path
    tracks: list[ScannedTrack] = field(default_factory=list)
    title: str = ""
    year: int | None = None
    artist_candidates: list[str] = field(default_factory=list)
    total_tracks: int = 0
    """Track count claimed by the tags (``TRACKTOTAL``), 0 when unknown."""

    @property
    def file_count(self) -> int:
        """Number of audio files found in this album."""
        return len(self.tracks)

    @property
    def disc_count(self) -> int:
        """Number of distinct disc numbers across the files."""
        return len({track.media_number for track in self.tracks}) or 1

    @property
    def bytes_on_disk(self) -> int:
        """Total size of the audio files."""
        return sum(track.file_size for track in self.tracks)

    @property
    def artist_name(self) -> str:
        """Best single artist name for display purposes."""
        return self.artist_candidates[0] if self.artist_candidates else ""

    def as_dict(self) -> dict[str, Any]:
        """Plain-dict form for the JSON API and the scan report."""
        return {
            "path": str(self.directory),
            "artist": self.artist_name,
            "title": self.title,
            "year": self.year,
            "files": self.file_count,
            "discs": self.disc_count,
            "bytes": self.bytes_on_disk,
        }


# ---------------------------------------------------------------------------
# Reading the disk (synchronous — call through asyncio.to_thread)
# ---------------------------------------------------------------------------
def _tag_values(tags: Any, *names: str) -> list[str]:
    """Every non-empty value stored under any of *names*, in order."""
    out: list[str] = []
    for name in names:
        try:
            raw = tags.get(name)
        except Exception:  # noqa: BLE001 - some mutagen tag objects are picky
            raw = None
        if raw is None:
            continue
        values = raw if isinstance(raw, (list, tuple)) else [raw]
        for value in values:
            text = str(value).strip()
            if text and text not in out:
                out.append(text)
    return out


def _tag(tags: Any, *names: str) -> str:
    """First non-empty value stored under any of *names*."""
    values = _tag_values(tags, *names)
    return values[0] if values else ""


def read_track(path: Path) -> ScannedTrack | None:
    """Read one audio file into a :class:`ScannedTrack`.

    **Synchronous** — it blocks on file I/O, so call it through
    :func:`asyncio.to_thread` (which is what :meth:`LibraryScanner.scan` does).

    ``mutagen``'s *easy* interface is tried first because it gives FLAC, MP3 and
    MP4 the same tag names; a file it cannot parse that way falls back to the
    raw interface, and a file it cannot parse at all returns ``None`` rather
    than raising, so one corrupt track never aborts a scan.
    """
    audio: Any = None
    for easy in (True, False):
        try:
            audio = mutagen.File(str(path), easy=easy)
        except Exception:  # noqa: BLE001 - unreadable/corrupt files are expected
            audio = None
        if audio is not None:
            break
    if audio is None:
        return None

    tags = getattr(audio, "tags", None) or {}
    info = getattr(audio, "info", None)

    try:
        file_size = path.stat().st_size
    except OSError:
        file_size = 0

    sample_rate = getattr(info, "sample_rate", None)
    duration = getattr(info, "length", None)

    return ScannedTrack(
        path=path,
        title=_tag(tags, "title", "TIT2"),
        artist=_tag(tags, "artist", "TPE1"),
        album_artist=_tag(tags, "albumartist", "album artist", "TPE2"),
        album=_tag(tags, "album", "TALB"),
        track_number=_number(_tag(tags, "tracknumber", "TRCK"), 0),
        media_number=_number(_tag(tags, "discnumber", "TPOS"), 1) or 1,
        year=_year_from(_tag(tags, "originaldate", "date", "originalyear", "year", "TDRC")),
        duration=int(duration) if duration else None,
        bit_depth=getattr(info, "bits_per_sample", None) or None,
        # Qobuz reports kHz and so do the Track columns; mutagen reports Hz.
        sampling_rate=(float(sample_rate) / 1000.0) if sample_rate else None,
        file_size=file_size,
        total_tracks=_number(_tag(tags, "tracktotal", "totaltracks"), 0),
        total_discs=_number(_tag(tags, "disctotal", "totaldiscs"), 0),
    )


def _album_directory_for(directory: Path, root: Path) -> tuple[Path, int | None]:
    """Fold a ``CD 01``-style directory into its parent.

    Returns ``(album_directory, disc_number_from_the_folder_name)``. The fold
    never climbs above *root*, so a library whose top level happens to be called
    ``Disc 1`` still scans.
    """
    match = _DISC_DIRECTORY_RE.match(directory.name.strip())
    if match is None:
        return directory, None

    disc = int(match.group(1))
    parent = directory.parent
    # Fold only while the parent is still inside the library. A library whose
    # own root is called "Disc 1" must not be folded away into its container.
    if parent == root or root in parent.parents:
        return parent, disc
    return directory, disc


def _most_common(values: Sequence[str]) -> list[str]:
    """Distinct *values* ordered by how often they occur, most frequent first."""
    counts: dict[str, int] = {}
    order: dict[str, int] = {}
    for position, value in enumerate(values):
        text = value.strip()
        if not text:
            continue
        counts[text] = counts.get(text, 0) + 1
        order.setdefault(text, position)
    return sorted(counts, key=lambda text: (-counts[text], order[text]))


def collect_albums(
    root: Path,
    *,
    follow_symlinks: bool = False,
    on_error: Callable[[str], None] | None = None,
) -> tuple[list[ScannedAlbum], dict[str, int]]:
    """Walk *root* and group every audio file it holds into albums.

    **Synchronous and read-only.** Nothing under *root* is created, renamed or
    modified; the walk only ever calls ``stat`` and ``open``.

    Args:
        root: Library root to walk.
        follow_symlinks: Whether ``os.walk`` should descend into symlinked
            directories. Off by default — a self-referential link would
            otherwise loop forever.
        on_error: Called with a human-readable message for each directory or
            file that could not be read. Errors never abort the walk.

    Returns:
        ``(albums, stats)`` where *stats* carries ``directories``,
        ``audio_files`` and ``unreadable`` counters for the whole walk.
    """
    root = Path(root)
    stats = {"directories": 0, "audio_files": 0, "unreadable": 0}
    grouped: dict[Path, list[ScannedTrack]] = {}

    if not root.is_dir():
        if on_error is not None:
            on_error(f"Library root {root} does not exist")
        return [], stats

    def _walk_error(error: OSError) -> None:
        stats["unreadable"] += 1
        if on_error is not None:
            on_error(f"{getattr(error, 'filename', root)}: {error}")

    for dirpath, dirnames, filenames in os.walk(
        root, followlinks=follow_symlinks, onerror=_walk_error
    ):
        # Prune in place so os.walk never descends into them.
        dirnames[:] = sorted(
            name for name in dirnames if name not in IGNORED_DIRECTORIES and not name.startswith(".")
        )
        stats["directories"] += 1

        directory = Path(dirpath)
        audio_files = sorted(
            directory / name
            for name in filenames
            if Path(name).suffix.lower() in AUDIO_EXTENSIONS and not name.startswith(".")
        )
        if not audio_files:
            continue

        album_dir, folder_disc = _album_directory_for(directory, root)
        for file_path in audio_files:
            stats["audio_files"] += 1
            track = read_track(file_path)
            if track is None:
                stats["unreadable"] += 1
                if on_error is not None:
                    on_error(f"{file_path}: unreadable audio file")
                continue
            # The folder name wins over a missing/one-valued disc tag: a file in
            # "CD 02" that forgot to say so is still on disc two.
            if folder_disc is not None and track.media_number <= 1:
                track.media_number = folder_disc
            grouped.setdefault(album_dir, []).append(track)

    albums: list[ScannedAlbum] = []
    for album_dir, tracks in sorted(grouped.items()):
        albums.append(_build_album(album_dir, tracks, root))
    return albums, stats


def _build_album(album_dir: Path, tracks: list[ScannedTrack], root: Path) -> ScannedAlbum:
    """Turn a directory's tracks into a :class:`ScannedAlbum`."""
    folder_title, folder_year = album_directory_title(album_dir.name)

    tagged_titles = _most_common([track.album for track in tracks])
    title = tagged_titles[0] if tagged_titles else folder_title

    tagged_years = [track.year for track in tracks if track.year]
    year = tagged_years[0] if tagged_years else folder_year

    # Artist candidates, best guess first. All of them are tried against the
    # database; whichever produces an album match is the one that is used.
    candidates: list[str] = []
    for value in _most_common([track.album_artist for track in tracks]):
        if artist_key(value) not in _VARIOUS_ARTISTS:
            candidates.append(value)
    # The parent directory is authoritative in an Artist/Album library and is
    # the only signal at all when the files carry no album-artist tag.
    if album_dir.parent != root and album_dir.parent != album_dir:
        candidates.append(album_dir.parent.name)
    for value in _most_common([track.artist for track in tracks]):
        if artist_key(value) not in _VARIOUS_ARTISTS:
            candidates.append(value)

    seen: set[str] = set()
    ordered: list[str] = []
    for value in candidates:
        key = artist_key(value)
        if not key or key in seen:
            continue
        seen.add(key)
        ordered.append(value)

    # TRACKTOTAL is per *disc*, so a two-disc set tagged 15/15 and 12/12 has 27
    # tracks, not 15. Summing the per-disc maxima gets that right and degrades
    # to the single-disc answer for everything else.
    total_tracks = sum(
        max((t.total_tracks for t in tracks if t.media_number == disc), default=0)
        for disc in {t.media_number for t in tracks}
    )

    return ScannedAlbum(
        directory=album_dir,
        tracks=sorted(tracks, key=lambda t: (t.media_number, t.track_number, t.path.name)),
        title=title or folder_title,
        year=year,
        artist_candidates=ordered,
        total_tracks=total_tracks,
    )


# ---------------------------------------------------------------------------
# The result of a scan
# ---------------------------------------------------------------------------
@dataclass(slots=True)
class ScanResult:
    """Everything one library scan observed and changed."""

    root: str = ""
    started_at: datetime = field(default_factory=_now)
    duration_seconds: float = 0.0
    applied: bool = True
    """False for a dry run: the report was built but nothing was written."""

    directories: int = 0
    audio_files: int = 0
    albums_found: int = 0

    albums_matched: int = 0
    """Scanned albums that were tied to a database album."""
    albums_adopted: int = 0
    """Matched albums whose status moved to ``downloaded`` during this scan."""
    albums_already: int = 0
    """Matched albums that were already recorded as downloaded."""
    albums_partial: int = 0
    """Matched albums with fewer files than Qobuz says the release has."""
    albums_busy: int = 0
    """Matched albums left alone because the worker is on them right now."""
    tracks_linked: int = 0
    queue_items_cancelled: int = 0

    partial: list[dict[str, Any]] = field(default_factory=list)
    unmatched: list[dict[str, Any]] = field(default_factory=list)
    """Albums whose artist is followed but whose release is not in the database."""
    unknown_artists: list[dict[str, Any]] = field(default_factory=list)
    """Artists on disk with no counterpart in the database, one row each.

    Rolled up per artist rather than per folder: thirteen albums by somebody
    you do not follow is one decision to make, not thirteen. Each row carries
    ``albums``/``files`` totals and a sample release title.
    """
    truncated: dict[str, int] = field(default_factory=dict)
    """How many detail rows were dropped from each capped list."""
    errors: list[str] = field(default_factory=list)

    @property
    def unmatched_count(self) -> int:
        """Total unmatched albums, including any dropped from the detail list."""
        return len(self.unmatched) + self.truncated.get("unmatched", 0)

    @property
    def unknown_artist_count(self) -> int:
        """Total unknown artists, including any dropped from the detail list."""
        return len(self.unknown_artists) + self.truncated.get("unknown_artists", 0)

    @property
    def partial_count(self) -> int:
        """Alias for :attr:`albums_partial`, which is always exact."""
        return self.albums_partial

    @property
    def ok(self) -> bool:
        """True when the scan completed without recording an error."""
        return not self.errors

    def summary(self) -> str:
        """One-line human summary for the activity feed, the CLI and logs."""
        parts = [
            f"{self.audio_files} file(s) in {self.albums_found} folder(s)",
            f"{self.albums_matched} matched",
            f"{self.albums_adopted} newly marked downloaded",
        ]
        if self.albums_partial:
            parts.append(f"{self.albums_partial} incomplete")
        if self.unmatched_count:
            parts.append(f"{self.unmatched_count} unmatched")
        if self.unknown_artist_count:
            parts.append(f"{self.unknown_artist_count} unknown artist(s)")
        if self.errors:
            parts.append(f"{len(self.errors)} error(s)")
        if not self.applied:
            parts.append("dry run — nothing written")
        return ", ".join(parts)

    def as_dict(self) -> dict[str, Any]:
        """JSON-safe dict, used for the API and for the stored ``Setting`` row."""
        return {
            "root": self.root,
            "started_at": self.started_at.isoformat(),
            "duration_seconds": round(self.duration_seconds, 3),
            "applied": self.applied,
            "directories": self.directories,
            "audio_files": self.audio_files,
            "albums_found": self.albums_found,
            "albums_matched": self.albums_matched,
            "albums_adopted": self.albums_adopted,
            "albums_already": self.albums_already,
            "albums_partial": self.albums_partial,
            "albums_busy": self.albums_busy,
            "tracks_linked": self.tracks_linked,
            "queue_items_cancelled": self.queue_items_cancelled,
            "partial": list(self.partial),
            "unmatched": list(self.unmatched),
            "unknown_artists": list(self.unknown_artists),
            "truncated": dict(self.truncated),
            "errors": list(self.errors),
            "summary": self.summary(),
        }

    def record(self, bucket: str, entry: dict[str, Any]) -> None:
        """Append *entry* to a capped detail list, counting what is dropped."""
        target: list[dict[str, Any]] = getattr(self, bucket)
        if len(target) >= REPORT_LIMITS.get(bucket, MAX_REPORTED):
            self.truncated[bucket] = self.truncated.get(bucket, 0) + 1
            return
        target.append(entry)


# ---------------------------------------------------------------------------
# Persistence of the last result
# ---------------------------------------------------------------------------
async def store_last_scan(session: AsyncSession, result: ScanResult) -> None:
    """Remember *result* in the ``settings`` table so the UI survives a restart."""
    payload = json.dumps(result.as_dict(), default=str)
    row = await session.get(Setting, LAST_SCAN_KEY)
    if row is None:
        session.add(Setting(key=LAST_SCAN_KEY, value=payload))
    else:
        row.value = payload
        row.updated_at = utcnow()


async def load_last_scan(session: AsyncSession) -> dict[str, Any] | None:
    """The stored summary of the most recent scan, or ``None``.

    A row written by an older version that no longer parses is treated as
    absent rather than raising into a page render.
    """
    row = await session.get(Setting, LAST_SCAN_KEY)
    if row is None or not row.value:
        return None
    try:
        data = json.loads(row.value)
    except (TypeError, ValueError):
        logger.warning("Stored library scan summary is not valid JSON; ignoring it")
        return None
    return data if isinstance(data, dict) else None


# ---------------------------------------------------------------------------
# The scanner
# ---------------------------------------------------------------------------
class LibraryScanner:
    """Matches the files under the library root against the database.

    One instance is held on :class:`~app.core.state.AppState`; it holds no
    per-scan state, so concurrent scans are safe in principle — but
    :meth:`scan` takes an internal lock anyway, because two scans racing on the
    same albums would double-count adoptions.
    """

    def __init__(self, settings: Settings | None = None) -> None:
        self._settings = settings or get_settings()
        self._lock = asyncio.Lock()
        self._running = False

    @property
    def settings(self) -> Settings:
        """The effective settings this scanner was built with."""
        return self._settings

    @property
    def running(self) -> bool:
        """True while a scan is in progress."""
        return self._running

    # ------------------------------------------------------------------ scan
    async def scan(
        self,
        session: AsyncSession,
        *,
        root: Path | str | None = None,
        artist_id: str | None = None,
        apply: bool = True,
        record: bool = True,
    ) -> ScanResult:
        """Walk the library, match what is there, and adopt what we already own.

        Args:
            session: Database session. Committed once at the end (never
                mid-scan) so a failure leaves nothing half-applied.
            root: Directory to walk. Defaults to ``Settings.library_path``; a
                per-artist scan narrows it to that artist's folder when one
                exists.
            artist_id: Restrict *changes* to a single artist. Folders belonging
                to anyone else are ignored rather than reported.
            apply: When false the scan is a dry run — the report is built in
                full but no album, track or queue row is touched.
            record: Store the summary in the ``settings`` table and write an
                activity row. Dry runs never record.

        Returns:
            A :class:`ScanResult`. Failures inside the walk land in
            ``result.errors`` rather than raising, so a single unreadable file
            or an unmounted sub-directory cannot abort the whole scan.
        """
        async with self._lock:
            self._running = True
            try:
                return await self._scan(
                    session, root=root, artist_id=artist_id, apply=apply, record=record
                )
            finally:
                self._running = False

    async def _scan(
        self,
        session: AsyncSession,
        *,
        root: Path | str | None,
        artist_id: str | None,
        apply: bool,
        record: bool,
    ) -> ScanResult:
        started = time.monotonic()
        settings = self._settings

        target_artist: Artist | None = None
        if artist_id is not None:
            target_artist = await session.get(Artist, str(artist_id))
            if target_artist is None:
                raise LookupError(f"Artist {artist_id!r} is not in the library")

        scan_root = Path(root) if root is not None else self._root_for(target_artist)
        result = ScanResult(root=str(scan_root), applied=apply)

        albums, stats = await asyncio.to_thread(
            collect_albums,
            scan_root,
            follow_symlinks=settings.library_scan_follow_symlinks,
            on_error=result.errors.append,
        )
        result.directories = stats["directories"]
        result.audio_files = stats["audio_files"]
        result.albums_found = len(albums)
        # Errors are appended from a worker thread; cap them the same way the
        # detail lists are capped so one broken mount cannot produce a 50k-line
        # activity row.
        if len(result.errors) > MAX_REPORTED:
            dropped = len(result.errors) - MAX_REPORTED
            del result.errors[MAX_REPORTED:]
            result.errors.append(f"... and {dropped} more error(s); see the log")

        artist_index = await self._artist_index(session)
        album_cache: dict[str, dict[str, list[Album]]] = {}
        # Unknown artists are rolled up per *artist*, not per folder: a library
        # holding thirteen albums by someone you do not follow is one decision,
        # not thirteen, and listing it thirteen times buries everything else.
        unknown: dict[str, dict[str, Any]] = {}

        for scanned in albums:
            try:
                await self._match_one(
                    session,
                    scanned,
                    artist_index=artist_index,
                    album_cache=album_cache,
                    result=result,
                    unknown=unknown,
                    apply=apply,
                    only_artist_id=str(target_artist.id) if target_artist else None,
                )
            except Exception as exc:  # noqa: BLE001 - one bad folder must not stop the scan
                logger.exception("Scanning %s failed: %s", scanned.directory, exc)
                result.errors.append(f"{scanned.directory}: {exc}")

        for entry in sorted(unknown.values(), key=lambda row: (-row["files"], row["name"])):
            result.record("unknown_artists", entry)

        result.duration_seconds = time.monotonic() - started

        if apply and record:
            await store_last_scan(session, result)
            session.add(
                Activity(
                    level=ActivityLevel.WARNING if result.errors else ActivityLevel.INFO,
                    event="library.scan",
                    message=f"Disk scan: {result.summary()}",
                    artist_id=str(target_artist.id) if target_artist else None,
                )
            )

        if apply:
            await session.commit()
        else:
            await session.rollback()

        logger.info("Library scan finished in %.1fs: %s", result.duration_seconds, result.summary())
        return result

    # -------------------------------------------------------------- matching
    def _root_for(self, artist: Artist | None) -> Path:
        """Narrow the walk to one artist's folder when it can be found.

        The folder is the one Fonoteca's naming template would produce. When it
        does not exist — a library laid out by something else, or an artist
        whose name was sanitised differently — the whole library is walked and
        the caller's ``artist_id`` filter does the narrowing instead.
        """
        library = Path(self._settings.library_path)
        if artist is None:
            return library

        from app.core import naming  # noqa: PLC0415 - avoids an import cycle at module load

        candidate = library / naming.sanitise_component(artist.name, settings=self._settings)
        return candidate if candidate.is_dir() else library

    async def _artist_index(self, session: AsyncSession) -> dict[str, str]:
        """Map :func:`artist_key` to artist id for every followed artist.

        Only ``id``/``name`` are selected: ``Artist.albums`` is a ``selectin``
        relationship, so loading whole rows here would drag the entire album
        table into memory before a single folder had been matched.

        Two artists that normalise to the same key (they exist — Qobuz has
        duplicate entries) resolve to the first by name, and the collision is
        logged rather than silently picking at random.
        """
        rows = await session.execute(select(Artist.id, Artist.name).order_by(Artist.name))
        index: dict[str, str] = {}
        for artist_id, name in rows.all():
            key = artist_key(name)
            if not key:
                continue
            if key in index:
                logger.debug("Artist name %r collides with an earlier entry on key %r", name, key)
                continue
            index[key] = str(artist_id)
        return index

    async def _albums_for(
        self, session: AsyncSession, artist_id: str, cache: dict[str, dict[str, list[Album]]]
    ) -> dict[str, list[Album]]:
        """All of one artist's albums, indexed by :func:`dedupe_key` of the title."""
        cached = cache.get(artist_id)
        if cached is not None:
            return cached

        rows = await session.execute(select(Album).where(Album.artist_id == str(artist_id)))
        index: dict[str, list[Album]] = {}
        for album in rows.scalars().unique().all():
            index.setdefault(dedupe_key(album.title), []).append(album)
            # The version suffix is part of the folder name a user is likely to
            # have on disk, so index the display title too.
            display_key = dedupe_key(album.display_title)
            if display_key != dedupe_key(album.title):
                index.setdefault(display_key, []).append(album)
        cache[artist_id] = index
        return index

    async def _match_one(
        self,
        session: AsyncSession,
        scanned: ScannedAlbum,
        *,
        artist_index: dict[str, str],
        album_cache: dict[str, dict[str, list[Album]]],
        result: ScanResult,
        unknown: dict[str, dict[str, Any]],
        apply: bool,
        only_artist_id: str | None,
    ) -> None:
        """Resolve one scanned directory to an album and, if asked, adopt it."""
        known_artist_id: str | None = None
        known_artist_name = ""
        match: Album | None = None

        # Try every artist candidate; the first that also yields an album wins.
        # Falling back to the first *known* candidate keeps the "unmatched"
        # report attached to a real artist rather than to a folder name.
        for candidate in scanned.artist_candidates:
            candidate_id = artist_index.get(artist_key(candidate))
            if candidate_id is None:
                continue
            if known_artist_id is None:
                known_artist_id, known_artist_name = candidate_id, candidate
            albums = await self._albums_for(session, candidate_id, album_cache)
            found = self._pick_album(albums, scanned)
            if found is not None:
                known_artist_id, known_artist_name = candidate_id, candidate
                match = found
                break

        if only_artist_id is not None and known_artist_id != only_artist_id:
            return

        if known_artist_id is None:
            name = scanned.artist_name or scanned.directory.parent.name
            entry = unknown.setdefault(
                artist_key(name) or name,
                {
                    "name": name,
                    "title": scanned.title,
                    "year": scanned.year,
                    "path": str(scanned.directory.parent),
                    "albums": 0,
                    "files": 0,
                    "bytes": 0,
                    "discs": 1,
                },
            )
            entry["albums"] += 1
            entry["files"] += scanned.file_count
            entry["bytes"] += scanned.bytes_on_disk
            return

        if match is None:
            entry = scanned.as_dict()
            entry["artist"] = known_artist_name
            entry["artist_id"] = known_artist_id
            result.record("unmatched", entry)
            return

        result.albums_matched += 1
        await self._adopt(session, match, scanned, result=result, apply=apply)

    def _pick_album(
        self, albums: dict[str, list[Album]], scanned: ScannedAlbum
    ) -> Album | None:
        """Choose the database album that best fits a scanned directory.

        Both the tagged album title and the (year-stripped) folder name are
        tried, because one of the two is wrong surprisingly often. When several
        editions share a normalised title, the one whose year and track count
        are closest to what is on disk wins — so a scanned 12-track 2018 folder
        prefers the 12-track 2018 row over a 30-track anniversary reissue.
        """
        folder_title, _ = album_directory_title(scanned.directory.name)
        candidates: list[Album] = []
        for title in (scanned.title, folder_title):
            for album in albums.get(dedupe_key(title), ()):
                if album not in candidates:
                    candidates.append(album)
        if not candidates:
            return None
        if len(candidates) == 1:
            return candidates[0]

        files = scanned.file_count

        def rank(album: Album) -> tuple[int, int, int, str]:
            year_gap = (
                abs((album.year or 0) - scanned.year)
                if scanned.year and album.year
                else 99
            )
            track_gap = abs((album.tracks_count or 0) - files)
            # An already-downloaded row is the one the user most likely means.
            owned = 0 if album.status is AlbumStatus.DOWNLOADED else 1
            return (year_gap, track_gap, owned, str(album.id))

        return sorted(candidates, key=rank)[0]

    # -------------------------------------------------------------- adoption
    async def _adopt(
        self,
        session: AsyncSession,
        album: Album,
        scanned: ScannedAlbum,
        *,
        result: ScanResult,
        apply: bool,
    ) -> None:
        """Record that *album* is present on disk.

        Status only ever moves towards ``downloaded``. Nothing here can mark an
        album ``wanted`` or queue anything — the scan exists to *stop* work, not
        to create it.
        """
        if album.status is AlbumStatus.DOWNLOADING:
            result.albums_busy += 1
            return

        expected = album.tracks_count or scanned.total_tracks or scanned.file_count
        found = scanned.file_count
        ratio = (found / expected) if expected else 0.0
        complete = expected > 0 and ratio >= self._settings.library_scan_complete_ratio

        if not complete:
            result.albums_partial += 1
            entry = scanned.as_dict()
            entry.update(
                {
                    "album_id": album.id,
                    "artist_id": album.artist_id,
                    "title": album.display_title,
                    "expected": expected,
                    "status": album.status.value,
                }
            )
            result.record("partial", entry)

        if not apply:
            if complete and album.status is not AlbumStatus.DOWNLOADED:
                result.albums_adopted += 1
            elif complete:
                result.albums_already += 1
            return

        if album.path != str(scanned.directory):
            album.path = str(scanned.directory)

        result.tracks_linked += self._link_tracks(album, scanned)

        if not complete:
            return

        if album.status is AlbumStatus.DOWNLOADED:
            result.albums_already += 1
        else:
            album.status = AlbumStatus.DOWNLOADED
            result.albums_adopted += 1
            logger.info(
                "Adopted %s by %s from disk (%d file(s) in %s)",
                album.display_title,
                getattr(album.artist, "name", album.artist_id),
                found,
                scanned.directory,
            )

        album.downloaded_at = album.downloaded_at or utcnow()
        result.queue_items_cancelled += await self._cancel_pending(session, album)

    def _link_tracks(self, album: Album, scanned: ScannedAlbum) -> int:
        """Point existing :class:`~app.models.Track` rows at the files on disk.

        Only rows that already exist are touched. Track rows carry the Qobuz
        track id as their primary key, so a file the database has never heard of
        cannot be turned into one — the album-level match above is what covers
        a release that was never downloaded through Fonoteca.

        Matching is by ``(disc, track)`` first and by normalised title second,
        which rescues an album whose files were numbered by a different ripper.
        """
        if not album.tracks:
            return 0

        by_position = {
            (track.media_number or 1, track.track_number or 0): track for track in album.tracks
        }
        by_title = {dedupe_key(track.title): track for track in album.tracks}

        linked = 0
        claimed: set[str] = set()
        for scanned_track in scanned.tracks:
            track = by_position.get((scanned_track.media_number, scanned_track.track_number))
            if track is None:
                track = by_title.get(dedupe_key(scanned_track.title))
            if track is None or track.id in claimed:
                continue
            claimed.add(track.id)

            track.path = str(scanned_track.path)
            track.status = TrackStatus.DOWNLOADED
            track.file_size = scanned_track.file_size or track.file_size
            # Never overwrite what Qobuz told us the delivered file was; only
            # fill the gaps for a file Fonoteca did not download itself.
            if track.bit_depth is None:
                track.bit_depth = scanned_track.bit_depth
            if track.sampling_rate is None:
                track.sampling_rate = scanned_track.sampling_rate
            if track.duration is None:
                track.duration = scanned_track.duration
            track.downloaded_at = track.downloaded_at or utcnow()
            linked += 1
        return linked

    async def _cancel_pending(self, session: AsyncSession, album: Album) -> int:
        """Cancel queued-but-not-started downloads for an album we already have.

        An ``active`` item is left alone: the worker owns it, and racing it here
        would corrupt the queue-item ownership rule in ``CLAUDE.md``.
        """
        rows = await session.execute(
            select(QueueItem).where(
                QueueItem.album_id == str(album.id),
                QueueItem.state == QueueState.PENDING,
            )
        )
        cancelled = 0
        for item in rows.scalars().all():
            item.state = QueueState.CANCELLED
            item.finished_at = utcnow()
            item.last_error = "Already present in the library (found by the disk scan)"
            cancelled += 1
        return cancelled
