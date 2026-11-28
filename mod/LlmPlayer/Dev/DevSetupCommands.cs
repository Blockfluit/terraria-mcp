using System.Text.Json;
using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Control;
using LlmPlayer.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Dev;

/// <summary>
/// TEST-instance-only dev commands (docs/FAST-DEV.md). Each one calls <see cref="FastDevSystem.Require"/> first, so the
/// user's real game (instance 0) refuses them with not_test_instance even with DevCommands on. Not MCP tools.
/// <list type="bullet">
/// <item><c>dev_timescale {factor: 1..8|"max"}</c>: game updates per 1/60 s of wall time.</item>
/// <item><c>dev_render {mode: "full"|"minimal"|"off"}</c>: how much is drawn.</item>
/// <item><c>dev_setup {...}</c> (see <see cref="DevSetup"/>): build a test scene. This is cheating by design
/// (teleport, items, tiles, spawns), which is exactly why it only exists in throwaway test worlds.</item>
/// </list>
/// </summary>
[CommandGroup]
public static class DevSetupCommands
{
    public static void Register()
    {
        CommandRouter.Register("dev_timescale", p =>
        {
            if (!Params.Has(p, "factor") || !FastDev.TryTimeScale(p.GetProperty("factor"), out var f))
                throw new CommandError(ErrorCodes.BadParams, $"factor must be 1..{FastDev.MaxFactor} or \"max\"");
            FastDevSystem.SetTimeScale(f);
            return FastDevSystem.Status();
        }, isAction: false, requiresWorld: false);

        CommandRouter.Register("dev_render", p =>
        {
            if (!FastDev.TryRender(Params.String(p, "mode"), out var m))
                throw new CommandError(ErrorCodes.BadParams, "mode must be full, minimal or off");
            FastDevSystem.SetRender(m);
            return FastDevSystem.Status();
        }, isAction: false, requiresWorld: false);

        CommandRouter.Register("dev_setup", Setup, isAction: false);
    }

    private static object Setup(JsonElement p)
    {
        FastDevSystem.Require();
        var pl = Main.LocalPlayer;
        if (pl.dead) throw new CommandError(ErrorCodes.BadParams, "the player is dead; wait for the respawn");
        // Parse everything before changing anything.
        var tiles = DevSetup.ParseTiles(p);
        var items = DevSetup.ParseItems(p);
        var spawns = DevSetup.ParseSpawns(p);
        bool? day = null;
        double time = 0;
        if (Params.Has(p, "time"))
        {
            if (!DevSetup.TryParseTime(Params.String(p, "time"), out var d, out time))
                throw new CommandError(ErrorCodes.BadParams, "time must be dawn, noon, dusk, midnight or H:MM");
            day = d;
        }
        var itemTypes = items.Select(i => (Spec: i, Type: ItemType(i.Name))).ToList();
        var npcTypes = spawns.Select(s => (Spec: s, Type: NpcType(s.Npc))).ToList();
        var tileTypes = tiles.Select(t => t.Kind == TileOpKind.Tile ? TileType(t.Name!) : -1).ToList();
        bool teleport = Params.Has(p, "x") || Params.Has(p, "y");
        int tx = 0, ty = 0;
        if (teleport)
        {
            tx = Params.Int(p, "x");
            ty = Params.Int(p, "y");
            if (!WorldGen.InWorld(tx, ty, 20)) throw new CommandError(ErrorCodes.BadParams, $"({tx},{ty}) is outside the world");
        }
        foreach (var t in tiles)
            if (!WorldGen.InWorld(t.X1, t.Y1, 10) || !WorldGen.InWorld(t.X2, t.Y2, 10))
                throw new CommandError(ErrorCodes.BadParams, $"place_tiles ({t.X1},{t.Y1})-({t.X2},{t.Y2}) leaves the world");

        var cancelled = TaskHost.Cancel("dev_setup");

        int changed = 0;
        var failed = new List<string>();
        for (int i = 0; i < tiles.Count; i++) changed += Apply(tiles[i], tileTypes[i], failed);

        int cleared = 0;
        if (Params.Bool(p, "clear_hostiles", false))
        {
            foreach (var n in Main.npc)
                if (n.active && !n.townNPC && !n.friendly && !NPCID.Sets.CountsAsCritter[n.type])
                {
                    n.active = false;
                    cleared++;
                }
            foreach (var pr in Main.projectile)
                if (pr.active && pr.hostile) pr.active = false;
        }
        if (Params.Bool(p, "clear_items", false))
            foreach (var it in Main.item)
                it.active = false;

        if (teleport)
        {
            pl.position = new Vector2(tx * 16f, (ty + 1) * 16f - pl.height);
            pl.velocity = Vector2.Zero;
            pl.fallStart = pl.fallStart2 = (int)(pl.position.Y / 16f);
            pl.immuneTime = Math.Max(pl.immuneTime, 30);
        }

        var given = new List<object>();
        if (Params.Bool(p, "clear_inventory", false))
            for (int i = 0; i < 58; i++)
                pl.inventory[i].TurnToAir();
        foreach (var (spec, type) in itemTypes) given.Add(Give(pl, type, spec.Count));

        if (Params.Has(p, "life"))
            pl.statLife = Math.Clamp(Params.Int(p, "life"), 1, pl.statLifeMax2);
        if (Params.Bool(p, "clear_buffs", false))
            for (int i = 0; i < pl.buffType.Length; i++)
                if (pl.buffType[i] > 0) pl.DelBuff(i--);

        if (day is { } dd)
        {
            Main.dayTime = dd;
            Main.time = time;
        }

        var spawned = new List<object>();
        foreach (var (spec, type) in npcTypes)
        {
            int id = NPC.NewNPC(new Terraria.DataStructures.EntitySource_Misc("LlmPlayer dev_setup"), spec.X * 16 + 8, (spec.Y + 1) * 16, type);
            spawned.Add(new { Npc = Lang.GetNPCNameValue(type), Id = id < Main.maxNPCs ? id : -1 });
        }

        var (sx, sy) = LlmPlayer.Observe.StateReader.StandingTile(pl);
        return new
        {
            Cancelled = cancelled,
            TilesChanged = changed,
            TilesFailed = failed.Count == 0 ? null : failed.Take(10).ToList(),
            HostilesCleared = cleared,
            Items = given,
            Spawned = spawned,
            Position = new { X = sx, Y = sy },
            Life = pl.statLife,
        };
    }

    private static int Apply(TileOp op, int tileType, List<string> failed)
    {
        int n = 0;
        for (int x = op.X1; x <= op.X2; x++)
        for (int y = op.Y1; y <= op.Y2; y++)
        {
            var t = Main.tile[x, y];
            switch (op.Kind)
            {
                case TileOpKind.Clear:
                    t.ClearEverything();
                    n++;
                    break;
                case TileOpKind.Liquid:
                    if (t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType]) WorldGen.KillTile(x, y, noItem: true);
                    t.LiquidType = op.Name switch { "lava" => LiquidID.Lava, "honey" => LiquidID.Honey, "shimmer" => LiquidID.Shimmer, _ => LiquidID.Water };
                    t.LiquidAmount = 255;
                    Liquid.AddWater(x, y);
                    n++;
                    break;
                case TileOpKind.Tile when tileType == TileID.Heart:
                    // Life Crystal: a 2x2 object, (x,y) = its bottom-left tile. Set the four tiles' frames directly
                    // (deterministic; WorldGen's placement rules for it are world-gen rules).
                    for (int dx = 0; dx < 2; dx++)
                    for (int dy = 0; dy < 2; dy++)
                    {
                        var h = Main.tile[x + dx, y - 1 + dy];
                        h.ClearTile();
                        h.HasTile = true;
                        h.TileType = TileID.Heart;
                        h.TileFrameX = (short)(dx * 18);
                        h.TileFrameY = (short)(dy * 18);
                    }
                    n += 4;
                    break;
                default:
                    if (t.HasTile && t.TileType != tileType) WorldGen.KillTile(x, y, noItem: true);
                    if (!t.HasTile) WorldGen.PlaceTile(x, y, tileType, mute: true, forced: true);
                    if (Main.tile[x, y].HasTile) n++;
                    else failed.Add($"{op.Name} at ({x},{y})");
                    break;
            }
        }
        WorldGen.RangeFrame(op.X1 - 1, op.Y1 - 1, op.X2 + 1, op.Y2 + 1);
        return n;
    }

    /// <summary>Ammo (arrows, bullets, gel…) into the ammo slots 54-57 when one is free, everything else into 0-49.</summary>
    private static object Give(Player pl, int type, int count)
    {
        var item = new Item(type, count);
        bool ammo = item.ammo != AmmoID.None && item.createTile < 0 && item.useStyle == ItemUseStyleID.None;
        int slot = -1;
        if (ammo)
            for (int i = 54; i < 58 && slot < 0; i++)
                if (pl.inventory[i].IsAir) slot = i;
        for (int i = 0; i < 50 && slot < 0; i++)
            if (pl.inventory[i].IsAir) slot = i;
        if (slot < 0) throw new CommandError("inventory_full", $"no free slot for {item.Name}");
        pl.inventory[slot] = item;
        return new { item.Name, Count = item.stack, Slot = slot };
    }

    private static int ItemType(string name)
    {
        if (ItemID.Search.TryGetId(name.Replace(" ", ""), out int id)) return id;
        for (int i = 1; i < ItemLoader.ItemCount; i++)
            if (string.Equals(Lang.GetItemNameValue(i), name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new CommandError(ErrorCodes.BadParams, $"no item named '{name}'");
    }

    private static int NpcType(string name)
    {
        if (NPCID.Search.TryGetId(name.Replace(" ", ""), out int id)) return id;
        for (int i = 1; i < NPCLoader.NPCCount; i++)
            if (string.Equals(Lang.GetNPCNameValue(i), name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new CommandError(ErrorCodes.BadParams, $"no NPC named '{name}'");
    }

    /// <summary>Internal tile names (TileID: "Stone", "Dirt", "Platforms", "Heart" = Life Crystal, "PressurePlates", "Torches", "WoodBlock").
/// Heart is placed once per rectangle tile as its bottom-left corner: give it a single point.</summary>
    private static int TileType(string name)
    {
        if (TileID.Search.TryGetId(name.Replace(" ", ""), out int id)) return id;
        if (string.Equals(name, "Life Crystal", StringComparison.OrdinalIgnoreCase)) return TileID.Heart;
        throw new CommandError(ErrorCodes.BadParams, $"no tile named '{name}' (use TileID names, e.g. Stone, Dirt, Platforms, Heart, PressurePlates)");
    }
}
