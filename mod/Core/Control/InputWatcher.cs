namespace LlmPlayer.Core.Control;

/// <summary>Raw device state. Never fill from game-adjusted values (e.g. Main.mouseX after the mod's cursor override).</summary>
public readonly record struct RawInputSnapshot(
    IReadOnlySet<int> KeysDown, bool LeftButton, bool RightButton, int MouseX, int MouseY, int ScrollValue);

public static class InputWatcher
{
    /// <summary>
    /// True if the user did something between <paramref name="prev"/> and <paramref name="cur"/>:
    /// a newly pressed key (other than the toggle key), a mouse button going up to down,
    /// a scroll change, or mouse movement with Euclidean distance strictly greater than
    /// <paramref name="mouseMoveThreshold"/> pixels.
    /// </summary>
    public static bool IsUserInput(RawInputSnapshot prev, RawInputSnapshot cur, int toggleKey, int mouseMoveThreshold = 8)
    {
        foreach (var k in cur.KeysDown)
            if (k != toggleKey && !prev.KeysDown.Contains(k)) return true;

        if (cur.LeftButton && !prev.LeftButton) return true;
        if (cur.RightButton && !prev.RightButton) return true;
        if (cur.ScrollValue != prev.ScrollValue) return true;

        long dx = cur.MouseX - prev.MouseX, dy = cur.MouseY - prev.MouseY;
        return dx * dx + dy * dy > (long)mouseMoveThreshold * mouseMoveThreshold;
    }
}
