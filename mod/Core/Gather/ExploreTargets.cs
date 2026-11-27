using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Gather;

/// <summary>
/// Where explore's next walking leg goes. Pure logic over the fair-vision grid: a target's body and floor are never
/// Unknown, and targets near a spot a walk already failed to reach are skipped.
/// <list type="bullet">
/// <item><see cref="Ahead"/>: the visible standable cell with the most progress along the direction (ties: closest to
/// the line of travel), within ±<c>lateral</c> rows (columns for up/down).</item>
/// <item><see cref="Surface"/> (explore's <c>surface</c> mode, left/right): per column, the topmost spot the body can
/// stand on, the ground's surface, wherever it is (up a hill, down into a valley); the farthest column whose surface is
/// known wins. Seen in game: explore walking west kept its target at the current height while the ground rose into a
/// hill (inside it: Unknown, unrevealed) and stopped with "no floor to walk on". Walking to the farthest known surface
/// point reveals the next stretch; the next leg looks again from there.</item>
/// </list>
/// </summary>
public static class ExploreTargets
{
    /// <summary>Rows above / below the feet a surface column is searched in.</summary>
    public const int SurfaceUp = 40, SurfaceDown = 25;
    /// <summary>Known clear rows a surface spot needs above it when the column higher up is Unknown.</summary>
    public const int KnownSky = 10;

    /// <param name="sign">+1 right/down, -1 left/up.</param>
    /// <param name="horizontal">Left/right (true) or up/down.</param>
    /// <param name="stand">What holds the body at the target (water walking gear).</param>
    public static TilePoint? Ahead(ITileGrid g, TilePoint here, int sign, bool horizontal, int reach, int lateral,
        MovementProfile stand, IReadOnlyCollection<TilePoint> failed)
    {
        TilePoint? best = null;
        int bestScore = int.MinValue;
        for (int a = reach; a >= 2; a--)
            for (int l = -lateral; l <= lateral; l++)
            {
                var p = horizontal ? new TilePoint(here.X + sign * a, here.Y + l) : new TilePoint(here.X + l, here.Y + sign * a);
                int score = a * 100 - Math.Abs(l);
                if (score <= bestScore || !Pathfinder.CanStand(g, p, stand) || HasUnknown(g, p) || NearFailed(p, failed)) continue;
                bestScore = score;
                best = p;
            }
        return best;
    }

    /// <summary>The farthest column (2..<paramref name="reach"/> tiles along <paramref name="sign"/>) whose surface
    /// (<see cref="SurfaceAt"/>) is known; null when no column ahead has one.</summary>
    public static TilePoint? Surface(ITileGrid g, TilePoint here, int sign, int reach, MovementProfile stand,
        IReadOnlyCollection<TilePoint> failed)
    {
        for (int a = reach; a >= 2; a--)
        {
            var s = SurfaceAt(g, here.X + sign * a, here.Y, stand);
            if (s is { } p && !NearFailed(p, failed)) return p;
        }
        return null;
    }

    /// <summary>
    /// The ground's surface in body column <paramref name="x"/> (body x..x+1) near row <paramref name="nearY"/>: scanning
    /// down from <see cref="SurfaceUp"/> rows above to <see cref="SurfaceDown"/> rows below, the first row whose support
    /// row holds a tile (or a walkable liquid surface) under either column. That spot counts only if the body fits
    /// there with its floor known, and the column above it is known: an Unknown patch at most
    /// <see cref="KnownSky"/> rows above may hide the real surface (the spot under it would be a cave floor), so the
    /// column has no known surface then (likewise under rock met right at the scan's top).
    /// </summary>
    public static TilePoint? SurfaceAt(ITileGrid g, int x, int nearY, MovementProfile stand)
    {
        int clear = 0;
        bool unknownSeen = false;
        for (int y = nearY - SurfaceUp; y <= nearY + SurfaceDown; y++)
        {
            var a = g.Get(x, y);
            var b = g.Get(x + 1, y);
            // Unknown, or a tile right at the scan's top / under an Unknown patch (the floor test of the row above would
            // have caught it otherwise): what lies below is only trusted after KnownSky known clear rows.
            if (a.Kind == TileKind.Unknown || b.Kind == TileKind.Unknown || a.BlocksBody || b.BlocksBody)
            {
                unknownSeen = true;
                clear = 0;
                continue;
            }
            clear++;
            var f0 = g.Get(x, y + 1);
            var f1 = g.Get(x + 1, y + 1);
            bool floor = f0.IsStandable || f1.IsStandable || Pathfinder.CanStand(g, new TilePoint(x, y), stand);
            if (!floor) continue;
            if (unknownSeen && clear < KnownSky) return null;
            var p = new TilePoint(x, y);
            return Pathfinder.CanStand(g, p, stand) && !HasUnknown(g, p) ? p : null;
        }
        return null;
    }

    /// <summary>Within 2 tiles of a spot a walk already failed to reach.</summary>
    private static bool NearFailed(TilePoint p, IReadOnlyCollection<TilePoint> failed)
    {
        foreach (var f in failed)
            if (Math.Abs(f.X - p.X) <= 2 && Math.Abs(f.Y - p.Y) <= 2) return true;
        return false;
    }

    /// <summary>Any Unknown cell in the body at <paramref name="p"/> or its support row.</summary>
    public static bool HasUnknown(ITileGrid g, TilePoint p)
    {
        for (int y = p.Y - 2; y <= p.Y + 1; y++)
            for (int x = p.X; x <= p.X + 1; x++)
                if (g.Get(x, y).Kind == TileKind.Unknown) return true;
        return false;
    }
}
