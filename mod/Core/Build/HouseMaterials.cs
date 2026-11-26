namespace LlmPlayer.Core.Build;

/// <summary>Which basic wall recipe a block feeds (1 block → 4 walls at a Work Bench): plain Wood → Wood Wall, Dirt
/// Block → Dirt Wall, Stone Block → Stone Wall.</summary>
public enum WallSource { None, Wood, Dirt, Stone }

/// <summary>A stack of frame-capable blocks the player owns. <paramref name="Source"/> marks the plain Wood / Dirt
/// Block / Stone Block that recipes take. List order = preference for the frame.</summary>
public sealed record BlockStock(int ItemType, string Name, int Count, WallSource Source = WallSource.None);

/// <summary>What the player has for a house: ready-made items per role, frame blocks, Gel (torches) and whether a
/// Work Bench is in crafting range right now.</summary>
public sealed record HouseStock(int Doors, int Tables, int Chairs, int Torches, int Walls, IReadOnlyList<BlockStock> Blocks,
    int Gel, bool WorkBenchNear);

/// <summary>Craft <paramref name="Item"/> <paramref name="Times"/> times (each craft yields the recipe's stack).</summary>
public readonly record struct CraftOrder(string Item, int Times);

public sealed record MissingItem(string Item, int Count, string Hint)
{
    public override string ToString() => $"{Item} x{Count} ({Hint})";
}

/// <summary>Ok when nothing is missing: the crafts to do first (in order) and which blocks the frame will use.</summary>
public sealed record HouseBill(IReadOnlyList<CraftOrder> Crafts, IReadOnlyList<(string Name, int Count)> FrameUse,
    IReadOnlyList<MissingItem> Missing, int FrameBlocks, int BackWalls)
{
    public bool Ok => Missing.Count == 0;

    public string MissingText() => string.Join("; ", Missing.Select(m => m.ToString()));
}

/// <summary>
/// Bill of materials for <c>build_house</c>, from vanilla's basic recipes (1.4.4):
/// Wooden Door 6 Wood, Wooden Chair 4 Wood, Wooden Table 8 Wood, Wood/Dirt/Stone Wall 1 block → 4 (all at a Work
/// Bench); Work Bench 10 Wood and Torch 1 Wood + 1 Gel → 3 (no station). A Work Bench also counts as the house's
/// flat surface, so without a Work Bench nearby the table becomes a Work Bench.
/// <list type="number">
/// <item>Items the player already has are used first (door, table/work bench, chair, torch, house walls).</item>
/// <item>Missing furniture is crafted from plain Wood (reserved before anything else).</item>
/// <item>Missing walls are crafted from the first of Dirt Block, Stone Block, Wood that covers them while leaving
/// enough blocks for the frame (cheap blocks first: the frame prefers Wood).</item>
/// <item>The frame takes the remaining blocks in the given preference order (Wood first).</item>
/// </list>
/// Short of anything → <see cref="HouseBill.Missing"/> with exact counts and a hint where to get it.
/// </summary>
public static class HouseMaterials
{
    public const int DoorWood = 6, ChairWood = 4, TableWood = 8, WorkBenchWood = 10, WallsPerBlock = 4, TorchesPerCraft = 3;

    /// <summary>Walls are hidden behind furniture and NPCs: cheap blocks first, so Wood stays for the frame.</summary>
    private static readonly WallSource[] WallPreference = { WallSource.Dirt, WallSource.Stone, WallSource.Wood };

    public static HouseBill Plan(int frameBlocks, int backWalls, HouseStock s)
    {
        var crafts = new List<CraftOrder>();
        var missing = new List<MissingItem>();
        int woodNeeded = 0;
        var benchFor = new List<string>();

        if (s.Doors < 1) { crafts.Add(new("Wooden Door", 1)); woodNeeded += DoorWood; benchFor.Add("Wooden Door"); }
        if (s.Chairs < 1) { crafts.Add(new("Wooden Chair", 1)); woodNeeded += ChairWood; benchFor.Add("Wooden Chair"); }
        if (s.Tables < 1)
        {
            if (s.WorkBenchNear) { crafts.Add(new("Wooden Table", 1)); woodNeeded += TableWood; }
            else { crafts.Add(new("Work Bench", 1)); woodNeeded += WorkBenchWood; }
        }
        if (s.Torches < 1)
        {
            if (s.Gel < 1) missing.Add(new MissingItem("Torch", 1, "bring a torch, or 1 Gel (kill a slime) + 1 Wood to craft 3"));
            else { crafts.Add(new("Torch", 1)); woodNeeded += 1; }
        }

        var counts = s.Blocks.Select(b => b.Count).ToArray();
        int Find(WallSource src) => s.Blocks.ToList().FindIndex(b => b.Source == src);
        int woodIdx = Find(WallSource.Wood);
        int wood = woodIdx >= 0 ? counts[woodIdx] : 0;
        if (wood < woodNeeded)
            missing.Add(new MissingItem("Wood", woodNeeded - wood, $"to craft {string.Join(", ", crafts.Select(c => c.Item))}: chop trees"));
        if (woodIdx >= 0) counts[woodIdx] = Math.Max(0, wood - woodNeeded);

        int shortWalls = Math.Max(0, backWalls - s.Walls);
        int wallCrafts = (shortWalls + WallsPerBlock - 1) / WallsPerBlock;
        int total = counts.Sum();
        if (wallCrafts > 0)
        {
            benchFor.Add("background walls");
            int chosen = -1;
            foreach (var src in WallPreference)
            {
                int i = Find(src);
                if (i >= 0 && counts[i] >= wallCrafts && total - wallCrafts >= frameBlocks) { chosen = i; break; }
            }
            if (chosen < 0)
            {
                // Nothing covers both: use the richest wall source for walls and report what's left short as Wood.
                int richest = -1;
                foreach (var src in new[] { WallSource.Wood, WallSource.Dirt, WallSource.Stone })
                {
                    int i = Find(src);
                    if (i >= 0 && (richest < 0 || counts[i] > counts[richest])) richest = i;
                }
                int usable = richest >= 0 ? Math.Min(counts[richest], wallCrafts) : 0;
                int short_ = (wallCrafts - usable) + Math.Max(0, frameBlocks - (total - usable));
                if (short_ > 0)
                    missing.Add(new MissingItem("Wood", short_,
                        $"{frameBlocks} blocks for the frame (any blocks: Wood, Stone, Dirt…) plus {wallCrafts} Wood/Dirt/Stone for {shortWalls} background walls (4 per block)"));
                if (richest >= 0) counts[richest] -= usable;
                if (usable > 0) crafts.Insert(0, new CraftOrder(WallName(s.Blocks[richest].Source), usable));
            }
            else
            {
                counts[chosen] -= wallCrafts;
                crafts.Insert(0, new CraftOrder(WallName(s.Blocks[chosen].Source), wallCrafts));
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

        if (benchFor.Count > 0 && !s.WorkBenchNear)
            missing.Add(new MissingItem("Work Bench nearby", 1,
                $"{string.Join(", ", benchFor)} are crafted at a Work Bench: stand next to one (or place one, 10 Wood) and call build_house again"));

        return new HouseBill(crafts, frameUse, missing, frameBlocks, backWalls);
    }

    public static string WallName(WallSource s) => s switch
    {
        WallSource.Wood => "Wood Wall",
        WallSource.Dirt => "Dirt Wall",
        WallSource.Stone => "Stone Wall",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}
