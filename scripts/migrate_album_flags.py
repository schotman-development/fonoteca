#!/usr/bin/env python
"""Add the per-album flag columns to an existing Qobuzarr database, in place.

Qobuzarr has exactly one migration story and it is ``create_all``: new *tables*
appear on an existing database for free, new *columns* on an existing table
silently do not. The enrichment side tables get around that with
``ENRICHMENT_SCHEMA_VERSION``, which drops and rebuilds all five of them because
every row in them is derived and re-fetchable. That mechanism is **not**
available here and must not be reached for: ``pin_tags``, ``freeze_path`` and
``mute_integrity`` land on ``albums``, and they hold the one kind of thing
Qobuzarr cannot re-derive — a decision a person made about one release. Dropping
the table would throw the answer away along with the question.

Hence this script. Run it once against a database that predates the flags::

    ./.venv/bin/python scripts/migrate_album_flags.py            # data/qobuzarr.db
    ./.venv/bin/python scripts/migrate_album_flags.py /path/to.db
    ./.venv/bin/python scripts/migrate_album_flags.py --dry-run

Stop the server first. Every statement here is an ``ADD COLUMN`` and nothing is
destructive, but a schema change under a live SQLAlchemy connection pool is a
needless thing to explain to yourself later.

**It is safe to run twice**, which is the property that matters most: each
column is guarded by a ``PRAGMA table_info`` check, so a second run reports zero
of everything, and a run interrupted halfway is repaired by running it again.

Unlike the qid migration there is no backfill pass, and that is not an omission.
A qid must be unique per row, so SQLite's requirement that a ``NOT NULL ADD
COLUMN`` carry a *constant* default is exactly what a qid cannot have; a flag is
the opposite case. ``DEFAULT 0`` is the correct value for every existing row —
nobody has pinned, frozen or muted anything yet — so the column arrives complete
and honestly ``NOT NULL``. The one cosmetic difference from a database created
fresh by ``create_all``: SQLAlchemy emits ``BOOLEAN NOT NULL`` with the default
applied Python-side, this emits ``BOOLEAN NOT NULL DEFAULT 0``. The constraint
and the data are identical; only ``PRAGMA table_info``'s ``dflt_value`` column
differs, and nothing reads it.
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

#: ``table -> [(column, SQLite declaration)]``. The declaration carries the
#: ``NOT NULL DEFAULT 0`` because SQLite refuses a ``NOT NULL`` ``ADD COLUMN``
#: without a constant default — and here a constant default is the right answer,
#: not a compromise. See the module docstring.
COLUMNS: dict[str, list[tuple[str, str]]] = {
    "albums": [
        ("pin_tags", "BOOLEAN NOT NULL DEFAULT 0"),
        ("freeze_path", "BOOLEAN NOT NULL DEFAULT 0"),
        ("mute_integrity", "BOOLEAN NOT NULL DEFAULT 0"),
    ],
}


def table_exists(conn: sqlite3.Connection, table: str) -> bool:
    """True when *table* is present in the main schema."""
    row = conn.execute(
        "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?", (table,)
    ).fetchone()
    return row is not None


def existing_columns(conn: sqlite3.Connection, table: str) -> set[str]:
    """Column names currently on *table*, via ``PRAGMA table_info``.

    This is the guard that makes the script re-runnable. ``ADD COLUMN`` has no
    ``IF NOT EXISTS`` form in SQLite, so asking first is the only way to tell a
    second run from a failure.
    """
    return {str(row[1]) for row in conn.execute(f"PRAGMA table_info({table})")}


def add_missing_columns(
    conn: sqlite3.Connection, table: str, dry_run: bool
) -> list[str]:
    """Add whatever *table* is missing from :data:`COLUMNS`. Returns what was added."""
    present = existing_columns(conn, table)
    added: list[str] = []
    for name, declaration in COLUMNS[table]:
        if name in present:
            continue
        added.append(name)
        if not dry_run:
            conn.execute(f"ALTER TABLE {table} ADD COLUMN {name} {declaration}")
    return added


def migrate(db_path: Path, dry_run: bool = False) -> int:
    """Apply the whole migration to *db_path*. Returns a process exit code."""
    if not db_path.exists():
        print(f"No such database: {db_path}", file=sys.stderr)
        return 1

    verb = "would add" if dry_run else "added"
    conn = sqlite3.connect(str(db_path))
    try:
        conn.execute("PRAGMA foreign_keys=ON")
        for table in COLUMNS:
            if not table_exists(conn, table):
                print(f"{table}: table not present, skipping")
                continue
            added = add_missing_columns(conn, table, dry_run)
            if not dry_run:
                conn.commit()
            print(
                f"{table}: {verb} {len(added)} column(s)"
                + (f" — {', '.join(added)}" if added else "")
            )
    finally:
        conn.close()

    if dry_run:
        print("\nDry run — nothing was written.")
    else:
        print(
            "\nDone. Every existing release starts unpinned, unfrozen and "
            "unmuted, which is what it was before the columns existed."
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Add the per-album pin/freeze/mute columns. Idempotent.",
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
