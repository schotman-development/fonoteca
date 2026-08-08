/**
 * The one string every formatter falls back to.
 *
 * Domain rule: **a formatter never throws and never guesses.** `null` is a
 * first-class answer all over this API — `complete`, `tracks_on_disk`,
 * `upgrade_format_id`, `on_disk_ratio` — and the em dash is how "not known" is
 * rendered, distinct from `0`, from `false` and from an empty string.
 */
export const EM_DASH = '—'
