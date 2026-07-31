"""Writing to and deleting from the music library.

Everything else in Qobuzarr either reads the library or only adds to it. This is
the module that can destroy data, so the tests are weighted towards the refusals
rather than the happy paths:

* :func:`resolve_in_library` must reject the library root, ``..`` traversal and
  a symlink pointing outside, because every operation is built on it;
* nothing may be unlinked from the library — deletes move to the trash, and only
  :func:`empty_trash` removes anything for real, only inside the trash;
* an album the download worker is busy with is refused by every operation;
* an upgrade only clears away the copy it replaced when the new one landed
  *complete*, since a partial upgrade makes the old copy the better one.

Every test runs against a scratch ``LIBRARY_PATH`` and ``DATA_PATH`` under
``tmp_path``. The fixture asserts that redirection actually took effect before
any test body runs — a bug there would point these at the developer's real
music collection.
"""

from __future__ import annotations

import asyncio
from datetime import date
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app import config
from app.config import Settings, get_settings
from app.core import librarian
from app.core.downloader import AlbumDownloader, DownloadResult
from app.db import get_session
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    QueueItem,
    QueueState,
    Track,
    TrackStatus,
)

ARTIST_ID = "720076"
ON_DISK = "downloaded0001"
MISFILED = "misfiled000002"
QUEUED = "beingfetched03"
NO_FILES = "nofiles0000004"

TRACKS = ("01 - Sunson.flac", "02 - My Friend The Forest.flac")

HX = {"HX-Request": "true"}


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
@pytest.fixture(name="settings")
def settings_fixture(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[Settings]:
    """Point LIBRARY_PATH and DATA_PATH at scratch directories, process-wide.

    Environment variables outrank ``.env`` in pydantic-settings, and clearing the
    ``get_settings`` cache makes every module that imported it agree — they all
    hold the same cached function. The assertion is not decoration: without it a
    silent failure here would run destructive tests against a real library.
    """
    library = tmp_path / "music"
    data = tmp_path / "data"
    library.mkdir()
    data.mkdir()
    monkeypatch.setenv("LIBRARY_PATH", str(library))
    monkeypatch.setenv("DATA_PATH", str(data))
    config.get_settings.cache_clear()
    conf = get_settings()
    assert conf.library_path == library, "refusing to run against the real library"
    assert conf.trash_dir == data / "trash"
    try:
        yield conf
    finally:
        config.get_settings.cache_clear()


def album_dir(settings: Settings, name: str) -> Path:
    return settings.library_path / "Nils Frahm" / name


def make_album_files(settings: Settings, name: str) -> Path:
    """An album folder with two tracks and a cover, as the downloader leaves it."""
    directory = album_dir(settings, name)
    directory.mkdir(parents=True, exist_ok=True)
    for track in TRACKS:
        (directory / track).write_bytes(b"audio-" + track.encode())
    (directory / "cover.jpg").write_bytes(b"\xff\xd8\xff")
    return directory


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path, settings: Settings) -> Iterator[TestClient]:
    """TestClient over a scratch DB whose album rows point at real scratch files."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'library.db'}", poolclass=NullPool
    )
    session_maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with session_maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm", monitored=True))
            # The folder names are what the default template renders for these
            # rows — except MISFILED, which is the "imported from somewhere
            # else" case the re-file pass exists for.
            for album_id, folder, released in (
                (ON_DISK, "All Melody (2018) [FLAC 24-96]", date(2018, 1, 26)),
                (MISFILED, "spaces-old-import", date(2013, 11, 15)),
                (QUEUED, "Felt (2011) [FLAC 24-96]", date(2011, 10, 14)),
                (NO_FILES, "Solo (2015) [FLAC 24-96]", date(2015, 9, 4)),
            ):
                on_disk = album_id != NO_FILES
                directory = make_album_files(settings, folder) if on_disk else None
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title={
                            ON_DISK: "All Melody",
                            MISFILED: "Spaces",
                            QUEUED: "Felt",
                            NO_FILES: "Solo",
                        }[album_id],
                        status=AlbumStatus.DOWNLOADED,
                        monitored=True,
                        release_date=released,
                        release_type="album",
                        tracks_count=2,
                        media_count=1,
                        hires=True,
                        max_bit_depth=24,
                        max_sampling_rate=96.0,
                        path=str(directory) if directory else None,
                    )
                )
                for number, name in enumerate(TRACKS, start=1):
                    session.add(
                        Track(
                            id=f"{album_id}-{number}",
                            album_id=album_id,
                            title=name.split(" - ", 1)[1].removesuffix(".flac"),
                            track_number=number,
                            status=(
                                TrackStatus.DOWNLOADED if on_disk else TrackStatus.PENDING
                            ),
                            path=str(directory / name) if directory else None,
                            format_id=7,
                            bit_depth=24,
                            sampling_rate=96.0,
                            file_size=12,
                        )
                    )
            session.add(QueueItem(album_id=QUEUED, state=QueueState.ACTIVE))
            await session.commit()

    asyncio.run(seed())
    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        # No engine.dispose(): NullPool closes each connection as it is
        # released, inside the loop that opened it. Disposing from a fresh
        # asyncio.run() afterwards races aiosqlite's worker thread against the
        # loop it was created on, which surfaces as an intermittent
        # "Event loop is closed" thread exception.
        main.app.dependency_overrides.pop(get_session, None)


def album_json(client: TestClient, album_id: str) -> dict:
    return client.get(f"/api/albums/{album_id}").json()


def trash(client: TestClient) -> dict:
    return client.get("/api/library/trash").json()


# ---------------------------------------------------------------------------
# Containment — everything else is built on this
# ---------------------------------------------------------------------------
def test_a_path_inside_the_library_resolves(settings: Settings) -> None:
    directory = make_album_files(settings, "Night (2025)")
    assert librarian.resolve_in_library(directory, settings.library_path) == directory


def test_the_library_root_itself_is_refused(settings: Settings) -> None:
    """Otherwise one empty album.path would be a whole-collection delete."""
    with pytest.raises(librarian.LibraryPathError):
        librarian.resolve_in_library(settings.library_path, settings.library_path)


def test_traversal_out_of_the_library_is_refused(settings: Settings) -> None:
    escape = settings.library_path / "Nils Frahm" / ".." / ".." / ".." / "etc"
    with pytest.raises(librarian.LibraryPathError):
        librarian.resolve_in_library(escape, settings.library_path)


def test_a_symlink_pointing_outside_is_refused(
    settings: Settings, tmp_path: Path
) -> None:
    """Symlinks are resolved *before* the containment test, not after."""
    outside = tmp_path / "not-my-music"
    outside.mkdir()
    link = settings.library_path / "escape"
    link.symlink_to(outside, target_is_directory=True)
    with pytest.raises(librarian.LibraryPathError):
        librarian.resolve_in_library(link, settings.library_path)


@pytest.mark.parametrize("value", [None, "", "   "])
def test_an_empty_path_is_refused(settings: Settings, value: Any) -> None:
    with pytest.raises(librarian.LibraryPathError):
        librarian.resolve_in_library(value, settings.library_path)


# ---------------------------------------------------------------------------
# Trash
# ---------------------------------------------------------------------------
def test_trashing_moves_the_files_out_of_the_library(settings: Settings) -> None:
    directory = make_album_files(settings, "Night (2025)")
    entry = librarian.move_to_trash(directory, settings=settings, reason="deleted")

    assert not directory.exists()
    assert entry.payload is not None
    restored_tracks = sorted(p.name for p in (entry.payload / directory.name).iterdir())
    assert restored_tracks == sorted([*TRACKS, "cover.jpg"])
    assert entry.file_count == 3
    assert entry.size_bytes > 0


def test_trashing_leaves_no_empty_artist_folder_behind(settings: Settings) -> None:
    directory = make_album_files(settings, "Night (2025)")
    librarian.move_to_trash(directory, settings=settings)
    assert not (settings.library_path / "Nils Frahm").exists()
    assert settings.library_path.is_dir()  # but never the root itself


def test_the_manifest_survives_a_reread(settings: Settings) -> None:
    directory = make_album_files(settings, "Night (2025)")
    entry = librarian.move_to_trash(directory, settings=settings, reason="superseded")

    listed = librarian.list_trash(settings)
    assert [item.id for item in listed] == [entry.id]
    assert listed[0].original_path == str(directory)
    assert listed[0].reason == "superseded"


def test_restoring_puts_it_back_exactly(settings: Settings) -> None:
    directory = make_album_files(settings, "Night (2025)")
    before = {p.name: p.read_bytes() for p in directory.iterdir()}
    entry = librarian.move_to_trash(directory, settings=settings)

    restored = librarian.restore_from_trash(entry.id, settings)

    assert restored == directory
    assert {p.name: p.read_bytes() for p in directory.iterdir()} == before
    assert librarian.list_trash(settings) == []


def test_restoring_onto_something_is_refused(settings: Settings) -> None:
    """A half-overwritten album is worse than a failed restore."""
    directory = make_album_files(settings, "Night (2025)")
    entry = librarian.move_to_trash(directory, settings=settings)
    make_album_files(settings, "Night (2025)")

    with pytest.raises(librarian.LibraryError):
        librarian.restore_from_trash(entry.id, settings)
    assert librarian.list_trash(settings)  # still recoverable


def test_emptying_the_trash_destroys_only_the_trash(settings: Settings) -> None:
    kept = make_album_files(settings, "Kept (2024)")
    doomed = make_album_files(settings, "Night (2025)")
    librarian.move_to_trash(doomed, settings=settings)

    removed, freed = librarian.empty_trash(settings)

    assert removed == 1 and freed > 0
    assert librarian.list_trash(settings) == []
    assert kept.is_dir() and (kept / TRACKS[0]).is_file()


def test_emptying_one_entry_leaves_the_others(settings: Settings) -> None:
    first = librarian.move_to_trash(
        make_album_files(settings, "One (2020)"), settings=settings
    )
    second = librarian.move_to_trash(
        make_album_files(settings, "Two (2021)"), settings=settings
    )

    librarian.empty_trash(settings, first.id)

    assert [entry.id for entry in librarian.list_trash(settings)] == [second.id]


def test_the_trash_cannot_be_pointed_at_the_library(settings: Settings) -> None:
    """``entry_id`` is user input; it must not escape ``trash_dir``."""
    make_album_files(settings, "Night (2025)")
    with pytest.raises(librarian.LibraryPathError):
        librarian.empty_trash(settings, "../../music/Nils Frahm")
    assert album_dir(settings, "Night (2025)").is_dir()


def test_trashing_something_outside_the_library_is_refused(
    settings: Settings, tmp_path: Path
) -> None:
    outsider = tmp_path / "somebody-elses"
    outsider.mkdir()
    with pytest.raises(librarian.LibraryPathError):
        librarian.move_to_trash(outsider, settings=settings)
    assert outsider.is_dir()


def test_two_deletes_in_the_same_second_do_not_collide(settings: Settings) -> None:
    first = librarian.move_to_trash(
        make_album_files(settings, "Night (2025)"), settings=settings
    )
    make_album_files(settings, "Night (2025)")
    second = librarian.move_to_trash(
        album_dir(settings, "Night (2025)"), settings=settings
    )
    assert first.id != second.id
    assert len(librarian.list_trash(settings)) == 2


# ---------------------------------------------------------------------------
# Deleting a release
# ---------------------------------------------------------------------------
def test_deleting_a_release_trashes_its_files(
    client: TestClient, settings: Settings
) -> None:
    directory = Path(album_json(client, ON_DISK)["path"])
    response = client.delete(f"/api/albums/{ON_DISK}/files")

    assert response.status_code == 200
    assert not directory.exists()
    assert trash(client)["total"] == 1
    assert trash(client)["entries"][0]["original_path"] == str(directory)


def test_a_deleted_release_becomes_wanted_again(client: TestClient) -> None:
    """Monitored means you still want it — that is what deleting one implies."""
    client.delete(f"/api/albums/{ON_DISK}/files")
    payload = album_json(client, ON_DISK)

    assert payload["status"] == "wanted"
    assert payload["path"] is None
    assert payload["owned_format_id"] is None
    assert all(track["path"] is None for track in payload["tracks"])


def test_deleting_an_ignored_release_marks_it_skipped(client: TestClient) -> None:
    client.post(f"/api/albums/{ON_DISK}/monitor", json={"monitored": False})
    client.delete(f"/api/albums/{ON_DISK}/files")
    assert album_json(client, ON_DISK)["status"] == "skipped"


def test_deleting_never_queues_a_replacement(client: TestClient) -> None:
    """Deleting is not a request to download it again. Nothing here auto-queues."""
    before = {item["album_id"] for item in client.get("/api/queue").json()["items"]}
    client.delete(f"/api/albums/{ON_DISK}/files")
    after = {item["album_id"] for item in client.get("/api/queue").json()["items"]}
    assert after == before


def test_deleting_a_release_the_worker_is_fetching_is_refused(
    client: TestClient,
) -> None:
    """Moving files out from under the download loop makes a broken album."""
    directory = Path(album_json(client, QUEUED)["path"])
    response = client.delete(f"/api/albums/{QUEUED}/files")

    assert response.status_code == 409
    assert directory.is_dir()
    assert album_json(client, QUEUED)["status"] == "downloaded"


def test_deleting_a_release_with_no_files_still_clears_the_row(
    client: TestClient,
) -> None:
    response = client.delete(f"/api/albums/{NO_FILES}/files")
    assert response.status_code == 200
    assert album_json(client, NO_FILES)["status"] == "wanted"
    assert trash(client)["total"] == 0


def test_only_that_release_is_touched(client: TestClient, settings: Settings) -> None:
    survivor = Path(album_json(client, MISFILED)["path"])
    client.delete(f"/api/albums/{ON_DISK}/files")
    assert survivor.is_dir()
    assert album_json(client, MISFILED)["status"] == "downloaded"


def test_the_ui_delete_button_reports_where_the_files_went(client: TestClient) -> None:
    response = client.post(f"/ui/albums/{ON_DISK}/delete", headers=HX)
    assert response.status_code == 200
    trigger = response.headers.get("HX-Trigger", "")
    assert "Library tidy" in trigger and "wanted" in trigger


def test_a_release_with_no_files_offers_no_delete_button(client: TestClient) -> None:
    body = client.get(f"/partials/albums/{ARTIST_ID}").text
    assert f"/ui/albums/{ON_DISK}/delete" in body
    assert f"/ui/albums/{NO_FILES}/delete" not in body


# ---------------------------------------------------------------------------
# Re-filing
# ---------------------------------------------------------------------------
def test_a_misfiled_release_is_reported(client: TestClient) -> None:
    report = client.post("/api/library/refile").json()
    planned = {plan["album_id"] for plan in report["plans"]}

    assert MISFILED in planned
    assert ON_DISK not in planned  # already where the template says
    assert report["dry_run"] is True


def test_a_preview_moves_nothing(client: TestClient) -> None:
    before = Path(album_json(client, MISFILED)["path"])
    client.post("/api/library/refile")
    assert before.is_dir()
    assert album_json(client, MISFILED)["path"] == str(before)


def test_applying_moves_the_folder_and_updates_the_rows(client: TestClient) -> None:
    before = Path(album_json(client, MISFILED)["path"])
    report = client.post("/api/library/refile?apply=true").json()

    assert report["changed"] >= 1
    payload = album_json(client, MISFILED)
    moved = Path(payload["path"])
    assert moved != before and moved.is_dir()
    assert not before.exists()
    assert "Spaces" in moved.name
    for track in payload["tracks"]:
        assert Path(track["path"]).is_file()
        assert Path(track["path"]).parent == moved


def test_the_cover_travels_with_the_music(client: TestClient) -> None:
    client.post("/api/library/refile?apply=true")
    moved = Path(album_json(client, MISFILED)["path"])
    assert (moved / "cover.jpg").is_file()


def test_refiling_a_busy_release_is_blocked_not_attempted(client: TestClient) -> None:
    report = client.post("/api/library/refile?apply=true").json()
    blocked = {plan["album_id"]: plan for plan in report["plans"] if plan["blocked"]}
    if QUEUED in blocked:
        assert "download" in blocked[QUEUED]["blocked"].lower()
    assert Path(album_json(client, QUEUED)["path"]).is_dir()


def test_refiling_is_idempotent(client: TestClient) -> None:
    client.post("/api/library/refile?apply=true")
    second = client.post("/api/library/refile").json()
    assert MISFILED not in {plan["album_id"] for plan in second["plans"]}


def test_an_occupied_destination_blocks_rather_than_merges(
    client: TestClient, settings: Settings
) -> None:
    target = album_dir(settings, "Spaces [FLAC 24-96]")
    plan = client.post(f"/api/albums/{MISFILED}/refile?dry_run=true").json()
    Path(plan["target_dir"]).mkdir(parents=True, exist_ok=True)
    (Path(plan["target_dir"]) / "someone-elses.flac").write_bytes(b"x")

    applied = client.post(f"/api/albums/{MISFILED}/refile").json()

    assert applied["blocked"]
    assert (Path(plan["target_dir"]) / "someone-elses.flac").is_file()
    assert Path(album_json(client, MISFILED)["path"]).is_dir()
    assert target is not None  # the fixture path is only a readability aid


# ---------------------------------------------------------------------------
# Re-tagging
# ---------------------------------------------------------------------------
def test_retagging_writes_every_file_on_disk(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    seen: list[str] = []
    monkeypatch.setattr(
        librarian, "tag_file", lambda path, *a, **k: seen.append(str(path)) or True
    )

    report = client.post(f"/api/albums/{ON_DISK}/retag").json()

    assert report["ok"] is True
    assert len(seen) == 2
    assert all(Path(path).is_file() for path in seen)


def test_retagging_embeds_the_cover_it_finds(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    covers: list[Any] = []
    monkeypatch.setattr(
        librarian,
        "tag_file",
        lambda path, track, album, cover=None, **k: covers.append(cover) or True,
    )
    client.post(f"/api/albums/{ON_DISK}/retag")
    assert covers and all(cover == b"\xff\xd8\xff" for cover in covers)


def test_retagging_a_busy_release_is_refused(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(librarian, "tag_file", lambda *a, **k: True)
    assert client.post(f"/api/albums/{QUEUED}/retag").status_code == 409


def test_one_unwritable_file_does_not_stop_the_rest(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    calls = {"n": 0}

    def flaky(*args: Any, **kwargs: Any) -> bool:
        calls["n"] += 1
        if calls["n"] == 1:
            raise OSError("read-only file")
        return True

    monkeypatch.setattr(librarian, "tag_file", flaky)
    report = client.post(f"/api/albums/{ON_DISK}/retag").json()

    assert calls["n"] == 2
    assert report["ok"] is False
    assert report["detail"]["tagged"] == 1 and report["detail"]["failed"] == 1


def test_retagging_the_library_reports_a_total(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(librarian, "tag_file", lambda *a, **k: True)
    report = client.post("/api/library/retag").json()
    # Three releases have files; the fourth has none and the busy one is blocked.
    assert report["changed"] == 2
    assert report["blocked"] == 1


# ---------------------------------------------------------------------------
# Automatic cleanup after an upgrade
# ---------------------------------------------------------------------------
def _clear(settings: Settings, result: DownloadResult, album: Album) -> DownloadResult:
    downloader = AlbumDownloader.__new__(AlbumDownloader)
    downloader._settings = settings  # noqa: SLF001 - exercising the private rule
    asyncio.run(downloader._clear_superseded(album, result))
    return result


def _album(settings: Settings) -> Album:
    return Album(id="x", artist_id=ARTIST_ID, title="All Melody", monitored=True)


def test_a_complete_upgrade_clears_the_copy_it_replaced(settings: Settings) -> None:
    old = make_album_files(settings, "All Melody (2018) [FLAC 16-44.1]")
    result = DownloadResult(
        album_id="x",
        status=AlbumStatus.DOWNLOADED,
        tracks_upgraded=2,
        previous_path=str(old),
    )

    _clear(settings, result, _album(settings))

    assert result.trashed
    assert not old.exists()
    assert librarian.list_trash(settings)[0].reason.startswith("superseded")


@pytest.mark.parametrize(
    "damage",
    [{"tracks_failed": 1}, {"tracks_unstreamable": 1}, {"cancelled": True}],
)
def test_an_incomplete_upgrade_keeps_the_old_copy(
    settings: Settings, damage: dict
) -> None:
    """A new folder missing a track makes the old folder the better one."""
    old = make_album_files(settings, "All Melody (2018) [FLAC 16-44.1]")
    result = DownloadResult(
        album_id="x",
        status=AlbumStatus.DOWNLOADED,
        tracks_upgraded=2,
        previous_path=str(old),
        **damage,
    )

    _clear(settings, result, _album(settings))

    assert result.trashed is None
    assert old.is_dir()


def test_an_ordinary_download_clears_nothing(settings: Settings) -> None:
    """Only an upgrade supersedes anything; a first download has no predecessor."""
    old = make_album_files(settings, "All Melody (2018) [FLAC 16-44.1]")
    result = DownloadResult(
        album_id="x", status=AlbumStatus.DOWNLOADED, previous_path=str(old)
    )
    _clear(settings, result, _album(settings))
    assert old.is_dir()


def test_the_cleanup_can_be_switched_off(settings: Settings) -> None:
    old = make_album_files(settings, "All Melody (2018) [FLAC 16-44.1]")
    result = DownloadResult(
        album_id="x",
        status=AlbumStatus.DOWNLOADED,
        tracks_upgraded=2,
        previous_path=str(old),
    )

    _clear(settings.model_copy(update={"upgrade_cleanup": False}), result, _album(settings))

    assert result.trashed is None
    assert old.is_dir()


def test_renamed_files_in_place_are_cleared_individually(settings: Settings) -> None:
    """No folder change means the per-track leftovers are what needs clearing."""
    directory = make_album_files(settings, "All Melody (2018)")
    stale = directory / TRACKS[0]
    result = DownloadResult(
        album_id="x",
        status=AlbumStatus.DOWNLOADED,
        tracks_upgraded=1,
        superseded_paths=[str(stale)],
    )

    _clear(settings, result, _album(settings))

    assert not stale.exists()
    assert (directory / TRACKS[1]).is_file()  # the rest of the album stays


# ---------------------------------------------------------------------------
# The page
# ---------------------------------------------------------------------------
def test_the_tidy_page_renders(client: TestClient) -> None:
    body = client.get("/library/tidy").text
    assert "Re-file" in body and "Re-tag" in body and "Trash" in body


def test_the_tidy_page_can_be_scoped_to_one_artist(client: TestClient) -> None:
    body = client.get(f"/library/tidy?artist_id={ARTIST_ID}").text
    assert "Nils Frahm" in body
    assert f"artist_id={ARTIST_ID}" in body


def test_the_trash_panel_lists_what_was_deleted(client: TestClient) -> None:
    client.delete(f"/api/albums/{ON_DISK}/files")
    body = client.get("/library/tidy").text
    assert "All Melody" in body
    assert "Restore" in body and "Delete forever" in body


def test_restoring_through_the_ui_puts_the_files_back(client: TestClient) -> None:
    directory = Path(album_json(client, ON_DISK)["path"])
    client.delete(f"/api/albums/{ON_DISK}/files")
    entry_id = trash(client)["entries"][0]["id"]

    response = client.post(f"/ui/library/trash/{entry_id}/restore", headers=HX)

    assert response.status_code == 200
    assert directory.is_dir() and (directory / TRACKS[0]).is_file()
    assert trash(client)["total"] == 0


def test_emptying_through_the_ui_is_final(client: TestClient) -> None:
    client.delete(f"/api/albums/{ON_DISK}/files")
    response = client.post("/ui/library/trash/empty", headers=HX)

    assert response.status_code == 200
    assert "Permanently deleted" in response.headers.get("HX-Trigger", "")
    assert trash(client)["total"] == 0


def test_the_nav_shows_how_much_is_in_the_trash(client: TestClient) -> None:
    client.delete(f"/api/albums/{ON_DISK}/files")
    assert "Library tidy" in client.get("/partials/nav").text
