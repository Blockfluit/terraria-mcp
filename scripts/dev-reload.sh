#!/usr/bin/env bash
# One-command developer reload, run from WSL:
#   save & quit the game -> rebuild the mod -> relaunch tModLoader -> re-enter the same player and world
#   -> ask the mod to switch AI mode on (request_control; refused if you took control back with the hotkey).
#
# Usage: scripts/dev-reload.sh [--player NAME --world NAME] [--no-build] [--no-launch]
#   --player/--world  enter this player/world after launch (instead of the ones that were being played)
#   --no-build        skip scripts/build-mod.sh
#   --no-launch       stop after the build (the game stays closed)
# Needs the mod config option DevCommands enabled once (Workshop -> Manage Mods -> LLM Player -> Config).
# Env: TERRARIA_HOST (default: the WSL default gateway), TERRARIA_PORT (default 7878), TML_MODSOURCES.
# It never kills the game: if the game doesn't exit by itself, it fails and leaves it running.
set -euo pipefail

usage() { sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

PLAYER="" WORLD="" BUILD=1 LAUNCH=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --player) [[ $# -ge 2 && -n "$2" ]] || { echo "--player needs a name" >&2; exit 2; }; PLAYER="$2"; shift 2 ;;
    --world)  [[ $# -ge 2 && -n "$2" ]] || { echo "--world needs a name" >&2; exit 2; }; WORLD="$2"; shift 2 ;;
    --no-build) BUILD=0; shift ;;
    --no-launch) LAUNCH=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done
if [[ -n "$PLAYER" && -z "$WORLD" ]] || [[ -z "$PLAYER" && -n "$WORLD" ]]; then
  echo "--player and --world go together" >&2; exit 2
fi
# Test hook: stop after argument parsing.
[[ -n "${DEV_RELOAD_PARSE_ONLY:-}" ]] && { echo "player=$PLAYER world=$WORLD build=$BUILD launch=$LAUNCH"; exit 0; }

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$REPO/scripts/tml-paths.sh"
SAVE_DIR="$(dirname "$TML_MODSOURCES")"            # Main.SavePath (Documents/My Games/Terraria/tModLoader)
AUTOLOAD="$SAVE_DIR/LlmPlayerAutoload.json"
PORT="${TERRARIA_PORT:-7878}"

default_gateway() { # same rule as mcp-server/src/host.ts: little-endian hex gateway of the 00000000 route
  local _iface dest gw _rest
  while read -r _iface dest gw _rest; do
    [[ "$dest" == 00000000 && "$gw" =~ ^[0-9A-Fa-f]{8}$ && "$gw" != 00000000 ]] || continue
    printf '%d.%d.%d.%d\n' "0x${gw:6:2}" "0x${gw:4:2}" "0x${gw:2:2}" "0x${gw:0:2}"
    return
  done < <(tail -n +2 /proc/net/route)
}
HOST="${TERRARIA_HOST:-$(default_gateway)}"
HOST="${HOST:-127.0.0.1}"

log() { printf '[dev-reload] %s\n' "$*"; }
die() { printf '[dev-reload] ERROR: %s\n' "$*" >&2; exit 1; }

port_open() { timeout 2 bash -c 'exec 3<>"/dev/tcp/$0/$1"' "$HOST" "$PORT" 2>/dev/null; }

# bridge_call METHOD PARAMS_JSON [TIMEOUT_S] -> prints the response line ({"id":1,"result"|"error":...}).
# Note: the mod serves one client at a time, so this briefly replaces the MCP server's connection (it reconnects).
bridge_call() {
  timeout "${3:-10}" bash -c '
    exec 3<>"/dev/tcp/$0/$1" || exit 1
    printf "{\"id\":1,\"method\":\"%s\",\"params\":%s}\n" "$2" "$3" >&3
    while IFS= read -r line <&3; do
      case "$line" in "{\"id\":1,"*) printf "%s\n" "$line"; exit 0 ;; esac   # skip events
    done
    exit 1' "$HOST" "$PORT" "$1" "$2" 2>/dev/null
}

TML_DIR="${TML_DIR:-/mnt/c/Program Files (x86)/Steam/steamapps/common/tModLoader}"

tml_running() { # the main game process (dotnet tModLoader.dll), not tModLoader's own build/server runs and not the
  # test instances of scripts/instance.sh (started with -tmlsavedirectory).
  # If the process list can't be read, assume it is running (never build or launch over a live game).
  local n
  n="$(cd /mnt/c && powershell.exe -NoProfile -Command \
    "@(Get-CimInstance Win32_Process | Where-Object { (\$_.Name -eq 'dotnet.exe' -or \$_.Name -eq 'tModLoader.exe') -and \$_.CommandLine -like '*tModLoader*' -and \$_.CommandLine -notlike '*-build*' -and \$_.CommandLine -notlike '*-server*' -and \$_.CommandLine -notlike '*-tmlsavedirectory*' }).Count" \
    2>/dev/null | tr -d '\r')" || true
  [[ "$n" =~ ^[0-9]+$ ]] || return 0
  (( n > 0 ))
}

write_autoload() { # NAME WORLD -> the one-shot file the mod consumes at the title screen
  python3 -I - "$AUTOLOAD" "$1" "$2" <<'EOF'
import json, sys, datetime
path, player, world = sys.argv[1:4]
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
with open(path, "w", encoding="utf-8") as f:
    json.dump({"player": player, "world": world, "createdUtc": now}, f, ensure_ascii=False)
EOF
}

refresh_autoload() { # bump createdUtc so a file left by a failed run isn't rejected as stale
  python3 -I - "$AUTOLOAD" <<'EOF'
import json, sys, datetime
path = sys.argv[1]
with open(path, encoding="utf-8") as f:
    data = json.load(f)
data["createdUtc"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f, ensure_ascii=False)
EOF
}

# --- (a) save & quit the running game -------------------------------------------------------------
if port_open; then
  RELOAD=true; [[ -n "$PLAYER" ]] && RELOAD=false
  log "game reachable at $HOST:$PORT; asking it to save and exit"
  REPLY="$(bridge_call dev_save_and_exit "{\"reload\":$RELOAD}")" || die "no reply to dev_save_and_exit"
  log "reply: $REPLY"
  case "$REPLY" in
    *'"dev_commands_disabled"'*) die "enable DevCommands once in game: Workshop -> Manage Mods -> LLM Player -> Config" ;;
    '{"id":1,"error"'*) die "the game refused to save and exit (see reply above); nothing was changed" ;;
  esac
  log "waiting for the game to save and exit (60 s max)"
  deadline=$((SECONDS + 60))
  while port_open || tml_running; do
    (( SECONDS < deadline )) || die "tModLoader is still running after 60 s; it was NOT killed. Check the game window."
    sleep 1
  done
  log "game exited"
elif tml_running; then
  die "tModLoader is running but the bridge at $HOST:$PORT doesn't answer; save & quit manually, then rerun"
else
  log "game not running"
fi

# The last character/world loaded, kept so a reload from the title screen (no world open, so the game wrote no
# autoload file) still comes back to the same world.
LAST="$(dirname "$AUTOLOAD")/LlmPlayerLastWorld.json"
if [[ -n "$PLAYER" ]]; then
  write_autoload "$PLAYER" "$WORLD"
  log "wrote $AUTOLOAD (player \"$PLAYER\", world \"$WORLD\")"
elif [[ -f "$AUTOLOAD" ]]; then
  refresh_autoload || die "could not update $AUTOLOAD"
elif [[ -f "$LAST" ]]; then
  cp "$LAST" "$AUTOLOAD" && refresh_autoload || die "could not restore $AUTOLOAD from $LAST"
  log "no world was open: reloading the last one ($LAST)"
fi
[[ -f "$AUTOLOAD" ]] && cp "$AUTOLOAD" "$LAST"

# --- (b) build ------------------------------------------------------------------------------------
if (( BUILD )); then
  "$REPO/scripts/build-mod.sh" || die "build failed; the game was saved and stays closed. Fix it and rerun (the autoload file is kept)."
fi
(( LAUNCH )) || { log "--no-launch: done"; exit 0; }

# --- (c) launch -----------------------------------------------------------------------------------
log "launching tModLoader through Steam"
(cd /mnt/c && cmd.exe /c start "" "steam://rungameid/1281930" >/dev/null 2>&1) || die "could not start tModLoader through Steam"
# Seen in play: after a clean exit the Steam launch sometimes started nothing (no process, no log line) and the
# reload timed out twice. If no game process shows up within 60 s, start tModLoader's own launcher instead.
for _ in $(seq 1 12); do tml_running && break; sleep 5; done
if ! tml_running; then
  log "Steam didn't start the game within 60 s: starting start-tModLoader.bat"
  # Fully detached (setsid, no inherited stdin/stdout/stderr): a child holding this script's output pipe kept the
  # script (and whoever waited on it) running after it finished (seen in play: dev-reload "hung" after request_control).
  # setsid -f forks and returns at once; exec leaves no shell behind. (A plain `( … & )` subshell stayed alive
  # waiting on cmd.exe, which lives as long as the game, and kept this script's output pipe open.)
  (cd "$TML_DIR" && exec setsid -f cmd.exe /c start "" start-tModLoader.bat </dev/null >/dev/null 2>&1) \
    || die "could not start start-tModLoader.bat"
fi

if [[ ! -f "$AUTOLOAD" ]]; then
  log "no autoload file: load a world manually (or rerun with --player NAME --world NAME)."
fi

# --- (d) wait for the world -----------------------------------------------------------------------
log "waiting for a world to load (5 min max)"
deadline=$((SECONDS + 300)) STATE=""
while (( SECONDS < deadline )); do
  if port_open; then
    STATE="$(bridge_call get_state '{}' || true)"
    [[ "$STATE" == '{"id":1,"result"'* ]] && break
    STATUS="$(bridge_call dev_status '{}' || true)"
    if [[ "$STATUS" == *'"autoload":"failed'* ]]; then
      die "auto-load failed: $(sed -E 's/.*"autoload":"([^"]*)".*/\1/' <<<"$STATUS"). Load the world manually."
    fi
  fi
  STATE=""
  sleep 3
done
[[ -n "$STATE" ]] || die "no world loaded after 5 min"
log "world loaded. get_state:"
printf '%s\n' "$STATE"

# --- (e) hand control to the AI (the mod refuses if the user took control back with the hotkey) ----
CONTROL="$(bridge_call request_control '{}' || true)"
log "request_control: ${CONTROL:-no reply}"
if [[ "$CONTROL" != *'"granted":true'* ]]; then
  log "the AI does not have control: press Insert in the game window to hand it over."
fi
