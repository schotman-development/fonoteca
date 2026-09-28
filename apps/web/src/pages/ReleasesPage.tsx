import type { components } from '@fonoteca/api-client'
import { Badge, CatalogueCard, CatalogueGrid, Input, Stack, Text } from '@fonoteca/ui'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { useDeferredValue, useId } from 'react'

import { api } from '../api.ts'
import { SortSelect } from '../components/SortSelect.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import { CERTAINTY } from './certainty.ts'
import { releaseCover, releaseGroupArt } from './coverArt.ts'
import { RELEASE_DEFAULT_SORT, RELEASE_SORTS, type ReleaseListSearch } from './listSearch.ts'
import styles from './ReleasesPage.module.css'

type AlbumSummary = components['schemas']['AlbumSummary']
type NoReleaseAlbum = components['schemas']['NoReleaseAlbum']

/**
 * The library, by album.
 *
 * Every release here was decided by the audio — which recordings a release
 * lists, and how closely each one's printed length matches what the files
 * actually measure. The directories on disk played no part in it, which is what
 * makes the validation card below a second opinion rather than an echo.
 */
export function ReleasesPage() {
  // In the address bar rather than in the component, so opening an album and
  // coming back keeps the order. See `routes.tsx` and the artist list.
  const { query: filter = '', sort = RELEASE_DEFAULT_SORT } = useSearch({
    from: '/library/albums',
  })

  const navigate = useNavigate({ from: '/library/albums' })

  const update = (next: ReleaseListSearch) => {
    void navigate({ search: (prev) => ({ ...prev, ...next }), replace: true })
  }

  const searchId = useId()
  const noReleaseId = useId()

  const query = useDeferredValue(filter.trim())

  // The default is left out of the request rather than sent, so the URL carries
  // only what somebody actually chose. See the artist list for the whole note.
  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/albums', {
        params: { query: { ...(query ? { query } : {}), ...(sort === 'title' ? {} : { sort }) } },
      }),
    [query, sort],
  )

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Albums
          </Text>
        </h1>
        <Text tone="secondary" block>
          Worked out from the audio: which recordings each release holds, and how closely their
          printed lengths match what the files measure. Not from the folder names.
        </Text>
      </Stack>

      <Validation />

      <Stack gap={16} align="end" wrap>
        <div className={styles.search}>
          <label htmlFor={searchId}>
            <Text size="xs" tone="tertiary">
              Filter by title
            </Text>
          </label>
          <Input
            id={searchId}
            type="search"
            value={filter}
            placeholder="Sloe Gin, Off the Wall…"
            onChange={(event) => update({ query: event.target.value })}
          />
        </div>

        <SortSelect
          label="Sort by"
          value={sort}
          options={RELEASE_SORTS}
          onChange={(value) => update({ sort: value === RELEASE_DEFAULT_SORT ? undefined : value })}
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

        {state.status === 'ready' ? (
          <Text
            size="sm"
            tone={state.data.items.length === state.data.total ? 'tertiary' : 'warning'}
          >
            {state.data.items.length === state.data.total
              ? albumCount(state.data.total + state.data.noRelease.length)
              : `Showing the first ${state.data.items.length.toLocaleString()} of ${state.data.total.toLocaleString()} — narrow the filter to reach the rest.`}
          </Text>
        ) : null}
      </div>

      {state.status === 'ready' ? (
        state.data.items.length === 0 && state.data.noRelease.length === 0 ? (
          <Empty filtered={query.length > 0} />
        ) : (
          <>
            {state.data.items.length > 0 ? (
              <CatalogueGrid aria-label="Albums">
                {state.data.items.map((album) => (
                  <AlbumCard key={album.id} album={album} />
                ))}
              </CatalogueGrid>
            ) : null}

            {state.data.noRelease.length > 0 ? (
              <section aria-labelledby={noReleaseId}>
                <Stack direction="column" gap={8}>
                  <h2 id={noReleaseId} className={styles.title}>
                    <Text size="lg" weight="semibold" block>
                      No release
                    </Text>
                  </h2>
                  <Text size="sm" tone="tertiary" block>
                    Folders answered as nobody's release. No album stands for them, so they open in
                    Files.
                  </Text>
                  <CatalogueGrid aria-label="No release">
                    {state.data.noRelease.map((album) => (
                      <NoReleaseCard key={album.folder} album={album} />
                    ))}
                  </CatalogueGrid>
                </Stack>
              </section>
            ) : null}
          </>
        )
      ) : null}
    </Stack>
  )
}

function albumCount(count: number) {
  return `${count.toLocaleString()} album${count === 1 ? '' : 's'}`
}

function NoReleaseCard({ album }: { readonly album: NoReleaseAlbum }) {
  return (
    <CatalogueCard
      variant="album"
      title={album.title}
      subtitle={album.artist ?? 'No artist folder'}
      meta={
        <Badge tone="neutral" size="sm" mono>
          {album.files} {album.files === 1 ? 'file' : 'files'}
        </Badge>
      }
      render={(props) => <Link {...props} to="/files" search={{ path: album.folder }} />}
    />
  )
}

/**
 * Where the folders finally get a say.
 *
 * Deliberately above the list rather than tucked away: the attribution declines
 * to guess, so the interesting part of the result is what it refused and where
 * it disagrees with the directories — and neither is visible from an album list
 * that only shows what worked.
 */
function Validation() {
  const state = useApiQuery(() => api.get('/api/catalogue/attribution'), [])

  if (state.status !== 'ready') return null

  const { folders, foldersAgreeing, foldersSplit, albumsSpanningFolders, incomplete } = state.data
  const disagreements = foldersSplit.length + albumsSpanningFolders.length

  return (
    <div className={styles.validation}>
      <Stack direction="column" gap={8}>
        <Stack gap={20} wrap align="center">
          <Stat label="Folders agreeing" value={`${foldersAgreeing} / ${folders}`} />
          <Stat label="Disagreements" value={disagreements.toLocaleString()} />
          <Stat label="Incomplete" value={incomplete.length.toLocaleString()} />
        </Stack>

        {disagreements === 0 ? (
          <Text size="xs" tone="tertiary">
            Every folder's files landed on one album, and no album drew from more than one folder.
          </Text>
        ) : (
          <Stack direction="column" gap={4}>
            {foldersSplit.length > 0 ? (
              <Text size="xs" tone="tertiary">
                {foldersSplit.length.toLocaleString()} folder
                {foldersSplit.length === 1 ? ' was' : 's were'} split across more than one album —
                often right, since a deluxe edition really is two releases in one directory.
              </Text>
            ) : null}
            {albumsSpanningFolders.length > 0 ? (
              <Text size="xs" tone="tertiary">
                {albumsSpanningFolders.length.toLocaleString()} album
                {albumsSpanningFolders.length === 1 ? '' : 's'} drew from more than one folder — the
                same album ripped twice, or a folder holding two.
              </Text>
            ) : null}
            <Text size="xs" tone="tertiary">
              Either side may be the wrong one. The folders were never consulted when deciding.
            </Text>
          </Stack>
        )}
      </Stack>
    </div>
  )
}

function Stat({ label, value }: { readonly label: string; readonly value: string }) {
  return (
    <Stack direction="column" gap={2}>
      <Text size="lg" weight="semibold" family="mono">
        {value}
      </Text>
      <Text size="xs" tone="tertiary">
        {label}
      </Text>
    </Stack>
  )
}

function Empty({ filtered }: { readonly filtered: boolean }) {
  return filtered ? (
    <Text tone="tertiary">No album matches that.</Text>
  ) : (
    <Stack direction="column" gap={4} align="start">
      <Text tone="tertiary" block>
        Nothing here yet.
      </Text>
      <Text size="sm" tone="tertiary" block>
        Albums appear once the attribution pass has worked out which release each file came from.
        Run it from <Link to="/">Foundation</Link>.
      </Text>
    </Stack>
  )
}

function AlbumCard({ album }: { readonly album: AlbumSummary }) {
  const certainty = CERTAINTY[album.certainty]

  return (
    <CatalogueCard
      variant="album"
      title={album.title}
      /*
        The stored sleeve of the edition that stands for the album; where none
        is stored, the archive's picture for the album as a whole.
      */
      {...(album.coverReleaseId != null
        ? { image: releaseCover(album.coverReleaseId) }
        : album.mbid != null
          ? { image: releaseGroupArt(album.mbid) }
          : {})}
      /*
        The artist alone on this line. The row this replaced ran artist, year
        and format together, and at tile width that sentence truncates in the
        middle of the year — so the two facts that are short enough to always
        fit have moved down to the meta row, where they do.
      */
      subtitle={album.artist ?? 'No credited artist'}
      meta={
        <>
          <Text size="xs" tone="tertiary" family="mono">
            {[album.year?.toString(), album.formats].filter(Boolean).join(' · ')}
          </Text>

          {/*
            Held against printed, so a part-ripped album reads as one at a glance.
            Tracks rather than files — an album held twice over in two encodings is
            still the same half of an album. Only against a pressing the files are
            known to be: measured against any other, the number would be a claim.

            A badge rather than the bare text the row used: next to the year it
            would otherwise read as a second number in the same sentence.
          */}
          {album.held != null && album.trackCount != null ? (
            <Badge tone={album.held < album.trackCount ? 'warning' : 'neutral'} size="sm" mono>
              {album.held}/{album.trackCount}
            </Badge>
          ) : (
            <Badge tone="neutral" size="sm" mono>
              {album.files} {album.files === 1 ? 'file' : 'files'}
            </Badge>
          )}

          {/* Last, so that when the row wraps it is the badge that moves. */}
          {certainty !== undefined && certainty.tone !== 'ok' ? (
            <Badge tone={certainty.tone === 'warning' ? 'warning' : 'neutral'} size="sm">
              {certainty.short}
            </Badge>
          ) : null}
        </>
      }
      /* The list's order and filter ride along on the click. See the artist card. */
      render={(props) => (
        <Link
          {...props}
          to="/library/albums/$albumId"
          from="/library/albums"
          params={{ albumId: album.id }}
          search={(prev) => prev}
        />
      )}
    />
  )
}
