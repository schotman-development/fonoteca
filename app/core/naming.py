"""Library path rendering: templates in, POSIX-safe paths out.

Everything in this module is a pure function of its arguments plus the
:class:`~app.config.Settings` it is handed, so it is trivially unit-testable and
never touches the filesystem or the database.

Template syntax
---------------
``Settings.naming_template`` describes the *whole* path of a track relative to
``Settings.library_path``.  The default is::

    {artist}/{album} ({year})[ [{quality}]]/{disc_prefix}{track:02d} - {title}.{ext}

* ``{...}`` is a placeholder.  A ``str.format`` spec may be appended
  (``{track:02d}``); it is ignored when the value cannot be formatted that way.
* ``[...]`` marks an **optional segment**: if any placeholder inside it resolves
  to an empty value the entire segment — brackets and literal text alike — is
  dropped.  Only the outermost pair is a marker; brackets inside a segment are
  literal, which is what makes ``[ [{quality}]]`` render as ``" [FLAC 24-96]"``
  or as nothing at all.
* Everything before the last ``/`` is the album directory; the final piece is
  the file name.  ``{disc_prefix}`` expands to ``"Disc 2/"`` (note the trailing
  slash, so it creates a sub-directory) but **only when the album really has
  more than one disc** — single-disc releases get no extra folder.

Supported placeholders
----------------------
``{artist}`` ``{albumartist}`` ``{album}`` ``{album_title}`` ``{version}``
``{year}`` ``{quality}`` ``{disc_prefix}`` ``{disc}`` ``{discs}`` ``{track}``
``{tracks}`` ``{title}`` ``{track_title}`` ``{ext}`` ``{label}`` ``{genre}``
``{format}`` ``{isrc}`` ``{upc}``

``{album}`` deliberately includes the Qobuz ``version`` suffix (``"All Melody
(Deluxe Edition)"``) so that two editions of the same record cannot collide in
one folder; ``{album_title}`` is the bare title for anyone who wants to build
the suffix themselves.  ``{title}`` behaves the same way for tracks.

Sanitising
----------
Every rendered path component is passed through :func:`sanitise_component`,
which is deliberately conservative: only ``/`` and NUL are actually illegal on
POSIX, but control characters, trailing dots and trailing spaces all cause real
pain with network shares, backups and other music players, so they go too.
"""

from __future__ import annotations

import re
import unicodedata
from pathlib import Path, PurePosixPath
from typing import Any, Mapping

from app.config import Settings, get_settings

__all__ = [
    "AUDIO_EXTENSIONS",
    "FORMAT_EXTENSIONS",
    "COVER_FILENAME",
    "MAX_COMPONENT_BYTES",
    "album_values",
    "clamp_to_format",
    "disc_prefix",
    "extension_for_format",
    "quality_tag",
    "render_album_dir",
    "render_template",
    "render_track_name",
    "render_track_path",
    "sanitise_component",
    "track_values",
]

#: File extensions Fonoteca may have written for a track, best first. Used when
#: looking for an already-downloaded file whose format is not yet known.
AUDIO_EXTENSIONS: tuple[str, ...] = ("flac", "mp3")

#: Cover art written next to the audio, the de-facto standard file name. Lives
#: here rather than in the downloader so the librarian can find it when
#: re-tagging or re-filing an album without importing the download loop.
COVER_FILENAME: str = "cover.jpg"

#: Qobuz ``format_id`` -> container extension.
FORMAT_EXTENSIONS: dict[int, str] = {5: "mp3", 6: "flac", 7: "flac", 27: "flac"}

#: Qobuz ``format_id`` -> the (bit depth, kHz) it can carry at most. Used to
#: keep an *estimated* quality tag honest: a 24/192 master delivered as
#: format 6 is still only 16/44.1 on disk.
FORMAT_CEILINGS: dict[int, tuple[int, float]] = {
    6: (16, 44.1),
    7: (24, 96.0),
    27: (24, 192.0),
}

#: Hard ceiling for one path component. ``NAME_MAX`` is 255 *bytes* on ext4 and
#: most other Linux filesystems, so long non-ASCII titles must be counted in
#: bytes rather than characters.
MAX_COMPONENT_BYTES: int = 255

#: Default character budget for a component when settings say nothing.
DEFAULT_COMPONENT_LENGTH: int = 120

#: Characters that must never appear in a POSIX path component.
_ILLEGAL_RE = re.compile(r"[/\x00]")

#: Control characters (including the newlines Qobuz occasionally leaves in
#: titles) and the Unicode line/paragraph separators.
_CONTROL_RE = re.compile("[\x00-\x1f\x7f\u2028\u2029]")

#: Any run of whitespace, collapsed to a single ordinary space.
_WHITESPACE_RE = re.compile(r"\s+")

#: An empty bracket pair left behind when an optional value was missing.
_EMPTY_GROUP_RE = re.compile(r"\(\s*\)|\[\s*\]|\{\s*\}|<\s*>")

#: Separator debris at the very start/end of a rendered component.
_EDGE_SEPARATOR_RE = re.compile(r"^[\s\-_,;:]+|[\s\-_,;:]+$")

#: A plausible file extension at the end of a name: ``.flac``, ``.mp3`` …
_EXTENSION_RE = re.compile(r"\.(?P<ext>[A-Za-z0-9]{1,8})$")

#: Names that are meaningless (or actively dangerous) as a directory entry.
_RESERVED_NAMES = {"", ".", ".."}

#: Fallbacks so a component is never empty.
_FALLBACK = "Unknown"

#: Trailing ``_600.jpg``-style size marker on a Qobuz cover URL.
_IMAGE_SIZE_RE = re.compile(r"_(?:\d+|max|org)(\.[A-Za-z0-9]+)$")

#: Placeholders allowed to introduce a directory boundary. Everything else has
#: its ``/`` characters replaced, so a track titled "Say Yes / Say No" stays one
#: file instead of quietly becoming a folder.
_PATH_PLACEHOLDERS = frozenset({"disc_prefix"})


# ---------------------------------------------------------------------------
# Small accessors that work on both ORM objects and plain mappings
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


def _settings(settings: Settings | None) -> Settings:
    """Return *settings* or the process-wide singleton."""
    return settings if settings is not None else get_settings()


# ---------------------------------------------------------------------------
# Quality / format helpers
# ---------------------------------------------------------------------------
def _khz(sampling_rate: float | int | None) -> float | None:
    """Normalise a sampling rate to kHz.

    Qobuz reports kHz (``96.0``, ``44.1``) on ``getFileUrl`` but Hz shows up in
    some payloads, so anything above 1000 is assumed to be Hz.
    """
    if sampling_rate is None:
        return None
    try:
        value = float(sampling_rate)
    except (TypeError, ValueError):
        return None
    if value <= 0:
        return None
    return value / 1000.0 if value > 1000 else value


def quality_tag(
    format_id: int | None = None,
    bit_depth: int | None = None,
    sampling_rate: float | None = None,
) -> str:
    """Return a short human quality label, e.g. ``"FLAC 24-96"`` or ``"MP3 320"``.

    Always call this with the values ``track/getFileUrl`` *returned*: Qobuz
    silently downgrades a request the subscription does not cover, so what was
    asked for is not what is on disk.

    Args:
        format_id: Qobuz format id (5/6/7/27), or ``None`` when unknown.
        bit_depth: Bit depth of the delivered file.
        sampling_rate: Sampling rate of the delivered file, in kHz (Hz is
            detected and converted).

    Returns:
        The label, or an empty string when nothing is known — which lets the
        surrounding ``[...]`` template segment disappear cleanly.
    """
    fid = None if format_id is None else _int(format_id, 0) or None

    if fid == 5:
        return "MP3 320"

    depth = _int(bit_depth, 0)
    rate = _khz(sampling_rate)

    if depth and rate:
        return f"FLAC {depth}-{rate:g}"
    if depth:
        return f"FLAC {depth}bit"
    if rate:
        return f"FLAC {rate:g}kHz"
    if fid in FORMAT_EXTENSIONS:
        # No audio details at all: fall back to the static label for the format.
        return "FLAC" if FORMAT_EXTENSIONS[fid] == "flac" else "MP3 320"
    return ""


def clamp_to_format(
    format_id: int | None,
    bit_depth: int | None,
    sampling_rate: float | None,
) -> tuple[int | None, float | None]:
    """Cap *bit_depth*/*sampling_rate* at what *format_id* can actually carry.

    The album's ``maximum_bit_depth`` describes the best master Qobuz has, not
    what the subscription will deliver. When the request is clamped down to
    FLAC 16/44.1 the folder name must say so, otherwise a lossless-only account
    ends up with directories labelled ``[FLAC 24-192]`` full of CD-quality
    files. Delivered values from ``getFileUrl`` are already consistent with
    their format, so this is a no-op for them.
    """
    ceiling = FORMAT_CEILINGS.get(_int(format_id, 0))
    if ceiling is None:
        return bit_depth, sampling_rate

    max_depth, max_rate = ceiling
    depth = _int(bit_depth, 0)
    rate = _khz(sampling_rate)
    return (
        min(depth, max_depth) if depth else bit_depth,
        min(rate, max_rate) if rate else sampling_rate,
    )


def extension_for_format(format_id: int | None, mime_type: str | None = None) -> str:
    """Return the file extension (no dot) for a Qobuz format id.

    Falls back to sniffing *mime_type*, then to ``"flac"`` — every Qobuz format
    except MP3 320 is FLAC.
    """
    fid = _int(format_id, 0)
    if fid in FORMAT_EXTENSIONS:
        return FORMAT_EXTENSIONS[fid]
    mime = (mime_type or "").lower()
    if "mpeg" in mime or "mp3" in mime:
        return "mp3"
    if "flac" in mime:
        return "flac"
    return "flac"


def upgrade_image_url(url: str | None) -> str | None:
    """Rewrite a Qobuz cover URL to request the largest available rendition.

    Cover URLs end in a size marker (``..._600.jpg``); ``_max`` yields the
    full-resolution image. Returns ``None`` when *url* is empty and the input
    unchanged when it carries no recognisable marker — callers should treat the
    result as a *hint* and fall back to the original URL if it 404s.
    """
    text = _text(url)
    if not text:
        return None
    return _IMAGE_SIZE_RE.sub(r"_max\1", text)


# ---------------------------------------------------------------------------
# Sanitising
# ---------------------------------------------------------------------------
def _truncate(text: str, max_chars: int, max_bytes: int = MAX_COMPONENT_BYTES) -> str:
    """Trim *text* to at most *max_chars* characters and *max_bytes* UTF-8 bytes."""
    if max_chars > 0:
        text = text[:max_chars]
    while text and len(text.encode("utf-8", "surrogatepass")) > max_bytes:
        text = text[:-1]
    return text


def sanitise_component(
    value: Any,
    *,
    max_length: int | None = None,
    replacement: str | None = None,
    keep_extension: bool = False,
    settings: Settings | None = None,
) -> str:
    """Turn arbitrary text into one safe POSIX path component.

    The transformation, in order: normalise to NFC, replace ``/`` and NUL with
    the replacement character, turn any other control character into a space,
    collapse whitespace, strip separator debris and trailing dots/spaces, then
    truncate.

    Args:
        value: Any object; it is stringified first.
        max_length: Character budget. Defaults to
            ``Settings.max_path_component_length``. The result is additionally
            capped at :data:`MAX_COMPONENT_BYTES` UTF-8 bytes.
        replacement: What to substitute for illegal characters. Defaults to
            ``Settings.path_replacement_char``.
        keep_extension: When true the trailing ``.ext`` is protected from
            truncation, so a very long title still yields a playable file.
        settings: Optional settings override (tests).

    Returns:
        A non-empty, safe component. Unicode is preserved — only genuinely
        problematic characters are touched.

    Examples:
        >>> sanitise_component("AC/DC")
        'AC_DC'
        >>> sanitise_component("Ænima ")
        'Ænima'
        >>> sanitise_component("...")
        'Unknown'
    """
    conf = _settings(settings)
    replacement = conf.path_replacement_char if replacement is None else replacement
    if max_length is None:
        max_length = conf.max_path_component_length or DEFAULT_COMPONENT_LENGTH

    text = _text(value)
    if not text:
        return _FALLBACK

    text = unicodedata.normalize("NFC", text)
    text = _ILLEGAL_RE.sub(replacement, text)
    # Tabs and newlines are whitespace, not garbage: fold them into spaces so a
    # two-line title reads as one line rather than as "Two_Lines".
    text = _CONTROL_RE.sub(" ", text)
    text = _WHITESPACE_RE.sub(" ", text)
    text = _EDGE_SEPARATOR_RE.sub("", text)
    # Leading dots hide the entry on POSIX; trailing dots upset SMB/NTFS shares.
    text = text.strip(" .")

    if not text or text in _RESERVED_NAMES:
        return _FALLBACK

    extension = ""
    if keep_extension:
        match = _EXTENSION_RE.search(text)
        if match:
            extension = match.group(0)
            text = text[: match.start()]
            if not text:
                text = _FALLBACK

    budget = max(1, max_length - len(extension))
    byte_budget = max(1, MAX_COMPONENT_BYTES - len(extension.encode("utf-8")))
    text = _truncate(text, budget, byte_budget)
    text = text.rstrip(" .") or _FALLBACK

    return f"{text}{extension}"


# ---------------------------------------------------------------------------
# Template rendering
# ---------------------------------------------------------------------------
class _RenderFlags:
    """Tracks whether a template fragment had placeholders and whether any was empty."""

    __slots__ = ("placeholders", "empty")

    def __init__(self) -> None:
        self.placeholders = False
        self.empty = False


def _match_bracket(text: str, start: int) -> int:
    """Index of the ``]`` closing the ``[`` at *start*, or ``-1`` if unbalanced."""
    depth = 0
    for index in range(start, len(text)):
        char = text[index]
        if char == "[":
            depth += 1
        elif char == "]":
            depth -= 1
            if depth == 0:
                return index
    return -1


def _substitute(
    token: str,
    values: Mapping[str, Any],
    flags: _RenderFlags,
    replacement: str,
) -> str:
    """Render one ``{name[:spec]}`` token, recording whether it was empty.

    A ``/`` inside a *value* is replaced: only the template itself (and the
    generated ``{disc_prefix}``) may introduce a directory boundary, otherwise a
    track called ``"Say Yes / Say No"`` would silently become a folder.
    """
    name, _, spec = token.partition(":")
    name = name.strip()
    # Strip a `!r`-style conversion; the values here are already display strings.
    name = name.split("!", 1)[0].strip()

    if name not in values:
        # Unknown placeholder: render nothing rather than exploding at runtime.
        flags.placeholders = True
        flags.empty = True
        return ""

    flags.placeholders = True
    value = values[name]

    if value is None or (isinstance(value, str) and not value.strip()):
        flags.empty = True
        return ""

    if spec:
        try:
            rendered = format(value, spec)
        except (TypeError, ValueError):
            rendered = str(value)
    else:
        rendered = str(value)

    if name in _PATH_PLACEHOLDERS:
        return rendered
    return rendered.replace("/", replacement)


def _render(
    text: str,
    values: Mapping[str, Any],
    flags: _RenderFlags,
    replacement: str,
    *,
    allow_optional: bool = True,
) -> str:
    """Render *text*, resolving optional ``[...]`` segments and placeholders.

    Only the outermost bracket pair is a marker: brackets *inside* an optional
    segment are literal text, which is what makes the default template's
    ``[ [{quality}]]`` render as ``" [FLAC 24-96]"`` (a literal bracketed tag
    that disappears entirely when the quality is unknown).
    """
    out: list[str] = []
    index = 0
    length = len(text)

    while index < length:
        char = text[index]
        if char == "[" and allow_optional:
            close = _match_bracket(text, index)
            if close == -1:
                out.append(char)
                index += 1
                continue
            inner_flags = _RenderFlags()
            inner = _render(
                text[index + 1 : close],
                values,
                inner_flags,
                replacement,
                allow_optional=False,
            )
            # Drop the whole segment when a placeholder inside had no value.
            if not (inner_flags.placeholders and inner_flags.empty):
                out.append(inner)
            index = close + 1
        elif char == "{":
            close = text.find("}", index)
            if close == -1:
                out.append(char)
                index += 1
                continue
            out.append(_substitute(text[index + 1 : close], values, flags, replacement))
            index = close + 1
        else:
            out.append(char)
            index += 1

    return "".join(out)


def render_template(
    template: str,
    values: Mapping[str, Any],
    *,
    replacement: str = "_",
) -> str:
    """Render a naming template into a raw (unsanitised) string.

    Optional ``[...]`` segments are resolved, placeholders substituted, then any
    bracket pair left empty by a missing value (``"Album ()"``) is removed and
    whitespace collapsed.

    The result may still contain ``/`` separators — but only ones the *template*
    asked for, because a ``/`` inside a value is rewritten to *replacement*
    first. Splitting the result and running each piece through
    :func:`sanitise_component` is still the caller's job.
    """
    rendered = _render(template, values, _RenderFlags(), replacement)

    previous = None
    while previous != rendered:
        previous = rendered
        rendered = _EMPTY_GROUP_RE.sub("", rendered)

    rendered = _WHITESPACE_RE.sub(" ", rendered)
    # Tidy each path component individually so " / " cannot survive.
    parts = [part.strip() for part in rendered.split("/")]
    return "/".join(parts)


def _sanitise_path(rendered: str, settings: Settings, *, is_file: bool) -> list[str]:
    """Split a rendered template on ``/`` and sanitise every component."""
    raw_parts = [part for part in rendered.split("/") if part.strip()]
    if not raw_parts:
        return [_FALLBACK]

    parts: list[str] = []
    last = len(raw_parts) - 1
    for position, part in enumerate(raw_parts):
        parts.append(
            sanitise_component(
                part,
                keep_extension=is_file and position == last,
                settings=settings,
            )
        )
    return parts


# ---------------------------------------------------------------------------
# Value dictionaries
# ---------------------------------------------------------------------------
def disc_prefix(album: Any, media_number: int | None = None) -> str:
    """Return ``"Disc N/"`` for multi-disc releases, otherwise ``""``.

    The trailing slash is intentional: it turns into a sub-directory when the
    rendered file name is split on ``/``. Single-disc albums (``media_count``
    of 0 or 1) never get a folder, per the library layout rules.
    """
    if _int(_get(album, "media_count", 1), 1) <= 1:
        return ""
    return f"Disc {max(1, _int(media_number, 1))}/"


def album_values(
    artist: Any,
    album: Any,
    *,
    format_id: int | None = None,
    bit_depth: int | None = None,
    sampling_rate: float | None = None,
) -> dict[str, Any]:
    """Build the album-level half of the template value dictionary."""
    title = _text(_get(album, "title")) or "Unknown Album"
    version = _text(_get(album, "version"))
    display = f"{title} ({version})" if version else title

    if isinstance(artist, str):
        artist_name = _text(artist)
    else:
        artist_name = _text(_get(artist, "name"))
    if not artist_name:
        # Fall back to the album's own artist relationship, then to a constant.
        artist_name = _text(_get(_get(album, "artist"), "name")) or "Unknown Artist"

    release_date = _get(album, "release_date")
    year = _get(album, "year")
    if not year and release_date is not None:
        year = getattr(release_date, "year", None)

    if bit_depth is None:
        bit_depth = _get(album, "max_bit_depth")
    if sampling_rate is None:
        sampling_rate = _get(album, "max_sampling_rate")

    bit_depth, sampling_rate = clamp_to_format(format_id, bit_depth, sampling_rate)
    quality = quality_tag(format_id, bit_depth, sampling_rate)

    return {
        "artist": artist_name,
        "albumartist": artist_name,
        "album": display,
        "album_title": title,
        "version": version,
        "year": _int(year, 0) or "",
        "quality": quality,
        "format": quality,
        "label": _text(_get(album, "label")),
        "genre": _text(_get(album, "genre")),
        "upc": _text(_get(album, "upc")),
        "discs": _int(_get(album, "media_count", 1), 1),
        "tracks": _int(_get(album, "tracks_count", 0), 0),
        # Album directories never carry disc/track information.
        "disc_prefix": "",
        "disc": "",
        "track": "",
        "title": display,
        "track_title": "",
        "ext": "",
        "isrc": "",
    }


def track_values(
    track: Any,
    album: Any,
    artist: Any = None,
    *,
    format_id: int | None = None,
    bit_depth: int | None = None,
    sampling_rate: float | None = None,
    ext: str | None = None,
) -> dict[str, Any]:
    """Build the full template value dictionary for one track."""
    values = album_values(
        artist if artist is not None else _get(album, "artist"),
        album,
        format_id=format_id,
        bit_depth=bit_depth,
        sampling_rate=sampling_rate,
    )

    title = _text(_get(track, "title")) or "Unknown Track"
    version = _text(_get(track, "version"))
    display = f"{title} ({version})" if version else title

    media_number = _int(_get(track, "media_number", 1), 1) or 1
    values.update(
        {
            "title": display,
            "track_title": title,
            "track": _int(_get(track, "track_number", 0), 0),
            "disc": media_number,
            "disc_prefix": disc_prefix(album, media_number),
            "isrc": _text(_get(track, "isrc")),
            "ext": _text(ext) or extension_for_format(format_id),
        }
    )
    return values


# ---------------------------------------------------------------------------
# Public renderers
# ---------------------------------------------------------------------------
def _split_template(template: str) -> tuple[list[str], str]:
    """Split a naming template into its directory segments and file segment."""
    parts = [part for part in template.split("/") if part.strip()]
    if not parts:
        return [], "{track:02d} - {title}.{ext}"
    return parts[:-1], parts[-1]


def render_album_dir(
    artist: Any,
    album: Any,
    settings: Settings | None = None,
    *,
    format_id: int | None = None,
    bit_depth: int | None = None,
    sampling_rate: float | None = None,
    root: Path | None = None,
) -> Path:
    """Return the absolute directory an album's files belong in.

    Only the directory portion of ``Settings.naming_template`` (everything
    before the final ``/``) is rendered, so the default template yields
    ``{library}/Nils Frahm/All Melody (2018) [FLAC 24-96]``.

    Args:
        artist: An :class:`~app.models.Artist` row, a mapping, or a plain name.
        album: An :class:`~app.models.Album` row or mapping.
        settings: Optional settings override.
        format_id: Delivered format id, used for the ``{quality}`` tag.
        bit_depth: Delivered bit depth; defaults to the album's maximum.
        sampling_rate: Delivered sampling rate; defaults to the album's maximum.
        root: Library root override; defaults to ``Settings.library_path``.

    Returns:
        An absolute :class:`~pathlib.Path`. Nothing is created on disk.
    """
    conf = _settings(settings)
    dir_segments, _ = _split_template(conf.naming_template)
    values = album_values(
        artist,
        album,
        format_id=format_id,
        bit_depth=bit_depth,
        sampling_rate=sampling_rate,
    )

    base = Path(root) if root is not None else Path(conf.library_path)
    if not dir_segments:
        return base

    rendered = render_template(
        "/".join(dir_segments), values, replacement=conf.path_replacement_char
    )
    return base.joinpath(*_sanitise_path(rendered, conf, is_file=False))


def render_track_name(
    track: Any,
    album: Any,
    settings: Settings | None = None,
    *,
    artist: Any = None,
    format_id: int | None = None,
    bit_depth: int | None = None,
    sampling_rate: float | None = None,
    ext: str | None = None,
) -> PurePosixPath:
    """Return a track's path **relative to its album directory**.

    For a single-disc album that is just the file name
    (``"03 - Sunson.flac"``); for a multi-disc album ``{disc_prefix}`` adds the
    leading folder (``"Disc 2/03 - Sunson.flac"``).

    Args:
        track: A :class:`~app.models.Track` row or mapping.
        album: The owning :class:`~app.models.Album` row or mapping.
        settings: Optional settings override.
        artist: Artist row/name; taken from ``album.artist`` when omitted.
        format_id: Delivered format id — trust ``getFileUrl``, not the request.
        bit_depth: Delivered bit depth.
        sampling_rate: Delivered sampling rate.
        ext: Extension override; derived from *format_id* when omitted.

    Returns:
        A relative :class:`~pathlib.PurePosixPath` with every component
        sanitised and the extension preserved through truncation.
    """
    conf = _settings(settings)
    _, file_segment = _split_template(conf.naming_template)
    values = track_values(
        track,
        album,
        artist,
        format_id=format_id,
        bit_depth=bit_depth,
        sampling_rate=sampling_rate,
        ext=ext,
    )

    rendered = render_template(
        file_segment, values, replacement=conf.path_replacement_char
    )
    return PurePosixPath(*_sanitise_path(rendered, conf, is_file=True))


def render_track_path(
    track: Any,
    album: Any,
    settings: Settings | None = None,
    *,
    artist: Any = None,
    album_dir: Path | None = None,
    format_id: int | None = None,
    bit_depth: int | None = None,
    sampling_rate: float | None = None,
    ext: str | None = None,
    root: Path | None = None,
) -> Path:
    """Absolute path of one track: :func:`render_album_dir` / :func:`render_track_name`.

    Pass *album_dir* when it has already been computed (the downloader does,
    because the album folder is decided once and then reused for every track).
    """
    conf = _settings(settings)
    if album_dir is None:
        album_dir = render_album_dir(
            artist if artist is not None else _get(album, "artist"),
            album,
            conf,
            format_id=format_id,
            bit_depth=bit_depth,
            sampling_rate=sampling_rate,
            root=root,
        )
    relative = render_track_name(
        track,
        album,
        conf,
        artist=artist,
        format_id=format_id,
        bit_depth=bit_depth,
        sampling_rate=sampling_rate,
        ext=ext,
    )
    return Path(album_dir).joinpath(*relative.parts)
