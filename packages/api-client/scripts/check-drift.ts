/**
 * Fails if the committed client types no longer match `openapi.json`.
 *
 * This is the whole point of the contract seam. The backend and the frontend
 * have separate toolchains and separate build steps, so nothing else would
 * notice a renamed field until it returned `undefined` at runtime. Here it is a
 * failed CI job with a diff attached.
 *
 * Run with plain `node` — Node 24 strips types natively, no build step.
 */

import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const packageRoot = join(dirname(fileURLToPath(import.meta.url)), '..')
const committedPath = join(packageRoot, 'src', 'schema.d.ts')
const specPath = join(packageRoot, '..', '..', 'openapi.json')

// The generator lives in tools/openapi-codegen, which pins TypeScript 5.9 —
// openapi-typescript needs the compiler API that TS 7.0 does not ship yet.
// See tools/openapi-codegen/README.md.
const generatorRoot = join(packageRoot, '..', '..', 'tools', 'openapi-codegen')
const generator = join(generatorRoot, 'node_modules', '.bin', 'openapi-typescript')

const regenerated = execFileSync(generator, [specPath], {
  encoding: 'utf8',
  cwd: generatorRoot,
})
const committed = readFileSync(committedPath, 'utf8')

if (regenerated.trim() === committed.trim()) {
  console.log('api-client: schema.d.ts matches openapi.json')
  process.exit(0)
}

console.error(
  [
    '',
    'api-client is out of date with the API contract.',
    '',
    `  spec:      ${specPath}`,
    `  committed: ${committedPath}`,
    '',
    'The API changed but the generated client was not regenerated, or the spec',
    'was not rebuilt after changing the API. Fix with:',
    '',
    '  dotnet build apps/api/src/Fonoteca.Api   # regenerate openapi.json',
    '  pnpm gen:api                             # regenerate the client',
    '',
    'then commit both.',
    '',
  ].join('\n'),
)
process.exit(1)
