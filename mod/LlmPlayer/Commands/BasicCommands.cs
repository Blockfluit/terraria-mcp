using LlmPlayer.Bridge;
using Terraria.ModLoader;

namespace LlmPlayer.Commands;

[CommandGroup]
public static class BasicCommands
{
    public static void Register()
    {
        CommandRouter.Register("ping", _ => new
        {
            Pong = true,
            Version = ModContent.GetInstance<LlmPlayerMod>().Version.ToString(),
        }, isAction: false, requiresWorld: false);

        // get_state lives in ObserveCommands (Observe/StateReader).
    }
}
