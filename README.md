# Fonoteca

A self-hosted music library manager for large libraries (100,000+ tracks).

Four pillars, none of them built yet:

1. **Catalogue and dedupe** — scan, hash, fingerprint, identify via AcoustID and
   MusicBrainz, detect duplicates and rank quality tiers
2. **Acquisition** — search and download from Qobuz and Deezer
3. **Upgrade monitoring** — watch for better versions, *arr-style
4. **Tag editor** — write corrected tags, artwork and MBIDs back to files

> **Status: scaffold, plus the first four slices of pillar 1.**
>
> `POST /api/library/scan` walks the library root and reconciles the catalogue's
> file list with what is on disk — path, size and modification time, and nothing
> more.
>
> `POST /api/library/identify` then fingerprints every file that has no AcoustID,
> looks it up, and writes the answer into the file's own tags. It returns `202`
> and a job id: AcoustID allows three requests a second, so a first pass over
> eight thousand files is tens of minutes. Progress arrives on `JobsHub`.
>
> **Tags are only written when `Fonoteca:AllowFileMutation` is enabled, and it
> defaults to `false`.** With it off the pass still fingerprints, still asks, and
> still records everything — it just does not write. Enabling it and running
> again then costs no further lookups, which makes the safe default a preview
> rather than a wasted hour. Take a backup before the first real write: the path
> is careful (see [ADR 0002](docs/adr/0002-two-tag-libraries.md)) and it is still
> the only operation here that can destroy anything.
>
> **`POST /api/library/enrich`** is the pass after that, and the one that makes
> a library browsable. It takes each identified file's *stored* fingerprint back
> to AcoustID for the MusicBrainz recording it names, then asks MusicBrainz who
> made it — so it opens no file, decodes nothing, and runs with the library
> volume unmounted. `GET /api/catalogue/artists` and the `/library` page are what
> come out: everyone credited on something you own, including the conductors,
> orchestras and composers a credit line never mentions.
>
> **`POST /api/library/attribute`** decides which release each file actually came
> from. It is the one pass that cannot work a file at a time: a recording of
> *Sloe Gin* appears on the album, on two compilations, on a remaster and on six
> regional pressings, and nothing about one file prefers any of them — eleven
> files filling eleven of eleven tracks prefer exactly one. So it decides files
> in *sets*, discovered by following shared candidate releases rather than by
> reading directories.
>
> **The folders are deliberately not consulted.** They are used afterwards, at
> `GET /api/catalogue/attribution`, as an independent second opinion: which
> folders were split across albums, which albums drew from several folders. Both
> sides are wrong sometimes — a folder named for a 1979 album can hold the audio
> of its 2015 remaster, which is exactly the kind of thing comparing measured
> durations against each edition's printed ones can tell.
>
> `GET /api/catalogue/releases` and the `/library/releases` page are what come
> out, including the tracks you are missing. Nothing hashes, probes or downloads.

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
infra/
  musicbrainz/          compose override for the optional local mirror
docs/
  adr/                  architecture decision records
  toolchain.md          Tier 0 setup
  musicbrainz-mirror.md running a local MusicBrainz mirror
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

Optional, and nothing else depends on it:

```sh
./scripts/musicbrainz-mirror.sh setup     # a local MusicBrainz mirror (~100 GB)
./scripts/musicbrainz-mirror.sh status    # replication position, /ws/2 probe
./scripts/musicbrainz-mirror.sh help
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
| [0006](docs/adr/0006-musicbrainz-mirror.md) | Mirror MusicBrainz locally, without a search index — and don't try to mirror AcoustID |
| [0007](docs/adr/0007-identification-in-process.md) | Identification runs in-process, not on a durable queue — the catalogue *is* the worklist |
| [0008](docs/adr/0008-tanstack-router.md) | TanStack Router, routes in code — a route rename should be a build failure |
| [0011](docs/adr/0011-the-provider-is-an-authority.md) | A provider is an authority — a Qobuz download states what it delivered, and the download is where that is believed |

## Credentials

Neither is required to start, and neither is needed to scan or browse.
`Fonoteca:AcoustIdApiKey` is what identification needs; the MusicBrainz contact
is for the enrichment that follows it. A lookup attempted without either is
refused locally, with a message naming the setting, rather than sent and
rejected.

**The key binds as `Fonoteca:AcoustIdApiKey` — flat, not nested.** `.env.example`
shipped `Fonoteca__Providers__AcoustId__ApiKey` for months, which binds to
nothing and which nothing noticed because nothing called AcoustID.

| Setting | Where to get it |
| --- | --- |
| `Fonoteca:AcoustIdApiKey` | free for non-commercial use at [acoustid.org/new-application](https://acoustid.org/new-application) |
| `Fonoteca:MusicBrainzContact` | a URL or email address of your own — MusicBrainz blocks clients that do not identify themselves |

`Fonoteca:MusicBrainzServer` points at a mirror if you run one. That is the
supported way to go faster than one request per second; lowering
`Fonoteca:MusicBrainzRequestIntervalMs` against the public instance is refused
at startup, because the unsupported way ends in a blocked address.

## Running a MusicBrainz mirror

Optional, and worth it once identification is doing real work: one request per
second means a 100,000-track library takes more than a day per pass, and you pay
it again on every re-scan.

```sh
./scripts/musicbrainz-mirror.sh setup
```

An 8 GB download that expands to ~100 GB of Postgres, a few hours (mostly the
import, not the download), a free MetaBrainz access token, and it replicates
itself daily. No *search* index — Fonoteca only ever looks things up by MBID,
and Solr would be another 250 GB that replication does not maintain.

Full runbook in [`docs/musicbrainz-mirror.md`](docs/musicbrainz-mirror.md);
the reasoning, including why AcoustID does *not* get the same treatment, in
[ADR 0006](docs/adr/0006-musicbrainz-mirror.md).

## Still open

- **Server-state for `apps/web`.** The router question is settled (ADR 0008);
  this one is not. `useApiQuery` is the shared version of the plain
  `useState`/`useEffect` the panels already used — no deduplication, no
  background refetch, no invalidation — so adopting TanStack Query stays a
  decision to take on evidence rather than one taken by association.
- **The catalogue virtualizer.** Excluding `@tanstack/react-virtual` from the
  design system means writing one. The largest piece of unplanned frontend work.
- **Anthologies of licensed catalogue stay unattributed.** The attribution pass
  refuses rather than guesses, and that is where the refusals land: one 1950s
  recording sits on its original album and on twenty compilations, all fitting
  equally badly, and nothing chooses between them. They are visible as
  `NoConfidentFit` in `GET /api/catalogue/attribution`, and a track with no album
  falls back to naming its folder — labelled as a folder, not dressed up as an
  album.
- **Run history is not persisted.** `LastCompleted` for the scan and both passes
  is an in-memory field, forgotten on restart. Making it
  durable means deciding what a run *is* as an entity, which ADR 0007 defers
  along with the job queue.
