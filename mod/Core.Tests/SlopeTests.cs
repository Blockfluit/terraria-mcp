using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

/// <summary>Sloped blocks and half blocks: grid parsing, planning (rises/descents along them are walks), the
/// standing tile of a hitbox riding them and start normalisation.</summary>
public class SlopeTests(ITestOutputHelper output)
{
    private static string Plan(PathResult r) => string.Join(" ", r.Steps.Select(s => $"{s.Kind}{s.Pos}"));

    [Fact]
    public void Parse_ReadsShapes()
    {
        var (g, _) = ArrayTileGrid.Parse("/\\_rq#");
        Assert.Equal(TileSlope.DownRight, g.Get(0, 0).Slope);
        Assert.Equal(TileSlope.DownLeft, g.Get(1, 0).Slope);
        Assert.True(g.Get(2, 0).HalfBlock);
        Assert.Equal(TileSlope.UpLeft, g.Get(3, 0).Slope);
        Assert.Equal(TileSlope.UpRight, g.Get(4, 0).Slope);
        Assert.True(g.Get(0, 0).IsLowTop && g.Get(1, 0).IsLowTop && g.Get(2, 0).IsLowTop);
        Assert.False(g.Get(3, 0).IsLowTop || g.Get(4, 0).IsLowTop || g.Get(5, 0).IsLowTop);
        Assert.All(Enumerable.Range(0, 6), x => Assert.True(g.Get(x, 0).IsSolid));
        Assert.Null(g.Get(5, 0).ShapeName);
        Assert.NotNull(g.Get(2, 0).ShapeName);
    }

    [Fact]
    public void SlopedHillside_IsWalkedUpAndDown_NoJumpsNoStepUps()
    {
        var (g, m) = FollowerIntegrationTests.SlopeHill().Parse();
        var noJumps = new MovementProfile(MaxJumpUp: 0, MaxJumpGap: 0);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], noJumps, new PathOptions(AllowDig: false, AllowBuild: false));
        output.WriteLine(Plan(r));
        Assert.True(r.Success, r.FailureReason);
        Assert.All(r.Steps, s => Assert.Equal(MoveKind.Walk, s.Kind));
        Assert.Contains(r.Steps, s => s.Pos.Y == 9); // over the top
    }

    [Fact]
    public void FullBlockStaircase_StillStepsUpAndFalls()
    {
        // The same hill without slopes: 1-tile ledges are StepUp / Fall as before.
        var rows = FollowerIntegrationTests.SlopeHill().Rows.Select(r => r.Replace('/', '#').Replace('\\', '#')).ToArray();
        var (g, m) = ArrayTileGrid.Parse(rows);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new MovementProfile(MaxJumpUp: 0, MaxJumpGap: 0),
            new PathOptions(AllowDig: false, AllowBuild: false));
        Assert.True(r.Success, r.FailureReason);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.StepUp);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.Fall);
    }

    [Fact]
    public void SlopeWallSide_IsStillAStepUp()
    {
        // Walking right into a slope that is high on the left: its left side is a full-height wall.
        var (g, m) = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "..........",
            "..........",
            "S....\\####",
            "##########");
        var r = Pathfinder.FindPath(g, m['S'], new TilePoint(7, 3), new MovementProfile(MaxJumpUp: 0, MaxJumpGap: 0),
            new PathOptions(AllowDig: false, AllowBuild: false));
        output.WriteLine(Plan(r));
        Assert.True(r.Success, r.FailureReason);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.StepUp);
    }

    [Fact]
    public void HalfBlock_IsWalkedOnto()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "..........",
            "S....____.",
            "##########");
        var r = Pathfinder.FindPath(g, m['S'], new TilePoint(6, 2), new MovementProfile(MaxJumpUp: 0, MaxJumpGap: 0),
            new PathOptions(AllowDig: false, AllowBuild: false));
        output.WriteLine(Plan(r));
        Assert.True(r.Success, r.FailureReason);
        Assert.All(r.Steps, s => Assert.Equal(MoveKind.Walk, s.Kind));
    }

    [Fact]
    public void StandingTile_HitboxSunkIntoSlope_StandsOnIt()
    {
        var (g, _) = FollowerIntegrationTests.InGameDiagonal().Parse();
        // Slope '/' at (10, 11), full blocks below; the hitbox's right corner rides it 0.4 tiles into row 11.
        float x = 10.6f - PlayerPhysics.HitboxWidth; // right edge at 10.6
        var k = new PlayerKinematics(x, 11.4f - 1f, 0, 0, true);
        Assert.Equal(11, NavMath.StandingTile(k).Y);       // the plain feet row is the slope's row...
        var t = NavMath.StandingTile(k, g);
        Assert.Equal(10, t.Y);                              // ...but the body stands on it
        Assert.True(Pathfinder.CanStand(g, t));
    }

    [Fact]
    public void StandingTile_FlatGroundNextToHalfBlock_Unchanged()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "......",
            "......",
            "......",
            "...__.",
            "######");
        // Standing on the ground (feet row 3) with the 2-wide body overlapping the half block at (3,3).
        var k = new PlayerKinematics(2.375f, 3f, 0, 0, true);
        Assert.Equal(new TilePoint(2, 3), NavMath.StandingTile(k, g));
    }

    [Fact]
    public void NormalizeStart_FeetInSlopeRow_MovesUpOntoIt()
    {
        var (g, _) = FollowerIntegrationTests.InGameDiagonal().Parse();
        // Body (9..10, 9..11) overlaps only the slope (10,11), in its feet row: the body stands on it, one row up.
        var start = Pathfinder.NormalizeStart(g, new TilePoint(9, 11));
        Assert.Equal(new TilePoint(9, 10), start);
        Assert.True(Pathfinder.CanStand(g, start));
    }

    /// <summary>
    /// The in-game failure: standing still anywhere on the sloped diagonal, plan from the standing tile (plain and
    /// grid-aware) and follow it: the first tick must never be NeedsReplan, and the walk must arrive without replans.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StandingAnywhereOnDiagonal_PlanIsFollowed(bool mirrored, bool plainStandingTile)
    {
        var rows = FollowerIntegrationTests.InGameDiagonal().Rows;
        if (mirrored) rows = rows.Select(FollowerIntegrationTests.Mirror).ToArray();
        int W = rows[0].Length;
        TilePoint Flip(TilePoint p) => mirrored ? new TilePoint(W - 2 - p.X, p.Y) : p;
        var goals = new[] { Flip(new TilePoint(30, 7)), Flip(new TilePoint(2, 13)) };
        var profile = JumpModel.Apply(new MovementProfile(), PlayerPhysics.Vanilla);
        int runs = 0;
        // Drop the player onto every pixel column along the diagonal (columns 7..14) and let it settle.
        for (float px = 7 * 16; px <= 14 * 16; px += 3)
            foreach (var goal in goals)
            {
                var (g, _) = ArrayTileGrid.Parse(rows);
                var sim = new MiniPhysics(g, new TilePoint(2, 13));
                sim.X = mirrored ? W * 16 - px - MiniPhysics.W : px;
                sim.Y = 3 * 16; // above the hill
                for (int i = 0; i < 90; i++) sim.Step(default);
                Assert.True(sim.Kinematics.OnGround, $"didn't settle: {sim}");
                var here = plainStandingTile ? NavMath.StandingTile(sim.Kinematics) : sim.StandingTile;
                var plan = Pathfinder.FindPath(g, here, goal, profile, new PathOptions(AllowDig: false, AllowBuild: false));
                Assert.True(plan.Success, $"{sim}: {plan.FailureReason}");
                var f = new PathFollower(plan.Steps, start: here);
                FollowerOutput o = default;
                for (int t = 0; t < 600; t++)
                {
                    o = f.Tick(sim.Kinematics, g);
                    if (o.Status != FollowerStatus.Running) break;
                    sim.Step(o.Intent);
                    Assert.Null(sim.Hurt);
                }
                Assert.True(o.Status == FollowerStatus.Arrived,
                    $"start px {px} ({here}) → {goal}: {o.Status} at {o.Current}; {sim}; plan {Plan(plan)}");
                for (int i = 0; i < 30; i++) sim.Step(default);
                Assert.Equal(plan.Steps.Count > 0 ? plan.Steps[^1].Pos : here, sim.StandingTile);
                runs++;
            }
        output.WriteLine($"{runs} runs");
    }

    [Fact]
    public void WalkingOverHill_StandingTileIsAlwaysAClearBody()
    {
        foreach (bool mirrored in new[] { false, true })
        {
            var rows = FollowerIntegrationTests.SlopeHill().Rows;
            if (mirrored) rows = rows.Select(FollowerIntegrationTests.Mirror).ToArray();
            var (g, _) = ArrayTileGrid.Parse(rows);
            var sim = new MiniPhysics(g, new TilePoint(mirrored ? 45 : 3, 15));
            int plainBlocked = 0;
            for (int i = 0; i < 300; i++)
            {
                sim.Step(new ControlIntent { Right = !mirrored, Left = mirrored });
                if (!NavMath.BodyClear(g, NavMath.StandingTile(sim.Kinematics))) plainBlocked++;
                var t = Pathfinder.NormalizeStart(g, sim.StandingTile);
                Assert.True(Pathfinder.CanStand(g, t), $"tick {i}: {sim} → {t}");
            }
            output.WriteLine($"mirrored {mirrored}: the plain standing tile overlapped a block on {plainBlocked} ticks");
            Assert.True(plainBlocked > 0); // the case this guards against does happen on a hill
        }
    }
}
