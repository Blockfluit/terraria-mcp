#!/usr/bin/env bash
# Test instances: extra tModLoader clients next to the user's main game, each with its own throwaway save folder,
# its own bridge port and its own copy of the built mod. Instance 0 is the user's main game (port 7878, the real
# saves); this script never starts, stops or changes it.
#
# Usage: scripts/instance.sh <command> [N] [options]       (N >= 1; port = 7878 + N unless taken, see status)
#   start N    copy the player/world/mod into instance N's folder (first start, or --fresh), launch it, wait until the
#              world is loaded, take control (request_control)
#              --player NAME --world NAME  which saves (default: the main game's last player/world); worlds are
#                                          looked up in <instances>/templates/Worlds first, then the main Worlds folder
#              --fresh        recopy player, world and map from the source (throws away instance N's progress)
#              --mod main|build|PATH       LlmPlayer.tmod to use (default: the newer of main Mods/ and <instances>/_build)
#              --res WxH      screen size (default: the main game's, so fair vision sees the same area)        --all-mods  enable the main game's mods too
#              --no-control   don't call request_control            --no-wait   return right after launching
#              --no-launch    only prepare the folder (copy saves, settings, mod, autoload request)
#              --render off|minimal|full  drawing (default off: nothing drawn, the mod moves the camera and lights
#                                          the screen itself; minimal = ~5 fps to watch; full = vanilla)
#              --timescale 1..8|max       game updates per 1/60 s (default max: as fast as the PC allows)
#              --watch        = --render full --timescale 1 (debug by watching at normal speed)
#              --no-audio     SDL's dummy audio driver (SDL_AUDIODRIVER=dummy): no sound device at all
#   stop N     save & exit instance N through the bridge (dev_save_and_exit); --force kills it if that fails
#   reload N   stop N, then start N (same options as start; picks up a new mod build)
#   speed N    change a running instance's --render / --timescale (dev_render / dev_timescale), print dev_status
#   status     every instance: folder, pid, port, what get_state says
#   port N     print instance N's bridge port (from its status file)
#   log N      print the path of instance N's client log (N = 0: the main game's)
#   template N copy instance N's worlds into <instances>/templates/Worlds (e.g. a small world made there by hand)
#   build      build this checkout into <instances>/_build/Mods only (main Mods/LlmPlayer.tmod is untouched)
# Env: TML_INSTANCES (instances root, default %LOCALAPPDATA%\LlmPlayerInstances), TERRARIA_HOST, TML_DIR, TML_MODSOURCES.
set -euo pipefail

usage() { sed -n '2,29p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { printf '[instance] ERROR: %s\n' "$*" >&2; exit 1; }
log() { printf '[instance] %s\n' "$*"; }

CMD="${1:-}"; [[ $# -gt 0 ]] && shift
N=""
case "$CMD" in
  start|stop|reload|port|log|template|speed)
    [[ $# -ge 1 && "$1" =~ ^[0-9]+$ ]] || { echo "$CMD needs an instance number" >&2; exit 2; }
    N="$1"; shift
    if [[ "$CMD" != log && "$N" -lt 1 ]]; then
      echo "instance 0 is the user's main game: use scripts/dev-reload.sh for it" >&2; exit 2
    fi
    (( N <= 100 )) || { echo "instance number too big (max 100)" >&2; exit 2; } ;;
  status|build) ;;
  -h|--help|"") usage; [[ -n "$CMD" ]] && exit 0; exit 2 ;;
  *) echo "unknown command: $CMD" >&2; usage >&2; exit 2 ;;
esac

PLAYER="" WORLD="" FRESH=0 MODSRC="" RES="" ALLMODS=0 CONTROL=1 WAIT=1 FORCE=0 LAUNCH=1
RENDER="off" TIMESCALE="max" NOAUDIO=0 SPEED_SET=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --player) [[ $# -ge 2 && -n "$2" ]] || { echo "--player needs a name" >&2; exit 2; }; PLAYER="$2"; shift 2 ;;
    --world)  [[ $# -ge 2 && -n "$2" ]] || { echo "--world needs a name" >&2; exit 2; }; WORLD="$2"; shift 2 ;;
    --mod)    [[ $# -ge 2 && -n "$2" ]] || { echo "--mod needs main, build or a path" >&2; exit 2; }; MODSRC="$2"; shift 2 ;;
    --res)    [[ $# -ge 2 && "$2" =~ ^[0-9]{3,4}x[0-9]{3,4}$ ]] || { echo "--res needs WxH, e.g. 1024x640" >&2; exit 2; }; RES="$2"; shift 2 ;;
    --fresh) FRESH=1; shift ;;
    --all-mods) ALLMODS=1; shift ;;
    --no-control) CONTROL=0; shift ;;
    --no-wait) WAIT=0; shift ;;
    --no-launch) LAUNCH=0; shift ;;
    --force) FORCE=1; shift ;;
    --render) [[ $# -ge 2 && "$2" =~ ^(off|minimal|full)$ ]] || { echo "--render needs off, minimal or full" >&2; exit 2; }
              RENDER="$2"; SPEED_SET+=r; shift 2 ;;
    --timescale) [[ $# -ge 2 && "$2" =~ ^([1-8]|max)$ ]] || { echo "--timescale needs 1..8 or max" >&2; exit 2; }
              TIMESCALE="$2"; SPEED_SET+=t; shift 2 ;;
    --watch) RENDER="full"; TIMESCALE="1"; SPEED_SET+=rt; shift ;;
    --no-audio) NOAUDIO=1; shift ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done
# Test hook (scripts/test-instance.sh): stop after argument parsing.
if [[ -n "${INSTANCE_PARSE_ONLY:-}" ]]; then
  echo "cmd=$CMD n=$N player=$PLAYER world=$WORLD fresh=$FRESH mod=$MODSRC res=$RES allmods=$ALLMODS control=$CONTROL wait=$WAIT force=$FORCE launch=$LAUNCH render=$RENDER timescale=$TIMESCALE noaudio=$NOAUDIO"
  exit 0
fi

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$REPO/scripts/tml-paths.sh"
MAIN_SAVE="$(dirname "$TML_MODSOURCES")"              # the user's real saves: only ever read here
TML_DIR="${TML_DIR:-/mnt/c/Program Files (x86)/Steam/steamapps/common/tModLoader}"
if [[ -z "${TML_INSTANCES:-}" ]]; then
  _lad="$(cd /mnt/c && powershell.exe -NoProfile -Command "[Environment]::GetFolderPath('LocalApplicationData')" 2>/dev/null | tr -d '\r')"
  [[ -n "$_lad" ]] || die "could not find %LOCALAPPDATA%: set TML_INSTANCES"
  TML_INSTANCES="$(wslpath -u "$_lad")/LlmPlayerInstances"
fi
case "$TML_INSTANCES" in /mnt/[a-z]/*) ;; *) die "TML_INSTANCES must be on a Windows drive: $TML_INSTANCES" ;; esac
[[ "$TML_INSTANCES" != "$MAIN_SAVE"* ]] || die "TML_INSTANCES must not be inside the main save folder"
TEMPLATES="$TML_INSTANCES/templates"
BUILD_DIR="$TML_INSTANCES/_build"
IDIR="$TML_INSTANCES/$N"

default_gateway() { # same rule as mcp-server/src/host.ts
  local _iface dest gw _rest
  while read -r _iface dest gw _rest; do
    [[ "$dest" == 00000000 && "$gw" =~ ^[0-9A-Fa-f]{8}$ && "$gw" != 00000000 ]] || continue
    printf '%d.%d.%d.%d\n' "0x${gw:6:2}" "0x${gw:4:2}" "0x${gw:2:2}" "0x${gw:0:2}"
    return
  done < <(tail -n +2 /proc/net/route)
}
HOST="${TERRARIA_HOST:-$(default_gateway)}"
HOST="${HOST:-127.0.0.1}"

port_open() { timeout 2 bash -c 'exec 3<>"/dev/tcp/$0/$1"' "$HOST" "$1" 2>/dev/null; }

# bridge_call PORT METHOD PARAMS_JSON [TIMEOUT_S] -> the response line. Replaces any other client of that instance.
bridge_call() {
  timeout "${4:-10}" bash -c '
    exec 3<>"/dev/tcp/$0/$1" || exit 1
    printf "{\"id\":1,\"method\":\"%s\",\"params\":%s}\n" "$2" "$3" >&3
    while IFS= read -r line <&3; do
      case "$line" in "{\"id\":1,"*) printf "%s\n" "$line"; exit 0 ;; esac
    done
    exit 1' "$HOST" "$1" "$2" "$3" 2>/dev/null
}

json_get() { # FILE KEY -> value ("" if missing)
  python3 -I -c 'import json,sys
try: v=json.load(open(sys.argv[1],encoding="utf-8-sig")).get(sys.argv[2],"")
except Exception: v=""
print(v)' "$1" "$2"
}

pid_file() { echo "$TML_INSTANCES/$1/instance.pid"; }
# The instance's port: the mod's status file when it belongs to the running pid, else 7878 + N.
instance_port() {
  local n="$1" st="$TML_INSTANCES/$1/LlmPlayerInstance.json" pid=""
  [[ -f "$(pid_file "$n")" ]] && pid="$(cat "$(pid_file "$n")")"
  if [[ -f "$st" && -n "$pid" && "$(json_get "$st" pid)" == "$pid" ]]; then json_get "$st" port; else echo $((7878 + n)); fi
}
# Is PID a tModLoader process started for instance N (its command line names the instance's folder)?
pid_is_instance() {
  local pid="$1" win; win="$(wslpath -w "$TML_INSTANCES/$2")"
  [[ "$pid" =~ ^[0-9]+$ ]] || return 1
  local cl; cl="$(cd /mnt/c && powershell.exe -NoProfile -Command \
    "(Get-CimInstance Win32_Process -Filter 'ProcessId=$pid').CommandLine" 2>/dev/null | tr -d '\r')" || return 1
  [[ "$cl" == *tModLoader.dll* && "$cl" == *"\"$win\""* ]]   # quoted: instance 1 must not match 10
}
instance_running() { local f; f="$(pid_file "$1")"; [[ -f "$f" ]] && pid_is_instance "$(cat "$f")" "$1"; }

# --- start helpers --------------------------------------------------------------------------------
find_world() { # NAME -> directory holding NAME's .wld (templates first, then the main Worlds folder) + file base
  python3 -I - "$1" "$TEMPLATES/Worlds" "$MAIN_SAVE/Worlds" <<'EOF'
import os, sys
name, dirs = sys.argv[1], sys.argv[2:]
want = name.lower().replace(" ", "_")
for d in dirs:
    if not os.path.isdir(d): continue
    for f in sorted(os.listdir(d)):
        base, ext = os.path.splitext(f)
        if ext == ".wld" and base.lower() in (name.lower(), want, os.path.splitext(name)[0].lower()):
            print(d + "\t" + base); sys.exit(0)
sys.exit(1)
EOF
}

copy_saves() {
  local player="$PLAYER" world="$WORLD" last="$MAIN_SAVE/LlmPlayerLastWorld.json"
  if [[ -z "$player" ]]; then
    [[ -f "$last" ]] || die "no --player given and no $last"
    player="$(json_get "$last" playerFile)"; player="${player%.plr}"
    [[ -n "$player" ]] || player="$(json_get "$last" player)"
  fi
  if [[ -z "$world" ]]; then
    [[ -f "$last" ]] || die "no --world given and no $last"
    world="$(json_get "$last" worldFile)"; world="${world%.wld}"
    [[ -n "$world" ]] || world="$(json_get "$last" world)"
  fi
  local pfile="${player%.plr}"; [[ -f "$MAIN_SAVE/Players/$pfile.plr" ]] || pfile="${pfile// /_}"
  [[ -f "$MAIN_SAVE/Players/$pfile.plr" ]] || die "player \"$player\" not found in $MAIN_SAVE/Players"
  local found wdir wbase
  found="$(find_world "$world")" || die "world \"$world\" not found in $TEMPLATES/Worlds or $MAIN_SAVE/Worlds"
  wdir="${found%%$'\t'*}" wbase="${found#*$'\t'}"

  mkdir -p "$IDIR/Players" "$IDIR/Worlds"
  if (( FRESH )) || [[ ! -f "$IDIR/Players/$pfile.plr" ]]; then
    log "copying player $pfile (with its maps) from the main saves"
    rm -rf "$IDIR/Players/$pfile" "$IDIR/Players/$pfile.plr" "$IDIR/Players/$pfile.tplr"
    cp "$MAIN_SAVE/Players/$pfile.plr" "$IDIR/Players/"
    [[ -f "$MAIN_SAVE/Players/$pfile.tplr" ]] && cp "$MAIN_SAVE/Players/$pfile.tplr" "$IDIR/Players/"
    [[ -d "$MAIN_SAVE/Players/$pfile" ]] && cp -r "$MAIN_SAVE/Players/$pfile" "$IDIR/Players/"
  fi
  if (( FRESH )) || [[ ! -f "$IDIR/Worlds/$wbase.wld" ]]; then
    log "copying world $wbase from $wdir"
    rm -f "$IDIR/Worlds/$wbase.wld" "$IDIR/Worlds/$wbase.twld"
    cp "$wdir/$wbase.wld" "$IDIR/Worlds/"
    [[ -f "$wdir/$wbase.twld" ]] && cp "$wdir/$wbase.twld" "$IDIR/Worlds/"
  fi
  # The autoload request (one-shot, consumed by the mod at the title screen of this instance only).
  python3 -I - "$IDIR/LlmPlayerAutoload.json" "$IDIR/Players/$pfile.plr" "$pfile.plr" "$wbase.wld" "$IDIR/Worlds/$wbase.wld" <<'EOF'
import json, sys, datetime
out, plr, pfile, wfile, wld = sys.argv[1:6]
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
# Names are matched by file first (DevAutoload.Pick); the display names are a fallback only.
json.dump({"player": pfile[:-4], "world": wfile[:-4].replace("_", " "), "playerFile": pfile, "worldFile": wfile,
           "createdUtc": now}, open(out, "w", encoding="utf-8"), ensure_ascii=False)
EOF
  log "autoload: $pfile.plr in $wbase.wld"
}

copy_settings() {
  mkdir -p "$IDIR/ModConfigs"
  # Game settings from the main game, then a small window and no sound/eye candy (drawing only: the simulation,
  # lighting and the AI's fair vision stay the same).
  # Same screen size as instance 0 by default: fair vision (lit tiles on screen) then covers the same area. Seen in
  # the engine tests: at 1024x640 goals 40+ tiles away were off-screen and "goal_not_standable". With rendering off
  # the size costs nothing.
  if [[ -z "$RES" ]]; then
    RES="$(python3 -I -c 'import json,sys; c=json.load(open(sys.argv[1])); print(f"{c.get(\"DisplayWidth\",1920)}x{c.get(\"DisplayHeight\",1080)}")' "$MAIN_SAVE/config.json" 2>/dev/null || echo 1920x1080)"
  fi
  python3 -I - "$MAIN_SAVE/config.json" "$IDIR/config.json" "${RES%x*}" "${RES#*x}" <<'EOF'
import json, os, sys
src, dst, w, h = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
cfg = {}
for p in (dst, src):
    if os.path.exists(p):
        try: cfg = json.load(open(p, encoding="utf-8-sig")); break
        except Exception: pass
cfg.update({"DisplayWidth": w, "DisplayHeight": h, "Fullscreen": False, "WindowMaximized": False,
            "WindowBorderless": False, "VolumeSound": 0.0, "VolumeAmbient": 0.0, "VolumeMusic": 0.0,
            "BackgroundEnabled": False, "UseStormEffect": False, "UseHeatDistortion": False, "WaveQuality": 0})
json.dump(cfg, open(dst, "w", encoding="utf-8"), indent=2)
EOF
  # The test-instance marker: together with -tmlsavedirectory and a port other than 7878 it unlocks the test-only dev
  # commands (time scale, render off, dev_setup). Only this script writes it, and only into an instance folder.
  printf '{"instance":%d}' "$N" > "$IDIR/LlmPlayerTestInstance.json"
  [[ -f "$MAIN_SAVE/input profiles.json" && ! -f "$IDIR/input profiles.json" ]] && cp "$MAIN_SAVE/input profiles.json" "$IDIR/"
  # The mod's config: the main one, with DevCommands on (stop N saves & exits through the bridge).
  python3 -I - "$MAIN_SAVE/ModConfigs/LlmPlayer_LlmConfig.json" "$IDIR/ModConfigs/LlmPlayer_LlmConfig.json" <<'EOF'
import json, os, sys
src, dst = sys.argv[1:3]
cfg = {}
if os.path.exists(src):
    try: cfg = json.load(open(src, encoding="utf-8-sig"))
    except Exception: pass
cfg["DevCommands"] = True
cfg.pop("Port", None)  # the port comes from -llmport
json.dump(cfg, open(dst, "w", encoding="utf-8"), indent=2)
EOF
}

copy_mod() {
  local main="$MAIN_SAVE/Mods/LlmPlayer.tmod" built="$BUILD_DIR/Mods/LlmPlayer.tmod" src
  case "$MODSRC" in
    main) src="$main" ;;
    build) src="$built" ;;
    "") src="$main"; [[ -f "$built" && ( ! -f "$main" || "$built" -nt "$main" ) ]] && src="$built" ;;
    *) src="$MODSRC" ;;
  esac
  [[ -f "$src" ]] || die "no mod build at $src (run scripts/build-mod.sh or scripts/instance.sh build)"
  mkdir -p "$IDIR/Mods"
  cp "$src" "$IDIR/Mods/LlmPlayer.tmod"
  if (( ALLMODS )) && [[ -f "$MAIN_SAVE/Mods/enabled.json" ]]; then
    cp "$MAIN_SAVE/Mods/enabled.json" "$IDIR/Mods/enabled.json"
    for f in "$MAIN_SAVE"/Mods/*.tmod; do [[ "$(basename "$f")" == LlmPlayer.tmod ]] || cp "$f" "$IDIR/Mods/"; done
  else
    printf '[\n  "LlmPlayer"\n]' > "$IDIR/Mods/enabled.json"   # only our mod: loads faster
  fi
  log "mod: $src ($(date -r "$src" '+%F %T'))"
}

launch() {
  local port=$((7878 + N)) exe wd idir_win pid
  exe="$(wslpath -w "$TML_DIR/dotnet/dotnet.exe")"
  [[ -f "$TML_DIR/dotnet/dotnet.exe" ]] || exe="dotnet"
  wd="$(wslpath -w "$TML_DIR")" idir_win="$(wslpath -w "$IDIR")"
  rm -f "$IDIR/LlmPlayerInstance.json"
  # Straight to tModLoader.dll with the working directory at the install (as start-tModLoader.bat does after its
  # dotnet checks), not through Steam: Steam starts one copy of an app. -tmlsavedirectory is used as-is as Main.SavePath.
  local psenv=""
  (( NOAUDIO )) && psenv="\$env:SDL_AUDIODRIVER='dummy'; "
  pid="$(cd /mnt/c && powershell.exe -NoProfile -Command "$psenv\$p = Start-Process -FilePath '$exe' -WorkingDirectory '$wd' -PassThru -ArgumentList 'tModLoader.dll','-tmlsavedirectory','\"$idir_win\"','-llmport','$port','-llmrender','$RENDER','-llmtimescale','$TIMESCALE'; \$p.Id" 2>&1 | tr -d '\r')"
  [[ "$pid" =~ ^[0-9]+$ ]] || die "launch failed: $pid"
  echo "$pid" > "$(pid_file "$N")"
  log "launched instance $N: pid $pid, save dir $idir_win, requested port $port, render $RENDER, timescale $TIMESCALE"
}

wait_ready() { # -> sets PORT
  local pid deadline st state status
  pid="$(cat "$(pid_file "$N")")"
  log "waiting for the bridge and the world (5 min max)"
  deadline=$((SECONDS + 300)) state=""
  while (( SECONDS < deadline )); do
    pid_is_instance "$pid" "$N" || die "instance $N (pid $pid) exited during startup; see: $0 log $N"
    PORT="$(instance_port "$N")"
    if [[ -f "$IDIR/LlmPlayerInstance.json" ]] && port_open "$PORT"; then
      state="$(bridge_call "$PORT" get_state '{}' || true)"
      [[ "$state" == '{"id":1,"result"'* ]] && break
      status="$(bridge_call "$PORT" dev_status '{}' || true)"
      [[ "$status" == *'"autoload":"failed'* ]] && die "auto-load failed: $(sed -E 's/.*"autoload":"([^"]*)".*/\1/' <<<"$status")"
    fi
    state=""
    sleep 3
  done
  [[ -n "$state" ]] || die "no world loaded after 5 min (the game keeps running; $0 stop $N --force)"
  log "instance $N ready on port $PORT: ${state:0:300}"
}

do_start() {
  if instance_running "$N"; then die "instance $N is already running (pid $(cat "$(pid_file "$N")")); use reload"; fi
  mkdir -p "$IDIR"
  copy_saves
  copy_settings
  copy_mod
  (( LAUNCH )) || { log "--no-launch: prepared $IDIR"; return; }
  launch
  (( WAIT )) || { log "--no-wait: done (port: $0 port $N)"; return; }
  wait_ready
  if (( CONTROL )); then
    log "request_control: $(bridge_call "$PORT" request_control '{}' || echo 'no reply')"
  fi
  log "use it: TERRARIA_PORT=$PORT node scripts/play/call.mjs get_state   (or --port $PORT)"
}

do_stop() {
  local pf pid port reply
  pf="$(pid_file "$N")"
  if ! instance_running "$N"; then log "instance $N is not running"; rm -f "$pf"; return 0; fi
  pid="$(cat "$pf")" port="$(instance_port "$N")"
  reply=""
  port_open "$port" && reply="$(bridge_call "$port" dev_save_and_exit '{"reload":false}' || true)"
  log "dev_save_and_exit on port $port: ${reply:-no reply}"
  if [[ "$reply" == '{"id":1,"result"'* ]]; then
    local deadline=$((SECONDS + 60))
    while pid_is_instance "$pid" "$N" && (( SECONDS < deadline )); do sleep 1; done
  fi
  if pid_is_instance "$pid" "$N"; then
    (( FORCE )) || die "instance $N (pid $pid) did not exit; rerun with --force to kill it (its unsaved progress is lost)"
    log "killing pid $pid"
    (cd /mnt/c && powershell.exe -NoProfile -Command "Stop-Process -Id $pid -Force") || die "could not kill pid $pid"
    sleep 1
  fi
  rm -f "$pf" "$IDIR/LlmPlayerInstance.json"
  log "instance $N stopped"
}

find_log() { # N -> the client log that names this save folder (the mod logs "save path ..." when the bridge starts)
  local n="$1" win logs="$TML_DIR/tModLoader-Logs" f
  if (( n == 0 )); then win="$(wslpath -w "$MAIN_SAVE")"; else win="$(wslpath -w "$TML_INSTANCES/$n")"; fi
  # Newest first; the line must END with the folder ("...\1" must not match "...\10").
  while IFS= read -r f; do
    if WANT="save path $win" awk '{ sub(/\r$/, ""); s = ENVIRON["WANT"]
        if (length($0) >= length(s) && substr($0, length($0) - length(s) + 1) == s) { found = 1; exit } }
        END { exit !found }' "$f"; then
      echo "$f"; return 0
    fi
  done < <(ls -t "$logs"/client*.log 2>/dev/null)
  (( n == 0 )) && [[ -f "$logs/client.log" ]] && { echo "$logs/client.log"; return 0; }
  return 1
}

case "$CMD" in
  start) do_start ;;
  stop) do_stop ;;
  reload) do_stop; do_start ;;
  port) instance_port "$N" ;;
  speed)
    instance_running "$N" || die "instance $N is not running"
    PORT="$(instance_port "$N")"
    [[ -n "$SPEED_SET" ]] || die "speed needs --render, --timescale or --watch"
    if [[ "$SPEED_SET" == *r* ]]; then
      reply="$(bridge_call "$PORT" dev_render "{\"mode\":\"$RENDER\"}" || true)"
      [[ "$reply" == '{"id":1,"result"'* ]] || die "dev_render: ${reply:-no reply}"
    fi
    if [[ "$SPEED_SET" == *t* ]]; then
      if [[ "$TIMESCALE" == max ]]; then f='"max"'; else f="$TIMESCALE"; fi
      reply="$(bridge_call "$PORT" dev_timescale "{\"factor\":$f}" || true)"
      [[ "$reply" == '{"id":1,"result"'* ]] || die "dev_timescale: ${reply:-no reply}"
    fi
    sleep 2   # one measuring window
    bridge_call "$PORT" dev_status '{}' ;;
  log) find_log "$N" || die "no client log names instance $N's save folder" ;;
  template)
    [[ -d "$IDIR/Worlds" ]] || die "instance $N has no worlds"
    mkdir -p "$TEMPLATES/Worlds"
    cp "$IDIR"/Worlds/*.wld "$TEMPLATES/Worlds/"; cp "$IDIR"/Worlds/*.twld "$TEMPLATES/Worlds/" 2>/dev/null || true
    log "templates now: $(cd "$TEMPLATES/Worlds" && ls ./*.wld | tr '\n' ' ')" ;;
  build)
    mkdir -p "$BUILD_DIR/Mods"
    TML_BUILD_SAVEDIR="$(wslpath -w "$BUILD_DIR")" "$REPO/scripts/build-mod.sh"
    [[ -f "$BUILD_DIR/Mods/LlmPlayer.tmod" ]] || die "the build didn't write $BUILD_DIR/Mods/LlmPlayer.tmod"
    log "built $BUILD_DIR/Mods/LlmPlayer.tmod ($(date -r "$BUILD_DIR/Mods/LlmPlayer.tmod" '+%F %T')); reload instances to use it" ;;
  status)
    found=0
    for d in "$TML_INSTANCES"/*/; do
      n="$(basename "$d")"; [[ "$n" =~ ^[0-9]+$ ]] || continue
      found=1
      if instance_running "$n"; then
        p="$(instance_port "$n")"
        st="$(bridge_call "$p" get_state '{}' 3 || true)"
        printf 'instance %s: running, pid %s, port %s, %s\n' "$n" "$(cat "$(pid_file "$n")")" "$p" \
          "$( [[ "$st" == '{"id":1,"result"'* ]] && echo "in world: ${st:0:160}" || echo "no world / bridge busy")"
      else
        printf 'instance %s: stopped (%s)\n' "$n" "$(ls "$d"Worlds 2>/dev/null | grep '\.wld$' | tr '\n' ' ')"
      fi
    done
    (( found )) || echo "no instances under $TML_INSTANCES" ;;
esac
