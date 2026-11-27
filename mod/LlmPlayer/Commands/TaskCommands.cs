using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Grid;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;

namespace LlmPlayer.Commands;

/// <summary>move_to, explore, cancel_task (actions) and get_task (observation).</summary>
[CommandGroup]
public static class TaskCommands
{
    public static void Register()
    {
        CommandRouter.Register("move_to", p =>
        {
            bool dig = Params.Bool(p, "allow_dig", true), build = Params.Bool(p, "allow_build", true), torches = Params.Bool(p, "torches", true);
            int reserve = Params.Has(p, "platform_reserve") ? Params.Int(p, "platform_reserve") : MoveToTask.DefaultPlatformReserve;
            if (reserve < 0 || reserve > 9999) throw new CommandError(ErrorCodes.BadParams, "platform_reserve must be 0..9999");
            MoveToTask task;
            if (Params.Has(p, "entity_id"))
            {
                int id = Params.Int(p, "entity_id");
                if (!EntityReader.TryResolve(id, out var e))
                    throw new CommandError(ErrorCodes.BadParams, $"no entity {id} (ids come from get_entities)");
                if (!Visibility.IsEntityVisible(e))
                    throw new CommandError(ErrorCodes.NotVisible, $"entity {id} is not visible");
                task = new MoveToTask(id, dig, build) { Torches = torches, PlatformReserve = reserve };
            }
            else
            {
                if (!Params.Has(p, "x") || !Params.Has(p, "y"))
                    throw new CommandError(ErrorCodes.BadParams, "give x and y, or entity_id");
                int x = Params.Int(p, "x"), y = Params.Int(p, "y");
                if (!WorldGen.InWorld(x, y, 10))
                    throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
                task = new MoveToTask(new TilePoint(x, y), dig, build) { Torches = torches, PlatformReserve = reserve };
            }
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name };
        }, isAction: true);

        CommandRouter.Register("explore", p =>
        {
            var dir = Params.String(p, "direction").Trim().ToLowerInvariant() switch
            {
                "left" => ExploreDirection.Left,
                "right" => ExploreDirection.Right,
                "down" => ExploreDirection.Down,
                "up" => ExploreDirection.Up,
                var d => throw new CommandError(ErrorCodes.BadParams, $"direction must be left, right, down or up (got '{d}')"),
            };
            int distance = Params.Int(p, "distance", 50);
            if (distance < 1 || distance > 500) throw new CommandError(ErrorCodes.BadParams, "distance must be 1..500");
            var task = new ExploreTask(dir, distance, Params.Bool(p, "allow_dig", true), Params.Bool(p, "torches", true),
                Params.Bool(p, "surface", false));
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name };
        }, isAction: true);

        CommandRouter.Register("cancel_task", _ =>
        {
            var name = TaskHost.Cancel("cancelled");
            return new { Cancelled = name != null, Task = name };
        }, isAction: true);

        CommandRouter.Register("get_task", _ => new
        {
            Current = TaskHost.Runner.Current,
            LastFinished = TaskHost.Runner.LastFinished,
        }, isAction: false);
    }
}
