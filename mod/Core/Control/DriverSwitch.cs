namespace LlmPlayer.Core.Control;

/// <summary>Who drives the character. The AI never selects itself; only the user's hotkey sets <see cref="Ai"/>.</summary>
public enum Driver { User, Ai }

public static class DriverExtensions
{
    /// <summary>"user" / "ai" — the wire spelling (matches the bridge's lowercase enum serialization).</summary>
    public static string WireName(this Driver d) => d == Driver.Ai ? "ai" : "user";
}

/// <summary>Two-state driver switch. <see cref="Changed"/> fires only on an actual change.</summary>
public sealed class DriverSwitch
{
    public Driver Driver { get; private set; } = Driver.User;

    public event Action<Driver, string>? Changed;

    /// <summary>Returns false (and stays silent) if <paramref name="d"/> is already the driver.</summary>
    public bool Set(Driver d, string reason)
    {
        if (d == Driver) return false;
        Driver = d;
        Changed?.Invoke(d, reason);
        return true;
    }

    /// <summary>Flips the driver and returns the new one.</summary>
    public Driver Toggle(string reason)
    {
        Set(Driver == Driver.User ? Driver.Ai : Driver.User, reason);
        return Driver;
    }

    /// <summary>Back to <see cref="Driver.User"/> without raising <see cref="Changed"/> (mod unload).</summary>
    public void Reset() => Driver = Driver.User;
}

/// <summary>
/// What may take control away from the AI once the user handed it over: only the user pressing the toggle key again,
/// or leaving the world. The user's own keyboard/mouse input and the character dying do not (dying only cancels the
/// current task).
/// </summary>
public static class HandoverPolicy
{
    public static bool ReleasesControl(string reason) => reason is "hotkey" or "left_world";
}
