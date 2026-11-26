using System.Text.Json;
using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Interact;
using LlmPlayer.Interact;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;

namespace LlmPlayer.Commands;

/// <summary>
/// Containers and beds. open_chest and use_bed start tasks (walk within reach, then right-click like vanilla);
/// chest_deposit, chest_withdraw and close_chest are instant actions on the container that is open
/// (<see cref="ChestAccess"/>). Contents are only reported once a container is open (fair vision: a player sees
/// them only then).
/// </summary>
[CommandGroup]
public static class ChestCommands
{
    public static void Register()
    {
        CommandRouter.Register("open_chest", p =>
        {
            var pl = Main.LocalPlayer;
            var c = Params.Has(p, "x") || Params.Has(p, "y") ? ContainerAt(p) : NearestContainer(pl);
            if (ChestAccess.Refusal(pl, c) is { } why) throw new CommandError(ErrorCodes.BadParams, why);
            var task = new OpenChestTask(c);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Chest = new { c.X, c.Y, Name = ChestAccess.NameOf(c) } };
        }, isAction: true);

        CommandRouter.Register("close_chest", _ =>
        {
            var pl = Main.LocalPlayer;
            var info = ChestAccess.OpenInfo(pl);
            return new { Closed = ChestAccess.Close(pl), Chest = info };
        }, isAction: true);

        CommandRouter.Register("chest_deposit", p => Transfer(p, deposit: true), isAction: true);
        CommandRouter.Register("chest_withdraw", p => Transfer(p, deposit: false), isAction: true);

        CommandRouter.Register("use_bed", p =>
        {
            var pl = Main.LocalPlayer;
            BedRef bed;
            if (Params.Has(p, "x") || Params.Has(p, "y"))
            {
                var (x, y) = Coords(p);
                bed = BedAccess.Resolve(x, y) ?? throw new CommandError(ErrorCodes.BadParams, $"no bed at ({x},{y})");
            }
            else
            {
                var hit = Finder.Find(pl, "bed", null).FirstOrDefault()
                          ?? throw new CommandError(ErrorCodes.BadParams, "no visible bed nearby (give x,y or explore)");
                bed = BedAccess.Resolve(hit.X, hit.Y) ?? throw new CommandError(ErrorCodes.BadParams, $"no bed at ({hit.X},{hit.Y})");
            }
            var task = new UseBedTask(bed);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Bed = new { bed.X, bed.Y } };
        }, isAction: true);
    }

    private static (int X, int Y) Coords(JsonElement p)
    {
        if (!Params.Has(p, "x") || !Params.Has(p, "y")) throw new CommandError(ErrorCodes.BadParams, "give both x and y, or neither");
        int x = Params.Int(p, "x"), y = Params.Int(p, "y");
        if (!WorldGen.InWorld(x, y, 10)) throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
        if (!Visibility.IsTileVisible(x, y)) throw new CommandError(ErrorCodes.NotVisible, $"({x},{y}) is not visible");
        return (x, y);
    }

    private static ContainerRef ContainerAt(JsonElement p)
    {
        var (x, y) = Coords(p);
        return ChestAccess.Resolve(x, y) ?? throw new CommandError(ErrorCodes.BadParams, $"no chest or other container at ({x},{y})");
    }

    private static ContainerRef NearestContainer(Player pl)
    {
        foreach (var hit in Finder.Find(pl, "chest", null))
            if (ChestAccess.Resolve(hit.X, hit.Y) is { } c && ChestAccess.Refusal(pl, c) == null) return c;
        throw new CommandError(ErrorCodes.BadParams, "no visible chest you can open nearby (give x,y or explore)");
    }

    /// <summary>
    /// chest_deposit {item, count?} | {all: true} | {quick_stack: true}; chest_withdraw {item, count?} | {all: true}.
    /// Returns {moved: [{name, count}], chest: {x, y, name}, items: [{slot, name, stack}]} (moved counts are what left
    /// the inventory for a deposit, what arrived for a withdrawal).
    /// </summary>
    private static object Transfer(JsonElement p, bool deposit)
    {
        var pl = Main.LocalPlayer;
        var items = ChestAccess.OpenItems(pl) ?? throw new CommandError(ErrorCodes.BadParams, "no chest is open (use open_chest first)");
        bool all = Params.Bool(p, "all", false), quick = deposit && Params.Bool(p, "quick_stack", false);
        bool hasItem = Params.Has(p, "item");
        if ((all ? 1 : 0) + (quick ? 1 : 0) + (hasItem ? 1 : 0) != 1)
            throw new CommandError(ErrorCodes.BadParams, deposit ? "give exactly one of item, all: true or quick_stack: true" : "give exactly one of item or all: true");
        int? count = null;
        if (Params.Has(p, "count"))
        {
            if (!hasItem) throw new CommandError(ErrorCodes.BadParams, "count only goes with item");
            count = Params.Int(p, "count");
            if (count < 1) throw new CommandError(ErrorCodes.BadParams, "count must be at least 1");
        }

        var before = ChestAccess.Snapshot(items);
        if (hasItem)
        {
            string query = Params.String(p, "item");
            string? error;
            if (deposit) ChestAccess.DepositItem(pl, query, count, out error);
            else ChestAccess.WithdrawItem(pl, items, query, count, out error);
            if (error != null) throw new CommandError(error.StartsWith("no '") ? ErrorCodes.MissingItem : ErrorCodes.BadParams, error);
        }
        else if (deposit)
        {
            if (quick) ChestAccess.QuickStack(pl);
            else ChestAccess.DepositAll(pl);
        }
        else ChestAccess.LootAll();

        items = ChestAccess.OpenItems(pl) ?? items;
        var after = ChestAccess.Snapshot(items);
        var moved = InteractMath.Diff(before, after)
            .Select(d => new ItemDelta(d.Name, deposit ? d.Count : -d.Count))
            .Where(d => d.Count > 0)
            .ToList();
        return new { Moved = moved, Chest = ChestAccess.OpenInfo(pl), Items = after };
    }
}
