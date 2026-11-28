#!/usr/bin/env bash
# Mine every visible ore vein near the player (iron/lead/silver/gold first), up to N veins.
J="$(cd "$(dirname "$0")" && pwd)"
N=${1:-8}; R=${2:-40}
for i in $(seq 1 $N); do
  res=$(node $J/call.mjs find "{\"category\":\"ore\",\"radius\":$R}" | head -1 | sed 's/^RESULT //')
  pick=$(python3 -c "
import json,sys
r=json.loads(sys.argv[1]).get('results',[])
pri=['Gold','Platinum','Silver','Tungsten','Iron','Lead','Copper','Tin']
r.sort(key=lambda e:(next((i for i,p in enumerate(pri) if p in e['name']),9), e['distance']))
print(f\"{r[0]['x']} {r[0]['y']} {r[0]['name']}\" if r else '')" "$res")
  [ -z "$pick" ] && { echo "no more visible ore"; break; }
  set -- $pick
  echo "vein $3 at $1,$2: $(node $J/call.mjs mine_vein "{\"x\":$1,\"y\":$2}" 300 | grep DONE | grep -o '"status":"[a-z]*"\|"mined":[0-9]*' | tr '\n' ' ')"
done
