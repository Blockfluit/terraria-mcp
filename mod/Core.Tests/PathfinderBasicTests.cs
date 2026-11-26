using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

/// <summary>Builds ASCII grids programmatically for tests that are too big to type.</summary>
internal sealed class GridBuilder
{
    private readonly char[][] _c;

    public GridBuilder(int w, int h) =>
        _c = Enumerable.Range(0, h).Select(_ => Enumerable.Repeat('.', w).ToArray()).ToArray();

    /// <summary>Fill the inclusive rectangle (x0,y0)-(x1,y1).</summary>
    public GridBuilder Fill(int x0, int y0, int x1, int y1, char ch)
    {
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                _c[y][x] = ch;
        return this;
    }

    public GridBuilder Put(int x, int y, char ch) => Fill(x, y, x, y, ch);

    public string[] Rows => _c.Select(r => new string(r)).ToArray();

    public (ArrayTileGrid Grid, Dictionary<char, TilePoint> Markers) Parse() => ArrayTileGrid.Parse(Rows);
}

public class PathfinderBasicTests
{
    private static bool Has(PathResult r, MoveKind k) => r.Steps.Any(s => s.Kind == k);

    /// <summary>Vertical drop of each step relative to the previous position.</summary>
    private static IEnumerable<(PathStep Step, int Drop)> Drops(TilePoint start, PathResult r)
    {
        var prev = start;
        foreach (var s in r.Steps)
        {
            yield return (s, s.Pos.Y - prev.Y);
            prev = s.Pos;
        }
    }

    [Fact]
    public void FlatGround_WalksToGoal()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "..........", "..........", "S......G..", "##########");
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
        Assert.All(r.Steps, s => Assert.Equal(MoveKind.Walk, s.Kind));
    }

    [Fact]
    public void StartIsGoal_EmptySuccess()
    {
        var (g, m) = ArrayTileGrid.Parse("....", "....", "S...", "####");
        var r = Pathfinder.FindPath(g, m['S'], m['S'], new(), new());
        Assert.True(r.Success);
        Assert.Empty(r.Steps);
    }

    [Fact]
    public void CanStand_RequiresClearBodyAndSupport()
    {
        var (g, _) = ArrayTileGrid.Parse(
            "....#.",
            "......",
            "......",
            "#.....",
            "#.=...");
        Assert.True(Pathfinder.CanStand(g, new TilePoint(0, 2)));   // support at (0,3)
        Assert.True(Pathfinder.CanStand(g, new TilePoint(1, 3)));   // platform at (2,4)
        Assert.False(Pathfinder.CanStand(g, new TilePoint(3, 3)));  // nothing under (3,4),(4,4)
        Assert.False(Pathfinder.CanStand(g, new TilePoint(3, 2)));  // body hits (4,0)
    }

    [Fact]
    public void OneTileLedge_StepsUp()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "..........",
            "......G...",
            "S....#####",
            "##########");
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
        Assert.True(Has(r, MoveKind.StepUp));
        Assert.False(Has(r, MoveKind.Jump));
    }

    private static (ArrayTileGrid, Dictionary<char, TilePoint>) Wall(int height)
    {
        // floor row 12, wall at x=6 rising `height` tiles above the floor
        var b = new GridBuilder(14, 13).Fill(0, 12, 13, 12, '#').Fill(6, 12 - height, 6, 11, '#');
        b.Put(1, 11, 'S').Put(11, 11, 'G');
        return b.Parse();
    }

    [Fact]
    public void FiveTileWall_JumpsOver()
    {
        var (g, m) = Wall(5);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(MaxJumpUp: 5), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.True(Has(r, MoveKind.Jump));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void SixTileWall_NoDig_Fails()
    {
        var (g, m) = Wall(6);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(MaxJumpUp: 5),
            new(AllowDig: false, AllowBuild: false));
        Assert.False(r.Success);
        Assert.Contains("unreachable", r.FailureReason);
    }

    [Fact]
    public void GapOfSix_JumpsAcross()
    {
        // floor row 4; gap columns 6..11 is a 25-deep pit (fatal fall)
        var (g, m) = new GridBuilder(20, 30)
            .Fill(0, 4, 5, 4, '#').Fill(12, 4, 19, 4, '#')
            .Put(1, 3, 'S').Put(15, 3, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.True(Has(r, MoveKind.Jump));
        Assert.False(Has(r, MoveKind.Fall));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void GapOfSeven_FallsInIfPitShallow()
    {
        // floor row 4; gap columns 6..12 (7 wide) is a pit two tiles deep with a floor at row 6
        var (g, m) = new GridBuilder(22, 10)
            .Fill(0, 6, 21, 9, '#').Fill(0, 4, 5, 5, '#').Fill(13, 4, 21, 5, '#')
            .Put(1, 3, 'S').Put(17, 3, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.True(Has(r, MoveKind.Fall));
        Assert.True(Has(r, MoveKind.StepUp) || Has(r, MoveKind.Jump));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void GapToLowerLedge_JumpsDown()
    {
        // 6-wide fatal pit; far side is 2 tiles lower than the near side
        var (g, m) = new GridBuilder(20, 32)
            .Fill(0, 4, 5, 4, '#').Fill(12, 6, 19, 6, '#')
            .Put(1, 3, 'S').Put(15, 5, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.True(Has(r, MoveKind.Jump));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    /// <summary>
    /// Plateau (feet row 7) with a 30-deep shaft at x=10..11 dropping into a tunnel (rows 35..37)
    /// that runs under everything. G is in the tunnel just under the plateau. The safe route is a
    /// long staircase to the right that descends 1 tile per 2 columns into the tunnel's far end.
    /// </summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) Cliff()
    {
        var b = new GridBuilder(90, 40);
        b.Fill(0, 8, 19, 39, '#');
        for (int i = 0; i < 30; i++) b.Fill(20 + 2 * i, 9 + i, 21 + 2 * i, 39, '#');
        b.Fill(80, 38, 89, 39, '#');
        b.Fill(10, 8, 11, 37, '.');   // shaft
        b.Fill(1, 35, 89, 37, '.');   // tunnel
        b.Put(2, 7, 'S').Put(5, 37, 'G');
        return b.Parse();
    }

    [Fact]
    public void LongFall_Avoided()
    {
        var (g, m) = Cliff();
        var profile = new MovementProfile(SafeFallTiles: 20);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], profile, new());
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
        foreach (var (s, drop) in Drops(m['S'], r))
            if (s.Kind is MoveKind.Fall or MoveKind.DropThrough)
                Assert.True(drop <= 20, $"fell {drop} tiles at {s.Pos}");
    }

    [Fact]
    public void NoFallDamage_AllowsLongFall()
    {
        var (g, m) = Cliff();
        var profile = new MovementProfile(SafeFallTiles: 20, NoFallDamage: true);
        var r = Pathfinder.FindPath(g, m['S'], m['G'], profile, new());
        Assert.True(r.Success, r.FailureReason);
        Assert.Contains(Drops(m['S'], r), d => d.Step.Kind == MoveKind.Fall && d.Drop > 20);
    }

    [Fact]
    public void PlatformAbove_JumpsThrough()
    {
        var (g, m) = new GridBuilder(12, 10)
            .Fill(0, 9, 11, 9, '#').Fill(4, 5, 9, 5, '=')
            .Put(0, 8, 'S').Put(6, 4, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.True(Has(r, MoveKind.Jump));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void PlatformBelow_DropsThrough()
    {
        var (g, m) = new GridBuilder(10, 10)
            .Fill(0, 4, 9, 4, '=').Fill(0, 9, 9, 9, '#')
            .Put(1, 3, 'S').Put(6, 8, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.True(Has(r, MoveKind.DropThrough));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void GoalTolerance_AcceptsNeighbourWhenExactGoalUnreachable()
    {
        // G is in the last column: the 2-wide body would stick out of the world there.
        var (g, m) = ArrayTileGrid.Parse(
            "........",
            "........",
            "........",
            "S......G",
            "########");
        Assert.False(Pathfinder.CanStand(g, m['G']));
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new(GoalTolerance: 1));
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(new TilePoint(6, 3), r.Steps[^1].Pos);
    }

    [Fact]
    public void JumpUp_NeverTargetsAStanceWithAPlatformInItsFeetRow()
    {
        // Platform rows 6 and 7 (a "step"): the stance (4,6) has the upper platform (5,6) in its feet row. Coming
        // down from the jump's across height the upper platform catches the player at row 5 instead.
        var (g, m) = new GridBuilder(14, 12)
            .Fill(0, 11, 13, 11, '#')
            .Fill(5, 6, 9, 6, '=').Fill(5, 7, 12, 7, '=')
            .Put(0, 10, 'S').Put(11, 6, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new(AllowDig: false, AllowBuild: false));
        Assert.True(r.Success, r.FailureReason);
        var prev = m['S'];
        foreach (var s in r.Steps)
        {
            if (s.Kind == MoveKind.Jump && s.Pos.Y < prev.Y)
                for (int x = s.Pos.X; x <= s.Pos.X + 1; x++)
                    Assert.NotEqual(TileKind.Platform, g.Get(x, s.Pos.Y).Kind);
            prev = s.Pos;
        }
    }
}
