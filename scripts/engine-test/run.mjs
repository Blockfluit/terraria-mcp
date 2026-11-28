// Engine test runner: plays scenario files against real tModLoader TEST instances (never instance 0: every scenario
// starts with dev_setup, which the mod refuses outside a test instance). See docs/FAST-DEV.md.
// Usage: node scripts/engine-test/run.mjs --ports 7879[,7880...] [--filter substr] [--json] [files...]
//   files default to scripts/engine-test/scenarios/*.json; with several ports, scenarios run in parallel (one per port).
// Host: TERRARIA_HOST, else the WSL default gateway (as call.mjs). Exit code 0 = all passed, 1 = a failure, 2 = usage.
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { translate, anchorFor, check, validate } from "./lib.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const opt = (name) => { const i = argv.indexOf(name); if (i < 0) return undefined; const v = argv[i + 1]; argv.splice(i, 2); return v; };
const flag = (name) => { const i = argv.indexOf(name); if (i < 0) return false; argv.splice(i, 1); return true; };
const ports = (opt("--ports") ?? process.env.TERRARIA_PORT ?? "").split(",").filter(Boolean).map(Number);
const filter = opt("--filter");
const asJson = flag("--json");
if (!ports.length || ports.some((p) => !(p > 1024 && p < 65536))) { console.error("usage: run.mjs --ports 7879[,7880] [--filter s] [files...]"); process.exit(2); }
if (ports.includes(7878)) { console.error("refusing port 7878: that is the user's main game (instance 0)"); process.exit(2); }

function host() {
  if (process.env.TERRARIA_HOST?.trim()) return process.env.TERRARIA_HOST.trim();
  const route = fs.readFileSync("/proc/net/route", "utf8").split("\n").slice(1).map((l) => l.trim().split(/\s+/)).find((c) => c[1] === "00000000");
  return [6, 4, 2, 0].map((i) => parseInt(route[2].slice(i, i + 2), 16)).join(".");
}

class Bridge {
  constructor(port) { this.port = port; this.id = 0; this.pending = new Map(); this.buf = ""; }
  connect() {
    return new Promise((resolve, reject) => {
      const s = (this.sock = net.createConnection({ host: host(), port: this.port }));
      const t = setTimeout(() => { s.destroy(); reject(new Error(`port ${this.port}: not connected`)); }, 5000);
      s.setEncoding("utf8");
      s.on("connect", () => { clearTimeout(t); resolve(); });
      s.on("error", (e) => { clearTimeout(t); reject(e); });
      s.on("close", () => { for (const r of this.pending.values()) r({ error: { code: "closed", message: "connection closed by the game" } }); this.pending.clear(); });
      s.on("data", (d) => {
        this.buf += d;
        let nl;
        while ((nl = this.buf.indexOf("\n")) >= 0) {
          const line = this.buf.slice(0, nl); this.buf = this.buf.slice(nl + 1);
          if (!line.trim()) continue;
          const m = JSON.parse(line);
          if (m.id !== undefined) { this.pending.get(m.id)?.(m); this.pending.delete(m.id); }
        }
      });
    });
  }
  call(method, params = {}, timeoutMs = 15000) {
    return new Promise((resolve) => {
      const id = ++this.id;
      const t = setTimeout(() => { this.pending.delete(id); resolve({ error: { code: "timeout", message: `${method}: no reply in ${timeoutMs} ms` } }); }, timeoutMs);
      this.pending.set(id, (m) => { clearTimeout(t); resolve(m); });
      this.sock.write(JSON.stringify({ id, method, params }) + "\n");
    });
  }
  async ok(method, params) {
    const r = await this.call(method, params);
    if (r.error) throw new Error(`${method}: ${r.error.code}: ${r.error.message}`);
    return r.result;
  }
  close() { this.sock?.destroy(); }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Wait `gameSeconds` of GAME time (60 updates each), polling; also stops early when `until()` is true. Wall cap. */
async function waitGame(b, gameSeconds, until) {
  const start = (await b.ok("dev_status")).fastDev.gameUpdateCount;
  const wallEnd = Date.now() + gameSeconds * 1000 * 2 + 30000; // never slower than half speed + 30 s
  for (;;) {
    if (until && (await until())) return "done";
    const now = (await b.ok("dev_status")).fastDev.gameUpdateCount;
    if (now - start >= gameSeconds * 60) return "elapsed";
    if (Date.now() > wallEnd) return "wall_timeout";
    await sleep(100);
  }
}

async function runScenario(b, sc) {
  const t0 = Date.now();
  const status = await b.ok("dev_status");
  if (!status.fastDev?.testInstance) throw new Error(`port ${b.port} is not a test instance: ${status.fastDev?.testInstanceReason}`);
  if (!status.world) throw new Error(`port ${b.port}: not in a world`);
  const a = anchorFor(status.world, sc.anchor);
  const T = (v) => translate(v, a.x, a.y);
  const ctl = await b.ok("request_control");
  if (ctl.driver !== "ai") throw new Error(`request_control refused: ${JSON.stringify(ctl)}`);
  await b.call("cancel_task");
  const setup = await b.ok("dev_setup", T({ time: "noon", clear_hostiles: true, clear_items: true, clear_buffs: true, ...sc.setup }));
  if (setup.tilesFailed?.length) throw new Error(`dev_setup could not place: ${setup.tilesFailed.join("; ")}`);
  const deathsBefore = JSON.stringify((await b.ok("get_state")).lastDeaths ?? []);
  await waitGame(b, sc.settle_s ?? 1); // liquids settle, lighting catches up, the player lands

  let report = null;
  const startUpdate = (await b.ok("dev_status")).fastDev.gameUpdateCount;
  for (const [i, raw] of sc.steps.entries()) {
    const step = T(raw);
    const r = await b.call(step.method, step.params ?? {});
    if (r.error) {
      if (step.expect_error && r.error.code === step.expect_error) continue;
      return { failures: [`step ${i} ${step.method}: ${r.error.code}: ${r.error.message}`], wallS: (Date.now() - t0) / 1000 };
    }
    if (step.expect_error) return { failures: [`step ${i} ${step.method}: expected error ${step.expect_error}`] };
    if (step.wait === false) continue;
    const done = async () => { const t = await b.ok("get_task"); return !t.current || t.current.status !== "running"; };
    if (step.run_s) {
      // Run for a fixed game time (e.g. survive a boss for 30 s), then stop the task.
      const how = await waitGame(b, step.run_s, step.stop_when_done ? done : undefined);
      if (how === "wall_timeout") return { failures: [`step ${i} ${step.method}: wall-clock timeout`] };
      const t = await b.ok("get_task");
      report = t.current ?? t.lastFinished;
      await b.call("cancel_task");
    } else {
      const how = await waitGame(b, step.timeout_s ?? 120, done);
      const t = await b.ok("get_task");
      report = t.current ?? t.lastFinished;
      if (how !== "done") {
        await b.call("cancel_task");
        return { failures: [`step ${i} ${step.method}: still running after ${step.timeout_s ?? 120} game s (${report?.progress ?? ""})`], report };
      }
    }
  }
  const state = await b.ok("get_state");
  const inventory = await b.ok("get_inventory");
  const before = new Set(JSON.parse(deathsBefore).map((d) => JSON.stringify(d)));
  const deathsAfter = (state.lastDeaths ?? []).filter((d) => !before.has(JSON.stringify(d))).length;
  const failures = check(T(sc.expect), { report, state, inventory, deathsBefore: 0, deathsAfter });
  const gameS = ((await b.ok("dev_status")).fastDev.gameUpdateCount - startUpdate) / 60;
  await b.call("cancel_task");
  await b.call("dev_setup", { clear_hostiles: true });
  return { failures, report, wallS: (Date.now() - t0) / 1000, gameS, at: { x: state.x, y: state.y } };
}

const files = argv.length ? argv : fs.readdirSync(path.join(HERE, "scenarios")).filter((f) => f.endsWith(".json")).sort().map((f) => path.join(HERE, "scenarios", f));
const scenarios = files.map((f) => ({ file: f, ...JSON.parse(fs.readFileSync(f, "utf8")) })).filter((s) => !filter || s.name.includes(filter) || s.file.includes(filter));
for (const s of scenarios) { const p = validate(s); if (p.length) { console.error(`${s.file}: ${p.join("; ")}`); process.exit(2); } }

const queue = [...scenarios];
const results = [];
// Hard deadline for the whole run: nothing may hang a session.
const totalCap = scenarios.reduce((n, s) => n + (s.steps.reduce((m, st) => m + (st.run_s ?? st.timeout_s ?? 120), 0) * 2 + 60), 0) * 1000;
setTimeout(() => { console.log("ERROR engine-test: overall deadline passed"); process.exit(3); }, totalCap).unref();

await Promise.all(ports.map(async (port) => {
  const b = new Bridge(port);
  try { await b.connect(); } catch (e) { console.log(`port ${port}: ${e.message ?? e} (its scenarios go to the other ports)`); return; }
  try {
    while (queue.length) {
      const sc = queue.shift();
      let r;
      try { r = await runScenario(b, sc); } catch (e) { r = { failures: [String(e.message ?? e)] }; }
      results.push({ name: sc.name, port, ...r });
      if (!asJson) console.log(`${r.failures.length ? "FAIL" : "PASS"} ${sc.name} (port ${port}, ${r.wallS?.toFixed(1) ?? "?"} s wall, ${r.gameS?.toFixed(1) ?? "?"} s game)${r.failures.length ? "\n  - " + r.failures.join("\n  - ") : ""}`);
    }
  } finally { b.close(); }
}));

for (const s of queue) results.push({ name: s.name, failures: ["not run: no instance reachable"] });
const failed = results.filter((r) => r.failures.length);
if (asJson) console.log(JSON.stringify({ passed: results.length - failed.length, failed: failed.length, results }, null, 2));
else console.log(`engine-test: ${results.length - failed.length} passed, ${failed.length} failed`);
process.exit(failed.length ? 1 : 0);
