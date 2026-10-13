/**
 * The playlist screens' rules, pure so they run under `node --test`.
 *
 * The server decides what an entry is and which copy plays it; what is left
 * here is the order a person is editing and how a playlist describes itself.
 */

/**
 * The order with the entry at `index` moved `offset` places, or `null` where
 * that would take it off either end — the button for it is disabled then, and
 * a null is what tells it so.
 */
export function moved<T>(order: readonly T[], index: number, offset: number): T[] | null {
  const to = index + offset
  if (index < 0 || index >= order.length || to < 0 || to >= order.length) return null

  const next = [...order]
  const [entry] = next.splice(index, 1)
  next.splice(to, 0, entry as T)
  return next
}

type AlbumRef = { readonly albumId: string }

/**
 * Up to four different albums from the top of the list, for its cover. A
 * playlist that is one album twice over gets that album once.
 */
export function coverAlbums<A extends AlbumRef>(albums: readonly (A | null)[], count = 4): A[] {
  const seen = new Set<string>()
  const picked: A[] = []

  for (const album of albums) {
    if (album == null || seen.has(album.albumId)) continue
    seen.add(album.albumId)
    picked.push(album)
    if (picked.length === count) break
  }

  return picked
}

/** "12 songs · 48:12", and how many are not held where any are not. */
export function songsLine(songs: number, held: number, duration: string | null): string {
  const parts = [`${songs.toLocaleString()} ${songs === 1 ? 'song' : 'songs'}`]
  if (duration != null) parts.push(duration)
  if (held < songs) parts.push(`${(songs - held).toLocaleString()} not in the library`)
  return parts.join(' · ')
}
