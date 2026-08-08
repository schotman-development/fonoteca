#!/usr/bin/env python
"""Add the attribution columns to an existing Qobuzarr database, in place.

A Qobuz artist id is not always one artist, and Qobuz files a guest appearance
under the guest. Both are facts about *whose release this is*, and both need
somewhere to live:

``albums.guest_appearance``
    The owning artist is credited ``featured-artist`` and never ``main-artist``
    on this release. Measured from the free ``artist/getReleasesList`` payload,
    so it costs no extra call — 122 such releases across 41 monitored artists in
    the library this was written against, 8.2% of everything they offer.
``albums.credit_names``
    The qualified credit lines on the release's tracks (``Boaz Roelevink``, not
    the bare ``Boaz``), read from ``album/get`` on request.
``artists.include_guest_appearances``
    Want the guest appearances anyway. Default off.
``artists.credit_filter_json``
    Accept only releases credited to these people. Default ``NULL`` — no filter,
    which is byte-for-byte the behaviour before this migration.

Qobuzarr has exactly one migration story and it is ``create_all``: new *tables*
appear on an existing database for free, new *columns* on an existing table
silently do not — the symptom is ``no such column: albums.guest_appearance``
raised out of whichever page read it first, not an error at startup. The
enrichment side tables get around that with ``ENRICHMENT_SCHEMA_VERSION``, which
drops and rebuilds all five because every row in them is derived and
re-fetchable. **That mechanism is not available here and must not be reached
for.** These four columns land on ``albums`` and ``artists``, and two of them
hold the one kind of thing Qobuzarr cannot re-derive: which of the people
sharing a Qobuz artist id a person actually follows.

Run it once against a database that predates the columns::

    ./.venv/bin/python scripts/migrate_artist_attribution.py            # data/qobuzarr.db
    ./.venv/bin/python scripts/migrate_artist_attribution.py /path/to.db
    ./.venv/bin/python scripts/migrate_artist_attribution.py --dry-run

Stop the server first. Every statement is an ``ADD COLUMN`` and nothing is
destructive, but a schema change under a live SQLAlchemy connection pool is a
needless thing to explain to yourself later.

**It is safe to run twice.** Each column is guarded by a ``PRAGMA table_info``
check, so a second run reports zero of everything and a run interrupted halfway
is repaired by running it again.

There is no backfill pass, and for the two nullable columns that is the *point*
rather than an omission. ``guest_appearance`` and ``credit_names`` arrive
``NULL``, and ``NULL`` means **nobody has measured this release yet** — which
:func:`app.core.indexer.desired_status` treats as a no-op, exactly as
``integrity.classify()`` answers ``UNKNOWN`` rather than ``CHANGED`` for a file
nothing has baselined. Backfilling them to ``0``/``[]`` would assert a
measurement that never happened, and for ``credit_names`` it would assert the
strongest available claim — *no credit qualifies* — about every release in the
library, which with a filter on would demote all of them. The indexer fills
``guest_appearance`` on its next visit to each artist; ``credit_names`` fills
only when somebody asks for it, per artist.

The two ``NOT NULL`` columns are the opposite case and are complete on arrival:
nobody has asked for guest appearances and nobody has set a credit filter, so
``DEFAULT 0`` and ``NULL`` are the true values for every existing row. The one
cosmetic difference from a database created fresh by ``create_all``: SQLAlchemy
emits ``BOOLEAN NOT NULL`` with the default applied Python-side, this emits
``BOOLEAN NOT NULL DEFAULT 0``. The constraint and the data are identical; only
``PRAGMA table_info``'s ``dflt_value`` differs, and nothing reads it.
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

#: ``table -> [(column, SQLite declaration)]``. ``NOT NULL`` carries a constant
#: default because SQLite refuses a ``NOT NULL`` ``ADD COLUMN`` without one; the
#: nullable pair deliberately has none. See the module docstring.
COLUMNS: dict[str, list[tuple[str, str]]] = {
    "albums": [
        ("guest_appearance", "BOOLEAN"),
        ("credit_names", "TEXT"),
    ],
    "artists": [
        ("include_guest_appearances", "BOOLEAN NOT NULL DEFAULT 0"),
        ("credit_filter_json", "TEXT"),
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
            "\nDone. Nothing changed status: every release starts unmeasured "
            "(NULL), which desired_status() treats exactly as it did before "
            "these columns existed. The indexer fills guest_appearance on its "
            "next visit to each artist."
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Add the guest-appearance and credit-filter columns. Idempotent.",
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
