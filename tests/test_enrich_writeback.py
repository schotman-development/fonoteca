"""Putting enrichment on disk: tags in the files, NFO beside them.

The tag tests are round trips through real files. mutagen writes a genuine FLAC
and a genuine MP3 in a temp directory, the tagger runs, and the frames are read
back out — because the only thing worth asserting here is that *another program*
would find these tags, and asserting on the dict the tagger built would prove
nothing about that. Which is also why the frame names are checked against
Picard's spelling: a tag spelled differently is a tag no tool will ever read.

The NFO tests parse the output back with ElementTree, for the same reason.

Two properties recur. **The audio is never touched** — this whole phase writes
metadata and nothing else. And **an unenriched library is unchanged**: every new
field is absent rather than blank when nothing has been resolved.
"""

from __future__ import annotations

import asyncio
import struct
import xml.etree.ElementTree as ET
from contextlib import asynccontextmanager
from datetime import date
from pathlib import Path
from typing import Any, AsyncIterator, Iterator

import pytest
from mutagen.flac import FLAC
from mutagen.id3 import ID3
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.config import Settings
from app.core.librarian import retag_album, write_library_nfo, write_nfo
from app.core.nfo import ALBUM_NFO, ARTIST_NFO, render_album_nfo, render_artist_nfo, tag_values
from app.core.tagger import build_tags, tag_file
from app.models import (
    Album,
    AlbumMetadata,
    AlbumStatus,
    Artist,
    ArtistMetadata,
    Base,
    Track,
    TrackMetadata,
    TrackStatus,
)

ARTIST_MBID = "984f8239-8fe1-4683-9c54-10ffb14439e9"
RELEASE_MBID = "43cc66a3-5418-4d0e-a14b-2b01aa23b171"
GROUP_MBID = "45017130-d783-4a09-97e1-780a3fd40382"
RECORDING_MBID = "a351de58-0a30-4eab-ad82-a7953c1a1897"
TRACK_MBID = "e4472456-245d-4056-8cea-de3aaebe7d3e"
ISNI = "0000000115034211"


# ---------------------------------------------------------------------------
# Fixtures — real audio files, small enough to be cheap
# ---------------------------------------------------------------------------
def write_flac(
    path: Path,
    *,
    sample_rate: int = 44100,
    channels: int = 1,
    bits: int = 16,
    samples: int = 44100,
) -> Path:
    """A genuine, minimal FLAC file: magic plus one STREAMINFO block.

    Built here rather than shelled out to ``flac(1)``, which is not installed and
    should not have to be — the suite must not depend on host tooling. mutagen
    needs only a parseable STREAMINFO to read and write tags, and tagging is what
    these tests are about; there are no audio frames because no test decodes one.
    """
    info = struct.pack(">HH", 4096, 4096)  # min/max block size
    info += b"\x00" * 6  # min/max frame size, unknown
    packed = (
        (sample_rate << 44)
        | ((channels - 1) << 41)
        | ((bits - 1) << 36)
        | samples
    )
    info += packed.to_bytes(8, "big")
    info += b"\x00" * 16  # MD5 of the unencoded audio, unset
    # 0x80 == last-metadata-block flag | block type 0 (STREAMINFO).
    path.write_bytes(b"fLaC" + bytes([0x80]) + len(info).to_bytes(3, "big") + info)
    return path


def write_mp3(path: Path) -> Path:
    """A minimal MPEG frame; enough for mutagen's ID3 handling."""
    # MPEG-1 Layer III, 128 kbps, 44.1 kHz, mono. 417 bytes per frame.
    header = b"\xff\xfb\x90\xc4"
    path.write_bytes(header + b"\x00" * 413)
    return path


class Row:
    """A stand-in for an ORM row: the tagger reads attributes, not types."""

    def __init__(self, **fields: Any) -> None:
        self.__dict__.update(fields)


def make_track(**overrides: Any) -> Row:
    fields: dict[str, Any] = {
        "id": "t1",
        "title": "This Train",
        "version": None,
        "track_number": 1,
        "media_number": 1,
        "isrc": "NLB931600601",
        "performer": "Joe Bonamassa",
        "composer": None,
    }
    fields.update(overrides)
    return Row(**fields)


def make_album(**overrides: Any) -> Row:
    fields: dict[str, Any] = {
        "id": "0804879535645",
        "title": "Blues Of Desperation",
        "version": None,
        "release_date": date(2016, 3, 25),
        "year": 2016,
        "tracks_count": 11,
        "media_count": 1,
        "genre": "Blues",
        "label": "J&R Adventures",
        "upc": "0804879535645",
        "artist": Row(name="Joe Bonamassa"),
        "release_type": "album",
        "image_url": "https://example.test/cover.jpg",
    }
    fields.update(overrides)
    return Row(**fields)


ENRICHED = {
    "musicbrainz_artistid": ARTIST_MBID,
    "musicbrainz_albumartistid": ARTIST_MBID,
    "musicbrainz_albumid": RELEASE_MBID,
    "musicbrainz_releasegroupid": GROUP_MBID,
    "musicbrainz_releasetrackid": TRACK_MBID,
    "musicbrainz_trackid": RECORDING_MBID,
    "acoustid_id": "aid-1",
    "isni": ISNI,
    "catalognumber": "JRA-2016",
    "releasecountry": "XW",
    "releasestatus": "Official",
    "releasetype": "Album",
    "originaldate": "2016-03-25",
}


# ---------------------------------------------------------------------------
# build_tags
# ---------------------------------------------------------------------------
def test_the_barcode_is_finally_written() -> None:
    """``Album.upc`` has been stored since the beginning and never tagged."""
    tags = build_tags(make_track(), make_album())

    assert tags["barcode"] == "0804879535645"


def test_extra_is_keyword_only() -> None:
    """A positional parameter would break every caller that patches ``tag_file``
    with ``lambda path, *a, **k``."""
    with pytest.raises(TypeError):
        build_tags(make_track(), make_album(), None, ENRICHED)  # type: ignore[misc]


def test_enrichment_values_merge_in_and_empties_are_dropped() -> None:
    tags = build_tags(
        make_track(), make_album(), extra={**ENRICHED, "releasestatus": ""}
    )

    assert tags["musicbrainz_albumid"] == RELEASE_MBID
    assert "releasestatus" not in tags


def test_without_enrichment_nothing_new_appears() -> None:
    tags = build_tags(make_track(), make_album())

    assert not [key for key in tags if key.startswith("musicbrainz")]


# ---------------------------------------------------------------------------
# FLAC round trip
# ---------------------------------------------------------------------------
@pytest.fixture(name="flac")
def flac_fixture(tmp_path: Path) -> Path:
    return write_flac(tmp_path / "01 - This Train.flac")


def test_flac_carries_the_musicbrainz_ids_picard_would_look_for(flac: Path) -> None:
    assert tag_file(flac, make_track(), make_album(), extra_tags=ENRICHED)

    audio = FLAC(str(flac))
    assert audio["MUSICBRAINZ_ARTISTID"] == [ARTIST_MBID]
    assert audio["MUSICBRAINZ_ALBUMID"] == [RELEASE_MBID]
    assert audio["MUSICBRAINZ_RELEASEGROUPID"] == [GROUP_MBID]
    assert audio["MUSICBRAINZ_RELEASETRACKID"] == [TRACK_MBID]
    assert audio["MUSICBRAINZ_TRACKID"] == [RECORDING_MBID]


def test_flac_carries_the_identifiers_and_the_original_date(flac: Path) -> None:
    assert tag_file(flac, make_track(), make_album(), extra_tags=ENRICHED)

    audio = FLAC(str(flac))
    assert audio["ISNI"] == [ISNI]
    assert audio["BARCODE"] == ["0804879535645"]
    assert audio["CATALOGNUMBER"] == ["JRA-2016"]
    assert audio["RELEASECOUNTRY"] == ["XW"]
    assert audio["ACOUSTID_ID"] == ["aid-1"]
    assert audio["ORIGINALDATE"] == ["2016-03-25"]
    assert audio["ORIGINALYEAR"] == ["2016"]


def test_flac_keeps_the_ordinary_tags_too(flac: Path) -> None:
    assert tag_file(flac, make_track(), make_album(), extra_tags=ENRICHED)

    audio = FLAC(str(flac))
    assert audio["TITLE"] == ["This Train"]
    assert audio["ALBUM"] == ["Blues Of Desperation"]
    assert audio["ISRC"] == ["NLB931600601"]


def test_an_unenriched_flac_gains_no_musicbrainz_fields(flac: Path) -> None:
    assert tag_file(flac, make_track(), make_album())

    audio = FLAC(str(flac))
    assert not [key for key in audio.keys() if key.startswith("MUSICBRAINZ")]
    assert audio["BARCODE"] == ["0804879535645"], "but the barcode still lands"


def test_tagging_does_not_touch_the_audio(flac: Path) -> None:
    before = FLAC(str(flac)).info.length

    tag_file(flac, make_track(), make_album(), extra_tags=ENRICHED)

    assert FLAC(str(flac)).info.length == pytest.approx(before)


# ---------------------------------------------------------------------------
# MP3 round trip
# ---------------------------------------------------------------------------
@pytest.fixture(name="mp3")
def mp3_fixture(tmp_path: Path) -> Path:
    return write_mp3(tmp_path / "01 - This Train.mp3")


def test_mp3_writes_the_txxx_descriptions_picard_uses(mp3: Path) -> None:
    """The description *is* the tag name here. Spell it differently and no tool
    will ever read it."""
    assert tag_file(mp3, make_track(), make_album(), ext="mp3", extra_tags=ENRICHED)

    id3 = ID3(str(mp3))
    found = {frame.desc: frame.text[0] for frame in id3.getall("TXXX")}
    assert found["MusicBrainz Artist Id"] == ARTIST_MBID
    assert found["MusicBrainz Album Id"] == RELEASE_MBID
    assert found["MusicBrainz Release Group Id"] == GROUP_MBID
    assert found["MusicBrainz Album Release Country"] == "XW"
    assert found["Acoustid Id"] == "aid-1"
    assert found["BARCODE"] == "0804879535645"
    assert found["CATALOGNUMBER"] == "JRA-2016"


def test_mp3_writes_the_recording_id_as_a_real_ufid_frame(mp3: Path) -> None:
    """UFID with MusicBrainz's own owner string — what Picard writes and beets
    reads. A TXXX here would be invisible to both."""
    assert tag_file(mp3, make_track(), make_album(), ext="mp3", extra_tags=ENRICHED)

    frame = ID3(str(mp3)).getall("UFID:http://musicbrainz.org")[0]
    assert frame.data.decode("ascii") == RECORDING_MBID


def test_mp3_writes_the_original_release_date(mp3: Path) -> None:
    assert tag_file(mp3, make_track(), make_album(), ext="mp3", extra_tags=ENRICHED)

    assert str(ID3(str(mp3))["TDOR"].text[0]) == "2016-03-25"


def test_an_unenriched_mp3_gains_no_musicbrainz_frames(mp3: Path) -> None:
    assert tag_file(mp3, make_track(), make_album(), ext="mp3")

    id3 = ID3(str(mp3))
    assert id3.getall("UFID:http://musicbrainz.org") == []
    descs = {frame.desc for frame in id3.getall("TXXX")}
    assert not [d for d in descs if d.startswith("MusicBrainz")]


# ---------------------------------------------------------------------------
# tag_values
# ---------------------------------------------------------------------------
def test_tag_values_resolves_from_the_three_metadata_rows() -> None:
    values = tag_values(
        artist_meta=ArtistMetadata(artist_id="a1", mb_artist_mbid=ARTIST_MBID, isni=ISNI),
        album_meta=AlbumMetadata(
            album_id="al1",
            mb_release_mbid=RELEASE_MBID,
            mb_release_group_mbid=GROUP_MBID,
            catalog_number="JRA-2016",
            first_release_date="2016-03-25",
        ),
        track_meta=TrackMetadata(track_id="t1", mb_recording_mbid=RECORDING_MBID),
    )

    assert values["musicbrainz_albumartistid"] == ARTIST_MBID
    assert values["musicbrainz_artistid"] == ARTIST_MBID, "defaulted from the album artist"
    assert values["isni"] == ISNI
    assert values["musicbrainz_trackid"] == RECORDING_MBID
    assert values["originaldate"] == "2016-03-25"


def test_tag_values_with_nothing_resolved_is_empty() -> None:
    assert tag_values() == {}


# ---------------------------------------------------------------------------
# NFO rendering
# ---------------------------------------------------------------------------
def test_artist_nfo_parses_and_carries_the_identity() -> None:
    meta = ArtistMetadata(
        artist_id="a1",
        mb_artist_mbid=ARTIST_MBID,
        isni=ISNI,
        sort_name="Bonamassa, Joe",
        artist_type="Person",
        country="US",
        life_span_begin="1977-05-08",
        genres="blues,blues rock",
    )

    root = ET.fromstring(render_artist_nfo(Row(name="Joe Bonamassa", image_url=None), meta))

    assert root.tag == "artist"
    assert root.findtext("name") == "Joe Bonamassa"
    assert root.findtext("musicBrainzArtistID") == ARTIST_MBID
    assert root.findtext("isni") == ISNI
    assert [node.text for node in root.findall("genre")] == ["blues", "blues rock"]


def test_a_person_is_born_and_a_group_is_formed() -> None:
    """Writing the wrong one makes a person look like a band in the interface."""
    person = ET.fromstring(
        render_artist_nfo(
            Row(name="Joe Bonamassa", image_url=None),
            ArtistMetadata(artist_id="a1", artist_type="Person", life_span_begin="1977-05-08"),
        )
    )
    group = ET.fromstring(
        render_artist_nfo(
            Row(name="Fleetwood Mac", image_url=None),
            ArtistMetadata(artist_id="a2", artist_type="Group", life_span_begin="1967"),
        )
    )

    assert person.findtext("born") == "1977-05-08" and person.find("formed") is None
    assert group.findtext("formed") == "1967" and group.find("born") is None


def test_a_biography_never_appears_without_its_attribution() -> None:
    """Wikipedia text is CC BY-SA. The source link is a licence term, not a nicety."""
    meta = ArtistMetadata(
        artist_id="a1",
        bio="An American blues rock guitarist.",
        bio_source_url="https://en.wikipedia.org/wiki/Joe_Bonamassa",
        bio_licence="CC BY-SA 4.0",
    )

    node = ET.fromstring(
        render_artist_nfo(Row(name="Joe Bonamassa", image_url=None), meta)
    ).find("biography")

    assert node is not None
    assert node.get("source") == "https://en.wikipedia.org/wiki/Joe_Bonamassa"
    assert node.get("licence") == "CC BY-SA 4.0"


def test_album_nfo_parses_and_carries_the_release_identity() -> None:
    meta = AlbumMetadata(
        album_id="al1",
        mb_release_mbid=RELEASE_MBID,
        mb_release_group_mbid=GROUP_MBID,
        barcode="0804879535645",
        catalog_number="JRA-2016",
        country="XW",
        status="Official",
        genres="blues rock",
        secondary_types="Live",
        first_release_date="2016-03-25",
    )
    tracks = [make_track(id="t1", title="This Train", duration=245)]

    root = ET.fromstring(
        render_album_nfo(
            make_album(),
            meta,
            tracks,
            {"t1": TrackMetadata(track_id="t1", mb_recording_mbid=RECORDING_MBID)},
            artist_name="Joe Bonamassa",
            artist_meta=ArtistMetadata(artist_id="a1", mb_artist_mbid=ARTIST_MBID),
        )
    )

    assert root.tag == "album"
    assert root.findtext("musicbrainzalbumid") == RELEASE_MBID
    assert root.findtext("musicbrainzreleasegroupid") == GROUP_MBID
    assert root.findtext("musicBrainzArtistID") == ARTIST_MBID
    assert root.findtext("barcode") == "0804879535645"
    assert root.findtext("catalognumber") == "JRA-2016"
    assert root.findtext("style") == "Live"
    track = root.find("track")
    assert track is not None
    assert track.findtext("musicBrainzTrackID") == RECORDING_MBID
    assert track.findtext("duration") == "4:05"


def test_an_unenriched_album_still_renders_valid_xml() -> None:
    root = ET.fromstring(render_album_nfo(make_album(), None, []))

    assert root.findtext("title") == "Blues Of Desperation"
    assert root.find("musicbrainzalbumid") is None


def test_empty_values_are_omitted_rather_than_written_blank() -> None:
    """A media server reads ``<genre></genre>`` as a genre called nothing."""
    xml = render_artist_nfo(Row(name="Nobody", image_url=None), None)

    assert "<genre" not in xml
    assert "<isni" not in xml


def test_the_output_is_a_declared_utf8_document() -> None:
    xml = render_artist_nfo(Row(name="Thorbjørn Risager", image_url=None), None)

    assert xml.startswith('<?xml version="1.0" encoding="UTF-8"')
    assert "Thorbjørn" in xml
    assert xml.endswith("\n")


# ---------------------------------------------------------------------------
# Merging with what a media server already wrote
# ---------------------------------------------------------------------------
# Shaped like a real Jellyfin/Emby album.nfo, because that is what a real
# library contains: a biography and ids from TheAudioDB, artwork paths, a
# tracklist and a dateadded — none of which Qobuzarr knows.
JELLYFIN_ALBUM = """<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<album>
  <review />
  <lockdata>false</lockdata>
  <dateadded>2026-01-07 14:56:35</dateadded>
  <title>Strong Persuader</title>
  <year>1986</year>
  <runtime>39</runtime>
  <genre>Blues</genre>
  <audiodbartistid>127230</audiodbartistid>
  <audiodbalbumid>2198633</audiodbalbumid>
  <musicbrainzalbumid>798857c7-0305-40dc-9e77-bc9352721654</musicbrainzalbumid>
  <musicbrainzalbumartistid>9adffe09-8bea-4f08-a409-923ecb3d029d</musicbrainzalbumartistid>
  <art>
    <poster>/data/music/Robert Cray/Strong Persuader/folder.jpg</poster>
  </art>
  <track>
    <position>1</position>
    <title>Smoking Gun</title>
  </track>
</album>
"""


def merged(existing: str, **kwargs: Any) -> ET.Element:
    from app.core.nfo import merge_nfo

    generated = render_album_nfo(
        make_album(title="Strong Persuader"),
        kwargs.get("meta"),
        kwargs.get("tracks", []),
        artist_meta=kwargs.get("artist_meta"),
    )
    return ET.fromstring(merge_nfo(existing, generated))


def test_merging_keeps_everything_qobuzarr_does_not_know() -> None:
    """A wholesale rewrite would silently destroy a library's existing metadata."""
    root = merged(JELLYFIN_ALBUM, meta=AlbumMetadata(album_id="al1", barcode="0075021251021"))

    assert root.findtext("audiodbartistid") == "127230"
    assert root.findtext("dateadded") == "2026-01-07 14:56:35"
    assert root.findtext("runtime") == "39"
    assert root.findtext("art/poster") == "/data/music/Robert Cray/Strong Persuader/folder.jpg"


def test_merging_updates_the_facts_qobuzarr_does_know() -> None:
    root = merged(
        JELLYFIN_ALBUM,
        meta=AlbumMetadata(
            album_id="al1", mb_release_mbid=RELEASE_MBID, barcode="0075021251021"
        ),
    )

    assert root.findtext("musicbrainzalbumid") == RELEASE_MBID, "ours wins"
    assert root.findtext("barcode") == "0075021251021", "and new facts are added"


def test_merging_updates_the_spelling_already_in_the_file() -> None:
    """Kodi says musicBrainzArtistID, Jellyfin says musicbrainzalbumartistid.

    Adding the other spelling would leave two elements claiming the same fact.
    """
    root = merged(
        JELLYFIN_ALBUM,
        artist_meta=ArtistMetadata(artist_id="a1", mb_artist_mbid=ARTIST_MBID),
    )

    assert root.findtext("musicbrainzalbumartistid") == ARTIST_MBID
    assert root.find("musicBrainzArtistID") is None


def test_merging_leaves_a_tracklist_alone_when_we_have_none() -> None:
    """An album adopted from disk has no Track rows; theirs is better than nothing."""
    root = merged(JELLYFIN_ALBUM)

    assert root.findtext("track/title") == "Smoking Gun"


def test_our_tracklist_replaces_theirs_when_we_have_one() -> None:
    root = merged(
        JELLYFIN_ALBUM,
        tracks=[make_track(id="t1", title="This Train", duration=245)],
    )

    titles = [node.findtext("title") for node in root.findall("track")]
    assert titles == ["This Train"], "replaced wholesale, not interleaved"


def test_a_locked_file_is_not_touched_at_all() -> None:
    """``lockdata`` is how someone tells their server to stop editing a file."""
    from app.core.nfo import NfoLocked, merge_nfo

    locked = JELLYFIN_ALBUM.replace(
        "<lockdata>false</lockdata>", "<lockdata>true</lockdata>"
    )

    with pytest.raises(NfoLocked):
        merge_nfo(locked, render_album_nfo(make_album(), None, []))


def test_an_unparseable_existing_file_is_replaced() -> None:
    """There is nothing to preserve, and leaving it broken helps nobody."""
    from app.core.nfo import merge_nfo

    result = merge_nfo("<album><not closed", render_album_nfo(make_album(), None, []))

    assert ET.fromstring(result).findtext("title") == "Blues Of Desperation"


def test_a_mismatched_document_is_replaced_rather_than_merged() -> None:
    from app.core.nfo import merge_nfo

    result = merge_nfo(
        "<artist><name>Someone</name></artist>", render_album_nfo(make_album(), None, [])
    )

    assert ET.fromstring(result).tag == "album"


def test_no_existing_file_just_writes_ours() -> None:
    from app.core.nfo import merge_nfo

    generated = render_album_nfo(make_album(), None, [])
    assert merge_nfo(None, generated) == generated
    assert merge_nfo("   ", generated) == generated


# ---------------------------------------------------------------------------
# Writing NFOs into a library
# ---------------------------------------------------------------------------
@pytest.fixture(name="library")
def library_fixture(tmp_path: Path) -> Iterator[tuple[Any, Settings, Path]]:
    root = tmp_path / "music"
    album_dir = root / "Joe Bonamassa" / "Blues Of Desperation (2016)"
    album_dir.mkdir(parents=True)
    (album_dir / "01 - This Train.flac").write_bytes(b"audio")

    settings = Settings(
        qobuz_app_id="a",
        qobuz_user_auth_token="b",
        library_path=str(root),
        data_path=str(tmp_path / "data"),
    )
    settings.ensure_directories()

    engine = create_async_engine(
        "sqlite+aiosqlite://", connect_args={"check_same_thread": False}, poolclass=StaticPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id="a1", name="Joe Bonamassa"))
            session.add(
                Album(
                    id="al1",
                    artist_id="a1",
                    title="Blues Of Desperation",
                    release_date=date(2016, 3, 25),
                    tracks_count=1,
                    status=AlbumStatus.DOWNLOADED,
                    path=str(album_dir),
                )
            )
            session.add(
                Track(
                    id="t1",
                    album_id="al1",
                    title="This Train",
                    track_number=1,
                    media_number=1,
                    duration=245,
                    status=TrackStatus.DOWNLOADED,
                    path=str(album_dir / "01 - This Train.flac"),
                )
            )
            session.add(
                ArtistMetadata(artist_id="a1", mb_artist_mbid=ARTIST_MBID, isni=ISNI)
            )
            session.add(
                AlbumMetadata(album_id="al1", mb_release_mbid=RELEASE_MBID)
            )
            await session.commit()

    asyncio.run(setup())

    @asynccontextmanager
    async def factory() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    try:
        yield factory, settings, album_dir
    finally:
        asyncio.run(engine.dispose())


def run_write(library: tuple[Any, Settings, Path]) -> Any:
    factory, settings, _ = library

    async def go() -> Any:
        async with factory() as session:
            album = await session.get(Album, "al1")
            return await write_nfo(session, album, settings=settings)

    return asyncio.run(go())


def test_both_files_land_in_the_right_folders(
    library: tuple[Any, Settings, Path]
) -> None:
    _, _, album_dir = library

    result = run_write(library)

    assert result.album_written and result.artist_written
    assert (album_dir / ALBUM_NFO).is_file()
    assert (album_dir.parent / ARTIST_NFO).is_file(), "the artist folder, one level up"


def test_what_lands_is_parseable_and_carries_the_identifiers(
    library: tuple[Any, Settings, Path]
) -> None:
    _, _, album_dir = library
    run_write(library)

    album = ET.parse(album_dir / ALBUM_NFO).getroot()
    artist = ET.parse(album_dir.parent / ARTIST_NFO).getroot()

    assert album.findtext("musicbrainzalbumid") == RELEASE_MBID
    assert album.findtext("track/title") == "This Train"
    assert artist.findtext("isni") == ISNI


def test_writing_twice_is_idempotent(library: tuple[Any, Settings, Path]) -> None:
    _, _, album_dir = library
    run_write(library)
    first = (album_dir / ALBUM_NFO).read_text()

    run_write(library)

    assert (album_dir / ALBUM_NFO).read_text() == first
    assert not list(album_dir.glob(".*.tmp")), "the temp file is cleaned up"


def test_the_audio_is_left_alone(library: tuple[Any, Settings, Path]) -> None:
    _, _, album_dir = library
    audio = album_dir / "01 - This Train.flac"
    before = audio.read_bytes()

    run_write(library)

    assert audio.read_bytes() == before


def test_switching_nfo_off_writes_nothing(
    library: tuple[Any, Settings, Path]
) -> None:
    factory, settings, album_dir = library
    off = settings.model_copy(update={"nfo_enabled": False})

    async def go() -> Any:
        async with factory() as session:
            album = await session.get(Album, "al1")
            return await write_nfo(session, album, settings=off)

    result = asyncio.run(go())

    assert result.skipped
    assert not (album_dir / ALBUM_NFO).exists()


def test_a_busy_album_is_refused(library: tuple[Any, Settings, Path]) -> None:
    """A folder the download loop is writing into is not one to drop files in."""
    from app.core.librarian import LibraryBusyError

    factory, settings, album_dir = library

    async def go() -> None:
        async with factory() as session:
            album = await session.get(Album, "al1")
            album.status = AlbumStatus.DOWNLOADING
        async with factory() as session:
            album = await session.get(Album, "al1")
            await write_nfo(session, album, settings=settings)

    with pytest.raises(LibraryBusyError):
        asyncio.run(go())
    assert not (album_dir / ALBUM_NFO).exists()


def test_an_album_with_no_path_is_skipped_not_failed(
    library: tuple[Any, Settings, Path]
) -> None:
    factory, settings, _ = library

    async def go() -> Any:
        async with factory() as session:
            album = await session.get(Album, "al1")
            album.path = None
        async with factory() as session:
            album = await session.get(Album, "al1")
            return await write_nfo(session, album, settings=settings)

    assert asyncio.run(go()).skipped == "nothing on disk"


def test_an_existing_media_server_nfo_survives_a_write(
    library: tuple[Any, Settings, Path]
) -> None:
    """The end-to-end version of the merge: nothing on disk is lost."""
    _, _, album_dir = library
    (album_dir / ALBUM_NFO).write_text(JELLYFIN_ALBUM, encoding="utf-8")

    run_write(library)

    root = ET.parse(album_dir / ALBUM_NFO).getroot()
    assert root.findtext("audiodbalbumid") == "2198633", "theirs preserved"
    assert root.findtext("art/poster", "").endswith("folder.jpg")
    assert root.findtext("musicbrainzalbumid") == RELEASE_MBID, "ours applied"


def test_a_locked_nfo_on_disk_is_left_byte_for_byte(
    library: tuple[Any, Settings, Path]
) -> None:
    _, _, album_dir = library
    locked = JELLYFIN_ALBUM.replace(
        "<lockdata>false</lockdata>", "<lockdata>true</lockdata>"
    )
    (album_dir / ALBUM_NFO).write_text(locked, encoding="utf-8")

    result = run_write(library)

    assert (album_dir / ALBUM_NFO).read_text(encoding="utf-8") == locked
    assert result.album_written is False
    assert result.skipped


def test_a_utf8_bom_written_by_a_media_server_is_handled(
    library: tuple[Any, Settings, Path]
) -> None:
    """Jellyfin writes a BOM; reading it as plain UTF-8 would corrupt the first tag."""
    _, _, album_dir = library
    (album_dir / ALBUM_NFO).write_text("﻿" + JELLYFIN_ALBUM, encoding="utf-8")

    run_write(library)

    root = ET.parse(album_dir / ALBUM_NFO).getroot()
    assert root.findtext("audiodbalbumid") == "2198633"


def test_an_unchanged_file_is_not_rewritten(
    library: tuple[Any, Settings, Path]
) -> None:
    """A stable mtime means a media server is not told to re-scan for nothing."""
    _, _, album_dir = library
    run_write(library)
    before = (album_dir / ALBUM_NFO).stat().st_mtime_ns

    run_write(library)

    assert (album_dir / ALBUM_NFO).stat().st_mtime_ns == before


def test_the_library_sweep_covers_every_release(
    library: tuple[Any, Settings, Path]
) -> None:
    factory, settings, album_dir = library

    async def go() -> Any:
        async with factory() as session:
            return await write_library_nfo(session, settings=settings)

    results = asyncio.run(go())

    assert len(results) == 1 and results[0].album_written
    assert (album_dir / ALBUM_NFO).is_file()


# ---------------------------------------------------------------------------
# Re-tagging a library nobody downloaded through Qobuzarr
# ---------------------------------------------------------------------------
@pytest.fixture(name="adopted")
def adopted_fixture(tmp_path: Path) -> Iterator[tuple[Any, Settings, Path]]:
    """An album the disk scan adopted: files on disk, and **no Track rows**.

    Which is what most of a pre-existing library looks like — only the download
    loop writes track rows — and therefore what the re-tag sweep has to work on.
    """
    root = tmp_path / "music"
    album_dir = root / "Joe Bonamassa" / "Blues Of Desperation (2016)"
    album_dir.mkdir(parents=True)
    for number, title in enumerate(("This Train", "Mountain Climbing"), start=1):
        path = write_flac(album_dir / f"0{number} - {title}.flac")
        audio = FLAC(str(path))
        audio["title"] = [title]
        audio["tracknumber"] = [str(number)]
        audio.save()

    settings = Settings(
        qobuz_app_id="a",
        qobuz_user_auth_token="b",
        library_path=str(root),
        data_path=str(tmp_path / "data"),
    )
    settings.ensure_directories()

    engine = create_async_engine(
        "sqlite+aiosqlite://", connect_args={"check_same_thread": False}, poolclass=StaticPool
    )
    maker = async_sessionmaker(engine, expire_on_commit=False, autoflush=False)

    async def setup() -> None:
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        async with maker() as session:
            session.add(Artist(id="a1", name="Joe Bonamassa"))
            session.add(
                Album(
                    id="al1",
                    artist_id="a1",
                    title="Blues Of Desperation",
                    release_date=date(2016, 3, 25),
                    tracks_count=2,
                    status=AlbumStatus.DOWNLOADED,
                    path=str(album_dir),
                )
            )
            session.add(
                ArtistMetadata(artist_id="a1", mb_artist_mbid=ARTIST_MBID, isni=ISNI)
            )
            session.add(AlbumMetadata(album_id="al1", mb_release_mbid=RELEASE_MBID))
            await session.commit()

    asyncio.run(setup())

    @asynccontextmanager
    async def factory() -> AsyncIterator[AsyncSession]:
        session = maker()
        try:
            yield session
            await session.commit()
        finally:
            await session.close()

    try:
        yield factory, settings, album_dir
    finally:
        asyncio.run(engine.dispose())


def run_retag(fixture: tuple[Any, Settings, Path]) -> Any:
    factory, settings, _ = fixture

    async def go() -> Any:
        async with factory() as session:
            album = await session.get(Album, "al1")
            return await retag_album(session, album, settings=settings)

    return asyncio.run(go())


def test_retag_reaches_files_that_have_no_track_rows(
    adopted: tuple[Any, Settings, Path]
) -> None:
    """It reported "0 tagged, 0 failed, 0 missing" and called that success, on
    every album a pre-existing library actually contains."""
    result = run_retag(adopted)

    assert result.tagged == 2
    assert result.failed == 0


def test_an_adopted_file_gains_the_release_identity(
    adopted: tuple[Any, Settings, Path]
) -> None:
    _, _, album_dir = adopted

    run_retag(adopted)

    audio = FLAC(str(album_dir / "01 - This Train.flac"))
    assert audio["musicbrainz_albumid"] == [RELEASE_MBID]
    assert audio["musicbrainz_albumartistid"] == [ARTIST_MBID]
    assert audio["album"] == ["Blues Of Desperation"]


def test_an_adopted_file_keeps_its_own_track_level_values(
    adopted: tuple[Any, Settings, Path]
) -> None:
    """The database has no opinion about track two's title — the file does, and
    inventing one from a row that does not exist is how a library gets scrambled."""
    _, _, album_dir = adopted

    run_retag(adopted)

    audio = FLAC(str(album_dir / "02 - Mountain Climbing.flac"))
    assert audio["title"] == ["Mountain Climbing"]
    assert audio["tracknumber"] == ["2"]
    assert "musicbrainz_trackid" not in audio, "no recording was matched to it"
