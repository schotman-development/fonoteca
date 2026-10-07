# 0015 — Album booklets, from the Cover Art Archive and Qobuz

**Status:** accepted, 2026-10-07

## Context

The owner wants, beside each album's music, one cover, the motion artwork
(ADR 0014) and every booklet — and explicitly nothing else of the packaging:
no backs, discs, trays, spines or obis.

Two sources have booklets. The **Cover Art Archive** holds whatever people
uploaded against a MusicBrainz *release*, each image typed (`Front`, `Back`,
`Booklet`, `Medium`, `Raw/Unedited`…), originals up to 14 MB measured here,
some PNG and some PDF. **Qobuz** sells some albums with a digital booklet, a
PDF of about 4 MB on `static.qobuz.com`, listed as a "goodie" on `album/get`
(never on a search row). Apple's public pages have no booklets.

Measured on this library (613 held albums): 24 sampled albums held 84 archive
images, a third of them booklet pages; across all six sampled albums with 4–9
editions each, the extra editions added mostly booklet pages and repeated
fronts. 3 of 22 sampled albums Qobuz sells came with a PDF. 183 of the 613
have a proven pressing; the rest are held to the album alone.

## Decision

**The archive's booklet is one edition's, the fullest, kept whole**
(`Providers/CoverArt/AlbumBooklets.cs`). Every edition of the album with a
MusicBrainz id is listed; the one with the most `Booklet` images wins, the
display edition — the pressing the files prove, else the one the cover comes
from — listed first so it wins a tie (the owner's choice: "prefer the cover
edition but add from all editions"). Pages are taken in the archive's order,
as uploaded (full size, the owner's choice), edited scans over `Raw/Unedited`
ones unless the raw are all there is. Mixing pages from two editions would
interleave two printings, so one edition's booklet is never topped up from
another's.

**The shop's PDF is added beside it**, found by `QobuzCovers.Match` — the
cover fallback's barcode-then-title rule — and fetched only from their file
host and only if the bytes are a PDF.

**Kept like the motion artwork, written beside the tracks.** A ninth
enrichment stage stores them in `AlbumBooklets` (the stamp and provenance) and
`AlbumBookletFiles` (the bytes). A row with no files expires after a week; a
row with any never does; an outage writes nothing. The tag write puts them in
the album folder as `booklet-01.jpg` onwards and `booklet.pdf` (the owner
chose the album folder over a `Scans` subfolder), journalled as
`tagging.booklet` and undone with the sleeve.

**A booklet a person put there wins** (the owner's choice): any `booklet*`
file in the folder that is not one of these by name and length means nothing
is written for that album — not beside it, which would be the same booklet
twice, and not over it. The five album folders that had their own `Scans`
were reorganised once by hand to match: their booklet pages renamed into the
folder, the rest trashed.

## Consequences

**The catalogue grows by roughly 4–6 GB** across this library at full size,
held in PostgreSQL until the tag write copies it out.

**Every edition is listed once per album** — about 3,650 archive requests for
this library — and both sources are asked for every album, because "every
booklet" is the ask and a CD's scanned booklet and the shop's digital one are
different documents. An album can therefore get both.

**A file that is gone, withheld or unkeepable is skipped, not fatal** — a
403, 404 or 410 from either source, over 100 MB, or not a picture or a PDF —
so a dead link cannot hold the stage at one album. **So is one edition the
archive cannot give**: one release of *Live at the Regal* sits on an
archive.org node that answers 500 every time, and read as an outage it ended
the stage at that album on every run. Its album's booklet comes from the
editions that answer, the next fullest where it was the fullest. Only when no
edition can be listed, or every edition with pages fails to give them, is it
an outage, ending the stage unstamped.

**A booklet written by an earlier run reads as a person's** if its stored
files are later replaced by hand, and stops the album until it is moved.

**Not built:** serving booklets over the API, and choosing another edition's
booklet by hand.
