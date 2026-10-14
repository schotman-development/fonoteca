import type { components } from '@fonoteca/api-client'
import { Badge, CatalogueCard, CatalogueGrid, Stack, Text } from '@fonoteca/ui'
import { Link, useParams } from '@tanstack/react-router'
import { Fragment } from 'react'

import { api } from '../api.ts'
import { type QueryState, useApiQuery } from '../useApiQuery.ts'
import { ArtistCard } from './ArtistsPage.tsx'
import styles from './BrowsePage.module.css'
import { releaseCover } from './coverArt.ts'
import { Shelf } from './HomePage.tsx'
import { AlbumCard } from './ReleasesPage.tsx'

type GenreSummary = components['schemas']['GenreSummary']

/**
 * The library by genre, after Roon: Fonoteca's own tops as tiles, each a
 * mosaic of four of its sleeves.
 *
 * The genres are Fonoteca's, not MusicBrainz's — every MusicBrainz genre is
 * folded into one of about a hundred, two levels deep (`Genres.cs`). An album
 * counts under every genre it carries and under their tops, so the tiles add
 * up to more than the library.
 */
export function GenresPage() {
  const state = useApiQuery(() => api.get('/api/catalogue/genres'), [])

  return (
    <Stack direction="column" gap={20}>
      <h1 className={styles.title}>
        <Text size="xl" weight="semibold" block>
          Genres
        </Text>
      </h1>

      <Loading state={state} />

      {state.status === 'ready' ? (
        state.data.genres.length === 0 ? (
          <Text tone="tertiary">
            No album carries a genre yet. Genres arrive with the enrich pass.
          </Text>
        ) : (
          <CatalogueGrid aria-label="Genres">
            {state.data.genres
              .filter((genre) => genre.tops.length === 0)
              .map((genre) => (
                <GenreCard key={genre.name} genre={genre} />
              ))}
          </CatalogueGrid>
        )
      ) : null}
    </Stack>
  )
}

/**
 * One genre: the tops it sits under, its subgenres, and the albums and artists
 * carrying it — a top's shelves hold its subgenres' too.
 */
export function GenrePage() {
  const { genre } = useParams({ from: '/library/genres/$genre' })

  const tree = useApiQuery(() => api.get('/api/catalogue/genres'), [])
  const albums = useApiQuery(
    () => api.get('/api/catalogue/albums', { params: { query: { genre } } }),
    [genre],
  )
  const artists = useApiQuery(
    () => api.get('/api/catalogue/artists', { params: { query: { genre, sort: 'albums' } } }),
    [genre],
  )

  const here =
    tree.status === 'ready' ? tree.data.genres.find((entry) => entry.name === genre) : undefined
  const subgenres =
    tree.status === 'ready' ? tree.data.genres.filter((entry) => entry.tops.includes(genre)) : []

  return (
    <Stack direction="column" gap={32}>
      <Stack direction="column" gap={4}>
        <Text size="sm" tone="tertiary">
          {here != null && here.tops.length > 0 ? (
            here.tops.map((top, index) => (
              <Fragment key={top}>
                {index > 0 ? ' · ' : null}
                <Link className={styles.link} to="/library/genres/$genre" params={{ genre: top }}>
                  <Text size="sm" tone="accent">
                    {top}
                  </Text>
                </Link>
              </Fragment>
            ))
          ) : (
            <Link className={styles.link} to="/library/genres">
              <Text size="sm" tone="accent">
                Genres
              </Text>
            </Link>
          )}
        </Text>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            {genre}
          </Text>
        </h1>
        {here != null ? (
          <Text tone="secondary">{albumCount(here.albums)}</Text>
        ) : tree.status === 'ready' ? (
          <Text tone="tertiary">No album here carries this genre.</Text>
        ) : null}
      </Stack>

      <Loading state={tree} />

      {subgenres.length > 0 ? (
        <section aria-label="Subgenres">
          <CatalogueGrid aria-label="Subgenres">
            {subgenres.map((entry) => (
              <GenreCard key={entry.name} genre={entry} />
            ))}
          </CatalogueGrid>
        </section>
      ) : null}

      <Shelf title="Albums" more={null} state={albums}>
        {(data) =>
          data.items.length === 0 ? (
            <Text tone="tertiary">No albums.</Text>
          ) : (
            <CatalogueGrid size="album" layout="shelf" aria-label={`${genre} albums`}>
              {data.items.map((album) => (
                <AlbumCard key={album.id} album={album} />
              ))}
            </CatalogueGrid>
          )
        }
      </Shelf>

      <Shelf title="Artists" more={null} state={artists}>
        {(data) =>
          data.items.length === 0 ? (
            <Text tone="tertiary">No artists.</Text>
          ) : (
            <CatalogueGrid size="artist" layout="shelf" aria-label={`${genre} artists`}>
              {data.items.map((artist) => (
                <ArtistCard key={artist.id} artist={artist} counting="albums" />
              ))}
            </CatalogueGrid>
          )
        }
      </Shelf>
    </Stack>
  )
}

function GenreCard({ genre }: { readonly genre: GenreSummary }) {
  return (
    <CatalogueCard
      variant="album"
      title={genre.name}
      subtitle={albumCount(genre.albums)}
      mosaic={genre.covers.map((id) => releaseCover(id))}
      render={(props) => (
        <Link {...props} to="/library/genres/$genre" params={{ genre: genre.name }} />
      )}
    />
  )
}

function Loading<T>({ state }: { readonly state: QueryState<T> }) {
  return (
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
    </div>
  )
}

function albumCount(count: number) {
  return `${count.toLocaleString()} album${count === 1 ? '' : 's'}`
}
