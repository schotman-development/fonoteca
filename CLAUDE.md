# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A self-hosted music library manager for 100,000+ track libraries: catalogue and
dedupe, acquisition (Qobuz/Deezer), *arr-style upgrade monitoring, tag editing.

**Status: scaffold, two features.** The library scan walks the root and
reconciles `MediaFiles` with what is on disk. The **identification pass** then
fingerprints every file that has no AcoustID, looks it up, and writes the result
into the file's tags — so `Fonoteca.Tagging` is real, `IStagedWrite` is
implemented, and there *is* now a code path that modifies audio files. It is
behind `Fonoteca:AllowFileMutation`, which still defaults to `false`; with it off
the pass does everything except the write. **Read `docs/adr/0002` before touching
that path.**

Still missing: hashing, probing (`AudioQuality` is never populated), downloading,
MusicBrainz enrichment, and anything that reads the catalogue back out.

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
- `packages/api-client` — generated types plus a ~30-line hand-written `fetch`
  wrapper. Zero runtime dependencies.
- `apps/web` — Vite + React 19. Router and server-state library are still an
  open choice; the shell uses plain `useState`/`useEffect` on purpose.

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
