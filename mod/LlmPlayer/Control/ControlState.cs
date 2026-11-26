using LlmPlayer.Bridge;
using LlmPlayer.Core.Control;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Control;

/// <summary>
/// Who drives the local player, plus the AI's control intent for the next frame. Main thread only.
/// The user's toggle hotkey (<see cref="AiModPlayer"/>) hands control over or takes it back. The AI may also switch
/// AI mode on itself (<see cref="TryAiTakeover"/>, the request_control command), unless the user took control back
/// with the hotkey: that sticks until the user hands control over again (<see cref="TakeoverPolicy"/>). Only the
/// hotkey (or leaving the world) ever takes control away from the AI.
/// </summary>
public static class ControlState
{
    private static readonly DriverSwitch Switch = new();
    private static readonly TakeoverPolicy Takeover = new();

    static ControlState() => Switch.Changed += OnSwitchChanged;

    public static Driver Driver => Switch.Driver;

    /// <summary>
    /// Controls the AI wants this frame. Written by the running task each tick; applied by
    /// <see cref="AiModPlayer.SetControls"/> only while <see cref="Driver"/> is Ai. Reset to default
    /// whenever control returns to the user.
    /// </summary>
    public static ControlIntent Pending;

    /// <summary>Optional label for the on-screen indicator ("AI driving: &lt;label&gt;"); null → "idle". Task 10 sets it.</summary>
    public static Func<string?>? TaskLabel;

    /// <summary>
    /// Raised after the driver changes (never for a no-op). Reasons: "hotkey", "user_input", "player_died", "left_world".
    /// Subscribers (e.g. the task host cancelling with the reason) run on the main thread; exceptions are swallowed
    /// per subscriber so one bad handler can't block the handback. Cleared on mod unload.
    /// </summary>
    public static event Action<Driver, string>? Changed;

    /// <summary>Change the driver. Taking control away from the AI is refused unless
    /// <see cref="HandoverPolicy.ReleasesControl"/> allows the reason (hotkey or leaving the world).</summary>
    public static void SetDriver(Driver d, string reason)
    {
        if (d == Driver.User && Switch.Driver == Driver.Ai && !HandoverPolicy.ReleasesControl(reason)) return;
        Switch.Set(d, reason);
    }

    /// <summary>The user cancelled AI mode with the hotkey and hasn't handed control over since.</summary>
    public static bool RevokedByUser => Takeover.RevokedByUser;

    /// <summary>
    /// The AI switches AI mode on (request_control). Refused (null → granted, else the reason) when the user took
    /// control back with the hotkey, while the player is dead, or when the session's save backup fails — the same
    /// backup-first rule as the hotkey. Tells the user in chat how to take control back.
    /// </summary>
    public static string? TryAiTakeover(Terraria.Player p)
    {
        if (Driver == Driver.Ai) return null;
        string key = LlmPlayerMod.ToggleKeyName();
        if (Takeover.Refusal(Driver, p.dead, key) is { } why) return why;
        if (!Safety.SaveBackup.EnsureForSession(out var error)) return $"save backup failed, not taking control: {error}";
        SetDriver(Driver.Ai, "requested");
        Terraria.Main.NewText($"LlmPlayer: the AI took control. Press {key} to take it back.", 120, 220, 255);
        return null;
    }

    private static void OnSwitchChanged(Driver d, string reason)
    {
        Takeover.OnDriverChanged(d, reason);
        if (d == Driver.User) Pending = default;
        BridgeSystem.Emit("control_changed", new { Driver = d.WireName(), Reason = reason });

        var changed = Changed;
        if (changed == null) return;
        foreach (var h in changed.GetInvocationList())
        {
            try
            {
                ((Action<Driver, string>)h)(d, reason);
            }
            catch (Exception e)
            {
                LlmPlayerMod.Log($"ControlState.Changed handler {h.Method.DeclaringType?.Name}.{h.Method.Name} failed: {e}");
            }
        }
    }

    internal static void Unload()
    {
        Switch.Reset();
        Pending = default;
        TaskLabel = null;
        Changed = null;
    }
}
