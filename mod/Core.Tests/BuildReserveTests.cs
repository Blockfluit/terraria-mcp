using LlmPlayer.Core.Build;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class BuildReserveTests
{
    [Theory]
    [InlineData(266, 0, 20, false, 246)]
    [InlineData(266, 50, 20, false, 296)]
    [InlineData(10, 50, 20, false, 50)]
    [InlineData(10, 50, 20, true, 60)]
    [InlineData(0, 0, 20, false, 0)]
    [InlineData(30, 0, 0, false, 30)]
    public void Budget_KeepsTheReserveUnlessReleased(int platforms, int blocks, int reserve, bool released, int expected) =>
        Assert.Equal(expected, BuildReserve.Budget(platforms, blocks, reserve, released));

    [Fact]
    public void Bridges_UseBlocksBeforeEatingIntoTheReserve()
    {
        Assert.Equal(BuildPick.Platform, BuildReserve.Pick(false, 21, 99, 20));
        Assert.Equal(BuildPick.Block, BuildReserve.Pick(false, 20, 99, 20));
        Assert.Equal(BuildPick.Platform, BuildReserve.Pick(false, 20, 0, 20)); // released route: reserve only when no block is left
        Assert.Equal(BuildPick.None, BuildReserve.Pick(false, 0, 0, 20));
    }

    [Fact]
    public void Descents_TakePlatformsWheneverThereAreAny()
    {
        Assert.Equal(BuildPick.Platform, BuildReserve.Pick(true, 5, 99, 20));
        Assert.Equal(BuildPick.Block, BuildReserve.Pick(true, 0, 99, 20));
    }

    [Fact]
    public void Release_OnlyWhenTheReserveHeldItemsBack_AndMoreItemsCouldHelp()
    {
        Assert.True(BuildReserve.ShouldRelease(false, true, 30, 0, 20, "no_path"));
        Assert.False(BuildReserve.ShouldRelease(true, true, 30, 0, 20, "no_path"));      // already released
        Assert.False(BuildReserve.ShouldRelease(false, false, 30, 0, 20, "no_path"));    // building not allowed
        Assert.False(BuildReserve.ShouldRelease(false, true, 0, 50, 20, "no_path"));     // no platforms held back
        Assert.False(BuildReserve.ShouldRelease(false, true, 30, 0, 0, "no_path"));      // no reserve
        Assert.False(BuildReserve.ShouldRelease(false, true, 30, 0, 20, "goal_not_standable: (1,2)"));
    }

    [Fact]
    public void Note_WarnsWhenPlatformsRunShort()
    {
        Assert.Null(BuildReserve.Note(100, 20, false));
        Assert.Contains("reserve 20", BuildReserve.Note(15, 20, false));
        Assert.Contains("used the platform reserve", BuildReserve.Note(3, 20, true));
    }
}
