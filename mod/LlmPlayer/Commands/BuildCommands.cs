using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Build;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Commands;

/// <summary>build_platforms (action, task). See <see cref="BuildPlatformsTask"/>.</summary>
[CommandGroup]
public static class BuildCommands
{
    public static void Register()
    {
        CommandRouter.Register("build_platforms", p =>
        {
            int x1 = Params.Int(p, "x1"), x2 = Params.Int(p, "x2"), y = Params.Int(p, "y");
            int rows = Params.Int(p, "rows", 1), spacing = Params.Int(p, "spacing", 8);
            if (rows < 1 || rows > PlatformPlan.MaxRows)
                throw new CommandError(ErrorCodes.BadParams, $"rows must be 1..{PlatformPlan.MaxRows}");
            if (spacing < 1 || spacing > PlatformPlan.MaxSpacing)
                throw new CommandError(ErrorCodes.BadParams, $"spacing must be 1..{PlatformPlan.MaxSpacing}");
            long count = PlatformPlan.TileCount(x1, x2, rows);
            if (count > PlatformPlan.MaxTiles)
                throw new CommandError(ErrorCodes.BadParams, $"that is {count} tiles; at most {PlatformPlan.MaxTiles} per call (split it up)");
            int topY = y - (rows - 1) * spacing;
            foreach (var (cx, cy) in new[] { (x1, y), (x2, y), (x1, topY), (x2, topY) })
                if (!WorldGen.InWorld(cx, cy, 10))
                    throw new CommandError(ErrorCodes.BadParams, $"({cx},{cy}) is outside the world");

            var pl = Main.LocalPlayer;
            var (type, anyPlatform, name) = ResolveItem(pl, Params.StringOrNull(p, "item"));
            int have = 0;
            for (int i = 0; i < 58; i++)
            {
                var it = pl.inventory[i];
                if (!it.IsAir && (anyPlatform ? HeldItemLease.IsPlatform(it) : it.type == type)) have += it.stack;
            }

            // Estimate from what the player can see (the task re-checks every tile when it gets there).
            int visibleBlocked = 0;
            foreach (int ry in PlatformPlan.RowYs(y, rows, spacing))
                for (int x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
                    if (Visibility.IsTileVisible(x, ry) && BuildPlatformsTask.SkipReason(x, ry) != null) visibleBlocked++;
            if (visibleBlocked == count)
                throw new CommandError(ErrorCodes.BadParams, "every tile there is already occupied or protected; nothing to build");

            var task = new BuildPlatformsTask(type, anyPlatform, name, x1, x2, y, rows, spacing);
            TaskHost.Start(task);
            long toPlace = count - visibleBlocked;
            return new
            {
                Started = true,
                Task = task.Name,
                Item = name,
                Tiles = count,
                ToPlaceEstimate = toPlace,
                Have = have,
                Warning = have < toPlace ? $"only {have} {name} for ~{toPlace} tiles: it will stop when they run out" : null,
            };
        }, isAction: true);
    }

    /// <summary>
    /// The item to build with: the named one (must place a block or platform, not furniture), else any platform
    /// (<paramref name="anyPlatform"/> true: every platform item counts), else the cheapest block
    /// (<see cref="HeldItemLease.BuildItem"/>).
    /// </summary>
    private static (int Type, bool AnyPlatform, string Name) ResolveItem(Player pl, string? query)
    {
        if (query != null)
        {
            var it = pl.inventory[ItemLookup.FindSlot(pl, query)];
            if (it.createTile < 0 || Main.tileFrameImportant[it.createTile] && !TileID.Sets.Platforms[it.createTile]
                || !(Main.tileSolid[it.createTile] || TileID.Sets.Platforms[it.createTile]))
                throw new CommandError(ErrorCodes.BadParams, $"{it.Name} isn't a block or platform");
            return (it.type, false, it.Name);
        }
        int slot = HeldItemLease.BuildItem(pl);
        if (slot < 0)
            throw new CommandError(ErrorCodes.MissingItem, "no platforms or cheap blocks (dirt, mud, stone, ash, clay, wood) in inventory; craft platforms from wood or give item");
        var item = pl.inventory[slot];
        bool platform = HeldItemLease.IsPlatform(item);
        return (item.type, platform, platform ? "platforms" : item.Name);
    }
}
