import { describe, expect, it } from "vitest";
import { compactEvents, MAX_EVENTS } from "../src/events.js";
import type { ModEvent } from "../src/connection.js";

const defend = (name: string): ModEvent => ({ event: "reflex", kind: "auto_defend", target: 1, name, paused: null });
const defendEnd: ModEvent = { event: "reflex", kind: "auto_defend_end", resumed: null };
const hit = (amount: number, source: string, life: number): ModEvent => ({
  event: "took_damage",
  amount,
  source,
  life,
  lifeMax: 100,
});

describe("compactEvents", () => {
  it("passes small streams through unchanged", () => {
    const evs = [hit(5, "Zombie", 95), { event: "task_finished", name: "mine", status: "succeeded" }];
    expect(compactEvents(evs)).toEqual(evs);
  });

  it("folds auto-defend start/end flapping into one summary at the first position", () => {
    const evs: ModEvent[] = [{ event: "task_finished", name: "mine" }];
    for (let i = 0; i < 30; i++) evs.push(defend(i % 3 === 0 ? "Flying Fish" : "Umbrella Slime"), defendEnd);
    const out = compactEvents(evs);
    expect(out).toEqual([
      { event: "task_finished", name: "mine" },
      { event: "reflex", kind: "auto_defend", engagements: 30, targets: { "Flying Fish": 10, "Umbrella Slime": 20 } },
    ]);
  });

  it("keeps quick_heal reflexes", () => {
    const heal = { event: "reflex", kind: "quick_heal", item: "Mushroom", lifeBefore: 45, life: 60 };
    expect(compactEvents([defend("Slime"), heal, defendEnd])).toContainEqual(heal);
  });

  it("lists up to 3 hits, folds more into a summary", () => {
    expect(compactEvents([hit(5, "A", 95), hit(5, "A", 90), hit(5, "B", 85)])).toHaveLength(3);
    const out = compactEvents([hit(10, "Fish", 90), hit(8, "Fish", 82), hit(9, "Slime", 73), hit(9, "Fish", 64)]);
    expect(out).toEqual([
      { event: "took_damage", hits: 4, total: 36, sources: { Fish: 27, Slime: 9 }, minLife: 64, life: 64, lifeMax: 100 },
    ]);
  });

  it("caps the list but never drops control or task events", () => {
    const evs: ModEvent[] = [{ event: "control_changed", driver: "ai", reason: "hotkey" }];
    for (let i = 0; i < 60; i++) evs.push({ event: "picked_up", item: `thing${i}` });
    evs.push({ event: "task_finished", name: "explore" });
    const out = compactEvents(evs);
    expect(out).toHaveLength(MAX_EVENTS);
    expect(out[0]).toMatchObject({ event: "omitted" });
    expect(out).toContainEqual({ event: "control_changed", driver: "ai", reason: "hotkey" });
    expect(out[out.length - 1]).toEqual({ event: "task_finished", name: "explore" });
    expect(out[out.length - 2]).toEqual({ event: "picked_up", item: "thing59" }); // newest kept
  });

  it("never drops weapon_switch events (the LLM chose that weapon)", () => {
    const sw = { event: "weapon_switch", by: "attack", from: "Wooden Bow", to: "Copper Shortsword", message: "attack: Wooden Bow unusable (out of ammo), switched to Copper Shortsword" };
    const evs: ModEvent[] = [sw];
    for (let i = 0; i < 60; i++) evs.push({ event: "picked_up", item: `thing${i}` });
    expect(compactEvents(evs)).toContainEqual(sw);
  });

  it("drops the result payload from task_finished (the report carries it)", () => {
    const evs = [{ event: "task_finished", name: "talk_to_npc", status: "succeeded", result: { chat: "hi" } }];
    expect(compactEvents(evs)).toEqual([{ event: "task_finished", name: "talk_to_npc", status: "succeeded" }]);
  });
});
