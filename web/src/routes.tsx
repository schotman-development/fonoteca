/**
 * The route table — the screens of build spec §2, plus a catch-all.
 *
 * Two addresses were added after the six-screen rebuild and are not the design
 * mock's: `/missing` (the whole backlog, of which the dashboard draws six rows)
 * and `/queue` (the download worker, which had no page at all — it was a
 * sentence in the footer, one row on the dashboard and a shelf of finished
 * downloads). Both were asked for; neither is one of the screens deleted on
 * purpose during the rebuild, and nothing here restores those.
 *
 * Three decisions, each one a rule from the spec rather than a preference.
 *
 * **Every screen is code-split.** A `lazy()` per route means the dashboard's
 * bundle does not carry the identify picker, the settings builder and the
 * activity log. The tree is shallow and the modules are default exports for
 * exactly this reason — `lazy` takes a promise of `{ default }`, and a named
 * export would need a wrapper per screen.
 *
 * **One Suspense boundary, at the table.** Not one per route: a boundary per
 * route means the first paint of every navigation is a skeleton *inside* the
 * frame's outlet slot, which is what we want, and putting the boundary here
 * gets that with one element instead of eight. `PageLoading` is a flex child of
 * the outlet row, so the skeleton occupies the space the screen will.
 *
 * **`:artistId` is a string.** Album and artist ids look like `uyej1o165e870`
 * and `0884977859300`; `useParams` hands back a string and nothing here may
 * coerce one. The screen reads it as it arrives.
 *
 * The address `/library` deliberately has no `index`/child arrangement: the two
 * library views are separate screens with separate queries, and nesting them
 * under a shared layout route would keep the grid's data alive underneath the
 * artist view for the whole visit.
 */

import { lazy, Suspense } from 'react'
import { Route, Routes } from 'react-router-dom'

import { PageLoading } from '@/design'

const Dashboard = lazy(() => import('@/screens/dashboard/Dashboard'))
const ArtistsIndex = lazy(() => import('@/screens/library/ArtistsIndex'))
const ArtistDetail = lazy(() => import('@/screens/library/ArtistDetail'))
const Radar = lazy(() => import('@/screens/radar/Radar'))
const Missing = lazy(() => import('@/screens/missing/Missing'))
const Queue = lazy(() => import('@/screens/queue/Queue'))
const Identify = lazy(() => import('@/screens/identify/Identify'))
const Rules = lazy(() => import('@/screens/rules/Rules'))
const Activity = lazy(() => import('@/screens/activity/Activity'))
const NotFound = lazy(() => import('@/screens/NotFound'))

export function AppRoutes() {
  return (
    <Suspense fallback={<PageLoading rows={6} label="Loading the screen" />}>
      <Routes>
        <Route path="/" element={<Dashboard />} />
        <Route path="/library" element={<ArtistsIndex />} />
        <Route path="/library/:artistId" element={<ArtistDetail />} />
        <Route path="/radar" element={<Radar />} />
        <Route path="/missing" element={<Missing />} />
        <Route path="/queue" element={<Queue />} />
        <Route path="/identify" element={<Identify />} />
        <Route path="/rules" element={<Rules />} />
        <Route path="/activity" element={<Activity />} />
        <Route path="*" element={<NotFound />} />
      </Routes>
    </Suspense>
  )
}
