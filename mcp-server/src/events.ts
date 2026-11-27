import type { ModEvent } from "./connection.js";

/** At most this many events reach the LLM per tool result (summaries and must-keep events included). */
export const MAX_EVENTS = 25;
/** Up to this many hits are listed one by one; more are folded into one took_damage summary. */
export const DAMAGE_DETAIL_LIMIT = 3;

/** Events that change what the LLM should do next: never folded or dropped by the cap. */
const MUST_KEEP = new Set(["control_changed", "task_finished", "player_died", "died", "respawned", "weapon_switch"]);

/** True for events that must reach the LLM however many others arrive (the mod emits deaths as `player_died`). */
export const isMustKeep = (e: ModEvent) => MUST_KEEP.has(e.event);

/**
 * Cap a raw event buffer at `max` entries by dropping the oldest droppable events first; must-keep events (deaths,
 * control changes, finished tasks) only go when nothing else is left to drop. Seen in play: a long wait under
 * auto-defend produced hundreds of reflex events and a plain oldest-first trim could push a death out.
 */
export function trimBuffer(events: ModEvent[], max: number): ModEvent[] {
  if (events.length <= max) return events;
  let excess = events.length - max;
  const out: ModEvent[] = [];
  for (const e of events) {
    if (excess > 0 && !isMustKeep(e)) {
      excess--;
      continue;
    }
    out.push(e);
  }
  return out.length > max ? out.slice(out.length - max) : out;
}

const isAutoDefend = (e: ModEvent) =>
  e.event === "reflex" && (e.kind === "auto_defend" || e.kind === "auto_defend_end");

/**
 * Shrink the raw event stream the mod sends between two tool calls into what an LLM needs.
 * Seen in play: one get_state carried ~100 events, nearly all auto-defend start/end pairs from a slime bouncing in
 * and out of reach.
 * - Auto-defend start/end events become one `{event:"reflex", kind:"auto_defend", engagements, targets}` summary
 *   (targets: name -> times engaged), at the position of the first one.
 * - More than DAMAGE_DETAIL_LIMIT took_damage events become one
 *   `{event:"took_damage", hits, total, sources, minLife, life, lifeMax}` summary at the first hit's position.
 * - task_finished events lose their `result` payload (e.g. a whole shop list): wait_for_task / get_task already
 *   return it in the report, so it was sent twice.
 * - Everything else is kept in order. Then, past MAX_EVENTS, the oldest events not in MUST_KEEP are dropped and an
 *   `{event:"omitted", count}` marker leads the list.
 */
export function compactEvents(events: ModEvent[]): ModEvent[] {
  if (events.length === 0) return events;

  const out: ModEvent[] = [];
  let defend: { event: string; kind: string; engagements: number; targets: Record<string, number> } | null = null;
  const hits = events.filter((e) => e.event === "took_damage");
  const foldDamage = hits.length > DAMAGE_DETAIL_LIMIT;
  let damagePlaced = false;

  for (const e of events) {
    if (isAutoDefend(e)) {
      if (!defend) {
        defend = { event: "reflex", kind: "auto_defend", engagements: 0, targets: {} };
        out.push(defend);
      }
      if (e.kind === "auto_defend") {
        defend.engagements++;
        const name = typeof e.name === "string" ? e.name : "unknown";
        defend.targets[name] = (defend.targets[name] ?? 0) + 1;
      }
      continue;
    }
    if (e.event === "took_damage" && foldDamage) {
      if (!damagePlaced) {
        damagePlaced = true;
        out.push(damageSummary(hits));
      }
      continue;
    }
    if (e.event === "task_finished" && "result" in e) {
      const { result: _dropped, ...rest } = e;
      out.push(rest as ModEvent);
      continue;
    }
    out.push(e);
  }

  if (out.length <= MAX_EVENTS) return out;

  // Drop the oldest droppable events until the list (plus the marker) fits.
  let excess = out.length + 1 - MAX_EVENTS;
  const kept: ModEvent[] = [];
  let omitted = 0;
  for (const e of out) {
    if (excess > 0 && !MUST_KEEP.has(e.event)) {
      excess--;
      omitted++;
      continue;
    }
    kept.push(e);
  }
  return omitted > 0 ? [{ event: "omitted", count: omitted, note: "older events dropped" }, ...kept] : kept;
}

function damageSummary(hits: ModEvent[]): ModEvent {
  const sources: Record<string, number> = {};
  let total = 0;
  let minLife = Infinity;
  for (const h of hits) {
    const amount = typeof h.amount === "number" ? h.amount : 0;
    total += amount;
    const src = typeof h.source === "string" ? h.source : "unknown";
    sources[src] = (sources[src] ?? 0) + amount;
    if (typeof h.life === "number") minLife = Math.min(minLife, h.life);
  }
  const last = hits[hits.length - 1];
  const summary: ModEvent = { event: "took_damage", hits: hits.length, total, sources };
  if (Number.isFinite(minLife)) summary.minLife = minLife;
  if (typeof last.life === "number") summary.life = last.life;
  if (typeof last.lifeMax === "number") summary.lifeMax = last.lifeMax;
  return summary;
}
