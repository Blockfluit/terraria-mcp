import sys,json
for l in sys.stdin:
    if 'DONE ' not in l: continue
    d=json.loads(l.split('DONE ',1)[1]); r=d.get('result') or {}
    print(d['status'], '|', d['reason'])
    print({k:v for k,v in r.items() if k!='found'})
    print([(f['kind'],f['name'],f['x'],f['y']) for f in r.get('found',[]) if f['kind']!='pot'])
