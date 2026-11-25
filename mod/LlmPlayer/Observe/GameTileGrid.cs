using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Observe;
using LlmPlayer.Safety;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Observe;

/// <summary>
/// <see cref="ITileGrid"/> over the live world (<c>Main.tile</c>). Main thread only.
/// <list type="bullet">
/// <item>Out of world → <see cref="TileInfo.Unbreakable"/>; not visible (fair vision) → Unknown.</item>
/// <item>Actuated tiles and non-solid tiles (grass plants, torches, vines…) → Air, unless a platform.</item>
/// <item>Trees 'T', chests/containers 'C', crafting stations 'W', other multi-tile objects 'F' (their Kind is
/// Platform if you can stand on them (solidTop), else Solid or Air); ores get lowercase letters from
/// <see cref="OreGlyphs"/> with Name = the localized tile name.</item>
/// <item>Hazard: tiles that hurt on touch (<c>TileID.Sets.TouchDamageImmediate</c> &gt; 0: spikes, wooden spikes,
/// thorns, cactus…). Hot tiles (hellstone, meteorite) aren't hazards; they only hurt without fire immunity.</item>
/// <item>Liquid at least half full in a non-solid cell → Water / Lava / Honey. Shimmer → Hazard (it drops you
/// through blocks).</item>
/// <item>PickPowerRequired: vanilla <see cref="PickPowerTable"/>, modded <c>ModTile.MinPick</c>; hammer-only
/// tiles → int.MaxValue. Protected: <see cref="Protection.IsProtected"/>.</item>
/// </list>
/// </summary>
public sealed class GameTileGrid : ITileGrid
{
    private readonly bool _fairVision;
    private readonly Player _player;
    private readonly ScreenRect _screen;
    private readonly PickContext _pick;

    /// <param name="player">Whose eyes (buffs) to use; defaults to the local player.</param>
    /// <param name="fairVision">False only for safety checks that must see the real world (never for the LLM's view).</param>
    public GameTileGrid(Player? player = null, bool fairVision = true)
    {
        _player = player ?? Main.LocalPlayer;
        _fairVision = fairVision;
        _screen = Visibility.Screen; // snapshot: one grid is used within one frame
        _pick = new PickContext(Main.worldSurface, Main.UnderworldLayer, Main.maxTilesX);
    }

    public TileInfo Get(int x, int y)
    {
        if (!WorldGen.InWorld(x, y)) return TileInfo.Unbreakable;
        bool prot = Protection.IsProtected(x, y);
        if (_fairVision && !Visibility.IsTileVisible(x, y, _screen, _player))
            return new TileInfo(TileKind.Unknown, Protected: prot);

        var tile = Main.tile[x, y];
        if (tile.HasTile && !tile.IsActuated)
        {
            int type = tile.TileType;
            if (TileClass.IsTree(type)) return new TileInfo(TileKind.Air, 0, prot, 'T', TileClass.Name(type));

            char glyph = TileClass.IsContainer(type) ? 'C'
                : TileClass.IsStation(type) ? 'W'
                : TileClass.IsMultiTileObject(type) ? 'F'
                : '\0';
            if (glyph != '\0')
            {
                var kind = Main.tileSolidTop[type] ? TileKind.Platform : Main.tileSolid[type] ? TileKind.Solid : TileKind.Air;
                return new TileInfo(kind, PickPower(type, x, y, tile), prot, glyph, TileClass.Name(type));
            }

            if (TileID.Sets.TouchDamageImmediate[type] > 0)
                return new TileInfo(TileKind.Hazard, PickPower(type, x, y, tile), prot);

            if (TileID.Sets.Platforms[type] || (Main.tileSolidTop[type] && !Main.tileSolid[type]))
                return new TileInfo(TileKind.Platform, PickPower(type, x, y, tile), prot);

            if (Main.tileSolid[type] && !Main.tileSolidTop[type])
            {
                return TileClass.IsOre(type)
                    ? new TileInfo(TileKind.Solid, PickPower(type, x, y, tile), prot, OreGlyphs.For(type), TileClass.Name(type))
                    : new TileInfo(TileKind.Solid, PickPower(type, x, y, tile), prot);
            }
            // Non-solid decoration (plants, torches, vines): passable; falls through to the liquid check.
        }

        if (tile.LiquidAmount >= 128)
        {
            return tile.LiquidType switch
            {
                LiquidID.Lava => new TileInfo(TileKind.Lava, Protected: prot),
                LiquidID.Honey => new TileInfo(TileKind.Honey, Protected: prot),
                LiquidID.Shimmer => new TileInfo(TileKind.Hazard, Protected: prot, Name: "shimmer"),
                _ => new TileInfo(TileKind.Water, Protected: prot),
            };
        }
        return prot ? new TileInfo(TileKind.Air, Protected: true) : TileInfo.Air;
    }

    private int PickPower(int type, int x, int y, Tile tile)
    {
        if (Main.tileHammer[type] || Main.tileAxe[type]) return int.MaxValue; // not breakable by a pickaxe
        var modTile = TileLoader.GetTile(type);
        if (modTile != null) return modTile.MinPick;
        return PickPowerTable.Required(type, x, y, _pick, Main.tileDungeon[type], tile.TileFrameY);
    }
}
