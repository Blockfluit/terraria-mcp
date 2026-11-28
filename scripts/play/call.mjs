// Usage: node call.mjs <method> '<json params>' [wait_s]  — calls the mod directly; with wait_s polls get_task until done.
import net from "node:net";
import fs from "node:fs";
const route = fs.readFileSync("/proc/net/route", "utf8").split("\n").slice(1).map(l => l.trim().split(/\s+/)).find(c => c[1] === "00000000");
const host = [6,4,2,0].map(i => parseInt(route[2].slice(i, i+2), 16)).join(".");
const [method, params = "{}", waitS] = process.argv.slice(2);
// Hard deadline: never hang (a call waiting on a dead/frozen game kept scripts stuck). wait_s + 60 s, or 90 s.
const deadline = (Number(waitS) || 30) * 1000 + 60000;
setTimeout(() => { console.log("ERROR timeout: no answer within " + Math.round(deadline / 1000) + " s (game frozen or busy?)"); process.exit(3); }, deadline).unref();
process.stdout.on("error", () => process.exit(0)); // reader closed the pipe (head -1): exit quietly, no EPIPE trace
const s = net.createConnection({ host, port: 7878 });
let buf = "", id = 0; const pending = new Map(); const events = [];
s.setEncoding("utf8");
s.on("close", () => { if (pending.size) { console.log("ERROR connection closed by the game"); process.exit(2); } });
s.on("data", d => { buf += d; let nl; while ((nl = buf.indexOf("\n")) >= 0) { const line = buf.slice(0, nl); buf = buf.slice(nl + 1); if (!line.trim()) continue; const m = JSON.parse(line); if (m.event) events.push(m); else pending.get(m.id)?.(m); } });
const call = (meth, p) => new Promise(r => { const i = ++id; pending.set(i, r); s.write(JSON.stringify({ id: i, method: meth, params: p }) + "\n"); });
await new Promise((r) => { const t = setTimeout(() => { console.log("ERROR not connected (game not running?)"); process.exit(2); }, 4000); s.on("connect", () => { clearTimeout(t); r(); }); s.on("error", () => { console.log("ERROR connection refused (game not running?)"); process.exit(2); }); });
const res = await call(method, JSON.parse(params));
console.log("RESULT", JSON.stringify(res.error ?? res.result));
if (waitS && !res.error) {
  const end = Date.now() + Number(waitS) * 1000;
  for (;;) {
    const t = (await call("get_task", {})).result;
    if (t.current && t.current.progress !== globalThis.lastP) { globalThis.lastP = t.current.progress; const st = (await call("get_state", {})).result; (globalThis.recent ??= []).push(...(st.recentEvents ?? [])); console.log("  progress:", t.current.progress, "@", st.x, st.y); }
    if (!t.current || t.current.status !== "running") { console.log("DONE", JSON.stringify(t.current ?? t.lastFinished)); break; }
    if (Date.now() > end) { console.log("TIMEOUT", JSON.stringify(t.current)); break; }
    await new Promise(r => setTimeout(r, 300));
  }
}
const kinds = {}; for (const e of events) { const k = e.event + (e.kind ? ":" + e.kind : ""); kinds[k] = (kinds[k] ?? 0) + 1; }
console.log("EVENTS", JSON.stringify(kinds));
const rec = (globalThis.recent ?? []).filter(e => /died|took|killed/.test(e)); if (rec.length) console.log("RECENT", JSON.stringify(rec.slice(-12)));
for (const e of events) if (e.event === "control_changed" || e.event === "player_died") console.log("  ", JSON.stringify(e));
s.destroy();
