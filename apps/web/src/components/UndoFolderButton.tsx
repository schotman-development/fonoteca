import { type components, describeError } from '@fonoteca/api-client'
import { Button, Stack, Text } from '@fonoteca/ui'
import { useState } from 'react'

import { api } from '../api.ts'
import { undoLabel, undoOutcome, undoQuestion } from '../pages/tagUndo.ts'
import { useApiQuery } from '../useApiQuery.ts'

type Result = components['schemas']['TagUndoResult']

/**
 * One step back for an album folder: the newest edit to its files, reversed.
 *
 * Shown only where there is something to undo and file writing is on — with
 * `Fonoteca:AllowFileMutation` off no button that would change a file is
 * offered at all. Asks first, naming the edit and its date, because undoing a
 * tag write also hands the folder back to the Identify screen.
 */
export function UndoFolderButton({
  folder,
  onUndone,
}: {
  readonly folder: string
  readonly onUndone: (result: Result) => void
}) {
  const [attempt, setAttempt] = useState(0)
  const [sending, setSending] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)

  const state = useApiQuery(
    () => api.get('/api/catalogue/folders/undo', { params: { query: { folder } } }),
    [folder, attempt],
  )

  if (state.status !== 'ready' || !state.data.willWrite || state.data.edit == null) return null

  const edit = state.data.edit

  async function undo() {
    if (!window.confirm(undoQuestion(folder, edit))) return

    setSending(true)
    setFailure(null)

    try {
      const result = await api.post('/api/catalogue/folders/undo', { json: { folder } })

      // Stopped before anything moved: the folder is where it was, so the
      // page stays and says why.
      if (result.status === 'Incomplete') setFailure(undoOutcome(result))
      else onUndone(result)
    } catch (cause: unknown) {
      setFailure(`Nothing was undone. ${describeError(cause)}`)
    } finally {
      setSending(false)
      setAttempt((value) => value + 1)
    }
  }

  return (
    <Stack direction="column" gap={4} align="start">
      <Button size="sm" variant="secondary" disabled={sending} onClick={() => void undo()}>
        {undoLabel(edit)}
      </Button>
      {failure !== null ? (
        <Text size="xs" tone="danger" role="status">
          {failure}
        </Text>
      ) : null}
    </Stack>
  )
}
