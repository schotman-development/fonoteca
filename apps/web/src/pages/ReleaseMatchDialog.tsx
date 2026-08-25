/**
 * Naming the album a folder of unidentifiable files came from.
 *
 * **The one screen in this application where a person is the evidence.** Every
 * other chooser here recovers a candidate set that a pass weighed and discarded:
 * the fingerprint is stored, so AcoustID can be re-asked; the component is
 * stored, so MusicBrainz can be re-browsed. The files this screen exists for
 * have nothing to recover. Measured on the library it was built against, not one
 * of the six hundred and ninety-six open file-level questions holds a MusicBrainz
 * recording — AcoustID has never heard the audio, or has heard it and links it to
 * nothing — so there is no rule that could rank anything, and adding one would be
 * a fourth pass whose worklist is precisely the files the other three refused.
 *
 * So the flow is: somebody types what they know, picks the release, checks a
 * pairing they can see, and commits it. Three things follow from that and are
 * worth stating, because each of them is a thing this screen deliberately does
 * not do.
 *
 * **It proposes a pairing rather than computing one.** The default is the order
 * the files sort in against the order the album prints, which is right on a
 * complete rip and wrong on any rip that is missing a track or has a hidden one.
 * That is a guess, and it is offered as a guess: every pair is on screen with
 * both lengths and the drift between them, and every one is changeable. What
 * makes it safe is not the quality of the guess, it is that nothing is written
 * until somebody has looked at it.
 *
 * **It scores nothing.** The search results come back in MusicBrainz's order,
 * with their own relevance printed rather than re-ranked. Sorting them by
 * anything measured here — track count against file count, say — would be a
 * matching rule wearing a list's clothes, and the whole point is that no rule
 * has any evidence to work from.
 *
 * **It touches no bytes.** An album is a catalogue fact, so this writes rows and
 * nothing else — no tag write, no `Fonoteca:AllowFileMutation`, no undo journal.
 * Which is also why the commit is allowed to be a single button: what it does is
 * recorded, reversible by hand, and visible on the release page immediately.
 */

import type { components } from '@fonoteca/api-client'
import { describeError } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  Button,
  Dialog,
  Field,
  Input,
  Stack,
  Text,
  VisuallyHidden,
} from '@fonoteca/ui'
import { Link } from '@tanstack/react-router'
import { useEffect, useMemo, useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import { releaseArt } from './coverArt.ts'
import styles from './ReleaseMatchDialog.module.css'
import { defaultSeating, driftMs, pairsFrom, queryFor, seatKey } from './seating.ts'

type OpenQuestion = components['schemas']['OpenQuestion']
type ReleaseSearchRow = components['schemas']['ReleaseSearchRow']
type ReleaseSlotRow = components['schemas']['ReleaseSlotRow']
type AlbumFilingResponse = components['schemas']['AlbumFilingResponse']

/** How far a file may sit from its slot's printed length before it is worth saying so. */
const DRIFT_WARNING_MS = 8_000

type CommitState =
  | { readonly status: 'idle' }
  | { readonly status: 'sending' }
  | { readonly status: 'done'; readonly result: AlbumFilingResponse }
  | { readonly status: 'error'; readonly message: string }

export function ReleaseMatchDialog({
  folder,
  questions,
  onClose,
  onFiled,
  initialQuery,
}: {
  readonly folder: string
  readonly questions: readonly OpenQuestion[]
  readonly onClose: () => void
  /** A filing reached the catalogue, so the worklist behind this dialog is stale. */
  readonly onFiled: () => void
  /**
   * What to search for on open, instead of a guess made from the folder name.
   *
   * Set on one path: somebody has just entered this concert in MusicBrainz and
   * been sent back with its MBID. Searching for an MBID is a lookup rather than
   * a search — {@link SearchReleases} spots one before it queries — so this
   * arrives as a single result which is certainly the right one, and it works
   * against a mirror where the text search does not.
   */
  readonly initialQuery?: string | undefined
}) {
  return (
    <Dialog
      open
      onClose={onClose}
      size="lg"
      title="Match these files to an album"
      description={
        <>
          <span className={styles.mono}>{folder === '' ? 'the library root' : folder}</span> ·{' '}
          {questions.length} file{questions.length === 1 ? '' : 's'}
        </>
      }
    >
      {/*
        Keyed on the folder and the exact set, so reopening on a different
        selection starts from a fresh search and a fresh pairing. Carrying a
        chosen release across would be the worst possible default here: the
        pairing is positional, so a stale album seats a *different* set of files
        on the same numbers without anything on screen changing.
      */}
      <MatchFlow
        key={`${folder}:${questions.map((question) => question.id).join(',')}`}
        folder={folder}
        questions={questions}
        onFiled={onFiled}
        initialQuery={initialQuery}
      />
    </Dialog>
  )
}

function MatchFlow({
  folder,
  questions,
  onFiled,
  initialQuery,
}: {
  readonly folder: string
  readonly questions: readonly OpenQuestion[]
  readonly onFiled: () => void
  readonly initialQuery?: string | undefined
}) {
  const [query, setQuery] = useState(() => initialQuery ?? queryFor(folder))

  // The search that has actually been run, which is not what is in the box. A
  // search costs a gated MusicBrainz request, so it happens on submit and not on
  // every keystroke — and the two being separate is what lets the box be edited
  // while the previous results are still readable.
  // Asked already when the release was named for us, because it was named by
  // somebody who has just typed the whole album into MusicBrainz — making them
  // press Search on an MBID they did not choose to type would be asking them to
  // confirm their own click.
  const [asked, setAsked] = useState<string | null>(() => initialQuery ?? null)

  const [chosen, setChosen] = useState<ReleaseSearchRow | null>(null)

  const files = useMemo(() => [...questions].sort(byPath), [questions])

  return (
    <Stack direction="column" gap={16} align="start" className={styles.flow}>
      <SearchBox
        query={query}
        onQuery={setQuery}
        onSubmit={() => {
          setAsked(query.trim())
          setChosen(null)
        }}
      />

      {asked !== null && chosen === null ? <Results query={asked} onChoose={setChosen} /> : null}

      {chosen !== null ? (
        <Pairing
          release={chosen}
          folder={folder}
          files={files}
          onBack={() => {
            setChosen(null)
          }}
          onFiled={onFiled}
        />
      ) : null}
    </Stack>
  )
}

function SearchBox({
  query,
  onQuery,
  onSubmit,
}: {
  readonly query: string
  readonly onQuery: (next: string) => void
  readonly onSubmit: () => void
}) {
  return (
    <form
      className={styles.search}
      onSubmit={(event) => {
        event.preventDefault()
        if (query.trim() !== '') onSubmit()
      }}
    >
      <Field
        label="Search MusicBrainz for the album"
        hint="Artist and title usually does it. MusicBrainz's own syntax works — artist:, date:, barcode: — and a release URL or MBID pasted here is looked up directly rather than searched for."
      >
        {(control) => (
          <Input
            {...control}
            value={query}
            fullWidth
            placeholder="e.g. Concertgebouworkest Beethoven Symphonies"
            onChange={(event) => {
              onQuery(event.currentTarget.value)
            }}
          />
        )}
      </Field>

      <Button type="submit" variant="primary" disabled={query.trim() === ''}>
        Search
      </Button>
    </form>
  )
}

/**
 * The albums MusicBrainz offered, in MusicBrainz's own order.
 *
 * Unsorted and unfiltered here on purpose — see the module remarks. The one
 * number this screen adds is the track count beside the release, because "does
 * this album have as many tracks as I hold" is the question a person is actually
 * asking of each row, and counting them by eye off a list of forty is how the
 * wrong pressing gets picked.
 */
function Results({
  query,
  onChoose,
}: {
  readonly query: string
  readonly onChoose: (release: ReleaseSearchRow) => void
}) {
  const state = useApiQuery(
    () => api.get('/api/catalogue/matching/releases/search', { params: { query: { q: query } } }),
    [query],
  )

  return (
    <div className={styles.results}>
      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? (
          <Text size="sm" tone="tertiary">
            Asking MusicBrainz…
          </Text>
        ) : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">MusicBrainz could not answer</Badge>
            <Text size="sm" tone="tertiary" block>
              {state.message}
            </Text>
            {/*
            The one failure mode worth naming on screen, because it looks like
            every other outage and is a configuration fact rather than a
            transient one. Replication does not carry the search index, so a
            mirror answers every other call in this application and cannot answer
            this one — see ADR 0006.
          */}
            <Text size="xs" tone="tertiary" block>
              If Fonoteca is pointed at a self-hosted MusicBrainz mirror, this is expected: mirrors
              replicate the database and not the search index, so searching needs musicbrainz.org.
              Pasting a release URL or MBID into the box works either way.
            </Text>
          </Stack>
        ) : null}

        {state.status === 'ready' && state.data.items.length === 0 ? (
          <Text size="sm" tone="tertiary" block>
            Nothing matched “{query}”. Try the artist and the album title alone — rippers put the
            year, the codec and the bit depth in the folder name, and none of those are indexed.
          </Text>
        ) : null}

        {state.status === 'ready' && state.data.items.length > 0 ? (
          <Text size="sm" tone="tertiary" block>
            {state.data.items.length} album{state.data.items.length === 1 ? '' : 's'} found.
          </Text>
        ) : null}
      </div>

      {state.status === 'ready' && state.data.items.length > 0 ? (
        <ul className={styles.resultList} aria-label="Albums matching your search">
          {state.data.items.map((release) => (
            <li key={release.mbid}>
              <button
                type="button"
                className={styles.resultButton}
                onClick={() => {
                  onChoose(release)
                }}
              >
                <Stack gap={12} align="center">
                  {/*
                    The cover, because two pressings of one album are two
                    identical lines of text and two different sleeves. Missing
                    art draws a monogram — plenty of releases have none.
                  */}
                  <Artwork name={release.title} src={releaseArt(release.mbid)} size="md" />

                  <Stack direction="column" gap={2} align="start">
                    <Stack gap={8} align="baseline" wrap>
                      <Text size="sm" weight="medium">
                        {release.title}
                      </Text>
                      {release.artist !== null ? (
                        <Text size="sm" tone="secondary">
                          {release.artist}
                        </Text>
                      ) : null}
                    </Stack>

                    <Text size="xs" tone="tertiary" family="mono" block>
                      {releaseFacts(release)}
                    </Text>
                  </Stack>
                </Stack>
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  )
}

/** Year, country, format, track count — the four things that separate two pressings. */
function releaseFacts(release: ReleaseSearchRow): string {
  return [
    release.year?.toString(),
    release.primaryType,
    ...release.secondaryTypes,
    release.formats,
    `${release.trackCount} track${release.trackCount === 1 ? '' : 's'}`,
    release.country,
    release.status !== 'Official' ? release.status : null,
    release.score !== null ? `${release.score}%` : null,
  ]
    .filter((part) => part != null && part !== '')
    .join(' · ')
}

/** A slot's identity on the wire and in a `<select>`: disc and position. */
function seatOf(slot: ReleaseSlotRow): string {
  return seatKey(slot.discNumber, slot.position)
}

/**
 * The pairing: which file goes on which position of the chosen album.
 *
 * **The seating is state, and the default is a guess that is allowed to be
 * wrong.** Files sorted by path against slots in disc-and-position order, which
 * is exactly right for a complete rip in track order and exactly wrong for one
 * that skipped a track. Both lengths and the drift between them are printed on
 * every row precisely so that being wrong is visible rather than committed.
 *
 * Changing a seat that another file already holds **swaps** the two rather than
 * refusing or displacing. Two files on one position is the one state the commit
 * rejects, so the alternative is an error message on an action whose intent is
 * never ambiguous — somebody dragging track 4 onto track 5 means the pair are
 * the wrong way round, which is what a rip with a mis-numbered file actually
 * looks like.
 */
function Pairing({
  release,
  folder,
  files,
  onBack,
  onFiled,
}: {
  readonly release: ReleaseSearchRow
  readonly folder: string
  readonly files: readonly OpenQuestion[]
  readonly onBack: () => void
  readonly onFiled: () => void
}) {
  // The folder goes with the request so the track list can come back saying
  // which of its positions this folder's *other* files already sit on. Those
  // files are not on this screen — the worklist lists open questions and they
  // are answered — which is exactly why the screen cannot work them out itself.
  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/matching/releases/{id}/slots', {
        params: { path: { id: release.mbid }, query: { folder } },
      }),
    [release.mbid, folder],
  )

  const [seats, setSeats] = useState<ReadonlyMap<string, string>>(() => new Map())
  const [commit, setCommit] = useState<CommitState>({ status: 'idle' })

  const slots = state.status === 'ready' ? state.data.slots : []

  // Seeded once the track list arrives, and never again — `seats` is what a
  // person has since edited, and recomputing the default over the top of it
  // would undo their corrections every time this re-rendered.
  useEffect(() => {
    if (state.status !== 'ready') return

    setSeats(defaultSeating(files, state.data.slots))
  }, [state, files])

  function seat(file: string, to: string) {
    const next = new Map(seats)

    if (to === '') {
      next.delete(file)
      setSeats(next)
      return
    }

    // Whoever is sitting there gets this file's seat, so the invariant "one file
    // per position" holds without an error state ever existing.
    const displaced = [...seats].find(([id, held]) => held === to && id !== file)
    const vacated = seats.get(file)

    if (displaced !== undefined) {
      if (vacated === undefined) next.delete(displaced[0])
      else next.set(displaced[0], vacated)
    }

    next.set(file, to)
    setSeats(next)
  }

  async function send() {
    setCommit({ status: 'sending' })

    try {
      const result = await api.post('/api/catalogue/matching/files/release', {
        json: {
          release: release.mbid,
          // Keyed on the *question* id throughout — `recording:{guid}` — because
          // that is what the worklist, the ticks and the row identity all use.
          // The API takes the media file's own id, so the unwrapping happens
          // here, at the one place the two vocabularies meet.
          pairs: pairsFrom(files, seats),
        },
      })

      setCommit({ status: 'done', result })
      onFiled()
    } catch (cause: unknown) {
      setCommit({ status: 'error', message: describeError(cause) })
    }
  }

  if (commit.status === 'done') {
    return <Filed result={commit.result} onBack={onBack} />
  }

  const unseated = files.filter((file) => !seats.has(file.id))
  const taken = slots.filter((slot) => slot.heldBy !== null)
  const empty = slots.filter(
    (slot) => slot.heldBy === null && ![...seats.values()].includes(seatOf(slot)),
  )

  return (
    <Stack direction="column" gap={12} align="start" className={styles.pairing}>
      <Stack gap={12} align="center" wrap>
        {/* The album being seated against, so a mis-click is visible before
            thirty files are filed under it. */}
        <Artwork name={release.title} src={releaseArt(release.mbid)} size="md" />
        <Text size="sm" weight="medium">
          {release.title}
        </Text>
        <Text size="sm" tone="secondary">
          {release.artist}
        </Text>
        <Button size="sm" variant="ghost" onClick={onBack}>
          Choose a different album
        </Button>
      </Stack>

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? (
          <Text size="sm" tone="tertiary">
            Reading the track list…
          </Text>
        ) : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read the track list</Badge>
            <Text size="sm" tone="tertiary" block>
              {state.message}
            </Text>
          </Stack>
        ) : null}
      </div>

      {state.status === 'ready' ? (
        <>
          <Text size="sm" tone="secondary" block>
            {taken.length > 0 ? (
              <>
                {taken.length} of this album’s {slots.length} positions{' '}
                {taken.length === 1 ? 'is' : 'are'} already held by{' '}
                {taken.length === 1 ? 'a file' : 'files'} in this folder that earlier passes
                identified, so Fonoteca has paired your remaining {files.length} file
                {files.length === 1 ? '' : 's'} with the {files.length === 1 ? 'gap' : 'gaps'}.
              </>
            ) : (
              <>Fonoteca has paired your files with the album’s positions in order.</>
            )}{' '}
            That is a guess — it is right when the rip is complete and in order, and wrong the
            moment a track is missing. Check the lengths, fix any row that is out, and file them.
          </Text>

          <table className={styles.table}>
            <caption>
              <VisuallyHidden>Your files paired with positions on {release.title}</VisuallyHidden>
            </caption>
            <thead>
              <tr>
                <th scope="col">File</th>
                <th scope="col">Position on the album</th>
                <th scope="col">Length</th>
              </tr>
            </thead>
            <tbody>
              {files.map((file) => (
                <SeatRow
                  key={file.id}
                  file={file}
                  slots={slots}
                  held={seats.get(file.id) ?? ''}
                  onSeat={(to) => {
                    seat(file.id, to)
                  }}
                />
              ))}
            </tbody>
          </table>

          <Stack direction="column" gap={4} align="start">
            {unseated.length > 0 ? (
              <Text size="sm" tone="warning" block>
                {unseated.length} file{unseated.length === 1 ? '' : 's'} on no position.{' '}
                {unseated.length === 1 ? 'It stays' : 'They stay'} on the worklist — nothing is
                written about {unseated.length === 1 ? 'it' : 'them'}.
              </Text>
            ) : null}

            {/*
              Empty positions are worth printing and are not a warning. An album
              you hold ten of twelve tracks of is an album you hold ten of twelve
              tracks of, and saying so is the whole reason the release's *whole*
              track list is written rather than only the parts something landed
              on.
            */}
            {empty.length > 0 ? (
              <Text size="sm" tone="secondary" block>
                {empty.length} position{empty.length === 1 ? '' : 's'} on this album will have no
                file: {empty.slice(0, 8).map(seatOf).join(', ')}
                {empty.length > 8 ? ` and ${empty.length - 8} more` : ''}.
              </Text>
            ) : null}

            {/*
              Not a warning either, and the count matters more than the list: it
              is how somebody checks that the album they picked is the one their
              other files were already filed under, which is the single thing
              that would make every number on this screen describe a different
              record. A wrong pick here reports zero held.
            */}
            {taken.length > 0 ? (
              <Text size="sm" tone="tertiary" block>
                {taken.length} position{taken.length === 1 ? '' : 's'} already filled from this
                folder and left alone: {taken.slice(0, 8).map(seatOf).join(', ')}
                {taken.length > 8 ? ` and ${taken.length - 8} more` : ''}.
              </Text>
            ) : null}
          </Stack>

          {commit.status === 'error' ? (
            <Stack direction="column" gap={4} align="start" role="status">
              <Badge tone="danger">Nothing was filed</Badge>
              <Text size="sm" tone="tertiary" block>
                {commit.message}
              </Text>
            </Stack>
          ) : null}

          <Button
            variant="primary"
            disabled={seats.size === 0 || commit.status === 'sending'}
            onClick={() => {
              void send()
            }}
          >
            {commit.status === 'sending'
              ? 'Filing…'
              : `File ${seats.size} file${seats.size === 1 ? '' : 's'} under this album`}
          </Button>

          <Text size="xs" tone="tertiary" block>
            Nothing on disk is touched. This writes the album, its track list and the recording each
            file is now known to hold into the catalogue, and records that you decided it — so no
            later pass overwrites it.
          </Text>
        </>
      ) : null}
    </Stack>
  )
}

/**
 * One file, its seat, and how far the two are apart.
 *
 * A native `<select>`. The design system has no combobox and this is not the
 * screen to build one on: the list is one album long, every option is a short
 * label, and the platform's own control already carries the role, the typeahead,
 * the keyboard behaviour and — on a phone — a picker that a hand-built listbox
 * would have to reimplement.
 */
function SeatRow({
  file,
  slots,
  held,
  onSeat,
}: {
  readonly file: OpenQuestion
  readonly slots: readonly ReleaseSlotRow[]
  readonly held: string
  readonly onSeat: (to: string) => void
}) {
  const slot = slots.find((candidate) => seatOf(candidate) === held)
  const drift = driftMs(file.length, slot?.durationMs)

  return (
    <tr>
      <td>
        <Text size="sm" family="mono" block>
          {file.subject}
        </Text>
      </td>

      <td>
        <label>
          <VisuallyHidden>Position for {file.subject}</VisuallyHidden>
          <select
            className={styles.select}
            value={held}
            onChange={(event) => {
              onSeat(event.currentTarget.value)
            }}
          >
            <option value="">— leave this file open —</option>
            {/*
              A held position stays in the list and cannot be chosen. Removing it
              would be worse in both directions: the numbers would stop matching
              the album's own, and the one thing a person most needs to see —
              that track 14 is the only gap in an otherwise complete album — is
              only legible against the tracks either side of it.
            */}
            {slots.map((candidate) => (
              <option
                key={seatOf(candidate)}
                value={seatOf(candidate)}
                disabled={candidate.heldBy !== null}
              >
                {seatOf(candidate)} · {candidate.title}
                {candidate.duration !== null ? ` (${candidate.duration})` : ''}
                {candidate.heldBy !== null ? ` — already ${candidate.heldBy}` : ''}
              </option>
            ))}
          </select>
        </label>
      </td>

      <td>
        <Text size="xs" tone="tertiary" family="mono" block>
          {file.length ?? '—'}
          {slot?.duration != null ? ` / ${slot.duration}` : ''}
        </Text>
        {drift !== null ? (
          <Text
            size="xs"
            tone={Math.abs(drift) > DRIFT_WARNING_MS ? 'warning' : 'tertiary'}
            family="mono"
            block
          >
            {drift > 0 ? '+' : '−'}
            {Math.round(Math.abs(drift) / 1000)}s
          </Text>
        ) : null}
      </td>
    </tr>
  )
}

function Filed({
  result,
  onBack,
}: {
  readonly result: AlbumFilingResponse
  readonly onBack: () => void
}) {
  return (
    <Stack direction="column" gap={8} align="start" role="status">
      <Badge tone="success">Filed</Badge>

      <Text size="sm" block>
        {result.detail}
      </Text>

      <Text size="sm" tone="secondary" block>
        “{result.title}” is in the catalogue with its whole track list, and these files carry the
        recording each position names. They are off every pass’s worklist for good — re-running
        identification or attribution will not touch them.
      </Text>

      <Text size="sm" block>
        <Link to="/library/releases/$releaseId" params={{ releaseId: result.release }}>
          Open “{result.title}”
        </Link>{' '}
        — the fingerprints of the files you just seated can be contributed to AcoustID from there,
        which is the only place that offer appears.
      </Text>

      <Button size="sm" variant="ghost" onClick={onBack}>
        File more under a different album
      </Button>
    </Stack>
  )
}

/** Path order, which is the order a rip is in and therefore the order to seat it in. */
function byPath(left: OpenQuestion, right: OpenQuestion): number {
  const a = `${left.folders[0] ?? ''}/${left.subject}`
  const b = `${right.folders[0] ?? ''}/${right.subject}`

  return a.localeCompare(b, undefined, { numeric: true })
}
