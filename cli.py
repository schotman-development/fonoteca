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
    ./.venv/bin/python cli.py verify-library [--dry-run]
    ./.venv/bin/python cli.py baseline-library
    ./.venv/bin/python cli.py queue-status
    ./.venv/bin/python cli.py serve [--host 0.0.0.0 --port 8000 --reload]

Every subcommand shares one :class:`~app.net.ratelimit.RateLimiter`, so the
CLI obeys exactly the same outbound budget as the server.  The CLI deliberately
does **not** start the download-queue worker or the scheduler: it never begins a
download behind your back.  Use ``serve`` (or the web UI) for that.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path
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
from app.net.ratelimit import RateLimiter

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


async def _effective_settings() -> Settings:
    """The environment plus the stored settings overlay.

    Degrades to the environment alone if the overlay cannot be read — a database
    from before this table existed, or an unreadable file. Same rule as
    :func:`app.core.state._load_overrides`: a command must still run.
    """
    from app.config import install_overrides  # noqa: PLC0415 - keeps startup light
    from app.core.settings_store import load_overrides  # noqa: PLC0415

    try:
        async with session_scope() as session:
            return install_overrides(await load_overrides(session))
    except Exception as exc:  # noqa: BLE001 - the overlay is never load-bearing
        err(f"Could not read the stored settings overrides ({exc}); using .env.")
        return get_settings()


@asynccontextmanager
async def session_ctx(*, with_client: bool, login: bool = True) -> AsyncIterator[Context]:
    """Prepare the database and, when asked, an authenticated Qobuz client.

    ``ctx.settings`` is the **effective** configuration: the environment with the
    ``app_setting`` overlay laid over it, exactly as the server reads it. The CLI
    runs the same passes the server does against the same library — ``enrich-now``
    builds the ladder from ``enrichment_sources``, ``verify-library`` reports
    ``integrity_enabled`` — so reading the environment alone would make the two
    surfaces disagree about settings the user changed on the Rules screen and
    never edited in ``.env``. Loading it needs the database, which is why it
    happens here rather than in :func:`main`.
    """
    await init_db()
    settings = await _effective_settings()
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


async def cmd_enrich_now(args: argparse.Namespace) -> int:
    """Run one enrichment batch, or report what is due.

    Opens the session context with ``with_client=False``: enrichment talks to
    Deezer, MusicBrainz and friends, never to Qobuz, so it needs no credentials
    and spends none of the Qobuz request budget.
    """
    from app.core.enricher import Enricher  # noqa: PLC0415 - keeps CLI startup light
    from app.enrich.registry import build_providers
    from app.models import EnrichmentEntity

    async with session_ctx(with_client=False) as ctx:
        if not ctx.settings.enrichment_enabled:
            err("ENRICHMENT_ENABLED is false — nothing to do.")
            return EXIT_ERROR

        enricher = Enricher(build_providers(ctx.settings), settings=ctx.settings)
        try:
            rule("configuration")
            out(f"sources   : {', '.join(ctx.settings.enrichment_source_list) or '(none)'}")
            out(f"available : {', '.join(p.source.value for p in enricher.providers.values()) or '(none)'}")
            out(f"contact   : {ctx.settings.enrichment_contact or '(unset — MusicBrainz is gated)'}")
            out(f"acoustid  : {'set' if ctx.settings.acoustid_ready else '(unset — fingerprinting is gated)'}")
            out(f"consensus : more than {ctx.settings.enrichment_consensus_threshold:.0%} of sources must agree")

            if args.artist or args.album:
                async with session_scope() as session:
                    if args.artist:
                        await enricher.reopen(
                            session, EnrichmentEntity.ARTIST, str(args.artist)
                        )
                        # Only the artist's releases that are actually on disk.
                        # Re-opening the rest would re-create exactly the rows
                        # the purge exists to delete, and report work no tick
                        # will ever claim: every other producer of state rows is
                        # scope-filtered, and this one must be too.
                        albums = (
                            (
                                await session.execute(
                                    select(Album.id).where(
                                        Album.artist_id == str(args.artist),
                                        Album.status == AlbumStatus.DOWNLOADED,
                                    )
                                )
                            )
                            .scalars()
                            .all()
                        )
                        for album_id in albums:
                            await enricher.reopen(
                                session, EnrichmentEntity.ALBUM, str(album_id)
                            )
                        out(
                            f"\nRe-opened artist {args.artist} and "
                            f"{len(albums)} album(s) on disk."
                        )
                    if args.album:
                        await enricher.reopen(
                            session, EnrichmentEntity.ALBUM, str(args.album)
                        )
                        out(f"\nRe-opened album {args.album}.")

            if args.run:
                rule("run")
                result = await enricher.tick(limit=args.limit)
                summary = result.as_dict()
                out(
                    f"seeded {summary['seeded']}, claimed {summary['claimed']} -> "
                    f"{summary['matched']} matched, {summary['unmatched']} unmatched, "
                    f"{summary['gated']} gated, {summary['failed']} failed "
                    f"in {summary['elapsed']}s"
                )
                if summary["budget_exhausted"]:
                    out("Tick budget ran out; the rest stays due for the next run.")
                if summary["by_source"]:
                    table(
                        ("source", "processed"),
                        sorted(summary["by_source"].items()),
                    )
            else:
                out("\n(dry report — pass --run to actually fetch)")

            async with session_scope() as session:
                status = await enricher.status(session)
            rule("coverage")
            scope = status.get("scope") or {}
            if scope:
                # The denominator, said out loud. Every number under it is a
                # count of the library, not of the catalogue, and reading them
                # against 3313 albums makes finished work look like 1% done.
                out(
                    f"library   : {scope.get('albums', 0)} album(s) on disk of "
                    f"{scope.get('catalogue_albums', 0)} known, "
                    f"{scope.get('artists', 0)} artist(s) of "
                    f"{scope.get('catalogue_artists', 0)}"
                )
                out("            (only what is on disk is enriched)")
            if not status["states"]:
                out("Nothing enriched yet.")
            else:
                seen_states = sorted(
                    {state for counts in status["states"].values() for state in counts}
                )
                table(
                    ("source", *seen_states),
                    [
                        (source, *[str(counts.get(state, 0)) for state in seen_states])
                        for source, counts in sorted(status["states"].items())
                    ],
                )
            if status["paused_reason"]:
                err(f"\nPaused: {status['paused_reason']}")
        finally:
            await enricher.aclose()

    return EXIT_OK


async def cmd_enrich_purge(args: argparse.Namespace) -> int:
    """Delete the enrichment work list for everything not in the library.

    The manual handle on the pass nightly housekeeping runs. Entirely local: no
    network, no Qobuz credentials, and nothing but ``enrichment_state`` rows is
    touched — what the sources already taught us stays where it is, so an album
    that comes back is re-seeded rather than re-fetched from scratch.
    """
    from app.core.enricher import (  # noqa: PLC0415 - keeps CLI startup light
        enrichment_scope_counts,
        purge_out_of_scope_enrichment,
    )
    from app.models import EnrichmentState

    async with session_ctx(with_client=False):
        async with session_scope() as session:
            scope = await enrichment_scope_counts(session)
            before = int(
                (
                    await session.execute(
                        select(func.count(EnrichmentState.entity_id))
                    )
                ).scalar_one()
                or 0
            )
            rule("scope")
            out(
                f"albums    : {scope['albums']} on disk of "
                f"{scope['catalogue_albums']} known"
            )
            out(
                f"artists   : {scope['artists']} with something on disk of "
                f"{scope['catalogue_artists']} followed"
            )
            out(f"state rows: {before}")

            if args.dry_run:
                out("\n(dry run — pass without --dry-run to delete)")
                return EXIT_OK

            removed = await purge_out_of_scope_enrichment(session)
            out(
                f"\nDeleted {removed} out-of-scope row(s); {before - removed} left. "
                "Metadata already fetched was not touched."
            )
    return EXIT_OK


async def cmd_backfill_upc(args: argparse.Namespace) -> int:
    """Fetch the barcode Qobuz holds for releases on disk that have none.

    One ``album/get`` per release, rate-limited by the shared limiter like every
    other Qobuz call. The releases the indexer created rows for have no ``upc``
    at all — ``artist/getReleasesList`` does not publish one — and that is the
    field the enrichment ladder's exact matching is built on.

    Nothing is queued and no file is touched. The enrichment rows this unblocks
    are re-opened by the enricher's own barcode pass on its next tick, which is
    why this command seeds no work of its own.
    """
    from app.core.catalogue import backfill_upc  # noqa: PLC0415 - keeps CLI startup light

    async with session_ctx(with_client=True) as ctx:
        async with session_scope() as session:
            def show(done: int, total: int) -> None:
                if done == total or done % 25 == 0:
                    out(f"  {done}/{total}")

            result = await backfill_upc(
                session,
                ctx.client,
                limit=args.limit,
                dry_run=args.dry_run,
                progress=show,
            )

            rule("upc backfill")
            out(f"releases on disk with no upc : {result.candidates}")
            out(f"asked about                  : {result.examined}")
            out(f"filled                       : {result.filled}")
            out(f"  usable as a match key      : {result.usable}")
            out(f"Qobuz published none         : {result.absent}")
            out(f"failed                       : {result.failed}")
            out(f"still without one            : {result.remaining}")

            for message in result.errors[:10]:
                out(f"  ! {message}")
            if len(result.errors) > 10:
                out(f"  ! … and {len(result.errors) - 10} more")

            if args.dry_run:
                out("\n(dry run — nothing was written)")
            elif result.usable:
                out(
                    "\nDeezer and MusicBrainz rows stranded on 'no barcode' are "
                    "re-opened on the enricher's next tick."
                )
    return EXIT_OK


async def cmd_folder(args: argparse.Namespace) -> int:
    """Record, list or undo a person's decision about one library folder.

    Two decisions the audio cannot make. Binding a folder to a Qobuz album by
    hand is one; the other is saying a folder holds **no** catalogue release at
    all — covers never released anywhere, a game soundtrack, a radio bootleg.

    Only a person may say the second thing. The automatic chain failing to find
    a release is not evidence that none exists, and a machine allowed to draw
    that conclusion would quietly retire folders it merely could not identify.
    """
    from app.core.binder import (  # noqa: PLC0415 - keeps CLI startup light
        clear_folder,
        list_folder_bindings,
        mark_folder,
    )
    from app.models import BINDING_NOT_IN_CATALOGUE

    async with session_ctx(with_client=False):
        async with session_scope() as session:
            if args.list:
                rows = await list_folder_bindings(session)
                if not rows:
                    out("No folder decisions recorded.")
                    return EXIT_OK
                rule(f"{len(rows)} folder decision(s)")
                for row in rows:
                    verdict = (
                        "not on Qobuz"
                        if row.state == BINDING_NOT_IN_CATALOGUE
                        else str(row.album_id)
                    )
                    out(f"  {Path(row.path).name[:48]:50.50} {verdict:16.16} {row.method}")
                    if row.note:
                        out(f"      note: {row.note}")
                return EXIT_OK

            if not args.path:
                out("Give a folder path, or --list.")
                return EXIT_ERROR

            path = str(Path(args.path).expanduser())
            if args.clear:
                if await clear_folder(session, path):
                    out(f"Forgot {path}. It returns to the unmatched list.")
                else:
                    out(f"Nothing was recorded for {path}.")
                return EXIT_OK

            try:
                row = await mark_folder(
                    session, path, album_id=args.album, note=args.note
                )
            except LookupError as exc:
                out(str(exc))
                return EXIT_ERROR

            if row.state == BINDING_NOT_IN_CATALOGUE:
                out(
                    f"Marked {path} as holding no catalogue release.\n"
                    "It leaves the unmatched count on the next scan and "
                    "bind-folders will not spend requests on it again."
                )
            else:
                out(
                    f"Bound {path} -> Qobuz album {row.album_id}.\n"
                    "Run `scan-library` to adopt it."
                )
    return EXIT_OK


async def cmd_bind_folders(args: argparse.Namespace) -> int:
    """Identify the folders the disk scan could not name, using their audio.

    The scan binds a folder to a catalogue row by normalising its title, and a
    folder whose name is a different string for the same record can never match
    — so a release you own sits in the wanted list waiting to be downloaded
    again. This runs AcoustID over the files, takes every barcode in the release
    group MusicBrainz agrees on, and lets one of them pick the Qobuz album.

    Writes only the binding. The adoption is the next disk scan's, through the
    same path every other folder takes.
    """
    from app.core.binder import bind_unmatched_folders  # noqa: PLC0415 - light startup
    from app.core.discovery import identifier_for
    from app.enrich.registry import build_providers

    async with session_ctx(with_client=True) as ctx:
        providers = build_providers(ctx.settings)
        try:
            identify = identifier_for(providers, ctx.client)
            async with session_scope() as session:
                def show(done: int, total: int) -> None:
                    if done == total or done % 10 == 0:
                        out(f"  {done}/{total}")

                result = await bind_unmatched_folders(
                    session,
                    identify=identify,
                    settings=ctx.settings,
                    root=args.path,
                    limit=args.limit,
                    dry_run=args.dry_run,
                    progress=show,
                )
        finally:
            for provider in providers:
                await provider.aclose()

    rule("folder binding")
    out(f"unmatched folders : {result.unmatched}")
    out(f"pinned by hand    : {result.manual}")
    out(f"put through audio : {result.examined}")
    out(f"  bound           : {result.bound}")
    out(f"  ambiguous       : {result.ambiguous}")
    out(f"  duplicate copy  : {result.duplicates}")
    out(f"  unidentified    : {result.unidentified}")
    out(f"  failed          : {result.failed}")
    if result.truncated:
        out(f"not reached       : {result.truncated} (re-run to continue)")

    if result.bindings:
        rule("bound")
        for entry in result.bindings[:40]:
            folder = Path(entry.path).name
            out(f"  {folder[:44]:46.46} -> {entry.album_title[:34]:36.36} {entry.album_id}")
        if len(result.bindings) > 40:
            out(f"  ... and {len(result.bindings) - 40} more")

    if args.verbose and result.unresolved:
        rule("still unresolved")
        for entry in result.unresolved[:40]:
            out(f"  {Path(entry['path']).name[:40]:42.42} {entry['outcome']}: {entry['reason'][:70]}")

    for message in result.errors[:10]:
        out(f"  ! {message}")

    if result.gated:
        out("\nThe audio route could not run. Nothing was measured.")
        return EXIT_ERROR
    if args.dry_run:
        out("\n(dry run — no binding was written)")
    elif result.bound:
        out(
            f"\n{result.bound} folder(s) bound. Run `scan-library` to adopt them "
            "— that is what takes them off the wanted list."
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
    if result.albums_excluded:
        # Printed even though it is not work: a folder that vanishes from every
        # figure is indistinguishable from one the scan never saw, and somebody
        # has to be able to notice a marker they regret.
        out(f"not on Qobuz: {result.albums_excluded} marked by hand")
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


def _integrity_table(report: Any) -> None:
    """Print one verification report's verdicts, in the order they matter.

    ``unknown`` sits at the bottom on purpose. It is not a problem and it is not
    a change — it is the count of files nothing has ever measured — and printing
    it beside ``replaced`` in alphabetical order invites reading the two as
    degrees of the same thing.
    """
    from app.core.integrity import IntegrityState  # noqa: PLC0415 - keeps startup light

    labels = {
        IntegrityState.VERIFIED: "hashes exactly as recorded",
        IntegrityState.RETAGGED: "different bytes, same audio (tags were edited)",
        IntegrityState.REPLACED: "different audio under the same name",
        IntegrityState.MISSING: "not on disk",
        IntegrityState.UNKNOWN: "never baselined — no claim was being made",
    }
    table(
        ("state", "tracks", "meaning"),
        [
            (state.value, report.count(state), labels[state])
            for state in (
                IntegrityState.VERIFIED,
                IntegrityState.RETAGGED,
                IntegrityState.REPLACED,
                IntegrityState.MISSING,
                IntegrityState.UNKNOWN,
            )
        ],
    )


async def cmd_verify_library(args: argparse.Namespace) -> int:
    """Measure every file on disk against the hash recorded for it.

    Entirely local — no Qobuz credentials, no network, nothing queued — and it is
    the expensive one: every file is opened and hashed, at roughly 201 ms each,
    because the point of this command is precisely to bypass the size/mtime
    tripwire that the nightly scan trusts. ``--limit`` bounds a run; the order is
    the same rotation the nightly job uses, so a limited run measures whatever has
    gone longest without being looked at.
    """
    from app.core.scheduler import verify_integrity  # noqa: PLC0415 - keeps startup light

    async with session_ctx(with_client=False) as ctx:
        if not ctx.settings.integrity_enabled:
            out(
                "INTEGRITY_ENABLED is false, so nothing checks these files on its "
                "own — running anyway because you asked.\n"
            )
        report = await verify_integrity(
            session_scope, limit=args.limit, apply=not args.dry_run
        )

    rule("integrity")
    out(f"checked   : {report.checked} track(s) with a file on disk")
    out(f"hashed    : {report.hashed} file(s) read in {report.elapsed:.1f}s")
    rule("verdicts")
    _integrity_table(report)

    if report.applied:
        rule("written")
        out(f"baselined : {report.baselined} track(s) measured for the first time")
        out(f"releases  : {report.albums_restamped} content digest(s) updated")
        out(f"re-opened : {report.albums_reopened} release(s) queued for re-identification")
    else:
        out("\nDry run — everything above was measured, nothing was written.")
    return EXIT_OK


async def cmd_baseline_library(args: argparse.Namespace) -> int:
    """Record a hash for every file that has never had one.

    The other half of the three-state rule: ``unknown`` is not ``changed``, so a
    library that has never been measured reports nothing and protects nothing
    until something takes the first measurement. This is that something.

    Safe to repeat and cheap to repeat: it only ever looks at rows with no
    recorded hash, so every run shrinks the set it works on and a second run over
    a finished library reads no files at all. It cannot overwrite a measurement
    anything else made — for that, use ``verify-library``.
    """
    from app.core.scheduler import verify_integrity  # noqa: PLC0415 - keeps startup light

    async with session_ctx(with_client=False):
        report = await verify_integrity(
            session_scope, limit=args.limit, unknown_only=True
        )

    rule("baseline")
    out(f"candidates: {report.checked} track(s) with no recorded hash")
    out(f"hashed    : {report.hashed} file(s) read in {report.elapsed:.1f}s")
    out(f"baselined : {report.baselined} track(s) now have a hash")
    out(f"releases  : {report.albums_restamped} content digest(s) computed")
    if report.checked > report.hashed:
        out(
            f"missing   : {report.checked - report.hashed} file(s) were not there; "
            "the nightly verify pass handles those."
        )
    if args.limit and report.checked >= args.limit:
        out("\nStopped at --limit; run it again to continue.")
    elif not report.checked:
        out("\nEvery file on disk already has a baseline.")
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
            "  cli.py baseline-library --limit 500\n"
            "  cli.py verify-library --dry-run\n"
            "  cli.py enrich-now --artist 312829 --run\n"
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

    integrity = subparsers.add_parser(
        "verify-library",
        help="Hash every file on disk and compare it with the recorded baseline.",
        description=(
            "Opens every file a track row points at, hashes it, and classifies "
            "it against the hash recorded for it: verified, retagged (different "
            "bytes, identical audio), replaced (different audio), missing, or "
            "unknown (never baselined, which is not the same as changed).\n\n"
            "This is the expensive check on purpose. The nightly scan trusts size "
            "and mtime to decide whether opening a file is worth it, and a tool "
            "that rewrites a file and restores its mtime walks past that; this "
            "reads every byte, at roughly 201 ms per file. Use --limit to bound a "
            "run — the order is the same rotation the nightly job uses, so a "
            "limited run measures whatever has gone longest unchecked.\n\n"
            "Entirely local: no Qobuz credentials, no network, and nothing is "
            "queued for download. Files that turn out to hold different audio are "
            "put back on the enrichment work list so the release is identified "
            "again from what is actually there."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    integrity.add_argument(
        "-n",
        "--dry-run",
        action="store_true",
        help="Measure and report without writing a single row.",
    )
    integrity.add_argument(
        "--limit",
        type=int,
        default=None,
        help="Stop after N tracks (default: the whole library).",
    )
    integrity.set_defaults(func=cmd_verify_library, needs_async=True)

    baseline = subparsers.add_parser(
        "baseline-library",
        help="Record a hash for every file that has never had one.",
        description=(
            "Measures the tracks in state 'unknown' — the ones nothing has ever "
            "hashed — and records the result. Until a file has a baseline it "
            "makes no claim, so nothing can tell that it changed; this is what "
            "gives an existing library something to be checked against.\n\n"
            "Idempotent by construction: it only looks at rows with no recorded "
            "hash, so every run shrinks the work and a second run over a finished "
            "library reads nothing. It never overwrites an existing measurement — "
            "that is what verify-library is for.\n\n"
            "Entirely local: no network, no credentials, nothing queued."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    baseline.add_argument(
        "--limit",
        type=int,
        default=None,
        help="Stop after N tracks, to spread the first pass over several runs.",
    )
    baseline.set_defaults(func=cmd_baseline_library, needs_async=True)

    queue = subparsers.add_parser("queue-status", help="Show the download queue.")
    queue.add_argument("--limit", type=int, default=20, help="Rows to show (default 20).")
    queue.add_argument("--all", action="store_true", help="Include finished/failed items.")
    queue.set_defaults(func=cmd_queue_status, needs_async=True)

    enrich = subparsers.add_parser(
        "enrich-now",
        help="Fetch metadata from the open sources for whatever is due.",
        description=(
            "Runs one enrichment batch against Deezer, MusicBrainz and the rest "
            "of the ladder. Read-only: it never downloads audio and never queues "
            "anything, and it touches no Qobuz endpoint at all.\n\n"
            "Without --run it only reports what is due and what each source has "
            "matched so far, which costs nothing and needs no network."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    enrich.add_argument(
        "--run",
        action="store_true",
        help="Actually make requests. Omitted, this is a dry report.",
    )
    enrich.add_argument(
        "--limit",
        type=int,
        default=None,
        help="Entities to process (default: ENRICHMENT_BATCH_SIZE).",
    )
    enrich.add_argument(
        "--artist",
        default=None,
        help="Re-open this artist and its albums first, so they go to the front.",
    )
    enrich.add_argument(
        "--album", default=None, help="Re-open just this album before running."
    )
    enrich.set_defaults(func=cmd_enrich_now, needs_async=True)

    purge = subparsers.add_parser(
        "enrich-purge",
        help="Delete enrichment state rows for anything not in the library.",
        description=(
            "Enrichment is scoped to what is on disk. This deletes the "
            "enrichment_state rows for albums that are not downloaded and for "
            "artists who own none, which is what nightly housekeeping does "
            "anyway — run it by hand after narrowing the scope rather than "
            "waiting a night.\n\n"
            "Only the work list goes. The metadata already fetched stays, so an "
            "album that is downloaded later is re-seeded, not re-fetched. "
            "Entirely local: no network and no Qobuz credentials."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    purge.add_argument(
        "--dry-run",
        action="store_true",
        help="Report the scope and the row count without deleting anything.",
    )
    purge.set_defaults(func=cmd_enrich_purge, needs_async=True)

    folder = subparsers.add_parser(
        "folder",
        help="Bind one folder to a Qobuz album by hand, or mark it as not on Qobuz.",
        description=(
            "The two decisions the audio cannot make.\n\n"
            "With --album, this folder is that Qobuz release; run scan-library "
            "afterwards to adopt it. With neither --album nor --clear, the "
            "folder is marked as holding NO catalogue release -- covers never "
            "released anywhere, a game soundtrack, a radio bootleg.\n\n"
            "Only a person may say that second thing. The automatic chain "
            "failing to find a release is not evidence that none exists, so a "
            "machine allowed to conclude it would quietly retire folders it "
            "merely could not identify. A marked folder leaves the unmatched "
            "count and bind-folders stops spending requests on it.\n\n"
            "--clear undoes either, and the folder returns to the unmatched list."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    folder.add_argument("path", nargs="?", help="The album directory.")
    folder.add_argument("--album", default=None, help="Qobuz album id to bind it to.")
    folder.add_argument("--note", default=None, help="Why — recorded alongside.")
    folder.add_argument("--clear", action="store_true", help="Forget this folder's decision.")
    folder.add_argument("--list", action="store_true", help="List every recorded decision.")
    folder.set_defaults(func=cmd_folder, needs_async=True)

    bind = subparsers.add_parser(
        "bind-folders",
        help="Identify unmatched library folders by their audio and bind them to Qobuz.",
        description=(
            "The disk scan binds a folder to a catalogue row by normalising its "
            "album title, so a folder whose name is a different string for the "
            "same record never matches -- 'Play: The Guitar Album' against a "
            "catalogue that calls it 'Play'. Those releases sit in the wanted "
            "list waiting to be downloaded again.\n\n"
            "This identifies them by audio instead: AcoustID over the files, "
            "every barcode in the release group MusicBrainz agrees on, and one "
            "of those barcodes picks the Qobuz album out of a search. A name is "
            "only ever used to narrow the search, never to choose -- two "
            "candidates is reported as ambiguous, not guessed at.\n\n"
            "Writes only the binding: no status moves, no file is touched and "
            "nothing is queued. Run scan-library afterwards to adopt them."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    bind.add_argument("--path", default=None, help="Directory to walk instead of LIBRARY_PATH.")
    bind.add_argument(
        "--limit", type=int, default=200, help="Most folders to identify (default: 200)."
    )
    bind.add_argument(
        "--dry-run",
        action="store_true",
        help="Run the identification and report, writing no binding.",
    )
    bind.add_argument(
        "-v", "--verbose", action="store_true", help="List the folders still unresolved."
    )
    bind.set_defaults(func=cmd_bind_folders, needs_async=True)

    upc = subparsers.add_parser(
        "backfill-upc",
        help="Fetch the barcode Qobuz holds for releases on disk that have none.",
        description=(
            "The indexer builds album rows from artist/getReleasesList, which "
            "does not publish a upc. Only album/get does, and only the download "
            "loop calls it — so a release Qobuzarr fetched has a barcode and a "
            "release the disk scan adopted does not.\n\n"
            "A barcode is the key exact matching needs: without one, Deezer and "
            "MusicBrainz can only match a release by browsing its artist's "
            "discography, and that artist id is itself derived from a release "
            "that matched on a barcode. This fills the gap with one album/get "
            "per release, scoped to what is on disk and capped by --limit.\n\n"
            "Fills only; a release that already has a upc is skipped. Writes no "
            "other column, queues nothing, and touches no file."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    upc.add_argument(
        "--limit",
        type=int,
        default=1000,
        help="Most releases to ask about in one pass (default: 1000).",
    )
    upc.add_argument(
        "--dry-run",
        action="store_true",
        help="Make the requests and report what would be filled, writing nothing.",
    )
    upc.set_defaults(func=cmd_backfill_upc, needs_async=True)

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
