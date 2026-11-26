using System.Text.Json.Serialization;

namespace LlmPlayer.Core.Observe;

/// <summary>Converts Terraria velocities (pixels per tick, 60 ticks/s, 16 px/tile) for observations.</summary>
public static class Motion
{
    public static double TilesPerSecond(float pxPerTick) => Math.Round(pxPerTick * 60.0 / 16.0, 1);

    /// <summary>(vx, vy) in tiles/second, or (null, null) when the entity is (nearly) stationary, to keep output small.</summary>
    public static (double? Vx, double? Vy) Velocity(float vxPx, float vyPx)
    {
        double vx = TilesPerSecond(vxPx), vy = TilesPerSecond(vyPx);
        if (vx == 0 && vy == 0) return (null, null);
        return (vx, vy);
    }
}

/// <summary>How get_entities prioritises entities inside its budget.</summary>
public enum EntityTier
{
    /// <summary>Bosses and boss segments: always listed first.</summary>
    Boss = 0,
    /// <summary>Hostile NPCs and hostile projectiles.</summary>
    Threat = 1,
    /// <summary>Town NPCs, critters, dropped items.</summary>
    Other = 2,
}

public static class EntityRanking
{
    /// <summary>At most this many boss entries (Eater of Worlds has ~50 segments; only the nearest matter).</summary>
    public const int MaxBosses = 10;

    /// <summary>At most this many projectiles, so a bullet hell doesn't hide the enemies firing it.</summary>
    public const int MaxProjectiles = 10;

    /// <summary>
    /// Pick at most <paramref name="max"/> entities: the nearest <see cref="MaxBosses"/> bosses first, then threats
    /// (hostile NPCs and up to <see cref="MaxProjectiles"/> projectiles) nearest first, then everything else nearest
    /// first. Output is ordered by tier, then distance (ties keep input order).
    /// </summary>
    public static List<T> Select<T>(IEnumerable<T> items, Func<T, EntityTier> tier, Func<T, double> distance,
        Func<T, bool> isProjectile, int max = Nearest.EntityCap, int maxBosses = MaxBosses, int maxProjectiles = MaxProjectiles)
    {
        var result = new List<T>();
        int bosses = 0, projectiles = 0;
        foreach (var it in items.OrderBy(tier).ThenBy(distance))
        {
            if (result.Count >= max) break;
            if (tier(it) == EntityTier.Boss)
            {
                if (bosses >= maxBosses) continue;
                bosses++;
            }
            else if (isProjectile(it))
            {
                if (projectiles >= maxProjectiles) continue;
                projectiles++;
            }
            result.Add(it);
        }
        return result;
    }
}

/// <summary>One boss NPC alive in the world. <paramref name="Segment"/>: part of a multi-NPC boss (Eater of Worlds)
/// that the game counts as a boss but isn't flagged boss itself.</summary>
/// <param name="Visible">The player can see it (or this segment). Out of sight only what the vanilla boss bar shows
/// (name, life) is known: position and distance are left out (fair vision).</param>
public readonly record struct BossSighting(int Id, string Name, int Life, int LifeMax, int X, int Y, double Distance, bool Segment,
    bool Visible = true);

/// <summary>get_state's boss entry. Segments: how many living segments were merged (only for segmented bosses).
/// X/Y/Distance are null (omitted) while it's out of sight.</summary>
public sealed record BossStatus(int Id, string Name, int Life, int LifeMax,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? X,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Distance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Segments = null);

public static class BossSummary
{
    public const int MaxBosses = 5;

    /// <summary>
    /// Real bosses are listed one by one; segments are merged per name into one entry (life summed, position and id of
    /// the nearest visible segment) like the vanilla boss bar. Position only while visible. Nearest first, at most <see cref="MaxBosses"/>.
    /// </summary>
    public static List<BossStatus> Collapse(IEnumerable<BossSighting> sightings)
    {
        var list = new List<BossStatus>();
        var all = sightings.ToList();
        foreach (var b in all.Where(s => !s.Segment))
            list.Add(b.Visible
                ? new BossStatus(b.Id, b.Name, b.Life, b.LifeMax, b.X, b.Y, b.Distance)
                : new BossStatus(b.Id, b.Name, b.Life, b.LifeMax, null, null, null));
        foreach (var g in all.Where(s => s.Segment).GroupBy(s => s.Name))
        {
            // Position of the nearest *visible* segment; none visible → no position.
            var near = g.Where(s => s.Visible).OrderBy(s => s.Distance).FirstOrDefault();
            int id = near.Name != null ? near.Id : g.First().Id;
            list.Add(near.Name != null
                ? new BossStatus(id, g.Key, g.Sum(s => s.Life), g.Sum(s => s.LifeMax), near.X, near.Y, near.Distance, g.Count())
                : new BossStatus(id, g.Key, g.Sum(s => s.Life), g.Sum(s => s.LifeMax), null, null, null, g.Count()));
        }
        // Visible first (nearest first), then the ones only the boss bar knows about.
        return list.OrderBy(b => b.Distance ?? double.MaxValue).Take(MaxBosses).ToList();
    }
}

/// <summary>Game-independent facts about an inventory item, for get_inventory tags.</summary>
public readonly record struct ItemTagInput(
    bool IsWeapon = false,
    int WeaponDamage = 0,
    int HealLife = 0,
    bool IsAmmo = false,
    bool Consumable = false,
    bool Places = false,
    int Pick = 0,
    int Axe = 0,
    int Hammer = 0);

/// <summary>Tags shown on an inventory slot; null = omitted from JSON.</summary>
public readonly record struct ItemTagSet(
    bool? Weapon, int? Damage, int? Heal, bool? Ammo, bool? Consumable, bool? Placeable, int? Pick, int? Axe, int? Hammer);

public static class ItemTags
{
    /// <summary>
    /// weapon + damage for usable weapons; heal = life restored; ammo; placeable for blocks/walls/furniture;
    /// pick/axe/hammer = tool power (axe as shown in game, i.e. the raw value x5); consumable for used-up items that
    /// aren't ammo or placeables (every block is technically consumable — that's noise).
    /// </summary>
    public static ItemTagSet Compute(in ItemTagInput i) => new(
        Weapon: i.IsWeapon ? true : null,
        Damage: i.IsWeapon ? i.WeaponDamage : null,
        Heal: i.HealLife > 0 ? i.HealLife : null,
        Ammo: i.IsAmmo ? true : null,
        Consumable: i.Consumable && !i.IsAmmo && !i.Places ? true : null,
        Placeable: i.Places ? true : null,
        Pick: i.Pick > 0 ? i.Pick : null,
        Axe: i.Axe > 0 ? i.Axe * 5 : null,
        Hammer: i.Hammer > 0 ? i.Hammer : null);
}

/// <summary>get_state's threat summary: how many hostiles are visible nearby and how far the nearest is.</summary>
public sealed record ThreatSummary(int Count, double Nearest);

public static class Threats
{
    /// <summary>Radius (tiles) for get_state's nearbyHostiles.</summary>
    public const int Radius = 40;

    /// <summary>Null when no hostile is within <paramref name="radius"/> (field omitted from get_state).</summary>
    public static ThreatSummary? Summarize(IEnumerable<double> hostileDistances, int radius = Radius)
    {
        var near = hostileDistances.Where(d => d <= radius).ToList();
        return near.Count == 0 ? null : new ThreatSummary(near.Count, Math.Round(near.Min(), 1));
    }
}
