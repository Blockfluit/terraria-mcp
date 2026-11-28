/**
 * Builds the MCP tool reference (docs/MCP-tools.md) and the tool index inside docs/MCP.md from the live tool
 * definitions: it registers the real tools on an in-memory MCP server and reads them back with tools/list, so the
 * names, descriptions, JSON schemas and annotations are exactly what an MCP client sees.
 *
 * Regenerate with `npm run docs`; test/docs.test.ts fails while the checked-in files are out of date.
 */
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { registerTools, SERVER_INFO, SERVER_INSTRUCTIONS } from "../src/tools.js";
import { registerPrompts } from "../src/prompts.js";

export const DOCS_DIR = resolve(dirname(fileURLToPath(import.meta.url)), "../../docs");
export const TOOLS_DOC = resolve(DOCS_DIR, "MCP-tools.md");
export const OVERVIEW_DOC = resolve(DOCS_DIR, "MCP.md");
export const INDEX_BEGIN = "<!-- BEGIN GENERATED TOOL INDEX (npm run docs) -->";
export const INDEX_END = "<!-- END GENERATED TOOL INDEX -->";
export const PROMPTS_BEGIN = "<!-- BEGIN GENERATED PROMPT INDEX (npm run docs) -->";
export const PROMPTS_END = "<!-- END GENERATED PROMPT INDEX -->";

/**
 * Every tool belongs to exactly one area and has a one-line summary. A tool added to tools.ts without an entry
 * here (or an entry for a tool that no longer exists) makes generation throw, so the docs can't silently miss it.
 */
export const AREAS: { area: string; tools: Record<string, string> }[] = [
  {
    area: "Control",
    tools: {
      get_control: "Who drives the character right now: the user or the AI.",
      request_control: "Switch AI mode on; refused after the user took control back with Insert, or while dead.",
    },
  },
  {
    area: "Observation",
    tools: {
      get_state: "Player status (position, life, buffs, time, biome, bosses, housing, last deaths) plus recent events. Start every decision here.",
      get_map: "Fair-vision ASCII map around the player with a legend ('?' = never seen).",
      get_entities: "Visible enemies, town NPCs, critters, dropped items and hostile projectiles, bosses first.",
      find: "Nearest visible ores, trees, chests, stations, beds, NPCs, items, blocks, cave valuables or plants.",
      inspect: "Exactly what is on one visible tile (tile, wall, liquid, protection, shape, pick power).",
      get_inventory: "Hotbar, inventory, armor, accessories, ammo and coins, with weapon/tool/consumable tags.",
      list_recipes: "Recipes craftable now, or every recipe for a named item.",
      check_housing: "Vanilla housing query for a room, plus residents, homeless NPCs and who could move in.",
      get_boss_info: "Static prep facts for Eye of Cthulhu and King Slime (summon, recipe, stats, tips, defeated).",
    },
  },
  {
    area: "Tasks",
    tools: {
      get_task: "The running task and the last finished one, with status, reason, progress and result.",
      wait_for_task: "Block until the current task ends or timeout_s passes (MCP-server side, polls get_task).",
      cancel_task: "Stop the running task.",
      wait: "Stand still for up to 60 s with reflexes active.",
    },
  },
  {
    area: "Movement and exploration",
    tools: {
      move_to: "A* walk to a tile or entity: jumps, drops, swims, digs, builds platforms, opens doors, lights tunnels.",
      explore: "Travel a distance in a direction, digging a shaft or tunnel when nothing visible is walkable.",
      explore_caves: "Systematic cave exploration frontier by frontier; stops when loot such as a Life Crystal comes into view.",
      door: "Open or close a door within reach.",
    },
  },
  {
    area: "Gathering and mining",
    tools: {
      mine: "Mine one tile with the best pickaxe (also harvests herbs).",
      mine_area: "Mine a rectangle (max 200 tiles), skipping what can't or mustn't be mined.",
      mine_vein: "Mine a whole ore vein, or N tiles of one block type, from any visible tile of it.",
      chop_tree: "Chop a tree (given or nearest allowed) and collect the wood.",
      pick_up_items: "Walk over visible dropped items in a radius.",
      collect: "Break cave loot (pot, Life Crystal, Life Fruit, sword in stone) and pick up the drops.",
      blast: "Blow up a block with a thrown explosive after a full safety check of the blast area.",
    },
  },
  {
    area: "Building",
    tools: {
      place: "Place one block, platform, torch or piece of furniture.",
      build_platforms: "Lay rows of platforms or blocks (bridges, floors, boss arenas).",
      build_house: "Build a valid town-NPC house from your own materials, crafting what is missing.",
    },
  },
  {
    area: "Crafting and inventory",
    tools: {
      craft: "Craft an item with real ingredients and stations, optionally crafting intermediates first.",
      select_hotbar: "Select a hotbar slot as the held item.",
      equip: "Equip armor or an accessory (optionally into a given accessory slot).",
      unequip: "Take off armor or an accessory.",
      use_item: "Use an item once (potion, tool, summon), optionally aimed; queues back to back.",
      swap_slots: "Swap two inventory slots (e.g. to order ammo).",
      drop_item: "Throw items on the ground or trash them to free slots.",
    },
  },
  {
    area: "Combat and reflexes",
    tools: {
      attack: "Fight a target, the nearest, every visible hostile, or a boss, with the weapon, ammo and strategy you choose.",
      set_reflexes: "Configure auto-defend, quick-heal, idle pause, auto-defend weapon/ammo, platform reserve, trash list.",
    },
  },
  {
    area: "NPCs and shops",
    tools: {
      talk_to_npc: "Walk into talk range of a town NPC, open its chat and read its shop.",
      buy: "Buy items from a town NPC's shop with your own coins.",
    },
  },
  {
    area: "Chests and beds",
    tools: {
      open_chest: "Walk to a container and open it; its contents become visible.",
      chest_deposit: "Put items into the open chest (item, Deposit All, Quick Stack).",
      chest_withdraw: "Take items out of the open chest (item or Loot All).",
      close_chest: "Close the open chest.",
      loot_chest: "open_chest + Loot All + close_chest in one task.",
      use_bed: "Set the spawn point at a bed.",
    },
  },
];

interface JsonSchema {
  type?: string;
  const?: unknown;
  enum?: unknown[];
  anyOf?: JsonSchema[];
  items?: JsonSchema;
  properties?: Record<string, JsonSchema>;
  required?: string[];
  minimum?: number;
  maximum?: number;
  exclusiveMinimum?: number;
  exclusiveMaximum?: number;
  minLength?: number;
  default?: unknown;
  description?: string;
}

export interface ListedTool {
  name: string;
  description?: string;
  inputSchema: JsonSchema;
  annotations?: { readOnlyHint?: boolean; destructiveHint?: boolean };
}

export interface ListedPrompt {
  name: string;
  title?: string;
  description?: string;
  arguments?: { name: string; description?: string; required?: boolean }[];
}

export interface ServerSurface {
  tools: ListedTool[];
  prompts: ListedPrompt[];
  /** Prompt name -> the text prompts/get returns with each argument filled in as `<name>`. */
  promptTexts: Record<string, string>;
  capabilities: Record<string, unknown>;
}

/** Everything an MCP client sees: tools, prompts (with their text) and capabilities (fake mod: nothing is called). */
export async function describeServer(): Promise<ServerSurface> {
  const server = new McpServer({ ...SERVER_INFO }, { instructions: SERVER_INSTRUCTIONS });
  registerTools(server, { call: async () => ({}), drainEvents: () => [] });
  registerPrompts(server);
  const [a, b] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "gen-docs", version: "0" });
  await Promise.all([server.connect(a), client.connect(b)]);
  try {
    const tools = (await client.listTools()).tools as ListedTool[];
    const prompts = (await client.listPrompts()).prompts as ListedPrompt[];
    const promptTexts: Record<string, string> = {};
    for (const p of prompts) {
      const args = Object.fromEntries((p.arguments ?? []).map((a) => [a.name, `<${a.name}>`]));
      const msg = (await client.getPrompt({ name: p.name, arguments: args })).messages[0];
      promptTexts[p.name] = msg.content.type === "text" ? msg.content.text : "";
    }
    return { tools, prompts, promptTexts, capabilities: client.getServerCapabilities() ?? {} };
  } finally {
    await client.close();
  }
}

/** The tools exactly as an MCP client lists them. */
export async function listTools(): Promise<ListedTool[]> {
  return (await describeServer()).tools;
}

const SAFE = Number.MAX_SAFE_INTEGER;

function typeOf(s: JsonSchema): string {
  if (s.anyOf) return s.anyOf.map(typeOf).join(" \\| ");
  if (s.const !== undefined) return JSON.stringify(s.const);
  if (s.enum) return s.enum.map((v) => JSON.stringify(v)).join(" \\| ");
  if (s.type === "array") return `${s.items ? typeOf(s.items) : "unknown"}[]`;
  if (s.type === "object" && s.properties) {
    const req = new Set(s.required ?? []);
    return `{${Object.entries(s.properties)
      .map(([k, v]) => `${k}${req.has(k) ? "" : "?"}: ${typeOf(v)}`)
      .join(", ")}}`;
  }
  return s.type ?? "any";
}

function rangeOf(s: JsonSchema): string {
  const parts: string[] = [];
  const lo = s.minimum !== undefined && s.minimum > -SAFE ? `≥ ${s.minimum}` : s.exclusiveMinimum !== undefined ? `> ${s.exclusiveMinimum}` : "";
  const hi = s.maximum !== undefined && s.maximum < SAFE ? `≤ ${s.maximum}` : s.exclusiveMaximum !== undefined ? `< ${s.exclusiveMaximum}` : "";
  if (lo || hi) parts.push([lo, hi].filter(Boolean).join(", "));
  if (s.minLength) parts.push(s.minLength === 1 ? "non-empty" : `length ≥ ${s.minLength}`);
  if (s.anyOf) for (const alt of s.anyOf) {
    const r = rangeOf(alt);
    if (r) parts.push(`${typeOf(alt)}: ${r}`);
  }
  return parts.join("; ");
}

/** Text that goes into Markdown as prose: keep `<item>`-style placeholders and stray asterisks literal. */
const prose = (t: string) => t.replace(/</g, "&lt;").replace(/\*/g, "\\*");
const cell = (t: string) => prose(t).replace(/\|/g, "\\|").replace(/\n/g, " ");

export function kindOf(t: ListedTool): string {
  if (t.name === "wait_for_task") return "read-only, blocks";
  if (t.annotations?.readOnlyHint) return "read-only";
  const d = t.description ?? "";
  if (d.includes("Starts an async task")) return "async task";
  if (d.includes("Instant action")) return "instant action";
  return "action";
}

function checkAreas(tools: ListedTool[]): void {
  const listed = new Set(tools.map((t) => t.name));
  const documented = AREAS.flatMap((a) => Object.keys(a.tools));
  const missing = [...listed].filter((n) => !documented.includes(n));
  const stale = documented.filter((n) => !listed.has(n));
  const dupes = documented.filter((n, i) => documented.indexOf(n) !== i);
  if (missing.length || stale.length || dupes.length)
    throw new Error(
      `scripts/tool-reference.ts AREAS out of sync with tools.ts: missing [${missing}], unknown [${stale}], duplicated [${dupes}]`,
    );
}

export function renderIndex(tools: ListedTool[]): string {
  checkAreas(tools);
  const byName = new Map(tools.map((t) => [t.name, t]));
  const out: string[] = [INDEX_BEGIN, "", `${tools.length} tools. Full parameters and descriptions: [MCP-tools.md](MCP-tools.md).`, ""];
  for (const { area, tools: entries } of AREAS) {
    out.push(`**${area}**`, "", "| Tool | Kind | Purpose |", "|---|---|---|");
    for (const [name, summary] of Object.entries(entries))
      out.push(`| [\`${name}\`](MCP-tools.md#${name}) | ${kindOf(byName.get(name)!)} | ${cell(summary)} |`);
    out.push("");
  }
  out.push(INDEX_END);
  return out.join("\n");
}

function renderTool(t: ListedTool): string {
  const ann = t.annotations ?? {};
  const out = [
    `### ${t.name}`,
    "",
    `Kind: **${kindOf(t)}** · annotations: readOnlyHint=${ann.readOnlyHint ?? "unset"}` +
      (ann.destructiveHint !== undefined ? `, destructiveHint=${ann.destructiveHint}` : ""),
    "",
    prose(t.description ?? ""),
    "",
  ];
  const props = Object.entries(t.inputSchema.properties ?? {});
  if (props.length === 0) {
    out.push("Parameters: none.", "");
    return out.join("\n");
  }
  const req = new Set(t.inputSchema.required ?? []);
  out.push("| Parameter | Type | Required | Range | Default | Description |", "|---|---|---|---|---|---|");
  for (const [name, s] of props)
    out.push(
      `| \`${name}\` | \`${typeOf(s).replace(/`/g, "'")}\` | ${req.has(name) ? "yes" : "no"} | ${cell(rangeOf(s))} | ${
        s.default !== undefined ? `\`${JSON.stringify(s.default)}\`` : ""
      } | ${cell(s.description ?? "")} |`,
    );
  out.push("");
  return out.join("\n");
}

export function renderToolsDoc(tools: ListedTool[], prompts = ""): string {
  checkAreas(tools);
  const byName = new Map(tools.map((t) => [t.name, t]));
  const out = [
    "# MCP tool and prompt reference",
    "",
    "<!-- GENERATED by `npm run docs` (mcp-server/scripts/gen-docs.ts) from mcp-server/src/tools.ts and src/prompts.ts. Do not edit by hand. -->",
    "",
    `Server \`${SERVER_INFO.name}\` ${SERVER_INFO.version}. Every tool exactly as \`tools/list\` returns it: description (what the LLM reads), ` +
      "input schema and annotations; then every prompt as `prompts/get` returns it. Overview, events, errors and environment: [MCP.md](MCP.md).",
    "",
    "Kinds: **read-only** observes and never needs control; **async task** starts a task and returns at once (follow with " +
      "`wait_for_task`; it replaces a running task); **instant action** acts and returns the outcome. Every non-read-only tool " +
      "except `request_control` fails with `user_has_control` while the user drives.",
    "",
    "## Server instructions",
    "",
    "Sent in the `initialize` result (`instructions`), verbatim:",
    "",
    "```text",
    SERVER_INSTRUCTIONS,
    "```",
    "",
  ];
  for (const { area, tools: entries } of AREAS) {
    out.push(`## ${area}`, "");
    for (const name of Object.keys(entries)) out.push(renderTool(byName.get(name)!));
  }
  if (prompts) out.push(prompts);
  return out.join("\n").replace(/\n{3,}/g, "\n\n").trimEnd() + "\n";
}

const argList = (p: ListedPrompt) =>
  (p.arguments ?? []).map((a) => `\`${a.name}\`${a.required ? "" : " (optional)"}`).join(", ") || "none";

export function renderPromptIndex(s: ServerSurface): string {
  const out = [
    PROMPTS_BEGIN,
    "",
    `Advertised capabilities: \`${JSON.stringify(s.capabilities)}\`.`,
    "",
    `${s.prompts.length} prompts. Full text: [MCP-tools.md](MCP-tools.md#prompts).`,
    "",
    "| Prompt | Arguments | Purpose |",
    "|---|---|---|",
  ];
  for (const p of s.prompts) out.push(`| [\`${p.name}\`](MCP-tools.md#prompt-${p.name}) | ${argList(p)} | ${cell(p.description ?? "")} |`);
  out.push("", PROMPTS_END);
  return out.join("\n");
}

function renderPrompts(s: ServerSurface): string {
  const out = [
    "## Prompts",
    "",
    "User-invoked templates (`prompts/list`, `prompts/get`); Claude Code shows them as `/mcp__terraria__<name>`. " +
      "The text below is what `prompts/get` returns, with each argument shown as `<name>`.",
    "",
  ];
  for (const p of s.prompts) {
    out.push(`### prompt: ${p.name}`, "", `**${prose(p.title ?? p.name)}**: ${prose(p.description ?? "")}`, "");
    if (p.arguments?.length) {
      out.push("| Argument | Required | Description |", "|---|---|---|");
      for (const a of p.arguments) out.push(`| \`${a.name}\` | ${a.required ? "yes" : "no"} | ${cell(a.description ?? "")} |`);
      out.push("");
    } else out.push("Arguments: none.", "");
    out.push("```text", s.promptTexts[p.name], "```", "");
  }
  return out.join("\n");
}

function replaceBetween(doc: string, begin: string, end: string, content: string): string {
  const start = doc.indexOf(begin);
  const stop = doc.indexOf(end);
  if (start < 0 || stop < start) throw new Error(`docs/MCP.md lacks the ${begin} ... ${end} markers`);
  return doc.slice(0, start) + content + doc.slice(stop + end.length);
}

/** docs/MCP.md with its generated tool index replaced by the current one. */
export function withIndex(overview: string, index: string): string {
  return replaceBetween(overview, INDEX_BEGIN, INDEX_END, index);
}

/** The generated files' expected contents: {path: content}. */
export async function generate(): Promise<Record<string, string>> {
  const surface = await describeServer();
  const overview = withIndex(readFileSync(OVERVIEW_DOC, "utf8"), renderIndex(surface.tools));
  return {
    [TOOLS_DOC]: renderToolsDoc(surface.tools, renderPrompts(surface)),
    [OVERVIEW_DOC]: replaceBetween(overview, PROMPTS_BEGIN, PROMPTS_END, renderPromptIndex(surface)),
  };
}
