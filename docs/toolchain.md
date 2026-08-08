# Toolchain

Policy lives in [`~/Repositories/CLAUDE.md`](../../CLAUDE.md): a toolchain is
either a self-contained directory you can `rm -rf`, or it lives in a container
you can `distrobox rm`. **Nothing here is installed with `sudo apt install`.**

Everything Fonoteca needs is Tier 0 — a host directory with a stable symlink,
one line in `~/.local/opt/env.sh` — except PostgreSQL, which runs in podman.

## What this project needs

| Tool | Version | Where | Why Fonoteca needs it |
| --- | --- | --- | --- |
| .NET SDK | 10.0.302 (LTS) | `~/.local/opt/dotnet` | the API |
| Node | 24.18.1 (LTS) | `~/.local/opt/node` | web, design system, Storybook |
| pnpm | 11.20.0 | via `corepack enable` | workspace package manager |
| ffmpeg / ffprobe | 8.1.2 static | `~/.local/opt/ffmpeg` | decode-testing files for corruption; probing stream properties |
| fpcalc | 1.6.1 | `~/.local/opt/chromaprint` | Chromaprint fingerprints for AcoustID |
| podman | 4.9.3 | `/usr/bin` (apt) | container runtime |
| docker-compose | 5.4.0 | `~/.local/bin` | compose provider for `podman compose` |
| Playwright Chromium | 151.0.7922.34 | `~/.cache/ms-playwright` | real browser for the a11y test suite |
| PostgreSQL | 18.4 | container | the catalogue |

`ffmpeg` never writes tags. It rewrites containers and can silently drop
non-standard frames, which is unacceptable on a user's library — ATL.NET and
TagLib# own that path. See [ADR 0002](adr/0002-two-tag-libraries.md).

## First-time setup

Each download is checksum-verified; the digests are recorded in
`~/.local/opt/env.sh` next to the block that puts the tool on PATH.

### .NET 10

```sh
curl -fsSL -o dotnet-sdk.tar.gz \
  https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-linux-x64.tar.gz
# Microsoft publishes a sha512 per file in the channel metadata:
#   curl -fsSL https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json \
#     | jq -r '.releases[0].sdk.files[] | select(.rid=="linux-x64" and (.name|endswith("tar.gz"))) | .hash'
sha512sum -c - <<<'<digest>  dotnet-sdk.tar.gz'
mkdir -p ~/.local/opt/dotnet-10.0.302
tar -xzf dotnet-sdk.tar.gz -C ~/.local/opt/dotnet-10.0.302
ln -sfn dotnet-10.0.302 ~/.local/opt/dotnet
```

Installed from the tarball rather than `dotnet-install.sh` on purpose: the
script appends its own PATH lines to shell rc files, which would put a toolchain
on PATH that `env.sh` does not list — and `env.sh` is the manifest.

### ffmpeg and fpcalc

Neither upstream publishes a checksum file, but the GitHub releases API returns
a server-computed digest per asset:

```sh
curl -fsSL https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest \
  | jq -r '.assets[] | select(.name|test("linux64-gpl-8\\.1\\.tar\\.xz$")) | "\(.digest)  \(.browser_download_url)"'

curl -fsSL https://api.github.com/repos/acoustid/chromaprint/releases/latest \
  | jq -r '.assets[] | select(.name|test("linux-x86_64")) | "\(.digest)  \(.browser_download_url)"'
```

Extract each to a versioned directory under `~/.local/opt` and symlink the
unversioned name, as for every other Tier 0 tool.

### Playwright Chromium

The design system's tests run every story through axe in a **real browser**.
jsdom is not an option here: axe checks computed styles, colour contrast and the
accessibility tree, and jsdom models none of them faithfully — contrast least of
all, which is most of what a hand-built palette needs checked.

```sh
pnpm --filter @fonoteca/ui exec playwright install chromium
```

Playwright manages its own browser cache in `~/.cache/ms-playwright`, outside
both the project and `~/.local/opt`. That is fine for Tier 0: it is disposable,
re-downloadable, and shared with any other project on this machine that uses
Playwright.

**No apt packages are required** — verified on this host. Playwright warns that
Pop!_OS is not an officially supported distribution and falls back to its
ubuntu24.04-x64 build, which launches cleanly. Do NOT run
`playwright install-deps`; it shells out to `apt-get install` and would drag
system libraries onto the host in violation of the policy. If a future browser
version does fail to launch, read what it actually asks for and put *that* in a
distrobox rather than on the host.

### Container tooling

```sh
systemctl --user enable --now podman.socket    # Testcontainers and compose need this
```

`podman compose` delegates to an external compose implementation and ships
none. The standalone Docker Compose binary is a single static file:

```sh
curl -fsSL -o docker-compose \
  https://github.com/docker/compose/releases/download/v5.4.0/docker-compose-linux-x86_64
sha256sum -c - <<<'837fd1d35bf6a494f41b5b5988269a7be79de337cf1a1a6ff0e45ab51bb4e9be  docker-compose'
install -m 0755 docker-compose ~/.local/bin/docker-compose
```

## Verifying

```sh
source ~/.local/opt/env.sh
dotnet --version     # 10.0.302
node --version       # v24.18.1
ffmpeg -version      # 8.1.2, from ~/.local/opt
fpcalc -version      # 1.6.1
podman compose version
```

If any of these resolve outside `~/.local/opt`, `env.sh` is not being sourced.
It must be sourced from **both** `~/.profile` and `~/.bashrc` — Ubuntu's stock
`.bashrc` returns early when non-interactive, so `.bashrc` alone leaves the
toolchain invisible to scripts, IDEs and `bash -lc` while looking fine at a
prompt.

## Gotchas found while setting this up

**Testcontainers and podman.** Testcontainers speaks the Docker API; podman's
user socket serves it. Two things differ from Docker: `DOCKER_HOST` must point
at the socket, and Ryuk (the resource reaper) must be disabled, because podman
does not serve its image. `PostgresFixture` sets both when they are unset, so
the suite runs without per-machine setup.

**PostgreSQL 18 changed its data directory.** The image now expects a single
mount at `/var/lib/postgresql` and places the cluster in a major-version
subdirectory, so `pg_upgrade --link` need not cross a mount boundary. Mounting
the pre-18 `/var/lib/postgresql/data` path makes the container exit on start.

**Playwright's Chromium needs nothing from apt on this host.** It warns that the
distribution is unsupported and downloads its ubuntu24.04-x64 fallback, which
launches headless without error. This was worth testing rather than assuming —
it is what kept the a11y suite in Tier 0 instead of a container.

**openapi-typescript cannot run on TypeScript 7.** It drives the compiler API,
which TS 7.0 does not ship until 7.1. The generator is isolated in
`tools/openapi-codegen` with its own pinned TypeScript 5.9; see
[ADR 0005](adr/0005-typescript-7.md).

## Removing all of it

```sh
rm -rf ~/.local/opt/dotnet-10.0.302 ~/.local/opt/dotnet
rm -rf ~/.local/opt/ffmpeg-8.1.2 ~/.local/opt/ffmpeg
rm -rf ~/.local/opt/chromaprint-1.6.1 ~/.local/opt/chromaprint
rm -rf ~/.nuget/packages                 # NuGet cache, outside the SDK tree
rm -f  ~/.local/bin/docker-compose
rm -rf ~/.cache/ms-playwright            # browser cache (shared; check first)
podman compose down -v                   # database and its volume
```

Then delete the corresponding blocks from `~/.local/opt/env.sh`.
