# 0017 — Picard's tags, and tempo and key heard locally

**Status:** accepted, 2026-10-14

## Context

The tag write put the catalogue's titles, numbers, identifiers, credits,
genres, ReplayGain, DR and lyrics into the files. Picard writes more, and the
owner asked for "all tags that are standard" that Fonoteca can answer, plus
two it could not: BPM and the musical key.

Most of the rest were already in the catalogue — sort names, the pressing's
label, catalogue number, barcode, country and status, the album's types and
first year, the billed artists one by one, the work, the fingerprint. Six
needed MusicBrainz asked again: the release track id, the ASIN, the script,
the disc's title, the movement's place in its work, and the work's language.

No source here states a tempo or a key. **Essentia** (AGPL, Python wheels)
measures both from the audio. On 90 songs here its `RhythmExtractor2013`
(multifeature) tempo looked right wherever its confidence was 1 or more and
was a guess below — 35 of the 90 fell below, nearly all classical, slow jazz
ballads and solo improvisation, which Essentia's own guidelines call "very
low confidence". Its `KeyExtractor` named the key exactly on 11 of 16 first
movements whose titles print it, with the `bgate` profile and with
`krumhansl`; `edma` 10, `temperley` 9. About 3–5 seconds a file at one core.

## Decision

**Tempo and key are a second question in the Analyse pass** (the owner's
choice over a pass of their own): `ITempoAndKeyMeasurer`, run through the
venv's own interpreter (`Fonoteca:EssentiaPythonPath`, empty = not asked),
stamped apart from the loudness (`TempoAndKeyMeasuredUtc`), so turning it on
asks only that half of every file. **Answers, never rankings**: every reading
is kept with its confidence (`MediaFile.TempoAndKey`), and the tag is a rule
over it — `BPM` only at a confidence of 1 or more (the owner's choice), whole
as ID3's `TBPM` wants it; `KEY` on every song, in `TKEY`'s notation (`F#m`).
A scan that sees the audio change clears both.

**Every other tag is Picard's name in Picard's place**, as ATL 7.16 writes it,
each measured by writing it alone into a file of each container and reading it
back with ATL, TagLib# and ffprobe (`CatalogueTagFields.Picard`):

- ATL's typed properties where ATL reads Picard's field into one — the sort
  names, `CATALOGNUMBER`, `BPM`, `LANGUAGE` everywhere; `TPUB` for the label
  and `TDOR` for the original year on ID3; `©mvn` and `©mvi` on MP4 — since a
  field written beside the property is a second copy.
- The rest as the container's own frame or atom where ATL knows one (`TSOC`,
  `TMED`, `TCMP`, `TKEY`, `TSST`, `TPE4`, `WOAR`; `soco`, `cpil`, `©wrk`,
  `©mvc`, `shwm`) and otherwise a Vorbis comment, a `TXXX` frame or an MP4
  freeform atom under Picard's name.

**The pressing's facts only with a proven pressing**, as the year and the
totals already were: label, catalogue number, barcode, country, status,
medium format, release track id, ASIN, script and disc title. The album's —
its types, its first year, whether Various Artists bill it — go to a file
held to its album alone too.

**Movements in the iTunes shape**: where a recording's work is a part of a
whole and its place there is known, `WORK` is the whole and `MOVEMENTNAME`,
`MOVEMENT`, `MOVEMENTTOTAL` and `SHOWMOVEMENT` say which part; elsewhere
`WORK` is the recording's own work, as Picard writes it. The movement's name
is its title without the whole's and its numeral (`WorkTags.MovementName`).

**Two enrichment stages ask what the tags need and nothing else reads**: each
work once for its languages and parts (`Works.DescribedUtc`), each release a
file is filed under once for its ASIN, script and track ids
(`Releases.DescribedUtc`, set by the attribution pass on what it writes).
Neither expires: these are not facts MusicBrainz gains over time.

**A fingerprint, a tempo, a key and a loudness range belong to the audio**,
so a file the catalogue has not answered gets them with its ReplayGain.

## Consequences

- The first Analyse after turning Essentia on is about four hours for this
  library at six cores; the two stages are about two hours at MusicBrainz's
  rate.
- ID3's movement frames are `TXXX:MOVEMENTNAME`, `MOVEMENT` and
  `MOVEMENTTOTAL`, not Picard's `MVNM`/`MVIN`: ATL writes those as `TXXX`
  frames of those names whatever it is handed. TagLib-based readers read
  either as the same field.
- An MP4 gets no ASIN (ATL makes a four-letter name an atom of that name) and,
  as Picard writes none there, no `WRITER`, `WEBSITE` or original year.
- `REPLAYGAIN_TRACK_RANGE` and `REPLAYGAIN_REFERENCE_LOUDNESS` are loudgain's,
  in its format (`7.56 dB`, `-18.00 LUFS`); rsgain removes both on a rescan.
- A person's own artist or album artist leaves the catalogue's sort names and
  artist lists for that billing out, rather than contradict it.
- **Not built:** copyright, encoder, mood, rating, grouping, title and album
  sort names, the disc id — nothing here holds an answer for them.
