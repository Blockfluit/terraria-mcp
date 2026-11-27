namespace LlmPlayer.Core.Combat;

/// <summary>One inventory slot as ammo ordering sees it. <see cref="AmmoType"/> = the item's ammo class (Terraria's
/// <c>Item.ammo</c>, 0 = not ammo), <see cref="ItemType"/> = which item it is. Empty slots: all zero.</summary>
public readonly record struct AmmoSlot(int AmmoType, int ItemType, int Stack)
{
    public bool Matches(int useAmmo) => useAmmo > 0 && AmmoType == useAmmo && Stack > 0;
}

public enum AmmoPlanKind
{
    /// <summary>The wanted ammo is already the stack vanilla fires first.</summary>
    Ready,
    /// <summary>Swap slots <see cref="AmmoPlan.A"/> (the stack fired first now) and <see cref="AmmoPlan.B"/> (the wanted one).</summary>
    Swap,
    /// <summary>None of the wanted ammo left.</summary>
    Missing,
    /// <summary>The wanted item doesn't fit the weapon (different ammo class).</summary>
    WrongAmmo,
}

public readonly record struct AmmoPlan(AmmoPlanKind Kind, int A = -1, int B = -1);

/// <summary>
/// Which ammo a weapon fires: vanilla (<c>Player.ChooseAmmo</c>) takes the first stack of the weapon's ammo class in
/// the ammo slots 54-57, then in the inventory 0-53. To fire a particular ammo, swap its stack with that first one.
/// </summary>
public static class AmmoOrder
{
    public const int AmmoSlotsStart = 54, AmmoSlotsEnd = 58, SearchedSlots = 58;

    /// <summary>The slots in the order vanilla looks for ammo.</summary>
    public static IEnumerable<int> VanillaOrder(int slots = SearchedSlots)
    {
        for (int i = AmmoSlotsStart; i < Math.Min(AmmoSlotsEnd, slots); i++) yield return i;
        for (int i = 0; i < Math.Min(AmmoSlotsStart, slots); i++) yield return i;
    }

    /// <summary>The slot whose ammo the weapon fires next, or -1.</summary>
    public static int FirstUsed(IReadOnlyList<AmmoSlot> inv, int useAmmo)
    {
        foreach (int i in VanillaOrder(inv.Count))
            if (inv[i].Matches(useAmmo)) return i;
        return -1;
    }

    /// <summary>The first slot (vanilla order) holding <paramref name="itemType"/> with a stack left, or -1.</summary>
    public static int FirstOfType(IReadOnlyList<AmmoSlot> inv, int itemType)
    {
        foreach (int i in VanillaOrder(inv.Count))
            if (inv[i].ItemType == itemType && inv[i].Stack > 0) return i;
        return -1;
    }

    /// <summary>What to do so a weapon using <paramref name="useAmmo"/> fires <paramref name="itemType"/> next.</summary>
    public static AmmoPlan Plan(IReadOnlyList<AmmoSlot> inv, int useAmmo, int itemType)
    {
        int want = FirstOfType(inv, itemType);
        if (want < 0) return new AmmoPlan(AmmoPlanKind.Missing);
        if (inv[want].AmmoType != useAmmo) return new AmmoPlan(AmmoPlanKind.WrongAmmo);
        int first = FirstUsed(inv, useAmmo);
        return first == want ? new AmmoPlan(AmmoPlanKind.Ready) : new AmmoPlan(AmmoPlanKind.Swap, first, want);
    }

    /// <summary>Other ammo the weapon would fire instead (the first matching stack of a different item), or -1.</summary>
    public static int Alternative(IReadOnlyList<AmmoSlot> inv, int useAmmo, int notItemType)
    {
        foreach (int i in VanillaOrder(inv.Count))
            if (inv[i].Matches(useAmmo) && inv[i].ItemType != notItemType) return i;
        return -1;
    }
}

/// <summary>
/// Slot swaps that can be undone: <see cref="Restore"/> swaps back in reverse order, each only if both items are
/// still where the swap left them (same object references), so nothing the player moved since gets shuffled.
/// </summary>
public sealed class SwapJournal<T> where T : class
{
    private readonly List<(int A, int B, T InA, T InB)> _swaps = new();

    public int Count => _swaps.Count;

    public void Swap(T[] slots, int a, int b)
    {
        if (a == b) return;
        (slots[a], slots[b]) = (slots[b], slots[a]);
        _swaps.Add((a, b, slots[a], slots[b]));
    }

    /// <summary>Undo the swaps (newest first); returns how many were undone.</summary>
    public int Restore(T[] slots)
    {
        int n = 0;
        for (int i = _swaps.Count - 1; i >= 0; i--)
        {
            var (a, b, inA, inB) = _swaps[i];
            if (!ReferenceEquals(slots[a], inA) || !ReferenceEquals(slots[b], inB)) continue;
            (slots[a], slots[b]) = (slots[b], slots[a]);
            n++;
        }
        _swaps.Clear();
        return n;
    }
}

/// <summary>Why a chosen weapon can't be used right now.</summary>
public enum WeaponIssue { None, Gone, NoAmmo, NoMana, Unusable }

public enum WeaponDecisionKind
{
    /// <summary>Use the weapon the LLM chose.</summary>
    UseChosen,
    /// <summary>It's unusable: use <see cref="WeaponDecision.Slot"/> instead.</summary>
    Fallback,
    /// <summary>It's unusable and fallback is off (or nothing else works): stop with <see cref="WeaponDecision.Reason"/>.</summary>
    Stop,
}

public readonly record struct WeaponDecision(WeaponDecisionKind Kind, int Slot = -1, string? Reason = null);

/// <summary>
/// The LLM picks the weapon; the mod only falls back when that weapon can't be used any more (gone, out of ammo or
/// mana), and only if fallback is allowed. The fallback is the best alternative by <see cref="WeaponScoring"/>
/// (defense-aware), preferring the style the strategy needs.
/// </summary>
public static class WeaponFallback
{
    public static WeaponIssue Diagnose(bool present, bool needsAmmo, bool hasAmmo, bool needsMana, bool hasMana, bool usable)
    {
        if (!present) return WeaponIssue.Gone;
        if (needsAmmo && !hasAmmo) return WeaponIssue.NoAmmo;
        if (needsMana && !hasMana) return WeaponIssue.NoMana;
        return usable ? WeaponIssue.None : WeaponIssue.Unusable;
    }

    public static string Describe(WeaponIssue i) => i switch
    {
        WeaponIssue.Gone => "no longer in the inventory",
        WeaponIssue.NoAmmo => "out of ammo",
        WeaponIssue.NoMana => "out of mana",
        WeaponIssue.Unusable => "can't be used right now",
        _ => "usable",
    };

    /// <param name="chosenSlot">Where the chosen weapon is now (ignored when gone).</param>
    /// <param name="alternatives">Every other weapon candidate (the chosen one is skipped by slot).</param>
    public static WeaponDecision Decide(string chosenName, int chosenSlot, WeaponIssue issue, bool allowFallback,
        IEnumerable<WeaponCandidate> alternatives, int targetDefense = 0, WeaponStyle? prefer = null)
    {
        if (issue == WeaponIssue.None) return new WeaponDecision(WeaponDecisionKind.UseChosen, chosenSlot);
        string why = $"{chosenName} unusable ({Describe(issue)})";
        if (!allowFallback) return new WeaponDecision(WeaponDecisionKind.Stop, Reason: why + ", fallback is off");
        var others = alternatives.Where(w => issue == WeaponIssue.Gone || w.Slot != chosenSlot);
        return Pick(others, targetDefense, prefer) is { } best
            ? new WeaponDecision(WeaponDecisionKind.Fallback, best.Slot, why)
            : new WeaponDecision(WeaponDecisionKind.Stop, Reason: why + ", and no other usable weapon");
    }

    /// <summary>The best usable weapon of the <paramref name="prefer"/>red style, else the best of any style.</summary>
    public static WeaponCandidate? Pick(IEnumerable<WeaponCandidate> weapons, int targetDefense = 0, WeaponStyle? prefer = null)
    {
        var list = weapons as IReadOnlyCollection<WeaponCandidate> ?? weapons.ToList();
        if (prefer is { } style && WeaponScoring.Best(list.Where(w => w.Style == style), targetDefense) is { } p) return p;
        return WeaponScoring.Best(list, targetDefense);
    }

    /// <param name="who">"attack" or "auto-defend": the start of the recent-event line.</param>
    public static string SwitchMessage(string chosen, WeaponIssue issue, string other, string who = "attack") =>
        $"{who}: {chosen} unusable ({Describe(issue)}), switched to {other}";

    public static string BackMessage(string chosen, string who = "attack") => $"{who}: {chosen} usable again, switched back";

    public static string AmmoSwitchMessage(string wanted, string other, string who = "attack") =>
        $"{who}: {wanted} ran out, firing {other}";

    /// <summary>
    /// After a fallback, when may the chosen weapon come back? Ammo / presence: as soon as it is usable. Mana: once
    /// there is mana for <see cref="ManaCastsToReturn"/> casts (or a full bar), so it doesn't flip back and forth with
    /// every regenerated point.
    /// </summary>
    public static bool MayReturn(WeaponIssue lastIssue, int mana, int manaMax, int manaCost) =>
        lastIssue != WeaponIssue.NoMana || mana >= Math.Min(manaMax, manaCost * ManaCastsToReturn);

    public const int ManaCastsToReturn = 4;
}
