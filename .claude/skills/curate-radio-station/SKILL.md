---
name: curate-radio-station
description: Adding or fixing a curated internet radio station in Fonoteca - finding a verified HTTPS stream and a proper square logo PNG, for the shipped catalogue the server offers (RadioCatalogue/) or a saved station's logo in the library's Radio/. Use when asked to add stations, fix station logos, or curate radio.
---

# Curating a radio station

Since 2026-11-04 a saved station is `Radio/<name>.xspf` plus a hand-picked `Radio/<name>.png`
beside it (`RadioStationFile.LogoPathFor`). Nothing looks logos up. Write no `<image>`.
Curated stations are **offered, never saved**: they go in `apps/api/src/Fonoteca.Api/RadioCatalogue/`
(XSPF + PNG), listed by `getRadioCatalogue`. Never write them into the library's `Radio/`.

## Stream
- Read `icy-br`/`content-type` with `curl -sL -m 8 -r 0-1 -D - -o /dev/null <url>`.
- Prefer HTTPS and the highest bitrate. NPO: `https://icecast.omroep.nl/<mount>-bb-mp3` is
  192 kbps; `-bb-aac` is only 64. StreamTheWorld: use
  `https://playerservices.streamtheworld.com/api/livestream-redirect/<MOUNT>.mp3`, not a numbered node.

## Logo, best source first
1. A vector on Wikimedia Commons (search with the API `list=search&srnamespace=6`, then
   `prop=imageinfo&iiprop=url`), or the brand's own SVG on its site. Send a User-Agent.
   Space requests out: a burst of about 15 API calls got HTML back instead of JSON.
2. The station site's web-app manifest icons (512 px), or the site's own logo image.
   An apple-touch-icon (180 px) or favicon is the last resort. Those are what the user called terrible.
3. Never a potrace `safari-pinned-tab.svg`: it is a tracing of the small icon, monochrome.

What the user wants: a **transparent background** always (an app icon on an opaque square is
refused; check every tile's corner alpha is 0), the **full official logo**, not a crop of it (NPO station diamonds without
the grey "npo" diamond were rejected), the brand's **current** mark from its own site over an old
Commons file, and the station's own brand (Sky Radio's logo, not a channel illustration).

## Rendering, ffmpeg only (no PIL, numpy or rsvg-convert here; ffmpeg has librsvg)
- Rasterize: `ffmpeg -width W -height H -i x.svg -frames:v 1 -pix_fmt rgba out.png`.
  When the SVG has only a viewBox, give both dimensions in its ratio, or it renders at 100 px high.
  A site favicon.svg often nests an inner `<svg viewBox>` in an outer one without one: extract the
  inner `<svg>`, write the size into its `width`/`height`, and render it without flags.
- Trim the transparent margin with a raw alpha scan: `format=rgba,alphaextract` to
  `-f rawvideo -pix_fmt gray`, then the first and last non-zero rows and columns in Python
  (`ffprobe -of csv=p=0` for the size). Never `cropdetect`: it shaves the soft edges and cut the
  top off the RCO crown's tallest spike, which passed my own eye check twice.
- Square it: `format=rgba,pad='max(iw,ih)*1.08':'max(iw,ih)*1.08':(ow-iw)/2:(oh-ih)/2:color=0x00000000,scale=1024:1024:flags=lanczos`.
  Put `format=rgba` first: a palette (pal8) source otherwise loses its transparency to black bands.
- Compare a tile with its source only after trimming both to the drawing, at the same scale.
  Padded tiles scaled side by side look like different drawings when they are not, and vice versa;
  compare the bounding-box ratio as a number.
- Before writing, check every logo on a contact sheet (`hstack`) over grey and dark backgrounds.

## Landing
- A changed catalogue PNG is not re-copied into the running API's `bin/Debug/net10.0/RadioCatalogue/`
  by `dotnet watch`; copy it there too (served from disk per request, no restart).
- Station names have spaces: build any per-file patch with `while IFS= read -r f`.
- A scratch worktree's copy of this skill is older than the repo's; leave `.claude/` out of a
  landing patch, or it reverts this file.

## Many stations at once
- `build_stations.py <research.json> <out> <work>` (beside this file): per entry it re-checks the
  stream, renders the logo (SVG or raster) with the raw alpha trim, refuses an opaque background
  unless the entry says `"solidMark": true` (a brand whose mark IS a coloured square: Radio France,
  ICI), and writes the XSPF. Research JSON: `[{name, stream, home, logo, solidMark?}]`.
- `site_logos.py <research.json>...` fills empty `logo`s from each home page's header (Brightspot's
  `?url=` original) or its JSON-LD publisher logo. Guesses: about one in four was wrong (app icons,
  co-brand banners, a broken Commons render), so look at every tile on a labelled contact sheet
  (`tile=8x6`) before copying anything in.
- Scraper agents cannot write outside their memory dir: have them return the JSON in their reply.
- Commons answers 429 to bursts: one search per 4 s with `generator=search&gsrsearch=<q> filetype:drawing`.
- A web channel with no mark of its own (FIP Rock, La Baroque) takes its parent's logo.

## Country, languages, genres (since 2026-11-04, catalogue v2)
- Every station, catalogue and saved, carries track `<meta rel="urn:fonoteca:radio:country|language|genre">`,
  one element per value: ISO 3166-1 alpha-2, ISO 639-1, MusicBrainz genre spelling (lowercase) plus `news`/`talk`.
- A new station must get them: add it to `shelve.py`'s table and run it on `RadioCatalogue/` (and `Radio/`).
- Streams must be https: `TheCatalogueIsOfferedAndASavedStationTakesItsLogo` fails on any http one.
  OPB was dropped for having none.
