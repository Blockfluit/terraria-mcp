using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>
/// One path search that never blocks the game: <see cref="Step"/> (main thread, once per tick) first copies the
/// region around start and goal from the live grid into an <see cref="ArrayTileGrid"/>, at most
/// <see cref="TilesPerTick"/> tiles per call, then runs <see cref="Pathfinder.FindPath"/> on the thread pool against
/// that snapshot only. Live game tiles are only ever read inside Step, on the caller's thread.
///
/// The region is the bounding box of start and goal plus <see cref="Margin"/>, capped at
/// <see cref="MaxWidth"/> x <see cref="MaxHeight"/> around the start. When the goal lies outside it (or too
/// close to its edge), the job plans to the known standable cell nearest the goal instead and reports
/// <see cref="IsPartial"/>; the caller replans from there.
/// </summary>
public sealed class PlanJob
{
    public const int Margin = 32, MaxWidth = 200, MaxHeight = 160, TilesPerTick = 6000;
    private const int EdgeKeepOut = 4;

    private readonly MovementProfile _profile;
    private readonly PathOptions _options;
    private readonly int _x0, _y0, _w, _h;
    private readonly TileInfo[,] _tiles;
    private int _copied;
    private Task<(PathResult Result, TilePoint Goal, bool Partial)>? _search;
    private volatile bool _cancelled;

    public TilePoint Start { get; }
    public TilePoint RequestedGoal { get; }

    /// <summary>Where the result's path ends up heading (the requested goal, or a partial target). Valid once done.</summary>
    public TilePoint PlannedGoal { get; private set; }
    public bool IsPartial { get; private set; }
    public PathResult? Result { get; private set; }

    public PlanJob(TilePoint start, TilePoint goal, MovementProfile profile, PathOptions options)
    {
        Start = start;
        RequestedGoal = goal;
        PlannedGoal = goal;
        _profile = profile;
        _options = options;
        (_x0, _w) = Span(start.X, goal.X, MaxWidth);
        (_y0, _h) = Span(start.Y, goal.Y, MaxHeight);
        _tiles = new TileInfo[_w, _h];
    }

    /// <summary>[origin, size] covering a..b plus margin, capped at `max` and always keeping `a` well inside.</summary>
    private static (int Origin, int Size) Span(int a, int b, int max)
    {
        int lo = Math.Min(a, b) - Margin, hi = Math.Max(a, b) + Margin;
        if (hi - lo + 1 <= max) return (lo, hi - lo + 1);
        // Too big: a window of `max` that contains `a` with the margin, shifted as far toward `b` as possible.
        int origin = b >= a ? a - Margin : a + Margin - max + 1;
        return (origin, max);
    }

    public void Cancel() => _cancelled = true;

    /// <summary>Advance the job; true once <see cref="Result"/> is set. Call on the thread that owns `source`.</summary>
    public bool Step(ITileGrid source)
    {
        if (Result != null) return true;
        if (_cancelled)
        {
            Result = new PathResult(false, Array.Empty<PathStep>(), "cancelled", 0);
            return true;
        }

        int total = _w * _h;
        if (_copied < total)
        {
            int end = Math.Min(total, _copied + TilesPerTick);
            for (int i = _copied; i < end; i++)
            {
                int lx = i % _w, ly = i / _w;
                _tiles[lx, ly] = source.Get(_x0 + lx, _y0 + ly);
            }
            _copied = end;
            if (_copied < total) return false;
        }

        if (_search == null)
        {
            var snapshot = new ArrayTileGrid(_x0, _y0, _tiles);
            _search = Task.Run(() => Search(snapshot));
            return false;
        }
        if (!_search.IsCompleted) return false;

        if (_cancelled) Result = new PathResult(false, Array.Empty<PathStep>(), "cancelled", 0);
        else if (_search.IsFaulted)
            Result = new PathResult(false, Array.Empty<PathStep>(), "internal: " + _search.Exception?.GetBaseException().Message, 0);
        else
        {
            var (result, goal, partial) = _search.Result;
            Result = result;
            PlannedGoal = goal;
            IsPartial = partial;
        }
        return true;
    }

    private (PathResult, TilePoint, bool) Search(ArrayTileGrid g)
    {
        var goal = RequestedGoal;
        bool partial = false;
        if (!Inside(goal, EdgeKeepOut))
        {
            if (!NearestKnownStandable(g, goal, out goal))
                return (new PathResult(false, Array.Empty<PathStep>(),
                    $"unreachable: no known standable ground toward {RequestedGoal} within the planning window", 0), RequestedGoal, false);
            partial = true;
        }
        return (Pathfinder.FindPath(g, Start, goal, _profile, _options), goal, partial);
    }

    private bool Inside(TilePoint p, int keepOut) =>
        p.X - 1 >= _x0 + keepOut && p.X + 2 <= _x0 + _w - 1 - keepOut && p.Y - 3 >= _y0 + keepOut && p.Y + 1 <= _y0 + _h - 1 - keepOut;

    /// <summary>The standable cell (fully known body and support, inside the window) nearest the goal.</summary>
    private bool NearestKnownStandable(ArrayTileGrid g, TilePoint goal, out TilePoint best)
    {
        best = default;
        long bestD = long.MaxValue;
        for (int y = _y0; y < _y0 + _h; y++)
            for (int x = _x0; x < _x0 + _w; x++)
            {
                var p = new TilePoint(x, y);
                long dx = goal.X - x, dy = goal.Y - y, d = dx * dx + dy * dy;
                if (d >= bestD || !Inside(p, EdgeKeepOut) || !Pathfinder.CanStand(g, p) || HasUnknown(g, p)) continue;
                bestD = d;
                best = p;
            }
        return bestD != long.MaxValue;
    }

    private static bool HasUnknown(ITileGrid g, TilePoint p)
    {
        for (int y = p.Y - 2; y <= p.Y + 1; y++)
            for (int x = p.X; x <= p.X + 1; x++)
                if (g.Get(x, y).Kind == TileKind.Unknown) return true;
        return false;
    }
}
