#!/usr/bin/env bash
# Checks scripts/instance.sh's syntax and argument handling without touching any game (parse-only mode).
set -uo pipefail
SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/instance.sh"
fail=0

bash -n "$SCRIPT" || { echo "FAIL: syntax"; fail=1; }

expect() { # EXIT_CODE EXPECTED_OUTPUT_SUBSTRING ARGS...
  local want="$1" text="$2"; shift 2
  local out code
  out="$(INSTANCE_PARSE_ONLY=1 bash "$SCRIPT" "$@" 2>&1)"; code=$?
  if [[ "$code" != "$want" || "$out" != *"$text"* ]]; then
    echo "FAIL: instance.sh $* -> exit $code, output: $out (want exit $want, '$text')"; fail=1
  fi
}

expect 0 "cmd=start n=1 player= world= fresh=0 mod= res=1024x640 allmods=0 control=1 wait=1 force=0" start 1
expect 0 "cmd=start n=2 player=Bot world=Tiny Test fresh=1 mod=build res=800x600" \
  start 2 --player Bot --world "Tiny Test" --fresh --mod build --res 800x600
expect 0 "control=0 wait=0" reload 3 --no-control --no-wait
expect 0 "cmd=stop n=1" stop 1 --force
expect 0 "force=1" stop 1 --force
expect 0 "launch=0" start 1 --no-launch
expect 0 "cmd=status n=" status
expect 0 "cmd=build" build
expect 0 "cmd=log n=0" log 0
expect 0 "cmd=port n=4" port 4
expect 0 "Usage" --help
expect 2 "main game" start 0
expect 2 "main game" stop 0
expect 2 "needs an instance number" start
expect 2 "needs an instance number" stop x
expect 2 "too big" start 101
expect 2 "unknown command: bogus" bogus
expect 2 "unknown argument: --bogus" start 1 --bogus
expect 2 "--res needs WxH" start 1 --res big
expect 2 "--player needs a name" start 1 --player
expect 2 "--mod needs" start 1 --mod ""

(( fail )) && exit 1
echo "test-instance: OK"
