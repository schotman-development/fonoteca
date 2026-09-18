/**
 * Identify: one album folder at a time.
 *
 * The folder, a search prefilled from its tags, and the search results one
 * release at a time — each with its facts against the tags, its track list
 * against the files, and what would go wrong if it were filed. Skip moves on and
 * records nothing; "Not a release" and File are the two answers.
 *
 * The seating is worked out in `identify.ts` and committed through
 * `POST …/files/release` exactly as shown. A file is filed when its title matches
 * the track it sits on, even in part, or when somebody picked the track; the rest
 * stay open.
 */

import { type components, describeError } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  Button,
  Dialog,
  Field,
  Input,
  Stack,
  Text,
  usePlayback,
  VisuallyHidden,
} from '@fonoteca/ui'
import { useNavigate, useSearch } from '@tanstack/react-router'
import { type ReactNode, useEffect, useId, useMemo, useRef, useState } from 'react'

import { api, apiBaseUrl } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import { releaseArt } from './coverArt.ts'
import { artUrl, contentUrl } from './files.ts'
import styles from './IdentifyPage.module.css'
import {
  clock,
  factsOf,
  filed,
  interleave,
  label,
  noticesOf,
  type Picks,
  pairsOf,
  type Seating,
  searchFor,
  seat,
  slotKey,
  worstDrift,
} from './identify.ts'
import { MatchingDialog, type MatchingSubjectRef } from './MatchingDialog.tsx'
import { ALBUM_FOLDER_DEPTH } from './seating.ts'

type FolderResponse = components['schemas']['IdentifyFolderResponse']
type FileRow = components['schemas']['IdentifyFileRow']
type SearchRow = components['schemas']['ReleaseSearchRow']
type Slot = components['schemas']['ReleaseSlotRow']
type SlotsResponse = components['schemas']['ReleaseSlotsResponse']
type FilingResponse = components['schemas']['AlbumFilingResponse']
type SeedResponse = components['schemas']['ReleaseSeedResponse']

export function IdentifyPage() {
  const [version, setVersion] = useState(0)
  const [index, setIndex] = useState(0)

  /**
   * The folder just answered, and whether its queue reload has started. The
   * index is only adjusted against the *reloaded* queue: read against the old
   * one, the answered folder is still there and every later folder shifts down
   * by one once it is gone, so Next would skip a folder.
   */
  const answered = useRef<{ folder: string; reloading: boolean } | null>(null)

  /** Whether the question on screen was reached by Skip or Next, and should take focus. */
  const [moved, setMoved] = useState(false)

  const queue = useApiQuery(() => api.get('/api/catalogue/matching/folders/queue'), [version])

  /*
    Coming back from MusicBrainz's release editor with the release just
    entered. Jump to that folder and search for the MBID, which is a lookup
    rather than a text search, so it works against a mirror and finds a
    release added seconds ago.
  */
  const returned = useSearch({ from: '/library/matching' })
  const [handled, setHandled] = useState<string | null>(null)
  const [lookup, setLookup] = useState<{ folder: string; query: string } | null>(null)

  useEffect(() => {
    if (queue.status !== 'ready') return
    if (returned.release_mbid === undefined || returned.release_mbid === handled) return

    setHandled(returned.release_mbid)

    const at = queue.data.items.findIndex((item) => item.folder === returned.folder)
    if (at < 0) return

    setIndex(at)
    setLookup({ folder: returned.folder ?? '', query: returned.release_mbid })
  }, [queue, returned, handled])

  useEffect(() => {
    const pending = answered.current
    if (pending === null) return

    if (queue.status === 'loading') {
      pending.reloading = true
      return
    }

    if (queue.status !== 'ready' || !pending.reloading) return

    answered.current = null

    // Gone: the same index is now the next folder. Still there, because part of
    // it stayed open: move past it.
    const items = queue.data.items
    const at = items.findIndex((item) => item.folder === pending.folder)
    if (at >= 0 && items.length > 1) setIndex((at + 1) % items.length)
  }, [queue])

  const heading = (
    <Stack direction="column" gap={4}>
      <h1 className={styles.title}>
        <Text size="xl" weight="semibold" block>
          Identify
        </Text>
      </h1>
    </Stack>
  )

  if (queue.status !== 'ready') {
    return (
      <div className={styles.page}>
        {heading}
        <div role="status" aria-busy={queue.status === 'loading'}>
          {queue.status === 'loading' ? (
            <Text tone="tertiary">Reading the folders and their tags…</Text>
          ) : (
            <Stack direction="column" gap={4} align="start">
              <Badge tone="danger">Could not read the folders</Badge>
              <Text size="sm" tone="tertiary">
                {queue.message}
              </Text>
            </Stack>
          )}
        </div>
      </div>
    )
  }

  const items = queue.data.items

  if (items.length === 0) {
    return (
      <div className={styles.page}>
        {heading}
        <Text tone="tertiary" block>
          Nothing is waiting. Every folder the passes reached has been answered.
        </Text>
      </div>
    )
  }

  const position = index % items.length
  const current = items[position]

  if (current === undefined) return null

  return (
    <div className={styles.page}>
      {heading}
      <FolderQuestion
        key={`${current.folder}:${version}`}
        folder={current.folder}
        position={position + 1}
        total={items.length}
        {...(lookup?.folder === current.folder ? { initialQuery: lookup.query } : {})}
        focus={moved}
        onSkip={() => {
          setMoved(true)
          setIndex((position + 1) % items.length)
        }}
        onDone={() => {
          answered.current = { folder: current.folder, reloading: false }
          setMoved(true)
          setLookup(null)
          setVersion((value) => value + 1)
        }}
      />
    </div>
  )
}

type Outcome =
  | {
      readonly kind: 'filed'
      readonly result: FilingResponse
      readonly seating: Seating
      readonly release: SearchRow
    }
  | { readonly kind: 'unreleased'; readonly detail: string }

function FolderQuestion({
  folder,
  position,
  total,
  initialQuery,
  focus,
  onSkip,
  onDone,
}: {
  readonly folder: string
  readonly position: number
  readonly total: number
  readonly initialQuery?: string
  /** Take keyboard focus on arrival, so Skip does not drop it to the page. */
  readonly focus: boolean
  readonly onSkip: () => void
  readonly onDone: () => void
}) {
  const counter = useRef<HTMLSpanElement>(null)

  // biome-ignore lint/correctness/useExhaustiveDependencies: once per folder, on arrival
  useEffect(() => {
    if (focus) counter.current?.focus()
  }, [])

  return (
    <FolderIdentify
      folder={folder}
      {...(initialQuery !== undefined ? { initialQuery } : {})}
      heading={
        <Text ref={counter} tabIndex={-1} size="sm" tone="secondary">
          Folder {position} of {total}
        </Text>
      }
      next="Next folder"
      onSkip={onSkip}
      onDone={onDone}
    />
  )
}

/**
 * One album folder's question, outside the queue: the Identify screen wraps it
 * with a counter and Skip, the Files screen shows it for the folder being browsed.
 */
export function FolderIdentify({
  folder,
  initialQuery,
  heading,
  next,
  onSkip,
  onDone,
  onChanged,
}: {
  readonly folder: string
  readonly initialQuery?: string
  readonly heading?: ReactNode
  /** The label of the button that leaves a result. */
  readonly next: string
  readonly onSkip?: () => void
  readonly onDone: () => void
  /** Anything in the catalogue changed: a filing, a mark or a reopen. */
  readonly onChanged?: () => void
}) {
  const [reads, setReads] = useState(0)
  const detail = useApiQuery(
    () => api.get('/api/catalogue/matching/folders/identify', { params: { query: { folder } } }),
    [folder, reads],
  )

  const [outcome, setOutcome] = useState<Outcome | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const reread = () => {
    setReads((value) => value + 1)
    onChanged?.()
  }

  return (
    <Stack direction="column" gap={16}>
      <Stack direction="column" gap={2} align="start">
        {heading}
        <div role="status">
          {notice !== null ? (
            <Text size="xs" tone="tertiary">
              {notice}
            </Text>
          ) : null}
        </div>
      </Stack>

      {detail.status === 'loading' ? (
        <div role="status" aria-busy>
          <Text tone="tertiary">Reading the folder’s tags…</Text>
        </div>
      ) : null}

      {detail.status === 'error' ? (
        <Stack direction="column" gap={8} align="start">
          <Badge tone="danger">Could not read this folder</Badge>
          <Text size="sm" tone="tertiary">
            {detail.message}
          </Text>
          {onSkip !== undefined ? (
            <Button variant="secondary" onClick={onSkip}>
              Skip
            </Button>
          ) : null}
        </Stack>
      ) : null}

      {detail.status === 'ready' ? (
        <>
          <section className={styles.panel} aria-label="Folder">
            <FolderHeader data={detail.data} />
          </section>

          {outcome !== null ? (
            <Result outcome={outcome} next={next} onNext={onDone} />
          ) : detail.data.open === 0 ? (
            <Settled
              data={detail.data}
              {...(onSkip !== undefined ? { onSkip } : {})}
              onNotice={setNotice}
              onReread={reread}
            />
          ) : (
            <Chooser
              data={detail.data}
              {...(initialQuery !== undefined ? { initialQuery } : {})}
              {...(onSkip !== undefined ? { onSkip } : {})}
              onOutcome={(value) => {
                setOutcome(value)
                onChanged?.()
              }}
              onNotice={setNotice}
              onReread={reread}
            />
          )}
        </>
      ) : null}
    </Stack>
  )
}

/** A folder with nothing open: the only thing left to say about it is that it is wrong. */
function Settled({
  data,
  onSkip,
  onNotice,
  onReread,
}: {
  readonly data: FolderResponse
  readonly onSkip?: () => void
  readonly onNotice: (notice: string) => void
  readonly onReread: () => void
}) {
  const [sending, setSending] = useState(false)
  const markable = data.folder.split('/').length >= ALBUM_FOLDER_DEPTH

  return (
    <Stack direction="column" gap={8} align="start" className={styles.panel}>
      <Text size="sm" block>
        Nothing here is waiting on you. If the album or the tracks are wrong, take the answer back
        and the folder becomes one question again.
      </Text>
      <Button
        variant="secondary"
        disabled={!markable || sending}
        onClick={() => {
          setSending(true)
          void reopenFolder(data.folder, onNotice, onReread).finally(() => {
            setSending(false)
          })
        }}
      >
        {sending ? 'Reopening…' : 'Wrong match — ask again'}
      </Button>
      {onSkip !== undefined ? (
        <Button variant="ghost" onClick={onSkip} disabled={sending}>
          Skip
        </Button>
      ) : null}
      {!markable ? (
        <Text size="xs" tone="tertiary" block>
          Only an Artist/Album folder can be reopened.
        </Text>
      ) : null}
    </Stack>
  )
}

/** Confirms, then takes back what was filed under the folder. */
async function reopenFolder(
  folder: string,
  onNotice: (notice: string) => void,
  onReread: () => void,
): Promise<void> {
  if (
    !window.confirm(
      `Ask “${folder}” again from scratch?\n\n` +
        'Every file in it that Fonoteca matched gives up its recording, album and track, and ' +
        'the whole folder comes back here as one question. No pass will match them again. ' +
        'Nothing on disk is touched.',
    )
  ) {
    return
  }

  try {
    const result = await api.post('/api/catalogue/matching/folders/reopen', { json: { folder } })
    onNotice(result.detail)
    onReread()
  } catch (cause: unknown) {
    onNotice(`Nothing was reopened. ${describeError(cause)}`)
  }
}

function FolderHeader({ data }: { readonly data: FolderResponse }) {
  const { tags } = data
  const length = data.items.reduce((sum, item) => sum + (item.lengthMs ?? 0), 0)

  return (
    <Stack gap={16} align="start">
      <Artwork name={tags.album ?? data.folder} src={artUrl(apiBaseUrl, data.folder)} size="lg" />
      <Stack direction="column" gap={4} align="start" className={styles.grow}>
        <h2 className={styles.heading}>
          <Text size="md" weight="semibold" family="mono" block>
            {data.folder}
          </Text>
        </h2>
        <Text size="sm" tone="secondary" block>
          {data.files} file{data.files === 1 ? '' : 's'} · {data.open} open
          {length > 0 ? ` · ${clock(length)}` : ''}
          {data.audio !== '' ? ` · ${data.audio}` : ''}
        </Text>
        {/* Only open files' tags are read, so a settled folder has nothing to say here. */}
        {data.open === 0 ? null : tags.album !== null || tags.artist !== null ? (
          <Text size="sm" block>
            Tags say: <strong>{tags.artist ?? 'no artist'}</strong> —{' '}
            <strong>{tags.album ?? 'no album'}</strong>
            {tags.year !== null ? ` (${tags.year})` : ''}
            {tags.label !== null ? ` · ${tags.label}` : ''}
          </Text>
        ) : (
          <Text size="sm" tone="secondary" block>
            The files carry no album or artist tags.
          </Text>
        )}
        {tags.release !== null ? (
          <Text size="xs" tone="tertiary" block>
            {tags.agreeing} of {data.open} open files name the same MusicBrainz release
          </Text>
        ) : null}
      </Stack>
    </Stack>
  )
}

function Chooser({
  data,
  initialQuery,
  onSkip,
  onOutcome,
  onNotice,
  onReread,
}: {
  readonly data: FolderResponse
  readonly initialQuery?: string
  readonly onSkip?: () => void
  readonly onOutcome: (outcome: Outcome) => void
  readonly onNotice: (notice: string) => void
  readonly onReread: () => void
}) {
  const [query, setQuery] = useState(() => initialQuery ?? searchFor(data.tags, data.folder))
  const [asked, setAsked] = useState(query)
  // By id, not by index: the tag-named release can arrive after the text search
  // and shift every option down one, which must not swap the card being read.
  const [chosen, setChosen] = useState<string | null>(null)

  const results = useApiQuery(
    () => api.get('/api/catalogue/matching/releases/search', { params: { query: { q: asked } } }),
    [asked],
  )

  // The release the tags name, looked up by id and put first. A text search for
  // the tags' own album title does not always return it — a live release
  // titled with its venue, say.
  const named = data.tags.release
  const tagged = useApiQuery(
    () =>
      named === null
        ? Promise.resolve(null)
        : api.get('/api/catalogue/matching/releases/search', { params: { query: { q: named } } }),
    [named],
  )

  const options = useMemo(() => {
    const found = results.status === 'ready' ? results.data.items : []
    const first = tagged.status === 'ready' && tagged.data !== null ? tagged.data.items : []

    const byId = new Map<string, SearchRow>()
    for (const row of [...first, ...found]) if (!byId.has(row.mbid)) byId.set(row.mbid, row)

    return [...byId.values()]
  }, [results, tagged])

  const current = Math.max(
    0,
    options.findIndex((row) => row.mbid === chosen),
  )
  const selected = options[current] ?? null
  const step = (by: number) => {
    const next = options[(current + by + options.length) % options.length]
    if (next !== undefined) setChosen(next.mbid)
  }

  const slots = useApiQuery(
    () =>
      selected === null
        ? Promise.resolve(null)
        : api.get('/api/catalogue/matching/releases/{id}/slots', {
            params: { path: { id: selected.mbid }, query: { folder: data.folder } },
          }),
    [selected?.mbid, data.folder],
  )

  // Tracks picked by hand belong to the release they were picked on.
  const [picked, setPicked] = useState<{ readonly mbid: string; readonly picks: Picks } | null>(
    null,
  )
  const picks: Picks = picked !== null && picked.mbid === selected?.mbid ? picked.picks : new Map()
  const pick = (file: string, slot: string | null) => {
    if (selected === null) return
    // One file per track: whoever picked this track before gives it up.
    const next = new Map([...picks].filter(([, each]) => slot === null || each !== slot))
    next.set(file, slot)
    setPicked({ mbid: selected.mbid, picks: next })
  }

  const seating =
    slots.status === 'ready' && slots.data !== null && slots.data.mbid === selected?.mbid
      ? seat(data.items, slots.data.slots, picks)
      : null
  const pairs = seating === null ? [] : pairsOf(seating)

  const [sending, setSending] = useState<'idle' | 'filing' | 'unreleasing'>('idle')
  const [failure, setFailure] = useState<string | null>(null)

  const markable = data.folder.split('/').length >= ALBUM_FOLDER_DEPTH

  async function file() {
    if (selected === null || seating === null || pairs.length === 0) return

    setSending('filing')
    setFailure(null)

    try {
      const result = await api.post('/api/catalogue/matching/files/release', {
        json: { release: selected.mbid, pairs: [...pairs] },
      })
      onOutcome({ kind: 'filed', result, seating, release: selected })
    } catch (cause: unknown) {
      setFailure(`Nothing was filed. ${describeError(cause)}`)
      setSending('idle')
    }
  }

  async function unreleased() {
    if (
      !window.confirm(
        `Mark “${data.folder}” as coming from no release?\n\n` +
          `The ${data.open} open file${data.open === 1 ? '' : 's'} here stop being asked about, ` +
          'here and by every pass. Files already matched are left as they are, and nothing on ' +
          'disk is touched.',
      )
    ) {
      return
    }

    setSending('unreleasing')
    setFailure(null)

    try {
      const result = await api.post('/api/catalogue/matching/folders/unreleased', {
        json: { folder: data.folder },
      })
      onOutcome({ kind: 'unreleased', detail: result.detail })
    } catch (cause: unknown) {
      setFailure(`Nothing was marked. ${describeError(cause)}`)
      setSending('idle')
    }
  }

  return (
    <>
      <Stack direction="column" gap={4} align="stretch">
        <form
          className={styles.search}
          onSubmit={(event) => {
            event.preventDefault()
            if (query.trim() === '') return
            setAsked(query.trim())
            setChosen(null)
          }}
        >
          <Field
            label="Search MusicBrainz for the album"
            hint="Filled in from your files’ tags. A release MBID or URL pasted here is looked up directly."
          >
            {(control) => (
              <Input
                {...control}
                fullWidth
                value={query}
                onChange={(event) => {
                  setQuery(event.currentTarget.value)
                }}
              />
            )}
          </Field>
          <Button type="submit" variant="secondary" disabled={query.trim() === ''}>
            Search
          </Button>
        </form>

        <div role="status" aria-busy={results.status === 'loading'}>
          {results.status === 'loading' ? (
            <Text size="xs" tone="tertiary">
              Asking MusicBrainz…
            </Text>
          ) : null}
          {results.status === 'error' ? (
            <Stack direction="column" gap={2} align="start">
              <Text size="xs" tone="danger">
                MusicBrainz could not search: {results.message}
              </Text>
              <Text size="xs" tone="tertiary">
                A self-hosted mirror has no search index. Pasting a release MBID or URL works either
                way.
              </Text>
            </Stack>
          ) : null}
          {results.status === 'ready' ? (
            <Text size="xs" tone="tertiary">
              {options.length === 0
                ? `Nothing matched “${asked}”. Try just the artist and the album title.`
                : `${options.length} album${options.length === 1 ? '' : 's'} for “${asked}”`}
            </Text>
          ) : null}
        </div>
      </Stack>

      {selected !== null && tagged.status !== 'loading' ? (
        <section
          className={styles.carousel}
          aria-roledescription="carousel"
          aria-label="Albums found"
        >
          <Stack justify="between" align="center" gap={12} wrap>
            <Text size="sm" weight="medium">
              Which album is this?
            </Text>
            <Stack gap={8} align="center">
              <Button
                variant="secondary"
                size="sm"
                disabled={options.length < 2}
                onClick={() => {
                  step(-1)
                }}
              >
                ← Previous
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={options.length < 2}
                onClick={() => {
                  step(1)
                }}
              >
                Next →
              </Button>
            </Stack>
          </Stack>

          <ReleaseCard
            key={selected.mbid}
            data={data}
            release={selected}
            position={`${current + 1} of ${options.length}`}
            slots={slots.status === 'ready' ? slots.data : null}
            slotsError={slots.status === 'error' ? slots.message : null}
            seating={seating}
            picks={picks}
            onPick={pick}
          />
        </section>
      ) : null}

      <div className={styles.stickyBar}>
        {failure !== null ? (
          <Text size="sm" tone="danger" block>
            {failure}
          </Text>
        ) : null}
        <Stack gap={8} align="center" justify="end" wrap>
          {onSkip !== undefined ? (
            <Button variant="ghost" onClick={onSkip} disabled={sending !== 'idle'}>
              Skip
            </Button>
          ) : null}
          <Button
            variant="secondary"
            disabled={!markable || sending !== 'idle'}
            onClick={() => {
              void unreleased()
            }}
          >
            {sending === 'unreleasing' ? 'Marking…' : 'Not a release'}
          </Button>
          <MoreMenu data={data} markable={markable} onNotice={onNotice} onReread={onReread} />
          <Button
            variant="primary"
            disabled={pairs.length === 0 || sending !== 'idle'}
            onClick={() => {
              void file()
            }}
          >
            {sending === 'filing'
              ? 'Filing…'
              : `File ${pairs.length} file${pairs.length === 1 ? '' : 's'} under this album`}
          </Button>
        </Stack>
      </div>
    </>
  )
}

function ReleaseCard({
  data,
  release,
  position,
  slots,
  slotsError,
  seating,
  picks,
  onPick,
}: {
  readonly data: FolderResponse
  readonly release: SearchRow
  readonly position: string
  readonly slots: SlotsResponse | null
  readonly slotsError: string | null
  readonly seating: Seating | null
  readonly picks: Picks
  readonly onPick: (file: string, slot: string | null) => void
}) {
  const namedByTags = data.tags.release === release.mbid
  const matched = seating === null ? 0 : pairsOf(seating).length

  const facts = [
    release.year?.toString(),
    release.country,
    release.formats,
    [release.primaryType, ...release.secondaryTypes].filter(Boolean).join(' · '),
    release.status,
  ].filter((part) => part != null && part !== '')

  return (
    <article
      className={styles.option}
      aria-roledescription="slide"
      aria-label={`${position}: ${release.title}`}
    >
      <Stack gap={16} align="start">
        <Artwork name={release.title} src={releaseArt(release.mbid)} size="lg" />
        <Stack direction="column" gap={6} align="start" className={styles.grow}>
          <h3 className={styles.heading}>
            <Text size="md" weight="semibold">
              {release.title}
            </Text>
            {release.artist !== null ? (
              <Text size="sm" tone="secondary">
                {' '}
                — {release.artist}
              </Text>
            ) : null}
          </h3>
          <Stack direction="column" gap={2} align="start">
            <Text size="xs" tone="secondary" family="mono" block>
              {facts.join(' · ')}
            </Text>
            <Text size="xs" tone="secondary" family="mono" block>
              {release.trackCount} tracks · {release.discCount} disc
              {release.discCount === 1 ? '' : 's'} · label{' '}
              {slots === null ? '…' : (slots.label ?? 'not listed')} · cat. no.{' '}
              {slots?.catalogNumber ?? '—'} · barcode {slots?.barcode ?? '—'}
            </Text>
          </Stack>
          <Stack gap={6} wrap>
            {namedByTags ? (
              <Badge tone="success" size="sm">
                Your files’ tags name this release
              </Badge>
            ) : null}
            {seating !== null ? (
              <>
                <Badge tone={matched === data.open ? 'success' : 'warning'} size="sm" mono>
                  {matched} of {data.open} files match a track
                </Badge>
                <Badge tone={worstDrift(seating) > 8 ? 'warning' : 'neutral'} size="sm" mono>
                  worst drift {worstDrift(seating)}s
                </Badge>
              </>
            ) : null}
          </Stack>
        </Stack>
      </Stack>

      {slotsError !== null ? (
        <Text size="sm" tone="danger" block>
          Could not read this album’s track list: {slotsError}
        </Text>
      ) : null}

      {slots === null && slotsError === null ? (
        <div role="status" aria-busy>
          <Text size="sm" tone="tertiary">
            Reading the track list…
          </Text>
        </div>
      ) : null}

      {slots !== null && seating !== null ? (
        <>
          <Snackbars key={release.mbid} seating={seating} discs={release.discCount} picks={picks} />
          <FactsTable data={data} release={release} slots={slots} />
          <TrackTable
            data={data}
            release={release}
            seating={seating}
            picks={picks}
            onPick={onPick}
          />
        </>
      ) : null}
    </article>
  )
}

function Snackbars({
  seating,
  discs,
  picks,
}: {
  readonly seating: Seating
  readonly discs: number
  readonly picks: Picks
}) {
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(() => new Set())
  const shown = noticesOf(seating, discs, picks).filter((notice) => !dismissed.has(notice.key))

  return (
    <ul className={styles.snackbars} aria-live="polite" aria-label="Notices about this album">
      {shown.map((notice) => (
        <li key={notice.key} className={styles.snackbar} data-tone={notice.tone}>
          <Text size="sm" className={styles.grow}>
            {notice.file !== undefined ? (
              <>
                <strong className={styles.mono}>{notice.file}</strong>{' '}
              </>
            ) : null}
            {notice.text}
          </Text>
          <Button
            variant="ghost"
            size="sm"
            aria-label={`Dismiss: ${notice.file === undefined ? '' : `${notice.file} `}${notice.text}`}
            onClick={() => {
              setDismissed(new Set([...dismissed, notice.key]))
            }}
          >
            ✕
          </Button>
        </li>
      ))}
    </ul>
  )
}

function FactsTable({
  data,
  release,
  slots,
}: {
  readonly data: FolderResponse
  readonly release: SearchRow
  readonly slots: SlotsResponse
}) {
  const facts = factsOf(data.tags, data.files, {
    title: release.title,
    artist: release.artist,
    year: release.year,
    discCount: slots.discCount,
    label: slots.label,
    tracks: slots.slots.length,
  })

  return (
    <table className={styles.table}>
      <caption>
        <VisuallyHidden>Your tags against {release.title}</VisuallyHidden>
      </caption>
      <thead>
        <tr>
          <th scope="col">
            <VisuallyHidden>Fact</VisuallyHidden>
          </th>
          <th scope="col">Your tags</th>
          <th scope="col">This release</th>
          <th scope="col">
            <VisuallyHidden>Difference</VisuallyHidden>
          </th>
        </tr>
      </thead>
      <tbody>
        {facts.map((fact) => (
          <tr key={fact.label} data-differs={fact.state === 'differs' || undefined}>
            <th scope="row">{fact.label}</th>
            <td>{fact.tags}</td>
            <td>{fact.release}</td>
            <td>
              <Badge
                tone={
                  fact.state === 'differs'
                    ? 'warning'
                    : fact.state === 'same'
                      ? 'success'
                      : 'neutral'
                }
                size="sm"
              >
                {fact.state}
              </Badge>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function TrackTable({
  data,
  release,
  seating,
  picks,
  onPick,
}: {
  readonly data: FolderResponse
  readonly release: SearchRow
  readonly seating: Seating
  readonly picks: Picks
  readonly onPick: (file: string, slot: string | null) => void
}) {
  const discs = release.discCount
  // A pick moves the row, which remounts its select: focus follows the file.
  const lastPicked = useRef<string | null>(null)

  // Offered wherever a rule did not settle the row on its own, and kept once used.
  const picker = (file: FileRow, seatedOn: string | null) => (
    <TrackPicker
      file={file}
      value={seatedOn}
      slots={seating.rows.map((row) => row.slot)}
      discs={discs}
      focus={lastPicked.current === file.mediaFileId}
      onPick={(id, slot) => {
        lastPicked.current = id
        onPick(id, slot)
      }}
    />
  )

  return (
    <table className={styles.table}>
      <caption>
        <VisuallyHidden>Your files seated on {release.title}</VisuallyHidden>
      </caption>
      <thead>
        <tr>
          <th scope="col">#</th>
          <th scope="col">On the release</th>
          <th scope="col">Your file</th>
          <th scope="col">Length</th>
          <th scope="col">Difference</th>
        </tr>
      </thead>
      <tbody>
        {interleave(seating, data.items).map((line) =>
          line.kind === 'unseated' ? (
            <tr key={`open:${line.file.mediaFileId}`} data-kind="unseated">
              <td className={styles.mono}>—</td>
              <td>
                <strong>
                  {picks.get(line.file.mediaFileId) === null
                    ? 'Left open by you'
                    : 'Not on this release'}
                </strong>
              </td>
              <td className={styles.mono}>
                {line.file.name}
                {picker(line.file, null)}
              </td>
              <td className={styles.mono}>{line.file.length ?? '—'}</td>
              <td>
                <Badge tone="danger" size="sm">
                  stays open
                </Badge>
              </td>
            </tr>
          ) : (
            <tr
              key={`${line.slot.discNumber}-${line.slot.position}`}
              data-kind={line.kind}
              data-differs={(line.kind === 'seated' && !filed(line)) || undefined}
            >
              <td className={styles.mono}>{label(line.slot, discs).replace('#', '')}</td>
              <td>{line.slot.title}</td>
              <td className={styles.mono}>
                {line.kind === 'seated' ? (
                  <>
                    {line.file.name}
                    {line.chosen || line.title !== 'same'
                      ? picker(line.file, filed(line) ? slotKey(line.slot) : null)
                      : null}
                  </>
                ) : line.kind === 'held' ? (
                  `already filed: ${line.slot.heldBy}`
                ) : (
                  '—'
                )}
              </td>
              <td className={styles.mono}>
                {line.kind === 'seated'
                  ? `${line.file.length ?? '—'} / ${line.slot.duration ?? '—'}`
                  : (line.slot.duration ?? '—')}
              </td>
              <td>
                <Stack gap={4} wrap>
                  {line.kind === 'empty' ? (
                    <Badge tone="neutral" size="sm">
                      no file
                    </Badge>
                  ) : null}
                  {line.kind === 'held' ? (
                    <Badge tone="neutral" size="sm">
                      filed
                    </Badge>
                  ) : null}
                  {line.kind === 'seated' ? (
                    <>
                      {line.chosen ? (
                        <Badge tone="neutral" size="sm">
                          picked by you
                        </Badge>
                      ) : line.title === 'differs' ? (
                        <Badge tone="danger" size="sm">
                          title differs
                        </Badge>
                      ) : line.title === 'partial' ? (
                        <Badge tone="warning" size="sm">
                          title partly matches
                        </Badge>
                      ) : null}
                      {line.numberDiffers && line.file.track != null ? (
                        <Badge tone="warning" size="sm" mono>
                          tag says{' '}
                          {discs > 1
                            ? `${line.file.disc ?? 1}-${line.file.track}`
                            : `#${line.file.track}`}
                        </Badge>
                      ) : null}
                      {line.drift !== null ? <Drift seconds={line.drift} /> : null}
                    </>
                  ) : null}
                </Stack>
              </td>
            </tr>
          ),
        )}
      </tbody>
    </table>
  )
}

function TrackPicker({
  file,
  value,
  slots,
  discs,
  focus,
  onPick,
}: {
  readonly file: FileRow
  /** The track this file will be filed on, or null when it will not be. */
  readonly value: string | null
  readonly slots: readonly Slot[]
  readonly discs: number
  readonly focus: boolean
  readonly onPick: (file: string, slot: string | null) => void
}) {
  return (
    <select
      ref={(element) => {
        if (focus && element !== null && document.activeElement !== element) element.focus()
      }}
      className={styles.picker}
      aria-label={`Track for ${file.name}`}
      value={value ?? ''}
      onChange={(event) => {
        const slot = event.currentTarget.value
        onPick(file.mediaFileId, slot === '' ? null : slot)
      }}
    >
      <option value="">Don’t file</option>
      {slots.map((slot) => (
        <option key={slotKey(slot)} value={slotKey(slot)} disabled={slot.heldBy != null}>
          {label(slot, discs)} {slot.title}
          {slot.heldBy != null ? ' (already filed)' : ''}
        </option>
      ))}
    </select>
  )
}

function Drift({ seconds }: { readonly seconds: number }) {
  const size = Math.abs(seconds)

  return (
    <Badge tone={size > 8 ? 'danger' : size > 3 ? 'warning' : 'success'} size="sm" mono>
      {size === 0 ? 'same length' : `${seconds > 0 ? '+' : '−'}${size}s`}
    </Badge>
  )
}

function Result({
  outcome,
  next: leave,
  onNext,
}: {
  readonly outcome: Outcome
  readonly next: string
  readonly onNext: () => void
}) {
  const next = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    next.current?.focus()
  }, [])

  const still =
    outcome.kind === 'filed'
      ? [
          ...outcome.seating.unseated.map((file) => file.name),
          ...outcome.seating.rows.flatMap((row) =>
            row.kind === 'seated' && !filed(row) ? [row.file.name] : [],
          ),
        ]
      : []

  const empty =
    outcome.kind === 'filed'
      ? outcome.seating.rows
          .filter((row) => row.kind === 'empty')
          .map((row) => label(row.slot, outcome.release.discCount))
      : []

  return (
    <div role="status" className={styles.result}>
      <Stack direction="column" gap={8} align="start">
        {outcome.kind === 'unreleased' ? (
          <>
            <Text size="md" weight="semibold" block>
              Marked as not a release
            </Text>
            <Text size="sm" tone="secondary" block>
              {outcome.detail}
            </Text>
          </>
        ) : (
          <>
            <Stack gap={12} align="center">
              <Artwork
                name={outcome.release.title}
                src={releaseArt(outcome.release.mbid)}
                size="md"
              />
              <Text size="md" weight="semibold" block>
                {outcome.result.detail}
              </Text>
            </Stack>
            {still.length > 0 ? (
              <Text size="sm" tone="secondary" block>
                Still open: {still.join(', ')}
              </Text>
            ) : null}
            {empty.length > 0 ? (
              <Text size="sm" tone="secondary" block>
                Tracks with no file: {empty.join(', ')}
              </Text>
            ) : null}
          </>
        )}
        <Button ref={next} variant="primary" onClick={onNext}>
          {leave}
        </Button>
      </Stack>
    </div>
  )
}

const RECORDING_QUESTIONS: ReadonlySet<string> = new Set(['Ambiguous', 'BelowThreshold'])

type MenuItem = {
  readonly label: string
  readonly description: string
  readonly unavailable: string | null
  readonly run: () => void
}

function MoreMenu({
  data,
  markable,
  onNotice,
  onReread,
}: {
  readonly data: FolderResponse
  readonly markable: boolean
  readonly onNotice: (notice: string) => void
  readonly onReread: () => void
}) {
  const id = useId()
  const menu = useRef<HTMLDivElement>(null)
  const navigate = useNavigate()
  const playback = usePlayback()

  const [dialog, setDialog] = useState<'tags' | 'musicbrainz' | null>(null)
  const [subject, setSubject] = useState<MatchingSubjectRef | null>(null)
  const [decided, setDecided] = useState(false)

  // Only the two refusals whose per-file dialog offers recordings; a file in a
  // refused set is answered by the album, which is this screen.
  const answerable = data.items.filter((item) => RECORDING_QUESTIONS.has(item.reason))
  const first = data.items[0]
  const placed = data.files - data.open

  const groups: readonly { readonly group: string; readonly items: readonly MenuItem[] }[] = [
    {
      group: 'Look closer',
      items: [
        {
          label: 'Play the folder',
          description: 'Starts the first open file in the player.',
          unavailable: first === undefined ? 'There is no open file to play.' : null,
          run: () => {
            if (first === undefined) return
            playback.play({
              id: first.mediaFileId,
              src: contentUrl(apiBaseUrl, first.path),
              title: first.title,
              subtitle: data.folder,
              ...(first.lengthMs != null ? { duration: first.lengthMs / 1000 } : {}),
            })
          },
        },
        {
          label: 'Open in Files',
          description: 'Everything in the folder, including covers, cue sheets and stray files.',
          unavailable: null,
          run: () => {
            void navigate({ to: '/files', search: { path: data.folder } })
          },
        },
        {
          label: 'Show every tag',
          description: 'The raw tags of each open file, as the file carries them.',
          unavailable: null,
          run: () => {
            setDialog('tags')
          },
        },
      ],
    },
    {
      group: 'Answer another way',
      items: [
        {
          label: 'Match files one at a time…',
          description:
            answerable.length > 0
              ? `${answerable.length} file${answerable.length === 1 ? ' has' : 's have'} recordings of ${answerable.length === 1 ? 'its' : 'their'} own to choose from.`
              : 'For files whose audio matched several recordings.',
          unavailable:
            answerable.length > 0 ? null : 'No file in this folder has recordings to choose from.',
          run: () => {
            const next = answerable[0]
            if (next === undefined) return
            setSubject({ source: 'catalogue', question: questionFor(next) })
          },
        },
        {
          label: 'Add this album to MusicBrainz…',
          description:
            'Opens the MusicBrainz release editor filled in from these files’ tags and lengths.',
          unavailable: markable ? null : 'Only an Artist/Album folder can be entered as a release.',
          run: () => {
            setDialog('musicbrainz')
          },
        },
        {
          label: 'Wrong match — ask again',
          description:
            'Takes back what the passes filed in this folder and makes the whole folder one question.',
          unavailable: !markable
            ? 'Only an Artist/Album folder can be reopened.'
            : placed > 0
              ? null
              : 'Nothing in this folder is filed yet.',
          run: () => {
            void reopenFolder(data.folder, onNotice, onReread)
          },
        },
      ],
    },
  ]

  return (
    <span className={styles.moreAnchor}>
      <Button variant="ghost" popoverTarget={id}>
        More ▾
      </Button>
      <div ref={menu} id={id} popover="auto" className={styles.menu}>
        {groups.map(({ group, items }) => (
          <fieldset key={group} className={styles.menuGroup}>
            <legend>
              <Text size="2xs" tone="tertiary" weight="medium">
                {group}
              </Text>
            </legend>
            {items.map((item) => (
              <button
                key={item.label}
                type="button"
                className={styles.menuItem}
                // aria-disabled rather than disabled, so the reason is reachable
                // by keyboard and read out rather than skipped.
                aria-disabled={item.unavailable !== null}
                onClick={() => {
                  if (item.unavailable !== null) return
                  menu.current?.hidePopover()
                  item.run()
                }}
              >
                <Text size="sm" weight="medium" block>
                  {item.label}
                </Text>
                <Text size="xs" tone="secondary" block>
                  {item.unavailable ?? item.description}
                </Text>
              </button>
            ))}
          </fieldset>
        ))}
      </div>

      {dialog === 'tags' ? (
        <Dialog
          open
          size="lg"
          title="Every tag"
          description={data.folder}
          onClose={() => {
            setDialog(null)
          }}
        >
          <TagList items={data.items} />
        </Dialog>
      ) : null}

      {dialog === 'musicbrainz' ? (
        <Dialog
          open
          title="Add this album to MusicBrainz"
          description={data.folder}
          onClose={() => {
            setDialog(null)
          }}
        >
          <AddToMusicBrainz folder={data.folder} />
        </Dialog>
      ) : null}

      {subject !== null ? (
        <MatchingDialog
          subject={subject}
          onClose={() => {
            setSubject(null)
            if (decided) {
              setDecided(false)
              onReread()
            }
          }}
          onDecided={() => {
            setDecided(true)
          }}
        />
      ) : null}
    </span>
  )
}

/** A folder file in the worklist's own shape, which is what the per-file dialog opens on. */
function questionFor(file: FileRow): components['schemas']['OpenQuestion'] {
  const cut = file.path.lastIndexOf('/')

  return {
    id: `recording:${file.mediaFileId}`,
    kind: 'recording',
    reason: file.reason,
    subject: file.name,
    folders: [cut < 0 ? '' : file.path.slice(0, cut)],
    files: 1,
    length: file.length,
    size: file.size,
    format: file.format,
  }
}

function TagList({ items }: { readonly items: readonly FileRow[] }) {
  return (
    <Stack direction="column" gap={16} align="stretch">
      {items.map((item) => (
        <section key={item.mediaFileId} aria-label={item.name}>
          <Text size="sm" weight="semibold" family="mono" block>
            {item.name}
          </Text>
          {item.tags.length === 0 ? (
            <Text size="xs" tone="tertiary" block>
              No tags.
            </Text>
          ) : (
            <dl className={styles.tagList}>
              {item.tags.map((tag) => (
                <div key={tag.name} className={styles.tagRow}>
                  <dt>
                    <Text size="2xs" family="mono" tone="tertiary">
                      {tag.name}
                    </Text>
                  </dt>
                  <dd>
                    <Text size="xs" family="mono" tone="secondary">
                      {tag.value}
                    </Text>
                  </dd>
                </div>
              ))}
            </dl>
          )}
        </section>
      ))}
    </Stack>
  )
}

/**
 * Entering an album MusicBrainz has never heard of.
 *
 * Two presses, not one: reading a folder's tags is an `ffprobe` per file, and a
 * window opened after an `await` is a pop-up as far as a browser is concerned.
 * Submitting the form from a second click keeps it inside a gesture, and puts
 * the track list in front of a person before it goes to a public database.
 */
function AddToMusicBrainz({ folder }: { readonly folder: string }) {
  const [state, setState] = useState<
    | { readonly status: 'idle' | 'reading' }
    | { readonly status: 'ready'; readonly seed: SeedResponse }
    | { readonly status: 'error'; readonly message: string }
  >({ status: 'idle' })

  async function read() {
    setState({ status: 'reading' })

    try {
      const seed = await api.get('/api/catalogue/matching/folders/seed', {
        params: { query: { folder } },
      })

      setState({ status: 'ready', seed })
    } catch (cause: unknown) {
      setState({ status: 'error', message: describeError(cause) })
    }
  }

  if (state.status === 'ready') {
    const { seed } = state

    return (
      <Stack direction="column" gap={8} align="start">
        <Text size="sm" block>
          <strong>{seed.title}</strong>
          {seed.year === null ? '' : ` (${seed.year})`} · {seed.artist} · {seed.trackCount} track
          {seed.trackCount === 1 ? '' : 's'}
          {seed.mediumCount > 1 ? ` across ${seed.mediumCount} discs` : ''}
        </Text>

        <Text size="xs" tone="tertiary" block>
          Read from the files themselves. MusicBrainz opens in a new tab with all of it filled in,
          under your account, and nothing is submitted until you press Save there. It is seeded as a{' '}
          <strong>bootleg</strong>; change it in the editor if this one was actually released.
          {seed.unmeasuredTracks > 0
            ? ` ${seed.unmeasuredTracks} of them have no length, so you will need to fill those in.`
            : ''}
        </Text>

        <Button
          variant="primary"
          onClick={() => {
            openEditor(seed, folder)
          }}
        >
          Open the MusicBrainz editor
        </Button>
      </Stack>
    )
  }

  return (
    <Stack direction="column" gap={8} align="start">
      <Text size="sm" tone="secondary" block>
        Reads every file’s tags and length, then shows what would be sent before anything leaves
        Fonoteca.
      </Text>
      <Button
        variant="secondary"
        disabled={state.status === 'reading'}
        onClick={() => {
          void read()
        }}
      >
        {state.status === 'reading' ? 'Reading the files…' : 'Read the files'}
      </Button>

      {state.status === 'error' ? (
        <Text size="xs" tone="warning" block>
          {state.message}
        </Text>
      ) : null}
    </Stack>
  )
}

/**
 * Hands the seed to MusicBrainz's release editor as a real form POST, so the
 * person arrives there signed in as themselves. `redirect_uri` brings the new
 * release's MBID back to this screen for this folder.
 */
function openEditor(seed: SeedResponse, folder: string): void {
  const form = document.createElement('form')

  form.method = 'post'
  form.action = seed.action
  form.target = '_blank'
  form.rel = 'noopener'
  form.hidden = true

  const returnTo = new URL('/library/matching', window.location.origin)
  returnTo.searchParams.set('folder', folder)

  const fields = [...seed.fields, { name: 'redirect_uri', value: returnTo.toString() }]

  for (const field of fields) {
    const input = document.createElement('input')

    input.type = 'hidden'
    input.name = field.name
    input.value = field.value
    form.append(input)
  }

  document.body.append(form)
  form.submit()
  form.remove()
}
