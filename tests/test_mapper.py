"""Unit tests for :mod:`app.qobuz.mapper`.

The fixtures below are trimmed copies of real payloads from the live Qobuz API,
deliberately covering the shapes that have bitten us:

* a **non-numeric album id** (``"uyej1o165e870"``) and a numeric-looking one with
  a leading zero (``"0884977859300"``) — both must survive as strings,
* an album with ``version``/``label``/``genre`` set to ``null``,
* a **multi-disc** album whose disc count is only discoverable from its tracks,
* the newer ``artist/getReleasesList`` shape (``dates``/``audio_info``/``rights``).
"""

from __future__ import annotations

from datetime import date

import pytest
from sqlalchemy import inspect as sa_inspect

from app.models import Album, Artist, Track
from app.qobuz.mapper import (
    album_track_items,
    classify_release_type,
    extract_album_artist,
    is_streamable,
    map_album,
    map_artist,
    map_track,
    parse_release_date,
    pick_image_url,
)

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
ARTIST_RAW = {
    "id": 26390,
    "name": "Nils Frahm",
    "slug": "nils-frahm",
    "albums_count": 37,
    "image": {
        "small": "https://static.qobuz.com/images/artists/covers/small/x.jpg",
        "medium": "https://static.qobuz.com/images/artists/covers/medium/x.jpg",
        "large": "https://static.qobuz.com/images/artists/covers/large/x.jpg",
    },
}

#: ``album/get`` shape, non-numeric id, deluxe version, single disc.
ALBUM_CLASSIC = {
    "id": "uyej1o165e870",
    "title": "All Melody",
    "version": "Deluxe Edition",
    "released_at": 1516924800,
    "release_date_original": "2018-01-26",
    "release_date_stream": "2018-01-26",
    "tracks_count": 12,
    "media_count": 1,
    "hires": True,
    "maximum_bit_depth": 24,
    "maximum_sampling_rate": 96.0,
    "upc": "5060281616784",
    "duration": 4437,
    "popularity": 12,
    "parental_warning": False,
    "streamable": True,
    "release_type": "album",
    "label": {"id": 12345, "name": "Erased Tapes"},
    "genre": {"id": 112, "name": "Electronic"},
    "artist": {"id": 26390, "name": "Nils Frahm", "slug": "nils-frahm"},
    "image": {
        "thumbnail": "https://static.qobuz.com/images/covers/50.jpg",
        "small": "https://static.qobuz.com/images/covers/230.jpg",
        "large": "https://static.qobuz.com/images/covers/600.jpg",
    },
    "tracks": {
        "limit": 50,
        "offset": 0,
        "total": 2,
        "items": [
            {
                "id": 41902718,
                "title": "The Whole Universe Wants to Be Touched",
                "version": None,
                "track_number": 1,
                "media_number": 1,
                "duration": 100,
                "isrc": "GBKQU1700101",
                "performer": {"id": 26390, "name": "Nils Frahm"},
                "composer": {"id": 26390, "name": "Nils Frahm"},
                "streamable": True,
            },
            {
                "id": 41902719,
                "title": "Sunson",
                "version": "Live",
                "track_number": 2,
                "media_number": 1,
                "duration": 537,
                "isrc": None,
                "performer": None,
                "composer": None,
                "streamable": True,
            },
        ],
    },
}

#: Sparse ``catalog/search`` hit: numeric-looking id with a leading zero and a
#: pile of nulls. Nothing here may raise, and nothing may become ``int``.
ALBUM_SPARSE = {
    "id": "0884977859300",
    "title": "Untitled Recordings",
    "version": None,
    "label": None,
    "genre": None,
    "upc": None,
    "image": None,
    "duration": None,
    "tracks_count": 9,
    "artist": {"id": 999111, "name": "Anonymous"},
}

#: Multi-disc album; ``media_count`` is absent and must come from the tracks.
ALBUM_MULTI_DISC = {
    "id": "qzs9dl3n5mhta",
    "title": "The Complete Sessions",
    "version": None,
    "release_date_original": "1997-11-04",
    "tracks_count": 4,
    "release_type": "album",
    "artist": {"id": 5511, "name": "Miles Davis"},
    "label": "Columbia",
    "genre": "Jazz",
    "tracks": {
        "items": [
            {"id": 1, "title": "A", "track_number": 1, "media_number": 1},
            {"id": 2, "title": "B", "track_number": 2, "media_number": 1},
            {"id": 3, "title": "C", "track_number": 1, "media_number": 2},
            {"id": 4, "title": "D", "track_number": 2, "media_number": 2},
        ]
    },
}

#: ``artist/getReleasesList`` shape — nested dates, audio_info and rights.
RELEASE_LIST_ITEM = {
    "id": "wxl78pvfqlm3b",
    "title": "Empty",
    "version": None,
    "tracks_count": 2,
    "release_type": "epSingle",
    "dates": {"download": "2021-04-16", "original": "2021-04-16", "stream": "2021-04-16"},
    "audio_info": {"maximum_bit_depth": 16, "maximum_sampling_rate": 44.1},
    "rights": {"streamable": True, "hires_streamable": False, "purchasable": True},
    "label": {"name": "LEITER"},
    "genre": {"name": "Classical"},
    "artist": {"id": 26390, "name": "Nils Frahm"},
    "image": {"large": "https://static.qobuz.com/images/covers/600b.jpg"},
    "duration": 610,
}


def _column_names(model: type) -> set[str]:
    """Mapped column names of an ORM class."""
    return {attr.key for attr in sa_inspect(model).mapper.column_attrs}


# ---------------------------------------------------------------------------
# map_artist
# ---------------------------------------------------------------------------
def test_map_artist_reads_every_field() -> None:
    mapped = map_artist(ARTIST_RAW)
    assert mapped == {
        "id": "26390",
        "name": "Nils Frahm",
        "image_url": "https://static.qobuz.com/images/artists/covers/large/x.jpg",
        "albums_count": 37,
        "qobuz_slug": "nils-frahm",
    }
    assert isinstance(mapped["id"], str)


def test_map_artist_survives_an_empty_payload() -> None:
    mapped = map_artist({})
    assert mapped["id"] is None
    assert mapped["name"] == "Unknown Artist"
    assert mapped["albums_count"] == 0
    assert mapped["image_url"] is None


def test_map_artist_keys_match_the_orm_columns() -> None:
    assert set(map_artist(ARTIST_RAW)) <= _column_names(Artist)
    Artist(**map_artist(ARTIST_RAW))


def test_map_artist_falls_back_to_the_albums_total() -> None:
    mapped = map_artist({"id": 7, "name": "X", "albums": {"total": 5, "items": []}})
    assert mapped["albums_count"] == 5


# ---------------------------------------------------------------------------
# map_album
# ---------------------------------------------------------------------------
def test_map_album_non_numeric_id_stays_a_string() -> None:
    mapped = map_album(ALBUM_CLASSIC, "26390")
    assert mapped["id"] == "uyej1o165e870"
    assert isinstance(mapped["id"], str)


def test_map_album_numeric_id_keeps_its_leading_zero() -> None:
    mapped = map_album(ALBUM_SPARSE, "999111")
    assert mapped["id"] == "0884977859300"
    assert mapped["id"].startswith("0")


def test_map_album_full_payload() -> None:
    mapped = map_album(ALBUM_CLASSIC, "26390")
    assert mapped["artist_id"] == "26390"
    assert mapped["title"] == "All Melody"
    assert mapped["version"] == "Deluxe Edition"
    assert mapped["release_date"] == date(2018, 1, 26)
    assert mapped["release_type"] == "album"
    assert mapped["tracks_count"] == 12
    assert mapped["media_count"] == 1
    assert mapped["hires"] is True
    assert mapped["max_bit_depth"] == 24
    assert mapped["max_sampling_rate"] == pytest.approx(96.0)
    assert mapped["label"] == "Erased Tapes"
    assert mapped["genre"] == "Electronic"
    assert mapped["upc"] == "5060281616784"
    assert mapped["image_url"].endswith("600.jpg")
    assert mapped["duration"] == 4437


def test_map_album_null_label_genre_and_version() -> None:
    mapped = map_album(ALBUM_SPARSE)
    assert mapped["version"] is None
    assert mapped["label"] is None
    assert mapped["genre"] is None
    assert mapped["upc"] is None
    assert mapped["image_url"] is None
    assert mapped["duration"] is None
    assert mapped["release_date"] is None
    assert mapped["max_bit_depth"] is None
    assert mapped["max_sampling_rate"] is None
    assert mapped["hires"] is False
    # The artist is discovered from the payload when none is supplied.
    assert mapped["artist_id"] == "999111"


def test_map_album_multi_disc_count_inferred_from_tracks() -> None:
    mapped = map_album(ALBUM_MULTI_DISC, "5511")
    assert mapped["media_count"] == 2
    assert Album(**mapped).is_multi_disc is True


def test_map_album_single_disc_is_not_multi_disc() -> None:
    assert Album(**map_album(ALBUM_CLASSIC, "26390")).is_multi_disc is False


def test_map_album_accepts_scalar_label_and_genre() -> None:
    mapped = map_album(ALBUM_MULTI_DISC, "5511")
    assert mapped["label"] == "Columbia"
    assert mapped["genre"] == "Jazz"


def test_map_album_handles_the_releases_list_shape() -> None:
    mapped = map_album(RELEASE_LIST_ITEM, "26390")
    assert mapped["id"] == "wxl78pvfqlm3b"
    assert mapped["release_date"] == date(2021, 4, 16)
    assert mapped["max_bit_depth"] == 16
    assert mapped["max_sampling_rate"] == pytest.approx(44.1)
    assert mapped["hires"] is False
    assert mapped["label"] == "LEITER"
    assert mapped["genre"] == "Classical"
    assert mapped["release_type"] == "single"  # epSingle + 2 tracks


def test_map_album_keys_match_the_orm_columns() -> None:
    mapped = map_album(ALBUM_CLASSIC, "26390")
    assert set(mapped) <= _column_names(Album)
    album = Album(**mapped)
    assert album.year == 2018
    assert album.display_title == "All Melody (Deluxe Edition)"


def test_map_album_of_garbage_never_raises() -> None:
    for payload in ({}, None, {"id": None}, {"id": [], "title": {}}):
        mapped = map_album(payload)  # type: ignore[arg-type]
        assert set(mapped) <= _column_names(Album)
        assert mapped["media_count"] == 1
        assert mapped["tracks_count"] == 0


def test_extract_album_artist() -> None:
    assert extract_album_artist(ALBUM_CLASSIC) == {
        "id": "26390",
        "name": "Nils Frahm",
        "image_url": None,
        "albums_count": 0,
        "qobuz_slug": "nils-frahm",
    }
    assert extract_album_artist({"title": "orphan"}) is None
    assert extract_album_artist({"artists": [{"id": 5, "name": "B"}]})["id"] == "5"


# ---------------------------------------------------------------------------
# map_track
# ---------------------------------------------------------------------------
def test_map_track_full_payload() -> None:
    raw = album_track_items(ALBUM_CLASSIC)[0]
    mapped = map_track(raw, "uyej1o165e870")
    assert mapped == {
        "id": "41902718",
        "album_id": "uyej1o165e870",
        "title": "The Whole Universe Wants to Be Touched",
        "version": None,
        "track_number": 1,
        "media_number": 1,
        "duration": 100,
        "isrc": "GBKQU1700101",
        "performer": "Nils Frahm",
        "composer": "Nils Frahm",
    }


def test_map_track_with_null_performer_and_isrc() -> None:
    raw = album_track_items(ALBUM_CLASSIC)[1]
    mapped = map_track(raw, "uyej1o165e870")
    assert mapped["isrc"] is None
    assert mapped["performer"] is None
    assert mapped["composer"] is None
    assert mapped["version"] == "Live"


def test_map_track_defaults_and_album_fallback() -> None:
    mapped = map_track({"id": 7, "album": {"id": "abc", "artist": {"name": "Fallback"}}})
    assert mapped["album_id"] == "abc"
    assert mapped["track_number"] == 0
    assert mapped["media_number"] == 1
    assert mapped["title"] == "Unknown Track"
    assert mapped["performer"] == "Fallback"


def test_map_track_keys_match_the_orm_columns() -> None:
    mapped = map_track(album_track_items(ALBUM_MULTI_DISC)[2], "qzs9dl3n5mhta")
    assert set(mapped) <= _column_names(Track)
    track = Track(**mapped)
    assert track.media_number == 2
    # Download-state columns are untouched so re-mapping cannot lose progress.
    assert "status" not in mapped and "path" not in mapped and "format_id" not in mapped


# ---------------------------------------------------------------------------
# Dates
# ---------------------------------------------------------------------------
def test_parse_release_date_prefers_the_original_date() -> None:
    raw = {
        "release_date_original": "1997-11-04",
        "release_date_stream": "2011-01-01",
        "released_at": 1293840000,
    }
    assert parse_release_date(raw) == date(1997, 11, 4)


def test_parse_release_date_from_unix_timestamp() -> None:
    assert parse_release_date({"released_at": 1516924800}) == date(2018, 1, 26)
    assert parse_release_date({"released_at": "1516924800"}) == date(2018, 1, 26)


def test_parse_release_date_from_nested_dates() -> None:
    assert parse_release_date({"dates": {"original": "2021-04-16"}}) == date(2021, 4, 16)
    assert parse_release_date({"dates": {"stream": "2021-04-16"}}) == date(2021, 4, 16)


def test_parse_release_date_tolerates_rubbish() -> None:
    for raw in ({}, None, {"release_date_original": ""}, {"release_date_original": "soon"}):
        assert parse_release_date(raw) is None  # type: ignore[arg-type]


def test_parse_release_date_accepts_iso_datetimes() -> None:
    assert parse_release_date({"release_date": "2019-06-07T00:00:00Z"}) == date(2019, 6, 7)


# ---------------------------------------------------------------------------
# classify_release_type
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ({"release_type": "album", "tracks_count": 12}, "album"),
        ({"release_type": "epSingle", "tracks_count": 1}, "single"),
        ({"release_type": "epSingle", "tracks_count": 3}, "single"),
        ({"release_type": "epSingle", "tracks_count": 6}, "ep"),
        ({"release_type": "ep", "tracks_count": 2}, "ep"),
        ({"release_type": "live", "tracks_count": 20}, "live"),
        ({"release_type": "compilation", "tracks_count": 40}, "compilation"),
        ({"release_type": "download", "tracks_count": 4}, "download"),
        ({"release_type": "bundle", "tracks_count": 4}, "other"),
        ({"tracks_count": 2}, "single"),
        ({"tracks_count": 5}, "ep"),
        ({"tracks_count": 11}, "album"),
        ({}, "album"),
    ],
)
def test_classify_release_type(raw: dict, expected: str) -> None:
    assert classify_release_type(raw) == expected


def test_classify_release_type_uses_product_type_as_a_fallback() -> None:
    assert classify_release_type({"product_type": "compilation", "tracks_count": 30}) == "compilation"


# ---------------------------------------------------------------------------
# Odds and ends
# ---------------------------------------------------------------------------
def test_pick_image_url_prefers_the_largest_variant() -> None:
    assert pick_image_url({"small": "s.jpg", "large": "l.jpg", "mega": "m.jpg"}) == "m.jpg"
    assert pick_image_url({"thumbnail": "t.jpg"}) == "t.jpg"
    assert pick_image_url("direct.jpg") == "direct.jpg"
    assert pick_image_url({"portrait": {"large": "p.jpg"}}) == "p.jpg"
    assert pick_image_url(None) is None
    assert pick_image_url({}) is None


def test_is_streamable() -> None:
    assert is_streamable(ALBUM_CLASSIC) is True
    assert is_streamable({"streamable": False}) is False
    assert is_streamable({"rights": {"streamable": False}}) is False
    assert is_streamable({}) is True  # unknown means "let getFileUrl decide"


def test_album_track_items_handles_both_shapes() -> None:
    assert len(album_track_items(ALBUM_CLASSIC)) == 2
    assert len(album_track_items({"tracks": [{"id": 1}, "junk"]})) == 1
    assert album_track_items({}) == []
