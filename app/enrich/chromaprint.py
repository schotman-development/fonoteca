"""Acoustic fingerprints, via Chromaprint's ``fpcalc``.

Synchronous — it spawns a process and reads audio — so every caller must go
through ``asyncio.to_thread``, exactly like :mod:`app.core.tagger`.

Two jobs, and the second is the one worth having even without an AcoustID key:

**Identity.** A fingerprint is derived from the audio itself, so it says whether
two files are the same *recording* regardless of what the tags claim. That is
what confirms a metadata match and what finds the same single reissued across
four compilations.

**Integrity.** ``fpcalc`` has to decode the audio to hash it. A file it cannot
decode is a file no music player can play either — so a decode failure *is* the
corruption test, and a much more honest one than checking a file size. That is
why :class:`FingerprintError` distinguishes "this file is broken" from "the tool
is missing": only the first means anything about the library.

``fpcalc`` is a static binary, installed per the repository's Tier 0 rule (a
versioned directory under ``~/.local/opt`` plus one line in ``env.sh``), never
from a distribution package.
"""

from __future__ import annotations

import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from app.logging_conf import get_logger

__all__ = [
    "FileUnavailable",
    "Fingerprint",
    "FingerprintError",
    "FingerprintTimeout",
    "FpcalcMissing",
    "UnreadableAudio",
    "find_fpcalc",
    "fingerprint_file",
]

logger = get_logger(__name__)

#: Seconds of audio to hash. The AcoustID index is built on 120s, so asking for
#: more produces a fingerprint their server cannot match.
_LENGTH_SECONDS = 120

#: Generous: it has to decode two minutes of audio, and a network-mounted
#: library can be slow. Short enough that one wedged file cannot stall a batch.
_TIMEOUT_SECONDS = 60


class FingerprintError(Exception):
    """Base for anything that stopped a fingerprint being produced."""


class FpcalcMissing(FingerprintError):
    """The ``fpcalc`` binary is not installed or not on ``PATH``.

    Says nothing about the library — the rung is simply not configured, and every
    caller must treat it as *gated* rather than as evidence about any file.
    """


class UnreadableAudio(FingerprintError):
    """``fpcalc`` ran, finished, and could not decode the file.

    This one **is** evidence: a file Chromaprint cannot decode is a file a music
    player cannot play. Callers treat it as corruption, and corruption gets the
    file moved out of the library — so only a *completed* decode that failed may
    raise this. See :class:`FingerprintTimeout` for the one that did not finish.
    """

    def __init__(self, message: str, *, path: Path | None = None) -> None:
        super().__init__(message)
        self.path = path


class FingerprintTimeout(FingerprintError):
    """``fpcalc`` did not finish in time, so nothing was learned about the file.

    Deliberately **not** an :class:`UnreadableAudio`. A slow decode and an
    undecodable file are different claims, and only one of them is evidence: a
    contended machine, a network mount that stalled, or an unusually long file
    all produce this, and treating it as corruption would have the nightly
    housekeeping job move a perfectly good album into the trash unattended.

    Like :class:`FpcalcMissing` it says the measurement did not happen, so the
    track is left with no verdict and tried again another day.
    """

    def __init__(self, message: str, *, path: Path | None = None) -> None:
        super().__init__(message)
        self.path = path


class FileUnavailable(FingerprintError):
    """The file is not there to be read, so nothing was measured about it.

    Deliberately **not** an :class:`UnreadableAudio`, for the same reason
    :class:`FingerprintTimeout` is not: only a completed decode that failed is
    evidence about the bytes, and this one never ran. A path can stop resolving
    for reasons that have nothing to do with the audio — a network share that
    blipped, a folder another tagger renamed a second ago, a row the disk scan
    has not caught up with — and every one of those looked exactly like
    corruption before this existed. The cost of getting it wrong is not a wrong
    verdict on a page: it is
    :func:`app.core.librarian.quarantine_corrupt_files`, running unattended from
    the nightly job, moving a healthy release into the trash on the strength of a
    measurement nobody took.

    Like the timeout, it leaves the track with no verdict at all, to be tried
    again once the file is reachable.
    """

    def __init__(self, message: str, *, path: Path | None = None) -> None:
        super().__init__(message)
        self.path = path


@dataclass(frozen=True, slots=True)
class Fingerprint:
    """One file's acoustic fingerprint and the duration ``fpcalc`` measured.

    Both travel together because the AcoustID lookup needs them together — the
    duration is how the index narrows candidates before comparing hashes.
    """

    duration: int
    fingerprint: str
    path: Path | None = None


def find_fpcalc(configured: str | None = None) -> str | None:
    """Locate ``fpcalc``: the configured path, else ``PATH``. ``None`` if absent."""
    if configured:
        candidate = Path(configured).expanduser()
        if candidate.is_file():
            return str(candidate)
        logger.warning("FPCALC_PATH points at %s, which is not a file", candidate)
        return None
    return shutil.which("fpcalc")


def fingerprint_file(
    path: Path | str,
    *,
    fpcalc: str | None = None,
    length: int = _LENGTH_SECONDS,
    timeout: float = _TIMEOUT_SECONDS,
    runner: Any = None,
) -> Fingerprint:
    """Fingerprint one audio file. **Synchronous** — call via ``asyncio.to_thread``.

    Args:
        path: The audio file.
        fpcalc: Path to the binary; resolved from ``PATH`` when omitted.
        length: Seconds of audio to hash. Leave at 120 — the AcoustID index is
            built on that and a different value produces unmatchable hashes.
        runner: Injected ``subprocess.run`` for tests.

    Raises:
        FpcalcMissing: The tool is not available. Not a fact about the file.
        FileUnavailable: The file is not there. Not a fact about the audio.
        FingerprintTimeout: The decode did not finish. Not a fact about it either.
        UnreadableAudio: The tool ran and could not decode the file. This is.
    """
    target = Path(path)
    binary = fpcalc or find_fpcalc()
    if not binary:
        raise FpcalcMissing(
            "fpcalc is not installed. Fetch the Chromaprint release into "
            "~/.local/opt/chromaprint and add it to PATH, or set FPCALC_PATH."
        )
    if not target.is_file():
        # Not UnreadableAudio: nothing has been decoded at this point, so there
        # is no evidence about the audio to report. Saying otherwise here is what
        # would let a share that was unreachable for a minute end with the whole
        # release in the trash, quarantined by the nightly job on a verdict that
        # was never a measurement.
        raise FileUnavailable(f"{target} does not exist", path=target)

    run = runner or subprocess.run
    try:
        completed = run(
            [binary, "-length", str(length), "-json", str(target)],
            capture_output=True,
            text=True,
            timeout=timeout,
            check=False,
        )
    except FileNotFoundError as exc:  # pragma: no cover - raced with an uninstall
        raise FpcalcMissing(f"{binary} disappeared") from exc
    except subprocess.TimeoutExpired as exc:
        # Not UnreadableAudio: that is a verdict about the file, and this is a
        # verdict about the run. The file may well be fine and the machine busy,
        # and the caller's response to corruption is to move it to the trash.
        raise FingerprintTimeout(
            f"fpcalc timed out on {target.name} after {timeout:.0f}s", path=target
        ) from exc

    if completed.returncode != 0:
        raise UnreadableAudio(
            f"fpcalc could not decode {target.name}: "
            f"{(completed.stderr or '').strip()[:200] or 'no error output'}",
            path=target,
        )

    return _parse(completed.stdout, target)


def _parse(stdout: str, target: Path) -> Fingerprint:
    """Read ``fpcalc -json`` output into a :class:`Fingerprint`."""
    import json  # noqa: PLC0415 - only needed here

    try:
        payload = json.loads(stdout or "{}")
    except ValueError as exc:
        raise UnreadableAudio(
            f"fpcalc produced unreadable output for {target.name}", path=target
        ) from exc

    fingerprint = str(payload.get("fingerprint") or "")
    duration = payload.get("duration")
    if not fingerprint or duration is None:
        raise UnreadableAudio(
            f"fpcalc produced no fingerprint for {target.name}", path=target
        )
    return Fingerprint(
        duration=int(round(float(duration))), fingerprint=fingerprint, path=target
    )
