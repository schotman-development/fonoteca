import type { components } from '@fonoteca/api-client'
import { Badge, Button, Stack, Text } from '@fonoteca/ui'

import styles from './Profile.module.css'

/*
 * What the artist and album pages share: the prose block, the rule-3 value
 * cell and the "set by you" mark.
 */

type WrittenText = components['schemas']['WrittenText']

export const stamp = (utc: string | null) =>
  utc == null
    ? 'Not asked yet'
    : new Date(utc).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/**
 * Rule 3 on screen: a blank after the lookup is "none recorded", a blank before
 * it is "not asked yet". The two must never print alike.
 */
export function Value({
  value,
  asked,
  mono = false,
}: {
  readonly value: string | null | undefined
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

/** Rule 4 on screen: what a person set is marked as theirs, beside the value. */
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
 * A biography or a review, credited to where it came from. With `onMore` it is
 * an excerpt whose "Read more" opens the whole text, as Roon's does.
 */
export function Prose({
  written,
  asked,
  none,
  onMore,
}: {
  readonly written: WrittenText | null
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

  const credit =
    written.byPerson && written.url == null ? (
      'Written by you'
    ) : written.url == null ? (
      `From ${written.source}`
    ) : (
      <>
        {written.byPerson ? 'Edited by you, from' : 'From'}{' '}
        <a className={styles.link} href={written.url} target="_blank" rel="noreferrer">
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

/** A form field's value as the wire wants it: blank is null. */
export const blank = (value: string) => (value.trim() === '' ? null : value)

/** A year field's value: blank is null, anything else a number. */
export const year = (value: string) => (value.trim() === '' ? null : Number(value))

/** A comma-separated field as a list. */
export const list = (value: string) =>
  value
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)
