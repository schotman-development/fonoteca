/**
 * The integrity badge on a release — one rule, in one place.
 *
 * Every surface that draws an album tile draws this pill (design 281–283), and
 * the rule behind it is not "what colour is this release" but *what has
 * actually been measured about it*. It is a pure function rather than three
 * ternaries inside `AlbumTile` because the same verdict is wanted by the
 * release drawer's health banner and by anything that summarises a grid, and a
 * badge rule copied twice is a badge rule that disagrees with itself the first
 * time a state is added.
 *
 * ── the order is the argument ──────────────────────────────────────────────
 *
 * `corrupt_tracks` is asked first because it is the only verdict that says a
 * file will not play. A release can be corrupt *and* replaced; saying
 * "REPLACED" about it would be true and useless.
 *
 * `integrity_state === null` is **UNVERIFIED**, not clean. `null` means no file
 * under this release carries a baseline at all, so nothing has ever been
 * compared — see `AlbumOut.integrity_state`, and the three-state argument in
 * the repository's CLAUDE.md, which is entirely about not collapsing "not
 * measured" into a verdict. A tile that showed nothing there would report an
 * unmeasured library as a healthy one, which is the single wrong answer
 * available.
 *
 * ── the word the design uses and this file does not ────────────────────────
 *
 * The design's fourth state is `TAMPERED` (DCLogic 1094). Qobuzarr performs no
 * tamper check: it hashes a file (blake2b-128 plus an audio sample count) and
 * classifies the difference as `retagged` — same audio, different container —
 * or `replaced` — different audio under the same name. Neither is evidence of
 * intent, and a badge claiming otherwise about somebody's own re-rip is an
 * accusation the data cannot support. The word appears nowhere in this
 * codebase, and `albumFlag.test.ts` pins that.
 *
 * `retagged` deliberately gets **no** badge. It is the verdict that says the
 * audio is provably identical and only the metadata around it moved, which is
 * the normal consequence of the app's own re-tag pass; flagging it would put an
 * amber pill on every release the user asked to be re-tagged.
 */

import type { AlbumOut } from '@/api/types'
import type { Tone } from '@/design'

/** A badge to draw, or nothing — the resting state is no badge at all. */
export interface AlbumFlag {
  /** The word, upper-case, as the design sets it (282). */
  label: string
  /** Which ink `Badge` sets it in. Never `neutral`: a flag is an exception. */
  tone: Tone
}

/**
 * The one flag a release carries, or `null` when there is nothing to say.
 *
 * Pure and total: it reads three fields, never throws, and answers `null` for
 * a release whose files were measured and agreed with their baseline.
 */
export function albumFlag(album: AlbumOut): AlbumFlag | null {
  if (album.corrupt_tracks > 0) return { label: 'CORRUPT', tone: 'bad' }
  if (album.integrity_state === 'replaced') return { label: 'REPLACED', tone: 'warn' }
  if (album.integrity_state === null) return { label: 'UNVERIFIED', tone: 'warn' }
  return null
}
