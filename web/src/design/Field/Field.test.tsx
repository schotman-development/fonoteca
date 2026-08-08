import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { Field, TextArea, TextInput } from '@/design/Field/Field'

describe('Field', () => {
  it('points its label at the control it was given', () => {
    render(
      <Field label="Artist name">
        {(control) => <TextInput {...control} defaultValue="Mark Knopfler" />}
      </Field>,
    )

    expect(screen.getByLabelText('Artist name')).toHaveValue('Mark Knopfler')
  })

  it('links the hint to the control so it is announced', () => {
    render(
      <Field label="Sort name" hint="Knopfler, Mark — used to file the folder">
        {(control) => <TextInput {...control} />}
      </Field>,
    )

    expect(screen.getByLabelText('Sort name')).toHaveAccessibleDescription(
      'Knopfler, Mark — used to file the folder',
    )
  })

  it('describes nothing when there is no hint', () => {
    render(<Field label="Artist name">{(c) => <TextInput {...c} />}</Field>)

    const input = screen.getByLabelText('Artist name')
    expect(input).not.toHaveAttribute('aria-describedby')
  })

  it('gives every field its own id, so two on one screen do not collide', () => {
    render(
      <>
        <Field label="One">{(c) => <TextInput {...c} />}</Field>
        <Field label="Two">{(c) => <TextInput {...c} />}</Field>
      </>,
    )

    const one = screen.getByLabelText('One')
    const two = screen.getByLabelText('Two')
    expect(one.id).not.toBe('')
    expect(one.id).not.toBe(two.id)
  })

  it('never points a label at nothing when the slot holds no control', () => {
    const { container } = render(
      <Field label="Collaborations" hint="nothing measures this yet">
        <div data-placeholder="collaborations">not wired</div>
      </Field>,
    )

    // The node form wraps its content, so there is no `for` to dangle.
    const labels = container.querySelectorAll('label')
    expect(labels).toHaveLength(1)
    expect(labels[0]?.hasAttribute('for')).toBe(false)
    expect(screen.getByText('not wired')).toBeInTheDocument()
  })

  it('renders the label and hint text in both forms', () => {
    const { rerender } = render(
      <Field label="Aliases" hint="the hint">
        <span>slot</span>
      </Field>,
    )
    expect(screen.getByText('Aliases')).toBeInTheDocument()
    expect(screen.getByText('the hint')).toBeInTheDocument()

    rerender(
      <Field label="Aliases" hint="the hint">
        {(c) => <TextInput {...c} />}
      </Field>,
    )
    expect(screen.getByText('Aliases')).toBeInTheDocument()
    expect(screen.getByText('the hint')).toBeInTheDocument()
  })
})

describe('TextArea', () => {
  it('is labelled by the Field it sits in, like any other control', () => {
    render(
      <Field label="Aliases" hint="One per line.">
        {(control) => <TextArea {...control} defaultValue={'Nonkeen\nVictor Solf'} />}
      </Field>,
    )

    const box = screen.getByLabelText('Aliases')
    expect(box.tagName).toBe('TEXTAREA')
    expect(box).toHaveValue('Nonkeen\nVictor Solf')
    expect(box).toHaveAccessibleDescription('One per line.')
  })

  it('is four lines tall unless a caller says otherwise', () => {
    const { container, rerender } = render(<TextArea />)
    expect(container.querySelector('textarea')).toHaveAttribute('rows', '4')

    rerender(<TextArea rows={8} />)
    expect(container.querySelector('textarea')).toHaveAttribute('rows', '8')
  })

  it('accepts typing, including a newline, and reports it', async () => {
    const onChange = vi.fn()
    render(
      <Field label="Aliases">{(c) => <TextArea {...c} onChange={onChange} />}</Field>,
    )

    await userEvent.type(screen.getByLabelText('Aliases'), 'a{enter}b')

    expect(onChange).toHaveBeenCalledTimes(3)
  })
})

describe('TextInput', () => {
  it('accepts typing and reports it', async () => {
    const onChange = vi.fn()
    render(
      <Field label="MusicBrainz artist">
        {(c) => <TextInput {...c} onChange={onChange} />}
      </Field>,
    )

    await userEvent.type(screen.getByLabelText('MusicBrainz artist'), 'ab')

    expect(onChange).toHaveBeenCalledTimes(2)
  })

  it('does not accept typing when it is disabled', async () => {
    const onChange = vi.fn()
    render(
      <Field label="Aliases">
        {(c) => <TextInput {...c} disabled onChange={onChange} />}
      </Field>,
    )

    const input = screen.getByLabelText('Aliases')
    await userEvent.type(input, 'anything')

    expect(onChange).not.toHaveBeenCalled()
    expect(input).toBeDisabled()
  })

  it('shows a read-only value without pretending it can be edited', async () => {
    const onChange = vi.fn()
    render(
      <Field label="Release MBID">
        {(c) => (
          <TextInput {...c} readOnly mono value="984f8239" onChange={onChange} />
        )}
      </Field>,
    )

    const input = screen.getByLabelText('Release MBID')
    await userEvent.type(input, 'x')

    expect(input).toHaveValue('984f8239')
    expect(onChange).not.toHaveBeenCalled()
  })

  it('takes the mono face only when asked', () => {
    const { container, rerender } = render(<TextInput mono />)
    const withMono = container.querySelector('input')?.className ?? ''

    rerender(<TextInput />)
    const without = container.querySelector('input')?.className ?? ''

    expect(withMono).not.toBe(without)
  })

  it('is a text input by default and lets a caller say otherwise', () => {
    const { container, rerender } = render(<TextInput />)
    expect(container.querySelector('input')).toHaveAttribute('type', 'text')

    rerender(<TextInput type="search" />)
    expect(container.querySelector('input')).toHaveAttribute('type', 'search')
  })
})
