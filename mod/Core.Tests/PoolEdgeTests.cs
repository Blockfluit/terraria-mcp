using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

/// <summary>Leaving liquid: a body partly or fully in water must still have every move its feet allow.</summary>
public class PoolEdgeTests(ITestOutputHelper output)
{
    // Seen in game after the breath merge (origin x=4090 y=691; '@' feet-left at (4095,696), standing in a pool's
    // shallow edge with an open passage straight up): move_to anywhere failed "unreachable: searched 2 nodes".
    internal static readonly string[] Map =
    {
        "###...#####",
        "###...#####",
        "####...####",
        ".###...####",
        "~~##...####",
        "~~~~~S~###.",
        "~~~~######.",
        "~~~~######.",
        "~~~#######.",
        "~~~########",
        "~##########",
    };

    private static MovementProfile Real(bool waterBreathing = false, int pick = 0, int builds = 0) =>
        JumpModel.Apply(new MovementProfile(PickPower: pick, BuildItemCount: builds, WaterBreathing: waterBreathing),
            PlayerPhysics.Vanilla);

    private static readonly PathOptions NoWork = new(AllowDig: false, AllowBuild: false);

    private PathResult Find(ArrayTileGrid g, TilePoint s, TilePoint goal, MovementProfile p, PathOptions o)
    {
        var r = Pathfinder.FindPath(g, s, goal, p, o);
        output.WriteLine($"{r.Success} {r.FailureReason} " + string.Join(" ", r.Steps.Select(st => $"{st.Kind}{st.Pos}")));
        return r;
    }

    [Fact]
    public void CanLeaveThePoolUpward()
    {
        var (g, m) = ArrayTileGrid.Parse(Map);
        var goal = new TilePoint(m['S'].X - 1, m['S'].Y - 4); // up the passage
        var r = Find(g, m['S'], goal, new(PickPower: 35, BuildItemCount: 50), new());
        var breathe = Find(g, m['S'], goal, new(PickPower: 35, BuildItemCount: 50) { WaterBreathing = true }, new());
        Assert.True(breathe.Success, "with water breathing: " + breathe.FailureReason);
        Assert.True(r.Success, $"{r.FailureReason} (nodes {r.NodesExpanded})");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanLeaveThePoolUpward_RealProfile(bool waterBreathing)
    {
        var (g, m) = ArrayTileGrid.Parse(Map);
        var r = Find(g, m['S'], new TilePoint(m['S'].X - 1, m['S'].Y - 4), Real(waterBreathing, 35, 50), new());
        Assert.True(r.Success, r.FailureReason);
    }

    [Fact]
    public void WetStart_CanStepOffADryEdge()
    {
        // Right body column in the pool's edge, left foot over a 9-deep drop (too deep to jump down): only stepping
        // off the edge gets there.
        var (g, m) = new GridBuilder(14, 16).Fill(0, 15, 13, 15, '#').Fill(7, 6, 13, 14, '#').Fill(7, 5, 13, 5, '~')
            .Put(6, 5, 'S').Put(1, 14, 'G').Parse();
        var r = Find(g, m['S'], m['G'], Real(), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.Fall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WadesOutOfAShallowPool_OntoALedge(bool left)
    {
        // 1-deep pool (row 5) between ledges whose tops are at the water row: one step up either side.
        var (g, m) = new GridBuilder(16, 8).Fill(0, 6, 15, 7, '#').Fill(0, 5, 3, 5, '#').Fill(12, 5, 15, 5, '#')
            .Fill(4, 5, 11, 5, '~').Put(7, 5, 'S').Put(left ? 1 : 13, 4, 'G').Parse();
        var r = Find(g, m['S'], m['G'], Real(), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void JumpsOutOfAPuddle_InAPit()
    {
        // A 2-wide pit, 3 deep, with one row of water at its bottom.
        var (g, m) = new GridBuilder(14, 10).Fill(0, 6, 13, 9, '#').Fill(6, 6, 7, 8, '.').Fill(6, 8, 7, 8, '~')
            .Put(6, 8, 'S').Put(11, 5, 'G').Parse();
        var r = Find(g, m['S'], m['G'], Real(), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void ClimbsOutOfATwoDeepPool_NextToAWall()
    {
        // Wall on the left, 2-deep water (rows 4..5), the bank on the right 2 above the pool floor.
        var (g, m) = new GridBuilder(14, 8).Fill(0, 0, 0, 7, 'X').Fill(0, 6, 13, 7, '#').Fill(7, 4, 13, 5, '#')
            .Fill(1, 4, 6, 5, '~').Put(1, 5, 'S').Put(10, 3, 'G').Parse();
        var r = Find(g, m['S'], m['G'], Real(), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubmergedStart_SwimsUpAndOut(bool waterBreathing)
    {
        // 5-deep pool (rows 3..7), body fully under water at its floor; the bank on the right at the surface.
        var (g, m) = new GridBuilder(14, 10).Fill(0, 0, 0, 9, 'X').Fill(0, 8, 13, 9, '#').Fill(7, 3, 13, 7, '#')
            .Fill(1, 3, 6, 7, '~').Put(2, 7, 'S').Put(10, 2, 'G').Parse();
        Assert.Equal(TileKind.Water, g.Get(m['S'].X, m['S'].Y - 2).Kind);
        var r = Find(g, m['S'], m['G'], Real(waterBreathing), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }
}
