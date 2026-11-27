using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace LlmPlayer.Core.Tests;

public class SurfaceDirectionTests(ITestOutputHelper output)
{
    /// <summary>A 6-deep pool (rows 8..13) under open air; optionally a rock lid over its left part (cols 3..9).</summary>
    private static (ArrayTileGrid, Dictionary<char, TilePoint>) Pool(bool lid, bool mirrored = false)
    {
        var b = new GridBuilder(24, 16).Fill(0, 0, 23, 15, 'X').Fill(1, 1, 22, 7, '.').Fill(3, 8, 12, 13, '~');
        if (lid) b.Fill(3, 8, 9, 9, 'X');
        b.Put(4, 13, 'S');
        var rows = b.Rows;
        if (mirrored) rows = rows.Select(r => new string(r.Reverse().ToArray())).ToArray();
        var (g, m) = ArrayTileGrid.Parse(rows);
        if (mirrored) m['S'] = m['S'] with { X = m['S'].X - 1 };
        return (g, m);
    }

    [Fact]
    public void OpenWater_GoesStraightUp()
    {
        var (g, m) = Pool(lid: false);
        var c = SurfaceDirection.Choose(g, m['S']);
        Assert.True(c.Found);
        Assert.True(c.Up);
        Assert.Equal(0, c.Dx);
        Assert.Equal(m['S'].X, c.Air!.Value.X);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    public void UnderALid_GoesSidewaysToTheOpening(bool mirrored, int dx)
    {
        var (g, m) = Pool(lid: true, mirrored);
        var c = SurfaceDirection.Choose(g, m['S']);
        output.WriteLine(string.Join(" ", c.Path));
        Assert.True(c.Found);
        Assert.Equal(dx, c.Dx);
        Assert.False(SurfaceDirection.HeadInLiquid(g, c.Air!.Value));
    }

    [Fact]
    public void HeadAlreadyOut_NothingToDo()
    {
        var (g, _) = Pool(lid: false);
        var c = SurfaceDirection.Choose(g, new TilePoint(4, 9)); // head row 7: air
        Assert.True(c.Found);
        Assert.Empty(c.Path);
        Assert.False(c.Up);
    }

    [Fact]
    public void NoKnownAir_TriesUp()
    {
        var (g, m) = new GridBuilder(10, 10).Fill(0, 0, 9, 9, 'X').Fill(2, 2, 7, 8, '~').Fill(2, 1, 7, 1, '?')
            .Put(3, 8, 'S').Parse();
        g.Set(3, 8, new TileInfo(TileKind.Water));
        var c = SurfaceDirection.Choose(g, m['S']);
        Assert.False(c.Found);
        Assert.True(c.Up);
    }

    [Fact]
    public void UnknownIsNeverTheAir_ButMayBeCrossed()
    {
        // Air two tiles above the water, an unknown band between: the target is the known air beyond it.
        var (g, m) = new GridBuilder(10, 14).Fill(0, 0, 9, 13, 'X').Fill(1, 1, 8, 4, '.').Fill(2, 5, 7, 6, '?')
            .Fill(2, 7, 7, 12, '~').Put(3, 12, 'S').Parse();
        g.Set(3, 12, new TileInfo(TileKind.Water));
        var c = SurfaceDirection.Choose(g, m['S']);
        Assert.True(c.Found);
        Assert.Equal(TileKind.Air, g.Get(c.Air!.Value.X, c.Air.Value.Y - 2).Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Sim_SurfacerGetsTheHeadOutOfTheWater(bool lid, bool mirrored)
    {
        var (g, m) = Pool(lid, mirrored);
        g.Set(m['S'].X, m['S'].Y, new TileInfo(TileKind.Water));
        g.Set(m['S'].X + (mirrored ? 1 : 0), m['S'].Y, new TileInfo(TileKind.Water));
        var sim = new MiniPhysics(g, m['S']);
        var s = new Surfacer();
        int t = 0;
        for (; t < 600; t++)
        {
            if (HeadDry(g, sim)) break;
            sim.Step(s.Tick(g, sim.Kinematics));
        }
        output.WriteLine($"{sim}");
        Assert.True(HeadDry(g, sim), $"still under water after {t} ticks: {sim}");
        Assert.Null(sim.Hurt);
    }

    /// <summary>
    /// No Flippers, on the bottom of a lake 11 deep (a jump off it can't get the head out) 20 tiles from the shore
    /// slope: the surfacer walks along the bottom toward the shallow end and gets the head out well within the breath
    /// the reflex starts with (45 % of 200 breath at 7 ticks each: 630 ticks).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sim_NonSwimmerWalksTheBottomToTheShallowEnd(bool mirrored)
    {
        var (g, m) = PathfinderSwimTests.SlopedLake(deepWidth: 20, mirrored);
        var start = m['S'];
        var sim = new MiniPhysics(g, start);
        var s = new Surfacer(canSwim: false);
        float minY = sim.Y;
        int t = 0;
        for (; t < 630; t++)
        {
            if (HeadDry(g, sim)) break;
            sim.Step(s.Tick(g, sim.Kinematics));
            minY = Math.Min(minY, sim.Y);
        }
        output.WriteLine($"t={t} {sim}");
        Assert.True(HeadDry(g, sim), $"still under water after {t} ticks: {sim}");
        Assert.Null(sim.Hurt);
        int moved = Math.Abs(sim.StandingTile.X - start.X);
        Assert.True(moved >= 18, $"only {moved} tiles toward the shore");
    }

    /// <summary>Vanilla's drown box: 10 px wide, centred, from 2 px above the hitbox top, 12 px tall (no liquid in it).</summary>
    private static bool HeadDry(ArrayTileGrid g, MiniPhysics sim)
    {
        float cx = sim.X + MiniPhysics.W / 2f, top = sim.Y - 2;
        for (float y = top; y < top + 12; y += 4)
            for (float x = cx - 5; x < cx + 5; x += 4)
                if (SurfaceDirection.IsLiquid(g.Get((int)MathF.Floor(x / 16f), (int)MathF.Floor(y / 16f)).Kind)) return false;
        return true;
    }
}
