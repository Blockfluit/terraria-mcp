using LlmPlayer.Core.Observe;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class GameClockTests
{
    [Theory]
    [InlineData(true, 0, "day 4:30 AM")]
    [InlineData(true, 7200, "day 6:30 AM")]
    [InlineData(true, 27000, "day 12:00 PM")]
    [InlineData(true, 53999, "day 7:29 PM")]
    [InlineData(false, 0, "night 7:30 PM")]
    [InlineData(false, 16200, "night 12:00 AM")]
    [InlineData(false, 18000, "night 12:30 AM")]
    [InlineData(false, 32399, "night 4:29 AM")]
    public void Formats(bool day, double time, string expected) =>
        Assert.Equal(expected, GameClock.Format(day, time));
}

public class DepthLayerTests
{
    // worldSurface 300, rockLayer 400, underworld 1100. Space is y <= 300*0.3499999940395355 (just under 105).
    [Theory]
    [InlineData(0, "space")]
    [InlineData(104, "space")]
    [InlineData(105, "surface")]
    [InlineData(300, "surface")]
    [InlineData(301, "underground")]
    [InlineData(400, "underground")]
    [InlineData(401, "cavern")]
    [InlineData(1100, "cavern")]
    [InlineData(1101, "underworld")]
    public void Classifies(int y, string expected) =>
        Assert.Equal(expected, DepthLayer.Classify(y, 300, 400, 1100));
}

public class ScreenRectTests
{
    [Fact]
    public void FromPixels_AddsMarginInTiles()
    {
        // Screen at pixel (160,320), 800x480 → tiles x 10..59, y 20..49 (inclusive), plus 4 margin.
        var r = ScreenRect.FromPixels(160, 320, 800, 480, marginTiles: 4);
        Assert.Equal(6, r.Left);
        Assert.Equal(16, r.Top);
        Assert.Equal(63, r.Right);
        Assert.Equal(53, r.Bottom);
        Assert.True(r.Contains(6, 16));
        Assert.True(r.Contains(63, 53));
        Assert.False(r.Contains(5, 30));
        Assert.False(r.Contains(30, 54));
    }

    [Fact]
    public void FromPixels_PartialTilesIncluded()
    {
        var r = ScreenRect.FromPixels(8, 8, 16, 16, marginTiles: 0);
        Assert.Equal(0, r.Left);
        Assert.Equal(0, r.Top);
        Assert.Equal(1, r.Right);
        Assert.Equal(1, r.Bottom);
    }
}

public class NearestTests
{
    [Fact]
    public void SortsByDistanceAndCaps()
    {
        var items = Enumerable.Range(0, 100).Select(i => (Id: i, D: (double)(100 - i))).ToList();
        var picked = Nearest.Take(items, x => x.D, 30);
        Assert.Equal(30, picked.Count);
        Assert.Equal(99, picked[0].Id);
        Assert.Equal(70, picked[29].Id);
    }

    [Fact]
    public void TiesKeepInputOrder()
    {
        var items = new[] { ("a", 1.0), ("b", 1.0), ("c", 0.5) };
        var picked = Nearest.Take(items, x => x.Item2, 30);
        Assert.Equal(new[] { "c", "a", "b" }, picked.Select(p => p.Item1));
    }

    [Fact]
    public void EntityCapIs30() => Assert.Equal(30, Nearest.EntityCap);
}

public class FindSelectorTests
{
    [Fact]
    public void LimitsToTenNearest()
    {
        var cands = Enumerable.Range(1, 50).Select(i => new FindCandidate("Copper Ore", i * 10, 0)).ToList();
        var res = FindSelector.Select(cands, 0, 0, max: 10, clusterRadius: 0);
        Assert.Equal(10, res.Count);
        Assert.Equal(10, res[0].X);
        Assert.Equal(10.0, res[0].Distance);
        Assert.Equal(100, res[9].X);
    }

    [Fact]
    public void ClustersSameNameWithinRadius()
    {
        var cands = new List<FindCandidate>
        {
            new("Copper Ore", 10, 0), new("Copper Ore", 11, 0), new("Copper Ore", 12, 1),
            new("Tin Ore", 11, 1), new("Copper Ore", 30, 0),
        };
        var res = FindSelector.Select(cands, 0, 0, max: 10, clusterRadius: 3);
        Assert.Equal(new[] { ("Copper Ore", 10), ("Tin Ore", 11), ("Copper Ore", 30) },
            res.Select(r => (r.Name, r.X)));
    }

    [Fact]
    public void NameFilterIsCaseInsensitiveSubstring()
    {
        var cands = new List<FindCandidate> { new("Copper Ore", 1, 0), new("Tin Ore", 2, 0) };
        var res = FindSelector.Select(cands, 0, 0, max: 10, clusterRadius: 0, name: "tin");
        Assert.Single(res);
        Assert.Equal("Tin Ore", res[0].Name);
    }

    [Fact]
    public void DistanceRoundedToOneDecimal()
    {
        var res = FindSelector.Select(new[] { new FindCandidate("x", 1, 1) }, 0, 0, max: 10, clusterRadius: 0);
        Assert.Equal(1.4, res[0].Distance);
    }
}

public class ItemMatcherTests
{
    private static readonly ItemName?[] Slots =
    {
        new("Copper Pickaxe", "CopperPickaxe"),
        null,
        new("Wood", "Wood"),
        new("Torch", "Torch"),
        new("Lesser Healing Potion", "LesserHealingPotion"),
    };

    [Theory]
    [InlineData("wood", 2)]
    [InlineData("WOOD", 2)]
    [InlineData("Copper Pickaxe", 0)]
    [InlineData("LesserHealingPotion", 4)]
    [InlineData("lesserhealingpotion", 4)]
    [InlineData("Gold Pickaxe", -1)]
    [InlineData("", -1)]
    public void MatchesDisplayThenInternal(string query, int expected) =>
        Assert.Equal(expected, ItemMatcher.Find(Slots, query));

    [Fact]
    public void DisplayNameBeatsEarlierInternalMatch()
    {
        var slots = new ItemName?[] { new("Foo Display", "Bar"), new("Bar", "Other") };
        Assert.Equal(1, ItemMatcher.Find(slots, "bar"));
    }
}

public class EventLogTests
{
    [Fact]
    public void KeepsMostRecentUpToCapacity()
    {
        var log = new EventLog(capacity: 3);
        for (int i = 0; i < 5; i++) log.Add($"e{i}");
        Assert.Equal(new[] { "e2", "e3", "e4" }, log.Recent());
    }

    [Fact]
    public void DrainEmptiesLog()
    {
        var log = new EventLog(capacity: 10);
        log.Add("a");
        log.Add("b");
        Assert.Equal(new[] { "a", "b" }, log.Drain());
        Assert.Empty(log.Recent());
    }

    [Fact]
    public void CollapsesRepeatsOfTheSameMessage()
    {
        var log = new EventLog(capacity: 10);
        log.Add("picked up Wood");
        log.Add("picked up Wood");
        log.Add("picked up Wood");
        log.Add("killed Green Slime");
        Assert.Equal(new[] { "picked up Wood (x3)", "killed Green Slime" }, log.Recent());
    }
}
