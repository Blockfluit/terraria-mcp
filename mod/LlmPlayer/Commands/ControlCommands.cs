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

        // Takes control when allowed (the user permits the AI to switch AI mode on), except after the user took
        // control back with the hotkey: then only the user can hand it over again.
        CommandRouter.Register("request_control", _ =>
        {
            string? refused = Terraria.Main.LocalPlayer is { active: true } p ? ControlState.TryAiTakeover(p) : "no player";
            return new
            {
                Driver = ControlState.Driver.WireName(),
                Granted = ControlState.Driver == Driver.Ai,
                Reason = refused,
                Hint = refused == null ? null : $"Ask the user to press {LlmPlayerMod.ToggleKeyName()} in game to hand control to the AI",
            };
        }, isAction: false);
    }
}
