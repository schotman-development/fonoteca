# 0011 — A provider is an authority, and a download is where it is believed

**Status:** proposed, 2026-09-04

## Context

Four passes decide what a file is by measuring the audio and asking two public
services about it. Every one of them is built on the same premise, which
`CLAUDE.md` states as *the folder's boundary is believed; its name never is* and
`AlbumFolder` restates in its own words. Nothing a file claims about itself is
evidence, because a claim in a tag or a directory name was written by somebody
unknown at an unknown time.

A Qobuz download breaks that premise, and the break is worth writing down rather
than working around.

`QobuzDownloadService` copies bytes and stops, writing nothing to the file, to
the catalogue or to the event log. The album is untraceable to the service it
came from the moment the last byte lands.

**The metadata is discarded a layer lower than that, and this ADR is mostly
about undoing that decision rather than the downloader's.** One `album/get`
carries 59 album fields and 32 per-track fields — measured against
`r8atsj1g7hcvb`, and including UPC, ISRC, composer, label, cover art and a
`performers` string with roles in it (`Various Artists, MainArtist - Johannes
Brahms, Composer - William Steinberg, Conductor - Pittsburgh Symphony
Orchestra, Orchestra`). `QobuzJson.cs` declares 13 of the album's and 9 of the
track's; `QobuzAlbum` and `QobuzTrack` carry 12 and 7 members to the service.
A grep for `upc`, `isrc`, `composer` or `performers` across
`Fonoteca.Providers/Qobuz` and `Fonoteca.Api/Acquisition` returns nothing, and
`QobuzTrack.Performer` is a bare credit string, not roles. So the downloader
never had most of this to discard — the wire shape did.

Measured on the library this was built against, downloading *Essential Brahms,
Volume 1* (Qobuz `r8atsj1g7hcvb`, 50 tracks) produced 50 FLACs of which **ten
carried no tags at all** and forty carried only the `ACOUSTID_ID` the
identification pass wrote afterwards. The passes cannot recover what was
discarded: the release is not in MusicBrainz — a search returns four unrelated
*Essential Brahms* titles — and its ISRCs 404 on `/ws/2/isrc/`. The copy of the
same album that has been in the library since August is the preview of what the
passes make of it: 37 of 50 files unfiled, and the 13 that were filed scattered
across three unrelated Brahms albums. Ten of the fifty are the complete Haydn
Variations, which AcoustID has never heard, in both copies, identically.

Refusing is the correct answer to *that* question. It is the wrong question. The
service that served the bytes said what they were, at the moment it served them.

## Decision

**Qobuz is an authority about the files it delivers, and the download is the one
moment its claim can be believed.** Bytes on disk can be edited afterwards by
anything; the claim is about the bytes that arrived, so it is made where they
arrive and it dies when they change.

**It is an authority about the album, not about the audio.** The distinction
decides which passes are pre-empted:

| claim | authority | at download time |
| --- | --- | --- |
| these bytes are album X, disc D, track N, titled T | Qobuz, first-hand | fact |
| album X is titled A, by B, on label L, UPC U, released Y | Qobuz | fact |
| the performers are Toscanini (conductor), Philharmonia (orchestra) | Qobuz | fact |
| which MusicBrainz recording this is | nobody | unknown |
| what this audio fingerprints to | nobody | unknown, and still worth asking |

So the download answers enrichment and attribution, and leaves identification
open. The fingerprint is about the audio, which Qobuz has said nothing about,
and it is what `FingerprintDuration`, the AcoustID and any future dedupe key are
made of.

**`IdentityDecidedUtc` splits into `AcoustIdDecidedUtc` and
`RecordingDecidedUtc`.** Today one stamp gates two passes —
`IdentificationService` excludes it and so does `EnrichmentService` — so there
is no way to say "the recording is named, now go and measure the audio". Setting
it protects the Qobuz link and costs the fingerprint; leaving it null lets
enrichment overwrite a known title with `NoRecording`.

There is a third option and it is rejected deliberately: stamping
`RecordingLookupUtc` on the downloaded row excludes it from enrichment's
worklist while leaving identification's alone, with no new column at all. It is
a lie. Every lookup stamp here means *the provider was put this question* —
which is why the by-hand filing path writes `AcoustIdCheckedUtc ??= now` rather
than `= now`, so that answering does not "misreport when the provider was last
consulted". MusicBrainz was never asked about a Qobuz download, and a column
saying it was would corrupt the one thing these stamps are for. A settled
recording and a consulted provider are different facts; the split is what lets
the row state the first without claiming the second.

Every existing person-decision writes **both** new stamps, so nothing changes
for them; the migration is a rename plus `RecordingDecidedUtc = IdentityDecidedUtc`.

**A provider-sourced release is keyed on its UPC, in `Releases.Barcode`.** Every
`Mbid` in the graph is nullable and `Tracks` has no MBID column at all — a track
is `(ReleaseId, DiscNumber, Position)` — so the whole of a Qobuz album writes
into the existing schema with no migration. `IX_Releases_Barcode` already exists.
`ReleaseWriter` cannot be reused as it stands, and the signature says so before
the body does: `UpsertAsync` takes a `MusicBrainzRelease` whose `Id` is a
non-nullable `Mbid`. Every lookup inside it agrees — release, group, artist and
recording are each found by `Mbid` — and two of its four memos are keyed
`Dictionary<Mbid, …>`, so a release with no MBID would be minted afresh on every
re-download. It gets a sibling keyed on the barcode.

**Two new outcome values, because a provider is a third authority.**
`EnrichmentOutcome.LinkedByProvider` and
`ReleaseAttributionOutcome.AttributedByProvider`. Not `Linked`/`Attributed` — no
rule cleared a threshold. Not `LinkedByPerson`/`AttributedByPerson` — nobody read
a shortlist. Seven values across the three enums already exist to keep a person
apart from a rule, for the reason stated at `AttributedByPerson`: any report of
how a pass performs would quietly count the albums it never saw.
`AcoustIdOutcome` gets nothing, because identification still runs and writes its
own answer.

**Tags are written by the pass that already writes tags.** `CatalogueTags.For`
omits every `MUSICBRAINZ_*` field whose Mbid is null, and `TagWriteService`'s
worklist is `RecordingId != null && ReleaseId != null && TrackId != null` with no
MBID requirement anywhere. A provider-sourced row therefore produces exactly
`TITLE ARTIST ALBUM ALBUMARTIST TRACKNUMBER TRACKTOTAL DISCNUMBER DISCTOTAL
YEAR` — plus `ACOUSTID_ID` once identification has run, which is the point of
leaving that pass open — and invents no identifiers. Today, unchanged.

The write path is the only code here that can destroy something a rescan cannot
rebuild, and ADR 0002's sequence already lives in it; a second copy in the
downloader would drift in the way nobody notices until a library is already
wrong.

The precondition, before any of that: `QobuzJson.cs`, `QobuzAlbum` and
`QobuzTrack` widen to carry `upc`, `isrc`, `composer` and `performers`. It is
not incidental — the barcode key and the artist graph below are both made of
fields the client does not currently parse — and it is the one part of this work
that touches `Fonoteca.Providers`.

Per track, then, inside `DownloadTrackAsync` while the response is still in hand:

1. bytes → `.part` → verify against `Content-Length` → rename *(exists)*
2. mint or find release, group, recording, track, artists, relationships
3. insert the `MediaFile` row — links, `LinkedByProvider`, `AttributedByProvider`,
   `RecordingDecidedUtc`, `ReleaseDecidedUtc`
4. tags, through `TagWriteService` with `TagWriteScope.ForRelease`

## Why

**The claim has to be made at step 2 and not recovered later.** Re-asking Qobuz
an hour afterwards answers a question about an album; it cannot establish that
the file on disk is the one that came down. Provenance decays the instant the
process that fetched the bytes lets go of them.

**The scan already implements the expiry.** `LibraryScanService` clears
`RecordingId`, `ReleaseId`, `ReleaseGroupId`, `TrackId`, both outcomes and both
decision stamps on any file whose size or mtime moved. Hanging the provider claim
on those same stamps means a tampered file loses it with everything else derived,
with no new mechanism and no new rule to remember.

**Which is also why step 4 is last and why the row records the size and mtime
after it.** A tag write changes the bytes; a row still holding the pre-tag size
reads as modified on the next scan and the scan discards the claim it just made.
That is identification's oldest lesson and `TagWriteService` already carries it —
which is a third reason not to write tags anywhere else.

**The download becomes the scan for the files it wrote.** It knows the path, the
size and the mtime at first hand, and `IX_MediaFiles_Path` is unique, so a later
scan finds the rows present and reconciles rather than re-adding them. This is
what "downloaded, and then straight through to attributed" actually means: not
four passes chained behind the download, but three questions already answered by
the only party in a position to answer them, and the fourth — the fingerprint —
left open for the pass that exists to ask it.

## Consequences

**A download now takes `LibraryWorkGate`**, which it does not today, because step
4 does and because the rows it mints are the kind a pass may hold in an in-flight
page.

**A UPC is not a unique key, unlike an MBID.** `IX_Releases_Barcode` is
deliberately not unique. Two Qobuz editions can share one, and so can a
MusicBrainz release — and that last case is the good one: a lookup that finds the
barcode fills the MBID in on the row Qobuz minted, and the album upgrades from
provider-fact to MusicBrainz-linked without moving a file. It only happens if the
writer looks the barcode up rather than assuming a null MBID means the row is
its own. An album with no UPC falls back to an unkeyed release that a re-download
duplicates, which is the honest failure — inventing a key would be worse.

**`LinkedByProvider` has no backfill path, and `LinkedByPerson` does.** The
by-hand album screen writes no artist graph at the moment it files a file — the
line `CLAUDE.md` and `EnrichmentOutcome.LinkedByPerson` both still carry — but
that is no longer where the story ends: `EnrichmentService` has a *second*
worklist, `PersonFiled`, which picks those files up on the next run, fetches the
recording and promotes them to `Linked` with their credits. Its predicate is
`EnrichmentOutcome == LinkedByPerson && Recording.Mbid != null`, so a
provider-linked recording can never be caught by it. Nothing would ever fill a
Qobuz album's artists in later.

Which is why the download has to write them at the time. Qobuz's `performers`
string carries roles — `Toscanini, Conductor`, `Philharmonia Orchestra,
Orchestra` — which is exactly what `PrimaryCredits` unions and exactly what a
classical library is otherwise missing. Parsed at download, a provider-linked
file browses under `/library/artists` properly and needs no second pass. Not
parsed, it never will.

**Fingerprint contribution stays blocked, and it is where contribution is worth
most.** `Contributable` requires `file.Recording!.Mbid != null` and
`AcoustIdClient.SubmitAsync` sends only `mbid.N`. The ten Haydn Variations tracks
are audio AcoustID has never heard, that Qobuz has just named authoritatively —
the highest-value submission available, structurally excluded. AcoustID's submit
API accepts `track.N` / `artist.N` / `album.N` / `year.N` in place of an MBID.
Separate change, bounded, and this ADR is what makes it worth making.

**Nothing reconciles a provider fact against MusicBrainz later.** When this
compilation is eventually added, nothing goes looking. The barcode key makes the
sweep possible; the sweep is not in scope here.

**`StagedFileName` still strips the colon, and that is a separate defect.**
`Illegal` contains `:`, so *Essential Brahms, Volume 1: 50 Tracks…* became
*Essential Brahms, Volume 1 50 Tracks…* — a different directory from the copy
already held, which `RefuseToMergeEditions` never saw because it only looks
inside the target folder. The library holds that album twice, 100 files, 3.4 GB.
A UPC on the release lets the *catalogue* notice; the filesystem check needs its
own fix.

## When to revisit

**If a second provider is added.** Deezer would arrive with the same shape and a
different identifier, and `Barcode` is the only key here that is not
provider-specific. Two providers disagreeing about one UPC is the case that would
justify the `SourceProvider`/`SourceId` columns rejected above.

**If a provider is ever measured to be wrong.** The entire decision rests on
"came straight from Qobuz, therefore fact". A mislabelled album that a pass would
have caught, filed confidently and off every worklist, is the failure this trades
for — `ReopenedByPerson` is the escape hatch and the thing to watch.
