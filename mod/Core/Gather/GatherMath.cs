using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Gather;

/// <summary>Tool reach: <c>Player.tileRangeX/Y</c> (vanilla 5/4) plus the held tool's <c>Item.tileBoost</c>.</summary>
public readonly record struct ReachSpec(int RangeX, int RangeY, int TileBoost);

/// <summary>Can the player's tool reach a tile? Mirrors vanilla <c>Player.IsTargetTileInItemRange</c> (1.4.4).</summary>
public static class Reach
{
    private const float HitboxHeight = PlayerPhysics.HitboxHeightPx / (float)PlayerPhysics.TilePx;

    /// <summary>
    /// Exact vanilla check for a hitbox given in tile units (left/top edge, width/height):
    /// <c>left - rx - boost ≤ tx ≤ right + rx + boost - 1</c> and <c>top - ry - boost ≤ ty ≤ bottom + ry + boost - 2</c>.
    /// <paramref name="margin"/> (tiles) shrinks the reach on every side, to tolerate imprecise stopping.
    /// </summary>
    public static bool InRange(float left, float top, float width, float height, TilePoint target, ReachSpec r, float margin = 0f)
    {
        float rx = r.RangeX + r.TileBoost - margin, ry = r.RangeY + r.TileBoost - margin;
        return left - rx <= target.X
            && left + width + rx - 1f >= target.X
            && top - ry <= target.Y
            && top + height + ry - 2f >= target.Y;
    }

    /// <summary>Reach from a standing position (feet-left tile) with the hitbox centred in the 2-wide body.</summary>
    public static bool FromStand(TilePoint stand, TilePoint target, ReachSpec r, float margin = 0f) =>
        InRange(stand.X + PlayerPhysics.CenterOffset, stand.Y + 1f - HitboxHeight, PlayerPhysics.HitboxWidth, HitboxHeight,
            target, r, margin);
}

/// <summary>
/// Order in which <c>mine_area</c> breaks tiles: <b>top row first</b>, and within a row the column nearest the player's
/// body centre first (ties left to right).
/// Why top-down: tiles above never rest on the player (sand/gravel above a mined tile falls into the
/// gap, and the gap is above the next row being worked, not on the player); the player can't be sealed in by
/// tiles still above it; and if it stands on the area it digs its own floor away row by row and drops in place
/// rather than tunnelling under unmined rock. Nearest-column-first keeps walking short.
/// Where the player stands for each tile is chosen by <see cref="StandFinder"/>, which avoids spots resting only on
/// tiles still to be mined and spots directly under the target.
/// </summary>
public static class MiningOrder
{
    public const int MaxAreaTiles = 200;

    /// <summary>Number of tiles in the rectangle with corners (x1,y1),(x2,y2), inclusive (any corner order).</summary>
    public static long RectCount(int x1, int y1, int x2, int y2) =>
        (Math.Abs((long)x2 - x1) + 1) * (Math.Abs((long)y2 - y1) + 1);

    /// <summary>All tiles of the inclusive rectangle (any corner order), row by row. Check <see cref="RectCount"/> first.</summary>
    public static List<TilePoint> Rect(int x1, int y1, int x2, int y2)
    {
        int xa = Math.Min(x1, x2), xb = Math.Max(x1, x2), ya = Math.Min(y1, y2), yb = Math.Max(y1, y2);
        var list = new List<TilePoint>();
        for (int y = ya; y <= yb; y++)
            for (int x = xa; x <= xb; x++)
                list.Add(new TilePoint(x, y));
        return list;
    }

    /// <summary>Distinct tiles, top row first, then by distance from the body centre column (player.X + 1.0).</summary>
    public static List<TilePoint> Order(IEnumerable<TilePoint> tiles, TilePoint player)
    {
        float centre = player.X + 1f;
        return tiles.Distinct()
            .OrderBy(t => t.Y)
            .ThenBy(t => Math.Abs(t.X + 0.5f - centre))
            .ThenBy(t => t.X)
            .ToList();
    }
}

/// <summary>Where to stand to work on a tile.</summary>
public static class StandFinder
{
    private const int PendingSupportPenalty = 100, BelowTargetPenalty = 50, BuiltSupportPenalty = 20;

    /// <summary>Body clear, plain air under the left foot, and vanilla would let a platform go there (a tile on one of
    /// its 4 sides or a background wall).</summary>
    private static bool CanBuildUnder(ITileGrid grid, TilePoint s)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (grid.Get(s.X + dx, s.Y - dy).BlocksBody) return false;
        var under = new TilePoint(s.X, s.Y + 1);
        var u = grid.Get(under);
        if (u.Kind != TileKind.Air) return false;
        // Vanilla anchoring: a tile on one of the 4 sides, or a background wall behind it.
        return u.Wall || grid.Get(under.X - 1, under.Y).IsStandable || grid.Get(under.X + 1, under.Y).IsStandable
               || grid.Get(under.X, under.Y + 1).IsStandable;
    }

    /// <summary>
    /// The standable position (body clear, supported: <see cref="Pathfinder.CanStand"/>) within
    /// <paramref name="searchRadius"/> of the target that reaches it with <paramref name="margin"/> to spare, cheapest
    /// first. Cost = Manhattan distance from the player, +100 if every support tile under it is one still to be mined
    /// (<paramref name="pending"/> or the target: it would drop out from under the player), +50 if the target is
    /// straight above the body (falling sand/debris). Null if nothing qualifies.
    /// </summary>
    /// <param name="buildSupport">Also accept spots with nothing underfoot when a platform can be placed there (plain
    /// air under the left foot next to a solid tile or platform): move_to builds it. Costs extra; use as a fallback.</param>
    public static TilePoint? Best(ITileGrid grid, TilePoint player, TilePoint target, ReachSpec reach,
        IReadOnlySet<TilePoint>? pending = null, float margin = 1f, int searchRadius = 8, bool buildSupport = false)
    {
        TilePoint? best = null;
        int bestCost = int.MaxValue;
        for (int y = target.Y - searchRadius; y <= target.Y + searchRadius; y++)
            for (int x = target.X - searchRadius; x <= target.X + searchRadius; x++)
            {
                var s = new TilePoint(x, y);
                if (!Reach.FromStand(s, target, reach, margin)) continue;
                bool built = false;
                if (!Pathfinder.CanStand(grid, s))
                {
                    if (!buildSupport || !CanBuildUnder(grid, s)) continue;
                    built = true;
                }
                int cost = Math.Abs(x - player.X) + Math.Abs(y - player.Y) + (built ? BuiltSupportPenalty : 0);
                if (RestsOnlyOnPending(grid, s, target, pending)) cost += PendingSupportPenalty;
                if (target.X >= x && target.X <= x + 1 && target.Y < y - 2) cost += BelowTargetPenalty;
                if (cost >= bestCost) continue;
                bestCost = cost;
                best = s;
            }
        return best;
    }

    private static bool RestsOnlyOnPending(ITileGrid grid, TilePoint s, TilePoint target, IReadOnlySet<TilePoint>? pending)
    {
        bool any = false;
        for (int dx = 0; dx < 2; dx++)
        {
            var t = new TilePoint(s.X + dx, s.Y + 1);
            if (!grid.Get(t).IsStandable) continue;
            if (t != target && (pending == null || !pending.Contains(t))) return false;
            any = true;
        }
        return any;
    }
}

/// <summary>Tree structure over a tree-tile predicate (trunk column, branches beside it over air, roots beside the base).</summary>
public static class TreeMath
{
    /// <summary>
    /// The trunk's bottom tile for any tile of the tree (trunk, top, branch or root); null if <paramref name="at"/> isn't
    /// a tree tile. Descends the column; a tile with no tree tile above it that has a tree neighbour with a tree tile
    /// above (a branch or root) hands over to that neighbour's column.
    /// </summary>
    public static TilePoint? FindBase(Func<int, int, bool> isTree, TilePoint at, int maxSteps = 256)
    {
        if (!isTree(at.X, at.Y)) return null;
        int x = at.X, y = at.Y;
        for (int guard = 0; guard < maxSteps; guard++)
        {
            while (isTree(x, y + 1) && guard++ < maxSteps) y++;
            if (isTree(x, y - 1)) return new TilePoint(x, y); // a trunk column: this is its bottom
            if (isTree(x - 1, y) && isTree(x - 1, y - 1)) x--;
            else if (isTree(x + 1, y) && isTree(x + 1, y - 1)) x++;
            else return new TilePoint(x, y); // lone tile (stump)
        }
        return new TilePoint(x, y);
    }
}

/// <summary>Item pickup range (vanilla <c>Player.GrabItems</c>): items whose hitbox overlaps the player's hitbox
/// grown by the grab range on every side get pulled in.</summary>
public static class GrabMath
{
    /// <summary>All in pixels (integers, as vanilla's Rectangle). Strict overlap, like XNA <c>Rectangle.Intersects</c>.</summary>
    public static bool InGrabRange(int pLeft, int pTop, int pWidth, int pHeight, int grabRange,
        int iLeft, int iTop, int iWidth, int iHeight)
    {
        int l = pLeft - grabRange, t = pTop - grabRange, r = pLeft + pWidth + grabRange, b = pTop + pHeight + grabRange;
        return l < iLeft + iWidth && iLeft < r && t < iTop + iHeight && iTop < b;
    }
}
