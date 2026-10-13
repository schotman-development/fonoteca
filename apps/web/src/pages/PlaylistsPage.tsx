import { describeError } from '@fonoteca/api-client'
import { Badge, Button, CatalogueCard, CatalogueGrid, Input, Stack, Text } from '@fonoteca/ui'
import { Link, useNavigate } from '@tanstack/react-router'
import { useId, useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './BrowsePage.module.css'
import { albumArt } from './coverArt.ts'
import { songsLine } from './playlists.ts'

/**
 * Every playlist, static and smart, as a shelf of covers.
 *
 * Each is an m3u in the library's Playlists folder, which is what makes it
 * reach Navidrome, Roon and the production copy; the page says so, because a
 * person editing one here should know where else it shows, and that a static
 * one edited there is edited here too.
 */
export function PlaylistsPage() {
  const state = useApiQuery(() => api.get('/api/catalogue/playlists'), [])

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Playlists
          </Text>
        </h1>
        <Text tone="secondary" block>
          Each is an m3u in the library's Playlists folder, so Navidrome, Roon and phone apps read
          it too. A playlist is its file: one edited by another program shows here as it now is, and
          when Fonoteca renames or moves a song's file the playlist follows it. A smart playlist is
          rules instead, and Fonoteca rewrites its file whenever the library changes.
        </Text>
      </Stack>

      <Stack gap={16} align="end" wrap>
        <NewPlaylist />
        <NewSmartPlaylist />
      </Stack>

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? <Text tone="tertiary">Reading the playlists…</Text> : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read the playlists</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}

        {state.status === 'ready' && state.data.items.length === 0 ? (
          <Text tone="tertiary">
            No playlists yet. Make one here, or add songs from{' '}
            <Link to="/library/tracks" className={styles.link}>
              <Text tone="accent">Tracks</Text>
            </Link>{' '}
            or an album's page.
          </Text>
        ) : null}
      </div>

      {state.status === 'ready' && state.data.items.length > 0 ? (
        <CatalogueGrid aria-label="Playlists">
          {state.data.items.map((playlist) => {
            const cover = playlist.albums[0] != null ? albumArt(playlist.albums[0]) : undefined

            return (
              <CatalogueCard
                key={`${playlist.kind}:${playlist.id}`}
                variant="album"
                title={playlist.name}
                subtitle={songsLine(playlist.songs, playlist.songs, playlist.duration)}
                meta={
                  playlist.kind === 'Smart' ? (
                    <Badge size="sm">Smart</Badge>
                  ) : playlist.held < playlist.songs ? (
                    <Badge tone="warning" size="sm">
                      {(playlist.songs - playlist.held).toLocaleString()} not in the library
                    </Badge>
                  ) : undefined
                }
                {...(cover != null ? { image: cover } : {})}
                render={(props) =>
                  playlist.kind === 'Smart' ? (
                    <Link
                      {...props}
                      to="/library/playlists/smart/$playlistId"
                      params={{ playlistId: playlist.id }}
                    />
                  ) : (
                    <Link
                      {...props}
                      to="/library/playlists/$playlistId"
                      params={{ playlistId: playlist.id }}
                    />
                  )
                }
              />
            )
          })}
        </CatalogueGrid>
      ) : null}
    </Stack>
  )
}

function NewSmartPlaylist() {
  const navigate = useNavigate()

  return (
    <Button
      variant="secondary"
      onClick={() =>
        void navigate({ to: '/library/playlists/smart/$playlistId', params: { playlistId: 'new' } })
      }
    >
      New smart playlist
    </Button>
  )
}

function NewPlaylist() {
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const id = useId()

  const create = async () => {
    setBusy(true)
    setError(null)
    try {
      const playlist = await api.post('/api/catalogue/playlists', {
        json: { name: name.trim(), recordingIds: null, fileIds: null },
      })
      await navigate({ to: '/library/playlists/$playlistId', params: { playlistId: playlist.id } })
    } catch (cause) {
      setError(describeError(cause))
      setBusy(false)
    }
  }

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        if (name.trim() !== '') void create()
      }}
    >
      <Stack direction="column" gap={4}>
        <label htmlFor={id}>
          <Text size="xs" tone="tertiary">
            A new playlist
          </Text>
        </label>
        <Stack gap={8} wrap>
          <div className={styles.search}>
            <Input
              id={id}
              value={name}
              maxLength={200}
              placeholder="Road trip"
              onChange={(event) => setName(event.target.value)}
            />
          </div>
          <Button type="submit" variant="primary" disabled={busy || name.trim() === ''}>
            Create
          </Button>
        </Stack>
        {error != null ? (
          <Text size="sm" tone="danger" role="alert">
            {error}
          </Text>
        ) : null}
      </Stack>
    </form>
  )
}
