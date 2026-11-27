using LlmPlayer.Bridge;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Inventory;
using LlmPlayer.Observe;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Inventory;

/// <summary>
/// Full-inventory glue (rules in Core's <see cref="FullInventory"/>). Main thread only.
/// <list type="bullet">
/// <item><see cref="Tick"/> (TaskHost, every <see cref="ScanTicks"/> ticks while a task runs): visible dropped items
/// near the player (<see cref="NearRadius"/> tiles) that are meant for this player but don't fit
/// (<c>Player.ItemSpace</c>) are noticed once each: an <c>inventory_full</c> event and a recent-events line.</item>
/// <item>Auto-junk (set_reflexes <c>trash_when_full</c>, default empty): for a noticed valuable item, one stack of a
/// listed item goes to vanilla's trash slot (as drop_item trash does), at most <see cref="FullInventory.MaxTrashesPerTask"/>
/// per task, with a <c>reflex {kind: "auto_trash"}</c> event.</item>
/// <item><see cref="FinishNote"/> (TaskRunner, when a task ends): the noticed items still lying there and still not
/// fitting, as "inventory full: … not picked up — drop/trash something (e.g. …)". pick_up_items also notices the
/// items in its whole radius.</item>
/// </list>
/// </summary>
public static class InventorySpace
{
    public const int ScanTicks = 15, NearRadius = 12;

    /// <summary>Junk to trash when a valuable item doesn't fit (set_reflexes trash_when_full; in memory only).</summary>
    public static List<string> TrashWhenFull { get; set; } = new();

    private static readonly Dictionary<int, int> Noticed = new(); // item index -> type
    private static int _ticks, _trashes;

    /// <summary>Empty main inventory slots (0-49).</summary>
    public static int FreeSlots(Player p)
    {
        int n = 0;
        for (int i = 0; i < FullInventory.MainSlots; i++)
            if (p.inventory[i] == null || p.inventory[i].IsAir) n++;
        return n;
    }

    /// <summary>A new task starts: forget what the previous one noticed.</summary>
    public static void BeginTask()
    {
        Noticed.Clear();
        _trashes = 0;
        _ticks = 0;
    }

    /// <summary>Dropped item <paramref name="i"/> is for this player and visible, but there's no room for it.</summary>
    public static bool Blocked(Player p, int i)
    {
        var it = Main.item[i];
        if (!it.active || it.IsAir || it.playerIndexTheItemIsReservedFor != p.whoAmI) return false;
        if (ItemID.Sets.IsAPickup[it.type] || p.ItemSpace(it).CanTakeItem) return false;
        return Visibility.IsEntityVisible(it, Visibility.Screen, p);
    }

    public static void Tick(Player p)
    {
        if (++_ticks < ScanTicks) return;
        _ticks = 0;
        if (FreeSlots(p) > 0) return; // a free slot takes anything
        var (px, py) = StateReader.StandingTile(p);
        long r2 = (long)NearRadius * NearRadius;
        for (int i = 0; i < Main.maxItems; i++)
        {
            var it = Main.item[i];
            if (!it.active) continue;
            var (x, y) = EntityReader.TileOf(it);
            long dx = x - px, dy = y - py;
            if (dx * dx + dy * dy > r2 || !Blocked(p, i)) continue;
            Notice(p, i);
        }
    }

    /// <summary>Note dropped item <paramref name="i"/> as left behind (once per item), and try the auto-junk policy.
    /// True when a stack was trashed to make room (the caller may retry the pickup).</summary>
    public static bool Notice(Player p, int i)
    {
        var it = Main.item[i];
        bool trashed = TryMakeRoom(p, it);
        if (trashed) return true;
        if (Noticed.TryGetValue(i, out int type) && type == it.type) return false;
        Noticed[i] = it.type;
        var (x, y) = EntityReader.TileOf(it);
        string? hint = Suggest(p);
        BridgeSystem.Emit(EventNames.InventoryFull, new { Item = it.Name, it.stack, X = x, Y = y, FreeSlots = FreeSlots(p), Suggestion = hint });
        RecentEvents.Add($"inventory full: {it.Name}{(it.stack > 1 ? $" x{it.stack}" : "")} not picked up at ({x},{y})");
        return false;
    }

    /// <summary>The auto-junk policy for <paramref name="incoming"/>: trash one listed stack. True if one was trashed.</summary>
    public static bool TryMakeRoom(Player p, Item incoming)
    {
        if (TrashWhenFull.Count == 0 || _trashes >= FullInventory.MaxTrashesPerTask) return false;
        int slot = FullInventory.ChooseTrash(Slots(p), TrashWhenFull, Traits(incoming));
        if (slot < 0) return false;
        var it = p.inventory[slot];
        string name = it.Name;
        int count = it.stack;
        p.trashItem = it.Clone(); // vanilla's trash slot: replaces what was there (gone for good)
        it.TurnToAir();
        _trashes++;
        BridgeSystem.Emit(EventNames.Reflex, new { Kind = "auto_trash", Trashed = name, Count = count, Slot = slot, For = incoming.Name });
        RecentEvents.Add($"reflex: trashed {name} x{count} (inventory full) to pick up {incoming.Name}");
        return true;
    }

    /// <summary>For TaskRunner.FinishNote: the noticed items still there and still not fitting, then forget them.</summary>
    public static string? FinishNote()
    {
        var p = Main.LocalPlayer;
        var left = new List<LeftItem>();
        if (p is { active: true })
            foreach (var (i, type) in Noticed)
            {
                var it = Main.item[i];
                if (!it.active || it.type != type || !Blocked(p, i)) continue;
                var (x, y) = EntityReader.TileOf(it);
                left.Add(new LeftItem(it.Name, it.stack, x, y));
            }
        string? note = left.Count > 0 && p != null ? FullInventory.Note(left, Suggest(p)) : null;
        BeginTask();
        return note;
    }

    private static string? Suggest(Player p) => FullInventory.Suggestion(Slots(p), TrashWhenFull);

    public static ItemWorth Traits(Item it) => new(it.Name, it.IsACoin,
        it.healLife > 0 || it.healMana > 0 || (it.buffType > 0 && it.consumable), it.rare,
        ItemID.Sets.BossBag[it.type] || it.questItem);

    private static List<InvSlot> Slots(Player p)
    {
        var list = new List<InvSlot>();
        bool busy = p.itemAnimation > 0 || p.itemTime > 0;
        for (int i = 0; i < FullInventory.MainSlots; i++)
        {
            var it = p.inventory[i];
            if (it == null || it.IsAir) continue;
            list.Add(new InvSlot(i, it.Name, it.stack, it.favorited, FullInventory.IsValuable(Traits(it)),
                it.createTile >= 0 || it.createWall > 0, InUse: busy && i == p.selectedItem));
        }
        return list;
    }
}
