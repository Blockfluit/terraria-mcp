using LlmPlayer.Bridge;
using LlmPlayer.Control;
using LlmPlayer.Core.Control;

namespace LlmPlayer.Commands;

[CommandGroup]
public static class ControlCommands
{
    public static void Register()
    {
        CommandRouter.Register("get_control", _ => new { Driver = ControlState.Driver.WireName() }, isAction: false);

        // Never changes the driver: only the user's hotkey hands control over.
        CommandRouter.Register("request_control", _ => new
        {
            Driver = ControlState.Driver.WireName(),
            Granted = ControlState.Driver == Driver.Ai,
            Hint = $"Press {LlmPlayerMod.ToggleKeyName()} in game to hand control to the AI",
        }, isAction: false);
    }
}
