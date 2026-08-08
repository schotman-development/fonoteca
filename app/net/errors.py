"""Typed exceptions for any HTTP upstream.

These mirror :mod:`app.qobuz.errors` in shape — status, application-level code,
endpoint, a ``retryable`` flag and an optional ``retry_after`` — but carry a
``source`` so a log line says *which* upstream failed. :mod:`app.net.http` feeds
``retryable`` straight to tenacity, so a new error class only participates in
retries if it opts in.

Qobuz keeps its own hierarchy. It predates this one, it is wired into a lot of
call sites and its ``QobuzSecretError``/``QobuzUnstreamable`` members mean
nothing to another API — merging the two would buy nothing and risk the download
path. These are for everything else.
"""

from __future__ import annotations

__all__ = [
    "HttpError",
    "HttpAuthError",
    "HttpNotFound",
    "HttpRateLimitError",
    "HttpServerError",
    "HttpTransportError",
]


class HttpError(Exception):
    """Base class for every failure talking to a non-Qobuz HTTP upstream.

    Args:
        message: Human-readable description, safe to log and to show in the UI.
        source: Short upstream name, e.g. ``"musicbrainz"``.
        status_code: HTTP status of the offending response, when there was one.
        code: The upstream's own error code, when it supplied one.
        endpoint: Relative path that was called.
        retryable: True when repeating the request could succeed later.
        retry_after: Seconds the server asked us to wait, when it said so.
    """

    #: Default for subclasses that are always (or never) worth retrying.
    default_retryable: bool = False

    def __init__(
        self,
        message: str,
        *,
        source: str | None = None,
        status_code: int | None = None,
        code: int | str | None = None,
        endpoint: str | None = None,
        retryable: bool | None = None,
        retry_after: float | None = None,
    ) -> None:
        super().__init__(message)
        self.message = message
        self.source = source
        self.status_code = status_code
        self.code = code
        self.endpoint = endpoint
        self.retryable = self.default_retryable if retryable is None else retryable
        self.retry_after = retry_after

    def __str__(self) -> str:
        details = []
        if self.source:
            details.append(f"source={self.source}")
        if self.endpoint:
            details.append(f"endpoint={self.endpoint}")
        if self.status_code is not None:
            details.append(f"http={self.status_code}")
        if self.code is not None and self.code != self.status_code:
            details.append(f"code={self.code}")
        return f"{self.message} ({', '.join(details)})" if details else self.message

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"{type(self).__name__}({self.message!r}, source={self.source!r})"


class HttpAuthError(HttpError):
    """Credentials or an API key were rejected.

    Not retryable, and deliberately *not* recorded against the circuit breaker:
    a wrong key fails identically forever, and letting it trip the breaker would
    stall an upstream that is perfectly healthy.
    """

    default_retryable = False


class HttpNotFound(HttpError):
    """The upstream has no such resource.

    A normal answer for several enrichment sources — the Cover Art Archive says
    404 to mean "no art for this release" — so callers routinely catch this
    rather than treating it as a failure.
    """

    default_retryable = False


class HttpRateLimitError(HttpError):
    """The upstream is throttling us.

    Usually HTTP 429, but MusicBrainz throttles with 503, which is why
    :class:`~app.net.http.JsonHttpClient` lets a subclass declare which statuses
    mean "slow down".
    """

    default_retryable = True


class HttpServerError(HttpError):
    """The upstream returned 5xx. Retryable — these are usually transient."""

    default_retryable = True


class HttpTransportError(HttpError):
    """A network-level failure: DNS, connect, read timeout, reset connection."""

    default_retryable = True
