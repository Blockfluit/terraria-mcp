using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Build;

/// <summary>What occupies a tile, as far as building a house there is concerned.</summary>
public enum SiteTile
{
    /// <summary>Nothing (cuttable plants count as nothing: placing replaces them).</summary>
    Air,
    /// <summary>A solid block that isn't a protected object: usable as part of the frame.</summary>
    Solid,
    Platform,
    Tree,
    /// <summary>Liquid at least half full.</summary>
    Liquid,
    /// <summary>Furniture, a chest, a station, a door, a torch or another multi-tile object.</summary>
    Object,
    /// <summary>Not visible to the player (fair vision).</summary>
    Unknown,
    /// <summary>Inside (or bounding) a town NPC's house: never built on.</summary>
    NpcHouse,
}

/// <summary>Background wall at a tile: none, a house wall (player-placed, counts for housing), or another wall
/// (natural cave/dirt walls don't count and can't be replaced without a hammer).</summary>
public enum WallState { None, House, Other }

public readonly record struct SiteCell(SiteTile Tile, WallState Wall = WallState.None)
{
    public bool IsBlock => Tile is SiteTile.Solid or SiteTile.Platform;
}

/// <summary>Result of checking a site: what is already there and what remains to be placed.</summary>
public sealed record SiteCheck(string? Problem, HashSet<TilePoint> FrameToPlace, HashSet<TilePoint> WallsToPlace, bool CanExit)
{
    public bool Ok => Problem == null;
}

/// <summary>
/// Is a spot fit for a <see cref="HouseLayout"/>, and where is the nearest good one? Pure: the game supplies each
/// tile as a <see cref="SiteCell"/>.
/// <list type="bullet">
/// <item>Interior and door tiles must be empty (air or cuttable plants): no trees, objects, liquid, blocks, unknown
/// tiles or NPC houses. Interior tiles with a house wall keep it; a natural wall is a problem (vanilla's room check
/// ignores it and it can't be replaced without a hammer).</item>
/// <item>Frame tiles that already hold a block or platform are kept (vanilla's room check treats any solid tile as
/// wall); empty ones are placed. In <paramref name="strict"/> mode (automatic spot search) the floor row must already
/// be solid ground, the rest of the frame empty, and the player must be able to stand just outside the door.</item>
/// </list>
/// </summary>
public static class HouseSite
{
    public static SiteCheck Check(HouseLayout l, Func<int, int, SiteCell> cell, bool strict)
    {
        var frame = new HashSet<TilePoint>();
        var walls = new HashSet<TilePoint>();
        string? problem = null;

        foreach (var t in l.FrameTiles())
        {
            var c = cell(t.X, t.Y);
            bool floor = t.Y == l.FloorY;
            if (strict && floor && c.Tile != SiteTile.Solid) { problem ??= $"floor {t} isn't solid ground"; continue; }
            if (strict && !floor && c.Tile != SiteTile.Air) { problem ??= $"{t} isn't empty ({Describe(c.Tile)})"; continue; }
            switch (c.Tile)
            {
                case SiteTile.Air: frame.Add(t); break;
                case SiteTile.Solid or SiteTile.Platform: break;
                default: problem ??= $"frame tile {t} holds {Describe(c.Tile)}"; break;
            }
        }
        foreach (var t in l.DoorTiles)
        {
            var c = cell(t.X, t.Y);
            if (c.Tile != SiteTile.Air) problem ??= $"door spot {t} isn't empty ({Describe(c.Tile)})";
        }
        foreach (var t in l.InteriorTiles())
        {
            var c = cell(t.X, t.Y);
            if (c.Tile != SiteTile.Air) { problem ??= $"{t} inside the house isn't empty ({Describe(c.Tile)})"; continue; }
            if (c.Wall == WallState.Other) { problem ??= $"{t} has a natural background wall (not valid for housing; hammer it off or pick another spot)"; continue; }
            if (c.Wall == WallState.None) walls.Add(t);
        }

        bool canExit = ExitClear(l, cell);
        if (strict && !canExit) problem ??= $"no room to stand outside the door at {l.OutsideStand}";
        return new SiteCheck(problem, frame, walls, canExit);
    }

    /// <summary>Can the player stand just outside the door (2×3 body empty, ground under it)?</summary>
    public static bool ExitClear(HouseLayout l, Func<int, int, SiteCell> cell)
    {
        var s = l.OutsideStand;
        for (int dx = 0; dx < 2; dx++)
        {
            for (int dy = 0; dy < 3; dy++)
                if (cell(s.X + dx, s.Y - dy).Tile != SiteTile.Air) return false;
        }
        return cell(s.X, s.Y + 1).IsBlock || cell(s.X + 1, s.Y + 1).IsBlock;
    }

    /// <summary>
    /// The nearest strict site to <paramref name="player"/> (feet tile) within <paramref name="radius"/>: floors from the
    /// player's row outward, the door on the side facing the player first. Distance = from the player's feet to the
    /// house's door-side bottom corner. Null if none.
    /// </summary>
    public static HouseLayout? Find(TilePoint player, int width, int height, Func<int, int, SiteCell> cell, int radius = 30)
    {
        HouseLayout? best = null;
        int bestDist = int.MaxValue;
        for (int floorY = player.Y - radius; floorY <= player.Y + radius; floorY++)
            for (int x = player.X - radius; x <= player.X + radius; x++)
            {
                // Cheap pre-check: the floor row must be ground and the row above it empty at both ends.
                if (cell(x, floorY).Tile != SiteTile.Solid || cell(x + width - 1, floorY).Tile != SiteTile.Solid) continue;
                if (cell(x, floorY - 1).Tile != SiteTile.Air || cell(x + width - 1, floorY - 1).Tile != SiteTile.Air) continue;
                bool playerLeft = player.X + 1 < x + width / 2f;
                foreach (var door in playerLeft ? new[] { DoorSide.Left, DoorSide.Right } : new[] { DoorSide.Right, DoorSide.Left })
                {
                    var l = new HouseLayout(x, floorY, width, height, door);
                    int dist = Math.Abs(l.OutsideStand.X - player.X) + Math.Abs(l.FloorY - 1 - player.Y);
                    if (dist >= bestDist) continue;
                    if (!Check(l, cell, strict: true).Ok) continue;
                    best = l;
                    bestDist = dist;
                }
            }
        return best;
    }

    public static string Describe(SiteTile t) => t switch
    {
        SiteTile.Air => "nothing",
        SiteTile.Solid => "a block",
        SiteTile.Platform => "a platform",
        SiteTile.Tree => "a tree: chop_tree it first or pick another spot",
        SiteTile.Liquid => "liquid",
        SiteTile.Object => "furniture or another object",
        SiteTile.Unknown => "an unexplored tile",
        SiteTile.NpcHouse => "part of a town NPC's house",
        _ => t.ToString(),
    };
}
