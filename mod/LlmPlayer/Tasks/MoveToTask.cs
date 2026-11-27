using LlmPlayer.Control;
using LlmPlayer.Core.Build;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Interact;
using LlmPlayer.Nav;
using LlmPlayer.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>
/// Walk (jump, dig, build, swim) to a tile or to an entity. Plans off the main thread (<see cref="PlanJob"/>; the
/// player holds still meanwhile), follows with a <see cref="PathFollower"/> and writes
/// <see cref="ControlState.Pending"/> every tick.
/// <list type="bullet">
/// <item>Replans when the follower reports Stuck/NeedsReplan (a fresh follower each time: those states are
/// sticky), every 120 ticks while the remaining path crosses Unknown tiles, every 60 ticks for an entity goal
/// that moved, and on reaching the end of a partial plan (goal beyond the planning window).</item>
/// <item>Replans keep the current partial target (<see cref="RouteCommitment"/>): each plan used to choose one from
/// scratch, and plans from either end of a pool flipped between two (seen in game, until the stall watchdog).</item>
/// <item>Fails after 5 consecutive failed replans (a plan that fails, or a follower that gets stuck/derailed
/// without completing any step), with the last reason, and after the same Stuck/NeedsReplan from the same tile
/// <see cref="ReplanGuard"/> times however much the plans in between progressed.</item>
/// <item>Dig steps hold the best pickaxe, build steps a platform or a cheap block (<see cref="BuildReserve"/>: the
/// plan keeps <see cref="PlatformReserve"/> platforms unless no route fits without them, bridges/climbs switch to blocks
/// at the reserve, descents take platforms); the previously held item comes back when the task ends
/// (<see cref="HeldItemLease"/>). Result: platforms/blocks used, platforms left.</item>
/// <item>Doors: when the follower asks to interact with a door (OpenDoor / CloseDoor steps) it is opened or closed
/// like a right-click (<see cref="DoorActions"/>, vanilla reach), at most every <see cref="DoorRetryTicks"/> ticks.</item>
/// <item>Torches (<see cref="Torches"/>, move_to's <c>torches</c>): in the dark underground or in a dug tunnel, with no
/// torch within 10 tiles, it stops between steps, places one within reach (<see cref="PlaceTask"/>: real controls,
/// vanilla rules) and replans, so the way is revealed for this and later trips (<see cref="WalkTorch"/> decides when
/// and where). Never in protected areas. Result: <see cref="MoveToResult"/> {torchesPlaced}.</item>
/// </list>
/// As a sub-task: construct it, call <see cref="Tick"/> from the parent's Tick until it returns non-Running, and
/// call <see cref="OnCancel"/> if the parent is cancelled or abandons it. It finishes cleanly on its own
/// (keys released, item restored) when it returns Succeeded/Failed.
/// </summary>
public sealed class MoveToTask : IGameTask<GameCtx>
{
    public const int MaxConsecutiveFailures = 5, MaxLearnedReplans = 15;
    private int _learnedReplans;
    private const int UnknownReplanTicks = 120, EntityReplanTicks = 60, RetryDelayTicks = 20, DoorRetryTicks = 10;
    private int _doorCooldown;

    private TilePoint _goal;
    private readonly int? _entityId;
    private readonly bool _allowDig, _allowBuild;

    /// <summary>Light the way with torches from the inventory (see the class summary). Off for sub-walks of tasks
    /// that place their own (explore) and for the torch's own placement.</summary>
    public bool Torches { get; init; }

    /// <summary>Torches placed so far.</summary>
    public int TorchesPlaced => _torchPacer.Placed;

    public object? Result
    {
        get
        {
            if (_startPlatforms < 0) return Torches ? new MoveToResult(TorchesPlaced) : null;
            var p = Main.LocalPlayer;
            int platforms = HeldItemLease.PlatformCount(p), blocks = HeldItemLease.BlockCount(p);
            return new MoveToResult(TorchesPlaced, Math.Max(0, _startPlatforms - platforms), Math.Max(0, _startBlocks - blocks),
                platforms, _reserveReleased,
                _allowBuild && (_startPlatforms != platforms || _reserveReleased) ? BuildReserve.Note(platforms, PlatformReserve, _reserveReleased) : null);
        }
    }

    /// <summary>Platforms walks keep for later descents (set_reflexes <c>platform_reserve</c>; in memory only).</summary>
    public static int DefaultPlatformReserve = BuildReserve.DefaultPlatforms;

    /// <summary>Platforms this walk keeps unless no route fits without them (<see cref="BuildReserve"/>).</summary>
    public int PlatformReserve { get; init; } = DefaultPlatformReserve;

    private bool _reserveReleased;
    private int _startPlatforms = -1, _startBlocks;
    private int EffectiveReserve => _reserveReleased ? 0 : PlatformReserve;

    private const int LightSettleTicks = 10;
    private readonly WalkTorch _torchPacer = new();
    private readonly HashSet<TilePoint> _badTorchSpots = new();
    private PlaceTask? _torch;
    private TilePoint _torchSpot;
    private bool _dug;

    /// <summary>Keep a goal inside diggable rock and dig to it (PathOptions.DigToGoal) instead of snapping.</summary>
    public bool DigToGoal { get; init; }
    private readonly int _tolerance;
    private readonly HeldItemLease _lease = new();

    private PlanJob? _job;
    private PathFollower? _follower;
    private bool _partial, _pathCrossesUnknown;
    private TilePoint? _resolvedGoal;
    private ProgressGuard _progress = new(maxStalls: 3);
    private readonly ReplanGuard _repeats = new();
    private readonly RouteCommitment _route = new();
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

    private IReadOnlySet<TilePoint>? _goalSet;
    private bool _retarget;

    /// <summary>Standing positions that count as arrived as well as the goal (<see cref="PathOptions.GoalSet"/>): the
    /// plan heads for the cheapest one to reach, the goal only steers it. E.g. every stand within talk range of an NPC.</summary>
    public IReadOnlySet<TilePoint>? GoalSet { get => _goalSet; init => _goalSet = value is { Count: > 0 } ? value : null; }

    /// <summary>Move the goal (and goal set) of a tile walk, e.g. after the NPC it is about moved: replans at once while
    /// the current path is followed in the meantime.</summary>
    public void Retarget(TilePoint goal, IReadOnlySet<TilePoint>? goalSet)
    {
        _goal = goal;
        _goalSet = goalSet is { Count: > 0 } ? goalSet : null;
        _retarget = true;
    }

    private bool InGoalSet(TilePoint t) => _goalSet != null && _goalSet.Contains(t);

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var here = NavMath.StandingTile(k, ctx.Grid);
        if (_startPlatforms < 0)
        {
            _startPlatforms = HeldItemLease.PlatformCount(p);
            _startBlocks = HeldItemLease.BlockCount(p);
        }

        if (_entityId is { } id && !UpdateEntityGoal(id, here, out var why)) return Finish(ctx, GameTaskStatus.Failed, why);

        int dist = Chebyshev(here, _goal);
        Progress = $"{dist} tiles remaining";
        if ((dist <= _tolerance || InGoalSet(here)) && k.OnGround) return Finish(ctx, GameTaskStatus.Succeeded, null);

        if (_torch != null) return TickTorch(ctx, here);
        if (Torches && _follower != null && TryStartTorch(ctx, k, here)) return GameTaskStatus.Running;

        if (_retarget)
        {
            _retarget = false;
            if (_follower != null || _job != null) StartPlan(p, here); // (re)plan to the new goal; keep following meanwhile
        }
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
                _route.OnArrived();
                // A complete plan whose goal was snapped (the requested tile wasn't standable) ends within tolerance
                // of the snapped goal, maybe 2-4 tiles from _goal: that's as close as the body can get, so done.
                // Without this the task replanned the same path forever ("2 tiles remaining").
                if (!_partial && _resolvedGoal is { } rg && (Chebyshev(here, rg) <= _tolerance || InGoalSet(here)) && k.OnGround)
                    return Finish(ctx, GameTaskStatus.Succeeded, null);
                // Any plan (partial or not) that keeps ending no closer to the goal: give up instead of looping.
                if (_progress.RecordArrival(Chebyshev(here, _goal)))
                {
                    // The route within the platform reserve leads nowhere: try once more with every build item.
                    if (TryReleaseReserve(p, "no progress"))
                    {
                        StartPlan(p, here);
                        ControlState.Pending = default;
                        return GameTaskStatus.Running;
                    }
                    return Finish(ctx, GameTaskStatus.Failed, $"unreachable: no progress toward {_goal}");
                }
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
                // A path through unknown tiles that changes because those tiles came into view isn't a failure: the
                // next plan knows more (seen in game: 5 such replans in a row ended move_to at a cave pool). Capped so
                // it can't replan forever.
                bool learned = o.Status == FollowerStatus.NeedsReplan && _pathCrossesUnknown && ++_learnedReplans <= MaxLearnedReplans;
                if (!progressed && !learned && ++_failures >= MaxConsecutiveFailures) return Finish(ctx, GameTaskStatus.Failed, _lastFailure);
                // The same failure from the same spot over and over (each plan in between walking a step or two, so the
                // count above keeps resetting): give up instead of looping until cancelled.
                if (!learned && _repeats.Record(here, _lastFailure)) return Finish(ctx, GameTaskStatus.Failed, _lastFailure + " (repeatedly)");
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

    // ---- torches ----------------------------------------------------------------------------------------------

    /// <summary>Dark and due: drop the path, place a torch, then replan from where the player stands.</summary>
    private bool TryStartTorch(GameCtx ctx, PlayerKinematics k, TilePoint here)
    {
        var step = _follower!.Remaining.Select(s => (PathStep?)s).FirstOrDefault();
        if (step?.Kind == MoveKind.Dig) _dug = true;
        if (!_torchPacer.Due()) return false;
        var p = ctx.Player;
        int slot = ExploreTask.TorchSlot(p);
        var sense = new WalkTorchSense(slot >= 0, k.OnGround, p.wet, step?.Kind, here.Y > Main.worldSurface, _dug,
            ExploreTask.BodyBrightness(here), ExploreTask.NearestTorch(here, TorchPlanner.SpacingTiles),
            Safety.Protection.IsProtectedArea(here.X, here.Y));
        if (WalkTorch.Blocker(sense) != null) return false;
        var reach = new ReachSpec(Player.tileRangeX, Player.tileRangeY, p.inventory[slot].tileBoost);
        if (WalkTorch.Spot(ctx.Grid, TorchCellAt, here, reach, _follower.Remaining) is not { } spot) return false;
        _job?.Cancel();
        _job = null;
        DropFollower();
        ControlState.Pending = default;
        _torch = new PlaceTask(p, slot, spot);
        _torchSpot = spot;
        Progress = "placing a torch";
        return true;
    }

    private GameTaskStatus TickTorch(GameCtx ctx, TilePoint here)
    {
        var st = _torch!.Tick(ctx);
        if (st == GameTaskStatus.Running)
        {
            Progress = "placing a torch";
            return GameTaskStatus.Running;
        }
        _torch = null;
        ControlState.Pending = default;
        if (st == GameTaskStatus.Succeeded) _torchPacer.OnPlaced();
        else
        {
            _badTorchSpots.Add(_torchSpot);
            _torchPacer.OnFailed();
        }
        // Let the new light spread, then plan again from here (the follower was dropped: it can't resume mid-step).
        _retryIn = LightSettleTicks;
        return GameTaskStatus.Running;
    }

    /// <summary>Vanilla torch rules the planner can't see: empty (or cuttable plants), dry, a wall behind; never a
    /// protected tile or a spot the game already refused.</summary>
    private TorchCell TorchCellAt(TilePoint t)
    {
        if (!WorldGen.InWorld(t.X, t.Y, 2) || _badTorchSpots.Contains(t) || Safety.Protection.IsProtected(t.X, t.Y))
            return new TorchCell(false, false);
        var tile = Main.tile[t.X, t.Y];
        bool free = (!tile.HasTile || Main.tileCut[tile.TileType]) && tile.LiquidAmount == 0;
        return new TorchCell(free, tile.WallType != WallID.None);
    }

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
            // No route within the platform reserve: replan at once with every build item (not counted as a failure).
            if (TryReleaseReserve(ctx.Player, r.FailureReason))
            {
                _retryIn = 0;
                return GameTaskStatus.Running;
            }
            if (deterministic || ++_failures >= MaxConsecutiveFailures)
                return Finish(ctx, GameTaskStatus.Failed, r.FailureReason);
            _retryIn = RetryDelayTicks;
            return GameTaskStatus.Running;
        }
        _partial = job.IsPartial;
        _resolvedGoal = r.ResolvedGoal;
        _route.OnPlanned(job.PlannedGoal, job.IsPartial);
        // The search started from the normalized start (FindPath): the follower's first "previous position" must match.
        _follower = new PathFollower(r.Steps, start: Pathfinder.NormalizeStart(ctx.Grid, job.Start), physics: ProfileBuilder.Physics(ctx.Player),
            canSwim: ProfileBuilder.CanSwim(ctx.Player), waterWalk: job.Profile.WaterWalk, lavaWalk: job.Profile.LavaWalk);
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
        var profile = ProfileBuilder.From(p, _allowDig, _allowBuild, EffectiveReserve);
        // Replans stick to the current partial target unless a clearly better one appears (no ping-pong between two).
        var hint = _route.BeforePlan(here, _goal);
        _job = new PlanJob(here, _goal, profile, new PathOptions(_allowDig, _allowBuild, GoalTolerance: _tolerance, DigToGoal: DigToGoal && _allowDig,
            GoalSet: _goalSet), hint);
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
            MoveKind.BuildPlatform or MoveKind.BuildDown => HeldItemLease.BuildItemFor(p, step.Value.Kind == MoveKind.BuildDown, EffectiveReserve),
            _ => -2,
        };
        if (slot == -2) return true;
        return slot >= 0 && _lease.Hold(p, slot);
    }

    private bool TryReleaseReserve(Player p, string? failure)
    {
        if (!BuildReserve.ShouldRelease(_reserveReleased, _allowBuild, HeldItemLease.PlatformCount(p), HeldItemLease.BlockCount(p),
                PlatformReserve, failure))
            return false;
        _reserveReleased = true;
        _progress = new ProgressGuard(maxStalls: 3);
        return true;
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
        _torch?.OnCancel(ctx);
        _torch = null;
        _job?.Cancel();
        _job = null;
        DropFollower();
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
    }

    private static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
