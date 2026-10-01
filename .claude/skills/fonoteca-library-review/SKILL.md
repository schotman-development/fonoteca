---
name: fonoteca-library-review
description: Full data review of a Fonoteca music library — wrong albums/recordings, duplicates, integrity, tag-write state, completeness, artist data — from the live PostgreSQL catalogue, the album endpoint and the Acquire lists. Use when asked to "review my library", audit the catalogue, or find what is wrong with it. Read-only; reports findings, fixes nothing unasked.
---

# Fonoteca library review

Read-only. Nothing here writes; fixes (reopen_folder, trash, tag write) only when asked.

Setup:
- DB: `podman exec -i fonoteca-postgres-1 psql -U fonoteca -d fonoteca -A -F '|' -t < q.sql` (pg_trgm installed). Put SQL in the scratchpad; quote identifiers (`"MediaFiles"`).
- API: `localhost:5088`. Test copy `/mnt/vault/fonoteca-test/music`; production `/mnt/vault/media/music`.
- Enum ints live in `apps/api/src/Fonoteca.Domain/Catalogue/Entities.cs` (`AcoustIdOutcome`, `EnrichmentOutcome`, `ReleaseAttributionOutcome`, `FolderOrderOutcome`, `IntegrityState`). Read them first; person = 6/7/15, agent = 10/16.
- Folder = `split_part("Path",'/',1)||'/'||split_part("Path",'/',2)` (a temp view `mf` with that column saves repetition).

Checks, in order:
1. `mcp__fonoteca__library_status` + `open_questions`: pending work, corrupt list, tag-write state.
2. Outcome distributions per enum column; files with no `RecordingId`/`ReleaseGroupId` by folder.
3. Integrity: corrupt by folder. Intact but `AcoustIdOutcome` 4 (unfingerprintable) is suspect: `ffprobe -count_frames -show_entries stream=nb_read_frames` N/A means no decodable audio (Coltrane 2026-09-29; the probe now marks those Unreadable). A FLAC ending in `TAG` (`tail -c 128 | head -c 3`) was a false positive until the probe re-read such files without the trailer (2026-09-28); if one shows up corrupt again, check that fix is still in `FfprobeAudioProbe`. `ffprobe -v error -count_frames` shows whether all frames decode.
4. Wrong album: folder name (strip `(...)`/`[...]`) vs `ReleaseGroups.Title`, `word_similarity` both ways < 0.6; and share of a folder's files whose recording is on any stored `Tracks` of its release group < 0.75.
5. Wrong recording: `coalesce("Quality_Duration","FingerprintDuration")` vs `Recordings.Duration` off by > max(10s, 6%). Also the same recording on two files in one folder (catches a 12" mix on the album version, under the threshold). For person-seated folders, compare `Tracks` via `TrackId` against file titles — disc-offset seats show up as every title differing.
   Classify each hit before reporting (2026-09-28: 79 hits → 39 MB length errors, 19 wrong versions, 14 mislabelled downloads, 7 unknown):
   - AcoustID `lookup?trackid=<file AcoustId>&meta=recordings+sources` (key `Fonoteca__AcoustIdApiKey`, 0.4 s spacing): current recording in the cluster → MB's length is off, fine; another recording of the same title at the file's length with many sources → wrong version; a *different title* dominating → the download is mislabelled.
   - Sources ×1–2 may be our own submission (`AcoustIdSubmittedUtc`) — circular, not evidence.
   - Qobuz `album/get` durations matching the files to the second → MB lengths wrong (Pensacola).
   - Stored `Tracks` of the folder's release group at the file's length (±3 s) with a similar title → the right recording is already on the album.
   - The original tags are in the undo journal (`DomainEvents` `tagging.catalogue.written`, `changes[].previous`) and in the production copy; the embedded sleeve names the real release.
   Fix: `decide_recording` per file (any file, not only open ones; keeps the album). For a batch, POST JSON-RPC `tools/call` to `localhost:5088/mcp` with `Authorization: Bearer $Fonoteca__McpToken` — that records the agent; the `/api/catalogue/matching/recordings/{id}/decision` route would record a *person*. `file_under_release` only takes open files: `reopen_folder` first.
6. Duplicates: folder pairs sharing ≥3 recordings, with codec/size per folder — a lossy folder fully contained in a FLAC folder is redundant.
7. Album pages: `GET /api/catalogue/albums/{id}` for every distinct `ReleaseGroupId` (loop with curl+jq, ~600 in a minute or two): `.unplaced` (MusicBrainz recording merges), `.album.coverReleaseId` null, `.album.certainty`, `.album.editionId`. `trackCount`/`held` are null unless a pressing is proven.
8. Acquire (MusicBrainz `[silence]`/`[data track]` placeholders no longer count as missing since 2026-09-29; shop products of one record fold into one missing row): `GET /api/qobuz/upgrades` → `items` (reason Lossy vs BelowHiRes), `incomplete` (watch for `[silence]` pad tracks and zero-byte files as false gaps), `missing` (followed artists).
9. Tag write (the owner holds the bulk write until the catalogue is correct — list refused writes as blockers, not the unwritten majority as a problem): `DomainEvents` types `tagging.catalogue.written` (how many files ever got the catalogue) and `tagging.catalogue.aborted` whose latest event has no later `written` — refused and never retried, by folder and reason. TITLE changes with low `similarity(previous, written)` expose wrong seats already written to disk.
10. Artists: followed artists missing portrait (`PortraitUrl` or `ArtistImages`), `BiographyText`, `BannerUrl`.
11. Production vs test copy: `find` audio in both, diff by folder; zero-byte files (`-size 0`) in production.

Report ordered by damage: wrong data already written to files first, then wrong seats, redundant files, false positives in pass output, unwritten tags, merges, completeness, orphans, certainty, Acquire and artist gaps. Name folders and counts; say who decided (rule / person / agent).
