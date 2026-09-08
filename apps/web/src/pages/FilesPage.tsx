import type { components } from '@fonoteca/api-client'
import { describeError } from '@fonoteca/api-client'
import {
  Badge,
  Button,
  Dialog,
  Input,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Text,
  VisuallyHidden,
} from '@fonoteca/ui'
import { useNavigate, useSearch } from '@tanstack/react-router'
import { type ChangeEvent, useId, useState } from 'react'

import { api, apiBaseUrl } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import { FilePreviewPane } from './FilePreviewPane.tsx'
import styles from './FilesPage.module.css'
import { breadcrumbs, destinationFor, matchSummary, parentOf, uploadName } from './files.ts'
import { fileSize } from './qobuz.ts'

type FolderEntry = components['schemas']['FolderEntry']

type Notice = { readonly tone: 'success' | 'danger'; readonly message: string }

/**
 * The library as it actually sits on disk.
 *
 * **Every other screen here is about music; this one is about files**, and it
 * exists because the two disagree. A duplicate rip is one album to MusicBrainz
 * and two folders on disk, and nothing that reasons about recordings can say
 * which of the two is the mess. This can: the rows come from the filesystem —
 * artwork, playlists and albums nothing has scanned included — and the counts on
 * them come from the catalogue, so "12 filed" sitting above "31 unmatched"
 * answers the question in one glance.
 *
 * Three acts, and each one is deliberately narrow:
 *
 * - **Trash** moves entries out to `Fonoteca:TrashPath` under a timestamp.
 *   Nothing is deleted, so a wrong click is undone with `mv`.
 * - **Rename** changes the last segment and carries the catalogue rows with it.
 *   A rename that went through the filesystem alone would read to the next scan
 *   as a deletion and an arrival, and every AcoustID, recording link and album
 *   decision under the folder would be discarded.
 * - **Upload** writes one file per request, streamed, so an album keeps its disc
 *   folders and nothing is buffered to a temporary copy on the way in.
 *
 * The catalogue only learns about an upload at the next scan, which is why the
 * scan button is on this page rather than only on the dashboard: uploading and
 * then wondering why the album has no artists is the obvious first confusion.
 */
export function FilesPage() {
  const { path: folder = '' } = useSearch({ from: '/files' })
  const navigate = useNavigate({ from: '/files' })
  const [attempt, setAttempt] = useState(0)
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set())
  // The row being looked at, which is not the set being acted on. A file
  // browser has both: one highlighted thing described in the pane, and any
  // number of ticked things a command applies to.
  const [current, setCurrent] = useState<string | null>(null)
  const [renaming, setRenaming] = useState<FolderEntry | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [busy, setBusy] = useState(false)
  const [uploading, setUploading] = useState<{ done: number; total: number } | null>(null)
  const [notice, setNotice] = useState<Notice | null>(null)

  const state = useApiQuery(
    () => api.get('/api/files', { params: { query: { path: folder } } }),
    [folder, attempt],
  )

  const refresh = () => setAttempt((value) => value + 1)

  const open = (path: string) => navigate({ to: '/files', search: path === '' ? {} : { path } })

  /**
   * Navigating is also the one reliable way to drop a selection of rows that no
   * longer exist — and it is hung off the folder rather than off `open`,
   * because the folder now moves without anybody clicking anything. Back and
   * forward change it too, and a selection is a set of paths in the folder it
   * was made in: carried across, the trash button acts on rows that are not on
   * the screen.
   */
  const [shown, setShown] = useState(folder)

  if (shown !== folder) {
    setShown(folder)
    setSelected(new Set())
    setCurrent(null)
    setNotice(null)
  }

  const run = async (work: () => Promise<Notice>) => {
    setBusy(true)

    try {
      setNotice(await work())
    } catch (cause: unknown) {
      setNotice({ tone: 'danger', message: describeError(cause) })
    } finally {
      setBusy(false)
      setSelected(new Set())
      refresh()
    }
  }

  const trash = () => {
    // Closed first, not on the way out: a refusal renders as a notice on the
    // page, and the page is behind the modal.
    setConfirming(false)

    return run(async () => {
      const result = await api.post('/api/files/trash', { json: { paths: [...selected] } })

      return {
        tone: 'success',
        message: `Moved ${result.entries} to ${result.destination}. Undo it with mv.`,
      }
    })
  }

  const move = (entry: FolderEntry, folder: string, name: string) =>
    run(async () => {
      const to = destinationFor(entry.path, folder, name)

      if (to === null) return { tone: 'danger', message: 'That is not a destination.' }

      await api.post('/api/files/move', { json: { from: entry.path, to } })
      setRenaming(null)

      return { tone: 'success', message: `Moved to ${to}. The catalogue came with it.` }
    })

  /**
   * One request per file, sequentially.
   *
   * Not the typed client: the body is the file's bytes rather than JSON, which
   * is what keeps a 475 MB FLAC from being buffered into a temporary copy on
   * the way in. Sequential because the interesting failure is a half-uploaded
   * album, and a progress count nobody can read is not an improvement on it.
   */
  const upload = (files: FileList) =>
    run(async () => {
      const chosen = [...files]
      let failed = 0

      try {
        for (const [index, file] of chosen.entries()) {
          setUploading({ done: index, total: chosen.length })

          const query = new URLSearchParams({ folder, name: uploadName(file) })

          // Per file, because the loop is the album. A 409 on one track already
          // came back as `!ok` and carried on; a dropped connection threw, and
          // threw out of the loop — so one transient failure on track 3 silently
          // abandoned tracks 4 to 12 and reported the ones that made it as a
          // success. A half-uploaded album that says it is whole is the one
          // outcome here worth writing code to avoid.
          try {
            const response = await fetch(`${apiBaseUrl}/api/files/upload?${query}`, {
              method: 'POST',
              // Stated, not inferred. `body: file` otherwise sends the file's
              // own type — `audio/flac` — and the endpoint declares it consumes
              // application/octet-stream, so every real upload came back 415
              // while an aborted-route probe never reached the server to find
              // out. The bytes are opaque to the endpoint either way.
              headers: { 'Content-Type': 'application/octet-stream' },
              body: file,
            })

            if (!response.ok) failed += 1
          } catch {
            failed += 1
          }
        }
      } finally {
        // In a finally, or an escaping error leaves "Uploading 3 of 12…" on the
        // toolbar for the rest of the session.
        setUploading(null)
      }

      return failed === 0
        ? {
            tone: 'success',
            message: `${chosen.length} file${chosen.length === 1 ? '' : 's'} written. Scan to catalogue them.`,
          }
        : {
            tone: 'danger',
            message: `${failed} of ${chosen.length} were refused — already there, most likely.`,
          }
    })

  const scan = () =>
    run(async () => {
      const summary = await api.post('/api/library/scan')

      return {
        tone: 'success',
        message: `Scanned ${summary.filesSeen.toLocaleString()} files: ${summary.added} added, ${summary.removed} removed.`,
      }
    })

  const entries = state.status === 'ready' ? state.data.entries : []

  return (
    <Stack direction="column" gap={20}>
      <Stack direction="column" gap={4}>
        <h1 className={styles.title}>
          <Text size="xl" weight="semibold" block>
            Files
          </Text>
        </h1>
        <Text tone="secondary" block>
          The library as it sits on disk, with what the catalogue has made of it. Nothing here is
          deleted — trashing moves a folder out to a stamped bin beside the library.
        </Text>
      </Stack>

      <Trail path={folder} onOpen={open} />

      <Toolbar
        busy={busy}
        uploading={uploading}
        selected={selected.size}
        onTrash={() => setConfirming(true)}
        onUpload={upload}
        onScan={scan}
      />

      {notice ? (
        <div role="status">
          <Badge tone={notice.tone}>{notice.message}</Badge>
        </div>
      ) : null}

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? <Text tone="tertiary">Reading the directory…</Text> : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read that folder</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}

        {state.status === 'ready' && !state.data.exists ? (
          <Badge tone="warning">
            That folder is not there. It may have been trashed or unmounted.
          </Badge>
        ) : null}
      </div>

      {state.status === 'ready' && state.data.exists ? (
        <div className={styles.layout}>
          {entries.length === 0 ? (
            <Text tone="tertiary">Nothing in this folder.</Text>
          ) : (
            <Listing
              entries={entries}
              selected={selected}
              current={current}
              onSelect={setSelected}
              onCurrent={setCurrent}
              onOpen={open}
              onRename={setRenaming}
            />
          )}

          <FilePreviewPane
            entry={entries.find((entry) => entry.path === current) ?? null}
            onOpen={open}
          />
        </div>
      ) : null}

      <MoveDialog entry={renaming} busy={busy} onClose={() => setRenaming(null)} onMove={move} />

      <Dialog
        open={confirming}
        onClose={() => setConfirming(false)}
        title="Move to trash?"
        description={
          selected.size === 1
            ? 'One entry, and everything inside it.'
            : `${selected.size} entries, and everything inside them.`
        }
        footer={
          <Stack gap={8} justify="end">
            <Button variant="ghost" onClick={() => setConfirming(false)}>
              Keep them
            </Button>
            <Button variant="danger" disabled={busy} onClick={trash}>
              Move to trash
            </Button>
          </Stack>
        }
      >
        <Stack direction="column" gap={8}>
          <Text size="sm" tone="secondary" block>
            They move out of the library into a folder stamped with the time, keeping their layout,
            and their catalogue rows go with them. Nothing is deleted — putting one back is a{' '}
            <code>mv</code>.
          </Text>
          <ul className={styles.paths}>
            {[...selected].map((path) => (
              <li key={path}>
                <Text size="sm" family="mono">
                  {path}
                </Text>
              </li>
            ))}
          </ul>
        </Stack>
      </Dialog>
    </Stack>
  )
}

/**
 * The way back up.
 *
 * Always rooted, so a folder that has been trashed out from under the tab is
 * still somewhere a person can leave.
 */
function Trail({ path, onOpen }: { path: string; onOpen: (path: string) => void }) {
  const trail = breadcrumbs(path)

  return (
    <nav aria-label="Folder" className={styles.trail}>
      {trail.map((crumb, index) => (
        <span key={crumb.path} className={styles.crumb}>
          {index > 0 ? <Text tone="tertiary">/</Text> : null}
          {index === trail.length - 1 ? (
            <Text weight="semibold" aria-current="page">
              {crumb.name}
            </Text>
          ) : (
            <Button variant="ghost" size="sm" onClick={() => onOpen(crumb.path)}>
              {crumb.name}
            </Button>
          )}
        </span>
      ))}
    </nav>
  )
}

function Toolbar({
  busy,
  uploading,
  selected,
  onTrash,
  onUpload,
  onScan,
}: {
  busy: boolean
  uploading: { done: number; total: number } | null
  selected: number
  onTrash: () => void
  onUpload: (files: FileList) => void
  onScan: () => void
}) {
  const filesId = useId()
  const folderId = useId()

  const pick = (event: ChangeEvent<HTMLInputElement>) => {
    if (event.target.files?.length) onUpload(event.target.files)

    // Or picking the same album twice in a row is silently ignored: the input's
    // value has not changed, so no change event is fired the second time.
    event.target.value = ''
  }

  return (
    <Stack gap={8} align="center" wrap>
      <Button variant="danger" size="sm" disabled={busy || selected === 0} onClick={onTrash}>
        Move to trash{selected > 0 ? ` (${selected})` : ''}
      </Button>

      <label className={styles.upload} htmlFor={filesId}>
        <Text size="sm">Add files</Text>
        <input id={filesId} type="file" multiple onChange={pick} disabled={busy} />
      </label>

      <label className={styles.upload} htmlFor={folderId}>
        <Text size="sm">Add an album folder</Text>
        <input
          id={folderId}
          type="file"
          multiple
          // React has no prop for it and every browser that matters supports it.
          // Set on the element, because the attribute is what makes the picker
          // choose a directory and hand back webkitRelativePath — which is the
          // only thing that keeps CD1 and CD2 apart on the way in.
          ref={(node) => {
            if (node)
              (node as HTMLInputElement & { webkitdirectory: boolean }).webkitdirectory = true
          }}
          onChange={pick}
          disabled={busy}
        />
      </label>

      <Button variant="secondary" size="sm" disabled={busy} onClick={onScan}>
        Scan library
      </Button>

      {uploading ? (
        <Text size="sm" tone="tertiary">
          Uploading {uploading.done + 1} of {uploading.total}…
        </Text>
      ) : null}
    </Stack>
  )
}

function Listing({
  entries,
  selected,
  current,
  onSelect,
  onCurrent,
  onOpen,
  onRename,
}: {
  entries: readonly FolderEntry[]
  selected: ReadonlySet<string>
  current: string | null
  onSelect: (paths: ReadonlySet<string>) => void
  onCurrent: (path: string) => void
  onOpen: (path: string) => void
  onRename: (entry: FolderEntry) => void
}) {
  const toggle = (path: string) => {
    const next = new Set(selected)

    if (!next.delete(path)) next.add(path)

    onSelect(next)
  }

  const all = selected.size === entries.length && entries.length > 0

  return (
    <Table density="compact">
      <thead>
        <tr>
          <TableHeaderCell className={styles.pick}>
            <input
              type="checkbox"
              checked={all}
              aria-label="Select every entry"
              onChange={() =>
                onSelect(all ? new Set() : new Set(entries.map((entry) => entry.path)))
              }
            />
          </TableHeaderCell>
          <TableHeaderCell className={styles.name}>Name</TableHeaderCell>
          <TableHeaderCell>Catalogue</TableHeaderCell>
          <TableHeaderCell numeric className={styles.number}>
            Size
          </TableHeaderCell>
          <TableHeaderCell numeric className={styles.number}>
            Modified
          </TableHeaderCell>
          <TableHeaderCell>
            <span className={styles.hidden}>Actions</span>
          </TableHeaderCell>
        </tr>
      </thead>
      <tbody>
        {entries.map((entry) => {
          const summary = matchSummary(entry)

          return (
            <tr
              key={entry.path}
              data-current={entry.path === current || undefined}
              className={styles.row}
            >
              <TableCell className={styles.pick}>
                <input
                  type="checkbox"
                  checked={selected.has(entry.path)}
                  aria-label={`Select ${entry.name}`}
                  onChange={() => toggle(entry.path)}
                />
              </TableCell>

              <TableCell className={styles.name}>
                <span className={styles.entry}>
                  <EntryIcon entry={entry} />

                  {/*
                    Selecting and opening are different clicks, the way they are
                    in every file browser. The name selects — which is what fills
                    the preview pane — and the chevron beside a folder is what
                    goes into it. Made one control, opening a folder would make
                    it impossible to look at one without leaving the folder it is
                    in.
                  */}
                  <button
                    type="button"
                    className={styles.select}
                    onClick={() => onCurrent(entry.path)}
                    onDoubleClick={() => entry.isDirectory && onOpen(entry.path)}
                  >
                    {entry.name}
                  </button>

                  {entry.isDirectory ? (
                    <button
                      type="button"
                      className={styles.enter}
                      aria-label={`Open ${entry.name}`}
                      onClick={() => onOpen(entry.path)}
                    >
                      ›
                    </button>
                  ) : null}
                </span>
              </TableCell>

              <TableCell>
                {summary ? (
                  <Badge tone={summary.tone === 'positive' ? 'success' : summary.tone}>
                    {summary.label}
                  </Badge>
                ) : (
                  <Text size="xs" tone="tertiary">
                    {entry.isDirectory || entry.isAudio ? 'not scanned' : '—'}
                  </Text>
                )}
              </TableCell>

              <TableCell numeric className={styles.number}>
                <Text size="sm" family="mono" tone="secondary">
                  {fileSize(entry.sizeBytes) ?? '—'}
                </Text>
              </TableCell>

              <TableCell numeric className={styles.number}>
                <Text size="sm" tone="tertiary">
                  {new Date(entry.modifiedUtc).toLocaleDateString()}
                </Text>
              </TableCell>

              <TableCell>
                <Button variant="ghost" size="sm" onClick={() => onRename(entry)}>
                  Rename
                </Button>
              </TableCell>
            </tr>
          )
        })}
      </tbody>
    </Table>
  )
}

/**
 * What kind of thing this row is, at a glance.
 *
 * Text rather than an icon set: the design system has no icon library and ADR
 * 0003 is why, so adding one for six glyphs would be the largest dependency
 * decision on the screen. `kind` comes from the server's own allowlist, so the
 * badge and the preview cannot disagree about what a file is.
 */
function EntryIcon({ entry }: { entry: FolderEntry }) {
  const label = entry.isDirectory
    ? 'Folder'
    : entry.isAudio
      ? 'Audio'
      : entry.kind === 'Image'
        ? 'Image'
        : entry.kind === 'Text'
          ? 'Text'
          : 'File'

  return (
    <span className={styles.icon} data-kind={label.toLowerCase()}>
      {/* The glyph is decoration; the word beside it is what is announced. */}
      <span aria-hidden="true">
        {label === 'Folder' ? '▸' : label === 'Audio' ? '♪' : label === 'Image' ? '▤' : '≡'}
      </span>
      <VisuallyHidden>{label}</VisuallyHidden>
    </span>
  )
}

/**
 * Moving and renaming, which are one act with two fields.
 *
 * This is the only thing on the screen that keeps the catalogue rather than
 * moving it aside, so the dialog says so — it is the reason to use it instead
 * of a shell, where the same rename costs every AcoustID under the folder at
 * the next scan.
 */
function MoveDialog({
  entry,
  busy,
  onClose,
  onMove,
}: {
  entry: FolderEntry | null
  busy: boolean
  onClose: () => void
  onMove: (entry: FolderEntry, folder: string, name: string) => void
}) {
  const folderId = useId()
  const nameId = useId()

  // Keyed on the entry, so the dialog is remounted per row and both fields
  // start from that row rather than from whatever was typed last.
  return entry === null ? null : (
    <MoveFields
      key={entry.path}
      entry={entry}
      busy={busy}
      folderId={folderId}
      nameId={nameId}
      onClose={onClose}
      onMove={onMove}
    />
  )
}

function MoveFields({
  entry,
  busy,
  folderId,
  nameId,
  onClose,
  onMove,
}: {
  entry: FolderEntry
  busy: boolean
  folderId: string
  nameId: string
  onClose: () => void
  onMove: (entry: FolderEntry, folder: string, name: string) => void
}) {
  const [folder, setFolder] = useState(() => parentOf(entry.path))
  const [name, setName] = useState(entry.name)

  const destination = destinationFor(entry.path, folder, name)

  return (
    <Dialog
      open
      onClose={onClose}
      title="Move or rename"
      description={entry.path}
      footer={
        <Stack gap={8} justify="end">
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            disabled={busy || destination === null}
            onClick={() => onMove(entry, folder, name)}
          >
            Move
          </Button>
        </Stack>
      }
    >
      <Stack direction="column" gap={12}>
        <Stack direction="column" gap={4}>
          <label htmlFor={folderId}>
            <Text size="xs" tone="tertiary">
              Folder — blank for the library root
            </Text>
          </label>
          <Input
            id={folderId}
            value={folder}
            placeholder="Johannes Brahms"
            onChange={(event) => setFolder(event.target.value)}
          />
        </Stack>

        <Stack direction="column" gap={4}>
          <label htmlFor={nameId}>
            <Text size="xs" tone="tertiary">
              Name
            </Text>
          </label>
          <Input id={nameId} value={name} onChange={(event) => setName(event.target.value)} />
        </Stack>

        <Text size="sm" tone="secondary" block>
          {destination === null ? (
            'Nothing would change.'
          ) : (
            <>
              Becomes <Text family="mono">{destination}</Text>. The catalogue moves with it, so
              nothing identified has to be identified again.
            </>
          )}
        </Text>
      </Stack>
    </Dialog>
  )
}
