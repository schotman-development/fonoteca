import { type KeyboardEvent, type ReactNode, useId, useRef } from 'react'

import styles from './Tabs.module.css'

export type TabsProps<K extends string> = {
  readonly tabs: readonly { readonly key: K; readonly label: string }[]
  /** The tab list's accessible name. */
  readonly label: string
  readonly selected: K
  readonly onSelect: (key: K) => void
  /** Rendered for the selected tab only. */
  readonly panel: (key: K) => ReactNode
}

/**
 * The WAI-ARIA tabs pattern with automatic activation: arrows move and select,
 * Home and End jump, and only the selected tab is in the tab order.
 *
 * Controlled, because the pages that use it move between tabs from inside a
 * panel ("Read more", "All works") and need to own the selection to do it.
 *
 * Every panel element exists and the others are `hidden`, so each tab's
 * `aria-controls` resolves — an id that does not exist is an axe failure, and
 * only in the states nobody screenshots.
 */
export function Tabs<K extends string>({ tabs, label, selected, onSelect, panel }: TabsProps<K>) {
  const id = useId()
  const list = useRef<HTMLDivElement>(null)

  const move = (event: KeyboardEvent<HTMLDivElement>) => {
    const index = tabs.findIndex((tab) => tab.key === selected)
    const next =
      event.key === 'ArrowRight'
        ? (index + 1) % tabs.length
        : event.key === 'ArrowLeft'
          ? (index - 1 + tabs.length) % tabs.length
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? tabs.length - 1
              : null
    const tab = next == null ? undefined : tabs[next]
    if (tab == null) return
    event.preventDefault()
    onSelect(tab.key)
    list.current?.querySelector<HTMLElement>(`[data-tab="${tab.key}"]`)?.focus()
  }

  return (
    <div className={styles.tabs}>
      <div ref={list} role="tablist" aria-label={label} className={styles.list} onKeyDown={move}>
        {tabs.map((tab) => (
          <button
            key={tab.key}
            id={`${id}tab-${tab.key}`}
            data-tab={tab.key}
            type="button"
            role="tab"
            className={styles.tab}
            aria-selected={tab.key === selected}
            aria-controls={`${id}panel-${tab.key}`}
            tabIndex={tab.key === selected ? 0 : -1}
            onClick={() => onSelect(tab.key)}
          >
            {tab.label}
          </button>
        ))}
      </div>
      {tabs.map((tab) => (
        <div
          key={tab.key}
          id={`${id}panel-${tab.key}`}
          role="tabpanel"
          aria-labelledby={`${id}tab-${tab.key}`}
          hidden={tab.key !== selected}
        >
          {tab.key === selected ? panel(tab.key) : null}
        </div>
      ))}
    </div>
  )
}
