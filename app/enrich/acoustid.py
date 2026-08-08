"""The AcoustID rung — asking the audio itself what a release is.

Every other source here answers questions about *metadata*: a barcode, a title,
a name. This one answers a question about the recording, which is the only kind
of evidence that cannot be wrong because somebody typed it wrong. That is why it
is **first** on the ladder rather than last: the library being identified is
exactly the one whose tags nobody vouches for, and the waveform is the only thing
in it that cannot be mistagged. The rungs below corroborate what the audio said;
they no longer have to propose the answer.

Four jobs, in the order they matter:

**Identify the release.** Each file is fingerprinted and looked up, which yields
a handful of *recordings* it could hold, each published on a handful of releases.
That is a hopeless answer per file and a decisive one per folder, so the verdict
is reached by :mod:`app.enrich.coverage` over the whole directory: a release is
admissible only if every file it explains sits at a distinct position in it, it
explains essentially all of them, and its track count equals the file count.
Exactly one admissible release identifies; none is unidentified; several are
editions of one record and go to a human. Measured on a fifteen-file Mark
Knopfler folder, *Down the Road Wherever* explains 15 of 15 and the promo *On the
Road to Milano* explains 1 — a gap no score has to be tuned to see.

**Nothing here ever takes "the first recording".** One AcoustID cluster maps to
two genuinely different songs often enough to be ordinary: measured on the same
library, one Knopfler file came back as *So Far Away* **and** *Rear View Mirror*
together. Per-file ambiguity is the normal input, not a fault, and it is precisely
why the solve is stated over the album. A file's recording is written down only
once the release has been identified and that release seats the file at a single
recording — identity propagates down from the release, never up from a track.

**Confirm or deny a metadata match.** Kept, and now reached only when the audio
identified nothing of its own: if a majority of the tracks MusicBrainz mapped
resolve to recordings that are nowhere among this file's candidates, the barcode
match was wrong and the album is stashed for a human rather than left quietly
wrong. One track differing is ordinary — a bonus track, a different mix.

**Detect corruption.** Chromaprint has to decode the audio to hash it, so a file
it cannot decode is a file no player can play. That verdict needs no API key at
all and is recorded even when AcoustID is unavailable.

This is the one source that needs registering — a free application key from
acoustid.org. Without it the rung is *gated*: the lookups are skipped, but the
corruption check still runs, because it is local.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from app.config import Settings, get_settings
from app.enrich.chromaprint import (
    FileUnavailable,
    Fingerprint,
    FingerprintTimeout,
    FpcalcMissing,
    UnreadableAudio,
    find_fpcalc,
    fingerprint_file,
)
from app.enrich.coverage import (
    CoverageResult,
    FileCandidates,
    RecordingCandidate,
    ReleaseCoverage,
    ReleaseFacts,
    ReleaseSlot,
    Verdict,
    solve_album,
)
from app.enrich.errors import (
    AmbiguousMatch,
    MatchRejected,
    NoMatchKey,
    NotMatched,
    SourceGated,
)
from app.enrich.matching import main_credit, normalize_mbid
from app.enrich.types import AlbumSnapshot, EnrichmentJob, EnrichmentResult, TrackSnapshot
from app.logging_conf import get_logger
from app.models import EnrichmentEntity, EnrichmentSource, FingerprintState
from app.net.errors import HttpError, HttpNotFound, HttpRateLimitError
from app.net.http import JsonHttpClient
from app.net.ratelimit import RateLimiter

__all__ = ["AcoustIdClient", "AcoustIdProvider"]

logger = get_logger(__name__)

#: Below this the AcoustID index is guessing, and a guess is worth less than
#: nothing here — it would put a release into the solve that no file is really
#: on, and deny a good metadata match.
_MIN_SCORE = 0.7

#: Fraction of the comparable tracks that must agree with the recordings an
#: earlier rung mapped before its match survives.
_CONFIRM_RATIO = 0.5

#: How many tracks must be comparable at all before a disagreement between them
#: is allowed to deny the whole release. A track is comparable only when
#: MusicBrainz mapped a recording onto it *and* AcoustID recognised the audio,
#: and on an obscure release that can easily be one widely-fingerprinted single.
#: One track out of twelve differing is the ordinary case the module documents,
#: not "most of an album" — so below this the honest answer is silence.
_MIN_COMPARABLE = 3

#: ``album_metadata.mb_match_method`` when the coverage solve named a release,
#: and when it could only name the record several editions share. Both are read
#: by a person on the album page, so they say how the id was arrived at rather
#: than which rung wrote it.
_RELEASE_METHOD = "audio-coverage"
_GROUP_METHOD = "audio-rg"

#: ``track_metadata.match_method`` for a recording the identified release seats.
_TRACK_METHOD = "audio-release"

#: ``artist_metadata.mb_match_method`` for an artist taken off the identified
#: release's first credit. Same derivation MusicBrainz records as
#: ``release-credit``, from a release the audio pinned rather than a barcode.
_ARTIST_METHOD = "audio-credit"


class AcoustIdClient(JsonHttpClient):
    """Lookups against the AcoustID web service."""

    source = "acoustid"
    base_url = "https://api.acoustid.org/v2/"
    timeout = 30.0

    def __init__(self, *, api_key: str = "", **kwargs: Any) -> None:
        super().__init__(**kwargs)
        self._api_key = api_key

    def check_payload(self, path: str, body: Any, *, status: int) -> Any:
        """AcoustID reports failure in the body, with ``status: error``."""
        if not isinstance(body, Mapping):
            return body
        if str(body.get("status", "ok")).lower() != "error":
            return body
        error = body.get("error")
        message = (
            str(error.get("message"))
            if isinstance(error, Mapping)
            else str(error or "AcoustID returned an error")
        )
        lowered = message.lower()
        if "rate limit" in lowered or "too many" in lowered:
            raise HttpRateLimitError(message, source=self.source, endpoint=path)
        if "invalid" in lowered and "fingerprint" in lowered:
            raise HttpNotFound(message, source=self.source, endpoint=path)
        raise HttpError(message, source=self.source, endpoint=path)

    async def lookup(self, fingerprint: Fingerprint) -> list[dict[str, Any]]:
        """Recordings matching a fingerprint, and the releases they appear on.

        Returns the raw ``results``. Scoring and filtering are the provider's
        job; anything below :data:`_MIN_SCORE` is a guess.
        """
        payload = await self.get_json(
            "lookup",
            {
                "client": self._api_key,
                # ``releases``, not ``releaseids``: the solve in
                # app/enrich/coverage.py needs each release's own track count to
                # rule a box set out, and its media to seat two files at two
                # positions. A bare list of ids can supply neither, so every
                # candidate would fail the count requirement and the rung would
                # identify nothing at all.
                #
                # Space-separated, not "recordings+releases". AcoustID's own
                # documentation writes the separator as a literal ``+`` because
                # that is what a form-encoded space *is*; sending the character
                # gets it percent-encoded to %2B, and the API then reads one
                # unknown token and silently returns results with no recordings
                # at all — a rung that answers 200 and identifies nothing.
                "meta": "recordings releases",
                "duration": fingerprint.duration,
                "fingerprint": fingerprint.fingerprint,
            },
        )
        results = payload.get("results") if isinstance(payload, Mapping) else None
        return [item for item in (results or []) if isinstance(item, Mapping)]


class AcoustIdProvider:
    """Fingerprints what is on disk, then asks AcoustID what it is."""

    source = EnrichmentSource.ACOUSTID

    def __init__(
        self,
        *,
        limiter: RateLimiter,
        settings: Settings | None = None,
        client: AcoustIdClient | None = None,
        fingerprinter: Any = None,
    ) -> None:
        self._settings = settings or get_settings()
        self._fpcalc = find_fpcalc(self._settings.fpcalc_path or None)
        # Injected in tests so the suite never spawns a process.
        self._fingerprint = fingerprinter or fingerprint_file
        self.client = client or AcoustIdClient(
            api_key=self._settings.acoustid_api_key,
            limiter=limiter,
            settings=self._settings,
        )

    def supports(self, entity_type: EnrichmentEntity) -> bool:
        """Albums only — a fingerprint is of a file, and files hang off releases."""
        return entity_type is EnrichmentEntity.ALBUM

    def is_ready(self) -> bool:
        """Needs the binary. The API key is checked separately, per job.

        Without a key the lookups are skipped but the **corruption check still
        runs**, because that part is entirely local and is the more valuable half
        for anyone who never registers one.
        """
        return bool(self._fpcalc)

    def gate_reason(self) -> str:
        """What is missing, specifically — this rung needs two separate things.

        The generic "acoustid is not configured" was wrong in the case that
        actually happens: an API key set, ``fpcalc`` absent, and someone sent to
        re-check the key. Only the binary gates the rung; the key gates half of
        what the rung then does, and that half is named here too so the state of
        both is legible from one line on the enrichment page.
        """
        missing = "Chromaprint's fpcalc binary is not installed (set FPCALC_PATH "
        missing += "or put it on PATH); fingerprinting and corruption detection "
        missing += "are unavailable"
        if not self._settings.acoustid_ready:
            missing += ". ACOUSTID_API_KEY is unset too, so lookups would be "
            missing += "skipped even with the binary"
        return missing

    async def aclose(self) -> None:
        await self.client.aclose()

    async def fetch(self, job: EnrichmentJob) -> EnrichmentResult:
        if not self._fpcalc:
            raise SourceGated(
                "fpcalc is not installed; fingerprinting is unavailable",
                source=self.source.value,
            )
        album = job.album
        playable = [track for track in album.tracks if track.path]
        if not playable:
            raise NoMatchKey(
                "nothing on disk to fingerprint", source=self.source.value
            )

        prints, damaged = await self._fingerprint_all(playable)
        result = EnrichmentResult(match_key=f"{len(playable)}-files")

        for track_id in damaged:
            result.track_fields[track_id] = {
                "fingerprint_state": FingerprintState.CORRUPT.value,
                "fingerprinted_at": None,
            }
        if damaged:
            names = ", ".join(sorted(damaged.values())[:3])
            _add_note(
                result, f"{len(damaged)} unplayable file(s) in '{album.title}': {names}"
            )

        if not prints:
            if not damaged:
                # Measured nothing rather than found nothing: every file timed
                # out. Saying "corrupt" here would quarantine a healthy album.
                raise NotMatched(
                    f"nothing in '{album.title}' could be fingerprinted this time",
                    source=self.source.value,
                )
            # Everything was unreadable. The corrupt verdicts travel with the
            # outcome so they are still written — an album where every file is
            # broken is precisely the one that needs quarantining, and it was
            # the only case where nothing was recorded.
            raise MatchRejected(
                f"every file in '{album.title}' failed to decode",
                source=self.source.value,
                partial=result,
            )

        if not self._settings.acoustid_ready:
            # Local verdicts only. Still worth persisting — this is the integrity
            # half of the job, and it needs no account anywhere.
            for track_id, fingerprint in prints.items():
                result.track_fields[track_id] = {
                    "fingerprint_state": FingerprintState.OK.value,
                }
            return result

        return await self._identify(album, playable, prints, result)

    # ----------------------------------------------------------- fingerprints
    async def _fingerprint_all(
        self, tracks: Sequence[TrackSnapshot]
    ) -> tuple[dict[str, Fingerprint], dict[str, str]]:
        """Hash every file, separating the readable from the broken.

        Three outcomes, not two: readable, *broken*, and **unmeasured**. Only a
        decode that ran and failed goes in ``damaged``, because that is the
        dictionary the quarantine ultimately acts on; a decode that timed out and
        a file that was not there are both left out of both results, to be tried
        again when the machine is quieter or the share is back.

        Each call goes through ``asyncio.to_thread``: ``fpcalc`` spawns a process
        and decodes two minutes of audio, and doing that on the event loop would
        stall every poller in the UI.
        """
        prints: dict[str, Fingerprint] = {}
        damaged: dict[str, str] = {}
        for track in tracks:
            try:
                prints[track.id] = await asyncio.to_thread(
                    self._fingerprint, Path(str(track.path)), fpcalc=self._fpcalc
                )
            except UnreadableAudio as exc:
                # A file Chromaprint cannot decode is a file no player can play.
                logger.warning("Unplayable file: %s", exc)
                damaged[track.id] = Path(str(track.path)).name
            except (FingerprintTimeout, FileUnavailable) as exc:
                # No verdict in either direction. Neither the decode that did not
                # finish nor the file that was not there measured anything: the
                # first is a fact about the machine, the second about a path that
                # a rename, a stale row or a share that blipped can invalidate
                # without the audio changing at all. The caller's answer to "this
                # file is corrupt" is to move it to the trash, so leave both
                # unmeasured and try again another day.
                logger.warning("Fingerprint skipped: %s", exc)
            except FpcalcMissing:
                raise
            except Exception:  # noqa: BLE001 - one bad file must not stop the album
                logger.exception("Fingerprinting %s failed", track.path)
        return prints, damaged

    # -------------------------------------------------------------- identity
    async def _identify(
        self,
        album: AlbumSnapshot,
        playable: Sequence[TrackSnapshot],
        prints: Mapping[str, Fingerprint],
        result: EnrichmentResult,
    ) -> EnrichmentResult:
        """Ask the index what each file is, then ask the folder what it is.

        One request per file, and every plausible answer to each is kept. Taking
        only the top hit per file is what makes fingerprinting look useless on
        the records it is most needed for: the candidate *sets* are the input to
        the solve, and a set of one is a decision already made.

        The solve is handed **every** file in the directory, not only the ones
        that decoded. A release's track count is checked against the file count,
        so dropping an unreadable file would silently make a fifteen-track album
        look like a fourteen-file folder that no release on earth accounts for —
        and the album with a broken file in it is exactly the one whose identity
        is worth establishing, because that is what the quarantine and the refill
        hang off. A file that did not decode is a hole in the evidence, the same
        as one the index has never heard of, and :data:`coverage.MIN_COVERAGE`
        exists to absorb a few of either.
        """
        candidates: dict[str, tuple[RecordingCandidate, ...]] = {}
        payloads: dict[str, Mapping[str, Any]] = {}
        for track_id, fingerprint in prints.items():
            try:
                results = _plausible(await self.client.lookup(fingerprint))
            except HttpNotFound:
                results = []
            found, releases = _recording_candidates(results)
            candidates[track_id] = found
            for release_id, payload in releases.items():
                payloads.setdefault(release_id, payload)

            fields: dict[str, Any] = {"fingerprint_state": FingerprintState.OK.value}
            if results and (cluster := _text(results[0].get("id"))):
                # The fingerprint's own bucket, not a choice between recordings:
                # it says which audio this is, and it is only ever *reported*, by
                # :func:`duplicate_groups`. The recording — the claim that leaves
                # the program in a file tag — is written further down, and only
                # once a release has been identified.
                fields["acoustid"] = cluster
            result.track_fields[track_id] = {
                **result.track_fields.get(track_id, {}),
                **fields,
            }

        # A file the index knows with zero linked recordings is silence, not a
        # failure: measured on this library, 1 file in 10 came back at score
        # 0.948 with nothing attached. It contributes no candidates, the solve
        # counts it unexplained, and MIN_COVERAGE is what absorbs it.
        solved = solve_album(
            [
                FileCandidates(entry.id, candidates.get(entry.id, ()))
                for entry in playable
            ],
            [
                _release_facts(release_id, payload)
                for release_id, payload in payloads.items()
            ],
        )

        if solved.verdict is Verdict.IDENTIFIED:
            return self._identified(album, solved, payloads, candidates, result)
        if solved.verdict is Verdict.AMBIGUOUS:
            raise AmbiguousMatch(
                _ambiguous_message(album, solved, payloads),
                source=self.source.value,
                candidates=[
                    _candidate_summary(item, payloads.get(item.release_id))
                    for item in solved.candidates
                ],
                partial=self._group_only(solved, result),
            )

        # Nothing of its own to say, so the audio falls back to contradicting —
        # which is the older job, and the only one left when no release covers
        # the folder.
        self._confirm(album, candidates, partial=result)
        raise NotMatched(
            f"AcoustID could not identify '{album.title}': {solved.reason}",
            source=self.source.value,
            partial=result,
        )

    def _identified(
        self,
        album: AlbumSnapshot,
        solved: CoverageResult,
        payloads: Mapping[str, Mapping[str, Any]],
        candidates: Mapping[str, tuple[RecordingCandidate, ...]],
        result: EnrichmentResult,
    ) -> EnrichmentResult:
        """Record the release the folder is, and what follows from it.

        Downwards to the tracks and upwards to the artist, both from the release
        and neither from a file: a recording is written only where the identified
        release leaves this file one recording to be, and the artist comes off the
        release's own first credit. A track's own credit is the *performer*, which
        on a compilation is a different person, so it is never read as the album
        artist — the same "never use a value as something it is not" rule that
        keeps the Qobuz album id out of the barcode field.
        """
        release_id = str(solved.release_id)
        payload = payloads.get(release_id) or {}
        result.album_fields = _album_fields(release_id, payload)

        for track_id, found in candidates.items():
            recordings = {
                recording.recording_id
                for recording in found
                if any(slot.release_id == release_id for slot in recording.slots)
            }
            if len(recordings) != 1:
                continue
            result.track_fields.setdefault(track_id, {}).update(
                {
                    "mb_recording_mbid": next(iter(recordings)),
                    "match_method": _TRACK_METHOD,
                }
            )

        # An identification always carries its working; the property is typed
        # optional because the other two verdicts have none to carry.
        evidence = solved.evidence
        assert evidence is not None
        title = _text(payload.get("title")) or release_id
        note = (
            f"AcoustID identified '{album.title}' as '{title}' from "
            f"{len(evidence.explained)} of {evidence.file_count} file(s)"
        )
        if self._derive_artist(album, payload, result):
            note += f", and {album.artist_name} from its first credit"
        _add_note(result, note)
        return result

    def _derive_artist(
        self,
        album: AlbumSnapshot,
        payload: Mapping[str, Any],
        result: EnrichmentResult,
    ) -> bool:
        """Take the artist off the identified release's first credit, or not.

        The *first* credit, not the only one: MusicBrainz orders artist-credit as
        the release is billed, so a collaboration still says who it is primarily
        by. :func:`app.enrich.matching.main_credit` then uses the name we already
        hold as a **rejection** test — it can throw the credit away, it can never
        choose one — which is what stops a guest appearance from rewriting the
        artist being enriched. Nothing here searches for an artist by name, and
        nothing may be added that does.
        """
        credits = [
            entry for entry in (payload.get("artists") or ()) if isinstance(entry, Mapping)
        ]
        derived = normalize_mbid(
            main_credit(credits, expected_name=album.artist_name)
        )
        if not derived or derived == album.mb_artist_mbid:
            return False

        result.artist_fields = {
            "mb_artist_mbid": derived,
            "mb_match_method": _ARTIST_METHOD,
            "mb_match_evidence": 1,
        }
        # An artist id is the key MusicBrainz browses a discography with, so the
        # rung that was waiting on one is worth running again now — the same
        # follow-up that module records when *it* derives an artist.
        result.reopen.append(
            (EnrichmentEntity.ARTIST, album.artist_id, EnrichmentSource.MUSICBRAINZ)
        )
        return True

    def _group_only(
        self, solved: CoverageResult, result: EnrichmentResult
    ) -> EnrichmentResult:
        """What survives an ambiguous verdict: the record, not the pressing.

        Several admissible releases are normally editions of one release group
        with identical tracklists, and nothing *about* the audio differs between
        them — so which pressing this is genuinely cannot be answered here, while
        which record it is can. That is the same fallback
        :meth:`app.enrich.musicbrainz.MusicBrainzProvider._release_group_only`
        makes, and it is carried on ``partial`` so it is written even though the
        rung is about to say "no": ambiguity is a question for a human, and
        putting the question to them with the record already named is strictly
        more than putting it to them blank.
        """
        group = solved.release_group_id
        if group:
            result.album_fields = {
                "mb_release_group_mbid": group,
                "mb_match_method": _GROUP_METHOD,
            }
        return result

    def _confirm(
        self,
        album: AlbumSnapshot,
        candidates: Mapping[str, tuple[RecordingCandidate, ...]],
        *,
        partial: EnrichmentResult | None = None,
    ) -> None:
        """Deny a metadata match when the audio says it is a different record.

        Only meaningful once MusicBrainz has mapped recordings onto these tracks:
        with nothing to compare against, silence is the honest answer, not a
        rubber stamp.

        A track agrees when the mapped recording is *anywhere* among what the
        index offered for that file, rather than being the file's top hit. That
        is deliberately the weaker test: one cluster maps to two different songs
        often enough to be ordinary, so demanding the mapped recording come first
        would deny good matches on a coin toss — and a denial here stashes a whole
        album for a human.

        A majority must disagree before anything is denied, and there must be
        enough comparable tracks for "majority" to mean anything at all — see
        :data:`_MIN_COMPARABLE`. One track differing is ordinary — a bonus track,
        a different mix of a single — but if most of an album resolves to
        recordings other than the ones the barcode match claimed, the barcode
        match was wrong.
        """
        offered = {
            track_id: {recording.recording_id for recording in found}
            for track_id, found in candidates.items()
            if found
        }
        comparable = {
            track.id: mapped
            for track in album.tracks
            if (mapped := normalize_mbid(track.mb_recording_mbid))
            and track.id in offered
        }
        if len(comparable) < _MIN_COMPARABLE:
            return

        agreed = sum(
            1 for track_id, mapped in comparable.items() if mapped in offered[track_id]
        )
        if agreed / len(comparable) >= _CONFIRM_RATIO:
            return
        raise MatchRejected(
            f"the audio in '{album.title}' does not match the release it was "
            f"matched to ({agreed} of {len(comparable)} tracks agreed)",
            source=self.source.value,
            partial=partial,
        )


# ---------------------------------------------------------------------------
# Payload helpers
# ---------------------------------------------------------------------------
def _plausible(results: Sequence[Mapping[str, Any]]) -> list[Mapping[str, Any]]:
    """Every result the index is confident enough about, best score first.

    All of them, not the best of them. The score's only job is to keep guesses
    out of the solve; ranking what survives would smuggle a tie-break into a
    module whose whole point is not to have one.
    """
    scored = [
        (score, item)
        for item in results
        if (score := _float(item.get("score"))) is not None and score >= _MIN_SCORE
    ]
    return [item for _, item in sorted(scored, key=lambda pair: -pair[0])]


def _recording_candidates(
    results: Sequence[Mapping[str, Any]],
) -> tuple[tuple[RecordingCandidate, ...], dict[str, Mapping[str, Any]]]:
    """Everything one file could be, plus the release payloads that said so.

    The releases are handed back alongside because they are needed twice — once
    as :class:`~app.enrich.coverage.ReleaseFacts` for the solve, and again for
    the title, credit and counts of whichever one wins — and asking for them a
    second time would be a second request per file.
    """
    recordings: list[RecordingCandidate] = []
    releases: dict[str, Mapping[str, Any]] = {}
    seen: set[str] = set()
    for result in results:
        for entry in result.get("recordings") or ():
            if not isinstance(entry, Mapping):
                continue
            recording_id = normalize_mbid(entry.get("id"))
            if not recording_id or recording_id in seen:
                continue
            seen.add(recording_id)
            slots: list[ReleaseSlot] = []
            for release in entry.get("releases") or ():
                if not isinstance(release, Mapping):
                    continue
                release_id = normalize_mbid(release.get("id"))
                if not release_id:
                    continue
                releases.setdefault(release_id, release)
                slots.extend(_slots(release_id, release))
            recordings.append(RecordingCandidate(recording_id, tuple(slots)))
    return tuple(recordings), releases


def _slots(release_id: str, release: Mapping[str, Any]) -> list[ReleaseSlot]:
    """Where this recording sits on that release, as far as AcoustID says.

    The ``mediums`` a release carries here are cut down to the *matching*
    recording's places on it, so every track entry under them is this recording's
    seat. When AcoustID states no position at all the slot carries none, and
    :func:`app.enrich.coverage._slot_keys` falls back to the recording's own
    identity — a weaker injectivity test that still catches the same file twice.
    """
    seats: list[ReleaseSlot] = []
    for medium in release.get("mediums") or ():
        if not isinstance(medium, Mapping):
            continue
        disc = _int(medium.get("position"))
        for entry in medium.get("tracks") or ():
            if isinstance(entry, Mapping) and (position := _int(entry.get("position"))):
                seats.append(ReleaseSlot(release_id, disc, position))
    return seats or [ReleaseSlot(release_id)]


def _release_facts(release_id: str, release: Mapping[str, Any]) -> ReleaseFacts:
    """One candidate release as the solve needs it.

    ``track_count`` is the requirement that keeps a 60-track anthology out, and
    it is the one field a release may honestly not have — "unknown means no", so
    an uncounted release can lose a match but can never manufacture one.
    """
    return ReleaseFacts(
        release_id,
        track_count=_int(release.get("track_count")) or _media_track_count(release),
        title=_text(release.get("title")) or None,
        release_group_id=_release_group_id(release),
    )


def _media_track_count(release: Mapping[str, Any]) -> int | None:
    """The release's track total summed off its media, when it gave no total."""
    total = sum(
        _int(medium.get("track_count")) or 0
        for medium in (release.get("mediums") or ())
        if isinstance(medium, Mapping)
    )
    return total or None


def _release_group_id(release: Mapping[str, Any]) -> str | None:
    """The record this pressing belongs to, if AcoustID happened to name it.

    Usually it does not — ``meta=releases`` carries no group — and that costs
    only the ambiguous verdict's fallback, never a match. A release belongs to
    exactly one group, so a list holding more than one is not something to choose
    from: it is a payload that cannot be read, and it is dropped.
    """
    nested = release.get("releasegroup")
    if isinstance(nested, Mapping):
        return normalize_mbid(nested.get("id"))
    groups = [
        item for item in (release.get("releasegroups") or ()) if isinstance(item, Mapping)
    ]
    return normalize_mbid(groups[0].get("id")) if len(groups) == 1 else None


def _album_fields(release_id: str, release: Mapping[str, Any]) -> dict[str, Any]:
    """The identified release, as ``album_metadata`` columns.

    Only what is actually known is included. A key present with ``None`` means
    "known empty" and *clears* the stored value, so a re-run whose payload
    happened to omit a title would wipe the one MusicBrainz supplied — the same
    sparse-answer trap ``Indexer._apply_metadata`` documents. The rest of the
    release facts are left to MusicBrainz, which this identification is what
    unblocks.
    """
    facts = _release_facts(release_id, release)
    fields: dict[str, Any] = {
        "mb_release_mbid": release_id,
        "mb_match_method": _RELEASE_METHOD,
    }
    if facts.release_group_id:
        fields["mb_release_group_mbid"] = facts.release_group_id
    if facts.title:
        fields["title"] = facts.title
    if facts.track_count:
        fields["track_count"] = facts.track_count
    if media := len([m for m in (release.get("mediums") or ()) if isinstance(m, Mapping)]):
        fields["media_count"] = media
    return fields


def _candidate_summary(
    coverage: ReleaseCoverage, release: Mapping[str, Any] | None
) -> dict[str, Any]:
    """The shape the review list shows for something a human must decide."""
    release_id = coverage.release_id
    facts = _release_facts(release_id, release or {})
    return {
        "id": release_id,
        "title": facts.title or "",
        "track_count": facts.track_count,
        "release_group_id": facts.release_group_id,
        "explains": f"{len(coverage.explained)} of {coverage.file_count}",
        "url": f"https://musicbrainz.org/release/{release_id}",
    }


def _ambiguous_message(
    album: AlbumSnapshot,
    solved: CoverageResult,
    payloads: Mapping[str, Mapping[str, Any]],
) -> str:
    """Name the releases in contention, because a person has to choose between them."""
    names = [
        _text((payloads.get(item.release_id) or {}).get("title")) or item.release_id
        for item in solved.candidates
    ]
    listed = ", ".join(f"'{name}'" for name in sorted(names)[:4])
    return (
        f"{len(solved.candidates)} releases account for the files in "
        f"'{album.title}' equally well ({listed}); the audio cannot tell them apart"
    )


def _add_note(result: EnrichmentResult, text: str) -> None:
    """Append to the activity note rather than replacing it.

    An album can be both partly unplayable and identified, and the corruption
    half is the one nobody else reports.
    """
    result.note = "; ".join(part for part in (result.note, text) if part)


def _text(value: Any) -> str:
    return str(value).strip() if value not in (None, "") else ""


def _float(value: Any) -> float | None:
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def duplicate_groups(
    rows: Iterable[tuple[str, str | None]]
) -> dict[str, list[str]]:
    """Group ``(track_id, acoustid)`` pairs by fingerprint.

    Only groups of two or more are returned, and each is ordered so the first
    entry is the one the others point at. Duplicate *recordings* are not a
    problem in themselves — a single genuinely appears on its parent album — so
    this reports rather than acts.
    """
    by_print: dict[str, list[str]] = {}
    for track_id, acoustid in rows:
        if acoustid:
            by_print.setdefault(acoustid, []).append(track_id)
    return {
        acoustid: sorted(ids) for acoustid, ids in by_print.items() if len(ids) > 1
    }
