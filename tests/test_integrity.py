"""The integrity layer, measured against real files rather than fakes.

Nothing here is mocked. The FLACs are genuine (if silent) — a STREAMINFO block
and nothing else, the same trick :mod:`tests.test_library_scan` uses — and the
MP3s are genuine MPEG-1 Layer III frames, so mutagen really parses both and
:func:`app.core.integrity.audio_sample_count` really goes through the code path
it will use on the user's library. A fake would prove nothing about the one claim
this module makes: that a tag write moves the bytes and leaves the sample count
alone, and that a change to the audio moves both.

That claim is what separates ``RETAGGED`` from ``REPLACED``, and the two license
different work — a retag keeps the identification, a replacement throws it away —
so it is asserted against a real ``mutagen`` save on both containers rather than
against a hand-built ``FileStamp``.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from mutagen.apev2 import APEv2
from mutagen.flac import FLAC
from mutagen.id3 import ID3, TIT2

from app.core.integrity import (
    DIGEST_SIZE,
    FileStamp,
    IntegrityState,
    album_content_digest,
    audio_sample_count,
    classify,
    hash_file,
    read_stamp,
    stat_stamp,
)


# ---------------------------------------------------------------------------
# Real files on disk
# ---------------------------------------------------------------------------
def write_flac(path: Path, *, seconds: int = 200, sample_rate: int = 44100) -> Path:
    """A minimal but valid FLAC: ``fLaC`` plus a STREAMINFO block, no frames.

    ``mutagen`` reports the stream properties from STREAMINFO alone, so this is
    enough to exercise the exact ``total_samples`` path — and the whole file is
    about 50 bytes, so a test library costs nothing.

    The MD5 field is left zeroed, which is not laziness: it is what 185 of the
    200 files in the user's real library look like, and it is precisely why
    :mod:`app.core.integrity` hashes the file itself instead of trusting it.
    """
    path.parent.mkdir(parents=True, exist_ok=True)

    bitstream = 0
    width = 0

    def put(value: int, bits_used: int) -> None:
        nonlocal bitstream, width
        bitstream = (bitstream << bits_used) | (value & ((1 << bits_used) - 1))
        width += bits_used

    put(4096, 16)  # minimum block size
    put(4096, 16)  # maximum block size
    put(0, 24)  # minimum frame size (unknown)
    put(0, 24)  # maximum frame size (unknown)
    put(sample_rate, 20)
    put(1, 3)  # channels - 1
    put(15, 5)  # bits per sample - 1
    put(sample_rate * seconds, 36)  # total samples
    streaminfo = bitstream.to_bytes(width // 8, "big") + b"\x00" * 16  # + empty MD5

    header = bytes([0x80]) + len(streaminfo).to_bytes(3, "big")  # last block, type 0
    path.write_bytes(b"fLaC" + header + streaminfo)
    return path


#: MPEG-1 Layer III, 128 kbps, 44100 Hz, stereo, no padding. 144 * 128000 / 44100
#: rounds down to 417 bytes per frame.
_MP3_FRAME = bytes([0xFF, 0xFB, 0x90, 0x00]) + b"\x00" * 413


def write_mp3(path: Path, *, frames: int = 40) -> Path:
    """A constant-bitrate MP3 of *frames* silent frames and no tags."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(_MP3_FRAME * frames)
    return path


# ---------------------------------------------------------------------------
# The cheap tripwire
# ---------------------------------------------------------------------------
def test_stat_stamp_reads_size_and_mtime(tmp_path: Path) -> None:
    path = write_flac(tmp_path / "a.flac")

    stat = stat_stamp(path)

    assert stat is not None
    size, mtime = stat
    assert size == path.stat().st_size
    assert mtime == pytest.approx(path.stat().st_mtime)


def test_stat_stamp_returns_none_for_a_missing_file(tmp_path: Path) -> None:
    assert stat_stamp(tmp_path / "nothing.flac") is None


# ---------------------------------------------------------------------------
# Hashing
# ---------------------------------------------------------------------------
def test_hash_file_is_128_bits_of_hex(tmp_path: Path) -> None:
    path = write_flac(tmp_path / "a.flac")

    digest = hash_file(path)

    assert len(digest) == DIGEST_SIZE * 2
    assert int(digest, 16) >= 0


def test_hash_file_is_stable_and_content_addressed(tmp_path: Path) -> None:
    """Same bytes hash the same wherever they live; different bytes do not."""
    one = write_flac(tmp_path / "one.flac", seconds=200)
    copy = tmp_path / "copy.flac"
    copy.write_bytes(one.read_bytes())
    other = write_flac(tmp_path / "other.flac", seconds=100)

    assert hash_file(one) == hash_file(copy)
    assert hash_file(one) != hash_file(other)


def test_hash_file_spans_more_than_one_chunk(tmp_path: Path) -> None:
    """A file larger than ``CHUNK_BYTES`` must hash its tail as well as its head.

    A chunked reader that stops after the first block would give every large file
    in the library the same verdict as long as its first megabyte was untouched —
    which is exactly what re-encoding the audio and keeping the tags looks like.
    """
    big = tmp_path / "big.bin"
    payload = bytes(range(256)) * 8192  # 2 MiB
    big.write_bytes(payload)
    tampered = tmp_path / "tampered.bin"
    tampered.write_bytes(payload[:-1] + b"\x00")

    assert hash_file(big) != hash_file(tampered)


def test_hash_file_raises_for_a_missing_file(tmp_path: Path) -> None:
    with pytest.raises(OSError):
        hash_file(tmp_path / "gone.flac")


# ---------------------------------------------------------------------------
# Sample counts
# ---------------------------------------------------------------------------
def test_sample_count_prefers_the_exact_flac_total(tmp_path: Path) -> None:
    path = write_flac(tmp_path / "a.flac", seconds=200, sample_rate=44100)

    assert audio_sample_count(path) == 44100 * 200


def test_sample_count_falls_back_to_length_times_rate(tmp_path: Path) -> None:
    """MP3 publishes no total, so the count is derived — and must be positive."""
    path = write_mp3(tmp_path / "a.mp3")

    count = audio_sample_count(path)

    assert count is not None and count > 0


def test_sample_count_is_none_when_nothing_says(tmp_path: Path) -> None:
    """Unparseable is ``None``, not a guess and not an exception."""
    junk = tmp_path / "junk.flac"
    junk.write_bytes(b"not audio at all")

    assert audio_sample_count(junk) is None


# ---------------------------------------------------------------------------
# read_stamp
# ---------------------------------------------------------------------------
def test_read_stamp_measures_everything(tmp_path: Path) -> None:
    path = write_flac(tmp_path / "a.flac", seconds=200)

    stamp = read_stamp(path)

    assert stamp is not None
    assert stamp.size == path.stat().st_size
    assert stamp.content_hash == hash_file(path)
    assert stamp.sample_count == 44100 * 200


def test_read_stamp_is_none_when_the_file_is_gone(tmp_path: Path) -> None:
    assert read_stamp(tmp_path / "gone.flac") is None


def test_read_stamp_still_hashes_a_file_mutagen_cannot_parse(tmp_path: Path) -> None:
    """A container mutagen does not recognise is not this module's verdict.

    Deciding a file is corrupt is :mod:`app.enrich.chromaprint`'s job, on the
    evidence of a completed decode, and that verdict ends with the file being
    moved out of the library. Returning ``None`` here would let "mutagen has not
    heard of this container" travel as ``MISSING``.
    """
    junk = tmp_path / "junk.flac"
    junk.write_bytes(b"not audio at all")

    stamp = read_stamp(junk)

    assert stamp is not None
    assert stamp.content_hash is not None
    assert stamp.sample_count is None


# ---------------------------------------------------------------------------
# classify — every branch
# ---------------------------------------------------------------------------
def _stamp(
    content_hash: str | None = "aa", sample_count: int | None = 100
) -> FileStamp:
    return FileStamp(size=1, mtime=1.0, content_hash=content_hash, sample_count=sample_count)


def test_classify_missing_beats_everything() -> None:
    assert classify(recorded=_stamp(), current=None) is IntegrityState.MISSING
    assert classify(recorded=None, current=None) is IntegrityState.MISSING


def test_classify_never_baselined_is_unknown_not_changed() -> None:
    """The three-state rule.

    A row with no recorded hash makes no claim about the file, so the file cannot
    contradict it. Reading this as change would report an entire un-baselined
    library as tampered with on its first pass, which buries the one real edit in
    thirty thousand false ones.
    """
    assert classify(recorded=None, current=_stamp()) is IntegrityState.UNKNOWN
    assert (
        classify(recorded=_stamp(content_hash=None), current=_stamp())
        is IntegrityState.UNKNOWN
    )


def test_classify_equal_hashes_are_verified() -> None:
    assert classify(recorded=_stamp("ab"), current=_stamp("ab")) is IntegrityState.VERIFIED


def test_classify_same_samples_different_bytes_is_retagged() -> None:
    state = classify(
        recorded=_stamp("ab", sample_count=8_820_000),
        current=_stamp("cd", sample_count=8_820_000),
    )
    assert state is IntegrityState.RETAGGED


def test_classify_different_samples_is_replaced() -> None:
    state = classify(
        recorded=_stamp("ab", sample_count=8_820_000),
        current=_stamp("cd", sample_count=4_410_000),
    )
    assert state is IntegrityState.REPLACED


@pytest.mark.parametrize(
    ("recorded_samples", "current_samples"),
    [
        (None, 8_820_000),
        (8_820_000, None),
        (None, None),
    ],
)
def test_classify_unknown_samples_are_not_agreement(
    recorded_samples: int | None, current_samples: int | None
) -> None:
    """``None == None`` is true and must not be read as "the audio matches".

    Two unmeasured sample counts are two refusals to answer, not a measurement
    that agreed. Calling that a retag files a genuine replacement as "metadata
    only", and a retag is precisely the verdict that says the identification
    still holds and the file need not be looked at again.
    """
    state = classify(
        recorded=_stamp("ab", sample_count=recorded_samples),
        current=_stamp("cd", sample_count=current_samples),
    )
    assert state is IntegrityState.REPLACED


# ---------------------------------------------------------------------------
# The real thing: a tag write against a real file
# ---------------------------------------------------------------------------
def test_retagging_a_real_flac_is_retagged_not_replaced(tmp_path: Path) -> None:
    path = write_flac(tmp_path / "a.flac", seconds=200)
    before = read_stamp(path)

    audio = FLAC(str(path))
    audio["title"] = ["Sultans of Swing"]
    audio["albumartist"] = ["Dire Straits"]
    audio.save()

    after = read_stamp(path)
    assert before is not None and after is not None
    assert after.content_hash != before.content_hash  # the bytes really moved
    assert after.sample_count == before.sample_count
    assert classify(recorded=before, current=after) is IntegrityState.RETAGGED


def test_retagging_a_real_mp3_is_retagged_not_replaced(tmp_path: Path) -> None:
    """The MP3 half of the same claim.

    An ID3v2 tag is *prepended*, which moves every audio byte in the file and so
    changes the hash outright — and mutagen measures the length from the first
    frame onward, so the derived sample count is unmoved. That asymmetry is the
    whole reason the sample count is the disambiguator.
    """
    path = write_mp3(tmp_path / "a.mp3")
    before = read_stamp(path)

    tags = ID3()
    tags.add(TIT2(encoding=3, text=["Telegraph Road"]))
    tags.save(str(path))

    after = read_stamp(path)
    assert before is not None and after is not None
    assert after.content_hash != before.content_hash
    assert after.sample_count == before.sample_count
    assert classify(recorded=before, current=after) is IntegrityState.RETAGGED


def test_an_id3v1_tag_at_the_end_of_an_mp3_is_still_a_retag(tmp_path: Path) -> None:
    """The tag that goes *after* the audio, which is the one that broke this.

    An MP3 with no Xing header publishes no duration, so mutagen derives one from
    the file size — and 128 bytes of ID3v1 appended to the end look exactly like
    128 bytes of audio. ``ID3().save(path, v1=2)`` is what mutagen itself does by
    default, as do mp3tag, foobar2000 and EasyTAG, so this is an ordinary tag
    write; before the audio region was measured it classified as ``REPLACED``,
    which re-identified the release and locked it out of every librarian
    operation through the fifth gate.
    """
    path = write_mp3(tmp_path / "a.mp3")
    before = read_stamp(path)

    tags = ID3()
    tags.add(TIT2(encoding=3, text=["Telegraph Road"]))
    tags.save(str(path), v1=2)

    after = read_stamp(path)
    assert before is not None and after is not None
    assert after.content_hash != before.content_hash
    assert after.sample_count == before.sample_count
    assert classify(recorded=before, current=after) is IntegrityState.RETAGGED


def test_an_apev2_tag_at_the_end_of_an_mp3_is_still_a_retag(tmp_path: Path) -> None:
    """The other trailing tag, and it lands *after* the ID3v1 mutagen just wrote.

    Which is why the trailing region is stripped in a loop rather than in the
    documented audio/APEv2/ID3v1 order: accounting for one of the two leaves the
    other counted as audio, and the file reads as replaced.
    """
    path = write_mp3(tmp_path / "a.mp3")
    before = read_stamp(path)

    tags = ID3()
    tags.add(TIT2(encoding=3, text=["Telegraph Road"]))
    tags.save(str(path), v1=2)
    ape = APEv2()
    ape["ARTIST"] = "Dire Straits"
    ape.save(str(path))

    after = read_stamp(path)
    assert before is not None and after is not None
    assert after.content_hash != before.content_hash
    assert after.sample_count == before.sample_count
    assert classify(recorded=before, current=after) is IntegrityState.RETAGGED


def test_a_shorter_mp3_is_still_replaced(tmp_path: Path) -> None:
    """Measuring the audio region must not soften what the count is *for*."""
    path = write_mp3(tmp_path / "a.mp3", frames=40)
    before = read_stamp(path)

    write_mp3(path, frames=39)

    after = read_stamp(path)
    assert before is not None and after is not None
    assert classify(recorded=before, current=after) is IntegrityState.REPLACED


def test_shorter_audio_is_replaced(tmp_path: Path) -> None:
    """Half the samples in the same container is a different recording."""
    path = write_flac(tmp_path / "a.flac", seconds=200)
    before = read_stamp(path)

    write_flac(path, seconds=100)

    after = read_stamp(path)
    assert before is not None and after is not None
    assert classify(recorded=before, current=after) is IntegrityState.REPLACED


def test_truncating_a_file_is_replaced(tmp_path: Path) -> None:
    """A file cut off mid-write measures nothing, and nothing is not agreement."""
    path = write_flac(tmp_path / "a.flac", seconds=200)
    before = read_stamp(path)

    path.write_bytes(path.read_bytes()[:20])

    after = read_stamp(path)
    assert before is not None and after is not None
    assert after.sample_count is None
    assert classify(recorded=before, current=after) is IntegrityState.REPLACED


def test_an_untouched_file_verifies(tmp_path: Path) -> None:
    path = write_flac(tmp_path / "a.flac")
    before = read_stamp(path)

    after = read_stamp(path)

    assert classify(recorded=before, current=after) is IntegrityState.VERIFIED


def test_a_deleted_file_is_missing(tmp_path: Path) -> None:
    path = write_flac(tmp_path / "a.flac")
    before = read_stamp(path)
    path.unlink()

    assert classify(recorded=before, current=read_stamp(path)) is IntegrityState.MISSING


# ---------------------------------------------------------------------------
# Album digests
# ---------------------------------------------------------------------------
def test_album_digest_is_stable_and_hex() -> None:
    hashes = ["aa", "bb", "cc"]

    digest = album_content_digest(hashes)

    assert digest is not None
    assert len(digest) == DIGEST_SIZE * 2
    assert album_content_digest(list(hashes)) == digest


def test_album_digest_is_order_sensitive() -> None:
    """Disc/track order is part of what a release *is*.

    Two albums holding the same recordings in a different running order are
    different releases, so the digest that stands for one must not equal the
    digest that stands for the other.
    """
    assert album_content_digest(["aa", "bb"]) != album_content_digest(["bb", "aa"])


def test_album_digest_does_not_confuse_a_join() -> None:
    """The member separator has to exist, or ``["ab", "c"]`` and ``["a", "bc"]``
    are the same album."""
    assert album_content_digest(["ab", "c"]) != album_content_digest(["a", "bc"])


def test_album_digest_is_none_when_any_member_is_unmeasured() -> None:
    """A partially baselined album has no honest digest.

    Computing one over the members that happen to be stamped produces a value
    that changes the moment the rest are, so the album would compare unequal to
    itself on every pass and be reported as altered forever.
    """
    assert album_content_digest(["aa", None, "cc"]) is None
    assert album_content_digest([None]) is None


def test_album_digest_is_none_for_an_empty_album() -> None:
    assert album_content_digest([]) is None


def test_album_digest_over_real_files(tmp_path: Path) -> None:
    """End to end: change one track and the release digest changes with it."""
    first = write_flac(tmp_path / "01.flac", seconds=200)
    second = write_flac(tmp_path / "02.flac", seconds=180)
    stamps = [read_stamp(first), read_stamp(second)]
    assert all(stamp is not None for stamp in stamps)
    before = album_content_digest([stamp.content_hash for stamp in stamps if stamp])

    write_flac(second, seconds=190)
    after_stamps = [read_stamp(first), read_stamp(second)]
    after = album_content_digest([stamp.content_hash for stamp in after_stamps if stamp])

    assert before is not None and after is not None
    assert before != after
