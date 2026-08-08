/**
 * The switch — design 388–390, 398–400, 507–509, 576–578 and 781–783, five
 * byte-identical copies of one control.
 *
 * There is no `Toggle.module.css`, on purpose: every declaration this control
 * has is the 34×19 track and the 15px knob in `shared/switch.module.css`, and a
 * module here would either restate them or hold nothing. What lives in this
 * file is the two things that are not paint.
 *
 * ── 1. `onToggle` takes NO arguments, and must not be widened ───────────────
 *
 * This is the front-end half of a rule from CLAUDE.md, and it has a scar behind
 * it. `POST /api/albums/{id}/monitor` **toggles on an empty body** and sets an
 * explicit value when given one. The old server-rendered UI carried the calling
 * screen's active filters on the mutating URL so the refreshed table was still
 * the one the user was looking at — and one of those filters is called
 * `monitored`. Read as the album's *new value*, it made the Wanted page's
 * "ignored only" filter unmonitor whatever row was pressed.
 *
 * A `() => void` handler is what designs that class of bug out: a caller cannot
 * pass a value on, because it is never handed one. Which is also why the click
 * handler below is `() => { onToggle() }` and not `onClick={onToggle}` — the
 * latter hands React's `MouseEvent` through as the first argument, and the day
 * somebody widens the type to `(value: unknown) => void` the event silently
 * becomes the value. The signature is not to be widened, and the wrapper is not
 * to be removed.
 *
 * ── 2. it is a `<button role="switch">`, and it is always named ─────────────
 *
 * `role="switch"` + `aria-checked` is the one spelling a screen reader
 * announces as "on"/"off" rather than as "pressed". A checkbox input would be
 * the other honest choice; the design's control is 34×19 with a sliding knob,
 * and re-skinning a native checkbox into that shape means fighting an appearance
 * that differs per platform.
 *
 * `label` is REQUIRED, and it is required as a `string` rather than a node
 * because it has exactly one job: to be the accessible name. A row of eleven
 * unnamed switches is one of the few genuinely unusable things a UI can be —
 * the visible label is a `<div>` beside the control, which is invisible to the
 * accessibility tree unless something points at it. Where such a label exists
 * (`SwitchField` draws one), pass `labelledBy` and the hidden copy is dropped
 * so the name is not read twice.
 */

import sw from '@/design/shared/switch.module.css'
import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

export interface ToggleProps extends StyleableProps {
  /** On or off. This control is fully controlled; it holds no state. */
  checked: boolean
  /**
   * Called on a press. **`() => void` — it receives nothing, ever.** See the
   * note at the top of this file before changing this signature.
   */
  onToggle: () => void
  /**
   * The accessible name. Required — a switch with no name is a switch nobody
   * can use. Rendered visually hidden unless `labelledBy` says a visible
   * element already carries it.
   */
  label: string
  /**
   * The id of the visible element that names this switch. Present ⇒ the hidden
   * copy of `label` is not rendered, so the name is announced once.
   */
  labelledBy?: string
  /** The id of an element describing it — `SwitchField`'s note line. */
  describedBy?: string
  /** Put on the button so a `<label htmlFor>` can point at it. */
  id?: string
  disabled?: boolean
}

export function Toggle({
  checked,
  onToggle,
  label,
  labelledBy,
  describedBy,
  id,
  disabled = false,
  className,
}: ToggleProps) {
  return (
    <button
      type="button"
      id={id}
      role="switch"
      aria-checked={checked}
      aria-labelledby={labelledBy}
      aria-describedby={describedBy}
      disabled={disabled}
      className={cx(sw.track, checked ? sw.trackOn : undefined, className)}
      // Wrapped, never passed by reference: see note 1 above.
      onClick={() => {
        onToggle()
      }}
    >
      <span aria-hidden="true" className={cx(sw.knob, checked ? sw.knobOn : undefined)} />
      {labelledBy === undefined ? <span className="visuallyHidden">{label}</span> : null}
    </button>
  )
}
