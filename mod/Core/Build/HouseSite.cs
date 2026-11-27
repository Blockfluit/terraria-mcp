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
    /// <summary>Natural decoration (rock pile, dye plant…): broken first, then treated as empty.</summary>
    Decor,
}

/// <summary>Background wall at a tile: none, a house wall (player-placed, counts for housing), or another wall
/// (natural cave/dirt walls don't count and can't be replaced without a hammer).</summary>
public enum WallState { None, House, Other }

/// <param name="Natural">A natural ground block (dirt, grass, stone, mud, clay, snow) outside protected areas: a
/// player would mine it out of the way. Only meaningful for <see cref="SiteTile.Solid"/>.</param>
/// <param name="Shaped">A half block or slope (hammered or natural). Furniture and doors can't stand on one.</param>
public readonly record struct SiteCell(SiteTile Tile, WallState Wall = WallState.None, bool Natural = false, bool Shaped = false)
{
    public bool IsBlock => Tile is SiteTile.Solid or SiteTile.Platform;
}

/// <summary>Result of checking a site: what is already there and what remains to be placed.</summary>
/// <param name="ClearFirst">Natural decoration in the way, to break before building.</param>
/// <param name="Bumps">Small natural ground bumps (at most <see cref="HouseSite.MaxBumpHeight"/> tiles above the
/// floor) inside the house or in the door spot: mined before building.</param>
/// <param name="Flatten">Existing frame blocks that are half blocks or slopes: hammered back to full blocks (furniture,
/// doors and the NPC's floor spot need full blocks).</param>
/// <param name="Work">Extra work the site needs beyond a flat plot: floor tiles to fill, bumps to mine (x2),
/// decoration to clear, blocks to flatten. The automatic search prefers less work.</param>
public sealed record SiteCheck(string? Problem, HashSet<TilePoint> FrameToPlace, HashSet<TilePoint> WallsToPlace, bool CanExit,
    HashSet<TilePoint>? ClearFirst = null, HashSet<TilePoint>? Bumps = null, HashSet<TilePoint>? Flatten = null, int Work = 0)
{
    public bool Ok => Problem == null;

    /// <summary>Tiles to mine before building, top-down (bumps and decoration).</summary>
    public List<TilePoint> MineFirst() =>
        (ClearFirst ?? new()).Concat(Bumps ?? new()).Distinct().OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
}

/// <summary>
/// Is a spot fit for a <see cref="HouseLayout"/>, and where is the best one nearby? Pure: the game supplies each
/// tile as a <see cref="SiteCell"/>. Built like a player would on ordinary, uneven ground:
/// <list type="bullet">
/// <item>Interior and door tiles must be empty (air or cuttable plants): no trees, objects, liquid, unknown tiles or
/// NPC houses. Natural ground blocks there at most <see cref="MaxBumpHeight"/> tiles above the floor are bumps to mine
/// first (<see cref="SiteCheck.Bumps"/>); any other block is a problem. Interior tiles with a house wall keep it; a
/// natural wall is a problem (vanilla's room check ignores it and it can't be replaced without a hammer).</item>
/// <item>Frame tiles that already hold a block or platform are kept (vanilla's room check treats any solid tile as
/// wall); empty ones are placed (floor tiles over a dip too: they hang off the rest of the floor). Kept half blocks
/// and slopes get hammered flat (<see cref="SiteCheck.Flatten"/>).</item>
/// <item>In <paramref name="strict"/> mode (automatic spot search): at least a third of the floor row must be solid
/// ground and every floor gap must have ground within <see cref="MaxDipDepth"/> tiles below (a dip, not a cliff); the
/// rest of the frame must be empty except natural bumps in the bottom rows; the player must be able to stand just
/// outside the door.</item>
/// </list>
/// </summary>
public static class HouseSite
{
    /// <summary>Natural blocks this high above the floor (rows FloorY-1, FloorY-2) are mined out of the way.</summary>
    public const int MaxBumpHeight = 2;
    /// <summary>Strict mode: a floor gap needs ground within this many tiles below it.</summary>
    public const int MaxDipDepth = 4;

    private static bool IsBump(HouseLayout l, TilePoint t, SiteCell c) =>
        c.Tile == SiteTile.Solid && c.Natural && t.Y < l.FloorY && t.Y >= l.FloorY - MaxBumpHeight;

    public static SiteCheck Check(HouseLayout l, Func<int, int, SiteCell> rawCell, bool strict)
    {
        // Decoration is broken first, so for every rule below it counts as empty air (its wall state kept).
        var clear = new HashSet<TilePoint>();
        SiteCell cell(int x, int y)
        {
            var c = rawCell(x, y);
            if (c.Tile != SiteTile.Decor) return c;
            clear.Add(new TilePoint(x, y));
            return c with { Tile = SiteTile.Air };
        }
        var frame = new HashSet<TilePoint>();
        var walls = new HashSet<TilePoint>();
        var bumps = new HashSet<TilePoint>();
        var flatten = new HashSet<TilePoint>();
        string? problem = null;
        int groundFloor = 0, floorFill = 0;

        foreach (var t in l.FrameTiles())
        {
            var c = cell(t.X, t.Y);
            bool floor = t.Y == l.FloorY;
            if (strict && floor)
            {
                if (c.Tile == SiteTile.Air)
                {
                    bool ground = false;
                    for (int d = 1; d <= MaxDipDepth && !ground; d++) ground = rawCell(t.X, t.Y + d).Tile == SiteTile.Solid;
                    if (!ground) { problem ??= $"floor {t} is over a drop deeper than {MaxDipDepth} tiles"; continue; }
                }
                else if (c.Tile != SiteTile.Solid) { problem ??= $"floor {t} isn't solid ground ({Describe(c.Tile)})"; continue; }
            }
            if (strict && !floor && c.Tile != SiteTile.Air && !IsBump(l, t, c)) { problem ??= $"{t} isn't empty ({Describe(c.Tile)})"; continue; }
            switch (c.Tile)
            {
                case SiteTile.Air:
                    frame.Add(t);
                    if (floor) floorFill++;
                    break;
                case SiteTile.Solid or SiteTile.Platform:
                    if (floor && c.Tile == SiteTile.Solid) groundFloor++;
                    if (c.Tile == SiteTile.Solid && c.Shaped) flatten.Add(t);
                    break;
                default: problem ??= $"frame tile {t} holds {Describe(c.Tile)}"; break;
            }
        }
        if (strict && problem == null && groundFloor < MinGround(l.Width))
            problem = $"only {groundFloor} of the {l.Width} floor tiles rest on ground (needs {MinGround(l.Width)})";
        foreach (var t in l.DoorTiles)
        {
            var c = cell(t.X, t.Y);
            if (IsBump(l, t, c)) bumps.Add(t);
            else if (c.Tile != SiteTile.Air) problem ??= $"door spot {t} isn't empty ({Describe(c.Tile)})";
        }
        foreach (var t in l.InteriorTiles())
        {
            var c = cell(t.X, t.Y);
            if (IsBump(l, t, c)) bumps.Add(t);
            else if (c.Tile != SiteTile.Air) { problem ??= $"{t} inside the house isn't empty ({Describe(c.Tile)}{(c.Tile == SiteTile.Solid ? BumpHint(l, t, c) : "")})"; continue; }
            if (c.Wall == WallState.Other) { problem ??= $"{t} has a natural background wall (not valid for housing; hammer it off or pick another spot)"; continue; }
            if (c.Wall == WallState.None) walls.Add(t);
        }

        bool canExit = ExitClear(l, (x, y) => cell(x, y));
        if (strict && !canExit) problem ??= $"no room to stand outside the door at {l.OutsideStand}";
        int work = floorFill + 2 * bumps.Count + clear.Count + flatten.Count;
        return new SiteCheck(problem, frame, walls, canExit, clear, bumps, flatten, work);
    }

    /// <summary>Floor tiles that must already be ground in strict mode: a third of the width, at least 2.</summary>
    public static int MinGround(int width) => Math.Max(2, (width + 2) / 3);

    private static string BumpHint(HouseLayout l, TilePoint t, SiteCell c) =>
        !c.Natural ? ": not natural ground, or in a protected area"
        : t.Y < l.FloorY - MaxBumpHeight ? $": more than {MaxBumpHeight} tiles above the floor (only small bumps are mined first)"
        : "";

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
    /// The best strict site near <paramref name="player"/> (feet tile) within <paramref name="radius"/>: lowest
    /// <c>distance + 2 x work</c> (<see cref="SiteCheck.Work"/>: floor gaps to fill, bumps to mine…), the door on the
    /// side facing the player first on ties. Distance = from the player's feet to the tile outside the door. Null if
    /// none.
    /// </summary>
    public static HouseLayout? Find(TilePoint player, int width, int height, Func<int, int, SiteCell> cell, int radius = 30)
        => FindWithCheck(player, width, height, cell, radius)?.Layout;

    /// <summary><see cref="Find"/> plus the winning site's check.</summary>
    public static (HouseLayout Layout, SiteCheck Check)? FindWithCheck(TilePoint player, int width, int height,
        Func<int, int, SiteCell> cell, int radius = 30)
    {
        // Every candidate reads ~70 cells and neighbouring candidates overlap: read each cell once.
        int x0 = player.X - radius - 4, y0 = player.Y - radius - height - 2;
        int w = 2 * radius + width + 9, h = 2 * radius + height + MaxDipDepth + 4;
        var cache = new SiteCell?[w, h];
        SiteCell cached(int x, int y)
        {
            int i = x - x0, j = y - y0;
            if (i < 0 || j < 0 || i >= w || j >= h) return cell(x, y);
            return cache[i, j] ??= cell(x, y);
        }

        (HouseLayout, SiteCheck)? best = null;
        int bestCost = int.MaxValue;
        for (int floorY = player.Y - radius; floorY <= player.Y + radius; floorY++)
            for (int x = player.X - radius; x <= player.X + radius; x++)
            {
                // Cheap pre-check: both floor corners are ground or a gap, the tiles above the bumps are open air.
                if (cached(x, floorY).Tile is not (SiteTile.Solid or SiteTile.Air)
                    || cached(x + width - 1, floorY).Tile is not (SiteTile.Solid or SiteTile.Air)) continue;
                if (cached(x + 1, floorY - MaxBumpHeight - 1).Tile is not (SiteTile.Air or SiteTile.Decor)
                    || cached(x + width - 2, floorY - MaxBumpHeight - 1).Tile is not (SiteTile.Air or SiteTile.Decor)) continue;
                bool playerLeft = player.X + 1 < x + width / 2f;
                foreach (var door in playerLeft ? new[] { DoorSide.Left, DoorSide.Right } : new[] { DoorSide.Right, DoorSide.Left })
                {
                    var l = new HouseLayout(x, floorY, width, height, door);
                    int dist = Math.Abs(l.OutsideStand.X - player.X) + Math.Abs(l.FloorY - 1 - player.Y);
                    if (dist >= bestCost) continue;
                    var c = Check(l, cached, strict: true);
                    if (!c.Ok) continue;
                    int cost = dist + 2 * c.Work;
                    if (cost >= bestCost) continue;
                    best = (l, c);
                    bestCost = cost;
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
