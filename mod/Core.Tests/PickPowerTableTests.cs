using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class PickPowerTableTests
{
    // worldSurface 300, underworld 1100, 4200 wide (small world).
    private static readonly PickContext Ctx = new(WorldSurface: 300, UnderworldLayer: 1100, MaxTilesX: 4200);

    [Theory]
    [InlineData(PickPowerTable.Dirt, 0)]
    [InlineData(PickPowerTable.Stone, 0)]
    [InlineData(PickPowerTable.Copper, 0)]
    [InlineData(PickPowerTable.Gold, 0)]
    [InlineData(PickPowerTable.Meteorite, 50)]
    [InlineData(PickPowerTable.Obsidian, 55)]
    [InlineData(PickPowerTable.Ebonstone, 65)]
    [InlineData(PickPowerTable.Crimstone, 65)]
    [InlineData(PickPowerTable.Pearlstone, 65)]
    [InlineData(PickPowerTable.Hellstone, 65)]
    [InlineData(PickPowerTable.Cobalt, 100)]
    [InlineData(PickPowerTable.Palladium, 100)]
    [InlineData(PickPowerTable.Mythril, 110)]
    [InlineData(PickPowerTable.Orichalcum, 110)]
    [InlineData(PickPowerTable.Adamantite, 150)]
    [InlineData(PickPowerTable.Titanium, 150)]
    [InlineData(PickPowerTable.Chlorophyte, 200)]
    [InlineData(PickPowerTable.LihzahrdBrick, 210)]
    [InlineData(PickPowerTable.LihzahrdAltar, 210)]
    public void FixedRequirements(int type, int expected) =>
        Assert.Equal(expected, PickPowerTable.Required(type, 100, 500, Ctx));

    [Fact]
    public void EvilOres_OnlyGatedBelowSurface()
    {
        Assert.Equal(0, PickPowerTable.Required(PickPowerTable.Demonite, 100, 300, Ctx));
        Assert.Equal(55, PickPowerTable.Required(PickPowerTable.Demonite, 100, 301, Ctx));
        Assert.Equal(55, PickPowerTable.Required(PickPowerTable.Crimtane, 100, 301, Ctx));
    }

    [Fact]
    public void Hellforge_GatedOnlyInUnderworld()
    {
        Assert.Equal(0, PickPowerTable.Required(PickPowerTable.Hellforge, 100, 1099, Ctx));
        Assert.Equal(65, PickPowerTable.Required(PickPowerTable.Hellforge, 100, 1100, Ctx));
    }

    [Fact]
    public void LihzahrdTraps_GatedByFrame()
    {
        Assert.Equal(0, PickPowerTable.Required(PickPowerTable.Traps, 100, 500, Ctx, frameY: 0)); // dart trap
        Assert.Equal(210, PickPowerTable.Required(PickPowerTable.Traps, 100, 500, Ctx, frameY: 18));
        Assert.Equal(210, PickPowerTable.Required(PickPowerTable.Traps, 100, 500, Ctx, frameY: 72));
        Assert.Equal(0, PickPowerTable.Required(PickPowerTable.Traps, 100, 500, Ctx, frameY: 90));
    }

    [Fact]
    public void DungeonBricks_NeedHundredBelowSurfaceOutsideCenter()
    {
        // Outside the middle 30% of the world and below the surface.
        Assert.Equal(100, PickPowerTable.Required(41, 100, 500, Ctx, isDungeonTile: true));
        Assert.Equal(100, PickPowerTable.Required(41, 4100, 500, Ctx, isDungeonTile: true));
        // Center strip or surface: no gate.
        Assert.Equal(0, PickPowerTable.Required(41, 2100, 500, Ctx, isDungeonTile: true));
        Assert.Equal(0, PickPowerTable.Required(41, 100, 300, Ctx, isDungeonTile: true));
    }

    [Theory]
    [InlineData(0, 100, true)]
    [InlineData(100, 99, false)]
    [InlineData(100, 100, true)]
    [InlineData(int.MaxValue, 5000, false)]
    public void CanMine(int required, int pickPower, bool expected) =>
        Assert.Equal(expected, PickPowerTable.CanMine(required, pickPower));
}
