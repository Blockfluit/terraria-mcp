using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Nav;

/// <summary>What a replan should stick to: the previous partial plan's target, kept unless a new candidate is more than
/// <see cref="SwitchMargin"/> tiles (straight-line distance) closer to the goal. Infinite margin = keep it whenever
/// it is still reachable.</summary>
public readonly record struct RouteHint(TilePoint Target, double SwitchMargin);

/// <summary>
/// Hysteresis for move_to's replans toward a goal beyond the planning window. Each plan used to choose its partial target
/// from scratch, and the window is anchored at the start: seen in game (west caves, a pool, goal ~140 tiles below),
/// a plan from the pool's right side headed left; a replan from the left (a few rows lower, so the window reached a
/// few rows deeper and held a deeper known spot on the right) headed right; climbing back up the right side lost that
/// spot from the window and the next replan headed left again, until the stall watchdog stopped the task.
/// <list type="bullet">
/// <item>Commitment: every replan gets the current partial target as a <see cref="RouteHint"/>. PlanJob widens its
/// window to keep the target in view and plans to it first, unless a new candidate is clearly better.</item>
/// <item>Oscillation: a plan starting where an earlier plan started (within <see cref="RevisitRadius"/>), after the
/// player had been at least <see cref="LeftRadius"/> tiles away from there, and never more than
/// <see cref="MaxSwing"/> tiles closer to the goal in between (in game the swing was 7 rows: 138 ↔ 145 tiles remaining):
/// from then on the target is kept whenever it is reachable (infinite margin).</item>
/// </list>
/// Reaching the end of a plan clears the target (the next partial plan chooses afresh); a new goal resets everything.
/// </summary>
public sealed class RouteCommitment
{
    /// <summary>A new partial target replaces the committed one only when it is this much closer to the goal.</summary>
    public const double SwitchMargin = 10;
    public const int RevisitRadius = 2, LeftRadius = 6, MaxSwing = 20, MaxHistory = 64;

    private readonly List<(TilePoint Pos, int Dist)> _starts = new();
    private TilePoint? _goal;

    /// <summary>The partial target the current plan heads for (null: none, or a complete plan).</summary>
    public TilePoint? Target { get; private set; }

    /// <summary>A plan came back to where an earlier one started without getting closer: commit for good.</summary>
    public bool Oscillating { get; private set; }

    /// <summary>Call when starting a plan from <paramref name="here"/>; pass the result to the <see cref="PlanJob"/>.</summary>
    public RouteHint? BeforePlan(TilePoint here, TilePoint goal)
    {
        if (_goal != goal)
        {
            _goal = goal;
            _starts.Clear();
            Target = null;
            Oscillating = false;
        }
        int dist = Chebyshev(here, goal);
        if (!Oscillating && Revisits(here, dist)) Oscillating = true;
        _starts.Add((here, dist));
        if (_starts.Count > MaxHistory) _starts.RemoveAt(0);
        if (Target is not { } t || Chebyshev(here, t) <= 1) return null;
        return new RouteHint(t, Oscillating ? double.PositiveInfinity : SwitchMargin);
    }

    /// <summary>A plan succeeded: remember its target if it was partial.</summary>
    public void OnPlanned(TilePoint plannedGoal, bool partial) => Target = partial ? plannedGoal : null;

    /// <summary>The follower reached the end of the plan.</summary>
    public void OnArrived() => Target = null;

    /// <summary>An earlier plan start near here that the player had left (a later start at least
    /// <see cref="LeftRadius"/> from it), with no plan start since then more than <see cref="MaxSwing"/> closer to
    /// the goal than it (a descent that turned back at a dead end is not a ping-pong).</summary>
    private bool Revisits(TilePoint here, int dist)
    {
        for (int i = 0; i < _starts.Count; i++)
        {
            var (p, d) = _starts[i];
            if (Chebyshev(p, here) > RevisitRadius) continue;
            bool left = false, gained = false;
            for (int k = i + 1; k < _starts.Count; k++)
            {
                if (Chebyshev(_starts[k].Pos, p) >= LeftRadius) left = true;
                if (_starts[k].Dist < d - MaxSwing) gained = true;
            }
            if (left && !gained) return true;
        }
        return false;
    }

    private static int Chebyshev(TilePoint a, TilePoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
