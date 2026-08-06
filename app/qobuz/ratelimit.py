"""Global outbound rate limiting for every Qobuz API call.

Fonoteca is deliberately, aggressively slow: a single :class:`RateLimiter`
instance gates *all* traffic — the indexer, UI searches and downloads alike —
so the account never looks like a scraper.

Two independent constraints are enforced at once:

* a **minimum interval** between any two requests (``QOBUZ_MIN_REQUEST_INTERVAL``),
* a **rolling one-hour ceiling** on request count (``QOBUZ_MAX_REQUESTS_PER_HOUR``).

On top of that sits a :class:`CircuitBreaker`: repeated 429/5xx responses trip
it and every ``acquire()`` blocks for a cooldown that grows exponentially while
the failures keep coming, and resets as soon as a request succeeds.

Concurrency
-----------
``acquire()`` is safe for any number of concurrent awaiters. It holds an
:class:`asyncio.Lock` for the whole wait, which makes servicing strictly FIFO
(``asyncio.Lock`` wakes waiters in order) and makes it impossible for two
coroutines to observe the same free slot. :meth:`RateLimiter.stats` is
lock-free so the dashboard can poll it from anywhere.

Testability
-----------
The clock and the sleep function are injectable (``time_func`` / ``sleep_func``),
so unit tests can drive virtual time instead of really waiting minutes.
"""

from __future__ import annotations

import asyncio
import time
from collections import deque
from datetime import datetime, timedelta, timezone
from typing import Any, Awaitable, Callable, Deque

from app.config import Settings, get_settings
from app.logging_conf import get_logger

__all__ = ["CircuitBreaker", "RateLimiter", "HOUR_SECONDS"]

logger = get_logger(__name__)

#: Length of the rolling window used for the hourly ceiling.
HOUR_SECONDS: float = 3600.0

TimeFunc = Callable[[], float]
SleepFunc = Callable[[float], Awaitable[Any]]


def _utc_now() -> datetime:
    """Wall-clock ``now`` in UTC (the monotonic clock cannot be formatted)."""
    return datetime.now(timezone.utc)


class CircuitBreaker:
    """Trips open after repeated upstream failures and blocks traffic.

    A failure is any response the client considers "we are being pushed back":
    HTTP 429 and 5xx. Failures are counted inside a sliding ``window``; once
    ``threshold`` of them accumulate the breaker opens for ``cooldown`` seconds.
    Every consecutive trip multiplies the cooldown by ``multiplier`` (capped at
    ``max_cooldown``), so a persistently unhappy API backs us off further and
    further.

    Epochs
    ------
    Outcomes are **not** "last response wins". Every trip bumps :attr:`epoch`,
    and callers pass back the epoch that was in force when their slot was
    granted (:meth:`RateLimiter.acquire` returns it). An outcome stamped with an
    older epoch belonged to a request that was already in flight when the
    breaker opened, so it is ignored — otherwise a 300-second FLAC download
    finishing at ``t=60`` would wipe a cooldown that three 429s installed at
    ``t=7``, which in practice disabled the breaker for the whole time any
    download was running.

    Retry-After
    -----------
    A deadline the *server* asked for is held separately in
    ``_retry_after_until`` and only the clock can clear it: no success, however
    fresh, may shorten it.

    The breaker never raises — it only reports how long the caller must wait.
    """

    def __init__(
        self,
        *,
        threshold: int = 3,
        cooldown: float = 1800.0,
        window: float = 300.0,
        multiplier: float = 2.0,
        max_cooldown: float | None = None,
        time_func: TimeFunc = time.monotonic,
    ) -> None:
        self.threshold = max(1, int(threshold))
        self.cooldown = max(0.0, float(cooldown))
        self.window = max(1.0, float(window))
        self.multiplier = max(1.0, float(multiplier))
        self.max_cooldown = (
            float(max_cooldown) if max_cooldown is not None else self.cooldown * 8.0
        )
        self._time = time_func

        self._failures: Deque[float] = deque()
        self._open_until: float | None = None
        self._retry_after_until: float | None = None
        self._consecutive_trips: int = 0
        self._total_trips: int = 0
        self._recent_429s: int = 0
        self._epoch: int = 0

    # ------------------------------------------------------------------ state
    def _prune(self, now: float) -> None:
        """Drop failures that fell out of the sliding window."""
        cutoff = now - self.window
        while self._failures and self._failures[0] < cutoff:
            self._failures.popleft()

    @property
    def epoch(self) -> int:
        """Monotonic counter bumped on every trip; stamps in-flight requests."""
        return self._epoch

    def is_open(self, now: float | None = None) -> bool:
        """True while the breaker is tripped and traffic must be held back."""
        return self.seconds_remaining(now) > 0.0

    def seconds_remaining(self, now: float | None = None) -> float:
        """Seconds left on the current cooldown (``0.0`` when closed).

        The effective deadline is the later of the threshold-derived cooldown
        and any explicit ``Retry-After`` the server asked for.
        """
        deadline: float | None = None
        for candidate in (self._open_until, self._retry_after_until):
            if candidate is not None and (deadline is None or candidate > deadline):
                deadline = candidate
        if deadline is None:
            return 0.0
        current = self._time() if now is None else now
        remaining = deadline - current
        if remaining <= 0.0:
            self._open_until = None
            self._retry_after_until = None
            return 0.0
        return remaining

    @property
    def open_until_wall(self) -> datetime | None:
        """When the cooldown expires, as a UTC datetime (``None`` if closed)."""
        remaining = self.seconds_remaining()
        if remaining <= 0.0:
            return None
        return _utc_now() + timedelta(seconds=remaining)

    @property
    def recent_failures(self) -> int:
        """Failures currently inside the sliding window."""
        self._prune(self._time())
        return len(self._failures)

    @property
    def recent_429s(self) -> int:
        """Count of 429 responses seen since the last success."""
        return self._recent_429s

    @property
    def total_trips(self) -> int:
        """How many times the breaker has opened over the process lifetime."""
        return self._total_trips

    # ----------------------------------------------------------- transitions
    def record_failure(
        self, *, is_rate_limit: bool = False, epoch: int | None = None
    ) -> bool:
        """Record one upstream failure. Returns True when this trips the breaker.

        Args:
            is_rate_limit: The failure was an HTTP 429.
            epoch: The :attr:`epoch` that was in force when this request's slot
                was granted. A failure from an older epoch is already accounted
                for by the trip that bumped the epoch, so it is not counted
                again (the caller may still install a ``Retry-After``, which
                can only ever extend the pause).
        """
        now = self._time()
        if epoch is not None and epoch < self._epoch:
            logger.debug(
                "Ignoring a stale failure from epoch %d (current %d)", epoch, self._epoch
            )
            return False
        self._prune(now)
        self._failures.append(now)
        if is_rate_limit:
            self._recent_429s += 1

        if len(self._failures) < self.threshold:
            return False

        cooldown = min(
            self.cooldown * (self.multiplier**self._consecutive_trips),
            self.max_cooldown,
        )
        self._consecutive_trips += 1
        self._total_trips += 1
        self._epoch += 1
        self._failures.clear()
        self._open_until = now + cooldown
        logger.warning(
            "Qobuz circuit breaker opened for %.0fs after %d failures (trip #%d)",
            cooldown,
            self.threshold,
            self._consecutive_trips,
        )
        return True

    def record_success(self, epoch: int | None = None) -> None:
        """Record a healthy response, closing the breaker and resetting growth.

        Args:
            epoch: The :attr:`epoch` in force when this request's slot was
                granted. A success stamped with an older epoch was already in
                flight when the breaker opened and says nothing about the API's
                current mood, so it is ignored.

        An explicit ``Retry-After`` deadline is never shortened here; only the
        clock (or :meth:`reset`) can clear it.
        """
        if epoch is not None and epoch < self._epoch:
            logger.debug(
                "Ignoring a stale success from epoch %d (current %d); the circuit "
                "breaker stays open",
                epoch,
                self._epoch,
            )
            return
        if self._failures or self._open_until is not None or self._consecutive_trips:
            logger.debug("Qobuz circuit breaker reset by a successful request")
        self._failures.clear()
        self._consecutive_trips = 0
        self._recent_429s = 0
        self._open_until = None
        if self._retry_after_until is not None and self._retry_after_until <= self._time():
            self._retry_after_until = None

    def trip(
        self,
        seconds: float | None = None,
        *,
        count: bool = True,
        explicit: bool = False,
    ) -> None:
        """Force the breaker open, e.g. after an explicit ``Retry-After``.

        The new deadline never shortens an existing one, and every call bumps
        :attr:`epoch` so requests already in flight cannot undo it.

        Args:
            seconds: How long to hold traffic (defaults to ``cooldown``).
            count: ``False`` to avoid inflating the trip statistics when merely
                extending an existing pause.
            explicit: The deadline came from the server (``Retry-After``) or
                from an operator pause, so no success may clear it early.
        """
        now = self._time()
        duration = self.cooldown if seconds is None else max(0.0, float(seconds))
        deadline = now + duration
        self._open_until = max(self._open_until or 0.0, deadline)
        if explicit:
            self._retry_after_until = max(self._retry_after_until or 0.0, deadline)
        self._epoch += 1
        if count:
            self._total_trips += 1

    def reset(self) -> None:
        """Clear everything, including an explicit pause — a manual "resume now"."""
        self._failures.clear()
        self._open_until = None
        self._retry_after_until = None
        self._consecutive_trips = 0
        self._recent_429s = 0

    def stats(self) -> dict[str, Any]:
        """Snapshot for the dashboard."""
        remaining = self.seconds_remaining()
        return {
            "open": remaining > 0.0,
            "seconds_remaining": remaining,
            "open_until": self.open_until_wall,
            "recent_failures": self.recent_failures,
            "recent_429s": self._recent_429s,
            "consecutive_trips": self._consecutive_trips,
            "total_trips": self._total_trips,
            "threshold": self.threshold,
            "cooldown": self.cooldown,
            "epoch": self._epoch,
        }


class RateLimiter:
    """Async token/leaky-bucket limiter enforcing interval *and* hourly cap.

    Usage::

        limiter = RateLimiter.from_settings(get_settings())
        epoch = await limiter.acquire()          # blocks until a slot is free
        response = await http.get(...)
        limiter.record_success(epoch)            # or record_failure(..., epoch=epoch)

    Always pass the epoch back. It is what stops a long-running download's
    eventual success from wiping a circuit-breaker cooldown that a *newer*
    request installed while it was streaming.

    Args:
        min_interval: Minimum seconds between two consecutive requests.
        max_per_hour: Ceiling on requests within any rolling 60-minute window.
        breaker: Circuit breaker to consult (one is created when omitted).
        time_func: Monotonic clock source; injectable for tests.
        sleep_func: Awaitable sleep; injectable for tests.
        name: Label used in log lines.
    """

    def __init__(
        self,
        min_interval: float = 2.0,
        max_per_hour: int = 1200,
        *,
        breaker: CircuitBreaker | None = None,
        time_func: TimeFunc = time.monotonic,
        sleep_func: SleepFunc | None = None,
        name: str = "qobuz",
    ) -> None:
        self.min_interval = max(0.0, float(min_interval))
        self.max_per_hour = max(1, int(max_per_hour))
        self.name = name
        self._time = time_func
        self._sleep: SleepFunc = sleep_func or asyncio.sleep
        self.breaker = breaker if breaker is not None else CircuitBreaker(time_func=time_func)

        self._lock = asyncio.Lock()
        self._timestamps: Deque[float] = deque()
        self._last_request_at: float | None = None
        self._last_request_wall: datetime | None = None
        self._served: int = 0
        self._blocked_seconds: float = 0.0

    # --------------------------------------------------------------- factory
    @classmethod
    def from_settings(
        cls,
        settings: Settings | None = None,
        *,
        time_func: TimeFunc = time.monotonic,
        sleep_func: SleepFunc | None = None,
    ) -> "RateLimiter":
        """Build a limiter (and matching breaker) from application settings."""
        settings = settings or get_settings()
        breaker = CircuitBreaker(
            threshold=settings.circuit_breaker_threshold,
            cooldown=float(settings.circuit_breaker_cooldown),
            window=float(settings.circuit_breaker_window),
            multiplier=settings.backoff_multiplier,
            time_func=time_func,
        )
        return cls(
            min_interval=settings.qobuz_min_request_interval,
            max_per_hour=settings.qobuz_max_requests_per_hour,
            breaker=breaker,
            time_func=time_func,
            sleep_func=sleep_func,
        )

    # --------------------------------------------------------------- internals
    def _prune(self, now: float) -> None:
        """Forget request timestamps older than the rolling hour."""
        cutoff = now - HOUR_SECONDS
        while self._timestamps and self._timestamps[0] <= cutoff:
            self._timestamps.popleft()

    def _wait_needed(self, now: float) -> float:
        """Seconds the caller must wait before a request may be issued."""
        wait = self.breaker.seconds_remaining(now)

        if self._last_request_at is not None and self.min_interval > 0.0:
            wait = max(wait, self.min_interval - (now - self._last_request_at))

        if len(self._timestamps) >= self.max_per_hour:
            oldest = self._timestamps[0]
            wait = max(wait, (oldest + HOUR_SECONDS) - now)

        return wait if wait > 0.0 else 0.0

    # ------------------------------------------------------------------- api
    async def acquire(self) -> int:
        """Block until a request slot is free, then consume it.

        Holds the internal lock for the whole wait, so concurrent callers are
        served first-come-first-served and the min-interval invariant cannot be
        violated by a race.

        Returns:
            The circuit-breaker epoch in force when the slot was granted. Pass
            it back to :meth:`record_success` / :meth:`record_failure` so an
            outcome that was already in flight when the breaker tripped cannot
            clear the cooldown.
        """
        async with self._lock:
            slept = 0.0
            while True:
                now = self._time()
                self._prune(now)
                wait = self._wait_needed(now)
                if wait <= 0.0:
                    break
                if slept == 0.0:
                    logger.debug(
                        "Rate limiter [%s] holding request for %.2fs", self.name, wait
                    )
                slept += wait
                await self._sleep(wait)

            stamp = self._time()
            self._timestamps.append(stamp)
            self._last_request_at = stamp
            self._last_request_wall = _utc_now()
            self._served += 1
            self._blocked_seconds += slept
            return self.breaker.epoch

    @property
    def epoch(self) -> int:
        """Current circuit-breaker epoch (see :meth:`acquire`)."""
        return self.breaker.epoch

    def record_success(self, epoch: int | None = None) -> None:
        """Tell the breaker the last request was healthy.

        Args:
            epoch: The value :meth:`acquire` returned for this request. Omitting
                it means "this outcome is current", which is only safe when no
                other request can have been in flight.
        """
        self.breaker.record_success(epoch)

    def record_failure(
        self,
        *,
        is_rate_limit: bool = False,
        retry_after: float | None = None,
        epoch: int | None = None,
    ) -> bool:
        """Tell the breaker the last request failed; honour ``Retry-After``.

        Returns True when this failure tripped the breaker.
        """
        tripped = self.breaker.record_failure(is_rate_limit=is_rate_limit, epoch=epoch)
        # Never issue another request before the server said we may, even when
        # the failure count alone would not have opened the breaker (and even
        # when the failure itself was stale — a Retry-After only ever extends).
        if retry_after and retry_after > self.breaker.seconds_remaining():
            self.breaker.trip(retry_after, count=not tripped, explicit=True)
        return tripped

    def pause(self, seconds: float) -> None:
        """Manually hold all traffic for *seconds* (used by the indexer/UI)."""
        self.breaker.trip(seconds, explicit=True)

    def resume(self) -> None:
        """Clear a manual pause / open breaker."""
        self.breaker.reset()

    @property
    def requests_last_hour(self) -> int:
        """Requests issued inside the rolling hour."""
        self._prune(self._time())
        return len(self._timestamps)

    @property
    def budget_remaining(self) -> int:
        """Requests still available inside the rolling hour."""
        return max(0, self.max_per_hour - self.requests_last_hour)

    @property
    def next_slot_in_seconds(self) -> float:
        """Seconds until :meth:`acquire` would return without sleeping."""
        now = self._time()
        self._prune(now)
        return self._wait_needed(now)

    @property
    def last_request_at(self) -> datetime | None:
        """Wall-clock time of the most recent request (``None`` if never)."""
        return self._last_request_wall

    @property
    def paused_until(self) -> datetime | None:
        """Wall-clock time the circuit breaker reopens (``None`` if closed)."""
        return self.breaker.open_until_wall

    def stats(self) -> dict[str, Any]:
        """Snapshot for the dashboard's rate-limit widget.

        Returns the keys the UI contract requires — ``requests_last_hour``,
        ``hourly_cap``, ``min_interval``, ``next_slot_in_seconds``,
        ``paused_until`` — plus extras that map straight onto
        :class:`app.schemas.RateLimitStatusOut`.
        """
        used = self.requests_last_hour
        return {
            "requests_last_hour": used,
            "hourly_cap": self.max_per_hour,
            "min_interval": self.min_interval,
            "next_slot_in_seconds": self.next_slot_in_seconds,
            "paused_until": self.paused_until,
            "budget_remaining": max(0, self.max_per_hour - used),
            "budget_used_percent": min(100, int(round(100.0 * used / self.max_per_hour))),
            "last_request_at": self._last_request_wall,
            "circuit_open": self.breaker.is_open(),
            "circuit_open_until": self.breaker.open_until_wall,
            "recent_429s": self.breaker.recent_429s,
            "total_requests": self._served,
            "total_blocked_seconds": round(self._blocked_seconds, 2),
        }

    def __repr__(self) -> str:  # pragma: no cover - debugging helper
        return (
            f"<RateLimiter {self.name} min_interval={self.min_interval} "
            f"cap={self.max_per_hour}/h used={self.requests_last_hour}>"
        )
