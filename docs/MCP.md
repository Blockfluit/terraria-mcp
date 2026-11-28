# What the MCP server provides

`mcp-server/` is the MCP server an LLM client (e.g. Claude Code) talks to. It owns no game logic: every tool except
`wait_for_task` forwards to a bridge command of the same name in the LlmPlayer mod and returns the mod's reply, with
the mod's events attached. This page covers the whole surface. The per-tool reference (descriptions, parameter
schemas, annotations) is generated from the code into [MCP-tools.md](MCP-tools.md).

| MCP feature | Provided? |
|---|---|
| Tools | Yes ([index below](#tools), [full reference](MCP-tools.md)) |
| Server instructions | Yes (`initialize` → `instructions`, text in [MCP-tools.md](MCP-tools.md#server-instructions)) |
| Resources | **No**: no `resources` capability; `resources/list` answers "Method not found" |
| Prompts | **No**: no `prompts` capability; `prompts/list` answers "Method not found" |
| Sampling, elicitation, logging, completions | No |
| Notifications to the client | None; game events ride along in tool results (see [Events](#events)) |

Advertised capabilities are just `{"tools": {"listChanged": true}}` (the SDK default; the tool list never changes at
runtime).

## Server

- **Name / version:** `terraria` `0.1.0` (`SERVER_INFO` in `src/tools.ts`).
- **Transport:** stdio only (`StdioServerTransport`). stdout carries the protocol; logs go to stderr as
  `[terraria-mcp] ...`. The process exits on SIGINT, SIGTERM or when stdin closes.
- **Register:** `claude mcp add terraria -- node "$PWD/dist/index.js"` after `npm ci && npm run build`.
- **Behind it:** a TCP connection to the mod (`src/connection.ts`): newline-delimited JSON, requests
  `{"id", "method", "params"}`, replies `{"id", "result"}` or `{"id", "error": {"code", "message"}}`, and unsolicited
  `{"event": "<name>", ...payload}` lines. All JSON is camelCase. It connects lazily on the first tool call and again
  on the next call after a drop. A connect attempt times out after 3 s; each bridge call after 10 s (error `timeout`).

### Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `TERRARIA_HOST` | WSL default gateway from `/proc/net/route` (the Windows host), else `127.0.0.1` | Host running tModLoader |
| `TERRARIA_PORT` | `7878` | Bridge port (must match the mod config's `Port`) |

There are no others. The mod side is configured in game (*Workshop → Manage Mods → LLM Player → Config*): `Port`,
`BindAddress` (default `0.0.0.0`), `ProtectSpawnRadius`, `BackupKeep`, `AutoDefend`, `QuickHealThreshold`,
`PauseWhenIdle`, `ShowPathOverlay`, `DevCommands`.

## Tools

How every tool behaves:

- **Arguments** are validated twice: by the zod schema in `src/tools.ts` (MCP-level validation error), then by an
  optional cross-field `check` (e.g. "give exactly one of item or slot"), which returns a `bad_params` tool error
  without calling the mod. `undefined` arguments are stripped before forwarding.
- **Success** is one text content block holding the mod's JSON result. If events arrived since the last tool call
  they are added as `events: [...]` (an object result gets an `events` key; any other result becomes
  `{"result": ..., "events": [...]}`).
- **Failure** is `isError: true` with the text `<code>: <message>`, then (for `user_has_control`) a hint to call
  `request_control` or ask the user, then `events: [...]` when there are any.
- **Kinds:** *read-only* tools observe and work while the user drives (annotation `readOnlyHint: true`).
  *Async task* tools start a task in the mod and return at once; only one task runs at a time and a new one cancels
  the old, so follow with `wait_for_task` or poll `get_task`. *Instant action* tools act and return the outcome.
  All non-read-only tools carry `readOnlyHint: false, destructiveHint: false`, and all of them except
  `request_control` fail with `user_has_control` while the user has control.
- **`wait_for_task`** is the only tool implemented in the server: it polls the mod's `get_task` every 250 ms until
  the current task is no longer running or `timeout_s` (default 30, max 300) passes, and returns
  `{timed_out, report}` (`report: null` with a note if no task has ever run). Task status arrives as a string
  (`running`, `succeeded`, `failed`, `cancelled`); a numeric enum from the mod is normalized.

The index below is generated; do not edit between the markers.

<!-- BEGIN GENERATED TOOL INDEX (npm run docs) -->

46 tools. Full parameters and descriptions: [MCP-tools.md](MCP-tools.md).

**Control**

| Tool | Kind | Purpose |
|---|---|---|
| [`get_control`](MCP-tools.md#get_control) | read-only | Who drives the character right now: the user or the AI. |
| [`request_control`](MCP-tools.md#request_control) | action | Switch AI mode on; refused after the user took control back with Insert, or while dead. |

**Observation**

| Tool | Kind | Purpose |
|---|---|---|
| [`get_state`](MCP-tools.md#get_state) | read-only | Player status (position, life, buffs, time, biome, bosses, housing, last deaths) plus recent events. Start every decision here. |
| [`get_map`](MCP-tools.md#get_map) | read-only | Fair-vision ASCII map around the player with a legend ('?' = never seen). |
| [`get_entities`](MCP-tools.md#get_entities) | read-only | Visible enemies, town NPCs, critters, dropped items and hostile projectiles, bosses first. |
| [`find`](MCP-tools.md#find) | read-only | Nearest visible ores, trees, chests, stations, beds, NPCs, items, blocks, cave valuables or plants. |
| [`inspect`](MCP-tools.md#inspect) | read-only | Exactly what is on one visible tile (tile, wall, liquid, protection, shape, pick power). |
| [`get_inventory`](MCP-tools.md#get_inventory) | read-only | Hotbar, inventory, armor, accessories, ammo and coins, with weapon/tool/consumable tags. |
| [`list_recipes`](MCP-tools.md#list_recipes) | read-only | Recipes craftable now, or every recipe for a named item. |
| [`check_housing`](MCP-tools.md#check_housing) | read-only | Vanilla housing query for a room, plus residents, homeless NPCs and who could move in. |
| [`get_boss_info`](MCP-tools.md#get_boss_info) | read-only | Static prep facts for Eye of Cthulhu and King Slime (summon, recipe, stats, tips, defeated). |

**Tasks**

| Tool | Kind | Purpose |
|---|---|---|
| [`get_task`](MCP-tools.md#get_task) | read-only | The running task and the last finished one, with status, reason, progress and result. |
| [`wait_for_task`](MCP-tools.md#wait_for_task) | read-only, blocks | Block until the current task ends or timeout_s passes (MCP-server side, polls get_task). |
| [`cancel_task`](MCP-tools.md#cancel_task) | instant action | Stop the running task. |
| [`wait`](MCP-tools.md#wait) | async task | Stand still for up to 60 s with reflexes active. |

**Movement and exploration**

| Tool | Kind | Purpose |
|---|---|---|
| [`move_to`](MCP-tools.md#move_to) | async task | A\* walk to a tile or entity: jumps, drops, swims, digs, builds platforms, opens doors, lights tunnels. |
| [`explore`](MCP-tools.md#explore) | async task | Travel a distance in a direction, digging a shaft or tunnel when nothing visible is walkable. |
| [`explore_caves`](MCP-tools.md#explore_caves) | async task | Systematic cave exploration frontier by frontier; stops when loot such as a Life Crystal comes into view. |
| [`door`](MCP-tools.md#door) | instant action | Open or close a door within reach. |

**Gathering and mining**

| Tool | Kind | Purpose |
|---|---|---|
| [`mine`](MCP-tools.md#mine) | async task | Mine one tile with the best pickaxe (also harvests herbs). |
| [`mine_area`](MCP-tools.md#mine_area) | async task | Mine a rectangle (max 200 tiles), skipping what can't or mustn't be mined. |
| [`mine_vein`](MCP-tools.md#mine_vein) | async task | Mine a whole ore vein, or N tiles of one block type, from any visible tile of it. |
| [`chop_tree`](MCP-tools.md#chop_tree) | async task | Chop a tree (given or nearest allowed) and collect the wood. |
| [`pick_up_items`](MCP-tools.md#pick_up_items) | async task | Walk over visible dropped items in a radius. |
| [`collect`](MCP-tools.md#collect) | async task | Break cave loot (pot, Life Crystal, Life Fruit, sword in stone) and pick up the drops. |
| [`blast`](MCP-tools.md#blast) | async task | Blow up a block with a thrown explosive after a full safety check of the blast area. |

**Building**

| Tool | Kind | Purpose |
|---|---|---|
| [`place`](MCP-tools.md#place) | async task | Place one block, platform, torch or piece of furniture. |
| [`build_platforms`](MCP-tools.md#build_platforms) | async task | Lay rows of platforms or blocks (bridges, floors, boss arenas). |
| [`build_house`](MCP-tools.md#build_house) | async task | Build a valid town-NPC house from your own materials, crafting what is missing. |

**Crafting and inventory**

| Tool | Kind | Purpose |
|---|---|---|
| [`craft`](MCP-tools.md#craft) | instant action | Craft an item with real ingredients and stations, optionally crafting intermediates first. |
| [`select_hotbar`](MCP-tools.md#select_hotbar) | instant action | Select a hotbar slot as the held item. |
| [`equip`](MCP-tools.md#equip) | instant action | Equip armor or an accessory (optionally into a given accessory slot). |
| [`unequip`](MCP-tools.md#unequip) | instant action | Take off armor or an accessory. |
| [`use_item`](MCP-tools.md#use_item) | async task | Use an item once (potion, tool, summon), optionally aimed; queues back to back. |
| [`swap_slots`](MCP-tools.md#swap_slots) | instant action | Swap two inventory slots (e.g. to order ammo). |
| [`drop_item`](MCP-tools.md#drop_item) | instant action | Throw items on the ground or trash them to free slots. |

**Combat and reflexes**

| Tool | Kind | Purpose |
|---|---|---|
| [`attack`](MCP-tools.md#attack) | async task | Fight a target, the nearest, every visible hostile, or a boss, with the weapon, ammo and strategy you choose. |
| [`set_reflexes`](MCP-tools.md#set_reflexes) | instant action | Configure auto-defend, quick-heal, idle pause, auto-defend weapon/ammo, platform reserve, trash list. |

**NPCs and shops**

| Tool | Kind | Purpose |
|---|---|---|
| [`talk_to_npc`](MCP-tools.md#talk_to_npc) | async task | Walk into talk range of a town NPC, open its chat and read its shop. |
| [`buy`](MCP-tools.md#buy) | async task | Buy items from a town NPC's shop with your own coins. |

**Chests and beds**

| Tool | Kind | Purpose |
|---|---|---|
| [`open_chest`](MCP-tools.md#open_chest) | async task | Walk to a container and open it; its contents become visible. |
| [`chest_deposit`](MCP-tools.md#chest_deposit) | instant action | Put items into the open chest (item, Deposit All, Quick Stack). |
| [`chest_withdraw`](MCP-tools.md#chest_withdraw) | instant action | Take items out of the open chest (item or Loot All). |
| [`close_chest`](MCP-tools.md#close_chest) | instant action | Close the open chest. |
| [`loot_chest`](MCP-tools.md#loot_chest) | async task | open_chest + Loot All + close_chest in one task. |
| [`use_bed`](MCP-tools.md#use_bed) | async task | Set the spawn point at a bed. |

<!-- END GENERATED TOOL INDEX -->

## Events

The mod pushes events at any time. The server buffers them (at most 1000; past that the oldest non-must-keep events
are dropped first) and attaches them to the **next** tool result of any tool, so every result carries what happened
since the previous call. Events the mod sends:

| Event | Payload | When |
|---|---|---|
| `task_finished` | `name, status, reason, progress` (`result` stripped, see below) | A task ended (succeeded, failed, cancelled) |
| `control_changed` | `driver: "user"\|"ai", reason` | Insert pressed, `request_control`, leaving the world |
| `player_died` | `text, killer, x, y` | The player died (the running task is cancelled; control stays with the AI) |
| `took_damage` | `amount, source, life, lifeMax` | Each hit taken |
| `reflex` | `kind: "auto_defend"` (`target, name, paused`), `"auto_defend_end"` (`resumed`), `"quick_heal"` (`item, lifeBefore, life, lifeMax`), `"surface"` / `"surface_end"` (`breath, breathMax, paused`/…), `"auto_trash"` (`trashed, count, slot, for`) | A reflex fired |
| `weapon_switch` | `by, from, to, message` | The chosen weapon/ammo became unusable (fallback) or works again |
| `inventory_full` | `item, stack, x, y, freeSlots, suggestion` | A drop could not be picked up |

`get_state` additionally has its own `recent events` text list (drained by each `get_state`) and `lastDeaths`
(never drained); those are part of the mod's result, not this stream.

### How `compactEvents` folds them (`src/events.ts`)

Seen in play, one call could carry ~100 events, so before attaching, the server shrinks them:

1. All `reflex` events with kind `auto_defend` / `auto_defend_end` become **one**
   `{event: "reflex", kind: "auto_defend", engagements, targets: {name: times}}` at the first one's position.
2. More than 3 (`DAMAGE_DETAIL_LIMIT`) `took_damage` events become **one**
   `{event: "took_damage", hits, total, sources: {source: damage}, minLife, life, lifeMax}`; up to 3 are listed one by one.
3. `task_finished` loses its `result` payload (`wait_for_task` / `get_task` already return it).
4. Everything else is kept in order. Past 25 (`MAX_EVENTS`) events, the oldest events **not** in `MUST_KEEP` are
   dropped and `{event: "omitted", count, note: "older events dropped"}` leads the list.

`MUST_KEEP` = `control_changed`, `task_finished`, `player_died`, `died`, `respawned`, `weapon_switch`: never folded
and never dropped by the caps (`died` and `respawned` are listed defensively; the mod currently emits deaths only as
`player_died` and sends no respawn event).

## Error codes

Mod errors arrive as `{"error": {"code", "message"}}` and become `isError` tool results `<code>: <message>`.
The message is written for the LLM and usually says what to do next.

| Code | Source | Meaning |
|---|---|---|
| `bad_params` | mod, server | Missing/invalid parameter, or a request that can't be done as asked (no such target, nothing visible nearby, refused chest such as `locked:` / `rigged` / `in_use:`, `chest_full`, `inventory_full` on a chest transfer, site not clear, ...) |
| `user_has_control` | mod | An action tool was called while the user drives. Call `request_control`; if refused, stop and ask the user |
| `not_in_world` | mod | At the main menu or loading |
| `not_visible` | mod | The target tile/entity is not visible to the player (fair vision) |
| `protected_tile` | mod | The target is protected (furniture, chest, door, station, NPC house, spawn area, or a `blast` radius containing such) |
| `missing_item` | mod | The required item or ingredients aren't in the inventory (message lists counts) |
| `missing_station` | mod | `craft`: the crafting station is not in range |
| `unknown_method` | mod | No such bridge command (version mismatch between server and mod) |
| `internal` | mod, server | Unexpected exception (`<Type>: <message>`) or a non-mod error in the server |
| `not_connected` | server | The game isn't reachable at `TERRARIA_HOST:TERRARIA_PORT` (not running, mod disabled, no world, firewall) |
| `timeout` | server | The mod didn't reply to one bridge call within 10 s |
| `dev_commands_disabled`, `not_singleplayer`, `item_on_cursor` | mod | Dev commands only (below) |

`no_path` and `out_of_reach` exist in `ErrorCodes` but no command raises them today: unreachable targets show up as
a failed task with a reason instead. Task failures in general (`status: "failed"`) carry their explanation in
`report.reason`, not in an error code.

## Bridge commands that are not MCP tools

The mod registers these commands on the bridge; the MCP server deliberately does not expose them.

| Command | Gate | What it does |
|---|---|---|
| `ping` | none, works at the menu | `{pong: true, version}`: mod version check |
| `dev_status` | none, works at the menu, read-only | `{devCommands, inWorld, autoload}`: whether dev commands are on and the last auto-load outcome (used by `scripts/dev-reload.sh` to fail fast) |
| `dev_save_and_exit {reload?}` | `DevCommands` config, single player | Runs the in-game *Save & Exit* (player + world), optionally writes an auto-load file so the next launch loads the same character and world, then quits the game. Used by `scripts/dev-reload.sh` |
| `dev_dump {x1, y1, x2, y2}` | `DevCommands`, in a world | The real tiles of a rectangle (max 200k tiles) as `ArrayTileGrid` glyph rows, **ignoring fair vision**: test-fixture capture for offline navigation fuzzing, never for the playing AI |
| `dev_plan {x, y, tolerance?}` | `DevCommands`, in a world | The path `move_to` would plan from the player's tile to (x,y) plus the follower's first tick: navigation debugging |

`DevCommands` is off by default because anyone who can reach the bridge port could close the game. The dev commands
are not actions (`user_has_control` does not apply), which is why they must stay out of the MCP tool list: `dev_dump`
would break fair play.

## Keeping this page correct

- The tool index above and all of [MCP-tools.md](MCP-tools.md) are generated by
  `mcp-server/scripts/tool-reference.ts`, which registers the real tools on an in-memory MCP server and reads
  `tools/list` back, so names, descriptions, JSON schemas and annotations are what clients see.
- After changing `src/tools.ts`, run `cd mcp-server && npm run docs` (or `npm run docs -- --check` to only verify).
  `npm test` fails (`test/docs.test.ts`) while the generated files are stale, or when a tool has no area/summary
  entry in `AREAS` in `scripts/tool-reference.ts`.
- The rest of this page (events, errors, environment, dev commands) is hand-written: update it with
  `src/events.ts`, `src/connection.ts`, `src/host.ts` and `mod/LlmPlayer/Bridge/Protocol.cs`.

## Possible additions

Not implemented; ideas for helping the LLM player through MCP features other than tools.

- **Resource `terraria://rules`** (static): the fair-play rules and protected-tile list, the control handover
  contract (Insert always wins; stop and ask after a refused `request_control`) and the coordinate convention. Today
  these live only in the `instructions` text, which some clients drop or truncate; a resource can be re-read mid-game.
- **Resource `terraria://guide/progression`** (static): the boss-by-boss plan as a checklist (gear, arena, potions
  and summon for Eye of Cthulhu, then King Slime, ...) and the tool recipes that worked (e.g. arena =
  `build_platforms` 3 rows, spacing 5; `attack target:"boss" heal_below 0.5 retreat_below 0`). It would replace
  long how-to text in `attack` / `get_boss_info` descriptions, which costs tokens on every `tools/list`.
- **Resource templates for live state**: `terraria://state`, `terraria://inventory`, `terraria://map/{radius}` as
  read-only views of the same fair data as `get_state` / `get_inventory` / `get_map`, for clients that attach
  resources as context. Event delivery would stay in tool results (resources have no event channel), and
  `notifications/resources/updated` on task end would let a client refresh state without polling.
- **Prompt `play_next_goal {goal?}`**: "read get_state and get_boss_info, check the progression guide, pick the next
  unmet step, plan only that step, then act with request_control → action → wait_for_task", so a session starts the
  same way every time.
- **Prompt `prepare_boss {boss}`**: a checklist prompt (life, defense, potions, ammo, arena, time of day) that ends in
  either "ready: summon" or a list of gathering tasks.
- **Prompt `after_death`**: read `lastDeaths` and the events, explain what killed the player and change the plan
  (reflex thresholds, arena, gear) before trying again.

Prompts are user-triggered in most clients (slash commands), so they suit the user starting or resuming a session;
the rules and guide fit resources better because the model can pull them on its own.
