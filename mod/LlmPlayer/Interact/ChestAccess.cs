using LlmPlayer.Core.Interact;
using LlmPlayer.Observe;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ObjectData;
using Terraria.UI;

namespace LlmPlayer.Interact;

/// <summary>The kinds of container vanilla's right-click handles (Player.TileInteractionsUse).</summary>
public enum ContainerKind { Chest, Dresser, PiggyBank, Safe, DefendersForge, VoidVault, Modded }

/// <summary>A container object in the world: its top-left tile, size and kind.</summary>
public readonly record struct ContainerRef(int X, int Y, int Width, int Height, int Type, ContainerKind Kind);

/// <summary>
/// Opening, reading, filling, emptying and closing containers through vanilla's own code paths (main thread only):
/// <list type="bullet">
/// <item>Open = the container branches of Player.TileInteractionsUse (decompiled 1.4.4): close sign/NPC chat and
/// chest renaming, then <c>Player.OpenChest</c> (sets <c>player.chest</c>, <c>chestX/Y</c>,
/// <c>Main.playerInventory = true</c>), the open sound, <c>Recipe.FindRecipes</c>, then <c>TileLoader.RightClick</c>
/// (GlobalTile hooks), exactly like a right-click. Chests and dressers (top row), piggy bank, safe, defender's forge,
/// void vault; modded containers go through <c>TileLoader.RightClick</c> (the mod opens them itself).</item>
/// <item>Locked chests fail ("locked"): vanilla would use up a key from the inventory, which the AI must not do
/// silently. Rigged chests (Dead Man's Chest, wired to explosives) are refused. A chest another player has open fails.
/// In multiplayer vanilla only sends a request (message 31) and the server opens it: the task waits for that.</item>
/// <item>Transfers: deposit all = <c>ChestUI.DepositAll</c> (main inventory 10-49 + coins; hotbar and favorites
/// stay), quick stack = <c>ChestUI.QuickStack</c> (only items the chest already holds), withdraw all =
/// <c>ChestUI.LootAll</c>; single items move like a shift-click: <c>ChestUI.TryPlacingInChest</c> into the
/// container, <c>Player.GetItem</c> into the inventory. Items are only ever moved; whatever doesn't fit stays put.</item>
/// <item>Vanilla closes the chest itself every frame once the player is out of range
/// (<c>Player.IsInInteractionRangeToMultiTileHitbox</c>) or the tile is gone; <see cref="Close"/> is the vanilla
/// "click it again" close (chest = -1, close sound, FindRecipes) and also closes the inventory if opening the chest
/// opened it.</item>
/// </list>
/// </summary>
public static class ChestAccess
{
    /// <summary>True if the inventory was closed when we opened the chest (so closing the chest closes it again).</summary>
    private static bool _openedInventory;

    public static bool IsOpen(Player p) => p.chest != -1;

    /// <summary>The container whose tile is at (x,y), or null if it isn't one vanilla (or a mod) can open.</summary>
    public static ContainerRef? Resolve(int x, int y)
    {
        if (!WorldGen.InWorld(x, y)) return null;
        var t = Main.tile[x, y];
        if (!t.HasTile) return null;
        int type = t.TileType, fx = t.TileFrameX, fy = t.TileFrameY;
        if (type == TileID.Dressers)
        {
            var (dx, dy) = InteractMath.DresserOrigin(x, y, fx, fy);
            return new ContainerRef(dx, dy, 3, 2, type, ContainerKind.Dresser);
        }
        if (type < TileID.Count && TileID.Sets.BasicChest[type])
        {
            var (cx, cy) = InteractMath.ChestOrigin(x, y, fx, fy);
            return new ContainerRef(cx, cy, 2, 2, type, ContainerKind.Chest);
        }
        if (type is TileID.PiggyBank or TileID.Safes)
        {
            var (cx, cy) = InteractMath.ChestOrigin(x, y, fx, fy);
            return new ContainerRef(cx, cy, 2, type == TileID.PiggyBank ? 1 : 2, type,
                type == TileID.PiggyBank ? ContainerKind.PiggyBank : ContainerKind.Safe);
        }
        if (type is TileID.DefendersForge or TileID.VoidVault)
        {
            var (ox, oy) = TileClass.ObjectOrigin(x, y, t);
            return new ContainerRef(ox, oy, 3, 3, type, type == TileID.DefendersForge ? ContainerKind.DefendersForge : ContainerKind.VoidVault);
        }
        if (type >= TileID.Count && (TileID.Sets.BasicChest[type] || Main.tileContainer[type]))
        {
            var (ox, oy) = TileClass.ObjectOrigin(x, y, t);
            var data = TileObjectData.GetTileData(type, 0);
            return new ContainerRef(ox, oy, Math.Max(1, data?.Width ?? 2), Math.Max(1, data?.Height ?? 2), type, ContainerKind.Modded);
        }
        return null;
    }

    /// <summary>The tile of the container to "click": one in vanilla interaction range of the player (top row for
    /// dressers, where a right-click opens the drawers), nearest first; null if none is in range.</summary>
    public static (int X, int Y)? ClickTile(Player p, ContainerRef c)
    {
        (int, int)? best = null;
        float bestD = float.MaxValue;
        int rows = c.Kind == ContainerKind.Dresser ? 1 : c.Height;
        for (int y = c.Y; y < c.Y + rows; y++)
            for (int x = c.X; x < c.X + c.Width; x++)
            {
                if (!p.IsInTileInteractionRange(x, y, TileReachCheckSettings.Simple)) continue;
                float d = Vector2Distance(p.Center.X, p.Center.Y, x * 16 + 8, y * 16 + 8);
                if (d < bestD)
                {
                    bestD = d;
                    best = (x, y);
                }
            }
        return best;
    }

    private static float Vector2Distance(float ax, float ay, float bx, float by) =>
        MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>The container's name as the hover text shows it: its custom name, else the style's name.</summary>
    public static string NameOf(ContainerRef c)
    {
        int idx = Chest.FindChest(c.X, c.Y);
        if (idx >= 0 && !string.IsNullOrEmpty(Main.chest[idx].name)) return Main.chest[idx].name;
        var t = Main.tile[c.X, c.Y];
        try
        {
            switch (c.Type)
            {
                case TileID.Containers:
                    return Lang.chestType[t.TileFrameX / 36].Value;
                case TileID.Containers2:
                    return Lang.chestType2[t.TileFrameX / 36].Value;
                case TileID.Dressers:
                    return Lang.dresserType[t.TileFrameX / 54].Value;
            }
            if (c.Kind == ContainerKind.Modded)
            {
                var n = TileLoader.DefaultContainerName(c.Type, t.TileFrameX, t.TileFrameY);
                if (!string.IsNullOrEmpty(n)) return n;
            }
        }
        catch (IndexOutOfRangeException)
        {
            // Unknown style: fall back to the tile name.
        }
        return TileClass.Name(c.Type);
    }

    /// <summary>Why this container can't be opened by the AI right now (locked, rigged, in use), or null.</summary>
    public static string? Refusal(Player p, ContainerRef c)
    {
        if ((c.Kind is ContainerKind.Chest or ContainerKind.Modded) && Chest.IsLocked(c.X, c.Y))
            return $"locked: {NameOf(c)} at ({c.X},{c.Y}) is locked (vanilla unlocks it with the matching key, which is not done automatically)";
        if (WorldGen.IsChestRigged(c.X, c.Y))
            return $"rigged: {NameOf(c)} at ({c.X},{c.Y}) is a trapped Dead Man's Chest (opening it sets off explosives); refused";
        if (c.Kind is ContainerKind.Chest or ContainerKind.Dresser)
        {
            int idx = Chest.FindChest(c.X, c.Y);
            if (idx < 0 && Main.netMode != NetmodeID.MultiplayerClient) return $"no chest data at ({c.X},{c.Y})";
            if (idx >= 0 && Chest.UsingChest(idx) is int user and >= 0 && user != p.whoAmI)
                return $"in_use: {Main.player[user].name} has this chest open";
        }
        if (c.Kind == ContainerKind.VoidVault && p.disableVoidBag >= 0)
            return "the void vault is disabled";
        return null;
    }

    /// <summary>The <c>player.chest</c> value the container opens as (bank ids are negative), or null when it
    /// depends on the server / a mod.</summary>
    public static int? ChestIndexOf(ContainerRef c) => c.Kind switch
    {
        ContainerKind.PiggyBank => -2,
        ContainerKind.Safe => -3,
        ContainerKind.DefendersForge => -4,
        ContainerKind.VoidVault => -5,
        ContainerKind.Chest or ContainerKind.Dresser when Chest.FindChest(c.X, c.Y) is int i and >= 0 => i,
        _ => null,
    };

    /// <summary>Is this container the one open right now?</summary>
    public static bool IsOpenContainer(Player p, ContainerRef c)
    {
        if (p.chest == -1) return false;
        if (ChestIndexOf(c) is int i) return p.chest == i && (p.chest < 0 || (p.chestX == c.X && p.chestY == c.Y));
        return p.chestX == c.X && p.chestY == c.Y;
    }

    /// <summary>
    /// Right-click the container at the click tile, as vanilla does. Returns null when the request went out
    /// (open now, or — multiplayer / modded — maybe a few ticks later: poll <see cref="IsOpenContainer"/>), else an
    /// error. An already-open container stays open (vanilla's second click would close it).
    /// </summary>
    public static string? Open(Player p, ContainerRef c, int clickX, int clickY)
    {
        if (Refusal(p, c) is { } why) return why;
        if (IsOpenContainer(p, c)) return null;
        bool invWasOpen = Main.playerInventory || p.chest != -1;

        if (c.Kind == ContainerKind.Modded)
        {
            TileLoader.RightClick(clickX, clickY);
            if (p.chest != -1) NoteOpened(invWasOpen);
            return null;
        }

        if (c.Kind == ContainerKind.Dresser) Main.CancelClothesWindow(quiet: true);
        Main.mouseRightRelease = false;
        p.CloseSign();
        p.SetTalkNPC(-1);
        Main.npcChatCornerItem = 0;
        Main.npcChatText = "";
        if (Main.editChest)
        {
            SoundEngine.PlaySound(SoundID.MenuTick);
            Main.editChest = false;
            Main.npcChatText = string.Empty;
        }
        if (p.editedChestName)
        {
            NetMessage.SendData(MessageID.ChestName, -1, -1, NetworkText.FromLiteral(Main.chest[p.chest].name), p.chest, 1f);
            p.editedChestName = false;
        }

        bool isChest = c.Kind is ContainerKind.Chest or ContainerKind.Dresser;
        if (Main.netMode == NetmodeID.MultiplayerClient && isChest)
        {
            NetMessage.SendData(MessageID.RequestChestOpen, -1, -1, null, c.X, c.Y);
            Main.stackSplit = 600;
            _openedInventory = !invWasOpen;
            return null;
        }
        if (c.Kind == ContainerKind.Dresser)
        {
            p.piggyBankProjTracker.Clear();
            p.voidLensChest.Clear();
        }
        int index = ChestIndexOf(c) ?? -1;
        if (index == -1) return $"no chest data at ({c.X},{c.Y})";

        Main.stackSplit = 600;
        if (p.chest == -1)
        {
            p.OpenChest(c.X, c.Y, index);
            SoundEngine.PlaySound(SoundID.MenuOpen);
        }
        else
        {
            p.OpenChest(c.X, c.Y, index);
            SoundEngine.PlaySound(SoundID.MenuTick);
        }
        Recipe.FindRecipes();
        if (c.Kind != ContainerKind.Dresser) TileLoader.RightClick(clickX, clickY);
        NoteOpened(invWasOpen);
        return null;
    }

    private static void NoteOpened(bool invWasOpen) => _openedInventory = !invWasOpen;

    /// <summary>Close the open container like vanilla's second right-click (and the inventory if opening the chest
    /// opened it). Returns false if none was open.</summary>
    public static bool Close(Player p)
    {
        if (p.chest == -1)
        {
            // Vanilla already closed it (walked out of reach): still close the inventory we opened with it.
            if (_openedInventory) Main.playerInventory = false;
            _openedInventory = false;
            return false;
        }
        p.chest = -1;
        SoundEngine.PlaySound(SoundID.MenuClose);
        if (_openedInventory) Main.playerInventory = false;
        _openedInventory = false;
        Recipe.FindRecipes();
        return true;
    }

    /// <summary>The open container's item array (chest or one of the banks), or null.</summary>
    public static Item[]? OpenItems(Player p)
    {
        if (p.chest == -1) return null;
        if (p.chest >= 0 && Main.chest[p.chest] == null) return null;
        ChestUI.GetContainerUsageInfo(out _, out var items);
        return items;
    }

    public static List<SlotItem> Snapshot(Item[] items)
    {
        var list = new List<SlotItem>();
        for (int i = 0; i < items.Length; i++)
            if (items[i] is { IsAir: false } it) list.Add(new SlotItem(i, it.Name, it.stack));
        return list;
    }

    /// <summary>{x, y, name} of the open container for get_state, or null.</summary>
    public static object? OpenInfo(Player p)
    {
        if (p.chest == -1) return null;
        var c = WorldGen.InWorld(p.chestX, p.chestY) ? Resolve(p.chestX, p.chestY) : null;
        string name = c is { } cr ? NameOf(cr) : p.chest switch
        {
            -2 => "Piggy Bank", -3 => "Safe", -4 => "Defender's Forge", -5 => "Void Vault", _ => "Chest",
        };
        return new { X = p.chestX, Y = p.chestY, Name = name };
    }

    // ---- transfers ---------------------------------------------------------------------------------------------

    /// <summary>Inventory slots a deposit may take from: hotbar, main, coins, ammo; never favorites, the mouse item,
    /// or the held item while it is being used.</summary>
    private static bool Depositable(Player p, int slot)
    {
        var it = p.inventory[slot];
        if (it.IsAir || it.favorited) return false;
        return !(slot == p.selectedItem && (p.itemAnimation > 0 || p.channel));
    }

    /// <summary>Move up to count (null = all) of the named item from the inventory into the open container, as
    /// shift-clicks would. Returns how many moved.</summary>
    public static int DepositItem(Player p, string query, int? count, out string? error)
    {
        error = null;
        var names = new Core.Observe.ItemName?[ItemLookup.SearchedSlots];
        bool anyFavorite = false;
        for (int i = 0; i < names.Length; i++)
        {
            var it = p.inventory[i];
            if (it.IsAir) continue;
            if (!Depositable(p, i))
            {
                anyFavorite |= it.favorited;
                continue;
            }
            names[i] = new Core.Observe.ItemName(it.Name, ItemLookup.InternalName(it.type));
        }
        var slots = InteractMath.MatchAll(names, query);
        if (slots.Count == 0)
        {
            error = anyFavorite ? $"no '{query}' to deposit that isn't favorited (favorites stay in the inventory)" : $"no '{query}' in inventory";
            return 0;
        }
        int moved = 0;
        foreach (var (slot, take) in InteractMath.PlanTake(slots.Select(s => (s, p.inventory[s].stack)).ToList(), count))
        {
            var src = p.inventory[slot];
            var part = src.Clone();
            part.stack = take;
            ChestUI.TryPlacingInChest(part, false, ItemSlot.Context.InventoryItem);
            int done = take - (part.IsAir ? 0 : part.stack);
            if (done <= 0) break; // container full
            src.stack -= done;
            if (src.stack <= 0) src.TurnToAir();
            moved += done;
        }
        if (moved == 0) error = "chest_full: no room in the container for that item";
        return moved;
    }

    /// <summary>Move up to count (null = all) of the named item from the open container into the inventory, through
    /// Player.GetItem like Loot All. Returns how many moved; leftovers stay in the container.</summary>
    public static int WithdrawItem(Player p, Item[] items, string query, int? count, out string? error)
    {
        error = null;
        var names = items.Select(it => it is { IsAir: false } ? new Core.Observe.ItemName(it.Name, ItemLookup.InternalName(it.type)) : (Core.Observe.ItemName?)null).ToArray();
        var slots = InteractMath.MatchAll(names, query);
        if (slots.Count == 0)
        {
            error = $"no '{query}' in the container";
            return 0;
        }
        var settings = p.chest > -1 ? GetItemSettings.LootAllSettingsRegularChest : GetItemSettings.LootAllSettings;
        int moved = 0;
        foreach (var (slot, take) in InteractMath.PlanTake(slots.Select(s => (s, items[s].stack)).ToList(), count))
        {
            var src = items[slot];
            var part = src.Clone();
            part.stack = take;
            part.position = p.Center;
            var left = p.GetItem(p.whoAmI, part, settings);
            int done = take - (left == null || left.IsAir ? 0 : left.stack);
            if (done > 0)
            {
                src.stack -= done;
                if (src.stack <= 0) items[slot] = new Item();
                if (Main.netMode == NetmodeID.MultiplayerClient && p.chest > -1)
                    NetMessage.SendData(MessageID.SyncChestItem, -1, -1, null, p.chest, slot);
                moved += done;
            }
            if (done < take) break; // inventory full
        }
        if (moved == 0) error = "inventory_full: no room in the inventory for that item";
        return moved;
    }

    /// <summary>Vanilla "Deposit All" button.</summary>
    public static void DepositAll(Player p) => ChestUI.DepositAll(ContainerTransferContext.FromUnknown(p));

    /// <summary>Vanilla "Quick Stack" button (only items the container already holds).</summary>
    public static void QuickStack(Player p) => ChestUI.QuickStack(ContainerTransferContext.FromUnknown(p));

    /// <summary>Vanilla "Loot All" button.</summary>
    public static void LootAll() => ChestUI.LootAll();
}
