namespace LlmPlayer.Core.Grid;

/// <summary>World facts the vanilla pickaxe gates depend on.</summary>
public readonly record struct PickContext(double WorldSurface, int UnderworldLayer, int MaxTilesX);

/// <summary>
/// Vanilla minimum pickaxe power per tile type, mirroring the zero-damage gates in
/// tModLoader 1.4.4 <c>Player.GetPickaxeDamage</c> (decompiled and checked). Tiles not listed need 0.
/// Modded tiles use <c>ModTile.MinPick</c> instead (the mod side handles that).
/// Lihzahrd bricks need 210 power. Vanilla has no Plantera check on the brick itself: the only
/// 210-power pick (Picksaw) drops from Golem, behind the temple, so the power gate is what keeps them safe.
/// </summary>
public static class PickPowerTable
{
    public const int Dirt = 0, Stone = 1;
    public const int Iron = 6, Copper = 7, Gold = 8, Silver = 9;
    public const int Demonite = 22, Ebonstone = 25, Meteorite = 37, Obsidian = 56, Hellstone = 58;
    public const int Hellforge = 77, Cobalt = 107, Mythril = 108, Adamantite = 111, Pearlstone = 117;
    public const int Traps = 137, Crimstone = 203, Crimtane = 204, Chlorophyte = 211;
    public const int Palladium = 221, Orichalcum = 222, Titanium = 223, LihzahrdBrick = 226, LihzahrdAltar = 237;

    /// <param name="isDungeonTile">Main.tileDungeon[type] (dungeon bricks).</param>
    /// <param name="frameY">The tile's frameY; only matters for traps (137), where frames 18..72 are lihzahrd traps.</param>
    public static int Required(int type, int x, int y, PickContext ctx, bool isDungeonTile = false, int frameY = 0)
    {
        if (type == Chlorophyte) return 200;
        switch (type)
        {
            case Ebonstone or Crimstone or Pearlstone: return 65;
            case Meteorite: return 50;
            case Demonite or Crimtane: return y > ctx.WorldSurface ? 55 : 0;
            case Obsidian: return 55;
            case Hellforge: return y >= ctx.UnderworldLayer ? 65 : 0;
            case Hellstone: return 65;
            case LihzahrdBrick or LihzahrdAltar: return 210;
            case Traps: return (uint)(frameY / 18 - 1) <= 3u ? 210 : 0;
        }
        if (isDungeonTile)
            return y > ctx.WorldSurface && (x < ctx.MaxTilesX * 0.35 || x > ctx.MaxTilesX * 0.65) ? 100 : 0;
        return type switch
        {
            Cobalt or Palladium => 100,
            Mythril or Orichalcum => 110,
            Adamantite or Titanium => 150,
            _ => 0,
        };
    }

    /// <summary>True if a pick of <paramref name="pickPower"/> can break a tile needing <paramref name="required"/>.</summary>
    public static bool CanMine(int required, int pickPower) => required != int.MaxValue && pickPower >= required;
}
