using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>
/// Water Walking (wiki: Water Walking Potion, Water Walking Boots): vanilla's Collision.WaterCollision stops a falling
/// player on the top of the topmost liquid cell (the cell above holds no liquid) like a platform, unless Down is held
/// (holding Down sinks through). <c>waterWalk2</c> (boots) walks on water and honey, <c>waterWalk</c> (potion, Lava
/// Waders) on lava too. Shimmer is a Hazard in the grid and never walkable here.
/// </summary>
public static class LiquidWalk
{
    /// <summary>Safety margin on the buff time (ticks) on top of <see cref="PaceSlack"/>.</summary>
    public const int MarginTicks = 120;
    /// <summary>The travel-time estimate assumes this fraction of the run speed (acceleration, jumps, stops).</summary>
    public const float PaceSlack = 0.75f;

    public static bool Walkable(TileKind k, bool water, bool lava) =>
        (water && k is TileKind.Water or TileKind.Honey) || (lava && k == TileKind.Lava);

    public static bool Walkable(TileKind k, MovementProfile p) => Walkable(k, p.WaterWalk, p.LavaWalk);

    /// <summary>Liquid cell (x, y) is walkable and its top is the liquid's surface: the cell above is open (air or a
    /// platform; not liquid, a block or unknown).</summary>
    public static bool IsSurface(ITileGrid g, int x, int y, bool water, bool lava) =>
        Walkable(g.Get(x, y).Kind, water, lava) && g.Get(x, y - 1).Kind is TileKind.Air or TileKind.Platform;

    public static bool IsSurface(ITileGrid g, int x, int y, MovementProfile p) =>
        (p.WaterWalk || p.LavaWalk) && IsSurface(g, x, y, p.WaterWalk, p.LavaWalk);

    /// <summary>The tile holds the feet: a block or platform, or a walkable liquid surface.</summary>
    public static bool Holds(ITileGrid g, int x, int y, bool water, bool lava) =>
        g.Get(x, y).IsStandable || ((water || lava) && IsSurface(g, x, y, water, lava));

    /// <summary>Ticks of travel from now the planner may still spend standing on a liquid surface (int.MaxValue:
    /// unlimited, or no water walking at all).</summary>
    public static int UsableTicks(MovementProfile p) =>
        p.WaterWalkTicks == int.MaxValue ? int.MaxValue : Math.Max(0, p.WaterWalkTicks - MarginTicks);

    /// <summary>Estimated ticks to cover one tile at the profile's run speed, with <see cref="PaceSlack"/>.</summary>
    public static float TicksPerTile(MovementProfile p) =>
        PlayerPhysics.TilePx / (Math.Max(0.5f, p.MaxRunSpeed) * PaceSlack);
}
