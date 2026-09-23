import { apiBaseUrl } from '../api.ts'

/**
 * Where a picture of this album comes from, on the matching screen.
 *
 * Two sources, and they are two different kinds of claim. The file's own
 * embedded cover is what the person who ripped it believed; the Cover Art
 * Archive's is what MusicBrainz holds against the release being offered. Put
 * side by side they answer at a glance the question six near-identical text
 * rows cannot — *is this the record I am holding* — which is the whole reason
 * this exists.
 *
 * **Neither is evidence a rule may read.** A cover travels with the bytes but
 * is copied off a web search about as often as it is scanned, so it is for a
 * person's eye in exactly the way the folder name is: shown, never believed.
 */
export function embeddedArt(mediaFileId: string): string {
  return `${apiBaseUrl}/api/catalogue/matching/files/${mediaFileId}/art`
}

/**
 * The cover of an album the catalogue holds, as stored there.
 *
 * **Served by the API, not the archive.** The archive is asked once per album
 * and the answer kept, so a grid of covers is a page of row reads rather than a
 * page of CDN requests — and a person can replace the archive's choice, which
 * a hot-link to `front-250` could never show. `version` changes the URL after
 * such a change, so the page that made it redraws at once.
 */
export function releaseCover(releaseId: string, version = 0): string {
  const url = `${apiBaseUrl}/api/catalogue/releases/${releaseId}/cover`
  return version === 0 ? url : `${url}?v=${version}`
}

/**
 * One Cover Art Archive image, at thumbnail size — for choosing between them.
 */
export function archiveImage(mbid: string, imageId: number): string {
  return `https://coverartarchive.org/release/${mbid}/${imageId}-250`
}

/**
 * The Cover Art Archive's front cover for a release, at thumbnail size.
 *
 * **Fetched by the browser, not proxied through the API.** It is a plain
 * `<img>` against a public CDN that is already MusicBrainz's own, so a proxy
 * would buy one thing — the request not leaving the machine — at the cost of an
 * endpoint, a cache and a rate limit to look after. `Artwork` draws its
 * monogram when the fetch 404s, which is the ordinary answer: plenty of
 * releases have no art at all.
 *
 * `front-250` rather than the full-size image: these are 88px boxes in a list
 * that can run to eight of them.
 */
export function releaseArt(mbid: string): string {
  return `https://coverartarchive.org/release/${mbid}/front-250`
}

/**
 * The sleeve of a record the library does not hold.
 *
 * Keyed on the *release group* rather than a release, which is the only key
 * available here: a discography is groups, and the whole point of a row on it
 * is that no pressing of it has been chosen — or owned.
 * The Cover Art Archive redirects a group to whichever of its releases has
 * artwork, which is exactly the "any sleeve for this album" question being
 * asked.
 *
 * Same rendition as {@link releaseArt} so the two sit at one size on a page
 * that shows both, and a 404 is the ordinary answer — the caller falls back to
 * a monogram the way every other artwork here does.
 */
export function releaseGroupArt(mbid: string): string {
  return `https://coverartarchive.org/release-group/${mbid}/front-250`
}

/**
 * Where a portrait comes from now: this application, not the provider.
 *
 * **The endpoint answers from the shelf first.** An uploaded picture, then
 * `artist.*` beside the artist's own records, then a redirect to whatever a
 * provider found. What that buys is the thing this whole feature began as: the
 * page and every other player reading the same disk show the same face, because
 * they are reading the same file.
 *
 * **`width` is forwarded rather than applied here.** Which rendition a provider
 * has is the provider's business, and it is written down once, in
 * `PortraitRendition` — Commons honours `?width=`, Qobuz puts the rendition in
 * the path, TheAudioDB has none. A second opinion in TypeScript would fail as a
 * 404 from somebody's CDN, which reads as a missing photograph rather than as a
 * bug. The measurement that made it worth doing at all is on that class: a grid
 * of unsized Qobuz `large` files was **39.7 MB of images drawn into 112px
 * circles**.
 *
 * **An album sleeve is not a portrait, and it used to be the fallback here.**
 * The reasoning was that a record of theirs is the nearest honest thing when no
 * photograph exists — and on the page it read as the opposite, because the
 * artists reaching the fallback are by definition the ones the preferred sources
 * have never heard of, which is very nearly the same set as the artists who are
 * not on the front of their own sleeves. A monogram says "no picture"; a sleeve
 * says "this is them", and is wrong. The endpoint answers 404 and the box draws
 * its monogram.
 *
 * Lives here rather than on either page because there are two of them — a tile
 * and the page it opens — and two copies of this is how one ends up showing
 * something the other does not. That is the one disagreement a person is
 * guaranteed to notice, because they get there by clicking the first.
 */
export function artistBannerUrl(artist: { readonly id: string }): string {
  // No width: a banner is drawn at the full width of the page, so the biggest
  // rendition is the right one and `PortraitRendition`'s default would shrink it.
  return `${apiBaseUrl}/api/catalogue/artists/${artist.id}/banner?width=1600`
}

export function artistImageUrl(artist: { readonly id: string }, width?: number): string {
  const sized = width != null ? `?width=${width}` : ''

  // Absolute, through `apiBaseUrl`, like every other picture this file builds:
  // the API is a separate origin in development, and a relative path is served
  // by Vite — which answers 200 with `index.html`, so the box falls back to its
  // monogram and nothing anywhere reports an error.
  return `${apiBaseUrl}/api/catalogue/artists/${artist.id}/portrait${sized}`
}
