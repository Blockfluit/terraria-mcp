using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Build;

/// <summary>
/// Pure planning for <c>build_platforms</c>: which tiles, in what order, and where to stand.
/// <list type="bullet">
/// <item>Rows: <c>rows</c> horizontal rows from x1 to x2, the first at y, then y - spacing, y - 2·spacing, … (upward),
/// built bottom row first so a finished row can serve as floor for reaching the next.</item>
/// <item>Within a row: a sweep from the end nearest the player towards the other end, so the player walks along
/// (and on) what it just built.</item>
/// <item>Vanilla only places a block or platform next to an existing tile or wall (its "anchor"), so the next tile is
/// the first one in sweep order that is anchored now (<see cref="NextIndex"/>). Placing it anchors its neighbours,
/// so a row grows from wherever it touches something.</item>
/// <item>Standing spot (<see cref="BestStand"/>): among standable spots that reach the next tile, the one that also
/// reaches the most of the following tiles (fewer stops), then the closest to the player.</item>
/// </list>
/// </summary>
public static class PlatformPlan
{
    public const int MaxTiles = 200;
    public const int MaxRows = 20;
    public const int MaxSpacing = 50;

    /// <summary>Tiles in the plan (|x2 - x1| + 1) × rows, for the size check.</summary>
    public static long TileCount(int x1, int x2, int rows) => (Math.Abs((long)x2 - x1) + 1) * Math.Max(0, rows);

    /// <summary>Row y-coordinates, bottom first: y, y - spacing, …</summary>
    public static List<int> RowYs(int y, int rows, int spacing)
    {
        var list = new List<int>(rows);
        for (int i = 0; i < rows; i++) list.Add(y - i * spacing);
        return list;
    }

    /// <summary>
    /// The tiles of one row in sweep order: starting at the end nearer the player's body centre (playerX + 1.0) and
    /// running to the other end. Equal distance: left to right.
    /// </summary>
    public static List<TilePoint> SweepRow(int x1, int x2, int y, int playerX)
    {
        int a = Math.Min(x1, x2), b = Math.Max(x1, x2);
        float centre = playerX + 1f;
        bool fromRight = Math.Abs(b + 0.5f - centre) < Math.Abs(a + 0.5f - centre);
        var list = new List<TilePoint>(b - a + 1);
        if (fromRight)
            for (int x = b; x >= a; x--) list.Add(new TilePoint(x, y));
        else
            for (int x = a; x <= b; x++) list.Add(new TilePoint(x, y));
        return list;
    }

    /// <summary>Index of the first tile in <paramref name="remaining"/> (sweep order) that can be placed now
    /// (<paramref name="anchored"/>), or -1 if none can.</summary>
    public static int NextIndex(IReadOnlyList<TilePoint> remaining, Func<TilePoint, bool> anchored)
    {
        for (int i = 0; i < remaining.Count; i++)
            if (anchored(remaining[i])) return i;
        return -1;
    }

    /// <summary>How many tiles of <paramref name="upcoming"/>, from the first, are reachable from <paramref name="stand"/>
    /// in a row (stops at the first one out of reach, or one inside the body when <paramref name="solid"/>).</summary>
    public static int Coverage(TilePoint stand, IReadOnlyList<TilePoint> upcoming, ReachSpec reach, bool solid, float margin = 0f)
    {
        int n = 0;
        foreach (var t in upcoming)
        {
            if (!Reach.FromStand(stand, t, reach, margin) || (solid && InBody(stand, t))) break;
            n++;
        }
        return n;
    }

    /// <summary>Is tile <paramref name="t"/> one of the 2×3 cells of a body standing at <paramref name="stand"/>?</summary>
    public static bool InBody(TilePoint stand, TilePoint t) =>
        t.X >= stand.X && t.X <= stand.X + 1 && t.Y <= stand.Y && t.Y >= stand.Y - 2;

    /// <summary>
    /// Where to stand to place <c>upcoming[0]</c> and as many of the following tiles as possible without moving.
    /// Candidates: standable spots (<see cref="Pathfinder.CanStand"/>) within <paramref name="searchRadius"/> of the
    /// target that reach it with <paramref name="margin"/> to spare; a solid block can't be placed inside the body,
    /// so for <paramref name="solid"/> items the spot's body must not cover the target. Best = feet above the target's
    /// row first (standing on the row or higher ground, not down in the pit the bridge spans), then the highest
    /// <see cref="Coverage"/> (capped at <paramref name="coverageCap"/> so a far spot isn't preferred for a
    /// marginal gain), then the smallest Manhattan distance from <paramref name="player"/>, then the leftmost/topmost.
    /// Null if no spot qualifies (e.g. a bridge whose start hangs over a chasm: walk there with building allowed).
    /// </summary>
    public static TilePoint? BestStand(ITileGrid grid, TilePoint player, IReadOnlyList<TilePoint> upcoming, ReachSpec reach,
        bool solid, float margin = 1f, int searchRadius = 8, int coverageCap = 6)
    {
        if (upcoming.Count == 0) return null;
        var target = upcoming[0];
        TilePoint? best = null;
        int bestCover = -1, bestDist = int.MaxValue;
        bool bestAbove = false;
        for (int y = target.Y - searchRadius; y <= target.Y + searchRadius; y++)
            for (int x = target.X - searchRadius; x <= target.X + searchRadius; x++)
            {
                var s = new TilePoint(x, y);
                if (solid && InBody(s, target)) continue;
                if (!Reach.FromStand(s, target, reach, margin) || !Pathfinder.CanStand(grid, s)) continue;
                bool above = y < target.Y;
                int cover = Math.Min(coverageCap, Coverage(s, upcoming, reach, solid, margin));
                int dist = Math.Abs(x - player.X) + Math.Abs(y - player.Y);
                if (best != null)
                {
                    if (above != bestAbove) { if (!above) continue; }
                    else if (cover < bestCover || (cover == bestCover && dist >= bestDist)) continue;
                }
                best = s;
                bestAbove = above;
                bestCover = cover;
                bestDist = dist;
            }
        return best;
    }
}
