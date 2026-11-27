using LlmPlayer.Core.Build;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Tests;

/// <summary>build_house next to the player's own platforms, the auto-site rejection report, the door anchors and
/// where a work bench goes (seen in play at x 4350-4377, y 547).</summary>
public class HousePlatformTests
{
    private static readonly SiteCell Ground = new(SiteTile.Solid, Natural: true);
    private static readonly SiteCell Air = new(SiteTile.Air);
    private static readonly SiteCell Plat = new(SiteTile.Platform, Removable: true);
    private static readonly SiteCell LockedPlat = new(SiteTile.Platform);
    private static readonly SiteCell Obj = new(SiteTile.Object);

    /// <summary>Flat grass from row 548 down (floor row 547 is the grass), a platform row at 543 over x 4330..4400,
    /// a campfire and a work bench on the grass west of the area.</summary>
    private static Func<int, int, SiteCell> LiveArea(SiteCell platform) => (x, y) =>
    {
        if (y >= 547) return Ground;
        if (y == 543 && x >= 4330 && x <= 4400) return platform;
        if (y == 546 && (x == 4340 || x == 4341 || x == 4344 || x == 4345)) return Obj; // campfire, work bench
        return Air; // tall grass is cuttable: Housing.Cell reads it as air
    };

    [Fact]
    public void AutoSite_AcceptsFlatGrassUnderAPlatformRow_ReplacingFramePlatforms_MiningInnerOnes()
    {
        var cell = LiveArea(Plat);
        var found = HouseSite.FindWithCheck(new TilePoint(4360, 546), 10, 6, cell, radius: 30);
        Assert.NotNull(found);
        var (l, c) = found!.Value;
        Assert.Equal(547, l.FloorY);
        // Ceiling 542, interior rows 543..546: the side walls at 543 are replaced, the 8 inner platforms mined.
        Assert.Equal(new HashSet<TilePoint> { new(l.Left, 543), new(l.Right, 543) }, c.Replace);
        Assert.Equal(8, c.Platforms!.Count);
        Assert.Subset(c.FrameToPlace, c.Replace!);
        Assert.Equal(10, c.PickaxeTiles);
        Assert.Contains(new TilePoint(l.Left, 543), c.MineFirst());
        Assert.Contains(new TilePoint(l.Left + 3, 543), c.WallsToPlace); // walls go behind the mined platforms
    }

    [Fact]
    public void AutoSite_ProtectedPlatforms_FailWithCountedReasons()
    {
        var report = new SiteSearchReport();
        var found = HouseSite.FindWithCheck(new TilePoint(4360, 546), 10, 6, LiveArea(LockedPlat), radius: 10, report);
        Assert.Null(found);
        Assert.True(report.Checked > 0);
        string top = report.Top(3);
        Assert.Contains("not empty (a protected platform)", top);
        Assert.DoesNotContain("drop", top); // floating spots are skipped by the quick check
        Assert.Contains("e.g. ", top);
        Assert.True(report.Reasons.Count >= 1);
    }

    [Fact]
    public void ExplicitHeight5_PlatformCeiling_IsReplaced_SoTheDoorHasASolidBlockAbove()
    {
        // Seen in play: the door under an existing Wood Platform was refused 3 times.
        var l = new HouseLayout(4366, 547, 12, 5, DoorSide.Right);
        var cell = LiveArea(Plat);
        var c = HouseSite.Check(l, cell, strict: false);
        Assert.True(c.Ok, c.Problem);
        Assert.Equal(12, c.Replace!.Count);
        var above = HousePlan.DoorAnchors(l)[0];
        Assert.Equal(new TilePoint(l.Right, 543), above);
        Assert.Contains(above, c.Replace);

        var mine = new HashSet<TilePoint>(c.MineFirst());
        var steps = HousePlan.Steps(l, c.FrameToPlace, c.WallsToPlace,
            t => cell(t.X, t.Y).IsBlock && !mine.Contains(t), t => cell(t.X, t.Y).Tile != SiteTile.Air && !mine.Contains(t), out var problem, c.Flatten);
        Assert.Null(problem);
        int door = steps.FindIndex(s => s.Part == HousePart.Door);
        int anchor = steps.FindIndex(s => s.Part == HousePart.Frame && s.Target == above);
        Assert.InRange(anchor, 0, door - 1);
        Assert.Equal(c.FrameToPlace.Count, steps.Take(door).Count(s => s.Part == HousePart.Frame));
    }

    [Fact]
    public void ProtectedPlatformInTheFrame_IsKept_NotReplaced_InExactMode()
    {
        var l = new HouseLayout(4366, 547, 12, 5, DoorSide.Right);
        var c = HouseSite.Check(l, LiveArea(LockedPlat), strict: false);
        Assert.True(c.Ok, c.Problem);
        Assert.Empty(c.Replace!);
    }

    [Fact]
    public void Steps_ReportAMissingDoorAnchor()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var frame = new HashSet<TilePoint>(l.FrameTiles());
        frame.Remove(new TilePoint(10, 16)); // above the doorway: not placed and nothing there
        HousePlan.Steps(l, frame, new HashSet<TilePoint>(), t => t.Y >= 21, _ => false, out var problem);
        Assert.Contains("hold the door", problem);
    }

    [Fact]
    public void Steps_BenchComesFirst()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var steps = HousePlan.Steps(l, new HashSet<TilePoint>(l.FrameTiles()), new HashSet<TilePoint>(), t => t.Y >= 21, _ => false,
            out var problem, bench: new TilePoint(22, 19));
        Assert.Null(problem);
        Assert.Equal(new HouseStep(HousePart.Bench, new TilePoint(22, 19)), steps[0]);
        Assert.Single(steps, s => s.Part == HousePart.Bench);
    }

    [Fact]
    public void BenchSpot_FarSideFirst_NeverInFrontOfTheDoor()
    {
        Func<int, int, SiteCell> flat = (x, y) => y >= 21 ? Ground : Air;
        var left = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        Assert.Equal(new TilePoint(21, 20), HouseSite.BenchSpot(left, flat)); // a free column past the far wall (x 19)

        // Far side is a cliff: falls back to the door side, beyond the exit spot and the approach columns.
        Func<int, int, SiteCell> cliff = (x, y) => x >= 20 ? Air : y >= 21 ? Ground : Air;
        var spot = HouseSite.BenchSpot(left, cliff);
        Assert.NotNull(spot);
        Assert.True(spot!.Value.X + 1 < left.Left - 2 - HouseSite.DoorApproach, $"{spot}");

        Func<int, int, SiteCell> nothing = (_, _) => Air;
        Assert.Null(HouseSite.BenchSpot(left, nothing));
        Func<int, int, SiteCell> unknownAround = (x, y) => x is >= 10 and <= 19 ? flat(x, y) : new SiteCell(SiteTile.Unknown);
        Assert.Null(HouseSite.BenchSpot(left, unknownAround));
    }
}
