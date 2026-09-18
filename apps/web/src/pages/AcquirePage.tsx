import type { components } from '@fonoteca/api-client'
import {
  Badge,
  Button,
  CatalogueCard,
  CatalogueGrid,
  Input,
  Stack,
  Text,
  VisuallyHidden,
} from '@fonoteca/ui'
import type { FocusEvent, KeyboardEvent, ReactNode } from 'react'
import { useEffect, useId, useRef, useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './AcquirePage.module.css'
import { releaseArt, releaseCover, releaseGroupArt } from './coverArt.ts'
import { QobuzAlbumDialog, type Replacing } from './QobuzAlbumDialog.tsx'
import { readiness } from './qobuz.ts'

type QobuzAlbumSummary = components['schemas']['QobuzAlbumSummary']
type UpgradeListResponse = components['schemas']['UpgradeListResponse']
type UpgradeCandidate = components['schemas']['UpgradeCandidate']
type IncompleteAlbum = components['schemas']['IncompleteAlbum']
type MissingTrack = components['schemas']['MissingTrack']
type MissingRecord = components['schemas']['MissingRecord']

/**
 * Buying music, by hand.
 *
 * The one screen in the application that spends money, and the only one whose
 * every action is irreversible in a way an undo journal cannot help with. So
 * nothing here happens on its own: there is no wanted list, no monitor and no
 * upgrade sweep, and a download is a person who searched, read a track list and
 * pressed a button. "Keep it manual" is the design, not a stage it is passing
 * through — automating a reverse-engineered API on a paid personal account puts
 * the account at risk, not a retry.
 *
 * What lands is a folder of audio in the library, not a row in the catalogue.
 * Nothing here writes to the database: the files become ordinary library files,
 * and the next scan is what catalogues them.
 */
export function AcquirePage() {
  const [term, setTerm] = useState('')
  /*
    A counter beside the term, not just the term. `setAsked(same string)` is a
    React bailout and `deps: [asked]` would not change either, so pressing
    Search again after a failed search did nothing at all — the only way to
    retry was to edit the text. MatchingDialog already carries this scar.
  */
  const [asked, setAsked] = useState<{ readonly query: string; readonly attempt: number } | null>(
    null,
  )
  const [open, setOpen] = useState<QobuzAlbumSummary | null>(null)
  const searchId = useId()

  /*
    Where a shelf press takes you. The form is above the shelves, so pressing a
    tile fills a box that is off the top of the screen and renders its results
    there too — the click reads as having done nothing at all. Scrolled to, the
    box shows the term that was searched and the results arrive underneath it.

    The form rather than the results, because at the moment of the press the
    results do not exist yet: the request has not come back, and there is no
    element to scroll to.
  */
  const form = useRef<HTMLFormElement>(null)

  const status = useApiQuery(() => api.get('/api/qobuz/status'), [])
  const upgrades = useApiQuery(() => api.get('/api/qobuz/upgrades'), [])

  /*
    What the current search is meant to replace, if it came from an upgrade tile.
    It survives until the next search rather than until the dialog closes: a
    person opens two or three candidates before choosing one, and losing the
    target on the first dismissal would silently turn the second choice into an
    ordinary download.
  */
  const [replacing, setReplacing] = useState<Replacing | null>(null)

  /*
    Both the form and a shelf tile go through here, so a tile click is a search
    somebody could have typed — the box ends up holding the term that produced
    the results, rather than the two disagreeing.
  */
  const ask = (query: string, replaces: Replacing | null = null) => {
    setTerm(query)
    setReplacing(replaces)
    setAsked((previous) => ({ query, attempt: (previous?.attempt ?? 0) + 1 }))

    // Instant where somebody has asked for less motion. `scroll-behavior` in
    // the stylesheet would be the platform answer and cannot be used here: it
    // belongs on the scroll container, which is the shell's, not this page's.
    form.current?.scrollIntoView({
      block: 'start',
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    })
  }

  /*
    Searched on submit rather than as you type, which is the opposite of the
    album and artist filters on the browse screens — and the difference is who
    is being asked. Those filter a local catalogue; this one is a request to
    somebody else's rate-limited service, and a keystroke-per-request search box
    is exactly the traffic shape that gets a subscription looked at.
  */
  const results = useApiQuery(
    () =>
      asked === null
        ? Promise.resolve(null)
        : api.get('/api/qobuz/albums', { params: { query: { query: asked.query } } }),
    [asked?.query, asked?.attempt],
  )

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Acquire
          </Text>
        </h1>
        <Text tone="secondary" block>
          Search Qobuz and download an album straight into the library. Nothing is queued, scheduled
          or monitored — every download is one you asked for.
        </Text>
      </Stack>

      <Status state={status} />

      <form
        ref={form}
        className={styles.search}
        onSubmit={(event) => {
          event.preventDefault()
          const query = term.trim()
          if (query.length === 0) return

          ask(query)
        }}
      >
        <label htmlFor={searchId}>
          <Text size="xs" tone="tertiary">
            Album, artist, or a Qobuz album id
          </Text>
        </label>
        <Stack gap={8} align="center">
          <Input
            id={searchId}
            type="search"
            value={term}
            fullWidth
            placeholder="Rumours, Kind of Blue…"
            onChange={(event) => setTerm(event.target.value)}
          />
          {/* Disabled on an empty box, rather than a submit that silently does nothing. */}
          <Button type="submit" variant="secondary" disabled={term.trim().length === 0}>
            Search
          </Button>
        </Stack>
      </form>

      <div role="status" aria-live="polite" aria-busy={results.status === 'loading'}>
        {asked !== null && results.status === 'loading' ? (
          <Text tone="tertiary">Asking Qobuz…</Text>
        ) : null}

        {results.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Qobuz could not answer</Badge>
            <Text size="sm" tone="tertiary">
              {results.message}
            </Text>
          </Stack>
        ) : null}

        {results.status === 'ready' && results.data !== null ? (
          <Text size="sm" tone="tertiary">
            {results.data.length === 0
              ? 'Nothing matched.'
              : `${results.data.length} album${results.data.length === 1 ? '' : 's'}`}
          </Text>
        ) : null}
      </div>

      {/*
        Said above the results rather than only inside the dialog, because this
        is the state a person is in while browsing: every album on this screen is
        now a candidate to replace that folder, including the ones that are
        nothing to do with it.
      */}
      {replacing !== null && results.status === 'ready' && results.data !== null ? (
        <Stack gap={8} align="center" wrap>
          <Badge tone="warning" size="sm">
            Upgrading
          </Badge>
          <Text size="sm" tone="secondary">
            Whichever album you download will replace{' '}
            <Text size="sm" family="mono" tone="primary">
              {replacing.folder}
            </Text>
          </Text>
          <Button size="sm" variant="ghost" onClick={() => setReplacing(null)}>
            Just download instead
          </Button>
        </Stack>
      ) : null}

      {results.status === 'ready' && results.data !== null && results.data.length > 0 ? (
        <CatalogueGrid aria-label="Qobuz search results">
          {results.data.map((album) => (
            <AlbumCard key={album.id} album={album} onOpen={() => setOpen(album)} />
          ))}
        </CatalogueGrid>
      ) : null}

      {/*
        The shelves sit under the search box, not over it, and that is a
        keyboard decision rather than a visual one. They are 520 and 47 tiles;
        above the form, reaching the box a person came here to type in meant
        tabbing past every one of them. Under it the box is two stops from the
        top of the page, and a shelf press — which fills the box and searches —
        lands its results where the eye already is.
      */}
      <Upgrades
        state={upgrades}
        onSearch={(item) =>
          ask(item.query, {
            folder: item.folder,
            title: item.title,
            files: item.files,
            formats: item.formats,
          })
        }
      />

      <Incomplete
        state={upgrades}
        /*
          A plain search, never an upgrade. Replacing refuses when what arrives
          is no better than the best file already held — see
          `AlbumReplacementService` — which is exactly the case here: the tracks
          that are missing are missing, and the ones that are not are usually
          the same quality. So the album lands in staging and the person moves
          in the tracks they wanted.
        */
        onSearch={(album) => ask(album.query)}
      />

      {/* A plain search for the same reason: there is no file to replace. */}
      <Missing state={upgrades} onSearch={(record) => ask(record.query)} />

      {open !== null ? (
        <QobuzAlbumDialog
          album={open}
          canDownload={status.status === 'ready' && status.data.canDownload}
          {...(replacing !== null ? { replacing } : {})}
          onClose={() => setOpen(null)}
        />
      ) : null}
    </Stack>
  )
}

/**
 * A titled shelf: a heading, what it is a list of, and the covers.
 *
 * Two callers with the same shape, so the heading level and the count badge are
 * decided once. `<h2>` under the page's one `<h1>` — a screen reader's outline
 * of this page is "Acquire, Upgrade candidates, Incomplete albums", which is
 * also the only thing that says the shelves are two lists rather than one long
 * one, since they run down the page with no rule between them.
 *
 * **One tab stop, and the arrow keys move along it.** This was a `<ul>` of 520
 * buttons, which is 520 tab stops in front of everything below it. A roving
 * `tabindex` is what a long run of like controls is supposed to do, and
 * `role="toolbar"` is the half that makes it discoverable — without the role a
 * reader lands on the first cover, tabs once, and never learns the other 519
 * are there, which is worse than the problem it fixes.
 *
 * The role costs the list semantics, so the count moves into the label:
 * "list, 47 items" becomes "toolbar, Albums held in part…, 47 albums". The
 * ordering is in there too, because it is what makes the first cover worth
 * looking at rather than an arbitrary one.
 */
function Shelf({
  title,
  count,
  detail,
  label,
  children,
}: {
  readonly title: string
  readonly count: number
  readonly detail: ReactNode
  readonly label: string
  readonly children: ReactNode
}) {
  const shelf = useRef<HTMLDivElement>(null)

  const tiles = () => [...(shelf.current?.querySelectorAll('button') ?? [])]

  /*
    Exactly one tile is tabbable, and on a fresh shelf it is the first.

    Every render and no dependency array, because what this has to react to is
    the buttons changing and they are somebody else's children — there is no
    value here to key it on. That is only safe because it is idempotent: a shelf
    that already has its one tab stop is left exactly as it is, so arriving back
    here does not throw away the cover a person had arrowed to.
  */
  useEffect(() => {
    const all = tiles()

    /*
      The attribute, never the property — and reading the property is why this
      never ran. A `<button>` is natively focusable, so `tile.tabIndex` reports
      `0` on a tile that carries no `tabindex` at all: the bail was therefore
      true on the first render of every shelf, nothing was ever assigned, and
      all of them stayed tab stops until somebody pressed an arrow key. Measured
      on the running page, all 527 upgrade tiles and all 27 incomplete ones had
      no attribute — precisely the "520 tab stops in front of everything below
      it" this effect exists to prevent.
    */
    if (all.some((tile) => tile.getAttribute('tabindex') === '0')) return

    for (const [index, tile] of all.entries()) tile.tabIndex = index === 0 ? 0 : -1
  })

  const rove = (to: number) => {
    const all = tiles()
    const target = all[Math.min(Math.max(to, 0), all.length - 1)]

    if (target === undefined) return

    for (const tile of all) tile.tabIndex = -1
    target.tabIndex = 0
    target.focus()
  }

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const all = tiles()
    const from = all.indexOf(document.activeElement as HTMLButtonElement)

    if (from < 0) return

    // Only the four. Everything else — Tab out, Enter and Space to press —
    // is the button's own behaviour and must keep working.
    const to =
      event.key === 'ArrowRight'
        ? from + 1
        : event.key === 'ArrowLeft'
          ? from - 1
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? all.length - 1
              : null

    if (to === null) return

    event.preventDefault()
    rove(to)
  }

  /*
    A mouse press moves the tab stop too, so shift-tabbing back into a shelf
    returns to the cover somebody was last on rather than to the far left of a
    shelf they have scrolled halfway along.
  */
  const onFocus = (event: FocusEvent<HTMLDivElement>) => {
    const target = event.target

    if (!(target instanceof HTMLButtonElement)) return

    for (const tile of tiles()) tile.tabIndex = -1
    target.tabIndex = 0
  }

  return (
    <Stack direction="column" gap={4}>
      <Stack gap={8} align="center">
        <h2 className={styles.heading}>
          <Text weight="semibold">{title}</Text>
        </h2>
        <Badge tone="info" size="sm">
          {count}
        </Badge>
      </Stack>

      <Text size="sm" tone="secondary" block>
        {detail}
      </Text>

      <div
        ref={shelf}
        className={styles.shelf}
        role="toolbar"
        aria-orientation="horizontal"
        aria-label={`${label}, ${count} albums`}
        onKeyDown={onKeyDown}
        onFocus={onFocus}
      >
        {children}
      </div>
    </Stack>
  )
}

/**
 * What each reason means on a tile, and how much of a win it is.
 *
 * Ordered the way the list is: a lossy album is a bigger upgrade than a CD rip
 * against a hi-res master, and on this library there are five times as many of
 * the second. Interleaved they bury the first.
 */
const REASONS = {
  Lossy: { label: 'Lossy', tone: 'warning' },
  BelowHiRes: { label: 'CD quality', tone: 'info' },
} as const satisfies Record<string, { readonly label: string; readonly tone: 'warning' | 'info' }>

/**
 * Albums held in something worse than Qobuz sells.
 *
 * The scan behind it asks Qobuz nothing — see `ListUpgrades`. So this is a shelf
 * of *questions*, not of offers: it says which albums are worth looking up, and
 * pressing a tile runs the ordinary search with the album's name in it. Whether
 * Qobuz actually sells something better is answered by the results, the same way
 * it is for a search somebody typed.
 *
 * Always open. It was a `Disclosure` while it was a table, because eighty rows
 * between the status and the search box is a wall — a shelf is one row tall
 * however many albums are on it, so the thing the collapsing bought is what the
 * shape now buys for free, and a list nobody has to open is a list people read.
 *
 * The covers stay off the network anyway, which was the other reason to keep it
 * shut. `Artwork` sets `loading="lazy"`, so a shelf fetches the tiles near the
 * viewport and no others however long it is.
 */
function Upgrades({
  state,
  onSearch,
}: {
  readonly state: ReturnType<typeof useApiQuery<UpgradeListResponse>>
  readonly onSearch: (item: UpgradeCandidate) => void
}) {
  if (state.status === 'loading') return null

  if (state.status === 'error') {
    return (
      <Stack direction="column" gap={4} align="start">
        <Badge tone="danger">Could not read the library</Badge>
        <Text size="sm" tone="tertiary">
          {state.message}
        </Text>
      </Stack>
    )
  }

  const { files, items } = state.data
  const lossy = items.filter((item) => item.reason === 'Lossy').length
  const cd = items.length - lossy

  if (items.length === 0) {
    return (
      <Text size="sm" tone="tertiary">
        {files === 0
          ? 'The library is empty, so there is nothing to compare.'
          : `Nothing among the ${files.toLocaleString()} files in the library is worth replacing.`}
      </Text>
    )
  }

  return (
    <Shelf
      title="Upgrade candidates"
      count={items.length}
      label="Albums worth replacing, biggest upgrades first"
      detail={
        <>
          {lossy > 0 ? `${lossy} held in a lossy encoding` : 'None held in a lossy encoding'}
          {cd > 0 ? `, ${cd} ripped at CD quality with a hi-res master to buy` : ''}, out of{' '}
          {files.toLocaleString()} files.{' '}
          <strong>Qobuz has not been asked about any of them</strong> — pressing one is what finds
          out whether it sells a better copy, or one at all.
          {cd === 0
            ? ' Run the measure pass to find CD rips: until something has read the depth and rate, only lossy containers can be seen.'
            : ''}
        </>
      }
    >
      {items.map((item) => (
        <UpgradeTile key={item.releaseId ?? item.folder} item={item} onSearch={onSearch} />
      ))}
    </Shelf>
  )
}

/**
 * One album on the upgrade shelf.
 *
 * The whole tile is the button, not a control inside it — the cover is what a
 * person recognises the album by, so it has to be the thing they can press.
 * That also makes each album one tab stop rather than four, which is what a
 * shelf of five hundred needs it to be.
 *
 * The file counts the table used to print are in the accessible name rather
 * than on the face of the tile. They only ever mattered as a pair — "12" reads
 * as a whole album to re-buy where "12 of 31" is one disc of a box set — and a
 * pair of numbers beside a cover is noise, so the label carries both or
 * neither.
 */
function UpgradeTile({
  item,
  onSearch,
}: {
  readonly item: UpgradeCandidate
  readonly onSearch: (item: UpgradeCandidate) => void
}) {
  const reason = REASONS[item.reason as keyof typeof REASONS]

  return (
    <CatalogueCard
      variant="album"
      title={item.title}
      /*
        No cover for a folder no pass attributed: the Cover Art Archive is keyed
        on the MusicBrainz release and there is not one. `Artwork` draws a
        monogram, which is the honest picture of an album nothing has
        identified — 75 of the 520 rows on this library.
      */
      {...(item.releaseId != null
        ? { image: releaseCover(item.releaseId) }
        : item.mbid != null
          ? { image: releaseArt(item.mbid) }
          : {})}
      subtitle={[item.artist, item.year].filter(Boolean).join(' · ') || 'No credited artist'}
      meta={
        <>
          {/*
            A tile with no release was named by a folder, which is the one claim
            this project makes a point of not believing. Saying so is the
            difference between a fact and a guess a person is about to act on —
            and it is a badge rather than a clause on the subtitle because a
            160px tile truncates "Andrea Bocelli · from the folder name" to
            "Andrea Bocelli · from th…", which says nothing at all.
          */}
          {item.releaseId === null ? (
            <Badge tone="neutral" size="sm">
              Folder name
            </Badge>
          ) : null}
          {reason !== undefined ? (
            <Badge tone={reason.tone} size="sm">
              {reason.label}
            </Badge>
          ) : null}
          <span className={styles.metaRow}>
            <Stack gap={4} wrap>
              {item.formats.map((format) => (
                <Text key={format} size="xs" tone="tertiary" family="mono">
                  {format}
                </Text>
              ))}
            </Stack>
          </span>
        </>
      }
      render={(props) => (
        <button {...props} type="button" onClick={() => onSearch(item)}>
          {props.children}
          <VisuallyHidden>
            {' '}
            {item.upgradable === item.files
              ? `${item.files} file${item.files === 1 ? '' : 's'}`
              : `${item.upgradable} of ${item.files} files`}{' '}
            in {item.folder}. Search Qobuz for a better copy.
          </VisuallyHidden>
        </button>
      )}
    />
  )
}

/**
 * Albums the library holds part of.
 *
 * The other half of "worth buying", and a different question from the upgrade
 * list above in every respect: that one is about how a file was encoded and its
 * unit is a folder, this one is about how many of them there are and its unit is
 * a release — only a release carries a track list to be short of.
 *
 * **What makes it worth reading is the subtraction.** A release whose track
 * count exceeds what the library holds is usually not a gap at all: measured on
 * the target library, 128 of the 175 look incomplete only because files sitting
 * in the same folder have not been matched to a track yet, and those are the
 * Identify screen's question rather than something to buy. The endpoint takes
 * them out and says how many it took; without that this list would be four
 * fifths wrong, in the direction that costs money.
 *
 * Nothing here is an upgrade. Downloading fetches the whole album into staging
 * and the person moves in the tracks they were missing — see the note at the
 * call site for why offering a replacement would only ever be refused.
 */
function Incomplete({
  state,
  onSearch,
}: {
  readonly state: ReturnType<typeof useApiQuery<UpgradeListResponse>>
  readonly onSearch: (album: IncompleteAlbum) => void
}) {
  // The error and the loading state are the upgrade panel's, one request behind
  // both lists. Saying it twice on one screen reads as two things being broken.
  if (state.status !== 'ready') return null

  const { incomplete, unmatchedAlbums } = state.data

  if (incomplete.length === 0) {
    return unmatchedAlbums === 0 ? null : (
      <Text size="sm" tone="tertiary">
        Nothing with a track list is short of one. {unmatchedAlbums.toLocaleString()} look short
        only because files in their folders are not matched yet — those are questions for Identify,
        not albums to buy.
      </Text>
    )
  }

  return (
    <Shelf
      title="Incomplete albums"
      count={incomplete.length}
      label="Albums held in part, nearest to complete first"
      detail={
        <>
          Albums whose release prints more tracks than the library holds, nearest to whole first.{' '}
          <strong>Qobuz has not been asked about any of them</strong> — and a download lands the
          whole album in staging, so only the tracks you are missing need moving in.
          {unmatchedAlbums > 0
            ? ` ${unmatchedAlbums.toLocaleString()} more were left off: the files that would fill their gaps are already in the folder, waiting on Identify.`
            : ''}
        </>
      }
    >
      {incomplete.map((album) => (
        <IncompleteTile key={album.releaseId} album={album} onSearch={onSearch} />
      ))}
    </Shelf>
  )
}

/**
 * How a track's position is printed when the album has more than one disc.
 *
 * `2-5` rather than `5`, and only on disc 2 and up: a bare position is honest on
 * a single disc and ambiguous on a set, where two tracks answer to "5". Four of
 * the forty-seven albums on this library are missing something off a second
 * disc. The endpoint has always sent `disc`; nothing was printing it.
 */
function slot(track: MissingTrack): string {
  return track.disc > 1 ? `${track.disc}-${track.position}` : String(track.position)
}

/**
 * One album on the incomplete shelf.
 *
 * **A gap of one names the track; a bigger gap counts them.** Nineteen of the
 * forty-seven albums on this library are missing exactly one, and for those the
 * song is the whole decision where a number is not. The rest of the names — the
 * endpoint sends up to eight — are in the accessible name and on the album's
 * own page.
 */
function IncompleteTile({
  album,
  onSearch,
}: {
  readonly album: IncompleteAlbum
  readonly onSearch: (album: IncompleteAlbum) => void
}) {
  const gap = album.trackCount - album.held
  const only = gap === 1 ? album.missing[0] : undefined

  return (
    <CatalogueCard
      variant="album"
      title={album.title}
      image={releaseCover(album.releaseId)}
      subtitle={[album.artist, album.year].filter(Boolean).join(' · ') || 'No credited artist'}
      meta={
        <>
          {/*
            A bare fraction beside a cover is legible to an eye and not to a
            screen reader, which reads "fourteen slash fifteen" and moves on. The
            unit goes inside the badge rather than into the button's hidden tail
            so that it stays attached to the number.
          */}
          <Badge tone="info" size="sm">
            {album.held}/{album.trackCount}
            <VisuallyHidden> tracks held</VisuallyHidden>
          </Badge>

          <Text size="xs" tone="secondary" truncate block className={styles.metaRow}>
            {only !== undefined
              ? `${slot(only)} · ${only.title ?? 'Untitled'}`
              : `${gap.toLocaleString()} tracks missing`}
          </Text>

          {album.unmatched > 0 ? (
            <Badge tone="warning" size="sm">
              {album.unmatched} unmatched nearby
            </Badge>
          ) : null}

          {/*
            The one gap nobody can buy their way out of. A CD+DVD-Video release
            is missing every video track on a library that holds only the audio,
            and without the format that reads as a bad rip.
          */}
          {album.mediumFormats?.includes('+') ? (
            <Text size="xs" tone="tertiary" family="mono">
              {album.mediumFormats}
            </Text>
          ) : null}
        </>
      }
      render={(props) => (
        <button {...props} type="button" onClick={() => onSearch(album)}>
          {props.children}
          <VisuallyHidden>
            {/*
              Only what the tile does not already say. A gap of one is printed
              in full above, so repeating it here made every such tile read
              "3 I'll Survive. Missing 3 I'll Survive." — the commonest shape on
              the shelf, and the one a reader hears most.
            */}
            {only === undefined ? (
              <>
                {' '}
                Missing{' '}
                {album.missing
                  .map((track) => `${slot(track)} ${track.title ?? 'untitled'}`)
                  .join(', ')}
                {gap > album.missing.length ? ` and ${gap - album.missing.length} more` : ''}.
              </>
            ) : null}{' '}
            Search Qobuz for the whole album.
          </VisuallyHidden>
        </button>
      )}
    />
  )
}

/**
 * Records by followed artists that the library has none of.
 *
 * **The third shelf, and the only one that starts from a person.** The two above
 * it start from the library — this file is lossy, this album is short of its
 * track list — and can only ever offer what is already here in a worse form.
 * Following somebody is the one fact in the catalogue nothing can recompute, so
 * this is the only list on the screen that can name a record the library has
 * never held.
 *
 * **Three states, and telling them apart is most of the job**, which is the
 * lesson `MissingRecords` on the artist page already paid for one artist at a
 * time: nobody follows anybody, they are followed but nothing has browsed them
 * yet, or they were browsed and there are gaps. Collapsing the first two into
 * "nothing missing" reports a complete collection to somebody who has simply not
 * run the pass — and the remedies are opposite, one being a button on another
 * screen and the other being nothing at all.
 *
 * The empty cases are one line of tertiary text rather than a shelf with a
 * heading, so a screen nobody has used the feature on does not grow a section
 * about it.
 */
function Missing({
  state,
  onSearch,
}: {
  readonly state: ReturnType<typeof useApiQuery<UpgradeListResponse>>
  readonly onSearch: (record: MissingRecord) => void
}) {
  // The upgrade panel's, one request behind all three lists — see `Incomplete`.
  if (state.status !== 'ready') return null

  const { missing, followedArtists, unbrowsedArtists, unmonitoredGaps } = state.data

  if (followedArtists === 0) {
    return (
      <Text size="sm" tone="tertiary">
        Follow an artist from the Artists screen to see the records of theirs the library has not
        got. It is the only list here that can name something you have never held.
      </Text>
    )
  }

  if (missing.length === 0) {
    /*
      Three silences, and they want three different sentences. Nothing marked is
      the commonest and the only one a person can act on — it is what a freshly
      followed artist looks like, because the first browse is a baseline that
      monitors nothing. Nothing browsed is a pass somebody has to run. Nothing
      missing is good news. Collapsing any pair of them tells somebody the
      library is complete when it is merely unasked, or sends them to a screen
      with nothing on it.
    */
    if (unmonitoredGaps > 0) {
      return (
        <Text size="sm" tone="tertiary">
          {unmonitoredGaps.toLocaleString()} record{unmonitoredGaps === 1 ? '' : 's'} by artists you
          follow {unmonitoredGaps === 1 ? 'is' : 'are'} not here, and none is marked as wanted. Mark
          the ones you want on the artist's page and they appear on this shelf — records released
          after you followed someone are marked for you.
          {/*
            Said here as well, because the two states overlap and this branch
            wins. A library with some unmarked gaps *and* some unbrowsed artists
            would otherwise only ever hear about the marking, and never learn
            that a pass has not looked at part of the list at all.
          */}
          {unbrowsedArtists > 0
            ? ` ${unbrowsedArtists.toLocaleString()} ${
                unbrowsedArtists === 1 ? 'artist has' : 'artists have'
              } not been browsed yet, so this count is incomplete — run the enrichment pass from Foundation.`
            : ''}
        </Text>
      )
    }

    return (
      <Text size="sm" tone="tertiary">
        {unbrowsedArtists > 0
          ? `Nothing listed yet — run the enrichment pass from Foundation to fetch what ${
              unbrowsedArtists === followedArtists
                ? 'they'
                : `${unbrowsedArtists.toLocaleString()} of them`
            } released.`
          : `Nothing missing from the ${followedArtists.toLocaleString()} artist${
              followedArtists === 1 ? '' : 's'
            } you follow.`}
      </Text>
    )
  }

  return (
    <Shelf
      title="Not in your library"
      count={missing.length}
      label="Records by artists you follow that the library has none of, by artist"
      detail={
        <>
          Records by the {followedArtists.toLocaleString()} artist
          {followedArtists === 1 ? '' : 's'} you follow that no file here sits under, and that you
          have marked as wanted. <strong>Qobuz has not been asked about any of them</strong> —
          pressing one is what finds out whether it sells a copy.
          {unmonitoredGaps > 0
            ? ` ${unmonitoredGaps.toLocaleString()} more ${
                unmonitoredGaps === 1 ? 'record is' : 'records are'
              } missing but unmarked; mark them on the artist's page.`
            : ''}
          {unbrowsedArtists > 0
            ? ` ${unbrowsedArtists.toLocaleString()} ${
                unbrowsedArtists === 1 ? 'artist has' : 'artists have'
              } not been browsed yet — run the enrichment pass from Foundation.`
            : ''}
        </>
      }
    >
      {missing.map((record) => (
        <MissingTile key={record.releaseGroupId} record={record} onSearch={onSearch} />
      ))}
    </Shelf>
  )
}

/**
 * One record they made that the library has not got.
 *
 * The same card as the artist page's `MissingCard` with one difference that
 * matters: it renders a real `<button>`. There it goes nowhere, because there is
 * no page for a record no file is under and inventing one would be inventing the
 * album — here the press is a Qobuz search, which is the whole point of the
 * screen. `Shelf` finds its tiles with `querySelectorAll('button')`, so a card
 * without one would also be invisible to the roving tab stop.
 *
 * The sleeve comes from the Cover Art Archive keyed on the *release group*,
 * which is the only key there is: no pressing has been chosen, because none has
 * been owned.
 */
function MissingTile({
  record,
  onSearch,
}: {
  readonly record: MissingRecord
  readonly onSearch: (record: MissingRecord) => void
}) {
  const image = record.mbid != null ? releaseGroupArt(record.mbid) : null

  return (
    <CatalogueCard
      variant="album"
      title={record.title}
      {...(image != null ? { image } : {})}
      subtitle={[record.artist, record.year].filter(Boolean).join(' · ')}
      meta={
        <span className={styles.metaRow}>
          <Stack gap={4} wrap>
            {/*
              An untyped group is one `Discography.IsGap` lets through rather
              than one it recognised, and on an obscure artist that is most of
              them — so the tile says which it is instead of printing nothing.
            */}
            <Badge tone="neutral" size="sm">
              {record.primaryType ?? 'Untyped'}
            </Badge>

            {record.secondaryTypes.map((type) => (
              <Badge key={type} tone="neutral" size="sm">
                {type}
              </Badge>
            ))}
          </Stack>
        </span>
      }
      render={(props) => (
        <button {...props} type="button" onClick={() => onSearch(record)}>
          {props.children}
          <VisuallyHidden> Search Qobuz for this record.</VisuallyHidden>
        </button>
      )}
    />
  )
}

/**
 * What this instance can currently do.
 *
 * Above the search box on purpose. The failure worth catching here is the one
 * that looks like success — an app id and a token with no app secret searches
 * perfectly and cannot sign a single download — and finding that out after
 * choosing an album is the wrong order to find it out in.
 */
function Status({
  state,
}: {
  readonly state: ReturnType<typeof useApiQuery<components['schemas']['QobuzStatusResponse']>>
}) {
  if (state.status === 'loading') return null

  if (state.status === 'error') {
    return (
      <div className={styles.status}>
        <Stack direction="column" gap={4} align="start">
          <Badge tone="danger">Could not read the Qobuz settings</Badge>
          <Text size="sm" tone="tertiary">
            {state.message}
          </Text>
        </Stack>
      </div>
    )
  }

  const ready = readiness(state.data)

  return (
    <div className={styles.status}>
      <Stack direction="column" gap={8} align="start">
        <Stack gap={8} align="center" wrap>
          <Badge
            tone={
              ready.kind === 'ready'
                ? 'success'
                : ready.kind === 'browse-only'
                  ? 'warning'
                  : 'danger'
            }
          >
            {ready.kind === 'ready'
              ? 'Ready'
              : ready.kind === 'browse-only'
                ? 'Search only'
                : 'Not configured'}
          </Badge>

          <Text size="sm" tone="secondary">
            {ready.detail}
          </Text>

          {state.data.busy ? (
            <Badge tone="info" size="sm">
              A download is running
            </Badge>
          ) : null}
        </Stack>

        {ready.kind !== 'ready' ? (
          <Stack direction="column" gap={2} align="start">
            <Text size="xs" tone="tertiary">
              Set {ready.settings.length === 1 ? 'this' : 'these'} and restart the API:
            </Text>
            {ready.settings.map((setting) => (
              <Text key={setting} size="xs" family="mono" tone="tertiary">
                {setting}
              </Text>
            ))}
          </Stack>
        ) : null}
      </Stack>
    </div>
  )
}

function AlbumCard({
  album,
  onOpen,
}: {
  readonly album: QobuzAlbumSummary
  readonly onOpen: () => void
}) {
  return (
    <CatalogueCard
      variant="album"
      title={album.title}
      {...(album.coverUrl != null ? { image: album.coverUrl } : {})}
      subtitle={album.artist ?? 'No credited artist'}
      meta={
        <>
          <Text size="xs" tone="tertiary" family="mono">
            {[
              album.releaseDate?.slice(0, 4),
              `${album.trackCount} track${album.trackCount === 1 ? '' : 's'}`,
            ]
              .filter(Boolean)
              .join(' · ')}
          </Text>

          {album.hiRes ? (
            <Badge tone="accent" size="sm">
              Hi-res
            </Badge>
          ) : null}

          {/*
            Not streamable means the subscription or the region does not cover
            it, and it is worth saying on the card: the alternative is choosing
            an album, reading its track list and being refused at the end.
          */}
          {album.streamable ? null : (
            <Badge tone="warning" size="sm">
              Unavailable
            </Badge>
          )}
        </>
      }
      render={(props) => (
        <button {...props} type="button" onClick={onOpen}>
          {props.children}
        </button>
      )}
    />
  )
}
