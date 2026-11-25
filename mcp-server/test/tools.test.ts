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
      ].sort(),
    );
  });

  it("move_to forwards x,y,allow_dig", async () => {
    const conn = new FakeConn(() => ({ started: "move_to" }));
    const client = await setup(conn);
    const res = await call(client, "move_to", { x: 100, y: 250, allow_dig: false });
    expect(res.isError).toBeFalsy();
    expect(conn.calls).toEqual([{ method: "move_to", params: { x: 100, y: 250, allow_dig: false } }]);
    expect(JSON.parse(res.content[0].text)).toEqual({ started: "move_to" });
  });

  it("user_has_control becomes isError with F8 hint text", async () => {
    const conn = new FakeConn(() => {
      throw new ModError("user_has_control", "the user is driving");
    });
    const client = await setup(conn);
    const res = await call(client, "move_to", { x: 1, y: 2 });
    expect(res.isError).toBe(true);
    expect(res.content[0].text).toMatch(/^user_has_control: the user is driving/);
    expect(res.content[0].text).toContain("F8");
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
});
