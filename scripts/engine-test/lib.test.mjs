// node --test scripts/engine-test/   (no game needed)
import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { translate, anchorFor, countItem, compare, check, validate } from "./lib.mjs";

test("translate shifts x/y keys deeply and nothing else", () => {
  const v = { x: 1, y: -1, count: 5, place_tiles: [{ x1: 0, x2: 3, y1: 0, y2: 2, tile: "Stone" }], arena: { x1: 2, x2: 9 }, s: "x" };
  assert.deepEqual(translate(v, 100, 200), {
    x: 101, y: 199, count: 5, place_tiles: [{ x1: 100, x2: 103, y1: 200, y2: 202, tile: "Stone" }], arena: { x1: 102, x2: 109 }, s: "x",
  });
  assert.equal(v.x, 1); // copy
});

test("anchorFor", () => {
  assert.deepEqual(anchorFor({ spawnX: 4200, worldSurface: 600 }), { x: 4500, y: 270 });
  assert.deepEqual(anchorFor({ spawnX: 4200, worldSurface: 600 }, { dx: -500, y: 250 }), { x: 3700, y: 250 });
});

test("countItem and compare", () => {
  const inv = { hotbar: [{ slot: 0, name: "Wooden Bow", stack: 1 }], inventory: [{ slot: 12, name: "Life Crystal", stack: 1 }, { slot: 13, name: "life crystal", stack: 2 }], selected: 0 };
  assert.equal(countItem(inv, "Life Crystal"), 3);
  assert.equal(countItem(inv, "Torch"), 0);
  assert.ok(compare(">=1", 3) && compare("<=3", 3) && compare(3, 3) && compare("==0", 0) && !compare(">3", 3) && compare("<4", 3));
  assert.throws(() => compare("lots", 1));
});

test("check passes and reports each failure", () => {
  const seen = { report: { name: "move_to", status: "succeeded" }, state: { x: 10, y: 5, life: 80, buffs: ["Water Walking"] }, inventory: {}, deathsBefore: 0, deathsAfter: 0 };
  assert.deepEqual(check({ task: "succeeded", near: { x: 11, y: 5 }, alive: true, buff: "water walking", life_min: 50 }, seen), []);
  const bad = check({ task: "failed", near: { x: 20, y: 5, tol: 2 }, inventory: { Gel: ">=1" }, buff: "Ironskin" },
    { ...seen, deathsAfter: 1 });
  assert.equal(bad.length, 4);
  assert.equal(check({ alive: true }, { ...seen, deathsAfter: 1 }).length, 1);
});

test("every scenario file is valid", () => {
  const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), "scenarios");
  const files = fs.readdirSync(dir).filter((f) => f.endsWith(".json"));
  assert.ok(files.length >= 5);
  for (const f of files) assert.deepEqual(validate(JSON.parse(fs.readFileSync(path.join(dir, f), "utf8"))), [], f);
});
