using LlmPlayer.Core.Npc;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class NpcMathTests
{
    [Theory]
    [InlineData(0L, "free")]
    [InlineData(1L, "1 copper")]
    [InlineData(99L, "99 copper")]
    [InlineData(100L, "1 silver")]
    [InlineData(250L, "2 silver 50 copper")]
    [InlineData(10000L, "1 gold")]
    [InlineData(15000L, "1 gold 50 silver")]
    [InlineData(1000000L, "1 platinum")]
    [InlineData(2030405L, "2 platinum 3 gold 4 silver 5 copper")]
    [InlineData(1000001L, "1 platinum 1 copper")]
    public void FormatCoins(long copper, string expected) => Assert.Equal(expected, CoinMath.Format(copper));

    [Fact]
    public void FormatCoins_NegativeIsFree() => Assert.Equal("free", CoinMath.Format(-5));

    // Player 20x42 px at (1000,1000): centre (1010,1021). Default range 5x4 tiles -> box x 930..1090, y 957..1085.
    [Fact]
    public void TalkRange_NpcInsideBox() =>
        Assert.True(NpcMath.InTalkRange(1000f, 1000f, 20, 42, 5, 4, 1060, 1000, 18, 40));

    [Fact]
    public void TalkRange_TouchingEdgeIsOutside()
    {
        // Box right edge is 1090 (exclusive); an NPC starting exactly there does not intersect (XNA Intersects).
        Assert.False(NpcMath.InTalkRange(1000f, 1000f, 20, 42, 5, 4, 1090, 1000, 18, 40));
        Assert.True(NpcMath.InTalkRange(1000f, 1000f, 20, 42, 5, 4, 1089, 1000, 18, 40));
    }

    [Fact]
    public void TalkRange_TooFarVertically()
    {
        // Box bottom is 957 + 128 = 1085.
        Assert.False(NpcMath.InTalkRange(1000f, 1000f, 20, 42, 5, 4, 1000, 1085, 18, 40));
        Assert.True(NpcMath.InTalkRange(1000f, 1000f, 20, 42, 5, 4, 1000, 1084, 18, 40));
    }

    [Fact]
    public void TalkRange_LargerRangeReaches() =>
        Assert.True(NpcMath.InTalkRange(1000f, 1000f, 20, 42, 8, 4, 1120, 1000, 18, 40));

    [Fact]
    public void TalkRange_TruncatesFractionalPositionLikeVanilla()
    {
        // left = (int)(1000.9 + 10 - 80) = 930; NPC right edge at 930 (x 912 + 18) does not intersect, 931 does.
        Assert.False(NpcMath.InTalkRange(1000.9f, 1000f, 20, 42, 5, 4, 912, 1000, 18, 40));
        Assert.True(NpcMath.InTalkRange(1000.9f, 1000f, 20, 42, 5, 4, 913, 1000, 18, 40));
    }
}
