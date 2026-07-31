"""Typed exceptions raised by the Qobuz client layer.

Every error carries enough context to be logged and surfaced in the UI without
the caller having to re-derive it: the HTTP status, the Qobuz application-level
``code``, the endpoint that failed, whether retrying could plausibly help, and
(for rate limiting) how long to wait.

The ``retryable`` flag is what :mod:`app.qobuz.client` feeds to tenacity, so a
new error class only participates in retries if it opts in.

Nothing here ever embeds the auth token, the app secret or a signed file URL in
its message — callers build messages from endpoint names and Qobuz's own text.
"""

from __future__ import annotations

__all__ = [
    "QobuzError",
    "QobuzAuthError",
    "QobuzRateLimitError",
    "QobuzNotFound",
    "QobuzUnstreamable",
    "QobuzSecretError",
    "QobuzTransportError",
]


class QobuzError(Exception):
    """Base class for every failure originating from the Qobuz API layer.

    Args:
        message: Human-readable description, safe to log and to show in the UI.
        status_code: HTTP status of the offending response, when there was one.
        code: Qobuz's application-level ``code`` field from the error body.
        endpoint: Relative endpoint (e.g. ``"album/get"``) that was called.
        retryable: True when repeating the request could succeed later.
        retry_after: Seconds the server asked us to wait, when it said so.
    """

    #: Default for subclasses that are always (or never) worth retrying.
    default_retryable: bool = False

    def __init__(
        self,
        message: str,
        *,
        status_code: int | None = None,
        code: int | str | None = None,
        endpoint: str | None = None,
        retryable: bool | None = None,
        retry_after: float | None = None,
    ) -> None:
        super().__init__(message)
        self.message = message
        self.status_code = status_code
        self.code = code
        self.endpoint = endpoint
        self.retryable = self.default_retryable if retryable is None else retryable
        self.retry_after = retry_after

    def __str__(self) -> str:
        bits = [self.message]
        details = []
        if self.endpoint:
            details.append(f"endpoint={self.endpoint}")
        if self.status_code is not None:
            details.append(f"http={self.status_code}")
        if self.code is not None and self.code != self.status_code:
            details.append(f"code={self.code}")
        if details:
            bits.append(f"({', '.join(details)})")
        return " ".join(bits)

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return f"{type(self).__name__}({self.message!r}, status_code={self.status_code!r})"


class QobuzAuthError(QobuzError):
    """The credentials were rejected (bad/expired token, wrong app id).

    Not retryable: hammering the endpoint with the same rejected credentials
    only risks the account. The fix is to refresh ``QOBUZ_USER_AUTH_TOKEN``.
    """

    default_retryable = False

    def __init__(self, message: str = "", **kwargs: object) -> None:
        super().__init__(
            message
            or (
                "Qobuz rejected the credentials. Check QOBUZ_APP_ID and "
                "QOBUZ_USER_AUTH_TOKEN in .env — the token expires when you log "
                "out of the Qobuz web player."
            ),
            **kwargs,  # type: ignore[arg-type]
        )


class QobuzRateLimitError(QobuzError):
    """Qobuz answered HTTP 429 (or an equivalent throttling code).

    Always retryable. ``retry_after`` mirrors the ``Retry-After`` header when
    the server supplied one; the client's backoff never waits less than that.
    """

    default_retryable = True

    def __init__(self, message: str = "", **kwargs: object) -> None:
        super().__init__(
            message or "Qobuz is rate limiting us (HTTP 429). Backing off.",
            **kwargs,  # type: ignore[arg-type]
        )


class QobuzNotFound(QobuzError):
    """The requested artist/album/track does not exist or is not visible.

    Common for regionally unavailable releases and for albums that have been
    delisted since we last indexed them.
    """

    default_retryable = False

    def __init__(self, message: str = "", **kwargs: object) -> None:
        super().__init__(
            message or "Qobuz has no such resource (HTTP 404).",
            **kwargs,  # type: ignore[arg-type]
        )


class QobuzUnstreamable(QobuzError):
    """``track/getFileUrl`` succeeded but returned no playable URL.

    Raised for tracks the account may not stream: purchase-only releases,
    geo-restricted content, or a format the subscription does not cover.
    """

    default_retryable = False

    def __init__(self, message: str = "", **kwargs: object) -> None:
        super().__init__(
            message
            or (
                "Qobuz returned no stream URL for this track — it is probably "
                "not streamable with the current account or in this country."
            ),
            **kwargs,  # type: ignore[arg-type]
        )


class QobuzSecretError(QobuzError):
    """The ``app_secret`` could not be derived, or the one we have is wrong.

    The message deliberately tells the user the manual escape hatch, because
    the derivation scrapes the web player bundle and can break when Qobuz ships
    a new one.
    """

    default_retryable = False

    def __init__(self, message: str = "", **kwargs: object) -> None:
        super().__init__(
            message
            or (
                "Could not determine the Qobuz app secret from the web player "
                "bundle. Set QOBUZ_APP_SECRET in .env to work around this."
            ),
            **kwargs,  # type: ignore[arg-type]
        )


class QobuzTransportError(QobuzError):
    """A network-level failure: DNS, connect, read timeout, reset connection.

    Retryable — these are almost always transient.
    """

    default_retryable = True

    def __init__(self, message: str = "", **kwargs: object) -> None:
        super().__init__(
            message or "Network error talking to Qobuz.",
            **kwargs,  # type: ignore[arg-type]
        )
