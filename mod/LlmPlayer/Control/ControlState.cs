using LlmPlayer.Bridge;
using LlmPlayer.Core.Control;
using LlmPlayer.Core.Nav;

namespace LlmPlayer.Control;

/// <summary>
/// Who drives the local player, plus the AI's control intent for the next frame. Main thread only.
/// The AI never takes control itself: only the toggle hotkey (<see cref="AiModPlayer"/>) sets <see cref="Driver.Ai"/>.
/// </summary>
public static class ControlState
{
    private static readonly DriverSwitch Switch = new();

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

    /// <summary>Change the driver. No-op if unchanged. Emits <c>control_changed {driver, reason}</c>.</summary>
    /// <summary>Change the driver. Taking control away from the AI is refused unless
    /// <see cref="HandoverPolicy.ReleasesControl"/> allows the reason (hotkey or leaving the world).</summary>
    public static void SetDriver(Driver d, string reason)
    {
        if (d == Driver.User && Switch.Driver == Driver.Ai && !HandoverPolicy.ReleasesControl(reason)) return;
        Switch.Set(d, reason);
    }

    private static void OnSwitchChanged(Driver d, string reason)
    {
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
