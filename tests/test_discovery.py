"""The audio route into the Qobuz catalogue.

    AcoustID -> MusicBrainz -> universal code -> Qobuz

The bulk importer answers "who is this folder?" by *name*, exactly, and refuses
anything else — which is right, and which leaves every artist whose folder is not
spelled the way Qobuz spells it. This is the other route, and the property every
test below is really defending is that it never reads a name to decide anything:
the MusicBrainz title is a search query, and the barcode is what selects.

Nothing here touches the network or the database. The three collaborators are
passed in, so the tests are about the chain's decisions rather than anybody's
HTTP.
"""

from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping, Sequence

import pytest

from app.core.discovery import (
    FolderIdentity,
    Outcome,
    folder_snapshot,
    identify_folder,
    search_terms,
)
from app.enrich.chromaprint import FpcalcMissing
from app.enrich.errors import AmbiguousMatch, NoMatchKey, SourceGated

# ---------------------------------------------------------------------------
# Fixtures: a folder, and the three collaborators
# ---------------------------------------------------------------------------


@dataclass
class FakeTrack:
    path: Path
    title: str = ""
    track_number: int = 0
    media_number: int = 1


@dataclass
class FakeScanned:
    directory: str = "/music/Joanne Shaw Taylor/White Sugar"
    artist: str = "Joanne Shaw Taylor"
    title: str = "White Sugar"
    tracks: Sequence[FakeTrack] = field(default_factory=list)


def folder(tracks: int = 3, **overrides: Any) -> FakeScanned:
    return FakeScanned(
        tracks=[
            FakeTrack(Path(f"/music/x/{n:02d} - Track {n}.flac"), f"Track {n}", n, 1)
            for n in range(1, tracks + 1)
        ],
        **overrides,
    )


class FakeAcoustId:
    """Returns whatever ``album_fields`` the test wants, or raises."""

    def __init__(self, album_fields: Mapping[str, Any] | None = None, raises: Any = None):
        self._fields = dict(album_fields or {})
        self._raises = raises
        self.jobs: list[Any] = []

    async def fetch(self, job: Any) -> Any:
        self.jobs.append(job)
        if self._raises is not None:
            raise self._raises
        return type("Result", (), {"album_fields": self._fields})()


def mb_release(
    mbid: str = "rel-1",
    *,
    barcode: str | None = "710347114727",
    title: str = "White Sugar",
    credits: Sequence[str] = ("Joanne Shaw Taylor",),
    group: str | None = "rg-1",
) -> dict[str, Any]:
    """A release payload shaped like ``MusicBrainzClient.release`` really returns.

    ``artist-credit`` and ``release-group`` are both in ``_RELEASE_INC``, so a
    real lookup carries them; a fake that omitted them would let the chain pass a
    test it fails in production, which is the exact trap that hid the missing
    artist name for as long as it did.
    """
    return {
        "id": mbid,
        "title": title,
        "barcode": barcode,
        "release-group": {"id": group} if group else None,
        "artist-credit": [{"name": name, "artist": {"name": name}} for name in credits],
    }


class FakeMusicBrainz:
    """``release`` and ``releases_in_group`` — both halves the chain uses."""

    def __init__(
        self,
        releases: Sequence[Mapping[str, Any]] | None = None,
        raises: Any = None,
        *,
        lookups: Mapping[str, Mapping[str, Any]] | None = None,
    ):
        #: What a *group browse* returns, as bare ``{"barcode": …}`` rows.
        self._releases = list(releases if releases is not None else [])
        self._raises = raises
        #: What a *release lookup* returns, keyed by MBID.
        self._lookups = dict(lookups or {})
        self.groups: list[str] = []
        self.released: list[str] = []

    async def release(self, mbid: str) -> Mapping[str, Any] | None:
        self.released.append(mbid)
        if self._raises is not None:
            raise self._raises
        if self._lookups:
            return self._lookups.get(mbid)
        return mb_release(mbid)

    async def releases_in_group(self, mbid: str) -> list[Mapping[str, Any]]:
        self.groups.append(mbid)
        if self._raises is not None:
            raise self._raises
        return list(self._releases)


class FakeQobuz:
    def __init__(self, items: Sequence[Mapping[str, Any]] | None = None, raises: Any = None):
        self._items = list(items or [])
        self._raises = raises
        self.queries: list[str] = []

    async def search_albums(self, query: str, limit: int = 25) -> list[Mapping[str, Any]]:
        self.queries.append(query)
        if self._raises is not None:
            raise self._raises
        return list(self._items)


def qobuz_album(album_id: str, upc: str, *, artist_id: Any = 1464909, artist_name: str = "Joanne Shaw Taylor"):
    return {
        "id": album_id,
        "upc": upc,
        "title": "White Sugar",
        "artist": {"id": artist_id, "name": artist_name},
    }


def run(**kwargs: Any) -> FolderIdentity:
    scanned = kwargs.pop("scanned", None) or folder()
    return asyncio.run(
        identify_folder(
            scanned,
            # The audio pins a *release*, which is what AcoustID actually writes.
            # It does not supply a release group — ``meta=releases`` carries none
            # — so the chain must reach one through the release lookup.
            acoustid=kwargs.pop("acoustid", FakeAcoustId({"mb_release_mbid": "rel-1"})),
            musicbrainz=kwargs.pop("musicbrainz", FakeMusicBrainz([{"barcode": "0884385226442"}])),
            qobuz=kwargs.pop("qobuz", FakeQobuz([qobuz_album("aaa", "0884385226442")])),
            **kwargs,
        )
    )


# ---------------------------------------------------------------------------
# The happy path, and what it is allowed to rest on
# ---------------------------------------------------------------------------


def test_the_chain_ends_at_an_exact_qobuz_artist() -> None:
    identity = run()
    assert identity.outcome == Outcome.IDENTIFIED
    assert identity.followable
    assert identity.qobuz_artist_id == "1464909"
    assert identity.qobuz_artist_name == "Joanne Shaw Taylor"
    assert identity.qobuz_album_id == "aaa"
    assert "0884385226442" in identity.reason


def test_an_edition_from_the_same_release_group_still_identifies_the_artist() -> None:
    """The real case: MusicBrainz's pressing is not the one Qobuz sells.

    ``710347114727`` is *White Sugar* in ``album_metadata``; Qobuz sells
    ``0884385226442``. Two editions, one record, one artist — so browsing the
    release group is what makes them meet, and the artist answer is still exact.
    """
    identity = run(
        musicbrainz=FakeMusicBrainz(
            [{"barcode": "710347114727"}, {"barcode": "0884385226442"}]
        ),
        qobuz=FakeQobuz([qobuz_album("aaa", "0884385226442")]),
    )
    assert identity.outcome == Outcome.IDENTIFIED
    assert identity.qobuz_artist_id == "1464909"
    # Both widths of each barcode, because MusicBrainz stores one as it was
    # typed: the same code is the 12-digit UPC-A on a release entered from a US
    # sleeve and the 13-digit EAN on one entered from a European sleeve. Brad
    # Paisley's *Play* is 884977725872 in MusicBrainz and 0884977725872 on
    # Qobuz — comparing a single form is one leading zero away from no match.
    assert set(identity.barcodes) >= {"710347114727", "0884385226442"}
    assert "0710347114727" in identity.barcodes


def test_only_the_matched_pressing_would_have_missed_it() -> None:
    """The same folder, with the group browse returning just MusicBrainz's own."""
    identity = run(
        musicbrainz=FakeMusicBrainz([{"barcode": "710347114727"}]),
        qobuz=FakeQobuz([qobuz_album("aaa", "0884385226442")]),
    )
    assert identity.outcome == Outcome.UNIDENTIFIED
    assert identity.qobuz_artist_id is None


def test_the_name_is_a_query_and_never_a_verdict() -> None:
    """A perfect name match with a wrong barcode identifies nothing."""
    qobuz = FakeQobuz([qobuz_album("aaa", "5051083067188")])
    identity = run(qobuz=qobuz)
    assert identity.outcome == Outcome.UNIDENTIFIED
    assert identity.qobuz_artist_id is None
    # The search still happened — the name's whole job is to narrow it.
    assert qobuz.queries == ["Joanne Shaw Taylor White Sugar"]


def test_the_query_comes_from_musicbrainz_and_never_from_the_folder() -> None:
    """The folder is the one input with positive evidence against it.

    A directory only reaches this module because its name *already failed* to
    match the catalogue, so searching in its vocabulary asks the question in the
    spelling just demonstrated not to work. Measured live: Brad Paisley's
    ``Play: The Guitar Album`` — the folder's own title — returns **nothing** on
    Qobuz, while MusicBrainz's title for the same record, ``Play``, returns it
    first.
    """
    qobuz = FakeQobuz([qobuz_album("aaa", "0884385226442")])
    run(
        scanned=folder(title="white sugar (2009) [FLAC 16-44.1]", artist="WHTE SUGR BAND"),
        musicbrainz=FakeMusicBrainz(
            [{"barcode": "0884385226442"}],
            lookups={"rel-1": mb_release("rel-1", title="White Sugar")},
        ),
        qobuz=qobuz,
    )
    assert qobuz.queries == ["Joanne Shaw Taylor White Sugar"]
    assert not any("WHTE SUGR" in query for query in qobuz.queries)
    assert not any("FLAC" in query for query in qobuz.queries)


def test_every_credit_is_tried_which_is_what_classical_needs() -> None:
    """MusicBrainz leads with the composer where Qobuz leads with the performer.

    Both catalogues credit everyone; they disagree about who is *the* album
    artist. Taking only the first credit is a coin toss on exactly the repertoire
    this route exists for, and the barcode still decides, so an extra query can
    cost requests and never precision.
    """
    qobuz = FakeQobuz([])
    run(
        musicbrainz=FakeMusicBrainz(
            [{"barcode": "0884385226442"}],
            lookups={
                "rel-1": mb_release(
                    "rel-1",
                    title="The Nutcracker",
                    credits=("Pyotr Ilyich Tchaikovsky", "André Previn"),
                )
            },
        ),
        qobuz=qobuz,
    )
    assert qobuz.queries == [
        "Pyotr Ilyich Tchaikovsky The Nutcracker",
        "André Previn The Nutcracker",
    ]


def test_searching_stops_as_soon_as_a_barcode_has_decided() -> None:
    """A barcode hit is exact, so a further query can only cost requests."""
    qobuz = FakeQobuz([qobuz_album("aaa", "0884385226442")])
    run(
        musicbrainz=FakeMusicBrainz(
            [{"barcode": "0884385226442"}],
            lookups={"rel-1": mb_release("rel-1", credits=("A", "B", "C"))},
        ),
        qobuz=qobuz,
    )
    assert len(qobuz.queries) == 1


# ---------------------------------------------------------------------------
# Every way it declines, and none of them is a guess
# ---------------------------------------------------------------------------


def test_two_qobuz_editions_are_ambiguous_not_the_first_one() -> None:
    identity = run(
        qobuz=FakeQobuz(
            [qobuz_album("aaa", "0884385226442"), qobuz_album("bbb", "0884385226442")]
        )
    )
    assert identity.outcome == Outcome.AMBIGUOUS
    assert identity.qobuz_artist_id is None
    assert len(identity.candidates) == 2


def test_audio_that_settles_on_no_release_at_all_stops_there() -> None:
    identity = run(acoustid=FakeAcoustId({}))
    assert identity.outcome == Outcome.UNIDENTIFIED
    assert "did not settle on any release" in identity.reason


def test_a_record_with_no_barcode_anywhere_never_reaches_qobuz() -> None:
    qobuz = FakeQobuz([qobuz_album("aaa", "0884385226442")])
    identity = run(
        musicbrainz=FakeMusicBrainz(
            [{"barcode": ""}], lookups={"rel-1": mb_release("rel-1", barcode=None)}
        ),
        qobuz=qobuz,
    )
    assert identity.outcome == Outcome.UNIDENTIFIED
    assert "barcode" in identity.reason
    assert qobuz.queries == [], "a search with no key to check is a wasted call"


def test_a_folder_with_no_audio_asks_nobody_anything() -> None:
    acoustid = FakeAcoustId({"mb_release_group_mbid": "rg-1"})
    identity = run(scanned=folder(tracks=0), acoustid=acoustid)
    assert identity.outcome == Outcome.UNIDENTIFIED
    assert acoustid.jobs == []


def test_an_ambiguous_fingerprint_is_reported_as_ambiguous() -> None:
    identity = run(acoustid=FakeAcoustId(raises=AmbiguousMatch("three editions fit")))
    assert identity.outcome == Outcome.AMBIGUOUS
    assert "three editions" in identity.reason


def _ambiguous(*mbids: str) -> AmbiguousMatch:
    """The verdict ``AcoustIdProvider`` really raises, candidates and all.

    Shaped like ``_candidate_summary``'s output, which is what the review list
    renders — deliberately *without* ``release_group_id``, because
    ``acoustid._release_group_id`` says ``meta=releases`` usually carries no
    group and the live data confirmed it: on 40 real folders it was empty every
    single time. The release ids, by contrast, are always there.
    """
    outcome = AmbiguousMatch(f"{len(mbids)} releases account for these files equally well")
    outcome.candidates = [
        {"id": mbid, "title": "White Sugar", "release_group_id": None} for mbid in mbids
    ]
    outcome.partial = type("Partial", (), {"album_fields": {}})()
    return outcome


def test_an_ambiguous_pressing_still_identifies_the_record() -> None:
    """The pressing cannot be chosen; the barcodes of its candidates still can.

    This is the case that matters most in practice. Brad Paisley's *Play* comes
    back as four MusicBrainz releases that each explain 16 of 16 files — all in
    one release group, all carrying barcodes, one of them Qobuz's exactly. The
    chain does not need to know which pressing it is holding; it needs a key.
    """
    mb = FakeMusicBrainz(
        [{"barcode": "0884385226442"}],
        lookups={
            "rel-1": mb_release("rel-1", barcode="886972690827"),
            "rel-2": mb_release("rel-2", barcode="884977725872"),
        },
    )
    identity = run(acoustid=FakeAcoustId(raises=_ambiguous("rel-1", "rel-2")), musicbrainz=mb)

    assert identity.outcome == Outcome.IDENTIFIED
    assert identity.qobuz_album_id == "aaa"
    assert mb.released == ["rel-1", "rel-2"]


def test_the_salvaged_match_says_it_was_the_record_not_the_pressing() -> None:
    """A reader must be able to tell the two apart without re-deriving it."""
    identity = run(acoustid=FakeAcoustId(raises=_ambiguous("rel-1")))
    assert "not the pressing" in identity.reason


def test_an_ambiguity_naming_no_release_is_still_ambiguous() -> None:
    """The salvage is a real claim, not a way of never saying no."""
    outcome = AmbiguousMatch("two records fit")
    outcome.candidates = []
    outcome.partial = type("Partial", (), {"album_fields": {}})()
    identity = run(acoustid=FakeAcoustId(raises=outcome))
    assert identity.outcome == Outcome.AMBIGUOUS


def test_the_candidate_lookups_are_capped() -> None:
    """A compilation can be ambiguous across dozens of editions, each a request."""
    mb = FakeMusicBrainz([{"barcode": "0884385226442"}])
    run(acoustid=FakeAcoustId(raises=_ambiguous(*[f"rel-{n}" for n in range(20)])), musicbrainz=mb)
    assert len(mb.released) == 6


def test_a_rung_with_no_key_is_gated_not_a_failed_match() -> None:
    """``no_key`` and ``gated`` are both "could not run", not "did not match"."""
    for cannot_run in (NoMatchKey("nothing to fingerprint"), SourceGated("no key")):
        identity = run(acoustid=FakeAcoustId(raises=cannot_run))
        assert identity.outcome == Outcome.GATED, cannot_run


def test_a_missing_fpcalc_gates_the_whole_pass_rather_than_one_folder() -> None:
    """Every remaining folder would answer the same; running them is waste."""
    with pytest.raises(FpcalcMissing):
        run(acoustid=FakeAcoustId(raises=FpcalcMissing("fpcalc is not installed")))


def test_an_upstream_that_breaks_loses_one_folder_and_not_the_pass() -> None:
    for broken in (
        {"musicbrainz": FakeMusicBrainz(raises=RuntimeError("503"))},
        {"qobuz": FakeQobuz(raises=RuntimeError("timeout"))},
        {"acoustid": FakeAcoustId(raises=RuntimeError("boom"))},
    ):
        identity = run(**broken)
        assert identity.outcome == Outcome.UNIDENTIFIED
        assert identity.qobuz_artist_id is None
        assert identity.reason


def test_a_matched_album_crediting_nobody_is_not_followable() -> None:
    identity = run(
        qobuz=FakeQobuz([{"id": "aaa", "upc": "0884385226442", "title": "X"}])
    )
    assert identity.followable is False
    assert identity.qobuz_album_id == "aaa"


# ---------------------------------------------------------------------------
# The adapter that lets an unmatched folder reach a rung built for rows
# ---------------------------------------------------------------------------


def test_the_snapshot_carries_the_paths_the_fingerprinter_reads() -> None:
    snapshot = folder_snapshot(folder(tracks=2))
    assert len(snapshot.tracks) == 2
    assert all(track.path for track in snapshot.tracks)
    assert snapshot.tracks_count == 2
    assert snapshot.artist_name == "Joanne Shaw Taylor"


def test_the_snapshot_id_is_synthetic_and_never_a_qobuz_id() -> None:
    """Nothing persists these; the prefix is what makes that obvious in a log."""
    snapshot = folder_snapshot(folder())
    assert snapshot.id.startswith("disk:")
    assert snapshot.artist_id == ""


def test_a_file_with_no_title_falls_back_to_its_filename() -> None:
    scanned = FakeScanned(tracks=[FakeTrack(Path("/music/x/07 - Untitled.flac"))])
    snapshot = folder_snapshot(scanned)
    assert snapshot.tracks[0].title == "07 - Untitled"


def test_search_terms_never_returns_a_bare_title() -> None:
    """``catalog/search`` is one free-text field; a bare title matches half of it."""
    assert search_terms([mb_release()]) == ("Joanne Shaw Taylor White Sugar",)


def test_search_terms_reads_nothing_but_musicbrainz() -> None:
    """No parameter for the folder exists any more — that is the guarantee."""
    import inspect

    assert list(inspect.signature(search_terms).parameters) == ["releases", "limit"]


def test_search_terms_pairs_every_credit_with_every_title() -> None:
    queries = search_terms(
        [
            mb_release("a", title="The Nutcracker", credits=("Tchaikovsky",)),
            mb_release("b", title="Nutcracker Suite", credits=("André Previn",)),
        ]
    )
    assert queries == (
        "Tchaikovsky The Nutcracker",
        "André Previn The Nutcracker",
        "Tchaikovsky Nutcracker Suite",
        "André Previn Nutcracker Suite",
    )


def test_search_terms_is_bounded() -> None:
    """Each query is a request; a heavily-credited classical release has many."""
    releases = [
        mb_release(str(n), title=f"T{n}", credits=tuple(f"C{m}" for m in range(5)))
        for n in range(5)
    ]
    assert len(search_terms(releases)) == 6


def test_search_terms_is_empty_when_musicbrainz_named_nothing() -> None:
    """And the caller must treat that as "no search", not as "use the folder"."""
    assert search_terms([mb_release(title="", credits=())]) == ()
