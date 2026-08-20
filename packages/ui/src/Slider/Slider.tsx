import {
  type CSSProperties,
  type HTMLAttributes,
  type PointerEvent as ReactPointerEvent,
  type Ref,
  useRef,
  useState,
} from 'react'

import styles from './Slider.module.css'

/**
 * A slider has no text of its own, so a name is not optional — axe's
 * `aria-input-field-name` fails the build without one. Expressing it as a union
 * turns that runtime failure into a compile error.
 */
export type SliderLabel =
  | { readonly 'aria-label': string; readonly 'aria-labelledby'?: never }
  | { readonly 'aria-labelledby': string; readonly 'aria-label'?: never }

export type SliderOrientation = 'horizontal' | 'vertical'

export type SliderBaseProps = Omit<
  HTMLAttributes<HTMLDivElement>,
  // The ARIA is derived, not accepted. `{...rest}` spreads last, so a caller
  // could otherwise overwrite the attributes that make this a slider at all —
  // the same reasoning as ThemeSwitch omitting `role` and `aria-checked`.
  | 'role'
  | 'tabIndex'
  | 'aria-valuenow'
  | 'aria-valuemin'
  | 'aria-valuemax'
  | 'aria-valuetext'
  | 'aria-orientation'
  | 'aria-label'
  | 'aria-labelledby'
  // A div has no onChange and no defaultValue; leaving them in the type invites
  // a caller to pass one and watch nothing happen.
  | 'onChange'
  | 'defaultValue'
> & {
  readonly value: number
  readonly min?: number
  readonly max: number
  /** The arrow-key increment, and the quantisation every value is snapped to. */
  readonly step?: number
  /** PageUp / PageDown. Defaults to ten steps. */
  readonly largeStep?: number
  /** Fires continuously — on every arrow key and every pointer move. */
  readonly onValueChange: (value: number) => void
  /**
   * Fires once per interaction: on pointer release, and on each key press.
   *
   * That asymmetry is the entire "scrub without fighting `timeupdate`"
   * mechanism. A seek issued on every pixel of a drag would send a range request
   * per mouse move; a seek issued on release sends one.
   */
  readonly onValueCommit?: (value: number) => void
  readonly orientation?: SliderOrientation
  /** `aria-disabled`, not `disabled`: it stays focusable so it stays findable. */
  readonly disabled?: boolean
  /** Produces `aria-valuetext`. The attribute is omitted when this is absent. */
  readonly formatValue?: (value: number, max: number) => string
  /** A second, purely visual band behind the fill — buffered audio. Never in ARIA. */
  readonly secondaryValue?: number
  readonly sliderSize?: 'sm' | 'md'
  readonly tone?: 'accent' | 'neutral'
  readonly ref?: Ref<HTMLDivElement>
}

export type SliderProps = SliderBaseProps & SliderLabel

/**
 * The WAI-ARIA **Slider** pattern, hand-built. See ADR 0009 for why, including
 * why `<input type="range">` was rejected.
 *
 * `role="slider"` sits on the **thumb**, per the APG examples: the focus ring
 * then points at the value rather than surrounding a 200px bar. The rail is
 * `aria-hidden` and is the pointer target, which is why a click on it explicitly
 * focuses the thumb.
 *
 * Pointer capture rather than a document-level listener, so a drag that leaves
 * the element still tracks and unmounting mid-drag cannot leak a handler.
 *
 * **There is no `indeterminate` prop.** ARIA 1.2 makes `aria-valuenow` required
 * on `slider` and axe enforces it, so an unknown length is a zero-length range
 * that reports `aria-valuenow={min}` and whatever `formatValue` says — not a
 * control that swaps roles under a focused user the moment metadata arrives.
 */
export function Slider({
  value,
  min = 0,
  max,
  step = 1,
  largeStep,
  onValueChange,
  onValueCommit,
  orientation = 'horizontal',
  disabled = false,
  formatValue,
  secondaryValue,
  sliderSize = 'md',
  tone = 'accent',
  'aria-label': ariaLabel,
  'aria-labelledby': ariaLabelledBy,
  className,
  style,
  ...rest
}: SliderProps) {
  const railRef = useRef<HTMLDivElement>(null)
  const thumbRef = useRef<HTMLDivElement>(null)
  const draggingRef = useRef(false)
  const latestRef = useRef(value)
  const [dragging, setDragging] = useState(false)

  const range = max - min
  // A zero-length range cannot be moved, so it is inert whatever the caller
  // said. It still renders, still takes focus and still reports a value — that
  // is the whole point of not having an `indeterminate` state.
  const inert = disabled || range <= 0

  const current = clamp(value, min, max)
  const fraction = range > 0 ? (current - min) / range : 0
  const secondaryFraction =
    range > 0 && secondaryValue != null ? (clamp(secondaryValue, min, max) - min) / range : 0

  function snap(raw: number): number {
    const clamped = clamp(raw, min, max)
    const stepped = min + Math.round((clamped - min) / step) * step
    // Decimal-aware, or a 0.05 volume step reports
    // `aria-valuenow="0.30000000000000004"` — a bad announcement and an
    // impossible assertion.
    const decimals = (String(step).split('.')[1] ?? '').length
    return Number(clamp(stepped, min, max).toFixed(decimals))
  }

  function valueFromPointer(event: ReactPointerEvent<HTMLDivElement>): number {
    const rail = railRef.current
    if (rail == null) return current

    const rect = rail.getBoundingClientRect()
    const along = orientation === 'horizontal' ? rect.width : rect.height
    if (along === 0) return current

    let t =
      orientation === 'horizontal'
        ? (event.clientX - rect.left) / rect.width
        : 1 - (event.clientY - rect.top) / rect.height

    if (orientation === 'horizontal' && isRtl(rail)) t = 1 - t

    return snap(min + t * range)
  }

  function release() {
    if (!draggingRef.current) return
    draggingRef.current = false
    setDragging(false)
    onValueCommit?.(latestRef.current)
  }

  const rootStyle = {
    '--slider-fraction': `${fraction}`,
    '--slider-secondary-fraction': `${secondaryFraction}`,
    ...style,
  } as CSSProperties

  return (
    <div
      className={className ? `${styles.root} ${className}` : styles.root}
      data-orientation={orientation}
      data-size={sliderSize}
      data-tone={tone}
      data-dragging={dragging || undefined}
      data-disabled={inert || undefined}
      style={rootStyle}
      onPointerDown={(event) => {
        if (inert || event.button !== 0) return

        // Suppresses the compatibility mouse events this would otherwise
        // generate. Their default action sets focus by hit-testing, which lands
        // on the rail — a decoration with no tabindex — and so undoes the
        // `thumb.focus()` below. It also stops a drag selecting text either side
        // of the control.
        event.preventDefault()

        const next = valueFromPointer(event)
        draggingRef.current = true
        latestRef.current = next
        setDragging(true)
        onValueChange(next)
        // The role is on the thumb, so a click on the rail has to move focus
        // there itself — otherwise the control is operable by mouse and
        // invisible to the keyboard that follows it.
        thumbRef.current?.focus()

        try {
          event.currentTarget.setPointerCapture(event.pointerId)
        } catch {
          // A synthetic PointerEvent carries a pointerId the browser never
          // issued, and `setPointerCapture` throws NotFoundError for it.
          // Dragging still works through the element's own pointermove; only
          // tracking beyond its bounds is lost, which no test depends on.
        }
      }}
      onPointerMove={(event) => {
        if (!draggingRef.current) return
        const next = valueFromPointer(event)
        latestRef.current = next
        onValueChange(next)
      }}
      onPointerUp={release}
      onPointerCancel={release}
      onLostPointerCapture={release}
      {...rest}
    >
      <div className={styles.rail} ref={railRef} aria-hidden="true">
        {secondaryValue != null ? <div className={styles.buffer} /> : null}
        <div className={styles.fill} />
      </div>
      <div
        className={styles.thumb}
        ref={thumbRef}
        role="slider"
        tabIndex={0}
        aria-valuemin={min}
        aria-valuemax={max}
        aria-valuenow={current}
        {...(formatValue != null ? { 'aria-valuetext': formatValue(current, max) } : {})}
        // Omitted for horizontal, which is the implicit default for `slider`.
        {...(orientation === 'vertical' ? { 'aria-orientation': 'vertical' as const } : {})}
        {...(ariaLabel != null ? { 'aria-label': ariaLabel } : {})}
        {...(ariaLabelledBy != null ? { 'aria-labelledby': ariaLabelledBy } : {})}
        aria-disabled={inert || undefined}
        onKeyDown={(event) => {
          if (inert) return

          // Read at event time rather than taken as a prop: the direction can
          // come from any ancestor, and the component has no business being told
          // something the browser already knows.
          const mirror = orientation === 'horizontal' && isRtl(event.currentTarget) ? -1 : 1
          const large = largeStep ?? step * 10
          let next: number

          switch (event.key) {
            // Only the horizontal arrows mirror. Up and Down are physical in
            // every writing mode, and PageUp/PageDown and Home/End are not
            // directions at all — they mean more, less, least and most.
            case 'ArrowRight':
              next = current + step * mirror
              break
            case 'ArrowLeft':
              next = current - step * mirror
              break
            case 'ArrowUp':
              next = current + step
              break
            case 'ArrowDown':
              next = current - step
              break
            case 'PageUp':
              next = current + large
              break
            case 'PageDown':
              next = current - large
              break
            case 'Home':
              next = min
              break
            case 'End':
              next = max
              break
            default:
              return
          }

          // Every one of these scrolls the page otherwise.
          event.preventDefault()
          const snapped = snap(next)
          onValueChange(snapped)
          onValueCommit?.(snapped)
        }}
      />
    </div>
  )
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value))
}

function isRtl(element: Element): boolean {
  return globalThis.getComputedStyle(element).direction === 'rtl'
}
