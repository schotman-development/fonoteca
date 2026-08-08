"""Unit tests for :mod:`app.net.ratelimit`.

Real waits would make these tests take hours, so both the clock and the sleep
function are injected: :class:`FakeClock` advances virtual time by exactly the
amount slept, which also makes every assertion exact rather than approximate.

The tests use ``asyncio.run`` directly rather than an async plugin, so no extra
pytest dependency is required.
"""

from __future__ import annotations

import asyncio

import pytest

from app.net.ratelimit import HOUR_SECONDS, CircuitBreaker, RateLimiter


class FakeClock:
    """A monotonic clock the test drives by hand."""

    def __init__(self, start: float = 1_000.0) -> None:
        self.now = start
        self.sleeps: list[float] = []

    def time(self) -> float:
        return self.now

    async def sleep(self, seconds: float) -> None:
        """Advance virtual time and yield to the event loop."""
        self.sleeps.append(seconds)
        self.now += max(0.0, seconds)
        await asyncio.sleep(0)

    def advance(self, seconds: float) -> None:
        self.now += seconds


def make_limiter(
    clock: FakeClock, *, min_interval: float = 2.0, max_per_hour: int = 1200, **breaker: float
) -> RateLimiter:
    """Build a limiter wired to *clock*."""
    kwargs: dict[str, float] = {"threshold": 3, "cooldown": 60.0, "window": 300.0}
    kwargs.update(breaker)
    return RateLimiter(
        min_interval=min_interval,
        max_per_hour=max_per_hour,
        breaker=CircuitBreaker(time_func=clock.time, **kwargs),  # type: ignore[arg-type]
        time_func=clock.time,
        sleep_func=clock.sleep,
    )


# ---------------------------------------------------------------------------
# Minimum interval
# ---------------------------------------------------------------------------
def test_first_acquire_is_immediate() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock)

    asyncio.run(limiter.acquire())

    assert clock.now == 1_000.0
    assert clock.sleeps == []
    assert limiter.requests_last_hour == 1


def test_min_interval_is_enforced_between_calls() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=2.5)

    async def scenario() -> list[float]:
        stamps = []
        for _ in range(4):
            await limiter.acquire()
            stamps.append(clock.now)
        return stamps

    stamps = asyncio.run(scenario())

    assert stamps == [1_000.0, 1_002.5, 1_005.0, 1_007.5]
    gaps = [b - a for a, b in zip(stamps, stamps[1:])]
    assert all(gap >= 2.5 for gap in gaps)


def test_time_already_spent_counts_towards_the_interval() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=5.0)

    async def scenario() -> None:
        await limiter.acquire()
        clock.advance(4.0)  # the request itself took four seconds
        await limiter.acquire()

    asyncio.run(scenario())

    assert clock.sleeps == [1.0]
    assert clock.now == 1_005.0


def test_zero_interval_never_sleeps() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0)

    async def scenario() -> None:
        for _ in range(10):
            await limiter.acquire()

    asyncio.run(scenario())

    assert clock.sleeps == []
    assert limiter.requests_last_hour == 10


# ---------------------------------------------------------------------------
# Rolling hourly ceiling
# ---------------------------------------------------------------------------
def test_hourly_cap_blocks_until_the_oldest_request_ages_out() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, max_per_hour=3)

    async def scenario() -> None:
        for _ in range(3):
            await limiter.acquire()
            clock.advance(10.0)
        await limiter.acquire()

    asyncio.run(scenario())

    # Three requests at t=1000/1010/1020, now t=1030; the fourth must wait until
    # the first leaves the rolling hour at t=1000+3600.
    assert clock.now == pytest.approx(1_000.0 + HOUR_SECONDS)
    assert limiter.requests_last_hour == 3  # the first one has aged out


def test_budget_accounting() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, max_per_hour=10)

    async def scenario() -> None:
        for _ in range(4):
            await limiter.acquire()

    asyncio.run(scenario())

    assert limiter.requests_last_hour == 4
    assert limiter.budget_remaining == 6
    assert limiter.stats()["budget_used_percent"] == 40


def test_old_requests_leave_the_window() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, max_per_hour=5)

    asyncio.run(limiter.acquire())
    assert limiter.requests_last_hour == 1

    clock.advance(HOUR_SECONDS + 1.0)
    assert limiter.requests_last_hour == 0
    assert limiter.budget_remaining == 5


# ---------------------------------------------------------------------------
# Concurrency
# ---------------------------------------------------------------------------
def test_concurrent_acquires_are_serialised() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=2.0)
    stamps: list[float] = []

    async def worker() -> None:
        await limiter.acquire()
        stamps.append(clock.now)

    async def scenario() -> None:
        await asyncio.gather(*(worker() for _ in range(5)))

    asyncio.run(scenario())

    assert len(stamps) == 5
    ordered = sorted(stamps)
    assert ordered == stamps  # asyncio.Lock hands out slots first-come-first-served
    gaps = [b - a for a, b in zip(ordered, ordered[1:])]
    assert all(gap >= 2.0 for gap in gaps), gaps
    assert limiter.requests_last_hour == 5


def test_concurrent_acquires_respect_the_hourly_cap() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, max_per_hour=2)

    async def scenario() -> None:
        await asyncio.gather(*(limiter.acquire() for _ in range(3)))

    asyncio.run(scenario())

    # The third caller could not be served until the first aged out.
    assert clock.now == pytest.approx(1_000.0 + HOUR_SECONDS)


# ---------------------------------------------------------------------------
# stats()
# ---------------------------------------------------------------------------
def test_stats_exposes_the_dashboard_contract() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=2.0, max_per_hour=1200)

    stats = limiter.stats()
    for key in (
        "requests_last_hour",
        "hourly_cap",
        "min_interval",
        "next_slot_in_seconds",
        "paused_until",
    ):
        assert key in stats

    assert stats["requests_last_hour"] == 0
    assert stats["hourly_cap"] == 1200
    assert stats["min_interval"] == 2.0
    assert stats["next_slot_in_seconds"] == 0.0
    assert stats["paused_until"] is None
    assert stats["last_request_at"] is None
    assert stats["circuit_open"] is False

    asyncio.run(limiter.acquire())
    stats = limiter.stats()
    assert stats["requests_last_hour"] == 1
    assert stats["next_slot_in_seconds"] == pytest.approx(2.0)
    assert stats["last_request_at"] is not None
    assert stats["budget_remaining"] == 1199


# ---------------------------------------------------------------------------
# Circuit breaker
# ---------------------------------------------------------------------------
def test_breaker_opens_after_the_threshold() -> None:
    clock = FakeClock()
    breaker = CircuitBreaker(threshold=3, cooldown=60.0, window=300.0, time_func=clock.time)

    assert breaker.record_failure() is False
    assert breaker.record_failure() is False
    assert breaker.is_open() is False
    assert breaker.record_failure() is True
    assert breaker.is_open() is True
    assert breaker.seconds_remaining() == pytest.approx(60.0)


def test_breaker_closes_when_the_cooldown_expires() -> None:
    clock = FakeClock()
    breaker = CircuitBreaker(threshold=1, cooldown=30.0, time_func=clock.time)

    breaker.record_failure()
    assert breaker.is_open() is True

    clock.advance(29.0)
    assert breaker.is_open() is True

    clock.advance(2.0)
    assert breaker.is_open() is False
    assert breaker.seconds_remaining() == 0.0


def test_breaker_cooldown_grows_exponentially_and_resets_on_success() -> None:
    clock = FakeClock()
    breaker = CircuitBreaker(
        threshold=1, cooldown=10.0, multiplier=2.0, window=300.0, time_func=clock.time
    )

    breaker.record_failure()
    assert breaker.seconds_remaining() == pytest.approx(10.0)

    clock.advance(11.0)
    breaker.record_failure()
    assert breaker.seconds_remaining() == pytest.approx(20.0)

    clock.advance(21.0)
    breaker.record_failure()
    assert breaker.seconds_remaining() == pytest.approx(40.0)

    breaker.record_success()
    assert breaker.is_open() is False

    # Growth restarted from the base cooldown.
    breaker.record_failure()
    assert breaker.seconds_remaining() == pytest.approx(10.0)


def test_breaker_cooldown_is_capped() -> None:
    clock = FakeClock()
    breaker = CircuitBreaker(
        threshold=1, cooldown=10.0, multiplier=10.0, max_cooldown=25.0, time_func=clock.time
    )

    for _ in range(4):
        breaker.record_failure()
        clock.advance(30.0)

    assert breaker.seconds_remaining() <= 25.0


def test_breaker_forgets_failures_outside_the_window() -> None:
    clock = FakeClock()
    breaker = CircuitBreaker(threshold=3, cooldown=60.0, window=100.0, time_func=clock.time)

    breaker.record_failure()
    breaker.record_failure()
    clock.advance(101.0)

    assert breaker.recent_failures == 0
    assert breaker.record_failure() is False
    assert breaker.is_open() is False


def test_open_breaker_blocks_acquire_for_the_cooldown() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, threshold=2, cooldown=120.0)

    limiter.record_failure(is_rate_limit=True)
    tripped = limiter.record_failure(is_rate_limit=True)
    assert tripped is True
    assert limiter.stats()["circuit_open"] is True
    assert limiter.stats()["recent_429s"] == 2

    asyncio.run(limiter.acquire())

    assert clock.now == pytest.approx(1_120.0)
    assert limiter.stats()["circuit_open"] is False


def test_retry_after_pauses_traffic_even_below_the_threshold() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, threshold=99, cooldown=5.0)

    assert limiter.record_failure(is_rate_limit=True, retry_after=45.0) is False
    assert limiter.paused_until is not None
    assert limiter.next_slot_in_seconds == pytest.approx(45.0)

    asyncio.run(limiter.acquire())
    assert clock.now == pytest.approx(1_045.0)


def test_success_resets_the_breaker_state() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, threshold=2, cooldown=60.0)

    limiter.record_failure(is_rate_limit=True)
    limiter.record_failure(is_rate_limit=True)
    limiter.record_success()

    assert limiter.stats()["circuit_open"] is False
    assert limiter.stats()["recent_429s"] == 0
    assert limiter.next_slot_in_seconds == 0.0


def test_manual_pause_and_resume() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0)

    limiter.pause(600.0)
    assert limiter.next_slot_in_seconds == pytest.approx(600.0)

    limiter.resume()
    assert limiter.next_slot_in_seconds == 0.0
    asyncio.run(limiter.acquire())
    assert clock.now == 1_000.0


def test_from_settings_uses_the_configured_profile() -> None:
    from app.config import get_settings

    settings = get_settings()
    limiter = RateLimiter.from_settings(settings)

    assert limiter.min_interval == settings.qobuz_min_request_interval
    assert limiter.max_per_hour == settings.qobuz_max_requests_per_hour
    assert limiter.breaker.threshold == settings.circuit_breaker_threshold
    assert limiter.breaker.cooldown == float(settings.circuit_breaker_cooldown)


# ---------------------------------------------------------------------------
# Regression: an in-flight success must not wipe a newer cooldown
# ---------------------------------------------------------------------------
def test_acquire_returns_the_breaker_epoch() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, threshold=2, cooldown=60.0)

    first = asyncio.run(limiter.acquire())
    assert first == 0

    limiter.record_failure(is_rate_limit=True)
    limiter.record_failure(is_rate_limit=True)  # trips -> epoch 1
    assert limiter.epoch == 1

    clock.advance(60.0)
    assert asyncio.run(limiter.acquire()) == 1


def test_stale_success_does_not_close_the_breaker() -> None:
    """The 300-second download finishing after three 429s must change nothing.

    This is the exact shape of the live bug: the queue worker acquires a slot,
    streams a FLAC for 60s, and meanwhile the indexer takes three 429s.  The
    download's eventual ``record_success`` used to clear the cooldown, the
    consecutive-trip counter *and* the exponential growth.
    """
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=2.0, threshold=3, cooldown=1800.0)

    download_epoch = asyncio.run(limiter.acquire())  # the track starts streaming

    clock.advance(7.0)
    limiter.record_failure(is_rate_limit=True)
    limiter.record_failure(is_rate_limit=True)
    assert limiter.record_failure(is_rate_limit=True) is True
    assert limiter.stats()["circuit_open"] is True
    assert limiter.next_slot_in_seconds == pytest.approx(1800.0)

    clock.advance(53.0)  # the track finishes, 60s after it started
    limiter.record_success(download_epoch)

    stats = limiter.stats()
    assert stats["circuit_open"] is True, "a stale success must not reopen traffic"
    assert limiter.next_slot_in_seconds == pytest.approx(1747.0)
    assert limiter.breaker.stats()["consecutive_trips"] == 1, "growth must survive"


def test_a_current_success_still_closes_the_breaker() -> None:
    """The epoch guard must not break the normal recovery path."""
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, threshold=2, cooldown=60.0)

    limiter.record_failure(is_rate_limit=True)
    limiter.record_failure(is_rate_limit=True)
    assert limiter.stats()["circuit_open"] is True

    clock.advance(60.0)
    epoch = asyncio.run(limiter.acquire())
    limiter.record_success(epoch)

    assert limiter.stats()["circuit_open"] is False
    assert limiter.stats()["recent_429s"] == 0


def test_stale_failure_is_not_counted_twice() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, threshold=2, cooldown=60.0)

    stale = asyncio.run(limiter.acquire())
    limiter.record_failure(is_rate_limit=True)
    limiter.record_failure(is_rate_limit=True)  # trip #1
    assert limiter.breaker.stats()["consecutive_trips"] == 1

    limiter.record_failure(is_rate_limit=True, epoch=stale)
    assert limiter.breaker.recent_failures == 0, "the stale failure is ignored"
    assert limiter.breaker.stats()["consecutive_trips"] == 1


def test_success_never_shortens_an_explicit_retry_after() -> None:
    """``Retry-After: 600`` is the server's word; only the clock may clear it."""
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, threshold=99, cooldown=5.0)

    assert limiter.record_failure(is_rate_limit=True, retry_after=600.0) is False
    assert limiter.next_slot_in_seconds == pytest.approx(600.0)

    limiter.record_success()  # a fresh, current success
    assert limiter.next_slot_in_seconds == pytest.approx(600.0)

    clock.advance(600.0)
    assert limiter.next_slot_in_seconds == 0.0


def test_consecutive_trips_keep_growing_across_an_interleaved_success() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0, threshold=1, cooldown=100.0)

    in_flight = asyncio.run(limiter.acquire())
    limiter.record_failure(is_rate_limit=True)  # trip #1 -> 100s
    assert limiter.next_slot_in_seconds == pytest.approx(100.0)

    limiter.record_success(in_flight)  # stale; ignored
    clock.advance(100.0)

    limiter.record_failure(is_rate_limit=True)  # trip #2 -> 200s, not 100s
    assert limiter.next_slot_in_seconds == pytest.approx(200.0)


def test_manual_pause_survives_a_stale_success_but_resume_clears_it() -> None:
    clock = FakeClock()
    limiter = make_limiter(clock, min_interval=0.0)

    in_flight = asyncio.run(limiter.acquire())
    limiter.pause(600.0)
    limiter.record_success(in_flight)
    assert limiter.next_slot_in_seconds == pytest.approx(600.0)

    limiter.resume()
    assert limiter.next_slot_in_seconds == 0.0
