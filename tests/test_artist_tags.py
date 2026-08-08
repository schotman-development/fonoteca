"""Editing an artist's identity by hand, and pushing it onto the files.

Two endpoints, and the whole point of splitting them is that one writes the
database and the other writes the library:

* ``PATCH /api/artists/{id}/tags`` — name, sort name, aliases, MusicBrainz id.
* ``POST  /api/artists/{id}/retag`` — re-tag every release of theirs on disk,
  write the NFOs, and (only when asked) rename the folders.

The tests lean on the refusals, because every field on that form is one a
careless implementation would quietly lose:

* an **unknown key** is a 422, not a silent discard. That refusal is what the
  *aliases* box was measured against: it was a 422 until the field earned a
  column on ``artists``, because dropping the list somebody typed is the
  outcome worse than not offering the field at all.
* **aliases round-trip losslessly** — a comma inside one stays inside it, an
  empty list clears, an omitted key leaves alone — and they reach **no file**:
  no tag, no NFO element, and nothing in ``app/enrich/matching.py``.
* a **blank name** is a 400 — it names the folder every release sits in.
* a **blank MusicBrainz id** is a 400, not a clear: an empty box is far more
  often one nobody filled in.
* a **bad MusicBrainz id** rolls the *whole* edit back, name included.
* the id is recorded as a **manual** match, so ``_MANUAL_OWNS`` stops the next
  automatic derivation putting a different one back. A field silently
  re-derived next tick is worse than no field: this one is written into every
  file as ``MUSICBRAINZ_ARTISTID``, and Picard, beets and Roon then believe it.
* the typed **sort name outranks** MusicBrainz's, and lives on ``artists``
  rather than in ``artist_metadata`` — the table ``ENRICHMENT_SCHEMA_VERSION``
  drops and rebuilds.
* ``freeze_path`` is honoured **and reported**, a **busy** release is blocked
  rather than attempted, and the pass **queues nothing**.

Everything runs against a scratch ``LIBRARY_PATH`` and ``DATA_PATH``. The
fixture asserts the redirection took before any test body runs, and the session
override deliberately **does not commit** — a committing fixture would hide a
route that forgot to, which is exactly the bug ``identify`` once shipped.
"""

from __future__ import annotations

import asyncio
import sqlite3
from datetime import date
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import NullPool

import main
from app import config
from app.api import routes_api
from app.config import Settings, get_settings
from app.core import librarian
from app.core.enricher import Enricher
from app.db import get_session
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    ArtistMetadata,
    Base,
    QueueItem,
    QueueState,
    Track,
    TrackStatus,
    parse_aliases,
)
from scripts import migrate_artist_tags

ARTIST_ID = "720076"
ON_DISK = "downloaded0001"
MISFILED = "misfiled000002"
BUSY = "beingfetched03"

#: An artist with **no** ``artist_metadata`` row, which is what most artists
#: are: enrichment is scoped to the library, so a followed artist with nothing
#: on disk is never enriched. Seeded per test rather than in the fixture,
#: because it exists to pin one read-model placement.
UNENRICHED = "990001"

MBID = "984f8239-8fe1-4683-9c54-10ffb14439e9"
OTHER_MBID = "45017130-d783-4a09-97e1-780a3fd40382"

TRACKS = ("01 - Sunson.flac", "02 - My Friend The Forest.flac")

#: What the default naming template renders for these rows. ``MISFILED`` is
#: deliberately not one of them — it is the folder a re-file exists to move.
FOLDERS = {
    ON_DISK: "All Melody (2018) [FLAC 24-96]",
    MISFILED: "spaces-old-import",
    BUSY: "Felt (2011) [FLAC 24-96]",
}


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
@pytest.fixture(name="settings")
def settings_fixture(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[Settings]:
    """Point LIBRARY_PATH and DATA_PATH at scratch directories, process-wide.

    The assertion is not decoration: without it a silent failure here would run
    file-writing tests against a real music collection.
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
    try:
        yield conf
    finally:
        config.get_settings.cache_clear()


def artist_dir(settings: Settings) -> Path:
    return settings.library_path / "Nils Frahm"


def make_album_files(settings: Settings, name: str) -> Path:
    """An album folder with two tracks and a cover, as the downloader leaves it."""
    directory = artist_dir(settings) / name
    directory.mkdir(parents=True, exist_ok=True)
    for track in TRACKS:
        (directory / track).write_bytes(b"audio-" + track.encode())
    (directory / "cover.jpg").write_bytes(b"\xff\xd8\xff")
    return directory


#: The session maker of the live fixture, so a test can read or write state the
#: API offers no route for — a ``freeze_path`` flag, or what ``_upsert`` does to
#: a manually identified row.
_MAKERS: list[Any] = []


@pytest.fixture(name="client")
def client_fixture(tmp_path: Path, settings: Settings) -> Iterator[TestClient]:
    """One artist, three releases on disk, one of them being downloaded."""
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'tags.db'}", poolclass=NullPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def override_session() -> AsyncIterator[AsyncSession]:
        """Exactly what ``app.db.get_session`` does — and no commit.

        ``update_artist_tags`` commits itself, sometimes through
        ``Enricher.identify``. A fixture that committed for it would make the
        tests pass whether or not it did.
        """
        session = maker()
        try:
            yield session
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    async def seed() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id=ARTIST_ID, name="Nils Frahm", monitored=True))
            # MusicBrainz's sort name, so the coalesce has something to lose to.
            session.add(
                ArtistMetadata(artist_id=ARTIST_ID, sort_name="Frahm, Nils (derived)")
            )
            for album_id, released, title in (
                (ON_DISK, date(2018, 1, 26), "All Melody"),
                (MISFILED, date(2013, 11, 15), "Spaces"),
                (BUSY, date(2011, 10, 14), "Felt"),
            ):
                directory = make_album_files(settings, FOLDERS[album_id])
                session.add(
                    Album(
                        id=album_id,
                        artist_id=ARTIST_ID,
                        title=title,
                        status=AlbumStatus.DOWNLOADED,
                        monitored=True,
                        release_date=released,
                        release_type="album",
                        tracks_count=2,
                        media_count=1,
                        hires=True,
                        max_bit_depth=24,
                        max_sampling_rate=96.0,
                        path=str(directory),
                    )
                )
                for number, name in enumerate(TRACKS, start=1):
                    session.add(
                        Track(
                            id=f"{album_id}-{number}",
                            album_id=album_id,
                            title=name.split(" - ", 1)[1].removesuffix(".flac"),
                            track_number=number,
                            status=TrackStatus.DOWNLOADED,
                            path=str(directory / name),
                            format_id=7,
                            bit_depth=24,
                            sampling_rate=96.0,
                            file_size=12,
                        )
                    )
            session.add(QueueItem(album_id=BUSY, state=QueueState.ACTIVE))
            await session.commit()

    asyncio.run(seed())
    _MAKERS.append(maker)
    main.app.dependency_overrides[get_session] = override_session
    try:
        yield TestClient(main.app)
    finally:
        # No engine.dispose(): NullPool closes each connection as it is
        # released, inside the loop that opened it.
        main.app.dependency_overrides.pop(get_session, None)
        _MAKERS.pop()


@pytest.fixture(name="enricher")
def enricher_fixture(monkeypatch: pytest.MonkeyPatch) -> Enricher:
    """A wired-up enricher with no providers — enough for ``identify``."""
    instance = Enricher([], settings=get_settings())
    monkeypatch.setattr(routes_api, "get_enricher", lambda: instance)
    monkeypatch.setattr("app.api.deps.get_enricher", lambda: instance)
    return instance


@pytest.fixture(name="tagged")
def tagged_fixture(monkeypatch: pytest.MonkeyPatch) -> list[str]:
    """Stand in for mutagen. The scratch ``.flac`` files are not real FLAC.

    What is under test here is which releases get re-tagged and what the pass
    reports, not whether mutagen can write a Vorbis comment — that is
    ``tests/test_tagger.py``'s job. Returns the paths that were written.
    """
    seen: list[str] = []
    monkeypatch.setattr(
        librarian, "tag_file", lambda path, *a, **k: seen.append(str(path)) or True
    )
    return seen


def run(coro_factory: Any) -> Any:
    """Run one coroutine against the fixture's own session maker."""

    async def go() -> Any:
        async with _MAKERS[-1]() as session:
            return await coro_factory(session)

    return asyncio.run(go())


def artist_json(client: TestClient) -> dict:
    return client.get(f"/api/artists/{ARTIST_ID}").json()


def seed_unenriched_artist() -> None:
    """One followed artist with no ``artist_metadata`` row and nothing on disk."""

    async def go(session: AsyncSession) -> None:
        session.add(Artist(id=UNENRICHED, name="Nonkeen", monitored=True))
        await session.commit()

    run(go)


def set_freeze(album_id: str) -> None:
    """Freeze one release's path — a switch only ``PATCH /api/albums`` offers."""

    async def go(session: AsyncSession) -> None:
        album = await session.get(Album, album_id)
        album.freeze_path = True
        await session.commit()

    run(go)


# ---------------------------------------------------------------------------
# Saving — what the drawer writes
# ---------------------------------------------------------------------------
def test_a_saved_name_survives_the_request_that_made_it(client: TestClient) -> None:
    """The route runs on the request-scoped session, which never commits."""
    response = client.patch(
        f"/api/artists/{ARTIST_ID}/tags", json={"name": "Nils Frahm & Ólafur"}
    )
    assert response.status_code == 200
    # A different request, so a different session: nothing is being read out of
    # the identity map that wrote it.
    assert artist_json(client)["name"] == "Nils Frahm & Ólafur"


def test_a_typed_sort_name_outranks_the_derived_one(client: TestClient) -> None:
    """MusicBrainz rewrites its own ``sort_name`` on every pass. This one wins."""
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"sort_name": "Frahm, Nils"})
    assert artist_json(client)["sort_name"] == "Frahm, Nils"


def test_the_typed_sort_name_is_not_stored_in_the_enrichment_table(
    client: TestClient,
) -> None:
    """``artist_metadata`` is dropped and rebuilt whenever the enrichment schema
    version moves. A sentence somebody typed cannot live there."""
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"sort_name": "Frahm, Nils"})

    async def go(session: AsyncSession) -> tuple[Any, Any]:
        artist = await session.get(Artist, ARTIST_ID)
        meta = await session.get(ArtistMetadata, ARTIST_ID)
        return artist.sort_name, meta.sort_name

    typed, derived = run(go)
    assert typed == "Frahm, Nils"
    assert derived == "Frahm, Nils (derived)", "the derived value was overwritten"


def test_clearing_the_sort_name_hands_the_answer_back_to_musicbrainz(
    client: TestClient,
) -> None:
    """An empty string is a real value here — it is the only way to un-decide."""
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"sort_name": "Frahm, Nils"})
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"sort_name": ""})
    assert artist_json(client)["sort_name"] == "Frahm, Nils (derived)"

    async def go(session: AsyncSession) -> Any:
        return (await session.get(Artist, ARTIST_ID)).sort_name

    assert run(go) is None, "stored blank rather than 'nobody has said'"


def test_an_omitted_field_is_left_alone(client: TestClient) -> None:
    """Partial updates mean partial, here as everywhere else."""
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"sort_name": "Frahm, Nils"})
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"name": "Nils Frahm"})
    body = artist_json(client)
    assert body["sort_name"] == "Frahm, Nils"
    assert body["name"] == "Nils Frahm"


def test_saving_is_logged(client: TestClient) -> None:
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"name": "Nils F."})
    events = [row["event"] for row in client.get("/api/activity").json()["items"]]
    assert "artist.tags" in events


# ---------------------------------------------------------------------------
# The MusicBrainz id — a manual override of a derived value
# ---------------------------------------------------------------------------
def test_a_typed_musicbrainz_id_is_recorded(
    client: TestClient, enricher: Enricher
) -> None:
    response = client.patch(
        f"/api/artists/{ARTIST_ID}/tags", json={"mb_artist_mbid": MBID}
    )
    assert response.status_code == 200
    assert artist_json(client)["mb_artist_mbid"] == MBID


def test_a_pasted_musicbrainz_link_is_an_identifier(
    client: TestClient, enricher: Enricher
) -> None:
    """A browser URL is the commonest thing anybody has to hand."""
    client.patch(
        f"/api/artists/{ARTIST_ID}/tags",
        json={"mb_artist_mbid": f"https://musicbrainz.org/artist/{MBID}"},
    )
    assert artist_json(client)["mb_artist_mbid"] == MBID


def test_a_typed_id_is_marked_manual_so_nothing_re_derives_it(
    client: TestClient, enricher: Enricher
) -> None:
    """The invariant this field exists or does not exist on.

    ``_MANUAL_OWNS`` reads the ``mb_match_method`` marker and drops incoming
    keys a person owns. Without the marker the next automatic pass replaces the
    id, and that id is written into every file as ``MUSICBRAINZ_ARTISTID``.
    """
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"mb_artist_mbid": MBID})

    async def go(session: AsyncSession) -> Any:
        meta = await session.get(ArtistMetadata, ARTIST_ID)
        assert meta.mb_match_method == "manual"
        # Exactly what an automatic derivation does on the next tick.
        await Enricher._upsert(
            session,
            ArtistMetadata,
            {"artist_id": ARTIST_ID},
            {"mb_artist_mbid": OTHER_MBID, "mb_match_method": "release-credit"},
        )
        await session.commit()
        await session.refresh(meta)
        return meta.mb_artist_mbid, meta.mb_match_method

    assert run(go) == (MBID, "manual")


def test_a_person_may_still_overrule_a_person(
    client: TestClient, enricher: Enricher
) -> None:
    """A second edit is somebody correcting the first, so it goes through."""
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"mb_artist_mbid": MBID})
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"mb_artist_mbid": OTHER_MBID})
    assert artist_json(client)["mb_artist_mbid"] == OTHER_MBID


def test_nothing_here_searches_on_a_name(
    client: TestClient, enricher: Enricher, monkeypatch: pytest.MonkeyPatch
) -> None:
    """"Exact or nothing": a name may reject a candidate, never select one.

    The enricher is built with no providers at all, so a code path that reached
    for a name search would have nothing to reach for and would fail loudly.
    Asserted anyway, because the failure this guards against is a *future*
    convenience — "we know the name, look it up for them".
    """
    monkeypatch.setattr(
        Enricher,
        "candidates",
        lambda *a, **k: pytest.fail("the tag editor must never search a name"),
    )
    assert (
        client.patch(
            f"/api/artists/{ARTIST_ID}/tags", json={"name": "Nils Frahm"}
        ).status_code
        == 200
    )


# ---------------------------------------------------------------------------
# Aliases — typed, stored, read back, and read by nothing else
# ---------------------------------------------------------------------------
def test_aliases_round_trip(client: TestClient) -> None:
    """Saved, and still there on a request that shares nothing with the save.

    ``UNENRICHED`` deliberately has **no** ``artist_metadata`` row. Enrichment
    is library-scoped, so most artists do not — and reading aliases inside
    ``_apply_artist_metadata`` (past its ``if meta is None: return``) would
    hand every one of them an empty box seconds after they saved three.
    """
    seed_unenriched_artist()
    response = client.patch(
        f"/api/artists/{UNENRICHED}/tags", json={"aliases": ["Nonkeen", "Victor Solf"]}
    )
    assert response.status_code == 200
    assert response.json()["aliases"] == ["Nonkeen", "Victor Solf"]

    body = client.get(f"/api/artists/{UNENRICHED}").json()
    assert body["aliases"] == ["Nonkeen", "Victor Solf"]

    async def go(session: AsyncSession) -> Any:
        return await session.get(ArtistMetadata, UNENRICHED)

    assert run(go) is None, "the fixture stopped pinning the un-enriched case"


def test_an_alias_may_contain_a_comma(client: TestClient) -> None:
    """The storage-shape proof. A comma join would make this two aliases."""
    client.patch(
        f"/api/artists/{ARTIST_ID}/tags", json={"aliases": ["Tchaikovsky, Pyotr Ilyich"]}
    )
    assert artist_json(client)["aliases"] == ["Tchaikovsky, Pyotr Ilyich"]


def test_an_empty_list_clears_and_omission_leaves_alone(client: TestClient) -> None:
    """The single-artist rule: ``[]`` is a value, an absent key is not."""
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"aliases": ["Nonkeen"]})
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"name": "Nils Frahm"})
    assert artist_json(client)["aliases"] == ["Nonkeen"], "an omitted key wrote"

    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"aliases": []})
    assert artist_json(client)["aliases"] == []

    async def go(session: AsyncSession) -> Any:
        return (await session.get(Artist, ARTIST_ID)).aliases_json

    assert run(go) is None, "stored '[]' rather than 'nobody has said'"


def test_blank_and_duplicate_entries_are_dropped(client: TestClient) -> None:
    response = client.patch(
        f"/api/artists/{ARTIST_ID}/tags", json={"aliases": ["  ", "A", "A", " A "]}
    )
    assert response.status_code == 200
    assert artist_json(client)["aliases"] == ["A"]


def test_an_overlong_alias_is_refused(client: TestClient) -> None:
    """A refusal with a sentence, not a truncation — and the list itself is
    bounded by ``max_length`` on the field, which is a 422 before any handler
    runs."""
    long = client.patch(
        f"/api/artists/{ARTIST_ID}/tags", json={"aliases": ["x" * 513]}
    )
    assert long.status_code == 400
    assert "512" in long.json()["error"]

    many = client.patch(
        f"/api/artists/{ARTIST_ID}/tags", json={"aliases": [f"a{n}" for n in range(65)]}
    )
    assert many.status_code == 422
    assert artist_json(client)["aliases"] == []


def test_aliases_reach_no_file(
    client: TestClient, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Database-only, deliberately.

    Picard writes no artist-alias field, so inventing a tag name would produce
    a tag no other tool reads; no media server reads an alias element out of an
    ``artist.nfo`` either. The re-tag pass must therefore write exactly what it
    wrote before the column existed.
    """
    alias = "Tchaikovsky, Pyotr Ilyich"
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"aliases": [alias]})

    written: list[tuple[tuple[Any, ...], dict[str, Any]]] = []

    def fake_tag_file(path: Path, *args: Any, **kwargs: Any) -> bool:
        # Both halves of the call, not just the keywords: ``extra_tags`` is
        # keyword-only today, but the claim being made is that the pass writes
        # exactly what it wrote before the column existed, and an alias
        # arriving positionally would break that just as loudly.
        written.append(((path, *args), kwargs))
        return True

    monkeypatch.setattr(librarian, "tag_file", fake_tag_file)
    assert client.post(f"/api/artists/{ARTIST_ID}/retag").status_code == 200
    assert written, "the pass tagged nothing, so it proves nothing"
    for args, kwargs in written:
        assert alias not in repr(args)
        assert alias not in repr(kwargs)

    nfo = artist_dir(settings) / "artist.nfo"
    assert nfo.is_file()
    assert alias not in nfo.read_text(encoding="utf-8")


def test_parse_aliases_survives_garbage() -> None:
    """A hand-edited database must not 500 the artist page."""
    assert parse_aliases(None) == []
    assert parse_aliases("not json") == []
    assert parse_aliases('{"a": 1}') == []
    assert parse_aliases("[1, 2]") == []
    assert parse_aliases('["  ", "A", "A"]') == ["A"]


# ---------------------------------------------------------------------------
# Refusals
# ---------------------------------------------------------------------------
def test_an_unknown_key_is_refused_rather_than_dropped(client: TestClient) -> None:
    """``extra="forbid"``, and this is the rule the aliases box was measured
    against.

    Ignoring a key tells the user something was saved that was not. *aliases*
    used to answer 422 here; it earned a column on ``artists`` instead, which
    is the only other honest outcome. Anything still unknown keeps the 422.
    """
    response = client.patch(
        f"/api/artists/{ARTIST_ID}/tags",
        json={"name": "Nils Frahm", "nickname": "Frahmy"},
    )
    assert response.status_code == 422
    assert artist_json(client)["name"] == "Nils Frahm"


def test_a_blank_name_is_refused(client: TestClient) -> None:
    """It is what names the folder every one of their releases sits in."""
    for value in ("", "   "):
        response = client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"name": value})
        assert response.status_code == 400
        assert "blank" in response.json()["error"]
    assert artist_json(client)["name"] == "Nils Frahm"


def test_a_blank_musicbrainz_id_is_refused_not_treated_as_a_clear(
    client: TestClient, enricher: Enricher
) -> None:
    """An empty box is far more often one nobody filled in."""
    response = client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"mb_artist_mbid": ""})
    assert response.status_code == 400
    assert "cannot be emptied" in response.json()["error"]


def test_a_bad_musicbrainz_id_rolls_the_whole_edit_back(
    client: TestClient, enricher: Enricher
) -> None:
    """Half-saving the form would attach an error to the field that did save."""
    response = client.patch(
        f"/api/artists/{ARTIST_ID}/tags",
        json={"name": "Someone Else", "mb_artist_mbid": "not-an-id"},
    )
    assert response.status_code == 400
    assert "not a MusicBrainz id" in response.json()["error"]
    assert artist_json(client)["name"] == "Nils Frahm"


def test_an_id_with_no_enricher_running_is_a_503_before_anything_is_written(
    client: TestClient,
) -> None:
    """No ``enricher`` fixture here, so nothing is wired.

    Checked before the columns are touched: a 503 that had already renamed the
    artist would be a failure that changed something.
    """
    response = client.patch(
        f"/api/artists/{ARTIST_ID}/tags",
        json={"name": "Someone Else", "mb_artist_mbid": MBID},
    )
    assert response.status_code == 503
    assert artist_json(client)["name"] == "Nils Frahm"


def test_editing_an_artist_that_is_not_followed_is_a_404(client: TestClient) -> None:
    assert (
        client.patch("/api/artists/nobody/tags", json={"name": "X"}).status_code == 404
    )


def test_identifying_an_artist_that_has_gone_is_a_404_not_a_500(
    client: TestClient, enricher: Enricher
) -> None:
    """Unfollowing leaves review rows behind; the id write would fail the FK."""
    response = client.patch(
        "/api/artists/nobody/tags", json={"mb_artist_mbid": MBID}
    )
    assert response.status_code == 404


# ---------------------------------------------------------------------------
# Re-tag — the half that writes to the library
# ---------------------------------------------------------------------------
def test_retagging_writes_every_release_on_disk(
    client: TestClient, tagged: list[str]
) -> None:
    report = client.post(f"/api/artists/{ARTIST_ID}/retag").json()
    # Three releases on disk; the one the worker is fetching is refused.
    assert report["considered"] == 3
    assert report["changed"] == 2
    assert report["level"] == "warning"


def test_a_busy_release_is_blocked_not_attempted(
    client: TestClient, tagged: list[str]
) -> None:
    """The worker is writing ``.part`` files into that directory.

    Counted **once** although two passes refuse it — the re-tag and the NFO —
    which is why ``errors`` is longer than ``blocked``. "2 blocked" about one
    release is a figure nobody can act on.
    """
    report = client.post(f"/api/artists/{ARTIST_ID}/retag").json()
    assert report["blocked"] == 1
    assert len([line for line in report["errors"] if "Felt" in line]) == 2
    assert not any(FOLDERS[BUSY] in path for path in tagged), "attempted anyway"


def test_retagging_writes_the_artist_nfo_with_the_typed_sort_name(
    client: TestClient, settings: Settings, tagged: list[str]
) -> None:
    """The only place a typed ``sort_name`` reaches disk.

    Leave the NFO pass out and saving a sort name is a value that never leaves
    the database — which is indistinguishable, to the person who typed it, from
    the field not working.
    """
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"sort_name": "Frahm, Nils"})
    report = client.post(f"/api/artists/{ARTIST_ID}/retag").json()
    assert report["described"] >= 1
    nfo = (artist_dir(settings) / "artist.nfo").read_text(encoding="utf-8")
    assert "<sortname>Frahm, Nils</sortname>" in nfo
    assert "derived" not in nfo


def test_a_new_name_reaches_the_files(
    client: TestClient, settings: Settings, tagged: list[str]
) -> None:
    """``retag_album`` reads ``album.artist.name``, so the edit flows straight in."""
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"name": "Nils F."})
    client.post(f"/api/artists/{ARTIST_ID}/retag")
    nfo = (artist_dir(settings) / "artist.nfo").read_text(encoding="utf-8")
    assert "<name>Nils F.</name>" in nfo


def test_retagging_queues_nothing(client: TestClient, tagged: list[str]) -> None:
    """Downloading is opt-in. A tag fix is not a user pressing Download."""
    client.post(f"/api/artists/{ARTIST_ID}/retag?refile=true")
    queue = client.get("/api/queue").json()
    assert [item for item in queue["items"] if item["state"] == "pending"] == []


def test_retagging_is_safely_re_runnable(
    client: TestClient, tagged: list[str]
) -> None:
    """It is a long loop of file writes; running it twice must not compound."""
    first = client.post(f"/api/artists/{ARTIST_ID}/retag?refile=true").json()
    second = client.post(f"/api/artists/{ARTIST_ID}/retag?refile=true").json()
    assert second["considered"] == first["considered"]
    assert second["changed"] == first["changed"]
    # The folder moved on the first pass and has nowhere left to go.
    assert first["moved"] == 1
    assert second["moved"] == 0


def test_retagging_an_artist_that_is_not_followed_is_a_404(client: TestClient) -> None:
    assert client.post("/api/artists/nobody/retag").status_code == 404


# ---------------------------------------------------------------------------
# Re-file — opt-in, and it honours freeze_path
# ---------------------------------------------------------------------------
def test_folders_are_not_renamed_unless_asked(
    client: TestClient, settings: Settings, tagged: list[str]
) -> None:
    """A tag fix and a thousand-file move are different-sized decisions."""
    report = client.post(f"/api/artists/{ARTIST_ID}/retag").json()
    assert report["moved"] == 0
    assert report["plans"] == []
    assert (artist_dir(settings) / FOLDERS[MISFILED]).is_dir()


def test_asking_moves_the_misfiled_folder(
    client: TestClient, settings: Settings, tagged: list[str]
) -> None:
    report = client.post(f"/api/artists/{ARTIST_ID}/retag?refile=true").json()
    assert report["moved"] == 1
    assert not (artist_dir(settings) / FOLDERS[MISFILED]).exists()
    assert (artist_dir(settings) / "Spaces (2013) [FLAC 24-96]").is_dir()


def test_the_artist_nfo_follows_the_folder_it_describes(
    client: TestClient, settings: Settings, tagged: list[str]
) -> None:
    """The whole point of the feature, end to end: rename, re-tag, re-file.

    ``artist.nfo`` is written into the *parent* of an album folder, so the order
    of the passes decides which artist folder gets one. Describe before moving
    and the file lands in the folder every release is about to leave: the
    renamed folder — the only one a media server will now read — has no
    ``artist.nfo`` at all, and the abandoned one keeps a stale copy that also
    stops ``_prune_empty_parents`` clearing it away. So the NFO pass runs last.
    """
    # No trailing dot: ``naming.sanitise_component`` strips one, and this test
    # is about which folder gets described, not about POSIX sanitising.
    client.patch(f"/api/artists/{ARTIST_ID}/tags", json={"name": "Nils Frahm Trio"})
    report = client.post(f"/api/artists/{ARTIST_ID}/retag?refile=true").json()
    assert report["described"] >= 1

    moved_to = settings.library_path / "Nils Frahm Trio"
    nfo = moved_to / "artist.nfo"
    assert nfo.is_file(), "the renamed artist folder was left undescribed"
    assert "<name>Nils Frahm Trio</name>" in nfo.read_text(encoding="utf-8")
    # BUSY is refused by every pass, so its folder — and only its folder — is
    # still under the old name. Nothing may have been described up there.
    assert not (settings.library_path / "Nils Frahm" / "artist.nfo").exists()


def test_the_report_only_counts_releases_the_pass_considered(
    client: TestClient, tagged: list[str]
) -> None:
    """The NFO sweep works from a wider album set than the re-tag does.

    ``albums_on_disk`` is ``DOWNLOADED`` **and** has a path; ``write_library_nfo``
    is any album with a path at all. ``_verify_library`` demotes every release it
    cannot see to ``WANTED`` without clearing ``Album.path``, so an unmounted
    share used to produce "0 of 0 release(s) re-tagged, N blocked" — N counted
    from releases this pass never touched, and named by bare album id.
    """

    async def demote(session: AsyncSession) -> None:
        for album_id in (ON_DISK, MISFILED, BUSY):
            album = await session.get(Album, album_id)
            album.status = AlbumStatus.WANTED
        await session.commit()

    run(demote)
    report = client.post(f"/api/artists/{ARTIST_ID}/retag").json()
    assert report["considered"] == 0
    assert report["blocked"] == 0
    assert report["described"] == 0
    assert report["errors"] == []


def test_a_blocked_release_is_named_not_numbered(
    client: TestClient, tagged: list[str]
) -> None:
    """Every reason names the release. A bare ``beingfetched03`` is unactionable."""
    report = client.post(f"/api/artists/{ARTIST_ID}/retag").json()
    assert report["errors"], "a busy release produced no reason at all"
    assert all(BUSY not in line for line in report["errors"])
    assert all("Felt" in line for line in report["errors"])


def test_the_retag_limit_is_bounded_like_every_other_library_route(
    client: TestClient,
) -> None:
    """``?limit=0`` reached ``max(1, limit)`` inside the librarian and re-tagged
    exactly one release while reporting ``considered: 1`` — which reads as an
    artist with one album, not as a refused request."""
    assert client.post(f"/api/artists/{ARTIST_ID}/retag?limit=0").status_code == 422


def test_a_frozen_release_is_left_where_it_is(
    client: TestClient, settings: Settings, tagged: list[str]
) -> None:
    """``freeze_path`` is somebody saying where this release lives."""
    set_freeze(MISFILED)
    report = client.post(f"/api/artists/{ARTIST_ID}/retag?refile=true").json()
    assert report["moved"] == 0
    assert (artist_dir(settings) / FOLDERS[MISFILED]).is_dir()


def test_a_frozen_release_is_reported_rather_than_silently_skipped(
    client: TestClient, tagged: list[str]
) -> None:
    """The nightly work list drops frozen releases; this is not that list.

    Somebody pressed a button asking for folders to be renamed. Saying nothing
    about the one that was not renamed reads as a rename that happened.
    """
    set_freeze(MISFILED)
    report = client.post(f"/api/artists/{ARTIST_ID}/retag?refile=true").json()
    assert any("frozen" in line for line in report["errors"])
    assert report["level"] == "warning"


def test_a_frozen_release_is_still_re_tagged(
    client: TestClient, tagged: list[str]
) -> None:
    """The flag freezes the *path*. Its tags are not frozen with it."""
    set_freeze(MISFILED)
    report = client.post(f"/api/artists/{ARTIST_ID}/retag").json()
    assert report["changed"] == 2


def test_a_frozen_release_is_refused_by_name_even_bypassing_the_plan(
    client: TestClient,
) -> None:
    """A lock only the planner honours is a lock with a hole in it."""
    set_freeze(MISFILED)

    async def go(session: AsyncSession) -> None:
        album = await session.get(Album, MISFILED)
        with pytest.raises(librarian.FrozenPathError):
            await librarian.refile_album(session, album)

    run(go)


# ---------------------------------------------------------------------------
# The migration script
# ---------------------------------------------------------------------------
def test_the_migration_adds_the_column_and_is_safe_to_run_twice(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    """``create_all`` adds tables, never columns. ``artists`` cannot be dropped
    and rebuilt the way the enrichment side tables are — a typed sort name is
    the one thing in it nothing can re-fetch."""
    db = tmp_path / "old.db"
    conn = sqlite3.connect(str(db))
    conn.execute("CREATE TABLE artists (id VARCHAR(64) PRIMARY KEY, name VARCHAR(512))")
    conn.commit()
    conn.close()

    assert migrate_artist_tags.migrate(db) == 0
    conn = sqlite3.connect(str(db))
    present = migrate_artist_tags.existing_columns(conn, "artists")
    assert "sort_name" in present
    assert "aliases_json" in present, "the second hand-typed column was not carried"
    conn.close()

    capsys.readouterr()
    assert migrate_artist_tags.migrate(db) == 0
    assert "added 0 column(s)" in capsys.readouterr().out


def test_the_migration_writes_nothing_on_a_dry_run(tmp_path: Path) -> None:
    db = tmp_path / "old.db"
    conn = sqlite3.connect(str(db))
    conn.execute("CREATE TABLE artists (id VARCHAR(64) PRIMARY KEY, name VARCHAR(512))")
    conn.commit()
    conn.close()

    assert migrate_artist_tags.migrate(db, dry_run=True) == 0
    conn = sqlite3.connect(str(db))
    assert "sort_name" not in migrate_artist_tags.existing_columns(conn, "artists")
    conn.close()


def test_the_migration_refuses_a_database_that_is_not_there(tmp_path: Path) -> None:
    assert migrate_artist_tags.migrate(tmp_path / "nope.db") == 1
