import { z } from "zod";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ModError, type ModEvent } from "./connection.js";

/** The subset of ModConnection the tools need (lets tests pass a fake). */
export interface ModClient {
  call(method: string, params?: object, timeoutMs?: number): Promise<unknown>;
  drainEvents(): ModEvent[];
}

export const SERVER_INSTRUCTIONS = `You control the user's own Terraria character through the LlmPlayer mod.
- Control: action tools need AI mode. Call request_control to switch it on yourself; the user can also hand control over with the toggle key (default Insert). The user pressing Insert takes control back at once and cancels the current task; after that request_control is refused until the user hands control over again — respect that, stop, and ask them. Dying cancels the current task but you keep control.
- Coordinates: absolute tile coordinates, y grows DOWNWARD (smaller y = higher up). The player's position (x,y) is the bottom-left tile of its 2-wide x 3-tall body (feet-left): it occupies x..x+1, y-2..y, standing on tile row y+1.
- Fair vision: you only see what the player could see. '?' in get_map = unknown/unrevealed tiles; find/get_entities only report visible things (exception: get_state lists every living boss, as the vanilla boss health bar does).
- Actions start asynchronous tasks and return immediately. Only one task runs at a time; starting a new one cancels the old. Follow an action with wait_for_task (blocks until done) or get_task (poll).
- Suggested loop: get_state -> get_map -> pick an action -> wait_for_task -> repeat. Check events in results (damage taken, control changes).
- Never breaks protected tiles (furniture, chests, doors, stations, NPC houses, spawn area), never attacks town NPCs/critters, never teleports or spawns items.`;

const COORDS =
  " Coordinates are absolute tiles, y grows downward; player position = feet-left tile of its 2x3 body.";
const STARTS_TASK =
  " Starts an async task and returns immediately: follow with wait_for_task (or get_task). Replaces any running task. Fails with user_has_control unless the user pressed Insert to hand over control.";
const INSTANT_ACTION =
  " Instant action (no task). Fails with user_has_control unless the user pressed Insert to hand over control.";

const TOGGLE_HINT =
  "The user is driving. Call request_control to take control; if it is refused (the user took control back with Insert), stop and ask the user to press Insert when they want you to continue. Observation tools still work meanwhile.";

const int = () => z.number().int();
const tileX = () => int().describe("Absolute tile x");
const tileY = () => int().describe("Absolute tile y (grows downward)");

const POLL_MS = 250;
const MAX_WAIT_S = 300;
const TASK_STATUSES = ["running", "succeeded", "failed", "cancelled"] as const;

type Shape = Record<string, z.ZodType>;

interface ToolDef {
  name: string;
  description: string;
  shape?: Shape;
  readOnly?: boolean;
  /** Extra validation beyond the schema; returns an error message or undefined. */
  check?: (args: Record<string, unknown>) => string | undefined;
}

const TOOLS: ToolDef[] = [
  // Control
  {
    name: "get_control",
    description: "Who is driving the character: {driver: 'user'|'ai'}. Only 'ai' lets action tools work.",
    readOnly: true,
  },
  {
    name: "request_control",
    description:
      "Switch AI mode on so action tools work: {driver, granted, reason?}. Refused (granted false, with the reason) after the user took control back with Insert — then only the user can hand it over again, so ask them — and while the player is dead.",
  },

  // Observation
  {
    name: "get_state",
    description:
      "Player status: position {x,y}, life/mana/defense, buffs, held item, time of day, biome, depth layer, spawn point, driver, current task and recent events (damage, kills, pickups). Optional fields, present only when relevant: nearbyHostiles {count, nearest} (visible hostile NPCs within 40 tiles and the nearest one's distance), boss [{id, name, life, lifeMax, x, y, distance, segments?}] (every boss alive in the world, like the vanilla boss health bar; segmented bosses merged), worldEvents (e.g. 'blood moon', 'goblin army', 'slime rain', 'rain'), openChest {x, y, name} (the container open right now; see open_chest), housing {homeless?: [names], canMoveIn?: [names]} (town NPCs without a house, and NPCs whose move-in conditions are met: they arrive during the day once a free valid house exists; see check_housing / build_house). Start every decision here." +
      COORDS,
    readOnly: true,
  },
  {
    name: "get_map",
    description:
      "ASCII map centred on the player with a legend and the top-left corner's coordinates. '@' player, '#' solid, '.' air, '=' platform, '~' water, 'L' lava, 'T' tree, 'C' chest, 'W' crafting station, 'D' closed door (move_to opens and closes doors on its way), lowercase letters = ores (see legend), 'E' enemy, 'N' town NPC, 'i' dropped item, '?' unknown (not yet seen — may be anything). Row r, column c is tile (left+c, top+r)." +
      COORDS,
    shape: { radius: int().min(1).max(40).optional().describe("Half-width in tiles, default 20, max 40") },
    readOnly: true,
  },
  {
    name: "get_entities",
    description:
      "Visible NPCs, enemies, critters, dropped items and hostile projectiles near the player (max 30): {id, name, kind: hostile|town|critter|item|projectile, x, y, life, lifeMax, distance}, plus when relevant boss: true, vx/vy (velocity in tiles/second, omitted when still; +y = falling/downward), damage (contact or projectile damage) and defense (hostiles). Ordered bosses first (nearest 10 segments), then threats (hostile NPCs and up to 10 projectiles), then everything else, nearest first within each group. Use id with move_to/attack/talk_to_npc (projectile ids are informational only).",
    shape: { radius: int().min(1).optional().describe("Search radius in tiles, default 40") },
    readOnly: true,
  },
  {
    name: "find",
    description:
      "Nearest visible things of a category (max 10, nearest first): ores, trees, chests (any container), crafting stations, beds, NPCs, dropped items, blocks by name (category block + name, e.g. 'Stone', 'Sand', 'Clay': get_map shows every solid tile as '#'), or valuable: cave loot and landmarks — 'Life Crystal' (collect it: +20 max life), 'Life Fruit', 'Pot' (collect: coins, potions, torches), chests by their in-game name ('Gold Chest', 'Locked Gold Chest' = dungeon, needs a key: refused), 'Enchanted Sword (in stone)' / 'Sword in Stone (maybe a decoy)' (collect), 'Demon Altar' / 'Crimson Altar' (crafting station for boss summons, e.g. Suspicious Looking Eye; can't be broken), 'Shadow Orb (don't break)' / 'Crimson Heart (don't break)' (breaking one has world effects; the third summons a boss — never broken by the AI) and 'Fallen Star' items on the ground. Objects report their top-left tile. Only searches tiles the player has seen; explore / explore_caves to reveal more." +
      COORDS,
    shape: {
      category: z.enum(["ore", "tree", "chest", "station", "bed", "npc", "item", "block", "valuable"]),
      name: z.string().optional().describe("Name filter, e.g. 'Copper Ore', 'Work Bench', 'Guide'; required for block ('Stone', 'Sand')"),
      radius: int().min(1).optional().describe("Search radius in tiles, default 60"),
    },
    readOnly: true,
  },
  {
    name: "get_inventory",
    description:
      "Hotbar (slots 0-9), main inventory, armor, accessories, ammo, coins and the selected hotbar slot. Empty slots are omitted. Hotbar/inventory/ammo items carry tags only when true: weapon + damage (with your bonuses), heal (life restored), ammo, consumable (used up on use, e.g. potions), placeable (blocks, walls, furniture), pick/axe/hammer (tool power, axe as shown in game).",
    readOnly: true,
  },
  {
    name: "list_recipes",
    description:
      "Recipes with ingredients and required stations. By default only what is craftable right now (current inventory + stations in range); craftable_only=false with a name searches all recipes producing that item.",
    shape: {
      craftable_only: z.boolean().optional().describe("Default true"),
      name: z.string().optional().describe("Result item name to search for (useful with craftable_only=false)"),
    },
    readOnly: true,
  },

  // Tasks
  {
    name: "get_task",
    description:
      "Current task and the last finished one: {current, lastFinished}, each {name, status: running|succeeded|failed|cancelled, reason, progress, result?} (result only for tasks that return data, e.g. talk_to_npc, attack, explore). Use wait_for_task to block instead of polling.",
    readOnly: true,
  },
  {
    name: "cancel_task",
    description: "Stop the running task; the player stops moving." + INSTANT_ACTION,
  },

  // Movement
  {
    name: "move_to",
    description:
      "Walk to a tile or an entity with smart pathfinding (jumps, drops, platforms; digs and builds if allowed). Give x,y (a standing position; a target inside solid ground snaps to the nearest standable tile) or entity_id from get_entities. Fails with a reason if unreachable." +
      COORDS +
      STARTS_TASK,
    shape: {
      x: tileX().optional(),
      y: tileY().optional(),
      entity_id: int().optional().describe("Entity id from get_entities, instead of x,y"),
      allow_dig: z.boolean().optional().describe("May mine through blocks on the way, default true"),
      allow_build: z.boolean().optional().describe("May place platforms/blocks to climb or bridge, default true"),
    },
    check: (a) =>
      a.entity_id !== undefined || (a.x !== undefined && a.y !== undefined)
        ? undefined
        : "give either x and y, or entity_id",
  },
  {
    name: "explore",
    description:
      "Travel roughly `distance` tiles in a direction, revealing the map as it goes. Good for finding ores, trees and caves. " +
      "Walks toward the farthest visible ground; when there is none (e.g. solid ground below or a wall ahead) and allow_dig is on, it digs with your best pickaxe: " +
      "down = a 2-wide shaft under you (you drop as the floor goes; move_to climbs back out with platforms), left/right = a 3-tall tunnel. Digging up isn't supported. " +
      "It only digs tiles it can see and stops with a reason before lava/water next to the hole, protected tiles, tiles too hard for the pickaxe (\"needs pick power N at (x,y)\") or an unsafe drop. " +
      "Dark tiles: it places a torch from your inventory and looks again (\"too dark: no torches\" without any); with torches on it also lights dark spots underground about every 10 tiles. " +
      "Progress shows walking/digging; result: {covered, dug, torchesPlaced, stoppedBecause?}." +
      STARTS_TASK,
    shape: {
      direction: z.enum(["left", "right", "down", "up"]),
      distance: int().min(1).optional().describe("Tiles to cover, default 50"),
      allow_dig: z.boolean().optional().describe("Dig when there is no visible ground to walk to, default true"),
      torches: z.boolean().optional().describe("Place torches from the inventory in the dark, default true"),
    },
  },

  // Gathering
  {
    name: "mine",
    description:
      "Mine one tile: walks within reach, picks the best pickaxe and digs it. Fails on protected tiles or if the pickaxe is too weak. To break a pot or life crystal and also pick up what drops, use collect." +
      COORDS +
      STARTS_TASK,
    shape: { x: tileX(), y: tileY() },
  },
  {
    name: "mine_area",
    description:
      "Mine every tile in the rectangle (x1,y1)-(x2,y2), inclusive, at most 200 tiles, top row first. Tiles that can't or " +
      "mustn't be mined are skipped, not fatal: air, trees, hammer-only, protected, locked (holding up an object), not " +
      "visible, too hard for your pickaxe. The reply (and the finished task's report.result) has skipped, " +
      "skippedReasons {reason: count} and skippedTiles [{x,y,reason}] (first 10, air omitted)." +
      COORDS +
      STARTS_TASK,
    shape: { x1: tileX(), y1: tileY(), x2: tileX(), y2: tileY() },
  },
  {
    name: "mine_vein",
    description:
      "Mine one connected deposit: give any visible tile of it. Ore: the whole vein (every visible tile of the same ore connected to it, 8-neighbourhood, up to 200) by default. Any other block (Stone, Sand, Clay, Mud…): `max` tiles of it (default 20), nearest to the given tile first — e.g. 20 Stone for a furnace. Only that block type is mined, not what surrounds it. Tiles it can't reach or break are skipped and reported (report.result.skippedReasons). find category=ore / category=block gives a tile." +
      COORDS +
      STARTS_TASK,
    shape: {
      x: tileX(),
      y: tileY(),
      max: int().min(1).max(200).optional().describe("How many tiles at most; default all of an ore vein, 20 of other blocks"),
    },
  },
  {
    name: "chop_tree",
    description:
      "Chop down a tree with the best axe. Give the trunk's x,y, or omit both to chop the nearest visible tree." +
      COORDS +
      STARTS_TASK,
    shape: { x: tileX().optional(), y: tileY().optional() },
    check: (a) => ((a.x === undefined) === (a.y === undefined) ? undefined : "give both x and y, or neither"),
  },
  {
    name: "pick_up_items",
    description: "Walk over visible dropped items within radius, nearest first, to collect them." + STARTS_TASK,
    shape: { radius: int().min(1).optional().describe("Tiles, default 10") },
  },

  // Inventory
  {
    name: "select_hotbar",
    description: "Select a hotbar slot (0-9) as the held item." + INSTANT_ACTION,
    shape: { slot: int().min(0).max(9) },
  },
  {
    name: "equip",
    description:
      "Equip armor or an accessory from the inventory, by item name. Armor goes to its own slot; an accessory goes to the first free accessory slot unless `slot` picks one (3-9, the accessory slot numbers get_inventory shows; 8 needs expert mode + Demon Heart, 9 master mode), replacing what is there. Anything replaced goes back to the inventory slot the new item came from. Vanilla rules apply: no duplicate accessories, one pair of wings." +
      INSTANT_ACTION,
    shape: {
      item: z.string().min(1).describe("Item display name (case-insensitive) or internal name"),
      slot: int().min(3).max(9).optional().describe("Accessory slot 3-9 (get_inventory numbering); accessories only"),
    },
  },
  {
    name: "unequip",
    description:
      "Take off equipped armor or an accessory into the first empty inventory slot. Identify it by item name or by equipment slot (0-2 armor, 3-9 accessories as get_inventory shows them, 10-19 vanity). Fails if the inventory has no empty slot." +
      INSTANT_ACTION,
    shape: {
      item: z.string().min(1).optional().describe("Equipped item name (case-insensitive)"),
      slot: int().min(0).max(19).optional().describe("Equipment slot 0-19, instead of item"),
    },
    check: (a) => ((a.item === undefined) === (a.slot === undefined) ? "give exactly one of item or slot" : undefined),
  },
  {
    name: "use_item",
    description:
      "Use an item once (potion, tool, summon), aiming at tile x,y if given. Identify it by item name or inventory slot." +
      COORDS +
      STARTS_TASK,
    shape: {
      item: z.string().min(1).optional().describe("Item name (case-insensitive)"),
      slot: int().min(0).optional().describe("Inventory slot index, instead of item"),
      x: tileX().optional(),
      y: tileY().optional(),
    },
    check: (a) => {
      if ((a.item === undefined) === (a.slot === undefined)) return "give exactly one of item or slot";
      if ((a.x === undefined) !== (a.y === undefined)) return "give both x and y, or neither";
      return undefined;
    },
  },
  {
    name: "place",
    description:
      "Place a block, platform, torch or furniture (e.g. 'Work Bench') at tile x,y. The tile must be visible, empty and not protected; walks within reach first." +
      COORDS +
      STARTS_TASK,
    shape: { item: z.string().min(1).describe("Item name (case-insensitive)"), x: tileX(), y: tileY() },
  },
  {
    name: "craft",
    description:
      "Craft an item `count` times, consuming ingredients. Needs the station in range: fails with missing_station or missing_item naming what is needed. Check list_recipes first. With with_intermediates=true, ingredients you are short of are crafted first from what you have, recursively (e.g. Lesser Healing Potion: Sand -> Glass -> Bottle at a Furnace, then the potion), using only owned items and stations in range now; the reply then lists intermediates [{item, crafted, planned}]. Nothing is ever crafted without real ingredients and stations." +
      INSTANT_ACTION,
    shape: {
      item: z.string().min(1).describe("Result item name (case-insensitive)"),
      count: int().min(1).max(999).optional().describe("Times to craft, default 1"),
      with_intermediates: z.boolean().optional().describe("Also craft missing intermediate ingredients first, default false"),
    },
  },

  // Building
  {
    name: "build_platforms",
    description:
      "Build horizontal rows of platforms (or blocks): bridges over gaps, floors, platform rows to stand or fight on. Places tiles from x1 to x2 on row y; rows>1 adds more rows upward at y-spacing, y-2*spacing, ... At most 200 tiles per call. Uses `item` if given (a block or platform), else any platform you have, else a cheap block (dirt, stone, wood...). Walks along and stands on what it builds, placing every tile in reach before moving on; uses real placement (needs something to attach to: an adjacent block/platform or a wall). Tiles already occupied, in NPC houses, with nothing to attach to, or unreachable are skipped and reported. Stops when the item runs out. report.result: {item, placed, remaining, skipped, skippedReasons, skippedTiles}." +
      COORDS +
      STARTS_TASK,
    shape: {
      x1: tileX(),
      x2: tileX(),
      y: tileY(),
      item: z.string().min(1).optional().describe("Block or platform item name; default: any platform, else a cheap block"),
      rows: int().min(1).max(20).optional().describe("Number of rows, default 1"),
      spacing: int().min(1).max(50).optional().describe("Tiles between rows (upward), default 8"),
    },
  },

  {
    name: "build_house",
    description:
      "Build a valid town-NPC house (Merchant, Nurse, Demolitionist, Arms Dealer… need one each) with real placement: frame of blocks (floor, walls, ceiling), a door in one side wall, background walls on every inside tile, then a table (or work bench), a chair and a torch. " +
      "x,y = the bottom-left tile of the frame (floor row); omit both to use the nearest flat, clear, visible spot within 30 tiles (level ground as the floor, open air above, no trees, room to stand outside the door). Default size 10x6 outer (frame included): vanilla's minimum of 60 tiles; height at most 7 so the ceiling stays in reach. " +
      "Materials come from your inventory: blocks for the frame (Wood first, then other wood, Stone, bricks, Dirt…; existing blocks in the frame's place are kept), owned doors/tables/chairs/torches/house walls first. What is missing is crafted first when possible (Wood Wall/Dirt Wall/Stone Wall 4 per block, Wooden Door, Chair, Table at a Work Bench in range; Work Bench as the table and Torch from Gel+Wood need no station). " +
      "If anything is short it fails up front with missing_item listing exact counts and where to get them; a site that isn't clear fails with bad_params naming the tile (tree: chop_tree it or pick another spot). " +
      "The player stands inside while building and walks out through the door at the end (move_to opens/closes doors). Then vanilla's housing query runs: succeeded = valid house; failed = built but vanilla's reason (e.g. missing walls). report.result: {x, y, width, height, door, valid, reason?, placed {frame block, door, background wall, table, chair, torch}, skipped, skippedReasons, exitedThroughDoor, housing}." +
      COORDS +
      STARTS_TASK,
    shape: {
      x: tileX().optional().describe("Left column of the house frame; omit x and y to find a spot automatically"),
      y: tileY().optional().describe("Floor row of the house frame (its bottom row)"),
      width: int().min(8).max(30).optional().describe("Outer width incl. frame, default 10"),
      height: int().min(5).max(7).optional().describe("Outer height incl. frame, default 6"),
      door: z.enum(["left", "right"]).optional().describe("Side wall for the door; default the side facing the player"),
    },
    check: (a) => ((a.x === undefined) === (a.y === undefined) ? undefined : "give both x and y, or neither"),
  },
  {
    name: "check_housing",
    description:
      "Town NPC housing status. With x,y (any visible open tile inside a room): vanilla's housing query, like the housing icon's '?' — housing {valid, message (vanilla's text, e.g. 'This housing is suitable for a new resident.' or what is missing), missing? [light source, door, table, chair], occupied, occupant?, room {x1,y1,x2,y2,tiles}}. " +
      "Always: residents [{name, type, homeless, homeX?, homeY?, pet}], homeless [names], notYetHere [{name, canMoveIn, condition, missing?, useful}] for useful NPCs the world lacks (Guide, Merchant: 50 silver in inventory, Nurse: Life Crystal used + Merchant, Arms Dealer: bullets or a gun, Demolitionist: explosive + Merchant, Dryad: a boss defeated, …; checked live with the game's own rules). An NPC that canMoveIn arrives during the day once a free valid house exists.",
    shape: { x: tileX().optional(), y: tileY().optional() },
    readOnly: true,
    check: (a) => ((a.x === undefined) === (a.y === undefined) ? undefined : "give both x and y, or neither"),
  },

  // Combat
  {
    name: "attack",
    description:
      'Fight hostile enemies: picks the best weapon, closes in for melee or keeps 8-15 tiles for ranged/magic, and does not trade hits — when an enemy is about to touch you (from its hitbox and velocity) it backs off or jumps, never into a hole or lava, still swinging/shooting (melee becomes hit-and-run). target: an entity id from get_entities, "nearest", "boss" (the nearest living boss, even off screen) or "all" = every visible hostile within radius, nearest first, until none remain (one call clears a group). Below retreat_below of max life with no healing potion available (quick-heal reflex handles the rest) it stops attacking, runs away for up to 3 s and fails with reason "low life". When it finishes, wait_for_task\'s report.result is {killed, kills: [names], damageTaken, durationS, dodges, retreated, potionsUsed, ammoLeft?}. Never targets town NPCs or critters.' +
      " BOSS FIGHTS (the target is a boss, e.g. target \"boss\"): it never pathfinds and never leaves the arena; with a ranged weapon it kites back and forth across the arena (turning near the ends) shooting with lead aim, and dodges dashes sideways, by jumping, or by dropping through the platform underfoot; minions about to reach you are shot first (kill_minions, default true), then back to the boss. A boss that leaves the screen stays the target (kiting continues, shooting resumes when it is back). Ends with killed, or fails with \"boss fled\" when it despawns alive (e.g. the Eye of Cthulhu at dawn). Boss results add {boss, bossKilled, bossLifeLeftPct (when not killed)}; progress shows boss life %, ammo, potions and dodges. Before a boss: build an arena first (build_platforms: 2-3 rows of platforms 60-100 tiles long, spacing ~5 so you can jump between rows), stand on it, carry a bow + plenty of arrows and healing potions, then attack with heal_below ~0.5 and retreat_below 0 (running away from a flying boss doesn't help). Use wait_for_task with a long timeout (fights take minutes) and check get_state between waits. get_boss_info has summon/strategy facts." +
      STARTS_TASK,
    shape: {
      target: z
        .union([int(), z.literal("nearest"), z.literal("boss"), z.literal("all")])
        .describe('Entity id of a hostile enemy, "nearest", "boss" (nearest living boss) or "all" (clear every visible hostile within radius)'),
      radius: z.number().min(1).max(40).optional().describe('For target "all": search radius in tiles, default 25'),
      retreat_below: z
        .number()
        .min(0)
        .lt(1)
        .optional()
        .describe("Retreat (and fail with 'low life') below this life fraction when no healing is available; default 0.3, 0 = never"),
      heal_below: z
        .number()
        .min(0)
        .lt(1)
        .optional()
        .describe("Quick-heal below this life fraction while this attack runs (only raises the set_reflexes threshold), e.g. 0.5 for a boss"),
      arena: z
        .object({ x1: tileX(), x2: tileX() })
        .optional()
        .describe("Boss fights: the tile columns to stay within (6-400 wide). Default: the walkable ground/platform span around you, up to 50 tiles each way"),
      kill_minions: z.boolean().optional().describe("Boss fights: shoot minions about to reach you first (default true)"),
    },
  },
  {
    name: "set_reflexes",
    description:
      "Configure automatic behaviour while you drive: auto_defend (hit hostile enemies in reach, or that just hit you, while idle, waiting, moving, exploring, chopping, mining, placing, picking up or walking to an NPC: the task pauses and resumes after the fight; attack and an item use already under way keep the controls), quick_heal_threshold (quick-heal below this life fraction), pause_when_idle (singleplayer: pause the game while no task runs so you can think). Omitted fields keep their value; returns the current settings." +
      INSTANT_ACTION,
    shape: {
      auto_defend: z.boolean().optional(),
      quick_heal_threshold: z.number().min(0).max(1).optional().describe("Life fraction 0..1, e.g. 0.35"),
      pause_when_idle: z.boolean().optional(),
    },
  },
  {
    name: "wait",
    description: "Stand still for N seconds (max 60), with reflexes still active — e.g. to let night pass near shelter." + STARTS_TASK,
    shape: { seconds: z.number().positive().max(60) },
  },

  // NPCs
  {
    name: "talk_to_npc",
    description:
      "Walk within talk range of a visible town NPC and open its chat. When the task succeeds, wait_for_task's report.result is {name, chat, shop?: [{name, price}]} (shop only if the NPC sells things; price after happiness discounts, e.g. '1 gold 50 silver'). Use buy to purchase." +
      STARTS_TASK,
    shape: { entity_id: int().describe("Town NPC id from get_entities") },
  },
  {
    name: "buy",
    description:
      "Buy `count` of an item from a visible town NPC's shop with your own coins: walks to the NPC, opens its shop, buys through the game's normal shop purchase (price includes happiness and discounts), then closes the shop. All of the NPC's shops are searched. Stops early if money or inventory space runs out (still succeeds if at least one was bought). report.result: {npc, item, bought, requested, spent, stopped}. Use talk_to_npc first to see the shop and prices. Selling is not supported." +
      STARTS_TASK,
    shape: {
      entity_id: int().describe("Town NPC id from get_entities"),
      item: z.string().min(1).describe("Item name as the shop lists it (case-insensitive)"),
      count: int().min(1).max(999).optional().describe("How many, default 1"),
    },
  },

  // Chests, beds
  {
    name: "open_chest",
    description:
      "Walk within reach of a chest (or dresser, piggy bank, safe, defender's forge, void vault, modded container) and open it like a right-click. Give any tile of it as x,y, or omit both for the nearest visible one you can open. When the task succeeds, wait_for_task's report.result is {x, y, name, items: [{slot, name, stack}]} — contents are only visible once opened. The chest stays open (get_state shows openChest) for chest_deposit / chest_withdraw until close_chest, another task starts, you walk out of reach (vanilla closes it) or control goes back to the user. Fails on locked chests ('locked': keys are never used automatically), trapped Dead Man's Chests ('rigged') and chests another player has open ('in_use')." +
      COORDS +
      STARTS_TASK,
    shape: { x: tileX().optional(), y: tileY().optional() },
    check: (a) => ((a.x === undefined) === (a.y === undefined) ? undefined : "give both x and y, or neither"),
  },
  {
    name: "chest_deposit",
    description:
      "Put items into the open chest. Exactly one of: item (+ optional count, default the whole amount; like shift-clicking stacks into the chest, fills partial stacks first; favorited items are never moved), all: true (vanilla 'Deposit All': main inventory and coins, never the hotbar or favorites), quick_stack: true (vanilla 'Quick Stack': only items the chest already holds). Returns {moved: [{name, count}], chest: {x, y, name}, items: [{slot, name, stack}]}; what doesn't fit stays in the inventory (error chest_full if nothing fit)." +
      INSTANT_ACTION,
    shape: {
      item: z.string().min(1).optional().describe("Item name (case-insensitive)"),
      count: int().min(1).optional().describe("How many, default all of them (only with item)"),
      all: z.boolean().optional().describe("Deposit All"),
      quick_stack: z.boolean().optional().describe("Quick Stack onto matching stacks"),
    },
    check: (a) => {
      const modes = [a.item !== undefined, a.all === true, a.quick_stack === true].filter(Boolean).length;
      if (modes !== 1) return "give exactly one of item, all: true or quick_stack: true";
      if (a.count !== undefined && a.item === undefined) return "count only goes with item";
      return undefined;
    },
  },
  {
    name: "chest_withdraw",
    description:
      "Take items out of the open chest into the inventory. Exactly one of: item (+ optional count, default all of it) or all: true (vanilla 'Loot All'). Items go in the way vanilla picks things up (stacking onto existing stacks first); whatever doesn't fit stays in the chest — nothing is ever dropped or deleted (error inventory_full if nothing fit). Returns {moved: [{name, count}], chest, items} like chest_deposit." +
      INSTANT_ACTION,
    shape: {
      item: z.string().min(1).optional().describe("Item name (case-insensitive)"),
      count: int().min(1).optional().describe("How many, default all of them (only with item)"),
      all: z.boolean().optional().describe("Loot All"),
    },
    check: (a) => {
      if ((a.item !== undefined) === (a.all === true)) return "give exactly one of item or all: true";
      if (a.count !== undefined && a.item === undefined) return "count only goes with item";
      return undefined;
    },
  },
  {
    name: "close_chest",
    description: "Close the open chest (and the inventory, if opening the chest opened it). Returns {closed, chest}; closed is false if none was open." + INSTANT_ACTION,
  },
  {
    name: "use_bed",
    description:
      "Walk to a bed and set your spawn point there, like right-clicking the bed's foot (vanilla rules: the bed must be in valid housing and not obstructed). Give any tile of the bed as x,y, or omit both for the nearest visible bed. When the task succeeds, report.result is {spawnX, spawnY, message}; on failure the reason is vanilla's (e.g. obstructed, room too small, not valid housing)." +
      COORDS +
      STARTS_TASK,
    shape: { x: tileX().optional(), y: tileY().optional() },
    check: (a) => ((a.x === undefined) === (a.y === undefined) ? undefined : "give both x and y, or neither"),
  },

  // Caves: loot and exploration
  {
    name: "collect",
    description:
      "Break one piece of natural cave loot with your best pickaxe and pick up what drops: a Pot (one hit; coins, potions, torches…), a Life Crystal (+20 max life when used: use_item 'Life Crystal'), Life Fruit, or the sword in stone of an Enchanted Sword shrine. Give any tile of it (find category=valuable). Walks within reach like mine. Refused for chests (use loot_chest), Shadow Orbs / Crimson Hearts (world effects; never broken), altars, and anything inside an NPC house or near spawn. When the task succeeds, report.result is {broke, x, y, pickedUp: [{name, count}]}." +
      COORDS +
      STARTS_TASK,
    shape: { x: tileX(), y: tileY() },
  },
  {
    name: "loot_chest",
    description:
      "open_chest + chest_withdraw all + close_chest in one: walks to the chest, opens it like a right-click, presses vanilla 'Loot All' and closes it. Give any tile of it, or omit both for the nearest visible chest you can open. Whatever doesn't fit stays in the chest (nothing is dropped). Locked chests (e.g. dungeon 'Locked Gold Chest': keys are never used automatically), trapped Dead Man's Chests and chests in use are refused. When the task succeeds, report.result is {x, y, name, looted: [{name, count}], left: [{slot, name, stack}]}. Use open_chest instead to see the contents and take only some items." +
      COORDS +
      STARTS_TASK,
    shape: { x: tileX().optional(), y: tileY().optional() },
    check: (a) => ((a.x === undefined) === (a.y === undefined) ? undefined : "give both x and y, or neither"),
  },
  {
    name: "explore_caves",
    description:
      "Explore caves systematically through their open space, revealing the map: repeatedly walks to a known ledge next to the nearest reachable edge between known open space and unknown tiles (no mass digging: it only digs through walls up to 3 tiles thick to reach the next cave), lights the dark opening with a torch from your inventory (also underground whenever it's dark and no torch is within ~10 tiles), looks, and moves on. A dark cave whose floor isn't known yet is lit from its edge first. Avoids lava (never walks through it, skips openings within 3 tiles of it) and unsafe drops. " +
      "Stops (succeeded) when something in stop_on that wasn't visible at the start comes into view — report.reason says what and where, report.result.stoppedAt has it — or when max_seconds is up or no reachable opening is left within radius tiles of the start; fails after 6 walks in a row that revealed nothing. " +
      "Kinds: life_crystal, life_fruit, pot, chest, enchanted_sword, altar, orb, ore, fallen_star (default stop_on: life_crystal, chest, enchanted_sword, life_fruit; [] = only time/radius). collect_pots: true breaks visible pots within 12 tiles on the way. " +
      "Result: {revealedTiles (approximate), found: [{kind, name, x, y}] (everything newly seen, max 30), torchesPlaced, potsBroken, legs, stoppedBecause, stoppedAt?}. Then: collect for life crystals/pots, loot_chest for chests, mine_vein for ore." +
      STARTS_TASK,
    shape: {
      radius: int().min(10).max(200).optional().describe("Max distance in tiles from the start, default 80"),
      max_seconds: int().min(5).max(600).optional().describe("Time limit, default 120"),
      stop_on: z
        .array(z.enum(["life_crystal", "life_fruit", "pot", "chest", "enchanted_sword", "altar", "orb", "ore", "fallen_star"]))
        .optional()
        .describe("Stop when one of these comes into view; default life_crystal, chest, enchanted_sword, life_fruit"),
      collect_pots: z.boolean().optional().describe("Break pots seen on the way (within 12 tiles), default false"),
      torches: z.boolean().optional().describe("Place torches in the dark, default true"),
    },
  },
  // Bosses
  {
    name: "get_boss_info",
    description:
      "Short static facts for preparing a boss fight (Classic mode, 1.4.4), currently Eye of Cthulhu and King Slime only: {bosses: [{key, name, defeated (this world), summon, recipe, summonWhen, naturalSpawn, leaves, stats, tips}], isNight}. Omit boss for all. Use before summoning: craft the summon item (craft with_intermediates), build an arena (build_platforms), then attack target \"boss\".",
    shape: {
      boss: z.string().min(1).optional().describe('"Eye of Cthulhu", "eoc", "King Slime", ...; omit for all'),
    },
    readOnly: true,
  },
];

function stripUndefined(args: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(args).filter(([, v]) => v !== undefined));
}

function withEvents(value: unknown, events: ModEvent[]): unknown {
  if (events.length === 0) return value;
  if (value !== null && typeof value === "object" && !Array.isArray(value)) return { ...value, events };
  return { result: value, events };
}

function ok(conn: ModClient, value: unknown): CallToolResult {
  return { content: [{ type: "text", text: JSON.stringify(withEvents(value, conn.drainEvents())) }] };
}

function fail(conn: ModClient, code: string, message: string): CallToolResult {
  let text = `${code}: ${message}`;
  if (code === "user_has_control") text += `\n${TOGGLE_HINT}`;
  const events = conn.drainEvents();
  if (events.length) text += `\nevents: ${JSON.stringify(events)}`;
  return { isError: true, content: [{ type: "text", text }] };
}

function failFrom(conn: ModClient, err: unknown): CallToolResult {
  if (err instanceof ModError) return fail(conn, err.code, err.message);
  return fail(conn, "internal", err instanceof Error ? err.message : String(err));
}

interface TaskReport {
  status?: unknown;
  [key: string]: unknown;
}

/** The mod may serialize the status enum as a string or as its ordinal. */
function normalizeReport(report: unknown): TaskReport | null {
  if (!report || typeof report !== "object") return null;
  const r = { ...(report as TaskReport) };
  if (typeof r.status === "number") r.status = TASK_STATUSES[r.status] ?? r.status;
  else if (typeof r.status === "string") r.status = r.status.toLowerCase();
  return r;
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function waitForTask(conn: ModClient, timeoutS: number): Promise<unknown> {
  const deadline = Date.now() + timeoutS * 1000;
  for (;;) {
    const res = (await conn.call("get_task", {})) as { current?: unknown; lastFinished?: unknown } | null;
    const current = normalizeReport(res?.current);
    if (current && current.status === "running") {
      const remaining = deadline - Date.now();
      if (remaining <= 0) return { timed_out: true, report: current };
      await sleep(Math.min(POLL_MS, remaining));
      continue;
    }
    const report = current ?? normalizeReport(res?.lastFinished);
    return report ? { timed_out: false, report } : { timed_out: false, report: null, note: "no task has run" };
  }
}

export function registerTools(server: McpServer, conn: ModClient): void {
  for (const def of TOOLS) {
    server.registerTool(
      def.name,
      {
        description: def.description,
        inputSchema: def.shape ?? {},
        annotations: def.readOnly ? { readOnlyHint: true } : { readOnlyHint: false, destructiveHint: false },
      },
      async (args: Record<string, unknown>) => {
        const params = stripUndefined(args ?? {});
        const problem = def.check?.(params);
        if (problem) return fail(conn, "bad_params", problem);
        try {
          return ok(conn, await conn.call(def.name, params));
        } catch (err) {
          return failFrom(conn, err);
        }
      },
    );
  }

  server.registerTool(
    "wait_for_task",
    {
      description:
        `Block until the current task finishes (succeeded, failed or cancelled) or timeout_s passes (default 30, max ${MAX_WAIT_S}). Returns {timed_out, report: {name, status, reason, progress, result?}}; result carries data from tasks like talk_to_npc and attack. If timed_out, the task is still running: call again, or cancel_task. Call this after every task-starting action.`,
      inputSchema: {
        timeout_s: z.number().min(0).max(MAX_WAIT_S).default(30).describe("Seconds to wait, default 30"),
      },
      annotations: { readOnlyHint: true },
    },
    async ({ timeout_s }) => {
      try {
        return ok(conn, await waitForTask(conn, timeout_s));
      } catch (err) {
        return failFrom(conn, err);
      }
    },
  );
}
