"""Unit tests for :mod:`app.core.naming`.

These cover the cases that actually bite in a music library: slashes in artist
names (``AC/DC``), unicode titles, Windows-hostile trailing dots, absurdly long
titles that must still keep their extension, multi-disc box sets, releases with
no known year, and the Qobuz ``version`` suffix that distinguishes two editions
of the same record.

Everything here is pure — no database, no network, no filesystem.
"""

from __future__ import annotations

from datetime import date
from pathlib import Path, PurePosixPath

import pytest

from app.config import Settings
from app.core.naming import (
    MAX_COMPONENT_BYTES,
    disc_prefix,
    extension_for_format,
    quality_tag,
    render_album_dir,
    render_template,
    render_track_name,
    render_track_path,
    sanitise_component,
    upgrade_image_url,
)
from app.models import Album, Artist, Track

LIBRARY = Path("/srv/music")


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------
@pytest.fixture()
def settings() -> Settings:
    """Settings with a predictable library root and the default template."""
    return Settings(library_path=LIBRARY)


def make_artist(name: str = "Nils Frahm") -> Artist:
    """A minimal in-memory artist row (never persisted)."""
    return Artist(id="26390", name=name)


def make_album(
    *,
    title: str = "All Melody",
    version: str | None = None,
    release_date: date | None = date(2018, 1, 26),
    media_count: int = 1,
    tracks_count: int = 12,
    max_bit_depth: int | None = 24,
    max_sampling_rate: float | None = 96.0,
) -> Album:
    """A minimal in-memory album row (never persisted)."""
    return Album(
        id="uyej1o165e870",
        artist_id="26390",
        title=title,
        version=version,
        release_date=release_date,
        media_count=media_count,
        tracks_count=tracks_count,
        max_bit_depth=max_bit_depth,
        max_sampling_rate=max_sampling_rate,
    )


def make_track(
    *,
    title: str = "Sunson",
    version: str | None = None,
    track_number: int = 3,
    media_number: int = 1,
) -> Track:
    """A minimal in-memory track row (never persisted)."""
    return Track(
        id="64868955",
        album_id="uyej1o165e870",
        title=title,
        version=version,
        track_number=track_number,
        media_number=media_number,
    )


# ---------------------------------------------------------------------------
# sanitise_component
# ---------------------------------------------------------------------------
class TestSanitiseComponent:
    """The POSIX-safety layer."""

    def test_replaces_slashes(self, settings: Settings) -> None:
        assert sanitise_component("AC/DC", settings=settings) == "AC_DC"

    def test_replaces_nul_and_control_characters(self, settings: Settings) -> None:
        assert sanitise_component("Bad\x00Name", settings=settings) == "Bad_Name"
        # Newlines and tabs are whitespace, so they fold into a single space.
        assert sanitise_component("Two\nLines", settings=settings) == "Two Lines"
        assert sanitise_component("Bell\x07Name", settings=settings) == "Bell Name"

    def test_preserves_unicode(self, settings: Settings) -> None:
        assert sanitise_component("Sigur Rós – Ágætis byrjun", settings=settings) == (
            "Sigur Rós – Ágætis byrjun"
        )
        assert sanitise_component("坂本龍一", settings=settings) == "坂本龍一"
        assert sanitise_component("Björk & Þeir", settings=settings) == "Björk & Þeir"

    def test_strips_trailing_dots_and_spaces(self, settings: Settings) -> None:
        assert sanitise_component("Album.  ", settings=settings) == "Album"
        assert sanitise_component("...Baby One More Time", settings=settings) == (
            "Baby One More Time"
        )

    def test_collapses_whitespace(self, settings: Settings) -> None:
        assert sanitise_component("  Too    many\tspaces ", settings=settings) == (
            "Too many spaces"
        )

    def test_empty_and_reserved_names_get_a_fallback(self, settings: Settings) -> None:
        assert sanitise_component("", settings=settings) == "Unknown"
        assert sanitise_component("   ", settings=settings) == "Unknown"
        assert sanitise_component(".", settings=settings) == "Unknown"
        assert sanitise_component("..", settings=settings) == "Unknown"
        assert sanitise_component(None, settings=settings) == "Unknown"

    def test_no_path_traversal_survives(self, settings: Settings) -> None:
        cleaned = sanitise_component("../../etc/passwd", settings=settings)
        assert "/" not in cleaned
        assert not cleaned.startswith(".")

    def test_truncates_long_names(self, settings: Settings) -> None:
        long_title = "A" * 500
        cleaned = sanitise_component(long_title, max_length=120, settings=settings)
        assert len(cleaned) == 120

    def test_truncation_preserves_the_extension(self, settings: Settings) -> None:
        name = "B" * 500 + ".flac"
        cleaned = sanitise_component(
            name, max_length=120, keep_extension=True, settings=settings
        )
        assert cleaned.endswith(".flac")
        assert len(cleaned) == 120

    def test_truncation_respects_the_byte_ceiling(self, settings: Settings) -> None:
        # Each of these is 3 bytes in UTF-8, so 200 characters is 600 bytes.
        cleaned = sanitise_component("é" * 200, max_length=250, settings=settings)
        assert len(cleaned.encode("utf-8")) <= MAX_COMPONENT_BYTES

    def test_custom_replacement_character(self, settings: Settings) -> None:
        assert sanitise_component("AC/DC", replacement="-", settings=settings) == "AC-DC"


# ---------------------------------------------------------------------------
# quality_tag / extension_for_format
# ---------------------------------------------------------------------------
class TestQualityTag:
    """Labels derived from what ``getFileUrl`` actually returned."""

    @pytest.mark.parametrize(
        ("format_id", "bit_depth", "sampling_rate", "expected"),
        [
            (5, None, None, "MP3 320"),
            (5, 16, 44.1, "MP3 320"),
            (6, 16, 44.1, "FLAC 16-44.1"),
            (7, 24, 96.0, "FLAC 24-96"),
            (27, 24, 192.0, "FLAC 24-192"),
            (6, 16, None, "FLAC 16bit"),
            (6, None, None, "FLAC"),
            (None, None, None, ""),
        ],
    )
    def test_labels(
        self,
        format_id: int | None,
        bit_depth: int | None,
        sampling_rate: float | None,
        expected: str,
    ) -> None:
        assert quality_tag(format_id, bit_depth, sampling_rate) == expected

    def test_hertz_is_converted_to_kilohertz(self) -> None:
        assert quality_tag(27, 24, 192000) == "FLAC 24-192"

    @pytest.mark.parametrize(
        ("format_id", "expected"), [(5, "mp3"), (6, "flac"), (7, "flac"), (27, "flac")]
    )
    def test_extensions(self, format_id: int, expected: str) -> None:
        assert extension_for_format(format_id) == expected

    def test_extension_falls_back_to_mime(self) -> None:
        assert extension_for_format(None, "audio/mpeg") == "mp3"
        assert extension_for_format(None, "audio/flac") == "flac"
        assert extension_for_format(None) == "flac"


# ---------------------------------------------------------------------------
# render_template
# ---------------------------------------------------------------------------
class TestRenderTemplate:
    """The optional-segment and format-spec machinery."""

    def test_optional_segment_is_kept_when_the_value_is_present(self) -> None:
        rendered = render_template("{album} ({year})[ [{quality}]]", {
            "album": "All Melody", "year": 2018, "quality": "FLAC 24-96",
        })
        assert rendered == "All Melody (2018) [FLAC 24-96]"

    def test_optional_segment_is_dropped_when_the_value_is_empty(self) -> None:
        rendered = render_template("{album} ({year})[ [{quality}]]", {
            "album": "All Melody", "year": 2018, "quality": "",
        })
        assert rendered == "All Melody (2018)"

    def test_empty_parentheses_are_removed(self) -> None:
        rendered = render_template("{album} ({year})", {"album": "All Melody", "year": ""})
        assert rendered == "All Melody"

    def test_format_spec_is_applied(self) -> None:
        assert render_template("{track:02d}", {"track": 3}) == "03"

    def test_format_spec_on_an_unformattable_value_does_not_raise(self) -> None:
        assert render_template("{track:02d}", {"track": "three"}) == "three"

    def test_unknown_placeholder_renders_empty(self) -> None:
        assert render_template("{nope}x", {}) == "x"


# ---------------------------------------------------------------------------
# render_album_dir
# ---------------------------------------------------------------------------
class TestRenderAlbumDir:
    """Album folder layout."""

    def test_default_layout(self, settings: Settings) -> None:
        path = render_album_dir(make_artist(), make_album(), settings, format_id=7)
        assert path == LIBRARY / "Nils Frahm" / "All Melody (2018) [FLAC 24-96]"

    def test_version_suffix_separates_editions(self, settings: Settings) -> None:
        deluxe = render_album_dir(
            make_artist(), make_album(version="Deluxe Edition"), settings, format_id=7
        )
        standard = render_album_dir(make_artist(), make_album(), settings, format_id=7)
        assert deluxe.name == "All Melody (Deluxe Edition) (2018) [FLAC 24-96]"
        assert deluxe != standard

    def test_missing_year_leaves_no_empty_parentheses(self, settings: Settings) -> None:
        path = render_album_dir(
            make_artist(), make_album(release_date=None), settings, format_id=7
        )
        assert path.name == "All Melody [FLAC 24-96]"
        assert "()" not in str(path)

    def test_missing_quality_drops_the_optional_segment(self, settings: Settings) -> None:
        album = make_album(max_bit_depth=None, max_sampling_rate=None)
        path = render_album_dir(make_artist(), album, settings, format_id=None)
        assert path.name == "All Melody (2018)"
        assert "[" not in path.name

    def test_missing_year_and_quality(self, settings: Settings) -> None:
        album = make_album(release_date=None, max_bit_depth=None, max_sampling_rate=None)
        path = render_album_dir(make_artist(), album, settings, format_id=None)
        assert path.name == "All Melody"

    def test_slashes_in_names_do_not_create_directories(self, settings: Settings) -> None:
        path = render_album_dir(
            make_artist("AC/DC"), make_album(title="Back/Forward"), settings, format_id=6
        )
        assert path.parent.name == "AC_DC"
        assert path.relative_to(LIBRARY).parts == (
            "AC_DC",
            "Back_Forward (2018) [FLAC 16-44.1]",
        )

    def test_artist_may_be_a_plain_string(self, settings: Settings) -> None:
        path = render_album_dir("Aphex Twin", make_album(), settings, format_id=6)
        assert path.parent.name == "Aphex Twin"

    def test_root_override(self, settings: Settings) -> None:
        path = render_album_dir(
            make_artist(), make_album(), settings, format_id=7, root=Path("/tmp/lib")
        )
        assert str(path).startswith("/tmp/lib/")

    def test_very_long_title_is_capped(self, settings: Settings) -> None:
        album = make_album(title="Z" * 400)
        path = render_album_dir(make_artist(), album, settings, format_id=7)
        assert len(path.name) <= settings.max_path_component_length
        assert len(path.name.encode("utf-8")) <= MAX_COMPONENT_BYTES


# ---------------------------------------------------------------------------
# render_track_name
# ---------------------------------------------------------------------------
class TestRenderTrackName:
    """File names, relative to the album directory."""

    def test_single_disc_has_no_subfolder(self, settings: Settings) -> None:
        name = render_track_name(make_track(), make_album(), settings, format_id=7)
        assert name == PurePosixPath("03 - Sunson.flac")

    def test_multi_disc_gets_a_disc_folder(self, settings: Settings) -> None:
        album = make_album(media_count=3)
        track = make_track(media_number=2, track_number=7)
        name = render_track_name(track, album, settings, format_id=7)
        assert name == PurePosixPath("Disc 2/07 - Sunson.flac")
        assert name.parts == ("Disc 2", "07 - Sunson.flac")

    def test_disc_folder_only_when_media_count_exceeds_one(self, settings: Settings) -> None:
        # media_count == 1 but the track claims disc 1 — still no folder.
        name = render_track_name(
            make_track(media_number=1), make_album(media_count=1), settings, format_id=7
        )
        assert len(name.parts) == 1

    def test_mp3_extension_follows_the_delivered_format(self, settings: Settings) -> None:
        name = render_track_name(make_track(), make_album(), settings, format_id=5)
        assert name.name.endswith(".mp3")

    def test_track_version_is_appended_to_the_title(self, settings: Settings) -> None:
        track = make_track(version="Live at Funkhaus")
        name = render_track_name(track, make_album(), settings, format_id=7)
        assert name.name == "03 - Sunson (Live at Funkhaus).flac"

    def test_slash_in_title_is_replaced_not_split(self, settings: Settings) -> None:
        track = make_track(title="Say Yes / Say No")
        name = render_track_name(track, make_album(), settings, format_id=7)
        assert len(name.parts) == 1
        assert name.name == "03 - Say Yes _ Say No.flac"

    def test_unicode_title_survives(self, settings: Settings) -> None:
        track = make_track(title="Étude pour la main gauche")
        name = render_track_name(track, make_album(), settings, format_id=7)
        assert name.name == "03 - Étude pour la main gauche.flac"

    def test_trailing_dot_in_title_is_trimmed_before_the_extension(
        self, settings: Settings
    ) -> None:
        track = make_track(title="Etc.")
        name = render_track_name(track, make_album(), settings, format_id=7)
        assert name.name == "03 - Etc.flac"

    def test_very_long_title_keeps_its_extension(self, settings: Settings) -> None:
        track = make_track(title="L" * 400)
        name = render_track_name(track, make_album(), settings, format_id=7)
        assert name.name.endswith(".flac")
        assert len(name.name) <= settings.max_path_component_length
        assert len(name.name.encode("utf-8")) <= MAX_COMPONENT_BYTES

    def test_long_unicode_title_stays_within_the_byte_ceiling(
        self, settings: Settings
    ) -> None:
        track = make_track(title="漢" * 300)
        name = render_track_name(track, make_album(), settings, format_id=7)
        assert name.name.endswith(".flac")
        assert len(name.name.encode("utf-8")) <= MAX_COMPONENT_BYTES

    def test_zero_track_number_still_formats(self, settings: Settings) -> None:
        name = render_track_name(
            make_track(track_number=0), make_album(), settings, format_id=7
        )
        assert name.name.startswith("00 - ")


# ---------------------------------------------------------------------------
# disc_prefix / render_track_path / misc
# ---------------------------------------------------------------------------
class TestMisc:
    """The remaining helpers."""

    def test_disc_prefix(self) -> None:
        assert disc_prefix(make_album(media_count=1), 1) == ""
        assert disc_prefix(make_album(media_count=2), 2) == "Disc 2/"
        assert disc_prefix({"media_count": 4}, 3) == "Disc 3/"

    def test_render_track_path_is_the_full_absolute_path(self, settings: Settings) -> None:
        path = render_track_path(
            make_track(), make_album(), settings, artist=make_artist(), format_id=7
        )
        assert path == (
            LIBRARY / "Nils Frahm" / "All Melody (2018) [FLAC 24-96]" / "03 - Sunson.flac"
        )

    def test_render_track_path_accepts_a_precomputed_album_dir(
        self, settings: Settings
    ) -> None:
        album_dir = Path("/srv/music/Custom")
        path = render_track_path(
            make_track(),
            make_album(),
            settings,
            artist=make_artist(),
            album_dir=album_dir,
            format_id=7,
        )
        assert path == album_dir / "03 - Sunson.flac"

    def test_mappings_work_as_well_as_orm_rows(self, settings: Settings) -> None:
        album = {
            "title": "Mapping Album",
            "version": None,
            "release_date": date(2020, 5, 1),
            "media_count": 1,
            "tracks_count": 4,
        }
        track = {"title": "Mapped", "track_number": 2, "media_number": 1}
        path = render_track_path(track, album, settings, artist="Dict Artist", format_id=6)
        assert path.name == "02 - Mapped.flac"
        assert path.parent.parent.name == "Dict Artist"

    def test_custom_template_without_directories(self) -> None:
        settings = Settings(
            library_path=LIBRARY, naming_template="{artist} - {album} - {title}.{ext}"
        )
        assert render_album_dir(make_artist(), make_album(), settings) == LIBRARY
        name = render_track_name(
            make_track(), make_album(), settings, artist=make_artist(), format_id=7
        )
        assert name == PurePosixPath("Nils Frahm - All Melody - Sunson.flac")

    def test_custom_template_with_label_and_disc_numbers(self) -> None:
        settings = Settings(
            library_path=LIBRARY,
            naming_template="{artist}/{album}/{disc}-{track:02d} {title}.{ext}",
        )
        name = render_track_name(
            make_track(media_number=2, track_number=5),
            make_album(media_count=2),
            settings,
            format_id=6,
        )
        assert name == PurePosixPath("2-05 Sunson.flac")

    def test_upgrade_image_url(self) -> None:
        assert upgrade_image_url(
            "https://static.qobuz.com/images/covers/aa/bb/id_600.jpg"
        ) == "https://static.qobuz.com/images/covers/aa/bb/id_max.jpg"
        assert upgrade_image_url("https://example.com/cover.jpg") == (
            "https://example.com/cover.jpg"
        )
        assert upgrade_image_url(None) is None
        assert upgrade_image_url("") is None
