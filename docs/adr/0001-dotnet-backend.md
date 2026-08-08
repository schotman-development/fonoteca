# 0001 — .NET 10 for the backend

**Status:** accepted, 2026-08-08

## Context

Fonoteca is a self-hosted music library manager for 100,000+ tracks, aiming to
be the most reliable one that exists. Four pillars: catalogue and dedupe,
acquisition from Qobuz and Deezer, *arr-style upgrade monitoring, and a tag
editor. It must reach AcoustID, MusicBrainz, Qobuz and Deezer, hash and
integrity-test audio, and write tags back to files.

Python, Go, Rust, Node, Kotlin and Elixir were all considered. The initial
comparison leaned toward Python, on the grounds that `mutagen` is the most
road-tested tag-writing library in existence.

## Decision

**C# / .NET 10 (LTS).**

The Python argument was comparing across two different product categories. Look
at what actually ships in *this* one:

| Product | Stack |
| --- | --- |
| Roon | .NET (migrated to .NET 10, April 2026) |
| Lidarr | .NET |
| Jellyfin | .NET |
| beets | Python |
| MusicBrainz Picard | Python |

Every serious library **manager** is .NET. The two Python tools are manual
tagging utilities — excellent ones, but a different kind of program with
different constraints. Choosing Python because Picard uses Python would be
copying a CLI tagger's stack into a long-running multi-user server.

Supporting reasons: real threads make a 100k-file scan parallelise without
subprocess gymnastics, `dotnet publish` produces a self-contained single-file
binary that fits the host's Tier 0 policy, and the type system is strong
without Rust's cost across four product pillars.

## Consequences

- The MusicBrainz and AcoustID client libraries are thinner than Python's. We
  use `MetaBrainz.MusicBrainz` (which Jellyfin also ships) and hand-write the
  AcoustID client against its HTTP API.
- Tag writing needed its own decision; see [ADR 0002](0002-two-tag-libraries.md).
- Chromaprint has no usable .NET binding, so fingerprinting shells out to
  `fpcalc`. This is true of every candidate stack except Rust, so it costs
  nothing relative to the alternatives.
