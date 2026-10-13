import type { components } from '@fonoteca/api-client'
import { Badge, Input, Stack, Table, TableCell, TableHeaderCell, Text } from '@fonoteca/ui'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { Fragment, useDeferredValue, useId, useRef } from 'react'

import { api } from '../api.ts'
import { Pager } from '../components/Pager.tsx'
import { SortSelect } from '../components/SortSelect.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './BrowsePage.module.css'
import { COMPOSITION_SORTS, type CompositionSort, type PagedListSearch } from './listSearch.ts'

type CompositionRow = components['schemas']['CompositionRow']

const PAGE = 100

/**
 * Every piece the library holds a recording of, with each performance of it.
 *
 * A symphony is one row however many movements it has, and a standard is one
 * row with every singer who recorded it under it — which is what this page is
 * for that the artist and album lists are not.
 */
export function CompositionsPage() {
  const {
    query: filter = '',
    sort = 'title',
    page = 1,
  } = useSearch({ from: '/library/compositions' })
  const navigate = useNavigate({ from: '/library/compositions' })
  const heading = useRef<HTMLHeadingElement>(null)
  const searchId = useId()

  const update = (next: PagedListSearch<CompositionSort>) => {
    void navigate({ search: (prev) => ({ ...prev, page: undefined, ...next }), replace: true })
  }

  const go = (to: number) => {
    void navigate({ search: (prev) => ({ ...prev, page: to > 1 ? to : undefined }) })
    heading.current?.scrollIntoView()
  }

  const query = useDeferredValue(filter.trim())

  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/compositions', {
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
            Compositions
          </Text>
        </h1>
        <Text tone="secondary" block>
          The pieces you hold, a symphony once however many movements it has, with every performance
          of each.
        </Text>
      </Stack>

      <Stack gap={16} align="end" wrap>
        <div className={styles.search}>
          <label htmlFor={searchId}>
            <Text size="xs" tone="tertiary">
              Filter by title or composer
            </Text>
          </label>
          <Input
            id={searchId}
            type="search"
            value={filter}
            placeholder="Summertime, Beethoven, Ellington…"
            onChange={(event) => update({ query: event.target.value })}
          />
        </div>

        <SortSelect
          label="Sort by"
          value={sort}
          options={COMPOSITION_SORTS}
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
            {query.length > 0
              ? 'No composition matches that.'
              : 'No compositions yet: they arrive when enrichment describes the recordings.'}
          </Text>
        ) : null}
      </div>

      {state.status === 'ready' && state.data.total > 0 ? (
        <>
          <Pager page={page} size={PAGE} total={state.data.total} noun="compositions" onPage={go} />

          <div className={`${styles.table} ${styles.compositions}`}>
            <Table density="cozy">
              <thead>
                <tr>
                  <TableHeaderCell className={styles.pieceCol}>Composition</TableHeaderCell>
                  <TableHeaderCell className={styles.composerCol}>Composer</TableHeaderCell>
                  <TableHeaderCell numeric className={styles.countCol}>
                    Performances
                  </TableHeaderCell>
                  <TableHeaderCell>Performed by</TableHeaderCell>
                </tr>
              </thead>
              <tbody>
                {state.data.items.map((composition) => (
                  <Row key={composition.id} composition={composition} />
                ))}
              </tbody>
            </Table>
          </div>

          <Pager page={page} size={PAGE} total={state.data.total} noun="compositions" onPage={go} />
        </>
      ) : null}
    </Stack>
  )
}

function Row({ composition }: { readonly composition: CompositionRow }) {
  return (
    <tr>
      <TableCell>
        <Stack direction="column" gap={4} align="start">
          <Text>{composition.title}</Text>
          {/* Song is nearly every row here, so only the other kinds are worth a badge. */}
          {composition.type != null && composition.type !== 'Song' ? (
            <Badge tone="neutral" size="sm">
              {composition.type}
            </Badge>
          ) : null}
        </Stack>
      </TableCell>

      <TableCell>
        {composition.composers.length === 0 ? (
          <Text size="sm" tone="tertiary">
            Not recorded
          </Text>
        ) : (
          composition.composers.map((composer, index) => (
            <Fragment key={composer.id}>
              {index > 0 ? <Text size="sm">, </Text> : null}
              <Link
                to="/library/artists/$artistId"
                params={{ artistId: composer.id }}
                className={styles.link}
              >
                <Text size="sm" tone="accent">
                  {composer.name}
                </Text>
              </Link>
            </Fragment>
          ))
        )}
      </TableCell>

      <TableCell numeric>
        <Text size="sm" family="mono">
          {composition.performances.length}
        </Text>
      </TableCell>

      <TableCell>
        <Stack direction="column" gap={4}>
          {composition.performances.map((performance) => (
            <span key={`${performance.albumId ?? performance.album}:${performance.performers}`}>
              {performance.performers ?? (
                <Text size="sm" tone="tertiary">
                  Performers not recorded
                </Text>
              )}{' '}
              <Text size="xs" tone="tertiary">
                {performance.albumId != null ? (
                  <Link
                    to="/library/albums/$albumId"
                    params={{ albumId: performance.albumId }}
                    className={styles.link}
                  >
                    <Text size="xs" tone="accent">
                      {performance.album}
                    </Text>
                  </Link>
                ) : (
                  `${performance.album} (folder)`
                )}
                {[
                  performance.year,
                  performance.tracks > 1
                    ? `${performance.tracks} ${performance.movements ? 'movements' : 'tracks'}`
                    : null,
                ]
                  .filter((part) => part != null)
                  .map((part) => ` · ${part}`)
                  .join('')}
              </Text>
            </span>
          ))}
        </Stack>
      </TableCell>
    </tr>
  )
}
