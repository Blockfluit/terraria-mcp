using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// Long trips over the travel memory (<see cref="TrailGraph"/>, <see cref="TrailLeg"/>), walked like move_to
/// (<see cref="MoveToSim"/>). Seen in game: move_to 3400 tiles east ended "unreachable: no progress" in an underground
/// dead end four times; walking it through surface spots walked before worked.
/// </summary>
public class TrailTravelSimTests(ITestOutputHelper output)
{
    private const int W = 520, H = 140;
    private static readonly TilePoint Start = new(10, 40), Goal = new(490, 124);

    /// <summary>
    /// Unbreakable rock with a flat surface (feet row 40). A platform shaft at x 20..22 leads down to a long tunnel
    /// (feet row 104) running east to a dead end at x 420: from the start its cells are the known ground nearest the
    /// goal, so partial plans head down there. The goal cave (x 470..500, feet row 124) is reached only by a second
    /// shaft from the surface at x 480..482.
    /// </summary>
    private static ArrayTileGrid World()
    {
        var b = new GridBuilder(W, H).Fill(0, 0, W - 1, H - 1, 'X').Fill(1, 1, W - 2, 40, '.');
        Shaft(b, 20, 104);
        b.Fill(20, 100, 420, 104, '.');
        Shaft(b, 480, 124);
        b.Fill(470, 120, 500, 124, '.');
        return b.Parse().Grid;
    }

    private static void Shaft(GridBuilder b, int x, int bottom)
    {
        b.Fill(x, 41, x + 2, bottom, '.');
        for (int y = 41; y < bottom - 2; y += 4) b.Fill(x, y, x + 2, y, '=');
    }

    /// <summary>What the player walked before: start → east along the surface → down the east shaft into the goal cave
    /// (and, with <paramref name="tunnel"/>, earlier: down the west shaft and along the tunnel to its dead end).</summary>
    private static TrailGraph Trail(bool tunnel)
    {
        var g = new TrailGraph();
        if (tunnel)
        {
            for (int x = 10; x <= 21; x++) g.Record(new(x, 40), true);
            for (int y = 44; y <= 104; y += 4) g.Record(new(21, y), false);
            for (int x = 21; x <= 418; x++) g.Record(new(x, 104), false);
            g.Break(); // recalled home
        }
        for (int x = 10; x <= 481; x++) g.Record(new(x, 40), true);
        for (int y = 44; y <= 124; y += 4) g.Record(new(481, y), false);
        for (int x = 481; x <= 492; x++) g.Record(new(x, 124), false);
        return g;
    }

    private (MoveOutcome, MoveToSim) Walk(TilePoint start, TrailGraph? trail)
    {
        var truth = World();
        var sim = new MiniPhysics(truth, start) { PickPower = 35, Platforms = 150 };
        var move = new MoveToSim(truth, sim, Goal, RealTerrainFuzzTests.Profile(), new PathOptions()) { Trail = trail };
        var outcome = move.Run(60000);
        foreach (var (s, t, partial) in move.PlanLog) output.WriteLine($"plan {s} -> {t}{(partial ? " (partial)" : "")}");
        foreach (var e in move.Events) output.WriteLine(e);
        output.WriteLine($"{outcome}: {move.Reason} after {sim.Ticks} ticks, {move.Plans} plans, trail routes {move.TrailRoutes}");
        return (outcome, move);
    }

    [Fact]
    public void WithoutTrail_PartialPlansEndInTheDeadEnd()
    {
        var (outcome, move) = Walk(Start, null);
        Assert.Equal(MoveOutcome.Failed, outcome);
        Assert.Contains(move.PlanLog, p => p.Target.Y == 104);
    }

    [Fact]
    public void WithTrail_FarGoalFollowsTheSurfaceTrail()
    {
        var (outcome, move) = Walk(Start, Trail(tunnel: true));
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.Equal(1, move.TrailRoutes);
        Assert.DoesNotContain(move.PlanLog, p => p.Target.Y == 104 && p.Target.X < 470);
    }

    [Fact]
    public void FromTheDeadEnd_NoProgressRescuesOverTheTrail()
    {
        // The goal is inside one window from here, so the trip starts without the trail; every plan fails (the way
        // round is outside any window) and the walk takes the trail back instead of failing.
        var (outcome, move) = Walk(new TilePoint(410, 104), Trail(tunnel: true));
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.Contains(move.Events, e => e.StartsWith("trail rescue"));
    }
}
