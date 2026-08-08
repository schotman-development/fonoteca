# 0005 — TypeScript 7, and isolating what cannot use it

**Status:** accepted, 2026-08-08

## Context

TypeScript 7.0 — the Go-native compiler rewrite — shipped on 8 July 2026, about
a month before this repository was created. It typechecks 8–12× faster, which
compounds in a monorepo with project references.

There is no stable alternative to move to: **6.0 never shipped**, existing only
as a beta, and 7.0.2 is `latest` on npm. Staying on 5.9 would mean adopting a
line that is already superseded.

The catch: **7.0 ships without a stable programmatic API**, expected in 7.1. Any
tool that drives the compiler from Node is blocked.

## Decision

TypeScript 7 everywhere, with one build-time tool isolated.

What the gap costs us, checked rather than assumed:

| Tool | Uses the compiler API? | Effect |
| --- | --- | --- |
| Biome | No — Rust | fine (this is partly why it was chosen over typescript-eslint) |
| Vite | No — does not typecheck | fine |
| Storybook docgen | Configured to `react-docgen`, not the `-typescript` variant | fine, verified by building Storybook |
| **openapi-typescript** | **Yes — builds output via `ts.factory`** | **fails outright** |

`openapi-typescript` dies immediately under TS 7:

```
TypeError: Cannot read properties of undefined (reading 'createKeywordTypeNode')
    at .../openapi-typescript/dist/lib/ts.mjs:11:28
```

Rather than downgrade the whole repository for one build-time tool, the
generator lives in `tools/openapi-codegen` with its own pinned
`typescript@5.9.3`. Its output is plain `.d.ts` text that TS 7 reads perfectly
well, and no application code depends on that package.

## Consequences

- One more workspace package, whose entire reason for existing is a version pin.
  Documented in its README so it is not mistaken for architecture.
- `pnpm gen:api` runs the isolated tool; `packages/api-client` holds only the
  committed output and typechecks on TS 7 like everything else.
- Two TypeScript versions resolve in the repo. pnpm keeps them isolated per
  package, so this does not leak.
- **Revisit when 7.1 ships the programmatic API**: move the `generate` script
  back into `packages/api-client` and delete `tools/openapi-codegen`.
