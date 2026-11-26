using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;

namespace LlmPlayer.Tasks;

public enum ExploreDirection { Left, Right, Down, Up }

/// <summary>
/// Travel <c>distance</c> tiles in a direction through known terrain: each leg is a <see cref="MoveToTask"/> to the
/// visible standable tile farthest along the direction (fair vision: Unknown tiles are never picked) within a
/// window ahead. Succeeds once the distance is covered; fails after 3 consecutive legs without progress (or when
/// no visible standable tile lies ahead).
/// </summary>
public sealed class ExploreTask : IGameTask<GameCtx>
{
    private const int MaxNoProgressLegs = 3, LegReach = 40, LateralReach = 15;

    private readonly ExploreDirection _dir;
    private readonly int _distance;
    private TilePoint? _origin;
    private MoveToTask? _leg;
    private int _bestAlong, _legStartAlong, _noProgress;

    public string Name => "explore";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    public ExploreTask(ExploreDirection dir, int distance)
    {
        _dir = dir;
        _distance = Math.Clamp(distance, 1, 500);
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var here = Here(ctx);
        _origin ??= here;
        int along = Along(here - _origin.Value);
        _bestAlong = Math.Max(_bestAlong, along);
        Progress = $"{Math.Max(0, along)}/{_distance} tiles";
        if (along >= _distance)
        {
            _leg?.OnCancel(ctx);
            return GameTaskStatus.Succeeded;
        }

        if (_leg != null)
        {
            var st = _leg.Tick(ctx);
            if (st == GameTaskStatus.Running) return GameTaskStatus.Running;
            string? legReason = _leg.Reason;
            _leg = null;
            if (along <= _legStartAlong) _noProgress++;
            else _noProgress = 0;
            if (_noProgress >= MaxNoProgressLegs)
                return Fail($"no progress after {MaxNoProgressLegs} attempts{(legReason != null ? ": " + legReason : "")}");
        }

        if (!PickTarget(ctx, here, out var target))
            return Fail($"no visible ground further {_dir.ToString().ToLowerInvariant()}");
        _legStartAlong = along;
        _leg = new MoveToTask(target, name: "explore");
        return GameTaskStatus.Running;
    }

    public void OnCancel(GameCtx ctx) => _leg?.OnCancel(ctx);

    private GameTaskStatus Fail(string reason)
    {
        Reason = reason;
        return GameTaskStatus.Failed;
    }

    private static TilePoint Here(GameCtx ctx)
    {
        var p = ctx.Player;
        return NavMath.StandingTile(NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height));
    }

    private int Along(TilePoint d) => _dir switch
    {
        ExploreDirection.Left => -d.X,
        ExploreDirection.Right => d.X,
        ExploreDirection.Down => d.Y,
        _ => -d.Y,
    };

    /// <summary>The visible standable cell ahead with the most progress (ties: closest to the line of travel),
    /// not beyond the remaining distance.</summary>
    private bool PickTarget(GameCtx ctx, TilePoint here, out TilePoint best)
    {
        best = default;
        int remaining = _distance - Along(here - _origin!.Value);
        int reach = Math.Min(LegReach, Math.Max(1, remaining));
        bool horizontal = _dir is ExploreDirection.Left or ExploreDirection.Right;
        int sign = _dir is ExploreDirection.Left or ExploreDirection.Up ? -1 : 1;
        int bestScore = int.MinValue;
        for (int a = reach; a >= 2; a--)
            for (int l = -LateralReach; l <= LateralReach; l++)
            {
                var p = horizontal ? new TilePoint(here.X + sign * a, here.Y + l) : new TilePoint(here.X + l, here.Y + sign * a);
                int score = a * 100 - Math.Abs(l);
                if (score <= bestScore || !Pathfinder.CanStand(ctx.Grid, p) || HasUnknown(ctx.Grid, p)) continue;
                bestScore = score;
                best = p;
            }
        return bestScore != int.MinValue;
    }

    private static bool HasUnknown(ITileGrid g, TilePoint p)
    {
        for (int y = p.Y - 2; y <= p.Y + 1; y++)
            for (int x = p.X; x <= p.X + 1; x++)
                if (g.Get(x, y).Kind == TileKind.Unknown) return true;
        return false;
    }
}
