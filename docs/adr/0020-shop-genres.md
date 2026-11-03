# 0020 — A shop's genre where MusicBrainz has none

**Status:** accepted, 2026-11-03

## Context

Genres come from MusicBrainz: an album's own (`ReleaseGroups.Genres`), else its
recordings' (`FileGenres`). Measured here on 2026-11-03, 119 held albums had
neither, so their files carry no genre, the genre tree does not list them and
the classical screens cannot find the classical ones among them. A client asked
for genres on those albums, and the owner's answer was that a genre missing
completely is the library's problem, not the client's, to be filled "from
another source".

The two shops Fonoteca already recognises records at answered for nearly all
of them, by the same rules the sleeves and motion art use:

| | by barcode | by title | answered |
| --- | --- | --- | --- |
| Apple Music | 67 | 31 | 98 |
| Qobuz | 53 | 47 | 100 |

Both answered 90; neither 11 (four live Mark Knopfler albums among them). Apple
names one genre a record, in English and finer-grained ("Contemporary Blues",
"Classical Crossover"); Qobuz names a path in the account's language
("Klassiek"), and files film, crossover and pop records as classical.

## Decision

**Apple Music first, Qobuz where Apple has nothing**, the owner's choice on that
measurement (`Providers/Genres/ShopAlbumGenres.cs`). Each recognises the record
as before — `AppleMusicRecords`, `QobuzCovers.Match` — so the three uses cannot
disagree about which record an album is; Apple's genre is its documented
lookup's `primaryGenreName`, Qobuz's the ids on `album.genre.path`.

**Stated as MusicBrainz states genres** (`ShopGenres`), into the same column,
so every reader — the genre tree, `ClassicalWorks`, the tag write, Subsonic —
takes them as it takes MusicBrainz's. Apple's names are lowercased, a few
spelled out ("R&B/Soul" is `r&b`, "Romantic Era" is `classical` and
`romantic classical`); Qobuz's are read by id, since their names are
translated. A shop genre with no counterpart ("Music", Qobuz's "Pop/Rock") is
no answer. `ReleaseGroups.GenresSource` says which shop it was; null is
MusicBrainz.

**Only where MusicBrainz has nothing, and only after it was asked.** An
enrichment stage after the artists asks about held albums with no genre of
their own and none on any recording, once the album-genre stage has stamped
them (an album with no MBID, a download filed as the shop sold it, has nobody
else to ask). `ReleaseGroups.ShopGenresLookupUtc` is the stamp: an answer is
never asked again, none is asked again after a week, an outage stamps nothing.

**MusicBrainz replaces it.** The album-genre stage still asks MusicBrainz
weekly for an album with a shop's genres, and its genres replace them; and a
shop's genres are taken off an album once MusicBrainz gives one of its
recordings genres, since `FileGenres` prefers an album's to a recording's.

## Why not the alternatives

**Genres typed by hand on every album**: the tag editor puts a person's GENRE
in the files, which other players read, but not in the catalogue's genres the
genre tree and Subsonic list; and for a hundred albums it is a week of
somebody's evenings.

**A column apart from MusicBrainz's genres** would keep the two sources apart
for good, at the price of every reader learning a second column. `GenresSource`
keeps what rule 4 asks for — which source said what — in one.

## Consequences

Apple's terms forbid scraping, as for the motion art; this stage uses only
Apple's documented search and lookup, one request a second.

Known and accepted:

- A shop's genre is one record's, not a vote; Qobuz's "Klassiek" on a crossover
  album makes it classical on every screen.
- An album a shop answered is never asked again, so a better answer later is
  missed; clearing `ShopGenresLookupUtc` and the genres by hand re-asks.
