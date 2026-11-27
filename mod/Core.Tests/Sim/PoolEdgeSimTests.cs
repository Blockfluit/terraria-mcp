using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>The follower really gets out of the in-game pool edge (<see cref="PoolEdgeTests.Map"/>) in MiniPhysics,
/// replanning from where it stands like MoveToTask does.</summary>
public class PoolEdgeSimTests(ITestOutputHelper output)
{
    private static MovementProfile Real(bool waterBreathing) =>
        JumpModel.Apply(new MovementProfile(PickPower: 35, BuildItemCount: 50, WaterBreathing: waterBreathing),
            PlayerPhysics.Vanilla);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FollowerLeavesThePoolEdge_UpThePassage(bool waterBreathing)
    {
        var (g, m) = ArrayTileGrid.Parse(PoolEdgeTests.Map);
        var start = m['S'];
        var goal = new TilePoint(start.X - 1, start.Y - 4);
        var profile = Real(waterBreathing);
        PathResult Plan(TilePoint from)
        {
            var p = Pathfinder.FindPath(g, from, goal, profile, new());
            output.WriteLine($"plan from {from}: {p.Success} {p.FailureReason} " +
                             string.Join(" ", p.Steps.Select(s => $"{s.Kind}{s.Pos}{s.WorkTile}")));
            Assert.True(p.Success, p.FailureReason);
            return p;
        }

        var plan = Plan(start);
        var sim = new MiniPhysics(g, start);
        var f = new PathFollower(plan.Steps, start: start);
        FollowerOutput o = default;
        int replans = 0;
        for (int t = 0; t < 3000; t++)
        {
            o = f.Tick(sim.Kinematics, g);
            if (o.Status == FollowerStatus.Arrived) break;
            if (o.Status != FollowerStatus.Running)
            {
                output.WriteLine($"t={t} {o.Status} at {o.Current?.Kind}{o.Current?.Pos}; {sim}");
                Assert.True(++replans <= 2, $"too many replans ({o.Status})");
                plan = Plan(sim.StandingTile);
                f = new PathFollower(plan.Steps, start: sim.StandingTile);
                continue;
            }
            var tool = o.Current?.Kind switch
            {
                MoveKind.Dig => SimTool.Pickaxe,
                MoveKind.BuildPlatform => SimTool.Platform,
                _ => SimTool.None,
            };
            sim.Step(o.Intent, tool);
            Assert.Null(sim.Hurt);
        }
        output.WriteLine($"{o.Status} {sim} replans {replans}");
        Assert.Equal(FollowerStatus.Arrived, o.Status);
        for (int i = 0; i < 40; i++) sim.Step(default);
        Assert.Equal(plan.Steps[^1].Pos, sim.StandingTile);
        Assert.True(sim.StandingTile.Y <= start.Y - 3, $"still at the pool: {sim.StandingTile}");
        Assert.Equal(0, sim.Misuse);
    }
}
