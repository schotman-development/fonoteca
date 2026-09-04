import { Text } from '@fonoteca/ui'
import { useId } from 'react'

import styles from './SortSelect.module.css'

/**
 * How a catalogue list is ordered.
 *
 * A native `<select>` rather than a design system component, for the reason the
 * seating dialog gives: three short options do not need a combobox, and the
 * platform's control brings the role, the typeahead, the keyboard handling and
 * a phone's picker that a hand-built listbox would have to reimplement.
 *
 * Shared between the artist and album lists rather than written twice, because
 * the two screens are the same control with different words in it — and the
 * value goes to the server, since both lists are paged there. Sorting the
 * returned page would order the first slice of a library and look like it had
 * worked.
 */
export function SortSelect<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  readonly label: string
  readonly value: T
  readonly options: ReadonlyArray<readonly [value: T, label: string]>
  readonly onChange: (value: T) => void
}) {
  const id = useId()

  return (
    <div className={styles.field}>
      <label htmlFor={id}>
        <Text size="xs" tone="tertiary">
          {label}
        </Text>
      </label>

      <select
        id={id}
        className={styles.select}
        value={value}
        onChange={(event) => {
          onChange(event.currentTarget.value as T)
        }}
      >
        {options.map(([option, text]) => (
          <option key={option} value={option}>
            {text}
          </option>
        ))}
      </select>
    </div>
  )
}
