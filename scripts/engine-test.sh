#!/usr/bin/env bash
# Engine tests: run scenario files (scripts/engine-test/scenarios/*.json) against running TEST instances, in parallel
# across instances. Each scenario builds its own terrain in the sky with dev_setup, runs real MCP-level actions
# (move_to, collect, attack ...) and checks the outcome. Test instances only: the mod refuses dev_setup elsewhere.
# Usage: scripts/engine-test.sh [-i 1,2,3] [--start] [--filter TEXT] [--json] [scenario.json ...]
#   -i LIST     instances to use (default 1)            --start   start instances that aren't running (main session only)
#   --filter    run only scenarios whose name/file contains TEXT
# Offline self-test (no game): node --test scripts/engine-test/lib.test.mjs
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INSTANCES="1" START=0 PASS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    -i|--instances) [[ $# -ge 2 && "$2" =~ ^[0-9]+(,[0-9]+)*$ ]] || { echo "-i needs e.g. 1,2" >&2; exit 2; }; INSTANCES="$2"; shift 2 ;;
    --start) START=1; shift ;;
    -h|--help) sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) PASS+=("$1"); shift ;;
  esac
done
PORTS=()
IFS=, read -ra NS <<<"$INSTANCES"
for n in "${NS[@]}"; do
  (( n >= 1 )) || { echo "instance 0 is the user's main game: engine tests run on test instances only" >&2; exit 2; }
  if (( START )) && ! "$REPO/scripts/instance.sh" status 2>/dev/null | grep -q "^instance $n: running"; then
    "$REPO/scripts/instance.sh" start "$n"
  fi
  PORTS+=("$("$REPO/scripts/instance.sh" port "$n")")
done
exec node "$REPO/scripts/engine-test/run.mjs" --ports "$(IFS=,; echo "${PORTS[*]}")" "${PASS[@]}"
