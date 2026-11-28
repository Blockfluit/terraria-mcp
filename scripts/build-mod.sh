#!/usr/bin/env bash
# Sync the mod into ModSources and build it with the Windows dotnet SDK.
# On success tModLoader writes LlmPlayer.tmod to Documents/My Games/Terraria/tModLoader/Mods.
# TML_BUILD_SAVEDIR=<Windows path without spaces> (used by scripts/instance.sh build): pass -tmlsavedirectory to
# tModLoader's packaging step instead, so the .tmod goes to <that dir>/Mods and the main game's copy is untouched.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$REPO/scripts/tml-paths.sh"

"$REPO/scripts/sync-mod.sh"

WIN_DEST="$(wslpath -w "$TML_MODSOURCES/LlmPlayer")"

# cmd.exe can't use a WSL (UNC) working directory. ModSources normally lives on a Windows drive
# (/mnt/c/...), so cd there in WSL and cmd.exe inherits it as a C:\ path — this also avoids
# quoting "My Games" through cmd's own `cd /d`.
case "$TML_MODSOURCES" in
  /mnt/[a-z]/*) ;;
  *) echo "TML_MODSOURCES must be on a Windows drive (/mnt/<drive>/...): $TML_MODSOURCES" >&2; exit 1 ;;
esac
cd "$TML_MODSOURCES/LlmPlayer"
echo "Building $WIN_DEST"
set +e
EXTRA=""
if [[ -n "${TML_BUILD_SAVEDIR:-}" ]]; then
  [[ "$TML_BUILD_SAVEDIR" != *" "* ]] || { echo "TML_BUILD_SAVEDIR must not contain spaces: $TML_BUILD_SAVEDIR" >&2; exit 1; }
  # %20: MSBuild unescapes it to the space between flag and value (no quotes to survive WSL -> cmd.exe).
  EXTRA=" -p:ExtraBuildModFlags=-tmlsavedirectory%20$TML_BUILD_SAVEDIR"
  echo "Packaging into $TML_BUILD_SAVEDIR\\Mods"
fi
OUTPUT="$(cmd.exe /c "dotnet build -c Release$EXTRA" 2>&1)"
set -e
printf '%s\n' "$OUTPUT"

if ! grep -q "Build succeeded" <<<"$OUTPUT"; then
  echo "build-mod: build failed" >&2
  exit 1
fi
