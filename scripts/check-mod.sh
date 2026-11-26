#!/usr/bin/env bash
# Compile-check the mod without packaging it: copies this checkout's mod into a scratch folder
# ModSources/_LlmCheck_<name> and runs `dotnet build -t:Compile` there with the Windows SDK.
# It never touches ModSources/LlmPlayer or Mods/LlmPlayer.tmod, so several checkouts (worktrees)
# can run it at the same time, and it is safe while the game is running.
# Usage: scripts/check-mod.sh            (name = the checkout folder's name)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$REPO/scripts/tml-paths.sh"

NAME="${1:-$(basename "$REPO")}"
DEST="$TML_MODSOURCES/_LlmCheck_${NAME//[^A-Za-z0-9_-]/_}"
mkdir -p "$DEST/Core"
rsync -a --delete --exclude 'bin/' --exclude 'obj/' --filter 'P /Core/' --filter 'P bin/' --filter 'P obj/' \
  "$REPO/mod/LlmPlayer/" "$DEST/"
rsync -a --delete --exclude '*.csproj' --exclude 'bin/' --exclude 'obj/' "$REPO/mod/Core/" "$DEST/Core/"

case "$DEST" in
  /mnt/[a-z]/*) ;;
  *) echo "TML_MODSOURCES must be on a Windows drive (/mnt/<drive>/...): $DEST" >&2; exit 1 ;;
esac
cd "$DEST"
set +e
OUTPUT="$(cmd.exe /c "dotnet build -c Release -t:Compile -nologo -v:q" 2>&1)"
set -e
# Show errors and our own warnings; hide tModCodeAssist "magic number" noise.
grep -E "error|warning" <<<"$OUTPUT" | grep -v "ChangeMagicNumberToID" | sort -u || true
if grep -q "Build succeeded" <<<"$OUTPUT" && ! grep -qE "[1-9][0-9]* Error\(s\)" <<<"$OUTPUT"; then
  echo "check-mod: compile OK ($DEST)"
else
  echo "check-mod: compile FAILED" >&2
  exit 1
fi
