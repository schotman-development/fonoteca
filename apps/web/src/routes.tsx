import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router'

import { AppShell } from './AppShell.tsx'
import { AcquirePage } from './pages/AcquirePage.tsx'
import { ArtistPage } from './pages/ArtistPage.tsx'
import { ArtistsPage } from './pages/ArtistsPage.tsx'
import { DashboardPage } from './pages/DashboardPage.tsx'
import { FilesPage } from './pages/FilesPage.tsx'
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

  /**
   * Where MusicBrainz sends somebody back to.
   *
   * The one screen here with search parameters, and they are not a UI state
   * that survived a reload — they are a return address. Adding a concert to
   * MusicBrainz happens on musicbrainz.org, so the folder being answered has to
   * make the round trip somehow, and `redirect_uri` on the seeded form is the
   * mechanism their editor offers: it appends `release_mbid` to whatever URL it
   * is given once the edit is saved. `folder` is ours, carried out and back so
   * the returning tab knows which question was being answered.
   *
   * `release_mbid` is spelled in MusicBrainz's snake case rather than this
   * codebase's camel, because they choose that name and nothing here can
   * rename it.
   */
  validateSearch: (
    search: Record<string, unknown>,
  ): { readonly folder?: string | undefined; readonly release_mbid?: string | undefined } => ({
    folder: typeof search.folder === 'string' ? search.folder : undefined,
    release_mbid: typeof search.release_mbid === 'string' ? search.release_mbid : undefined,
  }),
})

/**
 * The library as a directory tree.
 *
 * Outside `/library` for the reason `/acquire` is: everything under that path
 * browses the catalogue, and half of what this screen lists is not in it —
 * artwork, playlists, and the album that was uploaded four seconds ago. It is
 * about files, and the catalogue is the annotation rather than the subject.
 *
 * The folder is a search parameter rather than a route parameter. A library
 * path can be 4096 characters and contains slashes by definition, so it is not
 * a segment — but it is not component state either: held there, walking six
 * folders down left the URL saying `/files` the whole way, so Back left the
 * screen entirely and undid the whole descent in one press.
 */
const filesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/files',
  component: FilesPage,
  validateSearch: (search: Record<string, unknown>): { readonly path?: string | undefined } => ({
    path: typeof search.path === 'string' && search.path !== '' ? search.path : undefined,
  }),
})

/**
 * Manual acquisition.
 *
 * Outside `/library` deliberately: everything under that path browses the
 * catalogue, and nothing on this screen is in it. A download writes audio into
 * the library directory, but the catalogue does not learn about it until the
 * next scan — so this screen is about acquiring, not about what is held.
 */
const acquireRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/acquire',
  component: AcquirePage,
})

const routeTree = rootRoute.addChildren([
  dashboardRoute,
  artistsRoute,
  artistRoute,
  releasesRoute,
  releaseRoute,
  matchingRoute,
  filesRoute,
  acquireRoute,
])

export const router = createRouter({
  routeTree,
  // The library is on the other side of a request, so a fresh navigation should
  // show what is there now rather than what was there when the tab was opened.
  defaultPreload: false,
})

export {
  acquireRoute,
  artistRoute,
  artistsRoute,
  dashboardRoute,
  filesRoute,
  matchingRoute,
  releaseRoute,
  releasesRoute,
}

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
