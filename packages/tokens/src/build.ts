/**
 * Emits `dist/tokens.css` from the TypeScript sources.
 *
 * Run with plain `node src/build.ts` — Node 24 strips types natively, so the
 * token package needs no build dependency of its own.
 */

import { mkdir, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { flatten } from './cssVars.ts'
import { scales } from './scales.ts'
import { dark, light } from './semantic.ts'

const here = dirname(fileURLToPath(import.meta.url))
const outFile = join(here, '..', 'dist', 'tokens.css')

const indent = (decls: Record<string, string>, pad: string): string =>
  Object.entries(decls)
    .map(([name, value]) => `${pad}${name}: ${value};`)
    .join('\n')

const scaleVars = flatten(scales)
const lightVars = flatten(light)
const darkVars = flatten(dark)

/**
 * Three theme states, and all three must be handled:
 *
 *   - `data-theme="light"` / `data-theme="dark"` — an explicit user choice.
 *   - no attribute at all — "follow the system", which is the DEFAULT, and is
 *     distinguishable from an explicit light choice only by `prefers-color-scheme`.
 *
 * Hence the `:not([data-theme='light'])` guard on the media query: without it,
 * a user who explicitly picked light would still get dark on a dark-mode OS.
 *
 * `color-scheme` is set alongside so native scrollbars, form controls and the
 * canvas behind the page follow the theme too.
 */
const css = `/*
 * GENERATED FILE — do not edit.
 * Source: packages/tokens/src/*.ts   Regenerate: pnpm --filter @fonoteca/tokens build
 */

:root {
  color-scheme: light;

  /* ---- Scales (theme-independent) ---- */
${indent(scaleVars, '  ')}

  /* ---- Semantic (light) ---- */
${indent(lightVars, '  ')}
}

/* Explicit dark choice. */
:root[data-theme='dark'] {
  color-scheme: dark;

${indent(darkVars, '  ')}
}

/* System preference, unless the user explicitly chose light. */
@media (prefers-color-scheme: dark) {
  :root:not([data-theme='light']) {
    color-scheme: dark;

${indent(darkVars, '    ')}
  }
}
`

await mkdir(dirname(outFile), { recursive: true })
await writeFile(outFile, css, 'utf8')

const total = Object.keys(scaleVars).length + Object.keys(lightVars).length
console.log(
  `tokens: wrote ${total} custom properties (${Object.keys(scaleVars).length} scale, ` +
    `${Object.keys(lightVars).length} semantic x2 themes) -> dist/tokens.css`,
)
