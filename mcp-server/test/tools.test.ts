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
        "open_chest", "chest_deposit", "chest_withdraw", "close_chest", "use_bed",
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

  describe("chests and beds", () => {
    it("open_chest forwards x,y or nothing, and rejects a lone coordinate", async () => {
      const conn = new FakeConn(() => ({ started: true, task: "open_chest" }));
      const client = await setup(conn);
      expect((await call(client, "open_chest", { x: 10, y: 20 })).isError).toBeFalsy();
      expect((await call(client, "open_chest")).isError).toBeFalsy();
      const bad = await call(client, "open_chest", { x: 10 });
      expect(bad.isError).toBe(true);
      expect(bad.content[0].text).toMatch(/^bad_params: give both x and y/);
      expect(conn.calls).toEqual([
        { method: "open_chest", params: { x: 10, y: 20 } },
        { method: "open_chest", params: {} },
      ]);
    });

    it("chest_deposit needs exactly one mode and count only with item", async () => {
      const conn = new FakeConn(() => ({ moved: [{ name: "Wood", count: 50 }] }));
      const client = await setup(conn);
      expect((await call(client, "chest_deposit", { item: "Wood", count: 50 })).isError).toBeFalsy();
      expect((await call(client, "chest_deposit", { all: true })).isError).toBeFalsy();
      expect((await call(client, "chest_deposit", { quick_stack: true })).isError).toBeFalsy();
      for (const args of [{}, { item: "Wood", all: true }, { all: true, quick_stack: true }, { all: true, count: 3 }]) {
        const res = await call(client, "chest_deposit", args);
        expect(res.isError).toBe(true);
        expect(res.content[0].text).toMatch(/^bad_params:/);
      }
      expect(conn.calls.map((c) => c.params)).toEqual([{ item: "Wood", count: 50 }, { all: true }, { quick_stack: true }]);
    });

    it("chest_withdraw takes item or all, and passes mod errors through", async () => {
      const conn = new FakeConn((_m, p) => {
        if (p.item === "Gel") throw new ModError("bad_params", "inventory_full: no room in the inventory for that item");
        return { moved: [] };
      });
      const client = await setup(conn);
      expect((await call(client, "chest_withdraw", { all: true })).isError).toBeFalsy();
      expect((await call(client, "chest_withdraw", { item: "Wood" })).isError).toBeFalsy();
      expect((await call(client, "chest_withdraw", { item: "Wood", all: true })).isError).toBe(true);
      expect((await call(client, "chest_withdraw", {})).isError).toBe(true);
      const full = await call(client, "chest_withdraw", { item: "Gel" });
      expect(full.isError).toBe(true);
      expect(full.content[0].text).toContain("inventory_full");
    });

    it("close_chest and use_bed forward to the mod", async () => {
      const conn = new FakeConn((m) => (m === "close_chest" ? { closed: true } : { started: true }));
      const client = await setup(conn);
      expect(JSON.parse((await call(client, "close_chest")).content[0].text)).toEqual({ closed: true });
      expect((await call(client, "use_bed", { x: 5, y: 6 })).isError).toBeFalsy();
      expect((await call(client, "use_bed", { y: 6 })).isError).toBe(true);
      expect(conn.calls.map((c) => c.method)).toEqual(["close_chest", "use_bed"]);
    });

    it("descriptions mention doors, openChest and the bed find category", async () => {
      const client = await setup(new FakeConn());
      const tools = (await client.listTools()).tools;
      const desc = (name: string) => tools.find((t) => t.name === name)?.description ?? "";
      expect(desc("get_state")).toContain("openChest");
      expect(desc("get_map")).toContain("'D' closed door");
      expect(JSON.stringify(tools.find((t) => t.name === "find")?.inputSchema)).toContain("bed");
      expect(desc("open_chest")).toContain("locked");
    });
  });
});
