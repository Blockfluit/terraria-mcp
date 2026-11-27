using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tasks;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>How a simulated move_to ended.</summary>
public enum MoveOutcome { Arrived, Failed, Hurt, Drowning, TimedOut }

/// <summary>
/// A test-side copy of MoveToTask's control loop (mod/LlmPlayer/Tasks/MoveToTask.cs) driving <see cref="MiniPhysics"/>:
/// plans with a real <see cref="PlanJob"/> (snapshot window, partial plans), holds still while the job copies tiles
/// (one tick per <see cref="PlanJob.TilesPerTick"/> tiles, like in game), follows with a fresh
/// <see cref="PathFollower"/> per plan, replans on Stuck/NeedsReplan and at the end of partial plans, gives up after
/// <see cref="MaxConsecutiveFailures"/> failed replans or a <see cref="ProgressGuard"/> stall, replans every
/// <see cref="UnknownReplanTicks"/> while the path crosses Unknown tiles ("learned" replans when it changed because they
/// came into view), keeps partial targets with a <see cref="RouteCommitment"/>, and stops like the host's
/// <see cref="StallWatchdog"/>. With a <see cref="FogGrid"/> the planner and follower only see revealed tiles while
/// physics uses the true grid. Keep it in step with MoveToTask: the real-terrain fuzz measures exactly this loop.
/// </summary>
public sealed class MoveToSim
{
    public const int MaxConsecutiveFailures = 5, RetryDelayTicks = 20, MaxLearnedReplans = 15, UnknownReplanTicks = 120;

    private readonly ArrayTileGrid _g;
    /// <summary>What the planner and follower see: the fog view, or the true grid.</summary>
    private readonly ITileGrid _view;
    private readonly FogGrid? _fog;
    private readonly MiniPhysics _sim;
    private readonly TilePoint _goal;
    private readonly MovementProfile _profile;
    private readonly PathOptions _options;
    private readonly int _tolerance;
    private readonly Action<string>? _log;

    private ProgressGuard _progress = new(maxStalls: 3);
    private TrailLeg? _leg;
    private int _progressEpoch;
    private TrailLeg Leg => _leg ??= new TrailLeg(Trail);

    /// <summary>Travel memory for this walk (MoveToTask: the world's <c>TravelMemory</c>); null = none, as before.</summary>
    public TrailGraph? Trail { get; init; }
    /// <summary>Trail routes the walk used (far-goal start and rescues).</summary>
    public int TrailRoutes => _leg?.Routes ?? 0;
    private readonly ReplanGuard _repeats = new();
    private readonly RouteCommitment _route = new();
    private readonly StallWatchdog _watchdog = new();
    private PathFollower? _follower;
    private bool _partial, _pathCrossesUnknown;
    private TilePoint? _resolvedGoal;
    private int _failures, _stepsAtPlan, _retryIn, _ticksSincePlan, _learnedReplans;

    /// <summary>Every successful plan's start and target (the requested goal, or a partial target), in order.</summary>
    public readonly List<(TilePoint Start, TilePoint Target, bool Partial)> PlanLog = new();
    /// <summary>The route commitment saw a plan come back to an earlier start without progress.</summary>
    public bool Oscillated => _route.Oscillating;

    public string? Reason { get; private set; }
    public int Plans { get; private set; }
    public int FailedPlans { get; private set; }
    public int Replans { get; private set; }
    /// <summary>The first plan's failure reason (null when it succeeded).</summary>
    public string? FirstPlanFailure { get; private set; }
    public bool FirstPlanDone { get; private set; }
    public int MaxSubmergedTicks { get; private set; }
    /// <summary>Every Stuck/NeedsReplan/plan failure, in order (for the summary).</summary>
    public readonly List<string> Events = new();
    public readonly Queue<string> Trace = new();

    public MoveToSim(ArrayTileGrid g, MiniPhysics sim, TilePoint goal, MovementProfile profile, PathOptions options,
        int tolerance = 1, Action<string>? log = null, FogGrid? fog = null)
    {
        _g = g;
        _fog = fog;
        _view = (ITileGrid?)fog ?? g;
        _sim = sim;
        _goal = goal;
        _profile = profile;
        _options = options;
        _tolerance = tolerance;
        _log = log;
    }

    public MoveOutcome Run(int maxTicks)
    {
        int submerged = 0;
        while (_sim.Ticks < maxTicks)
        {
            var r = Tick();
            if (r is { } done) return done;
            if (_sim.Hurt != null)
            {
                Reason = _sim.Hurt;
                return MoveOutcome.Hurt;
            }
            submerged = HeadSubmerged() ? submerged + 1 : 0;
            MaxSubmergedTicks = Math.Max(MaxSubmergedTicks, submerged);
            if (submerged > DrownTicks)
            {
                Reason = $"head under water for {submerged} ticks at {_sim.StandingTile}";
                return MoveOutcome.Drowning;
            }
        }
        Reason = $"timed out after {maxTicks} ticks at {_sim.StandingTile}";
        return MoveOutcome.TimedOut;
    }

    /// <summary>Vanilla breath: 200, one point lost every 7 ticks with the head under, then damage.</summary>
    public const int DrownTicks = 1400;

    private bool HeadSubmerged()
    {
        int c0 = (int)MathF.Floor((_sim.X + 0.01f) / 16f), c1 = (int)MathF.Floor((_sim.X + MiniPhysics.W - 0.01f) / 16f);
        int row = (int)MathF.Floor((_sim.Y + 0.01f) / 16f);
        for (int c = c0; c <= c1; c++)
            if (_g.Get(c, row).Kind is TileKind.Water or TileKind.Honey) return true;
        return false;
    }

    private void Step(ControlIntent intent, SimTool tool = SimTool.None)
    {
        _sim.Step(intent, tool);
    }

    /// <summary>One game tick of the task. Non-null = finished.</summary>
    private MoveOutcome? Tick()
    {
        var k = _sim.Kinematics;
        var here = NavMath.StandingTile(k, _view);
        _fog?.Reveal(here);
        int dist = Chebyshev(here, _goal);
        if (dist <= _tolerance && k.OnGround) return MoveOutcome.Arrived;
        // The host's stall watchdog (no new tile, no new progress text for 20 s); the sim has no inventory to change.
        if (_watchdog.Tick(this, $"{dist} tiles remaining", here.X, here.Y, 0, paused: false))
            return Fail($"stuck: no progress for {_watchdog.IdleSeconds} s");

        if (_follower == null)
        {
            if (_retryIn > 0)
            {
                _retryIn--;
                Step(default);
                return null;
            }
            return Plan(here);
        }

        _ticksSincePlan++;
        if (_pathCrossesUnknown && _ticksSincePlan >= UnknownReplanTicks)
        {
            // In game the player keeps following the old path while the job copies its window.
            var replanned = Plan(here, holdStill: false);
            if (replanned != null) return replanned;
            if (_follower == null) return null;
        }

        var o = _follower.Tick(k, _view);
        Remember($"{_sim} | {o.Status} {o.Current?.Kind}{o.Current?.Pos}");
        switch (o.Status)
        {
            case FollowerStatus.Arrived:
                _failures = 0;
                _follower = null;
                _route.OnArrived();
                Leg.OnArrived(here);
                if (!_partial && _resolvedGoal is { } rg && Chebyshev(here, rg) <= _tolerance && k.OnGround)
                    return MoveOutcome.Arrived;
                SyncProgress();
                if (_progress.RecordArrival(Leg.Distance(here, _goal)))
                {
                    // Like MoveToTask: a trail route from here instead of failing.
                    if (Leg.TryRescue(here, _goal))
                    {
                        Events.Add($"trail rescue from {here}");
                        SyncProgress();
                        return Plan(here);
                    }
                    return Fail($"unreachable: no progress toward {_goal}");
                }
                if (_partial || Chebyshev(here, _goal) > _tolerance) return Plan(here);
                Step(default);
                return null;
            case FollowerStatus.Stuck:
            case FollowerStatus.NeedsReplan:
                bool progressed = _stepsAtPlan - _follower.RemainingSteps > 0;
                string what = o.Status == FollowerStatus.Stuck ? "stuck" : "path changed";
                string failure = $"{what} at {o.Current?.Kind} {o.Current?.Pos}";
                Events.Add($"{failure} (player {here})");
                _log?.Invoke($"t={_sim.Ticks} {failure} from {here}");
                _follower = null;
                Reason = failure;
                bool learned = o.Status == FollowerStatus.NeedsReplan && _pathCrossesUnknown && ++_learnedReplans <= MaxLearnedReplans;
                if (!progressed && !learned && ++_failures >= MaxConsecutiveFailures) return Fail(failure);
                if (!learned && _repeats.Record(here, failure)) return Fail($"{failure} (repeatedly)");
                if (progressed) _failures = 0;
                Replans++;
                return Plan(here);
        }

        var tool = o.Current?.Kind switch
        {
            MoveKind.Dig => SimTool.Pickaxe,
            // Like BuildReserve.Pick (no reserve here): platforms first, then blocks.
            MoveKind.BuildPlatform or MoveKind.BuildDown => _sim.Platforms > 0 || _sim.Blocks <= 0 ? SimTool.Platform : SimTool.Block,
            _ => SimTool.None,
        };
        Step(o.Intent, tool);
        return null;
    }

    /// <summary>StartPlan + PollPlan: hold still while the job copies its window, then search.</summary>
    private MoveOutcome? Plan(TilePoint here, bool holdStill = true)
    {
        // Like ProfileBuilder: the platforms actually left in the inventory.
        // Water walking as the sim has it now (the buff counts down like ProfileBuilder's buff time).
        var profile = _profile with
        {
            BuildItemCount = Math.Min(_profile.BuildItemCount, _sim.Platforms + _sim.Blocks),
            BuildPlatformCount = Math.Min(_profile.BuildPlatformCount, _sim.Platforms),
            WaterWalk = _profile.WaterWalk && _sim.WaterWalk,
            LavaWalk = _profile.LavaWalk && _sim.LavaWalk,
            WaterWalkTicks = Math.Min(_profile.WaterWalkTicks, _sim.WaterWalkTicks),
        };
        // Like MoveToTask.StartPlan: the next trail waypoint while travelling on the trail, else the goal.
        var target = Leg.PlanGoal(here, _goal);
        bool onTrail = Leg.Target != null;
        var hint = _route.BeforePlan(here, target);
        if (!Commit) hint = null;
        var job = new PlanJob(here, target, profile,
            _options with { GoalTolerance = onTrail ? TrailTravel.ReachedRadius : _tolerance, GoalSet = onTrail ? null : _options.GoalSet }, hint);
        Plans++;
        while (!job.Step(_view)) Thread.Yield();
        // In game the job copies its window over several ticks (the player holds still), then the result is picked up
        // a tick or two later: deterministic stand-in for that wait.
        if (holdStill)
            for (int i = 0; i < job.CopyTicks + 2; i++) Step(default);
        var r = job.Result!;
        if (!FirstPlanDone)
        {
            FirstPlanDone = true;
            FirstPlanFailure = r.Success ? null : r.FailureReason;
        }
        if (!r.Success)
        {
            _follower = null;
            FailedPlans++;
            Events.Add($"plan failed: {r.FailureReason} (from {here})");
            _log?.Invoke($"t={_sim.Ticks} plan failed from {here}: {r.FailureReason}");
            Reason = r.FailureReason;
            // A plan to a trail waypoint failed: back to normal planning at once (not a failure).
            if (Leg.OnPlanFailed())
            {
                _retryIn = 0;
                return null;
            }
            bool deterministic = r.FailureReason?.StartsWith("goal_not_standable") == true;
            if (deterministic || ++_failures >= MaxConsecutiveFailures)
            {
                if (!deterministic && Leg.TryRescue(here, _goal))
                {
                    Events.Add($"trail rescue from {here}");
                    _failures = 0;
                    _retryIn = 0;
                    return null;
                }
                return Fail(r.FailureReason);
            }
            _retryIn = RetryDelayTicks;
            return null;
        }
        _partial = job.IsPartial || onTrail;
        _resolvedGoal = r.ResolvedGoal;
        _route.OnPlanned(job.PlannedGoal, job.IsPartial);
        PlanLog.Add((job.Start, job.PlannedGoal, _partial));
        _follower = new PathFollower(r.Steps, start: Pathfinder.NormalizeStart(_view, job.Start),
            waterWalk: profile.WaterWalk, lavaWalk: profile.LavaWalk);
        _stepsAtPlan = _follower.RemainingSteps;
        _pathCrossesUnknown = CrossesUnknown(_view, r.Steps);
        _ticksSincePlan = 0;
        _log?.Invoke($"t={_sim.Ticks} plan from {job.Start}{(job.IsPartial ? " (partial)" : "")} ({r.Steps.Count}): " +
                     string.Join(" ", r.Steps.Select(s => $"{s.Kind}{s.Pos}{(s.WorkTile is { } w ? "w" + w : "")}")));
        return null;
    }

    /// <summary>The trail started or ended: the progress measure changed meaning, start a fresh guard.</summary>
    private void SyncProgress()
    {
        if (Leg.Epoch == _progressEpoch) return;
        _progressEpoch = Leg.Epoch;
        _progress = new ProgressGuard(maxStalls: 3);
    }

    private MoveOutcome Fail(string? why)
    {
        Reason = why;
        return MoveOutcome.Failed;
    }

    /// <summary>Off: replans ignore the route commitment (to measure what it changes).</summary>
    public bool Commit { get; init; } = true;

    /// <summary>Log every tick too (debugging one case).</summary>
    public bool LogTicks { get; init; }

    private void Remember(string line)
    {
        if (LogTicks) _log?.Invoke(line);
        Trace.Enqueue(line);
        if (Trace.Count > 80) Trace.Dequeue();
    }

    private static bool CrossesUnknown(ITileGrid g, IReadOnlyList<PathStep> steps)
    {
        foreach (var s in steps)
            for (int y = s.Pos.Y - 2; y <= s.Pos.Y + 1; y++)
                for (int x = s.Pos.X; x <= s.Pos.X + 1; x++)
                    if (g.Get(x, y).Kind == TileKind.Unknown) return true;
        return false;
    }

    private static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
