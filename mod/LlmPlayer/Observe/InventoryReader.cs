using Terraria;
using Terraria.ID;

namespace LlmPlayer.Observe;

public sealed record SlotInfo(int Slot, string Name, int Stack);

public sealed record CoinInfo(int Copper, int Silver, int Gold, int Platinum);

public sealed record InventoryInfo(
    List<SlotInfo> Hotbar, List<SlotInfo> Inventory, List<SlotInfo> Armor, List<SlotInfo> Accessories,
    List<SlotInfo> Ammo, CoinInfo Coins, int SelectedSlot);

/// <summary>
/// get_inventory. Slot numbers: hotbar/inventory/ammo use the <c>p.inventory</c> index (hotbar 0-9, main 10-49,
/// ammo 54-57); armor (0-2) and accessories (3-9) use the <c>p.armor</c> index. Coins are summed over all
/// inventory slots, and coin items are left out of the slot lists. Empty slots are omitted.
/// </summary>
public static class InventoryReader
{
    public static InventoryInfo Read(Player p) => new(
        Slots(p.inventory, 0, 10),
        Slots(p.inventory, 10, 50),
        Slots(p.armor, 0, 3),
        Slots(p.armor, 3, 10),
        Slots(p.inventory, 54, 58),
        Coins(p),
        p.selectedItem);

    private static List<SlotInfo> Slots(Item[] items, int from, int to)
    {
        var list = new List<SlotInfo>();
        for (int i = from; i < to && i < items.Length; i++)
        {
            var it = items[i];
            if (it == null || it.IsAir || IsCoin(it.type)) continue;
            list.Add(new SlotInfo(i, it.Name, it.stack));
        }
        return list;
    }

    private static bool IsCoin(int type) => type is ItemID.CopperCoin or ItemID.SilverCoin or ItemID.GoldCoin or ItemID.PlatinumCoin;

    private static CoinInfo Coins(Player p)
    {
        int c = 0, s = 0, g = 0, pl = 0;
        for (int i = 0; i < 58 && i < p.inventory.Length; i++)
        {
            var it = p.inventory[i];
            switch (it.type)
            {
                case ItemID.CopperCoin: c += it.stack; break;
                case ItemID.SilverCoin: s += it.stack; break;
                case ItemID.GoldCoin: g += it.stack; break;
                case ItemID.PlatinumCoin: pl += it.stack; break;
            }
        }
        return new CoinInfo(c, s, g, pl);
    }
}
