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
/// <item>Closed doors 'D': Solid and protected, with <see cref="TileInfo.Door"/> set unless locked, so the pathfinder
/// can plan to open them (an open door is an 'F' object you can walk through).</item>
/// <item>Trees 'T', chests/containers 'C', crafting stations 'W', other multi-tile objects 'F' (their Kind is
/// Platform if you can stand on them (solidTop), else Solid or Air); ores get lowercase letters from
/// <see cref="OreGlyphs"/> with Name = the localized tile name.</item>
/// <item>Hazard: tiles that hurt on touch (<c>TileID.Sets.TouchDamageImmediate</c> &gt; 0: spikes, wooden spikes,
/// thorns, cactus…). Hot tiles (hellstone, meteorite) aren't hazards; they only hurt without fire immunity.</item>
/// <item>Liquid at least half full in a non-solid cell → Water / Lava / Honey. Shimmer → Hazard (it drops you
/// through blocks).</item>
/// <item>PickPowerRequired: vanilla <see cref="PickPowerTable"/>, modded <c>ModTile.MinPick</c>; hammer-only
/// tiles → int.MaxValue. Protected: <see cref="Protection.IsProtected"/>, or locked right now
/// (<c>WorldGen.CanKillTile</c> false, e.g. grass holding up a tree).</item>
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
        // (Touching reads _player's live position; one grid is used within one frame.)
        _fairVision = fairVision;
        _screen = Visibility.Screen; // snapshot: one grid is used within one frame
        _pick = new PickContext(Main.worldSurface, Main.UnderworldLayer, Main.maxTilesX);
    }

    /// <summary>
    /// Tiles within one tile of the player's hitbox count as known even in the dark: the player feels what it stands
    /// on and bumps into. Seen in game: standing on an unlit cave floor, the pathfinder planned a fall through the
    /// "unknown" floor and got stuck replanning it.
    /// </summary>
    private bool Touching(int x, int y)
    {
        var p = _player.position;
        int left = (int)Math.Floor(p.X / 16f) - 1, right = (int)Math.Floor((p.X + _player.width - 1) / 16f) + 1;
        int top = (int)Math.Floor(p.Y / 16f) - 1, bottom = (int)Math.Floor((p.Y + _player.height - 1) / 16f) + 1;
        return x >= left && x <= right && y >= top && y <= bottom;
    }

    /// <summary>The tile with its background-wall flag (only for tiles the player can see: Unknown stays bare).</summary>
    public TileInfo Get(int x, int y)
    {
        var info = GetCore(x, y);
        if (info.Kind != TileKind.Unknown && WorldGen.InWorld(x, y) && Main.tile[x, y].WallType != 0) info = info with { Wall = true };
        return info;
    }

    private TileInfo GetCore(int x, int y)
    {
        if (!WorldGen.InWorld(x, y)) return TileInfo.Unbreakable;
        bool prot = Protection.IsProtected(x, y);
        if (_fairVision && !Touching(x, y) && !Visibility.IsTileVisible(x, y, _screen, _player))
            return new TileInfo(TileKind.Unknown, Protected: prot);

        var tile = Main.tile[x, y];
        if (tile.HasTile && !tile.IsActuated)
        {
            int type = tile.TileType;
            // Locked tiles (vanilla CanKillTile: e.g. the grass under a tree, a block holding up furniture) can't be
            // broken right now: treat them as protected so neither the pathfinder nor explore's digging plans on
            // breaking them (seen in game: explore swung at the grass under a tree root for 15 s).
            if (!prot && !Main.tileAxe[type] && !WorldGen.CanKillTile(x, y)) prot = true;
            if (TileClass.IsTree(type)) return new TileInfo(TileKind.Air, 0, prot, 'T', TileClass.Name(type));
            if (Main.tileSolid[type] && TileLoader.IsClosedDoor(type))
                return new TileInfo(TileKind.Solid, PickPower(type, x, y, tile), prot, 'D', TileClass.Name(type),
                    Door: !WorldGen.IsLockedDoor(x, y));

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
