using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Map;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class MapRendererTests
{
    private static string[] Lines(string s) => s.Replace("\r", "").TrimEnd('\n').Split('\n');

    private static string[] GridRows(string s)
    {
        var lines = Lines(s);
        int end = Array.IndexOf(lines, "legend:");
        return lines[1..end];
    }

    [Fact]
    public void RendersGlyphsAndPlayer()
    {
        var (g, _) = ArrayTileGrid.Parse(
            ".....",
            ".....",
            "..=..",
            "~~###",
            "#####");
        string map = MapRenderer.Render(g, new TilePoint(2, 2), 2, new TilePoint(1, 2));
        var lines = Lines(map);
        Assert.Equal("origin: x=0 y=0", lines[0]);
        var rows = GridRows(map);
        Assert.Equal(5, rows.Length);
        Assert.Equal(".....", rows[0]);
        Assert.Equal(".@=..", rows[2]);
        Assert.Equal("~~###", rows[3]);
    }

    [Fact]
    public void OriginIsTopLeftOfWindow()
    {
        var (g, _) = ArrayTileGrid.Parse(".....", ".....", ".....");
        string map = MapRenderer.Render(g, new TilePoint(10, 20), 3, new TilePoint(10, 20));
        Assert.Equal("origin: x=7 y=17", Lines(map)[0]);
        var rows = GridRows(map);
        Assert.Equal(7, rows.Length);
        Assert.All(rows, r => Assert.Equal(7, r.Length));
        Assert.Equal('@', rows[3][3]);
    }

    [Fact]
    public void UnknownShownAsQuestionMark()
    {
        var (g, _) = ArrayTileGrid.Parse("...", ".?.", "...");
        string map = MapRenderer.Render(g, new TilePoint(1, 1), 1, new TilePoint(0, 0));
        Assert.Equal("@..", GridRows(map)[0]);
        Assert.Equal(".?.", GridRows(map)[1]);
        Assert.Contains("? unknown", Lines(map));
    }

    [Fact]
    public void DefaultGlyphsForAllKinds()
    {
        var (g, _) = ArrayTileGrid.Parse(".#=~Lh^?");
        string map = MapRenderer.Render(g, new TilePoint(3, 0), 4, new TilePoint(100, 100));
        Assert.Contains(".#=~Lh^?", GridRows(map)[4]);
    }

    [Fact]
    public void MarkersOverrideTiles()
    {
        var (g, _) = ArrayTileGrid.Parse("...", "###", "###");
        var markers = new Dictionary<TilePoint, char> { [new TilePoint(2, 1)] = 'E' };
        string map = MapRenderer.Render(g, new TilePoint(1, 1), 1, new TilePoint(0, 0), markers);
        Assert.Equal("##E", GridRows(map)[1]);
        Assert.Contains("E marker", Lines(map));
    }

    [Fact]
    public void MarkerNamesLabelLegend()
    {
        var (g, _) = ArrayTileGrid.Parse("...", "...", "...");
        var markers = new Dictionary<TilePoint, char> { [new TilePoint(2, 1)] = 'E', [new TilePoint(0, 2)] = 'i' };
        var names = new Dictionary<char, string> { ['E'] = "hostile" };
        string map = MapRenderer.Render(g, new TilePoint(1, 1), 1, new TilePoint(0, 0), markers, names);
        var lines = Lines(map);
        Assert.Contains("E hostile", lines);
        Assert.Contains("i marker", lines); // unnamed markers fall back to "marker"
    }

    [Fact]
    public void PlayerOverridesMarkers()
    {
        var (g, _) = ArrayTileGrid.Parse("...", "...", "...");
        var markers = new Dictionary<TilePoint, char> { [new TilePoint(1, 1)] = 'E' };
        string map = MapRenderer.Render(g, new TilePoint(1, 1), 1, new TilePoint(1, 1), markers);
        Assert.Equal(".@.", GridRows(map)[1]);
        Assert.DoesNotContain("E marker", Lines(map));
    }

    [Fact]
    public void LegendListsOnlyGlyphsPresent()
    {
        var (g, _) = ArrayTileGrid.Parse("....", "....", "####");
        g.Set(0, 1, new TileInfo(TileKind.Solid, Glyph: 'T', Name: "tree"));
        g.Set(2, 2, new TileInfo(TileKind.Solid, PickPowerRequired: 35, Glyph: 'c', Name: "Copper Ore"));
        string map = MapRenderer.Render(g, new TilePoint(1, 1), 1, new TilePoint(1, 0));
        var lines = Lines(map);
        int legendAt = Array.IndexOf(lines, "legend:");
        Assert.True(legendAt > 0);
        var legend = lines[(legendAt + 1)..];
        Assert.Contains("T tree", legend);
        Assert.Contains("c Copper Ore", legend);
        Assert.Contains("@ player", legend);
        Assert.Contains("# solid", legend);
        Assert.Contains(". air", legend);
        Assert.DoesNotContain(legend, l => l.StartsWith("~") || l.StartsWith("=") || l.StartsWith("?"));
        Assert.Equal(5, legend.Length);
    }

    [Fact]
    public void GlyphWithoutName_UsesKindName()
    {
        var (g, _) = ArrayTileGrid.Parse("...");
        g.Set(1, 0, new TileInfo(TileKind.Solid, Glyph: 'x'));
        string map = MapRenderer.Render(g, new TilePoint(1, 0), 0, new TilePoint(9, 9));
        Assert.Contains("x solid", Lines(map));
    }

    [Fact]
    public void SameGlyphDifferentNames_JoinedInLegend()
    {
        var (g, _) = ArrayTileGrid.Parse("...");
        g.Set(0, 0, new TileInfo(TileKind.Solid, Glyph: 's', Name: "Silver Ore"));
        g.Set(2, 0, new TileInfo(TileKind.Solid, Glyph: 's', Name: "Sapphire"));
        string map = MapRenderer.Render(g, new TilePoint(1, 0), 1, new TilePoint(9, 9));
        Assert.Contains("s Silver Ore/Sapphire", Lines(map));
    }

    [Fact]
    public void RadiusClampedTo40()
    {
        var (g, _) = ArrayTileGrid.Parse(".");
        string map = MapRenderer.Render(g, new TilePoint(0, 0), 500, new TilePoint(0, 0));
        var rows = GridRows(map);
        Assert.Equal(81, rows.Length);
        Assert.All(rows, r => Assert.Equal(81, r.Length));
        Assert.Equal("origin: x=-40 y=-40", Lines(map)[0]);
    }

    [Fact]
    public void NegativeRadius_TreatedAsZero()
    {
        var (g, _) = ArrayTileGrid.Parse(".");
        string map = MapRenderer.Render(g, new TilePoint(0, 0), -3, new TilePoint(0, 0));
        Assert.Equal(new[] { "@" }, GridRows(map));
    }
}
