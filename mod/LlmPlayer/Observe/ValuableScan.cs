using LlmPlayer.Core.Observe;
using LlmPlayer.Interact;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Observe;

/// <summary>One visible valuable: multi-tile objects at their top-left tile, ores at the tile seen, Fallen Stars at
/// the item's standing tile.</summary>
public readonly record struct ValuableHit(ValuableKind Kind, string Name, int X, int Y);

/// <summary>
/// Visible cave valuables around a point (fair vision: <see cref="Visibility.IsTileVisible(int,int,Core.Observe.ScreenRect,Player)"/>,
/// items via <see cref="EntityReader"/>): life crystals, life fruit, pots, chests (named like the hover text, e.g.
/// "Locked Gold Chest"), the Enchanted Sword shrine's sword in stone, Demon/Crimson Altars, Shadow Orbs/Crimson
/// Hearts, Fallen Stars on the ground, and ores when <c>includeOre</c>. Main thread only.
/// </summary>
public static class ValuableScan
{
    public static List<ValuableHit> Scan(Player p, int cx, int cy, int radius, bool includeOre)
    {
        var screen = Visibility.Screen;
        var seen = new HashSet<(int, int)>();
        var list = new List<ValuableHit>();
        long r2 = (long)radius * radius;
        for (int y = cy - radius; y <= cy + radius; y++)
        for (int x = cx - radius; x <= cx + radius; x++)
        {
            long dx = x - cx, dy = y - cy;
            if (dx * dx + dy * dy > r2 || !WorldGen.InWorld(x, y)) continue;
            var tile = Main.tile[x, y];
            if (!tile.HasTile || tile.IsActuated) continue;
            var kind = TileClass.Valuable(tile);
            if (kind == null && includeOre && TileClass.IsOre(tile.TileType)) kind = ValuableKind.Ore;
            if (kind is not { } k || !Visibility.IsTileVisible(x, y, screen, p)) continue;
            int rx = x, ry = y;
            if (k != ValuableKind.Ore) (rx, ry) = TileClass.ObjectOrigin(x, y, tile);
            if (!seen.Add((rx, ry))) continue;
            list.Add(new ValuableHit(k, NameOf(k, rx, ry, tile), rx, ry));
        }
        foreach (var e in EntityReader.ReadAll(p, radius))
            if (e.Kind == EntityKind.Item && EntityReader.TryResolve(e.Id, out var ent) && ent is Item { type: ItemID.FallenStar })
                list.Add(new ValuableHit(ValuableKind.FallenStar, e.Name, e.X, e.Y));
        return list;
    }

    private static string NameOf(ValuableKind k, int x, int y, Tile tile)
    {
        switch (k)
        {
            case ValuableKind.Chest:
                return ChestAccess.Resolve(x, y) is { } c ? ChestAccess.NameOf(c) : TileClass.Name(tile.TileType);
            case ValuableKind.Ore:
                return TileClass.Name(tile.TileType);
            default:
                // Frame-dependent names (orb vs crimson heart, altar kind, sword style) come from the object's origin.
                var o = Main.tile[x, y];
                return Valuables.Name(k, o.HasTile && o.TileType == tile.TileType ? o.TileFrameX : tile.TileFrameX);
        }
    }
}
