"""Audio fingerprinting: identity, corroboration and corruption.

``fpcalc`` is never really spawned — the subprocess runner and the fingerprinter
are both injected — and AcoustID is served by ``httpx.MockTransport``. What is
real is the filesystem: the quarantine tests write actual files into a scratch
library and check they end up in the trash rather than deleted.

The distinctions being pinned down:

* **the audio identifies the release, and it does it per folder.** One release
  explaining fifteen of fifteen files beats one explaining one of them by
  failing a requirement, not by scoring higher.
* **nothing ever takes the first recording.** One fingerprint cluster maps to two
  different songs often enough to be ordinary — measured, *So Far Away* and
  *Rear View Mirror* for one Knopfler file — so a track's recording is written
  only once the identified release leaves it one recording to be.
* **a file the index knows with nothing attached is silence, not failure.** Also
  measured: 1 file in 10 came back at score 0.948 with zero linked recordings.
* **a missing tool says nothing about the library.** ``FpcalcMissing`` gates the
  rung; only ``UnreadableAudio`` is evidence that a file is broken. Neither a
  decode that timed out nor a file that was not there measured anything, and
  both used to be recorded as corruption.
* **the audio may contradict the metadata, but only by a majority.** One track
  differing is ordinary; most of an album differing means the barcode match was
  wrong, and the album is stashed rather than left quietly wrong.
* **corruption is quarantined, never deleted.** "This file is broken" is exactly
  the kind of verdict that has to be recoverable.
"""

from __future__ import annotations

import asyncio
import json
import subprocess
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, AsyncIterator, Callable, Iterator

import httpx
import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.pool import StaticPool

from app.config import Settings
from app.core.librarian import PAYLOAD_DIRNAME, quarantine_corrupt_files
from app.enrich.acoustid import AcoustIdClient, AcoustIdProvider, duplicate_groups
from app.enrich.chromaprint import (
    FileUnavailable,
    Fingerprint,
    FingerprintTimeout,
    FpcalcMissing,
    UnreadableAudio,
    fingerprint_file,
)
from app.enrich.errors import (
    AmbiguousMatch,
    MatchRejected,
    NoMatchKey,
    NotMatched,
    SourceGated,
)
from app.enrich.matching import VARIOUS_ARTISTS_MBID
from app.enrich.types import AlbumSnapshot, EnrichmentJob, TrackSnapshot
from app.models import (
    Album,
    AlbumStatus,
    Artist,
    Base,
    EnrichmentEntity,
    EnrichmentSource,
    FingerprintState,
    Track,
    TrackMetadata,
    TrackStatus,
)
from app.net.ratelimit import CircuitBreaker, RateLimiter

RECORDING_A = "a351de58-0a30-4eab-ad82-a7953c1a1897"
RECORDING_B = "f0fb85e6-6496-4c16-90b4-6401e71b72ba"
RECORDING_C = "6cf0f2f6-6b0e-4b9a-9c2f-1a0f3d6a2b77"
RECORDING_OTHER = "11111111-2222-3333-4444-555555555555"

#: The two songs one AcoustID cluster really returned together for a single
#: Knopfler file. Nothing about them makes one of the pair the right answer.
SO_FAR_AWAY = "22222222-3333-4444-5555-666666666601"
REAR_VIEW_MIRROR = "22222222-3333-4444-5555-666666666602"

DOWN_THE_ROAD = "33333333-4444-5555-6666-777777777701"
MILANO = "33333333-4444-5555-6666-777777777702"
RELEASE_UK = "33333333-4444-5555-6666-777777777703"
RELEASE_US = "33333333-4444-5555-6666-777777777704"
GROUP_BROTHERS = "44444444-5555-6666-7777-888888888801"

ARTIST_BONAMASSA = "55555555-6666-7777-8888-999999999901"
ARTIST_SOMEBODY_ELSE = "55555555-6666-7777-8888-999999999902"


# ---------------------------------------------------------------------------
# fpcalc
# ---------------------------------------------------------------------------
def fake_run(
    *, returncode: int = 0, stdout: str = "", stderr: str = "", raises: Exception | None = None
) -> Callable[..., Any]:
    def run(*args: Any, **kwargs: Any) -> Any:
        if raises is not None:
            raise raises

        class Completed:
            pass

        completed = Completed()
        completed.returncode = returncode  # type: ignore[attr-defined]
        completed.stdout = stdout  # type: ignore[attr-defined]
        completed.stderr = stderr  # type: ignore[attr-defined]
        return completed

    return run


@pytest.fixture(name="audio")
def audio_fixture(tmp_path: Path) -> Path:
    target = tmp_path / "01 - This Train.flac"
    target.write_bytes(b"not really audio")
    return target


def test_a_missing_binary_is_not_a_verdict_about_the_file(audio: Path) -> None:
    """It gates the rung. Confusing the two would condemn a healthy library."""
    with pytest.raises(FpcalcMissing):
        fingerprint_file(audio, fpcalc=None, runner=fake_run())


def test_a_decode_failure_is_a_verdict_about_the_file(audio: Path) -> None:
    with pytest.raises(UnreadableAudio) as caught:
        fingerprint_file(
            audio, fpcalc="/usr/bin/fpcalc", runner=fake_run(returncode=1, stderr="ERROR: bad frame")
        )

    assert caught.value.path == audio
    assert "bad frame" in str(caught.value)


def test_a_timeout_is_not_a_verdict_about_the_file(audio: Path) -> None:
    """A slow decode and an undecodable file are different claims, and only one
    of them ends with the file being moved into the trash by a nightly job."""
    with pytest.raises(FingerprintTimeout) as caught:
        fingerprint_file(
            audio,
            fpcalc="/usr/bin/fpcalc",
            runner=fake_run(raises=subprocess.TimeoutExpired("fpcalc", 60)),
        )

    assert not isinstance(caught.value, UnreadableAudio), "corruption is a decode that ran"
    assert caught.value.path == audio


def test_a_missing_file_is_not_a_verdict_about_the_file(tmp_path: Path) -> None:
    """A path that does not resolve is not a decode that failed.

    Nothing has been read at that point, so there is no evidence about the audio
    to report — and the answer to corruption is a file that shows on the
    Integrity screen as needing replacing, and that somebody may then quarantine.
    A share that blipped, a folder another tagger renamed, a row the scan has not
    caught up with: all of them arrive here, and all of them used to be recorded
    as ``corrupt``.
    """
    with pytest.raises(FileUnavailable) as caught:
        fingerprint_file(tmp_path / "gone.flac", fpcalc="/usr/bin/fpcalc")

    assert not isinstance(caught.value, UnreadableAudio), "nothing decoded it"
    assert caught.value.path == tmp_path / "gone.flac"


def test_a_good_run_parses_duration_and_fingerprint(audio: Path) -> None:
    out = json.dumps({"duration": 245.76, "fingerprint": "AQADtEmi"})
    result = fingerprint_file(audio, fpcalc="/usr/bin/fpcalc", runner=fake_run(stdout=out))

    assert result.duration == 246, "rounded — AcoustID wants whole seconds"
    assert result.fingerprint == "AQADtEmi"


def test_output_without_a_fingerprint_is_unreadable(audio: Path) -> None:
    out = json.dumps({"duration": 245.76})
    with pytest.raises(UnreadableAudio):
        fingerprint_file(audio, fpcalc="/usr/bin/fpcalc", runner=fake_run(stdout=out))


def test_unparseable_output_is_unreadable(audio: Path) -> None:
    with pytest.raises(UnreadableAudio):
        fingerprint_file(audio, fpcalc="/usr/bin/fpcalc", runner=fake_run(stdout="<html>"))


# ---------------------------------------------------------------------------
# AcoustID harness
# ---------------------------------------------------------------------------
def make_settings(**overrides: Any) -> Settings:
    base: dict[str, Any] = {
        "qobuz_app_id": "app",
        "qobuz_user_auth_token": "token",
        "acoustid_api_key": "test-key",
        "backoff_initial": 0.0,
        "backoff_max": 0.0,
        "backoff_jitter": 0.0,
        "request_max_retries": 1,
    }
    base.update(overrides)
    return Settings(**base)


def make_limiter() -> RateLimiter:
    now = [1_000.0]

    async def sleep(seconds: float) -> None:
        now[0] += seconds
        await asyncio.sleep(0)

    return RateLimiter(
        min_interval=0.0,
        max_per_hour=10_000,
        breaker=CircuitBreaker(time_func=lambda: now[0]),
        time_func=lambda: now[0],
        sleep_func=sleep,
        name="acoustid",
    )


def make_provider(
    lookups: dict[str, Any] | None = None,
    *,
    settings: Settings | None = None,
    broken: set[str] | None = None,
    timing_out: set[str] | None = None,
    absent: set[str] | None = None,
    fpcalc: str | None = "/usr/bin/fpcalc",
) -> tuple[AcoustIdProvider, list[httpx.URL]]:
    """A provider whose fingerprints are canned and whose lookups are mocked.

    ``broken`` names files that "cannot be decoded", by basename; ``timing_out``
    names files whose decode never finishes, which is a different claim. The
    whole request URL is captured rather than just the fingerprint, because one
    test is about a query parameter that has silently broken this rung before.

    ``absent`` is the third claim and is deliberately **not** canned: those names
    go through the real :func:`fingerprint_file` against a path that is genuinely
    not there, so the test is coupled to what that function actually raises. A
    hand-rolled exception here would pass whatever chromaprint decided a missing
    file means, which is the thing being pinned down.
    """
    hits: list[httpx.URL] = []
    answers = lookups or {}
    damaged = broken or set()
    slow = timing_out or set()
    gone = absent or set()

    def handler(request: httpx.Request) -> httpx.Response:
        hits.append(request.url)
        fingerprint = request.url.params.get("fingerprint", "")
        return httpx.Response(200, json=answers.get(fingerprint, {"status": "ok", "results": []}))

    def fingerprinter(path: Path, **kwargs: Any) -> Fingerprint:
        name = Path(path).name
        if name in damaged:
            raise UnreadableAudio(f"cannot decode {name}", path=Path(path))
        if name in slow:
            raise FingerprintTimeout(f"timed out on {name}", path=Path(path))
        if name in gone:
            return fingerprint_file(Path(path), fpcalc="/usr/bin/fpcalc")
        return Fingerprint(duration=240, fingerprint=f"fp-{name}", path=Path(path))

    resolved = settings or make_settings()
    client = AcoustIdClient(
        api_key=resolved.acoustid_api_key,
        limiter=make_limiter(),
        settings=resolved,
        http_client=httpx.AsyncClient(
            transport=httpx.MockTransport(handler), base_url="https://api.acoustid.org/v2/"
        ),
    )
    provider = AcoustIdProvider(
        limiter=make_limiter(), settings=resolved, client=client, fingerprinter=fingerprinter
    )
    provider._fpcalc = fpcalc  # noqa: SLF001 - the binary is never really present
    return provider, hits


def release(
    release_id: str,
    position: int | None = None,
    *,
    title: str = "Down the Road Wherever",
    track_count: int = 15,
    medium: int = 1,
    artists: list[dict[str, Any]] | None = None,
    group: str | None = None,
) -> dict[str, Any]:
    """One release as ``meta=releases`` hands it back.

    ``position`` is optional because AcoustID very often says a recording is *on*
    a release without saying where; the solve falls back to the recording's own
    identity for those, and both shapes need exercising.
    """
    payload: dict[str, Any] = {"id": release_id, "title": title, "track_count": track_count}
    if position is not None:
        payload["mediums"] = [
            {"position": medium, "track_count": track_count, "tracks": [{"position": position}]}
        ]
    if artists is not None:
        payload["artists"] = artists
    if group is not None:
        payload["releasegroup"] = {"id": group}
    return payload


def recording(mbid: str, *releases: dict[str, Any]) -> dict[str, Any]:
    return {"id": mbid, "releases": list(releases)}


def answer(*recordings: dict[str, Any], acoustid: str = "aid-1", score: float = 0.98) -> dict[str, Any]:
    """One AcoustID lookup response: a cluster, its score, and what it is."""
    return {
        "status": "ok",
        "results": [{"id": acoustid, "score": score, "recordings": list(recordings)}],
    }


def acoustid_result(acoustid: str, mbid: str, score: float = 0.98) -> dict[str, Any]:
    """A recognised file with a recording and *no release information*.

    Which is a real shape — the index knows the recording but lists nowhere it
    was published — and the one the corroboration tests want, because it leaves
    the coverage solve nothing to identify and the audio nothing to do but agree
    or disagree with the metadata.
    """
    return answer(recording(mbid), acoustid=acoustid, score=score)


def rec_id(index: int) -> str:
    return f"00000000-0000-4000-8000-{index:012d}"


def knopfler_folder(
    files: int = 15, *, track_count: int = 15, **release_kwargs: Any
) -> tuple[tuple[TrackSnapshot, ...], dict[str, Any]]:
    """The measured case, end to end: fifteen files, one also on a promo.

    Same shape as ``tests/test_enrich_coverage.py`` builds by hand, driven here
    through fingerprints and mocked HTTP so the wiring is exercised too.
    """
    tracks = tuple(track(f"{n:02d}.flac", index=n) for n in range(1, files + 1))
    lookups: dict[str, Any] = {}
    for n in range(1, files + 1):
        on = [release(DOWN_THE_ROAD, n, track_count=track_count, **release_kwargs)]
        if n == 4:
            on.append(release(MILANO, 1, title="On the Road to Milano", track_count=15))
        lookups[f"fp-{n:02d}.flac"] = answer(recording(rec_id(n), *on), acoustid=f"aid-{n}")
    return tracks, lookups


def album_job(tracks: tuple[TrackSnapshot, ...], **overrides: Any) -> EnrichmentJob:
    base: dict[str, Any] = {
        "id": "al1",
        "artist_id": "a1",
        "artist_name": "Joe Bonamassa",
        "title": "Blues Of Desperation",
        "tracks_count": len(tracks),
        "tracks": tracks,
    }
    base.update(overrides)
    return EnrichmentJob(
        entity_type=EnrichmentEntity.ALBUM,
        source=EnrichmentSource.ACOUSTID,
        entity_id=base["id"],
        subject=AlbumSnapshot(**base),
    )


def track(name: str, *, recording: str | None = None, index: int = 1) -> TrackSnapshot:
    return TrackSnapshot(
        id=f"t{index}",
        title=f"Track {index}",
        track_number=index,
        media_number=1,
        path=f"/music/{name}",
        mb_recording_mbid=recording,
    )


def run(provider: AcoustIdProvider, job: EnrichmentJob) -> Any:
    async def go() -> Any:
        try:
            return await provider.fetch(job)
        finally:
            await provider.aclose()

    return asyncio.run(go())


# ---------------------------------------------------------------------------
# Gating
# ---------------------------------------------------------------------------
def test_without_fpcalc_the_rung_is_gated() -> None:
    provider, hits = make_provider(fpcalc=None)

    assert provider.is_ready() is False
    with pytest.raises(SourceGated):
        run(provider, album_job((track("a.flac"),)))
    assert hits == []


def test_nothing_on_disk_is_no_key() -> None:
    provider, _ = make_provider()
    empty = TrackSnapshot(id="t1", title="x", track_number=1, media_number=1)

    with pytest.raises(NoMatchKey):
        run(provider, album_job((empty,)))


def test_without_an_api_key_the_corruption_check_still_runs() -> None:
    """The integrity half is local, and is the more valuable half for anyone who
    never registers a key."""
    provider, hits = make_provider(
        settings=make_settings(acoustid_api_key=""), broken={"b.flac"}
    )

    result = run(
        provider, album_job((track("a.flac", index=1), track("b.flac", index=2)))
    )

    assert hits == [], "no lookups without a key"
    assert result.track_fields["t1"]["fingerprint_state"] == FingerprintState.OK.value
    assert result.track_fields["t2"]["fingerprint_state"] == FingerprintState.CORRUPT.value


# ---------------------------------------------------------------------------
# The lookup itself
# ---------------------------------------------------------------------------
def test_the_lookup_asks_for_releases_with_a_space_separated_meta() -> None:
    """Both halves of this have silently broken the rung before.

    ``releaseids`` gives a bare list of ids, which carries no track count, so
    every candidate fails the count requirement and nothing is ever identified.
    And a literal ``+`` as the separator is percent-encoded to ``%2B``, at which
    point AcoustID reads one unknown token and answers 200 with no recordings at
    all — a rung that looks like it is working and identifies nothing.
    """
    provider, hits = make_provider()

    with pytest.raises(NotMatched):
        run(provider, album_job((track("a.flac"),)))

    assert hits[0].params["meta"] == "recordings releases"
    assert "%2B" not in str(hits[0]), "a literal + would arrive as an unknown token"


# ---------------------------------------------------------------------------
# Identity — the album is solved, never the track
# ---------------------------------------------------------------------------
def test_the_release_that_accounts_for_every_file_identifies_the_album() -> None:
    """The measured case: 15 of 15 against 1 of 15, decided by coverage."""
    tracks, lookups = knopfler_folder()
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.album_fields["mb_release_mbid"] == DOWN_THE_ROAD
    assert result.album_fields["mb_match_method"] == "audio-coverage"
    assert result.album_fields["title"] == "Down the Road Wherever"
    assert result.album_fields["track_count"] == 15
    assert "15 of 15" in (result.note or "")


def test_the_identified_release_is_what_gives_each_file_its_recording() -> None:
    tracks, lookups = knopfler_folder()
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.track_fields["t1"]["mb_recording_mbid"] == rec_id(1)
    assert result.track_fields["t1"]["match_method"] == "audio-release"
    assert result.track_fields["t1"]["acoustid"] == "aid-1"


def test_one_cluster_two_songs_is_normal_and_the_album_settles_it() -> None:
    """Measured: one Knopfler file came back as two different songs at once.

    Neither of them is "the first" in any sense worth acting on. The file is only
    resolved because the folder is: of the two recordings offered, exactly one is
    published on the release the other fourteen files agree about.
    """
    tracks, lookups = knopfler_folder()
    lookups["fp-01.flac"] = answer(
        # The wrong one listed first, deliberately. A provider that took the top
        # recording would write "Rear View Mirror" into this file.
        recording(REAR_VIEW_MIRROR, release(MILANO, 2, title="On the Road to Milano")),
        recording(SO_FAR_AWAY, release(DOWN_THE_ROAD, 1)),
        acoustid="aid-1",
    )
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.album_fields["mb_release_mbid"] == DOWN_THE_ROAD
    assert result.track_fields["t1"]["mb_recording_mbid"] == SO_FAR_AWAY


def test_a_file_the_index_knows_with_no_recordings_is_silence() -> None:
    """Measured: 1 file in 10 scored 0.948 with nothing at all attached.

    It is a hole in the index, not a failure. The other four files still identify
    the release, and the hole still gets its fingerprint state and its cluster id.
    """
    tracks = tuple(track(f"{n:02d}.flac", index=n) for n in range(1, 6))
    lookups = {
        f"fp-{n:02d}.flac": answer(
            recording(rec_id(n), release(DOWN_THE_ROAD, n, track_count=5)),
            acoustid=f"aid-{n}",
        )
        for n in range(1, 5)
    }
    lookups["fp-05.flac"] = answer(acoustid="aid-5", score=0.948)
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.album_fields["mb_release_mbid"] == DOWN_THE_ROAD
    assert result.track_fields["t5"]["acoustid"] == "aid-5"
    assert "mb_recording_mbid" not in result.track_fields["t5"]
    assert result.track_fields["t5"]["fingerprint_state"] == FingerprintState.OK.value


def test_a_low_scoring_result_is_a_guess_and_is_ignored() -> None:
    """A guess here is worth less than nothing: it would identify the wrong record."""
    tracks, lookups = knopfler_folder()
    provider, _ = make_provider(
        {name: {**payload, "results": [{**payload["results"][0], "score": 0.2}]}
         for name, payload in lookups.items()}
    )

    with pytest.raises(NotMatched):
        run(provider, album_job(tracks))


def test_recognising_nothing_is_not_a_match() -> None:
    provider, _ = make_provider()

    with pytest.raises(NotMatched) as caught:
        run(provider, album_job((track("a.flac"),)))

    assert "resolved to a recording" in str(caught.value)


def test_an_unidentified_album_still_records_what_the_audio_measured() -> None:
    """"Not identified" and "learned nothing" are different claims."""
    provider, _ = make_provider(broken={"b.flac"})

    with pytest.raises(NotMatched) as caught:
        run(provider, album_job((track("a.flac", index=1), track("b.flac", index=2))))

    states = caught.value.partial.track_fields
    assert states["t1"]["fingerprint_state"] == FingerprintState.OK.value
    assert states["t2"]["fingerprint_state"] == FingerprintState.CORRUPT.value


def test_two_editions_with_one_tracklist_are_ambiguous_not_a_contest() -> None:
    """Nothing *about* the audio differs between two identical pressings."""
    tracks = tuple(track(f"{n:02d}.flac", index=n) for n in range(1, 13))
    lookups = {
        f"fp-{n:02d}.flac": answer(
            recording(
                rec_id(n),
                release(RELEASE_UK, n, title="Brothers in Arms", track_count=12,
                        group=GROUP_BROTHERS),
                release(RELEASE_US, n, title="Brothers in Arms", track_count=12,
                        group=GROUP_BROTHERS),
            ),
            acoustid=f"aid-{n}",
        )
        for n in range(1, 13)
    }
    provider, _ = make_provider(lookups)

    with pytest.raises(AmbiguousMatch) as caught:
        run(provider, album_job(tracks))

    assert {item["id"] for item in caught.value.candidates} == {RELEASE_UK, RELEASE_US}
    assert "Brothers in Arms" in str(caught.value)


def test_an_ambiguous_verdict_still_records_the_record_it_could_name() -> None:
    """Which pressing is unknowable from the audio; which record is not."""
    tracks = tuple(track(f"{n:02d}.flac", index=n) for n in range(1, 13))
    lookups = {
        f"fp-{n:02d}.flac": answer(
            recording(
                rec_id(n),
                release(RELEASE_UK, n, title="Brothers in Arms", track_count=12,
                        group=GROUP_BROTHERS),
                release(RELEASE_US, n, title="Brothers in Arms", track_count=12,
                        group=GROUP_BROTHERS),
            ),
            acoustid=f"aid-{n}",
        )
        for n in range(1, 13)
    }
    provider, _ = make_provider(lookups)

    with pytest.raises(AmbiguousMatch) as caught:
        run(provider, album_job(tracks))

    fields = caught.value.partial.album_fields
    assert fields["mb_release_group_mbid"] == GROUP_BROTHERS
    assert fields["mb_match_method"] == "audio-rg"
    assert "mb_release_mbid" not in fields, "no pressing may be invented"


def test_an_identification_leaves_alone_what_it_does_not_know() -> None:
    """A key present with ``None`` clears the column, so absent means absent.

    A payload with no title must not wipe the title MusicBrainz supplied.
    """
    tracks, lookups = knopfler_folder(files=2, track_count=2)
    for name, payload in lookups.items():
        for entry in payload["results"][0]["recordings"]:
            for item in entry["releases"]:
                item.pop("title", None)
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.album_fields["mb_release_mbid"] == DOWN_THE_ROAD
    assert "title" not in result.album_fields


# ---------------------------------------------------------------------------
# Identity propagating upward — the release's first credit
# ---------------------------------------------------------------------------
def test_the_identified_release_names_the_artist() -> None:
    tracks, lookups = knopfler_folder(
        artists=[{"id": ARTIST_BONAMASSA, "name": "Joe Bonamassa"}]
    )
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.artist_fields["mb_artist_mbid"] == ARTIST_BONAMASSA
    assert result.artist_fields["mb_match_method"] == "audio-credit"
    assert result.reopen == [
        (EnrichmentEntity.ARTIST, "a1", EnrichmentSource.MUSICBRAINZ)
    ]


def test_a_credit_that_is_not_this_artist_is_refused() -> None:
    """The name is a veto, never a selection. A guest spot bills somebody else."""
    tracks, lookups = knopfler_folder(
        artists=[{"id": ARTIST_SOMEBODY_ELSE, "name": "Mark Knopfler"}]
    )
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.album_fields["mb_release_mbid"] == DOWN_THE_ROAD, "the release stands"
    assert result.artist_fields == {}
    assert result.reopen == []


def test_various_artists_never_becomes_an_artist() -> None:
    """Credited on every compilation; deriving from it points a library at one
    wrong entity. Rejected by id, whatever the name beside it says."""
    tracks, lookups = knopfler_folder(
        artists=[{"id": VARIOUS_ARTISTS_MBID, "name": "Joe Bonamassa"}]
    )
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.artist_fields == {}


def test_a_release_with_no_credit_derives_no_artist() -> None:
    """A track's own credit is the performer, which on a compilation is somebody
    else — so it is never read as the album artist."""
    tracks, lookups = knopfler_folder()
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(tracks))

    assert result.artist_fields == {}


# ---------------------------------------------------------------------------
# Corroboration and denial — the older job, and what is left of it
# ---------------------------------------------------------------------------
def test_agreeing_audio_confirms_the_metadata_match() -> None:
    """Nothing to identify from — no release information — but nothing wrong."""
    provider, _ = make_provider(
        {
            "fp-a.flac": acoustid_result("aid-1", RECORDING_A),
            "fp-b.flac": acoustid_result("aid-2", RECORDING_B),
        }
    )

    with pytest.raises(NotMatched) as caught:
        run(
            provider,
            album_job(
                (
                    track("a.flac", recording=RECORDING_A, index=1),
                    track("b.flac", recording=RECORDING_B, index=2),
                )
            ),
        )

    assert not isinstance(caught.value, MatchRejected), "agreement is not a denial"
    assert set(caught.value.partial.track_fields) == {"t1", "t2"}


def test_one_differing_track_is_ordinary_and_confirms_nothing_wrong() -> None:
    """A bonus track or a different mix of a single. Not evidence of a bad match."""
    provider, _ = make_provider(
        {
            "fp-a.flac": acoustid_result("aid-1", RECORDING_A),
            "fp-b.flac": acoustid_result("aid-2", RECORDING_B),
            "fp-c.flac": acoustid_result("aid-3", RECORDING_OTHER),
        }
    )

    with pytest.raises(NotMatched) as caught:
        run(
            provider,
            album_job(
                (
                    track("a.flac", recording=RECORDING_A, index=1),
                    track("b.flac", recording=RECORDING_B, index=2),
                    track("c.flac", recording=RECORDING_C, index=3),
                )
            ),
        )

    assert not isinstance(caught.value, MatchRejected)


def test_audio_that_mostly_disagrees_denies_the_match() -> None:
    """The barcode matched a different record. Stashed, not left quietly wrong."""
    provider, _ = make_provider(
        {
            "fp-a.flac": acoustid_result("aid-1", RECORDING_OTHER),
            "fp-b.flac": acoustid_result("aid-2", RECORDING_OTHER),
            "fp-c.flac": acoustid_result("aid-3", RECORDING_OTHER),
        }
    )

    with pytest.raises(MatchRejected):
        run(
            provider,
            album_job(
                (
                    track("a.flac", recording=RECORDING_A, index=1),
                    track("b.flac", recording=RECORDING_B, index=2),
                    track("c.flac", recording=RECORDING_C, index=3),
                )
            ),
        )


def test_the_mapped_recording_anywhere_in_the_answer_counts_as_agreement() -> None:
    """Same measurement as above, applied to denial: a cluster holding two songs
    is ordinary, so demanding the mapped one come first would deny good matches
    on a coin toss — and a denial stashes a whole album for a human."""
    provider, _ = make_provider(
        {
            f"fp-{name}.flac": answer(
                recording(RECORDING_OTHER), recording(mapped), acoustid=f"aid-{name}"
            )
            for name, mapped in (("a", RECORDING_A), ("b", RECORDING_B), ("c", RECORDING_C))
        }
    )

    with pytest.raises(NotMatched) as caught:
        run(
            provider,
            album_job(
                (
                    track("a.flac", recording=RECORDING_A, index=1),
                    track("b.flac", recording=RECORDING_B, index=2),
                    track("c.flac", recording=RECORDING_C, index=3),
                )
            ),
        )

    assert not isinstance(caught.value, MatchRejected)


def test_one_recognised_track_cannot_deny_a_whole_release() -> None:
    """AcoustID knowing only the single off an obscure album is the ordinary
    case, and a "majority" of one is not evidence that the barcode was wrong."""
    provider, _ = make_provider(
        {"fp-a.flac": acoustid_result("aid-1", RECORDING_OTHER)}
    )

    with pytest.raises(NotMatched) as caught:
        run(
            provider,
            album_job(
                (
                    track("a.flac", recording=RECORDING_A, index=1),
                    track("b.flac", recording=RECORDING_B, index=2),
                    track("c.flac", recording=RECORDING_C, index=3),
                )
            ),
        )

    assert not isinstance(caught.value, MatchRejected)


def test_the_audio_overrules_the_metadata_rather_than_being_denied_by_it() -> None:
    """Audio is the anchor. When the fingerprints name a release outright, a
    tracklist somebody else typed does not get to veto it — the release the audio
    identified is written, and the metadata rungs re-derive from that id."""
    tracks, lookups = knopfler_folder()
    contradicted = tuple(
        track(f"{n:02d}.flac", recording=RECORDING_OTHER, index=n)
        for n in range(1, 16)
    )
    provider, _ = make_provider(lookups)

    result = run(provider, album_job(contradicted))

    assert result.album_fields["mb_release_mbid"] == DOWN_THE_ROAD


def test_with_nothing_to_compare_against_silence_is_the_answer() -> None:
    """No MusicBrainz mapping yet, so the audio has nothing to contradict."""
    provider, _ = make_provider({"fp-a.flac": acoustid_result("aid-1", RECORDING_A)})

    with pytest.raises(NotMatched) as caught:
        run(provider, album_job((track("a.flac"),)))

    assert caught.value.partial.track_fields["t1"]["acoustid"] == "aid-1"


# ---------------------------------------------------------------------------
# Corruption
# ---------------------------------------------------------------------------
def test_an_undecodable_file_is_marked_corrupt() -> None:
    """And does not stop the album being identified, which is the point.

    A folder with a broken file in it is exactly the one whose identity is worth
    establishing — the quarantine and the refill that follows both hang off it —
    so the file that would not decode counts as a hole in the evidence rather
    than as a smaller album. Both verdicts come out of the one pass.
    """
    tracks, lookups = knopfler_folder()
    provider, _ = make_provider(lookups, broken={"07.flac"})

    result = run(provider, album_job(tracks))

    assert result.album_fields["mb_release_mbid"] == DOWN_THE_ROAD
    assert result.track_fields["t7"]["fingerprint_state"] == FingerprintState.CORRUPT.value
    assert "unplayable" in (result.note or "")
    assert "14 of 15" in (result.note or ""), "the hole is reported, not hidden"


def test_a_broken_file_does_not_shrink_the_directory() -> None:
    """The track count is checked against the *folder*, not against what decoded.

    Drop the unreadable file from the solve and a fifteen-track release stops
    accounting for a fourteen-file folder, so every album with one bad file in it
    becomes permanently unidentifiable.
    """
    tracks, lookups = knopfler_folder()
    provider, _ = make_provider(lookups, broken={"07.flac"}, timing_out={"09.flac"})

    result = run(provider, album_job(tracks))

    assert result.album_fields["track_count"] == 15
    assert "mb_recording_mbid" not in result.track_fields["t7"]


def test_an_album_where_nothing_decodes_is_rejected() -> None:
    provider, _ = make_provider(broken={"a.flac", "b.flac"})

    with pytest.raises(MatchRejected):
        run(provider, album_job((track("a.flac", index=1), track("b.flac", index=2))))


def test_an_album_where_nothing_decodes_still_records_the_corruption() -> None:
    """The worst case is the one that has to be recorded: an album where every
    file is broken is exactly the album that needs quarantining, and the verdict
    travels with the rejection rather than being computed and dropped."""
    provider, _ = make_provider(broken={"a.flac", "b.flac"})

    with pytest.raises(MatchRejected) as caught:
        run(provider, album_job((track("a.flac", index=1), track("b.flac", index=2))))

    states = {
        track_id: fields["fingerprint_state"]
        for track_id, fields in caught.value.partial.track_fields.items()
    }
    assert states == {
        "t1": FingerprintState.CORRUPT.value,
        "t2": FingerprintState.CORRUPT.value,
    }


def test_an_album_whose_files_are_not_there_is_not_called_corrupt() -> None:
    """An unreachable path is a fact about the mount, not about the audio.

    The whole sequence used to run automatically: a share blips, the enrichment
    tick fingerprints the album, every file records ``corrupt``, the share comes
    back, the scan re-points the rows at files that hash to exactly what was
    recorded — so the fifth gate passes — and the same night's quarantine moves
    a healthy release into the trash. Nothing here may write a verdict.
    """
    provider, _ = make_provider(absent={"a.flac", "b.flac"})

    with pytest.raises(NotMatched) as caught:
        run(provider, album_job((track("a.flac", index=1), track("b.flac", index=2))))

    assert not isinstance(caught.value, MatchRejected), "nothing was measured"
    assert getattr(caught.value, "partial", None) is None


def test_a_file_that_vanished_leaves_the_rest_of_the_album_alone() -> None:
    """One unreachable file must not take the album's verdicts with it."""
    tracks, lookups = knopfler_folder()
    provider, _ = make_provider(lookups, absent={"07.flac"})

    result = run(provider, album_job(tracks))

    assert result.album_fields["mb_release_mbid"] == DOWN_THE_ROAD
    assert "fingerprint_state" not in result.track_fields.get("t7", {})


def test_an_album_that_only_timed_out_is_not_called_corrupt() -> None:
    """A busy machine is not a broken library. Nothing was measured, so nothing
    is claimed — the alternative has housekeeping trash a healthy album."""
    provider, _ = make_provider(timing_out={"a.flac", "b.flac"})

    with pytest.raises(NotMatched) as caught:
        run(provider, album_job((track("a.flac", index=1), track("b.flac", index=2))))

    assert "fingerprinted" in str(caught.value)
    assert not isinstance(caught.value, MatchRejected)


# ---------------------------------------------------------------------------
# Duplicates
# ---------------------------------------------------------------------------
def test_duplicate_groups_reports_repeats_only() -> None:
    """The same recording on a single, an EP and two compilations."""
    groups = duplicate_groups(
        [("t1", "aid-1"), ("t2", "aid-1"), ("t3", "aid-2"), ("t4", None), ("t5", "aid-1")]
    )

    assert groups == {"aid-1": ["t1", "t2", "t5"]}


def test_duplicate_groups_ignores_unfingerprinted_tracks() -> None:
    assert duplicate_groups([("t1", None), ("t2", None)]) == {}


# ---------------------------------------------------------------------------
# Quarantine — the part that touches disk
# ---------------------------------------------------------------------------
@pytest.fixture(name="library")
def library_fixture(tmp_path: Path) -> Iterator[tuple[Any, Settings, Path]]:
    """A scratch library with one two-track album, one track flagged corrupt."""
    root = tmp_path / "music"
    album_dir = root / "Joe Bonamassa" / "Blues Of Desperation (2016)"
    album_dir.mkdir(parents=True)
    good = album_dir / "01 - This Train.flac"
    bad = album_dir / "02 - Mountain Climbing.flac"
    good.write_bytes(b"good audio")
    bad.write_bytes(b"\x00broken")

    settings = make_settings(
        library_path=str(root), data_path=str(tmp_path / "data"), acoustid_api_key=""
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
                    tracks_count=2,
                    status=AlbumStatus.DOWNLOADED,
                    path=str(album_dir),
                )
            )
            for index, path in ((1, good), (2, bad)):
                session.add(
                    Track(
                        id=f"t{index}",
                        album_id="al1",
                        title=f"Track {index}",
                        track_number=index,
                        media_number=1,
                        status=TrackStatus.DOWNLOADED,
                        path=str(path),
                    )
                )
            session.add(
                TrackMetadata(
                    track_id="t1", fingerprint_state=FingerprintState.OK
                )
            )
            session.add(
                TrackMetadata(
                    track_id="t2", fingerprint_state=FingerprintState.CORRUPT
                )
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


def test_a_corrupt_file_goes_to_the_trash_and_the_good_one_stays(
    library: tuple[Any, Settings, Path]
) -> None:
    """Never unlinked. "This file is broken" must be a recoverable verdict."""
    factory, settings, album_dir = library

    async def go() -> Any:
        async with factory() as session:
            return await quarantine_corrupt_files(session, settings=settings)

    result = asyncio.run(go())

    assert result.files_trashed == 1
    assert (album_dir / "01 - This Train.flac").exists(), "the good file is untouched"
    assert not (album_dir / "02 - Mountain Climbing.flac").exists()

    trashed = list(settings.trash_dir.glob(f"*/{PAYLOAD_DIRNAME}/*.flac"))
    assert [p.name for p in trashed] == ["02 - Mountain Climbing.flac"]


def test_the_track_is_marked_missing_and_the_album_becomes_wanted_again(
    library: tuple[Any, Settings, Path]
) -> None:
    factory, settings, _ = library

    async def go() -> tuple[Track, Track, Album]:
        async with factory() as session:
            await quarantine_corrupt_files(session, settings=settings)
        async with factory() as session:
            return (
                await session.get(Track, "t1"),
                await session.get(Track, "t2"),
                await session.get(Album, "al1"),
            )

    good, bad, album = asyncio.run(go())

    assert good.status is TrackStatus.DOWNLOADED
    assert bad.status is TrackStatus.PENDING and bad.path is None
    assert album.status is AlbumStatus.WANTED


def test_quarantine_never_queues_a_download(
    library: tuple[Any, Settings, Path]
) -> None:
    """Marking a gap is not a request to fill it — that stays a user action."""
    from app.models import QueueItem

    factory, settings, _ = library

    async def go() -> list[Any]:
        async with factory() as session:
            await quarantine_corrupt_files(session, settings=settings)
        async with factory() as session:
            return list((await session.execute(select(QueueItem))).scalars().all())

    assert asyncio.run(go()) == []


def test_a_busy_album_is_left_alone(library: tuple[Any, Settings, Path]) -> None:
    """A download writing into that folder is work in progress, not corruption."""
    factory, settings, album_dir = library

    async def go() -> Any:
        async with factory() as session:
            album = await session.get(Album, "al1")
            album.status = AlbumStatus.DOWNLOADING
        async with factory() as session:
            return await quarantine_corrupt_files(session, settings=settings)

    result = asyncio.run(go())

    assert result.files_trashed == 0
    assert result.errors, "and it says why"
    assert (album_dir / "02 - Mountain Climbing.flac").exists()


def test_a_healthy_library_is_a_no_op(library: tuple[Any, Settings, Path]) -> None:
    factory, settings, _ = library

    async def go() -> Any:
        async with factory() as session:
            meta = await session.get(TrackMetadata, "t2")
            meta.fingerprint_state = FingerprintState.OK
        async with factory() as session:
            return await quarantine_corrupt_files(session, settings=settings)

    result = asyncio.run(go())

    assert result.files_trashed == 0
    assert result.albums_checked == 0


# ---------------------------------------------------------------------------
# Muting — the alarm, never the fact
# ---------------------------------------------------------------------------
# ``Album.mute_integrity`` is the only flag in the codebase that suppresses a
# *safety* action, so what it may and may not reach is worth stating as tests
# rather than as a comment. It stops the quarantine and the figure that button
# counts; it does not stop the fingerprinting, clear a verdict, or change what
# the release says about itself on its own page.
async def _mute(factory: Any, album_id: str = "al1") -> None:
    async with factory() as session:
        album = await session.get(Album, album_id)
        album.mute_integrity = True


def test_a_muted_album_is_not_quarantined(library: tuple[Any, Settings, Path]) -> None:
    """The known-good rip that fails a strict check keeps its file.

    This is the whole point of the flag: without it, a release ``fpcalc`` cannot
    decode is moved to the trash by the nightly job, every night, for as long as
    it is in the library.
    """
    factory, settings, album_dir = library

    async def go() -> Any:
        await _mute(factory)
        async with factory() as session:
            return await quarantine_corrupt_files(session, settings=settings)

    result = asyncio.run(go())

    assert result.files_trashed == 0
    assert result.albums_checked == 0, "excluded in the SQL, not skipped in the loop"
    assert (album_dir / "02 - Mountain Climbing.flac").exists()
    assert not list(settings.trash_dir.glob(f"*/{PAYLOAD_DIRNAME}/*.flac"))


def test_muting_leaves_the_track_and_the_album_alone(
    library: tuple[Any, Settings, Path]
) -> None:
    """Not quarantined means not marked missing and not re-wanted either."""
    factory, settings, _ = library

    async def go() -> tuple[Track, Album]:
        await _mute(factory)
        async with factory() as session:
            await quarantine_corrupt_files(session, settings=settings)
        async with factory() as session:
            return await session.get(Track, "t2"), await session.get(Album, "al1")

    track, album = asyncio.run(go())

    assert track.status is TrackStatus.DOWNLOADED and track.path
    assert album.status is AlbumStatus.DOWNLOADED


def test_muting_does_not_erase_the_verdict(
    library: tuple[Any, Settings, Path]
) -> None:
    """The measurement stands. Muting is an instruction not to *act* on it."""
    factory, settings, _ = library

    async def go() -> TrackMetadata:
        await _mute(factory)
        async with factory() as session:
            await quarantine_corrupt_files(session, settings=settings)
        async with factory() as session:
            return await session.get(TrackMetadata, "t2")

    assert asyncio.run(go()).fingerprint_state is FingerprintState.CORRUPT


def test_a_muted_release_still_reports_its_own_corrupt_count(
    library: tuple[Any, Settings, Path]
) -> None:
    """Hides the alarm, never the fact — and this is where the fact lives.

    ``AlbumOut.corrupt_tracks`` is the release's own page. Suppressing it there
    too would make muting indistinguishable from a check that never ran, on
    exactly the release somebody has already decided something about.
    """
    from app.api.deps import album_corrupt_counts

    factory, _, _ = library

    async def go() -> dict[str, int]:
        await _mute(factory)
        async with factory() as session:
            return await album_corrupt_counts(session, ["al1"])

    assert asyncio.run(go()) == {"al1": 1}


def test_the_actionable_count_excludes_muted_and_publishes_the_remainder(
    library: tuple[Any, Settings, Path]
) -> None:
    """``corrupt_files`` is what the button would move; ``corrupt_muted`` is the rest.

    Counting a muted file in ``corrupt_files`` would put a number on the health
    screen that the quarantine then refuses to act on — an offer to do nothing.
    Dropping it entirely would be worse: a suppression with no trace anywhere
    reads exactly like a check that never ran.
    """
    from app.api.deps import integrity_status

    factory, settings, _ = library

    async def status(mute: bool) -> Any:
        if mute:
            await _mute(factory)
        async with factory() as session:
            return await integrity_status(session, settings)

    before = asyncio.run(status(False))
    assert (before.corrupt_files, before.corrupt_muted) == (1, 0)

    after = asyncio.run(status(True))
    assert (after.corrupt_files, after.corrupt_muted) == (0, 1)


def test_the_corrupt_list_and_the_quarantine_agree_about_muting(
    library: tuple[Any, Settings, Path]
) -> None:
    """The list sits above the button; a row it will not touch must not be in it."""
    from app.api.deps import list_corrupt_tracks

    factory, _, _ = library

    async def listed() -> tuple[list[Any], int]:
        async with factory() as session:
            return await list_corrupt_tracks(session)

    rows, total = asyncio.run(listed())
    assert [row.track_id for row in rows] == ["t2"] and total == 1

    asyncio.run(_mute(factory))
    rows, total = asyncio.run(listed())
    assert rows == [] and total == 0, "the count and the page agree"


def test_muting_one_release_does_not_shield_another(
    library: tuple[Any, Settings, Path]
) -> None:
    """The exclusion is per album, joined on the track's own album id."""
    factory, settings, album_dir = library
    other_dir = album_dir.parent / "Time Clocks (2021)"
    other_dir.mkdir()
    other_file = other_dir / "01 - Notches.flac"
    other_file.write_bytes(b"\x00also broken")

    async def go() -> Any:
        async with factory() as session:
            session.add(
                Album(
                    id="al2",
                    artist_id="a1",
                    title="Time Clocks",
                    tracks_count=1,
                    status=AlbumStatus.DOWNLOADED,
                    path=str(other_dir),
                )
            )
            session.add(
                Track(
                    id="t3",
                    album_id="al2",
                    title="Notches",
                    track_number=1,
                    media_number=1,
                    status=TrackStatus.DOWNLOADED,
                    path=str(other_file),
                )
            )
            session.add(
                TrackMetadata(track_id="t3", fingerprint_state=FingerprintState.CORRUPT)
            )
        await _mute(factory, "al1")
        async with factory() as session:
            return await quarantine_corrupt_files(session, settings=settings)

    result = asyncio.run(go())

    assert result.files_trashed == 1
    assert not other_file.exists(), "the unmuted release is still acted on"
    assert (album_dir / "02 - Mountain Climbing.flac").exists()
