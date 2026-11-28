/**
 * Regenerates docs/MCP-tools.md and the tool index in docs/MCP.md from src/tools.ts.
 *   npm run docs            write the files
 *   npm run docs -- --check exit 1 if they are out of date
 */
import { readFileSync, writeFileSync } from "node:fs";
import { relative } from "node:path";
import { generate } from "./tool-reference.js";

const check = process.argv.includes("--check");
let stale = 0;
for (const [path, content] of Object.entries(await generate())) {
  let current = "";
  try {
    current = readFileSync(path, "utf8");
  } catch {}
  const name = relative(process.cwd(), path);
  if (current === content) continue;
  stale++;
  if (check) console.error(`${name} is out of date: run npm run docs`);
  else {
    writeFileSync(path, content);
    console.log(`wrote ${name}`);
  }
}
if (check && stale) process.exit(1);
if (!stale) console.log("docs up to date");
