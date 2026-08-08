/**
 * Structure & tags — design 710–795, build spec §6.5.
 *
 * This is where the **settings overlay** lives: `GET /api/settings` and
 * `PATCH /api/settings`, whose allowlist is exactly seven keys and is published
 * as `SettingsOut.overridable`. That list is **read from the payload, never
 * copied**: a stale client-side copy either offers a control whose write is a
 * 400, or hides one that would have worked. Every control here checks it, and
 * a key outside it renders read-only.
 *
 * Three of the design's four blocks are real:
 *
 *  - the path-template builder writes `naming_template`, and the preview is the
 *    server's own rendering of it. **An empty `naming_preview` is meaningful** —
 *    the template could not be rendered — so it says that rather than showing a
 *    blank box.
 *  - the source ladder writes `enrichment_sources` as an ordered list, with
 *    each rung's `gated_on` shown where it cannot run, and `pending` shown
 *    where the ladder could not be rebuilt on the spot.
 *  - the five switches are the five real overridable booleans. The design's
 *    five talk about symlinks, SHA-256 manifests, transcode detection and
 *    auto-repair, none of which exist; the labels are rewritten to name what
 *    the switch actually does.
 *
 * The fourth, the Vorbis tag-mapping table, is `GET /api/meta`'s `tag_map` —
 * derived server-side from `tagger.VORBIS_FIELDS`, `nfo.ENRICHMENT_TAGS` and
 * `merge.WRITABLE_FIELDS`, so it cannot say something the tagger does not do.
 * There is **no local fallback copy**: a hand-written table beside the tagger is
 * precisely the drift publishing it removes, and the vocabulary rule about a
 * first-frame fallback is about closed sets of strings, not derived tables.
 *
 * *Resolved from* names the **column** a value is read from, not the rung that
 * wrote it. Which rung matched is a per-release fact (`mb_match_method`), shown
 * on the album detail and Identify screens; a per-field constant would be wrong
 * the moment somebody reorders the ladder in the block to the right.
 *
 * *On conflict* is a five-value verdict with a server-authored sentence.
 * `consensus` matches no row today — `WRITABLE_FIELDS` holds `release_type`,
 * which is an `Album` column and not a tag key — and that is the honest state
 * rather than a gap; it lights up by itself if a tag key ever enters that set.
 *
 * The re-file count under the preview is **press-driven**, and that is the
 * design decision rather than a shortcut: `GET /api/library/refile/estimate`
 * plans every downloaded release, which hashes each file behind the staleness
 * gate and re-reads the files of every release with no track rows — minutes on
 * a real library. So there is no live typing, no debounce (debouncing only
 * changes how many minutes-long disk walks a keystroke starts) and no polling;
 * an idle screen issues zero requests. It counts the **typed** template, not
 * the saved one, and says so when the two have since diverged.
 *
 * `frozen` and `blocked` are shown beside the headline rather than folded into
 * it: a release somebody froze is not a release that would move, and saying
 * nothing about it reads as a folder that would.
 *
 * The token list is checked against `app/core/naming.py`'s real vocabulary.
 * The design's `{bits}` and `{rate}` are **not** in it — `{quality}` carries
 * both — and its `{track:02}` is really `{track:02d}`. A token the renderer
 * does not know would render literally into a folder name.
 */

import { useEffect, useState, type CSSProperties } from 'react'

import {
  useEnrichmentStatus,
  useMeta,
  useRefileEstimate,
  useSettings,
  useUpdateSettings,
} from '@/api/queries'
import type { SettingsOut, TagMapRowOut } from '@/api/types'
import {
  Button,
  Chip,
  CodeBlock,
  Eyebrow,
  PageError,
  PageLoading,
  SwitchField,
  cx,
  useToast,
} from '@/design'

import { sourceDot } from '@/widgets'
import styles from '@/screens/rules/Rules.module.css'

/** Every token `naming.album_values`/`track_values` actually supplies, plus the
 *  separators the design offers (1028). */
const TOKENS = [
  '{artist}',
  '{album}',
  '{album_title}',
  '{year}',
  '{quality}',
  '{label}',
  '{disc}',
  '{track:02d}',
  '{title}',
  '{ext}',
  '/',
  ' - ',
  ' ',
  '[',
  ']',
  '(',
  ')',
]

/** What each rung is for. The ladder's own order is a setting, not this list. */
const ROLES: Readonly<Record<string, string>> = {
  acoustid: 'the audio anchor',
  musicbrainz: 'identity + credits',
  deezer: 'barcodes',
  coverartarchive: 'artwork',
  wikidata: 'biography',
}

/** The five overridable booleans, with honest labels. */
const SWITCHES: { key: keyof SettingsOut; label: string; note: string }[] = [
  {
    key: 'integrity_enabled',
    label: 'Measure file integrity',
    note: 'Hash every file (blake2b-128 plus an audio sample count) and re-hash a fixed slice nightly. Verdicts are verified, retagged, replaced or never baselined.',
  },
  {
    key: 'library_scan_nightly',
    label: 'Scan the library nightly',
    note: 'Read what is on disk and adopt what Qobuzarr already has a row for. Read-only: a scan can only ever reduce the work.',
  },
  {
    key: 'enrichment_write_back',
    label: 'Write enrichment into files',
    note: 'Re-tag releases this program downloaded with the ids the ladder agreed on. Releases the scan adopted are left alone, and pinned ones are skipped.',
  },
  {
    key: 'nfo_enabled',
    label: 'Write artist.nfo and album.nfo',
    note: 'Merged element by element into any file already there, and never touched at all when it carries <lockdata>.',
  },
  {
    key: 'upgrade_cleanup',
    label: 'Trash the folder an upgrade superseded',
    note: 'Only when the upgrade finished complete — a run with a missing track leaves the old copy where it is.',
  },
]

/** The verdicts this table actually uses, each with the server's sentence for
 *  it, in first-appearance order. Derived from the payload so a verdict the
 *  server adds explains itself without an edit here — and so a verdict no row
 *  carries (today, `consensus`) is not described as if it applied. */
function verdicts(rows: TagMapRowOut[]): [string, string][] {
  const seen = new Map<string, string>()
  for (const row of rows) {
    if (!seen.has(row.conflict)) seen.set(row.conflict, row.conflict_note)
  }
  return [...seen]
}

export default function Rules() {
  const toast = useToast()
  const settings = useSettings()
  const enrichment = useEnrichmentStatus()
  const meta = useMeta()
  const update = useUpdateSettings()

  const tagMap = meta.data?.tag_map ?? []
  const consensus = meta.data?.consensus_rule

  const data = settings.data
  const overridable = data?.overridable ?? []
  const origins = data?.origins ?? {}
  const pending = data?.pending ?? []

  const [template, setTemplate] = useState('')
  // The template the estimate was asked about. `null` is "nobody has pressed",
  // which is what keeps this screen at zero requests until somebody does.
  const [counted, setCounted] = useState<string | null>(null)
  const estimate = useRefileEstimate(counted)
  useEffect(() => {
    setTemplate(data?.naming_template ?? '')
    // A saved template makes any figure on screen a figure about a template
    // that is no longer the question.
    setCounted(null)
  }, [data?.naming_template])

  const [ladder, setLadder] = useState<string[]>([])
  useEffect(() => {
    setLadder(data?.enrichment_sources ?? [])
  }, [data?.enrichment_sources])

  function write(payload: Parameters<typeof update.mutate>[0], said: string) {
    update.mutate(payload, {
      onSuccess: (next) =>
        toast(
          next.pending.length === 0
            ? said
            : `${said} · takes effect on the next tick (${next.pending.join(', ')})`,
          next.pending.length === 0 ? 'ok' : 'warn',
        ),
      onError: (error: Error) => toast(error.message, 'bad'),
    })
  }

  function originOf(key: string) {
    const origin = origins[key]
    if (origin === undefined) return null
    return (
      <span className={cx(styles.origin, pending.includes(key) && styles.pending)}>
        {origin === 'override' ? 'overridden' : 'from .env'}
        {pending.includes(key) ? ' · pending' : ''}
        {origin === 'override' ? (
          <Button
            variant="quiet"
            size="sm"
            onClick={() => write({ reset: [key] }, `${key} back to .env`)}
          >
            Reset
          </Button>
        ) : null}
      </span>
    )
  }

  if (settings.isError) {
    return (
      <section className={styles.section}>
        <PageError
          message={settings.error.message}
          action={<Button onClick={() => void settings.refetch()}>Retry</Button>}
        />
      </section>
    )
  }

  if (data === undefined) {
    return (
      <section className={styles.section}>
        <PageLoading rows={6} label="Loading the settings" />
      </section>
    )
  }

  const templateWritable = overridable.includes('naming_template')
  const ladderWritable = overridable.includes('enrichment_sources')
  const templateDirty = template !== data.naming_template

  return (
    <section className={styles.section} aria-labelledby="rules-title">
      <div className={styles.header}>
        <div className={styles.headerMain}>
          <h1 className={styles.title} id="rules-title">
            Structure &amp; tags
          </h1>
          <p className={styles.lede}>
            Rules apply on every download, re-file and re-tag. Every write is logged in
            Activity.
          </p>
        </div>
      </div>

      <div className={styles.columns}>
        <div className={styles.column}>
          {/* ---- path template (719–739) ---- */}
          <div className={styles.block}>
            <h2 className={styles.blockTitle}>Path template</h2>
            <p className={styles.blockNote}>
              Click a token to append it. Rendered by the server against a real release.{' '}
              {originOf('naming_template')}
            </p>

            <CodeBlock content={template === '' ? '(empty)' : template} />

            <div className={styles.tokens}>
              {TOKENS.map((token) => (
                <Chip
                  key={token}
                  size="sm"
                  mono
                  accent
                  selected={false}
                  disabled={!templateWritable}
                  onClick={() => setTemplate((value) => value + token)}
                >
                  {token === ' ' ? '␣' : token}
                </Chip>
              ))}
              <div className={styles.tokenSpacer} />
              <Chip
                size="sm"
                selected={false}
                disabled={!templateWritable}
                onClick={() => setTemplate((value) => value.replace(/(\{[^}]*\}|.)$/, ''))}
              >
                ⌫ token
              </Chip>
            </div>

            <div className={styles.actions}>
              <Button
                variant="primary"
                size="sm"
                disabled={!templateWritable || !templateDirty}
                loading={update.isPending}
                onClick={() =>
                  write({ values: { naming_template: template } }, 'Template saved')
                }
              >
                Save template
              </Button>
              <Button
                size="sm"
                disabled={!templateDirty}
                onClick={() => setTemplate(data.naming_template)}
              >
                Undo edit
              </Button>
            </div>

            <div className={styles.preview}>
              <Eyebrow as="h3">Preview</Eyebrow>
              {data.naming_preview.length === 0 ? (
                <p className={styles.blockNote}>
                  The server could not render this template — nothing came back. Check the
                  tokens against the list above.
                </p>
              ) : (
                <div className={styles.previewLines}>
                  {data.naming_preview.map((line) => (
                    <div key={line}>{line}</div>
                  ))}
                </div>
              )}
              {templateDirty ? (
                <p className={styles.previewStale}>
                  unsaved — the preview is of the template currently in force
                </p>
              ) : null}
              {/* Design 737: how much of the library this template would move. */}
              <div className={styles.count}>
                <Button
                  size="sm"
                  variant="quiet"
                  loading={estimate.isFetching}
                  // An emptied box is refused here rather than sent. `template=`
                  // reaches the endpoint as `OptStrQuery`, which reads a blank
                  // as *no filter* — so pressing on an empty box would count the
                  // template still in force and report a confident figure about
                  // something the box no longer shows.
                  disabled={template.trim().length === 0}
                  onClick={() => {
                    if (counted === template) void estimate.refetch()
                    else setCounted(template)
                  }}
                >
                  Count releases this would move
                </Button>
                <span className={styles.countNote}>
                  Walks every release on disk and re-reads the files of releases it has
                  no track rows for. Slow on a large library.
                </span>
              </div>
              {estimate.isError ? (
                <p className={styles.countBad}>{estimate.error.message}</p>
              ) : estimate.data === undefined ? null : (
                <p className={styles.countLine}>
                  <strong>{estimate.data.would_refile.toLocaleString()}</strong> of{' '}
                  {estimate.data.considered.toLocaleString()} releases would be re-filed
                  {estimate.data.moves_directory > 0
                    ? ` · ${estimate.data.moves_directory.toLocaleString()} folders move`
                    : ''}
                  {estimate.data.blocked > 0
                    ? ` · ${estimate.data.blocked.toLocaleString()} blocked`
                    : ''}
                  {estimate.data.frozen > 0
                    ? ` · ${estimate.data.frozen.toLocaleString()} frozen`
                    : ''}
                  {/* The cap is the endpoint's parameter, not a constant this
                      screen knows, so the figure is read back off the answer —
                      a truncated walk considered exactly as many as it was
                      allowed. Spelling "2000" here would go on reading right
                      long after the default moved. */}
                  {estimate.data.truncated
                    ? ` · first ${estimate.data.considered.toLocaleString()} releases only`
                    : ''}
                </p>
              )}
              {counted !== null && counted !== template ? (
                <p className={styles.previewStale}>
                  counted for a different template — press again
                </p>
              ) : null}
            </div>
          </div>

          {/* ---- tag mapping (741–753) ---- */}
          <div className={styles.block}>
            <h2 className={styles.blockTitle}>Tag mapping</h2>
            <div className={styles.mapHead}>
              <div>Vorbis field</div>
              <div>Resolved from</div>
              <div>On conflict</div>
            </div>
            <div className={styles.mapBody}>
              {tagMap.length === 0 ? (
                <p className={styles.blockNote}>Loading the tag map…</p>
              ) : (
                tagMap.map((row) => (
                  <div className={styles.mapRow} key={row.vorbis_field}>
                    <div className={styles.mapField}>
                      <code>{row.vorbis_field}</code>
                      {row.id3_frame === null ? null : (
                        <span className={styles.mapId3}>MP3 {row.id3_frame}</span>
                      )}
                    </div>
                    <div className={styles.mapSource}>
                      <span className={styles.mapOrigin}>{row.origin}</span>
                      <code>{row.source_field}</code>
                    </div>
                    <div
                      className={cx(
                        styles.mapConflict,
                        styles[`conflict_${row.conflict}`],
                      )}
                      title={row.conflict_note}
                    >
                      {row.conflict}
                    </div>
                  </div>
                ))
              )}
            </div>
            {/* Each verdict's sentence once, rather than thirty-three times.
                The words in the column are the key into this list. */}
            <dl className={styles.mapLegend}>
              {verdicts(tagMap).map(([verdict, note]) => (
                <div className={styles.mapLegendRow} key={verdict}>
                  <dt className={cx(styles.mapConflict, styles[`conflict_${verdict}`])}>
                    {verdict}
                  </dt>
                  <dd className={styles.mapLegendNote}>{note}</dd>
                </div>
              ))}
            </dl>
            {consensus === undefined ? null : (
              <p className={styles.mapRule}>
                {consensus.note} Never overwritten at all:{' '}
                {consensus.never_writable.join(', ')}.
              </p>
            )}
          </div>
        </div>

        <div className={styles.column}>
          {/* ---- source priority (757–774) ---- */}
          <div className={styles.block}>
            <h2 className={styles.blockTitle}>Metadata source priority</h2>
            <p className={styles.blockNote}>
              The audio is the anchor, so AcoustID runs first and the rungs below corroborate
              what it said. Metadata only — Qobuz is the sole download source.{' '}
              {originOf('enrichment_sources')}
            </p>

            <div className={styles.ladder}>
              {ladder.map((name, i) => {
                const status = enrichment.data?.source_status.find(
                  (entry) => entry.name === name,
                )
                return (
                  <div
                    key={name}
                    className={styles.rung}
                    style={{ '--rung-dot': sourceDot(name) } as CSSProperties}
                  >
                    <span className={styles.rank}>{i + 1}</span>
                    <span className={styles.dot} aria-hidden="true" />
                    <span className={styles.rungName}>{name}</span>
                    {status?.gated_on == null ? (
                      <span className={styles.rungRole}>{ROLES[name] ?? 'metadata'}</span>
                    ) : (
                      <span className={styles.rungGated} title={status.gated_reason ?? ''}>
                        waiting on {status.gated_on}
                      </span>
                    )}
                    <div className={styles.arrows}>
                      <button
                        type="button"
                        className={styles.arrow}
                        disabled={i === 0 || !ladderWritable}
                        aria-label={`Move ${name} up`}
                        onClick={() => {
                          const next = [...ladder]
                          const above = next[i - 1]
                          const here = next[i]
                          if (above === undefined || here === undefined) return
                          next[i - 1] = here
                          next[i] = above
                          setLadder(next)
                          write(
                            { values: { enrichment_sources: next } },
                            `Ladder: ${next.join(' → ')}`,
                          )
                        }}
                      >
                        ▲
                      </button>
                      <button
                        type="button"
                        className={styles.arrow}
                        disabled={i === ladder.length - 1 || !ladderWritable}
                        aria-label={`Move ${name} down`}
                        onClick={() => {
                          const next = [...ladder]
                          const below = next[i + 1]
                          const here = next[i]
                          if (below === undefined || here === undefined) return
                          next[i + 1] = here
                          next[i] = below
                          setLadder(next)
                          write(
                            { values: { enrichment_sources: next } },
                            `Ladder: ${next.join(' → ')}`,
                          )
                        }}
                      >
                        ▼
                      </button>
                    </div>
                  </div>
                )
              })}
            </div>
          </div>

          {/* ---- structure & integrity (776–791) ---- */}
          <div className={styles.block}>
            <h2 className={styles.blockTitle}>Structure &amp; integrity</h2>
            <div className={styles.switches}>
              {SWITCHES.map((entry) => {
                const key = entry.key as string
                const checked = data[entry.key] === true
                const writable = overridable.includes(key)
                return (
                  <SwitchField
                    key={key}
                    checked={checked}
                    disabled={!writable || update.isPending}
                    onToggle={() =>
                      write(
                        { values: { [key]: !checked } },
                        `${entry.label}: ${checked ? 'off' : 'on'}`,
                      )
                    }
                    label={entry.label}
                    note={entry.note}
                    meta={originOf(key)}
                  />
                )
              })}
            </div>
          </div>
        </div>
      </div>
    </section>
  )
}
