using LlmPlayer.Bridge;
using LlmPlayer.Config;
using LlmPlayer.Control;
using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>
/// Auto-defend and quick-heal, ticked by <see cref="TaskHost"/> every update while the AI drives, before the task.
/// Decisions (and the precedence between reflexes and the task) come from Core's <see cref="ReflexArbiter"/>:
/// <list type="number">
/// <item>Quick-heal: any time, no controls involved — calls <see cref="Player.QuickHeal"/> (the quick-heal hotkey's
/// action, same potion/sickness rules) below <c>QuickHealThreshold</c>, or below a running attack's higher
/// <c>heal_below</c> (<see cref="SetHealOverride"/>). Also during attack: quick-heal never needs the controls.</item>
/// <item>Auto-defend: while idle, waiting, or in a task it may pause (<see cref="TaskPhases"/>: move_to, explore,
/// talk_to_npc, chop_tree, mine, mine_area, pick_up_items, place, use_item until it presses "use"), and a visible
/// hostile is within the best weapon's reach (or just touched the player and is one tile beyond it,
/// <see cref="ThreatRules"/>). <see cref="Tick"/> then returns true: reflexes own <see cref="ControlState.Pending"/>
/// this tick and TaskHost does not tick the task (it is paused, not cancelled, and resumes ~1/3 s after the last
/// threat, once the defender's swing has ended). The defender stands its ground, aims and swings/shoots; it switches
/// to its weapon only after a tool swing in progress ends (<see cref="HeldItemLease.Hold"/> waits; meanwhile "use" is
/// released, so the swing does end). The previously held item comes back (<see cref="EndDefense"/>) before the task
/// ticks again, so a task lease that swapped a main-inventory tool into the hotbar finds it where it left it.</item>
/// <item>attack (and a use_item that already pressed "use") keeps the controls.</item>
/// </list>
/// Each firing emits <c>reflex {kind, …}</c> and is noted in get_state's recent events. Main thread only.
/// </summary>
public static class Reflexes
{
    private const int WeaponCacheTicks = 30;

    private static readonly ReflexArbiter Arbiter = new();
    private static HeldItemLease _lease = new();
    private static Weapon? _weapon;
    private static uint _weaponAt;
    private static int _targetId = -1;
    private static bool _lastUse;

    private static object? _healOwner;
    private static float _healOverride;

    /// <summary>Quick-heals that used a potion since the game started (attack reports its own difference).</summary>
    public static int PotionsUsed { get; private set; }

    /// <summary>The quick-heal threshold in effect: the config's, or a running task's higher override.</summary>
    public static float HealThreshold => Math.Max(LlmConfig.Current.QuickHealThreshold, _healOwner != null ? _healOverride : 0f);

    /// <summary>Raise the quick-heal threshold while <paramref name="owner"/> (a task) runs; the config is untouched.</summary>
    public static void SetHealOverride(object owner, float threshold)
    {
        _healOwner = owner;
        _healOverride = threshold;
    }

    /// <summary>Drop <paramref name="owner"/>'s override (no-op if another task set one since).</summary>
    public static void ClearHealOverride(object owner)
    {
        if (ReferenceEquals(_healOwner, owner)) _healOwner = null;
    }

    /// <summary>True while auto-defend owns the controls.</summary>
    public static bool Defending => Arbiter.Defending;

    /// <summary>Which tasks auto-defend may pause: Core's <see cref="TaskPhases"/> (by task name).</summary>
    public static TaskPhase PhaseOf(IGameTask<GameCtx>? task) => TaskPhases.Of(task);

    /// <summary>Run the reflexes for this tick. Returns true when they own the controls (skip the task).</summary>
    public static bool Tick(Player p, IGameTask<GameCtx>? task)
    {
        var cfg = LlmConfig.Current;
        var phase = PhaseOf(task);

        NPC? threat = null;
        if (cfg.AutoDefend && phase != TaskPhase.Busy)
        {
            if (_weapon == null || Main.GameUpdateCount - _weaponAt >= WeaponCacheTicks || !CombatKit.StillUsable(p, _weapon))
            {
                _weapon = CombatKit.BestWeapon(p);
                _weaponAt = Main.GameUpdateCount;
            }
            if (_weapon != null) threat = CombatKit.NearestThreat(p, _weapon);
        }

        float healAt = HealThreshold;
        bool low = p.statLife > 0 && p.statLife < healAt * p.statLifeMax2;
        var d = Arbiter.Tick(new ReflexInputs(phase, cfg.AutoDefend, threat != null, p.statLife, p.statLifeMax2,
            healAt, p.HasBuff(BuffID.PotionSickness) || p.potionDelay > 0,
            low && p.QuickHeal_GetItemToUse() != null, UsingItem: p.itemAnimation > 0 || p.itemTime > 0));

        if (d.QuickHeal) QuickHeal(p);
        if (d.DefendEnded) EndDefense(p);
        if (!d.Defend) return false;

        if (threat != null && (d.DefendStarted || threat.whoAmI != _targetId))
        {
            _targetId = threat.whoAmI;
            Report(new { Kind = "auto_defend", Target = threat.whoAmI, Name = threat.GivenOrTypeName, Paused = task?.Name },
                $"reflex: auto-defend vs {threat.GivenOrTypeName}" + (task != null ? $" (paused {task.Name})" : ""));
        }

        // Stand still; aim and use the weapon at the threat (during the release grace period just hold still).
        ControlIntent intent = default;
        if (threat != null && _weapon is { } w)
        {
            bool held = _lease.Hold(p, w.Slot);
            intent = CombatKit.Aim(intent, p, w, threat, fire: held, ref _lastUse);
        }
        ControlState.Pending = intent;
        return true;
    }

    /// <summary>Forget all reflex state (driver change, world unload) and put the held item back.</summary>
    public static void Reset(Player? p)
    {
        Arbiter.Reset();
        _healOwner = null;
        if (p != null) _lease.Restore(p);
        _lease = new HeldItemLease();
        _weapon = null;
        _targetId = -1;
        _lastUse = false;
    }

    private static void EndDefense(Player p)
    {
        _lease.Restore(p);
        _targetId = -1;
        _lastUse = false;
        ControlState.Pending = default;
        BridgeSystem.Emit(EventNames.Reflex, new { Kind = "auto_defend_end", Resumed = TaskHost.Runner.Current?.Name });
    }

    private static void QuickHeal(Player p)
    {
        int before = p.statLife;
        string? potion = p.QuickHeal_GetItemToUse()?.Name;
        if (potion == null) return;
        int delayBefore = p.potionDelay;
        p.QuickHeal();
        if (p.potionDelay > delayBefore || p.statLife > before) PotionsUsed++;
        Report(new { Kind = "quick_heal", Item = potion, LifeBefore = before, Life = p.statLife, LifeMax = p.statLifeMax2 },
            $"reflex: quick-heal with {potion} ({before} -> {p.statLife} life)");
    }

    private static void Report(object payload, string recent)
    {
        BridgeSystem.Emit(EventNames.Reflex, payload);
        RecentEvents.Add(recent);
    }
}
