# 0002 — Two tag libraries, and a verified write path

**Status:** accepted, 2026-08-08

## Context

Writing tags is the only operation in Fonoteca that can **destroy a user's
library**. Everything else is recoverable by rescanning; a botched write is not.

The .NET options are `TagLibSharp` and `z440.atl.core` (ATL.NET). TagLib#'s last
release, 2.3.0, was published **30 July 2022** — four years stale, which looks
alarming for the single riskiest dependency in the system.

Checking what production media servers actually do complicates that reading:

- **Jellyfin** ships **both** `TagLibSharp 2.3.0` *and* `z440.atl.core` today.
- **Lidarr** maintains its own fork, published as `TagLibSharp-Lidarr`.

Neither trusts one library alone. "Dormant" turns out to mean *stable and
paired*, not abandoned.

## Decision

Carry both, with distinct jobs.

- **ATL.NET writes.** It is actively maintained and pure C#.
- **TagLib# never writes.** It is a second, independent reader used to verify.

The write path, implemented in `Fonoteca.Tagging`:

1. Read current tags with **both** libraries.
2. Compute a diff and present it as a **dry run**. Nothing writes without this.
3. Write via ATL.NET to a **temporary sibling file**, never in place.
4. Read the temp file back with ATL.NET — is it what we intended?
5. Read it back with TagLib# — do the two libraries agree about the result?
6. **Atomically** move the temp file over the original, and append the previous
   tag state to the undo journal.

Any mismatch at step 4 or 5 aborts, leaving the original byte-for-byte
untouched. Batch operations share a correlation id in the event log, so a whole
batch is reversible as a unit.

## Consequences

- Two dependencies where one would do, and disagreements between them need
  triage rather than being automatically bugs — tag-mapping conventions differ
  legitimately in places.
- Writes cost several extra reads per file. Irrelevant against the risk.
- `Fonoteca:AllowFileMutation` defaults to **false**, and nothing in the
  scaffold writes to audio files at all. It stays that way until this path is
  implemented and tested against the fixture corpus.
