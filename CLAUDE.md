# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A self-hosted music library manager for 100,000+ track libraries: catalogue and
dedupe, acquisition (Qobuz/Deezer), *arr-style upgrade monitoring, tag editing.

**Status: scaffold, four passes and three browse screens.** The library scan walks
the root and reconciles `MediaFiles` with what is on disk. The **identification
pass** then fingerprints every file that has no AcoustID, looks it up, and writes
the result into the file's tags — so `Fonoteca.Tagging` is real, `IStagedWrite` is
implemented, and there *is* now a code path that modifies audio files. It is
behind `Fonoteca:AllowFileMutation`, which still defaults to `false`; with it off
the pass does everything except the write. **Read `docs/adr/0002` before touching
that path.** The **enrichment pass** turns those identities into a catalogue —
recordings, works, artists — and `/library` browses it. The **attribution pass**
then decides which release each file came from, from the audio alone, and
`/library/releases` browses that.

Still missing: hashing, downloading, and probing as a *pass*. There **is** now a
probe — `IAudioProbe` over `ffprobe` — but the only thing that calls it is
`GET /api/catalogue/matching/files/{id}`, one file at a time, when somebody opens
it. `AudioQuality` therefore fills in gradually rather than being populated by a
run over the library.

## Toolchain

Everything comes from `~/.local/opt` (see `docs/toolchain.md` and the policy in
`~/Repositories/CLAUDE.md`). **Nothing is installed with apt.** Shells that
haven't sourced `~/.local/opt/env.sh` will not find `dotnet`, `node`, `pnpm`,
`ffmpeg` or `fpcalc` — `scripts/dev.sh` sources it, ad-hoc commands must too:

```sh
source ~/.local/opt/env.sh
```

PostgreSQL runs in podman. Testcontainers and `podman compose` both need
`systemctl --user enable --now podman.socket`.

## Commands

```sh
pnpm dev              # postgres (podman) + api :5088 + web :5173 + storybook :6006
pnpm dev --no-storybook
pnpm build            # tokens -> ui -> api-client -> web, topological
pnpm typecheck
pnpm lint             # biome; lint:fix / format to write
pnpm test             # JS side: every story through axe in real Chromium
pnpm api:build        # also regenerates openapi.json at the repo root
pnpm api:test         # xUnit; integration tests start a real PostgreSQL 18
pnpm gen:api          # openapi.json -> packages/api-client/src/schema.d.ts
pnpm gen:api:check    # fail if the committed client has drifted

./scripts/musicbrainz-mirror.sh help   # optional local mirror; see below
```

Single tests:

```sh
dotnet test apps/api/Fonoteca.slnx --filter "FullyQualifiedName~MigrationTests"
pnpm --filter @fonoteca/ui test -- src/Button          # one story file
pnpm --filter @fonoteca/ui test:watch
pnpm --filter @fonoteca/tokens test                    # node --test, no runner dep
```

EF migrations — **must be run from `apps/api`**; the tool manifest is
`apps/api/dotnet-tools.json` and .NET will not find it from the repo root:

```sh
cd apps/api && dotnet tool restore
dotnet ef migrations add <Name> --project src/Fonoteca.Data --startup-project src/Fonoteca.Api
```

## Architecture

### Two toolchains, one contract

`apps/api` (.NET 10) is deliberately **outside** the pnpm workspace. The only
seam is the OpenAPI document:

1. Building `Fonoteca.Api` writes `openapi.json` to the repo root (an MSBuild
   target in `Fonoteca.Api.csproj` renames the generator's output).
2. `pnpm gen:api` turns it into `packages/api-client/src/schema.d.ts`.
3. That file is **committed**; `pnpm gen:api:check` regenerates and fails on a
   diff.

So an API change the frontend hasn't caught up with breaks the build, not the
browser. Change an endpoint's shape → rebuild the API → `pnpm gen:api` → commit
both.

### Backend layering

```
Fonoteca.Api        minimal API, OpenAPI, SignalR, health
Fonoteca.Domain     PURE — entities, matching, quality ranking
Fonoteca.Ingest     walk, hash, probe, fingerprint
Fonoteca.Tagging    read/write, dry-run diff, undo journal
Fonoteca.Providers  qobuz | deezer | musicbrainz | acoustid
Fonoteca.Jobs       IJobQueue abstraction over Hangfire
Fonoteca.Data       EF Core, migrations
```

**`Fonoteca.Domain.csproj` has no `PackageReference` and no
`ProjectReference` — that emptiness is the architecture.** No EF, no
HttpClient, no System.IO. Anything the domain needs from outside is an
interface in `Domain/Abstractions/` that it receives. A package reference
appearing there means something leaked.

Entities are MusicBrainz-shaped (`Work` / `Recording` / `ReleaseGroup` /
`Release` / `Track` / `MediaFile` / `Artist` / `ArtistCredit` /
`Relationship`), not artist→album→track. See ADR 0004; it cannot be
retrofitted. Ids are strongly-typed `readonly record struct` wrappers over
`Guid.CreateVersion7()` — time-ordered so 100k-row inserts stay at the right
edge of the B-tree — mapped through the converters in `Data/IdConverters.cs`.

### The library scan, as a worked example of a feature slice

One capability, spread across the layers the way the next one should be:

| | |
| --- | --- |
| `Domain/Catalogue/AudioFormats.cs` | the rule — which extensions are audio. Pure, string in, bool out |
| `Ingest/FileSystemAudioFileStore.cs` | the adapter — `IAudioFileStore` over a directory tree. Owns the absolute root; everything crossing the boundary is library-relative |
| `Ingest/LibraryScanner.cs` | the walk — streams `ScannedFile(path, size, mtime)`, opens nothing |
| `Api/Library/LibraryScanService.cs` | the reconcile — diffs that stream against `MediaFiles`, in batches |
| `Api/Endpoints/LibraryEndpoints.cs` | `POST /api/library/scan`, `GET /api/library/scan` |

Four sharp edges are already paid for, all of them variations on one theme —
*absence of evidence is not evidence of deletion*:

- **Timestamps are floored to whole microseconds before they are stored.**
  `timestamptz` keeps microseconds, .NET keeps 100ns ticks; skip this and every
  file compares as modified on the next pass, taking every hash in the
  catalogue with it.
- **The walk does not follow directory symlinks.** `Directory.EnumerateFiles`
  does, with unbounded recursion depth, so a link to an ancestor walks
  `loop/loop/loop/…` forever. Symlinked *files* are still catalogued.
- **Unreadable directories are counted, not ignored.** `IgnoreInaccessible`
  turns a permissions error into silence, and the reconciler reads silence as
  "those files were deleted". With the flag off, `ContinueOnError` counts them
  and the scan removes nothing that pass.
- **A scan that sees zero files while the catalogue holds many deletes
  nothing.** An empty root is an unmounted volume far more often than an
  emptied library.

It runs in the foreground of the request on purpose — a walk with no file reads
is seconds, even at 100k. The pass that *does* open files is separate and
asynchronous; see below.

### The identification pass, and the loop it must not create

| | |
| --- | --- |
| `Domain/Abstractions/IAudioFingerprinter.cs` | the seam — path in, Chromaprint out |
| `Domain/Identification/AcoustIdSelection.cs` | the rule — which cluster is safe to write. Pure |
| `Ingest/ProcessRunner.cs` | the first subprocess in the app; ffprobe and ffmpeg follow this |
| `Ingest/FpcalcFingerprinter.cs` | `fpcalc -json -length 120` |
| `Ingest/FileSystemAudioFileStore.cs` | `OpenForReplaceAsync` — temp sibling, atomic swap |
| `Tagging/AcoustIdTagField.cs` | where an AcoustID lives, per container. Measured, not assumed |
| `Tagging/AcoustIdTagWriter.cs` | ADR 0002's write path, for one field |
| `Api/Library/IdentificationService.cs` | the pass: two stages, one gate, one commit per file |
| `Api/Library/LibraryWorkGate.cs` | a scan and a pass must not overlap |

**The sharp edge, and it is sharper than the scan's four.** Writing a tag changes
the file's bytes, and `ApplyChangesAsync` reacts to changed bytes by nulling
every derived column — including the AcoustID that was just written. Naively
implemented the two passes undo each other forever: identify, tag, rescan,
discard, identify. So **a committed write records the file's new size and mtime
in the same transaction as the AcoustID**, through `StoreTime.ToStorePrecision`,
which is why that rule moved into the domain where both callers share one copy of
it. `TaggingAFileDoesNotMakeTheNextScanThinkItChanged` is the guard. Do not
"fix" this by forging the old mtime back onto the file: the file genuinely
changed and the catalogue must agree with the filesystem.

The rest, in the same spirit as the scan's list:

- **The worklist is `AcoustIdCheckedUtc IS NULL`, not `AcoustId IS NULL`.** A
  library is full of bootlegs and field recordings AcoustID has never heard.
  Keyed on the identifier, every one is re-fingerprinted and re-asked about on
  every pass forever, at 340ms each. Recording *that we asked* lets the worklist
  reach empty.
- **`AcoustIdTaggedUtc` is a separate column from it, deliberately.** "We know
  what this is" and "the file says what it is" are different facts, and keeping
  them apart is what makes a run with mutation disabled a complete dry run: the
  run after the flag is flipped writes tags and spends no lookups.
- **The file is committed before the row.** Crash in the gap and the file carries
  a verified tag while the row says pending — the next run reads the tag, adopts
  it and converges. The reverse leaves a row claiming an AcoustID the file does
  not carry, which nothing can detect.
- **One scope and one `SaveChanges` per file, not batched.** It is the entire
  resumability story, and the journal entry only persists because it shares that
  scope — `IEventLog.AppendAsync` does not save itself, by design.
- **Fingerprinting is parallel, lookups are strictly serial.** `RequestGate`
  already allows one AcoustID request per 340ms, so concurrent lookups buy no
  throughput and only pin fingerprints in memory. `ScanConcurrency` governs stage
  A only, and past 2 it buys nothing.
- **fpcalc failures are split by whose fault they are.** A truncated FLAC marks
  one row; a missing binary must stop the pass on the first file. Merged, a PATH
  problem marks 100,000 files unreadable.
- **`AcoustIdTagField` was measured against ATL 7.16, not read off a spec.** ATL
  uppercases Vorbis keys, so the ID3 spelling silently produces `ACOUSTID ID`;
  MP4 takes the bare name and adds `----:com.apple.iTunes:` itself. The tests
  assert through **ffprobe** — a third tool — because the two libraries agreeing
  only proves they agree.
- **The undo journal is keyed by `MediaFileId`, never by path.**
  `DomainEvent.SubjectId` is `varchar(200)` and a library path is up to 4096; a
  box set broke a live run at file 76. Paths also move.

Not on a durable queue, and `Fonoteca.Jobs` is still empty — see ADR 0007, which
records what would reopen that.

### The enrichment pass, and why it is not a pipeline

| | |
| --- | --- |
| `Domain/Catalogue/PrimaryCredits.cs` | the rule — which artists a track is browsable under. Pure |
| `Api/Library/EnrichmentService.cs` | the pass: cluster → recording → rows, one file at a time |
| `Api/Endpoints/CatalogueEndpoints.cs` | `GET /api/catalogue/artists`, `…/artists/{id}` |

A fully identified library still has **zero artists**: identification decides an
AcoustID cluster, writes it into the file's tags and discards the MusicBrainz
recording MBIDs `AcoustIdSelection` looked at on the way past. This pass is the
other half, and its own list of paid-for edges:

- **It opens no files.** Fingerprints are already in the catalogue, so a lookup
  is a database read and an HTTP request. That is what makes re-asking after a
  rule change cost turns at the rate limit rather than hours of fpcalc, and what
  lets the pass run with the library volume unmounted.
- **Sequential on purpose, and the absence of a channel is the design.** Both
  halves are gated network calls — AcoustID at 340ms, MusicBrainz at whatever the
  server earns — so concurrency buys nothing that `RequestGate` would not
  serialise again one layer down. No producer, no consumer, nothing to deadlock.
  Identification's two-stage split exists because *its* halves have different
  costs; copying it here would import the hazard and none of the benefit.
- **Three memoisations, per run.** By cluster (five encodings of one track cost
  one lookup), by recording MBID, and by work MBID — the last is the big one on a
  classical library, where a symphony is four movements across a dozen recordings
  and one work. The dictionaries store nulls too: "we asked and MusicBrainz said
  no" is as worth remembering as an answer.
- **`RecordingLookupUtc IS NULL` is the worklist**, not `RecordingId IS NULL` —
  the same lesson `AcoustIdCheckedUtc` already paid for.
- **A credit line is not the whole story, and on classical it is barely any of
  it.** MusicBrainz bills a Karajan reading of Beethoven's Fifth to *Beethoven*
  and leaves the conductor and the orchestra in relationships, with the composer
  link one hop further out on the work. `PrimaryCredits` unions four sources;
  reading `ArtistCredits` alone files every symphony under a man who died in 1827.
  Individual instrument and vocal performers are deliberately excluded (a jazz
  quintet's sidemen would be most of the artist list) and so are production roles
  — the data is stored either way, so widening it later is a query change.
- **An ensemble is recognised by the artist's type OR by the relation.**
  MusicBrainz links the Berliner Philharmoniker with a plain `performer` relation
  about as often as with `performing orchestra`, and types most ensembles `Group`.
- **Billed credits go to `ArtistCredit`; everything else goes to
  `Relationship`.** `Position` and `JoinPhrase` describe a printed billing line,
  and inserting a conductor into that sequence corrupts it for every consumer.
- **`Relationship.ArtistId` is a typed column beside the generic
  `SourceType`/`SourceId` pair**, because a join from `Artists` cannot be written
  over a bare `Guid`: the strongly-typed ids are value-converted and EF translates
  no member access on them into SQL.
- **`CatalogueEndpoints` holds the browse rule twice, and that is EF's choice.**
  A helper taking the artist as an argument cannot be used inside a projection —
  EF reads it as a closure over the row and fails at runtime with a 500 — and
  expressing it as a union of `(artist, recording)` pairs is refused too
  ("unable to translate set operation after client projection has been applied").
  `TheListAndTheDetailPageAgree` is what stops the two copies drifting.

### The attribution pass, and why its unit is a set

| | |
| --- | --- |
| `Domain/Identification/ReleaseFit.cs` | how well one release explains a set of files. Pure |
| `Domain/Identification/ReleaseAttribution.cs` | the rule — gated rounds, and the three ways a tie resolves. Pure |
| `Api/Library/ReleaseAttributionService.cs` | the pass: seed → component → decision → rows |
| `Api/Endpoints/CatalogueEndpoints.cs` | `GET /api/catalogue/releases`, `…/{id}`, `…/attribution` |

**The folder's boundary is believed; its name never is.** For a long time
neither was, and that was the ask at the time — sampling 60 files with `ffprobe`
found `ACOUSTID_ID` on every one and nothing else, so the directory name and the
audio were the only two claims in existence and one of them is the one not to
believe. The name is still wrong where it matters: a folder named for a 1979
album holds the audio of the 2015 remaster, and one named `(2009)` holds a
release MusicBrainz dates to 2010.

But *which files belong together* is a different claim from *which release they
are*, and refusing both cost more than it bought. Growing a component outwards
through shared candidate releases is exactly how a compilation welds two albums
into one set, after which a box set reprinting both is the honest best answer —
the rule was never wrong, the set handed to it was. `Domain/Catalogue/AlbumFolder.cs`
now cuts at `Artist/Album`, measured: of 8,192 files, 7,084 sit at that depth and
all 1,107 below it are in disc folders, spelled `CD 01`, `Disc 1` **and**
`Digital Media 01` — the last a MusicBrainz *medium format*, which is why the cut
is by depth and not by a pattern that would have to know `HDCD` and `12" Vinyl`
too. 595 folders, median 12 files, largest 204, and the four largest are all
genuinely single releases.

- **A single file cannot name its release, so the unit is a component** — one
  album folder's still-open files, decided and committed together. This is the
  one pass with no per-file unit of work, so resumability is per component rather
  than free. Files in the folder that a person or an earlier run already answered
  stay out: the question has been narrowed, not reopened.
- **`UpgradeScan.AlbumFolder` defers to `AlbumFolder.Of`, and had to.** It was a
  second copy of the same two-segment cut, disagreeing on the root-file case and
  on backslashes. Three screens quietly disagreeing about where an album stops is
  not a bug anyone would ever report as one.
- **`inc=releases` on a recording lookup caps at 25 and does not say so.**
  `Don't Rock the Jukebox` returns 25 where a browse of the same recording
  reports 40. `MusicBrainzRecording.Appearances` is therefore not a candidate
  set; `BrowseReleasesForRecordingAsync` is.
- **`inc=recordings` on a *browse* is worse — it silently drops releases.** The
  same browse returns 40 without it and **15** with it, at every page size, while
  `release-count` goes on claiming 40. Track lists come from `GetReleaseAsync`,
  one release at a time. This forces the two-stage shape and there is no way
  around it.
- **The prune looks like an exact bound and is not one.** "Coverage cannot
  exceed the share of a release's track count the library holds" is true only if
  you already know what the library holds — and `hits` counts the recordings a
  browse has placed *so far*, while the whole point of the expansion is that more
  are still arriving. Two live runs died on this: applied in the opening round it
  discards every album (1/12 for an ordinary one, 1,149 refusals out of 1,247),
  and applied in later rounds it discarded a 25-track live album for sharing only
  5 songs with the compilation that seeded the component. It is now a stated
  heuristic — two shared recordings, or small enough for one to matter — and
  skipped entirely on the opening round.
- **Gates come before size.** The obvious greedy — take the release explaining
  the most files — let a 31-track bootleg *Greatest Hits* (coverage 0.55, drift
  1.99s) claim seventeen files out of four albums before any of them was
  considered. Requiring a fit to be *good* before asking whether it is *big*
  removes it entirely.
- **Duration is the edition discriminator and it is precise enough to be one.**
  `FingerprintDuration` is stored to 10ms; against *per-release* track lengths
  the 2015 remaster of `Off the Wall` matches to 0.00s while three earlier
  pressings sit at 0.76s, on identical track lists.
- **Refusing is a first-class answer.** A file no release explains well comes
  back `NoConfidentFit` rather than filed under whichever anthology scored least
  badly. Anthologies of licensed catalogue are where this lands, and it is
  working when it does — a wrong album is worse than a missing one and far
  harder to notice.
- **A tie resolves three ways, by what the tie costs.** Editions agreeing on
  every position → pick by a stated tie-break and record the count. Editions
  disagreeing about disc or position → keep only the release group, because a
  track number would be an invention. Neither → refuse. The agreement question is
  asked of the whole set, or one rip splits between a release and a group and
  then reports itself incomplete.
- **EF queries the database, not the change tracker.** Three unique-index
  collisions in live runs came from this, all the same shape: a row Added but not
  saved is invisible to the next query, and a component routinely names one thing
  twice — two releases listing a bonus track nobody owns, and two pressings
  sharing a release group, which is *by definition*. Everything the writer mints
  goes through a dictionary.
- **Only chosen releases are persisted, but their whole track list is.** The
  candidate set for one component runs to hundreds; writing them all makes the
  album list a browse of MusicBrainz. Writing the full track list of the ones
  kept is what makes "you are missing track 7" answerable.
- **`held` counts distinct tracks, not files.** Five encodings of one song are
  one track of the album; counting files makes a half-ripped album read complete.
- **Known failure, expected fixed by the folder cut and NOT yet re-measured.**
  Michael Jackson's albums did not attribute correctly: `Off the Wall` — ten
  tracks, all ten held, with a 2015 remaster matching to the millisecond — never
  entered the component's candidate set, so the files landed on whichever
  compilation did. All 31 files were decided in a *single* component
  (`count(DISTINCT ReleaseLookupUtc) = 1`) with no cap logged, and reproducing
  the gather in Python against the same mirror *did* confirm `Off the Wall` among
  the candidates at coverage 1.00 — so the rule would have picked it if it had
  arrived, and the loss was in `GatherAsync`.

  Two things about the folder cut should remove it, and both are arguments rather
  than measurements. The 31 files are several albums that only shared a component
  because the expansion welded them; each folder is now decided alone, where
  `Off the Wall` holds ten of ten and a box set holds ten of seventy-six.
  And candidates are ordered by hits over a *fixed* recording set, so the album
  is at the top by construction rather than by a count still arriving.
  `TwoAlbumsSharingABoxSetAreNotCollapsedIntoIt` reproduces the shape in a
  fixture and fails on the old gather. **Run the pass against the real mirror
  before believing any of that.** Artists with a shallower compilation history
  (Bonamassa, Ella Fitzgerald's albums) already came out right.

### The worklist of refusals

`GET /api/catalogue/matching` reads the three passes' outcome columns back as
questions for a person, and `/library/matching` renders it. **No candidate set is
recorded anywhere**, so what is open and what could answer it are two different
problems. The list shows what is open — as a summary, grouped by refusal and
collapsed, rather than as the seven hundred rows it started as. Three of the
seven refusals also get an answer, because their candidates can be recovered:
the two identification ones from the file's stored fingerprint, and
`NoConfidentFit` by asking MusicBrainz again.

`GET /api/catalogue/matching/recordings/{id}/candidates` is that re-ask.
`MediaFile.Fingerprint` is stored, so the expensive half of identification —
decoding audio — is already paid, and AcoustID can be asked the same question for
one request and no disk access. It is the same reason re-asking a whole library
after a rule change costs turns at the rate limit rather than hours of `fpcalc`.

**It is answered from the catalogue where it can be.** It began as a pure
recovery that persisted nothing, and that was one lookup too expensive to keep:
opening the same question twice cost an AcoustID turn plus a MusicBrainz
recording lookup per candidate — the heaviest request this application makes,
measured at 10.3 seconds cold — so returning to a file cost as much as reaching
it. Two caches now sit under it, both believed for a week
(`CatalogueEndpoints.CacheDuration`):

- **`MediaFile.AcoustIdMatchesJson` is the provider's own answer**, written by
  the identification pass, the enrichment pass, and either endpoint when it finds
  the stored copy stale. It is what the catalogue used to throw away — recording
  the verdict and discarding the evidence is why working out what 951 withheld
  files actually were cost 951 turns at the rate limit.
- **`MediaFile.RecordingCandidatesJson` is the assembled document**, filled the
  first time somebody opens the question — and, since the wait turned out to be
  one somebody pays on every row of a seven-hundred-row worklist, filled ahead of
  that click by `CandidateWarmService`. It is not a pass and takes no gate: a
  `BackgroundService` that puts every open question to *the endpoints themselves*
  every fifteen minutes, in the order the screen lists them, and stops for the
  rest of the sweep the moment a pass takes `LibraryWorkGate`. Measured cold on
  the target library, a recording question is 24s and a large component over two
  minutes; warmed, both are around 10ms. Two things make it safe to repeat
  forever: it assembles nothing itself — a warmer with its own copy of the
  ranking would be free to drift from the document a person reads — and an item
  whose cache is fresh comes back `fromCache` without touching a provider, so a
  warm sweep is one query per kind and a row read per question.
  `Fonoteca:WarmCandidates` turns it off, which is what the integration tests do:
  it would otherwise put its own questions to their stubs out of a thread nothing
  waits for. Three things it must keep doing: it stops for `LibraryScanService`
  as well as for the gate, because the scan is the thing that *clears* these
  columns and has never been on the gate — a sweep's read-modify-write spans a
  full AcoustID turn and up to six MusicBrainz lookups, long enough to land a
  document computed from audio that is gone. It counts a candidate set where
  nothing could be named as a failure and gives up after three: the endpoint
  stores a title-less document on purpose, which is right for a person who can
  see it and press refresh, and unattended is an outage written across the whole
  worklist and believed for a week with AcoustID still answering. And nothing in
  it may throw — an unhandled exception out of a `BackgroundService` stops the
  host by default, so a transient database error would take the API down to save
  a cache nobody is waiting for.
- **The enrichment memo holds AcoustID's answer, not the recording it collapses
  to.** Five encodings of one track still cost one lookup; the change is that all
  five rows keep the evidence rather than only the one that asked, which is what
  keeps the cache from having a gap with no explanation in it.
- **What is cached is answers, never rankings.** `RecordingCandidates` and
  `AcoustIdSelection` collapse the same evidence in opposite directions and both
  have changed since they were written. Clusters, scores and the links between
  them are facts; caching a rule's output would leave the cache quietly wrong the
  day the rule changed, with nothing to notice it.
- **A malformed entry is a cache miss, not a failure.** A cache must not be able
  to break the thing it makes faster; the most a bad row may cost is the request
  it was there to save.
- **The scan clears both, and it has to.** Freshness is checked *before* the
  endpoint checks for a fingerprint, so a stale entry does not go unused — it
  answers. Left behind, opening a replaced file would offer the recordings the
  previous audio matched, and committing one would write that cluster into the
  new bytes.
- **The enrichment memo is keyed on the cluster, so a file with no fingerprint
  must not write to it.** It cannot be asked about without reopening it, which
  the pass does not do — but that is a fact about one file's columns, not about
  the cluster, and recorded against the cluster it makes the next file to share
  one inherit an emptiness nobody asked for, skip its lookup, and cache that
  emptiness for a week.
- **`asOfUtc` and `fromCache` come back with it, and `?refresh=true` skips it.**
  A candidate list is evidence, and evidence with an unstated age is what makes a
  person distrust the screen when a score does not match MusicBrainz today.
- **The stamp is floored to whole microseconds** before it is stored, or the
  answer that filled the cache and the identical answer read back out of it
  report different `asOfUtc` values. The scan's oldest lesson, second place.

- **It is still a recovery rather than a record of what the pass saw.** The
  answer can differ from the one that produced the refusal — AcoustID's data
  moves, and a week is long enough for it to. That is honest rather than awkward:
  the screen is asking what the audio matches *now*, and saying when it asked.
- **`RecordingCandidates` ranks it, and that is not the rule that refused.**
  `AcoustIdSelection` compares *clusters*, and its own remarks record that
  reaching for `RecordingCandidates` there breaks 64 of 200 files. So the raw
  clusters come back beside the collapsed list, unaggregated, and the count of
  clusters per recording is on each row — two near-tied scores naming one
  recording are one answer arriving twice, which is the commonest reason a
  near-tie is not the disagreement it looks like.
- **Six candidates, because each is the heaviest lookup here.**
  `GetRecordingAsync` includes artists, credits, releases, release groups, media,
  ISRCs and two relationship kinds — the request measured at 10.3s cold. The full
  count comes back as `Total`, so a cut set says so.
- **Only `Ambiguous` and `BelowThreshold` are asked about here.** `Unknown` is
  audio AcoustID has never heard and `NoRecording` is a cluster MusicBrainz links
  nothing to, so re-asking spends a turn to reconfirm an emptiness the catalogue
  already holds. `NoConfidentFit` has its own endpoint below, because its unit is
  a component rather than a file.

`POST /api/catalogue/matching/recordings/{id}/decision` is the commit, and it is
the only path in the application that writes an audio file on a person's say-so.

- **What crosses the boundary is a recording, not a cluster.** The candidate list
  names recordings; the thing written into the file's bytes is an AcoustID. The
  endpoint re-asks AcoustID and picks the highest-scoring cluster naming the
  chosen recording, rather than taking an identifier from the request body — the
  one irreversible write in the system should not be authorised by a form post.
  It costs a second turn at the rate limit on a click somebody deliberately made.
  `TheClusterWrittenIsTheOneThatNamesTheChosenRecording` stubs the rival cluster
  *higher*, so anything that ranks by score alone fails.
- **The recording is linked even when no cluster survives.** AcoustID's data
  moves between the question and the answer. The link is a fact about MusicBrainz
  and stands on its own; what is lost is the tag, and that comes back as
  `tag: NotAttempted` with the reason rather than as a failure.
- **`answer` is a named field, not an inference from a missing MBID.** An empty
  body would otherwise deserialise into a person rejecting every candidate — a
  decision that then outranks every pass and can only be undone by hand.
- **Two outcomes a pass can never write.** `IdentifiedByPerson` is not
  `Identified`: one means a rule cleared a score and a margin and the other means
  it did not and somebody decided anyway, and folding them would make any report
  of how identification performs quietly count the files it failed on.
  `RejectedByPerson` is not `Unknown`: AcoustID answered and offered recordings,
  and somebody who listened rejected them — a stronger claim than the provider is
  in a position to make.
- **`MediaFile.IdentityDecidedUtc` is the guard, and it is why there is a
  column rather than just an enum value.** Clearing `AcoustIdCheckedUtc` by hand
  is the documented way to re-ask a library after a rule change, and that same
  UPDATE would hand every answered file back to the rule that could not answer
  it. Both passes' worklists and both partial indexes exclude it.
- **It takes `LibraryWorkGate` rather than reading it — which excludes the three
  passes, and not a scan.** Taking rather than checking is the easy half: the two
  lookups and the tag write all come after the check, so `ActiveKind` alone only
  narrows the window. The half worth writing down is what the gate is: **the scan
  has never been on it.** `LibraryScanService` guards itself with a private
  interlocked flag and `POST /api/library/scan` takes no lease, so a scan
  arriving mid-decision runs anyway and clears every derived column it decides a
  file changed. The gate's own remarks say "a scan and an identification pass
  must not overlap"; that is the intent, not the wiring, and it predates this
  work. What the lease does buy is real — all three passes take it, and any of
  them may hold the file in an in-flight page. It is held across up to two
  MusicBrainz requests at a 90-second timeout each, so against a degraded mirror
  one click can refuse every pass for minutes.
- **A scan that sees the bytes change clears the decision and the evidence with
  everything else.** Both are claims about audio that is no longer there. The
  stamp especially: the reset sets `AcoustIdOutcome` to `NotAttempted`, which the
  worklist deliberately does not count as a question, and both passes exclude
  `IdentityDecidedUtc` — so a file that kept it through a replacement would be
  invisible to every pass and to the screen at once, with nothing able to reach
  it again.
- **The decision is journalled separately from the tag write.** The undo entry
  records what the tags were before a byte changed; the decision entry records
  that a person overrode a rule. A rejection writes only the second, because
  nothing was opened — without it a refusal would leave no trace at all.
- **`Fonoteca:AllowFileMutation` off is the ordinary path, not an error.** The
  catalogue is decided, the cluster is stored, `AcoustIdTaggedUtc` stays null —
  which is exactly what `IX_MediaFiles_AcoustIdUntagged` exists to find — and the
  answer comes back `tag: Refused`. `tagWrite.ts` is the one place that becomes
  English, and it is the only place that knows this is not a failure.

`GET /api/catalogue/matching/files/{id}` is what a person reads while deciding.
The worklist and the candidate set are both about *music*; this is about the
file, and without it the screen was asking somebody to pick between six
recordings on the strength of a filename.

- **Half catalogue, half bytes, and the split is what keeps the worklist a
  query.** Path, size, the length `fpcalc` measured and each pass's outcome are
  row reads, so they also ride along on `GET /api/catalogue/matching` — a
  seven-hundred-row worklist still opens no files. Codec, bitrate, sample rate,
  bit depth, channels and the file's own tags need the bytes, so they are read
  here, one file at a time, on the file somebody actually opened.
- **This is the first thing that writes `MediaFile.Quality`.** The column has
  been in the schema since the first migration with nothing populating it, and a
  clean reading is written back — so the second visit is free, and so is the
  worklist row behind it. That second part is load-bearing rather than an
  optimisation: `GET /api/catalogue/matching` reads
  `FingerprintDuration ?? Quality.Duration`, and **a third of the target
  library's file-level questions have no fingerprint duration at all**, because
  their AcoustID was adopted from a tag they already carried and `fpcalc` never
  ran. Without the fallback those rows print a size and a container and no length
  forever. It is only ever filled in, never blanked: a failed read is a fact
  about the mount, and clearing the columns on it would make the numbers flicker
  with the volume rather than with the file.
- **A decoder measures the audio, not a tag library — `IAudioProbe` /
  `Ingest/FfprobeAudioProbe`.** This was TagLib# first, and it was wrong twice
  over on real files: a VBR MP3 with no Xing header read back at **64 kbps and
  5:35** where the audio is 128 kbps and 2:58 (the first frame's bitrate, and a
  duration extrapolated from it as MPEG-2), and a FLAC truncated to a quarter of
  its bytes read back at its original duration with the bitrate computed against
  what remained — **3 kbps, typed lossless CD**. Neither library is a decoder;
  they answer from headers they parse in passing. There are 64 MP3s on the target
  library's worklist, so this was not a corner case.
- **`-count_frames`, and what it buys is narrower than it sounds.** The numbers
  are still the container's declarations — codec, rate, depth, duration, bitrate
  — and a FLAC truncated *after* its metadata blocks goes on declaring its
  original length and exits **zero**. What reading the frames buys is that ffmpeg
  objects to them, and that sentence is the only thing distinguishing a damaged
  file from a short one. Costs 3.9s on the library's largest FLAC (475 MB)
  against 0.03s for a header read, which is why the timeout is 60s and why this
  is a request somebody clicked rather than anything a pass does. Whether the
  bytes are intact remains `IntegrityState`'s question and still needs its own
  pass.
- **Only a clean decode is written to the catalogue.** At `-v error` a healthy
  stream says nothing; anything on stderr is the decoder objecting to these bytes
  while it read them, and it exits *zero* having done so. A complaint means show
  the reading and do not remember it: measured across sixty real library files
  exactly one complains, and that one has a defect. `AudioQuality` decides which
  duplicate to keep, and a number the decoder objected to has no business being
  an input to that.
- **Losslessness comes from the codec name, never the extension.** An `.m4a` is
  ALAC about as often as it is AAC. `wavpack` is deliberately *not* in the
  lossless set — it has a lossy mode and nothing in the header says which — so it
  under-claims rather than promising a bit-exact copy that may not be one.
- **TagLib# reads the tags, which is the reverse of the write path**, because it
  resolves one fact across containers through named accessors — `ALBUMARTIST` in
  a Vorbis comment, `TPE2` in ID3v2, `aART` in MP4 — and because it is the
  library that does not fall over on the forty ID3-prefixed FLACs, which are
  disproportionately the files a person ends up looking at here. ATL is opened
  third and optionally, for `AdditionalFields`.
- **It cannot fail on the file** — and the `catch` is on `Exception`, not on the
  probe's own type. A path resolving outside the library root, a container
  declaring an absurd duration and output the deserialiser cannot map are none of
  them `AudioProbeFailedException`, and every one would 500 the single endpoint
  documented as unable to fail on a file, for exactly the class of broken file
  this screen exists for.
- **It is a GET that starts a subprocess and writes a row, and takes no lease for
  either.** Median 0.28s on this library, 2.3s on the largest file on the
  worklist, no cap on concurrent decodes. The write needs no `LibraryWorkGate`:
  no pass writes those columns, and a scan that sees the bytes change clears them
  with everything else derived — so the worst a race does is store a measurement
  of audio that has just been replaced, which the scan then removes. Taking the
  gate would refuse the screen for the length of any running pass, which is the
  wrong trade for a read.
- **`MUSICBRAINZ_TRACKID` in a file outranks every score on the screen, and
  nothing was reading it.** Identification asks AcoustID what the *audio* is; it
  never asks the file what it claims to be. **The note above that this library is
  tag-stripped is wrong** — it was measured during the attribution work as
  "`ACOUSTID_ID` on every one and nothing else at all", and re-measuring it
  through this endpoint finds a full Picard tag set on every sampled file the
  passes refused, MusicBrainz recording, release and release-group ids included.
  Whatever that sample was, it was not this library. Nothing has been changed on
  the strength of it — attribution still decides from the audio — but a tag-based
  fallback now has evidence behind it that it did not have.

**The candidate rows carry what the lookup had already paid for.**
`GetRecordingAsync` asks for artists, credits, releases, release groups, media,
ISRCs and two kinds of relationship — the 10.3-second request — and the row built
from it printed a title, a credit line, a length and three numbers. Everything
added is from that same response and costs nothing:

- **Drift**, signed, against the file's measured length. The edition
  discriminator attribution already ranks pressings on; a person should not have
  to subtract two timestamps in their head.
- **The performers from the relationships**, because MusicBrainz bills a Karajan
  reading of Beethoven's Fifth to *Beethoven* — so on classical catalogue every
  candidate reads identically until the conductor and the orchestra appear.
  `PrimaryCredits`'s narrowing is deliberately not applied here: that rule decides
  what a track is browsable under, and this is evidence, where an engineer is
  worth a line.
- **The releases it appears on**, earliest first on the parts MusicBrainz stated,
  with the first release date beside the title — what separates an original from
  the compilation that reprinted it when both rows say the same thing. Capped for
  reading, with the count beside it, and the count itself is capped at 25 by WS/2
  without saying so.
- **The ISRCs and the work.**

- **A stored document written before a field existed is a cache miss.**
  Deserialising last week's JSON into today's record succeeds and leaves the new
  collections null, which serialise back as `null` where the generated schema
  promises an array — so the cache that makes the screen fast would be the thing
  that broke it, and only for the files somebody had already opened. `Complete`
  checks the shape rather than a version number, for the same reason the
  malformed-JSON case beside it does: the most a stale entry may cost is the
  request it was there to save.

`GET /api/catalogue/matching/components/{stamp}/candidates` and
`POST …/decision` are the album-shaped half, which for a while the screen said
could not exist. The stated reason was that recovering the candidates means a
MusicBrainz browse per recording in the component, which is a pass rather than a
request — and that is true of *forming* a component and not of re-asking about
one that already exists.

- **The gather is the expensive half, and it is already paid for.** The pass
  browses every recording in the folder and fetches the track list of each
  release worth one, bounded by the folder rather than by a cap. None of it
  is needed on a re-ask: the component's members are written down, in the
  `ReleaseLookupUtc` they share. What is left is one browse per distinct
  recording and one lookup per release offered — bounded at 30 and 8, and a wait
  rather than a job. Tens of seconds against the public server, a few against a
  mirror.
- **The stamp is a string on the wire, and that is not cosmetic.**
  `DateTimeOffset.UtcTicks` is around 6.4 × 10^17 where JavaScript is exact to
  9 × 10^15. Routed as a `long` it arrives in a browser rounded to the nearest
  few hundred ticks, and every request asks about a component that does not
  exist. `CatalogueEndpoints.Component` is the one place it becomes a number.
- **The pass writes the answer down, and that is what makes the screen free.**
  Every browse and every track list the endpoint would gather, the attribution
  pass has already fetched by the time it refuses a component — and then threw
  away. `ReleaseCandidateSet` is that not happening twice: one row per open album
  question, keyed on the component's stamp, holding the assembled document. On an
  ordinary library opening a refused album costs **zero** provider requests, and
  `ThePassStoresTheCandidatesItWouldOtherwiseDiscard` asserts exactly that by
  counting calls. The live gather stays as the fallback — a component decided
  before this existed, one whose file set has moved, and `?refresh=true`.
- **Its own table, because its key is not a file.** A component's identity is the
  one `ReleaseLookupUtc` its files share, and nothing else has a row for that;
  stamping the same document onto every file of a 59-file component would store
  it 59 times. Written only where somebody may ask — a component the pass filed
  confidently is not a question — so the table is the size of the worklist, 117
  rows on the target library.
- **The pass's component stamp is floored now, and it had to be.** It is the
  primary key of that table and the id the worklist prints, and PostgreSQL keeps
  microseconds where .NET keeps 100ns ticks: an unfloored stamp is a key that
  never matches what comes back out of the column.
- **`Files` is the staleness check, not the timestamp.** A component is a *set*
  and the set moves under the document: a scan clears the stamp on a file whose
  bytes changed, a person answering part of a component takes files out of it.
  Every coverage figure was computed against the set that existed when it was
  written, so a different count means the numbers describe a component that no
  longer exists — a miss, never a failure. `AStoredAnswerForADifferentSetOf`
  `FilesIsAMiss`.
- **`asOfUtc` and `fromCache` come back with it, and the screen says so.** The
  ordinary answer is the set the *rule* was looking at when it gave up, which is
  the right thing to show and exactly the thing that has to state its age.
  `?refresh=true` is a button, not a default, because the alternative costs two
  minutes.
- **The shortlist is ranked by the harmonic mean of two shares, and each one
  alone was measured wrong.** By raw hits — how much of the component a release
  holds — a 59-file component came back as eight compilations and no album, led
  by a 1,197-track radio anthology holding 21 files. By estimated coverage — how
  much of the release the component holds — the same component came back as
  eight one-track singles, each scoring 1.00 by arithmetic. The mean of the two
  is near zero unless both are decent, which is the shape of "this looks like
  the album these came from". `CatalogueEndpoints.Support`.
- **The list a person reads is ordered by files explained, then coverage**, and
  that pair is `ReleaseFit`'s own. Coverage first put two singles above the album
  explaining all twelve files on a real component; files first would put a box
  set above the album it reprints, which is the documented failure. Both, in that
  order, get both right.
- **Both cuts are reported.** `browsed` against `recordings` says whether every
  recording was asked about, and `total` against the list length says how much of
  the tail was never looked up. Either can be why the album a person is looking
  for is not there, and a short list that looks complete is the failure
  `MusicBrainzRecording.Appearances` already paid for.
- **Nothing on disk is touched.** An album is a catalogue fact — no tag write, no
  `Fonoteca:AllowFileMutation`, no undo journal, one decision entry in the event
  log keyed on the component rather than on thirty-one files.
- **Files the chosen release does not list stay open.** A component is a set that
  shared a *candidate set*, which is not a promise that every file in it came
  from one album. Stamping the remainder with a release that does not name them
  would be the invention the pass exists to avoid, so they keep their refusal and
  the response says how many. `FilesTheChosenAlbumDoesNotListStayOnTheWorklist`.
- **A release that explains nothing is refused rather than written.** Without
  that check the writer mints the release, its group and its whole track list,
  links nothing to any of it and reports a decision — a catalogue growing albums
  nobody owns, one bad click at a time.
- **`ReleaseWriter.ApplyTracksAsync` replaces a track list slot by slot, not row
  by row, and the difference is a silent data loss.** `MediaFiles.TrackId` is
  `ON DELETE SET NULL`, so deleting the rows and minting new ids strips the
  position off every file already filed under that release — release and group
  intact, track link gone, nothing logged. The pass already reached it whenever
  two components chose one release; a person answering two album questions with
  the same album reaches it in one click, on the endpoint whose selling point is
  that the whole track list is written. A slot the release still prints keeps its
  row and its id; a slot re-pointed at a different recording is replaced, because
  `Track.RecordingId` is init-only for the good reason that the pair is the row's
  identity. `DecidingASecondComponentKeepsTheFirstComponentsTrackLinks`.
- **Both halves measure a file the same way.** The candidate screen scores with
  `FingerprintDuration ?? Quality.Duration` and the commit used to score with the
  fingerprint alone — and a third of this worklist has no fingerprint duration.
  No file changed hands, since `ReleaseFit` matches on recording MBID, but a
  release listing one recording twice could seat a file on a different slot than
  the one the person read.
- **`MediaFile.ReleaseDecidedUtc` is the guard**, and it is `IdentityDecidedUtc`'s
  counterpart down to the reasoning. Clearing `ReleaseLookupUtc` by hand is the
  documented way to re-attribute after a rule change; without a separate column
  that one UPDATE hands every answered component back to the rule that could not
  answer it. The pass's worklist and `IX_MediaFiles_ReleasePending` both exclude
  it, and a scan that sees the bytes change clears it with everything else
  derived.
- **A mixed component is taken apart one album at a time, and that falls out of
  the design rather than being built.** The 59-file example above is AC/DC,
  Aretha Franklin and Motown glued together by a compilation, and no album
  explains it. Answering with the one that explains two files leaves the other
  fifty-seven on the worklist under the same stamp, so re-opening asks a smaller
  question. Those figures were measured when a component was a discovered set;
  a component is now a folder, so the 59-file example cannot form at all and the
  sizes are the folder sizes above.
- **`AttributedByPerson` is not `Attributed`, and `NoReleaseByPerson` is not
  `NoConfidentFit`.** The same distinction identification already keeps: one
  means a fit cleared the gates and the other means it did not and somebody
  decided anyway. Folded together, any report of how attribution performs would
  quietly count the components it failed on.
- **`NoCandidate` is still not offered.** MusicBrainz holds no release with the
  recording on it at all, so the browse that would recover the candidates is the
  one already known to come back empty.

- **The unit differs by kind.** Identification and enrichment refuse one file at
  a time; attribution refuses a *component*, and the component is recovered from
  `ReleaseLookupUtc` — the pass reads the clock once per component and stamps
  that one value on every file it decides, so the timestamp is the component's
  identity written down. It is the same query the live database is asked by hand.
- **Three refusals are deliberately not questions.** `LookupFailed` is transient
  and the file stays on the pass's own worklist, `NotAttempted` is a queue
  position, and `Unfingerprintable` is a question about the file rather than
  about the music — its follow-up is an integrity check, and answering a match
  cannot clear it.
- **The reason is the first pass that refused.** A file AcoustID could not place
  is left `NotAttempted` by enrichment, which never sees it; reading the later
  silence would report a consequence and send a person to MusicBrainz to look for
  something nobody asked about.
- **Counted by reason, not merely counted.** 454 of the 697 open files in the
  target library are one fact — AcoustID knows the audio, MusicBrainz links no
  recording — so the counts come back grouped and the screen says it once. The
  attribution reasons sort first regardless of size, or the album-shaped question
  a person can actually finish sits under four hundred rows of the same sentence.
- **Reason names are flat and that is safe by construction.** Three enums feed
  this, and the two names that collide between them — `LookupFailed`,
  `NotAttempted` — are exactly the two never returned. `openQuestions.ts` is the
  one place they become English, delegating the attribution three to `CERTAINTY`.

### Matching an album by hand, when nothing about the files names one

| | |
| --- | --- |
| `Domain/Abstractions/IMusicBrainzCatalogue.cs` | `SearchReleasesAsync` — the one text search in the application |
| `Api/Endpoints/CatalogueEndpoints.AlbumMatching.cs` | search, track list, and the commit |
| `web/src/pages/MatchingPage.tsx` | the worklist, grouped by album folder |
| `web/src/pages/ReleaseMatchDialog.tsx` | search → choose → check the pairing → file |
| `web/src/pages/seating.ts` | the pure half — ids, drift, folder cut. `node --test` |

**Every other chooser on the matching screen recovers a candidate set. This one
has none to recover.** Measured on the target library, **all 696 open
file-level questions have a null `RecordingId`** — that is precisely why they
are open: AcoustID has never heard the audio, or has heard it and links it to no
recording. So `ReleaseFit` cannot seat these files (it matches on the recording
MBID the file holds), and neither can anything else keyed on an identifier.

The only two claims in existence about them are the folder they sit in and the
order they sit in it, and the folder is the claim this project makes a point of
not believing. The difference here is who is doing the believing: a pass reading
a folder name is a guess with nobody watching; a person reading it, searching
MusicBrainz and approving a pairing they can see is the only evidence available.

- **The worklist is grouped by album folder, and that was the whole complaint.**
  Ordered by path and split by refusal, one album appears as two runs of rows in
  two sections — the 112-file *Swan Lake* folder is 96 `NoRecording` and 16
  `Unknown`. `ALBUM_FOLDER_DEPTH` cuts at `Artist/Album`, so `CD1` and `CD2`
  collapse onto one question. 696 rows become 113 folders. The refusal survives
  as a badge on the row; it stopped being the heading.
- **A folder on this screen is rarely a whole album, and the worklist cannot say
  so.** It lists *open questions*, so a folder the passes mostly placed arrives
  here as a one-file album: Fleetwood Mac's `Rumours` is 11 files of which 10 are
  filed, `David Gilmour/Live At Pompeii` is 21 of which 20 are, `B.B. King/Live
  (2008)` is 12 of which 9 are. All five leftovers are `Ambiguous` — one AcoustID
  cluster legitimately linked to several recordings, the inverse of the case
  `AcoustIdSelection` was taught to see through, and not a tie any rule can break.
  Seated by raw index against the release's printed order, Pompeii's one leftover
  goes on **track 1**, which is wrong in the worst way available: the number is
  plausible, the drift is the only thing contradicting it, and the file it
  displaces is not on the screen to notice. So `GET …/releases/{id}/slots` takes
  an optional `folder` and returns `heldBy` per slot — the folder's *other* files,
  the answered ones, named on the positions they already sit on.
  `defaultSeating` then seats onto the gaps, which is exactly index order when
  nothing is filed and one click when one file has one hole. Held options stay in
  the `<select>`, disabled: removing them would renumber the album, and "14 is the
  only gap" is only legible against the tracks either side of it.
- **The held lookup is constrained to the same release, and that is the
  load-bearing half.** Track 2 of one pressing is not track 2 of another, so a
  sibling's position is only a fact about the release it was filed under.
  Reported across editions it greys out a slot on the strength of a number that
  means something else — silently, since the file making the claim is not on the
  screen. Where the editions differ the query finds nothing and the dialog
  behaves as it did before, which is the right way for this to fail. The prefix
  carries a trailing slash, or `Artist/Album` also matches `Artist/Album Live`.
  The wildcard question was *measured*, not assumed: EF Core's `StartsWith` over
  a parameter is safe, checked by asking for `David Gilmour/Live%` and for the
  same folder with `_` where the brackets are, and getting nothing both times.
  `_` is not exotic in this library — one Pompeii file is `Time _ Breathe`.
- **The seating is proposed by the client and committed verbatim.** There is no
  matching rule in the endpoint at all — one would be a fourth pass whose
  worklist is exactly the files the other three refused. The default pairing is
  file order against printed order, which is right on a complete rip and wrong
  the moment a track is missing, so every pair is on screen with both lengths
  and the drift between them and every one is changeable through a native
  `<select>`. Re-seating a taken position **swaps** the two files, which keeps
  "one file per position" true without an error state.
  `TheSeatingCommittedIsTheOneThatWasSentAndNotFileOrder` is the guard.
- **All three outcomes are written, not just attribution.** The worklist is
  `UnidentifiedOutcomes OR UnlinkedOutcomes`, so a file filed under an album with
  its identification refusal intact stays on the worklist forever and the screen
  appears to do nothing. Hence `EnrichmentOutcome.LinkedByPerson`, which is *not*
  `Linked`: that value promises "with its artists", and these files get the
  recording's identity off the release's track list and **no artist graph at
  all** — one release lookup covers thirty files where enriching them properly is
  thirty recording lookups at the rate limit. They browse under
  `/library/releases` and not yet under `/library/artists`.
- **A position the release does not print is refused before anything is
  written.** `TrackIdAt` answers null for a slot that does not exist, so the file
  would be filed under the album with no position — which reads on every later
  screen as a track MusicBrainz has since removed rather than as a number
  somebody invented.
- **`ReleaseWriter` gained `RecordingIdAt` beside `TrackIdAt`.** The pass never
  needed it: it matches on the recording MBID the file already holds, so it knows
  the recording *before* it finds the slot. This reads the identity off the slot,
  and those `Recording` rows do not exist in the database until `SaveChanges` —
  a query would miss every one.
- **A file that is no longer an open question is skipped, not rewritten.** This
  endpoint answers refusals; overruling a decision is a different act. The count
  comes back so two open tabs are visible rather than merely lucky.
- **Nothing on disk is touched.** An album is a catalogue fact: no tag write, no
  `Fonoteca:AllowFileMutation`, no undo journal — one decision entry recording
  every seat, because unlike every other decision here the pairing is not
  reproducible from anything the catalogue holds.
- **A worklist id is not a media file id, and getting that wrong is invisible.**
  `OpenQuestion.Id` is `recording:{guid}`; the request field is a bare `Guid`.
  Sending the whole thing failed to deserialise **before the handler ran**, so
  none of the endpoint's own validation was reached, the response was
  `text/plain` rather than a problem document, and the screen showed
  `400 Bad Request` with nothing in it to read. The integration tests could not
  have caught it — they POST media file ids directly and never meet the
  worklist's format. So the id unwrapping, the drift arithmetic and the folder
  cut live in `seating.ts`, which imports no React and no stylesheet and is
  therefore runnable under `node --test` (`apps/web` now has a `test` script, and
  `pnpm -r test` picks it up). `pairs carry the bare guid, never the recording:
  prefix` is the regression.
- **Search needs a Solr index, which a mirror has not got.** See the mirror
  section. Pasting an MBID or a release URL into the box is a lookup and works
  either way; `CatalogueEndpoints.AlbumMatching` detects one with a regex before
  it searches, because MusicBrainz indexes titles and not identifiers.

### Writing it all back, which is the only step nobody may automate

| | |
| --- | --- |
| `Domain/Catalogue/CatalogueTags.cs` | the rule — which tags a catalogue row implies, under Picard's names. Pure |
| `Tagging/CatalogueTagFields.cs` | how each container spells them. Measured, not read off a spec |
| `Tagging/TagWriter.cs` | ADR 0002's sequence, for any number of fields |
| `Tagging/AcoustIdTagWriter.cs` | the one field the identification pass writes, named. A wrapper now |
| `Api/Library/TagWriteService.cs` | the pass: one scope, three worklists |
| `Api/Endpoints/CatalogueEndpoints.Tagging.cs` | `POST …/releases/{id}/tags`, `…/artists/{id}/tags` |
| `Api/Endpoints/LibraryEndpoints.cs` | `POST /api/library/tags`, and its status and cancel |

**Everything above this line writes to PostgreSQL.** Scan, identify, enrich and
attribute between them decide what each file is, who made it and which album it
came from — and copy the library to another machine, or open it in any other
player, and none of that exists. This pass is what makes those answers portable,
and it is the only one that rewrites the audio rather than a row.

- **Three buttons and no fourth route.** Nothing chains into it, no timer reaches
  it, `Fonoteca.Jobs` does not know about it, and `IdentifyAfterScan` has no
  counterpart here. `Fonoteca:AllowFileMutation` is the second lock and is still
  the same one identification uses: off, the run reads every file, computes the
  whole diff, journals it and changes not one byte — which makes a disabled run
  a complete dry run rather than a no-op, and makes "Refused" the *ordinary*
  answer rather than an error.
- **One pass, three scopes, rather than three mechanisms.** An album is a dozen
  files and an artist a few hundred, so both look like they could be a request —
  but "quick" is a property of the library rather than of the endpoint, and the
  library-wide button is a two-hour job. All three take `LibraryWorkGate`, report
  on the same hub channel and are watched through one `GET /api/library/tags`.
  The artist scope is `CatalogueEndpoints.RecordingsOfAsync`, the same rule the
  artist page browses by, so what gets written is exactly what that page lists.
- **The sharp edge is identification's, at a hundred times the scale.** A tag
  write changes the bytes; a row still holding the old size and mtime reads as
  modified on the next scan, and the scan discards every derived column on it.
  Reached one file at a time that costs one re-identification; reached over a
  library it deletes the catalogue with the feature meant to preserve it. The
  new size and mtime go onto the row in the same `SaveChanges`.
  `WritingTagsDoesNotMakeTheNextScanThinkTheFileChanged`.
- **There is no `TagsWrittenUtc` and no migration, because the diff is the
  worklist.** A file that already says what the catalogue says comes back
  `NothingToDo` having been read and never opened for writing, so the pass is
  idempotent by construction and a second run costs a tag read per file. This is
  the one place the `AcoustIdCheckedUtc` lesson does *not* apply: that column
  exists because asking AcoustID again costs a turn at a rate limit, and reading
  a file's own tags costs nothing anyone is waiting for.
- **`AcoustIdTagWriter` is a wrapper over `TagWriter` now, and generalising
  rather than copying was the point.** The write path is the only code here that
  can destroy something a rescan cannot rebuild; two copies of it would drift in
  exactly the way nobody notices until a library is already wrong. What stayed
  behind is what is specific: the diff against the *normalised* AcoustID, so the
  227 files Picard tagged in upper case are recognised rather than rewritten, and
  its own `tagging.acoustid.*` event types.
- **The journal payload is a list of changes now**, and old
  `tagging.acoustid.written` rows carry the previous single-field shape. Nothing
  reads either programmatically, and `Previous` is still nullable for the reason
  it always was: null means the field was absent, so reversing means removing it.
- **`SaveChanges` runs whether or not anything was committed.**
  `IEventLog.AppendAsync` does not save itself — that is what makes one scope and
  one save per file the resumability story — so returning early on a refusal
  discards the journal for exactly the two cases with nothing else to show for
  themselves: a dry run, and a write abandoned at verification.
- **The verification exclusion is new and load-bearing.**
  `TagSnapshot.FieldsLostIn` reports everything that moved, which on a
  multi-field write is mostly the point, so the intended fields come out of it —
  and `DATE` with them, because ATL derives it from the same value as `YEAR`.
  Without that pairing every write that corrects a year fails verification and
  rolls itself back. The same exclusion fixes a latent bug on the AcoustID path,
  where replacing an existing *different* AcoustID always failed.
- **The field names were measured against ATL 7.16 with ffprobe and TagLib#**,
  the way `AcoustIdTagField`'s were, and the answer is the same two spellings:
  `MUSICBRAINZ_TRACKID` for Vorbis and APEv2, `MusicBrainz Track Id` for ID3 and
  MP4. Hand a FLAC the title-case spelling and ATL writes a comment called
  `MUSICBRAINZ TRACK ID` — plausible, valid, and invisible to Picard. The
  container's style comes from `AcoustIdTagField.For` rather than a second
  extension table, so the two cannot disagree about which containers are taggable
  at all.
- **One known shortfall, and it is not fixable from here.** Picard puts the
  *recording* id in an ID3 `UFID` frame and ATL exposes no way to write one, so
  on an MP3 the id goes to `TXXX:MusicBrainz Track Id` — readable by most tools,
  not where Picard would have put it. Every other identifier round-trips through
  TagLib#'s native accessors on every container.
- **Only the year, never a date.** ATL's `Date` is a `DateTime` and cannot hold
  "1969" without inventing the first of January — the same claim `ReleaseDate`
  exists to avoid making. A file already carrying a full date of the right year
  keeps it, because the year then matches and nothing is written.
- **A fact the catalogue does not hold produces no entry at all.** There is no
  way to express "erase this field", deliberately: over a library at a time a
  blank is a deletion wearing an edit's clothes, and nothing downstream could
  tell it from a ripper that never wrote the field.
- **All three links or none.** A file needs a recording, a track *and* a release
  to be on the worklist. Writing an album name with no track number, or a title
  with no album, produces something that reads as a half-tagged rip in every
  player.
- **No single file may end the run, and the catch is on `Exception`.** Because
  the diff is the worklist, nothing steps over a row that threw — an escaping
  exception ends the pass at the same file on every attempt, and one unreadable
  file blocks every file behind it forever. `TagReadFailedException` alone is not
  enough: `TagReader.ReadAsync` opens the stream *outside* its own try, so a
  permissions or I/O error on open arrives as itself. Unlike the probe pass there
  is no subprocess whose absence would fail every file identically, so there is
  nothing here worth stopping for.
- **A file whose album resolved only to a release group gets nothing**, not even
  its title. All three links or none is the rule, and this is the case where it
  costs something; widening it later means deciding what a track number means on
  an album nobody has named.
- **Serial, and the journal says the owner did it.** ATL renders the entire file
  into a staged sibling, so concurrency here is a page of album-sized copies
  competing for one disk. And the actor is `SingleUserCallerContext.OwnerId`
  rather than the identification pass's `SystemCallerContext.SystemId`: that pass
  runs because a scan finished, this one runs because a person pressed a button,
  and that is the only fact worth keeping about a run that rewrote eight thousand
  files.

### The identification providers, and what they refuse to do

| | |
| --- | --- |
| `Domain/Abstractions/IAcoustIdLookup.cs` | fingerprint → AcoustID clusters → recording MBIDs |
| `Domain/Abstractions/IMusicBrainzCatalogue.cs` | MBID → recording / release, flattened to catalogue shapes |
| `Domain/Abstractions/ProviderException.cs` | two subclasses, because there are two reactions: retry later, or fix something |
| `Domain/Identification/RecordingCandidates.cs` | the rule — collapse clusters onto recordings and rank them. Pure |
| `Providers/RequestGate.cs` | one request at a time, no faster than an interval. Both providers |
| `Providers/AcoustId/AcoustIdClient.cs` | gzipped form POST, `meta=recordingids sources` |
| `Providers/MusicBrainz/MusicBrainzCatalogue.cs` | `MetaBrainz.MusicBrainz`, with its rate limiting turned off |
| `Providers/MusicBrainz/MusicBrainzHealthProbe.cs` | is that server up, and would a lookup even be sent? Cached |
| `Providers/ProviderServiceCollectionExtensions.cs` | the wiring, which is where most of the correctness lives |

The theme this time is *these are somebody else's production systems and they
enforce their limits by blocking you*:

- **The rate gate sits underneath the resilience pipeline, not above it.**
  Handlers run outermost-first in registration order, so `AddStandardResilienceHandler()`
  goes on before `RateLimitedHandler`. Reversed, a 503 is answered with three
  immediate retries — the precise burst that turns a temporary rate limit into
  a lasting block.
- **`Query.DelayBetweenRequests = 0`, deliberately.** MetaBrainz throttles
  through a process-wide static, which cannot differ between the official host
  and a mirror and cannot be substituted in a test. `RequestGate` replaces it.
  Removing that line without the gate in place is how the address gets blocked.
- **The attempt timeout is 30s, not the standard 10s.** A cold recording lookup
  against musicbrainz.org with releases and media included was measured at 10.3
  seconds. The default would have cancelled it.
- **`Fonoteca:MusicBrainzRequestIntervalMs` below 1000 is refused at startup**
  when the server is `musicbrainz.org` (`FonotecaOptions.Validate`). Going
  faster is what a mirror is for.
- **The User-Agent is set on the `HttpClient`, not per request**, because
  `MetaBrainz.MusicBrainz` composes the requests. It is the one header
  MusicBrainz blocks an address over, so no code path may be able to forget it.
- **Partial dates stay partial.** `ReleaseDate(Year, Month?, Day?)` rather than
  `DateOnly`, because MusicBrainz's `NearestDate` turns "1969" into
  "1969-01-01" — a claim nobody made, and the one that makes a reissue outrank
  an original when editions are sorted.
- **AcoustID answers identity, MusicBrainz answers metadata.** `meta=recordingids sources`,
  never `meta=recordings`: the titles AcoustID would return are a stale mirror
  of MusicBrainz, and taking them would mean two paths to the same fact ageing
  at different rates.
- **One recording can sit under several AcoustID clusters.**
  `results[0].recordings[0]` is what a naive client reads and it is how a
  remaster gets tagged as the original. `RecordingCandidates.From` collapses
  them: max score, summed sources, ties broken by id so a rerun cannot change
  its mind.

**`Fonoteca:AcoustIdApiKey` is a flat key, and `.env.example` said otherwise for
months.** It shipped as `Fonoteca__Providers__AcoustId__ApiKey`, which binds to
nothing; nobody noticed because nothing called AcoustID. If lookups are being
refused with a key that is plainly set, check the spelling before anything else.

Missing credentials are not startup failures. Without `Fonoteca:AcoustIdApiKey`
or `Fonoteca:MusicBrainzContact` the app starts, logs one line each, and
refuses lookups *locally* with a message naming the setting — rather than
sending an unidentified request to find out.

`GET /api/system/musicbrainz` is what the web client's MusicBrainz card reads.
Two things about it are deliberate. It is **not** an `IHealthCheck` on
`/health`: that endpoint answers "should this process keep serving traffic",
and the answer does not change when MusicBrainz is down — scanning and browsing
work fine without it, and wiring it in would turn their outage into our restart
loop. And the reading is **cached for 30 seconds**, because the probe queues at
the same gate as identification work: an uncached probe does not just cost a
request, it costs a *turn*, and a browser tab left open on that card would
otherwise halve a scan's throughput. `POLL_MS` in `MusicBrainzPanel.tsx` and
`MusicBrainzHealthProbe.CacheDuration` are a pair; move one and move the other.

`Fonoteca.Providers.Tests` builds the real service collection and replaces only
the socket, so handler order, the gate and the User-Agent are the ones the
application gets. The MusicBrainz fixtures under `Responses/` are verbatim WS/2
documents; don't tidy them, the mess is the point.

### The MusicBrainz mirror

`./scripts/musicbrainz-mirror.sh` runs a local mirror — the web service, no
search index, replicating daily. Optional; nothing in the build, the tests or
`pnpm dev` touches it. See `docs/musicbrainz-mirror.md` and ADR 0006.

- **It is a wrapper over `metabrainz/musicbrainz-docker`, pinned by tag, not a
  compose file of our own.** Their repo is where the schema migrations live and
  MusicBrainz change the schema about twice a year. `infra/musicbrainz/mirror.yml`
  is the only part we own: it layers onto upstream's `alt-db-only-mirror` base
  and restores the web server that base turns off.
- **Its own compose project, `fonoteca-musicbrainz`, not a profile in
  `compose.yaml`.** `podman compose down -v` while resetting the dev database
  must not be able to delete 100 GB that took a day to build.
- **No Solr, and one call now needs it.** Replication does not cover search
  indexes, so they would need a rebuild schedule forever. That was free while
  `IMusicBrainzCatalogue` had no search method at all; it now has exactly one —
  `SearchReleasesAsync`, behind the by-hand album screen — so against a mirror
  that call fails where every other one works. It is reported as the provider
  error it is rather than as an empty result, and the screen says so in as many
  words, because "no albums match" and "this server cannot search" are otherwise
  indistinguishable. Pasting a release MBID or URL into the same box is a lookup
  and works against a mirror. Nothing automated calls it: if tag-based matching
  is ever added as a *pass*, this is still the thing to revisit first.
- **AcoustID gets no equivalent.** No full base dump exists — only daily
  incrementals back to 2011, ~414 GB compressed — and its server is documented
  as only meant to run on acoustid.org.
- **Two declarations gate the first run, and both fail silently upstream.**
  `fetch-dump.sh` asks a commercial-use question with `read -e`, which prints no
  prompt at all when stdin is not a TTY and then dies under `set -e` — a
  container that exits 1 in 40ms having printed nothing. `set-replication-token`
  loops forever on EOF for the same reason. `accept-terms` and `set-token` exist
  so both fail loudly and early instead. Neither answer is ours to guess.

### Two things that must stay in a hosted service

`StartupTasks` (migrations, config checks) is an `IHostedService`, not inline
after `builder.Build()`, because `GetDocument.Insider` (the build-time OpenAPI
generator) and `dotnet ef` both construct the host without running it. Inline
startup code therefore ran during an ordinary `dotnet build` — connecting to
PostgreSQL and migrating. `HostRuntime.IsDesignTime` detects those tools by
entry-assembly name and skips the work. `DesignTimeDbContextFactory` exists for
the same reason on the EF side. Don't move startup work into `Program.cs`.

### Frontend

- `packages/tokens` — one TypeScript source emitting **both** `dist/tokens.css`
  (custom properties) and typed `var()` refs. Light and dark are typed
  identically, so a token defined in one theme and missing from the other is a
  compile error. Built with plain `node src/build.ts` (Node 24 type stripping).
- `packages/ui` — the design system. **`react` + `react-dom` only**: no
  primitives library, no positioning library, no focus trap, no virtualizer
  (ADR 0003). Components are a `.tsx` + colocated `.module.css`, variants
  selected via `data-*` attributes rather than class concatenation. React 19,
  so `ref` is an ordinary prop — no `forwardRef`.
- `packages/api-client` — generated types plus a hand-written `fetch` wrapper.
  Zero runtime dependencies. `get` takes the route's own parameters, derived from
  the generated `parameters` map, and a route with a `{segment}` makes that
  argument **mandatory** — a rest tuple rather than an optional parameter,
  because an optional one cannot be made required by a condition and a call that
  forgot `{ id }` would happily request a URL containing a literal `{id}`.
- `apps/web` — Vite + React 19, **TanStack Router** with routes defined in code
  (ADR 0008); `AppShell` is the root route and the transport row sits outside the
  outlet. The **server-state** choice is still open: `useApiQuery` is the shared
  version of the throwaway `useState`/`useEffect`, with no cache, no dedup and no
  invalidation, so adopting TanStack Query stays a decision to take on evidence
  rather than one taken by association with the router's name.

The shell is a navigation rail, a top bar over the content column, a centred
content column and the transport row across the foot. Two things in it are not
free-form layout. `NAV_ITEMS` in `apps/web/src/navigation.ts` is the **one** list
of sections — the rail renders it and the command bar's "Go to" commands are
built from it, so a new screen cannot reach one and miss the other. And the
command bar itself (`CommandBar`, ADR 0010) is a native `<dialog>` around a
hand-built combobox: the dialog element is doing the focus trap, the focus
restoration, `Escape` and the top-layer stacking, which is why nothing in it
touches the z-index scale. Its commands are a prop; searching the catalogue is
not among them, because no endpoint answers that yet.

Three theme states, not two: `data-theme="light"`, `data-theme="dark"`, and
**no attribute at all** (follow the system). `ThemeProvider` removes the
attribute for "system"; the stylesheet guards its dark media query with
`:root:not([data-theme='light'])`. Don't collapse these into a boolean.

### Accessibility is enforced, not advised

`pnpm test` runs **every story** as a test in a real Chromium via
`@storybook/addon-vitest`, with axe set to `test: 'error'`. This is the
enforcement half of the zero-dependency design system: every ARIA role, label
and focus behaviour is hand-written, so the axe run is the only safety net. A
real browser, not jsdom — axe checks computed styles and contrast, which jsdom
does not model. Chromium comes from `~/.cache/ms-playwright`; never run
`playwright install-deps` (it shells out to apt).

Adding a component means adding stories, because that is what tests it.

## Conventions and constraints

- **Warnings are errors** on the .NET side (`Directory.Build.props`), nullable
  enabled, `AnalysisLevel latest-recommended`. Test projects relax
  `TreatWarningsAsErrors` only. `ConfigureAwait(false)` is enforced (CA2007).
- **Central Package Management**: every NuGet version is pinned in
  `apps/api/Directory.Packages.props`, including forward-pins of transitively
  vulnerable packages (NU1903 is a build failure here). Never add a `Version=`
  to a `PackageReference`.
- **Migrations are generated code.** `apps/api/.editorconfig` marks
  `**/Migrations/*.cs` as generated; don't hand-edit them.
- **Two tag libraries on purpose** (ADR 0002): ATL.NET writes, TagLib# reads
  the file back to verify, disagreement aborts the operation. ffmpeg must never
  write tags — it rewrites containers and can drop non-standard frames. The
  implemented path adds two steps the ADR does not have: a length sanity check,
  which catches the one failure two agreeing readers cannot (perfect tags, no
  audio), and appending the undo entry *before* the commit rather than after, so
  a crash in between over-records rather than under-records.
- **TypeScript 7 everywhere**, except `tools/openapi-codegen`, which pins 5.9
  because `openapi-typescript` drives the compiler API that 7.0 doesn't ship
  (ADR 0005). Nothing imports that package.
- Relative TS imports carry an explicit `.ts`/`.tsx` extension
  (`rewriteRelativeImportExtensions`), which is what lets scripts run under
  `node` directly with no build step.
- Biome: single quotes, no semicolons, trailing commas, 100 cols. It skips
  `apps/api`, `openapi.json` and the generated `schema.d.ts`.
- `pnpm-workspace.yaml` `allowBuilds` is an explicit allowlist for install
  scripts; adding an entry is a decision, not a formality.

## Gotchas already paid for

- **AcoustID returns clusters, not answers, and one recording routinely spans
  several of them.** A lossless rip and a 128kbps rip of the same track can sit
  in separate clusters that were never merged, so a lookup answering `0.957` and
  `0.939` is usually *one* answer arriving twice. `AcoustIdSelection` originally
  read any near-tied cluster as a disagreement and refused to tag the file;
  measured against the real library, **925 of the 951 files it had withheld were
  this**, and in 794 of them the rival named the same MusicBrainz recording.
  The rule now compares each near-tied rival's *dominant recording* against the
  winner's, and a cluster with no MusicBrainz link cannot contradict one that
  has. **Do not reach for `RecordingCandidates` here** — it collapses the other
  way, onto recordings, so one cluster legitimately linked to several recordings
  becomes a zero margin: measured, it breaks 64 of 200 files that identify
  confidently today. **And do not let `Sources` outvote a near-tied rival**; it
  recovers 36 more files and decides live-against-studio by popularity, tagging
  a track from an album called *Live* with the studio recording. Both wrong
  turns are pinned by tests that name them.
- **The catalogue records identification outcomes but not the evidence**, and
  `Log.FileNotIdentified` is `Debug`. Working out what those 951 files actually
  were meant re-asking AcoustID for every one of them. The fingerprints are
  stored, so it cost 951 turns at the rate limit and no decoding — but budget
  ~6 minutes and a script, not a SQL query, for any question of this shape.
  `AcoustIdCheckedUtc IS NULL` is the worklist and **no endpoint clears it**, so
  re-asking after a rule change is a hand-written `UPDATE`.
- **Two stages joined by a bounded channel and a `Task.WhenAll` deadlock when
  the consumer dies.** The producer blocks in `WriteAsync` on a channel nobody
  will drain again, so `WhenAll` waits on the producer forever and *never
  observes the consumer's exception*. No stack trace, no log line, no summary —
  the pass simply stops, and the status endpoint goes on reporting "running" at
  the file it reached. It cost twenty-five minutes of a live run before anyone
  noticed, and diagnosing it needed a process dump: `dotnet-dump collect` then
  `dumpasync`, where the producer sat under a `WhenAllPromise` with
  `_remainingToComplete = 1` and the consumer was simply absent.
  `IdentificationService.RunAsync` now runs the consumer through
  `ConsumeThenReleaseAsync`, whose `finally` cancels a linked CTS that the
  producer's token comes from. **Any pipeline added here needs the same
  release.** A test with one or two files cannot catch this — the producer
  finishes before the channel fills, which is exactly why every existing test
  stayed green while the bug was live. `AFatalErrorEndsThePassInsteadOf`
  `LeavingItRunningForever` uses eight.
- **Tag parsers throw whatever they like, and forty files in the target library
  make ATL throw `NullReferenceException`.** They are FLACs with an ID3v2 tag
  prepended — illegal, since a FLAC begins with `fLaC`, but several taggers did
  it anyway. The trigger is specifically **unsynchronisation**: a prepended tag
  alone is harmless and so is the flag on its own, but a tag whose bytes are
  genuinely unsynchronised shifts every offset after the picture when undone,
  and `Track.EmbeddedPictures` then dereferences null. Reading ordinary fields
  still works, which is why stage A sails past these files and only stage B
  falls over — the failure lands hundreds of files into a run, nowhere near the
  cause. `TagReader` now converts any parser failure into
  `TagReadFailedException`, and the pass reads that as **do not write to this
  file** rather than as something to tolerate: on these files ATL reports zero
  pictures where TagLib# finds one, so a forgiving read would have let the
  artwork check compare nothing to nothing and pass. `Corpus.Id3PrefixedFlac`
  builds one, and the recipe is spelled out there because four near-miss
  variants do *not* reproduce it.
- **`pnpm api:test` hangs about one run in three, and the tests have already
  passed when it does.** The hang is in `Fonoteca.Integration.Tests` at process
  exit, *after* the run reports success. Under `dotnet test` the symptom is a
  console that stops producing output for as long as you let it — seventeen
  minutes, in one case, before anyone looked; `dotnet test`'s output is buffered,
  so "no output" and "no progress" are indistinguishable from outside. A stack of
  the VSTest host shows it parked in `Xunit.v3.LocalTestProcess.WaitForExit`,
  waiting on the xUnit v3 test executable it spawned, and that executable's own
  `Main` is still awaiting the run task with **no Fonoteca frames anywhere on any
  thread**. The last two lines it logs are always the same pair:
  `Identification stopped early: The operation was canceled.` then
  `Hosting stopped` — so it correlates with the pass that
  `Fonoteca:IdentifyAfterScan` starts inside `LibraryScanEndpointTests`, and it
  survives the host shutting down cleanly. **It predates the enrichment work**
  (reproduced at `a1767c1`, three runs in four), so a bisect will not find it in
  recent commits. What is *not* yet established is which foreground thread keeps
  the process alive.
  **The workaround, and it is a good one:** xUnit v3 projects are executables, so
  run the test binary directly and skip the VSTest bridge entirely —
  `./apps/api/tests/Fonoteca.Integration.Tests/bin/Debug/net10.0/Fonoteca.Integration.Tests`
  runs all 103 integration tests in **18 seconds** against `dotnet test`'s
  several minutes. It still hangs occasionally, for the same reason, but it fails
  fast enough to just re-run. Per-class `--filter` runs are reliable.
- **A running `dotnet run` API stalls `dotnet test`.** The test build wants to
  write `Fonoteca.Api.dll`, the running host holds it, and MSBuild waits rather
  than failing — so the run sits at zero output for as long as you let it. Stop
  the API first. If one has already wedged, the orphaned `dotnet test` keeps the
  lock after the shell that started it is gone, and Testcontainers leaves its
  PostgreSQL behind too (Ryuk is disabled here, so nothing reaps it): kill the
  process by pid and `podman rm -f` the stray `postgres:18-alpine`.
- **And the collision the other way round poisons the running API instead.**
  Building while the dev host is up — `dotnet build` for a test run, say —
  replaces `Fonoteca.Api.dll` under a process that has it mapped. Everything the
  CLR has already JITted keeps working, so the API goes on answering and looks
  healthy; the *first* type it has to load afterwards throws
  `BadImageFormatException` — "The signature is incorrect. The format of the file
  '…/Fonoteca.Api.dll' is invalid." Which means it surfaces on whatever code path
  nobody has exercised yet, arbitrarily far from the build that caused it, and
  reads as a bug in that feature. `dotnet watch` does not always catch it: the
  build may land while a restart is already in flight. The fix is
  `systemctl --user restart fonoteca-api.service` — the API unit alone, not
  `fonoteca.target`, which would take Vite's dep cache down with it.
- PostgreSQL 18 wants a single mount at `/var/lib/postgresql`, not
  `/var/lib/postgresql/data`; the old path makes the container refuse to start.
- Testcontainers over podman needs `DOCKER_HOST` pointed at the user socket and
  Ryuk disabled — `PostgresFixture` sets both when unset, **from a static
  constructor**. `PostgreSqlBuilder.Build()` validates that a runtime is
  reachable, and it runs in the fixture's field initialiser: setting the
  variable from `InitializeAsync` is too late, and every integration test fails
  with "Docker is either not running or misconfigured" on a host where podman is
  running perfectly.
- Integration tests that touch `MediaFiles` need their own database
  (`PostgresFixture.CreateDatabaseAsync`), not just their own rows. The scan
  deletes catalogue rows whose files are not on disk, so a shared database means
  one test silently wipes another's fixture data.
- `podman compose up -d` returns before Postgres accepts queries; `dev.sh`
  polls the healthcheck because the API migrates on boot.
- `Microsoft.Extensions.Diagnostics.HealthChecks` ships in the shared framework
  — referencing it explicitly trips NU1510. Only the EF Core integration is a
  real package.
- **`ConfigureHttpJsonOptions` sets `NumberHandling = Strict`, and it has to
  stay that way.** ASP.NET's web defaults allow reading numbers from strings,
  and .NET 10's OpenAPI generator reports that faithfully: every `int` comes out
  as `["integer", "string"]`, so the generated TypeScript types every count as
  `string | number` and arithmetic on one fails to compile. The workaround that
  suggests itself — hand-declaring the response shape in the component — throws
  away the contract the OpenAPI seam exists to enforce, which is how
  `HealthPanel` ended up with a local `SystemInfo` type and a cast.
  `SystemEndpointTests` asserts the JSON is numbers.
