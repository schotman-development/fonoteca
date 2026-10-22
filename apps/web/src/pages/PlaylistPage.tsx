import { type components, describeError } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  Button,
  Dialog,
  Input,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Text,
  usePlayback,
  VisuallyHidden,
} from '@fonoteca/ui'
import { useNavigate, useParams } from '@tanstack/react-router'
import { useId, useState } from 'react'

import { useCan } from '../AccountGate.tsx'
import { api, apiBaseUrl } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './BrowsePage.module.css'
import { albumArt } from './coverArt.ts'
import { contentUrl } from './files.ts'
import own from './PlaylistPage.module.css'
import { coverAlbums, moved, songsLine } from './playlists.ts'
import { TrackCells } from './TracksPage.tsx'

type PlaylistResponse = components['schemas']['PlaylistResponse']
type PlaylistEntryRow = components['schemas']['PlaylistEntryRow']

export function PlaylistPage() {
  const { playlistId } = useParams({ from: '/library/playlists/$playlistId' })

  // Keyed, so an edit made on one playlist is not shown on the next.
  return <Playlist key={playlistId} id={playlistId} />
}

/**
 * One static playlist: its file's entries in order, each playing the file it
 * names.
 *
 * Every edit is saved as it is made and the server's answer replaces the page,
 * so what is shown is what the file now says. An entry naming nothing the
 * library holds stays in its place, dimmed, as the file has it. Entries are
 * named by their place, so an edit carries the version of the file it was made
 * against, and one made after another program changed the file is refused.
 */
function Playlist({ id }: { readonly id: string }) {
  const navigate = useNavigate()
  const state = useApiQuery(
    () => api.get('/api/catalogue/playlists/{file}', { params: { path: { file: id } } }),
    [id],
  )
  const [edited, setEdited] = useState<PlaylistResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const playlist = edited ?? (state.status === 'ready' ? state.data : null)

  const save = async (change: { readonly name?: string; readonly entries?: readonly number[] }) => {
    if (playlist == null) return false
    setBusy(true)
    setError(null)
    try {
      const saved = await api.post('/api/catalogue/playlists/{file}', {
        params: { path: { file: id } },
        json: {
          name: change.name ?? null,
          entries: change.entries != null ? [...change.entries] : null,
          version: playlist.version,
        },
      })

      // A rename is a new file, and so a new address.
      if (saved.id !== id) {
        await navigate({
          to: '/library/playlists/$playlistId',
          params: { playlistId: saved.id },
          replace: true,
        })
      } else {
        setEdited(saved)
      }
      return true
    } catch (cause) {
      setError(describeError(cause))
      return false
    } finally {
      setBusy(false)
    }
  }

  if (playlist == null) {
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

  const order = playlist.entries.map((entry) => entry.index)
  const move = (index: number, offset: number) => {
    const next = moved(order, index, offset)
    if (next != null) void save({ entries: next })
  }

  return (
    <Stack direction="column" gap={20}>
      <Header playlist={playlist} busy={busy} onRename={(name) => save({ name })} />

      {!playlist.editable ? (
        <Text size="sm" tone="secondary">
          This file is not UTF-8, so Fonoteca reads it and never writes it. Edit it where it was
          made.
        </Text>
      ) : null}

      {error != null ? (
        <Text size="sm" tone="danger" role="alert">
          {error}
        </Text>
      ) : null}

      {playlist.entries.length === 0 ? (
        <Text tone="tertiary">
          No songs yet. Add them from Tracks or an album's page, with the + beside each song.
        </Text>
      ) : (
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
                {playlist.editable ? (
                  <TableHeaderCell className={own.actionsCol}>
                    <VisuallyHidden>Order</VisuallyHidden>
                  </TableHeaderCell>
                ) : null}
              </tr>
            </thead>
            <tbody>
              {playlist.entries.map((entry, index) => (
                <tr key={entry.index}>
                  <TableCell numeric>
                    <Text size="sm" family="mono" tone="tertiary">
                      {index + 1}
                    </Text>
                  </TableCell>

                  {entry.track != null ? (
                    <TrackCells track={entry.track} />
                  ) : (
                    <Missing entry={entry} />
                  )}

                  {playlist.editable ? (
                    <TableCell>
                      <Stack gap={2}>
                        <Button
                          size="sm"
                          variant="ghost"
                          aria-label={`Move ${entry.title} up`}
                          disabled={busy || index === 0}
                          onClick={() => move(index, -1)}
                        >
                          ↑
                        </Button>
                        <Button
                          size="sm"
                          variant="ghost"
                          aria-label={`Move ${entry.title} down`}
                          disabled={busy || index === order.length - 1}
                          onClick={() => move(index, 1)}
                        >
                          ↓
                        </Button>
                        <Button
                          size="sm"
                          variant="ghost"
                          aria-label={`Remove ${entry.title} from the playlist`}
                          disabled={busy}
                          onClick={() =>
                            void save({ entries: order.filter((other) => other !== entry.index) })
                          }
                        >
                          ✕
                        </Button>
                      </Stack>
                    </TableCell>
                  ) : null}
                </tr>
              ))}
            </tbody>
          </Table>
        </div>
      )}
    </Stack>
  )
}

/** An entry naming nothing the library holds: what the file calls it, in the same columns, and why it does not play. */
export function Missing({ entry }: { readonly entry: PlaylistEntryRow }) {
  return (
    <>
      <TableCell />
      <TableCell truncate title={entry.title}>
        <Text tone="tertiary" truncate>
          {entry.title}
        </Text>
      </TableCell>
      <TableCell truncate>
        <Text size="sm" tone="tertiary" truncate>
          {entry.artist ?? '—'}
        </Text>
      </TableCell>
      <TableCell truncate title={entry.location}>
        <Text size="sm" tone="tertiary" truncate>
          Not in the library: {entry.location}
        </Text>
      </TableCell>
      <TableCell numeric>
        <Text size="sm" family="mono" tone="tertiary">
          —
        </Text>
      </TableCell>
      <TableCell />
    </>
  )
}

function Header({
  playlist,
  busy,
  onRename,
}: {
  readonly playlist: PlaylistResponse
  readonly busy: boolean
  readonly onRename: (name: string) => Promise<boolean>
}) {
  const playback = usePlayback()
  const mayPlay = useCan('Stream')
  const navigate = useNavigate()
  const [renaming, setRenaming] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [name, setName] = useState(playlist.name)
  const nameId = useId()

  const held = playlist.entries.filter((entry) => entry.track != null)
  const first = held[0]?.track
  const covers = coverAlbums(playlist.entries.map((entry) => entry.track?.album ?? null))
  const single = covers[0] != null ? albumArt(covers[0]) : undefined

  return (
    <header className={own.header}>
      <div className={own.cover} data-mosaic={covers.length === 4 ? '' : undefined}>
        {covers.length === 4 ? (
          covers.map((album) => {
            const art = albumArt(album)
            return (
              <Artwork
                key={album.albumId}
                name={album.title}
                size="fill"
                {...(art != null ? { src: art } : {})}
              />
            )
          })
        ) : (
          <Artwork
            name={covers[0]?.title ?? playlist.name}
            size="fill"
            {...(single != null ? { src: single } : {})}
          />
        )}
      </div>

      <Stack direction="column" gap={8}>
        <Text size="xs" tone="tertiary">
          Playlist
        </Text>

        {renaming ? (
          <form
            onSubmit={(event) => {
              event.preventDefault()
              if (name.trim() === '') return
              void onRename(name.trim()).then((saved) => {
                if (saved) setRenaming(false)
              })
            }}
          >
            <Stack gap={8} align="end" wrap>
              <Stack direction="column" gap={4}>
                <label htmlFor={nameId}>
                  <Text size="xs" tone="tertiary">
                    Name
                  </Text>
                </label>
                <Input
                  id={nameId}
                  value={name}
                  maxLength={200}
                  onChange={(event) => setName(event.target.value)}
                />
              </Stack>
              <Button type="submit" variant="primary" disabled={busy || name.trim() === ''}>
                Save
              </Button>
              <Button
                variant="ghost"
                onClick={() => {
                  setName(playlist.name)
                  setRenaming(false)
                }}
              >
                Cancel
              </Button>
            </Stack>
          </form>
        ) : (
          <h1 className={styles.title}>
            <Text size="xl" weight="semibold" block>
              {playlist.name}
            </Text>
          </h1>
        )}

        <Text tone="secondary">
          {songsLine(playlist.entries.length, held.length, playlist.duration)}
        </Text>
        <Text size="xs" tone="tertiary" family="mono">
          {playlist.file}
        </Text>

        <Stack gap={8} wrap>
          {mayPlay ? (
            <Button
              variant="primary"
              disabled={first == null}
              onClick={() => {
                if (first == null) return
                playback.play({
                  id: `${playlist.id}:${held[0]?.index}`,
                  src: contentUrl(apiBaseUrl, first.file.path),
                  title: first.title,
                  subtitle: playlist.name,
                })
              }}
            >
              Play
            </Button>
          ) : null}
          <Button
            variant="secondary"
            disabled={busy || renaming || !playlist.editable}
            onClick={() => setRenaming(true)}
          >
            Rename
          </Button>
          <Button variant="danger" disabled={busy} onClick={() => setDeleting(true)}>
            Delete
          </Button>
        </Stack>
      </Stack>

      {deleting ? (
        <DeleteDialog
          playlist={playlist}
          onClose={() => setDeleting(false)}
          onDeleted={() => void navigate({ to: '/library/playlists' })}
        />
      ) : null}
    </header>
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
      await api.delete('/api/catalogue/playlists/{file}', {
        params: { path: { file: playlist.id } },
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
      description="The songs stay in the library. The playlist's file moves to the trash, so other players drop it at their next scan."
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
