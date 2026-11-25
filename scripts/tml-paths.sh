# Sourced by the build scripts. Resolves TML_MODSOURCES (tModLoader's ModSources folder)
# from the Windows "Documents" folder (handles OneDrive redirection) unless already set.
if [[ -z "${TML_MODSOURCES:-}" ]]; then
  _docs_win="$(powershell.exe -NoProfile -Command "[Environment]::GetFolderPath('MyDocuments')" 2>/dev/null | tr -d '\r')"
  if [[ -z "$_docs_win" ]]; then
    echo "Could not find the Windows Documents folder — set TML_MODSOURCES" >&2
    exit 1
  fi
  TML_MODSOURCES="$(wslpath -u "$_docs_win")/My Games/Terraria/tModLoader/ModSources"
fi
export TML_MODSOURCES
