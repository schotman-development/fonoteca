"""The published tag map, and the closure that keeps it honest.

``MetaOut.tag_map`` exists to draw the Structure & tags table without anybody
hand-writing one, so the only thing worth testing is that it stays *derived*:
every tag any container writes has exactly one origin, every origin is written by
some container, and a change to the tagger shows up on the screen without a
second edit. A table maintained beside the tagger is worse than no table — it
keeps describing the program as it used to be, and nothing fails when it does.

The two regressions on the refactor live here too, because there is no
``test_tagger.py`` or ``test_nfo.py`` to put them in: the FLAC path must still
write exactly the fields it wrote when the dict was spelled out inline, and
``nfo.tag_values()`` must still return the same dict now that it is a loop.
"""

from __future__ import annotations

import inspect
from pathlib import Path

import pytest
from mutagen.flac import FLAC
from mutagen.id3 import ID3

from app.api.deps import build_consensus_rule, build_meta, build_tag_map
from app.core import nfo, tagger
from app.enrich import merge
from app.models import AlbumMetadata, ArtistMetadata, TrackMetadata

from tests.test_integrity import write_flac, write_mp3


# ---------------------------------------------------------------------------
# The closure
# ---------------------------------------------------------------------------
def _written_keys() -> set[str]:
    """Every logical tag key some container actually writes."""
    keys = {key for _field, key in tagger.VORBIS_FIELDS}
    keys |= {key for _cls, _frame, key in tagger.ID3_FRAMES}
    keys |= {key for _description, key in tagger.TXXX_TAGS}
    # The two ID3 specials, written by name rather than through a table.
    keys |= {"tracknumber", "totaltracks", "discnumber", "totaldiscs"}
    keys |= {"originaldate", "musicbrainz_trackid"}
    return keys


def _explained_keys() -> set[str]:
    """Every logical tag key something claims to be the source of."""
    return (
        {key for key, _source in tagger.QOBUZ_TAG_FIELDS}
        | {key for key, _entity, _column in nfo.ENRICHMENT_TAGS}
        | {key for key, _from in nfo.DERIVED_TAGS}
    )


def test_every_written_tag_has_an_origin() -> None:
    """Two-way closure: nothing written is unexplained, nothing explained unused.

    This is the whole anti-drift argument. A key on the left with no entry on the
    right is a blank cell on the screen (and a ``KeyError`` out of
    ``build_tag_map``); a key on the right with nothing writing it is a row about
    a tag no file ever gets.
    """
    assert _written_keys() - _explained_keys() == set()
    assert _explained_keys() - _written_keys() == set()


def test_row_order_matches_the_write_order() -> None:
    rows = build_meta().tag_map
    assert [row.vorbis_field for row in rows] == [f for f, _ in tagger.VORBIS_FIELDS]


def test_a_new_vorbis_field_appears_without_touching_the_table(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Adding a field to the tagger adds a row here, with no second edit."""
    monkeypatch.setattr(
        tagger,
        "VORBIS_FIELDS",
        tagger.VORBIS_FIELDS + (("MUSICBRAINZ_WORKID", "barcode"),),
    )
    row = next(r for r in build_meta().tag_map if r.vorbis_field == "MUSICBRAINZ_WORKID")
    assert row.tag_key == "barcode"
    assert row.origin == "enrichment"
    assert row.source_field == "album_metadata.barcode (falls back to albums.upc)"


def test_a_tag_with_no_origin_is_a_loud_failure(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A blank cell is the failure mode this refuses; a KeyError is not silent."""
    monkeypatch.setattr(
        tagger, "VORBIS_FIELDS", tagger.VORBIS_FIELDS + (("REPLAYGAIN", "loudness"),)
    )
    with pytest.raises(KeyError):
        build_tag_map()


# ---------------------------------------------------------------------------
# What wins a conflict
# ---------------------------------------------------------------------------
def test_label_and_genre_are_never_overwritten() -> None:
    rows = {row.vorbis_field: row for row in build_meta().tag_map}
    for field in ("LABEL", "ORGANIZATION", "GENRE"):
        assert rows[field].conflict == "never"
        assert "naming-template token" in rows[field].conflict_note
    assert build_consensus_rule().never_writable == ["genre", "label"]


def test_no_row_is_consensus_today() -> None:
    """The honest current state, pinned so it cannot change unnoticed.

    ``WRITABLE_FIELDS`` is ``{"release_type"}``, which is an ``Album`` column and
    not a tag key — the ``RELEASETYPE`` Vorbis field comes from
    ``album_metadata.primary_type``. So consensus governs no row of this table,
    and a table claiming otherwise would be fiction. If somebody adds a tag key
    to ``WRITABLE_FIELDS``, this fails and the column starts telling the truth
    about it by itself.
    """
    assert not (
        merge.WRITABLE_FIELDS & {key for _field, key in tagger.VORBIS_FIELDS}
    )
    assert all(row.conflict != "consensus" for row in build_meta().tag_map)


def test_a_new_writable_field_changes_the_conflict_column(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(merge, "NEVER_WRITABLE", frozenset())
    monkeypatch.setattr(merge, "WRITABLE_FIELDS", merge.WRITABLE_FIELDS | {"genre"})
    meta = build_meta()
    row = next(r for r in meta.tag_map if r.vorbis_field == "GENRE")
    assert row.conflict == "consensus"
    assert "more than half" in row.conflict_note
    assert "genre" in meta.consensus_rule.writable_fields


def test_threshold_is_read_from_consensus_itself() -> None:
    rule = build_consensus_rule()
    default = inspect.signature(merge.consensus).parameters["threshold"].default
    assert rule.threshold == pytest.approx(0.51)
    assert rule.threshold == pytest.approx(default)
    assert rule.tie_keeps == "qobuz"


# ---------------------------------------------------------------------------
# The ID3 half, derived rather than restated
# ---------------------------------------------------------------------------
def test_id3_frames_are_derived() -> None:
    assert tagger.id3_frame_for("musicbrainz_albumid") == "TXXX:MusicBrainz Album Id"
    assert tagger.id3_frame_for("musicbrainz_trackid") == "UFID:http://musicbrainz.org"
    assert tagger.id3_frame_for("totaltracks") == "TRCK (number/total)"
    assert tagger.id3_frame_for("title") == "TIT2"
    assert tagger.id3_frame_for("version") is None


def test_the_row_carries_whatever_mp3_gets() -> None:
    rows = {row.vorbis_field: row for row in build_meta().tag_map}
    assert rows["VERSION"].id3_frame is None
    assert rows["ISNI"].id3_frame == "TXXX:ISNI"
    assert rows["MUSICBRAINZ_TRACKID"].id3_frame == "UFID:http://musicbrainz.org"
    assert rows["ORIGINALDATE"].id3_frame == "TDOR"


def test_a_truncation_promises_no_id3_frame() -> None:
    """MP3 gets the whole date in TDOR and nothing carrying only the year.

    ``ORIGINALYEAR``'s tag key is ``originaldate``, so deriving its frame from
    the key alone would name TDOR and promise a cell an MP3 never has. The
    ``ORIGINALDATE`` row states the same fact truthfully.
    """
    rows = {row.vorbis_field: row for row in build_meta().tag_map}
    assert rows["ORIGINALYEAR"].id3_frame is None
    assert rows["ORIGINALYEAR"].origin == "derived"
    assert rows["ORIGINALDATE"].id3_frame == "TDOR"


def test_the_mp3_writer_and_the_published_frames_agree(tmp_path: Path) -> None:
    """Not a restated list: the frames written are the frames published.

    ``id3_frame_for`` reads the very tuples ``_tag_mp3`` writes from, so this
    walks a real file rather than trusting that. Every frame on disk must be one
    the table names for some tag key, and every key the table gives a plain
    frame id must be on the file.
    """
    path = write_mp3(tmp_path / "04 - Sultans of Swing.mp3")
    extra = {
        "originaldate": "1978-10-07",
        "musicbrainz_albumid": "abc-123",
        "musicbrainz_trackid": "rec-456",
        "isni": "0000",
    }
    assert tagger.tag_file(path, _Track(), _Album(), extra_tags=extra) is True

    written = {str(key).split(":")[0] for key in ID3(str(path)).keys()}
    published = {
        frame.split(" ")[0].split(":")[0]
        for frame in (tagger.id3_frame_for(key) for key in _written_keys())
        if frame is not None
    }
    # APIC is cover art, which no tag key names.
    assert written - {"APIC"} <= published
    assert {"TIT2", "TALB", "TRCK", "TPOS", "TDOR", "TXXX", "UFID"} <= written


# ---------------------------------------------------------------------------
# Regressions on the refactor
# ---------------------------------------------------------------------------
class _Track:
    id = "1"
    title = "Sultans of Swing"
    version = ""
    performer = "Dire Straits"
    isrc = "GBF017800123"
    composer = "Mark Knopfler"
    track_number = 4
    media_number = 1


class _Album:
    title = "Dire Straits"
    version = ""
    release_date = None
    year = 1978
    tracks_count = 9
    media_count = 1
    genre = "Rock"
    label = "Vertigo"
    upc = "0602498672600"
    artist = None


def test_flac_still_writes_exactly_the_published_fields(tmp_path: Path) -> None:
    """The dict ``_tag_flac`` builds from VORBIS_FIELDS is the old one."""
    path = write_flac(tmp_path / "04 - Sultans of Swing.flac")
    extra = {"originaldate": "1978-10-07", "musicbrainz_albumid": "abc-123"}
    assert tagger.tag_file(path, _Track(), _Album(), extra_tags=extra) is True

    audio = FLAC(str(path))
    # Vorbis comment names are case-insensitive and mutagen reads them back
    # lower-cased; the published table spells them the way Picard writes them.
    written = {key.upper() for key in audio.keys()}
    assert written <= {field for field, _key in tagger.VORBIS_FIELDS}
    assert {"TITLE", "ARTIST", "ALBUM", "LABEL", "ORGANIZATION", "BARCODE"} <= written
    assert audio["ORGANIZATION"] == audio["LABEL"] == ["Vertigo"]
    assert audio["TRACKTOTAL"] == audio["TOTALTRACKS"] == ["9"]
    # The one field that is a slice rather than the value.
    assert audio["ORIGINALDATE"] == ["1978-10-07"]
    assert audio["ORIGINALYEAR"] == ["1978"]
    # Nothing empty ever reaches the file.
    assert "VERSION" not in written


def test_tag_values_is_unchanged_by_the_loop() -> None:
    """The golden dict, and the derived key that is not read from anywhere."""
    values = nfo.tag_values(
        artist_meta=ArtistMetadata(artist_id="1", mb_artist_mbid="artist-mbid", isni="0000"),
        album_meta=AlbumMetadata(
            album_id="a",
            mb_release_mbid="release-mbid",
            mb_release_group_mbid="group-mbid",
            catalog_number="JRA-2016",
            country="GB",
            status="Official",
            primary_type="album",
            barcode="0602498672600",
            first_release_date="1978-10-07",
        ),
        track_meta=TrackMetadata(
            track_id="t",
            mb_recording_mbid="recording-mbid",
            mb_release_track_mbid="track-mbid",
            acoustid="acoustid-id",
            isrc="GBF017800123",
        ),
    )
    assert values == {
        "musicbrainz_albumartistid": "artist-mbid",
        "isni": "0000",
        "musicbrainz_albumid": "release-mbid",
        "musicbrainz_releasegroupid": "group-mbid",
        "catalognumber": "JRA-2016",
        "releasecountry": "GB",
        "releasestatus": "Official",
        "releasetype": "album",
        "barcode": "0602498672600",
        "originaldate": "1978-10-07",
        "musicbrainz_trackid": "recording-mbid",
        "musicbrainz_releasetrackid": "track-mbid",
        "acoustid_id": "acoustid-id",
        "isrc": "GBF017800123",
        "musicbrainz_artistid": "artist-mbid",
    }


def test_the_artist_id_still_falls_back_to_the_album_artist() -> None:
    values = nfo.tag_values(
        artist_meta=ArtistMetadata(artist_id="1", mb_artist_mbid="artist-mbid")
    )
    assert values["musicbrainz_artistid"] == "artist-mbid"
    assert nfo.tag_values() == {}
