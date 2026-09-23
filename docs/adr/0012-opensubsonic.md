# 0012 — OpenSubsonic, and the two things a client may browse

**Status:** proposed, 2026-09-22

## Context

Everything this application knows is reachable from one web client and one agent
endpoint, both of which are ours. A library manager that repairs a library and
then insists on being the only way to hear it has done half a job: the point of
correcting 100,000 files is to play them, on a phone, in a car, through
something somebody else maintains.

**There is no standards body for this and there is one protocol.** Subsonic's
REST API, frozen at 1.16.1 and continued by the community as OpenSubsonic, is
what Navidrome, Gonic, Airsonic and LMS all speak, and it is what Symfonium,
Amperfy, substreamer, Feishin, play:Sub, Tempo and DSub all consume. Its
ossification is the feature: nothing has moved under those clients in years,
which is why there are so many of them.

The alternatives were considered and are not close. DLNA/UPnP has no metadata
model worth the name, no album-artist concept and no client anybody enjoys
using. MPD is a TCP protocol with no artwork and no remote library browsing.
Jellyfin and Plex are servers, not protocols — a third party does not implement
them, it runs them. Ampache's API is Subsonic-shaped with a fraction of the
clients.

So the question is not *which* standard but *what a Subsonic client is told*,
and that question is harder here than it would be anywhere else, because the
entity graph is deliberately not the one the protocol assumes.

**Subsonic browses `MusicFolder → Artist → Album → Song`, where a song is a
file.** ADR 0004 spent its whole argument on not modelling that: `Recording` is
the dedupe anchor, `Track` is a position on a `Release`, `MediaFile` is bytes,
and the flat shape was rejected as the one decision that could not be
retrofitted. Handing that graph to a protocol built on the flat shape is a
lossy projection, and the interesting part of this ADR is which losses are
accepted and which are refused.

## Decision

**Implement OpenSubsonic under `/rest`, as a projection of what already exists,
with no new table, no new column and no new rule.**

### The two browsing modes are the two sources of truth

Subsonic has two ways to browse, and servers usually treat one as legacy. Here
they are not alternatives — they are the two things this application actually
knows, and the whole of `CLAUDE.md` is about not confusing them:

| Subsonic endpoints | answered from | means |
| --- | --- | --- |
| `getMusicFolders`, `getIndexes`, `getMusicDirectory` | **the disk**, via `FileManagerService.ListAsync` | what is there |
| `getArtists`, `getArtist`, `getAlbum`, `getAlbumList2`, `search3` | **the catalogue** | what it means |

That is the file manager's own sentence — *what is there comes from the disk;
what it means comes from the catalogue* — and the protocol happens to have a
separate endpoint family for each. Implementing both is cheaper than
implementing either one well, because neither then needs a fallback: the id3
endpoints serve exactly the releases the passes have attributed, and every file
in the library, attributed or not, is reachable by folder. No synthetic album,
no invented artist, no rule deciding where an unfiled file should appear.

**A folder tree that a client can browse is an output of this application, not
a compromise.** `AlbumFolder` already argues that the grouping is the one thing
about a library that is nearly always right, and a repaired library is one whose
folders say what its catalogue says. A client browsing by folder before the
repair is finished sees the work in progress, which is the truth.

### The mapping

| Subsonic | here | note |
| --- | --- | --- |
| `artist` | `Artist` | `musicBrainzId` and `sortName` are OpenSubsonic fields the row already carries |
| `album` | `Release`, not `ReleaseGroup` | `Track.Position` and `DiscNumber` hang off a release; an album with no track numbers is useless to a client |
| `song` | `MediaFile` | it is what gets streamed; nothing else can be |
| `song.track` / `discNumber` | `Track.Position` / `Track.DiscNumber` via `MediaFile.TrackId` | null until the file is attributed |
| `song.duration` / `bitRate` / `samplingRate` / `channelCount` / `bitDepth` | `MediaFile.Quality` | see below |
| `song.suffix` / `contentType` | derived from `MediaFile.Path` through `FilePreview.Of` | there is no format column, and adding one would duplicate the extension |
| `album.year` | `Release.ReleasedYear` | only the year, as everywhere else — `ReleaseDate` stays partial |
| `coverArt` | `ReleaseCover`, or embedded/folder art | two existing handlers behind one prefixed id; an artist carries `artistImageUrl` instead |
| `getScanStatus` / `startScan` | `LibraryScanService` | free, and starting a scan from a phone is genuinely useful |

**`AudioQuality` turns out to be the Subsonic `song` record.** Codec, sample
rate, channels, bit depth, bitrate and duration — the value object built to
decide which duplicate to keep is, field for field, what a client wants to
display. The consequence runs the other way and is worth stating plainly: **the
probe pass becomes a precondition for a usable client.** A file that has not
been probed streams correctly and has no duration, and a Subsonic client with no
duration has a broken progress bar and no seeking. Nothing in the protocol lets
a server say "unknown"; the field is simply absent.

**Duration, and only duration, falls back past the probe** — to the track length
MusicBrainz prints, then to the recording's. The other four fields do not and
must not: a sample rate or a bitrate is a fact about *this file*, and there is no
second source for it that would not be a guess about the bytes. A length is a
fact about the *music*, which the catalogue does hold, and the cost of being
slightly wrong about it is a progress bar that ends early — against a client with
no seeking at all. So the precondition above is softened for a library the
enrichment pass has been over, and it is not softened for one it has not.

### One song per file, and the duplicates are shown

**Where one `Recording` has five `MediaFile`s, a client sees five songs.** This
is the one place where a projection rule would have been easy to write —
`AudioQuality.Compare` already ranks them and could have picked a representative
— and it is refused.

Subsonic has no field for "these are the same performance in five encodings",
and no client would render one. So the choice is not between showing the truth
and hiding it; it is between showing the library as it is and quietly
suppressing evidence of the defect this application exists to remove. **Dedupe
is a missing pass, not a rendering problem.** Hashing is still the last item on
the original list and a dedupe key is what it would be made of; a `/rest` layer
that papered over duplicates would make the pass that fixes them feel less
necessary, in the client where the owner would otherwise notice them.

The same reasoning, one layer up: nothing here chooses between two folders
holding the same album. Both are listed, because both are there.

### Auth, and the window beside the door

Subsonic authenticates on every request with `u` plus one of `p`, `p=enc:<hex>`,
or `t=md5(password + salt)` with `s`. The token form requires the server to hold
the password **recoverably**. That is the protocol's flaw and no implementation
can design around it; it can only be contained.

**`Fonoteca:SubsonicPassword`, one password, no users.** The application is
single-user by construction — `SingleUserCallerContext` at `Program.cs:73` is
what `u=` would have to resolve against, and it resolves against nothing. All
three forms are accepted, because clients differ in which they send and a client
that cannot log in is a client that is not used.

**Empty setting means `/rest` does not exist**, answering 404, exactly as
`Fonoteca:McpToken` empty makes `/mcp` not exist. The guard is a second
`app.Use(...)` delegate beside `app.Use(LibraryTools.Guard)` at
`Program.cs:305`, with the same shape and for the reason written on that one:
middleware rather than an endpoint filter, so it runs in front of every route in
the group whatever shape those routes take. Here there is a second reason —
`/rest/stream` returns raw bytes and `/rest/ping.view` returns XML, so a refusal
has to be decided before any handler has chosen a response format.

**This ADR does not fix the open `/api`, and must not be read as having done
so.** There is no authentication anywhere in `Fonoteca.Api` today: a grep for
`UseAuthentication`, `UseAuthorization`, `RequireAuthorization`,
`AddAuthentication` and `[Authorize]` across `apps/api/src` returns nothing, and
`/mcp` guarding itself is stated in `.env.example` as *every `/api` route stays
as open as the port*. That is defensible for a process listening on a
workstation. It stops being defensible the first time somebody forwards the port
so a phone can reach `/rest`, which is the entire purpose of this work.

So: **an authenticated `/rest` beside an unauthenticated `/api` is a front door
next to an open window**, and the fix — a reverse proxy, or auth on `/api` — is
a separate decision that should be taken deliberately rather than discovered.
What this ADR owes it is the warning, in `.env.example`, on the setting that
turns `/rest` on.

### The wire format is the only real work

Subsonic answers in a `subsonic-response` envelope in **both XML and JSON**, and
`f=xml` is the default — a client that omits `f` gets XML. The two are not
mechanically related: XML puts scalars in attributes and children in elements,
JSON puts everything in properties, and singleton-versus-array differs between
them.

So: `ISubsonicWriter` with `Attr(name, value)` and `Child(name, …)`, and two
implementations over `Utf8JsonWriter` and `XmlWriter`. This is the rare
interface that earns itself on the day it is written — it has two
implementations, not one, and the alternatives are reflection over response
records or writing every response twice.

It also means nothing here passes through the configured `System.Text.Json`
options, so `NumberHandling = Strict` and the OpenAPI generator are both
untouched. The route group takes `.ExcludeFromDescription()`, the one precedent
being `MapMcp` on the line below the guard — without it every build rewrites
`openapi.json`, regenerates the TypeScript client and turns `gen:api:check` red.

### Ids

Subsonic ids are opaque strings, which is the one place the protocol's age
helps: typed `Guid`s go out prefixed — `ar-`, `al-`, `tr-` — because
`getCoverArt` takes a single id that may be any of the three and has to be
routed to one of three existing handlers.

Folder-mode ids are `fo-` plus the Base64Url of the library-relative path:
self-describing, stable, URL-safe, no lookup table and no collision. A hash
would need a reverse index, and a raw path would repeat the lesson
`DomainEvent.SubjectId` already paid for — a library path is up to 4096 bytes
and things that carry them have to be sized for it.

### Streaming is the existing handler, called directly

`/rest/stream` and `/rest/download` resolve a `MediaFileId` to its `Path` and
hand it to `FileEndpoints.GetContent`, which already does
`TypedResults.PhysicalFile(..., enableRangeProcessing: true)` — so `Range`,
`If-Range`, `206` and `Last-Modified` are the framework's, and
`FileSystemAudioFileStore.AbsolutePathFor`, `EnsureNoLinkedDirectory`, the
`FilePreview` media-type allowlist and `nosniff` all come with it unchanged.

**This is the `/mcp` rule and it matters more here.** Every tool is an existing
handler called directly, `internal` rather than `private` for that reason, so no
second copy of a rule can drift. The rule that would drift in this case is the
one that decides whether a path escapes the library root, and *lexical
containment is not containment* is a lesson this repository has already paid
for once.

`IAudioFileStore.OpenRangeAsync` exists, throws, and has no caller. It is not
the thing to reach for; the framework already does ranging.

### What is deliberately absent

**No transcoding.** `maxBitRate` and `format` are accepted and ignored; the
bytes go out raw. ffmpeg is already a dependency, so the upgrade is a pipe when
somebody is measured to need it — over a LAN, FLAC is what the clients want
anyway.

**No star, no rating, no play count.** There is no user table, no listen table
and nothing to hang them on, and the tempting shortcut is wrong: `Artist.Followed`
and `ReleaseGroup.Monitored` are standing intent about *acquisition*, and a
listener's star is standing intent about *listening*. They are different facts,
and folding them would lose the ability to report on either — rule 4, applied to
a new caller. If starring is wanted it gets its own table, in its own change.

**`scrobble` is accepted and stored nowhere**, which is within the protocol —
the endpoint exists so a client can tell a server about a play, and a server may
do nothing with it. The alternative, refusing it, makes well-behaved clients log
errors on every track.

**`getPlaylists` returns an empty list.** There is no queue and no playlist
anywhere — `PlaybackProvider` says so in its own words, and nothing is persisted
about playback at all. Writable playlists are a table and a decision, not part of
this.

Also absent: file-based *writing*, video, podcasts, jukebox, chat, bookmarks,
share, lyrics.

### Scope

One route group, one writer, one guard, in `Fonoteca.Api/Subsonic/`:
`ping`, `getLicense`, `getOpenSubsonicExtensions`, `getUser`, `getMusicFolders`,
`getIndexes`, `getMusicDirectory`, `getArtists`, `getArtist`, `getAlbum`,
`getAlbumList2`, `getSong`, `search3`, `stream`, `download`, `getCoverArt`,
`getPlaylists`, `scrobble`, `getScanStatus`, `startScan` — each at both `/rest/x`
and `/rest/x.view`, because half the client ecosystem still sends the suffix.

`getUser` is there because several clients call it at login to decide which
buttons to draw, and a server that does not answer reads to them as broken
rather than as limited. It states every role, and the three that are true are
the three this surface has: stream, download, cover art.

**Everything else under `/rest` answers error 0 naming the method**, through a
catch-all the literal routes outrank. A bare 404 is the failure mode that turns
one missing feature into an unusable server, because a client that gets an HTTP
error stops rather than hiding a screen.

**Parameters come from the query string only.** The protocol also allows a
form-encoded POST and one or two clients can be configured to use it; reading it
would make the credentials check async for the sake of an option nobody turns on
by default. A POST to a method that exists is a 405, which is the one remaining
place this surface answers outside the envelope.

Tests in `Fonoteca.Integration.Tests`: the envelope in both formats; the guard
accepting all three auth forms, refusing a wrong one inside a 200, and 404ing
when unset; a `Range` request against `stream`; a folder-mode listing reaching a
file the catalogue has not attributed; a catalogued file with no album at all,
which is the state most of a library is in and the one every navigation off the
media file is null for; `parent` differing between the two browsing modes; a
JSONP callback that is not an identifier; and one asserting the group is absent
from the generated document.

**Plus one thin case per method.** Most have no assertion worth making beyond
"it answered", but the projections behind them are where an EF query that cannot
be translated hides — and an untranslatable query compiles, ships, and throws the
first time somebody opens that screen.

## Why

**Because the projection is where a second opinion would grow.** The reason this
is a projection with no rules of its own is the reason `/mcp` has none: a second
surface with its own idea of which file represents a track, or where an unfiled
file belongs, is drift that nobody sees reported as a bug. Every question
`/rest` answers is a question some existing handler already answers, and the
only new code is the shape it goes out in.

**Because the protocol's two browsing modes already encode the distinction this
application is built on.** Choosing one would have forced a fallback rule — some
way of inventing an album for a file that has none — and that rule would have
been a fourth matching pass whose worklist is exactly the files the other three
refused. The by-hand screens exist for that, and they are ours, not a phone
client's.

**Because the duplicates are the product.** Showing five encodings of one
recording as five songs is not a defect in the projection. It is the library,
stated to the one audience that will notice.

## Consequences

**The probe pass moves from useful to required.** Anyone pointing a client at
this before probing gets a library with no durations. That is worth saying on
the setting and worth saying on the screen.

**`getCoverArt` is one endpoint over two existing handlers** — the release cover
(`ReleaseId`, with an ETag of `SavedUtc.UtcTicks`) and a path's own picture, the
file's embedded art or the folder's `cover.jpg`. The prefixed id is what routes
between them, and the one-week negative-cache window on `ReleaseCover` now
expires under load from clients that request art per song rather than per album.

**An artist is not among them, deliberately.** `Artist.PortraitUrl` is already a
full URL on whichever provider's CDN answered, and OpenSubsonic has
`artistImageUrl` for exactly that — so the artist element carries the URL and no
`coverArt` id at all, nothing is proxied, and a client that cannot reach the CDN
draws the same monogram the web client does.

**An audio format the `FilePreview` allowlist does not name is served as an
attachment.** `ape`, `wv`, `dsf` and `wma` go out as `application/octet-stream`
with a `Content-Disposition`, because that is what the allowlist does with
anything it will not vouch for, and most clients will not play that. It is the
honest consequence of reusing the file path rather than writing a second one —
the fix, if a library needs it, is a line in `FilePreview` where every other
media-type decision already lives, not a special case here.

**The password travels in the query string, on every request.** The protocol has
no other place to put it, so it lands in the access log verbatim, and in
Development the framework's own request logging prints it. Nothing here can fix
that; what it changes is that the log is now a credential store.

**There is no genre on a release or a track.** `Artist.Genres` is a string on
the artist and that is all, so `getGenres` is not offered and `song.genre` is
absent. Clients that browse by genre will show nothing rather than something
wrong.

**`search3` is a database query, and that is fine.** The note that search needs
a Solr index is about *MusicBrainz* search, behind the by-hand album screen. A
local search over `Artists`, `Releases` and `MediaFiles.Path` is an `ILIKE` and
owes nothing to a mirror.

**`MatchingSubject.tsx` should stop pointing at an endpoint that does not
exist.** `AUDIO_PATH = '/api/library/audio'` is hardcoded there and 404s, which
its own comment admits. It is not part of this work, but it is the same
question — what URL plays a file — and the answer is `/api/files/content`.

**A password now exists in configuration that is recoverable by construction.**
Not a hash. Anyone with read access to the environment has it, and it is the
credential to a surface that serves the whole library. That is the protocol's
design, and the containment is that it is *one* password, for *one* surface,
that does not exist when unset.

## When to revisit

**When hashing lands.** A dedupe key changes what "five files, one recording"
means, and it may then be right for `/rest` to show one song — because by then
the duplicates will have been dealt with rather than hidden.

**When somebody streams over cellular.** That is the measurement that buys
transcoding, and not before.

**When a client is measured to need the `apiKey` extension.** It replaces
`u`/`p`/`t`/`s` with a single key and would let the recoverable password go, at
the cost of clients that do not implement it. Today most do not.

**If `/api` ever gains authentication.** The guard here would be the second
scheme in the application, and two schemes is the point at which one of them
should stop being a hand-written middleware delegate.
