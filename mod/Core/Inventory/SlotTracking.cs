namespace LlmPlayer.Core.Inventory;

/// <summary>
/// Where an item a task picked by slot number lives now. A task remembers both the slot and the Item object; other
/// code may have moved the object in between: starting a task cancels the running one, whose held-item lease then
/// swaps its borrowed item back (seen in game: place picked the torches while move_to had them swapped into the
/// selected hotbar slot; the swap-back left that slot empty and place waited 5 s on an empty slot, then failed with
/// "busy using another item"). The auto-defend reflex swaps weapons in and out the same way. Pure.
/// </summary>
public static class SlotTracking
{
    /// <summary>
    /// <paramref name="slot"/> if it still holds <paramref name="item"/> (same object), else the slot that does, else
    /// the first slot whose item <paramref name="sameKind"/> matches (the stack was merged or replaced), else -1.
    /// Null / <paramref name="isEmpty"/> entries never match.
    /// </summary>
    public static int Resolve<T>(IReadOnlyList<T?> slots, int slot, T item, Func<T, bool> isEmpty, Func<T, T, bool> sameKind)
        where T : class
    {
        if (slot >= 0 && slot < slots.Count && ReferenceEquals(slots[slot], item) && !isEmpty(item)) return slot;
        for (int i = 0; i < slots.Count; i++)
            if (ReferenceEquals(slots[i], item) && !isEmpty(item)) return i;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] is { } s && !isEmpty(s) && sameKind(s, item)) return i;
        return -1;
    }
}
