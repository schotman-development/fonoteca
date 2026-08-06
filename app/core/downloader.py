"""Sequential album downloader.

:class:`AlbumDownloader` turns one row of the ``albums`` table into files on
disk.  It is deliberately boring: **one track at a time, never in parallel**,
with a configurable pause between tracks, everything gated by the shared
:class:`~app.qobuz.ratelimit.RateLimiter` inside the client.

Resumability is the other design goal.  All progress lives in SQLite and on the
filesystem, so a restart mid-album picks up where it left off:

* a track whose final file already exists (non-empty) is skipped without a
  single API call;
* audio is streamed to a ``.part`` sibling, tagged there, and only then
  ``os.replace``\\ d into place — so a file that exists is always complete and
  tagged, never a half-written stub;
* the album directory is decided once from stable metadata, so the second run
  looks in the same place as the first.

Failure policy: an individual track failure is logged and the loop continues.
The album is marked :attr:`~app.models.AlbumStatus.FAILED` only if at least one
track genuinely failed after its retries.  Tracks Qobuz refuses to stream at all
(``QobuzUnstreamable``) are marked :attr:`~app.models.TrackStatus.SKIPPED` and
recorded as warnings rather than failures — retrying them forever would just
burn rate-limit budget.
"""

from __future__ import annotations

import asyncio
import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Mapping

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.config import Settings, get_settings
from app.core import librarian, naming, quality
from app.core.quality import QUALITY_PROFILE_FORMATS
from app.core.tagger import detect_image_mime, tag_file
from app.logging_conf import get_logger
from app.models import (
    Activity,
    ActivityLevel,
    Album,
    AlbumStatus,
    Artist,
    QueueItem,
    QueueState,
    Track,
    TrackStatus,
    utcnow,
)
from app.qobuz.client import QobuzClient
from app.qobuz.errors import QobuzError, QobuzNotFound, QobuzUnstreamable
from app.qobuz.mapper import album_track_items, map_album, map_track

__all__ = [
    "AlbumDownloader",
    "DownloadResult",
    "TrackResult",
    "COVER_FILENAME",
    "PART_SUFFIX",
    "QUALITY_PROFILE_FORMATS",
]

logger = get_logger(__name__)

#: Cover art written next to the audio. Defined in :mod:`app.core.naming` (the
#: librarian needs it too and must not import the download loop); re-exported
#: here, and kept in ``__all__``, because this is where it used to live.
COVER_FILENAME = naming.COVER_FILENAME

#: Suffix used while a file is still being written.
PART_SUFFIX = ".part"

#: ``QUALITY_PROFILE_FORMATS`` is imported above from :mod:`app.core.quality`,
#: which owns the named-profile table so the artist page can work out what a
#: download *would* fetch without importing the downloader. It stays importable
#: from here — and in ``__all__`` — because this is where it used to live.

#: Album metadata refreshed from ``album/get`` on every download. Download
#: state (``status``, ``path``, …) is deliberately absent.
_ALBUM_REFRESH_FIELDS: tuple[str, ...] = (
    "title",
    "version",
    "release_date",
    "release_type",
    "tracks_count",
    "media_count",
    "hires",
    "max_bit_depth",
    "max_sampling_rate",
    "label",
    "genre",
    "upc",
    "image_url",
    "duration",
)


# ---------------------------------------------------------------------------
# Results
# ---------------------------------------------------------------------------
@dataclass(slots=True)
class TrackResult:
    """Outcome of a single track within an album download."""

    track_id: str
    title: str
    status: TrackStatus
    path: str | None = None
    bytes_written: int = 0
    already_present: bool = False
    upgraded: bool = False
    """This track replaced a copy that was already on disk in a worse format."""
    superseded: str | None = None
    """The worse copy, when the replacement landed under a different name and so
    did not overwrite it."""
    error: str | None = None

    @property
    def ok(self) -> bool:
        """True when the track ended up on disk (freshly or already there)."""
        return self.status is TrackStatus.DOWNLOADED


@dataclass(slots=True)
class DownloadResult:
    """Outcome of one :meth:`AlbumDownloader.download_album` call."""

    album_id: str
    status: AlbumStatus
    path: str | None = None
    cover_path: str | None = None
    format_id: int | None = None
    """The format that was *requested* — the profile clamped to the account."""
    obtainable_format_id: int | None = None
    """The format a fetch would actually land: :attr:`format_id` clamped again to
    what this release exists in. ``None`` when the release quality is unknown."""
    tracks_total: int = 0
    tracks_downloaded: int = 0
    tracks_skipped: int = 0
    tracks_failed: int = 0
    tracks_unstreamable: int = 0
    tracks_upgraded: int = 0
    """Tracks re-fetched because a better format than the file on disk had
    become obtainable. A subset of ``tracks_downloaded``, not an extra bucket."""
    bytes_written: int = 0
    previous_path: str | None = None
    """Where the album used to live, when an upgrade moved it to a folder named
    after the new quality."""
    superseded_paths: list[str] = field(default_factory=list)
    """Individual files an upgrade replaced under a *different* name, when the
    album directory itself did not change. Empty when ``previous_path`` is set —
    the whole old folder covers them."""
    trashed: str | None = None
    """The trash batch the superseded copy was moved to, when
    ``Settings.upgrade_cleanup`` allowed it. ``None`` means it is still on disk."""
    cancelled: bool = False
    errors: list[str] = field(default_factory=list)
    tracks: list[TrackResult] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        """True when the album finished without a genuine track failure."""
        return self.status is AlbumStatus.DOWNLOADED

    @property
    def summary(self) -> str:
        """One-line human summary, safe to put in an activity message."""
        return (
            f"{self.tracks_downloaded} downloaded, {self.tracks_skipped} already present, "
            f"{self.tracks_unstreamable} unstreamable, {self.tracks_failed} failed "
            f"of {self.tracks_total}"
        )


# ---------------------------------------------------------------------------
# Downloader
# ---------------------------------------------------------------------------
class AlbumDownloader:
    """Downloads one album at a time through a shared :class:`QobuzClient`.

    The client (and therefore the global rate limiter) is injected, never
    created here: the indexer, the UI and the download worker must all share a
    single limiter for the global pacing guarantees to hold.

    Example::

        limiter = RateLimiter.from_settings()
        async with QobuzClient(limiter=limiter) as client:
            downloader = AlbumDownloader(client)
            async with session_scope() as session:
                result = await downloader.download_album(session, "uyej1o165e870")
    """

    def __init__(self, client: QobuzClient, settings: Settings | None = None) -> None:
        """Store the shared client and resolve settings once."""
        self._client = client
        self._settings = settings or get_settings()

    # ------------------------------------------------------------- properties
    @property
    def client(self) -> QobuzClient:
        """The shared Qobuz client this downloader talks through."""
        return self._client

    @property
    def settings(self) -> Settings:
        """Effective settings."""
        return self._settings

    # ------------------------------------------------------------ entry point
    async def download_album(
        self,
        session: AsyncSession,
        album_id: str,
        *,
        queue_item: QueueItem | None = None,
        should_stop: Callable[[], bool] | None = None,
    ) -> DownloadResult:
        """Download every track of *album_id* into the library.

        The album row must already exist (the indexer creates it). Progress is
        committed to *session* as it goes, so the caller sees live state and a
        crash loses at most one track.

        Args:
            session: An open async session. **This method commits it** at each
                checkpoint; do not wrap the call in an outer transaction you
                expect to roll back.
            album_id: Qobuz album id — an opaque string, never an ``int``.
            queue_item: The queue row driving this download. When omitted the
                newest non-terminal row for the album is used, if any.
            should_stop: Optional predicate polled between tracks; when it
                returns true the download stops cleanly and the album is left
                queued for a later resume.

        Returns:
            A :class:`DownloadResult`. Failures are reported through it rather
            than raised — the only exceptions that escape are programming
            errors and database faults.
        """
        album_id = str(album_id)
        album = await session.get(Album, album_id)
        if album is None:
            logger.error("Cannot download album %s: not in the database", album_id)
            return DownloadResult(
                album_id=album_id,
                status=AlbumStatus.FAILED,
                errors=[f"Album {album_id} is not in the database"],
            )

        artist = album.artist or await session.get(Artist, album.artist_id)
        artist_name = artist.name if artist else "Unknown Artist"
        queue_item = queue_item or await self._find_queue_item(session, album_id)

        await self._mark_started(session, album, queue_item)

        result = DownloadResult(album_id=album_id, status=AlbumStatus.DOWNLOADING)
        try:
            await self._run(session, album, artist, artist_name, queue_item, result, should_stop)
        except QobuzError as exc:
            # Album-level API failure (metadata fetch, auth, circuit breaker).
            result.status = AlbumStatus.FAILED
            result.errors.append(str(exc))
            logger.warning("Album %s failed: %s", album_id, exc)
        except OSError as exc:
            result.status = AlbumStatus.FAILED
            result.errors.append(f"Filesystem error: {exc}")
            logger.error("Album %s failed on I/O: %s", album_id, exc)

        await self._finalise(session, album, artist_name, queue_item, result)
        return result

    # --------------------------------------------------------------- the work
    async def _run(
        self,
        session: AsyncSession,
        album: Album,
        artist: Artist | None,
        artist_name: str,
        queue_item: QueueItem | None,
        result: DownloadResult,
        should_stop: Callable[[], bool] | None,
    ) -> None:
        """Fetch metadata, then download every track in order."""
        settings = self._settings

        if not self._client.logged_in:
            # Entitlements decide which formats we may even ask for.
            await self._client.login()

        raw = await self._client.get_album(album.id)
        self._refresh_album(album, raw)
        tracks = await self._sync_tracks(session, album, raw)
        await session.commit()

        result.tracks_total = len(tracks)
        if queue_item is not None:
            queue_item.progress_tracks_total = len(tracks)
            queue_item.progress_tracks_done = 0
            await session.commit()

        if not tracks:
            result.status = AlbumStatus.FAILED
            result.errors.append("Qobuz returned no tracks for this album")
            return

        format_id = self._resolve_format_id(artist)
        result.format_id = format_id

        # What a fetch would *land*, which is the format we asked for clamped to
        # what this release actually exists in. Any file on disk below it is
        # upgradable; without the clamp, a CD master would look upgradable
        # forever because it can never reach a hi-res format id.
        obtainable = quality.obtainable_format_id(album, format_id)
        result.obtainable_format_id = obtainable

        album_dir = naming.render_album_dir(
            artist or artist_name,
            album,
            settings,
            format_id=format_id,
            bit_depth=album.max_bit_depth,
            sampling_rate=album.max_sampling_rate,
        )
        album_dir.mkdir(parents=True, exist_ok=True)
        if album.path and album.path != str(album_dir):
            # The naming template carries the quality tag by default, so an
            # upgrade lands in a *new* folder and the old one is left behind.
            result.previous_path = album.path
        album.path = str(album_dir)
        result.path = str(album_dir)
        await session.commit()

        cover_bytes, cover_path = await self._ensure_cover(album_dir, album, raw)
        result.cover_path = str(cover_path) if cover_path else None

        delay = max(0.0, float(settings.download_track_delay))
        network_used = False

        for track in tracks:
            if should_stop is not None and should_stop():
                result.cancelled = True
                logger.info("Album %s: stop requested, pausing after %d track(s)",
                            album.id, result.tracks_downloaded + result.tracks_skipped)
                break

            if network_used and delay:
                # Pacing between consecutive track downloads, on top of the
                # global limiter. Skipped tracks cost nothing, so no wait.
                await asyncio.sleep(delay)

            track_result, hit_network = await self._download_track(
                session,
                album=album,
                artist=artist,
                artist_name=artist_name,
                track=track,
                album_dir=album_dir,
                format_id=format_id,
                obtainable_format_id=obtainable,
                cover_bytes=cover_bytes,
            )
            network_used = hit_network
            self._record_track(result, track_result)

            if queue_item is not None:
                queue_item.progress_tracks_done = min(
                    result.tracks_downloaded + result.tracks_skipped
                    + result.tracks_failed + result.tracks_unstreamable,
                    queue_item.progress_tracks_total or len(tracks),
                )
            await session.commit()

        on_disk = result.tracks_downloaded + result.tracks_skipped
        if result.cancelled:
            result.status = AlbumStatus.QUEUED
        elif result.tracks_failed:
            result.status = AlbumStatus.FAILED
        elif not on_disk:
            # Every track was unstreamable: nothing landed, so this is not a
            # successful download however politely Qobuz declined.
            result.status = AlbumStatus.FAILED
            result.errors.append("No track of this album could be streamed")
        else:
            result.status = AlbumStatus.DOWNLOADED

    async def _download_track(
        self,
        session: AsyncSession,
        *,
        album: Album,
        artist: Artist | None,
        artist_name: str,
        track: Track,
        album_dir: Path,
        format_id: int,
        obtainable_format_id: int | None,
        cover_bytes: bytes | None,
    ) -> tuple[TrackResult, bool]:
        """Download, tag and place one track.

        Returns the per-track result and whether the network was actually used
        (so the caller only pays the inter-track delay when it means something).
        """
        settings = self._settings
        title = track.display_title

        existing = self._find_existing_file(track, album, artist or artist_name, album_dir)
        if existing is not None and self._is_upgradable(track, obtainable_format_id):
            # Resuming reuses whatever is already on disk — except when a better
            # format has become obtainable, in which case that file is not a
            # resume point, it is the thing being replaced.
            logger.info(
                "Track %s (%s): replacing %s with %s",
                track.id,
                title,
                quality.format_label(quality.track_format_id(track)) or "an unknown format",
                quality.format_label(obtainable_format_id),
            )
            existing = None
            upgrading = True
        else:
            upgrading = False

        if existing is not None:
            size = existing.stat().st_size
            track.status = TrackStatus.DOWNLOADED
            track.path = str(existing)
            track.file_size = size
            if track.downloaded_at is None:
                track.downloaded_at = utcnow()
            logger.debug("Track %s already on disk: %s", track.id, existing.name)
            return (
                TrackResult(
                    track_id=track.id,
                    title=title,
                    status=TrackStatus.DOWNLOADED,
                    path=str(existing),
                    bytes_written=size,
                    already_present=True,
                ),
                False,
            )

        track.status = TrackStatus.DOWNLOADING
        await session.commit()

        attempts = max(1, int(settings.download_max_attempts))
        last_error: str | None = None

        for attempt in range(1, attempts + 1):
            try:
                payload = await self._client.get_file_url(track.id, format_id)
            except QobuzUnstreamable as exc:
                track.status = TrackStatus.SKIPPED
                logger.warning("Track %s (%s) is not streamable: %s", track.id, title, exc)
                return (
                    TrackResult(
                        track_id=track.id,
                        title=title,
                        status=TrackStatus.SKIPPED,
                        error=str(exc),
                    ),
                    True,
                )
            except QobuzNotFound as exc:
                track.status = TrackStatus.SKIPPED
                logger.warning("Track %s (%s) vanished from Qobuz: %s", track.id, title, exc)
                return (
                    TrackResult(
                        track_id=track.id,
                        title=title,
                        status=TrackStatus.SKIPPED,
                        error=str(exc),
                    ),
                    True,
                )
            except QobuzError as exc:
                last_error = str(exc)
                logger.warning(
                    "Track %s: getFileUrl failed (attempt %d/%d): %s",
                    track.id, attempt, attempts, exc,
                )
                if not await self._sleep_before_retry(attempt, attempts):
                    break
                continue

            # Qobuz silently downgrades: what came back is the truth.
            actual_format = _int(payload.get("format_id"), format_id)
            bit_depth = _int(payload.get("bit_depth"), None)
            sampling_rate = _float(payload.get("sampling_rate"), None)
            ext = naming.extension_for_format(actual_format, payload.get("mime_type"))

            relative = naming.render_track_name(
                track,
                album,
                settings,
                artist=artist or artist_name,
                format_id=actual_format,
                bit_depth=bit_depth,
                sampling_rate=sampling_rate,
                ext=ext,
            )
            final_path = album_dir.joinpath(*relative.parts)
            part_path = final_path.with_name(final_path.name + PART_SUFFIX)

            url = payload.get("url")
            if not url:
                # get_file_url should already have raised, but never trust a
                # payload enough to KeyError inside the download loop.
                track.status = TrackStatus.SKIPPED
                logger.warning("Track %s (%s) came back without a URL", track.id, title)
                return (
                    TrackResult(
                        track_id=track.id,
                        title=title,
                        status=TrackStatus.SKIPPED,
                        error="Qobuz returned no file URL",
                    ),
                    True,
                )

            try:
                final_path.parent.mkdir(parents=True, exist_ok=True)
                written = await self._client.stream_to_file(url, part_path)
                if written <= 0:
                    raise QobuzError("Qobuz returned an empty file")

                await asyncio.to_thread(
                    tag_file,
                    part_path,
                    track,
                    album,
                    cover_bytes if settings.download_embed_cover else None,
                    artist_name=artist_name,
                    ext=ext,
                )
                # Atomic within a filesystem: readers never see a partial file.
                await asyncio.to_thread(os.replace, part_path, final_path)
            except QobuzError as exc:
                last_error = str(exc)
                _unlink_quietly(part_path)
                logger.warning(
                    "Track %s: download failed (attempt %d/%d): %s",
                    track.id, attempt, attempts, exc,
                )
                if not await self._sleep_before_retry(attempt, attempts):
                    break
                continue
            except OSError as exc:
                last_error = f"Filesystem error: {exc}"
                _unlink_quietly(part_path)
                logger.warning("Track %s: %s", track.id, last_error)
                if not await self._sleep_before_retry(attempt, attempts):
                    break
                continue

            superseded = track.path
            track.status = TrackStatus.DOWNLOADED
            track.path = str(final_path)
            track.format_id = actual_format
            track.bit_depth = bit_depth
            track.sampling_rate = sampling_rate
            track.file_size = written
            track.downloaded_at = utcnow()
            logger.info(
                "Downloaded %s - %s [%s]",
                album.title,
                title,
                naming.quality_tag(actual_format, bit_depth, sampling_rate) or ext,
            )
            # os.replace() overwrites in place, so a superseded file only
            # survives when the better copy is named differently. Report it
            # upwards; the album-level code clears it away once the whole
            # release has landed intact.
            left_behind = (
                superseded
                if upgrading and superseded and superseded != str(final_path)
                else None
            )
            if left_behind:
                logger.debug(
                    "Track %s upgraded into %s, superseding %s",
                    track.id,
                    final_path.name,
                    left_behind,
                )
            return (
                TrackResult(
                    track_id=track.id,
                    title=title,
                    status=TrackStatus.DOWNLOADED,
                    path=str(final_path),
                    bytes_written=written,
                    upgraded=upgrading,
                    superseded=left_behind,
                ),
                True,
            )

        track.status = TrackStatus.FAILED
        return (
            TrackResult(
                track_id=track.id,
                title=title,
                status=TrackStatus.FAILED,
                error=last_error or "Unknown download error",
            ),
            True,
        )

    # -------------------------------------------------------------- artwork
    async def _ensure_cover(
        self, album_dir: Path, album: Album, raw: Mapping[str, Any]
    ) -> tuple[bytes | None, Path | None]:
        """Fetch ``cover.jpg`` once per album folder.

        Reuses the file when it is already there, so a resumed download costs
        no extra request. Returns the image bytes (for embedding) and the path
        actually written, either of which may be ``None``.
        """
        settings = self._settings
        if not (settings.download_embed_cover or settings.download_write_cover_file):
            return None, None

        cover_path = album_dir / COVER_FILENAME
        if cover_path.exists() and cover_path.stat().st_size > 0:
            data = await asyncio.to_thread(cover_path.read_bytes)
            return (data if settings.download_embed_cover else None), cover_path

        source = album.image_url or _raw_image_url(raw)
        if not source:
            logger.debug("Album %s has no cover image URL", album.id)
            return None, None

        temp_path = cover_path.with_name(COVER_FILENAME + PART_SUFFIX)
        # `_max` is the full-resolution rendition; fall back to what Qobuz gave us.
        candidates = [url for url in (naming.upgrade_image_url(source), source) if url]

        for url in dict.fromkeys(candidates):
            try:
                written = await self._client.stream_to_file(url, temp_path)
            except QobuzError as exc:
                logger.debug("Cover fetch failed for album %s: %s", album.id, exc)
                continue
            if written <= 0:
                _unlink_quietly(temp_path)
                continue

            data = await asyncio.to_thread(temp_path.read_bytes)
            if detect_image_mime(data, "") == "":
                # Not an image at all (an error page, most likely).
                _unlink_quietly(temp_path)
                continue

            if settings.download_write_cover_file:
                await asyncio.to_thread(os.replace, temp_path, cover_path)
                written_path: Path | None = cover_path
            else:
                _unlink_quietly(temp_path)
                written_path = None
            return (data if settings.download_embed_cover else None), written_path

        _unlink_quietly(temp_path)
        logger.info("Could not fetch cover art for album %s", album.id)
        return None, None

    # ----------------------------------------------------------- persistence
    def _refresh_album(self, album: Album, raw: Mapping[str, Any]) -> None:
        """Update album metadata from a fresh ``album/get`` payload."""
        data = map_album(raw, album.artist_id)
        for key in _ALBUM_REFRESH_FIELDS:
            value = data.get(key)
            if value is not None:
                setattr(album, key, value)

    async def _sync_tracks(
        self, session: AsyncSession, album: Album, raw: Mapping[str, Any]
    ) -> list[Track]:
        """Upsert the album's track rows and return them in playing order.

        Existing rows keep their download state — :func:`map_track` only carries
        identity and metadata, never ``status``/``path``/``format_id``.
        """
        existing: dict[str, Track] = {row.id: row for row in album.tracks}
        ordered: list[Track] = []

        for item in album_track_items(raw):
            data = map_track(item, album.id)
            track_id = data.get("id")
            if not track_id:
                continue
            row = existing.get(track_id)
            if row is None:
                row = Track(**data)
                session.add(row)
                existing[track_id] = row
            else:
                for key, value in data.items():
                    if key in ("id", "album_id"):
                        continue
                    setattr(row, key, value)
            ordered.append(row)

        if not ordered:
            # album/get gave us nothing usable; fall back to what we already have.
            ordered = list(existing.values())

        ordered.sort(key=lambda row: (row.media_number or 1, row.track_number or 0, row.id))
        return ordered

    async def _find_queue_item(
        self, session: AsyncSession, album_id: str
    ) -> QueueItem | None:
        """Return the newest non-terminal queue row for *album_id*, if any."""
        stmt = (
            select(QueueItem)
            .where(
                QueueItem.album_id == album_id,
                QueueItem.state.in_((QueueState.PENDING, QueueState.ACTIVE)),
            )
            .order_by(QueueItem.id.desc())
            .limit(1)
        )
        return (await session.execute(stmt)).scalars().first()

    async def _mark_started(
        self, session: AsyncSession, album: Album, queue_item: QueueItem | None
    ) -> None:
        """Flip the album and its queue row into the running state.

        ``attempts`` is only incremented when the caller has not already claimed
        the item: :class:`app.core.queue.QueueWorker` flips the row to
        ``ACTIVE`` and bumps ``attempts`` itself when it picks the item up, so
        counting again here would halve the effective retry budget.
        """
        album.status = AlbumStatus.DOWNLOADING
        if queue_item is not None:
            already_claimed = queue_item.state is QueueState.ACTIVE
            queue_item.state = QueueState.ACTIVE
            queue_item.started_at = queue_item.started_at or utcnow()
            if not already_claimed:
                queue_item.attempts += 1
            queue_item.last_error = None
        await session.commit()

    async def _finalise(
        self,
        session: AsyncSession,
        album: Album,
        artist_name: str,
        queue_item: QueueItem | None,
        result: DownloadResult,
    ) -> None:
        """Write the terminal album/queue state plus an activity entry."""
        album.status = result.status
        if result.status is AlbumStatus.DOWNLOADED:
            album.downloaded_at = utcnow()
            await self._clear_superseded(album, result)

        if queue_item is not None:
            if result.cancelled:
                queue_item.state = QueueState.PENDING
                queue_item.started_at = None
            else:
                queue_item.state = (
                    QueueState.DONE
                    if result.status is AlbumStatus.DOWNLOADED
                    else QueueState.FAILED
                )
                queue_item.finished_at = utcnow()
            queue_item.last_error = "; ".join(result.errors[:5]) or None

        if result.cancelled:
            level, event = ActivityLevel.INFO, "download.paused"
            message = f"Paused '{album.title}' by {artist_name}: {result.summary}"
        elif result.status is AlbumStatus.DOWNLOADED and result.tracks_unstreamable:
            level, event = ActivityLevel.WARNING, "download.partial"
            message = (
                f"Downloaded '{album.title}' by {artist_name} with "
                f"{result.tracks_unstreamable} unstreamable track(s): {result.summary}"
            )
        elif result.status is AlbumStatus.DOWNLOADED and result.tracks_upgraded:
            # Say where the old copy went. Nothing in Fonoteca deletes from the
            # library, so an upgrade that landed in a differently-named folder
            # leaves the previous one behind for the user to remove.
            level, event = ActivityLevel.WARNING, "download.upgraded"
            message = (
                f"Upgraded '{album.title}' by {artist_name} to "
                f"{quality.format_label(result.obtainable_format_id) or 'a better format'}: "
                f"{result.tracks_upgraded} track(s) replaced"
            )
            leftover = result.previous_path or (
                result.superseded_paths[0] if result.superseded_paths else None
            )
            if result.trashed:
                message += f" — the previous copy is in the trash ({result.trashed})"
            elif leftover:
                message += f" — the previous copy is still at {leftover}"
        elif result.status is AlbumStatus.DOWNLOADED:
            level, event = ActivityLevel.INFO, "download.completed"
            message = f"Downloaded '{album.title}' by {artist_name}: {result.summary}"
        else:
            level, event = ActivityLevel.ERROR, "download.failed"
            detail = result.errors[0] if result.errors else "see the log for details"
            message = (
                f"Failed to download '{album.title}' by {artist_name}: "
                f"{result.summary} ({detail})"
            )

        session.add(
            Activity(
                level=level,
                event=event,
                message=message,
                artist_id=album.artist_id,
                album_id=album.id,
            )
        )
        await session.commit()
        logger.info("%s", message)

    async def _clear_superseded(self, album: Album, result: DownloadResult) -> None:
        """Move the copy this upgrade replaced to the trash, if it may.

        Only after a *complete* success. A release that finished with a failed
        or unstreamable track has a new folder that is missing something the old
        one had, and the old one is then the better copy — so the conditions
        below are the whole safety argument, not bookkeeping.

        It goes to the trash rather than being unlinked, so even a wrong call
        here costs a restore instead of a re-download.
        """
        if not self._settings.upgrade_cleanup or not result.tracks_upgraded:
            return
        if result.cancelled or result.tracks_failed or result.tracks_unstreamable:
            logger.info(
                "Album %s: keeping the previous copy — the upgrade was not complete",
                album.id,
            )
            return

        # A changed album directory means the whole old folder is a duplicate,
        # and trashing it covers every file inside; the per-track list only
        # matters when the release stayed put and files were renamed.
        targets = [result.previous_path] if result.previous_path else result.superseded_paths
        for target in targets:
            try:
                entry = await asyncio.to_thread(
                    librarian.move_to_trash,
                    target,
                    settings=self._settings,
                    reason="superseded by an upgrade",
                    album=album,
                )
            except librarian.LibraryError as exc:
                logger.warning("Could not clear away %s: %s", target, exc)
                continue
            result.trashed = entry.id

    # ---------------------------------------------------------------- helpers
    def _resolve_format_id(self, artist: Artist | None) -> int:
        """Pick the format to request: the artist's profile, clamped to the account.

        ``QobuzClient.best_format_id`` does the clamping against the formats the
        subscription actually entitles us to, so a hi-res profile on a lossless
        plan quietly becomes FLAC 16/44.1 instead of failing.
        """
        profile = (artist.quality_profile if artist else "") or self._settings.default_quality_profile
        requested = self._format_for_profile(profile)
        return self._client.best_format_id(requested)

    def _format_for_profile(self, profile: str) -> int:
        """Translate a quality-profile name into a Qobuz ``format_id``."""
        return quality.format_for_profile(profile, self._settings.default_format_id)

    @staticmethod
    def _is_upgradable(track: Track, obtainable_format_id: int | None) -> bool:
        """True when a better copy of *track* than the one on disk can be had.

        This is the same comparison the artist page uses to decide whether to
        show an *Upgrade* button (:func:`app.core.quality.upgrade_available`), by
        design: a button that promised an upgrade the download loop then refused
        to perform would simply do nothing.

        Anything unknown answers ``False``. A file whose format cannot be
        determined stays where it is rather than being re-fetched on a guess.
        """
        if obtainable_format_id is None:
            return False
        owned = quality.track_format_id(track)
        return owned is not None and owned < obtainable_format_id

    def _find_existing_file(
        self, track: Track, album: Album, artist: Any, album_dir: Path
    ) -> Path | None:
        """Return the already-downloaded file for *track*, if there is one.

        The delivered format is not known until ``getFileUrl`` has been called,
        so every plausible extension is probed. Only a non-empty file counts —
        a zero-byte leftover is treated as absent and re-downloaded.
        """
        for extension in naming.AUDIO_EXTENSIONS:
            relative = naming.render_track_name(
                track,
                album,
                self._settings,
                artist=artist,
                format_id=None,
                ext=extension,
            )
            candidate = album_dir.joinpath(*relative.parts)
            try:
                if candidate.is_file() and candidate.stat().st_size > 0:
                    return candidate
            except OSError:  # pragma: no cover - racing with an external mv
                continue

        # A path recorded on a previous run wins too, even if the template moved.
        if track.path:
            recorded = Path(track.path)
            try:
                if recorded.is_file() and recorded.stat().st_size > 0:
                    return recorded
            except OSError:  # pragma: no cover
                return None
        return None

    async def _sleep_before_retry(self, attempt: int, attempts: int) -> bool:
        """Back off between per-track attempts; False when there are none left."""
        if attempt >= attempts:
            return False
        settings = self._settings
        delay = min(
            float(settings.backoff_max),
            float(settings.backoff_initial) * (float(settings.backoff_multiplier) ** (attempt - 1)),
        )
        await asyncio.sleep(max(0.0, delay))
        return True

    @staticmethod
    def _record_track(result: DownloadResult, track_result: TrackResult) -> None:
        """Fold one :class:`TrackResult` into the album-level counters."""
        result.tracks.append(track_result)
        if track_result.status is TrackStatus.DOWNLOADED:
            if track_result.already_present:
                result.tracks_skipped += 1
            else:
                result.tracks_downloaded += 1
                result.bytes_written += track_result.bytes_written
                if track_result.upgraded:
                    result.tracks_upgraded += 1
                if track_result.superseded:
                    result.superseded_paths.append(track_result.superseded)
        elif track_result.status is TrackStatus.SKIPPED:
            result.tracks_unstreamable += 1
            if track_result.error:
                result.errors.append(f"{track_result.title}: {track_result.error}")
        else:
            result.tracks_failed += 1
            if track_result.error:
                result.errors.append(f"{track_result.title}: {track_result.error}")


# ---------------------------------------------------------------------------
# Module-level helpers
# ---------------------------------------------------------------------------
def _int(value: Any, default: int | None = None) -> int | None:
    """Best-effort integer conversion that never raises."""
    if value is None or isinstance(value, bool):
        return default
    try:
        return int(value)
    except (TypeError, ValueError):
        try:
            return int(float(value))
        except (TypeError, ValueError):
            return default


def _float(value: Any, default: float | None = None) -> float | None:
    """Best-effort float conversion that never raises."""
    if value is None or isinstance(value, bool):
        return default
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _unlink_quietly(path: Path) -> None:
    """Delete *path* if it exists, ignoring any filesystem complaint."""
    try:
        path.unlink(missing_ok=True)
    except OSError:  # pragma: no cover - best-effort cleanup
        logger.debug("Could not remove %s", path)


def _raw_image_url(raw: Mapping[str, Any]) -> str | None:
    """Pull a cover URL straight out of an ``album/get`` payload."""
    from app.qobuz.mapper import pick_image_url

    for key in ("image", "images", "cover"):
        url = pick_image_url(raw.get(key))
        if url:
            return url
    return None
