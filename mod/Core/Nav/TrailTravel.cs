using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>
/// move_to's use of the <see cref="TrailGraph"/>: which trail point the next plan heads for, and when to use the trail
/// at all. Pure; MoveToTask (and the test-side MoveToSim) keep one per walk.
/// <list type="bullet">
/// <item><see cref="ForFarGoal"/>: a goal beyond the planning window (<see cref="BeyondWindow"/>) routes over the trail
/// first, when that gets clearly closer to the goal and isn't an absurd detour (<see cref="MaxDetour"/>).</item>
/// <item><see cref="ForRescue"/>: a walk that stopped making progress (or whose plans keep failing) tries the trail
/// instead of failing, whatever the detour.</item>
/// <item><see cref="NextTarget"/>: the furthest waypoint such that it and every waypoint before it (from the next one
/// not reached yet) lie within <see cref="HopX"/> x <see cref="HopY"/> tiles of the player: the planner then plans a
/// complete path to it inside one window, along the part of the trail it skips.</item>
/// </list>
/// After the last waypoint the walk goes on as before (normal plans to the real goal).
/// </summary>
public sealed class TrailTravel
{
    /// <summary>Hop box: a waypoint this far away still fits one planning window with margin.</summary>
    public const int HopX = 80, HopY = 48;
    /// <summary>A waypoint counts as passed when the player stands this close to it.</summary>
    public const int ReachedRadius = 2;
    /// <summary>A far goal uses the trail only when its cost stays below this factor of the straight distance plus
    /// <see cref="DetourSlack"/>.</summary>
    public const double MaxDetour = 2.5, DetourSlack = 64;

    private readonly IReadOnlyList<TilePoint> _waypoints;
    private int _next;

    public TrailTravel(TrailRoute route)
    {
        Route = route;
        _waypoints = route.Waypoints;
    }

    public TrailRoute Route { get; }

    /// <summary>Index of the next waypoint not reached yet.</summary>
    public int NextIndex => _next;
    public bool Done => _next >= _waypoints.Count;

    /// <summary>The goal lies beyond what one plan from <paramref name="here"/> can reach (outside the hop box).</summary>
    public static bool BeyondWindow(TilePoint here, TilePoint goal) =>
        Math.Abs(goal.X - here.X) > PlanJob.MaxWidth - 2 * PlanJob.Margin - 16
        || Math.Abs(goal.Y - here.Y) > PlanJob.MaxHeight - 2 * PlanJob.Margin - 16;

    /// <summary>Trail travel for a far goal, or null (goal not far, no trail near, no gain, too long a detour).</summary>
    public static TrailTravel? ForFarGoal(TrailGraph? trail, TilePoint here, TilePoint goal)
    {
        if (trail == null || trail.Count < 2 || !BeyondWindow(here, goal)) return null;
        if (trail.Route(here, goal, minGain: 2 * TrailGraph.Spacing) is not { } r) return null;
        double straight = TrailGraph.Euclid(here, goal);
        if (r.Cost > MaxDetour * (straight - r.EndToGoal) + DetourSlack) return null;
        var t = new TrailTravel(r);
        t.Skip(here);
        return t.Done ? null : t;
    }

    /// <summary>Trail travel to get unstuck (any detour), or null.</summary>
    public static TrailTravel? ForRescue(TrailGraph? trail, TilePoint here, TilePoint goal)
    {
        if (trail == null || trail.Count < 2) return null;
        if (trail.Route(here, goal, minGain: TrailGraph.Spacing) is not { } r) return null;
        var t = new TrailTravel(r);
        t.Skip(here);
        return t.Done ? null : t;
    }

    /// <summary>Mark waypoints the player stands at (or past: a later one within reach) as reached.</summary>
    public void Skip(TilePoint here)
    {
        for (int i = _waypoints.Count - 1; i >= _next; i--)
            if (TrailGraph.Chebyshev(_waypoints[i], here) <= ReachedRadius)
            {
                _next = i + 1;
                return;
            }
    }

    /// <summary>The waypoint the next plan should head for, or null when the trail is done.</summary>
    public TilePoint? NextTarget(TilePoint here)
    {
        Skip(here);
        if (Done) return null;
        int pick = _next;
        for (int i = _next; i < _waypoints.Count; i++)
        {
            var w = _waypoints[i];
            if (Math.Abs(w.X - here.X) > HopX || Math.Abs(w.Y - here.Y) > HopY) break;
            pick = i;
        }
        return _waypoints[pick];
    }

    /// <summary>The plan to <paramref name="target"/> arrived: it and everything before it are reached.</summary>
    public void OnReached(TilePoint target)
    {
        for (int i = _next; i < _waypoints.Count; i++)
            if (_waypoints[i] == target)
            {
                _next = i + 1;
                return;
            }
    }

    /// <summary>Progress measure for <see cref="ProgressGuard"/> while on the trail: waypoints left (scaled) plus the
    /// end's distance to the goal; lower = closer.</summary>
    public int Remaining(TilePoint here, TilePoint goal)
    {
        if (Done) return TrailGraph.Chebyshev(here, goal);
        double d = TrailGraph.Euclid(here, _waypoints[_next]);
        for (int i = _next; i + 1 < _waypoints.Count; i++) d += TrailGraph.Euclid(_waypoints[i], _waypoints[i + 1]);
        return (int)Math.Round(d + Route.EndToGoal);
    }
}

/// <summary>
/// The trail part of one move_to (MoveToTask and MoveToSim share it, so the sim measures the same loop): before every
/// plan <see cref="PlanGoal"/> says where to plan to (the goal, or the next trail waypoint); the task reports arrivals,
/// failed plans and stalls back. Rules:
/// <list type="bullet">
/// <item>The first plan of a far goal tries <see cref="TrailTravel.ForFarGoal"/> (once per goal).</item>
/// <item>A plan to a waypoint that fails drops the trail (back to normal planning, not counted as a failure).</item>
/// <item><see cref="TryRescue"/> (no progress / plans keep failing): up to <see cref="MaxRescues"/> times per goal, a
/// trail route from where the player is instead of failing.</item>
/// <item><see cref="Epoch"/> changes whenever the trail starts or ends: the progress measure (<see cref="Distance"/>)
/// changes meaning then, so the caller starts a fresh <see cref="ProgressGuard"/>.</item>
/// </list>
/// </summary>
public sealed class TrailLeg
{
    public const int MaxRescues = 2;

    private readonly TrailGraph? _graph;
    private TrailTravel? _travel;
    private TilePoint? _goal;
    private bool _farTried;
    private int _rescues;

    public TrailLeg(TrailGraph? graph) => _graph = graph;

    /// <summary>The waypoint the current plan heads for (null: the plan heads for the goal).</summary>
    public TilePoint? Target { get; private set; }
    public bool Active => _travel != null;
    public int Epoch { get; private set; }
    /// <summary>Trail routes used so far (far-goal start and rescues), for reports.</summary>
    public int Routes { get; private set; }
    public TrailTravel? Travel => _travel;

    /// <summary>Where the next plan should go: the next waypoint while on the trail, else <paramref name="goal"/>.</summary>
    public TilePoint PlanGoal(TilePoint here, TilePoint goal)
    {
        if (_goal != goal)
        {
            _goal = goal;
            _farTried = false;
            _rescues = 0;
            if (_travel != null) End();
        }
        if (!_farTried)
        {
            _farTried = true;
            if (TrailTravel.ForFarGoal(_graph, here, goal) is { } t) Begin(t);
        }
        Target = _travel?.NextTarget(here);
        if (Target == null && _travel != null) End();
        return Target ?? goal;
    }

    /// <summary>A plan failed. True when it was a plan to a waypoint: the trail is dropped and the caller should plan
    /// again at once (normally) without counting a failure.</summary>
    public bool OnPlanFailed()
    {
        if (Target == null) return false;
        End();
        return true;
    }

    /// <summary>A plan that ended this close to its waypoint reached it (the goal may have been snapped a little).</summary>
    public const int ArrivedRadius = 4;

    /// <summary>The follower reached the end of the current plan. The waypoint counts as reached only when the player
    /// is near it: a plan cut short (search budget, best effort) ends elsewhere and the same waypoint is tried again.</summary>
    public void OnArrived(TilePoint here)
    {
        if (_travel == null) return;
        if (Target is { } t && TrailGraph.Chebyshev(here, t) <= ArrivedRadius) _travel.OnReached(t);
        _travel.Skip(here);
    }

    /// <summary>The progress measure: trail distance left while on the trail, else the Chebyshev distance.</summary>
    public int Distance(TilePoint here, TilePoint goal) =>
        _travel?.Remaining(here, goal) ?? Math.Max(Math.Abs(here.X - goal.X), Math.Abs(here.Y - goal.Y));

    /// <summary>The walk is about to fail for lack of progress: switch to a trail route from here if there is one.</summary>
    public bool TryRescue(TilePoint here, TilePoint goal)
    {
        if (_rescues >= MaxRescues) return false;
        _rescues++;
        if (TrailTravel.ForRescue(_graph, here, goal) is not { } t) return false;
        _goal = goal;
        _farTried = true;
        Begin(t);
        return true;
    }

    private void Begin(TrailTravel t)
    {
        _travel = t;
        Routes++;
        Epoch++;
    }

    private void End()
    {
        _travel = null;
        Target = null;
        Epoch++;
    }
}
