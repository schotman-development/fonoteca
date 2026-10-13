import { type components, describeError } from '@fonoteca/api-client'
import {
  Badge,
  Button,
  Dialog,
  Field,
  Input,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Text,
  VisuallyHidden,
} from '@fonoteca/ui'
import { useNavigate, useParams } from '@tanstack/react-router'
import { useEffect, useId, useState } from 'react'

import { api } from '../api.ts'
import select from '../components/SortSelect.module.css'
import { SortSelect } from '../components/SortSelect.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './BrowsePage.module.css'
import { Missing } from './PlaylistPage.tsx'
import { songsLine } from './playlists.ts'
import own from './SmartPlaylistPage.module.css'
import {
  complete,
  condition,
  FIELD_GROUPS,
  FIELDS,
  group,
  newCriteria,
  OPERATORS,
  type SmartCriteria,
  type SmartField,
  type SmartMatch,
  type SmartOperator,
  type SmartRule,
  SORTS,
  valuesFor,
  withAppended,
  withChild,
  withField,
  withOperator,
} from './smartRules.ts'
import { TrackCells } from './TracksPage.tsx'

type PlaylistResponse = components['schemas']['PlaylistResponse']
type SmartPreviewResponse = components['schemas']['SmartPreviewResponse']

/** The server's own limit on nesting, less one: the deepest group is offered no group of its own. */
const MAX_DEPTH = 8

export function SmartPlaylistPage() {
  const { playlistId } = useParams({ from: '/library/playlists/smart/$playlistId' })

  // Keyed, so leaving one for another starts from that one's rules.
  return <Smart key={playlistId} id={playlistId === 'new' ? null : playlistId} />
}

function Smart({ id }: { readonly id: string | null }) {
  const state = useApiQuery(
    async () =>
      id == null
        ? null
        : await api.get('/api/catalogue/smart-playlists/{id}', { params: { path: { id } } }),
    [id],
  )

  if (state.status !== 'ready') {
    return (
      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? <Text tone="tertiary">Reading the playlist…</Text> : null}
        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read the playlist</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}
      </div>
    )
  }

  return <Editor saved={state.data} />
}

/**
 * A smart playlist's rules, edited with what they come to beside them.
 *
 * Nothing is saved until Save: the preview asks the server what the rules
 * would hold without writing anything, and the file is written once they are
 * saved — and again after every change to the library.
 */
function Editor({ saved }: { readonly saved: PlaylistResponse | null }) {
  const navigate = useNavigate()
  const [name, setName] = useState(saved?.name ?? '')
  const [criteria, setCriteria] = useState<SmartCriteria>(saved?.criteria ?? newCriteria())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const preview = usePreview(criteria)

  const ready = name.trim() !== '' && complete(criteria.rules)

  const save = async () => {
    setBusy(true)
    setError(null)
    setDone(null)
    try {
      if (saved == null) {
        const created = await api.post('/api/catalogue/smart-playlists', {
          json: { name: name.trim(), criteria },
        })
        await navigate({
          to: '/library/playlists/smart/$playlistId',
          params: { playlistId: created.id },
          replace: true,
        })
      } else {
        const updated = await api.post('/api/catalogue/smart-playlists/{id}', {
          params: { path: { id: saved.id } },
          json: { name: name.trim(), criteria },
        })
        setDone(`Saved, and written to ${updated.file}.`)
      }
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        <Text size="xs" tone="tertiary">
          Smart playlist
        </Text>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            {saved?.name ?? 'New smart playlist'}
          </Text>
        </h1>
        {saved != null ? (
          <Text size="xs" tone="tertiary" family="mono">
            {saved.file}
          </Text>
        ) : null}
        <Text tone="secondary" block>
          Rules rather than songs. Fonoteca writes the songs they hold to an m3u in the Playlists
          folder, and writes it again whenever the library changes, so other players see a plain
          playlist. An edit made to that file elsewhere is overwritten.
        </Text>
      </Stack>

      <div className={styles.search}>
        <Field label="Name">
          {(control) => (
            <Input
              {...control}
              value={name}
              maxLength={200}
              placeholder="Recent hi-res jazz"
              onChange={(event) => setName(event.target.value)}
            />
          )}
        </Field>
      </div>

      <Stack direction="column" gap={8}>
        <h2 className={styles.title}>
          <Text weight="semibold">Songs that match</Text>
        </h2>
        <RuleGroup
          rule={criteria.rules}
          depth={1}
          onChange={(rules) => setCriteria({ ...criteria, rules })}
        />
      </Stack>

      <Stack gap={16} align="end" wrap>
        <SortSelect
          label="Order"
          value={criteria.sort}
          options={SORTS}
          onChange={(sort) => setCriteria({ ...criteria, sort })}
        />
        <label className={own.check}>
          <input
            type="checkbox"
            checked={criteria.reverse}
            disabled={criteria.sort === 'Random'}
            onChange={(event) => setCriteria({ ...criteria, reverse: event.currentTarget.checked })}
          />
          <Text size="sm">Reversed</Text>
        </label>
        <Field label="At most" hint="Songs. Empty for every song that matches.">
          {(control) => (
            <Input
              {...control}
              type="number"
              min={1}
              max={100000}
              inputMode="numeric"
              value={criteria.limit ?? ''}
              onChange={(event) => {
                const limit = Number.parseInt(event.target.value, 10)
                setCriteria({
                  ...criteria,
                  limit: Number.isFinite(limit) && limit > 0 ? limit : null,
                })
              }}
            />
          )}
        </Field>
      </Stack>

      <Stack direction="column" gap={4} align="start">
        <Stack gap={8} wrap>
          <Button variant="primary" disabled={!ready || busy} onClick={() => void save()}>
            {saved == null ? 'Create' : 'Save'}
          </Button>
          {saved != null ? (
            <Button variant="danger" disabled={busy} onClick={() => setDeleting(true)}>
              Delete
            </Button>
          ) : null}
        </Stack>
        <div role="status" aria-live="polite">
          {done != null ? (
            <Text size="sm" tone="secondary">
              {done}
            </Text>
          ) : null}
        </div>
        {error != null ? (
          <Text size="sm" tone="danger" role="alert">
            {error}
          </Text>
        ) : null}
      </Stack>

      <Preview state={preview} />

      {deleting && saved != null ? (
        <DeleteDialog
          playlist={saved}
          onClose={() => setDeleting(false)}
          onDeleted={() => void navigate({ to: '/library/playlists' })}
        />
      ) : null}
    </Stack>
  )
}

/** A group: all or any of its rules, each a condition or a group of its own. */
function RuleGroup({
  rule,
  depth,
  onChange,
  onRemove,
}: {
  readonly rule: SmartRule
  readonly depth: number
  readonly onChange: (rule: SmartRule) => void
  readonly onRemove?: () => void
}) {
  const rules = rule.rules ?? []

  return (
    <fieldset className={own.group}>
      <legend className={own.legend}>
        <Text size="sm">Match</Text>
        <select
          aria-label="Match all or any of these rules"
          className={select.select}
          value={rule.match ?? 'All'}
          onChange={(event) =>
            onChange({ ...rule, match: event.currentTarget.value as SmartMatch })
          }
        >
          <option value="All">all</option>
          <option value="Any">any</option>
        </select>
        <Text size="sm">of these</Text>
      </legend>

      {rules.length === 0 ? (
        <Text size="sm" tone="tertiary">
          No rules yet: every song.
        </Text>
      ) : (
        <ul className={own.rules}>
          {rules.map((child, index) => (
            <li key={index}>
              {child.rules != null ? (
                <RuleGroup
                  rule={child}
                  depth={depth + 1}
                  onChange={(next) => onChange(withChild(rule, index, next))}
                  onRemove={() => onChange(withChild(rule, index, null))}
                />
              ) : (
                <Condition
                  rule={child}
                  onChange={(next) => onChange(withChild(rule, index, next))}
                  onRemove={() => onChange(withChild(rule, index, null))}
                />
              )}
            </li>
          ))}
        </ul>
      )}

      <Stack gap={8} wrap>
        <Button size="sm" variant="ghost" onClick={() => onChange(withAppended(rule, condition()))}>
          Add a condition
        </Button>
        {depth < MAX_DEPTH ? (
          <Button
            size="sm"
            variant="ghost"
            onClick={() =>
              onChange(withAppended(rule, group(rule.match === 'Any' ? 'All' : 'Any')))
            }
          >
            Add a group
          </Button>
        ) : null}
        {onRemove != null ? (
          <Button size="sm" variant="ghost" onClick={onRemove}>
            Remove this group
          </Button>
        ) : null}
      </Stack>
    </fieldset>
  )
}

const FIELD_LIST = Object.entries(FIELDS) as ReadonlyArray<
  readonly [SmartField, (typeof FIELDS)[SmartField]]
>

/** One condition: a field, what is asked of it, and the value or range it is asked about. */
function Condition({
  rule,
  onChange,
  onRemove,
}: {
  readonly rule: SmartRule
  readonly onChange: (rule: SmartRule) => void
  readonly onRemove: () => void
}) {
  const field = rule.field ?? 'Artist'
  const info: (typeof FIELDS)[SmartField] = FIELDS[field]
  const operator = rule.operator ?? OPERATORS[info.kind][0]?.[0] ?? 'Is'
  const count = valuesFor(operator)
  const numeric = info.kind !== 'text'
  const unit = 'unit' in info ? info.unit : null

  return (
    <div className={own.condition}>
      <select
        aria-label="Field"
        className={select.select}
        value={field}
        onChange={(event) => onChange(withField(rule, event.currentTarget.value as SmartField))}
      >
        {FIELD_GROUPS.map((name) => (
          <optgroup key={name} label={name}>
            {FIELD_LIST.filter(([, each]) => each.group === name).map(([key, each]) => (
              <option key={key} value={key}>
                {each.label}
              </option>
            ))}
          </optgroup>
        ))}
      </select>

      <select
        aria-label={`What to ask of ${info.label}`}
        className={select.select}
        value={operator}
        onChange={(event) =>
          onChange(withOperator(rule, event.currentTarget.value as SmartOperator))
        }
      >
        {OPERATORS[info.kind].map(([value, label]) => (
          <option key={value} value={value}>
            {label}
          </option>
        ))}
      </select>

      {count >= 1 ? (
        <div className={own.value}>
          <Input
            aria-label={count === 2 ? `${info.label} from` : `${info.label} value`}
            value={rule.value ?? ''}
            {...(numeric ? { inputMode: 'decimal' as const } : {})}
            onChange={(event) => onChange({ ...rule, value: event.target.value })}
          />
        </div>
      ) : null}

      {count === 2 ? (
        <>
          <Text size="sm">and</Text>
          <div className={own.value}>
            <Input
              aria-label={`${info.label} to`}
              value={rule.to ?? ''}
              inputMode="decimal"
              onChange={(event) => onChange({ ...rule, to: event.target.value })}
            />
          </div>
        </>
      ) : null}

      {count >= 1 && unit != null ? (
        <Text size="sm" tone="tertiary">
          {unit}
        </Text>
      ) : null}

      <Button
        size="sm"
        variant="ghost"
        aria-label={`Remove the ${info.label} condition`}
        onClick={onRemove}
      >
        ✕
      </Button>
    </div>
  )
}

type PreviewState =
  | { readonly status: 'incomplete' }
  | { readonly status: 'loading'; readonly data: SmartPreviewResponse | null }
  | { readonly status: 'ready'; readonly data: SmartPreviewResponse }
  | { readonly status: 'error'; readonly message: string }

/**
 * What the rules hold, asked again a moment after the last change: typing a
 * year should not send one request per digit.
 */
function usePreview(criteria: SmartCriteria): PreviewState {
  const [state, setState] = useState<PreviewState>({ status: 'incomplete' })

  useEffect(() => {
    if (!complete(criteria.rules)) {
      setState({ status: 'incomplete' })
      return
    }

    const aborter = new AbortController()

    const timer = setTimeout(() => {
      setState((previous) => ({
        status: 'loading',
        data: previous.status === 'ready' || previous.status === 'loading' ? previous.data : null,
      }))

      api
        .post('/api/catalogue/smart-playlists/preview', {
          json: { criteria },
          signal: aborter.signal,
        })
        .then((data) => setState({ status: 'ready', data }))
        .catch((cause: unknown) => {
          if (!aborter.signal.aborted) setState({ status: 'error', message: describeError(cause) })
        })
    }, 400)

    return () => {
      clearTimeout(timer)
      aborter.abort()
    }
  }, [criteria])

  return state
}

function Preview({ state }: { readonly state: PreviewState }) {
  const data = state.status === 'ready' || state.status === 'loading' ? state.data : null
  const headingId = useId()

  return (
    <section aria-labelledby={headingId}>
      <Stack direction="column" gap={8}>
        <h2 id={headingId} className={styles.title}>
          <Text weight="semibold">What it holds</Text>
        </h2>

        <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
          {state.status === 'incomplete' ? (
            <Text size="sm" tone="tertiary">
              Finish each condition to see the songs.
            </Text>
          ) : null}
          {state.status === 'error' ? (
            <Text size="sm" tone="danger">
              {state.message}
            </Text>
          ) : null}
          {data != null ? (
            <Text size="sm" tone="secondary">
              {songsLine(data.songs, data.songs, data.duration)}
              {data.songs > data.entries.length
                ? ` · the first ${data.entries.length.toLocaleString()} shown`
                : ''}
            </Text>
          ) : state.status === 'loading' ? (
            <Text size="sm" tone="tertiary">
              Working out the songs…
            </Text>
          ) : null}
        </div>

        {data != null && data.entries.length > 0 ? (
          <div className={styles.table}>
            <Table density="cozy">
              <thead>
                <tr>
                  <TableHeaderCell numeric className={own.numberCol}>
                    No
                  </TableHeaderCell>
                  <TableHeaderCell className={styles.playCol}>
                    <VisuallyHidden>Play</VisuallyHidden>
                  </TableHeaderCell>
                  <TableHeaderCell>Track</TableHeaderCell>
                  <TableHeaderCell>Artist</TableHeaderCell>
                  <TableHeaderCell>Album</TableHeaderCell>
                  <TableHeaderCell numeric className={styles.lengthCol}>
                    Length
                  </TableHeaderCell>
                  <TableHeaderCell className={styles.formatCol}>Format</TableHeaderCell>
                </tr>
              </thead>
              <tbody>
                {data.entries.map((entry) => (
                  <tr key={entry.index}>
                    <TableCell numeric>
                      <Text size="sm" family="mono" tone="tertiary">
                        {entry.index + 1}
                      </Text>
                    </TableCell>
                    {entry.track != null ? (
                      <TrackCells track={entry.track} />
                    ) : (
                      <Missing entry={entry} />
                    )}
                  </tr>
                ))}
              </tbody>
            </Table>
          </div>
        ) : null}
      </Stack>
    </section>
  )
}

function DeleteDialog({
  playlist,
  onClose,
  onDeleted,
}: {
  readonly playlist: PlaylistResponse
  readonly onClose: () => void
  readonly onDeleted: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const remove = async () => {
    setBusy(true)
    setError(null)
    try {
      await api.delete('/api/catalogue/smart-playlists/{id}', {
        params: { path: { id: playlist.id } },
      })
      onDeleted()
    } catch (cause) {
      setError(describeError(cause))
      setBusy(false)
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title={`Delete ${playlist.name}?`}
      description="The songs stay in the library. The rules go, and the playlist's file moves to the trash, so other players drop it at their next scan."
      footer={
        <Stack gap={8} justify="end">
          <Button variant="ghost" onClick={onClose}>
            Keep it
          </Button>
          <Button variant="danger" disabled={busy} onClick={() => void remove()}>
            Delete
          </Button>
        </Stack>
      }
    >
      {error != null ? (
        <Text size="sm" tone="danger" role="alert">
          {error}
        </Text>
      ) : (
        <Text size="sm" tone="secondary">
          {playlist.file}
        </Text>
      )}
    </Dialog>
  )
}
