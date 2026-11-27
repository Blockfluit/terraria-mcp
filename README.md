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
  hostiles, lists every living boss with its health (like the vanilla boss bar; its position only while visible), and names events such as
  a blood moon or goblin army. `get_inventory` tags items as weapon (with damage), heal, ammo, consumable,
  placeable, or pickaxe/axe/hammer power.
- **Move smartly.** `move_to` plans an A* route over the real tile grid. The route walks, jumps,
  drops through platforms, swims, digs with your best pickaxe, and places platforms to cross gaps
  or climb out of shafts. It opens doors in its way like a player does (right-click, normal reach)
  and closes them again behind itself, so it can walk in and out of houses without letting zombies in. The route-following code is tested against a physics simulation of
  vanilla movement.
- **Explore and dig.** `explore` walks toward the farthest visible ground in a direction. When there's
  nothing visible to walk to, it digs with your best pickaxe: a 2-wide shaft straight down (you drop as the floor
  goes, and `move_to` climbs back out with platforms) or a 3-tall tunnel left or right. It only digs tiles it can
  see. It stops with a reason before lava or water next to the hole, protected tiles, tiles too hard for your
  pickaxe, or a drop longer than a safe fall. In the dark it places torches from your inventory, like you would:
  it lights a tile it needs to see, and it places a torch every ~10 tiles underground. The result reports tiles
  covered, tiles dug, torches placed and why it stopped. `allow_dig` and `torches` turn each part off.
- **Gather, build, fight.** It can mine, chop trees, pick up items, place, craft (only with real
  ingredients and stations), equip, use items, attack, and talk to NPCs.
- **Mine what it needs.** `mine_vein` mines one whole ore vein from any of its tiles, or a set number of
  tiles of any block (`max`, e.g. 20 Stone), without the surrounding rock. `find` locates ore and,
  with `category: "block"` and a name, blocks like Stone, Sand or Clay. Mining walks to a spot in reach,
  builds a platform to stand on in a shaft, or digs toward a tile no open spot can reach.
- **Explore caves and loot them.** `explore_caves` explores underground through the open cave space, frontier by
  frontier: it walks to a known ledge next to the nearest reachable edge between known open space and unknown
  tiles (digging only through walls up to 3 tiles thick to reach the next cave), lights the dark opening with your
  torches, looks, and moves on. Started on the surface it first goes underground (walking or digging a shaft down).
  When a cave pocket is exhausted it tunnels toward the nearest large unrevealed region (down preferred, else
  sideways; at most 40 tiles per tunnel, never toward known lava, through liquids or into unsafe drops) and explores
  again as soon as it breaks into open space (`tunnels`/`tunnelled` in the result). It never walks through lava, skips
  openings next to it and keeps to safe falls. It stops when something you asked for comes into view (`stop_on`:
  life crystals, chests, the Enchanted Sword shrine, pots, ore, altars, orbs, Fallen Stars), when time is up or when
  nothing reachable and no safe tunnel is left, and reports what it found, tiles revealed, torches placed and every
  Life Crystal seen (`lifeCrystals`; a Life Crystal coming into view stops it with its coordinates).
  `collect_pots` breaks pots on the way. `find` with `category: "valuable"` lists the same things by name
  ("Life Crystal", "Pot", "Locked Gold Chest", "Demon Altar", "Shadow Orb (don't break)"…). `collect` breaks a pot,
  life crystal, life fruit or the sword in stone with your pickaxe and picks up what drops. `loot_chest` opens a
  chest, presses *Loot All* and closes it again.
- **Fight without trading hits.** `attack` predicts when an enemy is about to touch the player (from its hitbox
  and velocity) and backs off or jumps instead, never into a hole or lava, while it keeps swinging or shooting.
  Melee becomes hit-and-run. `target: "all"` clears every visible hostile nearby in one call. Below a life
  threshold with no healing potion available, it retreats and reports "low life". The result reports kills,
  damage taken, duration and dodges. This movement is also tested against the physics simulation.
- **Fight bosses.** When the target is a boss (`target: "boss"` picks the nearest one in sight; out of sight it goes by
  the last sighting, never the real position), `attack` switches to arena mode. It never pathfinds and never leaves the arena
  (`arena: {x1, x2}`, by default the walkable ground or platform span around you, up to 50 tiles each way). With a bow
  it kites back and forth across the arena and shoots with lead aim. It dodges dashes sideways, by jumping, or by
  dropping through the platform under it to the row below. Servants and other minions about to reach you get shot
  first (`kill_minions`), then it goes back to the boss. If the boss leaves the screen it stays the target. The task
  fails with "boss fled" only when the boss despawns alive (the Eye of Cthulhu at dawn). `heal_below` raises the
  quick-heal threshold while the fight runs. When ammo runs out, the game picks the next stack of the same ammo
  type, and once none is left `attack` switches to another weapon. The result adds potions used, ammo left, and the
  boss's life left in percent if it survived. Build the arena first: `build_platforms` with 2-3 rows about 5 tiles
  apart, so you can jump between them. `get_boss_info` gives short facts about the Eye of Cthulhu and King Slime:
  summon item and recipe, spawn rules, when it leaves, and whether this world has beaten it. In a simulated fight
  against an Eye-of-Cthulhu-like dasher on a 3-level platform arena, kiting takes about 25-50% fewer hits than
  standing still.
- **Build bigger things.** `build_platforms` lays whole rows of platforms or blocks (bridges, floors,
  stacked platform rows): it walks along, stands on what it just built and places every tile in reach
  before moving on. Occupied tiles, NPC houses and tiles with nothing to attach to are skipped and
  reported. `mine_area` likewise skips protected or unminable tiles instead of failing.
- **House town NPCs.** `build_house` builds a valid NPC house with the player's own items and reach: a frame of
  blocks (Wood first), a door, background walls on every inside tile, a table, a chair and a torch. Without `x,y` it
  picks the nearest flat, clear spot it can see. Before it starts it works out the full bill of materials, crafts what
  it can (walls 4 per block, door, chair and table at a Work Bench in range; torches from Gel), and fails with exact
  counts if anything is still short. It builds from inside, walks out through the door, then runs vanilla's housing
  query and reports valid or vanilla's reason. `check_housing` runs that same query on any room, lists which town NPCs
  live in the world and which are homeless, and says which useful NPCs (Merchant, Nurse, Arms Dealer,
  Demolitionist, Dryad…) could move in now, or what they still need. `get_state` shows homeless NPCs and NPCs ready
  to move in.
- **Manage gear and supplies.** `equip` can target a specific accessory slot, and `unequip` takes
  items off. `craft` with `with_intermediates` crafts missing ingredients first from what you have
  (Sand → Glass → Bottle → Lesser Healing Potion). `buy` purchases from a town NPC's shop with your
  own coins, at the shop's real price.
- **Use chests and beds.** `open_chest` walks to a chest (or dresser, piggy bank, safe, ...) and opens
  it the way your right-click does; only then does the AI see what's inside. `chest_deposit` /
  `chest_withdraw` move items like shift-click, or use the chest's own *Deposit All*, *Quick Stack* and
  *Loot All* buttons, and never drop or delete an item that doesn't fit. Locked chests, trapped Dead
  Man's Chests and chests someone else has open are refused; keys are never used up. The chest closes
  when the AI is done (`close_chest`), starts another task, walks away, or hands control back.
  `use_bed` sets your spawn point at a bed, with the game's housing rules and failure messages.
- **Survive while thinking.** Reflexes auto-attack nearby hostiles and quick-heal while the LLM
  thinks. You can optionally pause the world while the AI is idle.
- **Keep playing unfocused.** While the AI drives, the game keeps running when the Terraria window
  isn't focused, so you can type in your terminal.

### Safety

The AI plays on your real saves, so the mod enforces these rules:

- It never cheats. There's no teleporting or item spawning. Every action uses your real controls,
  with normal reach, tool power and cooldowns.
- The AI may switch AI mode on itself (`request_control`), but only your hotkey takes control back, and it cancels the running task at once. After you take control back, the AI cannot take it again until you hand it over with the hotkey (or restart the game).
- The first time you hand over control in a session, it backs up your `.plr` and `.wld` files to
  `Documents/My Games/Terraria/tModLoader/LlmPlayerBackups/<timestamp>/`. It keeps the newest 5.
- It never breaks chests, furniture, doors, crafting stations, anything inside an NPC house, or
  blocks near spawn. It never attacks town NPCs or critters. Natural cave loot is the exception: pots, life
  crystals, life fruit and the Enchanted Sword shrine's sword may be broken (but not in an NPC house or near spawn).
  Shadow Orbs, Crimson Hearts and altars are never broken: breaking an orb has world effects.

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
| QuickHealThreshold | 0.35 | Reflex: quick-heal below this fraction of max life (an `attack` with `heal_below` raises it while it runs) |
| PauseWhenIdle | off | Singleplayer only: pause while the AI is driving but has no task |
| ShowPathOverlay | on | Draw the planned path in game |
| DevCommands | off | Allow `dev_save_and_exit` (save and quit the game) for `scripts/dev-reload.sh`, see below |

`set_reflexes` changes the reflex options at runtime.

## Development

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test mod/Core.Tests        # pathfinding, follower physics sim, map, tasks, bridge, ...
cd mcp-server && npm test         # MCP server
scripts/build-mod.sh              # compile and package the mod
```

The design and implementation plan are in `docs/superpowers/`.

### Development: one-command reload

tModLoader refuses to build the mod while it's loaded, and only the menus can load a world. From WSL,

```bash
scripts/dev-reload.sh
```

does the whole loop: it saves and quits the game, rebuilds the mod, relaunches tModLoader through Steam,
re-enters the same character and world, then calls `request_control` to switch AI mode on.

Enable it once: in game, *Workshop → Manage Mods → LLM Player → Config*, turn on **DevCommands**. It's off by
default because anyone who can reach the bridge port (BindAddress `0.0.0.0` listens on your LAN too) could then
close your game. It always saves first, but set BindAddress to `127.0.0.1` if you use mirrored networking.

How it works:

1. The script finds the game like the MCP server does (`TERRARIA_HOST`, else the WSL default gateway; port
   `TERRARIA_PORT`, default 7878). It sends the bridge command `dev_save_and_exit {"reload":true}` (not an MCP
   tool). The mod runs the same code as the in-game *Save & Exit* button: the player and the world are saved.
   It writes `LlmPlayerAutoload.json` to `Documents/My Games/Terraria/tModLoader/` and exits the way the title
   screen's *Exit* button does. The command only works in single player, and it refuses while an item is held
   on the mouse cursor. At the title screen it just exits.
2. The script waits up to 60 s for the port to close and the tModLoader process to end. It never kills the
   game. If the game is running but the bridge doesn't answer, the script stops without doing anything.
3. `scripts/build-mod.sh`, then `steam://rungameid/1281930`.
4. At the title screen, once per launch, the mod reads the autoload file and deletes it before doing anything,
   so it can't loop. It then picks that player and world and presses *Play* for you. If something doesn't
   match (unknown name, Journey mode mismatch, a file older than 10 minutes), it shows the reason on screen and
   loads nothing.
5. The script waits up to 5 min for `get_state` to answer, prints it, and calls `request_control`. A freshly
   launched game lets the AI take control, so the AI drives right away. Press **Insert** at any time to take
   control back. After that, only your own Insert hands control over again in that game session. If the call
   is refused (for example, the player is dead), the script tells you to press Insert.

Options: `--player NAME --world NAME` enters a different save, which also works when the game isn't running.
`--no-build` skips the build. `--no-launch` stops after the build. `scripts/test-dev-reload.sh` checks the
script's argument handling.

## Status

v1. Everything that doesn't need the game is covered by automated tests. In-game behaviour still
needs a full playtest. Multiplayer isn't supported yet.
