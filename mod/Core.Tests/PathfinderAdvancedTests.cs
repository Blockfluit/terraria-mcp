using System.Diagnostics;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

public class PathfinderAdvancedTests(ITestOutputHelper output)
{
    private static bool Has(PathResult r, MoveKind k) => r.Steps.Any(s => s.Kind == k);

    private static IEnumerable<TilePoint> Body(TilePoint p)
    {
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                yield return new TilePoint(p.X + dx, p.Y - dy);
    }

    /// <summary>Floor row 13, an 8-tall wall at x=6 made of `glyph`, S left, G right.</summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) TallWall(char glyph) =>
        new GridBuilder(14, 14).Fill(0, 13, 13, 13, '#').Fill(6, 5, 6, 12, glyph)
            .Put(1, 12, 'S').Put(11, 12, 'G').Parse();

    [Fact]
    public void WallTooHigh_DigsThrough()
    {
        var (g, m) = TallWall('#');
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 35), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
        var digs = r.Steps.Where(s => s.Kind == MoveKind.Dig).ToList();
        Assert.NotEmpty(digs);
        Assert.All(digs, s =>
        {
            Assert.NotNull(s.WorkTile);
            Assert.Equal(TileKind.Solid, g.Get(s.WorkTile!.Value).Kind);
        });
    }

    [Fact]
    public void DigSteps_StayAtSourceThenMove()
    {
        var (g, m) = TallWall('#');
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 35), new());
        Assert.True(r.Success, r.FailureReason);
        var prev = m['S'];
        for (int i = 0; i < r.Steps.Count; i++)
        {
            var s = r.Steps[i];
            if (s.Kind == MoveKind.Dig)
            {
                Assert.Equal(prev, s.Pos);                       // dig from where you stand
                Assert.NotEqual(MoveKind.Dig, r.Steps[^1].Kind);  // a move always follows
            }
            prev = s.Pos;
        }
        // Every dug tile is in the body of the step that follows the dig run.
        for (int i = 0; i < r.Steps.Count; i++)
        {
            if (r.Steps[i].Kind != MoveKind.Dig) continue;
            int j = i;
            while (r.Steps[j].Kind == MoveKind.Dig) j++;
            Assert.Contains(r.Steps[i].WorkTile!.Value, Body(r.Steps[j].Pos));
        }
    }

    [Fact]
    public void UnbreakableWall_NotDug()
    {
        var (g, m) = TallWall('X');
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 35), new());
        Assert.False(r.Success);
    }

    [Fact]
    public void PickTooWeak_NotDug()
    {
        var (g, m) = TallWall('#');
        for (int y = 5; y <= 12; y++) g.Set(6, y, new TileInfo(TileKind.Solid, PickPowerRequired: 65));
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 35), new());
        Assert.False(r.Success);
    }

    [Fact]
    public void ProtectedTile_NeverDug()
    {
        var (g, m) = TallWall('P');
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 1000), new());
        Assert.False(r.Success);
        Assert.DoesNotContain(r.Steps, s => s.WorkTile is { } w && g.Get(w).Protected);
    }

    [Fact]
    public void DigCostsMoreThanWalkingAround()
    {
        // 7-tall wall (too high to jump) at x=10; a 3-tall block at x=7..8 lets you climb onto it.
        var (g, m) = new GridBuilder(20, 14)
            .Fill(0, 13, 19, 13, '#').Fill(10, 6, 10, 12, '#').Fill(7, 10, 8, 12, '#')
            .Put(1, 12, 'S').Put(16, 12, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 35), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.False(Has(r, MoveKind.Dig));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void DigsDownIntoSealedCave()
    {
        // G is in a cave under 3 tiles of dirt; no other way in.
        var (g, m) = new GridBuilder(12, 16)
            .Fill(0, 4, 11, 15, '#').Fill(2, 8, 9, 11, '.')
            .Put(3, 3, 'S').Put(5, 11, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 35), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.True(Has(r, MoveKind.Dig));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    /// <summary>S at the bottom of a 10-deep, 2-wide shaft; G on the surface.</summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) Shaft() =>
        new GridBuilder(12, 18).Fill(0, 5, 11, 17, '#').Fill(5, 5, 6, 14, '.')
            .Put(5, 14, 'S').Put(9, 4, 'G').Parse();

    [Fact]
    public void ShaftOutOfReach_BuildsPlatform()
    {
        var (g, m) = Shaft();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 0, BuildItemCount: 20), new());
        Assert.True(r.Success, r.FailureReason);
        var builds = r.Steps.Where(s => s.Kind == MoveKind.BuildPlatform).ToList();
        Assert.NotEmpty(builds);
        Assert.True(builds.Count <= 20);
        Assert.All(builds, s =>
        {
            Assert.NotNull(s.WorkTile);
            Assert.Equal(TileKind.Air, g.Get(s.WorkTile!.Value).Kind);
            // the platform ends up directly under the new standing position
            Assert.Equal(s.Pos.Y + 1, s.WorkTile!.Value.Y);
            Assert.InRange(s.WorkTile!.Value.X, s.Pos.X, s.Pos.X + 1);
        });
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void BuildCount_RespectsCap()
    {
        var (g, m) = Shaft();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 0, BuildItemCount: 1), new());
        Assert.False(r.Success);  // 10 deep needs at least two platforms with a 5-tile jump
    }

    [Fact]
    public void NoBuildItems_NoBuildSteps()
    {
        var (g, m) = Shaft();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 0, BuildItemCount: 0), new());
        Assert.False(r.Success);
        Assert.False(Has(r, MoveKind.BuildPlatform));
    }

    [Fact]
    public void AllowBuildFalse_NoBuildSteps()
    {
        var (g, m) = Shaft();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(PickPower: 0, BuildItemCount: 20), new(AllowBuild: false));
        Assert.False(r.Success);
    }

    [Fact]
    public void GapTooWide_BuildsBridge()
    {
        // 10-wide fatal pit; no digging possible
        var (g, m) = new GridBuilder(24, 32)
            .Fill(0, 4, 5, 4, '#').Fill(16, 4, 23, 4, '#')
            .Put(1, 3, 'S').Put(19, 3, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(BuildItemCount: 50), new());
        Assert.True(r.Success, r.FailureReason);
        var builds = r.Steps.Where(s => s.Kind == MoveKind.BuildPlatform).ToList();
        Assert.NotEmpty(builds);
        Assert.All(builds, s => Assert.Equal(4, s.WorkTile!.Value.Y));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    private static void AssertNoBodyOn(ArrayTileGrid g, PathResult r, TileKind kind)
    {
        foreach (var s in r.Steps)
            foreach (var t in Body(s.Pos))
                Assert.NotEqual(kind, g.Get(t).Kind);
    }

    [Fact]
    public void LavaAvoided()
    {
        // a lava puddle on the floor; walking through is shortest
        var (g, m) = ArrayTileGrid.Parse(
            "..............",
            "..............",
            "..............",
            "..............",
            "S.....LLL....G.",
            "###############");
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success, r.FailureReason);
        AssertNoBodyOn(g, r, TileKind.Lava);
    }

    [Fact]
    public void HazardAvoided()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "..............",
            "..............",
            "..............",
            "..............",
            "S.....^^^....G.",
            "###############");
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.True(r.Success, r.FailureReason);
        AssertNoBodyOn(g, r, TileKind.Hazard);
    }

    [Fact]
    public void LavaPit_NeverFallenInto()
    {
        // the only way across is a fall into a lava-floored pit: must fail, not path through lava
        var (g, m) = new GridBuilder(24, 12)
            .Fill(0, 4, 5, 11, '#').Fill(16, 4, 23, 11, '#').Fill(6, 9, 15, 11, 'L')
            .Put(1, 3, 'S').Put(19, 3, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new(AllowDig: false, AllowBuild: false));
        Assert.False(r.Success);
    }

    [Fact]
    public void WaterSwims()
    {
        // wall to the top of the world; the only way across is a water channel under it
        var (g, m) = new GridBuilder(20, 16)
            .Fill(0, 6, 19, 15, '#').Fill(9, 0, 10, 5, '#').Fill(6, 6, 13, 10, '~')
            .Put(2, 5, 'S').Put(16, 5, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new(AllowDig: false));
        Assert.True(r.Success, r.FailureReason);
        Assert.True(Has(r, MoveKind.Swim));
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void UnknownTreatedAsPassableAtExtraCost()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "...???.....",
            "...???.....",
            "S..???....G",
            "###########");
        // G is in the last column; tolerance lets the path end one short of it
        var r = Pathfinder.FindPath(g, m['S'], new TilePoint(9, 2), new(), new());
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(new TilePoint(9, 2), r.Steps[^1].Pos);
    }

    [Fact]
    public void GoalInsideSolid_UsesNearestStandable()
    {
        var (g, m) = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "S.........",
            "##########",
            "##########");
        var goal = new TilePoint(7, 4);
        var r = Pathfinder.FindPath(g, m['S'], goal, new(), new());
        Assert.True(r.Success, r.FailureReason);
        var end = r.Steps[^1].Pos;
        Assert.True(Math.Max(Math.Abs(end.X - goal.X), Math.Abs(end.Y - goal.Y)) <= 2, $"ended at {end}");
    }

    [Fact]
    public void SnappedGoal_IsReportedAsResolvedGoal()
    {
        // Goal two tiles down inside the ground: the body can't be there, so the search aims at the surface cell
        // above it. MoveToTask relies on ResolvedGoal to know that arriving there means done (it used to replan
        // forever, seen in game as "walking: 2 tiles remaining").
        var b = new GridBuilder(20, 10).Fill(0, 5, 19, 9, '#');
        b.Put(0, 4, 'S');
        var (g, m) = b.Parse();
        var requested = new TilePoint(10, 6);
        var r = Pathfinder.FindPath(g, m['S'], requested, new(), new(AllowDig: false, AllowBuild: false));
        Assert.True(r.Success, r.FailureReason);
        Assert.NotNull(r.ResolvedGoal);
        Assert.NotEqual(requested, r.ResolvedGoal);
        Assert.Equal(4, r.ResolvedGoal!.Value.Y);
        var end = r.Steps[^1].Pos;
        Assert.True(Math.Max(Math.Abs(end.X - r.ResolvedGoal.Value.X), Math.Abs(end.Y - r.ResolvedGoal.Value.Y)) <= 1);
        Assert.True(Math.Max(Math.Abs(end.X - requested.X), Math.Abs(end.Y - requested.Y)) > 1,
            "the path legitimately ends outside tolerance of the requested goal");
    }

    [Fact]
    public void GoalToleranceZero_EndsExactlyOnTheGoal()
    {
        var (g, m) = ArrayTileGrid.Parse("............", "............", "S.........G.", "############");
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new(GoalTolerance: 0));
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void ExactGoal_ResolvedGoalIsRequested()
    {
        var (g, m) = ArrayTileGrid.Parse("..........", "..........", "S......G..", "##########");
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new());
        Assert.Equal(m['G'], r.ResolvedGoal);
    }

    [Fact]
    public void GoalDeepInSolid_FailsGoalNotStandable()
    {
        var b = new GridBuilder(20, 20).Fill(0, 3, 19, 19, '#');
        b.Put(0, 2, 'S');
        var (g, m) = b.Parse();
        var r = Pathfinder.FindPath(g, m['S'], new TilePoint(10, 12), new(PickPower: 35), new());
        Assert.False(r.Success);
        Assert.Contains("goal_not_standable", r.FailureReason);
    }

    /// <summary>
    /// 200x200 world with full-width platform rows every 4 tiles (lots of reachable states) and G
    /// sealed in an unbreakable box in the middle.
    /// </summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) SealedBoxWorld()
    {
        var b = new GridBuilder(200, 200);
        for (int y = 7; y < 199; y += 4) b.Fill(0, y, 199, y, '=');
        b.Fill(0, 199, 199, 199, '#');
        b.Fill(95, 90, 104, 100, 'X').Fill(96, 91, 103, 99, '.');
        b.Put(99, 99, 'G').Put(10, 198, 'S');
        return b.Parse();
    }

    [Fact]
    public void Unreachable_FailsWithinNodeBudget()
    {
        var (g, m) = SealedBoxWorld();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new(MaxNodes: 5000));
        Assert.False(r.Success);
        Assert.True(r.NodesExpanded <= 5000, $"expanded {r.NodesExpanded}");
        Assert.Contains("unreachable", r.FailureReason);
    }

    /// <summary>Hilly surface over dirt with water-filled and dry caves; G sealed in an X box.</summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) TerrainWorld()
    {
        const int w = 400, h = 200;
        var b = new GridBuilder(w, h);
        var surface = new int[w];
        for (int x = 0; x < w; x++)
        {
            surface[x] = 60 + (int)(8 * Math.Sin(x / 13.0) + 4 * Math.Sin(x / 4.7));
            b.Fill(x, surface[x], x, h - 1, '#');
        }
        var rng = new Random(42);
        for (int i = 0; i < 60; i++)
        {
            int cx = rng.Next(10, w - 10), cy = rng.Next(80, h - 10), rx = rng.Next(3, 10), ry = rng.Next(2, 6);
            char fill = i % 5 == 0 ? '~' : '.';
            for (int y = cy - ry; y <= cy + ry; y++)
                for (int x = cx - rx; x <= cx + rx; x++)
                    if (x > 0 && x < w - 1 && y < h - 1 &&
                        (x - cx) * (x - cx) * ry * ry + (y - cy) * (y - cy) * rx * rx <= rx * rx * ry * ry)
                        b.Put(x, y, fill);
        }
        b.Fill(360, 140, 372, 152, 'X').Fill(361, 141, 371, 151, '.').Put(365, 151, 'G');
        b.Put(20, surface[20] - 1, 'S');
        return b.Parse();
    }

    private double TimeSearch(ArrayTileGrid g, TilePoint s, TilePoint goal, MovementProfile profile, out PathResult r)
    {
        var o = new PathOptions(MaxNodes: 20000);
        r = null!;
        double ms = double.MaxValue;
        for (int i = 0; i < 12; i++) // first runs warm up the JIT; report the best run
        {
            var sw = Stopwatch.StartNew();
            r = Pathfinder.FindPath(g, s, goal, profile, o);
            ms = Math.Min(ms, sw.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"expanded {r.NodesExpanded} nodes in {ms:F2} ms");
        return ms;
    }

    [Fact]
    public void FullBudgetSearch_TypicalTerrain_IsFast()
    {
        var (g, m) = TerrainWorld();
        double ms = TimeSearch(g, m['S'], m['G'], new MovementProfile(PickPower: 35, BuildItemCount: 50), out var r);
        TimeSearch(g, m['S'], m['G'], new MovementProfile(PickPower: 35, BuildItemCount: 0), out _);
        TimeSearch(g, m['S'], m['G'], new MovementProfile(PickPower: 0, BuildItemCount: 0), out _);
        Assert.False(r.Success);
        Assert.Equal(20000, r.NodesExpanded);
        // A few ms in Release; generous bound so Debug/CI runs don't flake.
        Assert.True(ms < 150, $"took {ms:F2} ms");
    }

    [Fact]
    public void ExhaustiveSearch_PlatformDenseWorld_IsBounded()
    {
        // Adversarial: every position has a dozen jump targets through the platforms.
        var (g, m) = SealedBoxWorld();
        double ms = TimeSearch(g, m['S'], m['G'], new MovementProfile(), out var r);
        Assert.False(r.Success);
        Assert.True(r.NodesExpanded > 9000, $"only expanded {r.NodesExpanded}");
        Assert.True(ms < 300, $"took {ms:F2} ms");
    }

    [Fact]
    public void ClimbBuild_InOpenAir_OnlyOneTileAtATime()
    {
        // Open sky, a 9-high cliff and nothing to dig with: climbing next to it needs platforms, and vanilla only
        // places a platform next to an existing tile, so the climb steps must stay anchored.
        var (g, m) = new GridBuilder(20, 24).Fill(0, 20, 19, 23, '#').Fill(12, 11, 19, 19, '#')
            .Put(2, 19, 'S').Put(15, 10, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(BuildItemCount: 30), new(AllowDig: false));
        Assert.True(r.Success, r.FailureReason);
        var placed = new HashSet<TilePoint>();
        foreach (var s in r.Steps.Where(s => s.Kind == MoveKind.BuildPlatform))
        {
            var w = s.WorkTile!.Value;
            bool anchored = false;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var n = new TilePoint(w.X + dx, w.Y + dy);
                    if ((dx != 0 || dy != 0) && (g.Get(n).IsStandable || placed.Contains(n))) anchored = true;
                }
            Assert.True(anchored, $"platform at {w} has nothing to attach to");
            placed.Add(w);
        }
    }

    [Fact]
    public void Swim_NoDiagonalCornerCutting()
    {
        // A water channel entered past solid corners: every diagonal swim step needs a free orthogonal neighbour.
        var (g, m) = ArrayTileGrid.Parse(
            "XXXXXXXXXXXX",
            "X..........X",
            "X..........X",
            "X.S..#.....X",
            "XX##~~~~...X",
            "XXXX~~~~...X",
            "XXXX~~~~..GX",
            "XXXXXXXXXXXX");
        var r = Pathfinder.FindPath(g, m['S'], m['G'], new(), new(AllowDig: false, AllowBuild: false));
        var prev = m['S'];
        foreach (var s in r.Steps)
        {
            if (s.Kind == MoveKind.Swim && s.Pos.X != prev.X && s.Pos.Y != prev.Y)
                Assert.True(BodyFree(g, new TilePoint(s.Pos.X, prev.Y)) || BodyFree(g, new TilePoint(prev.X, s.Pos.Y)),
                    $"corner cut {prev} -> {s.Pos}");
            prev = s.Pos;
        }
    }

    private static bool BodyFree(ArrayTileGrid g, TilePoint p) => Body(p).All(t => !g.Get(t).BlocksBody);
}
