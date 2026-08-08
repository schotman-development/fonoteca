/**
 * The semantic layer. Components consume ONLY these names.
 *
 * `light` and `dark` are both typed as `SemanticTokens`, so the compiler
 * guarantees the two themes define exactly the same keys. A token that exists
 * in one theme and not the other is the classic way a dark mode ends up with an
 * invisible element, and it is a type error here rather than a bug report.
 *
 * EVERY foreground/background pair below meets WCAG AA (4.5:1), and the ratios
 * are noted inline. That is enforced, not aspirational: the Storybook suite
 * runs axe against a real browser on every story and fails the build otherwise.
 * The first version of this file was written by eye and produced ten contrast
 * violations, so these values are computed rather than chosen.
 */

import { absolute, amber, blue, gray, green, red } from './primitives.ts'

/**
 * Declared as a type alias rather than an interface on purpose: only type
 * aliases get an implicit index signature, and without one these are not
 * assignable to `TokenTree` — so `flatten()` and `toVarRefs()` could not
 * consume them.
 */
export type SemanticTokens = {
  readonly color: {
    /** Backgrounds, ordered by elevation. */
    readonly surface: {
      readonly base: string
      readonly raised: string
      readonly sunken: string
      readonly inset: string
      readonly overlay: string
    }
    readonly border: {
      readonly subtle: string
      readonly default: string
      readonly strong: string
      readonly focus: string
    }
    readonly text: {
      readonly primary: string
      readonly secondary: string
      readonly tertiary: string
      /**
       * Intentionally below 4.5:1. WCAG 1.4.3 exempts text that forms part of
       * an inactive control, and looking inactive is the whole job — a
       * "disabled" grey that meets AA is indistinguishable from body text.
       * Only ever apply this to genuinely disabled UI.
       */
      readonly disabled: string
      readonly inverse: string
      readonly link: string
    }
    /** The primary action colour. */
    readonly accent: {
      readonly subtle: string
      readonly subtleHover: string
      readonly solid: string
      readonly solidHover: string
      readonly solidActive: string
      readonly border: string
      readonly text: string
      /** Foreground for text sitting ON `solid`. */
      readonly onSolid: string
    }
    readonly success: StatusColors
    readonly warning: StatusColors
    readonly danger: StatusColors
    readonly info: StatusColors
    /** Row and control states. Kept separate from `accent` so selection can be neutral. */
    readonly interactive: {
      readonly hover: string
      readonly active: string
      readonly selected: string
      readonly selectedHover: string
      readonly disabledSurface: string
    }
  }
  readonly shadow: {
    readonly sm: string
    readonly md: string
    readonly lg: string
  }
}

export type StatusColors = {
  /** Tinted background for a low-emphasis chip. */
  readonly subtle: string
  /** Filled background for a high-emphasis chip. */
  readonly solid: string
  readonly border: string
  /** Foreground on `subtle`, or on a plain surface. */
  readonly text: string
  /**
   * Foreground on `solid` — per status, rather than one shared "on accent".
   *
   * This exists because amber and green cannot carry white text. No shade of
   * amber light enough to still read as amber reaches 4.5:1 against white, and
   * green only manages it at green[950], which reads as near-black. A single
   * shared foreground would force either unreadable chips or a palette bent out
   * of shape to accommodate one constraint. So warning and success use dark
   * text on a bright fill; accent, danger and info use white on a deep one.
   */
  readonly onSolid: string
}

export const light: SemanticTokens = {
  color: {
    surface: {
      base: absolute.white,
      raised: absolute.white,
      sunken: gray[50],
      inset: gray[100],
      overlay: 'rgba(18, 20, 22, 0.45)',
    },
    border: {
      subtle: gray[200],
      default: gray[300],
      strong: gray[400],
      focus: blue[800],
    },
    text: {
      primary: gray[900], // 16.1:1 on white
      secondary: gray[800], // 11.5:1
      tertiary: gray[700], // 8.2:1
      disabled: gray[500], // 2.1:1 — exempt; see the note on the type
      inverse: absolute.white,
      link: blue[800], // 5.0:1
    },
    accent: {
      subtle: blue[50],
      subtleHover: blue[100],
      solid: blue[800], // 5.0:1 with onSolid
      solidHover: blue[900],
      solidActive: blue[950],
      border: blue[300],
      text: blue[800], // 5.0:1 on white, 4.5:1 on subtle
      onSolid: absolute.white,
    },
    success: {
      subtle: green[50],
      solid: green[500], // 7.7:1 with onSolid
      border: green[300],
      text: green[950], // 8.4:1 on white, 7.8:1 on subtle
      onSolid: gray[900],
    },
    warning: {
      subtle: amber[50],
      solid: amber[500], // 9.6:1 with onSolid
      border: amber[300],
      text: amber[950], // 6.8:1 on white, 6.4:1 on subtle
      onSolid: gray[900],
    },
    danger: {
      subtle: red[50],
      solid: red[900], // 5.5:1 with onSolid
      border: red[300],
      text: red[900], // 5.5:1 on white, 5.1:1 on subtle
      onSolid: absolute.white,
    },
    info: {
      subtle: blue[50],
      solid: blue[800],
      border: blue[300],
      text: blue[800],
      onSolid: absolute.white,
    },
    interactive: {
      hover: gray[100],
      active: gray[200],
      selected: blue[50],
      selectedHover: blue[100],
      disabledSurface: gray[100],
    },
  },
  shadow: {
    sm: '0 1px 2px rgba(18, 20, 22, 0.08)',
    md: '0 2px 8px rgba(18, 20, 22, 0.10), 0 1px 2px rgba(18, 20, 22, 0.06)',
    lg: '0 8px 24px rgba(18, 20, 22, 0.14), 0 2px 6px rgba(18, 20, 22, 0.08)',
  },
}

/**
 * Dark is not an inversion of light. Three deliberate differences:
 *
 * 1. Surfaces get LIGHTER as they rise (`raised` above `base`), because a
 *    shadow cast onto a near-black background is invisible.
 * 2. Shadows are weaker and borders do more of the work of separating layers,
 *    for the same reason.
 * 3. Text tones move to the LIGHT end of each hue — contrast now comes from
 *    lightness rather than depth.
 *
 * Solid fills keep their light-theme values, because their contrast is measured
 * against their own foreground rather than the page, and that relationship does
 * not change with the theme.
 */
export const dark: SemanticTokens = {
  color: {
    surface: {
      base: gray[950],
      raised: gray[900],
      sunken: gray[1000],
      inset: gray[1000],
      overlay: 'rgba(0, 0, 0, 0.65)',
    },
    border: {
      subtle: '#2a2f34',
      default: '#3a4046',
      strong: gray[700],
      focus: blue[400],
    },
    text: {
      primary: gray[100], // 14.9:1 on surface.raised
      secondary: gray[400], // 10.3:1
      tertiary: gray[500], // 7.4:1
      disabled: gray[600], // exempt; see the note on the type
      inverse: gray[950],
      link: blue[300], // 7.9:1
    },
    accent: {
      subtle: 'rgba(34, 139, 230, 0.16)',
      subtleHover: 'rgba(34, 139, 230, 0.26)',
      solid: blue[800],
      solidHover: blue[900],
      solidActive: blue[950],
      border: 'rgba(34, 139, 230, 0.45)',
      text: blue[300], // 7.9:1 on surface.raised
      onSolid: absolute.white,
    },
    success: {
      subtle: 'rgba(64, 192, 87, 0.16)',
      solid: green[500], // 9.2:1 with onSolid
      border: 'rgba(64, 192, 87, 0.40)',
      text: green[300], // 10.5:1
      onSolid: gray[950],
    },
    warning: {
      subtle: 'rgba(250, 176, 5, 0.16)',
      solid: amber[500], // 11.5:1 with onSolid
      border: 'rgba(250, 176, 5, 0.40)',
      text: amber[300], // 11.8:1
      onSolid: gray[950],
    },
    danger: {
      subtle: 'rgba(250, 82, 82, 0.16)',
      solid: red[900], // 5.5:1 with onSolid
      border: 'rgba(250, 82, 82, 0.40)',
      text: red[300], // 8.4:1
      onSolid: absolute.white,
    },
    info: {
      subtle: 'rgba(34, 139, 230, 0.16)',
      solid: blue[800],
      border: 'rgba(34, 139, 230, 0.40)',
      text: blue[300],
      onSolid: absolute.white,
    },
    interactive: {
      hover: 'rgba(255, 255, 255, 0.05)',
      active: 'rgba(255, 255, 255, 0.09)',
      selected: 'rgba(34, 139, 230, 0.18)',
      selectedHover: 'rgba(34, 139, 230, 0.26)',
      disabledSurface: 'rgba(255, 255, 255, 0.04)',
    },
  },
  shadow: {
    sm: '0 1px 2px rgba(0, 0, 0, 0.40)',
    md: '0 2px 8px rgba(0, 0, 0, 0.50), 0 1px 2px rgba(0, 0, 0, 0.35)',
    lg: '0 8px 24px rgba(0, 0, 0, 0.60), 0 2px 6px rgba(0, 0, 0, 0.40)',
  },
}

export const themes = { light, dark } as const
export type ThemeName = keyof typeof themes
