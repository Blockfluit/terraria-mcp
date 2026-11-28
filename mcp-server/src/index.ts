#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { resolveHost } from "./host.js";
import { ModConnection } from "./connection.js";
import { registerTools, SERVER_INFO, SERVER_INSTRUCTIONS } from "./tools.js";
import { registerPrompts } from "./prompts.js";

// stdout carries the MCP protocol only; all logging goes to stderr.
const log = (msg: string) => process.stderr.write(`[terraria-mcp] ${msg}\n`);

function readRouteTable(): string {
  try {
    return readFileSync("/proc/net/route", "utf8");
  } catch {
    return "";
  }
}

async function main(): Promise<void> {
  const host = resolveHost(process.env, readRouteTable());
  const port = Number(process.env.TERRARIA_PORT) || 7878;
  const conn = new ModConnection(host, port);

  const server = new McpServer({ ...SERVER_INFO }, { instructions: SERVER_INSTRUCTIONS });
  registerTools(server, conn);
  registerPrompts(server);

  await server.connect(new StdioServerTransport());
  log(`ready; mod expected at ${host}:${port}`);

  const shutdown = () => {
    conn.close();
    process.exit(0);
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
  process.stdin.on("close", shutdown);
}

main().catch((err) => {
  log(`fatal: ${err instanceof Error ? (err.stack ?? err.message) : String(err)}`);
  process.exit(1);
});
