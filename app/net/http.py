"""A small, credential-free JSON client for third-party HTTP APIs.

:class:`JsonHttpClient` is the shape :class:`app.qobuz.client.QobuzClient` arrived
at, minus everything Qobuz-specific: acquire a slot from the rate limiter, issue
one GET, map the response onto a typed error or decoded JSON, tell the limiter
how it went, and let tenacity retry the ones flagged retryable.

What is deliberately *not* shared with ``QobuzClient``: that client is over a
thousand lines sitting on the download critical path, and the interesting half of
its response handling — ``{"status": "error"}`` sniffing, request-signature
detection, app-secret refresh — means nothing to another API. Folding both into
one ``_handle_response`` would produce a switchboard of per-source flags. The
generic skeleton is forty lines; the sources differ exactly where they should.

Subclass hooks, in the order they matter:

``source``
    Short name used in log lines and error messages.
``rate_limit_statuses``
    Which HTTP statuses mean "slow down". MusicBrainz throttles with **503**,
    not 429, so it widens this.
``check_payload``
    Application-level error detection for upstreams that answer **HTTP 200** with
    an error object in the body. Deezer does exactly that.
``build_headers``
    Extra request headers. The default carries only ``User-Agent`` and
    ``Accept`` — no client ever sends a credential it was not explicitly given,
    and none of these hosts is Qobuz, so the auth token must never reach them.
"""

from __future__ import annotations

import datetime as _dt
import email.utils
import random
from typing import Any, Callable, Mapping

import httpx
from tenacity import AsyncRetrying, RetryCallState, retry_if_exception, stop_after_attempt

from app.config import Settings, get_settings
from app.logging_conf import get_logger
from app.net.errors import (
    HttpAuthError,
    HttpError,
    HttpNotFound,
    HttpRateLimitError,
    HttpServerError,
    HttpTransportError,
)
from app.net.ratelimit import RateLimiter

__all__ = ["JsonHttpClient", "clean_params", "make_wait", "parse_retry_after"]

logger = get_logger(__name__)


# ---------------------------------------------------------------------------
# Pure helpers, shared with the Qobuz client's own pipeline
# ---------------------------------------------------------------------------
def parse_retry_after(value: str | None) -> float | None:
    """Parse a ``Retry-After`` header (delta-seconds or HTTP-date)."""
    if not value:
        return None
    text = value.strip()
    try:
        return max(0.0, float(text))
    except ValueError:
        pass
    try:
        when = email.utils.parsedate_to_datetime(text)
    except (TypeError, ValueError):
        return None
    if when is None:
        return None
    if when.tzinfo is None:
        when = when.replace(tzinfo=_dt.timezone.utc)
    return max(0.0, (when - _dt.datetime.now(_dt.timezone.utc)).total_seconds())


def clean_params(params: Mapping[str, Any] | None) -> dict[str, str]:
    """Drop ``None`` values and stringify the rest (ids must never see ``int``)."""
    if not params:
        return {}
    cleaned: dict[str, str] = {}
    for key, value in params.items():
        if value is None:
            continue
        if isinstance(value, bool):
            cleaned[key] = "true" if value else "false"
        else:
            cleaned[key] = str(value)
    return cleaned


def make_wait(settings: Settings) -> Callable[[RetryCallState], float]:
    """Build an exponential-backoff-with-jitter wait that honours ``Retry-After``."""

    def wait(retry_state: RetryCallState) -> float:
        attempt = max(1, retry_state.attempt_number)
        delay = settings.backoff_initial * (settings.backoff_multiplier ** (attempt - 1))
        delay = min(delay, settings.backoff_max)

        outcome = retry_state.outcome
        exc = outcome.exception() if outcome is not None and outcome.failed else None
        retry_after = getattr(exc, "retry_after", None)
        if retry_after:
            # The server's instruction is a floor, never a ceiling.
            delay = max(delay, float(retry_after))

        jitter = delay * settings.backoff_jitter * random.random()
        return min(delay + jitter, max(settings.backoff_max, float(retry_after or 0.0)))

    return wait


def _is_retryable(exc: BaseException) -> bool:
    """Tenacity predicate: only our own errors marked retryable are retried."""
    return isinstance(exc, HttpError) and exc.retryable


# ---------------------------------------------------------------------------
# Client
# ---------------------------------------------------------------------------
class JsonHttpClient:
    """Rate-limited async GET-and-decode against one JSON API.

    The constructor does no I/O and never raises, so a client is safe to build
    at startup whether or not the upstream is reachable — the same property
    :class:`~app.qobuz.client.QobuzClient` relies on.

    Args:
        limiter: The one :class:`~app.net.ratelimit.RateLimiter` for this
            upstream. One per upstream, shared by every caller of it.
        settings: Overrides ``get_settings()``; supplies the backoff policy.
        http_client: Inject an ``httpx.AsyncClient`` (a ``MockTransport`` in
            tests). An injected client is never closed by :meth:`aclose`.
        user_agent: Overrides :attr:`user_agent`. MusicBrainz requires a
            descriptive one naming the application and a contact.
    """

    #: Short upstream name, used in messages, log lines and error ``source``.
    source: str = "http"
    #: Base URL, with a trailing slash.
    base_url: str = ""
    #: Last-resort ``User-Agent``. Normally unused: the constructor prefers
    #: ``ENRICHMENT_CONTACT``-derived one from settings, because MusicBrainz
    #: requires a contact and a bare product name would be rejected.
    user_agent: str = "Qobuzarr"
    #: Statuses that mean "slow down" rather than "server broken".
    rate_limit_statuses: frozenset[int] = frozenset({429})
    #: Per-request timeout in seconds.
    timeout: float = 30.0

    def __init__(
        self,
        *,
        limiter: RateLimiter,
        settings: Settings | None = None,
        http_client: httpx.AsyncClient | None = None,
        user_agent: str | None = None,
    ) -> None:
        self.limiter = limiter
        self._settings = settings or get_settings()
        self._client = http_client
        self._owns_client = http_client is None
        self._closed = False
        # Settings win over the class default even when nobody passes one, so a
        # client built directly — in a test, a script, a future caller — still
        # identifies itself and its contact. Getting this wrong is not cosmetic:
        # MusicBrainz blocks requests that do not name a contact.
        self.user_agent = user_agent or self._settings.enrichment_user_agent

    # ------------------------------------------------------------------ setup
    def build_headers(self) -> dict[str, str]:
        """Headers for every request. Override to add an API key or Accept type.

        Never add a credential belonging to another upstream. Qobuz's auth token
        in particular must not leave qobuz.com — that is why
        ``QobuzClient.downloader`` exists and why this default is bare.
        """
        return {"User-Agent": self.user_agent, "Accept": "application/json"}

    def _build_client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(
            base_url=self.base_url,
            headers=self.build_headers(),
            timeout=httpx.Timeout(self.timeout),
            # The Cover Art Archive answers with a 307 to archive.org, and httpx
            # does not follow redirects unless asked.
            follow_redirects=True,
        )

    @property
    def client(self) -> httpx.AsyncClient:
        """The lazily built HTTP client."""
        if self._closed:
            raise RuntimeError(f"{type(self).__name__} has been closed")
        if self._client is None:
            self._client = self._build_client()
        return self._client

    async def aclose(self) -> None:
        """Close the HTTP client, unless it was injected by the caller."""
        self._closed = True
        if self._client is not None and self._owns_client:
            await self._client.aclose()
        self._client = None

    async def __aenter__(self) -> "JsonHttpClient":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.aclose()

    # --------------------------------------------------------------- requests
    async def get_json(
        self,
        path: str,
        params: Mapping[str, Any] | None = None,
        *,
        max_attempts: int | None = None,
    ) -> Any:
        """Rate-limited GET with retries on throttling, 5xx and network errors.

        Args:
            path: Path relative to :attr:`base_url`.
            params: Query parameters; ``None`` values are dropped.
            max_attempts: Override for ``REQUEST_MAX_RETRIES``.

        Raises:
            HttpError: Or a subclass, once retries are exhausted.
        """
        attempts = max(
            1,
            max_attempts
            if max_attempts is not None
            else self._settings.request_max_retries,
        )
        async for attempt in AsyncRetrying(
            stop=stop_after_attempt(attempts),
            wait=make_wait(self._settings),
            retry=retry_if_exception(_is_retryable),
            reraise=True,
        ):
            with attempt:
                return await self._request_once(path, params)
        raise HttpError(  # pragma: no cover - AsyncRetrying always returns or raises
            f"Exhausted retries calling {path}", source=self.source, endpoint=path
        )

    async def _request_once(self, path: str, params: Mapping[str, Any] | None) -> Any:
        """Issue exactly one rate-limited GET and decode the response.

        The circuit-breaker epoch returned by ``acquire()`` travels with the
        request, so an outcome that lands after a *later* request tripped the
        breaker cannot clear that cooldown.
        """
        epoch = await self.limiter.acquire()
        try:
            response = await self.client.get(path, params=clean_params(params))
        except httpx.TimeoutException as exc:
            self.limiter.record_failure(epoch=epoch)
            raise HttpTransportError(
                f"Timed out calling {self.source} {path}",
                source=self.source,
                endpoint=path,
            ) from exc
        except httpx.HTTPError as exc:
            self.limiter.record_failure(epoch=epoch)
            raise HttpTransportError(
                f"Network error calling {self.source} {path}: {type(exc).__name__}",
                source=self.source,
                endpoint=path,
            ) from exc
        return self._handle_response(path, response, epoch=epoch)

    def _handle_response(
        self, path: str, response: httpx.Response, *, epoch: int | None = None
    ) -> Any:
        """Turn an HTTP response into decoded JSON or a typed error."""
        status = response.status_code
        retry_after = parse_retry_after(response.headers.get("Retry-After"))
        kwargs: dict[str, Any] = {
            "source": self.source,
            "status_code": status,
            "endpoint": path,
        }

        if status in self.rate_limit_statuses:
            self.limiter.record_failure(
                is_rate_limit=True, retry_after=retry_after, epoch=epoch
            )
            raise HttpRateLimitError(
                f"{self.source} is throttling us (HTTP {status}).",
                retry_after=retry_after,
                **kwargs,
            )

        if status >= 500:
            self.limiter.record_failure(retry_after=retry_after, epoch=epoch)
            raise HttpServerError(
                f"{self.source} server error (HTTP {status}).",
                retry_after=retry_after,
                **kwargs,
            )

        # Auth failures are never recorded against the breaker: a wrong key fails
        # identically forever, and stalling a healthy upstream helps nobody.
        if status in (401, 403):
            raise HttpAuthError(f"{self.source} rejected the request.", **kwargs)

        if status == 404:
            raise HttpNotFound(f"{self.source} has no such resource.", **kwargs)

        if status >= 400:
            raise HttpError(
                f"{self.source} rejected the request (HTTP {status}).", **kwargs
            )

        try:
            body = response.json()
        except ValueError:
            self.limiter.record_failure(epoch=epoch)
            raise HttpServerError(
                f"{self.source} returned a non-JSON body for {path}.", **kwargs
            ) from None

        try:
            payload = self.check_payload(path, body, status=status)
        except HttpError as exc:
            if exc.retryable:
                self.limiter.record_failure(
                    is_rate_limit=isinstance(exc, HttpRateLimitError),
                    retry_after=exc.retry_after,
                    epoch=epoch,
                )
            else:
                # A clean "no such album" is a healthy round trip, not a fault.
                self.limiter.record_success(epoch)
            raise

        self.limiter.record_success(epoch)
        return payload

    def check_payload(self, path: str, body: Any, *, status: int) -> Any:
        """Inspect a 2xx body for an application-level error. Override per source.

        Several of these APIs answer **HTTP 200** with an error object — Deezer
        returns ``{"error": {"type": "DataException"}}`` for a miss and for a
        quota breach alike. A client that only read status codes would record
        both as successes and feed the circuit breaker the wrong signal.

        Return the payload to accept it, or raise an :class:`HttpError` subclass.
        """
        return body
