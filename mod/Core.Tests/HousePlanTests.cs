using LlmPlayer.Core.Build;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Tests;

public class HousePlanTests
{
    private static readonly ReachSpec Vanilla = new(5, 4, 0);

    /// <summary>Flat ground: row <paramref name="groundY"/> and below solid, everything else air; extra cells override.</summary>
    private static Func<int, int, SiteCell> Flat(int groundY, Dictionary<TilePoint, SiteCell>? extra = null) => (x, y) =>
        extra != null && extra.TryGetValue(new TilePoint(x, y), out var c) ? c
        : y >= groundY ? new SiteCell(SiteTile.Solid) : new SiteCell(SiteTile.Air);

    [Theory]
    [InlineData(10, 6, null)]
    [InlineData(8, 5, "at least 60")]
    [InlineData(7, 9, "width")]
    [InlineData(12, 8, "height")]
    [InlineData(12, 5, null)]
    public void SizeProblem_EnforcesVanillaLimits(int w, int h, string? problem)
    {
        var p = HouseLayout.SizeProblem(w, h);
        if (problem == null) Assert.Null(p);
        else Assert.Contains(problem, p);
    }

    [Fact]
    public void Default_10x6_IsExactlyVanillasMinimum() =>
        Assert.Equal(HouseLayout.MinTiles, HouseLayout.DefaultWidth * HouseLayout.DefaultHeight);

    [Fact]
    public void Layout_FrameDoorInteriorPartitionTheRectangle()
    {
        var l = new HouseLayout(100, 50, 10, 6, DoorSide.Left);
        var frame = l.FrameTiles().ToList();
        var interior = l.InteriorTiles().ToList();
        Assert.Equal(10 + 10 + 4 + 4 - 3, frame.Count); // floor, ceiling, two sides, minus the door
        Assert.Equal(frame.Count, frame.Distinct().Count());
        Assert.Equal(8 * 4, interior.Count);
        Assert.Equal(60, frame.Count + interior.Count + l.DoorTiles.Count);
        Assert.All(l.DoorTiles, t => Assert.Equal(100, t.X));
        Assert.Equal(new[] { 47, 48, 49 }, l.DoorTiles.Select(t => t.Y));
        Assert.Equal(new TilePoint(100, 48), l.DoorAim);
        Assert.Equal(45, l.CeilingY);
    }

    [Theory]
    [InlineData(DoorSide.Left)]
    [InlineData(DoorSide.Right)]
    public void Furniture_SitsOnTheFloorAwayFromTheDoor_WithoutOverlap(DoorSide door)
    {
        var l = new HouseLayout(0, 20, 8, 5, door); // smallest width: furniture must still fit
        Assert.True(l.IsInterior(l.ChairAim));
        Assert.Equal(l.InnerBottom, l.ChairAim.Y);
        Assert.Equal(l.InnerBottom, l.TableAim.Y);
        var table = l.TableFootprint().ToList();
        Assert.All(table, t => Assert.True(l.IsInterior(t)));
        Assert.DoesNotContain(l.ChairAim, table);
        Assert.DoesNotContain(new TilePoint(l.ChairAim.X, l.ChairAim.Y - 1), table);
        // A 2-wide work bench aimed at TableAim covers TableAim and the tile right of it: still inside, not the chair.
        var bench = new[] { l.TableAim, new TilePoint(l.TableAim.X + 1, l.TableAim.Y) };
        Assert.All(bench, t => Assert.True(l.IsInterior(t)));
        Assert.DoesNotContain(l.ChairAim, bench);
        // The torch is in the top row, above the chair (2 tall).
        Assert.Equal(l.InnerTop, l.TorchTile.Y);
        Assert.True(l.TorchTile.Y < l.ChairAim.Y - 1);
        // Two free columns next to the door for the player and the NPC's home spot.
        int nearDoor = door == DoorSide.Left ? l.InnerLeft : l.InnerRight;
        int next = door == DoorSide.Left ? nearDoor + 1 : nearDoor - 1;
        Assert.DoesNotContain(table, t => t.X == nearDoor || t.X == next);
        Assert.True(l.IsInterior(l.CheckTile));
        Assert.DoesNotContain(l.CheckTile, table);
    }

    [Fact]
    public void FrameOrder_OnGround_FloorFarWallCeilingThenDoorSideHangingFromCeiling()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var toPlace = new HashSet<TilePoint>(l.FrameTiles());
        var order = HousePlan.FrameOrder(l, toPlace, p => p.Y > 20, out var loose);
        Assert.Empty(loose);
        Assert.Equal(toPlace.Count, order.Count);
        Assert.Equal(new TilePoint(19, 20), order[0]); // floor from the far (right) side
        int firstCeiling = order.FindIndex(t => t.Y == l.CeilingY);
        int firstFarWall = order.FindIndex(t => t.X == 19 && t.Y < 20 && t.Y > l.CeilingY);
        int doorSideWall = order.FindIndex(t => t.X == 10 && t.Y == 16);
        Assert.True(order.FindLastIndex(t => t.Y == 20) < firstFarWall);
        Assert.True(firstFarWall < firstCeiling);
        Assert.True(firstCeiling < doorSideWall);
        // Every tile touches the ground or an earlier tile.
        var placed = new HashSet<TilePoint>();
        foreach (var t in order)
        {
            bool anchored = t.Y + 1 > 20 || new[] { new TilePoint(t.X - 1, t.Y), new TilePoint(t.X + 1, t.Y), new TilePoint(t.X, t.Y - 1), new TilePoint(t.X, t.Y + 1) }
                .Any(placed.Contains);
            Assert.True(anchored, $"{t} has nothing to attach to");
            placed.Add(t);
        }
    }

    [Fact]
    public void FrameOrder_FloatingTilesAreReportedUnanchored()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var order = HousePlan.FrameOrder(l, new HashSet<TilePoint>(l.FrameTiles()), _ => false, out var loose);
        Assert.Empty(order);
        Assert.Equal(l.FrameTiles().Count(), loose.Count);
    }

    [Fact]
    public void Steps_FrameThenDoorThenWallsThenFurniture()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Right);
        var frame = new HashSet<TilePoint>(l.FrameTiles().Where(t => t.Y != 20)); // floor is ground already
        var walls = new HashSet<TilePoint>(l.InteriorTiles());
        var steps = HousePlan.Steps(l, frame, walls, p => p.Y >= 20, _ => false, out var problem);
        Assert.Null(problem);
        var parts = steps.Select(s => s.Part).ToList();
        Assert.Equal(frame.Count, parts.Count(p => p == HousePart.Frame));
        Assert.Equal(walls.Count, parts.Count(p => p == HousePart.BackWall));
        int door = parts.IndexOf(HousePart.Door);
        Assert.True(parts.LastIndexOf(HousePart.Frame) < door);
        Assert.True(door < parts.IndexOf(HousePart.BackWall));
        Assert.Equal(new[] { HousePart.Table, HousePart.Chair, HousePart.Torch }, parts.TakeLast(3));
        Assert.Equal(l.DoorAim, steps[door].Target);
        // Walls go bottom row first.
        var wallYs = steps.Where(s => s.Part == HousePart.BackWall).Select(s => s.Target.Y).ToList();
        Assert.Equal(l.InnerBottom, wallYs[0]);
        Assert.Equal(l.InnerTop, wallYs[^1]);
    }

    [Fact]
    public void InsideStand_ReachesTheCeilingFromTheFloor_AndAvoidsCoveringASolidTarget()
    {
        var l = new HouseLayout(10, 20, 10, 7, DoorSide.Left); // tallest allowed
        var corner = new TilePoint(19, l.CeilingY);
        var s = HousePlan.InsideStand(l, new TilePoint(12, 19), new[] { corner }, Vanilla, solid: true, _ => true, margin: 0f);
        Assert.NotNull(s);
        Assert.True(Reach.FromStand(s!.Value, corner, Vanilla));

        var wallTile = new TilePoint(19, 19); // far wall, bottom: the stand must not cover it
        var s2 = HousePlan.InsideStand(l, new TilePoint(12, 19), new[] { wallTile }, Vanilla, solid: true, _ => true);
        Assert.NotNull(s2);
        Assert.False(PlatformPlan.InBody(s2!.Value, wallTile));
        Assert.Null(HousePlan.InsideStand(l, new TilePoint(12, 19), new[] { wallTile }, Vanilla, solid: true, _ => false));
    }

    [Fact]
    public void SiteCheck_FlatGround_FloorKeptRestToPlace()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var c = HouseSite.Check(l, Flat(20), strict: true);
        Assert.True(c.Ok, c.Problem);
        Assert.DoesNotContain(c.FrameToPlace, t => t.Y == 20);
        Assert.Equal(10 + 4 + 4 - 3, c.FrameToPlace.Count); // ceiling + both sides - door
        Assert.Equal(32, c.WallsToPlace.Count);
        Assert.True(c.CanExit);
    }

    [Fact]
    public void SiteCheck_DecorInDoorSpotAndInside_IsClearedFirst()
    {
        // Seen in game: a rock pile in the door spot and a Teal Mushroom inside blocked the house.
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var door = l.DoorTiles.First();
        var inside = new TilePoint(14, 19);
        var c = HouseSite.Check(l, Flat(20, new() { [door] = new SiteCell(SiteTile.Decor), [inside] = new SiteCell(SiteTile.Decor) }), strict: true);
        Assert.True(c.Ok, c.Problem);
        Assert.NotNull(c.ClearFirst);
        Assert.Contains(door, c.ClearFirst!);
        Assert.Contains(inside, c.ClearFirst!);
        Assert.Contains(inside, c.WallsToPlace);
    }

    [Fact]
    public void SiteCheck_TreeInsideOrNaturalWall_IsAProblem()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var tree = HouseSite.Check(l, Flat(20, new() { [new TilePoint(14, 18)] = new SiteCell(SiteTile.Tree) }), strict: false);
        Assert.Contains("tree", tree.Problem);
        var wall = HouseSite.Check(l, Flat(20, new() { [new TilePoint(14, 18)] = new SiteCell(SiteTile.Air, WallState.Other) }), strict: false);
        Assert.Contains("natural background wall", wall.Problem);
        var houseWall = HouseSite.Check(l, Flat(20, new() { [new TilePoint(14, 18)] = new SiteCell(SiteTile.Air, WallState.House) }), strict: false);
        Assert.True(houseWall.Ok);
        Assert.Equal(31, houseWall.WallsToPlace.Count); // the existing house wall is kept
    }

    [Fact]
    public void SiteCheck_Lenient_KeepsExistingBlocksInTheFrame_StrictRejectsThem()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        var hill = Flat(20, new() { [new TilePoint(19, 19)] = new SiteCell(SiteTile.Solid) });
        var lenient = HouseSite.Check(l, hill, strict: false);
        Assert.True(lenient.Ok, lenient.Problem);
        Assert.DoesNotContain(new TilePoint(19, 19), lenient.FrameToPlace);
        Assert.False(HouseSite.Check(l, hill, strict: true).Ok);
    }

    [Fact]
    public void SiteCheck_BlockedDoorOrNpcHouse_IsAProblem()
    {
        var l = new HouseLayout(10, 20, 10, 6, DoorSide.Left);
        Assert.Contains("door spot", HouseSite.Check(l, Flat(20, new() { [new TilePoint(10, 18)] = new SiteCell(SiteTile.Solid) }), false).Problem);
        Assert.Contains("NPC", HouseSite.Check(l, Flat(20, new() { [new TilePoint(19, 17)] = new SiteCell(SiteTile.NpcHouse) }), false).Problem);
    }

    [Fact]
    public void Find_PicksNearestFlatSpot_DoorFacingThePlayer()
    {
        // Ground at y=20 everywhere; the player stands at x=0 on it. Everything left of x=3 has a wall of trees.
        var extra = new Dictionary<TilePoint, SiteCell>();
        for (int y = 10; y < 20; y++)
            for (int x = -40; x < 3; x++) extra[new TilePoint(x, y)] = new SiteCell(SiteTile.Tree);
        var l = HouseSite.Find(new TilePoint(0, 19), 10, 6, Flat(20, extra), radius: 20);
        Assert.NotNull(l);
        Assert.Equal(20, l!.FloorY);
        Assert.Equal(DoorSide.Left, l.Door);
        Assert.True(l.OutsideStand.X >= 3, $"exit at {l.OutsideStand} would be in the trees");
        Assert.True(HouseSite.Check(l, Flat(20, extra), strict: true).Ok);
    }

    [Fact]
    public void Find_NoGround_ReturnsNull() =>
        Assert.Null(HouseSite.Find(new TilePoint(0, 19), 10, 6, (_, _) => new SiteCell(SiteTile.Air), radius: 10));
}
