"""Fill in each logo-less station's logo from its home page: the header's logo image, else the
publisher logo its JSON-LD names. Writes the research file back with "logo" set and
"logoKind": "site-guess" so every guess is looked at on a contact sheet before it ships.

usage: python3 -I site_logos.py <research.json>...
"""
import json, re, sys, urllib.parse, urllib.request

UA = "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0"
IMAGE = re.compile(r'(?:src|href|data-src|srcset|content)\s*=\s*["\']([^"\'\s]+?\.(?:svg|png|webp)(?:\?[^"\'\s]*)?)', re.I)
NOT_OURS = re.compile(r"favicon|apple-touch|sponsor|underwrit|npr-logo|npr_logo|pbs-logo|pbs_logo|bbc-logo|apm-logo|"
                      r"pri-logo|app-?store|google-?play|footer|icon-|social|facebook|twitter|instagram|youtube", re.I)
HEADER_SHARE = 0.35


def page(url):
    request = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "text/html"})
    with urllib.request.urlopen(request, timeout=20) as response:
        return response.geturl(), response.read(3_000_000).decode("utf-8", "replace")


def original(url):
    """Brightspot resizes through ?url=<original>; the original is the full-size file."""
    query = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)
    return query["url"][0] if "url" in query else url


def header_logo(base, html):
    header = html[: int(len(html) * HEADER_SHARE)]
    for match in IMAGE.finditer(header):
        url = original(urllib.parse.urljoin(base, match.group(1).replace("&amp;", "&")))
        context = header[max(0, match.start() - 400):match.end() + 100].lower()
        if "logo" in (url.lower() + context) and not NOT_OURS.search(url):
            return url
    return None


def publisher_logo(html):
    for block in re.findall(r'<script[^>]*application/ld\+json[^>]*>(.*?)</script>', html, re.S):
        found = re.search(r'"(?:logo|image)"\s*:\s*(?:\{[^}]*?"url"\s*:\s*)?"([^"]+\.(?:svg|png|webp)[^"]*)"', block)
        if found and not NOT_OURS.search(found.group(1)):
            return found.group(1)
    return None


for path in sys.argv[1:]:
    entries = json.load(open(path))
    for entry in entries:
        if entry.get("logo") or not entry.get("home"):
            continue
        try:
            base, html = page(entry["home"])
            logo = header_logo(base, html) or publisher_logo(html)
        except Exception as error:  # one unreachable site must not end the scan
            logo, base = None, str(error)
        if logo:
            entry["logo"], entry["logoKind"] = logo, "site-guess"
        print(f"{'+' if logo else '-'} {entry['name']}: {logo or base}"[:200])
    json.dump(entries, open(path, "w"), ensure_ascii=False, indent=1)
