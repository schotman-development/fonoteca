import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { describe, expect, it } from 'vitest'

import { queryKeys } from '@/api/queries'
import { QualityBadge } from '@/widgets/QualityBadge/QualityBadge'

/**
 * `useMeta()` is seeded rather than fetched: `format_labels` is the server's
 * vocabulary and the badge's whole job is to read it, so a test that stubbed
 * the label would be testing itself. `retry: false` keeps an unseeded case
 * from waiting on a backoff.
 */
function withMeta(labels: Record<string, string> | null) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  if (labels !== null) {
    client.setQueryData(queryKeys.meta(), { format_labels: labels })
  }
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>
  }
}

const LABELS = { '6': 'FLAC 16bit 44.1kHz', '7': 'FLAC 24bit 96kHz' }

describe('QualityBadge', () => {
  it("renders the server's own label for the id", () => {
    render(<QualityBadge formatId={7} />, { wrapper: withMeta(LABELS) })

    expect(screen.getByText('FLAC 24bit 96kHz')).toBeInTheDocument()
  })

  it('renders NOTHING when the format id is null', () => {
    // `owned_format_id` is null when nothing is on disk. An empty pill would
    // read as a measurement that came back blank.
    const { container } = render(<QualityBadge formatId={null} />, {
      wrapper: withMeta(LABELS),
    })

    expect(container).toBeEmptyDOMElement()
  })

  it('renders nothing for an id the vocabulary has no word for', () => {
    const { container } = render(<QualityBadge formatId={999} />, {
      wrapper: withMeta(LABELS),
    })

    expect(container).toBeEmptyDOMElement()
  })

  it('renders nothing before the vocabulary has arrived', () => {
    // One frame with no badge beats one frame with a wrong one.
    const { container } = render(<QualityBadge formatId={7} />, {
      wrapper: withMeta(null),
    })

    expect(container).toBeEmptyDOMElement()
  })

  it('is a label, not a control', () => {
    render(<QualityBadge formatId={6} />, { wrapper: withMeta(LABELS) })

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
