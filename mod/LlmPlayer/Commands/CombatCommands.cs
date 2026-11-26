using System.Text.Json;
using LlmPlayer.Bridge;
using LlmPlayer.Config;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Combat;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Commands;

/// <summary>attack, wait (tasks) and set_reflexes (instant). All actions.</summary>
[CommandGroup]
public static class CombatCommands
{
    public static void Register()
    {
        CommandRouter.Register("attack", p =>
        {
            var player = Main.LocalPlayer;
            bool all = IsAll(p);
            float radius = Params.Float(p, "radius", DefaultAllRadius);
            if (!float.IsFinite(radius) || radius < 1f || radius > EntityReader.DefaultRadius)
                throw new CommandError(ErrorCodes.BadParams, $"radius must be between 1 and {EntityReader.DefaultRadius}");
            float retreat = Params.Float(p, "retreat_below", 0.3f);
            if (!float.IsFinite(retreat) || retreat < 0f || retreat >= 1f)
                throw new CommandError(ErrorCodes.BadParams, "retreat_below must be >= 0 and < 1 (0 = never retreat)");
            var npc = all ? Nearest(player, radius) : ResolveTarget(p, player);
            var weapon = CombatKit.BestWeapon(player)
                ?? throw new CommandError(ErrorCodes.MissingItem, "no usable weapon (need a damaging item with its ammo/mana)");
            var task = new AttackTask(npc, all, radius, retreat);
            TaskHost.Start(task);
            return new
            {
                Started = true,
                Task = task.Name,
                Target = new { Id = npc.whoAmI, Name = npc.GivenOrTypeName },
                All = all,
                Weapon = weapon.Item.Name,
            };
        }, isAction: true);

        CommandRouter.Register("wait", p =>
        {
            float seconds = Params.Float(p, "seconds");
            if (!float.IsFinite(seconds) || seconds <= 0 || seconds > WaitTask.MaxSeconds)
                throw new CommandError(ErrorCodes.BadParams, $"seconds must be > 0 and <= {WaitTask.MaxSeconds:0}");
            var task = new WaitTask(seconds);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Seconds = seconds };
        }, isAction: true);

        CommandRouter.Register("set_reflexes", p =>
        {
            // Validate everything before changing anything.
            bool? autoDefend = Params.Has(p, "auto_defend") ? Params.Bool(p, "auto_defend") : null;
            bool? pause = Params.Has(p, "pause_when_idle") ? Params.Bool(p, "pause_when_idle") : null;
            float? threshold = null;
            if (Params.Has(p, "quick_heal_threshold"))
            {
                float t = Params.Float(p, "quick_heal_threshold");
                if (!float.IsFinite(t) || t < 0f || t > 1f)
                    throw new CommandError(ErrorCodes.BadParams, "quick_heal_threshold must be between 0 and 1");
                threshold = t;
            }

            // In memory only (the mod config file keeps the user's own defaults).
            var cfg = LlmConfig.Current;
            if (autoDefend is { } a) cfg.AutoDefend = a;
            if (threshold is { } th) cfg.QuickHealThreshold = th;
            if (pause is { } pw) cfg.PauseWhenIdle = pw;
            bool sp = Main.netMode == NetmodeID.SinglePlayer;
            return new
            {
                AutoDefend = cfg.AutoDefend,
                QuickHealThreshold = cfg.QuickHealThreshold,
                PauseWhenIdle = cfg.PauseWhenIdle,
                Note = cfg.PauseWhenIdle && !sp ? "pause_when_idle only works in singleplayer" : null,
            };
        }, isAction: true);
    }

    private const float DefaultAllRadius = 25f;

    private static bool IsAll(JsonElement p) =>
        Params.Has(p, "target") && p.GetProperty("target") is { ValueKind: JsonValueKind.String } t
        && string.Equals(t.GetString()?.Trim(), "all", StringComparison.OrdinalIgnoreCase);

    private static NPC Nearest(Player player, float radius)
    {
        var c = player.Center / 16f;
        var pick = TargetRules.PickNearest(CombatKit.VisibleNpcs(player), c.X, c.Y, radius)
            ?? throw new CommandError(ErrorCodes.NotVisible, $"no hostile enemy visible within {radius:0} tiles");
        return Main.npc[pick.Id];
    }

    /// <summary>
    /// "nearest" → the nearest visible attackable NPC within 40 tiles; an id → that NPC, refused with bad_params if
    /// it's a town NPC, critter, other friendly or invulnerable, and not_visible if the player can't see it.
    /// </summary>
    private static NPC ResolveTarget(JsonElement p, Player player)
    {
        if (!Params.Has(p, "target")) throw new CommandError(ErrorCodes.BadParams, "missing required param 'target' (entity id, \"nearest\" or \"all\")");
        var t = p.GetProperty("target");
        if (t.ValueKind == JsonValueKind.String)
        {
            if (!string.Equals(t.GetString()?.Trim(), "nearest", StringComparison.OrdinalIgnoreCase))
                throw new CommandError(ErrorCodes.BadParams, "target must be an entity id, \"nearest\" or \"all\"");
            return Nearest(player, EntityReader.DefaultRadius);
        }

        int id = Params.Int(p, "target");
        if (id < 0 || id >= Main.maxNPCs || !Main.npc[id].active)
            throw new CommandError(ErrorCodes.BadParams, $"no NPC {id} (ids come from get_entities; items can't be attacked)");
        var npc = Main.npc[id];
        string name = npc.GivenOrTypeName;
        switch (TargetRules.Check(CombatKit.Traits(npc)))
        {
            case TargetVerdict.TownNpc: throw new CommandError(ErrorCodes.BadParams, $"{name} is a town NPC; attacking town NPCs is not allowed");
            case TargetVerdict.Critter: throw new CommandError(ErrorCodes.BadParams, $"{name} is a critter; attacking critters is not allowed");
            case TargetVerdict.Friendly: throw new CommandError(ErrorCodes.BadParams, $"{name} is friendly; only hostile enemies can be attacked");
            case TargetVerdict.Invulnerable: throw new CommandError(ErrorCodes.BadParams, $"{name} can't be damaged");
        }
        if (!Visibility.IsEntityVisible(npc)) throw new CommandError(ErrorCodes.NotVisible, $"entity {id} is not visible");
        return npc;
    }
}
