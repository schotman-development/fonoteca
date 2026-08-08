/**
 * A switch, its label and its note — design 387–395, and again at 397–405,
 * 506–515 and 780–788.
 *
 * The design draws the label as a plain `<div>` next to the switch, which is
 * how a control ends up with a caption everybody can see and nobody's screen
 * reader can find. This component wires the three pieces together instead:
 *
 *   - the label is a real `<label htmlFor>` pointed at the switch, so the words
 *     are a click target too. A 34×19 track is a small thing to hit, and on the
 *     Rules screen there are five of them in a column.
 *   - the switch is `aria-labelledby` that same label, so the name is computed
 *     from the visible text rather than from a hidden copy of it. `Toggle`
 *     drops its own hidden span when told this, so the name is announced once.
 *   - the note is `aria-describedby`, so "Poll Qobuz every 6 h and grab
 *     anything new" is read to the person deciding whether to turn it on,
 *     rather than only to the person who can see it.
 *
 * `meta` is the one slot the design does not draw. The Rules screen has to say
 * where each value came from — `.env` or an override — and that is a fact about
 * the setting, not part of the sentence explaining it, so it sits at the end of
 * the row rather than inside the note.
 *
 * `onToggle` is `() => void`, unchanged and unwidened, all the way down to
 * `Toggle`. See the note in `Toggle.tsx`.
 */

import type { ReactNode } from 'react'
import { useId } from 'react'

import { Toggle } from '@/design/Toggle/Toggle'
import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/SwitchField/SwitchField.module.css'

export interface SwitchFieldProps extends StyleableProps {
  checked: boolean
  /** `() => void`. It receives nothing — see `Toggle`. */
  onToggle: () => void
  /**
   * The visible label, and the switch's accessible name. A string because it is
   * both at once: a node could not be handed to `aria-label` if the wiring ever
   * had to fall back to one.
   */
  label: string
  /** The sentence under it (design 392). Linked by `aria-describedby`. */
  note?: ReactNode
  /** A right-hand slot — an origin marker, a Reset link, a count. */
  meta?: ReactNode
  disabled?: boolean
}

export function SwitchField({
  checked,
  onToggle,
  label,
  note,
  meta,
  disabled = false,
  className,
}: SwitchFieldProps) {
  const id = useId()
  const labelId = `${id}-label`
  const noteId = `${id}-note`
  const described = note === undefined || note === null ? undefined : noteId

  return (
    <div className={cx(styles.field, disabled ? styles.disabled : undefined, className)}>
      <Toggle
        id={id}
        checked={checked}
        onToggle={onToggle}
        label={label}
        labelledBy={labelId}
        describedBy={described}
        disabled={disabled}
        className={styles.switch}
      />
      <div className={styles.text}>
        <label id={labelId} htmlFor={id} className={styles.label}>
          {label}
        </label>
        {described === undefined ? null : (
          <span id={noteId} className={styles.note}>
            {note}
          </span>
        )}
      </div>
      {meta == null ? null : <span className={styles.meta}>{meta}</span>}
    </div>
  )
}
