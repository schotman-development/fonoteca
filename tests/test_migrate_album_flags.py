"""The one-off ALTER TABLE for the per-release switches.

``create_all`` adds tables to an existing database and never columns, and these
three land on ``albums`` — a Qobuz-owned table that cannot be dropped and
rebuilt the way the enrichment side tables are. So there is a script, and the
property that matters about a script somebody runs against their only copy of
their library database is that **running it twice is not a failure**.

The tests build a database from the real ``Base.metadata`` and then drop the
columns back out of it, which is as close to "a database that predates the
feature" as SQLite allows without checking a binary fixture into the repo.
"""

from __future__ import annotations

import asyncio
import sqlite3
import sys
from pathlib import Path

import pytest
from sqlalchemy.ext.asyncio import create_async_engine

_REPO_ROOT = Path(__file__).resolve().parent.parent
_SCRIPTS = _REPO_ROOT / "scripts"
if str(_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS))

import migrate_album_flags as migration  # noqa: E402 - after the path bootstrap

from app.models import Album, AlbumStatus, Artist, Base  # noqa: E402

FLAGS = ("pin_tags", "freeze_path", "mute_integrity")


def _old_database(path: Path) -> None:
    """A database shaped the way it was before the switches existed.

    SQLite has had ``DROP COLUMN`` since 3.35, which is what makes this honest:
    the schema is built by the application's own metadata and then rolled back,
    rather than hand-written here and slowly drifting away from it.
    """

    async def build() -> None:
        engine = create_async_engine(f"sqlite+aiosqlite:///{path}")
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        await engine.dispose()

    asyncio.run(build())

    conn = sqlite3.connect(str(path))
    try:
        # Rolled back *before* the rows go in, so the seed release is inserted
        # by a schema that has never heard of the switches — which is the state
        # a real database being migrated is actually in.
        for column in FLAGS:
            conn.execute(f"ALTER TABLE albums DROP COLUMN {column}")
        conn.execute(
            "INSERT INTO artists (id, qid, name, monitored, monitor_mode, "
            "quality_profile, accepted_release_types, albums_count, added_at) "
            "VALUES ('a1', 'qa_1', 'Nils Frahm', 1, 'all', 'default', 'album', "
            "0, CURRENT_TIMESTAMP)"
        )
        conn.execute(
            "INSERT INTO albums (id, qid, artist_id, title, release_type, "
            "tracks_count, media_count, hires, status, monitored, added_at) "
            "VALUES ('al1', 'qr_1', 'a1', 'All Melody', 'album', 12, 1, 1, "
            "'downloaded', 1, CURRENT_TIMESTAMP)"
        )
        conn.commit()
    finally:
        conn.close()


def _columns(path: Path) -> set[str]:
    conn = sqlite3.connect(str(path))
    try:
        return {str(row[1]) for row in conn.execute("PRAGMA table_info(albums)")}
    finally:
        conn.close()


@pytest.fixture(name="database")
def database_fixture(tmp_path: Path) -> Path:
    path = tmp_path / "qobuzarr.db"
    _old_database(path)
    assert not (_columns(path) & set(FLAGS)), "the fixture did not roll the schema back"
    return path


def test_the_columns_are_added(database: Path) -> None:
    assert migration.migrate(database) == 0
    assert set(FLAGS) <= _columns(database)


def test_every_existing_release_starts_with_the_switches_off(database: Path) -> None:
    """The default is what an existing row *was* before the columns existed.

    A ``NOT NULL`` column with the wrong constant default silently changes every
    release in the library, and the symptom — enrichment quietly stopping, or a
    corruption alarm going quiet — is nothing anybody would connect to a
    migration they ran once.
    """
    migration.migrate(database)

    conn = sqlite3.connect(str(database))
    try:
        row = conn.execute(
            f"SELECT {', '.join(FLAGS)} FROM albums WHERE id = 'al1'"
        ).fetchone()
    finally:
        conn.close()

    assert row == (0, 0, 0)


def test_running_it_twice_is_not_a_failure(database: Path) -> None:
    """The whole re-runnability story, and the only reason for the PRAGMA guard.

    ``ADD COLUMN`` has no ``IF NOT EXISTS`` form, so a second run without the
    guard is a ``duplicate column name`` error — indistinguishable, to somebody
    reading it, from the migration having gone wrong.
    """
    assert migration.migrate(database) == 0
    assert migration.migrate(database) == 0
    assert set(FLAGS) <= _columns(database)


def test_a_second_run_reports_nothing_added(
    database: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    migration.migrate(database)
    capsys.readouterr()
    migration.migrate(database)
    assert "added 0 column(s)" in capsys.readouterr().out


def test_a_dry_run_writes_nothing(database: Path) -> None:
    assert migration.migrate(database, dry_run=True) == 0
    assert not (_columns(database) & set(FLAGS)), "a dry run must not touch the schema"


def test_a_dry_run_says_what_it_would_do(
    database: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    migration.migrate(database, dry_run=True)
    out = capsys.readouterr().out
    assert "would add 3 column(s)" in out
    for column in FLAGS:
        assert column in out


def test_a_missing_database_is_an_error_not_a_new_one(tmp_path: Path) -> None:
    """Creating one would leave the user with an empty library and no warning."""
    absent = tmp_path / "nowhere.db"
    assert migration.migrate(absent) == 1
    assert not absent.exists()


def test_an_up_to_date_database_needs_nothing(tmp_path: Path) -> None:
    """The case a fresh install is in: ``create_all`` already made the columns."""
    path = tmp_path / "fresh.db"

    async def build() -> None:
        engine = create_async_engine(f"sqlite+aiosqlite:///{path}")
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        await engine.dispose()

    asyncio.run(build())

    assert migration.migrate(path) == 0
    assert set(FLAGS) <= _columns(path)


def test_the_migrated_schema_is_the_one_the_orm_expects(database: Path) -> None:
    """A migrated database has to be usable by the application, not merely valid."""
    migration.migrate(database)

    async def go() -> tuple[bool, bool, bool]:
        engine = create_async_engine(f"sqlite+aiosqlite:///{database}")
        from sqlalchemy.ext.asyncio import async_sessionmaker

        maker = async_sessionmaker(engine, expire_on_commit=False)
        async with maker() as session:
            album = await session.get(Album, "al1")
            album.freeze_path = True
            await session.commit()
        async with maker() as session:
            album = await session.get(Album, "al1")
            assert isinstance(await session.get(Artist, "a1"), Artist)
            assert album.status is AlbumStatus.DOWNLOADED
            answer = (album.pin_tags, album.freeze_path, album.mute_integrity)
        await engine.dispose()
        return answer

    assert asyncio.run(go()) == (False, True, False)
