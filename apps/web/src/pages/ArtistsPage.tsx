import type { components } from '@fonoteca/api-client'
import { Badge, Input, Stack, Text } from '@fonoteca/ui'
import { Link } from '@tanstack/react-router'
import { useDeferredValue, useId, useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './ArtistsPage.module.css'

type ArtistSummary = components['schemas']['ArtistSummary']

/**
 * The library, by artist.
 *
 * Everyone the catalogue can reach a track through — the names on credit lines,
 * the conductors, the orchestras and the composers — as one alphabetical list.
 * A classical library makes that mixture obvious and it is the point rather than
 * a wrinkle: Karajan and Mozart are both artists you would want to browse to,
 * and only one of them is ever on a credit line.
 */
export function ArtistsPage() {
  const [filter, setFilter] = useState('')
  const searchId = useId()

  // The typed value drives the input and a deferred copy drives the request, so
  // typing stays responsive without a hand-rolled debounce timer — React
  // already knows how to let the urgent update win.
  const query = useDeferredValue(filter.trim())

  // The key omitted rather than set to undefined: `exactOptionalPropertyTypes`
  // draws that distinction and it is the right one here, since an empty filter
  // is the absence of a filter rather than a filter for nothing.
  const state = useApiQuery(
    () => api.get('/api/catalogue/artists', { params: { query: query ? { query } : {} } }),
    [query],
  )

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        {/* The heading element carries the semantics; Text carries the type. */}
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Library
          </Text>
        </h1>
        <Text tone="secondary" block>
          Everyone credited on something you own — billed, conducting, playing as an ensemble, or
          named as the composer of the work.
        </Text>
      </Stack>

      <div className={styles.search}>
        <label htmlFor={searchId}>
          <Text size="xs" tone="tertiary">
            Filter by name
          </Text>
        </label>
        <Input
          id={searchId}
          type="search"
          value={filter}
          placeholder="Karajan, Bonamassa, Mozart…"
          onChange={(event) => setFilter(event.target.value)}
        />
      </div>

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
          <Results total={state.data.total} shown={state.data.items.length} />
        ) : null}
      </div>

      {state.status === 'ready' ? (
        state.data.items.length === 0 ? (
          <Empty filtered={query.length > 0} />
        ) : (
          <ul className={styles.list}>
            {state.data.items.map((artist) => (
              <li key={artist.id}>
                <ArtistCard artist={artist} />
              </li>
            ))}
          </ul>
        )
      ) : null}
    </Stack>
  )
}

/**
 * How many artists there are, and — when the response was capped — what to do
 * about it.
 *
 * "500 of 2,752" is honest and useless on its own: it does not say that the
 * missing 2,252 are unreachable by scrolling. The server's cap is high enough
 * that this should not fire on a library of this size, which is exactly why it
 * has to say something actionable when it does.
 */
function Results({ total, shown }: { readonly total: number; readonly shown: number }) {
  if (shown === total) {
    return (
      <Text size="sm" tone="tertiary">
        {total.toLocaleString()} artist{total === 1 ? '' : 's'}
      </Text>
    )
  }

  return (
    <Text size="sm" tone="warning">
      Showing the first {shown.toLocaleString()} of {total.toLocaleString()} artists — narrow the
      filter to reach the rest.
    </Text>
  )
}

/**
 * Empty means two different things and they deserve different sentences: a
 * filter that matched nothing is the user's next keystroke, an empty catalogue
 * is a pass that has not been run.
 */
function Empty({ filtered }: { readonly filtered: boolean }) {
  return filtered ? (
    <Text tone="tertiary">No artist matches that.</Text>
  ) : (
    <Stack direction="column" gap={4} align="start">
      <Text tone="tertiary" block>
        Nothing here yet.
      </Text>
      <Text size="sm" tone="tertiary" block>
        Artists appear once the enrichment pass has asked MusicBrainz who made the identified files.
        Run it from <Link to="/">Foundation</Link>.
      </Text>
    </Stack>
  )
}

function ArtistCard({ artist }: { readonly artist: ArtistSummary }) {
  return (
    <Link
      to="/library/artists/$artistId"
      params={{ artistId: artist.id }}
      className={styles.artist}
    >
      <Stack direction="column" gap={2}>
        <Text weight="medium" truncate>
          {artist.name}
        </Text>
        {artist.disambiguation != null ? (
          <Text size="xs" tone="tertiary" truncate>
            {artist.disambiguation}
          </Text>
        ) : null}
      </Stack>

      <Stack gap={8} align="center">
        {/*
          The type is worth the space on a library like this one: it is what
          tells an orchestra from the person conducting it at a glance, and
          those sit next to each other in an alphabetical list.
        */}
        {artist.type != null && artist.type !== 'Person' ? (
          <Badge tone="neutral" size="sm">
            {artist.type}
          </Badge>
        ) : null}
        <Text size="sm" tone="tertiary" family="mono">
          {artist.trackCount.toLocaleString()}
        </Text>
      </Stack>
    </Link>
  )
}
