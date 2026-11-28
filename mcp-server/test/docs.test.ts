import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { AREAS, generate, listTools, renderIndex } from "../scripts/tool-reference.js";

describe("generated MCP docs", () => {
  it("docs/MCP-tools.md and the tool index in docs/MCP.md are up to date (run `npm run docs`)", async () => {
    for (const [path, expected] of Object.entries(await generate())) {
      expect(readFileSync(path, "utf8"), `${path} is stale: run npm run docs in mcp-server`).toBe(expected);
    }
  });

  it("every tool has an area and a summary in scripts/tool-reference.ts", async () => {
    const tools = await listTools();
    expect(() => renderIndex(tools)).not.toThrow();
    expect(AREAS.flatMap((a) => Object.keys(a.tools)).length).toBe(tools.length);
  });

  it("an unclassified tool fails generation", async () => {
    const tools = await listTools();
    expect(() => renderIndex([...tools, { name: "new_tool", inputSchema: {} }])).toThrow(/missing \[new_tool\]/);
  });
});
