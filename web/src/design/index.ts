/**
 * The design system's public surface.
 *
 * **Screens import from `@/design`, never from a file inside it.** That is what
 * keeps a primitive free to move, gain a sibling or change its internals
 * without a screen knowing — and it is what makes "does anything outside this
 * directory reach past the barrel?" a one-line grep rather than an audit.
 *
 * Nothing in here knows what a release, an artist, a queue item or Qobuz is.
 * A component that needs one of those words belongs in `@/widgets`.
 *
 * ── the foundations ───────────────────────────────────────────────────────
 *
 *   cx            the class-name joiner every primitive builds its list with
 *   types         Tone / Size / Shape / the navigable and styleable prop halves
 *   artwork       the placeholder-art hash and the two initials rules
 *
 * and, not exported because CSS is not a value, the shared recipes under
 * `design/shared/`. Each is defined ONCE and composed by its users — through
 * `composes:` where the composer has a module of its own, or by importing the
 * recipe module and joining the class names with `cx` where it does not. A
 * primitive that re-declares one of them has forked it:
 *
 *   shared/label.module.css    `.label` — the eyebrow (11px, uppercase,
 *                              --ls-label, --c-ink-mark) and `.labelNav`, the
 *                              same shape at --ls-nav for a Figure caption.
 *                              Composed by Eyebrow, Field, Figure, Drawer.
 *   shared/chip.module.css     `.chip` / `.chipSm` — the two pill geometries,
 *                              plus `.dot`, the categorical source marker.
 *                              Composed by Chip and SourceChip. Colour is NOT
 *                              here: a Chip is never tinted.
 *   shared/switch.module.css   `.track` / `.trackOn` / `.knob` / `.knobOn` —
 *                              the 34×19 track and 15px knob, with the knob's
 *                              on-position derived rather than written as 17px.
 *                              Composed by Toggle, and so by SwitchField.
 *   shared/surface.module.css  `.surface` / `.hoverEdge` / `.surfaceInset` —
 *                              the hairline card edge, over paper or canvas.
 *                              Composed by StatCard and PageError.
 *   shared/meta.module.css     `.meta` — the mono micro-label, the design's
 *                              quiet second line, and `.metaMuted` for a count
 *                              that must follow its container's colour.
 *                              Composed by Chip and SourceChip.
 *
 * There is deliberately no `shared/codeblock.module.css`: the mono block has
 * exactly one user (`CodeBlock`, which covers design lines 480–486, 638–650,
 * 688–692 and 722), and a shared recipe with one composer is indirection
 * without a payoff. Give it its own module. The rule for adding to `shared/`
 * is the same in both directions — two composers or it stays put.
 *
 * ── the primitives ────────────────────────────────────────────────────────
 *
 * One directory per primitive — `Button/Button.tsx`, `Button/Button.module.css`,
 * `Button/Button.test.tsx`, `Button/index.ts` — re-exported below with its prop
 * types, grouped the way they are used rather than alphabetically:
 *
 *   controls    Button, Chip, Toggle, SwitchField, Slider
 *   type & data Eyebrow, SectionHead, StatCard, Figure, Meter, Spinner, Pips
 *   imagery     Artwork, Badge, SpecBadge
 *   containers  Shelf, ListRow, KeyValueList, CodeBlock, SourceChip
 *   forms       Field, EmptyState, Drawer
 *   toasts      Toast, ToastHost, ToastProvider, useToast
 *   states      Placeholder, PageError, PageLoading, ErrorBoundary
 *
 * A primitive may compose another one — SwitchField renders Toggle, Shelf and
 * Drawer render Button, Button renders Spinner, ErrorBoundary renders
 * PageError. That is deliberate and it is the point: the alternative, which
 * this layer briefly had, is the same control drawn twice in two modules,
 * differing the moment either is retuned.
 *
 * Three rules are not negotiable and are the easiest thing to lose in a
 * refactor, so they are written here as well as beside the components:
 *
 *   - `ToggleProps.onToggle` is `() => void`. It must NOT be widened to receive
 *     a value: the monitor endpoint toggles on an empty body, and the bug this
 *     prevents is a filter named `monitored` being read as the album's new
 *     state.
 *   - `Meter`'s `null` is not a zero. Nothing has measured it yet, so it draws
 *     an empty track and no number — never a 0% bar.
 *   - A `Chip` is never tinted with a status colour. It has no `tone` prop and
 *     must not gain one: in this system colour means how bad something is, and
 *     a chip means which subset you are looking at.
 *
 * `Placeholder` is the one primitive that is not in the design. It marks a
 * datum the API does not have, carries `data-placeholder` so a test can count
 * them, and shows the em dash from `@/format` where a figure would go — never
 * an invented number.
 */

/* ---- foundations --------------------------------------------------------- */

export { cx } from '@/design/cx'
export type { ClassPart } from '@/design/cx'

export type {
  NavigableProps,
  PolymorphicTo,
  Shape,
  Size,
  StyleableProps,
  Tone,
} from '@/design/types'

export {
  ART_COUNT,
  artGradientVar,
  artIndex,
  initialsOf,
  initialsOrDash,
} from '@/design/artwork'
export type { InitialsMode } from '@/design/artwork'

/* ---- controls ------------------------------------------------------------ */

export { Button } from '@/design/Button'
export type { ButtonProps, ButtonSize, ButtonVariant } from '@/design/Button'

export { Chip } from '@/design/Chip'
export type { ChipProps, ChipSize } from '@/design/Chip'

export { Toggle } from '@/design/Toggle'
export type { ToggleProps } from '@/design/Toggle'

export { SwitchField } from '@/design/SwitchField'
export type { SwitchFieldProps } from '@/design/SwitchField'

export { Slider } from '@/design/Slider'
export type { SliderProps } from '@/design/Slider'

/* ---- type and data ------------------------------------------------------- */

export { Eyebrow } from '@/design/Eyebrow'
export type {
  EyebrowActionAlign,
  EyebrowElement,
  EyebrowProps,
} from '@/design/Eyebrow'

export { SectionHead } from '@/design/SectionHead'
export type { SectionHeadProps } from '@/design/SectionHead'

export { StatCard } from '@/design/StatCard'
export type { StatCardProps } from '@/design/StatCard'

export { Figure } from '@/design/Figure'
export type { FigureProps, FigureSize } from '@/design/Figure'

export { clampFraction, Meter } from '@/design/Meter'
export type { MeterProps, MeterThickness } from '@/design/Meter'

export { Spinner } from '@/design/Spinner'
export type { SpinnerProps, SpinnerSize, SpinnerTone } from '@/design/Spinner'

export { Pips } from '@/design/Pips'
export type { PipsProps } from '@/design/Pips'

/* ---- imagery ------------------------------------------------------------- */

export { Artwork, initialsFontSize, squareRadiusVar } from '@/design/Artwork'
export type { ArtworkProps, ArtworkSize } from '@/design/Artwork'

export { Badge } from '@/design/Badge'
export type { BadgeProps } from '@/design/Badge'

export { SpecBadge } from '@/design/SpecBadge'
export type { SpecBadgeProps } from '@/design/SpecBadge'

/* ---- containers ---------------------------------------------------------- */

export { Shelf, SHELF_STEP_PX } from '@/design/Shelf'
export type { ShelfProps } from '@/design/Shelf'

export { ListRow } from '@/design/ListRow'
export type {
  ClickableListRowProps,
  ListRowProps,
  StaticListRowProps,
} from '@/design/ListRow'

export { KeyValue, KeyValueList } from '@/design/KeyValueList'
export type { KeyValueListProps, KeyValueProps } from '@/design/KeyValueList'

export { CodeBlock } from '@/design/CodeBlock'
export type { CodeBlockProps, CodeBlockTone } from '@/design/CodeBlock'

export { SourceChip } from '@/design/SourceChip'
export type { SourceChipProps } from '@/design/SourceChip'

/* ---- forms and panels ---------------------------------------------------- */

export { Field, TextArea, TextInput } from '@/design/Field'
export type {
  FieldControl,
  FieldProps,
  TextAreaProps,
  TextInputProps,
} from '@/design/Field'

export { EmptyState } from '@/design/EmptyState'
export type { EmptyStateProps } from '@/design/EmptyState'

export { Drawer } from '@/design/Drawer'
export type { DrawerProps } from '@/design/Drawer'

/* ---- toasts -------------------------------------------------------------- */

export { Toast, TOAST_DURATION_MS, ToastHost, ToastProvider, useToast } from '@/design/Toast'
export type {
  ShowToast,
  ToastLevel,
  ToastMessage,
  ToastProps,
  ToastProviderProps,
} from '@/design/Toast'

/* ---- the states a screen can be in --------------------------------------- */

export { Placeholder } from '@/design/Placeholder'
export type { PlaceholderProps, PlaceholderVariant } from '@/design/Placeholder'

export { PageError } from '@/design/PageError'
export type { PageErrorProps } from '@/design/PageError'

export { PageLoading } from '@/design/PageLoading'
export type { PageLoadingProps } from '@/design/PageLoading'

export { ErrorBoundary } from '@/design/ErrorBoundary'
export type {
  ErrorBoundaryFallbackArgs,
  ErrorBoundaryProps,
} from '@/design/ErrorBoundary'
