using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class ExploreTargetsTests
{
    private static readonly MovementProfile Dry = new();
    private static readonly TilePoint[] None = Array.Empty<TilePoint>();

    /// <summary>Seen in game walking west at (2914,500): the ground rises to the west as a hill whose inside is
    /// unrevealed. Player on flat ground (feet row 29) at x 50; the hill's top (feet row 19) spans x 0..24, its slope
    /// steps up from x 25 to x 34; everything under the ground is Unknown.</summary>
    private static ArrayTileGrid Hill()
    {
        var b = new GridBuilder(60, 40).Fill(0, 30, 59, 39, '?');
        for (int x = 25; x <= 34; x++) b.Fill(x, 30 - (35 - x), x, 29, '?').Put(x, 30 - (35 - x), '#'); // stairs up-left
        b.Fill(0, 20, 24, 29, '?').Fill(0, 20, 24, 20, '#');
        b.Fill(35, 30, 59, 30, '#');
        return b.Parse().Grid;
    }

    [Fact]
    public void Surface_ClimbsTheHill_ToTheFarthestKnownSurface()
    {
        var g = Hill();
        var here = new TilePoint(50, 29);
        var t = ExploreTargets.Surface(g, here, -1, 40, Dry, None);
        Assert.Equal(new TilePoint(10, 19), t);
        Assert.True(Pathfinder.CanStand(g, t!.Value));
    }

    [Fact]
    public void Ahead_AtTheCurrentHeight_FindsNothingFarOnTheHill()
    {
        // The old leg target: ±15 rows of the current height still reaches the hill top here; the surface target is
        // the same column. With a taller hill (beyond ±15) only the surface search follows it.
        var b = new GridBuilder(60, 60).Fill(0, 50, 59, 59, '?');
        b.Fill(0, 21, 30, 49, '?').Fill(0, 20, 30, 20, '#');      // plateau top: feet row 19, 30 rows up
        b.Fill(31, 20, 31, 49, '?');                              // its east face, rock (unknown inside)
        for (int y = 20; y <= 49; y++) b.Put(31, y, '#');
        b.Fill(32, 50, 59, 50, '#');
        var g = b.Parse().Grid;
        var here = new TilePoint(50, 49);
        var ahead = ExploreTargets.Ahead(g, here, -1, true, 40, 15, Dry, None);
        Assert.True(ahead == null || ahead.Value.X >= 32, $"ahead {ahead}");
        var surface = ExploreTargets.Surface(g, here, -1, 40, Dry, None);
        Assert.Equal(new TilePoint(10, 19), surface);
    }

    [Fact]
    public void Surface_FollowsTheGroundDownIntoAValley()
    {
        var b = new GridBuilder(40, 30).Fill(0, 26, 39, 29, '?').Fill(0, 25, 39, 25, '#'); // valley floor: feet row 24
        b.Fill(30, 15, 39, 24, '?').Fill(30, 15, 39, 15, '#');                              // high ground: feet row 14
        var g = b.Parse().Grid;
        var t = ExploreTargets.Surface(g, new TilePoint(35, 14), -1, 30, Dry, None);
        Assert.Equal(new TilePoint(5, 24), t);
    }

    [Fact]
    public void Surface_UnknownBeyond_WalksToTheFarthestKnownPoint()
    {
        var g = Hill();
        for (int y = 0; y < 40; y++)
            for (int x = 0; x <= 20; x++)
                g.Set(x, y, new TileInfo(TileKind.Unknown));
        var t = ExploreTargets.Surface(g, new TilePoint(50, 29), -1, 40, Dry, None);
        Assert.Equal(new TilePoint(21, 19), t);
    }

    [Fact]
    public void Surface_UnknownAboveACave_IsNoTarget()
    {
        // Columns 10..11: unrevealed down to row 12, then a 6-row known cave pocket on rock: the real surface may be up
        // in the unknown part, so no target there. Columns 2..3: the known plateau top is the surface.
        var b = new GridBuilder(30, 40).Fill(0, 19, 29, 39, '#').Fill(0, 0, 29, 18, '.');
        b.Fill(9, 0, 12, 12, '?');
        var g = b.Parse().Grid;
        Assert.Null(ExploreTargets.SurfaceAt(g, 10, 18, Dry));
        Assert.Equal(new TilePoint(2, 18), ExploreTargets.SurfaceAt(g, 2, 18, Dry));
        // Unknown sky far above (beyond the known rows) is fine.
        b.Fill(20, 0, 23, 5, '?');
        g = b.Parse().Grid;
        Assert.Equal(new TilePoint(21, 18), ExploreTargets.SurfaceAt(g, 21, 18, Dry));
    }

    [Fact]
    public void Surface_SkipsSpotsNearAFailedWalk()
    {
        var g = Hill();
        var t = ExploreTargets.Surface(g, new TilePoint(50, 29), -1, 40, Dry, new[] { new TilePoint(10, 19) });
        Assert.Equal(new TilePoint(13, 19), t);
    }

    [Fact]
    public void Surface_OverAOneWideHole_StandsOverIt()
    {
        var b = new GridBuilder(30, 12).Fill(0, 10, 29, 11, '#').Put(12, 10, '.').Put(12, 11, '.');
        var g = b.Parse().Grid;
        Assert.Equal(new TilePoint(12, 9), ExploreTargets.SurfaceAt(g, 12, 9, Dry));
        Assert.Equal(new TilePoint(11, 9), ExploreTargets.SurfaceAt(g, 11, 9, Dry));
    }
}
