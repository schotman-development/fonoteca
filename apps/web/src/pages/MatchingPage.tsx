import type { components } from '@fonoteca/api-client'
import { Badge, Card, Disclosure, Stack, Text, VisuallyHidden } from '@fonoteca/ui'
import { Link } from '@tanstack/react-router'
import { useRef, useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import { MatchingDialog, type MatchingSubjectRef } from './MatchingDialog.tsx'
import styles from './MatchingPage.module.css'
import { MATCHING_QUESTIONS } from './matchingFixtures.ts'
import { isAnswerable, kindOf, type OpenKind, type OpenReason, whyOpen } from './openQuestions.ts'

type OpenQuestion = components['schemas']['OpenQuestion']
type OpenQuestionCount = components['schemas']['OpenQuestionCount']
type MatchingQueue = components['schemas']['MatchingQueueResponse']

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
const TRACK_ROWS = 50

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

        {state.status === 'ready' ? (
          <Summary kinds={state.data.kinds} reasons={state.data.reasons} />
        ) : null}
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
                away. So the question can only be put back to you where the list of answers can be
                rebuilt.
              </Text>
              <Text size="sm" tone="secondary" block>
                For a single file it can: the fingerprint is stored, so Fonoteca asks AcoustID the
                same question again for one request and no disk access. That is what the first
                section below offers. Your answer is recorded as a person’s decision, so no later
                pass overwrites it.
              </Text>
              <Text size="sm" tone="secondary" block>
                For a set of files it can too, and that costs more: Fonoteca asks MusicBrainz about
                each recording in the set, then fetches the track lists of the albums that come back
                so it can score them against what you hold. Expect a wait of seconds rather than an
                instant answer.
              </Text>
              <Text size="sm" tone="secondary" block>
                What is left is the questions with no answers to offer — audio nobody has submitted
                to AcoustID, a fingerprint nobody has linked to a recording, a recording that
                appears on no album. Re-asking those spends a turn to be told the same thing.
              </Text>
            </Stack>
          </Disclosure>
        </div>
      ) : null}

      {state.status === 'ready' && state.data.total > 0 ? (
        <Sections data={state.data} onOpen={setSubject} />
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
    </Stack>
  )
}

/**
 * How much is open — and how much of it is yours.
 *
 * **The second sentence is the one that was missing.** On the target library 743
 * of 817 questions are waiting on somebody else entirely: an AcoustID
 * submission, a MusicBrainz link, a gather that has to run as a pass. A bare
 * "817 open questions" reads as 817 things to work through, which is both
 * demoralising and wrong, and it was the number a person saw before anything
 * told them otherwise.
 */
function Summary({
  kinds,
  reasons,
}: {
  readonly kinds: readonly OpenQuestionCount[]
  readonly reasons: readonly OpenQuestionCount[]
}) {
  const questions = kinds.reduce((total, kind) => total + kind.questions, 0)
  const files = kinds.reduce((total, kind) => total + kind.files, 0)

  const yours = reasons
    .filter((reason) => isAnswerable(reason.name))
    .reduce((total, reason) => total + reason.questions, 0)

  if (questions === 0) return null

  return (
    <Stack direction="column" gap={2} align="start">
      <Text size="sm" tone="secondary" block>
        {questions.toLocaleString()} open question{questions === 1 ? '' : 's'}, over{' '}
        {files.toLocaleString()} file{files === 1 ? '' : 's'}.
      </Text>
      <Text size="sm" tone="secondary" block>
        {yours === 0
          ? 'None of them can be settled from this screen — they are all waiting on data nobody here controls.'
          : `${yours.toLocaleString()} of them ${yours === 1 ? 'is' : 'are'} waiting on you; the rest are waiting on data nobody here controls.`}
      </Text>
    </Stack>
  )
}

type Group = {
  readonly reason: OpenQuestionCount
  readonly questions: readonly OpenQuestion[]
  readonly kind: OpenKind
}

/**
 * The worklist, split by the unit it is open in and then by refusal.
 *
 * The split is the one thing the old page said in grey six-point type and buried
 * everything else under: these are two different jobs. A release question is a
 * set of files decided together — an album, and a finishable afternoon. A
 * recording question is one file, and there are hundreds of them, most waiting
 * on data nobody here owns. Sorting them into one list by size puts the four
 * hundred repetitions above the dozen decisions, every time.
 *
 * Attribution's refusals come back from the endpoint first for that reason, and
 * rendering the release section first keeps that ordering visible rather than
 * merely present.
 */
function Sections({
  data,
  onOpen,
}: {
  readonly data: MatchingQueue
  readonly onOpen: (subject: MatchingSubjectRef) => void
}) {
  const groups: readonly Group[] = data.reasons.map((reason) => {
    const questions = data.items.filter((item) => item.reason === reason.name)
    return { reason, questions, kind: kindOf(reason.name, questions) }
  })

  const yours = groups.filter((group) => isAnswerable(group.reason.name))
  const releases = groups.filter(
    (group) => group.kind === 'release' && !isAnswerable(group.reason.name),
  )
  const others = groups.filter(
    (group) => group.kind === 'recording' && !isAnswerable(group.reason.name),
  )

  return (
    <Stack direction="column" gap={16}>
      <Section
        title="You can settle these"
        note="Fonoteca found more than one plausible answer and would not guess. Open one and it asks again, then shows you what it found: recordings to compare against a single file, or albums scored against a whole set of them. Pick, or say it is none of them."
        count={total(yours)}
        groups={yours}
        onOpen={onOpen}
        tone="accent"
      />

      <Section
        title="Albums with nothing to choose from"
        note="Sets of files decided together and refused together, where MusicBrainz lists no album containing these recordings at all. There is nothing to put in front of you — opening one shows which files are in it and where they sit on disk."
        count={total(releases)}
        groups={releases}
        onOpen={onOpen}
      />

      <Section
        title="Waiting on somebody else"
        note="One file each. Nothing on this screen moves these: they are waiting on audio being submitted to AcoustID, or on a fingerprint being linked to a MusicBrainz recording. Worth knowing about, not worth working through."
        count={total(others)}
        groups={others}
        onOpen={onOpen}
      />
    </Stack>
  )
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
  tone,
}: {
  readonly title: string
  readonly note: string
  readonly count: OpenQuestionCount | undefined
  readonly groups: readonly Group[]
  readonly onOpen: (subject: MatchingSubjectRef) => void
  /** `accent` on the one section that is somebody's to work. */
  readonly tone?: 'accent'
}) {
  // A section with nothing in it is not an empty state, it is a section that
  // does not apply — every file identified, or attribution not yet run.
  if (groups.length === 0) return null

  return (
    <Card
      className={tone === 'accent' ? styles.yours : undefined}
      title={title}
      aside={count !== undefined ? <SectionCount count={count} /> : null}
    >
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

  const cap = kind === 'release' ? ALBUM_ROWS : TRACK_ROWS
  const shown = questions.slice(0, cap)

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
