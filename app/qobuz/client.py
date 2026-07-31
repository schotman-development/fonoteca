"""Async Qobuz API client.

One :class:`QobuzClient` instance is shared by the whole application. Every
outbound call — indexer, UI search and downloads alike — passes through the same
:class:`~app.qobuz.ratelimit.RateLimiter`, so the configured minimum interval
and hourly ceiling are global guarantees rather than per-component wishes.

Failure handling, in layers:

1. ``tenacity`` retries only errors flagged ``retryable`` (429, 5xx, network),
   with exponential backoff plus jitter, never waiting less than a supplied
   ``Retry-After``.
2. Each 429/5xx also feeds the rate limiter's circuit breaker, which pauses
   *all* traffic once failures pile up.
3. Everything else is raised immediately as a typed
   :mod:`app.qobuz.errors` exception.

Secrets hygiene: the auth token is sent as a header (never logged), the derived
app secret is registered for redaction as soon as it is known, and signed file
URLs are never written to a log record. Downloads use a *separate* HTTP client
that carries no credentials at all, so the token is never sent to Qobuz's CDN.
"""

from __future__ import annotations

import asyncio
import email.utils
import inspect
import random
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, AsyncIterator, Callable, Iterable, Mapping, Sequence

import httpx
from tenacity import AsyncRetrying, RetryCallState, retry_if_exception, stop_after_attempt

from app.config import FORMAT_IDS, Settings, get_settings
from app.logging_conf import get_logger
from app.qobuz.errors import (
    QobuzAuthError,
    QobuzError,
    QobuzNotFound,
    QobuzRateLimitError,
    QobuzSecretError,
    QobuzTransportError,
    QobuzUnstreamable,
)
from app.qobuz.ratelimit import RateLimiter
from app.qobuz.secrets import (
    FALLBACK_TRACK_ID,
    DerivationCooldown,
    load_cached_secret,
    resolve_app_secret as _resolve_app_secret,
    sign,
)

__all__ = ["QobuzClient", "ProgressCallback"]

logger = get_logger(__name__)

#: ``on_progress(bytes_written, total_bytes_or_None)``; may be sync or async.
ProgressCallback = Callable[[int, int | None], Any]

#: ``catalog/search`` result keys, plus the singular aliases callers may pass.
_SEARCH_TYPES: dict[str, str] = {
    "album": "albums",
    "albums": "albums",
    "artist": "artists",
    "artists": "artists",
    "track": "tracks",
    "tracks": "tracks",
    "playlist": "playlists",
    "playlists": "playlists",
}

#: Normalised release type -> the ``release_type`` value ``artist/getReleasesList``
#: understands. Qobuz has no separate "single" bucket; EPs and singles share one.
_RELEASE_TYPE_PARAMS: dict[str, str] = {
    "album": "album",
    "ep": "epSingle",
    "single": "epSingle",
    "live": "live",
    "compilation": "compilation",
    "download": "download",
}

#: What ``iter_artist_albums`` sweeps when the caller does not narrow it down.
_DEFAULT_RELEASE_TYPE_PARAMS: tuple[str, ...] = (
    "album",
    "epSingle",
    "live",
    "compilation",
)

#: Endpoints whose failure should never be retried into a loop.
_LOGIN_ENDPOINT = "user/login"

#: Format used when validating an app-secret candidate (available to all tiers).
_VALIDATION_FORMAT_ID = 5


# ---------------------------------------------------------------------------
# Retry helpers
# ---------------------------------------------------------------------------
def _is_retryable(exc: BaseException) -> bool:
    """Tenacity predicate: only our own errors marked retryable are retried."""
    return isinstance(exc, QobuzError) and exc.retryable


def _make_wait(settings: Settings) -> Callable[[RetryCallState], float]:
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
        total = min(delay + jitter, max(settings.backoff_max, float(retry_after or 0.0)))
        logger.debug("Backing off %.1fs before retry %d", total, attempt + 1)
        return total

    return wait


def _parse_retry_after(value: str | None) -> float | None:
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
        when = when.replace(tzinfo=timezone.utc)
    return max(0.0, (when - datetime.now(timezone.utc)).total_seconds())


def _clean_params(params: Mapping[str, Any] | None) -> dict[str, str]:
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


# ---------------------------------------------------------------------------
# Client
# ---------------------------------------------------------------------------
class QobuzClient:
    """Rate-limited async wrapper around the Qobuz JSON API.

    Args:
        settings: Injected settings (defaults to :func:`app.config.get_settings`).
        limiter: Shared rate limiter. One is created from settings when omitted;
            pass the *same* instance to every component that talks to Qobuz.
        http_client: Pre-built API client, for tests (an ``httpx.MockTransport``
            fits here). Its ``base_url`` and headers are used as-is.
        download_client: Pre-built credential-free client for file downloads.

    The client is lazy: constructing it performs no I/O and never raises, so it
    is safe to build at import/startup time even without credentials. The first
    network call validates that credentials exist.
    """

    def __init__(
        self,
        settings: Settings | None = None,
        *,
        limiter: RateLimiter | None = None,
        http_client: httpx.AsyncClient | None = None,
        download_client: httpx.AsyncClient | None = None,
    ) -> None:
        self._settings = settings or get_settings()
        self.limiter = limiter or RateLimiter.from_settings(self._settings)

        self._api = http_client
        self._owns_api = http_client is None
        self._downloader = download_client
        self._owns_downloader = download_client is None

        self._app_secret: str | None = None
        self._secret_source: str = "unresolved"
        self._secret_lock = asyncio.Lock()
        # Negative cache: a failed derivation is not retried in a hot loop.
        self._secret_cooldown = DerivationCooldown(
            float(self._settings.circuit_breaker_cooldown)
        )

        self._user: dict[str, Any] = {}
        self._credential_parameters: dict[str, Any] = {}
        self._logged_in = False
        self._validation_track_id: str = FALLBACK_TRACK_ID
        self._closed = False

    # ------------------------------------------------------------- lifecycle
    async def __aenter__(self) -> "QobuzClient":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.aclose()

    def _build_api_client(self) -> httpx.AsyncClient:
        """Create the authenticated API client."""
        self._settings.require_credentials()
        return httpx.AsyncClient(
            base_url=self._settings.qobuz_api_base,
            headers={
                "X-App-Id": self._settings.qobuz_app_id,
                "X-User-Auth-Token": self._settings.qobuz_user_auth_token,
                "User-Agent": self._settings.qobuz_user_agent,
                "Accept": "application/json",
            },
            timeout=httpx.Timeout(self._settings.qobuz_request_timeout),
            follow_redirects=True,
        )

    def _build_download_client(self) -> httpx.AsyncClient:
        """Create the credential-free client used for CDN downloads.

        Signed file URLs point at a CDN host; sending ``X-User-Auth-Token``
        there would leak the credential to a third party, so this client only
        carries a browser User-Agent.
        """
        return httpx.AsyncClient(
            headers={"User-Agent": self._settings.qobuz_user_agent},
            timeout=httpx.Timeout(self._settings.download_timeout, connect=30.0),
            follow_redirects=True,
        )

    @property
    def api(self) -> httpx.AsyncClient:
        """The authenticated API client, created on first use."""
        if self._closed:
            raise RuntimeError("QobuzClient has been closed")
        if self._api is None:
            self._api = self._build_api_client()
        return self._api

    @property
    def downloader(self) -> httpx.AsyncClient:
        """The credential-free download client, created on first use."""
        if self._closed:
            raise RuntimeError("QobuzClient has been closed")
        if self._downloader is None:
            self._downloader = self._build_download_client()
        return self._downloader

    async def aclose(self) -> None:
        """Close both underlying HTTP clients. Safe to call more than once."""
        self._closed = True
        if self._api is not None and self._owns_api:
            await self._api.aclose()
        if self._downloader is not None and self._owns_downloader:
            await self._downloader.aclose()
        self._api = None if self._owns_api else self._api
        self._downloader = None if self._owns_downloader else self._downloader

    # --------------------------------------------------------------- plumbing
    async def _request_once(
        self, endpoint: str, params: Mapping[str, Any] | None = None
    ) -> Any:
        """Issue exactly one rate-limited GET and decode the response.

        The circuit-breaker epoch returned by ``acquire()`` travels with the
        request, so an outcome that lands after a *later* request tripped the
        breaker cannot clear that cooldown.
        """
        epoch = await self.limiter.acquire()
        try:
            response = await self.api.get(endpoint, params=_clean_params(params))
        except httpx.TimeoutException as exc:
            self.limiter.record_failure(epoch=epoch)
            raise QobuzTransportError(
                f"Timed out calling Qobuz {endpoint}", endpoint=endpoint
            ) from exc
        except httpx.HTTPError as exc:
            self.limiter.record_failure(epoch=epoch)
            raise QobuzTransportError(
                f"Network error calling Qobuz {endpoint}: {type(exc).__name__}",
                endpoint=endpoint,
            ) from exc
        return self._handle_response(endpoint, response, epoch=epoch)

    def _handle_response(
        self, endpoint: str, response: httpx.Response, *, epoch: int | None = None
    ) -> Any:
        """Turn an HTTP response into decoded JSON or a typed error."""
        status = response.status_code
        body: Any = None
        try:
            body = response.json()
        except ValueError:
            body = None

        message, code = self._error_details(body)

        if status == 429:
            retry_after = _parse_retry_after(response.headers.get("Retry-After"))
            self.limiter.record_failure(
                is_rate_limit=True, retry_after=retry_after, epoch=epoch
            )
            raise QobuzRateLimitError(
                message or "Qobuz is rate limiting us (HTTP 429).",
                status_code=status,
                code=code,
                endpoint=endpoint,
                retry_after=retry_after,
            )

        if status >= 500:
            retry_after = _parse_retry_after(response.headers.get("Retry-After"))
            self.limiter.record_failure(retry_after=retry_after, epoch=epoch)
            raise QobuzError(
                message or f"Qobuz server error (HTTP {status}).",
                status_code=status,
                code=code,
                endpoint=endpoint,
                retryable=True,
                retry_after=retry_after,
            )

        if status in (401, 403):
            raise QobuzAuthError(message or "", status_code=status, code=code, endpoint=endpoint)

        if status == 404:
            raise QobuzNotFound(message or "", status_code=status, code=code, endpoint=endpoint)

        if status >= 400:
            if message and "signature" in message.lower():
                raise QobuzSecretError(
                    f"Qobuz rejected the request signature on {endpoint} — the "
                    "app secret is stale or wrong.",
                    status_code=status,
                    code=code,
                    endpoint=endpoint,
                )
            raise QobuzError(
                message or f"Qobuz rejected the request (HTTP {status}).",
                status_code=status,
                code=code,
                endpoint=endpoint,
            )

        if body is None:
            self.limiter.record_failure(epoch=epoch)
            raise QobuzError(
                f"Qobuz returned a non-JSON body for {endpoint}.",
                status_code=status,
                endpoint=endpoint,
                retryable=True,
            )

        # Some endpoints answer HTTP 200 with an application-level error.
        if isinstance(body, Mapping) and str(body.get("status", "")).lower() == "error":
            raise self._error_from_body(endpoint, body, status, epoch=epoch)

        self.limiter.record_success(epoch)
        return body

    @staticmethod
    def _error_details(body: Any) -> tuple[str | None, int | str | None]:
        """Extract ``(message, code)`` from a Qobuz error body."""
        if not isinstance(body, Mapping):
            return None, None
        message = body.get("message") or body.get("error") or body.get("error_description")
        code = body.get("code")
        return (str(message) if message else None), code

    def _error_from_body(
        self,
        endpoint: str,
        body: Mapping[str, Any],
        status: int,
        *,
        epoch: int | None = None,
    ) -> QobuzError:
        """Map an application-level ``{"status":"error"}`` body onto an exception."""
        message, code = self._error_details(body)
        numeric = code if isinstance(code, int) else None
        kwargs: dict[str, Any] = {"status_code": status, "code": code, "endpoint": endpoint}
        if numeric in (401, 403):
            return QobuzAuthError(message or "", **kwargs)
        if numeric == 404:
            return QobuzNotFound(message or "", **kwargs)
        if numeric == 429:
            self.limiter.record_failure(is_rate_limit=True, epoch=epoch)
            return QobuzRateLimitError(message or "", **kwargs)
        if numeric is not None and numeric >= 500:
            self.limiter.record_failure(epoch=epoch)
            return QobuzError(message or "", retryable=True, **kwargs)
        return QobuzError(message or f"Qobuz reported an error on {endpoint}.", **kwargs)

    async def _request(
        self,
        endpoint: str,
        params: Mapping[str, Any] | None = None,
        *,
        max_attempts: int | None = None,
    ) -> Any:
        """Rate-limited GET with retries on 429/5xx/network errors.

        Args:
            endpoint: Relative endpoint, e.g. ``"album/get"``.
            params: Query parameters; ``None`` values are dropped.
            max_attempts: Override for ``REQUEST_MAX_RETRIES``.

        Raises:
            QobuzError: Or one of its subclasses, after retries are exhausted.
        """
        attempts = max(1, max_attempts if max_attempts is not None else self._settings.request_max_retries)
        async for attempt in AsyncRetrying(
            stop=stop_after_attempt(attempts),
            wait=_make_wait(self._settings),
            retry=retry_if_exception(_is_retryable),
            reraise=True,
        ):
            with attempt:
                return await self._request_once(endpoint, params)
        raise QobuzError(  # pragma: no cover - AsyncRetrying always returns or raises
            f"Exhausted retries calling {endpoint}", endpoint=endpoint
        )

    async def _signed_request(
        self,
        endpoint: str,
        params: Mapping[str, Any],
        secret: str,
        *,
        max_attempts: int | None = None,
    ) -> Any:
        """Sign *params* with *secret* and issue the request."""
        ts = int(time.time())
        signed = dict(params)
        signed["request_ts"] = ts
        signed["request_sig"] = sign(endpoint, params, ts, secret)
        return await self._request(endpoint, signed, max_attempts=max_attempts)

    # ----------------------------------------------------------------- secret
    @property
    def app_secret_source(self) -> str:
        """Where the current secret came from: env, cache, derived, unresolved."""
        return self._secret_source

    @property
    def has_app_secret(self) -> bool:
        """True once an app secret has been resolved in this process."""
        return bool(self._app_secret)

    async def _validate_secret_candidate(self, candidate: str) -> bool:
        """Probe ``track/getFileUrl`` with *candidate*; True when it works."""
        params = {
            "format_id": _VALIDATION_FORMAT_ID,
            "intent": "stream",
            "track_id": str(self._validation_track_id),
        }
        try:
            payload = await self._signed_request(
                "track/getFileUrl", params, candidate, max_attempts=2
            )
        except QobuzError as exc:
            logger.debug("App-secret candidate rejected: %s", exc)
            return False
        return isinstance(payload, Mapping) and bool(payload.get("url"))

    async def resolve_app_secret(self, *, force_refresh: bool = False) -> str:
        """Return the app secret, deriving and validating it on first use.

        Args:
            force_refresh: Ignore the environment override, the cache and any
                in-memory value, and re-derive from the web player. Used after
                Qobuz rejects a signature.

        Raises:
            QobuzSecretError: when no candidate could be validated.
        """
        async with self._secret_lock:
            if self._app_secret and not force_refresh:
                return self._app_secret

            previous = self._app_secret
            self._app_secret = None
            configured = (self._settings.qobuz_app_secret or "").strip()
            cached = None if force_refresh else load_cached_secret(self._settings)

            secret = await _resolve_app_secret(
                self.downloader,
                self._validate_secret_candidate,
                settings=self._settings,
                force_refresh=force_refresh,
                extra_candidates=[previous] if (force_refresh and previous) else None,
                limiter=self.limiter,
                cooldown=self._secret_cooldown,
            )

            self._app_secret = secret
            if not force_refresh and configured and secret == configured:
                self._secret_source = "env"
            elif not force_refresh and cached and secret == cached:
                self._secret_source = "cache"
            else:
                self._secret_source = "derived"
            return secret

    # ------------------------------------------------------------------ login
    async def login(self) -> dict[str, Any]:
        """Validate the credentials and cache the account's entitlements.

        Returns:
            The raw ``user/login`` payload.

        Raises:
            QobuzAuthError: when Qobuz rejects the app id or auth token.
        """
        payload = await self._request(
            _LOGIN_ENDPOINT,
            {
                "app_id": self._settings.qobuz_app_id,
                "user_auth_token": self._settings.qobuz_user_auth_token,
            },
        )
        if not isinstance(payload, Mapping):
            raise QobuzAuthError("Unexpected user/login response.", endpoint=_LOGIN_ENDPOINT)

        user = payload.get("user")
        self._user = dict(user) if isinstance(user, Mapping) else dict(payload)
        credential = self._user.get("credential")
        parameters = credential.get("parameters") if isinstance(credential, Mapping) else None
        self._credential_parameters = dict(parameters) if isinstance(parameters, Mapping) else {}
        self._logged_in = True

        logger.info(
            "Qobuz login OK (user_id=%s country=%s formats=%s)",
            self._user.get("id"),
            self._user.get("country_code"),
            self.allowed_format_ids(),
        )
        return dict(payload)

    @property
    def user(self) -> dict[str, Any]:
        """The cached ``user`` object from the last :meth:`login` (may be empty)."""
        return self._user

    @property
    def logged_in(self) -> bool:
        """True once :meth:`login` has succeeded in this process."""
        return self._logged_in

    def allowed_format_ids(self) -> list[int]:
        """Format ids the account may stream, ascending.

        Derived from the ``credential.parameters`` cached by :meth:`login`. When
        the account has not been probed yet, every known format is returned:
        Qobuz silently downgrades an over-ambitious request, and callers are
        required to trust the ``format_id`` that ``getFileUrl`` returns anyway.
        """
        if not self._logged_in:
            return list(FORMAT_IDS)

        allowed = [5]
        params = self._credential_parameters
        if params.get("lossless_streaming"):
            allowed.append(6)
        if params.get("hires_streaming") or params.get("hires_purchases_streaming"):
            allowed.extend((7, 27))
        return sorted(set(allowed))

    def best_format_id(self, preferred: int | None = None) -> int:
        """Best allowed format not exceeding *preferred* (or the account's max)."""
        target = int(preferred or self._settings.default_format_id)
        allowed = self.allowed_format_ids()
        eligible = [fid for fid in allowed if fid <= target]
        return max(eligible) if eligible else min(allowed)

    # ----------------------------------------------------------------- search
    async def search(
        self,
        query: str,
        type: str = "albums",
        limit: int = 25,
        offset: int = 0,
    ) -> dict[str, Any]:
        """Search the Qobuz catalogue.

        Args:
            query: Free-text query.
            type: ``"albums"``, ``"artists"``, ``"tracks"``, ``"playlists"`` —
                or ``"all"``/``None`` for the whole payload. Filtering happens
                client-side: ``catalog/search`` returns every bucket in one
                request, so narrowing costs nothing extra.
            limit: Page size (Qobuz caps this at 500).
            offset: Page offset.

        Returns:
            For a specific type, that bucket: ``{"limit","offset","total","items"}``
            (an empty bucket when Qobuz omitted it). For ``"all"``, the full
            response payload.
        """
        payload = await self._request(
            "catalog/search",
            {"query": query, "limit": limit, "offset": offset},
        )
        if not isinstance(payload, Mapping):
            return {"limit": limit, "offset": offset, "total": 0, "items": []}

        if type is None or str(type).lower() in ("all", "*"):
            return dict(payload)

        key = _SEARCH_TYPES.get(str(type).lower())
        if key is None:
            raise ValueError(
                f"Unknown search type {type!r}; expected one of "
                f"{sorted(set(_SEARCH_TYPES.values()))} or 'all'."
            )
        bucket = payload.get(key)
        if isinstance(bucket, Mapping):
            return dict(bucket)
        return {"limit": limit, "offset": offset, "total": 0, "items": []}

    async def search_artists(
        self, query: str, limit: int = 25, offset: int = 0
    ) -> list[dict[str, Any]]:
        """Search for artists; returns the raw artist items (possibly empty)."""
        bucket = await self.search(query, type="artists", limit=limit, offset=offset)
        return _items(bucket)

    async def search_albums(
        self, query: str, limit: int = 25, offset: int = 0
    ) -> list[dict[str, Any]]:
        """Search for albums; returns the raw album items (possibly empty)."""
        bucket = await self.search(query, type="albums", limit=limit, offset=offset)
        return _items(bucket)

    # ---------------------------------------------------------------- artists
    async def get_artist(
        self,
        artist_id: str | int,
        with_albums: bool = True,
        limit: int = 100,
        offset: int = 0,
    ) -> dict[str, Any]:
        """Fetch one artist, optionally with a page of their albums.

        Args:
            artist_id: Qobuz artist id (coerced to ``str``, never ``int``).
            with_albums: Request ``extra=albums``.
            limit: Album page size.
            offset: Album page offset.
        """
        params: dict[str, Any] = {"artist_id": str(artist_id)}
        if with_albums:
            params.update({"extra": "albums", "limit": limit, "offset": offset})
        payload = await self._request("artist/get", params)
        if not isinstance(payload, Mapping):
            raise QobuzError(
                f"Unexpected artist/get response for artist {artist_id}",
                endpoint="artist/get",
            )
        return dict(payload)

    @staticmethod
    def _release_type_params(release_types: Iterable[str] | None) -> list[str]:
        """Map normalised release types onto ``getReleasesList`` parameters."""
        if not release_types:
            return list(_DEFAULT_RELEASE_TYPE_PARAMS)
        mapped: list[str] = []
        for value in release_types:
            param = _RELEASE_TYPE_PARAMS.get(str(value).strip().lower())
            if param and param not in mapped:
                mapped.append(param)
        return mapped or list(_DEFAULT_RELEASE_TYPE_PARAMS)

    async def _fetch_releases_page(
        self, artist_id: str, release_type: str, limit: int, offset: int
    ) -> tuple[list[dict[str, Any]], bool]:
        """One page of ``artist/getReleasesList``; returns ``(items, has_more)``."""
        payload = await self._request(
            "artist/getReleasesList",
            {
                "artist_id": str(artist_id),
                "release_type": release_type,
                "limit": limit,
                "offset": offset,
                "sort": "release_date",
            },
        )
        if not isinstance(payload, Mapping) or "items" not in payload:
            raise QobuzError(
                "artist/getReleasesList returned an unexpected payload shape",
                endpoint="artist/getReleasesList",
            )
        items = [item for item in (payload.get("items") or []) if isinstance(item, Mapping)]
        return [dict(item) for item in items], bool(payload.get("has_more"))

    async def iter_artist_albums(
        self,
        artist_id: str | int,
        release_types: Iterable[str] | None = None,
    ) -> AsyncIterator[dict[str, Any]]:
        """Yield every release of an artist, one rate-limited page at a time.

        Prefers the newer ``artist/getReleasesList`` (queried once per release
        type, paginated with ``has_more``) and falls back to
        ``artist/get?extra=albums`` when that endpoint errors or returns a shape
        we do not recognise — older catalogue entries only appear there.

        Args:
            artist_id: Qobuz artist id.
            release_types: Normalised types (``"album"``, ``"ep"``, ``"live"``…)
                to sweep. ``None`` sweeps albums, EPs/singles, live and
                compilations.

        Yields:
            Raw album payloads, de-duplicated by id across every page and type.
            Feed them to :func:`app.qobuz.mapper.map_album`.

        Note:
            This is a generator: nothing is fetched until it is iterated, and
            abandoning the iteration early stops the requests.
        """
        artist = str(artist_id)
        page_size = max(1, self._settings.indexer_page_size)
        max_pages = max(1, self._settings.indexer_max_pages)
        seen: set[str] = set()
        endpoint_ok = False

        for release_type in self._release_type_params(release_types):
            offset = 0
            for _ in range(max_pages):
                try:
                    items, has_more = await self._fetch_releases_page(
                        artist, release_type, page_size, offset
                    )
                except QobuzNotFound:
                    # This artist simply has nothing of this type.
                    endpoint_ok = True
                    break
                except QobuzError as exc:
                    logger.warning(
                        "artist/getReleasesList failed for artist %s (%s): %s",
                        artist,
                        release_type,
                        exc,
                    )
                    break

                endpoint_ok = True
                for item in items:
                    album_id = str(item.get("id") or "").strip()
                    if not album_id or album_id in seen:
                        continue
                    seen.add(album_id)
                    yield item

                if not has_more or not items:
                    break
                offset += page_size

        if endpoint_ok:
            return

        logger.info(
            "Falling back to artist/get?extra=albums for artist %s", artist
        )
        async for item in self._iter_artist_get_albums(artist, seen):
            yield item

    async def _iter_artist_get_albums(
        self, artist_id: str, seen: set[str]
    ) -> AsyncIterator[dict[str, Any]]:
        """Paginate ``artist/get?extra=albums`` (the compatibility path)."""
        page_size = max(1, self._settings.indexer_page_size)
        max_pages = max(1, self._settings.indexer_max_pages)
        offset = 0

        for _ in range(max_pages):
            payload = await self.get_artist(
                artist_id, with_albums=True, limit=page_size, offset=offset
            )
            albums = payload.get("albums")
            if not isinstance(albums, Mapping):
                return
            items = [item for item in (albums.get("items") or []) if isinstance(item, Mapping)]
            for item in items:
                album_id = str(item.get("id") or "").strip()
                if not album_id or album_id in seen:
                    continue
                seen.add(album_id)
                yield dict(item)

            total = albums.get("total")
            offset += page_size
            if not items or (isinstance(total, int) and offset >= total):
                return

    async def get_favorite_artists(self, limit: int = 50) -> list[dict[str, Any]]:
        """Return every artist favourited on the Qobuz account.

        Paginates ``favorite/getUserFavorites?type=artists`` until the reported
        total is reached (or the pages stop yielding anything).
        """
        artists: list[dict[str, Any]] = []
        offset = 0
        page_size = max(1, limit)

        for _ in range(max(1, self._settings.indexer_max_pages)):
            payload = await self._request(
                "favorite/getUserFavorites",
                {"type": "artists", "limit": page_size, "offset": offset},
            )
            bucket = payload.get("artists") if isinstance(payload, Mapping) else None
            items = _items(bucket)
            artists.extend(items)

            total = bucket.get("total") if isinstance(bucket, Mapping) else None
            offset += page_size
            if not items or (isinstance(total, int) and offset >= total):
                break
        return artists

    async def add_favorite_artist(self, artist_id: str | int) -> bool:
        """Favourite an artist on Qobuz (optional server-side sync)."""
        payload = await self._request("favorite/create", {"artist_ids": str(artist_id)})
        return _truthy_status(payload)

    async def remove_favorite_artist(self, artist_id: str | int) -> bool:
        """Un-favourite an artist on Qobuz (optional server-side sync)."""
        payload = await self._request("favorite/delete", {"artist_ids": str(artist_id)})
        return _truthy_status(payload)

    # ----------------------------------------------------------------- albums
    async def get_album(self, album_id: str) -> dict[str, Any]:
        """Fetch one album including its full track list.

        Album ids are opaque strings (``"uyej1o165e870"``) — they are passed
        through untouched. When Qobuz truncates the embedded track list (box
        sets), the remaining pages are fetched and merged.
        """
        payload = await self._request(
            "album/get",
            {"album_id": str(album_id), "extra": "albumsFromSameArtist"},
        )
        if not isinstance(payload, Mapping):
            raise QobuzError(
                f"Unexpected album/get response for album {album_id}", endpoint="album/get"
            )
        album = dict(payload)

        tracks = album.get("tracks")
        if not isinstance(tracks, Mapping):
            return album

        items = [item for item in (tracks.get("items") or []) if isinstance(item, Mapping)]
        total = tracks.get("total")
        expected = total if isinstance(total, int) else album.get("tracks_count")
        if not isinstance(expected, int) or len(items) >= expected or not items:
            return album

        page_size = len(items)
        offset = page_size
        for _ in range(max(1, self._settings.indexer_max_pages)):
            if len(items) >= expected:
                break
            page = await self._request(
                "album/get",
                {"album_id": str(album_id), "limit": page_size, "offset": offset},
            )
            page_tracks = page.get("tracks") if isinstance(page, Mapping) else None
            page_items = _items(page_tracks)
            if not page_items:
                break
            items.extend(page_items)
            offset += page_size

        album["tracks"] = {**dict(tracks), "items": items}
        return album

    async def get_track(self, track_id: str | int) -> dict[str, Any]:
        """Fetch one track's metadata."""
        payload = await self._request("track/get", {"track_id": str(track_id)})
        if not isinstance(payload, Mapping):
            raise QobuzError(
                f"Unexpected track/get response for track {track_id}", endpoint="track/get"
            )
        return dict(payload)

    # ------------------------------------------------------------- file URLs
    async def get_file_url(
        self, track_id: str | int, format_id: int | None = None
    ) -> dict[str, Any]:
        """Resolve a signed, time-limited stream URL for one track.

        Args:
            track_id: Qobuz track id.
            format_id: Desired format (5/6/7/27). Defaults to
                ``DEFAULT_FORMAT_ID``. Qobuz **silently downgrades** a request
                the subscription does not cover, so callers must tag and name
                files from the ``format_id``/``bit_depth``/``sampling_rate``
                fields of the returned payload, not from what they asked for.

        Returns:
            The raw payload: ``{"url","format_id","mime_type","bit_depth",
            "sampling_rate",...}``.

        Raises:
            QobuzUnstreamable: when Qobuz returns no URL for this track.
            QobuzSecretError: when no working app secret could be established.
        """
        target = int(format_id) if format_id else int(self._settings.default_format_id)
        params = {
            "format_id": target,
            "intent": "stream",
            "track_id": str(track_id),
        }

        secret = await self.resolve_app_secret()
        try:
            payload = await self._signed_request("track/getFileUrl", params, secret)
        except QobuzSecretError as exc:
            # Only a *signature* rejection means the secret is stale. A 401/403
            # is the user's auth token, and re-scraping the web player cannot
            # fix that — it would just hammer play.qobuz.com once per track.
            logger.warning(
                "Signed request rejected (%s); re-deriving the Qobuz app secret", exc
            )
            secret = await self.resolve_app_secret(force_refresh=True)
            payload = await self._signed_request("track/getFileUrl", params, secret)

        if not isinstance(payload, Mapping):
            raise QobuzUnstreamable(
                f"Unexpected getFileUrl response for track {track_id}",
                endpoint="track/getFileUrl",
            )

        url = payload.get("url")
        if not url:
            detail = payload.get("restrictions") or payload.get("message") or ""
            raise QobuzUnstreamable(
                f"Track {track_id} is not streamable at format {target}"
                + (f" ({detail})" if detail else ""),
                endpoint="track/getFileUrl",
            )
        return dict(payload)

    async def stream_to_file(
        self,
        url: str,
        dest: Path,
        on_progress: ProgressCallback | None = None,
    ) -> int:
        """Download *url* to *dest* in chunks, returning the bytes written.

        The destination's parent directory is created, and the file is
        truncated at the start of every attempt (the download restarts from
        scratch on retry rather than resuming). On final failure the partial
        file is removed so no half-written audio is left behind.

        Writes are pushed to a worker thread so a slow disk cannot stall the
        event loop. ``on_progress`` may be a plain or async callable and is
        invoked as ``on_progress(bytes_written, total_or_None)``.

        Args:
            url: A signed file URL from :meth:`get_file_url`. Never logged.
            dest: Destination path.
            on_progress: Optional progress callback.

        Returns:
            Number of bytes written.

        Raises:
            QobuzError: on HTTP or network failure after retries.
        """
        dest = Path(dest)
        dest.parent.mkdir(parents=True, exist_ok=True)
        attempts = max(1, self._settings.request_max_retries)

        try:
            async for attempt in AsyncRetrying(
                stop=stop_after_attempt(attempts),
                wait=_make_wait(self._settings),
                retry=retry_if_exception(_is_retryable),
                reraise=True,
            ):
                with attempt:
                    return await self._stream_once(url, dest, on_progress)
        except QobuzError:
            try:
                dest.unlink(missing_ok=True)
            except OSError:  # pragma: no cover - best effort cleanup
                pass
            raise
        raise QobuzError("Download retries exhausted")  # pragma: no cover

    async def _stream_once(
        self, url: str, dest: Path, on_progress: ProgressCallback | None
    ) -> int:
        """One download attempt. Truncates *dest* before writing."""
        epoch = await self.limiter.acquire()
        chunk_size = max(8192, self._settings.download_chunk_size)
        written = 0

        try:
            async with self.downloader.stream("GET", url) as response:
                if response.status_code >= 400:
                    await response.aread()
                    retry_after = _parse_retry_after(response.headers.get("Retry-After"))
                    retryable = response.status_code == 429 or response.status_code >= 500
                    if retryable:
                        self.limiter.record_failure(
                            is_rate_limit=response.status_code == 429,
                            retry_after=retry_after,
                            epoch=epoch,
                        )
                    # The URL is a credential: report the status, never the URL.
                    raise QobuzError(
                        f"Download failed with HTTP {response.status_code}",
                        status_code=response.status_code,
                        endpoint="download",
                        retryable=retryable,
                        retry_after=retry_after,
                    )

                total = _content_length(response)
                handle = dest.open("wb")
                try:
                    async for chunk in response.aiter_bytes(chunk_size):
                        if not chunk:
                            continue
                        await asyncio.to_thread(handle.write, chunk)
                        written += len(chunk)
                        if on_progress is not None:
                            await _emit_progress(on_progress, written, total)
                finally:
                    await asyncio.to_thread(handle.close)
        except httpx.TimeoutException as exc:
            self.limiter.record_failure(epoch=epoch)
            raise QobuzTransportError("Timed out downloading a track", endpoint="download") from exc
        except httpx.HTTPError as exc:
            self.limiter.record_failure(epoch=epoch)
            raise QobuzTransportError(
                f"Network error downloading a track: {type(exc).__name__}", endpoint="download"
            ) from exc
        except OSError as exc:
            raise QobuzError(f"Could not write {dest}: {exc}", endpoint="download") from exc

        self.limiter.record_success(epoch)
        return written

    # ------------------------------------------------------------------ misc
    def set_validation_track_id(self, track_id: str | int) -> None:
        """Use a known-good track id when validating app-secret candidates.

        The indexer calls this with a track discovered from a live search, which
        is more reliable than the built-in fallback id.
        """
        value = str(track_id).strip()
        if value:
            self._validation_track_id = value

    def rate_limit_stats(self) -> dict[str, Any]:
        """Convenience passthrough to the shared limiter's :meth:`stats`."""
        return self.limiter.stats()

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return (
            f"<QobuzClient logged_in={self._logged_in} "
            f"secret={self._secret_source} closed={self._closed}>"
        )


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
def _items(bucket: Any) -> list[dict[str, Any]]:
    """Return the ``items`` list of a Qobuz paged bucket, defensively."""
    if not isinstance(bucket, Mapping):
        return []
    items = bucket.get("items")
    if not isinstance(items, Sequence):
        return []
    return [dict(item) for item in items if isinstance(item, Mapping)]


def _truthy_status(payload: Any) -> bool:
    """Interpret the ``{"status": "success"}`` bodies of the favourite endpoints."""
    if isinstance(payload, Mapping):
        status = str(payload.get("status", "")).lower()
        if status:
            return status in ("success", "ok", "true")
    return payload is not None


def _content_length(response: httpx.Response) -> int | None:
    """Parse ``Content-Length`` from a streaming response, if present."""
    raw = response.headers.get("Content-Length")
    if not raw:
        return None
    try:
        return int(raw)
    except (TypeError, ValueError):
        return None


async def _emit_progress(
    callback: ProgressCallback, written: int, total: int | None
) -> None:
    """Invoke a progress callback that may be sync or async."""
    result = callback(written, total)
    if inspect.isawaitable(result):
        await result
