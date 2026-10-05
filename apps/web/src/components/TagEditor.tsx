import { type components, describeError } from '@fonoteca/api-client'
import {
  Badge,
  Button,
  Input,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Tabs,
  Text,
} from '@fonoteca/ui'
import { useState } from 'react'

import { api } from '../api.ts'
import {
  ALBUM_FIELDS,
  cellKey,
  changesOf,
  type Draft,
  type DraftCell,
  firstProblem,
  label,
  nameProblem,
  setAll,
  shown,
} from '../pages/tagEdits.ts'
import { useApiQuery } from '../useApiQuery.ts'

type Cell = components['schemas']['TagCell']
type Result = components['schemas']['TagEditResult']

/**
 * A person's tags for one album folder: what each file carries, what the
 * catalogue would write, and what they set — one field at a time, every file
 * of the folder in rows, with a row that sets them all.
 *
 * Saving writes the folder at once, through the tag write, and one press of
 * Undo takes the whole save back. With `Fonoteca:AllowFileMutation` off the
 * editor still shows everything and offers no Save, as no button that would
 * change a file is shown then.
 */
export function TagEditor({
  folder,
  onSaved,
}: {
  readonly folder: string
  readonly onSaved: (result: Result) => void
}) {
  const [attempt, setAttempt] = useState(0)
  const [draft, setDraft] = useState<Draft>(new Map())
  const [field, setField] = useState('TITLE')
  const [all, setAllValue] = useState('')
  const [saving, setSaving] = useState(false)
  const [said, setSaid] = useState<string | null>(null)
  const [added, setAdded] = useState<readonly string[]>([])
  const [naming, setNaming] = useState('')

  const state = useApiQuery(
    () => api.get('/api/catalogue/folders/tags', { params: { query: { folder } } }),
    [folder, attempt],
  )

  if (state.status === 'loading') return <Text tone="tertiary">Reading the files’ tags…</Text>
  if (state.status === 'error') {
    return (
      <Text size="sm" tone="danger">
        Could not read the tags: {state.message}
      </Text>
    )
  }

  const tags = state.data
  const files = tags.files.map((file) => file.id)
  const known = [...tags.fields, ...added.filter((name) => !tags.fields.includes(name))]
  const fields = tags.albumWide ? known.filter((name) => !ALBUM_FIELDS.includes(name)) : known
  const namingProblem = naming === '' ? null : nameProblem(naming.trim(), known)
  const blocked = firstProblem(draft)

  /** A change to one cell, or null to drop the change not yet saved. */
  const touch = (key: string, cell: DraftCell | null) => {
    const next = new Map(draft)
    if (cell === null) next.delete(key)
    else next.set(key, cell)
    setDraft(next)
    setSaid(null)
  }

  async function save() {
    setSaving(true)
    setSaid(null)

    try {
      const result = await api.post('/api/catalogue/folders/tags', {
        json: { folder, changes: changesOf(draft) },
      })

      setDraft(new Map())
      setSaid(
        `Saved. ${result.written} file${result.written === 1 ? '' : 's'} written` +
          (result.notWritten > 0 ? `, ${result.notWritten} not (the log says why)` : '') +
          (result.renamed > 0 ? `, ${result.renamed} renamed` : '') +
          '.',
      )
      onSaved(result)
      setAttempt((value) => value + 1)
    } catch (cause: unknown) {
      setSaid(`Nothing was saved. ${describeError(cause)}`)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Stack direction="column" gap={16} align="stretch">
      {tags.albumWide ? (
        <Stack direction="column" gap={8} align="stretch">
          <Text size="sm" weight="medium">
            The album — title and artist for every folder of it, the year for this folder
          </Text>
          {tags.album.map((cell) => (
            <Row
              key={cell.field}
              name={label(cell.field)}
              cell={cell}
              draft={draft.get(cellKey(null, cell.field))}
              canRemove={false}
              onChange={(next) => touch(cellKey(null, cell.field), next)}
            />
          ))}
        </Stack>
      ) : null}

      <Stack gap={8} align="end" wrap>
        <Input
          aria-label="A field of your own"
          placeholder="A field of your own"
          value={naming}
          onChange={(event) => setNaming(event.currentTarget.value)}
        />
        <Button
          size="sm"
          variant="secondary"
          disabled={naming.trim() === '' || namingProblem !== null}
          onClick={() => {
            setAdded([...added, naming.trim()])
            setField(naming.trim())
            setNaming('')
          }}
        >
          Add the field
        </Button>
        {namingProblem !== null ? (
          <Text size="xs" tone="danger">
            {namingProblem}
          </Text>
        ) : null}
      </Stack>

      <Tabs
        tabs={fields.map((name) => ({ key: name, label: label(name) }))}
        label={`Tags of ${folder}`}
        selected={fields.includes(field) ? field : (fields[0] ?? 'TITLE')}
        onSelect={setField}
        panel={(selected) => (
          <Stack direction="column" gap={8} align="stretch">
            <Stack gap={8} align="end" wrap>
              <Input
                aria-label={`${label(selected)} for every file`}
                placeholder={`${label(selected)} for every file`}
                value={all}
                onChange={(event) => setAllValue(event.currentTarget.value)}
              />
              <Button
                size="sm"
                variant="secondary"
                disabled={all.trim() === ''}
                onClick={() => {
                  setDraft(setAll(draft, files, selected, { kind: 'set', value: all }))
                  setAllValue('')
                }}
              >
                Set for every file
              </Button>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => setDraft(setAll(draft, files, selected, { kind: 'none' }))}
              >
                None for every file
              </Button>
            </Stack>

            <Table density="compact">
              <thead>
                <tr>
                  <TableHeaderCell>File</TableHeaderCell>
                  <TableHeaderCell>In the file now</TableHeaderCell>
                  <TableHeaderCell>{label(selected)}</TableHeaderCell>
                </tr>
              </thead>
              <tbody>
                {tags.files.map((file) => {
                  // A field added here has no cell yet: nothing set, nothing in the file.
                  const cell = file.cells.find((candidate) => candidate.field === selected) ?? {
                    field: selected,
                    value: null,
                    catalogue: null,
                    file: null,
                    mine: false,
                  }

                  const name = file.path.slice(file.path.lastIndexOf('/') + 1)

                  return (
                    <tr key={file.id}>
                      <TableCell>{name}</TableCell>
                      <TableCell truncate>
                        {file.readable ? (cell.file ?? '—') : 'unreadable'}
                      </TableCell>
                      <TableCell>
                        <Row
                          name={`${label(selected)}, ${name}`}
                          cell={cell}
                          draft={draft.get(cellKey(file.id, selected))}
                          canRemove
                          labelHidden
                          onChange={(next) => touch(cellKey(file.id, selected), next)}
                        />
                      </TableCell>
                    </tr>
                  )
                })}
              </tbody>
            </Table>
          </Stack>
        )}
      />

      <Stack gap={8} align="center" wrap role="status" aria-live="polite">
        {tags.willWrite ? (
          <Button
            variant="primary"
            size="sm"
            disabled={saving || draft.size === 0 || blocked !== null}
            onClick={() => void save()}
          >
            {saving
              ? 'Saving…'
              : `Save and write ${draft.size === 1 ? 'the change' : 'the changes'}`}
          </Button>
        ) : (
          <Badge tone="info" size="sm">
            File writing is off
          </Badge>
        )}
        {draft.size > 0 ? (
          <Button size="sm" variant="ghost" disabled={saving} onClick={() => setDraft(new Map())}>
            Discard changes
          </Button>
        ) : null}
        {blocked !== null ? (
          <Text size="xs" tone="danger">
            {blocked}
          </Text>
        ) : null}
        {said !== null ? <Text size="xs">{said}</Text> : null}
      </Stack>
    </Stack>
  )
}

/** One value: the input, where it came from, and the person's own controls over it. */
function Row({
  name,
  cell,
  draft,
  canRemove,
  labelHidden = false,
  onChange,
}: {
  readonly name: string
  readonly cell: Cell
  readonly draft: DraftCell | undefined
  readonly canRemove: boolean
  readonly labelHidden?: boolean
  readonly onChange: (cell: DraftCell | null) => void
}) {
  const none = draft?.kind === 'none' || (draft === undefined && cell.mine && cell.value === null)

  return (
    <Stack gap={8} align="center" wrap>
      {labelHidden ? null : (
        <Text size="sm" tone="secondary">
          {name}
        </Text>
      )}
      <Input
        aria-label={name}
        value={shown(cell, draft)}
        placeholder={none ? 'None' : (cell.catalogue ?? '')}
        onChange={(event) => onChange({ kind: 'set', value: event.currentTarget.value })}
      />
      {cell.mine && draft === undefined ? (
        <Badge tone="info" size="sm">
          Set by you
        </Badge>
      ) : null}
      {draft !== undefined ? (
        <Badge tone="warning" size="sm">
          {draft.kind === 'reset' ? 'Back to the catalogue' : 'Changed'}
        </Badge>
      ) : null}
      {canRemove && !none ? (
        <Button size="sm" variant="ghost" onClick={() => onChange({ kind: 'none' })}>
          None
        </Button>
      ) : null}
      {draft !== undefined ? (
        <Button size="sm" variant="ghost" onClick={() => onChange(null)}>
          Undo change
        </Button>
      ) : cell.mine ? (
        <Button size="sm" variant="ghost" onClick={() => onChange({ kind: 'reset' })}>
          Reset
        </Button>
      ) : null}
    </Stack>
  )
}
