using System.Text.Json.Serialization;
using LlmPlayer.Control;
using LlmPlayer.Core.Npc;
using LlmPlayer.Core.Tasks;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Tasks;

public sealed record ShopEntry(string Name, string Price);

/// <summary>What talk_to_npc reports in the task's <c>result</c>. <c>Shop</c> is omitted for NPCs without a shop.</summary>
public sealed record TalkResult(string Name, string Chat,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ShopEntry>? Shop);

/// <summary>
/// Walk within talk range of a town NPC (<see cref="NpcApproach"/>: to the nearest reachable stand from which it is in
/// range, through walls as vanilla allows, so an NPC shut in its house is talked to from outside) and open its chat the way a
/// right-click does (vanilla Main.HoverOverNPCs): <c>Player.SetTalkNPC</c>, <c>Main.npcChatText = npc.GetChat()</c>,
/// chat sound. The chat window stays open in game. Range: <see cref="NpcMath.InTalkRange"/> with
/// <c>Player.tileRangeX/Y</c>, checked every tick, so it stops as soon as the NPC is reachable.
/// <para/>The result (<see cref="TalkResult"/>) is exposed as <see cref="Result"/>, which the runner puts on the task
/// report: get_task / wait_for_task <c>report.result</c> and the <c>task_finished</c> event. Shop items are what the
/// shop would show right now (all of the NPC's registered shops, via NPCShopDatabase like Chest.SetupShop), priced with
/// <c>Player.GetItemExpectedPrice</c> (happiness and Discount Card included). Nothing is bought or sold.
/// <para/>Fails if the NPC leaves (despawns/dies) or goes out of sight, no stand within range is known or reachable
/// (with a hint), or it is still out of range after
/// <see cref="MaxWalks"/> walks (it kept wandering off).
/// </summary>
public sealed class TalkToNpcTask : IGameTask<GameCtx>
{
    public const int MaxWalks = NpcApproach.MaxWalks;

    private readonly int _npc, _type;
    private readonly string _npcName;
    private readonly NpcApproach _approach;

    public string Name => "talk_to_npc";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result { get; private set; }

    public TalkToNpcTask(NPC npc)
    {
        _npc = npc.whoAmI;
        _type = npc.type;
        _npcName = npc.GivenOrTypeName;
        _approach = new NpcApproach(npc, Name);
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        var npc = Main.npc[_npc];
        if (!npc.active || npc.type != _type) return Finish(ctx, GameTaskStatus.Failed, $"{_npcName} is gone");

        if (NpcApproach.InRange(p, npc))
        {
            StopMove(ctx);
            ControlState.Pending = default;
            Result = OpenChat(p, npc);
            Progress = "talking";
            return GameTaskStatus.Succeeded;
        }

        var s = _approach.Tick(ctx, out var why);
        Progress = _approach.Progress;
        if (s == GameTaskStatus.Failed) return Finish(ctx, GameTaskStatus.Failed, why ?? $"could not reach {_npcName}");
        return GameTaskStatus.Running; // a walk ended at a stand: the range check runs next tick (walks again if it wandered off)
    }

    public void OnCancel(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
    }

    /// <summary>Same steps as vanilla's right-click on an NPC (Main.HoverOverNPCs), then the shop listing.</summary>
    internal static TalkResult OpenChat(Player p, NPC npc)
    {
        Main.CancelHairWindow();
        Main.SetNPCShopIndex(0);
        Main.InGuideCraftMenu = false;
        p.dropItemCheck();
        Main.npcChatCornerItem = 0;
        p.sign = -1;
        Main.editSign = false;
        p.SetTalkNPC(npc.whoAmI);
        Main.playerInventory = false;
        p.chest = -1;
        Recipe.FindRecipes();
        Main.npcChatText = npc.GetChat() ?? "";
        SoundEngine.PlaySound(SoundID.Chat);
        // GetChat can transform a bound NPC into its town form (same slot): read the name afterwards.
        return new TalkResult(npc.FullName, Main.npcChatText, ShopOf(p, npc));
    }

    private static List<ShopEntry>? ShopOf(Player p, NPC npc)
    {
        List<ShopEntry>? list = null;
        foreach (var shop in NPCShopDatabase.AllShops)
        {
            if (shop.NpcType != npc.type) continue;
            list ??= new List<ShopEntry>();
            // As Chest.SetupShop(shopName, npc): fill, let mods adjust, mark as shop items.
            var items = new Item[Chest.maxItems];
            shop.FillShop(items, npc, out _);
            NPCLoader.ModifyActiveShop(npc, shop.FullName, items);
            foreach (var it in items)
            {
                if (it == null || it.IsAir) continue;
                it.isAShopItem = true;
                p.GetItemExpectedPrice(it, out _, out long buy);
                list.Add(new ShopEntry(it.stack > 1 ? $"{it.Name} x{it.stack}" : it.Name, Price(it, buy)));
            }
        }
        return list;
    }

    private static string Price(Item it, long buy)
    {
        if (it.shopSpecialCurrency == -1) return CoinMath.Format(buy);
        if (it.shopSpecialCurrency == CustomCurrencyID.DefenderMedals) return buy == 1 ? "1 Defender Medal" : $"{buy} Defender Medals";
        return $"{buy} (custom currency)";
    }

    private void StopMove(GameCtx ctx) => _approach.Cancel(ctx);

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string reason)
    {
        Reason = reason;
        OnCancel(ctx);
        return status;
    }
}
