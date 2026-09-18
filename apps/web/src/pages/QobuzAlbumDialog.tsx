import type { components } from '@fonoteca/api-client'
import { describeError } from '@fonoteca/api-client'
import { Badge, Button, Dialog, Stack, Table, TableCell, TableHeaderCell, Text } from '@fonoteca/ui'
import { useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './QobuzAlbumDialog.module.css'
import { downloadSummary, duration, fileSize, OUTCOMES, qualityLabel } from './qobuz.ts'

type QobuzAlbumSummary = components['schemas']['QobuzAlbumSummary']
type AlbumDownload = components['schemas']['AlbumDownload']
type AlbumReplacement = components['schemas']['AlbumReplacement']

/** The album this download is meant to replace, when it was launched from one. */
export type Replacing = {
  readonly folder: string
  readonly title: string
  readonly files: number
  readonly formats: readonly string[]
}

type DownloadState =
  | { readonly status: 'idle' }
  | { readonly status: 'running' }
  | {
      readonly status: 'done'
      readonly result: AlbumDownload
      readonly replacement: AlbumReplacement | null
    }
  | { readonly status: 'error'; readonly message: string }

/**
 * One album, its track list, and the button that spends the subscription.
 *
 * A dialog rather than a route, for the old matching page's reason: the thing being
 * worked is the search result behind it, and dismissing an album should return
 * a person to the row they came from. There is also nothing to link to — a
 * Qobuz album id is somebody else's identifier for something this catalogue
 * does not hold.
 *
 * The track list is fetched rather than taken from the search result, because
 * the search does not carry one. That is one request per album opened, which is
 * the right trade: the alternative is a search that fetches thirty track lists
 * to show ten cards.
 */
export function QobuzAlbumDialog({
  album,
  canDownload,
  replacing,
  onClose,
}: {
  readonly album: QobuzAlbumSummary
  readonly canDownload: boolean
  readonly replacing?: Replacing | undefined
  readonly onClose: () => void
}) {
  const [download, setDownload] = useState<DownloadState>({ status: 'idle' })

  const state = useApiQuery(
    () => api.get('/api/qobuz/albums/{albumId}', { params: { path: { albumId: album.id } } }),
    [album.id],
  )

  async function start() {
    setDownload({ status: 'running' })

    try {
      if (replacing === undefined) {
        const result = await api.post('/api/qobuz/albums/{albumId}/download', {
          params: { path: { albumId: album.id } },
        })

        setDownload({ status: 'done', result, replacement: null })
        return
      }

      const upgrade = await api.post('/api/qobuz/albums/{albumId}/upgrade', {
        params: { path: { albumId: album.id } },
        // The file count travels with the folder so the API can refuse a
        // folder that turns out to hold more than the row said — a row's folder
        // is a prefix, and not every one of them is an album.
        json: { folder: replacing.folder, files: replacing.files },
      })

      setDownload({ status: 'done', result: upgrade.download, replacement: upgrade.replacement })
    } catch (cause: unknown) {
      setDownload({ status: 'error', message: describeError(cause) })
    }
  }

  const running = download.status === 'running'

  return (
    <Dialog
      open
      onClose={running ? () => undefined : onClose}
      size="lg"
      title={album.title}
      description={album.artist ?? undefined}
      footer={
        <Stack gap={8} justify="end" align="center" wrap>
          {/*
            Disabled mid-download, and that is now only the button. The dialog
            itself must stay dismissable: Escape closes a native <dialog>
            without asking, so refusing to act on the close event left the
            element shut with `open` still true and nothing able to re-open it.
            Dialog reconciles that now; this button is a nudge, not a lock.

            The transfer does go on writing files if somebody leaves — which is
            what the busy state on the acquisition screen is for.
          */}
          <Button variant="ghost" onClick={onClose} disabled={running}>
            {download.status === 'done' ? 'Done' : 'Cancel'}
          </Button>

          <Button
            variant="primary"
            onClick={() => void start()}
            disabled={
              !canDownload ||
              running ||
              download.status === 'done' ||
              !album.streamable ||
              state.status !== 'ready'
            }
          >
            {running
              ? 'Downloading…'
              : download.status === 'done'
                ? 'Downloaded'
                : replacing !== undefined
                  ? 'Download and replace'
                  : 'Download'}
          </Button>
        </Stack>
      }
    >
      <Stack direction="column" gap={16}>
        <Stack gap={8} align="center" wrap>
          <Text size="sm" tone="tertiary" family="mono">
            {[
              album.releaseDate?.slice(0, 4),
              `${album.trackCount} tracks`,
              album.discCount > 1 ? `${album.discCount} discs` : null,
            ]
              .filter(Boolean)
              .join(' · ')}
          </Text>

          {qualityLabel(album.maximumBitDepth, album.maximumSamplingRate) !== null ? (
            <Badge tone={album.hiRes ? 'accent' : 'neutral'} size="sm" mono>
              {qualityLabel(album.maximumBitDepth, album.maximumSamplingRate)}
            </Badge>
          ) : null}

          {album.streamable ? null : (
            <Badge tone="warning" size="sm">
              Your subscription does not cover this release
            </Badge>
          )}
        </Stack>

        {/*
          Said before the button is pressed rather than after. Somebody who
          expects an album to appear in the library and finds nothing there will
          go looking for a bug.
        */}
        <Text size="xs" tone="tertiary">
          Downloads land in the library as <code>Artist/Album/NN Title</code>. Nothing is added to
          the catalogue by the download itself — the next library scan is what catalogues them.
        </Text>

        {/*
          What is about to be given up, named, before the button is pressed —
          not after. The search that opened this dialog was seeded from an
          upgrade row, and nothing stops a person browsing on to an unrelated
          album from here; the folder is the only thing on screen that says which
          one is being traded away.
        */}
        {replacing !== undefined ? (
          <div className={styles.replacing}>
            <Stack direction="column" gap={4} align="start">
              <Stack gap={8} align="center" wrap>
                <Badge tone="warning" size="sm">
                  Replaces
                </Badge>
                <Text size="sm" family="mono">
                  {replacing.folder}
                </Text>
              </Stack>
              <Text size="xs" tone="tertiary">
                {replacing.files} file{replacing.files === 1 ? '' : 's'}
                {replacing.formats.length > 0 ? ` · ${replacing.formats.join(', ')}` : ''}. Its
                files are moved out of the library, not deleted — and only if every track arrives
                and what arrives measures better than what is there.
              </Text>
            </Stack>
          </div>
        ) : null}

        {download.status === 'idle' || download.status === 'running' ? (
          <TrackList state={state} />
        ) : null}

        <div role="status" aria-live="polite" aria-busy={running}>
          {running ? (
            <Text tone="secondary">
              Fetching {album.trackCount} track{album.trackCount === 1 ? '' : 's'}. This is one
              request per track and stays open until the last one lands.
            </Text>
          ) : null}

          {download.status === 'error' ? (
            <Stack direction="column" gap={4} align="start">
              <Badge tone="danger">The download failed</Badge>
              <Text size="sm" tone="tertiary">
                {download.message}
              </Text>
            </Stack>
          ) : null}

          {download.status === 'done' ? (
            <Stack direction="column" gap={12}>
              <Result result={download.result} />
              {download.replacement !== null ? (
                <ReplacementResult replacement={download.replacement} />
              ) : null}
            </Stack>
          ) : null}
        </div>
      </Stack>
    </Dialog>
  )
}

function TrackList({
  state,
}: {
  readonly state: ReturnType<typeof useApiQuery<components['schemas']['QobuzAlbumResponse']>>
}) {
  if (state.status === 'loading') {
    return (
      <Text tone="tertiary" role="status">
        Reading the track list…
      </Text>
    )
  }

  if (state.status === 'error') {
    return (
      <Stack direction="column" gap={4} align="start" role="status">
        <Badge tone="danger">Could not read the track list</Badge>
        <Text size="sm" tone="tertiary">
          {state.message}
        </Text>
      </Stack>
    )
  }

  return (
    /*
      tabIndex, so a keyboard can reach the overflow. The rows are text — no
      focusable descendant — and a scroll container without one is a box set's
      track list that only a mouse can read. Dialog.tsx pays for the same lesson
      in its own source; axe calls it `scrollable-region-focusable`.
    */
    // biome-ignore lint/a11y/noNoninteractiveTabindex: a scrollable region with no focusable content must be focusable, or its overflow is unreachable by keyboard
    <div className={styles.scroll} tabIndex={0}>
      <Table density="compact">
        <caption className={styles.caption}>
          <Text size="xs" tone="tertiary">
            {state.data.tracks.length} track{state.data.tracks.length === 1 ? '' : 's'}
          </Text>
        </caption>
        <thead>
          <tr>
            <TableHeaderCell numeric>#</TableHeaderCell>
            <TableHeaderCell>Title</TableHeaderCell>
            <TableHeaderCell numeric>Length</TableHeaderCell>
          </tr>
        </thead>
        <tbody>
          {state.data.tracks.map((track) => (
            <tr key={track.id}>
              <TableCell numeric>
                <Text size="xs" family="mono" tone="tertiary">
                  {state.data.album.discCount > 1
                    ? `${track.discNumber}-${track.trackNumber}`
                    : track.trackNumber}
                </Text>
              </TableCell>
              <TableCell>
                <Stack gap={8} align="center" wrap>
                  <Text size="sm">{track.title}</Text>
                  {track.streamable ? null : (
                    <Badge tone="warning" size="sm">
                      Not offered
                    </Badge>
                  )}
                </Stack>
              </TableCell>
              <TableCell numeric>
                <Text size="xs" family="mono" tone="tertiary">
                  {duration(track.durationSeconds) ?? '—'}
                </Text>
              </TableCell>
            </tr>
          ))}
        </tbody>
      </Table>
    </div>
  )
}

/**
 * What actually happened, per track.
 *
 * Per track rather than a total, because the API deliberately does not fail an
 * album over one track: a compilation with one unlicensed song is still eleven
 * songs worth having, and the only way to know which is which is to say so.
 */
function Result({ result }: { readonly result: AlbumDownload }) {
  /*
    Asked of the whole set, not of each row. AlbumDownload carries no disc
    count, so a per-track `discNumber > 1` prints "1, 2, 3" for disc one and
    "2-1, 2-2" for disc two in the same column — two numbering schemes in one
    table. TrackList gets this right from the album; this is the same question
    asked of what came back.
  */
  const multiDisc = result.tracks.some((track) => track.discNumber > 1)

  return (
    <Stack direction="column" gap={8} align="start">
      <Stack gap={8} align="center" wrap>
        <Badge tone={result.downloaded === 0 ? 'warning' : 'success'}>
          {downloadSummary(result)}
        </Badge>
        <Text size="xs" tone="tertiary" family="mono">
          {result.folder}
        </Text>
      </Stack>

      {/* biome-ignore lint/a11y/noNoninteractiveTabindex: see TrackList — the overflow is unreachable by keyboard without it */}
      <div className={styles.scroll} tabIndex={0}>
        <Table density="compact">
          <thead>
            <tr>
              <TableHeaderCell numeric>#</TableHeaderCell>
              <TableHeaderCell>Title</TableHeaderCell>
              <TableHeaderCell>Outcome</TableHeaderCell>
              <TableHeaderCell>Quality</TableHeaderCell>
              <TableHeaderCell numeric>Size</TableHeaderCell>
            </tr>
          </thead>
          <tbody>
            {result.tracks.map((track) => {
              const outcome = OUTCOMES[track.outcome]

              return (
                <tr key={track.trackId}>
                  <TableCell numeric>
                    <Text size="xs" family="mono" tone="tertiary">
                      {multiDisc ? `${track.discNumber}-${track.trackNumber}` : track.trackNumber}
                    </Text>
                  </TableCell>
                  <TableCell>
                    <Text size="sm">{track.title}</Text>
                  </TableCell>
                  <TableCell>
                    <Stack gap={8} align="center" wrap>
                      <Badge tone={outcome.tone} size="sm">
                        {outcome.label}
                      </Badge>
                      {track.detail != null ? (
                        <Text size="xs" tone="tertiary">
                          {track.detail}
                        </Text>
                      ) : null}
                    </Stack>
                  </TableCell>
                  {/*
                    What was SERVED, not what was asked for. Qobuz give the best
                    encoding a release has up to the request, so a 24/192 ask
                    against a CD master returns 16/44.1 — and the album's
                    advertised ceiling above is the wrong number to show here.
                    Reporting the request as the result is how a library ends up
                    believing it holds hi-res.
                  */}
                  <TableCell>
                    <Text size="xs" family="mono" tone="tertiary">
                      {qualityLabel(track.bitDepth, track.samplingRate) ?? '—'}
                    </Text>
                  </TableCell>
                  <TableCell numeric>
                    <Text size="xs" family="mono" tone="tertiary">
                      {fileSize(track.sizeBytes) ?? '—'}
                    </Text>
                  </TableCell>
                </tr>
              )
            })}
          </tbody>
        </Table>
      </div>
    </Stack>
  )
}

/**
 * What happened to the album this download was meant to replace.
 *
 * Every outcome gets a sentence, including the ones that did nothing, because
 * "downloaded" and "downloaded and replaced" leave the library in states a
 * person has to act on differently — two copies against one — and the difference
 * is invisible from the download summary alone. The API's own `detail` is
 * shown verbatim rather than re-worded here: it is the only place that knows
 * which of the four refusals happened and with what numbers.
 */
function ReplacementResult({ replacement }: { readonly replacement: AlbumReplacement }) {
  const replaced = replacement.archived > 0

  return (
    <Stack direction="column" gap={4} align="start">
      <Badge tone={replaced ? 'success' : 'warning'}>
        {replaced
          ? `Replaced — ${replacement.archived} file${replacement.archived === 1 ? '' : 's'} moved out of the library`
          : 'Nothing was replaced'}
      </Badge>

      {replaced ? (
        <Text size="sm" tone="tertiary">
          The old album is at <code>{replacement.archivedTo}</code>, keeping its folders — move it
          back to undo this. Run a library scan to catalogue what replaced it.
        </Text>
      ) : (
        <Text size="sm" tone="tertiary">
          {replacement.detail}
        </Text>
      )}
    </Stack>
  )
}
