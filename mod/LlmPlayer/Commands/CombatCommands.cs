using System.Text.Json;
using LlmPlayer.Bridge;
using LlmPlayer.Config;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Inventory;
using LlmPlayer.Inventory;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Microsoft.Xna.Framework;
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
            float? healBelow = null;
            if (Params.Has(p, "heal_below"))
            {
                float hb = Params.Float(p, "heal_below");
                if (!float.IsFinite(hb) || hb < 0f || hb >= 1f)
                    throw new CommandError(ErrorCodes.BadParams, "heal_below must be >= 0 and < 1");
                healBelow = hb;
            }
            bool killMinions = Params.Bool(p, "kill_minions", false);
            ArenaBounds? arena = ParseArena(p);
            var chosen = ResolveWeapon(player, p);
            var ammo = ResolveAmmo(player, p, chosen);
            var strategy = AttackStrategy.Auto;
            if (Params.StringOrNull(p, "strategy") is { } sName && !Strategies.TryParse(sName, out strategy))
                throw new CommandError(ErrorCodes.BadParams, $"strategy must be one of {string.Join(", ", Strategies.Names)}");
            float? keep = null;
            if (Params.Has(p, "keep_distance"))
            {
                float kd = Params.Float(p, "keep_distance");
                if (!float.IsFinite(kd) || kd < Strategies.MinKeepTiles || kd > Strategies.MaxKeepTiles)
                    throw new CommandError(ErrorCodes.BadParams, $"keep_distance must be {Strategies.MinKeepTiles:0}..{Strategies.MaxKeepTiles:0} tiles");
                keep = kd;
            }
            bool fallback = Params.Bool(p, "fallback", true);
            if (chosen != null && CombatKit.Candidate(player, CombatKit.Locate(player, chosen)) is { } cc
                && Strategies.CheckWeapon(strategy, cc.Style, chosen.Name) is { } mismatch)
                throw new CommandError(ErrorCodes.BadParams, mismatch);
            var npc = all ? Nearest(player, radius) : ResolveTarget(p, player);
            var weapon = chosen ?? CombatKit.BestWeapon(player, npc.defense, Strategies.RequiredStyle(strategy), ammo?.ammo ?? 0)?.Item
                ?? throw new CommandError(ErrorCodes.MissingItem, "no usable weapon (need a damaging item with its ammo/mana)");
            var loadout = new WeaponLoadout(chosen, ammo, fallback, strategy);
            var task = new AttackTask(npc, all, radius, retreat, arena, killMinions, healBelow, loadout, keep);
            TaskHost.Start(task);
            return new
            {
                Started = true,
                Task = task.Name,
                Target = new { Id = npc.whoAmI, Name = npc.GivenOrTypeName },
                All = all,
                Weapon = weapon.Name,
                WeaponChoice = chosen != null ? "chosen" : "auto",
                Ammo = ammo?.Name ?? (weapon.useAmmo > 0 ? player.ChooseAmmo(weapon)?.Name : null),
                Strategy = Strategies.Name(strategy),
                KeepDistance = strategy is AttackStrategy.Kite or AttackStrategy.FleeAndShoot ? Strategies.Keep(keep) : (float?)null,
                Fallback = fallback,
                Boss = EntityReader.IsBoss(npc) ? true : (bool?)null,
                Arena = arena is { } a ? new { a.X1, a.X2 } : null,
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
            var pl = Main.LocalPlayer;
            bool hasWeapon = Params.Has(p, "weapon"), hasAmmo = Params.Has(p, "ammo");
            var defWeapon = hasWeapon ? ResolveWeapon(pl, p) : Reflexes.DefendWeapon;
            var defAmmo = hasAmmo ? ResolveAmmo(pl, p, defWeapon) : Reflexes.DefendAmmo;
            if (!hasAmmo && defAmmo != null && defWeapon != null && defWeapon.useAmmo != defAmmo.ammo) defAmmo = null; // new weapon, old ammo doesn't fit
            bool? autoDefend = Params.Has(p, "auto_defend") ? Params.Bool(p, "auto_defend") : null;
            bool? pause = Params.Has(p, "pause_when_idle") ? Params.Bool(p, "pause_when_idle") : null;
            int? reserve = Params.Has(p, "platform_reserve") ? Params.Int(p, "platform_reserve") : null;
            if (reserve is < 0 or > 9999) throw new CommandError(ErrorCodes.BadParams, "platform_reserve must be 0..9999");
            List<string>? trash = null;
            if (Params.Has(p, "trash_when_full"))
            {
                var v = p.GetProperty("trash_when_full");
                if (v.ValueKind != JsonValueKind.Array)
                    throw new CommandError(ErrorCodes.BadParams, "trash_when_full must be an array of item names ([] = never trash)");
                trash = new List<string>();
                foreach (var e in v.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString()))
                        throw new CommandError(ErrorCodes.BadParams, $"trash_when_full: {e} is not an item name");
                    var name = e.GetString()!.Trim();
                    if (FullInventory.IsValuable(new ItemWorth(name)))
                        throw new CommandError(ErrorCodes.BadParams, $"trash_when_full: {name} counts as valuable (ores, bars, potions, gems, crystals…); list junk only");
                    if (!trash.Contains(name, StringComparer.OrdinalIgnoreCase)) trash.Add(name);
                }
            }
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
            if (reserve is { } pr) MoveToTask.DefaultPlatformReserve = pr;
            if (trash != null) InventorySpace.TrashWhenFull = trash;
            if (hasWeapon || hasAmmo) Reflexes.SetDefendLoadout(pl, defWeapon, defAmmo);
            bool sp = Main.netMode == NetmodeID.SinglePlayer;
            return new
            {
                AutoDefend = cfg.AutoDefend,
                Weapon = Reflexes.DefendWeapon?.Name ?? "auto",
                Ammo = Reflexes.DefendAmmo?.Name ?? "auto",
                QuickHealThreshold = cfg.QuickHealThreshold,
                PauseWhenIdle = cfg.PauseWhenIdle,
                PlatformReserve = MoveToTask.DefaultPlatformReserve,
                TrashWhenFull = InventorySpace.TrashWhenFull,
                Note = cfg.PauseWhenIdle && !sp ? "pause_when_idle only works in singleplayer" : null,
            };
        }, isAction: true);
    }

    private const float DefaultAllRadius = 25f;

    /// <summary>Optional <c>weapon</c>: an item name or inventory slot (0-57); "auto" or absent = null (best weapon).
    /// Must be a weapon the AI may use, with ammo to fire.</summary>
    private static Item? ResolveWeapon(Player pl, JsonElement p)
    {
        if (ItemParam(p, "weapon") is not { } q) return null;
        var it = pl.inventory[ItemLookup.FindSlot(pl, q)];
        if (!CombatKit.IsWeapon(it))
            throw new CommandError(ErrorCodes.BadParams,
                $"{it.Name} is not a weapon the AI may use (tools, ammo, placeables, potions, summon staves, sentries and explosives don't count)");
        if (it.useAmmo > 0)
        {
            if (!pl.HasAmmo(it))
                throw new CommandError(ErrorCodes.MissingItem, $"{it.Name} has no {CombatKit.AmmoClassName(it.useAmmo)} to fire");
            if (pl.ChooseAmmo(it) is { } next && CombatKit.IsExplosive(next.shoot))
                throw new CommandError(ErrorCodes.BadParams, $"{it.Name} would fire explosive {next.Name} (can break your tiles); not allowed");
        }
        return it;
    }

    /// <summary>Optional <c>ammo</c>: an item name or slot; "auto" or absent = null (vanilla's order). Must be ammo that
    /// fits <paramref name="weapon"/> (or, without one, that some weapon in the inventory fires) and not explosive.</summary>
    private static Item? ResolveAmmo(Player pl, JsonElement p, Item? weapon)
    {
        if (ItemParam(p, "ammo") is not { } q) return null;
        var it = pl.inventory[ItemLookup.FindSlot(pl, q)];
        if (it.ammo <= 0) throw new CommandError(ErrorCodes.BadParams, $"{it.Name} is not ammo");
        if (CombatKit.IsExplosive(it.shoot))
            throw new CommandError(ErrorCodes.BadParams, $"{it.Name} is explosive (can break your tiles); not allowed");
        string cls = CombatKit.AmmoClassName(it.ammo) ?? "ammo";
        if (weapon != null)
        {
            if (weapon.useAmmo != it.ammo)
                throw new CommandError(ErrorCodes.BadParams, weapon.useAmmo > 0
                    ? $"{it.Name} ({cls}) doesn't fit {weapon.Name}, which fires {CombatKit.AmmoClassName(weapon.useAmmo)}"
                    : $"{weapon.Name} uses no ammo");
            return it;
        }
        for (int i = 0; i < 58; i++)
            if (CombatKit.IsWeapon(pl.inventory[i]) && pl.inventory[i].useAmmo == it.ammo) return it;
        throw new CommandError(ErrorCodes.BadParams, $"no weapon in the inventory fires {it.Name} ({cls})");
    }

    /// <summary>A string or integer item param; null when absent or "auto".</summary>
    private static string? ItemParam(JsonElement p, string name)
    {
        if (!Params.Has(p, name)) return null;
        var e = p.GetProperty(name);
        string? q = e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out int n) ? n.ToString() : null,
            JsonValueKind.String => e.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(q)) throw new CommandError(ErrorCodes.BadParams, $"{name} must be an item name or inventory slot (or \"auto\")");
        return string.Equals(q.Trim(), "auto", StringComparison.OrdinalIgnoreCase) ? null : q;
    }

    /// <summary>Optional <c>arena: {x1, x2}</c> (tile columns, any order, at least <see cref="ArenaBounds.MinWidth"/>
    /// wide, at most 400).</summary>
    private static ArenaBounds? ParseArena(JsonElement p)
    {
        if (!Params.Has(p, "arena")) return null;
        var a = p.GetProperty("arena");
        if (a.ValueKind != JsonValueKind.Object) throw new CommandError(ErrorCodes.BadParams, "arena must be {x1, x2}");
        int x1 = Params.Int(a, "x1"), x2 = Params.Int(a, "x2");
        var b = ArenaBounds.Of(x1, x2);
        if (b.Width < ArenaBounds.MinWidth || b.Width > 400)
            throw new CommandError(ErrorCodes.BadParams, $"arena must be {ArenaBounds.MinWidth}..400 tiles wide");
        if (b.X1 < 0 || b.X2 >= Main.maxTilesX) throw new CommandError(ErrorCodes.BadParams, "arena is outside the world");
        return b;
    }

    /// <summary>The nearest living boss (not a segment if a real boss exists), visible or not: vanilla shows every
    /// boss's health bar, and get_state lists them. Null if none.</summary>
    private static NPC? NearestBoss(Player player)
    {
        NPC? best = null;
        float bestD = float.MaxValue;
        foreach (var n in Main.npc)
        {
            if (!n.active || n.life <= 0 || !EntityReader.IsBoss(n) || !TargetRules.IsAttackable(CombatKit.Traits(n))) continue;
            if (!Visibility.IsEntityVisible(n)) continue; // fair vision: only a boss the player can see
            float d = Vector2.Distance(n.Center, player.Center) + (n.boss ? 0f : 100000f);
            if (d < bestD)
            {
                best = n;
                bestD = d;
            }
        }
        return best;
    }

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
        if (!Params.Has(p, "target")) throw new CommandError(ErrorCodes.BadParams, "missing required param 'target' (entity id, \"nearest\", \"boss\" or \"all\")");
        var t = p.GetProperty("target");
        if (t.ValueKind == JsonValueKind.String)
        {
            string? word = t.GetString()?.Trim();
            if (string.Equals(word, "boss", StringComparison.OrdinalIgnoreCase))
                return NearestBoss(player) ?? throw new CommandError(ErrorCodes.NotVisible,
                    "no boss in sight (get_state lists living bosses; wait until it shows up on screen)");
            if (!string.Equals(word, "nearest", StringComparison.OrdinalIgnoreCase))
                throw new CommandError(ErrorCodes.BadParams, "target must be an entity id, \"nearest\", \"boss\" or \"all\"");
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
        if (!Visibility.IsEntityVisible(npc) && !EntityReader.IsBoss(npc))
            throw new CommandError(ErrorCodes.NotVisible, $"entity {id} is not visible");
        return npc;
    }
}
