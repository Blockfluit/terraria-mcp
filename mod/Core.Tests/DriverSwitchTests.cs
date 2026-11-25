using LlmPlayer.Core.Control;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class DriverSwitchTests
{
    [Fact]
    public void StartsWithUser()
        => Assert.Equal(Driver.User, new DriverSwitch().Driver);

    [Fact]
    public void Set_ChangesDriverAndRaisesChangedOnce()
    {
        var s = new DriverSwitch();
        var seen = new List<(Driver, string)>();
        s.Changed += (d, r) => seen.Add((d, r));

        Assert.True(s.Set(Driver.Ai, "hotkey"));

        Assert.Equal(Driver.Ai, s.Driver);
        Assert.Equal(new[] { (Driver.Ai, "hotkey") }, seen);
    }

    [Fact]
    public void Set_SameDriver_IsNoOpAndSilent()
    {
        var s = new DriverSwitch();
        var raised = 0;
        s.Changed += (_, _) => raised++;

        Assert.False(s.Set(Driver.User, "player_died"));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Toggle_FlipsBothWays()
    {
        var s = new DriverSwitch();
        var seen = new List<(Driver, string)>();
        s.Changed += (d, r) => seen.Add((d, r));

        Assert.Equal(Driver.Ai, s.Toggle("hotkey"));
        Assert.Equal(Driver.User, s.Toggle("hotkey"));

        Assert.Equal(new[] { (Driver.Ai, "hotkey"), (Driver.User, "hotkey") }, seen);
    }

    [Fact]
    public void Reset_ReturnsToUserWithoutRaising()
    {
        var s = new DriverSwitch();
        s.Set(Driver.Ai, "hotkey");
        var raised = 0;
        s.Changed += (_, _) => raised++;

        s.Reset();

        Assert.Equal(Driver.User, s.Driver);
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(Driver.User, "user")]
    [InlineData(Driver.Ai, "ai")]
    public void WireName(Driver d, string expected)
        => Assert.Equal(expected, d.WireName());
}
