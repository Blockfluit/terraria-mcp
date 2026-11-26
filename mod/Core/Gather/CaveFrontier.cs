using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Gather;

/// <summary>Where explore_caves walks next: <see cref="Stand"/> is a standable, known spot from which the
/// <see cref="Frontier"/> cell (known open space touching unknown tiles) is within view. <see cref="Distance"/> = cost
/// of getting there (open steps, plus digging through thin walls); <see cref="Unknown"/> = unknown tiles within 2 of
/// the frontier cell; <see cref="FrontiersInView"/> = frontier cells within <see cref="CaveFrontier.ViewRadius"/>.</summary>
public sealed record FrontierPick(TilePoint Stand, TilePoint Frontier, int Distance, int Unknown, int FrontiersInView = 0);

/// <summary>
/// Frontier-based cave exploration over the fair-vision grid (pure logic):
/// <list type="bullet">
/// <item>Reach: a Dijkstra flood over known open cells (air, platforms, water) 4-connected from the player's body,
/// within <c>radius</c> (Chebyshev) of the exploration's origin and a node budget. Lava, hazards (spikes), honey and
/// Unknown cells aren't entered. A wall at most <see cref="MaxDigRun"/> tiles thick (diggable with
/// <c>pickPower</c>, not protected) between two open cells is crossed at <see cref="DigCost"/> per tile: caves
/// separated by a thin wall count as connected (move_to digs it), solid rock beyond that doesn't.</item>
/// <item>A frontier cell is a reached open cell with an Unknown 4-neighbour and at least <see cref="MinUnknown"/>
/// Unknown cells within 2 tiles (one dark corner isn't worth a walk), not within <see cref="LavaMargin"/> of lava.
/// Unknown tiles inside rock never touch open space, so they aren't frontiers.</item>
/// <item>Stand candidates: reached cells where the 2x3 body fits on a floor, every body/floor tile is known, the
/// feet aren't in water, no lava within 2 tiles, and some frontier cell lies within <see cref="ViewRadius"/>. The
/// frontier's own floor needn't be known: a dark cave whose floor is unknown is explored from a known ledge next to
/// it (the caller lights the frontier with a torch from there). Stands within <see cref="MinDistance"/> of the player
/// or rejected by the caller (tried and failed, already visited) are skipped.</item>
/// <item>Score (lowest wins): reach cost + 2 x Manhattan distance to the nearest frontier cell − a small bonus for
/// many frontier cells in view. So it walks right up to the frontier, nearest frontier first.</item>
/// </list>
/// </summary>
public static class CaveFrontier
{
    public const int MinUnknown = 3, MinDistance = 4, LavaMargin = 3, ViewRadius = 8;
    public const int MaxDigRun = 3, DigCost = 4, DefaultMaxNodes = 20000, MaxFrontierCells = 1500;

    /// <summary>Known cells the player can move through.</summary>
    public static bool IsOpen(TileInfo t) => t.Kind is TileKind.Air or TileKind.Platform or TileKind.Water;

    public static FrontierPick? Pick(ITileGrid g, TilePoint here, TilePoint origin, int radius,
        Func<TilePoint, bool>? rejected = null, int pickPower = 0, int maxNodes = DefaultMaxNodes)
    {
        var dist = Flood(g, here, origin, radius, pickPower, maxNodes);
        var frontiers = Frontiers(g, dist);
        if (frontiers.Count == 0) return null;

        // Nearest frontier cell (Manhattan) and how many are in view, for every cell within ViewRadius of one.
        var near = new Dictionary<TilePoint, (int D, TilePoint F, int Count)>();
        foreach (var (f, _) in frontiers)
            for (int dy = -ViewRadius; dy <= ViewRadius; dy++)
                for (int dx = -ViewRadius; dx <= ViewRadius; dx++)
                {
                    var p = new TilePoint(f.X + dx, f.Y + dy);
                    int md = Math.Abs(dx) + Math.Abs(dy);
                    near[p] = near.TryGetValue(p, out var e)
                        ? (md < e.D ? md : e.D, md < e.D ? f : e.F, e.Count + 1)
                        : (md, f, 1);
                }

        var unknownAt = frontiers.ToDictionary(f => f.Cell, f => f.Unknown);
        FrontierPick? best = null;
        int bestScore = int.MaxValue;
        foreach (var (s, d) in dist)
        {
            if (!near.TryGetValue(s, out var e)) continue;
            int score = d + 2 * e.D - Math.Min(e.Count, 20) / 4;
            if (score > bestScore || (score == bestScore && best != null && d >= best.Distance)) continue;
            if (!IsStand(g, s) || Chebyshev(s, here) < MinDistance) continue;
            if (rejected != null && rejected(s)) continue;
            bestScore = score;
            best = new FrontierPick(s, e.F, d, unknownAt[e.F], e.Count);
        }
        return best;
    }

    /// <summary>The reachable frontier cell nearest to the player (by reach cost), or null: where to aim a torch when
    /// there's nowhere to walk.</summary>
    public static TilePoint? NearestFrontier(ITileGrid g, TilePoint here, TilePoint origin, int radius, int pickPower = 0,
        int maxNodes = DefaultMaxNodes)
    {
        var dist = Flood(g, here, origin, radius, pickPower, maxNodes);
        var f = Frontiers(g, dist);
        return f.Count == 0 ? null : f.MinBy(x => dist[x.Cell]).Cell;
    }

    /// <summary>Frontier cells among the reached ones (nearest first, at most <see cref="MaxFrontierCells"/>), each
    /// with its Unknown count within 2 tiles.</summary>
    public static List<(TilePoint Cell, int Unknown)> Frontiers(ITileGrid g, Dictionary<TilePoint, int> dist)
    {
        var list = new List<(TilePoint, int)>();
        foreach (var c in dist.OrderBy(kv => kv.Value).Select(kv => kv.Key))
        {
            if (!TouchesUnknown(g, c)) continue;
            int unknown = UnknownAround(g, c);
            if (unknown < MinUnknown || LavaWithin(g, c, LavaMargin)) continue;
            list.Add((c, unknown));
            if (list.Count >= MaxFrontierCells) break;
        }
        return list;
    }

    /// <summary>Reach cost from the player's body cells to every reachable open cell (see the class summary).</summary>
    public static Dictionary<TilePoint, int> Flood(ITileGrid g, TilePoint here, TilePoint origin, int radius,
        int pickPower = 0, int maxNodes = DefaultMaxNodes)
    {
        var dist = new Dictionary<TilePoint, int>();
        var done = new HashSet<TilePoint>();
        var queue = new PriorityQueue<TilePoint, int>();
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                var b = new TilePoint(here.X + dx, here.Y - dy);
                if (IsOpen(g.Get(b)) && dist.TryAdd(b, 0)) queue.Enqueue(b, 0);
            }
        while (queue.TryDequeue(out var c, out int dc))
        {
            if (dc != dist[c] || !done.Add(c)) continue;
            if (done.Count >= maxNodes) break;
            foreach (var dir in Dirs)
            {
                // Open neighbour, or the open cell right behind a thin diggable wall.
                for (int k = 0; k <= MaxDigRun; k++)
                {
                    var n = new TilePoint(c.X + dir.X * (k + 1), c.Y + dir.Y * (k + 1));
                    if (Chebyshev(n, origin) > radius) break;
                    var t = g.Get(n);
                    if (IsOpen(t))
                    {
                        int nd = dc + k + 1 + k * DigCost;
                        if (!dist.TryGetValue(n, out int old) || nd < old)
                        {
                            dist[n] = nd;
                            queue.Enqueue(n, nd);
                        }
                        break;
                    }
                    if (k == MaxDigRun || !Diggable(t, pickPower)) break;
                }
            }
        }
        // Cells queued but never expanded (budget hit) still count as reached.
        return dist;
    }

    private static readonly TilePoint[] Dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

    private static bool Diggable(TileInfo t, int pickPower) =>
        pickPower > 0 && t.Kind == TileKind.Solid && !t.Protected && !t.IsClosedDoor && t.PickPowerRequired <= pickPower;

    /// <summary>The body fits on a floor here, everything around it is known, no water at the feet, no lava near.</summary>
    public static bool IsStand(ITileGrid g, TilePoint s) =>
        Pathfinder.CanStand(g, s) && AllKnown(g, s) && g.Get(s).Kind != TileKind.Water
        && !LavaWithin(g, new TilePoint(s.X, s.Y - 1), 2) && !LavaWithin(g, new TilePoint(s.X + 1, s.Y + 1), 2);

    private static bool AllKnown(ITileGrid g, TilePoint s)
    {
        for (int dy = -1; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (g.Get(s.X + dx, s.Y - dy).Kind == TileKind.Unknown) return false;
        return true;
    }

    private static bool TouchesUnknown(ITileGrid g, TilePoint c)
    {
        foreach (var d in Dirs)
            if (g.Get(c.X + d.X, c.Y + d.Y).Kind == TileKind.Unknown) return true;
        return false;
    }

    private static int UnknownAround(ITileGrid g, TilePoint c)
    {
        int n = 0;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (g.Get(c.X + dx, c.Y + dy).Kind == TileKind.Unknown) n++;
        return n;
    }

    public static bool LavaWithin(ITileGrid g, TilePoint c, int r)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (g.Get(c.X + dx, c.Y + dy).Kind == TileKind.Lava) return true;
        return false;
    }

    public static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
