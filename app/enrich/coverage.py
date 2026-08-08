"""Deciding which release a directory of files *is*, from the audio alone.

Pure: no I/O, no ORM, no network, no clock. The same contract as
:mod:`app.enrich.matching`, and for the same reason — every rule here is a
predicate a person could check by hand with the two lists in front of them, and a
rule that cannot be stated that way does not belong in it.

**Solve the album, never the track.** A fingerprint identifies a *recording*, and
a recording is a hopeless answer on its own: "Sultans of Swing" sits on the album,
on four live records and on a dozen compilations, so resolving each file
separately scatters one directory across a dozen releases and then asks which of
them won. The question worth asking is about the directory as a whole — which
single release explains *this set of files* — and it has an answer that needs no
scoring, because a release either seats every file it claims or it does not.

Three requirements, all of them necessary, none of them weighted:

**INJECTIVE.** Every file the release explains must be able to sit at a
*distinct* position in it. Two files claiming one slot does not mean one of them
wins; it means the hypothesis is wrong. This is checked as a matching problem
rather than by seating files in the order they arrive, because a first-fit walk
answers a different question depending on how the caller sorted its directory,
and "does a seating exist" has one answer.

**COVERAGE.** The release must explain essentially all the files — see
:data:`MIN_COVERAGE` for why "essentially" and not "all".

**TRACK COUNT.** The release's own track count must equal the number of files.
This is the gate that keeps a box set out: a 60-track anthology genuinely
contains all fifteen of these recordings at fifteen distinct positions, so it
passes the other two, and only counting rules it out.

Then the arithmetic, which is the whole design:

* exactly one admissible release — **identified**;
* none — **unidentified**;
* several — **ambiguous**, and they are put in front of a human.

Several admissible releases are normally not a puzzle but a fact about the world:
they are editions of one release group, pressed for different territories with
identical tracklists, and nothing in the audio can tell them apart because
nothing *about* the audio differs. That is the level
:mod:`app.enrich.musicbrainz` already falls back to — see its ``barcode-rg`` and
``rg-browse`` paths, which record release-group facts and deliberately leave
``mb_release_mbid`` empty rather than invent a pressing. :attr:`CoverageResult.
release_group_id` hands the caller that fallback when every admissible candidate
agrees on the group.

**There is no highest-coverage-wins branch, and there must never be one.**
:func:`app.enrich.matching.sole_match` states the reason and it applies here
unchanged: every tie-break anyone reaches for — most files explained, most
tracks, earliest release date, the country the user lives in — is a guess dressed
as a rule, and this one gets written into every file on disk as
``MUSICBRAINZ_ALBUMID``. A release that explains fourteen of fifteen files does
not beat one that explains fifteen; if both are admissible the honest answer is
that the audio does not distinguish them, and a person decides.

Nothing here reads a name. A title or an artist name may *reject* a candidate
elsewhere (:func:`app.enrich.matching.titles_match`,
:func:`app.enrich.matching.names_match`); it may never select one, and this
module needs no name at all to reach its verdict.
"""

from __future__ import annotations

import enum
import math
from dataclasses import dataclass
from typing import Iterable, Sequence

from app.enrich.errors import STATE_AMBIGUOUS, STATE_NOT_FOUND, STATE_OK

__all__ = [
    "MIN_COVERAGE",
    "ReleaseSlot",
    "RecordingCandidate",
    "FileCandidates",
    "ReleaseFacts",
    "ReleaseCoverage",
    "Verdict",
    "CoverageResult",
    "minimum_explained",
    "solve_album",
]

#: The fraction of the directory's files a release must explain to be
#: admissible.
#:
#: Not 1.0, because a fingerprint index has holes: an interlude nobody ever
#: uploaded, a track remastered enough to hash differently, a file that is
#: simply too short to fingerprint. Demanding every file lets one hole veto a
#: release that fourteen other files agree on, which is the failure mode that
#: makes fingerprinting look useless on exactly the obscure records it is most
#: needed for.
#:
#: Not lower, because coverage is the only requirement that scales with the
#: evidence: four fifths still means a release is carrying the *bulk* of the
#: directory. The measured case this was set against is a fifteen-file Mark
#: Knopfler album, where "Down the Road Wherever" explains 15 of 15 and "On the
#: Road to Milano" explains 1 — nowhere near the boundary in either direction,
#: which is the point. A threshold that has to be fine-tuned to separate the
#: real answer from the noise is a score, and a score is what this module
#: refuses to be.
MIN_COVERAGE = 0.8

#: One seat on a release, as the matcher keys it. Either a real position —
#: ``("track", "1/3")`` — or, when the source did not say where the recording
#: sits, the recording's own identity, ``("recording", "<mbid>")``. See
#: :func:`_slot_keys` for why the fallback is the honest weaker test.
SlotKey = tuple[str, str]


# ---------------------------------------------------------------------------
# Input — what the audio produced, as plain data
# ---------------------------------------------------------------------------
@dataclass(frozen=True, slots=True)
class ReleaseSlot:
    """Where one recording sits on one release.

    ``medium`` and ``position`` are optional because the source often knows a
    recording is *on* a release without saying where — AcoustID's ``releaseids``
    metadata is a bare list of ids. An absent ``medium`` is read as disc 1
    rather than as a namespace of its own: a release that never mentions discs
    has one, and the alternative would let two copies of track 3 sit side by
    side and call it injective.
    """

    release_id: str
    medium: int | None = None
    position: int | None = None


@dataclass(frozen=True, slots=True)
class RecordingCandidate:
    """One recording the audio in a file could be, and where it is published.

    Deliberately carries no score. Whatever confidence the fingerprint service
    attached has already done its job by the time a candidate reaches this
    module — the provider drops anything below its own threshold — and keeping
    the number here would put a tie-break within arm's reach of a solver whose
    entire purpose is not to have one.
    """

    recording_id: str
    slots: tuple[ReleaseSlot, ...] = ()


@dataclass(frozen=True, slots=True)
class FileCandidates:
    """One file on disk and every recording it could hold.

    A file with no candidates at all is normal, not an error: it is a hole in
    the index, and :data:`MIN_COVERAGE` exists to absorb a few of them.
    ``file_id`` is opaque — a track id, a path, an index — and is only ever
    echoed back in the evidence.
    """

    file_id: str
    recordings: tuple[RecordingCandidate, ...] = ()


@dataclass(frozen=True, slots=True)
class ReleaseFacts:
    """What is known about a candidate release as a whole.

    ``track_count`` is the only field the verdict depends on, and a release
    without one can never be admissible — "unknown means no", the rule
    :mod:`app.core.quality` already holds to. Note what that does *not* mean: an
    uncounted release cannot make a counted one ambiguous either, so forgetting
    to look one up can only ever lose a match, never manufacture one. Everything
    unjudged is listed in :attr:`CoverageResult.considered`.
    """

    release_id: str
    track_count: int | None = None
    title: str | None = None
    release_group_id: str | None = None


# ---------------------------------------------------------------------------
# Output — the verdict, and the working that produced it
# ---------------------------------------------------------------------------
@dataclass(frozen=True, slots=True)
class ReleaseCoverage:
    """How one candidate release fared against the directory.

    Produced for every release considered, admissible or not, because a refusal
    that cannot be explained is indistinguishable from a matcher doing nothing —
    the same argument the review list is built on. The three requirements are
    kept as separate flags rather than collapsed into a number so a caller can
    say *which* one failed without parsing prose.
    """

    release: ReleaseFacts
    file_count: int
    explained: tuple[str, ...] = ()
    unexplained: tuple[str, ...] = ()
    #: A witness, not a decision: ``(file_id, seat)`` pairs showing one way every
    #: explained file fits. Empty when no such seating exists.
    seating: tuple[tuple[str, str], ...] = ()
    covers: bool = False
    counts_match: bool = False
    injective: bool = False
    reason: str = ""

    @property
    def release_id(self) -> str:
        return self.release.release_id

    @property
    def admissible(self) -> bool:
        """All three requirements, and no weighting between them."""
        return self.covers and self.counts_match and self.injective


class Verdict(str, enum.Enum):
    """The three answers, and there is no fourth."""

    IDENTIFIED = "identified"
    AMBIGUOUS = "ambiguous"
    UNIDENTIFIED = "unidentified"


#: Which ``enrichment_state.state`` each verdict becomes. Kept beside the verdict
#: for the reason :mod:`app.enrich.errors` keeps it beside the outcome: the
#: caller writes it down rather than re-deriving it from the type, and the two
#: cannot drift apart. Unidentified is ``not_found`` rather than ``ambiguous`` —
#: the audio produced no candidate release worth putting to a human, so there is
#: nothing for one to decide.
_STATES = {
    Verdict.IDENTIFIED: STATE_OK,
    Verdict.AMBIGUOUS: STATE_AMBIGUOUS,
    Verdict.UNIDENTIFIED: STATE_NOT_FOUND,
}


@dataclass(frozen=True, slots=True)
class CoverageResult:
    """The answer for one directory.

    ``candidates`` holds the admissible releases: exactly one when identified,
    two or more when ambiguous, none otherwise. ``considered`` holds every
    release that was looked at, including the rejected ones, which is what makes
    a "no" reviewable.
    """

    verdict: Verdict
    #: The identified release. ``None`` for every other verdict — there is no
    #: "best guess" field here on purpose.
    release_id: str | None = None
    candidates: tuple[ReleaseCoverage, ...] = ()
    considered: tuple[ReleaseCoverage, ...] = ()
    reason: str = ""

    @property
    def identified(self) -> bool:
        return self.verdict is Verdict.IDENTIFIED

    @property
    def evidence(self) -> ReleaseCoverage | None:
        """The working behind an identification, or ``None``."""
        return self.candidates[0] if self.identified else None

    @property
    def state(self) -> str:
        """The ``enrichment_state.state`` this verdict becomes."""
        return _STATES[self.verdict]

    @property
    def release_group_id(self) -> str | None:
        """The release group every admissible candidate belongs to, if they agree.

        The salvage for an ambiguous result, and a strictly weaker claim than
        the one that failed: several editions with identical tracklists cannot
        be told apart by their audio, but they are still one record, and *which
        record* is the thing worth writing down. Returns ``None`` the moment the
        candidates disagree or any of them has no group — a group derived from
        some of the candidates would be a tie-break wearing a different hat.
        """
        groups = {item.release.release_group_id for item in self.candidates}
        if len(groups) != 1 or None in groups:
            return None
        return next(iter(groups))


# ---------------------------------------------------------------------------
# The solve
# ---------------------------------------------------------------------------
def minimum_explained(file_count: int) -> int:
    """How many files a release must explain, at :data:`MIN_COVERAGE`.

    Rounded up, which matters most where the evidence is thinnest: a two-file
    directory needs both files, because "four fifths of two" is not a majority
    of anything.
    """
    return math.ceil(file_count * MIN_COVERAGE)


def solve_album(
    files: Sequence[FileCandidates],
    releases: Iterable[ReleaseFacts] = (),
) -> CoverageResult:
    """Which release this directory is, or why that cannot be said.

    *files* is the directory, one entry per file, in any order — the verdict does
    not depend on it. *releases* is what the caller managed to learn about the
    releases those files point at; a release named in a slot but missing from
    here is still considered and still reported, it simply cannot pass the track
    count requirement.
    """
    directory = tuple(files)
    if not directory:
        return CoverageResult(
            Verdict.UNIDENTIFIED, reason="there are no files to identify"
        )

    facts = {item.release_id: item for item in releases}
    release_ids = sorted(
        {
            slot.release_id
            for entry in directory
            for recording in entry.recordings
            for slot in recording.slots
        }
    )
    if not release_ids:
        return CoverageResult(
            Verdict.UNIDENTIFIED,
            reason=f"none of the {len(directory)} file(s) resolved to a recording "
            "on any release",
        )

    considered = tuple(
        _cover(facts.get(release_id) or ReleaseFacts(release_id), directory)
        for release_id in release_ids
    )
    admissible = tuple(item for item in considered if item.admissible)

    if len(admissible) == 1:
        winner = admissible[0]
        return CoverageResult(
            Verdict.IDENTIFIED,
            release_id=winner.release_id,
            candidates=admissible,
            considered=considered,
            reason=winner.reason,
        )
    if admissible:
        return CoverageResult(
            Verdict.AMBIGUOUS,
            candidates=admissible,
            considered=considered,
            reason=f"{len(admissible)} releases each account for these "
            f"{len(directory)} file(s); the audio cannot tell them apart",
        )
    return CoverageResult(
        Verdict.UNIDENTIFIED,
        considered=considered,
        reason=f"none of the {len(considered)} candidate release(s) accounts for "
        f"these {len(directory)} file(s)",
    )


def _cover(release: ReleaseFacts, files: Sequence[FileCandidates]) -> ReleaseCoverage:
    """Test one release against the directory and record the working."""
    options = [_slot_keys(entry, release.release_id) for entry in files]
    explained = tuple(
        entry.file_id for entry, keys in zip(files, options) if keys
    )
    unexplained = tuple(
        entry.file_id for entry, keys in zip(files, options) if not keys
    )

    seating = _seat_everyone([keys for keys in options if keys])

    covers = len(explained) >= minimum_explained(len(files))
    counts_match = (
        release.track_count is not None and release.track_count == len(files)
    )
    injective = seating is not None

    faults: list[str] = []
    if not covers:
        faults.append(f"accounts for {len(explained)} of {len(files)} file(s)")
    if not counts_match:
        faults.append(
            "nobody has counted its tracks"
            if release.track_count is None
            else f"has {release.track_count} track(s) against {len(files)} file(s)"
        )
    if not injective:
        faults.append("two files claim one position on it")

    witness: tuple[tuple[str, str], ...] = ()
    if seating is not None:
        # The seating is indexed against the explained files, in the order they
        # were offered, which is the order ``explained`` was built in.
        witness = tuple(
            sorted(
                (explained[seated], _seat_label(key))
                for key, seated in seating.items()
            )
        )

    return ReleaseCoverage(
        release=release,
        file_count=len(files),
        explained=explained,
        unexplained=unexplained,
        seating=witness,
        covers=covers,
        counts_match=counts_match,
        injective=injective,
        reason="; ".join(faults)
        or f"accounts for {len(explained)} of {len(files)} file(s), "
        "each at a distinct position",
    )


def _slot_keys(entry: FileCandidates, release_id: str) -> frozenset[SlotKey]:
    """Every seat on *release_id* this file could occupy.

    A candidate whose slot states a position yields that position. A candidate
    whose slot does not yields the *recording's* identity instead, which is the
    strongest thing that can honestly be said without one: two files resolving to
    the same recording on the same release still collide — they are the same
    track twice, whatever number it carries — while two files resolving to
    different recordings are allowed to be different tracks. That is weaker than
    a real position check and it is documented as weaker; what it never does is
    let a collision it can see pass as a seating.
    """
    keys: set[SlotKey] = set()
    for recording in entry.recordings:
        for slot in recording.slots:
            if slot.release_id != release_id:
                continue
            if slot.position is None:
                keys.add(("recording", recording.recording_id))
            else:
                keys.add(("track", f"{slot.medium or 1}/{slot.position}"))
    return frozenset(keys)


def _seat_everyone(options: Sequence[frozenset[SlotKey]]) -> dict[SlotKey, int] | None:
    """Seat every file at a distinct slot, or answer ``None`` because none exists.

    Maximum bipartite matching by augmenting paths (Kuhn's algorithm), which is
    exact and — unlike seating files greedily in the order they were listed —
    independent of that order. The distinction is not academic: a file whose only
    seat is track 3 and a file that could take track 3 or track 7 have a valid
    seating, and a first-fit walk finds it or misses it depending on which of
    them the directory listing happened to yield first. A verdict that depends on
    a directory listing is not a verdict.
    """
    seating: dict[SlotKey, int] = {}
    for index in range(len(options)):
        if not _reseat(index, options, seating, set()):
            return None
    return seating


def _reseat(
    index: int,
    options: Sequence[frozenset[SlotKey]],
    seating: dict[SlotKey, int],
    visited: set[SlotKey],
) -> bool:
    """Find a seat for *index*, moving whoever is already sitting there."""
    for key in sorted(options[index]):
        if key in visited:
            continue
        visited.add(key)
        holder = seating.get(key)
        if holder is None or _reseat(holder, options, seating, visited):
            seating[key] = index
            return True
    return False


def _seat_label(key: SlotKey) -> str:
    """Render a seat for a human reading the evidence."""
    kind, value = key
    return value if kind == "track" else f"recording {value}"
