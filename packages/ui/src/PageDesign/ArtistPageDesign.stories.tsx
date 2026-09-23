import type { Meta, StoryObj } from '@storybook/react-vite'
import { useId, useState } from 'react'
import { flushSync } from 'react-dom'
import { expect, userEvent } from 'storybook/test'

import { Artwork } from '../Artwork/Artwork.tsx'
import { Badge } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { CatalogueCard } from '../CatalogueCard/CatalogueCard.tsx'
import { CatalogueGrid } from '../CatalogueCard/CatalogueGrid.tsx'
import { cover } from '../CatalogueCard/fixtures.ts'
import { Disclosure } from '../Disclosure/Disclosure.tsx'
import { Field } from '../Field/Field.tsx'
import { Input } from '../Input/Input.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Table, TableCell, TableHeaderCell } from '../Table/Table.tsx'
import { Text } from '../Text/Text.tsx'
import styles from './PageDesign.module.css'
import { countryName, Edited, Prose, stamp, Tabs, Value, type Written } from './parts.tsx'

/*
  Design candidate for the artist page. Nothing here is wired to the
  application. The shape is `ArtistSummary` plus the `Artists` columns the wire
  does not carry yet (Mbid, LatinName, the three lookup
  stamps) and a banner image the catalogue has no column for.

  The Works tab a composer gets is made from what the catalogue does hold: a
  `Work` per movement. The piece a movement belongs to is the title before the
  first ": ", as `workGroups.ts` reads it, so there is no catalogue number, date
  or count of parts beyond what is in the title, and no type: `Work.Type` sits
  on the movement, where MusicBrainz rarely sets one.
*/

type Artist = {
  readonly name: string
  readonly latinName: string | null
  readonly sortName: string | null
  readonly mbid: string | null
  readonly type: string | null
  readonly disambiguation: string | null
  readonly country: string | null
  readonly gender: string | null
  readonly beganYear: number | null
  readonly endedYear: number | null
  readonly ended: boolean
  readonly genres: readonly string[]
  readonly portrait: string | null
  readonly banner: string | null
  readonly following: boolean
  readonly describedAtUtc: string | null
  readonly portraitLookupUtc: string | null
  readonly discographyLookupUtc: string | null
  readonly biography: Written | null
  readonly biographyLookupUtc: string | null
}

type Related = { readonly name: string; readonly image?: string; readonly years: string | null }

type Album = {
  readonly title: string
  readonly year: number | null
  readonly tracks: number
  readonly image?: string
  readonly roles: readonly string[]
  /** Who plays it, where the page's artist wrote it rather than played it. */
  readonly performer?: string
  readonly folder?: boolean
  readonly unplaced?: number
  readonly files?: number
}

type Missing = {
  readonly title: string
  readonly year: number | null
  readonly type: string
  readonly monitored: boolean
  readonly performer?: string
}

type Track = {
  readonly title: string
  readonly roles: readonly string[]
  readonly album: string
  readonly length: string
  readonly files: number
}

type Performance = {
  readonly performers: string
  readonly album: string
  readonly year: number | null
  /** Tracks held that perform this work. */
  readonly tracks: number
}

/** A piece, as the movements' titles name it, and every recording of it held. */
type Composition = {
  readonly title: string
  readonly performances: readonly Performance[]
}

type Profile = {
  readonly artist: Artist
  readonly shelves: readonly { readonly title: string; readonly albums: readonly Album[] }[]
  readonly missing: readonly Missing[]
  readonly known: number
  readonly tracks: readonly Track[]
  readonly members: readonly Related[]
  readonly memberOf: readonly Related[]
  /** Only for someone credited as composer. */
  readonly works?: readonly Composition[]
}

/** A wide gradient with a horizon, standing in for a photograph. */
function banner(sky: string, ground: string, sun: string): string {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 300" preserveAspectRatio="xMidYMid slice">
<defs><linearGradient id="s" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${sky}"/><stop offset="1" stop-color="${ground}"/></linearGradient></defs>
<rect width="1200" height="300" fill="url(#s)"/><circle cx="860" cy="190" r="90" fill="${sun}" opacity=".55"/>
<path d="M0 230 Q300 170 600 220 T1200 200 V300 H0Z" fill="${ground}" opacity=".85"/></svg>`
  return `data:image/svg+xml,${encodeURIComponent(svg)}`
}

const BONAMASSA: Profile = {
  artist: {
    name: 'Joe Bonamassa',
    latinName: null,
    sortName: 'Bonamassa, Joe',
    mbid: '984f8239-8fe1-4683-9c54-10ffb14439e9',
    type: 'Person',
    disambiguation: 'US blues rock guitarist',
    country: 'US',
    gender: 'Male',
    beganYear: 1977,
    endedYear: null,
    ended: false,
    genres: ['blues rock', 'electric blues', 'hard rock', 'blues'],
    portrait: cover('#1971c2', '#0b0d0e'),
    banner: banner('#1864ab', '#0b0d0e', '#f59f00'),
    following: true,
    describedAtUtc: '2026-09-02T21:14:00Z',
    portraitLookupUtc: '2026-09-02T21:15:00Z',
    discographyLookupUtc: '2026-09-03T08:40:00Z',
    biographyLookupUtc: '2026-09-03T08:41:00Z',
    biography: {
      byPerson: false,
      source: 'Wikipedia',
      url: 'https://en.wikipedia.org/wiki/Joe_Bonamassa',
      text: [
        'Joe Bonamassa is an American blues rock guitarist, singer and songwriter from Utica, New York. He began playing as a small child, was opening for B.B. King at twelve, and by his early twenties had made his first record with the band Bloodline.',
        'His solo career began in 2000 with A New Day Yesterday, and he has released well over a dozen studio albums since, most of them on his own label. Blues Deluxe, Sloe Gin and Royal Tea each topped the Billboard blues chart.',
        'Alongside the solo work he formed the hard rock group Black Country Communion with Glenn Hughes, Jason Bonham and Derek Sherinian, and made a series of soul and jazz records with the singer Beth Hart. He tours almost constantly, and a large share of his catalogue is live recordings from particular venues.',
      ].join('\n\n'),
    },
  },
  shelves: [
    {
      title: 'Discography',
      albums: [
        {
          title: 'Sloe Gin',
          year: 2007,
          tracks: 11,
          files: 13,
          unplaced: 1,
          image: cover('#1864ab', '#121416'),
          roles: ['billed'],
        },
        {
          title: 'Blues Deluxe',
          year: 2003,
          tracks: 11,
          image: cover('#5f3dc4', '#1864ab'),
          roles: ['billed'],
        },
        {
          title: 'Royal Tea',
          year: 2020,
          tracks: 10,
          image: cover('#c92a2a', '#343a40'),
          roles: ['billed'],
        },
        { title: 'Live at Carnegie Hall', year: 2021, tracks: 14, roles: ['billed'], folder: true },
      ],
    },
    {
      title: 'With Black Country Communion',
      albums: [
        {
          title: 'Black Country',
          year: 2010,
          tracks: 12,
          image: cover('#2b8a3e', '#0b0d0e'),
          roles: ['member'],
        },
      ],
    },
    {
      title: 'Collaborations',
      albums: [
        {
          title: "Don't Explain",
          year: 2011,
          tracks: 10,
          image: cover('#e67700', '#7d1a1a'),
          roles: ['billed'],
        },
      ],
    },
  ],
  missing: [
    { title: 'Dust Bowl', year: 2011, type: 'Album', monitored: true },
    { title: 'Different Shades of Blue', year: 2014, type: 'Album', monitored: false },
    { title: 'Blues of Desperation', year: 2016, type: 'Album', monitored: false },
  ],
  known: 21,
  tracks: [
    { title: 'Ball Peen Hammer', roles: ['billed'], album: 'Sloe Gin', length: '4:32', files: 1 },
    { title: 'Seagull', roles: ['billed'], album: 'Sloe Gin', length: '5:17', files: 2 },
    { title: 'Blues Deluxe', roles: ['billed'], album: 'Blues Deluxe', length: '6:49', files: 1 },
    { title: 'Black Country', roles: ['member'], album: 'Black Country', length: '3:12', files: 1 },
  ],
  members: [],
  memberOf: [
    { name: 'Black Country Communion', image: cover('#2b8a3e', '#0b0d0e'), years: '2009–' },
  ],
}

/** A group: life span over with no date, no photograph anywhere, no banner. */
const DIRE_STRAITS: Profile = {
  artist: {
    ...BONAMASSA.artist,
    name: 'Dire Straits',
    sortName: 'Dire Straits',
    mbid: '614e3804-7d34-41ba-857f-811bad7c2b7a',
    type: 'Group',
    disambiguation: null,
    country: 'GB',
    gender: null,
    beganYear: 1977,
    endedYear: null,
    ended: true,
    genres: ['rock', 'pub rock'],
    biography: {
      byPerson: false,
      source: 'Wikipedia',
      url: 'https://en.wikipedia.org/wiki/Dire_Straits',
      text: 'Dire Straits were a British rock band formed in London in 1977 by Mark Knopfler, David Knopfler, John Illsley and Pick Withers. Their spare, guitar-led sound set them apart from the punk around them, and Brothers in Arms (1985) became one of the best-selling albums of the decade. The band dissolved in 1995.',
    },
    portrait: null,
    banner: null,
    following: false,
    portraitLookupUtc: '2026-09-02T21:16:00Z',
  },
  shelves: [
    {
      title: 'Discography',
      albums: [
        {
          title: 'Brothers in Arms',
          year: 1985,
          tracks: 9,
          image: cover('#2b8a3e', '#0b0d0e'),
          roles: ['billed'],
        },
      ],
    },
  ],
  missing: [],
  known: 1,
  tracks: [
    {
      title: 'Money for Nothing',
      roles: ['billed'],
      album: 'Brothers in Arms',
      length: '8:26',
      files: 1,
    },
  ],
  members: [
    { name: 'Mark Knopfler', image: cover('#5f3dc4', '#0b0d0e'), years: '1977–1995' },
    { name: 'John Illsley', years: '1977–1995' },
    { name: 'David Knopfler', years: '1977–1980' },
    { name: 'Pick Withers', years: '1977–1982' },
  ],
  memberOf: [],
}

/** Named in a script most readers cannot read, with MusicBrainz's English alias beside it. */
const HISAISHI: Profile = {
  artist: {
    ...BONAMASSA.artist,
    name: '久石譲',
    latinName: 'Joe Hisaishi',
    sortName: 'Hisaishi, Joe',
    mbid: 'bd28d9b0-3e35-4b0c-9c7b-fb4a2b0c4f2d',
    type: 'Person',
    gender: 'Male',
    disambiguation: 'Japanese composer',
    country: 'JP',
    beganYear: 1950,
    genres: ['film score', 'classical'],
    biography: {
      byPerson: false,
      source: 'Wikipedia',
      url: 'https://en.wikipedia.org/wiki/Joe_Hisaishi',
      text: 'Joe Hisaishi is a Japanese composer and conductor, best known for scoring nearly every Studio Ghibli film by Hayao Miyazaki, from Nausicaä of the Valley of the Wind onwards.',
    },
    portrait: cover('#343a40', '#868e96'),
    banner: null,
    following: false,
  },
  shelves: [
    {
      title: 'Composer',
      albums: [
        {
          title: 'Spirited Away',
          year: 2001,
          tracks: 21,
          image: cover('#1971c2', '#e67700'),
          roles: ['composer'],
        },
      ],
    },
  ],
  missing: [],
  known: 1,
  tracks: [
    {
      title: 'One Summer’s Day',
      roles: ['composer'],
      album: 'Spirited Away',
      length: '3:00',
      files: 1,
    },
  ],
  members: [],
  memberOf: [],
}

const MRAVINSKY = 'Leningrad Philharmonic, Yevgeny Mravinsky'
const KARAJAN = 'Berliner Philharmoniker, Herbert von Karajan'

/** A composer: dead for a century, billed on other people's records, and a Works tab. */
const TCHAIKOVSKY: Profile = {
  artist: {
    name: 'Pyotr Ilyich Tchaikovsky',
    latinName: null,
    sortName: 'Tchaikovsky, Pyotr Ilyich',
    mbid: '9ddd7abc-9e1b-471d-8031-583bc6bc8be9',
    type: 'Person',
    disambiguation: null,
    country: 'RU',
    gender: 'Male',
    beganYear: 1840,
    endedYear: 1893,
    ended: true,
    genres: ['classical', 'romantic', 'ballet'],
    portrait: cover('#5c3d2e', '#1a1110'),
    banner: banner('#3b2a4a', '#0b0d0e', '#e8c07d'),
    following: false,
    describedAtUtc: '2026-09-02T21:20:00Z',
    portraitLookupUtc: '2026-09-02T21:21:00Z',
    discographyLookupUtc: '2026-09-03T08:44:00Z',
    biographyLookupUtc: '2026-09-03T08:45:00Z',
    biography: {
      byPerson: false,
      source: 'Wikipedia',
      url: 'https://en.wikipedia.org/wiki/Pyotr_Ilyich_Tchaikovsky',
      text: [
        'Pyotr Ilyich Tchaikovsky was a Russian composer of the Romantic period, born in Votkinsk in 1840. He was the first Russian composer whose music made a lasting impression abroad, and he trained at the Saint Petersburg Conservatory before teaching at the new conservatory in Moscow.',
        'For thirteen years the widow Nadezhda von Meck paid him an allowance on the condition that they never meet, which let him leave teaching and compose full time. His works include the ballets Swan Lake, The Sleeping Beauty and The Nutcracker, six symphonies, three piano concertos, the Violin Concerto, the 1812 Overture and the opera Eugene Onegin.',
        'He died in Saint Petersburg in 1893, nine days after conducting the premiere of his Sixth Symphony, the Pathétique.',
      ].join('\n\n'),
    },
  },
  shelves: [
    {
      title: 'Composer',
      albums: [
        {
          title: 'Symphonies Nos. 4, 5 & 6',
          year: 1961,
          tracks: 12,
          performer: MRAVINSKY,
          image: cover('#862e9c', '#1a1110'),
          roles: ['composer'],
        },
        {
          title: 'Nutcracker Suite · Swan Lake Suite',
          year: 1966,
          tracks: 13,
          performer: KARAJAN,
          image: cover('#c2255c', '#212529'),
          roles: ['composer'],
        },
        {
          title: 'Violin Concerto',
          year: 1957,
          tracks: 3,
          performer: 'Jascha Heifetz, Chicago Symphony Orchestra, Fritz Reiner',
          image: cover('#e67700', '#343a40'),
          roles: ['composer'],
        },
        {
          title: 'Piano Concerto No. 1',
          year: 1994,
          tracks: 3,
          performer: 'Martha Argerich, Bavarian Radio Symphony Orchestra, Kirill Kondrashin',
          image: cover('#1864ab', '#0b0d0e'),
          roles: ['composer'],
        },
        {
          title: 'Piano Concerto No. 1',
          year: 1941,
          tracks: 3,
          performer: 'Vladimir Horowitz, NBC Symphony Orchestra, Arturo Toscanini',
          roles: ['composer'],
        },
      ],
    },
  ],
  missing: [
    {
      title: 'Swan Lake',
      year: 1976,
      type: 'Album',
      monitored: false,
      performer: 'London Symphony Orchestra, André Previn',
    },
    {
      title: 'Eugene Onegin',
      year: 1974,
      type: 'Album',
      monitored: false,
      performer: 'Orchestra of the Royal Opera House, Georg Solti',
    },
  ],
  known: 7,
  tracks: [
    {
      title: 'Symphony No. 6 in B minor, op. 74 “Pathétique”: I. Adagio – Allegro non troppo',
      roles: ['composer'],
      album: 'Symphonies Nos. 4, 5 & 6',
      length: '18:10',
      files: 1,
    },
    {
      title: 'Piano Concerto No. 1 in B-flat minor, op. 23: I. Allegro non troppo e molto maestoso',
      roles: ['composer'],
      album: 'Piano Concerto No. 1',
      length: '21:12',
      files: 1,
    },
    {
      title: 'The Nutcracker Suite, op. 71a: II. Marche',
      roles: ['composer'],
      album: 'Nutcracker Suite · Swan Lake Suite',
      length: '2:23',
      files: 1,
    },
  ],
  members: [],
  memberOf: [],
  works: [
    {
      title: 'Symphony No. 4 in F minor, op. 36',
      performances: [
        { performers: MRAVINSKY, album: 'Symphonies Nos. 4, 5 & 6', year: 1961, tracks: 4 },
      ],
    },
    {
      title: 'Symphony No. 5 in E minor, op. 64',
      performances: [
        { performers: MRAVINSKY, album: 'Symphonies Nos. 4, 5 & 6', year: 1961, tracks: 4 },
      ],
    },
    {
      title: 'Symphony No. 6 in B minor, op. 74 “Pathétique”',
      performances: [
        { performers: MRAVINSKY, album: 'Symphonies Nos. 4, 5 & 6', year: 1961, tracks: 4 },
      ],
    },
    {
      title: 'Piano Concerto No. 1 in B-flat minor, op. 23',
      performances: [
        {
          performers: 'Martha Argerich, Bavarian Radio Symphony Orchestra, Kirill Kondrashin',
          album: 'Piano Concerto No. 1',
          year: 1994,
          tracks: 3,
        },
        {
          performers: 'Vladimir Horowitz, NBC Symphony Orchestra, Arturo Toscanini',
          album: 'Piano Concerto No. 1',
          year: 1941,
          tracks: 3,
        },
      ],
    },
    {
      title: 'Violin Concerto in D major, op. 35',
      performances: [
        {
          performers: 'Jascha Heifetz, Chicago Symphony Orchestra, Fritz Reiner',
          album: 'Violin Concerto',
          year: 1957,
          tracks: 3,
        },
      ],
    },
    {
      title: 'The Nutcracker Suite, op. 71a',
      performances: [
        { performers: KARAJAN, album: 'Nutcracker Suite · Swan Lake Suite', year: 1966, tracks: 8 },
      ],
    },
    {
      title: 'Swan Lake Suite, op. 20a',
      performances: [
        { performers: KARAJAN, album: 'Nutcracker Suite · Swan Lake Suite', year: 1966, tracks: 5 },
      ],
    },
  ],
}

/** Credited on a file and never asked about: every "not asked yet" at once. */
const UNDESCRIBED: Profile = {
  artist: {
    name: 'Anton Fig',
    latinName: null,
    sortName: null,
    mbid: null,
    type: null,
    disambiguation: null,
    country: null,
    gender: null,
    beganYear: null,
    endedYear: null,
    ended: false,
    genres: [],
    portrait: null,
    banner: null,
    following: false,
    describedAtUtc: null,
    portraitLookupUtc: null,
    discographyLookupUtc: null,
    biography: null,
    biographyLookupUtc: null,
  },
  shelves: [
    {
      title: 'Collaborations',
      albums: [
        {
          title: 'Sloe Gin',
          year: 2007,
          tracks: 11,
          image: cover('#1864ab', '#121416'),
          roles: ['member'],
        },
      ],
    },
  ],
  missing: [],
  known: 0,
  tracks: [{ title: 'Seagull', roles: ['member'], album: 'Sloe Gin', length: '5:17', files: 1 }],
  members: [],
  memberOf: [],
}

const ROLE_TONE: Readonly<Record<string, 'accent' | 'info' | 'neutral'>> = {
  billed: 'accent',
  conductor: 'info',
  ensemble: 'info',
  composer: 'neutral',
  member: 'info',
}

function lifeSpan({ beganYear, endedYear, ended }: Artist): string | null {
  if (beganYear == null && endedYear == null) return null
  if (beganYear == null) return `–${endedYear}`
  if (endedYear != null) return `${beganYear}–${endedYear}`
  return ended ? `${beganYear}–?` : `${beganYear}–`
}

const TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'biography', label: 'Biography' },
  { key: 'discography', label: 'Discography' },
] as const

const COMPOSER_TABS = [TABS[0], TABS[1], { key: 'works', label: 'Works' }, TABS[2]] as const

type TabKey = (typeof COMPOSER_TABS)[number]['key']

/** A composer's pieces, each with every recording of it the library holds. */
function Works({
  works,
  label,
}: {
  readonly works: readonly Composition[]
  readonly label: string
}) {
  return (
    <div className={styles.tracks}>
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
                    <span key={`${performance.performers}:${performance.year}`}>
                      {performance.performers}{' '}
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

function WantButton({ record }: { readonly record: Missing }) {
  const [monitored, setMonitored] = useState(record.monitored)
  return (
    <Button
      size="sm"
      variant={monitored ? 'secondary' : 'ghost'}
      aria-label={`${monitored ? 'Stop wanting' : 'Want'} ${record.title}`}
      onClick={() => setMonitored(!monitored)}
    >
      {monitored ? 'Wanted' : 'Want this'}
    </Button>
  )
}

function ArtistPage({
  profile,
  editing: startEditing = false,
}: {
  readonly profile: Profile
  readonly editing?: boolean
}) {
  const source = profile.artist
  const [artist, setArtist] = useState(source)
  const [draft, setDraft] = useState(source)
  const [editing, setEditing] = useState(startEditing)
  const [following, setFollowing] = useState(source.following)
  const [tab, setTab] = useState<TabKey>(startEditing ? 'biography' : 'overview')

  const id = useId()
  const asked = artist.describedAtUtc != null
  const shown = artist.latinName ?? artist.name
  const facts = [countryName(artist.country), lifeSpan(artist), artist.gender].filter(
    (fact): fact is string => fact != null,
  )
  const held = profile.known - profile.missing.length
  // Most recorded first; the sort is stable, so equals keep the given order.
  const works = [...(profile.works ?? [])].sort(
    (a, b) => b.performances.length - a.performances.length,
  )
  const changed = (key: keyof Artist) => JSON.stringify(artist[key]) !== JSON.stringify(source[key])

  const set = (key: keyof Artist) => (event: { target: { value: string } }) =>
    setDraft({ ...draft, [key]: event.target.value === '' ? null : event.target.value })
  const setYear = (key: 'beganYear' | 'endedYear') => (event: { target: { value: string } }) =>
    setDraft({ ...draft, [key]: event.target.value === '' ? null : Number(event.target.value) })

  // The banner falls back to the portrait, blurred, so a page without one is
  // still a hero rather than a grey slab.
  const bannerImage = artist.banner ?? artist.portrait

  return (
    <article className={styles.page} aria-labelledby={`${id}name`}>
      <header>
        <div className={styles.banner} {...(artist.banner == null ? { 'data-fallback': '' } : {})}>
          {bannerImage != null ? <img src={bannerImage} alt="" /> : null}
        </div>

        <div className={styles.identity}>
          <div className={styles.portrait}>
            <Artwork
              name={shown}
              shape="circle"
              size="fill"
              {...(artist.portrait != null ? { src: artist.portrait } : {})}
            />
          </div>

          <div className={styles.names}>
            <h1 id={`${id}name`} className={styles.title}>
              {shown}
            </h1>

            {artist.latinName != null ? (
              <Text size="md" tone="secondary">
                {artist.name}
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

          <Stack gap={8} wrap>
            <Button
              variant={following ? 'secondary' : 'primary'}
              aria-pressed={following}
              onClick={() => setFollowing(!following)}
            >
              {following ? 'Following' : 'Follow'}
            </Button>
            <Button
              variant="secondary"
              aria-pressed={editing}
              onClick={() => {
                setDraft(artist)
                setEditing(!editing)
                setTab('biography')
              }}
            >
              Edit
            </Button>
            <Button variant="ghost">Write tags</Button>
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
                    written={artist.biography}
                    asked={artist.biographyLookupUtc != null}
                    none="No biography found for them."
                    onMore={() => {
                      // The button leaves with the Overview panel; focus follows the reader.
                      flushSync(() => setTab('biography'))
                      document.getElementById(`${id}bioFull`)?.focus()
                    }}
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
                    <Button
                      size="sm"
                      variant="ghost"
                      onClick={() => {
                        flushSync(() => setTab('works'))
                        document.getElementById(`${id}works`)?.focus()
                      }}
                    >
                      All {works.length} works
                    </Button>
                  </Stack>
                </section>
              ) : null}

              {profile.shelves.slice(0, 1).map((shelf, index) => (
                <section key={shelf.title} aria-labelledby={`${id}shelf${index}`}>
                  <Stack direction="column" gap={8}>
                    <h2 id={`${id}shelf${index}`} className={styles.heading}>
                      {shelf.title}
                    </h2>
                    <CatalogueGrid layout="shelf" aria-label={`${shelf.title}: ${shown}`}>
                      {shelf.albums.map((album) => (
                        <CatalogueCard
                          key={`${album.title}:${album.year}`}
                          variant="album"
                          title={album.title}
                          subtitle={[album.performer, album.year, `${album.tracks} tracks`]
                            .filter(Boolean)
                            .join(' · ')}
                          {...(album.image != null ? { image: album.image } : {})}
                          meta={
                            <>
                              {album.folder ? (
                                <Badge tone="warning" size="sm">
                                  folder
                                </Badge>
                              ) : null}
                              {album.unplaced ? (
                                <Badge tone="warning" size="sm" mono>
                                  {album.unplaced} unplaced
                                </Badge>
                              ) : null}
                              {album.files != null && album.files > album.tracks ? (
                                <Badge tone="neutral" size="sm" mono>
                                  {album.files} files
                                </Badge>
                              ) : null}
                              {album.roles.map((role) => (
                                <Badge key={role} tone={ROLE_TONE[role] ?? 'neutral'} size="sm">
                                  {role}
                                </Badge>
                              ))}
                            </>
                          }
                        />
                      ))}
                    </CatalogueGrid>
                  </Stack>
                </section>
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
                        {people.map((person) => (
                          <CatalogueCard
                            key={person.name}
                            variant="artist"
                            title={person.name}
                            {...(person.years != null ? { subtitle: person.years } : {})}
                            {...(person.image != null ? { image: person.image } : {})}
                          />
                        ))}
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
                    written={artist.biography}
                    asked={artist.biographyLookupUtc != null}
                    none="No biography found for them."
                  />
                </Stack>
              </section>

              <aside id={`${id}about`} className={styles.panel} aria-labelledby={`${id}abouth`}>
                <h2 id={`${id}abouth`} className={styles.heading}>
                  {editing ? 'Edit artist' : 'About'}
                </h2>

                {editing ? (
                  <form
                    className={styles.form}
                    onSubmit={(event) => {
                      event.preventDefault()
                      setArtist(draft)
                      setEditing(false)
                    }}
                  >
                    <Field className={styles.wide} label="Name">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.name}
                          onChange={set('name')}
                          required
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field
                      label="Name in Latin script"
                      hint="Shown instead of the name. The tags keep the original."
                    >
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.latinName ?? ''}
                          onChange={set('latinName')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Sort name">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.sortName ?? ''}
                          onChange={set('sortName')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field className={styles.wide} label="Disambiguation">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.disambiguation ?? ''}
                          onChange={set('disambiguation')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Type" hint="Person, Group, Orchestra, Choir">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.type ?? ''}
                          onChange={set('type')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Gender">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.gender ?? ''}
                          onChange={set('gender')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Country" hint="Two-letter code">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.country ?? ''}
                          onChange={set('country')}
                          maxLength={2}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Began">
                      {(control) => (
                        <Input
                          {...control}
                          type="number"
                          value={draft.beganYear ?? ''}
                          onChange={setYear('beganYear')}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Ended">
                      {(control) => (
                        <Input
                          {...control}
                          type="number"
                          value={draft.endedYear ?? ''}
                          onChange={setYear('endedYear')}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <label className={styles.check}>
                      <input
                        type="checkbox"
                        checked={draft.ended}
                        onChange={(event) => setDraft({ ...draft, ended: event.target.checked })}
                      />
                      Life span is over, even undated
                    </label>
                    <Field
                      className={styles.wide}
                      label="Genres"
                      hint="Comma-separated, most important first"
                    >
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.genres.join(', ')}
                          onChange={(event) =>
                            setDraft({
                              ...draft,
                              genres: event.target.value
                                .split(',')
                                .map((genre) => genre.trim())
                                .filter(Boolean),
                            })
                          }
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field
                      className={styles.wide}
                      label="Biography"
                      hint="Blank paragraphs split it. Written here, it is yours and no pass replaces it."
                    >
                      {(control) => (
                        <textarea
                          {...control}
                          className={styles.textarea}
                          rows={8}
                          value={draft.biography?.text ?? ''}
                          onChange={(event) =>
                            setDraft({
                              ...draft,
                              biography:
                                event.target.value === ''
                                  ? null
                                  : {
                                      text: event.target.value,
                                      source: 'You',
                                      url: null,
                                      byPerson: true,
                                    },
                            })
                          }
                        />
                      )}
                    </Field>
                    <Field className={styles.wide} label="Profile picture URL">
                      {(control) => (
                        <Input
                          {...control}
                          type="url"
                          value={draft.portrait ?? ''}
                          onChange={set('portrait')}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field
                      className={styles.wide}
                      label="Banner image URL"
                      hint="Blank uses the profile picture, blurred."
                    >
                      {(control) => (
                        <Input
                          {...control}
                          type="url"
                          value={draft.banner ?? ''}
                          onChange={set('banner')}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Stack className={styles.wide} gap={8} justify="end">
                      <Button type="button" variant="ghost" onClick={() => setEditing(false)}>
                        Cancel
                      </Button>
                      <Button type="submit">Save</Button>
                    </Stack>
                  </form>
                ) : (
                  <>
                    <dl className={styles.facts}>
                      <dt>Name</dt>
                      <dd>
                        {artist.name}
                        <Edited by={changed('name')} />
                      </dd>
                      <dt>Latin script</dt>
                      <dd>
                        {artist.latinName ?? (
                          <Text size="sm" tone="tertiary">
                            Same as name
                          </Text>
                        )}
                        <Edited by={changed('latinName')} />
                      </dd>
                      <dt>Sort name</dt>
                      <dd>
                        <Value value={artist.sortName} asked={asked} />
                        <Edited by={changed('sortName')} />
                      </dd>
                      <dt>Type</dt>
                      <dd>
                        <Value value={artist.type} asked={asked} />
                        <Edited by={changed('type')} />
                      </dd>
                      <dt>Disambiguation</dt>
                      <dd>
                        <Value value={artist.disambiguation} asked={asked} />
                        <Edited by={changed('disambiguation')} />
                      </dd>
                      <dt>Country</dt>
                      <dd>
                        <Value value={countryName(artist.country)} asked={asked} />
                        <Edited by={changed('country')} />
                      </dd>
                      <dt>Gender</dt>
                      <dd>
                        <Value value={artist.gender} asked={asked} />
                        <Edited by={changed('gender')} />
                      </dd>
                      <dt>Life span</dt>
                      <dd>
                        <Value value={lifeSpan(artist)} asked={asked} />

                        <Edited
                          by={changed('beganYear') || changed('endedYear') || changed('ended')}
                        />
                      </dd>
                      <dt>Genres</dt>
                      <dd>
                        <Value value={artist.genres.join(', ')} asked={asked} />
                        <Edited by={changed('genres')} />
                      </dd>
                      <dt>Biography</dt>
                      <dd>
                        <Value
                          value={
                            artist.biography == null
                              ? null
                              : artist.biography.byPerson
                                ? 'Written by you'
                                : artist.biography.source
                          }
                          asked={artist.biographyLookupUtc != null}
                        />
                        <Edited by={changed('biography')} />
                      </dd>
                      <dt>MusicBrainz</dt>
                      <dd>
                        {artist.mbid != null ? (
                          <a
                            className={styles.link}
                            href={`https://musicbrainz.org/artist/${artist.mbid}`}
                          >
                            <Text size="sm" family="mono">
                              {artist.mbid}
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
                        {artist.portrait != null ? (
                          <a className={styles.link} href={artist.portrait}>
                            View image
                          </a>
                        ) : (
                          <Value value={null} asked={artist.portraitLookupUtc != null} />
                        )}
                        <Edited by={changed('portrait')} />
                      </dd>
                      <dt>Banner</dt>
                      <dd>
                        {artist.banner == null ? (
                          <Text size="sm" tone="tertiary">
                            {artist.portrait == null ? 'None' : 'Profile picture, blurred'}
                          </Text>
                        ) : (
                          'Own image'
                        )}
                        <Edited by={changed('banner')} />
                      </dd>
                      <dt>Following</dt>
                      <dd>{following ? 'Yes — new records are wanted' : 'No'}</dd>
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
                      <dd>{stamp(artist.biographyLookupUtc)}</dd>
                      <dt>Picture looked for</dt>
                      <dd>{stamp(artist.portraitLookupUtc)}</dd>
                      <dt>Discography browsed</dt>
                      <dd>{stamp(artist.discographyLookupUtc)}</dd>
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
                    {works.length} works in your library, gathered from the movements your files
                    perform.
                  </Text>
                  <Works works={works} label={`Works by ${shown}`} />
                </Stack>
              </section>
            </div>
          ) : (
            <div className={styles.main}>
              {profile.shelves.map((shelf, index) => (
                <section key={shelf.title} aria-labelledby={`${id}shelf${index}`}>
                  <Stack direction="column" gap={8}>
                    <h2 id={`${id}shelf${index}`} className={styles.heading}>
                      {shelf.title}
                    </h2>
                    <CatalogueGrid aria-label={`${shelf.title}: ${shown}`}>
                      {shelf.albums.map((album) => (
                        <CatalogueCard
                          key={`${album.title}:${album.year}`}
                          variant="album"
                          title={album.title}
                          subtitle={[album.performer, album.year, `${album.tracks} tracks`]
                            .filter(Boolean)
                            .join(' · ')}
                          {...(album.image != null ? { image: album.image } : {})}
                          meta={
                            <>
                              {album.folder ? (
                                <Badge tone="warning" size="sm">
                                  folder
                                </Badge>
                              ) : null}
                              {album.unplaced ? (
                                <Badge tone="warning" size="sm" mono>
                                  {album.unplaced} unplaced
                                </Badge>
                              ) : null}
                              {album.files != null && album.files > album.tracks ? (
                                <Badge tone="neutral" size="sm" mono>
                                  {album.files} files
                                </Badge>
                              ) : null}
                              {album.roles.map((role) => (
                                <Badge key={role} tone={ROLE_TONE[role] ?? 'neutral'} size="sm">
                                  {role}
                                </Badge>
                              ))}
                            </>
                          }
                        />
                      ))}
                    </CatalogueGrid>
                  </Stack>
                </section>
              ))}

              <section aria-labelledby={`${id}missing`}>
                <Stack direction="column" gap={8}>
                  <h2 id={`${id}missing`} className={styles.heading}>
                    Not in your library
                  </h2>
                  {artist.discographyLookupUtc == null ? (
                    <Text size="sm" tone="tertiary">
                      Nothing listed yet — run the enrichment pass to fetch what they released.
                    </Text>
                  ) : profile.missing.length === 0 ? (
                    <Text size="sm" tone="tertiary">
                      Nothing missing — the library holds {held} of the {profile.known} records
                      MusicBrainz credits to them.
                    </Text>
                  ) : (
                    <CatalogueGrid aria-label={`Not in your library: ${shown}`}>
                      {profile.missing.map((record) => (
                        <CatalogueCard
                          key={record.title}
                          variant="album"
                          title={record.title}
                          subtitle={[record.performer, record.year ?? 'undated']
                            .filter(Boolean)
                            .join(' · ')}
                          meta={
                            <>
                              <Badge tone="neutral" size="sm">
                                {record.type}
                              </Badge>
                              <WantButton record={record} />
                            </>
                          }
                        />
                      ))}
                    </CatalogueGrid>
                  )}
                </Stack>
              </section>

              <Disclosure
                size="sm"
                summary={
                  <Text size="sm" weight="medium">
                    Track by track
                  </Text>
                }
              >
                <div className={styles.tracks}>
                  <Table density="cozy">
                    <caption>
                      <Text size="xs" tone="tertiary">
                        Tracks credited to {shown}
                      </Text>
                    </caption>
                    <thead>
                      <tr>
                        <TableHeaderCell>Track</TableHeaderCell>
                        <TableHeaderCell>Credited as</TableHeaderCell>
                        <TableHeaderCell>Album</TableHeaderCell>
                        <TableHeaderCell numeric>Length</TableHeaderCell>
                        <TableHeaderCell numeric>Files</TableHeaderCell>
                      </tr>
                    </thead>
                    <tbody>
                      {profile.tracks.map((track) => (
                        <tr key={track.title}>
                          <TableCell>{track.title}</TableCell>
                          <TableCell>
                            <Stack gap={4} wrap>
                              {track.roles.map((role) => (
                                <Badge key={role} tone={ROLE_TONE[role] ?? 'neutral'} size="sm">
                                  {role}
                                </Badge>
                              ))}
                            </Stack>
                          </TableCell>
                          <TableCell>{track.album}</TableCell>
                          <TableCell numeric>
                            <Text size="sm" family="mono">
                              {track.length}
                            </Text>
                          </TableCell>
                          <TableCell numeric>
                            <Text
                              size="sm"
                              family="mono"
                              tone={track.files > 1 ? 'warning' : 'tertiary'}
                            >
                              {track.files}
                            </Text>
                          </TableCell>
                        </tr>
                      ))}
                    </tbody>
                  </Table>
                </div>
              </Disclosure>
            </div>
          )
        }
      />
    </article>
  )
}

const meta = {
  title: 'Pages/Artist design',
  component: ArtistPage,
  parameters: { layout: 'fullscreen' },
  args: { profile: BONAMASSA },
} satisfies Meta<typeof ArtistPage>

export default meta
type Story = StoryObj<typeof meta>

/** Everything the catalogue holds: portrait, banner, a full description and a browsed discography. */
export const Person: Story = {
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('tab', { name: 'Overview' }))
    await userEvent.keyboard('{ArrowRight}')
    await expect(canvas.getByRole('tab', { name: 'Biography' })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    await expect(canvas.getByRole('tab', { name: 'Biography' })).toHaveFocus()
    await expect(canvas.getByRole('tabpanel', { name: 'Biography' })).toBeVisible()
    await userEvent.keyboard('{End}')
    await expect(canvas.getByRole('tabpanel', { name: 'Discography' })).toBeVisible()
    await userEvent.keyboard('{Home}')
    await expect(canvas.getByRole('tabpanel', { name: 'Overview' })).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Read more' }))
    await expect(canvas.getByRole('heading', { name: 'Biography' })).toHaveFocus()
  },
}

/** No banner and no picture: the hero falls back to the monogram on a plain band. */
export const Group: Story = { args: { profile: DIRE_STRAITS } }

/** The English alias is the heading; the name MusicBrainz holds sits under it. */
export const NonLatinName: Story = { args: { profile: HISAISHI } }

/**
 * A composer gets a Works tab, and every album says who plays it. "Not in your
 * library" is other people's recordings, because MusicBrainz bills the composer.
 */
export const Composer: Story = {
  args: { profile: TCHAIKOVSKY },
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'All 7 works' }))
    await expect(canvas.getByRole('heading', { name: 'Works' })).toHaveFocus()
    await expect(canvas.getByRole('tab', { name: 'Works' })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    const table = canvas.getByRole('table', { name: 'Works by Pyotr Ilyich Tchaikovsky' })
    await expect(table.querySelector('tbody tr td')).toHaveTextContent(
      'Piano Concerto No. 1 in B-flat minor, op. 23',
    )
  },
}

/** Credited on a file, never looked up: every field says "not asked yet", none says "none". */
export const NotYetDescribed: Story = { args: { profile: UNDESCRIBED } }

/** Editing is the About panel turned into a form; a saved change is marked as set by a person. */
export const Editing: Story = {
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Edit' }))
    const sortName = canvas.getByLabelText('Sort name')
    await userEvent.clear(sortName)
    await userEvent.type(sortName, 'Bonamassa, Joseph')
    await userEvent.click(canvas.getByRole('button', { name: 'Save' }))
    await expect(canvas.getByText('Bonamassa, Joseph')).toBeVisible()
    await expect(canvas.getByText('set by you')).toBeVisible()
  },
}
