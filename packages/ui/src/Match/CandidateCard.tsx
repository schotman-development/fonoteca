import type { HTMLAttributes, ReactNode, Ref } from 'react'

import { Artwork, type ArtworkShape } from '../Artwork/Artwork.tsx'
import { Badge } from '../Badge/Badge.tsx'
import { Disclosure } from '../Disclosure/Disclosure.tsx'
import { Text } from '../Text/Text.tsx'
import styles from './CandidateCard.module.css'

/**
 * What a selection mechanism hands the card.
 *
 * All-or-nothing on purpose: a `control` without a `labelFor` is a radio with no
 * accessible name, which is an axe failure, and one optional object says so
 * without a discriminated union that `exactOptionalPropertyTypes` would make
 * miserable to build.
 */
export type CandidateCardSelection = {
  /** The control itself. `CandidateList` passes an `<input type="radio">`. */
  readonly control: ReactNode
  /** The control's id. The identity block becomes a `<label htmlFor>` for it. */
  readonly labelFor: string
  /** Put on the fit region, so the control can describe itself with the evidence. */
  readonly fitId: string
  readonly selected: boolean
}

export type CandidateCardProps = Omit<HTMLAttributes<HTMLDivElement>, 'title'> & {
  /** Inside the label, and therefore part of the control's accessible name. */
  readonly title: string
  /** The pre-joined credit line. Also inside the label. */
  readonly subtitle?: ReactNode
  /** Year · country · status · format · discs and tracks. Also inside the label. */
  readonly facts?: ReactNode
  /**
   * Whether `facts` is figures.
   *
   * Default true, because it usually is — years, track counts, durations, all of
   * which want tabular figures and a column that lines up down a list. Pass
   * false when the line is a *sentence* ("same length as your file"): monospace
   * makes English read as output rather than as prose, which is the opposite of
   * what a plain-language line is for.
   */
  readonly factsMono?: boolean
  /**
   * The small print — label, catalogue number, barcode. **Outside** the label: a
   * catalogue number in an accessible name is noise, not identity.
   */
  readonly detail?: ReactNode
  readonly badges?: ReactNode
  /** `FitSummary` for a release, a score row for a recording, nothing for an artist. */
  readonly fit?: ReactNode
  /** Where a play control goes. Outside the label, so clicking it selects nothing. */
  readonly actions?: ReactNode
  readonly image?: string
  readonly artworkShape?: ArtworkShape
  /** The expandable body — a `SlotTable`, usually. Omit and no disclosure appears. */
  readonly children?: ReactNode
  readonly disclosureLabel?: ReactNode
  readonly disclosureAside?: ReactNode
  readonly defaultExpanded?: boolean
  readonly expanded?: boolean
  readonly onExpandedChange?: (expanded: boolean) => void
  readonly selection?: CandidateCardSelection
  readonly ref?: Ref<HTMLDivElement>
}

/**
 * One option, with the evidence for it.
 *
 * **A sibling of `CatalogueCard`, not a variant of it**, and the reasons are
 * structural rather than aesthetic:
 *
 * 1. `CatalogueCard`'s whole contract is "I have no width; the grid decides". A
 *    candidate is a wide row whose columns are its own.
 * 2. `CatalogueCard` renders as an `<a>` or a `<span>`, and a candidate must
 *    contain a disclosure button, a play button and a radio — none of which may
 *    live inside an anchor.
 * 3. `data-catalogue-card` is load-bearing: `CatalogueGrid` sizes its columns by
 *    looking for it, and its track widths are pinned to a tenth of a pixel in a
 *    play function. A third variant would either silently resize any grid it
 *    landed in or force that selector to be rewritten.
 *
 * What *is* shared is shared at `Artwork`, which owns the square, the circle, the
 * crop and the monogram for both.
 *
 * **The radio is clipped out of sight, and the card shows its state instead.**
 * It is hidden the way `VisuallyHidden` hides text — clipped to a pixel, still
 * rendered — never with `display: none`, `visibility: hidden` or the `hidden`
 * attribute, all of which would take it out of the tab order and the
 * accessibility tree and end the arrow-key navigation, the "2 of 6" position and
 * the group semantics that are the whole reason the radios are native. What the
 * browser drew for free is drawn here instead: a marker in the label's own
 * gutter, the thick accent rule down the inline-start edge, the "Selected"
 * badge, and a focus ring `:has()` puts on the card.
 * `ControlIsHiddenNotRemoved` is the story that holds that line.
 *
 * **The marker is not decoration, and leaving it out was a real defect.** The
 * three cues above are all *selected* states: until somebody picks something,
 * a card carrying six paragraphs of evidence and no control is indistinguishable
 * from a read-only panel. On the live screen that put the only visible radio on
 * "None of these" — so the one answer nobody wants to give was the one that
 * looked like an answer. The marker is `aria-hidden` paint over a native radio
 * that is still doing all the work.
 *
 * **The label covers only the identity block.** The disclosure toggle and the
 * actions slot sit outside it, because a click anywhere in a `<label>` forwards
 * to its control — so a play button inside one would select the candidate every
 * time somebody listened to it. axe will not catch that: a label is not
 * interactive per ARIA, so nothing about it is a violation. The `NestedControls`
 * story is what stands between this design and that regression.
 */
export function CandidateCard({
  title,
  subtitle,
  facts,
  factsMono = true,
  detail,
  badges,
  fit,
  actions,
  image,
  artworkShape = 'square',
  children,
  disclosureLabel = 'Track by track',
  disclosureAside,
  defaultExpanded = false,
  expanded,
  onExpandedChange,
  selection,
  className,
  ...rest
}: CandidateCardProps) {
  const identity = (
    <>
      {/*
        The radio the browser is no longer drawing, drawn here.

        `aria-hidden`, because the real control is a native radio a pixel away in
        the accessibility tree and announcing a second one would make every
        option read twice. This is paint, and it exists because the clip that
        buys the native semantics also bought a card with **no visible
        affordance at all**: measured on the live screen, six real answers looked
        like read-only information panels while "None of these" — the only row
        whose radio was never clipped — looked like the one thing on the screen a
        person could pick.
      */}
      {selection != null ? <span className={styles.marker} aria-hidden="true" /> : null}
      <Artwork
        name={title}
        shape={artworkShape}
        size="md"
        className={styles.art}
        {...(image != null ? { src: image } : {})}
      />
      <span className={styles.identityText}>
        <Text size="sm" weight="semibold" block>
          {title}
        </Text>
        {subtitle != null ? (
          <Text size="xs" tone="secondary" block>
            {subtitle}
          </Text>
        ) : null}
        {facts != null ? (
          <Text size="xs" tone="tertiary" family={factsMono ? 'mono' : 'sans'} block>
            {facts}
          </Text>
        ) : null}
      </span>
    </>
  )

  return (
    <div
      className={className ? `${styles.root} ${className}` : styles.root}
      data-selected={selection?.selected || undefined}
      data-selectable={selection != null || undefined}
      {...rest}
    >
      {selection != null ? <div className={styles.control}>{selection.control}</div> : null}

      {selection != null ? (
        <label className={styles.identity} htmlFor={selection.labelFor}>
          {identity}
        </label>
      ) : (
        <div className={styles.identity}>{identity}</div>
      )}

      <div className={styles.aside}>
        {badges != null ? <div className={styles.badges}>{badges}</div> : null}
        {/*
          `aria-hidden`, because the radio already announces that it is checked.
          It exists so that selection is legible as a *word* as well as a shape
          and a position — remove every colour and the card still says which one
          was chosen.
        */}
        {selection?.selected === true ? (
          <Badge tone="accent" size="sm" aria-hidden="true">
            Selected
          </Badge>
        ) : null}
        {actions != null ? <div className={styles.actions}>{actions}</div> : null}
      </div>

      {fit != null ? (
        <div className={styles.fit} {...(selection != null ? { id: selection.fitId } : {})}>
          {fit}
        </div>
      ) : null}

      {detail != null ? (
        <div className={styles.detail}>
          <Text size="2xs" tone="tertiary" family="mono">
            {detail}
          </Text>
        </div>
      ) : null}

      {children != null ? (
        <div className={styles.body}>
          <Disclosure
            size="sm"
            summary={disclosureLabel}
            defaultOpen={defaultExpanded}
            {...(expanded != null ? { open: expanded } : {})}
            {...(onExpandedChange != null ? { onOpenChange: onExpandedChange } : {})}
            {...(disclosureAside != null ? { aside: disclosureAside } : {})}
          >
            {children}
          </Disclosure>
        </div>
      ) : null}
    </div>
  )
}
