using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

/// <summary>
/// Vanilla has no swimming without Flippers (wiki "Swimming"): in water the player sinks and can only jump off the
/// bottom. Seen in game: explore walked into a surface lake 36 wide and 18 deep and drowned twice, because the planner
/// let everyone float across. A non-swimmer now crosses water only along its bottom (within breath) or over a
/// platform bridge; with <see cref="MovementProfile.CanSwim"/> it swims as before.
/// </summary>
public class PathfinderSwimTests(ITestOutputHelper output)
{
    private static readonly PathOptions NoWork = new(AllowDig: false, AllowBuild: false);

    private static MovementProfile Real(bool canSwim = false, int builds = 0, bool waterBreathing = false) =>
        JumpModel.Apply(new MovementProfile(BuildItemCount: builds, CanSwim: canSwim, WaterBreathing: waterBreathing),
            PlayerPhysics.Vanilla);

    private PathResult Find(ArrayTileGrid g, TilePoint s, TilePoint goal, MovementProfile p, PathOptions o)
    {
        var r = Pathfinder.FindPath(g, s, goal, p, o);
        output.WriteLine($"{r.Success} {r.FailureReason} nodes={r.NodesExpanded} "
                         + string.Join(" ", r.Steps.Select(st => $"{st.Kind}{st.Pos}{st.WorkTile}")));
        return r;
    }

    private static bool Wet(ITileGrid g, TilePoint p)
    {
        for (int y = p.Y - 2; y <= p.Y; y++)
            for (int x = p.X; x <= p.X + 1; x++)
                if (g.Get(x, y).Kind is TileKind.Water or TileKind.Honey) return true;
        return false;
    }

    private static bool Supported(ITileGrid g, TilePoint p) => g.Get(p.X, p.Y + 1).IsStandable || g.Get(p.X + 1, p.Y + 1).IsStandable;

    // ---- water jump height --------------------------------------------------------------------------------------

    [Fact]
    public void WaterJump_RisesEightTiles()
    {
        Assert.InRange(JumpModel.WaterJumpRisePx(), 128f, 144f);
        Assert.Equal(JumpModel.VanillaWaterJumpUp, JumpModel.WaterJumpUp());
        Assert.Equal(8, Real().MaxWaterJumpUp);
        Assert.False(new MovementProfile().CanSwim); // vanilla default: no swimming
    }

    [Fact]
    public void WaterJump_MatchesTheSim()
    {
        // Held jump off the bottom of a 20-deep shaft of water in MiniPhysics: the feet rise WaterJumpRisePx.
        var (g, m) = new GridBuilder(6, 26).Fill(0, 0, 5, 25, 'X').Fill(1, 2, 4, 24, '~').Put(2, 24, 'S').Parse();
        g.Set(2, 24, new TileInfo(TileKind.Water));
        var sim = new MiniPhysics(g, m['S']);
        float startY = sim.Y, minY = sim.Y;
        sim.Step(default);
        for (int t = 0; t < 120; t++)
        {
            sim.Step(new ControlIntent { Jump = true });
            minY = Math.Min(minY, sim.Y);
        }
        output.WriteLine($"rise {startY - minY:F1} px, model {JumpModel.WaterJumpRisePx():F1}");
        Assert.InRange(startY - minY, JumpModel.WaterJumpRisePx() - 3f, JumpModel.WaterJumpRisePx() + 3f);
    }

    // ---- the in-game lake: 36 wide, 18 deep ------------------------------------------------------------------------

    /// <summary>Flat shores (top at row 10) either side of a lake x=12..47, water rows 10..27, floor row 28.</summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) DeepLake() =>
        new GridBuilder(60, 32).Fill(0, 10, 59, 31, '#').Fill(12, 10, 47, 27, '~')
            .Put(4, 9, 'S').Put(54, 9, 'G').Parse();

    [Fact]
    public void DeepLake_NonSwimmerWithoutPlatforms_DoesNotCross()
    {
        var (g, m) = DeepLake();
        var r = Find(g, m['S'], m['G'], Real(), NoWork);
        Assert.False(r.Success);
    }

    [Fact]
    public void DeepLake_NonSwimmerBestEffort_StopsOnTheShore()
    {
        var (g, m) = DeepLake();
        var r = Find(g, m['S'], m['G'], Real(), NoWork with { BestEffort = true });
        if (r.Success && r.Steps.Count > 0)
        {
            var end = r.Steps[^1].Pos;
            Assert.True(!Wet(g, end) || !SurfaceDirection.HeadInLiquid(g, end), $"ends in the lake at {end}");
            Assert.True(Supported(g, end), $"ends afloat at {end}");
        }
    }

    [Fact]
    public void DeepLake_NonSwimmerBridgesOverIt()
    {
        var (g, m) = DeepLake();
        var r = Find(g, m['S'], m['G'], Real(builds: 60), new(AllowDig: false));
        Assert.True(r.Success, r.FailureReason);
        Assert.DoesNotContain(r.Steps, s => s.Kind == MoveKind.Swim);
        Assert.DoesNotContain(r.Steps, s => s.Kind != MoveKind.BuildPlatform && SurfaceDirection.HeadInLiquid(g, s.Pos));
        Assert.True(r.Steps.Count(s => s.Kind == MoveKind.BuildPlatform) >= 30);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
    }

    [Fact]
    public void DeepLake_SwimmerSwimsAcross()
    {
        var (g, m) = DeepLake();
        var r = Find(g, m['S'], m['G'], Real(canSwim: true), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.Swim);
    }

    [Fact]
    public void DeepLake_GoalOnTheBottom_IsRefusedForANonSwimmer()
    {
        // Left there with the head under, a non-swimmer can't get up again: the goal snaps elsewhere or fails.
        var (g, _) = DeepLake();
        var r = Find(g, new TilePoint(4, 9), new TilePoint(30, 27), Real(waterBreathing: false), NoWork);
        Assert.False(r.Success);
        var breathe = Find(g, new TilePoint(4, 9), new TilePoint(30, 27), Real(canSwim: true, waterBreathing: true), NoWork);
        Assert.True(breathe.Success, breathe.FailureReason);
    }

    [Fact]
    public void NonSwimmer_NeverRisesWithoutGround()
    {
        // Every rising Swim step of a non-swimmer path is part of a water jump that started on ground at most
        // MaxWaterJumpUp tiles lower; an afloat body never moves sideways while sinking.
        var (g, m) = SlopedLake(deepWidth: 1);
        var p = Real();
        var r = Find(g, m['S'], m['G'], p, NoWork);
        Assert.True(r.Success, r.FailureReason);
        var prev = m['S'];
        int? jumpFrom = Supported(g, prev) ? prev.Y : null;
        foreach (var s in r.Steps)
        {
            bool afloat = Wet(g, s.Pos) && !Supported(g, s.Pos);
            if (s.Pos.Y < prev.Y && afloat)
            {
                Assert.NotNull(jumpFrom);
                Assert.True(jumpFrom!.Value - s.Pos.Y <= p.MaxWaterJumpUp, $"{s.Pos}: rose past the water jump");
            }
            if (!afloat) jumpFrom = s.Pos.Y;
            else if (s.Pos.Y > prev.Y) jumpFrom = null; // sinking
            prev = s.Pos;
        }
    }

    // ---- shallow water is still fine --------------------------------------------------------------------------------

    [Fact]
    public void TwoDeepPuddle_IsWadedAcross()
    {
        // Water rows 10..11 (2 deep) between flat shores; no jump over it (8 wide), no platforms.
        var (g, m) = new GridBuilder(30, 16).Fill(0, 10, 29, 15, '#').Fill(10, 10, 17, 11, '~')
            .Put(3, 9, 'S').Put(25, 9, 'G').Parse();
        var r = Find(g, m['S'], m['G'], Real(), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
        Assert.DoesNotContain(r.Steps, s => SurfaceDirection.HeadInLiquid(g, s.Pos));
    }

    /// <summary>
    /// A lake whose water surface is row 6: a deep part (floor row 17, 11 deep: a water jump off it can't get the head
    /// out) <c>deepWidth + 1</c> tiles wide on the left against a wall, then a slope rising one tile per column to a
    /// dry shore (top row 7) on the right. S stands on the deep bottom at the wall, G on the right shore.
    /// </summary>
    internal static (ArrayTileGrid, Dictionary<char, TilePoint>) SlopedLake(int deepWidth, bool mirrored = false)
    {
        int slope0 = 2 + deepWidth, w = slope0 + 30;
        var b = new GridBuilder(w, 21).Fill(0, 0, 0, 20, 'X').Fill(0, 18, w - 1, 20, '#');
        for (int x = 1; x < w; x++)
        {
            int floor = Math.Max(7, 17 - Math.Max(0, x - slope0 + 1));
            b.Fill(x, floor, x, 17, '#');
            if (floor > 7 || x < slope0 + 10) b.Fill(x, 6, x, floor - 1, '~');
        }
        b.Put(1, 16, 'S').Put(w - 4, 6, 'G');
        if (!mirrored) return b.Parse();
        var (g, m) = ArrayTileGrid.Parse(b.Rows.Select(r => new string(r.Reverse().ToArray())).ToArray());
        foreach (var k in m.Keys.ToList()) m[k] = m[k] with { X = m[k].X - 1 }; // feet-left of the mirrored body
        return (g, m);
    }

    [Fact]
    public void SlopedLake_WadesOutUpTheShoreSlope()
    {
        var (g, m) = SlopedLake(deepWidth: 1);
        var r = Find(g, m['S'], m['G'], Real(), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
        // Along the bottom: never more than a water jump above the floor under the body.
        foreach (var s in r.Steps)
        {
            int floor = s.Pos.Y + 1;
            while (!g.Get(s.Pos.X, floor).IsStandable && !g.Get(s.Pos.X + 1, floor).IsStandable) floor++;
            Assert.True(floor - 1 - s.Pos.Y <= JumpModel.VanillaWaterJumpUp, $"{s.Pos} floats {floor - 1 - s.Pos.Y} above the bottom");
        }
    }

    [Fact]
    public void SlopedLake_WideDeepPart_IsTooLongForOneBreath()
    {
        // 20 tiles of deep bottom before the slope: the planner refuses it (the surfacing reflex walks it instead).
        var (g, m) = SlopedLake(deepWidth: 20);
        Assert.False(Find(g, m['S'], m['G'], Real(), NoWork).Success);
        Assert.True(Find(g, m['S'], m['G'], Real(waterBreathing: true), NoWork).Success);
    }

    // ---- surfacing reflex: walk along the bottom ----------------------------------------------------------------

    [Fact]
    public void Surface_NonSwimmerInDeepWater_WalksTheBottomToTheSlope()
    {
        var (g, m) = SlopedLake(deepWidth: 20);
        var c = SurfaceDirection.Choose(g, m['S'], canSwim: false);
        output.WriteLine($"air {c.Air} dx {c.Dx} up {c.Up}: " + string.Join(" ", c.Path));
        Assert.True(c.Found);
        Assert.Equal(1, c.Dx);
        Assert.False(c.Up); // jumping here can't reach air (11 deep)
        Assert.True(Supported(g, c.Path[0]));
        Assert.True(c.Air!.Value.X >= 20, $"air at {c.Air}");
    }

    [Fact]
    public void Surface_Swimmer_GoesStraightUp()
    {
        var (g, m) = SlopedLake(deepWidth: 20);
        var c = SurfaceDirection.Choose(g, m['S'], canSwim: true);
        Assert.True(c.Found);
        Assert.Equal(0, c.Dx);
        Assert.True(c.Up);
    }

    [Fact]
    public void Surface_NonSwimmerSinking_GoesDownFirst()
    {
        // Mid-water (nothing underfoot) in the deep part: the first step can only be down.
        var (g, m) = SlopedLake(deepWidth: 20);
        var mid = new TilePoint(m['S'].X, m['S'].Y - 4);
        var c = SurfaceDirection.Choose(g, mid, canSwim: false);
        Assert.True(c.Found);
        Assert.True(c.Path[0].Y > mid.Y, $"first step {c.Path[0]}");
        Assert.False(c.Up);
    }
}
