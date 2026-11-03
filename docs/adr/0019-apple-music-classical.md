# 0019 — Pieces, performers and composers' lives from Apple Music Classical

**Status:** accepted, 2026-10-31

## Context

Classical metadata is where a library manager is judged, and MusicBrainz is
thin on it in exactly the places a classical listener looks: measured here on
2026-10-30, of 1,044 held recordings on albums or recordings tagged classical,
260 were linked to no work and 272 named no performer. Of the 165 composers of
held classical works, 15 had no Wikipedia article to show.

Apple Music Classical has those facts — every track filed under its piece, the
piece under its composers, and everybody on an album with their roles — and
offers no API of its own. The documented Apple Music API needs a paid developer
membership and a self-signed token, and gives a song only its work and movement
names, never a performer's role; Apple Music Feed's terms forbid this use. The
public site, `classical.music.apple.com`, embeds the JSON each page is drawn
from, readable by anything a browser can fetch.

## Decision

**Two requests an album, each something a browser fetches without signing
in** (`Providers/AppleMusic/AppleMusicClassical.cs`), after the record has been
recognised the way the motion artwork recognises one (ADR 0014) — the same
code, `AppleMusicRecords`, so the two cannot come to disagree about which
record an album is:

1. The album's classical page. Its track list comes in sections, one per piece,
   headed by the piece (`secondRowLinks`, a link to a `work` page and Apple's
   id for it) and its composers (`firstRowLinks`, with Apple's artist ids).
   Each track names its performers; a list of everyone on the album gives each
   one's roles. A section whose heading links no piece performs nothing: its
   tracks' `workName` is whatever the label typed — a composer's surname, the
   track's own title.
2. The documented lookup, for each track's disc, number and length, since the
   page numbers nothing. *Amended 2026-11-03:* each song by its own id, as the
   page names it. Asked for the album's songs, Apple answered two records it
   carries (Previn's *Nutcracker*, a Shostakovich cycle) with no song at all.

A held file is the track at its disc and number whose length is within three
seconds of its own (`ClassicalCatalogue.Seat`); a file of unknown length is
nobody's. Positions, never titles: the page prints a movement where MusicBrainz
prints the symphony in front of it.

**Only where MusicBrainz has nothing**, the owner's choices:

- **A piece is a work row with no MBID**, keyed on Apple's id
  (`Works.AppleWorkId`), so the album page's headings, the composer pages, the
  tag write and Subsonic read it as they read MusicBrainz's. Its composers are
  contributions (`Contribution.Source`) carrying the MBID of the one composer
  MusicBrainz credits elsewhere under that name (`ClassicalCatalogue.Composer`),
  and a composer link where that artist has a row. When MusicBrainz names the
  recording's work, `CatalogueWriter.UpsertAsync` takes it and Apple's goes once
  nothing performs it, its hearts moving to the whole piece MusicBrainz's work
  is part of; while MusicBrainz names none, Apple's stays, through a merge too.
- **Performers only on a recording MusicBrainz names no performer for**, in
  MusicBrainz's link types (`ClassicalCatalogue.Links`: an orchestra is a
  `performing orchestra`, a soprano sings `soprano vocals`), with no artist row
  minted, as for MusicBrainz's own contributions. Describing the recording again
  keeps them until MusicBrainz names a performer.
- **A composer's biography only where Wikipedia has none**, credited to the
  Apple page. Wikipedia is asked first, and again a week on — or on the next
  run, where an outage left the composer unasked — and its article replaces
  Apple's when there is one.

An enrichment stage after the biographies asks about every held album tagged
classical (crossover included — Apple answers a record it does not carry with a
404), not only those with a gap, since the composers' pages come off the same
answer. `ReleaseGroups.ClassicalLookupUtc` is the stamp: a record Apple carries
is never asked about again (`AppleClassicalAlbumId` keeps which), one it does
not is asked again after a week, an outage stamps nothing.

## Why not the alternatives

**The Apple Music API** would be a supported route, for a yearly fee, and
answers the work names and nothing about who plays. The performers are the half
MusicBrainz is thinner on.

**Folding Apple's answers into MusicBrainz's rows** — a title on the recording,
performers as plain contributions — would lose which source said what, and with
it any way to let MusicBrainz's answer replace Apple's later (rule 4's reasoning,
applied to two sources).

## Consequences

**Apple's terms forbid scraping its services**, as for the motion artwork. The
pages are read through the motion stage's page client and its gate, one a
second, and only for albums somebody holds.

**A page that changes shape stops the stage rather than answering "none"**:
no embedded JSON, or JSON without sections, throws and nothing is stamped.

Known and accepted:

- A record Apple carries is never asked about again, so a file added to the
  album later stays without, and so does a merged recording's survivor for
  Apple's performers (its piece is kept). Clearing `ClassicalLookupUtc` by
  hand re-asks.
- Apple's page leaves some tracks out (20 of 21 on one record measured); those
  files get nothing.
- A composer is matched by printed name among the composers MusicBrainz already
  credits. One MusicBrainz has never credited, or two who share a name, get the
  contribution and no link and no biography.
- Apple's piece has no type, key, date or parent. Whether it is classical is
  decided by its recordings' and album's genres alone (`ClassicalWorks.Is`).
