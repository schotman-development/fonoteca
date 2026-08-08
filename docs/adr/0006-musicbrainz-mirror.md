# 0006 — Mirror MusicBrainz locally, without a search index

**Status:** accepted, 2026-08-08

## Context

`IMusicBrainzCatalogue` talks to musicbrainz.org through a request gate pinned
at one request per second, because that is the published limit and undercutting
it gets an address blocked. `FonotecaOptions.Validate` refuses a lower interval
against that host at startup.

That limit is the identification pipeline's ceiling. A 100,000-track library
needs at least one recording lookup per track and usually a release lookup too,
so a full pass is measured in days — and it is paid again on every re-scan or
metadata refresh. AcoustID's three-per-second limit is the same arithmetic at a
third of the cost, and its answers are cacheable per file, so it is a one-time
9-hour bill rather than a recurring one. MusicBrainz is the bottleneck that
recurs.

MetaBrainz's own answer to this is a mirror. The Live Data Feed exists precisely
so that heavy users stop hammering the public instance: hourly replication
packets, free for non-commercial use behind an access token.

Self-hosting the *other* provider was considered and rejected in the same
breath. AcoustID publishes no full base dump — only daily incremental JSONL
going back to August 2011, about 414 GB compressed across 38,000 files, of which
389 GB is fingerprints — and its server README says outright that the software
is only meant to run on acoustid.org. The public API stays.

## Decision

Run a MusicBrainz mirror as an opt-in local service: the web service, **no
search index**, replicating **daily**.

Three sub-decisions carry the weight.

**Wrap `metabrainz/musicbrainz-docker`; do not reimplement it.** A hand-written
compose file against their published images is maybe eighty lines and looks
cleaner. It would also mean owning the schema migrations. MusicBrainz change the
schema roughly twice a year, and upstream's repo is where `createdb.sh`,
`replication.sh` and `upgrade-db-schema.sh` live and stay correct.
`scripts/musicbrainz-mirror.sh` pins their tag, selects a topology, points the
whole thing at podman, and otherwise gets out of the way.

**No Solr.** It is 250 GB more disk, 12 GB more RAM, and — the part that
actually decides it — *not covered by replication*, so it needs its own rebuild
schedule forever. `IMusicBrainzCatalogue` has no search method by design:
AcoustID supplies the identifier and `RecordingCandidates` chooses between them,
so nothing in Fonoteca would ever call it. The topology is upstream's
`alt-db-only-mirror` base plus one override in `infra/musicbrainz/mirror.yml`
that restores the web server they turn off.

**Its own Compose project.** The mirror is `fonoteca-musicbrainz`, not a profile
in `compose.yaml`. `podman compose down -v` is a normal thing to type while
resetting the dev database, and it must not be able to delete 100 GB that took a
day to build.

## Consequences

- Identification stops being rate-limited by somebody else's fair-use policy.
  `Fonoteca:MusicBrainzRequestIntervalMs=0` becomes legitimate, and the startup
  validation already permits it for a non-official host — no code changed to
  support any of this.
- Search is genuinely gone. `/ws/2/…?query=` fails on the mirror and the local
  website's search box does not work. Accepted: nothing calls it. If tag-based
  matching is ever added as a fallback for files AcoustID cannot identify, this
  decision has to be revisited before that feature is designed, not after.
- The mirror is a day stale at worst. Daily rather than hourly because a
  catalogue manager does not need edits from four hours ago, and the schedule is
  one line in a crontab if that changes.
- Upgrades are now a thing to do: a pinned ref that nobody bumps rots, and a
  schema change stops replication until `upgrade-db-schema.sh` runs.
  `status` surfaces the applied timestamp so a stalled mirror is visible rather
  than silently serving old data.
- It stays optional. `pnpm dev` does not start it, the public instance remains
  the default in `appsettings.json`, and `destroy` plus `rm -rf` removes every
  trace — the same disposability rule the toolchain follows.
