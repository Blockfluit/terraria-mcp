// Pure helpers of the engine test runner (run.mjs): coordinate translation, expectation checks. Tested by lib.test.mjs.

const X_KEYS = new Set(["x", "x1", "x2"]);
const Y_KEYS = new Set(["y", "y1", "y2"]);

/** Deep copy of `v` with every numeric x/x1/x2 shifted by ax and y/y1/y2 by ay (scenario coords are anchor-relative). */
export function translate(v, ax, ay) {
  if (Array.isArray(v)) return v.map((e) => translate(e, ax, ay));
  if (v && typeof v === "object") {
    const out = {};
    for (const [k, e] of Object.entries(v)) {
      if (typeof e === "number" && X_KEYS.has(k)) out[k] = e + ax;
      else if (typeof e === "number" && Y_KEYS.has(k)) out[k] = e + ay;
      else out[k] = translate(e, ax, ay);
    }
    return out;
  }
  return v;
}

/** The anchor (absolute tile of scenario (0,0)): above the spawn's sky, clear of terrain, below the space layer. */
export function anchorFor(world, anchor = {}) {
  const dx = anchor.dx ?? 300;
  const y = anchor.y ?? Math.round(world.worldSurface * 0.45);
  return { x: world.spawnX + dx, y };
}

/** Total stack of items named `name` (case-insensitive) across every slot list of a get_inventory result. */
export function countItem(inv, name) {
  const want = name.toLowerCase();
  let n = 0;
  for (const v of Object.values(inv ?? {})) {
    if (!Array.isArray(v)) continue;
    for (const s of v) if (s && typeof s.name === "string" && s.name.toLowerCase() === want) n += s.stack ?? 1;
  }
  return n;
}

/** ">=1", "<=0", "==3", ">2", "<5" or a bare number (= equality) against n. */
export function compare(spec, n) {
  if (typeof spec === "number") return n === spec;
  const m = /^\s*(>=|<=|==|>|<)?\s*(-?\d+(?:\.\d+)?)\s*$/.exec(String(spec));
  if (!m) throw new Error(`bad comparison '${spec}'`);
  const v = Number(m[2]);
  switch (m[1] ?? "==") {
    case ">=": return n >= v;
    case "<=": return n <= v;
    case ">": return n > v;
    case "<": return n < v;
    default: return n === v;
  }
}

/**
 * Failures (strings) of `expect` against what the run saw: {report (last waited task's report), state (get_state),
 * inventory (get_inventory), deathsBefore, deathsAfter}. Empty = pass. All coordinates already absolute.
 */
export function check(expect = {}, seen) {
  const f = [];
  const { report, state, inventory } = seen;
  if (expect.task !== undefined) {
    const st = report?.status ?? "none";
    if (st !== expect.task) f.push(`task ${report?.name ?? "?"} ${st}${report?.reason ? ` (${report.reason})` : ""}, expected ${expect.task}`);
  }
  if (expect.reason_contains !== undefined && !(report?.reason ?? "").includes(expect.reason_contains))
    f.push(`reason '${report?.reason ?? ""}' does not contain '${expect.reason_contains}'`);
  if (expect.near) {
    const { x, y, tol = 1 } = expect.near;
    const dx = Math.abs(state.x - x), dy = Math.abs(state.y - y);
    if (dx > tol || dy > tol) f.push(`player at (${state.x},${state.y}), expected within ${tol} of (${x},${y})`);
  }
  if (expect.alive && (state.life <= 0 || seen.deathsAfter > seen.deathsBefore))
    f.push(`the player died (${seen.deathsAfter - seen.deathsBefore} death(s), life ${state.life})`);
  if (expect.life_min !== undefined && state.life < expect.life_min) f.push(`life ${state.life} < ${expect.life_min}`);
  if (expect.buff && !(state.buffs ?? []).some((b) => b.toLowerCase() === expect.buff.toLowerCase()))
    f.push(`no buff '${expect.buff}' (buffs: ${(state.buffs ?? []).join(", ") || "none"})`);
  for (const [name, spec] of Object.entries(expect.inventory ?? {})) {
    const n = countItem(inventory, name);
    if (!compare(spec, n)) f.push(`inventory ${name}: ${n}, expected ${spec}`);
  }
  return f;
}

/** Validate a scenario file's shape; returns a list of problems. */
export function validate(sc) {
  const p = [];
  if (!sc || typeof sc !== "object") return ["not an object"];
  if (typeof sc.name !== "string" || !sc.name) p.push("name missing");
  if (!sc.setup || typeof sc.setup !== "object") p.push("setup missing");
  if (!Array.isArray(sc.steps) || sc.steps.length === 0) p.push("steps missing");
  else
    sc.steps.forEach((s, i) => {
      if (typeof s.method !== "string") p.push(`steps[${i}].method missing`);
      if (s.run_s !== undefined && !(s.run_s > 0)) p.push(`steps[${i}].run_s must be > 0`);
    });
  if (!sc.expect || typeof sc.expect !== "object") p.push("expect missing");
  return p;
}
