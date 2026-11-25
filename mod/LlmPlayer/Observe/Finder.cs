using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Observe;
using Terraria;

namespace LlmPlayer.Observe;

/// <summary>
/// find: nearest visible ore / tree / chest / station / npc / item, at most 10.
/// Ores: one result per vein (same name within 3 tiles collapses). Trees: the bottom of the trunk (or a root beside it).
/// Chests and stations: the object's top-left tile. NPCs and items: their standing tile.
/// </summary>
public static class Finder
{
    public static readonly string[] Categories = { "ore", "tree", "chest", "station", "npc", "item" };
    public const int DefaultRadius = 60;
    public const int MaxRadius = 100;
    private const int OreClusterRadius = 3;
    private const int TreeClusterRadius = 1; // trunk + its roots

    public static List<FindResult> Find(Player p, string category, string? name, int radius = DefaultRadius)
    {
        var cat = category.Trim().ToLowerInvariant();
        if (Array.IndexOf(Categories, cat) < 0)
            throw new CommandError(ErrorCodes.BadParams, $"category must be one of {string.Join(", ", Categories)}");
        radius = Math.Clamp(radius, 0, MaxRadius);
        var (px, py) = StateReader.StandingTile(p);

        if (cat is "npc" or "item")
        {
            var entities = EntityReader.ReadAll(p, radius)
                .Where(e => cat == "item" ? e.Kind == EntityKind.Item : e.Kind != EntityKind.Item)
                .Select(e => new FindCandidate(e.Name, e.X, e.Y));
            return FindSelector.Select(entities, px, py, FindSelector.MaxResults, 0, name);
        }

        var cands = ScanTiles(p, cat, px, py, radius);
        int cluster = cat switch { "ore" => OreClusterRadius, "tree" => TreeClusterRadius, _ => 0 };
        return FindSelector.Select(cands, px, py, FindSelector.MaxResults, cluster, name);
    }

    private static List<FindCandidate> ScanTiles(Player p, string cat, int px, int py, int radius)
    {
        var screen = Visibility.Screen;
        var seen = new HashSet<(int, int)>();
        var list = new List<FindCandidate>();
        long r2 = (long)radius * radius;
        for (int y = py - radius; y <= py + radius; y++)
        for (int x = px - radius; x <= px + radius; x++)
        {
            long dx = x - px, dy = y - py;
            if (dx * dx + dy * dy > r2 || !WorldGen.InWorld(x, y)) continue;
            var tile = Main.tile[x, y];
            if (!tile.HasTile || tile.IsActuated) continue;
            int type = tile.TileType;
            bool match = cat switch
            {
                "ore" => TileClass.IsOre(type),
                "tree" => TileClass.IsTree(type) && IsTreeBase(x, y),
                "chest" => TileClass.IsContainer(type),
                "station" => TileClass.IsStation(type),
                _ => false,
            };
            if (!match || !Visibility.IsTileVisible(x, y, screen, p)) continue;
            int rx = x, ry = y;
            if (cat is "chest" or "station") (rx, ry) = TileClass.ObjectOrigin(x, y, tile);
            if (!seen.Add((rx, ry))) continue;
            list.Add(new FindCandidate(TileClass.Name(type), rx, ry));
        }
        return list;
    }

    /// <summary>Bottom trunk tile: it rests on solid non-tree ground (branches hang over air, so they don't count;
    /// roots beside the trunk do, and are collapsed by the tree cluster radius).</summary>
    private static bool IsTreeBase(int x, int y)
    {
        if (!WorldGen.InWorld(x, y + 1)) return false;
        var below = Main.tile[x, y + 1];
        return below.HasTile && !TileClass.IsTree(below.TileType) && Main.tileSolid[below.TileType];
    }
}
