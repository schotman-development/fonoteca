# Usage: mbtree.py <work mbid> [depth]: the work's parts, recursively, one MusicBrainz request per work.
import sys
from mbhttp import get

def show(mbid, depth, indent=''):
    work = get(f'work/{mbid}?inc=work-rels')
    parts = sorted((r for r in work.get('relations', []) if r['type'] == 'parts' and r['direction'] == 'forward'),
                   key=lambda r: r.get('ordering-key') or 0)
    print(f"{indent}{mbid} [{work.get('type')}] {work['title']}" + (f' ({len(parts)} parts)' if parts else ''))
    for part in parts:
        if depth > 1:
            show(part['work']['id'], depth - 1, indent + '  ')
        else:
            print(f"{indent}  {part['work']['id']} {part.get('ordering-key')}. {part['work']['title']}")

show(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 1)
