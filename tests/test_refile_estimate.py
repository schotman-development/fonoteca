"""The library-wide "how many releases would this template move?" count.

Two things are being pinned, and only one of them is arithmetic.

The arithmetic is that every release lands in **exactly one** of ``frozen`` /
``blocked`` / ``would_refile`` / ``in_place``, so the four sum to
``considered``. That is what makes the headline honest: a release somebody
froze is not a release that would move, and a release nothing can render a
target for is not one either, so folding either into the count would report
work that would never happen.

The other is the settings overlay, and it is the reason this feature needed a
code change rather than only an endpoint. ``naming_template`` is one of the
seven overridable keys and this screen is precisely where people override it,
so a planner that re-applies the overlay over a caller-supplied base would put
the *saved* template back on top of the **candidate** one somebody is typing —
a figure that looks computed and is silently about the wrong template.
:func:`test_candidate_template_beats_a_stored_override` is that regression, at
both the librarian level and over HTTP.

Everything here runs against a scratch ``LIBRARY_PATH`` under ``tmp_path``; the
fixture asserts the redirection took effect before any test body runs.
"""

from __future__ import annotations

import asyncio
from datetime import date
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app import config
from app.config import Settings, get_settings
from app.core import librarian
from app.db import get_session
from app.models import (
    Activity,
    Album,
    AlbumStatus,
    Artist,
    Base,
    Track,
    TrackStatus,
)

ARTIST_ID = "720076"

IN_PLACE = "inplace0000001"
MISFILED = "misfiled000002"
FROZEN = "frozen00000003"
BLOCKED = "blocked0000004"
RENAMES = "renames0000005"

#: What the default template renders for each of these rows, except where the
#: point of the row is that it does not.
FOLDERS = {
    IN_PLACE: "All Melody (2018) [FLAC 24-96]",
    MISFILED: "spaces-old-import",
    FROZEN: "felt-old-import",
    BLOCKED: "Solo (2015) [FLAC 24-96]",
    RENAMES: "Screws (2012) [FLAC 24-96]",
}

TITLES = {
    IN_PLACE: "All Melody",
    MISFILED: "Spaces",
    FROZEN: "Felt",
    BLOCKED: "Solo",
    RENAMES: "Screws",
}

YEARS = {
    IN_PLACE: date(2018, 1, 26),
    MISFILED: date(2013, 11, 15),
    FROZEN: date(2011, 10, 14),
    BLOCKED: date(2015, 9, 4),
    RENAMES: date(2012, 10, 1),
}

TRACKS = ("01 - Sunson.flac", "02 - My Friend The Forest.flac")


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
@pytest.fixture(name="settings")
def settings_fixture(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> Iterator[Settings]:
    """Point LIBRARY_PATH and DATA_PATH at scratch directories, process-wide."""
    library = tmp_path / "music"
    data = tmp_path / "data"
    library.mkdir()
    data.mkdir()
    monkeypatch.setenv("LIBRARY_PATH", str(library))
    monkeypatch.setenv("DATA_PATH", str(data))
    config.get_settings.cache_clear()
    config.reset_overrides()
    conf = get_settings()
    assert conf.library_path == library, "refusing to run against the real library"
    try:
        yield conf
    finally:
        config.reset_overrides()
        config.get_settings.cache_clear()


def album_dir(settings: Settings, name: str) -> Path:
    return settings.library_path / "Nils Frahm" / name


def make_album_files(settings: Settings, name: str, files: tuple[str, ...]) -> Path:
    directory = album_dir(settings, name)
    directory.mkdir(parents=True, exist_ok=True)
    for track in files:
        (directory / track).write_bytes(b"audio-" + track.encode())
    return directory


_MAKERS: list[Any] = []

#: One ``(new, dirty, deleted)`` triple per request, taken from the
#: request-scoped session at the moment it is handed back — see
#: :func:`test_writes_nothing`.
_PENDING: list[tuple[int, int, int]] = []


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path, settings: Settings) -> Iterator[TestClient]:
    """TestClient over a scratch DB whose album rows point at real scratch files.

    The session override deliberately **does not commit**: the estimate is
    read-only, and a fixture that committed would hide a write it made.
    """
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'library.db'}", poolclass=NullPool
    )
    session_maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    _PENDING.clear()

    async def override_session() -> AsyncIterator[AsyncSession]:
        session = session_maker()
        try:
            yield session
        finally:
            # Snapshot the request's OWN session while it is still open. Asking
            # a freshly opened one whether anything is pending is the vacuous
            # version of this check — so is asking this one after ``close()``,
            # which expunges every instance and so answers "clean" however the
            # request behaved.
            _PENDING.append((len(session.new), len(session.dirty), len(session.deleted)))
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with session_maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm", monitored=True))
            for album_id, folder in FOLDERS.items():
                # RENAMES sits in the right folder with the wrong filenames, so
                # only the files move; BLOCKED has no track rows at all and its
                # files are not decodable, so nothing can name its quality.
                files = ("1.flac", "2.flac") if album_id == RENAMES else TRACKS
                directory = make_album_files(settings, folder, files)
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=TITLES[album_id],
                        status=AlbumStatus.DOWNLOADED,
                        monitored=True,
                        release_date=YEARS[album_id],
                        release_type="album",
                        tracks_count=2,
                        media_count=1,
                        hires=True,
                        max_bit_depth=24,
                        max_sampling_rate=96.0,
                        freeze_path=album_id == FROZEN,
                        path=str(directory),
                    )
                )
                if album_id == BLOCKED:
                    continue
                for number, name in enumerate(files, start=1):
                    session.add(
                        Track(
                            id=f"{album_id}-{number}",
                            album_id=album_id,
                            title=TRACKS[number - 1]
                            .split(" - ", 1)[1]
                            .removesuffix(".flac"),
                            track_number=number,
                            status=TrackStatus.DOWNLOADED,
                            path=str(directory / name),
                            format_id=7,
                            bit_depth=24,
                            sampling_rate=96.0,
                            file_size=12,
                        )
                    )
            await session.commit()

    asyncio.run(seed())
    _MAKERS.append(session_maker)
    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        main.app.dependency_overrides.pop(get_session, None)
        _MAKERS.pop()


def estimate(client: TestClient, **params: Any) -> dict:
    response = client.get("/api/library/refile/estimate", params=params)
    assert response.status_code == 200, response.text
    return response.json()


def run(coro: Any) -> Any:
    return asyncio.run(coro)


async def _with_session(fn: Any) -> Any:
    async with _MAKERS[-1]() as session:
        return await fn(session)


# ---------------------------------------------------------------------------
# The arithmetic
# ---------------------------------------------------------------------------
def test_buckets_are_exclusive_and_sum(client: TestClient) -> None:
    """One album, one branch, one loop — so the four figures are a partition."""
    out = estimate(client)
    assert out["considered"] == 5
    assert out["frozen"] == 1
    assert out["blocked"] == 1
    assert out["would_refile"] == 2  # MISFILED (folder) + RENAMES (files only)
    assert out["in_place"] == 1
    assert (
        out["frozen"] + out["blocked"] + out["would_refile"] + out["in_place"]
        == out["considered"]
    )


def test_frozen_is_reported_not_skipped(client: TestClient) -> None:
    """FROZEN's folder does not match the template, so it would move if asked."""
    out = estimate(client)
    assert out["frozen"] == 1
    # It is neither counted as work nor mistaken for a problem.
    assert out["would_refile"] == 2
    assert out["blocked"] == 1


def test_blocked_counts_once(client: TestClient) -> None:
    """No track rows and undecodable files: nothing can name the quality tag."""
    out = estimate(client, artist_id=ARTIST_ID)
    assert out["blocked"] == 1
    assert out["level"] == "warning"
    assert "blocked" in out["summary"]


def test_moves_directory_is_a_subset(client: TestClient) -> None:
    """RENAMES only needs its files renamed, so it moves no folder."""
    out = estimate(client)
    assert out["would_refile"] == 2
    assert out["moves_directory"] == 1
    assert out["moves_directory"] < out["would_refile"]


# ---------------------------------------------------------------------------
# The overlay — the reason plan_refile stopped re-applying it
# ---------------------------------------------------------------------------
def test_candidate_template_beats_a_stored_override(client: TestClient) -> None:
    """A stored ``naming_template`` row must not overrule the typed candidate."""
    config.install_overrides({"naming_template": "{artist}/{album}/{title}.{ext}"})
    assert config.get_effective_settings().naming_template.startswith("{artist}/{album}/")

    candidate = "{artist}/{album} ({year})[ [{quality}]]/{disc_prefix}{track:02d} - {title}.{ext}"
    out = estimate(client, template=candidate)
    assert out["template"] == candidate
    # Under the *candidate* the in-place folder really is in place; under the
    # stored override it would not be.
    assert out["in_place"] == 1
    assert out["would_refile"] == 2

    async def check(session: AsyncSession) -> str:
        album = await session.get(Album, IN_PLACE)
        assert album is not None
        conf = get_settings().model_copy(update={"naming_template": candidate})
        plan = await librarian.plan_refile(session, album, conf)
        return plan.target_dir

    target = run(_with_session(check))
    assert target.endswith(FOLDERS[IN_PLACE])


def test_no_template_uses_the_template_in_force(client: TestClient) -> None:
    override = "{artist}/{album}/{title}.{ext}"
    config.install_overrides({"naming_template": override})
    out = estimate(client)
    assert out["template"] == override
    # Every folder carries a year and a quality tag the override does not
    # render, so nothing is in place any more — and BLOCKED stops being blocked,
    # because a template with no ``{quality}`` never had to know the quality.
    assert out["in_place"] == 0
    assert out["blocked"] == 0
    assert out["would_refile"] == 4
    assert out["frozen"] == 1


# ---------------------------------------------------------------------------
# Read-only
# ---------------------------------------------------------------------------
def test_writes_nothing(client: TestClient, settings: Settings) -> None:
    """Nothing pending on the request's own session, and nothing on disk moved.

    The session half is asked of the session the *request* used, captured while
    it was still open (see the fixture). A fresh session opened afterwards has
    nothing pending no matter what happened, which is an assertion that reads
    like a guarantee and is one only by luck.
    """
    before = {
        album_id: (album_dir(settings, folder), album_dir(settings, folder).stat().st_mtime)
        for album_id, folder in FOLDERS.items()
    }
    files_before = {
        path: path.stat().st_mtime
        for path in sorted(settings.library_path.rglob("*.flac"))
    }

    estimate(client)

    assert _PENDING == [(0, 0, 0)], (
        "the request's own session had pending work when it was handed back"
    )

    async def check(session: AsyncSession) -> None:
        activity = (
            await session.execute(select(func.count()).select_from(Activity))
        ).scalar_one()
        assert activity == 0
        for album_id, (directory, mtime) in before.items():
            album = await session.get(Album, album_id)
            assert album is not None
            assert album.path == str(directory)
            assert directory.stat().st_mtime == mtime

    run(_with_session(check))
    assert {
        path: path.stat().st_mtime
        for path in sorted(settings.library_path.rglob("*.flac"))
    } == files_before


# ---------------------------------------------------------------------------
# Refusals and edges
# ---------------------------------------------------------------------------
@pytest.mark.parametrize("template", ["{artist}/{album}/", "{artist}/{album}"])
def test_unrenderable_template_is_a_400_with_a_sentence(
    client: TestClient, template: str
) -> None:
    """Refused by the *same* validator a settings write would use.

    So a template this endpoint counts is one that could be saved. A template
    with no filename component is the case that matters most: it renders every
    track of an album to the same path, which silently leaves a nine-track
    release as one file. (An all-whitespace box is not among them: ``OptStrQuery``
    already reads a blank filter as "no filter", so it means "the one in force".)
    """
    response = client.get("/api/library/refile/estimate", params={"template": template})
    assert response.status_code == 400, response.text
    assert "Rules screen" in response.text


def test_truncated_is_reported(client: TestClient) -> None:
    out = estimate(client, limit=1)
    assert out["considered"] == 1
    assert out["truncated"] is True
    assert out["level"] == "warning"
    assert "first 1 only" in out["summary"]


def test_artist_id_scopes_the_walk(client: TestClient) -> None:
    assert estimate(client, artist_id="nobody")["considered"] == 0
    assert estimate(client, artist_id=ARTIST_ID)["considered"] == 5
