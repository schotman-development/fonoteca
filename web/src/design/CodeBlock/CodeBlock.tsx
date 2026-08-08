/**
 * A block of mono text: a path, a rendered naming template, or one half of a
 * before/after diff (design 480–486, 638–650, 688–692, 722).
 *
 * Two things about the API are load-bearing.
 *
 * `content` takes a string OR a list of lines because the design renders both
 * and they are not the same element: a path is one run of text that wraps, and
 * a diff panel is `sc-for`-ed into one `<div>` per line (639–641, 689–691) so
 * that the left and right panels line up row for row. Joining the list with
 * `\n` would collapse that, since this is not a `<pre>` — and it is not a
 * `<pre>` because `white-space: pre` and `word-break: break-all` are opposites,
 * and a path that must not overflow its drawer needs the second.
 *
 * `tone` is `neutral` or `accent` and nothing else. It is not the shared `Tone`
 * union: this block is a diff panel, and the meaning of its colour is "on disk"
 * versus "proposed" — identity, not status. There is no such thing as a
 * corrupt-red or a warning-amber code block in the design, and offering one
 * would be offering to paint a filesystem path with a severity.
 */

import type { ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps, Tone } from '@/design/types'
import styles from '@/design/CodeBlock/CodeBlock.module.css'

/**
 * Identity, not severity — see the note above. Narrowed from the shared `Tone`
 * rather than spelled again: a caller passing `tone="bad"` here should be a
 * compile error naming the two words that exist, not a silent no-op.
 */
export type CodeBlockTone = Extract<Tone, 'neutral' | 'accent'>

export interface CodeBlockProps extends StyleableProps {
  /** One run of text, or one `<div>` per entry so two panels align. */
  content: string | readonly string[]
  tone?: CodeBlockTone
  /** The line inside the block that says what it is: `canonical · 12 files`. */
  caption?: ReactNode
  /** `loose` is the design's 1.9 leading for the diff panels (642, 647, 688). */
  spacing?: 'normal' | 'loose'
}

export function CodeBlock({
  content,
  tone = 'neutral',
  caption,
  spacing = 'normal',
  className,
}: CodeBlockProps) {
  const lines = typeof content === 'string' ? null : content

  return (
    <div
      className={cx(
        styles.block,
        tone === 'accent' && styles.accent,
        spacing === 'loose' && styles.loose,
        className,
      )}
    >
      {caption === undefined || caption === null ? null : (
        <span className={styles.caption}>{caption}</span>
      )}
      {lines === null
        ? content
        : lines.map((line, i) => (
            // Position is part of the key on purpose: a diff panel's lines have
            // no identity of their own, are never reordered, and two identical
            // lines in one panel are two different lines.
            <div key={`${i}:${line}`}>{line}</div>
          ))}
    </div>
  )
}
