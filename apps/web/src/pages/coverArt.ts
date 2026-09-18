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
 * A photograph of the artist, at a size worth downloading.
 *
 * **Fetched by the browser, not proxied**, for `releaseArt`'s reason and against
 * the same kind of host: these are the providers' own CDNs, and a proxy would
 * buy one thing at the cost of an endpoint, a cache and a rate limit to look
 * after.
 *
 * **Three sources reach this, and the differences show.** Qobuz serves a press
 * photo: square, cropped to the faces, and what this is for. TheAudioDB serves
 * a contributor's artist thumbnail, which it keeps in a different field from
 * album art — measured on this library, 15 of 16 are genuine photographs.
 * Wikidata serves whatever Commons holds, which is often a wide concert shot:
 * measured, AC/DC's was a photograph of the Olympic Stadium.
 *
 * **No source stores a display-sized image, and this is where that is fixed.**
 * The stored URL is the biggest rendition, because that is the one that keeps
 * the choice open; every box this application draws is small. Measured in
 * Chromium: a tile is 112px and the artist page's portrait is *86*.
 *
 * They resize different ways and all of them are handled by width alone, so a
 * caller asks for a size and never for a provider:
 *
 * - Commons honours `?width=`. Its originals are frequently several megabytes of
 *   scanned photograph — one is 972 KB whole and 28 KB at 250.
 * - Qobuz puts the rendition in the path. `large` is not a fixed size: measured
 *   across 251 of them the median is 211 KB and the largest is **13.3 MB at
 *   4480x6720**, so a grid of them was 39.7 MB of images into 112px circles.
 *   `small` is 129-438px and 3-20 KB, which is the whole page in about the size
 *   of one of the old ones.
 * - TheAudioDB offers no renditions at all and falls through unchanged. Their
 *   thumbnails are already small, which is why that costs nothing.
 */
export function artistPortrait(url: string, width = 250): string {
  const sized = new URL(url)

  if (sized.pathname.startsWith('/wiki/Special:FilePath/')) {
    sized.searchParams.set('width', String(width))
    return sized.toString()
  }

  // Qobuz. `small` covers a 112px circle on a 2x display at the low end of its
  // range and comfortably above it at the high end; anything bigger than that is
  // for a box this application does not currently draw, and `medium` is the
  // honest answer there rather than the 13 MB one.
  const rendition = width <= 400 ? 'small' : 'medium'

  return url.replace('/images/artists/covers/large/', `/images/artists/covers/${rendition}/`)
}

/**
 * The picture for an artist.
 *
 * **An album sleeve is not one, and it used to be the fallback here.** The
 * reasoning was that a record of theirs is the nearest honest thing when no
 * photograph exists — and on the page it read as the opposite, because the
 * artists reaching the fallback are by definition the ones the preferred
 * sources have never heard of, which is very nearly the same set as the artists
 * who are not on the front of their own sleeves. So the tile showing a face was
 * a household name and the tile showing a cover was a session player, a
 * conductor or a guest, wearing a picture of somebody else's album. A monogram
 * says "no picture"; a sleeve says "this is them", and is wrong.
 *
 * Three sources now stand behind `portrait` — one searched by name and two
 * looked up by MusicBrainz id — which is what makes dropping the fallback
 * affordable rather than merely correct.
 *
 * A fourth was measured and left out for the same reason the fallback was:
 * Deezer covers the most artists and **16 of the 20 pictures it supplied here
 * were album covers**, because its artist image is whatever the label gave it.
 *
 * Lives here rather than on either page because there are two of them — a tile
 * and the page it opens — and two copies of "photograph, then nothing" is how
 * one ends up showing something the other does not. That is the one
 * disagreement a person is guaranteed to notice, because they get there by
 * clicking the first.
 *
 * Returns the URL or nothing, rather than a props object, because the two
 * callers spell the prop differently: `CatalogueCard` takes `image` and
 * `Artwork` takes `src`.
 */
export function artistImageUrl(
  artist: { readonly portrait?: string | null },
  width?: number,
): string | undefined {
  return artist.portrait != null ? artistPortrait(artist.portrait, width) : undefined
}
