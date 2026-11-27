using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// Seen in game: a non-swimmer on rock at the bottom of a narrow Ebonstone pit (unbreakable for the pickaxe), water in
/// the feet rows (head dry), deeper water beside it and a tall open shaft above with no ledge within jump height.
/// move_to anywhere up: "unreachable: searched 322-450 nodes". The only way out is a platform pillar, and the first
/// platform has to go into the water (vanilla places tiles into liquid).
/// </summary>
public class WetPitClimbTests(ITestOutputHelper output)
{
    private static MovementProfile Real() =>
        JumpModel.Apply(new MovementProfile(PickPower: 35, BuildItemCount: 50), PlayerPhysics.Vanilla);

    /// <summary>The pit: walls 'X' at x=0 and x=5, a 12-high open shaft (x=1..4) up to the surface row 3, water in rows
    /// 15..16 (<paramref name="depth"/> = 2) or row 16 only (1), deeper water at x=1..2 down to row 18, rock under the
    /// stand at x=3..4. The goal is on the surface to the right.</summary>
    internal static (ArrayTileGrid G, TilePoint S, TilePoint Goal) Pit(int depth, bool standByLeftWall = false)
    {
        var b = new GridBuilder(12, 21).Fill(0, 0, 11, 20, '.')
            .Fill(0, 3, 0, 20, 'X').Fill(5, 3, 11, 20, 'X').Fill(0, 19, 11, 20, 'X');
        if (standByLeftWall)
        {
            // Mirror: rock under x=1..2 (next to the left wall), deep water at x=3..4.
            b.Fill(1, 17, 2, 18, 'X').Fill(3, 17, 4, 18, '~');
        }
        else b.Fill(3, 17, 4, 18, 'X').Fill(1, 17, 2, 18, '~');
        b.Fill(1, 17 - depth, 4, 16, '~');
        var (g, _) = b.Parse();
        var s = new TilePoint(standByLeftWall ? 1 : 3, 16);
        return (g, s, new TilePoint(7, 2));
    }

    private PathResult Find(ArrayTileGrid g, TilePoint s, TilePoint goal, MovementProfile p, PathOptions o)
    {
        var r = Pathfinder.FindPath(g, s, goal, p, o);
        output.WriteLine($"{r.Success} {r.FailureReason} " + string.Join(" ", r.Steps.Select(st => $"{st.Kind}{st.Pos}{st.WorkTile}")));
        return r;
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(1, true)]
    public void Planner_PillarsOutOfTheWetPit(int depth, bool byLeftWall)
    {
        var (g, s, goal) = Pit(depth, byLeftWall);
        Assert.Equal(TileKind.Water, g.Get(s.X, s.Y).Kind);   // feet wet
        Assert.NotEqual(TileKind.Water, g.Get(s.X, s.Y - 2).Kind); // head dry
        var r = Find(g, s, goal, Real(), new PathOptions());
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(goal, r.Steps[^1].Pos);
        Assert.Contains(r.Steps, st => st.Kind == MoveKind.BuildPlatform);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(1, true)]
    public void Planner_LowJump_FirstPlatformGoesIntoTheWater(int depth, bool byLeftWall)
    {
        // A jump too low to get the feet above the water's top row plus a dry platform cell: the first platform has
        // to be placed into a water cell (vanilla displaces the liquid).
        var (g, s, goal) = Pit(depth, byLeftWall);
        var r = Find(g, s, goal, new MovementProfile(MaxJumpUp: depth + 1, PickPower: 35, BuildItemCount: 50), new PathOptions());
        Assert.True(r.Success, r.FailureReason);
        var first = r.Steps.First(st => st.Kind == MoveKind.BuildPlatform);
        Assert.Equal(TileKind.Water, g.Get(first.WorkTile!.Value).Kind);
    }

    [Fact]
    public void Planner_NoBuilding_StillUnreachable()
    {
        var (g, s, goal) = Pit(2);
        var r = Find(g, s, goal, Real(), new PathOptions(AllowBuild: false));
        Assert.False(r.Success);
    }

    [Fact]
    public void Planner_SubmergedHead_DoesNotBuildFromTheBottom()
    {
        // Three rows of water: the head is under, the rule (head dry) doesn't apply; no platform goes in from there.
        var (g, s, goal) = Pit(3);
        var r = Find(g, s, goal, Real(), new PathOptions());
        if (r.Success)
            Assert.DoesNotContain(r.Steps.Take(1), st => st.Kind == MoveKind.BuildPlatform);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(1, true)]
    public void Sim_WalksOutOfTheWetPit(int depth, bool byLeftWall)
    {
        var (g, s, goal) = Pit(depth, byLeftWall);
        var sim = new MiniPhysics(g, s) { PickPower = 35, Platforms = 50 };
        var move = new MoveToSim(g, sim, goal, Real(), new PathOptions(), tolerance: 0, log: output.WriteLine);
        var outcome = move.Run(6000);
        if (outcome != MoveOutcome.Arrived)
            foreach (var l in move.Trace) output.WriteLine(l);
        output.WriteLine($"{outcome}: {move.Reason} after {sim.Ticks} ticks, {move.Replans} replans, placed {sim.Placed.Count}");
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.Null(sim.Hurt);
        Assert.Equal(0, sim.Misuse);
        Assert.True(sim.StandingTile.Y <= 2, $"still in the pit: {sim.StandingTile}");
    }

    [Theory]
    [InlineData('X', true)]
    [InlineData(')', false)] // unbreakable floor slope: the feet start sunk, one tile of the water jump lost
    public void WaterJump_FromASlope_RisesOneTileLess(char floor, bool reachable)
    {
        // A flooded 2-wide shaft (x=1..2, rows 2..12) with its floor at row 13; the only exit is a ledge whose feet row
        // (4) is exactly MaxWaterJumpUp (8) above the floor's feet row (12).
        var (g, _) = new GridBuilder(10, 15).Fill(0, 0, 9, 14, 'X').Fill(1, 0, 9, 4, '.').Fill(1, 2, 2, 12, '~')
            .Fill(1, 13, 2, 13, floor).Parse();
        var p = new MovementProfile(PickPower: 0, BuildItemCount: 0);
        Assert.Equal(8, p.MaxWaterJumpUp);
        var r = Find(g, new TilePoint(1, 12), new TilePoint(6, 4), p, new PathOptions(AllowDig: false, AllowBuild: false));
        Assert.Equal(reachable, r.Success);
    }
}
