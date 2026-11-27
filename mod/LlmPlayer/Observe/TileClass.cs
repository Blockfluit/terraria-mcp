using LlmPlayer.Core.Observe;
using Terraria;
using Terraria.ID;
using Terraria.Map;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace LlmPlayer.Observe;

/// <summary>Per-tile-type classification shared by GameTileGrid, Protection and Finder. Main thread only.</summary>
public static class TileClass
{
    private static HashSet<int>? _stations;
    private static readonly Dictionary<int, string> Names = new();

    public static void Reset()
    {
        _stations = null;
        Names.Clear();
    }

    public static bool IsTree(int type) => TileID.Sets.IsATreeTrunk[type] || type == TileID.MushroomTrees;

    public static bool IsOre(int type) => TileID.Sets.Ore[type] || type is >= TileID.Sapphire and <= TileID.Diamond;

    public static bool IsContainer(int type) => Main.tileContainer[type];

    public static bool IsDoor(int type) => TileID.Sets.OpenDoorID[type] != -1 || TileID.Sets.CloseDoorID[type] != -1;

    /// <summary>Any tile some recipe lists as a required crafting station.</summary>
    public static bool IsStation(int type)
    {
        if (_stations == null)
        {
            _stations = new HashSet<int>();
            for (int i = 0; i < Recipe.numRecipes; i++)
                foreach (var t in Main.recipe[i].requiredTile)
                    if (t >= 0) _stations.Add(t);
        }
        return _stations.Contains(type);
    }

    /// <summary>Frame-important tile larger than 1x1 (furniture, banners, pots, ...).</summary>
    public static bool IsMultiTileObject(int type)
    {
        if (!Main.tileFrameImportant[type]) return false;
        var data = TileObjectData.GetTileData(type, 0);
        return data != null && (data.Width > 1 || data.Height > 1);
    }

    /// <summary>Furniture/objects the AI must never break (type-based part of Protection). World-generated loot
    /// (pots, life crystals, life fruit: <see cref="Valuables.IsLootType"/>) isn't; shadow orbs, altars, chests,
    /// doors and stations are.</summary>
    public static bool IsProtectedType(int type) =>
        !IsTree(type) && !Valuables.IsLootType(type)
        && (IsContainer(type) || IsStation(type) || IsDoor(type) || IsMultiTileObject(type));

    /// <summary><see cref="IsProtectedType"/> with the frame: also lets the sword in stone of an Enchanted Sword
    /// shrine be broken (<see cref="Valuables.IsBreakableLoot"/>).</summary>
    public static bool IsProtectedTile(Tile tile) =>
        IsProtectedType(tile.TileType) && !Valuables.IsBreakableLoot(tile.TileType, tile.TileFrameX)
        && !Valuables.IsNaturalDecor(tile.TileType, tile.TileFrameX);

    /// <summary>Which cave valuable the tile is (<see cref="Valuables.Classify"/>), or null.</summary>
    public static ValuableKind? Valuable(Tile tile) =>
        tile.HasTile ? Valuables.Classify(tile.TileType, tile.TileFrameX, IsContainer(tile.TileType)) : null;

    /// <summary>Localized tile name ("Copper Ore", "Chest"), cached per type.</summary>
    public static string Name(int type)
    {
        if (Names.TryGetValue(type, out var n)) return n;
        n = "";
        try
        {
            n = Lang.GetMapObjectName(MapHelper.TileToLookup(type, 0));
        }
        catch (Exception)
        {
            // Lookup tables not ready, or modded tile without a map entry.
        }
        if (string.IsNullOrWhiteSpace(n))
            n = TileLoader.GetTile(type)?.Name ?? (TileID.Search.TryGetName(type, out var s) ? s : $"tile {type}");
        Names[type] = n;
        return n;
    }

    /// <summary>Top-left tile of the multi-tile object containing (x,y), from its frame (18 px per sub-tile).</summary>
    public static (int X, int Y) ObjectOrigin(int x, int y, Tile tile)
    {
        // Pots, life crystals, life fruit and orbs are always 2x2 (explore_caves listed each pot 4 times; blast
        // counted one Shadow Orb as two): don't rely on TileObjectData for them.
        (int w, int h) = Valuables.IsFixed2x2(tile.TileType) ? (2, 2)
            : TileObjectData.GetTileData(tile.TileType, 0) is { } data ? (data.Width, data.Height) : (1, 1);
        return Valuables.FrameOrigin(x, y, tile.TileFrameX, tile.TileFrameY, w, h);
    }
}
