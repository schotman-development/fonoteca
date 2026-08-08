"""Pure translation of raw Qobuz JSON into model-ready dictionaries.

Nothing in this module performs I/O, touches the database or reads settings, so
it is trivially unit-testable — which matters, because Qobuz's payloads are
wildly inconsistent between endpoints:

* ``artist/get?extra=albums`` returns "classic" album objects with
  ``release_date_original``, ``maximum_bit_depth`` and ``hires``.
* ``artist/getReleasesList`` returns the newer shape with ``dates.original``,
  ``audio_info.maximum_bit_depth`` and ``rights.hires_streamable``.
* ``catalog/search`` mixes both, and omits fields at random.

Every accessor therefore tolerates a missing key, a ``None`` value, a scalar
where a nested object was expected (``genre`` is sometimes a string, sometimes
``{"name": ...}``) and vice versa.

**Album ids are strings.** Real ids look like ``"uyej1o165e870"`` and
``"0884977859300"`` — the latter would lose its leading zero if it were ever
coerced to ``int``. Artist and track ids are numeric today but are stringified
here too, matching the ORM.

The dicts returned by :func:`map_album`, :func:`map_track` and
:func:`map_artist` contain exactly the mutable column names of
:class:`app.models.Album` / :class:`~app.models.Track` /
:class:`~app.models.Artist`, so they can be splatted straight into the model
constructor or fed to an upsert helper.
"""

from __future__ import annotations

from datetime import date, datetime, timezone
from typing import Any, Iterable, Mapping

from app.models import ReleaseType, normalize_release_type

__all__ = [
    "album_credit_names",
    "album_guest_appearance",
    "album_track_items",
    "classify_release_type",
    "extract_album_artist",
    "is_streamable",
    "map_album",
    "map_artist",
    "map_track",
    "parse_release_date",
    "pick_image_url",
]

#: Image variants, best first. Album payloads carry a subset of these.
_IMAGE_PREFERENCE: tuple[str, ...] = (
    "mega",
    "extralarge",
    "large",
    "medium",
    "small",
    "thumbnail",
)

#: Date-ish fields on album payloads, in the order we trust them.
_DATE_FIELDS: tuple[str, ...] = (
    "release_date_original",
    "release_date_stream",
    "release_date_download",
    "release_date",
    "released_at",
)

#: Same idea, but for the nested ``dates`` object of ``artist/getReleasesList``.
_NESTED_DATE_KEYS: tuple[str, ...] = ("original", "stream", "download")

#: A digit string this long is a unix timestamp, not a ``YYYYMMDD``-ish value.
_MIN_TIMESTAMP_DIGITS = 9


# ---------------------------------------------------------------------------
# Low-level coercion helpers
# ---------------------------------------------------------------------------
def _mapping(value: Any) -> Mapping[str, Any]:
    """Return *value* when it is a mapping, otherwise an empty mapping."""
    return value if isinstance(value, Mapping) else {}


def _text(value: Any) -> str | None:
    """Coerce *value* to a non-empty stripped string, else ``None``."""
    if value is None or isinstance(value, (dict, list, tuple, set, bool)):
        return None
    text = str(value).strip()
    return text or None


def _identifier(value: Any) -> str | None:
    """Coerce an id to a string without ever going through ``int``."""
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, float):
        # Guard against a JSON parser handing back 1.234e12 for a numeric id.
        value = int(value)
    text = str(value).strip()
    if not text or text.lower() in {"none", "null"}:
        return None
    return text


def _int(value: Any, default: int | None = None) -> int | None:
    """Best-effort integer conversion that never raises."""
    if value is None or isinstance(value, bool):
        return default
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        try:
            return int(float(str(value).strip()))
        except (TypeError, ValueError):
            return default


def _float(value: Any, default: float | None = None) -> float | None:
    """Best-effort float conversion that never raises."""
    if value is None or isinstance(value, bool):
        return default
    try:
        return float(str(value).strip())
    except (TypeError, ValueError):
        return default


def _bool(value: Any, default: bool = False) -> bool:
    """Interpret Qobuz's assorted truthy spellings."""
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    text = str(value).strip().lower()
    if text in {"true", "1", "yes", "y"}:
        return True
    if text in {"false", "0", "no", "n", ""}:
        return False
    return default


def _named(value: Any) -> str | None:
    """Read a ``{"name": ...}`` object, a bare string, or the first list entry.

    Qobuz uses all three spellings for ``label``, ``genre``, ``performer`` and
    ``composer`` depending on endpoint and release age.
    """
    if isinstance(value, Mapping):
        return _text(value.get("name")) or _text(value.get("title"))
    if isinstance(value, (list, tuple)):
        for entry in value:
            name = _named(entry)
            if name:
                return name
        return None
    return _text(value)


def _first(source: Mapping[str, Any], *keys: str) -> Any:
    """Return the first non-``None`` value among *keys*."""
    for key in keys:
        if key in source and source[key] is not None:
            return source[key]
    return None


def pick_image_url(value: Any) -> str | None:
    """Pick the largest available image URL from a Qobuz ``image`` field.

    Accepts a plain URL string, the classic ``{"small","thumbnail","large"}``
    object, the artist ``{"small","medium","large","extralarge","mega"}``
    object, or the newer ``{"portrait": {...}}`` wrapper.
    """
    if isinstance(value, str):
        return _text(value)
    if not isinstance(value, Mapping):
        return None
    for key in _IMAGE_PREFERENCE:
        url = _text(value.get(key))
        if url:
            return url
    # Newer payloads nest the real object one level down.
    for nested_key in ("portrait", "square", "image", "cover"):
        nested = value.get(nested_key)
        if isinstance(nested, (Mapping, str)):
            url = pick_image_url(nested)
            if url:
                return url
    return None


def _parse_date_value(value: Any) -> date | None:
    """Parse one date-ish value: unix seconds, ``YYYY-MM-DD``, or ISO datetime."""
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value
    if isinstance(value, (int, float)):
        return _from_timestamp(value)

    text = str(value).strip()
    if not text:
        return None
    if text.lstrip("-").isdigit():
        if len(text.lstrip("-")) >= _MIN_TIMESTAMP_DIGITS:
            return _from_timestamp(int(text))
        return None
    try:
        return date.fromisoformat(text[:10])
    except ValueError:
        pass
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00")).date()
    except ValueError:
        return None


def _from_timestamp(value: int | float) -> date | None:
    """Convert unix seconds to a UTC date, tolerating out-of-range values."""
    try:
        return datetime.fromtimestamp(float(value), tz=timezone.utc).date()
    except (OverflowError, OSError, ValueError):
        return None


# ---------------------------------------------------------------------------
# Public mappers
# ---------------------------------------------------------------------------
def parse_release_date(raw: Mapping[str, Any] | None) -> date | None:
    """Extract the release date from any album-shaped payload.

    Checks the flat ``release_date_*`` / ``released_at`` fields first, then the
    ``dates`` object used by ``artist/getReleasesList``. Returns ``None`` when
    nothing parses rather than guessing.
    """
    raw = _mapping(raw)

    for field in _DATE_FIELDS:
        parsed = _parse_date_value(raw.get(field))
        if parsed is not None:
            return parsed

    dates = _mapping(raw.get("dates"))
    for key in _NESTED_DATE_KEYS:
        parsed = _parse_date_value(dates.get(key))
        if parsed is not None:
            return parsed

    return None


def classify_release_type(raw: Mapping[str, Any] | None) -> str:
    """Return the normalised release type for an album payload.

    Qobuz collapses EPs and singles into a single ``epSingle`` bucket, so when
    that is what we get the track count decides: three tracks or fewer is a
    single, anything longer is an EP.

    When the payload carries no type at all (common in ``catalog/search``
    results) the track count is used to guess: ``<=3`` single, ``<=6`` EP,
    otherwise album. A payload with an unrecognised explicit type maps to
    ``"other"`` and is never guessed at.
    """
    raw = _mapping(raw)
    tracks_count = _int(_first(raw, "tracks_count", "track_count"), 0) or 0

    raw_type = _text(_first(raw, "release_type", "releaseType", "product_type"))
    if raw_type is None:
        # No declared type: infer from length.
        if 0 < tracks_count <= 3:
            return ReleaseType.SINGLE.value
        if 3 < tracks_count <= 6:
            return ReleaseType.EP.value
        return ReleaseType.ALBUM.value

    normalized = normalize_release_type(raw_type)
    if normalized == ReleaseType.EP.value and raw_type.strip().lower() != "ep":
        # "epSingle" — split the bucket on length.
        if 0 < tracks_count <= 3:
            return ReleaseType.SINGLE.value
        return ReleaseType.EP.value
    return normalized


def is_streamable(raw: Mapping[str, Any] | None) -> bool:
    """True when Qobuz says this album/track can be streamed by anyone.

    Reads the flat ``streamable`` flag and the newer ``rights.streamable``.
    Defaults to ``True`` when the payload says nothing — the account's own
    entitlements are the real gate, and ``track/getFileUrl`` is authoritative.
    """
    raw = _mapping(raw)
    rights = _mapping(raw.get("rights"))
    for candidate in (raw.get("streamable"), rights.get("streamable")):
        if candidate is not None:
            return _bool(candidate, True)
    return True


def map_artist(raw: Mapping[str, Any] | None) -> dict[str, Any]:
    """Map a Qobuz artist object onto :class:`app.models.Artist` columns.

    Returns keys ``id``, ``name``, ``image_url``, ``albums_count`` and
    ``qobuz_slug``. ``id`` is ``None`` when the payload has no usable id, which
    the caller must treat as "unusable record" rather than inserting it.
    """
    raw = _mapping(raw)
    albums = _mapping(raw.get("albums"))

    return {
        "id": _identifier(_first(raw, "id", "artist_id", "qobuz_id")),
        "name": _text(_first(raw, "name", "title")) or "Unknown Artist",
        "image_url": pick_image_url(_first(raw, "image", "images", "picture")),
        "albums_count": _int(
            _first(raw, "albums_count", "albums_as_primary_artist_count"),
            _int(albums.get("total"), 0),
        )
        or 0,
        "qobuz_slug": _text(_first(raw, "slug", "url_slug")),
    }


def extract_album_artist(raw: Mapping[str, Any] | None) -> dict[str, Any] | None:
    """Pull the primary artist out of an album payload, mapped like an artist.

    Useful when an album arrives from search or from ``album/get`` and the
    owning artist is not yet in the database. Returns ``None`` when the album
    carries no identifiable artist.
    """
    raw = _mapping(raw)
    candidate = raw.get("artist")
    if not isinstance(candidate, Mapping):
        artists = raw.get("artists")
        if isinstance(artists, (list, tuple)):
            for entry in artists:
                if isinstance(entry, Mapping) and _identifier(entry.get("id")):
                    candidate = entry
                    break
    if not isinstance(candidate, Mapping):
        composer = raw.get("composer")
        candidate = composer if isinstance(composer, Mapping) else None
    if not isinstance(candidate, Mapping):
        return None

    mapped = map_artist(candidate)
    return mapped if mapped["id"] else None


def album_guest_appearance(
    raw: Mapping[str, Any] | None, artist_id: str | None
) -> bool | None:
    """Whether *artist_id* merely guests on this release.

    ``True`` only when the id is in the payload's ``artists`` array carrying
    ``featured-artist`` and **not** ``main-artist``. Feeds
    :attr:`app.models.Album.guest_appearance`; read that docstring for what the
    three values mean and why the third exists.

    The phrasing is the whole function. "Lacks ``main-artist``" is the reading
    to avoid: ``artists`` lists *performers*, so a composer is routinely absent
    from their own release — Samuel Barber's id appears on 13 of his 169 — and
    that reading would call the other 156 guest appearances and demote them.
    Absent therefore answers ``False``: nothing said this artist is a guest.

    ``None`` — *no payload has said* — is reserved for a payload that carries no
    usable ``artists`` array at all, which is the older
    ``artist/get?extra=albums`` shape :meth:`QobuzClient.iter_artist_albums`
    falls back to. A co-credited ``main-artist`` (a genuine collaboration, two
    names on the sleeve) is ``False``: it is that artist's release too.
    """
    owner = _identifier(artist_id)
    if owner is None:
        return None
    entries = _mapping(raw).get("artists")
    if not isinstance(entries, (list, tuple)) or not entries:
        return None
    guest = False
    seen = False
    for entry in entries:
        if not isinstance(entry, Mapping):
            continue
        if _identifier(entry.get("id")) != owner:
            continue
        seen = True
        roles = entry.get("roles")
        roles = {str(r).strip().lower() for r in roles} if isinstance(roles, (list, tuple)) else set()
        if "main-artist" in roles:
            return False
        if "featured-artist" in roles:
            guest = True
    if not seen:
        # Named nowhere in the performer credits — a composer, a conductor, an
        # orchestra. Not a guest; not our business to demote.
        return False
    return guest


def album_credit_names(
    raw: Mapping[str, Any] | None, artist_name: str | None
) -> list[str]:
    """Qualified credit names on this release that extend *artist_name*.

    Qobuz writes each track's credits as ``"Boaz, Vocals, MainArtist - Maurice
    van Hoek, Lyricist - Boaz Roelevink, Lyricist"``: segments split on ``" - "``,
    each ``name, role, role``. This returns the distinct names that *begin with*
    the artist's name and are longer than it — ``Boaz Roelevink``, never the
    bare ``Boaz`` that every release under the id carries and which therefore
    distinguishes nothing.

    Pure, and it **selects nothing**. The list is shown to a person, who picks;
    what they pick decides ``wanted`` vs ``skipped`` and reaches no file, no NFO
    and no identifier. Nothing here may be fed to :mod:`app.enrich.matching`.

    Returns ``[]`` when the payload carries no credits or none qualify — a real
    answer about a release, distinct from never having looked. See
    :attr:`app.models.Album.credit_names`.
    """
    base = " ".join(str(artist_name or "").split()).casefold()
    if not base:
        return []
    out: list[str] = []
    for track in album_track_items(_mapping(raw)):
        performers = track.get("performers")
        if not isinstance(performers, str):
            continue
        for segment in performers.split(" - "):
            name = " ".join(segment.split(",")[0].split())
            folded = name.casefold()
            # Extends the artist's name on a word boundary: "Boaz Roelevink"
            # qualifies, "Boazts and Hammock" does not.
            if len(folded) > len(base) and folded.startswith(base + " ") and name not in out:
                out.append(name)
    return out


def map_album(raw: Mapping[str, Any] | None, artist_id: str | None = None) -> dict[str, Any]:
    """Map a Qobuz album object onto :class:`app.models.Album` columns.

    Args:
        raw: The album payload, from any endpoint.
        artist_id: Owning artist id to stamp on the row. When omitted it is
            taken from the payload's own ``artist``/``artists`` fields.

    Returns:
        A dict with exactly the writable ``Album`` column names. ``id`` is
        ``None`` for an unusable payload; ``artist_id`` may be ``None`` when
        the album has no discoverable artist and none was supplied.

    Notes:
        ``media_count`` falls back to the highest ``media_number`` seen in the
        embedded track list, because several endpoints omit the disc count on
        multi-disc releases.
    """
    raw = _mapping(raw)
    audio_info = _mapping(raw.get("audio_info"))
    rights = _mapping(raw.get("rights"))

    tracks = album_track_items(raw)
    tracks_count = _int(_first(raw, "tracks_count", "track_count"), None)
    if tracks_count is None:
        tracks_count = len(tracks)

    media_count = _int(_first(raw, "media_count", "medias_count", "discs_count"), None)
    if media_count is None and tracks:
        media_count = max((_int(t.get("media_number"), 1) or 1) for t in tracks)
    if not media_count or media_count < 1:
        media_count = 1

    owner = _identifier(artist_id)
    if owner is None:
        artist = extract_album_artist(raw)
        owner = artist["id"] if artist else None

    hires_raw = _first(raw, "hires", "hires_streamable")
    if hires_raw is None:
        hires_raw = rights.get("hires_streamable")
    hires = _bool(hires_raw, False)

    return {
        "id": _identifier(_first(raw, "id", "album_id", "qobuz_id")),
        "artist_id": owner,
        "title": _text(_first(raw, "title", "name")) or "Unknown Album",
        "version": _text(raw.get("version")),
        "release_date": parse_release_date(raw),
        "release_type": classify_release_type(raw),
        "tracks_count": max(0, tracks_count or 0),
        "media_count": media_count,
        "hires": hires,
        "max_bit_depth": _int(
            _first(raw, "maximum_bit_depth", "max_bit_depth"),
            _int(audio_info.get("maximum_bit_depth"), None),
        ),
        "max_sampling_rate": _float(
            _first(raw, "maximum_sampling_rate", "max_sampling_rate"),
            _float(audio_info.get("maximum_sampling_rate"), None),
        ),
        "label": _named(raw.get("label")),
        "genre": _named(_first(raw, "genre", "genres_list", "genres")),
        "upc": _text(raw.get("upc")),
        "image_url": pick_image_url(_first(raw, "image", "images", "cover")),
        "duration": _int(raw.get("duration"), None),
        # Not `credit_names`: that costs an `album/get` per release and is
        # populated on request, per artist. Leaving it out of this dict is what
        # keeps `_apply_metadata` from clearing it on every index tick.
        "guest_appearance": album_guest_appearance(raw, owner),
    }


def map_track(raw: Mapping[str, Any] | None, album_id: str | None = None) -> dict[str, Any]:
    """Map a Qobuz track object onto :class:`app.models.Track` columns.

    Args:
        raw: The track payload, from ``album/get`` or ``track/get``.
        album_id: Owning album id. When omitted it is read from the track's
            embedded ``album`` object (present on ``track/get`` responses).

    Returns:
        A dict with exactly the identity/metadata ``Track`` columns. Download
        state (``status``, ``path``, ``format_id``, …) is deliberately absent so
        re-mapping an existing row never clobbers progress.
    """
    raw = _mapping(raw)

    owner = _identifier(album_id)
    if owner is None:
        owner = _identifier(_mapping(raw.get("album")).get("id"))

    performer = _named(_first(raw, "performer", "artist"))
    if performer is None:
        performer = _named(_mapping(raw.get("album")).get("artist"))

    return {
        "id": _identifier(_first(raw, "id", "track_id", "qobuz_id")),
        "album_id": owner,
        "title": _text(_first(raw, "title", "name")) or "Unknown Track",
        "version": _text(raw.get("version")),
        "track_number": _int(_first(raw, "track_number", "trackNumber"), 0) or 0,
        "media_number": _int(_first(raw, "media_number", "mediaNumber"), 1) or 1,
        "duration": _int(raw.get("duration"), None),
        "isrc": _text(raw.get("isrc")),
        "performer": performer,
        "composer": _named(raw.get("composer")),
    }


def album_track_items(raw: Mapping[str, Any]) -> list[Mapping[str, Any]]:
    """Return the embedded track list of an album payload (possibly empty)."""
    tracks = raw.get("tracks")
    items: Iterable[Any]
    if isinstance(tracks, Mapping):
        items = tracks.get("items") or []
    elif isinstance(tracks, (list, tuple)):
        items = tracks
    else:
        items = []
    return [item for item in items if isinstance(item, Mapping)]
