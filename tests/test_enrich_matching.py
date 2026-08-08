"""The pure rules: what counts as a match, and when a majority exists.

No database, no network, no ORM. Everything here is a predicate, which is the
point — a rule that cannot be checked by hand does not belong in
:mod:`app.enrich.matching`.

The four ideas being pinned down:

* **Exact or nothing.** A barcode that matches character-for-character, a title
  that matches after the same normalisation the de-duplicator already uses, or a
  human. Never a relevance score, never a closest hit, never a tie-break.
* **A value is only ever used as what it is.** A Qobuz album id is not a
  barcode, however much it looks like one, and the tests below carry the
  measurement that proves it.
* **A name may reject a candidate, never select one.** ``verify_artist_name``
  and ``main_credit`` are refusals: something else proposes an identity and the
  name is the veto.
* **More than half.** A value only overwrites Qobuz when a real majority of the
  sources that have an opinion agree. Two against two is a coin flip, and
  resolving a coin flip in favour of changing someone's library is the wrong
  default.
"""

from __future__ import annotations

from datetime import date

import pytest

from app.enrich import matching
from app.enrich.matching import (
    VARIOUS_ARTISTS_MBID,
    barcode_candidates,
    barcode_check_digit_ok,
    album_claimed_barcode,
    barcode_from_claim,
    barcodes_match,
    main_credit,
    names_match,
    normalize_barcode,
    normalize_isni,
    normalize_mbid,
    sole_main_credit,
    sole_match,
    titles_match,
    verify_artist_name,
    years_close,
)
from app.enrich.merge import (
    WRITABLE_FIELDS,
    consensus,
    decode_consensus,
    encode_consensus,
    release_type_from_deezer,
    release_type_from_musicbrainz,
    release_type_from_qobuz,
)


# ---------------------------------------------------------------------------
# Barcodes
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("0804879535645", "0804879535645"),
        ("724384960650", "724384960650"),
        ("0 04 4003 16162 7", "0044003161627"),
        ("00044003161627", "0044003161627"),  # 14 digits, leading zero stripped
        ("uyej1o165e870", None),  # a Qobuz id is not a barcode
        ("12345", None),
        ("", None),
        (None, None),
    ],
)
def test_normalize_barcode(raw: str | None, expected: str | None) -> None:
    assert normalize_barcode(raw) == expected


def test_a_qobuz_album_id_is_never_a_barcode() -> None:
    """The fallback this used to have, and the measurement that removed it.

    A Qobuz numeric id resembles a barcode closely enough to pass every test of
    shape and still be a different number. ``0060249867260`` is what Qobuz calls
    Mark Knopfler's *Shangri-La*; the barcode is ``602498672600``, MusicBrainz
    holds it, and the two are one digit-shift apart. On the live database that
    single fallback produced *all 25* "no MusicBrainz release carries barcode …"
    failures, while 6 of the 13 matches that worked rested on it — the split
    falling by which label numbered a release, not by anything about the music.
    """
    # The only way the id can be searched for is if a caller passes it as a
    # barcode: it is not a form of the real one, and there is no longer a
    # parameter that means "the album id". A release with no declared barcode
    # therefore has no key at all, which is exactly what the providers now see.
    assert "0060249867260" not in barcode_candidates("602498672600")
    assert barcode_candidates(None, None) == ()


def test_the_check_digit_does_not_rescue_the_album_id() -> None:
    """Why the fix is to stop reading the field, not to validate it harder.

    Five of eight sampled Qobuz ids fail the GS1 checksum — but the one that
    motivated the change is not among them: shifting a barcode by a digit can
    leave the checksum intact, so *Shangri-La*'s id validates perfectly and is
    still the wrong number.
    """
    assert barcode_check_digit_ok("602498672600"), "the real barcode"
    assert barcode_check_digit_ok("0060249867260"), "and the id that is not it"
    assert not barcode_check_digit_ok("0804879535646")
    assert not barcode_check_digit_ok("uyej1o165e870")


def test_both_upc12_and_ean13_forms_are_offered() -> None:
    """MusicBrainz stores barcodes as they were typed, so both forms must be tried."""
    assert barcode_candidates("724384960650") == ("724384960650", "0724384960650")


def test_a_declared_upc_outranks_a_barcode_learned_later() -> None:
    """Every argument is a field that *is* a barcode; order is order of trust."""
    candidates = barcode_candidates("0190296799778", "0804879535645")
    assert candidates[0] == "0190296799778"
    assert "0804879535645" in candidates


def test_no_barcode_anywhere_is_an_empty_tuple() -> None:
    """Callers must treat this as 'no key', never as a reason to search by title."""
    assert barcode_candidates(None, "uyej1o165e870") == ()


# ---------------------------------------------------------------------------
# A barcode a file claims for itself
# ---------------------------------------------------------------------------
def test_a_file_may_supply_the_barcode_the_catalogue_lacks() -> None:
    """Where a key legitimately comes back after the album-id fallback went.

    A ``BARCODE`` tag is a field that *is* a barcode, and mutagen hands the value
    over as a list.
    """
    assert barcode_from_claim({"BARCODE": ["602498672600"]}) == "602498672600"
    assert barcode_from_claim({"TXXX:UPC": "0602498672600"}) == "0602498672600"


def test_barcode_outranks_upc_whatever_order_the_tags_arrive_in() -> None:
    tags = {"upc": "0724384960650", "barcode": "602498672600"}
    assert barcode_from_claim(tags) == "602498672600"
    assert barcode_from_claim(dict(reversed(list(tags.items())))) == "602498672600"


def test_a_claim_that_is_not_a_barcode_is_refused() -> None:
    """Nothing vouches for a tag: taggers put catalogue numbers and free text in
    these fields, so the check digit is what separates a barcode from a number."""
    assert barcode_from_claim({"barcode": "JRA-2016"}) is None
    assert barcode_from_claim({"barcode": "602498672601"}) is None, "check digit"
    assert barcode_from_claim({"catalognumber": "602498672600"}) is None
    assert barcode_from_claim({}) is None
    assert barcode_from_claim(None) is None


def test_barcodes_match_across_forms_but_not_across_products() -> None:
    assert barcodes_match("724384960650", "0724384960650")
    assert barcodes_match("0804879535645", "0804879535645")
    assert not barcodes_match("0804879535645", "0804879535646")
    assert not barcodes_match(None, "0804879535645")


def test_an_album_barcode_needs_only_the_files_that_claim_one() -> None:
    """Untagged files are silent, not dissenting.

    Most libraries are full of files carrying no barcode at all, and requiring
    unanimity across every file would let one untagged interlude silence a folder
    Picard had otherwise done correctly.
    """
    assert album_claimed_barcode(["602498672600", None, "", "602498672600"]) == (
        "602498672600"
    )
    assert album_claimed_barcode([None, None]) is None
    assert album_claimed_barcode([]) is None


def test_the_two_forms_of_one_product_are_one_claim() -> None:
    """Some taggers write the UPC-A, some the EAN-13; that is not a disagreement."""
    assert album_claimed_barcode(["0602498672600", "602498672600"]) is not None


def test_files_claiming_two_barcodes_supply_none() -> None:
    """Disagreement is a refusal, never a vote.

    Two barcodes under one directory means it is not one release — a rip merged
    with a bonus disc, a hand-assembled compilation — and that is exactly when
    picking a winner would write a barcode into files it was never true of.
    A majority would be a tie-break, and there are none here.
    """
    assert album_claimed_barcode(["602498672600", "0804879535645"]) is None
    assert (
        album_claimed_barcode(["602498672600", "602498672600", "0804879535645"]) is None
    ), "the majority does not win"


def test_a_catalogue_number_cannot_outvote_a_barcode() -> None:
    """Junk is discarded by the check digit before it can disagree with anything."""
    assert album_claimed_barcode(["602498672600", "JRA-2016", "602498672601"]) == (
        "602498672600"
    )


# ---------------------------------------------------------------------------
# Titles, names, years
# ---------------------------------------------------------------------------
def test_titles_match_across_editions() -> None:
    """Editions of one record agree; telling them apart is the barcode's job."""
    assert titles_match("Rumours (Deluxe Edition)", "Rumours")
    assert titles_match("Sign o' the Times", "Sign O The Times")
    assert not titles_match("Blues Of Desperation", "Blues Deluxe")


def test_an_empty_title_never_matches_anything() -> None:
    assert not titles_match("", "")
    assert not titles_match(None, "Rumours")


def test_names_match_folds_accents_but_keeps_bands_apart() -> None:
    assert names_match(
        "Thorbjørn Risager & The Black Tornado", "Thorbjorn Risager & The Black Tornado"
    )
    # Punctuation folds to a space, so an ampersand does not have to be spelled
    # the same way — but it is *not* read as the word "and".
    assert names_match("Simon & Garfunkel", "Simon and Garfunkel") is False
    assert names_match("Simon & Garfunkel", "Simon  &  Garfunkel")
    # Two acts, two discographies — Qobuz lists them separately and so do we.
    assert not names_match("Robert Cray", "The Robert Cray Band")
    # The failure the importer's doctrine exists for.
    assert not names_match("Joanne Shaw Taylor", "Joanna Shaw Taylor")


def test_unknown_years_are_permissive_but_wrong_ones_are_not() -> None:
    assert years_close(2016, 2016)
    assert years_close(date(2016, 3, 25), "2017-01-01")
    assert years_close(None, 2016), "a year is corroboration, not evidence"
    assert not years_close(2016, 2019)


# ---------------------------------------------------------------------------
# Turning candidates into an answer
# ---------------------------------------------------------------------------
def test_sole_match_refuses_to_break_a_tie() -> None:
    winner, survivors = sole_match([1, 2, 3, 4], lambda n: n % 2 == 0)
    assert winner is None, "two survivors is ambiguous, not 'pick the first'"
    assert survivors == [2, 4]


def test_sole_match_returns_the_unique_survivor() -> None:
    winner, survivors = sole_match([1, 2, 3], lambda n: n == 2)
    assert winner == 2 and survivors == [2]


def test_sole_match_with_nothing_left() -> None:
    assert sole_match([1, 2], lambda n: n > 5) == (None, [])


# ---------------------------------------------------------------------------
# A name may reject a candidate, never select one
# ---------------------------------------------------------------------------
def mb_artist(name: str, *, sort_name: str = "", aliases: list[str] | None = None) -> dict:
    return {
        "id": "8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f11",
        "name": name,
        "sort-name": sort_name or name,
        "aliases": [{"name": alias, "sort-name": alias} for alias in aliases or []],
    }


def test_verify_artist_name_confirms_the_library_as_it_is_spelled() -> None:
    """Measured against the real library, including the awkward ones.

    The orchestra is carried here with a leading article MusicBrainz does not
    use, which ``artist_key`` strips from both sides; the band is a different act
    from its front man and is confirmed under its own name rather than his.
    """
    assert verify_artist_name(mb_artist("Joanne Shaw Taylor"), "Joanne Shaw Taylor")
    assert verify_artist_name(mb_artist("Robert Cray"), "Robert Cray")
    assert verify_artist_name(mb_artist("The Robert Cray Band"), "The Robert Cray Band")
    assert verify_artist_name(
        mb_artist("City of Prague Philharmonic Orchestra"),
        "The City of Prague Philharmonic Orchestra",
    )
    assert verify_artist_name(mb_artist("Molly Miller Trio"), "Molly Miller Trio")


def test_a_sort_name_or_an_alias_is_as_good_as_the_name() -> None:
    payload = mb_artist(
        "Pyotr Ilyich Tchaikovsky",
        sort_name="Tchaikovsky, Pyotr Ilyich",
        aliases=["Tchaikovsky", "Пётр Ильич Чайковский"],
    )
    assert verify_artist_name(payload, "Tchaikovsky, Pyotr Ilyich")
    assert verify_artist_name(payload, "Tchaikovsky")


def test_a_mistagged_mbid_is_rejected_by_the_name_on_it() -> None:
    """The failure this test exists for: a file claiming somebody else's id.

    Nothing else in the chain would notice — the MBID is well-formed, the artist
    exists, and the id would be written into every file on disk.
    """
    assert not verify_artist_name(mb_artist("Mark Knopfler"), "Joanne Shaw Taylor")


def test_a_spelling_no_alias_covers_is_refused_and_that_is_correct() -> None:
    """MusicBrainz publishes 92 aliases for Tchaikovsky and this library's French
    spelling is not one of them.

    The honest answer to "is this the same person?" from a name alone is no. The
    review list is where a human says otherwise, in one press — inventing a
    fuzzy threshold to cover this would also cover *Joanna* Shaw Taylor.
    """
    payload = mb_artist(
        "Pyotr Ilyich Tchaikovsky",
        sort_name="Tchaikovsky, Pyotr Ilyich",
        aliases=["Tchaikovsky", "Piotr Ilyitch Tchaïkovski", "Чайковский"],
    )
    assert not verify_artist_name(payload, "Pyotr Illitch Tchaikovski")


def test_verify_artist_name_needs_something_on_both_sides() -> None:
    assert not verify_artist_name(mb_artist("Robert Cray"), "")
    assert not verify_artist_name(None, "Robert Cray")
    assert not verify_artist_name({}, "Robert Cray")


# ---------------------------------------------------------------------------
# Deriving an artist without ever searching for one
# ---------------------------------------------------------------------------
def test_the_first_credit_identifies_the_artist() -> None:
    credits = [{"id": "abc", "name": "Joe Bonamassa", "joinphrase": ""}]
    assert main_credit(credits, expected_name="Joe Bonamassa") == "abc"


def test_a_collaboration_is_still_a_record_by_its_first_credit() -> None:
    """The gate that used to throw away three correct matches from this library.

    ``sole_main_credit`` demanded exactly one credit, so *Robert Cray, Hi
    Rhythm*, *The City of Prague Philharmonic Orchestra, Jen Brown* and *Molly
    Miller Trio, Tamir Barzilay, Andre De Santanna* all identified nobody — which
    is why the audio contributed nothing to artist matching at all. Billing order
    says who a record is by; the name check on that first credit is what keeps it
    exact.
    """
    credits = [
        {"id": "cray", "name": "Robert Cray", "joinphrase": ", "},
        {"id": "rhythm", "name": "Hi Rhythm", "joinphrase": ""},
    ]
    assert main_credit(credits, expected_name="Robert Cray") == "cray"
    assert sole_main_credit(credits, expected_name="Robert Cray") is None


def test_a_guest_spot_has_a_different_first_credit() -> None:
    """Which is the whole reason taking the first one is safe."""
    credits = [
        {"id": "hart", "name": "Beth Hart", "joinphrase": " & "},
        {"id": "joe", "name": "Joe Bonamassa", "joinphrase": ""},
    ]
    assert main_credit(credits, expected_name="Joe Bonamassa") is None
    assert main_credit(credits, expected_name="Beth Hart") == "hart"


def test_various_artists_is_rejected_by_id_however_it_is_credited() -> None:
    credits = [
        {"id": VARIOUS_ARTISTS_MBID, "name": "Various Artists", "joinphrase": ""}
    ]
    assert main_credit(credits, expected_name="Various Artists") is None


def test_no_credits_at_all_identifies_nobody() -> None:
    assert main_credit([], expected_name="Joe Bonamassa") is None


def test_a_deezer_style_first_contributor_must_still_be_a_main_one() -> None:
    credits = [{"id": "1424", "name": "Joe Bonamassa", "role": "Main"}]
    assert main_credit(credits, expected_name="Joe Bonamassa", role_key="role") == "1424"

    featured = [{"id": "1424", "name": "Joe Bonamassa", "role": "Featured"}]
    assert main_credit(featured, expected_name="Joe Bonamassa", role_key="role") is None


def test_a_single_clean_credit_identifies_the_artist() -> None:
    credits = [{"id": "abc", "name": "Joe Bonamassa", "joinphrase": ""}]
    assert sole_main_credit(credits, expected_name="Joe Bonamassa") == "abc"


def test_a_collaboration_identifies_nobody() -> None:
    credits = [
        {"id": "abc", "name": "Beth Hart", "joinphrase": " & "},
        {"id": "def", "name": "Joe Bonamassa", "joinphrase": ""},
    ]
    assert sole_main_credit(credits, expected_name="Joe Bonamassa") is None


def test_a_feat_joinphrase_is_rejected_even_when_alone() -> None:
    credits = [{"id": "abc", "name": "Joe Bonamassa", "joinphrase": " feat. "}]
    assert sole_main_credit(credits, expected_name="Joe Bonamassa") is None


def test_various_artists_is_rejected_by_id() -> None:
    """It is credited on every compilation; deriving from it would poison a library."""
    credits = [
        {"id": VARIOUS_ARTISTS_MBID, "name": "Various Artists", "joinphrase": ""}
    ]
    assert sole_main_credit(credits, expected_name="Various Artists") is None


def test_a_guest_appearance_cannot_rewrite_the_artist() -> None:
    """The credited name must be the artist being enriched, not merely *an* artist."""
    credits = [{"id": "xyz", "name": "Eric Clapton", "joinphrase": ""}]
    assert sole_main_credit(credits, expected_name="Joe Bonamassa") is None


def test_a_sort_name_also_counts_as_a_match() -> None:
    credits = [
        {"id": "abc", "name": "Bonamassa, Joe", "sort-name": "Joe Bonamassa", "joinphrase": ""}
    ]
    assert sole_main_credit(credits, expected_name="Joe Bonamassa") == "abc"


def test_deezer_style_credits_use_a_role_instead_of_a_joinphrase() -> None:
    credits = [{"id": "1424", "name": "Joe Bonamassa", "role": "Main"}]
    assert (
        sole_main_credit(
            credits, expected_name="Joe Bonamassa", join_key=None, role_key="role"
        )
        == "1424"
    )
    featured = [{"id": "1424", "name": "Joe Bonamassa", "role": "Featured"}]
    assert (
        sole_main_credit(
            featured, expected_name="Joe Bonamassa", join_key=None, role_key="role"
        )
        is None
    )


# ---------------------------------------------------------------------------
# Identifiers
# ---------------------------------------------------------------------------
def test_mbids_are_validated_not_trusted() -> None:
    assert normalize_mbid("89AD4AC3-39F7-470E-963A-56509C546377") == VARIOUS_ARTISTS_MBID
    assert normalize_mbid("not-a-uuid") is None
    assert normalize_mbid(None) is None


def test_isni_normalises_to_sixteen_characters() -> None:
    assert normalize_isni("0000 0001 2103 2683") == "0000000121032683"
    assert normalize_isni("0000-0001-2103-268X") == "000000012103268X"
    assert normalize_isni("0000 0001 2103") is None


# ---------------------------------------------------------------------------
# Consensus
# ---------------------------------------------------------------------------
def test_two_of_three_is_a_majority() -> None:
    verdict = consensus(
        "release_type", {"qobuz": "single", "deezer": "ep", "musicbrainz": "ep"}
    )
    assert verdict.decided and verdict.value == "ep"
    assert (verdict.agreed, verdict.total) == (2, 3)


def test_a_tie_changes_nothing() -> None:
    """Two against two is a coin flip, and Qobuz keeps what it had."""
    verdict = consensus(
        "release_type",
        {"qobuz": "album", "deezer": "album", "musicbrainz": "live", "wikidata": "live"},
    )
    assert not verdict.decided
    assert verdict.is_split


def test_exactly_half_is_not_more_than_half() -> None:
    verdict = consensus("release_type", {"qobuz": "single", "deezer": "ep"})
    assert not verdict.decided


def test_a_lone_opinion_is_unanimous() -> None:
    """With one source enabled its answer is all the evidence there is."""
    verdict = consensus("release_type", {"deezer": "ep"})
    assert verdict.decided and verdict.value == "ep"


def test_sources_with_no_opinion_do_not_get_to_abstain_against_the_rest() -> None:
    verdict = consensus(
        "release_type", {"qobuz": None, "deezer": "ep", "musicbrainz": "ep"}
    )
    assert verdict.decided and (verdict.agreed, verdict.total) == (2, 2)


def test_no_opinions_at_all_decides_nothing() -> None:
    verdict = consensus("release_type", {"qobuz": None, "deezer": None})
    assert not verdict.decided and verdict.total == 0


def test_a_three_way_split_decides_nothing() -> None:
    verdict = consensus(
        "release_type", {"qobuz": "album", "deezer": "ep", "musicbrainz": "single"}
    )
    assert not verdict.decided


def test_raising_the_threshold_demands_unanimity() -> None:
    votes = {"qobuz": "single", "deezer": "ep", "musicbrainz": "ep"}
    assert consensus("release_type", votes, threshold=0.51).decided
    assert not consensus("release_type", votes, threshold=1.0).decided


def test_a_threshold_below_a_half_is_clamped_up() -> None:
    """A 'majority' of 25% is not a majority; the setting cannot ask for one."""
    verdict = consensus(
        "release_type",
        {"qobuz": "album", "deezer": "ep", "musicbrainz": "live", "wikidata": "single"},
        threshold=0.2,
    )
    assert not verdict.decided


# ---------------------------------------------------------------------------
# Each source's vocabulary
# ---------------------------------------------------------------------------
def test_musicbrainz_secondary_types_outrank_the_primary_one() -> None:
    """A live album's primary type is still 'Album'; 'live' is the useful answer."""
    assert release_type_from_musicbrainz("Album", ["Live"]) == "live"
    assert release_type_from_musicbrainz("Album", ["Compilation"]) == "compilation"
    assert release_type_from_musicbrainz("Album", []) == "album"
    assert release_type_from_musicbrainz("EP", None) == "ep"
    assert release_type_from_musicbrainz("Broadcast", None) == "other"
    assert release_type_from_musicbrainz(None, None) is None


def test_deezer_record_types_map_across() -> None:
    assert release_type_from_deezer("compile") == "compilation"
    assert release_type_from_deezer("EP") == "ep"
    assert release_type_from_deezer("") is None


def test_qobuz_other_is_an_abstention_not_a_vote() -> None:
    """'We do not know' must not outvote two sources that do."""
    assert release_type_from_qobuz("other") is None
    assert release_type_from_qobuz("album") == "album"
    assert release_type_from_qobuz(None) is None


# ---------------------------------------------------------------------------
# Storage, and the boundary of what may be written
# ---------------------------------------------------------------------------
def test_only_release_type_may_ever_be_written_back() -> None:
    """``label`` and ``genre`` are naming-template tokens.

    Adding either here would make the next re-file want to move every folder
    they appear in — thousands of directories, from a metadata refresh.
    """
    assert WRITABLE_FIELDS == {"release_type"}


def test_the_split_survives_a_round_trip_so_a_human_can_see_it() -> None:
    verdicts = [
        consensus("release_type", {"qobuz": "album", "deezer": "compilation"}),
    ]
    decoded = decode_consensus(encode_consensus(verdicts))
    assert decoded["release_type"]["value"] is None
    assert decoded["release_type"]["votes"] == {
        "qobuz": "album",
        "deezer": "compilation",
    }


def test_decoding_junk_is_empty_rather_than_fatal() -> None:
    assert decode_consensus(None) == {}
    assert decode_consensus("not json") == {}
    assert decode_consensus("[1, 2]") == {}


# ---------------------------------------------------------------------------
# The last link: MusicBrainz identity -> Qobuz album -> Qobuz artist
#
# There is no barcode endpoint on Qobuz, so the only way in is a free-text
# ``catalog/search``. That makes this the one place a name is sent upstream in
# the automatic path, and the rule that keeps it honest is the same one
# ``verify_artist_name`` states from the other side: the name narrows, the
# barcode selects.


def _qobuz_item(album_id, upc, *, artist_id=None, artist_name=None, title="A Record"):
    """A ``catalog/search`` album item, shaped like the real payload."""
    item = {"id": album_id, "upc": upc, "title": title}
    if artist_id is not None or artist_name is not None:
        item["artist"] = {"id": artist_id, "name": artist_name}
    return item


def test_the_barcode_selects_and_the_name_does_not() -> None:
    items = [
        _qobuz_item("aaa", "0884385226442", title="White Sugar"),
        _qobuz_item("bbb", "5051083067188", title="White Sugar"),
    ]
    winner, survivors = matching.qobuz_album_by_barcode(items, ["0884385226442"])
    assert winner is not None and winner["id"] == "aaa"
    assert len(survivors) == 1


def test_an_edition_of_the_same_release_group_still_matches() -> None:
    """The pressing Qobuz sells is usually not the one MusicBrainz matched.

    Measured on this library: Joanne Shaw Taylor's *White Sugar* is
    ``710347114727`` in ``album_metadata`` and ``0884385226442`` on Qobuz. Two
    real barcodes, two real editions, one record — so the caller passes every
    barcode in the release group and the two meet.
    """
    items = [_qobuz_item("aaa", "0884385226442")]
    missed, _ = matching.qobuz_album_by_barcode(items, ["710347114727"])
    assert missed is None

    found, _ = matching.qobuz_album_by_barcode(
        items, ["710347114727", "0884385226442"]
    )
    assert found is not None and found["id"] == "aaa"


def test_two_matching_editions_are_ambiguous_not_the_first_one() -> None:
    """No tie-break here either; a human decides."""
    items = [_qobuz_item("aaa", "0884385226442"), _qobuz_item("bbb", "0884385226442")]
    winner, survivors = matching.qobuz_album_by_barcode(items, ["0884385226442"])
    assert winner is None
    assert len(survivors) == 2


def test_no_usable_barcode_matches_nothing_rather_than_everything() -> None:
    items = [_qobuz_item("aaa", "0884385226442")]
    for useless in ([], [None], [""], ["JRA-2016"], ["12345"]):
        winner, survivors = matching.qobuz_album_by_barcode(items, useless)
        assert winner is None, useless
        assert survivors == [], useless


def test_a_qobuz_album_with_no_upc_never_matches() -> None:
    items = [_qobuz_item("aaa", None), _qobuz_item("bbb", "")]
    winner, survivors = matching.qobuz_album_by_barcode(items, ["0884385226442"])
    assert winner is None
    assert survivors == []


def test_the_leading_zero_form_is_the_same_barcode() -> None:
    """``normalize_barcode`` strips a 14th leading zero; both directions must."""
    items = [_qobuz_item("aaa", "00884385226442")]
    winner, _ = matching.qobuz_album_by_barcode(items, ["0884385226442"])
    assert winner is not None


def test_the_credited_artist_comes_back_as_a_string_id() -> None:
    """Qobuz sends artist ids as numbers; every id column here is String."""
    item = _qobuz_item("aaa", "0884385226442", artist_id=1464909, artist_name="Joanne Shaw Taylor")
    assert matching.qobuz_artist_credit(item) == ("1464909", "Joanne Shaw Taylor")


def test_an_album_with_no_credit_yields_no_artist() -> None:
    assert matching.qobuz_artist_credit(None) == (None, None)
    assert matching.qobuz_artist_credit({"id": "aaa"}) == (None, None)
    assert matching.qobuz_artist_credit(_qobuz_item("aaa", "1", artist_name="  ")) == (
        None,
        None,
    )
