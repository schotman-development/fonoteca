"""Binding a folder the scan could not name to the Qobuz release it holds.

The scan matches a directory by normalising its album title, which cannot bridge
a folder whose name is a *different string* for the same record. On one real
library that left 104 of 611 folders unmatched, and the confirmed failures were
all subtitles — ``Play: The Guitar Album`` against a catalogue calling it
``Play``. Every one is a release the user owns, sitting in the *wanted* list,
queued to be downloaded again.

Two halves, tested apart because they are deliberately separate:

* :mod:`app.core.binder` decides, using the audio, and writes **only** a binding.
* :class:`~app.core.scanner.LibraryScanner` consults ``folder_bindings`` before
  it normalises anything, and adopts through the same one-way path every other
  folder takes.

The property most of these defend is that a binding is *exact*. The chain lets a
name narrow a Qobuz search and never choose within it, so two candidates is
ambiguous rather than a coin toss — which matters more here than in enrichment,
because a wrong binding does not mistag a release, it declares the user owns
something they do not.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any, Iterator

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

from app.config import Settings, get_settings
from app.core.binder import bind_unmatched_folders, clear_folder, mark_folder
from app.core.discovery import FolderIdentity, Outcome
from app.core.scanner import LibraryScanner
from app.enrich.chromaprint import FpcalcMissing
from app.models import Album, AlbumStatus, Artist, Base, FolderBinding

from tests.test_library_scan import write_album

#: The folder on disk, and the catalogue row that really is the same record.
FOLDER = "Spaces: The Live Album (2018)"
ALBUM_ID = "a2"


@pytest.fixture(name="library")
def library_fixture(tmp_path: Path) -> Path:
    root = tmp_path / "music"
    # Matches "All Melody" by name — the control.
    write_album(root, "Nils Frahm", "All Melody (2018)", tracks=4)
    # Does not: the catalogue calls this one "Spaces".
    write_album(root, "Nils Frahm", FOLDER, tracks=3, album="Spaces: The Live Album")
    return root


@pytest.fixture(name="settings")
def settings_fixture(library: Path) -> Settings:
    return get_settings().model_copy(update={"library_path": library})


@pytest.fixture(name="maker")
def maker_fixture(tmp_path: Path) -> Iterator[async_sessionmaker[AsyncSession]]:
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'bind.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id="100", name="Nils Frahm"))
            session.add(
                Album(
                    id="a1",
                    artist_id="100",
                    title="All Melody",
                    tracks_count=4,
                    status=AlbumStatus.WANTED,
                )
            )
            session.add(
                Album(
                    id=ALBUM_ID,
                    artist_id="100",
                    title="Spaces",
                    tracks_count=3,
                    status=AlbumStatus.WANTED,
                )
            )
            await session.commit()

    asyncio.run(seed())
    try:
        yield maker
    finally:
        asyncio.run(engine.dispose())


def scan(maker: Any, settings: Settings, **kwargs: Any) -> Any:
    async def run() -> Any:
        async with maker() as session:
            return await LibraryScanner(settings=settings).scan(session, **kwargs)

    return asyncio.run(run())


def album(maker: Any, album_id: str) -> Album:
    async def run() -> Album:
        async with maker() as session:
            return await session.get(Album, album_id)

    return asyncio.run(run())


def add_binding(maker: Any, path: Path, album_id: str, **fields: Any) -> None:
    async def run() -> None:
        async with maker() as session:
            session.add(FolderBinding(path=str(path), album_id=album_id, **fields))
            await session.commit()

    asyncio.run(run())


def bindings(maker: Any) -> dict[str, FolderBinding]:
    async def run() -> dict[str, FolderBinding]:
        async with maker() as session:
            rows = (await session.execute(select(FolderBinding))).scalars().all()
            return {row.path: row for row in rows}

    return asyncio.run(run())


def identifier(**by_folder: Any) -> Any:
    """An ``identify`` callable answering from a table keyed on folder name."""

    async def identify(scanned: Any) -> Any:
        answer = by_folder.get(Path(str(scanned.directory)).name)
        if isinstance(answer, BaseException):
            raise answer
        return answer or FolderIdentity(
            directory=str(scanned.directory),
            folder_artist="",
            reason="nothing matched",
        )

    return identify


def identified(directory: str, album_id: str = ALBUM_ID) -> FolderIdentity:
    return FolderIdentity(
        directory=directory,
        folder_artist="Nils Frahm",
        outcome=Outcome.IDENTIFIED,
        reason="matched on barcode 0884385226442",
        qobuz_album_id=album_id,
        qobuz_artist_id="100",
        qobuz_artist_name="Nils Frahm",
        mb_release_group_mbid="45017130-1af1-3e2f-bcbd-8c1d0ba4c3f2",
        barcodes=("0884385226442",),
    )


def bind(maker: Any, settings: Settings, identify: Any, **kwargs: Any) -> Any:
    async def run() -> Any:
        async with maker() as session:
            return await bind_unmatched_folders(
                session, identify=identify, settings=settings, **kwargs
            )

    return asyncio.run(run())


# ---------------------------------------------------------------------------
# The scanner half
# ---------------------------------------------------------------------------
def test_the_folder_is_unmatched_without_a_binding(maker: Any, settings: Settings) -> None:
    """The state this exists to fix: a release on disk, listed as missing."""
    result = scan(maker, settings)

    assert [Path(entry["path"]).name for entry in result.unmatched] == [FOLDER]
    assert album(maker, ALBUM_ID).status is AlbumStatus.WANTED


def test_a_binding_adopts_the_folder(maker: Any, settings: Settings) -> None:
    add_binding(maker, settings.library_path / "Nils Frahm" / FOLDER, ALBUM_ID)

    result = scan(maker, settings)

    assert result.unmatched == []
    adopted = album(maker, ALBUM_ID)
    assert adopted.status is AlbumStatus.DOWNLOADED
    assert Path(adopted.path).name == FOLDER


def test_adoption_goes_through_the_ordinary_path(maker: Any, settings: Settings) -> None:
    """Track rows and all — a binding supplies identity, not a shortcut past ``_adopt``."""
    add_binding(maker, settings.library_path / "Nils Frahm" / FOLDER, ALBUM_ID)
    scan(maker, settings)

    async def read() -> list[Any]:
        async with maker() as session:
            from app.models import Track

            rows = await session.execute(select(Track).where(Track.album_id == ALBUM_ID))
            return list(rows.scalars().all())

    tracks = asyncio.run(read())
    assert len(tracks) == 3
    assert all(track.path for track in tracks)


def test_a_binding_naming_an_absent_album_is_ignored(
    maker: Any, settings: Settings
) -> None:
    """The table has no foreign key on purpose; a dangling row must not raise."""
    add_binding(maker, settings.library_path / "Nils Frahm" / FOLDER, "gone")

    result = scan(maker, settings)

    assert [Path(entry["path"]).name for entry in result.unmatched] == [FOLDER]
    assert album(maker, ALBUM_ID).status is AlbumStatus.WANTED


def test_a_binding_does_not_disturb_folders_that_match_by_name(
    maker: Any, settings: Settings
) -> None:
    add_binding(maker, settings.library_path / "Nils Frahm" / FOLDER, ALBUM_ID)
    scan(maker, settings)
    assert album(maker, "a1").status is AlbumStatus.DOWNLOADED


def test_a_binding_still_only_moves_towards_downloaded(
    maker: Any, settings: Settings
) -> None:
    """The scan's one-way rule is not weakened by supplying an identity."""

    async def make_it_busy() -> None:
        async with maker() as session:
            row = await session.get(Album, ALBUM_ID)
            row.status = AlbumStatus.DOWNLOADING
            await session.commit()

    asyncio.run(make_it_busy())
    add_binding(maker, settings.library_path / "Nils Frahm" / FOLDER, ALBUM_ID)

    scan(maker, settings)

    assert album(maker, ALBUM_ID).status is AlbumStatus.DOWNLOADING


def test_the_snapshot_carries_the_artist_off_a_real_scanned_album(
    settings: Settings,
) -> None:
    """``folder_snapshot`` must read the class the scanner actually produces.

    It used to read ``scanned.artist``, which :class:`ScannedAlbum` does not
    have — it carries ``artist_candidates`` and computes ``artist_name`` — so the
    artist silently became ``""`` and :func:`search_terms` sent Qobuz a bare album
    title. Measured against the live catalogue, ``'Play: The Guitar Album'``
    returned *Tárrega: Guitar Edition* and a lullaby compilation; Brad Paisley's
    record was not among the two results at all.

    ``tests/test_discovery.py`` could not catch this: its ``FakeScanned`` is
    shaped like what the code expected rather than like the real class, so the
    fake had the attribute the genuine object lacks. Hence a test built on
    ``collect_albums`` output, which is the only shape production ever passes in.

    The name is for the *report* — ``FolderIdentity.folder_artist``, "reported so
    a human can read the row; never matched on". Finding this bug is what forced
    the sharper question of whether the folder should reach the Qobuz query at
    all, and the answer was no: see
    ``test_the_query_comes_from_musicbrainz_and_never_from_the_folder``.
    """
    from app.core.discovery import folder_snapshot
    from app.core.scanner import collect_albums

    scanned = collect_albums(settings.library_path / "Nils Frahm" / FOLDER)[0][0]
    snapshot = folder_snapshot(scanned)

    assert snapshot.artist_name == "Nils Frahm"


# ---------------------------------------------------------------------------
# "There is no such release" — the decision only a person can make
# ---------------------------------------------------------------------------
def mark(maker: Any, path: Any, **kwargs: Any) -> Any:
    async def run() -> Any:
        async with maker() as session:
            return await mark_folder(session, str(path), **kwargs)

    return asyncio.run(run())


def test_marking_a_folder_takes_it_out_of_the_unmatched_count(
    maker: Any, settings: Settings
) -> None:
    """The point of the marker: a count that can never fall is one nobody reads."""
    directory = settings.library_path / "Nils Frahm" / FOLDER
    assert scan(maker, settings).unmatched_count == 1

    mark(maker, directory, note="covers, never released anywhere")

    result = scan(maker, settings)
    assert result.unmatched_count == 0
    assert result.albums_excluded == 1


def test_a_marked_folder_is_still_reported_not_hidden(
    maker: Any, settings: Settings
) -> None:
    """A folder absent from every figure is one the scan appears never to have seen."""
    mark(maker, settings.library_path / "Nils Frahm" / FOLDER)
    result = scan(maker, settings)

    assert result.albums_excluded == 1
    assert "marked not on Qobuz" in result.summary()
    assert result.as_dict()["albums_excluded"] == 1


def test_a_marked_folder_adopts_nothing(maker: Any, settings: Settings) -> None:
    """It says there is no release, so no album may move on account of it."""
    mark(maker, settings.library_path / "Nils Frahm" / FOLDER)
    scan(maker, settings)
    assert album(maker, ALBUM_ID).status is AlbumStatus.WANTED


def test_the_marker_stops_the_binder_spending_requests(
    maker: Any, settings: Settings
) -> None:
    """Otherwise the chain re-derives the same "no" on every run, forever."""
    mark(maker, settings.library_path / "Nils Frahm" / FOLDER)

    calls: list[str] = []

    async def identify(scanned: Any) -> Any:
        calls.append(str(scanned.directory))
        return identified(str(scanned.directory))

    result = bind(maker, settings, identify)

    assert calls == []
    assert result.bound == 0


def test_the_marker_is_reversible(maker: Any, settings: Settings) -> None:
    """The way back is what makes it safe to apply liberally."""
    directory = settings.library_path / "Nils Frahm" / FOLDER
    mark(maker, directory)
    assert scan(maker, settings).unmatched_count == 0

    async def undo() -> bool:
        async with maker() as session:
            return await clear_folder(session, str(directory))

    assert asyncio.run(undo()) is True
    assert scan(maker, settings).unmatched_count == 1


def test_marking_is_always_recorded_as_a_persons_decision(
    maker: Any, settings: Settings
) -> None:
    """``manual`` is what protects it from the next automatic pass."""
    directory = settings.library_path / "Nils Frahm" / FOLDER
    row = mark(maker, directory, note="YouTube covers")

    assert (row.state, row.album_id, row.method) == ("not_in_catalogue", None, "manual")
    assert row.note == "YouTube covers"


def test_no_automatic_path_can_mark_a_folder_not_in_catalogue(
    maker: Any, settings: Settings
) -> None:
    """The invariant: failing to find a release is not evidence that none exists.

    A whole binding pass over a folder nothing can identify must leave the table
    empty, so the marker's presence in a row is proof a person put it there.
    """
    result = bind(maker, settings, identifier())  # answers "nothing matched"

    assert result.unidentified == 1
    assert bindings(maker) == {}


def test_a_person_can_bind_a_folder_by_hand(maker: Any, settings: Settings) -> None:
    """The other half: the audio need not be the only route to a binding."""
    directory = settings.library_path / "Nils Frahm" / FOLDER
    mark(maker, directory, album_id=ALBUM_ID)

    scan(maker, settings)
    assert album(maker, ALBUM_ID).status is AlbumStatus.DOWNLOADED


def test_binding_by_hand_to_an_album_that_is_not_there_is_refused(
    maker: Any, settings: Settings
) -> None:
    """A row pointing at nothing is worse than no row; the artist needs following."""
    with pytest.raises(LookupError, match="follow the artist first"):
        mark(maker, settings.library_path / "Nils Frahm" / FOLDER, album_id="ghost")

    assert bindings(maker) == {}


def test_marking_over_a_binding_drops_its_evidence(
    maker: Any, settings: Settings
) -> None:
    """A barcode beside "no such release" reads as a contradiction later."""
    directory = settings.library_path / "Nils Frahm" / FOLDER
    add_binding(maker, directory, ALBUM_ID, barcode="0884385226442")

    row = mark(maker, directory)

    assert (row.barcode, row.mb_release_group_mbid) == (None, None)


# ---------------------------------------------------------------------------
# The binder half
# ---------------------------------------------------------------------------
def test_it_binds_the_folder_the_scan_could_not_name(
    maker: Any, settings: Settings
) -> None:
    directory = str(settings.library_path / "Nils Frahm" / FOLDER)
    result = bind(maker, settings, identifier(**{FOLDER: identified(directory)}))

    assert (result.unmatched, result.bound) == (1, 1)
    row = bindings(maker)[directory]
    assert (row.album_id, row.method) == (ALBUM_ID, "audio-barcode")
    assert row.barcode == "0884385226442"


def test_binding_then_scanning_takes_it_off_the_wanted_list(
    maker: Any, settings: Settings
) -> None:
    """The end-to-end claim: the two halves together are what fix the report."""
    directory = str(settings.library_path / "Nils Frahm" / FOLDER)
    bind(maker, settings, identifier(**{FOLDER: identified(directory)}))

    assert album(maker, ALBUM_ID).status is AlbumStatus.WANTED  # binder moved nothing
    scan(maker, settings)
    assert album(maker, ALBUM_ID).status is AlbumStatus.DOWNLOADED


def test_an_ambiguous_folder_is_reported_and_not_bound(
    maker: Any, settings: Settings
) -> None:
    """Two Qobuz editions carrying a barcode from the group is a person's decision."""
    directory = str(settings.library_path / "Nils Frahm" / FOLDER)
    answer = FolderIdentity(
        directory=directory,
        folder_artist="Nils Frahm",
        outcome=Outcome.AMBIGUOUS,
        reason="2 Qobuz albums carry a barcode from this release group",
    )
    result = bind(maker, settings, identifier(**{FOLDER: answer}))

    assert (result.bound, result.ambiguous) == (0, 1)
    assert bindings(maker) == {}


def test_an_unidentified_folder_writes_nothing(maker: Any, settings: Settings) -> None:
    result = bind(maker, settings, identifier())
    assert (result.bound, result.unidentified) == (0, 1)
    assert bindings(maker) == {}


def test_an_album_nobody_follows_is_reported_rather_than_bound(
    maker: Any, settings: Settings
) -> None:
    """The chain answers from Qobuz's whole catalogue; ``albums`` is narrower."""
    directory = str(settings.library_path / "Nils Frahm" / FOLDER)
    result = bind(
        maker, settings, identifier(**{FOLDER: identified(directory, album_id="nope")})
    )

    assert (result.bound, result.unidentified) == (0, 1)
    assert bindings(maker) == {}
    assert "not followed" in result.unresolved[0]["outcome"]


def test_a_release_already_adopted_elsewhere_is_a_duplicate(
    maker: Any, settings: Settings
) -> None:
    """Binding it would re-point every library operation at the other directory."""

    async def adopt_elsewhere() -> None:
        async with maker() as session:
            row = await session.get(Album, ALBUM_ID)
            row.status = AlbumStatus.DOWNLOADED
            row.path = "/elsewhere/Spaces"
            await session.commit()

    asyncio.run(adopt_elsewhere())
    directory = str(settings.library_path / "Nils Frahm" / FOLDER)

    result = bind(maker, settings, identifier(**{FOLDER: identified(directory)}))

    assert (result.bound, result.duplicates) == (0, 1)
    assert bindings(maker) == {}
    assert "/elsewhere/Spaces" in result.unresolved[0]["reason"]


def test_a_dry_run_identifies_and_writes_nothing(maker: Any, settings: Settings) -> None:
    directory = str(settings.library_path / "Nils Frahm" / FOLDER)
    result = bind(
        maker, settings, identifier(**{FOLDER: identified(directory)}), dry_run=True
    )

    assert result.bound == 1
    assert bindings(maker) == {}


def test_a_missing_fpcalc_gates_the_pass_rather_than_one_folder(
    maker: Any, settings: Settings
) -> None:
    result = bind(maker, settings, identifier(**{FOLDER: FpcalcMissing("no fpcalc")}))

    assert result.gated is True
    assert result.bound == 0
    assert bindings(maker) == {}


def test_no_chain_configured_is_a_gated_answer_not_a_crash(
    maker: Any, settings: Settings
) -> None:
    """``identifier_for`` returns ``None`` without an AcoustID key. That is normal."""
    result = bind(maker, settings, None)

    assert result.gated is True
    assert result.unmatched == 1
    assert "ACOUSTID_API_KEY" in result.errors[0]


def test_a_working_binding_never_reaches_this_pass_at_all(
    maker: Any, settings: Settings
) -> None:
    """The cheapest possible skip: the scan matched it, so it is not unmatched."""
    directory = settings.library_path / "Nils Frahm" / FOLDER
    add_binding(maker, directory, ALBUM_ID)

    calls: list[str] = []

    async def identify(scanned: Any) -> Any:
        calls.append(str(scanned.directory))
        return identified(str(scanned.directory))

    result = bind(maker, settings, identify)

    assert calls == []
    assert (result.unmatched, result.examined) == (0, 0)


def test_a_broken_binding_is_re_derived(maker: Any, settings: Settings) -> None:
    """A binding naming an album the indexer dropped leaves the folder unmatched.

    Those are the only bound folders that arrive here, and re-deriving them is
    the point — the audio has not changed, but the catalogue row it named has.
    """
    directory = settings.library_path / "Nils Frahm" / FOLDER
    add_binding(maker, directory, "gone")

    result = bind(maker, settings, identifier(**{FOLDER: identified(str(directory))}))

    assert (result.unmatched, result.bound) == (1, 1)
    assert bindings(maker)[str(directory)].album_id == ALBUM_ID


def test_a_manual_binding_is_never_overruled(maker: Any, settings: Settings) -> None:
    """Same rule as ``_MANUAL_OWNS``: only a person overrules a person.

    Pinned even though it is broken — which is the hard case, since the automatic
    chain has a working answer and is still not allowed to apply it.
    """
    directory = settings.library_path / "Nils Frahm" / FOLDER
    add_binding(maker, directory, "gone", method="manual")

    calls: list[str] = []

    async def identify(scanned: Any) -> Any:
        calls.append(str(scanned.directory))
        return identified(str(scanned.directory))

    result = bind(maker, settings, identify)

    assert calls == []
    assert (result.manual, result.bound) == (1, 0)
    assert bindings(maker)[str(directory)].album_id == "gone"


def test_the_limit_caps_the_requests(maker: Any, settings: Settings) -> None:
    calls: list[str] = []

    async def identify(scanned: Any) -> Any:
        calls.append(str(scanned.directory))
        return identified(str(scanned.directory))

    result = bind(maker, settings, identify, limit=0)

    assert calls == []
    assert result.unmatched == 1
