"""The majority rule that decides when a source may overwrite Qobuz.

Pure, no I/O. One function does the work: :func:`consensus` counts the non-null
opinions on a field and returns the value only when **more than half** of them
agree. Anything less writes nothing and records the split, which the review list
renders.

Why a vote rather than a precedence order. Every source here is wrong sometimes
and each is wrong differently: Qobuz calls a four-track release a single because
it counts tracks, Deezer inherits whatever the distributor typed, MusicBrainz is
hand-curated and therefore both the best and the most likely to be mid-edit. A
fixed ranking would follow whichever source happened to be top of the list into
its particular failure. A majority only moves when the sources that disagree with
Qobuz also agree with each other, which is a much stronger signal than any one of
them alone.

Why *more than* half rather than at least half. With four sources, a 2–2 split is
a coin flip, and resolving a coin flip in favour of "change the user's library"
is the wrong default. Ties keep what Qobuz said.

What this is allowed to change is deliberately tiny — see
:data:`WRITABLE_FIELDS`. Everything else enrichment learns lives in the side
tables and is merged at read time, where being wrong costs a wrong line on a page
rather than a thousand renamed folders.
"""

from __future__ import annotations

import json
from collections import Counter
from dataclasses import dataclass, field
from typing import Any, Iterable, Mapping

from app.models import ReleaseType, normalize_release_type

__all__ = [
    "NEVER_WRITABLE",
    "WRITABLE_FIELDS",
    "Vote",
    "ConsensusResult",
    "consensus",
    "release_type_from_musicbrainz",
    "release_type_from_deezer",
    "encode_consensus",
    "decode_consensus",
]

#: The only Qobuz-owned columns a majority may rewrite.
#:
#: ``release_type`` is here because Qobuz genuinely guesses it — its own mapper
#: falls back to "three tracks or fewer is a single, six or fewer is an EP" —
#: while MusicBrainz has a curated release-group type and Deezer has the
#: distributor's own ``record_type``.
#:
#: ``label`` and ``genre`` are **not** here, and must not be added. Both are
#: tokens in the naming template, so changing one makes ``plan_refile()`` want to
#: move every folder it appears in. The enriched values are still available —
#: they live in the side tables, where the UI reads them and nothing renames
#: anything.
WRITABLE_FIELDS: frozenset[str] = frozenset({"release_type"})

#: The Qobuz-owned values a majority may **never** rewrite, named rather than
#: merely absent.
#:
#: The rule already held by omission — these are simply not in
#: :data:`WRITABLE_FIELDS` — but the Structure & tags screen has to be able to
#: *say* it, and a client that spells out "LABEL is never overwritten" on its own
#: is one edit away from being wrong. Both are naming-template tokens, so a
#: "better" value would make ``plan_refile()`` want to move every folder they
#: appear in.
NEVER_WRITABLE: frozenset[str] = frozenset({"label", "genre"})

#: MusicBrainz release-group primary types onto Qobuzarr's vocabulary.
_MB_PRIMARY: dict[str, str] = {
    "album": ReleaseType.ALBUM.value,
    "single": ReleaseType.SINGLE.value,
    "ep": ReleaseType.EP.value,
    "broadcast": ReleaseType.OTHER.value,
    "other": ReleaseType.OTHER.value,
}

#: Secondary types that describe what a release *is* more usefully than its
#: primary type does. A live album's primary type is still "Album"; calling it
#: live is the more informative answer, and it is what release-type filters are
#: actually for. Ordered — the first hit wins.
_MB_SECONDARY: tuple[tuple[str, str], ...] = (
    ("compilation", ReleaseType.COMPILATION.value),
    ("live", ReleaseType.LIVE.value),
)

#: Deezer's ``record_type`` vocabulary.
_DEEZER_RECORD_TYPES: dict[str, str] = {
    "album": ReleaseType.ALBUM.value,
    "single": ReleaseType.SINGLE.value,
    "ep": ReleaseType.EP.value,
    "compile": ReleaseType.COMPILATION.value,
    "compilation": ReleaseType.COMPILATION.value,
}


@dataclass(frozen=True, slots=True)
class Vote:
    """One source's opinion about one field."""

    source: str
    value: Any

    def as_pair(self) -> tuple[str, Any]:
        return self.source, self.value


@dataclass(slots=True)
class ConsensusResult:
    """The outcome of one vote, including the losers.

    ``value`` is non-``None`` only when a real majority formed. ``votes`` is kept
    either way, because a split is the interesting case: it is what the review
    list shows and what makes a disagreement inspectable instead of silent.
    """

    field_name: str
    value: Any = None
    agreed: int = 0
    total: int = 0
    votes: dict[str, Any] = field(default_factory=dict)

    @property
    def decided(self) -> bool:
        """True when a majority formed and the value may be written."""
        return self.value is not None

    @property
    def share(self) -> float:
        """Fraction of the non-null opinions that agreed."""
        return (self.agreed / self.total) if self.total else 0.0

    @property
    def is_split(self) -> bool:
        """True when sources disagreed and nothing was decided."""
        return self.total > 1 and not self.decided

    def as_dict(self) -> dict[str, Any]:
        return {
            "field": self.field_name,
            "value": self.value,
            "agreed": self.agreed,
            "total": self.total,
            "share": round(self.share, 3),
            "votes": dict(self.votes),
        }


def consensus(
    field_name: str,
    votes: Iterable[Vote] | Mapping[str, Any],
    *,
    threshold: float = 0.51,
) -> ConsensusResult:
    """Return the value more than *threshold* of the non-null opinions share.

    Args:
        field_name: The field being decided, carried through for the record.
        votes: Either :class:`Vote` objects or a ``{source: value}`` mapping.
            ``None`` values are dropped — a source with no opinion does not get
            to abstain *against* the others, it simply is not counted.
        threshold: Fraction that must agree. Clamped to just above one half:
            "51%" is a majority, "50%" is a tie, and a tie must not move
            anything.

    A single source is enough only when it is the *only* source with an opinion —
    one out of one is unanimous. That is intentional: with just Deezer enabled,
    Deezer's answer is the only evidence there is, and refusing to act on it
    would make the setting pointless. It is still a majority of what was asked.
    """
    pairs = (
        [(vote.source, vote.value) for vote in votes]
        if not isinstance(votes, Mapping)
        else list(votes.items())
    )
    opinions = {source: value for source, value in pairs if value is not None}
    result = ConsensusResult(field_name=field_name, votes=dict(opinions))
    if not opinions:
        return result

    counts = Counter(_hashable(value) for value in opinions.values())
    winner, agreed = counts.most_common(1)[0]
    result.total = len(opinions)
    result.agreed = agreed

    # A joint-first placing is a tie however many sources voted, and a tie is
    # never a majority. Checked explicitly because `most_common` picks one of the
    # tied values arbitrarily, which would otherwise look decisive.
    if list(counts.values()).count(agreed) > 1:
        return result

    if agreed / result.total > max(0.5, min(threshold, 1.0)) - 1e-9:
        result.value = _unhashable(winner, opinions.values())
    return result


def _hashable(value: Any) -> Any:
    """Make a value usable as a Counter key without losing what it was."""
    if isinstance(value, (list, tuple)):
        return tuple(value)
    if isinstance(value, dict):
        return tuple(sorted(value.items()))
    return value


def _unhashable(key: Any, originals: Iterable[Any]) -> Any:
    """Recover the original object a Counter key came from."""
    for value in originals:
        if _hashable(value) == key:
            return value
    return key  # pragma: no cover - the key always came from originals


# ---------------------------------------------------------------------------
# Turning each source's vocabulary into a vote
# ---------------------------------------------------------------------------
def release_type_from_musicbrainz(
    primary: str | None, secondary: Iterable[str] | None = None
) -> str | None:
    """MusicBrainz's release-group type as a Qobuzarr release type.

    Secondary types win over the primary one. A live album and a compilation are
    both "Album" primarily, and if the user has asked to skip compilations, the
    secondary type is the entire point of the question.
    """
    lowered = {str(value).strip().lower() for value in (secondary or ()) if value}
    for needle, mapped in _MB_SECONDARY:
        if needle in lowered:
            return mapped
    if not primary:
        return None
    return _MB_PRIMARY.get(str(primary).strip().lower())


def release_type_from_deezer(record_type: str | None) -> str | None:
    """Deezer's ``record_type`` as a Qobuzarr release type."""
    if not record_type:
        return None
    return _DEEZER_RECORD_TYPES.get(str(record_type).strip().lower())


def release_type_from_qobuz(value: str | None) -> str | None:
    """Qobuz's stored release type, normalised for comparison.

    ``other`` is returned as ``None`` — an explicit "we do not know" should not
    get to outvote two sources that do.
    """
    if not value:
        return None
    normalised = normalize_release_type(value)
    return None if normalised == ReleaseType.OTHER.value else normalised


# ---------------------------------------------------------------------------
# Storage
# ---------------------------------------------------------------------------
def encode_consensus(results: Iterable[ConsensusResult]) -> str | None:
    """Serialise vote records for ``album_metadata.consensus_json``.

    Every field that was voted on is stored, decided or not. The undecided ones
    are the reason this column exists: they are what the review list shows, and
    without them a disagreement would be invisible.
    """
    payload = {result.field_name: result.as_dict() for result in results}
    return json.dumps(payload, sort_keys=True) if payload else None


def decode_consensus(raw: str | None) -> dict[str, dict[str, Any]]:
    """Read back what :func:`encode_consensus` wrote, tolerating junk."""
    if not raw:
        return {}
    try:
        payload = json.loads(raw)
    except (TypeError, ValueError):
        return {}
    return payload if isinstance(payload, dict) else {}
