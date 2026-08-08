# Qobuzarr's front end

The React single-page app that is Qobuzarr's web interface. It talks to the
FastAPI server's `/api/*` JSON and nothing else — there is no private back
channel, no server-rendered HTML, and nothing the interface can do that the API
cannot.

React 19, TypeScript (`strict` plus `noUncheckedIndexedAccess`), Vite, TanStack
Query, React Router, CSS Modules over one file of custom properties. No Tailwind,
no component library, no CSS-in-JS, and nothing fetched over the network at
runtime: the font stack names IBM Plex and falls through to the system faces, so
the app renders identically on a machine with no internet at all.

---

## Running it

Node ≥ 20 has to be on `PATH`. On this machine that is one line:

```bash
export PATH=~/.local/opt/node/bin:$PATH
npm install --prefix web       # once
```

**Day-to-day work is two processes, not a rebuild loop:**

```bash
./.venv/bin/python -m uvicorn main:app --port 8000   # terminal one
npm run dev --prefix web                             # terminal two → :5173
```

Vite proxies `/api`, `/health` and `/static` through to uvicorn on 8000, which is
the whole reason there is no mock layer in this repository. The dev server is
driving the real database, the real library folder and the real Qobuz
credentials, so a screen that works here works in production for the same reason
rather than by coincidence — and a wrong assumption about a payload shows up
immediately instead of after a build. Two consequences worth holding in mind:
the same care applies as anywhere else in Qobuzarr (following an artist really
follows them, and a running server really downloads), and if `/api` calls start
failing with "Qobuzarr is not responding", the thing that is not running is
uvicorn.

**Building for the server to serve:**

```bash
npm run build --prefix web     # tsc -b && vite build → ../static/app
```

`static/app` is a build artefact: gitignored, never edited by hand, never
committed. `base` is `/static/app/` rather than `/` because FastAPI serves the
whole directory through its existing `/static` mount — only `index.html` needs a
route of its own, because it answers for paths that do not exist on disk. The
server checks for the file on each request, so a build that lands while it is
running is picked up without a restart, and a checkout that has never been built
answers every page with a short document naming this one command.

**Everything else:**

```bash
npm test --prefix web          # vitest run — 1050 tests in 101 files, jsdom
npm run test:watch --prefix web
npm run typecheck --prefix web # tsc -b, no emit
npm run lint --prefix web      # oxlint
```

---

## Layout

```
src/
  main.tsx        entry: three stylesheets in order (tokens → reset → base), one
                  QueryClient from @/api/queries, the router, App.
  App.tsx         the shell + the route table + one error boundary keyed on the
                  path. Owns the omnisearch and the two queries the FRAME needs
                  (nav counts, status) — no screen re-fetches those.
  routes.tsx      every route, code-split with React.lazy behind one Suspense
                  boundary. Read the header: the Health section is at /system.

  api/
    client.ts     the one fetch wrapper. Error envelope → ApiError, query
                  building, path encoding. Nothing else calls fetch.
    types.ts      a hand-kept mirror of app/schemas.py. See "The wire" below.
    queries.ts    every query and mutation hook, the queryKeys factory, the
                  REFETCH / LIMITS / STALE tables, createQueryClient and the one
                  invalidateLive helper.

  design/         the design system: 37 primitives, each a folder of
                  Thing.tsx + Thing.module.css + Thing.test.tsx + index.ts.
                  Import from '@/design'. It has its own README, which is the
                  authority — read that before adding a component.
  widgets/        the domain layer: ReleaseRow, MonitorToggle, CompletionCell,
                  BulkBar, StatStrip, PageError, PageLoading, RelativeTime…
                  A widget knows what a release is; a primitive does not. That
                  is the whole dividing line.
  screens/        one folder per section — library/ activity/ health/ settings/ —
                  and one default-exported component per route. Pure helpers a
                  screen needs (artistPatch.ts, releaseFacts.ts, releaseAction.ts)
                  live beside it in their own file, so they can be tested without
                  mounting anything.
  shell/          AppShell, TopBar, Sidebar, TitleBar, StatusFooter, ToastHost,
                  SkipLink. nav.ts is the navigation IA as data.
  format/         the old Jinja filters as pure functions: fmtAgo, fmtSpan,
                  fmtSize, fmtDuration, fmtQuality, formatLabel. All of them
                  return EM_DASH for an unknown, none of them throws, and there
                  is exactly one EM_DASH (in format/constants.ts).
  styles/         tokens.css (every value in the app), reset.css, base.css.
  test/           setup.ts (jest-dom + cleanup), providers.tsx (router + query
                  client + toast host), factories.ts (complete wire fixtures).
```

`@/` is an alias for `src/`, declared in both `vite.config.ts` and
`tsconfig.app.json` — change one and change the other.

---

## The four rules that are not style preferences

Each of these was a bug before it was a rule, and each is held by a module rather
than by a convention, because a convention gets a fresh chance to be broken at
every new call site.

### One query key, one cache

A screen and its own refresh are the same query with the same parameters. This is
the HTML app's rule translated: it rendered a live region's first frame inline and
re-fetched the identical fragment on a timer, and what kept going wrong was the
*context* — give the polled fragment a different row limit from the page that
rendered it and the screen silently rearranges itself five seconds after it loads.

So: **a component never fetches a narrowed copy of data another component already
has.** Every key comes from `queryKeys` in `api/queries.ts`, row limits are the
exported `LIMITS` object read by both the caller and the key, and `cleanParams`
drops `null`/`undefined` so `{q: undefined}` and `{}` hash identically — two
components asking for the same effective list share one entry instead of quietly
running two. The shell asks for `status?activity_limit=0` for exactly this
reason: the footer's payload must not be a narrowed copy of the Activity
screen's.

**Mutations invalidate; GETs never do.** `invalidateLive()` from a mutation's
`onSuccess`, and nothing else invalidates anything — the old contract was that
every mutating POST refreshed every live region and no GET ever did, because a GET
that claimed a change made every region refresh every other region forever. It is
deliberately broad rather than clever: every failure this has ever had came from
someone deciding a particular press could not possibly affect a particular
counter. Only `meta` and `settings` are exempt, because they describe the build
rather than the database.

**One scheduler, not one timer per component.** `refetchInterval` per query with
`refetchIntervalInBackground: false`, which reproduces the old shim exactly —
polling pauses with the tab and fires a single catch-up tick on return, never a
queued burst. No `setInterval` anywhere, no module-level timers, and a poller dies
with its component. Two intervals are functions of their own data (import
progress, an integrity pass) because an idle screen must issue **zero** requests,
not one every four seconds forever.

**Filters are part of the key**, so a refetch can never reset what somebody typed
— and because they live in the address bar, a filtered screen is a link you can
send.

### The wire is hand-kept, and a Python test guards it

`src/api/types.ts` mirrors `app/schemas.py` by hand. There is no code generation,
on purpose: a generator's output is not the place to write down that
`AlbumOut.complete` being `null` means *nothing has counted this release yet*, and
that sentence is the entire reason the field exists.

The cost is that the two halves can drift, invisibly in both directions — a field
the server gained is a field nothing renders, and a field the server dropped is
`undefined` reaching a component the type checker promised would get a value,
surfacing as a blank cell or a `NaN` three layers from the change. So
`tests/test_wire_contract.py` walks the running server's own OpenAPI document and
asserts every response model's field names round-trip. Add a field on one side and
the Python suite tells you about the other. (It checks names, not types:
TypeScript legitimately narrows several fields the server declares loosely.)

Three conventions the client half must keep, all enforced in `client.ts`:
**ids are strings** (`uyej1o165e870`, `0884977859300` — never `parseInt` one),
**an absent filter is omitted, never `null`** (`?monitored=null` is a 422, and
`String(null)` is exactly how that happens), and **an HTML body from `/api/*` is
a missing endpoint, not a payload** — the server's catch-all serves the SPA shell
for everything else, so without that check a typo'd URL surfaces as a JSON parse
error with the URL nowhere in sight.

### Errors render where they were asked for

`ApiError.status` is the routing key — 503 a subsystem is not wired up, 409 busy,
400 a refusal or a bad value, 404 missing, 422 validation, 502 the upstream did
not answer — and `message` is the server's own sentence, rendered **verbatim**.
Never match on the prose; `BannerOut.code` and `MessageOut.level` exist so nothing
has to.

A failed *request* renders inside the panel that made it, never as a global error
boundary. That is what "degrades gracefully without credentials" means in
practice: a Qobuzarr with no Qobuz token still has a completely working library,
activity and health section, and the search panel is the one thing that says so —
which is why `require_client()`'s message names `QOBUZ_APP_ID` and
`QOBUZ_USER_AUTH_TOKEN`, and why that text is shown rather than paraphrased. The
one boundary in `App.tsx` is for a component that *threw*, and it is keyed on the
path so one crashed screen does not outlive the visit to it.

Toasts come from the mutation's response (`MessageOut.message` and its `level`),
not from the client guessing. Two are composed here because the server never had
the sentence: the delete-album one and the "queued an upgrade of X" one, which
must be read from `detail.upgrade` at the time of the press — an upgrade keeps the
album's status at `downloaded` for the whole download, so it cannot be re-derived
afterwards.

### The Health section is `/system`

`GET /health` is the server's JSON liveness probe, registered before the SPA
catch-all, so it wins that path. A hard reload on `/health` would hand the browser
JSON instead of the application — and a hard reload is exactly what somebody does
when a health screen looks wrong. The nav **label** stays "Health". `routes.tsx`
and `shell/nav.ts` are the authority on client addresses; `tests/test_spa_shell.py`
pins the server half.

---

## The design system, in one paragraph each

`src/design/README.md` is the reference. What follows is the part you need before
writing any component at all.

**Every value comes from `styles/tokens.css`.** A component may not contain a raw
colour, a raw font size or a raw gap; layout primitives take a *step number*
(`<Stack gap={6}>`), not a length. There are three documented exceptions and all
three are `2px`.

**Colour marks an exception.** A screen where nothing is wrong shows no status
colour at all — `downloaded` is neutral ink, not green, because a healthy state is
not news. `Tag` is the one tinted family; `Chip` has no `tone` prop and that is
deliberate rather than unfinished, because a chip carrying a status colour makes
an ordinary fact read as a problem. Ceiling of two chips per row.

**The brief is spacious, and it is executed by using the top of the scale.** 48px
gutters, 32px card padding, 56px between sections. Reaching for step 2 or 3 for
anything that is not spacing inside a single row means you are building the
previous design.

**An unknown is never a zero.** `Meter`'s `value` is `number | null` and `null`
draws no fill, prints an em dash and announces "not counted yet" — a
zero-width bar and "not counted" look identical, and a release adopted from disk
has no per-track record, so reporting it as 0% calls a complete album empty. The
same rule reaches `Count`, `Badge`, `KeyValue` and every formatter in `@/format`.

**`EmptyState` requires a variant**, because "nothing has been scanned yet",
"this filter matches none of it" and "the check ran and found nothing to do" are
three different claims and only one of them is good news. A component that could
fall back to a generic "Nothing here" would let a screen make the wrong one.

**One primary button per screen, and it lives in the title bar.** Row actions are
`plain`/`quiet`; a filled button repeated down a list is a texture, not an
emphasis. `danger-strong` is reserved for Empty trash, the single irreversible
action in the application.

**Confirm copy is load-bearing.** Roughly twenty controls carry a confirmation
whose wording is the safety argument — what moves, what is not kept, what cannot
be undone. Carry it over verbatim rather than tightening it.

**Light theme only**, declared with `<meta name="color-scheme" content="light">`.
Every contrast ratio annotated in `tokens.css` is measured against `--c-paper`; a
dark theme is a separate task with its own audit over the same token names, and
bolting it on without that audit would be a lie the browser acts on.

---

## Tests

Vitest and Testing Library in jsdom, colocated as `Thing.test.tsx` beside the
thing. No network: nothing here has ever been given a base URL that resolves.

Mount screens through `src/test/providers.tsx` — a screen is not a pure function
of props, it reads the router (its filters *are* the address), the query client
(its data) and the toast host (its acknowledgement of a press), and mounting one
without those does not degrade, it throws. The query client is created per call
so no cache survives from one test into the next, and `retry: false` keeps a test
from waiting out a backoff before rendering the error state it is asserting.

Use `src/test/factories.ts` for wire objects. Every factory returns a **complete**
object and then applies overrides, which matters more here than usual: the
three-valued fields have to be *present and null* rather than absent, because "the
server did not send this key" and "the server said nothing has measured it" are
different claims and only one of them is legal. A partial fixture lets a component
pass by reading `undefined` where production hands it `null`.

Assert on what a user can perceive — roles, accessible names, visible text. The
accessibility rules in the design system's README are all testable through
`getByRole`, and that is the point of them.

---

## Adding things

**A screen** → `src/screens/<section>/`, default-exported, lazily imported in
`routes.tsx`, plus an entry in `shell/nav.ts` if it belongs in the sidebar (nav is
data — the sidebar and its badges follow). Render the heading through `TitleBar`:
one `<h1>` per screen, and an absent subtitle or action slot renders no container
at all, so do not build your own title bar.

**A component** → `src/design/` if it would mean something in another application,
`src/widgets/` if it knows what a release or a queue item is. For the design
system, follow the six steps at the end of its README, including the header
comment that names the domain rule the component encodes — or states that it
encodes none.

**An endpoint** → a hook in `api/queries.ts` with its key in `queryKeys`, its
interval (if any) from `REFETCH`, and its type in `api/types.ts` mirroring the
Pydantic model. Never call `client.ts` from a component.

**A formatter** → `src/format/`. Pure, `EM_DASH` for unknown, never throws.
