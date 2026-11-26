using LlmPlayer.Control;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Interact;
using LlmPlayer.Nav;
using LlmPlayer.Observe;
using Terraria;

namespace LlmPlayer.Tasks;

/// <summary>
/// Walk (jump, dig, build, swim) to a tile or to an entity. Plans off the main thread (<see cref="PlanJob"/>; the
/// player holds still meanwhile), follows with a <see cref="PathFollower"/> and writes
/// <see cref="ControlState.Pending"/> every tick.
/// <list type="bullet">
/// <item>Replans when the follower reports Stuck/NeedsReplan (a fresh follower each time: those states are
/// sticky), every 120 ticks while the remaining path crosses Unknown tiles, every 60 ticks for an entity goal
/// that moved, and on reaching the end of a partial plan (goal beyond the planning window).</item>
/// <item>Fails after 5 consecutive failed replans (a plan that fails, or a follower that gets stuck/derailed
/// without completing any step), with the last reason.</item>
/// <item>Dig steps hold the best pickaxe, build steps a platform (else the cheapest block); the previously held
/// item comes back when the task ends (<see cref="HeldItemLease"/>).</item>
/// <item>Doors: when the follower asks to interact with a door (OpenDoor / CloseDoor steps) it is opened or closed
/// like a right-click (<see cref="DoorActions"/>, vanilla reach), at most every <see cref="DoorRetryTicks"/> ticks.</item>
/// </list>
/// As a sub-task: construct it, call <see cref="Tick"/> from the parent's Tick until it returns non-Running, and
/// call <see cref="OnCancel"/> if the parent is cancelled or abandons it. It finishes cleanly on its own
/// (keys released, item restored) when it returns Succeeded/Failed.
/// </summary>
public sealed class MoveToTask : IGameTask<GameCtx>
{
    public const int MaxConsecutiveFailures = 5;
    private const int UnknownReplanTicks = 120, EntityReplanTicks = 60, RetryDelayTicks = 20, DoorRetryTicks = 10;
    private int _doorCooldown;

    private TilePoint _goal;
    private readonly int? _entityId;
    private readonly bool _allowDig, _allowBuild;

    /// <summary>Keep a goal inside diggable rock and dig to it (PathOptions.DigToGoal) instead of snapping.</summary>
    public bool DigToGoal { get; init; }
    private readonly int _tolerance;
    private readonly HeldItemLease _lease = new();

    private PlanJob? _job;
    private PathFollower? _follower;
    private bool _partial, _pathCrossesUnknown;
    private TilePoint? _resolvedGoal;
    private readonly ProgressGuard _progress = new(maxStalls: 3);
    private int _ticksSincePlan, _retryIn, _failures, _stepsAtPlan;
    private string? _lastFailure;

    public string Name { get; }
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }

    /// <param name="tolerance">Done when standing within this Chebyshev distance of the goal. 0 = exactly on it
    /// (TileWorker's stand spots, which may sit at the very edge of tool reach).</param>
    public MoveToTask(TilePoint goal, bool allowDig = true, bool allowBuild = true, int tolerance = 1, string name = "move_to")
    {
        _goal = goal;
        _allowDig = allowDig;
        _allowBuild = allowBuild;
        _tolerance = Math.Max(0, tolerance);
        Name = name;
    }

    /// <summary>Follow an entity from <see cref="EntityReader"/> (NPC index or 1000 + item index).</summary>
    public MoveToTask(int entityId, bool allowDig = true, bool allowBuild = true, int tolerance = 1, string name = "move_to")
        : this(default(TilePoint), allowDig, allowBuild, tolerance, name)
    {
        _entityId = entityId;
    }

    public TilePoint Goal => _goal;

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var here = NavMath.StandingTile(k);

        if (_entityId is { } id && !UpdateEntityGoal(id, here, out var why)) return Finish(ctx, GameTaskStatus.Failed, why);

        int dist = Chebyshev(here, _goal);
        Progress = $"{dist} tiles remaining";
        if (dist <= _tolerance && k.OnGround) return Finish(ctx, GameTaskStatus.Succeeded, null);

        if (_job != null) return PollPlan(ctx, k, here);
        if (_follower == null)
        {
            ControlState.Pending = default;
            if (_retryIn > 0)
            {
                _retryIn--;
                return GameTaskStatus.Running;
            }
            StartPlan(p, here);
            return GameTaskStatus.Running;
        }

        _ticksSincePlan++;
        if ((_pathCrossesUnknown && _ticksSincePlan >= UnknownReplanTicks)
            || (_entityId != null && _ticksSincePlan >= EntityReplanTicks))
        {
            StartPlan(p, here); // keep following the old path until the new one is ready
        }

        var o = _follower.Tick(k, ctx.Grid);
        switch (o.Status)
        {
            case FollowerStatus.Arrived:
                _failures = 0;
                DropFollower();
                // A complete plan whose goal was snapped (the requested tile wasn't standable) ends within tolerance
                // of the snapped goal, maybe 2-4 tiles from _goal: that's as close as the body can get, so done.
                // Without this the task replanned the same path forever ("2 tiles remaining").
                if (!_partial && _resolvedGoal is { } rg && Chebyshev(here, rg) <= _tolerance && k.OnGround)
                    return Finish(ctx, GameTaskStatus.Succeeded, null);
                // Any plan (partial or not) that keeps ending no closer to the goal: give up instead of looping.
                if (_progress.RecordArrival(Chebyshev(here, _goal)))
                    return Finish(ctx, GameTaskStatus.Failed, $"unreachable: no progress toward {_goal}");
                if (_partial || Chebyshev(here, _goal) > _tolerance) StartPlan(p, here);
                ControlState.Pending = default;
                return GameTaskStatus.Running;
            case FollowerStatus.Stuck:
            case FollowerStatus.NeedsReplan:
                bool progressed = _stepsAtPlan - _follower.RemainingSteps > 0;
                string what = o.Status == FollowerStatus.Stuck ? "stuck" : "path changed";
                _lastFailure = $"{what} at {o.Current?.Kind} {o.Current?.Pos}";
                DropFollower();
                ControlState.Pending = default;
                if (!progressed && ++_failures >= MaxConsecutiveFailures) return Finish(ctx, GameTaskStatus.Failed, _lastFailure);
                if (progressed) _failures = 0;
                StartPlan(p, here);
                return GameTaskStatus.Running;
        }

        var intent = o.Intent;
        if (!HoldToolFor(p, o.Current)) intent.UseItem = false; // wait for the right item before swinging
        UseDoor(p, intent, o.Current);
        ControlState.Pending = intent;
        return GameTaskStatus.Running;
    }

    /// <summary>Open (or, on a CloseDoor step, close) the door the follower asks for.</summary>
    private void UseDoor(Player p, ControlIntent intent, PathStep? step)
    {
        if (_doorCooldown > 0) _doorCooldown--;
        if (intent.Interact is not { } door || _doorCooldown > 0) return;
        bool done = step?.Kind == MoveKind.CloseDoor ? DoorActions.TryClose(p, door.X, door.Y) : DoorActions.TryOpen(p, door.X, door.Y);
        if (!done && DoorActions.InReach(p, door.X, door.Y)) _doorCooldown = DoorRetryTicks;
    }

    public void OnCancel(GameCtx ctx) => Cleanup(ctx);

    private GameTaskStatus PollPlan(GameCtx ctx, PlayerKinematics k, TilePoint here)
    {
        if (_follower == null) ControlState.Pending = default; // hold still while planning
        var job = _job!;
        if (!job.Step(ctx.Grid))
        {
            if (_follower != null) FollowWhilePlanning(ctx, k);
            return GameTaskStatus.Running;
        }
        _job = null;
        var r = job.Result!;
        if (!r.Success)
        {
            _lastFailure = r.FailureReason;
            DropFollower();
            ControlState.Pending = default;
            // A fixed goal inside rock won't improve by retrying; other failures may (tiles revealed, player moved,
            // a moving target).
            bool deterministic = _entityId == null && r.FailureReason?.StartsWith("goal_not_standable") == true;
            if (deterministic || ++_failures >= MaxConsecutiveFailures)
                return Finish(ctx, GameTaskStatus.Failed, r.FailureReason);
            _retryIn = RetryDelayTicks;
            return GameTaskStatus.Running;
        }
        _partial = job.IsPartial;
        _resolvedGoal = r.ResolvedGoal;
        _follower = new PathFollower(r.Steps, start: job.Start, physics: ProfileBuilder.Physics(ctx.Player));
        _stepsAtPlan = _follower.RemainingSteps;
        _pathCrossesUnknown = CrossesUnknown(ctx.Grid, r.Steps);
        _ticksSincePlan = 0;
        PathOverlayState.Set(_follower);
        return GameTaskStatus.Running;
    }

    /// <summary>A periodic replan is in flight: keep following the current path in the meantime.</summary>
    private void FollowWhilePlanning(GameCtx ctx, PlayerKinematics k)
    {
        var o = _follower!.Tick(k, ctx.Grid);
        if (o.Status != FollowerStatus.Running)
        {
            ControlState.Pending = default;
            return;
        }
        var intent = o.Intent;
        if (!HoldToolFor(ctx.Player, o.Current)) intent.UseItem = false;
        UseDoor(ctx.Player, intent, o.Current);
        ControlState.Pending = intent;
    }

    private void StartPlan(Player p, TilePoint here)
    {
        _job?.Cancel();
        var profile = ProfileBuilder.From(p, _allowDig, _allowBuild);
        _job = new PlanJob(here, _goal, profile, new PathOptions(_allowDig, _allowBuild, GoalTolerance: _tolerance, DigToGoal: DigToGoal && _allowDig));
    }

    private void DropFollower()
    {
        _follower = null;
        PathOverlayState.Clear();
    }

    private bool UpdateEntityGoal(int id, TilePoint here, out string? why)
    {
        why = null;
        if (!EntityReader.TryResolve(id, out var e) || !Visibility.IsEntityVisible(e))
        {
            why = $"target {id} is gone or no longer visible";
            return false;
        }
        var (x, y) = EntityReader.TileOf(e);
        var t = new TilePoint(x, y);
        if (_goal == default(TilePoint) && _follower == null && _job == null) _goal = t;
        else if (Chebyshev(t, _goal) > 2 && _ticksSincePlan >= EntityReplanTicks) _goal = t;
        return true;
    }

    private bool HoldToolFor(Player p, PathStep? step)
    {
        int slot = step?.Kind switch
        {
            MoveKind.Dig => HeldItemLease.BestPickaxe(p),
            MoveKind.BuildPlatform => HeldItemLease.BuildItem(p),
            _ => -2,
        };
        if (slot == -2) return true;
        return slot >= 0 && _lease.Hold(p, slot);
    }

    private static bool CrossesUnknown(ITileGrid g, IReadOnlyList<PathStep> steps)
    {
        foreach (var s in steps)
            for (int y = s.Pos.Y - 2; y <= s.Pos.Y + 1; y++)
                for (int x = s.Pos.X; x <= s.Pos.X + 1; x++)
                    if (g.Get(x, y).Kind == TileKind.Unknown) return true;
        return false;
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string? reason)
    {
        Reason = reason;
        Cleanup(ctx);
        return status;
    }

    private void Cleanup(GameCtx ctx)
    {
        _job?.Cancel();
        _job = null;
        DropFollower();
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
    }

    private static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
