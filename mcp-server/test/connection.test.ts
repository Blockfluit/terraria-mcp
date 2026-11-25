import net from "node:net";
import { afterEach, describe, expect, it } from "vitest";
import { ModConnection, ModError } from "../src/connection.js";

type Handler = (req: { id: number; method: string; params: unknown }, sock: net.Socket) => void;

interface Fake {
  server: net.Server;
  port: number;
  lines: string[];
  sockets: Set<net.Socket>;
  close(): Promise<void>;
}

function startFake(handler: Handler, port = 0): Promise<Fake> {
  const lines: string[] = [];
  const sockets = new Set<net.Socket>();
  const server = net.createServer((sock) => {
    sockets.add(sock);
    sock.on("close", () => sockets.delete(sock));
    let buf = "";
    sock.on("data", (d) => {
      buf += d.toString("utf8");
      let nl: number;
      while ((nl = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, nl);
        buf = buf.slice(nl + 1);
        lines.push(line);
        handler(JSON.parse(line), sock);
      }
    });
  });
  return new Promise((resolve) => {
    server.listen(port, "127.0.0.1", () => {
      const addr = server.address() as net.AddressInfo;
      resolve({
        server,
        port: addr.port,
        lines,
        sockets,
        close: () =>
          new Promise<void>((r) => {
            for (const s of sockets) s.destroy();
            server.close(() => r());
          }),
      });
    });
  });
}

const send = (sock: net.Socket, msg: unknown) => sock.write(JSON.stringify(msg) + "\n");

const fakes: Fake[] = [];
const conns: ModConnection[] = [];
afterEach(async () => {
  for (const c of conns.splice(0)) c.close();
  for (const f of fakes.splice(0)) await f.close();
});

async function fake(handler: Handler, port?: number) {
  const f = await startFake(handler, port);
  fakes.push(f);
  return f;
}
function conn(port: number) {
  const c = new ModConnection("127.0.0.1", port);
  conns.push(c);
  return c;
}

describe("ModConnection", () => {
  it("sends request line and resolves result", async () => {
    const f = await fake((req, sock) => send(sock, { id: req.id, result: { x: 10, y: 20 } }));
    const c = conn(f.port);
    await expect(c.call("get_state", { a: 1 })).resolves.toEqual({ x: 10, y: 20 });
    expect(f.lines).toHaveLength(1);
    const req = JSON.parse(f.lines[0]);
    expect(req).toMatchObject({ method: "get_state", params: { a: 1 } });
    expect(typeof req.id).toBe("number");
  });

  it("rejects ModError on error response", async () => {
    const f = await fake((req, sock) =>
      send(sock, { id: req.id, error: { code: "user_has_control", message: "user is driving" } }),
    );
    const c = conn(f.port);
    const err = await c.call("move_to", { x: 1, y: 2 }).catch((e) => e);
    expect(err).toBeInstanceOf(ModError);
    expect(err.code).toBe("user_has_control");
    expect(err.message).toBe("user is driving");
  });

  it("buffers events and drainEvents returns them once", async () => {
    const f = await fake((req, sock) => {
      send(sock, { event: "took_damage", amount: 12 });
      send(sock, { event: "control_changed", driver: "user", reason: "user_input" });
      send(sock, { id: req.id, result: {} });
    });
    const c = conn(f.port);
    await c.call("get_state");
    expect(c.drainEvents()).toEqual([
      { event: "took_damage", amount: 12 },
      { event: "control_changed", driver: "user", reason: "user_input" },
    ]);
    expect(c.drainEvents()).toEqual([]);
  });

  it("rejects with readable error when nothing listens", async () => {
    const f = await fake(() => {});
    const port = f.port;
    await f.close();
    fakes.length = 0;
    const c = conn(port);
    const err = await c.call("get_state").catch((e) => e);
    expect(err).toBeInstanceOf(ModError);
    expect(err.code).toBe("not_connected");
    expect(err.message).toContain(`127.0.0.1:${port}`);
  });

  it("reconnects after server restart", async () => {
    const handler: Handler = (req, sock) => send(sock, { id: req.id, result: { ok: true } });
    const f1 = await fake(handler);
    const port = f1.port;
    const c = conn(port);
    await expect(c.call("get_state")).resolves.toEqual({ ok: true });

    await f1.close();
    fakes.length = 0;
    // Give the client a moment to notice the drop.
    await new Promise((r) => setTimeout(r, 50));

    await fake(handler, port);
    await expect(c.call("get_state")).resolves.toEqual({ ok: true });
  });

  it("times out with code timeout", async () => {
    const f = await fake(() => {});
    const c = conn(f.port);
    const err = await c.call("get_state", {}, 100).catch((e) => e);
    expect(err).toBeInstanceOf(ModError);
    expect(err.code).toBe("timeout");
  });
});
