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
- The main session owns the play instance (instance 0): it plays, tests in game, merges agent branches into `feat/v1`,
  runs all checks, commits and pushes.
- Checks before every commit:
  `export PATH="$HOME/.dotnet:$PATH"; dotnet test mod/Core.Tests`,
  `cd mcp-server && npm test && npm run build`, `scripts/check-mod.sh main`.
- Deploy to the game: `scripts/dev-reload.sh` (saves and exits the running game, builds, relaunches through Steam,
  auto-loads the same character and world, takes control; ~25 s). Needs `DevCommands` on in the mod config.
- Logs: `C:\Program Files (x86)\Steam\steamapps\common\tModLoader\tModLoader-Logs\client.log` (with test instances
  running, a client started later logs to `client2.log`...: `scripts/instance.sh log 0` / `log N` finds the right one).
- Instance roles (user decision): exactly ONE instance plays to finish the game: instance 0, the user's real world,
  with the full UI, normal speed and completely fair play. Every other instance is purely for development: no UI
  needed, run it as fast and cheap as possible (no rendering, max timescale, dev setup commands), never fair-play
  bound, and its world is a throwaway copy. Dev/cheat commands must refuse to run in instance 0.
- Test instances (`scripts/instance.sh`, see README): instance 0 = the user's main game on 7878 with the real world
  "The Weird Fjord" (only `dev-reload.sh` touches it). Instances 1, 2, ... are extra clients on port 7878+N with
  throwaway copies of the player/world in `%LOCALAPPDATA%\LlmPlayerInstances\<N>`; use them to test features in
  parallel and to try things that could hurt the real world. Order for a new build: `scripts/instance.sh build`
  (packages into `<instances>/_build`, never the main game's .tmod) → `scripts/instance.sh reload N` → test with
  `node scripts/play/call.mjs --port "$(scripts/instance.sh port N)" ...` → when good, merge and `dev-reload.sh` for instance 0.
- Only the main session starts, stops or reloads instances, and it assigns them: an agent may be told "use
  instance N" and then only calls the bridge on that port (reads and actions), never starts/stops/reloads anything.
  Reasons: every client costs ~1-2 GB RAM and a GPU context, starting one can race another for ports and log files,
  and `instance.sh build` writes the shared ModSources/LlmPlayer folder, so builds must be serialized by one owner.
- Play helpers for the main session: `scripts/play/` (see its README). Every loop and bridge call must be bounded
  (call.mjs has a hard deadline) and background launches fully detached, so nothing can hang a session.
- Put decision logic in `mod/Core` (unit-testable, MiniPhysics sim for movement); keep `mod/LlmPlayer` glue thin.
