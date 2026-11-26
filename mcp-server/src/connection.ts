import net from "node:net";
import { compactEvents } from "./events.js";

export class ModError extends Error {
  constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "ModError";
  }
}

export interface ModEvent {
  event: string;
  [key: string]: unknown;
}

interface Pending {
  resolve(value: unknown): void;
  reject(err: ModError): void;
  timer: NodeJS.Timeout;
}

/** Raw events kept between two drains (compactEvents shrinks them for the LLM). */
const MAX_BUFFERED_EVENTS = 1000;

/**
 * Newline-delimited JSON client for the LlmPlayer mod. Connects lazily on the
 * first call and again on the next call after any drop.
 */
export class ModConnection {
  private socket: net.Socket | null = null;
  private connecting: Promise<net.Socket> | null = null;
  private pending = new Map<number, Pending>();
  private events: ModEvent[] = [];
  private nextId = 1;
  private buffer = "";

  constructor(
    private readonly host: string,
    private readonly port: number,
    private readonly connectTimeoutMs = 3000,
  ) {}

  get address(): string {
    return `${this.host}:${this.port}`;
  }

  async call(method: string, params: object = {}, timeoutMs = 10000): Promise<unknown> {
    const sock = await this.ensureConnected();
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new ModError("timeout", `${method} got no reply from ${this.address} within ${timeoutMs} ms`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      sock.write(JSON.stringify({ id, method, params }) + "\n");
    });
  }

  /** Events since the last drain, compacted for the LLM (see compactEvents). */
  drainEvents(): ModEvent[] {
    return compactEvents(this.events.splice(0));
  }

  close(): void {
    this.socket?.destroy();
    this.dropped("connection closed");
  }

  private notConnected(detail?: string): ModError {
    return new ModError(
      "not_connected",
      `Terraria not reachable at ${this.address} — is tModLoader running with LlmPlayer enabled and a world loaded?` +
        (detail ? ` (${detail})` : ""),
    );
  }

  private ensureConnected(): Promise<net.Socket> {
    if (this.socket) return Promise.resolve(this.socket);
    if (this.connecting) return this.connecting;
    this.connecting = new Promise<net.Socket>((resolve, reject) => {
      const sock = net.createConnection({ host: this.host, port: this.port });
      sock.setEncoding("utf8");
      sock.setNoDelay(true);
      const timer = setTimeout(() => {
        sock.destroy();
        fail("connect timed out");
      }, this.connectTimeoutMs);
      let settled = false;
      const fail = (detail: string) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        this.connecting = null;
        reject(this.notConnected(detail));
      };
      sock.once("error", (e: NodeJS.ErrnoException) => fail(e.code ?? e.message));
      sock.once("connect", () => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        sock.removeAllListeners("error");
        this.attach(sock);
        this.connecting = null;
        resolve(sock);
      });
    });
    return this.connecting;
  }

  private attach(sock: net.Socket): void {
    this.socket = sock;
    this.buffer = "";
    sock.on("data", (chunk: string) => this.onData(chunk));
    sock.on("error", (e: NodeJS.ErrnoException) => {
      process.stderr.write(`[terraria-mcp] socket error: ${e.code ?? e.message}\n`);
    });
    sock.on("close", () => {
      if (this.socket === sock) this.dropped("connection lost");
    });
  }

  private dropped(detail: string): void {
    this.socket = null;
    this.buffer = "";
    for (const [id, p] of this.pending) {
      clearTimeout(p.timer);
      p.reject(this.notConnected(detail));
      this.pending.delete(id);
    }
  }

  private onData(chunk: string): void {
    this.buffer += chunk;
    let nl: number;
    while ((nl = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, nl).trim();
      this.buffer = this.buffer.slice(nl + 1);
      if (line) this.onLine(line);
    }
  }

  private onLine(line: string): void {
    let msg: Record<string, unknown>;
    try {
      msg = JSON.parse(line);
    } catch {
      process.stderr.write(`[terraria-mcp] ignoring malformed line from mod: ${line.slice(0, 200)}\n`);
      return;
    }
    if (typeof msg.event === "string") {
      this.events.push(msg as ModEvent);
      if (this.events.length > MAX_BUFFERED_EVENTS) this.events.splice(0, this.events.length - MAX_BUFFERED_EVENTS);
      return;
    }
    if (typeof msg.id !== "number") return;
    const p = this.pending.get(msg.id);
    if (!p) return;
    this.pending.delete(msg.id);
    clearTimeout(p.timer);
    const error = msg.error as { code?: string; message?: string } | undefined;
    if (error) p.reject(new ModError(error.code ?? "internal", error.message ?? "unknown error"));
    else p.resolve(msg.result ?? null);
  }
}
