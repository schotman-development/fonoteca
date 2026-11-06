# Usage: mbsearch.py "<lucene query>" ...: the top work matches, with type and composers.
import sys, urllib.parse
from mbhttp import get
for query in sys.argv[1:]:
    works = get('work?limit=8&query=' + urllib.parse.quote(query))['works']
    print(f'== {query}')
    for work in works:
        composers = ', '.join(r['artist']['name'] for r in work.get('relations', []) if r.get('type') == 'composer')
        print(f"  {work['score']:>3} {work['id']} [{work.get('type')}] {work['title']} — {composers}")
