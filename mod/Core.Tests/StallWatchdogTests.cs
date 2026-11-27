using LlmPlayer.Core.Tasks;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class StallWatchdogTests
{
    private readonly object _task = new();

    [Fact]
    public void SwimmingInCircles_IsAStall()
    {
        var w = new StallWatchdog(limitTicks: 100);
        bool stalled = false;
        for (int t = 0; t < 400 && !stalled; t++)
            stalled = w.Tick(_task, "walking: 18 tiles remaining", 4218 + t % 4, 645, 7, paused: false);
        Assert.True(stalled);
    }

    [Fact]
    public void LongWalk_NewTilesAreProgress()
    {
        var w = new StallWatchdog(limitTicks: 100);
        for (int t = 0; t < 1000; t++)
            Assert.False(w.Tick(_task, "walking", 1000 + t / 10, 500, 7, paused: false));
    }

    [Fact]
    public void Mining_InventoryChangesAreProgress()
    {
        var w = new StallWatchdog(limitTicks: 100);
        for (int t = 0; t < 1000; t++)
            Assert.False(w.Tick(_task, "mined 0/9", 10, 10, t / 60, paused: false)); // an ore every second
    }

    [Fact]
    public void Attack_NewProgressStringsAreProgress()
    {
        var w = new StallWatchdog(limitTicks: 100);
        for (int t = 0; t < 1000; t++)
            Assert.False(w.Tick(_task, $"Zombie {45 - t / 30}/45 life", 10, 10, 7, paused: false));
    }

    [Fact]
    public void PausedTicks_DontCount()
    {
        var w = new StallWatchdog(limitTicks: 100);
        for (int t = 0; t < 1000; t++)
            Assert.False(w.Tick(_task, "chopping", 10, 10, 7, paused: true));
    }

    [Fact]
    public void NewTask_Resets()
    {
        var w = new StallWatchdog(limitTicks: 100);
        for (int t = 0; t < 99; t++) w.Tick(_task, "x", 10, 10, 7, false);
        Assert.False(w.Tick(new object(), "x", 10, 10, 7, false));
        Assert.Equal(0, w.IdleSeconds);
    }

    [Fact]
    public void TimeCounters_AreNotProgress()
    {
        var w = new StallWatchdog(limitTicks: 100);
        bool stalled = false;
        for (int t = 0; t < 400 && !stalled; t++)
            stalled = w.Tick(_task, $"breaking a pot at (4180,736); {t / 60}/300 s, revealed 0, found 0", 10, 10, 7, paused: false);
        Assert.True(stalled);
        Assert.Equal("mined 3/9", StallWatchdog.WithoutTimers("mined 3/9"));
    }
}
