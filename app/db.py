"""Database plumbing: async engine, session factory and schema bootstrap.

SQLite is configured for concurrent-ish use by a web app plus background
workers: WAL journalling, a generous ``busy_timeout``, and enforced foreign
keys (SQLite has them off by default, which would silently break our
``ON DELETE CASCADE`` rules).

Two ways to get a session:

* :func:`get_session` — a FastAPI dependency (``Depends(get_session)``).
* :func:`session_scope` — an ``async with`` context manager for background
  workers; it commits on clean exit and rolls back on exception.
"""

from __future__ import annotations

import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Any

from sqlalchemy import event, text
from sqlalchemy.engine.interfaces import DBAPIConnection
from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)
from sqlalchemy.pool import ConnectionPoolEntry

from app.config import Settings, get_settings
from app.models import Base

__all__ = [
    "AsyncSessionLocal",
    "engine",
    "get_engine",
    "get_session",
    "get_sessionmaker",
    "init_db",
    "dispose_engine",
    "session_scope",
    "healthcheck",
]

logger = logging.getLogger(__name__)

_settings: Settings = get_settings()

# Make sure the directory holding the SQLite file exists before the engine is
# created; SQLite will not create intermediate directories for us.
_settings.data_path.mkdir(parents=True, exist_ok=True)

#: Process-wide async engine.
engine: AsyncEngine = create_async_engine(
    _settings.database_url,
    echo=False,
    future=True,
    pool_pre_ping=True,
    connect_args={"timeout": 30, "check_same_thread": False},
)

#: Process-wide session factory. ``expire_on_commit=False`` keeps ORM objects
#: usable (for templating and JSON serialisation) after the session commits.
AsyncSessionLocal: async_sessionmaker[AsyncSession] = async_sessionmaker(
    bind=engine,
    class_=AsyncSession,
    expire_on_commit=False,
    autoflush=False,
)


@event.listens_for(engine.sync_engine, "connect")
def _set_sqlite_pragmas(
    dbapi_connection: DBAPIConnection, _record: ConnectionPoolEntry
) -> None:
    """Apply the per-connection SQLite pragmas Qobuzarr relies on."""
    cursor = dbapi_connection.cursor()
    try:
        cursor.execute("PRAGMA journal_mode=WAL")
        cursor.execute("PRAGMA busy_timeout=30000")
        cursor.execute("PRAGMA foreign_keys=ON")
        cursor.execute("PRAGMA synchronous=NORMAL")
        cursor.execute("PRAGMA temp_store=MEMORY")
    finally:
        cursor.close()


def get_engine() -> AsyncEngine:
    """Return the process-wide async engine."""
    return engine


def get_sessionmaker() -> async_sessionmaker[AsyncSession]:
    """Return the process-wide async session factory."""
    return AsyncSessionLocal


async def init_db() -> None:
    """Create the data/library directories and all tables if they are missing.

    Safe to call on every startup; ``create_all`` is a no-op for existing
    tables. This does not perform migrations — the schema is created as-is.
    """
    settings = get_settings()
    settings.ensure_directories()

    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)

    logger.info("Database ready at %s", settings.db_path)


async def dispose_engine() -> None:
    """Close all pooled connections. Call from the app's shutdown handler."""
    await engine.dispose()
    logger.info("Database engine disposed")


async def get_session() -> AsyncIterator[AsyncSession]:
    """FastAPI dependency yielding a session that is closed after the request.

    The caller is responsible for committing; on an unhandled exception the
    session is rolled back before being closed.
    """
    session = AsyncSessionLocal()
    try:
        yield session
    except Exception:
        await session.rollback()
        raise
    finally:
        await session.close()


@asynccontextmanager
async def session_scope() -> AsyncIterator[AsyncSession]:
    """Transactional session for background workers.

    Commits when the block exits cleanly, rolls back on any exception, and
    always closes the session::

        async with session_scope() as session:
            session.add(Activity(event="indexer", message="..."))
    """
    session = AsyncSessionLocal()
    try:
        yield session
        await session.commit()
    except Exception:
        await session.rollback()
        raise
    finally:
        await session.close()


async def healthcheck() -> dict[str, Any]:
    """Probe the database for ``/health``.

    Returns a dict with ``ok`` plus the effective journal mode, or ``ok=False``
    and an ``error`` string when the database cannot be reached.
    """
    try:
        async with engine.connect() as conn:
            await conn.execute(text("SELECT 1"))
            journal_mode = (await conn.execute(text("PRAGMA journal_mode"))).scalar_one()
        return {"ok": True, "journal_mode": str(journal_mode)}
    except Exception as exc:  # noqa: BLE001 - reported, not swallowed
        logger.warning("Database healthcheck failed: %s", exc)
        return {"ok": False, "error": str(exc)}
