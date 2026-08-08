"""The ``app_setting`` table — the settings overlay's only reader and writer.

:mod:`app.config` owns the *rules* (which keys may be overridden, what a value
may be, how the environment and the overlay combine); this module owns the
*rows*. Keeping them apart is what makes the allowlist checkable in one place:
nothing here decides whether a key is allowed, it only refuses to load a row for
a key the allowlist no longer names.

Three functions, and they follow the codebase's caller-commits convention —
except that the one HTTP entry point (``PATCH /api/settings``) commits itself,
for the same reason :meth:`app.core.enricher.Enricher.identify` does: the
request-scoped session from ``get_session`` never commits, so a write that
relied on the convention would be a green toast and a rollback.

Nothing here is called on a hot path. The overlay is read once at startup and
once per successful write; every other reader goes through
:func:`app.config.get_effective_settings`, which holds it in memory.
"""

from __future__ import annotations

from collections.abc import Iterable

from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import OVERRIDABLE_SETTINGS
from app.logging_conf import get_logger
from app.models import AppSetting

__all__ = ["clear_override", "load_overrides", "set_override"]

logger = get_logger(__name__)


async def load_overrides(session: AsyncSession) -> dict[str, str]:
    """Every stored override, as ``key -> text``.

    Rows naming a key that is no longer on the allowlist are skipped with a
    warning rather than raising or being deleted: the allowlist can legitimately
    shrink between releases, an unreadable row must never stop the application
    starting, and silently deleting somebody's setting because this version does
    not understand it is worse than ignoring it.
    """
    rows = (await session.execute(select(AppSetting.key, AppSetting.value))).all()
    overrides: dict[str, str] = {}
    for key, value in rows:
        name = str(key)
        if name not in OVERRIDABLE_SETTINGS:
            logger.warning(
                "Ignoring stored override for %r: not an overridable setting", name
            )
            continue
        overrides[name] = "" if value is None else str(value)
    return overrides


async def set_override(session: AsyncSession, key: str, value: str) -> None:
    """Store (or replace) one override. Does not commit.

    The ``flush`` is load-bearing: these sessions are ``autoflush=False``, so a
    row added and not flushed is invisible to the ``get`` that follows it, and
    writing the same key twice in one transaction would insert it twice and fail
    the primary key at commit — which is a 500 for what is a perfectly ordinary
    press of one switch twice.
    """
    row = await session.get(AppSetting, key)
    if row is None:
        session.add(AppSetting(key=key, value=value))
        await session.flush()
        return
    row.value = value


async def clear_override(session: AsyncSession, keys: Iterable[str]) -> int:
    """Delete the rows for *keys*, returning how many really went. Does not commit.

    Deleting the row **is** "reset to .env" — there is no stored copy of the
    environment value to restore, and there must not be one: a copy taken today
    would go stale the moment somebody edits the file, and then the reset button
    would restore a value that is nowhere in the configuration.
    """
    wanted = [str(key) for key in keys]
    if not wanted:
        return 0
    result = await session.execute(
        delete(AppSetting).where(AppSetting.key.in_(wanted))
    )
    return int(result.rowcount or 0)
