# CLAUDE.md

Guidance for Claude Code (claude.ai/code) working in this repository.

**This is a map, not the record.** Each rule is stated once, in the shortest
actionable form. The measurement behind it, the live failure that bought it and
the argument for it are in the XML `<remarks>` on the type named beside it, in
`docs/adr/`, and in `.env.example` for settings — all three are written to be
read. Where this file and the code disagree, the code is right.

## What this is

A self-hosted music library manager for 100,000+ track libraries: catalogue and
dedupe, acquisition (Qobuz), *arr-style upgrade monitoring, tag editing.
.NET 10 API plus a pnpm/Vite/React 19 workspace, joined only by OpenAPI.

**Six passes, six screens, one thing that can spend money.**

| pass | does | service |
| --- | --- | --- |
| scan | walks the root, reconciles `MediaFiles` with the disk | `Api/Library/LibraryScanService.cs` |
| identify | `fpcalc` → AcoustID → writes the tag | `Api/Library/IdentificationService.cs` |
| enrich | identities into a catalogue — five stages | `Api/Library/EnrichmentService.cs` |
| attribute | which release each *folder* came from, from the audio | `Api/Library/ReleaseAttributionService.cs` |
| probe | `ffprobe -count_frames` — quality and integrity in one decode | `Api/Library/ProbeService.cs` |
| tag write | the whole catalogue back into the files | `Api/Library/TagWriteService.cs` |

Five are a `POST`/`GET`/`DELETE` trio under `/api/library` taking
`LibraryWorkGate`. **The scan is neither**: it cannot be cancelled, and it
guards itself with a private interlocked flag rather than the gate — so a scan
can arrive in the middle of anything. Nothing chains one pass into another
except `Fonoteca:IdentifyAfterScan`. The tag write is the only one nobody may
automate.

**Still missing: hashing** — the last of the original list, and what a dedupe
key would be made of.

## Six rules the whole codebase turns on

Almost every sharp edge here is one of these. Reading them first saves reading
the rest.

1. **A worklist keys on "we asked", never on "we have an answer".**
   `AcoustIdCheckedUtc`, `RecordingLookupUtc`, `ReleaseLookupUtc`,
   `Artists.LookupUtc`, `PortraitLookupUtc`, `DiscographyLookupUtc`,
   `LastVerifiedUtc`. A library is full of things the provider has never heard
   of; keyed on the identifier, every one is re-asked forever and the worklist
   never empties. The stamp goes on when the answer is "no such thing" too, and
   is left null **only** when the lookup did not happen. Corollary: widening a
   lookup is invisible to everything already stamped, so re-asking is a
   hand-written `UPDATE` — narrow it to the rows that need it.

2. **A write changes the bytes, so the new size and mtime go in the same
   `SaveChanges`.** Otherwise the next scan reads the file as modified and
   discards every derived column, including what was just written. Never "fix"
   this by forging the old mtime back: the file genuinely changed.

3. **Absence of evidence is not evidence of deletion.** The scan counts
   unreadable directories rather than ignoring them, removes nothing when it
   sees zero files against a full catalogue, does not follow directory symlinks,
   and floors timestamps to whole microseconds (`timestamptz` keeps microseconds,
   .NET keeps 100ns ticks). Same shape everywhere: a missing credit row means the
   catalogue does not know, not that the answer is no.

4. **A rule's answer and a person's answer are different facts.** Folding them
   loses any ability to report on the rule. `IdentifiedByPerson`,
   `RejectedByPerson`, `LinkedByPerson`, `AttributedByPerson`,
   `NoReleaseByPerson`, `Unreleased`, `ReopenedByPerson` — each with an
   `…ByAgent` twin for `/mcp`, all listed in `ByCaller`. `IdentityDecidedUtc` and
   `ReleaseDecidedUtc` are separate *columns* from the outcome, because clearing
   a lookup stamp by hand must not hand answered files back to the rule that
   could not answer them.

5. **Rules are pure and live in `Fonoteca.Domain`; a rule a screen also needs
   gets a `.ts` twin beside the page.** Those import no React and no stylesheet
   so they run under `node --test`, and **may not import `api.ts`** — the
   generated client uses TypeScript parameter properties, which Node's
   strip-only type stripping refuses outright.

6. **Refusing is a first-class answer.** A wrong album is worse than a missing
   one and far harder to notice. Every pass has refusal outcomes; the by-hand
   screens exist to answer them.

## Toolchain

Everything comes from `~/.local/opt` (`docs/toolchain.md`). **Nothing is
installed with apt.** Shells that have not sourced `~/.local/opt/env.sh` will
not find `dotnet`, `node`, `pnpm`, `ffmpeg` or `fpcalc` — `scripts/dev.sh`
sources it, ad-hoc commands must too:

```sh
source ~/.local/opt/env.sh
```

PostgreSQL runs in podman. Testcontainers and `podman compose` both need
`systemctl --user enable --now podman.socket`.

**`.env.example` is a document, not a template.** Read it before adding a key.

## Commands

```sh
pnpm dev              # postgres (podman) + api :5088 + web :5173 + storybook :6006
pnpm build            # tokens -> ui -> api-client -> web, topological
pnpm typecheck
pnpm lint             # biome; lint:fix / format to write
pnpm test             # -r: every story through axe in real Chromium, plus node --test
pnpm api:build        # also regenerates openapi.json at the repo root
pnpm api:test         # xUnit; integration tests start a real PostgreSQL 18
pnpm gen:api          # openapi.json -> packages/api-client/src/schema.d.ts
pnpm gen:api:check    # fail if the committed client has drifted

./scripts/musicbrainz-mirror.sh help   # optional local mirror; docs/musicbrainz-mirror.md
```

Single tests:

```sh
dotnet test apps/api/Fonoteca.slnx --filter "FullyQualifiedName~MigrationTests"
pnpm --filter @fonoteca/ui test -- src/Button          # one story file
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

`apps/api` is deliberately **outside** the pnpm workspace. The only seam is the
OpenAPI document: building `Fonoteca.Api` writes `openapi.json` to the repo root,
`pnpm gen:api` turns it into `packages/api-client/src/schema.d.ts`, and that file
is **committed** so `pnpm gen:api:check` fails on drift. An API change the
frontend has not caught up with breaks the build, not the browser. Change an
endpoint's shape → rebuild the API → `pnpm gen:api` → commit both.

### Backend layering

```
Fonoteca.Api        minimal API, OpenAPI, SignalR, health
Fonoteca.Domain     PURE — entities, matching, quality ranking
Fonoteca.Ingest     walk, probe, fingerprint
Fonoteca.Tagging    read/write, dry-run diff, undo journal
Fonoteca.Providers  acoustid | musicbrainz | qobuz | coverart | wikidata | audiodb
Fonoteca.Jobs       empty — Hangfire refs, a reference to Domain, no source
Fonoteca.Data       EF Core, migrations
```

**`Fonoteca.Domain.csproj` has no `PackageReference` and no `ProjectReference` —
that emptiness is the architecture.** Anything the domain needs from outside is
an interface in `Domain/Abstractions/` that it receives; a package reference
appearing there means something leaked. `IJobQueue` lives there too (ADR 0007
records what would fill `Fonoteca.Jobs`). **There is no Deezer provider and the
absence is a decision**, recorded in `ArtistPortraitSources.cs`.

Entities are MusicBrainz-shaped (`Work`/`Recording`/`ReleaseGroup`/`Release`/
`Track`/`MediaFile`/`Artist`/`ArtistCredit`/`Relationship`), not
artist→album→track — ADR 0004, and it cannot be retrofitted. Ids are typed
`readonly record struct` wrappers over `Guid.CreateVersion7()`, time-ordered so
100k-row inserts stay at the right edge of the B-tree.

**Typed ids are value-converted, so EF translates no member access on them into
SQL.** Hence `Relationship.ArtistId` as a typed column beside the generic
`SourceType`/`SourceId` pair, and hence a few browse rules written twice rather
than extracted — a helper taking the row as an argument is read as a closure and
500s inside a projection. `TheListAndTheDetailPageAgree` stops those drifting.

### Where each capability lives

A capability is a pure rule in `Domain`, an adapter, a service, an endpoint and
(usually) a `.ts` twin. The rule file is the one to read first.

| capability | the pure rule | the service / adapter |
| --- | --- | --- |
| scan | `Domain/Catalogue/AudioFormats.cs` | `Ingest/LibraryScanner.cs`, `Ingest/FileSystemAudioFileStore.cs` |
| identify | `Domain/Identification/AcoustIdSelection.cs` | `Ingest/FpcalcFingerprinter.cs`, `Tagging/AcoustIdTagField.cs` |
| enrich | `Domain/Catalogue/PrimaryCredits.cs`, `LatinNames.cs` | `Providers/MusicBrainz/MusicBrainzCatalogue.cs` |
| portraits | `Domain/Catalogue/ArtistNameMatch.cs`, `Abstractions/ArtistPortraitSources.cs` | `Providers/{Wikidata,Qobuz,AudioDb}/*Portraits.cs` |
| attribute | `Domain/Identification/ReleaseFit.cs`, `ReleaseAttribution.cs`, `Catalogue/AlbumFolder.cs` (and its TS twin `ALBUM_FOLDER_DEPTH` in `seating.ts` — change both) | `Api/Matching/ComponentCandidates.cs` |
| probe | `Domain/Abstractions/IAudioProbe.cs` | `Ingest/FfprobeAudioProbe.cs` |
| tag write | `Domain/Catalogue/CatalogueTags.cs` | `Tagging/TagWriter.cs`, `Tagging/CatalogueTagFields.cs` |
| by-hand matching | `web/src/pages/seating.ts`, `identify.ts` | `Api/Endpoints/CatalogueEndpoints.{AlbumMatching,Identify}.cs` |
| files & previews | `Domain/Catalogue/FolderRollup.cs`, `FilePreview.cs` | `Api/Library/FileManagerService.cs` |
| covers | — | `Providers/CoverArt/`, `Api/Endpoints/CatalogueEndpoints.Cover.cs` |
| acquisition | `Domain/Acquisition/UpgradeScan.cs`, `UpgradeReplacement.cs`, `StagedFileName.cs`, `Discography.cs` | `Api/Acquisition/{QobuzDownloadService,AlbumReplacementService}.cs` |
| monitoring | `Domain/Catalogue/ReleaseTitleMatch.cs` | `Providers/Qobuz/QobuzReleaseDiscovery.cs` |
| MCP | — | `Api/Mcp/LibraryTools.cs` |

## The passes

Rules 1 and 2 are identification's, paid for again by every pass after it.
ADR 0002 governs the write path — **read it before touching that path.** File
mutation is behind `Fonoteca:AllowFileMutation`, default `false`; with it off a
run does everything except the write, which makes a disabled run a complete dry
run rather than a no-op.

- **The scan runs in the foreground of the request** — a walk with no file reads
  is seconds even at 100k. Its four edges are rule 3.
- **Identify: one scope and one `SaveChanges` per file, not batched.** That is
  the entire resumability story, and the journal entry only persists because it
  shares the scope — `IEventLog.AppendAsync` does not save itself, by design. The
  file is committed before the row, so a crash in the gap converges on the next
  run; the reverse leaves a row claiming a tag the file does not carry.
  `AcoustIdTaggedUtc` is a separate column from `AcoustIdCheckedUtc` because "we
  know what this is" and "the file says so" are different facts.
- **Fingerprinting is parallel, lookups are strictly serial.** `RequestGate`
  allows one AcoustID request per 340ms, so concurrent lookups buy nothing and
  pin fingerprints in memory.
- **Subprocess failures are split by whose fault they are**, in every pass that
  has one. A truncated FLAC marks one row; a missing binary must stop the pass on
  the first file, or a `PATH` problem marks 100,000 files unreadable.
- **The undo journal is keyed by `MediaFileId`, never by path.**
  `DomainEvent.SubjectId` is `varchar(200)`; a library path is up to 4096, and a
  box set broke a live run at file 76. Paths also move.

**Enrichment is five stages in `RunAsync`, in order, because each depends on what
the ones above wrote:** identified files → person-filed files
(`ClaimPersonFiledAsync`, promoting `LinkedByPerson` to `Linked` with credits) →
artists → portraits → discographies. `CountPendingAsync` returns one number per
stage with `Pending` as the sum; **a stage added here is a number added to four
places** — the record, the wire, `EnrichmentPanel`'s sentence, and `Total`.

- **It opens no files**, so re-asking after a rule change costs turns at the rate
  limit rather than hours of fpcalc, and it runs with the volume unmounted.
- **Sequential on purpose, and the absence of a channel is the design.** Both
  halves are gated network calls, so concurrency buys nothing `RequestGate` would
  not serialise again one layer down. Copying identification's two-stage split
  imports the deadlock hazard below and none of the benefit.
- **A credit line is not the whole story.** MusicBrainz bills a Karajan reading
  of Beethoven's Fifth to *Beethoven*; reading `ArtistCredits` alone files every
  symphony under a man who died in 1827. `PrimaryCredits` unions four sources and
  recognises an ensemble by type OR by relation. Billed credits go to
  `ArtistCredit` (`Position`/`JoinPhrase` describe a printed billing line);
  everything else goes to `Relationship`.
- **The artist stage is the only place with a source better than a sleeve**, so
  it overwrites — but only where there is an answer, since MusicBrainz sends `""`
  for absent text and a blank would stamp in the same save. Clamps are on length,
  not count: an overrun throws out of `SaveChangesAsync` and rolls the stamp back
  with it, landing the row on the worklist forever.
- **`Artists.LatinName` is MusicBrainz's own English alias**, a column rather
  than an overwrite so EF projections can read `LatinName ?? Name` and so the tag
  writer never rewrites a Japanese pressing's `ARTIST` frame on a display
  preference. Credits resolve `CreditedAs ?? LatinName ?? Name`.
- **Portraits: Wikidata runs over the whole batch first, and Qobuz and TheAudioDB
  are what it upgrades *from*** — those two are preferred and **overwrite** it,
  and the fill-the-gaps rule is between *those two*. **Only Qobuz is keyed on a
  name**, which is what `ArtistNameMatch` guards; the other two take the
  MusicBrainz id and cannot be wrong about who. A failed batch stamps nothing and
  **ends the stage**, because the claim query is keyed on the stamp.
- **Known and not fixed, and written down nowhere else:**
  `ArtistCredit.CreditedAs` is stored as `credit.Name == artist.Name ? null :
  credit.Name`, so credits written while the row still said "Bowie" recorded
  `null` — read back after the lookup renames it to "David Bowie", they claim a
  billing the sleeve never printed. Only credits written before the artist was
  described. The fix is recomputing every credit on a rename, which is a pass of
  its own.

**Attribution's unit is a folder, not a file.** *The folder's boundary is
believed; its name never is* — a folder named for a 1979 album holds the audio of
the 2015 remaster. But which files belong *together* is a different claim, and
refusing both cost more than it bought: growing components outwards through
shared candidates is how a compilation welds two albums into one set.

- **Two MusicBrainz shapes are unusable and force a two-stage gather.**
  `inc=releases` on a recording lookup caps at 25 without saying so;
  `inc=recordings` on a *browse* silently drops releases (40 becomes 15 while
  `release-count` still claims 40). Track lists come from `GetReleaseAsync`.
- **Gates come before size.** The greedy "most files explained" let a 31-track
  bootleg *Greatest Hits* claim seventeen files across four albums.
- **Duration is the edition discriminator**, stored to 10ms — enough to separate
  a remaster from three earlier pressings on identical track lists.
- **A tie resolves three ways, by what the tie costs:** editions agreeing on
  every position → a stated tie-break; disagreeing about disc or position → keep
  only the release group, because a track number would be an invention; neither →
  refuse. Asked of the whole set, or one rip splits and reports itself incomplete.
- **EF queries the database, not the change tracker** — three unique-index
  collisions in live runs came from this, so everything the writer mints goes
  through a dictionary.
- **Only chosen releases persist as catalogue, but their whole track list does**,
  which is what makes "you are missing track 7" answerable. A *refused*
  component's shortlist goes to `ReleaseCandidateSet`, keyed on the one
  `ReleaseLookupUtc` its files share.
- **Known failure, expected fixed by the folder cut and NOT re-measured.**
  Michael Jackson's albums attributed wrongly; the loss was in `GatherAsync`, not
  in the rule. **Run the pass against the real mirror before believing that.**

**Probe: two questions, one decode.** `-count_frames` decodes rather than parsing
the header, so the run that fills `AudioQuality` is the only one that can say
whether the bytes are intact — splitting them decodes the library twice. At
`-v error` a healthy stream says nothing, so anything on stderr is the decoder
objecting *while exiting zero*; that becomes `IntegrityState.Corrupt` and never
reaches `AudioQuality`, which decides which duplicate to keep. Parallel over a
claimed page with **deliberately no channel** — see the deadlock gotcha.

**Tag write: everything above this line writes to PostgreSQL.** Copy the library
elsewhere and none of it exists; this pass is what makes the answers portable.

- **Three buttons and no fourth route.** One pass, three scopes (library /
  release / artist) rather than three mechanisms — "quick" is a property of the
  library, not of the endpoint. The artist scope reuses the same rule the artist
  page browses by, so what is written is what that page lists.
- **There is no `TagsWrittenUtc`, because the diff is the worklist** — a file
  already saying what the catalogue says comes back `NothingToDo` having been read
  and never opened. The one place rule 1 does *not* apply: reading a file's own
  tags costs nothing anyone is waiting for.
- **`AcoustIdTagWriter` is a wrapper over `TagWriter`.** The write path is the
  only code here that can destroy something a rescan cannot rebuild; two copies
  would drift in the way nobody notices until a library is already wrong.
- **The verification exclusion is load-bearing.** `TagSnapshot.FieldsLostIn`
  reports everything that moved, so the intended fields come out of it — and
  `DATE` with them, because ATL derives it from the same value as `YEAR`. Without
  that pairing every write correcting a year rolls itself back.
- **All three links or none** (recording, track, release), **only the year, never
  a date**, and **a fact the catalogue does not hold produces no entry** — over a
  library at a time a blank is a deletion wearing an edit's clothes.
- **No single file may end the run, and the catch is on `Exception`.** Because
  the diff is the worklist, nothing steps over a row that threw. Serial, because
  ATL renders the whole file into a staged sibling.

## The by-hand screens

`/library/matching` renders `IdentifyPage`, one album folder at a time whatever
each open file in it was refused for. `GET /api/catalogue/matching` still serves
the grouped worklist but **nothing in the web client calls it** — it is reached
only over `/mcp` as `open_questions`.

- **The unit differs by kind.** Identification and enrichment refuse one file;
  attribution refuses a *component*, recovered from the one `ReleaseLookupUtc`
  the pass stamped on every file it decided.
- **Three refusals are deliberately not questions:** `LookupFailed` is transient,
  `NotAttempted` is a queue position, `Unfingerprintable` is about the file
  rather than the music. **The reason shown is the first pass that refused**, or
  the screen reports a consequence and sends a person to the wrong place.
- **Two caches sit under candidate recovery**, both believed for a week: the
  provider's own answer (`AcoustIdMatchesJson`) and the assembled document
  (`RecordingCandidatesJson`), because a recording lookup with releases and
  groups included was measured at **10.3s cold**. `CandidateWarmService` fills the
  second ahead of the click by putting questions to *the endpoints themselves*;
  it stops for the gate **and for the scan** (which clears these columns and has
  never been on the gate), assembles nothing itself, and may not throw — an
  unhandled exception out of a `BackgroundService` stops the host.
- **What is cached is answers, never rankings.** `RecordingCandidates` and
  `AcoustIdSelection` collapse the same evidence in opposite directions; caching
  a rule's output leaves the cache wrong the day the rule changes. A malformed or
  shape-incomplete entry is a cache miss, not a failure.
- **The decision endpoint re-asks AcoustID rather than trusting the request
  body** — the one irreversible write in the system should not be authorised by a
  form post. It links the recording even when no cluster survives.
- **The component stamp is a string on the wire.** `DateTimeOffset.UtcTicks` is
  ~6.4×10^17 where JavaScript is exact to 9×10^15; routed as a `long` it arrives
  rounded and every request asks about a component that does not exist.
- **The pass writes the candidates down, and that is what makes the screen
  free** — zero provider requests to open a refused album,
  `ThePassStoresTheCandidatesItWouldOtherwiseDiscard` asserts it by counting
  calls. `Files` is the staleness check, not the timestamp: a component is a set
  and the set moves under the document.
- **Seating is proposed by the client and committed verbatim.** There is no
  matching rule in the endpoint — one would be a fourth pass whose worklist is
  exactly the files the other three refused. Re-seating a taken position *swaps*.
  `heldBy` per slot is constrained to the same release, because track 2 of one
  pressing is not track 2 of another.
- **All three outcomes are written, not just attribution**, or a file filed under
  an album keeps its identification refusal and never leaves the worklist.
- **`ReleaseWriter.ApplyTracksAsync` replaces a track list slot by slot.**
  `MediaFiles.TrackId` is `ON DELETE SET NULL`, so deleting rows and minting new
  ids silently strips the position off every file already filed under that
  release.
- **Nothing on disk is touched by any album decision.** An album is a catalogue
  fact.
- **Search needs a Solr index, which a mirror has not got.** Pasting an MBID or
  URL is a lookup and works either way; the endpoint detects one with a regex
  before searching.
- **A worklist id is not a media file id** (`recording:{guid}` vs a bare `Guid`).
  Sending the whole thing failed to deserialise *before the handler ran*, so no
  validation was reached and the response was `text/plain`. The integration tests
  could not have caught it — they POST media file ids directly. That is why
  `seating.ts` exists under `node --test`.

## Files, and serving bytes out of the library

The file manager exists because music and files disagree: a duplicate rip is one
album to MusicBrainz and two folders on disk. What is there comes from the disk;
what it means comes from the catalogue.

- **A rename updates the rows — that is the whole reason this is an endpoint and
  not `mv` plus a rescan.** To the reconciler a rename is a path that vanished
  and one that arrived, taking every AcoustID, link and human answer with it,
  reported as nothing at all. Rows first, then the bytes, in one transaction.
- **Nothing is deleted; trash is a move** to `Fonoteca:TrashPath`, **validated to
  sit outside the library root** — a trashed folder inside is re-scanned as new
  files *after* its rows are deleted. Separate from `ReplacedPath` although the
  mechanism is identical, or nothing distinguishes a mistaken click from a
  mistaken upgrade. Trash *does* delete the rows, unlike the scan, because here
  there is direct evidence: this process moved these files.
- **Upload is a raw body, not a form** — `IFormFile` buffers over 64 KB to a temp
  file, which for an album writes every byte twice and into RAM where `/tmp` is
  tmpfs. `Accepts<Stream>(…)` is a *Consumes* constraint, so every real upload
  came back 415 while the service-level test passed. Only the half the browser
  invented is sanitised: running the segment sanitiser over the browsed prefix
  drops `:`, which seven album folders here contain, minting a fresh duplicate.
- **`FilePreview` is an allowlist and the whole answer to serving bytes.**
  Anything unnamed is `application/octet-stream` as an attachment, everything
  carries `nosniff`, and two absences are load-bearing: **no `text/html`** and
  **no `image/svg+xml`**, both documents that run script. The art path walked
  around it and **it was a live XSS** — an embedded cover has no filename, only a
  MIME string a tagger wrote inside the file, and the check was
  `StartsWith("image/")`. `nosniff` is no defence: it stops a browser guessing a
  type, not honouring the one it was given.
- **Lexical containment is not containment.** `Resolve` compares strings, so
  `ln -s /etc secretdir` inside the library is under the root by every comparison
  and `/etc` on the disk. `EnsureNoLinkedDirectory` walks the chain (.NET has no
  `realpath`) and refuses, in `Resolve` rather than at the endpoints. **Symlinked
  *files* are still allowed** — the walk catalogues those deliberately.
- **No config flag gates any of this, deliberately.** `AllowFileMutation` and
  `AllowFileReplacement` exist because a *pass* can rewrite a library unattended;
  every act here is one click on one named entry, and the reversibility is in the
  trash.

**Album covers** are fetched once at 500px and served from `ReleaseCovers` under
an ETag — every tile used to hot-link the archive's front-250. An album with no
front is remembered as a row with no bytes; **an outage is not stored** and the
next view retries. Only an image the archive lists against *this* release may be
chosen, and an upload is held to the same raster allowlist with SVG refused.

**`…/folders/seed`** hands a folder to MusicBrainz's own "add release" form,
prefilled. Fonoteca writes nothing to MusicBrainz and cannot — their `/ws/2`
write API cannot create a release at all, so the supported path is the one Picard
uses. The action URL is deliberately not configurable: an edit against a mirror
is refused or lost at the next replication. Lengths are measured, never declared.

**`…/releases/{id}/fingerprints`** is the only *answer* this application gives a
provider. A separate interface (`IAcoustIdSubmission`) so something can be given
the ability to ask without the ability to assert; **nothing automatic may ever
call it**, because a pass submitting conclusions drawn from AcoustID's own
answers is feeding the provider its own opinion and reading the echo as
corroboration. Eligibility is `IdentityDecidedUtc IS NOT NULL` — what a
submission carries is the *link*, so the case worth most is a cluster naming
nothing that a person has just named.

## Acquisition

**The only part that spends money, and the only part that can take music away.**
ADR 0011 is the design record and is **proposed, not built**: a download today
copies bytes and writes nothing to the catalogue or the event log, so a Qobuz
album is found by the next scan and goes through all six passes. **Read the ADR
before touching this** — most of what looks like an oversight is written up there
as the next piece of work.

- **No staging directory, and removing it was the point.** Bytes go to a `.part`
  sibling — an extension `AudioFormats` does not recognise — and the final name
  exists only after the body is checked against `Content-Length` and atomically
  renamed, so a scan only ever sees whole files. What staging did buy is kept:
  the collision check against a folder the library already holds.
- **One album at a time**, about a double-click rather than throughput: two runs
  write the same paths and the loser corrupts the winner's files.
- **The upgrade list answers two questions from different evidence.** "Is this
  lossy?" a filename settles, because what Qobuz sells is FLAC. "Is this lossless
  file below what exists?" needs a decoder, so it is answered only where the probe
  pass has filled `MediaFile.Quality`; an unprobed lossless file reports
  `UpgradeReason.None` rather than a guess. Guessing from implied bitrate was
  measured and is worse than silence. `m4a` is absent from both sides of the
  lossless split and so reads as lossy, which is the error worth making.
- **Replacement runs after the download and measures what arrived** — what Qobuz
  advertises is a ceiling, and this is the last place to take a provider's word
  for anything when the consequence is deleting music.
- **Files move to `Fonoteca:ReplacedPath`, outside the library root**, or the
  scan catalogues the archive. **The catalogue is not touched**: removing rows
  for missing files is exactly what the scan does, and a second copy of that logic
  here would be a worse one. It refuses to archive anything it just wrote.
- **`AllowFileReplacement` is deliberately not `AllowFileMutation`.** Somebody
  who turned the first on to get their files tagged has not agreed to "may take
  an album away". With the flag off an upgrade still downloads and reports what it
  *would* have replaced.
- **Download and upgrade are one request**, and both halves come back even on a
  refusal — otherwise somebody cannot tell whether they have two copies or none.
- **`StagedFileName` clamps a segment to 200 UTF-8 *bytes*** (ext4's limit is 255
  bytes; a 96-character CJK title is 288). Layout is `Artist/Album/NN Title.ext`.
- **The Qobuz app secret is not the one in their bundle** — `.env.example` is the
  document. `QobuzClient.Signature` is `public static` solely so a test can pin
  the scheme, because every component of it fails identically: a 400 naming none
  of the four things that could have caused it.
- **A setting that binds to nothing is worse than no setting.**
  `MaxRequestsPerHour` was removed rather than implemented;
  `Fonoteca__Monitor__*` and `Fonoteca__Providers__Deezer__Arl` are still in that
  state.

**The three Acquire lists are answered out of the catalogue with no request to
Qobuz.** `items` is quality and its unit is the **album folder**, never the
release (keying attributed files on their release and the rest on their folder
put one album on the list 125 times). `incomplete` is completeness and its unit
*is* a release, because only a release has a track list to be short of — and
subtracting unmatched files is the only reason it is worth reading, since 128 of
175 looked short only because files in the folder had not been matched yet.
`missing` starts from a person: records credited to a **followed** artist that
nothing here sits under, cut by `Discography.IsGap` — **an explicit placeholder,
the only `TODO(you)` in the repo, with six tests prefixed `PlaceholderDecision_`
written to be rewritten. An open decision, not an oversight.**

**`Artist.Followed` and `ReleaseGroup.Monitored` are the only two facts in the
catalogue nothing can recompute** — everything else derives from audio, a
provider or a person's answer; these two are standing intent, and a rescan must
never touch either. Monitoring is a filter, not an instruction: a *first* browse
is the baseline and writes everything unmonitored, later browses mark what
appeared. Stated consequence, not fixed: unfollow for a year and re-follow, and a
year of releases is marked wanted. A discovered group is minted with a null
`Mbid`, which is a one-way door — `ReleaseGroup` has no barcode column, so
nothing can later recognise it as a MusicBrainz record.

## Providers

*These are somebody else's production systems and they enforce their limits by
blocking you.* Six `RequestGate`s, one per provider.

- **The rate gate sits underneath the resilience pipeline, not above it.**
  Handlers run outermost-first in registration order, so
  `AddStandardResilienceHandler()` goes on before `RateLimitedHandler`. Reversed,
  a 503 is answered with three immediate retries — the precise burst that turns a
  temporary rate limit into a lasting block.
- **`Query.DelayBetweenRequests = 0`, deliberately.** MetaBrainz throttles
  through a process-wide static that cannot differ between the official host and
  a mirror and cannot be substituted in a test. Removing that line without
  `RequestGate` in place is how the address gets blocked.
- **The attempt timeout is 30s, not the standard 10s** — a cold recording lookup
  was measured at 10.3s. **`MusicBrainzRequestIntervalMs` below 1000 is refused
  at startup** when the server is `musicbrainz.org`; going faster is what a
  mirror is for.
- **The User-Agent is set on the `HttpClient`, not per request**, because
  MetaBrainz composes the requests. It is the one header MusicBrainz blocks an
  address over, so no code path may be able to forget it.
- **Partial dates stay partial.** `ReleaseDate(Year, Month?, Day?)` rather than
  `DateOnly`: `NearestDate` turns "1969" into "1969-01-01", a claim nobody made,
  and the one that makes a reissue outrank an original.
- **AcoustID answers identity, MusicBrainz answers metadata.**
  `meta=recordingids sources`, never `meta=recordings` — AcoustID's titles are a
  stale mirror of MusicBrainz, and taking them means two paths to one fact ageing
  at different rates.
- **`Fonoteca:AcoustIdApiKey` is a flat key**, and `.env.example` said
  `Fonoteca__Providers__AcoustId__ApiKey` for months, which binds to nothing. If
  lookups are refused with a key that is plainly set, check the spelling first.
  **Missing credentials are not startup failures** — the app logs one line and
  refuses lookups *locally*, rather than sending an unidentified request.
- **`GET /api/system/musicbrainz` is not an `IHealthCheck` on `/health`** — that
  endpoint answers "should this process keep serving traffic", and the answer does
  not change when MusicBrainz is down. Cached 30s, because the probe queues at the
  same gate as identification work: an uncached probe costs a *turn*, and a tab
  left open on that card would halve a scan's throughput. `POLL_MS` and
  `MusicBrainzHealthProbe.CacheDuration` are a pair.
- `Fonoteca.Providers.Tests` builds the real service collection and replaces only
  the socket, so handler order, the gate and the User-Agent are the ones the
  application gets. The fixtures under `Responses/` are verbatim WS/2 documents;
  don't tidy them, the mess is the point.

**The mirror** (`scripts/musicbrainz-mirror.sh`, ADR 0006) is a wrapper over
`metabrainz/musicbrainz-docker` pinned by tag, in its own compose project so
`podman compose down -v` on the dev database cannot delete 100 GB. **No Solr, and
exactly one call needs it** — `SearchReleasesAsync`, behind the by-hand album
screen — reported as the provider error it is rather than as an empty result,
because "no albums match" and "this server cannot search" are otherwise
indistinguishable. Two upstream setup scripts fail silently under a non-TTY
stdin; `accept-terms` and `set-token` exist so they fail loudly instead.

## MCP

`/mcp` is streamable HTTP, stateless. Twenty-one tools: `library_status`,
`musicbrainz_health`, `open_questions`, `list_artists`, `get_artist`,
`list_releases`, `get_release`, `get_file`, `list_folder`, `folder_contents`,
`recording_candidates`, `component_candidates`, `search_releases`,
`release_slots`, `start_pass`, `cancel_pass`, `decide_recording`,
`decide_component`, `file_under_release`, `mark_folder_unreleased`,
`reopen_folder`.

- **Every tool is an existing endpoint handler, called directly** — `internal`
  rather than `private` for that reason — so validation, the gate and every
  refusal stay the endpoint's own. A tool with its own copy of a rule is the drift
  this file keeps warning about.
- **An agent's decision is not the owner's** (rule 4). **Adding a by-a-person
  outcome means adding its twin to `ByCaller`**, or the agent path throws.
- **Not offered:** the tag write at any scope, trash/move/upload, Qobuz.
- **`Fonoteca:McpToken` empty is a 404, read per request.** `Guard` is middleware
  rather than a filter so it sits in front of whatever `MapMcp` maps. It locks
  `/mcp` and nothing else: every `/api` route is as open as the port.
- **Lists default to 50** — a response lands in a model's context.

## Frontend

- `packages/tokens` — one TypeScript source emitting **both** `dist/tokens.css`
  and typed `var()` refs, so a token defined in one theme and missing from the
  other is a compile error. Built with `node src/build.ts && tsc -b`.
- `packages/ui` — **`react` + `react-dom` only**: no primitives library, no
  positioning library, no focus trap, no virtualizer (ADR 0003). `.tsx` plus a
  colocated `.module.css`, variants by `data-*`. React 19, so `ref` is an
  ordinary prop.
- `packages/api-client` — generated types plus a hand-written `fetch` wrapper,
  zero runtime dependencies. A route with a `{segment}` makes that argument
  **mandatory** via a rest tuple, because an optional one cannot be made required
  by a condition and a call that forgot `{ id }` would request a URL containing a
  literal `{id}`.
- `apps/web` — Vite + React 19, **TanStack Router** with routes in code
  (ADR 0008). The **server-state choice is still open**: `useApiQuery` has no
  cache, dedup or invalidation, so adopting TanStack Query stays a decision to
  take on evidence rather than one taken by association with the router's name.

`NAV_ITEMS` in `apps/web/src/navigation.ts` is the **one** list of sections, read
by the rail and by the command bar, so a new screen cannot reach one and miss the
other. `CommandBar` (ADR 0010) is a native `<dialog>` around a hand-built
combobox — the dialog does the focus trap, focus restoration, `Escape` and
top-layer stacking, which is why nothing in it touches the z-index scale.
Playback is one audio element and a hand-built slider (ADR 0009).
`HeartbeatService` puts a beat on `JobsHub` every five seconds, because without
one a client cannot tell "no jobs running" from "the connection died quietly".

**Three theme states, not two**: `data-theme="light"`, `data-theme="dark"`, and
**no attribute at all** (follow the system). The stylesheet guards its dark media
query with `:root:not([data-theme='light'])`. Don't collapse these into a boolean.

**The pure modules** (rule 5) are where a screen's rules actually live:
`seating.ts`, `identify.ts`, `files.ts`, `qobuz.ts`, `artistFacts.ts`, plus —

- **`workGroups.ts`** — **the link points at the movement, not at the symphony**:
  `Recording.WorkId` reaches a *leaf*, and the parent piece lives in a
  work-to-work relationship the catalogue does not store, so grouping falls back
  to the text before the first `": "`. **Consecutive runs, not a group-by** —
  sorting rows into work buckets would renumber the album on screen.
- **`listSearch.ts`** — sort, scope and filter live in the **URL**, not in
  component state, or pressing Back lands on an alphabetical list again. The
  option lists have one home so a third sort cannot miss the validator in
  `routes.tsx`.
- **`artistAlbums.ts`** — `billedOnRelease` is **three-state** (rule 3), and
  `band` carries the band's **id**, not the printed credit, because this library
  prints both "The Robert Cray Band" and "Robert Cray Band".
- **`certainty.ts`** — the one place attribution outcomes become English, as
  `openQuestions.ts` is for refusals. `Attributed` renders as **nothing at all**:
  a badge on every album trains the eye to skip the two that matter.

**Accessibility is enforced, not advised.** `pnpm test` runs **every story** as a
test in real Chromium with axe at `test: 'error'` — the enforcement half of a
zero-dependency design system where every ARIA role and focus behaviour is
hand-written. A real browser, not jsdom, because axe checks computed styles and
contrast. Chromium comes from `~/.cache/ms-playwright`; never run
`playwright install-deps` (it shells out to apt). **Adding a component means
adding stories, because that is what tests it.**

## Conventions

- **Warnings are errors** (`Directory.Build.props`), nullable enabled,
  `ConfigureAwait(false)` enforced (CA2007). Test projects relax
  `TreatWarningsAsErrors` only.
- **Central Package Management** — every version pinned in
  `apps/api/Directory.Packages.props`, including forward-pins of transitively
  vulnerable packages (NU1903 is a build failure). Never add a `Version=` to a
  `PackageReference`.
- **Migrations are generated code**; don't hand-edit them.
- **Two tag libraries on purpose** (ADR 0002): ATL.NET writes, TagLib# reads the
  file back to verify, disagreement aborts. ffmpeg must never write tags — it
  rewrites containers and drops non-standard frames. The implemented path adds
  two steps the ADR lacks: a length sanity check, which catches the one failure
  two agreeing readers cannot (perfect tags, no audio), and appending the undo
  entry *before* the commit so a crash over-records rather than under-records.
- **TypeScript 7 everywhere** except `tools/openapi-codegen`, pinned to 5.9
  because `openapi-typescript` drives a compiler API 7.0 doesn't ship (ADR 0005).
- Relative TS imports carry an explicit `.ts`/`.tsx` extension, which is what lets
  scripts run under `node` with no build step.
- Biome: single quotes, no semicolons, trailing commas, 100 cols. Skips
  `apps/api`, `openapi.json` and the generated `schema.d.ts`.
- `StartupTasks` is an `IHostedService`, not inline after `builder.Build()`,
  because `GetDocument.Insider` and `dotnet ef` construct the host without
  running it — inline startup code therefore ran during an ordinary
  `dotnet build`, connecting to PostgreSQL and migrating. Don't move startup work
  into `Program.cs`.

## Gotchas already paid for

- **AcoustID returns clusters, not answers, and one recording routinely spans
  several.** A lossless rip and a 128kbps rip can sit in unmerged clusters, so
  `0.957` and `0.939` is usually *one* answer arriving twice — **925 of the 951
  files `AcoustIdSelection` had withheld were this**. The rule compares each
  near-tied rival's *dominant recording* against the winner's. **Do not reach for
  `RecordingCandidates` here** — it collapses the other way and breaks 64 of 200
  files that identify confidently today. **And do not let `Sources` outvote a
  near-tied rival** — it decides live-against-studio by popularity. Both wrong
  turns are pinned by tests that name them.
- **The catalogue records outcomes but not evidence, and `Log.FileNotIdentified`
  is `Debug`.** Any question of the shape "what were those files actually?" is a
  script and minutes at the rate limit, not a SQL query. No endpoint clears
  `AcoustIdCheckedUtc` — re-asking after a rule change is a hand-written `UPDATE`.
- **Two stages joined by a bounded channel and a `Task.WhenAll` deadlock when the
  consumer dies.** The producer blocks in `WriteAsync` on a channel nobody will
  drain, so `WhenAll` waits forever and **never observes the consumer's
  exception** — no stack trace, no log line, the status endpoint still reporting
  "running". It cost twenty-five minutes of a live run and needed
  `dotnet-dump collect` then `dumpasync` to find. `IdentificationService` now runs
  the consumer through `ConsumeThenReleaseAsync`, whose `finally` cancels a linked
  CTS the producer's token comes from. **Any pipeline added here needs the same
  release**, and a test with one or two files cannot catch it — the producer
  finishes before the channel fills, which is why every existing test stayed green
  while the bug was live.
- **Tag parsers throw whatever they like, and forty files here make ATL throw
  `NullReferenceException`.** FLACs with a prepended ID3v2 tag; the trigger is
  specifically **unsynchronisation**. Ordinary fields still read, which is why
  stage A sails past and stage B falls over hundreds of files into a run.
  `TagReader` converts any parser failure into `TagReadFailedException`, and the
  pass reads that as **do not write to this file** — on these files ATL reports
  zero pictures where TagLib# finds one, so a forgiving read would let the artwork
  check compare nothing to nothing and pass. `Corpus.Id3PrefixedFlac` builds one,
  and the recipe is spelled out there because four near-miss variants do *not*
  reproduce it.
- **`pnpm api:test` hangs about one run in three, and the tests have already
  passed when it does.** The hang is in `Fonoteca.Integration.Tests` at process
  exit, *after* success is reported; `dotnet test` buffers output, so "no output"
  and "no progress" are indistinguishable. A stack shows
  `Xunit.v3.LocalTestProcess.WaitForExit` with **no Fonoteca frames on any
  thread**. It correlates with `LibraryScanEndpointTests`, where
  `IdentifyAfterScan` starts a pass, and **it predates the enrichment work**
  (reproduced at `a1767c1`), so a bisect will not find it.
  **The workaround, and it is a good one:** xUnit v3 projects are executables, so
  run the binary directly and skip the VSTest bridge —
  `./apps/api/tests/Fonoteca.Integration.Tests/bin/Debug/net10.0/Fonoteca.Integration.Tests`
  runs the whole suite (349 tests) in seconds rather than the several minutes
  `dotnet test` takes. Per-class runs are reliable for every class but that one.
  The flag is `-class` with a fully qualified name, not `--filter`.
- **A running `dotnet run` API stalls `dotnet test`.** The test build wants to
  write `Fonoteca.Api.dll`, the running host holds it, and MSBuild waits rather
  than failing. Stop the API first. An orphaned `dotnet test` keeps the lock after
  its shell is gone, and Testcontainers leaves its PostgreSQL behind (Ryuk is
  disabled): kill by pid and `podman rm -f` the stray `postgres:18-alpine`.
- **The collision the other way round poisons the running API.** Building while
  the dev host is up replaces `Fonoteca.Api.dll` under a process that has it
  mapped; everything already JITted keeps working, so the API looks healthy, and
  the *first* type it loads afterwards throws `BadImageFormatException` —
  arbitrarily far from the build that caused it, reading as a bug in that feature.
  Fix is `systemctl --user restart fonoteca-api.service`, the API unit alone, not
  `fonoteca.target`, which takes Vite's dep cache down with it.
- **Testcontainers over podman needs `DOCKER_HOST` at the user socket and Ryuk
  disabled, set from a *static constructor*.** `PostgreSqlBuilder.Build()`
  validates a runtime is reachable and runs in the fixture's field initialiser,
  so setting it from `InitializeAsync` is too late and every integration test
  fails with "Docker is either not running or misconfigured" on a host where
  podman is running perfectly.
- **Integration tests touching `MediaFiles` need their own database**
  (`PostgresFixture.CreateDatabaseAsync`), not just their own rows — the scan
  deletes rows whose files are not on disk, so a shared database means one test
  wipes another's fixtures.
- PostgreSQL 18 wants a single mount at `/var/lib/postgresql`, not
  `/var/lib/postgresql/data`. `podman compose up -d` returns before Postgres
  accepts queries; `dev.sh` polls the healthcheck because the API migrates on boot.
- `Microsoft.Extensions.Diagnostics.HealthChecks` ships in the shared framework —
  referencing it explicitly trips NU1510.
- **`ConfigureHttpJsonOptions` sets `NumberHandling = Strict`, and it has to stay
  that way.** ASP.NET's web defaults allow reading numbers from strings, and .NET
  10's OpenAPI generator reports that faithfully: every `int` comes out as
  `["integer", "string"]`, so the generated TypeScript types every count as
  `string | number` and arithmetic fails to compile. The workaround that suggests
  itself — hand-declaring the response shape in the component — throws away the
  contract the seam exists to enforce. `SystemEndpointTests` asserts numbers.
