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
