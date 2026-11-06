# The one MusicBrainz GET the helpers share: 2s apart, and a 503 waits and asks again.
import json, re, time, urllib.error, urllib.request
env = open('/home/claude/repositories/fonoteca/.env', encoding='utf-8').read()
contact = re.search(r'^Fonoteca__MusicBrainzContact=(.*)$', env, re.M).group(1).strip()
AGENT = f'Fonoteca/0.1 ( {contact} )'

def get(path):
    sep = '&' if '?' in path else '?'
    request = urllib.request.Request(f'https://musicbrainz.org/ws/2/{path}{sep}fmt=json', headers={'User-Agent': AGENT})
    for attempt in range(5):
        time.sleep(3 + attempt * 10)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code != 503: raise
    raise RuntimeError(f'MusicBrainz kept answering 503 for {path}')
