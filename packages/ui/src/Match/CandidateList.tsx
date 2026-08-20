import { type FieldsetHTMLAttributes, type ReactNode, type Ref, useId } from 'react'

import { Text } from '../Text/Text.tsx'
import type { CandidateCardSelection } from './CandidateCard.tsx'
import styles from './CandidateList.module.css'

export type CandidateSelection =
  | { readonly kind: 'candidate'; readonly id: string }
  | { readonly kind: 'none' }

/** What each option's `render` is handed. Spread it onto a card's `selection`. */
export type CandidateOptionProps = CandidateCardSelection

export type CandidateOption = {
  readonly id: string
  readonly render: (selection: CandidateOptionProps) => ReactNode
}

export type CandidateRefusal = {
  readonly label: ReactNode
  /** Why refusing is a real answer. Wired as the option's description. */
  readonly description?: ReactNode
}

export type CandidateListProps = Omit<
  FieldsetHTMLAttributes<HTMLFieldSetElement>,
  // `onSelect` collides with React's `DOMAttributes.onSelect` (text selection),
  // and `name` would be a second thing called the same word as the radio group's
  // generated one.
  'onSelect' | 'name' | 'children'
> & {
  /**
   * The group's accessible name **and** a visible `<legend>`. Required, and not
   * hideable: this is the only sentence on the screen that says what the screen
   * is for, and a radio group without one announces as an unnamed group of n.
   */
  readonly label: ReactNode
  readonly description?: ReactNode
  /** The current answer. Controlled always — "which thing is this" is the app's. */
  readonly value: CandidateSelection | null
  /**
   * Arrow keys change the selection, because these are native radios. So this
   * must be cheap: no navigation, no network, no confirmation. Committing is a
   * separate control in `footer`.
   */
  readonly onSelect: (selection: CandidateSelection) => void
  /**
   * Rendered in the order given. The list **never sorts** — the ranking is the
   * domain's (coverage × files explained, then coverage, then drift), and a
   * component that reordered its options would be fighting it.
   */
  readonly options: readonly CandidateOption[]
  /** "None of these", as the last radio in the same group. */
  readonly refusal?: CandidateRefusal
  /** Shown when `options` is empty. The refusal still renders below it. */
  readonly empty?: ReactNode
  /** The commit bar. A slot, so the list has no opinion about committing. */
  readonly footer?: ReactNode
  readonly ref?: Ref<HTMLFieldSetElement>
}

/**
 * The options, as a native radio group.
 *
 * **Native `<input type="radio">` in a `<fieldset>`, and that is forced.** A
 * candidate card contains a disclosure toggle and a play button, which rules out
 * `role="radio"` on the card — ARIA forbids focusable descendants inside one and
 * axe's `nested-interactive` fires — and rules out wrapping the card in a
 * `<label>`, since a click anywhere in a label forwards to its control.
 *
 * Against a hand-built `role="radiogroup"` with roving tabindex, native wins on
 * everything that matters here: arrow keys with wrap, the "2 of 6" position, one
 * tab stop for the whole group, the checked marker, and forced-colors rendering,
 * none of which is implemented. And by ADR 0003's own rule a hand-built
 * radiogroup would need an ADR of its own, while this needs none — because it
 * hand-builds no ARIA at all.
 *
 * **"None of these" is the last radio in the same group**, not a link and not a
 * footer button. Refusing is a first-class answer in this domain — a wrong album
 * is worse than a missing one and far harder to notice — and making it an option
 * rather than an escape hatch means the same arrow keys reach it, it announces as
 * "6 of 6", and it survives an empty candidate list, which is exactly when it
 * matters most. It costs one array entry, and it costs that little *because* the
 * radios are native.
 *
 * There is deliberately no `<ul>`. A radio group already announces "1 of 6";
 * adding list semantics on top would give a screen reader two navigational
 * structures for one thing.
 */
export function CandidateList({
  label,
  description,
  value,
  onSelect,
  options,
  refusal,
  empty,
  footer,
  className,
  ...rest
}: CandidateListProps) {
  const base = useId()
  const name = `${base}-candidate`

  return (
    <fieldset className={className ? `${styles.root} ${className}` : styles.root} {...rest}>
      <legend className={styles.legend}>
        <Text size="md" weight="semibold">
          {label}
        </Text>
      </legend>

      {description != null ? (
        <Text size="sm" tone="secondary" block className={styles.description}>
          {description}
        </Text>
      ) : null}

      <div className={styles.options}>
        {options.length === 0 && empty != null ? <div className={styles.empty}>{empty}</div> : null}

        {options.map((option) => {
          const id = `${base}-${option.id}`
          const selected = value?.kind === 'candidate' && value.id === option.id

          return (
            <div key={option.id} className={styles.option}>
              {option.render({
                labelFor: id,
                fitId: `${id}-fit`,
                selected,
                control: (
                  <input
                    type="radio"
                    id={id}
                    name={name}
                    checked={selected}
                    // The evidence describes the option; the identity block
                    // names it. Keeping them apart is what stops the name
                    // becoming a paragraph.
                    aria-describedby={`${id}-fit`}
                    onChange={() => onSelect({ kind: 'candidate', id: option.id })}
                  />
                ),
              })}
            </div>
          )
        })}

        {refusal != null ? (
          <div className={styles.refusal}>
            <input
              type="radio"
              id={`${base}-none`}
              name={name}
              checked={value?.kind === 'none'}
              {...(refusal.description != null ? { 'aria-describedby': `${base}-none-note` } : {})}
              onChange={() => onSelect({ kind: 'none' })}
            />
            <div className={styles.refusalText}>
              <label htmlFor={`${base}-none`}>
                <Text size="sm" weight="medium">
                  {refusal.label}
                </Text>
              </label>
              {refusal.description != null ? (
                <Text size="xs" tone="tertiary" block id={`${base}-none-note`}>
                  {refusal.description}
                </Text>
              ) : null}
            </div>
          </div>
        ) : null}
      </div>

      {footer != null ? <div className={styles.footer}>{footer}</div> : null}
    </fieldset>
  )
}
