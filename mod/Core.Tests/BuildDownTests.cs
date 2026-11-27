using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

/// <summary>
/// Descending a drop too deep to fall safely by building down (seen in game: a corruption chasm 40 tiles deep whose
/// Ebonstone walls the copper pickaxe can't dig). Done by hand in game: place a platform up to 3 rows below the feet,
/// anchored to the shaft wall, drop through the one underfoot (or step off the rim), repeat.
/// </summary>
public class BuildDownTests(ITestOutputHelper output)
{
    private const int EbonstonePick = 65, CopperPick = 35;

    private static MovementProfile Real(int pick = CopperPick, int builds = 0) =>
        JumpModel.Apply(new MovementProfile(PickPower: pick, BuildItemCount: builds), PlayerPhysics.Vanilla);

    /// <summary>An 8-wide shaft (x 10..17) from the surface (rim row 6) down to a floor at row 48: 42 rows below the
    /// start on the left rim. Every block needs pick power 65.</summary>
    private static (ArrayTileGrid Grid, TilePoint Start, TilePoint Goal) Chasm(bool mirrored = false)
    {
        var b = new GridBuilder(24, 52).Fill(0, 6, 9, 51, '#').Fill(18, 6, 23, 51, '#').Fill(0, 48, 23, 51, '#')
            .Put(7, 5, 'S').Put(13, 47, 'G');
        var rows = b.Rows;
        if (mirrored) rows = rows.Select(r => new string(r.Reverse().ToArray())).ToArray();
        var (g, m) = ArrayTileGrid.Parse(rows);
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
                if (g.Get(x, y).IsSolid) g.Set(x, y, new TileInfo(TileKind.Solid, PickPowerRequired: EbonstonePick));
        TilePoint s = m['S'], goal = m['G'];
        if (mirrored)
        {
            s = s with { X = s.X - 1 };
            goal = goal with { X = goal.X - 1 };
        }
        return (g, s, goal);
    }

    private void Log(PathResult r) =>
        output.WriteLine($"{r.Success} {r.FailureReason} ({r.Steps.Count}): " +
                         string.Join(" ", r.Steps.Select(st => $"{st.Kind}{st.Pos}{(st.WorkTile is { } w ? "w" + w : "")}")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepChasm_DescendsByBuildingDown_WithSafeFallsOnly(bool mirrored)
    {
        var (g, s, goal) = Chasm(mirrored);
        var profile = Real(builds: 100);
        var r = Pathfinder.FindPath(g, s, goal, profile, new());
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(goal, r.Steps[^1].Pos);
        Assert.Contains(r.Steps, st => st.Kind == MoveKind.BuildDown);
        Assert.DoesNotContain(r.Steps, st => st.Kind == MoveKind.Dig);
        var prev = s;
        foreach (var st in r.Steps)
        {
            Assert.True(st.Pos.Y - prev.Y <= profile.SafeFallTiles, $"unsafe drop {prev} -> {st.Pos}");
            if (st.Kind == MoveKind.BuildDown)
            {
                var w = st.WorkTile!.Value;
                Assert.Equal(st.Pos.Y + 1, w.Y);                         // lands on it
                Assert.True(w.X == st.Pos.X || w.X == st.Pos.X + 1);
                Assert.InRange(w.Y - prev.Y, 2, 3);                      // within vanilla's downward reach
                Assert.InRange(st.Pos.Y - prev.Y, 1, Pathfinder.MaxBuildDown);
                Assert.True(Math.Abs(st.Pos.X - prev.X) <= 1);
                Assert.Equal(TileKind.Air, g.Get(w).Kind);
            }
            prev = st.Pos;
        }
    }

    [Fact]
    public void DeepChasm_NoBuildItems_Unreachable()
    {
        var (g, s, goal) = Chasm();
        var r = Pathfinder.FindPath(g, s, goal, Real(builds: 0), new());
        Log(r);
        Assert.False(r.Success);
    }

    [Fact]
    public void DeepChasm_BuildingNotAllowed_Unreachable()
    {
        var (g, s, goal) = Chasm();
        var r = Pathfinder.FindPath(g, s, goal, Real(builds: 100), new(AllowBuild: false));
        Assert.False(r.Success);
    }

    /// <summary>A 12-deep drop is a safe fall: no platforms.</summary>
    [Fact]
    public void ShallowDrop_FallsWithoutBuilding()
    {
        var (g, m) = new GridBuilder(24, 24).Fill(0, 6, 9, 23, '#').Fill(18, 6, 23, 23, '#').Fill(0, 18, 23, 23, '#')
            .Put(7, 5, 'S').Put(13, 17, 'G').Parse();
        var r = Pathfinder.FindPath(g, m['S'], m['G'], Real(builds: 100), new());
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        Assert.DoesNotContain(r.Steps, st => st.Kind is MoveKind.BuildDown or MoveKind.BuildPlatform);
        Assert.Contains(r.Steps, st => st.Kind == MoveKind.Fall && st.Pos.Y - 5 >= 10);
    }

    /// <summary>Standing on a platform row that spans the shaft (the hand-built loop's state): drop through it by
    /// building the next row below.</summary>
    [Fact]
    public void OnPlatformRow_BuildsDownThroughIt()
    {
        var (g, s, goal) = Chasm();
        for (int x = 10; x <= 17; x++) g.Set(x, 20, new TileInfo(TileKind.Platform));
        var start = new TilePoint(12, 19);
        var r = Pathfinder.FindPath(g, start, goal, Real(builds: 100), new());
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(goal, r.Steps[^1].Pos);
        Assert.Contains(r.Steps, st => st.Kind == MoveKind.BuildDown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepChasm_FollowerExecutesTheDescent_Unhurt(bool mirrored)
    {
        var (g, s, goal) = Chasm(mirrored);
        RunSim(g, s, goal);
    }

    /// <summary>A thin Ebonstone ledge sticking out of the wall mid-shaft: nothing to anchor a platform to below its
    /// edge, so the path bridges across the shaft to the far wall and builds down along it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThinLedge_BridgesToTheWallThenBuildsDown_Unhurt(bool mirrored)
    {
        var (g, _, goal) = Chasm(mirrored);
        int x0 = mirrored ? 11 : 10;
        for (int x = x0; x < x0 + 3; x++) g.Set(x, 20, new TileInfo(TileKind.Solid, PickPowerRequired: EbonstonePick));
        var start = new TilePoint(mirrored ? 12 : 10, 19);
        var r = RunSim(g, start, goal);
        Assert.Contains(r.Steps, st => st.Kind == MoveKind.BuildDown);
    }

    /// <summary>The one-row variant: a platform placed right under the one underfoot (anchored to it), dropped onto.</summary>
    [Fact]
    public void Follower_OneRowDrop_PlacesUnderThePlatformAndDropsOnIt()
    {
        var (g, _) = new GridBuilder(12, 50).Fill(4, 11, 7, 11, '=').Fill(0, 48, 11, 49, '#').Parse();
        var start = new TilePoint(5, 10);
        var steps = new[] { new PathStep(new TilePoint(5, 11), MoveKind.BuildDown, new TilePoint(5, 12)) };
        var sim = new MiniPhysics(g, start);
        var f = new PathFollower(steps, start: start);
        FollowerOutput o = default;
        for (int t = 0; t < 300 && (o = f.Tick(sim.Kinematics, g)).Status == FollowerStatus.Running; t++)
            sim.Step(o.Intent, SimTool.Platform);
        output.WriteLine($"{o.Status} {sim}");
        Assert.Equal(FollowerStatus.Arrived, o.Status);
        Assert.Equal(new TilePoint(5, 11), sim.StandingTile);
        Assert.Equal(TileKind.Platform, g.Get(5, 12).Kind);
        Assert.Equal(0, sim.Misuse);
        Assert.Null(sim.Hurt);
    }

    private PathResult RunSim(ArrayTileGrid g, TilePoint s, TilePoint goal)
    {
        var profile = Real(builds: 100);
        var r = Pathfinder.FindPath(g, s, goal, profile, new());
        Log(r);
        Assert.True(r.Success, r.FailureReason);
        var prev = s;
        foreach (var st in r.Steps)
        {
            Assert.True(st.Pos.Y - prev.Y <= profile.SafeFallTiles, $"unsafe drop {prev} -> {st.Pos}");
            prev = st.Pos;
        }

        var sim = new MiniPhysics(g, s) { PickPower = CopperPick, Platforms = 100 };
        var f = new PathFollower(r.Steps, start: s);
        var trace = new Queue<string>();
        FollowerOutput o = default;
        for (int t = 0; t < 8000; t++)
        {
            o = f.Tick(sim.Kinematics, g);
            if (o.Status != FollowerStatus.Running) break;
            var tool = o.Current?.Kind switch
            {
                MoveKind.Dig => SimTool.Pickaxe,
                MoveKind.BuildPlatform or MoveKind.BuildDown => SimTool.Platform,
                _ => SimTool.None,
            };
            sim.Step(o.Intent, tool);
            trace.Enqueue($"{sim} | {o.Current?.Kind}{o.Current?.Pos}");
            if (trace.Count > 40) trace.Dequeue();
            if (sim.Hurt != null) break;
        }
        if (o.Status != FollowerStatus.Arrived || sim.Hurt != null)
            foreach (var l in trace) output.WriteLine(l);
        output.WriteLine($"{o.Status} {sim}; placed {sim.Placed.Count}, max fall {sim.MaxFallTiles}");
        Assert.Equal(FollowerStatus.Arrived, o.Status);
        Assert.Null(sim.Hurt);
        Assert.Equal(0, sim.Misuse);
        Assert.Empty(sim.Mined);
        for (int i = 0; i < 40; i++) sim.Step(default);
        Assert.Equal(goal, sim.StandingTile);
        Assert.True(sim.MaxFallTiles <= profile.SafeFallTiles, $"fell {sim.MaxFallTiles}");
        Assert.Equal(r.Steps.Count(st => st.Kind is MoveKind.BuildDown or MoveKind.BuildPlatform), sim.Placed.Count);
        return r;
    }
}
