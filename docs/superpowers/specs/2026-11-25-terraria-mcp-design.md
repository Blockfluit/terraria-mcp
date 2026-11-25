# Terraria MCP — Design

Date: 2026-11-25
Status: draft, awaiting approval (builder-checked)

## Intent

Let an LLM play Terraria as the player, through an MCP server. The LLM decides *what* to do
(explore, gather, build, fight, craft); our code handles *how* — especially movement, which
must be "smart" (pathfinding, jumping, dropping, digging through obstacles) because an LLM
thinking for several seconds per step cannot steer a real-time platformer by itself.

Why (user's words): "me being able to play the game but also have AI control my player."
This is **shared control of the user's own character, on the user's real saves**: the user
plays normally and can hand the controls to the AI (and take them back) at any time. Driven
from an MCP client such as Claude Code. Singleplayer first; multiplayer is a later extension
the architecture should not block.

## Goals

- An MCP client can connect, observe the world, and issue high-level actions that the game
  executes for the player character.
- Smart movement: `move_to` a tile or entity using A* over the real tile grid with a
  movement model that matches Terraria physics closely enough to follow reliably.
- Observation designed for an LLM: compact text (ASCII tile map + structured lists), not
  raw dumps.
- Long-running actions are asynchronous tasks the LLM can poll, cancel, and get a clear
  success/failure reason from.
- **Shared control:** an in-game hotkey toggles AI control on/off; any real keyboard/mouse
  input from the user while the AI is driving immediately takes control back (cancels the
  task, AI off). Clear on-screen indicator of who is in control.
- **Fair vision:** the AI only sees what the player could — see Observation rules.
- Basic reflexes so the player doesn't die while the LLM is thinking (auto-defend,
  auto-heal), toggleable.

## Non-goals (v1)

- Multiplayer / teammate mode (keep the door open; don't build it).
- Vision/screenshot-based play.
- Vanilla (non-tModLoader) Terraria.
- Full coverage of every game system (fishing, wiring, boss strategies, NPC housing
  validation). v1 covers: move, mine, chop, place, fight, pick up, craft, use items,
  inventory/equipment management, simple NPC interaction.

## Anti-goals (failure even if it "works")

- Cheating: no teleporting, spawning items, ignoring tool power/reach, or editing tiles
  without the player actually doing it. Actions go through player inputs or the same game
  calls a player action triggers, with the same range/tool/cooldown limits.
- The AI fighting the user for the controls, or acting when the user didn't hand over control.
- Wrecking the user's stuff: digging out their houses, breaking chests/furniture, hurting
  town NPCs.
- Crashing or corrupting the user's world/character saves.
- Flooding the LLM with huge observations that blow its context.

## Constraints / environment

- Windows host runs Terraria via Steam at
  `C:\Program Files (x86)\Steam\steamapps\common\Terraria`. tModLoader (free, separate
  Steam app, 1.4.4, .NET 8) must be installed.
- .NET 8 SDK is **not** installed on Windows; required to build the mod (tModLoader builds
  mods from `Documents\My Games\Terraria\tModLoader\ModSources`).
- Dev environment is WSL2 in **NAT** networking mode (Windows host reachable at the default
  gateway, found via `ip route`). The MCP server runs in WSL (Node 24 available).
- Plays on the user's real character/world → guardrails required (see Safety).
- Terraria game state is not thread-safe: all game reads/writes must happen on the main
  game thread.

## Approaches considered

1. **tModLoader mod + MCP bridge** — *chosen by the user.* Exact game state, frame-accurate
   control, in-game pathfinding. Cost: requires tModLoader + .NET SDK.
2. Screen capture + simulated input on vanilla — works unmodded but blind to the tile grid;
   slow, error-prone, smart movement infeasible.
3. Headless network client against a dedicated server — real data without client mods, but
   requires reimplementing protocol and physics; brittle across game updates.

## Architecture

```
MCP client (Claude Code)
   │ stdio (MCP)
MCP server — TypeScript, @modelcontextprotocol/sdk, in WSL
   │ TCP, newline-delimited JSON  (WSL → Windows host IP : 7878)
tModLoader mod "LlmPlayer" — C#, inside Terraria
   ├─ Bridge: background TCP listener → command queue → main thread
   ├─ Observer: builds state/map/entity snapshots
   ├─ Task runner: one active long-running task, ticked each frame
   ├─ Controller: sets player controls (ModPlayer.SetControls / cursor override)
   ├─ Navigation: A* pathfinder + path follower
   └─ Reflexes: auto-defend / auto-heal / interrupt reporting
```

### Wire protocol (mod ⇄ MCP server)

Request: `{"id": 1, "method": "move_to", "params": {...}}\n`
Response: `{"id": 1, "result": {...}}` or `{"id": 1, "error": {"code": "...", "message": "..."}}`
Events (unsolicited, optional): `{"event": "task_finished" | "took_damage" | "died", ...}`

Mod listens on a configurable port (default 7878). Bind address configurable; default
`0.0.0.0` so WSL (NAT) can reach it, and the mod only accepts one client at a time.
(Alternative the user may prefer: WSL mirrored networking + bind 127.0.0.1.)
MCP server finds the host via `TERRARIA_HOST` env var, else the WSL default gateway.

### Threading

The listener thread only parses JSON and enqueues. A `ModSystem` hook on the main thread
(e.g. `PostUpdateEverything`) drains the queue, executes instant commands immediately and
starts/replaces tasks, then ticks the active task. Responses are written back from the
main thread through a thread-safe outbound queue.

### Navigation (the "smart walking")

- Pathfinder is a **pure C# library** over an `ITileGrid` abstraction (solid, platform,
  liquid, hazard, minable + hardness) so it can be unit-tested without the game.
- Nodes are tile positions where the player (2×3 tiles) can stand or be mid-move. Edges:
  walk, step-up (1-tile auto-step), jump (height/arc derived from the player's current jump
  stats and accessories), fall (bounded by safe-fall distance unless the player has
  fall-damage immunity), drop through platforms, swim, climb ropes/vines, and **dig**
  (cost scaled by block hardness and current pickaxe power; impossible if pickaxe too weak
  or the tile is protected), and **build** (Claude's call): place a platform/block from
  inventory to bridge a gap or step up out of a shaft, using the cheapest available
  dirt/stone/wood/platforms. Build edges cost more than walking, are disabled when no
  suitable items are held, and can be turned off (`allow_build=false`).
- Costs prefer walking over digging, avoid lava/spikes/deep water, and penalize long falls.
- Search is bounded (node budget, radius) and runs incrementally across frames if needed so
  it never stalls the game.
- Path follower converts the path into per-frame control inputs (hold left/right, timed
  jump presses, mine-ahead), monitors progress, and **re-plans** when stuck (no progress for
  N frames), when the world changes along the path, or when knocked back. After K failed
  re-plans the task fails with a reason ("blocked by unbreakable block at x,y").

### Task model

At most one active task. Starting a new task cancels the current one. Each task reports
`running | succeeded | failed | cancelled` with a human-readable reason and progress
(e.g. distance remaining). The MCP layer offers both "start and return" and an optional
`wait_for_task(timeout)` so the LLM can block until done or interrupted.

### Control handoff

- States: `user` (default) and `ai`. Hotkey (tModLoader keybind, default `F8`) toggles.
  MCP tool `request_control` cannot take control by itself; it only succeeds if the user
  has toggled to `ai` (Claude's call — the user always grants control).
- While `ai`: the mod overrides controls each frame. If any real user input is detected
  (keys, mouse buttons, mouse movement beyond a small threshold), control flips to `user`,
  the active task is cancelled, and a `control_lost` event is sent.
- While `user`: observation tools work; action tools return error `user_has_control`.
- On-screen indicator (small text/icon) shows "AI driving: <task>" vs. nothing.

### Safety (real saves)

- Before the first AI action of a session, trigger a normal save and copy the world and
  player files to a timestamped backup folder (keep last N).
- Protected tiles that mining/digging/pathfinding must never break: containers, furniture
  and other multi-tile objects, doors, crafting stations, anything inside a valid NPC house,
  and blocks within a small radius of the spawn point (configurable). Pathfinder treats them
  as unbreakable.
- Never target town NPCs or critters with attacks; reflexes only hit hostile NPCs.
- Never sell, trash, or drop items unless the LLM explicitly asks with the item named.

### Reflexes

Toggleable via `set_reflexes`: auto-attack hostile enemies in reach while idle or moving,
quick-heal below a health threshold, retreat is *not* automatic (LLM decides). Any reflex
firing, damage taken, or death is reported in the next observation/task status.

Singleplayer-only option (Claude's call, off by default): `pause_when_idle` — while in
`ai` control, pause the game whenever no task is running, so the LLM can think without the
world moving. Never pauses while the user has control.

## Observation rules (fair vision)

- A tile is visible to the AI if it is revealed on the player's map (`Main.Map`) **or** is
  currently lit and on/near screen. Unrevealed tiles show as `?` in `get_map` and are
  treated as unknown-cost by the pathfinder (passable estimate, re-plan when revealed).
- `find` searches only visible tiles; Spelunker/Hunter/Dangersense buffs widen what is
  visible exactly as they do for the player.
- `get_entities` lists only NPCs/items within roughly screen range and visible (lit or
  revealed by buffs like Hunter).

## MCP tools (v1)

Observation
- `get_state` — position (tiles), health/mana/defense, buffs, held item, time of day,
  biome, depth layer, current task status, recent events (damage, kills, pickups).
- `get_map(radius≤40)` — ASCII grid centered on the player with a legend
  (`@` player, `#` solid, `.` air, `=` platform, `~` water, `L` lava, `T` tree, letters for
  ores/notable tiles listed in the legend) plus coordinates of the top-left corner.
- `get_entities(radius)` — nearby NPCs/enemies (name, id, hp, position, hostile), dropped
  items.
- `find(category, name?, radius)` — nearest ore/tree/chest/crafting station/NPC/item.
- `get_inventory` — inventory, hotbar, equipment, ammo, coins.
- `list_recipes(craftable_only=true)` — given current inventory and nearby stations.

Actions (start tasks unless noted)
- `move_to(x, y | entity_id, allow_dig=true)`, `explore(direction, distance)`
- `mine(x, y)`, `mine_area(x1,y1,x2,y2)`, `chop_tree(x, y | nearest)`
- `place(item, x, y)`
- `attack(entity_id | "nearest")` — picks a suitable weapon, aims, keeps distance for ranged
- `pick_up_items(radius)`
- `use_item(slot | item, target?)` — potions, tools, summons
- `craft(item, count)` (instant if station in range, else fails with which station is needed)
- `equip(item)`, `select_hotbar(slot)` (instant)
- `talk_to_npc(entity_id)` / shop interactions — minimal in v1
- `get_control` (who is driving), `request_control` (see Control handoff)
- `cancel_task`, `wait_for_task(timeout_s)`, `set_reflexes(...)`, `wait(seconds)`

## v1 done when (Claude's call)

Starting from a fresh character on a new world with the user handing control to the AI,
the LLM — using only these tools — can: chop trees, craft a workbench and wooden tools,
mine copper/iron it finds by exploring, craft a pickaxe upgrade, return to spawn via
`move_to`, and survive the first night with reflexes on. Separately: the user can take
control back instantly at any moment, and the AI never breaks protected tiles.

Build order follows that scenario: bridge + handoff + `get_state`/`get_map` → `move_to`
→ mine/chop/pick up → inventory/craft/place → attack + reflexes → the rest.

## Testing

- Pathfinder: unit tests (xUnit) on hand-built grids — jumps of exact max height, gaps,
  platforms, required digging, unreachable targets, lava avoidance.
- Mod bridge/commands: manual in-game test checklist plus a debug overlay drawing the
  current path.
- MCP server: tests against a fake mod socket (vitest).

## Repo layout (Claude's call)

```
mod/LlmPlayer/          tModLoader mod source (copied/symlinked into ModSources to build)
mod/Navigation/         pure C# pathfinding library (+ Navigation.Tests)
mcp-server/             TypeScript MCP server
docs/
```

## Left to the builder

- Exact tModLoader hooks for input override and cursor aiming; whether mining uses
  simulated use-item with cursor at target or the equivalent `Player.PickTile` call under
  the same reach/power/cooldown rules.
- How to detect "real user input" robustly (raw keyboard/mouse state vs. game triggers).
- Physics constants for the movement model (verify against game source/decompile).
- Map legend details and size budgets.
- How the mod source gets into `ModSources` from WSL (copy script vs. symlink).
