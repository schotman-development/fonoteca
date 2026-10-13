import type { components } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  Input,
  PlayButton,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Text,
  VisuallyHidden,
} from '@fonoteca/ui'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { Fragment, useDeferredValue, useId, useRef } from 'react'

import { api, apiBaseUrl } from '../api.ts'
import { AddToPlaylist } from '../components/AddToPlaylist.tsx'
import { Pager } from '../components/Pager.tsx'
import { SortSelect } from '../components/SortSelect.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import { leaf } from './artistAlbums.ts'
import styles from './BrowsePage.module.css'
import { albumArt } from './coverArt.ts'
import { contentUrl } from './files.ts'
import { type PagedListSearch, TRACK_SORTS, type TrackSort } from './listSearch.ts'
import { qualityLabel, qualityTone } from './ReleasePage.tsx'

type TrackListRow = components['schemas']['TrackListRow']

const PAGE = 100

/**
 * Every recording the library holds, a hundred at a time.
 *
 * One row per recording, so a song kept as FLAC and MP3 is one row with a
 * badge saying so. A file nothing has identified is not a track yet; it is on
 * Identify.
 */
export function TracksPage() {
  const { query: filter = '', sort = 'title', page = 1 } = useSearch({ from: '/library/tracks' })
  const navigate = useNavigate({ from: '/library/tracks' })
  const heading = useRef<HTMLHeadingElement>(null)
  const searchId = useId()

  // A new filter or order starts from the first page; `replace` for the
  // reason the artist list gives.
  const update = (next: PagedListSearch<TrackSort>) => {
    void navigate({ search: (prev) => ({ ...prev, page: undefined, ...next }), replace: true })
  }

  const go = (to: number) => {
    void navigate({ search: (prev) => ({ ...prev, page: to > 1 ? to : undefined }) })
    heading.current?.scrollIntoView()
  }

  const query = useDeferredValue(filter.trim())

  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/tracks', {
        params: {
          query: {
            ...(query ? { query } : {}),
            ...(sort === 'title' ? {} : { sort }),
            skip: (page - 1) * PAGE,
            take: PAGE,
          },
        },
      }),
    [query, sort, page],
  )

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        <h1 ref={heading} className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Tracks
          </Text>
        </h1>
        <Text tone="secondary" block>
          Every recording you hold, once each however many copies of it there are.
        </Text>
      </Stack>

      <Stack gap={16} align="end" wrap>
        <div className={styles.search}>
          <label htmlFor={searchId}>
            <Text size="xs" tone="tertiary">
              Filter by title, artist or album
            </Text>
          </label>
          <Input
            id={searchId}
            type="search"
            value={filter}
            placeholder="Nature Boy, Laufey, Kind of Blue…"
            onChange={(event) => update({ query: event.target.value })}
          />
        </div>

        <SortSelect
          label="Sort by"
          value={sort}
          options={TRACK_SORTS}
          onChange={(value) => update({ sort: value === 'title' ? undefined : value })}
        />
      </Stack>

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? <Text tone="tertiary">Reading the catalogue…</Text> : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read the catalogue</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}

        {state.status === 'ready' && state.data.total === 0 ? (
          <Text tone="tertiary">
            {query.length > 0 ? 'No track matches that.' : 'No identified tracks yet.'}
          </Text>
        ) : null}
      </div>

      {state.status === 'ready' && state.data.total > 0 ? (
        <>
          <Pager page={page} size={PAGE} total={state.data.total} noun="tracks" onPage={go} />

          <div className={styles.table}>
            <Table density="cozy">
              <thead>
                <tr>
                  <TableHeaderCell className={styles.playCol}>
                    <VisuallyHidden>Play</VisuallyHidden>
                  </TableHeaderCell>
                  <TableHeaderCell>Track</TableHeaderCell>
                  <TableHeaderCell>Artist</TableHeaderCell>
                  <TableHeaderCell>Album</TableHeaderCell>
                  <TableHeaderCell numeric className={styles.lengthCol}>
                    Length
                  </TableHeaderCell>
                  <TableHeaderCell className={styles.formatCol}>Format</TableHeaderCell>
                  <TableHeaderCell className={styles.addCol}>
                    <VisuallyHidden>Add to a playlist</VisuallyHidden>
                  </TableHeaderCell>
                </tr>
              </thead>
              <tbody>
                {state.data.items.map((track) => (
                  <tr key={track.recordingId}>
                    <TrackCells track={track} />
                    <TableCell>
                      <AddToPlaylist
                        recordingIds={[track.recordingId]}
                        name={track.title}
                        compact
                      />
                    </TableCell>
                  </tr>
                ))}
              </tbody>
            </Table>
          </div>

          <Pager page={page} size={PAGE} total={state.data.total} noun="tracks" onPage={go} />
        </>
      ) : null}
    </Stack>
  )
}

/**
 * One track's cells, play button to format: the Tracks page's row, and the
 * playlist page's with its own cells either side.
 */
export function TrackCells({ track }: { readonly track: TrackListRow }) {
  const { album, file } = track
  const byline = track.artists.map((credit) => credit.name + (credit.joinPhrase ?? '')).join('')
  const cover = album != null ? albumArt(album) : undefined

  return (
    <>
      <TableCell>
        <PlayButton
          track={{
            id: track.recordingId,
            src: contentUrl(apiBaseUrl, file.path),
            title: track.title,
            ...(byline !== '' ? { subtitle: byline } : {}),
          }}
        />
      </TableCell>

      <TableCell truncate title={track.title}>
        <Stack direction="column" gap={2}>
          <Text truncate>{track.title}</Text>
          {track.workTitle != null && track.workTitle !== track.title ? (
            <Text size="xs" tone="tertiary" truncate>
              {track.workTitle}
            </Text>
          ) : null}
        </Stack>
      </TableCell>

      {/* Each billed name links to its own page, joined as the sleeve prints them. */}
      <TableCell truncate title={byline}>
        {track.artists.length === 0 ? (
          <Text size="sm" tone="tertiary">
            —
          </Text>
        ) : (
          track.artists.map((credit) => (
            <Fragment key={credit.artistId}>
              <Link
                to="/library/artists/$artistId"
                params={{ artistId: credit.artistId }}
                className={styles.link}
              >
                <Text size="sm" tone="accent">
                  {credit.name}
                </Text>
              </Link>
              {credit.joinPhrase != null ? <Text size="sm">{credit.joinPhrase}</Text> : null}
            </Fragment>
          ))
        )}
      </TableCell>

      {album != null ? (
        <TableCell
          truncate
          title={album.year != null ? `${album.title} (${album.year})` : album.title}
        >
          <Link
            to="/library/albums/$albumId"
            params={{ albumId: album.albumId }}
            className={`${styles.album} ${styles.link}`}
          >
            <Artwork name={album.title} size="sm" {...(cover != null ? { src: cover } : {})} />
            <Text size="sm" tone="accent" truncate>
              {album.title}
            </Text>
          </Link>
        </TableCell>
      ) : (
        <TableCell truncate title={`No album attributed. Folder: ${track.folder}`}>
          <Text size="sm" tone="tertiary" truncate>
            {leaf(track.folder)} (folder)
          </Text>
        </TableCell>
      )}

      <TableCell numeric>
        <Text size="sm" family="mono" tone={track.duration == null ? 'tertiary' : 'primary'}>
          {track.duration ?? '—'}
        </Text>
      </TableCell>

      <TableCell>
        <Stack gap={4} wrap>
          {file.quality != null ? (
            <Badge tone={qualityTone(file.quality, file.analysis)} size="sm" mono>
              {qualityLabel(file.quality, file.analysis)}
            </Badge>
          ) : null}
          {/* The same recording kept more than once: the dedupe question. */}
          {track.files > 1 ? (
            <Badge tone="warning" size="sm" mono>
              {`${track.files} files`}
            </Badge>
          ) : null}
        </Stack>
      </TableCell>
    </>
  )
}
