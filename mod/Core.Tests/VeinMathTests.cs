using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class VeinMathTests
{
    private static Func<int, int, bool> From(params string[] rows) =>
        (x, y) => y >= 0 && y < rows.Length && x >= 0 && x < rows[y].Length && rows[y][x] == 'o';

    [Fact]
    public void CollectsEightConnectedTilesOfTheVeinOnly()
    {
        var f = From(
            "#o#####",
            "#oo####",
            "###o###", // diagonal neighbour joins
            "#####oo"); // separate vein
        var v = VeinMath.Collect(f, new TilePoint(1, 0));
        Assert.Equal(new[] { new TilePoint(1, 0), new TilePoint(1, 1), new TilePoint(2, 1), new TilePoint(3, 2) }.ToHashSet(), v.ToHashSet());
        Assert.Equal(new TilePoint(1, 0), v[0]);
    }

    [Fact]
    public void StartNotOre_Empty() => Assert.Empty(VeinMath.Collect(From("###"), new TilePoint(1, 0)));

    [Fact]
    public void RespectsMax()
    {
        var f = From(new string('o', 50), new string('o', 50));
        Assert.Equal(30, VeinMath.Collect(f, new TilePoint(0, 0), max: 30).Count);
    }
}
