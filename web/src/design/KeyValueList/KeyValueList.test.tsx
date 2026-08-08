import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { EM_DASH } from '@/format'
import { KeyValue, KeyValueList } from '@/design/KeyValueList/KeyValueList'

describe('KeyValueList', () => {
  it('renders a real definition list of dt/dd pairs', () => {
    const { container } = render(
      <KeyValueList>
        <KeyValue label="Release MBID" value="984f8239-8fe1" />
      </KeyValueList>,
    )

    expect(container.querySelector('dl')).not.toBeNull()
    expect(container.querySelector('dt')?.textContent).toBe('Release MBID')
    expect(container.querySelector('dd')?.textContent).toBe('984f8239-8fe1')
  })

  it.each([null, undefined, ''])(
    'renders the em dash rather than an empty cell for %p',
    (value) => {
      render(
        <KeyValueList>
          <KeyValue label="Barcode" value={value} />
        </KeyValueList>,
      )

      expect(screen.getByText(EM_DASH)).toBeInTheDocument()
    },
  )

  it('does not mistake a zero for an absence', () => {
    render(
      <KeyValueList>
        <KeyValue label="Attempts" value={0} />
      </KeyValueList>,
    )

    expect(screen.getByText('0')).toBeInTheDocument()
    expect(screen.queryByText(EM_DASH)).not.toBeInTheDocument()
  })

  it('marks the ruled variant on the list, not on every row', () => {
    const { container, rerender } = render(
      <KeyValueList ruled>
        <KeyValue label="k" value="v" />
      </KeyValueList>,
    )
    const ruled = container.querySelector('dl')?.className ?? ''

    rerender(
      <KeyValueList>
        <KeyValue label="k" value="v" />
      </KeyValueList>,
    )
    const plain = container.querySelector('dl')?.className ?? ''

    expect(ruled).not.toBe(plain)
  })

  it('colours only the value, and only when a tone was asked for', () => {
    const { container } = render(
      <KeyValueList>
        <KeyValue label="State" value="ambiguous" tone="warn" />
        <KeyValue label="Source" value="musicbrainz" />
      </KeyValueList>,
    )

    const [toned, plain] = Array.from(container.querySelectorAll('dd'))
    expect(toned?.className).not.toBe(plain?.className)
    expect(container.querySelector('dt')?.className).toBe(
      container.querySelectorAll('dt')[1]?.className,
    )
  })

  it('renders a node value as given', () => {
    render(
      <KeyValueList>
        <KeyValue label="Link" value={<a href="/x">open</a>} />
      </KeyValueList>,
    )

    expect(screen.getByRole('link', { name: 'open' })).toBeInTheDocument()
  })
})
