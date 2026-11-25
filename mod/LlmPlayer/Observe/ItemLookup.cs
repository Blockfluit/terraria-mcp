using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Observe;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Observe;

public static class ItemLookup
{
    /// <summary>Inventory slots searched: hotbar 0-9, main 10-49, coins 50-53, ammo 54-57.</summary>
    public const int SearchedSlots = 58;

    /// <summary>
    /// Slot index in <c>p.inventory</c> for an item name or a slot number ("0".."57").
    /// Names match the display name case-insensitively first, then the internal name ("LesserHealingPotion").
    /// Throws CommandError missing_item if no such item (or the numbered slot is empty), bad_params for an
    /// out-of-range number.
    /// </summary>
    public static int FindSlot(Player p, string itemOrSlot)
    {
        var q = itemOrSlot.Trim();
        if (int.TryParse(q, out int slot))
        {
            if (slot < 0 || slot >= SearchedSlots)
                throw new CommandError(ErrorCodes.BadParams, $"slot must be 0..{SearchedSlots - 1}, got {slot}");
            if (p.inventory[slot].IsAir)
                throw new CommandError(ErrorCodes.MissingItem, $"inventory slot {slot} is empty");
            return slot;
        }

        var names = new ItemName?[SearchedSlots];
        for (int i = 0; i < SearchedSlots; i++)
        {
            var it = p.inventory[i];
            if (!it.IsAir) names[i] = new ItemName(it.Name, InternalName(it.type));
        }
        int found = ItemMatcher.Find(names, q);
        if (found < 0) throw new CommandError(ErrorCodes.MissingItem, $"no '{q}' in inventory");
        return found;
    }

    /// <summary>"LesserHealingPotion" for vanilla, the ModItem's Name for modded.</summary>
    public static string InternalName(int type) =>
        ItemLoader.GetItem(type)?.Name ?? (ItemID.Search.TryGetName(type, out var n) ? n : type.ToString());
}
