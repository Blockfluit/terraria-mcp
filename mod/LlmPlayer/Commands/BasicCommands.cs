using LlmPlayer.Bridge;
using Terraria;
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

        // Minimal until Task 9 replaces it with the full state.
        CommandRouter.Register("get_state", _ =>
        {
            var p = Main.LocalPlayer;
            return new
            {
                X = (int)Math.Floor(p.position.X / 16f),
                // Standing tile row: the player's feet are at Bottom.Y; the floor tile is the row below.
                Y = (int)Math.Floor((p.Bottom.Y - 1f) / 16f),
                Life = p.statLife,
                LifeMax = p.statLifeMax2,
                InWorld = true,
            };
        }, isAction: false);
    }
}
