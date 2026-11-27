using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>
/// "Select an item temporarily, then put things back." Hotbar items (slots 0-9) are selected by changing
/// <c>Player.selectedItem</c>; an item in the main inventory is swapped into the selected hotbar slot. Selection
/// only changes between uses (itemAnimation == 0 and itemTime == 0), like vanilla's hotbar. <see cref="Restore"/>
/// undoes the swap (if both items are still where we left them) and reselects the originally held slot.
/// Use one lease per task; call <see cref="Restore"/> when the task ends or is cancelled. Main thread only.
/// </summary>
public sealed class HeldItemLease
{
    private int _originalSelected = -1;
    private int _swapA = -1, _swapB = -1;
    private Item? _itemInA, _itemInB;

    public bool Active => _originalSelected >= 0;

    /// <summary>Hold inventory slot <paramref name="slot"/> (0..57). Returns true once it is the held item.</summary>
    public bool Hold(Player p, int slot)
    {
        if (slot < 0 || slot >= 58 || p.inventory[slot].IsAir) return false;
        if (IsHeld(p, slot)) return true;
        if (p.itemAnimation > 0 || p.itemTime > 0 || p.channel) return false;

        if (_originalSelected < 0) _originalSelected = p.selectedItem;
        UndoSwap(p);
        if (slot < 10)
        {
            p.selectedItem = slot;
            return true;
        }
        int target = p.selectedItem is >= 0 and < 10 ? p.selectedItem : _originalSelected is >= 0 and < 10 ? _originalSelected : 0;
        (p.inventory[slot], p.inventory[target]) = (p.inventory[target], p.inventory[slot]);
        _swapA = target;
        _swapB = slot;
        _itemInA = p.inventory[target];
        _itemInB = p.inventory[slot];
        p.selectedItem = target;
        return true;
    }

    /// <summary>Is the item that was in <paramref name="slot"/> (before any swap of ours) the held item?</summary>
    public bool IsHeld(Player p, int slot)
    {
        if (_swapB == slot && _swapA >= 0) return p.selectedItem == _swapA && ReferenceEquals(p.inventory[_swapA], _itemInA);
        return p.selectedItem == slot;
    }

    public void Restore(Player p)
    {
        if (_originalSelected < 0) return;
        UndoSwap(p);
        p.selectedItem = _originalSelected;
        _originalSelected = -1;
    }

    private void UndoSwap(Player p)
    {
        if (_swapA < 0) return;
        if (ReferenceEquals(p.inventory[_swapA], _itemInA) && ReferenceEquals(p.inventory[_swapB], _itemInB))
            (p.inventory[_swapA], p.inventory[_swapB]) = (p.inventory[_swapB], p.inventory[_swapA]);
        _swapA = _swapB = -1;
        _itemInA = _itemInB = null;
    }

    // ---- item choice -------------------------------------------------------------------------------------------

    /// <summary>Inventory slot (0..57, coins excluded) of the pickaxe with the highest pick power, or -1.</summary>
    public static int BestPickaxe(Player p)
    {
        int best = -1, power = 0;
        for (int i = 0; i < 58; i++)
        {
            var it = p.inventory[i];
            if (it.IsAir || it.pick <= power) continue;
            best = i;
            power = it.pick;
        }
        return best;
    }

    public static int PickPower(Player p) => BestPickaxe(p) is >= 0 and var s ? p.inventory[s].pick : 0;

    /// <summary>Cheap blocks the pathfinder may build with, after any platform.</summary>
    private static readonly int[] CheapBlocks = { ItemID.DirtBlock, ItemID.MudBlock, ItemID.StoneBlock, ItemID.AshBlock, ItemID.ClayBlock, ItemID.Wood };

    public static bool IsPlatform(Item it) => !it.IsAir && it.createTile >= 0 && TileID.Sets.Platforms[it.createTile];

    public static bool IsBuildItem(Item it) => IsPlatform(it) || (!it.IsAir && it.createTile >= 0 && Array.IndexOf(CheapBlocks, it.type) >= 0);

    /// <summary>Slot of the item to build with: any platform first, then the cheapest block; -1 if none.</summary>
    public static int BuildItem(Player p)
    {
        for (int i = 0; i < 58; i++)
            if (IsPlatform(p.inventory[i])) return i;
        foreach (int type in CheapBlocks)
            for (int i = 0; i < 58; i++)
                if (!p.inventory[i].IsAir && p.inventory[i].type == type) return i;
        return -1;
    }

    /// <summary>Slot of the item a walk's build step places (<see cref="Core.Build.BuildReserve.Pick"/>): BuildDown
    /// takes a platform whenever there is one; bridges and climbs use platforms above <paramref name="reserve"/>, then
    /// cheap blocks, then the reserve. -1 if none.</summary>
    public static int BuildItemFor(Player p, bool buildDown, int reserve)
    {
        var pick = Core.Build.BuildReserve.Pick(buildDown, PlatformCount(p), BlockCount(p), reserve);
        if (pick == Core.Build.BuildPick.None) return -1;
        if (pick == Core.Build.BuildPick.Platform)
        {
            for (int i = 0; i < 58; i++)
                if (IsPlatform(p.inventory[i])) return i;
            return -1;
        }
        foreach (int type in CheapBlocks)
            for (int i = 0; i < 58; i++)
                if (!p.inventory[i].IsAir && p.inventory[i].type == type) return i;
        return -1;
    }

    public static int PlatformCount(Player p)
    {
        int n = 0;
        for (int i = 0; i < 58; i++)
            if (IsPlatform(p.inventory[i])) n += p.inventory[i].stack;
        return n;
    }

    /// <summary>Cheap blocks a walk may build with (dirt, mud, stone, ash, clay, wood).</summary>
    public static int BlockCount(Player p) => BuildItemCount(p) - PlatformCount(p);

    public static int BuildItemCount(Player p)
    {
        int n = 0;
        for (int i = 0; i < 58; i++)
            if (IsBuildItem(p.inventory[i])) n += p.inventory[i].stack;
        return n;
    }
}
