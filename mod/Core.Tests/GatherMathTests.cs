using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class ReachTests
{
    private static readonly ReachSpec Vanilla = new(5, 4, 0);

    // Standing at (10,10) with the hitbox centred: left 10.375, width 1.25, top 8.375, bottom 11.
    // Vanilla IsTargetTileInItemRange: x in [left-5, right+5-1] = [5.375, 15.625] → 6..15; y in [top-4, bottom+4-2] = [4.375, 13] → 5..13.
    [Theory]
    [InlineData(6, 10, true)]
    [InlineData(5, 10, false)]
    [InlineData(15, 10, true)]
    [InlineData(16, 10, false)]
    [InlineData(10, 5, true)]
    [InlineData(10, 4, false)]
    [InlineData(10, 13, true)]
    [InlineData(10, 14, false)]
    [InlineData(15, 13, true)]
    public void FromStand_MatchesVanillaFormula(int tx, int ty, bool expected) =>
        Assert.Equal(expected, Reach.FromStand(new TilePoint(10, 10), new TilePoint(tx, ty), Vanilla));

    [Fact]
    public void TileBoost_WidensEverySide()
    {
        var boosted = Vanilla with { TileBoost = 1 };
        var stand = new TilePoint(10, 10);
        Assert.True(Reach.FromStand(stand, new TilePoint(5, 10), boosted));
        Assert.True(Reach.FromStand(stand, new TilePoint(16, 10), boosted));
        Assert.True(Reach.FromStand(stand, new TilePoint(10, 4), boosted));
        Assert.True(Reach.FromStand(stand, new TilePoint(10, 14), boosted));
        Assert.False(Reach.FromStand(stand, new TilePoint(4, 10), boosted));
    }

    [Fact]
    public void NegativeTileBoost_Shrinks() =>
        Assert.False(Reach.FromStand(new TilePoint(10, 10), new TilePoint(15, 10), Vanilla with { TileBoost = -1 }));

    [Fact]
    public void Margin_ShrinksReach()
    {
        var stand = new TilePoint(10, 10);
        Assert.False(Reach.FromStand(stand, new TilePoint(15, 10), Vanilla, margin: 1f));
        Assert.True(Reach.FromStand(stand, new TilePoint(14, 10), Vanilla, margin: 1f));
        Assert.False(Reach.FromStand(stand, new TilePoint(10, 5), Vanilla, margin: 1f));
    }

    [Fact]
    public void InRange_UsesRawHitbox()
    {
        // Hitbox left at 10.9 (not centred): left-5 = 5.9 → x=6 reachable, x=5 not; right 12.15 + 4 = 16.15 → x=16 ok.
        Assert.True(Reach.InRange(10.9f, 8.375f, 1.25f, 2.625f, new TilePoint(16, 10), Vanilla));
        Assert.True(Reach.InRange(10.9f, 8.375f, 1.25f, 2.625f, new TilePoint(6, 10), Vanilla));
        Assert.False(Reach.InRange(10.9f, 8.375f, 1.25f, 2.625f, new TilePoint(5, 10), Vanilla));
    }
}

public class MiningOrderTests
{
    [Fact]
    public void Rect_NormalizesCorners()
    {
        var a = MiningOrder.Rect(3, 5, 1, 4);
        Assert.Equal(6, a.Count);
        Assert.Contains(new TilePoint(1, 4), a);
        Assert.Contains(new TilePoint(3, 5), a);
    }

    [Theory]
    [InlineData(0, 0, 9, 19, 200L)]
    [InlineData(0, 0, 0, 0, 1L)]
    [InlineData(5, 5, 0, 0, 36L)]
    [InlineData(0, 0, 99999, 99999, 10000000000L)]
    public void RectCount_DoesNotOverflow(int x1, int y1, int x2, int y2, long expected) =>
        Assert.Equal(expected, MiningOrder.RectCount(x1, y1, x2, y2));

    [Fact]
    public void Order_TopRowFirst_ThenNearestColumn()
    {
        var tiles = MiningOrder.Rect(10, 20, 12, 21);
        var order = MiningOrder.Order(tiles, player: new TilePoint(14, 19));
        // Row 20 first, nearest column to the player's body centre (x=15.0) first: 12, 11, 10; then row 21.
        Assert.Equal(new[]
        {
            new TilePoint(12, 20), new TilePoint(11, 20), new TilePoint(10, 20),
            new TilePoint(12, 21), new TilePoint(11, 21), new TilePoint(10, 21),
        }, order);
    }

    [Fact]
    public void Order_RemovesDuplicates()
    {
        var order = MiningOrder.Order(new[] { new TilePoint(1, 1), new TilePoint(1, 1), new TilePoint(2, 1) }, new TilePoint(0, 0));
        Assert.Equal(2, order.Count);
    }

    [Fact]
    public void Order_PlayerOverArea_TiesGoLeftToRight()
    {
        // Body covers x=11..12 (centre 12.0): columns 11 and 12 are equally near, then 10 and 13.
        var order = MiningOrder.Order(MiningOrder.Rect(10, 5, 13, 5), new TilePoint(11, 4));
        Assert.Equal(new[] { new TilePoint(11, 5), new TilePoint(12, 5), new TilePoint(10, 5), new TilePoint(13, 5) }, order);
    }
}

public class StandFinderTests
{
    private static readonly ReachSpec Vanilla = new(5, 4, 0);

    [Fact]
    public void PicksNearestReachableStandableSpot()
    {
        // Player S on the floor at x=0; target A sits behind a wall tile at x=21.
        var (g, m) = ArrayTileGrid.Parse(
            "......................",
            "......................",
            "S...................#A",
            "######################");
        var stand = StandFinder.Best(g, m['S'], m['A'], Vanilla);
        Assert.NotNull(stand);
        Assert.True(Reach.FromStand(stand!.Value, m['A'], Vanilla, margin: 1f));
        // Body must clear the wall (x ≤ 18) and reach with margin (21 ≤ x+4 → x ≥ 17); nearest to the player is 17.
        Assert.Equal(new TilePoint(17, 2), stand.Value);
    }

    [Fact]
    public void AvoidsStandingOnlyOnPendingTiles()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "..........",
            "####AB####");
        var pending = new HashSet<TilePoint> { m['A'], m['B'] };
        var stand = StandFinder.Best(g, new TilePoint(4, 2), m['A'], Vanilla, pending);
        Assert.NotNull(stand);
        Assert.NotEqual(4, stand!.Value.X); // (4,2) rests only on A and B
    }

    [Fact]
    public void AvoidsStandingDirectlyBelowTarget()
    {
        // Target in the ceiling right above the player: prefer a spot to the side (debris/sand falls straight down).
        var (g, m) = ArrayTileGrid.Parse(
            "####A#####",
            "..........",
            "..........",
            "..........",
            "##########");
        var stand = StandFinder.Best(g, new TilePoint(4, 3), m['A'], Vanilla);
        Assert.NotNull(stand);
        Assert.False(stand!.Value.X <= 4 && stand.Value.X + 1 >= 4);
    }

    [Fact]
    public void NoStandableSpot_ReturnsNull()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "##########",
            "##########",
            "####A#####",
            "##########",
            "##########");
        Assert.Null(StandFinder.Best(g, new TilePoint(0, 0), m['A'], Vanilla));
    }

    [Fact]
    public void UnknownSupportIsNotStandable()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "....A.....",
            "??????????",
            "..........",
            "..........",
            "..........");
        // (Search radius 2 keeps candidates inside the grid: out of bounds reads as unbreakable solid.)
        Assert.Null(StandFinder.Best(g, new TilePoint(0, 2), m['A'], Vanilla, searchRadius: 2));
    }
}

public class TreeMathTests
{
    private static Func<int, int, bool> Trees(params string[] rows) => (x, y) =>
        y >= 0 && y < rows.Length && x >= 0 && x < rows[y].Length && rows[y][x] == 'T';

    private static readonly string[] Tree =
    {
        "..T..",
        ".TT..", // branch at (1,1)
        "..T..",
        "..TT.", // branch at (3,3)
        "..T..",
        ".TTT.", // roots at (1,5) and (3,5); base (2,5)
        "#####",
    };

    [Theory]
    [InlineData(2, 0)] // top
    [InlineData(2, 2)] // mid trunk
    [InlineData(2, 5)] // base itself
    [InlineData(1, 1)] // left branch
    [InlineData(3, 3)] // right branch
    [InlineData(1, 5)] // left root
    [InlineData(3, 5)] // right root
    public void FindsTrunkBase(int x, int y) =>
        Assert.Equal(new TilePoint(2, 5), TreeMath.FindBase(Trees(Tree), new TilePoint(x, y)));

    [Fact]
    public void NotATree_ReturnsNull() =>
        Assert.Null(TreeMath.FindBase(Trees(Tree), new TilePoint(0, 0)));

    [Fact]
    public void SingleTileStump_IsItsOwnBase() =>
        Assert.Equal(new TilePoint(1, 0), TreeMath.FindBase(Trees(".T.", "###"), new TilePoint(1, 0)));
}

public class GrabMathTests
{
    // Player hitbox 20x42 at (100,100); default grab range 42 px → pull box x 58..162, y 58..184.
    [Theory]
    [InlineData(150, 150, true)]
    [InlineData(161, 150, true)]
    [InlineData(162, 150, false)] // item left edge == box right edge: no overlap (XNA Intersects is strict)
    [InlineData(40, 150, true)]   // item 20 wide: 40..60 overlaps 58
    [InlineData(37, 150, false)]
    [InlineData(150, 184, false)]
    public void PullBox(int ix, int iy, bool expected) =>
        Assert.Equal(expected, GrabMath.InGrabRange(100, 100, 20, 42, 42, ix, iy, 20, 20));
}
