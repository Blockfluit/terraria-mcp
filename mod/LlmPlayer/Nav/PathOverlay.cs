using LlmPlayer.Config;
using LlmPlayer.Control;
using LlmPlayer.Core.Control;
using LlmPlayer.Core.Nav;
using LlmPlayer.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;

namespace LlmPlayer.Nav;

/// <summary>
/// Draws the remaining path of the running movement task as small squares at each step's feet tile (colour by
/// move kind, dig/build targets outlined) while the AI drives and <see cref="LlmConfig.ShowPathOverlay"/> is on.
/// </summary>
public sealed class PathOverlay : ModSystem
{
    private const int MaxDrawn = 400;

    public override void PostDrawTiles()
    {
        var follower = PathOverlayState.Follower;
        if (follower == null || ControlState.Driver != Driver.Ai || Main.gameMenu || !LlmConfig.Current.ShowPathOverlay) return;

        var pixel = TextureAssets.MagicPixel.Value;
        var sb = Main.spriteBatch;
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None,
            RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);
        try
        {
            int n = 0;
            foreach (var s in follower.Remaining)
            {
                if (n++ >= MaxDrawn) break;
                var feet = new Vector2(s.Pos.X * 16 + 16, s.Pos.Y * 16 + 8) - Main.screenPosition; // body centre, feet row
                sb.Draw(pixel, new Rectangle((int)feet.X - 3, (int)feet.Y - 3, 6, 6), Colour(s.Kind) * 0.8f);
                if (s.WorkTile is { } w)
                {
                    var tl = new Vector2(w.X * 16, w.Y * 16) - Main.screenPosition;
                    Outline(sb, pixel, new Rectangle((int)tl.X, (int)tl.Y, 16, 16), Colour(s.Kind));
                }
            }
        }
        finally
        {
            sb.End();
        }
    }

    private static Color Colour(MoveKind k) => k switch
    {
        MoveKind.Walk or MoveKind.StepUp => new Color(120, 220, 255),
        MoveKind.Jump => new Color(255, 230, 90),
        MoveKind.Fall or MoveKind.DropThrough => new Color(255, 150, 60),
        MoveKind.Swim => new Color(80, 140, 255),
        MoveKind.Dig => new Color(255, 80, 80),
        MoveKind.OpenDoor or MoveKind.CloseDoor => new Color(200, 140, 255),
        _ => new Color(120, 255, 120), // BuildPlatform
    };

    private static void Outline(SpriteBatch sb, Texture2D px, Rectangle r, Color c)
    {
        sb.Draw(px, new Rectangle(r.X, r.Y, r.Width, 1), c);
        sb.Draw(px, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), c);
        sb.Draw(px, new Rectangle(r.X, r.Y, 1, r.Height), c);
        sb.Draw(px, new Rectangle(r.Right - 1, r.Y, 1, r.Height), c);
    }
}
