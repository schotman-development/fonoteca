import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, fireEvent, fn, userEvent, waitFor } from 'storybook/test'

import { Button } from '../Button/Button.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { Dialog, type DialogProps } from './Dialog.tsx'

/** Enough rows to overflow the panel, named rather than indexed so each has a key. */
const CANDIDATES: readonly string[] = Array.from(
  { length: 30 },
  (_, index) => `Candidate ${index + 1}`,
)

/**
 * The component is controlled, so every story needs somewhere for "is it open"
 * to live. The trigger is part of the harness rather than part of `Dialog`:
 * what opens a dialog is a row, a button or a keyboard shortcut belonging to the
 * screen, and a component that shipped its own trigger could only ever be one of
 * those.
 */
function Harness({ open: initiallyOpen, onClose, ...rest }: DialogProps) {
  const [open, setOpen] = useState(initiallyOpen)

  return (
    <Stack direction="column" gap={12} align="start">
      <Button
        onClick={() => {
          setOpen(true)
        }}
      >
        Open the dialog
      </Button>

      <Dialog
        {...rest}
        open={open}
        onClose={() => {
          setOpen(false)
          onClose()
        }}
      />
    </Stack>
  )
}

const meta = {
  title: 'Patterns/Dialog',
  component: Dialog,
  parameters: { layout: 'padded' },
  argTypes: { size: { control: 'inline-radio', options: ['md', 'lg'] } },
  args: {
    open: true,
    onClose: fn(),
    title: 'Off the Wall (1979)',
    children: (
      <Text size="sm" tone="secondary" block>
        Ten files, ten slots, mean drift 0.00 s against the 2015 remaster.
      </Text>
    ),
  },
  render: (args) => <Harness {...args} />,
} satisfies Meta<typeof Dialog>

export default meta
type Story = StoryObj<typeof meta>

/**
 * Open, which is the only state worth running axe against — the dialog, its
 * heading and its close button exist in no other one.
 */
export const Open: Story = {
  play: async ({ canvas }) => {
    const dialog = canvas.getByRole('dialog', { name: 'Off the Wall (1979)' })

    await expect(dialog).toBeVisible()
    await expect(canvas.getByRole('heading', { level: 2 })).toHaveTextContent('Off the Wall (1979)')
    await expect(canvas.getByRole('button', { name: 'Close' })).toBeVisible()
  },
}

/**
 * Closed, and genuinely absent rather than merely invisible: a `<dialog>` that
 * has not been shown exposes no role at all, so nothing inside it is reachable
 * by the keyboard or by a screen reader while it sits there.
 */
export const Closed: Story = {
  args: { open: false },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(canvas.getByRole('button', { name: 'Open the dialog' }))
    await waitFor(async () => {
      await expect(canvas.getByRole('dialog')).toBeVisible()
    })
  },
}

/**
 * Focus is the platform's, and this is the assertion that proves it: opening
 * moves focus inside, and dismissing puts it back on the control that opened it.
 * Neither is code in `Dialog.tsx` — both follow from `showModal()` and `close()`.
 *
 * It is also why every dismissal in the component goes through `close()` rather
 * than through React state: a caller that unmounts the dialog on close would
 * otherwise remove it from the document with focus still inside, and focus would
 * land on `<body>`.
 */
export const RestoresFocus: Story = {
  args: { open: false },
  play: async ({ canvas }) => {
    const trigger = canvas.getByRole('button', { name: 'Open the dialog' })
    await userEvent.click(trigger)

    await waitFor(async () => {
      await expect(canvas.getByRole('dialog')).toBeVisible()
    })
    await expect(trigger).not.toHaveFocus()

    await userEvent.click(canvas.getByRole('button', { name: 'Close' }))
    await waitFor(async () => {
      await expect(trigger).toHaveFocus()
    })
  },
}

/**
 * The two dismissals a test can drive, and the one it cannot.
 *
 * `Escape` is deliberately not asserted here. It is the platform's, and the
 * platform only performs a default action for a *trusted* event — the synthetic
 * keydown `userEvent` dispatches reaches every listener and closes nothing. So
 * an assertion about it would be testing the harness rather than the dialog. It
 * still works in a browser, and it works through the same `close` event these
 * two go through, which is what this story does pin down: `onClose` is raised by
 * the element, so every route out reaches the caller by construction.
 */
export const Dismisses: Story = {
  play: async ({ canvas, args }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Close' }))
    await waitFor(async () => {
      await expect(args.onClose).toHaveBeenCalledOnce()
    })

    await userEvent.click(canvas.getByRole('button', { name: 'Open the dialog' }))
    await waitFor(async () => {
      await expect(canvas.getByRole('dialog')).toBeVisible()
    })

    // Dispatched at the element itself rather than clicked at its centre: the
    // centre is the panel, and the backdrop is only ever reported as a click
    // whose target is the dialog. Both halves of the gesture, because the
    // component requires the press to have landed out there too.
    const dialog = canvas.getByRole('dialog')
    fireEvent.mouseDown(dialog)
    fireEvent.click(dialog)
    await waitFor(async () => {
      await expect(args.onClose).toHaveBeenCalledTimes(2)
    })
  },
}

/**
 * A drag that starts inside the panel and ends on the backdrop is not a
 * dismissal.
 *
 * The browser dispatches that click at the common ancestor — the dialog — so it
 * arrives looking exactly like a backdrop click. Selecting a path or a title and
 * releasing past the panel's edge is an ordinary thing to do, and treating it as
 * "cancel" discards whatever the person had typed or chosen, silently.
 */
export const KeepsADragThatEndsOutside: Story = {
  play: async ({ canvas, args }) => {
    const dialog = canvas.getByRole('dialog')

    fireEvent.mouseDown(canvas.getByRole('heading', { level: 2 }))
    fireEvent.click(dialog)

    await expect(args.onClose).not.toHaveBeenCalled()
    await expect(dialog).toBeVisible()
  },
}

/**
 * Unmounted while open — a command elsewhere navigating the page away.
 *
 * The dialog has to close itself on the way out or focus is left on an element
 * that is no longer in the document, and the browser drops it to `<body>`: no
 * ring, and nowhere for the next `Tab` to continue from. Closing on unmount is
 * also the one close the caller must not hear about, which is what the second
 * assertion is for — under StrictMode the unmount is simulated and a caller that
 * heard it would tear down a dialog it is about to render again.
 */
export const ClosesWhenUnmounted: Story = {
  // The trigger sits outside the part that goes away, because that is the
  // element focus has to come back to — and because it is what the real caller
  // looks like: a row in a list that outlives the dialog it opened.
  render: (args) => {
    const [open, setOpen] = useState(false)
    const [gone, setGone] = useState(false)

    return (
      <Stack direction="column" gap={12} align="start">
        <Button
          onClick={() => {
            setOpen(true)
          }}
        >
          Open the dialog
        </Button>
        <Button
          onClick={() => {
            setGone(true)
          }}
        >
          Navigate away
        </Button>

        {gone ? null : (
          <Dialog
            {...args}
            open={open}
            onClose={() => {
              setOpen(false)
              args.onClose()
            }}
          />
        )}
      </Stack>
    )
  },
  play: async ({ canvas, args }) => {
    const trigger = canvas.getByRole('button', { name: 'Open the dialog' })
    await userEvent.click(trigger)
    await waitFor(async () => {
      await expect(canvas.getByRole('dialog')).toBeVisible()
    })

    // Reachable only because `userEvent` dispatches straight at the element; a
    // person could not hit it through the backdrop. It stands in for the same
    // unmount arriving from anywhere else — a route change, a command.
    await userEvent.click(canvas.getByRole('button', { name: 'Navigate away' }))

    await waitFor(async () => {
      await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument()
    })
    await expect(document.activeElement).not.toBe(document.body)
    await expect(args.onClose).not.toHaveBeenCalled()
  },
}

/** A description under the heading, and a footer pinned below the scroll area. */
export const WithFooter: Story = {
  args: {
    description: 'Releases existed, and none of them explained these files well enough.',
    footer: (
      <Stack gap={12} align="center" wrap>
        <Button variant="primary" size="sm">
          Commit
        </Button>
        <Text size="xs" tone="tertiary">
          Nothing chosen yet.
        </Text>
      </Stack>
    ),
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/none of them explained/i)).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Commit' })).toBeVisible()
  },
}

/**
 * A body longer than the panel scrolls *inside* it, with the header and the
 * footer staying put. `showModal()` does not lock the document's scroll, so
 * without the panel owning a scroll container the wheel falls through to the
 * page underneath.
 */
export const LongBody: Story = {
  args: {
    size: 'lg',
    footer: (
      <Text size="xs" tone="tertiary">
        Thirty candidates, none of them recorded anywhere.
      </Text>
    ),
    children: (
      <Stack direction="column" gap={12} align="start">
        {CANDIDATES.map((candidate) => (
          <Text key={candidate} size="sm" tone="secondary" block>
            {candidate} — a pressing that fits the track list and not the durations.
          </Text>
        ))}
      </Stack>
    ),
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/Candidate 1 —/)).toBeVisible()
    await expect(canvas.getByRole('heading', { level: 2 })).toBeVisible()
  },
}
