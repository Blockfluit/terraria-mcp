using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// Replan hysteresis (<see cref="RouteCommitment"/>). Seen in game (west caves around (3935..3975, 930..965), a pool,
/// move_to a goal ~140 tiles below): a plan from the pool's right side headed left; the replan from the left side
/// headed right again, and so on ("138 → 145 tiles remaining" and back) until the stall watchdog stopped the task,
/// twice. The planning window is anchored at the start (32 rows above it, 127 below), so a start a few rows lower sees
/// a few rows deeper: a deeper known spot on the far side came into the window from the low end and dropped out of it
/// from the high end, and each replan chose its partial target from scratch.
/// </summary>
public class RouteCommitmentTests(ITestOutputHelper output)
{
    private const int W = 80, H = 200;

    /// <summary>
    /// A high plateau (feet row 40, x 46..70) stepping down to a low one (feet row 47, x 12..38). Platform ladders go
    /// down on the left (x 12..14, into a pocket with its floor at row 161: the shallower target, ~33 tiles from the goal)
    /// and on the right (x 66..68, into a tunnel with its floor at row 168: the deeper target, ~27 tiles from the goal).
    /// The goal is a cave at (40,190), beyond every window from the plateaus. From the high plateau the tunnel is below
    /// the window (the pocket wins); from the low plateau it is inside (the tunnel wins). The rock is unbreakable down
    /// to the targets; below them it can be dug, except a wall between the two sides down to row 180, so a plan from
    /// the low plateau to the tunnel climbs back up and goes round by the right ladder. One row of each ladder is
    /// unrevealed: every plan crosses Unknown and is redone every 120 ticks on the way, like in game.
    /// </summary>
    private static (ArrayTileGrid Truth, bool[,] Known) World(int pocketFloor = 161)
    {
        var b = new GridBuilder(W, H).Fill(0, 0, W - 1, H - 1, 'X').Fill(1, pocketFloor, W - 2, H - 2, '#')
            .Fill(40, pocketFloor, 42, 180, 'X');
        for (int x = 12; x <= 70; x++)
        {
            int floor = x >= 46 ? 41 : x >= 39 ? 41 + (46 - x) : 48;
            b.Fill(x, 33, x, floor - 1, '.');
        }
        // left ladder and pocket
        b.Fill(12, 48, 14, pocketFloor - 1, '.');
        for (int y = 48; y < pocketFloor - 5; y += 5) b.Fill(12, y, 14, y, '=');
        b.Fill(12, pocketFloor - 5, 28, pocketFloor - 1, '.');
        // right ladder and tunnel
        b.Fill(66, 41, 68, 167, '.');
        for (int y = 41; y < 164; y += 5) b.Fill(66, y, 68, y, '=');
        b.Fill(54, 164, 68, 167, '.');
        // goal cave
        b.Fill(35, 186, 45, 190, '.');
        var truth = b.Parse().Grid;
        var known = new bool[W, H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                known[x, y] = !(y == 120 && (x is >= 12 and <= 14 || x is >= 66 and <= 68));
        return (truth, known);
    }

    private static readonly TilePoint Start = new(60, 40), Goal = new(40, 190);

    private MoveToSim Walk(out MoveOutcome outcome, out MiniPhysics sim, int pocketFloor = 161)
    {
        var (truth, known) = World(pocketFloor);
        sim = new MiniPhysics(truth, Start) { PickPower = 35, Platforms = 150 };
        var move = new MoveToSim(truth, sim, Goal, RealTerrainFuzzTests.Profile(), new PathOptions(), log: output.WriteLine,
            fog: new FogGrid(truth, known));
        outcome = move.Run(30000);
        foreach (var (s, t, partial) in move.PlanLog) output.WriteLine($"plan {s} -> {t}{(partial ? " (partial)" : "")}");
        output.WriteLine($"{outcome}: {move.Reason} after {sim.Ticks} ticks, {move.Plans} plans, oscillated {move.Oscillated}");
        return move;
    }

    /// <summary>Without the commitment: "stuck: no progress for 20 s" after 15 plans alternating (57,40) → pocket and
    /// (42,35) → right ladder (mid-jump on the step the window is a few rows higher still). With it the pocket is kept
    /// (the tunnel is only ~6 tiles closer to the goal). With a shallower pocket (floor 151) the right side wins from the
    /// start: the first plan heads for the right ladder and a replan on the way down switches to the tunnel, which is
    /// more than the margin closer.</summary>
    [Theory]
    [InlineData(161, 0)]
    [InlineData(151, 1)]
    public void FarGoalBelow_ReplansKeepThePartialTarget(int pocketFloor, int maxSwaps)
    {
        var move = Walk(out var outcome, out var sim, pocketFloor);
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.Null(sim.Hurt);
        // Partial targets don't flip back and forth between the sides.
        var sides = move.PlanLog.Where(p => p.Partial).Select(p => p.Target.X < 40 ? 'L' : 'R').ToList();
        int swaps = sides.Zip(sides.Skip(1)).Count(z => z.First != z.Second);
        Assert.True(swaps <= maxSwaps, $"partial targets swapped sides {swaps} times: {new string(sides.ToArray())}");
    }

    /// <summary>The PlanJob half: with the pocket as the committed target, a plan from the low plateau (whose window
    /// holds the slightly better tunnel) keeps the pocket; from the high plateau (whose window doesn't reach the tunnel)
    /// a committed tunnel target widens the window and is kept too.</summary>
    [Theory]
    [InlineData(25, 47, 27, 160)]
    [InlineData(60, 40, 54, 167)]
    public void PlanJob_KeepsTheHintedTarget(int sx, int sy, int tx, int ty)
    {
        var (truth, _) = World();
        var hint = new RouteHint(new TilePoint(tx, ty), RouteCommitment.SwitchMargin);
        var job = new PlanJob(new(sx, sy), Goal, RealTerrainFuzzTests.Profile(), new PathOptions(), hint);
        while (!job.Step(truth)) Thread.Yield();
        Assert.True(job.Result!.Success, job.Result.FailureReason);
        Assert.True(job.IsPartial);
        Assert.Equal(new TilePoint(tx, ty), job.PlannedGoal);
    }

    /// <summary>Without a hint the two plateaus pick opposite sides: the flip the commitment exists for.</summary>
    [Theory]
    [InlineData(25, 47, false)]
    [InlineData(60, 40, true)]
    public void PlanJob_WithoutHint_SidesDependOnTheStartRow(int sx, int sy, bool left)
    {
        var (truth, _) = World();
        var job = new PlanJob(new(sx, sy), Goal, RealTerrainFuzzTests.Profile(), new PathOptions());
        while (!job.Step(truth)) Thread.Yield();
        Assert.True(job.Result!.Success, job.Result.FailureReason);
        output.WriteLine($"{job.PlannedGoal}");
        Assert.Equal(left, job.PlannedGoal.X < 40);
    }

    /// <summary>A much better candidate still replaces the committed target.</summary>
    [Fact]
    public void PlanJob_SwitchesForAMuchBetterTarget()
    {
        var (truth, _) = World();
        // a weak hint: a spot on the low plateau, far from the goal
        var hint = new RouteHint(new TilePoint(15, 47), RouteCommitment.SwitchMargin);
        var job = new PlanJob(new(25, 47), Goal, RealTerrainFuzzTests.Profile(), new PathOptions(), hint);
        while (!job.Step(truth)) Thread.Yield();
        Assert.True(job.Result!.Success, job.Result.FailureReason);
        Assert.NotEqual(new TilePoint(15, 47), job.PlannedGoal);
    }
}

public class RouteCommitmentUnitTests
{
    private static readonly TilePoint Goal = new(100, 200);

    [Fact]
    public void HintsThePartialTargetUntilArrival()
    {
        var c = new RouteCommitment();
        Assert.Null(c.BeforePlan(new(10, 10), Goal));
        c.OnPlanned(new(30, 50), partial: true);
        Assert.Equal(new RouteHint(new(30, 50), RouteCommitment.SwitchMargin), c.BeforePlan(new(15, 20), Goal));
        c.OnPlanned(new(30, 50), partial: true);
        c.OnArrived();
        Assert.Null(c.BeforePlan(new(30, 50), Goal));
    }

    [Fact]
    public void CompletePlansAndNewGoalsClearTheTarget()
    {
        var c = new RouteCommitment();
        c.BeforePlan(new(10, 10), Goal);
        c.OnPlanned(new(30, 50), partial: true);
        Assert.Null(c.BeforePlan(new(12, 10), new TilePoint(5, 5)));
        c.OnPlanned(new(5, 5), partial: false);
        Assert.Null(c.BeforePlan(new(8, 8), new TilePoint(5, 5)));
    }

    [Fact]
    public void ComingBackWithoutProgress_CommitsForGood()
    {
        var c = new RouteCommitment();
        c.BeforePlan(new(50, 40), Goal);
        c.OnPlanned(new(10, 160), partial: true);
        c.BeforePlan(new(30, 47), Goal);  // went left and down a little
        c.OnPlanned(new(60, 167), partial: true);
        Assert.False(c.Oscillating);
        var hint = c.BeforePlan(new(51, 40), Goal); // back where the first plan started
        Assert.True(c.Oscillating);
        Assert.Equal(double.PositiveInfinity, hint!.Value.SwitchMargin);
    }

    [Fact]
    public void ComingBackAfterRealProgress_IsNoOscillation()
    {
        var c = new RouteCommitment();
        c.BeforePlan(new(50, 40), Goal);
        c.BeforePlan(new(40, 120), Goal); // well on the way
        c.BeforePlan(new(50, 41), Goal);  // a detour back up (a dead end)
        Assert.False(c.Oscillating);
    }
}
