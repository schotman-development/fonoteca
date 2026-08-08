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
    TDOR,
    TIT2,
    TPE1,
    TPE2,
    TPOS,
    TPUB,
    TRCK,
    TSRC,
    TXXX,
    UFID,
)

from app.logging_conf import get_logger

__all__ = [
    "COVER_DESCRIPTION",
    "FRONT_COVER_TYPE",
    "ID3_FRAMES",
    "ID3_SLASH_PAIRS",
    "MUSICBRAINZ_UFID_OWNER",
    "QOBUZ_TAG_FIELDS",
    "TXXX_TAGS",
    "VORBIS_FIELDS",
    "VORBIS_YEAR_SLICE",
    "build_tags",
    "detect_image_mime",
    "id3_frame_for",
    "tag_file",
]

logger = get_logger(__name__)

#: ID3/FLAC picture type 3 == "Cover (front)".
FRONT_COVER_TYPE: int = 3

#: Description written alongside the embedded picture.
COVER_DESCRIPTION: str = "Cover"

#: Owner string MusicBrainz uses for its ``UFID`` frame. Picard writes exactly
#: this and beets looks for exactly this; anything else is invisible to both.
MUSICBRAINZ_UFID_OWNER = "http://musicbrainz.org"

#: ``(TXXX description, logical tag key)``. ID3 has no dedicated frames for any
#: of these, so Picard's descriptions are the de facto standard — spelling one
#: differently makes the tag unreadable to every tool that would have used it.
TXXX_TAGS: tuple[tuple[str, str], ...] = (
    ("MusicBrainz Artist Id", "musicbrainz_artistid"),
    ("MusicBrainz Album Artist Id", "musicbrainz_albumartistid"),
    ("MusicBrainz Album Id", "musicbrainz_albumid"),
    ("MusicBrainz Release Group Id", "musicbrainz_releasegroupid"),
    ("MusicBrainz Release Track Id", "musicbrainz_releasetrackid"),
    ("MusicBrainz Album Release Country", "releasecountry"),
    ("MusicBrainz Album Status", "releasestatus"),
    ("MusicBrainz Album Type", "releasetype"),
    ("Acoustid Id", "acoustid_id"),
    ("BARCODE", "barcode"),
    ("CATALOGNUMBER", "catalognumber"),
    ("ISNI", "isni"),
)

#: ``(Vorbis field, logical tag key)``, in write order. The single source of
#: truth for what a FLAC gets: :func:`_tag_flac` builds its dict from this, and
#: ``deps.build_tag_map`` publishes it as ``MetaOut.tag_map``. A table written
#: out a second time beside the tagger drifts from it silently — the whole point
#: of publishing this is that the screen cannot say something the writer does
#: not do.
VORBIS_FIELDS: tuple[tuple[str, str], ...] = (
    ("TITLE", "title"),
    ("VERSION", "version"),
    ("ARTIST", "artist"),
    ("ALBUMARTIST", "albumartist"),
    ("ALBUM", "album"),
    ("DATE", "date"),
    ("YEAR", "year"),
    ("TRACKNUMBER", "tracknumber"),
    ("TRACKTOTAL", "totaltracks"),
    ("TOTALTRACKS", "totaltracks"),
    ("DISCNUMBER", "discnumber"),
    ("DISCTOTAL", "totaldiscs"),
    ("TOTALDISCS", "totaldiscs"),
    ("GENRE", "genre"),
    ("ISRC", "isrc"),
    ("LABEL", "label"),
    ("ORGANIZATION", "label"),
    ("COMPOSER", "composer"),
    # Enrichment. Vorbis field names follow Picard's, because these tags only
    # earn their keep if the tools that read them recognise the spelling.
    ("MUSICBRAINZ_ARTISTID", "musicbrainz_artistid"),
    ("MUSICBRAINZ_ALBUMARTISTID", "musicbrainz_albumartistid"),
    ("MUSICBRAINZ_ALBUMID", "musicbrainz_albumid"),
    ("MUSICBRAINZ_RELEASEGROUPID", "musicbrainz_releasegroupid"),
    ("MUSICBRAINZ_RELEASETRACKID", "musicbrainz_releasetrackid"),
    ("MUSICBRAINZ_TRACKID", "musicbrainz_trackid"),
    ("ACOUSTID_ID", "acoustid_id"),
    ("ISNI", "isni"),
    ("BARCODE", "barcode"),
    ("CATALOGNUMBER", "catalognumber"),
    ("RELEASECOUNTRY", "releasecountry"),
    ("RELEASESTATUS", "releasestatus"),
    ("RELEASETYPE", "releasetype"),
    ("ORIGINALDATE", "originaldate"),
    ("ORIGINALYEAR", "originaldate"),
)

#: The one Vorbis field that is a slice of another's value rather than the value
#: itself: ``ORIGINALYEAR`` is the first four characters of ``originaldate``.
VORBIS_YEAR_SLICE: frozenset[str] = frozenset({"ORIGINALYEAR"})

#: ``(frame class, ID3v2.4 frame id, logical tag key)`` — every single-valued
#: frame :func:`_tag_mp3` writes. :func:`_tag_mp3` builds its frame list from
#: this and :func:`id3_frame_for` reads the same tuple, so the writer and the
#: published table cannot disagree. The slash pairs live in
#: :data:`ID3_SLASH_PAIRS` for the same reason; nothing restates a frame id.
ID3_FRAMES: tuple[tuple[Any, str, str], ...] = (
    (TIT2, "TIT2", "title"),
    (TPE1, "TPE1", "artist"),
    (TPE2, "TPE2", "albumartist"),
    (TALB, "TALB", "album"),
    (TDRC, "TDRC", "date"),
    (TCON, "TCON", "genre"),
    (TSRC, "TSRC", "isrc"),
    (TPUB, "TPUB", "label"),
    (TCOM, "TCOM", "composer"),
    (TDOR, "TDOR", "originaldate"),
)

#: ``(frame class, frame id, number key, total key)`` — the two frames carrying
#: ``number/total`` rather than one value. Both :func:`_tag_mp3` and
#: :func:`id3_frame_for` are built from this tuple.
ID3_SLASH_PAIRS: tuple[tuple[Any, str, str, str], ...] = (
    (TRCK, "TRCK", "tracknumber", "totaltracks"),
    (TPOS, "TPOS", "discnumber", "totaldiscs"),
)

#: ``(logical tag key, where the Qobuz catalogue value comes from)``. Every key
#: :func:`build_tags` fills from an ORM row, and nothing else — the rest of what
#: a file gets is enrichment, and ``nfo.ENRICHMENT_TAGS`` names those. A key in
#: neither is a tag nothing can explain, which ``tests/test_tag_map.py`` refuses.
QOBUZ_TAG_FIELDS: tuple[tuple[str, str], ...] = (
    ("title", "tracks.title (+ tracks.version)"),
    ("version", "tracks.version"),
    ("artist", "tracks.performer"),
    ("albumartist", "artists.name"),
    ("album", "albums.title (+ albums.version)"),
    ("date", "albums.release_date"),
    ("year", "albums.year"),
    ("tracknumber", "tracks.track_number"),
    ("totaltracks", "albums.tracks_count"),
    ("discnumber", "tracks.media_number"),
    ("totaldiscs", "albums.media_count"),
    ("genre", "albums.genre"),
    ("isrc", "tracks.isrc"),
    ("label", "albums.label"),
    ("composer", "tracks.composer"),
    ("barcode", "albums.upc"),
)

#: Logical key -> the slash-pair frame it lands in, built from
#: :data:`ID3_SLASH_PAIRS` at import so no frame id is written down twice.
_SLASH_PAIR_FRAMES: dict[str, str] = {
    key: f"{frame_id} (number/total)"
    for _frame_cls, frame_id, number_key, total_key in ID3_SLASH_PAIRS
    for key in (number_key, total_key)
}


def id3_frame_for(tag_key: str) -> str | None:
    """The ID3 frame :func:`_tag_mp3` writes for *tag_key*, or ``None``.

    Derived from :data:`ID3_FRAMES`, :data:`ID3_SLASH_PAIRS` and
    :data:`TXXX_TAGS` — the very tuples :func:`_tag_mp3` writes from, never a
    second list, because a restated mapping is a mapping that drifts. ``None``
    means MP3 files carry nothing for that key at all, which is a real answer
    and not a gap: ``VERSION`` has no ID3 equivalent worth inventing.
    """
    for _frame_cls, frame_id, key in ID3_FRAMES:
        if key == tag_key:
            return frame_id
    if tag_key in _SLASH_PAIR_FRAMES:
        return _SLASH_PAIR_FRAMES[tag_key]
    if tag_key == "musicbrainz_trackid":
        return f"UFID:{MUSICBRAINZ_UFID_OWNER}"
    for description, key in TXXX_TAGS:
        if key == tag_key:
            return f"TXXX:{description}"
    return None


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
    *,
    extra: Mapping[str, str] | None = None,
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
        # Stored since the beginning and never written until now.
        "barcode": _text(_get(album, "upc")),
    }
    # Enrichment values, already resolved on the event loop by the caller. They
    # are handed in rather than read off the rows because this function runs in a
    # worker thread, where touching a relationship raises MissingGreenlet — which
    # `_get` does not swallow, so it would fail the whole track.
    for key, value in (extra or {}).items():
        text = _text(value)
        if text:
            tags[key] = text
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

    # Built from VORBIS_FIELDS rather than restated here, so the table the
    # Structure & tags screen draws is the one this loop actually writes.
    vorbis: dict[str, str] = {
        field: (
            tags.get(key, "")[:4] if field in VORBIS_YEAR_SLICE else tags.get(key, "")
        )
        for field, key in VORBIS_FIELDS
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

    frames: list[tuple[Any, str]] = [
        (
            frame_cls,
            # TDRC falls back to the bare year; nothing else has a second source.
            (tags.get(key, "") or tags.get("year", "")) if key == "date" else tags.get(key, ""),
        )
        for frame_cls, _frame_id, key in ID3_FRAMES
    ]
    frames += [
        (frame_cls, _slash_pair(tags.get(number_key, ""), tags.get(total_key, "")))
        for frame_cls, _frame_id, number_key, total_key in ID3_SLASH_PAIRS
    ]
    for frame_cls, value in frames:
        if value:
            id3.add(frame_cls(encoding=3, text=[value]))

    # ID3 has no dedicated frames for any of this, so Picard's TXXX descriptions
    # are the de facto standard. Spelling them differently would make the tags
    # invisible to every tool that reads them.
    for description, key in TXXX_TAGS:
        value = tags.get(key, "")
        if value:
            id3.add(TXXX(encoding=3, desc=description, text=[value]))

    # The recording id is the one that gets a real frame: UFID with MusicBrainz's
    # own owner string, which is what Picard writes and beets reads.
    recording = tags.get("musicbrainz_trackid", "")
    if recording:
        id3.add(UFID(owner=MUSICBRAINZ_UFID_OWNER, data=recording.encode("ascii")))

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
    extra_tags: Mapping[str, str] | None = None,
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
        extra_tags: Already-resolved enrichment values (MusicBrainz ids, ISNI,
            catalogue number, ...). **Keyword-only and plain strings**: this runs
            in a worker thread, where reading a relationship off an ORM row
            raises ``MissingGreenlet`` — which ``_get`` does not swallow, so it
            would fail the whole track. The caller resolves them on the event
            loop and passes the result in.

    Returns:
        ``True`` when the file was tagged, ``False`` when it could not be (the
        audio itself is left untouched and the reason is logged).
    """
    path = Path(path)
    if not path.exists():
        logger.warning("Cannot tag missing file %s", path)
        return False

    container = _container_for(path, ext)
    tags = build_tags(track, album, artist_name, extra=extra_tags)
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
