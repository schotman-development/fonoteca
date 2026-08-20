// biome-ignore-all lint/a11y/noNoninteractiveElementToInteractiveRole: the listbox and its options are the combobox pattern's own markup — a `ul` of `li`s is the element ARIA's listbox is built from, not a costume on a list. File-level because the diagnostic lands on the attribute, where a per-node suppression cannot reach.

import {
  type KeyboardEvent as ReactKeyboardEvent,
  type ReactNode,
  useCallback,
  useEffect,
  useId,
  useRef,
  useState,
} from 'react'

import styles from './CommandBar.module.css'

export type Command = {
  readonly id: string
  /** The visible row, and what the filter matches on first. */
  readonly label: string
  /** A short classifier at the end of the row — "Go to", "Theme". Also searched. */
  readonly kind?: string
  /** Words the filter matches but the row does not show: synonyms, old names. */
  readonly keywords?: readonly string[]
  readonly onSelect: () => void
}

export type CommandBarProps = {
  readonly commands: readonly Command[]
  /** The text in the closed bar. */
  readonly triggerLabel?: string
  readonly placeholder?: string
  /** Accessible name for the trigger, the dialog, the input and the listbox. */
  readonly label?: string
  /** Registers the document-level ⌘K / Ctrl K listener. */
  readonly shortcut?: boolean
  /** Opens on mount. For stories and tests — the palette is otherwise its own. */
  readonly defaultOpen?: boolean
  readonly className?: string
}

/**
 * `⌘K` on Apple hardware, `Ctrl K` everywhere else.
 *
 * Read once, from the user agent, because it is a fact about the keyboard in
 * front of the person and not about the render. The modifier check itself
 * accepts *either* key on every platform — telling someone the wrong shortcut
 * is a cosmetic bug, refusing the one they pressed is not.
 */
const SHORTCUT_HINT = /Mac|iPhone|iPad/i.test(globalThis.navigator?.userAgent ?? '')
  ? '⌘K'
  : 'Ctrl K'

const haystack = (command: Command): string =>
  [command.label, command.kind ?? '', ...(command.keywords ?? [])].join(' ').toLowerCase()

/**
 * Every whitespace-separated term must appear somewhere in the command's text.
 *
 * Substrings, not fuzzy subsequences: a fuzzy matcher ranks, and a ranking that
 * reorders the list under the cursor between keystrokes is how you select the
 * wrong command. This ordering is the caller's, unchanged, always.
 */
export function filterCommands(commands: readonly Command[], query: string): readonly Command[] {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean)
  if (terms.length === 0) return commands
  return commands.filter((command) => {
    const text = haystack(command)
    return terms.every((term) => text.includes(term))
  })
}

/**
 * The command bar: a field-shaped trigger that opens a filterable list of
 * commands — the WAI-ARIA **combobox with list autocomplete**, inside a modal
 * **dialog**. ADR 0010 records why both patterns landed in one component and
 * what each of them costs.
 *
 * Two things carry most of the accessibility work, and both are the platform's:
 *
 * **The overlay is a native `<dialog>` opened with `showModal()`.** That is the
 * focus trap, the focus restoration, the `Escape` handler, the inert background
 * and the top-layer stacking — five hand-written behaviours ADR 0003 budgeted
 * for, none of which we now maintain. It is also why there is no z-index here:
 * the top layer is above every stacking context, including the sticky rail.
 *
 * **Focus never leaves the input.** Options are not tabbable and are not
 * focused; the active one is named by `aria-activedescendant`, which is what
 * lets typing keep working while the selection moves. Arrow keys move it,
 * `Enter` runs it, the pointer sets it on hover so the two never disagree
 * about which row is armed.
 *
 * The list is always rendered, `hidden` when nothing matches rather than
 * unmounted, because `aria-controls` pointing at an id that is not in the
 * document is an `aria-valid-attr-value` failure — and the empty query is
 * exactly the state a story opens in.
 */
export function CommandBar({
  commands,
  triggerLabel = 'Search or jump to…',
  placeholder = 'Type a page or a command…',
  label = 'Command bar',
  shortcut = true,
  defaultOpen = false,
  className,
}: CommandBarProps): ReactNode {
  const id = useId()
  const listboxId = `${id}-listbox`
  const optionId = (index: number): string => `${id}-option-${index}`

  const dialogRef = useRef<HTMLDialogElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLUListElement>(null)

  const [open, setOpen] = useState(defaultOpen)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)

  const matches = filterCommands(commands, query.trim())
  // Clamped rather than reset in an effect: the filter narrows on the keystroke
  // that renders, so an effect would leave one frame pointing past the end.
  const activeIndex = matches.length === 0 ? -1 : Math.min(active, matches.length - 1)

  // `showModal()` is imperative and the DOM owns "is it open" — the browser
  // closes the dialog on Escape without asking us. So state drives the element
  // here, and `onClose` drives the state back.
  useEffect(() => {
    const dialog = dialogRef.current
    if (!dialog) return
    if (open && !dialog.open) {
      dialog.showModal()
      inputRef.current?.focus()
    } else if (!open && dialog.open) {
      dialog.close()
    }
  }, [open])

  useEffect(() => {
    if (!shortcut) return undefined
    const onKeyDown = (event: KeyboardEvent): void => {
      if ((event.metaKey || event.ctrlKey) && !event.altKey && event.key.toLowerCase() === 'k') {
        // Chrome's own "search the web" is on ⌘K; without this the palette and
        // the omnibox both open.
        event.preventDefault()
        setOpen((wasOpen) => !wasOpen)
      }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [shortcut])

  // `block: 'nearest'` so a list that fits does not scroll at all, and the
  // element rather than the index because the row heights are not fixed.
  // biome-ignore lint/correctness/useExhaustiveDependencies: activeIndex is the trigger, not an input — the effect reads the DOM the render it caused has just produced
  useEffect(() => {
    listRef.current?.querySelector('[data-active="true"]')?.scrollIntoView({ block: 'nearest' })
  }, [activeIndex])

  const close = useCallback((): void => {
    // Closed through the element, not only through state: `onSelect` may
    // navigate, and a dialog still in the top layer during that navigation
    // covers the page it just went to.
    dialogRef.current?.close()
    setOpen(false)
  }, [])

  const run = useCallback(
    (command: Command): void => {
      close()
      command.onSelect()
    },
    [close],
  )

  const onInputKeyDown = (event: ReactKeyboardEvent<HTMLInputElement>): void => {
    if (matches.length === 0) return
    const last = matches.length - 1

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault()
        setActive(activeIndex >= last ? 0 : activeIndex + 1)
        break
      case 'ArrowUp':
        event.preventDefault()
        setActive(activeIndex <= 0 ? last : activeIndex - 1)
        break
      case 'Home':
        event.preventDefault()
        setActive(0)
        break
      case 'End':
        event.preventDefault()
        setActive(last)
        break
      case 'Enter': {
        const command = matches[activeIndex]
        if (command) {
          event.preventDefault()
          run(command)
        }
        break
      }
      default:
        break
    }
  }

  return (
    <>
      <button
        type="button"
        className={className ? `${styles.trigger} ${className}` : styles.trigger}
        aria-label={label}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-keyshortcuts="Meta+K Control+K"
        onClick={() => {
          setOpen(true)
        }}
      >
        <span className={styles.triggerIcon}>
          <SearchGlyph />
        </span>
        <span className={styles.triggerLabel}>{triggerLabel}</span>
        {/*
          None of this needs `aria-hidden`: `aria-label` replaces the button's
          contents as its accessible name outright, so the glyph and the
          shortcut are already unspoken. The shortcut reaches a screen reader
          through `aria-keyshortcuts` instead, once, as a fact rather than as
          two characters of punctuation.
        */}
        <kbd className={styles.kbd}>{SHORTCUT_HINT}</kbd>
      </button>

      {/* biome-ignore lint/a11y/useKeyWithClickEvents: this handler is the backdrop's dismiss, and the keyboard's dismiss is Escape — which showModal() already handles, without a listener of ours to pair with */}
      <dialog
        ref={dialogRef}
        className={styles.dialog}
        aria-label={label}
        onClose={() => {
          setOpen(false)
          setQuery('')
          setActive(0)
        }}
        onClick={(event) => {
          // A click on the backdrop reports the dialog itself as the target;
          // anything inside the panel reports the panel or its descendants.
          if (event.target === dialogRef.current) close()
        }}
      >
        <div className={styles.panel}>
          <div className={styles.search}>
            <span className={styles.searchIcon} aria-hidden="true">
              <SearchGlyph />
            </span>
            <input
              ref={inputRef}
              className={styles.input}
              type="text"
              role="combobox"
              aria-label={label}
              aria-controls={listboxId}
              aria-expanded={matches.length > 0}
              aria-activedescendant={activeIndex >= 0 ? optionId(activeIndex) : undefined}
              aria-autocomplete="list"
              autoComplete="off"
              spellCheck={false}
              placeholder={placeholder}
              value={query}
              onChange={(event) => {
                setQuery(event.target.value)
                setActive(0)
              }}
              onKeyDown={onInputKeyDown}
            />
          </div>

          <ul
            ref={listRef}
            id={listboxId}
            className={styles.list}
            role="listbox"
            aria-label={label}
            hidden={matches.length === 0}
          >
            {matches.map((command, index) => (
              // biome-ignore lint/a11y/useFocusableInteractive: options in an activedescendant combobox must NOT be focusable — focus stays in the input, which is the whole mechanism
              // biome-ignore lint/a11y/useKeyWithClickEvents: the keyboard equivalent is Enter on the input, which runs the same `run(command)`; a key handler here could never fire, since the element cannot hold focus
              <li
                key={command.id}
                id={optionId(index)}
                className={styles.option}
                role="option"
                aria-selected={index === activeIndex}
                data-active={index === activeIndex || undefined}
                // Hover arms the row so the pointer and the keyboard cannot
                // disagree about which command Enter would run.
                onMouseMove={() => {
                  setActive(index)
                }}
                onClick={() => {
                  run(command)
                }}
              >
                <span className={styles.optionLabel}>{command.label}</span>
                {command.kind ? <span className={styles.optionKind}>{command.kind}</span> : null}
              </li>
            ))}
          </ul>

          {matches.length === 0 ? (
            <p className={styles.empty} role="status">
              Nothing matches “{query.trim()}”.
            </p>
          ) : null}

          <p className={styles.footer}>
            <kbd className={styles.kbd}>↑</kbd>
            <kbd className={styles.kbd}>↓</kbd> to move
            <span className={styles.footerDot} aria-hidden="true">
              ·
            </span>
            <kbd className={styles.kbd}>↵</kbd> to run
            <span className={styles.footerDot} aria-hidden="true">
              ·
            </span>
            <kbd className={styles.kbd}>esc</kbd> to close
          </p>
        </div>
      </dialog>
    </>
  )
}

/** 16px magnifier, inheriting `currentColor` like the ThemeSwitch glyphs. */
function SearchGlyph(): ReactNode {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      aria-hidden="true"
      focusable="false"
    >
      <circle cx="7" cy="7" r="4.25" />
      <path d="M10.25 10.25 13.5 13.5" />
    </svg>
  )
}
