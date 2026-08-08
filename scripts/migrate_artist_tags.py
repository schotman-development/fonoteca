#!/usr/bin/env python
"""Add the hand-editable artist tag columns to an existing database, in place.

Qobuzarr has exactly one migration story and it is ``create_all``: new *tables*
appear on an existing database for free, new *columns* on an existing table
silently do not. The enrichment side tables get around that with
``ENRICHMENT_SCHEMA_VERSION``, which drops and rebuilds all five of them because
every row in them is derived and re-fetchable.

``artists.sort_name`` cannot use that mechanism, and the reason is the same
reason the column exists at all. ``artist_metadata`` already has a ``sort_name``
— MusicBrainz's — and it is rewritten on every enrichment pass, protected by
nothing (``_MANUAL_OWNS`` covers ids, not descriptive fields), in a table the
version guard deletes wholesale. A sort name a person typed is the one kind of
value none of that is acceptable for: nothing can re-fetch it. So it lands on
``artists``, and ``artists`` is a table Qobuzarr must migrate rather than drop.

``artists.aliases_json`` lands here for exactly the same reason, and a second
one: an alias is *only* ever what a person typed. Nothing derives one, nothing
writes one to a file tag or an NFO, and ``app/enrich/matching.py`` must never
read one — so there is no upstream that could put it back if the enrichment
version guard dropped the table it lived in.

Hence this script. Run it once against a database that predates the columns::

    ./.venv/bin/python scripts/migrate_artist_tags.py            # data/qobuzarr.db
    ./.venv/bin/python scripts/migrate_artist_tags.py /path/to.db
    ./.venv/bin/python scripts/migrate_artist_tags.py --dry-run

Stop the server first. The only statement here is an ``ADD COLUMN`` and nothing
is destructive, but a schema change under a live SQLAlchemy connection pool is a
needless thing to explain to yourself later.

**It is safe to run twice**, which is the property that matters most: each
column is guarded by its own ``PRAGMA table_info`` check, so a database that
already took the ``sort_name`` run reports one column added and a second run
reports zero, and a run interrupted halfway is repaired by running it again.

There is no backfill pass and that is not an omission. Both columns are nullable
and ``NULL`` is the correct value for every existing row: nobody has typed a
sort name or an alias yet, and ``NULL`` means exactly that — "nobody has said".
For ``sort_name`` the read models turn that into MusicBrainz's answer rather
than into a blank; for ``aliases_json`` they turn it into the empty list, which
is the honest answer because there is no derived source to fall back to.
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

#: ``table -> [(column, SQLite declaration)]``. Nullable with no default, which
#: is what SQLAlchemy emits for ``Mapped[str | None]`` — a migrated database and
#: a fresh one describe themselves identically to ``PRAGMA table_info``.
COLUMNS: dict[str, list[tuple[str, str]]] = {
    "artists": [
        ("sort_name", "VARCHAR(512)"),
        ("aliases_json", "TEXT"),
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
            "\nDone. Every existing artist starts with no typed sort name and "
            "no aliases, so each one still files under whatever MusicBrainz "
            "supplied and answers the aliases box with an empty list."
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Add the hand-editable artist tag columns. Idempotent.",
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
