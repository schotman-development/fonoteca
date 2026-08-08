"""Unit tests for :mod:`app.net.http`.

Nothing touches the network: every request is served by an
``httpx.MockTransport`` handler the test writes itself, and the rate limiter is
built on a virtual clock so backoff never really sleeps.

What these prove, in one line each: the right HTTP status becomes the right typed
error; the limiter is told the truth about every outcome (including the ones the
status code alone would get wrong); retries stop when they should; and the two
per-source hooks — ``rate_limit_statuses`` and ``check_payload`` — actually
change behaviour, because they are what MusicBrainz and Deezer need.
"""

from __future__ import annotations

import asyncio
from typing import Any, Callable

import httpx
import pytest

from app.config import Settings
from app.net.errors import (
    HttpAuthError,
    HttpError,
    HttpNotFound,
    HttpRateLimitError,
    HttpServerError,
    HttpTransportError,
)
from app.net.http import JsonHttpClient, clean_params, make_wait, parse_retry_after
from app.net.ratelimit import CircuitBreaker, RateLimiter


class FakeClock:
    """A monotonic clock the test drives by hand."""

    def __init__(self, start: float = 1_000.0) -> None:
        self.now = start
        self.sleeps: list[float] = []

    def time(self) -> float:
        return self.now

    async def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.now += max(0.0, seconds)
        await asyncio.sleep(0)


def make_settings(**overrides: Any) -> Settings:
    """Real Settings with fast backoff, so retry tests finish instantly."""
    base: dict[str, Any] = {
        "qobuz_app_id": "app",
        "qobuz_user_auth_token": "token",
        "backoff_initial": 0.0,
        "backoff_max": 0.0,
        "backoff_multiplier": 1.0,
        "backoff_jitter": 0.0,
        "request_max_retries": 3,
    }
    base.update(overrides)
    return Settings(**base)


def make_limiter(clock: FakeClock | None = None) -> RateLimiter:
    clock = clock or FakeClock()
    return RateLimiter(
        min_interval=0.0,
        max_per_hour=10_000,
        breaker=CircuitBreaker(threshold=3, cooldown=60.0, time_func=clock.time),
        time_func=clock.time,
        sleep_func=clock.sleep,
        name="test",
    )


def make_client(
    handler: Callable[[httpx.Request], httpx.Response],
    *,
    cls: type[JsonHttpClient] = JsonHttpClient,
    limiter: RateLimiter | None = None,
    **kwargs: Any,
) -> JsonHttpClient:
    """A client whose transport is the supplied handler.

    The injected client carries the ``base_url`` a real one would get from
    :meth:`JsonHttpClient._build_client`, so paths resolve the same way.
    """
    return cls(
        limiter=limiter or make_limiter(),
        settings=make_settings(),
        http_client=httpx.AsyncClient(
            transport=httpx.MockTransport(handler), base_url="https://example.test/"
        ),
        **kwargs,
    )


def responder(*responses: httpx.Response) -> tuple[Callable[..., httpx.Response], list[str]]:
    """Serve *responses* in order, repeating the last one, recording each URL."""
    hits: list[str] = []
    queue = list(responses)

    def handler(request: httpx.Request) -> httpx.Response:
        hits.append(str(request.url))
        return queue.pop(0) if len(queue) > 1 else queue[0]

    return handler, hits


# ---------------------------------------------------------------------------
# Pure helpers
# ---------------------------------------------------------------------------
def test_parse_retry_after_accepts_seconds_and_dates() -> None:
    assert parse_retry_after("30") == 30.0
    assert parse_retry_after("  12.5 ") == 12.5
    assert parse_retry_after(None) is None
    assert parse_retry_after("not a date") is None
    # An HTTP-date in the past clamps to zero rather than going negative.
    assert parse_retry_after("Wed, 21 Oct 2015 07:28:00 GMT") == 0.0


def test_clean_params_drops_none_and_stringifies() -> None:
    assert clean_params(None) == {}
    assert clean_params({"a": None, "b": 7, "c": True, "d": False, "e": "x"}) == {
        "b": "7",
        "c": "true",
        "d": "false",
        "e": "x",
    }


def test_make_wait_treats_retry_after_as_a_floor_not_a_ceiling() -> None:
    settings = make_settings(backoff_initial=1.0, backoff_max=2.0, backoff_multiplier=1.0)
    wait = make_wait(settings)

    class _Outcome:
        failed = True

        def exception(self) -> BaseException:
            return HttpRateLimitError("slow down", retry_after=45.0)

    class _State:
        attempt_number = 1
        outcome = _Outcome()

    # backoff_max is 2s, but the server said 45s — the server wins.
    assert wait(_State()) == pytest.approx(45.0)


# ---------------------------------------------------------------------------
# Status mapping
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    ("status", "expected"),
    [
        (401, HttpAuthError),
        (403, HttpAuthError),
        (404, HttpNotFound),
        (418, HttpError),
        (500, HttpServerError),
        (502, HttpServerError),
    ],
)
def test_status_maps_to_typed_error(status: int, expected: type[Exception]) -> None:
    handler, _ = responder(httpx.Response(status, json={}))
    client = make_client(handler)

    with pytest.raises(expected):
        asyncio.run(client.get_json("thing", max_attempts=1))


def test_success_returns_decoded_json_and_records_it() -> None:
    handler, hits = responder(httpx.Response(200, json={"id": 42}))
    limiter = make_limiter()
    client = make_client(handler, limiter=limiter)

    assert asyncio.run(client.get_json("thing", {"q": "x", "skip": None})) == {"id": 42}
    assert hits == ["https://example.test/thing?q=x"]
    assert limiter.stats()["recent_429s"] == 0


def test_non_json_body_is_a_retryable_server_error() -> None:
    handler, hits = responder(httpx.Response(200, text="<html>nope</html>"))
    client = make_client(handler)

    with pytest.raises(HttpServerError):
        asyncio.run(client.get_json("thing"))

    # Retryable, so it really did try again rather than giving up at once.
    assert len(hits) == 3


def test_network_failure_becomes_a_transport_error() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("no route", request=request)

    client = make_client(handler)

    with pytest.raises(HttpTransportError):
        asyncio.run(client.get_json("thing", max_attempts=1))


# ---------------------------------------------------------------------------
# What the limiter is told
# ---------------------------------------------------------------------------
def test_throttling_is_recorded_as_a_rate_limit_with_its_retry_after() -> None:
    handler, _ = responder(
        httpx.Response(429, headers={"Retry-After": "17"}, json={})
    )
    limiter = make_limiter()
    client = make_client(handler, limiter=limiter)

    with pytest.raises(HttpRateLimitError) as caught:
        asyncio.run(client.get_json("thing", max_attempts=1))

    assert caught.value.retry_after == 17.0
    assert limiter.stats()["recent_429s"] == 1


def test_auth_failure_does_not_feed_the_circuit_breaker() -> None:
    """A wrong key fails identically forever; stalling a healthy host helps nobody."""
    handler, _ = responder(httpx.Response(401, json={}))
    limiter = make_limiter()
    client = make_client(handler, limiter=limiter)

    for _ in range(5):
        with pytest.raises(HttpAuthError):
            asyncio.run(client.get_json("thing", max_attempts=1))

    assert limiter.stats()["circuit_open"] is False


def test_repeated_server_errors_trip_the_breaker() -> None:
    handler, _ = responder(httpx.Response(503, json={}))
    limiter = make_limiter()
    client = make_client(handler, limiter=limiter)

    with pytest.raises(HttpServerError):
        asyncio.run(client.get_json("thing"))

    assert limiter.stats()["circuit_open"] is True


# ---------------------------------------------------------------------------
# Retries
# ---------------------------------------------------------------------------
def test_retries_then_succeeds() -> None:
    handler, hits = responder(
        httpx.Response(500, json={}),
        httpx.Response(500, json={}),
        httpx.Response(200, json={"ok": True}),
    )
    client = make_client(handler)

    assert asyncio.run(client.get_json("thing")) == {"ok": True}
    assert len(hits) == 3


def test_non_retryable_error_is_not_retried() -> None:
    handler, hits = responder(httpx.Response(404, json={}))
    client = make_client(handler)

    with pytest.raises(HttpNotFound):
        asyncio.run(client.get_json("thing"))

    assert len(hits) == 1


# ---------------------------------------------------------------------------
# The two per-source hooks
# ---------------------------------------------------------------------------
class _MusicBrainzish(JsonHttpClient):
    """MusicBrainz throttles with 503, not 429."""

    source = "musicbrainz"
    rate_limit_statuses = frozenset({429, 503})


def test_a_source_can_declare_503_as_throttling() -> None:
    handler, _ = responder(httpx.Response(503, json={}))
    limiter = make_limiter()
    client = make_client(handler, cls=_MusicBrainzish, limiter=limiter)

    with pytest.raises(HttpRateLimitError):
        asyncio.run(client.get_json("ws/2/release", max_attempts=1))

    assert limiter.stats()["recent_429s"] == 1


class _Deezerish(JsonHttpClient):
    """Deezer answers HTTP 200 with an error object, for misses and quota alike."""

    source = "deezer"

    def check_payload(self, path: str, body: Any, *, status: int) -> Any:
        error = body.get("error") if isinstance(body, dict) else None
        if not error:
            return body
        kind = str(error.get("type", ""))
        if "Quota" in str(error.get("message", "")) or kind == "QuotaException":
            raise HttpRateLimitError("quota exceeded", source=self.source, endpoint=path)
        raise HttpNotFound(str(error.get("message") or kind), source=self.source, endpoint=path)


def test_http_200_with_an_error_object_is_not_a_success() -> None:
    handler, hits = responder(
        httpx.Response(200, json={"error": {"type": "DataException", "message": "no data"}})
    )
    limiter = make_limiter()
    client = make_client(handler, cls=_Deezerish, limiter=limiter)

    with pytest.raises(HttpNotFound):
        asyncio.run(client.get_json("album/upc:000", max_attempts=3))

    # A clean miss is not a fault: one attempt, breaker untouched.
    assert len(hits) == 1
    assert limiter.stats()["circuit_open"] is False


def test_http_200_quota_error_is_retryable_and_recorded() -> None:
    handler, hits = responder(
        httpx.Response(200, json={"error": {"type": "Exception", "message": "Quota limit exceeded"}})
    )
    limiter = make_limiter()
    client = make_client(handler, cls=_Deezerish, limiter=limiter)

    with pytest.raises(HttpRateLimitError):
        asyncio.run(client.get_json("album/1", max_attempts=2))

    assert len(hits) == 2
    assert limiter.stats()["recent_429s"] == 2


# ---------------------------------------------------------------------------
# Lifecycle
# ---------------------------------------------------------------------------
def test_constructor_does_no_io_and_headers_carry_no_credentials() -> None:
    """Building a client must be safe at startup, and must never leak the token."""
    client = JsonHttpClient(limiter=make_limiter(), settings=make_settings())

    headers = client.build_headers()
    assert set(headers) == {"User-Agent", "Accept"}
    assert not any("qobuz" in key.lower() for key in headers)
    assert not any("token" in key.lower() or "auth" in key.lower() for key in headers)


def test_user_agent_override_wins() -> None:
    client = JsonHttpClient(
        limiter=make_limiter(), settings=make_settings(), user_agent="Qobuzarr/1.0 ( me@example.com )"
    )
    assert client.build_headers()["User-Agent"] == "Qobuzarr/1.0 ( me@example.com )"


def test_injected_client_is_not_closed_but_using_a_closed_one_raises() -> None:
    handler, _ = responder(httpx.Response(200, json={}))
    transport = httpx.AsyncClient(transport=httpx.MockTransport(handler))
    client = JsonHttpClient(
        limiter=make_limiter(), settings=make_settings(), http_client=transport
    )

    asyncio.run(client.aclose())

    assert transport.is_closed is False
    with pytest.raises(RuntimeError):
        _ = client.client
    asyncio.run(transport.aclose())
