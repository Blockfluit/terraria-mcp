using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;

namespace LlmPlayer.Core.Tests;

public class SkipReportTests
{
    [Fact]
    public void CountsByReason_QuietReasonsAreNotExamples()
    {
        var r = new SkipReport("air");
        r.Add(new TilePoint(1, 1), "air");
        r.Add(new TilePoint(2, 1), "protected");
        r.Add(new TilePoint(3, 1), "protected");
        r.Add(new TilePoint(4, 1), "occupied");
        Assert.Equal(4, r.Count);
        Assert.Equal(2, r.ByReason["protected"]);
        Assert.Equal(1, r.ByReason["air"]);
        Assert.Equal(new[] { new SkippedTile(2, 1, "protected"), new SkippedTile(3, 1, "protected"), new SkippedTile(4, 1, "occupied") }, r.Examples);
        Assert.Equal("2 protected, 1 air, 1 occupied", r.Summary());
    }

    [Fact]
    public void Examples_AreCapped()
    {
        var r = new SkipReport();
        for (int i = 0; i < 25; i++) r.Add(new TilePoint(i, 0), "occupied");
        Assert.Equal(25, r.Count);
        Assert.Equal(SkipReport.MaxExamples, r.Examples.Count);
        Assert.Equal("", new SkipReport().Summary());
    }
}
