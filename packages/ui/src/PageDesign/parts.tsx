import { Badge } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import styles from './PageDesign.module.css'

/*
  What the artist and album design candidates share. Story scaffolding, not
  exported from the package.
*/

/** Where the text came from travels with it: it is somebody else's prose, under their licence. */
export type Written = {
  readonly text: string
  readonly source: string
  readonly url: string | null
  readonly byPerson: boolean
}

export function countryName(code: string | null): string | null {
  if (code == null || !/^[A-Za-z]{2}$/.test(code)) return code
  return (
    new Intl.DisplayNames('en', { type: 'region', fallback: 'code' }).of(code.toUpperCase()) ?? code
  )
}

export const stamp = (utc: string | null) =>
  utc == null
    ? 'Not asked yet'
    : new Date(utc).toLocaleString('en-GB', {
        dateStyle: 'medium',
        timeStyle: 'short',
        timeZone: 'UTC',
      })

/**
 * Rule 3 on screen: a blank after the lookup is "the provider holds none", a
 * blank before it is "not asked yet". The two must never print alike.
 */
export function Value({
  value,
  asked,
  mono = false,
}: {
  readonly value: string | null
  readonly asked: boolean
  readonly mono?: boolean
}) {
  if (value != null && value !== '') {
    return mono ? (
      <Text size="sm" family="mono">
        {value}
      </Text>
    ) : (
      value
    )
  }
  return (
    <Text size="sm" tone="tertiary">
      {asked ? 'None recorded' : 'Not asked yet'}
    </Text>
  )
}

export function Edited({ by }: { readonly by: boolean }) {
  return by ? (
    <>
      {' '}
      <Badge tone="accent" size="sm">
        set by you
      </Badge>
    </>
  ) : null
}

/**
 * A biography or a review. Rule 3 again: no text after the lookup and no lookup
 * at all are different sentences. With `onMore` it is an excerpt whose "Read
 * more" opens the tab holding the whole text, as Roon's does.
 */
export function Prose({
  written,
  asked,
  none,
  onMore,
}: {
  readonly written: Written | null
  readonly asked: boolean
  /** What to say when the lookup found nothing. */
  readonly none: string
  readonly onMore?: () => void
}) {
  if (written == null) {
    return (
      <Text size="sm" tone="tertiary">
        {asked ? none : 'Not looked for yet.'}
      </Text>
    )
  }

  // ponytail: length stands in for measured overflow; a short text is shown whole.
  const excerpt = onMore != null && written.text.length > 400

  const credit = written.byPerson ? (
    'Written by you'
  ) : written.url == null ? (
    `From ${written.source}`
  ) : (
    <>
      From{' '}
      <a className={styles.link} href={written.url}>
        {written.source}
      </a>
    </>
  )

  return (
    <Stack direction="column" gap={8} align="start">
      <div className={styles.bio} {...(excerpt ? { 'data-collapsed': '' } : {})}>
        {written.text.split(/\n\s*\n/).map((paragraph, index) => (
          // biome-ignore lint/suspicious/noArrayIndexKey: static paragraphs, never reordered
          <p key={index}>{paragraph}</p>
        ))}
      </div>
      <Stack gap={12} align="center" wrap>
        {excerpt ? (
          <Button size="sm" variant="ghost" onClick={onMore}>
            Read more
          </Button>
        ) : null}
        <Text size="xs" tone="tertiary">
          {credit}
        </Text>
      </Stack>
    </Stack>
  )
}

export { Tabs } from '../Tabs/Tabs.tsx'
