#!/usr/bin/env python
"""Add the integrity columns to an existing Qobuzarr database, in place.

Qobuzarr has exactly one migration story and it is ``create_all``: new *tables*
appear on an existing database for free, new *columns* on an existing table
silently do not. That is fine for the enrichment side tables, which carry a
version and get dropped and rebuilt because every row in them is re-fetchable.
It is not fine here. The integrity columns land on ``artists``, ``albums`` and
``tracks`` — the three tables holding the things Qobuzarr cannot re-derive — so
they need the one-off script that does not exist for anything else.

Run it once against a database that predates the integrity work::

    ./.venv/bin/python scripts/migrate_integrity.py            # data/qobuzarr.db
    ./.venv/bin/python scripts/migrate_integrity.py /path/to.db
    ./.venv/bin/python scripts/migrate_integrity.py --dry-run

Stop the server first. Nothing here is destructive — every statement is an
``ADD COLUMN``, a ``CREATE INDEX`` or an ``UPDATE`` of a column that was NULL a
moment ago — but a running worker holding a write transaction will simply make it
wait, and a schema change under a live SQLAlchemy connection pool is a needless
thing to explain to yourself later.

**It is safe to run twice**, which is the property that matters most: every step
is guarded by a ``PRAGMA table_info`` check or an ``IF NOT EXISTS``, the backfill
only touches rows whose ``qid`` is still NULL, and a second run reports zero of
everything. A half-finished run — the machine lost power between the ALTER and
the backfill — is repaired by running it again, not by unpicking anything.

One honest limitation. The ORM declares ``qid`` as ``NOT NULL``; this script
cannot. SQLite's ``ALTER TABLE ADD COLUMN`` refuses a ``NOT NULL`` column unless
it also has a constant default, and a constant default is precisely what a unique
identifier must not have — every existing row would be handed the same value and
the unique index would then refuse to build. Reaching real ``NOT NULL`` means
rebuilding all three tables and their foreign keys, which is a great deal of risk
to buy a constraint that is already unenforceable in the only direction it could
be violated: :func:`app.models.mint_qid` is a Python-side default, so every row
this program inserts arrives with a qid whether or not the column would have
insisted. A database created fresh by ``create_all`` gets the real constraint; a
migrated one gets a nullable column with no NULLs in it. The unique index — the
half that actually catches a mistake — is created either way.
"""

from __future__ import annotations

import argparse
import sqlite3
import sys
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parent.parent
if str(_REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(_REPO_ROOT))

from app.models import (  # noqa: E402 - after the sys.path bootstrap above
    QID_ARTIST_PREFIX,
    QID_RELEASE_PREFIX,
    QID_TRACK_PREFIX,
    mint_qid,
)

DEFAULT_DB = _REPO_ROOT / "data" / "qobuzarr.db"

#: ``table -> [(column, SQLite type)]``. Types mirror what SQLAlchemy emits for
#: the mapped columns, so a migrated database and a freshly created one describe
#: themselves identically to ``PRAGMA table_info``.
COLUMNS: dict[str, list[tuple[str, str]]] = {
    "artists": [
        ("qid", "VARCHAR(32)"),
    ],
    "albums": [
        ("qid", "VARCHAR(32)"),
        ("content_digest", "VARCHAR(32)"),
    ],
    "tracks": [
        ("qid", "VARCHAR(32)"),
        ("content_hash", "VARCHAR(32)"),
        ("sample_count", "BIGINT"),
        ("file_mtime", "FLOAT"),
        ("verified_at", "DATETIME"),
    ],
}

#: ``table -> qid prefix``. The prefixes live in :mod:`app.models` rather than
#: being spelled out here, so the script and the ORM cannot drift apart.
QID_PREFIXES: dict[str, str] = {
    "artists": QID_ARTIST_PREFIX,
    "albums": QID_RELEASE_PREFIX,
    "tracks": QID_TRACK_PREFIX,
}

#: Index names match what SQLAlchemy generates for ``index=True`` columns, so
#: ``create_all`` on a migrated database finds them already present.
INDEXES: list[tuple[str, str]] = [
    ("ix_artists_qid", "CREATE UNIQUE INDEX IF NOT EXISTS ix_artists_qid ON artists (qid)"),
    ("ix_albums_qid", "CREATE UNIQUE INDEX IF NOT EXISTS ix_albums_qid ON albums (qid)"),
    ("ix_tracks_qid", "CREATE UNIQUE INDEX IF NOT EXISTS ix_tracks_qid ON tracks (qid)"),
    (
        "ix_tracks_verified_at",
        "CREATE INDEX IF NOT EXISTS ix_tracks_verified_at ON tracks (verified_at)",
    ),
]

#: Rows backfilled per transaction. Large enough that a real library is a handful
#: of round trips, small enough that a 300k-track database does not build one
#: enormous statement in memory.
BATCH = 2000


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
    for name, sql_type in COLUMNS[table]:
        if name in present:
            continue
        added.append(name)
        if not dry_run:
            conn.execute(f"ALTER TABLE {table} ADD COLUMN {name} {sql_type}")
    return added


def backfill_qids(conn: sqlite3.Connection, table: str, dry_run: bool) -> int:
    """Mint a ``qid`` for every row of *table* that has none. Returns the count.

    Rows are addressed by ``rowid`` rather than by their primary key on purpose.
    Qobuz ids are opaque strings and an album id may be anything at all; ``rowid``
    is an integer SQLite guarantees, which keeps the UPDATE trivially correct
    without this script needing to know one thing about id formats.

    Each row gets its own freshly minted value — that is the whole reason the
    backfill cannot be a single ``UPDATE ... SET qid = <expression>``. SQLite's
    ``randomblob`` could approximate it, but then the id format would be defined
    in two places and only one of them would be the one the application uses.
    """
    if "qid" not in existing_columns(conn, table):
        # Only reachable on a dry run, where the ALTER above was reported rather
        # than executed. Every row is then a row that would need a qid.
        return int(conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0])

    pending = (
        f"SELECT COUNT(*) FROM {table} WHERE qid IS NULL OR qid = ''"
    )
    if dry_run:
        return int(conn.execute(pending).fetchone()[0])

    prefix = QID_PREFIXES[table]
    done = 0
    while True:
        rows = conn.execute(
            f"SELECT rowid FROM {table} WHERE qid IS NULL OR qid = '' LIMIT {BATCH}"
        ).fetchall()
        if not rows:
            break
        conn.executemany(
            f"UPDATE {table} SET qid = ? WHERE rowid = ?",
            [(mint_qid(prefix), int(row[0])) for row in rows],
        )
        conn.commit()
        done += len(rows)
    return done


def create_indexes(conn: sqlite3.Connection, dry_run: bool) -> list[str]:
    """Create the qid unique indexes and the verified-at index. Returns new ones.

    Deliberately after the backfill. SQLite treats NULLs as distinct so a unique
    index would build over a half-filled column without complaint, but building it
    last means the index existing is evidence the backfill finished.
    """
    created: list[str] = []
    for name, sql in INDEXES:
        exists = conn.execute(
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = ?", (name,)
        ).fetchone()
        if exists:
            continue
        created.append(name)
        if not dry_run:
            conn.execute(sql)
    return created


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
            print(f"{table}: {verb} {len(added)} column(s)" + (f" — {', '.join(added)}" if added else ""))

            filled = backfill_qids(conn, table, dry_run)
            print(
                f"{table}: {'would backfill' if dry_run else 'backfilled'} "
                f"{filled} qid(s)"
            )

        created = create_indexes(conn, dry_run)
        if not dry_run:
            conn.commit()
        print(f"indexes: {verb} {len(created)}" + (f" — {', '.join(created)}" if created else ""))
    finally:
        conn.close()

    if dry_run:
        print("\nDry run — nothing was written.")
    else:
        print(
            "\nDone. The enrichment side tables (including the new file_claims) are "
            "rebuilt by init_db() on the next start; nothing to do for those here."
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Add the integrity columns and backfill qids. Idempotent.",
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
