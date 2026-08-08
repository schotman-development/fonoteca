/**
 * What a screen shows when its request failed.
 *
 * **The server's prose is rendered verbatim.** `client.ts` unwraps the uniform
 * error envelope, and the sentence inside it was written to be read by the
 * person holding the library — "no Qobuz credentials configured", "the library
 * path does not exist", "another scan is already running". Replacing it with a
 * generic apology per status code throws away the only part of the response
 * that says what to do. So: the title is the shape of the failure, the message
 * is the server's own words, and nothing here paraphrases either.
 *
 * `role="alert"` because it replaces content the reader asked for and is
 * therefore worth interrupting for — unlike the skeleton, which is polite.
 *
 * **No `Button` import.** The retry control arrives as a `ReactNode` in
 * `action`, which keeps this component free of the one primitive that a failure
 * panel would otherwise force into every bundle that renders one — and lets the
 * caller decide whether the answer is Retry, "Open settings", or nothing at all
 * (a 404 has no retry).
 */

import type { ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/PageError/PageError.module.css'

export interface PageErrorProps extends StyleableProps {
  /** The shape of the failure. The default is honest about knowing nothing more. */
  title?: string
  /** The server's own sentence. Rendered as given — do not paraphrase it upstream. */
  message?: string
  /** A Retry button, a link, or nothing. Supplied by the caller so this stays primitive-free. */
  action?: ReactNode
}

export function PageError({
  title = 'That did not work',
  message,
  action,
  className,
}: PageErrorProps) {
  return (
    <div className={cx(styles.root, className)} role="alert">
      <span className={styles.glyph} aria-hidden="true">
        !
      </span>
      <div className={styles.body}>
        <p className={styles.title}>{title}</p>
        {message ? <p className={styles.message}>{message}</p> : null}
        {action ? <div className={styles.action}>{action}</div> : null}
      </div>
    </div>
  )
}
