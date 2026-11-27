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
///
/// A <see cref="RouteHint"/> (the previous partial plan's target, from <see cref="RouteCommitment"/>) gives replans
/// hysteresis: the window grows (up to <see cref="MaxHintExtra"/> tiles past the caps) to keep that target in view, and
/// a partial plan heads for it first unless a new candidate is more than the hint's margin closer to the goal.
/// </summary>
public sealed class PlanJob
{
    public const int Margin = 32, MaxWidth = 200, MaxHeight = 160, TilesPerTick = 6000, MaxHintExtra = 48;
    private const int EdgeKeepOut = 4;

    private readonly MovementProfile _profile;
    private readonly PathOptions _options;
    private RouteHint? _hint;
    private int _x0, _y0, _w, _h;
    private TileInfo[,] _tiles;
    private bool _widened;
    private int _copied;
    private Task<(PathResult Result, TilePoint Goal, bool Partial)>? _search;
    private volatile bool _cancelled;

    public TilePoint Start { get; }
    public TilePoint RequestedGoal { get; }

    /// <summary>Where the result's path ends up heading (the requested goal, or a partial target). Valid once done.</summary>
    public TilePoint PlannedGoal { get; private set; }
    public bool IsPartial { get; private set; }
    public PathResult? Result { get; private set; }

    public PlanJob(TilePoint start, TilePoint goal, MovementProfile profile, PathOptions options, RouteHint? hint = null)
    {
        Start = start;
        RequestedGoal = goal;
        PlannedGoal = goal;
        _profile = profile;
        _options = options;
        _hint = hint;
        (_x0, _w) = Span(start.X, goal.X, MaxWidth);
        (_y0, _h) = Span(start.Y, goal.Y, MaxHeight);
        IncludeHint();
        _tiles = new TileInfo[_w, _h];
    }

    /// <summary>The partial target this job was asked to keep (null: none, or dropped as too far away).</summary>
    public RouteHint? Hint => _hint;

    /// <summary>Grow the window so the hinted target sits inside it with the usual keep-out, or drop the hint when that
    /// would grow the window more than <see cref="MaxHintExtra"/> past the caps (a target that far is stale anyway).</summary>
    private void IncludeHint()
    {
        if (_hint is not { } h) return;
        var t = h.Target;
        int xa = Math.Min(_x0, t.X - 2 - EdgeKeepOut), xb = Math.Max(_x0 + _w - 1, t.X + 3 + EdgeKeepOut);
        int ya = Math.Min(_y0, t.Y - 4 - EdgeKeepOut), yb = Math.Max(_y0 + _h - 1, t.Y + 2 + EdgeKeepOut);
        if (xb - xa + 1 > MaxWidth + MaxHintExtra || yb - ya + 1 > MaxHeight + MaxHintExtra)
        {
            _hint = null;
            return;
        }
        (_x0, _w, _y0, _h) = (xa, xb - xa + 1, ya, yb - ya + 1);
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

    /// <summary>The widest window (MaxWidth x MaxHeight, or the normal span where that is already capped) centred on
    /// start and goal, for the retry after a failed search.</summary>
    private static (int Origin, int Size) WideSpan(int a, int b, int max)
    {
        var normal = Span(a, b, max);
        if (normal.Size >= max) return normal;
        return ((a + b) / 2 - max / 2, max);
    }

    /// <summary>The widest snapshot a job for this trip can use (x0, y0, width, height).</summary>
    public static (int X0, int Y0, int W, int H) WidestWindow(TilePoint start, TilePoint goal)
    {
        var (x0, w) = WideSpan(start.X, goal.X, MaxWidth);
        var (y0, h) = WideSpan(start.Y, goal.Y, MaxHeight);
        return (x0, y0, w, h);
    }

    /// <summary>The search found no way inside the bounding box + <see cref="Margin"/> window: retry once in the widest
    /// window. Seen in the real-terrain fuzz: pockets walled by rock the pickaxe can't break, whose way out loops
    /// further around than the margin, failed "unreachable" though a route existed in plain sight of the player.</summary>
    private bool Widen()
    {
        if (_widened || _cancelled) return false;
        var (x0, w) = WideSpan(Start.X, RequestedGoal.X, MaxWidth);
        var (y0, h) = WideSpan(Start.Y, RequestedGoal.Y, MaxHeight);
        if (w == _w && h == _h) return false;
        _widened = true;
        (_x0, _w, _y0, _h) = (x0, w, y0, h);
        IncludeHint();
        _tiles = new TileInfo[_w, _h];
        _copied = 0;
        _search = null;
        return true;
    }

    /// <summary>How many <see cref="Step"/> calls (game ticks) copying the window(s) took so far.</summary>
    public int CopyTicks { get; private set; }

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
            CopyTicks++;
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
            SealEdges();
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
            if (!result.Success && result.FailureReason?.StartsWith("unreachable") == true && Widen()) return false;
            Result = result;
            PlannedGoal = goal;
            IsPartial = partial;
        }
        return true;
    }

    /// <summary>What the snapshot's outermost ring becomes: impassable and nothing to stand on.</summary>
    public static readonly TileInfo WindowEdge = new(TileKind.Hazard, Name: "planning window edge");

    /// <summary>
    /// Beyond the snapshot the grid reads as unbreakable solid, i.e. as ground: the search planned falls and walks onto
    /// "floor" just outside the window, which the follower then found unsupported (seen in the real-terrain fuzz: a
    /// fall onto the window's bottom edge, "path changed at Jump" five times without moving). The outermost ring is
    /// overwritten with an impassable, non-standable tile instead (it lies inside the margin, never at a goal).
    /// </summary>
    private void SealEdges()
    {
        for (int x = 0; x < _w; x++)
        {
            _tiles[x, 0] = WindowEdge;
            _tiles[x, _h - 1] = WindowEdge;
        }
        for (int y = 0; y < _h; y++)
        {
            _tiles[0, y] = WindowEdge;
            _tiles[_w - 1, y] = WindowEdge;
        }
    }

    /// <summary>Partial plans try this many intermediate targets (nearest the goal first, spread apart) before giving
    /// up: the single nearest one can sit in a pocket the player can't reach (seen in game: a long trip from spawn
    /// failed "unreachable" while the same trip from 30 tiles further along worked).</summary>
    public const int MaxPartialCandidates = 5;
    private const int CandidateSpacing = 6;

    private (PathResult, TilePoint, bool) Search(ArrayTileGrid g)
    {
        var goal = RequestedGoal;
        if (Inside(goal, EdgeKeepOut))
        {
            var direct = Pathfinder.FindPath(g, Start, goal, _profile, _options);
            // Out of search budget on a long trip (not a dead end: the search simply ran out of nodes, typical where
            // digging makes every tile an option): get as close as the search got and replan from there, like a goal
            // beyond the window. Seen in the real-terrain fuzz: 60-150 tile trips through caves failed outright
            // ("unreachable: searched 20000 nodes") although each half of them planned fine.
            if (direct.Success || direct.NodesExpanded < _options.MaxNodes) return (direct, goal, false);
            return Hinted(g, double.MaxValue) ?? Closest(g) ?? (direct, goal, false);
        }

        var candidates = KnownStandableTowardGoal(g, goal, MaxPartialCandidates);
        if (Hinted(g, candidates.Count > 0 ? Distance(candidates[0], goal) : double.MaxValue) is { } kept) return kept;
        if (candidates.Count == 0)
            return (new PathResult(false, Array.Empty<PathStep>(),
                $"unreachable: no known standable ground toward {RequestedGoal} within the planning window", 0), RequestedGoal, false);
        PathResult? last = null;
        foreach (var c in candidates)
        {
            if (_cancelled) break;
            var r = Pathfinder.FindPath(g, Start, c, _profile, _options with { GoalSet = null }); // an intermediate target: it ends the search
            if (r.Success) return (r, c, true);
            last = r;
        }
        // Every candidate sits somewhere unreachable (seen in game: all five in one sealed pocket): head for the
        // reachable spot closest to the goal and replan from there.
        return Closest(g) ?? (last!, candidates[0], true);
    }

    /// <summary>A partial plan to the hinted target (the previous plan's), or null: no hint, the target can't be stood on
    /// any more, a candidate <paramref name="bestDistance"/> from the goal beats it by more than the hint's margin, or
    /// no path leads there now.</summary>
    private (PathResult, TilePoint, bool)? Hinted(ArrayTileGrid g, double bestDistance)
    {
        if (_hint is not { } h || _cancelled) return null;
        var t = h.Target;
        if (!Inside(t, EdgeKeepOut) || !Pathfinder.CanStand(g, t)) return null;
        if (bestDistance < Distance(t, RequestedGoal) - h.SwitchMargin) return null;
        var r = Pathfinder.FindPath(g, Start, t, _profile, _options with { GoalSet = null });
        return r.Success ? (r, t, true) : null;
    }

    private static double Distance(TilePoint a, TilePoint b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>A partial plan to the reachable spot closest to the goal, or null. Known routes first (no digging):
    /// digging made "straight down toward the goal" look cheapest and the player dug a new shaft into unlit rock instead
    /// of taking the known shaft and tunnels (seen in game).</summary>
    private (PathResult, TilePoint, bool)? Closest(ArrayTileGrid g)
    {
        if (!_cancelled && _options.AllowDig)
        {
            var walk = Pathfinder.FindPath(g, Start, RequestedGoal, _profile, _options with { BestEffort = true, AllowDig = false });
            if (walk.Success && walk.Steps.Count > 0 && walk.ResolvedGoal is { } wend) return (walk, wend, true);
        }
        if (!_cancelled)
        {
            var best = Pathfinder.FindPath(g, Start, RequestedGoal, _profile, _options with { BestEffort = true });
            if (best.Success && best.Steps.Count > 0 && best.ResolvedGoal is { } end) return (best, end, true);
        }
        return null;
    }

    /// <summary>Known standable cells inside the window nearest the goal, at least <see cref="CandidateSpacing"/>
    /// tiles apart, nearest first.</summary>
    private List<TilePoint> KnownStandableTowardGoal(ArrayTileGrid g, TilePoint goal, int max)
    {
        var all = new List<(long D, TilePoint P)>();
        for (int y = _y0; y < _y0 + _h; y++)
            for (int x = _x0; x < _x0 + _w; x++)
            {
                var p = new TilePoint(x, y);
                if (!Inside(p, EdgeKeepOut) || !Pathfinder.CanStand(g, p) || HasUnknown(g, p)) continue;
                long dx = goal.X - x, dy = goal.Y - y;
                all.Add((dx * dx + dy * dy, p));
            }
        all.Sort((a, b) => a.D.CompareTo(b.D));
        var picked = new List<TilePoint>();
        foreach (var (_, p) in all)
        {
            if (picked.Any(q => Math.Max(Math.Abs(q.X - p.X), Math.Abs(q.Y - p.Y)) < CandidateSpacing)) continue;
            picked.Add(p);
            if (picked.Count >= max) break;
        }
        return picked;
    }

    private bool Inside(TilePoint p, int keepOut) =>
        p.X - 1 >= _x0 + keepOut && p.X + 2 <= _x0 + _w - 1 - keepOut && p.Y - 3 >= _y0 + keepOut && p.Y + 1 <= _y0 + _h - 1 - keepOut;

    private static bool HasUnknown(ITileGrid g, TilePoint p)
    {
        for (int y = p.Y - 2; y <= p.Y + 1; y++)
            for (int x = p.X; x <= p.X + 1; x++)
                if (g.Get(x, y).Kind == TileKind.Unknown) return true;
        return false;
    }
}
