using LlmPlayer.Core.Build;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Tests;

public class PlatformPlanTests
{
    private static readonly ReachSpec Vanilla = new(5, 4, 0);

    [Fact]
    public void TileCount_IsWidthTimesRows()
    {
        Assert.Equal(20, PlatformPlan.TileCount(10, 19, 2));
        Assert.Equal(20, PlatformPlan.TileCount(19, 10, 2));
        Assert.Equal(1, PlatformPlan.TileCount(5, 5, 1));
    }

    [Fact]
    public void RowYs_GoUpwardFromY() =>
        Assert.Equal(new[] { 100, 92, 84 }, PlatformPlan.RowYs(100, 3, 8));

    [Fact]
    public void SweepRow_StartsAtEndNearestPlayer()
    {
        var fromLeft = PlatformPlan.SweepRow(10, 14, 50, playerX: 3);
        Assert.Equal(new[] { 10, 11, 12, 13, 14 }, fromLeft.Select(t => t.X));
        Assert.All(fromLeft, t => Assert.Equal(50, t.Y));

        var fromRight = PlatformPlan.SweepRow(14, 10, 50, playerX: 20);
        Assert.Equal(new[] { 14, 13, 12, 11, 10 }, fromRight.Select(t => t.X));
    }

    [Fact]
    public void SweepRow_PlayerInMiddle_TieGoesLeftToRight()
    {
        // Body centre 12.5 is equidistant from tiles 10 and 15 (centres 10.5 and 15.5)? 2 vs 3 → left is nearer.
        Assert.Equal(10, PlatformPlan.SweepRow(10, 15, 0, playerX: 11)[0].X);
        // Centre 13.0: tile 10 centre 10.5 (2.5), tile 15 centre 15.5 (2.5) → tie → left first.
        Assert.Equal(10, PlatformPlan.SweepRow(10, 15, 0, playerX: 12)[0].X);
    }

    [Fact]
    public void NextIndex_FirstAnchoredInSweepOrder()
    {
        var row = PlatformPlan.SweepRow(0, 5, 0, playerX: 0);
        Assert.Equal(3, PlatformPlan.NextIndex(row, t => t.X >= 3));
        Assert.Equal(0, PlatformPlan.NextIndex(row, _ => true));
        Assert.Equal(-1, PlatformPlan.NextIndex(row, _ => false));
    }

    [Fact]
    public void NextIndex_RowGrowsFromAnchorInTheMiddle()
    {
        // Simulate placing: only x=3 touches something; each placed tile anchors its neighbours.
        var remaining = PlatformPlan.SweepRow(0, 5, 0, playerX: 0);
        var placed = new HashSet<int>();
        bool Anchored(TilePoint t) => t.X == 3 || placed.Contains(t.X - 1) || placed.Contains(t.X + 1);
        var order = new List<int>();
        while (remaining.Count > 0)
        {
            int i = PlatformPlan.NextIndex(remaining, Anchored);
            Assert.True(i >= 0);
            placed.Add(remaining[i].X);
            order.Add(remaining[i].X);
            remaining.RemoveAt(i);
        }
        Assert.Equal(new[] { 3, 2, 1, 0, 4, 5 }, order);
    }

    // Ground at row 10 for x 0..9 and 30..39, a chasm between.
    private static ArrayTileGrid Chasm(params int[] platformXs)
    {
        var rows = new string[30];
        for (int y = 0; y < 30; y++)
        {
            var chars = new char[40];
            for (int x = 0; x < 40; x++)
            {
                bool ground = (x < 10 || x >= 30) && y >= 10;
                chars[x] = ground ? '#' : '.';
            }
            rows[y] = new string(chars);
        }
        var (grid, _) = ArrayTileGrid.Parse(rows);
        foreach (int x in platformXs) grid.Set(x, 10, new TileInfo(TileKind.Platform));
        return grid;
    }

    [Fact]
    public void BestStand_BridgeStart_StandsAtTheEdgeToReachMostTiles()
    {
        var grid = Chasm();
        var upcoming = PlatformPlan.SweepRow(10, 29, 10, playerX: 2);
        var stand = PlatformPlan.BestStand(grid, new TilePoint(2, 9), upcoming, Vanilla, solid: false);
        Assert.Equal(new TilePoint(9, 9), stand);
        Assert.Equal(4, PlatformPlan.Coverage(stand!.Value, upcoming, Vanilla, solid: false, margin: 1f));
    }

    [Fact]
    public void BestStand_WalksOntoWhatWasBuilt()
    {
        var grid = Chasm(10, 11, 12, 13);
        var upcoming = PlatformPlan.SweepRow(14, 29, 10, playerX: 9);
        var stand = PlatformPlan.BestStand(grid, new TilePoint(9, 9), upcoming, Vanilla, solid: false);
        Assert.Equal(new TilePoint(13, 9), stand);
    }

    [Fact]
    public void BestStand_PrefersTheRowLevelOverAShallowPit()
    {
        // Chasm floor only 3 tiles below the row: standing in the pit would reach more tiles, but the edge wins.
        var grid = Chasm();
        for (int x = 10; x < 30; x++) grid.Set(x, 13, new TileInfo(TileKind.Solid));
        var upcoming = PlatformPlan.SweepRow(10, 29, 10, playerX: 2);
        Assert.Equal(new TilePoint(9, 9), PlatformPlan.BestStand(grid, new TilePoint(2, 9), upcoming, Vanilla, solid: false));
    }

    [Fact]
    public void BestStand_UpperRow_FallsBackToSpotsBelow()
    {
        // Row at y=4 above flat ground (feet row 9): only spots below the row exist.
        var grid = Chasm();
        var upcoming = PlatformPlan.SweepRow(2, 8, 4, playerX: 0);
        var stand = PlatformPlan.BestStand(grid, new TilePoint(0, 9), upcoming, Vanilla, solid: false, margin: 0f);
        Assert.NotNull(stand);
        Assert.Equal(9, stand!.Value.Y);
    }

    [Fact]
    public void BestStand_SolidBlock_NeverInsideTheBody()
    {
        // Flat ground at row 10; build a solid row at y=9 (the feet row) right where the player stands.
        var (grid, _) = ArrayTileGrid.Parse(
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "####################",
            "####################");
        var upcoming = PlatformPlan.SweepRow(5, 8, 9, playerX: 5);
        var player = new TilePoint(5, 9);
        var solid = PlatformPlan.BestStand(grid, player, upcoming, Vanilla, solid: true);
        Assert.NotNull(solid);
        Assert.False(PlatformPlan.InBody(solid!.Value, upcoming[0]));
        var platform = PlatformPlan.BestStand(grid, player, upcoming, Vanilla, solid: false);
        Assert.Equal(player, platform); // a platform may go where the player stands
    }

    [Fact]
    public void BestStand_NothingStandable_Null()
    {
        var (grid, _) = ArrayTileGrid.Parse(Enumerable.Repeat(new string('.', 30), 30).ToArray());
        Assert.Null(PlatformPlan.BestStand(grid, new TilePoint(10, 10), new[] { new TilePoint(15, 10) }, Vanilla, solid: false));
        Assert.Null(PlatformPlan.BestStand(grid, new TilePoint(0, 2), Array.Empty<TilePoint>(), Vanilla, solid: false));
    }

    [Fact]
    public void Coverage_StopsAtFirstOutOfReach()
    {
        var stand = new TilePoint(10, 10); // reaches x 6..15 with no margin
        var tiles = new[] { new TilePoint(14, 10), new TilePoint(15, 10), new TilePoint(16, 10), new TilePoint(12, 10) };
        Assert.Equal(2, PlatformPlan.Coverage(stand, tiles, Vanilla, solid: false));
    }

    [Fact]
    public void InBody_Covers2x3AboveFeet()
    {
        var s = new TilePoint(10, 10);
        Assert.True(PlatformPlan.InBody(s, new TilePoint(10, 10)));
        Assert.True(PlatformPlan.InBody(s, new TilePoint(11, 8)));
        Assert.False(PlatformPlan.InBody(s, new TilePoint(12, 10)));
        Assert.False(PlatformPlan.InBody(s, new TilePoint(10, 11)));
        Assert.False(PlatformPlan.InBody(s, new TilePoint(10, 7)));
    }
}
