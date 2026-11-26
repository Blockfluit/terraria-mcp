namespace LlmPlayer.Core.Control;

/// <summary>
/// May the AI switch AI mode on by itself (request_control)? The user allows it, with one hard rule: when the user
/// takes control back with the hotkey, that sticks. From then on only the user's own hotkey hands control over again
/// (which clears the latch). The latch lives for the game process, not the world: leaving and re-entering a world
/// doesn't re-open it, so a cancel can't be undone by the AI. A fresh game launch starts open, which lets the
/// automated dev reload loop take control after relaunching.
/// </summary>
public sealed class TakeoverPolicy
{
    /// <summary>The user took control back with the hotkey and hasn't handed it over since.</summary>
    public bool RevokedByUser { get; private set; }

    /// <summary>Feed every driver change (DriverSwitch.Changed).</summary>
    public void OnDriverChanged(Driver driver, string reason)
    {
        if (reason != "hotkey") return;
        RevokedByUser = driver == Driver.User;
    }

    /// <summary>Null if the AI may take control now, else why not (shown to the LLM).</summary>
    public string? Refusal(Driver current, bool playerDead, string toggleKey)
    {
        if (current == Driver.Ai) return null;
        if (RevokedByUser)
            return $"the user took control back with {toggleKey}; only the user can hand it over again (ask them to press {toggleKey})";
        if (playerDead) return "the player is dead; wait for the respawn";
        return null;
    }

    public void Reset() => RevokedByUser = false;
}
