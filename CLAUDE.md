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

Still missing: hashing, probing (`AudioQuality` is never populated), and
downloading.

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

**The folders are not consulted.** That was the ask and it turned out to be
forced: sampling 60 files with `ffprobe` found `ACOUSTID_ID` on every one and
nothing else at all — no `ALBUM`, no `TRACKNUMBER`, no barcode. The library was
deliberately tag-stripped, so the directory name and the audio are the only two
claims in existence and one of them is the one not to believe. It is wrong where
it matters, too: a folder named for a 1979 album holds the audio of the 2015
remaster, and one named `(2009)` holds a release MusicBrainz dates to 2010.

- **A single file cannot name its release, so the unit is a component** — a set
  of files sharing candidate releases, decided and committed together. This is
  the one pass with no per-file unit of work, so resumability is per component
  rather than free. Components are *discovered* by following shared candidate
  releases outward from one seed, never assumed from a directory.
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
- **Known failure: heavily anthologised catalogue.** Michael Jackson's albums do
  not attribute correctly. `Off the Wall` — ten tracks, all ten held, with a 2015
  remaster matching to the millisecond — never enters the component's candidate
  set, so the files land on whichever compilation did: a five-disc box set at one
  setting of the cap, a greatest-hits and a two-in-one at another. All 31 files
  are decided in a *single* component (`count(DISTINCT ReleaseLookupUtc) = 1`),
  so this is not fragmentation across components, and no cap is logged for it.
  Reproducing the gather in Python against the same mirror *does* confirm
  `Off the Wall` among the candidates at coverage 1.00 and drift 0.00, so the
  rule would pick it if it arrived — which means the loss is in `GatherAsync`
  and has not been isolated by reading. It needs a debugger and a
  candidate-by-candidate trace, not more inference. Artists with a shallower
  compilation history (Bonamassa, Ella Fitzgerald's albums) come out right.

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
- **No Solr, and that is load-bearing.** Replication does not cover search
  indexes, so they would need a rebuild schedule forever. `IMusicBrainzCatalogue`
  has no search method, so nothing would call it. If tag-based matching is ever
  added as a fallback, revisit this *before* designing it.
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
