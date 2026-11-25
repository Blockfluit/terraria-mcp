using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class ArrayTileGridTests
{
    [Fact]
    public void Parse_ReadsGlyphsAndMarkers()
    {
        var (g, m) = ArrayTileGrid.Parse("S.=", "#X~");
        Assert.Equal(new TilePoint(0, 0), m['S']);
        Assert.Equal(TileKind.Air, g.Get(0, 0).Kind);
        Assert.Equal(TileKind.Platform, g.Get(2, 0).Kind);
        Assert.Equal(int.MaxValue, g.Get(1, 1).PickPowerRequired);
        Assert.Equal(TileKind.Water, g.Get(2, 1).Kind);
    }

    [Fact]
    public void OutOfBounds_IsUnbreakableSolid()
    {
        var (g, _) = ArrayTileGrid.Parse("..");
        Assert.True(g.Get(-1, 0).IsSolid);
        Assert.Equal(int.MaxValue, g.Get(5, 5).PickPowerRequired);
    }

    [Fact]
    public void Parse_ProtectedAndHazardsAndUnknown()
    {
        var (g, _) = ArrayTileGrid.Parse("P^?L");
        Assert.True(g.Get(0, 0).Protected);
        Assert.True(g.Get(0, 0).IsSolid);
        Assert.Equal(TileKind.Hazard, g.Get(1, 0).Kind);
        Assert.Equal(TileKind.Unknown, g.Get(2, 0).Kind);
        Assert.Equal(TileKind.Lava, g.Get(3, 0).Kind);
    }

    [Fact]
    public void Standable_IncludesPlatform_BlocksBodyOnlySolid()
    {
        var (g, _) = ArrayTileGrid.Parse("=#.");
        Assert.True(g.Get(0, 0).IsStandable);
        Assert.False(g.Get(0, 0).BlocksBody);
        Assert.True(g.Get(1, 0).BlocksBody);
        Assert.False(g.Get(2, 0).IsStandable);
    }

    [Fact]
    public void Set_OverridesTile()
    {
        var (g, _) = ArrayTileGrid.Parse("..");
        g.Set(1, 0, new TileInfo(TileKind.Solid));
        Assert.True(g.Get(1, 0).IsSolid);
    }
}
