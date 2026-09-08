import type { components } from '@fonoteca/api-client'
import type { PlaybackTrack } from '@fonoteca/ui'
import { Artwork, Badge, formatTime, PlayButton, Stack, Text } from '@fonoteca/ui'

import { api, apiBaseUrl } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './FilePreviewPane.module.css'
import { artUrl, contentUrl, readTextHead } from './files.ts'
import { fileSize } from './qobuz.ts'

type FolderEntry = components['schemas']['FolderEntry']

/**
 * What the selected entry actually is.
 *
 * <b>The difference between a table of names and a file browser.</b> The listing
 * can say `cover.jpg, 553 kB`; only this can say whether it is the album's front
 * or a picture of the band, and only this can play the track rather than
 * describing it.
 *
 * Each kind costs what it needs and nothing more. An image and a text file are
 * already fully described by the row — the bytes are the preview — so they cost
 * one request each, made by the browser. Audio costs a `detail` call because the
 * useful part is what a decoder and a tag reader make of it, which is a
 * subprocess on the server; that is a price worth paying on a click and not one
 * worth paying for every row of a listing.
 */
export function FilePreviewPane({
  entry,
  onOpen,
}: {
  entry: FolderEntry | null
  onOpen: (path: string) => void
}) {
  if (entry === null) {
    return (
      <aside className={styles.pane} aria-label="Preview">
        <Text size="sm" tone="tertiary">
          Select something to see what it is.
        </Text>
      </aside>
    )
  }

  return (
    <aside className={styles.pane} aria-label="Preview">
      <Stack direction="column" gap={12}>
        <Stack direction="column" gap={4}>
          <Text weight="semibold" block>
            {entry.name}
          </Text>
          <span className={styles.path} title={entry.path}>
            <Text size="xs" tone="tertiary" family="mono" block>
              {entry.path}
            </Text>
          </span>
        </Stack>

        {entry.isDirectory ? (
          <FolderPreview entry={entry} onOpen={onOpen} />
        ) : (
          <FilePreview entry={entry} />
        )}

        <Stack direction="column" gap={4}>
          <Text size="xs" tone="tertiary">
            {entry.isDirectory ? 'Catalogued audio' : 'Size'} · {fileSize(entry.sizeBytes) ?? '—'}
          </Text>
          <Text size="xs" tone="tertiary">
            Modified · {new Date(entry.modifiedUtc).toLocaleString()}
          </Text>
        </Stack>
      </Stack>
    </aside>
  )
}

/**
 * A folder, shown as its own cover.
 *
 * The picture is whatever `cover.jpg` or `folder.jpg` it holds — the same file
 * the listing shows as a row, which is the point: on this screen an album folder
 * looks like an album.
 */
function FolderPreview({ entry, onOpen }: { entry: FolderEntry; onOpen: (path: string) => void }) {
  return (
    <Stack direction="column" gap={12}>
      <Artwork name={entry.name} src={artUrl(apiBaseUrl, entry.path)} size="fill" />

      <Stack direction="column" gap={4}>
        <Text size="sm" tone="secondary">
          {entry.cataloguedFiles === 0
            ? 'Nothing here has been scanned.'
            : `${entry.cataloguedFiles} catalogued, ${entry.identified} identified, ${entry.attributed} filed.`}
        </Text>
      </Stack>

      <button type="button" className={styles.open} onClick={() => onOpen(entry.path)}>
        Open folder
      </button>
    </Stack>
  )
}

function FilePreview({ entry }: { entry: FolderEntry }) {
  if (entry.isAudio) return <AudioPreview entry={entry} />
  if (entry.kind === 'Image') return <ImagePreview entry={entry} />
  if (entry.kind === 'Text') return <TextPreview entry={entry} />

  return (
    <Stack direction="column" gap={8} align="start">
      <Badge tone="neutral">No preview</Badge>
      <Text size="sm" tone="secondary" block>
        Nothing here can show this safely, so it is offered as a download instead.
      </Text>
      <a className={styles.download} href={contentUrl(apiBaseUrl, entry.path)}>
        Download {entry.name}
      </a>
    </Stack>
  )
}

/** The image itself, at whatever size the pane is. */
function ImagePreview({ entry }: { entry: FolderEntry }) {
  return (
    <img
      className={styles.image}
      src={contentUrl(apiBaseUrl, entry.path)}
      // The filename is the only description available and it is usually the
      // whole content — "cover.jpg", "back.png". Repeating it as alt text would
      // read it twice to a screen reader, since it is already the heading above.
      alt=""
    />
  )
}

/**
 * The head of the file, fetched as a range.
 *
 * Rip logs and cue sheets are what this is for, and they are what tells two
 * copies of an album apart when everything else about them matches.
 */
function TextPreview({ entry }: { entry: FolderEntry }) {
  const state = useApiQuery(() => readTextHead(apiBaseUrl, entry.path), [entry.path])

  if (state.status === 'loading')
    return (
      <Text size="sm" tone="tertiary">
        Reading…
      </Text>
    )

  if (state.status === 'error') {
    return (
      <Stack direction="column" gap={4} align="start">
        <Badge tone="warning">Could not read it</Badge>
        <Text size="xs" tone="tertiary">
          {state.message}
        </Text>
      </Stack>
    )
  }

  return <pre className={styles.text}>{state.data}</pre>
}

/**
 * The track handed to the player.
 *
 * Built by spreading rather than by assigning `undefined`, because
 * `exactOptionalPropertyTypes` is on: an absent optional and one explicitly set
 * to `undefined` are different types here, and only the first is what "we do not
 * know the duration yet" means.
 *
 * Everything except the id and the source is optional on purpose — the button
 * has to work on the first render, before `detail` has come back, and a file
 * whose tags will not parse still plays.
 */
function trackFor(
  entry: FolderEntry,
  detail: components['schemas']['FilePreviewResponse'] | null,
): PlaybackTrack {
  const artist = detail?.tags.find((tag) => tag.name === 'ARTIST')?.value

  return {
    id: entry.path,
    src: contentUrl(apiBaseUrl, entry.path),
    title: entry.name,
    ...(artist ? { subtitle: artist } : {}),
    ...(detail?.durationSeconds != null ? { duration: detail.durationSeconds } : {}),
    // The only way to tell a missing file from an undecodable one: MediaError
    // reports both as SRC_NOT_SUPPORTED.
    ...(detail?.mediaType ? { mimeType: detail.mediaType } : {}),
  }
}

/**
 * An audio file: its cover, a play button, what the decoder measured, and what
 * the file claims about itself.
 *
 * <b>This is the first thing in the application that plays a note.</b>
 * `PlaybackProvider` has been mounted at the root and `AudioTransport` has sat
 * in the shell since they were built, rendering nothing, because no screen ever
 * handed them a track. The transport appears at the foot of the window the
 * moment this button is pressed.
 */
function AudioPreview({ entry }: { entry: FolderEntry }) {
  const state = useApiQuery(
    () => api.get('/api/files/detail', { params: { query: { path: entry.path } } }),
    [entry.path],
  )

  const detail = state.status === 'ready' ? state.data : null

  return (
    <Stack direction="column" gap={12}>
      <Artwork name={entry.name} src={artUrl(apiBaseUrl, entry.path)} size="fill" />

      {/*
        Only for what a browser decodes. Monkey's Audio and WavPack are audio to
        every pass in this application and to no browser, so the server files
        them as unplayable and no control is offered — a play button that cannot
        work is worse than none.
      */}
      {entry.kind === 'Audio' ? (
        <Stack gap={8} align="center">
          <PlayButton track={trackFor(entry, detail)} />
          <Text size="sm" tone="secondary">
            {detail?.durationSeconds == null ? 'Play' : formatTime(detail.durationSeconds)}
          </Text>
        </Stack>
      ) : (
        <Badge tone="neutral">No browser plays this format</Badge>
      )}

      {state.status === 'loading' ? (
        <Text size="sm" tone="tertiary">
          Reading the file…
        </Text>
      ) : null}

      {detail?.audio ? (
        <Stack gap={4} wrap>
          <Badge tone={detail.audio.lossless ? 'success' : 'neutral'}>{detail.audio.codec}</Badge>
          <Badge tone="neutral">{detail.audio.bitrateKbps} kbps</Badge>
          <Badge tone="neutral">
            {detail.audio.bitDepth ? `${detail.audio.bitDepth}/` : ''}
            {(detail.audio.sampleRateHz / 1000).toFixed(1)} kHz
          </Badge>
          {detail.decodedCleanly === false ? (
            <Badge tone="warning">The decoder objected</Badge>
          ) : null}
        </Stack>
      ) : null}

      {detail?.note ? (
        <Text size="xs" tone="warning" block>
          {detail.note}
        </Text>
      ) : null}

      {detail && detail.tags.length > 0 ? (
        <dl className={styles.tags}>
          {detail.tags.slice(0, 12).map((tag) => (
            <div key={tag.name} className={styles.tag}>
              <dt>
                <Text size="2xs" tone="tertiary">
                  {tag.name}
                </Text>
              </dt>
              <dd>
                <Text size="xs" truncate>
                  {tag.value}
                </Text>
              </dd>
            </div>
          ))}
        </dl>
      ) : null}
    </Stack>
  )
}
