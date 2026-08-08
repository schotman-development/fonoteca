# 0007 — Identification runs in-process, not on a durable queue

**Status:** accepted, 2026-08-09

## Context

Identifying a library means fingerprinting every file with `fpcalc` and asking
AcoustID about each fingerprint. On the library this was built against — 7,962
files, 238 GB — that is about 25 minutes of decoding and, because AcoustID
allows three requests a second, a floor of **44 minutes of waiting**. It is the
first pass in this application that opens files, and it cannot run inside an
HTTP request.

The repository had already anticipated this. `LibraryScanService` says in its own
remarks that "the moment a pass has to open files, it belongs behind `IJobQueue`
with progress on `JobsHub`". `Fonoteca.Jobs` exists as a project with Hangfire
pinned, `IJobQueue` and `IJob` exist as interfaces, and `JobsHub.JobProgress`
exists as a contract with no sender. Everything was in place for this pass to be
the one that implemented them.

We did not implement them.

## Decision

**Run the identification pass in-process**, as a singleton holding a lease on a
`LibraryWorkGate`, launched fire-and-forget from the endpoint and reporting on
the existing `JobsHub`. `Fonoteca.Jobs` stays empty and `IJobQueue` stays
unimplemented.

The endpoint returns `202` with a job id. Progress is pushed over the hub;
`GET /api/library/identify` re-reads current state, because a client that
reconnects must not have to assume it caught every message.

## Why

**Durability is already in the catalogue, and a queue would duplicate it.**

The worklist is a query — `WHERE "AcoustIdCheckedUtc" IS NULL` — not a list held
in memory, and each file's outcome is committed as it is produced. A process
killed at any instant leaves work the next run simply picks up. There is no
partial state to recover, because there is no state anywhere except the rows.

Two column choices are what make that true, and they are the substance of this
decision more than the absence of Hangfire is:

- **`AcoustIdCheckedUtc`, not `AcoustId IS NULL`, defines the worklist.** A
  library contains bootlegs, DJ mixes and field recordings AcoustID has never
  heard. Keyed on the identifier, every one of them would be re-fingerprinted and
  re-asked about on every pass, forever, at a third of a second each. Recording
  *that we asked* is what lets the worklist shrink to empty.
- **`AcoustIdTaggedUtc` is separate from it.** "We know what this is" and "the
  file says what it is" are different facts. Keeping them apart is what makes a
  run with `Fonoteca:AllowFileMutation` off a complete dry run rather than a
  wasted one: it fills in everything expensive and leaves the tag timestamp null,
  so the run after the flag is flipped writes tags and spends no requests at all.

**The ordering that matters is the file before the row.** A crash between them
leaves a file carrying a verified tag while the row still says pending; the next
run reads the tag, adopts it, and converges. The reverse would leave a row
claiming an AcoustID the file does not carry — the one failure this design must
not have, because the tag on the file is the artifact.

So a durable queue would buy exactly two things: surviving a restart without
someone pressing the button again, and a persisted history of runs. For the first
job type in a single-user, single-process application whose recovery is one POST,
neither is worth Hangfire's own schema, its dashboard-authentication problem, its
job-serialisation surface, and Newtonsoft.Json on the critical path.

**And adopting it now would settle a question the repository deliberately left
open.** `IJobQueue`'s remarks name Wolverine's durable outbox as the likely
successor "once job semantics are concrete". Job semantics are not concrete after
one job type. Choosing between them on the evidence of a single pass is exactly
the decision the interface exists to defer.

## Consequences

- A pass does not survive a restart on its own. It has to be started again, and
  it resumes rather than repeats.
- There is no run history. `LastCompleted` is in memory and forgotten on restart,
  the same stopgap `LibraryScanService` already uses for scans.
- `LibraryWorkGate` is a new concept — one piece of library-wide work at a time —
  where a queue would have supplied that for free. It is about thirty lines, and
  it exists because a scan clears the very columns an identification pass is
  filling in, so the two overlapping is a correctness problem rather than a
  performance one.
- `Fonoteca.Jobs` remains a project with dependencies and no code, which is
  untidy, and `Hangfire` remains pinned in `Directory.Packages.props` for nothing.

## When to revisit

Any one of these should reopen it:

- **A third long-running pass exists.** Hashing and probing are already planned,
  and downloading after them. Three bespoke services with three hand-rolled gates
  is the point at which the abstraction pays.
- **Work needs scheduling rather than triggering.** Upgrade monitoring is
  *arr-style and periodic; `IJobQueue.ScheduleAsync` has no caller today and that
  is the feature that gives it one.
- **A pass acquires a step that is not idempotent.** Everything here can be
  repeated safely. A download that charges an account, or a move that renames a
  file, cannot, and at-least-once delivery then needs a real answer rather than a
  well-behaved worklist.
- **More than one process serves this library.** The gate is an in-memory lock. It
  is correct for one process and meaningless for two.
