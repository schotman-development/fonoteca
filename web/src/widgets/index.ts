/**
 * The domain layer's public surface.
 *
 * A widget is the half of the interface that knows what a release, an artist,
 * a queue item or an activity line **is**. A design primitive does not, and
 * that is the whole dividing line: `Meter` would mean something in another
 * application, `CompletionCell` would not.
 *
 * Screens import from `@/widgets`, never from a file inside it — the same rule
 * `@/design` keeps, for the same reason. A widget may import from `@/design`
 * and from another widget (`FollowRow` renders `RelativeTime`); nothing in here
 * may import from `@/screens` or `@/shell`.
 *
 * The pure helpers are exported beside the components because they are the
 * *rules* the components apply, and a screen legitimately needs them without a
 * component: the Activity screen groups its loaded page with `eventGroup`, and
 * a test asserts `albumFlag`'s three-way verdict without mounting a tile.
 *
 *   rows      ActivityRow · FollowRow · JobRow · MissingRow · QueueRow ·
 *             ReleaseRow · TodoRow · TrackRow
 *   tiles     AlbumTile · ArtistTile · ShelfCard
 *   bulk      BulkBar · buildBulkPayload
 *   marks     QualityBadge · RelativeTime
 *   rules     activityKind · albumFlag · eventGroup · queueState · releaseState
 */

/* ---- rows ---------------------------------------------------------------- */

export { ActivityRow } from '@/widgets/ActivityRow'
export type { ActivityRowProps } from '@/widgets/ActivityRow'

export { FollowRow } from '@/widgets/FollowRow'
export type { FollowRowProps } from '@/widgets/FollowRow'

export { JobRow } from '@/widgets/JobRow'
export type { JobRowProps } from '@/widgets/JobRow'

export { MissingRow } from '@/widgets/MissingRow'
export type { MissingRowProps } from '@/widgets/MissingRow'

export { QueueRow } from '@/widgets/QueueRow'
export type { QueueRowProps } from '@/widgets/QueueRow'

export { ReleaseRow } from '@/widgets/ReleaseRow'
export type { ReleaseRowProps } from '@/widgets/ReleaseRow'

export { TodoRow } from '@/widgets/TodoRow'
export type { TodoRowProps } from '@/widgets/TodoRow'

export { TrackRow } from '@/widgets/TrackRow'
export type { TrackRowProps } from '@/widgets/TrackRow'

/* ---- tiles --------------------------------------------------------------- */

export { AlbumTile } from '@/widgets/AlbumTile'
export type { AlbumTileProps } from '@/widgets/AlbumTile'

export { ArtistTile } from '@/widgets/ArtistTile'
export type { ArtistTileProps } from '@/widgets/ArtistTile'

export { ShelfCard } from '@/widgets/ShelfCard'
export type { ShelfCardProps } from '@/widgets/ShelfCard'

/* ---- bulk selection ------------------------------------------------------ */

export { BulkBar } from '@/widgets/BulkBar'
export type { BulkBarProps } from '@/widgets/BulkBar'

export {
  buildBulkPayload,
  EMPTY_BULK_DRAFT,
  KEEP,
  MONITOR_MODES,
} from '@/widgets/BulkBar'
export type { BulkDraft, BulkMonitored, BulkTypesAction } from '@/widgets/BulkBar'

/* ---- marks --------------------------------------------------------------- */

export { QualityBadge } from '@/widgets/QualityBadge'
export type { QualityBadgeProps } from '@/widgets/QualityBadge'

export { RelativeTime } from '@/widgets/RelativeTime'
export type { RelativeTimeProps } from '@/widgets/RelativeTime'

/* ---- the rules the rows apply -------------------------------------------- */

export { activityKind } from '@/widgets/activityKind'
export type { ActivityKind, ActivityTone } from '@/widgets/activityKind'

export { albumFlag } from '@/widgets/albumFlag'
export type { AlbumFlag } from '@/widgets/albumFlag'

export { eventGroup } from '@/widgets/eventGroup'
export type { EventGroup } from '@/widgets/eventGroup'

export {
  queueActions,
  queueProgress,
  queueProgressText,
  queueStateWord,
} from '@/widgets/queueState'
export type { QueueActions, QueueStateWord, QueueTone } from '@/widgets/queueState'

export { releaseNote, releaseState, releaseTiming } from '@/widgets/releaseState'
export type { ReleaseState, ReleaseTiming, ReleaseTone } from '@/widgets/releaseState'

export { sourceDot } from '@/widgets/sourceDot'

export { toastTone } from '@/widgets/toastTone'
