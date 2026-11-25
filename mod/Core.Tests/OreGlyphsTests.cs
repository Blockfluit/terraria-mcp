using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class OreGlyphsTests
{
    [Theory]
    [InlineData(7, 'c')]    // copper
    [InlineData(166, 't')]  // tin
    [InlineData(6, 'r')]    // iron ('i' is the dropped-item marker)
    [InlineData(167, 'l')]  // lead
    [InlineData(9, 's')]    // silver
    [InlineData(168, 'u')]  // tungsten
    [InlineData(8, 'g')]    // gold
    [InlineData(169, 'p')]  // platinum
    [InlineData(22, 'd')]   // demonite
    [InlineData(204, 'k')]  // crimtane
    [InlineData(211, 'b')]  // chlorophyte
    public void VanillaOres(int type, char glyph) => Assert.Equal(glyph, OreGlyphs.For(type));

    [Fact]
    public void AllGlyphsAreLowercaseAndAvoidReserved()
    {
        for (int type = 0; type < 2000; type++)
        {
            char g = OreGlyphs.For(type);
            Assert.InRange(g, 'a', 'z');
            Assert.DoesNotContain(g, OreGlyphs.Reserved);
        }
    }

    [Fact]
    public void VanillaOresAreDistinct()
    {
        var glyphs = OreGlyphs.Vanilla.Values.ToList();
        // Gems intentionally share one glyph; every other vanilla ore is unique.
        var nonGem = OreGlyphs.Vanilla.Where(kv => kv.Value != OreGlyphs.GemGlyph).Select(kv => kv.Value).ToList();
        Assert.Equal(nonGem.Count, nonGem.Distinct().Count());
    }

    [Fact]
    public void UnknownOres_AreStableAndAvoidVanillaLetters()
    {
        var vanillaLetters = OreGlyphs.Vanilla.Values.ToHashSet();
        for (int type = 700; type < 800; type++)
        {
            char a = OreGlyphs.For(type), b = OreGlyphs.For(type);
            Assert.Equal(a, b);
            Assert.DoesNotContain(a, vanillaLetters);
        }
    }
}
