using LlmPlayer.Core.Grid;
using LlmPlayer.Bridge;
using LlmPlayer.Config;
using LlmPlayer.Observe;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace LlmPlayer.Safety;

/// <summary>
/// Tiles the AI must never break: containers, crafting stations, doors and other frame-important
/// multi-tile objects (trees, pots, life crystals and life fruit excepted); every tile within
/// ProtectSpawnRadius of the world spawn and of the player's bed spawn; every tile in or bounding the
/// room of a housed town NPC. Rooms are recomputed by <see cref="Refresh"/> every 600 ticks.
/// </summary>
public static class Protection
{
    public const int RefreshTicks = 600;

    private static readonly List<Rectangle> Rooms = new();
    private static int _ticksUntilRefresh;

    public static IReadOnlyList<Rectangle> HouseRooms => Rooms;

    public static bool IsProtected(int x, int y)
    {
        if (!WorldGen.InWorld(x, y)) return true;
        var tile = Main.tile[x, y];
        if (tile.HasTile && TileClass.IsProtectedType(tile.TileType)) return true;
        if (SupportRules.SupportsProtectedObject(IsProtectedObject, x, y)) return true;
        return IsProtectedArea(x, y);
    }

    /// <summary>Location-based part only (spawn radii and NPC houses), ignoring what's on the tile.</summary>
    public static bool IsProtectedArea(int x, int y)
    {
        int r = LlmConfig.Current.ProtectSpawnRadius;
        if (r > 0)
        {
            if (Within(x, y, Main.spawnTileX, Main.spawnTileY, r)) return true;
            var p = Main.LocalPlayer;
            if (p != null && p.SpawnX >= 0 && p.SpawnY >= 0 && Within(x, y, p.SpawnX, p.SpawnY, r)) return true;
        }
        foreach (var room in Rooms)
            if (x >= room.Left && x < room.Right && y >= room.Top && y < room.Bottom) return true;
        return false;
    }

    private static bool Within(int x, int y, int cx, int cy, int r)
    {
        long dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= (long)r * r;
    }

    /// <summary>Recompute housed town NPC rooms (bounds expanded by 1 to include walls, floor and ceiling).</summary>
    public static void Refresh()
    {
        Rooms.Clear();
        foreach (var npc in Main.npc)
        {
            if (!npc.active || !npc.townNPC || npc.homeless) continue;
            if (!WorldGen.InWorld(npc.homeTileX, npc.homeTileY - 1)) continue;
            if (!WorldGen.StartRoomCheck(npc.homeTileX, npc.homeTileY - 1)) continue;
            int x1 = WorldGen.roomX1 - 1, y1 = WorldGen.roomY1 - 1, x2 = WorldGen.roomX2 + 1, y2 = WorldGen.roomY2 + 1;
            if (x2 < x1 || y2 < y1) continue;
            Rooms.Add(new Rectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1));
        }
    }

    internal static void Tick()
    {
        if (--_ticksUntilRefresh > 0) return;
        _ticksUntilRefresh = RefreshTicks;
        Refresh();
    }

    internal static void Reset()
    {
        Rooms.Clear();
        _ticksUntilRefresh = 0; // refresh on the first in-world tick
    }

    private static bool IsProtectedObject(int x, int y) =>
        WorldGen.InWorld(x, y) && Main.tile[x, y].HasTile && TileClass.IsProtectedType(Main.tile[x, y].TileType);
}

public sealed class ProtectionSystem : ModSystem
{
    public override void OnModLoad() => BridgeSystem.Tick += Protection.Tick;

    public override void OnWorldLoad()
    {
        Protection.Reset();
        TileClass.Reset();
    }

    public override void OnWorldUnload() => Protection.Reset();

    public override void Unload()
    {
        Protection.Reset();
        TileClass.Reset();
    }
}
