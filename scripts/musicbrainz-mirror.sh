#!/usr/bin/env bash
#
# A local MusicBrainz mirror: the web service, no search index, replicated once
# a day.
#
# This is a thin driver over metabrainz/musicbrainz-docker, which is the
# supported way to run a mirror. We do not reimplement it. Schema changes land
# roughly twice a year and upstream is where the migration scripts live; owning
# a hand-written copy of their compose file means owning those migrations too.
#
# What this script adds on top of a plain checkout:
#
#   * pins the upstream ref, so `setup` is reproducible
#   * picks the topology — db-only base plus our override, so no Solr
#   * points it at podman and gives it its own compose project, so the
#     100 GB of mirror data can never be caught by `podman compose down` in
#     the Fonoteca project
#   * probes readiness the way Fonoteca does, over WS/2 on the published port
#
#   ./scripts/musicbrainz-mirror.sh help

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# shellcheck source=/dev/null
[ -f "$HOME/.local/opt/env.sh" ] && . "$HOME/.local/opt/env.sh"

# Upstream, pinned. Their tags track the MusicBrainz Server release the images
# were built for, so bumping this is the same act as accepting a new server
# version. Read their release notes before you move it: a schema change needs
# `admin/upgrade-db-schema.sh` and not just a restart.
readonly UPSTREAM_URL='https://github.com/metabrainz/musicbrainz-docker.git'
readonly UPSTREAM_REF='v-2026-07-30.1'

readonly MIRROR_DIR="$REPO_ROOT/.musicbrainz-docker"
readonly OVERRIDE_SRC="$REPO_ROOT/infra/musicbrainz/mirror.yml"
readonly OVERRIDE_DST='local/fonoteca-mirror.yml'

# Separate compose project on purpose. Same reason the mirror is not a profile
# in compose.yaml: `podman compose down -v` is a normal thing to type while
# resetting the dev database, and it must not be able to delete a mirror that
# took a day to build.
readonly PROJECT='fonoteca-musicbrainz'

readonly PORT="${FONOTECA_MB_PORT:-5000}"

# One recording that has existed for years, used as the readiness probe. It is
# the same MBID the provider tests pin, so a green probe here and a green test
# suite mean the same thing.
readonly PROBE_MBID='cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff'

# Free space wanted before the import starts, in GB. Upstream quotes 100 GB for
# a mirror without search indexes; the dumps are downloaded and then expanded,
# so the peak is higher than the resting size.
readonly WANT_FREE_GB=140

say()  { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
note() { printf '    %s\n' "$*"; }
die()  { printf >&2 '\n%s: %s\n' "${0##*/}" "$*"; exit 1; }

# musicbrainz-docker's admin/* scripts shell out to `docker` unless told
# otherwise, and would find none. Both variables are theirs, documented in
# admin/lib/common.inc.bash.
export DOCKER_CMD='podman'
export DOCKER_COMPOSE_CMD='podman compose'
export COMPOSE_PROJECT_NAME="$PROJECT"

compose() { (cd "$MIRROR_DIR" && podman compose "$@"); }

# `podman compose` announces which external provider it is delegating to, on
# STDOUT, above the command's own output. Taking the last non-empty line is what
# separates the answer from the announcement — without it every reading comes
# back as a banner with a number stuck to the end, and `status` reports a
# perfectly healthy database as unreachable.
psql_db() {
  compose exec -T db psql -U musicbrainz -d musicbrainz_db -tAc "$1" 2>/dev/null \
    | tr -d ' ' | grep -v '^$' | tail -1
}

require_setup() {
  [ -d "$MIRROR_DIR/.git" ] || die "not set up yet — run: $0 setup"
}

# Compose refuses to start the musicbrainz service without the secret file, and
# the error it gives names a path inside the checkout rather than the thing to
# do about it.
require_token() {
  [ -s "$MIRROR_DIR/local/secrets/metabrainz_access_token" ] \
    || die "no replication token — run: $0 setup"
}

# ---------------------------------------------------------------------------

preflight() {
  for tool in git podman curl; do
    command -v "$tool" >/dev/null 2>&1 || die "missing: $tool — see docs/toolchain.md"
  done

  # musicbrainz-docker's own scripts require bash 4+ for associative arrays.
  ((BASH_VERSINFO[0] >= 4)) || die "bash 4+ required; found ${BASH_VERSION}"

  podman info >/dev/null 2>&1 || die 'podman is not responding — is the user socket up?'

  # Check the filesystem that actually holds the volumes, not $PWD — the
  # database and the downloaded dumps both live there, and being wrong about
  # which disk that is costs a day of downloading.
  local store free_gb
  store="$(podman info --format '{{.Store.VolumePath}}' 2>/dev/null)"
  [ -n "$store" ] || store="$(podman info --format '{{.Store.GraphRoot}}' 2>/dev/null || echo "$HOME")"
  free_gb="$(df -BG --output=avail "$store" 2>/dev/null | tail -1 | tr -dc '0-9')"
  if [ -n "$free_gb" ] && [ "$free_gb" -lt "$WANT_FREE_GB" ]; then
    say 'not much room'
    note "${free_gb}G free on $store; the import wants about ${WANT_FREE_GB}G"
    note 'peak usage is during the import, when the downloaded dumps and the'
    note 'imported database are both on disk. It settles to ~100G afterwards —'
    note 'see docs/musicbrainz-mirror.md for reclaiming the dumps.'
    confirm 'Continue anyway?'
  fi
}

fetch_upstream() {
  if [ -d "$MIRROR_DIR/.git" ]; then
    local head
    head="$(git -C "$MIRROR_DIR" rev-parse HEAD)"
    if [ "$head" = "$(git -C "$MIRROR_DIR" rev-parse "$UPSTREAM_REF^{commit}" 2>/dev/null)" ]; then
      note "already at $UPSTREAM_REF"
      return
    fi
    say "updating musicbrainz-docker to $UPSTREAM_REF"
    git -C "$MIRROR_DIR" diff --quiet HEAD -- ':!local' \
      || die "$MIRROR_DIR has local modifications outside local/ — resolve them first"
    # Explicit refspec, and shallow: the clone below is --depth 1, and a plain
    # `fetch --tags` on a shallow clone can leave the tag pointing at an object
    # that was never downloaded.
    git -C "$MIRROR_DIR" fetch --depth 1 origin \
      "refs/tags/${UPSTREAM_REF}:refs/tags/${UPSTREAM_REF}"
    git -C "$MIRROR_DIR" checkout --quiet "$UPSTREAM_REF"
  else
    say "cloning musicbrainz-docker $UPSTREAM_REF"
    git clone --quiet --branch "$UPSTREAM_REF" --depth 1 "$UPSTREAM_URL" "$MIRROR_DIR"
  fi
}

configure() {
  say 'configuring topology'

  install -D -m 0644 "$OVERRIDE_SRC" "$MIRROR_DIR/$OVERRIDE_DST"

  # Compose reads this file too, so anything set here also reaches the
  # containers. admin/configure only rewrites its own COMPOSE_FILE line, so
  # these survive it.
  local env_file="$MIRROR_DIR/.env"
  touch "$env_file"
  set_env "$env_file" COMPOSE_PROJECT_NAME "$PROJECT"
  set_env "$env_file" FONOTECA_MB_PORT "$PORT"
  set_env "$env_file" MUSICBRAINZ_WEB_SERVER_PORT "$PORT"

  # `with` replaces the base; `add` appends. Order matters — `with` blanks the
  # list, so it has to come first.
  (cd "$MIRROR_DIR" && ./admin/configure with alt-db-only-mirror >/dev/null)
  (cd "$MIRROR_DIR" && ./admin/configure add \
    replication-token replication-cron local-fonoteca-mirror >/dev/null)

  note "$(cd "$MIRROR_DIR" && grep '^COMPOSE_FILE=' .env)"
  note 'daily replication at 03:00 UTC (upstream default/replication.cron)'
}

set_env() {
  local file=$1 key=$2 value=$3
  if grep -q "^${key}=" "$file"; then
    sed -i "s|^${key}=.*|${key}=${value}|" "$file"
  else
    printf '%s=%s\n' "$key" "$value" >> "$file"
  fi
}

token_path() { printf '%s/local/secrets/metabrainz_access_token' "$MIRROR_DIR"; }

# The token buys replication packets. It does NOT buy the data dumps, which are
# public — so a run with no terminal to prompt at can still do the download and
# the import, which is the part measured in hours.
#
# Compose mounts the secret as a file and refuses to create the container if it
# is missing, so the placeholder is what lets the import proceed. Everything
# that would actually use the token goes through require_token, which tests for
# a non-empty file.
ensure_token() {
  local token
  token="$(token_path)"

  if [ -s "$token" ]; then
    note 'replication token already present'
    return
  fi

  say 'replication token'
  note 'Free for non-commercial use: sign in at https://metabrainz.org/ and'
  note 'generate one on your profile. 40 characters.'

  if [ ! -t 0 ]; then
    note
    note 'No terminal to prompt at, so this step is deferred. Setup will'
    note 'continue through the import and stop before starting the server.'
    note "When you have the token:  $0 set-token"
    install -D -m 0600 /dev/null "$token"
    return
  fi

  note 'It will not be echoed.'
  echo
  (cd "$MIRROR_DIR" && ./admin/set-replication-token --force)
}

cmd_set_token() {
  require_setup
  install -d -m 0700 "$(dirname "$(token_path)")"
  # --force because ensure_token may have left an empty placeholder, and
  # upstream refuses to overwrite an existing file without it.
  (cd "$MIRROR_DIR" && ./admin/set-replication-token --force)
  note "now: $0 up"
}

database_is_populated() {
  compose up -d db >/dev/null 2>&1 || return 1
  # EXISTS rather than COUNT: this only has to answer "is there anything here",
  # and counting 30-odd million rows to decide that is a scan nobody needs.
  [ "$(psql_db 'select exists (select 1 from musicbrainz.recording)' || true)" = 't' ]
}

# Before its first download, upstream's fetch-dump.sh asks whether the data is
# for commercial use. The answer changes nothing about what is fetched — it
# records a declaration and points at the right MetaBrainz sign-up page — and it
# is remembered as a marker file in the dbdump volume.
#
# It is asked with `read -e`, so readline suppresses the prompt entirely when
# stdin is not a terminal, and `set -e` then kills the script. The visible
# symptom is a container that exits 1 in 40ms having printed nothing at all,
# which is why this is checked up front instead.
dumps_terms_accepted() {
  compose run --rm --no-deps musicbrainz \
    bash -c 'compgen -G "/media/dbdump/.for-*-use" >/dev/null' >/dev/null 2>&1
}

cmd_accept_terms() {
  require_setup
  local kind=${1:-}
  case "$kind" in
    commercial | non-commercial) ;;
    *) die 'usage: accept-terms <commercial|non-commercial>' ;;
  esac

  compose run --rm --no-deps musicbrainz \
    touch "/media/dbdump/.for-${kind}-use" >/dev/null
  note "recorded: ${kind} use"
  if [ "$kind" = commercial ]; then
    note 'MetaBrainz ask commercial users to support them financially:'
    note '  https://metabrainz.org/supporters/account-type'
  else
    note 'MetaBrainz ask non-commercial users to sign up (free) so they can see'
    note 'how the data is used, and to donate if you can:'
    note '  https://metabrainz.org/supporters/account-type'
    note '  https://metabrainz.org/donate'
  fi
}

create_database() {
  if database_is_populated; then
    note 'database already holds data — skipping import'
    note "force a rebuild with: $0 recreate-db"
    return
  fi

  if ! dumps_terms_accepted; then
    say 'MetaBrainz terms declaration required'
    note 'Before the first download, MetaBrainz ask whether you are using this'
    note 'data for commercial or business purposes. It is your declaration to'
    note 'make, not this script’s, and it is asked once.'
    note
    note 'It changes nothing about what is downloaded — it records the answer'
    note 'and points at the right sign-up page.'
    note
    note "  $0 accept-terms non-commercial"
    note "  $0 accept-terms commercial"
    note
    die 'no declaration recorded'
  fi

  say 'downloading dumps and creating the database'
  note 'The download is about 8 GB of bzip2 and takes minutes. The import that'
  note 'follows is the long part — it expands to roughly 100 GB of Postgres and'
  note 'then builds the indexes over it. Expect hours.'
  note 'A partial download resumes; re-run setup.'
  echo
  compose run --rm musicbrainz createdb.sh -fetch
}

wait_ready() {
  local budget=${1:-900} waited=0
  printf '    waiting for /ws/2 on :%s' "$PORT"
  while [ "$waited" -lt "$budget" ]; do
    if probe >/dev/null 2>&1; then
      printf ' ready (%ss)\n' "$waited"
      return 0
    fi
    printf '.'
    sleep 5
    waited=$((waited + 5))
  done
  printf ' not ready after %ss\n' "$budget"
  note "check: $0 logs"
  return 1
}

probe() {
  curl -fsS --max-time 10 \
    -A 'fonoteca-mirror-probe/1.0 ( readiness check, local mirror only )' \
    -o /dev/null \
    "http://localhost:${PORT}/ws/2/recording/${PROBE_MBID}?fmt=json"
}

# ---------------------------------------------------------------------------

cmd_setup() {
  preflight
  fetch_upstream
  configure

  say 'pulling images'
  # Both services are `build:` stanzas whose Dockerfiles are a single FROM
  # against a published image, so this pulls rather than compiles. It runs
  # before the token prompt so the unattended part happens first.
  compose build --pull

  ensure_token
  create_database

  if [ ! -s "$(token_path)" ]; then
    say 'not started'
    note 'The database is ready but there is no replication token, so the'
    note 'server has not been started — it would come up unable to replicate.'
    note "  $0 set-token"
    note "  $0 up"
    return
  fi

  say 'starting'
  compose up -d
  wait_ready 1800 || true

  cmd_status
  echo
  note 'Point Fonoteca at it — in .env:'
  note "  Fonoteca__MusicBrainzServer=http://localhost:${PORT}"
  note '  Fonoteca__MusicBrainzRequestIntervalMs=0'
  echo
  note "Then catch up on everything published since the dump: $0 replicate"
}

cmd_up() {
  require_setup
  require_token
  compose up -d
  wait_ready 900 || true
  cmd_status
}

cmd_down() {
  require_setup
  say 'stopping (volumes kept)'
  compose down
}

cmd_status() {
  require_setup
  say 'containers'
  compose ps

  say 'replication'
  local seq applied recordings
  seq="$(psql_db 'select current_replication_sequence from musicbrainz.replication_control' || true)"
  applied="$(psql_db 'select last_replication_date from musicbrainz.replication_control' || true)"
  # The planner's estimate, not a count. It is maintained by autovacuum and is
  # close enough for "does this look like a full mirror", which is the only
  # question being asked.
  recordings="$(psql_db "select reltuples::bigint from pg_class where oid = 'musicbrainz.recording'::regclass" || true)"

  if [ -n "$seq" ]; then
    note "packet      ${seq}"
    note "applied     ${applied:-never}"
    note "recordings  ~${recordings:-?}"
  else
    note 'database not reachable, or not imported yet'
  fi

  say 'web service'
  if probe >/dev/null 2>&1; then
    note "http://localhost:${PORT}/ws/2  ok"
  else
    note "http://localhost:${PORT}/ws/2  not answering"
  fi
  note 'search is not installed — /ws/2 lookups work, ?query= does not'
}

cmd_replicate() {
  require_setup
  require_token
  say 'replicating now'
  note 'The daily cron does this at 03:00 UTC; this is the manual catch-up.'
  note 'Each packet is one hour of edits, so a long gap takes a while.'
  note "Progress goes to mirror.log inside the container, not here — follow it"
  note "in another terminal with: $0 replication-log"
  compose exec -T musicbrainz replication.sh
  cmd_status
}

cmd_logs() {
  require_setup
  compose logs --follow --tail 100 "${@:-musicbrainz}"
}

cmd_replication_log() {
  require_setup
  compose exec musicbrainz tail --follow mirror.log
}

cmd_recreate_db() {
  require_setup
  say 'recreating the database from fresh dumps'
  note 'This discards the mirror and re-imports. Hours.'
  confirm 'Re-import the database?'
  compose run --rm musicbrainz createdb.sh -fetch
  compose up -d
  wait_ready 1800 || true
}

cmd_destroy() {
  require_setup
  say 'destroying the mirror'
  note "This removes the containers AND the volumes of project '$PROJECT'."
  note 'Rebuilding means downloading the dumps again.'
  confirm 'Delete the mirror data?'
  compose down --volumes
  note "checkout left at $MIRROR_DIR — remove it by hand if you want it gone"
}

confirm() {
  local reply
  [ -t 0 ] || die "$1 — refusing to assume an answer with no terminal attached"
  read -r -p "    $1 [y/N] " reply
  case "$reply" in
    y | Y | yes) ;;
    *) die 'aborted' ;;
  esac
}

cmd_compose() {
  require_setup
  compose "$@"
}

cmd_help() {
  cat <<EOF
Usage: ${0##*/} <command>

  setup            clone, configure, import, start  (hours, mostly download)
  accept-terms <commercial|non-commercial>
                   record the MetaBrainz use declaration the dumps ask for
  set-token        enter the MetaBrainz access token (prompts; needs a terminal)
  up               start the mirror
  down             stop it, keeping the data
  status           containers, replication position, WS/2 reachability
  replicate        apply outstanding replication packets now
  logs [service]   follow container logs (default: musicbrainz)
  replication-log  follow mirror.log inside the container
  recreate-db      re-import from fresh dumps
  destroy          remove containers and volumes
  compose ...      run podman compose in the checkout

The mirror runs as compose project '$PROJECT' in $MIRROR_DIR,
pinned to musicbrainz-docker $UPSTREAM_REF.

Environment:
  FONOTECA_MB_PORT        host port for the web service (default 5000)
  FONOTECA_MB_PROCESSES   server worker processes (default 4)

See docs/musicbrainz-mirror.md.
EOF
}

case "${1:-help}" in
  setup)           shift; cmd_setup "$@" ;;
  accept-terms)    shift; cmd_accept_terms "$@" ;;
  set-token)       shift; cmd_set_token "$@" ;;
  up)              shift; cmd_up "$@" ;;
  down)            shift; cmd_down "$@" ;;
  status)          shift; cmd_status "$@" ;;
  replicate)       shift; cmd_replicate "$@" ;;
  logs)            shift; cmd_logs "$@" ;;
  replication-log) shift; cmd_replication_log "$@" ;;
  recreate-db)     shift; cmd_recreate_db "$@" ;;
  destroy)         shift; cmd_destroy "$@" ;;
  compose)         shift; cmd_compose "$@" ;;
  help | --help | -h) cmd_help ;;
  *) printf >&2 'unknown command: %s\n\n' "$1"; cmd_help; exit 2 ;;
esac
