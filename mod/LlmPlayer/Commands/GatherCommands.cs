using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;

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

        CommandRouter.Register("mine_area", p =>
        {
            int x1 = Params.Int(p, "x1"), y1 = Params.Int(p, "y1"), x2 = Params.Int(p, "x2"), y2 = Params.Int(p, "y2");
            long count = MiningOrder.RectCount(x1, y1, x2, y2);
            if (count > MiningOrder.MaxAreaTiles)
                throw new CommandError(ErrorCodes.BadParams, $"area has {count} tiles; at most {MiningOrder.MaxAreaTiles}");
            var player = Main.LocalPlayer;
            var (px, py) = StateReader.StandingTile(player);
            var ordered = MiningOrder.Order(MiningOrder.Rect(x1, y1, x2, y2), new TilePoint(px, py));
            var tiles = MineTask.Validate(player, ordered, area: true, out int skipped);
            if (tiles.Count == 0)
                throw new CommandError(ErrorCodes.BadParams, $"nothing a pickaxe can mine in that area ({skipped} tiles are air, trees or hammer-only)");
            var task = new MineTask(tiles, "mine_area");
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Tiles = tiles.Count, Skipped = skipped };
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
