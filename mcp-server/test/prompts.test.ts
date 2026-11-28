import { afterEach, describe, expect, it } from "vitest";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { registerPrompts } from "../src/prompts.js";
import { registerTools } from "../src/tools.js";

const clients: Client[] = [];
afterEach(async () => {
  for (const c of clients.splice(0)) await c.close();
});

async function setup() {
  const server = new McpServer({ name: "terraria", version: "test" });
  registerTools(server, { call: async () => ({}), drainEvents: () => [] });
  registerPrompts(server);
  const [a, b] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "test", version: "test" });
  await Promise.all([server.connect(a), client.connect(b)]);
  clients.push(client);
  return client;
}

async function text(client: Client, name: string, args?: Record<string, string>) {
  const r = await client.getPrompt({ name, arguments: args });
  expect(r.messages).toHaveLength(1);
  expect(r.messages[0].role).toBe("user");
  const c = r.messages[0].content;
  if (c.type !== "text") throw new Error("expected text");
  return c.text;
}

describe("registerPrompts", () => {
  it("advertises the prompts capability and lists play, prepare_boss, after_death with their arguments", async () => {
    const client = await setup();
    expect(client.getServerCapabilities()?.prompts).toBeDefined();
    expect(client.getServerCapabilities()?.resources).toBeUndefined();
    const prompts = (await client.listPrompts()).prompts;
    const args = Object.fromEntries(
      prompts.map((p) => [p.name, (p.arguments ?? []).map((a) => `${a.name}${a.required ? "" : "?"}`)]),
    );
    expect(args).toEqual({ play: ["goal", "time_budget?"], prepare_boss: ["boss"], after_death: [] });
    for (const p of prompts) expect(p.description).toBeTruthy();
  });

  it("play substitutes the goal and only mentions the time budget when given", async () => {
    const client = await setup();
    const t = await text(client, "play", { goal: "beat the Eye of Cthulhu" });
    expect(t).toContain("toward this goal: beat the Eye of Cthulhu.");
    expect(t).not.toContain("Time budget");
    for (const w of ["request_control", "Insert", "wait_for_task", "lastDeaths", "Fair play"]) expect(t).toContain(w);
    expect(await text(client, "play", { goal: "x", time_budget: "20 minutes" })).toContain("Time budget: 20 minutes;");
  });

  it("prepare_boss substitutes the boss name into the checklist", async () => {
    const client = await setup();
    const t = await text(client, "prepare_boss", { boss: "King Slime" });
    expect(t).toContain("Prepare for King Slime");
    expect(t).toContain('get_boss_info boss "King Slime"');
    for (const w of ["build_platforms", "use_item", "heal_below", 'attack target "boss"', "80+"]) expect(t).toContain(w);
  });

  it("after_death takes no arguments and points at lastDeaths", async () => {
    const client = await setup();
    const t = await text(client, "after_death");
    for (const w of ["lastDeaths", "pick_up_items", "move_to"]) expect(t).toContain(w);
  });

  it("a missing required argument is rejected", async () => {
    const client = await setup();
    await expect(client.getPrompt({ name: "prepare_boss", arguments: {} })).rejects.toThrow();
  });

  it("every tool a prompt names exists", async () => {
    const client = await setup();
    const tools = new Set((await client.listTools()).tools.map((t) => t.name));
    const words = new Set<string>();
    for (const p of (await client.listPrompts()).prompts) {
      const args = Object.fromEntries((p.arguments ?? []).map((a) => [a.name, "x"]));
      for (const w of (await text(client, p.name, args)).match(/\b[a-z]+(?:_[a-z]+)+\b/g) ?? []) words.add(w);
    }
    const notTools = new Set([
      "quick_heal_threshold", "heal_below", "retreat_below", "with_intermediates", "timeout_s", "timed_out",
      "control_changed", "took_damage", "player_died", "weapon_switch", "inventory_full", "time_budget",
      "prepare_boss", "after_death", "user_has_control", "quick_heal",
    ]);
    const unknown = [...words].filter((w) => !tools.has(w) && !notTools.has(w));
    expect(unknown).toEqual([]);
  });
});
