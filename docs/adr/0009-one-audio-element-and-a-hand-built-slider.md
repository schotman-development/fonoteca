# 0009 — One audio element, and a hand-built slider

**Status:** accepted, 2026-08-14

## Context

The identification and matching screens need playback. On a library that was
deliberately tag-stripped — sampling it with ffprobe found `ACOUSTID_ID` on every
file and nothing else at all — the audio is one of only two claims in existence
about what a file is, and the other one is a directory name nobody should
believe. A person resolving a refused attribution has to be able to *hear* the
track.

ADR 0003 says `packages/ui` depends on `react` and `react-dom` only, and that the
components carrying behaviour a primitives library would otherwise supply each
get an ADR naming the WAI-ARIA pattern they implement. A slider is squarely in
that class: a role, value semantics, a full key map with right-to-left mirroring,
pointer capture and step quantisation, every part of which fails silently and
looks like a design decision when it does.

The audio element and the slider are one decision, not two, because the
interesting parts are coupled: commit-on-release exists *because* of `timeupdate`.

## Decision

**One `HTMLAudioElement`, constructed by `PlaybackProvider` with `new Audio()`
and never attached to the document.** One element is what makes "two rows cannot
play at once" a fact rather than a convention, and what lets the transport always
show what is audible. Keeping it detached is the other half: an `<audio>` node in
the tree is a permanent axe surface — `audio-caption`, `no-autoplay-audio` — that
every story in the package would have to argue with, for an element that renders
nothing.

**Two contexts, one provider.** `PlaybackContext` carries the track, the status
and the controls, and changes when a person acts. `PlaybackProgressContext`
carries `currentTime`, `duration` and `buffered`, and changes about four times a
second. Merged, a table of five hundred play buttons would re-render four times a
second in order to move one progress bar.

**Seek and volume are one hand-built `Slider` implementing the WAI-ARIA Slider
pattern** — `role="slider"` on the thumb (per the APG examples, so the focus ring
points at the value rather than surrounding the bar), `aria-valuemin`/`max`/`now`/
`valuetext`, arrows by `step`, PageUp/PageDown by a larger step, Home and End to
the ends. Only the horizontal arrows mirror in right-to-left; Home, End, PageUp
and PageDown are not directions, they are least, most, more and less. Direction is
read from computed style at event time rather than taken as a prop, because it can
come from any ancestor.

Pointer capture rather than a document-level listener, so a drag that leaves the
element still tracks and unmounting mid-drag cannot leak a handler.

`<input type="range">` was rejected. It gives the keyboard map and the
accessibility tree for free, but its thumb is reachable only through
`::-webkit-slider-thumb` and `::-moz-range-thumb`, which cannot share a
declaration and cannot express `:focus-visible`; it has nowhere to put a buffered
band behind the fill; and engines disagree about when `change` fires relative to
`input`, which is precisely the distinction a scrub depends on.

**The slider reports `onValueChange` continuously and `onValueCommit` once per
interaction** — on pointer release, and on each key press. The transport seeks
only on commit and holds the dragged value until the element's `seeked` event, so
a scrub is never fought by the `timeupdate` it caused, and a drag across a 30 MB
file over the network issues one range request rather than one per pixel.

**An unknown length is not a slider state.** ARIA 1.2 makes `aria-valuenow`
required on `slider` and axe enforces it, so there is no `indeterminate` prop: an
unknown duration is a disabled zero-length range reporting
`aria-valuetext="Length unknown"`, rather than a control that swaps roles under a
focused user the moment metadata arrives.

**`PlayButton` changes its accessible name and carries no `aria-pressed`.** The
state changes without the user — a track ends, a file 404s, autoplay is refused —
and a toggle that un-presses itself is describing something other than a toggle.
There are also four states, not two, and a boolean cannot carry *loading* or
*error*. The cost is that a screen reader does not reliably announce that the
focused element's name changed, so the change is announced once, globally, by
`AudioTransport`'s polite live region rather than five hundred times by five
hundred buttons. That region carries state and never `currentTime`: a live region
driven by `timeupdate` speaks over everything else on the page four times a
second.

## Consequences

Three sharp edges are already paid for and must stay paid:

- **Cleanup uses `removeAttribute('src')`, never `src = ''`.** The empty string
  resolves against the document URL, and the browser fetches the page itself as
  audio.
- **`AbortError` from `play()` is ignored.** It fires every time a new `load()`
  interrupts a `play()` still in flight, which is what a fast click from one row
  to the next does. Treating it as a failure puts an error on every impatient
  double-click. `stalled` is likewise not an error — a slow LAN read of a 30 MB
  FLAC fires it routinely.
- **A 404 and an undecodable codec both arrive as `MediaError` code 4,** so the
  message for it claims neither. `PlaybackTrack.mimeType` plus `canPlayType` is
  the only path to a definite answer, and it is taken before the request rather
  than after.

`blocked` is a first-class error kind, because Chromium's autoplay policy is
`document-user-activation-required` and refusing to start is not a failure of the
file. **No test may assert `audio.paused`:** Playwright's Chromium is headless
with `--mute-audio`, whether `play()` resolves there is not a property of these
components, and the stories therefore assert *coherence either way* — playing
implies the button offers to pause; blocked implies a message is shown and the
button still offers to play.

Volume is deliberately not persisted, breaking from `ThemeProvider`'s
localStorage precedent: this package owns the theme because it owns the tokens the
theme selects, and it does not own a person's audio preferences. A persisted
volume would also leak between stories in one Storybook run and make
`aria-valuetext="70%"` an order-dependent assertion.

`AudioTransport` sets no height of its own — it fills `AppShell`'s reserved slot,
sized by `--density-transport-height`, and renders `null` when nothing is playing
so that the slot collapses. `--density-transport-height` and
`AppShell.module.css` are the pair; move one and move the other.

Not covered by the test run: `forced-colors: active`. Storybook has no parameter
for it and a play function cannot reach Playwright's `emulateMedia`, so the
`@media (forced-colors: active)` blocks in `Slider.module.css` and
`PlayButton.module.css` are hand-verified. `ThemeSwitch.module.css` is in the same
position.
