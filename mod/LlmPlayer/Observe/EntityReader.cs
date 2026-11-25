using System.Text.Json.Serialization;
using LlmPlayer.Core.Observe;
using Terraria;

namespace LlmPlayer.Observe;

public enum EntityKind { Hostile, Town, Critter, Item }

/// <summary>One visible NPC or dropped item. (X,Y) is the tile the entity stands on, like the player's.</summary>
public sealed record EntityInfo(int Id, string Name, EntityKind Kind, int X, int Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Life,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LifeMax,
    double Distance);

/// <summary>
/// Visible NPCs and dropped items. Entity ids: NPCs use their <c>Main.npc</c> index (0..199); items use
/// <see cref="ItemIdOffset"/> + their <c>Main.item</c> index. Use <see cref="TryResolve"/> to turn an id back into an entity.
/// </summary>
public static class EntityReader
{
    public const int ItemIdOffset = 1000;
    public const int DefaultRadius = 40;
    public const int MaxRadius = 200;

    /// <summary>Visible entities within <paramref name="radius"/> tiles, nearest first, at most 30.</summary>
    public static List<EntityInfo> Read(Player p, int radius = DefaultRadius) =>
        Nearest.Take(ReadAll(p, radius), e => e.Distance, Nearest.EntityCap);

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
        return new EntityInfo(npc.whoAmI, npc.GivenOrTypeName, KindOf(npc), x, y, npc.life, npc.lifeMax, Dist(x - px, y - py));
    }

    private static EntityInfo Describe(Item item, int index, int px, int py)
    {
        var (x, y) = TileOf(item);
        string name = item.stack > 1 ? $"{item.Name} x{item.stack}" : item.Name;
        return new EntityInfo(ItemIdOffset + index, name, EntityKind.Item, x, y, null, null, Dist(x - px, y - py));
    }

    private static double Dist(int dx, int dy) => Math.Round(Math.Sqrt((double)dx * dx + (double)dy * dy), 1);
}
