using LlmPlayer.Core.Control;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class TakeoverPolicyTests
{
    [Fact]
    public void FreshSession_AllowsTakeover() =>
        Assert.Null(new TakeoverPolicy().Refusal(Driver.User, playerDead: false, "Insert"));

    [Fact]
    public void UserHotkeyCancel_BlocksTakeoverUntilUserHandsOver()
    {
        var p = new TakeoverPolicy();
        p.OnDriverChanged(Driver.Ai, "requested");
        p.OnDriverChanged(Driver.User, "hotkey"); // the user cancels AI mode
        Assert.True(p.RevokedByUser);
        Assert.Contains("only the user", p.Refusal(Driver.User, false, "Insert"));

        p.OnDriverChanged(Driver.Ai, "hotkey"); // the user hands control over again
        Assert.False(p.RevokedByUser);
        p.OnDriverChanged(Driver.User, "left_world");
        Assert.Null(p.Refusal(Driver.User, false, "Insert"));
    }

    [Fact]
    public void LeavingTheWorld_DoesNotClearARevoke()
    {
        var p = new TakeoverPolicy();
        p.OnDriverChanged(Driver.User, "hotkey");
        p.OnDriverChanged(Driver.User, "left_world");
        p.OnDriverChanged(Driver.Ai, "requested"); // can't happen through the policy, and doesn't clear it either
        Assert.True(p.RevokedByUser);
    }

    [Fact]
    public void Dead_IsRefused() =>
        Assert.Contains("dead", new TakeoverPolicy().Refusal(Driver.User, playerDead: true, "Insert"));

    [Fact]
    public void AlreadyAi_IsFine()
    {
        var p = new TakeoverPolicy();
        p.OnDriverChanged(Driver.User, "hotkey");
        Assert.Null(p.Refusal(Driver.Ai, false, "Insert"));
    }
}
