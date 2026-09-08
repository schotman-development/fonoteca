import type { components } from '@fonoteca/api-client'
import { Badge, Button, CatalogueCard, CatalogueGrid, Input, Stack, Text } from '@fonoteca/ui'
import { Link } from '@tanstack/react-router'
import { useDeferredValue, useId, useState } from 'react'

import { api } from '../api.ts'
import { SortSelect } from '../components/SortSelect.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './ArtistsPage.module.css'
import { artistImageUrl } from './coverArt.ts'

type ArtistSummary = components['schemas']['ArtistSummary']

/**
 * The two orders worth having, and the reason there is not a third.
 *
 * Alphabetical is how you find somebody you already have in mind; by holdings
 * is how you find out who this library is actually *about*, which on a
 * collection assembled over years is rarely who you would guess. Everything
 * else — by type, by year of first release — is a filter wearing a sort's
 * clothes, and neither is a column here.
 */
const SORTS = [
  ['name', 'Name'],
  ['tracks', 'Most tracks'],
] as const

type Sort = (typeof SORTS)[number][0]

/**
 * Which names count as artists here.
 *
 * The union the catalogue can reach a track through — credit lines, conductors,
 * ensembles and the composer of the work — is 2,860 names on the author's
 * library, of which 2,157 are songwriters and lyricists with one track each.
 * That is the right answer to "whose page can I reach this recording from" and a
 * useless front page. The default is the sleeve: who the album is *by*.
 */
const SCOPES = [
  ['album', 'Album artists'],
  ['all', 'Everyone credited'],
] as const

type Scope = (typeof SCOPES)[number][0]

/**
 * The library, by artist.
 *
 * A shelf of records by default — who the albums are by, which is the question
 * somebody opening this page is asking. The wider list is still one select away
 * and it is a genuinely different question: Karajan and Mozart are both artists
 * you would want to browse to, and only one of them is ever on a sleeve as the
 * album's own artist.
 */
export function ArtistsPage() {
  const [filter, setFilter] = useState('')
  const [sort, setSort] = useState<Sort>('name')
  const [scope, setScope] = useState<Scope>('album')
  const searchId = useId()

  // The typed value drives the input and a deferred copy drives the request, so
  // typing stays responsive without a hand-rolled debounce timer — React
  // already knows how to let the urgent update win.
  const query = useDeferredValue(filter.trim())

  // The key omitted rather than set to undefined: `exactOptionalPropertyTypes`
  // draws that distinction and it is the right one here, since an empty filter
  // is the absence of a filter rather than a filter for nothing.
  // `sort` is only sent when it is not the default, for the same reason the
  // filter is omitted rather than sent empty: the absence of a sort is what the
  // endpoint documents as sort name, and spelling it out would put a parameter
  // in the URL that means "do what you were going to do anyway".
  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/artists', {
        params: {
          query: {
            ...(query ? { query } : {}),
            ...(sort === 'name' ? {} : { sort }),
            ...(scope === 'album' ? {} : { scope }),
          },
        },
      }),
    [query, sort, scope],
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
          {scope === 'album'
            ? 'Who your albums are by, collaborations included — the name on the sleeve rather than everyone on the record.'
            : 'Everyone credited on something you own — billed, conducting, playing as an ensemble, or named as the composer of the work.'}
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
            placeholder="Karajan, Bonamassa, Mozart…"
            onChange={(event) => setFilter(event.target.value)}
          />
        </div>

        <SortSelect label="Sort by" value={sort} options={SORTS} onChange={setSort} />

        {/*
          The same native select, because this is the same control with different
          words in it — and the value goes to the server for the same reason the
          sort does: the list is paged there, so narrowing the page in the browser
          would narrow one slice of a library and look like it had worked.
        */}
        <SortSelect label="Show" value={scope} options={SCOPES} onChange={setScope} />
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
          <Results total={state.data.total} shown={state.data.items.length} />
        ) : null}
      </div>

      {state.status === 'ready' ? (
        state.data.items.length === 0 ? (
          <Empty
            filtered={query.length > 0}
            narrowed={scope === 'album'}
            onWiden={() => setScope('all')}
          />
        ) : (
          /*
            No `size` prop: the grid reads its own contents, and everything in
            it is an artist card. Pinning it would state the same fact twice
            and be the copy that goes stale.
          */
          <CatalogueGrid aria-label="Artists">
            {state.data.items.map((artist) => (
              <ArtistCard key={artist.id} artist={artist} />
            ))}
          </CatalogueGrid>
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
 * Empty means three things now, and the third one is a trap.
 *
 * A filter that matched nothing is the user's next keystroke and an empty
 * catalogue is a pass that has not been run — but a filter that matched nothing
 * *on the shelf* is neither. 2,534 of the artists this catalogue holds are off
 * it, every one with a page and some with substantial holdings: searching a band
 * member by name returns nothing here while their page lists seventy tracks.
 * Answered with "No artist matches that" a person concludes the library does not
 * hold them, which is the one wrong conclusion available. So the sentence names
 * the narrowing and the button undoes it.
 */
function Empty({
  filtered,
  narrowed,
  onWiden,
}: {
  readonly filtered: boolean
  readonly narrowed: boolean
  readonly onWiden: () => void
}) {
  if (filtered) {
    return narrowed ? (
      <Stack direction="column" gap={8} align="start">
        <Text tone="tertiary" block>
          No album artist matches that.
        </Text>
        <Button variant="secondary" onClick={onWiden}>
          Search everyone credited
        </Button>
      </Stack>
    ) : (
      <Text tone="tertiary">No artist matches that.</Text>
    )
  }

  return (
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

/**
 * One artist, as a tile.
 *
 * `render` rather than `href`: the design system has no router and will not
 * grow one, so the caller owns the element. Every prop it is handed has to be
 * spread onto the `Link` — the `data-catalogue-card` attribute among them is
 * what the grid sizes its columns by, and dropping it would silently give the
 * whole page the album width.
 */
function ArtistCard({ artist }: { readonly artist: ArtistSummary }) {
  const image = artistImageUrl(artist)

  return (
    <CatalogueCard
      variant="artist"
      title={artist.name}
      /*
        A photograph where Wikidata has one, and an album of theirs where it does
        not. The order is the whole point: a page of sleeves answers "what do I
        own" and a page of faces answers "who is this", and only the second is
        the question somebody scanning three hundred names is asking. The
        fallback stays because roughly a quarter of them have no picture
        anywhere.

        Spread rather than passed as `undefined`, which `exactOptionalPropertyTypes`
        draws a distinction between — and the absent prop is what makes the card
        draw its monogram.
      */
      {...(image != null ? { image } : {})}
      /*
        The count and nothing else. It is on every artist, it is what says
        whether a name is a whole shelf or one guest appearance, and it is
        short enough to survive a tile — the subtitle truncates to one line,
        so anything sharing it with the count is shown as a fragment or not
        at all.
      */
      subtitle={`${artist.trackCount.toLocaleString()} tracks`}
      meta={
        <>
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

          {/*
            The disambiguation, wrapping rather than truncating. It is the only
            thing telling two artists of the same name apart, so half of it is
            worth less than all of it on two lines — and it is rare enough that
            the tiles it makes taller are a handful in a page.
          */}
          {artist.disambiguation != null ? (
            <Text size="xs" tone="tertiary">
              {artist.disambiguation}
            </Text>
          ) : null}
        </>
      }
      render={(props) => (
        <Link {...props} to="/library/artists/$artistId" params={{ artistId: artist.id }} />
      )}
    />
  )
}
