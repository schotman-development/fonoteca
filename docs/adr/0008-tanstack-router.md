# 0008 — TanStack Router, defined in code

**Status:** accepted, 2026-08-09

## Context

`apps/web` had no router. That was deliberate: `AppShell.tsx` carried a comment
reserving the outlet and saying so, because adding one to render a second panel
would have settled the question by accident rather than on purpose.

The library browser is the screen that forces it. Three routes — the dashboard,
the artist list, one artist — and two of them are URLs somebody will want to
bookmark, send, and reload.

Three candidates:

- **React Router** — the conventional answer. One dependency, no build step,
  declarative mode reads like the JSX around it. Route params are typed
  `string | undefined`; correctness is the caller's problem.
- **TanStack Router** — typed route paths, params and search params, checked by
  the compiler. Heavier, and its file-based mode wants a Vite plugin.
- **Hand-rolled** — the History API and a matcher, about sixty lines, zero
  dependencies. Closest to ADR 0003's spirit.

## Decision

**TanStack Router, with routes defined in code rather than by file convention.**

The deciding argument is the one this repository keeps making elsewhere: a
mistake should be a type error rather than a runtime surprise. The OpenAPI seam
exists so a backend change breaks the build instead of the browser; the token
package types light and dark identically so a missing colour is a compile error;
entity ids are eight distinct struct types so passing the wrong one cannot
compile. A router where `params.artistId` is `string | undefined` and a typo in
a `<Link to="...">` is a runtime 404 is the odd one out.

Concretely, `router.d.ts`-style registration makes `<Link to="/library/artists/$artistId" params={{ artistId }} />`
check both halves, and `useParams({ from: '/library/artists/$artistId' })` return
a known shape. Renaming a route breaks every link to it at build time.

**Code-based routes, so no Vite plugin.** File-based routing generates a route
tree that has to be kept in step with the source. This repository already
maintains one generated-and-committed artefact — `schema.d.ts` — and that one
buys a contract with a separate toolchain. A generated route tree for three
routes buys nothing and adds a plugin to the build.

The hand-rolled option was rejected on scope rather than on principle. ADR 0003
excludes third-party *runtime behaviour* from the design system — focus traps,
positioning, virtualization — because that behaviour is accessibility work we
want to own. Routing is not that; it is plumbing, and the parts a hand-rolled
version omits (nested layouts, scroll restoration, code-split boundaries) are
exactly the parts the catalogue and playback screens will need.

## Consequences

- `apps/web` gains one runtime dependency. `packages/ui` gains none, and the
  constraint in ADR 0003 is untouched — the design system still knows nothing
  about routing, so its components stay usable in Storybook.
- `AppShell` is now the root route's component: header, `<Outlet />`, and the
  reserved transport row *outside* the outlet. That arrangement is why the row
  was declared before there was a player to put in it.
- The dashboard moved into `pages/DashboardPage.tsx` unchanged.
- **The server-state decision stays open.** A router is not a query cache, and
  `useApiQuery` is deliberately the shared version of the throwaway
  `useState`/`useEffect` the panels already used — no deduplication, no
  background refetch, no invalidation. Adopting TanStack Query because the
  router shares its name would be the accident this ADR exists to avoid.
- Route paths are now a compile-time contract, which means a route rename is a
  build failure. That is the point, and it is also the cost.
