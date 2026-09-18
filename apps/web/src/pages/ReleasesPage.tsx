import type { components } from '@fonoteca/api-client'
import { Badge, CatalogueCard, CatalogueGrid, Input, Stack, Text } from '@fonoteca/ui'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { useDeferredValue, useId } from 'react'

import { api } from '../api.ts'
import { SortSelect } from '../components/SortSelect.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import { CERTAINTY } from './certainty.ts'
import { releaseCover } from './coverArt.ts'
import { RELEASE_DEFAULT_SORT, RELEASE_SORTS, type ReleaseListSearch } from './listSearch.ts'
import styles from './ReleasesPage.module.css'

type ReleaseSummary = components['schemas']['ReleaseSummary']

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
    from: '/library/releases',
  })

  const navigate = useNavigate({ from: '/library/releases' })

  const update = (next: ReleaseListSearch) => {
    void navigate({ search: (prev) => ({ ...prev, ...next }), replace: true })
  }

  const searchId = useId()

  const query = useDeferredValue(filter.trim())

  // The default is left out of the request rather than sent, so the URL carries
  // only what somebody actually chose. See the artist list for the whole note.
  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/releases', {
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
              ? `${state.data.total.toLocaleString()} album${state.data.total === 1 ? '' : 's'}`
              : `Showing the first ${state.data.items.length.toLocaleString()} of ${state.data.total.toLocaleString()} — narrow the filter to reach the rest.`}
          </Text>
        ) : null}
      </div>

      {state.status === 'ready' ? (
        state.data.items.length === 0 ? (
          <Empty filtered={query.length > 0} />
        ) : (
          <CatalogueGrid aria-label="Albums">
            {state.data.items.map((release) => (
              <ReleaseCard key={release.id} release={release} />
            ))}
          </CatalogueGrid>
        )
      ) : null}
    </Stack>
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

  const { folders, foldersAgreeing, foldersSplit, releasesSpanningFolders, incomplete } = state.data
  const disagreements = foldersSplit.length + releasesSpanningFolders.length

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
            {releasesSpanningFolders.length > 0 ? (
              <Text size="xs" tone="tertiary">
                {releasesSpanningFolders.length.toLocaleString()} album
                {releasesSpanningFolders.length === 1 ? '' : 's'} drew from more than one folder —
                the same album ripped twice, or a folder holding two.
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

function ReleaseCard({ release }: { readonly release: ReleaseSummary }) {
  const certainty = CERTAINTY[release.certainty]
  const partial = release.held < release.trackCount

  return (
    <CatalogueCard
      variant="album"
      title={release.title}
      image={releaseCover(release.id)}
      /*
        The artist alone on this line. The row this replaced ran artist, year
        and format together, and at tile width that sentence truncates in the
        middle of the year — so the two facts that are short enough to always
        fit have moved down to the meta row, where they do.
      */
      subtitle={release.artist ?? 'No credited artist'}
      meta={
        <>
          <Text size="xs" tone="tertiary" family="mono">
            {[release.year?.toString(), release.formats].filter(Boolean).join(' · ')}
          </Text>

          {/*
            Held against printed, so a part-ripped album reads as one at a glance.
            Tracks rather than files — an album held twice over in two encodings is
            still the same half of an album.

            A badge rather than the bare text the row used: next to the year it
            would otherwise read as a second number in the same sentence.
          */}
          <Badge tone={partial ? 'warning' : 'neutral'} size="sm" mono>
            {release.held}/{release.trackCount}
          </Badge>

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
          to="/library/releases/$releaseId"
          from="/library/releases"
          params={{ releaseId: release.id }}
          search={(prev) => prev}
        />
      )}
    />
  )
}
