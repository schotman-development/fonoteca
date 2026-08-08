"""Open-data enrichment sources.

Qobuz says what it says; this package is everything else the library can be told
about itself. The rungs, in the order they run:

``deezer``
    Fast, near-total catalogue coverage, key-free. Matched exactly on a barcode
    or on a browse of an already-resolved artist. **Runs first because it
    supplies the UPC that MusicBrainz needs** — of 3313 albums in a real
    library only 6 carried a ``upc``, so run the other way round MusicBrainz has
    nothing to join on.
``musicbrainz``
    The authority: MBIDs, ISNI, release-group types, catalogue numbers, credits,
    recordings. CC0, ~1 req/s, and it requires a User-Agent naming a contact.
``acoustid``
    Chromaprint fingerprints. Confirms or denies a candidate match, finds
    duplicate singles and EPs, and detects corrupt files (a file ``fpcalc``
    cannot decode is a broken file).
``coverart``
    The Cover Art Archive, keyed by MBID — zero matching risk by construction.
``wikidata``
    Reached only through MusicBrainz url-relations, so also an exact id chain.
    Artist biography (CC BY-SA, stored with its attribution) and a Commons
    portrait.

Two rules hold across all of them:

**Exact or nothing.** No fuzzy scoring, no closest-hit-wins, anywhere. Anything
unmatched or ambiguous raises an :class:`~app.enrich.errors.EnrichmentOutcome`
and lands on the review list for a human to identify.

**A Qobuz column is overwritten only by consensus.** More than half of the
non-null source opinions must agree — see :mod:`app.enrich.merge`. Everything
else stays in the metadata side tables and is merged at read time, so enrichment
can never disturb downloading, filing, naming or the quality comparison.
"""

from __future__ import annotations

from app.enrich.errors import (
    AmbiguousMatch,
    EnrichmentOutcome,
    MatchRejected,
    NoMatchKey,
    NotMatched,
    SourceGated,
)

__all__ = [
    "EnrichmentOutcome",
    "NoMatchKey",
    "NotMatched",
    "AmbiguousMatch",
    "MatchRejected",
    "SourceGated",
]
