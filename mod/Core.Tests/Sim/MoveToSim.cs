using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>How a simulated move_to ended.</summary>
public enum MoveOutcome { Arrived, Failed, Hurt, Drowning, TimedOut }

/// <summary>
/// A test-side copy of MoveToTask's control loop (mod/LlmPlayer/Tasks/MoveToTask.cs) driving <see cref="MiniPhysics"/>:
/// plans with a real <see cref="PlanJob"/> (snapshot window, partial plans), holds still while the job copies tiles
/// (one tick per <see cref="PlanJob.TilesPerTick"/> tiles, like in game), follows with a fresh
/// <see cref="PathFollower"/> per plan, replans on Stuck/NeedsReplan and at the end of partial plans, gives up after
/// <see cref="MaxConsecutiveFailures"/> failed replans or a <see cref="ProgressGuard"/> stall. Keep it in step with
/// MoveToTask: the real-terrain fuzz measures exactly this loop.
/// </summary>
public sealed class MoveToSim
{
    public const int MaxConsecutiveFailures = 5, RetryDelayTicks = 20;

    private readonly ArrayTileGrid _g;
    private readonly MiniPhysics _sim;
    private readonly TilePoint _goal;
    private readonly MovementProfile _profile;
    private readonly PathOptions _options;
    private readonly int _tolerance;
    private readonly Action<string>? _log;

    private readonly ProgressGuard _progress = new(maxStalls: 3);
    private readonly ReplanGuard _repeats = new();
    private PathFollower? _follower;
    private bool _partial;
    private TilePoint? _resolvedGoal;
    private int _failures, _stepsAtPlan, _retryIn;

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
        int tolerance = 1, Action<string>? log = null)
    {
        _g = g;
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
        var here = NavMath.StandingTile(k, _g);
        if (Chebyshev(here, _goal) <= _tolerance && k.OnGround) return MoveOutcome.Arrived;

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

        var o = _follower.Tick(k, _g);
        Remember($"{_sim} | {o.Status} {o.Current?.Kind}{o.Current?.Pos}");
        switch (o.Status)
        {
            case FollowerStatus.Arrived:
                _failures = 0;
                _follower = null;
                if (!_partial && _resolvedGoal is { } rg && Chebyshev(here, rg) <= _tolerance && k.OnGround)
                    return MoveOutcome.Arrived;
                if (_progress.RecordArrival(Chebyshev(here, _goal)))
                    return Fail($"unreachable: no progress toward {_goal}");
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
                if (!progressed && ++_failures >= MaxConsecutiveFailures) return Fail(failure);
                if (_repeats.Record(here, failure)) return Fail($"{failure} (repeatedly)");
                if (progressed) _failures = 0;
                Replans++;
                return Plan(here);
        }

        var tool = o.Current?.Kind switch
        {
            MoveKind.Dig => SimTool.Pickaxe,
            MoveKind.BuildPlatform or MoveKind.BuildDown => SimTool.Platform,
            _ => SimTool.None,
        };
        Step(o.Intent, tool);
        return null;
    }

    /// <summary>StartPlan + PollPlan: hold still while the job copies its window, then search.</summary>
    private MoveOutcome? Plan(TilePoint here)
    {
        // Like ProfileBuilder: the platforms actually left in the inventory.
        var profile = _profile with { BuildItemCount = Math.Min(_profile.BuildItemCount, _sim.Platforms) };
        var job = new PlanJob(here, _goal, profile, _options with { GoalTolerance = _tolerance });
        Plans++;
        while (!job.Step(_g)) Thread.Yield();
        // In game the job copies its window over several ticks (the player holds still), then the result is picked up
        // a tick or two later: deterministic stand-in for that wait.
        for (int i = 0; i < job.CopyTicks + 2; i++) Step(default);
        var r = job.Result!;
        if (!FirstPlanDone)
        {
            FirstPlanDone = true;
            FirstPlanFailure = r.Success ? null : r.FailureReason;
        }
        if (!r.Success)
        {
            FailedPlans++;
            Events.Add($"plan failed: {r.FailureReason} (from {here})");
            _log?.Invoke($"t={_sim.Ticks} plan failed from {here}: {r.FailureReason}");
            Reason = r.FailureReason;
            bool deterministic = r.FailureReason?.StartsWith("goal_not_standable") == true;
            if (deterministic || ++_failures >= MaxConsecutiveFailures) return Fail(r.FailureReason);
            _retryIn = RetryDelayTicks;
            return null;
        }
        _partial = job.IsPartial;
        _resolvedGoal = r.ResolvedGoal;
        _follower = new PathFollower(r.Steps, start: Pathfinder.NormalizeStart(_g, job.Start));
        _stepsAtPlan = _follower.RemainingSteps;
        _log?.Invoke($"t={_sim.Ticks} plan from {job.Start}{(job.IsPartial ? " (partial)" : "")} ({r.Steps.Count}): " +
                     string.Join(" ", r.Steps.Select(s => $"{s.Kind}{s.Pos}{(s.WorkTile is { } w ? "w" + w : "")}")));
        return null;
    }

    private MoveOutcome Fail(string? why)
    {
        Reason = why;
        return MoveOutcome.Failed;
    }

    /// <summary>Log every tick too (debugging one case).</summary>
    public bool LogTicks { get; init; }

    private void Remember(string line)
    {
        if (LogTicks) _log?.Invoke(line);
        Trace.Enqueue(line);
        if (Trace.Count > 80) Trace.Dequeue();
    }

    private static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
