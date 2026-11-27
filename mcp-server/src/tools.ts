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
/** talk_to_npc / buy: the NPC by entity_id or by name, exactly one. */
const oneNpc = (a: Record<string, unknown>) =>
  (a.entity_id === undefined) === (a.name === undefined) ? "give exactly one of entity_id or name" : undefined;
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
      "Player status: position {x,y}, life/mana/defense, buffs, held item, time of day, biome, depth layer, spawn point, driver, current task and recent events (damage, kills, pickups, deaths; drained by each get_state). Optional fields, present only when relevant: breath {now, max} (only while below max, i.e. the head has been under water/honey; at 0 you drown. A reflex surfaces for air on its own below 45% and resumes the task at 90%; routes avoid more than ~11 tiles under water unless you have gills), nearbyHostiles {count, nearest} (visible hostile NPCs within 40 tiles and the nearest one's distance), boss [{id, name, life, lifeMax, x?, y?, distance?, segments?}] (every boss alive in the world with its life, like the vanilla boss health bar; x/y/distance only while you can see it; segmented bosses merged), worldEvents (e.g. 'blood moon', 'goblin army', 'slime rain', 'rain'), openChest {x, y, name} (the container open right now; see open_chest), housing {homeless?: [names], canMoveIn?: [names]} (town NPCs without a house, and NPCs whose move-in conditions are met: they arrive during the day once a free valid house exists; see check_housing / build_house), lastDeaths [{text, killer, x, y, time, secondsAgo}] (the last 5 deaths in this world, newest first, never drained: what killed you and where). Start every decision here." +
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
      "Visible NPCs, enemies, critters, dropped items and hostile projectiles near the player (max 30): {id, name, kind: hostile|town|critter|item|projectile, x, y, life, lifeMax, distance}, plus when relevant profession (town NPCs with a personal name: e.g. name 'Demetrius', profession 'Arms Dealer'), boss: true, vx/vy (velocity in tiles/second, omitted when still; +y = falling/downward), damage (contact or projectile damage) and defense (hostiles). Ordered bosses first (nearest 10 segments), then threats (hostile NPCs and up to 10 projectiles), then everything else, nearest first within each group. Use id with move_to/attack/talk_to_npc/buy (projectile ids are informational only; talk_to_npc and buy also take the name or profession).",
    shape: { radius: int().min(1).optional().describe("Search radius in tiles, default 40") },
    readOnly: true,
  },
  {
    name: "find",
    description:
      "Nearest visible things of a category (max 10, nearest first): ores, trees, chests (any container), crafting stations, beds, NPCs, dropped items, blocks by name (category block + name, e.g. 'Stone', 'Sand', 'Clay': get_map shows every solid tile as '#'), or valuable: cave loot and landmarks — 'Life Crystal' (collect it: +20 max life), 'Life Fruit', 'Pot' (collect: coins, potions, torches), chests by their in-game name ('Gold Chest', 'Locked Gold Chest' = dungeon, needs a key: refused), 'Enchanted Sword (in stone)' / 'Sword in Stone (maybe a decoy)' (collect), 'Demon Altar' / 'Crimson Altar' (crafting station for boss summons, e.g. Suspicious Looking Eye; can't be broken), 'Shadow Orb (don't break)' / 'Crimson Heart (don't break)' (breaking one has world effects; the third summons a boss — never broken by the AI) and 'Fallen Star' items on the ground; or plant: harvestable herbs and mushrooms ('Daybloom', 'Moonglow', 'Blinkroot', 'Deathweed', 'Waterleaf', 'Fireblossom', 'Shiverthorn', '… (blooming)' = also drops seeds, 'Mushroom', 'Vile Mushroom' (Vile Powder → Worm Food), 'Vicious Mushroom', 'Glowing Mushroom'): harvest one with mine at its x,y. Objects report their top-left tile. Only searches tiles the player has seen; explore / explore_caves to reveal more." +
      COORDS,
    shape: {
      category: z.enum(["ore", "tree", "chest", "station", "bed", "npc", "item", "block", "valuable", "plant"]),
      name: z.string().optional().describe("Name filter, e.g. 'Copper Ore', 'Work Bench', 'Guide'; required for block ('Stone', 'Sand')"),
      radius: int().min(1).optional().describe("Search radius in tiles, default 60"),
    },
    readOnly: true,
  },
  {
    name: "door",
    description:
      "Open or close a door within interaction reach (like a right-click): {x, y, action: 'open'|'close' (default close)}. Close the door of a house to keep zombies out at night; move_to already opens and closes doors it walks through." +
      COORDS +
      INSTANT_ACTION,
    shape: { x: tileX(), y: tileY(), action: z.enum(["open", "close"]).optional() },
  },
  {
    name: "swap_slots",
    description:
      "Swap two inventory slots (like dragging an item onto another): slots 0-9 hotbar, 10-49 inventory, 50-53 coins, 54-57 ammo. Ammo is used from the ammo slots left to right, so put the arrows/bullets you want fired first in slot 54." +
      INSTANT_ACTION,
    shape: { a: int().min(0).max(57), b: int().min(0).max(57) },
  },
  {
    name: "drop_item",
    description:
      "Throw items out of the inventory onto the ground at your feet, like the vanilla throw key: {item (name or inventory slot), count? (default the whole stack)}. Use it to free slots when the inventory is full (crafted items otherwise drop at your feet); trash: true deletes junk for good (thrown items get picked back up when walked over). pick_up_items ignores items you threw away. Favorited items are refused." +
      INSTANT_ACTION,
    shape: {
      item: z.string().min(1).describe("Item name (case-insensitive) or inventory slot number"),
      count: int().min(1).optional().describe("How many, default the whole stack"),
      trash: z.boolean().optional().describe("Put it in the trash slot instead (gone for good; thrown items get picked up again when you walk over them)"),
    },
  },
  {
    name: "inspect",
    description:
      "What exactly is on one visible tile: {tile (name or null), solid, cuttable (grass/plants placing replaces), wall, liquid, protected, pickPower, shape (sloped or half block; null for a full block)}. get_map draws small objects (saplings, rocks, herbs) as '.'; use this when a build or placement says a spot isn't empty." +
      COORDS,
    shape: { x: tileX(), y: tileY() },
    readOnly: true,
  },
  {
    name: "get_inventory",
    description:
      "Hotbar (slots 0-9), main inventory, armor, accessories, ammo, coins and the selected hotbar slot. Empty slots are omitted. Hotbar/inventory/ammo items carry tags only when true: weapon + damage (with your bonuses), heal (life restored), ammo, consumable (used up on use, e.g. potions), placeable (blocks, walls, furniture), pick/axe/hammer (tool power, axe as shown in game). For choosing attack's weapon and ammo: weapons also carry useTime (ticks per use, 60 = 1 s; lower = faster), ammoType (the ammo class they fire, e.g. \"arrow\", \"bullet\") and mana (cost per use); ammo stacks carry damage (added to the weapon's per shot) and ammoType. Each hit loses about half the target's defense (get_entities shows defense), so against armored enemies fewer, harder hits win.",
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
      "Walk to a tile or an entity with smart pathfinding (jumps, drops, platforms; digs and builds if allowed). Give x,y (a standing position; a target inside solid ground snaps to the nearest standable tile) or entity_id from get_entities. Fails with a reason if unreachable. " +
      "Torches (default on, needs torches in the inventory): in the dark underground or in a tunnel it digs, with no torch within ~10 tiles, it pauses between steps, places a torch within reach and walks on, so the way is revealed (the pathfinder only knows tiles you have seen: unlit tunnels stay unknown). Never in an NPC house or the spawn area. " +
      "Platform reserve: building keeps platform_reserve platforms (default 20, see set_reflexes) for later descents: the route may only use platforms above it plus cheap blocks, bridges and climbs switch to dirt/stone/… at the reserve, and only when no route fits does it use the reserve (reserveReleased). When the task ends, report.result is {torchesPlaced, platformsUsed, blocksUsed, platformsLeft, reserveReleased, note?} (note when platforms run short)." +
      COORDS +
      STARTS_TASK,
    shape: {
      x: tileX().optional(),
      y: tileY().optional(),
      entity_id: int().optional().describe("Entity id from get_entities, instead of x,y"),
      allow_dig: z.boolean().optional().describe("May mine through blocks on the way, default true"),
      allow_build: z.boolean().optional().describe("May place platforms/blocks to climb, bridge, or build down a drop too deep to fall safely (a platform below the feet, then drop onto it), default true"),
      torches: z.boolean().optional().describe("Light the walk: place torches from the inventory in the dark (underground or in a dug tunnel, about every 10 tiles), default true"),
      platform_reserve: int().min(0).max(9999).optional().describe("Platforms to keep for this walk unless no route fits without them (default: set_reflexes platform_reserve, 20)"),
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
    shape: { x: tileX(), y: tileY(),
      torches: z.boolean().optional().describe("Light the walk: place torches from the inventory in the dark (underground or in a dug tunnel, about every 10 tiles), default true"),
    },
  },
  {
    name: "mine_area",
    description:
      "Mine every tile in the rectangle (x1,y1)-(x2,y2), inclusive, at most 200 tiles, top row first. Tiles that can't or " +
      "mustn't be mined are skipped, not fatal: air, trees, hammer-only, protected, locked (holding up an object), not " +
      "visible, too hard for your pickaxe. The reply (and the finished task's report.result) has skipped, " +
      "skippedReasons {reason: count}, skippedTiles [{x,y,reason}] (first 10, air omitted) and torchesPlaced (walks are lit like move_to's)." +
      COORDS +
      STARTS_TASK,
    shape: { x1: tileX(), y1: tileY(), x2: tileX(), y2: tileY(),
      torches: z.boolean().optional().describe("Light the walk: place torches from the inventory in the dark (underground or in a dug tunnel, about every 10 tiles), default true"),
    },
  },
  {
    name: "mine_vein",
    description:
      "Mine one connected deposit: give any visible tile of it. Ore: the whole vein (every visible tile of the same ore connected to it, 8-neighbourhood, up to 200) by default. Any other block (Stone, Sand, Clay, Mud…): `max` tiles of it (default 20), nearest to the given tile first — e.g. 20 Stone for a furnace. Only that block type is mined, not what surrounds it. Tiles it can't reach or break are skipped and reported (report.result.skippedReasons; also torchesPlaced: walks are lit like move_to's). find category=ore / category=block gives a tile." +
      COORDS +
      STARTS_TASK,
    shape: {
      x: tileX(),
      y: tileY(),
      max: int().min(1).max(200).optional().describe("How many tiles at most; default all of an ore vein, 20 of other blocks"),
      torches: z.boolean().optional().describe("Light the walk: place torches from the inventory in the dark (underground or in a dug tunnel, about every 10 tiles), default true"),
    },
  },
  {
    name: "chop_tree",
    description:
      "Chop down a tree with the best axe and collect the wood. Give the trunk's x,y, or omit both for the nearest visible tree that may be chopped: trees near spawn or in NPC houses (protected) and trees whose trunk base isn't visible are skipped (the reply says how many: skipped, skippedReasons), and if the chosen tree can't be reached it moves on to the next nearest (up to 3 trees; the report names the unreachable ones)." +
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
      "Use an item once (potion, tool, summon), aiming at tile x,y if given. Identify it by item name or inventory slot. Explosives are refused: use blast. Waits for any swing still running, then checks the use really happened (a consumable lost one, a buff item's buff is active) and fails with a reason otherwise. Calling use_item while another use_item is still running queues it (reply has queued: n) instead of cancelling the first, so several buff potions can be drunk back to back; one wait_for_task then covers them all and report.result is {used: [{item, ok, detail}]} (the task fails if any use failed)." +
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
      "Place a block, platform, torch or furniture (e.g. 'Work Bench') at tile x,y. The tile must be visible, empty and not protected; walks within reach first, waits for any current swing/use to finish before switching to the item, and follows the item if it moved slots." +
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
      count: int().min(1).max(999).optional().describe("Times to craft, default 1. Careful: one craft can make many items (one Wooden Arrow craft = 25 arrows, one Torch craft = 3); use `items` to ask for an item count"),
      items: int().min(1).max(99999).optional().describe("Instead of count: craft until at least this many items are made (e.g. items 500 of Wooden Arrow = 20 crafts). The reply's `produced` is the item count made"),
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
      "x,y = the bottom-left tile of the frame (floor row); omit both to pick the best visible spot within 30 tiles on ordinary uneven ground, scored by distance and work: ground under at least a third of the floor, floor gaps up to 4 deep get blocks placed over them, natural bumps (dirt/grass/stone, outside protected areas) up to 2 tiles above the floor inside the house or in the door spot are mined first, open air above that, no trees or natural background walls, room to stand outside the door (2 wide, 3 high, on ground). With x,y the same bump mining applies and dips are built over; without door, the door goes on a side with room to stand outside, the side facing you first. Never builds in front of an existing door: a house must leave at least 2 free columns before a neighbouring door (two houses with 1 tile between them make one unreachable). Half blocks and slopes in the frame are hammered flat with your hammer (furniture and doors can't stand on them). Default size 10x6 outer (frame included): vanilla's minimum of 60 tiles; height at most 7 so the ceiling stays in reach. The reply lists frameBlocks, floorGapsToFill, mineFirst and flatten. " +
      "Materials come from your inventory: blocks for the frame (Wood first, then other wood, Stone, bricks, Dirt…; existing blocks in the frame's place are kept), owned doors/tables/chairs/torches/house walls first. What is missing is crafted first when possible (Wood Wall/Dirt Wall/Stone Wall 4 per block, Wooden Door, Chair, Table at a Work Bench in range; Work Bench as the table and Torch from Gel+Wood need no station). " +
      "If anything is short it fails up front with missing_item listing exact counts and where to get them; a site that isn't clear fails with bad_params naming the tile (tree: chop_tree it or pick another spot). " +
      "The player stands inside while building and walks out through the door at the end (move_to opens/closes doors). Then vanilla's housing query runs: succeeded = valid house; failed = built but vanilla's reason (e.g. missing walls). report.result: {x, y, width, height, door, valid, reason?, placed {frame block, flattened block, door, background wall, table, chair, torch}, skipped, skippedReasons, exitedThroughDoor, housing}." +
      COORDS +
      STARTS_TASK,
    shape: {
      x: tileX().optional().describe("Left column of the house frame; omit x and y to find a spot automatically"),
      y: tileY().optional().describe("Floor row of the house frame (its bottom row)"),
      width: int().min(8).max(30).optional().describe("Outer width incl. frame, default 10"),
      height: int().min(5).max(7).optional().describe("Outer height incl. frame, default 6"),
      door: z.enum(["left", "right"]).optional().describe("Side wall for the door; default a side with room to stand outside (2 wide, 3 high), the one facing the player first"),
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
      "YOU choose the weapon, ammo and strategy (get_inventory shows damage, useTime, ammoType; get_entities shows the enemy's defense): pass weapon (item name or slot), optionally ammo and strategy. The mod then only does what must happen within a second: aiming, dodging, stepping. Omitting weapon/strategy is a convenience (auto: highest damage-per-second after the target's defense, by weapon style), not a plan; in a real boss fight auto once switched to a useless Wand of Sparking. If the chosen weapon becomes unusable (gone, out of ammo/mana) it falls back to the best alternative and reports it (a weapon_switch event, a recent event and result.switches) and switches back once it works again; fallback: false stops the task with the reason instead. The chosen ammo is moved to the front of the ammo slots (vanilla fires the first matching stack in ammo slots 54-57, then the inventory) and put back when the task ends; when it runs out the next matching ammo fires (reported), or with fallback: false the task stops. strategy: auto (melee closes in, ranged keeps 8-15 tiles), kite (ranged: keep keep_distance tiles, default 12, and keep running along the ground/arena), hit_and_run (melee: step in, swing, back off), hold (stand your ground; only dodging moves you), flee_and_shoot (ranged: keep moving away, shooting backwards). Dodging predicted contact is always on. " +
      'Fight hostile enemies: closes in for melee or keeps its distance for ranged/magic (per strategy), and does not trade hits — when an enemy is about to touch you (from its hitbox and velocity) it backs off or jumps, never into a hole or lava, still swinging/shooting (melee becomes hit-and-run). target: an entity id from get_entities, "nearest", "boss" (the nearest boss in sight) or "all" = every visible hostile within radius, nearest first, until none remain (one call clears a group). Below retreat_below of max life with no healing potion available (quick-heal reflex handles the rest) it stops attacking, runs away for up to 3 s and fails with reason "low life". When it finishes, wait_for_task\'s report.result is {killed, kills: [names], damageTaken, durationS, dodges, retreated, potionsUsed, ammoLeft?, weapon, ammo?, strategy, switches?: [messages]}; progress shows the weapon + ammo in use (marked auto or fallback) and the strategy. Never targets town NPCs or critters.' +
      " BOSS FIGHTS (the target is a boss, e.g. target \"boss\"): it never pathfinds and never leaves the arena; with a ranged weapon it kites back and forth across the arena (turning near the ends) shooting with lead aim, and dodges dashes sideways, by jumping, or by dropping through the platform underfoot; minions about to reach you are shot first (kill_minions, default true), then back to the boss. A boss that leaves the screen stays the target: you don't see where it is, so it keeps kiting by the last sighting (carried along its last motion) and shoots again once it is back in view. Ends with killed, or fails with \"boss fled\" when it despawns alive (e.g. the Eye of Cthulhu at dawn). Boss results add {boss, bossKilled, bossLifeLeftPct (when not killed)}; progress shows boss life %, ammo, potions and dodges. Before a boss: build an arena first (build_platforms: 2-3 rows of platforms 60-100 tiles long, spacing ~5 so you can jump between rows), stand on it, carry a bow + plenty of arrows and healing potions, then attack with heal_below ~0.5 and retreat_below 0 (running away from a flying boss doesn't help). Use wait_for_task with a long timeout (fights take minutes) and check get_state between waits. get_boss_info has summon/strategy facts." +
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
      weapon: z
        .union([z.string().min(1), int().min(0).max(57)])
        .optional()
        .describe('The weapon to fight with: item name or inventory slot 0-57 (get_inventory). Used exactly; omitted or "auto" = the mod picks (convenience only)'),
      ammo: z
        .string()
        .min(1)
        .optional()
        .describe("Ammo item name to fire first (must fit the weapon's ammoType, e.g. Flaming Arrow for a bow); omitted = vanilla's ammo slot order"),
      strategy: z
        .enum(["auto", "kite", "hit_and_run", "hold", "flee_and_shoot"])
        .optional()
        .describe("How to move: auto (default), kite and flee_and_shoot need a ranged/magic weapon, hit_and_run a melee one, hold any"),
      keep_distance: z.number().min(3).max(30).optional().describe("kite / flee_and_shoot: distance to keep in tiles, default 12"),
      fallback: z
        .boolean()
        .optional()
        .describe("Default true: if the chosen weapon/ammo becomes unusable, use the best alternative (reported). false = stop the task instead"),
    },
  },
  {
    name: "set_reflexes",
    description:
      "Configure automatic behaviour while you drive: auto_defend (hit hostile enemies in reach, or that just hit you, while idle, waiting, moving, exploring, chopping, mining, placing, picking up or walking to an NPC: the task pauses and resumes after the fight; attack and an item use already under way keep the controls), quick_heal_threshold (quick-heal below this life fraction), pause_when_idle (singleplayer: pause the game while no task runs so you can think), weapon / ammo (what auto-defend fights with; \"auto\" = the best weapon by damage per second and vanilla's ammo order, the default; a chosen weapon that becomes unusable falls back to the best other one, with a weapon_switch event, and comes back once usable), platform_reserve (platforms every walk — move_to and the walks of mine, collect, explore_caves … — keeps for later descents unless no route fits without them; default 20, 0 = use all). Omitted fields keep their value; returns the current settings." +
      INSTANT_ACTION,
    shape: {
      auto_defend: z.boolean().optional(),
      quick_heal_threshold: z.number().min(0).max(1).optional().describe("Life fraction 0..1, e.g. 0.35"),
      pause_when_idle: z.boolean().optional(),
      weapon: z
        .union([z.string().min(1), int().min(0).max(57)])
        .optional()
        .describe('Auto-defend weapon: item name or inventory slot 0-57, or "auto"'),
      ammo: z.string().min(1).optional().describe('Auto-defend ammo item name (must fit the weapon), or "auto"'),
      platform_reserve: int().min(0).max(9999).optional().describe("Platforms walks keep for descents, default 20"),
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
      "Walk within talk range of a visible town NPC and open its chat. Pick the NPC by entity_id or by name (personal name like 'Demetrius' or profession like 'Arms Dealer', case-insensitive; the nearest visible match). Talk range is vanilla's (about 5 tiles across and 4 up/down from your centre) and works through walls: the walk goes to the nearest reachable spot within range, e.g. outside the NPC's house when its door is shut off. Fails with a hint if no such spot is reachable (wait for the NPC to come outside, or build a way/platform). When the task succeeds, wait_for_task's report.result is {name, chat, shop?: [{name, price}]} (shop only if the NPC sells things; price after happiness discounts, e.g. '1 gold 50 silver'). Use buy to purchase." +
      STARTS_TASK,
    shape: {
      entity_id: int().optional().describe("Town NPC id from get_entities"),
      name: z.string().min(1).optional().describe("Instead of entity_id: the NPC's name or profession, e.g. 'Demetrius' or 'Arms Dealer'"),
    },
    check: oneNpc,
  },
  {
    name: "buy",
    description:
      "Buy `count` of an item from a visible town NPC's shop with your own coins: walks within talk range of the NPC (as talk_to_npc: through walls, from the nearest reachable spot), opens its shop, buys through the game's normal shop purchase (price includes happiness and discounts), then closes the shop. All of the NPC's shops are searched. Stops early if money or inventory space runs out (still succeeds if at least one was bought). report.result: {npc, item, bought, requested, spent, stopped}. Use talk_to_npc first to see the shop and prices. Selling is not supported." +
      STARTS_TASK,
    shape: {
      entity_id: int().optional().describe("Town NPC id from get_entities"),
      name: z.string().min(1).optional().describe("Instead of entity_id: the NPC's name or profession, e.g. 'Merchant'"),
      item: z.string().min(1).describe("Item name as the shop lists it (case-insensitive)"),
      count: int().min(1).max(999).optional().describe("How many, default 1"),
    },
    check: oneNpc,
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
      "Break one piece of natural cave loot with your best pickaxe and pick up what drops: a Pot (one hit; coins, potions, torches…), a Life Crystal (+20 max life when used: use_item 'Life Crystal'), Life Fruit, or the sword in stone of an Enchanted Sword shrine. Give any tile of it (find category=valuable). Walks within reach like mine. Refused for chests (use loot_chest), Shadow Orbs / Crimson Hearts (world effects; never broken), altars, and anything inside an NPC house or near spawn. Walks there lighting dark tunnels like move_to (torches). When the task succeeds, report.result is {broke, x, y, pickedUp: [{name, count}], torchesPlaced}." +
      COORDS +
      STARTS_TASK,
    shape: {
      x: tileX(),
      y: tileY(),
      allow_orb: z.boolean().optional().describe("Break a Shadow Orb / Crimson Heart with your best hammer (first one drops a Musket + 100 Musket Balls; meteorite/goblin effects follow)"),
      allow_boss_summon: z.boolean().optional().describe("Also allow breaking the 3rd orb, which summons the Eater of Worlds / Brain of Cthulhu"),
      torches: z.boolean().optional().describe("Light the walk: place torches from the inventory in the dark (underground or in a dug tunnel, about every 10 tiles), default true"),
    },
  },
  {
    name: "blast",
    description:
      "Blow up the block at x,y with a thrown explosive from your inventory — the only way the AI uses explosives (use_item, attack and auto-defend never do). Use it for rock your pickaxe can't break: the Ebonstone / Crimstone around a Shadow Orb / Crimson Heart (pick power 65), or other hard rock. " +
      "Default explosive: the smallest blast that works, sticky first (Sticky Bomb, Bomb, Sticky Dynamite, Dynamite; Bomb/Sticky Bomb: radius 4 tiles, 3 s fuse; Dynamite: radius 7, 5 s; bouncy variants aren't supported). " +
      "Before throwing it checks the real tiles within the blast radius + 2 tiles of both the target and the predicted landing spot and refuses (protected_tile) if anything there is furniture, a chest, door, bed or crafting station, inside an NPC house or the spawn area, a Life Crystal / Life Fruit / sword in stone (collect it first), or a Shadow Orb / Crimson Heart without allow_orb (and the 3rd orb without allow_boss_summon, like collect). Altars and other explosion-proof blocks are fine; pots are fine too: the blast breaks them (listed in expectedDestroyed as 'Pot') and their loot is picked up with the other drops. " +
      "Explosives hurt you: it walks to a spot outside the damage area + a margin with a predicted throw (real throw speed and gravity, only through tiles you can see) that explodes at the target, throws with real controls, stays clear (backs off if it rolls close) until it explodes, retries once if the target survived, then picks up the drops. Auto-defend can't interrupt from the throw to the explosion. " +
      "A buried target (e.g. an orb deep in rock) is refused with the closest reachable spot: blast the exposed rock nearest to you first (a block next to open space you can see) and work inward, a layer per blast. Also refused when no safe spot to stand exists (a cramped cave: Dynamite needs ~10 tiles of room). Throwing spots the pathfinder proves unreachable are skipped; a spot whose walk fails is remembered for 5 minutes (it and its neighbours are not picked again) and the next best spot is tried. " +
      "Reply: {item, stand, predictedExplosion, radius, fuseSeconds, expectedDestroyed: {count, byName, tiles, unseen, wallsInRadius, orbs}} — blocks and background walls in the radius are lost. When the task succeeds, report.result is {item, x, y, target, targetDestroyed, blasts, destroyed, destroyedByName, pickedUp: [{name, count}]}." +
      COORDS +
      STARTS_TASK,
    shape: {
      x: tileX(),
      y: tileY(),
      item: z.string().min(1).optional().describe("Explosive to throw (e.g. 'Sticky Bomb', 'Bomb', 'Dynamite'); default: the smallest that can be thrown safely"),
      allow_orb: z.boolean().optional().describe("Allow a Shadow Orb / Crimson Heart in the blast (breaking it on purpose: the first drops a Musket)"),
      allow_boss_summon: z.boolean().optional().describe("Also allow breaking the 3rd orb, which summons the Eater of Worlds / Brain of Cthulhu"),
    },
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
      "Explore caves systematically through their open space, revealing the map, to find Life Crystals and other cave loot. Started in the surface layer it first goes underground (walks down, or digs a 2-wide shaft like explore down). Then it repeatedly walks to a known ledge next to the nearest reachable edge between known open space and unknown tiles (digging only through walls up to 3 tiles thick to reach the next cave), lights the dark opening with a torch from your inventory (also underground whenever it's dark and no torch is within ~10 tiles), looks, and moves on. A dark cave whose floor isn't known yet is lit from its edge first. " +
      "When the cave pocket is exhausted it tunnels toward the nearest large unrevealed region (down preferred, else left/right): a 2-wide shaft or 3-tall tunnel of at most 40 tiles per tunnel (max 10 per area; an attempt that dug nothing — its start unreachable — doesn't count and another start is tried) through rock your pickaxe can break, never toward known lava, through water/honey, protected or too-hard tiles, never into an unsafe drop, torches in the dark; as soon as it breaks into open space it explores again (radius then counts from there). When the whole neighbourhood is exhausted (no opening, no short tunnel, or 6 walks in a row failed) it relocates: travels to the nearest large unexplored region on the map ('?' country, up to ~250 tiles left/right at the current depth or down), walking through known caves and tunnelling through rock with the same safety rules and torches, then explores there (max 4 relocations per call). Failed walks and tunnel starts are remembered across calls (per world, ~20 min), so the next call doesn't retry them. Avoids lava (never walks through it, skips openings within 3 tiles of it) and unsafe drops. " +
      "Stops (succeeded) when something in stop_on that wasn't visible at the start comes into view — report.reason says what and where (a Life Crystal: 'found a Life Crystal at (x,y): collect it for +20 max life'), report.result.stoppedAt has it; each thing stops only one call (later calls just list it in found, for ~20 min), and rigged Dead Man's Chests and chests you opened before that are empty now never stop it — or when max_seconds is up or no reachable opening, no safe tunnel and no relocation is left (the reason says why each direction was refused); fails when walks keep failing and neither a tunnel nor a relocation is possible, or if it can't get underground. " +
      "Kinds: life_crystal, life_fruit, pot, chest, enchanted_sword, altar, orb, ore, fallen_star (default stop_on: life_crystal, chest, enchanted_sword, life_fruit; [] = only time/radius). collect_pots: true breaks visible pots within 12 tiles on the way. " +
      "Result: {revealedTiles (approximate), found: [{kind, name, x, y}] (everything newly seen, max 30), torchesPlaced, potsBroken, legs, descended (tiles dug getting underground), tunnels (that dug or moved), tunnelled (tiles dug in tunnels and relocations), failedTunnels (attempts that couldn't start), relocations, relocated (tiles travelled relocating), lifeCrystals: [{kind, name, x, y}] (every Life Crystal seen, also ones in view at the start), stoppedBecause, stoppedAt?}. Then: collect for life crystals/pots, loot_chest for chests, mine_vein for ore." +
      STARTS_TASK,
    shape: {
      radius: int().min(10).max(200).optional().describe("Max distance in tiles from the start (after a tunnel: from where it broke through), default 80"),
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
