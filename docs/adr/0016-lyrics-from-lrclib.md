# 0016 — Lyrics, from LRCLIB, into the files

**Status:** accepted, 2026-10-14

## Context

Roon showed lyrics this library did not have. AllMusic, which Roon licenses,
answers every request with a Cloudflare 403 and has no public API, so the
owner chose open sources instead; for lyrics that is **LRCLIB**
(`lrclib.net`), a free, crowd-sourced database of timed lyrics with no key
and no published rate limit.

Its `/api/get` is an exact match on folded names, never a search: the title
and the artist lowercased with punctuation and apostrophes dropped, the album
a further exact filter where one is sent, the length held within two seconds
either way, the lowest id winning among matches (its server source, read
2026-10-14). A miss is a 404. A busy server answers anyone 503 with
`Retry-After: 1` — about half of a first sample here, at one request a second.

Measured on this library: of 60 random held recordings LRCLIB answered 54 —
23 timed, 11 plain, 2 instrumental, 18 not found (mostly instrumental pieces:
movements, game music, jazz) — and 6 never got past the 503s. About 3% of
the files carry lyrics in a tag. 153 `.lrc` files sit beside tracks: 73 are
lyrics (one timed), 80 are a release group's advert (`www.t.me/pmedia_music`
on 69, `kmn` on 11).

What reads what: Roon reads the `LYRICS` tag (ID3 `USLT`, MP4 `©lyr`), timed or
plain, and not `.lrc` files (Roon's own posts, 2019). Navidrome 0.64 reads a
`.lrc` beside the track first, then the `lyrics`/`unsyncedlyrics`/`USLT` tag,
and parses timed LRC in either. OpenSubsonic clients ask the server
(`getLyricsBySongId`, the `songLyrics` extension).

## Decision

**An enrichment stage keeps LRCLIB's answer per recording** (`RecordingLyrics`,
`EnrichmentService.LyricsAsync`, `Providers/Lrclib/LrclibLyrics.cs`). A song is
asked about as its first held file has it — that file's album and its length
as the probe measured it — in up to three asks, each wider, all held to the
length: with the album; with any album, since the same title by the same
artist within two seconds is the same song wherever it was released (a
Hungarian Sinatra compilation found nothing by album and *Fly Me to the Moon*
by any); then, where the credit line bills several, the first artist alone.
Both texts are kept, timed and plain; an instrumental is an answer. A row
with no words expires after a week, a row with words or marked instrumental
never does, and an outage writes nothing; three songs in a row with no answer
end the stage.

**The tag write puts them in the files** (the owner's choice over `.lrc`
files or the catalogue alone): the `LYRICS` Vorbis comment, the ID3 `USLT`
frame (language `XXX`, the frame's word for unknown), the MP4 `©lyr` atom.
**Timed LRC where there is any** (the owner's choice), so Roon and Navidrome
can follow the line being sung; a player that does not understand LRC shows
the times as text. **They replace the file's own words, as every other
field does** (the owner's choice), and the same words under foobar2000's
`UNSYNCEDLYRICS` go with them; where LRCLIB has none the file keeps its own.

**Every `.lrc` beside a written track goes to the trash** (the owner's
choice), once the tag holds the words — and where LRCLIB has none, the
lyrics file's own words go into the tag first. An advert is not words:
fewer than three lines with any (`LyricsText.IsLyrics`). A file the
catalogue has not answered, written for its ReplayGain and DR alone, takes a
lyrics file's words, which are its own, and not LRCLIB's, which are part of
the answer it waits for. The trash is the
file manager's stamped folder, journalled as `tagging.catalogue.lyrics`, and
Undo puts the file back.

**Fonoteca shows them nowhere** (the owner's choice). Subsonic serves them:
`getLyricsBySongId`, timed line by line, and the original `getLyrics`, plain,
both from the catalogue's words else the file's own.

## Consequences

**ATL damaged timed lyrics on every save, and still parses them lossily.**
Measured on ATL 7.16: it reads LRC into its own structure and writes it back
from that, so the space after each time went and a blank timed line moved to
the top as a header — in any file holding timed words that the tag write
touched for any reason. The writer now hands back the words as stored
(`TagWriter.RenderAsync`, where the file holds one set of words), and the
lyrics are read as stored by TagLib# rather than as ATL parses them
(`TagReader.StoredLyrics`), so the diff is stable and the check that a write
changed nothing it did not mean to now covers them.

**A first run is slow**: up to three gated requests a recording, about two to
four hours for this library's 7,800 songs, then the week's misses again each
week — mostly instrumental pieces LRCLIB will never hold.

**Junk longer than two lines still passes as words**: a rip's "thank you for
downloading" note in ten Sinatra `.lrc` files, a book's preface beside a
spoken-word track. Where LRCLIB answers, its words replace them; where it does
not, they go into the tag.

**The match is by name.** The length guards a live take against the studio
one; nothing guards a different song of the same title by the same artist
and length. `LrclibId` and `MatchedBy` on the row say where any answer came
from.

**Not built:** showing lyrics in Fonoteca, and `songLyrics` version 2's
translations and word timing.
