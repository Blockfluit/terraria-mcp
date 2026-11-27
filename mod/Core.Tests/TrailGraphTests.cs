using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class TrailGraphTests
{
    private static void Walk(TrailGraph g, int x0, int x1, int y, bool surface = true)
    {
        int step = x1 >= x0 ? 1 : -1;
        for (int x = x0; x != x1 + step; x += step) g.Record(new TilePoint(x, y), surface);
    }

    [Fact]
    public void SamplesEverySpacingTiles_AndLinksInOrder()
    {
        var g = new TrailGraph();
        Walk(g, 0, 80, 50);
        Assert.Equal(11, g.Count); // 0, 8, ..., 80
        for (int i = 0; i + 1 < g.Count; i++)
        {
            Assert.True(g.Walked(i, i + 1));
            Assert.False(g.Walked(i + 1, i));
            Assert.Contains(i, g.Neighbours(i + 1));
        }
    }

    [Fact]
    public void WalkingTheSameRoadAgain_AddsNoNodes_AndMarksBothWays()
    {
        var g = new TrailGraph();
        Walk(g, 0, 80, 50);
        int n = g.Count;
        Walk(g, 80, 0, 50);
        Assert.Equal(n, g.Count);
        Assert.True(g.Walked(1, 0));
        Assert.True(g.Walked(0, 1));
    }

    [Fact]
    public void ATunnelUnderTheRoad_IsNotMerged()
    {
        var g = new TrailGraph();
        Walk(g, 0, 40, 50);
        int n = g.Count;
        g.Break();
        Walk(g, 0, 40, 54, surface: false); // 4 rows below
        Assert.Equal(2 * n, g.Count);
    }

    [Fact]
    public void APositionJump_BreaksTheChain()
    {
        var g = new TrailGraph();
        Walk(g, 0, 16, 50);
        g.Record(new TilePoint(500, 50), true); // teleport
        Walk(g, 500, 516, 50);
        int far = g.Nearest(new TilePoint(500, 50), 2, out _);
        int near = g.Nearest(new TilePoint(16, 50), 2, out _);
        Assert.DoesNotContain(near, g.Neighbours(far));
        var r = g.Route(new TilePoint(0, 50), new TilePoint(520, 50))!;
        Assert.All(r.Waypoints, w => Assert.True(w.X <= 16)); // the far chain can't be reached over the trail
    }

    [Fact]
    public void Route_EndsAtTheNodeNearestTheGoal()
    {
        var g = new TrailGraph();
        Walk(g, 0, 400, 50);
        var r = g.Route(new TilePoint(3, 50), new TilePoint(600, 60));
        Assert.NotNull(r);
        Assert.Equal(new TilePoint(400, 50), r!.Waypoints[^1]);
        Assert.True(Math.Abs(r.Waypoints[0].X - 3) <= TrailGraph.StartRadius);
        Assert.True(r.EndToGoal < 201);
    }

    [Fact]
    public void Route_NullWithoutANodeNearTheStart_OrWithoutGain()
    {
        var g = new TrailGraph();
        Walk(g, 0, 400, 50);
        Assert.Null(g.Route(new TilePoint(0, 120), new TilePoint(600, 60))); // 70 rows below the trail
        Assert.Null(g.Route(new TilePoint(400, 50), new TilePoint(600, 50))); // already at the trail's end
    }

    [Fact]
    public void Route_PrefersTheSurfaceRoad()
    {
        // Two roads from A (0,50) to B (200,50): a surface one with a 30-row hill, and a flat tunnel below.
        var g = new TrailGraph();
        Walk(g, 0, 60, 50);
        for (int y = 50; y >= 30; y--) g.Record(new TilePoint(60, y), true);
        Walk(g, 60, 140, 30);
        for (int y = 30; y <= 50; y++) g.Record(new TilePoint(140, y), true);
        Walk(g, 140, 200, 50);
        g.Break();
        Walk(g, 0, 200, 58, surface: false);
        for (int y = 58; y >= 50; y--) g.Record(new TilePoint(200, y), false); // climbs up to the road's end
        var r = g.Route(new TilePoint(0, 50), new TilePoint(196, 0))!; // the road's end node is (196,50)
        Assert.Equal(new TilePoint(196, 50), r.Waypoints[^1]);
        Assert.All(r.Waypoints, w => Assert.True(w.Y <= 50, $"{w} is on the tunnel"));
    }

    [Fact]
    public void ClimbingBackUpAFall_CostsExtra()
    {
        var g = new TrailGraph();
        g.Record(new TilePoint(0, 10), true);
        g.Record(new TilePoint(4, 20), true); // fell 10 rows (breaks? no: 10 <= BreakDistance)
        Assert.Equal(2, g.Count);
        Assert.True(g.EdgeCost(1, 0) > g.EdgeCost(0, 1) + 15);
    }
}

public class TrailTravelTests
{
    private static TrailGraph Road(int x0, int x1, int y)
    {
        var g = new TrailGraph();
        for (int x = x0; x <= x1; x++) g.Record(new TilePoint(x, y), true);
        return g;
    }

    [Fact]
    public void NearGoal_DoesNotUseTheTrail()
    {
        var g = Road(0, 400, 50);
        Assert.Null(TrailTravel.ForFarGoal(g, new TilePoint(0, 50), new TilePoint(100, 50)));
        Assert.Null(TrailTravel.ForFarGoal(null, new TilePoint(0, 50), new TilePoint(1000, 50)));
    }

    [Fact]
    public void FarGoal_HopsAlongTheTrail()
    {
        var g = Road(0, 400, 50);
        var t = TrailTravel.ForFarGoal(g, new TilePoint(0, 50), new TilePoint(600, 50))!;
        Assert.NotNull(t);
        var here = new TilePoint(0, 50);
        var hops = new List<TilePoint>();
        while (t.NextTarget(here) is { } w)
        {
            Assert.True(Math.Abs(w.X - here.X) <= TrailTravel.HopX);
            int before = t.Remaining(here, new TilePoint(600, 50));
            hops.Add(w);
            t.OnReached(w);
            here = w;
            Assert.True(t.Remaining(here, new TilePoint(600, 50)) < before);
            Assert.True(hops.Count < 20);
        }
        Assert.Equal(new TilePoint(400, 50), hops[^1]);
        Assert.Equal(5, hops.Count); // 80 tiles per hop
        Assert.True(t.Done);
    }

    [Fact]
    public void Hops_StopWhereTheTrailLeavesTheBox()
    {
        // The trail goes 100 rows down a shaft, then east: a hop never skips past the part outside the box.
        var g = new TrailGraph();
        for (int y = 10; y <= 110; y++) g.Record(new TilePoint(0, y), false);
        for (int x = 0; x <= 300; x++) g.Record(new TilePoint(x, 110), false);
        var t = TrailTravel.ForFarGoal(g, new TilePoint(0, 10), new TilePoint(400, 110))!;
        var w = t.NextTarget(new TilePoint(0, 10))!.Value;
        Assert.Equal(0, w.X);
        Assert.True(w.Y - 10 <= TrailTravel.HopY);
    }

    [Fact]
    public void AbsurdDetour_IsNotTakenForAFarGoal_ButRescueTakesIt()
    {
        // The only trail from here goes 600 tiles west, loops and comes back 30 rows lower toward a goal 150 east.
        var g = new TrailGraph();
        for (int x = 0; x >= -600; x--) g.Record(new TilePoint(x, 50), true);
        for (int y = 50; y <= 80; y++) g.Record(new TilePoint(-600, y), false);
        for (int x = -600; x <= 140; x++) g.Record(new TilePoint(x, 80), false);
        var goal = new TilePoint(150, 50);
        Assert.Null(TrailTravel.ForFarGoal(g, new TilePoint(0, 50), goal));
        Assert.NotNull(TrailTravel.ForRescue(g, new TilePoint(0, 50), goal));
    }

    [Fact]
    public void Leg_FallsBackWhenAWaypointPlanFails_AndRescuesAtMostTwice()
    {
        var g = Road(0, 400, 50);
        var leg = new TrailLeg(g);
        var goal = new TilePoint(600, 50);
        var here = new TilePoint(0, 50);
        int e0 = leg.Epoch;
        var target = leg.PlanGoal(here, goal);
        Assert.NotEqual(goal, target);
        Assert.True(leg.Active);
        Assert.NotEqual(e0, leg.Epoch);
        Assert.True(leg.OnPlanFailed());
        Assert.False(leg.Active);
        Assert.Equal(goal, leg.PlanGoal(here, goal)); // the far-goal trail is tried once per goal
        Assert.False(leg.OnPlanFailed());
        Assert.True(leg.TryRescue(here, goal));
        Assert.NotEqual(goal, leg.PlanGoal(here, goal));
        Assert.True(leg.OnPlanFailed());
        Assert.True(leg.TryRescue(here, goal));
        Assert.False(leg.TryRescue(here, goal));
    }

    [Fact]
    public void Leg_DistanceFollowsTheTrailWhileOnIt()
    {
        var g = Road(0, 400, 50);
        var leg = new TrailLeg(g);
        var goal = new TilePoint(600, 50);
        var here = new TilePoint(0, 50);
        var t = leg.PlanGoal(here, goal);
        Assert.Equal(600, leg.Distance(here, goal));
        leg.OnArrived(t);
        Assert.Equal(600 - t.X, leg.Distance(t, goal));
    }
}

public class TrailLegArrivalTests
{
    [Fact]
    public void APlanCutShort_DoesNotSkipItsWaypoint()
    {
        var g = new TrailGraph();
        for (int x = 0; x <= 400; x++) g.Record(new TilePoint(x, 50), true);
        var leg = new TrailLeg(g);
        var goal = new TilePoint(600, 50);
        var t = leg.PlanGoal(new TilePoint(0, 50), goal);
        Assert.Equal(new TilePoint(80, 50), t);
        leg.OnArrived(new TilePoint(50, 50)); // ended 30 tiles short
        Assert.True(leg.Travel!.NextIndex <= 10, $"next {leg.Travel.NextIndex}"); // (80,50) is waypoint 10
        Assert.True(leg.Distance(new TilePoint(50, 50), goal) >= 550);
        leg.OnArrived(new TilePoint(79, 50));
        Assert.Equal(11, leg.Travel.NextIndex);
    }
}
