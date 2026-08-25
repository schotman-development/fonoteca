import { type components, describeError } from '@fonoteca/api-client'
import { Badge, Button, Card, Disclosure, Stack, Text, VisuallyHidden } from '@fonoteca/ui'
import { Link, useSearch } from '@tanstack/react-router'
import { useEffect, useMemo, useRef, useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import { CERTAINTY } from './certainty.ts'
import { MatchingDialog, type MatchingSubjectRef } from './MatchingDialog.tsx'
import styles from './MatchingPage.module.css'
import { MATCHING_QUESTIONS } from './matchingFixtures.ts'
import { kindOf, type OpenKind, type OpenReason, whyOpen } from './openQuestions.ts'
import { ReleaseMatchDialog } from './ReleaseMatchDialog.tsx'
import { ALBUM_FOLDER_DEPTH, albumFolderOf, mediaFileIdOf } from './seating.ts'

type OpenQuestion = components['schemas']['OpenQuestion']
type OpenQuestionCount = components['schemas']['OpenQuestionCount']
type MatchingQueue = components['schemas']['MatchingQueueResponse']
type ReleaseSeedResponse = components['schemas']['ReleaseSeedResponse']
type FolderFile = components['schemas']['FolderFileRow']

/**
 * How many rows a refusal is allowed to print.
 *
 * Hard ceilings, not a first page: there is no "show all", because the rows
 * below the cut are the same sentence and scrolling four hundred of them is the
 * behaviour this screen was rebuilt to stop. The two numbers differ because the
 * units do — ten sets of files are ten albums, each one a different decision,
 * while fifty single files are fifty repetitions of one fact and already more
 * than anybody reads.
 *
 * The counts on the group and in the truncation line come from the endpoint's
 * own grouping over the whole worklist, so capping the rows never understates
 * how much is open.
 */
const ALBUM_ROWS = 10

/**
 * How many album folders the by-hand section is allowed to print.
 *
 * A ceiling for a pathological library, not a page size — the old cap of fifty
 * rows was a reading decision, because fifty of six hundred and ninety-six files
 * were the same sentence fifty times and the rest were more of it. Grouped, no
 * two rows are the same fact: each one is an album somebody either recognises or
 * does not, and cutting the list in half hides work rather than repetition. The
 * measured worklist is a hundred and thirteen folders, so this shows all of it
 * and only bites on a library several times the size.
 *
 * Files inside a folder are never capped at all. A folder is the thing somebody
 * is about to match, and offering the first fifty of a hundred and twelve would
 * file two thirds of an album and leave the rest looking like a separate
 * problem.
 */
const FOLDER_ROWS = 250

/**
 * What the passes would not decide.
 *
 * The three passes are all allowed to refuse, and refusing is the behaviour
 * being protected rather than a bug to route around — a wrong album is worse
 * than a missing one and far harder to notice. What was missing is the other
 * half: a refusal has been a dead end, with nothing in the application willing
 * to say what had been refused or how much of the library was sitting in it.
 *
 * **This list is real, and half of it can be answered.** `GET
 * /api/catalogue/matching` reads the outcome columns the three passes write, so
 * every row here is a file or a set of files the catalogue is genuinely
 * undecided about. No candidate set is stored anywhere, so an answer is only
 * offered where the set can be *recovered* — and all three of the refusals that
 * have candidates now can be. The two identification ones are re-asked from the
 * file's stored fingerprint; the album-shaped one is re-gathered from
 * MusicBrainz for the whole component, which is bounded because the component
 * already exists and has nothing left to expand into. Both commit through a
 * `POST …/decision`.
 *
 * What is left over is the questions with no answers to offer at all: audio
 * AcoustID has never heard, a cluster MusicBrainz links nothing to, and a
 * recording on no release anywhere.
 *
 * **Read as a summary, not as a list.** The first version printed every question
 * inline — around seven hundred rows, four hundred and fifty-four of them the
 * same sentence — and the result was a page whose only affordance was scrolling.
 * What replaced it says the shape once: how much is open, in which of the two
 * units, under which refusal, and, for each refusal, whether anything is
 * actually waiting on a person. The files are one click behind that and capped
 * when you get there.
 */
export function MatchingPage() {
  // Bumped by a committed decision, and the whole of this screen's cache
  // invalidation. `useApiQuery` has none by design — adopting a server-state
  // library is a decision to take on evidence, not one to take by association —
  // and one counter in the one place on this page that mutates anything is not
  // yet that evidence. It is also the honest amount of invalidation: a decision
  // changes the *worklist*, so the worklist is what gets read again.
  const [decisions, setDecisions] = useState(0)

  // The endpoint's own maximum, asked for in one request. A worklist that has
  // been truncated somewhere out of sight is worse than a long one, and at the
  // measured size — 698 questions — the whole thing is a single modest payload.
  // The row caps below are a reading decision and are applied to what arrived;
  // the counts they reconcile against are the endpoint's, over everything.
  const state = useApiQuery(
    () => api.get('/api/catalogue/matching', { params: { query: { take: 1000 } } }),
    [decisions],
  )

  const [subject, setSubject] = useState<MatchingSubjectRef | null>(null)

  /**
   * Which files are ticked, across every folder at once.
   *
   * One set for the page rather than one per folder, so collapsing a group does
   * not throw a selection away — a person comparing two folders before deciding
   * which is which should not lose their ticks by looking. Each folder reads its
   * own intersection with this, which is also what makes "all selected, or all
   * of them if none are" a per-folder question with a page-wide answer.
   */
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set())

  /** The files a release is being chosen for, once somebody has asked for one. */
  const [filing, setFiling] = useState<Filing | null>(null)

  /**
   * Coming back from MusicBrainz with a release that did not exist an hour ago.
   *
   * The seeded editor is handed a `redirect_uri`, so a saved edit lands here
   * with `release_mbid` and the folder it was for. The dialog is then opened on
   * that folder with the MBID as its query, which the search endpoint resolves
   * as a lookup — one certain result rather than a text search that would not
   * find a release added five seconds ago even on the official server, and
   * could not run at all against a mirror.
   *
   * Guarded by the MBID rather than by a boolean, so closing the dialog does not
   * reopen it and a *second* return in the same tab still works. The folder may
   * legitimately not be on the worklist any more — somebody may have answered it
   * from another tab — and then nothing opens, which is the truthful outcome:
   * there is no question left to answer.
   */
  const returned = useSearch({ from: '/library/matching' })
  const [handled, setHandled] = useState<string | null>(null)

  const returning =
    returned.release_mbid !== undefined && returned.release_mbid !== handled
      ? { mbid: returned.release_mbid, folder: returned.folder ?? '' }
      : null

  useEffect(() => {
    if (returning === null || state.status !== 'ready') return

    const questions = state.data.items.filter(
      (question) => albumFolderOf(question.folders[0] ?? '') === returning.folder,
    )

    setHandled(returning.mbid)
    if (questions.length > 0) setFiling({ folder: returning.folder, questions })
  }, [returning, state])

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Identify
          </Text>
        </h1>
        <Text tone="secondary" block>
          Files Fonoteca could not match on its own, grouped by what went wrong. Open a group to see
          what is in it, and a row to see the file and settle it.
        </Text>
      </Stack>

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? <Text tone="tertiary">Reading the catalogue…</Text> : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read the worklist</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}

        {state.status === 'ready' ? <Summary kinds={state.data.kinds} /> : null}
      </div>

      {/*
        Collapsed, and that is the change.

        This was six lines of open prose at the top of the screen, before
        anything a person could act on: what the catalogue does and does not
        record, what a candidate set is, why a MusicBrainz browse is a pass
        rather than a request. Every sentence of it is true and none of it is
        what somebody arriving here needs first — they need to know which of the
        eight hundred rows below is theirs to answer, which is now the line above
        and the section order below. The explanation keeps every word and waits
        behind a question a person can choose to ask.
      */}
      {state.status === 'ready' && state.data.total > 0 ? (
        <div className={styles.notice}>
          <Disclosure
            size="sm"
            summary={
              <Text size="sm" weight="medium">
                Why can’t I answer all of these?
              </Text>
            }
          >
            <Stack direction="column" gap={8} align="start" className={styles.noticeBody}>
              <Text size="sm" tone="secondary" block>
                When a pass gives up on a file it records <em>that</em> it gave up, not what it was
                choosing between — the candidates are worked out while the pass runs and thrown
                away. So Fonoteca can only put a list of answers back in front of you where that
                list can be rebuilt.
              </Text>
              <Text size="sm" tone="secondary" block>
                For a set of files it can: they were refused together because they share the
                recordings they were compared against, so asking MusicBrainz again about that set
                rebuilds the same shortlist. That is the second section, and it costs a wait of
                seconds rather than an instant answer.
              </Text>
              <Text size="sm" tone="secondary" block>
                For a single file it usually cannot, and that is not a gap in Fonoteca. These files
                are open because AcoustID has never heard the audio, or has heard it and links it to
                no MusicBrainz recording — there is no shortlist anywhere, and re-asking spends a
                turn at the rate limit to be told the same thing.
              </Text>
              <Text size="sm" tone="secondary" block>
                Which leaves the one thing that does work: you know what the album is. Search
                MusicBrainz for it, check the pairing Fonoteca proposes and file them. Your answer
                is recorded as a person’s decision, so no later pass overwrites it, and nothing on
                disk is touched.
              </Text>
            </Stack>
          </Disclosure>
        </div>
      ) : null}

      {state.status === 'ready' && state.data.total > 0 ? (
        <Sections
          data={state.data}
          onOpen={setSubject}
          selected={selected}
          onSelect={setSelected}
          onFile={setFiling}
          onDismissed={() => {
            setSelected(new Set())
            setDecisions((committed) => committed + 1)
          }}
        />
      ) : null}

      {state.status === 'ready' && state.data.total === 0 ? <Empty /> : null}

      {/*
        Below the real work, not above it.

        These are invented, and they used to be the first clickable thing on the
        page — so the first thing a new reader clicked was fake data, with
        nothing after the click to say so. Still reachable, still labelled, now
        placed where somebody goes looking for them rather than where they trip
        over them.
      */}
      <WorkedExamples onOpen={setSubject} />

      {subject !== null ? (
        <MatchingDialog
          subject={subject}
          onClose={() => {
            setSubject(null)
          }}
          onDecided={() => {
            setDecisions((committed) => committed + 1)
          }}
        />
      ) : null}

      {filing !== null ? (
        <ReleaseMatchDialog
          folder={filing.folder}
          questions={filing.questions}
          onClose={() => {
            setFiling(null)
          }}
          onFiled={() => {
            // The ticks are spent. Clearing them is the honest reading of what
            // just happened — those files are no longer open questions, so a
            // selection that survived would be a set of things this screen can
            // no longer do anything with, waiting to be re-offered to the next
            // album somebody opens.
            setSelected(new Set())
            setDecisions((committed) => committed + 1)
          }}
        />
      ) : null}
    </Stack>
  )
}

/** A folder and the files in it a release is being chosen for. */
type Filing = {
  readonly folder: string
  readonly questions: readonly OpenQuestion[]
}

/**
 * How much is open, and in which of the two units.
 *
 * **The second sentence used to say how much was "waiting on you", counted from
 * the refusals that had a candidate set to show.** That number was 74 of 817 and
 * it is no longer true of anything: the six hundred and ninety-six file-level
 * questions have no candidate set and never will — that is what makes them
 * open — and they are now the part of this screen a person can actually finish,
 * by naming the album themselves. Counting them as somebody else's problem was
 * the honest reading of the old screen and would be a lie about this one.
 *
 * So it says the split instead, which is the thing that decides what to do next:
 * one number is folders to work through by hand, the other is sets Fonoteca has
 * already found candidates for.
 */
function Summary({ kinds }: { readonly kinds: readonly OpenQuestionCount[] }) {
  const questions = kinds.reduce((total, kind) => total + kind.questions, 0)
  const files = kinds.reduce((total, kind) => total + kind.files, 0)

  const loose = kinds.find((kind) => kind.name === 'recording')
  const sets = kinds.find((kind) => kind.name === 'release')

  if (questions === 0) return null

  return (
    <Stack direction="column" gap={2} align="start">
      <Text size="sm" tone="secondary" block>
        {questions.toLocaleString()} open question{questions === 1 ? '' : 's'}, over{' '}
        {files.toLocaleString()} file{files === 1 ? '' : 's'}.
      </Text>
      <Text size="sm" tone="secondary" block>
        {[
          loose !== undefined && loose.files > 0
            ? `${loose.files.toLocaleString()} single files to match against an album yourself`
            : null,
          sets !== undefined && sets.questions > 0
            ? `${sets.questions.toLocaleString()} set${sets.questions === 1 ? '' : 's'} of files Fonoteca can offer candidates for`
            : null,
        ]
          .filter((part) => part !== null)
          .join(', and ')}
        .
      </Text>
    </Stack>
  )
}

type Group = {
  readonly reason: OpenQuestionCount
  readonly questions: readonly OpenQuestion[]
  readonly kind: OpenKind
}

/** An album folder, and every open file in it whatever refused them. */
type Folder = {
  /** `Artist/Album`, which is the key and the heading at once. */
  readonly path: string
  readonly questions: readonly OpenQuestion[]
}

/**
 * Every file-level question, gathered by the album it probably came from.
 *
 * **The change this screen was rebuilt for.** The worklist arrives ordered by
 * path and split by refusal, which reads as six hundred and ninety-six unrelated
 * files: an album whose eight tracks AcoustID has never heard and whose four
 * others it knows but cannot link appears as two runs of rows in two different
 * sections, and the only thing on screen tying them together is a folder name
 * printed twelve times. Matching a release from that is not hard, it is
 * impossible — the unit of the decision is the album and the unit of the display
 * was the refusal.
 *
 * So the refusal becomes a badge on the row and the folder becomes the group.
 * On the target library that is a hundred and twenty-five groups instead of six
 * hundred and ninety-six rows, and each group is one album somebody can name.
 *
 * Ordered by size, then by path. Biggest first for the reason the endpoint
 * orders components that way — a folder with thirty files in it is a whole album
 * missing from the catalogue, and it is worth a person's attention before a
 * folder holding one stray track.
 */
function foldersOf(questions: readonly OpenQuestion[]): readonly Folder[] {
  const byFolder = new Map<string, OpenQuestion[]>()

  for (const question of questions) {
    const path = albumFolderOf(question.folders[0] ?? '')
    const held = byFolder.get(path)

    if (held === undefined) byFolder.set(path, [question])
    else held.push(question)
  }

  return [...byFolder]
    .map(([path, held]) => ({ path, questions: held }))
    .sort(
      (left, right) =>
        right.questions.length - left.questions.length || left.path.localeCompare(right.path),
    )
}

/**
 * The worklist, split by the unit the decision is taken in.
 *
 * Two units, and they were always two jobs. A release question is a set of files
 * the attribution pass formed and refused together; it has a candidate set that
 * can be re-gathered, and answering one is a click on an album Fonoteca found.
 * A file-level question has none of that — nothing recorded what it was choosing
 * between, and for these files there was never anything to choose between, since
 * AcoustID could not place the audio at all.
 *
 * That second kind is now grouped by folder rather than by refusal, and given
 * the only tool that can settle it: a person who knows what the album is,
 * searching MusicBrainz for it. See {@link foldersOf}.
 */
function Sections({
  data,
  onOpen,
  selected,
  onSelect,
  onFile,
  onDismissed,
}: {
  readonly data: MatchingQueue
  readonly onOpen: (subject: MatchingSubjectRef) => void
  readonly selected: ReadonlySet<string>
  readonly onSelect: (next: ReadonlySet<string>) => void
  readonly onFile: (filing: Filing) => void
  readonly onDismissed: () => void
}) {
  const groups: readonly Group[] = data.reasons.map((reason) => {
    const questions = data.items.filter((item) => item.reason === reason.name)
    return { reason, questions, kind: kindOf(reason.name, questions) }
  })

  const albums = groups.filter((group) => group.kind === 'release')

  // Memoised because the grouping walks every item and rebuilds a hundred and
  // twenty-five arrays, and it re-runs on every tick of a checkbox otherwise.
  const folders = useMemo(
    () => foldersOf(data.items.filter((item) => item.kind === 'recording')),
    [data.items],
  )

  const files = groups.filter((group) => group.kind === 'recording')

  return (
    <Stack direction="column" gap={16}>
      <FolderSection
        folders={folders}
        count={total(files)}
        selected={selected}
        onSelect={onSelect}
        onOpen={onOpen}
        onFile={onFile}
        onDismissed={onDismissed}
      />

      <Section
        title="Sets of files Fonoteca could not place"
        note="Files the passes decided together and refused together, because they share the recordings they were compared against. Open one and Fonoteca asks MusicBrainz again for the whole set, then scores every album that comes back against what you actually hold."
        count={total(albums)}
        groups={albums}
        onOpen={onOpen}
      />
    </Stack>
  )
}

/**
 * The by-hand half, and the only place on this screen a release can be searched for.
 *
 * **Why it is a section of its own and not a third `Section`.** Every other
 * group here is a refusal with a candidate set behind it, recovered from
 * something a pass wrote down; the label a person needs is *why the rule gave
 * up*. These files have nothing written down about them at all — measured on the
 * target library, not one of the six hundred and ninety-six holds a MusicBrainz
 * recording — so the useful label is not the refusal but the folder, and the
 * useful action is not "look at what Fonoteca found" but "tell it what this is".
 *
 * The refusals have not been thrown away: each row still carries its own, and
 * the file dialog behind a filename still offers the AcoustID re-ask for the two
 * that have one. What changed is that the refusal is no longer the thing the
 * page is organised around, because organising by it is what made an album
 * unmatchable.
 */
function FolderSection({
  folders,
  count,
  selected,
  onSelect,
  onOpen,
  onFile,
  onDismissed,
}: {
  readonly folders: readonly Folder[]
  readonly count: OpenQuestionCount | undefined
  readonly selected: ReadonlySet<string>
  readonly onSelect: (next: ReadonlySet<string>) => void
  readonly onOpen: (subject: MatchingSubjectRef) => void
  readonly onFile: (filing: Filing) => void
  readonly onDismissed: () => void
}) {
  if (folders.length === 0) return null

  const shown = folders.slice(0, FOLDER_ROWS)

  return (
    <Card
      className={styles.yours}
      title="Albums to match by hand"
      aside={count !== undefined ? <SectionCount count={count} /> : null}
    >
      <Stack direction="column" gap={12} align="start">
        <Text size="sm" tone="secondary" block>
          Files grouped by the folder they sit in, whatever went wrong for each one — separate discs
          of a set are one group. Nothing here can be recovered from a fingerprint: AcoustID either
          has never heard this audio or knows it and links it to no recording, so the only thing
          that can settle these is somebody who knows what the album is. Tick the files, find the
          release on MusicBrainz, check the pairing and file them.
        </Text>

        <Stack direction="column" gap={4} className={styles.groups}>
          {shown.map((folder) => (
            <FolderGroup
              key={folder.path}
              folder={folder}
              selected={selected}
              onSelect={onSelect}
              onOpen={onOpen}
              onFile={onFile}
              onDismissed={onDismissed}
            />
          ))}
        </Stack>

        {shown.length < folders.length ? (
          <Text size="xs" tone="warning" block>
            Showing {shown.length.toLocaleString()} of {folders.length.toLocaleString()} folders.
            The rest are smaller and hold the same kinds of question.
          </Text>
        ) : null}
      </Stack>
    </Card>
  )
}

/**
 * One album folder: the files in it, what refused each, and the button that
 * settles the lot.
 *
 * Closed by default, like every group on this page, and for the same reason — a
 * hundred and twenty-five folders that all insist on printing themselves is the
 * flat list again with extra headings. What survives the collapse is the folder,
 * the file count and the refusals in it, which is enough to decide whether this
 * is the album you came for.
 */
function FolderGroup({
  folder,
  selected,
  onSelect,
  onOpen,
  onFile,
  onDismissed,
}: {
  readonly folder: Folder
  readonly selected: ReadonlySet<string>
  readonly onSelect: (next: ReadonlySet<string>) => void
  readonly onOpen: (subject: MatchingSubjectRef) => void
  readonly onFile: (filing: Filing) => void
  readonly onDismissed: () => void
}) {
  const root = useRef<HTMLDivElement>(null)

  const [dismissal, setDismissal] = useState<'idle' | 'sending' | string>('idle')
  const [reopening, setReopening] = useState<'idle' | 'sending' | string>('idle')

  /*
    Whether this folder has ever been open, which is what gates the read of its
    whole contents.

    `Disclosure` renders its children whether it is open or not — closed is
    `hidden`, not unmounted — so a fetch mounted unconditionally would be a
    hundred and thirteen requests on a page nobody has interacted with. Sticky
    rather than tracking the open state, so collapsing and reopening a folder
    does not re-read it.
  */
  const [opened, setOpened] = useState(false)

  const ticked = folder.questions.filter((question) => selected.has(question.id))

  // "All selected, or all of them if none are." The fallback is what makes the
  // ordinary case — a folder that is one album, entirely — a two-click job
  // rather than thirty ticks and a click.
  const filing = ticked.length > 0 ? ticked : folder.questions

  function set(ids: readonly string[], on: boolean) {
    const next = new Set(selected)

    for (const id of ids) {
      if (on) next.add(id)
      else next.delete(id)
    }

    onSelect(next)
  }

  const allTicked = ticked.length === folder.questions.length

  /**
   * Whether this group's path is safe to dismiss whole.
   *
   * The button sends a path and the server closes everything beneath it, so the
   * two have to mean the same set. They do at `Artist/Album`, which is what
   * {@link albumFolderOf} cuts to and what every group on this screen is. They
   * do not at `Artist` — that function cuts a path down to two segments and does
   * not pad a shorter one up to them, so a stray file sitting directly under an
   * artist folder produces a one-segment group, and `Artist/` as a prefix is
   * every album that artist has. Rare, and answered file by file instead of by
   * sweeping a discography on a click that promised six files.
   */
  const markable = folder.path.split('/').length >= ALBUM_FOLDER_DEPTH

  /**
   * The other answer: this folder is nobody's release.
   *
   * Some folders are somebody's own compilation — tracks pulled off YouTube, a
   * mixtape, a rip of a set never issued as an album — and no amount of
   * searching will find them, because there is nothing to find. Without this the
   * folder sits here forever and three passes go on re-asking about it at the
   * rate limit.
   *
   * Confirmed through the browser's own dialog rather than a built one: it is a
   * bulk write over every open file under a path, undone only by hand, and the
   * whole of what a person needs to read before agreeing to it is one sentence.
   */
  async function dismiss() {
    const count = folder.questions.length

    if (
      !window.confirm(
        `Mark “${folder.path}” as coming from no release?\n\n` +
          `The ${count} open file${count === 1 ? '' : 's'} here — and anything else under that ` +
          'path Fonoteca still has a question about — stop being asked about, here and by every ' +
          'pass. Files Fonoteca has already matched are left exactly as they are, and nothing ' +
          'on disk is touched.',
      )
    ) {
      return
    }

    setDismissal('sending')

    try {
      await api.post('/api/catalogue/matching/folders/unreleased', {
        json: { folder: folder.path },
      })

      onDismissed()
    } catch (cause: unknown) {
      setDismissal(describeError(cause))
    }
  }

  /**
   * The third answer: the passes matched this folder to the wrong thing.
   *
   * The only one of the three that is not about the files on this screen. A
   * folder arrives here showing the files a pass *refused* — one leftover of an
   * album that was otherwise placed correctly, most of the time. But sometimes
   * the placement itself is wrong: a live set whose songs AcoustID matched to
   * the studio recordings of the same titles is filed, linked, off every
   * worklist and readable as a finished album until somebody plays it. The
   * measured example is a 2019 concert where thirteen of sixteen files went to
   * the studio album of the same name, leaving three on this screen — which is
   * exactly how it looks: a folder that says three when it means sixteen.
   *
   * So this reaches past the rows shown. Every file under the path that a pass
   * placed gives up its recording and album and joins the question.
   */
  async function reopen() {
    if (
      !window.confirm(
        `Ask “${folder.path}” again from scratch?\n\n` +
          'Every file under that path that Fonoteca matched gives up its recording, its album ' +
          'and its track, and the whole folder comes back here as one question for you. Files ' +
          'already waiting for an answer keep the answer they are waiting for. No pass will ' +
          'match them again — this is undone by matching them yourself. Nothing on disk is ' +
          'touched.',
      )
    ) {
      return
    }

    setReopening('sending')

    try {
      await api.post('/api/catalogue/matching/folders/reopen', {
        json: { folder: folder.path },
      })

      onDismissed()
    } catch (cause: unknown) {
      setReopening(describeError(cause))
    }
  }

  return (
    <Disclosure
      ref={root}
      size="sm"
      onOpenChange={(open) => {
        if (!open) return

        setOpened(true)
        root.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
      }}
      summary={
        <Text size="sm" weight="medium" family="mono">
          {folder.path === '' ? 'At the library root' : folder.path}
        </Text>
      }
      aside={
        <Badge tone={ticked.length > 0 ? 'accent' : 'neutral'} size="sm" mono>
          {ticked.length > 0
            ? `${ticked.length} of ${folder.questions.length} ticked`
            : `${folder.questions.length} open`}
        </Badge>
      }
      detail={
        <Text size="xs" tone="tertiary" block>
          {refusalLine(folder.questions)}
        </Text>
      }
    >
      <Stack direction="column" gap={12} align="start" className={styles.panel}>
        <Stack gap={12} align="center" wrap>
          <Button
            size="sm"
            variant="primary"
            aria-haspopup="dialog"
            onClick={() => {
              onFile({ folder: folder.path, questions: filing })
            }}
          >
            Match {filing.length === folder.questions.length ? 'all' : `${filing.length}`} to an
            album…
          </Button>

          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              set(
                folder.questions.map((question) => question.id),
                !allTicked,
              )
            }}
          >
            {allTicked ? 'Untick all' : 'Tick all'}
          </Button>

          <Button
            size="sm"
            variant="ghost"
            disabled={!markable || dismissal === 'sending'}
            onClick={() => {
              void dismiss()
            }}
          >
            {dismissal === 'sending' ? 'Marking…' : 'Not a release'}
          </Button>

          <Button
            size="sm"
            variant="ghost"
            disabled={!markable || reopening === 'sending'}
            onClick={() => {
              void reopen()
            }}
          >
            {reopening === 'sending' ? 'Reopening…' : 'Wrong match — ask again'}
          </Button>
        </Stack>

        <AddToMusicBrainz folder={folder.path} enabled={markable} />

        {dismissal !== 'idle' && dismissal !== 'sending' ? (
          <Text size="xs" tone="warning" block>
            {dismissal}
          </Text>
        ) : null}

        {reopening !== 'idle' && reopening !== 'sending' ? (
          <Text size="xs" tone="warning" block>
            {reopening}
          </Text>
        ) : null}

        {opened && markable ? (
          <FolderContents
            folder={folder.path}
            questions={folder.questions}
            selected={selected}
            onTick={(id, on) => {
              set([id], on)
            }}
            onOpen={onOpen}
          />
        ) : (
          <OpenRows
            folder={folder.path}
            questions={folder.questions}
            selected={selected}
            onTick={(id, on) => {
              set([id], on)
            }}
            onOpen={onOpen}
          />
        )}
      </Stack>
    </Disclosure>
  )
}

/**
 * The whole folder, matched files and all.
 *
 * **The worklist prints the leftovers, and for a wrongly-matched album the
 * leftovers are the smaller half.** The measured example is a 2019 concert
 * where thirteen of sixteen files were matched to the studio album of the same
 * name: the three the pass refused are the group above, the thirteen wrong ones
 * are on no screen in the application, and the folder therefore reads as a
 * three-file album. "Wrong match — ask again" is right there and there is
 * nothing on screen to tell somebody they need it.
 *
 * So the rows are the directory rather than the queue. An open question keeps
 * its tick, its refusal and its dialog; a file that is already matched shows
 * what it was matched to and cannot be ticked, because the filing endpoint
 * skips a file that is not an open question and a tick that silently does
 * nothing is worse than no tick.
 *
 * **Read on first open, never on render.** One request per folder somebody
 * actually looks at — see the `opened` flag above — and only for a path that is
 * an album folder. A one-segment group is a stray file sitting directly under an
 * artist, where the prefix is the whole discography and the listing would be
 * hundreds of unrelated files reported as this album's; the library root is
 * refused by the endpoint outright. Both keep the plain list of open questions,
 * which is what they had before.
 *
 * While it is loading, and if it fails, the open questions are still listed:
 * this adds to the group, so a slow read must not take away what the group
 * could already show.
 */
function FolderContents({
  folder,
  questions,
  selected,
  onTick,
  onOpen,
}: {
  readonly folder: string
  readonly questions: readonly OpenQuestion[]
  readonly selected: ReadonlySet<string>
  readonly onTick: (id: string, on: boolean) => void
  readonly onOpen: (subject: MatchingSubjectRef) => void
}) {
  /*
    The folder, and the open questions in it, as this read's identity.

    **Filing is what makes the second half necessary.** A committed decision
    bumps the page's one counter, which re-reads the *worklist* — so the files
    just answered leave `questions` and their rows flip from a tick to a placed
    row, rendered from a listing fetched before the write. They would print
    "matched to nothing yet" about the tracks somebody had just matched, on the
    feature whose whole job is saying what is matched. The ids are the honest
    dependency rather than the counter: this listing goes stale exactly when the
    set of open questions under it moves, which is also what reopening a folder
    does.
  */
  const asking = questions.map((question) => question.id).join(' ')

  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/matching/folders/files', {
        params: { query: { folder } },
      }),
    [folder, asking],
  )

  if (state.status !== 'ready') {
    return (
      <>
        <OpenRows
          folder={folder}
          questions={questions}
          selected={selected}
          onTick={onTick}
          onOpen={onOpen}
        />

        {state.status === 'loading' ? (
          <Text size="xs" tone="tertiary" block>
            Reading the rest of the folder…
          </Text>
        ) : (
          <Text size="xs" tone="warning" block>
            The rest of the folder could not be read: {state.message}
          </Text>
        )}
      </>
    )
  }

  const { items, files, open } = state.data

  // Keyed on the media file id rather than on the question id, because the two
  // lists come from two endpoints that name the same file differently: the
  // worklist says `recording:{guid}` and this one says the guid.
  const asked = new Map(
    questions.flatMap((question) => {
      const id = mediaFileIdOf(question)

      return id === null ? [] : [[id, question] as const]
    }),
  )

  return (
    <>
      <Text size="xs" tone="secondary" block>
        {files.toLocaleString()} file{files === 1 ? '' : 's'} in this folder — {open} still waiting
        for an answer, {(files - open).toLocaleString()} already matched, both counted over the
        whole folder. The matched ones are listed so a wrong one is visible; if this album went to
        the wrong record, “Wrong match — ask again” brings the whole folder back here.
      </Text>

      <ul className={styles.rows} aria-label={`Files in ${folder}`}>
        {items.map((item) => {
          const question = asked.get(item.mediaFileId)

          return (
            <li key={item.mediaFileId}>
              {question === undefined ? (
                <PlacedRow item={item} folder={folder} />
              ) : (
                <FileRow
                  question={question}
                  folder={folder}
                  ticked={selected.has(question.id)}
                  onTick={(on) => {
                    onTick(question.id, on)
                  }}
                  onOpen={onOpen}
                />
              )}
            </li>
          )
        })}
      </ul>

      {items.length < files ? (
        <Text size="xs" tone="warning" block>
          Showing {items.length.toLocaleString()} of {files.toLocaleString()} files. This path holds
          more than one album's worth, so it is probably a level above the album.
        </Text>
      ) : null}
    </>
  )
}

/** The open questions in a folder, which is what the group could always show. */
function OpenRows({
  folder,
  questions,
  selected,
  onTick,
  onOpen,
}: {
  readonly folder: string
  readonly questions: readonly OpenQuestion[]
  readonly selected: ReadonlySet<string>
  readonly onTick: (id: string, on: boolean) => void
  readonly onOpen: (subject: MatchingSubjectRef) => void
}) {
  return (
    <ul className={styles.rows} aria-label={`Files in ${folder}`}>
      {questions.map((question) => (
        <li key={question.id}>
          <FileRow
            question={question}
            folder={folder}
            ticked={selected.has(question.id)}
            onTick={(on) => {
              onTick(question.id, on)
            }}
            onOpen={onOpen}
          />
        </li>
      ))}
    </ul>
  )
}

/**
 * One file that is not an open question: what it was matched to, and by whom.
 *
 * **No tick and no dialog, deliberately.** `POST matching/files/release` skips a
 * file that is not an open question — it answers refusals, it does not overrule
 * decisions — so a checkbox here would send a pair the server drops, and the
 * screen would report a filing that did not happen to this row. The way to
 * change one of these is to reopen the folder, which is a button on the group
 * above and says exactly what it does.
 *
 * A row with no album is not a mistake either: attribution refuses sets it
 * cannot place, so a file can be identified, linked and on no release at all.
 * The certainty badge is what says which of those happened.
 */
function PlacedRow({ item, folder }: { readonly item: FolderFile; readonly folder: string }) {
  const certainty = CERTAINTY[item.certainty]
  const tail = item.folder === folder ? null : item.folder.slice(folder.length + 1)

  const seat =
    item.disc !== null && item.position !== null
      ? `${item.disc > 1 ? `Disc ${item.disc} · ` : ''}Track ${item.position}`
      : null

  return (
    <div className={styles.placedRow}>
      <Stack direction="column" gap={2} align="start" className={styles.row}>
        <Stack gap={8} align="baseline" wrap>
          <Text size="sm" family="mono" tone="secondary" block>
            {item.name}
          </Text>

          {item.length !== null ? (
            <Text size="xs" tone="tertiary" family="mono">
              {item.length}
            </Text>
          ) : null}

          {certainty !== undefined && certainty.label !== 'Certain' ? (
            <Badge tone={certainty.tone === 'ok' ? 'success' : certainty.tone} size="sm">
              {certainty.short}
            </Badge>
          ) : null}
        </Stack>

        <Text size="xs" tone="tertiary" block>
          {item.release === null ? (
            item.recording === null ? (
              'Matched to nothing yet'
            ) : (
              <>Identified as “{item.recording}”, on no album</>
            )
          ) : (
            <>
              {seat === null ? '' : `${seat} · `}
              {item.track ?? item.recording ?? 'Untitled'} —{' '}
              {item.releaseId === null ? (
                item.release
              ) : (
                <Link to="/library/releases/$releaseId" params={{ releaseId: item.releaseId }}>
                  {item.release}
                </Link>
              )}
              {item.year === null ? '' : ` (${item.year})`}
            </>
          )}
        </Text>

        {tail !== null && tail !== '' ? (
          <Text size="xs" tone="tertiary" family="mono" block>
            {tail}
          </Text>
        ) : null}
      </Stack>
    </div>
  )
}

/**
 * What went wrong in this folder, said once however many files it happened to.
 *
 * The refusal has not stopped mattering — "AcoustID has never heard any of
 * this" and "AcoustID knows all of it and MusicBrainz links none of it" are
 * different facts about an album, and the second means somebody could fix it
 * upstream. It has stopped being the *heading*, which is different.
 */
function refusalLine(questions: readonly OpenQuestion[]): string {
  const counts = new Map<string, number>()

  for (const question of questions) {
    counts.set(question.reason, (counts.get(question.reason) ?? 0) + 1)
  }

  return [...counts]
    .sort((left, right) => right[1] - left[1])
    .map(([reason, files]) =>
      counts.size === 1 ? whyOpen(reason).label : `${whyOpen(reason).label} (${files})`,
    )
    .join(' · ')
}

/**
 * One file in a folder: a tick, a name that opens it, and what refused it.
 *
 * The checkbox is a bare `<input type="checkbox">` inside its own `<label>`.
 * There is no checkbox in the design system, and this is not the reason to add
 * one: the native control already carries the role, the state, the keyboard
 * behaviour and the label association that a hand-built one would have to
 * reimplement and have tested against axe.
 *
 * It sits *beside* the button rather than inside it, because a button
 * containing a checkbox is one control claiming to be two — clicking the tick
 * would open the dialog, and neither the mouse nor the keyboard could reach the
 * tick on its own.
 */
function FileRow({
  question,
  folder,
  ticked,
  onTick,
  onOpen,
}: {
  readonly question: OpenQuestion
  readonly folder: string
  readonly ticked: boolean
  readonly onTick: (on: boolean) => void
  readonly onOpen: (subject: MatchingSubjectRef) => void
}) {
  const why = whyOpen(question.reason)

  return (
    <Stack gap={8} align="start" className={styles.fileRow}>
      <label className={styles.tick}>
        <input
          type="checkbox"
          checked={ticked}
          onChange={(event) => {
            onTick(event.currentTarget.checked)
          }}
        />
        <VisuallyHidden>Include {question.subject}</VisuallyHidden>
      </label>

      <button
        type="button"
        className={styles.rowButton}
        aria-haspopup="dialog"
        onClick={() => {
          onOpen({ source: 'catalogue', question })
        }}
      >
        <Stack direction="column" gap={2} align="start" className={styles.row}>
          <Stack gap={8} align="baseline" wrap>
            <Text size="sm" family="mono" block>
              {question.subject}
            </Text>

            {fileFacts(question) !== null ? (
              <Text size="xs" tone="tertiary" family="mono">
                {fileFacts(question)}
              </Text>
            ) : null}

            <Badge tone={why.tone} size="sm">
              {why.label}
            </Badge>
          </Stack>

          {/*
            Only where it differs from the group's own heading — which is where
            it is worth reading. A two-disc set prints `CD1` and `CD2` here and
            nothing at all on the ordinary album, where every row would otherwise
            repeat the folder name above it.
          */}
          {folderTail(question, folder) !== null ? (
            <Text size="xs" tone="tertiary" family="mono" block>
              {folderTail(question, folder)}
            </Text>
          ) : null}
        </Stack>
      </button>
    </Stack>
  )
}

/** Whatever of a file's folder the group heading does not already say. */
function folderTail(question: OpenQuestion, folder: string): string | null {
  const own = question.folders[0] ?? ''
  if (own === folder) return null

  return own.startsWith(`${folder}/`) ? own.slice(folder.length + 1) : own
}

/**
 * A section's size, summed from the refusals in it rather than from the
 * endpoint's `kinds`.
 *
 * The sections no longer line up with the two units — "you can settle these" and
 * "waiting on somebody else" are both `recording` — so the endpoint's own split
 * can no longer supply their totals. Summed from the same `reasons` counts the
 * badges use, which are over the whole worklist rather than over the rows a
 * group chose to print.
 */
function total(groups: readonly Group[]): OpenQuestionCount | undefined {
  if (groups.length === 0) return undefined

  return groups.reduce<OpenQuestionCount>(
    (sum, group) => ({
      name: sum.name,
      questions: sum.questions + group.reason.questions,
      files: sum.files + group.reason.files,
    }),
    { name: 'section', questions: 0, files: 0 },
  )
}

function Section({
  title,
  note,
  count,
  groups,
  onOpen,
}: {
  readonly title: string
  readonly note: string
  readonly count: OpenQuestionCount | undefined
  readonly groups: readonly Group[]
  readonly onOpen: (subject: MatchingSubjectRef) => void
}) {
  // A section with nothing in it is not an empty state, it is a section that
  // does not apply — every file identified, or attribution not yet run.
  if (groups.length === 0) return null

  return (
    <Card title={title} aside={count !== undefined ? <SectionCount count={count} /> : null}>
      <Stack direction="column" gap={12} align="start">
        <Text size="sm" tone="secondary" block>
          {note}
        </Text>

        <Stack direction="column" gap={4} className={styles.groups}>
          {groups.map((group) => (
            <ReasonGroup key={group.reason.name} group={group} onOpen={onOpen} />
          ))}
        </Stack>
      </Stack>
    </Card>
  )
}

/**
 * A section's size, in both units where they differ.
 *
 * They differ only for the release section, where one question is a rip. Saying
 * "2 · 31 files" there and "695 files" below is the whole distinction the two
 * sections exist to draw, and printing "695 questions, 695 files" would bury it
 * in a repetition.
 */
function SectionCount({ count }: { readonly count: OpenQuestionCount }) {
  return (
    <Badge tone="neutral" size="sm" mono>
      {count.questions === count.files
        ? `${count.files.toLocaleString()} file${count.files === 1 ? '' : 's'}`
        : `${count.questions.toLocaleString()} set${count.questions === 1 ? '' : 's'} · ${count.files.toLocaleString()} files`}
    </Badge>
  )
}

/**
 * One refusal, said once, closed by default.
 *
 * Closed is the point. The whole reason a person could not read this page was
 * that every group insisted on printing itself, and the four hundred and
 * fifty-four rows of "AcoustID knows this, MusicBrainz does not link it" are the
 * same fact four hundred and fifty-four times. Collapsed, the group is a label,
 * a count and a sentence about whether it is anybody's to fix — which is all
 * that fact ever was.
 *
 * **Those three survive the collapse and the explanation does not**, which is
 * the whole division of labour here. `next` says whether to open this at all, so
 * it is the disclosure's `detail` rather than part of its panel; `note` says why
 * the pass refused, which is what somebody who opened it came to read.
 *
 * The counts are the whole worklist's, from the endpoint's own grouping, not a
 * count of the rows below them.
 */
function ReasonGroup({
  group,
  onOpen,
}: {
  readonly group: Group
  readonly onOpen: (subject: MatchingSubjectRef) => void
}) {
  const { reason, questions, kind } = group
  const why = whyOpen(reason.name)

  const shown = questions.slice(0, ALBUM_ROWS)

  const root = useRef<HTMLDivElement>(null)

  return (
    <Disclosure
      ref={root}
      size="sm"
      /*
        Opening the last group on the page used to do nothing visible: the rows
        unfold below the fold, the page does not move, and the click reads as
        broken. `nearest` rather than `start` so a group already in view is left
        exactly where it is — scrolling a heading somebody is looking at to the
        top of the window is its own small betrayal.
      */
      onOpenChange={(open) => {
        if (open) root.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
      }}
      summary={
        <Text size="sm" weight="medium">
          {why.label}
        </Text>
      }
      aside={<ReasonCount reason={reason} tone={why.tone} />}
      // The half the old page never had, and it stays visible when the group is
      // shut. The note says why the pass refused; this says whether it is
      // anybody's to fix — and for most of these the honest answer is "no",
      // which is a decision a person can act on where a bare refusal is not.
      // Behind the disclosure it would only reach somebody who had already
      // decided to open fifty rows to find out.
      {...(why.next
        ? {
            detail: (
              <Text size="xs" tone="tertiary" block>
                {why.next}
              </Text>
            ),
          }
        : {})}
    >
      <Stack direction="column" gap={12} align="start" className={styles.panel}>
        {why.note ? (
          <Text size="sm" tone="secondary" block>
            {why.note}
          </Text>
        ) : null}

        {shown.length > 0 ? (
          <ul className={styles.rows} aria-label={why.label}>
            {shown.map((question, index) => (
              <li key={question.id}>
                <QuestionRow
                  question={question}
                  // The rows arrive ordered by path, so a folder holding fifteen
                  // unidentified files prints its name fifteen times — which is
                  // the same fact fifteen times, and it drowns the filenames
                  // that actually differ. Said once, at the point it changes.
                  repeatsFolder={sameFolder(shown[index - 1], question)}
                  onOpen={onOpen}
                />
              </li>
            ))}
          </ul>
        ) : null}

        {shown.length < reason.questions ? (
          <Text size="xs" tone="warning" block>
            Showing {shown.length.toLocaleString()} of {reason.questions.toLocaleString()}.{' '}
            {kind === 'release'
              ? 'The rest are the same refusal over other sets of files.'
              : 'The rest are the same question in other folders.'}
          </Text>
        ) : null}
      </Stack>
    </Disclosure>
  )
}

/**
 * A refusal's size, in both units where they differ.
 *
 * They differ only for the attribution refusals, where one question is a rip:
 * `NoConfidentFit` is a hundred and seventeen decisions over five hundred and
 * four files, and a badge saying `117` alone hides how much of the library is
 * behind it — which is the number that decides whether it is worth an afternoon.
 * For the file-level refusals the two are equal by construction, so it prints
 * one number rather than the same number twice.
 */
function ReasonCount({
  reason,
  tone,
}: {
  readonly reason: OpenQuestionCount
  readonly tone: OpenReason['tone']
}) {
  if (reason.questions === reason.files) {
    return (
      <Badge tone={tone} size="sm" mono>
        {reason.files.toLocaleString()} file{reason.files === 1 ? '' : 's'}
      </Badge>
    )
  }

  return (
    <Badge tone={tone} size="sm" mono>
      {reason.questions.toLocaleString()} set{reason.questions === 1 ? '' : 's'} ·{' '}
      {reason.files.toLocaleString()} files
    </Badge>
  )
}

/**
 * One question, as a row.
 *
 * A button rather than a link, because what it opens is a dialog rather than a
 * place. The old version of this row was deliberately inert, and the argument
 * for that still stands as far as it went — a link that leads to an empty list
 * of candidates is indistinguishable from a broken one. What answers it is that
 * the dialog never shows an empty one: where a candidate set can be recovered —
 * the two identification refusals, re-asked from the stored fingerprint — it
 * shows the real thing, and where it cannot it shows the evidence the catalogue
 * holds and says plainly that the set was never recorded. Either way that is a
 * destination, and a person arriving at a refusal is entitled to see what is
 * behind it.
 */
function QuestionRow({
  question,
  repeatsFolder,
  onOpen,
}: {
  readonly question: OpenQuestion
  readonly repeatsFolder: boolean
  readonly onOpen: (subject: MatchingSubjectRef) => void
}) {
  return (
    <button
      type="button"
      className={styles.rowButton}
      aria-haspopup="dialog"
      onClick={() => {
        onOpen({ source: 'catalogue', question })
      }}
    >
      <Stack direction="column" gap={2} align="start" className={styles.row}>
        <Stack gap={8} align="baseline" wrap>
          <Text size="sm" family="mono" block>
            {question.subject}
          </Text>

          {/*
            Only where it means something. Every recording question is one file,
            so a "1 file" chip on fifty rows would be fifty repetitions of the
            row's own kind.
          */}
          {question.files > 1 ? (
            <Badge tone="neutral" size="sm" mono>
              {question.files} files
            </Badge>
          ) : null}

          {/*
            What the file is, before anybody opens it. All three come out of the
            catalogue — length is the duration `fpcalc` already measured — so the
            worklist still opens no files, and a row is no longer just a name.
            Bitrate and the file's own tags need the bytes and live one click
            away, on the file itself.
          */}
          {fileFacts(question) !== null ? (
            <Text size="xs" tone="tertiary" family="mono">
              {fileFacts(question)}
            </Text>
          ) : null}
        </Stack>

        {/*
          Where it is on disk, and nothing more is claimed for it. The folders
          were never consulted when deciding — on the documented failure the
          folder says 1979 while the audio is the 2015 remaster — so this is here
          because it is how a person finds the music, not because it is evidence.
        */}
        {/*
          Hidden from the eye when it repeats, never removed. A row read aloud
          has no column above it to inherit the folder from, so dropping the
          element would take the file's location away from exactly the reader who
          cannot glance up two rows to find it.
        */}
        {repeatsFolder ? (
          <VisuallyHidden>{folderLine(question)}</VisuallyHidden>
        ) : (
          <Text size="xs" tone="tertiary" family="mono" block>
            {folderLine(question)}
          </Text>
        )}
      </Stack>
    </button>
  )
}

/**
 * Length, size and container, for the rows that are one file.
 *
 * Null on a component, and not because the numbers are unavailable: a set of
 * files has a total runtime and a total size, and neither is a fact about the
 * music. An album's length belongs to the release, not to the rip.
 */
function fileFacts(question: OpenQuestion): string | null {
  const parts = [question.length, question.size, question.format].filter(
    (part) => part != null && part !== '',
  )

  return parts.length === 0 ? null : parts.join(' · ')
}

/** Where the files are, in one line, however many places that is. */
function folderLine(question: OpenQuestion): string {
  const [first, ...rest] = question.folders

  if (first === undefined) return 'At the library root'
  if (rest.length === 0) return first

  return `${first} and ${rest.length} more folder${rest.length === 1 ? '' : 's'}`
}

/**
 * Whether two consecutive rows sit in exactly the same place.
 *
 * Exact rather than "shares a prefix": a folder and its subfolder are two
 * places, and collapsing them would put a row under a heading that is not where
 * its file is. Compared as a joined string because a component carries several
 * folders and the whole set has to match, in order, for the line to be a repeat.
 */
function sameFolder(previous: OpenQuestion | undefined, question: OpenQuestion): boolean {
  return previous !== undefined && previous.folders.join(' ') === question.folders.join(' ')
}

function Empty() {
  return (
    <Stack direction="column" gap={4} align="start">
      <Text tone="tertiary" block>
        Nothing is open.
      </Text>
      <Text size="sm" tone="tertiary" block>
        Either every file the passes have reached was decided, or the passes have not run yet — a
        file nothing has asked about is a queue position rather than a question, and does not appear
        here. Run them from <Link to="/">Foundation</Link>.
      </Text>
    </Stack>
  )
}

/**
 * The answer screen, against the cases it was built for.
 *
 * **Back below the fold, and collapsed.** These were moved to the top when the
 * real questions had nothing to choose between — at that point the fixtures were
 * the only working thing on the page. They are not any more: the first section
 * is a real chooser over real files. What being first bought instead was a new
 * reader clicking invented data before they ever reached their own library, with
 * only a small grey word above it to say so.
 *
 * Still reachable, still plainly labelled, and the label is now on the closed
 * summary rather than only on the panel: the honesty is the whole reason they
 * are allowed to stay.
 */
function WorkedExamples({ onOpen }: { readonly onOpen: (subject: MatchingSubjectRef) => void }) {
  return (
    <div className={styles.preview}>
      <Disclosure
        size="sm"
        summary={
          <Text size="sm" weight="medium">
            Worked examples
          </Text>
        }
        aside={
          <Badge tone="neutral" size="sm">
            Made-up data
          </Badge>
        }
        detail={
          <Text size="xs" tone="tertiary" block>
            Three invented hard cases. Nothing here is from your library.
          </Text>
        }
      >
        <Stack direction="column" gap={8} align="start" className={styles.previewBody}>
          <Text size="sm" tone="secondary" block>
            The album-shaped answer screen, built against three documented hard cases before
            anything in your library could reach it. Kept because it is the only way to see that
            screen — and labelled, every time, because invented data that looks real is worse than
            no example at all.
          </Text>

          <Stack gap={16} wrap>
            {MATCHING_QUESTIONS.map((question) => (
              <button
                key={question.id}
                type="button"
                className={styles.exampleButton}
                aria-haspopup="dialog"
                onClick={() => {
                  onOpen({ source: 'fixture', question })
                }}
              >
                <Text size="sm">{question.subjectLine}</Text>
              </button>
            ))}
          </Stack>
        </Stack>
      </Disclosure>
    </div>
  )
}

/**
 * Entering a concert MusicBrainz has never heard of.
 *
 * **The step before every other button on this screen can do anything.** Search,
 * candidates, seating and the fingerprint contribution all assume the album is
 * in MusicBrainz and the only question is which one. For a concert nobody has
 * entered there is nothing to search for, nothing to seat onto, and therefore no
 * recording to bind a fingerprint to — the folder is stuck at step zero, and no
 * amount of re-asking any provider moves it.
 *
 * **Fonoteca does not write to MusicBrainz, and this does not either.** Their
 * write API cannot create a release at all; the supported path — Picard's path —
 * is to POST the fields to their own release editor and let the person's browser
 * open it, signed in as them. So this fetches the seed, shows what is in it, and
 * then submits a form. The edit is theirs.
 *
 * **Two presses, not one, and the reason is mechanical as well as editorial.**
 * Reading a folder's tags is an `ffprobe` per file — five seconds for a
 * thirteen-track concert — and a window opened after an `await` is a pop-up as
 * far as a browser is concerned, blocked by default. Submitting the form from a
 * *second* click keeps it inside a gesture. That it also puts the track list in
 * front of a person before it goes to a public database is the better half of
 * the argument.
 */
function AddToMusicBrainz({
  folder,
  enabled,
}: {
  readonly folder: string
  readonly enabled: boolean
}) {
  const [state, setState] = useState<
    | { readonly status: 'idle' | 'reading' }
    | { readonly status: 'ready'; readonly seed: ReleaseSeedResponse }
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
          Read from the files themselves — titles from their tags, lengths measured by the decoder
          rather than taken from the container. MusicBrainz opens in a new tab with all of it filled
          in, under your account, and nothing is submitted until you press Save there. It is seeded
          as a <strong>bootleg</strong>, which is what an unissued concert recording is; change it
          in the editor if this one was actually released.
          {seed.unmeasuredTracks > 0
            ? ` ${seed.unmeasuredTracks} of them have no length, so you will need to fill those in.`
            : ''}
        </Text>

        <Stack gap={12} align="center" wrap>
          <Button
            size="sm"
            variant="primary"
            onClick={() => {
              openEditor(seed, folder)
            }}
          >
            Open the MusicBrainz editor
          </Button>

          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              setState({ status: 'idle' })
            }}
          >
            Cancel
          </Button>
        </Stack>
      </Stack>
    )
  }

  return (
    <Stack direction="column" gap={4} align="start">
      <Button
        size="sm"
        variant="ghost"
        disabled={!enabled || state.status === 'reading'}
        onClick={() => {
          void read()
        }}
      >
        {state.status === 'reading' ? 'Reading the files…' : 'Add to MusicBrainz…'}
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
 * Hands the seed to MusicBrainz's release editor.
 *
 * A real form POST rather than a fetch, and that is the whole trick: the person
 * has to *arrive* at musicbrainz.org carrying this data, signed in as
 * themselves. A cross-origin fetch would be sending it from Fonoteca, which is
 * both blocked and wrong — nothing here is entitled to submit an edit.
 *
 * `redirect_uri` is the return address. MusicBrainz appends `release_mbid` to it
 * once the edit is saved, so the new tab comes back to this screen knowing which
 * release was created and, from `folder`, which question it answers. If that
 * ever stops happening the MBID is still in the address bar of the tab they are
 * standing in, and pasting it into the search box is the same lookup.
 */
function openEditor(seed: ReleaseSeedResponse, folder: string): void {
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
