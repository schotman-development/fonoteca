# 0003 — A design system with no third-party runtime dependencies

**Status:** accepted, 2026-08-08

## Context

Fonoteca needs a component library. The usual answers are a headless primitives
library (Radix, Base UI) styled by us, or a batteries-included kit (MUI,
Mantine).

Both were rejected. The design system is built from scratch on design tokens,
and `packages/ui` depends on **`react` and `react-dom` only** — no positioning
library, no focus-trap helper, no virtualizer.

## Decision

**CSS Modules + CSS custom properties**, with tokens as the single source of
truth.

- `packages/tokens` is TypeScript, emitting both a stylesheet of custom
  properties and typed `var()` references. Light and dark are typed identically,
  so the compiler guarantees they define the same names — a token present in one
  theme and missing from the other is how an element becomes invisible in dark
  mode, and it is a type error here.
- Components carry a colocated `.module.css` and select variants with
  `data-*` attributes rather than concatenated class names: one source of truth
  per variant, visible in the DOM when debugging, no class-joining helper.
- Zero runtime and zero bundler plugins. Nothing to rip out later.

Scales are tuned for a **dense data application** — the primary screen is a
100k-row table. A 2px spacing base, a type scale whose steps are 1px apart below
16px, and explicit row-height tokens, because a virtualizer must know its row
height as a number before it renders anything.

## Consequences

**The accessibility work is ours.** With no primitives underneath, the hard
components each carry behaviour a library would otherwise supply:

| Component | What must be hand-built |
| --- | --- |
| Dialog | focus trap, focus restoration, scroll lock, `aria-modal`, dismiss layer |
| Popover / Tooltip | collision-aware positioning, flip/shift, scroll tracking, portalling |
| Menu | roving tabindex, typeahead, submenu timing |
| Combobox | `aria-activedescendant`, listbox semantics, filtering |
| Data table | row virtualization, header semantics, keyboard navigation |

Each of these gets its own ADR naming the WAI-ARIA pattern it implements, and is
built when a real screen needs it rather than speculatively.

### The enforcement, and what it caught immediately

`pnpm --filter @fonoteca/ui test` runs **every story through axe in a real
Chromium**, via `@storybook/addon-vitest` and Vitest browser mode. The a11y
addon is configured to `error`, so a violation fails the build.

This is not theoretical. The first run against a palette that had been written
by eye produced **ten contrast failures**, including:

- white text on `blue[600]`, `red[700]` and `green[700]` fills — 3.55:1, 3.84:1
  and 2.74:1 against a 4.5:1 requirement
- `success.text` on `success.subtle` at 4.06:1
- `text.tertiary` at 3.32:1 on white

Two things changed as a result. Every semantic pair is now **computed** rather
than chosen, with its ratio recorded inline in `semantic.ts`. And
`StatusColors` gained an **`onSolid`** token, because a single shared
"foreground on a solid fill" is not achievable: no shade of amber light enough
to read as amber reaches 4.5:1 against white, and green only manages it at
`green[950]`, which reads as near-black. Warning and success therefore use dark
text on a bright fill; accent, danger and info use white on a deep one.

The gate itself was verified by deliberately regressing one token and confirming
the suite went red — a passing suite that checks nothing is worse than no suite,
because it looks like coverage.

One documented exemption: `text.disabled` is intentionally below 4.5:1, since a
"disabled" grey that meets AA is indistinguishable from body text. WCAG 1.4.3
exempts text forming part of an inactive control. It lives in its own story with
the `color-contrast` rule disabled, so the exemption covers that one sample and
every other tone stays checked.

The largest single cost is the **virtualizer** for the catalogue table. That is
unplanned work the constraint creates, and worth revisiting when that screen is
designed.
