using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class DigPlannerShaftTests
{
    private const int Power = 35, SafeFall = 20;

    // Player feet-left at (2,2): body x 2..3, rows 0..2, standing on row 3.
    private static ArrayTileGrid Ground(int depth = 10, int width = 8)
    {
        var rows = new List<string> { new('.', width), new('.', width), new('.', width) };
        for (int i = 0; i < depth; i++) rows.Add(new string('#', width));
        return ArrayTileGrid.Parse(rows.ToArray()).Grid;
    }

    private static readonly TilePoint Stand = new(2, 2);

    [Fact]
    public void DigsTwoColumnsUnderTheBody_TopRowFirst()
    {
        var plan = DigPlanner.ShaftDown(Ground(), Stand, 4, Power, SafeFall, false);
        Assert.Null(plan.Stop);
        Assert.Equal(Stand, plan.Stand);
        Assert.Equal(new[]
        {
            new TilePoint(2, 3), new TilePoint(3, 3), new TilePoint(2, 4), new TilePoint(3, 4),
            new TilePoint(2, 5), new TilePoint(3, 5), new TilePoint(2, 6), new TilePoint(3, 6),
        }, plan.Tiles);
    }

    [Fact]
    public void StopsAboveLava_NeverDigsTheRowLavaWouldBeUnder()
    {
        var g = Ground();
        g.Set(2, 6, new TileInfo(TileKind.Lava));
        var plan = DigPlanner.ShaftDown(g, Stand, 4, Power, SafeFall, false);
        Assert.Equal(DigStopKind.Lava, plan.Stop!.Kind);
        Assert.Equal(new TilePoint(2, 6), plan.Stop.At);
        Assert.Equal("lava below at (2,6)", plan.Stop.Describe(Power));
        Assert.Equal(4, plan.Tiles.Count); // rows 3 and 4 only: digging row 5 would put the feet on the lava
        Assert.DoesNotContain(new TilePoint(2, 5), plan.Tiles);
    }

    [Fact]
    public void StopsBesideLavaOrWater()
    {
        var g = Ground();
        g.Set(1, 4, new TileInfo(TileKind.Lava));
        var plan = DigPlanner.ShaftDown(g, Stand, 4, Power, SafeFall, false);
        Assert.Equal(DigStopKind.Lava, plan.Stop!.Kind);
        Assert.Equal(2, plan.Tiles.Count);

        g = Ground();
        g.Set(4, 5, new TileInfo(TileKind.Water));
        plan = DigPlanner.ShaftDown(g, Stand, 4, Power, SafeFall, false);
        Assert.Equal(DigStopKind.Liquid, plan.Stop!.Kind);
        Assert.Equal(4, plan.Tiles.Count);
    }

    [Fact]
    public void UnknownBelow_IsDark()
    {
        var g = Ground();
        for (int x = 0; x < 8; x++) g.Set(x, 5, new TileInfo(TileKind.Unknown));
        var plan = DigPlanner.ShaftDown(g, Stand, 4, Power, SafeFall, false);
        Assert.Equal(DigStopKind.Dark, plan.Stop!.Kind);
        Assert.Equal(5, plan.Stop.At.Y);
        Assert.Equal(2, plan.Tiles.Count); // row 3 only: row 4 can't be dug until row 5 is seen

        var none = DigPlanner.ShaftDown(g, new TilePoint(2, 3), 4, Power, SafeFall, false);
        Assert.False(none.HasWork);
    }

    [Fact]
    public void TooHard_ReportsPickPowerNeeded()
    {
        var g = Ground();
        g.Set(3, 4, new TileInfo(TileKind.Solid, 65));
        var plan = DigPlanner.ShaftDown(g, Stand, 4, Power, SafeFall, false);
        Assert.Equal(DigStopKind.TooHard, plan.Stop!.Kind);
        Assert.Equal(65, plan.Stop.Need);
        Assert.Equal("needs pick power 65 at (3,4) (best pickaxe has 35)", plan.Stop.Describe(Power));
        Assert.Equal(2, plan.Tiles.Count);
    }

    [Fact]
    public void BreaksIntoCave_StopsAfterTheOpeningRow()
    {
        var g = Ground(12);
        for (int y = 5; y <= 8; y++)
            for (int x = 0; x < 8; x++) g.Set(x, y, TileInfo.Air);
        var plan = DigPlanner.ShaftDown(g, Stand, 4, Power, SafeFall, false);
        Assert.Null(plan.Stop);
        Assert.Equal(4, plan.Tiles.Count); // rows 3, 4; then the player drops 6 tiles to the cave floor
    }

    [Fact]
    public void BreakingIntoADeepCave_IsAnUnsafeDrop()
    {
        var g = Ground(12);
        for (int y = 5; y <= 11; y++)
            for (int x = 0; x < 8; x++) g.Set(x, y, TileInfo.Air);
        var plan = DigPlanner.ShaftDown(g, Stand, 4, Power, safeFall: 4, noFallDamage: false);
        Assert.Equal(DigStopKind.Drop, plan.Stop!.Kind);
        Assert.Equal(2, plan.Tiles.Count);

        var immune = DigPlanner.ShaftDown(g, Stand, 4, Power, safeFall: 4, noFallDamage: true);
        Assert.Null(immune.Stop);
        Assert.Equal(4, immune.Tiles.Count);
    }

    [Fact]
    public void ProtectedUnderBody_ShiftsToANeighbouringColumn()
    {
        var g = Ground();
        g.Set(2, 3, new TileInfo(TileKind.Solid, Protected: true));
        var only = DigPlanner.ShaftDown(g, Stand, 4, Power, SafeFall, false);
        Assert.Equal(DigStopKind.Protected, only.Stop!.Kind);
        Assert.False(only.HasWork);

        var plan = DigPlanner.ShaftDownNear(g, Stand, 4, Power, SafeFall, false);
        Assert.True(plan.HasWork);
        Assert.Equal(new TilePoint(3, 2), plan.Stand);
        Assert.DoesNotContain(new TilePoint(2, 3), plan.Tiles);
    }

    [Fact]
    public void Near_InsideAShaft_DoesNotShift()
    {
        var g = Ground();
        for (int y = 3; y <= 5; y++) { g.Set(2, y, TileInfo.Air); g.Set(3, y, TileInfo.Air); }
        g.Set(2, 6, new TileInfo(TileKind.Solid, Protected: true));
        var plan = DigPlanner.ShaftDownNear(g, new TilePoint(2, 5), 4, Power, SafeFall, false);
        Assert.False(plan.HasWork);
        Assert.Equal(DigStopKind.Protected, plan.Stop!.Kind);
        Assert.Equal("protected tile at (2,6)", plan.Stop.Describe(Power));
    }

    [Fact]
    public void Spikes_AreAHazardStop()
    {
        var g = Ground();
        g.Set(2, 5, new TileInfo(TileKind.Hazard));
        var plan = DigPlanner.ShaftDown(g, Stand, 4, Power, SafeFall, false);
        Assert.Equal(DigStopKind.Hazard, plan.Stop!.Kind);
        Assert.Equal(2, plan.Tiles.Count);
    }
}

public class DigPlannerTunnelTests
{
    private const int Power = 35;

    // Player feet-left at (1,3) in a pocket: body x 1..2, rows 1..3, solid rock to the right.
    private static ArrayTileGrid Pocket() => ArrayTileGrid.Parse(
        "############",
        "#..#########",
        "#..#########",
        "#..#########",
        "############",
        "############").Grid;

    private static readonly TilePoint Stand = new(1, 3);

    [Fact]
    public void DigsThreeTallColumnsAhead_ColumnByColumnTopDown()
    {
        var plan = DigPlanner.Tunnel(Pocket(), Stand, +1, maxColumns: 2, scanColumns: 8, Power);
        Assert.Null(plan.Stop);
        Assert.Equal(new[]
        {
            new TilePoint(3, 1), new TilePoint(3, 2), new TilePoint(3, 3),
            new TilePoint(4, 1), new TilePoint(4, 2), new TilePoint(4, 3),
        }, plan.Tiles);
    }

    [Fact]
    public void OpenColumns_AreWalkedThroughNotCounted()
    {
        var g = Pocket();
        for (int y = 1; y <= 3; y++) g.Set(3, y, TileInfo.Air);
        var plan = DigPlanner.Tunnel(g, Stand, +1, maxColumns: 1, scanColumns: 8, Power);
        Assert.Equal(new[] { new TilePoint(4, 1), new TilePoint(4, 2), new TilePoint(4, 3) }, plan.Tiles);
    }

    [Fact]
    public void WaterAhead_StopsBeforeTheColumnNextToIt()
    {
        var g = Pocket();
        g.Set(6, 2, new TileInfo(TileKind.Water));
        var plan = DigPlanner.Tunnel(g, Stand, +1, 4, 8, Power);
        Assert.Equal(DigStopKind.Liquid, plan.Stop!.Kind);
        Assert.Equal("liquid ahead at (6,2) would flood the hole", plan.Stop.Describe(Power));
        Assert.Equal(6, plan.Tiles.Count); // columns 3 and 4
    }

    [Fact]
    public void LavaAboveTheTunnel_Stops()
    {
        var g = Pocket();
        g.Set(4, 0, new TileInfo(TileKind.Lava));
        var plan = DigPlanner.Tunnel(g, Stand, +1, 4, 8, Power);
        Assert.Equal(DigStopKind.Lava, plan.Stop!.Kind);
        Assert.Equal(3, plan.Tiles.Count);
    }

    [Fact]
    public void UnknownAhead_IsDark()
    {
        var g = Pocket();
        for (int y = 0; y < 6; y++) g.Set(5, y, new TileInfo(TileKind.Unknown));
        var plan = DigPlanner.Tunnel(g, Stand, +1, 4, 8, Power);
        Assert.Equal(DigStopKind.Dark, plan.Stop!.Kind);
        Assert.Equal(5, plan.Stop.At.X);
        Assert.Equal(3, plan.Tiles.Count); // column 3; column 4 needs column 5 seen
    }

    [Fact]
    public void MissingFloor_Stops()
    {
        var g = Pocket();
        g.Set(4, 4, TileInfo.Air);
        var plan = DigPlanner.Tunnel(g, Stand, +1, 4, 8, Power);
        Assert.Equal(DigStopKind.NoFloor, plan.Stop!.Kind);
        Assert.Equal(new TilePoint(4, 4), plan.Stop.At);
        Assert.Equal(3, plan.Tiles.Count);
    }

    [Fact]
    public void Left_DigsTheColumnsLeftOfTheBody()
    {
        var g = ArrayTileGrid.Parse(
            "##########",
            "#######..#",
            "#######..#",
            "#######..#",
            "##########").Grid;
        var plan = DigPlanner.Tunnel(g, new TilePoint(7, 3), -1, 2, 8, Power);
        Assert.Equal(new TilePoint(6, 1), plan.Tiles[0]);
        Assert.Equal(new TilePoint(5, 3), plan.Tiles[^1]);
    }

    [Fact]
    public void ProtectedOrTooHard_Stops()
    {
        var g = Pocket();
        g.Set(4, 2, new TileInfo(TileKind.Solid, Protected: true));
        Assert.Equal(DigStopKind.Protected, DigPlanner.Tunnel(g, Stand, +1, 4, 8, Power).Stop!.Kind);

        g = Pocket();
        g.Set(3, 1, new TileInfo(TileKind.Solid, 100));
        var plan = DigPlanner.Tunnel(g, Stand, +1, 4, 8, Power);
        Assert.Equal(DigStopKind.TooHard, plan.Stop!.Kind);
        Assert.False(plan.HasWork);
    }
}

public class TorchPlannerTests
{
    private static readonly ReachSpec Vanilla = new(5, 4, 0);

    [Theory]
    [InlineData(0.05f, null, true)]
    [InlineData(0.05f, 5, false)]
    [InlineData(0.05f, 11, true)]
    [InlineData(0.6f, null, false)]
    public void ShouldPlace_WhenDarkAndNoTorchNearby(float brightness, int? nearest, bool expected) =>
        Assert.Equal(expected, TorchPlanner.ShouldPlace(brightness, nearest));

    private static (ArrayTileGrid G, Func<TilePoint, TorchCell> Cell) Tunnel()
    {
        var g = ArrayTileGrid.Parse(
            "############",
            "#..........#",
            "#..........#",
            "#..........#",
            "############").Grid;
        return (g, t => new TorchCell(g.Get(t).Kind == TileKind.Air, false));
    }

    [Fact]
    public void PicksAnAnchoredCellNearTheHead()
    {
        var (g, cell) = Tunnel();
        // Stand (5,3): head row 1, aim (5,2). Mid-tunnel cells at row 2 have no anchor; row 1 has the ceiling above
        // (not an anchor), row 3 has the floor below (anchor).
        var spot = TorchPlanner.BestSpot(g, cell, new TilePoint(5, 3), Vanilla);
        Assert.Equal(new TilePoint(5, 3), spot);
    }

    [Fact]
    public void BlocksAboutToBeDug_AreNoAnchor()
    {
        var (g, cell) = Tunnel();
        var floor = new HashSet<TilePoint>();
        for (int x = 0; x < 12; x++) floor.Add(new TilePoint(x, 4));
        var spot = TorchPlanner.BestSpot(g, cell, new TilePoint(2, 3), Vanilla, pending: floor);
        Assert.NotNull(spot);
        Assert.Equal(1, spot!.Value.X); // only the left wall (x 0) anchors now
    }

    [Fact]
    public void WallBehind_Anchors()
    {
        var g = ArrayTileGrid.Parse(
            "..........",
            "..........",
            "..........",
            "..........",
            "##########").Grid;
        var wall = new TilePoint(4, 1);
        var floor = new HashSet<TilePoint>();
        for (int x = 0; x < 10; x++) floor.Add(new TilePoint(x, 4));
        var spot = TorchPlanner.BestSpot(g, t => new TorchCell(true, t == wall), new TilePoint(4, 3), Vanilla, pending: floor);
        Assert.Equal(wall, spot);
    }

    [Fact]
    public void Focus_PullsTowardTheDarkTile_KeepClearCostsExtra()
    {
        var (g, cell) = Tunnel();
        var right = TorchPlanner.BestSpot(g, cell, new TilePoint(5, 3), Vanilla, focus: new TilePoint(11, 2));
        Assert.Equal(new TilePoint(9, 3), right); // the reachable anchored cell nearest the focus (floor-anchored)

        var keep = new HashSet<TilePoint> { new(5, 3), new(6, 3) };
        var spot = TorchPlanner.BestSpot(g, cell, new TilePoint(5, 3), Vanilla, keepClear: keep);
        Assert.NotNull(spot);
        Assert.DoesNotContain(spot!.Value, keep);
    }

    [Fact]
    public void NothingFree_IsNull()
    {
        var (g, _) = Tunnel();
        Assert.Null(TorchPlanner.BestSpot(g, _ => new TorchCell(false, true), new TilePoint(5, 3), Vanilla));
    }
}

/// <summary>
/// Discrete simulation of explore's dig loop: plan a chunk, break its tiles in order, let the player drop after each
/// tile (gravity), walk forward through open tunnel, repeat. Checks the player is never hurt (falls within the safe
/// distance, never ends up in or beside lava) and that the loop ends for the reported reason.
/// </summary>
public class DigLoopSimTests
{
    private const int Power = 35;

    private static int Drop(ArrayTileGrid g, ref TilePoint p)
    {
        int fell = 0;
        while (!g.Get(p.X, p.Y + 1).IsStandable && !g.Get(p.X + 1, p.Y + 1).IsStandable)
        {
            p = new TilePoint(p.X, p.Y + 1);
            fell++;
            Assert.True(fell < 100, "fell out of the world");
        }
        return fell;
    }

    private static (TilePoint Feet, DigStop? Stop, int Dug) DigDown(ArrayTileGrid g, TilePoint p, int distance, int safeFall)
    {
        var origin = p;
        int dug = 0;
        for (int guard = 0; guard < 100 && p.Y - origin.Y < distance; guard++)
        {
            var plan = DigPlanner.ShaftDownNear(g, p, Math.Min(DigPlanner.ShaftChunk, distance - (p.Y - origin.Y)), Power, safeFall, false);
            if (!plan.HasWork) return (p, plan.Stop, dug);
            p = plan.Stand;
            foreach (var t in plan.Tiles)
            {
                Assert.True(g.Get(t).IsStandable, $"{t} planned but not solid");
                g.Set(t.X, t.Y, TileInfo.Air);
                dug++;
                Assert.True(Drop(g, ref p) <= safeFall, "unsafe fall");
                for (int y = p.Y - 2; y <= p.Y + 1; y++)
                    for (int x = p.X - 1; x <= p.X + 2; x++)
                        Assert.NotEqual(TileKind.Lava, g.Get(x, y).Kind);
            }
        }
        return (p, null, dug);
    }

    [Fact]
    public void Down_ReachesTheDistance()
    {
        var rows = new List<string> { "........", "........", "........" };
        for (int i = 0; i < 40; i++) rows.Add("########");
        var g = ArrayTileGrid.Parse(rows.ToArray()).Grid;
        var (feet, stop, dug) = DigDown(g, new TilePoint(2, 2), 25, 20);
        Assert.Null(stop);
        Assert.Equal(27, feet.Y);
        Assert.Equal(50, dug);
    }

    [Fact]
    public void Down_StopsSafelyAboveALavaPocket()
    {
        var rows = new List<string> { "........", "........", "........" };
        for (int i = 0; i < 40; i++) rows.Add(i == 15 ? "##LLL###" : "########");
        var g = ArrayTileGrid.Parse(rows.ToArray()).Grid;
        var (feet, stop, _) = DigDown(g, new TilePoint(2, 2), 25, 20);
        Assert.Equal(DigStopKind.Lava, stop!.Kind);
        Assert.True(feet.Y < 18 - 1, $"feet at {feet.Y}, lava at row 18");
    }

    [Fact]
    public void Down_IntoACave_LandsOnItsFloor()
    {
        var rows = new List<string> { "........", "........", "........" };
        for (int i = 0; i < 40; i++) rows.Add(i is >= 8 and <= 12 ? "........" : "########");
        var g = ArrayTileGrid.Parse(rows.ToArray()).Grid;
        var (feet, stop, _) = DigDown(g, new TilePoint(2, 2), 25, 20);
        Assert.Null(stop);
        Assert.Equal(27, feet.Y);
    }

    [Fact]
    public void Right_TunnelsThroughAHill()
    {
        var g = ArrayTileGrid.Parse(
            "..............................",
            "....##########################",
            "....##########################",
            "....##########################",
            "##############################").Grid;
        var p = new TilePoint(0, 3);
        int dug = 0;
        for (int guard = 0; guard < 50 && p.X < 20; guard++)
        {
            var plan = DigPlanner.Tunnel(g, p, +1, DigPlanner.TunnelChunk, Math.Min(12, 20 - p.X), Power);
            foreach (var t in plan.Tiles) { g.Set(t.X, t.Y, TileInfo.Air); dug++; }
            int before = p.X;
            while (p.X < 20 && Pathfinder.CanStand(g, new TilePoint(p.X + 1, p.Y))) p = new TilePoint(p.X + 1, p.Y);
            Assert.True(p.X > before || plan.HasWork, $"stuck at {p} ({plan.Stop?.Kind})");
        }
        Assert.Equal(20, p.X);
        Assert.Equal(3 * (21 - 4 + 1), dug); // columns 4..21 (the body's right column reaches x 21)
    }
}
