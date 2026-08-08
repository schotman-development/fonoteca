# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A self-hosted music library manager for 100,000+ track libraries: catalogue and
dedupe, acquisition (Qobuz/Deezer), *arr-style upgrade monitoring, tag editing.

**Status: scaffold plus one feature.** The library scan exists — it walks the
library root and reconciles `MediaFiles` with what is on disk, recording path,
size and modification time. Nothing else does: no hashing, probing,
fingerprinting, identification, downloading or tag writing.
`Fonoteca:AllowFileMutation` defaults to `false` and no code path writes to an
audio file. Read `README.md` and `docs/adr/` before adding one.

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
is seconds, even at 100k. The moment a pass opens files it belongs behind
`IJobQueue` with progress on `JobsHub`, and `LibraryScanService` is the thing
that gets replaced then, not the foundation it grows on.

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
  write tags — it rewrites containers and can drop non-standard frames.
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
