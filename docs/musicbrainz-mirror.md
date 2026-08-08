# Running a MusicBrainz mirror

A local copy of the MusicBrainz database, serving the same `/ws/2` web service
as musicbrainz.org, kept current by the Live Data Feed. No search index.

Why bother: the public instance enforces roughly one request per second, and
Fonoteca needs more than one lookup per track. At 100,000 tracks that is over a
day of wall-clock for a single identification pass, paid again on every
re-scan. A mirror is the supported way to go faster — the *only* supported way.
Undercutting the published rate against the public instance gets the address
blocked, which is why `Fonoteca:MusicBrainzRequestIntervalMs` below 1000 is
refused at startup unless you have pointed `Fonoteca:MusicBrainzServer`
somewhere else.

See [ADR 0006](adr/0006-musicbrainz-mirror.md) for why it is a wrapper around
upstream's Compose project rather than a compose file of our own.

## What you need first

| | |
| --- | --- |
| Download | ~8 GB of bzip2 dumps — minutes, not hours |
| Disk | ~100 GB resting; the dumps expand into Postgres and are indexed on top |
| RAM | 4 GB for the mirror (Postgres is configured for 2 GB of shared buffers) |
| CPU | 2 threads |
| Time | hours, nearly all of it the *import*, not the download |
| Account | a free MetaBrainz access token — sign in at [metabrainz.org](https://metabrainz.org/) and generate one on your profile |

The token is what makes it stay current: replication packets are only served to
token holders. It is free for non-commercial use. Packets are licensed
CC BY-NC-SA 3.0; if this is commercial, talk to MetaBrainz first.

Those numbers are the *without search index* tier. With Solr it would be 350 GB,
16 GB of RAM and 16 threads — see [Why no search index](#why-no-search-index).

## Setting it up

Two things have to be declared before the download starts, both once:

```sh
./scripts/musicbrainz-mirror.sh accept-terms non-commercial   # or: commercial
./scripts/musicbrainz-mirror.sh set-token                     # needs a terminal
./scripts/musicbrainz-mirror.sh setup
```

`accept-terms` records the answer to a question MetaBrainz ask before serving
the dumps: whether you are using the data for commercial or business purposes.
It changes nothing about what is downloaded — it records a declaration and
points at the right sign-up page. It is yours to answer, so the script will not
guess; `setup` stops and tells you if it has not been answered.

`set-token` is the interactive one and needs a real terminal. `setup` will run
without it and stop before starting the server, so the multi-hour import can
proceed while you go and fetch the token.

Then:

```sh
./scripts/musicbrainz-mirror.sh setup
```

That clones a pinned [`musicbrainz-docker`](https://github.com/metabrainz/musicbrainz-docker)
into `.musicbrainz-docker/`, selects the topology, prompts once for the token,
pulls the images, downloads the latest full data dumps, imports them, and starts
the server.

It is re-runnable. If the download dies half way, run it again — the steps that
already finished are detected and skipped, and the dump fetch resumes.

Then point Fonoteca at it, in `.env`:

```sh
Fonoteca__MusicBrainzServer=http://localhost:5000
Fonoteca__MusicBrainzRequestIntervalMs=0
```

and finally catch up on everything published since the dump was cut:

```sh
./scripts/musicbrainz-mirror.sh replicate
```

## Day to day

```sh
./scripts/musicbrainz-mirror.sh status      # containers, replication position, /ws/2 probe
./scripts/musicbrainz-mirror.sh up          # after a reboot, if you stopped it
./scripts/musicbrainz-mirror.sh down        # stop, keep the data
./scripts/musicbrainz-mirror.sh replicate   # apply outstanding packets now
./scripts/musicbrainz-mirror.sh logs        # follow the server
./scripts/musicbrainz-mirror.sh destroy     # remove containers and volumes
```

`status` prints the replication packet number and the timestamp of the last one
applied. That timestamp is the honest answer to "how stale is my data".

## Staying up to date

Replication runs **daily at 03:00 UTC**, as a cron job inside the `musicbrainz`
container. Upstream publishes one packet per hour, so a daily pass applies
twenty-four of them in a batch and the mirror is never more than a day behind.

To change the schedule, edit the crontab that the container mounts:

```sh
$EDITOR .musicbrainz-docker/default/replication.cron
./scripts/musicbrainz-mirror.sh up
```

Hourly (`0 * * * *`) is what the feed is designed for and is not rude — the
packets exist whether you fetch them or not. Daily is the default here because
a catalogue manager does not need edits from four hours ago, and one wake-up a
day is easier on a laptop that sleeps.

The cron only runs while the container is up. A machine that was off overnight
catches up on the next scheduled run, or immediately with `replicate`.

## Upgrading

`UPSTREAM_REF` in `scripts/musicbrainz-mirror.sh` pins the musicbrainz-docker
tag, which tracks a MusicBrainz Server release. Bumping it is the same act as
accepting a new server version, so read upstream's release notes first:

- a plain release is `setup` again — it re-checks out the tag and pulls images
- a **schema change** additionally needs `admin/upgrade-db-schema.sh`, and
  MusicBrainz announce those weeks ahead on their blog

Replication stops working across a schema change until the upgrade is applied.
`status` will show the applied timestamp stop moving, which is the signal.

## Why no search index

Not to be confused with the ordinary Postgres indexes, which this mirror very
much does have — the B-trees on `recording.gid` and friends are what make a
lookup by MBID a millisecond instead of a scan over 35 million rows, and they
are built during the import. Every relational database has those.

The *search* index is a separate thing: a Solr service in its own container,
answering full-text `?query=` requests. It is the expensive half of a mirror:
250 GB more disk, 12 GB more RAM, and either a 4½-hour reindex or a 60 GB
download. Worse, it is **not covered by replication** — you would have to
rebuild it on a schedule of its own, forever.

Fonoteca never asks for it. `IMusicBrainzCatalogue` exposes lookup by MBID and
nothing else, deliberately: AcoustID supplies the identifier, and choosing
between candidate recordings is a domain rule in `RecordingCandidates`, not a
query someone else answers. So the search index would be a quarter of a
terabyte maintaining an endpoint nothing calls.

The consequence, stated plainly: on this mirror `/ws/2/recording?query=…`
fails, and the search box on the local website does not work. Lookup and browse
— everything Fonoteca uses — are straight database reads and work fine.

If you ever want search, upstream documents both routes and this setup can grow
into it; see their README under "Set up search indexes".

## Isolation from the dev database

The mirror runs as its own Compose project, `fonoteca-musicbrainz`, in its own
directory. Fonoteca's own Postgres is project `fonoteca`, from `compose.yaml` at
the repository root.

This is not tidiness. `podman compose down -v` is a normal thing to type while
resetting the dev database, and it must not be able to delete a mirror that took
a day to build. Two projects means it cannot.

`pnpm dev` does not start the mirror and never will — bringing up 100 GB of
Postgres is not part of a dev loop.

## Troubleshooting

**The import container exits 1 in under a second, printing nothing.** That is
upstream's `fetch-dump.sh` hitting its commercial-use prompt with no terminal
attached. It asks with `read -e`, and readline suppresses the prompt entirely
when stdin is not a TTY, so `set -e` kills the script in silence. Run
`accept-terms` once and it never asks again. `setup` checks for this up front
now, so you should see a real message instead.

**First start takes ten minutes and serves nothing.** Expected. When the
configured host and port differ from the ones baked into the image, the server
recompiles its static resources before it listens. `logs` shows it.

**`/ws/2` returns 503 or hangs.** The server is up but the database is not
imported yet, or Postgres is still starting. `status` distinguishes them.

**Replication stopped.** `replication-log` follows `mirror.log` inside the
container, which is where the packet loop reports. The usual causes are an
expired or wrong token and a pending schema change.

**Port 5000 is taken.** `FONOTECA_MB_PORT=5001 ./scripts/musicbrainz-mirror.sh setup`,
and match `Fonoteca__MusicBrainzServer`.

**Reclaiming the ~40 GB of dumps after a successful import.** They sit in the
`dbdump` volume and are only read by `createdb.sh`:

```sh
./scripts/musicbrainz-mirror.sh compose exec musicbrainz bash -c 'rm -rf /media/dbdump/*'
```

Know what you are buying: `recreate-db` then has to download them again, which
is most of the setup cost. Only worth it if the disk is genuinely tight.

**Getting rid of it entirely.** `destroy` removes the containers and volumes;
`rm -rf .musicbrainz-docker` removes the checkout. Nothing is left on the host —
same rule as every toolchain here.
