import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'

import { Badge } from '../Badge/Badge.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { CatalogueCard } from './CatalogueCard.tsx'
import { CatalogueGrid } from './CatalogueGrid.tsx'
import {
  ALBUMS,
  type AlbumFixture,
  ARTISTS,
  type ArtistFixture,
  BEETHOVEN_5,
  BONAMASSA,
  JACKSON,
  KARAJAN,
  OFF_THE_WALL,
  PRIVATE_INVESTIGATIONS,
  SLOE_GIN,
} from './fixtures.ts'

/**
 * The conditional spread is `exactOptionalPropertyTypes` doing its job: a
 * fixture with no artwork has no `image` key at all, and passing an explicit
 * `undefined` for one is a different statement than omitting it.
 */
function AlbumTile({ album }: { readonly album: AlbumFixture }) {
  return (
    <CatalogueCard
      variant="album"
      title={album.title}
      subtitle={album.artist}
      meta={<Badge tone="neutral">{album.year}</Badge>}
      href={`#${album.id}`}
      {...(album.image != null ? { image: album.image } : {})}
    />
  )
}

function ArtistTile({ artist }: { readonly artist: ArtistFixture }) {
  return (
    <CatalogueCard
      variant="artist"
      title={artist.name}
      subtitle={`${artist.tracks.toLocaleString()} tracks`}
      href={`#${artist.id}`}
      {...(artist.image != null ? { image: artist.image } : {})}
    />
  )
}

/**
 * The sizing rule, asserted.
 *
 * `pnpm test` runs every story in a real Chromium, so the rule can be checked
 * where it actually lives — in the cascade — rather than by reading the
 * stylesheet. That matters more here than for most components: the rule is a
 * `:has()` selector and an override that only wins on source order, and both of
 * those fail silently and look like a design decision when they do.
 *
 * The *used* track width, read back from `grid-template-columns`, rather than
 * the custom property it came from: the artist width is now a `calc()` over the
 * album width, and a custom property reads back unresolved. Measuring what the
 * browser laid out also tests the arithmetic rather than restating it.
 */
function measure(root: HTMLElement, index = 0): { readonly track: number; readonly gap: number } {
  const grid = root.querySelectorAll('ul')[index]
  if (grid == null) throw new Error(`no grid at index ${index}`)

  const style = globalThis.getComputedStyle(grid)
  const track = Number.parseFloat(style.gridTemplateColumns.split(' ')[0] ?? '')
  return { track, gap: Number.parseFloat(style.columnGap) }
}

/** What `count` tiles occupy end to end, gaps included. */
function span({ track, gap }: { readonly track: number; readonly gap: number }, count: number) {
  return count * track + (count - 1) * gap
}

const ARTIST_COLUMN = 125.6
const ALBUM_COLUMN = 160

async function expectColumn(root: HTMLElement, expected: number, index = 0) {
  await expect(measure(root, index).track).toBeCloseTo(expected, 1)
}

const meta = {
  title: 'Catalogue/CatalogueGrid',
  component: CatalogueGrid,
  parameters: { layout: 'padded' },
} satisfies Meta<typeof CatalogueGrid>

export default meta
type Story = StoryObj<typeof meta>

/** Albums: the larger tile, because a cover is the thing being recognised. */
export const Albums: Story = {
  render: () => (
    <CatalogueGrid aria-label="Albums">
      {ALBUMS.map((album) => (
        <AlbumTile key={album.id} album={album} />
      ))}
    </CatalogueGrid>
  ),
  play: async ({ canvasElement }) => {
    await expectColumn(canvasElement, ALBUM_COLUMN)
  },
}

/**
 * Artists: the smaller tile, and more of them per row. A name is legible at a
 * size a cover is not, and an artist list is long — 2,752 of them in the target
 * library — so the tile that fits more per screen is the one that fits the job.
 */
export const Artists: Story = {
  render: () => (
    <CatalogueGrid aria-label="Artists">
      {ARTISTS.map((artist) => (
        <ArtistTile key={artist.id} artist={artist} />
      ))}
    </CatalogueGrid>
  ),
  play: async ({ canvasElement }) => {
    await expectColumn(canvasElement, ARTIST_COLUMN)
  },
}

/**
 * Mixed, and the rule the grid exists to hold: **one album card anywhere inside
 * raises the whole grid to the album size.** Sizing down instead would shrink
 * the covers to fit the names, which is the wrong way round — and a grid whose
 * tiles are two different widths is not a grid.
 *
 * Nothing is passed to make this happen. The stylesheet asks whether an album
 * card is present, so a search result that gains its first album mid-render
 * gets it right without the caller tracking what it is holding.
 */
export const Mixed: Story = {
  render: () => (
    <CatalogueGrid aria-label="Albums and artists">
      <AlbumTile album={OFF_THE_WALL} />
      <ArtistTile artist={JACKSON} />
      <AlbumTile album={SLOE_GIN} />
      <ArtistTile artist={BONAMASSA} />
      <ArtistTile artist={KARAJAN} />
      <AlbumTile album={BEETHOVEN_5} />
    </CatalogueGrid>
  ),
  // Four artists and two albums: the albums win.
  play: async ({ canvasElement }) => {
    await expectColumn(canvasElement, ALBUM_COLUMN)
  },
}

/**
 * The two sizes one under the other, lined up: five artists occupy exactly the
 * span of four albums, which is what the artist width is derived from.
 */
export const SizesCompared: Story = {
  render: () => (
    <Stack direction="column" gap={24}>
      <Stack direction="column" gap={8}>
        <Text size="sm" weight="semibold" tone="secondary" block>
          Five artists — 125.6px tracks
        </Text>
        <CatalogueGrid aria-label="Artists only">
          {ARTISTS.slice(0, 5).map((artist) => (
            <ArtistTile key={artist.id} artist={artist} />
          ))}
        </CatalogueGrid>
      </Stack>

      <Stack direction="column" gap={8}>
        <Text size="sm" weight="semibold" tone="secondary" block>
          Four albums — 160px tracks, ending in the same place
        </Text>
        <CatalogueGrid aria-label="Albums only">
          {ALBUMS.slice(0, 4).map((album) => (
            <AlbumTile key={album.id} album={album} />
          ))}
        </CatalogueGrid>
      </Stack>
    </Stack>
  ),
  // The requirement, stated as itself: five artist tiles end where four album
  // tiles end. Asserted on the rendered tracks, so it fails if either width or
  // the column gap moves without the other following.
  play: async ({ canvasElement }) => {
    const artists = measure(canvasElement, 0)
    const albums = measure(canvasElement, 1)

    await expect(artists.track).toBeCloseTo(ARTIST_COLUMN, 1)
    await expect(albums.track).toBeCloseTo(ALBUM_COLUMN, 1)
    await expect(span(artists, 5)).toBeCloseTo(span(albums, 4), 1)
  },
}

/**
 * `size` pins the track width. The case it is for is a grid that has not
 * received its data yet: the contents cannot answer the question, and a grid
 * that resizes under the reader when the first album arrives is worse than one
 * that was told.
 */
export const PinnedSize: Story = {
  render: () => (
    <Stack direction="column" gap={8}>
      <Text size="sm" tone="secondary" block>
        Albums, pinned to the artist size.
      </Text>
      <CatalogueGrid size="artist" aria-label="Albums at the artist size">
        {ALBUMS.slice(0, 4).map((album) => (
          <AlbumTile key={album.id} album={album} />
        ))}
      </CatalogueGrid>
    </Stack>
  ),
  // The one that only source order gets right: albums inside, `size="artist"`
  // outside, and the two rules weigh exactly the same.
  play: async ({ canvasElement }) => {
    await expectColumn(canvasElement, ARTIST_COLUMN)
  },
}

/**
 * Long titles, at both sizes. The tile truncates rather than growing, so the
 * `1fr` tracks stay equal and the row stays a row.
 */
export const LongNames: Story = {
  render: () => (
    <CatalogueGrid aria-label="Long titles">
      <CatalogueCard
        variant="album"
        title="Ella in Berlin: Mack the Knife (Expanded Edition, 2013 Remaster)"
        subtitle="Ella Fitzgerald"
        href="#ella-berlin-expanded"
      />
      <CatalogueCard
        variant="artist"
        title="Orchestre de la Société des Concerts du Conservatoire"
        subtitle="88 tracks"
        href="#conservatoire"
      />
      <AlbumTile album={PRIVATE_INVESTIGATIONS} />
    </CatalogueGrid>
  ),
}
