import { z } from "zod";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { GetPromptResult } from "@modelcontextprotocol/sdk/types.js";

/**
 * MCP prompts: user-invoked templates (Claude Code shows them as /mcp__terraria__<name>). They only package how to
 * use the existing tools for a recurring situation; they carry no game data and call nothing. Live state stays in
 * the tools (it comes with the event stream), and the always-needed rules stay in SERVER_INSTRUCTIONS.
 */

const FAIR_PLAY =
  "Fair play, always: use only what the tools show (fair vision; '?' on get_map is unknown), real controls and vanilla " +
  "reach; no teleporting, no item spawning, no guessing off-screen positions.";

const CONTROL =
  "Control: call request_control before acting. If the user presses Insert (a control_changed event with driver " +
  "'user', or user_has_control errors) or request_control is refused, stop at once, don't retry, and ask the user.";

const LOOP =
  "Loop: get_state (position, life, time, bosses, lastDeaths, events) -> get_map / find / get_entities / get_inventory " +
  "as needed -> one action -> wait_for_task (raise timeout_s for long tasks; on timed_out call it again or " +
  "cancel_task) -> read report.status/reason and the events (took_damage, player_died, weapon_switch, " +
  "inventory_full) -> repeat. A new task cancels the running one, so finish or cancel before starting the next.";

const text = (t: string): GetPromptResult => ({ messages: [{ role: "user", content: { type: "text", text: t } }] });

export interface PromptDef {
  name: string;
  title: string;
  description: string;
  args: Record<string, { optional: boolean; description: string }>;
  render: (args: Record<string, string | undefined>) => string;
}

export const PROMPTS: PromptDef[] = [
  {
    name: "play",
    title: "Play toward a goal",
    description: "Play the character toward a goal with the standard loop, fair-play rules and control etiquette.",
    args: {
      goal: { optional: false, description: 'What to achieve, e.g. "beat the Eye of Cthulhu" or "get 20 Iron Bars"' },
      time_budget: { optional: true, description: 'Optional limit, e.g. "20 minutes" or "one in-game night"' },
    },
    render: ({ goal, time_budget }) =>
      [
        `Play my Terraria character toward this goal: ${goal}.` +
          (time_budget ? ` Time budget: ${time_budget}; stop and report when it is used up.` : ""),
        "",
        "- " + FAIR_PLAY,
        "- " + CONTROL,
        "- " + LOOP,
        "- Plan only the next step toward the goal; plan the one after that once it is done.",
        "- You decide weapon, ammo and strategy (attack weapon/ammo/strategy, set_reflexes); the mod only does sub-second control.",
        "- Safety: carry healing potions, set_reflexes quick_heal_threshold, keep torches and platforms; at night or low life head for shelter.",
        "- After a death (player_died event, or a new lastDeaths entry), handle it like the after_death prompt before continuing.",
        "- When a task fails, read its reason and change the approach instead of repeating the same call.",
        "- Report progress briefly every few steps, and stop with a summary when the goal is reached or blocked.",
      ].join("\n"),
  },
  {
    name: "prepare_boss",
    title: "Prepare and fight a boss",
    description: "Checklist to get ready for a boss (gear, potions, arena, summon) and then fight it with attack.",
    args: {
      boss: { optional: false, description: 'Boss name, e.g. "Eye of Cthulhu" or "King Slime"' },
    },
    render: ({ boss }) =>
      [
        `Prepare for ${boss} and fight it when ready. ${FAIR_PLAY} ${CONTROL}`,
        "",
        `1. get_boss_info boss "${boss}": summon item and recipe, when it can be summoned, stats, tips, defeated. ` +
          "It covers Eye of Cthulhu and King Slime; for other bosses use your own knowledge of vanilla 1.4.4 Classic.",
        "2. get_state and get_inventory: life, defense, armor, weapons (damage, useTime, ammoType), ammo, potions, " +
          "coins. List what is missing and gather or craft it first, one task at a time (craft with_intermediates).",
        "3. Gear: the best armor equipped (equip), a ranged weapon with plenty of ammo (hundreds of arrows) or a strong " +
          "melee weapon, healing potions (Lesser Healing Potions at least), and buff potions if you have them.",
        "4. Arena: a flat, open space near the summon spot. build_platforms 2-3 rows 60-100 tiles long with spacing ~5 " +
          "so you can jump between rows; 80+ tiles wide and flat for King Slime. Stand on it before summoning.",
        "5. Timing: check get_state time (e.g. the Eye of Cthulhu is summoned at night and flees at dawn).",
        "6. Just before: drink buffs with use_item (queue several, then one wait_for_task), select the weapon.",
        "7. Summon with use_item, then attack target \"boss\" with YOUR choice of weapon, ammo and strategy (e.g. kite " +
          "for a bow), heal_below ~0.5, retreat_below 0, and arena {x1, x2} = the arena span.",
        "8. wait_for_task with timeout_s 120-300, check get_state (boss life %) between waits; adjust weapon/strategy " +
          "with a new attack if it goes badly. On death, follow the after_death prompt.",
        "9. After the kill: pick_up_items for the drops, then report the result and suggest the next boss.",
      ].join("\n"),
  },
  {
    name: "after_death",
    title: "Recover after a death",
    description: "Read what killed the player, recover dropped coins/items fairly and adjust the plan before retrying.",
    args: {},
    render: () =>
      [
        `The character died. Recover and adjust before trying again. ${FAIR_PLAY} ${CONTROL}`,
        "",
        "1. get_state: lastDeaths[0] (text, killer, x, y, secondsAgo), current position (the spawn point), life, and the " +
          "events before the death (took_damage sources, reflex quick_heal, weapon_switch).",
        "2. Explain briefly why it happened: which enemy or hazard, low life without potions, cornered, fell, drowned, " +
          "wrong weapon or ammo ran out.",
        "3. Recover: in Classic mode about half your coins drop where you died (other items too in Mediumcore). Walk " +
          "back with move_to to the death x,y if the way is safe, then pick_up_items; dropped items despawn after a while.",
        "4. Adjust the plan: better armor or weapon, more potions, a higher quick_heal_threshold or heal_below, a " +
          "different strategy or arena, fighting in daylight, or skip the danger.",
        "5. Say what you will change, then continue with the next step.",
      ].join("\n"),
  },
];

/** Registers every prompt in PROMPTS. Arguments are strings (MCP prompt arguments always are). */
export function registerPrompts(server: McpServer): void {
  for (const p of PROMPTS) {
    const shape: Record<string, z.ZodType<string | undefined>> = {};
    for (const [name, a] of Object.entries(p.args)) {
      const s = z.string().min(1);
      shape[name] = (a.optional ? s.optional() : s).describe(a.description);
    }
    const config = { title: p.title, description: p.description };
    if (Object.keys(shape).length === 0) server.registerPrompt(p.name, config, () => text(p.render({})));
    else
      server.registerPrompt(p.name, { ...config, argsSchema: shape as z.ZodRawShape }, ((args: Record<string, string | undefined>) =>
        text(p.render(args))) as never);
  }
}
