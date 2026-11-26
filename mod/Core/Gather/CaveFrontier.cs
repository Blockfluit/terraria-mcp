using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Gather;

/// <summary>Where explore_caves walks next: <see cref="Stand"/> is a standable spot under the
/// <see cref="Frontier"/> cell (known open space touching unknown tiles). <see cref="Distance"/> = steps through open
/// space from the player; <see cref="Unknown"/> = unknown tiles within 2 of the frontier cell.</summary>
public sealed record FrontierPick(TilePoint Stand, TilePoint Frontier, int Distance, int Unknown);

/// <summary>
/// Frontier-based cave exploration over the fair-vision grid (pure logic):
/// <list type="bullet">
/// <item>Flood-fills known open cells (air, platforms, water) 4-connected from the player's body, within
/// <c>radius</c> (Chebyshev) of the exploration's origin and a node budget. Lava, hazards (spikes), honey, solid and
/// Unknown cells aren't entered, so a frontier is always connected to the player through open space (no mass
/// digging; move_to may still dig a tile or two where that's shorter).</item>
/// <item>A frontier cell is an open cell with an Unknown 4-neighbour and at least <see cref="MinUnknown"/> Unknown
/// cells within 2 tiles (one dark corner isn't worth a walk). Frontiers within <see cref="LavaMargin"/> tiles of
/// known lava are skipped.</item>
/// <item>Its stand: the first spot at or below the frontier (up to <see cref="StandDrop"/> rows; the body may sit at
/// the frontier's column or one left) where the 2x3 body fits on a floor, every body/floor tile is known and no lava
/// lies within 2 tiles. Stands within <see cref="MinDistance"/> of the player (nothing to gain by walking: light a
/// torch instead) or rejected by the caller (tried and failed, already visited) are skipped.</item>
/// <item>Best = fewest open steps, then the most Unknown around it.</item>
/// </list>
/// </summary>
public static class CaveFrontier
{
    public const int MinUnknown = 3, MinDistance = 4, LavaMargin = 3, StandDrop = 8, DefaultMaxNodes = 20000;

    /// <summary>Known cells the player can move through.</summary>
    public static bool IsOpen(TileInfo t) => t.Kind is TileKind.Air or TileKind.Platform or TileKind.Water;

    public static FrontierPick? Pick(ITileGrid g, TilePoint here, TilePoint origin, int radius,
        Func<TilePoint, bool>? rejected = null, int maxNodes = DefaultMaxNodes)
    {
        var dist = Flood(g, here, origin, radius, maxNodes);
        FrontierPick? best = null;
        var standCache = new Dictionary<TilePoint, TilePoint?>();
        foreach (var (c, d) in dist)
        {
            if (best != null && d > best.Distance) continue;
            if (!TouchesUnknown(g, c)) continue;
            int unknown = UnknownAround(g, c);
            if (unknown < MinUnknown) continue;
            if (best != null && d == best.Distance && unknown <= best.Unknown) continue;
            if (LavaWithin(g, c, LavaMargin)) continue;
            if (!standCache.TryGetValue(c, out var stand)) standCache[c] = stand = StandFor(g, c);
            if (stand is not { } s) continue;
            if (Chebyshev(s, here) < MinDistance) continue;
            if (rejected != null && rejected(s)) continue;
            best = new FrontierPick(s, c, d, unknown);
        }
        return best;
    }

    /// <summary>Open-space step counts from the player's body cells (BFS, 4-neighbourhood).</summary>
    public static Dictionary<TilePoint, int> Flood(ITileGrid g, TilePoint here, TilePoint origin, int radius, int maxNodes = DefaultMaxNodes)
    {
        var dist = new Dictionary<TilePoint, int>();
        var queue = new Queue<TilePoint>();
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                var b = new TilePoint(here.X + dx, here.Y - dy);
                if (IsOpen(g.Get(b)) && dist.TryAdd(b, 0)) queue.Enqueue(b);
            }
        while (queue.Count > 0 && dist.Count < maxNodes)
        {
            var c = queue.Dequeue();
            int d = dist[c] + 1;
            foreach (var n in Neighbours(c))
            {
                if (Chebyshev(n, origin) > radius || dist.ContainsKey(n)) continue;
                if (!IsOpen(g.Get(n))) continue;
                dist[n] = d;
                queue.Enqueue(n);
                if (dist.Count >= maxNodes) break;
            }
        }
        return dist;
    }

    /// <summary>The stand spot explore_caves walks to for a frontier cell, or null (see the class summary).</summary>
    public static TilePoint? StandFor(ITileGrid g, TilePoint frontier)
    {
        for (int drop = 0; drop <= StandDrop; drop++)
        {
            int y = frontier.Y + drop;
            // Falling past solid ground isn't a drop: stop at the first column cell that blocks.
            if (drop > 0 && !IsOpen(g.Get(frontier.X, y))) break;
            foreach (int x in new[] { frontier.X, frontier.X - 1 })
            {
                var s = new TilePoint(x, y);
                if (Pathfinder.CanStand(g, s) && AllKnown(g, s) && !LavaWithin(g, new TilePoint(x, y - 1), 2)
                    && !LavaWithin(g, new TilePoint(x + 1, y + 1), 2) && g.Get(s).Kind != TileKind.Water)
                    return s;
            }
        }
        return null;
    }

    private static bool AllKnown(ITileGrid g, TilePoint s)
    {
        for (int dy = -1; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (g.Get(s.X + dx, s.Y - dy).Kind == TileKind.Unknown) return false;
        return true;
    }

    private static bool TouchesUnknown(ITileGrid g, TilePoint c)
    {
        foreach (var n in Neighbours(c))
            if (g.Get(n).Kind == TileKind.Unknown) return true;
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

    private static IEnumerable<TilePoint> Neighbours(TilePoint c)
    {
        yield return new TilePoint(c.X + 1, c.Y);
        yield return new TilePoint(c.X - 1, c.Y);
        yield return new TilePoint(c.X, c.Y + 1);
        yield return new TilePoint(c.X, c.Y - 1);
    }

    public static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
