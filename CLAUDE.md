# terraria-mcp — working notes for Claude

## Goal
Play the user's character through the MCP and beat Terraria boss by boss (first: Eye of Cthulhu). Plan only the
next step when the current one is done. Fix or add tools when play shows a real gap.

## Rules that never bend
- Completely fair play: only what the player could see (fair vision, map-revealed tiles, tiles touching the body);
  no off-screen positions, no teleporting, no item spawning, real controls and vanilla reach only.
- The AI may take control with `request_control`, but when the user presses Insert to take it back, stop and wait.

## Workflow
- Use subagents to speed things up (the user wants this). Each agent works in its own worktree:
  `git worktree add -b wt/<name> .claude/worktrees/<name> feat/v1`. Agents may run Core tests, MCP tests and
  `scripts/check-mod.sh <name>` (compile only, safe while the game runs). They must never run `build-mod.sh`,
  `sync-mod.sh`, `dev-reload.sh` or start/stop the game, and they commit on their branch without pushing.
- The main session owns the single game instance: it plays, tests in game, merges agent branches into `feat/v1`,
  runs all checks, commits and pushes.
- Checks before every commit:
  `export PATH="$HOME/.dotnet:$PATH"; dotnet test mod/Core.Tests`,
  `cd mcp-server && npm test && npm run build`, `scripts/check-mod.sh main`.
- Deploy to the game: `scripts/dev-reload.sh` (saves and exits the running game, builds, relaunches through Steam,
  auto-loads the same character and world, takes control; ~25 s). Needs `DevCommands` on in the mod config.
- Logs: `C:\Program Files (x86)\Steam\steamapps\common\tModLoader\tModLoader-Logs\client.log`.
- Play helpers for the main session: `scripts/play/` (see its README). Every loop and bridge call must be bounded
  (call.mjs has a hard deadline) and background launches fully detached, so nothing can hang a session.
- Put decision logic in `mod/Core` (unit-testable, MiniPhysics sim for movement); keep `mod/LlmPlayer` glue thin.
