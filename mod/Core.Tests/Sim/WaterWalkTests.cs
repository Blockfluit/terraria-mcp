using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests.Sim;

/// <summary>
/// Water Walking (potion / boots): the top liquid cell holds the player like a platform (vanilla
/// Collision.WaterCollision), so a lake the player can't swim or bridge can be walked over, within the buff's time.
/// </summary>
public class WaterWalkTests(ITestOutputHelper output)
{
    private static readonly PathOptions NoWork = new(AllowDig: false, AllowBuild: false);

    private static MovementProfile Real(bool waterWalk = false, bool lavaWalk = false, int ticks = int.MaxValue, int builds = 0) =>
        JumpModel.Apply(new MovementProfile(BuildItemCount: builds, WaterWalk: waterWalk, LavaWalk: lavaWalk, WaterWalkTicks: ticks),
            PlayerPhysics.Vanilla);

    /// <summary>Shores (top at row 10) either side of a 60-wide lake x=12..71 of <paramref name="liquid"/>, its surface
    /// <paramref name="drop"/> rows below the shore top, floor at row 28.</summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) Lake(char liquid = '~', int drop = 0, bool mirror = false)
    {
        var b = new GridBuilder(84, 32).Fill(0, 10, 83, 31, '#').Fill(12, 10, 71, 27, '.').Fill(12, 10 + drop, 71, 27, liquid);
        return mirror ? b.Put(78, 9, 'S').Put(4, 9, 'G').Parse() : b.Put(4, 9, 'S').Put(78, 9, 'G').Parse();
    }

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
                if (g.Get(x, y).Kind is TileKind.Water or TileKind.Honey or TileKind.Lava) return true;
        return false;
    }

    // ---- planner ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lake60_WithoutWaterWalk_IsImpassable(bool mirror)
    {
        var (g, m) = Lake(mirror: mirror);
        Assert.False(Find(g, m['S'], m['G'], Real(), NoWork).Success);
    }

    [Theory]
    [InlineData('~', 0, false)]
    [InlineData('~', 0, true)]
    [InlineData('h', 0, false)]
    [InlineData('~', 2, false)] // surface 2 rows below the shore: drop onto it, jump back up the far bank
    [InlineData('~', 2, true)]
    public void Lake60_WithWaterWalk_IsWalkedOver(char liquid, int drop, bool mirror)
    {
        var (g, m) = Lake(liquid, drop, mirror);
        var r = Find(g, m['S'], m['G'], Real(waterWalk: true), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.Equal(m['G'], r.Steps[^1].Pos);
        Assert.DoesNotContain(r.Steps, s => s.Kind is MoveKind.Swim or MoveKind.DropThrough);
        Assert.DoesNotContain(r.Steps, s => Wet(g, s.Pos));
        Assert.Contains(r.Steps, s => s.Pos.X == 40 && s.Pos.Y == 9 + drop); // on the surface mid-lake
    }

    [Fact]
    public void Lava_NeedsLavaWalk()
    {
        var (g, m) = Lake('L');
        Assert.False(Find(g, m['S'], m['G'], Real(waterWalk: true), NoWork).Success);
        var r = Find(g, m['S'], m['G'], Real(waterWalk: true, lavaWalk: true), NoWork);
        Assert.True(r.Success, r.FailureReason);
        Assert.DoesNotContain(r.Steps, s => Wet(g, s.Pos));
    }

    [Fact]
    public void BuffTime_TooShortForTheCrossing_IsRefused()
    {
        var (g, m) = Lake();
        // 60 tiles at 3 px/tick with the pace slack: ~430 ticks on the water, plus the shore and the margin.
        var r = Find(g, m['S'], m['G'], Real(waterWalk: true, ticks: 400), NoWork);
        Assert.False(r.Success);
        Assert.Contains("water walking would run out", r.FailureReason);
        Assert.True(Find(g, m['S'], m['G'], Real(waterWalk: true, ticks: 60 * 60), NoWork).Success);
    }

    [Fact]
    public void BuffTime_ShortBuffStillAllowsANarrowPond()
    {
        var (g, m) = new GridBuilder(40, 20).Fill(0, 10, 39, 19, '#').Fill(15, 10, 22, 17, '~')
            .Put(10, 9, 'S').Put(28, 9, 'G').Parse();
        Assert.True(Find(g, m['S'], m['G'], Real(waterWalk: true, ticks: 400), NoWork).Success);
    }

    [Fact]
    public void BestEffort_ShortBuff_NeverStopsOnTheWater()
    {
        var (g, m) = Lake();
        var r = Find(g, m['S'], m['G'], Real(waterWalk: true, ticks: 400), NoWork with { BestEffort = true });
        // The partial plan heads as far as the buff allows, but never ends on the water (the player would sink there).
        Assert.True(r.Success && r.Steps.Count > 0, r.FailureReason);
        var end = r.Steps[^1].Pos;
        Assert.True(g.Get(end.X, end.Y + 1).IsStandable || g.Get(end.X + 1, end.Y + 1).IsStandable, $"ends on the water at {end}");
    }

    [Fact]
    public void Platforms_AreNotAnchoredToTheWater()
    {
        // Only water around: a non-water-walker can't hang a bridge platform off the surface. With water walking and
        // platforms the plan walks (no build steps at all).
        var (g, m) = Lake();
        var r = Find(g, m['S'], m['G'], Real(waterWalk: true, builds: 100), new PathOptions(AllowDig: false));
        Assert.True(r.Success, r.FailureReason);
        Assert.DoesNotContain(r.Steps, s => s.Kind == MoveKind.BuildPlatform);
    }

    [Fact]
    public void CanStand_CountsTheSurfaceOnlyWhenWaterWalking()
    {
        var (g, _) = Lake();
        var onWater = new TilePoint(40, 9);
        Assert.False(Pathfinder.CanStand(g, onWater));
        Assert.False(Pathfinder.CanStand(g, onWater, Real()));
        Assert.True(Pathfinder.CanStand(g, onWater, Real(waterWalk: true)));
        Assert.False(Pathfinder.CanStand(g, new TilePoint(40, 12), Real(waterWalk: true))); // inside the lake
        Assert.False(Pathfinder.CanStand(g, onWater, Real(waterWalk: true, ticks: 3600))); // a buff: no stopping there
    }

    [Fact]
    public void GoalOnTheWater_OnlyWithUnlimitedWaterWalk()
    {
        var (g, m) = Lake();
        var mid = new TilePoint(40, 9);
        var boots = Find(g, m['S'], mid, Real(waterWalk: true), NoWork);
        Assert.True(boots.Success, boots.FailureReason);
        Assert.Equal(mid, boots.Steps[^1].Pos);
        // With the potion the player would be left standing there while the buff runs out.
        var potion = Find(g, m['S'], mid, Real(waterWalk: true, ticks: 3600), NoWork);
        Assert.False(potion.Success);
    }

    [Fact]
    public void StandingTile_FeetSunkIntoTheTopLiquidCell_IsTheRowAbove()
    {
        var (g, _) = Lake();
        // Resting 6 px into the surface cell (row 10): a half-full cell's liquid top.
        var k = NavMath.FromPixels(40 * 16 + 6, 10 * 16 + 6 - PlayerPhysics.HitboxHeightPx, 0, 0);
        Assert.Equal(new TilePoint(40, 9), NavMath.StandingTile(k, g));
    }

    // ---- follower + sim -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Sim_WalksOverTheLakeAndArrivesDry(int drop, bool mirror)
    {
        var (g, m) = Lake('~', drop, mirror);
        var sim = new MiniPhysics(g, m['S']) { WaterWalk = true, Platforms = 0 };
        var move = new MoveToSim(g, sim, m['G'], Real(waterWalk: true), NoWork, tolerance: 0, log: output.WriteLine);
        var outcome = move.Run(4000);
        if (outcome != MoveOutcome.Arrived)
            foreach (var l in move.Trace) output.WriteLine(l);
        output.WriteLine($"{outcome}: {move.Reason} after {sim.Ticks} ticks, {move.Replans} replans, wet {sim.WetTicks}, down {sim.DownTicks}");
        Assert.Equal(MoveOutcome.Arrived, outcome);
        Assert.Null(sim.Hurt);
        Assert.Equal(0, sim.WetTicks);
        Assert.Equal(0, sim.DownTicks);
        Assert.Equal(m['G'], sim.StandingTile);
    }

    [Fact]
    public void Sim_WithoutWaterWalk_TheSameLakeSinksThePlayer()
    {
        // The sim's own surface collision is what holds the player: without it, standing on the lake sinks.
        var (g, _) = Lake();
        var sim = new MiniPhysics(g, new TilePoint(40, 9));
        for (int i = 0; i < 30; i++) sim.Step(default);
        Assert.True(sim.WetTicks > 0);
        var ww = new MiniPhysics(g, new TilePoint(40, 9)) { WaterWalk = true };
        for (int i = 0; i < 30; i++) ww.Step(default);
        Assert.Equal(0, ww.WetTicks);
        Assert.Equal(new TilePoint(40, 9), ww.StandingTile);
        // Holding Down sinks through.
        for (int i = 0; i < 30; i++) ww.Step(new ControlIntent { Down = true });
        Assert.True(ww.WetTicks > 0);
    }

    [Fact]
    public void Sim_BuffTicksSoLowThePlanRefuses_NeverStepsOntoTheWater()
    {
        var (g, m) = Lake();
        var sim = new MiniPhysics(g, m['S']) { WaterWalk = true, WaterWalkTicks = 400, Platforms = 0 };
        var move = new MoveToSim(g, sim, m['G'], Real(waterWalk: true, ticks: 400), NoWork, tolerance: 0, log: output.WriteLine);
        var outcome = move.Run(2000);
        output.WriteLine($"{outcome}: {move.Reason}");
        Assert.NotEqual(MoveOutcome.Arrived, outcome);
        Assert.Equal(0, sim.WetTicks);
    }
}
