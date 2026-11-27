namespace LlmPlayer.Core.Inventory;

/// <summary>What the full-inventory rules need to know about an item (from vanilla's Item fields).</summary>
/// <param name="Potion">Heals life/mana or gives a buff when consumed.</param>
/// <param name="Rarity">Vanilla <c>rare</c> (0 = white, 1 = blue, …; boss drops and Life Crystals are above 0).</param>
/// <param name="Special">Boss bag, quest fish, or anything else vanilla flags as precious.</param>
public readonly record struct ItemWorth(string Name, bool Coin = false, bool Potion = false, int Rarity = 0, bool Special = false);

/// <summary>One main-inventory slot (0-49) for choosing what to trash or suggest dropping.</summary>
public readonly record struct InvSlot(int Slot, string Name, int Stack, bool Favorited = false, bool Valuable = false,
    bool Placeable = false, bool InUse = false);

/// <summary>A dropped item the player couldn't take because the inventory is full.</summary>
public readonly record struct LeftItem(string Name, int Stack, int X, int Y);

/// <summary>
/// Full inventory rules. Seen in game: with all 50 slots full, collect broke a Life Crystal and reported "picked up 0,
/// 0 left in range" (pick_up_items silently drops items the player can't take), and explore/blast had filled the slots
/// with hundreds of junk blocks (Ebonstone Block 250). Now tasks report what was left behind (<see cref="Note"/>), and
/// an opt-in policy (set_reflexes <c>trash_when_full</c>, default empty) trashes one stack of a listed junk item when a
/// valuable item (<see cref="IsValuable"/>) can't be picked up (<see cref="ChooseTrash"/>). Pure.
/// </summary>
public static class FullInventory
{
    /// <summary>Main inventory slots (hotbar 0-9 + 10-49); coins and ammo have their own.</summary>
    public const int MainSlots = 50;

    /// <summary>Trashes per task at most: the policy frees room for valuables, it doesn't sort the inventory.</summary>
    public const int MaxTrashesPerTask = 5;

    private static readonly HashSet<string> Precious = new(StringComparer.OrdinalIgnoreCase)
    {
        "Life Crystal", "Life Fruit", "Mana Crystal", "Fallen Star",
        "Amethyst", "Topaz", "Sapphire", "Emerald", "Ruby", "Diamond", "Amber",
        "Lens", "Shadow Scale", "Tissue Sample", "Demonite Ore", "Crimtane Ore", "Hellstone", "Meteorite",
        "Obsidian", "Bomb", "Rope", "Torch", "Wooden Arrow", "Musket Ball", "Healing Potion", "Mana Potion",
    };

    /// <summary>Worth freeing a slot for: coins, potions, ores, bars, gems, life/mana crystals, anything above white
    /// rarity (boss drops, accessories, weapons found in chests), boss bags.</summary>
    public static bool IsValuable(ItemWorth t) =>
        t.Coin || t.Potion || t.Special || t.Rarity >= 1 || Precious.Contains(t.Name)
        || t.Name.EndsWith(" Ore", StringComparison.OrdinalIgnoreCase) || t.Name.EndsWith(" Bar", StringComparison.OrdinalIgnoreCase)
        || t.Name.EndsWith(" Potion", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Treasure Bag", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> is on the trash list (case-insensitive).</summary>
    public static bool Listed(IReadOnlyList<string> trashList, string name) =>
        trashList.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The slot to trash so <paramref name="incoming"/> fits, or -1: only when the trash list is non-empty, the incoming
    /// item is valuable and not itself on the list; candidates are listed, non-favorited, not-in-use main slots; the
    /// earliest name in the list wins, then the smallest stack (lose as little as possible).
    /// </summary>
    public static int ChooseTrash(IReadOnlyList<InvSlot> slots, IReadOnlyList<string> trashList, ItemWorth incoming)
    {
        if (trashList.Count == 0 || !IsValuable(incoming) || Listed(trashList, incoming.Name)) return -1;
        for (int k = 0; k < trashList.Count; k++)
        {
            int best = -1, stack = int.MaxValue;
            foreach (var s in slots)
            {
                if (s.Slot < 0 || s.Slot >= MainSlots || s.Favorited || s.InUse || s.Stack <= 0) continue;
                if (!string.Equals(s.Name, trashList[k], StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Stack < stack) { best = s.Slot; stack = s.Stack; }
            }
            if (best >= 0) return best;
        }
        return -1;
    }

    /// <summary>What to suggest dropping: a listed item first, else the biggest stack of a non-valuable placeable (junk
    /// blocks), else the biggest non-valuable stack; never favorited or in-use slots. "Name Stack", or null.</summary>
    public static string? Suggestion(IReadOnlyList<InvSlot> slots, IReadOnlyList<string> trashList)
    {
        var ok = slots.Where(s => s.Slot is >= 0 and < MainSlots && !s.Favorited && !s.InUse && s.Stack > 0).ToList();
        var pick = ok.Where(s => Listed(trashList, s.Name)).OrderByDescending(s => s.Stack).FirstOrDefault();
        if (pick.Name == null) pick = ok.Where(s => !s.Valuable && s.Placeable).OrderByDescending(s => s.Stack).FirstOrDefault();
        if (pick.Name == null) pick = ok.Where(s => !s.Valuable).OrderByDescending(s => s.Stack).FirstOrDefault();
        return pick.Name == null ? null : $"{pick.Name} {pick.Stack}";
    }

    public const int MaxListed = 4;

    /// <summary>
    /// "inventory full: Life Crystal not picked up at (x,y) — drop/trash something (e.g. Ebonstone Block 250) and
    /// pick_up_items again". Several items: the first <see cref="MaxListed"/> then "+N more". Null when none.
    /// </summary>
    public static string? Note(IReadOnlyList<LeftItem> left, string? suggestion)
    {
        if (left.Count == 0) return null;
        var parts = left.Take(MaxListed).Select(i => (i.Stack > 1 ? $"{i.Name} x{i.Stack}" : i.Name) + $" at ({i.X},{i.Y})");
        string items = string.Join(", ", parts) + (left.Count > MaxListed ? $" (+{left.Count - MaxListed} more)" : "");
        string eg = suggestion != null ? $" (e.g. {suggestion})" : "";
        return $"inventory full: {items} not picked up — drop/trash something{eg} with drop_item and pick_up_items again";
    }
}
