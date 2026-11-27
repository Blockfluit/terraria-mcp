using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

/// <summary>The three explore_caves stalls seen in game at (3972,1197): tunnels counted without digging, the same
/// unreachable ledge retried call after call, and no way out of an exhausted neighbourhood.</summary>
public class CaveRelocateTests
{
    private const int Power = 35;

    /// <summary>
    /// A 300 x 100 map, the player 'S' feet-left at (150,50) in a small exhausted pocket (air x 146..156, y 48..50).
    /// Everything is known rock unless <paramref name="edit"/> changes it (e.g. marks a region Unknown).
    /// </summary>
    private static (ArrayTileGrid G, TilePoint S) Map(Action<char[][]>? edit = null)
    {
        const int w = 300, h = 100;
        var rows = new char[h][];
        for (int y = 0; y < h; y++)
        {
            rows[y] = new char[w];
            for (int x = 0; x < w; x++)
                rows[y][x] = x >= 146 && x <= 156 && y >= 48 && y <= 50 ? '.' : '#';
        }
        edit?.Invoke(rows);
        rows[50][150] = 'S';
        var (g, m) = ArrayTileGrid.Parse(rows.Select(r => new string(r)).ToArray());
        return (g, m['S']);
    }

    private static void Fill(char[][] r, int x0, int x1, int y0, int y1, char c)
    {
        for (int y = Math.Max(0, y0); y <= Math.Min(r.Length - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(r[0].Length - 1, x1); x++)
                r[y][x] = c;
    }

    // ---- relocation --------------------------------------------------------------------------------------------

    [Fact]
    public void ExhaustedNeighbourhoodRelocatesTowardTheUnexploredRegion()
    {
        // Unexplored country starts 60 tiles to the left (x <= 90); right and below are known.
        var (g, s) = Map(r => Fill(r, 0, 90, 0, 99, '?'));
        Assert.Null(CaveFrontier.Pick(g, s, s, 160, pickPower: Power));
        Assert.Null(CaveTunnel.Choose(g, s, s, 160, Power).Choice); // no short tunnel reaches it either

        var d = CaveRelocate.Choose(g, s, Power, maxScan: 100);
        Assert.NotNull(d.Choice);
        var c = d.Choice!;
        Assert.Equal(TunnelDir.Left, c.Dir);
        Assert.Equal(s, c.Start);
        Assert.InRange(c.RegionAt, 50, 60); // the first window that's >= 85 % unknown (the region starts 60 out)
        Assert.Equal(c.RegionAt + CaveRelocate.Along / 2, c.Distance); // into the region's middle
        Assert.True(c.UnknownShare >= CaveRelocate.MinShare);
        Assert.Contains(d.Refused, x => x.Dir == TunnelDir.Right && x.Why.Contains("no large unexplored region"));
    }

    [Fact]
    public void ExploredCountryIsNotARegion()
    {
        // To the left: explored caves (every other row known) - half unknown, never a target. To the right, farther
        // away, untouched country.
        var (g, s) = Map(r =>
        {
            for (int y = 0; y < 100; y += 2) Fill(r, 0, 120, y, y, '?');
            Fill(r, 230, 299, 0, 99, '?');
        });
        var d = CaveRelocate.Choose(g, s, Power, maxScan: 110);
        Assert.Equal(TunnelDir.Right, d.Choice!.Dir);
        Assert.Contains(d.Refused, x => x.Dir == TunnelDir.Left);
    }

    [Fact]
    public void LavaOrProtectedTilesOnTheWayRefuseTheDirection()
    {
        var (lava, s) = Map(r => { Fill(r, 0, 90, 0, 99, '?'); Fill(r, 120, 122, 52, 53, 'L'); });
        var d = CaveRelocate.Choose(lava, s, Power, maxScan: 100);
        Assert.Null(d.Choice);
        Assert.Contains(d.Refused, x => x.Dir == TunnelDir.Left && x.Why.Contains("lava"));

        var (prot, s2) = Map(r => { Fill(r, 0, 90, 0, 99, '?'); Fill(r, 110, 110, 48, 50, 'P'); });
        Assert.Contains("protected", CaveRelocate.Choose(prot, s2, Power, maxScan: 100).Summary);

        var (hard, s3) = Map(r => { Fill(r, 0, 90, 0, 99, '?'); Fill(r, 110, 110, 48, 50, 'X'); });
        Assert.Contains("unbreakable", CaveRelocate.Choose(hard, s3, Power, maxScan: 100).Summary);
    }

    [Fact]
    public void WaterBesideRockToDigRefusesButACaveOnTheWayIsFine()
    {
        // Water right above the tunnel's rock would pour in.
        var (wet, s) = Map(r => { Fill(r, 0, 90, 0, 99, '?'); Fill(r, 115, 118, 47, 47, '~'); });
        Assert.Contains("flood", CaveRelocate.Choose(wet, s, Power, maxScan: 100).Summary);

        // A known dry cave across the way is just walked through.
        var (cave, s2) = Map(r => { Fill(r, 0, 90, 0, 99, '?'); Fill(r, 105, 125, 45, 50, '.'); });
        Assert.Equal(TunnelDir.Left, CaveRelocate.Choose(cave, s2, Power, maxScan: 100).Choice!.Dir);
    }

    [Fact]
    public void DownWhenTheUnknownLiesBelow()
    {
        // A band only 20 deep (y 80..99) at the map's bottom edge can't fill 85 % of a 40-deep window.
        var (g, s) = Map(r => Fill(r, 0, 299, 80, 99, '?'));
        Assert.Null(CaveRelocate.Choose(g, s, Power, maxScan: 40).Choice);
        var (deep, s2) = Map(r => Fill(r, 0, 299, 60, 99, '?'));
        var c2 = CaveRelocate.Choose(deep, s2, Power, maxScan: 40).Choice;
        Assert.NotNull(c2);
        Assert.Equal(TunnelDir.Down, c2!.Dir);
    }

    [Fact]
    public void TriedDirectionsAndNoPickaxe()
    {
        var (g, s) = Map(r => Fill(r, 0, 90, 0, 99, '?'));
        Assert.Null(CaveRelocate.Choose(g, s, 0).Choice);
        var d = CaveRelocate.Choose(g, s, Power, (dir, _) => dir == TunnelDir.Left, maxScan: 100);
        Assert.Null(d.Choice);
        Assert.Contains(d.Refused, x => x.Dir == TunnelDir.Left && x.Why.Contains("already tried"));
    }

    [Fact]
    public void OnlyFrontiersWellAlongTheWayEndTheTrip()
    {
        var r = new RelocateChoice(TunnelDir.Left, new TilePoint(150, 50), 80, 60, 95, 80);
        Assert.False(CaveRelocate.Beyond(r, new TilePoint(140, 50))); // still the old neighbourhood
        Assert.True(CaveRelocate.Beyond(r, new TilePoint(125, 40)));
        Assert.False(CaveRelocate.Beyond(r, new TilePoint(175, 50))); // behind us
        var down = new RelocateChoice(TunnelDir.Down, new TilePoint(150, 50), 30, 10, 95, 30);
        Assert.True(CaveRelocate.Beyond(down, new TilePoint(150, 61))); // region only 10 tiles below: 10 is enough
    }

    // ---- tunnels that dig nothing don't count ------------------------------------------------------------------

    [Fact]
    public void ATunnelThatDugNothingAndMovedNobodyDoesNotCount()
    {
        var t = new TunnelTally();
        for (int i = 0; i < 5; i++) Assert.False(t.Record(0, 0)); // start unreachable / first dig refused
        Assert.False(t.Record(0, 1)); // a nudge isn't a tunnel
        Assert.Equal(0, t.Counted);
        Assert.Equal(6, t.Wasted);
        Assert.False(t.LimitReached(10));
        Assert.True(t.Record(12, 0));
        Assert.True(t.Record(0, 8)); // walked through open space
        Assert.Equal(2, t.Counted);
        Assert.Equal(12, t.Tiles);
    }

    [Fact]
    public void TunnelLimitsArePerArea()
    {
        var t = new TunnelTally();
        for (int i = 0; i < 3; i++) t.Record(5, 5);
        Assert.True(t.LimitReached(3));
        t.NewArea(); // relocated
        Assert.False(t.LimitReached(3));
        for (int i = 0; i < TunnelTally.MaxWastedPerArea; i++) t.Record(0, 0);
        Assert.True(t.LimitReached(3)); // too many failed starts here: relocate instead
        Assert.Equal(3, t.Counted);
    }

    [Fact]
    public void TunnelFromWhereThePlayerStandsWhenOtherStartsFailed()
    {
        // The exhausted pocket of CaveTunnelTests (air x 10..20, y 10..12, rock 4 deep, then unknown); every start the
        // flood offers has been tried (walks to them failed). The player's own spot needs no walk.
        var rows = new string[50];
        for (int y = 0; y < 50; y++)
        {
            var row = new char[40];
            for (int x = 0; x < 40; x++)
            {
                bool inPocket = x >= 10 && x <= 20 && y >= 10 && y <= 12;
                int dx = Math.Max(0, Math.Max(10 - x, x - 20)), dy = Math.Max(0, Math.Max(10 - y, y - 12));
                row[x] = inPocket ? '.' : Math.Max(dx, dy) <= 4 ? '#' : '?';
            }
            if (y == 12) row[15] = 'S';
            rows[y] = new string(row);
        }
        var (g, m) = ArrayTileGrid.Parse(rows);
        var here = m['S'];
        var d = CaveTunnel.Choose(g, here, here, 60, Power, (dir, s) => s != here);
        Assert.NotNull(d.Choice);
        Assert.Equal(here, d.Choice!.Start);
    }

    // ---- memory across calls -----------------------------------------------------------------------------------

    // A long gallery (y 3), frontiers at both ends: the left end is nearer.
    private static readonly string[] Gallery =
    {
        "##################################",
        "??..............................??",
        "??..............................??",
        "??..........S...................??",
        "##################################",
    };

    [Fact]
    public void FailedWalksAreRememberedAcrossCalls()
    {
        var (g, m) = ArrayTileGrid.Parse(Gallery);
        var s = m['S'];
        var mem = new ExploreMemory(ttlTicks: 1000);
        mem.UseWorld("w");
        var first = CaveFrontier.Pick(g, s, s, 60);
        Assert.NotNull(first);
        Assert.True(first!.Frontier.X < s.X); // the near (left) end

        // Call 1: the walk there fails. Call 2 (same static memory): picks a neighbouring stand on the same ledge…
        mem.WalkFailed(first.Stand, first.Frontier, now: 10);
        var second = CaveFrontier.Pick(g, s, s, 60, st => mem.StandBlocked(st, 20), ignoreFrontier: f => mem.FrontierBlocked(f, 20));
        Assert.NotNull(second);
        Assert.True(CaveFrontier.Chebyshev(second!.Stand, first.Stand) > ExploreMemory.StandRadius);
        // …and once two walks toward that end failed, the whole end is ignored: the far end is next.
        mem.WalkFailed(second.Stand, second.Frontier, now: 30);
        var third = CaveFrontier.Pick(g, s, s, 60, st => mem.StandBlocked(st, 40), ignoreFrontier: f => mem.FrontierBlocked(f, 40));
        Assert.NotNull(third);
        Assert.True(third!.Frontier.X > s.X);

        // Entries expire.
        Assert.False(mem.StandBlocked(first.Stand, 2000));
        Assert.False(mem.FrontierBlocked(first.Frontier, 2000));
    }

    [Fact]
    public void MemoryForgetsOnANewWorldOrAReload()
    {
        var mem = new ExploreMemory();
        mem.UseWorld("a");
        var p = new TilePoint(3944, 1162);
        mem.WalkFailed(p, p, now: 500);
        mem.TunnelFailed(TunnelDir.Left, p, now: 500);
        Assert.True(mem.StandBlocked(new TilePoint(3945, 1163), 600));
        Assert.True(mem.TunnelBlocked(TunnelDir.Left, new TilePoint(3946, 1162), 600));
        Assert.False(mem.TunnelBlocked(TunnelDir.Right, p, 600));
        mem.UseWorld("a"); // same world: kept
        Assert.True(mem.StandBlocked(p, 700));
        Assert.True(mem.StandBlocked(p, 100) == false); // clock went backwards (world reloaded): forgotten
        mem.WalkFailed(p, p, now: 200);
        mem.UseWorld("b");
        Assert.False(mem.StandBlocked(p, 300));
    }
}
