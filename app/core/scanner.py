"""Library scanning: reconcile what is already on disk with what Qobuzarr knows.

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
reduce the amount of downloading Qobuzarr would do, which is what makes it safe
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
  rather than two albums.  ``{disc_prefix}`` folders written by Qobuzarr's own
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

What the tags claim
-------------------
The files were tagged by *something*, and in a real library that something is
usually Picard: 126 of the 137 Joanne Shaw Taylor files in this user's library
carry a ``MUSICBRAINZ_ALBUMARTISTID``, and every one of the 796 scanned tracks in
the database showed no ISRC — not because the files lacked one, but because
nothing read it.  Identifiers that are already sitting on disk are the cheapest
input the matcher will ever get: a tag parse instead of an HTTP request.

They are read into :class:`ScannedTrack` and persisted to
:class:`~app.models.FileClaim`, which is a table of *hypotheses* — never
:class:`~app.models.TrackMetadata`, which is a table of conclusions.  The
difference is the whole point of reading them here: a claim may **narrow** a
search and it may **reject** a candidate, and it may never select one.  A
hand-typed barcode, an MBID copied from the wrong release and an ISRC belonging
to a different mix all look exactly like the real thing, and once a claim has
been written where verified facts live nothing downstream can tell that the whole
chain rests on a string a stranger typed.

Integrity stamps
----------------
Each file is also *measured*: its size and mtime (the tripwire), its sample count
(what a tag edit cannot change) and — sometimes — a hash of its whole contents.
Sometimes, because :func:`app.core.integrity.hash_file` costs about **201 ms per
file** on this user's library: hashing everything on every pass turns the nightly
scan into an hour and a half of disk I/O on 30 000 files, for an answer that is
"nothing changed" 99.9% of the time.  So the scanner opens a file only when the
cheap pair disagrees with what was recorded, or when nothing was ever recorded.
Anything that makes the hash unconditional — reading it in the walk, computing it
"just in case", dropping the ``content_hash`` argument to :func:`read_track` — is
the difference between a scan that runs nightly and one nobody dares start.
"""

from __future__ import annotations

import asyncio
import hashlib
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
from mutagen.easyid3 import EasyID3
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, get_settings
from app.core.indexer import dedupe_key
from app.core.integrity import (
    FileStamp,
    IntegrityState,
    album_content_digest,
    audio_sample_count,
    classify,
    hash_file,
    stat_stamp,
)
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    Artist,
    FileClaim,
    FingerprintState,
    BINDING_NOT_IN_CATALOGUE,
    FolderBinding,
    QID_ARTIST_PREFIX,
    QID_RELEASE_PREFIX,
    QID_TRACK_PREFIX,
    QueueItem,
    QueueState,
    Setting,
    Track,
    TrackOrigin,
    TrackStatus,
    mint_qid,
    utcnow,
)

__all__ = [
    "AUDIO_EXTENSIONS",
    "IGNORED_DIRECTORIES",
    "LAST_SCAN_KEY",
    "MAX_CLAIM_LENGTH",
    "MAX_REPORTED",
    "QID_TAG",
    "REPORT_LIMITS",
    "DiskCapacity",
    "LibraryScanner",
    "ScanResult",
    "ScannedAlbum",
    "ScannedTrack",
    "album_directory_title",
    "artist_key",
    "collect_albums",
    "disk_capacity",
    "load_last_scan",
    "read_track",
    "scanned_track_id",
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

#: A trailing ``[FLAC 24-96]``-style quality tag, which Qobuzarr's own naming
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

#: The tag Qobuzarr writes its own track ``qid`` into, in both spellings: a
#: Vorbis comment of this name, and an ID3 ``TXXX`` frame with this description.
QID_TAG = "QOBUZARR_QID"

#: Longest string that may be believed to be an identifier. Every
#: :class:`~app.models.FileClaim` column is ``String(64)`` — deliberately looser
#: than the verified tables, because it stores whatever was in the tag — so a
#: value that will not fit is not a hand-typed barcode with a digit wrong, it is
#: something that was never an identifier at all.
MAX_CLAIM_LENGTH = 64

#: A ``qid`` lives in ``String(32)``. Same rule, narrower column.
_MAX_QID_LENGTH = 32

# ``mutagen``'s easy interface is what :func:`read_track` parses MP3s through, and
# it exposes only the keys it has been taught. Every identifier below except the
# qid is already registered by ``EasyID3`` (``isrc`` → ``TSRC``, ``barcode`` →
# ``TXXX:BARCODE``, ``musicbrainz_trackid`` → the ``UFID`` frame with owner
# ``http://musicbrainz.org``); the qid is ours, so it has to be registered or the
# tag Qobuzarr wrote on the last pass would be invisible on exactly the files it
# was written to. Registration is idempotent and namespaced by the frame
# description, so doing it at import costs nothing and cannot collide.
EasyID3.RegisterTXXXKey("qobuzarr_qid", QID_TAG)


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
    and Qobuzarr's own ``"All Melody (2018) [FLAC 24-96]"``. The quality tag is
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

    # -- what the tags claim ------------------------------------------------
    # Read off the file, believed by nothing. These land in ``file_claims``, not
    # in ``track_metadata``; see the module docstring for why the two tables are
    # separate and must stay that way.
    isrc: str = ""
    barcode: str = ""
    mb_recording_mbid: str = ""
    mb_release_mbid: str = ""
    mb_release_group_mbid: str = ""
    mb_artist_mbid: str = ""
    mb_album_artist_mbid: str = ""
    qobuzarr_qid: str = ""

    # -- what the file measures ---------------------------------------------
    file_mtime: float = 0.0
    """POSIX mtime, the other half of the tripwire. 0.0 when ``stat`` failed."""
    sample_count: int | None = None
    """Decoded sample frames, or ``None`` when the container did not say.

    ``None`` is a refusal to guess and is never read as agreement — see
    :func:`app.core.integrity.classify`.
    """
    content_hash: str | None = None
    """blake2b-128 of the whole file, and **only** when it was asked for.

    ``None`` is the normal case: the walk does not hash, because it cannot know
    which files are worth opening — that answer lives in the database, and
    :func:`collect_albums` never touches it.
    """

    unreadable: bool = False
    """The file is there and nothing can parse it — usually because it is empty.

    Set by :func:`read_track` **only** when the file was stat-able before and
    after the failed parse and had not changed size in between, so it is a
    statement about the file rather than about the mount. Every other field is
    then meaningless and none of them is filled in: the row exists so a
    corruption verdict has somewhere to live, not to describe audio.

    A track carrying this is deliberately still returned rather than dropped.
    Dropping it is what made 30 zero-byte files in one real library invisible to
    the entire application — no track row, so nothing to fingerprint, nothing to
    baseline, no place for a verdict, and no count they appeared in. Their only
    trace was a line in the transient error list of whichever scan last ran.
    """

    @property
    def extension(self) -> str:
        """Lowercase file extension including the dot."""
        return self.path.suffix.lower()

    def claims(self) -> dict[str, str | None]:
        """The identifiers this file claims, shaped for :class:`~app.models.FileClaim`.

        Keys are the column names, and an absent claim is ``None`` rather than
        ``""`` — the columns are nullable and "the tag was not there" is a
        different statement from "the tag was empty".
        """
        return {
            "isrc": self.isrc or None,
            "barcode": self.barcode or None,
            "mb_recording_mbid": self.mb_recording_mbid or None,
            "mb_release_mbid": self.mb_release_mbid or None,
            "mb_release_group_mbid": self.mb_release_group_mbid or None,
            "mb_artist_mbid": self.mb_artist_mbid or None,
            "mb_album_artist_mbid": self.mb_album_artist_mbid or None,
            "qobuzarr_qid": self.qobuzarr_qid or None,
        }


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


def _identifier_text(value: Any, limit: int) -> str:
    """One tag value as an identifier, or ``""`` if it cannot be one.

    Not simply ``str(value)``, and the exception is the reason this exists: an
    ID3 ``UFID`` frame carries its id as raw **bytes** on ``.data``, and mutagen's
    ``Frame`` has no ``__str__``, so stringifying one yields
    ``UFID(owner='http://musicbrainz.org', data=b'...')`` — a claim that looks
    plausible enough to store and is not an MBID at all. Decoding ``.data`` is
    what makes the raw-ID3 fallback path (a file the easy interface refused) read
    the same value as the easy path.

    Length and whitespace are the only validation, and both are structural rather
    than clever: no ISRC, barcode, MBID or qid contains a space, and a value too
    long for a ``String(64)`` column was never one of those. Anything beyond that
    — checking an MBID is a UUID, a barcode is digits — belongs to whatever
    consumes the claim, because the point of this table is to record what the file
    said, not a tidied version of it.
    """
    data: Any = getattr(value, "data", None)
    if data is None and isinstance(value, (bytes, bytearray)):
        data = value
    if data is not None:
        try:
            text = bytes(data).decode("utf-8")
        except (UnicodeDecodeError, TypeError, ValueError):
            return ""
    else:
        text = str(value)

    text = text.strip()
    if not text or len(text) > limit or any(char.isspace() for char in text):
        return ""
    return text


def _identifier(tags: Any, *names: str, limit: int = MAX_CLAIM_LENGTH) -> str:
    """First value under any of *names* that can be an identifier.

    Same shape as :func:`_tag` — several spellings tried in order, because one
    library is Vorbis comments and the next is ID3 — but the value is put through
    :func:`_identifier_text` instead of ``str()``.
    """
    for name in names:
        try:
            raw = tags.get(name)
        except Exception:  # noqa: BLE001 - some mutagen tag objects are picky
            continue
        if raw is None:
            continue
        for value in raw if isinstance(raw, (list, tuple)) else [raw]:
            text = _identifier_text(value, limit)
            if text:
                return text
    return ""


def _read_claims(tags: Any) -> dict[str, str]:
    """Every identifier the tags claim, in both Vorbis and ID3 spellings.

    The spellings are **Picard's**, exactly. A tag spelled differently is a tag no
    other tool will read, and this is the reading half of the same rule the tagger
    keeps on the writing side. Both vocabularies are tried for every field rather
    than branching on container type, because ``mutagen``'s easy interface hands
    back Vorbis-style keys for FLAC/MP4 and its own registered names for MP3, and
    the raw fallback hands back frame ids — three shapes, one lookup order.

    Two deliberate omissions:

    ``MUSICBRAINZ_RELEASETRACKID`` is read and then **dropped**. It identifies a
    track's slot on one particular release, which is not what a recording id is,
    and there is no column for it. Folding it into ``mb_recording_mbid`` would put
    a release-specific id where every consumer expects a recording — the third
    principle of the integrity model, never use a value as something it is not.

    MP4/M4A freeform atoms (``----:com.apple.iTunes:MusicBrainz Album Id``) are
    not listed. Their values are ``MP4FreeForm`` bytes whose text encoding is
    per-atom, and a wrong guess there produces a plausible-looking wrong id rather
    than a failure — the one outcome this whole design exists to avoid.
    """
    return {
        "isrc": _identifier(tags, "isrc", "ISRC", "TSRC"),
        "barcode": _identifier(tags, "barcode", "BARCODE", "upc", "UPC", "TXXX:BARCODE"),
        # MUSICBRAINZ_TRACKID is the *recording* mbid — the name is a historical
        # accident in Picard, not a description, and it is the one everybody wrote.
        "mb_recording_mbid": _identifier(
            tags,
            "musicbrainz_trackid",
            "MUSICBRAINZ_TRACKID",
            "UFID:http://musicbrainz.org",
        ),
        "mb_release_mbid": _identifier(
            tags,
            "musicbrainz_albumid",
            "MUSICBRAINZ_ALBUMID",
            "TXXX:MusicBrainz Album Id",
        ),
        "mb_release_group_mbid": _identifier(
            tags,
            "musicbrainz_releasegroupid",
            "MUSICBRAINZ_RELEASEGROUPID",
            "TXXX:MusicBrainz Release Group Id",
        ),
        "mb_artist_mbid": _identifier(
            tags,
            "musicbrainz_artistid",
            "MUSICBRAINZ_ARTISTID",
            "TXXX:MusicBrainz Artist Id",
        ),
        "mb_album_artist_mbid": _identifier(
            tags,
            "musicbrainz_albumartistid",
            "MUSICBRAINZ_ALBUMARTISTID",
            "TXXX:MusicBrainz Album Artist Id",
        ),
        "qobuzarr_qid": _identifier(
            tags, "qobuzarr_qid", QID_TAG, f"TXXX:{QID_TAG}", limit=_MAX_QID_LENGTH
        ),
    }


def read_track(path: Path, *, content_hash: bool = False) -> ScannedTrack | None:
    """Read one audio file into a :class:`ScannedTrack`.

    **Synchronous** — it blocks on file I/O, so call it through
    :func:`asyncio.to_thread` (which is what :meth:`LibraryScanner.scan` does).

    ``mutagen``'s *easy* interface is tried first because it gives FLAC, MP3 and
    MP4 the same tag names; a file it cannot parse that way falls back to the
    raw interface, and a file it cannot parse at all returns ``None`` rather
    than raising, so one corrupt track never aborts a scan.

    Args:
        path: The audio file to read.
        content_hash: Hash the whole file as well. **Off by default, and the
            default is the important half.** Hashing costs about 201 ms per file
            against roughly a millisecond for everything else here, so a walk that
            did it unconditionally would take an hour and a half over 30 000
            files to answer a question that is almost always "nothing changed".
            :class:`LibraryScanner` leaves it off and hashes only the files whose
            size or mtime disagrees with the recorded baseline; the argument is
            here for a caller that genuinely wants one file measured in full.

    Everything except the hash is cheap because the file is already open: the tag
    identifiers, the sample count and the stream properties all come out of the
    same parse. The sample count goes through
    :func:`app.core.integrity.audio_sample_count` rather than being re-derived
    from ``info`` here, at the cost of one more container parse. That is bought on
    purpose: two implementations of "how many samples" would eventually disagree
    by one, and a sample count that disagrees with the one
    :func:`app.core.integrity.read_stamp` measures makes every file in the library
    read as ``REPLACED`` — a false alarm on everything, which is the same as no
    alarm at all.
    """
    # Stat first, and treat a stat that fails as a fact about the *mount* rather
    # than about the file. This is the same four-way split
    # :mod:`app.enrich.chromaprint` makes and it is here for the same reason: a
    # share that blipped, a folder another tagger renamed a second ago, a
    # permission that is briefly wrong — none of those is evidence that the audio
    # is bad, and recording one as corruption puts a healthy release on the
    # Integrity screen (and, before the quarantine became a press, in the trash).
    try:
        stat = path.stat()
    except OSError:
        return None

    audio: Any = None
    for easy in (True, False):
        try:
            audio = mutagen.File(str(path), easy=easy)
        except Exception:  # noqa: BLE001 - unreadable/corrupt files are expected
            audio = None
        if audio is not None:
            break
    if audio is None:
        # The parse failed. Ask the disk once more whether the file is still
        # there and still the size it was: if it is not, the failure was the
        # mount moving under us and this says nothing about the audio.
        try:
            again = path.stat()
        except OSError:
            return None
        if again.st_size != stat.st_size:
            return None
        # It is present, stable, and nothing can make sense of it. That is a
        # statement about the file, and it earns a row so the verdict has
        # somewhere to live — an empty file that produced no row at all was
        # invisible to every count in the application.
        return ScannedTrack(
            path=path,
            unreadable=True,
            file_size=stat.st_size,
            file_mtime=stat.st_mtime,
        )

    tags = getattr(audio, "tags", None) or {}
    info = getattr(audio, "info", None)

    stat = stat_stamp(path)
    file_size, file_mtime = stat if stat is not None else (0, 0.0)

    sample_rate = getattr(info, "sample_rate", None)
    duration = getattr(info, "length", None)

    digest: str | None = None
    if content_hash:
        try:
            digest = hash_file(path)
        except OSError as exc:
            logger.debug("scan.hash_failed path=%s error=%s", path, exc)

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
        file_mtime=file_mtime,
        sample_count=audio_sample_count(path),
        content_hash=digest,
        **_read_claims(tags),
    )


@dataclass(frozen=True, slots=True)
class DiskCapacity:
    """What the volume holding a path has room for. One ``statvfs``, no walk.

    ``used_bytes + free_bytes`` is normally **less** than ``total_bytes``, and
    that is correct rather than a rounding bug: most filesystems reserve a
    percentage of blocks for root, which is neither in use nor available to
    anyone else. ``df`` omits the same blocks from *Avail*. Do not "fix" this
    into an identity by deriving one field from the other two — the reserve is
    real space and pretending otherwise misreports either fullness or headroom.
    """

    path: str
    total_bytes: int
    used_bytes: int
    free_bytes: int


def disk_capacity(path: Path | str) -> DiskCapacity | None:
    """Measure the filesystem *path* sits on, or ``None`` if it cannot be.

    SYNCHRONOUS and blocking — call via :func:`asyncio.to_thread`. ``statvfs``
    is a single syscall and walks nothing, but on a stale network mount it can
    block for the length of that mount's timeout, which is a thread to lose,
    not the event loop.

    ``None`` covers every "we do not know": the path does not exist, it is not
    on a mounted filesystem, the syscall failed, or the kernel reported a zero
    block size. It is never a zeroed record — a 0-byte disk is a *claim*, and a
    fullness bar is exactly the figure nobody may invent.

    This is the **disk's** fullness, not the library's size. The library's own
    byte total is a database sum over ``tracks.file_size``; they are different
    facts and only one of them says whether another discography will fit.
    """
    try:
        stat = os.statvfs(os.fspath(path))
    except (OSError, ValueError):
        return None
    frsize = stat.f_frsize or stat.f_bsize
    if not frsize or stat.f_blocks <= 0:
        return None
    return DiskCapacity(
        path=str(path),
        total_bytes=int(frsize * stat.f_blocks),
        used_bytes=int(frsize * (stat.f_blocks - stat.f_bfree)),
        free_bytes=int(frsize * stat.f_bavail),
    )


def scanned_track_id(album_id: str, scanned: ScannedTrack) -> str:
    """A stable primary key for a file the Qobuz catalogue has no track id for.

    Keyed on the track's **position within its release**, not on its path. That
    is the choice that makes a rescan converge instead of churn: re-filing an
    album renames every file in it — :func:`app.core.librarian.plan_refile` does
    exactly that — and a path-keyed id would delete and recreate the whole
    album's rows on the next scan, taking every fingerprint verdict and
    MusicBrainz recording id in ``track_metadata`` with it.

    Position is not immutable either, and it is not pretended to be: a retagger
    that renumbers a track gives it a new id and retires the old row. That is the
    honest answer, because the track's identity *within the release* is what
    actually changed. Files with no usable number fall back to the filename,
    which is the only thing left that distinguishes them.

    The ``scan:`` prefix is for humans reading the table. Nothing branches on it
    — :attr:`app.models.Track.origin` is what says where a row came from — and
    the digest keeps the whole id inside ``String(64)`` however long the album id
    and the filename are.
    """
    if scanned.track_number > 0:
        key = f"{scanned.media_number or 1}/{scanned.track_number}"
    else:
        key = f"file/{scanned.path.name}"
    digest = hashlib.sha1(f"{album_id}\x00{key}".encode()).hexdigest()
    return f"scan:{digest[:24]}"


def _ensure_qid(entity: Artist | Album | Track, prefix: str) -> None:
    """Give *entity* a ``qid`` if it has none, and never touch one it already has.

    The "never touch" half is the load-bearing one. A ``qid`` is the only key that
    survives everything else about a row changing — a re-file, a re-tag, a
    re-match to a different release — and it is what every measurement made about
    a file hangs from. Re-minting one on a rescan would silently orphan all of it,
    and the symptom would be a library that quietly forgets its fingerprints every
    night rather than an error anybody could see.

    Minting here at all is for two cases the column default cannot reach: a row
    read back from a database whose ``qid`` column was added by hand (there are no
    migrations — see ``CLAUDE.md``), and a row some other code path constructed
    before the column existed. A row inserted normally already arrives with one.
    """
    if not getattr(entity, "qid", None):
        entity.qid = mint_qid(prefix)


def recorded_stamp(track: Track) -> FileStamp | None:
    """The baseline stored on a track row, as a :class:`FileStamp`.

    ``None`` when there is no ``content_hash``, which
    :func:`app.core.integrity.classify` reads as ``UNKNOWN`` — a row nothing has
    ever baselined makes no claim, so there is nothing for the file to
    contradict.

    A row that has a hash but no size or mtime gets ``0``/``0.0``, which no real
    file on disk matches, so a tripwire built on it fires and the verdict comes
    from the hash. That is the right direction to be wrong in: it costs one read.

    Lives here rather than in :mod:`app.core.integrity` because that module has
    no ORM in it by design, and it is shared with :mod:`app.core.librarian` so
    that the gate and the scan cannot end up reading the same four columns
    differently.
    """
    if not track.content_hash:
        return None
    return FileStamp(
        size=track.file_size or 0,
        mtime=track.file_mtime if track.file_mtime is not None else 0.0,
        content_hash=track.content_hash,
        sample_count=track.sample_count,
    )


def _hash_files(paths: Sequence[Path]) -> dict[Path, str]:
    """Hash each of *paths*, skipping the ones that will not open.

    **Synchronous and expensive** — roughly 201 ms per file — so it runs in a
    worker thread, never on the event loop. A file that raises is left out of the
    result rather than recorded as ``None``: the caller then leaves the existing
    baseline alone, which is the honest outcome, because failing to read a file
    proves nothing about whether its contents changed.
    """
    digests: dict[Path, str] = {}
    for path in paths:
        try:
            digests[path] = hash_file(path)
        except OSError as exc:
            logger.debug("scan.hash_failed path=%s error=%s", path, exc)
    return digests


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
                # The file could not be reached, which says nothing about the
                # audio. Reported, counted, and given no row — see `read_track`.
                stats["unreadable"] += 1
                if on_error is not None:
                    on_error(f"{file_path}: could not be read (skipped)")
                continue
            if track.unreadable:
                # Present and unparseable. It still becomes a track row, so the
                # count reported here is what the scan *found*, not what it
                # discarded.
                stats["unreadable"] += 1
                if on_error is not None:
                    detail = "empty file" if track.file_size == 0 else "cannot be parsed"
                    on_error(f"{file_path}: unreadable audio file ({detail})")
                grouped.setdefault(album_dir, []).append(track)
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
    albums_excluded: int = 0
    """Folders a person marked as holding no catalogue release, so they are not
    counted as unmatched. Reported rather than silent: a folder that vanishes
    from every figure is indistinguishable from one the scan never saw, and
    somebody has to be able to notice a marker they regret."""
    albums_adopted: int = 0
    """Matched albums whose status moved to ``downloaded`` during this scan."""
    albums_already: int = 0
    """Matched albums that were already recorded as downloaded."""
    albums_partial: int = 0
    """Matched albums with fewer files than Qobuz says the release has."""
    albums_busy: int = 0
    """Matched albums left alone because the worker is on them right now."""
    tracks_linked: int = 0
    """Audio files recorded as track rows — both those matched to an existing
    row and those the scan created because nothing in the database claimed them."""
    queue_items_cancelled: int = 0

    files_measured: int = 0
    """Files this scan actually opened and hashed, because their tripwire fired.

    Almost always zero on a library nobody has touched: that is the point of the
    tripwire, and the reason a nightly scan is affordable at all."""
    files_retagged: int = 0
    """Measured files whose bytes moved but whose audio did not."""
    files_replaced: int = 0
    """Measured files holding audio that is not the audio that was recorded.

    Counted here because the scan re-baselines what it measures, so this is the
    only moment the verdict exists — see :meth:`LibraryScanner._report_integrity`."""
    albums_reopened: int = 0
    """Releases put back on the enrichment work list because a file was replaced."""
    files_corrupt: int = 0
    """Files present on disk holding no playable audio — usually zero-byte.

    Recorded as ``fingerprint_state=corrupt`` so they reach the Integrity screen
    and its badge. Nothing is trashed for it: see
    :func:`app.core.scheduler._count_corrupt`."""
    files_recovered: int = 0
    """Files that carried a corruption verdict and parse again, so it was cleared."""

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
        if self.files_retagged:
            parts.append(f"{self.files_retagged} re-tagged since the last scan")
        if self.files_replaced:
            # The one line of this summary that is not routine. A file holding
            # different audio under the same name is the verdict this pass is the
            # only one able to give, because it re-baselines what it measures.
            parts.append(f"{self.files_replaced} file(s) replaced since the last scan")
        if self.files_corrupt:
            # Said out loud rather than left to the error list. These files used
            # to appear only there, which meant they vanished the moment the
            # next scan ran.
            parts.append(f"{self.files_corrupt} unplayable file(s)")
        if self.files_recovered:
            parts.append(f"{self.files_recovered} no longer unplayable")
        if self.unmatched_count:
            parts.append(f"{self.unmatched_count} unmatched")
        if self.albums_excluded:
            parts.append(f"{self.albums_excluded} marked not on Qobuz")
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
            "albums_excluded": self.albums_excluded,
            "albums_adopted": self.albums_adopted,
            "albums_already": self.albums_already,
            "albums_partial": self.albums_partial,
            "albums_busy": self.albums_busy,
            "tracks_linked": self.tracks_linked,
            "queue_items_cancelled": self.queue_items_cancelled,
            "files_measured": self.files_measured,
            "files_retagged": self.files_retagged,
            "files_replaced": self.files_replaced,
            "albums_reopened": self.albums_reopened,
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
        #: ``folder_bindings``, loaded once per scan: directory -> album id. A
        #: local read, so it does not touch the "the scan never calls Qobuz"
        #: rule; deciding an identity is :mod:`app.core.binder`'s job and this
        #: only ever *consults* what that already settled.
        self._bindings: dict[str, str] = {}
        #: Artist id -> name, for reporting a bound folder against a real artist
        #: rather than the string on disk that failed to match in the first place.
        self._artist_names: dict[str, str] = {}
        #: Directories a person has marked as holding no catalogue release.
        self._excluded: set[str] = set()

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
        self._bindings, self._excluded = await self._load_bindings(session)
        album_cache: dict[str, dict[str, list[Album]]] = {}
        # Unknown artists are rolled up per *artist*, not per folder: a library
        # holding thirteen albums by someone you do not follow is one decision,
        # not thirteen, and listing it thirteen times buries everything else.
        unknown: dict[str, dict[str, Any]] = {}

        for scanned in albums:
            # Each folder gets its own SAVEPOINT. "One bad folder must not stop
            # the scan" was the intent here from the start, and catching the
            # exception was not enough to deliver it: the whole walk runs inside
            # ONE transaction, so a failure during a flush leaves the session in
            # a state where every later statement raises PendingRollbackError.
            # The result was that one album with a bad row failed *every*
            # remaining album — 200-odd identical tracebacks in the log — and
            # then the commit below raised too, so POST /api/library/scan
            # answered 500 and a scan that had already adopted hundreds of
            # releases stored none of them. A nested transaction rolls back only
            # what this folder wrote and leaves the session usable.
            try:
                async with session.begin_nested():
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

        The folder is the one Qobuzarr's naming template would produce. When it
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
        names: dict[str, str] = {}
        for artist_id, name in rows.all():
            names[str(artist_id)] = str(name or "")
            key = artist_key(name)
            if not key:
                continue
            if key in index:
                logger.debug("Artist name %r collides with an earlier entry on key %r", name, key)
                continue
            index[key] = str(artist_id)
        self._artist_names = names
        return index

    async def _load_bindings(
        self, session: AsyncSession
    ) -> tuple[dict[str, str], set[str]]:
        """``folder_bindings`` for this scan: what is bound, and what is excluded.

        Read once rather than per folder: the table is one row per settled
        directory, so it is small, and a per-folder query would be a round trip
        for every directory in the library to answer "probably not".
        """
        rows = await session.execute(
            select(FolderBinding.path, FolderBinding.album_id, FolderBinding.state)
        )
        bound: dict[str, str] = {}
        excluded: set[str] = set()
        for path, album_id, state in rows.all():
            if state == BINDING_NOT_IN_CATALOGUE:
                excluded.add(str(path))
            elif album_id:
                bound[str(path)] = str(album_id)
        return bound, excluded

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

        # A folder somebody has said holds no catalogue release is counted apart
        # from the ones still waiting to be identified, and that is the whole
        # value of the marker. ``unmatched`` is the number a person reads to know
        # how much work is left, so leaving a permanently-unmatchable folder in
        # it means the figure never falls however much of the list is worked
        # through — and a count that never falls is a count people stop reading.
        if str(scanned.directory) in self._excluded:
            result.albums_excluded += 1
            return

        # An exact binding wins outright, and is checked before any name is
        # normalised. A row in ``folder_bindings`` says the audio in this
        # directory *is* that release — decided by a barcode, never by a title —
        # so consulting it after the name match would let a worse answer win, and
        # consulting it inside the artist loop would make it depend on the folder
        # artist matching, which is a second name test the binding exists to
        # replace. It is what lets a folder called "Play: The Guitar Album" adopt
        # the catalogue's "Play" instead of sitting in the wanted list forever.
        bound_id = self._bindings.get(str(scanned.directory))
        if bound_id is not None:
            bound = await session.get(Album, bound_id)
            if bound is not None:
                # Still held to ``only_artist_id`` below, and still adopted
                # through the same one-way ``_adopt``: a binding supplies the
                # identity, never a licence to skip a gate.
                match = bound
                known_artist_id = str(bound.artist_id or "")
                known_artist_name = self._artist_names.get(known_artist_id, "")
            else:
                logger.debug(
                    "Binding for %s names album %s, which is not in the database",
                    scanned.directory,
                    bound_id,
                )

        # Try every artist candidate; the first that also yields an album wins.
        # Falling back to the first *known* candidate keeps the "unmatched"
        # report attached to a real artist rather than to a folder name.
        for candidate in () if match is not None else scanned.artist_candidates:
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

        result.tracks_linked += await self._link_tracks(session, album, scanned, result)

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
            await self._mark_enrichable(session, album)

        album.downloaded_at = album.downloaded_at or utcnow()
        result.queue_items_cancelled += await self._cancel_pending(session, album)

    async def _mark_enrichable(self, session: AsyncSession, album: Album) -> None:
        """Queue a newly adopted release for enrichment, at the front.

        An adopted album is in the library by exactly the same standard as a
        downloaded one — it is on disk — and enrichment is scoped to the library,
        so adoption is the moment it becomes eligible. This is the whole of the
        scan's involvement: it queues a row and makes no request, so the rule
        that the scan is local-only, read-only and one-way still holds.

        Swallowed on failure, like everything else the scan does to a side table.
        ``Enricher._seed`` reaches the same album from its status on the next
        tick, so losing this delays enrichment rather than preventing it.
        """
        from app.core.enricher import mark_library_due  # noqa: PLC0415 - avoids a cycle

        try:
            await mark_library_due(session, [str(album.id)])
        except Exception:  # noqa: BLE001 - a scan must not fail on a side table
            logger.exception("Could not queue %s for enrichment", album.id)

    async def _link_tracks(
        self,
        session: AsyncSession,
        album: Album,
        scanned: ScannedAlbum,
        result: ScanResult,
    ) -> int:
        """Record every audio file of *album*, as a :class:`~app.models.Track` row.

        Four steps, and each one exists because of what the step before it costs.

        :meth:`_pair_files` decides which row each file belongs to. A file is
        first offered to the rows that are already there — matched by
        ``(disc, track)``, then by normalised title, which rescues an album whose
        files were numbered by a different ripper — because a row the download
        loop wrote knows things no file does: the Qobuz track id, the ISRC, and
        the format actually delivered. Only what no existing row claimed becomes a
        new ``scan``-origin row.

        This used to stop after the first pass, on the reasoning that a track row
        carries the Qobuz track id as its primary key and so a file the database
        has never heard of could not become one. True of the key, wrong as a
        conclusion: it left every release Qobuzarr did not download itself as an
        opaque folder — nothing to fingerprint, no per-file quality to compare, no
        row for a corruption verdict to land on. Those are precisely the releases
        whose provenance is unknown and therefore worth checking.

        :meth:`_baseline` then hashes **only** the files whose size or mtime
        disagrees with what the row records, in a worker thread. Pairing has to
        come first for that to be possible at all: the tripwire is a comparison
        against a specific row, so until the file has a row there is nothing to
        compare it to.

        The rows are then written, and finally *retired* as well as created: a
        ``scan`` row this pass did not account for describes a file that is no
        longer there under that number, and leaving it would let a library that
        shrank keep reporting the tracks it lost. Only ``scan`` rows are ever
        removed — a ``download`` row is the record of work this program did, and a
        file temporarily missing from an unmounted share must not erase it. That
        is :func:`app.core.scheduler._verify_library`'s job, at the album level,
        where it can be reversed.
        """
        pairs = self._pair_files(album, scanned)
        digests = await self._baseline(pairs)

        verdicts: list[IntegrityState] = []
        for track, scanned_track in pairs:
            state = self._apply_file(track, scanned_track, digests)
            if state is not None:
                verdicts.append(state)

        retired = self._retire_scanned(album, {track.id for track, _ in pairs})
        self._drop_claims(session, retired)
        # The track rows have to REACH the database before anything references
        # them. ``file_claims.track_id`` is a ForeignKey, but there is no ORM
        # relationship from Track to FileClaim — CLAUDE.md forbids one, because
        # the synchronous read models in app/api/deps.py would raise
        # MissingGreenlet on it — and without a relationship the unit of work has
        # no inter-mapper dependency to sort on. A table-level ForeignKey orders
        # DDL, not the INSERTs of a flush. Measured on this library, the flush
        # emitted ``INSERT INTO file_claims`` first and never reached
        # ``INSERT INTO tracks`` at all: ten pending Track objects sat in
        # session.new, correctly keyed, while their claims were written against
        # rows that did not exist yet, and the album died of FOREIGN KEY
        # constraint failed. Flushing here is what makes the claim's parent real.
        await session.flush()
        await self._record_claims(session, pairs)
        await self._record_corruption(session, pairs, result)
        self._stamp_release(album)
        await self._report_integrity(session, album, verdicts, result)
        return len(pairs)

    async def _record_corruption(
        self,
        session: AsyncSession,
        pairs: Sequence[tuple[Track, ScannedTrack]],
        result: ScanResult,
    ) -> None:
        """Record which files hold no playable audio, in ``track_metadata``.

        The verdict goes to ``fingerprint_state`` — the column
        :func:`app.core.librarian.quarantine_corrupt_files`,
        :func:`app.api.deps.album_corrupt_counts` and the Integrity screen all
        already read — rather than to a channel of its own. There is one concept
        of "this file is broken" in the application and it has one home; a second
        one would mean every consumer learning about both, and the consumer that
        forgot would be a screen quietly under-reporting.

        The column is named for the fingerprinter because that used to be the only
        thing that could reach this verdict. It is not what the column *means*:
        it means the audio is unusable, and there are two ways to establish that.
        ``fpcalc`` decoding a file and failing is one — see
        :mod:`app.enrich.chromaprint`, which is careful to separate that from a
        missing tool, a timeout and an unreachable path. A file nothing can parse
        at all is the other, and it is the stronger evidence of the two: fpcalc
        never even gets to run, because there is nothing to run on. Critically it
        is reached by the same standard, ``ScannedTrack.unreadable``, which
        :func:`read_track` sets only for a file that was present and stable
        across the failure.

        **Both directions, or the verdict never clears.** A file that has been
        replaced with a good copy must lose the mark on the very next scan, or
        the Integrity screen keeps reporting a problem somebody has already
        fixed — and since the fix is *replace the file*, that is the common case
        rather than the rare one. So a readable file clears the flag it set.
        Nothing else in the row is touched: the recording ids and the AcoustID
        match belong to :mod:`app.enrich.acoustid` and are none of the scan's
        business.

        ``track_metadata`` is one of :data:`app.models.ENRICHMENT_TABLES`, so a
        schema bump drops all of this — which is correct and is the test those
        tables are chosen by: every row here is re-derived by one pass over the
        disk, which is exactly what this method is.

        Swallowed on failure like the scan's other side-table writes: losing it
        delays a verdict until the next scan rather than failing the album.
        """
        from app.models import TrackMetadata  # noqa: PLC0415 - keeps the graph flat

        broken = {track.id for track, scanned in pairs if scanned.unreadable}
        track_ids = [track.id for track, _ in pairs]
        if not track_ids:
            return

        try:
            rows = (
                (
                    await session.execute(
                        select(TrackMetadata).where(
                            TrackMetadata.track_id.in_(track_ids)
                        )
                    )
                )
                .scalars()
                .all()
            )
            existing = {str(row.track_id): row for row in rows}

            for track_id in track_ids:
                row = existing.get(str(track_id))
                if track_id in broken:
                    result.files_corrupt += 1
                    if row is None:
                        session.add(
                            TrackMetadata(
                                track_id=track_id,
                                fingerprint_state=FingerprintState.CORRUPT,
                            )
                        )
                    elif row.fingerprint_state is not FingerprintState.CORRUPT:
                        row.fingerprint_state = FingerprintState.CORRUPT
                elif (
                    row is not None
                    and row.fingerprint_state is FingerprintState.CORRUPT
                ):
                    # It parses now. Whatever made it unreadable is gone, and a
                    # stale alarm is worse than none: it teaches people that the
                    # number on the Integrity screen does not go down.
                    row.fingerprint_state = None
                    result.files_recovered += 1
        except Exception:  # noqa: BLE001 - a scan must not fail on a side table
            logger.exception("Could not record corruption verdicts")

    async def _report_integrity(
        self,
        session: AsyncSession,
        album: Album,
        verdicts: Sequence[IntegrityState],
        result: ScanResult,
    ) -> None:
        """Count what the measured files turned out to be, and act on a replacement.

        The scan re-baselines whatever it hashed, so this is the only moment the
        verdict exists: by the time the nightly rotation reaches the file, the row
        agrees with it again. That is what made a replaced file invisible in the
        default configuration — housekeeping runs the disk scan first, the
        integrity rotation last, and the rotation covers 1/30 of the library a
        night, so any change that moved size or mtime was measured, overwritten
        and forgotten before anything could report it.

        ``REPLACED`` re-opens the release for identification, exactly as
        :func:`app.core.scheduler._record_measurements` does and for the same
        reason: the recording ids, the release match and the fingerprint verdicts
        were all made about bytes that are gone. ``RETAGGED`` does not — the audio
        is provably the same, so only the stamp needed refreshing.

        This does not make the scan two-way. It writes a work row for the
        enricher and counts on the report; it starts no download, changes no
        album status, and touches no file. Failure is swallowed like every other
        side-table write here: the album is reached again from its status on a
        later ``_seed``.
        """
        for state in verdicts:
            result.files_measured += 1
            if state is IntegrityState.RETAGGED:
                result.files_retagged += 1
            elif state is IntegrityState.REPLACED:
                result.files_replaced += 1
        if IntegrityState.REPLACED not in verdicts:
            return

        from app.core.enricher import reopen_library_albums  # noqa: PLC0415 - cycle

        logger.warning(
            "The audio of %d file(s) in %s is not the audio that was recorded; "
            "re-identifying the release",
            sum(1 for state in verdicts if state is IntegrityState.REPLACED),
            album.display_title,
        )
        try:
            result.albums_reopened += await reopen_library_albums(
                session, [str(album.id)]
            )
        except Exception:  # noqa: BLE001 - a scan must not fail on a side table
            logger.exception("Could not re-open %s for re-identification", album.id)

    def _pair_files(
        self, album: Album, scanned: ScannedAlbum
    ) -> list[tuple[Track, ScannedTrack]]:
        """Decide which track row each file on disk belongs to. No I/O.

        Returns the pairs in disk order. A file that could not be given a row of
        its own is simply absent from the result — see :meth:`_adopt_track`.
        """
        by_position: dict[tuple[int, int], Track] = {}
        by_title: dict[str, Track] = {}
        for track in album.tracks:
            by_position.setdefault(
                (track.media_number or 1, track.track_number or 0), track
            )
            by_title.setdefault(dedupe_key(track.title), track)

        pairs: list[tuple[Track, ScannedTrack]] = []
        claimed: set[str] = set()
        for scanned_track in scanned.tracks:
            # A file with no track number matches nothing by position: every
            # such file would look up the same ``(disc, 0)`` slot, so the first
            # would claim a row keyed on somebody else's filename and the rest
            # would find that row already claimed and be dropped. They go
            # straight to their own deterministic filename-based id instead.
            track = (
                by_position.get((scanned_track.media_number, scanned_track.track_number))
                if scanned_track.track_number > 0
                else None
            )
            if track is None and scanned_track.title:
                track = by_title.get(dedupe_key(scanned_track.title))
            if track is None or track.id in claimed:
                track = self._adopt_track(album, scanned_track, claimed)
                if track is None:
                    continue
            claimed.add(track.id)
            pairs.append((track, scanned_track))
        return pairs

    async def _baseline(
        self, pairs: Sequence[tuple[Track, ScannedTrack]]
    ) -> dict[Path, str]:
        """Hash the files whose tripwire fired, and nothing else.

        This is the one expensive thing the scan does, and the whole point of the
        tripwire is that it almost never runs: a nightly scan over a library that
        did not change hashes zero files. Hashing all of them instead costs about
        201 ms each — an hour and a half on 30 000 files, every night, to learn
        nothing — which is not a slower scan but a scan that gets switched off.

        The work goes to a thread because it is blocking file I/O, exactly like
        :func:`collect_albums`. Doing it inline would stall the event loop for
        minutes on a large adoption, and with it the download worker, the
        scheduler and every HTTP request.
        """
        stale = [
            scanned_track.path
            for track, scanned_track in pairs
            # An unreadable file is never hashed. A digest of it would be a
            # perfectly good baseline for a file that has no audio, and the row
            # would then read as VERIFIED for as long as nobody touched it —
            # the integrity states describe *changes* to audio, and there is no
            # audio here to change. The verdict this file gets is `corrupt`.
            if not scanned_track.unreadable
            and self._tripwire_fired(track, scanned_track)
        ]
        if not stale:
            return {}
        logger.debug("scan.hashing files=%d", len(stale))
        return await asyncio.to_thread(_hash_files, stale)

    @staticmethod
    def _tripwire_fired(track: Track, scanned: ScannedTrack) -> bool:
        """Is it worth opening this file?

        Yes when nothing was ever recorded — an unbaselined row is ``UNKNOWN``,
        which is not ``CHANGED`` but is the state that has to be resolved by
        measuring — and yes when size or mtime differ from what was recorded.

        Neither number is evidence and neither is compared anywhere else: a tag
        editor that preserves mtime and a filesystem with one-second resolution
        both make this pair lie towards "unchanged", which is why a match here
        means *skip the read* rather than *the file is verified*. In the other
        direction it is free to be over-eager — an mtime that moved for no reason
        costs exactly one hash, which then agrees with the recorded one.
        """
        if not track.content_hash:
            return True
        if track.file_mtime is None or track.file_size is None:
            return True
        return track.file_size != scanned.file_size or track.file_mtime != scanned.file_mtime

    @staticmethod
    def _apply_file(
        track: Track, scanned: ScannedTrack, digests: dict[Path, str]
    ) -> IntegrityState | None:
        """Write one file's facts onto its row, and say what the file turned out to be.

        The stream properties are filled in only where the row is silent: never
        overwrite what Qobuz told us the delivered file was, because that came
        from the response that actually delivered the bytes.

        The *measurement* is the other way round — it describes the file as it is
        right now — but it is written as one thing or not at all. The four
        columns are a single statement about a single read, and splitting them is
        what made the tripwire silenceable: a file that really had been replaced,
        whose hash then failed to read, kept the **old** ``content_hash`` beside
        the **new** size, mtime and sample count. Every later pass then compared
        size and mtime, found them equal, and never opened the file again — and
        when the rotation in :func:`app.core.scheduler.verify_integrity`
        eventually did, the sample count it was compared against had already been
        copied from the new file, so a replacement classified as ``RETAGGED`` and
        the release was never re-identified. So when :meth:`_baseline` came back
        without a digest, nothing recorded is touched and the tripwire fires
        again next pass, which is the honest outcome.

        The verdict is taken **before** the new stamp lands, for the same reason
        :func:`app.core.scheduler._record_measurements` takes it first: this pass
        re-baselines what it measures, so afterwards every file agrees with the
        row and there is nothing left to notice. Silently re-baselining is what
        made ``REPLACED`` undetectable in the default configuration — housekeeping
        runs the scan first and the integrity rotation last, and the rotation
        covers 1/30 of the library a night, so for any change that moved size or
        mtime the scan had already erased the evidence. Returns ``None`` when
        nothing was measured, which is not a verdict.

        ``verified_at`` moves only when the file was really hashed on this pass. A
        tripwire that did not fire is a decision not to look, and stamping it as a
        verification would turn "we skipped this for a year" into "we checked this
        last night".
        """
        _ensure_qid(track, QID_TRACK_PREFIX)
        track.path = str(scanned.path)

        if scanned.unreadable:
            # The file is there and holds no playable audio. It is recorded —
            # path, size, and a row for the verdict to hang off — but it is not
            # `DOWNLOADED`, because `deps._apply_completeness` counts exactly
            # that, and calling a broken file present is what makes a release
            # report 13 of 13 while six of them are silent. `FAILED` is the
            # honest one: something is on disk and it did not work out.
            track.status = TrackStatus.FAILED
            track.file_size = scanned.file_size or None
            # No stream properties, no `downloaded_at`, and above all no stamp:
            # nothing was measured, so there is no verdict, which is what
            # returning None means everywhere else in this function.
            return None

        track.status = TrackStatus.DOWNLOADED
        # Never overwrite what Qobuz told us the delivered file was; only
        # fill the gaps for a file Qobuzarr did not download itself.
        if track.bit_depth is None:
            track.bit_depth = scanned.bit_depth
        if track.sampling_rate is None:
            track.sampling_rate = scanned.sampling_rate
        if track.duration is None:
            track.duration = scanned.duration
        track.downloaded_at = track.downloaded_at or utcnow()

        digest = digests.get(scanned.path)
        if digest is None:
            # Either the tripwire did not fire — in which case size and mtime
            # already agree and there is nothing to write — or the read failed,
            # in which case writing half a stamp is the bug described above.
            if track.file_size is None:
                track.file_size = scanned.file_size or None
            return None

        current = FileStamp(
            size=scanned.file_size,
            mtime=scanned.file_mtime,
            content_hash=digest,
            sample_count=scanned.sample_count,
        )
        state = classify(recorded=recorded_stamp(track), current=current)
        track.content_hash = current.content_hash
        track.sample_count = current.sample_count
        track.file_size = current.size
        track.file_mtime = current.mtime
        track.verified_at = utcnow()
        return state

    async def _record_claims(
        self, session: AsyncSession, pairs: Sequence[tuple[Track, ScannedTrack]]
    ) -> None:
        """Store what the files *claim*, in ``file_claims`` and nowhere else.

        These strings are hypotheses. They go to :class:`~app.models.FileClaim`
        precisely so that they cannot be mistaken for the verified ids in
        ``track_metadata``, which arrived by exact barcode match, by a human
        choosing, or with the audio itself agreeing. A claim may narrow a search
        and may reject a candidate; promoting one to a fact has to be a different
        write, made on purpose, by something that checked.

        A file claiming nothing gets no row — most libraries are full of them and
        an empty row says nothing. But an existing row whose file has *stopped*
        claiming something is cleared rather than left standing: the tag was
        removed or corrected, and the fourth principle is to verify the disk
        rather than trust the database. Unchanged rows are not rewritten, so a
        nightly scan over a stable library issues no writes here at all.
        """
        track_ids = [track.id for track, _ in pairs]
        if not track_ids:
            return

        rows = await session.execute(
            select(FileClaim).where(FileClaim.track_id.in_(track_ids))
        )
        existing = {row.track_id: row for row in rows.scalars().all()}

        for track, scanned in pairs:
            claims = scanned.claims()
            row = existing.get(track.id)
            if row is None:
                if any(claims.values()):
                    session.add(FileClaim(track_id=track.id, **claims))
                continue
            changed = [
                (column, value)
                for column, value in claims.items()
                if getattr(row, column) != value
            ]
            for column, value in changed:
                setattr(row, column, value)
            if changed:
                row.read_at = utcnow()

    @staticmethod
    def _stamp_release(album: Album) -> None:
        """Give the release and its artist their identity and its digest.

        The digest is taken over the member hashes in disc/track order, which is
        what makes it a statement about *this release as sequenced* rather than
        about a bag of files: a retagger that swapped two track numbers has
        changed the release, and a set-of-hashes digest would call that unchanged.
        It is ``None`` — recomputed as ``None``, not left stale — whenever any
        member is unmeasured, because a digest over the subset that happens to be
        hashed changes the moment the rest are, and would report the album as
        altered on every pass forever.
        """
        _ensure_qid(album, QID_RELEASE_PREFIX)
        artist = getattr(album, "artist", None)
        if artist is not None:
            _ensure_qid(artist, QID_ARTIST_PREFIX)

        ordered = sorted(
            album.tracks,
            key=lambda track: (track.media_number or 1, track.track_number or 0, track.id),
        )
        album.content_digest = album_content_digest([track.content_hash for track in ordered])

    def _adopt_track(
        self, album: Album, scanned_track: ScannedTrack, claimed: set[str]
    ) -> Track | None:
        """Create — or re-find — the ``scan`` row for one unclaimed file.

        Returns ``None`` when the id is already spoken for this pass, which is
        what two files sharing a disc/track number look like. Neither is more
        right than the other, so the first one seen keeps the row and the second
        is left out rather than overwriting it.
        """
        track_id = scanned_track_id(str(album.id), scanned_track)
        if track_id in claimed:
            return None
        existing = next((t for t in album.tracks if t.id == track_id), None)
        if existing is not None:
            return existing
        track = Track(
            id=track_id,
            album_id=str(album.id),
            title=scanned_track.title or scanned_track.path.stem,
            track_number=scanned_track.track_number,
            media_number=scanned_track.media_number or 1,
            origin=TrackOrigin.SCAN,
            status=TrackStatus.PENDING,
        )
        album.tracks.append(track)
        return track

    @staticmethod
    def _retire_scanned(album: Album, claimed: set[str]) -> set[str]:
        """Drop ``scan`` rows for files this pass did not find.

        ``Album.tracks`` cascades ``delete-orphan``, so removing from the
        collection is the deletion — and it takes the row's ``track_metadata``
        with it, which is correct: a fingerprint is a statement about a file that
        is no longer where that row said it was.

        Returns the ids it retired, because the caller has one more table to
        settle: see :meth:`_drop_claims`.
        """
        stale = [
            track
            for track in album.tracks
            if track.origin is TrackOrigin.SCAN and track.id not in claimed
        ]
        for track in stale:
            album.tracks.remove(track)
        return {track.id for track in stale}

    @staticmethod
    def _drop_claims(session: AsyncSession, retired: set[str]) -> None:
        """Discard pending ``file_claims`` whose track this pass just retired.

        ``file_claims.track_id`` is ``ForeignKey(..., ondelete="CASCADE")``, which
        settles the *persistent* case entirely: the ``DELETE`` that
        :meth:`_retire_scanned` causes takes the stored claim with it, in the
        database, without SQLAlchemy having to know the two tables are related.
        And it deliberately does not know — ``CLAUDE.md`` forbids an ORM
        relationship from :class:`~app.models.Track` to its side tables, because
        the synchronous read models in ``app/api/deps.py`` would then raise
        ``MissingGreenlet`` on a row that was committed but never refreshed.

        The gap that leaves is the *pending* case, and it is the one that fired.
        The whole scan runs in a single transaction, so a claim written for a
        track that has not been flushed yet is an ``INSERT`` waiting in
        ``session.new``. Discarding the track from ``album.tracks`` before that
        flush does not emit a ``DELETE`` — a never-inserted row has nothing to
        delete, it is simply dropped — so the claim survives its parent and the
        flush ends with ``INSERT INTO file_claims ... FOREIGN KEY constraint
        failed``. One album's worth of files did that to a real 4 704-album
        library and took the rest of the scan down with it.

        So the claims are expunged explicitly, here, at the one moment the
        scanner knows a track is going away.
        """
        if not retired:
            return
        for obj in list(session.new):
            if isinstance(obj, FileClaim) and obj.track_id in retired:
                session.expunge(obj)

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
