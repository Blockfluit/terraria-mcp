using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Commands;

/// <summary>mine, mine_area, chop_tree, pick_up_items (actions). Validation errors come back immediately; otherwise
/// the task starts and they return <c>{started:true, task}</c>.</summary>
[CommandGroup]
public static class GatherCommands
{
    public const int MaxPickupRadius = 40;

    public static void Register()
    {
        CommandRouter.Register("mine", p =>
        {
            var t = new TilePoint(Params.Int(p, "x"), Params.Int(p, "y"));
            var tiles = MineTask.Validate(Main.LocalPlayer, new[] { t }, area: false, out _);
            var task = new MineTask(tiles, "mine");
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name };
        }, isAction: true);

        // One ore vein (or `max` tiles of any block): visible tiles of the same type 8-connected to (x,y), nearest to
        // it first, without whatever surrounds them.
        CommandRouter.Register("mine_vein", p =>
        {
            int x = Params.Int(p, "x"), y = Params.Int(p, "y");
            var player = Main.LocalPlayer;
            if (!WorldGen.InWorld(x, y, 10)) throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
            if (!Visibility.IsTileVisible(x, y)) throw new CommandError(ErrorCodes.NotVisible, $"({x},{y}) is not visible");
            var start = Main.tile[x, y];
            if (!start.HasTile || !Main.tileSolid[start.TileType] || Main.tileAxe[start.TileType])
                throw new CommandError(ErrorCodes.BadParams, $"no block at ({x},{y}) (find category=ore or category=block)");
            ushort type = start.TileType;
            // Ore: the whole vein by default. Other blocks (stone, sand, clay…) form huge connected masses: then max
            // says how many tiles to take (e.g. 20 stone for a furnace).
            bool ore = TileID.Sets.Ore[type];
            int max = Params.Has(p, "max") ? Params.Int(p, "max") : ore ? MiningOrder.MaxAreaTiles : 20;
            if (max < 1 || max > MiningOrder.MaxAreaTiles)
                throw new CommandError(ErrorCodes.BadParams, $"max must be 1..{MiningOrder.MaxAreaTiles}");
            var vein = VeinMath.Collect((tx, ty) => WorldGen.InWorld(tx, ty, 10) && Visibility.IsTileVisible(tx, ty)
                && Main.tile[tx, ty] is { HasTile: true } t && t.TileType == type, new TilePoint(x, y), max);
            var (px, py) = StateReader.StandingTile(player);
            var ordered = MiningOrder.Order(vein, new TilePoint(px, py));
            var tiles = MineTask.Validate(player, ordered, area: true, out var skipped);
            if (tiles.Count == 0)
                throw new CommandError(ErrorCodes.BadParams, $"nothing in that vein can be mined (skipped: {skipped.Summary()})");
            var task = new MineTask(tiles, "mine_vein", skipped);
            TaskHost.Start(task);
            return new
            {
                Started = true, Task = task.Name, Ore = TileClass.Name(type), Tiles = tiles.Count,
                Skipped = skipped.Count, SkippedReasons = skipped.ByReason,
            };
        }, isAction: true);

        CommandRouter.Register("mine_area", p =>
        {
            int x1 = Params.Int(p, "x1"), y1 = Params.Int(p, "y1"), x2 = Params.Int(p, "x2"), y2 = Params.Int(p, "y2");
            long count = MiningOrder.RectCount(x1, y1, x2, y2);
            if (count > MiningOrder.MaxAreaTiles)
                throw new CommandError(ErrorCodes.BadParams, $"area has {count} tiles; at most {MiningOrder.MaxAreaTiles}");
            var player = Main.LocalPlayer;
            var (px, py) = StateReader.StandingTile(player);
            var ordered = MiningOrder.Order(MiningOrder.Rect(x1, y1, x2, y2), new TilePoint(px, py));
            var tiles = MineTask.Validate(player, ordered, area: true, out var skipped);
            if (tiles.Count == 0)
                throw new CommandError(ErrorCodes.BadParams, $"nothing a pickaxe can mine in that area (skipped: {skipped.Summary()})");
            var task = new MineTask(tiles, "mine_area", skipped);
            TaskHost.Start(task);
            return new
            {
                Started = true, Task = task.Name, Tiles = tiles.Count,
                Skipped = skipped.Count, SkippedReasons = skipped.ByReason, SkippedTiles = skipped.Examples,
            };
        }, isAction: true);

        CommandRouter.Register("chop_tree", p =>
        {
            bool hasX = Params.Has(p, "x"), hasY = Params.Has(p, "y");
            if (hasX != hasY) throw new CommandError(ErrorCodes.BadParams, "give both x and y, or neither (nearest tree)");
            TilePoint? at = hasX ? new TilePoint(Params.Int(p, "x"), Params.Int(p, "y")) : null;
            var trunk = ChopTreeTask.ResolveBase(Main.LocalPlayer, at);
            var task = new ChopTreeTask(trunk);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Tree = new { trunk.X, trunk.Y } };
        }, isAction: true);

        CommandRouter.Register("pick_up_items", p =>
        {
            int radius = Params.Int(p, "radius", 10);
            if (radius < 1 || radius > MaxPickupRadius)
                throw new CommandError(ErrorCodes.BadParams, $"radius must be 1..{MaxPickupRadius}");
            var task = new PickUpTask(radius);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name };
        }, isAction: true);
    }
}
