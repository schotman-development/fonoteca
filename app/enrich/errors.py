"""Failures and non-failures raised by the enrichment layer.

Two families, and the distinction matters:

**Transport failures** are :mod:`app.net.errors` classes, raised by the clients.
They mean the upstream misbehaved, and they retry.

**Outcomes** are :class:`EnrichmentOutcome` subclasses, raised by the matchers.
They mean the upstream behaved perfectly and the answer was "no" — no barcode to
search with, nothing matching it, two things matching it, or a rung that is not
configured. None of them is a fault, none of them retries quickly, and each
carries the ``enrichment_state.state`` value it becomes, so the orchestrator
writes ``exc.state`` instead of re-deriving it from the exception type.

That split is the whole reason "unidentified" never quietly becomes "identified":
an ambiguous match raises rather than picking a winner, and the row lands on the
review list for a human.
"""

from __future__ import annotations

from typing import Any, Sequence

from app.net.errors import (
    HttpAuthError,
    HttpError,
    HttpNotFound,
    HttpRateLimitError,
    HttpServerError,
    HttpTransportError,
)

__all__ = [
    "HttpError",
    "HttpAuthError",
    "HttpNotFound",
    "HttpRateLimitError",
    "HttpServerError",
    "HttpTransportError",
    "EnrichmentOutcome",
    "NoMatchKey",
    "NotMatched",
    "AmbiguousMatch",
    "MatchRejected",
    "SourceGated",
    "STATE_OK",
    "STATE_PENDING",
    "STATE_NO_KEY",
    "STATE_NOT_FOUND",
    "STATE_AMBIGUOUS",
    "STATE_GATED",
    "STATE_FAILED",
    "STATE_REJECTED",
    "ENRICHMENT_STATES",
]

#: The ``enrichment_state.state`` vocabulary. Kept here rather than in the model
#: so the matchers can name a state without importing the ORM.
STATE_OK = "ok"
STATE_PENDING = "pending"
STATE_NO_KEY = "no_key"
STATE_NOT_FOUND = "not_found"
STATE_AMBIGUOUS = "ambiguous"
STATE_GATED = "gated"
STATE_FAILED = "failed"

#: A person looked at this row and said no.
#:
#: The only state in the vocabulary that no matcher can produce — nothing in
#: :mod:`app.enrich` ever raises its way here, and no outcome class carries it.
#: It is written by :meth:`app.core.enricher.Enricher.reject` and by nothing
#: else, which is what makes it mean what it says.
#:
#: It is terminal in the strongest sense the system has. ``no_key`` and
#: ``gated`` also store ``next_attempt_at = NULL``, but both are waiting on their
#: *inputs* — a barcode, an identified artist, an installed ``fpcalc`` — and
#: :meth:`app.core.enricher.Enricher._rearm_stranded` exists precisely to notice
#: when one of those arrives. This one is waiting on nobody. Re-arming it would
#: put back on the review list the very row somebody just took off it, every
#: tick, forever. The one way out is
#: :meth:`app.core.enricher.Enricher.reopen` — a person changing their mind.
STATE_REJECTED = "rejected"

#: The same vocabulary as a sequence, in lifecycle order — where a row starts,
#: the two ways it ends well, the two dead ends that only a change of *inputs*
#: reopens, the two that retry on a timer, and the one a person closed by hand.
#: Published because the review list and the coverage grid both enumerate it,
#: and a client hand-typing eight strings is a list maintained twice.
ENRICHMENT_STATES: tuple[str, ...] = (
    STATE_PENDING,
    STATE_OK,
    STATE_NOT_FOUND,
    STATE_AMBIGUOUS,
    STATE_NO_KEY,
    STATE_GATED,
    STATE_FAILED,
    STATE_REJECTED,
)


class EnrichmentOutcome(Exception):
    """A definite, non-faulty answer of "not enriched".

    Args:
        message: Human-readable reason, shown on the review list.
        source: Which rung produced it, e.g. ``"musicbrainz"``.
        candidates: For an ambiguous result, what was in contention. These are
            put in front of a human rather than scored — see
            :class:`AmbiguousMatch`.
        partial: Anything the rung established *before* it decided the answer
            was "no". "Not identified" and "learned nothing" are different
            claims: AcoustID that cannot decode a single file has not identified
            the release, but it has proved every file is broken, and throwing
            that away loses the one verdict the whole rung exists to produce.
            The orchestrator persists it and then records ``state``.
    """

    #: The ``enrichment_state.state`` this outcome becomes.
    state: str = STATE_NOT_FOUND

    def __init__(
        self,
        message: str = "",
        *,
        source: str | None = None,
        candidates: Sequence[Any] | None = None,
        partial: Any = None,
    ) -> None:
        super().__init__(message or self.__class__.__doc__ or self.state)
        self.message = message
        self.source = source
        self.candidates = list(candidates or ())
        self.partial = partial

    def __str__(self) -> str:
        base = self.message or self.state
        return f"{base} (source={self.source})" if self.source else base


class NoMatchKey(EnrichmentOutcome):
    """There is nothing to match on — no barcode, no resolved artist, no ISRC.

    Terminal until the inputs change. The orchestrator stores ``NULL`` for
    ``next_attempt_at`` and re-opens the row only when ``match_key`` changes,
    which is what makes a corrected UPC re-trigger matching by itself.
    """

    state = STATE_NO_KEY


class NotMatched(EnrichmentOutcome):
    """The upstream answered, and nothing it returned matched.

    Retried on a slow ladder, because these catalogues genuinely gain releases
    over time.
    """

    state = STATE_NOT_FOUND


class AmbiguousMatch(EnrichmentOutcome):
    """More than one candidate survived, so none of them is trusted.

    This is the doctrine the bulk importer already follows: MusicBrainz answers
    ``Joanne Shaw Taylor`` with a ``Joanna Shaw Taylor`` too, and the cost of a
    wrong guess is a wrong MBID written into every file on disk. There is no
    scoring tie-break — ``candidates`` goes to the review list for a human.
    """

    state = STATE_AMBIGUOUS


class MatchRejected(EnrichmentOutcome):
    """A single candidate was found but failed a confirmation gate.

    Track counts disagreed, titles disagreed, an ISRC contradicted the mapping,
    or the audio fingerprint denied it. Treated like an ambiguous result: stashed
    for a human, never accepted on the balance of probability.
    """

    state = STATE_AMBIGUOUS


class SourceGated(EnrichmentOutcome):
    """The rung is not configured, so it did not run.

    MusicBrainz without ``ENRICHMENT_CONTACT``, AcoustID without an API key,
    fingerprinting without ``fpcalc``. Deliberately not an error: every path
    degrades to "this source contributed nothing" rather than failing the
    enrichment of an entity that other sources handled fine.
    """

    state = STATE_GATED
