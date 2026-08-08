"""What is actually on disk, measured rather than remembered.

Everything downstream of identification — the recording ids written into files,
the release a folder is believed to hold, the fingerprint verdicts — is a claim
the *database* makes about a *file*. This module is the only thing that checks
that claim, and it checks it by reading the file. That is principle four of the
integrity model, "verify the disk before trusting the database": a tag editor, a
rip that was redone, a media server with write access or a half-finished copy all
leave the database describing a file that no longer exists in that form, and
nothing else in Qobuzarr notices.

Pure in the sense that matters here: **no ORM, no session, no network**. It takes
paths and returns values. Everything is **synchronous** and does blocking file
I/O, so every caller on the event loop must go through ``asyncio.to_thread``,
exactly like :mod:`app.core.tagger` and :mod:`app.enrich.chromaprint`::

    stamp = await asyncio.to_thread(read_stamp, path)

**Three states, not two.** ``UNKNOWN`` — a file nothing has ever baselined — is
not ``CHANGED``. Collapsing the two is the mistake that makes this feature
useless on the day it ships: a library that has never been stamped would report
every file as tampered with, which is indistinguishable from noise, and the one
real edit in it would be invisible. A file only becomes ``RETAGGED`` or
``REPLACED`` by disagreeing with a measurement that was genuinely taken.

**One hash, not two.** The obvious design is a whole-file hash *plus* an
audio-only hash, so that a tag edit is visible as "the container moved but the
audio did not". It was considered and rejected. An audio-only digest means
walking FLAC metadata blocks, ID3v2 sizes and trailing APE tags to find where the
audio starts and stops — format-specific code that is subtly wrong for years,
because the wrong answer is a hash that is merely *different*, never a crash.
A whole-file hash plus :func:`audio_sample_count` gives the same answer for less:
a tag edit cannot change the number of audio samples, and the sample count is
already published by every container mutagen parses. FLAC's ``STREAMINFO`` MD5
would have been the free version of this, and it is not usable — in this user's
library it is zeroed in **185 of 200** files, because most encoders and every
re-muxer leave it blank.

"Published by the container" is where that argument nearly failed. An MP3 with no
Xing header publishes no duration at all, so mutagen derives one from the file
size — which counts a trailing ID3v1 or APEv2 tag as audio, so a plain tag write
moved the sample count and the "cannot change" claim was false for the commonest
container in a real library. :func:`audio_sample_count` measures those files over
the audio region instead; see it for what that costs.

**The tripwire comes first.** Hashing a whole file costs about **201 ms** per
file on this user's library, so 30 000 files is an hour and a half of disk. The
cheap pair — ``st_size`` and ``st_mtime``, one ``os.stat`` and no read — is what
runs on every pass; :func:`hash_file` runs only for the files whose tripwire
fired. Size and mtime are not evidence and are never compared here: they decide
whether it is worth opening the file, and the verdict always comes from the hash.

**It heals itself.** Suppose a write dies between changing a file and recording
the new stamp — the process is killed mid re-tag, the box loses power. The next
pass sees a hash that disagrees with the recorded one and calls it tampering.
That is a false positive, and it costs exactly one re-identification: the audio
is unchanged, so the same recording comes back, the row is re-baselined against
the file as it now is, and the library is correct again. There is no state that
needs unwinding and no window in which a wrong answer persists, which is why the
crash-safety story here is "let it re-verify" rather than a journal.
"""

from __future__ import annotations

import enum
import hashlib
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence

import mutagen
from mutagen.mp3 import BitrateMode, MPEGInfo

from app.logging_conf import get_logger

__all__ = [
    "CHUNK_BYTES",
    "DIGEST_SIZE",
    "FileStamp",
    "IntegrityState",
    "album_content_digest",
    "audio_sample_count",
    "classify",
    "hash_file",
    "read_stamp",
    "stat_stamp",
]

logger = get_logger(__name__)

#: 128 bits, as hex. Wide enough that a collision is not a thing that happens to
#: a music library, narrow enough that a row per track stays small — these are
#: stored once per file and read on every pass.
DIGEST_SIZE: int = 16

#: Read size for :func:`hash_file`. Large enough that the syscall overhead
#: disappears against the read itself, small enough that hashing a 400 MB
#: 24/192 file never holds 400 MB of it in memory.
CHUNK_BYTES: int = 1024 * 1024


class IntegrityState(str, enum.Enum):
    """What the disk says about one file, relative to what was recorded.

    ``UNKNOWN`` is a first-class answer, not a degenerate one: it is the state of
    every file until something baselines it, and treating it as change is what
    would make the whole feature cry wolf on day one.

    ``RETAGGED`` and ``REPLACED`` are separated because they license different
    work. A retag changed the metadata around audio that is provably the same, so
    the identification still holds and only the stamp needs refreshing. A
    replacement is a different piece of audio wearing the same filename, and the
    release it belongs to has to be worked out again from scratch.
    """

    #: Never baselined. No recorded hash exists, so nothing is being claimed.
    UNKNOWN = "unknown"

    #: The file hashes to exactly what was recorded.
    VERIFIED = "verified"

    #: Different bytes, same audio — the sample count is unchanged.
    RETAGGED = "retagged"

    #: Different bytes and the audio does not agree (or cannot be measured).
    REPLACED = "replaced"

    #: The file is not there.
    MISSING = "missing"


@dataclass(frozen=True, slots=True)
class FileStamp:
    """One measurement of one file.

    ``size`` and ``mtime`` are the tripwire; ``content_hash`` and
    ``sample_count`` are the evidence. The two halves are deliberately in one
    object because they are only meaningful together: a stamp carrying a size and
    no hash is a file that was seen but never baselined, which
    :func:`classify` reads as :attr:`IntegrityState.UNKNOWN`.

    Both evidence fields are optional, and for different reasons.
    ``content_hash`` is ``None`` when only :func:`stat_stamp` has run.
    ``sample_count`` is ``None`` when the container does not publish one or
    mutagen could not parse the file — an honest "not measured", which
    :func:`classify` refuses to read as agreement.
    """

    size: int
    mtime: float
    content_hash: str | None = None
    sample_count: int | None = None


def stat_stamp(path: Path) -> tuple[int, float] | None:
    """``(size, mtime)`` for *path*, or ``None`` if it is not there.

    One ``os.stat`` and no read. This is what runs over the whole library on
    every pass; :func:`hash_file` is what runs for the handful of files whose
    answer here differs from what was recorded.

    Neither number is evidence. A tag editor that preserves mtime, a restore from
    backup that moves it backwards, and a filesystem with one-second timestamp
    resolution all make this pair lie in the direction of "unchanged", which is
    why a match here means *skip the read*, never *the file is verified*. In the
    other direction it is free to be over-eager: an mtime that moved for no
    reason costs one hash and produces :attr:`IntegrityState.VERIFIED`.
    """
    try:
        info = os.stat(path)
    except OSError:
        return None
    return info.st_size, info.st_mtime


def hash_file(path: Path) -> str:
    """blake2b-128 over the **whole** file, as hex.

    Whole-file, container and all, which is what makes it cheap to be sure about:
    there is no "which bytes are the audio" question to get wrong. The cost of
    that choice is that a tag edit changes the hash, and
    :func:`audio_sample_count` is what tells a tag edit apart from a different
    recording — see the module docstring for why that beats a second, audio-only
    digest.

    Measured at roughly **201 ms per file** on this user's library, which is the
    entire reason :func:`stat_stamp` exists: at that price this must run for
    files whose cheap tripwire has already fired, never as a sweep.

    Raises ``OSError`` if the file cannot be read. Callers that want a verdict
    rather than an exception want :func:`read_stamp`.
    """
    digest = hashlib.blake2b(digest_size=DIGEST_SIZE)
    with open(path, "rb") as handle:
        while True:
            chunk = handle.read(CHUNK_BYTES)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def audio_sample_count(path: Path) -> int | None:
    """Number of audio samples in *path*, or ``None`` if nothing said.

    This is the field that separates :attr:`IntegrityState.RETAGGED` from
    :attr:`IntegrityState.REPLACED`, and it works because **no tag edit can
    change it**. Adding a Vorbis comment, rewriting an ID3v2 frame, embedding
    cover art or stripping an APE tag all move the container around audio whose
    sample count is fixed by the encode; re-ripping, transcoding or truncating
    the audio all change it.

    Exact where the format publishes it — FLAC's ``STREAMINFO`` carries
    ``total_samples`` outright — and otherwise derived from the duration, which
    every other container mutagen parses computes from its own headers and not
    from how big the file happens to be.

    **The exception is MPEG without a Xing header**, and it is the one that
    matters, because it is most of the MP3s in a real library. There mutagen has
    no frame count to work from and estimates the duration as
    ``8 * (filesize - first_frame_offset) / bitrate`` — a figure that counts any
    tag written at the *end* of the file as audio. Adding an ID3v1 tag is a pure
    metadata write (mutagen's own ``ID3().save(path, v1=2)``, and the default in
    mp3tag, foobar2000 and EasyTAG) and it moved this number by 128 bytes' worth
    of samples, which classified an ordinary retag as :attr:`
    IntegrityState.REPLACED` — re-identifying the release and, worse, refusing
    every :mod:`app.core.librarian` operation on it through the fifth gate. So
    those files are measured over the audio region only, with
    :func:`_trailing_tag_bytes` taken off the end.

    ``None`` when the format publishes neither, when the file will not parse, or
    when the numbers come back non-positive. That is a refusal to guess, and
    :func:`classify` honours it by declining to call two unknowns a match: an
    unmeasurable file that has changed is a *replacement* until something proves
    otherwise, because the cost of the other reading is skipping the
    re-identification of a file that is now a different recording.
    """
    try:
        audio: Any = mutagen.File(str(path))
    except Exception as exc:  # noqa: BLE001 - unreadable/corrupt files are expected here
        logger.debug("integrity.sample_count_failed path=%s error=%s", path, exc)
        return None
    info = getattr(audio, "info", None)
    if info is None:
        return None

    total = getattr(info, "total_samples", None)
    if isinstance(total, int) and total > 0:
        return total

    if isinstance(info, MPEGInfo) and info.bitrate_mode is BitrateMode.UNKNOWN:
        # No Xing/VBRI header, so mutagen's own length came from the file size.
        # A Xing file is left alone deliberately: its length is derived from the
        # frame count in the header, which is exact and already immune to
        # anything appended after the audio.
        return _mpeg_sample_count(path, info)

    length = getattr(info, "length", None)
    rate = getattr(info, "sample_rate", None)
    if not length or not rate:
        return None
    samples = round(float(length) * float(rate))
    return samples if samples > 0 else None


def _mpeg_sample_count(path: Path, info: MPEGInfo) -> int | None:
    """Samples in an MPEG stream whose duration has to be derived from bytes.

    Mutagen's estimate, with the two things that are not audio removed: the
    header region in front of the first frame (which it already accounts for)
    and whatever tag sits after the last one (which it does not). What is left is
    a figure a tag write cannot move, which is the only property this function is
    for — accuracy in seconds is beside the point, because both sides of every
    comparison are produced here.
    """
    frame_offset = getattr(info, "frame_offset", None)
    bitrate = getattr(info, "bitrate", None)
    rate = getattr(info, "sample_rate", None)
    if frame_offset is None or not bitrate or not rate:
        return None
    try:
        with open(path, "rb") as handle:
            handle.seek(0, os.SEEK_END)
            size = handle.tell()
            audio_bytes = size - int(frame_offset) - _trailing_tag_bytes(handle, size)
    except OSError as exc:
        logger.debug("integrity.mpeg_measure_failed path=%s error=%s", path, exc)
        return None
    if audio_bytes <= 0:
        return None
    samples = round(audio_bytes * 8.0 / float(bitrate) * float(rate))
    return samples if samples > 0 else None


def _trailing_tag_bytes(handle: Any, size: int) -> int:
    """How many bytes at the end of an open file are tag rather than audio.

    ID3v1 (128 bytes) and APEv2 are recognised, which is what every tagger in
    ordinary use writes. They are stripped in a **loop** rather than in a fixed
    order, because the documented order — audio, APEv2, ID3v1 — is not the only
    one that occurs: mutagen's own ``APEv2.save()`` appends its tag *after* an
    existing ID3v1, and a pass that expected the convention would then account
    for one of the two and report the file as replaced.

    Lyrics3 is not recognised, and the consequence of that gap is bounded and
    self-correcting: writing one produces a single ``REPLACED`` verdict, which
    costs one re-identification of a recording that comes back the same, and the
    row is then re-baselined against the file as it now is.
    """
    total = 0
    end = size

    while end > 0:
        if end >= 128:
            handle.seek(end - 128)
            if handle.read(3) == b"TAG":
                total += 128
                end -= 128
                continue

        if end >= 32:
            handle.seek(end - 32)
            footer = handle.read(32)
            if footer[:8] == b"APETAGEX":
                tag_size = int.from_bytes(footer[12:16], "little")
                flags = int.from_bytes(footer[20:24], "little")
                if flags & 0x80000000:  # the tag carries a header as well as a footer
                    tag_size += 32
                if 32 <= tag_size <= end:
                    total += tag_size
                    end -= tag_size
                    continue

        break

    return total


def read_stamp(path: Path) -> FileStamp | None:
    """Measure *path* fully: size, mtime, content hash and sample count.

    ``None`` when the file is gone or unreadable — the caller's own record is
    then the only thing that knows it should have been there, and
    :func:`classify` turns that into :attr:`IntegrityState.MISSING`.

    An unreadable *audio stream* is not the same failure and does not produce
    ``None``: the bytes still hash, so the stamp comes back with a
    ``content_hash`` and ``sample_count=None``. Deciding a file is corrupt is
    :mod:`app.enrich.chromaprint`'s job, on the evidence of a completed decode,
    and nothing here may pre-empt it — a verdict from this module would end with
    :func:`app.core.librarian.quarantine_corrupt_files` moving files out of the
    library on the strength of mutagen not recognising a container.
    """
    stat = stat_stamp(path)
    if stat is None:
        return None
    size, mtime = stat
    try:
        content_hash = hash_file(path)
    except OSError:
        return None
    return FileStamp(
        size=size,
        mtime=mtime,
        content_hash=content_hash,
        sample_count=audio_sample_count(path),
    )


def classify(*, recorded: FileStamp | None, current: FileStamp | None) -> IntegrityState:
    """Compare a recorded stamp with a fresh one. Pure — no I/O.

    The rules, in order, and the order is the argument:

    1. nothing on disk now → ``MISSING``;
    2. nothing recorded, or recorded without a hash → ``UNKNOWN``;
    3. hashes equal → ``VERIFIED``;
    4. hashes differ but the sample counts agree and are both known →
       ``RETAGGED``;
    5. anything else → ``REPLACED``.

    Rule 2 is the three-state rule: a row that was never baselined makes no
    claim, so there is nothing for the file to contradict. Rule 5 is where the
    caution lives — a sample count of ``None`` on **either** side is not
    agreement. Two unknowns look equal to ``==`` and are not: reading them as a
    match would file a genuine replacement as a retag, and a retag is exactly the
    verdict that says "the identification still holds, do not look again". The
    conservative direction costs one fingerprint of an unchanged recording.

    *current* is expected to come from :func:`read_stamp`, not from
    :func:`stat_stamp`: a caller whose tripwire did not fire has already decided
    not to look, and should skip this rather than ask it about a stamp with no
    hash in it — which, correctly but unhelpfully, answers ``REPLACED``.
    """
    if current is None:
        return IntegrityState.MISSING
    if recorded is None or recorded.content_hash is None:
        return IntegrityState.UNKNOWN
    if recorded.content_hash == current.content_hash:
        return IntegrityState.VERIFIED
    if (
        recorded.sample_count is not None
        and current.sample_count is not None
        and recorded.sample_count == current.sample_count
    ):
        return IntegrityState.RETAGGED
    return IntegrityState.REPLACED


def album_content_digest(hashes: Sequence[str | None]) -> str | None:
    """One digest standing for a whole release, or ``None``.

    blake2b-128 over the member hashes in the order given — which the caller
    supplies in disc/track order, so the digest is a statement about *this
    release as sequenced*, not about a bag of files. Two albums sharing every
    track in a different running order are different releases and get different
    digests; that is the point, not a side effect.

    ``None`` when any member hash is ``None``, and ``None`` for an empty
    sequence. A partially baselined album has no honest digest: computing one
    over the members that happen to be measured produces a value that changes the
    moment the rest are stamped, so it would compare unequal to itself forever
    and every pass would report the album as altered. "Not fully measured" is a
    state to report, not a number to fabricate.
    """
    members: list[str] = []
    for value in hashes:
        if value is None:
            return None
        members.append(value)
    if not members:
        return None

    digest = hashlib.blake2b(digest_size=DIGEST_SIZE)
    for value in members:
        digest.update(value.encode("ascii", "replace"))
        digest.update(b"\n")
    return digest.hexdigest()
