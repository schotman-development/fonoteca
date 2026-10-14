<h1 align="center">Fonoteca</h1>

<p align="center">
  <strong>A self-hosted music library manager for libraries of 100,000 tracks and more.</strong>
</p>

<p align="center">
  Identify every file by its sound, file each folder under the album it really is,
  measure what the audio actually holds, write it all back into the tags,
  and buy the upgrades.
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="React 19" src="https://img.shields.io/badge/React-19-61DAFB?logo=react&logoColor=black">
  <img alt="PostgreSQL 18" src="https://img.shields.io/badge/PostgreSQL-18-4169E1?logo=postgresql&logoColor=white">
  <img alt="OpenSubsonic" src="https://img.shields.io/badge/OpenSubsonic-server-E76F00">
  <img alt="MCP" src="https://img.shields.io/badge/MCP-server-555555">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

<p align="center">
  <a href="#features">Features</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="#configuration">Configuration</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#development">Development</a> ·
  <a href="#roadmap">Roadmap</a>
</p>

---

A large library is a mess nobody can see: folders named for an album they do not
hold, the same record ripped three times, MP3s where FLACs exist, 16-bit audio
padded into 24-bit files, albums two tracks short. Fonoteca reads the audio rather
than the names, builds a catalogue shaped like MusicBrainz, and puts the answers
back into your files, where every player can read them.

- **It refuses rather than guesses.** A wrong album is worse than a missing one
  and far harder to notice. Whatever a pass cannot prove lands on the Identify
  screen as a question for you.
- **It is a dry run until you say otherwise.** No pass writes to a file until
  `Fonoteca__AllowFileMutation` is on. With it off every pass still does
  everything except the write.
- **Every write is verified and can be undone.** One tag library writes, a second
  reads the result back, and any disagreement aborts. Undo steps an album folder
  back one edit at a time.
- **Your library stays plain files.** Standard tags under Picard's names, folders
  named by a pattern, playlists as `.m3u8`. Copy the library elsewhere and the
  answers come with it.

> [!NOTE]
> Fonoteca is in active development and runs from source. There is no packaged
> release or container image yet.

## Features

### Identify by sound

- **Acoustic fingerprinting.** Chromaprint fingerprints looked up on AcoustID,
  resolved to a MusicBrainz recording, and the AcoustID written into the file.
- **A real catalogue.** Artists, works, recordings, albums and editions, the way
  MusicBrainz models them, so a symphony is filed under its conductor and
  orchestra as well as the composer the credit line names.
- **Full credits.** Producers, engineers, every player with their instrument,
  lyricists and arrangers, plus genres and ISRCs.
- **Artist pages.** Portraits, articles, banners and discographies.

### Albums from folders

- **One folder, one album.** Each album folder is matched to the album that
  explains more than half of it. Its name is never believed: a folder named for
  the 1979 album can hold the 2015 remaster.
- **Editions only on proof.** A pressing is claimed only when the track count
  matches and every track is within 100 ms of its printed length.
- **What you are missing.** The album page combines every edition's track list,
  so "the deluxe has two tracks you do not" is a fact on screen.
- **By-hand matching.** Everything the passes refused, one album folder at a
  time: search MusicBrainz, paste an MBID, seat each file on its track.

### Know what you have

- **Integrity.** Every file is decoded once, which tells its real format and
  quality and flags corrupt files.
- **Loudness and dynamics.** EBU R128 loudness as ReplayGain 2.0 for track and
  album, the DR figure, clipping and silence.
- **Real bit depth.** 16-bit audio in a 24-bit FLAC is a CD rip, and the upgrade
  lists rank it as one.

### Write it back

- **Tags.** Titles, artists, MusicBrainz ids, genres, ISRCs, credits, ReplayGain
  and DR, written for library, artist or album at a time.
- **Artwork.** The front cover beside the tracks, booklets, and Apple Music's
  looping album videos.
- **Folder naming.** Album folders renamed to a pattern such as
  `{albumartist}/{album}[ ({year})]/[{disc}-]{track} - {title}`, with symlinks
  under every other billed artist of a collaboration.
- **Tag editor.** Your own corrections, kept beside the catalogue's and always
  winning over them.

### Browse and listen

- Artists, albums, tracks, composers, compositions and the folders on disk.
- Playback in the browser, a keyboard command bar, light and dark themes.
- **Playlists as files.** Static `.m3u8` playlists in `Playlists/` that Navidrome
  and Roon read too, and smart playlists built from nested any/all rules over
  song, people, album, artist and file.
- **File manager.** Rename and move with the catalogue kept in step, upload, and
  a trash that is a move, never a delete.

### Acquire

- **Search and download** with your own store subscription. Every purchase is a
  click; nothing buys on its own.
- **Upgrade lists.** What is lossy, what is below the best available quality,
  albums with tracks missing, and records by artists you follow that you do not
  have.
- **Replacement.** Downloading an album you already hold replaces it only when
  the new copy measures better, or as good with more tracks, and the old files go
  to an archive outside the library.

### Integrations

- **OpenSubsonic** at `/rest`: Symfonium, Amperfy, substreamer, Feishin,
  play:Sub and DSub browse and play the library.
- **MCP** at `/mcp`: an AI agent can read the catalogue, run passes and answer
  open questions, recorded as the agent's decisions and never as yours. It
  cannot write tags in bulk, move files or buy anything.
- **A local MusicBrainz mirror**, one script away, for libraries where one
  request a second is too slow.

## Quick start

### Requirements

| Tool | Version | Used for |
| --- | --- | --- |
| .NET SDK | 10 | the API |
| Node.js | 24 | the web client |
| pnpm | 11, via `corepack` | the workspace |
| ffmpeg and ffprobe | 8 | decoding and measuring |
| fpcalc (Chromaprint) | 1.6 | fingerprints |
| podman with compose | 4.9 | PostgreSQL 18, in a container |
| Playwright Chromium | | `pnpm test` only |

[`docs/toolchain.md`](docs/toolchain.md) installs all but podman into your home
directory, checksum-verified, without a package manager.

### Run

```sh
git clone https://github.com/schotman-development/fonoteca.git
cd fonoteca
cp .env.example .env          # set LibraryPath, AcoustIdApiKey, MusicBrainzContact
source ~/.local/opt/env.sh    # if you installed the tools per docs/toolchain.md
systemctl --user enable --now podman.socket
corepack enable
pnpm install
pnpm dev
```

`pnpm dev` starts PostgreSQL, waits until it answers queries, then runs the API,
the web client and Storybook together, and stops them all on Ctrl-C.

| | |
| --- | --- |
| Web | http://localhost:5173 |
| API | http://localhost:5088 |
| OpenAPI | http://localhost:5088/openapi/v1.json |
| Health | http://localhost:5088/health |
| Storybook | http://localhost:6006 |

Open **Settings** and run a scan. With the example settings, identification
follows it on its own; enrich, attribute, probe and analyse are a button each
on the same page. Write tags when you are happy with what the catalogue says,
and take a backup before the first real write.

> [!WARNING]
> Only `/rest` and `/mcp` check credentials. Every `/api` route, including the
> file manager and the passes, is as open as the port. Do not expose it beyond
> your own network without something in front of it.

## Configuration

Settings live in `.env`. [`.env.example`](.env.example) is the documentation:
every key carries the reason it exists and what happens without it. The ones
to know first:

| Setting | What it does |
| --- | --- |
| `Fonoteca__LibraryPath` | The music library root. |
| `Fonoteca__AllowFileMutation` | Lets passes write to files. Off by default, which makes every run a dry run. |
| `Fonoteca__AllowFileReplacement` | Lets a download replace an album you hold. Off by default. |
| `Fonoteca__AcoustIdApiKey` | Needed to identify. Free for non-commercial use at [acoustid.org](https://acoustid.org/new-application). |
| `Fonoteca__MusicBrainzContact` | Your URL or email address. MusicBrainz blocks clients that do not identify themselves. |
| `Fonoteca__MusicBrainzServer` | Point at a mirror to go faster than one request a second. |
| `Fonoteca__FileNaming` | The folder and file naming pattern. |
| `Fonoteca__TrashPath`, `Fonoteca__ReplacedPath` | Where trashed and replaced files go. Must be outside the library. |
| `Fonoteca__SubsonicPassword` | Turns on `/rest`. |
| `Fonoteca__McpToken` | Turns on `/mcp`. |

Nothing is required to start, scan or browse. A feature whose credentials are
missing refuses locally, naming the setting, rather than sending a request.

## How it works

Seven passes, each a button on the Settings page. Every pass after the scan keeps
its worklist in the catalogue, so a stopped run picks up where it left off.

| Pass | What it does |
| --- | --- |
| **Scan** | Walks the library and reconciles the catalogue with the disk. Never removes anything it is unsure is gone. |
| **Identify** | Fingerprints each new file and asks AcoustID which recording it is. |
| **Enrich** | Asks MusicBrainz about each identified recording and builds the catalogue. Opens no files. |
| **Attribute** | Decides which album each album folder is, and which edition only on proof. |
| **Probe** | Decodes every file once for quality and integrity. |
| **Analyse** | Measures loudness, dynamic range, real bit depth, clipping and silence. |
| **Tag write** | Writes the catalogue back into the files and renames the folders. Never automatic, except for a download's own album folder. |

Everything before the tag write lives in PostgreSQL. The tag write is what makes
the answers portable.

The write path is the one part that can destroy something a rescan cannot
rebuild, so it is the most careful: ATL.NET writes into a staged sibling,
TagLib# reads it back, a length check catches a file that lost its audio, the
undo entry is journalled before the swap, and the swap is atomic.
[ADR 0002](docs/adr/0002-two-tag-libraries.md) is the design record.

### Running a MusicBrainz mirror

One request a second means a 100,000-track library takes more than a day per
pass. A local mirror removes the limit:

```sh
./scripts/musicbrainz-mirror.sh setup
```

An 8 GB download that expands to about 100 GB of PostgreSQL, a few hours of
import, a free MetaBrainz access token, and it replicates itself daily. It has no
search index, so the album search on the Identify screen needs the public server;
pasting an MBID works either way. The runbook is
[`docs/musicbrainz-mirror.md`](docs/musicbrainz-mirror.md).

## Development

A .NET 10 API and a pnpm workspace with Vite and React 19, joined only by the
OpenAPI document. Building the API writes `openapi.json`, `pnpm gen:api` turns it
into a typed client, and that client is committed, so an API change the frontend
has not caught up with fails the build rather than the browser.

```
apps/
  api/                  .NET 10, outside the pnpm workspace
    src/
      Fonoteca.Api/         minimal API, OpenAPI, SignalR, OpenSubsonic, MCP
      Fonoteca.Domain/      pure rules: entities, matching, quality ranking
      Fonoteca.Ingest/      walk, probe, fingerprint
      Fonoteca.Tagging/     verified write path and undo journal
      Fonoteca.Providers/   AcoustID, MusicBrainz, Cover Art Archive, Wikidata,
                            TheAudioDB, Apple Music
      Fonoteca.Data/        EF Core and migrations
    tests/
  web/                  Vite, React 19, TanStack Router
packages/
  tokens/               design tokens as CSS custom properties and typed refs
  ui/                   design system and Storybook, react and react-dom only
  api-client/           generated from openapi.json, no runtime dependencies
docs/
  adr/                  architecture decision records
```

```sh
pnpm build            # tokens, ui, api-client, web
pnpm typecheck
pnpm lint             # biome
pnpm test             # every story through axe in a real Chromium, plus node --test
pnpm api:build        # also regenerates openapi.json
pnpm api:test         # xUnit; integration tests start a real PostgreSQL 18
pnpm gen:api          # openapi.json to the TypeScript client
pnpm gen:api:check    # fail if the committed client has drifted
```

Accessibility is enforced rather than advised: the design system has no
third-party components, and every story runs through axe in a real browser as
part of `pnpm test`.

### Design decisions

| | |
| --- | --- |
| [0001](docs/adr/0001-dotnet-backend.md) | .NET 10 for the backend |
| [0002](docs/adr/0002-two-tag-libraries.md) | Two tag libraries: ATL.NET writes, TagLib# verifies, disagreement aborts |
| [0003](docs/adr/0003-custom-design-system.md) | A design system with no third-party runtime dependencies |
| [0004](docs/adr/0004-musicbrainz-shaped-entity-graph.md) | An entity graph shaped like MusicBrainz, because it cannot be retrofitted |
| [0005](docs/adr/0005-typescript-7.md) | TypeScript 7, with the OpenAPI generator isolated on 5.9 |
| [0006](docs/adr/0006-musicbrainz-mirror.md) | Mirror MusicBrainz locally, without a search index |
| [0007](docs/adr/0007-identification-in-process.md) | Identification runs in-process; the catalogue is the worklist |
| [0008](docs/adr/0008-tanstack-router.md) | TanStack Router, routes in code |
| [0009](docs/adr/0009-one-audio-element-and-a-hand-built-slider.md) | One audio element and a hand-built slider for playback |
| [0010](docs/adr/0010-command-bar-native-dialog-hand-built-combobox.md) | The command bar: a native dialog around a hand-built combobox |
| [0011](docs/adr/0011-the-provider-is-an-authority.md) | A provider is an authority, and a download is where it is believed |
| [0012](docs/adr/0012-opensubsonic.md) | OpenSubsonic, and the two things a client may browse |
| [0013](docs/adr/0013-an-album-folder-is-one-album.md) | An album folder is one album; an edition only on proof |
| [0014](docs/adr/0014-album-motion-from-public-pages.md) | Album motion artwork, from Apple Music's public pages |
| [0015](docs/adr/0015-album-booklets.md) | Album booklets |

## Roadmap

- **Duplicate detection.** Not built yet.
- **Server-state caching in the web client.** An open decision: TanStack Query
  is to be adopted on evidence, not by association with the router.
- **A list virtualizer** for the catalogue screens, written in-house.
- **Persisted run history.** The last run of each pass is held in memory and
  forgotten on restart.
- **Anthologies of licensed catalogue stay unattributed**, by design: one 1950s
  recording on its original album and twenty compilations fits all of them
  equally badly, and the pass refuses rather than guesses.
- **No Deezer provider**, and that absence is a decision.

## Acknowledgements

Fonoteca stands on open data and open source: [MusicBrainz](https://musicbrainz.org),
[AcoustID](https://acoustid.org) and [Chromaprint](https://acoustid.org/chromaprint),
the [Cover Art Archive](https://coverartarchive.org), [Wikidata](https://www.wikidata.org),
[TheAudioDB](https://www.theaudiodb.com), [FFmpeg](https://ffmpeg.org),
[ATL.NET](https://github.com/Zeugma440/atldotnet) and
[TagLib#](https://github.com/mono/taglib-sharp).

## License

[MIT](LICENSE)
