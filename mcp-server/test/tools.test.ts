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
        "build_platforms", "unequip", "buy", "mine_vein",
        "open_chest", "chest_deposit", "chest_withdraw", "close_chest", "use_bed",
        "collect", "loot_chest", "explore_caves",
        "get_boss_info", "inspect", "door",
        "build_house", "check_housing",
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

  it("explore forwards allow_dig/torches and describes digging, torches and the result", async () => {
    const conn = new FakeConn(() => ({ started: true, task: "explore" }));
    const client = await setup(conn);
    const res = await call(client, "explore", { direction: "down", distance: 25, allow_dig: true, torches: false });
    expect(res.isError).toBeFalsy();
    expect(conn.calls).toEqual([
      { method: "explore", params: { direction: "down", distance: 25, allow_dig: true, torches: false } },
    ]);
    const tool = (await client.listTools()).tools.find((t) => t.name === "explore");
    for (const word of ["shaft", "tunnel", "torch", "lava", "pick power", "too dark: no torches", "torchesPlaced", "stoppedBecause"])
      expect(tool?.description).toContain(word);
    const props = (tool?.inputSchema as { properties: Record<string, unknown> }).properties;
    expect(Object.keys(props).sort()).toEqual(["allow_dig", "direction", "distance", "torches"]);
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
    expect((await call(client, "attack", { target: "king" })).isError).toBe(true);
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

  it("attack forwards boss-fight params: target boss, arena, heal_below, kill_minions", async () => {
    const conn = new FakeConn(() => ({ started: true, task: "attack", boss: true }));
    const client = await setup(conn);
    const args = { target: "boss", arena: { x1: 100, x2: 180 }, heal_below: 0.5, kill_minions: false, retreat_below: 0 };
    const res = await call(client, "attack", args);
    expect(res.isError).toBeFalsy();
    expect(conn.calls).toEqual([{ method: "attack", params: args }]);
  });

  it("attack rejects a bad arena or heal_below", async () => {
    const conn = new FakeConn(() => ({ started: true }));
    const client = await setup(conn);
    expect((await call(client, "attack", { target: "boss", arena: { x1: 100 } })).isError).toBe(true);
    expect((await call(client, "attack", { target: "boss", arena: { x1: 1.5, x2: 9 } })).isError).toBe(true);
    expect((await call(client, "attack", { target: "boss", heal_below: 1 })).isError).toBe(true);
    expect((await call(client, "attack", { target: "boss", heal_below: -0.1 })).isError).toBe(true);
    expect(conn.calls).toEqual([]);
  });

  it("attack description explains boss fights: arena, kiting, heal_below, boss fled", async () => {
    const client = await setup(new FakeConn());
    const tool = (await client.listTools()).tools.find((t) => t.name === "attack")!;
    for (const word of ["BOSS FIGHTS", "build_platforms", "heal_below", "boss fled", "bossLifeLeftPct", "kill_minions", "potionsUsed"])
      expect(tool.description).toContain(word);
    for (const prop of ["arena", "heal_below", "kill_minions"]) expect(tool.inputSchema.properties).toHaveProperty(prop);
  });

  it("get_boss_info is read-only and forwards the boss name", async () => {
    const info = { bosses: [{ key: "eye_of_cthulhu", name: "Eye of Cthulhu", defeated: false }], isNight: true };
    const conn = new FakeConn(() => info);
    const client = await setup(conn);
    const tool = (await client.listTools()).tools.find((t) => t.name === "get_boss_info")!;
    expect(tool.annotations?.readOnlyHint).toBe(true);
    expect(tool.description).toContain("Eye of Cthulhu");
    const body = JSON.parse((await call(client, "get_boss_info", { boss: "eoc" })).content[0].text);
    expect(conn.calls).toEqual([{ method: "get_boss_info", params: { boss: "eoc" } }]);
    expect(body).toEqual(info);
  });

  it("wait_for_task passes a boss attack's result through", async () => {
    const result = { killed: 5, kills: ["Servant of Cthulhu"], damageTaken: 180, durationS: 140.2, dodges: 31, retreated: false,
      potionsUsed: 3, boss: "Eye of Cthulhu", bossKilled: false, bossLifeLeftPct: 34, ammoLeft: 120 };
    const conn = new FakeConn(() => ({ current: null, lastFinished: { name: "attack", status: 2, reason: "boss fled", result } }));
    const client = await setup(conn);
    const body = JSON.parse((await call(client, "wait_for_task", { timeout_s: 1 })).content[0].text);
    expect(body.report).toMatchObject({ name: "attack", status: "failed", result });
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
  describe("caves", () => {
    it("find accepts the valuable category and describes life crystals, pots and orbs", async () => {
      const conn = new FakeConn(() => [{ name: "Life Crystal", x: 10, y: 300, distance: 12 }]);
      const client = await setup(conn);
      expect((await call(client, "find", { category: "valuable" })).isError).toBeFalsy();
      expect(conn.calls).toEqual([{ method: "find", params: { category: "valuable" } }]);
      const desc = (await client.listTools()).tools.find((t) => t.name === "find")?.description ?? "";
      for (const s of ["Life Crystal", "Pot", "Locked Gold Chest", "Enchanted Sword", "Demon Altar", "Shadow Orb", "Fallen Star"])
        expect(desc).toContain(s);
    });

    it("collect needs x and y and forwards them", async () => {
      const conn = new FakeConn(() => ({ started: true, task: "collect" }));
      const client = await setup(conn);
      expect((await call(client, "collect", { x: 3, y: 400 })).isError).toBeFalsy();
      expect((await call(client, "collect", { x: 3 })).isError).toBe(true);
      expect(conn.calls).toEqual([{ method: "collect", params: { x: 3, y: 400 } }]);
    });

    it("loot_chest forwards x,y or nothing, rejects a lone coordinate", async () => {
      const conn = new FakeConn(() => ({ started: true, task: "loot_chest" }));
      const client = await setup(conn);
      expect((await call(client, "loot_chest", { x: 1, y: 2 })).isError).toBeFalsy();
      expect((await call(client, "loot_chest")).isError).toBeFalsy();
      const bad = await call(client, "loot_chest", { y: 2 });
      expect(bad.isError).toBe(true);
      expect(bad.content[0].text).toMatch(/^bad_params: give both x and y/);
      expect(conn.calls.map((c) => c.params)).toEqual([{ x: 1, y: 2 }, {}]);
    });

    it("explore_caves forwards stop_on, collect_pots and limits, and rejects unknown kinds", async () => {
      const conn = new FakeConn(() => ({ started: true, task: "explore_caves" }));
      const client = await setup(conn);
      const args = { radius: 60, max_seconds: 90, stop_on: ["life_crystal", "chest"], collect_pots: true };
      expect((await call(client, "explore_caves", args)).isError).toBeFalsy();
      expect((await call(client, "explore_caves", { stop_on: [] })).isError).toBeFalsy();
      expect((await call(client, "explore_caves", { stop_on: ["diamonds"] })).isError).toBe(true);
      expect((await call(client, "explore_caves", { radius: 500 })).isError).toBe(true);
      expect(conn.calls).toEqual([
        { method: "explore_caves", params: args },
        { method: "explore_caves", params: { stop_on: [] } },
      ]);
    });

    it("explore_caves describes frontiers, torches, lava, stop_on and the result", async () => {
      const client = await setup(new FakeConn());
      const tool = (await client.listTools()).tools.find((t) => t.name === "explore_caves");
      for (const s of ["unknown", "torches", "lava", "stop_on", "revealedTiles", "found", "stoppedBecause", "collect_pots",
        "surface layer", "tunnels", "tunnelled", "40 tiles", "descended", "lifeCrystals", "found a Life Crystal at (x,y)"])
        expect(tool?.description).toContain(s);
      expect(Object.keys((tool?.inputSchema as { properties: object }).properties).sort()).toEqual(
        ["collect_pots", "max_seconds", "radius", "stop_on", "torches"],
      );
    });

    it("wait_for_task passes loot and exploration results through", async () => {
      const result = { looted: [{ name: "Band of Regeneration", count: 1 }], left: [] };
      const conn = new FakeConn(() => ({ current: null, lastFinished: { name: "loot_chest", status: "succeeded", result } }));
      const client = await setup(conn);
      const res = JSON.parse((await call(client, "wait_for_task", { timeout_s: 1 })).content[0].text);
      expect(res.report.result).toEqual(result);
    });
  });

  describe("housing", () => {
    it("build_house forwards an explicit spot or nothing, and rejects a lone coordinate", async () => {
      const conn = new FakeConn(() => ({ started: true }));
      const client = await setup(conn);
      await call(client, "build_house", {});
      await call(client, "build_house", { x: 100, y: 250, width: 12, height: 6, door: "right" });
      const bad = await call(client, "build_house", { x: 100 });
      expect(bad.isError).toBe(true);
      expect(bad.content[0].text).toContain("both x and y");
      expect(conn.calls).toEqual([
        { method: "build_house", params: {} },
        { method: "build_house", params: { x: 100, y: 250, width: 12, height: 6, door: "right" } },
      ]);
    });

    it("build_house validates size and door side before calling the mod", async () => {
      const conn = new FakeConn();
      const client = await setup(conn);
      expect((await call(client, "build_house", { height: 9 })).isError).toBe(true);
      expect((await call(client, "build_house", { width: 5 })).isError).toBe(true);
      expect((await call(client, "build_house", { door: "up" })).isError).toBe(true);
      expect(conn.calls).toEqual([]);
    });

    it("build_house passes missing_item through with the counts", async () => {
      const conn = new FakeConn(() => {
        throw new ModError("missing_item", "Missing: Wood x12 (chop trees)");
      });
      const client = await setup(conn);
      const res = await call(client, "build_house", {});
      expect(res.isError).toBe(true);
      expect(res.content[0].text).toContain("missing_item: Missing: Wood x12");
    });

    it("check_housing is read-only, takes x,y or nothing, and returns the verdict unchanged", async () => {
      const verdict = { housing: { valid: true, message: "This housing is suitable for a new resident." }, homeless: [] };
      const conn = new FakeConn(() => verdict);
      const client = await setup(conn);
      const tool = (await client.listTools()).tools.find((t) => t.name === "check_housing")!;
      expect(tool.annotations?.readOnlyHint).toBe(true);
      const res = await call(client, "check_housing", { x: 10, y: 20 });
      expect(JSON.parse(res.content[0].text)).toEqual(verdict);
      await call(client, "check_housing", {});
      expect((await call(client, "check_housing", { y: 3 })).isError).toBe(true);
      expect(conn.calls).toEqual([
        { method: "check_housing", params: { x: 10, y: 20 } },
        { method: "check_housing", params: {} },
      ]);
    });

    it("descriptions cover the bill of materials, the door exit, vanilla's verdict and NPC conditions", async () => {
      const client = await setup(new FakeConn());
      const tools = (await client.listTools()).tools;
      const build = tools.find((t) => t.name === "build_house")!.description!;
      for (const s of ["missing_item", "Work Bench", "door", "valid", "60 tiles"]) expect(build).toContain(s);
      const check = tools.find((t) => t.name === "check_housing")!.description!;
      for (const s of ["Merchant", "Nurse", "Life Crystal", "canMoveIn", "homeless"]) expect(check).toContain(s);
      expect(tools.find((t) => t.name === "get_state")!.description).toContain("housing");
    });
  });
});
