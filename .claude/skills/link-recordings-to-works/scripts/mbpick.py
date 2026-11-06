# Usage: mbpick.py "<lucene query>" [how many]: the best-scoring matches, each with its parts and whether it is an arrangement.
import sys, urllib.parse
from mbhttp import get
query, count = sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 2
works = get('work?limit=10&query=' + urllib.parse.quote(query))['works']
print(f'== {query}')
for found in works[:count]:
    work = get(f"work/{found['id']}?inc=work-rels")
    relations = work.get('relations', [])
    arranged_from = [r['work']['title'] for r in relations if r['type'] == 'arrangement' and r['direction'] == 'backward']
    part_of = [r['work']['title'] for r in relations if r['type'] == 'parts' and r['direction'] == 'backward']
    parts = sorted((r for r in relations if r['type'] == 'parts' and r['direction'] == 'forward'), key=lambda r: r.get('ordering-key') or 0)
    print(f"  {found['score']} {work['id']} [{work.get('type')}] {work['title']}"
          + (f' | ARRANGEMENT of {arranged_from}' if arranged_from else '') + (f' | part of {part_of}' if part_of else ''))
    for part in parts:
        print(f"      {part['work']['id']} {part.get('ordering-key')}. {part['work']['title']}")
for rest in works[count:]:
    print(f"  ({rest['score']} {rest['id']} [{rest.get('type')}] {rest['title']})")
