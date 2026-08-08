"""The album coverage solve: which release a directory of files actually is.

Pure input, pure output, no fixtures and no I/O — every test here is the same
shape as the module it exercises. What is being pinned down:

* a release is admitted only when it seats every file it explains at a distinct
  position, explains essentially all of them, and has as many tracks as the
  directory has files;
* one admissible release identifies, none is unidentified, several is ambiguous;
* and **nothing** breaks a tie. Several tests below exist purely to fail if
  somebody adds a "highest coverage wins" branch, because that branch is what
  every naive version of this reaches for and it is how a wrong
  ``MUSICBRAINZ_ALBUMID`` gets written into fifteen files at once.

The Mark Knopfler case is real and measured: for one file of *Down the Road
Wherever*, the fingerprint index offers both that album and a promo called *On
the Road to Milano*, and the two are separated not by a score but by the fact
that one accounts for 15 of the 15 files in the folder and the other for 1.
"""

from __future__ import annotations

import pytest

from app.enrich.coverage import (
    MIN_COVERAGE,
    CoverageResult,
    FileCandidates,
    RecordingCandidate,
    ReleaseFacts,
    ReleaseSlot,
    Verdict,
    minimum_explained,
    solve_album,
)
from app.enrich.errors import STATE_AMBIGUOUS, STATE_NOT_FOUND, STATE_OK


# ---------------------------------------------------------------------------
# Builders — kept tiny so the tests read as the tracklists they are
# ---------------------------------------------------------------------------
def _rec(recording_id: str, *slots: ReleaseSlot) -> RecordingCandidate:
    return RecordingCandidate(recording_id, slots)


def _on(release_id: str, position: int | None = None, medium: int = 1) -> ReleaseSlot:
    return ReleaseSlot(release_id, medium, position)


def _file(file_id: str, *recordings: RecordingCandidate) -> FileCandidates:
    return FileCandidates(file_id, recordings)


DOWN_THE_ROAD = "mb-down-the-road-wherever"
MILANO = "mb-on-the-road-to-milano"


def _knopfler_folder() -> list[FileCandidates]:
    """Fifteen files, one of which is also published on the Milano promo."""
    folder = []
    for number in range(1, 16):
        slots = [_on(DOWN_THE_ROAD, number)]
        if number == 4:
            slots.append(_on(MILANO, 1))
        folder.append(
            _file(f"{number:02d} - track.flac", _rec(f"rec-{number}", *slots))
        )
    return folder


def _knopfler_releases() -> list[ReleaseFacts]:
    return [
        ReleaseFacts(DOWN_THE_ROAD, 15, "Down the Road Wherever", "rg-down-the-road"),
        # The promo is given the *same* track count as the album on purpose, so
        # the only thing left standing between it and an identification is
        # coverage. If it were rejected on its track count the measured case
        # would prove nothing about the requirement it was measured for.
        ReleaseFacts(MILANO, 15, "On the Road to Milano", "rg-milano"),
    ]


# ---------------------------------------------------------------------------
# The measured case
# ---------------------------------------------------------------------------
def test_the_release_that_accounts_for_every_file_is_the_answer() -> None:
    result = solve_album(_knopfler_folder(), _knopfler_releases())

    assert result.verdict is Verdict.IDENTIFIED
    assert result.release_id == DOWN_THE_ROAD
    evidence = result.evidence
    assert evidence is not None
    assert len(evidence.explained) == 15
    assert evidence.unexplained == ()
    assert evidence.admissible


def test_one_file_of_fifteen_is_rejected_on_coverage_alone() -> None:
    """The promo is not beaten on points; it fails a requirement outright."""
    result = solve_album(_knopfler_folder(), _knopfler_releases())
    promo = next(item for item in result.considered if item.release_id == MILANO)

    assert promo.explained == ("04 - track.flac",)
    assert promo.covers is False
    # Everything else about it is fine, which is exactly why coverage has to be
    # a requirement rather than a term in a score.
    assert promo.counts_match is True
    assert promo.injective is True
    assert promo.admissible is False


def test_the_verdict_does_not_depend_on_the_order_of_the_directory() -> None:
    folder = _knopfler_folder()
    forwards = solve_album(folder, _knopfler_releases())
    backwards = solve_album(list(reversed(folder)), _knopfler_releases())

    assert forwards.verdict is backwards.verdict
    assert forwards.release_id == backwards.release_id


# ---------------------------------------------------------------------------
# Nothing admissible
# ---------------------------------------------------------------------------
def test_no_admissible_release_is_unidentified() -> None:
    """Only the promo is on offer, and it accounts for one file in fifteen."""
    result = solve_album(
        _knopfler_folder(), [ReleaseFacts(MILANO, 15, "On the Road to Milano")]
    )

    assert result.verdict is Verdict.UNIDENTIFIED
    assert result.release_id is None
    assert result.candidates == ()
    assert result.evidence is None
    # The rejected releases are still reported: a refusal nobody can read is
    # indistinguishable from a matcher that did nothing.
    assert {item.release_id for item in result.considered} == {DOWN_THE_ROAD, MILANO}


def test_an_empty_directory_identifies_nothing() -> None:
    result = solve_album([], [ReleaseFacts(DOWN_THE_ROAD, 15)])

    assert result.verdict is Verdict.UNIDENTIFIED
    assert "no files" in result.reason


def test_a_directory_the_index_has_never_heard_of_identifies_nothing() -> None:
    folder = [_file("01.flac"), _file("02.flac"), _file("03.flac")]

    result = solve_album(folder, [ReleaseFacts(DOWN_THE_ROAD, 3)])

    assert result.verdict is Verdict.UNIDENTIFIED
    assert result.considered == ()


# ---------------------------------------------------------------------------
# Several admissible — editions, not a contest
# ---------------------------------------------------------------------------
def _two_editions(*, groups: tuple[str | None, str | None]) -> CoverageResult:
    """Twelve files published identically on a UK and a US pressing."""
    folder = [
        _file(
            f"{number:02d}.flac",
            _rec(f"rec-{number}", _on("rel-uk", number), _on("rel-us", number)),
        )
        for number in range(1, 13)
    ]
    return solve_album(
        folder,
        [
            ReleaseFacts("rel-uk", 12, "Brothers in Arms", groups[0]),
            ReleaseFacts("rel-us", 12, "Brothers in Arms", groups[1]),
        ],
    )


def test_two_admissible_releases_are_ambiguous() -> None:
    result = _two_editions(groups=("rg-brothers", "rg-brothers"))

    assert result.verdict is Verdict.AMBIGUOUS
    assert result.release_id is None
    assert {item.release_id for item in result.candidates} == {"rel-uk", "rel-us"}


def test_ambiguous_editions_fall_back_to_their_release_group() -> None:
    """Which pressing is unknowable from the audio; which record is not."""
    result = _two_editions(groups=("rg-brothers", "rg-brothers"))

    assert result.release_group_id == "rg-brothers"


def test_candidates_from_different_groups_have_no_shared_group() -> None:
    result = _two_editions(groups=("rg-brothers", "rg-something-else"))

    assert result.verdict is Verdict.AMBIGUOUS
    assert result.release_group_id is None


def test_a_group_is_not_derived_from_some_of_the_candidates() -> None:
    result = _two_editions(groups=("rg-brothers", None))

    assert result.release_group_id is None


def test_more_coverage_never_wins() -> None:
    """The one branch this module must never grow.

    Two releases of fifteen tracks: one accounts for all fifteen files, the
    other for twelve — above the threshold, so both are admissible. A solver
    with a tie-break returns the fifteen. This one returns a question.
    """
    folder = []
    for number in range(1, 16):
        slots = [_on("rel-complete", number)]
        if number <= 12:
            slots.append(_on("rel-partial", number))
        folder.append(_file(f"{number:02d}.flac", _rec(f"rec-{number}", *slots)))

    result = solve_album(
        folder,
        [
            ReleaseFacts("rel-complete", 15, "Complete", "rg-a"),
            ReleaseFacts("rel-partial", 15, "Partial", "rg-b"),
        ],
    )

    assert minimum_explained(15) == 12
    assert result.verdict is Verdict.AMBIGUOUS
    assert result.release_id is None
    assert len(result.candidates) == 2


# ---------------------------------------------------------------------------
# Injectivity
# ---------------------------------------------------------------------------
def test_two_files_on_one_position_defeat_a_release_with_the_right_count() -> None:
    """A collision is not a contest between the two files. It sinks the release.

    Both files resolve to the same recording, which sits at track 3. The release
    has exactly as many tracks as the folder has files and accounts for both of
    them, so every requirement except injectivity is satisfied — and it must
    still not identify.
    """
    folder = [
        _file("a.flac", _rec("rec-3", _on("rel-x", 3))),
        _file("b.flac", _rec("rec-3", _on("rel-x", 3))),
    ]

    result = solve_album(folder, [ReleaseFacts("rel-x", 2, "Two Tracks")])
    candidate = result.considered[0]

    assert candidate.covers is True
    assert candidate.counts_match is True
    assert candidate.injective is False
    assert result.verdict is Verdict.UNIDENTIFIED


def test_a_file_is_moved_aside_rather_than_blocking_another() -> None:
    """Seating is a matching problem, not a first-fit walk.

    The first file listed could sit at track 1 or track 2; the second can only
    sit at track 1. Seating them in listed order takes track 1 with the file
    that had a choice and then fails. Both are seatable, and the answer must not
    depend on which order the directory happened to yield.
    """
    folder = [
        _file(
            "flexible.flac",
            _rec("rec-a", _on("rel-x", 1)),
            _rec("rec-b", _on("rel-x", 2)),
        ),
        _file("fixed.flac", _rec("rec-c", _on("rel-x", 1))),
    ]

    result = solve_album(folder, [ReleaseFacts("rel-x", 2, "Two Tracks")])

    assert result.verdict is Verdict.IDENTIFIED
    assert result.release_id == "rel-x"
    assert dict(result.evidence.seating) == {"flexible.flac": "1/2", "fixed.flac": "1/1"}


def test_the_same_track_number_on_two_discs_is_two_seats() -> None:
    folder = [
        _file("d1t1.flac", _rec("rec-a", _on("rel-x", 1, medium=1))),
        _file("d2t1.flac", _rec("rec-b", _on("rel-x", 1, medium=2))),
    ]

    result = solve_album(folder, [ReleaseFacts("rel-x", 2, "Double")])

    assert result.verdict is Verdict.IDENTIFIED


def test_an_unplaced_recording_falls_back_to_its_own_identity() -> None:
    """No position stated is not a free pass, and not a veto either.

    AcoustID's release ids arrive without positions. Two files resolving to
    *different* recordings may be two different tracks; two files resolving to
    the *same* recording are the same track twice however it is numbered, and
    that collision is still caught.
    """
    distinct = [
        _file("a.flac", _rec("rec-a", _on("rel-x"))),
        _file("b.flac", _rec("rec-b", _on("rel-x"))),
    ]
    duplicated = [
        _file("a.flac", _rec("rec-a", _on("rel-x"))),
        _file("b.flac", _rec("rec-a", _on("rel-x"))),
    ]
    facts = [ReleaseFacts("rel-x", 2, "Two Tracks")]

    assert solve_album(distinct, facts).verdict is Verdict.IDENTIFIED
    assert solve_album(duplicated, facts).verdict is Verdict.UNIDENTIFIED
    assert solve_album(duplicated, facts).considered[0].injective is False


# ---------------------------------------------------------------------------
# Coverage
# ---------------------------------------------------------------------------
def test_a_file_with_no_candidates_at_all_is_absorbed() -> None:
    """A hole in the fingerprint index must not veto fourteen agreeing files."""
    folder = _knopfler_folder()
    folder[6] = _file("07 - track.flac")

    result = solve_album(folder, _knopfler_releases())
    evidence = result.evidence

    assert result.verdict is Verdict.IDENTIFIED
    assert evidence is not None
    assert evidence.unexplained == ("07 - track.flac",)
    assert len(evidence.explained) == 14


def test_too_many_unknown_files_stop_an_identification() -> None:
    folder = _knopfler_folder()
    for index in range(4):
        folder[index] = _file(f"{index + 1:02d} - track.flac")

    result = solve_album(folder, _knopfler_releases())

    assert result.verdict is Verdict.UNIDENTIFIED
    album = next(item for item in result.considered if item.release_id == DOWN_THE_ROAD)
    assert len(album.explained) == 11
    assert album.covers is False


@pytest.mark.parametrize(
    ("files", "required"),
    [(1, 1), (2, 2), (5, 4), (10, 8), (12, 10), (15, 12), (40, 32)],
)
def test_minimum_explained_rounds_up(files: int, required: int) -> None:
    """Rounded up, so a small folder has to be explained outright."""
    assert minimum_explained(files) == required
    assert required >= files * MIN_COVERAGE


# ---------------------------------------------------------------------------
# Track count
# ---------------------------------------------------------------------------
def test_a_box_set_holding_the_whole_album_is_rejected_on_its_track_count() -> None:
    """The anthology really does contain all fifteen, at fifteen distinct seats.

    Coverage and injectivity both pass. Only counting keeps it out, which is the
    entire reason the track count is a requirement and not corroboration.
    """
    folder = [
        _file(
            f"{number:02d}.flac",
            _rec(f"rec-{number}", _on("rel-album", number), _on("rel-box", 20 + number)),
        )
        for number in range(1, 16)
    ]

    result = solve_album(
        folder,
        [ReleaseFacts("rel-album", 15, "The Album"), ReleaseFacts("rel-box", 60, "Box")],
    )
    box = next(item for item in result.considered if item.release_id == "rel-box")

    assert box.covers is True
    assert box.injective is True
    assert box.counts_match is False
    assert result.verdict is Verdict.IDENTIFIED
    assert result.release_id == "rel-album"


def test_a_release_nobody_counted_is_never_admissible() -> None:
    folder = [
        _file(f"{number:02d}.flac", _rec(f"rec-{number}", _on("rel-x", number)))
        for number in range(1, 6)
    ]

    result = solve_album(folder, [ReleaseFacts("rel-x", None, "Uncounted")])

    assert result.verdict is Verdict.UNIDENTIFIED
    assert "counted" in result.considered[0].reason


def test_an_uncounted_release_cannot_make_a_match_ambiguous() -> None:
    """Forgetting to look a release up loses matches; it never invents one."""
    folder = [
        _file(
            f"{number:02d}.flac",
            _rec(f"rec-{number}", _on("rel-known", number), _on("rel-unknown", number)),
        )
        for number in range(1, 6)
    ]

    result = solve_album(folder, [ReleaseFacts("rel-known", 5, "Known")])

    assert result.verdict is Verdict.IDENTIFIED
    assert result.release_id == "rel-known"
    assert len(result.considered) == 2


def test_a_release_missing_a_track_is_not_this_folder() -> None:
    folder = [
        _file(f"{number:02d}.flac", _rec(f"rec-{number}", _on("rel-x", number)))
        for number in range(1, 6)
    ]

    result = solve_album(folder, [ReleaseFacts("rel-x", 6, "One More Track")])

    assert result.verdict is Verdict.UNIDENTIFIED
    assert result.considered[0].counts_match is False


# ---------------------------------------------------------------------------
# The realistic shape: several candidates per file
# ---------------------------------------------------------------------------
def test_three_candidates_a_file_and_one_release_consistent_with_all_of_them() -> None:
    """Every file offers the album, a live cut and a single. Only one survives.

    This is what the input really looks like — a fingerprint hit resolves to
    several recordings, each published in several places — and it is why the
    solve is stated over the folder rather than the file. No single file here
    distinguishes anything; the ten of them together leave one release standing.
    """
    folder = []
    for number in range(1, 11):
        folder.append(
            _file(
                f"{number:02d}.flac",
                _rec(
                    f"rec-studio-{number}",
                    _on("rel-album", number),
                    _on("rel-anthology", 20 + number),
                ),
                _rec(f"rec-live-{number}", _on("rel-live", number)),
                _rec(f"rec-single-{number}", _on(f"rel-single-{number}", 1)),
            )
        )

    releases = [
        ReleaseFacts("rel-album", 10, "The Album", "rg-album"),
        ReleaseFacts("rel-anthology", 40, "Anthology", "rg-anthology"),
        ReleaseFacts("rel-live", 14, "Live", "rg-live"),
        *[ReleaseFacts(f"rel-single-{number}", 2, "Single") for number in range(1, 11)],
    ]

    result = solve_album(folder, releases)

    assert result.verdict is Verdict.IDENTIFIED
    assert result.release_id == "rel-album"
    assert len(result.considered) == 13
    singles = [
        item for item in result.considered if item.release_id.startswith("rel-single")
    ]
    assert all(item.covers is False for item in singles)


# ---------------------------------------------------------------------------
# The verdict carries the state it becomes
# ---------------------------------------------------------------------------
def test_each_verdict_knows_its_enrichment_state() -> None:
    """Same contract as the outcome family: the caller writes it, not derives it."""
    assert CoverageResult(Verdict.IDENTIFIED).state == STATE_OK
    assert CoverageResult(Verdict.AMBIGUOUS).state == STATE_AMBIGUOUS
    assert CoverageResult(Verdict.UNIDENTIFIED).state == STATE_NOT_FOUND


def test_an_identification_carries_its_working() -> None:
    result = solve_album(_knopfler_folder(), _knopfler_releases())

    assert result.evidence is not None
    assert "15 of 15" in result.evidence.reason
    assert len(result.evidence.seating) == 15
    assert ("01 - track.flac", "1/1") in result.evidence.seating
