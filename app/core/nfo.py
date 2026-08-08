"""Rendering what enrichment learned into files a media server can read.

Two outputs, one rule: **render here, write elsewhere.** Every function in this
module is pure and returns a string. Putting the bytes on disk is
:mod:`app.core.librarian`'s job, because that module is the only one allowed to
change files already in the library and it is where the safety gates live. This
is the same split :mod:`app.core.naming` uses.

``artist.nfo`` / ``album.nfo``
    Kodi's music NFO shape, which Jellyfin, Emby and Plex's agents all read.
    Written beside the music so a media server picks up the artist's biography,
    the release's identifiers and the artwork without repeating every lookup
    Qobuzarr already made.

:func:`tag_values`
    The enrichment half of the tags, resolved into plain strings for
    :func:`app.core.tagger.build_tags`. It lives here rather than in the tagger
    because the tagger runs in a worker thread and must never see an ORM row it
    might have to lazy-load.

A note on the biography: Wikipedia text is CC BY-SA, so the source link is not
decoration. It is written into the NFO with the text, always, and the two are
stored together for exactly that reason.

**An existing NFO is merged into, never replaced.** Real libraries already have
these files, written by Jellyfin, Emby or Kodi, and they hold things Qobuzarr
does not know: a biography from TheAudioDB, artwork paths, that server's own ids,
its ``dateadded``. Rewriting the file from scratch would silently destroy all of
it. So :func:`merge_nfo` updates the elements this module owns, leaves every
other element exactly where it was, and honours ``<lockdata>true</lockdata>`` by
refusing to touch the file at all — that flag is how a user tells their media
server "stop editing this", and it is not ours to ignore.
"""

from __future__ import annotations

import xml.etree.ElementTree as ET
from typing import Any, Iterable, Mapping, Sequence

from app.models import AlbumMetadata, ArtistMetadata, TrackMetadata, parse_tags

__all__ = [
    "ARTIST_NFO",
    "ALBUM_NFO",
    "DERIVED_TAGS",
    "ENRICHMENT_TAGS",
    "NfoLocked",
    "merge_nfo",
    "render_artist_nfo",
    "render_album_nfo",
    "tag_values",
]

#: The filenames media servers look for.
ARTIST_NFO = "artist.nfo"
ALBUM_NFO = "album.nfo"

#: Element names that mean the same thing in different servers' dialects. Kodi
#: writes ``musicBrainzArtistID``; Jellyfin and Emby write
#: ``musicbrainzartistid`` or ``musicbrainzalbumartistid``. When a file already
#: uses one spelling, that is the one to update — adding the other would leave
#: two elements claiming the same fact, and the server would pick whichever it
#: liked.
_ALIASES: dict[str, tuple[str, ...]] = {
    "musicBrainzArtistID": ("musicbrainzartistid", "musicbrainzalbumartistid"),
    "musicbrainzalbumid": ("musicBrainzAlbumID",),
    "musicbrainzreleasegroupid": ("musicBrainzReleaseGroupID",),
    "musicBrainzTrackID": ("musicbrainztrackid",),
}


#: ``(logical tag key, entity, metadata column)``. :func:`tag_values` loops this
#: and ``deps.build_tag_map`` publishes it, so a tag added to one is a row in the
#: other. ``entity`` is ``"artist"`` / ``"album"`` / ``"track"`` — the side table
#: the value is read from, which is the honest answer to "resolved from". *Which
#: rung* wrote that column is a per-release fact (``mb_match_method``), not a
#: per-field constant, and inventing one would be a table that lies the moment
#: the ladder is reordered.
ENRICHMENT_TAGS: tuple[tuple[str, str, str], ...] = (
    ("musicbrainz_albumartistid", "artist", "mb_artist_mbid"),
    ("isni", "artist", "isni"),
    ("musicbrainz_albumid", "album", "mb_release_mbid"),
    ("musicbrainz_releasegroupid", "album", "mb_release_group_mbid"),
    ("catalognumber", "album", "catalog_number"),
    ("releasecountry", "album", "country"),
    ("releasestatus", "album", "status"),
    ("releasetype", "album", "primary_type"),
    ("barcode", "album", "barcode"),
    # The release group's first release date, which for a reissue is years
    # earlier than the release date — that is the whole point of the tag, and is
    # also why it is never written to ``Album.release_date``, where it would
    # rewrite folder names.
    ("originaldate", "album", "first_release_date"),
    ("musicbrainz_trackid", "track", "mb_recording_mbid"),
    ("musicbrainz_releasetrackid", "track", "mb_release_track_mbid"),
    ("acoustid_id", "track", "acoustid"),
    ("isrc", "track", "isrc"),
)

#: ``(logical tag key, the key it is computed from)`` — keys read from nowhere.
DERIVED_TAGS: tuple[tuple[str, str], ...] = (
    ("musicbrainz_artistid", "musicbrainz_albumartistid"),
)


class NfoLocked(Exception):
    """The existing file carries ``<lockdata>true</lockdata>``.

    That flag is how someone tells their media server to stop editing a file.
    Qobuzarr is not the server, and it is not an exception to the instruction.
    """


# ---------------------------------------------------------------------------
# Tags
# ---------------------------------------------------------------------------
def tag_values(
    *,
    artist_meta: ArtistMetadata | None = None,
    album_meta: AlbumMetadata | None = None,
    track_meta: TrackMetadata | None = None,
) -> dict[str, str]:
    """The enrichment tag values for one track, as plain strings.

    Fed to ``build_tags(..., extra=...)``. Everything is resolved here, on the
    event loop, precisely so the tagger — which runs in a worker thread — never
    has to touch a row it might lazy-load.

    Keys are the logical names :mod:`app.core.tagger` maps onto Vorbis fields and
    ID3 frames; empty values are dropped by the caller.
    """
    values: dict[str, str] = {}
    rows: dict[str, Any] = {
        "artist": artist_meta,
        "album": album_meta,
        "track": track_meta,
    }
    for key, entity, column in ENRICHMENT_TAGS:
        row = rows.get(entity)
        if row is not None:
            _put(values, key, getattr(row, column, None))

    # The performing artist is the album artist unless something says otherwise;
    # Qobuzarr has no per-track credit ids, so claiming one would be a fabrication.
    if "musicbrainz_albumartistid" in values:
        values.setdefault("musicbrainz_artistid", values["musicbrainz_albumartistid"])
    return values


def _put(target: dict[str, str], key: str, value: Any) -> None:
    """Set *key* when *value* has content."""
    text = str(value).strip() if value not in (None, "") else ""
    if text:
        target[key] = text


# ---------------------------------------------------------------------------
# NFO
# ---------------------------------------------------------------------------
def render_artist_nfo(artist: Any, meta: ArtistMetadata | None = None) -> str:
    """Kodi-style ``artist.nfo`` for one artist.

    ``<sortname>`` is the one element with two possible sources, and the typed
    one wins. ``Artist.sort_name`` is a person's answer and nothing re-derives
    it; ``ArtistMetadata.sort_name`` is MusicBrainz's and is rewritten on every
    enrichment pass. Preferring the derived value would mean a media server
    re-sorted the artist back the next night.
    """
    root = ET.Element("artist")
    _text_child(root, "name", _attr(artist, "name"))
    _text_child(root, "musicBrainzArtistID", _attr(meta, "mb_artist_mbid"))
    _text_child(root, "sortname", _attr(artist, "sort_name") or _attr(meta, "sort_name"))

    if meta is not None:
        _text_child(root, "isni", meta.isni)
        _text_child(root, "disambiguation", meta.disambiguation)
        _text_child(root, "type", meta.artist_type)
        _text_child(root, "gender", meta.gender)
        # Kodi reads <born> for people and <formed> for groups. Writing the one
        # that does not apply makes a person look like a band in the interface.
        born_tag = "formed" if (meta.artist_type or "").lower() != "person" else "born"
        _text_child(root, born_tag, meta.life_span_begin)
        _text_child(
            root,
            "disbanded" if born_tag == "formed" else "died",
            meta.life_span_end,
        )
        _text_child(root, "country", meta.country)
        _text_child(root, "area", meta.area)
        for genre in meta.genre_list:
            _text_child(root, "genre", genre)
        if meta.bio:
            biography = _text_child(root, "biography", meta.bio)
            if biography is not None and meta.bio_source_url:
                # CC BY-SA is share-alike: the attribution travels with the text
                # or the text should not be republished at all.
                biography.set("source", meta.bio_source_url)
                if meta.bio_licence:
                    biography.set("licence", meta.bio_licence)
        _text_child(root, "thumb", _attr(artist, "image_url") or meta.portrait_url)
        _text_child(root, "url", meta.official_homepage)
        _text_child(root, "url", meta.wikipedia_url)
    else:
        _text_child(root, "thumb", _attr(artist, "image_url"))

    return _serialise(root)


def render_album_nfo(
    album: Any,
    meta: AlbumMetadata | None = None,
    tracks: Sequence[Any] = (),
    track_meta: Mapping[str, TrackMetadata] | None = None,
    *,
    artist_name: str | None = None,
    artist_meta: ArtistMetadata | None = None,
) -> str:
    """Kodi-style ``album.nfo`` for one release, including its tracklist."""
    root = ET.Element("album")
    _text_child(root, "title", _attr(album, "title"))
    _text_child(root, "artistdesc", artist_name or _attr(_attr(album, "artist"), "name"))
    _text_child(root, "musicBrainzArtistID", _attr(artist_meta, "mb_artist_mbid"))

    year = _attr(album, "year")
    release_date = _attr(album, "release_date")
    _text_child(root, "year", year)
    _text_child(
        root,
        "releasedate",
        release_date.isoformat() if hasattr(release_date, "isoformat") else release_date,
    )
    _text_child(root, "label", _attr(album, "label"))
    _text_child(root, "type", _attr(album, "release_type"))
    _text_child(root, "thumb", _attr(album, "image_url"))

    if meta is not None:
        _text_child(root, "musicbrainzalbumid", meta.mb_release_mbid)
        _text_child(root, "musicbrainzreleasegroupid", meta.mb_release_group_mbid)
        _text_child(root, "barcode", meta.barcode)
        _text_child(root, "catalognumber", meta.catalog_number)
        _text_child(root, "releasecountry", meta.country)
        _text_child(root, "releasestatus", meta.status)
        _text_child(root, "media", meta.media_format)
        _text_child(root, "originaldate", meta.first_release_date)
        for genre in meta.genre_list:
            _text_child(root, "genre", genre)
        for style in parse_tags(meta.secondary_types):
            _text_child(root, "style", style)
        if not _attr(album, "image_url"):
            _text_child(root, "thumb", meta.cover_url)

    by_id = track_meta or {}
    for track in sorted(
        tracks, key=lambda t: (_attr(t, "media_number") or 1, _attr(t, "track_number") or 0)
    ):
        node = ET.SubElement(root, "track")
        _text_child(node, "position", _attr(track, "track_number"))
        _text_child(node, "title", _attr(track, "title"))
        _text_child(node, "duration", _duration(_attr(track, "duration")))
        found = by_id.get(str(_attr(track, "id")))
        if found is not None:
            _text_child(node, "musicBrainzTrackID", found.mb_recording_mbid)
            _text_child(node, "isrc", found.isrc or _attr(track, "isrc"))
        else:
            _text_child(node, "isrc", _attr(track, "isrc"))

    return _serialise(root)


# ---------------------------------------------------------------------------
# XML helpers
# ---------------------------------------------------------------------------
def _attr(obj: Any, name: str) -> Any:
    """Read *name* off a row or mapping, tolerating ``None``."""
    if obj is None:
        return None
    if isinstance(obj, Mapping):
        return obj.get(name)
    return getattr(obj, name, None)


def _text_child(parent: ET.Element, tag: str, value: Any) -> ET.Element | None:
    """Append ``<tag>value</tag>``, or nothing at all when *value* is empty.

    Empty elements are omitted rather than written blank: a media server reads
    ``<genre></genre>`` as a genre called nothing, which is worse than silence.
    """
    text = str(value).strip() if value not in (None, "") else ""
    if not text:
        return None
    child = ET.SubElement(parent, tag)
    child.text = text
    return child


def _duration(seconds: Any) -> str:
    """``mm:ss`` for a track length in seconds, or empty."""
    try:
        total = int(seconds)
    except (TypeError, ValueError):
        return ""
    if total <= 0:
        return ""
    return f"{total // 60}:{total % 60:02d}"


def _serialise(root: ET.Element) -> str:
    """Pretty-printed UTF-8 XML with a declaration, ending in a newline."""
    ET.indent(root, space="  ")
    body = ET.tostring(root, encoding="unicode")
    return f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n{body}\n'


# ---------------------------------------------------------------------------
# Merging with what is already there
# ---------------------------------------------------------------------------
def merge_nfo(existing: str | None, generated: str) -> str:
    """Fold *generated* into *existing*, keeping everything Qobuzarr did not write.

    A real library's NFO files are not blank. Jellyfin, Emby and Kodi write them,
    and they hold a biography from TheAudioDB, artwork paths, their own ids and a
    ``dateadded`` — none of which Qobuzarr knows, and all of which a wholesale
    rewrite would destroy. So this replaces only the elements the generated
    document actually contains and leaves the rest untouched, in place.

    Where the two spell the same fact differently — Kodi's ``musicBrainzArtistID``
    against Jellyfin's ``musicbrainzalbumartistid`` — the *existing* spelling
    wins, so the file stays internally consistent rather than ending up with two
    elements making the same claim.

    Args:
        existing: The file's current contents, or ``None`` when there is none.
        generated: What :func:`render_album_nfo` or :func:`render_artist_nfo`
            produced.

    Returns:
        The document to write.

    Raises:
        NfoLocked: the existing file says ``<lockdata>true</lockdata>``.
    """
    if not existing or not existing.strip():
        return generated

    try:
        target = ET.fromstring(existing)
    except ET.ParseError:
        # Unparseable: there is nothing to preserve, and leaving a broken file in
        # place helps nobody.
        return generated

    if (target.findtext("lockdata") or "").strip().lower() == "true":
        raise NfoLocked("the existing NFO is locked against edits")

    source = ET.fromstring(generated)
    if source.tag != target.tag:
        # An album.nfo where an artist.nfo belongs, or vice versa. Not something
        # to merge into.
        return generated

    for tag in _ordered_tags(source):
        values = source.findall(tag)
        name = _existing_name(target, tag)
        _replace_all(target, name, values)

    return _serialise(target)


def _ordered_tags(source: ET.Element) -> list[str]:
    """The element names *source* provides, in document order, without repeats."""
    seen: list[str] = []
    for child in source:
        if child.tag not in seen:
            seen.append(child.tag)
    return seen


def _existing_name(target: ET.Element, tag: str) -> str:
    """The name *target* already uses for *tag*, falling back to *tag* itself."""
    if target.find(tag) is not None:
        return tag
    for alias in _ALIASES.get(tag, ()):
        if target.find(alias) is not None:
            return alias
    return tag


def _replace_all(
    target: ET.Element, tag: str, values: Sequence[ET.Element]
) -> None:
    """Swap every ``<tag>`` in *target* for *values*, keeping the first position.

    Position is preserved so a merged file does not reshuffle itself on every
    write — which would make it look changed to anything watching the directory.
    """
    existing = target.findall(tag)
    index = list(target).index(existing[0]) if existing else len(list(target))
    for node in existing:
        target.remove(node)
    for offset, value in enumerate(values):
        clone = ET.Element(tag, dict(value.attrib))
        clone.text = value.text
        clone.extend(list(value))
        target.insert(index + offset, clone)
