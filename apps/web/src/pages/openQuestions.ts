/**
 * How a refusal reads on screen.
 *
 * `GET /api/catalogue/matching` returns the outcome enum's own name — `Unknown`,
 * `NoRecording`, `NoConfidentFit` — and this is the one place those become
 * English. It is a sibling of {@link CERTAINTY} rather than an extension of it,
 * for a reason worth stating: three enums feed this worklist, and two of the
 * names in them collide. `AcoustIdOutcome.LookupFailed` and
 * `ReleaseAttributionOutcome.LookupFailed` are different sentences about
 * different providers, and one flat table keyed on the bare name could only hold
 * one of them.
 *
 * What makes a flat table safe here is the endpoint's guarantee rather than
 * luck: the two colliding names are `LookupFailed` and `NotAttempted`, and
 * neither is ever returned — a lookup that did not answer is transient and stays
 * on the worklist, and a file the pass has not reached is a queue position. So
 * the seven names that *can* arrive are distinct, and the attribution three are
 * read straight out of `CERTAINTY` rather than reworded here.
 *
 * The tones follow the rule the album screens already keep to. Refusing is a
 * first-class answer, not a failure, so nothing here is `danger`: a screen that
 * shouted about a refusal would be arguing with the rule that produced it.
 */

import type { BadgeTone } from '@fonoteca/ui'

import { CERTAINTY } from './certainty.ts'

/** Why a question is open, in the words that go on the badge. */
export type OpenReason = {
  readonly label: string
  readonly tone: BadgeTone
  readonly note: string
  /**
   * What a person can actually do about it, in one sentence.
   *
   * Separate from {@link note} because they answer different questions — the
   * note says why the pass refused, this says whether anything is waiting on
   * *you* — and a screen that only ever explained itself is the thing this
   * worklist was accused of being.
   *
   * Several of these have no local action, and saying so plainly is the answer
   * rather than a gap in the table. "Nothing here changes until somebody submits
   * this audio" is a decision a person can take — to leave it alone — and it is
   * the one they cannot take from a row that only names a refusal.
   */
  readonly next: string
}

/**
 * The identification and enrichment refusals.
 *
 * Four of these five say something quite different about what a person can do
 * next, which is exactly why the catalogue keeps them apart instead of leaving
 * one null column to mean all of them. `Ambiguous` has a right answer a rule
 * declined to guess at; `BelowThreshold` has no answer worth having;
 * `NoRecording` is a gap in AcoustID's links that submitting would close; and
 * `Unknown` is audio nobody has ever submitted. Rewording them into one
 * "unidentified" would throw that away at the last step.
 */
const IDENTITY: Readonly<Record<string, OpenReason>> = {
  Unknown: {
    label: 'Never heard of it',
    tone: 'neutral',
    note:
      'AcoustID has never been sent this audio, so there was nothing to compare against. Live ' +
      'sets, bootlegs, private recordings and field recordings land here legitimately, and no ' +
      'amount of re-asking will change the answer until somebody submits it.',
    next:
      'Nothing here changes until somebody submits this audio to AcoustID. Re-running the pass ' +
      'spends turns at the rate limit and comes back with the same answer.',
  },
  Ambiguous: {
    label: 'Two answers, neither clearly ahead',
    tone: 'warning',
    note:
      'Two AcoustID clusters meant genuinely different audio and neither won by enough to write ' +
      'into the file. This is the one refusal with a right answer already in it — the rule ' +
      'declined to pick, rather than failing to find.',
    next:
      'Yours to settle: open it, compare each recording against your file — length is usually ' +
      'what tells them apart — and pick the one that matches, or say none of them do.',
  },
  BelowThreshold: {
    label: 'Weak match',
    tone: 'warning',
    note:
      'Something matched, but too weakly to write into a file. Old, noisy or sparsely-submitted ' +
      'audio lands here, and no margin will help it — the answer, if there is one, comes from ' +
      'somewhere other than the fingerprint.',
    next:
      'Worth opening anyway: a weak score against a much-submitted recording is a different ' +
      'thing from a weak score against almost nothing, and you can tell them apart on the ' +
      'screen. Loosening the rule instead would mislabel these rather than identify them.',
  },
  NoRecording: {
    label: 'Known audio, no recording',
    tone: 'neutral',
    note:
      'AcoustID recognises this audio and links it to no MusicBrainz recording. Ordinary rather ' +
      'than broken: a cluster is a fingerprint grouping, and connecting one to MusicBrainz is a ' +
      'separate act of curation that plenty are still waiting for.',
    next:
      'Closes when somebody links the cluster to a MusicBrainz recording. Until then this is a ' +
      'gap in their data, not in yours, and re-running enrichment cannot close it.',
  },
  ReopenedByPerson: {
    label: 'You said this was wrong',
    tone: 'warning',
    note:
      'A pass matched these files confidently and you disagreed, so they gave up the recording ' +
      'and album it chose. The commonest case is a live set matched to the studio recordings of ' +
      'the same songs — right title, right length, wrong performance.',
    next:
      'Yours to settle, and only yours: no pass will look at these again, because re-asking the ' +
      'same providers the same question returns the same answer. Match the folder to the right ' +
      'album — adding it to MusicBrainz first if it is not there yet.',
  },
  RecordingNotFound: {
    label: 'The recording is gone',
    tone: 'warning',
    note:
      'AcoustID names a MusicBrainz recording that MusicBrainz no longer holds — merged away ' +
      'since AcoustID last saw it. The audio is identified; the thing it was identified as has ' +
      'moved.',
    next:
      'Worth re-running enrichment after the mirror has replicated: the recording was merged ' +
      'rather than deleted, so the catalogue it moved into may already be there.',
  },
}

/**
 * The attribution refusals, whose label and note come from {@link CERTAINTY}.
 *
 * Only the next step is written here. `CERTAINTY` is shared with the album
 * screens, where "what should I do about this" is not the question being asked —
 * a release page states how firmly the edition was decided and stops, and giving
 * it a call to action would turn a fact into a nag on several hundred rows.
 */
const ATTRIBUTION_NEXT: Readonly<Record<string, string>> = {
  NoConfidentFit:
    'Yours to settle: open it and Fonoteca asks MusicBrainz again, this time listing every album ' +
    'these recordings appear on and scoring each one against the set — how much of the album you ' +
    'hold, and how closely the running times agree. A set need not be one album, so answering ' +
    'names one and leaves the files it does not list open: two rips glued together by a ' +
    'compilation are taken apart one album at a time.',
  NoCandidate:
    'MusicBrainz holds no release containing this recording at all, so nothing local will move ' +
    'it. Re-running attribution against a freshly replicated mirror is the only thing that can.',
}

/**
 * The refusals a person can actually settle from this screen.
 *
 * **One table, read by both the list and the dialog**, because the two used to
 * decide it separately and a screen that offers a chooser for a refusal the list
 * called unanswerable — or the reverse — is worse than either behaviour on its
 * own.
 *
 * The first two are AcoustID saying "here are the answers and I will not
 * choose", which is precisely the refusal that has a set worth showing: the
 * fingerprint is stored, so the candidates can be *recovered* for one request
 * and no disk access.
 *
 * `NoConfidentFit` is the third, and it was the one this screen used to say
 * could never be here. Recovering an album's candidates means browsing
 * MusicBrainz for each recording in the component, which is genuinely a pass
 * when a component is being *formed* — the gather expands outwards through every
 * release it finds. Re-asking about a component that already exists expands into
 * nothing: its files are written down in the timestamp they share, so the work
 * is one browse per recording and one lookup per album offered. Bounded, and a
 * wait rather than a job.
 *
 * The rest still cannot. `Unknown` is audio AcoustID has never heard,
 * `NoRecording` is a cluster MusicBrainz links nothing to, and `NoCandidate` is
 * MusicBrainz holding no release with this recording on it at all — re-asking
 * any of the three spends a turn at the rate limit to reconfirm an emptiness the
 * catalogue already holds.
 */
const ANSWERABLE: ReadonlySet<string> = new Set(['Ambiguous', 'BelowThreshold', 'NoConfidentFit'])

/** Whether opening this refusal puts a real choice in front of somebody. */
export function isAnswerable(reason: string): boolean {
  return ANSWERABLE.has(reason)
}

/** The fallback, so an outcome added to the API renders as itself rather than as nothing. */
const UNNAMED = (reason: string): OpenReason => ({
  label: reason,
  tone: 'neutral',
  note: '',
  next: '',
})

/**
 * One outcome name, in English.
 *
 * Identification and enrichment first, then attribution — the order the passes
 * run in, and the order a collision would have to be resolved in if the
 * endpoint's guarantee above ever stopped holding.
 */
export function whyOpen(reason: string): OpenReason {
  const identity = IDENTITY[reason]
  if (identity !== undefined) return identity

  const certainty = CERTAINTY[reason]
  if (certainty === undefined) return UNNAMED(reason)

  return {
    label: certainty.label,
    tone:
      certainty.tone === 'warning' ? 'warning' : certainty.tone === 'ok' ? 'success' : 'neutral',
    note: certainty.note,
    next: ATTRIBUTION_NEXT[reason] ?? '',
  }
}

/**
 * Which unit a refusal is counted in.
 *
 * The endpoint answers this per item — `release` for a component, `recording`
 * for a file — but not per *reason*, and the reason is what the page groups by.
 * So the items win where there are any, and the tables above are the fallback
 * for a reason with no items at all on this page — which means the endpoint's
 * own `take` cut them, not the caller's row cap: this is asked of the whole
 * response, before any group decides how many of its rows to print.
 *
 * The fallback is sound rather than lucky: `IDENTITY` holds exactly the
 * identification and enrichment refusals, which are always one file, and
 * `CERTAINTY` holds the attribution ones, which are always a component. An
 * outcome added to the API and to neither table reads as `recording`, which is
 * the unit that undercounts rather than the one that invents a set of files.
 */
export function kindOf(reason: string, items: readonly { readonly kind: string }[]): OpenKind {
  const first = items[0]
  if (first !== undefined) return first.kind === 'release' ? 'release' : 'recording'

  return IDENTITY[reason] === undefined && CERTAINTY[reason] !== undefined ? 'release' : 'recording'
}

/** The two units the worklist is open in. Matches the endpoint's `kinds`. */
export type OpenKind = 'release' | 'recording'
