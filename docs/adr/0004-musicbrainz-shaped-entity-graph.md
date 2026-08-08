# 0004 — An entity graph shaped like MusicBrainz

**Status:** accepted, 2026-08-08

## Context

The obvious schema for a music library is `artist → album → track`. It is also
the one decision that would force a rewrite later.

Two pressures push against it:

1. **Dedupe, which is in scope now.** The core problem statement is "the same
   recording in five encodings". A schema where a track *is* a file cannot
   express that, because the five files are neither duplicates nor unrelated —
   they are versions of one thing.
2. **Roon-style browsing, which is explicitly out of scope to build but must
   not be foreclosed.** "Every recording of this composition", "everything this
   engineer worked on", "this performance across seven releases" are only
   answerable if `Work`, `Recording`, `Release` and `Track` are distinct
   entities with typed relationships between them.

## Decision

Model MusicBrainz's shape from the start, before anything populates it.

| Entity | Meaning |
| --- | --- |
| `Work` | the composition — sparse, and that is fine |
| `Recording` | a captured performance; the dedupe anchor, what AcoustID identifies |
| `ReleaseGroup` / `Release` | the album as an idea, and each published edition |
| `Track` | a recording's position on a release |
| `MediaFile` | bytes on disk; N per recording |
| `Artist`, `ArtistCredit` | ordered, join-phrased credits, not a bare many-to-many |
| `Relationship` | typed links: composer, conductor, engineer, remixer, cover-of |

Identifiers are strongly typed (`RecordingId`, `ReleaseId`, …) over
`Guid.CreateVersion7`. Two reasons: eight entity types keyed by bare `Guid` is
eight chances to pass the wrong one, and v7 is time-ordered, so a 100k-file
scan's inserts stay at the right-hand edge of the index instead of scattering
page splits across the whole B-tree.

## Consequences

- More tables and more joins than a flat schema, for a catalogue that is empty
  today. Accepted knowingly: this is the piece that cannot be retrofitted.
- `ArtistCredit` carries `Position` and `JoinPhrase` so that "Miles Davis feat.
  John Coltrane" keeps both its billing order and its navigable links —
  collapsing it to a string loses the links, collapsing it to an unordered join
  loses the billing.
- Strongly-typed ids need EF value converters, registered once in
  `FonotecaDbContext.ConfigureConventions`.
- Review this schema against the Roon browsing questions at the end of the first
  ingest slice, while changing it is still cheap.
