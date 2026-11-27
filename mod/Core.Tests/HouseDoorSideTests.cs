using LlmPlayer.Core.Build;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Tests;

/// <summary>build_house doors must open onto room to stand, and a house must not wall off a neighbour's door (seen in
/// game: houses at x 4423-4432, door right, and 4434-4443, door left: the doors faced each other across a 1-tile gap
/// and the second house was unreachable).</summary>
public class HouseDoorSideTests
{
    private static readonly SiteCell Ground = new(SiteTile.Solid, Natural: true);
    private static readonly SiteCell Air = new(SiteTile.Air);
    private static readonly SiteCell Block = new(SiteTile.Solid);
    private static readonly SiteCell DoorCell = new(SiteTile.Object, Door: true);

    private static Func<int, int, SiteCell> Terrain(int groundY, Dictionary<TilePoint, SiteCell>? extra = null) => (x, y) =>
        extra != null && extra.TryGetValue(new TilePoint(x, y), out var c) ? c : y >= groundY ? Ground : Air;

    /// <summary>An existing 10x6 house with its frame as plain blocks and a door on <paramref name="door"/>.</summary>
    private static void AddHouse(Dictionary<TilePoint, SiteCell> world, HouseLayout l)
    {
        foreach (var t in l.FrameTiles()) world[t] = Block;
        foreach (var t in l.DoorTiles) world[t] = DoorCell;
    }

    [Fact]
    public void DefaultDoor_FacesThePlayer_WhenThereIsRoom()
    {
        var cell = Terrain(20);
        Assert.Equal(DoorSide.Left, HouseSite.DefaultDoor(10, 20, 10, 6, new TilePoint(2, 19), cell));
        Assert.Equal(DoorSide.Right, HouseSite.DefaultDoor(10, 20, 10, 6, new TilePoint(30, 19), cell));
    }

    [Fact]
    public void DefaultDoor_LiveCase_SecondHouseOpensAwayFromTheNeighbour()
    {
        // House 1 at 4423-4432 with its door on the right (x 4432); the player stands in front of it at 4433.
        var world = new Dictionary<TilePoint, SiteCell>();
        AddHouse(world, new HouseLayout(4423, 20, 10, 6, DoorSide.Right));
        var cell = Terrain(20, world);
        // New house at 4436 or 4435: the left side (facing the player) has a clear 2-wide stand: left.
        Assert.Equal(DoorSide.Left, HouseSite.DefaultDoor(4436, 20, 10, 6, new TilePoint(4433, 19), cell));
        Assert.Equal(DoorSide.Left, HouseSite.DefaultDoor(4435, 20, 10, 6, new TilePoint(4433, 19), cell));
        // New house at 4434 (the live case): a left door would open onto house 1's door across a 1-tile gap: right.
        Assert.Equal(DoorSide.Right, HouseSite.DefaultDoor(4434, 20, 10, 6, new TilePoint(4433, 19), cell));
    }

    [Fact]
    public void DefaultDoor_BlockedFacingSide_PicksTheOther()
    {
        // A rock wall right of the house: the right door would open into it although the player stands on that side.
        var extra = new Dictionary<TilePoint, SiteCell>();
        for (int y = 14; y < 20; y++) extra[new TilePoint(21, y)] = Ground;
        Assert.Equal(DoorSide.Left, HouseSite.DefaultDoor(10, 20, 10, 6, new TilePoint(25, 15), Terrain(20, extra)));
    }

    [Fact]
    public void DefaultDoor_NoGroundOutside_PicksTheSideWithGround()
    {
        // A cliff on the left: no ground to stand on outside a left door.
        var extra = new Dictionary<TilePoint, SiteCell>();
        for (int x = 0; x < 10; x++)
            for (int y = 20; y < 40; y++) extra[new TilePoint(x, y)] = Air;
        Assert.Equal(DoorSide.Right, HouseSite.DefaultDoor(10, 20, 10, 6, new TilePoint(2, 19), Terrain(20, extra)));
    }

    [Fact]
    public void DefaultDoor_NeitherSideClear_FacesThePlayer()
    {
        var extra = new Dictionary<TilePoint, SiteCell>();
        for (int y = 14; y < 20; y++) { extra[new TilePoint(8, y)] = Ground; extra[new TilePoint(21, y)] = Ground; }
        Assert.Equal(DoorSide.Left, HouseSite.DefaultDoor(10, 20, 10, 6, new TilePoint(2, 19), Terrain(20, extra)));
    }

    [Fact]
    public void DefaultDoor_DecorOutside_CountsAsOpen()
    {
        var extra = new Dictionary<TilePoint, SiteCell> { [new TilePoint(8, 19)] = new SiteCell(SiteTile.Decor) };
        Assert.Equal(DoorSide.Left, HouseSite.DefaultDoor(10, 20, 10, 6, new TilePoint(2, 19), Terrain(20, extra)));
    }

    [Theory]
    [InlineData(4434, DoorSide.Right)] // 1 free column: the live case
    [InlineData(4434, DoorSide.Left)]
    [InlineData(4433, DoorSide.Right)] // wall right against the door
    public void Check_WallingOffANeighboursDoor_IsAProblem_InBothModes(int x, DoorSide side)
    {
        var world = new Dictionary<TilePoint, SiteCell>();
        AddHouse(world, new HouseLayout(4423, 20, 10, 6, DoorSide.Right));
        var l = new HouseLayout(x, 20, 10, 6, side);
        foreach (bool strict in new[] { false, true })
        {
            var c = HouseSite.Check(l, Terrain(20, world), strict);
            Assert.Contains("wall off the door at (4432,", c.Problem);
            Assert.Contains("x >= 4435", c.Problem);
        }
    }

    [Fact]
    public void Check_TwoFreeColumnsBeforeANeighboursDoor_IsFine()
    {
        var world = new Dictionary<TilePoint, SiteCell>();
        AddHouse(world, new HouseLayout(4423, 20, 10, 6, DoorSide.Right));
        var c = HouseSite.Check(new HouseLayout(4435, 20, 10, 6, DoorSide.Right), Terrain(20, world), strict: true);
        Assert.True(c.Ok, c.Problem);
        Assert.Null(HouseSite.BlockedDoor(new HouseLayout(4435, 20, 10, 6, DoorSide.Right), Terrain(20, world)));
    }

    [Fact]
    public void Check_NeighbourDoorOnTheRight_HintsTheLeftLimit()
    {
        var world = new Dictionary<TilePoint, SiteCell>();
        AddHouse(world, new HouseLayout(30, 20, 10, 6, DoorSide.Left)); // door at x 30
        var c = HouseSite.Check(new HouseLayout(19, 20, 10, 6, DoorSide.Left), Terrain(20, world), strict: false); // right wall 28
        Assert.Contains("wall off the door at (30,", c.Problem);
        Assert.Contains("x <= 18", c.Problem);
        Assert.True(HouseSite.Check(new HouseLayout(18, 20, 10, 6, DoorSide.Left), Terrain(20, world), strict: false).Ok);
    }

    [Fact]
    public void Check_DoorAboveOrBelowTheHouseRows_DoesNotCount()
    {
        var world = new Dictionary<TilePoint, SiteCell>();
        for (int y = 8; y <= 10; y++) world[new TilePoint(9, y)] = DoorCell; // far above the 6-high house (rows 15..20)
        Assert.True(HouseSite.Check(new HouseLayout(10, 20, 10, 6, DoorSide.Right), Terrain(20, world), strict: true).Ok);
    }

    [Fact]
    public void Find_NextToAHouse_LeavesItsDoorReachable_AndOpensOntoRoom()
    {
        // House 1 at 4423-4432, door right. Rock wall at 4446 and beyond (nothing to stand on right of 4445). The player
        // stands in front of house 1's door.
        var world = new Dictionary<TilePoint, SiteCell>();
        AddHouse(world, new HouseLayout(4423, 20, 10, 6, DoorSide.Right));
        for (int x = 4446; x < 4500; x++)
            for (int y = 0; y < 20; y++) world[new TilePoint(x, y)] = Ground;
        for (int x = 4380; x < 4423; x++)
            for (int y = 0; y < 20; y++) world[new TilePoint(x, y)] = Ground;
        var cell = Terrain(20, world);
        var found = HouseSite.FindWithCheck(new TilePoint(4433, 19), 10, 6, cell, radius: 20);
        Assert.NotNull(found);
        var (l, c) = found!.Value;
        Assert.True(l.Left >= 4435, $"{l} walls off house 1's door");
        Assert.True(c.CanExit);
        Assert.True(HouseSite.ExitClear(l, cell));
        // House 1's door still has its 2-wide stand.
        var stand = new HouseLayout(4423, 20, 10, 6, DoorSide.Right).OutsideStand;
        Assert.True(l.Left > stand.X + 1, $"{l} covers house 1's outside stand {stand}");
    }
}
