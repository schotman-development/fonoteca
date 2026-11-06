# Usage: mbinfo.py <work mbid> ...: each work's disambiguation, attributes, what it arranges or revises, and how many recordings MusicBrainz links to it.
import sys
from mbhttp import get
for mbid in sys.argv[1:]:
    work = get(f'work/{mbid}?inc=work-rels+artist-rels')
    recordings = get(f'recording?work={mbid}&limit=1')['recording-count']
    relations = work.get('relations', [])
    links = [f"{r['type']}{'<' if r['direction'] == 'backward' else '>'}{r['work']['title']}" for r in relations
             if 'work' in r]
    people = sorted({f"{r['type']}:{r['artist']['name']}" for r in relations if 'artist' in r})
    attributes = [f"{a['type']}={a['value']}" for a in work.get('attributes', [])]
    print(f"{mbid} {work['title']} [{work.get('disambiguation','')}] recordings={recordings} {attributes} {people} {links[:8]}")
