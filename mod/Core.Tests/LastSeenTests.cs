using LlmPlayer.Core.Combat;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class LastSeenTests
{
    private static readonly EnemyMotion Seen = new(100f, 50f, 3f, 3f, 0.5f, -0.25f);

    [Fact]
    public void JustSeen_Unchanged() => Assert.Equal(Seen, LastSeen.Extrapolate(Seen, 0));

    [Fact]
    public void CarriedAlongItsVelocity()
    {
        var m = LastSeen.Extrapolate(Seen, 20);
        Assert.Equal(110f, m.CX, 3);
        Assert.Equal(45f, m.CY, 3);
        Assert.Equal(0.5f, m.VX);
    }

    [Fact]
    public void AfterOneSecond_HeldStillAtTheGuess()
    {
        var m = LastSeen.Extrapolate(Seen, 600);
        Assert.Equal(130f, m.CX, 3);
        Assert.Equal(35f, m.CY, 3);
        Assert.Equal(0f, m.VX);
        Assert.Equal(0f, m.VY);
    }
}
