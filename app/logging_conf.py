"""Logging setup for Fonoteca.

Call :func:`setup_logging` exactly once, as early as possible during startup.
It configures the root logger with two handlers — stdout and a rotating file in
``data/`` — and installs :class:`RedactionFilter` on both.

The redaction filter is not optional decoration: Qobuz auth tokens, the derived
app secret and signed ``getFileUrl`` results are credentials, and they turn up
naturally in exception text and in httpx's own log records. The filter masks
them before anything is written.
"""

from __future__ import annotations

import logging
import logging.handlers
import re
import sys
from pathlib import Path
from typing import Iterable

from app.config import LogLevel, Settings, get_settings

__all__ = [
    "REDACTED",
    "RedactionFilter",
    "setup_logging",
    "register_secret",
    "redact",
    "get_logger",
]

#: Text substituted in place of anything sensitive.
REDACTED = "***REDACTED***"

#: Console/file line format.
LOG_FORMAT = "%(asctime)s %(levelname)-8s %(name)s: %(message)s"
DATE_FORMAT = "%Y-%m-%d %H:%M:%S"

#: Query/header parameters whose values must never be logged.
_SENSITIVE_KEYS = (
    "user_auth_token",
    "x-user-auth-token",
    "app_secret",
    "appsecret",
    "request_sig",
    "password",
    "auth_token",
    "token",
)

#: ``key=value`` (query-string or kwargs style) for any sensitive key.
_KV_PATTERN = re.compile(
    r"(?i)\b(" + "|".join(re.escape(k) for k in _SENSITIVE_KEYS) + r")\s*[=:]\s*['\"]?([^\s'\"&,;)\]}]+)"
)

#: ``url=<anything>`` — the signed file URL returned by ``track/getFileUrl``.
#: The character class deliberately allows ``&`` and ``?`` so the whole query
#: string is swallowed, not just the part before the first parameter separator.
_URL_KV_PATTERN = re.compile(r"(?i)\burl\s*[=:]\s*['\"]?(https?://[^\s'\"<>\]}]+)")

#: Bare signed streaming URLs that appear without a ``url=`` prefix.
_SIGNED_URL_PATTERN = re.compile(
    r"(?i)https?://[^\s'\"]*(?:qobuz|akamaized|streaming)[^\s'\"]*"
    r"[?&](?:eid|uid|hmac|Expires|Signature|Key-Pair-Id)=[^\s'\"]*"
)

#: Literal secret values registered at runtime (token, app secret, ...).
_LITERAL_SECRETS: set[str] = set()


def register_secret(value: str | None) -> None:
    """Register a literal string that must be masked everywhere it appears.

    Called for the auth token and the app secret at startup, and again by the
    secret-derivation code once a candidate has been validated. Values shorter
    than six characters are ignored to avoid masking innocuous text.
    """
    if value and len(value) >= 6:
        _LITERAL_SECRETS.add(value)


def redact(message: str) -> str:
    """Return *message* with credentials and signed URLs masked."""
    if not message:
        return message

    result = message
    for secret in _LITERAL_SECRETS:
        if secret in result:
            result = result.replace(secret, REDACTED)

    result = _KV_PATTERN.sub(lambda m: f"{m.group(1)}={REDACTED}", result)
    result = _URL_KV_PATTERN.sub(lambda m: f"url={REDACTED}", result)
    result = _SIGNED_URL_PATTERN.sub(REDACTED, result)
    return result


class RedactionFilter(logging.Filter):
    """Logging filter that rewrites each record's rendered message.

    The record is rendered once (``record.getMessage()``), redacted, stored back
    on ``record.msg`` and ``record.args`` is cleared, so downstream handlers and
    formatters cannot re-expand the original arguments.
    """

    def filter(self, record: logging.LogRecord) -> bool:
        try:
            message = record.getMessage()
        except Exception:  # noqa: BLE001 - a broken format string must not kill logging
            message = str(record.msg)

        redacted = redact(message)
        if redacted != message or record.args:
            record.msg = redacted
            record.args = ()

        if record.exc_text:
            record.exc_text = redact(record.exc_text)
        return True


def _resolve_level(level: str | int | LogLevel | None, settings: Settings) -> int:
    """Turn any accepted level representation into a stdlib logging level int."""
    if level is None:
        level = settings.log_level
    if isinstance(level, LogLevel):
        return int(getattr(logging, level.value, logging.INFO))
    if isinstance(level, int):
        return level
    return int(getattr(logging, str(level).upper(), logging.INFO))


def setup_logging(
    level: str | int | LogLevel | None = None,
    *,
    log_file: Path | None = None,
    settings: Settings | None = None,
    quiet_loggers: Iterable[str] = (
        "httpx",
        "httpcore",
        "aiosqlite",
        "sqlalchemy.engine",
        "apscheduler.executors.default",
        "multipart",
    ),
) -> logging.Logger:
    """Configure root logging and return the root logger.

    Args:
        level: Override for the configured ``LOG_LEVEL``.
        log_file: Override for ``data/fonoteca.log``.
        settings: Injected settings (defaults to :func:`get_settings`).
        quiet_loggers: Chatty third-party loggers pinned to WARNING.

    Idempotent: existing handlers installed by a previous call are removed
    first, so calling it twice will not duplicate output.
    """
    settings = settings or get_settings()
    resolved_level = _resolve_level(level, settings)
    target_file = log_file or settings.log_path
    target_file.parent.mkdir(parents=True, exist_ok=True)

    formatter = logging.Formatter(LOG_FORMAT, datefmt=DATE_FORMAT)
    redaction = RedactionFilter() if settings.log_redact_secrets else None

    if settings.log_redact_secrets:
        register_secret(settings.qobuz_user_auth_token)
        register_secret(settings.qobuz_app_secret)

    root = logging.getLogger()
    for handler in list(root.handlers):
        root.removeHandler(handler)
        try:
            handler.close()
        except Exception:  # noqa: BLE001 - closing a foreign handler is best-effort
            pass

    stream_handler = logging.StreamHandler(stream=sys.stdout)
    stream_handler.setFormatter(formatter)

    file_handler = logging.handlers.RotatingFileHandler(
        filename=str(target_file),
        maxBytes=settings.log_max_bytes,
        backupCount=settings.log_backup_count,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)

    for handler in (stream_handler, file_handler):
        if redaction is not None:
            handler.addFilter(redaction)
        root.addHandler(handler)

    root.setLevel(resolved_level)

    for name in quiet_loggers:
        logging.getLogger(name).setLevel(max(resolved_level, logging.WARNING))

    logging.getLogger(__name__).debug("Logging initialised at %s", logging.getLevelName(resolved_level))
    return root


def get_logger(name: str) -> logging.Logger:
    """Convenience wrapper around :func:`logging.getLogger`."""
    return logging.getLogger(name)
