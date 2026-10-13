import { Button, Stack, Text } from '@fonoteca/ui'

/**
 * Which slice of a paged list is showing, and the way to the next.
 *
 * Previous and next rather than numbered pages: on a list ordered by title
 * page 41 means nothing, and the filter is the way to a particular row.
 */
export function Pager({
  page,
  size,
  total,
  noun,
  onPage,
}: {
  /** One-based. */
  readonly page: number
  readonly size: number
  readonly total: number
  /** Plural, as the count reads: "tracks". */
  readonly noun: string
  readonly onPage: (page: number) => void
}) {
  const first = (page - 1) * size + 1
  const last = Math.min(page * size, total)

  return (
    <Stack gap={12} align="center" wrap>
      <Text size="sm" tone="tertiary">
        {total <= size
          ? `${total.toLocaleString()} ${noun}`
          : `${first.toLocaleString()}–${last.toLocaleString()} of ${total.toLocaleString()} ${noun}`}
      </Text>
      {total > size ? (
        <>
          <Button variant="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)}>
            Previous
          </Button>
          <Button variant="secondary" disabled={last >= total} onClick={() => onPage(page + 1)}>
            Next
          </Button>
        </>
      ) : null}
    </Stack>
  )
}
