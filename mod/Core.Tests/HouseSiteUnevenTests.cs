using LlmPlayer.Core.Build;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Tests;

/// <summary>build_house on ordinary, uneven ground: floor gaps filled, small bumps mined, shaped blocks flattened.</summary>
public class HouseSiteUnevenTests
{
    private static readonly SiteCell Ground = new(SiteTile.Solid, Natural: true);
    private static readonly SiteCell Air = new(SiteTile.Air);

    /// <summary>Natural ground from row <paramref name="groundY"/> down; overrides in <paramref name="extra"/>.</summary>
    private static Func<int, int, SiteCell> Terrain(int groundY, Dictionary<TilePoint, SiteCell>? extra = null) => (x, y) =>
        extra != null && extra.TryGetValue(new TilePoint(x, y), out var c) ? c : y >= groundY ? Ground : Air;

    [Fact]
    public void Exact_InteriorBumpsUpToTwoHigh_AreMinedFirst_TopDown()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var extra = new Dictionary<TilePoint, SiteCell>
        {
            [new(14, 19)] = Ground, [new(14, 18)] = Ground, // a 2-high bump inside
            [new(10, 19)] = Ground,                         // a bump in the door spot (bottom door tile)
        };
        var c = HouseSite.Check(l, Terrain(20, extra), strict: false);
        Assert.True(c.Ok, c.Problem);
        Assert.Equal(new[] { new TilePoint(14, 18), new TilePoint(10, 19), new TilePoint(14, 19) }, c.MineFirst());
        Assert.Contains(new TilePoint(14, 19), c.WallsToPlace); // walls go behind where the bump was
        Assert.Equal(6, c.Work);
    }

    [Fact]
    public void Exact_TallOrNonNaturalBlocksInside_StayProblems()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var tall = HouseSite.Check(l, Terrain(20, new() { [new(14, 17)] = Ground }), strict: false);
        Assert.Contains("more than 2 tiles above the floor", tall.Problem);
        var placed = HouseSite.Check(l, Terrain(20, new() { [new(14, 19)] = new SiteCell(SiteTile.Solid) }), strict: false);
        Assert.Contains("not natural ground", placed.Problem);
        var door = HouseSite.Check(l, Terrain(20, new() { [new(10, 17)] = Ground }), strict: false); // top door tile
        Assert.Contains("door spot", door.Problem);
    }

    [Fact]
    public void Strict_FloorOverADip_IsFilled_ButNotOverADrop()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        // Dip: floor tiles 13..15 are air with ground two rows lower.
        var dip = new Dictionary<TilePoint, SiteCell>();
        for (int x = 13; x <= 15; x++) { dip[new(x, 20)] = Air; dip[new(x, 21)] = Air; }
        var c = HouseSite.Check(l, Terrain(20, dip), strict: true);
        Assert.True(c.Ok, c.Problem);
        Assert.Contains(new TilePoint(14, 20), c.FrameToPlace);
        Assert.Equal(3, c.Work);

        // Drop: the same gap with nothing within 4 tiles below.
        var drop = new Dictionary<TilePoint, SiteCell>(dip);
        for (int y = 22; y <= 24; y++) drop[new(14, y)] = Air;
        Assert.Contains("over a drop", HouseSite.Check(l, Terrain(20, drop), strict: true).Problem);
    }

    [Fact]
    public void Strict_NeedsAThirdOfTheFloorOnGround()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var extra = new Dictionary<TilePoint, SiteCell>();
        for (int x = 10; x <= 17; x++) extra[new(x, 20)] = Air; // only 2 of 10 floor tiles on ground (x 18, 19)
        var c = HouseSite.Check(l, Terrain(20, extra), strict: true);
        Assert.Contains("rest on ground", c.Problem);
        Assert.Equal(4, HouseSite.MinGround(10));
    }

    [Fact]
    public void Strict_NaturalBumpInBottomOfSideWall_IsKeptAsFrame()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var c = HouseSite.Check(l, Terrain(20, new() { [new(19, 19)] = Ground }), strict: true);
        Assert.True(c.Ok, c.Problem);
        Assert.DoesNotContain(new TilePoint(19, 19), c.FrameToPlace);
        Assert.Empty(c.Bumps!);
    }

    [Fact]
    public void Find_HillySurface_FindsASite_WithFloorFillAndBumps()
    {
        // Rolling ground everywhere around the player: a bump every 6 columns, a 1-deep dip every 7 columns: no 10
        // tiles of level ground anywhere (the old search found nothing).
        var extra = new Dictionary<TilePoint, SiteCell>();
        for (int x = -40; x <= 40; x++)
        {
            if (Math.Abs(x) % 6 == 2) extra[new(x, 19)] = Ground;
            if (Math.Abs(x) % 7 == 4) extra[new(x, 20)] = Air;
        }
        var cell = Terrain(20, extra);
        var found = HouseSite.FindWithCheck(new TilePoint(0, 19), 10, 6, cell, radius: 20);
        Assert.NotNull(found);
        var (l, c) = found!.Value;
        Assert.True(c.Ok);
        Assert.True(c.Work > 0);
        Assert.True(HouseSite.Check(l, cell, strict: true).Ok);
        Assert.Equal(l, HouseSite.Find(new TilePoint(0, 19), 10, 6, cell, radius: 20));
    }

    [Fact]
    public void Find_PrefersLessWork_OverASlightlyNearerBumpySpot()
    {
        // Single-tile bumps on every other column left of x=3 (no level ground on either row), flat to the right;
        // the player stands at the border.
        var extra = new Dictionary<TilePoint, SiteCell>();
        for (int x = -40; x < 3; x += 2) extra[new(x, 19)] = Ground;
        var found = HouseSite.FindWithCheck(new TilePoint(2, 19), 10, 6, Terrain(20, extra), radius: 20);
        Assert.NotNull(found);
        Assert.Equal(0, found!.Value.Check.Work);
        Assert.True(found.Value.Layout.X >= 3);
    }

    [Fact]
    public void ShapedFrameBlocks_AreFlattened_BeforeTheDoorAndFurniture()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var half = Ground with { Shaped = true };
        var c = HouseSite.Check(l, Terrain(20, new() { [new(16, 20)] = half, [new(12, 20)] = half }), strict: true);
        Assert.True(c.Ok, c.Problem);
        Assert.Equal(new HashSet<TilePoint> { new(16, 20), new(12, 20) }, c.Flatten);

        var steps = HousePlan.Steps(l, c.FrameToPlace, c.WallsToPlace, p => p.Y >= 20, _ => false, out var problem, c.Flatten);
        Assert.Null(problem);
        var parts = steps.Select(s => s.Part).ToList();
        int firstFlatten = parts.IndexOf(HousePart.Flatten);
        Assert.Equal(2, parts.Count(p => p == HousePart.Flatten));
        Assert.True(parts.LastIndexOf(HousePart.Frame) < firstFlatten);
        Assert.True(parts.LastIndexOf(HousePart.Flatten) < parts.IndexOf(HousePart.Door));
        Assert.True(parts.LastIndexOf(HousePart.Flatten) < parts.IndexOf(HousePart.Table));
    }

    [Fact]
    public void ShapedAirOrPlatforms_AreNotFlattened()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var c = HouseSite.Check(l, Terrain(20, new() { [new(16, 20)] = new SiteCell(SiteTile.Platform, Shaped: true) }), strict: false);
        Assert.True(c.Ok, c.Problem);
        Assert.Empty(c.Flatten!);
    }
}
