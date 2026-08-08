/**
 * The ⌘K palette — spec §6.7.
 *
 * The design draws the trigger and promises "artists, MBIDs, 'rescan',
 * 'retag'". This is the real thing behind it, and the rule it is built to is
 * the spec's: **nothing here fabricates. Every entry is an endpoint.** An action
 * with no route behind it is not built, however good it would look in the list.
 *
 * What that leaves is three groups:
 *
 *   - **Go to** — every screen in `nav.ts`, in its order. No fetch, no risk.
 *   - **Run** — the five mutations that exist and are safe to fire from a
 *     keystroke: a disk scan, an enrichment tick, an integrity pass, a library
 *     re-tag and a library re-file. The re-file is deliberately the **preview**:
 *     `POST /api/library/refile` defaults `apply` to false, walking the library
 *     and moving nothing, and a command palette is the wrong place to commit a
 *     thousand-folder rename with one Enter. The label says so.
 *   - **Artists / Albums** — `GET /api/search`, the Qobuz catalogue. Selecting a
 *     followed artist navigates to them; selecting one who is not followed
 *     follows them, which queues nothing (`auto_download` is the global opt-in
 *     and following alone downloads nothing — CLAUDE.md's opt-in invariant).
 *
 * Three implementation notes worth keeping:
 *
 *   - **The query is debounced by 450 ms.** `/api/search` is a live call to
 *     Qobuz through the one shared rate limiter, so a request per keystroke
 *     spends a paid account's hourly budget on prefixes nobody meant to search.
 *     The debounce is a `setTimeout` inside an effect with a cleanup — it is not
 *     a poller, and there is no `setInterval` here.
 *   - **The key handler is registered once, on `window`.** The component stays
 *     mounted while closed (it renders `null`) precisely so the listener exists
 *     before there is anything to close; a panel that mounts on ⌘K cannot be the
 *     thing that hears ⌘K.
 *   - **The listbox is not focusable.** Focus stays in the combobox and the
 *     active option is named by `aria-activedescendant`, which is the WAI-ARIA
 *     combobox pattern. Tab is swallowed rather than escaping the dialog, so
 *     the trap is one line instead of a focus-ring walker.
 */

import {
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
} from 'react'
import { useNavigate } from 'react-router-dom'

import {
  useFollowArtist,
  useLibraryScan,
  useRefileLibrary,
  useRetagLibrary,
  useRunEnrichment,
  useSearch,
  useVerifyIntegrity,
} from '@/api/queries'
import { useToast } from '@/design'
import styles from '@/shell/CommandPalette.module.css'
import { NAV_ITEMS } from '@/shell/nav'

/** `/api/search` is a paid, rate-limited upstream. Do not lower this. */
const SEARCH_DEBOUNCE_MS = 450

interface Entry {
  id: string
  group: string
  glyph: string
  label: string
  hint?: string
  run: () => void
}

export interface CommandPaletteProps {
  open: boolean
  onOpenChange: (open: boolean) => void
}

/** True for the platform chord that opens the palette: ⌘K on a Mac, Ctrl-K elsewhere. */
function isPaletteChord(event: KeyboardEvent): boolean {
  return (event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k'
}

/**
 * Fold a string down for matching: punctuation goes, spacing stays.
 *
 * The design's own hint promises `"retag"` (line 34) and the command is spelled
 * `Re-tag library`, so a plain `includes` answers *nothing matches that* —
 * nobody types a hyphen into a command bar. Dropping punctuation fixes it.
 *
 * **Word gaps are kept**, and that half is not cosmetic. Folding them away too
 * joins the end of one word to the start of the next and invents matches across
 * the seam: `Structure & tags` becomes `structuretags`, which contains `retag`,
 * so typing the name of one command also offers an unrelated screen. Collapsing
 * runs of separators to a single space is what keeps `re-tag` ≡ `retag` while
 * keeping `structure tags` ≢ `retag`.
 */
function fold(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
}

export function CommandPalette({ open, onOpenChange }: CommandPaletteProps) {
  const navigate = useNavigate()
  const toast = useToast()

  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  const [active, setActive] = useState(0)

  const inputRef = useRef<HTMLInputElement>(null)
  const restoreFocusTo = useRef<HTMLElement | null>(null)
  const listId = useId()

  const scan = useLibraryScan()
  const enrich = useRunEnrichment()
  const verify = useVerifyIntegrity()
  const retag = useRetagLibrary()
  const refile = useRefileLibrary()
  const follow = useFollowArtist()

  // `open ? … : ''` rather than `debounced` alone: the component stays mounted
  // while closed so it can hear the chord, and an empty `q` is what
  // `useSearch`'s own `enabled` reads — otherwise a closed palette keeps the
  // last search alive and refetches it on the next window focus.
  const search = useSearch({ q: open ? debounced : '' })

  const close = useCallback(() => onOpenChange(false), [onOpenChange])

  // ---- the one global key handler ----------------------------------------
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (!isPaletteChord(event)) return
      // The browser's own "search the page" bindings live on this chord in a
      // couple of shells; the palette is the app's answer to the same intent.
      event.preventDefault()
      onOpenChange(true)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onOpenChange])

  // ---- open / close housekeeping -----------------------------------------
  useEffect(() => {
    if (!open) return
    restoreFocusTo.current =
      document.activeElement instanceof HTMLElement ? document.activeElement : null
    setQuery('')
    setDebounced('')
    setActive(0)
    inputRef.current?.focus()
    return () => {
      // Returning focus to the trigger is not a nicety: without it a keyboard
      // user who presses Escape is left at the top of the document.
      restoreFocusTo.current?.focus()
    }
  }, [open])

  // ---- debounce ------------------------------------------------------------
  useEffect(() => {
    if (!open) return
    const handle = setTimeout(() => setDebounced(query.trim()), SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(handle)
  }, [query, open])

  const runMutation = useCallback(
    (label: string, fire: (done: { onSuccess: () => void; onError: (e: Error) => void }) => void) => {
      close()
      fire({
        onSuccess: () => toast(`${label} — done`, 'ok'),
        onError: (error: Error) => toast(error.message, 'bad'),
      })
    },
    [close, toast],
  )

  const entries = useMemo<Entry[]>(() => {
    const needle = fold(query)
    const matches = (...haystack: string[]) =>
      needle === '' || haystack.some((text) => fold(text).includes(needle))

    const out: Entry[] = []

    for (const item of NAV_ITEMS) {
      if (!matches(item.label, item.path)) continue
      out.push({
        id: `nav:${item.id}`,
        group: 'Go to',
        glyph: item.glyph,
        label: item.label,
        hint: item.path,
        run: () => {
          close()
          navigate(item.path)
        },
      })
    }

    const commands: { id: string; label: string; hint?: string; go: () => void }[] = [
      {
        id: 'scan',
        label: 'Scan library',
        hint: 'disk',
        go: () =>
          runMutation('Library scan', (done) => scan.mutate(undefined, done)),
      },
      {
        id: 'enrich',
        label: 'Run enrichment',
        hint: 'metadata',
        go: () =>
          runMutation('Enrichment run', (done) => enrich.mutate(undefined, done)),
      },
      {
        id: 'verify',
        label: 'Verify integrity',
        hint: 'hashes',
        go: () =>
          runMutation('Integrity pass', (done) => verify.mutate(undefined, done)),
      },
      {
        id: 'retag',
        label: 'Re-tag library',
        hint: 'writes',
        go: () =>
          runMutation('Library re-tag', (done) => retag.mutate(undefined, done)),
      },
      {
        // The preview, not the commit. `apply` is left off, so the server's own
        // default (false) walks the library and moves nothing.
        id: 'refile',
        label: 'Preview a library re-file',
        hint: 'moves nothing',
        go: () =>
          runMutation('Re-file preview', (done) => refile.mutate(undefined, done)),
      },
    ]

    for (const command of commands) {
      if (!matches(command.label)) continue
      out.push({
        id: `run:${command.id}`,
        group: 'Run',
        glyph: '›',
        label: command.label,
        ...(command.hint === undefined ? {} : { hint: command.hint }),
        run: command.go,
      })
    }

    for (const artist of search.data?.artists ?? []) {
      out.push({
        id: `artist:${artist.id}`,
        group: 'Artists',
        glyph: '◎',
        label: artist.name,
        hint: artist.followed ? 'open' : 'follow',
        run: () => {
          close()
          if (artist.followed) {
            navigate(`/library/${encodeURIComponent(artist.id)}`)
            return
          }
          follow.mutate(
            { artist_id: artist.id, name: artist.name },
            {
              // Following queues nothing — the toast says what really happened.
              onSuccess: () => toast(`Now following ${artist.name}`, 'ok'),
              onError: (error: Error) => toast(error.message, 'bad'),
            },
          )
        },
      })
    }

    for (const album of search.data?.albums ?? []) {
      // An album hit is only actionable through its artist: there is no album
      // screen at an address of its own, and an entry that goes nowhere is
      // exactly what "every entry is an endpoint" forbids.
      const artistId = album.artist_id
      if (artistId === null) continue
      out.push({
        id: `album:${album.id}`,
        group: 'Albums',
        glyph: '▤',
        label: album.artist_name === null ? album.title : `${album.title} — ${album.artist_name}`,
        hint: album.in_library ? 'in library' : undefined,
        run: () => {
          close()
          navigate(`/library/${encodeURIComponent(artistId)}`)
        },
      })
    }

    return out
  }, [
    query,
    search.data,
    close,
    navigate,
    runMutation,
    scan,
    enrich,
    verify,
    retag,
    refile,
    follow,
    toast,
  ])

  // The active row can outlive the list that held it — a debounce landing while
  // somebody is at row nine leaves nine pointing past the end.
  const clamped = entries.length === 0 ? 0 : Math.min(active, entries.length - 1)

  if (!open) return null

  function onInputKeyDown(event: ReactKeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape') {
      event.preventDefault()
      close()
      return
    }
    if (event.key === 'Tab') {
      // The dialog holds exactly one focusable element, so the trap is simply
      // refusing to leave it.
      event.preventDefault()
      return
    }
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setActive((n) => (entries.length === 0 ? 0 : (n + 1) % entries.length))
      return
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault()
      setActive((n) =>
        entries.length === 0 ? 0 : (n - 1 + entries.length) % entries.length,
      )
      return
    }
    if (event.key === 'Enter') {
      event.preventDefault()
      entries[clamped]?.run()
    }
  }

  const groups: { name: string; items: Entry[] }[] = []
  for (const entry of entries) {
    const last = groups[groups.length - 1]
    if (last !== undefined && last.name === entry.group) last.items.push(entry)
    else groups.push({ name: entry.group, items: [entry] })
  }

  return (
    <div
      className={styles.scrim}
      // A press on the backdrop is a dismissal; a press inside the panel is not.
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) close()
      }}
    >
      <div
        className={styles.panel}
        role="dialog"
        aria-modal="true"
        aria-label="Search or run a command"
      >
        <div className={styles.searchRow}>
          <span className={styles.searchGlyph} aria-hidden="true">
            ⌘
          </span>
          <input
            ref={inputRef}
            className={styles.input}
            type="text"
            role="combobox"
            aria-label="Search or run a command"
            aria-expanded="true"
            aria-controls={listId}
            aria-autocomplete="list"
            aria-activedescendant={
              entries.length === 0 ? undefined : `${listId}-${clamped}`
            }
            placeholder="Search or run a command — artists, albums, “scan”, “retag”…"
            value={query}
            onChange={(event) => {
              setQuery(event.target.value)
              setActive(0)
            }}
            onKeyDown={onInputKeyDown}
          />
        </div>

        <div className={styles.results}>
          {entries.length === 0 ? (
            <p className={styles.note}>
              {search.isFetching ? 'Searching…' : 'Nothing matches that.'}
            </p>
          ) : (
            <div id={listId} role="listbox" aria-label="Commands and results">
              {groups.map((group) => (
                <div key={group.name} className={styles.group}>
                  <div className={styles.groupLabel}>{group.name}</div>
                  {group.items.map((entry) => {
                    const index = entries.indexOf(entry)
                    return (
                      <div
                        key={entry.id}
                        id={`${listId}-${index}`}
                        role="option"
                        aria-selected={index === clamped}
                        className={
                          index === clamped
                            ? `${styles.option} ${styles.optionActive}`
                            : styles.option
                        }
                        onMouseEnter={() => setActive(index)}
                        onClick={entry.run}
                      >
                        <span className={styles.optionGlyph} aria-hidden="true">
                          {entry.glyph}
                        </span>
                        <span className={styles.optionLabel}>{entry.label}</span>
                        {entry.hint === undefined ? null : (
                          <span className={styles.optionHint}>{entry.hint}</span>
                        )}
                      </div>
                    )
                  })}
                </div>
              ))}
            </div>
          )}
        </div>

        <div className={styles.footer}>
          <span>↑↓ move</span>
          <span>⏎ run</span>
          <span>esc close</span>
        </div>
      </div>
    </div>
  )
}
