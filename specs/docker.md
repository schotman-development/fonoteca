# Docker

Run Fonoteca as a container, published to the GitHub Container Registry.

## Image

Single-stage `python:3.12-slim`. `requirements.txt` is copied and installed on
its own layer so editing source does not re-run pip. No multi-stage build: there
is nothing to compile, so a builder stage would only move the same wheels around.

Three settings are baked in as `ENV` because their defaults are wrong in a
container:

| Var | Default | In the image | Why |
|---|---|---|---|
| `HOST` | `127.0.0.1` | `0.0.0.0` | the loopback default is reachable by nothing outside the container |
| `LIBRARY_PATH` | `music` | `/music` | `config.py` resolves a relative path against `BASE_DIR` (`/app`), i.e. the image layer, not the mount |
| `DATA_PATH` | `data` | `/data` | same |

Runs as uid 1000, which both deployments then reconcile with the host — there is
no PUID/PGID entrypoint script, because neither runtime needs one:

- Podman: `UserNS=keep-id:uid=1000,gid=1000` maps the host user, whatever their
  uid, to 1000 inside. Not optional — rootless Podman otherwise maps you to uid
  0 in the container and both bind mounts come out unwritable.
- Docker: `user: "${PUID:-1000}:${PGID:-1000}"`, read from the same `.env`.

`HEALTHCHECK` calls `/health` (which probes the database too) with a stdlib
`urllib` one-liner. slim ships no curl and this does not justify installing one.

## Two deployment files

- `deploy/fonoteca.container` — a Podman Quadlet unit. The recommended path.
- `compose.yaml` — the Docker path.

They exist separately rather than one wrapping the other because Quadlet is what
makes updating free (below), and it has no compose equivalent.

## Volumes

Both are bind mounts, and both must be writable by the running uid.

- `${MUSIC_DIR:-./music}:/music` — the library. Deliberately a bind mount rather
  than a named volume so it can be browsed, added to and backed up from the
  host. Cannot be `:ro`: Fonoteca downloads into it, and re-files, re-tags and
  trashes what is already there.
- `${DATA_DIR:-./data}:/data` — SQLite database, logs, `secret.cache`, trash and
  partial downloads. A bind mount for the same reason: the database must survive
  `docker compose down -v`, and it is the thing worth backing up.

`env_file: .env` carries the credentials. `QOBUZ_APP_ID` and
`QOBUZ_USER_AUTH_TOKEN` are required; compose fails outright if `.env` is
missing, which is the correct failure.

## Publishing

`.github/workflows/docker.yml`, on `release: published` plus
`workflow_dispatch`. Runs the test suite first — the image is the release
artifact and should not ship failing code — then builds and pushes
`ghcr.io/schotman-development/fonoteca:$TAG` using `github.token`, no stored
secret. `:latest` moves only for a real, non-pre release, so neither a
pre-release nor a manual dispatch off `main` can claim it.

Plain `docker build`/`docker push` in shell rather than
`docker/build-push-action` + `metadata-action`: two fewer actions to keep pinned,
and it matches what the aria repo already does.

## Updating

**Podman:** `systemctl --user enable --now podman-auto-update.timer`. That is
the whole mechanism — `AutoUpdate=registry` on the Quadlet unit plus a timer
that already ships with Podman. Nothing was written to make it work.

This is the reason for choosing Quadlet over compose. `podman auto-update`
restarts *systemd units*, and compose-managed containers are not units — which
is why the aria repo had to hand-write a `podman-compose@.service` wrapper
purely to give auto-update something to restart. A `.container` file is itself
the unit source, so the wrapper disappears.

It also rolls back. If the updated image fails to come up, Podman reverts to the
previous one. `HealthOnFailure=stop` is what connects an unhealthy container to
a failed unit, and `Restart=on-failure` (not `always`) is what stops systemd
from resurrecting a broken container and hiding the failure.

**Docker:** `docker compose pull && docker compose up -d`.

No watchtower, on either path. It needs `/var/run/docker.sock` — root-equivalent
on the host — cannot roll back a bad `:latest`, and is unreliable against
Podman's docker-compat socket anyway. A two-word command is not worth a daemon
with root.

## Deliberately not done

- **Multi-arch (`buildx` + QEMU).** Roughly triples release build time. Add
  `platforms: linux/amd64,linux/arm64` when Fonoteca actually needs to run on a
  Pi or an arm64 NAS.
- **A separate CI workflow for pushes/PRs.** The repo has none today. The test
  step here only gates releases; wiring up per-PR CI is its own change.
- **In-app self-update.** The container is immutable and the registry already
  distributes versions; an updater inside the process would fight both.
