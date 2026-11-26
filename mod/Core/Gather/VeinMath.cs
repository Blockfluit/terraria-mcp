using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Gather;

/// <summary>Which tiles make up one ore vein: the 8-connected tiles of the same kind as the start, flood-filled.</summary>
public static class VeinMath
{
    /// <param name="sameVein">True for a tile of the start's kind the player can see (unknown tiles stop the fill).</param>
    /// <returns>The vein's tiles, start first, then breadth-first (nearest to the start first), at most
    /// <paramref name="max"/>; empty if the start itself isn't part of a vein.</returns>
    public static List<TilePoint> Collect(Func<int, int, bool> sameVein, TilePoint start, int max = MiningOrder.MaxAreaTiles)
    {
        var result = new List<TilePoint>();
        if (!sameVein(start.X, start.Y)) return result;
        var seen = new HashSet<TilePoint> { start };
        var queue = new Queue<TilePoint>();
        queue.Enqueue(start);
        while (queue.Count > 0 && result.Count < max)
        {
            var t = queue.Dequeue();
            result.Add(t);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var n = new TilePoint(t.X + dx, t.Y + dy);
                    if (seen.Add(n) && sameVein(n.X, n.Y)) queue.Enqueue(n);
                }
        }
        return result;
    }
}
