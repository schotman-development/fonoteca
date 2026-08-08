"""Whose release is this? — guest appearances, and one artist id holding many.

Two independent refusals, both landing in :func:`app.core.indexer.desired_status`
because that is the single statement of "does this artist want this release?"
and the only thing that re-derives the status of a row that already exists.

The first is exact and needs no judgement: Qobuz files a guest appearance under
the guest, and its own payload says so in ``artists[].roles``. The second is not
exact and deliberately does not pretend to be — a Qobuz artist id is sometimes
several people, no upstream separates them (Deezer merges them identically,
MusicBrainz knows 12% of the ISRCs), so a person picks from the track credits
and the machine only remembers.

The phrasing of the first is what most of this file is about. "Lacks
``main-artist``" and "is ``featured-artist`` and never ``main-artist``" agree on
every release a performer makes and disagree on a composer's entire catalogue.
"""

from __future__ import annotations

import pytest

from app.core.indexer import desired_status, wants_nothing
from app.models import Album, AlbumStatus, Artist, MonitorMode
from app.qobuz.mapper import album_credit_names, album_guest_appearance, map_album


# ---------------------------------------------------------------------------
# Reading the roles off a payload
# ---------------------------------------------------------------------------
def _payload(*credits: tuple[str, list[str]]) -> dict:
    return {
        "id": "alb1",
        "title": "A Record",
        "artists": [
            {"id": int(aid), "name": f"n{aid}", "roles": roles} for aid, roles in credits
        ],
    }


def test_a_main_artist_is_not_a_guest() -> None:
    assert album_guest_appearance(_payload(("322476", ["main-artist"])), "322476") is False


def test_featured_only_is_a_guest() -> None:
    payload = _payload(("11168107", ["main-artist"]), ("322476", ["featured-artist"]))
    assert album_guest_appearance(payload, "322476") is True


def test_a_co_credited_main_artist_is_not_a_guest() -> None:
    """Two names on the sleeve is a collaboration; it is that artist's release too."""
    payload = _payload(("191038", ["main-artist"]), ("322476", ["main-artist"]))
    assert album_guest_appearance(payload, "322476") is False


def test_both_roles_at_once_is_not_a_guest() -> None:
    """``main-artist`` wins outright, whatever else is beside it."""
    payload = _payload(("322476", ["featured-artist", "main-artist"]))
    assert album_guest_appearance(payload, "322476") is False


def test_an_artist_absent_from_the_credits_is_not_a_guest() -> None:
    """The composer case, and the reason the rule is not "lacks main-artist".

    ``artists`` lists *performers*. Samuel Barber's id appears on 13 of his 169
    releases; the other 156 name the orchestra and the conductor. Reading absence
    as "not a main artist, therefore a guest" would demote 92% of a composer's
    catalogue, and this library follows eight composers covering ~3,300 albums.
    """
    payload = _payload(("26401", ["main-artist"]), ("17113", ["main-artist"]))
    assert album_guest_appearance(payload, "27") is False


def test_no_artists_array_is_unmeasured() -> None:
    """The older ``artist/get?extra=albums`` shape carries no roles at all.

    ``None`` and not ``False``: the fallback path says nothing about this
    release, and saying nothing has to be distinguishable from saying no.
    """
    assert album_guest_appearance({"id": "a", "title": "t"}, "322476") is None
    assert album_guest_appearance({"id": "a", "artists": []}, "322476") is None


def test_map_album_carries_the_verdict_through() -> None:
    payload = _payload(("1726164", ["main-artist"]), ("322476", ["featured-artist"]))
    assert map_album(payload, "322476")["guest_appearance"] is True
    assert map_album(payload, "1726164")["guest_appearance"] is False


def test_map_album_does_not_touch_credit_names() -> None:
    """It costs an ``album/get``, so the index tick must not clear it."""
    assert "credit_names" not in map_album(_payload(("1", ["main-artist"])), "1")


# ---------------------------------------------------------------------------
# Reading the credits off a payload
# ---------------------------------------------------------------------------
def _tracks(*performers: str) -> dict:
    return {"tracks": {"items": [{"performers": p} for p in performers]}}


def test_a_qualified_credit_is_found() -> None:
    raw = _tracks(
        "Boaz, Vocals, MainArtist - Maurice van Hoek, Lyricist - "
        "Boaz Roelevink, Lyricist"
    )
    assert album_credit_names(raw, "Boaz") == ["Boaz Roelevink"]


def test_the_bare_artist_name_is_never_a_credit() -> None:
    """Every release under the id carries it, so it distinguishes nothing."""
    assert album_credit_names(_tracks("Boaz, MainArtist"), "Boaz") == []


def test_a_name_that_merely_starts_with_the_artists_is_not_a_credit() -> None:
    """Word boundary: ``Boazts and Hammock`` is a different act, not a Boaz."""
    assert album_credit_names(_tracks("Boazts and Hammock, MainArtist"), "Boaz") == []


def test_credits_are_distinct_and_ordered_by_first_appearance() -> None:
    raw = _tracks("Boaz Ndarivoi, Composer", "Boaz Ndarivoi, Lyricist - Boaz B, Composer")
    assert album_credit_names(raw, "Boaz") == ["Boaz Ndarivoi", "Boaz B"]


def test_no_credits_at_all_is_an_empty_list() -> None:
    assert album_credit_names({"tracks": {"items": []}}, "Boaz") == []
    assert album_credit_names(_tracks("Boaz, MainArtist"), "") == []


# ---------------------------------------------------------------------------
# What the rule does with it
# ---------------------------------------------------------------------------
def _artist(**kwargs: object) -> Artist:
    artist = Artist(id="322476", name="Boaz")
    artist.monitored = True
    artist.monitor_mode = MonitorMode.ALL
    artist.accepted_release_types = "album,ep,live,single,compilation,download,other"
    artist.include_guest_appearances = False
    artist.credit_filter_json = None
    for key, value in kwargs.items():
        setattr(artist, key, value)
    return artist


def _album(**kwargs: object) -> Album:
    album = Album(id="alb1", artist_id="322476", title="A Record")
    album.release_type = "album"
    album.guest_appearance = None
    album.credit_names = None
    for key, value in kwargs.items():
        setattr(album, key, value)
    return album


def test_a_guest_appearance_is_not_wanted() -> None:
    status, reason = desired_status(_artist(), _album(guest_appearance=True))
    assert status is AlbumStatus.SKIPPED
    assert "guest" in reason


def test_a_guest_appearance_is_wanted_when_the_artist_asks_for_them() -> None:
    artist = _artist(include_guest_appearances=True)
    assert desired_status(artist, _album(guest_appearance=True))[0] is AlbumStatus.WANTED


def test_an_unmeasured_release_is_unaffected() -> None:
    """``NULL`` is a no-op, not a demotion — the whole reason the column is
    three-valued. Every row predates the column on the day it ships, and only
    ``apply_monitoring_to_backlog`` would ever bring them back."""
    assert desired_status(_artist(), _album(guest_appearance=None))[0] is AlbumStatus.WANTED


def test_a_credit_filter_keeps_what_it_names() -> None:
    artist = _artist()
    artist.set_credit_filter(["Boaz Roelevink"])
    album = _album()
    album.set_credit_names(["Boaz Roelevink"])
    assert desired_status(artist, album)[0] is AlbumStatus.WANTED


def test_a_credit_filter_refuses_somebody_else() -> None:
    artist = _artist()
    artist.set_credit_filter(["Boaz Roelevink"])
    album = _album()
    album.set_credit_names(["Boaz Ndarivoi"])
    status, reason = desired_status(artist, album)
    assert status is AlbumStatus.SKIPPED
    assert "credited" in reason


def test_the_credit_match_is_case_insensitive() -> None:
    artist = _artist()
    artist.set_credit_filter(["boaz roelevink"])
    album = _album()
    album.set_credit_names(["Boaz Roelevink"])
    assert desired_status(artist, album)[0] is AlbumStatus.WANTED


def test_an_unanalysed_release_is_unaffected_by_a_filter() -> None:
    """``credit_names IS NULL`` means nobody looked, and must behave as if the
    filter were off. Reading it as "no credit matched" would demote a whole
    catalogue the moment a filter was set, before a single release was read."""
    artist = _artist()
    artist.set_credit_filter(["Boaz Roelevink"])
    album = _album(credit_names=None)
    assert desired_status(artist, album)[0] is AlbumStatus.WANTED


def test_an_analysed_release_with_no_qualified_credit_is_refused() -> None:
    """``"[]"`` is a measurement, not an absence — and it is a different value
    from ``NULL`` in the column even though both read back as ``[]``."""
    artist = _artist()
    artist.set_credit_filter(["Boaz Roelevink"])
    album = _album()
    album.set_credit_names([])
    assert album.credit_names == "[]"
    assert desired_status(artist, album)[0] is AlbumStatus.SKIPPED


def test_an_empty_filter_is_no_filter() -> None:
    """A filter that refused everything would look exactly like a broken artist,
    and ``monitor_mode='none'`` already says that on purpose."""
    artist = _artist()
    artist.set_credit_filter([])
    assert artist.credit_filter_json is None
    album = _album()
    album.set_credit_names([])
    assert desired_status(artist, album)[0] is AlbumStatus.WANTED


def test_neither_clause_belongs_in_wants_nothing() -> None:
    """``wants_nothing`` must stay a faithful optimisation: true there has to
    mean ``desired_status`` is SKIPPED for **every** album. Both new clauses
    read the album, so an artist carrying them still has to have their rows
    loaded — and both must leave the fast path alone."""
    artist = _artist(include_guest_appearances=False)
    artist.set_credit_filter(["Boaz Roelevink"])
    assert wants_nothing(artist) is False


@pytest.mark.parametrize("mode", [MonitorMode.NONE])
def test_the_cheap_refusals_still_come_first(mode: MonitorMode) -> None:
    """An unmonitored artist is answered without consulting attribution at all."""
    artist = _artist(monitor_mode=mode)
    status, reason = desired_status(artist, _album(guest_appearance=True))
    assert status is AlbumStatus.SKIPPED
    assert "monitor mode" in reason
