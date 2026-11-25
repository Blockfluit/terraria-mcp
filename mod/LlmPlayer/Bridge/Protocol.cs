namespace LlmPlayer.Bridge;

// Framing, parsing and serialization live in Core (LlmPlayer.Core.Bridge.WireProtocol, CommandRegistry,
// CommandError, Params) so they are unit-tested without the game. This file only names the wire constants.

public static class ErrorCodes
{
    public const string UnknownMethod = "unknown_method";
    public const string BadParams = "bad_params";
    public const string NotInWorld = "not_in_world";
    public const string UserHasControl = "user_has_control";
    public const string NotVisible = "not_visible";
    public const string ProtectedTile = "protected_tile";
    public const string OutOfReach = "out_of_reach";
    public const string MissingItem = "missing_item";
    public const string MissingStation = "missing_station";
    public const string NoPath = "no_path";
    public const string Internal = "internal";
}

public static class EventNames
{
    public const string TaskFinished = "task_finished";
    public const string ControlChanged = "control_changed";
    public const string TookDamage = "took_damage";
    public const string Reflex = "reflex";
}
