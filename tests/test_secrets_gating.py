"""Regression tests: app-secret derivation must be rate-limited and cooled down.

Two defects are pinned here.

1. ``fetch_bundle`` issued two raw GETs to ``play.qobuz.com`` (the login page
   and a multi-megabyte ``bundle.js``) on a plain ``httpx.AsyncClient`` — no
   ``limiter.acquire()``, nothing counted against ``QOBUZ_MAX_REQUESTS_PER_HOUR``
   and nothing visible to the circuit breaker.
2. ``get_file_url`` re-derived the secret on *any* auth failure with no negative
   caching, so an expired ``QOBUZ_USER_AUTH_TOKEN`` turned a 20-track album into
   up to 120 ungated bundle downloads that could not possibly help.

Nothing here touches the real network: ``httpx.MockTransport`` serves every
request.
"""

from __future__ import annotations

import asyncio
from typing import Any

import httpx
import pytest

from app.config import Settings
from app.qobuz.client import QobuzClient
from app.qobuz.errors import QobuzAuthError, QobuzSecretError
from app.qobuz.secrets import (
    DerivationCooldown,
    fetch_bundle,
    resolve_app_secret,
)

BUNDLE_PATH = "/resources/8.2.0-b034/bundle.js"
#: Shaped like the real web-player bundle so ``DIRECT_SECRET_RE`` matches it, but
#: the id and secret are invented — nothing here is a value from anyone's .env.
BUNDLE_BODY = 'production:{api:{appId:"000000000",appSecret:"0123456789abcdef0123456789abcdef"'
LOGIN_BODY = f'<script src="{BUNDLE_PATH}"></script>'


class RecordingLimiter:
    """A limiter stand-in that records how it was used."""

    def __init__(self) -> None:
        self.acquires = 0
        self.successes: list[int | None] = []
        self.failures: list[dict[str, Any]] = []
        self._epoch = 0

    async def acquire(self) -> int:
        self.acquires += 1
        return self._epoch

    def record_success(self, epoch: int | None = None) -> None:
        self.successes.append(epoch)

    def record_failure(
        self,
        *,
        is_rate_limit: bool = False,
        retry_after: float | None = None,
        epoch: int | None = None,
    ) -> bool:
        self.failures.append(
            {"is_rate_limit": is_rate_limit, "retry_after": retry_after, "epoch": epoch}
        )
        return False


def make_settings(**overrides: Any) -> Settings:
    values: dict[str, Any] = {
        "qobuz_app_id": "123456",
        "qobuz_user_auth_token": "token-abc",
        "qobuz_min_request_interval": 0.0,
        "request_max_retries": 1,
        "download_max_attempts": 1,
    }
    values.update(overrides)
    return Settings(**values)


def bundle_client(hits: list[str], *, status: int = 200) -> httpx.AsyncClient:
    """A client serving the login page and the bundle, counting every hit."""

    def handler(request: httpx.Request) -> httpx.Response:
        hits.append(request.url.path)
        if status != 200:
            return httpx.Response(status)
        if request.url.path.endswith("/login"):
            return httpx.Response(200, text=LOGIN_BODY)
        return httpx.Response(200, text=BUNDLE_BODY)

    return httpx.AsyncClient(transport=httpx.MockTransport(handler))


# ---------------------------------------------------------------------------
# Gating
# ---------------------------------------------------------------------------
def test_fetch_bundle_goes_through_the_rate_limiter() -> None:
    async def main() -> None:
        hits: list[str] = []
        limiter = RecordingLimiter()
        async with bundle_client(hits) as client:
            text = await fetch_bundle(
                client, settings=make_settings(), limiter=limiter
            )

        assert "appSecret" in text
        assert len(hits) == 2, "login page + bundle"
        assert limiter.acquires == 2, "every play.qobuz.com GET must take a slot"
        assert limiter.successes == [0, 0]
        assert limiter.failures == []

    asyncio.run(main())


def test_fetch_bundle_reports_a_429_to_the_limiter() -> None:
    async def main() -> None:
        hits: list[str] = []
        limiter = RecordingLimiter()
        async with bundle_client(hits, status=429) as client:
            with pytest.raises(QobuzSecretError):
                await fetch_bundle(client, settings=make_settings(), limiter=limiter)

        assert limiter.acquires == 1
        assert limiter.failures and limiter.failures[0]["is_rate_limit"] is True

    asyncio.run(main())


def test_fetch_bundle_without_a_limiter_still_works() -> None:
    """The limiter is optional so unit tests can call the parser directly."""

    async def main() -> None:
        hits: list[str] = []
        async with bundle_client(hits) as client:
            assert "appSecret" in await fetch_bundle(client, settings=make_settings())
        assert len(hits) == 2

    asyncio.run(main())


# ---------------------------------------------------------------------------
# Negative caching
# ---------------------------------------------------------------------------
def test_failed_derivation_is_not_retried_during_the_cooldown() -> None:
    async def main() -> None:
        hits: list[str] = []
        cooldown = DerivationCooldown(600.0)
        settings = make_settings(qobuz_app_secret="")

        async def never(candidate: str) -> bool:
            return False

        async with bundle_client(hits) as client:
            for _ in range(5):
                with pytest.raises(QobuzSecretError):
                    await resolve_app_secret(
                        client,
                        never,
                        settings=settings,
                        force_refresh=True,
                        cooldown=cooldown,
                    )

        assert len(hits) == 2, (
            "the bundle must be fetched once, not once per attempt; got "
            f"{len(hits)} requests"
        )
        assert cooldown.seconds_remaining() > 0.0

    asyncio.run(main())


def test_cooldown_expires_and_allows_another_attempt() -> None:
    clock = {"now": 0.0}
    cooldown = DerivationCooldown(100.0, time_func=lambda: clock["now"])

    cooldown.record_failure(QobuzSecretError("nope"))
    assert cooldown.blocking_error() is not None

    clock["now"] = 101.0
    assert cooldown.blocking_error() is None
    assert cooldown.seconds_remaining() == 0.0


def test_cooldown_is_cleared_by_a_success() -> None:
    cooldown = DerivationCooldown(100.0)
    cooldown.record_failure(QobuzSecretError("nope"))
    cooldown.record_success()
    assert cooldown.blocking_error() is None


# ---------------------------------------------------------------------------
# A bad *token* must not trigger a re-derivation
# ---------------------------------------------------------------------------
def test_expired_token_does_not_rescrape_the_web_player() -> None:
    """A 401 is the user's token; re-deriving the secret cannot fix it."""

    async def main() -> None:
        bundle_hits: list[str] = []

        def api_handler(request: httpx.Request) -> httpx.Response:
            return httpx.Response(401, json={"status": "error", "message": "invalid token"})

        api = httpx.AsyncClient(
            base_url="https://www.qobuz.com/api.json/0.2/",
            transport=httpx.MockTransport(api_handler),
        )
        downloader = bundle_client(bundle_hits)
        client = QobuzClient(
            settings=make_settings(qobuz_app_secret="0" * 32),
            http_client=api,
            download_client=downloader,
        )
        try:
            with pytest.raises(QobuzAuthError):
                await client.get_file_url("64868955", format_id=5)
        finally:
            await api.aclose()
            await downloader.aclose()

        assert bundle_hits == [], "play.qobuz.com must not be touched on a 401"

    asyncio.run(main())
