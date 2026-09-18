import type { components } from '@fonoteca/api-client'
import { describeError } from '@fonoteca/api-client'
import { Badge, Button, CatalogueCard, CatalogueGrid, Input, Stack, Text } from '@fonoteca/ui'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { useDeferredValue, useId, useState } from 'react'

import { api } from '../api.ts'
import { SortSelect } from '../components/SortSelect.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './ArtistsPage.module.css'
import { artistImageUrl } from './coverArt.ts'
import {
  ARTIST_DEFAULTS,
  ARTIST_SCOPES,
  ARTIST_SORTS,
  type ArtistListSearch,
  type ArtistScope,
} from './listSearch.ts'

type ArtistSummary = components['schemas']['ArtistSummary']

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
  // The shelf's state, read from the address bar rather than from a `useState`
  // that opening an artist would destroy. See `routes.tsx`.
  const {
    query: filter = '',
    sort = ARTIST_DEFAULTS.sort,
    scope = ARTIST_DEFAULTS.scope,
  } = useSearch({ from: '/library' })

  const navigate = useNavigate({ from: '/library' })

  /**
   * Change one part of the view.
   *
   * `replace`, because a sort is a way of looking at the shelf rather than a
   * place on it: pushed, Back would undo the last keystroke of a filter instead
   * of leaving the screen, and getting out of a list somebody had typed into
   * would take twenty presses.
   *
   * A key set to `undefined` is dropped by `artistListSearch` rather than
   * written as empty, which is how choosing the default clears it from the URL.
   */
  const update = (next: ArtistListSearch) => {
    void navigate({ search: (prev) => ({ ...prev, ...next }), replace: true })
  }

  const searchId = useId()
  const followId = useId()

  const [candidate, setCandidate] = useState('')
  // `saving`, not `following`: on this page "following" is already a fact about
  // an artist, and a flag of that name meaning "a request is in flight" is the
  // one somebody misreads later.
  const [saving, setSaving] = useState(false)
  const [followError, setFollowError] = useState<string | null>(null)

  // `useApiQuery` has no cache and no invalidation — the decision `CLAUDE.md`
  // records as still open — so a list that has to be re-read after a write is
  // re-read by changing a dependency. A counter is the whole of it; adopting a
  // query library to refresh one list would be deciding that question by
  // accident.
  const [reload, setReload] = useState(0)

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
    [query, sort, scope, reload],
  )

  const follow = async () => {
    setSaving(true)

    try {
      await api.post('/api/catalogue/artists/follow', {
        json: { artist: candidate.trim() },
      })

      setCandidate('')
      setFollowError(null)

      // Shown on the shelf they were just added to, rather than left to be
      // looked for. An artist followed from here is routinely one the library
      // holds nothing by, and on the default shelf that is a tile with no
      // records under it — true, and not obviously the thing that just
      // happened.
      update({ scope: 'following' })
      setReload((seen) => seen + 1)
    } catch (cause) {
      setFollowError(describeError(cause))
    } finally {
      setSaving(false)
    }
  }

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
            : scope === 'all'
              ? 'Everyone credited on something you own — billed, conducting, playing as an ensemble, or named as the composer of the work.'
              : 'Artists you follow, whether or not you own anything by them — the one list here that is a choice rather than a reading of the library.'}
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
            onChange={(event) => update({ query: event.target.value })}
          />
        </div>

        <SortSelect
          label="Sort by"
          value={sort}
          options={ARTIST_SORTS}
          onChange={(value) => update({ sort: value === ARTIST_DEFAULTS.sort ? undefined : value })}
        />

        {/*
          The same native select, because this is the same control with different
          words in it — and the value goes to the server for the same reason the
          sort does: the list is paged there, so narrowing the page in the browser
          would narrow one slice of a library and look like it had worked.
        */}
        <SortSelect
          label="Show"
          value={scope}
          options={ARTIST_SCOPES}
          onChange={(value) =>
            update({ scope: value === ARTIST_DEFAULTS.scope ? undefined : value })
          }
        />

        {/*
          The only way into this page for an artist the library holds nothing
          by — every other name here arrived as a byproduct of a file, so there
          is no row to toggle and nothing to search for.

          An id or a URL rather than a name, and that is a constraint rather
          than a preference: the one free-text call this application makes is
          for releases, it needs a Solr index, and it fails against a
          self-hosted mirror. An MBID is a lookup and works everywhere.
        */}
        <div className={styles.search}>
          <label htmlFor={followId}>
            <Text size="xs" tone="tertiary">
              Follow by MusicBrainz id
            </Text>
          </label>
          <Stack gap={8} align="center">
            <Input
              id={followId}
              value={candidate}
              placeholder="musicbrainz.org/artist/… or the id itself"
              onChange={(event) => setCandidate(event.target.value)}
            />
            <Button
              variant="secondary"
              disabled={saving || candidate.trim() === ''}
              onClick={() => void follow()}
            >
              Follow
            </Button>
          </Stack>
        </div>
      </Stack>

      {followError != null ? (
        <Text size="sm" tone="warning">
          {followError}
        </Text>
      ) : null}

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
            scope={scope}
            onWiden={() => update({ scope: 'all' })}
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
  scope,
  onWiden,
}: {
  readonly filtered: boolean
  readonly scope: ArtistScope
  readonly onWiden: () => void
}) {
  // The fourth meaning, and it is not a narrowing at all. An empty Following
  // list is not a library that has not been enriched and not a shelf hiding
  // somebody — it is a list nobody has put anything on, so neither the pass
  // nor the widen button is the answer. Offering "search everyone credited"
  // here would answer "you follow nobody" with a button that cannot help.
  if (scope === 'following') {
    return (
      <Stack direction="column" gap={4} align="start">
        <Text tone="tertiary" block>
          {filtered ? 'No artist you follow matches that.' : 'You are not following anyone yet.'}
        </Text>
        <Text size="sm" tone="tertiary" block>
          Following is a choice rather than a reading of the library. Follow someone from their
          page, or add an artist you own nothing by with their MusicBrainz id or URL.
        </Text>
      </Stack>
    )
  }

  if (filtered) {
    return scope === 'album' ? (
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
          {/*
            First, because it is the one thing on this tile that is a decision
            rather than a reading of the library — and because on the default
            shelf a followed artist with nothing held is otherwise indis-
            tinguishable from one whose files have not been enriched yet.

            A badge and not a button: `CatalogueCard`'s `render` wraps the whole
            tile in a `Link`, so a control here would be an interactive element
            inside an interactive element. Following is toggled on the artist's
            own page, which is also where the discography it fetches is shown.
          */}
          {artist.following ? (
            <Badge tone="info" size="sm">
              following
            </Badge>
          ) : null}

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
      /*
        The shelf's order and filter travel with the click, so the artist page
        has something for its back link to hand back. Carried by the card rather
        than by a middleware on the destination route: a middleware retains by
        key and cannot tell where a navigation came from, and both lists spell
        their filter `query`.
      */
      render={(props) => (
        <Link
          {...props}
          to="/library/artists/$artistId"
          from="/library"
          params={{ artistId: artist.id }}
          search={(prev) => prev}
        />
      )}
    />
  )
}
