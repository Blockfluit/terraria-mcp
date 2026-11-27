using LlmPlayer.Core.Observe;
using Terraria;
using Terraria.GameContent.Drawing;

namespace LlmPlayer.Observe;

/// <summary>
/// Fair vision: the AI sees only what the player could see.
/// A tile is visible if it's revealed on the map, or it's on screen (+4 tiles) and lit,
/// or Spelunker shows it (on screen), or Dangersense shows it (on screen).
/// An entity is visible if it's on screen (+4 tiles) and lit at its center, or (NPCs only) Hunter is active.
/// </summary>
public static class Visibility
{
    public const int ScreenMarginTiles = 4;
    public const float MinBrightness = 0.05f;

    public static ScreenRect Screen =>
        ScreenRect.FromPixels(Main.screenPosition.X, Main.screenPosition.Y, Main.screenWidth, Main.screenHeight, ScreenMarginTiles);

    public static bool IsTileVisible(int x, int y) => IsTileVisible(x, y, Screen, Main.LocalPlayer);

    /// <summary>Overload for scans: compute <paramref name="screen"/> once.</summary>
    public static bool IsTileVisible(int x, int y, ScreenRect screen, Player player)
    {
        if (!WorldGen.InWorld(x, y)) return false;
        if (Main.Map != null && Main.Map.IsRevealed(x, y)) return true;
        if (Touching(player, x, y)) return true;
        if (!screen.Contains(x, y)) return false;
        if (Lighting.Brightness(x, y) > MinBrightness) return true;
        var tile = Main.tile[x, y];
        if (player.findTreasure && tile.HasTile && Main.tileSpelunker[tile.TileType]) return true;
        if (player.dangerSense && tile.HasTile && TileDrawing.IsTileDangerous(x, y, player)) return true;
        return false;
    }

    /// <summary>Within one tile of the player's hitbox: known by touch even in the dark (the tiles the player stands
    /// in, on and bumps into), the same rule as GameTileGrid. Seen in game: in an unlit pocket, place refused a torch
    /// at the player's own feet as "not visible".</summary>
    public static bool Touching(Player player, int x, int y)
    {
        var p = player.position;
        int left = (int)Math.Floor(p.X / 16f) - 1, right = (int)Math.Floor((p.X + player.width - 1) / 16f) + 1;
        int top = (int)Math.Floor(p.Y / 16f) - 1, bottom = (int)Math.Floor((p.Y + player.height - 1) / 16f) + 1;
        return x >= left && x <= right && y >= top && y <= bottom;
    }

    public static bool IsEntityVisible(Entity e) => IsEntityVisible(e, Screen, Main.LocalPlayer);

    public static bool IsEntityVisible(Entity e, ScreenRect screen, Player player)
    {
        if (!e.active) return false;
        int tx = (int)(e.Center.X / 16f), ty = (int)(e.Center.Y / 16f);
        if (!screen.Contains(tx, ty) || !WorldGen.InWorld(tx, ty)) return false;
        if (Lighting.Brightness(tx, ty) > MinBrightness) return true;
        return e is NPC && player.detectCreature;
    }
}
