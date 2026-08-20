import type { Meta, StoryObj } from '@storybook/react-vite'
import type { ReactNode } from 'react'

import { Badge } from '../Badge/Badge.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { CatalogueCard } from './CatalogueCard.tsx'
import { cover } from './fixtures.ts'

/**
 * A tile has no width of its own — in the application it fills a
 * `CatalogueGrid` track. These stories put it in a fixed frame so each variant
 * can be looked at on its own; the grid stories are where the sizing rule is.
 */
function Frame({ width, children }: { readonly width: number; readonly children: ReactNode }) {
  return <div style={{ width: `${width}px` }}>{children}</div>
}

const meta = {
  title: 'Catalogue/CatalogueCard',
  component: CatalogueCard,
  args: {
    variant: 'album',
    title: 'Off the Wall',
    subtitle: 'Michael Jackson',
    image: cover('#f59f00', '#c92a2a'),
    href: '#off-the-wall',
  },
  render: (args) => (
    <Frame width={args.variant === 'artist' ? 126 : 160}>
      <CatalogueCard {...args} />
    </Frame>
  ),
} satisfies Meta<typeof CatalogueCard>

export default meta
type Story = StoryObj<typeof meta>

export const Album: Story = {}

/** A circle rather than a square, and the text centred under it. */
export const Artist: Story = {
  args: {
    variant: 'artist',
    title: 'Joe Bonamassa',
    subtitle: '412 tracks',
    image: cover('#1971c2', '#0b0d0e'),
  },
}

/**
 * What the application actually renders today: the catalogue holds no cover
 * art, so the monogram is the normal case rather than a fallback for a broken
 * URL. It is `aria-hidden` — the initials would otherwise be announced in front
 * of the title they were derived from.
 */
export const WithoutArtwork: Story = {
  render: () => (
    <Stack gap={16} align="start">
      <Frame width={160}>
        <CatalogueCard
          variant="album"
          title="Symphony No. 5 in C minor, Op. 67"
          subtitle="Berliner Philharmoniker, Herbert von Karajan"
          href="#beethoven-5"
        />
      </Frame>
      <Frame width={126}>
        <CatalogueCard variant="artist" title="Herbert von Karajan" subtitle="1,204 tracks" />
      </Frame>
    </Stack>
  ),
}

/**
 * The `meta` slot, which is where a card earns its place over a row: quality,
 * certainty, and whether the album is all there.
 */
export const WithMeta: Story = {
  args: {
    title: 'Sloe Gin',
    subtitle: 'Joe Bonamassa',
    image: cover('#1864ab', '#121416'),
    meta: (
      <>
        <Badge tone="neutral" mono>
          FLAC
        </Badge>
        <Badge tone="warning">8 of 11</Badge>
      </>
    ),
  },
}

/**
 * Titles are truncated to one line so a row of tiles stays a row. A classical
 * work title will always be longer than any tile, so the alternative is not
 * "show it all" but "let one card be four lines tall".
 */
export const LongTitle: Story = {
  args: {
    title: 'Ella in Berlin: Mack the Knife (Expanded Edition, 2013 Remaster)',
    subtitle: 'Ella Fitzgerald',
    image: cover('#495057', '#adb5bd'),
  },
}

/**
 * No `href` and no `render`: a tile that is not a link. It renders a span, not
 * a div with a click handler — an interaction the keyboard cannot reach is
 * worse than no interaction.
 */
export const Static: Story = {
  render: () => (
    <Frame width={160}>
      <CatalogueCard
        variant="album"
        title="Off the Wall"
        subtitle="Michael Jackson"
        image={cover('#f59f00', '#c92a2a')}
      />
    </Frame>
  ),
}

/**
 * `render` hands the element back to the caller, which is how a TanStack Router
 * `<Link>` becomes a card. Spread all of the props: the `data-catalogue-card`
 * attribute among them is what the grid measures.
 */
export const CustomElement: Story = {
  render: () => (
    <Stack direction="column" gap={8} align="start">
      <Frame width={160}>
        <CatalogueCard
          variant="album"
          title="Blues Deluxe"
          subtitle="Joe Bonamassa"
          image={cover('#5f3dc4', '#1864ab')}
          render={(props) => <a {...props} href="#blues-deluxe" data-router-link="stand-in" />}
        />
      </Frame>
      <Text size="xs" tone="tertiary">
        Rendered through <code>render</code>, standing in for a router link.
      </Text>
    </Stack>
  ),
}

/** Both variants together, at the sizes their own grid would give them. */
export const Variants: Story = {
  render: () => (
    <Stack gap={24} align="start">
      <Frame width={160}>
        <CatalogueCard
          variant="album"
          title="Off the Wall"
          subtitle="Michael Jackson"
          image={cover('#f59f00', '#c92a2a')}
          href="#off-the-wall"
        />
      </Frame>
      <Frame width={126}>
        <CatalogueCard
          variant="artist"
          title="Michael Jackson"
          subtitle="31 tracks"
          href="#michael-jackson"
        />
      </Frame>
    </Stack>
  ),
}
