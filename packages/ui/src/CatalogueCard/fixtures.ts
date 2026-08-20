/**
 * Fixtures for the stories, and only for the stories.
 *
 * The artwork is generated rather than fetched. Every story in this package is
 * also a test, run in a real browser by `pnpm test`, and a story that loads an
 * image over the network is a test that fails when the network does. These are
 * `data:` URIs, so the browser never leaves the page.
 */

/** A two-stop gradient as an SVG data URI, standing in for a cover. */
export function cover(from: string, to: string): string {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120">
<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
<stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/>
</linearGradient></defs><rect width="120" height="120" fill="url(#g)"/></svg>`

  // encodeURIComponent rather than base64: it keeps the markup readable in dev
  // tools, and it is what escapes the `#` in every one of those colours.
  return `data:image/svg+xml,${encodeURIComponent(svg)}`
}

export type AlbumFixture = {
  readonly id: string
  readonly title: string
  readonly artist: string
  readonly year: string
  readonly image?: string
}

export type ArtistFixture = {
  readonly id: string
  readonly name: string
  readonly tracks: number
  readonly image?: string
}

/*
 * Named individually as well as collected into the arrays below, because
 * `noNonNullAssertion` is an error in this repo and `ALBUMS[0]!` is how a story
 * that wants one particular fixture would otherwise have to ask for it.
 */

export const OFF_THE_WALL: AlbumFixture = {
  id: 'off-the-wall',
  title: 'Off the Wall',
  artist: 'Michael Jackson',
  year: '1979',
  image: cover('#f59f00', '#c92a2a'),
}

export const SLOE_GIN: AlbumFixture = {
  id: 'sloe-gin',
  title: 'Sloe Gin',
  artist: 'Joe Bonamassa',
  year: '2007',
  image: cover('#1864ab', '#121416'),
}

export const ELLA_IN_BERLIN: AlbumFixture = {
  id: 'ella-berlin',
  title: 'Ella in Berlin: Mack the Knife',
  artist: 'Ella Fitzgerald',
  year: '1960',
  image: cover('#495057', '#adb5bd'),
}

/**
 * No artwork, and that is not the exceptional case: the catalogue holds no
 * cover art at all today, so every card in the application is this one.
 */
export const BEETHOVEN_5: AlbumFixture = {
  id: 'beethoven-5',
  title: 'Symphony No. 5 in C minor, Op. 67',
  artist: 'Berliner Philharmoniker, Herbert von Karajan',
  year: '1963',
}

export const PRIVATE_INVESTIGATIONS: AlbumFixture = {
  id: 'private-investigations',
  title: 'Private Investigations: The Best of Dire Straits',
  artist: 'Dire Straits',
  year: '2005',
  image: cover('#2b8a3e', '#0b0d0e'),
}

export const BLUES_DELUXE: AlbumFixture = {
  id: 'blues-deluxe',
  title: 'Blues Deluxe',
  artist: 'Joe Bonamassa',
  year: '2003',
  image: cover('#5f3dc4', '#1864ab'),
}

export const BONAMASSA: ArtistFixture = {
  id: 'bonamassa',
  name: 'Joe Bonamassa',
  tracks: 412,
  image: cover('#1971c2', '#0b0d0e'),
}

export const FITZGERALD: ArtistFixture = {
  id: 'fitzgerald',
  name: 'Ella Fitzgerald',
  tracks: 268,
  image: cover('#e67700', '#7d1a1a'),
}

export const KARAJAN: ArtistFixture = { id: 'karajan', name: 'Herbert von Karajan', tracks: 1_204 }

export const BERLINER: ArtistFixture = {
  id: 'berliner',
  name: 'Berliner Philharmoniker',
  tracks: 1_811,
}

export const BEETHOVEN: ArtistFixture = {
  id: 'beethoven',
  name: 'Ludwig van Beethoven',
  tracks: 96,
  image: cover('#343a40', '#868e96'),
}

export const JACKSON: ArtistFixture = { id: 'jackson', name: 'Michael Jackson', tracks: 31 }

export const ALBUMS: readonly AlbumFixture[] = [
  OFF_THE_WALL,
  SLOE_GIN,
  ELLA_IN_BERLIN,
  BEETHOVEN_5,
  PRIVATE_INVESTIGATIONS,
  BLUES_DELUXE,
]

export const ARTISTS: readonly ArtistFixture[] = [
  BONAMASSA,
  FITZGERALD,
  KARAJAN,
  BERLINER,
  BEETHOVEN,
  JACKSON,
]
