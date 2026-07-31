"""Qobuz API integration layer.

Modules
-------
``errors``
    Typed exceptions (:class:`QobuzError` and friends) carrying HTTP status,
    Qobuz's own error code and a ``retryable`` flag.
``ratelimit``
    The single global :class:`RateLimiter` every outbound call must pass
    through, plus the :class:`CircuitBreaker` that pauses traffic after
    repeated 429/5xx responses.
``secrets``
    Resolution, derivation, validation and caching of the ``app_secret``, and
    the :func:`sign` helper used for signed endpoints.
``client``
    :class:`QobuzClient`, the async façade the rest of the application uses.
``mapper``
    Pure functions turning raw Qobuz JSON into model-ready dictionaries.

Typical wiring::

    from app.qobuz import QobuzClient, RateLimiter

    limiter = RateLimiter.from_settings()      # build ONE, share it everywhere
    client = QobuzClient(limiter=limiter)
    await client.login()
"""

from __future__ import annotations

from app.qobuz.client import QobuzClient
from app.qobuz.errors import (
    QobuzAuthError,
    QobuzError,
    QobuzNotFound,
    QobuzRateLimitError,
    QobuzSecretError,
    QobuzTransportError,
    QobuzUnstreamable,
)
from app.qobuz.mapper import (
    classify_release_type,
    extract_album_artist,
    map_album,
    map_artist,
    map_track,
    parse_release_date,
)
from app.qobuz.ratelimit import CircuitBreaker, RateLimiter
from app.qobuz.secrets import resolve_app_secret, sign

__all__ = [
    "QobuzClient",
    "QobuzError",
    "QobuzAuthError",
    "QobuzRateLimitError",
    "QobuzNotFound",
    "QobuzUnstreamable",
    "QobuzSecretError",
    "QobuzTransportError",
    "RateLimiter",
    "CircuitBreaker",
    "sign",
    "resolve_app_secret",
    "map_artist",
    "map_album",
    "map_track",
    "classify_release_type",
    "parse_release_date",
    "extract_album_artist",
]
