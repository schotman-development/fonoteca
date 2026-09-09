import assert from 'node:assert/strict'
import { test } from 'node:test'

import { albumsOf, fromFolder, sectionOf, sectionsOf } from './artistAlbums.ts'

const track = (over: Partial<Parameters<typeof albumsOf>[0][number]>) => ({
  recordingId: crypto.randomUUID(),
  title: 'A track',
  workTitle: null,
  duration: '3:20',
  roles: ['billed'],
  album: null,
  folder: 'An Artist/An Album',
  files: [{ path: 'An Artist/An Album/01.flac', sizeBytes: 1 }],
  ...over,
})

const album = (over: Record<string, unknown> = {}) => ({
  releaseId: 'r-1',
  mbid: 'm-1',
  title: 'Rumours',
  year: 1977,
  artist: 'Fleetwood Mac',
  billed: true,
  band: null,
  ...over,
})

test('a year is read off the folder however it was written', () => {
  assert.deepEqual(fromFolder('X/Rumours (1977)'), { title: 'Rumours', year: 1977 })
  assert.deepEqual(fromFolder('X/[1977] Rumours'), { title: 'Rumours', year: 1977 })
  assert.deepEqual(fromFolder('X/Rumours - 1977'), { title: 'Rumours', year: 1977 })
  assert.deepEqual(fromFolder('X/1977 Rumours'), { title: 'Rumours', year: 1977 })
})

test('a number that is not a year stays in the title', () => {
  assert.deepEqual(fromFolder('X/Blink-182'), { title: 'Blink-182', year: null })
  assert.deepEqual(fromFolder('X/Live At Pompeii'), { title: 'Live At Pompeii', year: null })
})

test('tracks of one release become one album, files and roles summed', () => {
  const albums = albumsOf([
    track({ album: album(), roles: ['billed'] }),
    track({
      album: album(),
      roles: ['conductor'],
      files: [
        { path: 'a.flac', sizeBytes: 1 },
        { path: 'a.mp3', sizeBytes: 1 },
      ],
    }),
  ])

  assert.equal(albums.length, 1)
  assert.deepEqual(albums[0]?.roles, ['billed', 'conductor'])
  assert.equal(albums[0]?.trackCount, 2)
  assert.equal(albums[0]?.fileCount, 3)
  assert.equal(albums[0]?.releaseId, 'r-1')
})

test('an unattributed track groups by its folder and says the year came from there', () => {
  const albums = albumsOf([track({ album: null, folder: 'An Artist/Tusk (1979)' })])

  assert.equal(albums[0]?.releaseId, null)
  assert.equal(albums[0]?.title, 'Tusk')
  assert.equal(albums[0]?.year, 1979)
  assert.equal(albums[0]?.yearFromFolder, true)
})

test('the folder never overrules a year the catalogue has', () => {
  const albums = albumsOf([track({ album: album({ year: 2015 }), folder: 'X/Rumours (1977)' })])

  assert.equal(albums[0]?.year, 2015)
  assert.equal(albums[0]?.yearFromFolder, false)
})

test('the folder fills a year the catalogue has not', () => {
  const albums = albumsOf([track({ album: album({ year: null }), folder: 'X/Rumours (1977)' })])

  assert.equal(albums[0]?.year, 1977)
  assert.equal(albums[0]?.yearFromFolder, true)
})

test('the discs of one rip are one folder, not two', () => {
  const albums = albumsOf([
    track({ album: null, folder: 'X/Rumours (1977)/CD2' }),
    track({ album: null, folder: 'X/Rumours (1977)/CD1' }),
  ])

  assert.equal(albums.length, 1)
  assert.equal(albums[0]?.title, 'Rumours')
  assert.deepEqual(albums[0]?.folders, ['X/Rumours (1977)'])
})

test('genuinely different folders under one release are both kept', () => {
  const albums = albumsOf([
    track({ album: album(), folder: 'X/Rumours (Remaster)' }),
    track({ album: album(), folder: 'X/Rumours' }),
  ])

  assert.deepEqual(albums[0]?.folders, ['X/Rumours', 'X/Rumours (Remaster)'])
})

test('the discography is ordered by year, undated last', () => {
  const albums = albumsOf([
    track({ album: album({ releaseId: 'c', title: 'C', year: null }) }),
    track({ album: album({ releaseId: 'b', title: 'B', year: 1979 }) }),
    track({ album: album({ releaseId: 'a', title: 'A', year: 1977 }) }),
  ])

  assert.deepEqual(
    albums.map((a) => a.title),
    ['A', 'B', 'C'],
  )
})

test('leftovers fold into the album they are missing from', () => {
  const albums = albumsOf([
    track({ album: album(), folder: 'X/Rumours (1977)', roles: ['billed'] }),
    track({ album: album(), folder: 'X/Rumours (1977)', roles: ['billed'] }),
    track({ album: null, folder: 'X/Rumours (1977)', roles: ['composer'] }),
  ])

  assert.equal(albums.length, 1)
  assert.equal(albums[0]?.releaseId, 'r-1')
  assert.equal(albums[0]?.trackCount, 3)
  assert.equal(albums[0]?.unplaced, 1)
  // The roles came with them.
  assert.deepEqual(albums[0]?.roles, ['billed', 'composer'])
})

test('a release that only reprints one of the folder does not swallow it', () => {
  const albums = albumsOf([
    // A compilation with one track in somebody else's album folder, and the
    // twenty the passes could not place. The compilation is the folder's only
    // claimant and is emphatically not its album.
    track({ album: album({ releaseId: 'comp', title: 'Free Album 2017' }), folder: 'X/Vienna' }),
    ...Array.from({ length: 20 }, () => track({ album: null, folder: 'X/Vienna' })),
  ])

  assert.equal(albums.length, 2)
  assert.equal(albums.find((a) => a.releaseId === 'comp')?.trackCount, 1)
  assert.equal(albums.find((a) => a.releaseId === null)?.trackCount, 20)
})

test('the bigger of two claimants takes the leftovers', () => {
  const albums = albumsOf([
    ...Array.from({ length: 9 }, () =>
      track({ album: album({ releaseId: 'lp', title: 'The LP' }), folder: 'X/The LP' }),
    ),
    track({ album: album({ releaseId: 'comp', title: 'Hits' }), folder: 'X/The LP' }),
    track({ album: null, folder: 'X/The LP' }),
  ])

  assert.equal(albums.find((a) => a.releaseId === 'lp')?.unplaced, 1)
  assert.equal(
    albums.find((a) => a.releaseId === null),
    undefined,
  )
})

test('a folder two releases claim equally is left standing on its own', () => {
  const albums = albumsOf([
    track({ album: album({ releaseId: 'a', title: 'A' }), folder: 'X/Deluxe (1977)' }),
    track({ album: album({ releaseId: 'a', title: 'A' }), folder: 'X/Deluxe (1977)' }),
    track({ album: album({ releaseId: 'b', title: 'B' }), folder: 'X/Deluxe (1977)' }),
    track({ album: album({ releaseId: 'b', title: 'B' }), folder: 'X/Deluxe (1977)' }),
    track({ album: null, folder: 'X/Deluxe (1977)' }),
  ])

  assert.equal(albums.length, 3)
  assert.equal(albums.filter((a) => a.releaseId === null).length, 1)
})

test('a folder nothing was placed from stays an album of its own', () => {
  const albums = albumsOf([track({ album: null, folder: 'X/Tusk (1979)' })])

  assert.equal(albums.length, 1)
  assert.equal(albums[0]?.unplaced, 1)
})

test('a year inside a longer parenthetical is not the album year', () => {
  // The bracket has to close on the year itself. These are ripper conventions,
  // and the earlier rule ate the opening bracket and left the closing one.
  assert.deepEqual(fromFolder('X/Off the Wall (2015 Remaster)'), {
    title: 'Off the Wall (2015 Remaster)',
    year: null,
  })
  assert.deepEqual(fromFolder('X/1917 (Original Score)'), {
    title: '1917 (Original Score)',
    year: null,
  })
  // The library's own convention, which the same rule has to keep: a leading
  // year before a dash is a year here, and `2001 - A Space Odyssey` losing its
  // name is the price. Measured — five real folders against nothing.
  assert.deepEqual(fromFolder('X/2019 - Texas Honey'), {
    title: 'Texas Honey',
    year: 2019,
  })
  assert.deepEqual(fromFolder('X/Ballads & Blues 1982-1994 (1994)'), {
    title: 'Ballads & Blues 1982-1994',
    year: 1994,
  })
})

test("a rip's note about itself is not part of the album title", () => {
  assert.deepEqual(fromFolder('X/Beethoven: Symphonies Nos. 1-9 (2020) [FLAC 24-44.1]'), {
    title: 'Beethoven: Symphonies Nos. 1-9',
    year: 2020,
  })
  assert.deepEqual(fromFolder('X/Live From Nowhere in Particular (Live)'), {
    title: 'Live From Nowhere in Particular (Live)',
    year: null,
  })
})

test('among the roles, an album is filed under the strongest claim its tracks make', () => {
  // On somebody else's release, so the role is what decides. An artist on the
  // release's own billing line is their record whatever the track role says,
  // which the test below covers.
  const shelf = (roles: string[]) =>
    albumsOf([track({ album: album({ artist: 'Someone Else', billed: false }), roles })]).map((a) =>
      sectionOf(a, 'An Artist'),
    )[0]

  assert.equal(shelf(['composer', 'billed']), 'collaborations')
  assert.equal(shelf(['composer', 'ensemble']), 'collaborations')
  assert.equal(shelf(['conductor']), 'collaborations')
  assert.equal(shelf(['composer']), 'composer')
})

test('the release billing line outranks a performing track role', () => {
  // A conductor or an orchestra printed on the sleeve is looking at their own
  // record, not a booking. Only the release credit can say so — the recording
  // relation says "conductor" either way.
  const shelf = (roles: string[]) =>
    albumsOf([track({ album: album({ billed: true }), roles })]).map((a) =>
      sectionOf(a, 'An Artist'),
    )[0]

  assert.equal(shelf(['conductor']), 'discography')
  assert.equal(shelf(['ensemble']), 'discography')
  assert.equal(shelf(['billed']), 'discography')
})

test('writing it is not making it, whoever the sleeve bills first', () => {
  // MusicBrainz bills a classical release to the composer: a Perlman/Ashkenazy
  // record reads "Beethoven; Perlman, Ashkenazy", and a rule that reads the
  // billing line first files it as Beethoven's own record. 27 albums on the
  // target library are this shape, and they are the whole point of the shelf
  // they were being taken out of.
  const billedFirst = albumsOf([
    track({
      roles: ['composer'],
      album: album({ artist: 'Beethoven; Perlman, Ashkenazy', billed: true }),
    }),
  ])

  // And where the composer's own name contains the whole billing line, which
  // is what `sameArtist` would otherwise call their record.
  const named = albumsOf([
    track({
      roles: ['composer'],
      album: album({
        artist: 'Ludwig van Beethoven, Koninklijk Concertgebouworkest',
        billed: true,
      }),
    }),
  ])

  assert.deepEqual(
    billedFirst.map((a) => sectionOf(a, 'Ludwig van Beethoven')),
    ['composer'],
  )
  assert.deepEqual(
    named.map((a) => sectionOf(a, 'Ludwig van Beethoven')),
    ['composer'],
  )
})

test('a composer who also played on it is not filed as a cover', () => {
  const own = albumsOf([track({ roles: ['composer', 'billed'], album: album({ billed: true }) })])

  assert.deepEqual(
    own.map((a) => sectionOf(a, 'An Artist')),
    ['discography'],
  )
})

test('the shelves come back in order, and an empty one is dropped', () => {
  const sections = sectionsOf(
    albumsOf([
      track({
        album: album({ releaseId: 'r-2', title: 'A Cover', artist: 'Someone', billed: false }),
        roles: ['composer'],
      }),
      track({ album: album(), roles: ['billed'] }),
    ]),
    'An Artist',
  )

  assert.deepEqual(
    sections.map((section) => section.key),
    ['discography', 'composer'],
  )
  assert.deepEqual(
    sections.map((section) => section.albums.length),
    [1, 1],
  )
})

test('one album lands on one shelf, so the shelves total the discography', () => {
  const albums = albumsOf([
    track({ album: album(), roles: ['billed', 'conductor', 'composer'] }),
    track({ album: album({ releaseId: 'r-2' }), roles: ['ensemble'] }),
  ])

  const filed = sectionsOf(albums, 'An Artist').reduce(
    (sum, section) => sum + section.albums.length,
    0,
  )
  assert.equal(filed, albums.length)
})

test("a track billed on somebody else's album is an appearance, not their own record", () => {
  // The reported case, in the shape it arrives in: Marc Broussard sings one
  // song on a B.B. King tribute album whose billing line is Joe Bonamassa's.
  // The track's role is 'billed' — that is what made this look like his.
  const tribute = albumsOf([
    track({
      title: 'Three O\u2019Clock Blues',
      roles: ['billed'],
      album: album({
        releaseId: 'r-bb',
        title: 'B.B. King\u2019s Blues Summit 100',
        artist: 'Joe Bonamassa',
        billed: false,
      }),
    }),
  ])

  assert.equal(tribute[0]?.albumArtist, 'Joe Bonamassa')
  assert.deepEqual(
    tribute.map((a) => sectionOf(a, 'An Artist')),
    ['collaborations'],
  )
})

test('being billed on the release keeps the album in the discography', () => {
  const own = albumsOf([track({ roles: ['billed'], album: album({ billed: true }) })])

  assert.deepEqual(
    own.map((a) => sectionOf(a, 'An Artist')),
    ['discography'],
  )
})

test('a release the catalogue holds no credit for is not demoted', () => {
  // Null is "nobody wrote a credit row", which is not evidence the artist is
  // absent from the billing line. Eight of the target library's 644 releases.
  const unknown = albumsOf([
    track({ roles: ['billed'], album: album({ artist: null, billed: null }) }),
  ])

  assert.deepEqual(
    unknown.map((a) => sectionOf(a, 'An Artist')),
    ['discography'],
  )
})

test('a folder nothing has placed is not demoted either', () => {
  const folder = albumsOf([track({ roles: ['billed'], album: null })])

  assert.equal(folder[0]?.billedOnRelease, null)
  assert.deepEqual(
    folder.map((a) => sectionOf(a, 'An Artist')),
    ['discography'],
  )
})

test('a guest appearance they also wrote goes to collaborations, not composer', () => {
  const guest = albumsOf([
    track({ roles: ['billed', 'composer'], album: album({ billed: false }) }),
  ])

  assert.deepEqual(
    guest.map((a) => sectionOf(a, 'An Artist')),
    ['collaborations'],
  )
})

test("a band MusicBrainz files as its own artist is still the person's record", () => {
  // "Louis Armstrong" and "Louis Armstrong & His All-Stars" are two artist
  // entities, so Satch Plays Fats carries a release credit his personal entity
  // is not on. Demoting on the credit id alone takes it off his discography.
  const satch = albumsOf([
    track({
      roles: ['billed'],
      album: album({
        title: 'Satch Plays Fats',
        artist: 'Louis Armstrong & His All‐Stars',
        billed: false,
      }),
    }),
  ])

  assert.deepEqual(
    satch.map((a) => sectionOf(a, 'Louis Armstrong')),
    ['discography'],
  )
})

test('the band-and-person split is recognised from either side', () => {
  // The Nat King Cole Trio's albums are credited to Nat King Cole, so here the
  // page is the band and the shorter name is the one on the sleeve.
  const albums = albumsOf([
    track({
      roles: ['billed'],
      album: album({ title: 'After Midnight', artist: 'Nat King Cole', billed: false }),
    }),
  ])

  assert.deepEqual(
    albums.map((a) => sectionOf(a, 'The Nat King Cole Trio')),
    ['discography'],
  )
})

test('a short name is not matched inside a longer unrelated one', () => {
  // The comparison lands on whole words: "Air" must not match "Airto Moreira",
  // which raw substring containment would.
  const albums = albumsOf([
    track({ roles: ['billed'], album: album({ artist: 'Airto Moreira', billed: false }) }),
  ])

  assert.deepEqual(
    albums.map((a) => sectionOf(a, 'Air')),
    ['collaborations'],
  )
})

test('a group the artist played in is not somebody else covering them', () => {
  // The reported case. MusicBrainz credits Brothers in Arms to Dire Straits,
  // which is an artist entity of its own, so Mark Knopfler is on neither
  // billing line and reaches the catalogue only through the work's composer
  // relation. Read by role alone it lands on the Composer shelf, whose whole
  // claim is that somebody else recorded their music.
  const brothers = albumsOf([
    track({
      roles: ['composer'],
      album: album({
        title: 'Brothers in Arms',
        artist: 'Dire Straits',
        billed: false,
        band: { id: 'b-1', name: 'A Band' },
      }),
    }),
  ])

  assert.deepEqual(
    brothers.map((a) => sectionOf(a, 'Mark Knopfler')),
    ['band'],
  )
})

test('a genuine cover by another act is still a cover', () => {
  // The same shape without the membership, which is 95 albums on this library:
  // Lang Lang playing Bach, Eric Clapton covering J.J. Cale.
  const cover = albumsOf([
    track({
      roles: ['composer'],
      album: album({ title: 'Piano Book', artist: 'Lang Lang', billed: false, band: null }),
    }),
  ])

  assert.deepEqual(
    cover.map((a) => sectionOf(a, 'Johann Sebastian Bach')),
    ['composer'],
  )
})

test('an act named after them still gets its own shelf', () => {
  // This used to be the exception: a bandleader is a member of their own
  // eponymous act, so "Louis Armstrong & His All-Stars" kept its albums in his
  // discography while Dire Straits got a shelf on Mark Knopfler's page. The only
  // thing separating those two cases was whether the artist's name happens to be
  // a substring of the band's, which is a fact about spelling.
  const own = albumsOf([
    track({
      roles: ['billed'],
      album: album({
        title: 'Satch Plays Fats',
        artist: 'Louis Armstrong & His All‐Stars',
        billed: false,
        band: { id: 'all-stars', name: 'Louis Armstrong & His All-Stars' },
      }),
    }),
  ])

  assert.deepEqual(
    own.map((a) => sectionOf(a, 'Louis Armstrong')),
    ['band'],
  )
})

test('the band shelf sits between the discography and the guest spots', () => {
  const sections = sectionsOf(
    albumsOf([
      track({
        album: album({ releaseId: 'x', artist: 'Someone', billed: false }),
        roles: ['composer'],
      }),
      track({
        album: album({
          releaseId: 'b',
          artist: 'A Band',
          billed: false,
          band: { id: 'b-1', name: 'A Band' },
        }),
        roles: ['composer'],
      }),
      track({
        album: album({ releaseId: 'g', artist: 'Another', billed: false }),
        roles: ['billed'],
      }),
      track({ album: album(), roles: ['billed'] }),
    ]),
    'An Artist',
  )

  assert.deepEqual(
    sections.map((s) => s.key),
    ['discography', 'band', 'collaborations', 'composer'],
  )
})

test('the band shelf is one section per band, named after it', () => {
  // "With the band" says nothing about which, and an artist is routinely in
  // several: Mark Knopfler was in Dire Straits and the Notting Hillbillies.
  const ds = { id: 'dire-straits', name: 'Dire Straits' }
  const nh = { id: 'notting', name: 'The Notting Hillbillies' }

  const sections = sectionsOf(
    albumsOf([
      track({
        album: album({ releaseId: 'ds1', artist: 'Dire Straits', billed: false, band: ds }),
      }),
      track({
        album: album({ releaseId: 'ds2', artist: 'Dire Straits', billed: false, band: ds }),
      }),
      track({
        album: album({
          releaseId: 'nh1',
          artist: 'The Notting Hillbillies',
          billed: false,
          band: nh,
        }),
      }),
    ]),
    'Mark Knopfler',
  )

  assert.deepEqual(
    sections.map((x) => x.key),
    ['band', 'band'],
  )
  assert.deepEqual(
    sections.map((x) => x.band),
    ['Dire Straits', 'The Notting Hillbillies'],
  )
  // The one the library holds more of leads.
  assert.deepEqual(
    sections.map((x) => x.albums.length),
    [2, 1],
  )
})

test('one band spelled two ways on two sleeves is one shelf', () => {
  // This library prints both "The Robert Cray Band" and "Robert Cray Band".
  // Grouped on the printed credit that is two shelves for one band, so the
  // section is keyed on the band's own id and headed with its own name.
  const cray = { id: 'cray-band', name: 'The Robert Cray Band' }

  const sections = sectionsOf(
    albumsOf([
      track({
        album: album({
          releaseId: 'r1',
          artist: 'The Robert Cray Band',
          billed: false,
          band: cray,
        }),
      }),
      track({
        album: album({ releaseId: 'r2', artist: 'Robert Cray Band', billed: false, band: cray }),
      }),
    ]),
    'Robert Cray',
  )

  const only = sections[0]

  assert.equal(sections.length, 1)
  assert.equal(only?.band, 'The Robert Cray Band')
  assert.equal(only?.albums.length, 2)
})

test('splitting the band shelf loses no album and invents none', () => {
  const albums = albumsOf([
    track({
      album: album({
        releaseId: 'a',
        artist: 'A Band',
        billed: false,
        band: { id: 'ba', name: 'A Band' },
      }),
    }),
    track({
      album: album({
        releaseId: 'b',
        artist: 'B Band',
        billed: false,
        band: { id: 'bb', name: 'B Band' },
      }),
    }),
    track({ album: album({ releaseId: 'c' }), roles: ['billed'] }),
  ])

  const filed = sectionsOf(albums, 'An Artist').reduce((sum, s) => sum + s.albums.length, 0)

  assert.equal(filed, albums.length)
})

test('a member credited only as writer is not filed as covering themselves', () => {
  // The Robert Cray Band's albums credit Robert Cray as their writer and
  // nothing else, so before membership was read they sat on his "recorded by
  // somebody else" shelf. Membership returns outright rather than falling
  // through to the composer test, which is what that bug was.
  const own = albumsOf([
    track({
      roles: ['composer'],
      album: album({
        title: 'Strong Persuader',
        artist: 'The Robert Cray Band',
        billed: false,
        band: { id: 'cray-band', name: 'The Robert Cray Band' },
      }),
    }),
  ])

  assert.deepEqual(
    own.map((a) => sectionOf(a, 'Robert Cray')),
    ['band'],
  )
})

test('membership settles the shelf whatever the role and whatever the name', () => {
  const shelf = (artist: string, line: string) =>
    albumsOf([
      track({
        roles: ['composer'],
        album: album({ artist: line, billed: false, band: { id: 'b', name: line } }),
      }),
    ]).map((a) => sectionOf(a, artist))[0]

  assert.equal(shelf('Robert Cray', 'The Robert Cray Band'), 'band')
  assert.equal(shelf('Louis Armstrong', 'Louis Armstrong and His Orchestra'), 'band')
  assert.equal(shelf('Mark Knopfler', 'Dire Straits'), 'band')
  assert.equal(shelf('Susan Tedeschi', 'Tedeschi Trucks Band'), 'band')
})

test('an album reached only through a band is not filed as a cover', () => {
  // A track the artist has no personal credit on arrives with the single role
  // "member". Before the API named that reason the list was empty, and
  // `[].every(r => r === 'composer')` is true — so two of Neville Marriner's
  // albums were filed as his music recorded by somebody else.
  const inherited = albumsOf([
    track({
      roles: ['member'],
      album: album({ title: 'Bewitched', artist: 'Laufey', billed: false, band: null }),
    }),
  ])

  assert.deepEqual(
    inherited.map((a) => sectionOf(a, 'Sir Neville Marriner')),
    ['collaborations'],
  )
})

test('an album with no stated role at all is not a cover either', () => {
  // Defence in depth against the same vacuous truth, for whatever names the
  // next reason.
  const nothing = albumsOf([
    track({ roles: [], album: album({ artist: 'Somebody', billed: false, band: null }) }),
  ])

  assert.deepEqual(
    nothing.map((a) => sectionOf(a, 'An Artist')),
    ['collaborations'],
  )
})
