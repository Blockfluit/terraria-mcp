using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class CaveTunnelTests
{
    private const int Power = 35;

    /// <summary>
    /// An exhausted cave pocket (air x 10..20, y 10..12), the player 'S' feet-left at (12,12). The rock within
    /// <paramref name="ring"/> tiles of the pocket is known (lit by its torches), everything beyond is Unknown:
    /// unrevealed rock and caves. <paramref name="edit"/> may change cells.
    /// </summary>
    private static (ArrayTileGrid G, TilePoint S) Pocket(int ring = 4, Action<char[][]>? edit = null)
    {
        const int w = 40, h = 50;
        var rows = new char[h][];
        for (int y = 0; y < h; y++)
        {
            rows[y] = new char[w];
            for (int x = 0; x < w; x++)
            {
                bool inPocket = x >= 10 && x <= 20 && y >= 10 && y <= 12;
                int dx = Math.Max(0, Math.Max(10 - x, x - 20)), dy = Math.Max(0, Math.Max(10 - y, y - 12));
                rows[y][x] = inPocket ? '.' : Math.Max(dx, dy) <= ring ? '#' : '?';
            }
        }
        rows[12][12] = 'S';
        edit?.Invoke(rows);
        var (g, m) = ArrayTileGrid.Parse(rows.Select(r => new string(r)).ToArray());
        return (g, m['S']);
    }

    [Fact]
    public void ExhaustedPocketTunnelsDownTowardTheUnknown()
    {
        var (g, s) = Pocket();
        Assert.Null(CaveFrontier.Pick(g, s, s, 60, pickPower: Power)); // nothing left to walk to
        var d = CaveTunnel.Choose(g, s, s, 60, Power);
        Assert.NotNull(d.Choice);
        Assert.Equal(TunnelDir.Down, d.Choice!.Dir);
        Assert.Equal(12, d.Choice.Start.Y); // from the pocket's floor
        Assert.Equal(4, d.Choice.ToUnknown); // 4 tiles of known rock, then the unknown
        Assert.True(d.Choice.UnknownMass >= CaveTunnel.MinUnknownMass);
    }

    [Fact]
    public void LavaBeyondTheRockBelowRefusesDownAndGoesSideways()
    {
        // A lava pool revealed below (e.g. its glow): known lava 7 tiles under the floor, in the unknown beyond.
        var (g, s) = Pocket(edit: r => { for (int x = 6; x <= 26; x++) r[19][x] = 'L'; });
        var d = CaveTunnel.Choose(g, s, s, 60, Power);
        Assert.NotNull(d.Choice);
        Assert.NotEqual(TunnelDir.Down, d.Choice!.Dir);
        Assert.Contains(d.Refused, x => x.Dir == TunnelDir.Down && x.Why.Contains("lava"));
        Assert.Contains("lava", d.Summary);
    }

    [Fact]
    public void LavaOnEverySideRefusesEverything()
    {
        var (g, s) = Pocket(edit: r =>
        {
            for (int x = 0; x < 40; x++) r[18][x] = 'L'; // under the pocket
            for (int y = 0; y < 50; y++) { r[y][4] = 'L'; r[y][27] = 'L'; } // left and right
        });
        var d = CaveTunnel.Choose(g, s, s, 60, Power);
        Assert.Null(d.Choice);
        Assert.All(new[] { TunnelDir.Down, TunnelDir.Left, TunnelDir.Right },
            dir => Assert.Contains(d.Refused, x => x.Dir == dir && x.Why.Contains("lava")));
    }

    [Fact]
    public void UnbreakableOrTooHardRockBelowIsRefused()
    {
        var (g, s) = Pocket(edit: r => { for (int x = 5; x <= 25; x++) r[14][x] = 'X'; });
        var d = CaveTunnel.Choose(g, s, s, 60, Power);
        Assert.NotNull(d.Choice);
        Assert.NotEqual(TunnelDir.Down, d.Choice!.Dir);
        Assert.Contains(d.Refused, x => x.Dir == TunnelDir.Down && x.Why.Contains("unbreakable"));
    }

    [Fact]
    public void WaterBesideTheShaftIsRefused()
    {
        // Every lowest stand has water right beside its shaft column 2 rows down.
        var (g, s) = Pocket(edit: r => { for (int x = 5; x <= 25; x += 3) r[14][x] = '~'; });
        var (choice, why) = CaveTunnel.Evaluate(g, new TilePoint(12, 12), TunnelDir.Down, Power);
        Assert.Null(choice);
        Assert.Contains("flood", why);
    }

    [Fact]
    public void SidewaysWhenTheUnknownIsMuchCloserThere()
    {
        // The rock below is known 20 deep; to the right the unknown starts right behind the wall.
        var (g, s) = Pocket(edit: r =>
        {
            for (int y = 0; y < 50; y++)
                for (int x = 0; x < 40; x++)
                {
                    if (y >= 13 && y <= 35 && x >= 0 && x <= 28 && r[y][x] == '?') r[y][x] = '#';
                    if (y <= 16 && x >= 22) r[y][x] = '?';
                }
        });
        var d = CaveTunnel.Choose(g, s, s, 60, Power);
        Assert.Equal(TunnelDir.Right, d.Choice!.Dir);
    }

    [Fact]
    public void TriedDirectionsAreSkipped()
    {
        var (g, s) = Pocket();
        var d = CaveTunnel.Choose(g, s, s, 60, Power, (dir, _) => dir == TunnelDir.Down);
        Assert.NotEqual(TunnelDir.Down, d.Choice!.Dir);
    }

    [Fact]
    public void NothingWithoutAPickaxeOrUnknown()
    {
        var (g, s) = Pocket();
        Assert.Null(CaveTunnel.Choose(g, s, s, 60, 0).Choice);
        var (known, ks) = Pocket(ring: 60); // the whole map is known rock
        var d = CaveTunnel.Choose(known, ks, ks, 60, Power, maxTiles: 8);
        Assert.Null(d.Choice);
        Assert.Contains("no unexplored space", d.Summary);
    }

    [Fact]
    public void FrontiersInTheTunnelFaceCanBeIgnored()
    {
        // Mid-tunnel: the dug shaft (columns 12..13 down to y 16) touches unknown rock at its face.
        var (g, s) = Pocket(ring: 4, edit: r =>
        {
            for (int y = 13; y <= 16; y++) { r[y][12] = '.'; r[y][13] = '.'; }
            r[12][12] = '.';
            r[16][12] = 'S';
            for (int y = 17; y <= 18; y++) for (int x = 10; x <= 15; x++) r[y][x] = '?';
        });
        var start = new TilePoint(12, 12);
        bool Face(TilePoint c) => CaveTunnel.InCorridor(start, TunnelDir.Down, CaveTunnel.MaxTiles, c);
        Assert.NotNull(CaveFrontier.NearestFrontier(g, s, s, 60, Power));
        Assert.Null(CaveFrontier.Pick(g, s, s, 60, pickPower: Power, ignoreFrontier: Face));
        Assert.Null(CaveFrontier.NearestFrontier(g, s, s, 60, Power, ignoreFrontier: Face));
    }

    [Fact]
    public void BreakingIntoACaveOutsideTheCorridorIsAFrontier()
    {
        // The shaft broke into a cave below whose far side runs into the unknown.
        var (g, s) = Pocket(ring: 4, edit: r =>
        {
            for (int y = 13; y <= 17; y++) { r[y][12] = '.'; r[y][13] = '.'; }
            for (int y = 18; y <= 20; y++) for (int x = 4; x <= 24; x++) r[y][x] = '.';
            for (int x = 4; x <= 24; x++) r[21][x] = '#';
            for (int y = 17; y <= 21; y++) { r[y][2] = '?'; r[y][3] = '?'; }
            r[12][12] = '.';
            r[20][12] = 'S';
        });
        var start = new TilePoint(12, 12);
        bool Face(TilePoint c) => CaveTunnel.InCorridor(start, TunnelDir.Down, CaveTunnel.MaxTiles, c);
        var pick = CaveFrontier.Pick(g, s, s, 60, pickPower: Power, ignoreFrontier: Face);
        Assert.NotNull(pick);
        Assert.False(Face(pick!.Frontier));
        Assert.Equal(20, pick.Stand.Y); // on the new cave's floor
    }

    [Theory]
    [InlineData(TunnelDir.Down, 12, 30, true)]
    [InlineData(TunnelDir.Down, 15, 30, false)]
    [InlineData(TunnelDir.Down, 12, 53, false)]
    [InlineData(TunnelDir.Left, 0, 12, true)]
    [InlineData(TunnelDir.Left, 0, 14, false)]
    [InlineData(TunnelDir.Right, 30, 9, true)]
    [InlineData(TunnelDir.Right, 5, 11, false)]
    public void Corridor(TunnelDir dir, int x, int y, bool inside) =>
        Assert.Equal(inside, CaveTunnel.InCorridor(new TilePoint(12, 12), dir, CaveTunnel.MaxTiles, new TilePoint(x, y)));

    [Theory]
    [InlineData(200, 250.4, 61)]
    [InlineData(251, 250.4, 0)]
    [InlineData(250, 250.0, 10)]
    public void DescendsBelowTheSurfaceLayer(int feetY, double surface, int expected) =>
        Assert.Equal(expected, CaveTunnel.DescentDistance(feetY, surface));
}
