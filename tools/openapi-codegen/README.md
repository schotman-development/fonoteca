# @fonoteca/openapi-codegen

A one-script package that exists solely to pin a **different TypeScript version**
than the rest of the repository.

## Why this is separate

`openapi-typescript` builds its output by driving the TypeScript compiler API
(`ts.factory`). **TypeScript 7.0 ships without a stable programmatic API** — the
Go-native rewrite landed in July 2026 and the compiler API is expected in 7.1 —
so under TS 7 the generator dies immediately:

```
TypeError: Cannot read properties of undefined (reading 'createKeywordTypeNode')
    at .../openapi-typescript/dist/lib/ts.mjs:11:28
```

The three ways out, and why this one:

| Option | Verdict |
| --- | --- |
| Downgrade the repo to TS 5.9 | Loses the 8–12× typecheck the project chose TS 7 for, across every package, to satisfy one build-time tool. |
| Pin TS 5.9 inside `packages/api-client` | That package is also typechecked as part of the build; two compiler versions in one package is worse than two packages. |
| **Isolate the tool** | The generator is build-time only. Its output is plain `.d.ts` text that TS 7 reads perfectly well. Nothing imports this package. |

So the incompatibility is confined to one directory that no application code
depends on, and `packages/api-client` stays on TS 7 like everything else.

## Removing this

When `openapi-typescript` supports TypeScript 7 — likely once 7.1 ships the
programmatic API — move the `generate` script back into
`packages/api-client/package.json`, delete this directory, and drop `tools/*`
from `pnpm-workspace.yaml` if nothing else has moved in.

## Usage

Not invoked directly. `pnpm gen:api` at the repository root runs it.
