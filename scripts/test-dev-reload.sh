#!/usr/bin/env bash
# Checks scripts/dev-reload.sh's syntax and argument handling without touching the game (parse-only mode).
set -uo pipefail
SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/dev-reload.sh"
fail=0

bash -n "$SCRIPT" || { echo "FAIL: syntax"; fail=1; }

expect() { # EXIT_CODE EXPECTED_OUTPUT_SUBSTRING ARGS...
  local want="$1" text="$2"; shift 2
  local out code
  out="$(DEV_RELOAD_PARSE_ONLY=1 bash "$SCRIPT" "$@" 2>&1)"; code=$?
  if [[ "$code" != "$want" || "$out" != *"$text"* ]]; then
    echo "FAIL: dev-reload.sh $* -> exit $code, output: $out (want exit $want, '$text')"; fail=1
  fi
}

expect 0 "player= world= build=1 launch=1"
expect 0 "player=Hero One world=My World build=0 launch=1" --player "Hero One" --world "My World" --no-build
expect 0 "build=1 launch=0" --no-launch
expect 0 "Usage" --help
expect 2 "go together" --player Hero
expect 2 "go together" --world W
expect 2 "--player needs a name" --player
expect 2 "--world needs a name" --world ""
expect 2 "unknown argument: --bogus" --bogus

(( fail )) && exit 1
echo "test-dev-reload: OK"
