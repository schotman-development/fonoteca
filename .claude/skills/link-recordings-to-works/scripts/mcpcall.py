# Usage: mcpcall.py <tool> '<json arguments>': one tools/call to Fonoteca's /mcp, prints the tool's text answer.
import json, re, sys, urllib.request
env = open('/home/claude/repositories/fonoteca/.env', encoding='utf-8').read()
token = re.search(r'^Fonoteca__McpToken=(.*)$', env, re.M).group(1).strip()
body = {'jsonrpc': '2.0', 'id': 1, 'method': 'tools/call', 'params': {'name': sys.argv[1], 'arguments': json.loads(sys.argv[2])}}
request = urllib.request.Request('http://localhost:5088/mcp', data=json.dumps(body).encode(), headers={
    'Authorization': f'Bearer {token}', 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'})
with urllib.request.urlopen(request, timeout=120) as response:
    for line in response.read().decode().splitlines():
        if line.startswith('data:'):
            result = json.loads(line[5:])
            if 'error' in result: sys.exit(json.dumps(result['error']))
            if result['result'].get('isError'): sys.exit(result['result']['content'][0]['text'])
            print(result['result']['content'][0]['text'])
