import { type components, describeError } from '@fonoteca/api-client'
import {
  Badge,
  Button,
  Dialog,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Text,
  VisuallyHidden,
} from '@fonoteca/ui'
import { Link } from '@tanstack/react-router'
import { type ReactNode, useId, useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './DuplicatesPage.module.css'
import { fileSize } from './qobuz.ts'

type DuplicateAlbum = components['schemas']['DuplicateAlbum']
type DuplicateSong = components['schemas']['DuplicateSong']

type Notice = { readonly tone: 'success' | 'danger'; readonly message: string }

/**
 * What the library holds more than once: an album in two folders, or a song
 * twice in one album folder.
 *
 * A list, and nothing on it moves until a person picks the copy to lose — the
 * owner's choice over keeping the best automatically. A copy goes to the same
 * trash the file manager uses, so a wrong click is a `mv` back.
 */
export function DuplicatesPage() {
  const [loaded, setLoaded] = useState(0)
  const [trashing, setTrashing] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<Notice | null>(null)

  const state = useApiQuery(() => api.get('/api/catalogue/duplicates'), [loaded])

  const trash = async (path: string) => {
    // Closed first: a refusal renders as a notice on the page, behind the modal.
    setTrashing(null)
    setBusy(true)

    try {
      const result = await api.post('/api/files/trash', { json: { paths: [path] } })
      setNotice({ tone: 'success', message: `Moved ${path} to ${result.destination}.` })
    } catch (cause: unknown) {
      setNotice({ tone: 'danger', message: describeError(cause) })
    } finally {
      setBusy(false)
      setLoaded((value) => value + 1)
    }
  }

  return (
    <Stack direction="column" gap={24}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Duplicates
          </Text>
        </h1>
        <Text tone="secondary" block>
          Albums held in more than one folder, and songs held twice in one album folder. A song on
          its album and again on a best-of is two records, not a duplicate. Nothing moves until you
          move a copy to the trash.
        </Text>
      </Stack>

      {notice ? (
        <div role="status">
          <Badge tone={notice.tone}>{notice.message}</Badge>
        </div>
      ) : null}

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? <Text tone="tertiary">Reading the catalogue…</Text> : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read the catalogue</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}
      </div>

      {state.status === 'ready' ? (
        <>
          <Section
            title="Albums in more than one folder"
            empty="No album is held in two folders."
            count={state.data.albums.length}
          >
            {state.data.albums.map((album) => (
              <AlbumRow key={album.id} album={album} busy={busy} onTrash={setTrashing} />
            ))}
          </Section>

          <Section
            title="Songs twice in one folder"
            empty="No album folder holds a song twice."
            count={state.data.songs.length}
          >
            {state.data.songs.map((song) => (
              <SongRow
                key={`${song.folder}\u0000${song.copies[0]?.path}`}
                song={song}
                busy={busy}
                onTrash={setTrashing}
              />
            ))}
          </Section>
        </>
      ) : null}

      <Dialog
        open={trashing !== null}
        onClose={() => setTrashing(null)}
        title="Move to trash?"
        description="This copy, and everything inside it."
        footer={
          <Stack gap={8} justify="end">
            <Button variant="ghost" onClick={() => setTrashing(null)}>
              Keep it
            </Button>
            <Button
              variant="danger"
              disabled={busy}
              onClick={() => (trashing === null ? undefined : void trash(trashing))}
            >
              Move to trash
            </Button>
          </Stack>
        }
      >
        <Stack direction="column" gap={8}>
          <Text size="sm" tone="secondary" block>
            It moves out of the library into a folder stamped with the time, keeping its layout, and
            its catalogue rows go with it. Nothing is deleted: a <code>mv</code> puts it back.
          </Text>
          <Text size="sm" family="mono" block>
            {trashing}
          </Text>
        </Stack>
      </Dialog>
    </Stack>
  )
}

function Section({
  title,
  empty,
  count,
  children,
}: {
  readonly title: string
  readonly empty: string
  readonly count: number
  readonly children: ReactNode
}) {
  const id = useId()

  return (
    <section aria-labelledby={id}>
      <Stack direction="column" gap={12}>
        <h2 id={id} className={styles.title}>
          <Text size="lg" weight="semibold" block>
            {title}
            {count > 0 ? <span className={styles.count}> {count.toLocaleString()}</span> : null}
          </Text>
        </h2>
        {count === 0 ? <Text tone="tertiary">{empty}</Text> : children}
      </Stack>
    </section>
  )
}

function AlbumRow({
  album,
  busy,
  onTrash,
}: {
  readonly album: DuplicateAlbum
  readonly busy: boolean
  readonly onTrash: (path: string) => void
}) {
  return (
    <div className={styles.panel}>
      <Stack direction="column" gap={8}>
        <Stack direction="column" gap={2}>
          <Link
            to="/library/albums/$albumId"
            params={{ albumId: album.id }}
            className={styles.link}
          >
            <Text weight="semibold">{album.title}</Text>
          </Link>
          {album.artist ? (
            <Text size="sm" tone="secondary">
              {album.artist}
            </Text>
          ) : null}
        </Stack>

        <div className={styles.table}>
          <Table density="compact">
            <thead>
              <tr>
                <TableHeaderCell className={styles.path}>Folder</TableHeaderCell>
                <TableHeaderCell numeric>Songs</TableHeaderCell>
                <TableHeaderCell numeric>Files</TableHeaderCell>
                <TableHeaderCell>Format</TableHeaderCell>
                <TableHeaderCell numeric>Size</TableHeaderCell>
                <TableHeaderCell>
                  <VisuallyHidden>Actions</VisuallyHidden>
                </TableHeaderCell>
              </tr>
            </thead>
            <tbody>
              {album.folders.map((folder) => (
                <tr key={folder.path}>
                  <TableCell className={styles.path}>
                    <Stack gap={8} align="center" wrap>
                      <Link to="/files" search={{ path: folder.path }} className={styles.link}>
                        <Text size="sm" family="mono">
                          {folder.path}
                        </Text>
                      </Link>
                      <Marks better={folder.better} damaged={folder.damaged} />
                    </Stack>
                  </TableCell>
                  <TableCell numeric>{folder.songs.toLocaleString()}</TableCell>
                  <TableCell numeric>{folder.files.toLocaleString()}</TableCell>
                  <TableCell className={styles.nowrap}>{folder.formats.join(', ')}</TableCell>
                  <TableCell numeric className={styles.nowrap}>
                    {fileSize(folder.sizeBytes)}
                  </TableCell>
                  <TableCell className={styles.nowrap}>
                    <Button
                      variant="danger"
                      size="sm"
                      disabled={busy}
                      onClick={() => onTrash(folder.path)}
                    >
                      Move to trash
                    </Button>
                  </TableCell>
                </tr>
              ))}
            </tbody>
          </Table>
        </div>
      </Stack>
    </div>
  )
}

function SongRow({
  song,
  busy,
  onTrash,
}: {
  readonly song: DuplicateSong
  readonly busy: boolean
  readonly onTrash: (path: string) => void
}) {
  return (
    <div className={styles.panel}>
      <Stack direction="column" gap={8}>
        <Stack direction="column" gap={2}>
          <Stack gap={8} align="center" wrap>
            <Text weight="semibold">{song.title}</Text>
            {song.sameAudio ? <Badge tone="info">Same audio</Badge> : null}
          </Stack>
          <Link to="/files" search={{ path: song.folder }} className={styles.link}>
            <Text size="sm" tone="secondary" family="mono">
              {song.folder}
            </Text>
          </Link>
        </Stack>

        <div className={styles.table}>
          <Table density="compact">
            <thead>
              <tr>
                <TableHeaderCell className={styles.path}>File</TableHeaderCell>
                <TableHeaderCell>Format</TableHeaderCell>
                <TableHeaderCell numeric>Size</TableHeaderCell>
                <TableHeaderCell>
                  <VisuallyHidden>Actions</VisuallyHidden>
                </TableHeaderCell>
              </tr>
            </thead>
            <tbody>
              {song.copies.map((copy) => (
                <tr key={copy.path}>
                  <TableCell className={styles.path}>
                    <Stack gap={8} align="center" wrap>
                      <Text size="sm" family="mono">
                        {song.folder === '' ? copy.path : copy.path.slice(song.folder.length + 1)}
                      </Text>
                      <Marks better={copy.better} damaged={copy.damaged ? 1 : 0} />
                    </Stack>
                  </TableCell>
                  <TableCell className={styles.nowrap}>{copy.format}</TableCell>
                  <TableCell numeric className={styles.nowrap}>
                    {fileSize(copy.sizeBytes)}
                  </TableCell>
                  <TableCell className={styles.nowrap}>
                    <Button
                      variant="danger"
                      size="sm"
                      disabled={busy}
                      onClick={() => onTrash(copy.path)}
                    >
                      Move to trash
                    </Button>
                  </TableCell>
                </tr>
              ))}
            </tbody>
          </Table>
        </div>
      </Stack>
    </div>
  )
}

/** The better copy, and damage the probe found; nothing at all where neither applies. */
function Marks({ better, damaged }: { readonly better: boolean; readonly damaged: number }) {
  return (
    <>
      {better ? <Badge tone="success">Better</Badge> : null}
      {damaged > 0 ? (
        <Badge tone="danger">{damaged === 1 ? 'Damaged' : `${damaged} damaged`}</Badge>
      ) : null}
    </>
  )
}
