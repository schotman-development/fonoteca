import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router'

import { AppShell } from './AppShell.tsx'
import { ArtistPage } from './pages/ArtistPage.tsx'
import { ArtistsPage } from './pages/ArtistsPage.tsx'
import { DashboardPage } from './pages/DashboardPage.tsx'
import { MatchingPage } from './pages/MatchingPage.tsx'
import { ReleasePage } from './pages/ReleasePage.tsx'
import { ReleasesPage } from './pages/ReleasesPage.tsx'

/**
 * The route tree, defined in code.
 *
 * No file-based routing and therefore no Vite plugin: five routes do not need
 * a code generator, and the generated route tree would be a build artefact to
 * keep in step with the source — the repository already has one of those in
 * `schema.d.ts`, and that one buys a contract with another toolchain. This one
 * would buy nothing.
 *
 * See ADR 0008 for why this router at all.
 */
const rootRoute = createRootRoute({ component: AppShell })

const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: DashboardPage,
})

const artistsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/library',
  component: ArtistsPage,
})

const artistRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/library/artists/$artistId',
  component: ArtistPage,
})

const releasesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/library/releases',
  component: ReleasesPage,
})

const releaseRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/library/releases/$releaseId',
  component: ReleasePage,
})

/**
 * The worklist of refusals.
 *
 * A route rather than a mode of the album pages because the subject of a
 * matching question is not a release — it is a set of files that no release
 * explained, and there is no album page to hang it off precisely because the
 * pass refused to file one.
 *
 * **One route, not two.** Opening a question used to be a navigation to
 * `/library/matching/$questionId`; it is a dialog now. The worklist is the thing
 * being worked, so dismissing a question should return a person to the row they
 * came from with their scroll position and their open group intact — three
 * things a route would have to restore, from ids that are documented as
 * unstable. Re-running a pass re-stamps a component and mints a new id, so a
 * link kept or shared would silently point at a different decision.
 */
const matchingRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/library/matching',
  component: MatchingPage,
})

const routeTree = rootRoute.addChildren([
  dashboardRoute,
  artistsRoute,
  artistRoute,
  releasesRoute,
  releaseRoute,
  matchingRoute,
])

export const router = createRouter({
  routeTree,
  // The library is on the other side of a request, so a fresh navigation should
  // show what is there now rather than what was there when the tab was opened.
  defaultPreload: false,
})

export { artistRoute, artistsRoute, dashboardRoute, matchingRoute, releaseRoute, releasesRoute }

/**
 * Teaches `Link`, `useParams` and the rest about this tree.
 *
 * Without it every route path is `string` and every param is `unknown`, which
 * is most of the reason this router was chosen over the alternatives — see
 * ADR 0008.
 */
declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
