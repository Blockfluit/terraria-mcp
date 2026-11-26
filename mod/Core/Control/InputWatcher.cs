namespace LlmPlayer.Core.Control;

/// <summary>Raw device state. Never fill from game-adjusted values (e.g. Main.mouseX after the mod's cursor override).</summary>
public readonly record struct RawInputSnapshot(
    IReadOnlySet<int> KeysDown, bool LeftButton, bool RightButton, int MouseX, int MouseY, int ScrollValue);

public static class InputWatcher
{
    /// <summary>
    /// XNA key codes for Alt and Windows (LeftWindows 91, RightWindows 92, LeftAlt 164, RightAlt 165). These start OS
    /// window-switching chords (Alt+Tab, Win, Win+D…), which the user needs to reach the terminal after handing over
    /// control, so neither they nor any key pressed while they are held count as user input.
    /// </summary>
    private static readonly HashSet<int> OsSwitchModifiers = new() { 91, 92, 164, 165 };

    /// <summary>
    /// True if the user did something between <paramref name="prev"/> and <paramref name="cur"/>:
    /// a newly pressed key (other than the toggle key, Alt/Win, or any key while Alt/Win is held), a mouse button
    /// going up to down, a scroll change, or — unless <paramref name="ignoreMouseMove"/> — mouse movement with
    /// Euclidean distance strictly greater than <paramref name="mouseMoveThreshold"/> pixels.
    /// </summary>
    public static bool IsUserInput(RawInputSnapshot prev, RawInputSnapshot cur, int toggleKey,
        int mouseMoveThreshold = 8, bool ignoreMouseMove = false)
    {
        if (!cur.KeysDown.Any(OsSwitchModifiers.Contains))
            foreach (var k in cur.KeysDown)
                if (k != toggleKey && !prev.KeysDown.Contains(k)) return true;

        if (cur.LeftButton && !prev.LeftButton) return true;
        if (cur.RightButton && !prev.RightButton) return true;
        if (cur.ScrollValue != prev.ScrollValue) return true;
        if (ignoreMouseMove) return false;

        long dx = cur.MouseX - prev.MouseX, dy = cur.MouseY - prev.MouseY;
        return dx * dx + dy * dy > (long)mouseMoveThreshold * mouseMoveThreshold;
    }
}
