/**
 * The toolbar that appears under the artist wall once anything is ticked.
 *
 * **Its actions apply on the press.** There is no form, no draft and no Apply:
 * *Stop watching* writes `monitored: false` to the selection and says so. That
 * is the same shape the single-artist drawer already has — three presses are
 * three one-key patches, not one three-key patch — and here it also *is* the
 * safety argument. A bulk body may only ever carry the key somebody pressed,
 * so the failure the tri-state rule exists to prevent (an untouched control
 * serialised as `false`, unmonitoring a whole selection) has nowhere to happen:
 * there are no untouched controls. `buildBulkPayload` still builds every body,
 * so the rule is enforced rather than merely unreachable.
 *
 * Every action here is reversed by the button beside it, which is what makes
 * one press acceptable on a selection of two hundred. Release types are the
 * exception and are not here: `set`, `add` and `remove` mean three different
 * things to one list, so that edit needs a verb, and a verb needs a form.
 *
 * **The count is the selection, not the page.** Filtering the grid unticks
 * nobody, so this number can legitimately exceed the rows on screen — which is
 * exactly why it is stated rather than left to be read off the ticks. `shown`
 * is only ever the argument to *Select all*.
 *
 * The bar belongs to the selection **mode** rather than to the selection, so it
 * is on screen from the moment the mode is, holding *Select all* and the way
 * out. With nothing ticked the writes are disabled rather than hidden: a
 * toolbar that grows a row of buttons on the first tick moves the control
 * somebody is already reaching for.
 */

import type { MonitorMode } from '@/api/types'
import { Button, cx } from '@/design'
import { enumLabel, MONITOR_MODE_LABELS } from '@/format'
import { MONITOR_MODES } from '@/widgets/BulkBar/bulkPayload'

import styles from '@/widgets/BulkBar/BulkBar.module.css'

export interface BulkBarProps {
  /** Artists ticked, across every filter — not just the ones on screen. */
  count: number
  /** Rows the current filter is showing, which is what *Select all* takes. */
  shown: number
  /** Every row on screen is already ticked, so *Select all* would do nothing. */
  allShownSelected: boolean
  /** A write is in flight: the bar goes inert rather than queueing presses. */
  busy?: boolean
  onSelectAll: () => void
  /** Leave the mode. The screen drops the selection with it. */
  onDone: () => void
  /** Write `monitored` to the whole selection, now. */
  onWatch: (monitored: boolean) => void
  /** Write `monitor_mode` to the whole selection, now. */
  onMode: (mode: MonitorMode) => void
  /** Open the one edit that needs a verb. */
  onEditTypes: () => void
}

export function BulkBar({
  count,
  shown,
  allShownSelected,
  busy = false,
  onSelectAll,
  onDone,
  onWatch,
  onMode,
  onEditTypes,
}: BulkBarProps) {
  // The mode can be on with nothing ticked — that is the state it starts in —
  // and an action that would write to nobody is disabled rather than absent, so
  // the bar does not rearrange itself under the pointer on the first tick.
  const idle = busy || count === 0

  return (
    <div className={styles.bar} role="group" aria-label="Bulk edit">
      {/* Announced: the number changes under a keyboard user who is ticking
          cells several tab stops above it. */}
      <span className={cx(styles.count, 'mono')} role="status">
        {count.toLocaleString()} selected
      </span>

      {allShownSelected ? null : (
        <Button size="sm" variant="quiet" disabled={shown === 0} onClick={onSelectAll}>
          Select all {shown.toLocaleString()}
        </Button>
      )}

      <span className={styles.rule} aria-hidden="true" />

      {/* Three groups, not eight buttons in a row: each is one decision, and
          the tighter gap inside a group than between groups is what says so
          without adding a box round each of them. `data-idle` dims a group
          whose buttons are disabled, because a row of greyed labels reads as
          broken where a dimmed block reads as waiting. */}
      <div className={styles.group} data-idle={idle ? '' : undefined}>
        <span className={styles.groupLabel}>New releases</span>
        <Button size="sm" disabled={idle} onClick={() => onWatch(true)}>
          Watch
        </Button>
        <Button size="sm" disabled={idle} onClick={() => onWatch(false)}>
          Stop watching
        </Button>
      </div>

      <div className={styles.group} data-idle={idle ? '' : undefined}>
        <span className={styles.groupLabel}>Back catalogue</span>
        {MONITOR_MODES.map((mode) => (
          <Button key={mode} size="sm" disabled={idle} onClick={() => onMode(mode)}>
            {enumLabel(mode, MONITOR_MODE_LABELS)}
          </Button>
        ))}
      </div>

      <div className={styles.group} data-idle={idle ? '' : undefined}>
        <Button size="sm" disabled={idle} onClick={onEditTypes}>
          Release types…
        </Button>
      </div>

      <div className={styles.spacer} />

      {/* Never disabled: a stuck request must not trap somebody in the mode. */}
      <Button size="sm" variant="primary" onClick={onDone}>
        Done
      </Button>
    </div>
  )
}
