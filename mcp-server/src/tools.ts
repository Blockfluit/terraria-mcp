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
- Control: the user must hand over control by pressing the toggle key in game (default Insert). Until then observation tools work but action tools fail with user_has_control: ask the user to press Insert. Only the user pressing Insert again takes control back (it cancels the current task); dying cancels the current task but you keep control.
- Coordinates: absolute tile coordinates, y grows DOWNWARD (smaller y = higher up). The player's position (x,y) is the bottom-left tile of its 2-wide x 3-tall body (feet-left): it occupies x..x+1, y-2..y, standing on tile row y+1.
- Fair vision: you only see what the player could see. '?' in get_map = unknown/unrevealed tiles; find/get_entities only report visible things.
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
  "The user is driving. Ask the user to press Insert in Terraria to hand control to the AI, then retry. Observation tools still work meanwhile.";

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
      "Check whether the user has handed control to you. It cannot take control by itself: if granted is false, ask the user to press Insert in Terraria, then call this again.",
    readOnly: true,
  },

  // Observation
  {
    name: "get_state",
    description:
      "Player status: position {x,y}, life/mana/defense, buffs, held item, time of day, biome, depth layer, spawn point, driver, current task and recent events (damage, kills, pickups). Start every decision here." +
      COORDS,
    readOnly: true,
  },
  {
    name: "get_map",
    description:
      "ASCII map centred on the player with a legend and the top-left corner's coordinates. '@' player, '#' solid, '.' air, '=' platform, '~' water, 'L' lava, 'T' tree, 'C' chest, 'W' crafting station, lowercase letters = ores (see legend), 'E' enemy, 'N' town NPC, 'i' dropped item, '?' unknown (not yet seen — may be anything). Row r, column c is tile (left+c, top+r)." +
      COORDS,
    shape: { radius: int().min(1).max(40).optional().describe("Half-width in tiles, default 20, max 40") },
    readOnly: true,
  },
  {
    name: "get_entities",
    description:
      "Visible NPCs, enemies, critters and dropped items near the player, nearest first (max 30): {id, name, kind: hostile|town|critter|item, x, y, life, distance}. Use id with move_to/attack/talk_to_npc.",
    shape: { radius: int().min(1).optional().describe("Search radius in tiles, default 40") },
    readOnly: true,
  },
  {
    name: "find",
    description:
      "Nearest visible things of a category (max 10, nearest first): ores, trees, chests, crafting stations, NPCs or dropped items. Only searches tiles the player has seen; explore to reveal more." +
      COORDS,
    shape: {
      category: z.enum(["ore", "tree", "chest", "station", "npc", "item"]),
      name: z.string().optional().describe("Optional name filter, e.g. 'Copper Ore', 'Work Bench', 'Guide'"),
      radius: int().min(1).optional().describe("Search radius in tiles, default 60"),
    },
    readOnly: true,
  },
  {
    name: "get_inventory",
    description:
      "Hotbar (slots 0-9), main inventory, armor, accessories, ammo, coins and the selected hotbar slot. Empty slots are omitted.",
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
      "Current task and the last finished one: {current, lastFinished}, each {name, status: running|succeeded|failed|cancelled, reason, progress, result?} (result only for tasks that return data, e.g. talk_to_npc, attack). Use wait_for_task to block instead of polling.",
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
      "Travel roughly `distance` tiles in a direction toward the farthest reachable visible ground, revealing the map as it goes. Good for finding ores, trees and caves." +
      STARTS_TASK,
    shape: {
      direction: z.enum(["left", "right", "down", "up"]),
      distance: int().min(1).optional().describe("Tiles to cover, default 50"),
    },
  },

  // Gathering
  {
    name: "mine",
    description:
      "Mine one tile: walks within reach, picks the best pickaxe and digs it. Fails on protected tiles or if the pickaxe is too weak." +
      COORDS +
      STARTS_TASK,
    shape: { x: tileX(), y: tileY() },
  },
  {
    name: "mine_area",
    description:
      "Mine every tile in the rectangle (x1,y1)-(x2,y2), inclusive, at most 200 tiles, top row first. Air, trees and " +
      "hammer-only tiles are skipped; a protected or not-visible tile inside the area fails the call, naming the tile." +
      COORDS +
      STARTS_TASK,
    shape: { x1: tileX(), y1: tileY(), x2: tileX(), y2: tileY() },
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
    description: "Equip armor or an accessory from the inventory, by item name." + INSTANT_ACTION,
    shape: { item: z.string().min(1).describe("Item display name (case-insensitive) or internal name") },
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
      "Craft an item `count` times, consuming ingredients. Needs the station in range: fails with missing_station or missing_item naming what is needed. Check list_recipes first." +
      INSTANT_ACTION,
    shape: {
      item: z.string().min(1).describe("Result item name (case-insensitive)"),
      count: int().min(1).optional().describe("Times to craft, default 1"),
    },
  },

  // Combat
  {
    name: "attack",
    description:
      'Fight hostile enemies: picks the best weapon, closes in for melee or keeps 8-15 tiles for ranged/magic, and does not trade hits — when an enemy is about to touch you (from its hitbox and velocity) it backs off or jumps, never into a hole or lava, still swinging/shooting (melee becomes hit-and-run). target: an entity id from get_entities, "nearest", or "all" = every visible hostile within radius, nearest first, until none remain (one call clears a group). Below retreat_below of max life with no healing potion available (quick-heal reflex handles the rest) it stops attacking, runs away for up to 3 s and fails with reason "low life". When it finishes, wait_for_task\'s report.result is {killed, kills: [names], damageTaken, durationS, dodges, retreated}. Never targets town NPCs or critters.' +
      STARTS_TASK,
    shape: {
      target: z
        .union([int(), z.literal("nearest"), z.literal("all")])
        .describe('Entity id of a hostile enemy, "nearest", or "all" (clear every visible hostile within radius)'),
      radius: z.number().min(1).max(40).optional().describe('For target "all": search radius in tiles, default 25'),
      retreat_below: z
        .number()
        .min(0)
        .lt(1)
        .optional()
        .describe("Retreat (and fail with 'low life') below this life fraction when no healing is available; default 0.3, 0 = never"),
    },
  },
  {
    name: "set_reflexes",
    description:
      "Configure automatic behaviour while you drive: auto_defend (hit hostile enemies in reach while idle or moving), quick_heal_threshold (quick-heal below this life fraction), pause_when_idle (singleplayer: pause the game while no task runs so you can think). Omitted fields keep their value; returns the current settings." +
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
      "Walk within talk range of a visible town NPC and open its chat. When the task succeeds, wait_for_task's report.result is {name, chat, shop?: [{name, price}]} (shop only if the NPC sells things; price after happiness discounts, e.g. '1 gold 50 silver'). Buying/selling is not supported." +
      STARTS_TASK,
    shape: { entity_id: int().describe("Town NPC id from get_entities") },
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
