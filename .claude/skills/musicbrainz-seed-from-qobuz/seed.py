import json, html, sys
src, url, out = sys.argv[1:4]
d = json.load(open(src)); items = d["tracks"]["items"]
assert len(items) == d["tracks_count"] and d["media_count"] == 1
year, month, day = d["release_date_original"].split("-")

def name(t):
    title = t["title"].strip()
    # MusicBrainz ETI style: descriptive extra title information in lowercase.
    return f'{title} ({t["version"].lower()})' if t.get("version") else title

fields = [
    ("name", d["title"].strip()),
    ("type", "Album"), ("type", "Compilation"),
    ("status", "official"), ("packaging", "None"),
    ("language", "eng"), ("script", "Latn"),
    ("barcode", d["upc"]),
    ("events.0.date.year", year), ("events.0.date.month", str(int(month))), ("events.0.date.day", str(int(day))),
    ("events.0.country", "XW"),
    ("labels.0.name", d["label"]["name"]),
    ("artist_credit.names.0.mbid", "fbe054ec-a143-4101-9e9e-64abc5ff5ac9"),
    ("artist_credit.names.0.name", d["artist"]["name"]),
    ("mediums.0.format", "Digital Media"),
    ("urls.0.url", url), ("urls.0.link_type", "74"),
    ("urls.1.url", url), ("urls.1.link_type", "980"),
    ("edit_note", f"Seeded from Qobuz: {url}\n"
                  f"Title, artist, label, barcode, date and the {len(items)}-track list with lengths are from Qobuz's album data. "
                  f"Qobuz copyright line: {d.get('copyright')}"),
]
for i, t in enumerate(items):
    assert t["track_number"] == i + 1 and t["performer"]["name"] == "Nat King Cole"
    fields += [(f"mediums.0.track.{i}.name", name(t)),
               (f"mediums.0.track.{i}.number", str(i + 1)),
               (f"mediums.0.track.{i}.length", str(t["duration"] * 1000))]

inputs = "\n".join(f'<input type="hidden" name="{html.escape(k)}" value="{html.escape(v)}">' for k, v in fields)
rows = "\n".join(f'<tr><td>{i+1}</td><td>{html.escape(name(t))}</td><td>{t["duration"]//60}:{t["duration"]%60:02d}</td><td>{t["isrc"]}</td></tr>' for i, t in enumerate(items))
title, artist = html.escape(d["title"].strip()), html.escape(d["artist"]["name"])
page = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Seed {title}</title>
<style>
:root {{ color-scheme: light dark; --bg: #fff; --fg: #1a1a1a; --muted: #666; --line: #ddd; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg: #161616; --fg: #eee; --muted: #999; --line: #333; }} }}
body {{ background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, sans-serif; margin: 0 auto; max-width: 760px; padding: 16px; }}
button {{ font: inherit; padding: 10px 18px; cursor: pointer; }}
table {{ border-collapse: collapse; width: 100%; font-size: 13px; }}
td {{ border-top: 1px solid var(--line); padding: 3px 6px; vertical-align: top; }}
td:first-child, td:nth-child(3) {{ text-align: right; white-space: nowrap; }}
td:last-child {{ color: var(--muted); font-family: ui-monospace, monospace; }}
p {{ color: var(--muted); }}
</style></head><body>
<h1>{title} · {artist}</h1>
<p>{html.escape(d["label"]["name"])} · {d["release_date_original"]} · barcode {d["upc"]} · {len(items)} tracks, Digital Media · Official, Album + Compilation.
Opens MusicBrainz's release editor prefilled, under your own account; nothing is saved until you press Enter edit there.</p>
<form method="post" action="https://musicbrainz.org/release/add" target="_blank">
{inputs}
<button type="submit">Open in MusicBrainz</button>
</form>
<h2>Track list</h2>
<table>{rows}</table>
</body></html>
"""
open(out, "w", encoding="utf-8").write(page)
print(len(fields), "fields")
