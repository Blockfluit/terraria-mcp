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

    /// <summary>World-generated multi-tile objects that are loot, not the user's builds, so they aren't protected:
    /// pots, life crystals, life fruit.</summary>
    private static readonly HashSet<int> NaturalBreakables = new() { TileID.Pots, TileID.Heart, TileID.LifeFruit };

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

    /// <summary>Furniture/objects the AI must never break (type-based part of Protection).</summary>
    public static bool IsProtectedType(int type) =>
        !IsTree(type) && !NaturalBreakables.Contains(type)
        && (IsContainer(type) || IsStation(type) || IsDoor(type) || IsMultiTileObject(type));

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
        var data = TileObjectData.GetTileData(tile.TileType, 0);
        if (data == null) return (x, y);
        int ox = tile.TileFrameX / 18 % Math.Max(1, data.Width);
        int oy = tile.TileFrameY / 18 % Math.Max(1, data.Height);
        return (x - ox, y - oy);
    }
}
