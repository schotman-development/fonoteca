"""The nightly integrity pass: measuring the library against its own record.

This is principle four — *verify the disk before trusting the database* — as a
job, and until now nothing exercised it. That gap was not theoretical: a
one-line change to how the stamp is written left the whole suite green while
making every subsequent pass classify the same file ``REPLACED`` forever and
re-queue its release for identification every night.

The files are real FLACs, the hashes are real, and the enricher reached through
:mod:`app.core.state` is a real :class:`~app.core.enricher.Enricher` with a
canned provider — because the thing being pinned down is what the pass *writes*,
and three of the four writes go somewhere else: ``Track``'s stamp,
``Album.content_digest`` and ``enrichment_state``.

What each verdict licenses, which is the whole design:

* ``UNKNOWN`` is baselined and is **not** change — the state of every file in a
  library on the day this ships;
* ``VERIFIED`` writes the same values back and reports nothing;
* ``RETAGGED`` re-stamps and stops there: the sample count proves the audio is
  the same recording, so the identification still holds;
* ``REPLACED`` re-stamps *and* re-opens the release, because the recording ids,
  the release match and the fingerprint verdicts were all made about bytes that
  are gone;
* ``MISSING`` writes nothing at all, so an unmounted share cannot erase the
  baseline it is about to need again.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from pathlib import Path
from types import SimpleNamespace
from typing import Any, AsyncIterator, Iterator

import pytest
from mutagen.flac import FLAC
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.config import Settings
from app.core import scheduler as scheduler_module
from app.core import state as state_module
from app.core.enricher import Enricher
from app.core.integrity import IntegrityState, album_content_digest, hash_file
from app.core.scanner import LibraryScanner
from app.core.scheduler import _reverify_files, verify_integrity
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
    Track,
    TrackStatus,
)
from tests.test_enrichment_scope import FakeProvider
from tests.test_library_scan import write_flac

ARTIST_ID = "312829"
ALBUM_ID = "uyej1o165e870"
FILES = ("01 - Sunson.flac", "02 - Says.flac", "03 - My Friend The Forest.flac")


# ---------------------------------------------------------------------------
# A real album on disk, and rows that claim to describe it
# ---------------------------------------------------------------------------
@pytest.fixture(name="album_dir")
def album_dir_fixture(tmp_path: Path) -> Path:
    directory = tmp_path / "music" / "Nils Frahm" / "All Melody (2018)"
    for number, name in enumerate(FILES, start=1):
        write_flac(
            directory / name,
            seconds=200,
            album="All Melody",
            albumartist="Nils Frahm",
            artist="Nils Frahm",
            title=name.split(" - ", 1)[1].removesuffix(".flac"),
            tracknumber=str(number),
            date="2018",
        )
    return directory


@pytest.fixture(name="factory")
def factory_fixture(album_dir: Path) -> Iterator[Any]:
    """A scratch database plus a ``session_scope``-alike factory that commits."""
    engine = create_async_engine(
        "sqlite+aiosqlite://",
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm"))
            session.add(
                Album(
                    id=ALBUM_ID,
                    artist_id=ARTIST_ID,
                    title="All Melody",
                    status=AlbumStatus.DOWNLOADED,
                    tracks_count=len(FILES),
                    path=str(album_dir),
                )
            )
            for number, name in enumerate(FILES, start=1):
                session.add(
                    Track(
                        id=f"t{number}",
                        album_id=ALBUM_ID,
                        title=name.split(" - ", 1)[1].removesuffix(".flac"),
                        track_number=number,
                        media_number=1,
                        status=TrackStatus.DOWNLOADED,
                        path=str(album_dir / name),
                    )
                )
            await session.commit()

    asyncio.run(setup())

    @asynccontextmanager
    async def session_factory() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    try:
        yield session_factory
    finally:
        asyncio.run(engine.dispose())


@pytest.fixture(name="running_enricher")
def running_enricher_fixture(
    factory: Any, monkeypatch: pytest.MonkeyPatch
) -> Enricher:
    """An enricher reachable through ``app.core.state``, as in production.

    Re-opening a release goes looking for one there rather than being plumbed
    through the scheduler, so without this the interesting half of the pass is
    silently a no-op — which is exactly the bug these tests were written for.
    """
    enricher = Enricher(
        [FakeProvider(EnrichmentSource.DEEZER)],
        settings=Settings(
            qobuz_app_id="app",
            qobuz_user_auth_token="token",
            enrichment_sources="deezer",
        ),
        session_factory=factory,
    )
    monkeypatch.setattr(
        state_module, "_state", SimpleNamespace(enricher=enricher), raising=False
    )
    return enricher


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def run(factory: Any, **kwargs: Any) -> Any:
    return asyncio.run(verify_integrity(factory, **kwargs))


def tracks(factory: Any) -> dict[str, Track]:
    async def go() -> dict[str, Track]:
        async with factory() as session:
            rows = (await session.execute(select(Track))).scalars().all()
            return {str(row.id): row for row in rows}

    return asyncio.run(go())


def album(factory: Any) -> Album:
    async def go() -> Album:
        async with factory() as session:
            return await session.get(Album, ALBUM_ID)

    return asyncio.run(go())


def states(factory: Any) -> dict[tuple[str, str], EnrichmentState]:
    async def go() -> dict[tuple[str, str], EnrichmentState]:
        async with factory() as session:
            rows = (await session.execute(select(EnrichmentState))).scalars().all()
            return {(row.entity_id, row.source.value): row for row in rows}

    return asyncio.run(go())


def plant_identified(factory: Any) -> None:
    """The state of a release the ladder has already matched: every row ``ok``.

    Which is why re-opening cannot go through ``mark_due``: that moves
    ``pending``/``not_found``/``failed`` and nothing else, so on exactly these
    rows it does nothing while reporting that it did.
    """

    async def go() -> None:
        async with factory() as session:
            for entity_type, entity_id in (
                (EnrichmentEntity.ALBUM, ALBUM_ID),
                (EnrichmentEntity.ARTIST, ARTIST_ID),
            ):
                session.add(
                    EnrichmentState(
                        entity_type=entity_type,
                        entity_id=entity_id,
                        source=EnrichmentSource.DEEZER,
                        state="ok",
                        attempts=3,
                        last_error=None,
                    )
                )
            await session.commit()

    asyncio.run(go())


def retag(path: Path) -> None:
    """Somebody else's tagger: the bytes move, the audio does not."""
    audio = FLAC(str(path))
    audio["comment"] = ["ripped by somebody else"]
    audio.save()


def replace_audio(path: Path) -> None:
    """A re-rip: different audio under the same filename."""
    write_flac(path, seconds=100, album="All Melody", title="Sunson")


# ---------------------------------------------------------------------------
# UNKNOWN is not change
# ---------------------------------------------------------------------------
def test_a_library_nobody_has_measured_is_unknown_and_gets_a_baseline(
    factory: Any, album_dir: Path, running_enricher: Enricher
) -> None:
    report = run(factory)

    assert report.checked == len(FILES)
    assert report.count(IntegrityState.UNKNOWN) == len(FILES)
    assert report.baselined == len(FILES)
    assert report.changed == 0, "never measured is not tampered with"
    assert report.albums_reopened == 0
    assert states(factory) == {}, "nothing to re-identify — nothing was contradicted"

    rows = tracks(factory)
    for number, name in enumerate(FILES, start=1):
        row = rows[f"t{number}"]
        assert row.content_hash == hash_file(album_dir / name)
        assert row.sample_count == 200 * 44100
        assert row.verified_at is not None


def test_the_release_digest_is_written_from_the_files(factory: Any) -> None:
    report = run(factory)

    assert report.albums_restamped == 1
    rows = tracks(factory)
    expected = album_content_digest(
        [rows[f"t{number}"].content_hash for number in range(1, len(FILES) + 1)]
    )
    assert album(factory).content_digest == expected


def test_a_second_pass_over_an_untouched_library_verifies_and_rewrites_nothing(
    factory: Any,
) -> None:
    run(factory)
    before = album(factory).content_digest

    report = run(factory)

    assert report.count(IntegrityState.VERIFIED) == len(FILES)
    assert report.changed == 0
    assert report.albums_restamped == 0, "an unchanged digest is not rewritten"
    assert album(factory).content_digest == before


# ---------------------------------------------------------------------------
# RETAGGED keeps the identification; REPLACED throws it away
# ---------------------------------------------------------------------------
def test_a_retagged_file_is_re_stamped_and_nothing_is_re_identified(
    factory: Any, album_dir: Path, running_enricher: Enricher
) -> None:
    run(factory)
    plant_identified(factory)
    retag(album_dir / FILES[0])

    report = run(factory)

    assert report.count(IntegrityState.RETAGGED) == 1
    assert report.count(IntegrityState.VERIFIED) == len(FILES) - 1
    assert report.albums_reopened == 0
    assert tracks(factory)["t1"].content_hash == hash_file(album_dir / FILES[0])
    assert states(factory)[(ALBUM_ID, "deezer")].state == "ok", (
        "the audio is the same recording, so the match still holds"
    )


def test_a_replaced_file_puts_its_release_back_on_the_work_list(
    factory: Any, album_dir: Path, running_enricher: Enricher
) -> None:
    """The rows are all ``ok``, which is what an identified release looks like.

    ``mark_due`` moves none of them, so the obvious call reported a re-identify
    it had not performed — on precisely the albums this pass exists to catch.
    """
    run(factory)
    plant_identified(factory)
    replace_audio(album_dir / FILES[0])

    report = run(factory)

    assert report.count(IntegrityState.REPLACED) == 1
    assert report.albums_reopened == 1

    row = states(factory)[(ALBUM_ID, "deezer")]
    assert row.state == "pending"
    assert row.attempts == 0
    assert row.next_attempt_at is not None
    assert tracks(factory)["t1"].content_hash == hash_file(album_dir / FILES[0])


def test_a_replaced_file_in_a_release_that_is_not_in_the_library_is_not_reopened(
    factory: Any, album_dir: Path, running_enricher: Enricher
) -> None:
    """Scope still decides. A release the verify pass has demoted is not on disk
    as far as enrichment is concerned, and giving it work rows only makes the
    nightly purge delete them again."""
    run(factory)
    replace_audio(album_dir / FILES[0])

    async def demote() -> None:
        async with factory() as session:
            found = await session.get(Album, ALBUM_ID)
            found.status = AlbumStatus.WANTED
            await session.commit()

    asyncio.run(demote())

    report = run(factory)

    assert report.count(IntegrityState.REPLACED) == 1
    assert report.albums_reopened == 0
    assert states(factory) == {}


# ---------------------------------------------------------------------------
# MISSING keeps what it knows
# ---------------------------------------------------------------------------
def test_a_file_that_is_gone_keeps_its_baseline(
    factory: Any, album_dir: Path, running_enricher: Enricher
) -> None:
    """An unmounted share is what this looks like, and demoting is the *other*
    job — at the album level, where it reverses when the mount comes back."""
    run(factory)
    recorded = tracks(factory)["t1"].content_hash
    (album_dir / FILES[0]).unlink()

    report = run(factory)

    assert report.count(IntegrityState.MISSING) == 1
    assert report.albums_reopened == 0
    assert tracks(factory)["t1"].content_hash == recorded


# ---------------------------------------------------------------------------
# The dry run, and the rotation
# ---------------------------------------------------------------------------
def test_a_dry_run_classifies_the_same_and_writes_nothing(
    factory: Any, album_dir: Path
) -> None:
    run(factory)
    replace_audio(album_dir / FILES[0])
    before = tracks(factory)["t1"].content_hash

    report = run(factory, apply=False)

    assert report.applied is False
    assert report.count(IntegrityState.REPLACED) == 1
    assert report.baselined == 0
    assert tracks(factory)["t1"].content_hash == before
    assert states(factory) == {}


def test_the_rotation_reaches_the_never_verified_files_first(factory: Any) -> None:
    """What makes a nightly slice cover the library rather than the same files
    every night: never-verified rows sort to the front, then the oldest."""
    first = run(factory, limit=2)
    assert first.checked == 2

    measured = {
        track_id
        for track_id, row in tracks(factory).items()
        if row.verified_at is not None
    }
    assert len(measured) == 2

    second = run(factory, limit=1)

    assert second.checked == 1
    assert second.count(IntegrityState.UNKNOWN) == 1, "the one nothing had measured"
    assert all(row.verified_at is not None for row in tracks(factory).values())


def test_unknown_only_never_re_measures_what_is_already_baselined(
    factory: Any, album_dir: Path
) -> None:
    """"Baseline the library" converges and cannot overwrite a measurement."""
    run(factory, limit=1)
    replace_audio(album_dir / FILES[0])

    report = run(factory, unknown_only=True)

    assert report.checked == len(FILES) - 1
    assert report.count(IntegrityState.REPLACED) == 0, "the replaced row was skipped"


# ---------------------------------------------------------------------------
# The two writers of ``content_digest`` must agree
# ---------------------------------------------------------------------------
def test_the_scan_and_the_integrity_pass_order_the_digest_the_same_way(
    factory: Any,
) -> None:
    """They write the same column from the same member hashes. If they disagreed
    about the order, the digest would flip every time the other one ran and the
    release would report as altered forever."""
    run(factory)
    from_pass = album(factory).content_digest

    async def restamp_as_the_scanner_would() -> str | None:
        async with factory() as session:
            found = await session.get(Album, ALBUM_ID)
            found.content_digest = None
            LibraryScanner._stamp_release(found)  # noqa: SLF001 - that is the subject
            await session.commit()
            return found.content_digest

    assert asyncio.run(restamp_as_the_scanner_would()) == from_pass


# ---------------------------------------------------------------------------
# The nightly wrapper
# ---------------------------------------------------------------------------
def reverify(factory: Any, summary: dict[str, Any]) -> dict[str, Any]:
    asyncio.run(_reverify_files(factory, summary))
    return summary


def test_the_nightly_slice_stands_down_when_the_verify_pass_demoted_anything(
    factory: Any, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Albums flipped back to ``WANTED`` is what an unreachable library looks
    like, and hashing across a flapping mount spends the whole night's budget
    producing ``MISSING`` verdicts about files that are fine."""
    monkeypatch.setattr(
        scheduler_module,
        "get_settings",
        lambda: Settings(
            qobuz_app_id="app",
            qobuz_user_auth_token="token",
            integrity_enabled=True,
            integrity_reverify_fraction=1.0,
        ),
    )

    summary = reverify(factory, {"albums_flagged": 2})

    assert summary["integrity_skipped"] == 2
    assert "integrity" not in summary
    assert all(row.verified_at is None for row in tracks(factory).values())


def test_the_nightly_slice_runs_when_nothing_was_demoted(
    factory: Any, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(
        scheduler_module,
        "get_settings",
        lambda: Settings(
            qobuz_app_id="app",
            qobuz_user_auth_token="token",
            integrity_enabled=True,
            integrity_reverify_fraction=1.0,
        ),
    )

    summary = reverify(factory, {"albums_flagged": 0})

    assert summary["files_verified"] == len(FILES)
    assert summary["files_changed"] == 0
    assert summary["integrity"]["unknown"] == len(FILES)


def test_the_nightly_slice_is_off_when_integrity_is_disabled(
    factory: Any, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(
        scheduler_module,
        "get_settings",
        lambda: Settings(
            qobuz_app_id="app",
            qobuz_user_auth_token="token",
            integrity_enabled=False,
        ),
    )

    summary = reverify(factory, {"albums_flagged": 0})

    assert "integrity" not in summary
    assert all(row.verified_at is None for row in tracks(factory).values())
