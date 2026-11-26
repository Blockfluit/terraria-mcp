using LlmPlayer.Core.Interact;
using LlmPlayer.Observe;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace LlmPlayer.Interact;

/// <summary>A bed in the world: its top-left tile (4x2) and type.</summary>
public readonly record struct BedRef(int X, int Y, int Type);

/// <summary>
/// Set the spawn point at a bed through vanilla's bed right-click (Player.TileInteractionsUse, bed branch,
/// decompiled 1.4.4): clicking the foot half (<c>Player.IsHoveringOverABottomSideOfABed</c>) runs
/// <c>Player.FindSpawn</c>, then <c>Player.CheckSpawn</c> (bed not obstructed + <c>WorldGen.StartRoomCheck</c>: valid
/// housing) and <c>Player.ChangeSpawn</c> with the "Spawn point set!" message. The head half would make the player
/// sleep instead, so the foot half is always used. Unlike a second vanilla click, this never removes an existing
/// spawn point at the same bed. Modded beds go through <c>TileLoader.RightClick</c> on a foot tile.
/// </summary>
public static class BedAccess
{
    public static bool IsBed(int type) => type == TileID.Beds || (type >= TileID.Count && TileID.Sets.CanBeSleptIn[type]);

    public static BedRef? Resolve(int x, int y)
    {
        if (!WorldGen.InWorld(x, y)) return null;
        var t = Main.tile[x, y];
        if (!t.HasTile || !IsBed(t.TileType)) return null;
        var (ox, oy) = TileClass.ObjectOrigin(x, y, t);
        return new BedRef(ox, oy, t.TileType);
    }

    /// <summary>A foot-half tile of the bed within interaction range of the player, or null.</summary>
    public static (int X, int Y)? ClickTile(Player p, BedRef b)
    {
        for (int y = b.Y; y < b.Y + 2; y++)
            for (int x = b.X; x < b.X + 4; x++)
            {
                if (!WorldGen.InWorld(x, y)) continue;
                var t = Main.tile[x, y];
                if (!t.HasTile || t.TileType != b.Type || !InteractMath.IsBedFootSide(t.TileFrameX)) continue;
                if (p.IsInTileInteractionRange(x, y, TileReachCheckSettings.Simple)) return (x, y);
            }
        return null;
    }

    /// <summary>Right-click the bed's foot at (cx,cy). Returns (success, message): vanilla's text on success, or why
    /// the bed can't be a spawn point.</summary>
    public static (bool Ok, string Message, int SpawnX, int SpawnY) SetSpawn(Player p, int cx, int cy)
    {
        var t = Main.tile[cx, cy];
        if (t.TileType != TileID.Beds)
        {
            int beforeX = p.SpawnX, beforeY = p.SpawnY;
            TileLoader.RightClick(cx, cy);
            p.FindSpawn();
            bool changed = p.SpawnX != beforeX || p.SpawnY != beforeY;
            return changed
                ? (true, Language.GetTextValue("Game.SpawnPointSet"), p.SpawnX, p.SpawnY)
                : (false, "the bed refused (modded bed: invalid housing or obstructed)", p.SpawnX, p.SpawnY);
        }

        var (sx, sy) = InteractMath.BedSpawnPoint(cx, cy, t.TileFrameX, t.TileFrameY);
        p.FindSpawn();
        if (p.SpawnX == sx && p.SpawnY == sy) return (true, "this bed is already your spawn point", sx, sy);
        WorldGen.roomCheckFailureReason = TownNPCRoomCheckFailureReason.None;
        bool obstructed = Obstructed(sx, sy);
        if (!Player.CheckSpawn(sx, sy))
        {
            string why = obstructed ? Language.GetTextValue("Game.BedObstructed")
                : InteractMath.RoomFailureText(WorldGen.roomCheckFailureReason.ToString());
            return (false, why, sx, sy);
        }
        p.ChangeSpawn(sx, sy);
        string msg = Language.GetTextValue("Game.SpawnPointSet");
        Main.NewText(msg, byte.MaxValue, 240, 20);
        return (true, msg, sx, sy);
    }

    /// <summary>CheckSpawn_Internal's obstruction test (a solid, non-platform tile in the 3x3 above the spawn).</summary>
    private static bool Obstructed(int x, int y)
    {
        for (int i = x - 1; i <= x + 1; i++)
            for (int j = y - 3; j < y; j++)
            {
                if (!WorldGen.InWorld(i, j)) return false;
                var t = Main.tile[i, j];
                // CheckSpawn counts bubbles (379) as solid for this test.
                bool solid = t.HasTile && !t.IsActuated && (Main.tileSolid[t.TileType] || t.TileType == TileID.Bubble);
                if (solid && !Main.tileSolidTop[t.TileType]) return true;
            }
        return false;
    }
}
