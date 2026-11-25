#!/usr/bin/env bash
# Copy the mod and the Core library source into tModLoader's ModSources/LlmPlayer.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$REPO/scripts/tml-paths.sh"

if [[ ! -d "$TML_MODSOURCES" ]]; then
  echo "ModSources not found — launch tModLoader once or set TML_MODSOURCES" >&2
  exit 1
fi

DEST="$TML_MODSOURCES/LlmPlayer"
mkdir -p "$DEST/Core"

# --delete removes stale sources; protect build output, the Core subtree (synced separately)
# and localization files tModLoader generates in the source folder.
rsync -a --delete \
  --exclude 'bin/' --exclude 'obj/' --exclude '.vs/' \
  --filter 'P /Core/' --filter 'P /Localization/' --filter 'P bin/' --filter 'P obj/' --filter 'P .vs/' \
  "$REPO/mod/LlmPlayer/" "$DEST/"

rsync -a --delete \
  --exclude '*.csproj' --exclude 'bin/' --exclude 'obj/' \
  "$REPO/mod/Core/" "$DEST/Core/"

echo "Synced mod to $DEST"
