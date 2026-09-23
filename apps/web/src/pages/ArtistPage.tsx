import type { components } from '@fonoteca/api-client'
import { describeError } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  Button,
  CatalogueCard,
  CatalogueGrid,
  type CatalogueGridProps,
  Disclosure,
  Field,
  Input,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Tabs,
  Text,
} from '@fonoteca/ui'
import { Link, useParams } from '@tanstack/react-router'
import { type FormEvent, useEffect, useId, useState } from 'react'
import { flushSync } from 'react-dom'
import { api, apiBaseUrl } from '../api.ts'
import { TagWritePanel } from '../components/TagWritePanel.tsx'
import { useApiQuery } from '../useApiQuery.ts'
import type { AlbumSection, AlbumSectionKey, ArtistAlbum } from './artistAlbums.ts'
import { albumsOf, leaf, sameArtist, sectionsOf } from './artistAlbums.ts'
import { countryName, lifeSpan } from './artistFacts.ts'
import { type Composition, worksOf } from './artistWorks.ts'
import {
  artistBannerUrl,
  artistImageUrl,
  releaseArt,
  releaseCover,
  releaseGroupArt,
} from './coverArt.ts'
import styles from './Profile.module.css'
import { blank, Edited, list, Prose, stamp, Value, year } from './profile.tsx'
import { workGroups } from './workGroups.ts'

type Detail = components['schemas']['ArtistDetailResponse']
type TrackRow = components['schemas']['TrackRow']
type EditRequest = components['schemas']['ArtistEditRequest']

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
    detail: 'Albums credited to them.',
  },
  // The heading names the band instead — see the render — so this title is the
  // fallback nothing reaches and the detail line is omitted, since it says the
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

const TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'biography', label: 'Biography' },
  { key: 'discography', label: 'Discography' },
] as const

/** A composer's page gets a Works tab, before the discography. */
const COMPOSER_TABS = [TABS[0], TABS[1], { key: 'works', label: 'Works' }, TABS[2]] as const

type TabKey = (typeof COMPOSER_TABS)[number]['key']

/**
 * One artist, as Roon lays one out: a hero with their picture, then tabs for
 * the overview, the biography, the works a composer wrote, and the discography.
 *
 * **Everything the catalogue holds about them is on the page**, and what it
 * does not hold says which of the two kinds of nothing it is (rule 3): the
 * About panel prints "not asked yet" against a lookup that never ran and "none
 * recorded" against one that did, with the four lookup stamps beside it.
 *
 * **Editing is the About panel turned into a form.** A save is stored beside
 * the providers' answers (rule 4), and every field a person set is marked as
 * theirs. The page keeps showing what it had while it reads the saved answer
 * back, so the tab somebody was on survives the save.
 */
export function ArtistPage() {
  const { artistId } = useParams({ from: '/library/artists/$artistId' })
  const [version, setVersion] = useState(0)

  const state = useApiQuery(
    () => api.get('/api/catalogue/artists/{id}', { params: { path: { id: artistId } } }),
    [artistId, version],
  )

  const [held, setHeld] = useState<{ readonly id: string; readonly data: Detail } | null>(null)

  useEffect(() => {
    if (state.status === 'ready') setHeld({ id: artistId, data: state.data })
  }, [state, artistId])

  const data = state.status === 'ready' ? state.data : held?.id === artistId ? held.data : null

  return (
    <Stack direction="column" gap={20}>
      {/*
        The list's own order and filter, carried back rather than reset. The
        card that was clicked put them on this page's URL, so `prev` is the
        state the shelf was in when it was opened. `prev` is the *validated*
        search, so a bookmark landing here with an album's order on it hands
        back nothing rather than a value the artists list cannot show.
      */}
      <Link
        to="/library"
        from="/library/artists/$artistId"
        search={(prev) => prev}
        className={styles.back}
      >
        <Text size="sm">← All artists</Text>
      </Link>

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' && data == null ? (
          <Text tone="tertiary">Reading the catalogue…</Text>
        ) : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read this artist</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}
      </div>

      {data != null ? (
        <Profile
          key={artistId}
          artistId={artistId}
          data={data}
          onSaved={() => setVersion((current) => current + 1)}
        />
      ) : null}
    </Stack>
  )
}

function Profile({
  artistId,
  data,
  onSaved,
}: {
  readonly artistId: string
  readonly data: Detail
  readonly onSaved: () => void
}) {
  const { artist, profile, tracks, discography } = data
  const [tab, setTab] = useState<TabKey>('overview')
  const [editing, setEditing] = useState(false)
  const id = useId()

  const asked = artist.describedAtUtc != null
  // `artist.name` is already the Latin name where there is one.
  const shown = artist.name
  const edited = (field: string) => profile.edited.includes(field)
  const facts = [countryName(artist.country), lifeSpan(artist), artist.gender].filter(
    (fact): fact is string => fact != null,
  )

  // Not memoised: single passes over a list the same request just produced,
  // cheaper than the dependency arrays that would guard them.
  const albums = albumsOf(tracks)
  const sections = sectionsOf(albums, shown)
  const works = worksOf(tracks)

  const portrait = artistImageUrl(artist, 320)

  const open = (key: TabKey, focus: string) => {
    // The button leaves with its panel, so focus follows the reader.
    flushSync(() => setTab(key))
    document.getElementById(focus)?.focus()
  }

  return (
    <article className={styles.page} aria-labelledby={`${id}name`}>
      <header>
        {/*
          No banner is a plain grey band, never a blown-up portrait. The src is
          this application's own endpoint, so an uploaded banner and one written
          onto the artist's shelf both show here without the page knowing which
          it got; a 404 leaves the band plain.
        */}
        <div className={styles.banner}>
          {profile.banner != null || profile.bannerUploaded ? (
            <img src={artistBannerUrl({ id: artist.id })} alt="" />
          ) : null}
        </div>

        <div className={styles.identity}>
          <div className={styles.portrait}>
            <Artwork name={shown} shape="circle" size="fill" src={portrait} />
          </div>

          <div className={styles.names}>
            <h1 id={`${id}name`} className={styles.title}>
              {shown}
            </h1>

            {profile.latinName != null ? (
              <Text size="md" tone="secondary">
                {profile.name}
              </Text>
            ) : null}

            <Text size="sm" tone="secondary">
              {[artist.disambiguation, ...facts].filter(Boolean).join(' · ') ||
                (asked ? 'MusicBrainz holds nothing more about them.' : 'Not described yet.')}
            </Text>

            {artist.type != null || artist.genres.length > 0 ? (
              <Stack gap={8} align="center" wrap>
                {artist.type != null ? (
                  <Badge tone="neutral" size="sm">
                    {artist.type}
                  </Badge>
                ) : null}
                {artist.genres.map((genre) => (
                  <Badge key={genre} tone="info" size="sm">
                    {genre}
                  </Badge>
                ))}
              </Stack>
            ) : null}
          </div>

          <Stack className={styles.actions} gap={8} wrap align="start">
            <FollowButton artistId={artistId} following={artist.following} />
            <Button
              variant="secondary"
              aria-pressed={editing}
              onClick={() => {
                setEditing(!editing)
                open('biography', `${id}about`)
              }}
            >
              Edit
            </Button>
          </Stack>
        </div>
      </header>

      <Tabs
        tabs={works.length === 0 ? TABS : COMPOSER_TABS}
        label={shown}
        selected={tab}
        onSelect={setTab}
        panel={(key) =>
          key === 'overview' ? (
            <div className={styles.main}>
              <section aria-labelledby={`${id}bio`}>
                <Stack direction="column" gap={8}>
                  <h2 id={`${id}bio`} className={styles.heading}>
                    Biography
                  </h2>
                  <Prose
                    written={profile.biography}
                    asked={profile.biographyLookupUtc != null}
                    none="No biography found for them."
                    onMore={() => open('biography', `${id}bioFull`)}
                  />
                </Stack>
              </section>

              {works.length > 0 ? (
                <section aria-labelledby={`${id}worksTop`}>
                  <Stack direction="column" gap={8} align="start">
                    <h2 id={`${id}worksTop`} className={styles.heading}>
                      Most recorded works
                    </h2>
                    <Works works={works.slice(0, 4)} label={`Most recorded works by ${shown}`} />
                    <Button size="sm" variant="ghost" onClick={() => open('works', `${id}works`)}>
                      All {works.length} works
                    </Button>
                  </Stack>
                </section>
              ) : null}

              {sections.slice(0, 1).map((section) => (
                <Shelf
                  key="first"
                  section={section}
                  artist={shown}
                  id={`${id}shelfTop`}
                  layout="shelf"
                />
              ))}

              {(
                [
                  ['Members', profile.members],
                  ['Member of', profile.memberOf],
                ] as const
              ).map(([title, people], index) =>
                people.length === 0 ? null : (
                  <section key={title} aria-labelledby={`${id}people${index}`}>
                    <Stack direction="column" gap={8}>
                      <h2 id={`${id}people${index}`} className={styles.heading}>
                        {title}
                      </h2>
                      <CatalogueGrid size="artist" layout="shelf" aria-label={`${title}: ${shown}`}>
                        {people.map((person) => {
                          const face = artistImageUrl(person)
                          return (
                            <CatalogueCard
                              key={person.id}
                              variant="artist"
                              title={person.name}
                              image={face}
                              render={(props) => (
                                <Link
                                  {...props}
                                  to="/library/artists/$artistId"
                                  params={{ artistId: person.id }}
                                />
                              )}
                            />
                          )
                        })}
                      </CatalogueGrid>
                    </Stack>
                  </section>
                ),
              )}
            </div>
          ) : key === 'biography' ? (
            <div className={styles.columns}>
              <section className={styles.main} aria-labelledby={`${id}bioFull`}>
                <Stack direction="column" gap={8}>
                  <h2 id={`${id}bioFull`} className={styles.heading} tabIndex={-1}>
                    Biography
                  </h2>
                  <Prose
                    written={profile.biography}
                    asked={profile.biographyLookupUtc != null}
                    none="No biography found for them."
                  />
                </Stack>
              </section>

              <aside className={styles.panel} aria-labelledby={`${id}about`}>
                <h2 id={`${id}about`} className={styles.heading} tabIndex={-1}>
                  {editing ? 'Edit artist' : 'About'}
                </h2>

                {editing ? (
                  <EditForm
                    artistId={artistId}
                    data={data}
                    onCancel={() => setEditing(false)}
                    onSaved={() => {
                      setEditing(false)
                      onSaved()
                    }}
                  />
                ) : (
                  <>
                    <dl className={styles.facts}>
                      <dt>Name</dt>
                      <dd>
                        {profile.name}
                        <Edited by={edited('name')} />
                      </dd>
                      <dt>Latin script</dt>
                      <dd>
                        {profile.latinName ?? (
                          <Text size="sm" tone="tertiary">
                            Same as name
                          </Text>
                        )}
                        <Edited by={edited('latinName')} />
                      </dd>
                      <dt>Sort name</dt>
                      <dd>
                        <Value value={artist.sortName} asked={asked} />
                        <Edited by={edited('sortName')} />
                      </dd>
                      <dt>Type</dt>
                      <dd>
                        <Value value={artist.type} asked={asked} />
                        <Edited by={edited('type')} />
                      </dd>
                      <dt>Disambiguation</dt>
                      <dd>
                        <Value value={artist.disambiguation} asked={asked} />
                        <Edited by={edited('disambiguation')} />
                      </dd>
                      <dt>Country</dt>
                      <dd>
                        <Value value={countryName(artist.country)} asked={asked} />
                        <Edited by={edited('country')} />
                      </dd>
                      <dt>Gender</dt>
                      <dd>
                        <Value value={artist.gender} asked={asked} />
                        <Edited by={edited('gender')} />
                      </dd>
                      <dt>Life span</dt>
                      <dd>
                        <Value value={lifeSpan(artist)} asked={asked} />
                        <Edited
                          by={edited('beganYear') || edited('endedYear') || edited('ended')}
                        />
                      </dd>
                      <dt>Genres</dt>
                      <dd>
                        <Value value={artist.genres.join(', ')} asked={asked} />
                        <Edited by={edited('genres')} />
                      </dd>
                      <dt>Biography</dt>
                      <dd>
                        <Value
                          value={
                            profile.biography == null
                              ? null
                              : profile.biography.byPerson
                                ? profile.biography.url == null
                                  ? 'Written by you'
                                  : `Edited by you, from ${profile.biography.source}`
                                : profile.biography.source
                          }
                          asked={profile.biographyLookupUtc != null || edited('biography')}
                        />
                        <Edited by={edited('biography')} />
                      </dd>
                      <dt>MusicBrainz</dt>
                      <dd>
                        {profile.mbid != null ? (
                          <a
                            className={styles.link}
                            href={`https://musicbrainz.org/artist/${profile.mbid}`}
                            target="_blank"
                            rel="noreferrer"
                          >
                            <Text size="sm" family="mono">
                              {profile.mbid}
                            </Text>
                          </a>
                        ) : (
                          <Text size="sm" tone="tertiary">
                            Not linked
                          </Text>
                        )}
                      </dd>
                      <dt>Picture</dt>
                      <dd>
                        <Stack direction="column" gap={8} align="start">
                          <Stack gap={8} align="center" wrap>
                            <a
                              className={styles.link}
                              href={artistImageUrl({ id: artist.id })}
                              target="_blank"
                              rel="noreferrer"
                            >
                              View image
                            </a>
                            {profile.portraitUploaded ? (
                              <Badge tone="info" size="sm">
                                Uploaded by you
                              </Badge>
                            ) : null}
                            <Edited by={edited('portrait')} />
                          </Stack>

                          <PicturePicker
                            artistId={artist.id}
                            kind="portrait"
                            uploaded={profile.portraitUploaded}
                            onChanged={onSaved}
                          />
                        </Stack>
                      </dd>
                      <dt>Banner</dt>
                      <dd>
                        <Stack direction="column" gap={8} align="start">
                          <Stack gap={8} align="center" wrap>
                            {profile.banner != null || profile.bannerUploaded ? (
                              <a
                                className={styles.link}
                                href={artistBannerUrl({ id: artist.id })}
                                target="_blank"
                                rel="noreferrer"
                              >
                                View image
                              </a>
                            ) : (
                              <Text size="sm" tone="tertiary">
                                {profile.bannerLookupUtc == null && !edited('banner')
                                  ? 'Not asked yet'
                                  : 'None recorded'}
                              </Text>
                            )}
                            {profile.bannerUploaded ? (
                              <Badge tone="info" size="sm">
                                Uploaded by you
                              </Badge>
                            ) : null}
                            <Edited by={edited('banner')} />
                          </Stack>

                          <PicturePicker
                            artistId={artist.id}
                            kind="banner"
                            uploaded={profile.bannerUploaded}
                            onChanged={onSaved}
                          />
                        </Stack>
                      </dd>
                      <dt>Following</dt>
                      <dd>{artist.following ? 'Yes — new records are wanted' : 'No'}</dd>
                    </dl>

                    <h3 className={styles.heading}>
                      <Text size="sm" weight="semibold">
                        Last asked
                      </Text>
                    </h3>
                    <dl className={styles.facts}>
                      <dt>Described</dt>
                      <dd>{stamp(artist.describedAtUtc)}</dd>
                      <dt>Biography looked for</dt>
                      <dd>{stamp(profile.biographyLookupUtc)}</dd>
                      <dt>Picture looked for</dt>
                      <dd>{stamp(profile.portraitLookupUtc)}</dd>
                      <dt>Banner looked for</dt>
                      <dd>{stamp(profile.bannerLookupUtc)}</dd>
                      <dt>Discography browsed</dt>
                      <dd>{stamp(profile.discographyLookupUtc)}</dd>
                    </dl>
                  </>
                )}
              </aside>
            </div>
          ) : key === 'works' ? (
            <div className={styles.main}>
              <section aria-labelledby={`${id}works`}>
                <Stack direction="column" gap={8}>
                  <h2 id={`${id}works`} className={styles.heading} tabIndex={-1}>
                    Works
                  </h2>
                  <Text size="sm" tone="secondary">
                    {works.length} work{works.length === 1 ? '' : 's'} in your library, gathered
                    from the movements your files perform.
                  </Text>
                  <Works works={works} label={`Works by ${shown}`} />
                </Stack>
              </section>
            </div>
          ) : (
            <div className={styles.main}>
              {/*
                Held and missing are two different questions and only the first
                is gated on the library. A followed artist nothing here is by
                has no tracks at all, and the discography below is the entire
                reason for following them.
              */}
              {tracks.length === 0 ? (
                <Text tone="tertiary">
                  {artist.following
                    ? 'Nothing in the library is by this artist yet.'
                    : 'Nothing in the library credits this artist.'}
                </Text>
              ) : (
                <>
                  {/*
                    Every track on this page at once, which is the unit for the
                    case the album button cannot serve. The scope is the same
                    rule this page browses by, so what gets written is exactly
                    what is listed below.
                  */}
                  <TagWritePanel
                    scope={{ kind: 'artist', id: artistId }}
                    label={`${shown}'s tracks`}
                    size="sm"
                  />

                  {sections.map((section, index) => (
                    <Shelf
                      key={`${section.key}:${section.band ?? ''}`}
                      section={section}
                      artist={shown}
                      id={`${id}shelf${index}`}
                      detail
                    />
                  ))}
                </>
              )}

              {/*
                Outside the branch above, deliberately: this half of the page is
                about records the library does *not* hold, so gating it on
                holding something would hide it in exactly the case it was
                built for.
              */}
              <MissingRecords discography={discography} artist={shown} id={`${id}missing`} />

              {tracks.length > 0 ? (
                <Disclosure
                  size="sm"
                  summary={
                    <Text size="sm" weight="medium">
                      Track by track
                    </Text>
                  }
                  aside={
                    <Text size="sm" tone="tertiary" family="mono">
                      {tracks.length.toLocaleString()}
                    </Text>
                  }
                >
                  <Tracks name={shown} tracks={tracks} />
                </Disclosure>
              ) : null}
            </div>
          )
        }
      />
    </article>
  )
}

/**
 * One shelf of albums. `<h2>` under the page's one `<h1>`, so a screen
 * reader's outline of the page is the split itself.
 */
function Shelf({
  section,
  artist,
  id,
  detail = false,
  layout = 'grid',
}: {
  readonly section: AlbumSection
  readonly artist: string
  readonly id: string
  /** The line saying what the shelf lists, which the overview leaves out. */
  readonly detail?: boolean
  /** One row on the overview; only the Discography tab wraps. */
  readonly layout?: CatalogueGridProps['layout']
}) {
  // A band shelf names its group and needs no sentence under it.
  const title = section.band == null ? SECTIONS[section.key].title : `With ${section.band}`

  return (
    <section className={styles.shelf} aria-labelledby={id}>
      <h2 id={id} className={styles.heading}>
        {title}
      </h2>

      {detail && section.band == null ? (
        <Text size="sm" tone="secondary" block>
          {SECTIONS[section.key].detail}
        </Text>
      ) : null}

      <CatalogueGrid layout={layout} aria-label={`${title}: ${artist}`}>
        {section.albums.map((album) => (
          <AlbumCard
            key={album.key}
            album={album}
            artist={artist}
            performers={section.key === 'composer'}
          />
        ))}
      </CatalogueGrid>
    </section>
  )
}

/** A composer's pieces, each with every recording of it the library holds. */
function Works({
  works,
  label,
}: {
  readonly works: readonly Composition[]
  readonly label: string
}) {
  return (
    <div className={styles.tracklist}>
      <Table density="cozy">
        <caption>
          <Text size="xs" tone="tertiary">
            {label}
          </Text>
        </caption>
        <thead>
          <tr>
            <TableHeaderCell>Work</TableHeaderCell>
            <TableHeaderCell numeric>Recordings</TableHeaderCell>
            <TableHeaderCell>Performed by</TableHeaderCell>
          </tr>
        </thead>
        <tbody>
          {works.map((work) => (
            <tr key={work.title}>
              <TableCell>{work.title}</TableCell>
              <TableCell numeric>
                <Text size="sm" family="mono">
                  {work.performances.length}
                </Text>
              </TableCell>
              <TableCell>
                <Stack direction="column" gap={4}>
                  {work.performances.map((performance) => (
                    <span
                      key={`${performance.releaseId ?? performance.album}:${performance.performers}`}
                    >
                      {performance.performers ?? (
                        <Text size="sm" tone="tertiary">
                          Performers not recorded
                        </Text>
                      )}{' '}
                      <Text size="xs" tone="tertiary">
                        {[
                          performance.album,
                          performance.year,
                          `${performance.tracks} ${performance.tracks === 1 ? 'track' : 'tracks'}`,
                        ]
                          .filter(Boolean)
                          .join(' · ')}
                      </Text>
                    </span>
                  ))}
                </Stack>
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
 * differs from the providers, so putting a value back undoes that edit.
 */
function EditForm({
  artistId,
  data,
  onCancel,
  onSaved,
}: {
  readonly artistId: string
  readonly data: Detail
  readonly onCancel: () => void
  readonly onSaved: () => void
}) {
  const { artist, profile } = data

  const [draft, setDraft] = useState({
    name: profile.name,
    latinName: profile.latinName ?? '',
    sortName: artist.sortName ?? '',
    disambiguation: artist.disambiguation ?? '',
    type: artist.type ?? '',
    gender: artist.gender ?? '',
    country: artist.country ?? '',
    beganYear: artist.beganYear?.toString() ?? '',
    endedYear: artist.endedYear?.toString() ?? '',
    ended: artist.ended,
    genres: artist.genres.join(', '),
    biography: profile.biography?.text ?? '',
    portrait: artist.portrait ?? '',
    banner: profile.banner ?? '',
  })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const text = (key: Exclude<keyof typeof draft, 'ended'>) => ({
    value: draft[key],
    onChange: (event: { target: { value: string } }) =>
      setDraft({ ...draft, [key]: event.target.value }),
  })

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)

    const body: EditRequest = {
      name: draft.name,
      latinName: blank(draft.latinName),
      sortName: blank(draft.sortName),
      disambiguation: blank(draft.disambiguation),
      type: blank(draft.type),
      country: blank(draft.country),
      gender: blank(draft.gender),
      beganYear: year(draft.beganYear),
      endedYear: year(draft.endedYear),
      ended: draft.ended,
      genres: list(draft.genres),
      biography: blank(draft.biography),
      portrait: blank(draft.portrait),
      banner: blank(draft.banner),
    }

    try {
      await api.post('/api/catalogue/artists/{id}/edits', {
        params: { path: { id: artistId } },
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
      <Field className={styles.wide} label="Name">
        {(control) => <Input {...control} {...text('name')} required fullWidth />}
      </Field>
      <Field
        label="Name in Latin script"
        hint="Shown instead of the name. The tags keep the original."
      >
        {(control) => <Input {...control} {...text('latinName')} fullWidth />}
      </Field>
      <Field label="Sort name">
        {(control) => <Input {...control} {...text('sortName')} fullWidth />}
      </Field>
      <Field className={styles.wide} label="Disambiguation">
        {(control) => <Input {...control} {...text('disambiguation')} fullWidth />}
      </Field>
      <Field label="Type" hint="Person, Group, Orchestra, Choir">
        {(control) => <Input {...control} {...text('type')} fullWidth />}
      </Field>
      <Field label="Gender">
        {(control) => <Input {...control} {...text('gender')} fullWidth />}
      </Field>
      <Field label="Country" hint="Two-letter code">
        {(control) => <Input {...control} {...text('country')} maxLength={2} mono fullWidth />}
      </Field>
      <Field label="Began">
        {(control) => <Input {...control} {...text('beganYear')} type="number" mono fullWidth />}
      </Field>
      <Field label="Ended">
        {(control) => <Input {...control} {...text('endedYear')} type="number" mono fullWidth />}
      </Field>
      <label className={styles.check}>
        <input
          type="checkbox"
          checked={draft.ended}
          onChange={(event) => setDraft({ ...draft, ended: event.target.checked })}
        />
        Life span is over, even undated
      </label>
      <Field className={styles.wide} label="Genres" hint="Comma-separated, most important first">
        {(control) => <Input {...control} {...text('genres')} fullWidth />}
      </Field>
      <Field
        className={styles.wide}
        label="Biography"
        hint="Blank lines split paragraphs. Written here, it is yours and no pass replaces it."
      >
        {(control) => (
          <textarea {...control} {...text('biography')} className={styles.textarea} rows={8} />
        )}
      </Field>
      <Field className={styles.wide} label="Profile picture URL">
        {(control) => <Input {...control} {...text('portrait')} type="url" mono fullWidth />}
      </Field>
      <Field className={styles.wide} label="Banner image URL" hint="Blank leaves a plain band.">
        {(control) => <Input {...control} {...text('banner')} type="url" mono fullWidth />}
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
 * Follow, which is the one control on this page that changes a fact rather than
 * a view.
 *
 * Optimistic in the narrow sense only — the flag flips after the request
 * returns, not before it — because `useApiQuery` has no invalidation and a
 * refetch of the whole artist would cost a second read of every track to redraw
 * one word. The state it owns is one boolean the server just confirmed.
 *
 * It no longer gates the discography below. Following used to be what put an
 * artist on the enrichment pass's fifth worklist, which is why that list was
 * empty for everybody else; the worklist is the whole catalogue now. What this
 * button still buys is monitoring — a record that turns up after the click is
 * wanted by default, and one already in the back catalogue is not.
 */
function FollowButton({
  artistId,
  following: initial,
}: {
  readonly artistId: string
  readonly following: boolean
}) {
  const [following, setFollowing] = useState(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggle = async () => {
    const next = !following

    setBusy(true)

    try {
      await api.post('/api/catalogue/artists/{id}/follow', {
        params: { path: { id: artistId } },
        json: { follow: next },
      })

      setFollowing(next)
      setError(null)
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack direction="column" gap={4} align="start">
      <Button
        variant={following ? 'secondary' : 'primary'}
        disabled={busy}
        onClick={() => void toggle()}
      >
        {following ? 'Following' : 'Follow'}
      </Button>

      {error != null ? (
        <Text size="sm" tone="warning">
          {error}
        </Text>
      ) : null}
    </Stack>
  )
}

/**
 * The records they made that no file here is under.
 *
 * **Three states, and telling them apart is the whole job.** Nobody has asked
 * MusicBrainz yet; it was asked and the library holds everything; it was asked
 * and there are gaps. Collapsing the first into the second would report a
 * complete collection to somebody who has simply not run the pass — which is
 * why `fetchedAtUtc` is on the wire at all, and the same reason
 * `describedAtUtc` is.
 *
 * `known` and `held` travel beside the list because the list is *cut*:
 * `Discography.IsGap` hides compilations and the rest, so its length is not the
 * number of records missing and saying "7 missing of 41" from it would be
 * wrong in both figures.
 */
function MissingRecords({
  discography,
  artist,
  id,
}: {
  readonly discography: components['schemas']['ArtistDiscography']
  readonly artist: string
  readonly id: string
}) {
  return (
    <section className={styles.shelf} aria-labelledby={id}>
      <h2 id={id} className={styles.heading}>
        Not in your library
      </h2>

      {discography.fetchedAtUtc == null ? (
        // Nothing auto-starts enrichment — `StartEnrichment` is the only
        // caller of `Start()` — so an unbrowsed artist means a pass nobody has
        // run yet, and saying which of the three states this is remains the
        // whole content here.
        <Text size="sm" tone="tertiary">
          Nothing listed yet — run the enrichment pass from Foundation to fetch what they released.
        </Text>
      ) : discography.missing.length === 0 ? (
        <Text size="sm" tone="tertiary">
          Nothing missing — the library holds {discography.held.toLocaleString()} of the{' '}
          {discography.known.toLocaleString()} records MusicBrainz credits to them.
        </Text>
      ) : (
        <>
          <Text size="sm" tone="secondary" block>
            Records MusicBrainz credits to them that no file here sits under — holding{' '}
            {discography.held.toLocaleString()} of {discography.known.toLocaleString()}.
          </Text>

          <CatalogueGrid aria-label={`Not in your library: ${artist}`}>
            {discography.missing.map((record) => (
              <MissingCard key={record.id} record={record} />
            ))}
          </CatalogueGrid>

          {/*
            **No shop rows here, and that is a decision rather than an
            omission.** What a shop stocks is not a record the artist made, and
            a second list beside this one is a duplicate whether it is
            interleaved or folded away — the two catalogues name the same record
            differently (MusicBrainz prints the sleeve title, a shop prints the
            composer and the works) and nothing they share settles which rows
            are the same. Measured on this library: shop barcodes match the
            catalogue's 106 times in 94,660, because a shop sells the digital
            edition under its own UPC.

            So this page answers one question from one source — what MusicBrainz
            credits to them — and whether a record can be bought belongs to the
            acquire shelf, which is where wanting one is expressed. A shop's
            answers are kept (`DiscoveredRecords`) to enrich these rows, never
            to add to them.
          */}
        </>
      )}
    </section>
  )
}

/**
 * Wanting a record, which is what keeps the acquire shelf readable.
 *
 * `FollowButton`'s counterpart one level down, and the same narrow optimism: the
 * flag flips after the request returns rather than before, because `useApiQuery`
 * has no invalidation and refetching the artist to redraw one button would cost
 * a second read of every track.
 *
 * **It lives inside the card rather than making the card a button.**
 * `MissingCard` deliberately has no `render` — there is no page for a record no
 * file is under, and inventing one would be inventing the album — so the tile is
 * not interactive and a control inside it is not a nested one. This is the
 * opposite of the acquire shelf's tiles, where the whole tile is the button
 * because there the press is a search.
 *
 * The title is in the accessible name because a page of these is otherwise a
 * column of buttons all called "Monitor".
 */
function MonitorButton({
  id,
  monitored: initial,
  title,
}: {
  readonly id: string
  readonly monitored: boolean
  readonly title: string
}) {
  const [monitored, setMonitored] = useState(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggle = async () => {
    const next = !monitored

    setBusy(true)

    try {
      await api.post('/api/catalogue/release-groups/{id}/monitor', {
        params: { path: { id } },
        json: { monitor: next },
      })

      setMonitored(next)
      setError(null)
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Button
        size="sm"
        variant={monitored ? 'secondary' : 'ghost'}
        disabled={busy}
        aria-label={`${monitored ? 'Stop wanting' : 'Want'} ${title}`}
        onClick={() => void toggle()}
      >
        {monitored ? 'Wanted' : 'Want this'}
      </Button>

      {error != null ? (
        <Text size="xs" tone="warning">
          {error}
        </Text>
      ) : null}
    </>
  )
}

/**
 * One record they made and the library has not got.
 *
 * No `render`, so it goes nowhere — there is no page for a record no file is
 * under, and inventing one would be inventing the album. The sleeve comes from
 * the Cover Art Archive keyed on the *release group*, which is the only key
 * there is here: no pressing has been chosen, because none has been owned.
 */
/**
 * Handing the application a picture of an artist, or taking it back.
 *
 * **Why an upload exists at all when there is already a URL field.** Picking a
 * URL means finding one somewhere else that is a picture of the right person
 * and will still be there next year; three providers have already been asked by
 * the time anybody opens this. An upload is the answer for the artist none of
 * them has heard of, which — measured on this library — is 52% of the catalogue.
 *
 * **It is stored beside the provider's answer, never over it** (rule 4), so
 * "Use the provider's picture" is a delete rather than a second edit, and what
 * was underneath is unharmed. It does not reach the library until tags are
 * written for this artist: putting a file into somebody's music is the tag
 * write's job and it is a button, never a consequence of a save.
 *
 * `fetch` rather than the generated client, as `CoverDialog` does it and for the
 * same reason: the body is the image and its `Content-Type` is the claim the API
 * checks, which a JSON client cannot express.
 */
function PicturePicker({
  artistId,
  kind,
  uploaded,
  onChanged,
}: {
  readonly artistId: string
  /** Which of the artist's two pictures this control is for. */
  readonly kind: 'portrait' | 'banner'
  readonly uploaded: boolean
  readonly onChanged: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function run(work: () => Promise<unknown>) {
    setBusy(true)
    setError(null)

    try {
      await work()
      onChanged()
    } catch (cause: unknown) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }

  function upload(file: File) {
    void run(async () => {
      const response = await fetch(`${apiBaseUrl}/api/catalogue/artists/${artistId}/${kind}`, {
        method: 'POST',
        headers: { 'Content-Type': file.type || 'application/octet-stream' },
        body: file,
      })

      if (!response.ok) {
        throw new Error(
          response.status === 400
            ? 'Not a picture this application will store — use a JPEG, PNG, GIF, WebP, BMP or AVIF under 10 MB.'
            : `The upload failed (${response.status}).`,
        )
      }
    })
  }

  return (
    <Stack direction="column" gap={4} align="start">
      <Stack gap={8} align="center" wrap>
        <label className={styles.upload} data-disabled={busy ? '' : undefined}>
          <input
            type="file"
            accept="image/jpeg,image/png,image/gif,image/webp,image/bmp,image/avif"
            className={styles.fileInput}
            disabled={busy}
            onChange={(event) => {
              const file = event.currentTarget.files?.[0]
              if (file !== undefined) upload(file)
              event.currentTarget.value = ''
            }}
          />
          <Text size="sm">{uploaded ? 'Replace…' : 'Upload a picture…'}</Text>
        </label>

        {uploaded ? (
          <Button
            size="sm"
            variant="ghost"
            disabled={busy}
            onClick={() =>
              void run(() =>
                kind === 'portrait'
                  ? api.delete('/api/catalogue/artists/{id}/portrait', {
                      params: { path: { id: artistId } },
                    })
                  : api.delete('/api/catalogue/artists/{id}/banner', {
                      params: { path: { id: artistId } },
                    }),
              )
            }
          >
            Remove this upload
          </Button>
        ) : null}
      </Stack>

      <div role="status" aria-live="polite" aria-busy={busy}>
        {busy ? (
          <Text size="xs" tone="tertiary">
            Saving…
          </Text>
        ) : null}
        {error !== null ? (
          <Text size="xs" tone="danger" block>
            {error}
          </Text>
        ) : null}
      </div>

      <Text size="xs" tone="tertiary">
        {uploaded
          ? "Removing it goes back to the provider's picture, but the copy already beside this artist's records stays until tags are written for them again."
          : "Written beside this artist's records the next time tags are written for them."}
      </Text>
    </Stack>
  )
}

/**
 * A shelf row's second line: its year, and what was folded into it.
 *
 * **The count is printed because the rows are real.** One artist here has 132
 * release groups titled "The Four Seasons" — different performances, all
 * correctly stored, and indistinguishable on screen because MusicBrainz bills a
 * classical recording to the composer and names no performer. Folding them into
 * one row is what makes the shelf readable; saying so is what stops it being a
 * lie, because somebody after a particular performance has to know the others
 * are there.
 */
function shelfSubtitle(year: number | null, editions: number, noun: string) {
  const dated = year != null ? String(year) : 'undated'

  return editions > 1 ? `${dated} · ${editions.toLocaleString()} ${noun}s` : dated
}

function MissingCard({ record }: { readonly record: components['schemas']['DiscographyRow'] }) {
  const image = record.mbid != null ? releaseGroupArt(record.mbid) : null

  return (
    <CatalogueCard
      variant="album"
      title={record.title}
      {...(image != null ? { image } : {})}
      subtitle={shelfSubtitle(record.firstReleaseYear, record.editions, 'recording')}
      meta={
        <>
          {record.primaryType != null ? (
            <Badge tone="neutral" size="sm">
              {record.primaryType}
            </Badge>
          ) : null}

          {record.secondaryTypes.map((type) => (
            <Badge key={type} tone="neutral" size="sm">
              {type}
            </Badge>
          ))}

          <MonitorButton id={record.id} monitored={record.monitored} title={record.title} />
        </>
      }
    />
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
function AlbumCard({
  album,
  artist,
  performers,
}: {
  readonly album: ArtistAlbum
  readonly artist: string
  /** Say who plays it, on a composer's shelf, where the billing line names the composer back. */
  readonly performers: boolean
}) {
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
        performers ? album.performers : null,
        album.year == null ? null : `${album.year}${album.yearFromFolder ? '?' : ''}`,
        // Whose record it is, printed only when it is not this artist's —
        // asking `sameArtist`, the same test the shelf is chosen by, so the
        // line and the placement cannot disagree. It is what makes a card on
        // the Collaborations shelf explain itself ("one track on a Joe
        // Bonamassa album" rather than an album of theirs that has mysteriously
        // moved), and it stays quiet where the name would only repeat the one
        // at the top of the page — including on the Composer shelf, where a
        // musician's own band is still not somebody else.
        performers || sameArtist(album, artist) ? null : album.albumArtist,
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
      {...(releaseId != null
        ? { image: releaseCover(releaseId) }
        : album.mbid != null
          ? { image: releaseArt(album.mbid) }
          : {})}
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
