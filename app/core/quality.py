"""Comparing what is already on disk with what Qobuz would deliver today.

Pure functions, no I/O, no database. Two very different callers need the same
answer to one question — *is there a better copy of this release available than
the one we already have?* — so the arithmetic lives in one place:

* :mod:`app.core.downloader` asks it per track, to decide whether a file that is
  already on disk may be reused or has to be fetched again;
* :mod:`app.api.deps` asks it per album, to decide whether the artist page shows
  an *Upgrade* button, a *Download* button, or no button at all.

Both must agree. If the table offered an upgrade the download loop would then
decline to perform, the button would do nothing; if they disagreed the other
way, every re-queue would re-fetch an album that was already as good as it gets.

Everything is expressed as a Qobuz ``format_id`` because that is the only
quality knob the API takes, and the ids sort by quality::

    5  MP3 320        <  6  FLAC 16/44.1  <  7  FLAC 24/<=96  <  27  FLAC 24/<=192

Three separate ceilings decide what a fresh download actually lands, and the
lowest of them wins:

* the **account** — ``QobuzClient.allowed_format_ids()``. ``getFileUrl``
  silently downgrades a request the subscription does not cover.
* the **profile** — the artist's ``quality_profile``, else
  ``Settings.default_quality_profile``.
* the **release** — a CD master is 16/44.1 however politely you ask for hi-res.

The first two are folded together by ``QobuzClient.best_format_id()`` and arrive
here as *target*; :func:`obtainable_format_id` applies the third. Comparing that
with :func:`owned_format_id` is the whole feature.

**Unknown means no.** Every function returns ``None`` rather than a guess when
the metadata does not support one, and callers treat ``None`` as "leave it
alone": the download loop keeps the file it has, and the UI shows no button.
Guessing the other way costs a redundant album download, which is exactly what
this project exists to avoid.
"""

from __future__ import annotations

from typing import Any

from app.core import naming

__all__ = [
    "QUALITY_PROFILE_FORMATS",
    "format_for_profile",
    "format_for_quality",
    "format_label",
    "obtainable_format_id",
    "owned_format_id",
    "release_ceiling_format_id",
    "track_format_id",
    "upgrade_available",
]

#: Named quality profiles -> Qobuz ``format_id``. ``"default"`` defers to
#: ``Settings.default_format_id``; a bare number ("27") is also accepted.
QUALITY_PROFILE_FORMATS: dict[str, int] = {
    "mp3": 5,
    "mp3-320": 5,
    "lossy": 5,
    "cd": 6,
    "flac": 6,
    "lossless": 6,
    "16bit": 6,
    "hi-res": 7,
    "hires": 7,
    "24bit": 7,
    "hi-res-96": 7,
    "hi-res-192": 27,
    "max": 27,
    "best": 27,
}


def _int(value: Any) -> int | None:
    """Coerce to a positive int, or ``None`` when that is not possible."""
    try:
        number = int(value)
    except (TypeError, ValueError):
        return None
    return number if number > 0 else None


def _khz(value: Any) -> float | None:
    """Coerce a sampling rate to kHz. Values above 1000 are read as Hz."""
    try:
        rate = float(value)
    except (TypeError, ValueError):
        return None
    if rate <= 0:
        return None
    return rate / 1000.0 if rate > 1000 else rate


def format_for_profile(profile: str | None, default_format_id: int) -> int:
    """Translate a quality-profile name into a Qobuz ``format_id``.

    Unknown names fall back to *default_format_id* rather than raising: the
    profile is free text on the artist row, and a typo must not stop a download.
    """
    key = str(profile or "").strip().lower()
    if not key or key == "default":
        return int(default_format_id)
    if key.isdigit():
        candidate = int(key)
        if candidate in naming.FORMAT_EXTENSIONS:
            return candidate
    return QUALITY_PROFILE_FORMATS.get(key, int(default_format_id))


def format_for_quality(bit_depth: Any, sampling_rate: Any) -> int | None:
    """The smallest lossless format that can carry *bit_depth* / *sampling_rate*.

    The inverse of :data:`app.core.naming.FORMAT_CEILINGS`. Returns ``None`` when
    either half is missing — an MP3 has no bit depth, and neither does a FLAC
    whose tags mutagen could not read, and in both cases the honest answer is
    "unknown", not "16/44.1".
    """
    depth = _int(bit_depth)
    rate = _khz(sampling_rate)
    if depth is None or rate is None:
        return None
    if depth <= 16:
        return 6
    return 7 if rate <= 96.0 else 27


def format_label(format_id: int | None) -> str:
    """Short human label for a format id, e.g. ``"FLAC 24-96"``.

    Uses the same wording as the quality tag written into folder names, so the
    button tooltip and the directory on disk say the same thing.
    """
    fid = _int(format_id)
    if fid is None:
        return ""
    if fid == 5:
        return "MP3 320"
    ceiling = naming.FORMAT_CEILINGS.get(fid)
    if ceiling is None:
        return "FLAC"
    return naming.quality_tag(fid, ceiling[0], ceiling[1])


def track_format_id(track: Any) -> int | None:
    """The format one track is actually stored in, as far as we can tell.

    Prefers the ``format_id`` the downloader recorded from ``getFileUrl``. Falls
    back to deriving it from the bit depth and sampling rate, which is all the
    library scanner can supply for a file Qobuzarr did not fetch itself.
    """
    recorded = _int(getattr(track, "format_id", None))
    if recorded is not None:
        return recorded
    return format_for_quality(
        getattr(track, "bit_depth", None), getattr(track, "sampling_rate", None)
    )


def owned_format_id(tracks: Any) -> int | None:
    """The format of the *worst* track we hold for a release.

    The worst rather than the best or the most common: an album is only as
    upgraded as its weakest file, and offering an upgrade that fixes one track
    of twelve is still an upgrade worth offering.

    Tracks whose format cannot be determined are skipped rather than counted as
    bad — a single untagged file would otherwise keep an already-maximal album
    permanently "upgradable". Returns ``None`` when nothing at all is known.
    """
    best_known: int | None = None
    for track in tracks or ():
        if not getattr(track, "path", None):
            continue
        fid = track_format_id(track)
        if fid is None:
            continue
        best_known = fid if best_known is None else min(best_known, fid)
    return best_known


def release_ceiling_format_id(album: Any) -> int | None:
    """The best format *this release* can be delivered in, ``None`` if unknown.

    Read from the ``max_bit_depth`` / ``max_sampling_rate`` the catalogue
    reports for the album. Asking for 24/192 on a CD master gets you 16/44.1,
    so without this an upgrade would be offered forever and never arrive.
    """
    return format_for_quality(
        getattr(album, "max_bit_depth", None), getattr(album, "max_sampling_rate", None)
    )


def obtainable_format_id(album: Any, target_format_id: int | None) -> int | None:
    """What a fresh download of *album* would actually land, or ``None``.

    *target_format_id* is the account-and-profile ceiling — pass
    ``QobuzClient.best_format_id(...)``, not the raw setting, or an upgrade will
    be promised that the subscription cannot deliver.
    """
    target = _int(target_format_id)
    ceiling = release_ceiling_format_id(album)
    if target is None or ceiling is None:
        return None
    return min(target, ceiling)


def upgrade_available(
    album: Any, tracks: Any, target_format_id: int | None
) -> tuple[int, int] | None:
    """``(owned, obtainable)`` when a better copy exists, else ``None``.

    ``None`` covers all three ways the answer can be no: nothing is on disk to
    compare, something is unknown, or what is on disk is already as good as this
    account can get for this release.
    """
    owned = owned_format_id(tracks)
    obtainable = obtainable_format_id(album, target_format_id)
    if owned is None or obtainable is None or obtainable <= owned:
        return None
    return owned, obtainable
