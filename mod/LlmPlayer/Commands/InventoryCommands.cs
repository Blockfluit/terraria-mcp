using System.Text.Json;
using LlmPlayer.Bridge;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Grid;
using LlmPlayer.Observe;
using LlmPlayer.Safety;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.UI;

namespace LlmPlayer.Commands;

/// <summary>select_hotbar, equip (instant actions); use_item, place (tasks).</summary>
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

        CommandRouter.Register("equip", p => Equip(Main.LocalPlayer, Params.String(p, "item")), isAction: true);

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
