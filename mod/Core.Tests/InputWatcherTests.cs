using LlmPlayer.Core.Control;
using Xunit;

namespace LlmPlayer.Core.Tests;

public class InputWatcherTests
{
    private const int Toggle = 119; // F8

    private static RawInputSnapshot Snap(int[]? keys = null, bool left = false, bool right = false,
        int x = 100, int y = 100, int scroll = 0)
        => new(new HashSet<int>(keys ?? Array.Empty<int>()), left, right, x, y, scroll);

    [Fact]
    public void NewKeyPressed_IsUserInput()
        => Assert.True(InputWatcher.IsUserInput(Snap(), Snap(new[] { 65 }), Toggle));

    [Fact]
    public void ToggleKeyIgnored()
        => Assert.False(InputWatcher.IsUserInput(Snap(), Snap(new[] { Toggle }), Toggle));

    [Fact]
    public void HeldKeyFromBeforeHandoff_NotUserInput()
        => Assert.False(InputWatcher.IsUserInput(Snap(new[] { 65 }), Snap(new[] { 65 }), Toggle));

    [Fact]
    public void MouseMovedBeyondThreshold_IsUserInput()
        => Assert.True(InputWatcher.IsUserInput(Snap(x: 100), Snap(x: 120), Toggle));

    [Fact]
    public void MouseJitterBelowThreshold_Ignored()
        => Assert.False(InputWatcher.IsUserInput(Snap(x: 100, y: 100), Snap(x: 103, y: 104), Toggle));

    [Fact]
    public void CursorOverrideIsNotUserInput()
    {
        // The mod's virtual cursor override only changes Main.mouseX/Y; it never feeds this
        // snapshot, which is read from raw device state. So an AI-driven frame yields two
        // identical raw snapshots and must not hand control back.
        var raw = Snap(x: 500, y: 300);
        Assert.False(InputWatcher.IsUserInput(raw, raw, Toggle));
    }

    [Fact]
    public void MouseButtonDown_IsUserInput()
    {
        Assert.True(InputWatcher.IsUserInput(Snap(), Snap(left: true), Toggle));
        Assert.True(InputWatcher.IsUserInput(Snap(), Snap(right: true), Toggle));
    }

    [Fact]
    public void MouseButtonHeldOrReleased_NotUserInput()
    {
        Assert.False(InputWatcher.IsUserInput(Snap(left: true), Snap(left: true), Toggle));
        Assert.False(InputWatcher.IsUserInput(Snap(left: true), Snap(), Toggle));
    }

    [Fact]
    public void ScrollChange_IsUserInput()
        => Assert.True(InputWatcher.IsUserInput(Snap(scroll: 0), Snap(scroll: 120), Toggle));

    [Fact]
    public void ThresholdIsEuclidean_StrictlyGreater()
    {
        // distance exactly 8 (threshold) is not "more than"; (6,6) is ~8.49 so it is.
        Assert.False(InputWatcher.IsUserInput(Snap(x: 0, y: 0), Snap(x: 8, y: 0), Toggle));
        Assert.True(InputWatcher.IsUserInput(Snap(x: 0, y: 0), Snap(x: 6, y: 6), Toggle));
    }

    private const int LeftAlt = 164, RightAlt = 165, LeftWin = 91, RightWin = 92, Tab = 9;

    [Theory]
    [InlineData(LeftAlt)]
    [InlineData(RightAlt)]
    [InlineData(LeftWin)]
    [InlineData(RightWin)]
    public void OsSwitchModifierAlone_NotUserInput(int key)
        => Assert.False(InputWatcher.IsUserInput(Snap(), Snap(new[] { key }), Toggle));

    [Fact]
    public void AltTab_NotUserInput()
        => Assert.False(InputWatcher.IsUserInput(Snap(new[] { LeftAlt }), Snap(new[] { LeftAlt, Tab }), Toggle));

    [Fact]
    public void WinChord_NotUserInput()
        => Assert.False(InputWatcher.IsUserInput(Snap(new[] { LeftWin }), Snap(new[] { LeftWin, 68 }), Toggle));

    [Fact]
    public void KeyAfterAltReleased_IsUserInput()
        => Assert.True(InputWatcher.IsUserInput(Snap(new[] { LeftAlt }), Snap(new[] { Tab }), Toggle));

    [Fact]
    public void ClickWhileAltHeld_IsUserInput()
        => Assert.True(InputWatcher.IsUserInput(Snap(new[] { LeftAlt }), Snap(new[] { LeftAlt }, left: true), Toggle));

    [Fact]
    public void IgnoreMouseMove_LargeMoveNotUserInput()
        => Assert.False(InputWatcher.IsUserInput(Snap(x: 0), Snap(x: 500), Toggle, ignoreMouseMove: true));

    [Fact]
    public void IgnoreMouseMove_ClicksScrollAndKeysStillCount()
    {
        Assert.True(InputWatcher.IsUserInput(Snap(), Snap(left: true), Toggle, ignoreMouseMove: true));
        Assert.True(InputWatcher.IsUserInput(Snap(), Snap(scroll: 120), Toggle, ignoreMouseMove: true));
        Assert.True(InputWatcher.IsUserInput(Snap(), Snap(new[] { 65 }), Toggle, ignoreMouseMove: true));
    }
}
