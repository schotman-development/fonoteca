"""Deciding whether a candidate from an open source is *this* release.

Pure, no I/O, no ORM. Every rule here is a predicate a human could check by hand,
and that is the point: this module is where "exact or nothing" is enforced, and a
rule that cannot be stated as a predicate does not belong in it.

The doctrine, in one sentence: **when in doubt, no match.** The bulk importer
already works this way, for the reason its docstring gives — Qobuz answers
``Joanne Shaw Taylor`` with a ``Joanna Shaw Taylor`` too, and monitoring a
stranger's discography is worse than monitoring nobody's. Enrichment's version is
worse still, because a wrong MusicBrainz id gets written into every file on disk
as ``MUSICBRAINZ_ARTISTID`` and other tools then believe it. So: no fuzzy
scoring, no Levenshtein, no closest-hit-wins, and no accepting a search engine's
relevance ranking as evidence. What survives is a barcode that matches
character-for-character, a title that matches after normalisation, or a human.

Two corollaries this module now enforces rather than assumes:

**A value is only ever used as what it is.** :func:`barcode_candidates` takes
barcodes from fields that hold barcodes and from nowhere else. It used to fall
back on the Qobuz album id, which resembles one closely enough to pass every
test of shape and still be a different number — that docstring records what it
cost.

**A name may reject a candidate, never select one.** :func:`verify_artist_name`
and :func:`main_credit` are both refusals: something else — a barcode, a
release's own credit order, a fingerprint — proposes an identity, and the name
is the veto. Nothing here turns a name into an id, because the module that did
would be a name search with extra steps.

Normalisers are borrowed rather than rewritten — :func:`app.core.indexer.dedupe_key`
for titles and :func:`app.core.scanner.artist_key` for names. One normaliser with
three callers cannot drift; two implementations of "the same title" will.
"""

from __future__ import annotations

import re
from typing import Any, Iterable, Mapping, Sequence

from app.core.indexer import dedupe_key
from app.core.scanner import artist_key

__all__ = [
    "VARIOUS_ARTISTS_MBID",
    "normalize_barcode",
    "barcode_check_digit_ok",
    "barcode_candidates",
    "barcode_from_claim",
    "album_claimed_barcode",
    "barcodes_match",
    "titles_match",
    "names_match",
    "verify_artist_name",
    "years_close",
    "sole_match",
    "qobuz_album_by_barcode",
    "qobuz_artist_credit",
    "normalize_mbid",
    "normalize_isni",
    "main_credit",
    "sole_main_credit",
]

#: MusicBrainz's "Various Artists" placeholder. It is credited on every
#: compilation, so deriving an artist from it would quietly point half a library
#: at the same wrong entity. Rejected by name, everywhere, always.
VARIOUS_ARTISTS_MBID = "89ad4ac3-39f7-470e-963a-56509c546377"

_DIGITS_RE = re.compile(r"\D+")
_UUID_RE = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
)
#: A year the album title or release date may disagree by. Reissues and regional
#: releases legitimately slip by one; two is a different record.
_YEAR_TOLERANCE = 1

#: Tag names under which a file may carry the release's barcode, lower-cased.
#: ``CATALOGNUMBER`` is deliberately absent: a catalogue number is the label's
#: own reference (``JRA-2016``), and reading one as a barcode would be the same
#: category error as reading a Qobuz album id as one.
_BARCODE_TAG_KEYS = ("barcode", "upc", "ean", "upccode", "upc_code")


# ---------------------------------------------------------------------------
# Barcodes — the key everything else hangs off
# ---------------------------------------------------------------------------
def normalize_barcode(raw: Any) -> str | None:
    """Reduce a barcode to bare digits, or ``None`` when it is not one.

    Only 12-digit (UPC-A) and 13-digit (EAN-13) values are accepted. Anything
    else — a Qobuz alphanumeric id, an empty string, a catalogue number — is not
    a barcode and must not be searched with, because a partial barcode query
    returns confident nonsense.
    """
    if raw is None:
        return None
    digits = _DIGITS_RE.sub("", str(raw))
    if len(digits) == 14 and digits.startswith("0"):
        digits = digits[1:]
    return digits if len(digits) in (12, 13) else None


def barcode_check_digit_ok(raw: Any) -> bool:
    """True when *raw* is a barcode whose GS1 check digit agrees with its body.

    The one self-verifying property a barcode has: the last digit is a mod-10
    checksum over the others, so a mistyped or invented number fails it about
    nine times in ten. Use it as a **rejection** test only. Passing does not make
    something a barcode — see :func:`barcode_candidates` for the id that passes
    this and is still not one.
    """
    digits = normalize_barcode(raw)
    if digits is None:
        return False
    body, check = digits[:-1], int(digits[-1])
    total = sum(
        int(char) * (3 if index % 2 == 0 else 1)
        for index, char in enumerate(reversed(body))
    )
    return (10 - total % 10) % 10 == check


def barcode_candidates(*sources: Any) -> tuple[str, ...]:
    """Every form of this release's barcode worth searching for.

    Each argument must be a field that **is** a barcode — Qobuz's ``upc``, a
    barcode already learned from Deezer or MusicBrainz, or one read off a file
    by :func:`barcode_from_claim`. Ordered by trust; the first is what error
    messages name.

    The **Qobuz album id is not one of them**, and used to be. It looks like one
    often enough to be tempting (``0804879535645`` really is Joe Bonamassa's
    *Blues Of Desperation*) and the fallback was justified by arithmetic —
    6 matchable albums against 791 — computed when enrichment covered the entire
    4489-album catalogue. Scoping enrichment to the library reverses that sum:
    the subject is now the ~71 albums on disk, and for those the fallback was
    not supplying keys, it was supplying *wrong* keys. Measured on the live
    database: every one of the 25 "no MusicBrainz release carries barcode …"
    failures came from it, and 6 of the 13 matches that did work rested on it —
    working for Joe Bonamassa's label and failing for Mark Knopfler's, purely by
    how each numbered its releases. Concretely, Qobuz id ``0060249867260`` is not
    the barcode of *Shangri-La*; the barcode is ``602498672600``, MusicBrainz
    holds it, and the two are one digit-shift apart. Note that the Qobuz id also
    *passes* :func:`barcode_check_digit_ok` — validating harder would not have
    caught the case that motivated this, which is why the fix is to stop reading
    a field as something it is not rather than to check it more carefully.

    Both the 12- and 13-digit forms are returned when they exist: MusicBrainz
    stores barcodes as they were typed, so a release entered from a US CD sleeve
    holds the 12-digit UPC-A while the same record entered from a European one
    holds the 13-digit EAN. Comparing only one form misses half of them.

    Returns an empty tuple when there is nothing to search with, which callers
    must treat as :class:`~app.enrich.errors.NoMatchKey` rather than as a reason
    to fall back on a title search.
    """
    seen: list[str] = []
    for raw in sources:
        canonical = normalize_barcode(raw)
        if canonical is None:
            continue
        forms = (
            (canonical, canonical[1:])
            if len(canonical) == 13 and canonical.startswith("0")
            else (canonical, f"0{canonical}")
            if len(canonical) == 12
            else (canonical,)
        )
        for form in forms:
            if form not in seen:
                seen.append(form)
    return tuple(seen)


def barcode_from_claim(
    tags: Mapping[str, Any] | None, *, require_check_digit: bool = True
) -> str | None:
    """The barcode a file claims for itself, or ``None``.

    Removing the album-id fallback took a key away from releases that had no
    ``upc``, and this is where one comes back honestly: a ``BARCODE`` or ``UPC``
    tag is a field that *is* a barcode, written by whoever tagged the file — a
    label, Picard, or the shop it came from. Values arrive from mutagen as lists
    as often as strings, and ID3 spells them ``TXXX:BARCODE``, so both shapes are
    accepted.

    Unlike Qobuz's ``upc``, nothing vouches for a tag: taggers put catalogue
    numbers, ISRCs and free text in these fields. So the check digit is required
    by default — it is the cheapest way to tell a barcode from something typed
    into a barcode-shaped box, and a claim is only ever one input to
    :func:`barcode_candidates`, never on its own the answer.
    """
    if not isinstance(tags, Mapping):
        return None
    # Indexed rather than iterated so the answer does not depend on the order
    # mutagen happened to hand the tags over in: ``BARCODE`` outranks ``UPC``
    # because it is the name a barcode is deliberately written under.
    found: dict[str, Any] = {}
    for key, value in tags.items():
        name = str(key).strip().lower()
        if name.startswith("txxx:"):
            name = name[5:]
        found.setdefault(name, value)

    for name in _BARCODE_TAG_KEYS:
        value = found.get(name)
        for item in value if isinstance(value, (list, tuple)) else (value,):
            canonical = normalize_barcode(item)
            if canonical is None:
                continue
            if require_check_digit and not barcode_check_digit_ok(canonical):
                continue
            return canonical
    return None


def barcodes_match(left: Any, right: Any) -> bool:
    """True when two barcodes are the same product, across UPC-12/EAN-13 forms."""
    a, b = normalize_barcode(left), normalize_barcode(right)
    if a is None or b is None:
        return False
    return a == b or a.lstrip("0") == b.lstrip("0")


def album_claimed_barcode(values: Iterable[Any]) -> str | None:
    """The one barcode a directory's files agree on, or ``None``.

    :func:`barcode_from_claim` answers for a single file; a release is a set of
    them, and the interesting case is when they disagree. **Disagreement is a
    refusal, not a vote.** Files under one directory claiming two different
    barcodes means the folder is not one release — a rip merged with a bonus
    disc, a "greatest hits" someone assembled by hand, a stray track copied in —
    and that is precisely the situation in which reading a tag as the release's
    identity does the most damage, because whichever barcode won would then be
    written back into every file in the folder, including the ones it was never
    true of. Taking the majority would be a tie-break, and there are none here
    for the reason :func:`sole_match` gives.

    Files claiming *nothing* are ignored rather than counted as dissent: most
    libraries are full of them, and requiring unanimity among every file would
    mean one untagged interlude silenced a folder Picard had otherwise done
    correctly. The check digit is required, via :func:`barcode_from_claim`, so a
    catalogue number typed into a barcode box is discarded before it can
    disagree with anything.

    The 12- and 13-digit forms of one product are the same claim, so a folder
    where some files carry the UPC-A and others the EAN-13 agrees with itself.
    The value returned is whatever form the first claiming file used;
    :func:`barcode_candidates` expands it to both again.
    """
    agreed: str | None = None
    for raw in values:
        canonical = barcode_from_claim({"barcode": raw})
        if canonical is None:
            continue
        if agreed is None:
            agreed = canonical
        elif not barcodes_match(agreed, canonical):
            return None
    return agreed


# ---------------------------------------------------------------------------
# Titles, names and years
# ---------------------------------------------------------------------------
def titles_match(left: str | None, right: str | None) -> bool:
    """True when two release titles are the same record after normalisation.

    Uses :func:`app.core.indexer.dedupe_key`, so ``"Rumours (Deluxe Edition)"``
    and ``"Rumours"`` agree — which is right here: the two are editions of one
    release group, and telling editions apart is the job of the barcode, not of
    a string comparison.

    A title that normalises to nothing never matches anything, including another
    empty title.
    """
    key = dedupe_key(left)
    return bool(key) and key == dedupe_key(right)


def names_match(left: str | None, right: str | None) -> bool:
    """True when two artist names are the same act after normalisation.

    Uses :func:`app.core.scanner.artist_key`, the same function the bulk importer
    trusts for exactly this decision. It folds accents and punctuation but strips
    a leading article only from the front, so ``"Robert Cray"`` and ``"The Robert
    Cray Band"`` stay apart — they are two acts with two discographies.
    """
    key = artist_key(left)
    return bool(key) and key == artist_key(right)


def verify_artist_name(payload: Mapping[str, Any] | None, our_name: str | None) -> bool:
    """True when an upstream artist record can be the act we call *our_name*.

    A **rejection** test, and the only kind of name comparison the automatic path
    is allowed to make. It never selects an artist — nothing here can turn a name
    into an id — it only refuses one that something else proposed: a release's
    first credit, or an MBID a file claims in its tags.

    Three fields are consulted, all under :func:`app.core.scanner.artist_key`:
    ``name``, ``sort-name``, and every alias MusicBrainz publishes. The aliases
    are what make it usable rather than pedantic — measured against this library
    it confirms *Joanne Shaw Taylor*, *Robert Cray*, *The Robert Cray Band*, the
    City of Prague orchestra (whose name we carry with a leading article it does
    not) and *Molly Miller Trio*, while rejecting a file that claims Mark
    Knopfler's MBID for a Joanne Shaw Taylor release.

    It also refuses *Pyotr Illitch Tchaikovski*: MusicBrainz holds 92 aliases for
    Tchaikovsky and this library's French spelling is not one of them. That is
    the doctrine working, not a gap in it — the honest answer to "is this the
    same person?" from a name alone is no, and a human on the review list can
    say otherwise in one press.
    """
    if not isinstance(payload, Mapping) or not artist_key(our_name):
        return False
    return any(
        names_match(candidate, our_name) for candidate in _artist_name_forms(payload)
    )


def _artist_name_forms(payload: Mapping[str, Any]) -> Iterable[str]:
    """Every string an upstream offers as a name for one artist.

    Aliases arrive either as ``{"name": …, "sort-name": …}`` objects (the web
    service) or as bare strings; both shapes are yielded flat, because the caller
    only ever asks whether *any* of them matches.
    """
    for key in ("name", "sort-name", "sort_name", "sortName"):
        if value := payload.get(key):
            yield str(value)
    for key in ("aliases", "alias-list", "alias_list"):
        for entry in payload.get(key) or ():
            if isinstance(entry, Mapping):
                for inner in ("name", "sort-name", "sort_name"):
                    if value := entry.get(inner):
                        yield str(value)
            elif entry:
                yield str(entry)


def years_close(left: Any, right: Any, tolerance: int = _YEAR_TOLERANCE) -> bool:
    """True when two years are within *tolerance*, or either is unknown.

    Unknown is permissive on purpose. A year is corroboration, never evidence on
    its own: it narrows a candidate list that a title match already produced, and
    refusing every candidate whose date one side happens to be missing would
    throw away good matches to no benefit.
    """
    a, b = _year(left), _year(right)
    if a is None or b is None:
        return True
    return abs(a - b) <= tolerance


def _year(value: Any) -> int | None:
    """Pull a four-digit year out of an int, a date, or a partial date string."""
    if value is None:
        return None
    if isinstance(value, int):
        return value if 1000 <= value <= 9999 else None
    year = getattr(value, "year", None)
    if isinstance(year, int):
        return year
    match = re.match(r"\s*(\d{4})", str(value))
    return int(match.group(1)) if match else None


# ---------------------------------------------------------------------------
# Turning a candidate list into an answer
# ---------------------------------------------------------------------------
def sole_match(
    candidates: Iterable[Any], predicate: Any
) -> tuple[Any | None, list[Any]]:
    """Filter *candidates* by *predicate* and insist the answer is unique.

    Returns ``(winner, survivors)``. The winner is non-``None`` **only** when
    exactly one candidate survived; with two or more the caller must raise
    :class:`~app.enrich.errors.AmbiguousMatch` and put ``survivors`` in front of
    a human.

    There is no tie-break, deliberately. Every tie-break anyone reaches for here
    — highest search score, most tracks, earliest date — is a guess dressed as a
    rule, and it is wrong often enough to poison a library quietly.
    """
    survivors = [candidate for candidate in candidates if predicate(candidate)]
    return (survivors[0] if len(survivors) == 1 else None), survivors


def qobuz_album_by_barcode(
    items: Iterable[Mapping[str, Any]], barcodes: Iterable[Any]
) -> tuple[Mapping[str, Any] | None, list[Mapping[str, Any]]]:
    """Pick the Qobuz album whose ``upc`` is one of *barcodes*. Exact or nothing.

    This is the last link of the identification chain that starts at the audio:
    AcoustID says which recordings these files hold, coverage.solve_album() says
    which release — or which release *group* — explains the directory,
    MusicBrainz supplies that record's barcodes and its canonical name, and this
    turns the name into Qobuz candidates and then throws away every one whose
    barcode disagrees.

    The division of labour is the whole point, and it is the rule
    :func:`verify_artist_name` already states from the other side: **the name
    only narrows the search, the barcode selects.** A free-text
    ``catalog/search`` is the only way into the Qobuz catalogue — there is no
    barcode endpoint — so a name has to be sent. What comes back is a list of
    confident-looking neighbours, exactly like MusicBrainz's Lucene index, and
    taking the first of them would be the "guess dressed as a rule" that
    ``coverage.solve_album`` refuses to make. So the name is never allowed to
    decide anything: it is a query, and the returned ``upc`` is the key.

    *barcodes* is deliberately a **set** rather than one value, because the
    release Qobuz sells is usually not the pressing MusicBrainz matched. Measured
    on this library, Joanne Shaw Taylor's *White Sugar* is ``710347114727`` in
    ``album_metadata`` and ``0884385226442`` on Qobuz — two real barcodes for two
    real editions of one record. Passing every barcode in the release *group* is
    what makes those meet, and it costs no precision: a release group has one
    artist credit, so an edition is still an exact answer about who made it.

    Returns ``(winner, survivors)`` from :func:`sole_match`, so two matching
    editions are ``ambiguous`` and not "pick the first".
    """
    wanted = {digits for digits in (normalize_barcode(one) for one in barcodes) if digits}
    if not wanted:
        return None, []
    return sole_match(
        items, lambda item: normalize_barcode(item.get("upc")) in wanted
    )


def qobuz_artist_credit(item: Mapping[str, Any] | None) -> tuple[str | None, str | None]:
    """The ``(id, name)`` of the artist Qobuz credits on an album payload.

    ``catalog/search`` returns the credit inline, which is what keeps the whole
    chain at one request per folder: the album that matched on its barcode
    already names the artist to follow, so nothing has to search for a *person*
    by name — the operation this codebase refuses to do automatically.

    The id is returned as a **string**. Qobuz sends artist ids as JSON numbers
    and every id column here is ``String``; coercing at the boundary is what
    stops a later query silently matching no rows.
    """
    if not item:
        return None, None
    artist = item.get("artist")
    if not isinstance(artist, Mapping):
        return None, None
    raw_id = artist.get("id")
    name = _text_or_none(artist.get("name"))
    return (str(raw_id) if raw_id not in (None, "") else None), name


def _text_or_none(raw: Any) -> str | None:
    """A trimmed string, or ``None`` for anything blank."""
    if raw is None:
        return None
    text = str(raw).strip()
    return text or None


# ---------------------------------------------------------------------------
# Identifiers
# ---------------------------------------------------------------------------
def normalize_mbid(raw: Any) -> str | None:
    """Validate a MusicBrainz id, or ``None``.

    Validated rather than trusted because this is also the entry point for a
    hand-pasted id from the review page, and a malformed one stored now is a
    confusing bug much later.
    """
    if raw is None:
        return None
    text = str(raw).strip().lower()
    return text if _UUID_RE.match(text) else None


def normalize_isni(raw: Any) -> str | None:
    """Normalise an ISNI to its bare 16-character form, or ``None``.

    ISNIs are written both as ``0000 0001 2103 2683`` and as
    ``0000000121032683``; the last character may be ``X``. This is the artist's
    identifier across every catalogue that bothers with one, which is why it is
    worth storing separately from any single source's id.
    """
    if raw is None:
        return None
    text = re.sub(r"[\s\-]+", "", str(raw)).upper()
    if len(text) != 16 or not text[:15].isdigit():
        return None
    return text if text[15].isdigit() or text[15] == "X" else None


def main_credit(
    credits: Sequence[Mapping[str, Any]],
    *,
    expected_name: str | None,
    id_key: str = "id",
    role_key: str | None = None,
    main_role: str = "Main",
) -> str | None:
    """The artist a release is *primarily* by, or ``None``.

    This is how an artist identifier is derived without ever running a name
    search, and it is far safer than searching because the release is already
    pinned: the only question is whether the act the release names first is the
    act being enriched.

    Three gates:

    1. there is a first credit, and it holds a role that is a main one;
    2. it is not Various Artists — rejected by id, because that placeholder is
       credited on every compilation and deriving from it would quietly point
       half a library at one wrong entity;
    3. :func:`verify_artist_name` accepts its name against *expected_name*.

    Gate 3 is the whole rule and it is a **rejection**, not a selection: a name
    can throw a candidate away, it can never choose one. A guest spot on
    somebody else's record has a different first credit and fails there, which
    is what stops one appearance from rewriting an artist's identity.

    The difference from :func:`sole_main_credit` is gate 1, and it was measured.
    That function additionally demands the release name exactly one act, and
    against real AcoustID answers for this library it threw away *Robert Cray,
    Hi Rhythm* (2 credits), *The City of Prague Philharmonic Orchestra, Jen
    Brown* (2) and *Molly Miller Trio, Tamir Barzilay, Andre De Santanna* (3) —
    three correct matches, which is why the audio contributed nothing to artist
    identification at all. A collaboration is still one record by somebody, and
    the credit order says who; requiring a solo credit is not extra exactness,
    it is a different and wrong question.
    """
    if not credits:
        return None
    credit = credits[0]

    if role_key and str(credit.get(role_key) or main_role) != main_role:
        return None

    nested = credit.get("artist") if isinstance(credit.get("artist"), Mapping) else credit
    identifier = str(nested.get(id_key) or "").strip()
    if not identifier or identifier == VARIOUS_ARTISTS_MBID:
        return None
    return identifier if verify_artist_name(nested, expected_name) else None


def sole_main_credit(
    credits: Sequence[Mapping[str, Any]],
    *,
    expected_name: str | None,
    id_key: str = "id",
    join_key: str | None = "joinphrase",
    role_key: str | None = None,
    main_role: str = "Main",
) -> str | None:
    """:func:`main_credit`, plus a demand that the release name exactly one act.

    Two further gates on top of that function's three: there is a single credit,
    and it carries no join phrase (``" & "``, ``" feat. "``, ``" with "``). Both
    are about the *shape* of the credit rather than about who it names, so this
    refuses a collaboration even when the artist being enriched is the one
    leading it.

    That is stricter than the doctrine requires and stricter than is useful —
    see :func:`main_credit` for the three real releases it discarded — so it is
    not what the providers derive an artist from. It survives for a caller that
    genuinely needs "this release is by one person and nobody else" and can
    afford to answer no whenever it is not.
    """
    if len(credits) != 1:
        return None
    if join_key and str(credits[0].get(join_key) or "").strip():
        return None
    return main_credit(
        credits,
        expected_name=expected_name,
        id_key=id_key,
        role_key=role_key,
        main_role=main_role,
    )
