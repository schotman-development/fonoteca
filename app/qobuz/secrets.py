"""Resolution, derivation, validation and caching of the Qobuz ``app_secret``.

Signed endpoints (notably ``track/getFileUrl``) require an ``app_secret`` that
Qobuz never hands out through the API — the official web player carries it
inside its JavaScript bundle. This module reproduces exactly what that player
does, so a user with their own paid subscription can use the official API.

Resolution order
----------------
1. ``QOBUZ_APP_SECRET`` from ``.env`` — an explicit override, used as-is.
2. ``data/secret.cache`` — the last secret that was proven to work.
3. Derivation from ``https://play.qobuz.com`` (see below), with every candidate
   validated against a real ``track/getFileUrl`` call before it is trusted.

Derivation
----------
* ``GET /login`` and regex out the bundle path. The version segment contains
  letters (``/resources/8.2.0-b034/bundle.js``), so a digits-only pattern
  silently fails — :data:`BUNDLE_PATH_RE` allows word characters, dots and
  dashes.
* ``GET`` the bundle, then look for, in order:

  a. the plain ``production:{api:{appId:"...",appSecret:"..."}}`` object,
  b. ``.initialSeed("<seed>", window.utimezone.<tz>)`` pairs, matched against
     ``name:"...<Timezone>",info:"...",extras:"..."`` entries. ``seed + info +
     extras`` is a base64 string whose last 44 characters are filler; dropping
     them and decoding what remains leaves the 32-character secret.

Verified against the live bundle (2026): for the ``london``/``abidjan``/
``berlin`` seeds the trim-then-decode order yields clean hex secrets, whereas
decoding first and trimming 44 *bytes* afterwards yields nothing usable. Both
orders are tried regardless — every candidate is validated against the real API
before it is trusted, so an extra wrong one costs one request, while a missing
right one costs the whole feature.

Every validated secret is registered with :func:`app.logging_conf.register_secret`
so it can never appear in a log line.
"""

from __future__ import annotations

import base64
import binascii
import hashlib
import re
import time
from typing import Any, Awaitable, Callable, Iterable, Mapping, Protocol, Sequence

import httpx

from app.config import Settings, get_settings
from app.logging_conf import get_logger, register_secret
from app.qobuz.errors import QobuzSecretError

__all__ = [
    "BUNDLE_PATH_RE",
    "DIRECT_SECRET_RE",
    "SEED_RE",
    "INFO_EXTRAS_RE",
    "PLAY_QOBUZ_BASE",
    "FALLBACK_TRACK_ID",
    "sign",
    "decode_seed_candidate",
    "decode_seed_candidates",
    "extract_bundle_path",
    "extract_candidates",
    "extract_direct_credentials",
    "load_cached_secret",
    "store_cached_secret",
    "clear_cached_secret",
    "fetch_bundle",
    "fetch_secret_candidates",
    "resolve_app_secret",
    "candidate_summary",
    "DerivationCooldown",
    "RateLimiterLike",
]

logger = get_logger(__name__)

#: Where the official web player lives.
PLAY_QOBUZ_BASE = "https://play.qobuz.com"

#: Bundle path inside the login page. The version segment contains letters and
#: dashes (e.g. ``8.2.0-b034``) — a ``[\d.]+`` pattern here is a real bug.
BUNDLE_PATH_RE = re.compile(r"/resources/[\w.\-]+/bundle\.js")

#: The app id / secret pair, when the bundle still ships it in plain sight.
DIRECT_SECRET_RE = re.compile(r'production:\{api:\{appId:"(\d+)",appSecret:"(\w*)"')

#: ``.initialSeed("<seed>", window.utimezone.<timezone>)``
SEED_RE = re.compile(r'\.initialSeed\("([\w=]+)",window\.utimezone\.([a-z]+)\)')

#: ``name:"...Berlin",info:"...",extras:"..."``
INFO_EXTRAS_RE = re.compile(
    r'name:"[\w\\/]*([A-Z][a-z]+)",info:"([\w=]+)",extras:"([\w=]+)"'
)

#: Number of trailing characters of the concatenated seed that are not the secret.
_SECRET_TAIL_LENGTH = 44

#: What a plausible app secret looks like (the real ones are 32 hex characters).
_SECRET_SHAPE_RE = re.compile(r"[0-9A-Za-z]{16,64}")

#: A track that has been streamable for years; used only to validate candidates.
FALLBACK_TRACK_ID = "64868955"

#: Format used for validation — MP3 320 is available to every paid tier.
_VALIDATION_FORMAT_ID = 5

#: Async predicate: given a candidate secret, does a signed call succeed?
Validator = Callable[[str], Awaitable[bool]]


class RateLimiterLike(Protocol):
    """The slice of :class:`app.qobuz.ratelimit.RateLimiter` this module needs.

    Fetching ``play.qobuz.com/login`` and the multi-megabyte ``bundle.js`` is
    outbound Qobuz traffic like any other, so it must be paced by the same
    global limiter, counted against ``QOBUZ_MAX_REQUESTS_PER_HOUR`` and visible
    to the circuit breaker.
    """

    async def acquire(self) -> int:
        """Block until a request slot is free; return the breaker epoch."""

    def record_success(self, epoch: int | None = None) -> None:
        """Report a healthy response."""

    def record_failure(
        self,
        *,
        is_rate_limit: bool = False,
        retry_after: float | None = None,
        epoch: int | None = None,
    ) -> bool:
        """Report a 429/5xx response."""


class DerivationCooldown:
    """Negative cache preventing a hot loop of web-player re-scrapes.

    ``get_file_url`` re-derives the app secret whenever Qobuz rejects a
    signature.  Without a cooldown a persistently failing account would fetch
    the login page and the whole bundle once per track *per attempt* — dozens of
    multi-megabyte downloads that fix nothing.  A failed derivation is
    remembered for ``cooldown`` seconds and replayed instead of retried.

    Args:
        cooldown: Seconds to suppress re-derivation after a failure.
        time_func: Monotonic clock; injectable for tests.
    """

    def __init__(
        self,
        cooldown: float = 1800.0,
        *,
        time_func: Callable[[], float] = time.monotonic,
    ) -> None:
        self.cooldown = max(0.0, float(cooldown))
        self._time = time_func
        self._blocked_until: float | None = None
        self._error: QobuzSecretError | None = None

    def seconds_remaining(self) -> float:
        """Seconds left before another derivation may be attempted."""
        if self._blocked_until is None:
            return 0.0
        remaining = self._blocked_until - self._time()
        if remaining <= 0.0:
            self._blocked_until = None
            self._error = None
            return 0.0
        return remaining

    def blocking_error(self) -> QobuzSecretError | None:
        """The remembered failure while the cooldown is active, else ``None``."""
        if self.seconds_remaining() <= 0.0:
            return None
        return self._error

    def record_failure(self, error: BaseException) -> None:
        """Remember *error* and suppress derivation for ``cooldown`` seconds."""
        if self.cooldown <= 0.0:
            return
        self._blocked_until = self._time() + self.cooldown
        message = str(error) or type(error).__name__
        self._error = QobuzSecretError(
            f"{message} (not retrying the web-player derivation for "
            f"{self.cooldown:.0f}s; set QOBUZ_APP_SECRET in .env to fix this now)"
        )

    def record_success(self) -> None:
        """Forget any remembered failure."""
        self._blocked_until = None
        self._error = None


# ---------------------------------------------------------------------------
# Signing
# ---------------------------------------------------------------------------
def sign(endpoint: str, params: Mapping[str, Any], ts: int, secret: str) -> str:
    """Build the ``request_sig`` for a signed Qobuz endpoint.

    The payload is a single string with **no separators**: the endpoint with all
    ``/`` removed, then every parameter as ``key`` immediately followed by
    ``value`` in **alphabetical key order**, then the timestamp, then the secret.

    ``track/getFileUrl`` with ``format_id=5``, ``intent="stream"`` and
    ``track_id=1`` therefore signs::

        trackgetFileUrlformat_id5intentstreamtrack_id1<ts><secret>

    Args:
        endpoint: Relative endpoint, e.g. ``"track/getFileUrl"``.
        params: Parameters to include in the signature. ``request_ts`` and
            ``request_sig`` must **not** be present; they are excluded anyway.
        ts: Unix timestamp, the same value sent as ``request_ts``.
        secret: The Qobuz app secret.

    Returns:
        The lowercase MD5 hex digest Qobuz expects.
    """
    payload = endpoint.replace("/", "")
    for key in sorted(params):
        if key in {"request_ts", "request_sig", "app_id", "user_auth_token"}:
            continue
        value = params[key]
        if value is None:
            continue
        payload += f"{key}{value}"
    payload += f"{ts}{secret}"
    return hashlib.md5(payload.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------------------
# Cache
# ---------------------------------------------------------------------------
def load_cached_secret(settings: Settings | None = None) -> str | None:
    """Return the cached secret from ``data/secret.cache``, if any."""
    settings = settings or get_settings()
    path = settings.secret_cache_path
    try:
        if not path.is_file():
            return None
        value = path.read_text(encoding="utf-8").strip()
    except OSError as exc:
        logger.warning("Could not read the app-secret cache: %s", exc)
        return None
    if not value:
        return None
    register_secret(value)
    return value


def store_cached_secret(secret: str, settings: Settings | None = None) -> None:
    """Persist a validated secret to ``data/secret.cache`` (best effort)."""
    settings = settings or get_settings()
    path = settings.secret_cache_path
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(secret.strip() + "\n", encoding="utf-8")
        try:
            path.chmod(0o600)
        except OSError:  # pragma: no cover - filesystem without chmod
            pass
    except OSError as exc:
        logger.warning("Could not write the app-secret cache: %s", exc)
        return
    logger.info(
        "Cached the derived Qobuz app secret in %s. Add QOBUZ_APP_SECRET to .env "
        "to skip the derivation entirely on the next start.",
        path,
    )


def clear_cached_secret(settings: Settings | None = None) -> None:
    """Delete the cached secret so the next resolution re-derives it."""
    settings = settings or get_settings()
    try:
        settings.secret_cache_path.unlink(missing_ok=True)
    except OSError as exc:  # pragma: no cover - unlikely
        logger.warning("Could not clear the app-secret cache: %s", exc)


# ---------------------------------------------------------------------------
# Extraction (pure, unit-testable)
# ---------------------------------------------------------------------------
def extract_bundle_path(login_html: str) -> str:
    """Find the web player's bundle path inside the login page HTML.

    Raises:
        QobuzSecretError: when the page carries no recognisable bundle path.
    """
    match = BUNDLE_PATH_RE.search(login_html or "")
    if not match:
        raise QobuzSecretError(
            "Could not find the Qobuz web-player bundle path in "
            f"{PLAY_QOBUZ_BASE}/login. The player layout has probably changed; "
            "set QOBUZ_APP_SECRET in .env to bypass the derivation."
        )
    return match.group(0)


def extract_direct_credentials(bundle: str) -> tuple[str, str] | None:
    """Return ``(app_id, app_secret)`` when the bundle ships them in the clear."""
    match = DIRECT_SECRET_RE.search(bundle or "")
    if not match:
        return None
    app_id, app_secret = match.group(1), match.group(2)
    return (app_id, app_secret) if app_secret else None


def _b64_decode_text(value: str) -> str | None:
    """Base64-decode *value* to text, tolerating embedded/absent padding.

    The concatenated seed carries an ``=`` mid-string, which a strict decoder
    rejects; on failure the padding characters are stripped and re-applied.
    """
    if not value:
        return None
    attempts = [value]
    stripped = value.replace("=", "")
    if stripped != value:
        attempts.append(stripped)

    for attempt in attempts:
        padded = attempt + "=" * (-len(attempt) % 4)
        try:
            decoded = base64.b64decode(padded)
        except (binascii.Error, ValueError):
            continue
        try:
            text = decoded.decode("utf-8").strip()
        except UnicodeDecodeError:
            continue
        if text:
            return text
    return None


def _looks_like_secret(value: str | None) -> bool:
    """Cheap sanity check so obvious garbage never costs a validation request."""
    return bool(value) and bool(_SECRET_SHAPE_RE.fullmatch(value or ""))


def decode_seed_candidates(seed: str, info: str, extras: str) -> list[str]:
    """Decode one ``seed + info + extras`` triple into candidate secrets.

    Two interpretations of "drop the 44 characters of filler" are produced, best
    first:

    1. drop the last 44 characters of the **base64** string, then decode — this
       is what the live bundle actually wants,
    2. decode the whole string, then drop the last 44 **bytes**.

    Returns:
        Ordered, de-duplicated candidates that look like secrets (possibly empty).
    """
    payload = f"{seed}{info}{extras}"
    candidates: list[str] = []

    trimmed = payload[:-_SECRET_TAIL_LENGTH]
    first = _b64_decode_text(trimmed) if trimmed else None
    if _looks_like_secret(first):
        candidates.append(first)  # type: ignore[arg-type]

    whole = _b64_decode_text(payload)
    if whole and len(whole) > _SECRET_TAIL_LENGTH:
        second = whole[:-_SECRET_TAIL_LENGTH].strip()
        if _looks_like_secret(second) and second not in candidates:
            candidates.append(second)

    return candidates


def decode_seed_candidate(seed: str, info: str, extras: str) -> str | None:
    """Best single candidate for a triple, or ``None`` when nothing decodes."""
    candidates = decode_seed_candidates(seed, info, extras)
    return candidates[0] if candidates else None


def extract_candidates(bundle: str) -> list[str]:
    """Extract every plausible app secret from the web-player bundle.

    The direct ``appSecret`` (when present) comes first, then the seed-derived
    candidates in timezone order. Duplicates are removed while preserving order.

    Raises:
        QobuzSecretError: when the bundle yields no candidates at all.
    """
    bundle = bundle or ""
    candidates: list[str] = []

    direct = extract_direct_credentials(bundle)
    if direct:
        logger.debug("Found a directly embedded app secret in the bundle")
        candidates.append(direct[1])

    seeds = SEED_RE.findall(bundle)
    info_extras = INFO_EXTRAS_RE.findall(bundle)
    by_timezone: dict[str, tuple[str, str]] = {
        name.lower(): (info, extras) for name, info, extras in info_extras
    }

    for seed, timezone_name in seeds:
        # The bundle spells the timezone lowercase in the seed call
        # (``window.utimezone.berlin``) and capitalised in the info/extras entry
        # (``name:"Europe\/Berlin"``).
        entry = by_timezone.get(timezone_name.capitalize().lower())
        if entry is None:
            entry = by_timezone.get(timezone_name.lower())
        if entry is None:
            logger.debug("No info/extras entry for seed timezone %r", timezone_name)
            continue
        candidates.extend(decode_seed_candidates(seed, entry[0], entry[1]))

    unique: list[str] = []
    for candidate in candidates:
        if candidate and candidate not in unique:
            unique.append(candidate)

    if not unique:
        raise QobuzSecretError(
            "The Qobuz web-player bundle yielded no app-secret candidates "
            f"(found {len(seeds)} seeds and {len(info_extras)} info/extras "
            "entries). Set QOBUZ_APP_SECRET in .env to bypass the derivation."
        )
    return unique


# ---------------------------------------------------------------------------
# Network
# ---------------------------------------------------------------------------
async def _gated_get(
    http_client: httpx.AsyncClient,
    url: str,
    headers: Mapping[str, str],
    limiter: RateLimiterLike | None,
) -> httpx.Response:
    """GET *url* through the shared rate limiter, reporting the outcome.

    Every play.qobuz.com fetch counts against the same hourly budget and the
    same circuit breaker as the API calls; without this the derivation was an
    invisible, unpaced hole in the rate limiting.
    """
    epoch: int | None = None
    if limiter is not None:
        epoch = await limiter.acquire()
    try:
        response = await http_client.get(url, headers=dict(headers), follow_redirects=True)
    except httpx.HTTPError:
        if limiter is not None:
            limiter.record_failure(epoch=epoch)
        raise
    if limiter is not None:
        status = response.status_code
        if status == 429 or status >= 500:
            limiter.record_failure(is_rate_limit=status == 429, epoch=epoch)
        else:
            limiter.record_success(epoch)
    return response


async def fetch_bundle(
    http_client: httpx.AsyncClient,
    *,
    settings: Settings | None = None,
    limiter: RateLimiterLike | None = None,
) -> str:
    """Download the web player's JavaScript bundle.

    Args:
        http_client: A client used **only** for play.qobuz.com. It must not
            carry the Qobuz API auth headers.
        settings: Injected settings (defaults to :func:`get_settings`).
        limiter: The shared :class:`~app.qobuz.ratelimit.RateLimiter`. Strongly
            recommended: both GETs are real outbound Qobuz traffic and the
            bundle is several megabytes. ``None`` (tests only) skips pacing.

    Raises:
        QobuzSecretError: on any network or HTTP failure.
    """
    settings = settings or get_settings()
    headers = {"User-Agent": settings.qobuz_user_agent}

    try:
        login = await _gated_get(
            http_client, f"{PLAY_QOBUZ_BASE}/login", headers, limiter
        )
        login.raise_for_status()
        bundle_path = extract_bundle_path(login.text)
        logger.debug("Qobuz web player bundle: %s", bundle_path)

        bundle = await _gated_get(
            http_client, f"{PLAY_QOBUZ_BASE}{bundle_path}", headers, limiter
        )
        bundle.raise_for_status()
    except httpx.HTTPStatusError as exc:
        raise QobuzSecretError(
            "Fetching the Qobuz web-player bundle failed with HTTP "
            f"{exc.response.status_code}. Set QOBUZ_APP_SECRET in .env to bypass "
            "the derivation."
        ) from exc
    except httpx.HTTPError as exc:
        raise QobuzSecretError(
            f"Could not reach {PLAY_QOBUZ_BASE} to derive the app secret ({exc!r}). "
            "Check connectivity, or set QOBUZ_APP_SECRET in .env."
        ) from exc

    return bundle.text


async def fetch_secret_candidates(
    http_client: httpx.AsyncClient,
    *,
    settings: Settings | None = None,
    limiter: RateLimiterLike | None = None,
) -> list[str]:
    """Download the bundle and return every candidate secret it yields."""
    bundle = await fetch_bundle(http_client, settings=settings, limiter=limiter)
    candidates = extract_candidates(bundle)
    logger.info("Derived %d app-secret candidate(s) from the web player", len(candidates))
    return candidates


async def resolve_app_secret(
    http_client: httpx.AsyncClient,
    validator: Validator | None = None,
    *,
    settings: Settings | None = None,
    force_refresh: bool = False,
    extra_candidates: Sequence[str] | None = None,
    limiter: RateLimiterLike | None = None,
    cooldown: DerivationCooldown | None = None,
) -> str:
    """Return a usable ``app_secret``, deriving and validating it if necessary.

    Args:
        http_client: Client used to fetch the web-player bundle.
        validator: Async predicate that proves a candidate works (the client
            passes a bound method that performs a signed ``track/getFileUrl``).
            When ``None``, derived candidates are accepted unverified — only
            useful in tests.
        settings: Injected settings.
        force_refresh: Ignore the ``.env`` override and the cache, and re-derive.
        extra_candidates: Additional secrets to try before deriving (e.g. one
            recovered from a previous process).
        limiter: Shared rate limiter gating the play.qobuz.com fetches.
        cooldown: Negative cache; when a previous derivation failed recently the
            remembered error is re-raised instead of re-scraping the bundle.

    Returns:
        A validated app secret, already registered for log redaction.

    Raises:
        QobuzSecretError: when nothing could be derived or nothing validated.
    """
    settings = settings or get_settings()

    if not force_refresh:
        configured = (settings.qobuz_app_secret or "").strip()
        if configured:
            register_secret(configured)
            logger.debug("Using QOBUZ_APP_SECRET from the environment")
            return configured

        cached = load_cached_secret(settings)
        if cached:
            logger.debug("Using the cached Qobuz app secret")
            return cached

    if cooldown is not None:
        remembered = cooldown.blocking_error()
        if remembered is not None:
            logger.debug(
                "Skipping app-secret derivation for another %.0fs",
                cooldown.seconds_remaining(),
            )
            raise remembered

    candidates: list[str] = [c.strip() for c in (extra_candidates or []) if c and c.strip()]
    try:
        derived = await fetch_secret_candidates(
            http_client, settings=settings, limiter=limiter
        )
    except QobuzSecretError as exc:
        if cooldown is not None:
            cooldown.record_failure(exc)
        raise
    for candidate in derived:
        if candidate not in candidates:
            candidates.append(candidate)

    if validator is None:
        secret = candidates[0]
        register_secret(secret)
        store_cached_secret(secret, settings)
        if cooldown is not None:
            cooldown.record_success()
        return secret

    failures: list[str] = []
    for index, candidate in enumerate(candidates, start=1):
        logger.debug("Validating app-secret candidate %d/%d", index, len(candidates))
        try:
            ok = await validator(candidate)
        except Exception as exc:  # noqa: BLE001 - a bad candidate must not abort the sweep
            failures.append(f"candidate {index}: {exc}")
            continue
        if ok:
            register_secret(candidate)
            store_cached_secret(candidate, settings)
            logger.info("Validated Qobuz app secret (candidate %d of %d)", index, len(candidates))
            if cooldown is not None:
                cooldown.record_success()
            return candidate
        failures.append(f"candidate {index}: rejected by Qobuz")

    detail = "; ".join(failures[:5]) or "no candidates"
    error = QobuzSecretError(
        f"None of the {len(candidates)} derived app-secret candidates were "
        f"accepted by Qobuz ({detail}). Set QOBUZ_APP_SECRET in .env — you can "
        "read it from the web player's bundle.js manually."
    )
    if cooldown is not None:
        cooldown.record_failure(error)
    raise error


def candidate_summary(candidates: Iterable[str]) -> str:
    """Describe candidates for logging without revealing them."""
    return ", ".join(f"{len(c)} chars" for c in candidates) or "none"
