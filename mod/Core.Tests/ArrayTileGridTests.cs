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

    [Fact]
    public void Parse_HardShapedBlocks_AreUnbreakableWithTheirShape()
    {
        var (g, _) = ArrayTileGrid.Parse(")(-RQ");
        var expect = new[] { (TileSlope.DownRight, false), (TileSlope.DownLeft, false), (TileSlope.None, true), (TileSlope.UpLeft, false), (TileSlope.UpRight, false) };
        for (int x = 0; x < expect.Length; x++)
        {
            var t = g.Get(x, 0);
            Assert.Equal(TileKind.Solid, t.Kind);
            Assert.Equal(int.MaxValue, t.PickPowerRequired);
            Assert.Equal(expect[x].Item1, t.Slope);
            Assert.Equal(expect[x].Item2, t.HalfBlock);
        }
    }

    [Fact]
    public void Parse_SoftShapes_StayDiggable()
    {
        var (g, _) = ArrayTileGrid.Parse("/\\_rq");
        for (int x = 0; x < 5; x++) Assert.Equal(0, g.Get(x, 0).PickPowerRequired);
    }

    [Fact]
    public void GlyphOf_ChecksHardnessBeforeShape()
    {
        var hardSlope = new TileInfo(TileKind.Solid, 65, Slope: TileSlope.DownRight);
        Assert.Equal(')', ArrayTileGrid.GlyphOf(hardSlope));
        Assert.Equal('/', ArrayTileGrid.GlyphOf(hardSlope with { PickPowerRequired = 0 }));
        Assert.Equal('-', ArrayTileGrid.GlyphOf(new TileInfo(TileKind.Solid, 65, HalfBlock: true)));
        Assert.Equal('X', ArrayTileGrid.GlyphOf(new TileInfo(TileKind.Solid, 65)));
        Assert.Equal('#', ArrayTileGrid.GlyphOf(new TileInfo(TileKind.Solid, 35)));
    }

    [Theory]
    [InlineData("#X=.~L^?|P/\\_rq)(-RQh")]
    public void GlyphOf_RoundTripsEveryParseGlyph(string row)
    {
        var (g, _) = ArrayTileGrid.Parse(row);
        var back = new string(Enumerable.Range(0, row.Length).Select(x => ArrayTileGrid.GlyphOf(g.Get(x, 0))).ToArray());
        Assert.Equal(row, back);
    }
}
