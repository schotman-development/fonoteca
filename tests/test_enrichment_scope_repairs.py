"""What scoping enrichment to the library broke, and the repairs for it.

Three of these are consequences of the same fact: once only ``downloaded``
albums are enriched, *half-enriched* stops being a state a library passes
through on its way to being finished and becomes the state it permanently lives
in. Thirty-two releases out of 3313 carry MusicBrainz ids; the other 3281 never
will. Anything that quietly assumed enrichment eventually reaches everything is
now wrong on almost every row.

* **Edition grouping.** ``release_group_key`` is the MBID when one is known and
  the normalised title otherwise, so the copy you own and its catalogue-only
  twins ended up keyed differently — permanently. The page for the release you
  actually have was the one claiming the record had no other editions, and the
  artist page showed one record twice. Fixed by resolving both kinds of key
  through a single equivalence rule, which is deliberately asymmetric: two
  MBIDs that disagree are two records whatever their titles say, but an album
  with no MBID joins the record whose title it shares.
* **The nightly purge.** ``_verify_library`` demotes every album whose files it
  cannot see, which is exactly what an unmounted NAS produces, and the purge
  behind it would read that as an empty library and delete every state row in
  the database. The refresh clocks go with them, so the mount returning costs a
  full re-enrichment of everything. The purge now stands down on any run that
  demoted something — it is idempotent and loses nothing by waiting a night.
* **The write-back.** A release becomes enrichable only once it is downloaded,
  and the download loop resolves its tags before the first file is written, so
  for a first download the ids now arrive strictly after the only thing that
  would have written them. The library knew every MusicBrainz id and put none of
  them where Picard, beets or Roon would look.

The fourth is smaller: ``enrich-now --artist`` re-opened every album an artist
had, re-creating precisely the rows the purge exists to delete.
"""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from types import SimpleNamespace
from typing import Any, AsyncIterator, Iterator

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.api.deps import _record_key_pairs, album_to_out, group_editions, list_release_group
from app.config import Settings
from app.core.enricher import Enricher, load_album_metadata
from app.core.scheduler import _prune_enrichment
from app.enrich.types import AlbumSnapshot, EnrichmentJob, EnrichmentResult
from app.models import (
    Album,
    AlbumMetadata,
    AlbumStatus,
    Artist,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    EnrichmentState,
    Track,
    TrackOrigin,
    TrackStatus,
)

RG_RUMOURS = "45017130-1af1-3e2f-bcbd-8c1d0ba4c3f2"
RG_OTHER = "9f1d3e21-77aa-4d6c-8b0e-2c4a5f6d7e8b"


@pytest.fixture(name="factory")
def factory_fixture() -> Iterator[Any]:
    """A scratch in-memory database plus a ``session_scope``-alike factory.

    Same shape as ``tests/test_enrichment_scope.py``: plain synchronous tests
    driving ``asyncio.run``, because that is how every async test in this suite
    is written and a second convention helps nobody.
    """
    engine = create_async_engine(
        "sqlite+aiosqlite://",
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)

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

    session_factory.maker = maker  # type: ignore[attr-defined]
    try:
        yield session_factory
    finally:
        asyncio.run(engine.dispose())


async def _outs(session: AsyncSession) -> list[Any]:
    rows = list((await session.execute(select(Album))).unique().scalars().all())
    meta = await load_album_metadata(session, [row.id for row in rows])
    return [album_to_out(row, meta=meta.get(str(row.id))) for row in rows]


# ---------------------------------------------------------------------------
# Edition grouping across a permanently half-enriched library
# ---------------------------------------------------------------------------


def test_an_unenriched_edition_joins_the_enriched_one_it_shares_a_title_with() -> None:
    assigned = _record_key_pairs(
        [("own", RG_RUMOURS, "rumours"), ("cat", None, "rumours")]
    )
    assert assigned["own"] == assigned["cat"]


def test_two_release_groups_that_share_a_title_are_still_two_records() -> None:
    """MusicBrainz has already said these differ; a matching title is not evidence."""
    assigned = _record_key_pairs(
        [("a", RG_RUMOURS, "greatest hits"), ("b", RG_OTHER, "greatest hits")]
    )
    assert assigned["a"] != assigned["b"]


def test_a_title_claimed_by_two_records_leaves_the_unmatched_album_alone() -> None:
    """No honest way to choose, so it does not choose."""
    assigned = _record_key_pairs(
        [
            ("a", RG_RUMOURS, "greatest hits"),
            ("b", RG_OTHER, "greatest hits"),
            ("c", None, "greatest hits"),
        ]
    )
    assert assigned["c"] not in {assigned["a"], assigned["b"]}


def test_a_wholly_unenriched_library_still_groups_by_title() -> None:
    """The common case, and the one the fallback exists for."""
    assigned = _record_key_pairs(
        [("a", None, "rumours"), ("b", None, "rumours"), ("c", None, "tusk")]
    )
    assert assigned["a"] == assigned["b"] != assigned["c"]


def test_differently_titled_editions_of_one_release_group_stay_together() -> None:
    assigned = _record_key_pairs(
        [
            ("a", RG_RUMOURS, "rumours"),
            ("b", RG_RUMOURS, "rumours deluxe edition"),
            ("c", None, "rumours deluxe edition"),
        ]
    )
    assert assigned["a"] == assigned["b"] == assigned["c"]


def test_the_release_you_own_sees_the_editions_you_do_not(factory: Any) -> None:
    """The failure this repairs: /albums/<owned> rendered no other editions.

    Keyed naively the owned copy is filed under its MBID and its catalogue twin
    under the normalised title, and ``list_release_group`` matched a key against
    one or the other — so the title key found both rows while the MBID found only
    itself. The page for the copy the user actually has was the one that claimed
    the record was alone.
    """

    async def go() -> tuple[set[str], set[str], list[Any]]:
        async with factory() as session:
            session.add(Artist(id="ar1", name="Fleetwood Mac"))
            session.add(Album(id="own", artist_id="ar1", title="Rumours",
                              status=AlbumStatus.DOWNLOADED))
            session.add(Album(id="cat", artist_id="ar1", title="Rumours",
                              status=AlbumStatus.WANTED))
            session.add(AlbumMetadata(album_id="own",
                                      mb_release_group_mbid=RG_RUMOURS))

        async with factory() as session:
            from_owned, _ = await list_release_group(session, RG_RUMOURS)
            from_catalogue, _ = await list_release_group(session, "rumours")
            groups = group_editions(await _outs(session))
            return (
                {row.id for row in from_owned},
                {row.id for row in from_catalogue},
                groups,
            )

    owned, catalogue, groups = asyncio.run(go())
    assert owned == {"own", "cat"}
    assert catalogue == {"own", "cat"}
    assert len(groups) == 1
    assert groups[0]["total"] == 2
    assert groups[0]["downloaded"] == 1


def test_the_artist_page_shows_one_record_once(factory: Any) -> None:
    async def go() -> list[Any]:
        async with factory() as session:
            session.add(Artist(id="ar1", name="Fleetwood Mac"))
            for album_id, status in (
                ("own", AlbumStatus.DOWNLOADED),
                ("dlx", AlbumStatus.WANTED),
                ("sdx", AlbumStatus.WANTED),
            ):
                session.add(Album(id=album_id, artist_id="ar1", title="Rumours",
                                  status=status))
            session.add(AlbumMetadata(album_id="own",
                                      mb_release_group_mbid=RG_RUMOURS))

        async with factory() as session:
            return group_editions(await _outs(session))

    groups = asyncio.run(go())
    assert len(groups) == 1, "one record must not render as two rows"
    assert groups[0]["total"] == 3


def test_a_key_from_either_side_reaches_the_same_record(factory: Any) -> None:
    """Both kinds of key round-trip through /release-groups/{key}."""

    async def go() -> list[set[str]]:
        async with factory() as session:
            session.add(Artist(id="ar1", name="Fleetwood Mac"))
            session.add(Album(id="own", artist_id="ar1", title="Rumours",
                              status=AlbumStatus.DOWNLOADED))
            session.add(Album(id="cat", artist_id="ar1", title="Rumours",
                              status=AlbumStatus.WANTED))
            session.add(AlbumMetadata(album_id="own",
                                      mb_release_group_mbid=RG_RUMOURS))

        seen: list[set[str]] = []
        async with factory() as session:
            for out in await _outs(session):
                editions, _ = await list_release_group(session, out.release_group_key)
                seen.append({row.id for row in editions})
        return seen

    for reached in asyncio.run(go()):
        assert reached == {"own", "cat"}


# ---------------------------------------------------------------------------
# The nightly purge and the unreachable library
# ---------------------------------------------------------------------------


async def _seed_for_purge(factory: Any) -> None:
    async with factory() as session:
        session.add(Artist(id="ar1", name="Beth Hart"))
        session.add(Album(id="al1", artist_id="ar1", title="War In My Mind",
                          status=AlbumStatus.DOWNLOADED))
        session.add(
            EnrichmentState(
                entity_type=EnrichmentEntity.ALBUM,
                entity_id="al1",
                source=EnrichmentSource.MUSICBRAINZ,
                state="ok",
            )
        )


def test_the_purge_stands_down_when_the_verify_pass_demoted_anything(
    factory: Any,
) -> None:
    """An unreachable library is indistinguishable from an empty one.

    ``_verify_library`` flips every album whose files it cannot see back to
    ``wanted``. If the purge then ran it would delete every enrichment_state row
    there is, refresh clocks and all, and the mount coming back would cost a full
    re-enrichment of the entire library.
    """

    async def go() -> tuple[dict[str, Any], int]:
        await _seed_for_purge(factory)
        # What a night with an unmounted NAS looks like by the time the purge is
        # reached: the album demoted, and albums_flagged non-zero.
        async with factory() as session:
            album = await session.get(Album, "al1")
            assert album is not None
            album.status = AlbumStatus.WANTED

        summary: dict[str, Any] = {"albums_flagged": 1}
        async with factory() as session:
            await _prune_enrichment(session, summary)
        async with factory() as session:
            rows = (await session.execute(select(EnrichmentState))).scalars().all()
            return summary, len(rows)

    summary, remaining = asyncio.run(go())
    assert summary["enrichment_purged"] == 0
    assert summary["enrichment_purge_skipped"] == 1
    assert remaining == 1, "the refresh clock must survive a bad mount"


def test_the_purge_runs_on_a_night_that_demoted_nothing(factory: Any) -> None:
    """The guard is a delay, not an exemption — a real deletion is still collected."""

    async def go() -> tuple[dict[str, Any], int]:
        await _seed_for_purge(factory)
        async with factory() as session:
            album = await session.get(Album, "al1")
            assert album is not None
            album.status = AlbumStatus.WANTED  # genuinely removed from disk

        summary: dict[str, Any] = {"albums_flagged": 0}
        async with factory() as session:
            await _prune_enrichment(session, summary)
        async with factory() as session:
            rows = (await session.execute(select(EnrichmentState))).scalars().all()
            return summary, len(rows)

    summary, remaining = asyncio.run(go())
    assert summary["enrichment_purged"] == 1
    assert summary.get("enrichment_purge_skipped") is None
    assert remaining == 0


# ---------------------------------------------------------------------------
# Writing what was learned into the files
# ---------------------------------------------------------------------------


class _Recorder:
    """Stands in for the librarian, which is the only thing allowed to write."""

    def __init__(self) -> None:
        self.retagged: list[str] = []
        self.nfo: list[str] = []


@pytest.fixture(name="librarian")
def librarian_fixture(monkeypatch: pytest.MonkeyPatch) -> _Recorder:
    recorder = _Recorder()

    async def fake_retag(session: Any, album: Any, **kwargs: Any) -> Any:
        recorder.retagged.append(str(album.id))
        return SimpleNamespace(tagged=3, failed=0, missing=0)

    async def fake_nfo(session: Any, album: Any, **kwargs: Any) -> Any:
        recorder.nfo.append(str(album.id))
        return SimpleNamespace(album_written=True, artist_written=False, skipped=None)

    import app.core.librarian as librarian_module

    monkeypatch.setattr(librarian_module, "retag_album", fake_retag)
    monkeypatch.setattr(librarian_module, "write_nfo", fake_nfo)
    return recorder


def _run_write_back(factory: Any, settings: Settings, album_id: str) -> int:
    job = EnrichmentJob(
        entity_type=EnrichmentEntity.ALBUM,
        entity_id=album_id,
        source=EnrichmentSource.MUSICBRAINZ,
        subject=AlbumSnapshot(
            id=album_id,
            artist_id="ar1",
            artist_name="Beth Hart",
            title="War In My Mind",
        ),
    )
    enricher = Enricher((), settings=settings, session_factory=factory)
    return asyncio.run(enricher._write_back([(job, EnrichmentResult())]))


async def _seed_release(
    factory: Any,
    *,
    status: AlbumStatus,
    tracks: bool,
    origin: TrackOrigin = TrackOrigin.DOWNLOAD,
    pin_tags: bool = False,
) -> None:
    async with factory() as session:
        session.add(Artist(id="ar1", name="Beth Hart"))
        session.add(Album(id="al1", artist_id="ar1", title="War In My Mind",
                          status=status, path="/library/beth/war",
                          pin_tags=pin_tags))
        if tracks:
            session.add(
                Track(id="t1", album_id="al1", title="Sugar Shack", track_number=1,
                      status=TrackStatus.DOWNLOADED, origin=origin,
                      path="/library/beth/war/01.flac")
            )


def _settings(**overrides: Any) -> Settings:
    base: dict[str, Any] = {"qobuz_app_id": "app", "qobuz_user_auth_token": "token"}
    base.update(overrides)
    return Settings(**base)


def test_identifying_a_release_puts_the_ids_in_the_files(
    factory: Any, librarian: _Recorder
) -> None:
    """The loop that scoping closed: enrichment now always lands after tagging."""
    asyncio.run(_seed_release(factory, status=AlbumStatus.DOWNLOADED, tracks=True))

    written = _run_write_back(factory, _settings(), "al1")

    assert librarian.retagged == ["al1"]
    assert librarian.nfo == ["al1"]
    assert written == 1


def test_an_adopted_album_keeps_the_tags_it_arrived_with(
    factory: Any, librarian: _Recorder
) -> None:
    """A release nothing has counted yet is certainly not ours to rewrite."""
    asyncio.run(_seed_release(factory, status=AlbumStatus.DOWNLOADED, tracks=False))

    written = _run_write_back(factory, _settings(), "al1")

    assert librarian.retagged == [], "somebody else's tags are not ours to overwrite"
    assert librarian.nfo == ["al1"], "the NFO is merged, so it is still written"
    assert written == 1


def test_scanned_track_rows_do_not_make_an_album_ours_to_retag(
    factory: Any, librarian: _Recorder
) -> None:
    """The gate reads ``origin``, not the mere existence of a track row.

    This is the regression the disk scan could have caused. The gate used to
    infer "Qobuzarr downloaded this" from *having* track rows, which was true
    only while the download loop was the sole writer of them. The scan writes
    rows now, so reading it the old way would have turned one setting into a
    licence to rewrite the tags of every hand-curated album in the library.
    """
    asyncio.run(
        _seed_release(
            factory,
            status=AlbumStatus.DOWNLOADED,
            tracks=True,
            origin=TrackOrigin.SCAN,
        )
    )

    written = _run_write_back(factory, _settings(), "al1")

    assert librarian.retagged == [], "scanned rows describe somebody else's files"
    assert librarian.nfo == ["al1"]
    assert written == 1


def test_pinning_a_release_keeps_the_write_back_off_its_tags(
    factory: Any, librarian: _Recorder
) -> None:
    """``Album.pin_tags`` is the per-release version of the adopted-album rule.

    The provenance gate above says "this release arrived with somebody else's
    tags". Pinning says "this release has *since become* somebody else's tags",
    which the database cannot infer and a person can. It is the same refusal for
    the same reason and it lands in the same guard clause.
    """
    asyncio.run(
        _seed_release(
            factory, status=AlbumStatus.DOWNLOADED, tracks=True, pin_tags=True
        )
    )

    written = _run_write_back(factory, _settings(), "al1")

    assert librarian.retagged == [], "a pinned release's tags are not ours to rewrite"
    assert librarian.nfo == ["al1"], "the NFO is merged, so pinning does not stop it"
    assert written == 1


def test_an_unpinned_release_is_the_default(
    factory: Any, librarian: _Recorder
) -> None:
    """The flag's default is off, so nothing about existing behaviour moved.

    Worth pinning down explicitly: a column added with the wrong default silently
    switches a feature off for every row already in the database, and the symptom
    is a library that quietly stops receiving its own ids.
    """
    asyncio.run(_seed_release(factory, status=AlbumStatus.DOWNLOADED, tracks=True))

    async def read() -> bool:
        async with factory() as session:
            album = await session.get(Album, "al1")
            return bool(album.pin_tags)

    assert asyncio.run(read()) is False
    _run_write_back(factory, _settings(), "al1")
    assert librarian.retagged == ["al1"]


def test_the_write_back_can_be_switched_off(
    factory: Any, librarian: _Recorder
) -> None:
    asyncio.run(_seed_release(factory, status=AlbumStatus.DOWNLOADED, tracks=True))

    written = _run_write_back(factory, _settings(enrichment_write_back=False), "al1")

    assert librarian.retagged == []
    assert librarian.nfo == []
    assert written == 0


def test_a_release_that_left_the_library_is_not_written_to(
    factory: Any, librarian: _Recorder
) -> None:
    """Demoted between the lookup and the write — the files are not there."""
    asyncio.run(_seed_release(factory, status=AlbumStatus.WANTED, tracks=True))

    written = _run_write_back(factory, _settings(), "al1")

    assert librarian.retagged == []
    assert librarian.nfo == []
    assert written == 0


def test_a_failing_write_does_not_fail_the_tick(
    factory: Any, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Enrichment that succeeded must not be lost to a library write that did not."""
    import app.core.librarian as librarian_module

    async def boom(session: Any, album: Any, **kwargs: Any) -> Any:
        raise OSError("read-only file system")

    monkeypatch.setattr(librarian_module, "retag_album", boom)
    monkeypatch.setattr(librarian_module, "write_nfo", boom)

    asyncio.run(_seed_release(factory, status=AlbumStatus.DOWNLOADED, tracks=False))

    assert _run_write_back(factory, _settings(), "al1") == 0
