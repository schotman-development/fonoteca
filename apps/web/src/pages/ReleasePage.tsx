import { type components, describeError } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  type BadgeTone,
  Button,
  CatalogueCard,
  CatalogueGrid,
  Disclosure,
  Field,
  Input,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Tabs,
  Text,
  usePlayback,
} from '@fonoteca/ui'
import { Link, useNavigate, useParams } from '@tanstack/react-router'
import { type FormEvent, useCallback, useEffect, useId, useState } from 'react'
import { flushSync } from 'react-dom'
import { api, apiBaseUrl } from '../api.ts'
import { TagWritePanel } from '../components/TagWritePanel.tsx'
import { UndoFolderButton } from '../components/UndoFolderButton.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import { countryName } from './artistFacts.ts'
import { CoverDialog } from './CoverDialog.tsx'
import { CERTAINTY, FOLDER_ORDER } from './certainty.ts'
import { artistImageUrl, releaseCover, releaseGroupArt } from './coverArt.ts'
import { contentUrl, parentOf } from './files.ts'
import styles from './Profile.module.css'
import { blank, Edited, list, Prose, stamp, Value, year } from './profile.tsx'
import { ALBUM_FOLDER_DEPTH, albumFolderOf } from './seating.ts'
import { undoOutcome } from './tagUndo.ts'
import { workGroups } from './workGroups.ts'

type Detail = components['schemas']['AlbumDetailResponse']
type AlbumTrackRow = components['schemas']['AlbumTrackRow']
type AlbumEditionRow = components['schemas']['AlbumEditionRow']
type AlbumFileRow = components['schemas']['AlbumFileRow']
type FileRow = components['schemas']['FileRow']
type FileQuality = components['schemas']['FileQuality']
type AlbumCredit = components['schemas']['AlbumCredit']
type SearchRow = components['schemas']['ReleaseSearchRow']
type EditRequest = components['schemas']['ReleaseEditRequest']

const TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'credits', label: 'Credits' },
] as const

type TabKey = (typeof TABS)[number]['key']

/**
 * The groups the credits come in. There is no Production group: the catalogue
 * stores no producers or engineers — see `CatalogueEndpoints.AlbumCredits`.
 */
const CREDIT_GROUPS = ['Main', 'Performers', 'Composition'] as const

const COVER_SOURCE: Readonly<Record<string, string>> = {
  archive: 'Cover Art Archive, this pressing',
  qobuz: 'Qobuz, matched by barcode or title',
  upload: 'Uploaded by you',
}

/**
 * One album, and everything on it — including what the library does not have.
 *
 * Laid out as Roon lays one out: the sleeve blurred behind the sleeve, then an
 * overview (the review, the track list, the files, more by the same artist and
 * everything the catalogue holds about this edition) and the credits, one card
 * per artist, each opening the tracks they are on.
 *
 * The missing tracks are the reason this page stores the whole track list rather
 * than only the part that was matched. "Eleven of twelve" is a number; a greyed
 * row where track 7 should be is an answer.
 *
 * An album is a release group. Where the files are known to be one pressing,
 * that pressing's facts are shown; where they are not, the tracks are every
 * stored edition's at once and nothing only a pressing has is claimed.
 */
export function ReleasePage() {
  const { albumId: releaseId } = useParams({ from: '/library/albums/$albumId' })
  const [version, setVersion] = useState(0)
  // Here rather than in AskAgain: the refetch after a reopen unmounts it.
  const [reopened, setReopened] = useState<(Reopened & { readonly release: string }) | null>(null)

  // Stable, because the panels below hold it in an effect's dependency array: a
  // fresh arrow every render re-runs the effect that called it, which is a
  // refetch loop rather than a refetch.
  const changed = useCallback(() => {
    setVersion((done) => done + 1)
  }, [])

  const state = useApiQuery(
    () => api.get('/api/catalogue/albums/{id}', { params: { path: { id: releaseId } } }),
    [releaseId, version],
  )

  // What was on screen stays there while the page reads a change back, so the
  // tab and the open credit survive a save.
  const [held, setHeld] = useState<{ readonly id: string; readonly data: Detail } | null>(null)

  useEffect(() => {
    if (state.status === 'ready') setHeld({ id: releaseId, data: state.data })
  }, [state, releaseId])

  const data = state.status === 'ready' ? state.data : held?.id === releaseId ? held.data : null

  return (
    <Stack direction="column" gap={20}>
      {/* The list's order and filter, carried back. See the artist page. */}
      <Link
        to="/library/albums"
        from="/library/albums/$albumId"
        search={(prev) => prev}
        className={styles.back}
      >
        <Text size="sm">← All albums</Text>
      </Link>

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' && data == null ? (
          <Text tone="tertiary">Reading the catalogue…</Text>
        ) : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read this album</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}
      </div>

      {reopened !== null && reopened.release === releaseId ? (
        <div role="status">
          <Text size="sm" tone={reopened.failed ? 'danger' : 'secondary'} block>
            {reopened.message}
          </Text>
          <Stack gap={8} wrap>
            {reopened.folders.map((folder) => (
              <Link key={folder} to="/files" search={{ path: folder }}>
                <Text size="sm">{folder}</Text>
              </Link>
            ))}
          </Stack>
        </div>
      ) : null}

      {data != null ? (
        <Album
          key={releaseId}
          releaseId={releaseId}
          data={data}
          onChanged={changed}
          onReopened={(result) => {
            setReopened({ ...result, release: releaseId })
            changed()
          }}
        />
      ) : null}
    </Stack>
  )
}

function Album({
  releaseId,
  data,
  onChanged,
  onReopened,
}: {
  readonly releaseId: string
  readonly data: Detail
  readonly onChanged: () => void
  readonly onReopened: (result: Reopened) => void
}) {
  const { album: release, about, tracks, credits, moreBy, unplaced, folders: onDisk } = data
  const [tab, setTab] = useState<TabKey>('overview')
  const [editing, setEditing] = useState(false)
  const [reviewOpen, setReviewOpen] = useState(false)
  const [picked, setPicked] = useState<string | null>(null)
  const [choosing, setChoosing] = useState(false)
  // Bumped after a change so the header asks for the new picture at once.
  const [coverVersion, setCoverVersion] = useState(0)
  const playback = usePlayback()
  const navigate = useNavigate()
  const id = useId()

  const edited = (field: string) => about.edited.includes(field)
  // Asked is the release lookup: a release row exists only because a pass or a
  // person looked it up, so its blanks are MusicBrainz's.
  const asked = true
  const discs = release.discCount ?? Math.max(1, ...tracks.map((track) => track.discNumber))
  const files = [...tracks.flatMap((track) => track.files), ...unplaced.map((row) => row.file)]
  const held = tracks.filter((track) => track.held).length
  const qualities = [
    ...new Map(
      files.flatMap((file) =>
        file.quality == null ? [] : [[qualityLabel(file.quality), file.quality] as const],
      ),
    ).entries(),
  ]
  const certainty = CERTAINTY[release.certainty]
  const released = dateLabel(about.releasedYear, about.releasedMonth, about.releasedDay)
  const facts = [
    released ?? release.year?.toString(),
    about.label,
    countryName(release.country),
    release.formats,
    about.disambiguation,
  ].filter((fact): fact is string => fact != null)
  // The stored sleeve of the edition that stands for the album, and the one a
  // person changes; without one stored, the archive's picture of the album.
  const cover =
    release.coverReleaseId != null
      ? releaseCover(release.coverReleaseId, coverVersion)
      : release.mbid != null
        ? releaseGroupArt(release.mbid)
        : undefined
  // Owned is any file filed under it. Wanting a record already held means
  // nothing, so an owned album's slot for it writes its tags instead.
  const owned = release.files > 0
  const first = tracks.flatMap((track) => track.files.map((file) => ({ track, file })))[0]

  return (
    <article className={styles.page} aria-labelledby={`${id}title`}>
      <header>
        {/* The lead artist's banner; without one, a plain grey band. */}
        <div className={styles.banner}>
          {about.artistBanner != null ? <img src={about.artistBanner} alt="" /> : null}
        </div>

        <div className={styles.identity}>
          <div className={styles.cover}>
            <Artwork name={release.title} size="fill" {...(cover != null ? { src: cover } : {})} />
          </div>

          <div className={styles.names}>
            <h1 id={`${id}title`} className={styles.title}>
              {release.title}
            </h1>
            {release.artist != null ? (
              about.artistId != null ? (
                <Link
                  to="/library/artists/$artistId"
                  params={{ artistId: about.artistId }}
                  className={styles.album}
                >
                  <Text size="lg" tone="secondary">
                    {release.artist}
                  </Text>
                </Link>
              ) : (
                <Text size="lg" tone="secondary">
                  {release.artist}
                </Text>
              )
            ) : null}
            <Text size="sm" tone="secondary">
              {facts.join(' · ')}
            </Text>

            <Stack gap={8} align="center" wrap>
              {[about.primaryType, ...about.secondaryTypes]
                .filter((type): type is string => type != null)
                .map((type) => (
                  <Badge key={type} tone="neutral" size="sm">
                    {type}
                  </Badge>
                ))}
              {/*
                Status only when it is not the ordinary one. A "Official" badge
                on almost every album teaches the eye to ignore the badge, and
                then the bootleg goes unnoticed too.
              */}
              {release.status != null && release.status !== 'Official' ? (
                <Badge tone="warning" size="sm">
                  {release.status}
                </Badge>
              ) : null}
              {qualities.map(([label, quality]) => (
                <Badge key={label} tone={qualityTone(quality)} size="sm" mono>
                  {label}
                </Badge>
              ))}
              {certainty !== undefined && certainty.tone !== 'ok' ? (
                <Badge tone={certainty.tone === 'warning' ? 'warning' : 'neutral'} size="sm">
                  {certainty.label}
                </Badge>
              ) : null}
            </Stack>
          </div>

          <Stack className={styles.actions} gap={8} wrap align="start">
            <Button
              variant="primary"
              disabled={first === undefined}
              onClick={() => {
                if (first === undefined) return
                playback.play({
                  id: `${release.id}:${first.track.discNumber}:${first.track.position}:${first.track.recordingId}`,
                  src: contentUrl(apiBaseUrl, first.file.path),
                  title: first.track.title,
                  subtitle: release.title,
                })
              }}
            >
              Play
            </Button>
            {owned ? (
              <TagWritePanel
                scope={{ kind: 'album', id: releaseId }}
                label="this album"
                onWritten={onChanged}
              />
            ) : about.groupId != null ? (
              <WantButton groupId={about.groupId} monitored={about.monitored} />
            ) : null}
            {/*
              The corrections live on the display edition's row, which is what
              every page reads the album from. A pressing's own facts are
              corrected only where the files are proven to be it.
            */}
            <Button
              variant="secondary"
              aria-pressed={editing}
              disabled={release.coverReleaseId == null}
              onClick={() => {
                setEditing(!editing)
                flushSync(() => setTab('overview'))
                document.getElementById(`${id}about`)?.focus()
              }}
            >
              Edit
            </Button>
          </Stack>
        </div>
      </header>

      {choosing && release.coverReleaseId != null ? (
        <CoverDialog
          releaseId={release.coverReleaseId}
          title={release.title}
          onClose={() => setChoosing(false)}
          onChanged={() => {
            setCoverVersion((current) => current + 1)
            onChanged()
          }}
        />
      ) : null}

      <Tabs
        tabs={TABS}
        label={release.title}
        selected={tab}
        onSelect={setTab}
        panel={(key) =>
          key === 'overview' ? (
            <div className={styles.columns}>
              <div className={styles.main}>
                <section aria-labelledby={`${id}review`}>
                  <Stack direction="column" gap={8}>
                    <h2 id={`${id}review`} className={styles.heading} tabIndex={-1}>
                      Review
                    </h2>
                    <Prose
                      written={about.review}
                      asked={about.reviewLookupUtc != null || edited('review')}
                      none="No article found for this album."
                      {...(reviewOpen
                        ? {}
                        : {
                            onMore: () => {
                              // The button goes with the excerpt; focus follows the reader.
                              flushSync(() => setReviewOpen(true))
                              document.getElementById(`${id}review`)?.focus()
                            },
                          })}
                    />
                  </Stack>
                </section>

                <section aria-labelledby={`${id}tracks`}>
                  <Stack direction="column" gap={8}>
                    <h2 id={`${id}tracks`} className={styles.heading}>
                      Tracks
                    </h2>
                    <Tracks tracks={tracks} discs={discs} editions={data.editions} />
                  </Stack>
                </section>

                {unplaced.length > 0 ? (
                  <section aria-labelledby={`${id}unplaced`}>
                    <Stack direction="column" gap={8}>
                      <h2 id={`${id}unplaced`} className={styles.heading}>
                        On no edition
                      </h2>
                      <Text size="sm" tone="secondary">
                        Held to this album, but no edition of it stored here prints the recording.
                      </Text>
                      <Files files={unplaced.map((row) => row.file)} />
                    </Stack>
                  </section>
                ) : null}

                {/*
                  One per folder, each in its own order: two rips of one album are
                  two folders, and the pressing (or its absence) is a fact about
                  each of them rather than about the album.
                */}
                {onDisk.map((folder) => (
                  <Disclosure
                    key={folder.path}
                    size="sm"
                    summary={
                      <Text size="sm" weight="medium">
                        {folder.path}
                      </Text>
                    }
                    aside={
                      <Text size="sm" tone="tertiary">
                        {[
                          folder.edition ?? 'pressing not known',
                          FOLDER_ORDER[folder.order],
                          folder.files.length,
                        ]
                          .filter((part) => part !== undefined)
                          .join(' · ')}
                      </Text>
                    }
                  >
                    <Stack direction="column" gap={12}>
                      <FolderFiles files={folder.files} />
                      {/*
                        Undoing a tag write reopens the folder, which takes it off
                        this album: where it went is on the Files screen.
                      */}
                      <UndoFolderButton
                        folder={folder.path}
                        onUndone={(result) => {
                          // Said before the page moves on, since neither the
                          // reload nor the Files screen can say it afterwards.
                          if (result.problems.length > 0) window.alert(undoOutcome(result))

                          if (result.edit?.kind === 'TagWrite') {
                            void navigate({ to: '/files', search: { path: result.folder } })
                          } else onChanged()
                        }}
                      />
                      <NotThisAlbum
                        folder={folder.path}
                        albumId={releaseId}
                        onChanged={onChanged}
                      />
                    </Stack>
                  </Disclosure>
                ))}

                {moreBy.length > 0 ? (
                  <section className={styles.shelf} aria-labelledby={`${id}more`}>
                    <h2 id={`${id}more`} className={styles.heading}>
                      More by{' '}
                      {credits.find((credit) => credit.group === 'Main')?.name ?? release.artist}
                    </h2>
                    <CatalogueGrid
                      layout="shelf"
                      aria-label={`More by ${release.artist ?? release.title}`}
                    >
                      {moreBy.map((other) => (
                        <CatalogueCard
                          key={other.id}
                          variant="album"
                          title={other.title}
                          subtitle={other.year?.toString() ?? 'undated'}
                          {...(other.coverReleaseId != null
                            ? { image: releaseCover(other.coverReleaseId) }
                            : other.mbid != null
                              ? { image: releaseGroupArt(other.mbid) }
                              : {})}
                          render={(props) => (
                            <Link
                              {...props}
                              to="/library/albums/$albumId"
                              params={{ albumId: other.id }}
                            />
                          )}
                        />
                      ))}
                    </CatalogueGrid>
                  </section>
                ) : null}
              </div>

              <aside className={styles.panel} aria-labelledby={`${id}about`}>
                <h2 id={`${id}about`} className={styles.heading} tabIndex={-1}>
                  {editing ? 'Edit album' : 'About'}
                </h2>

                {editing ? (
                  <EditForm
                    releaseId={release.editionId ?? release.coverReleaseId ?? ''}
                    data={data}
                    onCancel={() => setEditing(false)}
                    onSaved={() => {
                      setEditing(false)
                      onChanged()
                    }}
                  />
                ) : (
                  <>
                    <dl className={styles.facts}>
                      <dt>Title</dt>
                      <dd>
                        {release.title}
                        <Edited by={edited('title')} />
                      </dd>
                      <dt>Credited to</dt>
                      <dd>
                        <Value value={release.artist} asked={asked} />
                        <Edited by={edited('credit')} />
                      </dd>
                      <dt>Type</dt>
                      <dd>
                        <Value
                          value={[about.primaryType, ...about.secondaryTypes]
                            .filter(Boolean)
                            .join(' · ')}
                          asked={asked}
                        />
                        <Edited by={edited('primaryType') || edited('secondaryTypes')} />
                      </dd>
                      <dt>First released</dt>
                      <dd>
                        <Value value={about.firstReleaseYear?.toString()} asked={asked} />
                        <Edited by={edited('firstReleaseYear')} />
                      </dd>
                      {/*
                        What only a pressing has, shown only for the pressing the
                        files are known to be. Without one these rows would read
                        "none recorded" — an absence stated as a fact, about a
                        record nobody chose.
                      */}
                      {release.editionId != null ? (
                        <>
                          <dt>Edition note</dt>
                          <dd>
                            <Value value={about.disambiguation} asked={asked} />
                            <Edited by={edited('disambiguation')} />
                          </dd>
                          <dt>This edition</dt>
                          <dd>
                            <Value value={released} asked={asked} />
                            <Edited
                              by={
                                edited('releasedYear') ||
                                edited('releasedMonth') ||
                                edited('releasedDay')
                              }
                            />
                          </dd>
                          <dt>Country</dt>
                          <dd>
                            <Value value={countryName(release.country)} asked={asked} />
                            <Edited by={edited('country')} />
                          </dd>
                          <dt>Status</dt>
                          <dd>
                            <Value value={release.status} asked={asked} />
                            <Edited by={edited('status')} />
                          </dd>
                          <dt>Label</dt>
                          <dd>
                            <Value value={about.label} asked={asked} />
                            <Edited by={edited('label')} />
                          </dd>
                          <dt>Catalogue no.</dt>
                          <dd>
                            <Value value={about.catalogNumber} asked={asked} mono />
                            <Edited by={edited('catalogNumber')} />
                          </dd>
                          <dt>Barcode</dt>
                          <dd>
                            <Value value={about.barcode} asked={asked} mono />
                            <Edited by={edited('barcode')} />
                          </dd>
                          <dt>Format</dt>
                          <dd>
                            <Value value={release.formats} asked={asked} />
                            <Edited by={edited('formats')} />
                          </dd>
                          <dt>Discs</dt>
                          <dd>
                            <Value value={release.discCount?.toString()} asked={asked} />
                          </dd>
                        </>
                      ) : (
                        <>
                          <dt>Pressing</dt>
                          <dd>
                            <Text size="sm" tone="secondary">
                              Not known. The album is; which edition of it the files are is not
                              proven, so none is named.
                            </Text>
                          </dd>
                        </>
                      )}
                      <dt>Tracks</dt>
                      <dd>
                        {release.trackCount != null
                          ? held === release.trackCount
                            ? `All ${release.trackCount} in your library`
                            : `${held} of ${release.trackCount} in your library`
                          : `${held} of ${tracks.length} its editions print, in your library`}
                        {release.files > held ? ` · ${release.files} files` : ''}
                      </dd>
                      <dt>Identified</dt>
                      <dd>
                        <Stack direction="column" gap={4} align="start">
                          <span>
                            {certainty?.label ?? release.certainty}
                            {release.editionAlternatives > 0
                              ? ` · ${release.editionAlternatives} others fitted`
                              : ''}
                          </span>
                          {certainty != null ? (
                            <Text size="xs" tone="tertiary">
                              {certainty.note}
                            </Text>
                          ) : null}
                          <AskAgain files={files} onReopened={onReopened} />
                        </Stack>
                      </dd>
                      <dt>Cover</dt>
                      <dd>
                        <Stack direction="column" gap={4} align="start">
                          {about.coverSource != null ? (
                            (COVER_SOURCE[about.coverSource] ?? about.coverSource)
                          ) : (
                            <Text size="sm" tone="tertiary">
                              {about.coverLookupUtc == null
                                ? 'Not looked for yet'
                                : "Neither source has one; the files' own is shown if they carry one"}
                            </Text>
                          )}
                          {release.coverReleaseId != null ? (
                            <Button size="sm" variant="ghost" onClick={() => setChoosing(true)}>
                              Change cover
                            </Button>
                          ) : null}
                        </Stack>
                      </dd>
                      <dt>Review</dt>
                      <dd>
                        <Value
                          value={
                            about.review == null
                              ? null
                              : about.review.byPerson
                                ? about.review.url == null
                                  ? 'Written by you'
                                  : `Edited by you, from ${about.review.source}`
                                : about.review.source
                          }
                          asked={about.reviewLookupUtc != null || edited('review')}
                        />
                        <Edited by={edited('review')} />
                      </dd>
                      <dt>MusicBrainz</dt>
                      <dd>
                        <Stack direction="column" gap={4} align="start">
                          {(
                            [
                              ['release', about.editionMbid, 'This edition'],
                              ['release-group', about.groupMbid, 'The album'],
                            ] as const
                          ).map(([kind, mbid, label]) =>
                            mbid == null ? (
                              <Text key={kind} size="sm" tone="tertiary">
                                {label}: not linked
                              </Text>
                            ) : (
                              <a
                                key={kind}
                                className={styles.link}
                                href={`https://musicbrainz.org/${kind}/${mbid}`}
                                target="_blank"
                                rel="noreferrer"
                              >
                                {label}
                              </a>
                            ),
                          )}
                        </Stack>
                      </dd>
                      {owned ? null : (
                        <>
                          <dt>Wanted</dt>
                          <dd>{about.monitored ? 'Yes' : 'No'}</dd>
                        </>
                      )}
                      {data.contributable > 0 ? (
                        <>
                          <dt>AcoustID</dt>
                          <dd>
                            <Contribute
                              releaseId={releaseId}
                              count={data.contributable}
                              onContributed={onChanged}
                            />
                          </dd>
                        </>
                      ) : null}
                      <dt>On disk</dt>
                      <dd>
                        <Stack direction="column" gap={4} align="start">
                          {onDisk.map((folder) => (
                            <Stack key={folder.path} direction="column" gap={2} align="start">
                              <Link
                                to="/files"
                                search={{ path: folder.path }}
                                className={styles.album}
                              >
                                <Text size="xs" family="mono" className={styles.path}>
                                  {folder.path}
                                </Text>
                              </Link>
                              <Text size="xs" tone="tertiary">
                                {folder.files.length} {folder.files.length === 1 ? 'file' : 'files'}{' '}
                                · {folder.edition ?? 'pressing not known'}
                              </Text>
                            </Stack>
                          ))}
                        </Stack>
                      </dd>
                    </dl>

                    <h3 className={styles.heading}>
                      <Text size="sm" weight="semibold">
                        Last asked
                      </Text>
                    </h3>
                    <dl className={styles.facts}>
                      <dt>Release looked up</dt>
                      <dd>{stamp(about.releaseLookupUtc)}</dd>
                      <dt>Cover looked for</dt>
                      <dd>{stamp(about.coverLookupUtc)}</dd>
                      <dt>Review looked for</dt>
                      <dd>{stamp(about.reviewLookupUtc)}</dd>
                      <dt>Files probed</dt>
                      <dd>{stamp(about.probedUtc)}</dd>
                    </dl>
                  </>
                )}
              </aside>
            </div>
          ) : (
            <div className={styles.main}>
              {credits.length === 0 ? (
                <Text size="sm" tone="tertiary">
                  The catalogue records no credits for this album yet.
                </Text>
              ) : null}
              {CREDIT_GROUPS.map((group, index) => {
                const people = credits.filter((credit) => credit.group === group)
                const open = people.find((person) => `${group}/${person.artistId}` === picked)
                return people.length === 0 ? null : (
                  <section key={group} aria-labelledby={`${id}credits${index}`}>
                    <Stack direction="column" gap={8}>
                      <h2 id={`${id}credits${index}`} className={styles.heading}>
                        {group}
                      </h2>
                      <CatalogueGrid
                        size="artist"
                        layout="shelf"
                        aria-label={`${group}: ${release.title}`}
                      >
                        {people.map((person) => {
                          const face = artistImageUrl({ id: person.artistId })
                          const key = `${group}/${person.artistId}`
                          return (
                            <CatalogueCard
                              key={person.artistId}
                              variant="artist"
                              title={person.name}
                              subtitle={person.role}
                              image={face}
                              render={(props) => (
                                <button
                                  {...props}
                                  type="button"
                                  className={`${props.className} ${styles.credit}`}
                                  aria-expanded={key === picked}
                                  aria-controls={`${id}songs${index}`}
                                  onClick={() =>
                                    setPicked((current) => (current === key ? null : key))
                                  }
                                />
                              )}
                            />
                          )
                        })}
                      </CatalogueGrid>
                      <div id={`${id}songs${index}`}>
                        {open != null ? (
                          <Songs credit={open} tracks={tracks} discs={discs} />
                        ) : null}
                      </div>
                    </Stack>
                  </section>
                )
              })}
            </div>
          )
        }
      />
    </article>
  )
}

/** A partial date as it is known: a year, a month and a year, or a whole date. Never widened. */
function dateLabel(year: number | null, month: number | null, day: number | null): string | null {
  if (year == null) return null
  return new Date(Date.UTC(year, (month ?? 1) - 1, day ?? 1)).toLocaleDateString(undefined, {
    year: 'numeric',
    ...(month != null ? { month: 'long' } : {}),
    ...(month != null && day != null ? { day: 'numeric' } : {}),
    timeZone: 'UTC',
  })
}

function qualityLabel(quality: FileQuality): string {
  return quality.lossless
    ? `${quality.codec} ${quality.bitDepth ?? '?'}/${quality.sampleRateHz / 1000}`
    : `${quality.codec} ${quality.bitrateKbps}`
}

/** Above CD in depth or rate is hi-res, as `AudioQuality.Tier` reads it. */
function qualityTone(quality: FileQuality): BadgeTone {
  if (!quality.lossless) return 'warning'
  return (quality.bitDepth ?? 0) > 16 || quality.sampleRateHz > 48_000 ? 'accent' : 'neutral'
}

/** A track's printed number, which is not always its position: "A1", "12a". */
function numberOf(track: AlbumTrackRow, discs: number): string {
  return `${discs > 1 ? `${track.discNumber}·` : ''}${track.number ?? track.position}`
}

/** Wanting the record this edition is of, which is what the acquire shelf lists. */
function WantButton({
  groupId,
  monitored: initial,
}: {
  readonly groupId: string
  readonly monitored: boolean
}) {
  const [monitored, setMonitored] = useState(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggle = async () => {
    setBusy(true)
    try {
      await api.post('/api/catalogue/release-groups/{id}/monitor', {
        params: { path: { id: groupId } },
        json: { monitor: !monitored },
      })
      setMonitored(!monitored)
      setError(null)
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack direction="column" gap={4} align="start">
      <Button variant="secondary" disabled={busy} onClick={() => void toggle()}>
        {monitored ? 'Wanted' : 'Want'}
      </Button>
      {error != null ? (
        <Text size="xs" tone="warning">
          {error}
        </Text>
      ) : null}
    </Stack>
  )
}

function FileBadges({ files }: { readonly files: readonly FileRow[] }) {
  if (files.length === 0) {
    return (
      <Text size="sm" tone="tertiary">
        missing
      </Text>
    )
  }
  return (
    <Stack gap={4} wrap>
      {files.map((file) =>
        file.quality == null ? (
          <Text key={file.path} size="sm" tone="tertiary">
            not probed
          </Text>
        ) : (
          <Badge key={file.path} tone={qualityTone(file.quality)} size="sm" mono>
            {qualityLabel(file.quality)}
          </Badge>
        ),
      )}
      {files
        .filter((file) => file.integrity !== 'Intact' && file.integrity !== 'Unchecked')
        .map((file) => (
          <Badge key={`${file.path}:integrity`} tone="danger" size="sm">
            {file.integrity.toLowerCase()}
          </Badge>
        ))}
    </Stack>
  )
}

/**
 * The track list, under the works its tracks perform where there are any, and
 * under a heading per disc on a set of more than one.
 *
 * **One `<tbody>` per work, which is what the element is for.** Each is a row
 * group with a heading of its own, so a screen reader announces the symphony
 * once and reads four movements under it. `workGroups` decides where there are
 * headings at all; see that module for why the bar is a run of two.
 */
function Tracks({
  tracks,
  discs,
  editions,
}: {
  readonly tracks: readonly AlbumTrackRow[]
  readonly discs: number
  /** The album's stored editions, which a row's `on` names — see `onlyOn`. */
  readonly editions: readonly AlbumEditionRow[]
}) {
  if (tracks.length === 0) {
    return (
      <Text tone="tertiary">No edition of this album is stored, so there is no track list.</Text>
    )
  }

  const byDisc = new Map<number, AlbumTrackRow[]>()
  for (const track of tracks)
    byDisc.set(track.discNumber, [...(byDisc.get(track.discNumber) ?? []), track])

  return (
    <div className={styles.tracklist}>
      <Table density="cozy">
        <caption className={styles.caption}>Every track its editions print, held or not</caption>
        <thead>
          <tr>
            <TableHeaderCell numeric>No</TableHeaderCell>
            <TableHeaderCell>Track</TableHeaderCell>
            <TableHeaderCell numeric>Length</TableHeaderCell>
            <TableHeaderCell>In your library</TableHeaderCell>
          </tr>
        </thead>

        {[...byDisc].flatMap(([disc, onDisc]) => {
          const groups = workGroups(onDisc) ?? [
            { key: 'all', workTitle: null, prefix: '', tracks: onDisc },
          ]

          return groups.map((group, index) => (
            <tbody key={`${disc}:${group.key}`}>
              {discs > 1 && index === 0 ? (
                <tr className={styles.group}>
                  <th colSpan={4} scope="rowgroup">
                    Disc {disc}
                  </th>
                </tr>
              ) : null}
              {group.workTitle !== null ? (
                <tr>
                  <td className={styles.workGutter} />
                  <th className={styles.work} colSpan={3} scope="rowgroup">
                    <Text size="sm" weight="medium">
                      {group.workTitle}
                    </Text>
                  </th>
                </tr>
              ) : null}
              {group.tracks.map((track) => (
                <Row
                  key={`${track.discNumber}-${track.position}-${track.recordingId}`}
                  track={track}
                  strip={group.prefix}
                  only={onlyOn(track, editions)}
                />
              ))}
            </tbody>
          ))
        })}
      </Table>
    </div>
  )
}

/**
 * The editions a track is on, where that is not all of them — the deluxe's bonus
 * disc, the Japanese edition's extra song. Null on a track every stored edition
 * prints, which is most of them, so the note only appears where it tells
 * somebody something.
 */
function onlyOn(track: AlbumTrackRow, editions: readonly AlbumEditionRow[]): string | null {
  if (track.on.length >= editions.length) return null
  return editions
    .filter((edition) => track.on.includes(edition.id))
    .map((edition) =>
      [edition.title, edition.country, edition.year?.toString()].filter(Boolean).join(', '),
    )
    .join('; ')
}

function Row({
  track,
  strip,
  only,
}: {
  readonly track: AlbumTrackRow
  /** The run-in the group heading has already said. See `workGroups`. */
  readonly strip: string
  /** The editions it is on, when it is not on all of them. */
  readonly only: string | null
}) {
  const tone = track.held ? 'primary' : 'tertiary'

  return (
    <tr data-missing={track.held ? undefined : ''}>
      <TableCell numeric>
        <Text size="sm" family="mono" tone="tertiary">
          {track.number ?? track.position}
        </Text>
      </TableCell>

      {/* The whole printed title stays in the tooltip. */}
      <TableCell title={track.title}>
        <Stack direction="column" gap={2}>
          <Text tone={tone}>{track.title.slice(strip.length)}</Text>
          {track.artist != null ? (
            <Text size="xs" tone="secondary">
              {track.artist}
            </Text>
          ) : null}
          {only != null ? (
            <Text size="xs" tone="tertiary">
              Only on {only}
            </Text>
          ) : null}
        </Stack>
      </TableCell>

      <TableCell numeric>
        <Text size="sm" family="mono" tone={tone}>
          {track.duration ?? '—'}
        </Text>
      </TableCell>

      <TableCell>
        {/*
          A badge per file rather than a tick: more than one is the same track
          in several encodings, which is the dedupe question the whole schema
          exists to be able to ask.
        */}
        <FileBadges files={track.files} />
      </TableCell>
    </tr>
  )
}

function Files({ files }: { readonly files: readonly FileRow[] }) {
  return (
    <div className={styles.tracklist}>
      <Table density="cozy">
        <caption className={styles.caption}>Every file filed under this album</caption>
        <thead>
          <tr>
            <TableHeaderCell>Path</TableHeaderCell>
            <TableHeaderCell>Format</TableHeaderCell>
            <TableHeaderCell>Integrity</TableHeaderCell>
            <TableHeaderCell numeric>Size</TableHeaderCell>
          </tr>
        </thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.path}>
              <TableCell>
                <Text size="xs" family="mono" className={styles.path}>
                  {file.path}
                </Text>
              </TableCell>
              <TableCell>
                <Text size="sm" family="mono">
                  {file.quality == null
                    ? 'not probed'
                    : `${qualityLabel(file.quality)} · ${file.quality.bitrateKbps} kbps`}
                </Text>
              </TableCell>
              <TableCell>
                <Text
                  size="sm"
                  tone={
                    file.integrity === 'Intact'
                      ? 'secondary'
                      : file.integrity === 'Unchecked'
                        ? 'tertiary'
                        : 'danger'
                  }
                >
                  {file.integrity === 'Unchecked' ? 'not checked' : file.integrity.toLowerCase()}
                </Text>
              </TableCell>
              <TableCell numeric>
                <Text size="sm" family="mono">
                  {(file.sizeBytes / 1_000_000).toFixed(1)} MB
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
 * One folder's files, in the folder's own order. The number is the claimed
 * pressing's where there is one, and otherwise the file's own tag — what the file
 * says about itself, never a position on a pressing nobody claimed.
 */
function FolderFiles({ files }: { readonly files: readonly AlbumFileRow[] }) {
  return (
    <div className={styles.tracklist}>
      <Table density="cozy">
        <thead>
          <tr>
            <TableHeaderCell numeric>No</TableHeaderCell>
            <TableHeaderCell>File</TableHeaderCell>
            <TableHeaderCell>Format</TableHeaderCell>
          </tr>
        </thead>
        <tbody>
          {files.map((row) => {
            const number = row.position ?? row.taggedTrack
            const disc = row.position == null ? row.taggedDisc : row.disc

            return (
              <tr key={row.file.path}>
                <TableCell numeric>
                  <Text size="sm" family="mono" tone="tertiary">
                    {number == null ? '—' : disc != null && disc > 1 ? `${disc}·${number}` : number}
                  </Text>
                </TableCell>
                <TableCell title={row.file.path}>
                  <Stack direction="column" gap={2}>
                    <Text size="sm">{row.recording ?? row.file.path.split('/').at(-1)}</Text>
                    <Text size="xs" family="mono" tone="tertiary" className={styles.path}>
                      {row.file.path.split('/').at(-1)}
                    </Text>
                  </Stack>
                </TableCell>
                <TableCell>
                  <FileBadges files={[row.file]} />
                </TableCell>
              </tr>
            )
          })}
        </tbody>
      </Table>
    </div>
  )
}

/**
 * Naming which album a folder is, whatever the passes decided.
 *
 * Any release of the album names it — found by a search or a pasted link — but
 * only its album is claimed: the pressing is left to the attribution pass, which
 * may prove one of that album's editions from the audio and can never move the
 * folder to another album.
 */
function NotThisAlbum({
  folder,
  albumId,
  onChanged,
}: {
  readonly folder: string
  readonly albumId: string
  readonly onChanged: () => void
}) {
  const navigate = useNavigate()
  const [query, setQuery] = useState('')
  const [asked, setAsked] = useState<string | null>(null)
  const [sending, setSending] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)

  const results = useApiQuery(
    () =>
      asked === null
        ? Promise.resolve(null)
        : api.get('/api/catalogue/matching/releases/search', { params: { query: { q: asked } } }),
    [asked],
  )

  async function choose(row: SearchRow) {
    if (
      !window.confirm(
        `File every file in “${folder}” under “${row.title}”?\n\n` +
          'No pressing is claimed: the next attribution run may prove one of this album’s ' +
          'editions from the audio. Nothing on disk is touched.',
      )
    ) {
      return
    }

    setSending(true)
    setFailure(null)

    try {
      const result = await api.post('/api/catalogue/matching/folders/album', {
        json: { folder, release: row.mbid },
      })

      if (result.album === albumId) onChanged()
      else void navigate({ to: '/library/albums/$albumId', params: { albumId: result.album } })
    } catch (cause: unknown) {
      setFailure(`Nothing was changed. ${describeError(cause)}`)
    } finally {
      setSending(false)
    }
  }

  return (
    <Stack direction="column" gap={8} align="stretch">
      <form
        onSubmit={(event) => {
          event.preventDefault()
          if (query.trim() !== '') setAsked(query.trim())
        }}
      >
        <Stack gap={8} align="end">
          <Field
            label="Not this album?"
            hint="Search MusicBrainz, or paste a release link or MBID. Only the album is claimed."
          >
            {(control) => (
              <Input
                {...control}
                fullWidth
                value={query}
                onChange={(event) => {
                  setQuery(event.currentTarget.value)
                }}
              />
            )}
          </Field>
          <Button type="submit" size="sm" variant="secondary" disabled={query.trim() === ''}>
            Search
          </Button>
        </Stack>
      </form>

      <div role="status" aria-busy={results.status === 'loading' && asked !== null}>
        {results.status === 'loading' && asked !== null ? (
          <Text size="xs" tone="tertiary">
            Asking MusicBrainz…
          </Text>
        ) : null}
        {results.status === 'error' ? (
          <Text size="xs" tone="danger">
            MusicBrainz could not search: {results.message}
          </Text>
        ) : null}
        {results.status === 'ready' && results.data !== null && results.data.items.length === 0 ? (
          <Text size="xs" tone="tertiary">
            Nothing matched.
          </Text>
        ) : null}
        {failure !== null ? (
          <Text size="xs" tone="danger">
            {failure}
          </Text>
        ) : null}
      </div>

      {results.status === 'ready' && results.data !== null
        ? results.data.items.map((row) => (
            <Stack key={row.mbid} gap={12} align="center" justify="between">
              <Stack direction="column" gap={2}>
                <Text size="sm">{row.title}</Text>
                <Text size="xs" tone="tertiary">
                  {[row.artist, row.year, row.country, row.formats]
                    .filter((part) => part != null)
                    .join(' · ')}
                </Text>
              </Stack>
              <Button
                size="sm"
                variant="secondary"
                disabled={sending}
                onClick={() => {
                  void choose(row)
                }}
              >
                This album
              </Button>
            </Stack>
          ))
        : null}
    </Stack>
  )
}

/** What one credited artist worked on here: their tracks, or the whole album. */
function Songs({
  credit,
  tracks,
  discs,
}: {
  readonly credit: AlbumCredit
  readonly tracks: readonly AlbumTrackRow[]
  readonly discs: number
}) {
  const id = useId()
  const theirs =
    credit.tracks == null ? tracks : credit.tracks.flatMap((place) => tracks[place - 1] ?? [])

  return (
    <div className={styles.panel}>
      <Stack gap={12} align="center" justify="between" wrap>
        <h3 id={`${id}h`} className={styles.heading}>
          {credit.name} on this album
        </h3>
        <Link
          to="/library/artists/$artistId"
          params={{ artistId: credit.artistId }}
          className={styles.link}
        >
          <Text size="sm">Artist page</Text>
        </Link>
      </Stack>
      <Text size="sm" tone="secondary">
        {credit.role} ·{' '}
        {credit.tracks == null ? 'every track' : `${theirs.length} of ${tracks.length} tracks`}
      </Text>
      <Table density="cozy" aria-labelledby={`${id}h`}>
        <thead>
          <tr>
            <TableHeaderCell numeric>No</TableHeaderCell>
            <TableHeaderCell>Track</TableHeaderCell>
            <TableHeaderCell numeric>Length</TableHeaderCell>
          </tr>
        </thead>
        <tbody>
          {theirs.map((track) => (
            <tr key={`${track.discNumber}-${track.position}-${track.recordingId}`}>
              <TableCell numeric>
                <Text size="sm" family="mono" tone="tertiary">
                  {numberOf(track, discs)}
                </Text>
              </TableCell>
              <TableCell>
                <Text tone={track.held ? 'primary' : 'tertiary'}>{track.title}</Text>
              </TableCell>
              <TableCell numeric>
                <Text size="sm" family="mono">
                  {track.duration ?? '—'}
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
 * The About panel as a form. The whole form is sent; the server keeps only what
 * differs from what MusicBrainz and Wikipedia hold.
 */
function EditForm({
  releaseId,
  data,
  onCancel,
  onSaved,
}: {
  readonly releaseId: string
  readonly data: Detail
  readonly onCancel: () => void
  readonly onSaved: () => void
}) {
  const { album: release, about } = data
  const pressing = release.editionId != null

  const [draft, setDraft] = useState({
    title: release.title,
    credit: release.artist ?? '',
    disambiguation: about.disambiguation ?? '',
    primaryType: about.primaryType ?? '',
    secondaryTypes: about.secondaryTypes.join(', '),
    firstReleaseYear: about.firstReleaseYear?.toString() ?? '',
    releasedYear: about.releasedYear?.toString() ?? '',
    releasedMonth: about.releasedMonth?.toString() ?? '',
    releasedDay: about.releasedDay?.toString() ?? '',
    country: release.country ?? '',
    status: release.status ?? '',
    label: about.label ?? '',
    catalogNumber: about.catalogNumber ?? '',
    barcode: about.barcode ?? '',
    formats: release.formats ?? '',
    review: about.review?.text ?? '',
  })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const text = (key: keyof typeof draft) => ({
    value: draft[key],
    onChange: (event: { target: { value: string } }) =>
      setDraft({ ...draft, [key]: event.target.value }),
  })

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)

    const body: EditRequest = {
      title: draft.title,
      credit: blank(draft.credit),
      disambiguation: blank(draft.disambiguation),
      primaryType: blank(draft.primaryType),
      secondaryTypes: list(draft.secondaryTypes),
      firstReleaseYear: year(draft.firstReleaseYear),
      releasedYear: year(draft.releasedYear),
      releasedMonth: year(draft.releasedMonth),
      releasedDay: year(draft.releasedDay),
      country: blank(draft.country),
      status: blank(draft.status),
      label: blank(draft.label),
      catalogNumber: blank(draft.catalogNumber),
      barcode: blank(draft.barcode),
      formats: blank(draft.formats),
      review: blank(draft.review),
    }

    try {
      await api.post('/api/catalogue/releases/{id}/edits', {
        params: { path: { id: releaseId } },
        json: body,
      })
      onSaved()
    } catch (cause) {
      setError(describeError(cause))
      setSaving(false)
    }
  }

  return (
    <form className={styles.form} onSubmit={(event) => void submit(event)}>
      <Field className={styles.wide} label="Title">
        {(control) => <Input {...control} {...text('title')} required fullWidth />}
      </Field>
      <Field className={styles.wide} label="Credited to" hint="As the sleeve prints it.">
        {(control) => <Input {...control} {...text('credit')} fullWidth />}
      </Field>
      {pressing ? (
        <Field className={styles.wide} label="Edition note" hint="“remastered”, “deluxe”">
          {(control) => <Input {...control} {...text('disambiguation')} fullWidth />}
        </Field>
      ) : null}
      <Field label="Type" hint="Album, EP, Single">
        {(control) => <Input {...control} {...text('primaryType')} fullWidth />}
      </Field>
      <Field label="Also" hint="Live, Compilation — comma-separated">
        {(control) => <Input {...control} {...text('secondaryTypes')} fullWidth />}
      </Field>
      <Field label="First released" hint="The album, any edition">
        {(control) => (
          <Input {...control} {...text('firstReleaseYear')} type="number" mono fullWidth />
        )}
      </Field>
      {/* Sent either way; the server keeps them as they were without a pressing. */}
      {pressing ? (
        <>
          <Field label="This edition: year">
            {(control) => (
              <Input {...control} {...text('releasedYear')} type="number" mono fullWidth />
            )}
          </Field>
          <Field label="Month" hint="Blank if unknown, never January">
            {(control) => (
              <Input
                {...control}
                {...text('releasedMonth')}
                type="number"
                min={1}
                max={12}
                disabled={draft.releasedYear === ''}
                mono
                fullWidth
              />
            )}
          </Field>
          <Field label="Day">
            {(control) => (
              <Input
                {...control}
                {...text('releasedDay')}
                type="number"
                min={1}
                max={31}
                disabled={draft.releasedMonth === ''}
                mono
                fullWidth
              />
            )}
          </Field>
          <Field label="Country" hint="Two-letter code">
            {(control) => <Input {...control} {...text('country')} maxLength={2} mono fullWidth />}
          </Field>
          <Field label="Status" hint="Official, Promotion, Bootleg">
            {(control) => <Input {...control} {...text('status')} fullWidth />}
          </Field>
          <Field label="Label">
            {(control) => <Input {...control} {...text('label')} fullWidth />}
          </Field>
          <Field label="Catalogue number">
            {(control) => <Input {...control} {...text('catalogNumber')} mono fullWidth />}
          </Field>
          <Field label="Barcode">
            {(control) => (
              <Input {...control} {...text('barcode')} inputMode="numeric" mono fullWidth />
            )}
          </Field>
          <Field label="Format" hint="CD, Digital Media, CD+DVD-Video">
            {(control) => <Input {...control} {...text('formats')} fullWidth />}
          </Field>
        </>
      ) : null}
      <Field
        className={styles.wide}
        label="Review"
        hint="Blank lines split paragraphs. Written here, it is yours and no pass replaces it."
      >
        {(control) => (
          <textarea {...control} {...text('review')} className={styles.textarea} rows={8} />
        )}
      </Field>

      {error != null ? (
        <Text className={styles.wide} size="sm" tone="danger" role="alert">
          {error}
        </Text>
      ) : null}

      <Stack className={styles.wide} gap={8} justify="end">
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={saving}>
          {saving ? 'Saving…' : 'Save'}
        </Button>
      </Stack>
    </form>
  )
}

/**
 * Saying the album is wrong, so its folders become questions again.
 *
 * The reopen is the folder's, which is the unit the Identify screen answers in:
 * every matched file under each album folder these files sit in gives up its
 * answer. A file loose under an artist has no album folder, and reopening its
 * parent would take back the whole artist, so those are left and named.
 */
type Reopened = {
  readonly failed: boolean
  readonly message: string
  /** The folders that were reopened, to open in Files. */
  readonly folders: readonly string[]
}

function AskAgain({
  files,
  onReopened,
}: {
  readonly files: readonly FileRow[]
  readonly onReopened: (result: Reopened) => void
}) {
  const navigate = useNavigate()
  const [sending, setSending] = useState(false)

  const parents = [...new Set(files.map((file) => parentOf(file.path)))]
  const folders = [
    ...new Set(
      parents.filter((parent) => parent.split('/').length >= ALBUM_FOLDER_DEPTH).map(albumFolderOf),
    ),
  ]
  const loose = parents
    .filter((parent) => parent.split('/').length < ALBUM_FOLDER_DEPTH)
    .map((parent) => (parent === '' ? 'the library root' : parent))

  if (folders.length === 0) return null

  async function reopen() {
    if (
      !window.confirm(
        'Say this album was identified wrong?\n\n' +
          `Every matched file in ${folders.map((folder) => `“${folder}”`).join(', ')} gives up ` +
          'its recording, album and track, and each folder becomes one question on the Identify ' +
          'screen. No pass will match them again. Nothing on disk is touched.',
      )
    ) {
      return
    }

    setSending(true)

    const done: string[] = []
    const details: string[] = []

    try {
      for (const folder of folders) {
        const result = await api.post('/api/catalogue/matching/folders/reopen', {
          json: { folder },
        })
        done.push(folder)
        details.push(result.detail)
      }
    } catch (cause: unknown) {
      const left = folders.filter((folder) => !done.includes(folder))
      onReopened({
        failed: true,
        message: [...details, `Not reopened: ${left.join(', ')}. ${describeError(cause)}`].join(
          ' ',
        ),
        folders: done,
      })
      return
    }

    const [only] = folders
    if (folders.length === 1 && only !== undefined) {
      void navigate({ to: '/files', search: { path: only } })
      return
    }

    onReopened({ failed: false, message: details.join(' '), folders: done })
  }

  return (
    <Stack direction="column" gap={8} align="start">
      <Button
        size="sm"
        variant="secondary"
        disabled={sending}
        onClick={() => {
          void reopen().finally(() => {
            setSending(false)
          })
        }}
      >
        {sending ? 'Reopening…' : 'Identified wrong — ask again'}
      </Button>

      {loose.length > 0 ? (
        <Text size="xs" tone="tertiary" block>
          Not reopened, because they sit in no album folder: {loose.join(', ')}
        </Text>
      ) : null}
    </Stack>
  )
}

/**
 * Offering this album's fingerprints back to AcoustID.
 *
 * Only ever shown when there is something to offer, which on an ordinary album
 * is never: the identification pass took its answer from AcoustID, so AcoustID
 * is where the link came from. A non-zero count means somebody chose these
 * recordings by hand — a live recording nobody has fingerprinted, or audio
 * AcoustID knows and MusicBrainz linked to nothing — and the link they made
 * exists only in this library.
 *
 * A button and nothing else. There is no automatic path to this call and there
 * must not be: it writes a claim into a shared database under the operator's own
 * account, and the whole point of the screen behind it was that a person looked
 * at the pairing first.
 */
function Contribute({
  releaseId,
  count,
  onContributed,
}: {
  readonly releaseId: string
  readonly count: number
  readonly onContributed: () => void
}) {
  const [state, setState] = useState<
    { status: 'idle' | 'sending' } | { status: 'done' | 'error'; message: string }
  >({ status: 'idle' })

  if (count === 0) {
    // After a successful send the refetch brings the count back as zero, which
    // is the confirmation — so the sentence survives the row disappearing.
    return state.status === 'done' ? (
      <Text size="sm" tone="tertiary">
        {state.message}
      </Text>
    ) : null
  }

  async function send() {
    setState({ status: 'sending' })

    try {
      const result = await api.post('/api/catalogue/albums/{id}/fingerprints', {
        params: { path: { id: releaseId } },
      })

      setState({ status: 'done', message: result.detail })
      onContributed()
    } catch (cause: unknown) {
      setState({ status: 'error', message: describeError(cause) })
    }
  }

  return (
    <Stack direction="column" gap={8} align="start">
      <Stack gap={12} align="center" wrap>
        <Badge tone="neutral" size="sm">
          {count} to contribute
        </Badge>

        <Button
          size="sm"
          variant="secondary"
          disabled={state.status === 'sending'}
          onClick={() => {
            void send()
          }}
        >
          {state.status === 'sending' ? 'Sending…' : 'Contribute fingerprints'}
        </Button>
      </Stack>

      <Text size="xs" tone="tertiary" block>
        You chose {count === 1 ? "this file's recording" : 'these recordings'} by hand rather than
        taking AcoustID's answer. Sending the stored fingerprint bound to that recording makes the
        music identifiable for everyone — including this library, if it is ever re-ripped. Submitted
        under your own AcoustID account. Nothing on disk is touched.
      </Text>

      {state.status === 'error' ? (
        <Text size="sm" tone="danger" block>
          {state.message}
        </Text>
      ) : null}
    </Stack>
  )
}
