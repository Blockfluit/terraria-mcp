namespace LlmPlayer.Core.Build;

/// <summary>Which basic wall recipe a block feeds (1 block → 4 walls at a Work Bench): any wood with a furniture
/// family (<see cref="HouseMaterials.FamilyOf"/>) → its wood wall, Dirt Block → Dirt Wall, Stone Block → Stone Wall.</summary>
public enum WallSource { None, Wood, Dirt, Stone }

/// <summary>A stack of frame-capable blocks the player owns. <paramref name="Source"/> marks the woods / Dirt Block /
/// Stone Block that recipes take. List order = preference for the frame.</summary>
public sealed record BlockStock(int ItemType, string Name, int Count, WallSource Source = WallSource.None);

/// <summary>What the player has for a house: ready-made items per role, frame blocks, Gel (torches) and whether a
/// Work Bench is in crafting range right now. <paramref name="Tables"/> counts work bench items too (a work bench is
/// the house's flat surface); <paramref name="WorkBenchItems"/> is how many of them are work benches.</summary>
public sealed record HouseStock(int Doors, int Tables, int Chairs, int Torches, int Walls, IReadOnlyList<BlockStock> Blocks,
    int Gel, bool WorkBenchNear, int WorkBenchItems = 0);

/// <summary>Craft <paramref name="Item"/> <paramref name="Times"/> times (each craft yields the recipe's stack).</summary>
public readonly record struct CraftOrder(string Item, int Times);

public sealed record MissingItem(string Item, int Count, string Hint)
{
    public override string ToString() => $"{Item} x{Count} ({Hint})";
}

/// <summary>The furniture and wall one kind of wood makes (vanilla 1.4.4 names; same amounts for every wood).</summary>
public sealed record WoodFamily(string Wood, string Door, string Chair, string Table, string WorkBench, string Wall);

/// <summary>Ok when nothing is missing: the crafts to do first (in order) and which blocks the frame will use.</summary>
/// <param name="PlaceBench">No Work Bench in range but something must be crafted at one: place a work bench next to
/// the site first (one from <see cref="Crafts"/> or the inventory), then craft <see cref="AfterBench"/> at it.</param>
/// <param name="AfterBench">Crafts that need the placed Work Bench (empty unless <see cref="PlaceBench"/>).</param>
public sealed record HouseBill(IReadOnlyList<CraftOrder> Crafts, IReadOnlyList<(string Name, int Count)> FrameUse,
    IReadOnlyList<MissingItem> Missing, int FrameBlocks, int BackWalls, bool PlaceBench = false,
    IReadOnlyList<CraftOrder>? AfterBench = null)
{
    public bool Ok => Missing.Count == 0;

    public IReadOnlyList<CraftOrder> BenchCrafts => AfterBench ?? Array.Empty<CraftOrder>();

    public string MissingText() => string.Join("; ", Missing.Select(m => m.ToString()));
}

/// <summary>
/// Bill of materials for <c>build_house</c>, from vanilla's basic recipes (1.4.4), for every common wood (Wood,
/// Boreal Wood, Rich Mahogany, Ebonwood, Shadewood, Palm Wood, Pearlwood, Ash Wood): Door 6, Chair 4, Table 8 wood,
/// Wall 1 wood → 4, Dirt/Stone Wall 1 block → 4 (all at a Work Bench); Work Bench 10 wood and Torch 1 wood (any) +
/// 1 Gel → 3 (no station). A Work Bench also counts as the house's flat surface.
/// <list type="number">
/// <item>Items the player already has are used first (door, table/work bench, chair, torch, house walls).</item>
/// <item>Missing furniture is crafted from wood (reserved before anything else): each item from the wood the player
/// has the most of that covers it, so 81 Ebonwood makes an Ebonwood Door when there's no plain Wood.</item>
/// <item>No Work Bench in range but a door, chair or walls to craft: a work bench is placed next to the site
/// (<see cref="HouseBill.PlaceBench"/>; one from the inventory, else crafted, 10 wood), the rest is crafted at it and
/// the table is crafted too (8 wood). With only the table missing and no bench near, the table becomes a Work Bench.</item>
/// <item>Missing walls are crafted from the first of Dirt Block, Stone Block, wood that covers them while leaving
/// enough blocks for the frame (cheap blocks first: the frame prefers wood).</item>
/// <item>The frame takes the remaining blocks in the given preference order (Wood first).</item>
/// </list>
/// Short of anything → <see cref="HouseBill.Missing"/> with exact counts and a hint where to get it.
/// </summary>
public static class HouseMaterials
{
    public const int DoorWood = 6, ChairWood = 4, TableWood = 8, WorkBenchWood = 10, WallsPerBlock = 4, TorchesPerCraft = 3;

    public static readonly IReadOnlyList<WoodFamily> Families = new[]
    {
        new WoodFamily("Wood", "Wooden Door", "Wooden Chair", "Wooden Table", "Work Bench", "Wood Wall"),
        Named("Boreal Wood"), Named("Rich Mahogany"), Named("Ebonwood"), Named("Shadewood"), Named("Palm Wood"),
        Named("Pearlwood"), Named("Ash Wood"),
    };

    private static WoodFamily Named(string wood) =>
        new(wood, $"{wood} Door", $"{wood} Chair", $"{wood} Table", $"{wood} Work Bench", $"{wood} Wall");

    /// <summary>The furniture family of a wood item name, or null (not a wood build_house crafts from).</summary>
    public static WoodFamily? FamilyOf(string woodName) => Families.FirstOrDefault(f => f.Wood == woodName);

    private static readonly string AnyWood = $"any wood: {string.Join(", ", new[] { "Wood", "Boreal Wood", "Rich Mahogany", "Ebonwood", "Shadewood", "Palm Wood", "Pearlwood" })}…";

    private enum Role { Door, Chair, Table, WorkBench, Torch }

    public static HouseBill Plan(int frameBlocks, int backWalls, HouseStock s)
    {
        var missing = new List<MissingItem>();
        var counts = s.Blocks.Select(b => b.Count).ToArray();
        var woods = Enumerable.Range(0, s.Blocks.Count)
            .Where(i => s.Blocks[i].Source == WallSource.Wood && FamilyOf(s.Blocks[i].Name) != null).ToList();
        int Find(WallSource src) => s.Blocks.ToList().FindIndex(b => b.Source == src);

        int shortWalls = Math.Max(0, backWalls - s.Walls);
        int wallCrafts = (shortWalls + WallsPerBlock - 1) / WallsPerBlock;

        bool needDoor = s.Doors < 1, needChair = s.Chairs < 1;
        bool needStation = needDoor || needChair || wallCrafts > 0;
        bool placeBench = needStation && !s.WorkBenchNear;
        int tables = s.Tables;
        bool craftBench = false;
        if (placeBench)
        {
            if (s.WorkBenchItems > 0) tables--; // that bench becomes the crafting station, not the house's table
            else craftBench = true;
        }
        bool benchAvailable = s.WorkBenchNear || placeBench;

        // Wood furniture, largest first so a small stack doesn't get used up by the chair.
        var wanted = new List<(Role Role, int Wood)>();
        if (craftBench) wanted.Add((Role.WorkBench, WorkBenchWood));
        if (tables < 1) wanted.Add(benchAvailable ? (Role.Table, TableWood) : (Role.WorkBench, WorkBenchWood));
        if (needDoor) wanted.Add((Role.Door, DoorWood));
        if (needChair) wanted.Add((Role.Chair, ChairWood));
        if (s.Torches < 1)
        {
            if (s.Gel < 1) missing.Add(new MissingItem("Torch", 1, "bring a torch, or 1 Gel (kill a slime) + 1 Wood to craft 3"));
            else wanted.Add((Role.Torch, 1));
        }

        var picked = new List<(Role Role, string Item)>();
        int woodShort = 0;
        var shortFor = new List<string>();
        foreach (var (role, need) in wanted.OrderByDescending(w => w.Wood))
        {
            int best = -1;
            foreach (int i in woods)
                if (counts[i] >= need && (best < 0 || counts[i] > counts[best])) best = i;
            if (best < 0)
            {
                woodShort += need;
                shortFor.Add(RoleLabel(role));
                continue;
            }
            counts[best] -= need;
            var fam = FamilyOf(s.Blocks[best].Name)!;
            picked.Add((role, role switch
            {
                Role.Door => fam.Door,
                Role.Chair => fam.Chair,
                Role.Table => fam.Table,
                Role.WorkBench => fam.WorkBench,
                _ => "Torch",
            }));
        }
        if (woodShort > 0)
        {
            // The rest of the biggest stack would have gone toward the items that didn't fit.
            int rest = woods.Select(i => counts[i]).DefaultIfEmpty(0).Max();
            missing.Add(new MissingItem("Wood", Math.Max(1, woodShort - rest), $"to craft {string.Join(", ", shortFor)} ({AnyWood}): chop trees"));
        }

        // Craft order: the work bench to place first, then walls (at the bench), door, chair, table, torch. At most one
        // Work Bench is wanted: the one to place (bench not near), or the table substitute (nothing else needs a bench).
        string? Item(Role r) => picked.Where(p => p.Role == r).Select(p => p.Item).FirstOrDefault();
        var furniture = new List<CraftOrder>();
        string? tableItem = tables < 1 ? Item(benchAvailable ? Role.Table : Role.WorkBench) : null;
        string? benchItem = craftBench ? Item(Role.WorkBench) : null;
        if (Item(Role.Door) is { } door) furniture.Add(new(door, 1));
        if (Item(Role.Chair) is { } chair) furniture.Add(new(chair, 1));
        if (tableItem != null) furniture.Add(new(tableItem, 1));
        bool torchCraft = Item(Role.Torch) != null;

        var wallOrders = new List<CraftOrder>();
        int total = counts.Sum();
        if (wallCrafts > 0)
        {
            int chosen = -1;
            foreach (var src in new[] { WallSource.Dirt, WallSource.Stone, WallSource.Wood })
            {
                int i = src == WallSource.Wood ? Richest(woods, counts) : Find(src);
                if (i >= 0 && counts[i] >= wallCrafts && total - wallCrafts >= frameBlocks) { chosen = i; break; }
            }
            if (chosen < 0)
            {
                // Nothing covers both: use the richest wall source for walls and report what's left short as Wood.
                int richest = -1;
                foreach (int i in woods.Concat(new[] { Find(WallSource.Dirt), Find(WallSource.Stone) }).Where(i => i >= 0))
                    if (richest < 0 || counts[i] > counts[richest]) richest = i;
                int usable = richest >= 0 ? Math.Min(counts[richest], wallCrafts) : 0;
                int short_ = (wallCrafts - usable) + Math.Max(0, frameBlocks - (total - usable));
                if (short_ > 0)
                    missing.Add(new MissingItem("Wood", short_,
                        $"{frameBlocks} blocks for the frame (any blocks: Wood, Stone, Dirt…) plus {wallCrafts} wood/Dirt/Stone for {shortWalls} background walls (4 per block)"));
                if (richest >= 0) counts[richest] -= usable;
                if (usable > 0) wallOrders.Add(new CraftOrder(WallName(s.Blocks[richest]), usable));
            }
            else
            {
                counts[chosen] -= wallCrafts;
                wallOrders.Add(new CraftOrder(WallName(s.Blocks[chosen]), wallCrafts));
            }
        }

        var frameUse = new List<(string, int)>();
        int left = frameBlocks;
        for (int i = 0; i < counts.Length && left > 0; i++)
        {
            int take = Math.Min(counts[i], left);
            if (take <= 0) continue;
            frameUse.Add((s.Blocks[i].Name, take));
            left -= take;
        }
        if (left > 0 && !missing.Any(m => m.Item == "Wood" && m.Hint.Contains("frame")))
            missing.Add(new MissingItem("Wood", left, $"{frameBlocks} blocks for the frame (any blocks: Wood, Stone, Dirt…): chop trees or mine"));

        var now = new List<CraftOrder>();
        var after = new List<CraftOrder>();
        if (placeBench)
        {
            if (benchItem != null) now.Add(new(benchItem, 1));
            if (torchCraft) now.Add(new("Torch", 1));
            after.AddRange(wallOrders);
            after.AddRange(furniture);
        }
        else
        {
            now.AddRange(wallOrders);
            now.AddRange(furniture);
            if (torchCraft) now.Add(new("Torch", 1));
        }
        return new HouseBill(now, frameUse, missing, frameBlocks, backWalls, placeBench, placeBench ? after : null);
    }

    private static int Richest(List<int> woods, int[] counts)
    {
        int best = -1;
        foreach (int i in woods)
            if (best < 0 || counts[i] > counts[best]) best = i;
        return best;
    }

    private static string RoleLabel(Role role) => role == Role.WorkBench ? "Work Bench" : role.ToString();

    public static string WallName(BlockStock b) => b.Source switch
    {
        WallSource.Wood => FamilyOf(b.Name)?.Wall ?? "Wood Wall",
        WallSource.Dirt => "Dirt Wall",
        WallSource.Stone => "Stone Wall",
        _ => throw new ArgumentOutOfRangeException(nameof(b)),
    };
}
