import type { components } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  CatalogueCard,
  CatalogueGrid,
  Disclosure,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Text,
} from '@fonoteca/ui'
import { Link, useParams } from '@tanstack/react-router'
import { api } from '../api.ts'
import { TagWritePanel } from '../components/TagWritePanel.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import styles from './ArtistPage.module.css'
import type { AlbumSectionKey, ArtistAlbum } from './artistAlbums.ts'
import { albumsOf, leaf, sameArtist, sectionsOf } from './artistAlbums.ts'
import { countryName, lifeSpan } from './artistFacts.ts'
import { artistImageUrl, releaseArt } from './coverArt.ts'
import { workGroups } from './workGroups.ts'

type TrackRow = components['schemas']['TrackRow']

/** Roles the API sends, in the order they should read. */
const ROLE_TONE: Readonly<Record<string, 'accent' | 'info' | 'neutral'>> = {
  billed: 'accent',
  conductor: 'info',
  ensemble: 'info',
  composer: 'neutral',
  member: 'info',
}

/**
 * What each grid is a list of, in the words a person would use for it.
 *
 * Four shelves rather than one, because "records by them", "records by a group
 * they were in", "records they played on" and "records of their music that
 * somebody else made" are four different questions and one grid answers none of
 * them: on a composer's page the pieces they wrote and the pieces they
 * conducted run together in year order, and the only thing separating them is a
 * badge on each card.
 *
 * The detail line is doing the work the heading cannot. "Collaborations" is
 * ambiguous on its own — a reader's first guess is a duet, not an orchestra —
 * and "Composer" reads as a job title rather than as a list.
 */
const SECTIONS: Readonly<Record<AlbumSectionKey, { title: string; detail: string }>> = {
  discography: {
    title: 'Discography',
    detail: 'Albums they are billed on.',
  },
  // The heading a band section falls back to when the sleeve named no group.
  // The ordinary one names it — "With Dire Straits" — because "the band" is the
  // one thing a reader of this shelf already knows.
  band: {
    title: 'With the band',
    detail: 'Albums by groups they were a member of.',
  },
  collaborations: {
    title: 'Collaborations',
    detail: "Somebody else's albums that they appear on.",
  },
  composer: {
    title: 'Composer',
    detail: 'Their music, recorded by somebody else.',
  },
}

/**
 * One artist, and everything of theirs the library holds — as a discography.
 *
 * **Albums, not tracks.** The endpoint answers in recordings because that is
 * the shape of the browse rule, but nobody browses a musician by track: a
 * hundred rows of "II. Andante" is a database dump, and the same page folded
 * into twelve covers is a record collection. `albumsOf` does the folding.
 *
 * The tracks are still here, folded into a `Disclosure`. They are
 * the only place the roles read per recording — the same recording appears
 * under four artists on a classical library, and without a word saying why each
 * of them has it the fourth looks like a bug — and for a folder the passes
 * have not placed, this page is the only place its tracks appear at all.
 */
export function ArtistPage() {
  const { artistId } = useParams({ from: '/library/artists/$artistId' })

  const state = useApiQuery(
    () => api.get('/api/catalogue/artists/{id}', { params: { path: { id: artistId } } }),
    [artistId],
  )

  // The default size, not a larger one: measured in Chromium this portrait is
  // 86px — smaller than a tile in the list, which is 112. Asking for 500 here
  // was a guess about the layout that the layout does not share. Undefined where
  // there is neither a photograph nor an album, which is what makes Artwork draw
  // its monogram.
  const portrait = state.status === 'ready' ? artistImageUrl(state.data.artist) : undefined

  // Not memoised: it is a single pass over a list the same request just
  // produced, so the fold costs less than the dependency array that would
  // guard it, and a stale one would be a bug nobody could see.
  const albums = state.status === 'ready' ? albumsOf(state.data.tracks) : []

  // Split for the same reason and at the same cost: one pass over a list this
  // render already holds.
  const sections = state.status === 'ready' ? sectionsOf(albums, state.data.artist.name) : []

  // Only the parts MusicBrainz actually stated. `describedAtUtc` is what tells
  // "no country is recorded for this orchestra" from "nobody has asked yet", and
  // an empty line is the honest rendering of both — so it is not read here, and
  // is on the wire for the screen that wants to offer the ask.
  const facts =
    state.status === 'ready'
      ? [
          countryName(state.data.artist.country),
          lifeSpan(state.data.artist),
          state.data.artist.gender,
        ].filter((fact): fact is string => fact != null)
      : []

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
              A photograph of them where Wikidata holds one, and an album of
              theirs where it does not — `artistImageUrl` decides, so this and
              their card in the list cannot disagree about which. Circular either
              way: the shape is what says this is a person or a group rather than
              a record you own.

              At the same size as the list asks for, because this box is not
              bigger than a tile — it measures 86px against the tile's 112.
            */}
            <Artwork
              name={state.data.artist.name}
              shape="circle"
              size="lg"
              {...(portrait != null ? { src: portrait } : {})}
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
                  {albums.length.toLocaleString()} album{albums.length === 1 ? '' : 's'} ·{' '}
                  {state.data.tracks.length.toLocaleString()} track
                  {state.data.tracks.length === 1 ? '' : 's'} in the library
                </Text>
              </Stack>

              {/*
                Who they are, rather than what of theirs is on the disk — the
                half of an artist a credit line cannot carry, and the whole
                point of the enrichment pass's artist stage.

                Joined into one sentence rather than laid out as a field list,
                because two of the three are usually absent: MusicBrainz holds
                no country for a great many ensembles and no life span for most
                session players, and a labelled grid of empty cells reads as
                broken where a shorter line reads as brief.
              */}
              {facts.length > 0 ? (
                <Text size="sm" tone="secondary">
                  {facts.join(' · ')}
                </Text>
              ) : null}

              {/*
                MusicBrainz's curated genres, most-voted first, capped at three.
                Capped because the tail is where the disagreement lives — an
                artist with nine genres has four that somebody would argue with
                — and because this is a subtitle rather than a taxonomy.
              */}
              {state.data.artist.genres.length > 0 ? (
                <Stack gap={4} wrap>
                  {state.data.artist.genres.slice(0, 3).map((genre) => (
                    <Badge key={genre} tone="info" size="sm">
                      {genre}
                    </Badge>
                  ))}
                </Stack>
              ) : null}
            </Stack>
          </Stack>

          {state.data.tracks.length === 0 ? (
            <Text tone="tertiary">Nothing in the library credits this artist.</Text>
          ) : (
            <>
              {/*
                Every track on this page at once, which is the unit for the case
                the album button cannot serve: a discography enriched in one run,
                scattered across a dozen releases. The scope is the same rule
                this page browses by — billed, conducted, played with, or
                composed — so what gets written is exactly what is listed below.
              */}
              <TagWritePanel
                scope={{ kind: 'artist', id: artistId }}
                label={`${state.data.artist.name}'s tracks`}
              />

              {/*
                One shelf per way of being responsible for a record. `<h2>`
                under the page's one `<h1>`, so a screen reader's outline of
                this page is the split itself — and an artist with only their
                own records still gets the heading, because a condition that
                hides it would make the page's shape depend on the library.
              */}
              {sections.map((section) => {
                // A band section names its group and needs no sentence under
                // it; every other shelf keeps the line saying what it lists.
                // The key alone is no longer unique — one artist can have
                // several bands — so the band joins it.
                const title =
                  section.band == null ? SECTIONS[section.key].title : `With ${section.band}`

                return (
                  <Stack key={`${section.key}:${section.band ?? ''}`} direction="column" gap={4}>
                    <Stack gap={8} align="center">
                      <h2 className={styles.heading}>
                        <Text weight="semibold">{title}</Text>
                      </h2>
                      <Badge tone="info" size="sm">
                        {section.albums.length}
                      </Badge>
                    </Stack>

                    {section.band == null ? (
                      <Text size="sm" tone="secondary" block>
                        {SECTIONS[section.key].detail}
                      </Text>
                    ) : null}

                    <CatalogueGrid aria-label={`${title}: ${state.data.artist.name}`}>
                      {section.albums.map((album) => (
                        <AlbumCard key={album.key} album={album} artist={state.data.artist.name} />
                      ))}
                    </CatalogueGrid>
                  </Stack>
                )
              })}

              {/*
                `Disclosure`, not `<details>`. The design system already
                recorded that decision and its reasons — `<summary>`'s implicit
                role differs between engines and its whole content becomes the
                accessible name — and this page has no story, so the axe run
                that enforces the rest of it would never have seen a hand-rolled
                one.
              */}
              <Disclosure
                className={styles.trackList}
                size="sm"
                summary={
                  <Text size="sm" weight="medium">
                    Track by track
                  </Text>
                }
                aside={
                  <Text size="sm" tone="tertiary" family="mono">
                    {state.data.tracks.length.toLocaleString()}
                  </Text>
                }
              >
                <Tracks name={state.data.artist.name} tracks={state.data.tracks} />
              </Disclosure>
            </>
          )}
        </Stack>
      ) : null}
    </Stack>
  )
}

/**
 * Every recording of theirs, under the works they perform where there are any.
 *
 * **One `<tbody>` per work, which is what the element is for.** A table may
 * hold any number of them and each is a row group with its own heading, so a
 * symphony reads as four movements under one line rather than as four rows each
 * repeating the same forty characters of work title.
 *
 * The API orders these by work title falling back to track title, so a work's
 * movements arrive consecutively — see the note on that `OrderBy`. That
 * ordering is also what makes the identity check in `workGroups` load-bearing
 * here rather than theoretical: it brings every work of the same *name*
 * together, and this library holds four distinct pieces called "Main Theme".
 *
 * Grouping is not a classical-only outcome, whatever the shape it was built
 * for: an artist's several recordings of one song gather under it too, which is
 * the same claim honestly made.
 */
function Tracks({ name, tracks }: { readonly name: string; readonly tracks: readonly TrackRow[] }) {
  const groups = workGroups(tracks)

  // Per group rather than per table: a run this fold demoted carries no heading,
  // so its rows are the only place their work is named.
  const rows = (group: readonly TrackRow[], headed: boolean, strip: string) =>
    group.map((track) => (
      <Row key={track.recordingId} track={track} grouped={headed} strip={strip} />
    ))

  return (
    <div className={styles.tracks}>
      <Table density="cozy">
        <caption className={styles.caption}>
          Tracks credited to {name}
          {groups === null ? '' : ', grouped by the work they perform'}
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

        {groups === null ? (
          <tbody>{rows(tracks, false, '')}</tbody>
        ) : (
          groups.map((group) => (
            <tbody key={group.key}>
              {group.workTitle !== null ? (
                <tr>
                  {/*
                    `scope="rowgroup"`: the heading names the remaining cells of
                    the row group it opens, which is what the standard defines
                    that value as. `colgroup` would claim it labels a column
                    group instead.
                  */}
                  <th className={styles.work} colSpan={5} scope="rowgroup">
                    <Text size="sm" weight="medium">
                      {group.workTitle}
                    </Text>
                  </th>
                </tr>
              ) : null}

              {rows(group.tracks, group.workTitle !== null, group.prefix)}
            </tbody>
          ))
        )}
      </Table>
    </div>
  )
}

/**
 * One album of theirs, or one folder standing in for one.
 *
 * **Both kinds are on the same grid and only one of them is a catalogue fact.**
 * A release the attribution pass decided links to its page and carries its
 * cover; a folder nothing has placed yet carries a badge saying so, in words
 * rather than in a shade of grey, and goes nowhere — there is no page for a
 * directory, and inventing one would be inventing the album.
 *
 * The folder is printed either way. On a decided album it is the second
 * opinion the albums screen makes a whole card out of: where the directory
 * disagrees with what the audio said, this is where a person sees it. On a
 * folder-derived one it is the only claim there is, so the year beside it is
 * marked as read off the directory rather than known.
 */
function AlbumCard({ album, artist }: { readonly album: ArtistAlbum; readonly artist: string }) {
  // Pulled out so the narrowing survives into the render callback below; the
  // property access on its own does not.
  const { releaseId } = album
  const folders = album.folders.join('\n')

  const meta = (
    <>
      {album.releaseId == null ? (
        <Badge tone="warning" size="sm">
          folder
        </Badge>
      ) : null}

      {/*
        The hole in a part-placed album. Only ever on a decided one — on a
        folder card every track is unplaced, which the badge beside it already
        says in the one word that matters.
      */}
      {album.releaseId != null && album.unplaced > 0 ? (
        <Badge tone="warning" size="sm" mono>
          {album.unplaced} unplaced
        </Badge>
      ) : null}

      {album.roles.map((role) => (
        <Badge key={role} tone={ROLE_TONE[role] ?? 'neutral'} size="sm">
          {role}
        </Badge>
      ))}

      {/*
        More files than tracks is the same recording held in several encodings,
        which is the dedupe question the whole schema exists to be able to ask.
      */}
      {album.fileCount > album.trackCount ? (
        <Badge tone="neutral" size="sm" mono>
          {album.fileCount} files
        </Badge>
      ) : null}
    </>
  )

  const subtitle = (
    <span title={folders}>
      {[
        album.year == null ? null : `${album.year}${album.yearFromFolder ? '?' : ''}`,
        // Whose record it is, printed only when it is not this artist's —
        // asking `sameArtist`, the same test the shelf is chosen by, so the
        // line and the placement cannot disagree. It is what makes a card on
        // the Collaborations shelf explain itself ("one track on a Joe
        // Bonamassa album" rather than an album of theirs that has mysteriously
        // moved), and it stays quiet where the name would only repeat the one
        // at the top of the page — including on the Composer shelf, where a
        // musician's own band is still not somebody else.
        sameArtist(album, artist) ? null : album.albumArtist,
        `${album.trackCount} track${album.trackCount === 1 ? '' : 's'}`,
        leaf(album.folders[0] ?? ''),
        album.folders.length > 1 ? `+${album.folders.length - 1}` : null,
      ]
        .filter(Boolean)
        .join(' · ')}
    </span>
  )

  return (
    <CatalogueCard
      variant="album"
      title={album.title}
      subtitle={subtitle}
      meta={meta}
      {...(album.mbid != null ? { image: releaseArt(album.mbid) } : {})}
      {...(releaseId != null
        ? {
            render: (props) => (
              <Link {...props} to="/library/releases/$releaseId" params={{ releaseId }} />
            ),
          }
        : {})}
    />
  )
}

function Row({
  track,
  grouped,
  strip,
}: {
  readonly track: TrackRow
  readonly grouped: boolean
  /** The run-in the group heading has already said. See `workGroups`. */
  readonly strip: string
}) {
  return (
    <tr>
      {/* The whole title stays in the tooltip; see the album page's note. */}
      <TableCell truncate title={track.title}>
        <Stack direction="column" gap={2}>
          <Text truncate>{track.title.slice(strip.length)}</Text>
          {/*
            The work, when there is one and the table is not already grouped by
            it. It is what the movement belongs to, and on a classical library
            the track title alone ("II. Andante") says almost nothing without it
            — but under a heading that has just said it, repeating it on every
            row is the noise the grouping exists to remove.
          */}
          {!grouped && track.workTitle != null && track.workTitle !== track.title ? (
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
