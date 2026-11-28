#!/usr/bin/env bash
# Night farming rounds at x=$1 (default 4430); prints kills, damage, coins, life; stops at dawn.
J="$(cd "$(dirname "$0")" && pwd)"; X=${1:-4430}
node $J/call.mjs move_to "{\"x\":$X,\"y\":${Y:-548}}" 300 | grep -c DONE >/dev/null
for i in $(seq 1 40); do
  out=$(node $J/call.mjs attack '{"target":"all","radius":40,"retreat_below":0.3,"weapon":"Platinum Bow","strategy":"kite","keep_distance":10}' 100)
  echo "$out" | grep -E "DONE|code" | grep -o '"kills":\[[^]]*\]\|"damageTaken":[0-9]*\|"code":"[a-z_]*"\|"reason":"low[^"]*"' | tr '\n' ' '
  echo "$out" | grep RECENT | grep -o 'died[^"]*'
  node $J/call.mjs pick_up_items '{"radius":25}' 30 >/dev/null
  st=$(node $J/call.mjs get_state '{}' | head -1); echo "$st" | grep -o 'died[^"]*'
  t=$(echo "$st" | grep -o '"timeOfDay":"[^"]*"'); echo " $(node $J/call.mjs get_inventory '{}' | head -1 | grep -o '"coins":{[^}]*}') $(echo "$st" | grep -o '"life":[0-9]*') $t"
  echo "$t" | grep -q '"day' && break
  node $J/call.mjs wait '{"seconds":12}' 20 >/dev/null
done
