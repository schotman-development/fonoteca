"""Turn research JSON into catalogue entries: verified stream, transparent square logo, XSPF.

usage: python3 -I build_stations.py <research.json> <out dir> <work dir>
Needs ffmpeg/ffprobe on PATH (source ~/.local/opt/env.sh). Prints one line per station.
"""
import json, os, re, subprocess, sys, time, urllib.request
from xml.sax.saxutils import escape

UA_WIKI = "Fonoteca/0.1 (self-hosted radio curation; https://example.invalid)"
UA_WEB = "Mozilla/5.0 (X11; Linux x86_64) Fonoteca/0.1"
TILE = 1024
MARGIN = 1.08


def run(*args, **kw):
    return subprocess.run(args, capture_output=True, check=False, **kw)


def fetch(url, path):
    ua = UA_WIKI if "wikimedia.org" in url else UA_WEB
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": ua}), timeout=30) as r:
        open(path, "wb").write(r.read(20_000_000))
    if "wikimedia.org" in url:
        time.sleep(1.5)


def stream_ok(url):
    out = run("curl", "-sL", "-m", "10", "-r", "0-1", "-D", "-", "-o", "/dev/null", url).stdout.decode("latin-1")
    types = re.findall(r"(?im)^content-type:\s*([^\r\n;]+)", out)
    return bool(types) and (types[-1].startswith("audio/") or "mpegurl" in types[-1]), (types[-1] if types else "none")


def size_of(path):
    w, h = run("ffprobe", "-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", path).stdout.decode().strip().split(",")[:2]
    return int(w), int(h)


def svg_ratio(text):
    box = re.search(r'viewBox="\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)', text)
    if box:
        return float(box.group(1)) / float(box.group(2))
    w = re.search(r'<svg[^>]*\swidth="([\d.]+)', text)
    h = re.search(r'<svg[^>]*\sheight="([\d.]+)', text)
    return float(w.group(1)) / float(h.group(1)) if w and h else 1.0


def render_svg(src, dst):
    text = open(src, encoding="utf-8", errors="replace").read()
    inner = re.search(r"<svg[^>]*viewBox[^>]*>.*?</svg>", text[text.find("<svg") + 4:], re.S)
    if inner and not re.search(r"<svg[^>]*viewBox", text[: text.find(">", text.find("<svg")) + 1]):
        text = inner.group(0)
    ratio = svg_ratio(text)
    w, h = (2400, round(2400 / ratio)) if ratio >= 1 else (round(2400 * ratio), 2400)
    start = text.find("<svg")
    end = text.find(">", start)
    opening = re.sub(r'\s(width|height)\s*=\s*("[^"]*"|\'[^\']*\')', "", text[start:end])
    text = text[:start] + opening.replace("<svg", f'<svg width="{w}" height="{h}"', 1) + text[end:]
    fixed = src + ".sized.svg"
    open(fixed, "w", encoding="utf-8").write(text)
    r = run("ffmpeg", "-v", "error", "-y", "-i", fixed, "-frames:v", "1", "-pix_fmt", "rgba", dst)
    if r.returncode:
        raise RuntimeError(r.stderr.decode()[:200])


def alpha_box(path):
    w, h = size_of(path)
    raw = run("ffmpeg", "-v", "error", "-i", path, "-vf", "format=rgba,alphaextract", "-f", "rawvideo", "-pix_fmt", "gray", "-").stdout
    xs, ys = [], []
    for y in range(h):
        row = raw[y * w:(y + 1) * w]
        hit = [x for x, a in enumerate(row) if a > 0]
        if hit:
            ys.append(y)
            xs += [hit[0], hit[-1]]
    if not ys:
        raise RuntimeError("fully transparent")
    corner = [raw[0], raw[w - 1], raw[(h - 1) * w], raw[h * w - 1]]
    return (max(xs) - min(xs) + 1, max(ys) - min(ys) + 1, min(xs), min(ys)), corner


def tile(raster, dst, solid_mark=False):
    (cw, ch, cx, cy), corner = alpha_box(raster)
    if not solid_mark and max(corner) == 255 and (cw, ch) == size_of(raster):
        raise RuntimeError("opaque background")
    vf = (f"format=rgba,crop={cw}:{ch}:{cx}:{cy},"
          f"pad='max(iw,ih)*{MARGIN}':'max(iw,ih)*{MARGIN}':(ow-iw)/2:(oh-ih)/2:color=0x00000000,"
          f"scale={TILE}:{TILE}:flags=lanczos")
    r = run("ffmpeg", "-v", "error", "-y", "-i", raster, "-vf", vf, "-frames:v", "1", dst)
    if r.returncode:
        raise RuntimeError(r.stderr.decode()[:200])


def xspf(name, stream, home):
    n, s, h = escape(name), escape(stream), escape(home or "")
    info = f"\n      <info>{h}</info>" if home else ""
    return (f'<?xml version="1.0" encoding="utf-8"?>\n<playlist version="1" xmlns="http://xspf.org/ns/0/">\n'
            f"  <title>{n}</title>\n  <trackList>\n    <track>\n      <location>{s}</location>\n"
            f"      <title>{n}</title>{info}\n    </track>\n  </trackList>\n</playlist>\n")


def main():
    research, out, work = sys.argv[1:4]
    os.makedirs(out, exist_ok=True)
    os.makedirs(work, exist_ok=True)
    for entry in json.load(open(research)):
        name = entry["name"].replace("/", "-").strip()
        problems = []
        ok, ctype = stream_ok(entry["stream"]) if entry.get("stream") else (False, "no stream")
        if not ok:
            problems.append(f"stream {ctype}")
        logo = entry.get("logo")
        if logo:
            try:
                ext = ".svg" if logo.split("?")[0].lower().endswith(".svg") else ".img"
                src = os.path.join(work, name + ext)
                fetch(logo, src)
                raster = src
                if ext == ".svg":
                    raster = os.path.join(work, name + ".raster.png")
                    render_svg(src, raster)
                tile(raster, os.path.join(out, name + ".png"), entry.get("solidMark", False))
            except Exception as error:  # report every failure per station, never stop the batch
                problems.append(f"logo {error}")
        else:
            problems.append("no logo")
        if ok:
            open(os.path.join(out, name + ".xspf"), "w", encoding="utf-8").write(xspf(entry["name"].strip(), entry["stream"], entry.get("home")))
        print(f"{'OK ' if not problems else 'BAD'} {entry.get('country','')} {name}: {'; '.join(problems) or ctype}")


if __name__ == "__main__":
    main()
