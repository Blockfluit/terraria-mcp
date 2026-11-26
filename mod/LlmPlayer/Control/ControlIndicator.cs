using LlmPlayer.Core.Control;
using LlmPlayer.Safety;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;

namespace LlmPlayer.Control;

/// <summary>
/// While the AI drives, draws a pulsing coloured frame around the screen and an "AI IS PLAYING" banner with the
/// current task and the key to take control back. Also returns
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

    private const int BorderPx = 8;

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

        // Pulsing cyan frame around the whole screen: impossible to miss that the AI has the controls.
        float pulse = 0.55f + 0.45f * (float)Math.Sin(Main.GlobalTimeWrappedHourly * 4f);
        var frame = new Color(60, 200, 255) * pulse;
        int w = Main.screenWidth, h = Main.screenHeight;
        var px = TextureAssets.MagicPixel.Value;
        spriteBatch.Draw(px, new Rectangle(0, 0, w, BorderPx), frame);
        spriteBatch.Draw(px, new Rectangle(0, h - BorderPx, w, BorderPx), frame);
        spriteBatch.Draw(px, new Rectangle(0, 0, BorderPx, h), frame);
        spriteBatch.Draw(px, new Rectangle(w - BorderPx, 0, BorderPx, h), frame);

        // Banner: dark backing bar, big title, then what it's doing and how to take back control.
        var key = LlmPlayerMod.ToggleKeyName();
        spriteBatch.Draw(px, new Rectangle(w / 2 - 260, 92, 520, 78), Color.Black * 0.55f);
        Utils.DrawBorderStringBig(spriteBatch, "AI IS PLAYING", new Vector2(w / 2f, 96f), new Color(120, 220, 255), 0.6f, 0.5f, 0f);
        Utils.DrawBorderString(spriteBatch, $"{label}   ·   press {key} to take back control", new Vector2(w / 2f, 142f),
            Color.White, 0.9f, 0.5f, 0f);
    }
}
