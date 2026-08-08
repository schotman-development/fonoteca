/**
 * The catch-all. The design draws none, so this is a heading, a sentence and a
 * link home — the spec's own description of it (§2), kept to that.
 *
 * It names the address it did not recognise, because the two ways to arrive
 * here are a stale bookmark and a typo, and both are answered by seeing the
 * path. The link is a router `Link`, not an `<a href>`: a full document load
 * would throw the whole query cache away to reach a screen that is one render
 * from here.
 */

import { Link, useLocation } from 'react-router-dom'

import styles from '@/screens/NotFound.module.css'

export default function NotFound() {
  const { pathname } = useLocation()
  return (
    <section className={styles.section} aria-labelledby="notfound-title">
      <div className={styles.well}>
        <h1 id="notfound-title">No such screen</h1>
        <p className={styles.note}>
          Nothing in qobuzarr answers to <span className={styles.path}>{pathname}</span>.
        </p>
        <Link to="/" className={styles.home}>
          Back to the dashboard
        </Link>
      </div>
    </section>
  )
}
