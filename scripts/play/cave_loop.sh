#!/usr/bin/env bash
# Cave progression loop: explore caves (stop on life crystal/chest), collect+use crystals, loot chests, mine ore veins.
J="$(cd "$(dirname "$0")" && pwd)"; N=${1:-4}
for i in $(seq 1 $N); do
  out=$(node $J/call.mjs explore_caves '{"max_seconds":400,"collect_pots":true,"radius":160,"stop_on":["life_crystal","life_fruit","enchanted_sword"]}' 480)
  echo "$out" | python3 $J/caves.py | head -2
  echo "$out" | grep -o '"text":"[^"]*"'   # death message, if any
  stop=$(echo "$out" | grep -o '"stoppedAt":{[^}]*}')
  if echo "$stop" | grep -q life_crystal; then x=$(echo "$stop" | grep -o '"x":[0-9]*' | grep -o '[0-9]*'); y=$(echo "$stop" | grep -o '"y":[0-9]*' | grep -o '[0-9]*')
    echo "collect crystal: $(node $J/call.mjs collect "{\"x\":$x,\"y\":$y}" 120 | grep DONE | grep -o '"status":"[a-z]*"\|"reason":"[^"]*"' | tr '\n' ' ')"
    echo "use: $(node $J/call.mjs use_item '{"item":"Life Crystal"}' 20 | grep -E 'DONE|code' | grep -o '"status":"[a-z]*"\|"reason":"[^"]*"\|"message":"[^"]*"' | tr '\n' ' ')"; fi
  if echo "$stop" | grep -q chest; then x=$(echo "$stop" | grep -o '"x":[0-9]*' | grep -o '[0-9]*'); y=$(echo "$stop" | grep -o '"y":[0-9]*' | grep -o '[0-9]*')
    echo "loot: $(node $J/call.mjs loot_chest "{\"x\":$x,\"y\":$y}" 150 | grep DONE | grep -o '"looted":\[[^]]*\]\|"reason":"[^"]*"' | cut -c1-300)"; fi
  $J/mine_nearby.sh 3 30 2>/dev/null | grep -v "no more" 
  st=$(node $J/call.mjs get_state '{}' | head -1); echo "-- $(echo "$st" | grep -o '"x":[0-9]*,"y":[0-9]*\|"life":[0-9]*\|"lifeMax":[0-9]*\|"timeOfDay":"[^"]*"' | head -5 | tr '\n' ' ')"
done
