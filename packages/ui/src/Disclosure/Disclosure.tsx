import { type HTMLAttributes, type ReactNode, type Ref, useId, useState } from 'react'

import styles from './Disclosure.module.css'

export type DisclosureProps = Omit<HTMLAttributes<HTMLDivElement>, 'children'> & {
  /** The button's label, and therefore its accessible name. */
  readonly summary: ReactNode
  /**
   * At the end of the button row — a count, a badge.
   *
   * Deliberately **inside** the button, so it joins the accessible name:
   * "Track by track, 10 of 10" is one answer, and splitting it out would leave
   * the count unreachable from the control it describes.
   */
  readonly aside?: ReactNode
  /**
   * Between the button and the panel, and visible in both states.
   *
   * For the line that has to survive the collapse — what this section is *for*,
   * or what to do about it — as against the body, which is what a person opened
   * it to read. Outside the button rather than inside it, because `summary` is
   * the accessible name and a name is a label rather than a paragraph; a
   * sentence appended there would be read out in full every time focus landed on
   * the control.
   */
  readonly detail?: ReactNode
  readonly defaultOpen?: boolean
  /** Controlled. Omit it and the component keeps its own state. */
  readonly open?: boolean
  readonly onOpenChange?: (open: boolean) => void
  readonly size?: 'sm' | 'md'
  readonly children: ReactNode
  readonly ref?: Ref<HTMLDivElement>
}

/**
 * A button that shows and hides a region — the WAI-ARIA **Disclosure** pattern.
 *
 * Not `<details>`/`<summary>`. `<summary>`'s implicit role still differs between
 * engines, it cannot reliably hold interactive content, and its whole content
 * becomes the accessible name whether or not that was the intention. A
 * `<button aria-expanded aria-controls>` plus a region is fully specified,
 * needs no focus management, and behaves the same everywhere — which is why
 * this needs no ADR of its own while a menu or a combobox would.
 *
 * **The panel is always in the DOM**, toggled with the `hidden` attribute rather
 * than conditionally rendered: `aria-controls` pointing at an id that does not
 * exist is an `aria-valid-attr-value` failure, and it would appear only in the
 * collapsed state — the state most stories open in.
 */
export function Disclosure({
  summary,
  aside,
  detail,
  defaultOpen = false,
  open,
  onOpenChange,
  size = 'md',
  children,
  className,
  ...rest
}: DisclosureProps) {
  const panelId = `${useId()}-panel`
  const [uncontrolled, setUncontrolled] = useState(defaultOpen)
  const isOpen = open ?? uncontrolled

  return (
    <div
      className={className ? `${styles.root} ${className}` : styles.root}
      data-size={size}
      data-open={isOpen || undefined}
      {...rest}
    >
      <button
        type="button"
        className={styles.trigger}
        aria-expanded={isOpen}
        aria-controls={panelId}
        onClick={() => {
          // Set from the value we just rendered, not from the previous state:
          // in the controlled case there is no local state to read.
          if (open === undefined) setUncontrolled(!isOpen)
          onOpenChange?.(!isOpen)
        }}
      >
        <span className={styles.marker} aria-hidden="true" />
        <span className={styles.summary}>{summary}</span>
        {aside != null ? <span className={styles.aside}>{aside}</span> : null}
      </button>
      {detail != null ? <div className={styles.detail}>{detail}</div> : null}
      <div className={styles.panel} id={panelId} hidden={!isOpen}>
        {children}
      </div>
    </div>
  )
}
