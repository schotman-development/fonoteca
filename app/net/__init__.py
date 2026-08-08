"""Upstream-agnostic networking primitives.

Everything here is about *how* to talk to a remote HTTP API politely, never about
*which* one. Qobuz was the first upstream and these pieces grew inside
:mod:`app.qobuz`; the enrichment sources (Deezer, MusicBrainz, AcoustID, the
Cover Art Archive, Wikidata) need the same machinery, so it lives one layer down
where both can reach it.

Modules
-------
``ratelimit``
    :class:`RateLimiter` — one per upstream, shared by every caller of that
    upstream — and the :class:`CircuitBreaker` that pauses its traffic after
    repeated 429/5xx responses.
``http``
    :class:`JsonHttpClient`, a small credential-free JSON client that wires a
    limiter, tenacity retries and a circuit breaker together. Subclasses supply
    the per-source response mapping, which is the only part that genuinely
    differs between APIs.

Layering: ``api -> core -> {qobuz, enrich} -> net -> config/db/models``. Nothing
in here may import from any of those upper layers.
"""

from __future__ import annotations

from app.net.ratelimit import CircuitBreaker, RateLimiter

__all__ = ["RateLimiter", "CircuitBreaker"]
