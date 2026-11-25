using LlmPlayer.Core.Control;
using LlmPlayer.Safety;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace LlmPlayer.Control;

/// <summary>
/// Draws "AI driving: &lt;task or idle&gt; (F8 to take back)" at the top centre while the AI drives, and returns
/// control to the user when the world is left (also resetting the once-per-session save backup).
/// </summary>
public sealed class ControlIndicator : ModSystem
{
    public override void OnWorldLoad() => SaveBackup.ResetSession();

    public override void OnWorldUnload()
    {
        ControlState.SetDriver(Driver.User, "left_world");
        SaveBackup.ResetSession();
    }

    public override void PostDrawInterface(SpriteBatch spriteBatch)
    {
        if (ControlState.Driver != Driver.Ai || Main.gameMenu) return;

        string label;
        try
        {
            label = ControlState.TaskLabel?.Invoke() ?? "idle";
        }
        catch
        {
            label = "?";
        }
        var text = $"AI driving: {label} ({LlmPlayerMod.ToggleKeyName()} to take back)";
        Utils.DrawBorderString(spriteBatch, text, new Vector2(Main.screenWidth / 2f, 110f), new Color(120, 220, 255),
            1f, 0.5f, 0f);
    }
}
