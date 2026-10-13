import { Badge, CatalogueGrid, Input, Stack, Text } from '@fonoteca/ui'
import { useNavigate, useSearch } from '@tanstack/react-router'
import { useDeferredValue, useId } from 'react'

import { api } from '../api.ts'
import { SortSelect } from '../components/SortSelect.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import { ArtistCard } from './ArtistsPage.tsx'
import styles from './BrowsePage.module.css'
import { COMPOSER_SORTS, type ComposerListSearch } from './listSearch.ts'

/**
 * Everyone who wrote something the library holds a recording of.
 *
 * Songwriters as well as composers, as MusicBrainz credits both; ordered by
 * what they wrote, the classical names and the prolific songwriters come first.
 */
export function ComposersPage() {
  const { query: filter = '', sort = 'name' } = useSearch({ from: '/library/composers' })
  const navigate = useNavigate({ from: '/library/composers' })
  const searchId = useId()

  const update = (next: ComposerListSearch) => {
    void navigate({ search: (prev) => ({ ...prev, ...next }), replace: true })
  }

  const query = useDeferredValue(filter.trim())

  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/composers', {
        params: { query: { ...(query ? { query } : {}), ...(sort === 'name' ? {} : { sort }) } },
      }),
    [query, sort],
  )

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Composers
          </Text>
        </h1>
        <Text tone="secondary" block>
          Who wrote the music you hold — composers and songwriters alike, each with the pieces of
          theirs you have a recording of.
        </Text>
      </Stack>

      <Stack gap={16} align="end" wrap>
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
            placeholder="Mozart, Knopfler, Gershwin…"
            onChange={(event) => update({ query: event.target.value })}
          />
        </div>

        <SortSelect
          label="Sort by"
          value={sort}
          options={COMPOSER_SORTS}
          onChange={(value) => update({ sort: value === 'name' ? undefined : value })}
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
          <Text size="sm" tone="tertiary">
            {state.data.total === 0
              ? query.length > 0
                ? 'No composer matches that.'
                : 'No composers yet: they arrive when enrichment describes the recordings.'
              : `${state.data.total.toLocaleString()} composer${state.data.total === 1 ? '' : 's'}`}
          </Text>
        ) : null}
      </div>

      {state.status === 'ready' && state.data.items.length > 0 ? (
        <CatalogueGrid aria-label="Composers">
          {state.data.items.map((artist) => (
            <ArtistCard key={artist.id} artist={artist} counting="compositions" />
          ))}
        </CatalogueGrid>
      ) : null}
    </Stack>
  )
}
