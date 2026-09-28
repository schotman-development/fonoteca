# 0013 — An album folder is one album; an edition only on proof

**Status:** accepted, 2026-09-23

## Context

Attribution used to name a MusicBrainz *release* — one pressing — for every
folder it could, and split a folder across releases where its files fitted
different ones. Measured on the target library before this changed:

- only 226 of 551 complete releases matched their files within 50 ms on every
  track; the drift gate was on the *mean*, so one track 15 s out passed;
- 60 releases MusicBrainz lists as CD held files no CD could have produced —
  hi-res downloads, and 16/44.1 files whose lengths are not whole CD sectors
  (588 samples);
- 25 album folders were split across releases or left files unfiled;
- 45 releases chosen by hand had a track more than 3 s off.

A digital download filed as the CD it resembles is 99% right and 1% a lie, and
the 1% is invisible: every screen, every tag write and every Subsonic client then
repeats it. The owner's rule for this change is *prevent lies rather than be
perfect*.

## Decision

**The folder is the album; the pressing is claimed only where nothing can
contradict it; and what is checked about order is the files' relative order,
never a track number.**

### The album

An album folder (`AlbumFolder.Of`) is one album — a MusicBrainz release group.
The best-fitting candidate names it, and only if it explains **more than half**
of the folder's files, counting every file including the unidentified ones. Two
different albums explaining the folder equally well is a refusal, as before.
Files in the folder on no edition of that album **stay with the folder**
(`OnNoEdition`) rather than being refused or filed elsewhere: a bonus track from
another record is still in this folder.

A person or an agent can **always name the folder's album**
(`POST /api/catalogue/matching/folders/album`, MCP `set_folder_album`). That
writes `AlbumByPerson`/`AlbumByAgent` on every file in the folder, no pressing,
and clears the lookup stamp so the pass proves the pressing — within that album
only. The pass reads the album from those rows and never moves the folder.

A person's answers name the folder's album only where they are most of the
folder, the same majority the rule is held to. After the re-check every earlier
per-file pick is an `AlbumByPerson` row too, and one file filed under a single
must not carry eleven files of an album with it: a minority pick keeps its own
album, untouched, and the rule decides the rest.

### The pressing

An edition is claimed (`Attributed`, with release and track links) only when
`EditionProof.Seat` seats it:

- its track count is the folder's file count, and every file takes a slot;
- **every** track is within 100 ms of its printed length, and the printed
  lengths are known and not all whole seconds (a whole second is what a guess
  looks like);
- on a medium that may be a CD, no file is lossless audio a CD could not hold.
  Lossy and unprobed files cannot contradict a CD, and a null format may be one.

More than one edition proving is **album only** (`GroupOnly`, with the tie
counted in `EditionAlternatives`), because choosing between them would be a coin
flip written down as a fact. A pseudo-release — a transliteration of another
release, usually printing its lengths — is not a pressing and never proves.

The length the proof uses is the probe's, else fpcalc's, which reports
hundredths of a second. An unprobed file cannot contradict a CD, so an unprobed
folder can prove one; that gap is accepted (77 files here were unprobed).

### The order

`FolderOrder.Check` compares the folder's own order — track tags if every file
has a distinct one, else numbered file names — with every official edition, on
the recordings both share. One edition agreeing corroborates it. Only a *tag*
order that every official edition reverses, from a gather that was not cut off
at its cap, is `Contradicted`: without a proven pressing or a person's album
that is a question (`OrderContradicted`); with either, the album stands and the
order is recorded.
A folder with no order of its own takes the editions' where they agree.

This reads tags, which ADR 0011 says are not evidence. They still are not: a
file's number is compared and shown as *the file's own*, never promoted into the
catalogue as a position. The tags are read in the pass and stored for every file in the folder
(`TagTrackNumber`, `TagDiscNumber`), and any failure to read them — forty files
here make ATL throw — means the names are used instead. A file carrying no
number of its own is given its place in the folder's settled order when tags
are written; one that carries a number keeps it.

### What is written

The album's official editions the gather fetched are kept as catalogue — the
album page's combined track list is read from them — plus the proven pressing,
the best-fitting edition where it is of the chosen album, and any edition a
substituted recording is printed on.
Nothing else. File tags
already written are left alone. The tag write gives an album-only file the
album's facts — title, artist, album, album artist, year and the recording and
release-group MBIDs — its place in the folder's settled order as the track
number, and nothing a pressing owns: no disc, track total or release MBID. The
track number and year are written only where the file carries none of its own,
or carries the one an earlier write filled in, and no track number goes beside
a disc number. The owner chose that over leaving every
download untagged.

## Consequences

- Most hand-picked editions do not survive the 100 ms proof. That is intended:
  the album stays, the pressing becomes "not known".
- A pass now opens files (tags), so it needs the volume mounted. A missing volume
  silently falls back to name order, which is safe and invisible.
- Classical box sets cap the gather, so their order can never be contradicted.
- Files carrying an earlier tag write's track numbers corroborate the edition
  those numbers came from. The journal that could tell them apart
  (`tagging.catalogue.written`) is deleted with the re-check, by decision.
- Orphan `Release` rows from before the re-check stay as stored editions; every
  list filters on files.

The re-check of the whole library and its measured results are recorded below
once run.
