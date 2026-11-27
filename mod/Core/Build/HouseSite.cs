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
/// <param name="Door">A door (open or closed), whatever <paramref name="Tile"/> says (an NPC house's door reads as
/// <see cref="SiteTile.NpcHouse"/>): a new house must not wall off the space in front of it.</param>
/// <param name="Removable">A platform the player may mine (not protected, holds up nothing protected). In a frame
/// position it is replaced by a block (doors need solid blocks above and below and an NPC needs a solid floor spot);
/// inside the house or in the doorway it is mined. Only meaningful for <see cref="SiteTile.Platform"/>.</param>
public readonly record struct SiteCell(SiteTile Tile, WallState Wall = WallState.None, bool Natural = false, bool Shaped = false,
    bool Door = false, bool Removable = false)
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
/// decoration and platforms to clear, platforms to replace (x2), blocks to flatten. The automatic search prefers less
/// work.</param>
/// <param name="Replace">Removable platforms in frame positions: mined first, then a block is placed there (they are
/// in <see cref="FrameToPlace"/> too).</param>
/// <param name="Platforms">Removable platforms inside the house or in the doorway: mined first.</param>
/// <param name="Kind">Short, coordinate-free category of <see cref="Problem"/>, for counting why candidates fail.</param>
public sealed record SiteCheck(string? Problem, HashSet<TilePoint> FrameToPlace, HashSet<TilePoint> WallsToPlace, bool CanExit,
    HashSet<TilePoint>? ClearFirst = null, HashSet<TilePoint>? Bumps = null, HashSet<TilePoint>? Flatten = null, int Work = 0,
    HashSet<TilePoint>? Replace = null, HashSet<TilePoint>? Platforms = null, string? Kind = null)
{
    public bool Ok => Problem == null;

    /// <summary>Tiles to mine before building, top-down (bumps, decoration, platforms in the way or to replace).</summary>
    public List<TilePoint> MineFirst() =>
        (ClearFirst ?? new()).Concat(Bumps ?? new()).Concat(Replace ?? new()).Concat(Platforms ?? new())
            .Distinct().OrderBy(t => t.Y).ThenBy(t => t.X).ToList();

    /// <summary>Tiles to mine that need a pickaxe (everything but decoration).</summary>
    public int PickaxeTiles => (Bumps?.Count ?? 0) + (Replace?.Count ?? 0) + (Platforms?.Count ?? 0);
}

/// <summary>Why the automatic site search rejected candidates: counts per <see cref="SiteCheck.Kind"/> with the first
/// full problem as an example, plus positions skipped by the cheap pre-check.</summary>
public sealed class SiteSearchReport
{
    public int Checked { get; private set; }
    public int PreSkipped { get; internal set; }
    public Dictionary<string, (int Count, string Example)> Reasons { get; } = new();

    internal void Reject(SiteCheck c)
    {
        Checked++;
        string kind = c.Kind ?? "other";
        Reasons[kind] = Reasons.TryGetValue(kind, out var r) ? (r.Count + 1, r.Example) : (1, c.Problem ?? kind);
    }

    /// <summary>"most common reasons: 120x interior not empty (a platform) (e.g. …); …" (top <paramref name="n"/>).</summary>
    public string Top(int n = 3)
    {
        if (Reasons.Count == 0)
            return PreSkipped > 0 ? $"no candidate passed the quick check ({PreSkipped} positions: floor corners not on ground (or over a drop), or no open air above the floor)" : "no candidates";
        var top = Reasons.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key).Take(n)
            .Select(kv => $"{kv.Value.Count}x {kv.Key} (e.g. {kv.Value.Example})");
        return $"{Checked} candidate spots rejected, most often: {string.Join("; ", top)}";
    }
}

/// <summary>
/// Is a spot fit for a <see cref="HouseLayout"/>, and where is the best one nearby? Pure: the game supplies each
/// tile as a <see cref="SiteCell"/>. Built like a player would on ordinary, uneven ground:
/// <list type="bullet">
/// <item>Interior and door tiles must be empty (air or cuttable plants): no trees, objects, liquid, unknown tiles or
/// NPC houses. Natural ground blocks there at most <see cref="MaxBumpHeight"/> tiles above the floor are bumps to mine
/// first (<see cref="SiteCheck.Bumps"/>); any other block is a problem. Interior tiles with a house wall keep it; a
/// natural wall is a problem (vanilla's room check ignores it and it can't be replaced without a hammer).</item>
/// <item>Frame tiles that already hold a block are kept (vanilla's room check treats any solid tile as wall); empty
/// ones are placed (floor tiles over a dip too: they hang off the rest of the floor). Kept half blocks and slopes get
/// hammered flat (<see cref="SiteCheck.Flatten"/>). Removable platforms in the frame are replaced by blocks
/// (<see cref="SiteCheck.Replace"/>: seen in game, a door under a platform was refused, doors need solid blocks
/// above and below); removable platforms inside or in the doorway are mined (<see cref="SiteCheck.Platforms"/>).
/// Platforms that can't be removed (protected) stay as frame (non-strict) or reject the spot.</item>
/// <item>In <paramref name="strict"/> mode (automatic spot search): at least a third of the floor row must be solid
/// ground and every floor gap must have ground within <see cref="MaxDipDepth"/> tiles below (a dip, not a cliff); the
/// rest of the frame must be empty except natural bumps in the bottom rows; the player must be able to stand just
/// outside the door.</item>
/// <item>Both modes: the house must not stand in front of an existing door (a neighbour's): its frame may not come
/// within <see cref="DoorApproach"/> columns of a door beside it, or that door opens onto a gap the 2-wide player
/// can't stand in (seen in game: two houses one tile apart, doors facing each other, the second unreachable).</item>
/// </list>
/// </summary>
public static class HouseSite
{
    /// <summary>Free columns a door needs in front of it: the player's body width.</summary>
    public const int DoorApproach = 2;

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
        var replace = new HashSet<TilePoint>();
        var platforms = new HashSet<TilePoint>();
        string? problem = null, kind = null;
        void Bad(string k, string text)
        {
            if (problem != null) return;
            problem = text;
            kind = k;
        }
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
                    if (!ground) { Bad("floor over a drop", $"floor {t} is over a drop deeper than {MaxDipDepth} tiles"); continue; }
                }
                else if (c.Tile != SiteTile.Solid) { Bad($"floor not solid ground ({Short(c)})", $"floor {t} isn't solid ground ({Describe(c.Tile)})"); continue; }
            }
            bool removable = c.Tile == SiteTile.Platform && c.Removable;
            if (strict && !floor && c.Tile != SiteTile.Air && !IsBump(l, t, c) && !removable)
            { Bad($"frame not empty ({Short(c)})", $"{t} isn't empty ({Describe(c.Tile)})"); continue; }
            switch (c.Tile)
            {
                case SiteTile.Air:
                    frame.Add(t);
                    if (floor) floorFill++;
                    break;
                case SiteTile.Platform when removable:
                    // A platform isn't a door anchor or an NPC floor spot: mine it and put a block in its place.
                    replace.Add(t);
                    frame.Add(t);
                    break;
                case SiteTile.Solid or SiteTile.Platform:
                    if (floor && c.Tile == SiteTile.Solid) groundFloor++;
                    if (c.Tile == SiteTile.Solid && c.Shaped) flatten.Add(t);
                    break;
                default: Bad($"frame holds {Short(c)}", $"frame tile {t} holds {Describe(c.Tile)}"); break;
            }
        }
        if (strict && problem == null && groundFloor < MinGround(l.Width))
            Bad("too little ground under the floor", $"only {groundFloor} of the {l.Width} floor tiles rest on ground (needs {MinGround(l.Width)})");
        foreach (var t in l.DoorTiles)
        {
            var c = cell(t.X, t.Y);
            if (IsBump(l, t, c)) bumps.Add(t);
            else if (c.Tile == SiteTile.Platform && c.Removable) platforms.Add(t);
            else if (c.Tile != SiteTile.Air) Bad($"door spot not empty ({Short(c)})", $"door spot {t} isn't empty ({Describe(c.Tile)})");
        }
        foreach (var t in l.InteriorTiles())
        {
            var c = cell(t.X, t.Y);
            if (IsBump(l, t, c)) bumps.Add(t);
            else if (c.Tile == SiteTile.Platform && c.Removable) platforms.Add(t);
            else if (c.Tile != SiteTile.Air)
            {
                Bad($"inside not empty ({Short(c)})", $"{t} inside the house isn't empty ({Describe(c.Tile)}{(c.Tile == SiteTile.Solid ? BumpHint(l, t, c) : "")})");
                continue;
            }
            if (c.Wall == WallState.Other) { Bad("natural background wall inside", $"{t} has a natural background wall (not valid for housing; hammer it off or pick another spot)"); continue; }
            if (c.Wall == WallState.None) walls.Add(t);
        }

        if (BlockedDoor(l, rawCell) is { } blocked)
            Bad("would wall off another house's door", $"the house would wall off the door at {blocked} (another house's): leave at least {DoorApproach} free columns in front of it " +
                        $"(x {(blocked.X < l.Left ? $">= {blocked.X + DoorApproach + 1}" : $"<= {blocked.X - DoorApproach - l.Width}")})");

        bool canExit = ExitClear(l, (x, y) => cell(x, y));
        if (strict && !canExit) Bad("no room to stand outside the door", $"no room to stand outside the door at {l.OutsideStand}");
        int work = floorFill + 2 * bumps.Count + clear.Count + flatten.Count + 2 * replace.Count + platforms.Count;
        return new SiteCheck(problem, frame, walls, canExit, clear, bumps, flatten, work, replace, platforms, kind);
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

    /// <summary>An existing door within <see cref="DoorApproach"/> columns beside the house whose rows the house
    /// covers (so the house would fill the space in front of it), or null.</summary>
    public static TilePoint? BlockedDoor(HouseLayout l, Func<int, int, SiteCell> cell)
    {
        for (int d = 1; d <= DoorApproach; d++)
            foreach (int x in new[] { l.Left - d, l.Right + d })
                for (int y = l.CeilingY; y <= l.FloorY; y++)
                    if (cell(x, y).Door) return new TilePoint(x, y);
        return null;
    }

    /// <summary>
    /// Door side for a house at a given spot when the caller didn't choose one: a side with room to stand outside
    /// (<see cref="ExitClear"/>: a 2-wide, 3-high clear body on ground) first; between two such sides (or two without),
    /// the one facing the player (<paramref name="player"/> = feet tile), so the door opens toward the way the player
    /// came. Decoration outside counts as open (it is broken when in the way).
    /// </summary>
    public static DoorSide DefaultDoor(int x, int floorY, int width, int height, TilePoint player, Func<int, int, SiteCell> cell)
    {
        var facing = player.X + 1 < x + width / 2f ? DoorSide.Left : DoorSide.Right;
        var other = facing == DoorSide.Left ? DoorSide.Right : DoorSide.Left;
        SiteCell open(int cx, int cy) => cell(cx, cy) is { Tile: SiteTile.Decor } c ? c with { Tile = SiteTile.Air } : cell(cx, cy);
        if (ExitClear(new HouseLayout(x, floorY, width, height, facing), open)) return facing;
        if (ExitClear(new HouseLayout(x, floorY, width, height, other), open)) return other;
        return facing;
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
    /// <param name="report">Filled with why candidates were rejected (for the failure message).</param>
    public static (HouseLayout Layout, SiteCheck Check)? FindWithCheck(TilePoint player, int width, int height,
        Func<int, int, SiteCell> cell, int radius = 30, SiteSearchReport? report = null)
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
                if (!FloorCorner(x, floorY, cached) || !FloorCorner(x + width - 1, floorY, cached)
                    || !OpenAbove(cached(x + 1, floorY - MaxBumpHeight - 1))
                    || !OpenAbove(cached(x + width - 2, floorY - MaxBumpHeight - 1)))
                {
                    if (report != null) report.PreSkipped++;
                    continue;
                }
                bool playerLeft = player.X + 1 < x + width / 2f;
                foreach (var door in playerLeft ? new[] { DoorSide.Left, DoorSide.Right } : new[] { DoorSide.Right, DoorSide.Left })
                {
                    var l = new HouseLayout(x, floorY, width, height, door);
                    int dist = Math.Abs(l.OutsideStand.X - player.X) + Math.Abs(l.FloorY - 1 - player.Y);
                    if (dist >= bestCost) continue;
                    var c = Check(l, cached, strict: true);
                    if (!c.Ok) { report?.Reject(c); continue; }
                    int cost = dist + 2 * c.Work;
                    if (cost >= bestCost) continue;
                    best = (l, c);
                    bestCost = cost;
                }
            }
        return best;
    }

    /// <summary>Pre-check for a floor corner: ground, or a gap with ground within <see cref="MaxDipDepth"/> below.</summary>
    private static bool FloorCorner(int x, int y, Func<int, int, SiteCell> cell)
    {
        var c = cell(x, y).Tile;
        if (c == SiteTile.Solid) return true;
        if (c != SiteTile.Air) return false;
        for (int d = 1; d <= MaxDipDepth; d++)
            if (cell(x, y + d).Tile == SiteTile.Solid) return true;
        return false;
    }

    /// <summary>Open enough above the bumps for the pre-check: air, decoration or a platform that can be mined.</summary>
    private static bool OpenAbove(SiteCell c) => c.Tile is SiteTile.Air or SiteTile.Decor || (c.Tile == SiteTile.Platform && c.Removable);

    /// <summary>Coordinate-free tile description for counting rejection reasons.</summary>
    public static string Short(SiteCell c) => c.Tile switch
    {
        SiteTile.Air => "nothing",
        SiteTile.Solid => "a block",
        SiteTile.Platform => c.Removable ? "a platform" : "a protected platform",
        SiteTile.Tree => "a tree",
        SiteTile.Liquid => "liquid",
        SiteTile.Object => "furniture/object",
        SiteTile.Unknown => "unexplored/not visible",
        SiteTile.NpcHouse => "a town NPC's house",
        var t => t.ToString(),
    };

    /// <summary>
    /// Where to place a Work Bench (2×1, aimed at its left tile) next to the house so build_house can craft there:
    /// both tiles empty air, full solid blocks (not platforms, not shaped) under both, outside the house and at least
    /// one column away from its walls, not in front of the door (the exit spot and <see cref="DoorApproach"/> columns
    /// beyond it stay free). The far side from the door first, then nearest to the house's floor row. Null if none
    /// within <paramref name="maxOut"/> columns.
    /// </summary>
    public static TilePoint? BenchSpot(HouseLayout l, Func<int, int, SiteCell> cell, int maxOut = 8)
    {
        TilePoint? best = null;
        int bestCost = int.MaxValue;
        int doorReachEnd = l.Door == DoorSide.Left ? l.Left - 2 - DoorApproach : l.Right + 2 + DoorApproach; // inclusive
        for (int y = l.FloorY - 4; y <= l.FloorY + 3; y++)
            foreach (int side in new[] { -1, 1 })
                for (int d = 2; d <= maxOut; d++)
                {
                    // Left tile of the bench; the bench covers x..x+1 and keeps a free column next to the wall.
                    int x = side < 0 ? l.Left - d - 1 : l.Right + d;
                    bool doorSide = (side < 0) == (l.Door == DoorSide.Left);
                    if (doorSide && (side < 0 ? x + 1 >= doorReachEnd : x <= doorReachEnd)) continue;
                    if (cell(x, y).Tile != SiteTile.Air || cell(x + 1, y).Tile != SiteTile.Air) continue;
                    if (!FullBlock(cell(x, y + 1)) || !FullBlock(cell(x + 1, y + 1))) continue;
                    int cost = (doorSide ? 100 : 0) + d + 2 * Math.Abs(y - (l.FloorY - 1));
                    if (cost < bestCost) { best = new TilePoint(x, y); bestCost = cost; }
                }
        return best;
    }

    private static bool FullBlock(SiteCell c) => c.Tile == SiteTile.Solid && !c.Shaped;

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
