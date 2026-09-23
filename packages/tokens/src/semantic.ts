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

import { absolute, amber, denim, green, red, sand } from './primitives.ts'

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
    /*
     * The page is not white; cards are. A white card on an off-white page reads
     * as lifted without needing a border or a shadow to say so, which is what
     * lets both be quieter everywhere else.
     */
    surface: {
      base: sand[50],
      raised: absolute.white,
      sunken: sand[100],
      inset: sand[200],
      overlay: 'rgba(43, 40, 35, 0.40)',
    },
    /* Lighter than the neutrals beside them, and used less: space separates
       most things here, and a rule is kept for where space cannot. */
    border: {
      subtle: sand[200],
      default: sand[300],
      strong: sand[400],
      focus: denim[700],
    },
    /*
     * Three steps that are visibly three steps. A secondary barely
     * distinguishable from primary makes every label on a screen compete with
     * the value beside it.
     */
    text: {
      primary: sand[900], // 14.1:1 on base
      secondary: sand[800], // 7.3:1
      tertiary: sand[700], // 5.5:1 on base, 4.8:1 on inset
      disabled: sand[500], // exempt; see the note on the type
      inverse: sand[50],
      link: denim[700], // 6.0:1
    },
    accent: {
      subtle: denim[50],
      subtleHover: denim[100],
      solid: denim[700], // 6.2:1 with onSolid
      solidHover: denim[800],
      solidActive: denim[900],
      border: denim[200],
      text: denim[700], // 6.0:1 on base, 5.5:1 on subtle
      onSolid: absolute.white,
    },
    success: {
      subtle: '#eef6ee',
      solid: green[500], // 7.3:1 with onSolid
      border: green[200],
      text: green[950], // 8.4:1 on white, 7.6:1 on subtle
      onSolid: sand[900],
    },
    warning: {
      subtle: '#fbf4e2',
      solid: amber[500], // 9.1:1 with onSolid
      border: amber[200],
      text: '#80470a', // 7.4:1 on white, 6.8:1 on subtle
      onSolid: sand[900],
    },
    danger: {
      subtle: '#fbefed',
      solid: '#a8322d', // 6.7:1 with onSolid
      border: red[200],
      text: '#a8322d', // 6.7:1 on white, 5.9:1 on subtle
      onSolid: absolute.white,
    },
    info: {
      subtle: denim[50],
      solid: denim[700],
      border: denim[200],
      text: denim[700],
      onSolid: absolute.white,
    },
    interactive: {
      hover: '#f3f1ec',
      active: sand[200],
      selected: denim[50],
      selectedHover: denim[100],
      disabledSurface: sand[100],
    },
  },
  /* Warm-tinted and wide rather than dark and tight: a lift, not a cut-out. */
  shadow: {
    sm: '0 1px 3px rgba(43, 40, 35, 0.06)',
    md: '0 4px 16px rgba(43, 40, 35, 0.07), 0 1px 3px rgba(43, 40, 35, 0.05)',
    lg: '0 12px 40px rgba(43, 40, 35, 0.12), 0 2px 8px rgba(43, 40, 35, 0.06)',
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
 * not change with the theme. The page is warm here too: a blue-black at night
 * is as cold as blue-white by day.
 */
export const dark: SemanticTokens = {
  color: {
    surface: {
      base: sand[1000],
      raised: sand[950],
      sunken: sand[1100],
      inset: sand[1100],
      overlay: 'rgba(0, 0, 0, 0.60)',
    },
    border: {
      subtle: '#2e2b27',
      default: '#3d3934',
      strong: sand[700],
      focus: denim[400],
    },
    text: {
      primary: sand[200], // 13.6:1 on surface.raised
      secondary: sand[500], // 8.0:1
      tertiary: sand[600], // 6.3:1, 4.6:1 on interactive.selectedHover
      disabled: sand[700], // exempt; see the note on the type
      inverse: sand[1000],
      link: denim[300], // 7.8:1
    },
    accent: {
      subtle: 'rgba(95, 140, 200, 0.16)',
      subtleHover: 'rgba(95, 140, 200, 0.26)',
      solid: denim[700],
      solidHover: denim[800],
      solidActive: denim[900],
      border: 'rgba(95, 140, 200, 0.45)',
      text: denim[300], // 7.8:1 on surface.raised
      onSolid: absolute.white,
    },
    success: {
      subtle: 'rgba(64, 192, 87, 0.14)',
      solid: green[500],
      border: 'rgba(64, 192, 87, 0.36)',
      text: green[300],
      onSolid: sand[1000],
    },
    warning: {
      subtle: 'rgba(250, 176, 5, 0.14)',
      solid: amber[500],
      border: 'rgba(250, 176, 5, 0.36)',
      text: amber[300],
      onSolid: sand[1000],
    },
    danger: {
      subtle: 'rgba(250, 82, 82, 0.14)',
      solid: '#a8322d',
      border: 'rgba(250, 82, 82, 0.36)',
      text: red[300],
      onSolid: absolute.white,
    },
    info: {
      subtle: 'rgba(95, 140, 200, 0.16)',
      solid: denim[700],
      border: 'rgba(95, 140, 200, 0.40)',
      text: denim[300],
      onSolid: absolute.white,
    },
    interactive: {
      hover: 'rgba(255, 255, 255, 0.05)',
      active: 'rgba(255, 255, 255, 0.09)',
      selected: 'rgba(95, 140, 200, 0.18)',
      selectedHover: 'rgba(95, 140, 200, 0.22)',
      disabledSurface: 'rgba(255, 255, 255, 0.04)',
    },
  },
  shadow: {
    sm: '0 1px 3px rgba(0, 0, 0, 0.35)',
    md: '0 4px 16px rgba(0, 0, 0, 0.40), 0 1px 3px rgba(0, 0, 0, 0.30)',
    lg: '0 12px 40px rgba(0, 0, 0, 0.55), 0 2px 8px rgba(0, 0, 0, 0.35)',
  },
}

export const themes = { light, dark } as const
export type ThemeName = keyof typeof themes
