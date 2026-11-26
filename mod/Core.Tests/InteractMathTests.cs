using LlmPlayer.Core.Interact;
using LlmPlayer.Core.Observe;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class InteractMathTests
{
    // Vanilla player hitbox 20x42 px, tileRangeX/Y = 5/4.
    private static bool InRange(float px, float py, int tx, int ty) =>
        InteractMath.InTileInteractionRange(px, py, 20, 42, 5, 4, tx, ty);

    [Theory]
    [InlineData(2, 10, true)]   // 6.25 - 5 = 1.25 <= 2
    [InlineData(1, 10, false)]  // 1.25 > 1
    [InlineData(11, 10, true)]  // 7.5 + 5 - 1 = 11.5 >= 11
    [InlineData(12, 10, false)]
    [InlineData(7, 6, true)]    // 10 - 4 = 6
    [InlineData(7, 5, false)]
    [InlineData(7, 14, true)]   // 12.625 + 4 - 2 = 14.6
    [InlineData(7, 15, false)]
    public void InteractionRange_Edges(int tx, int ty, bool expected) => Assert.Equal(expected, InRange(100, 160, tx, ty));

    [Fact]
    public void InteractionRange_CappedAt20()
    {
        Assert.True(InteractMath.InTileInteractionRange(0, 0, 20, 42, 100, 100, 20, 0));
        Assert.False(InteractMath.InTileInteractionRange(0, 0, 20, 42, 100, 100, 22, 0));
    }

    [Theory]
    [InlineData(10, 20, 0, 0, 10, 20)]
    [InlineData(11, 20, 18, 0, 10, 20)]
    [InlineData(11, 21, 18, 18, 10, 20)]
    [InlineData(10, 21, 36 * 3, 18, 10, 20)] // style 3, left column
    [InlineData(11, 21, 36 * 3 + 18, 18, 10, 20)]
    public void ChestOrigin(int x, int y, int fx, int fy, int ox, int oy) =>
        Assert.Equal((ox, oy), InteractMath.ChestOrigin(x, y, fx, fy));

    [Theory]
    [InlineData(12, 5, 36, 18, 10, 4)]
    [InlineData(10, 4, 54, 0, 10, 4)] // style 1, column 0
    public void DresserOrigin(int x, int y, int fx, int fy, int ox, int oy) =>
        Assert.Equal((ox, oy), InteractMath.DresserOrigin(x, y, fx, fy));

    [Fact]
    public void BedSpawnPoint_SameFromEveryTileOfTheBed()
    {
        // Left-facing bed at x 10..13, rows 5..6 (frameX 0..54); right-facing at 20..23 (frameX 72..126).
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 2; r++)
            {
                Assert.Equal((12, 7), InteractMath.BedSpawnPoint(10 + c, 5 + r, c * 18, r * 18));
                Assert.Equal((21, 7), InteractMath.BedSpawnPoint(20 + c, 5 + r, 72 + c * 18, 36 * 2 + r * 18)); // style 2
            }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(18, true)]
    [InlineData(36, false)]
    [InlineData(54, false)]
    [InlineData(72, false)]
    [InlineData(90, false)]
    [InlineData(108, true)]
    [InlineData(126, true)]
    public void BedFootSide(int frameX, bool foot) => Assert.Equal(foot, InteractMath.IsBedFootSide(frameX));

    [Fact]
    public void PlanTake_TakesFromSlotsInOrderUpToCount()
    {
        var m = new List<(int, int)> { (3, 10), (7, 0), (9, 25) };
        Assert.Equal(new List<(int, int)> { (3, 10), (9, 5) }, InteractMath.PlanTake(m, 15));
        Assert.Equal(new List<(int, int)> { (3, 4) }, InteractMath.PlanTake(m, 4));
        Assert.Equal(new List<(int, int)> { (3, 10), (9, 25) }, InteractMath.PlanTake(m, null));
        Assert.Equal(new List<(int, int)> { (3, 10), (9, 25) }, InteractMath.PlanTake(m, 999));
        Assert.Empty(InteractMath.PlanTake(m, 0));
    }

    [Fact]
    public void MatchAll_DisplayNameFirstThenInternal()
    {
        var slots = new ItemName?[]
        {
            new ItemName("Wood", "Wood"), null, new ItemName("Torch", "Torch"), new ItemName("wood", "Wood"),
            new ItemName("Lesser Healing Potion", "LesserHealingPotion"),
        };
        Assert.Equal(new[] { 0, 3 }, InteractMath.MatchAll(slots, " WOOD "));
        Assert.Equal(new[] { 4 }, InteractMath.MatchAll(slots, "lesserhealingpotion"));
        Assert.Empty(InteractMath.MatchAll(slots, "Gel"));
        Assert.Empty(InteractMath.MatchAll(slots, " "));
    }

    [Fact]
    public void Diff_PerNameNetChange()
    {
        var before = new[] { new SlotItem(0, "Wood", 50), new SlotItem(1, "Gel", 3) };
        var after = new[] { new SlotItem(0, "Wood", 99), new SlotItem(1, "Gel", 3), new SlotItem(2, "Wood", 10), new SlotItem(3, "Torch", 5) };
        Assert.Equal(new[] { new ItemDelta("Wood", 59), new ItemDelta("Torch", 5) }, InteractMath.Diff(before, after));
        Assert.Equal(new[] { new ItemDelta("Wood", -59), new ItemDelta("Torch", -5) }, InteractMath.Diff(after, before));
    }

    [Fact]
    public void RoomFailureText_KnownAndUnknown()
    {
        Assert.Contains("too small", InteractMath.RoomFailureText("RoomIsTooSmall"));
        Assert.Contains("not valid housing", InteractMath.RoomFailureText("None"));
    }
}
