/**
 * Identification review — design 589–707, build spec §6.4.
 *
 * The visible half of "exact or nothing". A matcher that refuses to guess is
 * indistinguishable from one doing nothing unless somebody is told, and this
 * screen is the telling.
 *
 * Corrections to the design, all of them the same correction: **there is no
 * confidence score anywhere in this application.**
 *
 *  - the auto-accept threshold band (599–605) keeps its geometry and carries the
 *    real split instead: `EnrichmentStatusOut.autonomy`, how much of the library
 *    was identified with nobody involved, computed server-side over entities by
 *    the same `review_picker_for()` the list and the badge use. The slider is
 *    gone because no continuous quantity exists to slide along — and a bar that
 *    auto-accepted above it would be the tie-break `coverage.solve_album()`
 *    refuses to contain, written into every file as `MUSICBRAINZ_ALBUMID`. What
 *    stands in its place maps to a choice that is real: which subset of the
 *    review list to look at, including the dismissals, which are otherwise
 *    unreachable and are the only rows a person can change their mind about.
 *  - `q.scoreLabel` becomes the row's `state` and `source`.
 *  - the stack card's big amber figure becomes the state, captioned with the
 *    source that answered.
 *  - "Fingerprint evidence" is re-titled **Evidence** and shows what the work
 *    row actually holds: attempts, when it last ran, and why it stopped.
 *
 * `state_explanation` is **server-authored** and rendered as it arrives. It is
 * domain knowledge — which states wait on a person and which wait on an input —
 * and a sentence per state hard-coded here would be a second copy of a rule.
 *
 * The before/after panels are built from what the API really publishes: the
 * row's current identity on the left, the proposal it is holding on the right.
 * A row holding nothing says so rather than showing an invented file tree.
 */

import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'

import {
  useAcceptEnrichment,
  useEnrichmentReview,
  useEnrichmentStatus,
  useMeta,
  useRejectEnrichment,
  useReopenEnrichment,
} from '@/api/queries'
import type { EnrichmentReviewOut, EnrichmentSource } from '@/api/types'
import {
  Artwork,
  Button,
  Chip,
  CodeBlock,
  EmptyState,
  Eyebrow,
  Figure,
  KeyValue,
  KeyValueList,
  Meter,
  PageError,
  PageLoading,
  Pips,
  cx,
  initialsOf,
  useToast,
} from '@/design'
import { EM_DASH, enumLabel, fmtAgo, fmtCount } from '@/format'

import {
  IdentifyPicker,
  type IdentifyTarget,
} from '@/screens/identify/IdentifyPicker'
import styles from '@/screens/identify/Identify.module.css'

/** The states a person is being asked about. `meta.review_states` is the truth;
 *  this is the first-frame fallback only. */
const REVIEW_STATES_FALLBACK = ['ambiguous', 'no_key']

/** The one state a person can put a row into, and so the one the review list
 *  has to be asked for by name. Its rows are the only ones `Reopen` applies to. */
const DISMISSED = 'rejected'

type Mode = 'split' | 'stack'

function modeFrom(value: string | null): Mode {
  return value === 'stack' ? 'stack' : 'split'
}

export default function Identify() {
  const [params, setParams] = useSearchParams()
  const mode = modeFrom(params.get('review'))
  const toast = useToast()

  // The filter lives in the URL and therefore in the query key — never on a
  // mutating URL, which is the collision the monitor endpoint was bitten by.
  // `null` is the server's own default (the actionable review states), and
  // `cleanParams` drops the key entirely, so the default and the "Needs you"
  // chip genuinely share one cache entry rather than quietly running two.
  const stateFilter = params.get('state')

  const review = useEnrichmentReview(stateFilter ? { state: stateFilter } : {})
  const status = useEnrichmentStatus()
  const meta = useMeta()
  const accept = useAcceptEnrichment()
  const reject = useRejectEnrichment()
  const reopen = useReopenEnrichment()

  const autonomy = status.data?.autonomy

  const items = review.data?.items ?? []
  const [index, setIndex] = useState(0)
  const [resolved, setResolved] = useState(0)
  const [target, setTarget] = useState<IdentifyTarget | null>(null)

  // The list shrinks under a resolution and grows on a poll; an index past the
  // end for one render is ordinary, so it is clamped rather than guarded at
  // every read.
  const current: EnrichmentReviewOut | undefined =
    items.length === 0 ? undefined : items[Math.min(index, items.length - 1)]

  const reviewStates = meta.data?.review_states ?? REVIEW_STATES_FALLBACK
  const canReject = current !== undefined && reviewStates.includes(current.state)
  const isDismissed = current !== undefined && current.state === DISMISSED

  // A Meter's `null` is not a zero: nothing has counted the library until the
  // status payload arrives, and an empty library has no share to report either.
  const share =
    autonomy === undefined || autonomy.entities === 0
      ? null
      : autonomy.automatic / autonomy.entities

  function advance() {
    setResolved((n) => n + 1)
    setIndex(0)
  }

  function filterBy(next: string | null) {
    // The list identity changed, so the selection cannot survive it.
    setIndex(0)
    setParams((prev) => {
      const params = new URLSearchParams(prev)
      if (next === null) params.delete('state')
      else params.set('state', next)
      return params
    })
  }

  function openPicker(item: EnrichmentReviewOut) {
    const source = item.identify_sources[0] ?? item.source
    setTarget({
      entityType: item.entity_type,
      entityId: item.entity_id,
      source,
      name: item.name,
    })
  }

  function doAccept(item: EnrichmentReviewOut) {
    if (item.suggested_release_type === null) {
      openPicker(item)
      return
    }
    accept.mutate(
      { entityType: item.entity_type, entityId: item.entity_id },
      {
        onSuccess: (result) => {
          toast(result.message, result.applied ? 'ok' : 'neutral')
          if (result.applied) advance()
          else openPicker(item)
        },
        onError: (error: Error) => toast(error.message, 'bad'),
      },
    )
  }

  function doReject(item: EnrichmentReviewOut) {
    reject.mutate(
      {
        entityType: item.entity_type,
        entityId: item.entity_id,
        payload: { source: item.source as EnrichmentSource },
      },
      {
        onSuccess: () => {
          toast(
            `Rejected — ${item.name} will not be asked about again. Reopen it to change your mind.`,
            'neutral',
          )
          advance()
        },
        onError: (error: Error) => toast(error.message, 'bad'),
      },
    )
  }

  /** The way back out of a dismissal — the only reason `state=rejected` is
   *  reachable at all. `reject` is already disabled for these rows. */
  function doReopen(item: EnrichmentReviewOut) {
    reopen.mutate(
      {
        entityType: item.entity_type,
        entityId: item.entity_id,
        source: item.source,
      },
      {
        onSuccess: (result) => {
          toast(result.message, 'ok')
          advance()
        },
        onError: (error: Error) => toast(error.message, 'bad'),
      },
    )
  }

  /** What the primary button does for the row in front of you. */
  function doPrimary(item: EnrichmentReviewOut) {
    if (item.state === DISMISSED) doReopen(item)
    else doAccept(item)
  }

  function skip() {
    if (items.length === 0) return
    setIndex((i) => (i + 1) % items.length)
  }

  // The stack's three key hints, wired for real (694–696). Ignored while a
  // field has focus and while the picker is open — a drawer with a search box
  // in it must not have `x` mean "reject".
  useEffect(() => {
    if (mode !== 'stack' || current === undefined || target !== null) return
    function onKeyDown(event: KeyboardEvent) {
      const el = event.target
      if (
        el instanceof HTMLInputElement ||
        el instanceof HTMLTextAreaElement ||
        (el instanceof HTMLElement && el.isContentEditable)
      ) {
        return
      }
      if (event.metaKey || event.ctrlKey || event.altKey) return
      const item = current
      if (item === undefined) return
      const key = event.key.toLowerCase()
      if (key === 'a') doPrimary(item)
      else if (key === 'x') {
        if (canReject) doReject(item)
      } else if (event.key === 'ArrowRight') skip()
      else return
      event.preventDefault()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  })

  const before = current === undefined ? [] : [
    current.name,
    `source · ${current.source}`,
    `state · ${enumLabel(current.state) || current.state}`,
  ]
  const after =
    current === undefined
      ? []
      : current.suggested_release_type === null
        ? ['nothing proposed', 'a person has to say which record this is']
        : [`release type · ${current.suggested_release_type}`]

  return (
    <>
      <section className={styles.section} aria-labelledby="identify-title">
        <div className={styles.header}>
          <div className={styles.headerMain}>
            <h1 className={styles.title} id="identify-title">
              Identification review
            </h1>
            <p className={styles.lede}>
              {`${fmtCount(items.length, 'entity', 'entities')} ${
                stateFilter === DISMISSED
                  ? 'you have dismissed'
                  : 'the matchers refused to guess at'
              }. ${resolved} resolved this session.`}
            </p>
          </div>
          <Button
            size="sm"
            className={styles.modeButton}
            onClick={() =>
              setParams((prev) => {
                const next = new URLSearchParams(prev)
                if (mode === 'split') next.set('review', 'stack')
                else next.delete('review')
                return next
              })
            }
          >
            review: {mode}
          </Button>
        </div>

        {/* Design 599–605, the band the slider stood in. What it carries now is
            a partition, not a score: a share of the library identified with
            nobody involved, and the filters that say which subset of the
            remainder you are looking at. Nothing here is a threshold. */}
        <div className={styles.band}>
          <span className={styles.bandLabel}>Identified without you</span>
          <div className={styles.bandShare}>
            <Meter
              className={styles.bandMeter}
              value={share}
              thickness={4}
              label={
                autonomy === undefined || autonomy.entities === 0
                  ? 'Nothing has counted the library yet'
                  : `${autonomy.automatic} of ${autonomy.entities} identified without a person`
              }
            />
            <span className={cx(styles.bandPct, 'mono')}>
              {share === null ? EM_DASH : `${Math.round(share * 100)}%`}
            </span>
          </div>
          {autonomy === undefined ? null : (
            <span className={styles.bandCaption}>
              {`${fmtCount(autonomy.automatic, 'entity', 'entities')} of ${autonomy.entities} in the library`}
            </span>
          )}
          <div className={styles.bandSpacer} />
          {/* Static text, not a chip: there is no review-list filter that would
              show these rows, because they are correctly unpressable — they are
              waiting on an `fpcalc`, or on an id another rung will hand them,
              and `_rearm_stranded()` is what notices. A zero here is a counted
              zero and is shown as one; the null-vs-zero rule governs the share
              above, which is a ratio nothing may have measured yet. */}
          {autonomy === undefined ? null : (
            <span className={styles.bandWaiting}>
              {`${autonomy.waiting_input} waiting on an input`}
            </span>
          )}
          <div className={styles.bandFilters}>
            <Chip
              size="md"
              selected={stateFilter === null}
              count={autonomy?.waiting_person}
              onClick={() => filterBy(null)}
            >
              Needs you
            </Chip>
            {/* Generated from `meta.review_states`, never spelled here: the
                screen already reads that list for `canReject`, and a second
                copy is a rule that can disagree with itself. */}
            {reviewStates.map((state) => (
              <Chip
                key={state}
                size="md"
                selected={stateFilter === state}
                onClick={() => filterBy(state)}
              >
                {enumLabel(state) || state}
              </Chip>
            ))}
            <Chip
              size="md"
              selected={stateFilter === DISMISSED}
              count={autonomy?.dismissed}
              onClick={() => filterBy(DISMISSED)}
            >
              Dismissed
            </Chip>
          </div>
        </div>

        {review.isError ? (
          <PageError
            message={review.error.message}
            action={<Button onClick={() => void review.refetch()}>Retry</Button>}
          />
        ) : review.isLoading ? (
          <PageLoading rows={5} title={false} label="Loading the review list" />
        ) : current === undefined ? (
          <EmptyState
            glyph="✓"
            title="Queue clear"
            note={`${resolved} resolved this session. Nothing is waiting on a decision.`}
          />
        ) : mode === 'split' ? (
          <div className={styles.split}>
            <div className={styles.queueList}>
              {items.map((item, i) => (
                <button
                  key={`${item.entity_type}:${item.entity_id}:${item.source}`}
                  type="button"
                  className={cx(
                    styles.queueCard,
                    item === current ? styles.queueCardCurrent : undefined,
                  )}
                  aria-current={item === current ? 'true' : undefined}
                  onClick={() => setIndex(i)}
                >
                  <Artwork
                    seed={item.entity_id}
                    initials={initialsOf(item.name, 'chars')}
                    shape={item.entity_type === 'artist' ? 'circle' : 'square'}
                    size={40}
                  />
                  <span className={styles.queueBody}>
                    <span className={styles.queueName}>{item.name}</span>
                    <span className={cx(styles.queueMeta, 'mono')}>
                      {item.state} · {item.source}
                    </span>
                  </span>
                </button>
              ))}
            </div>

            <div className={styles.detail}>
              <div className={styles.detailHead}>
                <Eyebrow as="h2">Candidate match</Eyebrow>
                <span className={styles.reason}>{current.reason ?? EM_DASH}</span>
              </div>

              <p className={styles.empty}>{current.state_explanation}</p>

              <div className={styles.diff}>
                <div>
                  <div className={styles.panelLabel}>On disk</div>
                  <CodeBlock content={before} spacing="loose" />
                </div>
                <div>
                  <div className={cx(styles.panelLabel, styles.panelLabelProposed)}>
                    Proposed ({current.source})
                  </div>
                  <CodeBlock content={after} tone="accent" spacing="loose" />
                </div>
              </div>

              <Eyebrow as="h3">Evidence</Eyebrow>
              <KeyValueList ruled className={styles.evidence}>
                <KeyValue label="Attempts" value={String(current.attempts)} />
                <KeyValue
                  label="Last attempt"
                  value={
                    current.last_attempt_at === null
                      ? EM_DASH
                      : fmtAgo(current.last_attempt_at)
                  }
                />
                <KeyValue label="State" value={current.state} />
                <KeyValue label="Why" value={current.reason} />
              </KeyValueList>

              <div className={styles.actions}>
                <Button
                  variant="primary"
                  size="lg"
                  loading={accept.isPending || reopen.isPending}
                  onClick={() => doPrimary(current)}
                >
                  {isDismissed
                    ? 'Reopen'
                    : current.suggested_release_type === null
                      ? 'Find a match…'
                      : `Accept & apply ${current.suggested_release_type}`}
                </Button>
                <Button
                  size="lg"
                  disabled={!canReject}
                  loading={reject.isPending}
                  onClick={() => doReject(current)}
                >
                  Reject
                </Button>
                <Button size="lg" onClick={skip}>
                  Skip
                </Button>
                <div className={styles.actionsSpacer} />
                <Button size="lg" onClick={() => openPicker(current)}>
                  Search {current.identify_sources[0] ?? current.source}…
                </Button>
              </div>
            </div>
          </div>
        ) : (
          <div className={styles.stack}>
            <div className={styles.stackCard}>
              <div className={styles.stackHead}>
                <Artwork
                  seed={current.entity_id}
                  initials={initialsOf(current.name, 'chars')}
                  shape={current.entity_type === 'artist' ? 'circle' : 'square'}
                  size={84}
                />
                <div className={styles.stackIdentity}>
                  <div className={cx(styles.stackFolder, 'mono')}>
                    {current.entity_type} · {current.entity_id}
                  </div>
                  <div className={styles.stackTitle}>{current.name}</div>
                  <div className={styles.stackCredit}>
                    {current.artist_name ?? current.state_explanation}
                  </div>
                </div>
                <Figure value={current.state} caption={current.source} size="lg" tone="warn" />
              </div>

              <CodeBlock content={after} tone="accent" spacing="loose" />

              <div className={styles.stackActions}>
                <Button
                  variant="primary"
                  size="block"
                  loading={accept.isPending || reopen.isPending}
                  onClick={() => doPrimary(current)}
                >
                  {isDismissed
                    ? 'Reopen'
                    : current.suggested_release_type === null
                      ? 'Find a match'
                      : 'Accept'}{' '}
                  <span className={styles.key}>A</span>
                </Button>
                <Button
                  size="block"
                  disabled={!canReject}
                  loading={reject.isPending}
                  onClick={() => doReject(current)}
                >
                  Reject <span className={styles.key}>X</span>
                </Button>
                <Button size="lg" onClick={skip}>
                  Later <span className={styles.key}>→</span>
                </Button>
              </div>
            </div>

            <Pips
              className={styles.pips}
              total={items.length}
              current={Math.min(index, items.length - 1)}
              onSelect={setIndex}
              label="Review queue"
            />
          </div>
        )}
      </section>

      {/* Outside the polled list's tree: a background refetch re-renders the
          list underneath and never unmounts a picker somebody is using. */}
      <IdentifyPicker
        target={target}
        onClose={() => setTarget(null)}
        onIdentified={advance}
      />
    </>
  )
}
