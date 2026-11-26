using System.Text.Json.Serialization;
using LlmPlayer.Core.Observe;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Observe;

/// <summary>One inventory slot. Tags (from <see cref="ItemTags"/>) are omitted unless true/non-zero.</summary>
public sealed record SlotInfo(int Slot, string Name, int Stack,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Weapon = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Damage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Heal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Ammo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Consumable = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Placeable = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Pick = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Axe = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Hammer = null);

public sealed record CoinInfo(int Copper, int Silver, int Gold, int Platinum);

public sealed record InventoryInfo(
    List<SlotInfo> Hotbar, List<SlotInfo> Inventory, List<SlotInfo> Armor, List<SlotInfo> Accessories,
    List<SlotInfo> Ammo, CoinInfo Coins, int SelectedSlot);

/// <summary>
/// get_inventory. Slot numbers: hotbar/inventory/ammo use the <c>p.inventory</c> index (hotbar 0-9, main 10-49,
/// ammo 54-57); armor (0-2) and accessories (3-9) use the <c>p.armor</c> index. Coins are summed over all
/// inventory slots, and coin items are left out of the slot lists. Empty slots are omitted. Hotbar, inventory and
/// ammo slots carry item tags (weapon+damage, heal, ammo, consumable, placeable, pick/axe/hammer power).
/// </summary>
public static class InventoryReader
{
    public static InventoryInfo Read(Player p) => new(
        Slots(p.inventory, 0, 10, p),
        Slots(p.inventory, 10, 50, p),
        Slots(p.armor, 0, 3),
        Slots(p.armor, 3, 10),
        Slots(p.inventory, 54, 58, p),
        Coins(p),
        p.selectedItem);

    /// <param name="tagsFor">Player to compute item tags for (weapon damage includes their bonuses); null = no tags.</param>
    private static List<SlotInfo> Slots(Item[] items, int from, int to, Player? tagsFor = null)
    {
        var list = new List<SlotInfo>();
        for (int i = from; i < to && i < items.Length; i++)
        {
            var it = items[i];
            if (it == null || it.IsAir || IsCoin(it.type)) continue;
            list.Add(tagsFor == null ? new SlotInfo(i, it.Name, it.stack) : Tagged(i, it, tagsFor));
        }
        return list;
    }

    private static SlotInfo Tagged(int slot, Item it, Player p)
    {
        bool weapon = CombatKit.IsWeapon(it);
        var t = ItemTags.Compute(new ItemTagInput(
            IsWeapon: weapon,
            WeaponDamage: weapon ? p.GetWeaponDamage(it) : 0,
            HealLife: it.healLife,
            IsAmmo: it.ammo > 0,
            Consumable: it.consumable,
            Places: it.createTile >= 0 || it.createWall >= 0,
            Pick: it.pick,
            Axe: it.axe,
            Hammer: it.hammer));
        return new SlotInfo(slot, it.Name, it.stack, t.Weapon, t.Damage, t.Heal, t.Ammo, t.Consumable, t.Placeable, t.Pick, t.Axe, t.Hammer);
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
