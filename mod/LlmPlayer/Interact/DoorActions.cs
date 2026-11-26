using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Interact;

/// <summary>
/// Open/close a door the way a right-click does: the door branches of vanilla Player.TileInteractionsUse
/// (decompiled 1.4.4), only within <c>Player.IsInTileInteractionRange(…, TileReachCheckSettings.Simple)</c>.
/// <list type="bullet">
/// <item>Open: <c>WorldGen.OpenDoor(x, y, player.direction)</c> (swings away from a player facing it), else the
/// other way; net message 19 when it changed. Locked doors (Lihzahrd) are left alone: vanilla would spend a
/// Temple Key.</item>
/// <item>Close: <c>WorldGen.CloseDoor(x, y)</c>, which refuses while anyone stands in the door's column.</item>
/// </list>
/// Main thread only. Return true when the door changed state.
/// </summary>
public static class DoorActions
{
    public static bool InReach(Player p, int x, int y) => p.IsInTileInteractionRange(x, y, TileReachCheckSettings.Simple);

    public static bool TryOpen(Player p, int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2) || !InReach(p, x, y)) return false;
        var tile = Main.tile[x, y];
        if (!tile.HasTile || !TileLoader.IsClosedDoor(tile) || WorldGen.IsLockedDoor(x, y)) return false;
        int type = tile.TileType;
        foreach (int dir in new[] { p.direction, -p.direction })
        {
            WorldGen.OpenDoor(x, y, dir);
            if (Main.tile[x, y].TileType != type)
            {
                NetMessage.SendData(MessageID.ToggleDoorState, -1, -1, null, 0, x, y, dir);
                return true;
            }
        }
        return false;
    }

    public static bool TryClose(Player p, int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2) || !InReach(p, x, y)) return false;
        var tile = Main.tile[x, y];
        if (!tile.HasTile || TileLoader.CloseDoorID(tile) < 0) return false;
        if (!WorldGen.CloseDoor(x, y)) return false;
        NetMessage.SendData(MessageID.ToggleDoorState, -1, -1, null, 1, x, y, p.direction);
        return true;
    }
}
