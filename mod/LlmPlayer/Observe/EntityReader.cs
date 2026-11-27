using System.Text.Json.Serialization;
using LlmPlayer.Core.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Observe;

public enum EntityKind { Hostile, Town, Critter, Item, Projectile }

/// <summary>One visible NPC, dropped item or hostile projectile. (X,Y) is the tile the entity stands on, like the
/// player's. Optional fields are omitted when null: Boss only when true, velocity (tiles/s) only when moving,
/// Damage/Defense only for hostile NPCs and projectiles when above 0. Profession: a town NPC's type ("Arms Dealer") when its
/// name is a personal one ("Demetrius").</summary>
public sealed record EntityInfo(int Id, string Name, EntityKind Kind, int X, int Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Life,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LifeMax,
    double Distance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Boss = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Vx = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Vy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Damage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Defense = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Profession = null)
{
    [JsonIgnore] public EntityTier Tier =>
        Boss == true ? EntityTier.Boss
        : Kind is EntityKind.Hostile or EntityKind.Projectile ? EntityTier.Threat
        : EntityTier.Other;
}

/// <summary>
/// Visible NPCs, dropped items and hostile projectiles. Entity ids: NPCs use their <c>Main.npc</c> index (0..199);
/// items use <see cref="ItemIdOffset"/> + their <c>Main.item</c> index; projectiles use <see cref="ProjectileIdOffset"/>
/// + their <c>Main.projectile</c> index (informational only: they can't be targeted). Use <see cref="TryResolve"/> to turn an id back into an entity.
/// </summary>
public static class EntityReader
{
    public const int ItemIdOffset = 1000;
    public const int ProjectileIdOffset = 2000;
    public const int DefaultRadius = 40;
    public const int MaxRadius = 200;

    /// <summary>get_entities: visible entities and hostile projectiles within <paramref name="radius"/> tiles, at most
    /// 30, ranked by <see cref="EntityRanking.Select"/> (bosses, then threats, then the rest; nearest first in each).</summary>
    public static List<EntityInfo> Read(Player p, int radius = DefaultRadius) =>
        EntityRanking.Select(ReadAll(p, radius).Concat(ReadProjectiles(p, radius)),
            e => e.Tier, e => e.Distance, e => e.Kind == EntityKind.Projectile);

    /// <summary>Visible hostile, damaging projectiles in radius (unsorted).</summary>
    public static IEnumerable<EntityInfo> ReadProjectiles(Player p, int radius)
    {
        radius = Math.Clamp(radius, 0, MaxRadius);
        var screen = Visibility.Screen;
        var (px, py) = StateReader.StandingTile(p);
        for (int i = 0; i < Main.maxProjectiles; i++)
        {
            var proj = Main.projectile[i];
            if (!proj.active || !proj.hostile || proj.damage <= 0 || !Visibility.IsEntityVisible(proj, screen, p)) continue;
            var (x, y) = TileOf(proj);
            double d = Dist(x - px, y - py);
            if (d > radius) continue;
            var (vx, vy) = Motion.Velocity(proj.velocity.X, proj.velocity.Y);
            yield return new EntityInfo(ProjectileIdOffset + i, proj.Name, EntityKind.Projectile, x, y, null, null, d,
                Vx: vx, Vy: vy, Damage: proj.damage);
        }
    }

    /// <summary>A boss, or part of one the game counts as a boss (Eater of Worlds segments).</summary>
    public static bool IsBoss(NPC npc) =>
        npc.boss || (npc.type >= 0 && npc.type < NPCID.Sets.ShouldBeCountedAsBoss.Length && NPCID.Sets.ShouldBeCountedAsBoss[npc.type]);

    /// <summary>All visible entities in radius (unsorted, uncapped). For find/markers.</summary>
    public static IEnumerable<EntityInfo> ReadAll(Player p, int radius)
    {
        radius = Math.Clamp(radius, 0, MaxRadius);
        var screen = Visibility.Screen;
        var (px, py) = StateReader.StandingTile(p);
        foreach (var npc in Main.npc)
        {
            if (!npc.active || !Visibility.IsEntityVisible(npc, screen, p)) continue;
            var info = Describe(npc, px, py);
            if (info.Distance <= radius) yield return info;
        }
        for (int i = 0; i < Main.maxItems; i++)
        {
            var item = Main.item[i];
            if (!item.active || item.IsAir || !Visibility.IsEntityVisible(item, screen, p)) continue;
            var info = Describe(item, i, px, py);
            if (info.Distance <= radius) yield return info;
        }
    }

    public static EntityKind KindOf(NPC npc) =>
        npc.townNPC ? EntityKind.Town
        : npc.CountsAsACritter ? EntityKind.Critter
        : npc.friendly ? EntityKind.Town // bound NPCs and other friendlies: never attack
        : EntityKind.Hostile;

    /// <summary>Resolve an entity id from get_entities. Returns false if gone or inactive (visibility not checked).</summary>
    public static bool TryResolve(int id, out Entity entity)
    {
        entity = null!;
        if (id >= 0 && id < Main.maxNPCs && Main.npc[id].active) entity = Main.npc[id];
        else if (id >= ItemIdOffset && id - ItemIdOffset < Main.maxItems && Main.item[id - ItemIdOffset].active)
            entity = Main.item[id - ItemIdOffset];
        return entity != null;
    }

    public static (int X, int Y) TileOf(Entity e) =>
        ((int)Math.Floor(e.Center.X / 16f), (int)Math.Floor((e.Bottom.Y - 1f) / 16f));

    private static EntityInfo Describe(NPC npc, int px, int py)
    {
        var (x, y) = TileOf(npc);
        var kind = KindOf(npc);
        bool hostile = kind == EntityKind.Hostile;
        var (vx, vy) = Motion.Velocity(npc.velocity.X, npc.velocity.Y);
        return new EntityInfo(npc.whoAmI, npc.GivenOrTypeName, kind, x, y, npc.life, npc.lifeMax, Dist(x - px, y - py),
            Boss: IsBoss(npc) ? true : null, Vx: vx, Vy: vy,
            Damage: hostile && npc.damage > 0 ? npc.damage : null,
            Defense: hostile && npc.defense > 0 ? npc.defense : null,
            Profession: kind == EntityKind.Town ? ProfessionOf(npc) : null);
    }

    /// <summary>The NPC's type name ("Arms Dealer") when it differs from its shown name (a personal name), else null.</summary>
    public static string? ProfessionOf(NPC npc)
    {
        string type = npc.TypeName;
        return string.IsNullOrEmpty(type) || type == npc.GivenOrTypeName ? null : type;
    }

    private static EntityInfo Describe(Item item, int index, int px, int py)
    {
        var (x, y) = TileOf(item);
        string name = item.stack > 1 ? $"{item.Name} x{item.stack}" : item.Name;
        return new EntityInfo(ItemIdOffset + index, name, EntityKind.Item, x, y, null, null, Dist(x - px, y - py));
    }

    private static double Dist(int dx, int dy) => Math.Round(Math.Sqrt((double)dx * dx + (double)dy * dy), 1);
}
