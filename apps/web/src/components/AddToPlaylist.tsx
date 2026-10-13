import { describeError } from '@fonoteca/api-client'
import { Button, Dialog, Input, Stack, Text } from '@fonoteca/ui'
import { Link } from '@tanstack/react-router'
import { useId, useState } from 'react'

import { api } from '../api.ts'
import { songsLine } from '../pages/playlists.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './AddToPlaylist.module.css'

type Added = { readonly id: string; readonly name: string }

/**
 * Puts songs on a playlist, one already made or a new one.
 *
 * `compact` is a table row's: a "+" whose accessible name says what it adds,
 * turning to a tick that names the playlist once it has. Otherwise a button
 * with a line under it linking to where the songs went, because a dialog that
 * closes on success says nothing about it.
 */
export function AddToPlaylist({
  recordingIds,
  name,
  compact = false,
}: {
  /** Songs the library holds, in order. */
  readonly recordingIds: readonly string[]
  /** What is being added, for the dialog and the button's name. */
  readonly name: string
  readonly compact?: boolean
}) {
  const [open, setOpen] = useState(false)
  const [added, setAdded] = useState<Added | null>(null)

  const label =
    added != null ? `Added to ${added.name}. Add ${name} again` : `Add ${name} to a playlist`

  return (
    <>
      {compact ? (
        <Button
          size="sm"
          variant="ghost"
          aria-label={label}
          title={label}
          disabled={recordingIds.length === 0}
          onClick={() => setOpen(true)}
        >
          {added != null ? '✓' : '+'}
        </Button>
      ) : (
        <Stack direction="column" gap={4} align="start">
          <Button
            variant="secondary"
            disabled={recordingIds.length === 0}
            onClick={() => setOpen(true)}
          >
            Add to playlist
          </Button>
          {added != null ? (
            <Text size="xs" tone="secondary" role="status">
              Added to{' '}
              <Link
                to="/library/playlists/$playlistId"
                params={{ playlistId: added.id }}
                className={styles.link}
              >
                <Text size="xs" tone="accent">
                  {added.name}
                </Text>
              </Link>
            </Text>
          ) : null}
        </Stack>
      )}

      {open ? (
        <Chooser
          recordingIds={recordingIds}
          name={name}
          onClose={() => setOpen(false)}
          onAdded={(playlist) => {
            setAdded(playlist)
            setOpen(false)
          }}
        />
      ) : null}
    </>
  )
}

function Chooser({
  recordingIds,
  name,
  onClose,
  onAdded,
}: {
  readonly recordingIds: readonly string[]
  readonly name: string
  readonly onClose: () => void
  readonly onAdded: (playlist: Added) => void
}) {
  const state = useApiQuery(() => api.get('/api/catalogue/playlists'), [])
  const [title, setTitle] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const inputId = useId()

  const run = async (work: () => Promise<Added>) => {
    setBusy(true)
    setError(null)
    try {
      onAdded(await work())
    } catch (cause) {
      setError(describeError(cause))
      setBusy(false)
    }
  }

  const create = () =>
    run(async () => {
      const playlist = await api.post('/api/catalogue/playlists', {
        json: { name: title.trim(), recordingIds: [...recordingIds] },
      })
      return { id: playlist.id, name: playlist.name }
    })

  const addTo = (id: string) =>
    run(async () => {
      const playlist = await api.post('/api/catalogue/playlists/{id}/songs', {
        params: { path: { id } },
        json: { recordingIds: [...recordingIds] },
      })
      return { id: playlist.id, name: playlist.name }
    })

  const songs = `${recordingIds.length} ${recordingIds.length === 1 ? 'song' : 'songs'}`

  return (
    <Dialog
      open
      onClose={onClose}
      title="Add to a playlist"
      description={recordingIds.length === 1 ? name : `${name}: ${songs}`}
    >
      <Stack direction="column" gap={16}>
        <form
          onSubmit={(event) => {
            event.preventDefault()
            if (title.trim() !== '') void create()
          }}
        >
          <Stack direction="column" gap={4}>
            <label htmlFor={inputId}>
              <Text size="xs" tone="tertiary">
                A new playlist
              </Text>
            </label>
            <Stack gap={8}>
              <Input
                id={inputId}
                value={title}
                maxLength={200}
                placeholder="Road trip"
                onChange={(event) => setTitle(event.target.value)}
              />
              <Button type="submit" variant="primary" disabled={busy || title.trim() === ''}>
                Create
              </Button>
            </Stack>
          </Stack>
        </form>

        {state.status === 'loading' ? (
          <Text size="sm" tone="tertiary">
            Reading the playlists…
          </Text>
        ) : null}

        {state.status === 'error' ? (
          <Text size="sm" tone="danger">
            {state.message}
          </Text>
        ) : null}

        {state.status === 'ready' && state.data.items.length > 0 ? (
          <Stack direction="column" gap={4}>
            <Text size="xs" tone="tertiary">
              Or one you have
            </Text>
            <ul className={styles.list}>
              {state.data.items.map((playlist) => (
                <li key={playlist.id}>
                  <button
                    type="button"
                    className={styles.choice}
                    disabled={busy}
                    onClick={() => void addTo(playlist.id)}
                  >
                    <Text truncate>{playlist.name}</Text>
                    <Text size="xs" tone="tertiary">
                      {songsLine(playlist.songs, playlist.held, playlist.duration)}
                    </Text>
                  </button>
                </li>
              ))}
            </ul>
          </Stack>
        ) : null}

        {error != null ? (
          <Text size="sm" tone="danger" role="alert">
            {error}
          </Text>
        ) : null}
      </Stack>
    </Dialog>
  )
}
