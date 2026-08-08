# Fonoteca

A self-hosted music library manager for large libraries (100,000+ tracks).

Four pillars, none of them built yet:

1. **Catalogue and dedupe** — scan, hash, fingerprint, identify via AcoustID and
   MusicBrainz, detect duplicates and rank quality tiers
2. **Acquisition** — search and download from Qobuz and Deezer
3. **Upgrade monitoring** — watch for better versions, *arr-style
4. **Tag editor** — write corrected tags, artwork and MBIDs back to files

> **Status: scaffold, plus the first slices of pillar 1.** The library scan
> exists: `POST /api/library/scan` walks the library root and reconciles the
> catalogue's file list with what is on disk — path, size and modification time,
> and nothing more. The AcoustID and MusicBrainz adapters exist too, behind
> interfaces in `Fonoteca.Domain`, rate-limited and verified against the live
> services — but nothing calls them yet, because there is no fingerprinting pass
> to feed them. Nothing hashes, fingerprints, downloads or writes tags.
> `Fonoteca:AllowFileMutation` defaults to `false` and no code path writes to an
> audio file.

## Layout

```
apps/
  api/                  .NET 10 — own toolchain, outside the pnpm graph
    src/
      Fonoteca.Api/         minimal API, OpenAPI, SignalR
      Fonoteca.Domain/      PURE — entities, matching, quality ranking.
                            References nothing: no EF, no HttpClient, no System.IO
      Fonoteca.Ingest/      walk, hash, probe, fingerprint
      Fonoteca.Tagging/     read/write, dry-run diff, undo journal
      Fonoteca.Providers/   qobuz | deezer | musicbrainz | acoustid
      Fonoteca.Jobs/        queue abstraction, workers
      Fonoteca.Data/        EF Core, migrations
    tests/
  web/                  Vite 8 + React 19 + TypeScript 7
packages/
  tokens/               design tokens -> CSS custom properties + typed refs
  ui/                   design system + Storybook (react + react-dom only)
  api-client/           generated from openapi.json, zero runtime deps
  tsconfig/             shared TypeScript bases
tools/
  openapi-codegen/      pinned TS 5.9 for the generator; see ADR 0005
docs/
  adr/                  architecture decision records
  toolchain.md          Tier 0 setup
```

## Getting started

Toolchain first — see [`docs/toolchain.md`](docs/toolchain.md). Nothing is
installed with apt.

```sh
source ~/.local/opt/env.sh
corepack enable
pnpm install
pnpm dev
```

`pnpm dev` brings up PostgreSQL in podman, waits for it to be genuinely healthy,
then runs the API, the web client and Storybook together, shutting them all down
on Ctrl-C.

| | |
| --- | --- |
| web | http://localhost:5173 |
| api | http://localhost:5088 |
| OpenAPI | http://localhost:5088/openapi/v1.json |
| health | http://localhost:5088/health |
| Storybook | http://localhost:6006 |

## Commands

```sh
pnpm build            # tokens -> ui -> api-client -> web, in topological order
pnpm typecheck
pnpm lint             # biome
pnpm test             # JS side, incl. every story through axe in a real Chromium
pnpm api:build
pnpm api:test         # xUnit; integration tests start a real PostgreSQL 18
pnpm gen:api          # regenerate the TypeScript client from openapi.json
pnpm gen:api:check    # fail if the committed client has drifted
pnpm storybook
```

## How the two halves connect

`apps/api` is deliberately outside the pnpm workspace. The single seam is the
OpenAPI document:

1. Building `Fonoteca.Api` writes `openapi.json` to the repository root.
2. `pnpm gen:api` turns it into `packages/api-client/src/schema.d.ts`.
3. That file is **committed**, and CI regenerates it and fails on a diff.

So a backend change that the frontend has not caught up with breaks the build,
rather than returning `undefined` in a browser.

## Decisions worth knowing before you change things

| | |
| --- | --- |
| [0001](docs/adr/0001-dotnet-backend.md) | .NET 10 — every serious library manager (Roon, Lidarr, Jellyfin) is .NET; the Python tools in this space are manual taggers |
| [0002](docs/adr/0002-two-tag-libraries.md) | Two tag libraries: ATL.NET writes, TagLib# verifies, disagreement aborts |
| [0003](docs/adr/0003-custom-design-system.md) | Custom design system, `react` + `react-dom` only — a11y is ours, and axe enforces it in CI |
| [0004](docs/adr/0004-musicbrainz-shaped-entity-graph.md) | MusicBrainz-shaped entity graph, because it cannot be retrofitted |
| [0005](docs/adr/0005-typescript-7.md) | TypeScript 7, with the OpenAPI generator isolated on 5.9 |

## Credentials

Neither is required to start, and neither is needed to scan or browse. Both are
needed to identify anything, and a lookup attempted without them is refused
locally with a message naming the setting.

| Setting | Where to get it |
| --- | --- |
| `Fonoteca:AcoustIdApiKey` | free for non-commercial use at [acoustid.org/new-application](https://acoustid.org/new-application) |
| `Fonoteca:MusicBrainzContact` | a URL or email address of your own — MusicBrainz blocks clients that do not identify themselves |

`Fonoteca:MusicBrainzServer` points at a mirror if you run one. That is the
supported way to go faster than one request per second; lowering
`Fonoteca:MusicBrainzRequestIntervalMs` against the public instance is refused
at startup, because the unsupported way ends in a blocked address.

## Still open

- **Router and server-state for `apps/web`.** The zero-dependency rule was
  scoped to the design system; the app shell currently uses plain `useState` and
  `useEffect` so this stays an open choice rather than being settled by default.
- **The catalogue virtualizer.** Excluding `@tanstack/react-virtual` from the
  design system means writing one. The largest piece of unplanned frontend work.
- **`.env.example`** predates this scaffold and does not describe the current
  configuration; `appsettings.json` and `compose.yaml` do. Needs reconciling.
