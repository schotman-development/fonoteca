/**
 * The pure half of the file manager.
 *
 * Path arithmetic, and it is here rather than inline in the page for the reason
 * `seating.ts` is: nothing in this file imports React, a stylesheet or the API
 * client, so `node --test` runs it with Node's own type stripping and no runner
 * dependency.
 *
 * Every function here decides where a file ends up. A rename that drops a
 * segment moves an album to the library root, and a scan then reads that as one
 * deletion and a hundred arrivals — so this is the smallest, most testable
 * piece of the most destructive screen in the application.
 */

/** Library-relative paths use '/', on every platform. The API says so too. */
const SEPARATOR = '/'

export type Crumb = {
  readonly name: string
  /** Library-relative; the empty string is the root. */
  readonly path: string
}

/**
 * The trail from the library root down to `path`, root first.
 *
 * Always starts with the root, so there is always a way back up — including
 * from a folder that has just been trashed out from under the tab.
 */
export function breadcrumbs(path: string): readonly Crumb[] {
  const segments = path.split(SEPARATOR).filter((segment) => segment.length > 0)

  const trail: Crumb[] = [{ name: 'Library', path: '' }]

  segments.reduce((prefix, segment) => {
    const next = prefix ? `${prefix}${SEPARATOR}${segment}` : segment
    trail.push({ name: segment, path: next })
    return next
  }, '')

  return trail
}

/** `Brahms/Sym 1` + `01.flac` → `Brahms/Sym 1/01.flac`. The root has no prefix. */
export function joinPath(folder: string, name: string): string {
  const parent = folder.replace(/\/+$/, '')
  return parent ? `${parent}${SEPARATOR}${name}` : name
}

/** The folder an entry sits in. The empty string for one at the library root. */
export function parentOf(path: string): string {
  const separator = path.lastIndexOf(SEPARATOR)

  return separator < 0 ? '' : path.slice(0, separator)
}

/**
 * Where an entry ends up, given a folder to sit in and a name to wear.
 *
 * <b>One function for both acts, because they are one act.</b> A move changes
 * the folder, a rename changes the name, and the API takes a whole destination
 * path either way — so two functions would only be two chances to build that
 * path differently.
 *
 * <b>The name's separators are stripped; the folder's are kept.</b> A folder is
 * a path and slashes belong in it. A name is one segment, and a slash typed
 * there is somebody moving an entry from a box labelled "name" — honoured
 * literally it silently relocates the thing, and the row it was on disappears.
 *
 * <b>`..` is dropped from the folder rather than honoured.</b> The API's
 * containment check would refuse it, which turns a plausible-looking path into
 * an opaque 400; and the failure this guards against is the one where it
 * <i>works</i>.
 */
export function destinationFor(path: string, folder: string, name: string): string | null {
  const clean = name.replaceAll(SEPARATOR, ' ').replaceAll('\\', ' ').trim()

  if (clean.length === 0 || clean === '.' || clean === '..') return null

  const parent = folder
    .replaceAll('\\', SEPARATOR)
    .split(SEPARATOR)
    .filter((segment) => segment.length > 0 && segment !== '.' && segment !== '..')
    .join(SEPARATOR)

  const destination = joinPath(parent, clean)

  return destination === path ? null : destination
}

/**
 * An uploaded file's own path, relative to the folder it is going into.
 *
 * `webkitRelativePath` is what a directory picker sets — `Album/CD1/01.flac` —
 * and it is the only reason uploading a whole album keeps its shape. A file
 * picker leaves it empty, so the name is the whole path.
 *
 * The leading segment of a directory pick is the folder the person chose, which
 * is exactly what should be created; dropping it would empty a two-disc set
 * into one directory and renumber nothing, which reads as a rip that lost its
 * discs.
 *
 * <b>It is deliberately not joined to the destination here.</b> The two halves
 * go to the API separately because it treats them differently: the folder
 * already exists and is believed, this half was invented by a browser and is
 * sanitised. Joined into one string, the sanitising ran over both — and it
 * removes `:`, so uploading into `Essential Brahms, Volume 1: 50 Tracks…`
 * created a *new* folder without the colon.
 */
export function uploadName(file: File): string {
  const relative = file.webkitRelativePath?.trim()

  return relative && relative.length > 0 ? relative : file.name
}

/**
 * How much of a folder the catalogue has actually placed.
 *
 * Null when nothing under it is catalogued at all — an uploaded album before
 * the first scan, or a folder of artwork. That is a different statement from
 * "none of it matched", and the screen says so differently.
 */
export function matchSummary(entry: {
  readonly cataloguedFiles: number
  readonly identified: number
  readonly attributed: number
}): { readonly label: string; readonly tone: 'positive' | 'warning' | 'neutral' } | null {
  if (entry.cataloguedFiles === 0) return null

  // A single file is not "1 filed". The count is only information when there is
  // more than one thing being counted, and every row of an album folder's
  // listing is one file.
  const count = entry.cataloguedFiles === 1 ? '' : `${entry.cataloguedFiles} `

  if (entry.attributed === entry.cataloguedFiles) {
    return { label: `${count}filed`, tone: 'positive' }
  }

  if (entry.identified === 0) {
    return { label: `${count}unmatched`, tone: 'warning' }
  }

  return {
    label: `${entry.attributed}/${entry.cataloguedFiles} filed`,
    tone: entry.attributed === 0 ? 'warning' : 'neutral',
  }
}

/**
 * The bytes of one file.
 *
 * A plain URL rather than a typed client call, because what consumes it is an
 * `<img>`, an `<audio>` and a ranged `fetch` — none of which take a parsed JSON
 * body. The API decides the media type from its own allowlist, so nothing here
 * has an opinion about what the file is.
 *
 * <b>The base URL is a parameter rather than an import, and that is not
 * ceremony.</b> Importing it from `api.ts` pulls in the generated client, whose
 * `ApiError` uses TypeScript parameter properties — which Node's strip-only
 * type stripping refuses outright. One import took this whole file out of
 * `node --test`, which is the only reason its path arithmetic is tested at all.
 * Same rule `seating.ts` states: nothing here may import React, a stylesheet, or
 * the API client.
 */
export function contentUrl(baseUrl: string, path: string): string {
  return `${baseUrl}/api/files/content?path=${encodeURIComponent(path)}`
}

/**
 * A picture of one entry: a file's embedded cover, or a folder's own `cover.jpg`.
 *
 * 404 is the ordinary answer and `Artwork` draws its monogram for it, which is
 * why this is a bare URL handed to an `<img>` rather than a request anything
 * waits on.
 */
export function artUrl(baseUrl: string, path: string): string {
  return `${baseUrl}/api/files/art?path=${encodeURIComponent(path)}`
}

/**
 * The head of a text file, as text.
 *
 * <b>A range request, not a truncated download.</b> The endpoint enables range
 * processing so an `<audio>` element can seek; the same support means a preview
 * of a rip log costs eight kilobytes rather than the whole file, and a file that
 * is enormous despite its extension cannot be pulled into the tab by clicking
 * a row.
 */
export async function readTextHead(
  baseUrl: string,
  path: string,
  bytes = 8 * 1024,
): Promise<string> {
  const response = await fetch(contentUrl(baseUrl, path), {
    headers: { Range: `bytes=0-${bytes - 1}` },
  })

  if (!response.ok) throw new Error(`Could not read ${path}`)

  const text = await response.text()

  // A 206 means there is more; a 200 means the server sent everything because
  // the file is smaller than the range asked for.
  return response.status === 206 ? `${text}\n…` : text
}
