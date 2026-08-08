#!/usr/bin/env python
"""Add ``state`` and ``note`` to an existing ``folder_bindings`` table, in place.

Qobuzarr has exactly one migration story and it is ``create_all``: new *tables*
appear on an existing database for free, new *columns* on an existing table
silently do not — the symptom is ``no such column: folder_bindings.state``
raised out of whichever command happened to read it, rather than anything at
startup.

This one is unusual in *why* it is needed, and the reason is worth writing down
because it is a trap the next person will fall into the same way. ``folder_bindings``
is a new table, so it needed no migration when it shipped — ``create_all``
created it. The columns below were added a few hours later, in the same piece of
work, by which time the table already existed on the one database anybody had run
the intermediate build against. ``create_all`` saw a table of that name, decided
there was nothing to do, and the new columns never appeared. **A brand-new table
stops being free the moment anybody has run the build that created it**, and
"nobody has that build yet" is only true until it isn't.

A fresh database needs none of this. Run it once against a database created
by a build between the two::

    ./.venv/bin/python scripts/migrate_folder_bindings.py            # data/qobuzarr.db
    ./.venv/bin/python scripts/migrate_folder_bindings.py /path/to.db
    ./.venv/bin/python scripts/migrate_folder_bindings.py --dry-run

Stop the server first — every statement here is an ``ADD COLUMN`` and nothing is
destructive, but a schema change under a live connection pool is a needless
thing to explain to yourself later.

**It is safe to run twice.** Each column is guarded by a ``PRAGMA table_info``
check, so a second run reports zero of everything and a run interrupted halfway
is repaired by running it again.

Every existing row becomes ``state='bound'``, which is the correct value rather
than a compromise: before the column the table could express exactly one thing —
*this directory is that Qobuz release* — so every row already in it means
``bound``. The other value, ``not_in_catalogue``, is one only a person can write
(see :class:`app.models.FolderBinding`), so no pre-existing row can have been
owed it.

**This rebuilds the table rather than adding columns, and it has to.**
``album_id`` was ``NOT NULL`` and must become nullable, because a
``not_in_catalogue`` row names no album; SQLite cannot drop a ``NOT NULL``
constraint with ``ALTER TABLE``. Storing ``''`` instead would have avoided the
rebuild and was rejected: the loader happens to test the value for truth, so it
would work today and break silently the first time anything tests it for
``None`` — an empty string that means "no album" is the kind of second
representation that outlives whoever chose it. So: create, copy, drop, rename,
all inside one transaction, with the rows preserved.
"""

from __future__ import annotations

import argparse
import sqlite3
import sys
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parent.parent
if str(_REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(_REPO_ROOT))

DEFAULT_DB = _REPO_ROOT / "data" / "qobuzarr.db"

TABLE = "folder_bindings"

#: The shape ``create_all`` produces today. Kept as one literal so the rebuild
#: and the ORM can be compared by eye rather than reconstructed from an ALTER.
CREATE_CURRENT = """
CREATE TABLE folder_bindings_migrated (
    path VARCHAR(1024) NOT NULL,
    state VARCHAR(24) NOT NULL DEFAULT 'bound',
    album_id VARCHAR(64),
    method VARCHAR(32) NOT NULL DEFAULT 'audio-barcode',
    barcode VARCHAR(32),
    mb_release_group_mbid VARCHAR(64),
    note TEXT,
    bound_at DATETIME NOT NULL,
    PRIMARY KEY (path)
)
"""

#: Columns carried over. ``state`` and ``note`` are absent on purpose — the
#: former takes its constant default, the latter is a sentence only a person
#: writes and no existing row can have one.
CARRIED = ("path", "album_id", "method", "barcode", "mb_release_group_mbid", "bound_at")


def table_exists(conn: sqlite3.Connection, table: str) -> bool:
    """True when *table* is present in the main schema."""
    row = conn.execute(
        "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?", (table,)
    ).fetchone()
    return row is not None


def existing_columns(conn: sqlite3.Connection, table: str) -> set[str]:
    """Column names currently on *table*, via ``PRAGMA table_info``.

    The guard that makes the script re-runnable: asking first is the only way to
    tell a second run from a failure.
    """
    return {str(row[1]) for row in conn.execute(f"PRAGMA table_info({table})")}


def rebuild(conn: sqlite3.Connection) -> int:
    """Recreate the table in its current shape, preserving every row.

    Create, copy, drop, rename — the standard SQLite dance for a constraint
    change, in one transaction so an interruption leaves the original in place
    rather than half a table.
    """
    rows = int(conn.execute(f"SELECT COUNT(*) FROM {TABLE}").fetchone()[0])
    columns = ", ".join(CARRIED)
    conn.execute("BEGIN")
    conn.execute(CREATE_CURRENT)
    conn.execute(
        f"INSERT INTO folder_bindings_migrated ({columns}) SELECT {columns} FROM {TABLE}"
    )
    conn.execute(f"DROP TABLE {TABLE}")
    conn.execute(f"ALTER TABLE folder_bindings_migrated RENAME TO {TABLE}")
    conn.execute(f"CREATE INDEX ix_folder_bindings_album_id ON {TABLE} (album_id)")
    conn.commit()
    return rows


def migrate(db_path: Path, dry_run: bool = False) -> int:
    """Apply the whole migration to *db_path*. Returns a process exit code."""
    if not db_path.exists():
        print(f"No such database: {db_path}", file=sys.stderr)
        return 1

    conn = sqlite3.connect(str(db_path))
    try:
        conn.execute("PRAGMA foreign_keys=OFF")  # nothing references this table
        if not table_exists(conn, TABLE):
            # The ordinary case on a database that predates the feature
            # entirely: create_all will make the whole table, correctly.
            print(f"{TABLE}: not present — create_all will add it. Nothing to do.")
            return 0

        present = existing_columns(conn, TABLE)
        missing = sorted({"state", "note"} - present)
        if not missing:
            print(f"{TABLE}: already current. Nothing to do.")
            return 0

        rows = int(conn.execute(f"SELECT COUNT(*) FROM {TABLE}").fetchone()[0])
        print(f"{TABLE}: missing {', '.join(missing)}; {rows} row(s) to carry over")
        if dry_run:
            print("\nDry run — nothing was written.")
            return 0

        carried = rebuild(conn)
        print(f"{TABLE}: rebuilt, {carried} row(s) preserved, all reading 'bound'")
    finally:
        conn.close()

    print("\nDone.")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Add folder_bindings.state and .note. Idempotent.",
    )
    parser.add_argument(
        "database",
        nargs="?",
        default=str(DEFAULT_DB),
        help=f"path to the SQLite database (default: {DEFAULT_DB})",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="report what would change without writing anything",
    )
    args = parser.parse_args(argv)
    return migrate(Path(args.database), dry_run=args.dry_run)


if __name__ == "__main__":  # pragma: no cover - script entry point
    raise SystemExit(main())
