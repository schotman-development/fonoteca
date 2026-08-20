import type { HTMLAttributes, ReactNode, Ref } from 'react'

import { Field } from '../Field/Field.tsx'
import { Input } from '../Input/Input.tsx'
import { Text } from '../Text/Text.tsx'
import styles from './CandidateSearch.module.css'

export type CandidateSearchProps = Omit<
  HTMLAttributes<HTMLDivElement>,
  'onChange' | 'defaultValue' | 'children'
> & {
  readonly label?: ReactNode
  readonly value: string
  readonly onValueChange: (value: string) => void
  readonly placeholder?: string
  /** How many are on screen **right now** — the rendered count, not a prediction. */
  readonly shown: number
  readonly total: number
  /**
   * The box has moved and the list has not caught up. Pass
   * `value !== deferredValue`.
   */
  readonly busy?: boolean
  /** Says what the box searches, since it is not obvious that it is local. */
  readonly hint?: ReactNode
  readonly labelHidden?: boolean
  readonly ref?: Ref<HTMLDivElement>
}

const DEFAULT_HINT = 'Filters the candidates already gathered. It does not search MusicBrainz.'

/**
 * A search box over the options, and a count of what survived it.
 *
 * **It filters; it does not search MusicBrainz.** The mirror runs with no Solr on
 * purpose (ADR 0006) and `IMusicBrainzCatalogue` has no search method, so a box
 * that looked like it queried MusicBrainz would be promising something the
 * architecture will not deliver. The default hint says so on screen.
 *
 * The count is the *rendered* count, passed in rather than derived, and it comes
 * from the same `filterCandidates` predicate the caller filtered with — which is
 * the only way the number in the live region and the rows on the page cannot
 * disagree. `useDeferredValue` stays the caller's too: a component that holds no
 * state cannot own it, and `ReleasesPage` already sets that precedent.
 */
export function CandidateSearch({
  label = 'Filter candidates',
  value,
  onValueChange,
  placeholder = 'Title, artist, year, country…',
  shown,
  total,
  busy = false,
  hint = DEFAULT_HINT,
  labelHidden = false,
  className,
  ...rest
}: CandidateSearchProps) {
  return (
    <div className={className ? `${styles.root} ${className}` : styles.root} {...rest}>
      <Field label={label} labelHidden={labelHidden} {...(hint != null ? { hint } : {})}>
        {(control) => (
          <Input
            {...control}
            type="search"
            value={value}
            placeholder={placeholder}
            fullWidth
            onChange={(event) => onValueChange(event.currentTarget.value)}
          />
        )}
      </Field>

      {/*
        `aria-busy` is written as a plain boolean rather than through the
        `flag || undefined` idiom the rest of the package uses for data
        attributes. `aria-busy="false"` is a meaningful ARIA value — it is the
        announcement that the region has settled — while `data-x="false"` is not.
      */}
      <div className={styles.count} role="status" aria-live="polite" aria-busy={busy}>
        <Text size="xs" tone="tertiary">
          {describeCount(shown, total)}
        </Text>
      </div>
    </div>
  )
}

/**
 * Zero is an outcome, not a count. "0 of 12 candidates" reads as arithmetic;
 * "No candidate matches that" reads as the answer it is.
 */
function describeCount(shown: number, total: number): string {
  if (shown === 0) return 'No candidate matches that.'
  if (shown === total) return `${total.toLocaleString()} candidate${total === 1 ? '' : 's'}`
  return `${shown.toLocaleString()} of ${total.toLocaleString()} candidates`
}
