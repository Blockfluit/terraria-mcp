using LlmPlayer.Control;
using LlmPlayer.Core.Npc;
using LlmPlayer.Core.Observe;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Tasks;

/// <summary>What buy reports in the task's <c>result</c>.</summary>
public sealed record BuyResult(string Npc, string Item, int Bought, int Requested, string Spent, string? Stopped);

/// <summary>
/// Buy items from a town NPC's shop with the player's own money, the way a player does: walk within talk range
/// (<see cref="MoveToTask"/> following the NPC), open its chat (as a right-click, <see cref="TalkToNpcTask.OpenChat"/>),
/// open the shop (as the chat's "Shop" button, vanilla <c>Main.OpenShop</c>: inventory open, <c>npcShop = 1</c>,
/// <c>Chest.SetupShop(shopName, npc)</c>), buy, then close the shop and chat again (as closing the inventory).
/// <para/>Each unit bought mirrors vanilla's shop-slot click, <c>ItemSlot.HandleShopSlot</c> (tModLoader 1.4.4), step by
/// step: <c>PlayerLoader.CanBuyItem</c>; price from <c>Player.GetItemExpectedPrice</c> (NPC happiness via
/// <c>SetTalkNPC</c>'s shopping settings, Discount Card, buy-once items); <c>Player.BuyItem(price, currency)</c> takes the
/// coins (inventory, piggy bank, safe, … exactly as vanilla) and fails if the player can't afford it; the bought item is
/// the shop item cloned with <c>buyOnce/isAShopItem</c> cleared, special currency reset, stack 1, and
/// <c>OnCreated(BuyItemCreationContext)</c>; the sell-back helper is updated; a buy-once shop stack shrinks;
/// <c>PlayerLoader.PostBuyItem</c> runs. The one difference: vanilla puts the item on the mouse cursor and the player
/// then drops it into the inventory, here it goes straight into the inventory with <c>Player.GetItem</c>, and buying
/// stops first if the inventory has no room for it (<c>Player.ItemSpace</c>), so nothing is ever paid for and lost.
/// <para/>All of an NPC's shops are searched (e.g. the Painter's second shop); the first one selling the item is used.
/// Fails if the NPC leaves, can't be reached, doesn't sell the item now, or the player can't afford even one.
/// Buying fewer than requested (money or space ran out) still succeeds, with the reason in the result.
/// </summary>
public sealed class BuyTask : IGameTask<GameCtx>
{
    public const int MaxWalks = 6, MaxCount = 999;

    private readonly int _npc, _type, _count;
    private readonly string _npcName, _query;
    private MoveToTask? _move;
    private int _walks;

    public string Name => "buy";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result { get; private set; }

    public BuyTask(NPC npc, string query, int count)
    {
        _npc = npc.whoAmI;
        _type = npc.type;
        _npcName = npc.GivenOrTypeName;
        _query = query;
        _count = count;
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        var npc = Main.npc[_npc];
        if (!npc.active || npc.type != _type) return Finish(ctx, GameTaskStatus.Failed, $"{_npcName} is gone");

        if (NpcMath.InTalkRange(p.position.X, p.position.Y, p.width, p.height, Player.tileRangeX, Player.tileRangeY,
                (int)npc.position.X, (int)npc.position.Y, npc.width, npc.height))
        {
            StopMove(ctx);
            ControlState.Pending = default;
            return Shop(ctx, p, npc);
        }

        if (_move == null)
        {
            if (_walks >= MaxWalks)
                return Finish(ctx, GameTaskStatus.Failed, $"could not get within talk range of {_npcName} (it keeps moving)");
            _walks++;
            _move = new MoveToTask(_npc, tolerance: 2, name: Name);
        }
        var s = _move.Tick(ctx);
        Progress = $"walking to {_npcName}" + (_move.Progress is { } mp ? $": {mp}" : "");
        if (s == GameTaskStatus.Running) return GameTaskStatus.Running;
        var why = _move.Reason;
        _move = null;
        ControlState.Pending = default;
        if (s != GameTaskStatus.Succeeded) return Finish(ctx, GameTaskStatus.Failed, why ?? $"could not reach {_npcName}");
        return GameTaskStatus.Running; // arrived: the range check runs next tick
    }

    public void OnCancel(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
    }

    private GameTaskStatus Shop(GameCtx ctx, Player p, NPC npc)
    {
        TalkToNpcTask.OpenChat(p, npc);
        try
        {
            var shopChest = Main.instance.shop[1];
            int slot = OpenShopSelling(npc, shopChest, out var forSale);
            if (slot < 0)
            {
                string list = forSale.Count == 0 ? "nothing right now" : string.Join(", ", forSale.Take(25)) + (forSale.Count > 25 ? ", …" : "");
                return Finish(ctx, GameTaskStatus.Failed, $"{npc.FullName} doesn't sell '{_query.Trim()}' right now; for sale: {list}");
            }
            var inv = shopChest.item;
            string itemName = inv[slot].Name;
            int bought = BuyUnits(p, npc, inv, slot, _count, out long spent, out string? stopped);
            Result = new BuyResult(npc.FullName, itemName, bought, _count, CoinMath.Format(spent), stopped);
            Progress = $"bought {bought}/{_count}";
            if (bought == 0) return Finish(ctx, GameTaskStatus.Failed, $"bought no {itemName}: {stopped}");
            Reason = bought < _count ? $"bought {bought} of {_count} {itemName}: {stopped}" : $"bought {bought} {itemName} for {CoinMath.Format(spent)}";
            return GameTaskStatus.Succeeded;
        }
        finally
        {
            CloseShop(p);
        }
    }

    /// <summary>Open the first of the NPC's shops that sells the item (as Main.OpenShop) and return the item's shop
    /// slot; -1 if none does (<paramref name="forSale"/> then lists everything on offer).</summary>
    private int OpenShopSelling(NPC npc, Chest shopChest, out List<string> forSale)
    {
        forSale = new List<string>();
        foreach (var shop in NPCShopDatabase.AllShops)
        {
            if (shop.NpcType != npc.type) continue;
            Main.playerInventory = true;
            Main.stackSplit = 9999;
            Main.npcChatText = "";
            Main.SetNPCShopIndex(1);
            shopChest.SetupShop(shop.FullName, npc);
            SoundEngine.PlaySound(SoundID.MenuTick); // OpenShop's sound 12

            var names = new ItemName?[shopChest.item.Length];
            for (int i = 0; i < shopChest.item.Length; i++)
            {
                var it = shopChest.item[i];
                if (it == null || it.IsAir) continue;
                names[i] = new ItemName(it.Name, ItemLookup.InternalName(it.type));
                forSale.Add(it.Name);
            }
            int found = ItemMatcher.Find(names, _query);
            if (found >= 0) return found;
        }
        return -1;
    }

    /// <summary>Buy up to <paramref name="count"/> units of <c>inv[slot]</c>, each exactly like one vanilla shop click
    /// (ItemSlot.HandleShopSlot), the result going into the inventory. Returns how many were bought.</summary>
    private static int BuyUnits(Player p, NPC npc, Item[] inv, int slot, int count, out long spent, out string? stopped)
    {
        spent = 0;
        stopped = null;
        int bought = 0;
        while (bought < count)
        {
            var shopItem = inv[slot];
            if (shopItem.IsAir || shopItem.stack <= 0) { stopped = "sold out"; break; }
            if (!PlayerLoader.CanBuyItem(p, npc, inv, shopItem)) { stopped = "a mod refused the purchase"; break; }

            var item = shopItem.Clone();
            item.buyOnce = false;
            item.isAShopItem = false;
            if (item.shopSpecialCurrency != -1)
            {
                item.shopSpecialCurrency = -1;
                item.shopCustomPrice = null;
            }
            item.stack = 1;
            if (!p.ItemSpace(item).CanTakeItem) { stopped = "inventory full"; break; }

            p.GetItemExpectedPrice(shopItem, out _, out long price);
            bool coins = shopItem.shopSpecialCurrency == -1;
            if (!p.BuyItem(price, shopItem.shopSpecialCurrency))
            {
                stopped = $"can't afford another (costs {Price(shopItem, price)})";
                break;
            }
            if (bought == 0) SoundEngine.PlaySound(shopItem.value > 0 ? SoundID.Coins : SoundID.Grab);
            item.OnCreated(new BuyItemCreationContext(new Item(), npc));
            if (!shopItem.buyOnce) Main.shopSellbackHelper.Add(shopItem);
            if (shopItem.buyOnce && --shopItem.stack <= 0) shopItem.SetDefaults();
            PlayerLoader.PostBuyItem(p, npc, inv, item);

            var left = p.GetItem(p.whoAmI, item, GetItemSettings.InventoryUIToInventorySettings);
            if (left != null && !left.IsAir) p.QuickSpawnItem(p.GetSource_Misc("LlmPlayer:buy"), left, left.stack);
            if (coins) spent += price;
            bought++;
        }
        return bought;
    }

    private static string Price(Item it, long price) =>
        it.shopSpecialCurrency == -1 ? CoinMath.Format(price) : $"{price} (special currency)";

    /// <summary>As closing the inventory while a shop is open: shop, chat and inventory close.</summary>
    private static void CloseShop(Player p)
    {
        Main.SetNPCShopIndex(0);
        Main.playerInventory = false;
        Main.npcChatText = "";
        Main.npcChatCornerItem = 0;
        p.SetTalkNPC(-1);
    }

    private void StopMove(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        OnCancel(ctx);
        return status;
    }
}
