import { afterEach, describe, expect, it } from "vitest";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { ModError, type ModEvent } from "../src/connection.js";
import { registerTools, type ModClient } from "../src/tools.js";

type Responder = (method: string, params: Record<string, unknown>) => unknown;

class FakeConn implements ModClient {
  calls: { method: string; params: Record<string, unknown> }[] = [];
  events: ModEvent[] = [];
  constructor(private respond: Responder = () => ({})) {}
  async call(method: string, params: object = {}): Promise<unknown> {
    const p = params as Record<string, unknown>;
    this.calls.push({ method, params: p });
    return this.respond(method, p);
  }
  drainEvents(): ModEvent[] {
    return this.events.splice(0);
  }
}

const clients: Client[] = [];
afterEach(async () => {
  for (const c of clients.splice(0)) await c.close();
});

async function setup(conn: ModClient) {
  const server = new McpServer({ name: "terraria", version: "test" });
  registerTools(server, conn);
  const [a, b] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "test", version: "test" });
  await Promise.all([server.connect(a), client.connect(b)]);
  clients.push(client);
  return client;
}

type CallResult = { isError?: boolean; content: { type: string; text: string }[] };
async function call(client: Client, name: string, args: Record<string, unknown> = {}) {
  return (await client.callTool({ name, arguments: args })) as CallResult;
}

describe("registerTools", () => {
  it("registers every protocol method except ping, plus wait_for_task", async () => {
    const client = await setup(new FakeConn());
    const names = (await client.listTools()).tools.map((t) => t.name).sort();
    expect(names).toEqual(
      [
        "get_control", "request_control", "get_state", "get_map", "get_entities", "find",
        "get_inventory", "get_task", "cancel_task", "move_to", "explore", "mine", "mine_area",
        "chop_tree", "pick_up_items", "select_hotbar", "equip", "use_item", "place", "craft",
        "list_recipes", "attack", "set_reflexes", "wait", "talk_to_npc", "wait_for_task",
        "build_platforms", "unequip", "buy",
      ].sort(),
    );
  });

  it("observation tools describe the boss/threat/projectile/item-tag fields", async () => {
    const client = await setup(new FakeConn());
    const tools = (await client.listTools()).tools;
    const desc = (name: string) => tools.find((t) => t.name === name)?.description ?? "";
    for (const word of ["nearbyHostiles", "boss", "worldEvents"]) expect(desc("get_state")).toContain(word);
    for (const word of ["projectile", "boss: true", "vx/vy", "damage", "defense"]) expect(desc("get_entities")).toContain(word);
    for (const word of ["weapon", "damage", "heal", "ammo", "consumable", "placeable", "pick/axe/hammer"])
      expect(desc("get_inventory")).toContain(word);
    for (const name of ["get_state", "get_entities", "get_inventory"])
      expect(tools.find((t) => t.name === name)?.annotations?.readOnlyHint).toBe(true);
  });

  it("get_entities passes optional entity fields through unchanged", async () => {
    const entity = { id: 2003, name: "Slime Spike", kind: "projectile", x: 10, y: 20, distance: 4.1, vx: -7.5, vy: 1.2, damage: 18 };
    const conn = new FakeConn(() => ({ entities: [entity] }));
    const client = await setup(conn);
    const body = JSON.parse((await call(client, "get_entities", { radius: 30 })).content[0].text);
    expect(conn.calls).toEqual([{ method: "get_entities", params: { radius: 30 } }]);
    expect(body).toEqual({ entities: [entity] });
  });

  it("move_to forwards x,y,allow_dig", async () => {
    const conn = new FakeConn(() => ({ started: "move_to" }));
    const client = await setup(conn);
    const res = await call(client, "move_to", { x: 100, y: 250, allow_dig: false });
    expect(res.isError).toBeFalsy();
    expect(conn.calls).toEqual([{ method: "move_to", params: { x: 100, y: 250, allow_dig: false } }]);
    expect(JSON.parse(res.content[0].text)).toEqual({ started: "move_to" });
  });

  it("user_has_control becomes isError with toggle-key hint text", async () => {
    const conn = new FakeConn(() => {
      throw new ModError("user_has_control", "the user is driving");
    });
    const client = await setup(conn);
    const res = await call(client, "move_to", { x: 1, y: 2 });
    expect(res.isError).toBe(true);
    expect(res.content[0].text).toMatch(/^user_has_control: the user is driving/);
    expect(res.content[0].text).toContain("Insert");
  });

  it("wait_for_task polls until succeeded", async () => {
    let polls = 0;
    const conn = new FakeConn((method) => {
      if (method !== "get_task") return {};
      polls++;
      if (polls < 3) {
        return { current: { name: "move_to", status: "running", progress: "5 tiles remaining" }, lastFinished: null };
      }
      return { current: null, lastFinished: { name: "move_to", status: "succeeded", reason: null } };
    });
    const client = await setup(conn);
    const res = await call(client, "wait_for_task", { timeout_s: 5 });
    expect(res.isError).toBeFalsy();
    expect(polls).toBe(3);
    const body = JSON.parse(res.content[0].text);
    expect(body.timed_out).toBe(false);
    expect(body.report).toMatchObject({ name: "move_to", status: "succeeded" });
  });

  it("wait_for_task returns running report on timeout", async () => {
    const conn = new FakeConn(() => ({
      current: { name: "explore", status: "running", progress: "40 tiles remaining" },
      lastFinished: null,
    }));
    const client = await setup(conn);
    const started = Date.now();
    const res = await call(client, "wait_for_task", { timeout_s: 0.5 });
    expect(Date.now() - started).toBeLessThan(2000);
    expect(res.isError).toBeFalsy();
    const body = JSON.parse(res.content[0].text);
    expect(body.timed_out).toBe(true);
    expect(body.report).toMatchObject({ name: "explore", status: "running" });
  });

  it("attack forwards target all with radius and retreat_below", async () => {
    const conn = new FakeConn(() => ({ started: true, task: "attack", all: true }));
    const client = await setup(conn);
    const res = await call(client, "attack", { target: "all", radius: 20, retreat_below: 0.25 });
    expect(res.isError).toBeFalsy();
    expect(conn.calls).toEqual([{ method: "attack", params: { target: "all", radius: 20, retreat_below: 0.25 } }]);
  });

  it("attack accepts an entity id and nearest, rejects other strings and bad retreat_below", async () => {
    const conn = new FakeConn(() => ({ started: true }));
    const client = await setup(conn);
    expect((await call(client, "attack", { target: 7 })).isError).toBeFalsy();
    expect((await call(client, "attack", { target: "nearest" })).isError).toBeFalsy();
    expect((await call(client, "attack", { target: "boss" })).isError).toBe(true);
    expect((await call(client, "attack", { target: "all", retreat_below: 1 })).isError).toBe(true);
    expect(conn.calls.map((c) => c.params)).toEqual([{ target: 7 }, { target: "nearest" }]);
  });

  it("attack description explains evasion, all and the result", async () => {
    const client = await setup(new FakeConn());
    const tool = (await client.listTools()).tools.find((t) => t.name === "attack")!;
    expect(tool.description).toContain('"all"');
    expect(tool.description).toContain("low life");
    expect(tool.description).toContain("damageTaken");
    expect(tool.inputSchema.properties).toHaveProperty("retreat_below");
  });

  it("wait_for_task passes attack's result through", async () => {
    const result = { killed: 3, kills: ["Zombie", "Zombie", "Demon Eye"], damageTaken: 12, durationS: 9.5, dodges: 4, retreated: false };
    const conn = new FakeConn(() => ({ current: null, lastFinished: { name: "attack", status: 1, reason: "no hostiles left", result } }));
    const client = await setup(conn);
    const body = JSON.parse((await call(client, "wait_for_task", { timeout_s: 1 })).content[0].text);
    expect(body.report).toMatchObject({ name: "attack", status: "succeeded", result });
  });

  it("events are appended to results", async () => {
    const conn = new FakeConn(() => ({ x: 5, y: 6 }));
    conn.events.push({ event: "took_damage", amount: 7 });
    const client = await setup(conn);
    const res = await call(client, "get_state");
    const body = JSON.parse(res.content[0].text);
    expect(body).toEqual({ x: 5, y: 6, events: [{ event: "took_damage", amount: 7 }] });

    const again = JSON.parse((await call(client, "get_state")).content[0].text);
    expect(again).toEqual({ x: 5, y: 6 });
  });

  it("build_platforms forwards row parameters", async () => {
    const conn = new FakeConn(() => ({ started: true, task: "build_platforms" }));
    const client = await setup(conn);
    const res = await call(client, "build_platforms", { x1: 100, x2: 140, y: 250, rows: 3, spacing: 8 });
    expect(res.isError).toBeFalsy();
    expect(conn.calls).toEqual([
      { method: "build_platforms", params: { x1: 100, x2: 140, y: 250, rows: 3, spacing: 8 } },
    ]);
  });

  it("build_platforms rejects rows out of range before calling the mod", async () => {
    const conn = new FakeConn();
    const client = await setup(conn);
    const res = await call(client, "build_platforms", { x1: 1, x2: 2, y: 3, rows: 0 });
    expect(res.isError).toBe(true);
    expect(conn.calls).toEqual([]);
  });

  it("unequip needs exactly one of item or slot", async () => {
    const conn = new FakeConn(() => ({ unequipped: "Hermes Boots" }));
    const client = await setup(conn);
    const both = await call(client, "unequip", { item: "Hermes Boots", slot: 3 });
    expect(both.isError).toBe(true);
    expect(both.content[0].text).toMatch(/^bad_params: give exactly one of item or slot/);
    const neither = await call(client, "unequip", {});
    expect(neither.isError).toBe(true);
    expect(conn.calls).toEqual([]);
    const ok = await call(client, "unequip", { slot: 3 });
    expect(ok.isError).toBeFalsy();
    expect(conn.calls).toEqual([{ method: "unequip", params: { slot: 3 } }]);
  });

  it("equip forwards an optional accessory slot and validates its range", async () => {
    const conn = new FakeConn(() => ({ equipped: "Cloud in a Bottle", armorSlot: 4 }));
    const client = await setup(conn);
    const res = await call(client, "equip", { item: "Cloud in a Bottle", slot: 4 });
    expect(res.isError).toBeFalsy();
    expect(conn.calls).toEqual([{ method: "equip", params: { item: "Cloud in a Bottle", slot: 4 } }]);
    const bad = await call(client, "equip", { item: "Cloud in a Bottle", slot: 1 });
    expect(bad.isError).toBe(true);
    expect(conn.calls).toHaveLength(1);
  });

  it("craft forwards with_intermediates", async () => {
    const conn = new FakeConn(() => ({ item: "Lesser Healing Potion", crafted: 1, intermediates: [] }));
    const client = await setup(conn);
    await call(client, "craft", { item: "Lesser Healing Potion", count: 2, with_intermediates: true });
    expect(conn.calls).toEqual([
      { method: "craft", params: { item: "Lesser Healing Potion", count: 2, with_intermediates: true } },
    ]);
  });

  it("buy forwards npc, item and count; item is required", async () => {
    const conn = new FakeConn(() => ({ started: true, task: "buy" }));
    const client = await setup(conn);
    const res = await call(client, "buy", { entity_id: 7, item: "Lesser Healing Potion", count: 5 });
    expect(res.isError).toBeFalsy();
    expect(conn.calls).toEqual([
      { method: "buy", params: { entity_id: 7, item: "Lesser Healing Potion", count: 5 } },
    ]);
    const missing = await call(client, "buy", { entity_id: 7 });
    expect(missing.isError).toBe(true);
    expect(conn.calls).toHaveLength(1);
  });
});
