using System.Text.Json;
using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Observe;
using LlmPlayer.Observe;
using LlmPlayer.Safety;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;

namespace LlmPlayer.Commands;

/// <summary>select_hotbar, equip, unequip (instant actions); use_item, place (tasks).</summary>
[CommandGroup]
public static class InventoryCommands
{
    public static void Register()
    {
        CommandRouter.Register("select_hotbar", p =>
        {
            int slot = Params.Int(p, "slot");
            if (slot is < 0 or > 9) throw new CommandError(ErrorCodes.BadParams, $"slot must be 0..9, got {slot}");
            var pl = Main.LocalPlayer;
            if (pl.itemAnimation > 0 || pl.itemTime > 0 || pl.channel)
                throw new CommandError(ErrorCodes.BadParams, "can't switch items in the middle of a use; try again in a moment");
            pl.selectedItem = slot;
            var held = pl.inventory[slot];
            return new { Selected = slot, Item = held.IsAir ? null : held.Name };
        }, isAction: true);

        CommandRouter.Register("equip", p => Params.Has(p, "slot")
            ? EquipToSlot(Main.LocalPlayer, Params.String(p, "item"), Params.Int(p, "slot"))
            : Equip(Main.LocalPlayer, Params.String(p, "item")), isAction: true);

        // Throw items out of the inventory, like the vanilla "throw" key (the stack lands at the player's feet as a
        // normal dropped item). Frees slots when the inventory is full (seen in game: a crafted bow fell on the floor).
        // Swap two inventory slots (0-49 inventory incl. hotbar 0-9, 50-53 coins, 54-57 ammo), like dragging an item
        // onto another slot. Ammo is used from the ammo slots left to right, so this picks which arrows fire first.
        CommandRouter.Register("swap_slots", p =>
        {
            var pl = Main.LocalPlayer;
            int a = Params.Int(p, "a"), b = Params.Int(p, "b");
            if (a < 0 || a > 57 || b < 0 || b > 57 || a == b) throw new CommandError(ErrorCodes.BadParams, "a and b must be different slots 0..57");
            if (pl.itemAnimation > 0 && (a == pl.selectedItem || b == pl.selectedItem))
                throw new CommandError(ErrorCodes.BadParams, "the held item is in use right now");
            bool coinOk(int s, Item it) => s is < 50 or > 53 || it.IsAir || it.IsACoin;
            bool ammoOk(int s, Item it) => s < 54 || it.IsAir || it.ammo > 0;
            var ia = pl.inventory[a]; var ib = pl.inventory[b];
            if (!coinOk(b, ia) || !coinOk(a, ib)) throw new CommandError(ErrorCodes.BadParams, "coin slots (50-53) only take coins");
            if (!ammoOk(b, ia) || !ammoOk(a, ib)) throw new CommandError(ErrorCodes.BadParams, "ammo slots (54-57) only take ammo");
            (pl.inventory[a], pl.inventory[b]) = (ib, ia);
            return new { Swapped = new[] { new { Slot = a, Item = pl.inventory[a].IsAir ? null : pl.inventory[a].Name },
                                           new { Slot = b, Item = pl.inventory[b].IsAir ? null : pl.inventory[b].Name } } };
        }, isAction: true);

        CommandRouter.Register("drop_item", p =>
        {
            var pl = Main.LocalPlayer;
            int slot = ItemLookup.FindSlot(pl, Params.String(p, "item"));
            var it = pl.inventory[slot];
            if (it.favorited) throw new CommandError(ErrorCodes.BadParams, $"{it.Name} is favorited; refusing to drop it");
            if (pl.itemAnimation > 0 && slot == pl.selectedItem)
                throw new CommandError(ErrorCodes.BadParams, $"{it.Name} is being used right now");
            int count = Params.Has(p, "count") ? Math.Clamp(Params.Int(p, "count"), 1, it.stack) : it.stack;
            string name = it.Name;
            // trash: vanilla's trash slot (the item goes there and is gone once something else is trashed). Thrown items
            // get picked straight back up when walking over them (seen in game), trash doesn't.
            if (Params.Has(p, "trash") && Params.Bool(p, "trash"))
            {
                var trashed = it.Clone();
                trashed.stack = count;
                pl.trashItem = trashed;
                it.stack -= count;
                if (it.stack <= 0) it.TurnToAir();
                return new { Trashed = name, Count = count, Slot = slot };
            }
            var dropped = it.Clone();
            dropped.stack = count;
            int idx = Item.NewItem(pl.GetSource_DropAsItem(), pl.getRect(), dropped);
            if (idx >= 0 && idx < Main.maxItems)
            {
                Main.item[idx].noGrabDelay = 100; // like a thrown item: not picked straight back up
                Main.item[idx].velocity.X = pl.direction * 3f;
                LlmPlayer.Tasks.PickUpTask.Ignore(idx); // pick_up_items leaves things the AI threw away
            }
            it.stack -= count;
            if (it.stack <= 0) it.TurnToAir();
            return new { Dropped = name, Count = count, Slot = slot };
        }, isAction: true);

        CommandRouter.Register("unequip", p =>
        {
            bool hasItem = Params.Has(p, "item"), hasSlot = Params.Has(p, "slot");
            if (hasItem == hasSlot) throw new CommandError(ErrorCodes.BadParams, "give exactly one of item or slot");
            return Unequip(Main.LocalPlayer, hasItem ? Params.String(p, "item") : null, hasSlot ? Params.Int(p, "slot") : -1);
        }, isAction: true);

        CommandRouter.Register("use_item", p =>
        {
            var pl = Main.LocalPlayer;
            int slot = ResolveItemOrSlot(pl, p);
            var item = pl.inventory[slot];
            TilePoint? aim = null;
            if (Params.Has(p, "x") || Params.Has(p, "y"))
            {
                var t = Target(p);
                if (!Visibility.IsTileVisible(t.X, t.Y))
                    throw new CommandError(ErrorCodes.NotVisible, $"{t} is not visible");
                // Breaking tools respect full protection (incl. spawn area / houses); placing only
                // needs the target itself not to be a protected object — placing into empty space is harmless.
                bool breaks = item.pick > 0 || item.axe > 0 || item.hammer > 0;
                bool places = item.createTile >= 0 || item.createWall > 0;
                if ((breaks && Protection.IsProtected(t.X, t.Y)) || (places && IsProtectedObject(t.X, t.Y)))
                    throw new CommandError(ErrorCodes.ProtectedTile, $"{t} is protected (house, spawn area, furniture or station)");
                aim = t;
            }
            else if (item.pick > 0 || item.axe > 0 || item.hammer > 0 || item.createTile >= 0 || item.createWall > 0)
            {
                throw new CommandError(ErrorCodes.BadParams, $"{item.Name} works on tiles: give x and y to aim it (or use mine/place)");
            }
            if (LlmPlayer.Tasks.CombatKit.IsExplosive(item.shoot))
                throw new CommandError(ErrorCodes.BadParams, $"{item.Name} is an explosive: explosives are disabled because they destroy tiles, furniture and walls in a radius");
            if (item.potion && pl.potionDelay > 0)
                throw new CommandError(ErrorCodes.BadParams, $"potion sickness: can't drink {item.Name} for another {(pl.potionDelay + 59) / 60}s");
            var task = new UseItemTask(pl, slot, aim);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Item = item.Name };
        }, isAction: true);

        CommandRouter.Register("place", p =>
        {
            var pl = Main.LocalPlayer;
            int slot = ItemLookup.FindSlot(pl, Params.String(p, "item"));
            var item = pl.inventory[slot];
            if (item.createTile < 0 && item.createWall <= 0)
                throw new CommandError(ErrorCodes.BadParams, $"{item.Name} can't be placed");
            var t = Target(p);
            if (!Visibility.IsTileVisible(t.X, t.Y))
                throw new CommandError(ErrorCodes.NotVisible, $"{t} is not visible");
            if (IsProtectedObject(t.X, t.Y))
                throw new CommandError(ErrorCodes.ProtectedTile, $"{t} holds a protected object (furniture, door, chest or station)");
            var tile = Main.tile[t.X, t.Y];
            if (item.createTile >= 0)
            {
                if (tile.HasTile && !Main.tileCut[tile.TileType])
                    throw new CommandError(ErrorCodes.BadParams, $"{t} is not empty (mine it first or pick another tile)");
            }
            else if (tile.WallType != WallID.None)
            {
                throw new CommandError(ErrorCodes.BadParams, $"{t} already has a wall");
            }
            var task = new PlaceTask(pl, slot, t);
            TaskHost.Start(task);
            return new { Started = true, Task = task.Name, Item = item.Name };
        }, isAction: true);
    }

    /// <summary>
    /// Equip armor or an accessory through vanilla's own right-click equip (<c>ItemSlot.SwapEquip</c>, inventory
    /// context), so the game's rules apply unchanged:
    /// <list type="bullet">
    /// <item>Armor goes to its slot (head/body/legs by headSlot/bodySlot/legSlot; vanity armor to the vanity slot),
    /// swapping out what was there.</item>
    /// <item>An accessory goes to the first empty accessory slot the player has unlocked and can use
    /// (<c>Player.IsItemSlotUnlockedAndUsable</c>: the 6th/7th slots need expert/master or the Demon Heart), then
    /// empty modded accessory slots. If none is empty it replaces an identical accessory or a conflicting one
    /// (e.g. other wings); failing that, it swaps with the first accessory slot.</item>
    /// <item>A swapped-out item lands in the inventory slot the new item came from, so nothing is ever destroyed.
    /// Equip-locked items and mod CanEquipAccessory vetoes are respected (equip fails with bad_params).</item>
    /// </list>
    /// </summary>
    private static object Equip(Player pl, string query)
    {
        int slot = ItemLookup.FindSlot(pl, query);
        var item = pl.inventory[slot];
        if (item.headSlot < 0 && item.bodySlot < 0 && item.legSlot < 0 && !item.accessory)
            throw new CommandError(ErrorCodes.BadParams, $"{item.Name} is not armor or an accessory");
        if (slot == pl.selectedItem && (pl.itemAnimation > 0 || pl.channel))
            throw new CommandError(ErrorCodes.BadParams, $"{item.Name} is being used right now; try again in a moment");

        string name = item.Name;
        var before = pl.armor.Select(Snapshot).ToArray();
        ItemSlot.SwapEquip(pl.inventory, ItemSlot.Context.InventoryItem, slot);

        int equippedAt = -1;
        for (int i = 0; i < pl.armor.Length; i++)
            if (Snapshot(pl.armor[i]) != before[i])
            {
                equippedAt = i;
                break;
            }
        var back = pl.inventory[slot];
        bool moved = !ReferenceEquals(back, item); // a refused swap puts the very same Item back
        if (equippedAt < 0 && !moved)
            throw new CommandError(ErrorCodes.BadParams, $"{name} couldn't be equipped (equip-locked, or a mod/game rule refused it)");

        return new
        {
            Equipped = name,
            ArmorSlot = equippedAt >= 0 ? equippedAt : (int?)null,
            SlotKind = SlotKind(equippedAt),
            SwappedOut = back.IsAir ? null : back.Name,
        };
    }

    /// <summary>First accessory slot (<c>p.armor</c> index) and one past the last functional one (3..9, as get_inventory
    /// numbers them).</summary>
    public const int FirstAccessorySlot = 3, EndAccessorySlot = 10;

    /// <summary>
    /// Put an accessory into a specific accessory slot (<c>p.armor</c> index 3-9, as get_inventory lists them), replacing
    /// what is there; the replaced item goes to the inventory slot the new one came from. Mirrors vanilla dropping an
    /// item from the cursor onto an accessory slot (ItemSlot.LeftClick, context EquipAccessory):
    /// <list type="bullet">
    /// <item>The slot must be unlocked and usable (<c>Player.IsItemSlotUnlockedAndUsable</c>: the 8th needs expert mode +
    /// Demon Heart, the 9th master mode).</item>
    /// <item><c>ItemSlot.AccCheck</c> (PickItemMovementAction): no duplicate of the same accessory elsewhere, no second pair
    /// of wings unless replacing wings; plus the mod rules <c>ItemLoader.CanAccessoryBeEquippedWith</c> against the
    /// other equipped accessories and <c>ItemLoader.CanEquipAccessory</c> for the slot.</item>
    /// <item>Then the swap, the equip sound and a recipe refresh, as vanilla. Modded extra accessory slots aren't
    /// addressable here (use equip without slot).</item>
    /// </list>
    /// </summary>
    private static object EquipToSlot(Player pl, string query, int armorSlot)
    {
        if (armorSlot < FirstAccessorySlot || armorSlot >= EndAccessorySlot)
            throw new CommandError(ErrorCodes.BadParams, $"slot must be an accessory slot {FirstAccessorySlot}..{EndAccessorySlot - 1} (as get_inventory numbers them); armor goes to its own slot (equip without slot)");
        int invSlot = ItemLookup.FindSlot(pl, query);
        var item = pl.inventory[invSlot];
        if (!item.accessory)
            throw new CommandError(ErrorCodes.BadParams, $"{item.Name} is not an accessory (slot is only for accessories)");
        if (invSlot == pl.selectedItem && (pl.itemAnimation > 0 || pl.channel))
            throw new CommandError(ErrorCodes.BadParams, $"{item.Name} is being used right now; try again in a moment");
        if (!pl.IsItemSlotUnlockedAndUsable(armorSlot))
            throw new CommandError(ErrorCodes.BadParams, $"accessory slot {armorSlot} is locked (slot 8 needs expert mode and the Demon Heart, slot 9 master mode)");
        if (ItemSlot.AccCheck(pl.armor, item, armorSlot))
            throw new CommandError(ErrorCodes.BadParams, $"{item.Name} can't go there: the same accessory is already equipped, or it would be a second pair of wings");
        for (int i = FirstAccessorySlot; i < EndAccessorySlot; i++)
            if (i != armorSlot && !pl.armor[i].IsAir && !ItemLoader.CanAccessoryBeEquippedWith(pl, pl.armor[i], item))
                throw new CommandError(ErrorCodes.BadParams, $"{item.Name} can't be worn together with {pl.armor[i].Name} (mod rule)");
        if (!ItemLoader.CanEquipAccessory(pl, item, armorSlot, false))
            throw new CommandError(ErrorCodes.BadParams, $"{item.Name} can't be equipped in slot {armorSlot} (a mod rule refused it)");

        var old = pl.armor[armorSlot];
        pl.armor[armorSlot] = item;
        pl.inventory[invSlot] = old.IsAir ? new Item() : old;
        Terraria.GameContent.Achievements.AchievementsHelper.HandleOnEquip(pl, item, ItemSlot.Context.EquipAccessory);
        SoundEngine.PlaySound(SoundID.Grab);
        Recipe.FindRecipes();
        return new
        {
            Equipped = item.Name,
            ArmorSlot = armorSlot,
            SlotKind = SlotKind(armorSlot),
            SwappedOut = old.IsAir ? null : old.Name,
            SwappedOutTo = old.IsAir ? (int?)null : invSlot,
        };
    }

    /// <summary>
    /// Take off an equipped item (<c>p.armor</c> 0-19: armor 0-2, accessories 3-9, vanity 10-19), by name or slot, into
    /// the first empty main inventory slot (10-49, then the hotbar), like picking it up from its slot and dropping it
    /// into an empty inventory slot. Fails with bad_params when the inventory has no empty slot (nothing is dropped).
    /// </summary>
    private static object Unequip(Player pl, string? query, int armorSlot)
    {
        if (query != null)
        {
            var names = new ItemName?[pl.armor.Length];
            for (int i = 0; i < pl.armor.Length; i++)
                if (!pl.armor[i].IsAir) names[i] = new ItemName(pl.armor[i].Name, ItemLookup.InternalName(pl.armor[i].type));
            armorSlot = ItemMatcher.Find(names, query);
            if (armorSlot < 0) throw new CommandError(ErrorCodes.MissingItem, $"no '{query.Trim()}' equipped");
        }
        else if (armorSlot < 0 || armorSlot >= pl.armor.Length)
        {
            throw new CommandError(ErrorCodes.BadParams, $"slot must be 0..{pl.armor.Length - 1} (0-2 armor, 3-9 accessories, 10-19 vanity)");
        }
        var item = pl.armor[armorSlot];
        if (item.IsAir) throw new CommandError(ErrorCodes.MissingItem, $"equipment slot {armorSlot} is empty");

        int free = -1;
        for (int i = 10; i < 50 && free < 0; i++)
            if (pl.inventory[i].IsAir) free = i;
        for (int i = 0; i < 10 && free < 0; i++)
            if (pl.inventory[i].IsAir) free = i;
        if (free < 0) throw new CommandError(ErrorCodes.BadParams, $"inventory is full: no empty slot for {item.Name}");

        pl.inventory[free] = item;
        pl.armor[armorSlot] = new Item();
        SoundEngine.PlaySound(SoundID.Grab);
        Recipe.FindRecipes();
        return new { Unequipped = item.Name, FromSlot = armorSlot, SlotKind = SlotKind(armorSlot), InventorySlot = free };
    }

    private static (int Type, int Stack, int Prefix) Snapshot(Item it) => (it.type, it.stack, it.prefix);

    private static string SlotKind(int armorIndex) => armorIndex switch
    {
        0 => "head",
        1 => "body",
        2 => "legs",
        >= 3 and < 10 => "accessory",
        10 or 11 or 12 => "vanity armor",
        >= 13 and < 20 => "vanity accessory",
        < 0 => "modded accessory slot",
        _ => "equipment",
    };

    private static int ResolveItemOrSlot(Player pl, JsonElement p)
    {
        bool hasItem = Params.Has(p, "item"), hasSlot = Params.Has(p, "slot");
        if (hasItem == hasSlot) throw new CommandError(ErrorCodes.BadParams, "give exactly one of item or slot");
        if (hasItem) return ItemLookup.FindSlot(pl, Params.String(p, "item"));
        return ItemLookup.FindSlot(pl, Params.Int(p, "slot").ToString());
    }

    private static TilePoint Target(JsonElement p)
    {
        if (!Params.Has(p, "x") || !Params.Has(p, "y")) throw new CommandError(ErrorCodes.BadParams, "give both x and y");
        int x = Params.Int(p, "x"), y = Params.Int(p, "y");
        if (!WorldGen.InWorld(x, y, 10)) throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
        return new TilePoint(x, y);
    }

    /// <summary>Protected because of what is on the tile (not where it is).</summary>
    private static bool IsProtectedObject(int x, int y) =>
        !WorldGen.InWorld(x, y)
        || (Main.tile[x, y].HasTile && LlmPlayer.Observe.TileClass.IsProtectedType(Main.tile[x, y].TileType));
}
