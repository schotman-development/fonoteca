"""Write each station's country, languages and genres into its XSPF as track <meta> elements.

usage: python3 -I shelve.py <folder>...   (stations not in SHELVES are reported, not touched)
"""
import glob, os, re, sys
from xml.sax.saxutils import escape

NEWS = ["news", "talk"]
SHELVES = {
    # The Netherlands
    "NPO Radio 1": ("NL", ["nl"], NEWS), "NPO Radio 2": ("NL", ["nl"], ["pop", "rock"]),
    "NPO 3FM": ("NL", ["nl"], ["pop", "rock", "alternative rock"]), "NPO Klassiek": ("NL", ["nl"], ["classical"]),
    "NPO Radio 5": ("NL", ["nl"], ["pop"]), "NPO FunX": ("NL", ["nl"], ["hip hop", "r&b"]),
    "Arrow Classic Rock": ("NL", ["nl"], ["classic rock", "rock"]), "Arrow Bluesbox": ("NL", ["nl"], ["blues"]),
    "Grolloo Radio": ("NL", ["nl"], ["blues", "rock"]), "Koninklijk Concertgebouworkest": ("NL", ["nl"], ["classical"]),
    "Sky Radio 80's Hits": ("NL", ["nl"], ["pop"]),
    # Germany
    "Deutschlandfunk": ("DE", ["de"], NEWS), "Deutschlandfunk Kultur": ("DE", ["de"], ["talk", "classical"]),
    "Deutschlandfunk Nova": ("DE", ["de"], ["talk", "pop"]), "1LIVE": ("DE", ["de"], ["pop", "hip hop"]),
    "WDR 2": ("DE", ["de"], ["pop", "news"]), "WDR 3": ("DE", ["de"], ["classical", "jazz"]),
    "Bayern 1": ("DE", ["de"], ["pop"]), "Bayern 3": ("DE", ["de"], ["pop"]), "BR-Klassik": ("DE", ["de"], ["classical"]),
    "SWR1": ("DE", ["de"], ["pop", "rock"]), "SWR3": ("DE", ["de"], ["pop"]), "NDR 2": ("DE", ["de"], ["pop"]),
    "NDR Kultur": ("DE", ["de"], ["classical"]), "hr3": ("DE", ["de"], ["pop"]),
    "radioeins": ("DE", ["de"], ["rock", "alternative rock"]), "MDR JUMP": ("DE", ["de"], ["pop"]),
    "Bremen Vier": ("DE", ["de"], ["pop"]),
    # France
    "France Inter": ("FR", ["fr"], NEWS), "franceinfo": ("FR", ["fr"], ["news"]), "France Culture": ("FR", ["fr"], ["talk"]),
    "France Musique": ("FR", ["fr"], ["classical"]), "Mouv'": ("FR", ["fr"], ["hip hop", "r&b"]),
    "FIP": ("FR", ["fr"], ["jazz", "rock", "world music"]), "FIP Rock": ("FR", ["fr"], ["rock"]),
    "FIP Jazz": ("FR", ["fr"], ["jazz"]), "FIP Groove": ("FR", ["fr"], ["funk", "soul"]),
    "FIP Monde": ("FR", ["fr"], ["world music"]), "FIP Reggae": ("FR", ["fr"], ["reggae"]),
    "FIP Electro": ("FR", ["fr"], ["electronic"]), "FIP Nouveautés": ("FR", ["fr"], ["pop", "rock"]),
    "FIP Pop": ("FR", ["fr"], ["pop"]), "FIP Metal": ("FR", ["fr"], ["metal"]), "FIP Hip Hop": ("FR", ["fr"], ["hip hop"]),
    "France Musique Classique Easy": ("FR", ["fr"], ["classical"]), "France Musique Classique Plus": ("FR", ["fr"], ["classical"]),
    "France Musique Concerts Radio France": ("FR", ["fr"], ["classical"]), "La Baroque": ("FR", ["fr"], ["baroque", "classical"]),
    "La B.O.": ("FR", ["fr"], ["soundtrack"]), "La Contemporaine": ("FR", ["fr"], ["contemporary classical"]),
    "La Jazz": ("FR", ["fr"], ["jazz"]), "Ocora Musiques du monde": ("FR", ["fr"], ["world music"]),
    "Opéra": ("FR", ["fr"], ["opera"]), "Piano Zen": ("FR", ["fr"], ["classical"]),
    # Spain
    "Radio Nacional": ("ES", ["es"], NEWS), "Radio Clásica": ("ES", ["es"], ["classical"]),
    "Radio 3": ("ES", ["es"], ["pop", "rock", "alternative rock"]), "Radio 5": ("ES", ["es"], ["news"]),
    "Ràdio 4": ("ES", ["ca"], NEWS), "Radio Exterior": ("ES", ["es"], ["news"]),
    # Italy
    "Rai Radio 1": ("IT", ["it"], NEWS), "Rai Radio 2": ("IT", ["it"], ["pop"]), "Rai Radio 3": ("IT", ["it"], ["classical", "talk"]),
    "Rai Isoradio": ("IT", ["it"], ["news"]), "Rai Radio Classica": ("IT", ["it"], ["classical"]),
    "Rai Radio Tutta Italiana": ("IT", ["it"], ["pop"]), "Rai Radio Techetè": ("IT", ["it"], ["talk"]),
    "Rai Radio Live": ("IT", ["it"], ["pop"]), "Rai Radio Kids": ("IT", ["it"], ["children's music"]),
    # Canada
    "CBC Music": ("CA", ["en"], ["pop", "rock"]), "ICI Première": ("CA", ["fr"], NEWS), "ICI Musique": ("CA", ["fr"], ["pop"]),
    # USA
    "IPR Studio One": ("US", ["en"], ["rock", "indie rock"]),
}
US_NEWS = ["Alabama Public Radio", "Alaska Public Media", "Boise State Public Radio", "Delaware Public Media", "GPB Radio",
           "Hawaii Public Radio", "KJZZ", "KNPR", "KQED", "KUAR", "Kansas Public Radio", "MPR News", "Maine Public",
           "Michigan Public", "Mississippi Public Broadcasting", "Montana Public Radio", "Nebraska Public Media", "NHPR", "OPB",
           "St. Louis Public Radio", "Vermont Public", "WBEZ Chicago", "WFPL", "WHYY", "WNYC", "WPLN", "WUSF Public Media", "WYPR",
           "West Virginia Public Broadcasting", "Wyoming Public Media"]
SHELVES.update({name: ("US", ["en"], NEWS) for name in US_NEWS})
RELS = ("urn:fonoteca:radio:country", "urn:fonoteca:radio:language", "urn:fonoteca:radio:genre")

for folder in sys.argv[1:]:
    for path in sorted(glob.glob(os.path.join(folder, "*.xspf"))):
        name = os.path.basename(path)[:-5]
        if name not in SHELVES:
            print("not shelved:", path)
            continue
        country, languages, genres = SHELVES[name]
        text = open(path, encoding="utf-8").read()
        text = re.sub(r'\n\s*<meta rel="urn:fonoteca:radio:[a-z]+">[^<]*</meta>', "", text)
        values = [(RELS[0], country)] + [(RELS[1], code) for code in languages] + [(RELS[2], genre) for genre in genres]
        meta = "".join(f'\n      <meta rel="{rel}">{escape(value)}</meta>' for rel, value in values)
        text = text.replace("\n    </track>", meta + "\n    </track>", 1)
        open(path, "w", encoding="utf-8").write(text)
