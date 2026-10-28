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

**Seven passes, seven screens, one thing that can spend money.**

| pass | does | service |
| --- | --- | --- |
| scan | walks the root, reconciles `MediaFiles` with the disk | `Api/Library/LibraryScanService.cs` |
| identify | `fpcalc` → AcoustID → writes the tag | `Api/Library/IdentificationService.cs` |
| enrich | identities into a catalogue, stage by stage | `Api/Library/EnrichmentService.cs` |
| attribute | which album each *folder* is, and its pressing only on proof | `Api/Library/ReleaseAttributionService.cs` |
| probe | `ffprobe -count_frames` — quality and integrity in one decode | `Api/Library/ProbeService.cs` |
| analyse | `ffmpeg` analysis filters — loudness, DR, real bit depth, silence, clipping | `Api/Library/AnalysisService.cs` |
| tag write | the whole catalogue back into the files | `Api/Library/TagWriteService.cs` |

Six are a `POST`/`GET`/`DELETE` trio under `/api/library` taking
`LibraryWorkGate`. **The scan is neither**: it cannot be cancelled, and it
guards itself with a private interlocked flag rather than the gate — so a scan
can arrive in the middle of anything. Nothing chains one pass into another
except `Fonoteca:IdentifyAfterScan`. The tag write is the only one nobody may
automate — but a download writes its own album folder (ADR 0011, the owner's
choice).

**Dedupe is a list, not a pass** (`Duplicates`, the Duplicates screen): an
album held in more than one album folder, and a song held twice in one beyond
the slots its pressing prints. A song on its album and again on a best-of is
two records, not a duplicate. Nothing goes but by a person's click, through
the file manager's trash — the owner's choice over keeping the best
automatically. "Better" is marked only where a copy wins on every count.

## Six rules the whole codebase turns on

Almost every sharp edge here is one of these. Reading them first saves reading
the rest.

1. **A worklist keys on "we asked", never on "we have an answer".**
   `AcoustIdCheckedUtc`, `RecordingLookupUtc`, `ReleaseLookupUtc`,
   `Artists.LookupUtc`, `PortraitLookupUtc`, `DiscographyLookupUtc`,
   `LastVerifiedUtc`, `Releases.ProviderCheckedUtc`, `Recordings.DescribedUtc`,
   `ReleaseGroups.GenresLookupUtc`, `Works.PartOfLookupUtc`, `Works.DescribedUtc`,
   `Releases.DescribedUtc`, `TempoAndKeyMeasuredUtc`. A library is full of things the provider has never heard
   of; keyed on the identifier, every one is re-asked forever and the worklist
   never empties. The stamp goes on when the answer is "no such thing" too, and
   is left null **only** when the lookup did not happen. Where a source gains
   answers over time — a portrait, an article, a banner, a cover, a song's words — a stamp that
   found nothing expires after a week (`NothingFoundRetryAfter`,
   `CoverRetryAfter`); one that found something never does. Corollary: widening a
   lookup is invisible to everything already stamped, so re-asking is a
   hand-written `UPDATE` — narrow it to the rows that need it.

2. **A write changes the bytes, so the new size and mtime go in the same
   `SaveChanges`.** Otherwise the next scan reads the file as modified: a hash
   read at best, and where the row has no `AudioHash` every derived column
   discarded, including what was just written. Never "fix" this by forging the
   old mtime back: the file genuinely changed.

3. **Absence of evidence is not evidence of deletion.** The scan counts
   unreadable directories rather than ignoring them, removes nothing when it
   sees zero files against a full catalogue, does not follow directory symlinks,
   and floors timestamps to whole microseconds (`timestamptz` keeps microseconds,
   .NET keeps 100ns ticks). Same shape everywhere: a missing credit row means the
   catalogue does not know, not that the answer is no.

4. **A rule's answer and a person's answer are different facts.** Folding them
   loses any ability to report on the rule. `IdentifiedByPerson`,
   `RejectedByPerson`, `LinkedByPerson`, `AttributedByPerson`, `AlbumByPerson`,
   `NoReleaseByPerson`, `Unreleased`, `ReopenedByPerson` — each with an
   `…ByAgent` twin for `/mcp`, all listed in `ByCaller`. `IdentityDecidedUtc` and
   `ReleaseDecidedUtc` are separate *columns* from the outcome, because clearing
   a lookup stamp by hand must not hand answered files back to the rule that
   could not answer them. `AcoustIdDecidedUtc` guards identification alone, so a
   download can settle the recording and still have its audio fingerprinted
   (ADR 0011); every person's decision writes both.

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
Fonoteca.Providers  acoustid | musicbrainz | qobuz | coverart | wikidata | audiodb | applemusic | lrclib | radiobrowser
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
| attribute | `Domain/Identification/ReleaseAttribution.cs`, `EditionProof.cs`, `FolderOrder.cs`, `ReleaseFit.cs`, `Catalogue/AlbumFolder.cs` (and its TS twin `ALBUM_FOLDER_DEPTH` in `seating.ts` — change both), `Catalogue/Editions.cs`, `Catalogue/TrackTitles.cs` | `Api/Matching/ComponentCandidates.cs` |
| probe | `Domain/Abstractions/IAudioProbe.cs` | `Ingest/FfprobeAudioProbe.cs` |
| tag write | `Domain/Catalogue/CatalogueTags.cs`, `FileNaming.cs` | `Tagging/TagWriter.cs`, `Tagging/CatalogueTagFields.cs`, `Api/Library/TagWriteService.Rename.cs` |
| by-hand matching | `web/src/pages/seating.ts`, `identify.ts` | `Api/Endpoints/CatalogueEndpoints.{AlbumMatching,Identify}.cs` |
| files & previews | `Domain/Catalogue/FolderRollup.cs`, `FilePreview.cs` | `Api/Library/FileManagerService.cs` |
| covers | — | `Providers/CoverArt/`, `Api/Endpoints/CatalogueEndpoints.Cover.cs` |
| motion artwork | — | `Providers/AppleMusic/AppleMusicMotions.cs`, `EnrichmentService.MotionAsync`, `TagWriteService.EnsureMotionAsync` |
| booklets | — | `Providers/CoverArt/AlbumBooklets.cs`, `EnrichmentService.BookletsAsync`, `TagWriteService.EnsureBookletAsync` |
| lyrics | `Domain/Catalogue/LyricsText.cs` | `Providers/Lrclib/LrclibLyrics.cs`, `EnrichmentService.LyricsAsync`, `TagWriteService.TrashLyricsAsync`, `Api/Subsonic/SubsonicEndpoints.Lyrics.cs` |
| acquisition | `Domain/Acquisition/UpgradeScan.cs`, `UpgradeReplacement.cs`, `StagedFileName.cs`, `Discography.cs` | `Api/Acquisition/{QobuzDownloadService,AlbumReplacementService}.cs` |
| monitoring | `Domain/Catalogue/ReleaseTitleMatch.cs` | `Providers/Qobuz/QobuzReleaseDiscovery.cs` |
| playlists | `Domain/Catalogue/PlaylistFile.cs`, `SmartRules.cs` (and its TS twin `smartRules.ts`) | `Api/Library/PlaylistFiles.cs`, `Api/Library/SmartQuery.cs`, `Api/Endpoints/CatalogueEndpoints.Playlists.cs` |
| radio | `Domain/Catalogue/RadioStationFile.cs`, `PageIcons.cs` | `Api/Subsonic/SubsonicEndpoints.Radio.cs`, `Providers/Radio/StationLogos.cs` |
| duplicates | `Domain/Catalogue/Duplicates.cs` | `Api/Endpoints/CatalogueEndpoints.Duplicates.cs`, trashed through `FileManagerService` |
| MCP | — | `Api/Mcp/LibraryTools.cs` |

## The passes

Rules 1 and 2 are identification's, paid for again by every pass after it.
ADR 0002 governs the write path — **read it before touching that path.** File
mutation is behind `Fonoteca:AllowFileMutation`, default `false`; with it off a
run does everything except the write, which makes a disabled run a complete dry
run rather than a no-op.

- **The scan runs in the foreground of the request** — a walk with no file reads
  is seconds even at 100k. Its four edges are rule 3.
- **The files are leading, and other programs edit them** (the owner's
  principle). A changed file is hashed (`FfmpegAudioHasher`, the MD5 of its
  audio packets, which the probe fills); one whose `AudioHash` still matches was
  retagged, and keeps everything but its tag numbers, which are re-read and
  reopen its folder where they moved. A file that left its path — vanished, or
  replaced there by other audio, as a renumbering does — is paired with where
  one arrived: same size and mtime, else same hash, exactly one each side, only
  on a complete walk. It keeps its row, and its album stamp only where its album
  folder moved whole with every name kept; a row whose path another took while
  its own audio went nowhere is removed. Moves go through a parked path in one
  transaction, since a swap is a cycle on a unique column. Every read precedes
  every write, so with ffmpeg unrunnable a scan that needs a hash refuses (503)
  and changes nothing. A row with no hash still loses everything on a retag,
  and a bulk retag is a read of every retagged file inside the request.
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

**Enrichment's stages run in `RunAsync`, in order, because each depends on what
the ones above wrote:** identified files → person-filed files
(`ClaimPersonFiledAsync`, promoting `LinkedByPerson` to `Linked` with credits) →
held recordings described again → parent works → works and filed releases
asked for what the tags need → album genres → artists → portraits → articles,
banners, covers, motion, booklets → lyrics → discographies. `CountPendingAsync` returns one number per
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
- **Who gets a page and who is credited are two tables.** `Relationship` is the
  browse index above; `Contribution` is every artist link MusicBrainz states on
  a recording or its work — producer, engineer, each player with all their
  qualifiers, lyricist apart from composer — by MBID and printed name, minting
  **no artist row**, so a session guitarist never joins the artist list. The
  album page and the tag write read credits from it, and Subsonic's
  `contributors` too, under `CreditTags.Role`'s names. Each link keeps its
  `Begin`/`End` as partial as stated; a recording's place, area and
  `performance` links go beside it in `RecordingSessions`, which nothing
  reading credits sees. A work's own composer links date it on the work
  (`Works.ComposedBegin`/`ComposedEnd`), filled wherever its lookup arrives,
  the climb's included — **a parent work keeps no links**, or the tag write,
  smart playlists and `displayComposer` would start reading through the
  piece. Subsonic serves all three as `fonotecaPerformance` (`Performance`:
  the sessions' and the players' span, the place over the city, the piece's
  years). Rebuilt wholesale on every describe, including rows the same save
  added (EF queries the database).
- **Every held recording is described again** (`Recordings.DescribedUtc`):
  genres, every ISRC (`Recordings.Isrcs`, apart from the shop's `Isrc`), the
  contributions and the work's parent (`Works.ParentId`, minted from the
  `parts` stub). One with no genre is asked again after a week; one with
  genres never. The parent stage then asks each parent what it is a part of
  (`Works.PartOfLookupUtc`, the top again after a week), so a chain climbs to
  the whole piece, which `WorkTree.Top` reads for every count, the composer
  pages and the album headings (the owner's choice) — **below a mere
  collection**: an untyped top whose every part MusicBrainz types
  (`Works.PartsTyped`) is cut off in `WorkParentsAsync` (`WorkTree.Cut`), so
  the Ring is four operas and op. 8 four concertos. Climb from the
  recording's own work through that dictionary, never from the `ParentId` on
  its row. Album genres are a release-group lookup each
  (`ReleaseGroups.Genres`), same rule. Artists carry `LinksJson`, every URL
  relation, which is how a later source finds one by identity.
- **A merged MBID is followed, never refused.** WS/2 answers it with the
  survivor's document, so a different id coming back *is* the merge.
  `CatalogueWriter.UpsertAsync(…, asked)` folds the stale recording row into the
  survivor: files and tracks repointed in place before the row goes
  (`Tracks.RecordingId` cascades, `MediaFiles.TrackId` sets null), its credits
  dropped, no outcome or decided stamp touched (rule 4), the move journalled as
  `catalogue.recording.merged`, and a moved file the rule decided, or whose
  album alone a person named, loses `ReleaseLookupUtc` so its folder is
  attributed again. Enrichment, the recording decision and attribution
  all pass `asked`; attribution looks a recording up only where a fetched track
  list does not print what the browse placed on it (`MergedAsync`), saved in a
  scope of its own before the component's. `RecordingNotFound` therefore means
  *deleted*. **Known and not fixed:** a merged *artist* is logged
  (`ArtistMerged`) and journalled, and its row keeps the old MBID, because moving
  it touches follows, pictures and discographies.
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

**Attribution's unit is an album folder, and a folder is one album** (ADR 0013).
*The folder's boundary is believed; its name never is* — a folder named for a
1979 album holds the audio of the 2015 remaster. The album is a release group;
the pressing is claimed only where nothing can contradict it, because a download
filed as the CD it resembles is a lie every later screen repeats.

- **The album needs more than half the folder**, counted over every file
  including the unidentified. Files on no edition of it stay with the folder
  (`OnNoEdition`); two albums fitting equally is a refusal.
- **A pressing is proved, never chosen** (`EditionProof`): track count equals
  the folder's file count, *every* track within 100 ms of its printed length, on a
  track list whose lengths are known and not all whole seconds, and no lossless
  file a CD could not hold on a medium that may be a CD. More than one proving is
  album only, the tie counted in `EditionAlternatives`; pseudo-releases never
  prove. The length is the probe's, else fpcalc's (hundredths of a second) —
  **so an unprobed folder can prove a CD it cannot contradict**, a gap accepted
  in the plan.
- **`[silence]` and `[data track]` are positions, not songs** (`TrackTitles`,
  and its TS twin `PLACEHOLDER_TITLES` in `identify.ts` — change both). One
  counts towards an edition only where a file holds it — in the proof, the fit,
  the album page and the incomplete lists — and Identify never seats a file on
  one by its number alone. The SQL counts spell the two titles out, because a
  helper inside a projection is a closure.
- **Order is relative, never a number** (`FolderOrder`): the files' own tags if
  every one is numbered, else numbered names, checked against every official
  edition on shared recordings. Only a *tag* order every official edition
  reverses, from an uncapped gather, is `Contradicted`; without a proven pressing
  or a person's album that is a question (`OrderContradicted`). Tags are read
  here and stored as `TagTrackNumber`/`TagDiscNumber` for every file in the
  folder; any read failure means names instead. **The pass opens files, so it needs the volume.**
- **The whole folder is re-decided when any file in it is pending** — a new file
  changes the file count a pressing is proved against. Rows a person decided are
  read and never decided again (their own tag numbers are still recorded),
  except `AlbumByPerson`/`AlbumByAgent`, whose album is kept and whose pressing
  the rule may prove. **A person's album names the folder's only where their
  answers are most of it**; a minority pick keeps its own album untouched and the
  rule decides the rest. `SetFolderAlbum` answers for every file, so it always
  names the folder's; the pass never moves a named folder.
- **Two MusicBrainz shapes are unusable and force a two-stage gather.**
  `inc=releases` on a recording lookup caps at 25 without saying so;
  `inc=recordings` on a *browse* silently drops releases (40 becomes 15 while
  `release-count` still claims 40). Track lists come from `GetReleaseAsync`. The
  browse is paged to its end, which is what makes "on no edition of this album"
  a claim rather than a guess.
- **EF queries the database, not the change tracker** — three unique-index
  collisions in live runs came from this, so everything the writer mints goes
  through a dictionary.
- **The album's official editions persist as catalogue, whole track lists and
  all**, plus the proven pressing, the best-fitting edition where it is of the
  chosen album, and any edition a substituted recording is printed on — the album page's combined track list (`Editions.Combine`) is read
  from them, which is what makes "the deluxe has two you do not" answerable. A
  *refused* folder's shortlist goes to `ReleaseCandidateSet`, keyed on the one
  `ReleaseLookupUtc` its files share.
- **Known failure, expected fixed by the folder cut and NOT re-measured.**
  Michael Jackson's albums attributed wrongly; the loss was in `GatherAsync`, not
  in the rule. **Run the pass against the real mirror before believing that.**

**Analyse: a second decode, on purpose.** Its own pass by the owner's choice,
because the probe reads anything ffmpeg says as the decoder objecting and the
analysis filters (`ebur128`, `astats`, `drmeter`, `silencedetect`) talk on the
same channel. Called Analyse because the dashboard already calls the probe
"measure". The probe's shape throughout: `MeasuredUtc` is the "we asked" stamp,
a missing binary ends the pass, a file not on disk stays pending, a scan that
sees the audio change clears `Measurement` (and the tempo and key). **Tempo and
key are a second question in the same pass** (ADR 0017): Essentia, through its
venv's interpreter (`Fonoteca:EssentiaPythonPath`, empty = not asked), stamped
apart (`TempoAndKeyMeasuredUtc`) so turning it on asks only that half; every
reading is kept, `BPM` is tagged only at a confidence of 1 or more, `KEY` always
(the owner's choices). **Answers, never rankings**:
`AudioMeasurement` keeps readings; `AudioAnalysis` turns them into ReplayGain 2.0
(−18 LUFS, sample peak), the DR figure, clipping and the **measured bit depth,
which the upgrade list and album replacement rank by** — 16-bit audio in a
24-bit FLAC is a CD rip (`AudioAnalysis.Decided`; the owner's choice).
astats' "Bit depth: a/b/c/d" is undocumented; its second figure is the one that
told padded from real on ffmpeg 8.0. ReplayGain's album is the **album folder**
(`AlbumLoudness`), computed when asked and only when every file in it is
analysed; the tag write puts all four values in the files, replacing a file's
own (the owner's choice), and Subsonic serves them. The DR figures go in too,
as `DYNAMIC RANGE` and `ALBUM DYNAMIC RANGE` — foobar2000's DR Meter's names,
no standard naming any — the album's only where every track has one.
**Every analysed file is on the tag write's worklist**, since a gain needs no
identity — one the catalogue has not answered (`TagWriteService.Answered`) gets
its ReplayGain and DR alone, its tempo, key and fingerprint, and a lyrics
file's words beside it: no partial answer, no sleeve, no rename. Opus
gets no ReplayGain (RFC 7845 wants `R128_TRACK_GAIN` there; no Opus in the
library yet).

**Probe: two questions, one decode.** `-count_frames` decodes rather than parsing
the header, so the run that fills `AudioQuality` is the only one that can say
whether the bytes are intact — splitting them decodes the library twice. At
`-v error` a healthy stream says nothing, so anything on stderr is the decoder
objecting *while exiting zero*; that becomes `IntegrityState.Corrupt` and never
reaches `AudioQuality`, which decides which duplicate to keep. Parallel over a
claimed page with **deliberately no channel** — see the deadlock gotcha.
Two readings are not the decoder's to give. A FLAC ending in a 128-byte ID3v1
tag decodes the tag as a broken last frame, so a complaint about one is asked
again without it (ffmpeg's `subfile` protocol) — 24 intact FLACs here were
marked corrupt. And a stream that decodes no frames (`nb_read_frames` absent)
is `Unreadable`, not intact with its header's numbers. A row a stricter reading
refuses forgets the `Quality` an earlier pass stored.

**Tag write: everything above this line writes to PostgreSQL.** Copy the library
elsewhere and none of it exists; this pass is what makes the answers portable.

- **Three buttons and no fourth route.** One pass, three scopes (library /
  album / artist) rather than three mechanisms — "quick" is a property of the
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
- **ATL re-renders a date on every save, and its own reading cannot see it.**
  ID3v2.3's `TDAT` day is dropped on the upgrade to 2.4, a 2.4 "2017" (and
  `TDOR`) gains a first of January, "2008-10" a first of the month. The writer
  hands ATL back the precision TagLib# read (`Restate`), and when the year is not
  in the plan TagLib#'s `RecordedDate` and `OriginalDate` must still match before
  and after, or the file is not written — the owner's choice over losing the day.
  A stored `0001-01-01` is read as no date, and an MP3 whose modern tag has no
  date is dated by its ID3v1 year — the owner's choices too. TagLib#
  itself folds 2.3's `TYER`+`TDAT` month-first; `VerifierReading` puts it back.
- **ATL damages what it does not understand, so each reader is held to its own
  before.** It wrote an encoding byte into every ID3 `UFID` owner (748 MP3s here
  lost their MusicBrainz id to other readers) — `TaggingDefaults.UfidIsBinary`
  patches ATL's private list by reflection, a planned `UFID` change repairs a
  damaged one, and `Id3v2Repair` trims the NUL ATL still ends the id with —
  MusicBrainz's only; a NUL it cannot take off (another owner's id, a tag it does
  not walk) refuses the write, since TagLib# cannot see it. The same repair puts
  back the old ReplayGain `RGAD` frame, which ATL rewrites as text, growing the
  tag when ATL left no padding. **Both tags, old and new**, the owner's choice
  for compatibility: ATL empties the ID3v1 year whenever it writes a full date
  and drops a multi-value genre, and writes no ID3v1 where there was none, so
  `Id3v1Trailer` writes it in full after every MP3 save — no reader checks it.
  A journal value holding a NUL is stored as U+2400, since `jsonb` refuses it
  and a failed journal save after the commit is rule 2 broken. It drops a cover
  stored inside a Vorbis comment, which TagLib# never sees, so pictures are
  compared per reader as well as across them — and `FlacCommentPictures` puts
  it back as a picture block of its own, the owner's choice. And TagLib# 2.3 times an MP3
  with a LAME `Info` header at double length, so durations are compared per
  reader too. It reads timed lyrics into a parse that drops the space after
  each time and moves a blank timed line to the top, and writes them back from
  it on every save: the lyrics are read as stored, by TagLib#
  (`TagReader.StoredLyrics`), and the writer hands one set of words back as
  stored.
- **Genres, ISRCs and credits go in too, under Picard's names** — the owner's
  choice, replacing whatever the file carried. A file's genres are its album's,
  its recording's only where the album has none, five at most, spelled as
  MusicBrainz spells them (`FileGenres`, which Subsonic serves by as well).
  Credits come from `Contribution` through `CreditTags`: COMPOSER, LYRICIST,
  CONDUCTOR, ARRANGER, PRODUCER, ENGINEER, MIXER, `PERFORMER=Name (instrument)`.
  Several values are one field's several values (`CatalogueTags.ValueSeparator`),
  except in an MP4, where ATL runs them together and they go as one value joined
  with "; " — and an MP4 gets no ISRC or conductor, which ATL writes where
  nothing reads them. On ID3 the five without a frame of ATL's go in `TXXX`,
  where Picard uses `TIPL`/`TMCL`; TagLib-based readers find them either way.
  The tag editor shows several values joined with "; ", and a value a person
  saves is theirs, as one value. **So do lyrics** (ADR 0016): LRCLIB's, timed
  LRC where there is any, in `LYRICS`/`USLT`/`©lyr`, replacing the file's own
  and its `UNSYNCEDLYRICS`; every `.lrc` beside a written track goes to the
  trash once the tag holds the words, its own words going in first where
  LRCLIB has none. All the owner's choices, and nothing in Fonoteca shows them.
  **And the rest of Picard's tags the catalogue can answer** (ADR 0017, "all tags
  that are standard", the owner's choice): sort names, ARTISTS/ALBUMARTISTS,
  WRITER, REMIXER, the composers' ids and sort names, the first artist's
  homepage, RELEASETYPE, ORIGINALYEAR, COMPILATION, WORK and the movement
  four, LANGUAGE, the fingerprint, the loudness range and reference, BPM and
  KEY — each where ATL 7.16 was measured to put it where Picard does
  (`CatalogueTagFields.Picard`): a typed property where ATL reads one, else the
  container's frame or atom, else a comment, `TXXX` or freeform atom. ID3's
  movements are `TXXX`, not `MVNM`/`MVIN`, which ATL cannot write; an MP4 gets
  no ASIN. The pressing's own — label, catalogue number, barcode, country,
  status, medium, release track id, ASIN, script, disc title — only with a
  proven pressing; a person's own artist or album artist drops the
  catalogue's sort name and list for that billing.
- **A pressing's tags only with a proven pressing.** A file held to its album
  alone gets the album's facts — title, artist, album, album artist (the display
  edition's billing), year (`AlbumYear`), recording and release-group MBIDs, and
  its place in the folder's order as the track number — and no disc, track total
  or release MBID. The track number and year go only where the file has none of
  its own, or has the one this pass filled in (the journal says which), and no
  track number beside a disc number. **Only the year, never a date**, and **a
  fact the catalogue does not hold produces no entry** — over a library at a time
  a blank is a deletion wearing an edit's clothes, so an older pressing's tags on
  a file stay until something replaces them.
- **No single file may end the run, and the catch is on `Exception`.** Because
  the diff is the worklist, nothing steps over a row that threw. Serial, because
  ATL renders the whole file into a staged sibling.
- **Then every album folder the run covered is renamed** to `Fonoteca:FileNaming`
  (`FileNaming`, default `{albumartist}/{album}[ ({year})]/[{disc}-]{track} -
  {title}`), named from the catalogue — but a file held to its album alone by
  the track and disc numbers its tags keep — rows first and never over anything,
  through a link, or a folder holding links. A collaboration lives under its first-billed
  artist; each other billed artist gets a *relative* directory symlink, journalled
  — the journal is the only way the pass knows a link is its own; Navidrome
  follows links by default and lists each linked track twice, hence
  `ND_SCANNER_FOLLOWSYMLINKS=false` in compose. Unit is the album folder, so a
  loose file or a folder naming two albums stays put. A file under a track's
  name (`01.cue`) goes with it — a lyrics file is in the trash by then — and an artist folder only respelled takes its
  pictures along.
- **The artist's pictures go on the shelf named for them** — `artist.*` and
  `backdrop.*` beside the album folders, from every button (the owner's
  choice): a shelf is an artist's only where its name is one the album in it
  bills (`ArtistShelf`), or, from an artist's page, that artist's, so a
  violinist's face never lands on Bach's. What the catalogue shows displaces
  what is there to the trash; Lidarr's `folder.jpg` and `logo.png` are not
  ours and stay. Fetched once per picture per run at 1280 wide
  (`PortraitRendition`) through a client with Wikimedia's User-Agent — Commons
  answers 403 without one, and most portraits are Commons'. A folder the run
  itself renames onto a new shelf gets its picture on the next run.
- **Undo steps one album folder back by one edit** (`TagWriteService.Undo.cs`,
  `POST /api/catalogue/folders/undo`): the newest journal correlation that
  touched it — a tag write's tags, renames, links and sleeve together, or
  identification's AcoustID — found by file id, a folder move by its `to` and a
  link by its `target`. Each press writes `tagging.undo.undone` per file, which
  is how the next press skips it and steps further back. Tags go back even over
  another tagger's change since (the owner's choice); moves never go over
  anything. Undoing a tag write **reopens the folder**, or the next library run
  would redo it. It writes through `TagWriter` like any write (`PlanRestoreAsync`,
  where a null value removes a field — ATL reads null on a typed property as
  "leave it" and empty as "remove"), under the `tagging.undo` prefix, which
  `FilledAsync` reads as part of the same chain. **A file whose tags will not go
  back stops it** (`Incomplete`): nothing moves, nothing is reopened, the edit is
  not marked, so the next press retries; an unmounted folder is `NotOnDisk`. A
  path is not an album, so a folder move or link counts only where the folder's
  files vouch for it — named in its `files`, or written by the same run. **Totals
  pair with numbers:** ATL writes no total without its number, and before
  `TotalsVerified` the journal said "no total" where ATL misread a doubled
  `TRACKTOTAL`/`TOTALTRACKS`, so an older entry's missing total is believed only
  when its number goes too. Not restored: a date cut to a year, a dropped ID3
  prefix, the ID3 version and ID3v1 trailer, repairs, a shelf's carried pictures.
- **A person's tags (`PersonTags`, the tag editor on Files and on each album
  folder) are stored beside the catalogue's and laid over them by this pass —
  they win**, over the catalogue — except where another program changed a
  field since the correction was written (`PersonTags.Superseded`, off the
  journal): the newer edit stands and becomes the file's correction, journalled
  `tagging.person.superseded`, until a person saves that field again (the
  owner's choice). The album's own corrections are the catalogue's and replace.
  Per file in `MediaFiles.TagEditsJson` (null = "carries none", the one way a
  removal enters a write); where the folder is one album its title and artist
  are corrections to the album, in `ReleaseGroups.EditsJson` — the album page's
  own corrections now live there too, a pressing's facts on
  `Releases.EditsJson`, read back with `PersonEdits.Combine` — and its year is
  set on every file of the folder, so it survives the folder being filed under
  a pressing later and leaves the album's other folders alone (the owner's
  choice). Taking out a track or disc number takes its total too, since ATL
  writes no total without its number. A file with corrections is on the worklist even with no
  catalogue answer, and names follow them; an album artist typed is the shelf
  as typed, with no links. A year typed for an album on the album page is the
  person's on a file held to the album alone too, replacing its own. Saving
  writes the folder at once (`TagWriteScope.ForFolder`, run in the request under
  the gate) and journals `tagging.person.saved`, so one Undo press reverts the
  files and the corrections and reopens nothing — only the keys that save
  changed, so a later album-page correction or another folder's stays. Any
  field a person names is allowed but an identity, a four-character name (ATL
  writes those as an ID3 frame or MP4 atom) or a name ATL maps to a field of
  its own (`PersonTags.TagLibraryOwn`, "Publisher" in a FLAC); TagLib# confirms
  each in its container's own place, as it does the AcoustID.
- **With `AllowFileMutation` off no button that writes a file is shown** — Write
  tags and Undo alike (the owner's choice); the endpoints refuse as well.

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
  not `mv` plus a rescan.** The scan follows only a move it can pair, and
  repoints no playlist line. Rows first, then the bytes, in one transaction.
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

**Album covers** are fetched once at 1200px (Qobuz's at its largest, about 1400px)
and served from `ReleaseCovers` under
an ETag — every tile used to hot-link the archive's front-250. Only an image the
archive lists against *this* release may be chosen, and an upload is held to the
same raster allowlist with SVG refused. Enrichment asks for every sleeve the
tag write would draw from the same way (the cover stage), so an album nobody
opened still gets its `cover.jpg`.

- **Two sources, in order, and the order is the safety argument.** The archive
  is keyed on the release mbid and cannot be wrong about which record it shows.
  **Qobuz is the fallback** (`QobuzCovers`), reached only where that answered
  nothing — 105 of 612 albums here — and keyed on the barcode where there is
  one, on a matched title and artist otherwise. Measured on the first run: **63
  of the 105 gained a cover**, 13 by barcode and 50 by title. The barcode is any
  edition's of the album, with the check digit Qobuz drops from Universal's
  dropped here too (`Barcodes`); the artist is the whole credit line or any one
  name on it, because a classical box is billed composer, orchestra and
  conductor here and the conductor alone there. A loose match is still
  refused: an ampersand written out as "and" does not fold, and that is a
  `QobuzCoversTests` case rather than an oversight.
- **A row with no bytes is a stamp, not an answer**, and expires after
  `CatalogueEndpoints.CoverRetryAfter` (a week). Treated as final it would hide
  every sleeve that reached either source after the first view, forever, with no
  endpoint to clear it. **A row with bytes never expires**, from any source.
- **Where neither source has one, the album's own files lend theirs** — the
  album folder's cover image, else an embedded front, never a folder above the
  album's — **served and never stored**: a
  row with bytes is never re-asked, so storing it would keep out the sleeve the
  archive gains later.
- **An outage is not stored** — from either source, which is why `QobuzCovers`
  throws rather than returning null when it could not ask. A stored outage would
  be believed for a week.
- **`QobuzAlbumId` is provenance, and `Uploaded` means a person's upload.** A
  cover the shop supplied is nobody's upload, and the dialog says which it is.
  This is the one caller of `QobuzClient` that is not a person choosing an album,
  so what bounds it is written on both.

**Album motion artwork** — Apple Music's looping album videos — is ADR 0014.
An enrichment stage reads it from Apple's *public* pages (barcode lookup,
the album page's header JSON, the best H.264 rung of each HLS ladder as one
MP4), never the web player's private API and its scraped token; it keeps the
square video only (the tall one was dropped on 2026-10-15, the owner's choice)
in `AlbumMotions`, a row per album shaped like `ReleaseCovers` (no video = a
stamp that expires in a week), and the tag write puts it beside the album as
`square_animated_artwork.mp4`, journalled as `tagging.motion` and undone with
the sleeve; every `tall_animated_artwork.mp4` in a folder it writes is deleted
for good, not trashed (the owner's choice, 2026-10-15), and journalled with its
path and length for a record Undo skips. **A page that changes shape stops the stage instead of answering
"none"**, so a redesign cannot stamp the whole library. `getAlbumMotion` serves
it to clients as an animated WebP (`FfmpegCoverScaler.AnimateAsync`, kept per
size) under Fonoteca's own `fonotecaAlbumMotion` extension.

**Album booklets** are ADR 0015 — booklets only, never backs, discs or trays
(the owner's choice). An enrichment stage lists every edition on the Cover Art
Archive and keeps the fullest edition's `Booklet` pages whole (never one also
typed `Front`), full size, the display edition winning a tie; Qobuz's digital booklet (a PDF "goodie" on
`album/get`, matched by `QobuzCovers.Match`) is kept beside it. Stored in
`AlbumBooklets`/`AlbumBookletFiles` on the same stamp rules, written by the tag
write beside the tracks as `booklet-01.jpg`… and `booklet.pdf`, journalled as
`tagging.booklet`. **Any other `booklet*` file in the folder is a person's and
wins**: nothing is written for that album. An album no source can answer is
recorded as failed (`FailedUtc`, never "none") and asked after every album not
yet asked; three failures in a row end the stage.

**`…/folders/seed`** hands a folder to MusicBrainz's own "add release" form,
prefilled. Fonoteca writes nothing to MusicBrainz and cannot — their `/ws/2`
write API cannot create a release at all, so the supported path is the one Picard
uses. The action URL is deliberately not configurable: an edit against a mirror
is refused or lost at the next replication. Lengths are measured, never declared.

**`…/albums/{id}/fingerprints`** is the only *answer* this application gives a
provider. A separate interface (`IAcoustIdSubmission`) so something can be given
the ability to ask without the ability to assert; **nothing automatic may ever
call it**, because a pass submitting conclusions drawn from AcoustID's own
answers is feeding the provider its own opinion and reading the echo as
corroboration. Eligibility is `IdentityDecidedUtc IS NOT NULL` — what a
submission carries is the *link*, so the case worth most is a cluster naming
nothing that a person has just named.

**Playlists** are m3u files at the top of `Playlists/` in the library root,
with paths relative to the file (the owner's choices: Navidrome, Roon and the
production copy read them there). Two kinds share the folder.

- **A static playlist is its file and nothing else** (the owner's choice), so it
  has no row. Any `.m3u8` or `.m3u` there that no smart playlist owns is one,
  whoever wrote it, and its id is the file's name. An edit here changes only
  the lines it names and keeps every other line, comment and the file's own
  newline; entries are named by their place, so an edit carries the file's
  `version` and is refused when the file changed since. A file that is not
  UTF-8 is read as Latin-1 and never written.
- **A line follows its file only where Fonoteca moved it.** Every move in the tag
  write's rename and its undo (`TagWriteService.MoveAsync`) and in the file
  manager calls `PlaylistFiles.RepointAsync` after it commits; a folder move
  carries every line under it, the dead ones too. **A new place that moves
  files must do the same.** A file that leaves the library leaves its line as
  written — another release's copy of the song is no replacement (the owner's
  choice) — except after an upgrade, when `FollowFormatsAsync` points the line
  at the one file of its name in another format in that folder, the FLAC that
  replaced an MP3. Adding a song writes its best copy (`PlaylistFile.Best`); a
  Subsonic client's song is the file it named.
- **A smart playlist is rules** (`SmartPlaylist`, `SmartRules`, and its twin
  `smartRules.ts`): a tree of groups, each matching all or any of its
  conditions and groups, after WordPress's `tax_query` (the owner's choice).
  The fields cover song, people, album, artist and file, and the tree is one
  SQL `WHERE` over files (`SmartQuery`). A song is in once, as the best of its
  matching copies, and "added in the last" compares UUIDv7 file ids as rows.
  **Only the file holds the songs.** It is written whole on save, and again on
  every release of `LibraryWorkGate` — every pass, undo, tag edit, download and
  answer on Identify — and at the end of every scan, which never takes the gate
  (the owner's choice, over a sweep). A time rule moves only at the next of
  those. An edit made to the file elsewhere is overwritten.

Nothing is written while the root looks unmounted, and a deleted playlist's file
goes to the trash. Subsonic reads both kinds through the same handlers and writes
static ones; a smart one is read-only there (error 50). Roon's documentation
does not say whether it re-reads a changed m3u; Navidrome does.

**Internet radio stations are files too**: one XSPF per station in `Radio/`
at the top of the library, no row, believed like a static playlist and
written through `PlaylistFiles`' reads, writes, lock and mount check (the
owner's choice, so stations go wherever the library goes). Subsonic is the
only way in; a station's id is `ir-` and its file's name, kept while its name
is. Saving one looks up a logo for `image` (`StationLogos`, the owner's
sources): a large icon the station's home page names, then Wikidata's `P154`
on the item whose stream (`P963`) or official website (`P856`) is the
station's, then radio-browser.info by stream, then the page's share picture,
small icons and a `/favicon.ico` it answers for. An update keeps the file's
own logo while the stream is unchanged. **The home page is the one address
nobody vetted** — anyone can enter one on radio-browser — so its client
connects only to public addresses (`StationHomepages.ConnectAsync`), no proxy,
redirects included. `getCoverArt` serves the logo under the station's id,
fetched once through that client and kept by address, and always as a PNG
ffmpeg made (`FfmpegCoverScaler.PngAsync`): a logo is often transparent,
which a scaled JPEG paints black, and often a `favicon.ico`, which no client
is handed as fetched. Fonoteca never plays a stream.

## Acquisition

**The only part that spends money, and the only part that can take music away.**
ADR 0011 is the design record: **a download is filed where it lands, as the
shop described it** (`Acquisition/DownloadFiling.cs`), and its album folder's
tags are written at once. **Read the ADR before touching this** — most of what
looks like an oversight is written up there.

- **The shop is an authority about the album, not the audio.** Files are linked
  to a release minted from the shop's facts and keyed on its barcode (one minted
  from an earlier download of that barcode is found again), outcomes
  `LinkedByProvider`/`AttributedByProvider`, recording and release settled —
  but not `AcoustIdDecidedUtc` and no lookup stamp, so identification still
  fingerprints the audio and nobody claims MusicBrainz was asked. The shop's
  credit string gives the four roles the catalogue keeps (`ProviderCredits`);
  a credited artist is linked only where exactly one answers to the name, else
  minted with no MBID (the owner's choice). No badge marks a download's album.
- **A download takes `LibraryWorkGate`**, and a busy gate refuses it (409) —
  the owner's choice, over waiting. **Its tags are written** under that lease
  through `TagWriteService.WriteFolderAsync`, the one tag write that is not a
  person's own click on Write tags: the owner's choice, for files that arrive
  untagged. That folder only, and renamed as any album is. Undo treats it as
  any tag write: the folder is reopened and the shop's filing goes with it
  (the owner's choice).
- **A replacement deletes the archived files' rows** (the owner's choice) and
  files the new album in their place — unless the old one was filed under an
  album MusicBrainz knows and the barcode names no release of it, which the
  passes are left to file again. A download with tracks still missing is not
  filed or tagged until a later request completes it, or asking again could
  not find what landed to resume it.
- **MusicBrainz is asked whenever it could name the album**
  (`MusicBrainzCatchUp`). At download by the shop's barcode: exactly one
  release printing the shop's track list slot for slot (`ProviderMatch`) takes
  the files, else the shop's own is minted. Then weekly (`ProviderCatchUpService`,
  the owner's choice) for every album still the shop's: a release the barcode
  names takes its files whole and the shop's release goes; where it names none,
  a recording MusicBrainz holds under exactly one ISRC gains that MBID, or is
  folded into the row already holding it. Naming such a folder's album by hand
  re-files it at once — each file takes the recording the named release prints
  at its disc and position, so attribution can prove a pressing. A download
  under MusicBrainz's recordings joins enrichment's person-filed worklist until
  asked once, its outcome staying the shop's.

- **No staging directory for a new album, and removing it was the point** — only
  a download replacing a held album is fetched aside first, below. Bytes go to a `.part`
  sibling — an extension `AudioFormats` does not recognise — and the final name
  exists only after the body is checked against `Content-Length` and atomically
  renamed, so a scan only ever sees whole files. What staging did buy is kept:
  the collision check against a folder the library already holds.
- **A download of an album the library holds replaces it.** The album is the
  release group: `HeldAsync` finds the folder (a barcode of any edition, else
  the cover rule's match — the shop's title also asked without an edition note,
  "(Deluxe Edition)", never without "(Live)" — on more than half the folder, or
  the `Artist/Album` it would land in); a folder whose files are filed under
  another album, or under none, is replaced only once a person confirms it
  (`Unconfirmed`, then `confirmed`), from a row or a search, the owner's
  choice — the match misses a shop's "(Live)" and a band billed by its
  leader's name. Where a search finds nothing that way, one held album of the
  same title, whatever its billing, is asked about too (`SameTitle`), and a
  person may say `separate` instead; two are a common title and no question.
  Whether it is this album is asked before whether it is better — the other
  order refused somebody else's *Unplugged* as a worse copy of the one held.
  The offer is compared *before* fetching
  (`UpgradeReplacement.Offer`: better, or as good with more tracks, never
  fewer); the album is fetched into the hidden `.fonoteca-downloads/<id>` and
  moved into the old folder once replaced — the old rip's cue sheets and
  playlists archived with it, its sleeve kept — or removed, as it is when Qobuz
  refuses part-way; one kept to resume goes after a week. An interrupted
  download of the same album resumes instead. **Tracks are counted, never
  matched by title** — the album is the release group and the reasons to
  replace are quality and missing tracks, the owner's choice: the shop titles
  one track "The Chain (Album Version)", and matching refused 15 of 22 real
  upgrades. The stated cost: a held track the shop's album lacks is archived
  with the rest.
- **One album at a time**, about a double-click rather than throughput: two runs
  write the same paths and the loser corrupts the winner's files.
- **The upgrade list answers two questions from different evidence.** "Is this
  lossy?" a filename settles, because what Qobuz sells is FLAC. "Is this lossless
  file below what exists?" needs a decoder, so it is answered only where the probe
  pass has filled `MediaFile.Quality`; an unprobed lossless file reports
  `UpgradeReason.None` rather than a guess. Guessing from implied bitrate was
  measured and is worse than silence. `m4a` is absent from both sides of the
  lossless split and so reads as lossy, which is the error worth making.
- **Replacement still runs after the download and measures what arrived** — what
  Qobuz advertises is a ceiling, and this is the last place to take a provider's
  word for anything when the consequence is deleting music.
- **Files move to `Fonoteca:ReplacedPath`, outside the library root**, or the
  scan catalogues the archive. The archived files' rows are deleted as they go,
  above. It refuses to archive anything it just wrote.
- **`AllowFileReplacement` is deliberately not `AllowFileMutation`.** Somebody
  who turned the first on to get their files tagged has not agreed to "may take
  an album away". With the flag off a download of a held album is refused before
  anything is fetched, saying whether it was an upgrade.
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
put one album on the list 125 times). `incomplete` is completeness: one row per
album, measured against the claimed pressing where every file is filed under
one and otherwise against the stored edition the files are nearest to
(`Editions.Nearest`) — never a longer one they were not, which would sell the
deluxe's bonus disc as a gap. Subtracting unfiled files is the only reason it is
worth reading, since 128 of 175 looked short only because files in the folder
had not been matched yet.
`missing` starts from a person: records credited to a **followed** artist that
nothing here sits under, cut by `Discography.IsGap` — **an explicit placeholder,
the only `TODO(you)` in the repo, with six tests prefixed `PlaceholderDecision_`
written to be rewritten. An open decision, not an oversight.**

**What a shop says goes in `DiscoveredRecords`, never in `ReleaseGroups`.** A
discovery row is a *question* — `IReleaseDiscovery` says so in its own words —
and a question written into the catalogue's album table becomes a fact nothing
can retract: credited to the artist, counted in their discography, and skipped
by the prune, which only considers rows carrying an MBID. It was measured at
**38,487 of 180,625 release groups**, and not one of them could ever stop being
reported as missing — not even by buying the record, since the files land under
the MusicBrainz group instead. Keyed on `(ArtistId, Source, SourceId)`, because a
shop rewords its own titles and a title-keyed upsert mints a fresh row each time
it does. **Every field the source stated is kept**, the barcode above all: it is
how a later pass recognises the record as one MusicBrainz has since described,
and dropping it is what made the old shape a one-way door. So is the quality it
states (`HiRes`, `MaximumBitDepth`, `MaximumSamplingRate`): a shop sells one
record as several products under several barcodes, and the missing shelf folds
the same artist, title and year into one row showing the best, the rest in
`Also`, so not wanting the row stops wanting all of them.

**Nothing about a discovered row is judged on the way in** — not the track count,
not whether the library already holds it. Both are rules about the row rather
than facts in it, so `Discography.IsGap` runs where each shelf is built and the
row stops being a gap the moment the catalogue gains the album. *What is cached
is answers, never rankings*, applied to the one place that was storing a verdict.

**`Artist.Followed`, `ReleaseGroup.Monitored`, `DiscoveredRecord.Monitored` and
a listener's `Loves` are the only facts in the catalogue nothing can recompute** — everything else
derives from audio, a provider or a person's answer; these are standing intent,
and a rescan must never touch them. Monitoring is a filter, not an instruction: a
*first* browse is the baseline and writes everything unmonitored, later browses
mark what appeared. A later browse restates a discovered row's title, year,
barcode and sleeve and leaves `Monitored` alone. Stated consequence, not fixed:
unfollow for a year and re-follow, and a year of releases is marked wanted.

## Providers

*These are somebody else's production systems and they enforce their limits by
blocking you.* A `RequestGate` per provider.

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

`/mcp` is streamable HTTP, stateless. Twenty-five tools: `library_status`,
`musicbrainz_health`, `open_questions`, `list_artists`, `get_artist`,
`list_albums`, `get_album`, `get_file`, `list_folder`, `folder_contents`,
`recording_candidates`, `component_candidates`, `search_releases`,
`release_slots`, `start_pass`, `cancel_pass`, `decide_recording`,
`decide_component`, `file_under_release`, `mark_folder_unreleased`,
`reopen_folder`, `set_folder_album`, `undo_folder_edit`, `folder_tags`,
`edit_tags`.

- **Every tool is an existing endpoint handler, called directly** — `internal`
  rather than `private` for that reason — so validation, the gate and every
  refusal stay the endpoint's own. A tool with its own copy of a rule is the drift
  this file keeps warning about.
- **An agent's decision is not the owner's** (rule 4). **Adding a by-a-person
  outcome means adding its twin to `ByCaller`**, or the agent path throws.
- **Not offered:** the tag write at any scope, trash/move/upload, Qobuz. Undo
  and the tag editor are, recorded as the agent's (the owner's choice).
- **`Fonoteca:McpToken` empty is a 404, read per request.** `Guard` is middleware
  rather than a filter so it sits in front of whatever `MapMcp` maps. It locks
  `/mcp` and nothing else; `/api` takes a signed-in account (Accounts, below).
- **Lists default to 50** — a response lands in a model's context.

## Accounts

ADR 0018. **The library has no owner; an account owns only its hearts, its
tags, its playlist folders and its API keys**, and its roles say what it may
do. Never scope a catalogue fact, a file or a playlist by account.

- **ASP.NET Core Identity, and OpenSubsonic's twelve roles** (`AccountRoles`,
  a column, made claims by `AccountClaims`). A fallback policy closes every
  route; `/health`, `/openapi`, the sign-in endpoints and `/rest` and `/mcp`
  (their own guards) say `AllowAnonymous`. A new route is closed without anyone
  writing a line, which is the point. **A route that changes something names
  its role** (`.RequireRole(AccountRoles.Admin)` — admin for the library's
  machinery and every file write but an upload, cover art for covers and corrections, upload, settings), a
  Subsonic method checks `May`, and the web client hides it (`useCan`).
  Integration tests sign every request in with `builder.SignedIn()`
  (`TestSignIn`, every role); a test of signing in or of Subsonic makes real
  accounts.
- **The first visit makes the first account**, with every role, and claims the
  hearts from before accounts; the last admin can be neither removed nor
  demoted.
- **One password for the web and Subsonic**, so it is kept twice in one save
  (`AccountEndpoints.SetPasswordAsync`): Identity's hash, and
  `Account.SubsonicPassword` under Data Protection, because `t=md5(p+s)` needs
  the password itself. Never set one without the other.
- **Hearts are filtered, not queried for.** `Love` has a global query filter on
  `FonotecaDbContext.AccountId`, set per request by the Subsonic guard; code
  that moves hearts for everybody (`CatalogueWriter`) reads past it with
  `IgnoreQueryFilters` and carries each account's hearts separately. So do
  `Tag`, `SongTag` (through its tag), `PlaylistFolder` and
  `PlaylistPlacement`: a song's tags move with its hearts
  (`CarryLovesAsync`), and a playlist's rename or delete moves or drops every
  account's place for it (`CatalogueEndpoints.PlacedAsync`).
- **The journal names the account** (`AccountCallerContext`); work with no
  request behind it is `system`, `/mcp` is `agent`.

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
  `Recording.WorkId` reaches a *leaf*; the piece is `partOf` (the top of
  `Works.ParentId`), and before a recording is described again grouping falls back to
  the text before the first `": "`. **Consecutive runs, not a group-by** —
  sorting rows into work buckets would renumber the album on screen.
- **`listSearch.ts`** — sort, scope and filter live in the **URL**, not in
  component state, or pressing Back lands on an alphabetical list again. The
  option lists have one home so a third sort cannot miss the validator in
  `routes.tsx`.
- **`artistAlbums.ts`** — `billedOnRelease` is **three-state** (rule 3), and
  `band` carries the band's **id**, not the printed credit, because this library
  prints both "The Robert Cray Band" and "Robert Cray Band".
- **`certainty.ts`** — the one place attribution outcomes become English, as
  `openQuestions.ts` is for refusals. `Attributed` and `GroupOnly` render as
  **nothing at all**: a badge on every album trains the eye to skip the ones
  that matter.

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
  three steps the ADR lacks: a length sanity check, which catches the one failure
  two agreeing readers cannot (perfect tags, no audio); appending the undo entry
  *before* the commit so a crash over-records rather than under-records; and a
  date check through TagLib#, below.
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
- **Tag parsers throw whatever they like.** ATL threw `NullReferenceException`
  on FLACs with a prepended, **unsynchronised** ID3v2 tag; it is now handed the
  file from `fLaC` on (`Id3Prefix`), so a write drops that block — the owner's
  choice — and is refused if the block held the only copy of a picture.
  `TagReader` still converts any parser failure into `TagReadFailedException`, and
  the pass reads that as **do not write to this file**. `Corpus.Id3PrefixedFlac`
  builds one, and the recipe is spelled out there because four near-miss variants
  do *not* reproduce it.
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
  runs a class in seconds rather than the several minutes `dotnet test` takes.
  Run it one class at a time: the whole binary at once (602 tests) runs out of
  PostgreSQL connections or goes idle. Per-class runs are reliable for every
  class but that one.
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
