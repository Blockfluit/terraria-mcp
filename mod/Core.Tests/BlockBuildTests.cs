using LlmPlayer.Core.Build;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

/// <summary>
/// Walks that build with blocks (dirt, stone) instead of platforms. Seen in game: move_to up a hill failed "stuck at
/// BuildPlatform (2908,498) (repeatedly)" with 0 Wood Platforms but 71 Dirt and 415 Stone. A block is solid: it can't be
/// dropped through, and vanilla won't place it where it would overlap the player's hitbox.
/// </summary>
public class BlockBuildTests(ITestOutputHelper output)
{
    private static MovementProfile Real(int builds, int platforms, int pick = 0) =>
        JumpModel.Apply(new MovementProfile(PickPower: pick, BuildItemCount: builds, BuildPlatformCount: platforms), PlayerPhysics.Vanilla);

    [Fact]
    public void BuildIsPlatform_CountsPlatformsFirst()
    {
        var p = new MovementProfile(BuildItemCount: 10, BuildPlatformCount: 3);
        Assert.True(p.BuildIsPlatform(1));
        Assert.True(p.BuildIsPlatform(3));
        Assert.False(p.BuildIsPlatform(4));
        Assert.True(new MovementProfile(BuildItemCount: 10).BuildIsPlatform(10)); // default: all platforms
        Assert.False(new MovementProfile(BuildItemCount: 10, BuildPlatformCount: 0).BuildIsPlatform(1));
    }

    [Theory]
    [InlineData(30, 5, 20, false, 10)]
    [InlineData(30, 5, 20, true, 30)]
    [InlineData(10, 0, 20, false, 0)]
    public void PlatformBudget_MatchesPickOrder(int platforms, int blocks, int reserve, bool released, int expected)
    {
        Assert.Equal(expected, BuildReserve.PlatformBudget(platforms, reserve, released));
        // Pick takes exactly that many platforms for bridges/climbs before the first block.
        int p = platforms, b = blocks, taken = 0;
        while (BuildReserve.Pick(false, p, b, released ? 0 : reserve) == BuildPick.Platform && p > 0 && b > 0)
        {
            p--;
            taken++;
        }
        if (blocks > 0) Assert.Equal(expected, taken);
    }

    /// <summary>An 8-wide chasm 30 deep with unbreakable walls: descending needs BuildDown.</summary>
    private static (ArrayTileGrid, TilePoint, TilePoint) Chasm() =>
        Parse(new GridBuilder(20, 44).Fill(0, 6, 5, 43, 'X').Fill(14, 6, 19, 43, 'X').Fill(0, 40, 19, 43, 'X')
            .Put(3, 5, 'S').Put(9, 39, 'G'));

    private static (ArrayTileGrid, TilePoint, TilePoint) Parse(GridBuilder b)
    {
        var (g, m) = b.Parse();
        return (g, m['S'], m['G']);
    }

    [Fact]
    public void BlocksOnly_NeverDropsThroughAPlacedBlock()
    {
        var (g, s, goal) = Chasm();
        var withPlatforms = Pathfinder.FindPath(g, s, goal, Real(40, int.MaxValue), new PathOptions(AllowDig: false));
        Assert.True(withPlatforms.Success, withPlatforms.FailureReason);
        Assert.Contains(withPlatforms.Steps, st => st.Kind == MoveKind.BuildDown);

        var blocks = Pathfinder.FindPath(g, s, goal, Real(40, 0), new PathOptions(AllowDig: false));
        output.WriteLine(blocks.Success ? string.Join(" ", blocks.Steps.Select(st => $"{st.Kind}{st.Pos}")) : blocks.FailureReason);
        if (!blocks.Success) return;
        var prev = s;
        for (int i = 0; i < blocks.Steps.Count; i++)
        {
            var st = blocks.Steps[i];
            bool throughFloor = st.Kind == MoveKind.DropThrough || (st.Kind == MoveKind.BuildDown && st.Pos.X == prev.X);
            if (throughFloor && i > 0)
                Assert.False(blocks.Steps[i - 1].Kind is MoveKind.BuildPlatform or MoveKind.BuildDown,
                    $"step {i} drops through the block step {i - 1} placed");
            prev = st.Pos;
        }
    }

    [Fact]
    public void MiniPhysics_RefusesABlockInsideTheHitbox()
    {
        var (g, _) = new GridBuilder(10, 8).Fill(0, 6, 9, 7, '#').Parse();
        var sim = new MiniPhysics(g, new TilePoint(3, 5)) { Platforms = 0, Blocks = 5 };
        sim.Step(new ControlIntent { UseItem = true, CursorTile = new TilePoint(3, 5) }, SimTool.Block);
        Assert.Equal(TileKind.Air, g.Get(3, 5).Kind);
        sim.Step(new ControlIntent { UseItem = true, CursorTile = new TilePoint(5, 5) }, SimTool.Block);
        Assert.Equal(TileKind.Solid, g.Get(5, 5).Kind);
        Assert.Equal(4, sim.Blocks);
    }

    /// <summary>The in-game case: up a hill whose last rise is a 3-tile cliff, blocks only, walked by the full move_to
    /// loop (plans, follower, replans) in the sim.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MoveTo_UpAHill_WithBlocksOnly(bool mirrored)
    {
        var b = new GridBuilder(40, 30).Fill(0, 25, 39, 29, '#');
        for (int x = 0; x <= 20; x++) b.Fill(x, 25 - (21 - x) / 3, x, 24, '#'); // gentle rise to the left
        b.Fill(0, 10, 8, 24, '#');                                                 // a cliff on top
        b.Put(30, 24, 'S').Put(3, 9, 'G');
        var rows = b.Rows;
        if (mirrored) rows = rows.Select(FollowerIntegrationTests.Mirror).ToArray();
        var (g, m) = ArrayTileGrid.Parse(rows);
        TilePoint s = m['S'], goal = m['G'];
        if (mirrored)
        {
            s = s with { X = s.X - 1 };
            goal = goal with { X = goal.X - 1 };
        }
        var sim = new MiniPhysics(g, s) { Platforms = 0, Blocks = 60, PickPower = 0 };
        var run = new MoveToSim(g, sim, goal, Real(60, 0), new PathOptions(AllowDig: false), log: output.WriteLine);
        var outcome = run.Run(6000);
        output.WriteLine($"{outcome}: {run.Reason}; placed {sim.Placed.Count}, misuse {sim.Misuse}");
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.True(sim.Placed.Count > 0);
        Assert.Equal(0, sim.Misuse);
    }
}
