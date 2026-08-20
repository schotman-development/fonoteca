import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, waitFor } from 'storybook/test'

import { type Command, CommandBar } from './CommandBar.tsx'

/**
 * The commands a screen would pass. Deliberately a mix of the two kinds the app
 * has — going somewhere, and doing something — because the classifier column
 * only earns its width if more than one value appears in it.
 */
const commands: readonly Command[] = [
  { id: 'foundation', label: 'Foundation', kind: 'Go to', onSelect: fn() },
  {
    id: 'artists',
    label: 'Artists',
    kind: 'Go to',
    keywords: ['library', 'people'],
    onSelect: fn(),
  },
  { id: 'albums', label: 'Albums', kind: 'Go to', keywords: ['releases'], onSelect: fn() },
  { id: 'identify', label: 'Identify', kind: 'Go to', keywords: ['matching'], onSelect: fn() },
  { id: 'theme-light', label: 'Theme — light', kind: 'Action', onSelect: fn() },
  { id: 'theme-dark', label: 'Theme — dark', kind: 'Action', onSelect: fn() },
  { id: 'theme-system', label: 'Theme — follow the system', kind: 'Action', onSelect: fn() },
]

const meta = {
  title: 'Patterns/CommandBar',
  component: CommandBar,
  parameters: { layout: 'padded' },
  args: { commands },
  render: (args) => (
    <div style={{ maxWidth: '480px', marginInline: 'auto' }}>
      <CommandBar {...args} />
    </div>
  ),
} satisfies Meta<typeof CommandBar>

export default meta
type Story = StoryObj<typeof meta>

/**
 * The resting state: a button that looks like a field.
 *
 * The shortcut is on the button as `aria-keyshortcuts` and shown as a glyph
 * that is `aria-hidden` — one fact, announced once, in the form each audience
 * can use.
 */
export const Closed: Story = {
  play: async ({ canvas }) => {
    const trigger = canvas.getByRole('button', { name: 'Command bar' })

    await expect(trigger).toHaveAttribute('aria-haspopup', 'dialog')
    await expect(trigger).toHaveAttribute('aria-expanded', 'false')
    await expect(trigger).toHaveAttribute('aria-keyshortcuts', 'Meta+K Control+K')
    await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument()
  },
}

/**
 * Open, which is the state worth running axe against: the dialog, the combobox
 * and the listbox all only exist here.
 *
 * `defaultOpen` rather than a click in `play`, so the assertions and the a11y
 * run both see the palette even if the trigger ever stops working.
 */
export const Open: Story = {
  args: { defaultOpen: true },
  play: async ({ canvas }) => {
    const input = canvas.getByRole('combobox', { name: 'Command bar' })

    await expect(input).toHaveFocus()
    await expect(input).toHaveAttribute('aria-expanded', 'true')

    // The active option is named, not focused — focus stays in the input for
    // the whole life of the palette.
    const options = canvas.getAllByRole('option')
    await expect(options).toHaveLength(commands.length)
    await expect(input).toHaveAttribute('aria-activedescendant', options[0]?.id ?? '')
    await expect(options[0]).toHaveAttribute('aria-selected', 'true')
  },
}

/** Typing narrows the list; the caller's order survives it. */
export const Filters: Story = {
  args: { defaultOpen: true },
  play: async ({ canvas }) => {
    const input = canvas.getByRole('combobox', { name: 'Command bar' })
    await userEvent.type(input, 'theme')

    const options = canvas.getAllByRole('option')
    await expect(options).toHaveLength(3)
    await expect(options[0]).toHaveTextContent('Theme — light')

    // Keywords match without being shown: "releases" is not on the Albums row.
    await userEvent.clear(input)
    await userEvent.type(input, 'releases')
    await expect(canvas.getAllByRole('option')).toHaveLength(1)
    await expect(canvas.getByRole('option')).toHaveTextContent('Albums')
  },
}

/**
 * Arrow keys move the armed row, Enter runs it, and the palette closes before
 * the command does anything — a dialog still in the top layer would cover the
 * page a navigation just landed on.
 */
export const ArrowsAndEnter: Story = {
  args: {
    defaultOpen: true,
    commands: commands.map((command) => ({ ...command, onSelect: fn() })),
  },
  play: async ({ canvas, args }) => {
    // No element is targeted: the palette's whole key map is on the input, and
    // the input is where focus already is.
    await userEvent.keyboard('{ArrowDown}{ArrowDown}')
    await expect(canvas.getAllByRole('option')[2]).toHaveAttribute('aria-selected', 'true')

    // Up from the first wraps to the last, so a list longer than the panel is
    // reachable from either end.
    await userEvent.keyboard('{Home}{ArrowUp}')
    await expect(canvas.getAllByRole('option').at(-1)).toHaveAttribute('aria-selected', 'true')

    await userEvent.keyboard('{Home}{ArrowDown}{Enter}')
    await expect(args.commands[1]?.onSelect).toHaveBeenCalledOnce()
    await waitFor(async () => {
      await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument()
    })
  },
}

/**
 * Nothing matched. The listbox is `hidden` rather than unmounted — the input's
 * `aria-controls` must keep pointing at something that exists — and the message
 * is a live region, since the person who needs it is not watching the list.
 */
export const NoMatches: Story = {
  args: { defaultOpen: true },
  play: async ({ canvas }) => {
    const input = canvas.getByRole('combobox', { name: 'Command bar' })
    await userEvent.type(input, 'qobuz')

    await expect(canvas.queryAllByRole('option')).toHaveLength(0)
    await expect(input).toHaveAttribute('aria-expanded', 'false')
    await expect(input).not.toHaveAttribute('aria-activedescendant')
    await expect(canvas.getByRole('status')).toHaveTextContent('Nothing matches')

    const listboxId = input.getAttribute('aria-controls')
    await expect(listboxId).not.toBeNull()
    await expect(document.getElementById(listboxId ?? '')).toBeInTheDocument()
  },
}

/** ⌘K / Ctrl K opens it from anywhere, and closes it again. */
export const Shortcut: Story = {
  play: async ({ canvas }) => {
    await userEvent.keyboard('{Control>}k{/Control}')
    await waitFor(async () => {
      await expect(canvas.getByRole('combobox', { name: 'Command bar' })).toHaveFocus()
    })

    await userEvent.keyboard('{Control>}k{/Control}')
    await waitFor(async () => {
      await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument()
    })
  },
}
