# terraria-mcp

Let an LLM play Terraria **as your character**. You play normally. When you press **Insert**, the AI
takes over through an [MCP](https://modelcontextprotocol.io) server, and a pulsing border plus an "AI IS PLAYING"
banner show it has the controls. Press **Insert** again to take control back.

```
MCP client (e.g. Claude Code)
   │ stdio
mcp-server/   TypeScript MCP server (runs in WSL / Linux / Windows)
   │ TCP, newline-delimited JSON (port 7878)
mod/LlmPlayer tModLoader mod inside Terraria
   └─ mod/Core  pure C# library: pathfinding, path following, map rendering, tasks
```

## What the AI can do

- **See fairly.** It only sees tiles you've explored or that are lit near the screen, shown as an
  ASCII map with a legend. Unknown tiles show as `?`. Spelunker, Hunter and Dangersense potions
  widen its view the same way they widen yours.
- **Read a fight at a glance.** `get_entities` shows each enemy's velocity, contact damage and defense,
  flags bosses (always listed first), and includes visible hostile projectiles. `get_state` counts nearby
  hostiles, lists every living boss with its health (like the vanilla boss bar), and names events such as
  a blood moon or goblin army. `get_inventory` tags items as weapon (with damage), heal, ammo, consumable,
  placeable, or pickaxe/axe/hammer power.
- **Move smartly.** `move_to` plans an A* route over the real tile grid. The route walks, jumps,
  drops through platforms, swims, digs with your best pickaxe, and places platforms to cross gaps
  or climb out of shafts. The route-following code is tested against a physics simulation of
  vanilla movement.
- **Gather, build, fight.** It can mine, chop trees, pick up items, place, craft (only with real
  ingredients and stations), equip, use items, attack, and talk to NPCs.
- **Fight without trading hits.** `attack` predicts when an enemy is about to touch the player (from its hitbox
  and velocity) and backs off or jumps instead, never into a hole or lava, while it keeps swinging or shooting.
  Melee becomes hit-and-run. `target: "all"` clears every visible hostile nearby in one call. Below a life
  threshold with no healing potion available, it retreats and reports "low life". The result reports kills,
  damage taken, duration and dodges. This movement is also tested against the physics simulation.
- **Build bigger things.** `build_platforms` lays whole rows of platforms or blocks (bridges, floors,
  stacked platform rows): it walks along, stands on what it just built and places every tile in reach
  before moving on. Occupied tiles, NPC houses and tiles with nothing to attach to are skipped and
  reported. `mine_area` likewise skips protected or unminable tiles instead of failing.
- **Manage gear and supplies.** `equip` can target a specific accessory slot, and `unequip` takes
  items off. `craft` with `with_intermediates` crafts missing ingredients first from what you have
  (Sand → Glass → Bottle → Lesser Healing Potion). `buy` purchases from a town NPC's shop with your
  own coins, at the shop's real price.
- **Survive while thinking.** Reflexes auto-attack nearby hostiles and quick-heal while the LLM
  thinks. You can optionally pause the world while the AI is idle.
- **Keep playing unfocused.** While the AI drives, the game keeps running when the Terraria window
  isn't focused, so you can type in your terminal.

### Safety

The AI plays on your real saves, so the mod enforces these rules:

- It never cheats. There's no teleporting or item spawning. Every action uses your real controls,
  with normal reach, tool power and cooldowns.
- It never takes control on its own. Only your hotkey hands control over, and only the hotkey takes it back.
- The first time you hand over control in a session, it backs up your `.plr` and `.wld` files to
  `Documents/My Games/Terraria/tModLoader/LlmPlayerBackups/<timestamp>/`. It keeps the newest 5.
- It never breaks chests, furniture, doors, crafting stations, anything inside an NPC house, or
  blocks near spawn. It never attacks town NPCs or critters.

## Setup

Prerequisites:

- Terraria with **tModLoader** (free on Steam). Launch it once and open *Workshop → Develop Mods*
  so `ModSources` exists.
- The **.NET 8 SDK on Windows**: `winget install Microsoft.DotNet.SDK.8`.
- **Node.js 20+**, for the MCP server.
- Optional: the .NET 8 SDK in WSL/Linux, to run the Core tests.

### 1. Build the mod

From WSL:

```bash
scripts/build-mod.sh
```

The script copies the mod into your tModLoader `ModSources` folder, then builds it with the
Windows `dotnet`. The script finds the Documents folder itself; set `TML_MODSOURCES` to override.
`LlmPlayer.tmod` lands in tModLoader's `Mods` folder. Enable **LLM Player** in *Workshop → Manage
Mods* if it isn't already enabled.

### 2. Build and register the MCP server

```bash
cd mcp-server && npm ci && npm run build
claude mcp add terraria -- node "$PWD/dist/index.js"
```

The server finds the game at `TERRARIA_HOST` (default: the WSL default gateway, i.e. the Windows
host) and `TERRARIA_PORT` (default 7878). The first time you load a world, Windows Firewall asks
about tModLoader listening on port 7878. Allow it on private networks.

### 3. Play

1. Load a world in tModLoader.
2. Press **Insert** (rebindable under *Settings → Controls*, mod keybinds). A pulsing cyan border and an
   "AI IS PLAYING" banner appear. Switch to your MCP client however you like: while the AI drives your own
   keys and mouse do nothing in game, and the game keeps running while its window is unfocused.
3. Ask your MCP client to play, for example: *"Gather wood, make a workbench and wooden tools, then
   find some copper."*
4. Press **Insert** again (in the Terraria window) to take control back. Nothing else does: if the character
   dies, its current task is cancelled but the AI keeps control. Leaving the world also hands control back.

A good loop for the LLM is `get_state` → `get_map` → an action → `wait_for_task`.

## Configuration

In game: *Workshop → Manage Mods → LLM Player → Config*.

| Option | Default | Meaning |
|---|---|---|
| Port | 7878 | TCP port the mod listens on |
| BindAddress | 0.0.0.0 | Use `127.0.0.1` if your MCP client runs on Windows or WSL uses mirrored networking |
| ProtectSpawnRadius | 20 | Blocks within this many tiles of spawn are never broken |
| BackupKeep | 5 | How many save backups to keep |
| AutoDefend | on | Reflex: attack hostiles in reach (or that just hit you) while idle, moving, gathering, placing or picking up; the task pauses and resumes after the fight (not during `attack`) |
| QuickHealThreshold | 0.35 | Reflex: quick-heal below this fraction of max life |
| PauseWhenIdle | off | Singleplayer only: pause while the AI is driving but has no task |
| ShowPathOverlay | on | Draw the planned path in game |

`set_reflexes` changes the reflex options at runtime.

## Development

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test mod/Core.Tests        # pathfinding, follower physics sim, map, tasks, bridge, ...
cd mcp-server && npm test         # MCP server
scripts/build-mod.sh              # compile and package the mod
```

The design and implementation plan are in `docs/superpowers/`.

## Status

v1. Everything that doesn't need the game is covered by automated tests. In-game behaviour still
needs a full playtest. Multiplayer isn't supported yet.
