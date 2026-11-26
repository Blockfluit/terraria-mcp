using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Build;
using LlmPlayer.Core.Grid;
using LlmPlayer.Interact;
using LlmPlayer.Inventory;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;

namespace LlmPlayer.Commands;

/// <summary>build_house (action, task) and check_housing (observation). See <see cref="BuildHouseTask"/> and
/// <see cref="Housing"/>.</summary>
[CommandGroup]
public static class HousingCommands
{
    public const int SearchRadius = 30;

    public static void Register()
    {
        CommandRouter.Register("check_housing", p =>
        {
            bool hasX = Params.Has(p, "x"), hasY = Params.Has(p, "y");
            if (hasX != hasY) throw new CommandError(ErrorCodes.BadParams, "give both x and y, or neither");
            HousingVerdict? room = null;
            if (hasX)
            {
                int x = Params.Int(p, "x"), y = Params.Int(p, "y");
                if (!WorldGen.InWorld(x, y, 10)) throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
                if (!Visibility.IsTileVisible(x, y)) throw new CommandError(ErrorCodes.NotVisible, $"({x},{y}) is not visible");
                room = Housing.Query(x, y);
            }
            var npcs = Housing.Npcs();
            return new { Housing = room, npcs.Residents, npcs.Homeless, npcs.NotYetHere, npcs.Note };
        }, isAction: false);

        CommandRouter.Register("build_house", p =>
        {
            var pl = Main.LocalPlayer;
            int width = Params.Int(p, "width", HouseLayout.DefaultWidth), height = Params.Int(p, "height", HouseLayout.DefaultHeight);
            if (HouseLayout.SizeProblem(width, height) is { } sizeProblem) throw new CommandError(ErrorCodes.BadParams, sizeProblem);
            bool hasX = Params.Has(p, "x"), hasY = Params.Has(p, "y");
            if (hasX != hasY) throw new CommandError(ErrorCodes.BadParams, "give both x and y, or neither");
            string? doorParam = Params.StringOrNull(p, "door")?.Trim().ToLowerInvariant();
            if (doorParam is not (null or "left" or "right")) throw new CommandError(ErrorCodes.BadParams, "door must be 'left' or 'right'");

            var (px, py) = StateReader.StandingTile(pl);
            HouseLayout layout;
            SiteCheck site;
            if (hasX)
            {
                int x = Params.Int(p, "x"), y = Params.Int(p, "y");
                if (!WorldGen.InWorld(x - 3, y - height, 10) || !WorldGen.InWorld(x + width + 2, y + 1, 10))
                    throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is too close to the world edge");
                var door = doorParam switch
                {
                    "left" => DoorSide.Left,
                    "right" => DoorSide.Right,
                    _ => px + 1 < x + width / 2f ? DoorSide.Left : DoorSide.Right, // the side facing the player
                };
                layout = new HouseLayout(x, y, width, height, door);
                site = HouseSite.Check(layout, Housing.Cell, strict: false);
                if (!site.Ok) throw new CommandError(ErrorCodes.BadParams, $"can't build a {layout}: {site.Problem}");
            }
            else
            {
                layout = HouseSite.Find(new TilePoint(px, py), width, height, Housing.Cell, SearchRadius)
                         ?? throw new CommandError(ErrorCodes.BadParams,
                             $"no flat, clear, visible spot for a {width}x{height} house within {SearchRadius} tiles " +
                             $"(needs {width} tiles of level ground, {height - 1} tiles of open air above it, no trees, room outside the door); " +
                             "explore or give x,y (bottom-left floor tile)");
                if (doorParam != null)
                {
                    var forced = layout with { Door = doorParam == "left" ? DoorSide.Left : DoorSide.Right };
                    if (HouseSite.Check(forced, Housing.Cell, strict: true).Ok) layout = forced;
                }
                site = HouseSite.Check(layout, Housing.Cell, strict: true);
            }

            // Anchors: existing blocks/platforms (and walls) where the frame attaches.
            var steps = HousePlan.Steps(layout, site.FrameToPlace, site.WallsToPlace,
                t => Housing.Cell(t.X, t.Y) is var c && (c.IsBlock || c.Wall != WallState.None),
                t => Housing.Cell(t.X, t.Y) is var c && (c.Tile != SiteTile.Air || c.Wall != WallState.None),
                out var planProblem);
            if (planProblem != null) throw new CommandError(ErrorCodes.BadParams, $"can't build a {layout}: {planProblem}");

            // Bill of materials, up front: fail with exact counts rather than run out half-way.
            var bill = HouseMaterials.Plan(site.FrameToPlace.Count, site.WallsToPlace.Count, HouseItems.Stock(pl));
            if (!bill.Ok)
                throw new CommandError(ErrorCodes.MissingItem,
                    $"a {layout} needs {site.FrameToPlace.Count} frame blocks, {site.WallsToPlace.Count} background walls, a door, a table, a chair and a torch. " +
                    $"Missing: {bill.MissingText()}{NearestBench(pl, bill)}");

            // Crafting (instant, real recipes and stations) before walking off.
            var crafted = new List<string>();
            foreach (var order in bill.Crafts)
            {
                var r = Crafting.Craft(pl, order.Item, order.Times);
                crafted.Add($"{r.Item} x{r.Crafted}");
                if (r.Reason != null)
                    throw new CommandError(ErrorCodes.MissingItem, $"crafting {order.Item} x{order.Times} stopped: {r.Reason} (crafted so far: {Describe(crafted)})");
            }
            var after = HouseMaterials.Plan(site.FrameToPlace.Count, site.WallsToPlace.Count, HouseItems.Stock(pl));
            if (after.Crafts.Count > 0 || !after.Ok)
                throw new CommandError(ErrorCodes.MissingItem, $"still short after crafting ({Describe(crafted)}): " +
                    (after.Ok ? string.Join(", ", after.Crafts.Select(c => $"{c.Item} x{c.Times}")) : after.MissingText()));

            var task = new BuildHouseTask(layout, steps, site.CanExit);
            TaskHost.Start(task);
            return new
            {
                Started = true,
                Task = task.Name,
                House = new
                {
                    layout.X, Y = layout.FloorY, layout.Width, layout.Height,
                    Door = layout.Door == DoorSide.Left ? "left" : "right",
                    DoorAt = new { layout.DoorAim.X, layout.DoorAim.Y },
                    Inside = new { X = layout.CheckTile.X, Y = layout.CheckTile.Y },
                },
                Steps = steps.Count,
                FrameBlocks = site.FrameToPlace.Count,
                BackgroundWalls = site.WallsToPlace.Count,
                FrameUses = after.FrameUse.Select(f => new { Item = f.Name, f.Count }),
                Crafted = crafted.Count > 0 ? crafted : null,
                Note = site.CanExit ? null : "no room to stand outside the door: the player stays inside when done",
            };
        }, isAction: true);
    }

    private static string Describe(List<string> crafted) => crafted.Count == 0 ? "nothing" : string.Join(", ", crafted);

    private static string NearestBench(Player p, HouseBill bill)
    {
        if (!bill.Missing.Any(m => m.Item == "Work Bench nearby")) return "";
        try
        {
            var near = Finder.Find(p, "station", "Work Bench", 60);
            if (near.Count > 0) return $". Nearest Work Bench: ({near[0].X},{near[0].Y}), {near[0].Distance:0} tiles away (move_to next to it, then build_house again with x,y)";
        }
        catch (CommandError) { }
        return "";
    }
}
