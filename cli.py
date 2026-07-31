#!/usr/bin/env python3
"""Qobuzarr command-line interface (stdlib :mod:`argparse` only).

Everything the web UI can do that is useful from a terminal, plus the one thing
it cannot: proving that the Qobuz handshake works end to end.

    ./.venv/bin/python cli.py verify-credentials
    ./.venv/bin/python cli.py add-artist "Nils Frahm"
    ./.venv/bin/python cli.py add-artist 720076 --scan
    ./.venv/bin/python cli.py list-artists
    ./.venv/bin/python cli.py scan-now [--artist 720076]
    ./.venv/bin/python cli.py scan-library [--dry-run]
    ./.venv/bin/python cli.py import-library --dry-run
    ./.venv/bin/python cli.py queue-status
    ./.venv/bin/python cli.py serve [--host 0.0.0.0 --port 8000 --reload]

Every subcommand shares one :class:`~app.qobuz.ratelimit.RateLimiter`, so the
CLI obeys exactly the same outbound budget as the server.  The CLI deliberately
does **not** start the download-queue worker or the scheduler: it never begins a
download behind your back.  Use ``serve`` (or the web UI) for that.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from collections.abc import Sequence
from contextlib import asynccontextmanager
from dataclasses import dataclass
from datetime import datetime
from typing import Any, AsyncIterator

from sqlalchemy import func, select

from app import __version__
from app.config import FORMAT_LABELS, Settings, get_settings
from app.core.indexer import Indexer, UnknownArtistError, utc
from app.core.importer import LibraryImporter
from app.core.scanner import LibraryScanner
from app.db import dispose_engine, init_db, session_scope
from app.logging_conf import setup_logging
from app.models import Album, AlbumStatus, Artist, QueueItem, QueueState
from app.qobuz.client import QobuzClient
from app.qobuz.errors import QobuzError
from app.qobuz.ratelimit import RateLimiter

__all__ = ["build_parser", "main"]

EXIT_OK = 0
EXIT_ERROR = 1
EXIT_USAGE = 2


# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------
def out(message: str = "") -> None:
    """Write a line to stdout."""
    print(message)


def err(message: str) -> None:
    """Write a line to stderr."""
    print(message, file=sys.stderr)


def rule(title: str = "") -> None:
    """Print a section separator."""
    out(f"-- {title} ".ljust(72, "-") if title else "-" * 72)


def table(headers: Sequence[str], rows: Sequence[Sequence[Any]]) -> None:
    """Print a plain fixed-width table."""
    cells = [[("" if c is None else str(c)) for c in row] for row in rows]
    widths = [len(h) for h in headers]
    for row in cells:
        for index, value in enumerate(row):
            widths[index] = max(widths[index], len(value))
    out("  ".join(h.ljust(widths[i]) for i, h in enumerate(headers)).rstrip())
    out("  ".join("-" * widths[i] for i in range(len(headers))))
    for row in cells:
        out("  ".join(value.ljust(widths[i]) for i, value in enumerate(row)).rstrip())


def fmt_time(value: datetime | None) -> str:
    """Render a timestamp as UTC, tolerating SQLite's naive values."""
    stamped = utc(value)
    return stamped.strftime("%Y-%m-%d %H:%M UTC") if stamped else "never"


# ---------------------------------------------------------------------------
# Shared context
# ---------------------------------------------------------------------------
@dataclass(slots=True)
class Context:
    """Objects a subcommand needs: settings plus (optionally) a Qobuz client."""

    settings: Settings
    limiter: RateLimiter
    client: QobuzClient | None = None


@asynccontextmanager
async def session_ctx(*, with_client: bool, login: bool = True) -> AsyncIterator[Context]:
    """Prepare the database and, when asked, an authenticated Qobuz client."""
    settings = get_settings()
    await init_db()
    limiter = RateLimiter.from_settings(settings)
    client: QobuzClient | None = None
    try:
        if with_client:
            settings.require_credentials()
            client = QobuzClient(settings, limiter=limiter)
            if login:
                await client.login()
        yield Context(settings=settings, limiter=limiter, client=client)
    finally:
        if client is not None:
            await client.aclose()
        await dispose_engine()


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------
async def cmd_verify_credentials(args: argparse.Namespace) -> int:
    """Log in, resolve the app secret and print the account's entitlements."""
    settings = get_settings()
    rule("credentials")
    out(f"API base            : {settings.qobuz_api_base}")
    out(f"QOBUZ_APP_ID        : {settings.qobuz_app_id or '(unset)'}")
    out(
        "QOBUZ_USER_AUTH_TOKEN: "
        + ("set (redacted)" if settings.qobuz_user_auth_token else "(unset)")
    )
    out(
        "QOBUZ_APP_SECRET    : "
        + ("set in .env (redacted)" if settings.qobuz_app_secret else "(not set — will be derived)")
    )
    if not settings.has_credentials:
        err(
            "\nMissing credentials. Put QOBUZ_APP_ID and QOBUZ_USER_AUTH_TOKEN in "
            f"{settings.model_config.get('env_file', '.env')} and try again."
        )
        return EXIT_ERROR

    async with session_ctx(with_client=True, login=False) as ctx:
        assert ctx.client is not None
        try:
            payload = await ctx.client.login()
        except QobuzError as exc:
            err(f"\nLogin FAILED: {exc}")
            return EXIT_ERROR

        user = payload.get("user", payload) or {}
        rule("account")
        out(f"user id             : {user.get('id', '?')}")
        out(f"display name        : {user.get('display_name') or user.get('login') or '?'}")
        out(f"country             : {user.get('country_code', '?')}")
        out(f"zone                : {user.get('zone', '?')}")

        credential = user.get("credential") or {}
        parameters = credential.get("parameters") or {}
        rule("entitlements")
        if parameters:
            for key in sorted(parameters):
                out(f"{key:<28}: {parameters[key]}")
        else:
            out("(no credential parameters returned — probably a free account)")

        allowed = ctx.client.allowed_format_ids()
        rule("streamable formats")
        for format_id in allowed:
            out(f"  {format_id:<3} {FORMAT_LABELS.get(format_id, 'unknown')}")
        chosen = ctx.client.best_format_id(settings.default_format_id)
        out(
            f"\nDEFAULT_FORMAT_ID={settings.default_format_id} "
            f"({FORMAT_LABELS.get(settings.default_format_id, '?')}) "
            f"-> effective {chosen} ({FORMAT_LABELS.get(chosen, '?')})"
        )

        rule("app secret")
        try:
            secret = await ctx.client.resolve_app_secret(force_refresh=args.refresh_secret)
        except QobuzError as exc:
            err(f"App secret could NOT be resolved: {exc}")
            return EXIT_ERROR
        out(f"resolved            : yes ({len(secret)} chars, redacted)")
        out(f"source              : {ctx.client.app_secret_source}")
        out(f"cache file          : {settings.secret_cache_path}")
        if ctx.client.app_secret_source != "env":
            out(
                "\nHint: add this to .env to skip derivation on every cold start:\n"
                "  QOBUZ_APP_SECRET=<the value cached in data/secret.cache>"
            )

        rule("signed request test")
        track_id = args.track_id
        if not track_id:
            try:
                albums = await ctx.client.search_albums("nils frahm", limit=1)
                if albums:
                    album = await ctx.client.get_album(str(albums[0].get("id")))
                    items = (album.get("tracks") or {}).get("items") or []
                    if items:
                        track_id = str(items[0].get("id"))
            except QobuzError as exc:  # pragma: no cover - network dependent
                out(f"(catalogue probe failed, falling back to a known id: {exc})")
        track_id = track_id or "64868955"
        try:
            file_info = await ctx.client.get_file_url(track_id, format_id=min(allowed))
        except QobuzError as exc:
            err(f"getFileUrl FAILED for track {track_id}: {exc}")
            return EXIT_ERROR
        out(f"track id            : {track_id}")
        out(f"returned format_id  : {file_info.get('format_id')}")
        out(f"mime type           : {file_info.get('mime_type')}")
        out(
            "bit depth / rate    : "
            f"{file_info.get('bit_depth')} bit / {file_info.get('sampling_rate')} kHz"
        )
        out(f"url                 : {'received (redacted)' if file_info.get('url') else 'MISSING'}")

    rule()
    out("Credentials, app secret and request signing all work.")
    return EXIT_OK


async def cmd_add_artist(args: argparse.Namespace) -> int:
    """Follow an artist by Qobuz id, or by searching for a name."""
    async with session_ctx(with_client=True) as ctx:
        assert ctx.client is not None
        indexer = Indexer(ctx.client, settings=ctx.settings)
        query = args.query.strip()

        artist_id: str | None = None
        artist_name: str | None = None

        if query.isdigit() and not args.search:
            artist_id = query
        else:
            try:
                matches = await ctx.client.search_artists(query, limit=args.limit)
            except QobuzError as exc:
                err(f"Search failed: {exc}")
                return EXIT_ERROR
            if not matches:
                err(f"No artists matched {query!r}.")
                return EXIT_ERROR
            rule("matches")
            table(
                ("#", "id", "name", "albums"),
                [
                    (
                        index,
                        raw.get("id"),
                        raw.get("name"),
                        raw.get("albums_count", 0),
                    )
                    for index, raw in enumerate(matches)
                ],
            )
            if args.dry_run:
                return EXIT_OK
            if args.index >= len(matches):
                err(f"--index {args.index} is out of range (0..{len(matches) - 1}).")
                return EXIT_USAGE
            chosen = matches[args.index]
            artist_id = str(chosen.get("id"))
            artist_name = chosen.get("name")

        if args.dry_run:
            out(f"Would follow artist {artist_id}.")
            return EXIT_OK

        async with session_scope() as session:
            try:
                artist = await indexer.add_artist(
                    session,
                    str(artist_id),
                    monitored=not args.unmonitored,
                    name=artist_name,
                    monitor_mode=args.monitor_mode,
                    quality_profile=args.quality_profile,
                    accepted_release_types=(
                        [t.strip() for t in args.release_types.split(",") if t.strip()]
                        if args.release_types
                        else None
                    ),
                    index_now=args.scan,
                )
            except QobuzError as exc:
                err(f"Could not add artist {artist_id}: {exc}")
                return EXIT_ERROR
            out(
                f"Following {artist.name} (id={artist.id}, mode={artist.monitor_mode.value}, "
                f"types={','.join(artist.accepted_release_types_list)})"
            )

        if args.scan:
            async with session_scope() as session:
                total = await session.scalar(
                    select(func.count()).select_from(Album).where(Album.artist_id == str(artist_id))
                )
                out(f"Indexed {int(total or 0)} releases.")
    return EXIT_OK


async def cmd_list_artists(args: argparse.Namespace) -> int:
    """Print every followed artist with its album counts."""
    async with session_ctx(with_client=False):
        async with session_scope() as session:
            artists = (
                (await session.execute(select(Artist).order_by(Artist.name.asc())))
                .scalars()
                .all()
            )
            if not artists:
                out("No artists followed yet. Try: cli.py add-artist \"<name>\"")
                return EXIT_OK

            counts = await session.execute(
                select(Album.artist_id, Album.status, func.count()).group_by(
                    Album.artist_id, Album.status
                )
            )
            buckets: dict[str, dict[str, int]] = {}
            for artist_id, status, total in counts.all():
                key = status.value if isinstance(status, AlbumStatus) else str(status)
                buckets.setdefault(str(artist_id), {})[key] = int(total)

            rows = []
            for artist in artists:
                stats = buckets.get(str(artist.id), {})
                rows.append(
                    (
                        artist.id,
                        artist.name,
                        "yes" if artist.monitored else "no",
                        artist.monitor_mode.value,
                        sum(stats.values()),
                        stats.get(AlbumStatus.WANTED.value, 0)
                        + stats.get(AlbumStatus.QUEUED.value, 0),
                        stats.get(AlbumStatus.DOWNLOADED.value, 0),
                        fmt_time(artist.last_checked_at),
                    )
                )
            table(
                ("id", "name", "mon", "mode", "albums", "wanted", "done", "last checked"),
                rows,
            )
            if args.verbose:
                out(f"\n{len(rows)} artist(s).")
    return EXIT_OK


async def cmd_scan_now(args: argparse.Namespace) -> int:
    """Run one indexer pass immediately (one artist, or a named artist)."""
    async with session_ctx(with_client=True) as ctx:
        assert ctx.client is not None
        indexer = Indexer(ctx.client, settings=ctx.settings)
        async with session_scope() as session:
            if args.artist:
                try:
                    result = await indexer.check_artist(session, str(args.artist))
                except UnknownArtistError:
                    err(f"Artist {args.artist} is not followed.")
                    return EXIT_ERROR
                except QobuzError as exc:
                    err(f"Scan failed: {exc}")
                    return EXIT_ERROR
            else:
                result = await indexer.tick(session)
                if result is None:
                    out("Nothing due: no monitored artist needs checking right now.")
                    return EXIT_OK
        out(result.summary())
        if result.wanted_album_ids:
            out(f"Newly wanted albums: {', '.join(result.wanted_album_ids[:20])}")
        for problem in result.errors:
            err(f"  ! {problem}")
        out(
            "\nQueued items are downloaded by the running server "
            "(cli.py serve); the CLI never downloads on its own."
        )
    return EXIT_OK


async def cmd_scan_library(args: argparse.Namespace) -> int:
    """Walk the library folder and adopt whatever is already on disk.

    Entirely local: no Qobuz credentials are needed and no API call is made,
    which is why this opens a session context ``with_client=False``.
    """
    async with session_ctx(with_client=False) as ctx:
        scanner = LibraryScanner(settings=ctx.settings)
        async with session_scope() as session:
            try:
                result = await scanner.scan(
                    session,
                    root=args.path,
                    artist_id=str(args.artist) if args.artist else None,
                    apply=not args.dry_run,
                )
            except LookupError as exc:
                err(str(exc))
                return EXIT_ERROR

    rule("disk scan")
    out(f"root      : {result.root}")
    out(f"files     : {result.audio_files} in {result.albums_found} album folder(s)")
    out(f"matched   : {result.albums_matched}")
    out(f"adopted   : {result.albums_adopted} newly marked downloaded")
    out(f"already   : {result.albums_already}")
    out(f"incomplete: {result.albums_partial}")
    out(f"unmatched : {result.unmatched_count}")
    out(f"unknown   : {result.unknown_artist_count} artist(s) not followed")
    if result.queue_items_cancelled:
        out(f"cancelled : {result.queue_items_cancelled} pending queue item(s)")

    if args.verbose:
        if result.partial:
            rule("incomplete")
            table(
                ("artist", "release", "on disk"),
                [
                    (row["artist"][:28], row["title"][:44], f"{row['files']}/{row['expected']}")
                    for row in result.partial
                ],
            )
        if result.unmatched:
            rule("not in the catalogue")
            table(
                ("artist", "folder title", "files"),
                [
                    (row["artist"][:28], row["title"][:44], row["files"])
                    for row in result.unmatched
                ],
            )
        if result.unknown_artists:
            rule("artists you do not follow")
            table(
                ("name", "releases", "files", "for example"),
                [
                    (row["name"][:28], row["albums"], row["files"], row["title"][:40])
                    for row in result.unknown_artists
                ],
            )

    for problem in result.errors[:20]:
        err(f"  ! {problem}")

    if args.dry_run:
        out("\nDry run — nothing was written.")
    return EXIT_OK


async def cmd_import_library(args: argparse.Namespace) -> int:
    """Look up every artist found on disk and follow the unambiguous matches.

    One Qobuz search per artist through the shared limiter, so a large library
    takes minutes. Nothing is downloaded, and nothing is indexed unless
    ``--index-now`` is passed — the running server's indexer picks the new
    artists up on its own schedule.
    """
    async with session_ctx(with_client=True) as ctx:
        assert ctx.client is not None
        scanner = LibraryScanner(settings=ctx.settings)
        indexer = Indexer(ctx.client, settings=ctx.settings)
        importer = LibraryImporter(
            ctx.client, indexer, scanner, settings=ctx.settings
        )

        async with session_scope() as session:
            candidates = await importer.preview(session)
        if args.limit:
            candidates = candidates[: args.limit]

        if not candidates:
            out("Every artist in the library folder is already followed.")
            return EXIT_OK

        minutes = importer.estimate_seconds(len(candidates)) / 60.0
        rule("to look up")
        table(
            ("artist", "releases", "files"),
            [
                (row["name"][:36], row.get("albums", 0), row.get("files", 0))
                for row in candidates[: 40 if not args.verbose else len(candidates)]
            ],
        )
        if not args.verbose and len(candidates) > 40:
            out(f"... and {len(candidates) - 40} more (-v lists them all).")
        out(
            f"\n{len(candidates)} artist(s), one Qobuz search each "
            f"-> at least {minutes:.0f} minute(s) at "
            f"{ctx.settings.qobuz_min_request_interval}s per call."
        )

        if args.dry_run:
            out("\nDry run — nothing was looked up or followed.")
            return EXIT_OK

        if not args.yes:
            err(
                "\nRefusing to start without --yes. Re-run with --yes to follow "
                "the exact matches, or --dry-run to see this list again."
            )
            return EXIT_USAGE

        await importer.start(
            names=[row["name"] for row in candidates],
            monitored=not args.unmonitored,
            index_now=args.index_now,
        )
        await importer.wait()
        progress = importer.snapshot()

        rule("result")
        out(progress["summary"])
        if progress["review"]:
            rule("needs a decision")
            table(
                ("on disk", "qobuz suggests"),
                [
                    (
                        row["name"][:36],
                        ", ".join(
                            f"{c['name']} ({c['albums_count']})" for c in row["candidates"]
                        )
                        or "nothing found",
                    )
                    for row in progress["review"]
                ],
            )
            out("\nResolve these on the Disk scan page, or with `add-artist <name>`.")
        for problem in progress["errors"][:20]:
            err(f"  ! {problem}")
        if not args.index_now:
            out(
                "\nNew artists are followed but not indexed yet; the running server's "
                "indexer works through them one per tick."
            )
    return EXIT_OK


async def cmd_queue_status(args: argparse.Namespace) -> int:
    """Show queue counts and the head of the queue."""
    async with session_ctx(with_client=False):
        async with session_scope() as session:
            counts = await session.execute(
                select(QueueItem.state, func.count()).group_by(QueueItem.state)
            )
            buckets = {
                (state.value if isinstance(state, QueueState) else str(state)): int(total)
                for state, total in counts.all()
            }
            rule("queue")
            for state in QueueState:
                out(f"{state.value:<12}: {buckets.get(state.value, 0)}")
            out(f"{'total':<12}: {sum(buckets.values())}")

            statement = (
                select(QueueItem)
                .order_by(QueueItem.priority.desc(), QueueItem.id.asc())
                .limit(args.limit)
            )
            if not args.all:
                statement = statement.where(
                    QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE))
                )
            items = (await session.execute(statement)).scalars().all()
            if items:
                rule("items")
                table(
                    ("#", "state", "album", "artist", "tracks", "att", "error"),
                    [
                        (
                            item.id,
                            item.state.value,
                            (item.album.title if item.album else item.album_id)[:44],
                            (item.album.artist.name if item.album and item.album.artist else "—")[
                                :26
                            ],
                            f"{item.progress_tracks_done}/{item.progress_tracks_total}",
                            item.attempts,
                            (item.last_error or "")[:40],
                        )
                        for item in items
                    ],
                )
            else:
                out("\nQueue is empty.")
    return EXIT_OK


def cmd_serve(args: argparse.Namespace) -> int:
    """Run the web server (blocking)."""
    import uvicorn

    settings = get_settings()
    host = args.host or settings.host
    port = args.port or settings.port
    reload = bool(args.reload or settings.reload)
    out(f"Qobuzarr {__version__} -> http://{host}:{port}")
    uvicorn.run(
        "main:app",
        host=host,
        port=port,
        reload=reload,
        log_level=str(settings.log_level.value).lower(),
    )
    return EXIT_OK


# ---------------------------------------------------------------------------
# Parser
# ---------------------------------------------------------------------------
def build_parser() -> argparse.ArgumentParser:
    """Build the argparse command tree."""
    parser = argparse.ArgumentParser(
        prog="cli.py",
        description="Qobuzarr command line — follow artists and drive the indexer.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Examples:\n"
            "  cli.py verify-credentials\n"
            '  cli.py add-artist "Nils Frahm" --scan\n'
            "  cli.py scan-now --artist 720076\n"
            "  cli.py scan-library --dry-run -v\n"
            "  cli.py import-library --dry-run\n"
            "  cli.py serve --port 8000\n"
        ),
    )
    parser.add_argument("--version", action="version", version=f"Qobuzarr {__version__}")
    parser.add_argument(
        "--log-level",
        default=None,
        help="Override LOG_LEVEL for this invocation (DEBUG/INFO/WARNING/ERROR).",
    )
    subparsers = parser.add_subparsers(dest="command", metavar="<command>")

    verify = subparsers.add_parser(
        "verify-credentials",
        help="Log in, resolve the app secret and print streamable formats.",
    )
    verify.add_argument(
        "--refresh-secret",
        action="store_true",
        help="Ignore data/secret.cache and re-derive the app secret.",
    )
    verify.add_argument(
        "--track-id",
        default=None,
        help="Track id to use for the signed getFileUrl test.",
    )
    verify.set_defaults(func=cmd_verify_credentials, needs_async=True)

    add = subparsers.add_parser("add-artist", help="Follow an artist by name or Qobuz id.")
    add.add_argument("query", help="Artist name to search for, or a numeric Qobuz artist id.")
    add.add_argument("--search", action="store_true", help="Search even if the query is numeric.")
    add.add_argument("--index", type=int, default=0, help="Which search hit to follow (default 0).")
    add.add_argument("--limit", type=int, default=10, help="Search result count (default 10).")
    add.add_argument("--dry-run", action="store_true", help="Show matches without following.")
    add.add_argument("--unmonitored", action="store_true", help="Add without monitoring.")
    add.add_argument(
        "--monitor-mode",
        choices=("all", "future", "none"),
        default=None,
        help="Monitor mode (default: DEFAULT_MONITOR_MODE).",
    )
    add.add_argument("--quality-profile", default=None, help="Per-artist quality profile name.")
    add.add_argument(
        "--release-types",
        default=None,
        help="Comma-separated accepted release types (album,ep,single,live,compilation).",
    )
    add.add_argument(
        "--scan",
        action="store_true",
        help="Index the artist immediately (slow: it is rate limited).",
    )
    add.set_defaults(func=cmd_add_artist, needs_async=True)

    listing = subparsers.add_parser("list-artists", help="List followed artists.")
    listing.add_argument("-v", "--verbose", action="store_true", help="Print a total line.")
    listing.set_defaults(func=cmd_list_artists, needs_async=True)

    scan = subparsers.add_parser("scan-now", help="Run one indexer pass right now.")
    scan.add_argument("--artist", default=None, help="Scan this artist id instead of the next due.")
    scan.set_defaults(func=cmd_scan_now, needs_async=True)

    disk = subparsers.add_parser(
        "scan-library",
        help="Scan the library folder and mark albums already on disk as downloaded.",
        description=(
            "Reads LIBRARY_PATH, matches every album folder against the database "
            "and marks the complete ones downloaded so they stop being wanted. "
            "Purely local: no Qobuz credentials are used and no file is modified."
        ),
    )
    disk.add_argument(
        "--path",
        default=None,
        help="Directory to walk instead of LIBRARY_PATH (useful for testing).",
    )
    disk.add_argument(
        "--artist", default=None, help="Only apply changes to this artist id."
    )
    disk.add_argument(
        "-n",
        "--dry-run",
        action="store_true",
        help="Report what would change without writing anything.",
    )
    disk.add_argument(
        "-v", "--verbose", action="store_true", help="List the folders needing attention."
    )
    disk.set_defaults(func=cmd_scan_library, needs_async=True)

    importer = subparsers.add_parser(
        "import-library",
        help="Look every artist on disk up on Qobuz and follow the exact matches.",
        description=(
            "Searches Qobuz once per artist found in LIBRARY_PATH but not yet "
            "followed, and follows the ones whose name matches exactly. Names it "
            "cannot resolve are printed for you to handle. Nothing is downloaded, "
            "and catalogues are not indexed unless --index-now is given."
        ),
    )
    importer.add_argument(
        "-n", "--dry-run", action="store_true", help="List who would be looked up, then stop."
    )
    importer.add_argument(
        "--yes", action="store_true", help="Actually run it (required: this spends API calls)."
    )
    importer.add_argument("--limit", type=int, default=None, help="Stop after N artists.")
    importer.add_argument(
        "--index-now",
        action="store_true",
        help="Import each catalogue as the artist is followed (many more API calls).",
    )
    importer.add_argument(
        "--unmonitored", action="store_true", help="Follow without monitoring."
    )
    importer.add_argument(
        "-v", "--verbose", action="store_true", help="List every artist, not the first 40."
    )
    importer.set_defaults(func=cmd_import_library, needs_async=True)

    queue = subparsers.add_parser("queue-status", help="Show the download queue.")
    queue.add_argument("--limit", type=int, default=20, help="Rows to show (default 20).")
    queue.add_argument("--all", action="store_true", help="Include finished/failed items.")
    queue.set_defaults(func=cmd_queue_status, needs_async=True)

    serve = subparsers.add_parser("serve", help="Run the Qobuzarr web server.")
    serve.add_argument("--host", default=None, help="Bind address (default: HOST).")
    serve.add_argument("--port", type=int, default=None, help="Bind port (default: PORT).")
    serve.add_argument("--reload", action="store_true", help="Auto-reload on code changes.")
    serve.set_defaults(func=cmd_serve, needs_async=False)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """Parse *argv* and dispatch to the chosen subcommand."""
    parser = build_parser()
    args = parser.parse_args(list(argv) if argv is not None else None)

    if getattr(args, "func", None) is None:
        parser.print_help()
        return EXIT_USAGE

    settings = get_settings()
    setup_logging(args.log_level or settings.log_level, settings=settings)

    try:
        if args.needs_async:
            return int(asyncio.run(args.func(args)))
        return int(args.func(args))
    except KeyboardInterrupt:
        err("\nInterrupted.")
        return 130
    except RuntimeError as exc:
        err(f"Error: {exc}")
        return EXIT_ERROR
    except QobuzError as exc:
        err(f"Qobuz error: {exc}")
        return EXIT_ERROR


if __name__ == "__main__":
    raise SystemExit(main())
