# terraria-mcp

Let an LLM play Terraria **as your character**. You play normally. When you press **F8**, the AI
takes over through an [MCP](https://modelcontextprotocol.io) server. Touch the keyboard or mouse
and control comes straight back to you.

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
- **Move smartly.** `move_to` plans an A* route over the real tile grid. The route walks, jumps,
  drops through platforms, swims, digs with your best pickaxe, and places platforms to cross gaps
  or climb out of shafts. The route-following code is tested against a physics simulation of
  vanilla movement.
- **Gather, build, fight.** It can mine, chop trees, pick up items, place, craft (only with real
  ingredients and stations), equip, use items, attack, and talk to NPCs.
- **Survive while thinking.** Reflexes auto-attack nearby hostiles and quick-heal while the LLM
  thinks. You can optionally pause the world while the AI is idle.
- **Keep playing unfocused.** While the AI drives, the game keeps running when the Terraria window
  isn't focused, so you can type in your terminal.

### Safety

The AI plays on your real saves, so the mod enforces these rules:

- It never cheats. There's no teleporting or item spawning. Every action uses your real controls,
  with normal reach, tool power and cooldowns.
- It never takes control on its own. Only your hotkey hands control over.
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
2. Press **F8** (rebindable under *Controls → Mod Controls*). "AI driving" appears at the top of
   the screen.
3. Ask your MCP client to play, for example: *"Gather wood, make a workbench and wooden tools, then
   find some copper."*
4. Press any key or click to take control back.

A good loop for the LLM is `get_state` → `get_map` → an action → `wait_for_task`.

## Configuration

In game: *Workshop → Manage Mods → LLM Player → Config*.

| Option | Default | Meaning |
|---|---|---|
| Port | 7878 | TCP port the mod listens on |
| BindAddress | 0.0.0.0 | Use `127.0.0.1` if your MCP client runs on Windows or WSL uses mirrored networking |
| ProtectSpawnRadius | 20 | Blocks within this many tiles of spawn are never broken |
| BackupKeep | 5 | How many save backups to keep |
| AutoDefend | on | Reflex: attack hostiles in reach while idle or moving |
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
