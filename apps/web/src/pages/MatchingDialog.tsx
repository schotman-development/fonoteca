import { type components, describeError } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  Button,
  CandidateCard,
  CandidateList,
  type CandidateOption,
  type CandidateOptionProps,
  type CandidateSelection,
  Dialog,
  Disclosure,
  EvidencePanel,
  type EvidenceRow,
  FitSummary,
  type ReleaseFitReadout,
  type SlotRow,
  SlotTable,
  Stack,
  Text,
  VisuallyHidden,
} from '@fonoteca/ui'
import { useState } from 'react'

import { api } from '../api.ts'
import { type QueryState, useApiQuery } from '../useApiQuery.ts'
import { embeddedArt, releaseArt } from './coverArt.ts'
import styles from './MatchingDialog.module.css'
import { MatchingSubject } from './MatchingSubject.tsx'
import type { MatchingQuestion } from './matchingFixtures.ts'
import { isAnswerable, whyOpen } from './openQuestions.ts'
import { mediaFileIdOf } from './seating.ts'
import { readTagWrite } from './tagWrite.ts'

type OpenQuestion = components['schemas']['OpenQuestion']
type SubjectFile = components['schemas']['SubjectFileResponse']
type CandidateAppearance = components['schemas']['CandidateAppearance']
type RecordingCandidateRow = components['schemas']['RecordingCandidateRow']
type RecordingDecision = components['schemas']['RecordingDecisionResponse']
type RecordingCandidates = components['schemas']['RecordingCandidatesResponse']
type FileTagRow = components['schemas']['FileTagRow']
type ComponentCandidateRow = components['schemas']['ComponentCandidateRow']
type ComponentSlotRow = components['schemas']['ComponentSlotRow']
type ComponentDecision = components['schemas']['ComponentDecisionResponse']

/**
 * A commit in flight, or its result.
 *
 * The same discriminated union `useApiQuery` uses for a read, with an idle state
 * a read does not need: this one starts by not having happened. `done` carries
 * the server's answer rather than a boolean because the interesting half of a
 * successful decision is what it says about the file — a write refused because
 * mutation is off is a success that a person has to be told about.
 */
type CommitState =
  | { readonly status: 'idle' }
  | { readonly status: 'sending' }
  | { readonly status: 'done'; readonly result: RecordingDecision }
  | { readonly status: 'error'; readonly message: string }

/** The same, for the album question, whose answer is a different shape. */
type ComponentCommitState =
  | { readonly status: 'idle' }
  | { readonly status: 'sending' }
  | { readonly status: 'done'; readonly result: ComponentDecision }
  | { readonly status: 'error'; readonly message: string }

/**
 * The component a `release:` question names, as the string it arrived as.
 *
 * **Never parsed to a number.** The stamp is `DateTimeOffset.UtcTicks` — around
 * 6.4 × 10^17, where JavaScript is exact to 9 × 10^15 — so `Number(...)` would
 * round it to the nearest few hundred ticks and every request would ask about a
 * component that does not exist. The API takes it as a string for the same
 * reason.
 */
function componentStampOf(question: OpenQuestion): string | null {
  if (question.kind !== 'release') return null

  const stamp = question.id.startsWith('release:') ? question.id.slice('release:'.length) : ''
  return /^\d+$/.test(stamp) ? stamp : null
}

/**
 * How many folder names an evidence row prints before it starts counting.
 *
 * A component is not always an album. The attribution pass follows shared
 * candidate releases outward from a seed, and on heavily compiled catalogue that
 * legitimately reaches fifty-nine files across forty-three folders by way of one
 * greatest-hits record — so an uncapped list turns this row into the wall of
 * text the worklist itself was rebuilt to stop being. Enough to recognise where
 * the music is, then the count.
 */
const FOLDERS_SHOWN = 8

function folderList(folders: readonly string[]): string {
  if (folders.length === 0) return 'At the library root'
  if (folders.length <= FOLDERS_SHOWN) return folders.join(' · ')

  const rest = folders.length - FOLDERS_SHOWN
  return `${folders.slice(0, FOLDERS_SHOWN).join(' · ')} — and ${rest} more folder${rest === 1 ? '' : 's'}`
}

/**
 * What the dialog was opened on.
 *
 * Two sources, discriminated rather than merged, because they are two different
 * amounts of knowledge and pretending otherwise is exactly the failure this
 * screen is prone to. A fixture carries evidence rows, playable files and a
 * ranked candidate set; a row from `GET /api/catalogue/matching` carries a
 * subject, a count and some folder names. Looking the second one up in the
 * fixture module would be the same thing as guessing.
 */
export type MatchingSubjectRef =
  | { readonly source: 'fixture'; readonly question: MatchingQuestion }
  | { readonly source: 'catalogue'; readonly question: OpenQuestion }

/**
 * One open question, in a dialog.
 *
 * A dialog rather than a page because the worklist is the thing being worked:
 * opening a question, reading it and dismissing it should put a person back on
 * the row they came from, at the scroll position they left, with the disclosure
 * they opened still open. A route would have to restore all three, and the
 * ids it would restore them from are documented as unstable — re-running a pass
 * re-stamps a component and mints a new one, so a shared link would silently
 * point at a different decision.
 */
export function MatchingDialog({
  subject,
  onClose,
  onDecided,
}: {
  readonly subject: MatchingSubjectRef
  readonly onClose: () => void
  /**
   * A decision reached the catalogue, so the worklist behind this dialog is out
   * of date.
   *
   * A callback rather than a refetch in here, because the stale thing is the
   * *list* and this component does not own it. It fires once per committed
   * answer and the dialog stays open afterwards on purpose — the row it came
   * from vanishing under a person who has not read the result yet is the
   * behaviour that hides "nothing was written to the file, mutation is off".
   */
  readonly onDecided: () => void
}) {
  if (subject.source === 'fixture') {
    const { question } = subject

    return (
      <Dialog
        open
        onClose={onClose}
        size="lg"
        title={question.title}
        /*
          Labelled inside the dialog, not only on the row that opened it. The
          list says "made-up data" on the summary above these links; the dialog
          it opens looked exactly like a real question about a real album, and a
          person who arrived from a shared screenshot or a stray click had
          nothing on screen telling them otherwise.
        */
        description={
          <>
            {question.subjectLine} · <strong>worked example</strong> — invented data, not from your
            library
          </>
        }
      >
        {/*
          Keyed, so that reopening on a different subject resets the filter and
          the answer. Carrying a half-typed filter from one subject to the next
          would silently hide candidates on arrival, which is the one failure
          this screen exists to prevent.
        */}
        <MatchingSubject key={question.id} question={question} />
      </Dialog>
    )
  }

  const { question } = subject
  const why = whyOpen(question.reason)

  return (
    <Dialog open onClose={onClose} size="lg" title={question.subject} description={why.label}>
      <CatalogueSubject question={question} onDecided={onDecided} />
    </Dialog>
  )
}

/**
 * A real question, with everything the catalogue holds about it and nothing else.
 *
 * **A chooser only where there is something to choose between.** Nothing records
 * a candidate set, so where one appears it has been *recovered* rather than read
 * back: the two identification refusals can be re-asked from the stored
 * fingerprint, and {@link Candidates} does that. The rest cannot. `ReleaseFit`
 * is computed inside the attribution pass and discarded once it has ranked, and
 * re-running that gather is a MusicBrainz browse per recording in the component
 * rather than one request; `NoRecording` and `Unknown` have no candidates by
 * definition, since AcoustID linked nothing.
 *
 * **An empty chooser is never rendered for the rest.** It would be the "link
 * that leads nowhere" failure wearing a different hat: it looks answerable,
 * behaves as though the answer is missing rather than unrecorded, and invites
 * somebody to go and look for a bug. What is there instead is the evidence,
 * which is real, and the reason, which answers the question a person actually
 * arrives with — *is this one mine to fix?*
 */
function CatalogueSubject({
  question,
  onDecided,
}: {
  readonly question: OpenQuestion
  readonly onDecided: () => void
}) {
  const component = question.kind === 'release'
  const why = whyOpen(question.reason)
  const mediaFileId = mediaFileIdOf(question)
  const stamp = componentStampOf(question)
  // The list and the dialog read one table, so the group a person opened from
  // and the screen they land on cannot disagree about whether it is answerable.
  const answerable = isAnswerable(question.reason)
  const recoverable = mediaFileId !== null && answerable
  const placeable = stamp !== null && answerable

  // Resolved to null rather than skipped, because a hook cannot be conditional
  // and a component question has no single file to describe. The alternative —
  // two components, one per kind — would fork the evidence panel to add three
  // rows to one of them.
  const file = useApiQuery(
    () =>
      mediaFileId === null
        ? Promise.resolve(null)
        : api.get('/api/catalogue/matching/files/{id}', {
            params: { path: { id: mediaFileId } },
          }),
    [mediaFileId],
  )

  const claimed = claimedRecording(file)

  const evidence = (
    <EvidencePanel
      heading={component ? 'These files, as a set' : 'This file'}
      subject={question.subject}
      subjectMono={!component}
      {...(component
        ? {
            subjectNote:
              'Decided together, because a single file cannot name its release — these ' +
              'shared a candidate set and were refused as one. That is not a claim they are ' +
              'all one album.',
          }
        : {})}
      badges={<Badge tone={why.tone}>{why.label}</Badge>}
      {...(mediaFileId !== null
        ? {
            // The cover the file itself carries, which nothing on this screen
            // was showing. It is the one claim a person can check against a
            // candidate without reading a word — and on a component there is no
            // single file to ask, so the aside is simply absent there.
            actions: <Artwork name={question.subject} src={embeddedArt(mediaFileId)} size="lg" />,
          }
        : {})}
      rows={[
        // Only where it says something. "Files: 1" under a heading that
        // already reads "This file" is the row restating the panel.
        ...(component
          ? [
              {
                label: 'Files',
                value: question.files.toLocaleString(),
                mono: true,
              },
            ]
          : []),
        {
          label: question.folders.length === 1 ? 'Folder' : 'Folders',
          value: folderList(question.folders),
          mono: true,
          wide: true,
          // Carried because it is how a person finds the music, and never
          // because it is evidence: the folders were not consulted when
          // deciding, and on the documented failure the folder says 1979
          // while the audio measures as the 2015 remaster.
          note: component
            ? 'Where they sit on disk. Not what they were judged on.'
            : 'Where it sits on disk. Not what it was judged on.',
        },
        ...(component ? [] : fileRows(file)),
        recoverable || placeable
          ? {
              label: 'Candidates',
              value: 'Not recorded — asked again below',
              tone: 'info',
              note: placeable
                ? 'Gathered from MusicBrainz just now, not read back from the catalogue.'
                : 'Recovered from the stored fingerprint, not read back from the catalogue.',
            }
          : {
              label: 'Candidates',
              value: 'Not recorded',
              tone: 'warning',
              note: 'Weighed while the pass ran, and thrown away once it had ranked them.',
            },
      ]}
    >
      {file.status === 'ready' && file.data !== null ? <FileTags file={file.data} /> : null}
    </EvidencePanel>
  )

  /*
    **The question first, and everything else under it.** The chooser used to sit
    below the evidence panel, and the panel is an inspector: a folder, five
    measurements and a table of up to sixty raw tag rows. Measured on the live
    screen that put "Which recording is this?" roughly seven hundred pixels down
    a scroll region, so the dialog painted itself complete — header, file, tags —
    while the candidate lookup was still running four to twelve seconds behind
    it, with the only sign of that below the fold. A person saw a finished dialog
    with no answers in it.

    So the order is now: what is being asked, the answers, then the evidence
    behind a disclosure for whoever wants it. The evidence did not get smaller
    and nothing was dropped — a technical reader is one click from all of it, and
    the facts that actually settle a near-tie (the file's length, and what its
    own tags claim) are lifted out of the panel and printed beside the options by
    {@link Candidates}, where they are used.

    Where there is no chooser the panel is the content, so it stays open and the
    lead says plainly that there is nothing here to decide.
  */
  if (!recoverable && !placeable) {
    return (
      <Stack direction="column" gap={16}>
        <NothingToDecide why={why} component={component} />
        {evidence}
      </Stack>
    )
  }

  const chooser =
    stamp !== null && placeable ? (
      <ComponentCandidates stamp={stamp} onDecided={onDecided} />
    ) : mediaFileId !== null ? (
      <Candidates
        mediaFileId={mediaFileId}
        file={file}
        {...(claimed != null ? { claimed } : {})}
        onDecided={onDecided}
      />
    ) : null

  return (
    <Stack direction="column" gap={16}>
      <Text size="sm" tone="secondary" block>
        {why.note}
      </Text>

      {chooser}

      <Disclosure
        size="sm"
        summary={
          <Text size="sm" weight="medium">
            {component
              ? 'Everything else the catalogue holds about this set'
              : 'Everything else the catalogue holds about this file'}
          </Text>
        }
        detail={
          <Text size="xs" tone="tertiary" block>
            {component
              ? 'Which files are in it, and where they sit on disk.'
              : 'Where it sits on disk, what the decoder measured, and every tag the file carries.'}
          </Text>
        }
      >
        <div className={styles.evidence}>{evidence}</div>
      </Disclosure>

      {why.next ? (
        <Text size="sm" tone="secondary" block>
          {why.next}
        </Text>
      ) : null}
    </Stack>
  )
}

/**
 * The lead on a question nobody can answer from this screen.
 *
 * **Said first, and said as the answer rather than as a caveat.** Four of the
 * six refusals on the worklist are waiting on somebody else — an AcoustID
 * submission, a MusicBrainz link, a gather that is a pass rather than a request
 * — and the honest thing to tell a person who opened one is that they are done
 * here. Before this, the dialog opened on an inspector and left them to work
 * that out from the absence of a chooser, which reads as a missing feature or a
 * bug rather than as a finished answer.
 *
 * `why.next` is the sentence that says whether it is anybody's to fix; `why.note`
 * says why the pass refused. Both are already written and neither was being read
 * at the bottom of a scroll region.
 */
function NothingToDecide({
  why,
  component,
}: {
  readonly why: ReturnType<typeof whyOpen>
  readonly component: boolean
}) {
  return (
    <div className={styles.verdict}>
      <Stack direction="column" gap={6} align="start">
        <Badge tone="neutral" size="sm">
          Nothing to decide here
        </Badge>
        <Text size="sm" block>
          {component
            ? 'There is nothing to choose between: MusicBrainz lists no album holding these ' +
              'recordings, so asking again would come back with the same emptiness.'
            : 'There is nothing to choose between: the providers named no recordings for this ' +
              'audio, so there is no list to put in front of you.'}
        </Text>
        {/* What happened, then what would change it. In that order, because the
            second only makes sense once somebody knows the first. */}
        {why.note ? (
          <Text size="sm" tone="secondary" block>
            {why.note}
          </Text>
        ) : null}
        {why.next ? (
          <Text size="sm" tone="secondary" block>
            {why.next}
          </Text>
        ) : null}
      </Stack>
    </div>
  )
}

/* -------------------------------------------------------------- the file */

/**
 * What the catalogue and the bytes both say about one file.
 *
 * **The rows that were missing, and the reason the screen could not be used for
 * what it is for.** A person was being asked which of six recordings a file
 * holds while being shown its filename — and every candidate row prints a
 * length, which is not a comparison unless the file's own length is on the same
 * screen. Length, format and bitrate are what settle a near-tie between two
 * masters of one track; the file's surviving tags settle it outright when there
 * are any.
 *
 * **Loading and failure are rows, not a spinner over the panel.** The evidence
 * that came from the catalogue — the folder, the refusal, whether a candidate
 * set can be recovered — is already correct and already useful, and hiding it
 * behind a file read that may be waiting on an unmounted volume would trade the
 * half that works for the half that might not.
 *
 * **"Could not be read" is itself evidence.** A file the decoder refuses is
 * disproportionately a file nothing could identify, so the failure belongs on
 * the screen next to the refusal rather than in a console.
 */
function fileRows(state: QueryState<SubjectFile | null>): readonly EvidenceRow[] {
  if (state.status === 'loading') {
    return [{ label: 'Audio', value: 'Reading the file…', tone: 'info' }]
  }

  if (state.status === 'error') {
    return [
      {
        label: 'Audio',
        value: 'Could not be read',
        tone: 'warning',
        note: state.message,
      },
    ]
  }

  const file = state.data
  if (file === null) return []

  return [
    {
      label: 'Length',
      // Whichever of the two exists, and never "Not measured" while a real
      // number is sitting one field away. A third of the files on this worklist
      // have no fingerprint duration — their AcoustID was adopted from a tag
      // they already carried, so `fpcalc` never ran on them — and length is
      // half of what was asked for.
      value: file.measured ?? file.duration ?? 'Not measured',
      mono: true,
      ...lengthNote(file),
    },
    {
      label: 'Audio',
      value: audioLine(file),
      mono: true,
      ...(file.audio == null && file.note != null
        ? { tone: 'warning' as const, note: file.note }
        : {}),
    },
    {
      label: 'Size',
      value: file.size,
      mono: true,
    },
    {
      label: 'AcoustID',
      value: file.acoustId ?? 'None recorded',
      mono: true,
      note:
        file.acoustId == null
          ? 'The cluster this audio belongs to was never settled — which is what this screen is for.'
          : file.tagged
            ? 'Written into the file’s own tags.'
            : 'Held by the catalogue; the file’s bytes do not carry it.',
    },
  ]
}

/**
 * Which length that was, and whether the two disagree.
 *
 * **One decision, not two spreads.** These conditions overlap — a file can lack
 * a fingerprint duration *and* be readable now — and expressing them as two
 * conditional spreads onto one object silently let the second win: a file with
 * no `measured` had the length it did have thrown away by a note about not
 * having one.
 *
 * The disagreement case is the interesting one and it is deliberately a warning.
 * `fpcalc` measured the file when identification reached it; the decoder
 * measured it just now. A gap means the bytes changed under the catalogue, and
 * it is also why a candidate's score can look wrong — AcoustID matched against
 * the older number.
 */
function lengthNote(file: SubjectFile): { note?: string; tone?: 'warning' } {
  if (file.measured != null && file.duration != null && file.measured !== file.duration) {
    return {
      note: `Fingerprinted at ${file.measured}; the file reads ${file.duration} now.`,
      tone: 'warning',
    }
  }

  if (file.measured == null && file.duration != null) {
    return {
      note: 'Measured just now. Nothing has fingerprinted this file, so nothing measured it when it was catalogued.',
    }
  }

  if (file.measured == null) {
    return {
      note: 'Nothing has fingerprinted this file, and it could not be read just now either.',
      tone: 'warning',
    }
  }

  return {}
}

/**
 * The technical line: what the decoder found, in the order a person reads it.
 *
 * Container first because it is what they typed to find the file, then the
 * shape of the samples, then the rate. `lossless` is spelled out rather than
 * left to be inferred from the bit depth — a 24-bit number beside a 320 kbps
 * one is exactly the comparison that makes an MP3 look like the better file.
 */
function audioLine(file: SubjectFile): string {
  const audio = file.audio

  if (audio == null) {
    return file.format === '' ? 'Could not be read' : `${file.format} — could not be read`
  }

  return [
    file.format === '' ? audio.codec : file.format,
    audio.bitDepth != null ? `${audio.bitDepth}-bit` : null,
    audio.sampleRateHz > 0 ? `${kilohertz(audio.sampleRateHz)} kHz` : null,
    channels(audio.channels),
    audio.bitrateKbps > 0 ? `${audio.bitrateKbps.toLocaleString()} kbps` : null,
    audio.lossless ? 'lossless' : 'lossy',
  ]
    .filter((part) => part != null)
    .join(' · ')
}

/**
 * 44.1, and 48 rather than 48.0 — but also 22.05 rather than 22.1.
 *
 * A fixed decimal place is wrong for exactly the rates that need two of them:
 * 22050 Hz and 11025 Hz are real sample rates and rounding them to 22.1 and 11
 * prints a number nobody's file has. Three places is enough for every rate in
 * use, and `Number()` drops the trailing zeros the common ones would gain.
 */
function kilohertz(hertz: number): string {
  return Number((hertz / 1000).toFixed(3)).toString()
}

function channels(count: number): string | null {
  if (count <= 0) return null
  if (count === 1) return 'mono'
  if (count === 2) return 'stereo'
  return `${count} channels`
}

/**
 * Whatever the file still claims about itself.
 *
 * **Frequently the answer, and nothing else on this screen is.** A file carrying
 * a `MUSICBRAINZ_TRACKID` has named its own recording, and no score above it is
 * better evidence than that — the identification pass never looks, because it
 * asks AcoustID what the audio is rather than what the file claims to be. So the
 * tags go under the evidence rather than at the bottom of a diagnostics panel.
 *
 * Measured against the library this was built on, that is the common case rather
 * than the rare one: every file sampled carried a full Picard tag set, MusicBrainz
 * identifiers included, on files all three passes had refused.
 *
 * An empty list is still a finding and still says so in words, because "this file
 * claims nothing" and "nobody looked" render identically as an absence.
 */
function FileTags({ file }: { readonly file: SubjectFile }) {
  return (
    <Stack direction="column" gap={6} align="start" className={styles.tags}>
      <Text size="xs" tone="tertiary">
        {file.tags.length === 0 ? 'Tags' : `Tags — ${file.tags.length}`}
      </Text>

      {file.tags.length === 0 ? (
        <Text size="xs" tone="secondary" block>
          The file claims nothing about itself — no title, no album, no identifiers. Which is why
          the question had to be put to the audio in the first place.
        </Text>
      ) : (
        <dl className={styles.tagList}>
          {file.tags.map((tag) => (
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

      {/*
        Said under the list rather than as a row, because it is about the read
        rather than about the file: one library refused and the other did not,
        and what is missing is fields nobody may notice are absent.
      */}
      {file.note != null && file.audio != null ? (
        <Text size="2xs" tone="tertiary" block>
          {file.note}
        </Text>
      ) : null}
    </Stack>
  )
}

/* ----------------------------------------------- reading the numbers aloud */

/**
 * The signed drift string, back as a number.
 *
 * The endpoint formats it — `+3.93s`, `-8.84s`, `0.00s` — and the sign is
 * `file - recording`, so a positive value means the *recording* is the shorter
 * of the two. Parsed rather than recomputed because the endpoint is the one
 * holding both durations at full precision.
 *
 * Null on anything that does not match, which is the same answer as "not
 * measured": a drift nobody can read is not a comparison, and inventing one from
 * a half-parsed string is how a screen ends up claiming an agreement that was
 * never established.
 */
function driftSeconds(drift: string | null | undefined): number | null {
  if (drift == null) return null

  const match = /^([+-]?\d+(?:\.\d+)?)s$/.exec(drift)
  if (match?.[1] == null) return null

  const seconds = Number(match[1])
  return Number.isFinite(seconds) ? seconds : null
}

/**
 * Drift as a sentence about the recording, not as a signed number.
 *
 * **The one fact that separates six identical-looking candidates, and it was
 * unreadable.** On a real ambiguous file every option printed the same title,
 * the same artist and the same score to three places; the only thing that
 * differed was `+3.93s vs file`, in grey, mid-line, in a row of five other
 * numbers. And "vs file" does not say which way round it is — the reading most
 * people take from `+3.93s` on a 4:36 recording beside a 4:40 file is that the
 * recording is *longer*, which is backwards.
 *
 * The exact signed figure is not lost. It moves to the small print line with the
 * score and the source count, where a technical reader still gets it to the
 * hundredth of a second.
 */
function plainDrift(drift: string | null | undefined): string | null {
  const seconds = driftSeconds(drift)
  if (seconds === null) return null

  const size = Math.abs(seconds)

  // The same threshold the endpoint prints "0.00s" at, so the two can never
  // disagree about whether this is an exact match.
  if (size < 0.005) return 'same length as your file'

  const rounded = size < 10 ? `${size.toFixed(1)}s` : `${Math.round(size)}s`

  return seconds > 0 ? `${rounded} shorter than your file` : `${rounded} longer than your file`
}

/**
 * Which candidate the file's own tags already name.
 *
 * **Frequently the answer, and until now nothing on the screen joined it up.**
 * The identification pass asks AcoustID what the *audio* is and never asks the
 * file what it claims to be, so a file carrying a full Picard tag set arrives on
 * this screen with `MUSICBRAINZ_TRACKID` in its tags — and, measured on the
 * target library, that is the common case rather than the rare one. It was being
 * printed, honestly, as one row of a sixty-row tag table seven hundred pixels
 * above the candidate whose identifier it matched. Establishing the match meant
 * comparing two thirty-six-character hex strings by eye.
 *
 * It is a badge on the row rather than a pre-selection. The tag is a claim
 * somebody's tagger made and the audio is the thing being asked about; they
 * disagree often enough that the file's word is evidence, not an answer, and
 * pre-selecting from it would quietly turn this screen into a tag importer.
 */
function claimedRecording(state: QueryState<SubjectFile | null>): string | null {
  if (state.status !== 'ready' || state.data === null) return null

  return tagValue(state.data.tags, 'MUSICBRAINZ_TRACKID')?.toLowerCase() ?? null
}

/**
 * One tag by name, case-insensitively.
 *
 * The names are whatever the tagger wrote and the two readers disagree about
 * their case — ATL uppercases Vorbis keys, TagLib# does not — so matching on the
 * exact string is how a lookup silently finds nothing on half the library.
 */
function tagValue(tags: readonly FileTagRow[], name: string): string | null {
  const found = tags.find((tag) => tag.name.toUpperCase() === name)
  const value = found?.value.trim()

  return value != null && value.length > 0 ? value : null
}

/**
 * What the file says it is, in one line, or nothing.
 *
 * Lifted out of the tag table and printed beside the options because this is
 * where it is used. A person choosing between six recordings called
 * "Tears in Heaven" is helped enormously by "your file's tags say: Tears in
 * Heaven — Eric Clapton — Unplugged" and not at all by finding the same three
 * values as rows 1, 2 and 3 of a table under a closed disclosure.
 */
function tagClaim(file: SubjectFile): string | null {
  const parts = [
    tagValue(file.tags, 'TITLE'),
    tagValue(file.tags, 'ARTIST'),
    tagValue(file.tags, 'ALBUM'),
  ].filter((part) => part != null)

  return parts.length === 0 ? null : parts.join(' — ')
}

/**
 * The candidate set, asked for again rather than read back.
 *
 * Fetched when the dialog opens, not behind a button, because opening the dialog
 * *is* the deliberate act — a person clicked one row out of hundreds. It costs a
 * turn at AcoustID's rate limit and one at MusicBrainz's per recording named,
 * which is why the worklist itself does not do this for fifty rows at once.
 *
 * **The chooser commits, and what it commits is the recording.** `POST
 * …/decision` takes the MBID, not the AcoustID cluster behind it: the cluster is
 * what gets written into the file's bytes, and resolving it server-side against
 * a live lookup is what keeps the only irreversible write in the application
 * from being authorised by a form post. So this sends a claim about music and
 * the server turns it into an identifier.
 *
 * **"None of these" is sent as an answer, not as a cancel.** It records that
 * somebody listened and rejected every candidate, which is a stronger claim than
 * the rule could make and the only one that closes the question without an
 * identity.
 */
function Candidates({
  mediaFileId,
  file,
  claimed,
  onDecided,
}: {
  readonly mediaFileId: string
  /**
   * The subject file's own reading, shared rather than fetched again.
   *
   * Two facts out of it are printed beside the options because they are what
   * settles the choice: the length the options are all being compared against,
   * and what the file's own tags claim to be. Both used to live only in the
   * evidence panel, which is now closed by default — and they were the reason
   * to open it.
   */
  readonly file: QueryState<SubjectFile | null>
  /** The recording MBID the file's tags name, lowercased. See {@link claimedRecording}. */
  readonly claimed?: string
  readonly onDecided: () => void
}) {
  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/matching/recordings/{id}/candidates', {
        params: { path: { id: mediaFileId } },
      }),
    [mediaFileId],
  )

  const [answer, setAnswer] = useState<CandidateSelection | null>(null)
  const [commit, setCommit] = useState<CommitState>({ status: 'idle' })

  async function send(selection: CandidateSelection) {
    setCommit({ status: 'sending' })

    try {
      const result = await api.post('/api/catalogue/matching/recordings/{id}/decision', {
        params: { path: { id: mediaFileId } },
        json:
          selection.kind === 'none'
            ? { answer: 'none', recording: null }
            : { answer: 'recording', recording: selection.id },
      })

      setCommit({ status: 'done', result })

      // After the state, so a refetch that re-renders the worklist cannot race
      // the result a person is about to read.
      onDecided()
    } catch (cause: unknown) {
      setCommit({ status: 'error', message: describeError(cause) })
    }
  }

  const candidates = state.status === 'ready' ? state.data.candidates : []
  const closest = closestLength(candidates)

  return (
    <Stack direction="column" gap={12} align="start" className={styles.chooser}>
      <YourFile
        file={file}
        {...(state.status === 'ready' ? { measured: state.data.measured } : {})}
      />

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {/*
          Loud, and at the top, because the rest of the dialog is already
          painted by the time this starts. The lookup is one AcoustID turn plus
          up to six MusicBrainz recording lookups — measured at 4.3s and 11.7s on
          two real files — and the previous wording ("Reading the candidates, and
          asking the providers if the catalogue's answer is stale…") sat in
          tertiary grey below a complete-looking file panel. A person could not
          tell a slow dialog from a finished one with nothing in it.
        */}
        {state.status === 'loading' ? (
          <Stack direction="column" gap={4} align="start" className={styles.waiting}>
            <Text size="sm" weight="medium" block>
              Looking up what this could be…
            </Text>
            <Text size="sm" tone="secondary" block>
              This takes a few seconds. Fonoteca is asking AcoustID what the audio matches, then
              asking MusicBrainz about each recording it names.
            </Text>
          </Stack>
        ) : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="warning">Could not recover the candidates</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}

        {/*
          The arrival, announced. The list itself renders outside this region —
          a live region containing a radio group would re-read the whole thing
          on every selection — so what goes here is the one sentence saying the
          waiting is over. Without it a screen reader hears "asking AcoustID
          again…" and then nothing at all, which is what a failure sounds like.
        */}
        {/*
          Announced, not printed. It is the one sentence saying the waiting is
          over — without it a screen reader hears "looking up what this could
          be…" and then nothing at all, which is what a failure sounds like. On
          screen it was a third count in four lines, above a legend and six
          visible cards that already say it.
        */}
        {state.status === 'ready' ? (
          <VisuallyHidden>
            {state.data.candidates.length === 0
              ? 'AcoustID answered and named no recording.'
              : `${state.data.candidates.length} recording${
                  state.data.candidates.length === 1 ? '' : 's'
                } to choose between.`}
          </VisuallyHidden>
        ) : null}
      </div>

      {state.status === 'ready' ? (
        state.data.candidates.length === 0 ? (
          <Text size="sm" tone="secondary" block>
            There is nothing to choose from. AcoustID recognises this audio, but it names no
            MusicBrainz recording for it — so there is no list of answers to put in front of you.
            That is a gap in their data rather than in yours, and it closes when somebody links the
            fingerprint to a recording.
          </Text>
        ) : (
          <>
            {/*
              The raw clusters, above the collapsed list, because they are the
              whole shape of an ambiguous refusal — the scores the rule actually
              compared, before anything folded them onto recordings. Two
              near-tied scores read as a disagreement until the `clusters` count
              on a row below says both point at one recording.

              The measured length is here rather than in the evidence panel
              because this is where it is used: every candidate prints a length,
              and the one that differs by seconds is a different master.
            */}
            <Text size="xs" tone="tertiary" block>
              {state.data.clusters.length} fingerprint cluster
              {state.data.clusters.length === 1 ? '' : 's'} matched, scoring{' '}
              {state.data.clusters.map((cluster) => cluster.score.toFixed(3)).join(', ')}.{' '}
              {provenance(state.data)}
            </Text>

            <CandidateList
              label="Which recording is this?"
              /*
                Rewritten from the inside out. What it said was true and needed a
                paragraph of AcoustID's data model to be worth reading: "one
                recording routinely sits under several AcoustID clusters, so two
                near-tied scores naming the same recording are one answer
                arriving twice". What a person needs first is what the choice is
                *for* and how to tell the rows apart — the rows all say the same
                title, and length is what separates them. The cluster arithmetic
                is still on every row and still in the small print, where the
                reader who wants it will find it.
              */
              description="Same song, different recordings of it — a studio take, a live take, a remaster. Length is usually what tells them apart, so compare each one against your file."
              value={answer}
              onSelect={(selection) => {
                setAnswer(selection)

                // A failed attempt is about the answer that failed, so choosing
                // a different one clears it. A committed one is not cleared:
                // the radios are disabled once it lands, and the result stays
                // on screen until the dialog is dismissed.
                if (commit.status === 'error') setCommit({ status: 'idle' })
              }}
              // Not `!== 'idle'`. `disabled` lands on the `<fieldset>`, and a
              // disabled fieldset disables every control inside it — including
              // the Commit button, which the list renders in its footer. On an
              // error that would leave the radios and the button both dead, with
              // the only route back to idle being the `onSelect` above that can
              // no longer fire: the dialog would have to be closed and reopened,
              // re-running the candidate query, and the two failures that get
              // here are exactly the retryable ones — a pass holding the gate,
              // and a provider that did not answer.
              disabled={commit.status === 'sending' || commit.status === 'done'}
              options={state.data.candidates.map((candidate) =>
                candidateOption(candidate, {
                  ...(claimed != null ? { claimed } : {}),
                  ...(closest != null ? { closest } : {}),
                }),
              )}
              refusal={{
                label: 'None of these — it is something else',
                description:
                  'Records that you listened and none of them is this audio. Nothing is written ' +
                  'to the file, and no pass will ask about it again.',
              }}
              footer={
                <Stack direction="column" gap={6} align="start">
                  <Stack gap={12} align="center" wrap>
                    <Button
                      variant="primary"
                      size="sm"
                      // The same predicate the fieldset uses, and not
                      // `!== 'idle'`. On an error the fieldset comes back but a
                      // button disabled in its own right does not, and the way
                      // back to idle is `onSelect` — which a native radio that
                      // is already checked never fires. Retrying the same answer
                      // after a 503 would mean detouring through another
                      // candidate and back, and the two failures that get here
                      // are exactly the ones worth retrying unchanged.
                      disabled={
                        answer == null || commit.status === 'sending' || commit.status === 'done'
                      }
                      onClick={() => {
                        if (answer != null) void send(answer)
                      }}
                    >
                      {commitLabel(answer, commit)}
                    </Button>
                    <Text size="xs" tone="tertiary">
                      {commitHint(answer, commit, chosenTitle(candidates, answer))}
                    </Text>
                  </Stack>

                  {/*
                    Polite, and it holds every phase of the commit rather than
                    only its failures. A button that changes its own label is
                    not an announcement, so without this a screen reader hears
                    nothing at all between pressing Commit and the row quietly
                    disappearing from the list behind the dialog.
                  */}
                  <div role="status" aria-live="polite">
                    {commit.status === 'error' ? (
                      <Text size="xs" tone="danger" block>
                        Nothing was committed. {commit.message}
                      </Text>
                    ) : null}

                    {commit.status === 'done' ? <Committed result={commit.result} /> : null}
                  </div>
                </Stack>
              }
            />

            {state.data.candidates.length < state.data.total ? (
              <Text size="xs" tone="tertiary" block>
                These are the {state.data.candidates.length} best-scoring of {state.data.total}{' '}
                recordings AcoustID named. The other{' '}
                {state.data.total - state.data.candidates.length} scored lower and were not looked
                up — each one is a request to MusicBrainz. If none of these is right, answering
                “none of these” is the accurate thing to do.
              </Text>
            ) : null}
          </>
        )
      ) : null}
    </Stack>
  )
}

/**
 * The candidate whose length is closest to the file's, when one clearly is.
 *
 * **Refuses to name a winner when two are within half a second**, and that
 * restraint is the point. Duration is the edition discriminator — the 2015
 * remaster of *Off the Wall* matches to 0.00s where three earlier pressings sit
 * at 0.76s — but only when the gap is real. Badging the nearest of two rows that
 * differ by four hundredths of a second would dress a coin toss as evidence, on
 * the one screen whose entire job is to stop that happening.
 *
 * Null is also the answer when nothing was measured, which is a third of this
 * worklist: a file whose AcoustID came from a tag it already carried was never
 * fingerprinted, so no candidate has a drift to compare.
 */
function closestLength(candidates: readonly RecordingCandidateRow[]): string | null {
  const measured = candidates
    .map((candidate) => ({ mbid: candidate.mbid, seconds: driftSeconds(candidate.drift) }))
    .filter((row): row is { mbid: string; seconds: number } => row.seconds !== null)
    .sort((left, right) => Math.abs(left.seconds) - Math.abs(right.seconds))

  const [best, runnerUp] = measured

  if (best === undefined) return null
  if (runnerUp === undefined) return best.mbid

  return Math.abs(runnerUp.seconds) - Math.abs(best.seconds) >= 0.5 ? best.mbid : null
}

/**
 * The two facts the choice is actually made on, above the choice.
 *
 * Every candidate row prints a length, and a length is not a comparison unless
 * the file's own is on the same screen. It was — in an evidence panel that is now
 * closed by default, which would have made this screen worse rather than better
 * had these not come with it.
 *
 * The tag claim is the other half, and it is the stronger of the two: a file
 * carrying `TITLE`, `ARTIST` and `ALBUM` has told you what it thinks it is, and
 * on this library most of them do. It is offered as context and never as a
 * selection — see {@link claimedRecording} for why the file's word does not get
 * to answer a question about the audio.
 */
function YourFile({
  file,
  measured,
}: {
  readonly file: QueryState<SubjectFile | null>
  /** The length AcoustID was asked about, for a file the decoder could not read. */
  readonly measured?: string
}) {
  const data = file.status === 'ready' ? file.data : null
  const length = data?.measured ?? data?.duration ?? measured ?? null
  const claim = data === null || data === undefined ? null : tagClaim(data)

  if (length === null && claim === null) return null

  const technical = [length, data?.audio != null ? audioLine(data) : null]
    .filter((part) => part != null)
    .join(' · ')

  return (
    <div className={styles.yourFile}>
      <Stack direction="column" gap={2} align="start">
        <Text size="xs" tone="tertiary" block>
          Your file
        </Text>

        {technical !== '' ? (
          <Text size="sm" family="mono" block>
            {technical}
          </Text>
        ) : null}

        {claim !== null ? (
          <Text size="sm" tone="secondary" block>
            Its tags say: {claim}
          </Text>
        ) : (
          <Text size="sm" tone="secondary" block>
            Its tags name no title, artist or album — which is why the question had to be put to the
            audio.
          </Text>
        )}
      </Stack>
    </div>
  )
}

/**
 * When this answer was taken, and from where.
 *
 * **Said because the answer is allowed to be old.** The passes store AcoustID's
 * whole answer as they go and the assembled set is kept for a week, so what is
 * on screen is usually the catalogue's copy rather than a live reading — which
 * is the whole point, since the alternative was a minute of provider round trips
 * every time somebody opened the same question. But a candidate list is
 * *evidence*, and evidence with an unstated age is the thing that makes a person
 * distrust the screen when a score does not match what MusicBrainz shows today.
 *
 * Relative rather than a timestamp: nobody needs the minute, and "today" against
 * "6 days ago" is exactly the distinction that decides whether to press refresh.
 */
function provenance(data: RecordingCandidates): string {
  if (!data.fromCache) return 'Asked just now.'

  const days = Math.floor((Date.now() - Date.parse(data.asOfUtc)) / 86_400_000)

  if (days < 1) return 'Read from the catalogue, asked today.'

  return `Read from the catalogue, asked ${days} day${days === 1 ? '' : 's'} ago.`
}

/**
 * What the button says.
 *
 * **"Commit" is not a word for this audience.** It is the word for the operation
 * — one transaction, one row, one tag write — and it was the only label on the
 * one screen in the application that changes a person's audio files on their
 * say-so. It says what happens now, and it changes for the refusal, because
 * saving "none of these" and saving a match are different enough decisions that
 * the button should not read identically for both.
 */
function commitLabel(answer: CandidateSelection | null, commit: CommitState): string {
  if (commit.status === 'sending') return 'Saving…'
  if (commit.status === 'done') return 'Saved'
  if (answer?.kind === 'none') return 'Save “none of these”'

  return 'Save this match'
}

/**
 * The line beside the button: what pressing it will do, or did.
 *
 * It names the consequence rather than the control. "This will tag the file" is
 * the sentence somebody needs before an irreversible-looking button, and it is
 * only true for one of the two answers — refusing opens nothing and changes no
 * bytes, and saying so is what makes "None of these" safe to reach for.
 *
 * **The file write is stated as conditional, because it is.**
 * `Fonoteca:AllowFileMutation` is off by default, so the ordinary outcome is a
 * decided catalogue and an untouched file. Promising the write outright would
 * send somebody looking in their tags for something that was never written; not
 * mentioning it at all would let the one irreversible action in the application
 * happen without warning. Both are worse than a sentence with an "if" in it.
 */
function commitHint(
  answer: CandidateSelection | null,
  commit: CommitState,
  /**
   * What was chosen, named.
   *
   * The bar is pinned to the foot of a scroll region several screens tall, so by
   * the time somebody reaches for it the card they picked is usually off the top
   * of it. "Save this match" beside a sentence that names the match is the
   * difference between confirming a decision and confirming that a decision
   * exists.
   */
  chosen?: string,
): string {
  if (commit.status === 'sending') return 'Saving the decision, and working out what to write.'
  if (commit.status === 'done') return 'Saved. The list behind this dialog has been updated.'
  if (answer == null) return 'Pick a recording above, or “none of these”.'

  if (answer.kind === 'none') {
    return 'Records that this audio is none of them. Your file on disk is not changed.'
  }

  return (
    `Links your file to ${chosen == null ? 'this recording' : `“${chosen}”`} and credits its ` +
    'artists — and writes the match into the file’s own tags, if tag writing is switched on.'
  )
}

/** The chosen candidate's title and billing, for the sentence beside the button. */
function chosenTitle(
  candidates: readonly RecordingCandidateRow[],
  answer: CandidateSelection | null,
): string | undefined {
  if (answer?.kind !== 'candidate') return undefined

  const found = candidates.find((candidate) => candidate.mbid === answer.id)
  if (found?.title == null) return undefined

  return found.artist == null ? found.title : `${found.title} — ${found.artist}`
}

/**
 * What the decision actually did, once it has.
 *
 * **Three separate facts, and they can disagree.** The catalogue was changed,
 * the file may or may not have been, and the two are not the same success —
 * `Fonoteca:AllowFileMutation` is off by default, so the ordinary happy path
 * here is a linked recording and an untouched file. Collapsing that into a tick
 * would be the screen lying in the direction that costs the most: somebody would
 * go looking in their tags for something that was never written.
 *
 * The recording title is echoed back from the server rather than taken from the
 * row that was clicked. They are almost always the same string; where they are
 * not, MusicBrainz has moved under the candidate list, and the one worth
 * printing is what was actually linked.
 */
function Committed({ result }: { readonly result: RecordingDecision }) {
  const tag = readTagWrite(result.tag)

  return (
    <Stack direction="column" gap={4} align="start">
      <Text size="xs" tone="secondary" block>
        {result.recording == null
          ? 'Recorded as none of these. This file will not be asked about again by the ' +
            'identification or enrichment passes.'
          : `Linked to ${result.title ?? result.recording}. It joins the attribution worklist, ` +
            'which is where the edition gets decided.'}
      </Text>

      <Stack gap={8} align="center" wrap>
        <Badge tone={tag.tone} size="sm">
          {tag.label}
        </Badge>
        {tag.note != null ? (
          <Text size="xs" tone="tertiary">
            {tag.note}
          </Text>
        ) : null}
      </Stack>

      {/*
        The server's own sentence, and only when it is saying something the
        badge does not. `detail` repeats the note for the statuses that have
        one; where it differs it is carrying the actual reason — which library
        refused, what the disk said — and that is the half worth reading.
      */}
      {result.detail != null && result.detail !== tag.note ? (
        <Text size="xs" tone="tertiary" block>
          {result.detail}
        </Text>
      ) : null}
    </Stack>
  )
}

/**
 * One candidate, with everything the lookup already paid for.
 *
 * **The row used to print four fields out of a response that carries a dozen.**
 * `GetRecordingAsync` is the heaviest request this application makes — artists,
 * credits, releases, release groups, media, ISRCs and two kinds of relationship,
 * measured at 10.3 seconds cold — and the previous version of this card showed a
 * title, a credit line, a length and three numbers. Everything added here
 * arrived in that same response and costs nothing more.
 *
 * What each part is for, because none of it is decoration:
 *
 * - **Drift** is the edition discriminator, and it is the number the attribution
 *   pass already decides pressings on: against per-release track lengths the
 *   2015 remaster of *Off the Wall* matches to 0.00s where three earlier
 *   pressings sit at 0.76s. A person choosing between two masters of one song
 *   should not have to subtract two timestamps in their head.
 * - **The first release date** separates an original from the compilation that
 *   reprinted it, which is the case where two candidates have the same title,
 *   the same artist and lengths a frame apart.
 * - **Performers** are the half a credit line does not carry. MusicBrainz bills
 *   a Karajan reading of Beethoven's Fifth to *Beethoven*, so on classical
 *   catalogue every candidate reads identically until the conductor and the
 *   orchestra appear.
 * - **ISRCs** end the question outright when the file happens to carry one.
 * - **The releases** are behind a disclosure because there can be twenty-five of
 *   them and they are the answer to a different question — *where would I have
 *   got this from* — than the one the card is being read for.
 */
function candidateOption(
  candidate: RecordingCandidateRow,
  marks: { readonly claimed?: string; readonly closest?: string } = {},
): CandidateOption {
  /*
    The line under the credit, in English.

    Length and how it compares, and nothing else — those are the two facts that
    tell six rows with one title apart. Everything that used to share this line
    (the score to three decimal places, a submission count, a cluster count) is
    figures about the *evidence* rather than about the music, and it has moved
    down to {@link detail} intact. Nothing was removed; the two layers are read
    by two different people and used to be one unreadable line.
  */
  const facts = [
    candidate.length,
    // Only where both sides are known. "same length as your file" against a file
    // nothing measured would be a claim of agreement, which is the opposite of
    // the truth.
    plainDrift(candidate.drift),
  ]
    .filter((part) => part != null)
    .join(' · ')

  /*
    The small print: the same numbers, named rather than abbreviated, with the
    identifier last.

    **The MBID is labelled and it is always in the same place.** It used to
    appear as `disambiguation ?? mbid` — so one row read "live, 1992-01-16:
    'Unplugged', Bray Film Studios" and the next read
    "21ddde6c-90f4-469e-bdd0-1f438d011917", a bare UUID in the slot a person had
    just learned held a sentence. That reads as a bug. Named and placed
    consistently it is what it always was: the string you paste into MusicBrainz
    to go and look.
  */
  const detail = [
    candidate.firstReleased != null ? `first released ${candidate.firstReleased}` : null,
    candidate.disambiguation,
    `match ${candidate.score.toFixed(3)}`,
    `${candidate.sources} AcoustID submission${candidate.sources === 1 ? '' : 's'}`,
    `${candidate.clusters} fingerprint cluster${candidate.clusters === 1 ? '' : 's'}`,
    candidate.drift != null ? `drift ${candidate.drift}` : null,
    `MBID ${candidate.mbid}`,
  ]
    .filter((part) => part != null)
    .join(' · ')

  /*
    The two things a person can act on at a glance, as badges.

    Both are evidence the screen already held and was not joining up: the file's
    own `MUSICBRAINZ_TRACKID` was a row in a sixty-row tag table, and "which of
    these is closest to my file's length" was six signed numbers to compare by
    eye. Neither badge selects anything — see {@link claimedRecording} for why the
    file's claim is not allowed to answer a question about the audio, and
    {@link closestLength} for why the length badge refuses to appear on a tie.
  */
  const badges = [
    marks.claimed != null && candidate.mbid.toLowerCase() === marks.claimed ? (
      <Badge key="claimed" tone="success" size="sm">
        Matches this file’s tags
      </Badge>
    ) : null,
    marks.closest != null && candidate.mbid === marks.closest ? (
      <Badge key="closest" tone="info" size="sm">
        Closest length
      </Badge>
    ) : null,
  ].filter((badge) => badge != null)

  const aside = [
    candidate.work != null ? `Work: ${candidate.work}` : null,
    candidate.performers.length > 0
      ? candidate.performers.map((who) => `${who.name} (${who.role})`).join(' · ')
      : null,
    candidate.isrcs.length > 0 ? `ISRC ${candidate.isrcs.join(', ')}` : null,
  ].filter((part) => part != null)

  return {
    id: candidate.mbid,
    render: (selection: CandidateOptionProps) => (
      <CandidateCard
        {...(candidate.releases[0] != null
          ? // The earliest release it appears on, which is the list's own order.
            // A recording has no cover of its own; the record it came out on
            // does, and the original is the one a person recognises.
            { image: releaseArt(candidate.releases[0].releaseId) }
          : {})}
        // Falls back to the identifier rather than to "Unknown": MusicBrainz not
        // answering does not make the recording nameless, and the MBID is what a
        // person would paste into MusicBrainz to go and look.
        title={candidate.title ?? candidate.mbid}
        {...(candidate.artist != null
          ? {
              subtitle:
                candidate.release != null
                  ? `${candidate.artist} — ${candidate.release}`
                  : candidate.artist,
            }
          : {})}
        // Empty on a candidate MusicBrainz gave no length for, and then omitted
        // rather than passed as '': the card renders any non-null `facts`, so an
        // empty string is a blank line under the credit. It could not be empty
        // before — the score and the source count used to live on this line.
        {...(facts === '' ? {} : { facts })}
        // A sentence, not figures — see `factsMono`.
        factsMono={false}
        detail={detail}
        {...(badges.length > 0 ? { badges: <>{badges}</> } : {})}
        selection={selection}
        {...(aside.length > 0
          ? {
              fit: (
                <span className={styles.aside}>
                  {aside.map((line) => (
                    <Text key={line} size="xs" tone="secondary" block>
                      {line}
                    </Text>
                  ))}
                </span>
              ),
            }
          : {})}
        {...(candidate.releases.length > 0
          ? {
              disclosureLabel: 'Where it appears',
              disclosureAside: (
                <Badge tone="neutral" size="sm" mono>
                  {/*
                    The count MusicBrainz gave, not the number of rows below it —
                    WS/2 caps a recording lookup's release list at 25 without
                    saying so, and the rows are cut again for reading. A badge
                    that counted the rendered list would report a cut set as a
                    complete one.
                  */}
                  {candidate.appearances}
                  {candidate.appearances >= 25 ? '+' : ''}
                </Badge>
              ),
              children: (
                <ul className={styles.appearances} aria-label="Releases">
                  {candidate.releases.map((appearance) => (
                    <li key={appearance.releaseId} className={styles.appearance}>
                      <Text size="xs" tone="secondary" block>
                        {appearance.title}
                        {appearance.primaryType != null ? ` · ${appearance.primaryType}` : ''}
                        {appearance.country != null ? ` · ${appearance.country}` : ''}
                        {appearance.status != null && appearance.status !== 'Official'
                          ? ` · ${appearance.status}`
                          : ''}
                      </Text>
                      <Text size="xs" tone="tertiary" family="mono">
                        {position(appearance)}
                        {appearance.released != null ? ` · ${appearance.released}` : ''}
                      </Text>
                    </li>
                  ))}

                  {candidate.releases.length < candidate.appearances ? (
                    <li>
                      <Text size="2xs" tone="tertiary" block>
                        Showing {candidate.releases.length} of {candidate.appearances}.
                      </Text>
                    </li>
                  ) : null}
                </ul>
              ),
            }
          : {})}
      />
    ),
  }
}

/** Where on the release the track sits: "2-7 of 10", "A1", or nothing. */
function position(appearance: CandidateAppearance): string {
  const number = appearance.trackNumber ?? appearance.trackPosition?.toString()
  if (number == null) return ''

  const disc =
    appearance.discNumber != null && appearance.discNumber > 1 ? `${appearance.discNumber}-` : ''
  const of = appearance.trackCount != null ? ` of ${appearance.trackCount}` : ''

  return `${disc}${number}${of}`
}

/* ------------------------------------------------------------- the album */

/**
 * Which album a whole component came from, asked again and put to a person.
 *
 * **The question this screen used to say it could not ask.** Attribution refuses
 * a *set* of files, and the candidate releases it weighed are computed inside the
 * pass and thrown away once they have ranked — so the worklist could name the
 * refusal and nothing else, and the dialog said so at length. What made that
 * true was the *gather*: forming a component means browsing a recording,
 * fetching every release it names, reading back which of their tracks the
 * library holds, admitting those files and browsing theirs — unbounded, and
 * genuinely a pass.
 *
 * None of that applies to a component that already exists. Its files are written
 * down, in the timestamp they share, so there is nothing to expand into: the
 * work is one browse per distinct recording and one lookup per album offered.
 * Seconds, not a job.
 *
 * **Everything is scored by the rule that refused it.** The server runs the same
 * `ReleaseFit` the pass does and returns the pass's own coverage floor and drift
 * tolerance beside the numbers, so the meters colour by the application's gates
 * rather than by a threshold this screen invented. A person seeing why the rule
 * would not commit is most of what they need to decide whether they should.
 */
function ComponentCandidates({
  stamp,
  onDecided,
}: {
  readonly stamp: string
  readonly onDecided: () => void
}) {
  // Bumped by "ask again", and the whole of this component's invalidation. The
  // stored answer is the ordinary one — the attribution pass writes a candidate
  // document for every component it refuses, out of the gather it had already
  // paid for — so a fresh gather is a deliberate act rather than a default.
  const [asked, setAsked] = useState(0)

  const state = useApiQuery(
    () =>
      api.get('/api/catalogue/matching/components/{stamp}/candidates', {
        params: { path: { stamp }, ...(asked > 0 ? { query: { refresh: true } } : {}) },
      }),
    [stamp, asked],
  )

  const [answer, setAnswer] = useState<CandidateSelection | null>(null)
  const [commit, setCommit] = useState<ComponentCommitState>({ status: 'idle' })

  async function send(selection: CandidateSelection) {
    setCommit({ status: 'sending' })

    try {
      const result = await api.post('/api/catalogue/matching/components/{stamp}/decision', {
        params: { path: { stamp } },
        json:
          selection.kind === 'none'
            ? { answer: 'none', release: null }
            : { answer: 'release', release: selection.id },
      })

      setCommit({ status: 'done', result })
      onDecided()
    } catch (cause: unknown) {
      setCommit({ status: 'error', message: describeError(cause) })
    }
  }

  const candidates = state.status === 'ready' ? state.data.candidates : []

  return (
    <Stack direction="column" gap={12} align="start" className={styles.chooser}>
      {state.status === 'ready' ? <TheseFiles data={state.data} /> : null}

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {/*
          Louder and longer-winded than the recording lookup's, because the wait
          is longer: one gated MusicBrainz request per recording in the set,
          then one per album offered. Against the public server that is tens of
          seconds. Saying which of the two halves is running would be nicer and
          would need a stream; saying honestly how long it takes does not.
        */}
        {state.status === 'loading' ? (
          <Stack direction="column" gap={4} align="start" className={styles.waiting}>
            <Text size="sm" weight="medium" block>
              {asked > 0 ? 'Asking MusicBrainz again…' : 'Working out which albums these could be…'}
            </Text>
            <Text size="sm" tone="secondary" block>
              Fonoteca asks MusicBrainz which albums each of these recordings appears on, then
              fetches the track list of the most likely ones so it can measure them against what you
              actually hold. Half a minute is normal; a set of fifty files takes a couple of
              minutes. Nothing else is blocked while it runs.
            </Text>
          </Stack>
        ) : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="warning">Could not gather the albums</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}

        {state.status === 'ready' ? (
          <VisuallyHidden>
            {candidates.length === 0
              ? 'MusicBrainz named no album holding these recordings.'
              : `${candidates.length} album${candidates.length === 1 ? '' : 's'} to choose between.`}
          </VisuallyHidden>
        ) : null}
      </div>

      {state.status === 'ready' ? (
        candidates.length === 0 ? (
          <Text size="sm" tone="secondary" block>
            There is nothing to choose from. MusicBrainz lists no album holding these recordings —
            which is a gap in their data rather than in yours, and it closes when somebody adds the
            release these came from.
          </Text>
        ) : (
          <>
            <Provenance
              data={state.data}
              onRefresh={() => {
                setAnswer(null)
                setCommit({ status: 'idle' })
                setAsked((count) => count + 1)
              }}
            />

            <CandidateList
              label="Which album did these come from?"
              description="These need not all be from one album. The set is whatever shared a candidate list, so a compilation can glue two rips together — answering names one album, the files it lists are filed under it, and everything it does not list stays open as a smaller question. Each one is scored against the set: how much of the album you actually hold, and how closely its printed running times match what your files measure. Coverage is what separates the album from the compilation that reprints half of it; the running times are what separate one pressing from its remaster."
              value={answer}
              onSelect={(selection) => {
                setAnswer(selection)
                if (commit.status === 'error') setCommit({ status: 'idle' })
              }}
              // On the `<fieldset>`, which disables the footer button with it —
              // so an error deliberately leaves both alive. Same reasoning as
              // the recording chooser, and the same two retryable failures: a
              // pass holding the gate, and a provider that did not answer.
              disabled={commit.status === 'sending' || commit.status === 'done'}
              options={candidates.map((candidate) =>
                albumOption(candidate, state.data.maximumDriftMs),
              )}
              refusal={{
                label: 'None of these — none of these files came from any of them',
                description:
                  'Records that you looked and none of these albums is where this music came ' +
                  'from — every file still in this set, not just some of them. If part of it is ' +
                  'one of these albums, save that album first: the rest stay open. Nothing is ' +
                  'written to any file, and no pass will ask about them again.',
              }}
              footer={
                <Stack direction="column" gap={6} align="start">
                  <Stack gap={12} align="center" wrap>
                    <Button
                      variant="primary"
                      size="sm"
                      disabled={
                        answer == null || commit.status === 'sending' || commit.status === 'done'
                      }
                      onClick={() => {
                        if (answer != null) void send(answer)
                      }}
                    >
                      {albumCommitLabel(answer, commit)}
                    </Button>
                    <Text size="xs" tone="tertiary">
                      {albumCommitHint(answer, commit, candidates, state.data.files)}
                    </Text>
                  </Stack>

                  <div role="status" aria-live="polite">
                    {commit.status === 'error' ? (
                      <Text size="xs" tone="danger" block>
                        Nothing was committed. {commit.message}
                      </Text>
                    ) : null}

                    {commit.status === 'done' ? (
                      <AlbumCommitted
                        result={commit.result}
                        onContinue={() => {
                          setAnswer(null)
                          setCommit({ status: 'idle' })
                          setAsked((count) => count + 1)
                        }}
                      />
                    ) : null}
                  </div>
                </Stack>
              }
            />

            {/*
              Two independent cuts, and both are stated. A short list that looked
              complete is the failure the API's own remarks are about: the
              browses can be cut when a component is large, and the tail of
              releases is never looked up at all. Either one can be why the album
              a person is looking for is not here.
            */}
            {state.data.browsed < state.data.recordings ? (
              <Text size="xs" tone="tertiary" block>
                Asked about {state.data.browsed} of the {state.data.recordings} recordings in this
                set. An album that only the other {state.data.recordings - state.data.browsed} would
                have named is not on this list.
              </Text>
            ) : null}

            {candidates.length < state.data.total ? (
              <Text size="xs" tone="tertiary" block>
                These are the {candidates.length} best-supported of {state.data.total} albums
                MusicBrainz named. The other {state.data.total - candidates.length} hold less of
                this set and were not fetched — each one is a request. If none of these is right,
                “none of these” is the accurate answer.
              </Text>
            ) : null}
          </>
        )
      ) : null}
    </Stack>
  )
}

/**
 * How old this answer is, and how to get a newer one.
 *
 * **Evidence with an unstated age is what makes a person distrust the screen.**
 * The ordinary case here is a stored answer: the attribution pass writes a
 * candidate document for every component it refuses, out of the gather it had
 * already paid for, so what a person opens is usually the set the *rule* was
 * looking at when it gave up — which is exactly the right thing to be shown, and
 * exactly the thing that has to say so. MusicBrainz moves; a mirror replicates
 * daily; a score that does not match the website today has an explanation and
 * this is it.
 *
 * The button is the only way to spend the two minutes, and it is a button rather
 * than a default for that reason.
 */
function Provenance({
  data,
  onRefresh,
}: {
  readonly data: components['schemas']['ComponentCandidatesResponse']
  readonly onRefresh: () => void
}) {
  return (
    <Stack gap={8} align="center" wrap>
      <Text size="xs" tone="tertiary">
        {/*
          Age, then reach. Not who gathered it: a stored document is written by
          whichever asked first — usually the attribution pass, sometimes this
          screen — and that is a fact about the code rather than about the
          evidence. What a reader needs is how old it is and how much of the set
          it covers.
        */}
        {`Gathered ${data.fromCache ? when(data.asOfUtc) : 'just now'}, from ${data.browsed} of ${
          data.recordings
        } recording${data.recordings === 1 ? '' : 's'} in this set.`}
      </Text>
      <Button variant="ghost" size="sm" onClick={onRefresh}>
        Ask MusicBrainz again
      </Button>
    </Stack>
  )
}

/**
 * A timestamp as a person would say it.
 *
 * Days rather than a date, because the question a reader has is "is this stale",
 * not "what was the date". Falls back to the raw value rather than to nothing —
 * an unparseable stamp is still an age somebody can read.
 */
function when(iso: string): string {
  const at = Date.parse(iso)
  if (Number.isNaN(at)) return `at ${iso}`

  const days = Math.floor((Date.now() - at) / 86_400_000)

  if (days <= 0) return 'today'
  if (days === 1) return 'yesterday'

  return `${days} days ago`
}

/**
 * The set being decided, listed by what the music is rather than by filename.
 *
 * The evidence panel says how many files and which folders; neither is the thing
 * a person compares an album's track list against. Enrichment has already named
 * every one of these recordings, so the titles are a catalogue read and free.
 */
function TheseFiles({
  data,
}: {
  readonly data: components['schemas']['ComponentCandidatesResponse']
}) {
  const shown = data.fileList.slice(0, FILES_SHOWN)

  return (
    <div className={styles.yourFile}>
      <Stack direction="column" gap={2} align="start">
        <Text size="xs" tone="tertiary" weight="medium" block>
          These {data.files.toLocaleString()} file{data.files === 1 ? '' : 's'}
        </Text>
        {shown.map((file) => (
          <Text key={file.id} size="sm" block>
            {file.title ?? file.path}
            {file.duration != null ? ` · ${file.duration}` : ''}
          </Text>
        ))}
        {data.fileList.length > shown.length ? (
          <Text size="xs" tone="tertiary" block>
            …and {(data.fileList.length - shown.length).toLocaleString()} more.
          </Text>
        ) : null}
      </Stack>
    </div>
  )
}

/**
 * How many of a component's files are named before the list starts counting.
 *
 * A component is an album folder, which reaches two hundred files at the worst
 * on this library. Enough to recognise the record, then the count — the same
 * judgement `FOLDERS_SHOWN` makes.
 */
const FILES_SHOWN = 12

/** One candidate album, with its fit and its whole track list. */
function albumOption(candidate: ComponentCandidateRow, driftToleranceMs: number): CandidateOption {
  const fit: ReleaseFitReadout = {
    coverage: candidate.coverage,
    meanDriftMs: candidate.meanDriftMs,
    filesExplained: candidate.filesExplained,
    slotCount: candidate.trackCount,
    // Distinct tracks, which is what the server matched: `ReleaseFit` gives each
    // track at most one file, so five encodings of one song fill one slot.
    held: candidate.filesExplained,
    trackCount: candidate.trackCount,
    official: candidate.official,
    // Never a tie-break here. The count means "this many other editions fitted
    // exactly as well and one was taken by a rule"; a person choosing is not
    // that, and the decision endpoint writes zero for the same reason.
    editionAlternatives: 0,
  }

  /*
    The plain line, in English and in the order a person reads it: how much of
    the album you hold, then whether the audio agrees. Every figure behind those
    two sentences is in {@link FitSummary} directly below, and the identifiers
    are in the small print — the same three layers the recording rows use.
  */
  const facts = [
    `You hold ${candidate.filesExplained} of its ${candidate.trackCount} track${
      candidate.trackCount === 1 ? '' : 's'
    }`,
    plainFit(candidate.meanDriftMs),
  ]
    .filter((part) => part != null)
    .join(' · ')

  const detail = [
    candidate.year?.toString(),
    candidate.primaryType,
    ...candidate.secondaryTypes,
    candidate.formats,
    candidate.discCount > 1 ? `${candidate.discCount} discs` : null,
    candidate.country,
    candidate.status != null && candidate.status !== 'Official' ? candidate.status : null,
    `MBID ${candidate.mbid}`,
  ]
    .filter((part) => part != null && part !== '')
    .join(' · ')

  /*
    Two badges, and between them they are the documented failure. A five-disc
    box set explaining twenty files outranks the ten-track album beside it on
    every count except the one that matters, and "you hold all of it" is that
    count said out loud. The second is the edition discriminator: the 2015
    remaster of *Off the Wall* matches to 0.00s where three earlier pressings
    sit at 0.76s, on identical track lists.
  */
  const badges = [
    candidate.coverage >= 1 ? (
      <Badge key="complete" tone="success" size="sm">
        You hold all of it
      </Badge>
    ) : null,
    candidate.meanDriftMs != null && candidate.meanDriftMs <= 50 ? (
      <Badge key="exact" tone="info" size="sm">
        Running times match exactly
      </Badge>
    ) : null,
  ].filter((badge) => badge != null)

  return {
    id: candidate.mbid,
    render: (selection: CandidateOptionProps) => (
      <CandidateCard
        image={releaseArt(candidate.mbid)}
        title={candidate.title}
        {...(candidate.artist != null ? { subtitle: candidate.artist } : {})}
        facts={facts}
        // A sentence, not figures.
        factsMono={false}
        detail={detail}
        {...(badges.length > 0 ? { badges: <>{badges}</> } : {})}
        selection={selection}
        fit={<FitSummary fit={fit} driftToleranceMs={driftToleranceMs} layout="row" />}
        disclosureLabel="Track by track"
        disclosureAside={
          <Badge tone="neutral" size="sm" mono>
            {candidate.filesExplained}/{candidate.trackCount}
          </Badge>
        }
      >
        <SlotTable
          caption={`${candidate.title} — every track, held or not`}
          rows={candidate.slots.map(slotRow)}
          driftToleranceMs={driftToleranceMs}
        />
      </CandidateCard>
    ),
  }
}

/**
 * A slot as the table wants it.
 *
 * **`driftMs` is only ever a key when a file landed here.** The component reads
 * an absent key as an empty slot and a `null` value as "one of the two lengths
 * is unknown", and those are different rows on screen — so a blanket
 * `driftMs: row.driftMs` would print "no comparison" against every track the
 * library does not hold, which is a measurement nobody took.
 */
function slotRow(row: ComponentSlotRow): SlotRow {
  return {
    discNumber: row.discNumber,
    position: row.position,
    ...(row.number != null ? { number: row.number } : {}),
    title: row.title,
    ...(row.duration != null ? { duration: row.duration } : {}),
    ...(row.measured != null ? { measured: row.measured } : {}),
    ...(row.path != null
      ? {
          driftMs: row.driftMs,
          file: {
            path: row.path,
            ...(row.sizeBytes != null ? { sizeBytes: row.sizeBytes } : {}),
          },
        }
      : {}),
  }
}

/**
 * What the running times say, as a sentence.
 *
 * **Null is not zero**, and the wording has to keep them apart: a release
 * MusicBrainz prints no lengths for has produced no evidence, and reading that
 * as agreement is what would let the least documented pressing win.
 */
function plainFit(meanDriftMs: number | null): string {
  if (meanDriftMs == null) return 'no printed lengths to compare against'
  if (meanDriftMs <= 50) return 'running times match exactly'
  if (meanDriftMs < 1000)
    return `running times differ by ${(meanDriftMs / 1000).toFixed(2)}s on average`

  return `running times differ by ${(meanDriftMs / 1000).toFixed(1)}s on average`
}

function albumCommitLabel(answer: CandidateSelection | null, commit: ComponentCommitState): string {
  if (commit.status === 'sending') return 'Saving…'
  if (commit.status === 'done') return 'Saved'
  if (answer?.kind === 'none') return 'Save “none of these”'
  return 'Save this album'
}

function albumCommitHint(
  answer: CandidateSelection | null,
  commit: ComponentCommitState,
  candidates: readonly ComponentCandidateRow[],
  files: number,
): string {
  if (commit.status === 'sending') return 'Writing the album into the catalogue…'
  if (commit.status === 'done') return 'Recorded as your decision. No pass will overwrite it.'
  if (answer == null) return 'Pick an album above, or “none of these”.'

  if (answer.kind === 'none') {
    return (
      'Closes this question without an album. Nothing on disk is touched, and the files stop ' +
      'appearing on this list.'
    )
  }

  const chosen = candidates.find((candidate) => candidate.mbid === answer.id)

  if (chosen == null)
    return 'Files this album lists are filed under it. Nothing on disk is touched.'

  const filed = `${chosen.filesExplained} file${chosen.filesExplained === 1 ? '' : 's'}`
  const rest = files - chosen.filesExplained

  return (
    `Files ${filed} under “${chosen.title}” and writes its whole track list into the ` +
    'catalogue. Nothing on disk is touched.' +
    (rest > 0
      ? ` The other ${rest} stay open — this album does not list what they hold, and you can name ` +
        'theirs next.'
      : '')
  )
}

/**
 * What the decision did, once it has landed.
 *
 * **`stillOpen` is the line that has to be here.** A component is a set that
 * shared a candidate set, which is not a promise that every file in it came from
 * one album — so an answer routinely files most of the set and leaves the rest
 * on the worklist. Without this the row count would change under a person with
 * no explanation, and the honest reading of that is "it half worked".
 */
function AlbumCommitted({
  result,
  onContinue,
}: {
  readonly result: ComponentDecision
  readonly onContinue: () => void
}) {
  return (
    <Stack direction="column" gap={2} align="start">
      <Badge tone="success" size="sm">
        {result.decided.toLocaleString()} file{result.decided === 1 ? '' : 's'} decided
      </Badge>
      <Text size="xs" tone="secondary" block>
        {result.detail}
      </Text>
      {result.stillOpen > 0 ? (
        <Stack direction="column" gap={6} align="start" className={styles.continue}>
          <Text size="xs" tone="tertiary" block>
            The remaining {result.stillOpen.toLocaleString()} hold recordings this album does not
            list, so they keep their place in the queue rather than being filed under a record that
            does not name them. One album at a time is how a set holding two rips gets taken apart.
          </Text>
          <Button variant="secondary" size="sm" onClick={onContinue}>
            Ask about the remaining {result.stillOpen.toLocaleString()}
          </Button>
          <Text size="2xs" tone="tertiary" block>
            A fresh gather, because the set has changed — the stored answer described the larger
            one. Half a minute or so.
          </Text>
        </Stack>
      ) : null}
    </Stack>
  )
}
