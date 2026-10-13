import { Badge, CatalogueGrid, Stack, Text } from '@fonoteca/ui'
import { Link } from '@tanstack/react-router'
import { type ReactNode, useId } from 'react'

import { api } from '../api.ts'
import { type QueryState, useApiQuery } from '../useApiQuery.ts'
import { ArtistCard } from './ArtistsPage.tsx'
import styles from './HomePage.module.css'
import { AlbumCard } from './ReleasesPage.tsx'

/** `QualityTier` as the overview names it, best first. */
const QUALITY: Readonly<Record<string, string>> = {
  LosslessHiRes: 'Hi-res lossless',
  LosslessCd: 'CD quality',
  LossyHigh: 'Lossy, 256 kbps and up',
  LossyStandard: 'Lossy, 128–255 kbps',
  LossyLow: 'Lossy, under 128 kbps',
  Unknown: 'Not measured',
}

const SHELF = 20

/**
 * The library at a glance: how much there is, what came in last, who it is
 * mostly by, and what it is made of. The passes and the API's own state are
 * under Settings.
 */
export function HomePage() {
  const info = useApiQuery(() => api.get('/api/system/info'), [])
  const overview = useApiQuery(() => api.get('/api/catalogue/overview'), [])
  const recent = useApiQuery(
    () => api.get('/api/catalogue/albums', { params: { query: { sort: 'added', take: SHELF } } }),
    [],
  )
  const artists = useApiQuery(
    () => api.get('/api/catalogue/artists', { params: { query: { sort: 'albums', take: SHELF } } }),
    [],
  )

  return (
    <Stack direction="column" gap={32}>
      <h1 className={styles.title}>
        <Text size="xl" weight="semibold" block>
          Home
        </Text>
      </h1>

      <Pending states={[info, overview]}>
        {info.status === 'ready' && overview.status === 'ready' ? (
          <dl className={styles.tiles}>
            <Tile label="Albums" value={info.data.counts.albums.toLocaleString()} />
            <Tile label="Artists" value={info.data.counts.artists.toLocaleString()} />
            <Tile label="Songs" value={overview.data.songs.toLocaleString()} />
            <Tile label="Composers" value={overview.data.composers.toLocaleString()} />
            <Tile label="Compositions" value={overview.data.compositions.toLocaleString()} />
          </dl>
        ) : null}
      </Pending>

      <Shelf
        title="Recently added"
        more={
          <Link to="/library/albums" search={{ sort: 'added' }}>
            All albums
          </Link>
        }
        state={recent}
      >
        {(data) =>
          data.items.length === 0 ? (
            <Text tone="tertiary">No albums yet.</Text>
          ) : (
            <CatalogueGrid size="album" layout="shelf" aria-label="Recently added">
              {data.items.map((album) => (
                <AlbumCard key={album.id} album={album} />
              ))}
            </CatalogueGrid>
          )
        }
      </Shelf>

      <Shelf
        title="Most in your library"
        more={
          <Link to="/library" search={{ sort: 'albums' }}>
            All artists
          </Link>
        }
        state={artists}
      >
        {(data) =>
          data.items.length === 0 ? (
            <Text tone="tertiary">No artists yet.</Text>
          ) : (
            <CatalogueGrid size="artist" layout="shelf" aria-label="Most in your library">
              {data.items.map((artist) => (
                <ArtistCard key={artist.id} artist={artist} counting="albums" />
              ))}
            </CatalogueGrid>
          )
        }
      </Shelf>

      {overview.status === 'ready' ? (
        <div className={styles.breakdowns}>
          <Breakdown
            title="Quality"
            caption="Files, by what the audio measures"
            rows={overview.data.quality.map((row) => ({
              label: QUALITY[row.name] ?? row.name,
              count: row.count,
            }))}
          />
          <Breakdown
            title="Formats"
            caption="Measured files, by codec"
            rows={overview.data.formats.map((row) => ({
              label: row.name.toUpperCase(),
              count: row.count,
            }))}
          />
          <Breakdown
            title="Decades"
            caption="Albums with a release, by when they first came out"
            rows={[
              ...overview.data.decades.map((row) => ({
                label: `${row.decade}s`,
                count: row.count,
              })),
              ...(overview.data.undatedAlbums > 0
                ? [{ label: 'Undated', count: overview.data.undatedAlbums }]
                : []),
            ]}
          />
          <Breakdown
            title="Genres"
            caption="Albums carrying each, commonest first"
            rows={overview.data.genres.map((row) => ({ label: row.name, count: row.count }))}
          />
        </div>
      ) : null}
    </Stack>
  )
}

/** Loading and failure for the reads named, and nothing once they are in. */
function Pending({
  states,
  children,
}: {
  readonly states: readonly QueryState<unknown>[]
  readonly children: ReactNode
}) {
  const failed = states.find((state) => state.status === 'error')
  const loading = states.some((state) => state.status === 'loading')

  return (
    <div role="status" aria-live="polite" aria-busy={loading}>
      {failed?.status === 'error' ? (
        <Stack direction="column" gap={4} align="start">
          <Badge tone="danger">Could not read the catalogue</Badge>
          <Text size="sm" tone="tertiary">
            {failed.message}
          </Text>
        </Stack>
      ) : loading ? (
        <Text tone="tertiary">Reading the catalogue…</Text>
      ) : (
        children
      )}
    </div>
  )
}

function Tile({ label, value }: { readonly label: string; readonly value: string }) {
  return (
    <div className={styles.tile}>
      <dt>
        <Text size="xs" tone="tertiary">
          {label}
        </Text>
      </dt>
      <dd className={styles.value}>
        <Text size="xl" weight="semibold" family="mono">
          {value}
        </Text>
      </dd>
    </div>
  )
}

function Shelf<T>({
  title,
  more,
  state,
  children,
}: {
  readonly title: string
  readonly more: ReactNode
  readonly state: QueryState<T>
  readonly children: (data: T) => ReactNode
}) {
  const id = useId()

  return (
    <section aria-labelledby={id}>
      <Stack direction="column" gap={12}>
        <Stack gap={12} align="baseline" justify="between">
          <h2 id={id} className={styles.title}>
            <Text size="lg" weight="semibold" block>
              {title}
            </Text>
          </h2>
          <Text size="sm">{more}</Text>
        </Stack>

        <Pending states={[state]}>{state.status === 'ready' ? children(state.data) : null}</Pending>
      </Stack>
    </section>
  )
}

/** One count per row, each with a bar against the largest. */
function Breakdown({
  title,
  caption,
  rows,
}: {
  readonly title: string
  readonly caption: string
  readonly rows: readonly { readonly label: string; readonly count: number }[]
}) {
  const id = useId()
  const largest = Math.max(1, ...rows.map((row) => row.count))

  return (
    <section aria-labelledby={id} className={styles.panel}>
      <Stack direction="column" gap={2}>
        <h2 id={id} className={styles.title}>
          <Text weight="semibold" block>
            {title}
          </Text>
        </h2>
        <Text size="xs" tone="tertiary" block>
          {caption}
        </Text>
      </Stack>

      {rows.length === 0 ? (
        <Text size="sm" tone="tertiary">
          Nothing yet.
        </Text>
      ) : (
        <ul className={styles.bars}>
          {rows.map((row) => (
            <li key={row.label} className={styles.bar}>
              <Text size="sm" truncate>
                {row.label}
              </Text>
              <span className={styles.track} aria-hidden="true">
                <span
                  className={styles.fill}
                  style={{ inlineSize: `${(row.count / largest) * 100}%` }}
                />
              </span>
              <Text size="sm" tone="secondary" family="mono">
                {row.count.toLocaleString()}
              </Text>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
