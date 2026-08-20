import type { Meta, StoryObj } from '@storybook/react-vite'
import type { ReactNode } from 'react'
import { expect, waitFor } from 'storybook/test'

import { cover } from '../CatalogueCard/fixtures.ts'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { Artwork } from './Artwork.tsx'

/** `size="fill"` has no width of its own, so these stories give it one. */
function Frame({ width, children }: { readonly width: number; readonly children: ReactNode }) {
  return <div style={{ width: `${width}px` }}>{children}</div>
}

/**
 * The monogram is `aria-hidden`, so it is deliberately unreachable by every
 * accessible query — which is the property being protected. Reading it takes a
 * DOM selector, exactly as `CatalogueGrid` reads its own grid tracks.
 */
function monograms(root: HTMLElement): readonly HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>('span[aria-hidden="true"]')]
}

function fontSizeOf(element: HTMLElement): number {
  return Number.parseFloat(globalThis.getComputedStyle(element).fontSize)
}

const meta = {
  title: 'Primitives/Artwork',
  component: Artwork,
  parameters: { layout: 'centered' },
  argTypes: {
    shape: { control: 'inline-radio', options: ['square', 'circle'] },
    size: { control: 'inline-radio', options: ['fill', 'sm', 'md', 'lg'] },
  },
  args: { name: 'Off the Wall', shape: 'square', size: 'fill' },
  render: (args) => (
    <Frame width={160}>
      <Artwork {...args} />
    </Frame>
  ),
} satisfies Meta<typeof Artwork>

export default meta
type Story = StoryObj<typeof meta>

export const Square: Story = {
  args: { src: cover('#f59f00', '#c92a2a') },
}

/**
 * The one shape difference that carries meaning: a thing you own is a square, a
 * person or a group is a circle. Nothing else about the two differs.
 */
export const Circle: Story = {
  args: { name: 'Joe Bonamassa', shape: 'circle', src: cover('#1971c2', '#0b0d0e') },
}

/**
 * What the application actually renders today — the catalogue holds no cover
 * art, so the monogram is the normal case rather than a fallback.
 *
 * It is `aria-hidden`, because the name it was derived from is already text
 * beside it and the initials would otherwise be announced in front of it.
 */
export const Monogram: Story = {
  args: { name: 'Symphony No. 5 in C minor, Op. 67' },
  play: async ({ canvasElement }) => {
    const [monogram] = monograms(canvasElement)
    await expect(monogram).toBeInTheDocument()
    await expect(monogram).toHaveTextContent('SN')
  },
}

/**
 * A source that cannot decode falls back to the monogram with no network
 * involved — the `data:` URI is a valid MIME type wrapping four bytes that are
 * not a PNG, so `onError` fires deterministically.
 *
 * The failing src is what gets stored, not a boolean, so an `src` prop that
 * changes gets a fresh attempt without an effect to reset a flag.
 */
export const BrokenSource: Story = {
  args: { name: 'Ella Fitzgerald', src: 'data:image/png;base64,AAAAAA==' },
  play: async ({ canvasElement }) => {
    // `waitFor`, because the fallback is a reaction to the image's `error`
    // event and the first render still holds the `<img>` that will fire it.
    await waitFor(async () => {
      await expect(monograms(canvasElement)[0]).toHaveTextContent('EF')
    })
    await expect(canvasElement.querySelector('img')).toBeNull()
  },
}

/** `fill` follows its container; the fixed sizes are a box in a row. */
export const Sizes: Story = {
  render: () => (
    <Stack gap={16} align="center">
      <Artwork name="Off the Wall" size="sm" src={cover('#f59f00', '#c92a2a')} />
      <Artwork name="Sloe Gin" size="md" src={cover('#1864ab', '#121416')} />
      <Artwork name="Blues Deluxe" size="lg" src={cover('#5f3dc4', '#1864ab')} />
      <Frame width={160}>
        <Artwork name="Ella in Berlin" size="fill" src={cover('#495057', '#adb5bd')} />
      </Frame>
    </Stack>
  ),
}

/**
 * Two things at once, and the second is the reason this story has a play
 * function.
 *
 * `Array.from(word)[0]` rather than `word[0]`: a name beginning outside the BMP
 * would otherwise be cut in half and render as a replacement character.
 *
 * And the monogram is sized in container units, so it must shrink with the box
 * it was given. That depends on `container-type` sitting on the same element
 * that has the width — put it one level out and every card still renders, with
 * letters silently frozen at the cap. Measuring what the browser laid out is
 * the only way to see it.
 */
export const Diacritics: Story = {
  render: () => (
    <Stack direction="column" gap={12} align="start">
      <Frame width={64}>
        <Artwork name="Björk Guðmundsdóttir" shape="circle" />
      </Frame>
      <Frame width={160}>
        <Artwork name="Björk Guðmundsdóttir" shape="circle" />
      </Frame>
      <Text size="xs" tone="tertiary">
        The same name in a 64px box and a 160px box.
      </Text>
    </Stack>
  ),
  play: async ({ canvasElement }) => {
    const [small, large] = monograms(canvasElement)
    if (small == null || large == null) throw new Error('expected two monograms')

    await expect(small).toHaveTextContent('BG')
    await expect(fontSizeOf(small)).toBeLessThan(fontSizeOf(large))
  },
}
