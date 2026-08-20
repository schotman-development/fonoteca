# 0010 — The command bar: a native dialog, a hand-built combobox

**Status:** accepted, 2026-08-14

## Context

The shell grew a navigation rail and a top bar, and the top bar wanted a command
bar in the middle of it — `⌘K`, type, jump.

ADR 0003 says `packages/ui` depends on `react` and `react-dom` only, and lists
the components that therefore carry accessibility work a primitives library
would otherwise supply. This one is two entries from that table at once:

| Component | What must be hand-built |
| --- | --- |
| Dialog | focus trap, focus restoration, scroll lock, `aria-modal`, dismiss layer |
| Combobox | `aria-activedescendant`, listbox semantics, filtering |

Both, in one control, is the most accessibility surface any component in this
repository has asked for so far.

## Decision

**The overlay is a native `<dialog>` opened with `showModal()`.** Every item in
the dialog row of that table is the platform's: focus containment, focus
restoration to whatever opened it, `Escape`, the background going inert, and
top-layer stacking. Nothing in `CommandBar.module.css` mentions `z-index`,
because the top layer is above every stacking context in the document —
including a sticky rail — without joining the z-index scale at all.

Two consequences of choosing the element are worth writing down, because both
look like bugs from the React side:

- **The DOM owns "is it open".** The browser closes the dialog on `Escape`
  without telling React first. So state drives the element through an effect,
  and `onClose` drives the state back; there is no way to make the boolean the
  single source of truth.
- **Closing is done through the element, not only through the state.** A
  command may navigate, and a dialog still in the top layer during that
  navigation covers the page it just arrived at.

**The list is the WAI-ARIA combobox with list autocomplete, and focus never
leaves the input.** Options are not tabbable and are never focused; the active
one is named by `aria-activedescendant`. That is the whole mechanism — it is
what lets typing continue while the selection moves — and it is also why three
Biome a11y rules are suppressed at the two places they fire: an option that is
focusable, or that carries its own key handler, would be the wrong
implementation of this pattern rather than a safer one.

**Filtering is substring, not fuzzy, and the caller's order is preserved.** A
fuzzy matcher ranks, and a ranking that reorders rows between keystrokes moves
the row under the cursor after the person has decided which one they want.
Every whitespace-separated term must appear somewhere in the command's label,
kind or keywords.

**The commands are the caller's.** `packages/ui` knows nothing about routes or
themes; `apps/web/src/commands.ts` builds the list from `NAV_ITEMS` — the same
list the rail renders — plus the three theme settings.

**The empty listbox is `hidden`, not unmounted.** `aria-controls` pointing at an
id that is not in the document is an `aria-valid-attr-value` failure, and it
would appear only when nothing matches. Same lesson as `Disclosure`'s panel.

## Consequences

**`::backdrop` is now part of the theme surface.** It reads
`--color-surface-overlay`, which existed unused until now.

**Six stories, and one of them is the reason the others can exist.** `Open`
renders with `defaultOpen`, so axe sees the dialog, the combobox and the
listbox — the states that are otherwise unreachable without an interaction, and
therefore the states an a11y suite silently skips.

**The shortcut is claimed globally.** A document-level `keydown` listener takes
`⌘K` / `Ctrl K` and calls `preventDefault`, which is also Chrome's "search the
web" accelerator. That is deliberate and conventional, but it is a key the
application has now taken from the browser.

**What it deliberately does not do:** search the catalogue. There is no endpoint
that answers "artists and albums matching this", and a command bar that quietly
searches four page names while looking like it searches 100,000 tracks is worse
than one that plainly does not. When such an endpoint exists, the commands are a
prop — the component does not change.
