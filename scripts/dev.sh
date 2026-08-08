#!/usr/bin/env bash
#
# Starts everything needed for development and shuts it all down together.
#
# No process-manager dependency: bash job control plus one trap does the job,
# and there is nothing to keep up to date.
#
#   postgres    container (podman)
#   api         host, dotnet watch, :5088
#   web         host, vite,         :5173
#   storybook    host, storybook,    :6006  (skip with --no-storybook)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# shellcheck source=/dev/null
[ -f "$HOME/.local/opt/env.sh" ] && . "$HOME/.local/opt/env.sh"

# Load .env and export it, so the API inherits the same values compose reads.
#
# `set -a` marks everything defined while it is on for export, which is what
# lets ASP.NET Core see `Fonoteca__LibraryPath` and friends. Compose reads .env
# by itself; the API does not, hence this.
#
# Note the ordering: values already in the environment win, because `set -a`
# only affects assignments in the file, and an operator overriding one on the
# command line should not be silently reverted.
if [ -f "$REPO_ROOT/.env" ]; then
  set -a
  # shellcheck source=/dev/null
  . "$REPO_ROOT/.env"
  set +a
else
  echo "note: no .env found — copy .env.example to .env" >&2
fi

WITH_STORYBOOK=1
for arg in "$@"; do
  case "$arg" in
    --no-storybook) WITH_STORYBOOK=0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

# Testcontainers and podman compose both speak the Docker API. Point them at
# podman's user socket unless the caller already chose something.
if [ -z "${DOCKER_HOST:-}" ] && [ -S "${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/podman/podman.sock" ]; then
  export DOCKER_HOST="unix://${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/podman/podman.sock"
fi

for tool in dotnet node pnpm podman; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "missing: $tool — see docs/toolchain.md" >&2
    exit 1
  }
done

pids=()

cleanup() {
  echo
  echo "shutting down…"
  for pid in "${pids[@]:-}"; do
    [ -n "$pid" ] && kill "$pid" 2>/dev/null || true
  done
  wait 2>/dev/null || true
  echo "containers left running; stop them with: podman compose down"
}
trap cleanup EXIT INT TERM

echo "==> postgres"
podman compose up -d
# `up -d` returns once the container is started, not once Postgres will accept a
# query. The API applies migrations at startup, so racing it here means a failed
# boot rather than a slow one.
printf '    waiting for health'
for _ in $(seq 1 40); do
  status="$(podman inspect --format '{{.State.Health.Status}}' fonoteca-postgres-1 2>/dev/null || echo starting)"
  [ "$status" = "healthy" ] && break
  printf '.'
  sleep 1
done
echo " ${status:-unknown}"

echo "==> api        http://localhost:5088"
ASPNETCORE_ENVIRONMENT=Development \
  dotnet watch --project apps/api/src/Fonoteca.Api --non-interactive \
  run --no-launch-profile --urls http://localhost:5088 &
pids+=($!)

echo "==> web        http://localhost:5173"
pnpm --filter @fonoteca/web dev &
pids+=($!)

if [ "$WITH_STORYBOOK" -eq 1 ]; then
  echo "==> storybook  http://localhost:6006"
  pnpm --filter @fonoteca/ui storybook &
  pids+=($!)
fi

echo
echo "Ctrl-C to stop."
wait
