# `web/src/design` — the primitive layer

Thirty-odd components over `web/src/styles/tokens.css`. Everything a screen
draws that is not a release, an artist or a queue item is here, and everything
here is drawn out of tokens.

**Screens import from `@/design`. Never from a file inside it.** One barrel,
one grep: `grep -rn "@/design/" web/src/screens web/src/widgets` should return
nothing with a second slash in it. `index.ts` is the whole public surface and
`index.test.ts` fails when a primitive is added to this directory and left out
of it.

## A primitive knows nothing about the domain

No component in here may contain the word album, artist, release, queue, track
or Qobuz — in a prop name, a type, a default string or a rendered word. If it
needs one, it belongs in `@/widgets`, which is the layer that knows what a
release is. The test is whether the component would mean anything in a
different application: `Meter`, `Chip` and `Drawer` would; `ReleaseRow` would
not.

Design-line references in comments are the exception and are wanted — a value
that cannot be traced back to the design is a value somebody invented.

## Layout

One directory per primitive, four files, no variation:

```
Button/Button.tsx   Button/Button.module.css   Button/Button.test.tsx   Button/index.ts
```

Intra-layer imports use the `@/design/…` alias, never `./`. A primitive may
render another primitive — `SwitchField` renders `Toggle`, `Shelf` and `Drawer`
render `Button`, `Button` renders `Spinner`, `ErrorBoundary` renders
`PageError`. That is deliberate. The alternative, which this layer briefly had,
is one control in the design drawn twice in two modules, free to drift apart at
the next retune.

## The five shared recipes

`shared/*.module.css` holds the declarations that repeat across primitives.
Each is defined **once** and composed — by `composes:` where the composer has a
module of its own, or by importing the recipe module and joining class names
with `cx` where it does not. A primitive that restates one has forked it.

| Recipe | What it is | Composed by |
| --- | --- | --- |
| `label.module.css` | `.label`, the eyebrow (11px, uppercase, `--ls-label`, `--c-ink-mark`); `.labelNav`, the same at `--ls-nav` under a figure | Eyebrow, Field, Figure, Drawer |
| `chip.module.css` | `.chip` / `.chipSm`, the two pill geometries; `.dot`, the categorical marker | Chip, SourceChip |
| `switch.module.css` | `.track` / `.trackOn` / `.knob` / `.knobOn` — 34×19 with the knob's on-position **derived**, not written as 17px | Toggle, and so SwitchField |
| `surface.module.css` | `.surface` / `.hoverEdge` / `.surfaceInset` — the hairline card edge over paper or canvas | StatCard, PageError |
| `meta.module.css` | `.meta`, the mono micro-label; `.metaMuted`, a count that follows its container's colour | Chip, SourceChip |

The rule for adding to `shared/` runs both ways: **two composers or it stays
put.** There is no `shared/codeblock.module.css` because `CodeBlock` is its only
user, and a shared recipe with one composer is indirection without a payoff.

Colour is deliberately absent from `chip.module.css`. Folding a chip's on-state
into the geometry is how a chip ends up able to carry a status colour, which
this system forbids — see below.

## Token rules

Read `web/src/styles/tokens.css` before writing any CSS. Every value in it came
from the design's own inline styles.

- **No raw hex, ever.** `grep -rnE '#[0-9a-fA-F]{3,8}' --include=*.module.css`
  returns hits only inside comments quoting the design.
- **No raw px font size.** The design's fourteen sizes snap onto the nine-step
  `--fs-*` ladder.
- **No gap off the space scale.** One 4px scale for the whole app. Where the
  design's gap falls between steps, write the expression (`calc(var(--sp-4) +
  var(--sp-1))` for 20px), not the literal.
- **A control's own geometry is a named custom property on its own class**,
  with the design line in a comment: `--switch-w: 34px`, `--pip-w: 26px`,
  `--spinner-d: 10px`. These are one control's shape, not a shared measurement,
  and snapping them onto `--sp-*` would delete distinctions the design makes.
  A value is promoted to `tokens.css` only when a **second** module reads it —
  which is why `--pad-chip-*`, `--gap-chip` and `--e-knob` are tokens and
  `--btn-icon-size` is not.
- **One focus treatment**, the `:focus-visible` ring at the foot of
  `tokens.css`. Nothing here declares another. `Drawer` suppresses `:focus`
  only for the programmatic focus it puts on the panel, which no keyboard user
  ever reaches by tabbing.
- **Keyframes live in `base.css`** (`fadeIn`, `spin`). A keyframe *name*
  declared inside a CSS module is mangled by the bundler; one merely referenced
  is left alone.

### The two approved contrast deviations

Both are from BUILD_SPEC §3 and both are deliberate. Reviewers: these are
correct.

1. `#7D786D` is 4.4:1 on white, below the 4.5:1 floor. It is `--c-ink-faint`
   and is **decoration only**. Text the design paints `#7D786D` is painted
   `--c-ink-mark` (`#6E6A61`, 5.4:1). The two are one shade apart; the audit
   result is not.
2. The design's amber `#D97706` is 3.2:1. It is `--c-warn-solid`, for bars,
   dots and other non-text marks. Amber **text** is `--c-warn` (`#B45309`,
   5.0:1). The design already uses both — this is choosing the right one per
   context. `Meter` is where it bites: a fill sits on `--c-fill`, not on paper,
   and the module records the measured ratios against that track.

## Three domain rules the components hold

These are front-end halves of rules in the repository's `CLAUDE.md`. Each is a
bug somebody has already shipped.

1. **A `Toggle` sends no value.** `ToggleProps.onToggle` is `() => void` and
   must not be widened. `POST /api/albums/{id}/monitor` **toggles on an empty
   body**; the old UI carried the calling screen's filters on the mutating URL,
   one of those filters is named `monitored`, and reading it as the album's new
   value made an "ignored only" filter unmonitor whatever row was pressed. A
   handler that is never handed a value cannot pass one on. `Toggle`'s click
   handler is wrapped (`() => { onToggle() }`) for the same reason — passing
   `onToggle` by reference hands React's `MouseEvent` through as the first
   argument.
2. **A `Meter`'s `null` is not a zero.** `null` means nothing has measured this
   yet, so the track is drawn empty, `aria-valuenow` is **omitted** rather than
   zeroed, and no number is rendered. A `0` reads as "none of it is done",
   which is a different and usually false claim. `indeterminate` is the third
   state, for a job that is running and publishes no percentage.
3. **A `Chip` is never tinted with a status colour.** There is no `tone` prop
   and there must not be one. In this system colour means *how bad something
   is*; a chip means *which subset you are looking at*, and a red "Errors"
   filter chip is indistinguishable from a count of errors. The one colour a
   chip has is ink-on-ink when it is on, plus the accent the design gives the
   path-template token buttons — and the accent is identity (a link, a proposed
   value), not a verdict.

Two smaller rules ride with them. `Placeholder` is the only primitive that is
not in the design: it marks a datum the API does not have, carries
`data-placeholder` so a test can count them, and shows `EM_DASH` from
`@/format` where a figure would go — never an invented number. And every
icon-only control carries a name: `Button`'s `label` prop renders the app's
`.visuallyHidden` span and marks the glyph `aria-hidden`, because `×` is
announced as "multiplication sign" or as nothing at all.

## Tests

Every component has a colocated `.test.tsx` asserting the behaviour that
matters rather than the paint — a toggle emits no value, a disabled button does
not fire, an unknown value renders the em dash, a static chip is not a button.
`shared/shared.test.ts` pins the recipes and `index.test.ts` pins the barrel.

```bash
export PATH=~/.local/opt/node/bin:$PATH
npm run typecheck --prefix web
npm test --prefix web
npm run lint --prefix web
```
