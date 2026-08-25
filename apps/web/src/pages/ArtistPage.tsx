import type { components } from '@fonoteca/api-client'
import { Artwork, Badge, Stack, Table, TableCell, TableHeaderCell, Text } from '@fonoteca/ui'
import { Link, useParams } from '@tanstack/react-router'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './ArtistPage.module.css'
import { releaseArt } from './coverArt.ts'

type TrackRow = components['schemas']['TrackRow']

/** Roles the API sends, in the order they should read. */
const ROLE_TONE: Readonly<Record<string, 'accent' | 'info' | 'neutral'>> = {
  billed: 'accent',
  conductor: 'info',
  ensemble: 'info',
  composer: 'neutral',
}

/**
 * One artist, and everything of theirs the library holds.
 *
 * The roles column is what makes the page make sense on a classical library.
 * The same recording appears under four artists, and without a word saying why
 * each of them has it — billed, conductor, ensemble, composer — the fourth
 * looks like a bug.
 */
export function ArtistPage() {
  const { artistId } = useParams({ from: '/library/artists/$artistId' })

  const state = useApiQuery(
    () => api.get('/api/catalogue/artists/{id}', { params: { path: { id: artistId } } }),
    [artistId],
  )

  return (
    <Stack direction="column" gap={20}>
      <Link to="/library" className={styles.back}>
        <Text size="sm">← All artists</Text>
      </Link>

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? <Text tone="tertiary">Reading the catalogue…</Text> : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read this artist</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}
      </div>

      {state.status === 'ready' ? (
        <Stack direction="column" gap={20}>
          <Stack gap={16} align="center">
            {/*
              An album of theirs in place of a portrait, circular like every
              other artist in the application. There is no photograph to be had:
              MusicBrainz holds none, and the Cover Art Archive is keyed on
              releases. The circle is what says this is a person or a group
              rather than a record you own — the same shape their card carries in
              the list, drawn from the same field, so the two agree.
            */}
            <Artwork
              name={state.data.artist.name}
              shape="circle"
              size="lg"
              {...(state.data.artist.cover != null
                ? { src: releaseArt(state.data.artist.cover) }
                : {})}
            />

            <Stack direction="column" gap={4}>
              <h1 className={styles.title}>
                <Text size="xl" weight="semibold" block>
                  {state.data.artist.name}
                </Text>
              </h1>

              <Stack gap={8} align="center" wrap>
                {state.data.artist.type != null ? (
                  <Badge tone="neutral" size="sm">
                    {state.data.artist.type}
                  </Badge>
                ) : null}
                {state.data.artist.disambiguation != null ? (
                  <Text size="sm" tone="tertiary">
                    {state.data.artist.disambiguation}
                  </Text>
                ) : null}
                <Text size="sm" tone="tertiary">
                  {state.data.tracks.length.toLocaleString()} track
                  {state.data.tracks.length === 1 ? '' : 's'} in the library
                </Text>
              </Stack>
            </Stack>
          </Stack>

          {state.data.tracks.length === 0 ? (
            <Text tone="tertiary">Nothing in the library credits this artist.</Text>
          ) : (
            <div className={styles.tracks}>
              <Table density="cozy">
                <caption className={styles.caption}>
                  Tracks credited to {state.data.artist.name}
                </caption>
                <thead>
                  <tr>
                    <TableHeaderCell className={styles.trackCol}>Track</TableHeaderCell>
                    <TableHeaderCell>Credited as</TableHeaderCell>
                    <TableHeaderCell className={styles.folderCol}>Album</TableHeaderCell>
                    <TableHeaderCell numeric>Length</TableHeaderCell>
                    <TableHeaderCell numeric>Files</TableHeaderCell>
                  </tr>
                </thead>
                <tbody>
                  {state.data.tracks.map((track) => (
                    <Row key={track.recordingId} track={track} />
                  ))}
                </tbody>
              </Table>
            </div>
          )}
        </Stack>
      ) : null}
    </Stack>
  )
}

/** The album-shaped end of a library-relative directory. */
function leaf(folder: string): string {
  const cut = folder.lastIndexOf('/')
  return cut < 0 ? folder : folder.slice(cut + 1)
}

function Row({ track }: { readonly track: TrackRow }) {
  return (
    <tr>
      <TableCell truncate>
        <Stack direction="column" gap={2}>
          <Text truncate>{track.title}</Text>
          {/*
            The work, when there is one. It is what the movement belongs to, and
            on a classical library the track title alone ("II. Andante") says
            almost nothing without it.
          */}
          {track.workTitle != null && track.workTitle !== track.title ? (
            <Text size="xs" tone="tertiary" truncate>
              {track.workTitle}
            </Text>
          ) : null}
        </Stack>
      </TableCell>

      <TableCell>
        <Stack gap={4} wrap>
          {track.roles.map((role) => (
            <Badge key={role} tone={ROLE_TONE[role] ?? 'neutral'} size="sm">
              {role}
            </Badge>
          ))}
        </Stack>
      </TableCell>

      {/*
        The album attribution decided on — and where it declined, the directory,
        shown as the directory rather than dressed up as an album. That
        distinction is the whole reason the API returns both: a folder name is
        the filesystem's claim, and presenting one as a catalogue answer is the
        kind of quiet lie that survives into every screen built on top of it.

        Only the last segment of a path is shown. An ellipsis can only eat the
        *end* of a string, and these paths are "Artist/Album", so the full value
        truncates every row of a soundtrack composer's page to the same forty
        characters of orchestra name. The whole path stays in the title.
      */}
      {track.album != null ? (
        <TableCell truncate title={track.album.title}>
          <Link
            to="/library/releases/$releaseId"
            params={{ releaseId: track.album.releaseId }}
            className={styles.album}
          >
            {/*
              The tone goes on the Text, not on the anchor. Text sets its own
              colour, so a colour on the link is overridden by the span inside it
              and the album reads as plain text while the folder fallback beside
              it looks like the link.
            */}
            <Text size="sm" tone="accent" truncate>
              {track.album.title}
            </Text>
          </Link>
        </TableCell>
      ) : (
        <TableCell truncate title={`No album attributed. Folder: ${track.folder}`}>
          {/*
            "(folder)" in visible words rather than a tone or an aria-label. The
            distinction it carries — a directory name standing in for an album
            nobody worked out — has to reach every reader, and a greyer grey
            reaches none of them.
          */}
          <Text size="sm" tone="tertiary" truncate>
            {leaf(track.folder)} (folder)
          </Text>
        </TableCell>
      )}

      <TableCell numeric>
        <Text size="sm" family="mono" tone={track.duration == null ? 'tertiary' : 'primary'}>
          {track.duration ?? '—'}
        </Text>
      </TableCell>

      {/*
        More than one file is the same recording in several encodings, which is
        the dedupe question this whole schema exists to be able to ask. The count
        is the smallest honest way to show it before there is a screen for it.
      */}
      <TableCell numeric>
        <Text size="sm" family="mono" tone={track.files.length > 1 ? 'warning' : 'tertiary'}>
          {track.files.length}
        </Text>
      </TableCell>
    </tr>
  )
}
