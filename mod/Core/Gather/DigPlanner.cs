using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Gather;

/// <summary>Why a dig plan stops where it does.</summary>
public enum DigStopKind
{
    /// <summary>A tile the plan must see (to dig, to stand next to, or to fall past) is Unknown: place a torch.</summary>
    Dark,
    Lava,
    /// <summary>Water, honey or shimmer that would flood the hole.</summary>
    Liquid,
    /// <summary>Spikes or another touch-damage tile in the way.</summary>
    Hazard,
    Protected,
    /// <summary>The best pickaxe is too weak (<see cref="DigStop.Need"/> = pick power needed).</summary>
    TooHard,
    /// <summary>Can't be broken by a pickaxe at all (or outside the world).</summary>
    Unbreakable,
    /// <summary>Digging the next row would open a fall longer than the safe fall distance.</summary>
    Drop,
    /// <summary>A tunnel column has no floor to walk on.</summary>
    NoFloor,
}

public sealed record DigStop(DigStopKind Kind, TilePoint At, int Need = 0)
{
    /// <summary>Human-readable reason, e.g. "lava below at (10,20)".</summary>
    public string Describe(int pickPower) => Kind switch
    {
        DigStopKind.Dark => $"too dark to see {At}",
        DigStopKind.Lava => $"lava {Where} at {At}",
        DigStopKind.Liquid => $"liquid {Where} at {At} would flood the hole",
        DigStopKind.Hazard => $"hazard (spikes) at {At}",
        DigStopKind.Protected => $"protected tile at {At}",
        DigStopKind.TooHard => $"needs pick power {Need} at {At} (best pickaxe has {pickPower})",
        DigStopKind.Unbreakable => $"unbreakable tile at {At}",
        DigStopKind.Drop => $"digging on would drop {Need} tiles at {At} (more than a safe fall)",
        DigStopKind.NoFloor => $"no floor to walk on at {At}",
        _ => Kind.ToString(),
    };

    /// <summary>"below"/"ahead"/"next to the hole", set by the planner for liquid stops.</summary>
    public string Where { get; init; } = "next to the hole";
}

/// <summary>
/// Tiles to break, in order, standing at <see cref="Stand"/> (the player walks there first if it isn't where it
/// stands). <see cref="Stop"/>: why the plan ends early (null = it reached the requested size). A plan can have both
/// tiles and a stop: dig the tiles, the next plan reports the stop.
/// </summary>
public sealed record DigPlan(TilePoint Stand, IReadOnlyList<TilePoint> Tiles, DigStop? Stop)
{
    public bool HasWork => Tiles.Count > 0;
}

/// <summary>
/// Plans explore's digging: a 2-wide shaft straight down under the body, or a 3-tall tunnel sideways. Pure logic over
/// the fair-vision grid: only tiles the player can see are dug, and the plan stops (with a reason) before
/// <list type="bullet">
/// <item>an Unknown tile it would dig, stand beside, or fall past (Dark: place a torch and plan again),</item>
/// <item>lava/water/honey in or right next to the hole (sides, below, above the tunnel, the column ahead),</item>
/// <item>protected, unbreakable or too-hard tiles, spikes,</item>
/// <item>a row whose removal would drop the player further than <c>safeFall</c> (unless it takes no fall damage).</item>
/// </list>
/// Why a straight shaft and not a staircase: it digs half the tiles per depth, the player drops one row per row dug
/// (no fall damage), and <c>move_to</c> climbs back out by building platforms.
/// </summary>
public static class DigPlanner
{
    /// <summary>Rows per shaft chunk / columns per tunnel chunk.</summary>
    public const int ShaftChunk = 4, TunnelChunk = 4;

    /// <summary>
    /// Shaft under <paramref name="here"/>, or under a standable spot up to 2 tiles to the side when the body column
    /// itself can't be dug (protected, too hard, liquid…): the first offset (0, -1, +1, -2, +2) whose plan has work.
    /// Without any, the plan for offset 0 (its Stop says why).
    /// </summary>
    public static DigPlan ShaftDownNear(ITileGrid g, TilePoint here, int maxRows, int pickPower, int safeFall, bool noFallDamage)
    {
        var first = ShaftDown(g, here, maxRows, pickPower, safeFall, noFallDamage);
        if (first.HasWork) return first;
        foreach (int o in new[] { -1, 1, -2, 2 })
        {
            var s = new TilePoint(here.X + o, here.Y);
            if (!Pathfinder.CanStand(g, s) || !Walkable(g, here, s)) continue;
            var plan = ShaftDown(g, s, maxRows, pickPower, safeFall, noFallDamage);
            if (plan.HasWork) return plan;
        }
        return first;
    }

    /// <summary>
    /// Up to <paramref name="maxRows"/> rows of the two body columns below <paramref name="stand"/>, top row first
    /// (within a row: left, right). Each dug row lets the player drop onto the next one.
    /// </summary>
    public static DigPlan ShaftDown(ITileGrid g, TilePoint stand, int maxRows, int pickPower, int safeFall, bool noFallDamage)
    {
        var tiles = new List<TilePoint>();
        int feet = stand.Y;
        for (int r = stand.Y + 1; r <= stand.Y + maxRows; r++)
        {
            var row = new List<TilePoint>(2);
            for (int c = stand.X; c <= stand.X + 1; c++)
            {
                var stop = CheckDig(g, new TilePoint(c, r), pickPower, "below", row);
                if (stop != null) return new DigPlan(stand, tiles, stop);
            }
            foreach (int c in new[] { stand.X - 1, stand.X + 2 })
                if (CheckNeighbour(g, new TilePoint(c, r), "next to the hole") is { } stop) return new DigPlan(stand, tiles, stop);

            // Where the player lands once row r is gone: the first row with support under either column.
            var landing = Landing(g, stand.X, r, feet, safeFall, noFallDamage, out var fallStop);
            if (fallStop != null) return new DigPlan(stand, tiles, fallStop);
            tiles.AddRange(row);
            if (landing > r) break; // broke into an open space: walking takes over from here
            feet = r;
        }
        return new DigPlan(stand, tiles, null);
    }

    /// <summary>
    /// A 3-tall tunnel (body rows stand.Y-2..stand.Y) through the columns ahead in direction <paramref name="sign"/>
    /// (+1 right, -1 left), column by column, top-down. Scans up to <paramref name="scanColumns"/> columns and stops
    /// after <paramref name="maxColumns"/> columns that need digging (already open columns are walked through). Each
    /// column needs a known floor (a 1-wide hole is fine: the body spans two columns), a known ceiling with no liquid
    /// above, and a known column ahead with no liquid.
    /// </summary>
    public static DigPlan Tunnel(ITileGrid g, TilePoint stand, int sign, int maxColumns, int scanColumns, int pickPower)
    {
        var tiles = new List<TilePoint>();
        int dugColumns = 0;
        // Floor under the column behind the next one (at first the body's column on that side): the 2-wide body over
        // columns c and c - sign is held by either, so a 1-wide hole in the floor is walked over (vanilla: the 20 px
        // hitbox is supported by either column). Seen in game: explore stopped 12 times with "no floor to walk on" in
        // front of a single missing tile.
        bool prevFloor = g.Get(sign > 0 ? stand.X + 1 : stand.X, stand.Y + 1).IsStandable;
        for (int i = 0; i < scanColumns && dugColumns < maxColumns; i++)
        {
            int c = sign > 0 ? stand.X + 2 + i : stand.X - 1 - i;
            var col = new List<TilePoint>(3);
            for (int r = stand.Y - 2; r <= stand.Y; r++)
                if (CheckDig(g, new TilePoint(c, r), pickPower, "ahead", col) is { } stop) return new DigPlan(stand, tiles, stop);

            var floor = new TilePoint(c, stand.Y + 1);
            var f = g.Get(floor);
            if (f.Kind == TileKind.Unknown) return new DigPlan(stand, tiles, new DigStop(DigStopKind.Dark, floor));
            if (LiquidStop(f.Kind, floor, "below") is { } ls) return new DigPlan(stand, tiles, ls);
            if (f.Kind == TileKind.Hazard) return new DigPlan(stand, tiles, new DigStop(DigStopKind.Hazard, floor));
            if (!f.IsStandable && !prevFloor) return new DigPlan(stand, tiles, new DigStop(DigStopKind.NoFloor, floor));
            prevFloor = f.IsStandable;

            if (CheckNeighbour(g, new TilePoint(c, stand.Y - 3), "above the tunnel") is { } up) return new DigPlan(stand, tiles, up);
            for (int r = stand.Y - 2; r <= stand.Y; r++)
                if (CheckNeighbour(g, new TilePoint(c + sign, r), "ahead") is { } ahead) return new DigPlan(stand, tiles, ahead);

            tiles.AddRange(col);
            if (col.Count > 0) dugColumns++;
        }
        return new DigPlan(stand, tiles, null);
    }

    /// <summary>A tile inside the hole: dig it (added to <paramref name="into"/>), pass it (air), or stop.</summary>
    private static DigStop? CheckDig(ITileGrid g, TilePoint t, int pickPower, string where, List<TilePoint> into)
    {
        var info = g.Get(t);
        switch (info.Kind)
        {
            case TileKind.Unknown: return new DigStop(DigStopKind.Dark, t);
            case TileKind.Air: return null;
            case TileKind.Hazard: return new DigStop(DigStopKind.Hazard, t);
            case TileKind.Lava or TileKind.Water or TileKind.Honey: return LiquidStop(info.Kind, t, where);
        }
        // Solid or platform: break it.
        if (info.Protected) return new DigStop(DigStopKind.Protected, t);
        if (info.PickPowerRequired == int.MaxValue) return new DigStop(DigStopKind.Unbreakable, t);
        if (info.PickPowerRequired > pickPower) return new DigStop(DigStopKind.TooHard, t, info.PickPowerRequired);
        into.Add(t);
        return null;
    }

    /// <summary>A tile beside the hole: must be known and hold no liquid.</summary>
    private static DigStop? CheckNeighbour(ITileGrid g, TilePoint t, string where)
    {
        var k = g.Get(t).Kind;
        if (k == TileKind.Unknown) return new DigStop(DigStopKind.Dark, t);
        return LiquidStop(k, t, where);
    }

    private static DigStop? LiquidStop(TileKind k, TilePoint t, string where) => k switch
    {
        TileKind.Lava => new DigStop(DigStopKind.Lava, t) { Where = where },
        TileKind.Water or TileKind.Honey => new DigStop(DigStopKind.Liquid, t) { Where = where },
        _ => null,
    };

    /// <summary>
    /// Row the feet end on after row <paramref name="r"/> of columns x, x+1 is cleared: r when row r+1 supports either
    /// column, else further down (an open space below). Every tile fallen past and the landing must be known and dry;
    /// the drop from <paramref name="feet"/> must be a safe fall.
    /// </summary>
    private static int Landing(ITileGrid g, int x, int r, int feet, int safeFall, bool noFallDamage, out DigStop? stop)
    {
        stop = null;
        int limit = noFallDamage ? 200 : safeFall + 1;
        for (int land = r; land - feet <= limit; land++)
        {
            bool support = false;
            for (int c = x; c <= x + 1; c++)
            {
                var below = new TilePoint(c, land + 1);
                var k = g.Get(below).Kind;
                if (k == TileKind.Unknown) { stop = new DigStop(DigStopKind.Dark, below); return land; }
                if (LiquidStop(k, below, "below") is { } ls) { stop = ls; return land; }
                if (k == TileKind.Hazard) { stop = new DigStop(DigStopKind.Hazard, below); return land; }
                if (k is TileKind.Solid or TileKind.Platform) support = true;
            }
            if (!support) continue;
            if (!noFallDamage && land - feet > safeFall)
                stop = new DigStop(DigStopKind.Drop, new TilePoint(x, r), land - feet);
            return land;
        }
        stop = new DigStop(DigStopKind.Drop, new TilePoint(x, r), limit);
        return r;
    }

    /// <summary>Same row, every cell between the two stands standable (a flat walk of a tile or two).</summary>
    private static bool Walkable(ITileGrid g, TilePoint a, TilePoint b)
    {
        int step = Math.Sign(b.X - a.X);
        for (int x = a.X + step; x != b.X + step; x += step)
            if (!Pathfinder.CanStand(g, new TilePoint(x, a.Y))) return false;
        return true;
    }
}
