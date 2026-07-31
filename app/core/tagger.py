"""Metadata tagging for downloaded audio files.

Qobuz delivers ready-to-play FLAC and MP3, so all that is left is writing the
tags Qobuzarr knows about and embedding the cover art.  Two containers are
supported:

* **FLAC** — Vorbis comments plus a ``METADATA_BLOCK_PICTURE``.
* **MP3** — ID3v2.4 frames plus an ``APIC`` frame.

Everything here is **synchronous** and does blocking file I/O; callers on the
event loop must wrap it, e.g.::

    await asyncio.to_thread(tag_file, path, track, album, cover_bytes)

Tagging failures are reported by returning ``False`` (and logging a warning)
rather than by raising: a perfectly good audio file should never be thrown away
because mutagen disliked one of its frames.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Mapping

from mutagen import MutagenError
from mutagen.flac import FLAC, Picture
from mutagen.id3 import (
    APIC,
    ID3,
    ID3NoHeaderError,
    TALB,
    TCOM,
    TCON,
    TDRC,
    TIT2,
    TPE1,
    TPE2,
    TPOS,
    TPUB,
    TRCK,
    TSRC,
)

from app.logging_conf import get_logger

__all__ = [
    "COVER_DESCRIPTION",
    "FRONT_COVER_TYPE",
    "build_tags",
    "detect_image_mime",
    "tag_file",
]

logger = get_logger(__name__)

#: ID3/FLAC picture type 3 == "Cover (front)".
FRONT_COVER_TYPE: int = 3

#: Description written alongside the embedded picture.
COVER_DESCRIPTION: str = "Cover"

#: Extensions handled natively; anything else is sniffed by mutagen.
_FLAC_EXTENSIONS = frozenset({"flac"})
_MP3_EXTENSIONS = frozenset({"mp3", "mp2", "mpga"})

#: Magic bytes -> MIME type, for cover art of unknown provenance.
_IMAGE_MAGIC: tuple[tuple[bytes, str], ...] = (
    (b"\xff\xd8\xff", "image/jpeg"),
    (b"\x89PNG\r\n\x1a\n", "image/png"),
    (b"GIF87a", "image/gif"),
    (b"GIF89a", "image/gif"),
    (b"BM", "image/bmp"),
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def _get(obj: Any, name: str, default: Any = None) -> Any:
    """Read *name* off an ORM row, a dataclass or a mapping."""
    if obj is None:
        return default
    if isinstance(obj, Mapping):
        value = obj.get(name, default)
    else:
        value = getattr(obj, name, default)
    return default if value is None else value


def _text(value: Any) -> str:
    """Coerce *value* to a trimmed string (``None`` becomes ``""``)."""
    if value is None:
        return ""
    return str(value).strip()


def _int(value: Any, default: int = 0) -> int:
    """Best-effort integer conversion that never raises."""
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def detect_image_mime(data: bytes | None, default: str = "image/jpeg") -> str:
    """Return the MIME type of *data* from its magic bytes."""
    if not data:
        return default
    for magic, mime in _IMAGE_MAGIC:
        if data.startswith(magic):
            return mime
    return default


def _container_for(path: Path, ext: str | None) -> str:
    """Decide which tagging path to take: ``"flac"``, ``"mp3"`` or ``""``."""
    suffix = (ext or path.suffix.lstrip(".")).strip().lower()
    if suffix in _FLAC_EXTENSIONS:
        return "flac"
    if suffix in _MP3_EXTENSIONS:
        return "mp3"
    return ""


def build_tags(
    track: Any,
    album: Any,
    artist_name: str | None = None,
) -> dict[str, str]:
    """Collect the common tag values for one track as plain strings.

    Empty values are omitted entirely so that neither container ends up with
    blank frames. The ``date`` value is the ISO release date when known, or the
    bare year when only that is available.

    Args:
        track: An :class:`~app.models.Track` row or mapping.
        album: The owning :class:`~app.models.Album` row or mapping.
        artist_name: Album artist. Falls back to ``album.artist.name``, then to
            the track performer.

    Returns:
        A dict keyed by lowercase logical tag names (``title``, ``artist``,
        ``albumartist``, ``album``, ``date``, ``year``, ``tracknumber``,
        ``totaltracks``, ``discnumber``, ``totaldiscs``, ``genre``, ``isrc``,
        ``label``, ``composer``, ``version``).
    """
    title = _text(_get(track, "title")) or "Unknown Track"
    track_version = _text(_get(track, "version"))
    if track_version:
        title = f"{title} ({track_version})"

    album_title = _text(_get(album, "title")) or "Unknown Album"
    album_version = _text(_get(album, "version"))
    if album_version:
        album_title = f"{album_title} ({album_version})"

    albumartist = _text(artist_name)
    if not albumartist:
        albumartist = _text(_get(_get(album, "artist"), "name"))
    performer = _text(_get(track, "performer")) or albumartist
    if not albumartist:
        albumartist = performer

    release_date = _get(album, "release_date")
    year = _get(album, "year")
    if not year and release_date is not None:
        year = getattr(release_date, "year", None)

    date_value = ""
    if release_date is not None:
        date_value = _text(getattr(release_date, "isoformat", lambda: release_date)())
    elif year:
        date_value = _text(year)

    total_tracks = _int(_get(album, "tracks_count", 0), 0)
    total_discs = _int(_get(album, "media_count", 1), 1)

    tags: dict[str, str] = {
        "title": title,
        "version": track_version,
        "artist": performer,
        "albumartist": albumartist,
        "album": album_title,
        "date": date_value,
        "year": _text(year) if year else "",
        "tracknumber": _text(_int(_get(track, "track_number", 0), 0) or ""),
        "totaltracks": _text(total_tracks or ""),
        "discnumber": _text(_int(_get(track, "media_number", 1), 1) or ""),
        "totaldiscs": _text(total_discs or ""),
        "genre": _text(_get(album, "genre")),
        "isrc": _text(_get(track, "isrc")),
        "label": _text(_get(album, "label")),
        "composer": _text(_get(track, "composer")),
    }
    return {key: value for key, value in tags.items() if value}


# ---------------------------------------------------------------------------
# FLAC
# ---------------------------------------------------------------------------
def _tag_flac(
    path: Path,
    tags: Mapping[str, str],
    cover_bytes: bytes | None,
    cover_mime: str,
) -> None:
    """Write Vorbis comments and the front cover onto a FLAC file."""
    audio = FLAC(str(path))
    if audio.tags is None:
        audio.add_tags()
    else:
        # Drop whatever Qobuz shipped; Qobuzarr is authoritative. Clearing in
        # memory (rather than FLAC.delete()) avoids a second full-file rewrite.
        audio.tags.clear()

    vorbis: dict[str, str] = {
        "TITLE": tags.get("title", ""),
        "VERSION": tags.get("version", ""),
        "ARTIST": tags.get("artist", ""),
        "ALBUMARTIST": tags.get("albumartist", ""),
        "ALBUM": tags.get("album", ""),
        "DATE": tags.get("date", ""),
        "YEAR": tags.get("year", ""),
        "TRACKNUMBER": tags.get("tracknumber", ""),
        "TRACKTOTAL": tags.get("totaltracks", ""),
        "TOTALTRACKS": tags.get("totaltracks", ""),
        "DISCNUMBER": tags.get("discnumber", ""),
        "DISCTOTAL": tags.get("totaldiscs", ""),
        "TOTALDISCS": tags.get("totaldiscs", ""),
        "GENRE": tags.get("genre", ""),
        "ISRC": tags.get("isrc", ""),
        "LABEL": tags.get("label", ""),
        "ORGANIZATION": tags.get("label", ""),
        "COMPOSER": tags.get("composer", ""),
    }
    for key, value in vorbis.items():
        if value:
            audio[key] = value

    audio.clear_pictures()
    if cover_bytes:
        picture = Picture()
        picture.type = FRONT_COVER_TYPE
        picture.mime = cover_mime
        picture.desc = COVER_DESCRIPTION
        picture.data = cover_bytes
        audio.add_picture(picture)

    audio.save()


# ---------------------------------------------------------------------------
# MP3 / ID3
# ---------------------------------------------------------------------------
def _tag_mp3(
    path: Path,
    tags: Mapping[str, str],
    cover_bytes: bytes | None,
    cover_mime: str,
) -> None:
    """Write ID3v2.4 frames and the APIC cover onto an MP3 file."""
    try:
        id3 = ID3(str(path))
    except ID3NoHeaderError:
        id3 = ID3()
    # Replace, never merge: a stale TPE1 from Qobuz must not survive.
    id3.clear()

    def _slash_pair(number: str, total: str) -> str:
        return f"{number}/{total}" if number and total else number

    frames = [
        (TIT2, tags.get("title", "")),
        (TPE1, tags.get("artist", "")),
        (TPE2, tags.get("albumartist", "")),
        (TALB, tags.get("album", "")),
        (TDRC, tags.get("date", "") or tags.get("year", "")),
        (TCON, tags.get("genre", "")),
        (TSRC, tags.get("isrc", "")),
        (TPUB, tags.get("label", "")),
        (TCOM, tags.get("composer", "")),
        (TRCK, _slash_pair(tags.get("tracknumber", ""), tags.get("totaltracks", ""))),
        (TPOS, _slash_pair(tags.get("discnumber", ""), tags.get("totaldiscs", ""))),
    ]
    for frame_cls, value in frames:
        if value:
            id3.add(frame_cls(encoding=3, text=[value]))

    if cover_bytes:
        id3.add(
            APIC(
                encoding=3,
                mime=cover_mime,
                type=FRONT_COVER_TYPE,
                desc=COVER_DESCRIPTION,
                data=cover_bytes,
            )
        )

    # v1=0 strips any trailing ID3v1 tag so the two cannot disagree.
    id3.save(str(path), v2_version=4, v1=0)


# ---------------------------------------------------------------------------
# Public entry point
# ---------------------------------------------------------------------------
def tag_file(
    path: Path | str,
    track: Any,
    album: Any,
    cover_bytes: bytes | None = None,
    *,
    artist_name: str | None = None,
    ext: str | None = None,
    cover_mime: str | None = None,
) -> bool:
    """Write metadata (and optionally cover art) onto a downloaded file.

    Synchronous and blocking — call it via :func:`asyncio.to_thread`.

    The container is chosen from *ext* when given, otherwise from the file
    suffix. That matters because the downloader tags a ``.part`` file *before*
    renaming it into place, so the suffix on disk is not the real one.

    Args:
        path: The audio file to tag, in place.
        track: An :class:`~app.models.Track` row or mapping.
        album: The owning :class:`~app.models.Album` row or mapping.
        cover_bytes: Raw front-cover image, or ``None`` to embed nothing. Any
            existing embedded artwork is replaced.
        artist_name: Album artist override (avoids touching a lazy relationship
            from a worker thread).
        ext: Container hint without the dot, e.g. ``"flac"`` or ``"mp3"``.
        cover_mime: MIME type of *cover_bytes*; sniffed from the data when
            omitted.

    Returns:
        ``True`` when the file was tagged, ``False`` when it could not be (the
        audio itself is left untouched and the reason is logged).
    """
    path = Path(path)
    if not path.exists():
        logger.warning("Cannot tag missing file %s", path)
        return False

    container = _container_for(path, ext)
    tags = build_tags(track, album, artist_name)
    mime = cover_mime or detect_image_mime(cover_bytes)

    try:
        if container == "flac":
            _tag_flac(path, tags, cover_bytes, mime)
        elif container == "mp3":
            _tag_mp3(path, tags, cover_bytes, mime)
        else:
            logger.warning(
                "Unsupported container for tagging: %s (ext=%r)", path.name, ext
            )
            return False
    except MutagenError as exc:
        logger.warning("Tagging failed for %s: %s", path.name, exc)
        return False
    except OSError as exc:
        logger.warning("Tagging failed for %s: %s", path.name, exc)
        return False

    logger.debug("Tagged %s (%s, cover=%s)", path.name, container, bool(cover_bytes))
    return True
