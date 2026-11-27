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
                    // A side with room to stand outside, the one facing the player first.
                    _ => HouseSite.DefaultDoor(x, y, width, height, new TilePoint(px, py), Housing.Cell),
                };
                layout = new HouseLayout(x, y, width, height, door);
                site = HouseSite.Check(layout, Housing.Cell, strict: false);
                if (!site.Ok) throw new CommandError(ErrorCodes.BadParams, $"can't build a {layout}: {site.Problem}");
            }
            else
            {
                var report = new SiteSearchReport();
                var found = HouseSite.FindWithCheck(new TilePoint(px, py), width, height, Housing.Cell, SearchRadius, report)
                         ?? throw new CommandError(ErrorCodes.BadParams,
                             $"no clear, visible spot for a {width}x{height} house within {SearchRadius} tiles " +
                             $"(needs ground under at least {HouseSite.MinGround(width)} of the {width} floor tiles, gaps at most {HouseSite.MaxDipDepth} deep " +
                             $"(filled with blocks), {height - 1} tiles of open air above the floor apart from natural bumps at most {HouseSite.MaxBumpHeight} tall " +
                             "and unprotected platforms (mined first; platforms in the frame are replaced by blocks), no trees, no natural background walls, " +
                             $"room to stand outside the door). {report.Top(3)}. Explore or give x,y (bottom-left floor tile)");
                (layout, site) = found;
                if (doorParam != null)
                {
                    var forced = layout with { Door = doorParam == "left" ? DoorSide.Left : DoorSide.Right };
                    var forcedSite = HouseSite.Check(forced, Housing.Cell, strict: true);
                    if (forcedSite.Ok) (layout, site) = (forced, forcedSite);
                }
            }

            // Tiles to mine first (bumps, decoration) don't anchor anything: they'll be gone.
            var mineFirst = site.MineFirst();
            var mineSet = new HashSet<TilePoint>(mineFirst);
            if (site.PickaxeTiles > 0 && HeldItemLease.BestPickaxe(pl) < 0)
                throw new CommandError(ErrorCodes.MissingItem,
                    $"a {layout} needs {site.PickaxeTiles} tile(s) mined out of the way first (ground bumps, platforms in the plot; first {mineFirst.First()}) and you have no pickaxe");
            if (site.Flatten is { Count: > 0 } && HouseItems.BestHammer(pl) < 0)
                throw new CommandError(ErrorCodes.MissingItem,
                    $"{site.Flatten.Count} frame block(s) are half blocks or slopes (first {site.Flatten.First()}): furniture and doors can't stand on them and you have no hammer to flatten them");

            // Bill of materials, up front: fail with exact counts rather than run out half-way.
            var bill = HouseMaterials.Plan(site.FrameToPlace.Count, site.WallsToPlace.Count, HouseItems.Stock(pl));
            if (!bill.Ok)
                throw new CommandError(ErrorCodes.MissingItem,
                    $"a {layout} needs {site.FrameToPlace.Count} frame blocks, {site.WallsToPlace.Count} background walls, a door, a table, a chair and a torch. " +
                    $"Missing: {bill.MissingText()}");

            // No Work Bench in range: place one beside the house first, then craft the rest at it.
            TilePoint? benchAt = null;
            if (bill.PlaceBench)
            {
                benchAt = HouseSite.BenchSpot(layout, Housing.Cell)
                    ?? throw new CommandError(ErrorCodes.MissingItem,
                        $"{string.Join(", ", bill.BenchCrafts.Select(c => c.Item))} must be crafted at a Work Bench: none is in range and there's no flat, " +
                        $"visible 2-wide spot beside the house to place one{NearestBench(pl)}");
            }

            // Anchors: existing blocks/platforms (and walls) where the frame attaches.
            var steps = HousePlan.Steps(layout, site.FrameToPlace, site.WallsToPlace,
                t => Housing.Cell(t.X, t.Y) is var c && ((c.IsBlock && !mineSet.Contains(t)) || c.Wall != WallState.None),
                t => Housing.Cell(t.X, t.Y) is var c && ((c.Tile != SiteTile.Air && !mineSet.Contains(t)) || c.Wall != WallState.None),
                out var planProblem, site.Flatten, benchAt);
            if (planProblem != null) throw new CommandError(ErrorCodes.BadParams, $"can't build a {layout}: {planProblem}");

            // Crafting (instant, real recipes and stations) before walking off; bench crafts once the bench stands.
            var crafted = new List<string>();
            foreach (var order in bill.Crafts)
            {
                var r = Crafting.Craft(pl, order.Item, order.Times);
                crafted.Add($"{r.Item} x{r.Crafted}");
                if (r.Reason != null)
                    throw new CommandError(ErrorCodes.MissingItem, $"crafting {order.Item} x{order.Times} stopped: {r.Reason} (crafted so far: {Describe(crafted)})");
            }
            var after = HouseMaterials.Plan(site.FrameToPlace.Count, site.WallsToPlace.Count, HouseItems.Stock(pl));
            if (!after.Ok || (!bill.PlaceBench && after.Crafts.Count > 0))
                throw new CommandError(ErrorCodes.MissingItem, $"still short after crafting ({Describe(crafted)}): " +
                    (after.Ok ? string.Join(", ", after.Crafts.Select(c => $"{c.Item} x{c.Times}")) : after.MissingText()));

            var task = new BuildHouseTask(layout, steps, site.CanExit, mineFirst,
                bill.PlaceBench ? new BenchCrafting(bill.BenchCrafts, site.FrameToPlace.Count, site.WallsToPlace.Count) : null);
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
                FloorGapsToFill = site.FrameToPlace.Count(t => t.Y == layout.FloorY),
                MineFirst = mineFirst.Count > 0 ? mineFirst.Count : (int?)null,
                PlatformsReplaced = site.Replace is { Count: > 0 } rp ? rp.Count : (int?)null,
                WorkBenchAt = benchAt is { } ba ? new { ba.X, ba.Y } : null,
                CraftAtBench = bill.PlaceBench ? bill.BenchCrafts.Select(c => $"{c.Item} x{c.Times}") : null,
                Flatten = site.Flatten is { Count: > 0 } fl ? fl.Count : (int?)null,
                BackgroundWalls = site.WallsToPlace.Count,
                FrameUses = after.FrameUse.Select(f => new { Item = f.Name, f.Count }),
                Crafted = crafted.Count > 0 ? crafted : null,
                Note = site.CanExit ? null
                    : "no room to stand outside the door (2 wide, 3 high, on ground): the player stays inside when done and the house may be unreachable; clear the space or pick another spot/door side",
            };
        }, isAction: true);
    }

    private static string Describe(List<string> crafted) => crafted.Count == 0 ? "nothing" : string.Join(", ", crafted);

    private static string NearestBench(Player p)
    {
        try
        {
            var near = Finder.Find(p, "station", "Work Bench", 60);
            if (near.Count > 0) return $". Nearest Work Bench: ({near[0].X},{near[0].Y}), {near[0].Distance:0} tiles away (move_to next to it, then build_house again with x,y)";
        }
        catch (CommandError) { }
        return "";
    }
}
