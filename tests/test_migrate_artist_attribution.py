"""The one-off ALTER TABLE for the attribution columns.

``create_all`` adds tables to an existing database and never columns, and these
four land on ``albums`` and ``artists`` — Qobuz-owned tables that cannot be
dropped and rebuilt the way the enrichment side tables are. So there is a
script, and the property that matters about a script somebody runs against their
only copy of their library database is that **running it twice is not a
failure**.

The property that matters *second* is what the new columns say about the rows
that were already there, and here that is deliberately **nothing**: the two
measurement columns arrive ``NULL``, meaning "nobody has looked", which
``desired_status`` treats exactly as it behaved before they existed. A migration
that backfilled them would assert a measurement that never happened — and for
``credit_names`` it would assert the strongest available claim about every
release in the library.
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

import migrate_artist_attribution as migration  # noqa: E402 - after the path bootstrap

from app.models import Base  # noqa: E402

ALBUM_COLUMNS = ("guest_appearance", "credit_names")
ARTIST_COLUMNS = ("include_guest_appearances", "credit_filter_json")


def _build(path: Path) -> None:
    async def build() -> None:
        engine = create_async_engine(f"sqlite+aiosqlite:///{path}")
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        await engine.dispose()

    asyncio.run(build())


def _old_database(path: Path) -> None:
    """A database shaped the way it was before attribution existed.

    Built from the application's own metadata and then rolled back with
    ``DROP COLUMN``, rather than hand-written here and slowly drifting from it.
    The seed rows go in *after* the rollback, so they are inserted by a schema
    that has never heard of these columns — the state a real database is in.
    """
    _build(path)
    conn = sqlite3.connect(str(path))
    try:
        for column in ALBUM_COLUMNS:
            conn.execute(f"ALTER TABLE albums DROP COLUMN {column}")
        for column in ARTIST_COLUMNS:
            conn.execute(f"ALTER TABLE artists DROP COLUMN {column}")
        conn.execute(
            "INSERT INTO artists (id, qid, name, monitored, monitor_mode, "
            "quality_profile, accepted_release_types, albums_count, added_at) "
            "VALUES ('322476', 'qa_1', 'Boaz', 1, 'all', 'default', 'album', "
            "0, CURRENT_TIMESTAMP)"
        )
        # The per-album switches are spelled out because they are ``NOT NULL``
        # with a Python-side default only: a raw INSERT never sees it. That is
        # also why ``artists.include_guest_appearances`` carries a
        # ``server_default`` — a NOT NULL column that fixtures and tooling
        # cannot insert around is a column that breaks them.
        conn.execute(
            "INSERT INTO albums (id, qid, artist_id, title, release_type, "
            "tracks_count, media_count, hires, status, monitored, "
            "pin_tags, freeze_path, mute_integrity, added_at) "
            "VALUES ('alb1', 'qr_1', '322476', 'Stone Cold Sober', 'single', 1, 1, 1, "
            "'wanted', 1, 0, 0, 0, CURRENT_TIMESTAMP)"
        )
        conn.commit()
    finally:
        conn.close()


def _columns(path: Path, table: str) -> set[str]:
    conn = sqlite3.connect(str(path))
    try:
        return {str(row[1]) for row in conn.execute(f"PRAGMA table_info({table})")}
    finally:
        conn.close()


@pytest.fixture(name="database")
def database_fixture(tmp_path: Path) -> Path:
    path = tmp_path / "qobuzarr.db"
    _old_database(path)
    assert not (_columns(path, "albums") & set(ALBUM_COLUMNS))
    assert not (_columns(path, "artists") & set(ARTIST_COLUMNS))
    return path


def test_the_columns_are_added(database: Path) -> None:
    assert migration.migrate(database) == 0
    assert set(ALBUM_COLUMNS) <= _columns(database, "albums")
    assert set(ARTIST_COLUMNS) <= _columns(database, "artists")


def test_existing_releases_arrive_unmeasured_not_decided(database: Path) -> None:
    """``NULL``, and the distinction is the whole design.

    ``guest_appearance = 0`` would assert every release has been checked and is
    the artist's own; ``credit_names = '[]'`` would assert every release has
    been read and no credit qualified — which, with a filter on, refuses the
    lot. Neither measurement happened. ``NULL`` is the only honest value and
    ``desired_status`` treats it as a no-op.
    """
    migration.migrate(database)
    conn = sqlite3.connect(str(database))
    try:
        row = conn.execute(
            "SELECT guest_appearance, credit_names FROM albums WHERE id = 'alb1'"
        ).fetchone()
    finally:
        conn.close()
    assert row == (None, None)


def test_existing_artists_start_with_no_filter_and_no_guests(database: Path) -> None:
    """The two decision columns *are* complete on arrival: nobody has asked for
    guest appearances and nobody has picked a credit, so these are true values
    for every existing row rather than a placeholder."""
    migration.migrate(database)
    conn = sqlite3.connect(str(database))
    try:
        row = conn.execute(
            "SELECT include_guest_appearances, credit_filter_json "
            "FROM artists WHERE id = '322476'"
        ).fetchone()
    finally:
        conn.close()
    assert row == (0, None)


def test_running_it_twice_is_not_a_failure(database: Path) -> None:
    """``ADD COLUMN`` has no ``IF NOT EXISTS`` form, so a second run without the
    PRAGMA guard is a ``duplicate column name`` error — indistinguishable, to
    somebody reading it, from the migration having gone wrong."""
    assert migration.migrate(database) == 0
    assert migration.migrate(database) == 0
    assert set(ALBUM_COLUMNS) <= _columns(database, "albums")


def test_a_second_run_reports_nothing_added(
    database: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    migration.migrate(database)
    capsys.readouterr()
    migration.migrate(database)
    assert capsys.readouterr().out.count("added 0 column(s)") == 2


def test_a_dry_run_writes_nothing(database: Path) -> None:
    assert migration.migrate(database, dry_run=True) == 0
    assert not (_columns(database, "albums") & set(ALBUM_COLUMNS))
    assert not (_columns(database, "artists") & set(ARTIST_COLUMNS))


def test_a_dry_run_says_what_it_would_do(
    database: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    migration.migrate(database, dry_run=True)
    out = capsys.readouterr().out
    assert "would add 2 column(s)" in out
    for column in ALBUM_COLUMNS + ARTIST_COLUMNS:
        assert column in out


def test_a_missing_database_is_an_error_not_a_new_one(tmp_path: Path) -> None:
    """Creating one would leave the user with an empty library and no warning."""
    absent = tmp_path / "nowhere.db"
    assert migration.migrate(absent) == 1
    assert not absent.exists()


def test_an_up_to_date_database_needs_nothing(tmp_path: Path) -> None:
    """The case a fresh install is in: ``create_all`` already made the columns."""
    path = tmp_path / "fresh.db"
    _build(path)
    assert migration.migrate(path) == 0
    assert set(ALBUM_COLUMNS) <= _columns(path, "albums")
    assert set(ARTIST_COLUMNS) <= _columns(path, "artists")


def test_the_migrated_schema_is_the_one_the_orm_expects(tmp_path: Path) -> None:
    """A migrated database and a fresh one must not diverge in what exists."""
    migrated = tmp_path / "migrated.db"
    _old_database(migrated)
    migration.migrate(migrated)

    fresh = tmp_path / "fresh.db"
    _build(fresh)

    for table in ("albums", "artists"):
        assert _columns(migrated, table) == _columns(fresh, table)
